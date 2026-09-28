using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mynell.Lottie.Expressions
{
    /// <summary>Standard library subset: Math, conversions, String / Array / Number methods.</summary>
    static class JsBuiltins
    {
        static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
        static double Num(JsInterpreter it, JsValue[] a, int i, double def = double.NaN) =>
            i < a.Length && !a[i].IsUndefined ? JsOps.ToNumber(it, a[i]) : def;

        // ------------------------------------------------------------------ deterministic random (xorshift128+)

        public sealed class Rng
        {
            ulong _s0 = 0x9E3779B97F4A7C15UL, _s1 = 0xBF58476D1CE4E5B9UL;

            public void Seed(double seed)
            {
                ulong x = (ulong)BitConverter.DoubleToInt64Bits(seed) ^ 0x94D049BB133111EBUL;
                _s0 = SplitMix(ref x);
                _s1 = SplitMix(ref x);
                if (_s0 == 0 && _s1 == 0) _s1 = 1;
            }

            static ulong SplitMix(ref ulong x)
            {
                ulong z = x += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            public double Next()
            {
                ulong s1 = _s0, s0 = _s1;
                _s0 = s0;
                s1 ^= s1 << 23;
                _s1 = s1 ^ s0 ^ (s1 >> 17) ^ (s0 >> 26);
                return ((_s1 + s0) >> 11) * (1.0 / (1UL << 53));
            }
        }

        // ------------------------------------------------------------------ install

        public static void Install(JsInterpreter it)
        {
            var math = new JsObject();
            math.Set("PI", Math.PI);
            math.Set("E", Math.E);
            math.Set("LN2", Math.Log(2));
            math.Set("LN10", Math.Log(10));
            math.Set("LOG2E", 1 / Math.Log(2));
            math.Set("LOG10E", 1 / Math.Log(10));
            math.Set("SQRT2", Math.Sqrt(2));
            math.Set("SQRT1_2", Math.Sqrt(0.5));
            void M1(string name, Func<double, double> f) =>
                math.Set(name, new JsNativeFunction(name, (i, t, a) => f(Num(i, a, 0))));
            M1("abs", Math.Abs);
            M1("acos", Math.Acos);
            M1("asin", Math.Asin);
            M1("atan", Math.Atan);
            M1("ceil", Math.Ceiling);
            M1("cos", Math.Cos);
            M1("exp", Math.Exp);
            M1("floor", Math.Floor);
            M1("log", Math.Log);
            M1("log10", Math.Log10);
            M1("log2", x => Math.Log(x, 2));
            M1("sin", Math.Sin);
            M1("sqrt", Math.Sqrt);
            M1("cbrt", x => Math.Sign(x) * Math.Pow(Math.Abs(x), 1.0 / 3.0));
            M1("tan", Math.Tan);
            M1("trunc", Math.Truncate);
            M1("sign", x => double.IsNaN(x) ? double.NaN : Math.Sign(x));
            M1("sinh", Math.Sinh);
            M1("cosh", Math.Cosh);
            M1("tanh", Math.Tanh);
            M1("round", x => Math.Floor(x + 0.5));
            M1("fround", x => (float)x);
            math.Set("atan2", new JsNativeFunction("atan2", (i, t, a) => Math.Atan2(Num(i, a, 0), Num(i, a, 1))));
            math.Set("pow", new JsNativeFunction("pow", (i, t, a) => Math.Pow(Num(i, a, 0), Num(i, a, 1))));
            math.Set("hypot", new JsNativeFunction("hypot", (i, t, a) =>
            {
                double s = 0;
                for (int k = 0; k < a.Length; k++) { double v = JsOps.ToNumber(i, a[k]); s += v * v; }
                return Math.Sqrt(s);
            }));
            math.Set("max", new JsNativeFunction("max", (i, t, a) =>
            {
                double m = double.NegativeInfinity;
                for (int k = 0; k < a.Length; k++) { double v = JsOps.ToNumber(i, a[k]); if (double.IsNaN(v)) return double.NaN; if (v > m) m = v; }
                return m;
            }));
            math.Set("min", new JsNativeFunction("min", (i, t, a) =>
            {
                double m = double.PositiveInfinity;
                for (int k = 0; k < a.Length; k++) { double v = JsOps.ToNumber(i, a[k]); if (double.IsNaN(v)) return double.NaN; if (v < m) m = v; }
                return m;
            }));
            math.Set("random", new JsNativeFunction("random", (i, t, a) => i.Random.Next()));
            it.DefineGlobal("Math", math);

            it.DefineFunction("parseInt", (i, t, a) =>
            {
                string s = JsOps.ToStr(i, Arg(a, 0)).Trim();
                int radix = (int)Num(i, a, 1, 10);
                if (radix == 0) radix = 10;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && (radix == 16 || a.Length < 2)) { s = s.Substring(2); radix = 16; }
                bool neg = false;
                if (s.StartsWith("-")) { neg = true; s = s.Substring(1); }
                else if (s.StartsWith("+")) s = s.Substring(1);
                double r = 0;
                int n = 0;
                foreach (char c in s)
                {
                    int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 99;
                    if (d >= radix) break;
                    r = r * radix + d;
                    n++;
                }
                if (n == 0) return double.NaN;
                return neg ? -r : r;
            });
            it.DefineFunction("parseFloat", (i, t, a) =>
            {
                string s = JsOps.ToStr(i, Arg(a, 0)).Trim();
                int end = 0;
                while (end < s.Length && ("+-.eE".IndexOf(s[end]) >= 0 || char.IsDigit(s[end]))) end++;
                while (end > 0 && !double.TryParse(s.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) end--;
                return end > 0 ? double.Parse(s.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture) : double.NaN;
            });
            it.DefineFunction("isNaN", (i, t, a) => JsValue.Bool(double.IsNaN(Num(i, a, 0))));
            it.DefineFunction("isFinite", (i, t, a) => { double d = Num(i, a, 0); return JsValue.Bool(!double.IsNaN(d) && !double.IsInfinity(d)); });
            it.DefineFunction("Number", (i, t, a) => a.Length == 0 ? 0 : JsOps.ToNumber(i, a[0]));
            it.DefineFunction("String", (i, t, a) => JsValue.String(a.Length == 0 ? "" : JsOps.ToStr(i, a[0])));
            it.DefineFunction("Boolean", (i, t, a) => JsValue.Bool(a.Length > 0 && JsOps.ToBool(i, a[0])));
            var array = new JsNativeFunction("Array", (i, t, a) =>
            {
                if (a.Length == 1 && a[0].IsNumber)
                {
                    var r = new JsArray((int)a[0].Num);
                    for (int k = 0; k < (int)a[0].Num; k++) r.Items.Add(JsValue.Undefined);
                    return r;
                }
                var arr = new JsArray(a.Length);
                arr.Items.AddRange(a);
                return arr;
            });
            array.Set("isArray", new JsNativeFunction("isArray", (i, t, a) => JsValue.Bool(JsOps.Prim(i, Arg(a, 0)).Obj is JsArray)));
            it.DefineGlobal("Array", array);
            var obj = new JsNativeFunction("Object", (i, t, a) => a.Length > 0 && a[0].IsObject ? a[0] : new JsObject());
            obj.Set("keys", new JsNativeFunction("keys", (i, t, a) =>
            {
                var r = new JsArray();
                if (Arg(a, 0).Obj != null) foreach (var k in a[0].Obj.Keys()) r.Items.Add(JsValue.String(k));
                return r;
            }));
            it.DefineGlobal("Object", obj);
            it.DefineGlobal("NaN", double.NaN);
            it.DefineGlobal("Infinity", double.PositiveInfinity);
            var console = new JsObject();
            console.Set("log", new JsNativeFunction("log", (i, t, a) => JsValue.Undefined));
            it.DefineGlobal("console", console);
        }

        public static bool InstanceOf(JsValue a, JsValue b)
        {
            if (b.Obj is JsNativeFunction nf)
            {
                if (nf.Name == "Array") return a.Obj is JsArray;
                if (nf.Name == "Object") return a.IsObject;
                if (nf.Name == "Function") return a.Obj.IsCallable;
            }
            return false;
        }

        // ------------------------------------------------------------------ methods on primitives

        static readonly Dictionary<string, JsNativeFunction> s_String = new Dictionary<string, JsNativeFunction>();
        static readonly Dictionary<string, JsNativeFunction> s_Number = new Dictionary<string, JsNativeFunction>();
        static readonly Dictionary<string, JsNativeFunction> s_Array = new Dictionary<string, JsNativeFunction>();
        static readonly Dictionary<string, JsNativeFunction> s_Function = new Dictionary<string, JsNativeFunction>();

        static string ThisStr(JsInterpreter it, JsValue t) => JsOps.ToStr(it, JsOps.Prim(it, t));

        static JsArray ThisArr(JsInterpreter it, JsValue t)
        {
            var p = JsOps.Prim(it, t);
            return p.Obj as JsArray ?? new JsArray();
        }

        static JsNativeFunction Fn(Dictionary<string, JsNativeFunction> table, string name, NativeFn f)
        {
            var fn = new JsNativeFunction(name, f);
            table[name] = fn;
            return fn;
        }

        static JsBuiltins()
        {
            // String
            Fn(s_String, "charAt", (i, t, a) => { var s = ThisStr(i, t); int k = (int)Num(i, a, 0, 0); return JsValue.String(k >= 0 && k < s.Length ? s[k].ToString() : ""); });
            Fn(s_String, "charCodeAt", (i, t, a) => { var s = ThisStr(i, t); int k = (int)Num(i, a, 0, 0); return k >= 0 && k < s.Length ? s[k] : double.NaN; });
            Fn(s_String, "indexOf", (i, t, a) => ThisStr(i, t).IndexOf(JsOps.ToStr(i, Arg(a, 0)), Math.Max(0, Math.Min((int)Num(i, a, 1, 0), ThisStr(i, t).Length)), StringComparison.Ordinal));
            Fn(s_String, "lastIndexOf", (i, t, a) => ThisStr(i, t).LastIndexOf(JsOps.ToStr(i, Arg(a, 0)), StringComparison.Ordinal));
            Fn(s_String, "includes", (i, t, a) => JsValue.Bool(ThisStr(i, t).Contains(JsOps.ToStr(i, Arg(a, 0)))));
            Fn(s_String, "startsWith", (i, t, a) => JsValue.Bool(ThisStr(i, t).StartsWith(JsOps.ToStr(i, Arg(a, 0)), StringComparison.Ordinal)));
            Fn(s_String, "endsWith", (i, t, a) => JsValue.Bool(ThisStr(i, t).EndsWith(JsOps.ToStr(i, Arg(a, 0)), StringComparison.Ordinal)));
            Fn(s_String, "toUpperCase", (i, t, a) => JsValue.String(ThisStr(i, t).ToUpperInvariant()));
            Fn(s_String, "toLowerCase", (i, t, a) => JsValue.String(ThisStr(i, t).ToLowerInvariant()));
            Fn(s_String, "trim", (i, t, a) => JsValue.String(ThisStr(i, t).Trim()));
            Fn(s_String, "toString", (i, t, a) => JsValue.String(ThisStr(i, t)));
            Fn(s_String, "valueOf", (i, t, a) => JsValue.String(ThisStr(i, t)));
            Fn(s_String, "concat", (i, t, a) =>
            {
                var sb = new StringBuilder(ThisStr(i, t));
                foreach (var x in a) sb.Append(JsOps.ToStr(i, x));
                return JsValue.String(sb.ToString());
            });
            Fn(s_String, "repeat", (i, t, a) =>
            {
                var sb = new StringBuilder();
                string s = ThisStr(i, t);
                for (int k = 0; k < (int)Num(i, a, 0, 0) && k < 10000; k++) sb.Append(s);
                return JsValue.String(sb.ToString());
            });
            Fn(s_String, "slice", (i, t, a) =>
            {
                var s = ThisStr(i, t);
                int n = s.Length;
                int b = Clamp(Rel((int)Num(i, a, 0, 0), n), 0, n);
                int e = Clamp(Rel(a.Length > 1 && !a[1].IsUndefined ? (int)Num(i, a, 1) : n, n), 0, n);
                return JsValue.String(e > b ? s.Substring(b, e - b) : "");
            });
            Fn(s_String, "substring", (i, t, a) =>
            {
                var s = ThisStr(i, t);
                int n = s.Length;
                int b = Clamp((int)Num(i, a, 0, 0), 0, n);
                int e = Clamp(a.Length > 1 && !a[1].IsUndefined ? (int)Num(i, a, 1) : n, 0, n);
                if (b > e) (b, e) = (e, b);
                return JsValue.String(s.Substring(b, e - b));
            });
            Fn(s_String, "substr", (i, t, a) =>
            {
                var s = ThisStr(i, t);
                int n = s.Length;
                int b = Clamp(Rel((int)Num(i, a, 0, 0), n), 0, n);
                int len = a.Length > 1 && !a[1].IsUndefined ? (int)Num(i, a, 1) : n - b;
                len = Clamp(len, 0, n - b);
                return JsValue.String(s.Substring(b, len));
            });
            Fn(s_String, "split", (i, t, a) =>
            {
                var s = ThisStr(i, t);
                var r = new JsArray();
                if (a.Length == 0 || a[0].IsUndefined) { r.Items.Add(JsValue.String(s)); return r; }
                string sep = JsOps.ToStr(i, a[0]);
                if (sep.Length == 0) { foreach (char c in s) r.Items.Add(JsValue.String(c.ToString())); return r; }
                foreach (var part in s.Split(new[] { sep }, StringSplitOptions.None)) r.Items.Add(JsValue.String(part));
                return r;
            });
            Fn(s_String, "replace", (i, t, a) =>
            {
                var s = ThisStr(i, t);
                string find = JsOps.ToStr(i, Arg(a, 0));
                int at = s.IndexOf(find, StringComparison.Ordinal);
                if (at < 0) return JsValue.String(s);
                string rep = Arg(a, 1).Obj != null && Arg(a, 1).Obj.IsCallable
                    ? JsOps.ToStr(i, i.CallFunction(a[1], JsValue.Undefined, JsValue.String(find)))
                    : JsOps.ToStr(i, Arg(a, 1));
                return JsValue.String(s.Substring(0, at) + rep + s.Substring(at + find.Length));
            });
            Fn(s_String, "padStart", (i, t, a) => JsValue.String(ThisStr(i, t).PadLeft((int)Num(i, a, 0, 0), a.Length > 1 ? JsOps.ToStr(i, a[1]).PadRight(1)[0] : ' ')));
            Fn(s_String, "padEnd", (i, t, a) => JsValue.String(ThisStr(i, t).PadRight((int)Num(i, a, 0, 0), a.Length > 1 ? JsOps.ToStr(i, a[1]).PadRight(1)[0] : ' ')));

            // Number
            Fn(s_Number, "toFixed", (i, t, a) =>
            {
                double d = JsOps.ToNumber(i, t);
                int digits = Clamp((int)Num(i, a, 0, 0), 0, 20);
                return JsValue.String(Math.Round(d, digits, MidpointRounding.AwayFromZero).ToString("F" + digits, CultureInfo.InvariantCulture));
            });
            Fn(s_Number, "toString", (i, t, a) => JsValue.String(JsValue.NumberToString(JsOps.ToNumber(i, t))));
            Fn(s_Number, "valueOf", (i, t, a) => JsOps.ToNumber(i, t));
            Fn(s_Number, "toPrecision", (i, t, a) => JsValue.String(JsOps.ToNumber(i, t).ToString("G" + Clamp((int)Num(i, a, 0, 6), 1, 21), CultureInfo.InvariantCulture)));

            // Array
            Fn(s_Array, "push", (i, t, a) => { var r = ThisArr(i, t); r.Items.AddRange(a); return r.Items.Count; });
            Fn(s_Array, "pop", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                if (r.Items.Count == 0) return JsValue.Undefined;
                var v = r.Items[r.Items.Count - 1];
                r.Items.RemoveAt(r.Items.Count - 1);
                return v;
            });
            Fn(s_Array, "shift", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                if (r.Items.Count == 0) return JsValue.Undefined;
                var v = r.Items[0];
                r.Items.RemoveAt(0);
                return v;
            });
            Fn(s_Array, "unshift", (i, t, a) => { var r = ThisArr(i, t); r.Items.InsertRange(0, a); return r.Items.Count; });
            Fn(s_Array, "slice", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                int n = r.Items.Count;
                int b = Clamp(Rel((int)Num(i, a, 0, 0), n), 0, n);
                int e = Clamp(Rel(a.Length > 1 && !a[1].IsUndefined ? (int)Num(i, a, 1) : n, n), 0, n);
                return new JsArray(e > b ? r.Items.GetRange(b, e - b) : new List<JsValue>());
            });
            Fn(s_Array, "splice", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                int n = r.Items.Count;
                int b = Clamp(Rel((int)Num(i, a, 0, 0), n), 0, n);
                int count = a.Length > 1 ? Clamp((int)Num(i, a, 1, 0), 0, n - b) : n - b;
                var removed = new JsArray(r.Items.GetRange(b, count));
                r.Items.RemoveRange(b, count);
                for (int k = a.Length - 1; k >= 2; k--) r.Items.Insert(b, a[k]);
                return removed;
            });
            Fn(s_Array, "concat", (i, t, a) =>
            {
                var r = ThisArr(i, t).Clone();
                foreach (var x in a)
                {
                    var p = JsOps.Prim(i, x);
                    if (p.Obj is JsArray xa) r.Items.AddRange(xa.Items);
                    else r.Items.Add(x);
                }
                return r;
            });
            Fn(s_Array, "join", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                string sep = a.Length > 0 && !a[0].IsUndefined ? JsOps.ToStr(i, a[0]) : ",";
                var sb = new StringBuilder();
                for (int k = 0; k < r.Items.Count; k++)
                {
                    if (k > 0) sb.Append(sep);
                    if (!r.Items[k].IsNullish) sb.Append(JsOps.ToStr(i, r.Items[k]));
                }
                return JsValue.String(sb.ToString());
            });
            Fn(s_Array, "indexOf", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                for (int k = 0; k < r.Items.Count; k++) if (JsOps.StrictEquals(i, r.Items[k], Arg(a, 0))) return k;
                return -1;
            });
            Fn(s_Array, "includes", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                for (int k = 0; k < r.Items.Count; k++) if (JsOps.StrictEquals(i, r.Items[k], Arg(a, 0))) return JsValue.True;
                return JsValue.False;
            });
            Fn(s_Array, "reverse", (i, t, a) => { var r = ThisArr(i, t); r.Items.Reverse(); return r; });
            Fn(s_Array, "fill", (i, t, a) => { var r = ThisArr(i, t); for (int k = 0; k < r.Items.Count; k++) r.Items[k] = Arg(a, 0); return r; });
            Fn(s_Array, "map", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                var o = new JsArray(r.Items.Count);
                for (int k = 0; k < r.Items.Count; k++) o.Items.Add(i.CallFunction(Arg(a, 0), JsValue.Undefined, r.Items[k], k, r));
                return o;
            });
            Fn(s_Array, "forEach", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                for (int k = 0; k < r.Items.Count; k++) i.CallFunction(Arg(a, 0), JsValue.Undefined, r.Items[k], k, r);
                return JsValue.Undefined;
            });
            Fn(s_Array, "filter", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                var o = new JsArray();
                for (int k = 0; k < r.Items.Count; k++)
                    if (JsOps.ToBool(i, i.CallFunction(Arg(a, 0), JsValue.Undefined, r.Items[k], k, r))) o.Items.Add(r.Items[k]);
                return o;
            });
            Fn(s_Array, "some", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                for (int k = 0; k < r.Items.Count; k++)
                    if (JsOps.ToBool(i, i.CallFunction(Arg(a, 0), JsValue.Undefined, r.Items[k], k, r))) return JsValue.True;
                return JsValue.False;
            });
            Fn(s_Array, "every", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                for (int k = 0; k < r.Items.Count; k++)
                    if (!JsOps.ToBool(i, i.CallFunction(Arg(a, 0), JsValue.Undefined, r.Items[k], k, r))) return JsValue.False;
                return JsValue.True;
            });
            Fn(s_Array, "reduce", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                int k = 0;
                JsValue acc;
                if (a.Length > 1) acc = a[1];
                else
                {
                    if (r.Items.Count == 0) throw new JsRuntimeError("Reduce of empty array with no initial value");
                    acc = r.Items[0];
                    k = 1;
                }
                for (; k < r.Items.Count; k++) acc = i.CallFunction(Arg(a, 0), JsValue.Undefined, acc, r.Items[k], k, r);
                return acc;
            });
            Fn(s_Array, "sort", (i, t, a) =>
            {
                var r = ThisArr(i, t);
                var cmp = Arg(a, 0);
                var list = new List<JsValue>(r.Items);
                if (cmp.Obj != null && cmp.Obj.IsCallable)
                    MergeSort(list, (x, y) => Math.Sign(JsOps.ToNumber(i, i.CallFunction(cmp, JsValue.Undefined, x, y))));
                else
                    MergeSort(list, (x, y) => string.CompareOrdinal(JsOps.ToStr(i, x), JsOps.ToStr(i, y)));
                r.Items.Clear();
                r.Items.AddRange(list);
                return r;
            });
            Fn(s_Array, "toString", (i, t, a) => JsValue.String(JsOps.ToStr(i, JsOps.Prim(i, t))));

            // Function
            Fn(s_Function, "call", (i, t, a) => i.CallFunction(t, Arg(a, 0), a.Length > 1 ? Sub(a, 1) : Array.Empty<JsValue>()));
            Fn(s_Function, "apply", (i, t, a) =>
            {
                var list = Arg(a, 1).Obj is JsArray arr ? arr.Items.ToArray() : Array.Empty<JsValue>();
                return i.CallFunction(t, Arg(a, 0), list);
            });
            Fn(s_Function, "bind", (i, t, a) =>
            {
                var fn = t;
                var self = Arg(a, 0);
                var bound = a.Length > 1 ? Sub(a, 1) : Array.Empty<JsValue>();
                return new JsNativeFunction("bound", (i2, t2, a2) =>
                {
                    var all = new JsValue[bound.Length + a2.Length];
                    bound.CopyTo(all, 0);
                    a2.CopyTo(all, bound.Length);
                    return i2.CallFunction(fn, self, all);
                });
            });
        }

        static JsValue[] Sub(JsValue[] a, int from)
        {
            var r = new JsValue[a.Length - from];
            Array.Copy(a, from, r, 0, r.Length);
            return r;
        }

        static void MergeSort(List<JsValue> list, Comparison<JsValue> cmp)
        {
            // Stable sort (JS Array.prototype.sort is stable).
            var arr = list.ToArray();
            var tmp = new JsValue[arr.Length];
            Sort(arr, tmp, 0, arr.Length, cmp);
            list.Clear();
            list.AddRange(arr);
        }

        static void Sort(JsValue[] a, JsValue[] tmp, int lo, int hi, Comparison<JsValue> cmp)
        {
            if (hi - lo < 2) return;
            int mid = (lo + hi) / 2;
            Sort(a, tmp, lo, mid, cmp);
            Sort(a, tmp, mid, hi, cmp);
            int i = lo, j = mid, k = lo;
            while (i < mid && j < hi) tmp[k++] = cmp(a[j], a[i]) < 0 ? a[j++] : a[i++];
            while (i < mid) tmp[k++] = a[i++];
            while (j < hi) tmp[k++] = a[j++];
            Array.Copy(tmp, lo, a, lo, hi - lo);
        }

        static int Rel(int i, int n) => i < 0 ? n + i : i;
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        public static JsValue StringMethod(string name) => s_String.TryGetValue(name, out var f) ? f : JsValue.Undefined;
        public static JsValue NumberMethod(string name) => s_Number.TryGetValue(name, out var f) ? f : JsValue.Undefined;
        public static JsValue ArrayMethod(string name) => s_Array.TryGetValue(name, out var f) ? f : JsValue.Undefined;
        public static JsValue FunctionMethod(string name) => s_Function.TryGetValue(name, out var f) ? f : JsValue.Undefined;
    }
}
