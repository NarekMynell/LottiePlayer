// Mynell Lottie Player — Bake-time world transform + canvas-center normalization.
// All layer/group matrices and coordinate remapping happen here; runtime is passthrough.

using Unity.Mathematics;

namespace Mynell.LottiePlayer
{
    internal static class LottieBakeGeometry
    {
        /// <summary>
        /// Fold static hierarchy transforms into geometry, then map every vertex to
        /// pivot-centered canvas space: [-rectW/2, rectW/2] × [-rectH/2, rectH/2] (Y-up).
        /// </summary>
        public static void Apply(LottieParser.ParseContext ctx, float assetWidth, float assetHeight)
        {
            float aw = math.max(1f, assetWidth);
            float ah = math.max(1f, assetHeight);
            float rw = aw;
            float rh = ah;

            BakeCompositionRange(ctx, ctx.Header.LayerOffset, ctx.Header.LayerCount, AffineMatrix.Identity);

            for (int i = 0; i < ctx.BezierVertices.Count; i++)
            {
                var bv = ctx.BezierVertices[i];
                bv.V = NormalizeVertex(bv.V, aw, ah, rw, rh);
                ctx.BezierVertices[i] = bv;
            }

            for (int i = 0; i < ctx.Vec2Pool.Count; i++)
                ctx.Vec2Pool[i] = NormalizeVertex(ctx.Vec2Pool[i], aw, ah, rw, rh);
        }

        /// <summary>
        /// Map Lottie comp pixels (Y-down) → RectTransform local (Y-up, centered at origin).
        /// </summary>
        public static float2 NormalizeVertex(float2 p, float assetW, float assetH, float rectW, float rectH)
        {
            return new float2(
                (p.x / assetW) * rectW - rectW * 0.5f,
                (1f - p.y / assetH) * rectH - rectH * 0.5f);
        }

        private static void BakeCompositionRange(
            LottieParser.ParseContext ctx, int layerOffset, int layerCount, in AffineMatrix outerParent)
        {
            if (layerCount <= 0) return;

            var worldByLocal = new AffineMatrix[layerCount];
            var ready = new bool[layerCount];

            for (int pass = 0; pass < layerCount; pass++)
            {
                bool progress = false;
                for (int li = 0; li < layerCount; li++)
                {
                    if (ready[li]) continue;
                    var layer = ctx.Layers[layerOffset + li];
                    AffineMatrix local = EvalStaticLayerMatrix(layer, ctx);
                    int parentLocal = layer.ParentLocalIndex;

                    if (parentLocal < 0 || parentLocal >= layerCount)
                    {
                        worldByLocal[li] = AffineMatrix.Multiply(in outerParent, in local);
                        ready[li] = true;
                        progress = true;
                    }
                    else if (ready[parentLocal])
                    {
                        worldByLocal[li] = AffineMatrix.Multiply(in worldByLocal[parentLocal], in local);
                        ready[li] = true;
                        progress = true;
                    }
                }
                if (!progress) break;
            }

            for (int li = 0; li < layerCount; li++)
            {
                if (!ready[li]) worldByLocal[li] = outerParent;

                var layer = ctx.Layers[layerOffset + li];
                if (layer.IsTrackMatte != 0) continue;

                switch (layer.Kind)
                {
                    case LottieLayerKind.Shape:
                        BakeShapeRange(ctx, layer.ShapeOffset, layer.ShapeCount, in worldByLocal[li]);
                        break;
                    case LottieLayerKind.Precomp:
                        if (layer.PrecompId >= 0 && layer.PrecompId < ctx.Precomps.Count)
                        {
                            var pc = ctx.Precomps[layer.PrecompId];
                            BakeCompositionRange(ctx, pc.LayerOffset, pc.LayerCount, in worldByLocal[li]);
                        }
                        break;
                }
            }
        }

        private static void BakeShapeRange(
            LottieParser.ParseContext ctx, int offset, int count, in AffineMatrix parentWorld)
        {
            if (count <= 0) return;

            AffineMatrix groupTr = FindGroupTransform(ctx, offset, count);
            AffineMatrix bakeM = AffineMatrix.Multiply(in parentWorld, in groupTr);
            if (bakeM.IsDegenerate(1e-8f)) return;

            for (int i = offset; i < offset + count; i++)
            {
                var s = ctx.Shapes[i];
                switch (s.Kind)
                {
                    case LottieShapeKind.Group:
                        BakeShapeRange(ctx, s.ChildOffset, s.ChildCount, in bakeM);
                        break;
                    case LottieShapeKind.Path:
                        BakePathProperty(ctx, s.P0, in bakeM);
                        break;
                    case LottieShapeKind.Rect:
                        BakeRectShape(ctx, ref s, in bakeM);
                        ctx.Shapes[i] = s;
                        break;
                    case LottieShapeKind.Ellipse:
                        BakeEllipseShape(ctx, ref s, in bakeM);
                        ctx.Shapes[i] = s;
                        break;
                }
            }
        }

        private static AffineMatrix FindGroupTransform(LottieParser.ParseContext ctx, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                if (ctx.Shapes[i].Kind != LottieShapeKind.Transform) continue;
                return EvalStaticShapeTransform(ctx.Shapes[i], ctx);
            }
            return AffineMatrix.Identity;
        }

