// Mynell Lottie Player — Phase E Stage 2 (parallel, zero per-frame GC):
// per-command tessellation with persistent per-thread scratch slices.
//
// Each iteration of this IJobParallelFor consumes one DrawCommand produced by
// BuildDrawCommandsJob and writes EXACTLY into its prefix-summed disjoint slot
// inside OutVertices / OutIndices. No atomics, no mutexes, no contention.
//
// Phase D used Allocator.Temp scratchpads (cheap per-thread bump alloc, but still
// non-zero). Phase E migrates them to a single persistent NativeArray partitioned
// by [NativeSetThreadIndex]. Each worker thread receives its own private slice
// within the global buffer — guaranteed-disjoint by construction, zero allocations
// per frame, zero GC, zero allocator chatter.

using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Mynell.LottiePlayer
{
    [BurstCompile(FloatPrecision.Standard, FloatMode.Default)]
    public struct TessellateCommandsJob : IJobParallelFor
    {
        // ----------------- Inputs (read-only) -----------------
        [ReadOnly] public NativeArray<DrawCommand> Commands;
        [ReadOnly] public NativeArray<float2> PolyPool;

        // ----------------- Per-thread persistent scratch (Phase E) -----------------
        // These four arrays are pre-allocated once by the component (Allocator.Persistent)
        // and sized as JobsUtility.MaxJobThreadCount * MaxVerticesPerShape (or *(n-2)*3
        // for the index buffer). Each parallel iteration gets a private slice keyed by
        // m_ThreadIndex below, so two threads never share memory.
        // [NativeDisableParallelForRestriction] lifts the per-iteration safety check
        // (we slice by *thread*, not by *iteration index*).
        [NativeDisableParallelForRestriction] public NativeArray<int> ScratchPrev;
        [NativeDisableParallelForRestriction] public NativeArray<int> ScratchNext;
        [NativeDisableParallelForRestriction] public NativeArray<byte> ScratchAlive;
        [NativeDisableParallelForRestriction] public NativeArray<int> ScratchLocalIdx;

        /// <summary>Per-thread polyline capacity. Drives slice arithmetic below.</summary>
        public int MaxVerticesPerShape;

        /// <summary>When set, every emitted vertex uses opaque white (visibility debug).</summary>
        public byte ForceOpaqueWhite;

        // ----------------- Outputs -----------------
        // [NativeDisableParallelForRestriction] is REQUIRED: each iteration writes a
        // *range* of slots (not just slot[index]). Stage 1 already guarantees those
        // ranges are disjoint via prefix-sum, so the lifted safety check is sound.
        [NativeDisableParallelForRestriction]
        [WriteOnly] public NativeArray<VertexLayout> OutVertices;
        [NativeDisableParallelForRestriction]
        [WriteOnly] public NativeArray<ushort> OutIndices;

        // ----------------- Thread routing -----------------
        // Auto-populated by the job system before each Execute() call. Range is
        // [0, JobsUtility.MaxJobThreadCount). Under .Run() it falls back to the main
        // thread's index — still valid, still in range, still partitioning-safe
        // because all iterations execute serially in that mode.
#pragma warning disable CS0649 // assigned by Unity via [NativeSetThreadIndex]
        [NativeSetThreadIndex] private int m_ThreadIndex;
#pragma warning restore CS0649

        public void Execute(int index)
        {
            var cmd = Commands[index];
            if (cmd.Kind == DrawCommandKind.Fill)
                ExecuteFill(in cmd);
            else
                ExecuteStroke(in cmd);
        }

        // ============================== Fill (ear-clipping) ==============================

        private void ExecuteFill(in DrawCommand cmd)
        {
            if (cmd.PolyCount < 3 || cmd.IndexCount <= 0)
            {
                FillDegenerate(in cmd);
                return;
            }

            // Stage 1 already gates against MaxVerticesPerShape, so we should never see
            // a command that exceeds the per-thread slice. Defensive recheck still
            // protects against future API misuse.
            if (cmd.PolyCount > MaxVerticesPerShape)
            {
                FillDegenerate(in cmd);
                return;
            }

            // Compute disjoint per-thread slice offsets — pure index arithmetic, no
            // bounds checks (Burst elides them on release).
            int polyBase = m_ThreadIndex * MaxVerticesPerShape;
            int idxBase  = m_ThreadIndex * IndexScratchSizePerThread(MaxVerticesPerShape);

            // Build NativeArray sub-views over the persistent buffers. GetSubArray is
            // a no-copy alias that Burst lowers to plain pointer arithmetic.
            var scratchPrev  = ScratchPrev .GetSubArray(polyBase, cmd.PolyCount);
            var scratchNext  = ScratchNext .GetSubArray(polyBase, cmd.PolyCount);
            var scratchAlive = ScratchAlive.GetSubArray(polyBase, cmd.PolyCount);
            var localIdx     = ScratchLocalIdx.GetSubArray(idxBase, cmd.IndexCount);
            int localCount = 0;

            // Triangulator writes polyline-relative indices [0..PolyCount). We post-offset
            // them by VertexOffset when copying into the global OutIndices.
            EarClipping.Triangulate(PolyPool, cmd.PolyOffset, cmd.PolyCount,
                localIdx, ref localCount,
                scratchPrev, scratchNext, scratchAlive);

            // Vertices: transform each polyline point through cmd.World once.
            float2 uv = new float2(0.5f, 0.5f);
            Color32 vertexColor = ResolveVertexColor(cmd.Color);
            for (int v = 0; v < cmd.PolyCount; v++)
            {
                float2 p = cmd.World.Apply(PolyPool[cmd.PolyOffset + v]);
                OutVertices[cmd.VertexOffset + v] = new VertexLayout
                {
                    Position = new float3(p, 0f),
                    Color = vertexColor,
                    UV = uv,
                };
            }

            // EarClipping emits poly-pool indices (PolyOffset + local). Remap to global
            // vertex-buffer indices via VertexOffset + local vertex index.
            int writeAt = cmd.IndexOffset;
            for (int k = 0; k < localCount; k++)
            {
                int localIndex = localIdx[k] - cmd.PolyOffset;
                OutIndices[writeAt + k] = (ushort)(cmd.VertexOffset + localIndex);
            }
            for (int k = localCount; k < cmd.IndexCount; k++)
                OutIndices[writeAt + k] = (ushort)cmd.VertexOffset;
        }

        // ============================== Stroke (quad extrusion) ==============================

        private void ExecuteStroke(in DrawCommand cmd)
        {
            bool closed = cmd.Closed != 0;
            int segCount = closed ? cmd.PolyCount : cmd.PolyCount - 1;
            if (segCount < 1)
            {
                FillDegenerate(in cmd);
                return;
            }
            float halfW = cmd.StrokeWidth * 0.5f;
            float2 uv = new float2(0.5f, 0.5f);
            Color32 vertexColor = ResolveVertexColor(cmd.Color);

            // Transform thickness with the world matrix's local x-axis magnitude. This
            // matches a "consistent 2D stroke" — full affine xform of the offset would
            // bend strokes under non-uniform scale; the current single-job pipeline uses
            // the same model.
            // (We pre-extrude in local space then xform the four corners — gives the
            // correct screen-space stroke for uniform scale; non-uniform scale produces
            // the standard Lottie behaviour.)

            for (int i = 0; i < segCount; i++)
            {
                int i0 = i;
                int i1 = closed ? ((i + 1) % cmd.PolyCount) : (i + 1);
                float2 p0 = PolyPool[cmd.PolyOffset + i0];
                float2 p1 = PolyPool[cmd.PolyOffset + i1];
                float2 d = p1 - p0;
                float len2 = math.lengthsq(d);

                int baseV = cmd.VertexOffset + i * 4;
                int baseI = cmd.IndexOffset + i * 6;

                if (len2 < 1e-8f)
                {
                    // Should be rare — Stage 1's CompactPoly already removed near-coincident
                    // points. Defensive fallback: zero-area quad at p0.
                    float2 q = cmd.World.Apply(p0);
                    var v = new VertexLayout { Position = new float3(q, 0f), Color = vertexColor, UV = uv };
                    OutVertices[baseV + 0] = v;
                    OutVertices[baseV + 1] = v;
                    OutVertices[baseV + 2] = v;
                    OutVertices[baseV + 3] = v;
                }
                else
                {
                    float2 dir = d * math.rsqrt(len2);
                    float2 nrm = new float2(-dir.y, dir.x) * halfW;

                    float2 a = cmd.World.Apply(p0 + nrm);
                    float2 b = cmd.World.Apply(p1 + nrm);
                    float2 c = cmd.World.Apply(p1 - nrm);
                    float2 e = cmd.World.Apply(p0 - nrm);

                    OutVertices[baseV + 0] = new VertexLayout { Position = new float3(a, 0f), Color = vertexColor, UV = uv };
                    OutVertices[baseV + 1] = new VertexLayout { Position = new float3(b, 0f), Color = vertexColor, UV = uv };
                    OutVertices[baseV + 2] = new VertexLayout { Position = new float3(c, 0f), Color = vertexColor, UV = uv };
                    OutVertices[baseV + 3] = new VertexLayout { Position = new float3(e, 0f), Color = vertexColor, UV = uv };
                }

                OutIndices[baseI + 0] = (ushort)(baseV + 0);
                OutIndices[baseI + 1] = (ushort)(baseV + 1);
                OutIndices[baseI + 2] = (ushort)(baseV + 2);
                OutIndices[baseI + 3] = (ushort)(baseV + 0);
                OutIndices[baseI + 4] = (ushort)(baseV + 2);
                OutIndices[baseI + 5] = (ushort)(baseV + 3);
            }
        }

        // ============================== Defensive fallback ==============================

        /// <summary>
        /// Fill the entire reserved range with zeroed verts and self-referential indices.
        /// Used when a command's PolyCount turns out to be unrenderable in Stage 2 (e.g.
        /// fewer than 3 points for a fill). Keeps the vertex buffer free of NaN / garbage.
        /// </summary>
        private void FillDegenerate(in DrawCommand cmd)
        {
            var v = new VertexLayout
            {
                Position = float3.zero,
                Color = ResolveVertexColor(cmd.Color),
                UV = new float2(0.5f, 0.5f),
            };
            for (int i = 0; i < cmd.VertexCount; i++)
                OutVertices[cmd.VertexOffset + i] = v;
            for (int i = 0; i < cmd.IndexCount; i++)
                OutIndices[cmd.IndexOffset + i] = (ushort)cmd.VertexOffset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Color32 ResolveVertexColor(uint packed)
        {
            if (ForceOpaqueWhite != 0)
                return VertexLayout.OpaqueWhite;
            return UnpackColor(packed);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Color32 UnpackColor(uint packed)
        {
            byte r = (byte)(packed & 0xFFu);
            byte g = (byte)((packed >> 8) & 0xFFu);
            byte b = (byte)((packed >> 16) & 0xFFu);
            byte a = (byte)((packed >> 24) & 0xFFu);
            if (a == 0)
                a = 255;
            return new Color32(r, g, b, a);
        }

        /// <summary>
        /// Per-thread localIdx slice size. An ear-clipped polygon of N vertices yields
        /// at most (N-2) triangles = (N-2)*3 indices. Public so the component can size
        /// the persistent buffer the same way Stage 2 reads from it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexScratchSizePerThread(int maxVerticesPerShape)
        {
            // Math.max(0,...) guards against absurdly small caps.
            int t = maxVerticesPerShape - 2;
            return t < 0 ? 0 : t * 3;
        }
    }
}
