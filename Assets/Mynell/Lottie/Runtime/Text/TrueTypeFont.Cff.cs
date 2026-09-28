using System;
using System.Collections.Generic;

namespace Mynell.Lottie.Text
{
    /// <summary>CFF (Type 2 charstring) outlines for OpenType fonts with PostScript glyphs.</summary>
    public sealed partial class TrueTypeFont
    {
        int _cffBase;
        List<int> _charStrings;        // (offset, length) pairs flattened: start offsets, count + 1 entries
        List<int> _globalSubrs;
        readonly List<List<int>> _localSubrs = new List<List<int>>();
        int _fdSelect = -1;
        int _fdSelectFormat;

        /// <summary>INDEX as a list of absolute offsets (count + 1 entries); returns the offset after the INDEX.</summary>
        List<int> ReadIndex(int o, out int next)
        {
            int count = U16(o);
            var list = new List<int>(count + 1);
            if (count == 0)
            {
                next = o + 2;
                return list;
            }
            int offSize = _d[o + 2];
            int offs = o + 3;
            int dataStart = offs + (count + 1) * offSize - 1;
            for (int i = 0; i <= count; i++)
            {
                int v = 0;
                for (int k = 0; k < offSize; k++) v = v << 8 | _d[offs + i * offSize + k];
                list.Add(dataStart + v);
            }
            next = list[count];
            return list;
        }

        Dictionary<int, List<double>> ReadDict(int start, int end)
        {
            var dict = new Dictionary<int, List<double>>();
            var operands = new List<double>();
            int p = start;
            while (p < end)
            {
                int b0 = _d[p];
                if (b0 <= 21)
                {
                    int op = b0;
                    p++;
                    if (b0 == 12) op = 1200 + _d[p++];
                    dict[op] = new List<double>(operands);
                    operands.Clear();
                }
                else if (b0 == 28) { operands.Add(S16(p + 1)); p += 3; }
                else if (b0 == 29) { operands.Add((int)U32(p + 1)); p += 5; }
                else if (b0 == 30)
                {
                    // real number (nibbles)
                    p++;
                    var sb = new System.Text.StringBuilder();
                    bool done = false;
                    while (!done && p < end)
                    {
                        int b = _d[p++];
                        for (int s = 4; s >= 0 && !done; s -= 4)
                        {
                            int nib = (b >> s) & 0xF;
                            switch (nib)
                            {
                                case 0xA: sb.Append('.'); break;
                                case 0xB: sb.Append('E'); break;
                                case 0xC: sb.Append("E-"); break;
                                case 0xE: sb.Append('-'); break;
                                case 0xF: done = true; break;
                                case 0xD: break;
                                default: sb.Append((char)('0' + nib)); break;
                            }
                        }
                    }
                    double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double r);
                    operands.Add(r);
                }
                else if (b0 >= 32 && b0 <= 246) { operands.Add(b0 - 139); p++; }
                else if (b0 >= 247 && b0 <= 250) { operands.Add((b0 - 247) * 256 + _d[p + 1] + 108); p += 2; }
                else if (b0 >= 251 && b0 <= 254) { operands.Add(-(b0 - 251) * 256 - _d[p + 1] - 108); p += 2; }
                else p++;
            }
            return dict;
        }

        List<int> ReadLocalSubrs(int privateOffset, int privateSize)
        {
            var priv = ReadDict(privateOffset, privateOffset + privateSize);
            if (!priv.TryGetValue(19, out var subrs) || subrs.Count == 0) return new List<int>();
            return ReadIndex(privateOffset + (int)subrs[0], out _);
        }

        void InitCff(int cff)
        {
            _cffBase = cff;
            int hdrSize = _d[cff + 2];
            ReadIndex(cff + hdrSize, out int afterName);
            var topDicts = ReadIndex(afterName, out int afterTop);
            ReadIndex(afterTop, out int afterStrings);
            _globalSubrs = ReadIndex(afterStrings, out _);
            var top = ReadDict(topDicts[0], topDicts[1]);
            if (top.TryGetValue(17, out var cs)) _charStrings = ReadIndex(cff + (int)cs[0], out _);
            if (top.TryGetValue(1236, out var fdArray))
            {
                var fds = ReadIndex(cff + (int)fdArray[0], out _);
                for (int i = 0; i + 1 < fds.Count; i++)
                {
                    var fd = ReadDict(fds[i], fds[i + 1]);
                    if (fd.TryGetValue(18, out var pr) && pr.Count >= 2) _localSubrs.Add(ReadLocalSubrs(cff + (int)pr[1], (int)pr[0]));
                    else _localSubrs.Add(new List<int>());
                }
                if (top.TryGetValue(1237, out var sel))
                {
                    _fdSelect = cff + (int)sel[0];
                    _fdSelectFormat = _d[_fdSelect];
                }
            }
            else if (top.TryGetValue(18, out var priv) && priv.Count >= 2)
            {
                _localSubrs.Add(ReadLocalSubrs(cff + (int)priv[1], (int)priv[0]));
            }
        }

