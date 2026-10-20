using System;
using System.Collections.Generic;
using System.Text;

namespace Mynell.Lottie.Text
{
    /// <summary>A glyph outline contour in Lottie glyph space (font size 100, y down): vertices with absolute tangents.</summary>
    public struct GlyphOutlineVertex
    {
        public float X, Y, InX, InY, OutX, OutY;
    }

    public sealed class GlyphOutlineContour
    {
        public readonly List<GlyphOutlineVertex> Vertices = new List<GlyphOutlineVertex>();
        public bool Closed = true;
    }

    /// <summary>
    /// Minimal OpenType / TrueType reader: names, metrics, cmap and outlines from 'glyf' (quadratic, converted to cubic)
    /// or 'CFF ' (Type 2 charstrings). Supports font collections (.ttc).
    /// </summary>
    public sealed partial class TrueTypeFont
    {
        readonly byte[] _d;
        readonly Dictionary<string, int> _tables = new Dictionary<string, int>();
        readonly Dictionary<string, int> _tableLengths = new Dictionary<string, int>();
        int _numGlyphs, _numHMetrics, _locFormat;
        int _cmapOffset;
        int _cmapFormat;
        bool _cff;

        public int UnitsPerEm { get; private set; } = 1000;
        public int Ascender { get; private set; }
        public int Descender { get; private set; }
        public string Family { get; private set; } = "";
        public string SubFamily { get; private set; } = "";
        public string FullName { get; private set; } = "";
        public string PostScriptName { get; private set; } = "";
        public string TypographicFamily { get; private set; } = "";
        public string TypographicSubFamily { get; private set; } = "";
        public bool IsValid { get; private set; }

        TrueTypeFont(byte[] data, int offset)
        {
            _d = data;
            try
            {
                Parse(offset);
                IsValid = true;
            }
            catch (Exception)
            {
                IsValid = false;
            }
        }

        /// <summary>Loads every font of a file (one for .ttf / .otf, several for .ttc).</summary>
        public static List<TrueTypeFont> Load(byte[] data)
        {
            var list = new List<TrueTypeFont>();
            if (data == null || data.Length < 12) return list;
            if (data[0] == 't' && data[1] == 't' && data[2] == 'c' && data[3] == 'f')
            {
                int count = (int)U32(data, 8);
                for (int i = 0; i < count && i < 256; i++)
                {
                    var f = new TrueTypeFont(data, (int)U32(data, 12 + i * 4));
                    if (f.IsValid) list.Add(f);
                }
            }
            else
            {
                var f = new TrueTypeFont(data, 0);
                if (f.IsValid) list.Add(f);
            }
            return list;
        }

        /// <summary>The font starting at <paramref name="offset"/> of a file (one face of a collection); null when invalid.</summary>
        internal static TrueTypeFont LoadFace(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 12 > data.Length) return null;
            var f = new TrueTypeFont(data, offset);
            return f.IsValid ? f : null;
        }

        static uint U32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
        int U16(int o) => _d[o] << 8 | _d[o + 1];
        int S16(int o) => (short)(_d[o] << 8 | _d[o + 1]);
        uint U32(int o) => U32(_d, o);

        void Parse(int offset)
        {
            int numTables = U16(offset + 4);
            for (int i = 0; i < numTables; i++)
            {
                int rec = offset + 12 + i * 16;
                string tag = Encoding.ASCII.GetString(_d, rec, 4);
                _tables[tag] = (int)U32(rec + 8);
                _tableLengths[tag] = (int)U32(rec + 12);
            }
            int head = _tables["head"];
            UnitsPerEm = U16(head + 18);
            _locFormat = S16(head + 50);
            _numGlyphs = U16(_tables["maxp"] + 4);
            int hhea = _tables["hhea"];
            Ascender = S16(hhea + 4);
            Descender = S16(hhea + 6);
            _numHMetrics = U16(hhea + 34);
            _cff = _tables.ContainsKey("CFF ") && !_tables.ContainsKey("glyf");
            ParseCmap();
            ParseNames();
            if (_cff) InitCff(_tables["CFF "]);
        }

