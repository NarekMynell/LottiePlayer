using Unity.Mathematics;

namespace Mynell.Lottie.Raster
{
    /// <summary>A horizontal run of constant coverage [X0, X1) (raster-relative).</summary>
    public struct CoverageSpan
    {
        public int X0, X1;
        public float Cov;
    }

    /// <summary>
    /// Exact-area coverage rasterizer (signed area accumulation, font-rs / FreeType style) with sparse rows:
    /// every accumulation cell written by an edge is flagged in a per-row bitmap, so a row resolves into
    /// single-pixel edge spans plus long constant-coverage interior runs.
    /// Works on a raster box [0, W) × [0, H) with a fractional clip rectangle inside it: segments are cut
    /// against the clip in y and clamped (after splitting) in x, which is equivalent to clipping the
    /// polygons against the rectangle, so clip edges are anti-aliased exactly.
    /// </summary>
    public unsafe struct CoverageRasterizer
    {
        /// <summary>Accumulation cells, <see cref="Stride"/> floats per row; must be zero before use.</summary>
        public float* Acc;
        /// <summary>Touched-cell bitmap, <see cref="WordStride"/> words per row; must be zero before use.</summary>
        public ulong* Bits;
        public int* RowMin;
        public int* RowMax;
        public int W, H, Stride, WordStride;
        public float CX0, CY0, CX1, CY1;

        public static int CellStride(int w) => w + 2;
        public static int WordsPerRow(int w) => (w + 2 + 63) >> 6;

        public void Begin(float* acc, ulong* bits, int* rowMin, int* rowMax, int w, int h, float4 clip)
        {
            Acc = acc;
            Bits = bits;
            RowMin = rowMin;
            RowMax = rowMax;
            W = w;
            H = h;
            Stride = CellStride(w);
            WordStride = WordsPerRow(w);
            CX0 = math.clamp(clip.x, 0, w);
            CY0 = math.clamp(clip.y, 0, h);
            CX1 = math.clamp(clip.z, 0, w);
            CY1 = math.clamp(clip.w, 0, h);
            for (int y = 0; y < h; y++)
            {
                rowMin[y] = int.MaxValue;
                rowMax[y] = -1;
            }
        }

        /// <summary>Adds one polygon edge (raster-relative coordinates).</summary>
        public void Edge(float x0, float y0, float x1, float y1)
        {
            if (y0 == y1) return;
            // Cut against the clip rectangle vertically.
            if (y0 < y1)
            {
                if (y1 <= CY0 || y0 >= CY1) return;
                if (y0 < CY0) { x0 += (x1 - x0) * (CY0 - y0) / (y1 - y0); y0 = CY0; }
                if (y1 > CY1) { x1 = x0 + (x1 - x0) * (CY1 - y0) / (y1 - y0); y1 = CY1; }
            }
            else
            {
                if (y0 <= CY0 || y1 >= CY1) return;
                if (y1 < CY0) { x1 += (x0 - x1) * (CY0 - y1) / (y0 - y1); y1 = CY0; }
                if (y0 > CY1) { x0 = x1 + (x0 - x1) * (CY1 - y1) / (y0 - y1); y0 = CY1; }
            }
            if (y0 == y1) return;

            // Split at the vertical clip lines; parts outside are projected onto them.
            bool cross0 = (x0 < CX0) != (x1 < CX0);
            bool cross1 = (x0 > CX1) != (x1 > CX1);
            if (!cross0 && !cross1)
            {
                Piece(x0, y0, x1, y1);
                return;
            }
            float ta = cross0 ? (CX0 - x0) / (x1 - x0) : 2f;
            float tb = cross1 ? (CX1 - x0) / (x1 - x0) : 2f;
            if (ta > tb) { float t = ta; ta = tb; tb = t; }
            float px = x0, py = y0;
            if (ta > 0f && ta < 1f)
            {
                float qx = x0 + (x1 - x0) * ta, qy = y0 + (y1 - y0) * ta;
                Piece(px, py, qx, qy);
                px = qx; py = qy;
            }
            if (tb > 0f && tb < 1f)
            {
                float qx = x0 + (x1 - x0) * tb, qy = y0 + (y1 - y0) * tb;
                Piece(px, py, qx, qy);
                px = qx; py = qy;
            }
            Piece(px, py, x1, y1);
        }

        void Piece(float x0, float y0, float x1, float y1)
        {
            float xm = 0.5f * (x0 + x1);
            if (xm <= CX0) x0 = x1 = CX0;
            else if (xm >= CX1) x0 = x1 = CX1;
            else
            {
                x0 = math.clamp(x0, CX0, CX1);
                x1 = math.clamp(x1, CX0, CX1);
            }
            Line(x0, y0, x1, y1);
        }

        void Touch(ulong* bits, int x) => bits[x >> 6] |= 1UL << (x & 63);