        int FdFor(int glyph)
        {
            if (_fdSelect < 0) return 0;
            if (_fdSelectFormat == 0) return _d[_fdSelect + 1 + glyph];
            if (_fdSelectFormat == 3)
            {
                int nRanges = U16(_fdSelect + 1);
                for (int i = 0; i < nRanges; i++)
                {
                    int r = _fdSelect + 3 + i * 3;
                    int first = U16(r), nextFirst = U16(r + 3);
                    if (glyph >= first && glyph < nextFirst) return _d[r + 2];
                }
            }
            return 0;
        }

        static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        sealed class CffState
        {
            public readonly List<double> Stack = new List<double>(48);
            public double X, Y;
            public int Stems;
            public bool HaveWidth;
            public bool Open;
            public GlyphOutlineContour Contour;
            public List<GlyphOutlineContour> Result;
            public float Scale;
            public int Depth;
            public bool Done;
        }

        void CffOutline(int glyph, List<GlyphOutlineContour> result, float scale)
        {
            if (_charStrings == null || glyph + 1 >= _charStrings.Count) return;
            var st = new CffState { Result = result, Scale = scale };
            var local = _localSubrs.Count > 0 ? _localSubrs[Math.Min(FdFor(glyph), _localSubrs.Count - 1)] : new List<int>();
            RunCharString(_charStrings[glyph], _charStrings[glyph + 1], st, local);
            ClosePath(st);
        }

        void MoveTo(CffState st, double x, double y)
        {
            ClosePath(st);
            st.X = x;
            st.Y = y;
            st.Contour = new GlyphOutlineContour();
            AddVertex(st, x, y);
            st.Open = true;
        }

        void AddVertex(CffState st, double x, double y)
        {
            float fx = (float)(x * st.Scale), fy = (float)(-y * st.Scale);
            st.Contour.Vertices.Add(new GlyphOutlineVertex { X = fx, Y = fy, InX = fx, InY = fy, OutX = fx, OutY = fy });
        }

        void LineTo(CffState st, double x, double y)
        {
            if (!st.Open) MoveTo(st, st.X, st.Y);
            st.X = x;
            st.Y = y;
            AddVertex(st, x, y);
        }

        void CurveTo(CffState st, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            if (!st.Open) MoveTo(st, st.X, st.Y);
            var list = st.Contour.Vertices;
            var last = list[list.Count - 1];
            last.OutX = (float)(x1 * st.Scale);
            last.OutY = (float)(-y1 * st.Scale);
            list[list.Count - 1] = last;
            float fx = (float)(x3 * st.Scale), fy = (float)(-y3 * st.Scale);
            list.Add(new GlyphOutlineVertex { X = fx, Y = fy, InX = (float)(x2 * st.Scale), InY = (float)(-y2 * st.Scale), OutX = fx, OutY = fy });
            st.X = x3;
            st.Y = y3;
        }

        static void ClosePath(CffState st)
        {
            if (!st.Open || st.Contour == null) return;
            var list = st.Contour.Vertices;
            // The closing point usually duplicates the start: merge its in-tangent into vertex 0.
            if (list.Count > 1)
            {
                var first = list[0];
                var last = list[list.Count - 1];
                if (Math.Abs(first.X - last.X) < 1e-4f && Math.Abs(first.Y - last.Y) < 1e-4f)
                {
                    first.InX = last.InX;
                    first.InY = last.InY;
                    list[0] = first;
                    list.RemoveAt(list.Count - 1);
                }
            }
            if (list.Count > 0) st.Result.Add(st.Contour);
            st.Open = false;
            st.Contour = null;
        }

