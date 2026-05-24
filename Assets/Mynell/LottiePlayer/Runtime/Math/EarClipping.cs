// Mynell Lottie Player — Burst-compatible ear-clipping triangulator.
// O(n^2) — perfectly fine for typical Lottie polygons (< 256 verts post-flatten).
// Operates on a NativeArray<float2> polygon and writes triangle indices to outIndices.

using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Mynell.LottiePlayer
{
    [BurstCompile]
    public static class EarClipping
    {
        /// <summary>Signed polygon area * 2. Positive = CCW.</summary>
        public static float SignedArea2(NativeArray<float2> poly, int start, int count)
        {
            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                float2 a = poly[start + i];
                float2 b = poly[start + ((i + 1) % count)];
                sum += a.x * b.y - b.x * a.y;
            }
            return sum;
        }

        /// <summary>
        /// Triangulate a simple polygon (verts[start..start+count]).
        /// Appends 3 indices per triangle to <paramref name="outIndices"/> via <paramref name="outCount"/>.
        /// Indices are absolute (start + local).
        /// </summary>
        public static void Triangulate(
            NativeArray<float2> poly, int start, int count,
            NativeArray<int> outIndices, ref int outCount,
            NativeArray<int> scratchPrev, NativeArray<int> scratchNext,
            NativeArray<byte> scratchAlive)
        {
            if (count < 3) return;

            for (int i = 0; i < count; i++)
            {
                scratchPrev[i] = (i + count - 1) % count;
                scratchNext[i] = (i + 1) % count;
                scratchAlive[i] = 1;
            }

            float area2 = SignedArea2(poly, start, count);
            bool ccw = area2 > 0f;
            if (!ccw)
            {
                for (int i = 0; i < count; i++)
                {
                    scratchPrev[i] = (i + 1) % count;
                    scratchNext[i] = (i + count - 1) % count;
                }
            }

            int remaining = count;
            int curr = 0;
            int guard = count * count + 4;

            while (remaining > 3 && guard-- > 0)
            {
                int prev = scratchPrev[curr];
                int next = scratchNext[curr];

                if (IsEar(poly, start, prev, curr, next, scratchAlive, scratchNext, count))
                {
                    if (outCount + 3 <= outIndices.Length)
                    {
                        outIndices[outCount++] = start + prev;
                        outIndices[outCount++] = start + curr;
                        outIndices[outCount++] = start + next;
                    }
                    scratchAlive[curr] = 0;
                    scratchNext[prev] = next;
                    scratchPrev[next] = prev;
                    remaining--;
                    curr = next;
                }
                else
                {
                    curr = next;
                }
            }

            if (remaining > 3 && guard <= 0)
                WarnFailsafe();

            if (remaining == 3 && outCount + 3 <= outIndices.Length)
            {
                int prev = scratchPrev[curr];
                int next = scratchNext[curr];
                outIndices[outCount++] = start + prev;
                outIndices[outCount++] = start + curr;
                outIndices[outCount++] = start + next;
            }
        }

        [BurstDiscard]
        private static void WarnFailsafe()
        {
            Debug.LogWarning("EarClipping Failsafe Triggered! Triangulation failed for a shape.");
        }

        private static bool IsEar(
            NativeArray<float2> poly, int start,
            int prev, int curr, int next,
            NativeArray<byte> alive, NativeArray<int> nxt, int total)
        {
            float2 a = poly[start + prev];
            float2 b = poly[start + curr];
            float2 c = poly[start + next];

            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (cross <= 0f) return false;

            int v = nxt[next];
            int guard = total + 4;
            while (v != prev && guard-- > 0)
            {
                if (alive[v] != 0)
                {
                    float2 p = poly[start + v];
                    if (PointInTriangle(p, a, b, c)) return false;
                }
                v = nxt[v];
            }
            return true;
        }

        private static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }

        private static float Sign(float2 p1, float2 p2, float2 p3)
            => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }
}
