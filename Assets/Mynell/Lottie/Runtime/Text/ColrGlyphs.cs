using System;
using System.Collections.Generic;
using Mynell.Lottie.Raster;
using Unity.Mathematics;

namespace Mynell.Lottie.Text
{
    /// <summary>
    /// Colour glyphs of 'COLR' / 'CPAL' fonts rendered into bitmaps: layered glyphs (COLR version 0, Segoe UI Emoji) and
    /// paint graphs (version 1, Noto Color Emoji on Android 13+: gradients, transforms and composites), as specified by
    /// OpenType 1.9.1 and rendered by Skia (Chrome). Font variations are not applied (default instance).
    /// </summary>
    sealed unsafe class ColrGlyphs
    {
        const int MaxDepth = 64;
        const int MaxSize = 512;
        const int LutSize = 1024;

        /// <summary>Affine map x' = A x + C y + E, y' = B x + D y + F.</summary>
        struct Affine
        {
            public float A, B, C, D, E, F;

            public Affine(float a, float b, float c, float d, float e, float f)
            {
                A = a; B = b; C = c; D = d; E = e; F = f;
            }

            public static Affine Identity => new Affine(1, 0, 0, 1, 0, 0);
            public static Affine Translate(float x, float y) => new Affine(1, 0, 0, 1, x, y);
            public static Affine Scale(float x, float y) => new Affine(x, 0, 0, y, 0, 0);

            /// <summary>m * n: n applied first.</summary>
            public static Affine operator *(in Affine m, in Affine n) => new Affine(
                m.A * n.A + m.C * n.B, m.B * n.A + m.D * n.B,
                m.A * n.C + m.C * n.D, m.B * n.C + m.D * n.D,
                m.A * n.E + m.C * n.F + m.E, m.B * n.E + m.D * n.F + m.F);

            public float2 Map(float2 p) => new float2(A * p.x + C * p.y + E, B * p.x + D * p.y + F);

            public bool Invert(out Affine inv)
            {
                float det = A * D - B * C;
                inv = Identity;
                if (!(math.abs(det) > 1e-12f)) return false;
                float r = 1f / det;
                inv = new Affine(D * r, -B * r, -C * r, A * r, (C * F - D * E) * r, (B * E - A * F) * r);
                return true;
            }

            /// <summary>M about a centre: T(c) · m · T(-c).</summary>
            public Affine Around(float cx, float cy) => Translate(cx, cy) * this * Translate(-cx, -cy);
        }

        /// <summary>Coverage of the canvas; zero outside [X0, X1) × [Y0, Y1).</summary>
        sealed class Mask
        {
            public float[] Cov;
            public int X0, Y0, X1, Y1;
        }

        struct ColorLine
        {
            public int Extend;          // 0 pad, 1 repeat, 2 reflect
            public float[] Offsets;
            public float4[] Colors;     // straight RGBA
        }

        readonly byte[] _d;
        readonly TrueTypeFont _font;
        readonly int _upem;
        readonly int _numBase, _baseRecords, _layerRecords;
        readonly int _baseList, _layerList, _clipList;
        readonly float4[] _palette;
        readonly Dictionary<int, List<GlyphOutlineContour>> _outlines = new Dictionary<int, List<GlyphOutlineContour>>();
        readonly HashSet<int> _active = new HashSet<int>();
        // Buffers of the current canvas size, reused across layers (zeroed when returned).
        readonly Stack<float[]> _maskPool = new Stack<float[]>();
        readonly Stack<float4[]> _layerPool = new Stack<float4[]>();
        float[] _acc;
        int _w, _h;
        Affine _device;

        /// <summary>Colour of palette index 0xFFFF (the text colour), straight RGBA.</summary>
        public float4 Foreground = new float4(0, 0, 0, 1);

