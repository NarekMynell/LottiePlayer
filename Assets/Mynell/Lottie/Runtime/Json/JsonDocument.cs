using System;
using System.Collections.Generic;
using System.Text;

namespace Mynell.Lottie.Json
{
    public enum JsonType : byte { Missing, Null, False, True, Number, String, Array, Object }

    /// <summary>
    /// Minimal, fast, allocation-light JSON DOM. The whole document is stored in flat arrays;
    /// <see cref="JsonValue"/> is a lightweight handle into it. Strings are decoded lazily.
    /// </summary>
    public sealed class JsonDocument
    {
        internal JsonType[] Types;
        internal int[] A;      // container: first child node | string: byte offset
        internal int[] B;      // container: child count      | string: byte length (bit 31 = has escapes)
        internal int[] Next;   // next sibling (-1 = none)
        internal int[] KeyOff; // member key byte offset (-1 when not an object member)
        internal int[] KeyLen;
        internal double[] Num;
        internal byte[] Src;
        internal int Count;

        public JsonValue Root => Count > 0 ? new JsonValue(this, 0) : default;

        public static JsonDocument Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

        public static JsonDocument Parse(byte[] utf8)
        {
            var doc = new JsonDocument();
            var parser = new Parser(doc, utf8);
            parser.ParseRoot();
            return doc;
        }

        int NewNode(JsonType type)
        {
            if (Count == Types.Length) Grow();
            int i = Count++;
            Types[i] = type;
            A[i] = 0;
            B[i] = 0;
            Next[i] = -1;
            KeyOff[i] = -1;
            KeyLen[i] = 0;
            Num[i] = 0;
            return i;
        }

        void Allocate(int capacity)
        {
            Types = new JsonType[capacity];
            A = new int[capacity];
            B = new int[capacity];
            Next = new int[capacity];
            KeyOff = new int[capacity];
            KeyLen = new int[capacity];
            Num = new double[capacity];
        }

        void Grow()
        {
            int n = Math.Max(64, Types.Length * 2);
            Array.Resize(ref Types, n);
            Array.Resize(ref A, n);
            Array.Resize(ref B, n);
            Array.Resize(ref Next, n);
            Array.Resize(ref KeyOff, n);
            Array.Resize(ref KeyLen, n);
            Array.Resize(ref Num, n);
        }