        void ParseCmap()
        {
            if (!_tables.TryGetValue("cmap", out int cmap)) return;
            int n = U16(cmap + 2);
            int best = -1, bestScore = -1;
            for (int i = 0; i < n; i++)
            {
                int rec = cmap + 4 + i * 8;
                int platform = U16(rec), encoding = U16(rec + 2);
                int sub = cmap + (int)U32(rec + 4);
                int format = U16(sub);
                int score = -1;
                if (format == 12 && (platform == 3 && encoding == 10 || platform == 0)) score = 4;
                else if (format == 4 && platform == 3 && encoding == 1) score = 3;
                else if (format == 4 && platform == 0) score = 2;
                else if (format == 6 || format == 0) score = 1;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = sub;
                }
            }
            if (best >= 0)
            {
                _cmapOffset = best;
                _cmapFormat = U16(best);
            }
        }

        void ParseNames()
        {
            if (!_tables.TryGetValue("name", out int name)) return;
            var n = ParseNameTable(_d, name);
            Family = n.Family;
            SubFamily = n.SubFamily;
            FullName = n.FullName;
            PostScriptName = n.PostScriptName;
            TypographicFamily = n.TypographicFamily;
            TypographicSubFamily = n.TypographicSubFamily;
        }

        /// <summary>The naming strings of one font face.</summary>
        public sealed class FaceNames
        {
            public string Family = "", SubFamily = "", FullName = "", PostScriptName = "", TypographicFamily = "", TypographicSubFamily = "";
        }

        static int U16(byte[] d, int o) => d[o] << 8 | d[o + 1];

        static FaceNames ParseNameTable(byte[] d, int name)
        {
            var r = new FaceNames();
            int count = U16(d, name + 2);
            int strings = name + U16(d, name + 4);
            for (int i = 0; i < count; i++)
            {
                int rec = name + 6 + i * 12;
                int platform = U16(d, rec), encoding = U16(d, rec + 2), language = U16(d, rec + 4), id = U16(d, rec + 6);
                int len = U16(d, rec + 8), off = U16(d, rec + 10);
                if (strings + off + len > d.Length) continue;
                string s;
                if (platform == 3 || platform == 0)
                {
                    if (platform == 3 && language != 0x409 && language != 0) continue;
                    s = Encoding.BigEndianUnicode.GetString(d, strings + off, len);
                }
                else if (platform == 1 && encoding == 0) s = Encoding.ASCII.GetString(d, strings + off, len);
                else continue;
                switch (id)
                {
                    case 1: if (r.Family.Length == 0 || platform == 3) r.Family = s; break;
                    case 2: if (r.SubFamily.Length == 0 || platform == 3) r.SubFamily = s; break;
                    case 4: if (r.FullName.Length == 0 || platform == 3) r.FullName = s; break;
                    case 6: if (r.PostScriptName.Length == 0 || platform == 3) r.PostScriptName = s; break;
                    case 16: if (r.TypographicFamily.Length == 0 || platform == 3) r.TypographicFamily = s; break;
                    case 17: if (r.TypographicSubFamily.Length == 0 || platform == 3) r.TypographicSubFamily = s; break;
                }
            }
            return r;
        }

        /// <summary>Names of every face in a font file, reading only the table directories and name tables.</summary>
        public static List<FaceNames> ReadNames(string path)
        {
            var list = new List<FaceNames>();
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 4096);
            byte[] head = ReadAt(fs, 0, 12);
            if (head == null) return list;
            var offsets = new List<long>();
            if (head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')
            {
                int count = (int)Math.Min(U32(head, 8), 256u);
                byte[] dir = ReadAt(fs, 12, count * 4);
                if (dir == null) return list;
                for (int i = 0; i < count; i++) offsets.Add(U32(dir, i * 4));
            }
            else offsets.Add(0);
            foreach (long offset in offsets)
            {
                byte[] hdr = ReadAt(fs, offset, 12);
                if (hdr == null) continue;
                int numTables = U16(hdr, 4);
                byte[] dir = ReadAt(fs, offset + 12, numTables * 16);
                if (dir == null) continue;
                for (int t = 0; t < numTables; t++)
                {
                    if (dir[t * 16] != 'n' || dir[t * 16 + 1] != 'a' || dir[t * 16 + 2] != 'm' || dir[t * 16 + 3] != 'e') continue;
                    uint tableOffset = U32(dir, t * 16 + 8), tableLength = U32(dir, t * 16 + 12);
                    if (tableLength < 6 || tableLength > 1 << 22) break;
                    byte[] table = ReadAt(fs, tableOffset, (int)tableLength);
                    if (table != null) list.Add(ParseNameTable(table, 0));
                    break;
                }
            }
            return list;
        }

        static byte[] ReadAt(System.IO.Stream s, long offset, int count)
        {
            if (count < 0 || offset < 0 || offset + count > s.Length) return null;
            s.Position = offset;
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) return null;
                read += n;
            }
            return buf;
        }

        /// <summary>Glyph index for a Unicode code point (0 = missing).</summary>
        public int GlyphIndex(int codePoint)
        {
            int t = _cmapOffset;
            if (t == 0) return 0;
            switch (_cmapFormat)
            {
                case 0:
                    return codePoint < 256 ? _d[t + 6 + codePoint] : 0;
                case 4:
                {
                    if (codePoint > 0xFFFF) return 0;
                    int segX2 = U16(t + 6);
                    int ends = t + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
                    for (int s = 0; s < segX2; s += 2)
                    {
                        int end = U16(ends + s);
                        if (codePoint > end) continue;
                        int start = U16(starts + s);
                        if (codePoint < start) return 0;
                        int delta = S16(deltas + s);
                        int rangeOffset = U16(ranges + s);
                        if (rangeOffset == 0) return (codePoint + delta) & 0xFFFF;
                        int gi = U16(ranges + s + rangeOffset + (codePoint - start) * 2);
                        return gi == 0 ? 0 : (gi + delta) & 0xFFFF;
                    }
                    return 0;
                }
                case 6:
                {
                    int first = U16(t + 6), count = U16(t + 8);
                    return codePoint >= first && codePoint < first + count ? U16(t + 10 + (codePoint - first) * 2) : 0;
                }
                case 12:
                {
                    int groups = (int)U32(t + 12);
                    int lo = 0, hi = groups - 1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) >> 1;
                        int g = t + 16 + mid * 12;
                        uint start = U32(g), end = U32(g + 4);
                        if ((uint)codePoint < start) hi = mid - 1;
                        else if ((uint)codePoint > end) lo = mid + 1;
                        else return (int)(U32(g + 8) + ((uint)codePoint - start));
                    }
                    return 0;
                }
            }
            return 0;
        }

        /// <summary>Advance width in font units.</summary>
        public int Advance(int glyph)
        {
            if (!_tables.TryGetValue("hmtx", out int hmtx) || _numHMetrics == 0) return 0;
            int i = Math.Min(glyph, _numHMetrics - 1);
            return U16(hmtx + i * 4);
        }

        /// <summary>
        /// Outline of a glyph in Lottie glyph space (font size 100 -> scale 100 / unitsPerEm, y flipped).
        /// </summary>
        public List<GlyphOutlineContour> Outline(int glyph)
        {
            var result = new List<GlyphOutlineContour>();
            if (glyph < 0 || glyph >= _numGlyphs) return result;
            float scale = 100f / UnitsPerEm;
            try
            {
                if (_cff) CffOutline(glyph, result, scale);
                else GlyfOutline(glyph, result, scale, 1, 0, 0, 1, 0, 0, 0);
            }
            catch (Exception)
            {
                result.Clear();
            }
            return result;
        }

        // ------------------------------------------------------------------ glyf (TrueType quadratic outlines)

        int GlyfOffset(int glyph, out int length)
        {
            int loca = _tables["loca"];
            int start, end;
            if (_locFormat == 0)
            {
                start = U16(loca + glyph * 2) * 2;
                end = U16(loca + glyph * 2 + 2) * 2;
            }
            else
            {
                start = (int)U32(loca + glyph * 4);
                end = (int)U32(loca + glyph * 4 + 4);
            }
            length = end - start;
            return _tables["glyf"] + start;
        }

        void GlyfOutline(int glyph, List<GlyphOutlineContour> result, float scale,
            float a, float b, float c, float dd, float e, float f, int depth)
        {
            if (depth > 8) return;
            int o = GlyfOffset(glyph, out int length);
            if (length <= 0) return;
            int nContours = S16(o);
            if (nContours >= 0)
            {
                SimpleGlyph(o, nContours, result, scale, a, b, c, dd, e, f);
                return;
            }
            // Composite glyph.
            int p = o + 10;
            while (true)
            {
                int flags = U16(p);
                int component = U16(p + 2);
                p += 4;
                float dx, dy;
                if ((flags & 1) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; }
                else { dx = (sbyte)_d[p]; dy = (sbyte)_d[p + 1]; p += 2; }
                float ca = 1, cb = 0, cc = 0, cd = 1;
                if ((flags & 8) != 0) { ca = cd = F2Dot14(p); p += 2; }
                else if ((flags & 0x40) != 0) { ca = F2Dot14(p); cd = F2Dot14(p + 2); p += 4; }
                else if ((flags & 0x80) != 0) { ca = F2Dot14(p); cb = F2Dot14(p + 2); cc = F2Dot14(p + 4); cd = F2Dot14(p + 6); p += 8; }
                // compose: point' = parent(component(point))
                float na = ca * a + cb * c, nb = ca * b + cb * dd;
                float nc = cc * a + cd * c, nd = cc * b + cd * dd;
                float ne = dx * a + dy * c + e, nf = dx * b + dy * dd + f;
                GlyfOutline(component, result, scale, na, nb, nc, nd, ne, nf, depth + 1);
                if ((flags & 0x20) == 0) break;
            }
        }

        float F2Dot14(int o) => S16(o) / 16384f;

        void SimpleGlyph(int o, int nContours, List<GlyphOutlineContour> result, float scale,
            float a, float b, float c, float dd, float e, float f)
        {
            var ends = new int[nContours];
            for (int i = 0; i < nContours; i++) ends[i] = U16(o + 10 + i * 2);
            int nPoints = nContours > 0 ? ends[nContours - 1] + 1 : 0;
            int insLen = U16(o + 10 + nContours * 2);
            int p = o + 12 + nContours * 2 + insLen;
            var flags = new byte[nPoints];
            for (int i = 0; i < nPoints;)
            {
                byte fl = _d[p++];
                flags[i++] = fl;
                if ((fl & 8) != 0)
                {
                    int rep = _d[p++];
                    while (rep-- > 0 && i < nPoints) flags[i++] = fl;
                }
            }
            var xs = new int[nPoints];
            var ys = new int[nPoints];
            int v = 0;
            for (int i = 0; i < nPoints; i++)
            {
                byte fl = flags[i];
                if ((fl & 2) != 0) { int d = _d[p++]; v += (fl & 16) != 0 ? d : -d; }
                else if ((fl & 16) == 0) { v += S16(p); p += 2; }
                xs[i] = v;
            }
            v = 0;
            for (int i = 0; i < nPoints; i++)
            {
                byte fl = flags[i];
                if ((fl & 4) != 0) { int d = _d[p++]; v += (fl & 32) != 0 ? d : -d; }
                else if ((fl & 32) == 0) { v += S16(p); p += 2; }
                ys[i] = v;
            }

            int start = 0;
            for (int ci = 0; ci < nContours; ci++)
            {
                int end = ends[ci];
                int n = end - start + 1;
                if (n <= 0) { start = end + 1; continue; }
                // Build the on/off point list, inserting implied on-curve points.
                var pts = new List<(float x, float y, bool on)>(n * 2);
                for (int k = 0; k < n; k++)
                {
                    int idx = start + k;
                    float tx = a * xs[idx] + c * ys[idx] + e;
                    float ty = b * xs[idx] + dd * ys[idx] + f;
                    pts.Add((tx * scale, -ty * scale, (flags[idx] & 1) != 0));
                }
                start = end + 1;
                var expanded = new List<(float x, float y, bool on)>(n * 2);
                for (int k = 0; k < pts.Count; k++)
                {
                    var cur = pts[k];
                    var next = pts[(k + 1) % pts.Count];
                    expanded.Add(cur);
                    if (!cur.on && !next.on) expanded.Add(((cur.x + next.x) * 0.5f, (cur.y + next.y) * 0.5f, true));
                }
                // Rotate so the list starts with an on-curve point.
                int first = expanded.FindIndex(q => q.on);
                if (first < 0) continue;
                var ring = new List<(float x, float y, bool on)>(expanded.Count);
                for (int k = 0; k < expanded.Count; k++) ring.Add(expanded[(first + k) % expanded.Count]);

                var contour = new GlyphOutlineContour();
                BuildContour(contour, ring);
                result.Add(contour);
            }
        }

        /// <summary>
        /// Builds cubic vertices from a ring of TrueType points that starts on-curve and never has two consecutive
        /// off-curve points (implied points already inserted): each quadratic (p0, q, p1) becomes a cubic.
        /// </summary>
        static void BuildContour(GlyphOutlineContour contour, List<(float x, float y, bool on)> ring)
        {
            int count = ring.Count;
            // First pass: vertices = on-curve points.
            var onIndices = new List<int>();
            for (int k = 0; k < count; k++) if (ring[k].on) onIndices.Add(k);
            int m = onIndices.Count;
            for (int i = 0; i < m; i++)
            {
                var p = ring[onIndices[i]];
                contour.Vertices.Add(new GlyphOutlineVertex { X = p.x, Y = p.y, InX = p.x, InY = p.y, OutX = p.x, OutY = p.y });
            }
            // Second pass: segments between consecutive on points (at most one off point in between after expansion).
            for (int i = 0; i < m; i++)
            {
                int a = onIndices[i];
                int bIdx = onIndices[(i + 1) % m];
                int gap = (bIdx - a + count) % count;
                if (gap == 2)
                {
                    var p0 = ring[a];
                    var q = ring[(a + 1) % count];
                    var p1 = ring[bIdx];
                    var v0 = contour.Vertices[i];
                    v0.OutX = p0.x + 2f / 3f * (q.x - p0.x);
                    v0.OutY = p0.y + 2f / 3f * (q.y - p0.y);
                    contour.Vertices[i] = v0;
                    int ni = (i + 1) % m;
                    var v1 = contour.Vertices[ni];
                    v1.InX = p1.x + 2f / 3f * (q.x - p1.x);
                    v1.InY = p1.y + 2f / 3f * (q.y - p1.y);
                    contour.Vertices[ni] = v1;
                }
            }
        }
    }
}
