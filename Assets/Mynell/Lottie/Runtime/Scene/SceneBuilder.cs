using Mynell.Lottie.Eval;
using Mynell.Lottie.Model;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Scene
{
    /// <summary>Scratch buffers reused by the scene builder (per player instance).</summary>
    public struct SceneScratch : System.IDisposable
    {
        public NativeList<Affine2> GroupLocal;
        public NativeList<Affine2> GroupWorld;
        public NativeList<float> GroupOpacity;
        public NativeList<byte> GroupHidden;
        public NativeList<int2> ShapeRanges;   // contour (first, count) per shape instance
        public NativeList<int> ShapeSpace;     // group whose space a shape's contours are in (merge results: the merge's group)
        public NativeList<float> Lengths;      // trim length tables
        public NativeList<float2> Temp;        // temporary points
        public NativeList<Contour> TempContours;

        public static SceneScratch Create()
        {
            return new SceneScratch
            {
                GroupLocal = new NativeList<Affine2>(32, Allocator.Persistent),
                GroupWorld = new NativeList<Affine2>(32, Allocator.Persistent),
                GroupOpacity = new NativeList<float>(32, Allocator.Persistent),
                GroupHidden = new NativeList<byte>(32, Allocator.Persistent),
                ShapeRanges = new NativeList<int2>(64, Allocator.Persistent),
                ShapeSpace = new NativeList<int>(64, Allocator.Persistent),
                Lengths = new NativeList<float>(1024, Allocator.Persistent),
                Temp = new NativeList<float2>(256, Allocator.Persistent),
                TempContours = new NativeList<Contour>(16, Allocator.Persistent),
            };
        }

        public void Dispose()
        {
            if (!GroupLocal.IsCreated) return;
            GroupLocal.Dispose(); GroupWorld.Dispose(); GroupOpacity.Dispose(); GroupHidden.Dispose();
            ShapeRanges.Dispose(); ShapeSpace.Dispose(); Lengths.Dispose(); Temp.Dispose(); TempContours.Dispose();
        }
    }

    /// <summary>
    /// Evaluates a Lottie composition at a given frame and produces draw items + compositing commands.
    /// Semantics follow lottie-web's SVG renderer.
    /// </summary>
    public unsafe partial struct SceneBuilder
    {
        public PropReader P;
        [NativeDisableUnsafePtrRestriction] public CompData* Comps;
        [NativeDisableUnsafePtrRestriction] public LayerData* Layers;
        [NativeDisableUnsafePtrRestriction] public ShapeNode* Nodes;
        [NativeDisableUnsafePtrRestriction] public MaskData* Masks;
        [NativeDisableUnsafePtrRestriction] public DashData* DashTable;
        [NativeDisableUnsafePtrRestriction] public ImageAsset* Images;
        [NativeDisableUnsafePtrRestriction] public EffectData* Effects;
        [NativeDisableUnsafePtrRestriction] public EffectParam* EffectParams;
        [NativeDisableUnsafePtrRestriction] public ShapeProgram* Programs;
        [NativeDisableUnsafePtrRestriction] public SPGroup* PGroups;
        [NativeDisableUnsafePtrRestriction] public SPShape* PShapes;
        [NativeDisableUnsafePtrRestriction] public SPModifier* PModifiers;
        [NativeDisableUnsafePtrRestriction] public SPStyle* PStyles;
        [NativeDisableUnsafePtrRestriction] public SPOp* POps;
        [NativeDisableUnsafePtrRestriction] public int* PIndices;
        public float FrameRate;
        public float RootWidth, RootHeight;

        public FrameData Out;
        public SceneScratch S;
        int _group;          // current render group (-1 = root target)
        int _section;        // 0 content, 1 mask, 2 matte (only informative)

        const int MaxDepth = 32;

        public static SceneBuilder Create(in LottieNativeData d, FrameData output, SceneScratch scratch)
        {
            var sb = new SceneBuilder
            {
                P = PropReader.Create(d),
                Comps = (CompData*)d.Comps.GetUnsafeReadOnlyPtr(),
                Layers = (LayerData*)d.Layers.GetUnsafeReadOnlyPtr(),
                Nodes = (ShapeNode*)d.Nodes.GetUnsafeReadOnlyPtr(),
                Masks = (MaskData*)d.Masks.GetUnsafeReadOnlyPtr(),
                DashTable = (DashData*)d.Dashes.GetUnsafeReadOnlyPtr(),
                Images = (ImageAsset*)d.Images.GetUnsafeReadOnlyPtr(),
                Effects = (EffectData*)d.Effects.GetUnsafeReadOnlyPtr(),
                EffectParams = (EffectParam*)d.EffectParams.GetUnsafeReadOnlyPtr(),
                Programs = (ShapeProgram*)d.Programs.GetUnsafeReadOnlyPtr(),
                PGroups = (SPGroup*)d.PGroups.GetUnsafeReadOnlyPtr(),
                PShapes = (SPShape*)d.PShapes.GetUnsafeReadOnlyPtr(),
                PModifiers = (SPModifier*)d.PModifiers.GetUnsafeReadOnlyPtr(),
                PStyles = (SPStyle*)d.PStyles.GetUnsafeReadOnlyPtr(),
                POps = (SPOp*)d.POps.GetUnsafeReadOnlyPtr(),
                PIndices = (int*)d.PIndices.GetUnsafeReadOnlyPtr(),
                FrameRate = d.FrameRate,
                RootWidth = d.Width,
                RootHeight = d.Height,
                Out = output,
                S = scratch,
                _group = -1,
            };
            sb.InitText(d);
            return sb;
        }

        /// <summary>Builds the scene for <paramref name="frame"/> (absolute frame of the root composition).</summary>
        public void Build(float frame, in Affine2 compToDevice, float4 viewport)
        {
            Out.Clear();
            _group = -1;
            float4 rootClip = Intersect(TransformRectBounds(compToDevice, new float4(0, 0, RootWidth, RootHeight)), viewport);
            RenderComposition(0, frame, compToDevice, rootClip, 0, new float2(RootWidth, RootHeight));
            PostProcessGroups();
        }

        // ------------------------------------------------------------------ rect helpers

        public static float4 EmptyRect => new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        public static float4 HugeRect => new float4(-1e9f, -1e9f, 1e9f, 1e9f);

        public static float4 Intersect(float4 a, float4 b) =>
            new float4(math.max(a.x, b.x), math.max(a.y, b.y), math.min(a.z, b.z), math.min(a.w, b.w));

        public static float4 Union(float4 a, float4 b) =>
            new float4(math.min(a.x, b.x), math.min(a.y, b.y), math.max(a.z, b.z), math.max(a.w, b.w));

        public static bool IsEmpty(float4 r) => r.x >= r.z || r.y >= r.w;

        public static float4 TransformRectBounds(in Affine2 m, float4 r)
        {
            float2 p0 = m.Apply(new float2(r.x, r.y));
            float2 p1 = m.Apply(new float2(r.z, r.y));
            float2 p2 = m.Apply(new float2(r.z, r.w));
            float2 p3 = m.Apply(new float2(r.x, r.w));
            float2 mn = math.min(math.min(p0, p1), math.min(p2, p3));
            float2 mx = math.max(math.max(p0, p1), math.max(p2, p3));
            return new float4(mn, mx);
        }

        // ------------------------------------------------------------------ commands & groups

        void Cmd(CmdKind kind, int index) => Out.Cmds.Add(new RenderCmd { Kind = kind, Index = index });

        int BeginGroup(float opacity, byte blend, byte matteMode, float4 clip, byte reason)
        {
            int g = Out.Groups.Length;
            Out.Groups.Add(new GroupData
            {
                Opacity = opacity,
                BlendMode = blend,
                MatteMode = matteMode,
                Parent = _group,
                Clip = clip,
                AuxRect = EmptyRect,
                Reason = reason,
            });
            Cmd(CmdKind.BeginGroup, g);
            _group = g;
            return g;
        }

        void EndGroup(int g)
        {
            Cmd(CmdKind.EndGroup, g);
            _group = Out.Groups[g].Parent;
        }

        // ------------------------------------------------------------------ compositions & layers

        static bool InRange(in LayerData l, float compTime) => l.InPoint <= compTime && l.OutPoint > compTime;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void RenderComposition(int compIndex, float compTime, in Affine2 compToDevice, float4 clip, int depth, float2 compSize)
        {
            if (depth > MaxDepth || IsEmpty(clip)) return;
            CompData comp = Comps[compIndex];
            float2 prevSize = _compSize;
            _compSize = compSize;
            for (int i = comp.LayerCount - 1; i >= 0; i--)
            {
                int li = comp.FirstLayer + i;
                LayerData L = Layers[li];
                if ((L.Flags & (LayerFlags.MatteSource | LayerFlags.Hidden)) != 0) continue;
                if (!InRange(L, compTime)) continue;
                RenderLayer(li, compTime, compToDevice, clip, depth);
            }
            _compSize = prevSize;
        }

        bool IsRenderable(LayerKind k) =>
            k == LayerKind.Shape || k == LayerKind.Precomp || k == LayerKind.Solid || k == LayerKind.Image || k == LayerKind.Text;

        // Not inlined: layers recurse (precomps, mattes, glyph compositions), which would bloat the Burst code.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void RenderLayer(int li, float compTime, in Affine2 compToDevice, float4 clip, int depth, bool setMatteApplied = false)
        {
            LayerData L = Layers[li];
            if (!IsRenderable(L.Kind)) return;
            if (!setMatteApplied && FindSetMatte(L, compTime, out int matteLayer, out byte matteMode))
            {
                // Set Matte (lottie-web SVGMatte3Effect): the whole layer is masked by another layer of its composition.
                if (matteMode == 0) return; // matte channel "off"
                if (matteMode == 255 || depth >= MaxDepth)
                {
                    RenderLayer(li, compTime, compToDevice, clip, depth + 1, true);
                    return;
                }
                int wg = BeginGroup(1f, 0, matteMode, clip, 3);
                Cmd(CmdKind.BeginMatte, wg);
                int prevSection = _section;
                _section = 2;
                if (matteLayer >= 0 && matteLayer != li && InRange(Layers[matteLayer], compTime))
                    RenderLayer(matteLayer, compTime, compToDevice, clip, depth + 1);
                _section = prevSection;
                Cmd(CmdKind.EndMatte, wg);
                RenderLayer(li, compTime, compToDevice, clip, depth + 1, true);
                EndGroup(wg);
                return;
            }
            float opacity = P.Scalar(L.Transform.Opacity, compTime, 100f) * 0.01f;
            // Transform effects (lottie-web localTransforms): content -> effect matrix -> layer transform.
            bool hasTransformEffect = TransformEffects(L, compTime, false, out Affine2 effectMatrix, out float effectOpacity);
            // After Effects applies the effects of shape / text layers after the layer transform (composition space).
            bool hasCompTransform = TransformEffects(L, compTime, true, out Affine2 compEffectMatrix, out float compEffectOpacity);
            opacity *= effectOpacity * compEffectOpacity;
            if (opacity <= 0) return; // lottie-web hideOnTransparent
            Affine2 world = LayerWorld(li, compTime);
            if (hasCompTransform) world = world.Then(compEffectMatrix);
            world = world.Then(compToDevice);
            if (hasTransformEffect) world = effectMatrix.Then(world);

            // 3D layers seen through a camera: an affine view keeps the vector path; perspective renders the layer in
            // a raster space that is warped onto the target.
            bool projected = false;
            float3x3 projection = float3x3.identity;
            float4 rasterRect = EmptyRect;
            if (CameraHomography(li, compTime, compToDevice, effectMatrix, hasTransformEffect, out float3x3 h))
            {
                float4 pbox = PerspectiveBox(L);
                if (IsAffine(h, pbox.zw - pbox.xy, out Affine2 affine))
                {
                    // Seen without perspective: keep lottie-web's matrix when it is the same view (flat 3D layers at
                    // the focal plane of the default camera), so the result stays identical to the 2D path.
                    if (!Same(affine, world)) world = affine;
                }
                else
                {
                    if (!RasterSpace(h, pbox, out Affine2 layerToRaster, out rasterRect)) return;
                    projected = true;
                    projection = math.mul(h, math.inverse(ToMatrix3(layerToRaster)));
                    world = hasTransformEffect ? effectMatrix.Then(layerToRaster) : layerToRaster;
                }
            }

            int strokeEffect = FindStrokeEffect(L);
            int strokeStyle = strokeEffect >= 0 ? StrokeEffectPaintStyle(Effects[strokeEffect], compTime) : 0;
            // lottie-web's Stroke effect removes the layer masks when painting on (or instead of) the layer.
            bool hasMask = HasActiveMasks(L) && strokeStyle != 1 && strokeStyle != 2;
            bool hasMatte = L.MatteMode != 0 && L.MatteSource >= 0;
            bool isPrecomp = L.Kind == LayerKind.Precomp && L.RefComp >= 0;
            bool hasEffects = HasRenderableEffects(L);
            bool precompNeedsIsolation = isPrecomp && CompHasBlendModes(L.RefComp);
            bool rotatedClip = isPrecomp && L.Width > 0 && L.Height > 0 && !world.IsAxisAligned;

            bool needsGroup = hasMask || hasMatte || L.BlendMode != 0 || opacity < 1 || hasEffects ||
                              precompNeedsIsolation || rotatedClip || strokeStyle != 0 || projected;
            bool nonLocal = hasEffects && HasNonLocalEffects(L);
            if (nonLocal)
            {
                // Content anywhere in the layer box can be moved into view (tiles, spheres, corner pins...):
                // render the source beyond the visible area (bounded to a few view sizes).
                float4 box = TransformRectBounds(EffectSpace(L, world, compToDevice), new float4(0, 0, LayerBoxSize(L)));
                float2 grow = clip.zw - clip.xy;
                clip = Union(clip, Intersect(box, new float4(clip.xy - grow, clip.zw + grow)));
            }
            int g = -1;
            float itemOpacity = 1f;
            if (needsGroup)
            {
                g = BeginGroup(opacity, L.BlendMode, hasMatte ? L.MatteMode : (byte)0, clip, 1);
                if (hasEffects && !projected)
                {
                    var gd = Out.Groups[g];
                    gd.EffectFirst = Out.Effects.Length;
                    EmitEffects(L, compTime, world, compToDevice);
                    gd.EffectCount = Out.Effects.Length - gd.EffectFirst;
                    gd.NonLocal = (byte)(nonLocal ? 1 : 0);
                    Out.Groups[g] = gd;
                    int mapLayer = DisplacementSource(L, compTime);
                    if (mapLayer >= 0)
                    {
                        // The displacement map reads another layer of the composition (rendered in its place), anywhere
                        // in that layer's box.
                        var gd2 = Out.Groups[g];
                        float4 mapBox = TransformRectBounds(compToDevice, new float4(0, 0, LayerBoxSize(Layers[mapLayer])));
                        float2 grow = clip.zw - clip.xy;
                        gd2.AuxRect = Intersect(mapBox, new float4(clip.xy - grow, clip.zw + grow));
                        Out.Groups[g] = gd2;
                        Cmd(CmdKind.BeginAux, g);
                        int prev = _section;
                        _section = 3;
                        if (InRange(Layers[mapLayer], compTime) && depth < MaxDepth)
                            RenderLayer(mapLayer, compTime, compToDevice, gd2.AuxRect, depth + 1);
                        _section = prev;
                        Cmd(CmdKind.EndAux, g);
                    }
                }
            }
            else itemOpacity = opacity;

            if (hasMatte)
            {
                Cmd(CmdKind.BeginMatte, g);
                int prev = _section;
                _section = 2;
                LayerData M = Layers[L.MatteSource];
                if ((M.Flags & LayerFlags.Hidden) == 0 && InRange(M, compTime))
                    RenderLayer(L.MatteSource, compTime, compToDevice, clip, depth + 1);
                _section = prev;
                Cmd(CmdKind.EndMatte, g);
            }

            // Perspective: masks, effects and content are rendered in the layer's raster space (a projected group
            // inside the layer group, which keeps opacity, blending and the track matte in the target space).
            int pg = -1, contentGroup = g;
            if (projected)
            {
                pg = BeginGroup(1f, 0, 0, rasterRect, 5);
                var pd = Out.Groups[pg];
                pd.Projected = 1;
                pd.Projection = projection;
                if (hasEffects)
                {
                    pd.EffectFirst = Out.Effects.Length;
                    EmitEffects(L, compTime, world, world);
                    pd.EffectCount = Out.Effects.Length - pd.EffectFirst;
                }
                Out.Groups[pg] = pd;
                clip = rasterRect;
                contentGroup = pg;
                rotatedClip = false;
            }

            if (hasMask || rotatedClip)
            {
                Cmd(CmdKind.BeginMask, contentGroup);
                int prev = _section;
                _section = 1;
                EmitMasks(li, compTime, world, clip, contentGroup, rotatedClip);
                _section = prev;
                Cmd(CmdKind.EndMask, contentGroup);
            }

            int revealGroup = -1;
            if (strokeStyle == 3)
            {
                // "Reveal original image": the strokes are an alpha matte of the layer content.
                revealGroup = BeginGroup(1f, 0, 1, clip, 4);
                Cmd(CmdKind.BeginMatte, revealGroup);
                int prev = _section;
                _section = 2;
                EmitStrokeEffect(L, Effects[strokeEffect], compTime, world, clip, 1f, true);
                _section = prev;
                Cmd(CmdKind.EndMatte, revealGroup);
            }

            if (strokeStyle != 2)
                RenderLayerContent(L, compTime, world, clip, itemOpacity, needsGroup, isPrecomp, rotatedClip, depth);

            if (revealGroup >= 0) EndGroup(revealGroup);
            if (strokeStyle == 1 || strokeStyle == 2) EmitStrokeEffect(L, Effects[strokeEffect], compTime, world, clip, itemOpacity, false);

            if (pg >= 0) EndGroup(pg);
            if (g >= 0) EndGroup(g);
        }

        void RenderLayerContent(in LayerData L, float compTime, in Affine2 world, float4 clip, float itemOpacity, bool needsGroup,
            bool isPrecomp, bool rotatedClip, int depth)
        {
            switch (L.Kind)
            {
                case LayerKind.Shape:
                    if (L.ShapeProgram >= 0) RenderShapeProgram(L.ShapeProgram, compTime, world, clip, itemOpacity);
                    break;
                case LayerKind.Solid:
                    EmitSolid(L, world, clip, itemOpacity);
                    break;
                case LayerKind.Image:
                    EmitImage(L, world, clip, itemOpacity);
                    break;
                case LayerKind.Text:
                    RenderText(L, compTime, world, clip, itemOpacity, depth);
                    break;
                case LayerKind.Precomp:
                {
                    if (!isPrecomp) break;
                    float childTime;
                    if (L.TimeRemap >= 0)
                    {
                        childTime = P.Scalar(L.TimeRemap, compTime, 0) * FrameRate;
                        if (childTime == L.OutPoint) childTime = L.OutPoint - 1;
                    }
                    else childTime = (compTime - L.StartTime) / L.Stretch;
                    float4 childClip = clip;
                    if (L.Width > 0 && L.Height > 0 && !rotatedClip)
                        childClip = Intersect(clip, TransformRectBounds(world, new float4(0, 0, L.Width, L.Height)));
                    if (needsGroup && itemOpacity != 1f) { /* unreachable */ }
                    // The camera of a precomposition sees the precomp layer's size.
                    float2 size = L.Width > 0 && L.Height > 0 ? new float2(L.Width, L.Height) : new float2(Comps[L.RefComp].Width, Comps[L.RefComp].Height);
                    if (!needsGroup && itemOpacity < 1f)
                    {
                        // Opacity without isolation would be wrong for overlapping children: isolate.
                        int gg = BeginGroup(itemOpacity, 0, 0, clip, 2);
                        RenderComposition(L.RefComp, childTime, world, childClip, depth + 1, size);
                        EndGroup(gg);
                    }
                    else RenderComposition(L.RefComp, childTime, world, childClip, depth + 1, size);
                    break;
                }
            }
        }

        bool HasActiveMasks(in LayerData L)
        {
            for (int i = 0; i < L.MaskCount; i++)
                if (Masks[L.MaskFirst + i].Mode != MaskMode.None) return true;
            return false;
        }

        bool CompHasBlendModes(int compIndex)
        {
            CompData c = Comps[compIndex];
            for (int i = 0; i < c.LayerCount; i++)
                if (Layers[c.FirstLayer + i].BlendMode != 0) return true;
            return false;
        }

        // ------------------------------------------------------------------ transforms

        /// <summary>Layer matrix including parents (lottie-web: own × parent × grandparent, 4x4) projected to 2D.</summary>
        internal Affine2 LayerWorld(int li, float compTime)
        {
            float4x4 m = LayerLocal(li, compTime);
            int p = Layers[li].Parent;
            int guard = 0;
            while (p >= 0 && guard++ < 64)
            {
                m = math.mul(m, LayerLocal(p, compTime));
                p = Layers[p].Parent;
            }
            return LMat4.To2D(m);
        }

        float4x4 LayerLocal(int li, float t)
        {
            LayerData L = Layers[li];
            if ((L.Flags & LayerFlags.HasTransform) == 0) return float4x4.identity;
            TransformDesc tr = L.Transform;
            float4x4 m = float4x4.identity;
            float3 a = P.Vec3(tr.Anchor, t, float3.zero);
            m = LMat4.Translate(m, -a.x, -a.y, a.z);
            float3 s = P.Vec3(tr.Scale, t, new float3(100, 100, 100)) * 0.01f;
            m = LMat4.Scale(m, s.x, s.y, s.z);
            if (tr.Skew >= 0)
                m = LMat4.SkewFromAxis(m, -math.radians(P.Scalar(tr.Skew, t, 0)), math.radians(P.Scalar(tr.SkewAxis, t, 0)));
            if (tr.Has3D != 0)
            {
                float rz = math.radians(P.Scalar(tr.RotZ, t, 0));
                float ry = math.radians(P.Scalar(tr.RotY, t, 0));
                float rx = math.radians(P.Scalar(tr.RotX, t, 0));
                float3 or = math.radians(P.Vec3(tr.Orientation, t, float3.zero));
                m = LMat4.RotateZ(m, -rz);
                m = LMat4.RotateY(m, ry);
                m = LMat4.RotateX(m, rx);
                m = LMat4.RotateZ(m, -or.z);
                m = LMat4.RotateY(m, or.y);
                m = LMat4.RotateX(m, or.x);
            }
            else m = LMat4.Rotate(m, -math.radians(P.Scalar(tr.Rotation, t, 0)));

            if ((L.Flags & LayerFlags.AutoOrient) != 0)
            {
                float2 v1, v2;
                AutoOrientVectors(tr, t, out v1, out v2);
                m = LMat4.Rotate(m, -math.atan2(v1.y - v2.y, v1.x - v2.x));
            }

            if (tr.Split != 0)
            {
                float px = P.Scalar(tr.PosX, t, 0), py = P.Scalar(tr.PosY, t, 0), pz = P.Scalar(tr.PosZ, t, 0);
                m = LMat4.Translate(m, px, py, -pz);
            }
            else
            {
                float3 p = P.Vec3(tr.Position, t, float3.zero);
                m = LMat4.Translate(m, p.x, p.y, -p.z);
            }
            return m;
        }

        void AutoOrientVectors(in TransformDesc tr, float t, out float2 v1, out float2 v2)
        {
            if (tr.Split == 0 && P.IsAnimated(tr.Position))
            {
                PropDesc pd = P.Props[tr.Position];
                float first = P.Keys[pd.Offset].Time;
                float last = P.Keys[pd.Offset + pd.KeyCount - 1].Time;
                if (t <= first) { v1 = P.Vec2(tr.Position, first + 0.01f, 0); v2 = P.Vec2(tr.Position, first, 0); }
                else if (t >= last) { v1 = P.Vec2(tr.Position, last, 0); v2 = P.Vec2(tr.Position, last - 0.05f, 0); }
                else { v1 = P.Vec2(tr.Position, t, 0); v2 = P.Vec2(tr.Position, t - 0.01f, 0); }
                return;
            }
            if (tr.Split != 0 && P.IsAnimated(tr.PosX) && P.IsAnimated(tr.PosY))
            {
                PropDesc px = P.Props[tr.PosX];
                PropDesc py = P.Props[tr.PosY];
                float firstX = P.Keys[px.Offset].Time, lastX = P.Keys[px.Offset + px.KeyCount - 1].Time;
                float firstY = P.Keys[py.Offset].Time, lastY = P.Keys[py.Offset + py.KeyCount - 1].Time;
                if (t <= firstX)
                {
                    v1 = new float2(P.Scalar(tr.PosX, firstX + 0.01f, 0), P.Scalar(tr.PosY, firstY + 0.01f, 0));
                    v2 = new float2(P.Scalar(tr.PosX, firstX, 0), P.Scalar(tr.PosY, firstY, 0));
                }
                else if (t >= lastX)
                {
                    v1 = new float2(P.Scalar(tr.PosX, lastX, 0), P.Scalar(tr.PosY, lastY, 0));
                    v2 = new float2(P.Scalar(tr.PosX, lastX - 0.01f, 0), P.Scalar(tr.PosY, lastY - 0.01f, 0));
                }
                else
                {
                    v1 = new float2(P.Scalar(tr.PosX, t, 0), P.Scalar(tr.PosY, t, 0));
                    v2 = new float2(P.Scalar(tr.PosX, t - 0.01f, 0), P.Scalar(tr.PosY, t - 0.01f, 0));
                }
                return;
            }
            v1 = v2 = float2.zero;
        }

        // ------------------------------------------------------------------ masks

        void EmitMasks(int li, float t, in Affine2 world, float4 clip, int g, bool precompClip)
        {
            LayerData L = Layers[li];
            bool first = true;
            for (int i = 0; i < L.MaskCount; i++)
            {
                MaskData m = Masks[L.MaskFirst + i];
                if (m.Mode == MaskMode.None) continue;
                MaskOp op = m.Mode switch
                {
                    MaskMode.Subtract => MaskOp.Subtract,
                    MaskMode.Intersect => MaskOp.Intersect,
                    MaskMode.Lighten => MaskOp.Lighten,
                    MaskMode.Darken => MaskOp.Darken,
                    MaskMode.Difference => MaskOp.Difference,
                    _ => MaskOp.Add,
                };
                if (first)
                {
                    // After Effects: a leading subtract / intersect / darken mask works against the full layer.
                    if (op == MaskOp.Subtract || op == MaskOp.Intersect || op == MaskOp.Darken)
                    {
                        var gd = Out.Groups[g];
                        gd.MaskInitOne = 1;
                        Out.Groups[g] = gd;
                    }
                    var g2 = Out.Groups[g];
                    g2.HasMask = 1;
                    Out.Groups[g] = g2;
                    first = false;
                }
                float opacity = P.Scalar(m.Opacity, t, 100f) * 0.01f;
                float expansion = P.Scalar(m.Expansion, t, 0);
                int cFirst = Out.Contours.Length;
                bool closed = AppendPathProp(m.Path, t, world);
                if (!closed)
                {
                    // lottie-web renders open mask paths as an empty path.
                    Out.Contours.ResizeUninitialized(cFirst);
                }
                else if (m.Inverted != 0)
                {
                    // lottie-web: solid comp-sized rect (layer space) + mask path, nonzero.
                    AppendRectContour(world, 0, 0, RootWidth, RootHeight, true);
                }
                int cCount = Out.Contours.Length - cFirst;
                bool grow = expansion > 0 && cCount > 0;
                int paint = AddSolidPaint(new float4(1, 1, 1, 1), opacity, Affine2.Identity);
                var fillItem = new DrawItem
                {
                    Kind = ItemKind.Fill,
                    FillRule = 0,
                    Mask = op,
                    MaskMerge = (byte)(grow ? 1 : 0),
                    ContourFirst = cFirst,
                    ContourCount = cCount,
                    ToDevice = Affine2.Identity,
                    Paint = paint,
                    Clip = clip,
                    Group = g,
                };
                if (expansion < 0 && cCount > 0)
                {
                    // feMorphology erode: radius in layer space, mapped by the layer transform's axis scales.
                    fillItem.Erode = new float2(math.length(new float2(world.a, world.b)), math.length(new float2(world.c, world.d))) * -expansion;
                }
                AddItem(fillItem);

                if (grow)
                {
                    // lottie-web: stroke-width = 2x with the mask colour, stroke-opacity 1 (miter join, butt cap).
                    // The stroke is merged (union) with the fill before the mask op is applied.
                    int sFirst = Out.Contours.Length;
                    AppendPathProp(m.Path, t, Affine2.Identity);
                    if (m.Inverted != 0) AppendRectContour(Affine2.Identity, 0, 0, RootWidth, RootHeight, true);
                    int sCount = Out.Contours.Length - sFirst;
                    int sp = AddSolidPaint(new float4(1, 1, 1, 1), 1f, Affine2.Identity);
                    AddItem(new DrawItem
                    {
                        Kind = ItemKind.Stroke,
                        Mask = op,
                        Cap = 1,
                        Join = 1,
                        MiterLimit = 4,
                        StrokeWidth = expansion * 2,
                        ContourFirst = sFirst,
                        ContourCount = sCount,
                        ToDevice = world,
                        Paint = sp,
                        Clip = clip,
                        Group = g,
                    });
                }
            }

            if (precompClip)
            {
                // Rotated precomp: clip to its bounds through the mask target.
                bool hadMask = Out.Groups[g].HasMask != 0;
                var gd = Out.Groups[g];
                gd.HasMask = 1;
                Out.Groups[g] = gd;
                int cFirst = Out.Contours.Length;
                AppendRectContour(world, 0, 0, L.Width, L.Height, false);
                int paint = AddSolidPaint(new float4(1, 1, 1, 1), 1f, Affine2.Identity);
                AddItem(new DrawItem
                {
                    Kind = ItemKind.Fill,
                    Mask = hadMask ? MaskOp.Intersect : MaskOp.Add,
                    ContourFirst = cFirst,
                    ContourCount = Out.Contours.Length - cFirst,
                    ToDevice = Affine2.Identity,
                    Paint = paint,
                    Clip = clip,
                    Group = g,
                });
            }
        }

        // ------------------------------------------------------------------ solids & images

        void EmitSolid(in LayerData L, in Affine2 world, float4 clip, float opacity)
        {
            if (L.Width <= 0 || L.Height <= 0) return;
            int cFirst = Out.Contours.Length;
            AppendRectContour(world, 0, 0, L.Width, L.Height, false);
            int paint = AddSolidPaint(L.SolidColor, opacity, Affine2.Identity);
            AddDraw(new DrawItem
            {
                Kind = ItemKind.Fill,
                ContourFirst = cFirst,
                ContourCount = 1,
                ToDevice = Affine2.Identity,
                Paint = paint,
                Clip = clip,
                Group = _group,
            });
        }

        void EmitImage(in LayerData L, in Affine2 world, float4 clip, float opacity)
        {
            if (L.Image < 0) return;
            ImageAsset img = Images[L.Image];
            if (img.Width <= 0 || img.Height <= 0) return;
            int cFirst = Out.Contours.Length;
            AppendRectContour(world, 0, 0, img.Width, img.Height, false);
            int paint = Out.Paints.Length;
            Out.Paints.Add(new PaintData
            {
                Kind = PaintKind.Image,
                Color = new float4(1, 1, 1, 1),
                Opacity = opacity,
                DeviceToPaint = world.Inverse(),
                Image = L.Image,
                ImageSize = new float2(img.Width, img.Height),
            });
            AddDraw(new DrawItem
            {
                Kind = ItemKind.Fill,
                ContourFirst = cFirst,
                ContourCount = 1,
                ToDevice = Affine2.Identity,
                Paint = paint,
                Clip = clip,
                Group = _group,
            });
        }

        // ------------------------------------------------------------------ item helpers

        int AddSolidPaint(float4 color, float opacity, in Affine2 deviceToPaint)
        {
            Out.Paints.Add(new PaintData
            {
                Kind = PaintKind.Solid,
                Color = color,
                Opacity = opacity,
                DeviceToPaint = deviceToPaint,
            });
            return Out.Paints.Length - 1;
        }

        /// <summary>Adds an item and a Draw command.</summary>
        void AddDraw(DrawItem item)
        {
            item.Bounds = EmptyRect;
            int idx = Out.Items.Length;
            Out.Items.Add(item);
            Cmd(CmdKind.Draw, idx);
        }

        void AddItem(DrawItem item) => AddDraw(item);

        void AppendRectContour(in Affine2 m, float x0, float y0, float x1, float y1, bool clockwiseFromOrigin)
        {
            int first = Out.Points.Length;
            // lottie-web solid path: M0,0 h w v h h-w v-h (clockwise in y-down space).
            AddVertex(m.Apply(new float2(x0, y0)));
            AddVertex(m.Apply(new float2(x1, y0)));
            AddVertex(m.Apply(new float2(x1, y1)));
            AddVertex(m.Apply(new float2(x0, y1)));
            Out.Contours.Add(new Contour { First = first, Count = 4, Closed = 1 });
        }

        void AddVertex(float2 p)
        {
            Out.Points.Add(p);
            Out.Points.Add(p);
            Out.Points.Add(p);
        }

        /// <summary>Evaluates a path property, transforms it and appends one contour. Returns the closed flag.</summary>
        bool AppendPathProp(int prop, float t, in Affine2 m)
        {
            if (prop < 0) return false;
            PropDesc pd = P.Props[prop];
            int dim = pd.Dim;
            int count = dim / 6;
            if (count <= 0) return false;
            int tempStart = S.Temp.Length;
            S.Temp.ResizeUninitialized(tempStart + count * 3);
            float* v = (float*)S.Temp.GetUnsafePtr() + tempStart * 2;
            P.Eval(prop, t, v, dim);
            int first = Out.Points.Length;
            for (int k = 0; k < count; k++)
            {
                float2 pv = new float2(v[k * 6 + 0], v[k * 6 + 1]);
                float2 pi = new float2(v[k * 6 + 2], v[k * 6 + 3]);
                float2 po = new float2(v[k * 6 + 4], v[k * 6 + 5]);
                Out.Points.Add(m.Apply(pv));
                Out.Points.Add(m.Apply(pi));
                Out.Points.Add(m.Apply(po));
            }
            S.Temp.ResizeUninitialized(tempStart);
            bool closed = (pd.Flags & PropFlags.Closed) != 0;
            Out.Contours.Add(new Contour { First = first, Count = count, Closed = (byte)(closed ? 1 : 0) });
            return closed;
        }

        // ------------------------------------------------------------------ group post-processing

        /// <summary>
        /// Collapses groups that don't need an offscreen target: a group with only an opacity and a
        /// single direct child (or opacity 1 and no other feature) is folded into its child.
        /// </summary>
        void PostProcessGroups()
        {
            int gc = Out.Groups.Length;
            if (gc == 0) return;
            // Count direct children per group.
            var childCount = new NativeArray<int>(gc, Allocator.Temp);
            var lastChildItem = new NativeArray<int>(gc, Allocator.Temp);
            var lastChildGroup = new NativeArray<int>(gc, Allocator.Temp);
            for (int i = 0; i < gc; i++) { lastChildItem[i] = -1; lastChildGroup[i] = -1; }
            int cur = -1;
            int sectionDepth = 0; // inside mask / matte of the current group
            var stack = new NativeList<int>(16, Allocator.Temp);
            for (int c = 0; c < Out.Cmds.Length; c++)
            {
                var cmd = Out.Cmds[c];
                switch (cmd.Kind)
                {
                    case CmdKind.BeginGroup:
                        if (cur >= 0 && sectionDepth == 0) { childCount[cur]++; lastChildGroup[cur] = cmd.Index; lastChildItem[cur] = -1; }
                        stack.Add(sectionDepth);
                        sectionDepth = 0;
                        cur = cmd.Index;
                        break;
                    case CmdKind.EndGroup:
                        cur = Out.Groups[cmd.Index].Parent;
                        sectionDepth = stack[stack.Length - 1];
                        stack.RemoveAt(stack.Length - 1);
                        break;
                    case CmdKind.BeginMask:
                    case CmdKind.BeginMatte:
                    case CmdKind.BeginAux:
                        sectionDepth++;
                        break;
                    case CmdKind.EndMask:
                    case CmdKind.EndMatte:
                    case CmdKind.EndAux:
                        sectionDepth--;
                        break;
                    case CmdKind.Draw:
                        if (cur >= 0 && sectionDepth == 0) { childCount[cur]++; lastChildItem[cur] = cmd.Index; lastChildGroup[cur] = -1; }
                        break;
                }
            }
            // Collapse bottom-up (children have higher indices than parents).
            for (int g = gc - 1; g >= 0; g--)
            {
                var gd = Out.Groups[g];
                if (gd.HasMask != 0 || gd.MatteMode != 0 || gd.BlendMode != 0 || gd.EffectCount > 0 || gd.Projected != 0) continue;
                int n = childCount[g];
                if (n == 0) { gd.Collapsed = 1; Out.Groups[g] = gd; continue; }
                if (gd.Opacity >= 1f) { gd.Collapsed = 1; Out.Groups[g] = gd; continue; }
                if (n != 1) continue;
                if (lastChildItem[g] >= 0)
                {
                    var item = Out.Items[lastChildItem[g]];
                    var paint = Out.Paints[item.Paint];
                    paint.Opacity *= gd.Opacity;
                    Out.Paints[item.Paint] = paint;
                    gd.Collapsed = 1;
                    Out.Groups[g] = gd;
                }
                else if (lastChildGroup[g] >= 0)
                {
                    var child = Out.Groups[lastChildGroup[g]];
                    if (child.Collapsed != 0) continue;
                    child.Opacity *= gd.Opacity;
                    Out.Groups[lastChildGroup[g]] = child;
                    gd.Collapsed = 1;
                    Out.Groups[g] = gd;
                }
            }
            childCount.Dispose();
            lastChildItem.Dispose();
            lastChildGroup.Dispose();
            stack.Dispose();
        }
    }
}
