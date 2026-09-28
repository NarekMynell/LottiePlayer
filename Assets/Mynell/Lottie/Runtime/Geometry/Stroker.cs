using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Geometry
{
    /// <summary>
    /// Converts polylines into stroke outline polygons (SVG semantics: butt/round/square caps,
    /// miter/round/bevel joins with miter limit). The outlines are meant to be filled with the
    /// nonzero rule; all produced polygons share the same orientation.
    /// </summary>
    public static unsafe class Stroker
    {
        public const byte CapButt = 1, CapRound = 2, CapSquare = 3;
        public const byte JoinMiter = 1, JoinRound = 2, JoinBevel = 3;

        public struct Style
        {
            public float HalfWidth;
            public byte Cap;
            public byte Join;
            public float MiterLimit;
            public float Tolerance;
        }

        static float2 Perp(float2 d) => new float2(-d.y, d.x);
        static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Strokes one polyline, appending closed polygons to the output lists.</summary>
        public static void Stroke(float2* pts, int count, bool closed, in Style st,
            ref UnsafeList<float2> outPts, ref UnsafeList<Poly> outPolys, ref UnsafeList<float2> tmp)
        {
            if (count <= 0 || st.HalfWidth <= 0) return;
            float hw = st.HalfWidth;

            // Remove near-duplicate points (tiny segments produce unstable normals).
            tmp.Clear();
            float eps = math.max(1e-6f, hw * 1e-5f);
            for (int i = 0; i < count; i++)
            {
                float2 p = pts[i];
                if (tmp.Length > 0 && math.distancesq(tmp[tmp.Length - 1], p) <= eps * eps) continue;
                tmp.Add(p);
            }
            if (closed && tmp.Length > 1 && math.distancesq(tmp[tmp.Length - 1], tmp[0]) <= eps * eps) tmp.Length--;
            int n = tmp.Length;
            float2* p0 = tmp.Ptr;

            if (n == 1 || (closed && n == 1))
            {
                // Zero-length subpath: round cap -> circle, square cap -> axis-aligned square (SVG).
                float2 c = p0[0];
                if (st.Cap == CapRound)
                {
                    int start = outPts.Length;
                    AddArc(ref outPts, c, new float2(hw, 0), math.PI * 2, st.Tolerance, hw, false);
                    Finish(ref outPts, ref outPolys, start);
                }
                else if (st.Cap == CapSquare)
                {
                    int start = outPts.Length;
                    outPts.Add(c + new float2(hw, hw));
                    outPts.Add(c + new float2(hw, -hw));
                    outPts.Add(c + new float2(-hw, -hw));
                    outPts.Add(c + new float2(-hw, hw));
                    Finish(ref outPts, ref outPolys, start);
                }
                return;
            }

            if (closed && n >= 2)
            {
                // Two loops: +n side forward, -n side backward.
                int start = outPts.Length;
                for (int i = 0; i < n; i++)
                {
                    float2 prev = p0[(i + n - 1) % n], cur = p0[i], next = p0[(i + 1) % n];
                    float2 dIn = math.normalize(cur - prev), dOut = math.normalize(next - cur);
                    Join(ref outPts, cur, dIn, dOut, 1f, st);
                }
                Finish(ref outPts, ref outPolys, start);
                start = outPts.Length;
                for (int i = n - 1; i >= 0; i--)
                {
                    float2 prev = p0[(i + 1) % n], cur = p0[i], next = p0[(i + n - 1) % n];
                    float2 dIn = math.normalize(cur - prev), dOut = math.normalize(next - cur);
                    Join(ref outPts, cur, dIn, dOut, 1f, st);
                }
                Finish(ref outPts, ref outPolys, start);
                return;
            }

            {
                int start = outPts.Length;
                // + side forward
                float2 d0 = math.normalize(p0[1] - p0[0]);
                outPts.Add(p0[0] + Perp(d0) * hw);
                for (int i = 1; i < n - 1; i++)
                {
                    float2 dIn = math.normalize(p0[i] - p0[i - 1]);
                    float2 dOut = math.normalize(p0[i + 1] - p0[i]);
                    Join(ref outPts, p0[i], dIn, dOut, 1f, st);
                }
                float2 dl = math.normalize(p0[n - 1] - p0[n - 2]);
                outPts.Add(p0[n - 1] + Perp(dl) * hw);
                Cap(ref outPts, p0[n - 1], dl, st);
                // - side backward
                outPts.Add(p0[n - 1] - Perp(dl) * hw);
                for (int i = n - 2; i >= 1; i--)
                {
                    float2 dIn = math.normalize(p0[i] - p0[i + 1]);
                    float2 dOut = math.normalize(p0[i - 1] - p0[i]);
                    Join(ref outPts, p0[i], dIn, dOut, 1f, st);
                }
                outPts.Add(p0[0] - Perp(d0) * hw);
                Cap(ref outPts, p0[0], -d0, st);
                Finish(ref outPts, ref outPolys, start);
            }
        }

        static void Finish(ref UnsafeList<float2> outPts, ref UnsafeList<Poly> outPolys, int start)
        {
            int cnt = outPts.Length - start;
            if (cnt < 3) { outPts.Length = start; return; }
            outPolys.Add(new Poly { First = start, Count = cnt, Closed = 1 });
        }

        /// <summary>Emits the offset geometry on the +normal side at a vertex.</summary>
        static void Join(ref UnsafeList<float2> outPts, float2 p, float2 dIn, float2 dOut, float side, in Style st)
        {
            float hw = st.HalfWidth;
            float2 nIn = Perp(dIn), nOut = Perp(dOut);
            float2 a = p + nIn * hw;
            float2 b = p + nOut * hw;
            float cr = Cross(dIn, dOut);
            float dt = math.dot(dIn, dOut);
            if (math.abs(cr) < 1e-7f && dt > 0)
            {
                outPts.Add(a);
                return;
            }
            if (cr > 0 && dt > -0.99999f)
            {
                // Turning towards +n: inner side -> connect through the pivot.
                outPts.Add(a);
                outPts.Add(p);
                outPts.Add(b);
                return;
            }
            // Outer side.
            outPts.Add(a);
            switch (st.Join)
            {
                case JoinRound:
                {
                    float ang = math.atan2(Cross(nIn, nOut), math.dot(nIn, nOut));
                    if (dt <= -0.99999f) ang = -math.PI; // reversal: go around the outside (clockwise from nIn)
                    AddArc(ref outPts, p, nIn * hw, ang, st.Tolerance, hw, true);
                    break;
                }
                case JoinMiter:
                {
                    // SVG: miter ratio = 1 / sin(theta/2), theta = angle between segments.
                    float cosHalf = math.sqrt(math.max(0, (1 + math.dot(nIn, nOut)) * 0.5f));
                    if (cosHalf > 1e-6f && 1f / cosHalf <= st.MiterLimit)
                    {
                        float2 bis = math.normalize(nIn + nOut);
                        outPts.Add(p + bis * (hw / cosHalf));
                    }
                    break;
                }
            }
            outPts.Add(b);
        }

        static void Cap(ref UnsafeList<float2> outPts, float2 p, float2 d, in Style st)
        {
            float hw = st.HalfWidth;
            float2 n = Perp(d);
            switch (st.Cap)
            {
                case CapSquare:
                    outPts.Add(p + n * hw + d * hw);
                    outPts.Add(p - n * hw + d * hw);
                    break;
                case CapRound:
                    // From +n to -n passing through +d.
                    AddArc(ref outPts, p, n * hw, -math.PI, st.Tolerance, hw, true);
                    break;
            }
        }

        /// <summary>Adds arc points around <paramref name="c"/> starting at c + r0 sweeping <paramref name="sweep"/> radians.</summary>
        static void AddArc(ref UnsafeList<float2> outPts, float2 c, float2 r0, float sweep, float tol, float radius, bool skipEnds)
        {
            float t = math.saturate(1 - tol / math.max(radius, 1e-6f));
            float maxStep = 2 * math.acos(t);
            if (!(maxStep > 1e-3f)) maxStep = 1e-3f;
            maxStep = math.min(maxStep, math.PI / 8);
            int segs = math.max(1, (int)math.ceil(math.abs(sweep) / maxStep));
            math.sincos(sweep / segs, out float s, out float co);
            float2 v = r0;
            int from = skipEnds ? 1 : 0;
            int to = skipEnds ? segs - 1 : segs - 1;
            if (!skipEnds) outPts.Add(c + v);
            for (int i = 1; i <= to; i++)
            {
                v = new float2(v.x * co - v.y * s, v.x * s + v.y * co);
                outPts.Add(c + v);
            }
        }
    }
}
