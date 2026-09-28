using System;
using System.Globalization;
using System.Text;

namespace Mynell.Lottie.Expressions
{
    /// <summary>JavaScript conversions and operators, with After Effects / Bodymovin array arithmetic.</summary>
    public static class JsOps
    {
        /// <summary>Unwraps host values (property references etc.) into primitives or arrays.</summary>
        public static JsValue Prim(JsInterpreter it, JsValue v)
        {
            if (v.Type != JsType.Object) return v;
            var o = v.Obj;
            if (o is JsArray) return v;
            if (o.TryToPrimitive(it, out var p)) return p;
            return v;
        }

        public static double ToNumber(JsInterpreter it, JsValue v)
        {
            switch (v.Type)
            {
                case JsType.Number: return v.Num;
                case JsType.Boolean: return v.Num;
                case JsType.Null: return 0;
                case JsType.Undefined: return double.NaN;
                case JsType.String: return StringToNumber(v.Str);
                case JsType.Object:
                {
                    var p = Prim(it, v);
                    if (p.Type != JsType.Object) return ToNumber(it, p);
                    if (p.Obj is JsArray a)
                    {
                        if (a.Items.Count == 0) return 0;
                        if (a.Items.Count == 1) return ToNumber(it, a.Items[0]);
                    }
                    return double.NaN;
                }
            }
            return double.NaN;
        }

