// Mynell Lottie Player — Burst-compatible polygon triangulation.
// Ear-clipping for concave polygons (≤ EarClipMaxVerts), fan fallback otherwise / on failure.

using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Mynell.LottiePlayer
{
    [BurstCompile]
    public static class EarClipping
    {
        /// <summary>Above this vertex count, skip O(n²) ear clip and use fan triangulation.</summary>
        public const int EarClipMaxVerts = 64;

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
        /// Triangulate a simple polygon. Uses ear clipping for small polys, fan for large ones.
        /// Falls back to fan when ear clipping yields zero triangles (self-intersecting / degenerate).
        /// </summary>
        public static void Triangulate(
            NativeArray<float2> poly, int start, int count,
            NativeArray<int> outIndices, ref int outCount,
            NativeArray<int> scratchPrev, NativeArray<int> scratchNext,
            NativeArray<byte> scratchAlive)
        {
            if (count < 3) return;

            if (count > EarClipMaxVerts)
            {
                FanTriangulate(start, count, outIndices, ref outCount);
                return;
            }

            EarClipTriangulate(poly, start, count, outIndices, ref outCount, scratchPrev, scratchNext, scratchAlive);

            if (outCount == 0)
                FanTriangulate(start, count, outIndices, ref outCount);
        }

        /// <summary>O(n) fan from vertex 0 — robust fallback for dense / degenerate polys.</summary>
        public static void FanTriangulate(int start, int count, NativeArray<int> outIndices, ref int outCount)
        {
            if (count < 3) return;
            for (int i = 1; i < count - 1; i++)
            {
                if (outCount + 3 > outIndices.Length) return;
                outIndices[outCount++] = start;
                outIndices[outCount++] = start + i;
                outIndices[outCount++] = start + i + 1;
            }
        }

        private static void EarClipTriangulate(
            NativeArray<float2> poly, int start, int count,
            NativeArray<int> outIndices, ref int outCount,
            NativeArray<int> scratchPrev, NativeArray<int> scratchNext,
            NativeArray<byte> scratchAlive)
        {
            for (int i = 0; i < count; i++)
            {
                scratchPrev[i] = (i + count - 1) % count;
                scratchNext[i] = (i + 1) % count;
                scratchAlive[i] = 1;
            }

            float area2 = SignedArea2(poly, start, count);
            if (math.abs(area2) < 1e-8f) return;

            if (area2 <= 0f)
            {
                for (int i = 0; i < count; i++)
                {
                    scratchPrev[i] = (i + 1) % count;
                    scratchNext[i] = (i + count - 1) % count;
                }
            }

            int remaining = count;
            int curr = 0;

            while (remaining > 2)
            {
                bool earClipped = false;
                int probe = curr;
                for (int pass = 0; pass < remaining; pass++)
                {
                    if (scratchAlive[probe] == 0)
                    {
                        probe = scratchNext[probe];
                        continue;
                    }

                    int prev = scratchPrev[probe];
                    int next = scratchNext[probe];

                    if (IsEar(poly, start, prev, probe, next, scratchAlive, scratchNext, remaining))
                    {
                        if (outCount + 3 <= outIndices.Length)
                        {
                            outIndices[outCount++] = start + prev;
                            outIndices[outCount++] = start + probe;
                            outIndices[outCount++] = start + next;
                        }
                        scratchAlive[probe] = 0;
                        scratchNext[prev] = next;
                        scratchPrev[next] = prev;
                        remaining--;
                        curr = next;
                        earClipped = true;
                        break;
                    }

                    probe = next;
                }

                if (!earClipped)
                    break;
            }

            if (remaining == 3 && outCount + 3 <= outIndices.Length)
            {
                int prev = scratchPrev[curr];
                int next = scratchNext[curr];
                outIndices[outCount++] = start + prev;
                outIndices[outCount++] = start + curr;
                outIndices[outCount++] = start + next;
            }
        }

        private static bool IsEar(
            NativeArray<float2> poly, int start,
            int prev, int curr, int next,
            NativeArray<byte> alive, NativeArray<int> nxt, int remaining)
        {
            if (alive[prev] == 0 || alive[curr] == 0 || alive[next] == 0) return false;

            float2 a = poly[start + prev];
            float2 b = poly[start + curr];
            float2 c = poly[start + next];

            float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
            if (cross <= 0f) return false;

            int v = nxt[next];
            int walked = 0;
            while (v != prev && walked < remaining)
            {
                if (alive[v] != 0)
                {
                    float2 p = poly[start + v];
                    if (PointInTriangle(p, a, b, c)) return false;
                }
                v = nxt[v];
                walked++;
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
