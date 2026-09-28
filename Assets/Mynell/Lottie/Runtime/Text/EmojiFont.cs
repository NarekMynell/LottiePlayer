using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mynell.Lottie.Text
{
    /// <summary>
    /// Colour emoji from bitmap fonts: Apple Color Emoji ('sbix') and Noto Color Emoji ('CBDT' / 'CBLC'). Only the tables
    /// needed are read (the fonts are large). Emoji sequences (skin tones, ZWJ, flags, keycaps) are resolved through the
    /// glyph names (Apple) or 'GSUB' ligatures (Noto).
    /// </summary>
    public sealed class EmojiFont : IDisposable
    {
        /// <summary>A bitmap glyph: PNG data and its box in Lottie glyph space (font size 100, y down).</summary>
        public struct Glyph
        {
            public byte[] Png;
            public float X, Y, Width, Height;   // width < 0: mirrored horizontally
            public float Advance;
        }

        readonly Stream _s;
        readonly long _base;
        readonly Dictionary<string, (long offset, int length)> _tables = new Dictionary<string, (long, int)>();
        int _unitsPerEm = 1000, _numGlyphs, _numHMetrics;
        ushort[] _advances;
        readonly Dictionary<int, int> _cmap = new Dictionary<int, int>();
        Dictionary<string, int> _names;
        // sbix: the largest strike.
        long _sbixStrike = -1;
        int _sbixPpem;
        // CBDT: glyph -> (image format, data offset, data length) of the largest strike.
        Dictionary<int, (int format, long offset, int length)> _cbdt;
        int _cbdtPpem;
        // GSUB ligatures: first glyph -> list of (components after the first, ligature glyph).
        Dictionary<int, List<(int[] rest, int glyph)>> _ligatures;

        EmojiFont(Stream s, long fontOffset)
        {
            _s = s;
            _base = fontOffset;
        }

        /// <summary>Well-known colour emoji fonts of the current OS (first existing path), null when none.</summary>
        public static string SystemFontPath()
        {
            string[] candidates =
            {
                "/System/Library/Fonts/Apple Color Emoji.ttc",
                "/System/Library/Fonts/Core/AppleColorEmoji.ttc",
                "/system/fonts/NotoColorEmoji.ttf",
                "/system/fonts/NotoColorEmojiLegacy.ttf",
                "/usr/share/fonts/truetype/noto/NotoColorEmoji.ttf",
                "/usr/share/fonts/noto/NotoColorEmoji.ttf",
                "/usr/share/fonts/google-noto-emoji/NotoColorEmoji.ttf",
            };
            foreach (var p in candidates)
                try { if (File.Exists(p)) return p; }
                catch (Exception) { }
            return null;
        }

        /// <summary>Opens a bitmap emoji font file (.ttf / .ttc); null when it has no supported colour glyphs.</summary>
        public static EmojiFont Open(string path)
        {
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                var f = Open(fs);
                if (f == null) fs.Dispose();
                return f;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Opens a bitmap emoji font from a stream (kept open until disposed).</summary>
        public static EmojiFont Open(Stream s)
        {
            try
            {
                var head = Read(s, 0, 12);
                long offset = 0;
                if (head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')
                    offset = U32(Read(s, 12, 4), 0);
                var f = new EmojiFont(s, offset);
                return f.Parse() ? f : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose() => _s.Dispose();

        bool Parse()
        {
            var dir = Read(_s, _base, 12);
            int numTables = U16(dir, 4);
            var records = Read(_s, _base + 12, numTables * 16);
            for (int i = 0; i < numTables; i++)
            {
                string tag = Encoding.ASCII.GetString(records, i * 16, 4);
                _tables[tag] = (U32(records, i * 16 + 8), (int)U32(records, i * 16 + 12));
            }
            if (!_tables.ContainsKey("cmap") || !(_tables.ContainsKey("sbix") || _tables.ContainsKey("CBDT"))) return false;
            var headT = Table("head");
            _unitsPerEm = Math.Max(1, U16(headT, 18));
            _numGlyphs = U16(Table("maxp"), 4);
            _numHMetrics = U16(Table("hhea"), 34);
            var hmtx = Table("hmtx");
            _advances = new ushort[_numGlyphs];
            ushort last = 0;
            for (int g = 0; g < _numGlyphs; g++)
            {
                if (g < _numHMetrics && g * 4 + 2 <= hmtx.Length) last = (ushort)U16(hmtx, g * 4);
                _advances[g] = last;
            }
            ParseCmap(Table("cmap"));
            if (_tables.ContainsKey("post")) ParsePost(Table("post"));
            if (_tables.ContainsKey("sbix")) ParseSbix();
            if (_sbixStrike < 0 && _tables.ContainsKey("CBDT") && _tables.ContainsKey("CBLC")) ParseCblc();
            if (_tables.ContainsKey("GSUB")) ParseGsub(Table("GSUB"));
            return _sbixStrike >= 0 || _cbdt != null;
        }

        byte[] Table(string tag)
        {
            var t = _tables[tag];
            return Read(_s, t.offset, t.length);
        }

        // ------------------------------------------------------------------ lookup

        /// <summary>The colour glyph for an emoji letter (a grapheme: one or more code points).</summary>
        public bool TryGetGlyph(string text, out Glyph glyph)
        {
            glyph = default;
            var cps = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsSurrogatePair(text, i)) cp = char.ConvertToUtf32(text[i], text[++i]);
                cps.Add(cp);
            }
            if (cps.Count == 0) return false;
            int gid = -1;
            if (_names != null) gid = ByName(cps);
            if (gid < 0) gid = ByLigature(cps);
            if (gid <= 0) return false;
            return Bitmap(gid, out glyph);
        }

        /// <summary>Apple naming: u1F468_u2695.3 (ZWJ / VS16 dropped, skin tone modifier as the .N suffix).</summary>
        int ByName(List<int> cps)
        {
            var sb = new StringBuilder();
            int tone = 0;
            foreach (int cp in cps)
            {
                if (cp == 0xFE0F || cp == 0x200D || cp == 0xFE0E) continue;
                if (cp >= 0x1F3FB && cp <= 0x1F3FF)
                {
                    if (tone == 0) tone = cp - 0x1F3FB + 1;
                    continue;
                }
                if (sb.Length > 0) sb.Append('_');
                sb.Append('u').Append(cp.ToString(cp > 0xFFFF ? "X5" : "X4"));
            }
            string name = sb.ToString();
            if (name.Length == 0) return -1;
            if (tone > 0 && _names.TryGetValue(name + "." + tone, out int g)) return g;
            if (_names.TryGetValue(name + ".0", out g)) return g;
            if (_names.TryGetValue(name, out g)) return g;
            return -1;
        }

        /// <summary>cmap + GSUB ligatures (longest match at the start of the sequence).</summary>
        int ByLigature(List<int> cps)
        {
            var glyphs = new List<int>();
            foreach (int cp in cps)
                if (_cmap.TryGetValue(cp, out int g)) glyphs.Add(g);
                else if (cp != 0xFE0F && cp != 0xFE0E) glyphs.Add(0);
            if (glyphs.Count == 0) return -1;
            if (glyphs.Count > 1 && _ligatures != null && _ligatures.TryGetValue(glyphs[0], out var candidates))
            {
                int best = -1, bestLen = 0;
                foreach (var (rest, lig) in candidates)
                {
                    if (rest.Length + 1 > glyphs.Count || rest.Length < bestLen) continue;
                    bool match = true;
                    for (int k = 0; k < rest.Length && match; k++) match = glyphs[k + 1] == rest[k];
                    if (match) { best = lig; bestLen = rest.Length; }
                }
                if (best >= 0) return best;
            }
            return glyphs[0];
        }

        bool Bitmap(int gid, out Glyph glyph)
        {
            glyph = default;
            float advance = gid < _advances.Length ? _advances[gid] * 100f / _unitsPerEm : 100f;
            if (_sbixStrike >= 0)
            {
                bool flip = false;
                for (int hop = 0; hop < 3; hop++)
                {
                    var offs = Read(_s, _sbixStrike + 4 + gid * 4L, 8);
                    long o0 = U32(offs, 0), o1 = U32(offs, 4);
                    if (o1 <= o0 + 8) return false;
                    var data = Read(_s, _sbixStrike + o0, (int)(o1 - o0));
                    int ox = (short)U16(data, 0), oy = (short)U16(data, 2);
                    string type = Encoding.ASCII.GetString(data, 4, 4);
                    if (type == "dupe" || type == "flip")
                    {
                        flip ^= type == "flip";
                        gid = U16(data, 8);
                        continue;
                    }
                    if (type != "png ") return false;
                    var png = new byte[data.Length - 8];
                    Buffer.BlockCopy(data, 8, png, 0, png.Length);
                    if (!PngSize(png, out int w, out int h)) return false;
                    float s = 100f / _sbixPpem;
                    // Lower-left corner at the glyph origin + offset (pixels, y up) -> y-down glyph space.
                    glyph = new Glyph
                    {
                        Png = png,
                        X = flip ? (ox + w) * s : ox * s,
                        Y = -(oy + h) * s,
                        Width = flip ? -w * s : w * s,
                        Height = h * s,
                        Advance = advance,
                    };
                    return true;
                }
                return false;
            }
            if (_cbdt != null && _cbdt.TryGetValue(gid, out var rec))
            {
                var data = Read(_s, rec.offset, rec.length);
                int pos, w, h;
                float bx, by;
                switch (rec.format)
                {
                    case 17: // small metrics + png
                        h = data[0]; w = data[1]; bx = (sbyte)data[2]; by = (sbyte)data[3];
                        pos = 5;
                        break;
                    case 18: // big metrics + png
                        h = data[0]; w = data[1]; bx = (sbyte)data[2]; by = (sbyte)data[3];
                        pos = 8;
                        break;
                    case 19: // metrics in CBLC (not kept): assume the strike box
                        pos = 0; w = h = _cbdtPpem; bx = 0; by = _cbdtPpem * 0.8f;
                        break;
                    default:
                        return false;
                }
                int len = (int)U32(data, pos);
                if (pos + 4 + len > data.Length) return false;
                var png = new byte[len];
                Buffer.BlockCopy(data, pos + 4, png, 0, len);
                if (rec.format == 19 && PngSize(png, out int pw, out int ph)) { w = pw; h = ph; }
                float s = 100f / _cbdtPpem;
                glyph = new Glyph { Png = png, X = bx * s, Y = -by * s, Width = w * s, Height = h * s, Advance = advance };
                return true;
            }
            return false;
        }

        static bool PngSize(byte[] png, out int w, out int h)
        {
            w = h = 0;
            if (png.Length < 24 || png[0] != 0x89 || png[1] != 'P') return false;
            w = (int)U32(png, 16);
            h = (int)U32(png, 20);
            return w > 0 && h > 0;
        }

        // ------------------------------------------------------------------ tables

        void ParseCmap(byte[] t)
        {
            int n = U16(t, 2);
            int best = -1, bestFormat = 0;
            for (int i = 0; i < n; i++)
            {
                int platform = U16(t, 4 + i * 8), encoding = U16(t, 6 + i * 8);
                int off = (int)U32(t, 8 + i * 8);
                int format = U16(t, off);
                bool unicode = platform == 0 || (platform == 3 && (encoding == 1 || encoding == 10));
                if (!unicode) continue;
                if (format == 12 && bestFormat != 12) { best = off; bestFormat = 12; }
                else if (format == 4 && bestFormat == 0) { best = off; bestFormat = 4; }
            }
            if (best < 0) return;
            if (bestFormat == 12)
            {
                int groups = (int)U32(t, best + 12);
                for (int gi = 0; gi < groups; gi++)
                {
                    int p = best + 16 + gi * 12;
                    int start = (int)U32(t, p), end = (int)U32(t, p + 4), glyph = (int)U32(t, p + 8);
                    if (end - start > 0x20000) continue;
                    for (int c = start; c <= end; c++) _cmap[c] = glyph + (c - start);
                }
                return;
            }
            int segX2 = U16(t, best + 6);
            int endBase = best + 14, startBase = endBase + segX2 + 2, deltaBase = startBase + segX2, rangeBase = deltaBase + segX2;
            for (int s = 0; s < segX2 / 2; s++)
            {
                int end = U16(t, endBase + s * 2), start = U16(t, startBase + s * 2);
                int delta = (short)U16(t, deltaBase + s * 2), range = U16(t, rangeBase + s * 2);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int g;
                    if (range == 0) g = (c + delta) & 0xFFFF;
                    else
                    {
                        int gp = rangeBase + s * 2 + range + (c - start) * 2;
                        if (gp + 2 > t.Length) continue;
                        g = U16(t, gp);
                        if (g != 0) g = (g + delta) & 0xFFFF;
                    }
                    if (g != 0) _cmap[c] = g;
                }
            }
        }

        void ParsePost(byte[] t)
        {
            if (U32(t, 0) != 0x00020000 || t.Length < 34) return;
            int n = U16(t, 32);
            var indexes = new int[n];
            for (int i = 0; i < n; i++) indexes[i] = U16(t, 34 + i * 2);
            var custom = new List<string>();
            int p = 34 + n * 2;
            while (p < t.Length)
            {
                int len = t[p];
                if (p + 1 + len > t.Length) break;
                custom.Add(Encoding.ASCII.GetString(t, p + 1, len));
                p += 1 + len;
            }
            _names = new Dictionary<string, int>(n);
            for (int g = 0; g < n; g++)
            {
                int idx = indexes[g];
                if (idx >= 258 && idx - 258 < custom.Count) _names[custom[idx - 258]] = g;
            }
        }

        void ParseSbix()
        {
            var t = _tables["sbix"];
            var head = Read(_s, t.offset, 8);
            int strikes = (int)U32(head, 4);
            var offsets = Read(_s, t.offset + 8, strikes * 4);
            int bestPpem = 0;
            for (int i = 0; i < strikes; i++)
            {
                long so = t.offset + U32(offsets, i * 4);
                int ppem = U16(Read(_s, so, 4), 0);
                if (ppem > bestPpem && ppem <= 256)
                {
                    bestPpem = ppem;
                    _sbixStrike = so;
                }
            }
            _sbixPpem = Math.Max(1, bestPpem);
        }

        void ParseCblc()
        {
            var cblc = Table("CBLC");
            long cbdt = _tables["CBDT"].offset;
            int numSizes = (int)U32(cblc, 4);
            int best = -1, bestPpem = 0;
            for (int i = 0; i < numSizes; i++)
            {
                int ppem = cblc[8 + i * 48 + 45];
                if (ppem > bestPpem) { bestPpem = ppem; best = i; }
            }
            if (best < 0) return;
            _cbdtPpem = Math.Max(1, bestPpem);
            int rec = 8 + best * 48;
            int arrayOffset = (int)U32(cblc, rec), numTables = (int)U32(cblc, rec + 8);
            _cbdt = new Dictionary<int, (int, long, int)>();
            for (int k = 0; k < numTables; k++)
            {
                int e = arrayOffset + k * 8;
                int first = U16(cblc, e), last = U16(cblc, e + 2);
                int sub = arrayOffset + (int)U32(cblc, e + 4);
                int indexFormat = U16(cblc, sub), imageFormat = U16(cblc, sub + 2);
                long imageData = cbdt + U32(cblc, sub + 4);
                if (indexFormat == 1)
                {
                    for (int g = first; g <= last; g++)
                    {
                        long o0 = U32(cblc, sub + 8 + (g - first) * 4), o1 = U32(cblc, sub + 12 + (g - first) * 4);
                        if (o1 > o0) _cbdt[g] = (imageFormat, imageData + o0, (int)(o1 - o0));
                    }
                }
                else if (indexFormat == 3)
                {
                    for (int g = first; g <= last; g++)
                    {
                        long o0 = U16(cblc, sub + 8 + (g - first) * 2), o1 = U16(cblc, sub + 10 + (g - first) * 2);
                        if (o1 > o0) _cbdt[g] = (imageFormat, imageData + o0, (int)(o1 - o0));
                    }
                }
                else if (indexFormat == 4)
                {
                    int numGlyphs = (int)U32(cblc, sub + 8);
                    for (int j = 0; j < numGlyphs; j++)
                    {
                        int p = sub + 12 + j * 4;
                        int g = U16(cblc, p);
                        long o0 = U16(cblc, p + 2), o1 = U16(cblc, p + 6);
                        if (o1 > o0) _cbdt[g] = (imageFormat, imageData + o0, (int)(o1 - o0));
                    }
                }
            }
        }

        /// <summary>Every ligature substitution (lookup type 4, also inside extension lookups) of the font.</summary>
        void ParseGsub(byte[] t)
        {
            int lookupList = U16(t, 8);
            int count = U16(t, lookupList);
            _ligatures = new Dictionary<int, List<(int[], int)>>();
            for (int i = 0; i < count; i++)
            {
                int lookup = lookupList + U16(t, lookupList + 2 + i * 2);
                int type = U16(t, lookup), subCount = U16(t, lookup + 4);
                for (int s = 0; s < subCount; s++)
                {
                    int sub = lookup + U16(t, lookup + 6 + s * 2);
                    int subType = type;
                    if (type == 7)
                    {
                        subType = U16(t, sub + 2);
                        sub += (int)U32(t, sub + 4);
                    }
                    if (subType == 4) ParseLigatureSubst(t, sub);
                }
            }
        }

        void ParseLigatureSubst(byte[] t, int sub)
        {
            var coverage = Coverage(t, sub + U16(t, sub + 2));
            int setCount = U16(t, sub + 4);
            for (int i = 0; i < setCount && i < coverage.Count; i++)
            {
                int set = sub + U16(t, sub + 6 + i * 2);
                int ligCount = U16(t, set);
                for (int l = 0; l < ligCount; l++)
                {
                    int lig = set + U16(t, set + 2 + l * 2);
                    int ligGlyph = U16(t, lig), compCount = U16(t, lig + 2);
                    var rest = new int[Math.Max(0, compCount - 1)];
                    for (int c = 0; c < rest.Length; c++) rest[c] = U16(t, lig + 4 + c * 2);
                    if (!_ligatures.TryGetValue(coverage[i], out var list)) _ligatures[coverage[i]] = list = new List<(int[], int)>();
                    list.Add((rest, ligGlyph));
                }
            }
        }

        static List<int> Coverage(byte[] t, int off)
        {
            var list = new List<int>();
            int format = U16(t, off);
            if (format == 1)
            {
                int n = U16(t, off + 2);
                for (int i = 0; i < n; i++) list.Add(U16(t, off + 4 + i * 2));
            }
            else if (format == 2)
            {
                int n = U16(t, off + 2);
                for (int i = 0; i < n; i++)
                {
                    int start = U16(t, off + 4 + i * 6), end = U16(t, off + 6 + i * 6);
                    for (int g = start; g <= end; g++) list.Add(g);
                }
            }
            return list;
        }

        static byte[] Read(Stream s, long offset, int length)
        {
            var b = new byte[Math.Max(0, length)];
            s.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < length)
            {
                int n = s.Read(b, read, length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return b;
        }

        static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
        static uint U32(byte[] d, int o) => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
    }
}