        void Line(float x0, float y0, float x1, float y1)
        {
            if (y0 == y1) return;
            float dir = 1f;
            if (y0 > y1)
            {
                dir = -1f;
                float t = x0; x0 = x1; x1 = t;
                t = y0; y0 = y1; y1 = t;
            }
            float dxdy = (x1 - x0) / (y1 - y0);
            float x = x0;
            int yStart = math.max(0, (int)y0);
            int yEnd = math.min(H, (int)math.ceil(y1));
            float wMax = W;
            for (int y = yStart; y < yEnd; y++)
            {
                float dy = math.min(y + 1f, y1) - math.max((float)y, y0);
                if (dy <= 0) continue;
                float xnext = x + dxdy * dy;
                float d = dy * dir;
                float xa = math.clamp(math.min(x, xnext), 0, wMax);
                float xb = math.clamp(math.max(x, xnext), 0, wMax);
                float xaFloor = math.floor(xa);
                int xai = (int)xaFloor;
                float xbCeil = math.ceil(xb);
                int xbi = (int)xbCeil;
                float* row = Acc + y * Stride;
                ulong* bits = Bits + y * WordStride;
                if (xbi <= xai + 1)
                {
                    float xmf = 0.5f * (xa + xb) - xaFloor;
                    row[xai] += d - d * xmf;
                    row[xai + 1] += d * xmf;
                    Touch(bits, xai);
                    Touch(bits, xai + 1);
                    xbi = xai + 1;
                }
                else
                {
                    float s = 1f / (xb - xa);
                    float x0f = xa - xaFloor;
                    float a0 = 0.5f * s * (1f - x0f) * (1f - x0f);
                    float x1f = xb - xbCeil + 1f;
                    float am = 0.5f * s * x1f * x1f;
                    row[xai] += d * a0;
                    Touch(bits, xai);
                    if (xbi == xai + 2)
                    {
                        row[xai + 1] += d * (1f - a0 - am);
                        Touch(bits, xai + 1);
                    }
                    else
                    {
                        float a1 = s * (1.5f - x0f);
                        row[xai + 1] += d * (a1 - a0);
                        Touch(bits, xai + 1);
                        float ds = d * s;
                        for (int xi = xai + 2; xi < xbi - 1; xi++)
                        {
                            row[xi] += ds;
                            Touch(bits, xi);
                        }
                        float a2 = a1 + (xbi - xai - 3) * s;
                        row[xbi - 1] += d * (1f - a2 - am);
                        Touch(bits, xbi - 1);
                    }
                    row[xbi] += d * am;
                    Touch(bits, xbi);
                }
                if (xai < RowMin[y]) RowMin[y] = xai;
                if (xbi > RowMax[y]) RowMax[y] = xbi;
                x = xnext;
            }
        }

        static float Rule(float s, bool evenOdd)
        {
            float a = math.abs(s);
            if (evenOdd)
            {
                a = a - 2f * math.floor(a * 0.5f);
                if (a > 1f) a = 2f - a;
            }
            else if (a > 1f) a = 1f;
            return a;
        }

        /// <summary>
        /// Resolves row <paramref name="y"/> into constant-coverage spans (clearing the cells and bits).
        /// Spans with (near) zero coverage are dropped. Returns the span count.
        /// </summary>
        public int Sweep(int y, bool evenOdd, CoverageSpan* spans, float minCoverage)
        {
            int mx = RowMax[y];
            if (mx < 0) return 0;
            int mn = RowMin[y];
            float* row = Acc + y * Stride;
            ulong* bits = Bits + y * WordStride;
            int n = 0;
            float s = 0;
            int prevEnd = mn;
            int w0 = mn >> 6, w1 = mx >> 6;
            for (int wi = w0; wi <= w1; wi++)
            {
                ulong word = bits[wi];
                if (word == 0) continue;
                bits[wi] = 0;
                while (word != 0)
                {
                    int x = (wi << 6) + math.tzcnt(word);
                    word &= word - 1;
                    if (x >= W)
                    {
                        // Cells past the last pixel only close the row; flush the interior run first.
                        if (prevEnd < W)
                        {
                            float c = Rule(s, evenOdd);
                            if (c >= minCoverage)
                            {
                                if (n > 0 && spans[n - 1].X1 == prevEnd && spans[n - 1].Cov == c) spans[n - 1].X1 = W;
                                else spans[n++] = new CoverageSpan { X0 = prevEnd, X1 = W, Cov = c };
                            }
                            prevEnd = W;
                        }
                        row[x] = 0;
                        continue;
                    }
                    if (x > prevEnd)
                    {
                        float c = Rule(s, evenOdd);
                        if (c >= minCoverage)
                        {
                            if (n > 0 && spans[n - 1].X1 == prevEnd && spans[n - 1].Cov == c) spans[n - 1].X1 = x;
                            else spans[n++] = new CoverageSpan { X0 = prevEnd, X1 = x, Cov = c };
                        }
                    }
                    s += row[x];
                    row[x] = 0;
                    float cc = Rule(s, evenOdd);
                    if (cc >= minCoverage)
                    {
                        if (n > 0 && spans[n - 1].X1 == x && spans[n - 1].Cov == cc) spans[n - 1].X1 = x + 1;
                        else spans[n++] = new CoverageSpan { X0 = x, X1 = x + 1, Cov = cc };
                    }
                    prevEnd = x + 1;
                }
            }
            return n;
        }
    }
}