        ColrGlyphs(byte[] data, TrueTypeFont font, int colr, int cpal)
        {
            _d = data;
            _font = font;
            _upem = Math.Max(1, font.UnitsPerEm);
            int version = U16(colr);
            _numBase = U16(colr + 2);
            _baseRecords = colr + (int)U32(colr + 4);
            _layerRecords = colr + (int)U32(colr + 8);
            if (version >= 1)
            {
                uint baseList = U32(colr + 14), layerList = U32(colr + 18), clipList = U32(colr + 22);
                _baseList = baseList != 0 ? colr + (int)baseList : 0;
                _layerList = layerList != 0 ? colr + (int)layerList : 0;
                _clipList = clipList != 0 ? colr + (int)clipList : 0;
            }
            int entries = U16(cpal + 2), records = cpal + (int)U32(cpal + 8), first = U16(cpal + 12);
            _palette = new float4[entries];
            for (int i = 0; i < entries; i++)
            {
                int r = records + (first + i) * 4;
                if (r + 4 <= _d.Length) _palette[i] = new float4(_d[r + 2], _d[r + 1], _d[r], _d[r + 3]) / 255f;
            }
        }

        /// <summary>Colour glyphs of the font at <paramref name="fontOffset"/>, given its COLR and CPAL table offsets.</summary>
        public static ColrGlyphs Create(byte[] data, int fontOffset, int colr, int cpal)
        {
            try
            {
                var font = TrueTypeFont.LoadFace(data, fontOffset);
                return font == null ? null : new ColrGlyphs(data, font, colr, cpal);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public int UnitsPerEm => _upem;

        public bool Has(int gid) => BasePaint(gid) >= 0 || BaseLayers(gid, out _, out _);

        /// <summary>
        /// Renders a colour glyph at <paramref name="ppem"/> pixels per em: straight RGBA8 rows from the top, and the
        /// bitmap box in font units (x min, y min, x max, y max; y up).
        /// </summary>
        public bool Render(int gid, float ppem, out byte[] rgba, out int width, out int height, out float4 box)
        {
            rgba = null;
            width = height = 0;
            box = default;
            try
            {
                int root = BasePaint(gid);
                bool layered = root < 0 && BaseLayers(gid, out _, out _);
                if (root < 0 && !layered) return false;

                bool clipped = false;
                float4 bounds = new float4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                if (root >= 0 && ClipBox(gid, out var clip))
                {
                    bounds = clip;
                    clipped = true;
                }
                else
                {
                    bool bounded = layered ? LayerBounds(gid, ref bounds) : Bounds(root, Affine.Identity, ref bounds, 0);
                    if (!bounded || !(bounds.x < bounds.z) || !(bounds.y < bounds.w))
                        bounds = new float4(0, _font.Descender, math.max(1, _font.Advance(gid)), _font.Ascender);
                }
                float s = ppem / _upem;
                int x0 = (int)math.floor(bounds.x * s), x1 = (int)math.ceil(bounds.z * s);
                int y0 = (int)math.floor(bounds.y * s), y1 = (int)math.ceil(bounds.w * s);
                _w = x1 - x0;
                _h = y1 - y0;
                if (_w <= 0 || _h <= 0 || _w > MaxSize || _h > MaxSize) return false;
                _device = new Affine(s, 0, 0, -s, -x0, y1);
                if (_acc == null || _acc.Length != (_w + 2) * _h)
                {
                    _maskPool.Clear();
                    _layerPool.Clear();
                    _acc = new float[(_w + 2) * _h];
                }
                var dst = new float4[_w * _h];
                _active.Clear();
                if (layered) PaintLayers(gid, dst);
                else
                {
                    _active.Add(gid);
                    Paint(root, Affine.Identity, dst, clipped ? BoxMask(bounds, Affine.Identity) : null, 0);
                }
                rgba = new byte[_w * _h * 4];
                fixed (float4* src = dst)
                fixed (byte* bytes = rgba)
                    ColrKernels.ToRgba8(src, bytes, dst.Length);
                width = _w;
                height = _h;
                box = new float4(x0 / s, y0 / s, x1 / s, y1 / s);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ table lookups

        int BasePaint(int gid)
        {
            if (_baseList == 0) return -1;
            int lo = 0, hi = (int)U32(_baseList) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = _baseList + 4 + mid * 6;
                int g = U16(r);
                if (g < gid) lo = mid + 1;
                else if (g > gid) hi = mid - 1;
                else return _baseList + (int)U32(r + 2);
            }
            return -1;
        }

        bool BaseLayers(int gid, out int first, out int count)
        {
            first = count = 0;
            int lo = 0, hi = _numBase - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = _baseRecords + mid * 6;
                int g = U16(r);
                if (g < gid) lo = mid + 1;
                else if (g > gid) hi = mid - 1;
                else
                {
                    first = U16(r + 2);
                    count = U16(r + 4);
                    return count > 0;
                }
            }
            return false;
        }

        int LayerPaint(long index) => _layerList + (int)U32(_layerList + 4 + (int)index * 4);

        bool ClipBox(int gid, out float4 box)
        {
            box = default;
            if (_clipList == 0) return false;
            int lo = 0, hi = (int)U32(_clipList + 1) - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = _clipList + 5 + mid * 7;
                if (gid < U16(r)) hi = mid - 1;
                else if (gid > U16(r + 2)) lo = mid + 1;
                else
                {
                    int b = _clipList + Off24(r + 4);
                    box = new float4(S16(b + 1), S16(b + 3), S16(b + 5), S16(b + 7));
                    return box.x < box.z && box.y < box.w;
                }
            }
            return false;
        }

        float4 PaletteColor(int index, float alpha)
        {
            float4 c = index == 0xFFFF ? Foreground : index < _palette.Length ? _palette[index] : float4.zero;
            c.w *= alpha;
            return c;
        }

        static float4 Premultiply(float4 c) => new float4(c.xyz * c.w, c.w);

        // ------------------------------------------------------------------ paint graph

        void PaintLayers(int gid, float4[] dst)
        {
            BaseLayers(gid, out int first, out int count);
            for (int i = 0; i < count; i++)
            {
                int r = _layerRecords + (first + i) * 4;
                var mask = GlyphMask(U16(r), Affine.Identity);
                FillSolid(dst, mask, Premultiply(PaletteColor(U16(r + 2), 1)));
                Release(mask);
            }
        }

        void Paint(int p, in Affine t, float4[] dst, Mask clip, int depth)
        {
            if (depth > MaxDepth) return;
            int format = _d[p];
            switch (format)
            {
                case 1: // PaintColrLayers
                {
                    int count = _d[p + 1];
                    uint first = U32(p + 2);
                    for (int i = 0; i < count; i++) Paint(LayerPaint(first + i), t, dst, clip, depth + 1);
                    break;
                }
                case 2:
                case 3: // PaintSolid
                    FillSolid(dst, clip, Premultiply(PaletteColor(U16(p + 1), F2Dot14(p + 3))));
                    break;
                case 4:
                case 5: // PaintLinearGradient
                case 6:
                case 7: // PaintRadialGradient
                case 8:
                case 9: // PaintSweepGradient
                    FillGradient(p, format, t, dst, clip);
                    break;
                case 10: // PaintGlyph
                {
                    var mask = GlyphMask(U16(p + 4), t);
                    if (clip != null) Intersect(mask, clip);
                    if (mask.X0 < mask.X1 && mask.Y0 < mask.Y1) Paint(p + Off24(p + 1), t, dst, mask, depth + 1);
                    Release(mask);
                    break;
                }
                case 11: // PaintColrGlyph
                {
                    int gid = U16(p + 1);
                    int child = BasePaint(gid);
                    if (child < 0 || !_active.Add(gid)) break;
                    var c = clip;
                    if (ClipBox(gid, out var box))
                    {
                        c = BoxMask(box, t);
                        if (clip != null) Intersect(c, clip);
                    }
                    Paint(child, t, dst, c, depth + 1);
                    if (c != clip) Release(c);
                    _active.Remove(gid);
                    break;
                }
                case 32: // PaintComposite
                {
                    var backdrop = _layerPool.Count > 0 ? _layerPool.Pop() : new float4[dst.Length];
                    Paint(p + Off24(p + 5), t, backdrop, clip, depth + 1);
                    var source = _layerPool.Count > 0 ? _layerPool.Pop() : new float4[dst.Length];
                    Paint(p + Off24(p + 1), t, source, clip, depth + 1);
                    fixed (float4* d = dst)
                    fixed (float4* src = source)
                    fixed (float4* back = backdrop)
                        ColrKernels.Composite(d, src, back, dst.Length, _d[p + 4]);
                    Array.Clear(backdrop, 0, backdrop.Length);
                    Array.Clear(source, 0, source.Length);
                    _layerPool.Push(backdrop);
                    _layerPool.Push(source);
                    break;
                }
                default:
                    if (format >= 12 && format <= 31)
                        Paint(p + Off24(p + 1), t * Transform(p, format), dst, clip, depth + 1);
                    break;
            }
        }

        /// <summary>The transform of paint formats 12 to 31 (font space, y up; angles counter-clockwise).</summary>
        Affine Transform(int p, int format)
        {
            switch (format)
            {
                case 12:
                case 13:
                {
                    int a = p + Off24(p + 4);
                    return new Affine(Fixed(a), Fixed(a + 4), Fixed(a + 8), Fixed(a + 12), Fixed(a + 16), Fixed(a + 20));
                }
                case 14:
                case 15:
                    return Affine.Translate(S16(p + 4), S16(p + 6));
                case 16:
                case 17:
                    return Affine.Scale(F2Dot14(p + 4), F2Dot14(p + 6));
                case 18:
                case 19:
                    return Affine.Scale(F2Dot14(p + 4), F2Dot14(p + 6)).Around(S16(p + 8), S16(p + 10));
                case 20:
                case 21:
                    return Affine.Scale(F2Dot14(p + 4), F2Dot14(p + 4));
                case 22:
                case 23:
                    return Affine.Scale(F2Dot14(p + 4), F2Dot14(p + 4)).Around(S16(p + 6), S16(p + 8));
                case 24:
                case 25:
                    return Rotate(F2Dot14(p + 4));
                case 26:
                case 27:
                    return Rotate(F2Dot14(p + 4)).Around(S16(p + 6), S16(p + 8));
                case 28:
                case 29:
                    return Skew(F2Dot14(p + 4), F2Dot14(p + 6));
                default:
                    return Skew(F2Dot14(p + 4), F2Dot14(p + 6)).Around(S16(p + 8), S16(p + 10));
            }
        }

        static Affine Rotate(float halfTurns)
        {
            float a = halfTurns * math.PI;
            float c = math.cos(a), s = math.sin(a);
            return new Affine(c, s, -s, c, 0, 0);
        }

        static Affine Skew(float xHalfTurns, float yHalfTurns) =>
            new Affine(1, math.tan(yHalfTurns * math.PI), -math.tan(xHalfTurns * math.PI), 1, 0, 0);

        // ------------------------------------------------------------------ bounds

        void AddPoint(ref float4 r, float2 p)
        {
            r.xy = math.min(r.xy, p);
            r.zw = math.max(r.zw, p);
        }

        bool LayerBounds(int gid, ref float4 r)
        {
            BaseLayers(gid, out int first, out int count);
            for (int i = 0; i < count; i++) GlyphBounds(U16(_layerRecords + (first + i) * 4), Affine.Identity, ref r);
            return true;
        }

        void GlyphBounds(int gid, in Affine t, ref float4 r)
        {
            var m = t * FontFromOutline;
            foreach (var contour in Outline(gid))
                foreach (var v in contour.Vertices)
                {
                    AddPoint(ref r, m.Map(new float2(v.X, v.Y)));
                    AddPoint(ref r, m.Map(new float2(v.InX, v.InY)));
                    AddPoint(ref r, m.Map(new float2(v.OutX, v.OutY)));
                }
        }

        /// <summary>Bounds (font units) of what a paint draws; false when it fills the whole plane.</summary>
        bool Bounds(int p, in Affine t, ref float4 r, int depth)
        {
            if (depth > MaxDepth) return false;
            int format = _d[p];
            switch (format)
            {
                case 1:
                {
                    bool bounded = true;
                    int count = _d[p + 1];
                    uint first = U32(p + 2);
                    for (int i = 0; i < count; i++) bounded = Bounds(LayerPaint(first + i), t, ref r, depth + 1) & bounded;
                    return bounded;
                }
                case 10:
                    GlyphBounds(U16(p + 4), t, ref r);
                    return true;
                case 11:
                {
                    int gid = U16(p + 1);
                    if (ClipBox(gid, out var box))
                    {
                        AddPoint(ref r, t.Map(box.xy));
                        AddPoint(ref r, t.Map(box.zy));
                        AddPoint(ref r, t.Map(box.xw));
                        AddPoint(ref r, t.Map(box.zw));
                        return true;
                    }
                    int child = BasePaint(gid);
                    return child >= 0 && Bounds(child, t, ref r, depth + 1);
                }
                case 32:
                {
                    bool a = Bounds(p + Off24(p + 1), t, ref r, depth + 1);
                    bool b = Bounds(p + Off24(p + 5), t, ref r, depth + 1);
                    return a && b;
                }
                default:
                    if (format >= 12 && format <= 31) return Bounds(p + Off24(p + 1), t * Transform(p, format), ref r, depth + 1);
                    return false;
            }
        }

        // ------------------------------------------------------------------ fills

        void FillSolid(float4[] dst, Mask clip, float4 c)
        {
            if (c.w <= 0f) return;
            Extent(clip, out int x0, out int y0, out int x1, out int y1);
            fixed (float4* d = dst)
            fixed (float* cov = clip?.Cov)
                ColrKernels.FillSolid(d, cov, _w, x0, y0, x1, y1, c);
        }

        void Extent(Mask clip, out int x0, out int y0, out int x1, out int y1)
        {
            if (clip == null)
            {
                x0 = y0 = 0;
                x1 = _w;
                y1 = _h;
            }
            else
            {
                x0 = clip.X0;
                y0 = clip.Y0;
                x1 = clip.X1;
                y1 = clip.Y1;
            }
        }

        void FillGradient(int p, int format, in Affine t, float4[] dst, Mask clip)
        {
            bool variable = (format & 1) != 0;
            var line = ReadColorLine(p + Off24(p + 1), variable);
            if (line.Offsets.Length == 0) return;
            if (line.Offsets.Length == 1)
            {
                FillSolid(dst, clip, Premultiply(line.Colors[0]));
                return;
            }
            if (!(_device * t).Invert(out var inv)) return;
            var g = new ColrKernels.Gradient { A = inv.A, B = inv.B, C = inv.C, D = inv.D, E = inv.E, F = inv.F, Extend = line.Extend };
            if (format <= 5)
            {
                float2 p0 = new float2(S16(p + 4), S16(p + 6));
                float2 p1 = new float2(S16(p + 8), S16(p + 10)), p2 = new float2(S16(p + 12), S16(p + 14));
                // The colour line runs from p0 to p1 projected onto the normal of p0p2 (nanoemoji / Skia).
                float2 v = p2 - p0, n = new float2(v.y, -v.x);
                float nn = math.dot(n, n);
                float cross = (p1.x - p0.x) * v.y - (p1.y - p0.y) * v.x;
                float2 p3 = nn > 0f ? p0 + n * (math.dot(p1 - p0, n) / nn) : p0;
                float len2 = math.lengthsq(p3 - p0);
                if (cross == 0f || !(len2 > 0f))
                {
                    FillSolid(dst, clip, Premultiply(line.Colors[0]));
                    return;
                }
                g.Kind = 0;
                g.P0 = p0;
                g.Dir = (p3 - p0) / len2;
            }
            else if (format <= 7)
            {
                g.Kind = 1;
                g.C0 = new float2(S16(p + 4), S16(p + 6));
                g.R0 = U16(p + 8);
                g.Cd = new float2(S16(p + 10), S16(p + 12)) - g.C0;
                g.Dr = U16(p + 14) - g.R0;
                g.Qa = math.dot(g.Cd, g.Cd) - g.Dr * g.Dr;
            }
            else
            {
                g.Kind = 2;
                g.C0 = new float2(S16(p + 4), S16(p + 6));
                // OpenType 1.9.1: angles are biased by 180 degrees.
                g.Start = F2Dot14(p + 8) * 180f + 180f;
                g.Sweep = F2Dot14(p + 10) * 180f + 180f - g.Start;
                if (g.Sweep == 0f && line.Extend != 0) return;
            }

            int count = line.Offsets.Length;
            g.First = line.Offsets[0];
            float range = line.Offsets[count - 1] - g.First;
            g.FirstColor = Premultiply(line.Colors[0]);
            g.LastColor = Premultiply(line.Colors[count - 1]);
            if (!(range > 0f))
            {
                // All stops at one offset: pad paints the first colour before it and the last one after it.
                if (line.Extend != 0) return;
                g.Scale = 0f;
            }
            else
            {
                g.Scale = 1f / range;
                g.LutSize = LutSize;
            }
            var lut = new float4[LutSize + 1];
            Extent(clip, out int x0, out int y0, out int x1, out int y1);
            fixed (float* offsets = line.Offsets)
            fixed (float4* colors = line.Colors)
            fixed (float4* table = lut)
            fixed (float4* d = dst)
            fixed (float* cov = clip?.Cov)
            {
                if (g.Scale > 0f) ColrKernels.BuildLut(offsets, colors, count, table, LutSize);
                ColrKernels.FillGradient(d, cov, _w, x0, y0, x1, y1, g, table);
            }
        }

        ColorLine ReadColorLine(int o, bool variable)
        {
            int count = U16(o + 1), stride = variable ? 10 : 6;
            var stops = new List<(float offset, float4 color)>(count);
            for (int i = 0; i < count; i++)
            {
                int s = o + 3 + i * stride;
                stops.Add((F2Dot14(s), PaletteColor(U16(s + 2), F2Dot14(s + 4))));
            }
            // Stable sort by offset.
            var sorted = new List<(float offset, float4 color, int index)>(count);
            for (int i = 0; i < count; i++) sorted.Add((stops[i].offset, stops[i].color, i));
            sorted.Sort((x, y) => x.offset != y.offset ? x.offset.CompareTo(y.offset) : x.index.CompareTo(y.index));
            var line = new ColorLine { Extend = _d[o], Offsets = new float[count], Colors = new float4[count] };
            for (int i = 0; i < count; i++)
            {
                line.Offsets[i] = sorted[i].offset;
                line.Colors[i] = sorted[i].color;
            }
            return line;
        }

        // ------------------------------------------------------------------ coverage

        /// <summary>Outline space (Lottie glyph space: font size 100, y down) to font units (y up).</summary>
        Affine FontFromOutline => Affine.Scale(_upem / 100f, -_upem / 100f);

        List<GlyphOutlineContour> Outline(int gid)
        {
            if (!_outlines.TryGetValue(gid, out var outline)) _outlines[gid] = outline = _font.Outline(gid);
            return outline;
        }

        Mask GlyphMask(int gid, in Affine t)
        {
            var raster = new Coverage(_w, _h, _acc);
            var m = _device * t * FontFromOutline;
            foreach (var contour in Outline(gid))
            {
                var v = contour.Vertices;
                int n = v.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = v[i];
                    var b = v[(i + 1) % n];
                    raster.Cubic(m.Map(new float2(a.X, a.Y)), m.Map(new float2(a.OutX, a.OutY)),
                                 m.Map(new float2(b.InX, b.InY)), m.Map(new float2(b.X, b.Y)));
                }
            }
            return raster.Resolve(RentMask());
        }

