using Mynell.Lottie.Eval;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Geometry
{
    /// <summary>A flattened contour: points range in a float2 list.</summary>
    public struct Poly
    {
        public int First;
        public int Count;
        public byte Closed;
        public byte R0; public ushort R1;
    }

    public static unsafe class Flattener
    {
        /// <summary>
        /// Number of line segments needed so a cubic stays within <paramref name="tolerance"/> (Wang's formula).
        /// </summary>
        public static int SegmentCount(float2 p0, float2 p1, float2 p2, float2 p3, float tolerance)
        {
            float2 d1 = p0 - 2 * p1 + p2;
            float2 d2 = p1 - 2 * p2 + p3;
            float m = math.max(math.lengthsq(d1), math.lengthsq(d2));
            if (m <= 1e-12f) return 1;
            float n = math.sqrt(0.75f * math.sqrt(m) / tolerance);
            return math.clamp((int)math.ceil(n), 1, 512);
        }

        /// <summary>
        /// Flattens bezier contours (v, in, out triples) through transform <paramref name="m"/> into polylines.
        /// Duplicate consecutive points are removed. Closed contours don't repeat the first point.
        /// </summary>
        public static void Flatten(float2* pts, int first, int count, bool closed, in Affine2 m, float tolerance,
            ref UnsafeList<float2> outPts, ref UnsafeList<Poly> outPolys)
        {
            if (count <= 0) return;
            int start = outPts.Length;
            float2 v0 = m.Apply(pts[first]);
            outPts.Add(v0);
            int segs = closed ? count : count - 1;
            for (int s = 0; s < segs; s++)
            {
                int a = s, b = s + 1 == count ? 0 : s + 1;
                float2 p0 = m.Apply(pts[first + a * 3]);
                float2 p1 = m.Apply(pts[first + a * 3 + 2]);
                float2 p2 = m.Apply(pts[first + b * 3 + 1]);
                float2 p3 = m.Apply(pts[first + b * 3]);
                bool line = math.all(p1 == p0) && math.all(p2 == p3);
                if (line)
                {
                    AddPoint(ref outPts, start, p3);
                    continue;
                }
                int n = SegmentCount(p0, p1, p2, p3, tolerance);
                float dt = 1f / n;
                for (int i = 1; i <= n; i++)
                {
                    float t = i == n ? 1f : i * dt;
                    float u = 1 - t;
                    float2 p = u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
                    AddPoint(ref outPts, start, i == n ? p3 : p);
                }
            }
            int cnt = outPts.Length - start;
            if (closed && cnt > 1 && math.all(outPts[outPts.Length - 1] == outPts[start]))
            {
                outPts.Length = outPts.Length - 1;
                cnt--;
            }
            outPolys.Add(new Poly { First = start, Count = cnt, Closed = (byte)(closed ? 1 : 0) });
        }

        static void AddPoint(ref UnsafeList<float2> pts, int start, float2 p)
        {
            if (pts.Length > start && math.all(pts[pts.Length - 1] == p)) return;
            pts.Add(p);
        }
    }
}
