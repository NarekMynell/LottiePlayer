// Mynell Lottie Player — Phase D Stage 1 (sequential): walk the Lottie hierarchy,
// fold parent matrices, evaluate animated properties at the current frame, flatten
// bezier paths into a shared polyline pool and emit prefix-summed DrawCommands for
// the parallel tessellator (TessellateCommandsJob, Stage 2).
//
// Why IJob (not IJobParallelFor): the layer/shape traversal is inherently sequential
// because every child's world matrix depends on its parent's composed matrix. Burst
// still gives us full vectorised codegen for the per-shape work; the single-thread
// scan typically takes 100-400 µs even for dense animations. The HEAVY work
// (ear-clipping triangulation + per-vertex transform) happens in Stage 2 on N cores.

using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Mynell.LottiePlayer
{
    [BurstCompile(FloatPrecision.Standard, FloatMode.Default)]
    public struct BuildDrawCommandsJob : IJob
    {
        // ----------------- Inputs (read-only) -----------------
        public LottieHeader Header;
        [ReadOnly] public NativeArray<LottieLayer> Layers;
        [ReadOnly] public NativeArray<LottiePrecomp> Precomps;
        [ReadOnly] public NativeArray<LottieShape> Shapes;
        [ReadOnly] public NativeArray<LottieProperty> Properties;
        [ReadOnly] public NativeArray<LottieKeyframe> Keyframes;
        [ReadOnly] public NativeArray<LottiePathProperty> PathProperties;
        [ReadOnly] public NativeArray<LottiePathSnapshot> PathSnapshots;
        [ReadOnly] public NativeArray<LottieBezierVertex> BezierVertices;
        [ReadOnly] public NativeArray<float> ScalarPool;
        [ReadOnly] public NativeArray<float2> Vec2Pool;
        [ReadOnly] public NativeArray<float4> Vec4Pool;

        public float CurrentFrame;
        public float Tolerance;       // bezier flattening tolerance (composition px)
        public float2 OutputScale;    // composition pixel → UI unit
        public float2 OutputOffset;   // where composition origin maps in UI units
        public float YFlip;           // -1 to flip y (Lottie is y-down)

        // Capacity gates for the prefix-sum allocator. Once any of these would be exceeded,
        // we flip the overflow flag and stop emitting further commands.
        public int MaxVertices;
        public int MaxIndices;
        /// <summary>
        /// Per-shape polyline cap. Stage 2's per-thread scratch buffers (Phase E) are
        /// sized exactly to this — any path that would flatten beyond it is dropped
        /// here in Stage 1 and bumps the overflow flag, so Stage 2 never sees an
        /// oversized command. Default is set by the component (1024 is comfortable).
        /// </summary>
        public int MaxVerticesPerShape;

        // ----------------- Outputs -----------------
        // Polyline pool: every emitted command points at a disjoint window here. Filled
        // sequentially during Stage 1, then read in parallel during Stage 2.
        public NativeArray<float2> PolyPool;
        // List of work items for Stage 2. Pre-allocated capacity; we use AddNoResize because
        // we already gate against Capacity in the AppendXxxCommand helpers.
        public NativeList<DrawCommand> Commands;
        // [vertReserved, indexReserved, overflow(0/1), commandCount, polyUsed]
        public NativeArray<int> Counts;
        /// <summary>
        /// Persistent scratch used by <see cref="ApplyTrimToPoly"/>. Sized to
        /// <c>MaxVerticesPerShape</c> by the component. Stage 1 is single-threaded so
        /// no per-thread partitioning is needed. Phase E moves this off Allocator.Temp
        /// so the IJob makes literally zero allocations per frame.
        /// </summary>
        public NativeArray<float2> TrimScratch;

        // -----------------------------------------------------------------------------------
        public void Execute()
        {
            // Composition → output bridge: scale + offset + Y-flip folded into one matrix
            // applied as the OUTERMOST transform. Same trick as Phase A — the per-vertex
            // (q.y *= YFlip; q = q * OutputScale + OutputOffset) post-pass is subsumed by
            // multiplying every layer's local matrix into this one.
            AffineMatrix outputBridge;
            outputBridge.c0 = new float2(OutputScale.x, 0f);
            outputBridge.c1 = new float2(0f, OutputScale.y * YFlip);
            outputBridge.c2 = OutputOffset;

            int nextVert = 0;
            int nextIdx = 0;
            int polyUsed = 0;
            byte overflow = 0;

            ProcessLayerRange(Header.LayerOffset, Header.LayerCount,
                in outputBridge, PaintState.Default, CurrentFrame, depth: 0,
                ref nextVert, ref nextIdx, ref polyUsed, ref overflow);

            Counts[0] = nextVert;
            Counts[1] = nextIdx;
            Counts[2] = overflow;
            Counts[3] = Commands.Length;
            Counts[4] = polyUsed;
        }

        // ============================== Hierarchy walk ==============================

        /// <summary>
        /// Reverse-z-order layer traversal (first JSON layer = top = drawn LAST). Recurses
        /// into precomp layers. World matrices are built in topological order (parents before
        /// children) before the draw pass. Prefix-sum slots are allocated in draw order only.
        /// </summary>
        private unsafe void ProcessLayerRange(int offset, int count, in AffineMatrix parent, PaintState parentPaint,
            float parentTime, int depth,
            ref int nextVert, ref int nextIdx, ref int polyUsed, ref byte overflow)
        {
            if (depth >= 8) return;
            if (offset < 0 || count <= 0 || offset >= Layers.Length) return;
            int safeCount = math.min(count, Layers.Length - offset);
            if (safeCount <= 0) return;

            const int MaxCompLayers = 128;
            AffineMatrix* worldCache = stackalloc AffineMatrix[MaxCompLayers];
            byte* worldReady = stackalloc byte[MaxCompLayers];
            BuildCompositionWorldMatrices(offset, safeCount, parentTime, in parent,
                worldCache, worldReady, MaxCompLayers);

            for (int li = safeCount - 1; li >= 0; li--)
            {
                if (overflow != 0) return;

                var layer = Layers[offset + li];
                if (layer.IsTrackMatte != 0) continue;
                if (layer.Kind != LottieLayerKind.Shape && layer.Kind != LottieLayerKind.Precomp) continue;
                if (parentTime < layer.InFrame || parentTime >= layer.OutFrame) continue;

                float localTime = (parentTime - layer.StartFrame) * layer.StretchTimeMul;

                AffineMatrix world = li < MaxCompLayers && worldReady[li] != 0
                    ? worldCache[li]
                    : EvalLayerWorldFallback(offset, safeCount, li, parentTime, in parent);
                if (world.IsDegenerate(1e-8f)) continue;

                float layerOpacity = EvalScalar(layer.OpacityProp, parentTime, 100f) * 0.01f;
                PaintState paint = parentPaint;
                paint.Opacity = parentPaint.Opacity * layerOpacity;

                switch (layer.Kind)
                {
                    case LottieLayerKind.Shape:
                        ProcessShapeRange(layer.ShapeOffset, layer.ShapeCount, in world, paint, parentTime,
                            inheritTrim: 0, inheritTrimStart: 0f, inheritTrimEnd: 1f,
                            ref nextVert, ref nextIdx, ref polyUsed, ref overflow);
                        break;

                    case LottieLayerKind.Precomp:
                        if (layer.PrecompId >= 0 && layer.PrecompId < Precomps.Length)
                        {
                            var pc = Precomps[layer.PrecompId];
                            ProcessLayerRange(pc.LayerOffset, pc.LayerCount, in world, paint, localTime, depth + 1,
                                ref nextVert, ref nextIdx, ref polyUsed, ref overflow);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Topological pass: FinalGlobal[child] = FinalGlobal[parent] · Local[child].
        /// Parents are resolved before children via multi-pass relaxation.
        /// Includes null layers so their transforms participate in the hierarchy.
        /// </summary>
        private unsafe void BuildCompositionWorldMatrices(
            int layerOffset, int layerCount, float compTime, in AffineMatrix outerParent,
            AffineMatrix* worldCache, byte* worldReady, int cacheCapacity)
        {
            int safeCount = math.min(layerCount, cacheCapacity);
            for (int li = 0; li < safeCount; li++) worldReady[li] = 0;

            for (int pass = 0; pass < safeCount; pass++)
            {
                bool anyProgress = false;
                for (int li = 0; li < safeCount; li++)
                {
                    if (worldReady[li] != 0) continue;

                    var layer = Layers[layerOffset + li];
                    AffineMatrix local = EvalLayerTransform(layer, compTime);
                    int parentLocal = ResolveParentLocalIndex(layerOffset, layerCount, li);

                    if (parentLocal < 0 || parentLocal >= safeCount)
                    {
                        worldCache[li] = AffineMatrix.Multiply(in outerParent, in local);
                        worldReady[li] = 1;
                        anyProgress = true;
                    }
                    else if (worldReady[parentLocal] != 0)
                    {
                        worldCache[li] = AffineMatrix.Multiply(in worldCache[parentLocal], in local);
                        worldReady[li] = 1;
                        anyProgress = true;
                    }
                }
                if (!anyProgress) break;
            }
        }

        /// <summary>
        /// Fallback when cache is full or hierarchy has a cycle: walk parent chain upward.
        /// </summary>
        private unsafe AffineMatrix EvalLayerWorldFallback(
            int layerOffset, int layerCount, int localIdx, float compTime, in AffineMatrix outerParent)
        {
            const int MaxChain = 16;
            int* chain = stackalloc int[MaxChain];
            int depth = 0;
            int cur = localIdx;
            while (cur >= 0 && depth < MaxChain)
            {
                if (cur >= layerCount) break;
                int globalIdx = layerOffset + cur;
                if (globalIdx < 0 || globalIdx >= Layers.Length) break;
                bool already = false;
                for (int j = 0; j < depth; j++) { if (chain[j] == cur) { already = true; break; } }
                if (already) break;
                chain[depth++] = cur;
                cur = ResolveParentLocalIndex(layerOffset, layerCount, cur);
            }

            AffineMatrix world = outerParent;
            for (int i = depth - 1; i >= 0; i--)
            {
                AffineMatrix local = EvalLayerTransform(Layers[layerOffset + chain[i]], compTime);
                world = AffineMatrix.Multiply(in world, in local);
            }
            return world;
        }

        private int ResolveParentLocalIndex(int layerOffset, int layerCount, int localIdx)
        {
            if (localIdx < 0 || localIdx >= layerCount) return -1;
            int globalIdx = layerOffset + localIdx;
            if (globalIdx < 0 || globalIdx >= Layers.Length) return -1;

            var layer = Layers[globalIdx];
            int parentLocal = layer.ParentLocalIndex;
            if (parentLocal >= 0 && parentLocal < layerCount) return parentLocal;

            int parentInd = layer.ParentInd;
            if (parentInd < 0) return -1;
            return FindLayerLocalByInd(layerOffset, layerCount, parentInd);
        }

        private int FindLayerLocalByInd(int layerOffset, int layerCount, int ind)
        {
            for (int i = 0; i < layerCount; i++)
            {
                if (Layers[layerOffset + i].LayerInd == ind) return i;
            }
            return -1;
        }

        private AffineMatrix EvalLayerTransform(in LottieLayer layer, float compTime)
        {
            float2 anchor = EvalVec2(layer.AnchorProp, compTime);
            float2 pos = EvalVec2(layer.PositionProp, compTime);
            float2 scalePerc = EvalVec2Pct(layer.ScaleProp, compTime);
            float rot = EvalScalar(layer.RotationProp, compTime, 0f);
            return AffineMatrix.GetMatrixFromTransform(anchor, pos, scalePerc, rot);
        }

        private AffineMatrix EvalShapeTransform(in LottieShape tr, float compTime)
        {
            float2 anchor = EvalVec2(tr.P0, compTime);
            float2 pos = EvalVec2(tr.P1, compTime);
            float2 scalePerc = EvalVec2Pct(tr.P2, compTime);
            float rot = EvalScalar(tr.P3, compTime, 0f);
            return AffineMatrix.GetMatrixFromTransform(anchor, pos, scalePerc, rot);
        }

        private AffineMatrix EvalScopedGroupTransform(int offset, int count, float compTime, out int transformIdx)
        {
            transformIdx = -1;
            for (int i = offset; i < offset + count; i++)
            {
                if (Shapes[i].Kind != LottieShapeKind.Transform) continue;
                transformIdx = i;
                return EvalShapeTransform(Shapes[i], compTime);
            }
            return AffineMatrix.Identity;
        }

        // ============================== Shape walker ==============================

        /// <summary>
        /// Two-pass walk of a shape range: pass 1 collects fill / stroke / transform / trim;
        /// pass 2 emits commands for paths, rects, ellipses (recurses into groups).
        /// </summary>
        private void ProcessShapeRange(
            int offset, int count, in AffineMatrix parentWorld, PaintState parentPaint, float compTime,
            byte inheritTrim, float inheritTrimStart, float inheritTrimEnd,
            ref int nextVert, ref int nextIdx, ref int polyUsed, ref byte overflow)
        {
            AffineMatrix scopedGroupTr = EvalScopedGroupTransform(offset, count, compTime, out int scopedTrIdx);
            AffineMatrix bakeMatrix = AffineMatrix.Multiply(in parentWorld, in scopedGroupTr);
            if (bakeMatrix.IsDegenerate(1e-8f)) return;

            PaintState paint = parentPaint;
            float4 strokeColor = new float4(0f, 0f, 0f, 0f);
            float strokeWidth = 0f;
            byte hasStroke = 0;
            float trimStart = inheritTrimStart;
            float trimEnd = inheritTrimEnd;
            byte hasTrim = inheritTrim;

            for (int i = offset; i < offset + count; i++)
            {
                var s = Shapes[i];
                if (s.Kind == LottieShapeKind.Fill)
                {
                    float4 col = EvalColor(s.P0, compTime);
                    float op = EvalScalar(s.P1, compTime, 100f) * 0.01f;
                    col.w *= op * paint.Opacity;
                    paint.Color = col;
                    paint.HasFill = 1;
                }
                else if (s.Kind == LottieShapeKind.Stroke)
                {
                    float4 col = EvalColor(s.P0, compTime);
                    float op = EvalScalar(s.P1, compTime, 100f) * 0.01f;
                    col.w *= op * paint.Opacity;
                    strokeColor = col;
                    strokeWidth = EvalScalar(s.P2, compTime, 1f);
                    hasStroke = 1;
                }
                else if (s.Kind == LottieShapeKind.Transform && i == scopedTrIdx)
                {
                    paint.Opacity *= EvalScalar(s.P4, compTime, 100f) * 0.01f;
                }
                else if (s.Kind == LottieShapeKind.TrimPath)
                {
                    float startPct = EvalScalar(s.P0, compTime, 0f);
                    float endPct = EvalScalar(s.P1, compTime, 100f);
                    float offsetDeg = EvalScalar(s.P2, compTime, 0f);
                    float phase = offsetDeg / 360f;
                    float ns = startPct * 0.01f + phase;
                    float ne = endPct * 0.01f + phase;
                    if (ns > ne) { var tmp = ns; ns = ne; ne = tmp; }
                    trimStart = math.saturate(ns);
                    trimEnd = math.saturate(ne);
                    hasTrim = 1;
                }
            }

            uint fillColorPacked = paint.HasFill != 0 ? PackColor(paint.Color) : 0u;
            uint strokeColorPacked = hasStroke != 0 ? PackColor(strokeColor) : 0u;

            for (int i = offset + count - 1; i >= offset; i--)
            {
                if (overflow != 0) return;
                var s = Shapes[i];
                switch (s.Kind)
                {
                    case LottieShapeKind.Group:
                        ProcessShapeRange(s.ChildOffset, s.ChildCount, in bakeMatrix, paint, compTime,
                            hasTrim, trimStart, trimEnd,
                            ref nextVert, ref nextIdx, ref polyUsed, ref overflow);
                        break;

                    case LottieShapeKind.Path:
                        EmitPathCommands(s.P0, in bakeMatrix,
                            paint.HasFill, fillColorPacked,
                            hasStroke, strokeColorPacked, strokeWidth,
                            hasTrim, trimStart, trimEnd, compTime,
                            ref nextVert, ref nextIdx, ref polyUsed, ref overflow);
                        break;

                    case LottieShapeKind.Rect:
                        EmitPrimitiveCommands(kind: 0, s, in bakeMatrix,
                            paint.HasFill, fillColorPacked,
                            hasStroke, strokeColorPacked, strokeWidth, compTime,
                            ref nextVert, ref nextIdx, ref polyUsed, ref overflow);
                        break;

                    case LottieShapeKind.Ellipse:
                        EmitPrimitiveCommands(kind: 1, s, in bakeMatrix,
                            paint.HasFill, fillColorPacked,
                            hasStroke, strokeColorPacked, strokeWidth, compTime,
                            ref nextVert, ref nextIdx, ref polyUsed, ref overflow);
                        break;
                }
            }
        }

        // ============================== Geometry → Commands ==============================

        /// <summary>
        /// Flatten a bezier path into a fresh PolyPool window, optionally trim it, optionally
        /// compact-clean degenerate segments, then emit one or two commands.
        /// </summary>
        private void EmitPathCommands(int pathPropIdx, in AffineMatrix world,
            byte hasFill, uint fillColorPacked,
            byte hasStroke, uint strokeColorPacked, float strokeWidth,
            byte hasTrim, float trimStart, float trimEnd, float t,
            ref int nextVert, ref int nextIdx, ref int polyUsed, ref byte overflow)
        {
            if (pathPropIdx < 0 || pathPropIdx >= PathProperties.Length) return;
            var pp = PathProperties[pathPropIdx];
            if (pp.VertexCount < 2) return;

            int polyOffset = polyUsed;
            int spaceLeft = PolyPool.Length - polyOffset;
            // Need room for at least a short polyline; be conservative.
            if (spaceLeft < 8) { overflow = 1; return; }

            // Cap the working view at MaxVerticesPerShape so Stage 2's persistent per-
            // thread scratch buffers can never be overrun. If the natural flatten count
            // hits the cap we treat it as an overflow and skip the shape.
            int viewCap = math.min(spaceLeft, MaxVerticesPerShape);
            var view = PolyPool.GetSubArray(polyOffset, viewCap);
            int polyCount = 0;
            FlattenPathToPoly(pp, t, view, ref polyCount);
            if (polyCount < 2) return;
            if (polyCount >= viewCap)
            {
                // Filled the slot exactly — likely truncated. Refuse to render with a
                // partial polyline; user should bump MaxVerticesPerShape.
                overflow = 1;
                return;
            }

            bool closed = pp.Closed != 0;
            if (hasTrim != 0)
            {
                polyCount = ApplyTrimToPoly(view, polyCount, closed, trimStart, trimEnd, TrimScratch);
                if (polyCount < 2) return;
                closed = false;
            }

            // Compact-clean degenerate consecutive points so Stage 2's stroke extruder
            // never divides by zero (and its prefix-sum count is exact).
            polyCount = CompactPoly(view, polyCount, closed);
            if (polyCount < 2) return;
            if (polyCount > MaxVerticesPerShape) { overflow = 1; return; }

            polyUsed = polyOffset + polyCount;

            if (hasFill != 0 && polyCount >= 3)
            {
                AppendFillCommand(in world, fillColorPacked, polyOffset, polyCount,
                    ref nextVert, ref nextIdx, ref overflow);
            }
            if (hasStroke != 0 && strokeWidth > 0.001f)
            {
                AppendStrokeCommand(in world, strokeColorPacked, strokeWidth, closed, polyOffset, polyCount,
                    ref nextVert, ref nextIdx, ref overflow);
            }
        }

        /// <summary>
        /// Build polyline for a Rect (kind=0) or Ellipse (kind=1), then emit commands.
        /// Both primitives are always closed.
        /// </summary>
        private void EmitPrimitiveCommands(byte kind, LottieShape s, in AffineMatrix world,
            byte hasFill, uint fillColorPacked,
            byte hasStroke, uint strokeColorPacked, float strokeWidth, float t,
            ref int nextVert, ref int nextIdx, ref int polyUsed, ref byte overflow)
        {
            int polyOffset = polyUsed;
            int spaceLeft = PolyPool.Length - polyOffset;
            if (spaceLeft < 8) { overflow = 1; return; }

            int viewCap = math.min(spaceLeft, MaxVerticesPerShape);
            var view = PolyPool.GetSubArray(polyOffset, viewCap);
            int polyCount = 0;
            if (kind == 0) BuildRectPoly(s, t, view, ref polyCount);
            else           BuildEllipsePoly(s, t, view, ref polyCount);
            if (polyCount < 2) return;

            polyCount = CompactPoly(view, polyCount, closed: true);
            if (polyCount < 2) return;
            if (polyCount > MaxVerticesPerShape) { overflow = 1; return; }

            polyUsed = polyOffset + polyCount;

            if (hasFill != 0 && polyCount >= 3)
            {
                AppendFillCommand(in world, fillColorPacked, polyOffset, polyCount,
                    ref nextVert, ref nextIdx, ref overflow);
            }
            if (hasStroke != 0 && strokeWidth > 0.001f)
            {
                AppendStrokeCommand(in world, strokeColorPacked, strokeWidth, closed: true, polyOffset, polyCount,
                    ref nextVert, ref nextIdx, ref overflow);
            }
        }

        /// <summary>
        /// Reserve a fill slot in the prefix-summed buffers. Vertex count = polyCount,
        /// index count = (polyCount - 2) * 3 (max ear-clip triangles).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AppendFillCommand(in AffineMatrix world, uint color, int polyOffset, int polyCount,
            ref int nextVert, ref int nextIdx, ref byte overflow)
        {
            int vCount = polyCount;
            int iCount = (polyCount - 2) * 3;
            if (nextVert + vCount > MaxVertices ||
                nextIdx + iCount > MaxIndices ||
                Commands.Length >= Commands.Capacity)
            {
                overflow = 1;
                return;
            }
            Commands.AddNoResize(new DrawCommand
            {
                World = world,
                Color = color,
                Kind = DrawCommandKind.Fill,
                Closed = 1,
                StrokeWidth = 0f,
                PolyOffset = polyOffset,
                PolyCount = polyCount,
                VertexOffset = nextVert,
                VertexCount = vCount,
                IndexOffset = nextIdx,
                IndexCount = iCount,
            });
            nextVert += vCount;
            nextIdx += iCount;
        }

        /// <summary>
        /// Reserve a stroke slot. Each segment becomes 4 verts + 6 indices (one quad).
        /// CompactPoly already removed degenerates so segCount is exact.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AppendStrokeCommand(in AffineMatrix world, uint color, float strokeWidth, bool closed,
            int polyOffset, int polyCount,
            ref int nextVert, ref int nextIdx, ref byte overflow)
        {
            int segCount = closed ? polyCount : polyCount - 1;
            if (segCount < 1) return;
            int vCount = segCount * 4;
            int iCount = segCount * 6;
            if (nextVert + vCount > MaxVertices ||
                nextIdx + iCount > MaxIndices ||
                Commands.Length >= Commands.Capacity)
            {
                overflow = 1;
                return;
            }
            Commands.AddNoResize(new DrawCommand
            {
                World = world,
                Color = color,
                Kind = DrawCommandKind.Stroke,
                Closed = (byte)(closed ? 1 : 0),
                StrokeWidth = strokeWidth,
                PolyOffset = polyOffset,
                PolyCount = polyCount,
                VertexOffset = nextVert,
                VertexCount = vCount,
                IndexOffset = nextIdx,
                IndexCount = iCount,
            });
            nextVert += vCount;
            nextIdx += iCount;
        }

        // ============================== Geometry helpers ==============================

        /// <summary>
        /// Drop consecutive points within 1e-4 of each other (and close-back-to-start for
        /// closed paths). Keeps Stage 2's stroke extruder out of the rsqrt(0) trap and
        /// makes the prefix-summed segment counts exact.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CompactPoly(NativeArray<float2> poly, int count, bool closed)
        {
            if (count < 2) return count;
            int w = 1;
            for (int r = 1; r < count; r++)
            {
                if (math.lengthsq(poly[r] - poly[w - 1]) >= 1e-8f) poly[w++] = poly[r];
            }
            if (closed && w >= 2 && math.lengthsq(poly[w - 1] - poly[0]) < 1e-8f) w--;
            return w;
        }

        /// <summary>
        /// Saturate-and-pack RGBA float4 into a UNorm8x4 uint matching VertexLayout.Color
        /// (R = byte0). Identical formula to the inline version in TessellateCommandsJob —
        /// kept here so Stage 1 can pre-pack and Stage 2 only sees uint.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PackColor(float4 c)
        {
            uint4 b = (uint4)(math.saturate(c) * 255f + 0.5f);
            return b.x | (b.y << 8) | (b.z << 16) | (b.w << 24);
        }

        // -------- Path flatten / trim (carried over from the single-job pipeline) --------

        private void FlattenPathToPoly(LottiePathProperty pp, float t,
            NativeArray<float2> polyBuf, ref int polyCount)
        {
            polyCount = 0;
            int nVerts = pp.VertexCount;
            if (nVerts < 2) return;

            int snapA = pp.SnapshotOffset;
            int snapB = pp.SnapshotOffset;
            float blend = 0f;
            if (pp.Kind == LottiePropertyKind.AnimatedPath && pp.SnapshotCount > 1)
            {
                ResolveKeyframePair(pp.KeyframeOffset, pp.SnapshotCount, t, out int idxA, out int idxB, out blend);
                snapA = pp.SnapshotOffset + idxA;
                snapB = pp.SnapshotOffset + idxB;
            }

            var sa = PathSnapshots[snapA];
            var sb = PathSnapshots[snapB];
            int n = math.min(sa.VertexCount, sb.VertexCount);
            if (n < 2) return;
            bool closed = sa.Closed != 0;

            int segs = closed ? n : (n - 1);
            for (int i = 0; i < segs; i++)
            {
                int i0 = i;
                int i1 = (i + 1) % n;
                var av0 = BezierVertices[sa.VertexOffset + i0];
                var av1 = BezierVertices[sa.VertexOffset + i1];
                var bv0 = BezierVertices[sb.VertexOffset + i0];
                var bv1 = BezierVertices[sb.VertexOffset + i1];

                float2 p0 = math.lerp(av0.V, bv0.V, blend);
                float2 p1 = math.lerp(av1.V, bv1.V, blend);
                float2 out0 = math.lerp(av0.Out, bv0.Out, blend);
                float2 in1 = math.lerp(av1.In, bv1.In, blend);
                float2 c0 = p0 + out0;
                float2 c1 = p1 + in1;
                BezierMath.FlattenCubic(p0, c0, c1, p1, Tolerance, polyBuf, ref polyCount);
            }
            if (!closed && polyCount < polyBuf.Length)
            {
                var last = BezierVertices[sa.VertexOffset + (n - 1)];
                var lastB = BezierVertices[sb.VertexOffset + (n - 1)];
                polyBuf[polyCount++] = math.lerp(last.V, lastB.V, blend);
            }
        }

        /// <summary>
        /// Trim a polyline in place to the arc-length window [ts*total, te*total].
        /// Phase E: scratch is supplied by the caller (a Persistent buffer owned by the
        /// component) — no per-call allocation. Scratch capacity must be ≥ polyBuf.Length.
        /// </summary>
        private static int ApplyTrimToPoly(NativeArray<float2> polyBuf, int polyCount, bool closed,
            float ts, float te, NativeArray<float2> scratch)
        {
            if (polyCount < 2) return polyCount;
            if (ts <= 0f && te >= 1f) return polyCount;
            if (te <= ts) return 0;

            int segCount = closed ? polyCount : polyCount - 1;
            if (segCount <= 0) return 0;

            float total = 0f;
            for (int i = 0; i < segCount; i++)
            {
                float2 a = polyBuf[i];
                float2 b = polyBuf[(i + 1) % polyCount];
                total += math.distance(a, b);
            }
            if (total < 1e-6f) return 0;

            float startArc = ts * total;
            float endArc = te * total;

            int scratchCap = scratch.Length;
            int outCount = 0;
            float walked = 0f;
            bool started = false;
            for (int i = 0; i < segCount; i++)
            {
                float2 a = polyBuf[i];
                float2 b = polyBuf[(i + 1) % polyCount];
                float segLen = math.distance(a, b);
                if (segLen < 1e-8f) continue;
                float segEnd = walked + segLen;
                if (segEnd < startArc) { walked = segEnd; continue; }
                if (!started)
                {
                    float t0 = (startArc - walked) / segLen;
                    if (outCount < scratchCap) scratch[outCount++] = math.lerp(a, b, math.saturate(t0));
                    started = true;
                }
                if (segEnd >= endArc)
                {
                    float t1 = (endArc - walked) / segLen;
                    if (outCount < scratchCap) scratch[outCount++] = math.lerp(a, b, math.saturate(t1));
                    break;
                }
                if (outCount < scratchCap) scratch[outCount++] = b;
                walked = segEnd;
            }
            // Defensive: outCount can't exceed polyBuf capacity since we capped each
            // write against scratchCap and scratch was sized to ≥ polyBuf.Length by
            // the caller.
            for (int i = 0; i < outCount; i++) polyBuf[i] = scratch[i];
            return outCount;
        }

        // -------- Rect / Ellipse tessellation --------

        private void BuildRectPoly(LottieShape s, float t, NativeArray<float2> polyBuf, ref int polyCount)
        {
            polyCount = 0;
            float2 center = EvalVec2(s.P0, t);
            float2 size = EvalVec2(s.P1, t);
            float r = math.max(0f, EvalScalar(s.P2, t, 0f));
            float hx = size.x * 0.5f;
            float hy = size.y * 0.5f;
            r = math.min(r, math.min(hx, hy));

            if (r <= 0.001f)
            {
                polyBuf[polyCount++] = center + new float2(-hx, -hy);
                polyBuf[polyCount++] = center + new float2(hx, -hy);
                polyBuf[polyCount++] = center + new float2(hx, hy);
                polyBuf[polyCount++] = center + new float2(-hx, hy);
            }
            else
            {
                const int seg = 8;
                AppendArc(polyBuf, ref polyCount, center + new float2(hx - r, hy - r), r, 0f, math.PI * 0.5f, seg);
                AppendArc(polyBuf, ref polyCount, center + new float2(-hx + r, hy - r), r, math.PI * 0.5f, math.PI, seg);
                AppendArc(polyBuf, ref polyCount, center + new float2(-hx + r, -hy + r), r, math.PI, math.PI * 1.5f, seg);
                AppendArc(polyBuf, ref polyCount, center + new float2(hx - r, -hy + r), r, math.PI * 1.5f, math.PI * 2f, seg);
            }
        }

        private void BuildEllipsePoly(LottieShape s, float t, NativeArray<float2> polyBuf, ref int polyCount)
        {
            polyCount = 0;
            float2 center = EvalVec2(s.P0, t);
            float2 size = EvalVec2(s.P1, t);
            float rx = size.x * 0.5f, ry = size.y * 0.5f;
            int segs = math.clamp((int)(math.max(rx, ry) * 1.5f), 16, 64);
            for (int i = 0; i < segs; i++)
            {
                float a = (i / (float)segs) * math.PI * 2f;
                if (polyCount < polyBuf.Length)
                    polyBuf[polyCount++] = center + new float2(math.cos(a) * rx, math.sin(a) * ry);
            }
        }

        private static void AppendArc(NativeArray<float2> poly, ref int count, float2 center, float r, float a0, float a1, int seg)
        {
            for (int i = 0; i < seg; i++)
            {
                float a = math.lerp(a0, a1, i / (float)seg);
                if (count < poly.Length) poly[count++] = center + new float2(math.cos(a) * r, math.sin(a) * r);
            }
        }

        // -------- Property evaluation (scalar / vec2 / color / keyframe pair) --------

        private float EvalScalar(int propIdx, float t, float def)
        {
            if (propIdx < 0 || propIdx >= Properties.Length) return def;
            var p = Properties[propIdx];
            if (p.Kind == LottiePropertyKind.Constant) return ScalarPool[p.ValueIndex];
            if (p.Kind != LottiePropertyKind.AnimatedScalar || p.KeyframeCount == 0) return def;
            ResolveKeyframePair(p.KeyframeOffset, p.KeyframeCount, t, out int ia, out int ib, out float blend);
            float a = ScalarPool[Keyframes[p.KeyframeOffset + ia].ValueOffset];
            float b = ScalarPool[Keyframes[p.KeyframeOffset + ib].ValueOffset];
            return math.lerp(a, b, blend);
        }

        private float2 EvalVec2(int propIdx, float t)
        {
            if (propIdx < 0 || propIdx >= Properties.Length) return float2.zero;
            var p = Properties[propIdx];
            if (p.Kind == LottiePropertyKind.Constant) return Vec2Pool[p.ValueIndex];
            if (p.Kind != LottiePropertyKind.AnimatedVec2 || p.KeyframeCount == 0) return float2.zero;
            ResolveKeyframePair(p.KeyframeOffset, p.KeyframeCount, t, out int ia, out int ib, out float blend);
            float2 a = Vec2Pool[Keyframes[p.KeyframeOffset + ia].ValueOffset];
            float2 b = Vec2Pool[Keyframes[p.KeyframeOffset + ib].ValueOffset];
            return math.lerp(a, b, blend);
        }

        private float2 EvalVec2Pct(int propIdx, float t)
        {
            if (propIdx < 0 || propIdx >= Properties.Length) return new float2(1f, 1f);
            return EvalVec2(propIdx, t); // already stored normalized by parser
        }

        private float4 EvalColor(int propIdx, float t)
        {
            if (propIdx < 0 || propIdx >= Properties.Length) return new float4(1f, 1f, 1f, 1f);
            var p = Properties[propIdx];
            if (p.Kind == LottiePropertyKind.Constant) return Vec4Pool[p.ValueIndex];
            if (p.Kind != LottiePropertyKind.AnimatedVec4 || p.KeyframeCount == 0) return new float4(1f, 1f, 1f, 1f);
            ResolveKeyframePair(p.KeyframeOffset, p.KeyframeCount, t, out int ia, out int ib, out float blend);
            float4 a = Vec4Pool[Keyframes[p.KeyframeOffset + ia].ValueOffset];
            float4 b = Vec4Pool[Keyframes[p.KeyframeOffset + ib].ValueOffset];
            return math.lerp(a, b, blend);
        }

        private void ResolveKeyframePair(int kfOffset, int kfCount, float t, out int idxA, out int idxB, out float blend)
        {
            idxA = 0; idxB = 0; blend = 0f;
            if (kfCount <= 0) return;
            if (t <= Keyframes[kfOffset + 0].Time) { idxA = 0; idxB = 0; return; }
            if (t >= Keyframes[kfOffset + kfCount - 1].Time) { idxA = kfCount - 1; idxB = kfCount - 1; return; }
            for (int i = 0; i < kfCount - 1; i++)
            {
                var ka = Keyframes[kfOffset + i];
                var kb = Keyframes[kfOffset + i + 1];
                if (t >= ka.Time && t < kb.Time)
                {
                    idxA = i; idxB = i + 1;
                    if (ka.Hold != 0) { blend = 0f; return; }
                    float span = math.max(0.0001f, kb.Time - ka.Time);
                    float u = (t - ka.Time) / span;
                    if (ka.HasOutTan != 0 && kb.HasInTan != 0)
                    {
                        float ox = ScalarPool[ka.OutTanIndex];
                        float oy = ScalarPool[ka.OutTanIndex + 1];
                        float ix = ScalarPool[kb.InTanIndex];
                        float iy = ScalarPool[kb.InTanIndex + 1];
                        blend = BezierMath.Ease(u, ox, oy, ix, iy);
                    }
                    else
                    {
                        blend = u;
                    }
                    return;
                }
            }
        }
    }
}
