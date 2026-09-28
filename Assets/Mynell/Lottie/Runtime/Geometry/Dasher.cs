using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Geometry
{
    /// <summary>SVG stroke-dasharray / stroke-dashoffset applied to polylines (pattern restarts per subpath).</summary>
    public static unsafe class Dasher
    {
        /// <summary>
        /// Splits one polyline into dash polylines (always open). Zero-length dashes are emitted as a tiny
        /// segment along the local tangent so caps keep their orientation.
        /// </summary>
        public static void Dash(float2* pts, int count, bool closed, float* pattern, int patternCount, float offset,
            ref UnsafeList<float2> outPts, ref UnsafeList<Poly> outPolys)
        {
            if (count < 2 || patternCount <= 0) return;
            float total = 0;
            for (int i = 0; i < patternCount; i++) total += pattern[i];
            if (total <= 0) return;

            // Starting state from the offset.
            float off = offset % total;
            if (off < 0) off += total;
            int idx = 0;
            int guard = 0;
            while (off >= pattern[idx] && guard++ < patternCount * 4)
            {
                off -= pattern[idx];
                idx = (idx + 1) % patternCount;
            }
            float remaining = pattern[idx] - off; // remaining length in the current dash/gap
            bool on = (idx & 1) == 0;

            int segCount = closed ? count : count - 1;
            int start = -1;
            if (on) { start = outPts.Length; outPts.Add(pts[0]); }
            for (int s = 0; s < segCount; s++)
            {
                float2 a = pts[s];
                float2 b = pts[s + 1 == count ? 0 : s + 1];
                float segLen = math.distance(a, b);
                if (segLen <= 0) continue;
                float2 dir = (b - a) / segLen;
                float pos = 0;
                while (segLen - pos > remaining)
                {
                    pos += remaining;
                    float2 p = a + dir * pos;
                    if (on)
                    {
                        outPts.Add(p);
                        EndDash(ref outPts, ref outPolys, start, dir);
                        start = -1;
                    }
                    else
                    {
                        start = outPts.Length;
                        outPts.Add(p);
                    }
                    on = !on;
                    idx = (idx + 1) % patternCount;
                    remaining = pattern[idx];
                    // Zero-length dash: immediately ends (handled by EndDash producing a dot).
                }
                remaining -= segLen - pos;
                if (on) outPts.Add(b);
            }
            if (on && start >= 0)
            {
                float2 dir = float2.zero;
                int last = outPts.Length - 1;
                if (last > start) dir = math.normalizesafe(outPts[last] - outPts[last - 1]);
                EndDash(ref outPts, ref outPolys, start, dir);
            }
        }

        static void EndDash(ref UnsafeList<float2> outPts, ref UnsafeList<Poly> outPolys, int start, float2 dir)
        {
            int cnt = outPts.Length - start;
            if (cnt <= 0) return;
            if (cnt == 1 || AllSame(ref outPts, start, cnt))
            {
                // Degenerate dash: keep the orientation with a tiny segment.
                float2 p = outPts[start];
                outPts.Length = start;
                outPts.Add(p);
                outPts.Add(p + dir * 1e-3f);
                cnt = 2;
            }
            outPolys.Add(new Poly { First = start, Count = cnt, Closed = 0 });
        }

        static bool AllSame(ref UnsafeList<float2> pts, int start, int cnt)
        {
            for (int i = start + 1; i < start + cnt; i++)
                if (math.any(pts[i] != pts[start])) return false;
            return true;
        }
    }
}