        Mask BoxMask(float4 box, in Affine t)
        {
            var raster = new Coverage(_w, _h, _acc);
            var m = _device * t;
            float2 a = m.Map(box.xy), b = m.Map(box.zy), c = m.Map(box.zw), d = m.Map(box.xw);
            raster.Line(a, b);
            raster.Line(b, c);
            raster.Line(c, d);
            raster.Line(d, a);
            return raster.Resolve(RentMask());
        }

        float[] RentMask() => _maskPool.Count > 0 ? _maskPool.Pop() : new float[_w * _h];

        /// <summary>Returns a mask's buffer to the pool, zeroed (it is zero outside its extent).</summary>
        void Release(Mask mask)
        {
            if (mask == null) return;
            for (int y = mask.Y0; y < mask.Y1; y++) Array.Clear(mask.Cov, y * _w + mask.X0, mask.X1 - mask.X0);
            _maskPool.Push(mask.Cov);
        }

        /// <summary>mask *= clip (coverage product), shrinking the mask's extent.</summary>
        void Intersect(Mask mask, Mask clip)
        {
            fixed (float* m = mask.Cov)
            fixed (float* c = clip.Cov)
                ColrKernels.Intersect(m, c, _w, mask.X0, mask.Y0, mask.X1, mask.Y1, clip.X0, clip.Y0, clip.X1, clip.Y1);
            int x0 = math.max(mask.X0, clip.X0), y0 = math.max(mask.Y0, clip.Y0);
            mask.X0 = x0;
            mask.Y0 = y0;
            mask.X1 = math.max(x0, math.min(mask.X1, clip.X1));
            mask.Y1 = math.max(y0, math.min(mask.Y1, clip.Y1));
        }