        internal string DecodeString(int off, int lenFlags)
        {
            int len = lenFlags & 0x7FFFFFFF;
            if ((lenFlags & unchecked((int)0x80000000)) == 0)
                return Encoding.UTF8.GetString(Src, off, len);
            var sb = new StringBuilder(len);
            var bytes = new List<byte>(len);
            int end = off + len;
            int i = off;
            while (i < end)
            {
                byte c = Src[i];
                if (c != (byte)'\\') { bytes.Add(c); i++; continue; }
                i++;
                if (i >= end) break;
                byte e = Src[i++];
                switch (e)
                {
                    case (byte)'"': bytes.Add((byte)'"'); break;
                    case (byte)'\\': bytes.Add((byte)'\\'); break;
                    case (byte)'/': bytes.Add((byte)'/'); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'u':
                    {
                        int cp = ReadHex4(i); i += 4;
                        if (cp >= 0xD800 && cp <= 0xDBFF && i + 6 <= end && Src[i] == (byte)'\\' && Src[i + 1] == (byte)'u')
                        {
                            int lo = ReadHex4(i + 2);
                            if (lo >= 0xDC00 && lo <= 0xDFFF)
                            {
                                cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                                i += 6;
                            }
                        }
                        AppendUtf8(bytes, cp);
                        break;
                    }
                    default: bytes.Add(e); break;
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        int ReadHex4(int i)
        {
            int v = 0;
            for (int k = 0; k < 4 && i + k < Src.Length; k++)
            {
                int c = Src[i + k];
                int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;
                v = (v << 4) | d;
            }
            return v;
        }

        static void AppendUtf8(List<byte> b, int cp)
        {
            if (cp < 0x80) b.Add((byte)cp);
            else if (cp < 0x800) { b.Add((byte)(0xC0 | (cp >> 6))); b.Add((byte)(0x80 | (cp & 0x3F))); }
            else if (cp < 0x10000) { b.Add((byte)(0xE0 | (cp >> 12))); b.Add((byte)(0x80 | ((cp >> 6) & 0x3F))); b.Add((byte)(0x80 | (cp & 0x3F))); }
            else { b.Add((byte)(0xF0 | (cp >> 18))); b.Add((byte)(0x80 | ((cp >> 12) & 0x3F))); b.Add((byte)(0x80 | ((cp >> 6) & 0x3F))); b.Add((byte)(0x80 | (cp & 0x3F))); }
        }

        internal bool KeyEquals(int node, string key)
        {
            int off = KeyOff[node];
            if (off < 0) return false;
            int len = KeyLen[node];
            if (len != key.Length) return false;
            var s = Src;
            for (int i = 0; i < len; i++)
                if (s[off + i] != key[i]) return false;
            return true;
        }

        struct Parser
        {
            readonly JsonDocument _doc;
            readonly byte[] _s;
            int _p;

            public Parser(JsonDocument doc, byte[] src)
            {
                _doc = doc;
                _s = src;
                _p = 0;
                doc.Src = src;
                doc.Allocate(Math.Max(64, src.Length / 5));
            }

            public void ParseRoot()
            {
                // Skip UTF-8 BOM.
                if (_s.Length >= 3 && _s[0] == 0xEF && _s[1] == 0xBB && _s[2] == 0xBF) _p = 3;
                SkipWs();
                ParseValue();
            }

            void SkipWs()
            {
                var s = _s;
                int p = _p;
                while (p < s.Length)
                {
                    byte c = s[p];
                    if (c == ' ' || c == '\n' || c == '\r' || c == '\t') p++;
                    else break;
                }
                _p = p;
            }

            Exception Error(string msg) => new FormatException($"JSON parse error at byte {_p}: {msg}");

            int ParseValue()
            {
                if (_p >= _s.Length) throw Error("unexpected end of input");
                byte c = _s[_p];
                switch (c)
                {
                    case (byte)'{': return ParseObject();
                    case (byte)'[': return ParseArray();
                    case (byte)'"':
                    {
                        int n = _doc.NewNode(JsonType.String);
                        ParseStringBody(out int off, out int len);
                        _doc.A[n] = off;
                        _doc.B[n] = len;
                        return n;
                    }
                    case (byte)'t': Expect("true"); return _doc.NewNode(JsonType.True);
                    case (byte)'f': Expect("false"); return _doc.NewNode(JsonType.False);
                    case (byte)'n': Expect("null"); return _doc.NewNode(JsonType.Null);
                    default:
                    {
                        int n = _doc.NewNode(JsonType.Number);
                        _doc.Num[n] = ParseNumber();
                        return n;
                    }
                }
            }

            void Expect(string lit)
            {
                if (_p + lit.Length > _s.Length) throw Error("unexpected end of input");
                for (int i = 0; i < lit.Length; i++)
                    if (_s[_p + i] != lit[i]) throw Error("invalid literal");
                _p += lit.Length;
            }

            int ParseObject()
            {
                int node = _doc.NewNode(JsonType.Object);
                _p++; // {
                SkipWs();
                int prev = -1;
                int count = 0;
                if (_p < _s.Length && _s[_p] == '}') { _p++; return node; }
                while (true)
                {
                    SkipWs();
                    if (_p >= _s.Length || _s[_p] != '"') throw Error("expected object key");
                    ParseStringBody(out int koff, out int klen);
                    SkipWs();
                    if (_p >= _s.Length || _s[_p] != ':') throw Error("expected ':'");
                    _p++;
                    SkipWs();
                    int child = ParseValue();
                    _doc.KeyOff[child] = koff;
                    _doc.KeyLen[child] = klen & 0x7FFFFFFF;
                    if (prev < 0) _doc.A[node] = child; else _doc.Next[prev] = child;
                    prev = child;
                    count++;
                    SkipWs();
                    if (_p >= _s.Length) throw Error("unterminated object");
                    byte c = _s[_p++];
                    if (c == ',') continue;
                    if (c == '}') break;
                    throw Error("expected ',' or '}'");
                }
                _doc.B[node] = count;
                return node;
            }

            int ParseArray()
            {
                int node = _doc.NewNode(JsonType.Array);
                _p++; // [
                SkipWs();
                int prev = -1;
                int count = 0;
                if (_p < _s.Length && _s[_p] == ']') { _p++; return node; }
                while (true)
                {
                    SkipWs();
                    int child = ParseValue();
                    if (prev < 0) _doc.A[node] = child; else _doc.Next[prev] = child;
                    prev = child;
                    count++;
                    SkipWs();
                    if (_p >= _s.Length) throw Error("unterminated array");
                    byte c = _s[_p++];
                    if (c == ',') continue;
                    if (c == ']') break;
                    throw Error("expected ',' or ']'");
                }
                _doc.B[node] = count;
                return node;
            }

            void ParseStringBody(out int off, out int lenFlags)
            {
                _p++; // opening quote
                int start = _p;
                bool esc = false;
                var s = _s;
                int p = _p;
                while (p < s.Length)
                {
                    byte c = s[p];
                    if (c == '"') break;
                    if (c == '\\') { esc = true; p += 2; continue; }
                    p++;
                }
                if (p >= s.Length) { _p = p; throw Error("unterminated string"); }
                off = start;
                lenFlags = (p - start) | (esc ? unchecked((int)0x80000000) : 0);
                _p = p + 1;
            }

            static readonly double[] s_Pow10 =
            {
                1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
                1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
            };

            double ParseNumber()
            {
                var s = _s;
                int p = _p;
                bool neg = false;
                if (p < s.Length && (s[p] == '-' || s[p] == '+')) { neg = s[p] == '-'; p++; }
                ulong mant = 0;
                int digits = 0;
                int exp10 = 0;
                bool any = false;
                while (p < s.Length)
                {
                    int d = s[p] - '0';
                    if ((uint)d > 9) break;
                    any = true;
                    if (digits < 19) { mant = mant * 10 + (ulong)d; if (mant != 0) digits++; }
                    else exp10++;
                    p++;
                }
                if (p < s.Length && s[p] == '.')
                {
                    p++;
                    while (p < s.Length)
                    {
                        int d = s[p] - '0';
                        if ((uint)d > 9) break;
                        any = true;
                        if (digits < 19) { mant = mant * 10 + (ulong)d; if (mant != 0) digits++; exp10--; }
                        p++;
                    }
                }
                if (!any) { _p = p; throw Error("invalid number"); }
                if (p < s.Length && (s[p] == 'e' || s[p] == 'E'))
                {
                    p++;
                    bool eneg = false;
                    if (p < s.Length && (s[p] == '-' || s[p] == '+')) { eneg = s[p] == '-'; p++; }
                    int e = 0;
                    while (p < s.Length)
                    {
                        int d = s[p] - '0';
                        if ((uint)d > 9) break;
                        if (e < 10000) e = e * 10 + d;
                        p++;
                    }
                    exp10 += eneg ? -e : e;
                }
                _p = p;
                double v = mant;
                if (exp10 != 0)
                {
                    if (exp10 > 0) v = exp10 <= 22 ? v * s_Pow10[exp10] : v * Math.Pow(10, exp10);
                    else v = -exp10 <= 22 ? v / s_Pow10[-exp10] : v * Math.Pow(10, exp10);
                }
                return neg ? -v : v;
            }
        }
    }

    /// <summary>Handle to a node of a <see cref="JsonDocument"/>.</summary>
    public readonly struct JsonValue
    {
        readonly JsonDocument _d;
        readonly int _i;

        internal JsonValue(JsonDocument d, int i) { _d = d; _i = i; }

        public JsonType Type => _d == null ? JsonType.Missing : _d.Types[_i];
        public bool Exists => _d != null && _d.Types[_i] != JsonType.Null;
        public bool IsMissing => _d == null;
        /// <summary>Stable identity of this node inside its document (-1 when missing).</summary>
        public int NodeId => _d == null ? -1 : _i;
        public bool IsNumber => Type == JsonType.Number;
        public bool IsString => Type == JsonType.String;
        public bool IsArray => Type == JsonType.Array;
        public bool IsObject => Type == JsonType.Object;
        public bool IsBool => Type == JsonType.True || Type == JsonType.False;

        public int Count => (_d != null && (_d.Types[_i] == JsonType.Array || _d.Types[_i] == JsonType.Object)) ? _d.B[_i] : 0;

        public double AsDouble(double def = 0)
        {
            if (_d == null) return def;
            switch (_d.Types[_i])
            {
                case JsonType.Number: return _d.Num[_i];
                case JsonType.True: return 1;
                case JsonType.False: return 0;
                case JsonType.String:
                    return double.TryParse(AsString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : def;
                case JsonType.Array: return Count > 0 ? this[0].AsDouble(def) : def;
                default: return def;
            }
        }

        public float AsFloat(float def = 0) => (float)AsDouble(def);
        public int AsInt(int def = 0) => _d == null || !(IsNumber || IsBool || IsString || IsArray) ? def : (int)Math.Round(AsDouble(def));

        public bool AsBool(bool def = false)
        {
            if (_d == null) return def;
            switch (_d.Types[_i])
            {
                case JsonType.True: return true;
                case JsonType.False: return false;
                case JsonType.Number: return _d.Num[_i] != 0;
                case JsonType.Null: return false;
                default: return def;
            }
        }

        public string AsString(string def = null)
        {
            if (_d == null) return def;
            if (_d.Types[_i] == JsonType.String) return _d.DecodeString(_d.A[_i], _d.B[_i]);
            if (_d.Types[_i] == JsonType.Number) return _d.Num[_i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return def;
        }

        public string Key => _d == null || _d.KeyOff[_i] < 0 ? null : Encoding.UTF8.GetString(_d.Src, _d.KeyOff[_i], _d.KeyLen[_i]);

        public JsonValue this[string key]
        {
            get
            {
                if (_d == null || _d.Types[_i] != JsonType.Object) return default;
                int c = _d.A[_i];
                int n = _d.B[_i];
                for (int k = 0; k < n; k++)
                {
                    if (_d.KeyEquals(c, key)) return new JsonValue(_d, c);
                    c = _d.Next[c];
                }
                return default;
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (_d == null) return default;
                var t = _d.Types[_i];
                if (t != JsonType.Array && t != JsonType.Object) return default;
                int n = _d.B[_i];
                if ((uint)index >= (uint)n) return default;
                int c = _d.A[_i];
                for (int k = 0; k < index; k++) c = _d.Next[c];
                return new JsonValue(_d, c);
            }
        }

        public bool Has(string key) => !this[key].IsMissing;

        public Enumerator GetEnumerator() => new Enumerator(_d, _i);

        /// <summary>Reads a numeric array (or a single number) into a new float array.</summary>
        public float[] ToFloatArray()
        {
            if (_d == null) return Array.Empty<float>();
            if (IsNumber) return new[] { (float)_d.Num[_i] };
            if (!IsArray) return Array.Empty<float>();
            var r = new float[Count];
            int k = 0;
            foreach (var v in this) r[k++] = v.AsFloat();
            return r;
        }

        public struct Enumerator
        {
            readonly JsonDocument _d;
            int _next;
            int _cur;
            int _remaining;

            internal Enumerator(JsonDocument d, int i)
            {
                _d = d;
                _cur = -1;
                if (d != null && (d.Types[i] == JsonType.Array || d.Types[i] == JsonType.Object))
                {
                    _remaining = d.B[i];
                    _next = _remaining > 0 ? d.A[i] : -1;
                }
                else
                {
                    _remaining = 0;
                    _next = -1;
                }
            }

            public JsonValue Current => new JsonValue(_d, _cur);

            public bool MoveNext()
            {
                if (_remaining <= 0) return false;
                _cur = _next;
                _next = _d.Next[_cur];
                _remaining--;
                return true;
            }
        }

        public override string ToString()
        {
            switch (Type)
            {
                case JsonType.Missing: return "<missing>";
                case JsonType.Null: return "null";
                case JsonType.True: return "true";
                case JsonType.False: return "false";
                case JsonType.Number: return AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JsonType.String: return "\"" + AsString() + "\"";
                case JsonType.Array: return $"[array:{Count}]";
                default: return $"{{object:{Count}}}";
            }
        }
    }
}
