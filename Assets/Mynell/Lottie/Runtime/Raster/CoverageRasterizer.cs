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
    /// Works on a raster box [0, W) x [Row0, Row0 + H) with a fractional clip rectangle inside it: segments are
    /// cut against the clip in y and clamped (after splitting) in x, which is equivalent to clipping the
    /// polygons against the rectangle, so clip edges are anti-aliased exactly.
    /// Where an edge crosses a row is computed from its end points, never accumulated row by row, so the rows
    /// of a raster get the same coverage whichever part of the raster is stored (bands of a frame match the
    /// whole frame bit for bit).
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
        /// <summary>First stored row: rows [Row0, Row0 + H) of the raster are accumulated, row y at index y - Row0.</summary>
        public int Row0;
        public float CX0, CY0, CX1, CY1;

        public static int CellStride(int w) => w + 2;
        public static int WordsPerRow(int w) => (w + 2 + 63) >> 6;

        /// <summary>
        /// Starts a raster W wide whose rows [<paramref name="row0"/>, <paramref name="row0"/> + <paramref name="h"/>) are
        /// stored. <paramref name="clip"/> is in raster coordinates.
        /// </summary>
        public void Begin(float* acc, ulong* bits, int* rowMin, int* rowMax, int w, int h, float4 clip, int row0 = 0)
        {
            Acc = acc;
            Bits = bits;
            RowMin = rowMin;
            RowMax = rowMax;
            W = w;
            H = h;
            Stride = CellStride(w);
            WordStride = WordsPerRow(w);
            Row0 = row0;
            CX0 = math.clamp(clip.x, 0, w);
            CY0 = math.clamp(clip.y, row0, row0 + h);
            CX1 = math.clamp(clip.z, 0, w);
            CY1 = math.clamp(clip.w, row0, row0 + h);
            for (int y = 0; y < h; y++)
            {
                rowMin[y] = int.MaxValue;
                rowMax[y] = -1;
            }
        }

        /// <summary>Adds one polygon edge (raster coordinates).</summary>
        public void Edge(float x0, float y0, float x1, float y1)
        {
            if (y0 == y1) return;
            // Entirely above or below the clip (Line cuts the rest vertically).
            if (math.max(y0, y1) <= CY0 || math.min(y0, y1) >= CY1) return;

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

        /// <summary>Flags cells <paramref name="a"/>..<paramref name="b"/> (inclusive).</summary>
        static void Touch(ulong* bits, int a, int b)
        {
            int wa = a >> 6, wb = b >> 6;
            ulong ma = ~0UL << (a & 63), mb = ~0UL >> (63 - (b & 63));
            if (wa == wb)
            {
                bits[wa] |= ma & mb;
                return;
            }
            bits[wa] |= ma;
            for (int w = wa + 1; w < wb; w++) bits[w] = ~0UL;
            bits[wb] |= mb;
        }

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
            // The part inside the clip.
            float ya = math.max(y0, CY0), yb = math.min(y1, CY1);
            if (!(ya < yb)) return;
            float dxdy = (x1 - x0) / (y1 - y0);
            // x where the edge crosses height t is x0 + dxdy * (t - y0), from the end points every time.
            float x = x0 + dxdy * (ya - y0);
            int yStart = (int)math.floor(ya);
            int yEnd = (int)math.ceil(yb);
            float wMax = W;
            int row0 = Row0;
            // Fields in locals (the cell stores could alias this struct, so they would be reloaded every row).
            float* acc = Acc;
            ulong* allBits = Bits;
            int* rowMin = RowMin, rowMax = RowMax;
            int stride = Stride, wordStride = WordStride;
            for (int y = yStart; y < yEnd; y++)
            {
                float bottom = math.min(y + 1f, yb);
                float dy = bottom - math.max((float)y, ya);
                if (dy <= 0) continue;
                float xnext = x0 + dxdy * (bottom - y0);
                float d = dy * dir;
                float xa = math.clamp(math.min(x, xnext), 0, wMax);
                float xb = math.clamp(math.max(x, xnext), 0, wMax);
                float xaFloor = math.floor(xa);
                int xai = (int)xaFloor;
                float xbCeil = math.ceil(xb);
                int xbi = (int)xbCeil;
                int r = y - row0;
                float* row = acc + r * stride;
                ulong* bits = allBits + r * wordStride;
                if (xbi <= xai + 1)
                {
                    float xmf = 0.5f * (xa + xb) - xaFloor;
                    row[xai] += d - d * xmf;
                    row[xai + 1] += d * xmf;
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
                    if (xbi == xai + 2)
                    {
                        row[xai + 1] += d * (1f - a0 - am);
                    }
                    else
                    {
                        float a1 = s * (1.5f - x0f);
                        row[xai + 1] += d * (a1 - a0);
                        float ds = d * s;
                        for (int xi = xai + 2; xi < xbi - 1; xi++) row[xi] += ds;
                        float a2 = a1 + (xbi - xai - 3) * s;
                        row[xbi - 1] += d * (1f - a2 - am);
                    }
                    row[xbi] += d * am;
                }
                Touch(bits, xai, xbi);
                if (xai < rowMin[r]) rowMin[r] = xai;
                if (xbi > rowMax[r]) rowMax[r] = xbi;
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
            int n = 0, width = W;
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
                    if (x >= width)
                    {
                        // Cells past the last pixel only close the row; flush the interior run first.
                        if (prevEnd < width)
                        {
                            float c = Rule(s, evenOdd);
                            if (c >= minCoverage)
                            {
                                if (n > 0 && spans[n - 1].X1 == prevEnd && spans[n - 1].Cov == c) spans[n - 1].X1 = width;
                                else spans[n++] = new CoverageSpan { X0 = prevEnd, X1 = width, Cov = c };
                            }
                            prevEnd = width;
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
