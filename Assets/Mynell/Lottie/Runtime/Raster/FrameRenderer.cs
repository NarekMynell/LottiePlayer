using Mynell.Lottie.Eval;
using Mynell.Lottie.Geometry;
using Mynell.Lottie.Scene;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Raster
{
    /// <summary>Per-instance scratch memory of the rasterizer (reused across frames).</summary>
    public struct RasterScratch : System.IDisposable
    {
        public NativeList<float2> GeoPts;
        public NativeList<Poly> GeoPolys;
        public NativeList<int2> ItemGeo;
        public NativeList<float4> ItemBounds;
        public NativeList<int4> GroupRect;
        public NativeList<float4> GroupContent;
        public NativeList<float4> GroupMaskBounds;
        public NativeList<float4> GroupMatteBounds;
        public NativeList<float2> GroupReach;
        public NativeList<float2> LocalPts;
        public NativeList<Poly> LocalPolys;
        public NativeList<float2> DashPts;
        public NativeList<Poly> DashPolys;
        public NativeList<float2> StrokeTmp;
        public NativeList<uint> Arena;
        public NativeList<byte> MaskArena;
        public NativeList<float> Acc;
        public NativeList<int> RowMin;
        public NativeList<int> RowMax;
        public NativeList<ulong> Bits;
        public NativeList<CoverageSpan> Spans;
        public NativeList<FrameRenderer.Surface> Surfaces;
        public NativeList<FrameRenderer.StackEntry> Stack;

        public static RasterScratch Create()
        {
            return new RasterScratch
            {
                GeoPts = new NativeList<float2>(4096, Allocator.Persistent),
                GeoPolys = new NativeList<Poly>(256, Allocator.Persistent),
                ItemGeo = new NativeList<int2>(64, Allocator.Persistent),
                ItemBounds = new NativeList<float4>(64, Allocator.Persistent),
                GroupRect = new NativeList<int4>(16, Allocator.Persistent),
                GroupContent = new NativeList<float4>(16, Allocator.Persistent),
                GroupMaskBounds = new NativeList<float4>(16, Allocator.Persistent),
                GroupMatteBounds = new NativeList<float4>(16, Allocator.Persistent),
                GroupReach = new NativeList<float2>(16, Allocator.Persistent),
                LocalPts = new NativeList<float2>(1024, Allocator.Persistent),
                LocalPolys = new NativeList<Poly>(16, Allocator.Persistent),
                DashPts = new NativeList<float2>(1024, Allocator.Persistent),
                DashPolys = new NativeList<Poly>(64, Allocator.Persistent),
                StrokeTmp = new NativeList<float2>(1024, Allocator.Persistent),
                Arena = new NativeList<uint>(0, Allocator.Persistent),
                MaskArena = new NativeList<byte>(0, Allocator.Persistent),
                Acc = new NativeList<float>(0, Allocator.Persistent),
                RowMin = new NativeList<int>(0, Allocator.Persistent),
                RowMax = new NativeList<int>(0, Allocator.Persistent),
                Bits = new NativeList<ulong>(0, Allocator.Persistent),
                Spans = new NativeList<CoverageSpan>(0, Allocator.Persistent),
                Surfaces = new NativeList<FrameRenderer.Surface>(16, Allocator.Persistent),
                Stack = new NativeList<FrameRenderer.StackEntry>(16, Allocator.Persistent),
            };
        }

        public void Dispose()
        {
            if (!GeoPts.IsCreated) return;
            GeoPts.Dispose(); GeoPolys.Dispose(); ItemGeo.Dispose(); ItemBounds.Dispose();
            GroupRect.Dispose(); GroupContent.Dispose(); GroupMaskBounds.Dispose(); GroupMatteBounds.Dispose(); GroupReach.Dispose();
            LocalPts.Dispose(); LocalPolys.Dispose(); DashPts.Dispose(); DashPolys.Dispose(); StrokeTmp.Dispose();
            Arena.Dispose(); MaskArena.Dispose(); Acc.Dispose(); RowMin.Dispose(); RowMax.Dispose(); Bits.Dispose(); Spans.Dispose();
            Surfaces.Dispose(); Stack.Dispose();
        }
    }

    /// <summary>
    /// Executes a <see cref="FrameData"/> command stream into a premultiplied RGBA8 image:
    /// geometry (flatten / dash / stroke), exact-area coverage, paints, isolated groups with masks,
    /// track mattes, opacity and blend modes.
    /// </summary>
    public unsafe struct FrameRenderer
    {
        public struct Surface
        {
            public int Offset;     // arena offset (pixels or mask bytes); -1 = root target
            public int X0, Y0, W, H;
            public byte IsMask;
            /// <summary>Pixels not cleared yet: the first item drawn clears them (or <see cref="EnsureCleared"/>).</summary>
            public byte Uncleared;
            public int4 Rect => new int4(X0, Y0, X0 + W, Y0 + H);
        }

        public struct StackEntry
        {
            public int Group;
            public byte Collapsed;
            public byte Section;   // 0 content, 1 mask, 2 matte, 3 aux
            public int Layer;      // surface receiving content
            public int Matte;      // matte surface or -1
            public int Mask;       // mask surface or -1
            public int Aux;        // aux surface (layer read by an effect) or -1
            public int ArenaMark;
            public int MaskMark;
        }

        public FrameData F;
        /// <summary>Per-band scratch (surfaces, arenas, coverage buffers).</summary>
        public RasterScratch R;
        /// <summary>Geometry shared by all bands (built once by <see cref="Prepare"/>). Same as R when not banded.</summary>
        public RasterScratch G;
        /// <summary>Rows rendered by <see cref="ExecuteBand"/>.</summary>
        public int BandY0, BandY1;
        [NativeDisableUnsafePtrRestriction] public uint* RootPx;
        public int RootW, RootH;
        /// <summary>Flattening tolerance in device pixels.</summary>
        public float Tolerance;
        /// <summary>Optional debug counters: [0] item span pixels, [1] group composite pixels, [2] cleared pixels, [3] edges, [4] items drawn,
        /// [5] gradient / image pixels, [6] opaque solid pixels. Single-threaded use only.</summary>
        [NativeDisableUnsafePtrRestriction] public long* Stats;
        /// <summary>Decoded images (premultiplied, y-down rows) and their (offset, width, height).</summary>
        [NativeDisableUnsafePtrRestriction] public uint* ImagePixels;
        [NativeDisableUnsafePtrRestriction] public int4* ImageInfo;
        public int ImageCount;

        const float MinCoverage = 0.5f / 255f;

        public void GeometryOnly() => Prepare();

        /// <summary>Flattening / stroking and group bounds (shared by all bands).</summary>
        public void Prepare()
        {
            if (!G.GeoPts.IsCreated) G = R;
            BuildGeometry();
            ComputeGroupBounds();
        }

        /// <summary>Renders the whole target (single band).</summary>
        public void Render()
        {
            G = R;
            Prepare();
            ExecuteBand(0, RootH);
        }

        /// <summary>Rasterizes rows [y0, y1) of the target (geometry from <see cref="Prepare"/> in <see cref="G"/>).</summary>
        public void ExecuteBand(int y0, int y1)
        {
            if (!G.GeoPts.IsCreated) G = R;
            BandY0 = math.clamp(y0, 0, RootH);
            BandY1 = math.clamp(y1, BandY0, RootH);
            if (BandY1 <= BandY0) return;
            Execute();
        }

        // ================================================================== geometry

        void BuildGeometry()
        {
            int n = F.Items.Length;
            G.GeoPts.Clear();
            G.GeoPolys.Clear();
            G.ItemGeo.ResizeUninitialized(n);
            G.ItemBounds.ResizeUninitialized(n);
            float2* pts = (float2*)F.Points.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < n; i++)
            {
                DrawItem item = F.Items[i];
                int polyFirst = G.GeoPolys.Length;
                int ptFirst = G.GeoPts.Length;
                if (item.Kind == ItemKind.Fill) FillGeometry(item, pts);
                else StrokeGeometry(item, pts);

                float4 b = SceneBuilder.EmptyRect;
                float2* gp = (float2*)G.GeoPts.GetUnsafePtr();
                int ptEnd = G.GeoPts.Length;
                if (ptEnd > ptFirst)
                {
                    float2 mn = gp[ptFirst], mx = gp[ptFirst];
                    for (int k = ptFirst + 1; k < ptEnd; k++)
                    {
                        mn = math.min(mn, gp[k]);
                        mx = math.max(mx, gp[k]);
                    }
                    b = SceneBuilder.Intersect(new float4(mn, mx), item.Clip);
                }
                G.ItemBounds[i] = b;
                G.ItemGeo[i] = new int2(polyFirst, G.GeoPolys.Length - polyFirst);
            }
        }

        void FillGeometry(in DrawItem item, float2* pts)
        {
            ref var outPts = ref *G.GeoPts.GetUnsafeList();
            ref var outPolys = ref *G.GeoPolys.GetUnsafeList();
            for (int c = 0; c < item.ContourCount; c++)
            {
                Contour ct = F.Contours[item.ContourFirst + c];
                if (ct.Count < 2) continue;
                Flattener.Flatten(pts, ct.First, ct.Count, ct.Closed != 0, item.ToDevice, Tolerance, ref outPts, ref outPolys);
            }
        }

        void StrokeGeometry(in DrawItem item, float2* pts)
        {
            float scale = item.ToDevice.MaxScale;
            if (!(scale > 1e-6f) || !(item.StrokeWidth > 0)) return;
            float tol = Tolerance / scale;
            var style = new Stroker.Style
            {
                HalfWidth = item.StrokeWidth * 0.5f,
                Cap = item.Cap == 0 ? Stroker.CapButt : item.Cap,
                Join = item.Join == 0 ? Stroker.JoinMiter : item.Join,
                MiterLimit = item.MiterLimit > 0 ? item.MiterLimit : 4f,
                Tolerance = tol,
            };
            ref var outPts = ref *G.GeoPts.GetUnsafeList();
            ref var outPolys = ref *G.GeoPolys.GetUnsafeList();
            ref var localPts = ref *G.LocalPts.GetUnsafeList();
            ref var localPolys = ref *G.LocalPolys.GetUnsafeList();
            ref var dashPts = ref *G.DashPts.GetUnsafeList();
            ref var dashPolys = ref *G.DashPolys.GetUnsafeList();
            ref var tmp = ref *G.StrokeTmp.GetUnsafeList();
            int ptFirst = outPts.Length;
            float* dashes = item.DashCount > 0 ? (float*)F.Dashes.GetUnsafeReadOnlyPtr() + item.DashFirst : null;
            bool trimmed = (item.Trim.x != 0 || item.Trim.y != 0) && !(item.Trim.x <= 0 && item.Trim.y >= 1);

            for (int c = 0; c < item.ContourCount; c++)
            {
                Contour ct = F.Contours[item.ContourFirst + c];
                bool closed = ct.Closed != 0;
                if (ct.Count < 1 || (ct.Count == 1 && !closed)) continue;
                localPts.Clear();
                localPolys.Clear();
                Flattener.Flatten(pts, ct.First, ct.Count, closed, Affine2.Identity, tol, ref localPts, ref localPolys);
                if (trimmed)
                {
                    // Trim each contour to [start, end] of its length (Stroke effect).
                    dashPts.Clear();
                    dashPolys.Clear();
                    for (int p = 0; p < localPolys.Length; p++)
                    {
                        Poly poly = localPolys[p];
                        TrimPolyline(localPts.Ptr + poly.First, poly.Count, closed, item.Trim.x, item.Trim.y, ref dashPts, ref dashPolys);
                    }
                    localPts.Clear();
                    localPolys.Clear();
                    for (int k = 0; k < dashPts.Length; k++) localPts.Add(dashPts[k]);
                    for (int k = 0; k < dashPolys.Length; k++) localPolys.Add(dashPolys[k]);
                }
                bool polyClosed = closed && !trimmed;
                for (int p = 0; p < localPolys.Length; p++)
                {
                    Poly poly = localPolys[p];
                    if (dashes != null)
                    {
                        dashPts.Clear();
                        dashPolys.Clear();
                        Dasher.Dash(localPts.Ptr + poly.First, poly.Count, polyClosed, dashes, item.DashCount, item.DashOffset,
                            ref dashPts, ref dashPolys);
                        for (int d = 0; d < dashPolys.Length; d++)
                        {
                            Poly dp = dashPolys[d];
                            Stroker.Stroke(dashPts.Ptr + dp.First, dp.Count, false, style, ref outPts, ref outPolys, ref tmp);
                        }
                    }
                    else
                    {
                        Stroker.Stroke(localPts.Ptr + poly.First, poly.Count, polyClosed, style, ref outPts, ref outPolys, ref tmp);
                    }
                }
            }
            Affine2 m = item.ToDevice;
            for (int k = ptFirst; k < outPts.Length; k++) outPts.Ptr[k] = m.Apply(outPts.Ptr[k]);
        }

        /// <summary>Appends the part [s, e] (fractions of the length) of a polyline as an open polyline.</summary>
        static void TrimPolyline(float2* pts, int count, bool closed, float s, float e, ref UnsafeList<float2> outPts, ref UnsafeList<Poly> outPolys)
        {
            if (count < 2 || !(e > s)) return;
            int segs = closed ? count : count - 1;
            float total = 0;
            for (int i = 0; i < segs; i++) total += math.distance(pts[i], pts[(i + 1) % count]);
            if (!(total > 0)) return;
            float from = s * total, to = e * total, acc = 0;
            int first = outPts.Length;
            for (int i = 0; i < segs; i++)
            {
                float2 a = pts[i], b = pts[(i + 1) % count];
                float len = math.distance(a, b), next = acc + len;
                if (next >= from && acc <= to && len > 0)
                {
                    float t0 = math.saturate((from - acc) / len), t1 = math.saturate((to - acc) / len);
                    if (outPts.Length == first) outPts.Add(math.lerp(a, b, t0));
                    outPts.Add(math.lerp(a, b, t1));
                }
                acc = next;
                if (acc > to) break;
            }
            if (outPts.Length - first >= 2) outPolys.Add(new Poly { First = first, Count = outPts.Length - first });
            else outPts.Resize(first);
        }

        // ================================================================== bounds

        static int4 RoundOut(float4 r)
        {
            if (!(r.x < r.z) || !(r.y < r.w)) return new int4(0, 0, 0, 0);
            return new int4((int)math.floor(r.x), (int)math.floor(r.y), (int)math.ceil(r.z), (int)math.ceil(r.w));
        }

        static int4 IntersectI(int4 a, int4 b) => new int4(math.max(a.xy, b.xy), math.min(a.zw, b.zw));
        static bool EmptyI(int4 r) => r.x >= r.z || r.y >= r.w;

        void ComputeGroupBounds()
        {
            int gc = F.Groups.Length;
            G.GroupRect.ResizeUninitialized(gc);
            G.GroupContent.ResizeUninitialized(gc);
            G.GroupReach.ResizeUninitialized(gc);
            G.GroupMaskBounds.ResizeUninitialized(gc);
            G.GroupMatteBounds.ResizeUninitialized(gc);
            for (int g = 0; g < gc; g++)
            {
                G.GroupReach[g] = float2.zero;
                G.GroupContent[g] = SceneBuilder.EmptyRect;
                G.GroupMaskBounds[g] = SceneBuilder.EmptyRect;
                G.GroupMatteBounds[g] = SceneBuilder.EmptyRect;
            }
            // stack of (group, section)
            var stack = new NativeList<int2>(16, Allocator.Temp);
            for (int c = 0; c < F.Cmds.Length; c++)
            {
                RenderCmd cmd = F.Cmds[c];
                switch (cmd.Kind)
                {
                    case CmdKind.BeginGroup:
                        stack.Add(new int2(cmd.Index, 0));
                        break;
                    case CmdKind.EndGroup:
                    {
                        int g = cmd.Index;
                        stack.RemoveAt(stack.Length - 1);
                        GroupData gd = F.Groups[g];
                        float4 content = G.GroupContent[g];
                        if (gd.EffectCount > 0)
                        {
                            float2 reach = float2.zero;
                            for (int k = 0; k < gd.EffectCount; k++)
                            {
                                var op = F.Effects[gd.EffectFirst + k];
                                if (!SceneBuilder.IsEmpty(op.OutputRect))
                                {
                                    // The effect draws its own region (motion tile output, sphere, corner pin quad...);
                                    // the source content stays in the surface.
                                    content = SceneBuilder.Union(content, op.OutputRect);
                                    continue;
                                }
                                float2 r = Effects.Reach(op);
                                // Levels raising the alpha of transparent pixels paints the whole effect region
                                // (SVG default filter region: the bounding box grown by 10% on each side).
                                if (op.Kind == EffectOpKind.Levels && op.TableA >= 0 && F.PaintFloats[op.TableA] > 0.5f / 255f && !SceneBuilder.IsEmpty(content))
                                    r += (content.zw - content.xy) * 0.1f;
                                if (!SceneBuilder.IsEmpty(content)) content += new float4(-r, r);
                                reach += r;
                            }
                            G.GroupReach[g] += reach;
                        }
                        float4 b = SceneBuilder.Intersect(content, gd.Clip);
                        // Visible area is limited by an additive mask (starting empty) ...
                        if (gd.HasMask != 0 && gd.MaskInitOne == 0) b = SceneBuilder.Intersect(b, G.GroupMaskBounds[g]);
                        // ... and by a non-inverted track matte.
                        if (gd.MatteMode != 0 && (gd.MatteMode & 1) == 1) b = SceneBuilder.Intersect(b, G.GroupMatteBounds[g]);
                        if (SceneBuilder.IsEmpty(b)) b = SceneBuilder.EmptyRect;
                        G.GroupRect[g] = RoundOut(b);
                        // A projected group covers its warped raster rectangle in the parent.
                        if (stack.Length > 0 && !SceneBuilder.IsEmpty(b))
                            AddBounds(stack[stack.Length - 1], gd.Projected != 0 ? ProjectRect(gd.Projection, b) : b);
                        break;
                    }
                    case CmdKind.BeginMask:
                    case CmdKind.BeginMatte:
                    case CmdKind.BeginAux:
                    case CmdKind.EndMask:
                    case CmdKind.EndMatte:
                    case CmdKind.EndAux:
                        if (stack.Length > 0)
                        {
                            int2 top = stack[stack.Length - 1];
                            top.y = cmd.Kind == CmdKind.BeginMask ? 1 : cmd.Kind == CmdKind.BeginMatte ? 2 : cmd.Kind == CmdKind.BeginAux ? 3 : 0;
                            stack[stack.Length - 1] = top;
                        }
                        break;
                    case CmdKind.Draw:
                        if (stack.Length > 0)
                        {
                            float4 ib = G.ItemBounds[cmd.Index];
                            if (SceneBuilder.IsEmpty(ib)) break;
                            int2 top = stack[stack.Length - 1];
                            if (top.y == 1)
                            {
                                // Only ops that can raise the mask above zero extend its bounds.
                                DrawItem mi = F.Items[cmd.Index];
                                if (mi.Mask == MaskOp.Add || mi.Mask == MaskOp.Lighten || mi.Mask == MaskOp.Difference)
                                    G.GroupMaskBounds[top.x] = SceneBuilder.Union(G.GroupMaskBounds[top.x], ib);
                                // An eroded mask depends on coverage around each pixel.
                                G.GroupReach[top.x] = math.max(G.GroupReach[top.x], mi.Erode);
                            }
                            else AddBounds(top, ib);
                        }
                        break;
                }
            }
            stack.Dispose();
        }

        /// <summary>Bounds of a rectangle mapped by a homography (unbounded when it crosses the camera plane).</summary>
        static float4 ProjectRect(in float3x3 h, float4 r)
        {
            float4 b = SceneBuilder.EmptyRect;
            for (int i = 0; i < 4; i++)
            {
                float2 q = new float2((i & 1) == 0 ? r.x : r.z, (i & 2) == 0 ? r.y : r.w);
                float3 p = math.mul(h, new float3(q, 1));
                if (!(p.z > 1e-6f)) return SceneBuilder.HugeRect;
                float2 d = p.xy / p.z;
                b = SceneBuilder.Union(b, new float4(d, d));
            }
            return b;
        }

        void AddBounds(int2 top, float4 b)
        {
            if (top.y == 0) G.GroupContent[top.x] = SceneBuilder.Union(G.GroupContent[top.x], b);
            else if (top.y == 2) G.GroupMatteBounds[top.x] = SceneBuilder.Union(G.GroupMatteBounds[top.x], b);
        }

        // ================================================================== execution

        int AddSurface(int4 rect, bool mask, bool clearToOne)
        {
            int w = rect.z - rect.x, h = rect.w - rect.y;
            var s = new Surface { X0 = rect.x, Y0 = rect.y, W = w, H = h, IsMask = (byte)(mask ? 1 : 0) };
            if (mask)
            {
                s.Offset = R.MaskArena.Length;
                R.MaskArena.ResizeUninitialized(s.Offset + w * h);
                UnsafeUtility.MemSet((byte*)R.MaskArena.GetUnsafePtr() + s.Offset, clearToOne ? (byte)255 : (byte)0, w * h);
            }
            else
            {
                s.Offset = R.Arena.Length;
                R.Arena.ResizeUninitialized(s.Offset + w * h);
                s.Uncleared = 1;
            }
            R.Surfaces.Add(s);
            return R.Surfaces.Length - 1;
        }

        uint* Px(in Surface s) => s.Offset < 0 ? RootPx + (long)BandY0 * RootW : (uint*)R.Arena.GetUnsafePtr() + s.Offset;
        byte* MaskPx(in Surface s) => (byte*)R.MaskArena.GetUnsafePtr() + s.Offset;

        int CurrentTarget()
        {
            var top = R.Stack[R.Stack.Length - 1];
            if (top.Section == 3) return top.Aux;
            if (top.Section == 2) return top.Matte;
            if (top.Section == 1) return top.Mask;
            return top.Layer;
        }

        void Execute()
        {
            R.Arena.Clear();
            R.MaskArena.Clear();
            R.Surfaces.Clear();
            R.Stack.Clear();
            R.Surfaces.Add(new Surface { Offset = -1, X0 = 0, Y0 = BandY0, W = RootW, H = BandY1 - BandY0, Uncleared = 1 });
            R.Stack.Add(new StackEntry { Group = -1, Layer = 0, Matte = -1, Mask = -1, Aux = -1 });
            _mergeOffset = -1;

            int cmdCount = F.Cmds.Length;
            for (int c = 0; c < cmdCount; c++)
            {
                RenderCmd cmd = F.Cmds[c];
                switch (cmd.Kind)
                {
                    case CmdKind.Draw:
                    {
                        int target = CurrentTarget();
                        if (target < 0) break;
                        DrawItem item = F.Items[cmd.Index];
                        Surface s = R.Surfaces[target];
                        if (s.IsMask != 0) DrawMaskItem(cmd.Index, item, s);
                        else if (DrawItemInto(cmd.Index, item, s, s.Uncleared != 0) && s.Uncleared != 0)
                        {
                            s.Uncleared = 0;
                            R.Surfaces[target] = s;
                        }
                        break;
                    }
                    case CmdKind.BeginGroup:
                    {
                        int g = cmd.Index;
                        GroupData gd = F.Groups[g];
                        int parentTarget = CurrentTarget();
                        if (gd.Collapsed != 0)
                        {
                            R.Stack.Add(new StackEntry { Group = g, Collapsed = 1, Layer = parentTarget, Matte = -1, Mask = -1, Aux = -1 });
                            break;
                        }
                        int4 parentRect = parentTarget >= 0 ? R.Surfaces[parentTarget].Rect : new int4();
                        if (parentTarget >= 0 && math.any(G.GroupReach[g] > 0))
                        {
                            // Effects / eroded masks read pixels around the band: render the group with its halo.
                            int2 halo = (int2)math.ceil(G.GroupReach[g]);
                            parentRect = IntersectI(new int4(parentRect.xy - halo, parentRect.zw + halo), new int4(0, 0, RootW, RootH));
                        }
                        int4 rect = parentTarget >= 0
                            ? (gd.NonLocal != 0 || gd.Projected != 0 ? G.GroupRect[g] : IntersectI(G.GroupRect[g], parentRect))
                            : new int4();
                        if (parentTarget < 0 || EmptyI(rect))
                        {
                            c = SkipGroup(c, g);
                            break;
                        }
                        var e = new StackEntry
                        {
                            Group = g,
                            ArenaMark = R.Arena.Length,
                            MaskMark = R.MaskArena.Length,
                            Matte = -1,
                            Mask = -1,
                            Aux = -1,
                        };
                        e.Layer = AddSurface(rect, false, false);
                        R.Stack.Add(e);
                        break;
                    }
                    case CmdKind.BeginMatte:
                    {
                        var e = R.Stack[R.Stack.Length - 1];
                        if (e.Matte < 0) e.Matte = AddSurface(R.Surfaces[e.Layer].Rect, false, false);
                        e.Section = 2;
                        R.Stack[R.Stack.Length - 1] = e;
                        break;
                    }
                    case CmdKind.BeginMask:
                    {
                        var e = R.Stack[R.Stack.Length - 1];
                        if (e.Mask < 0) e.Mask = AddSurface(R.Surfaces[e.Layer].Rect, true, F.Groups[e.Group].MaskInitOne != 0);
                        e.Section = 1;
                        R.Stack[R.Stack.Length - 1] = e;
                        break;
                    }
                    case CmdKind.BeginAux:
                    {
                        var e = R.Stack[R.Stack.Length - 1];
                        if (e.Aux < 0)
                        {
                            float4 auxRect = F.Groups[e.Group].AuxRect;
                            int4 rect = SceneBuilder.IsEmpty(auxRect) ? R.Surfaces[e.Layer].Rect : RoundOut(auxRect);
                            e.Aux = AddSurface(EmptyI(rect) ? R.Surfaces[e.Layer].Rect : rect, false, false);
                        }
                        e.Section = 3;
                        R.Stack[R.Stack.Length - 1] = e;
                        break;
                    }
                    case CmdKind.EndAux:
                    case CmdKind.EndMatte:
                    case CmdKind.EndMask:
                    {
                        var e = R.Stack[R.Stack.Length - 1];
                        e.Section = 0;
                        R.Stack[R.Stack.Length - 1] = e;
                        break;
                    }
                    case CmdKind.EndGroup:
                    {
                        var e = R.Stack[R.Stack.Length - 1];
                        R.Stack.RemoveAt(R.Stack.Length - 1);
                        if (e.Collapsed != 0) break;
                        int dst = CurrentTarget();
                        if (dst >= 0) CompositeGroup(e, dst);
                        R.Arena.ResizeUninitialized(e.ArenaMark);
                        R.MaskArena.ResizeUninitialized(e.MaskMark);
                        // Surfaces allocated by this group are dropped (LIFO).
                        R.Surfaces.ResizeUninitialized(e.Layer);
                        break;
                    }
                }
            }
            EnsureCleared(0);
        }

        int SkipGroup(int c, int g)
        {
            for (int k = c + 1; k < F.Cmds.Length; k++)
            {
                RenderCmd cmd = F.Cmds[k];
                if (cmd.Kind == CmdKind.EndGroup && cmd.Index == g) return k;
            }
            return F.Cmds.Length;
        }

        // ================================================================== group compositing

        /// <summary>Applies the group's effects to its surface. Returns true when the layer mask was baked into it.</summary>
        bool ApplyEffects(in GroupData gd, in StackEntry e)
        {
            bool maskBaked = false;
            for (int k = 0; k < gd.EffectCount; k++)
            {
                EffectOp op = F.Effects[gd.EffectFirst + k];
                if (op.PostMask != 0 && !maskBaked && e.Mask >= 0)
                {
                    // After Effects order: masks, then effects and styles.
                    BakeMask(e);
                    maskBaked = true;
                }
                Surface s = R.Surfaces[e.Layer];
                int n = s.W * s.H;
                float* floats = (float*)F.PaintFloats.GetUnsafeReadOnlyPtr();
                switch (op.Kind)
                {
                    case EffectOpKind.GaussianBlur:
                    {
                        int mark = R.Arena.Length;
                        int tmpLen = 2 * math.max(s.W, s.H) + 2;
                        R.Arena.ResizeUninitialized(mark + tmpLen);
                        uint* tmp = (uint*)R.Arena.GetUnsafePtr() + mark;
                        Effects.BlurRGBA(Px(s), s.W, s.H, op.Z, op.W, tmp);
                        R.Arena.ResizeUninitialized(mark);
                        break;
                    }
                    case EffectOpKind.DropShadow:
                    {
                        int mark = R.Arena.Length;
                        int tmpLen = 2 * math.max(s.W, s.H) + 2;
                        R.Arena.ResizeUninitialized(mark + n + tmpLen);
                        uint* shadow = (uint*)R.Arena.GetUnsafePtr() + mark;
                        Effects.DropShadow(op, Px(s), s.W, s.H, shadow, shadow + n);
                        R.Arena.ResizeUninitialized(mark);
                        break;
                    }
                    case EffectOpKind.Fill:
                    case EffectOpKind.Tint:
                    case EffectOpKind.Tritone:
                    case EffectOpKind.Levels:
                        Effects.ColorOp(op, Px(s), n, floats);
                        break;
                    case EffectOpKind.ColorMatrix:
                    case EffectOpKind.HslMatrix:
                        Effects.ColorMatrix(op, Target(s), floats + op.Data, op.Kind == EffectOpKind.HslMatrix);
                        break;
                    case EffectOpKind.Saturate:
                    case EffectOpKind.Brightness:
                    case EffectOpKind.Contrast:
                    case EffectOpKind.BlackAndWhite:
                        Effects.PremulFilter(op, Target(s));
                        break;
                    case EffectOpKind.Threshold:
                        Effects.Threshold(op, Target(s));
                        break;
                    case EffectOpKind.ColorTables:
                        Effects.ColorTables(op, Target(s), floats);
                        break;
                    case EffectOpKind.GradientMap:
                        Effects.GradientMap(op, Target(s), floats + op.Data);
                        break;
                    case EffectOpKind.GradientRamp:
                        Effects.GradientRamp(op, Target(s));
                        break;
                    case EffectOpKind.LinearMask:
                        Effects.LinearMask(op, Target(s));
                        break;
                    case EffectOpKind.SweepMask:
                        Effects.SweepMask(op, Target(s));
                        break;
                    case EffectOpKind.FractalNoise:
                        Effects.FractalNoise(op, Target(s), floats + op.Data);
                        break;
                    case EffectOpKind.MotionTile:
                    case EffectOpKind.CornerPin:
                    case EffectOpKind.Bulge:
                    case EffectOpKind.Sphere:
                    case EffectOpKind.DirectionalBlur:
                    case EffectOpKind.Sharpen:
                    case EffectOpKind.DisplacementMap:
                    {
                        // These read the unmodified source anywhere while writing the output.
                        int mark = R.Arena.Length;
                        R.Arena.ResizeUninitialized(mark + n);
                        uint* copy = (uint*)R.Arena.GetUnsafePtr() + mark;
                        var dst = Target(s);
                        UnsafeUtility.MemCpy(copy, dst.Px, (long)n * 4);
                        var src = dst;
                        src.Px = copy;
                        switch (op.Kind)
                        {
                            case EffectOpKind.MotionTile: Effects.MotionTile(op, dst, src); break;
                            case EffectOpKind.CornerPin: Effects.CornerPin(op, dst, src, floats + op.Data); break;
                            case EffectOpKind.Bulge: Effects.Bulge(op, dst, src); break;
                            case EffectOpKind.Sphere: Effects.Sphere(op, dst, src, floats + op.Data); break;
                            case EffectOpKind.DirectionalBlur: Effects.DirectionalBlur(op, dst, src); break;
                            case EffectOpKind.DisplacementMap:
                                if (e.Aux >= 0) Effects.DisplacementMap(op, dst, src, Target(R.Surfaces[e.Aux]), floats + op.Data);
                                break;
                            default: Effects.Sharpen(op, dst, src); break;
                        }
                        R.Arena.ResizeUninitialized(mark);
                        break;
                    }
                    case EffectOpKind.StyleShadow:
                    case EffectOpKind.StyleGlow:
                    {
                        int mark = R.Arena.Length;
                        R.Arena.ResizeUninitialized(mark + 2 * n + 4 * math.max(s.W, s.H) + 4);
                        uint* tmp = (uint*)R.Arena.GetUnsafePtr() + mark;
                        if (op.Kind == EffectOpKind.StyleShadow) Effects.StyleShadow(op, Target(s), tmp);
                        else Effects.StyleGlow(op, Target(s), tmp);
                        R.Arena.ResizeUninitialized(mark);
                        break;
                    }
                }
            }
            return maskBaked;
        }

        Effects.Target Target(in Surface s) => new Effects.Target { Px = Px(s), X0 = s.X0, Y0 = s.Y0, W = s.W, H = s.H };

        /// <summary>Multiplies the layer surface by its mask (the mask then no longer applies at composite time).</summary>
        void BakeMask(in StackEntry e)
        {
            Surface s = R.Surfaces[e.Layer];
            Surface m = R.Surfaces[e.Mask];
            uint* px = Px(s);
            byte* mp = MaskPx(m);
            int n = s.W * s.H;
            // Mul(p, 255) == p and Mul(p, 0) == 0: the same as skipping full and clearing empty mask pixels.
            int i = 0;
            for (; i + 4 <= n; i += 4) *(uint4*)(px + i) = Px8.Mul(*(uint4*)(px + i), Px8.Bytes(mp + i));
            for (; i < n; i++) px[i] = Px8.Mul(px[i], mp[i]);
        }

        void CompositeGroup(in StackEntry e, int dstIndex)
        {
            GroupData gd = F.Groups[e.Group];
            // Nothing painted: compositing transparent pixels changes nothing (effects may still create content).
            if (R.Surfaces[e.Layer].Uncleared != 0 && gd.EffectCount == 0) return;
            EnsureCleared(e.Layer);
            if (e.Matte >= 0) EnsureCleared(e.Matte);
            if (e.Aux >= 0) EnsureCleared(e.Aux);
            EnsureCleared(dstIndex);
            bool maskBaked = gd.EffectCount > 0 && ApplyEffects(gd, e);
            if (gd.Projected != 0)
            {
                if (e.Mask >= 0 && !maskBaked) BakeMask(e);
                WarpGroup(gd, R.Surfaces[e.Layer], R.Surfaces[dstIndex]);
                return;
            }
            Surface src = R.Surfaces[e.Layer];
            Surface dst = R.Surfaces[dstIndex];
            int4 rect = IntersectI(src.Rect, dst.Rect);
            if (EmptyI(rect)) return;
            uint op8 = (uint)(math.saturate(gd.Opacity) * 255f + 0.5f);
            if (op8 == 0) return;
            int mode = gd.BlendMode;
            int matteMode = e.Matte >= 0 ? gd.MatteMode : 0;
            Surface matte = e.Matte >= 0 ? R.Surfaces[e.Matte] : default;
            Surface mask = e.Mask >= 0 ? R.Surfaces[e.Mask] : default;
            uint* sp = Px(src);
            uint* dp = Px(dst);
            uint* mp = e.Matte >= 0 ? Px(matte) : null;
            byte* kp = e.Mask >= 0 && !maskBaked ? MaskPx(mask) : null;
            if (Stats != null) Stats[1] += (long)(rect.z - rect.x) * (rect.w - rect.y);
            // Matte factor: a channel (alpha, red, green, blue) or the luma, optionally inverted.
            bool luma = matteMode == 3 || matteMode == 4;
            int shift = matteMode <= 2 ? 24 : matteMode <= 6 ? 0 : matteMode <= 8 ? 8 : 16;
            bool invert = (matteMode & 1) == 0;
            bool hasMatte = mp != null && matteMode >= 1 && matteMode <= 10;

            for (int y = rect.y; y < rect.w; y++)
            {
                uint* srow = sp + (y - src.Y0) * src.W - src.X0;
                uint* drow = dp + (y - dst.Y0) * dst.W - dst.X0;
                uint* mrow = mp != null ? mp + (y - matte.Y0) * matte.W - matte.X0 : null;
                byte* krow = kp != null ? kp + (y - mask.Y0) * mask.W - mask.X0 : null;
                if (mode == 0)
                {
                    // Branch-free per pixel: Mul(s, 255) == s, Mul(s, 0) == 0 and Over(0, d) == d, so this matches the
                    // scalar loop below bit for bit; blocks of four transparent source pixels are skipped.
                    int x = rect.x;
                    if (mrow == null && krow == null)
                    {
                        if (op8 == 255)
                        {
                            for (; x + 4 <= rect.z; x += 4)
                            {
                                uint4 sv = *(uint4*)(srow + x);
                                // Over(s, d) == s when every alpha is 255.
                                if (math.all(sv >= 0xFF000000u)) *(uint4*)(drow + x) = sv;
                                else if (math.any(sv != 0)) *(uint4*)(drow + x) = Px8.Over(sv, *(uint4*)(drow + x));
                            }
                        }
                        else
                        {
                            for (; x + 4 <= rect.z; x += 4)
                            {
                                uint4 sv = *(uint4*)(srow + x);
                                if (math.any(sv != 0)) *(uint4*)(drow + x) = Px8.Over(Px8.Mul(sv, op8), *(uint4*)(drow + x));
                            }
                        }
                    }
                    else
                    {
                        for (; x + 4 <= rect.z; x += 4)
                        {
                            uint4 sv = *(uint4*)(srow + x);
                            if (!math.any(sv != 0)) continue;
                            uint4 k = op8;
                            if (krow != null) k = Px8.Mul8(k, Px8.Bytes(krow + x));
                            if (hasMatte)
                            {
                                uint4 m = *(uint4*)(mrow + x);
                                uint4 f = !math.any(m != 0) ? 0u : luma ? Px8.Luma(m) : (m >> shift) & 0xFFu;
                                k = Px8.Mul8(k, invert ? 255u - f : f);
                            }
                            // k == 0 keeps the destination, k == 255 the source pixel, opaque source replaces it.
                            if (!math.any(k != 0)) continue;
                            uint4 s = math.all(k == 255u) ? sv : Px8.Mul(sv, k);
                            if (math.all(s >= 0xFF000000u)) *(uint4*)(drow + x) = s;
                            else *(uint4*)(drow + x) = Px8.Over(s, *(uint4*)(drow + x));
                        }
                    }
                    for (; x < rect.z; x++)
                    {
                        uint sv = srow[x];
                        if (sv == 0) continue;
                        uint k = op8;
                        if (krow != null) k = Px8.Mul8(k, krow[x]);
                        if (mrow != null && k != 0) k = Px8.Mul8(k, MatteFactor8(matteMode, mrow[x]));
                        if (k == 0) continue;
                        drow[x] = Px8.Over(k == 255 ? sv : Px8.Mul(sv, k), drow[x]);
                    }
                    continue;
                }
                for (int x = rect.x; x < rect.z; x++)
                {
                    uint sv = srow[x];
                    if (sv == 0) continue;
                    uint k = op8;
                    if (krow != null) k = Px8.Mul8(k, krow[x]);
                    if (mrow != null && k != 0) k = Px8.Mul8(k, MatteFactor8(matteMode, mrow[x]));
                    if (k == 0) continue;
                    uint s = k == 255 ? sv : Px8.Mul(sv, k);
                    if (mode == 0) drow[x] = Px8.Over(s, drow[x]);
                    else drow[x] = Blend.Pack(Blend.Composite(mode, Blend.Unpack(s), Blend.Unpack(drow[x])));
                }
            }
        }

        /// <summary>Draws a projected group's raster surface into the parent through its homography (bilinear).</summary>
        void WarpGroup(in GroupData gd, in Surface src, in Surface dst)
        {
            float4 b = SceneBuilder.Intersect(ProjectRect(gd.Projection, new float4(src.X0, src.Y0, src.X0 + src.W, src.Y0 + src.H)),
                new float4(dst.X0, dst.Y0, dst.X0 + dst.W, dst.Y0 + dst.H));
            int4 rect = RoundOut(b);
            if (EmptyI(rect)) return;
            float3x3 inv = math.inverse(gd.Projection);
            float3 w = new float3(gd.Projection.c0.z, gd.Projection.c1.z, gd.Projection.c2.z);
            uint op8 = (uint)(math.saturate(gd.Opacity) * 255f + 0.5f);
            uint* sp = Px(src);
            uint* dp = Px(dst);
            var s = new Effects.Target { Px = sp, X0 = src.X0, Y0 = src.Y0, W = src.W, H = src.H };
            for (int y = rect.y; y < rect.w; y++)
            {
                uint* drow = dp + (y - dst.Y0) * dst.W - dst.X0;
                for (int x = rect.x; x < rect.z; x++)
                {
                    float3 q = math.mul(inv, new float3(x + 0.5f, y + 0.5f, 1));
                    if (math.abs(q.z) < 1e-12f) continue;
                    float2 r = q.xy / q.z;
                    // Only points of the plane in front of the camera.
                    if (!(math.dot(w, new float3(r, 1)) > 0)) continue;
                    if (r.x < src.X0 - 1 || r.y < src.Y0 - 1 || r.x > src.X0 + src.W + 1 || r.y > src.Y0 + src.H + 1) continue;
                    uint c = Effects.SampleBilinear(s, r);
                    if (c == 0) continue;
                    if (op8 < 255) c = Px8.Mul(c, op8);
                    drow[x] = Px8.Over(c, drow[x]);
                }
            }
        }

        static uint MatteFactor8(int mode, uint px)
        {
            switch (mode)
            {
                case 1: return px >> 24;
                case 2: return 255 - (px >> 24);
                case 3: return Px8.Luma(px);
                case 4: return 255 - Px8.Luma(px);
                case 5: return px & 0xFF;
                case 6: return 255 - (px & 0xFF);
                case 7: return (px >> 8) & 0xFF;
                case 8: return 255 - ((px >> 8) & 0xFF);
                case 9: return (px >> 16) & 0xFF;
                case 10: return 255 - ((px >> 16) & 0xFF);
                default: return 255;
            }
        }

        // ================================================================== items

        bool PrepareCoverage(int itemIndex, in DrawItem item, int4 targetRect, out int4 box, out CoverageRasterizer ras)
        {
            ras = default;
            box = IntersectI(RoundOut(G.ItemBounds[itemIndex]), targetRect);
            if (EmptyI(box)) return false;
            int w = box.z - box.x, h = box.w - box.y;
            int need = CoverageRasterizer.CellStride(w) * h;
            if (R.Acc.Length < need)
            {
                int old = R.Acc.Length;
                R.Acc.ResizeUninitialized(need);
                UnsafeUtility.MemClear((float*)R.Acc.GetUnsafePtr() + old, (long)(need - old) * 4);
            }
            int words = CoverageRasterizer.WordsPerRow(w) * h;
            if (R.Bits.Length < words)
            {
                int old = R.Bits.Length;
                R.Bits.ResizeUninitialized(words);
                UnsafeUtility.MemClear((ulong*)R.Bits.GetUnsafePtr() + old, (long)(words - old) * 8);
            }
            if (R.RowMin.Length < h)
            {
                R.RowMin.ResizeUninitialized(h);
                R.RowMax.ResizeUninitialized(h);
            }
            int spansNeed = 2 * (w + 2) + 2;
            if (R.Spans.Length < spansNeed) R.Spans.ResizeUninitialized(spansNeed);
            float4 clip = SceneBuilder.Intersect(item.Clip, new float4(box.x, box.y, box.z, box.w));
            clip -= new float4(box.x, box.y, box.x, box.y);
            ras.Begin((float*)R.Acc.GetUnsafePtr(), (ulong*)R.Bits.GetUnsafePtr(), (int*)R.RowMin.GetUnsafePtr(),
                (int*)R.RowMax.GetUnsafePtr(), w, h, clip);

            int2 geo = G.ItemGeo[itemIndex];
            float2* gp = (float2*)G.GeoPts.GetUnsafeReadOnlyPtr();
            float2 origin = new float2(box.x, box.y);
            for (int p = 0; p < geo.y; p++)
            {
                Poly poly = G.GeoPolys[geo.x + p];
                if (poly.Count < 2) continue;
                float2* v = gp + poly.First;
                float2 prev = v[poly.Count - 1] - origin;
                for (int k = 0; k < poly.Count; k++)
                {
                    float2 cur = v[k] - origin;
                    ras.Edge(prev.x, prev.y, cur.x, cur.y);
                    prev = cur;
                }
                if (Stats != null) Stats[3] += poly.Count;
            }
            return true;
        }

        /// <summary>
        /// Paints an item into a surface. A <paramref name="fresh"/> surface has not been cleared yet: the item is
        /// painted onto transparent black (Over(c, 0) == c) and every pixel it does not paint is cleared, so the
        /// surface is written once. Returns false when nothing was written (a fresh surface stays uncleared).
        /// </summary>
        bool DrawItemInto(int itemIndex, in DrawItem item, in Surface target, bool fresh)
        {
            if (!PrepareCoverage(itemIndex, item, target.Rect, out int4 box, out CoverageRasterizer ras)) return false;
            PaintData paint = F.Paints[item.Paint];
            var shader = PaintShader.Create(paint, F.PaintFloats, ImagePixels, ImageInfo, ImageCount);
            if (Stats != null) Stats[4]++;
            bool evenOdd = item.FillRule == 1;
            var spans = (CoverageSpan*)R.Spans.GetUnsafePtr();
            int h = box.w - box.y;
            if (shader.Transparent)
            {
                for (int y = 0; y < h; y++) ras.Sweep(y, evenOdd, spans, MinCoverage);
                return false;
            }
            uint* px = Px(target);
            if (fresh)
            {
                // Rows above and below the item.
                ClearPixels(px, (long)(box.y - target.Y0) * target.W);
                ClearPixels(px + (long)(box.w - target.Y0) * target.W, (long)(target.Y0 + target.H - box.w) * target.W);
            }
            uint solid = shader.IsSolid ? Px8.Pack(shader.Solid) : 0;
            bool gradient = !shader.IsSolid && !shader.IsImage;
            uint* lut = stackalloc uint[gradient ? PaintShader.LutSize : 1];
            if (gradient) shader.BuildLut(lut);
            for (int y = 0; y < h; y++)
            {
                int n = ras.Sweep(y, evenOdd, spans, MinCoverage);
                int dy = box.y + y;
                uint* row = px + (dy - target.Y0) * target.W + (box.x - target.X0);
                // Fresh surface: pixels of the row before this (box-relative) x are written.
                int done = target.X0 - box.x;
                for (int k = 0; k < n; k++)
                {
                    CoverageSpan sp = spans[k];
                    uint cov = (uint)(sp.Cov * 255f + 0.5f);
                    if (cov == 0) continue;
                    int len = sp.X1 - sp.X0;
                    uint* p = row + sp.X0;
                    if (Stats != null) Stats[0] += len;
                    if (shader.IsSolid)
                    {
                        uint c = cov >= 255 ? solid : Px8.Mul(solid, cov);
                        if (c == 0) continue;
                        if (fresh)
                        {
                            ClearPixels(row + done, sp.X0 - done);
                            done = sp.X1;
                        }
                        if (fresh || (c >> 24) == 255)
                        {
                            if (Stats != null) Stats[6] += len;
                            for (int i = 0; i < len; i++) p[i] = c;
                        }
                        else
                        {
                            uint inv = 255 - (c >> 24);
                            for (int i = 0; i < len; i++) p[i] = c + Px8.Mul(p[i], inv);
                        }
                    }
                    else
                    {
                        if (fresh)
                        {
                            ClearPixels(row + done, sp.X0 - done);
                            done = sp.X1;
                        }
                        if (Stats != null) Stats[5] += len;
                        float py = dy + 0.5f;
                        float fx = box.x + sp.X0 + 0.5f;
                        if (shader.IsImage)
                        {
                            for (int i = 0; i < len; i++)
                            {
                                uint c = shader.SampleImage(new float2(fx + i, py));
                                if (cov < 255) c = Px8.Mul(c, cov);
                                if (fresh) p[i] = c;
                                else if (c != 0) p[i] = Px8.Over(c, p[i]);
                            }
                        }
                        else if (shader.IsLinear)
                        {
                            float t = shader.T(new float2(fx, py));
                            float dt = shader.LinearStepX;
                            for (int i = 0; i < len; i++, t += dt)
                            {
                                uint c = lut[(int)(math.saturate(t) * (PaintShader.LutSize - 1) + 0.5f)];
                                if (cov < 255) c = Px8.Mul(c, cov);
                                if (fresh) p[i] = c;
                                else if (c != 0) p[i] = Px8.Over(c, p[i]);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < len; i++)
                            {
                                float t = shader.T(new float2(fx + i, py));
                                uint c = lut[(int)(math.saturate(t) * (PaintShader.LutSize - 1) + 0.5f)];
                                if (cov < 255) c = Px8.Mul(c, cov);
                                if (fresh) p[i] = c;
                                else if (c != 0) p[i] = Px8.Over(c, p[i]);
                            }
                        }
                    }
                }
                if (fresh) ClearPixels(row + done, target.X0 + target.W - box.x - done);
            }
            return true;
        }

        void ClearPixels(uint* p, long count)
        {
            if (count <= 0) return;
            UnsafeUtility.MemClear(p, count * 4);
            if (Stats != null) Stats[2] += count;
        }

        /// <summary>Clears a surface nothing has been painted into yet (see <see cref="DrawItemInto"/>).</summary>
        void EnsureCleared(int index)
        {
            Surface s = R.Surfaces[index];
            if (s.Uncleared == 0) return;
            ClearPixels(Px(s), (long)s.W * s.H);
            s.Uncleared = 0;
            R.Surfaces[index] = s;
        }

        // ------------------------------------------------------------------ masks

        int _mergeOffset;   // temp coverage buffer (mask arena offset) of a pending merge chain, -1 none

        void DrawMaskItem(int itemIndex, in DrawItem item, in Surface target)
        {
            PaintData paint = F.Paints[item.Paint];
            uint op8 = (uint)(math.saturate(paint.Opacity * paint.Color.w) * 255f + 0.5f);
            bool evenOdd = item.FillRule == 1;
            bool chained = _mergeOffset >= 0 || item.MaskMerge != 0;
            bool needsTemp = chained || math.any(item.Erode > 0) ||
                             item.Mask == MaskOp.Intersect || item.Mask == MaskOp.Darken;
            int area = target.W * target.H;

            if (!needsTemp)
            {
                // Direct application: pixels outside the coverage are unaffected by add / subtract / lighten / difference.
                if (!PrepareCoverage(itemIndex, item, target.Rect, out int4 b, out CoverageRasterizer r)) return;
                var spans = (CoverageSpan*)R.Spans.GetUnsafePtr();
                byte* mp = MaskPx(target);
                MaskOp direct = item.Mask;
                int h = b.w - b.y;
                for (int y = 0; y < h; y++)
                {
                    int n = r.Sweep(y, evenOdd, spans, MinCoverage);
                    byte* row = mp + (b.y + y - target.Y0) * target.W + (b.x - target.X0);
                    for (int k = 0; k < n; k++)
                    {
                        CoverageSpan sp = spans[k];
                        uint a = Px8.Mul8((uint)(sp.Cov * 255f + 0.5f), op8);
                        if (a != 0) MaskSpan(direct, row + sp.X0, sp.X1 - sp.X0, a);
                    }
                }
                return;
            }

            // Render the coverage into a temporary buffer (union with a pending chain).
            if (_mergeOffset < 0)
            {
                _mergeOffset = R.MaskArena.Length;
                R.MaskArena.ResizeUninitialized(_mergeOffset + area);
                UnsafeUtility.MemClear((byte*)R.MaskArena.GetUnsafePtr() + _mergeOffset, area);
            }
            if (PrepareCoverage(itemIndex, item, target.Rect, out int4 box, out CoverageRasterizer ras))
            {
                var spans = (CoverageSpan*)R.Spans.GetUnsafePtr();
                byte* tp = (byte*)R.MaskArena.GetUnsafePtr() + _mergeOffset;
                int h = box.w - box.y;
                for (int y = 0; y < h; y++)
                {
                    int n = ras.Sweep(y, evenOdd, spans, MinCoverage);
                    byte* row = tp + (box.y + y - target.Y0) * target.W + (box.x - target.X0);
                    for (int k = 0; k < n; k++)
                    {
                        CoverageSpan sp = spans[k];
                        uint a = Px8.Mul8((uint)(sp.Cov * 255f + 0.5f), op8);
                        if (a != 0) MaskSpan(MaskOp.Add, row + sp.X0, sp.X1 - sp.X0, a);
                    }
                }
            }
            if (item.MaskMerge != 0) return; // wait for the rest of the chain

            if (math.any(item.Erode > 0)) Erode(target.W, target.H, item.Erode);
            byte* temp = (byte*)R.MaskArena.GetUnsafePtr() + _mergeOffset;
            ApplyMask(item.Mask, MaskPx(target), temp, area);
            R.MaskArena.ResizeUninitialized(_mergeOffset);
            _mergeOffset = -1;
        }

        /// <summary>Applies a mask op with constant coverage <paramref name="a"/> to a run of mask bytes.</summary>
        static void MaskSpan(MaskOp op, byte* p, int len, uint a)
        {
            // Full coverage sets the mask to 255 (add, lighten) or 0 (subtract).
            if (a == 255 && len >= 16 && (op == MaskOp.Add || op == MaskOp.Lighten || op == MaskOp.Subtract))
            {
                UnsafeUtility.MemSet(p, op == MaskOp.Subtract ? (byte)0 : (byte)255, len);
                return;
            }
            switch (op)
            {
                case MaskOp.Subtract: for (int x = 0; x < len; x++) p[x] = (byte)Px8.Mul8(p[x], 255 - a); break;
                case MaskOp.Intersect: for (int x = 0; x < len; x++) p[x] = (byte)Px8.Mul8(p[x], a); break;
                case MaskOp.Lighten: for (int x = 0; x < len; x++) p[x] = (byte)math.max(p[x], a); break;
                case MaskOp.Darken: for (int x = 0; x < len; x++) p[x] = (byte)math.min(p[x], a); break;
                case MaskOp.Difference:
                    for (int x = 0; x < len; x++)
                    {
                        uint m = p[x];
                        p[x] = (byte)(m > a ? m - a : a - m);
                    }
                    break;
                default:
                    for (int x = 0; x < len; x++)
                    {
                        uint m = p[x];
                        p[x] = (byte)(m + Px8.Mul8(255 - m, a));
                    }
                    break;
            }
        }

        /// <summary>
        /// Combines a coverage buffer into a mask. Zero coverage leaves the mask unchanged for add, subtract, lighten
        /// and difference, so every op runs over the whole buffer without a test.
        /// </summary>
        static void ApplyMask(MaskOp op, byte* dst, byte* cov, int n)
        {
            switch (op)
            {
                case MaskOp.Subtract: for (int i = 0; i < n; i++) dst[i] = (byte)Px8.Mul8(dst[i], 255u - cov[i]); break;
                case MaskOp.Intersect: for (int i = 0; i < n; i++) dst[i] = (byte)Px8.Mul8(dst[i], cov[i]); break;
                case MaskOp.Lighten: for (int i = 0; i < n; i++) dst[i] = (byte)math.max(dst[i], (uint)cov[i]); break;
                case MaskOp.Darken: for (int i = 0; i < n; i++) dst[i] = (byte)math.min(dst[i], (uint)cov[i]); break;
                case MaskOp.Difference:
                    for (int i = 0; i < n; i++)
                    {
                        uint m = dst[i], a = cov[i];
                        dst[i] = (byte)(m > a ? m - a : a - m);
                    }
                    break;
                default:
                    for (int i = 0; i < n; i++)
                    {
                        uint m = dst[i];
                        dst[i] = (byte)(m + Px8.Mul8(255u - m, cov[i]));
                    }
                    break;
            }
        }

        /// <summary>Separable min filter (feMorphology erode) with radii in pixels.</summary>
        void Erode(int w, int h, float2 radius)
        {
            int rx = (int)math.round(radius.x), ry = (int)math.round(radius.y);
            if (rx <= 0 && ry <= 0) return;
            int tmpOffset = R.MaskArena.Length;
            int line = math.max(w, h);
            R.MaskArena.ResizeUninitialized(tmpOffset + line);
            byte* buf = (byte*)R.MaskArena.GetUnsafePtr() + _mergeOffset;
            byte* tmp = (byte*)R.MaskArena.GetUnsafePtr() + tmpOffset;
            if (rx > 0)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = buf + y * w;
                    UnsafeUtility.MemCpy(tmp, row, w);
                    for (int x = 0; x < w; x++)
                    {
                        int mn = 255;
                        int x0 = x - rx, x1 = x + rx;
                        if (x0 < 0 || x1 >= w) mn = 0; // outside the region is transparent
                        else for (int k = x0; k <= x1 && mn > 0; k++) mn = math.min(mn, tmp[k]);
                        row[x] = (byte)mn;
                    }
                }
            }
            if (ry > 0)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++) tmp[y] = buf[y * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        int mn = 255;
                        int y0 = y - ry, y1 = y + ry;
                        if (y0 < 0 || y1 >= h) mn = 0;
                        else for (int k = y0; k <= y1 && mn > 0; k++) mn = math.min(mn, tmp[k]);
                        buf[y * w + x] = (byte)mn;
                    }
                }
            }
            R.MaskArena.ResizeUninitialized(tmpOffset);
        }
    }

    /// <summary>Packed premultiplied RGBA8 arithmetic (SWAR, exact rounding of x/255).</summary>
    public static unsafe class Px8
    {
        /// <summary>Multiplies all four channels by a/255 (a in 0..255).</summary>
        public static uint Mul(uint p, uint a)
        {
            uint rb = (p & 0x00FF00FFu) * a + 0x00800080u;
            rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
            uint ga = ((p >> 8) & 0x00FF00FFu) * a + 0x00800080u;
            ga = (ga + ((ga >> 8) & 0x00FF00FFu)) & 0xFF00FF00u;
            return rb | ga;
        }

        /// <summary>Single-channel a*b/255 with rounding.</summary>
        public static uint Mul8(uint a, uint b)
        {
            uint t = a * b + 128;
            return (t + (t >> 8)) >> 8;
        }

        public static uint Over(uint s, uint d) => s + Mul(d, 255 - (s >> 24));

        /// <summary><see cref="Mul(uint, uint)"/> on four pixels.</summary>
        public static uint4 Mul(uint4 p, uint4 a)
        {
            uint4 rb = (p & 0x00FF00FFu) * a + 0x00800080u;
            rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
            uint4 ga = ((p >> 8) & 0x00FF00FFu) * a + 0x00800080u;
            ga = (ga + ((ga >> 8) & 0x00FF00FFu)) & 0xFF00FF00u;
            return rb | ga;
        }

        public static uint4 Mul8(uint4 a, uint4 b)
        {
            uint4 t = a * b + 128u;
            return (t + (t >> 8)) >> 8;
        }

        public static uint4 Over(uint4 s, uint4 d) => s + Mul(d, 255u - (s >> 24));

        public static uint4 Luma(uint4 p) => ((p & 0xFF) * 54u + ((p >> 8) & 0xFF) * 183u + ((p >> 16) & 0xFF) * 19u + 128u) >> 8;

        /// <summary>Four bytes widened to four lanes.</summary>
        public static uint4 Bytes(byte* p)
        {
            uint w = *(uint*)p;
            return new uint4(w, w >> 8, w >> 16, w >> 24) & 0xFFu;
        }

        public static uint Pack(float4 c)
        {
            uint4 u = (uint4)(math.saturate(c) * 255f + 0.5f);
            return u.x | (u.y << 8) | (u.z << 16) | (u.w << 24);
        }

        /// <summary>Luminance (Rec.709 weights) of a premultiplied pixel, 0..255.</summary>
        public static uint Luma(uint p) => ((p & 0xFF) * 54 + ((p >> 8) & 0xFF) * 183 + ((p >> 16) & 0xFF) * 19 + 128) >> 8;
    }

    /// <summary>Evaluates a paint (solid, linear / radial gradient) to premultiplied colour at device points.</summary>
    public unsafe struct PaintShader
    {
        public bool IsSolid;
        public bool Transparent;
        public float4 Solid;
        PaintKind _kind;
        Affine2 _toPaint;
        float2 _p0, _dir;
        float _invLen2;
        float2 _focal, _cd;
        float _a, _radius;
        float* _stops;
        int _count;
        float _opacity;

        uint* _img;
        int _imgW, _imgH;
        float2 _imgScale, _imgOffset;
        uint _opacity8;
        // Minified images: two mip levels (base pointers, sizes) and the blend between them.
        uint* _mip0, _mip1;
        int2 _mip0Size, _mip1Size;
        float _mip0Scale, _mip1Scale, _mipBlend;
        bool _mipmapped;

        public bool IsImage => _kind == PaintKind.Image;

        public static PaintShader Create(in PaintData p, NativeList<float> floats, uint* imagePixels = null, int4* imageInfo = null, int imageCount = 0)
        {
            var s = new PaintShader { _kind = p.Kind, _opacity = math.saturate(p.Opacity) };
            s._opacity8 = (uint)(s._opacity * 255f + 0.5f);
            switch (p.Kind)
            {
                case PaintKind.Solid:
                {
                    float a = math.saturate(p.Color.w) * s._opacity;
                    s.IsSolid = true;
                    s.Solid = new float4(math.saturate(p.Color.xyz) * a, a);
                    s.Transparent = a <= 0;
                    break;
                }
                case PaintKind.Linear:
                case PaintKind.Radial:
                {
                    s._toPaint = p.DeviceToPaint;
                    s._stops = (float*)floats.GetUnsafeReadOnlyPtr() + p.StopFirst;
                    s._count = p.StopCount;
                    s.Transparent = s._opacity <= 0 || p.StopCount <= 0;
                    if (s.Transparent) break;
                    if (p.Kind == PaintKind.Linear)
                    {
                        s._p0 = p.P0;
                        s._dir = p.P1 - p.P0;
                        float l2 = math.lengthsq(s._dir);
                        if (!(l2 > 1e-12f)) MakeLastStop(ref s);
                        else s._invLen2 = 1f / l2;
                    }
                    else
                    {
                        s._radius = p.Radius;
                        s._focal = p.Focal;
                        s._cd = p.P0 - p.Focal;
                        s._a = math.lengthsq(s._cd) - p.Radius * p.Radius;
                        if (!(p.Radius > 1e-6f)) MakeLastStop(ref s);
                    }
                    break;
                }
                case PaintKind.Image:
                {
                    s._toPaint = p.DeviceToPaint;
                    s.Transparent = true;
                    if (imagePixels == null || p.Image < 0 || p.Image >= imageCount) break;
                    int4 info = imageInfo[p.Image];
                    if (info.x < 0 || info.y <= 0 || info.z <= 0 || s._opacity8 == 0) break;
                    s._img = imagePixels + info.x;
                    s._imgW = info.y;
                    s._imgH = info.z;
                    // SVG <image preserveAspectRatio="xMidYMid slice"> into the asset's w x h box.
                    float2 box = p.ImageSize;
                    float scale = math.max(box.x / info.y, box.y / info.z);
                    s._imgScale = new float2(1f / scale);
                    s._imgOffset = (box - new float2(info.y, info.z) * scale) * 0.5f;
                    s.Transparent = false;
                    // Level of detail: image texels per device pixel (the paint transform is affine, so it is constant).
                    float2 dx = s._toPaint.ApplyVector(new float2(1, 0)) * s._imgScale, dy = s._toPaint.ApplyVector(new float2(0, 1)) * s._imgScale;
                    float lod = math.log2(math.max(math.max(math.length(dx), math.length(dy)), 1e-6f));
                    if (info.w > 1 && lod > 0)
                    {
                        int l0 = math.min((int)lod, info.w - 1), l1 = math.min(l0 + 1, info.w - 1);
                        s._mipmapped = true;
                        s._mipBlend = l1 > l0 ? math.saturate(lod - l0) : 0;
                        MipLevel(imagePixels + info.x, info.y, info.z, l0, out s._mip0, out s._mip0Size);
                        MipLevel(imagePixels + info.x, info.y, info.z, l1, out s._mip1, out s._mip1Size);
                        s._mip0Scale = 1f / (1 << l0);
                        s._mip1Scale = 1f / (1 << l1);
                    }
                    break;
                }
                default:
                    s.Transparent = true;
                    break;
            }
            return s;
        }

        static void MakeLastStop(ref PaintShader s)
        {
            // SVG: degenerate gradients paint with the last stop.
            float* st = s._stops + (s._count - 1) * 5;
            float a = math.saturate(st[4]) * s._opacity;
            s.IsSolid = true;
            s.Solid = new float4(new float3(st[1], st[2], st[3]) * a, a);
            s.Transparent = a <= 0;
        }

        /// <summary>
        /// Bilinear sample (premultiplied, clamp to edge) of the image paint at a device point; minified images are
        /// sampled trilinearly from their mip levels.
        /// </summary>
        public uint SampleImage(float2 device)
        {
            float2 p = (_toPaint.Apply(device) - _imgOffset) * _imgScale;
            uint c;
            if (_mipmapped)
            {
                c = Bilinear(_mip0, _mip0Size.x, _mip0Size.y, p * _mip0Scale - 0.5f);
                if (_mipBlend > 0)
                {
                    uint c1 = Bilinear(_mip1, _mip1Size.x, _mip1Size.y, p * _mip1Scale - 0.5f);
                    c = Lerp256(c, c1, (uint)(_mipBlend * 256f + 0.5f));
                }
            }
            else c = Bilinear(_img, _imgW, _imgH, p - 0.5f);
            return _opacity8 >= 255 ? c : Px8.Mul(c, _opacity8);
        }

        static uint Bilinear(uint* img, int w, int h, float2 p)
        {
            float2 f = math.floor(p);
            int x0 = (int)f.x, y0 = (int)f.y;
            float2 t = p - f;
            int xa = math.clamp(x0, 0, w - 1), xb = math.clamp(x0 + 1, 0, w - 1);
            int ya = math.clamp(y0, 0, h - 1), yb = math.clamp(y0 + 1, 0, h - 1);
            uint* r0 = img + ya * w;
            uint* r1 = img + yb * w;
            uint wx = (uint)(t.x * 256f + 0.5f), wy = (uint)(t.y * 256f + 0.5f);
            uint top = Lerp256(r0[xa], r0[xb], wx);
            uint bot = Lerp256(r1[xa], r1[xb], wx);
            return Lerp256(top, bot, wy);
        }

        static void MipLevel(uint* base0, int w, int h, int level, out uint* ptr, out int2 size)
        {
            ptr = base0;
            for (int l = 0; l < level; l++)
            {
                ptr += w * h;
                w = math.max(1, w >> 1);
                h = math.max(1, h >> 1);
            }
            size = new int2(w, h);
        }

        static uint Lerp256(uint a, uint b, uint w)
        {
            if (w == 0) return a;
            if (w >= 256) return b;
            uint iw = 256 - w;
            uint rb = ((a & 0x00FF00FFu) * iw + (b & 0x00FF00FFu) * w + 0x00800080u) >> 8;
            uint ga = (((a >> 8) & 0x00FF00FFu) * iw + ((b >> 8) & 0x00FF00FFu) * w + 0x00800080u) >> 8;
            return (rb & 0x00FF00FFu) | ((ga & 0x00FF00FFu) << 8);
        }

        public const int LutSize = 1024;

        public bool IsLinear => _kind == PaintKind.Linear;

        /// <summary>Change of the linear gradient parameter per device pixel along +x.</summary>
        public float LinearStepX => math.dot(_toPaint.ApplyVector(new float2(1, 0)), _dir) * _invLen2;

        /// <summary>Gradient parameter (unclamped) at a device point.</summary>
        public float T(float2 device)
        {
            float2 p = _toPaint.Apply(device);
            if (_kind == PaintKind.Linear) return math.dot(p - _p0, _dir) * _invLen2;
            float2 pd = p - _focal;
            float b = math.dot(pd, _cd);
            float cc = math.dot(pd, pd);
            if (math.abs(_a) < 1e-9f) return b != 0 ? cc / (2f * b) : 0f;
            return (b - math.sqrt(math.max(0f, b * b - _a * cc))) / _a;
        }

        public float4 Shade(float2 device) => StopColor(math.saturate(T(device)));

        /// <summary>Fills a <see cref="LutSize"/>-entry table of packed premultiplied colours over t in [0, 1].</summary>
        public void BuildLut(uint* lut)
        {
            for (int i = 0; i < LutSize; i++) lut[i] = Px8.Pack(StopColor(i / (float)(LutSize - 1)));
        }

        float4 StopColor(float t)
        {
            float* s = _stops;
            int n = _count;
            float4 c;
            if (n == 1 || t <= s[0]) c = new float4(s[1], s[2], s[3], s[4]);
            else if (t >= s[(n - 1) * 5]) { float* l = s + (n - 1) * 5; c = new float4(l[1], l[2], l[3], l[4]); }
            else
            {
                int i = 0;
                while (i < n - 2 && t >= s[(i + 1) * 5]) i++;
                float* a = s + i * 5;
                float* b = a + 5;
                float span = b[0] - a[0];
                float f = span > 0 ? (t - a[0]) / span : 1f;
                c = math.lerp(new float4(a[1], a[2], a[3], a[4]), new float4(b[1], b[2], b[3], b[4]), f);
            }
            float alpha = math.saturate(c.w) * _opacity;
            return new float4(math.saturate(c.xyz) * alpha, alpha);
        }
    }
}
