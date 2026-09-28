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
            RenderComposition(0, frame, compToDevice, rootClip, 0);
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

        void RenderComposition(int compIndex, float compTime, in Affine2 compToDevice, float4 clip, int depth)
        {
            if (depth > MaxDepth || IsEmpty(clip)) return;
            CompData comp = Comps[compIndex];
            for (int i = comp.LayerCount - 1; i >= 0; i--)
            {
                int li = comp.FirstLayer + i;
                LayerData L = Layers[li];
                if ((L.Flags & (LayerFlags.MatteSource | LayerFlags.Hidden)) != 0) continue;
                if (!InRange(L, compTime)) continue;
                RenderLayer(li, compTime, compToDevice, clip, depth);
            }
        }

        bool IsRenderable(LayerKind k) =>
            k == LayerKind.Shape || k == LayerKind.Precomp || k == LayerKind.Solid || k == LayerKind.Image || k == LayerKind.Text;

        void RenderLayer(int li, float compTime, in Affine2 compToDevice, float4 clip, int depth)
        {
            LayerData L = Layers[li];
            if (!IsRenderable(L.Kind)) return;
            float opacity = P.Scalar(L.Transform.Opacity, compTime, 100f) * 0.01f;
            // Transform effects (lottie-web localTransforms): content -> effect matrix -> layer transform.
            bool hasTransformEffect = TransformEffects(L, compTime, out Affine2 effectMatrix, out float effectOpacity);
            opacity *= effectOpacity;
            if (opacity <= 0) return; // lottie-web hideOnTransparent
            Affine2 world = LayerWorld(li, compTime).Then(compToDevice);
            if (hasTransformEffect) world = effectMatrix.Then(world);

            bool hasMask = HasActiveMasks(L);
            bool hasMatte = L.MatteMode != 0 && L.MatteSource >= 0;
            bool isPrecomp = L.Kind == LayerKind.Precomp && L.RefComp >= 0;
            bool hasEffects = HasRenderableEffects(L);
            bool precompNeedsIsolation = isPrecomp && CompHasBlendModes(L.RefComp);
            bool rotatedClip = isPrecomp && L.Width > 0 && L.Height > 0 && !world.IsAxisAligned;

            bool needsGroup = hasMask || hasMatte || L.BlendMode != 0 || opacity < 1 || hasEffects ||
                              precompNeedsIsolation || rotatedClip;
            int g = -1;
            float itemOpacity = 1f;
            if (needsGroup)
            {
                g = BeginGroup(opacity, L.BlendMode, hasMatte ? L.MatteMode : (byte)0, clip, 1);
                if (hasEffects)
                {
                    var gd = Out.Groups[g];
                    gd.EffectFirst = Out.Effects.Length;
                    EmitEffects(L, compTime, world);
                    gd.EffectCount = Out.Effects.Length - gd.EffectFirst;
                    Out.Groups[g] = gd;
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

            if (hasMask || rotatedClip)
            {
                Cmd(CmdKind.BeginMask, g);
                int prev = _section;
                _section = 1;
                EmitMasks(li, compTime, world, clip, g, rotatedClip);
                _section = prev;
                Cmd(CmdKind.EndMask, g);
            }

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
                    RenderText(L, compTime, world, clip, itemOpacity);
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
                    if (!needsGroup && itemOpacity < 1f)
                    {
                        // Opacity without isolation would be wrong for overlapping children: isolate.
                        int gg = BeginGroup(itemOpacity, 0, 0, clip, 2);
                        RenderComposition(L.RefComp, childTime, world, childClip, depth + 1);
                        EndGroup(gg);
                    }
                    else RenderComposition(L.RefComp, childTime, world, childClip, depth + 1);
                    break;
                }
            }

            if (g >= 0) EndGroup(g);
        }

        bool HasActiveMasks(in LayerData L)
        {
            for (int i = 0; i < L.MaskCount; i++)
                if (Masks[L.MaskFirst + i].Mode != MaskMode.None) return true;
            return false;
        }

        bool HasRenderableEffects(in LayerData L)
        {
            for (int i = 0; i < L.EffectCount; i++)
            {
                var e = Effects[L.EffectFirst + i];
                if (e.Enabled == 0) continue;
                switch (e.Kind)
                {
                    case EffectKind.GaussianBlur:
                    case EffectKind.DropShadow:
                    case EffectKind.Fill:
                    case EffectKind.Tint:
                    case EffectKind.Tritone:
                    case EffectKind.ProLevels:
                        return true;
                }
            }
            return false;
        }

        float EffectScalar(in EffectData e, int param, float t, float def) =>
            param < e.ParamCount ? P.Scalar(EffectParams[e.ParamFirst + param].Prop, t, def) : def;

        float4 EffectColor(in EffectData e, int param, float t, float4 def) =>
            param < e.ParamCount ? P.Vec4(EffectParams[e.ParamFirst + param].Prop, t, def) : def;

        float2 EffectPoint(in EffectData e, int param, float t, float2 def) =>
            param < e.ParamCount ? P.Vec2(EffectParams[e.ParamFirst + param].Prop, t, def) : def;

        /// <summary>lottie-web TransformEffect: combined matrix and opacity of the layer's Transform effects.</summary>
        bool TransformEffects(in LayerData L, float t, out Affine2 matrix, out float opacity)
        {
            matrix = Affine2.Identity;
            opacity = 1f;
            bool any = false;
            for (int i = 0; i < L.EffectCount; i++)
            {
                var e = Effects[L.EffectFirst + i];
                if (e.Enabled == 0 || e.Kind != EffectKind.Transform) continue;
                float2 anchor = EffectPoint(e, 0, t, float2.zero);
                float2 position = EffectPoint(e, 1, t, float2.zero);
                bool uniform = EffectScalar(e, 2, t, 1) == 1;
                float scaleH = EffectScalar(e, 3, t, 100);
                float scaleW = uniform ? scaleH : EffectScalar(e, 4, t, 100);
                float skew = EffectScalar(e, 5, t, 0);
                float skewAxis = EffectScalar(e, 6, t, 0);
                float rotation = EffectScalar(e, 7, t, 0);
                Affine2 m = Affine2.Translate(-anchor.x, -anchor.y);
                m = m.Then(Affine2.Scale(scaleW * 0.01f, scaleH * 0.01f));
                if (rotation != 0) m = m.Then(Affine2.Rotate(-math.radians(rotation)));
                if (skew != 0) m = m.Then(Affine2.SkewFromAxis(-math.radians(skew), math.radians(skewAxis + 90)));
                m = m.Then(Affine2.Translate(position.x, position.y));
                // lottie-web: localTransforms[0] * localTransforms[1] * ... * layer matrix.
                matrix = any ? matrix.Then(m) : m;
                opacity *= EffectScalar(e, 8, t, 100) * 0.01f;
                any = true;
            }
            return any;
        }

        /// <summary>Resolves the layer's pixel effects into <see cref="FrameData.Effects"/> (device units).</summary>
        void EmitEffects(in LayerData L, float t, in Affine2 world)
        {
            float sx = math.length(new float2(world.a, world.b));
            float sy = math.length(new float2(world.c, world.d));
            for (int i = 0; i < L.EffectCount; i++)
            {
                var e = Effects[L.EffectFirst + i];
                if (e.Enabled == 0) continue;
                var op = new EffectOp { TableR = -1, TableG = -1, TableB = -1, TableA = -1, TableRGB = -1 };
                switch (e.Kind)
                {
                    case EffectKind.Fill:
                    {
                        op.Kind = EffectOpKind.Fill;
                        float4 c = EffectColor(e, 2, t, new float4(1, 0, 0, 1));
                        op.A = new float4(c.xyz, EffectScalar(e, 6, t, 1));
                        break;
                    }
                    case EffectKind.Tint:
                        op.Kind = EffectOpKind.Tint;
                        op.A = EffectColor(e, 0, t, new float4(0, 0, 0, 1));
                        op.B = EffectColor(e, 1, t, new float4(1, 1, 1, 1));
                        op.X = EffectScalar(e, 2, t, 100) * 0.01f;
                        break;
                    case EffectKind.Tritone:
                        op.Kind = EffectOpKind.Tritone;
                        op.C = EffectColor(e, 0, t, new float4(1, 1, 1, 1));   // highlights
                        op.B = EffectColor(e, 1, t, new float4(0.5f, 0.5f, 0.5f, 1)); // midtones
                        op.A = EffectColor(e, 2, t, new float4(0, 0, 0, 1));   // shadows
                        op.X = EffectScalar(e, 3, t, 0) * 0.01f;
                        break;
                    case EffectKind.ProLevels:
                        op.Kind = EffectOpKind.Levels;
                        op.TableR = LevelsTable(e, 10, t);
                        op.TableG = LevelsTable(e, 17, t);
                        op.TableB = LevelsTable(e, 24, t);
                        op.TableA = LevelsTable(e, 31, t);
                        op.TableRGB = LevelsTable(e, 3, t);
                        if (op.TableR < 0 && op.TableG < 0 && op.TableB < 0 && op.TableA < 0 && op.TableRGB < 0) continue;
                        break;
                    case EffectKind.GaussianBlur:
                    {
                        op.Kind = EffectOpKind.GaussianBlur;
                        float sigma = EffectScalar(e, 0, t, 0) * 0.3f; // lottie-web kBlurrinessToSigma
                        int dims = (int)EffectScalar(e, 1, t, 1);
                        op.Z = dims == 3 ? 0 : sigma * sx;
                        op.W = dims == 2 ? 0 : sigma * sy;
                        op.Flags = (byte)(EffectScalar(e, 2, t, 0) == 1 ? 1 : 0);
                        if (!(op.Z > 0.01f) && !(op.W > 0.01f)) continue;
                        break;
                    }
                    case EffectKind.DropShadow:
                    {
                        op.Kind = EffectOpKind.DropShadow;
                        float4 c = EffectColor(e, 0, t, new float4(0, 0, 0, 1));
                        // lottie-web: flood colour rounded to bytes, opacity in 0..255.
                        c.xyz = math.round(math.saturate(c.xyz) * 255f) / 255f;
                        op.A = new float4(c.xyz, math.saturate(EffectScalar(e, 1, t, 127.5f) / 255f));
                        float distance = EffectScalar(e, 3, t, 5);
                        float angle = math.radians(EffectScalar(e, 2, t, 135) - 90);
                        float2 offset = world.ApplyVector(new float2(distance * math.cos(angle), distance * math.sin(angle)));
                        op.X = offset.x;
                        op.Y = offset.y;
                        float sigma = EffectScalar(e, 4, t, 0) / 4f;
                        op.Z = sigma * sx;
                        op.W = sigma * sy;
                        op.Flags = (byte)(EffectScalar(e, 5, t, 0) == 1 ? 1 : 0);
                        break;
                    }
                    default:
                        continue;
                }
                Out.Effects.Add(op);
            }
        }

        /// <summary>lottie-web SVGProLevelsFilter.getTableValue: 256-entry table, or -1 for the identity mapping.</summary>
        int LevelsTable(in EffectData e, int first, float t)
        {
            if (first + 4 >= e.ParamCount) return -1;
            float inBlack = EffectScalar(e, first, t, 0), inWhite = EffectScalar(e, first + 1, t, 1), gamma = EffectScalar(e, first + 2, t, 1);
            float outBlack = EffectScalar(e, first + 3, t, 0), outWhite = EffectScalar(e, first + 4, t, 1);
            if (inBlack == 0 && inWhite == 1 && gamma == 1 && outBlack == 0 && outWhite == 1) return -1;
            int offset = Out.PaintFloats.Length;
            float mn = math.min(inBlack, inWhite), mx = math.max(inBlack, inWhite);
            float outDelta = outWhite - outBlack, inDelta = inWhite - inBlack;
            for (int k = 0; k < 256; k++)
            {
                float perc = k / 255f;
                float v;
                if (perc <= mn) v = inDelta < 0 ? outWhite : outBlack;
                else if (perc >= mx) v = inDelta < 0 ? outBlack : outWhite;
                else v = outBlack + outDelta * math.pow((perc - inBlack) / inDelta, 1f / gamma);
                Out.PaintFloats.Add(v);
            }
            return offset;
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
                        sectionDepth++;
                        break;
                    case CmdKind.EndMask:
                    case CmdKind.EndMatte:
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
                if (gd.HasMask != 0 || gd.MatteMode != 0 || gd.BlendMode != 0 || gd.EffectCount > 0) continue;
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