        public static double StringToNumber(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return 0;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)) return hex;
            if (s == "Infinity" || s == "+Infinity") return double.PositiveInfinity;
            if (s == "-Infinity") return double.NegativeInfinity;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
        }

        public static bool ToBool(JsInterpreter it, JsValue v)
        {
            switch (v.Type)
            {
                case JsType.Boolean:
                case JsType.Number: return v.Num != 0 && !double.IsNaN(v.Num);
                case JsType.String: return v.Str.Length > 0;
                case JsType.Object: return true;
                default: return false;
            }
        }

        public static int ToInt32(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
            return unchecked((int)(long)Math.Truncate(d));
        }

        public static string ToStr(JsValue v) => ToStr(null, v);

        public static string ToStr(JsInterpreter it, JsValue v)
        {
            switch (v.Type)
            {
                case JsType.Undefined: return "undefined";
                case JsType.Null: return "null";
                case JsType.Boolean: return v.Num != 0 ? "true" : "false";
                case JsType.Number: return JsValue.NumberToString(v.Num);
                case JsType.String: return v.Str;
                case JsType.Object:
                {
                    if (v.Obj is JsArray a)
                    {
                        var sb = new StringBuilder();
                        for (int i = 0; i < a.Items.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            if (!a.Items[i].IsNullish) sb.Append(ToStr(it, a.Items[i]));
                        }
                        return sb.ToString();
                    }
                    if (it != null)
                    {
                        var p = Prim(it, v);
                        if (p.Type != JsType.Object || p.Obj != v.Obj) return ToStr(it, p);
                    }
                    return v.Obj.IsCallable ? "function" : "[object Object]";
                }
            }
            return "";
        }

        public static string ToKey(JsValue v) => v.IsNumber ? JsValue.NumberToString(v.Num) : ToStr(v);

        public static string TypeOf(JsValue v)
        {
            switch (v.Type)
            {
                case JsType.Undefined: return "undefined";
                case JsType.Null: return "object";
                case JsType.Boolean: return "boolean";
                case JsType.Number: return "number";
                case JsType.String: return "string";
                default: return v.Obj.IsCallable ? "function" : "object";
            }
        }

        static bool IsArr(JsValue v) => v.Obj is JsArray;

        static bool IsNumerable(JsValue v) => v.Type == JsType.Number || v.Type == JsType.Boolean || v.Type == JsType.String;

        // ------------------------------------------------------------------ Bodymovin helpers ($bm_sum etc.)

        public static JsValue Sum(JsInterpreter it, JsValue a, JsValue b)
        {
            a = Prim(it, a);
            b = Prim(it, b);
            if ((IsNumerable(a) && IsNumerable(b)) || a.IsString || b.IsString)
            {
                if (a.IsString || b.IsString) return JsValue.String(ToStr(it, a) + ToStr(it, b));
                return ToNumber(it, a) + ToNumber(it, b);
            }
            if (IsArr(a) && IsNumerable(b))
            {
                var r = a.Arr.Clone();
                if (r.Items.Count > 0) r.Items[0] = ToNumber(it, r.Items[0]) + ToNumber(it, b);
                return r;
            }
            if (IsNumerable(a) && IsArr(b))
            {
                var r = b.Arr.Clone();
                if (r.Items.Count > 0) r.Items[0] = ToNumber(it, a) + ToNumber(it, r.Items[0]);
                return r;
            }
            if (IsArr(a) && IsArr(b))
            {
                var la = a.Arr.Items;
                var lb = b.Arr.Items;
                int n = Math.Max(la.Count, lb.Count);
                var r = new JsArray(n);
                for (int i = 0; i < n; i++)
                {
                    var x = i < la.Count ? la[i] : JsValue.Undefined;
                    var y = i < lb.Count ? lb[i] : JsValue.Undefined;
                    if (x.IsNumber && y.IsNumber) r.Items.Add(x.Num + y.Num);
                    else r.Items.Add(y.IsUndefined ? x : (ToBool(it, x) ? x : y));
                }
                return r;
            }
            if (a.IsNullish && IsNumerable(b)) return double.NaN;
            return 0;
        }

        public static JsValue Sub(JsInterpreter it, JsValue a, JsValue b)
        {
            a = Prim(it, a);
            b = Prim(it, b);
            if (IsNumerable(a) && IsNumerable(b)) return ToNumber(it, a) - ToNumber(it, b);
            if (IsArr(a) && IsNumerable(b))
            {
                var r = a.Arr.Clone();
                if (r.Items.Count > 0) r.Items[0] = ToNumber(it, r.Items[0]) - ToNumber(it, b);
                return r;
            }
            if (IsNumerable(a) && IsArr(b))
            {
                var r = b.Arr.Clone();
                if (r.Items.Count > 0) r.Items[0] = ToNumber(it, a) - ToNumber(it, r.Items[0]);
                return r;
            }
            if (IsArr(a) && IsArr(b))
            {
                var la = a.Arr.Items;
                var lb = b.Arr.Items;
                int n = Math.Max(la.Count, lb.Count);
                var r = new JsArray(n);
                for (int i = 0; i < n; i++)
                {
                    var x = i < la.Count ? la[i] : JsValue.Undefined;
                    var y = i < lb.Count ? lb[i] : JsValue.Undefined;
                    if (x.IsNumber && y.IsNumber) r.Items.Add(x.Num - y.Num);
                    else r.Items.Add(y.IsUndefined ? x : (ToBool(it, x) ? x : y));
                }
                return r;
            }
            return 0;
        }

        public static JsValue Mul(JsInterpreter it, JsValue a, JsValue b)
        {
            a = Prim(it, a);
            b = Prim(it, b);
            if (IsNumerable(a) && IsNumerable(b)) return ToNumber(it, a) * ToNumber(it, b);
            if (IsArr(a) && IsNumerable(b))
            {
                double k = ToNumber(it, b);
                var la = a.Arr.Items;
                var r = new JsArray(la.Count);
                foreach (var x in la) r.Items.Add(ToNumber(it, x) * k);
                return r;
            }
            if (IsNumerable(a) && IsArr(b))
            {
                double k = ToNumber(it, a);
                var lb = b.Arr.Items;
                var r = new JsArray(lb.Count);
                foreach (var x in lb) r.Items.Add(k * ToNumber(it, x));
                return r;
            }
            return 0;
        }

        public static JsValue Div(JsInterpreter it, JsValue a, JsValue b)
        {
            a = Prim(it, a);
            b = Prim(it, b);
            if (IsNumerable(a) && IsNumerable(b)) return ToNumber(it, a) / ToNumber(it, b);
            if (IsArr(a) && IsNumerable(b))
            {
                double k = ToNumber(it, b);
                var la = a.Arr.Items;
                var r = new JsArray(la.Count);
                foreach (var x in la) r.Items.Add(ToNumber(it, x) / k);
                return r;
            }
            if (IsNumerable(a) && IsArr(b))
            {
                double k = ToNumber(it, a);
                var lb = b.Arr.Items;
                var r = new JsArray(lb.Count);
                foreach (var x in lb) r.Items.Add(k / ToNumber(it, x));
                return r;
            }
            return 0;
        }

        public static JsValue Mod(JsInterpreter it, JsValue a, JsValue b)
        {
            a = Prim(it, a);
            b = Prim(it, b);
            double x = a.IsString ? ParseInt(a.Str) : ToNumber(it, a);
            double y = b.IsString ? ParseInt(b.Str) : ToNumber(it, b);
            return x % y;
        }

        static double ParseInt(string s)
        {
            s = s.Trim();
            int end = 0;
            if (end < s.Length && (s[end] == '-' || s[end] == '+')) end++;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            return double.TryParse(s.Substring(0, end), NumberStyles.Integer, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;
        }

        public static JsValue Negate(JsInterpreter it, JsValue a)
        {
            a = Prim(it, a);
            if (IsArr(a))
            {
                var la = a.Arr.Items;
                var r = new JsArray(la.Count);
                foreach (var x in la) r.Items.Add(-ToNumber(it, x));
                return r;
            }
            return -ToNumber(it, a);
        }

        // ------------------------------------------------------------------ operators

        public static JsValue Binary(JsInterpreter it, string op, JsValue a, JsValue b)
        {
            switch (op)
            {
                case "+": return Sum(it, a, b);
                case "-": return Sub(it, a, b);
                case "*": return Mul(it, a, b);
                case "/": return Div(it, a, b);
                case "%": return Mod(it, a, b);
                case "**": return Math.Pow(ToNumber(it, a), ToNumber(it, b));
                case "==": return JsValue.Bool(LooseEquals(it, a, b));
                case "!=": return JsValue.Bool(!LooseEquals(it, a, b));
                case "===": return JsValue.Bool(StrictEquals(it, a, b));
                case "!==": return JsValue.Bool(!StrictEquals(it, a, b));
                case "<": return JsValue.Bool(Compare(it, a, b, out int c) && c < 0);
                case ">": return JsValue.Bool(Compare(it, a, b, out int c2) && c2 > 0);
                case "<=": return JsValue.Bool(Compare(it, a, b, out int c3) && c3 <= 0);
                case ">=": return JsValue.Bool(Compare(it, a, b, out int c4) && c4 >= 0);
                case "&": return ToInt32(ToNumber(it, a)) & ToInt32(ToNumber(it, b));
                case "|": return ToInt32(ToNumber(it, a)) | ToInt32(ToNumber(it, b));
                case "^": return ToInt32(ToNumber(it, a)) ^ ToInt32(ToNumber(it, b));
                case "<<": return ToInt32(ToNumber(it, a)) << (ToInt32(ToNumber(it, b)) & 31);
                case ">>": return ToInt32(ToNumber(it, a)) >> (ToInt32(ToNumber(it, b)) & 31);
                case ">>>": return (double)((uint)ToInt32(ToNumber(it, a)) >> (ToInt32(ToNumber(it, b)) & 31));
                case "instanceof": return JsValue.Bool(a.IsObject && b.IsObject && JsBuiltins.InstanceOf(a, b));
                case "in": return JsValue.Bool(b.Obj != null && b.Obj.Has(ToKey(a)));
            }
            throw new JsRuntimeError("Unsupported operator " + op);
        }

        public static bool StrictEquals(JsInterpreter it, JsValue a, JsValue b)
        {
            if (a.Type != b.Type)
            {
                // Host values compare by their primitive.
                if (a.IsObject && !(a.Obj is JsArray)) { var pa = Prim(it, a); if (!pa.IsObject) return StrictEquals(it, pa, b); }
                if (b.IsObject && !(b.Obj is JsArray)) { var pb = Prim(it, b); if (!pb.IsObject) return StrictEquals(it, a, pb); }
                return false;
            }
            switch (a.Type)
            {
                case JsType.Undefined:
                case JsType.Null: return true;
                case JsType.Boolean:
                case JsType.Number: return a.Num == b.Num;
                case JsType.String: return a.Str == b.Str;
                default: return ReferenceEquals(a.Obj, b.Obj);
            }
        }

        public static bool LooseEquals(JsInterpreter it, JsValue a, JsValue b)
        {
            if (a.Type == b.Type) return StrictEquals(it, a, b);
            if (a.IsNullish && b.IsNullish) return true;
            if (a.IsNullish || b.IsNullish) return false;
            if (a.IsObject && !(a.Obj is JsArray)) a = Prim(it, a);
            if (b.IsObject && !(b.Obj is JsArray)) b = Prim(it, b);
            if (a.IsObject && b.IsObject) return ReferenceEquals(a.Obj, b.Obj);
            if (a.IsObject) return ToStr(it, a) == ToStr(it, b) || ToNumber(it, a) == ToNumber(it, b);
            if (b.IsObject) return ToStr(it, a) == ToStr(it, b) || ToNumber(it, a) == ToNumber(it, b);
            if (a.IsString && b.IsString) return a.Str == b.Str;
            return ToNumber(it, a) == ToNumber(it, b);
        }

        static bool Compare(JsInterpreter it, JsValue a, JsValue b, out int result)
        {
            a = Prim(it, a);
            b = Prim(it, b);
            if (a.IsString && b.IsString)
            {
                result = string.CompareOrdinal(a.Str, b.Str);
                return true;
            }
            double x = ToNumber(it, a), y = ToNumber(it, b);
            if (double.IsNaN(x) || double.IsNaN(y))
            {
                result = 0;
                return false;
            }
            result = x < y ? -1 : x > y ? 1 : 0;
            return true;
        }
    }
}
