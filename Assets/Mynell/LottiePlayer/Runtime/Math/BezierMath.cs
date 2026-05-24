// Mynell Lottie Player — Burst-compatible bezier math.
// All routines: zero allocations, no exceptions, operate on NativeArrays / float2.

using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Mynell.LottiePlayer
{
    [BurstCompile]
    public static class BezierMath
    {
        /// <summary>Cubic-bezier easing (Lottie 'i'/'o' tangents). t in [0,1] → eased.</summary>
        public static float Ease(float t, float x1, float y1, float x2, float y2)
        {
            // Standard CSS cubic-bezier (P0=0, P1=(x1,y1), P2=(x2,y2), P3=1).
            // Newton-Raphson on x(t)=u, returns y(u). 4 iters is plenty for animation.
            float u = t;
            for (int i = 0; i < 4; i++)
            {
                float omu = 1f - u;
                float bx = 3f * omu * omu * u * x1 + 3f * omu * u * u * x2 + u * u * u;
                float dbx = 3f * omu * omu * x1 + 6f * omu * u * (x2 - x1) + 3f * u * u * (1f - x2);
                float diff = bx - t;
                if (math.abs(dbx) < 1e-6f) break;
                u -= diff / dbx;
                u = math.saturate(u);
            }
            float omu2 = 1f - u;
            return 3f * omu2 * omu2 * u * y1 + 3f * omu2 * u * u * y2 + u * u * u;
        }

        /// <summary>Cubic bezier point at parameter t.</summary>
        public static float2 CubicPoint(float2 p0, float2 c0, float2 c1, float2 p1, float t)
        {
            float omt = 1f - t;
            float b0 = omt * omt * omt;
            float b1 = 3f * omt * omt * t;
            float b2 = 3f * omt * t * t;
            float b3 = t * t * t;
            return b0 * p0 + b1 * c0 + b2 * c1 + b3 * p1;
        }

        /// <summary>
        /// Flatten a cubic bezier into a polyline by recursive midpoint subdivision,
        /// iterative (Burst-safe). Appends to <paramref name="outVerts"/> via <paramref name="outCount"/>.
        /// Does NOT append the final point p1 (caller appends to avoid duplication on multi-segment paths).
        /// </summary>
        public static void FlattenCubic(
            float2 p0, float2 c0, float2 c1, float2 p1,
            float tolerancePx,
            NativeArray<float2> outVerts,
            ref int outCount,
            int maxDepth = 12)
        {
            // Stack-based subdivision. Each entry: (p0, c0, c1, p1, depth).
            // Capacity 64 == 12 levels deep on a single branch is plenty.
            const int Cap = 64;
            var stackP0 = new NativeArray<float2>(Cap, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var stackC0 = new NativeArray<float2>(Cap, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var stackC1 = new NativeArray<float2>(Cap, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var stackP1 = new NativeArray<float2>(Cap, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var stackD  = new NativeArray<int>(Cap, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            int sp = 0;
            stackP0[sp] = p0; stackC0[sp] = c0; stackC1[sp] = c1; stackP1[sp] = p1; stackD[sp] = 0; sp++;

            float tol2 = tolerancePx * tolerancePx;

            while (sp > 0)
            {
                sp--;
                float2 q0 = stackP0[sp], qc0 = stackC0[sp], qc1 = stackC1[sp], q1 = stackP1[sp];
                int depth = stackD[sp];

                // Flatness: max distance from c0, c1 to chord q0-q1.
                float2 chord = q1 - q0;
                float chordLen2 = math.lengthsq(chord);
                bool flat;
                if (chordLen2 < 1e-12f)
                {
                    flat = math.lengthsq(qc0 - q0) < tol2 && math.lengthsq(qc1 - q0) < tol2;
                }
                else
                {
                    float invLen = math.rsqrt(chordLen2);
                    float2 nrm = new float2(-chord.y, chord.x) * invLen;
                    float d0 = math.dot(qc0 - q0, nrm);
                    float d1 = math.dot(qc1 - q0, nrm);
                    flat = (d0 * d0 < tol2) && (d1 * d1 < tol2);
                }

                if (flat || depth >= maxDepth)
                {
                    if (outCount < outVerts.Length) { outVerts[outCount] = q0; outCount++; }
                    continue;
                }

                // De Casteljau split at t=0.5.
                float2 m0 = 0.5f * (q0 + qc0);
                float2 m1 = 0.5f * (qc0 + qc1);
                float2 m2 = 0.5f * (qc1 + q1);
                float2 m01 = 0.5f * (m0 + m1);
                float2 m12 = 0.5f * (m1 + m2);
                float2 mid = 0.5f * (m01 + m12);

                if (sp + 2 <= Cap)
                {
                    // Right half (push first so left is processed first).
                    stackP0[sp] = mid; stackC0[sp] = m12; stackC1[sp] = m2; stackP1[sp] = q1; stackD[sp] = depth + 1; sp++;
                    // Left half.
                    stackP0[sp] = q0;  stackC0[sp] = m0;  stackC1[sp] = m01; stackP1[sp] = mid; stackD[sp] = depth + 1; sp++;
                }
                else
                {
                    if (outCount < outVerts.Length) { outVerts[outCount] = q0; outCount++; }
                }
            }

            stackP0.Dispose(); stackC0.Dispose(); stackC1.Dispose(); stackP1.Dispose(); stackD.Dispose();
        }
    }
}