        /// <summary>Anti-aliased coverage of closed paths (signed area accumulation, non-zero approximated by |winding| ≤ 1).</summary>
        sealed class Coverage
        {
            readonly int _w, _h, _stride;
            readonly float[] _acc;
            int _minX = int.MaxValue, _minY = int.MaxValue, _maxX = int.MinValue, _maxY = int.MinValue;

            /// <param name="acc">Zeroed accumulation buffer of (w + 2) × h, left zeroed by <see cref="Resolve"/>.</param>
            public Coverage(int w, int h, float[] acc)
            {
                _w = w;
                _h = h;
                _stride = w + 2;
                _acc = acc;
            }

            public void Cubic(float2 p0, float2 p1, float2 p2, float2 p3)
            {
                float2 dd = math.max(math.abs(p0 - 2f * p1 + p2), math.abs(p1 - 2f * p2 + p3));
                int n = (int)math.ceil(math.sqrt(math.length(dd) * 0.75f / 0.05f));
                n = math.clamp(n, 1, 100);
                float2 prev = p0;
                for (int i = 1; i <= n; i++)
                {
                    float t = (float)i / n, u = 1f - t;
                    float2 q = i == n ? p3 : u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
                    Line(prev, q);
                    prev = q;
                }
            }

            public void Line(float2 a, float2 b)
            {
                if (!math.all(math.isfinite(a)) || !math.all(math.isfinite(b))) return;
                // Split at the left and right borders: parts outside become vertical lines on the border.
                if ((a.x < 0f && b.x > 0f) || (a.x > 0f && b.x < 0f))
                {
                    float2 m = new float2(0f, a.y + (b.y - a.y) * (0f - a.x) / (b.x - a.x));
                    Line(a, m);
                    Line(m, b);
                    return;
                }
                if ((a.x < _w && b.x > _w) || (a.x > _w && b.x < _w))
                {
                    float2 m = new float2(_w, a.y + (b.y - a.y) * (_w - a.x) / (b.x - a.x));
                    Line(a, m);
                    Line(m, b);
                    return;
                }
                Accumulate(math.clamp(a.x, 0f, _w), a.y, math.clamp(b.x, 0f, _w), b.y);
            }