        void RunCharString(int start, int end, CffState st, List<int> local)
        {
            if (++st.Depth > 10) return;
            var s = st.Stack;
            int p = start;
            while (p < end && !st.Done)
            {
                int b0 = _d[p++];
                if (b0 >= 32 || b0 == 28)
                {
                    if (b0 == 28) { s.Add(S16(p)); p += 2; }
                    else if (b0 <= 246) s.Add(b0 - 139);
                    else if (b0 <= 250) { s.Add((b0 - 247) * 256 + _d[p] + 108); p++; }
                    else if (b0 <= 254) { s.Add(-(b0 - 251) * 256 - _d[p] - 108); p++; }
                    else { s.Add((int)U32(p) / 65536.0); p += 4; }
                    continue;
                }
                switch (b0)
                {
                    case 1: case 3: case 18: case 23: // hstem vstem hstemhm vstemhm
                        TakeWidth(st, s.Count % 2 != 0);
                        st.Stems += s.Count / 2;
                        s.Clear();
                        break;
                    case 19: case 20: // hintmask cntrmask
                        TakeWidth(st, s.Count % 2 != 0);
                        st.Stems += s.Count / 2;
                        s.Clear();
                        p += (st.Stems + 7) >> 3;
                        break;
                    case 21: // rmoveto
                        TakeWidth(st, s.Count > 2);
                        MoveTo(st, st.X + s[s.Count - 2], st.Y + s[s.Count - 1]);
                        s.Clear();
                        break;
                    case 22: // hmoveto
                        TakeWidth(st, s.Count > 1);
                        MoveTo(st, st.X + s[s.Count - 1], st.Y);
                        s.Clear();
                        break;
                    case 4: // vmoveto
                        TakeWidth(st, s.Count > 1);
                        MoveTo(st, st.X, st.Y + s[s.Count - 1]);
                        s.Clear();
                        break;
                    case 5: // rlineto
                        for (int i = 0; i + 1 < s.Count; i += 2) LineTo(st, st.X + s[i], st.Y + s[i + 1]);
                        s.Clear();
                        break;
                    case 6: // hlineto
                    case 7: // vlineto
                    {
                        bool horizontal = b0 == 6;
                        for (int i = 0; i < s.Count; i++)
                        {
                            if (horizontal) LineTo(st, st.X + s[i], st.Y);
                            else LineTo(st, st.X, st.Y + s[i]);
                            horizontal = !horizontal;
                        }
                        s.Clear();
                        break;
                    }
                    case 8: // rrcurveto
                        for (int i = 0; i + 5 < s.Count; i += 6)
                        {
                            double x1 = st.X + s[i], y1 = st.Y + s[i + 1];
                            double x2 = x1 + s[i + 2], y2 = y1 + s[i + 3];
                            CurveTo(st, x1, y1, x2, y2, x2 + s[i + 4], y2 + s[i + 5]);
                        }
                        s.Clear();
                        break;
                    case 24: // rcurveline
                    {
                        int i = 0;
                        for (; i + 5 < s.Count - 2; i += 6)
                        {
                            double x1 = st.X + s[i], y1 = st.Y + s[i + 1];
                            double x2 = x1 + s[i + 2], y2 = y1 + s[i + 3];
                            CurveTo(st, x1, y1, x2, y2, x2 + s[i + 4], y2 + s[i + 5]);
                        }
                        if (i + 1 < s.Count) LineTo(st, st.X + s[i], st.Y + s[i + 1]);
                        s.Clear();
                        break;
                    }
                    case 25: // rlinecurve
                    {
                        int i = 0;
                        for (; i + 1 < s.Count - 6; i += 2) LineTo(st, st.X + s[i], st.Y + s[i + 1]);
                        if (i + 5 < s.Count)
                        {
                            double x1 = st.X + s[i], y1 = st.Y + s[i + 1];
                            double x2 = x1 + s[i + 2], y2 = y1 + s[i + 3];
                            CurveTo(st, x1, y1, x2, y2, x2 + s[i + 4], y2 + s[i + 5]);
                        }
                        s.Clear();
                        break;
                    }
                    case 26: // vvcurveto
                    {
                        int i = 0;
                        double dx1 = 0;
                        if (s.Count % 2 == 1) { dx1 = s[0]; i = 1; }
                        for (; i + 3 < s.Count; i += 4)
                        {
                            double x1 = st.X + dx1, y1 = st.Y + s[i];
                            double x2 = x1 + s[i + 1], y2 = y1 + s[i + 2];
                            CurveTo(st, x1, y1, x2, y2, x2, y2 + s[i + 3]);
                            dx1 = 0;
                        }
                        s.Clear();
                        break;
                    }
                    case 27: // hhcurveto
                    {
                        int i = 0;
                        double dy1 = 0;
                        if (s.Count % 2 == 1) { dy1 = s[0]; i = 1; }
                        for (; i + 3 < s.Count; i += 4)
                        {
                            double x1 = st.X + s[i], y1 = st.Y + dy1;
                            double x2 = x1 + s[i + 1], y2 = y1 + s[i + 2];
                            CurveTo(st, x1, y1, x2, y2, x2 + s[i + 3], y2);
                            dy1 = 0;
                        }
                        s.Clear();
                        break;
                    }
                    case 30: // vhcurveto
                    case 31: // hvcurveto
                    {
                        bool horizontal = b0 == 31;
                        int i = 0;
                        while (i + 3 < s.Count)
                        {
                            bool lastPair = s.Count - i == 5;
                            if (horizontal)
                            {
                                double x1 = st.X + s[i], y1 = st.Y;
                                double x2 = x1 + s[i + 1], y2 = y1 + s[i + 2];
                                double y3 = y2 + s[i + 3];
                                double x3 = x2 + (lastPair ? s[i + 4] : 0);
                                CurveTo(st, x1, y1, x2, y2, x3, y3);
                            }
                            else
                            {
                                double x1 = st.X, y1 = st.Y + s[i];
                                double x2 = x1 + s[i + 1], y2 = y1 + s[i + 2];
                                double x3 = x2 + s[i + 3];
                                double y3 = y2 + (lastPair ? s[i + 4] : 0);
                                CurveTo(st, x1, y1, x2, y2, x3, y3);
                            }
                            i += lastPair ? 5 : 4;
                            horizontal = !horizontal;
                        }
                        s.Clear();
                        break;
                    }
                    case 10: // callsubr
                    case 29: // callgsubr
                    {
                        var subrs = b0 == 10 ? local : _globalSubrs;
                        if (s.Count == 0 || subrs == null || subrs.Count < 2) { s.Clear(); break; }
                        int idx = (int)s[s.Count - 1] + Bias(subrs.Count - 1);
                        s.RemoveAt(s.Count - 1);
                        if (idx >= 0 && idx + 1 < subrs.Count) RunCharString(subrs[idx], subrs[idx + 1], st, local);
                        break;
                    }
                    case 11: // return
                        st.Depth--;
                        return;
                    case 14: // endchar
                        TakeWidth(st, s.Count == 1 || s.Count == 5);
                        ClosePath(st);
                        st.Done = true;
                        s.Clear();
                        break;
                    case 12:
                    {
                        int op = _d[p++];
                        switch (op)
                        {
                            case 35: // flex
                            {
                                if (s.Count < 12) break;
                                double x1 = st.X + s[0], y1 = st.Y + s[1];
                                double x2 = x1 + s[2], y2 = y1 + s[3];
                                double x3 = x2 + s[4], y3 = y2 + s[5];
                                CurveTo(st, x1, y1, x2, y2, x3, y3);
                                double x4 = x3 + s[6], y4 = y3 + s[7];
                                double x5 = x4 + s[8], y5 = y4 + s[9];
                                CurveTo(st, x4, y4, x5, y5, x5 + s[10], y5 + s[11]);
                                break;
                            }
                            case 34: // hflex
                            {
                                if (s.Count < 7) break;
                                double y0 = st.Y;
                                double x1 = st.X + s[0], y1 = st.Y;
                                double x2 = x1 + s[1], y2 = y1 + s[2];
                                double x3 = x2 + s[3], y3 = y2;
                                CurveTo(st, x1, y1, x2, y2, x3, y3);
                                double x4 = x3 + s[4], y4 = y2;
                                double x5 = x4 + s[5], y5 = y0;
                                CurveTo(st, x4, y4, x5, y5, x5 + s[6], y0);
                                break;
                            }
                            case 36: // hflex1
                            {
                                if (s.Count < 9) break;
                                double y0 = st.Y;
                                double x1 = st.X + s[0], y1 = st.Y + s[1];
                                double x2 = x1 + s[2], y2 = y1 + s[3];
                                double x3 = x2 + s[4], y3 = y2;
                                CurveTo(st, x1, y1, x2, y2, x3, y3);
                                double x4 = x3 + s[5], y4 = y2;
                                double x5 = x4 + s[6], y5 = y4 + s[7];
                                CurveTo(st, x4, y4, x5, y5, x5 + s[8], y0);
                                break;
                            }
                            case 37: // flex1
                            {
                                if (s.Count < 11) break;
                                double sx = st.X, sy = st.Y;
                                double x1 = st.X + s[0], y1 = st.Y + s[1];
                                double x2 = x1 + s[2], y2 = y1 + s[3];
                                double x3 = x2 + s[4], y3 = y2 + s[5];
                                CurveTo(st, x1, y1, x2, y2, x3, y3);
                                double x4 = x3 + s[6], y4 = y3 + s[7];
                                double x5 = x4 + s[8], y5 = y4 + s[9];
                                double dx = x5 - sx, dy = y5 - sy;
                                if (Math.Abs(dx) > Math.Abs(dy)) CurveTo(st, x4, y4, x5, y5, x5 + s[10], sy);
                                else CurveTo(st, x4, y4, x5, y5, sx, y5 + s[10]);
                                break;
                            }
                        }
                        s.Clear();
                        break;
                    }
                    default:
                        s.Clear();
                        break;
                }
            }
            st.Depth--;
        }

        static void TakeWidth(CffState st, bool hasWidth)
        {
            if (st.HaveWidth) return;
            st.HaveWidth = true;
            if (hasWidth && st.Stack.Count > 0) st.Stack.RemoveAt(0);
        }
    }
}
