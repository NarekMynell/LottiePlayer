using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Geometry
{
    /// <summary>
    /// Boolean operations on polygon regions (flattened paths), used by Merge Paths.
    /// Every edge is split at all intersections; each piece is kept when the combined region differs on its two sides,
    /// oriented so the region lies on its left, and the kept pieces are chained into closed contours. Each operand is
    /// interpreted with the non-zero rule; operands are combined in order: ((op0 ∘ op1) ∘ op2) ...
    /// </summary>
    public static unsafe class PolyBoolean
    {
        public const int Add = 2, Subtract = 3, Intersect = 4, Exclude = 5;

        struct Seg
        {
            public double2 A, B;
            public int Op;
            public int Bin0;
        }

        struct Split : IComparable<Split>
        {
            public int Seg;
            public double T;
            public double2 P;

            public int CompareTo(Split o) => Seg != o.Seg ? Seg.CompareTo(o.Seg) : T.CompareTo(o.T);
        }

        struct Frag : IComparable<Frag>
        {
            public double2 A, B;

            public int CompareTo(Frag o)
            {
                if (A.x != o.A.x) return A.x.CompareTo(o.A.x);
                if (A.y != o.A.y) return A.y.CompareTo(o.A.y);
                if (B.x != o.B.x) return B.x.CompareTo(o.B.x);
                return B.y.CompareTo(o.B.y);
            }
        }

        struct VKey : IEquatable<VKey>
        {
            public long X, Y;
            public VKey(double2 p) { X = math.asulong(p.x) == 0x8000000000000000UL ? 0 : (long)math.asulong(p.x); Y = math.asulong(p.y) == 0x8000000000000000UL ? 0 : (long)math.asulong(p.y); }
            public bool Equals(VKey o) => X == o.X && Y == o.Y;
            public override int GetHashCode() => (int)(X ^ (X >> 32)) * 397 ^ (int)(Y ^ (Y >> 32));
        }

        /// <summary>
        /// Combines closed polygons. <paramref name="contours"/> holds (first, count, operand) into <paramref name="pts"/>.
        /// The result (closed contours, region on the left of each edge) is appended to the output lists.
        /// </summary>
        public static void Combine(double2* pts, int3* contours, int contourCount, int operands, int mode,
            ref NativeList<double2> outPts, ref NativeList<int2> outContours)
        {
            if (operands <= 0 || contourCount <= 0) return;

            // 1. Edges and bounds.
            var segs = new NativeList<Seg>(256, Allocator.Temp);
            double2 mn = new double2(double.MaxValue), mx = new double2(double.MinValue);
            for (int c = 0; c < contourCount; c++)
            {
                int3 ct = contours[c];
                if (ct.y < 3) continue;
                for (int i = 0; i < ct.y; i++)
                {
                    double2 a = pts[ct.x + i], b = pts[ct.x + (i + 1) % ct.y];
                    if (a.x == b.x && a.y == b.y) continue;
                    segs.Add(new Seg { A = a, B = b, Op = ct.z });
                    mn = math.min(mn, math.min(a, b));
                    mx = math.max(mx, math.max(a, b));
                }
            }
            int n = segs.Length;
            if (n == 0)
            {
                segs.Dispose();
                return;
            }
            double scale = math.max(1.0, math.cmax(mx - mn));
            double eps = scale * 1e-10;       // intersection / collinearity tolerance
            double offset = scale * 1e-7;     // side-sampling distance for classification

            // 2. Horizontal bins (CSR) to limit pair tests and winding queries.
            int bins = math.clamp((int)math.sqrt(n), 1, 1024);
            double y0 = mn.y, binScale = bins / math.max(mx.y - mn.y, 1e-12);
            var binStart = new NativeArray<int>(bins + 1, Allocator.Temp);
            for (int s = 0; s < n; s++)
            {
                Seg sg = segs[s];
                int b0 = BinOf(math.min(sg.A.y, sg.B.y), y0, binScale, bins), b1 = BinOf(math.max(sg.A.y, sg.B.y), y0, binScale, bins);
                sg.Bin0 = b0;
                segs[s] = sg;
                for (int b = b0; b <= b1; b++) binStart[b + 1]++;
            }
            for (int b = 0; b < bins; b++) binStart[b + 1] += binStart[b];
            var binItems = new NativeArray<int>(binStart[bins], Allocator.Temp);
            var fill = new NativeArray<int>(bins, Allocator.Temp);
            for (int s = 0; s < n; s++)
            {
                Seg sg = segs[s];
                int b1 = BinOf(math.max(sg.A.y, sg.B.y), y0, binScale, bins);
                for (int b = sg.Bin0; b <= b1; b++) binItems[binStart[b] + fill[b]++] = s;
            }
            fill.Dispose();

            // 3. Intersections (each pair is tested in the first bin both segments share).
            var splits = new NativeList<Split>(64, Allocator.Temp);
            Seg* sp = (Seg*)segs.GetUnsafePtr();
            for (int b = 0; b < bins; b++)
            {
                int start = binStart[b], end = binStart[b + 1];
                for (int i = start; i < end; i++)
                {
                    int si = binItems[i];
                    Seg s1 = sp[si];
                    double4 box1 = Box(s1);
                    for (int j = i + 1; j < end; j++)
                    {
                        int sj = binItems[j];
                        Seg s2 = sp[sj];
                        if (math.max(s1.Bin0, s2.Bin0) != b) continue;
                        double4 box2 = Box(s2);
                        if (box1.x > box2.z + eps || box2.x > box1.z + eps || box1.y > box2.w + eps || box2.y > box1.w + eps) continue;
                        IntersectSegments(si, s1, sj, s2, eps, ref splits);
                    }
                }
            }

            // 4. Fragments.
            splits.Sort();
            var frags = new NativeList<Frag>(n + splits.Length, Allocator.Temp);
            int k = 0;
            for (int s = 0; s < n; s++)
            {
                Seg sg = sp[s];
                double2 prev = sg.A;
                while (k < splits.Length && splits[k].Seg == s)
                {
                    double2 p = splits[k].P;
                    k++;
                    if (p.x == prev.x && p.y == prev.y) continue;
                    if (p.x == sg.B.x && p.y == sg.B.y) continue;
                    frags.Add(new Frag { A = prev, B = p });
                    prev = p;
                }
                frags.Add(new Frag { A = prev, B = sg.B });
            }
            splits.Dispose();

            // 5. Classification: keep the pieces where the combined region changes, region on the left.
            var kept = new NativeList<Frag>(frags.Length / 2 + 4, Allocator.Temp);
            var winding = new NativeArray<int>(operands, Allocator.Temp);
            for (int f = 0; f < frags.Length; f++)
            {
                Frag fr = frags[f];
                double2 d = fr.B - fr.A;
                double len = math.length(d);
                if (!(len > 0)) continue;
                double2 mid = (fr.A + fr.B) * 0.5;
                double2 nrm = new double2(-d.y, d.x) / len * offset;
                bool left = Inside(mid + nrm, sp, binItems, binStart, y0, binScale, bins, winding, operands, mode);
                bool right = Inside(mid - nrm, sp, binItems, binStart, y0, binScale, bins, winding, operands, mode);
                if (left == right) continue;
                kept.Add(left ? fr : new Frag { A = fr.B, B = fr.A });
            }
            winding.Dispose();
            frags.Dispose();
            binItems.Dispose();
            binStart.Dispose();
            segs.Dispose();

            // 6. Coincident pieces from several operands: keep one.
            kept.Sort();
            int w = 0;
            for (int r = 0; r < kept.Length; r++)
            {
                if (w > 0 && kept[r].CompareTo(kept[w - 1]) == 0) continue;
                kept[w++] = kept[r];
            }
            kept.ResizeUninitialized(w);

            Chain(kept, ref outPts, ref outContours);
            kept.Dispose();
        }

        static int BinOf(double y, double y0, double scale, int bins) => math.clamp((int)((y - y0) * scale), 0, bins - 1);

        static double4 Box(in Seg s) => new double4(math.min(s.A, s.B), math.max(s.A, s.B));

        static void IntersectSegments(int i1, in Seg s1, int i2, in Seg s2, double eps, ref NativeList<Split> splits)
        {
            double2 d1 = s1.B - s1.A, d2 = s2.B - s2.A;
            double len1 = math.length(d1), len2 = math.length(d2);
            double den = d1.x * d2.y - d1.y * d2.x;
            double2 r = s2.A - s1.A;
            if (math.abs(den) <= 1e-12 * len1 * len2)
            {
                // Parallel: split both at the other's endpoints when collinear and overlapping.
                if (math.abs(r.x * d1.y - r.y * d1.x) > eps * len1) return;
                SplitAt(i1, s1, d1, len1, s2.A, eps, ref splits);
                SplitAt(i1, s1, d1, len1, s2.B, eps, ref splits);
                SplitAt(i2, s2, d2, len2, s1.A, eps, ref splits);
                SplitAt(i2, s2, d2, len2, s1.B, eps, ref splits);
                return;
            }
            double t = (r.x * d2.y - r.y * d2.x) / den;
            double u = (r.x * d1.y - r.y * d1.x) / den;
            double et = eps / len1, eu = eps / len2;
            if (t < -et || t > 1 + et || u < -eu || u > 1 + eu) return;
            bool tA = t <= et, tB = t >= 1 - et, uA = u <= eu, uB = u >= 1 - eu;
            double2 p = tA ? s1.A : tB ? s1.B : uA ? s2.A : uB ? s2.B : s1.A + t * d1;
            if (!tA && !tB) splits.Add(new Split { Seg = i1, T = t, P = p });
            if (!uA && !uB) splits.Add(new Split { Seg = i2, T = u, P = p });
        }

        static void SplitAt(int index, in Seg s, double2 d, double len, double2 p, double eps, ref NativeList<Split> splits)
        {
            double t = math.dot(p - s.A, d) / (len * len);
            double et = eps / len;
            if (t <= et || t >= 1 - et) return;
            splits.Add(new Split { Seg = index, T = t, P = p });
        }

        static bool Inside(double2 p, Seg* segs, NativeArray<int> binItems, NativeArray<int> binStart, double y0, double binScale,
            int bins, NativeArray<int> winding, int operands, int mode)
        {
            for (int o = 0; o < operands; o++) winding[o] = 0;
            int b = BinOf(p.y, y0, binScale, bins);
            for (int i = binStart[b], end = binStart[b + 1]; i < end; i++)
            {
                Seg s = segs[binItems[i]];
                double cross = (s.B.x - s.A.x) * (p.y - s.A.y) - (p.x - s.A.x) * (s.B.y - s.A.y);
                if (s.A.y <= p.y)
                {
                    if (s.B.y > p.y && cross > 0) winding[s.Op]++;
                }
                else if (s.B.y <= p.y && cross < 0) winding[s.Op]--;
            }
            bool r = winding[0] != 0;
            for (int o = 1; o < operands; o++)
            {
                bool s = winding[o] != 0;
                switch (mode)
                {
                    case Add: r = r || s; break;
                    case Subtract: r = r && !s; break;
                    case Intersect: r = r && s; break;
                    default: r = r != s; break;
                }
            }
            return r;
        }

        /// <summary>Links directed pieces into closed contours (pieces meet at bit-identical vertices).</summary>
        static void Chain(NativeList<Frag> frags, ref NativeList<double2> outPts, ref NativeList<int2> outContours)
        {
            int n = frags.Length;
            if (n == 0) return;
            var ids = new NativeHashMap<VKey, int>(n * 2, Allocator.Temp);
            var from = new NativeArray<int>(n, Allocator.Temp);
            var to = new NativeArray<int>(n, Allocator.Temp);
            int vc = 0;
            for (int f = 0; f < n; f++)
            {
                from[f] = VertexId(ref ids, frags[f].A, ref vc);
                to[f] = VertexId(ref ids, frags[f].B, ref vc);
            }
            // Outgoing pieces per vertex (CSR).
            var outStart = new NativeArray<int>(vc + 1, Allocator.Temp);
            for (int f = 0; f < n; f++) outStart[from[f] + 1]++;
            for (int v = 0; v < vc; v++) outStart[v + 1] += outStart[v];
            var outItems = new NativeArray<int>(n, Allocator.Temp);
            var cursor = new NativeArray<int>(vc, Allocator.Temp);
            for (int f = 0; f < n; f++) outItems[outStart[from[f]] + cursor[from[f]]++] = f;
            var used = new NativeArray<bool>(n, Allocator.Temp);
            for (int f0 = 0; f0 < n; f0++)
            {
                if (used[f0]) continue;
                int first = outPts.Length;
                int startVertex = from[f0];
                int f = f0;
                outPts.Add(frags[f].A);
                int guard = 0;
                while (guard++ <= n)
                {
                    used[f] = true;
                    int v = to[f];
                    if (v == startVertex) break;
                    outPts.Add(frags[f].B);
                    int next = -1;
                    for (int i = outStart[v]; i < outStart[v + 1]; i++)
                        if (!used[outItems[i]]) { next = outItems[i]; break; }
                    if (next < 0) break;
                    f = next;
                }
                int count = outPts.Length - first;
                if (count >= 3) outContours.Add(new int2(first, count));
                else outPts.ResizeUninitialized(first);
            }
            used.Dispose();
            cursor.Dispose();
            outItems.Dispose();
            outStart.Dispose();
            to.Dispose();
            from.Dispose();
            ids.Dispose();
        }

        static int VertexId(ref NativeHashMap<VKey, int> ids, double2 p, ref int count)
        {
            var key = new VKey(p);
            if (ids.TryGetValue(key, out int id)) return id;
            ids.Add(key, count);
            return count++;
        }
    }
}