            // font-rs line accumulation.
            void Accumulate(float x0, float y0, float x1, float y1)
            {
                if (math.abs(y0 - y1) <= 1e-7f) return;
                float dir = 1f;
                if (y0 > y1)
                {
                    dir = -1f;
                    (x0, x1) = (x1, x0);
                    (y0, y1) = (y1, y0);
                }
                float dxdy = (x1 - x0) / (y1 - y0);
                float x = x0;
                if (y0 < 0f) x -= y0 * dxdy;
                int yStart = math.max(0, (int)math.floor(y0)), yEnd = math.min(_h, (int)math.ceil(y1));
                if (yStart >= yEnd) return;
                _minY = math.min(_minY, yStart);
                _maxY = math.max(_maxY, yEnd);
                _minX = math.min(_minX, (int)math.floor(math.min(x0, x1)));
                _maxX = math.max(_maxX, (int)math.ceil(math.max(x0, x1)) + 1);
                for (int y = yStart; y < yEnd; y++)
                {
                    int row = y * _stride;
                    float dy = math.min(y + 1f, y1) - math.max(y, y0);
                    float xNext = x + dxdy * dy;
                    float d = dy * dir;
                    float xa = math.min(x, xNext), xb = math.max(x, xNext);
                    float xaFloor = math.floor(xa);
                    int xai = (int)xaFloor;
                    float xbCeil = math.ceil(xb);
                    int xbi = (int)xbCeil;
                    if (xbi <= xai + 1)
                    {
                        float xmf = 0.5f * (x + xNext) - xaFloor;
                        _acc[row + xai] += d - d * xmf;
                        _acc[row + xai + 1] += d * xmf;
                    }
                    else
                    {
                        float s = 1f / (xb - xa);
                        float xaf = xa - xaFloor;
                        float a0 = 0.5f * s * (1f - xaf) * (1f - xaf);
                        float xbf = xb - xbCeil + 1f;
                        float am = 0.5f * s * xbf * xbf;
                        _acc[row + xai] += d * a0;
                        if (xbi == xai + 2) _acc[row + xai + 1] += d * (1f - a0 - am);
                        else
                        {
                            float a1 = s * (1.5f - xaf);
                            _acc[row + xai + 1] += d * (a1 - a0);
                            for (int xi = xai + 2; xi < xbi - 1; xi++) _acc[row + xi] += d * s;
                            float a2 = a1 + (xbi - xai - 3) * s;
                            _acc[row + xbi - 1] += d * (1f - a2 - am);
                        }
                        _acc[row + xbi] += d * am;
                    }
                    x = xNext;
                }
            }

            /// <param name="cov">Zeroed coverage buffer of w × h.</param>
            public Mask Resolve(float[] cov)
            {
                var mask = new Mask { Cov = cov };
                if (_minY >= _maxY) return mask;
                mask.X0 = math.max(0, _minX);
                mask.X1 = math.min(_w, _maxX);
                mask.Y0 = _minY;
                mask.Y1 = _maxY;
                fixed (float* acc = _acc)
                fixed (float* c = cov)
                    ColrKernels.Resolve(acc, c, _stride, _w, mask.X0, mask.Y0, mask.X1, mask.Y1);
                return mask;
            }
        }

        // ------------------------------------------------------------------ reading

        int U16(int o) => (_d[o] << 8) | _d[o + 1];
        int S16(int o) => (short)((_d[o] << 8) | _d[o + 1]);
        uint U32(int o) => ((uint)_d[o] << 24) | ((uint)_d[o + 1] << 16) | ((uint)_d[o + 2] << 8) | _d[o + 3];
        int Off24(int o) => (_d[o] << 16) | (_d[o + 1] << 8) | _d[o + 2];
        float F2Dot14(int o) => S16(o) / 16384f;
        float Fixed(int o) => (int)U32(o) / 65536f;
    }
}