        private static void BakePathProperty(LottieParser.ParseContext ctx, int pathPropIdx, in AffineMatrix world)
        {
            if (pathPropIdx < 0 || pathPropIdx >= ctx.PathProperties.Count) return;
            var pp = ctx.PathProperties[pathPropIdx];
            int snapEnd = pp.SnapshotOffset + pp.SnapshotCount;
            for (int si = pp.SnapshotOffset; si < snapEnd; si++)
            {
                var snap = ctx.PathSnapshots[si];
                int vEnd = snap.VertexOffset + snap.VertexCount;
                for (int vi = snap.VertexOffset; vi < vEnd; vi++)
                {
                    var bv = ctx.BezierVertices[vi];
                    bv.V = world.Apply(bv.V);
                    bv.In = world.ApplyDirection(bv.In);
                    bv.Out = world.ApplyDirection(bv.Out);
                    ctx.BezierVertices[vi] = bv;
                }
            }
        }

        private static void BakeRectShape(LottieParser.ParseContext ctx, ref LottieShape s, in AffineMatrix world)
        {
            TransformVec2Prop(ctx, s.P0, world, isSize: false);
            TransformVec2Prop(ctx, s.P1, world, isSize: true);
            TransformScalarProp(ctx, s.P2, world);
        }

        private static void BakeEllipseShape(LottieParser.ParseContext ctx, ref LottieShape s, in AffineMatrix world)
        {
            TransformVec2Prop(ctx, s.P0, world, isSize: false);
            TransformVec2Prop(ctx, s.P1, world, isSize: true);
        }

        private static void TransformVec2Prop(
            LottieParser.ParseContext ctx, int propIdx, in AffineMatrix world, bool isSize)
        {
            if (propIdx < 0 || propIdx >= ctx.Properties.Count) return;
            var prop = ctx.Properties[propIdx];

            if (prop.Kind == LottiePropertyKind.Constant)
            {
                ctx.Vec2Pool[prop.ValueIndex] = TransformVec2(ctx.Vec2Pool[prop.ValueIndex], in world, isSize);
                return;
            }

            if (prop.Kind == LottiePropertyKind.AnimatedVec2 && prop.KeyframeCount > 0)
            {
                int kfEnd = prop.KeyframeOffset + prop.KeyframeCount;
                for (int ki = prop.KeyframeOffset; ki < kfEnd; ki++)
                {
                    int vi = ctx.Keyframes[ki].ValueOffset;
                    ctx.Vec2Pool[vi] = TransformVec2(ctx.Vec2Pool[vi], in world, isSize);
                }
            }
        }

        private static void TransformScalarProp(LottieParser.ParseContext ctx, int propIdx, in AffineMatrix world)
        {
            if (propIdx < 0 || propIdx >= ctx.Properties.Count) return;
            var prop = ctx.Properties[propIdx];
            float scale = math.max(math.length(world.c0), math.length(world.c1));

            if (prop.Kind == LottiePropertyKind.Constant)
            {
                ctx.ScalarPool[prop.ValueIndex] *= scale;
                return;
            }

            if (prop.Kind == LottiePropertyKind.AnimatedScalar && prop.KeyframeCount > 0)
            {
                int kfEnd = prop.KeyframeOffset + prop.KeyframeCount;
                for (int ki = prop.KeyframeOffset; ki < kfEnd; ki++)
                {
                    int si = ctx.Keyframes[ki].ValueOffset;
                    ctx.ScalarPool[si] *= scale;
                }
            }
        }

        private static float2 TransformVec2(float2 v, in AffineMatrix world, bool isSize)
            => isSize ? new float2(
                math.length(world.c0) * v.x,
                math.length(world.c1) * v.y)
            : world.Apply(v);

        private static AffineMatrix EvalStaticLayerMatrix(LottieLayer layer, LottieParser.ParseContext ctx)
        {
            float2 anchor = ReadStaticVec2(layer.AnchorProp, ctx);
            float2 pos = ReadStaticVec2(layer.PositionProp, ctx);
            float2 scale = ReadStaticVec2(layer.ScaleProp, ctx);
            if (scale.x == 0f && scale.y == 0f) scale = new float2(100f, 100f);
            float rot = ReadStaticScalar(layer.RotationProp, ctx);
            return AffineMatrix.GetMatrixFromTransform(anchor, pos, scale, rot);
        }

        private static AffineMatrix EvalStaticShapeTransform(LottieShape tr, LottieParser.ParseContext ctx)
        {
            float2 anchor = ReadStaticVec2(tr.P0, ctx);
            float2 pos = ReadStaticVec2(tr.P1, ctx);
            float2 scale = ReadStaticVec2(tr.P2, ctx);
            if (scale.x == 0f && scale.y == 0f) scale = new float2(100f, 100f);
            float rot = ReadStaticScalar(tr.P3, ctx);
            return AffineMatrix.GetMatrixFromTransform(anchor, pos, scale, rot);
        }

        private static float2 ReadStaticVec2(int propIdx, LottieParser.ParseContext ctx)
        {
            if (propIdx < 0 || propIdx >= ctx.Properties.Count) return float2.zero;
            var p = ctx.Properties[propIdx];
            if (p.Kind == LottiePropertyKind.Constant)
                return ctx.Vec2Pool[p.ValueIndex];
            if (p.Kind == LottiePropertyKind.AnimatedVec2 && p.KeyframeCount > 0)
                return ctx.Vec2Pool[ctx.Keyframes[p.KeyframeOffset].ValueOffset];
            return float2.zero;
        }

        private static float ReadStaticScalar(int propIdx, LottieParser.ParseContext ctx)
        {
            if (propIdx < 0 || propIdx >= ctx.Properties.Count) return 0f;
            var p = ctx.Properties[propIdx];
            if (p.Kind == LottiePropertyKind.Constant)
                return ctx.ScalarPool[p.ValueIndex];
            if (p.Kind == LottiePropertyKind.AnimatedScalar && p.KeyframeCount > 0)
                return ctx.ScalarPool[ctx.Keyframes[p.KeyframeOffset].ValueOffset];
            return 0f;
        }
    }
}
