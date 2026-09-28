using System;
using System.Collections.Generic;
using Mynell.Lottie.Eval;
using Mynell.Lottie.Json;
using Mynell.Lottie.Model;
using Unity.Mathematics;

namespace Mynell.Lottie.Expressions
{
    public sealed partial class ExpressionBaker
    {
        // ================================================================== global names

        bool Resolve(string name, out JsValue v)
        {
            if (_stack.Count == 0)
            {
                v = JsValue.Undefined;
                return false;
            }
            var top = Top;
            var layer = top.Site.Layer;
            switch (name)
            {
                case "time": v = top.Time; return true;
                case "value": v = top.Value; return true;
                case "thisProperty": v = top.This; return true;
                case "thisLayer": v = new LayerRef(this, layer); return true;
                case "thisComp": v = new CompRef(this, layer.Comp); return true;
                case "comp": v = Fn("comp", (i, t, a) => FindComp(Str(a, 0))); return true;
                case "transform":
                case "$bm_transform": v = LayerMember(layer, "transform"); return true;
                case "position":
                case "scale":
                case "rotation":
                case "anchorPoint":
                case "opacity":
                case "content":
                case "effect":
                case "mask":
                case "inPoint":
                case "outPoint":
                case "startTime":
                case "index":
                case "name":
                case "width":
                case "height":
                case "hasParent":
                case "parent":
                case "active":
                case "enabled":
                case "toComp":
                case "fromComp":
                case "toWorld":
                case "fromWorld":
                case "toCompVec":
                case "fromCompVec":
                case "toWorldVec":
                case "fromWorldVec":
                case "sourceRectAtTime":
                case "timeRemap":
                case "marker":
                    v = LayerMember(layer, name);
                    return true;
                case "numKeys":
                case "key":
                case "nearestKey":
                case "valueAtTime":
                case "velocityAtTime":
                case "speedAtTime":
                case "velocity":
                case "speed":
                case "loopIn":
                case "loopOut":
                case "loop_in":
                case "loop_out":
                case "loopInDuration":
                case "loopOutDuration":
                case "wiggle":
                case "smooth":
                case "temporalWiggle":
                case "propertyGroup":
                case "points":
                case "inTangents":
                case "outTangents":
                case "isClosed":
                case "pointOnPath":
                case "tangentOnPath":
                case "normalOnPath":
                    v = top.This.Get(name.Replace("loop_in", "loopIn").Replace("loop_out", "loopOut"));
                    return true;
                case "fromCompToSurface":
                    v = LayerMember(layer, "fromComp");
                    return true;
                case "posterizeTime":
                    v = Fn(name, (i, t, a) =>
                    {
                        double fps = Num(a, 0, 0);
                        var f = Top;
                        f.Time = fps == 0 ? 0 : Math.Floor(f.Time * fps) / fps;
                        f.Value = RawValue(f.Site.Prop, f.Time * _fr);
                        _stack[_stack.Count - 1] = f;
                        return JsValue.Undefined;
                    });
                    return true;
                case "linear": v = Fn(name, (i, t, a) => Interp(a, null)); return true;
                case "ease": v = Fn(name, (i, t, a) => Interp(a, EaseInOut)); return true;
                case "easeIn": v = Fn(name, (i, t, a) => Interp(a, EaseIn)); return true;
                case "easeOut": v = Fn(name, (i, t, a) => Interp(a, EaseOut)); return true;
                case "clamp":
                    v = Fn(name, (i, t, a) =>
                    {
                        var x = JsOps.Prim(i, Arg(a, 0));
                        double lo = Num(a, 1, double.NegativeInfinity), hi = Num(a, 2, double.PositiveInfinity);
                        if (lo > hi) (lo, hi) = (hi, lo);
                        if (x.Obj is JsArray arr)
                        {
                            var r = new JsArray(arr.Length);
                            var loA = JsOps.Prim(i, Arg(a, 1)).Obj as JsArray;
                            var hiA = JsOps.Prim(i, Arg(a, 2)).Obj as JsArray;
                            for (int k = 0; k < arr.Length; k++)
                            {
                                double l2 = loA != null ? JsOps.ToNumber(i, loA.Items[Math.Min(k, loA.Length - 1)]) : lo;
                                double h2 = hiA != null ? JsOps.ToNumber(i, hiA.Items[Math.Min(k, hiA.Length - 1)]) : hi;
                                r.Items.Add(Math.Min(Math.Max(JsOps.ToNumber(i, arr.Items[k]), l2), h2));
                            }
                            return r;
                        }
                        return Math.Min(Math.Max(JsOps.ToNumber(i, x), lo), hi);
                    });
                    return true;
                case "add":
                case "sum":
                case "$bm_sum": v = Fn(name, (i, t, a) => JsOps.Sum(i, Arg(a, 0), Arg(a, 1))); return true;
                case "sub":
                case "$bm_sub": v = Fn(name, (i, t, a) => JsOps.Sub(i, Arg(a, 0), Arg(a, 1))); return true;
                case "mul":
                case "$bm_mul": v = Fn(name, (i, t, a) => JsOps.Mul(i, Arg(a, 0), Arg(a, 1))); return true;
                case "div":
                case "$bm_div": v = Fn(name, (i, t, a) => JsOps.Div(i, Arg(a, 0), Arg(a, 1))); return true;
                case "$bm_mod": v = Fn(name, (i, t, a) => JsOps.Mod(i, Arg(a, 0), Arg(a, 1))); return true;
                case "$bm_neg": v = Fn(name, (i, t, a) => JsOps.Negate(i, Arg(a, 0))); return true;
                case "$bm_isInstanceOfArray": v = Fn(name, (i, t, a) => JsValue.Bool(JsOps.Prim(i, Arg(a, 0)).Obj is JsArray)); return true;
                case "isNumerable": v = Fn(name, (i, t, a) => JsValue.Bool(JsOps.Prim(i, Arg(a, 1)).IsNumber)); return true;
                case "length":
                    v = Fn(name, (i, t, a) =>
                    {
                        var x = JsOps.Prim(i, Arg(a, 0));
                        var y = JsOps.Prim(i, Arg(a, 1));
                        if (!(x.Obj is JsArray))
                            return Math.Abs(JsOps.ToNumber(i, x) - (y.IsUndefined ? 0 : JsOps.ToNumber(i, y)));
                        var xa = x.Arr;
                        var ya = y.Obj as JsArray;
                        int n = ya != null ? Math.Min(xa.Length, ya.Length) : xa.Length;
                        double s = 0;
                        for (int k = 0; k < n; k++)
                        {
                            double d = (ya != null ? JsOps.ToNumber(i, ya.Items[k]) : 0) - JsOps.ToNumber(i, xa.Items[k]);
                            s += d * d;
                        }
                        return Math.Sqrt(s);
                    });
                    return true;
                case "normalize":
                    v = Fn(name, (i, t, a) =>
                    {
                        var x = JsOps.Prim(i, Arg(a, 0));
                        if (!(x.Obj is JsArray xa)) { double d = JsOps.ToNumber(i, x); return d == 0 ? 0 : d / Math.Abs(d); }
                        double s = 0;
                        foreach (var e in xa.Items) { double d = JsOps.ToNumber(i, e); s += d * d; }
                        s = Math.Sqrt(s);
                        var r = new JsArray(xa.Length);
                        foreach (var e in xa.Items) r.Items.Add(s == 0 ? 0 : JsOps.ToNumber(i, e) / s);
                        return r;
                    });
                    return true;
                case "dot":
                    v = Fn(name, (i, t, a) =>
                    {
                        var x = Vec(Arg(a, 0));
                        var y = Vec(Arg(a, 1));
                        double s = 0;
                        for (int k = 0; k < Math.Min(x.Length, y.Length); k++) s += x[k] * y[k];
                        return s;
                    });
                    return true;
                case "cross":
                    v = Fn(name, (i, t, a) =>
                    {
                        var x = Vec(Arg(a, 0), 3);
                        var y = Vec(Arg(a, 1), 3);
                        return JsArray.FromDoubles(new[] { x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0] });
                    });
                    return true;
                case "lookAt":
                    v = Fn(name, (i, t, a) =>
                    {
                        var from = Vec(Arg(a, 0), 3);
                        var to = Vec(Arg(a, 1), 3);
                        double dx = to[0] - from[0], dy = to[1] - from[1], dz = to[2] - from[2];
                        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (len == 0) return JsArray.FromDoubles(new double[] { 0, 0, 0 });
                        dx /= len; dy /= len; dz /= len;
                        double pitch = Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) / (Math.PI / 180);
                        double yaw = Math.Atan2(dx, -dz) / (Math.PI / 180);
                        return JsArray.FromDoubles(new[] { yaw, pitch, 0 });
                    });
                    return true;
                case "radiansToDegrees":
                case "radians_to_degrees": v = Fn(name, (i, t, a) => Num(a, 0, 0) * 180 / Math.PI); return true;
                case "degreesToRadians":
                case "degrees_to_radians": v = Fn(name, (i, t, a) => Num(a, 0, 0) * Math.PI / 180); return true;
                case "random": v = Fn(name, (i, t, a) => Random(i, a, false)); return true;
                case "gaussRandom": v = Fn(name, (i, t, a) => Random(i, a, true)); return true;
                case "seedRandom":
                    v = Fn(name, (i, t, a) =>
                    {
                        bool timeless = a.Length > 1 && JsOps.ToBool(i, a[1]);
                        i.Random.Seed(Top.Site.Seed * 31.0 + Num(a, 0, 0) + (timeless ? 0 : Top.Time));
                        return JsValue.Undefined;
                    });
                    return true;
                case "noise": v = Fn(name, (i, t, a) => NoiseFn(JsOps.Prim(i, Arg(a, 0)))); return true;
                case "framesToTime": v = Fn(name, (i, t, a) => Num(a, 0, 0) / Num(a, 1, _fr)); return true;
                case "timeToFrames":
                    v = Fn(name, (i, t, a) => (a.Length > 0 && !a[0].IsUndefined ? Num(a, 0, 0) : Top.Time) * Num(a, 1, _fr));
                    return true;
                case "timeToTimecode":
                case "timeToCurrentFormat":
                    v = Fn(name, (i, t, a) =>
                    {
                        double sec = a.Length > 0 ? Num(a, 0, 0) : Top.Time;
                        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
                        return JsValue.String($"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}:{(int)(ts.Milliseconds / 1000.0 * _fr):00}");
                    });
                    return true;
                case "rgbToHsl": v = Fn(name, (i, t, a) => RgbToHsl(Vec(Arg(a, 0), 4))); return true;
                case "hslToRgb": v = Fn(name, (i, t, a) => HslToRgb(Vec(Arg(a, 0), 4))); return true;
                case "hexToRgb": v = Fn(name, (i, t, a) => HexToRgb(Str(a, 0))); return true;
                case "createPath": v = Fn(name, (i, t, a) => PathValue.Create(i, Arg(a, 0), Arg(a, 1), Arg(a, 2), a.Length > 3 ? JsOps.ToBool(i, a[3]) : true)); return true;
                case "footage":
                    v = Fn(name, (i, t, a) => throw new JsRuntimeError("footage() is not supported"));
                    return true;
                case "text":
                {
                    var o = new JsObject();
                    o.Set("sourceText", JsValue.String(""));
                    v = o;
                    return true;
                }
            }
            v = JsValue.Undefined;
            return false;
        }

        // ------------------------------------------------------------------ small helpers

        static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
        double Num(JsValue[] a, int i, double def) => i < a.Length && !a[i].IsUndefined ? JsOps.ToNumber(_it, a[i]) : def;
        string Str(JsValue[] a, int i) => i < a.Length ? JsOps.ToStr(_it, JsOps.Prim(_it, a[i])) : "";
        static JsNativeFunction Fn(string name, NativeFn fn) => new JsNativeFunction(name, fn);

        double[] Vec(JsValue v, int minLen = 0)
        {
            v = JsOps.Prim(_it, v);
            if (v.Obj is JsArray arr)
            {
                var r = new double[Math.Max(minLen, arr.Length)];
                for (int k = 0; k < arr.Length; k++) r[k] = JsOps.ToNumber(_it, arr.Items[k]);
                return r;
            }
            var s = new double[Math.Max(1, minLen)];
            s[0] = JsOps.ToNumber(_it, v);
            return s;
        }

        static JsValue ArrOrNum(double[] v, bool array) => array ? JsArray.FromDoubles(v) : (JsValue)v[0];

        // ------------------------------------------------------------------ interpolation helpers (linear / ease)

        static readonly float4 EaseInBez = new float4(0.333f, 0f, 0.833f, 0.833f);
        static readonly float4 EaseOutBez = new float4(0.167f, 0.167f, 0.667f, 1f);
        static readonly float4 EaseInOutBez = new float4(0.33f, 0f, 0.667f, 1f);
        static double EaseIn(double t) => Easing.Evaluate(EaseInBez.x, EaseInBez.y, EaseInBez.z, EaseInBez.w, t);
        static double EaseOut(double t) => Easing.Evaluate(EaseOutBez.x, EaseOutBez.y, EaseOutBez.z, EaseOutBez.w, t);
        static double EaseInOut(double t) => Easing.Evaluate(EaseInOutBez.x, EaseInOutBez.y, EaseInOutBez.z, EaseInOutBez.w, t);

        JsValue Interp(JsValue[] a, Func<double, double> ease)
        {
            double t = Num(a, 0, 0);
            JsValue v1, v2;
            double tMin, tMax;
            if (a.Length < 4)
            {
                // linear(t, value1, value2): t in [0, 1]
                tMin = 0;
                tMax = 1;
                v1 = JsOps.Prim(_it, Arg(a, 1));
                v2 = JsOps.Prim(_it, Arg(a, 2));
            }
            else
            {
                tMin = Num(a, 1, 0);
                tMax = Num(a, 2, 1);
                v1 = JsOps.Prim(_it, Arg(a, 3));
                v2 = JsOps.Prim(_it, Arg(a, 4));
            }
            if (tMax < tMin)
            {
                (tMin, tMax) = (tMax, tMin);
                (v1, v2) = (v2, v1);
            }
            double perc;
            if (t <= tMin) perc = 0;
            else if (t >= tMax) perc = 1;
            else perc = tMax == tMin ? 0 : (t - tMin) / (tMax - tMin);
            if (ease != null) perc = ease(perc);
            if (v1.Obj is JsArray || v2.Obj is JsArray)
            {
                var x = Vec(v1);
                var y = Vec(v2);
                int n = Math.Max(x.Length, y.Length);
                var r = new double[n];
                for (int k = 0; k < n; k++)
                {
                    double xa = k < x.Length ? x[k] : 0, ya = k < y.Length ? y[k] : 0;
                    r[k] = xa + (ya - xa) * perc;
                }
                return JsArray.FromDoubles(r);
            }
            double p1 = JsOps.ToNumber(_it, v1), p2 = JsOps.ToNumber(_it, v2);
            return p1 + (p2 - p1) * perc;
        }

        // ------------------------------------------------------------------ random / noise

        JsValue Random(JsInterpreter it, JsValue[] a, bool gauss)
        {
            double Next()
            {
                if (!gauss) return it.Random.Next();
                // Box-Muller, clipped to about +-3 sigma and mapped to [0, 1] like After Effects' gaussRandom.
                double u1 = Math.Max(1e-12, it.Random.Next()), u2 = it.Random.Next();
                double n = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                return Math.Min(1, Math.Max(0, 0.5 + n / 6));
            }
            if (a.Length == 0) return Next();
            var p0 = JsOps.Prim(it, a[0]);
            var p1 = a.Length > 1 ? JsOps.Prim(it, a[1]) : JsValue.Undefined;
            bool arr = p0.Obj is JsArray || p1.Obj is JsArray;
            if (a.Length == 1)
            {
                var max = Vec(p0);
                var r = new double[max.Length];
                for (int k = 0; k < r.Length; k++) r[k] = Next() * max[k];
                return ArrOrNum(r, arr);
            }
            var lo = Vec(p0);
            var hi = Vec(p1);
            int n2 = Math.Max(lo.Length, hi.Length);
            var res = new double[n2];
            for (int k = 0; k < n2; k++)
            {
                double l = k < lo.Length ? lo[k] : 0, h = k < hi.Length ? hi[k] : 0;
                res[k] = l + Next() * (h - l);
            }
            return ArrOrNum(res, arr);
        }

        static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352dU;
            x ^= x >> 15;
            x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }

        /// <summary>Smooth 1D value noise in about [-1, 1] (Catmull-Rom through hashed lattice values).</summary>
        static double Noise1(double x, uint seed)
        {
            double fl = Math.Floor(x);
            int i = (int)fl;
            double f = x - fl;
            double R(int n) => Hash((uint)n * 0x9E3779B1u ^ seed) / (double)uint.MaxValue * 2 - 1;
            double p0 = R(i - 1), p1 = R(i), p2 = R(i + 1), p3 = R(i + 2);
            double f2 = f * f, f3 = f2 * f;
            return 0.5 * (2 * p1 + (-p0 + p2) * f + (2 * p0 - 5 * p1 + 4 * p2 - p3) * f2 + (-p0 + 3 * p1 - 3 * p2 + p3) * f3);
        }

        /// <summary>After Effects style wiggle(freq, amp, octaves, amp_mult, t) of a value (deterministic, smooth).</summary>
        JsValue Wiggle(JsValue value, double freq, double amp, int octaves, double ampMult, double t, uint seed)
        {
            var v = JsOps.Prim(_it, value);
            bool arr = v.Obj is JsArray;
            var baseV = Vec(v);
            var r = new double[baseV.Length];
            octaves = Math.Max(1, Math.Min(octaves, 10));
            for (int d = 0; d < baseV.Length; d++)
            {
                double n = 0, a = 1, f = freq;
                for (int o = 0; o < octaves; o++)
                {
                    n += a * Noise1(t * f, seed ^ (uint)(d * 0x68E31DA4) ^ (uint)(o * 0xB5297A4D));
                    a *= ampMult;
                    f *= 2;
                }
                r[d] = baseV[d] + n * amp;
            }
            return ArrOrNum(r, arr);
        }

        JsValue NoiseFn(JsValue v)
        {
            var p = Vec(v, 1);
            double x = p[0], y = p.Length > 1 ? p[1] : 0, z = p.Length > 2 ? p[2] : 0;
            return Perlin.Noise(x, y, z);
        }

        // ------------------------------------------------------------------ colour helpers

        static JsValue RgbToHsl(double[] c)
        {
            double r = c[0], g = c[1], b = c[2];
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double h = 0, s, l = (max + min) / 2;
            if (max == min) s = 0;
            else
            {
                double d = max - min;
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            return JsArray.FromDoubles(new[] { h, s, l, c.Length > 3 ? c[3] : 1 });
        }

        static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        static JsValue HslToRgb(double[] c)
        {
            double h = c[0], s = c[1], l = c[2], r, g, b;
            if (s == 0) r = g = b = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3);
            }
            return JsArray.FromDoubles(new[] { r, g, b, c.Length > 3 ? c[3] : 1 });
        }

        static JsValue HexToRgb(string hex)
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            double C(int i) => i + 2 <= hex.Length ? Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0 : 0;
            return JsArray.FromDoubles(new[] { C(0), C(2), C(4), hex.Length >= 8 ? C(6) : 1 });
        }

        // ================================================================== lookups

        JsValue FindComp(string name)
        {
            foreach (var c in _comps)
                if (c.Name == name) return new CompRef(this, c);
            // Precomp names are not always exported: fall back to a layer referencing the asset.
            foreach (var l in _layers)
                if (l != null && l.Name == name && _data.Layers[l.Index].RefComp >= 0)
                    return new CompRef(this, _comps[_data.Layers[l.Index].RefComp]);
            throw new JsRuntimeError($"Composition '{name}' not found");
        }

        LayerHost FindLayer(CompHost comp, JsValue key)
        {
            key = JsOps.Prim(_it, key);
            if (key.IsNumber)
            {
                int idx = (int)key.Num;
                if (idx >= 1 && idx <= comp.Layers.Count) return comp.Layers[idx - 1];
            }
            else
            {
                string n = JsOps.ToStr(_it, key);
                foreach (var l in comp.Layers)
                    if (l.Name == n) return l;
            }
            throw new JsRuntimeError($"Layer {JsOps.ToStr(_it, key)} not found");
        }

        PropRef Prop(JsonValue json, LayerHost layer, JsObject parent, string name = null)
        {
            if (json.IsMissing) return null;
            if (!_propByJson.TryGetValue(json.NodeId, out int prop)) return null;
            return new PropRef(this, prop, layer, json, parent) { Name = name };
        }

        JsValue PropOrZero(JsonValue json, LayerHost layer, JsObject parent, string name = null)
        {
            var p = Prop(json, layer, parent, name);
            return p != null ? (JsValue)p : 0;
        }

        JsValue LayerMember(LayerHost layer, string name)
        {
            var lj = layer.Json;
            var ks = lj["ks"];
            var ld = _data.Layers[layer.Index];
            switch (name)
            {
                case "transform": return new TransformRef(this, layer, ks, null);
                case "position":
                case "scale":
                case "rotation":
                case "anchorPoint":
                case "opacity":
                    return new TransformRef(this, layer, ks, null).Get(name);
                case "content":
                    return Fn("content", (i, t, a) => new GroupRef(this, layer, lj["shapes"], null, "").Content(Arg(a, 0)));
                case "effect":
                    return Fn("effect", (i, t, a) => FindEffect(layer, Arg(a, 0)));
                case "mask":
                    return Fn("mask", (i, t, a) => FindMask(layer, Arg(a, 0)));
                case "inPoint": return ld.InPoint / _fr;
                case "outPoint": return ld.OutPoint / _fr;
                case "startTime": return ld.StartTime / _fr;
                case "index": return layer.IndexInComp;
                case "name": return JsValue.String(layer.Name);
                case "width": return ld.Width > 0 ? ld.Width : lj["sw"].AsFloat(layer.Comp.Width);
                case "height": return ld.Height > 0 ? ld.Height : lj["sh"].AsFloat(layer.Comp.Height);
                case "hasParent": return JsValue.Bool(layer.Parent != null);
                case "parent": return layer.Parent != null ? (JsValue)new LayerRef(this, layer.Parent) : JsValue.Null;
                case "active":
                case "enabled":
                {
                    double f = CurrentFrame;
                    return JsValue.Bool(!lj["hd"].AsBool() && f >= ld.InPoint && f < ld.OutPoint);
                }
                case "timeRemap": { var tr = Prop(lj["tm"], layer, null, "Time Remap"); return tr != null ? (JsValue)tr : JsValue.Undefined; }
                case "toComp":
                case "toWorld":
                case "fromComp":
                case "fromWorld":
                case "toCompVec":
                case "fromCompVec":
                case "toWorldVec":
                case "fromWorldVec":
                {
                    bool inverse = name.StartsWith("from");
                    bool vec = name.EndsWith("Vec");
                    return Fn(name, (i, t, a) =>
                    {
                        double frame = a.Length > 1 ? Num(a, 1, 0) * _fr : CurrentFrame;
                        var m = LayerMatrix(layer, frame);
                        if (inverse) m = m.Inverse();
                        var p = Vec(Arg(a, 0), 2);
                        float2 r = vec ? m.ApplyVector(new float2((float)p[0], (float)p[1])) : m.Apply(new float2((float)p[0], (float)p[1]));
                        var res = new double[p.Length];
                        Array.Copy(p, res, p.Length);
                        res[0] = r.x;
                        res[1] = r.y;
                        return JsArray.FromDoubles(res);
                    });
                }
                case "sourceRectAtTime":
                    return Fn(name, (i, t, a) =>
                    {
                        var o = new JsObject();
                        o.Set("top", 0);
                        o.Set("left", 0);
                        o.Set("width", layer.Comp.Width);
                        o.Set("height", layer.Comp.Height);
                        return o;
                    });
                case "marker":
                    return new MarkerRef(this, lj["markers"]);
            }
            return JsValue.Undefined;
        }

        JsValue FindEffect(LayerHost layer, JsValue key)
        {
            var ef = layer.Json["ef"];
            key = JsOps.Prim(_it, key);
            int i = 0;
            foreach (var e in ef)
            {
                i++;
                if (key.IsNumber ? (int)key.Num == i : (e["nm"].AsString() == JsOps.ToStr(_it, key) || e["mn"].AsString() == JsOps.ToStr(_it, key)))
                    return new EffectRef(this, layer, e);
            }
            throw new JsRuntimeError($"Effect {JsOps.ToStr(_it, key)} not found");
        }

        JsValue FindMask(LayerHost layer, JsValue key)
        {
            var masks = layer.Json["masksProperties"];
            key = JsOps.Prim(_it, key);
            int i = 0;
            foreach (var m in masks)
            {
                i++;
                if (key.IsNumber ? (int)key.Num == i : m["nm"].AsString() == JsOps.ToStr(_it, key))
                    return MaskObject(layer, m);
            }
            throw new JsRuntimeError($"Mask {JsOps.ToStr(_it, key)} not found");
        }

        JsObject MaskObject(LayerHost layer, JsonValue m)
        {
            var o = new HostObject();
            o.Getter = n =>
            {
                switch (n)
                {
                    case "maskPath": return PropOrZero(m["pt"], layer, o, "Mask Path");
                    case "maskOpacity": return PropOrZero(m["o"], layer, o, "Mask Opacity");
                    case "maskExpansion": return PropOrZero(m["x"], layer, o, "Mask Expansion");
                    case "maskFeather": return JsArray.FromDoubles(new double[] { 0, 0 });
                    case "name": return JsValue.String(m["nm"].AsString(""));
                }
                return JsValue.Undefined;
            };
            o.Invoker = (it, a) => o.Get(JsOps.ToStr(it, JsOps.Prim(it, Arg(a, 0))));
            return o;
        }

        // ================================================================== host objects

        /// <summary>Generic host object with a getter callback.</summary>
        sealed class HostObject : JsObject
        {
            public Func<string, JsValue> Getter;
            public Func<JsInterpreter, JsValue[], JsValue> Invoker;
            public override bool IsCallable => Invoker != null;
            public override JsValue Get(string key)
            {
                var v = Getter != null ? Getter(key) : JsValue.Undefined;
                return v.IsUndefined ? base.Get(key) : v;
            }
            public override JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args) =>
                Invoker != null ? Invoker(it, args) : base.Call(it, thisValue, args);
        }

        /// <summary>An animatable property: behaves like its current value, with AE property methods.</summary>
        sealed class PropRef : JsObject
        {
            readonly ExpressionBaker _b;
            public readonly int Prop;
            public readonly LayerHost Layer;
            public readonly JsonValue Json;
            public readonly JsObject Group;
            public string Name;

            public PropRef(ExpressionBaker b, int prop, LayerHost layer, JsonValue json, JsObject group)
            {
                _b = b;
                Prop = prop;
                Layer = layer;
                Json = json;
                Group = group;
            }

            public override string ClassName => "Property";

            bool IsThis => _b._stack.Count > 0 && _b.Top.Site.Prop == Prop;

            JsValue Now => IsThis ? _b.Top.Value : _b.ValueAt(Prop, _b.CurrentFrame);

            public override bool TryToPrimitive(JsInterpreter it, out JsValue value)
            {
                value = Now;
                return true;
            }

            PropDesc Desc => _b._data.Props[Prop];

            float KeyTime(int i) => _b._data.Keys[Desc.Offset + i].Time;

            JsValue KeyValue(int i)
            {
                var k = _b._data.Keys[Desc.Offset + i];
                int dim = Desc.Dim;
                if ((Desc.Flags & PropFlags.Path) != 0)
                {
                    var buf = new float[dim];
                    Array.Copy(_b._data.Floats, k.Start, buf, 0, dim);
                    return PathValue.FromLayout(buf, dim / 6, (Desc.Flags & PropFlags.Closed) != 0);
                }
                if (dim == 1) return _b._data.Floats[k.Start];
                return JsArray.FromFloats(_b._data.Floats, k.Start, dim);
            }

            /// <summary>valueAtTime: pre-expression value for the property being evaluated, post-expression otherwise.</summary>
            JsValue AtFrame(double frame) => IsThis ? _b.RawValue(Prop, frame) : _b.ValueAt(Prop, frame);

            JsValue Velocity(double seconds)
            {
                const double delta = -0.001;
                var v1 = JsOps.Prim(_b._it, AtFrame(seconds * _b._fr));
                var v2 = JsOps.Prim(_b._it, AtFrame((seconds + delta) * _b._fr));
                if (v1.Obj is JsArray)
                {
                    var a = _b.Vec(v1);
                    var c = _b.Vec(v2);
                    var r = new double[a.Length];
                    for (int k = 0; k < r.Length; k++) r[k] = (c[k] - a[k]) / delta;
                    return JsArray.FromDoubles(r);
                }
                return (JsOps.ToNumber(_b._it, v2) - JsOps.ToNumber(_b._it, v1)) / delta;
            }

            static double Speed(ExpressionBaker b, JsValue vel)
            {
                var v = b.Vec(vel);
                double s = 0;
                foreach (var d in v) s += d * d;
                return Math.Sqrt(s);
            }

            public override JsValue Get(string key)
            {
                var b = _b;
                switch (key)
                {
                    case "value": return Now;
                    case "valueAtTime": return Fn(key, (i, t, a) => AtFrame(b.Num(a, 0, 0) * b._fr));
                    case "velocityAtTime": return Fn(key, (i, t, a) => Velocity(b.Num(a, 0, 0)));
                    case "speedAtTime": return Fn(key, (i, t, a) => Speed(b, Velocity(b.Num(a, 0, 0))));
                    case "velocity": return Velocity(b.CurrentFrame / b._fr);
                    case "speed": return Speed(b, Velocity(b.CurrentFrame / b._fr));
                    case "numKeys": return Desc.KeyCount;
                    case "name": return JsValue.String(Name ?? "");
                    case "propertyIndex": return Json["ix"].AsInt(0);
                    case "key":
                        return Fn(key, (i, t, a) =>
                        {
                            int idx = (int)b.Num(a, 0, 1);
                            if (Desc.KeyCount == 0 || idx < 1 || idx > Desc.KeyCount) throw new JsRuntimeError($"The property has no keyframe at index {idx}");
                            return new KeyRef(b, KeyTime(idx - 1) / b._fr, KeyValue(idx - 1), idx);
                        });
                    case "nearestKey":
                        return Fn(key, (i, t, a) =>
                        {
                            int n = Desc.KeyCount;
                            if (n == 0) return new KeyRef(b, 0, Now, 0);
                            double frame = b.Num(a, 0, 0) * b._fr;
                            int best = 0;
                            double bestD = double.MaxValue;
                            for (int k = 0; k < n; k++)
                            {
                                double d = Math.Abs(KeyTime(k) - frame);
                                if (d < bestD) { bestD = d; best = k; }
                            }
                            return new KeyRef(b, KeyTime(best) / b._fr, KeyValue(best), best + 1);
                        });
                    case "loopIn":
                    case "loopOut":
                    case "loopInDuration":
                    case "loopOutDuration":
                    {
                        bool isIn = key.StartsWith("loopIn");
                        bool dur = key.EndsWith("Duration");
                        return Fn(key, (i, t, a) =>
                        {
                            string type = a.Length > 0 && !a[0].IsUndefined ? JsOps.ToStr(i, a[0]).ToLowerInvariant() : "cycle";
                            double amount = b.Num(a, 1, 0);
                            return isIn ? LoopIn(type, amount, dur) : LoopOut(type, amount, dur);
                        });
                    }
                    case "wiggle":
                        return Fn(key, (i, t, a) => b.Wiggle(Now, b.Num(a, 0, 0), b.Num(a, 1, 0), (int)b.Num(a, 2, 1), b.Num(a, 3, 0.5),
                            a.Length > 4 ? b.Num(a, 4, 0) : b.Top.Time, b.Top.Site.Seed ^ (uint)Prop));
                    case "temporalWiggle":
                        return Fn(key, (i, t, a) =>
                        {
                            double time = a.Length > 4 ? b.Num(a, 4, 0) : b.Top.Time;
                            double freq = b.Num(a, 0, 0), amp = b.Num(a, 1, 0);
                            double w = Noise1(time * freq, (uint)Prop * 7919u) * amp;
                            return AtFrame((time + w) * b._fr);
                        });
                    case "smooth":
                        return Fn(key, (i, t, a) =>
                        {
                            double width = b.Num(a, 0, 0.2), time = a.Length > 2 ? b.Num(a, 2, 0) : b.Top.Time;
                            int samples = Math.Max(1, (int)b.Num(a, 1, 5));
                            double[] acc = null;
                            bool arr = false;
                            for (int s = 0; s < samples; s++)
                            {
                                double st = samples == 1 ? time : time - width / 2 + width * s / (samples - 1);
                                var v = JsOps.Prim(i, AtFrame(st * b._fr));
                                arr |= v.Obj is JsArray;
                                var d = b.Vec(v);
                                if (acc == null) acc = new double[d.Length];
                                for (int k = 0; k < acc.Length && k < d.Length; k++) acc[k] += d[k] / samples;
                            }
                            return ArrOrNum(acc, arr);
                        });
                    case "propertyGroup":
                        return Fn(key, (i, t, a) =>
                        {
                            int levels = (int)b.Num(a, 0, 1);
                            if (levels <= 1 && Group != null) return Group;
                            return new LayerRef(b, Layer);
                        });
                    case "points":
                    case "inTangents":
                    case "outTangents":
                    case "isClosed":
                        return Fn(key, (i, t, a) =>
                        {
                            var v = a.Length > 0 && !a[0].IsUndefined ? AtFrame(b.Num(a, 0, 0) * b._fr) : Now;
                            return v.Obj is PathValue pv ? pv.Get(key).Obj.Call(i, v, Array.Empty<JsValue>()) : JsValue.Undefined;
                        });
                    case "pointOnPath":
                    case "tangentOnPath":
                    case "normalOnPath":
                        return Fn(key, (i, t, a) =>
                        {
                            var v = a.Length > 1 && !a[1].IsUndefined ? AtFrame(b.Num(a, 1, 0) * b._fr) : Now;
                            if (!(v.Obj is PathValue pv)) return JsValue.Undefined;
                            return pv.Get(key).Obj.Call(i, v, new[] { a.Length > 0 ? a[0] : (JsValue)0.5 });
                        });
                }
                var prim = JsOps.Prim(b._it, Now);
                return b._it.GetMember(prim, key);
            }

            public override bool Has(string key) => false;

            // lottie-web loopOut / loopIn (After Effects semantics for the default cycle of loopIn).
            JsValue LoopOut(string type, double duration, bool durationFlag)
            {
                var b = _b;
                int n = Desc.KeyCount;
                if (n < 2) return Now;
                double current = b.CurrentFrame;
                double lastKey = KeyTime(n - 1);
                if (current <= lastKey) return Now;
                double firstKey, cycle;
                if (!durationFlag)
                {
                    int d = (int)duration;
                    if (d <= 0 || d > n - 1) d = n - 1;
                    firstKey = KeyTime(n - 1 - d);
                    cycle = lastKey - firstKey;
                }
                else
                {
                    var ld = b._data.Layers[Layer.Index];
                    cycle = duration == 0 ? Math.Max(0, lastKey - ld.InPoint) : Math.Abs(lastKey - b._fr * duration);
                    firstKey = lastKey - cycle;
                }
                if (cycle <= 0) return Now;
                double into = current - firstKey;
                switch (type)
                {
                    case "pingpong":
                    {
                        int iterations = (int)Math.Floor(into / cycle);
                        if (iterations % 2 != 0) return b.RawValue(Prop, cycle - (into % cycle) + firstKey);
                        break;
                    }
                    case "offset":
                    {
                        var initV = b.Vec(b.RawValue(Prop, firstKey));
                        var endV = b.Vec(b.RawValue(Prop, lastKey));
                        var cur = JsOps.Prim(b._it, b.RawValue(Prop, into % cycle + firstKey));
                        var curV = b.Vec(cur);
                        double repeats = Math.Floor(into / cycle);
                        var r = new double[curV.Length];
                        for (int k = 0; k < r.Length; k++) r[k] = (endV[k] - initV[k]) * repeats + curV[k];
                        return ArrOrNum(r, cur.Obj is JsArray);
                    }
                    case "continue":
                    {
                        var last = JsOps.Prim(b._it, b.RawValue(Prop, lastKey));
                        var lastV = b.Vec(last);
                        var prevV = b.Vec(b.RawValue(Prop, lastKey - 0.001));
                        var r = new double[lastV.Length];
                        for (int k = 0; k < r.Length; k++) r[k] = lastV[k] + (lastV[k] - prevV[k]) * ((current - lastKey) / 0.001);
                        return ArrOrNum(r, last.Obj is JsArray);
                    }
                }
                return b.RawValue(Prop, into % cycle + firstKey);
            }

            JsValue LoopIn(string type, double duration, bool durationFlag)
            {
                var b = _b;
                int n = Desc.KeyCount;
                if (n < 2) return Now;
                double current = b.CurrentFrame;
                double firstKey = KeyTime(0);
                if (current >= firstKey) return Now;
                double lastKey, cycle;
                if (!durationFlag)
                {
                    int d = (int)duration;
                    if (d <= 0 || d > n - 1) d = n - 1;
                    lastKey = KeyTime(d);
                    cycle = lastKey - firstKey;
                }
                else
                {
                    var ld = b._data.Layers[Layer.Index];
                    cycle = duration == 0 ? Math.Max(0, ld.OutPoint - firstKey) : Math.Abs(b._fr * duration);
                    lastKey = firstKey + cycle;
                }
                if (cycle <= 0) return Now;
                double before = firstKey - current;
                switch (type)
                {
                    case "pingpong":
                    {
                        int iterations = (int)Math.Floor(before / cycle);
                        if (iterations % 2 == 0) return b.RawValue(Prop, before % cycle + firstKey);
                        break;
                    }
                    case "offset":
                    {
                        var initV = b.Vec(b.RawValue(Prop, firstKey));
                        var endV = b.Vec(b.RawValue(Prop, lastKey));
                        var cur = JsOps.Prim(b._it, b.RawValue(Prop, cycle - before % cycle + firstKey));
                        var curV = b.Vec(cur);
                        double repeats = Math.Floor(before / cycle) + 1;
                        var r = new double[curV.Length];
                        for (int k = 0; k < r.Length; k++) r[k] = curV[k] - (endV[k] - initV[k]) * repeats;
                        return ArrOrNum(r, cur.Obj is JsArray);
                    }
                    case "continue":
                    {
                        var first = JsOps.Prim(b._it, b.RawValue(Prop, firstKey));
                        var firstV = b.Vec(first);
                        var nextV = b.Vec(b.RawValue(Prop, firstKey + 0.001));
                        var r = new double[firstV.Length];
                        for (int k = 0; k < r.Length; k++) r[k] = firstV[k] + (firstV[k] - nextV[k]) * before / 0.001;
                        return ArrOrNum(r, first.Obj is JsArray);
                    }
                }
                // cycle: map into [firstKey, lastKey) counting back from the end of the cycle.
                return b.RawValue(Prop, firstKey + cycle - before % cycle);
            }
        }

        sealed class KeyRef : JsObject
        {
            readonly ExpressionBaker _b;
            readonly double _time;
            readonly JsValue _value;
            readonly int _index;

            public KeyRef(ExpressionBaker b, double time, JsValue value, int index)
            {
                _b = b;
                _time = time;
                _value = value;
                _index = index;
            }

            public override bool TryToPrimitive(JsInterpreter it, out JsValue value)
            {
                value = _value;
                return true;
            }

            public override JsValue Get(string key)
            {
                switch (key)
                {
                    case "time": return _time;
                    case "value": return _value;
                    case "index": return _index;
                }
                return _b._it.GetMember(_value, key);
            }
        }

        sealed class MarkerRef : JsObject
        {
            readonly ExpressionBaker _b;
            readonly JsonValue _markers;

            public MarkerRef(ExpressionBaker b, JsonValue markers)
            {
                _b = b;
                _markers = markers;
            }

            public override JsValue Get(string key)
            {
                switch (key)
                {
                    case "numKeys": return _markers.Count;
                    case "key":
                        return Fn(key, (i, t, a) =>
                        {
                            var k = JsOps.Prim(i, Arg(a, 0));
                            int idx = 0;
                            foreach (var m in _markers)
                            {
                                idx++;
                                bool hit = k.IsNumber ? idx == (int)k.Num : m["cm"].AsString() == JsOps.ToStr(i, k);
                                if (!hit) continue;
                                var o = new JsObject();
                                o.Set("time", m["tm"].AsFloat(0) / _b._fr);
                                o.Set("duration", m["dr"].AsFloat(0) / _b._fr);
                                o.Set("comment", JsValue.String(m["cm"].AsString("")));
                                o.Set("index", idx);
                                return o;
                            }
                            throw new JsRuntimeError("Marker not found");
                        });
                    case "nearestKey":
                        return Fn(key, (i, t, a) =>
                        {
                            double time = _b.Num(a, 0, 0);
                            JsObject best = null;
                            double bestD = double.MaxValue;
                            int idx = 0;
                            foreach (var m in _markers)
                            {
                                idx++;
                                double mt = m["tm"].AsFloat(0) / _b._fr;
                                if (Math.Abs(mt - time) < bestD)
                                {
                                    bestD = Math.Abs(mt - time);
                                    best = new JsObject();
                                    best.Set("time", mt);
                                    best.Set("index", idx);
                                    best.Set("comment", JsValue.String(m["cm"].AsString("")));
                                }
                            }
                            if (best == null) throw new JsRuntimeError("No markers");
                            return best;
                        });
                }
                return JsValue.Undefined;
            }
        }

        sealed class LayerRef : JsObject
        {
            readonly ExpressionBaker _b;
            public readonly LayerHost Layer;

            public LayerRef(ExpressionBaker b, LayerHost layer)
            {
                _b = b;
                Layer = layer;
            }

            public override string ClassName => "Layer";
            public override bool IsCallable => true;

            public override JsValue Get(string key)
            {
                var v = _b.LayerMember(Layer, key);
                if (!v.IsUndefined) return v;
                switch (key)
                {
                    case "source":
                    {
                        int rc = _b._data.Layers[Layer.Index].RefComp;
                        return rc >= 0 ? (JsValue)new CompRef(_b, _b._comps[rc]) : JsValue.Undefined;
                    }
                    case "text":
                    {
                        var o = new JsObject();
                        o.Set("sourceText", JsValue.String(""));
                        return o;
                    }
                }
                return JsValue.Undefined;
            }

            // thisLayer("ADBE Root Vectors Group") / ("ADBE Transform Group") / ("ADBE Effect Parade")
            public override JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args)
            {
                var k = JsOps.Prim(it, Arg(args, 0));
                string s = JsOps.ToStr(it, k);
                switch (s)
                {
                    case "ADBE Root Vectors Group":
                    case "Contents":
                    case "2":
                        return new GroupRef(_b, Layer, Layer.Json["shapes"], null, "Contents");
                    case "ADBE Transform Group":
                    case "Transform":
                        return new TransformRef(_b, Layer, Layer.Json["ks"], null);
                    case "ADBE Effect Parade":
                    case "Effects":
                    case "4":
                        return _b.LayerMember(Layer, "effect");
                    case "ADBE Mask Parade":
                    case "Masks":
                        return _b.LayerMember(Layer, "mask");
                }
                return JsValue.Undefined;
            }
        }

        sealed class CompRef : JsObject
        {
            readonly ExpressionBaker _b;
            readonly CompHost _c;

            public CompRef(ExpressionBaker b, CompHost c)
            {
                _b = b;
                _c = c;
            }

            public override string ClassName => "Comp";

            public override JsValue Get(string key)
            {
                switch (key)
                {
                    case "layer": return Fn(key, (i, t, a) => new LayerRef(_b, _b.FindLayer(_c, Arg(a, 0))));
                    case "numLayers": return _c.Layers.Count;
                    case "width": return _c.Width;
                    case "height": return _c.Height;
                    case "name": return JsValue.String(_c.Name);
                    case "frameDuration": return 1.0 / _b._fr;
                    case "duration": return (_b._data.OutPoint - _b._data.InPoint) / _b._fr;
                    case "displayStartTime": return 0;
                    case "pixelAspect": return 1;
                    case "marker": return new MarkerRef(_b, _c.Index == 0 ? _c.Json["markers"] : default);
                }
                return JsValue.Undefined;
            }
        }

        sealed class TransformRef : JsObject
        {
            readonly ExpressionBaker _b;
            readonly LayerHost _layer;
            readonly JsonValue _ks;
            readonly JsObject _parent;

            public TransformRef(ExpressionBaker b, LayerHost layer, JsonValue ks, JsObject parent)
            {
                _b = b;
                _layer = layer;
                _ks = ks;
                _parent = parent;
            }

            public override string ClassName => "Transform";
            public override bool IsCallable => true;

            public override JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args) => Get(JsOps.ToStr(it, JsOps.Prim(it, Arg(args, 0))));

            public override JsValue Get(string key)
            {
                var ks = _ks;
                switch (key)
                {
                    case "anchorPoint":
                    case "Anchor Point":
                    case "ADBE Anchor Point":
                        return _b.PropOrZero(ks["a"], _layer, this, "Anchor Point");
                    case "position":
                    case "Position":
                    case "ADBE Position":
                    {
                        var p = ks["p"];
                        if (p["s"].AsBool())
                        {
                            // Separated dimensions: compose [x, y(, z)].
                            var o = new HostObject();
                            var px = _b.Prop(p["x"], _layer, this);
                            var py = _b.Prop(p["y"], _layer, this);
                            var pz = _b.Prop(p["z"], _layer, this);
                            var arr = new JsArray();
                            arr.Items.Add(px != null ? JsOps.Prim(_b._it, px) : 0);
                            arr.Items.Add(py != null ? JsOps.Prim(_b._it, py) : 0);
                            if (pz != null) arr.Items.Add(JsOps.Prim(_b._it, pz));
                            return arr;
                        }
                        return _b.PropOrZero(p, _layer, this, "Position");
                    }
                    case "xPosition":
                    case "X Position":
                        return _b.PropOrZero(ks["p"]["x"], _layer, this, "X Position");
                    case "yPosition":
                    case "Y Position":
                        return _b.PropOrZero(ks["p"]["y"], _layer, this, "Y Position");
                    case "zPosition":
                    case "Z Position":
                        return _b.PropOrZero(ks["p"]["z"], _layer, this, "Z Position");
                    case "scale":
                    case "Scale":
                    case "ADBE Scale":
                        return _b.PropOrZero(ks["s"], _layer, this, "Scale");
                    case "rotation":
                    case "Rotation":
                    case "zRotation":
                    case "ADBE Rotate Z":
                        return ks.Has("r") ? _b.PropOrZero(ks["r"], _layer, this, "Rotation") : _b.PropOrZero(ks["rz"], _layer, this, "Z Rotation");
                    case "xRotation":
                        return _b.PropOrZero(ks["rx"], _layer, this, "X Rotation");
                    case "yRotation":
                        return _b.PropOrZero(ks["ry"], _layer, this, "Y Rotation");
                    case "orientation":
                        return _b.PropOrZero(ks["or"], _layer, this, "Orientation");
                    case "opacity":
                    case "Opacity":
                    case "ADBE Opacity":
                        return _b.PropOrZero(ks["o"], _layer, this, "Opacity");
                    case "skew":
                        return _b.PropOrZero(ks["sk"], _layer, this, "Skew");
                    case "skewAxis":
                        return _b.PropOrZero(ks["sa"], _layer, this, "Skew Axis");
                    // Shape group / repeater transforms.
                    case "startOpacity":
                        return _b.PropOrZero(ks["so"], _layer, this, "Start Opacity");
                    case "endOpacity":
                        return _b.PropOrZero(ks["eo"], _layer, this, "End Opacity");
                }
                return JsValue.Undefined;
            }
        }

        sealed class EffectRef : JsObject
        {
            readonly ExpressionBaker _b;
            readonly LayerHost _layer;
            readonly JsonValue _e;

            public EffectRef(ExpressionBaker b, LayerHost layer, JsonValue e)
            {
                _b = b;
                _layer = layer;
                _e = e;
            }

            public override string ClassName => "Effect";
            public override bool IsCallable => true;

            JsValue Param(JsInterpreter it, JsValue key)
            {
                key = JsOps.Prim(it, key);
                int i = 0;
                foreach (var p in _e["ef"])
                {
                    i++;
                    bool hit = key.IsNumber ? (int)key.Num == i
                        : p["nm"].AsString() == JsOps.ToStr(it, key) || p["mn"].AsString() == JsOps.ToStr(it, key);
                    if (!hit) continue;
                    if (p["ty"].AsInt(0) == 10)
                    {
                        // Layer control: resolves to the referenced layer (by "ind") in the same composition.
                        var pv = _b.Prop(p["v"], _layer, this);
                        double ind = pv != null ? JsOps.ToNumber(it, pv) : p["v"]["k"].AsFloat(0);
                        foreach (var l in _layer.Comp.Layers)
                            if (l.Json["ind"].AsInt(int.MinValue) == (int)ind) return new LayerRef(_b, l);
                        return JsValue.Null;
                    }
                    return _b.PropOrZero(p["v"], _layer, this, p["nm"].AsString(""));
                }
                throw new JsRuntimeError($"Effect parameter {JsOps.ToStr(it, key)} not found");
            }

            public override JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args) => Param(it, Arg(args, 0));

            public override JsValue Get(string key)
            {
                switch (key)
                {
                    case "param": return Fn(key, (i, t, a) => Param(i, Arg(a, 0)));
                    case "name": return JsValue.String(_e["nm"].AsString(""));
                    case "active":
                    case "enabled": return JsValue.Bool(_e["en"].AsInt(1) != 0);
                    case "numProperties": return _e["ef"].Count;
                }
                // effect("Slider Control").slider style access by display name.
                foreach (var p in _e["ef"])
                {
                    string nm = p["nm"].AsString("");
                    if (string.Equals(nm.Replace(" ", ""), key, StringComparison.OrdinalIgnoreCase))
                        return _b.PropOrZero(p["v"], _layer, this, nm);
                }
                return JsValue.Undefined;
            }
        }

        /// <summary>Shape content group (layer contents or a 'gr' item).</summary>
        sealed class GroupRef : JsObject
        {
            readonly ExpressionBaker _b;
            readonly LayerHost _layer;
            readonly JsonValue _items;     // array of shape items
            readonly JsonValue _group;     // 'gr' item (default for layer root)
            readonly string _name;

            public GroupRef(ExpressionBaker b, LayerHost layer, JsonValue items, JsonValue? group, string name)
            {
                _b = b;
                _layer = layer;
                _items = items;
                _group = group ?? default;
                _name = name;
            }

            public override string ClassName => "PropertyGroup";
            public override bool IsCallable => true;

            public JsValue Content(JsValue key)
            {
                key = JsOps.Prim(_b._it, key);
                int i = 0;
                foreach (var item in _items)
                {
                    i++;
                    bool hit = key.IsNumber ? (int)key.Num == i
                        : item["nm"].AsString() == JsOps.ToStr(_b._it, key) || item["mn"].AsString() == JsOps.ToStr(_b._it, key);
                    if (hit) return _b.ShapeItem(_layer, item, this);
                }
                throw new JsRuntimeError($"Content {JsOps.ToStr(_b._it, key)} not found");
            }

            public override JsValue Call(JsInterpreter it, JsValue thisValue, JsValue[] args)
            {
                var k = JsOps.Prim(it, Arg(args, 0));
                if (k.IsString)
                {
                    switch (k.Str)
                    {
                        case "ADBE Vectors Group":
                        case "Contents":
                            return this;
                        case "ADBE Vector Transform Group":
                        case "Transform":
                            return Get("transform");
                    }
                }
                return Content(k);
            }

            public override JsValue Get(string key)
            {
                switch (key)
                {
                    case "content": return Fn(key, (i, t, a) => Content(Arg(a, 0)));
                    case "name": return JsValue.String(_name);
                    case "numProperties": return _items.Count;
                    case "transform":
                    case "ADBE Vector Transform Group":
                        foreach (var item in _items)
                            if (item["ty"].AsString() == "tr") return new TransformRef(_b, _layer, item, this);
                        return JsValue.Undefined;
                    case "propertyGroup": return Fn(key, (i, t, a) => this);
                }
                // Direct child access by name (group.content("x") shorthand used by some rigs).
                foreach (var item in _items)
                    if (item["nm"].AsString() == key) return _b.ShapeItem(_layer, item, this);
                return JsValue.Undefined;
            }
        }

        JsValue ShapeItem(LayerHost layer, JsonValue item, JsObject parent)
        {
            string ty = item["ty"].AsString("");
            if (ty == "gr") return new GroupRef(this, layer, item["it"], item, item["nm"].AsString(""));
            if (ty == "tr") return new TransformRef(this, layer, item, parent);
            var o = new HostObject();
            JsValue P(string k, string display) => PropOrZero(item[k], layer, o, display);
            o.Getter = key =>
            {
                switch (key)
                {
                    case "name": return JsValue.String(item["nm"].AsString(""));
                    case "propertyGroup": return Fn(key, (i, t, a) => parent);
                }
                switch (ty)
                {
                    case "rc":
                        if (key == "size" || key == "Size") return P("s", "Size");
                        if (key == "position" || key == "Position") return P("p", "Position");
                        if (key == "roundness" || key == "Roundness") return P("r", "Roundness");
                        break;
                    case "el":
                        if (key == "size" || key == "Size") return P("s", "Size");
                        if (key == "position" || key == "Position") return P("p", "Position");
                        break;
                    case "sr":
                        switch (key)
                        {
                            case "position": return P("p", "Position");
                            case "rotation": return P("r", "Rotation");
                            case "points": return P("pt", "Points");
                            case "outerRadius": return P("or", "Outer Radius");
                            case "outerRoundness": return P("os", "Outer Roundness");
                            case "innerRadius": return P("ir", "Inner Radius");
                            case "innerRoundness": return P("is", "Inner Roundness");
                        }
                        break;
                    case "sh":
                        if (key == "path" || key == "Path" || key == "shape" || key == "ADBE Vector Shape") return P("ks", "Path");
                        break;
                    case "fl":
                    case "st":
                    case "gf":
                    case "gs":
                        switch (key)
                        {
                            case "color": case "Color": return P("c", "Color");
                            case "opacity": case "Opacity": return P("o", "Opacity");
                            case "strokeWidth": case "Stroke Width": return P("w", "Stroke Width");
                            case "startPoint": return P("s", "Start Point");
                            case "endPoint": return P("e", "End Point");
                        }
                        break;
                    case "tm":
                        switch (key)
                        {
                            case "start": case "Start": return P("s", "Start");
                            case "end": case "End": return P("e", "End");
                            case "offset": case "Offset": return P("o", "Offset");
                        }
                        break;
                    case "rd":
                        if (key == "radius" || key == "Radius") return P("r", "Radius");
                        break;
                    case "rp":
                        switch (key)
                        {
                            case "copies": return P("c", "Copies");
                            case "offset": return P("o", "Offset");
                            case "transform": return new TransformRef(this, layer, item["tr"], o);
                        }
                        break;
                    case "pb":
                        if (key == "amount") return P("a", "Amount");
                        break;
                    case "zz":
                        if (key == "size") return P("s", "Size");
                        if (key == "ridgesPerSegment") return P("r", "Ridges per segment");
                        break;
                    case "op":
                        if (key == "amount") return P("a", "Amount");
                        if (key == "miterLimit") return P("ml", "Miter Limit");
                        break;
                }
                return JsValue.Undefined;
            };
            o.Invoker = (i, a) => o.Get(JsOps.ToStr(i, JsOps.Prim(i, Arg(a, 0))));
            return o;
        }
    }

    /// <summary>Bezier path value (After Effects path object: points / inTangents / outTangents / isClosed).</summary>
    sealed class PathValue : JsObject
    {
        public double[] V, I, O;   // x, y pairs; tangents relative to the vertex
        public bool Closed;

        public int Count => V.Length / 2;

        public static PathValue FromLayout(float[] buf, int count, bool closed)
        {
            var p = new PathValue { V = new double[count * 2], I = new double[count * 2], O = new double[count * 2], Closed = closed };
            for (int k = 0; k < count; k++)
            {
                double vx = buf[k * 6], vy = buf[k * 6 + 1];
                p.V[k * 2] = vx;
                p.V[k * 2 + 1] = vy;
                p.I[k * 2] = buf[k * 6 + 2] - vx;
                p.I[k * 2 + 1] = buf[k * 6 + 3] - vy;
                p.O[k * 2] = buf[k * 6 + 4] - vx;
                p.O[k * 2 + 1] = buf[k * 6 + 5] - vy;
            }
            return p;
        }

        public float[] ToLayout()
        {
            int n = Count;
            var r = new float[n * 6];
            for (int k = 0; k < n; k++)
            {
                double vx = V[k * 2], vy = V[k * 2 + 1];
                r[k * 6] = (float)vx;
                r[k * 6 + 1] = (float)vy;
                r[k * 6 + 2] = (float)(vx + I[k * 2]);
                r[k * 6 + 3] = (float)(vy + I[k * 2 + 1]);
                r[k * 6 + 4] = (float)(vx + O[k * 2]);
                r[k * 6 + 5] = (float)(vy + O[k * 2 + 1]);
            }
            return r;
        }

        static double[] Pairs(JsInterpreter it, JsValue v, int count)
        {
            var r = new double[count * 2];
            v = JsOps.Prim(it, v);
            if (!(v.Obj is JsArray arr)) return r;
            for (int k = 0; k < count && k < arr.Length; k++)
            {
                var p = JsOps.Prim(it, arr.Items[k]);
                if (p.Obj is JsArray pa)
                {
                    r[k * 2] = pa.Length > 0 ? JsOps.ToNumber(it, pa.Items[0]) : 0;
                    r[k * 2 + 1] = pa.Length > 1 ? JsOps.ToNumber(it, pa.Items[1]) : 0;
                }
            }
            return r;
        }

        public static JsValue Create(JsInterpreter it, JsValue points, JsValue inT, JsValue outT, bool closed)
        {
            var pts = JsOps.Prim(it, points).Obj as JsArray;
            int n = pts?.Length ?? 0;
            return new PathValue
            {
                V = Pairs(it, points, n),
                I = Pairs(it, inT, n),
                O = Pairs(it, outT, n),
                Closed = closed,
            };
        }

        /// <summary>Point and unit tangent at an arc-length fraction of the path.</summary>
        void Sample(double perc, out double px, out double py, out double tx, out double ty)
        {
            int n = Count;
            px = py = tx = ty = 0;
            if (n == 0) return;
            int segs = Closed ? n : n - 1;
            if (segs <= 0) { px = V[0]; py = V[1]; tx = 1; return; }
            const int steps = 64;
            var lens = new double[segs * steps];
            double total = 0;
            for (int s = 0; s < segs; s++)
            {
                GetSeg(s, out double x0, out double y0, out double x1, out double y1, out double x2, out double y2, out double x3, out double y3);
                double lx = x0, ly = y0;
                for (int k = 1; k <= steps; k++)
                {
                    double u = k / (double)steps;
                    Bez(u, x0, y0, x1, y1, x2, y2, x3, y3, out double bx, out double by);
                    total += Math.Sqrt((bx - lx) * (bx - lx) + (by - ly) * (by - ly));
                    lens[s * steps + k - 1] = total;
                    lx = bx; ly = by;
                }
            }
            double target = perc * total;
            int idx = 0;
            while (idx < lens.Length - 1 && lens[idx] < target) idx++;
            int seg = idx / steps;
            double prev = idx == 0 ? 0 : lens[idx - 1];
            double frac = lens[idx] > prev ? (target - prev) / (lens[idx] - prev) : 0;
            double uu = ((idx % steps) + frac) / steps;
            GetSeg(seg, out double a0, out double b0, out double a1, out double b1, out double a2, out double b2, out double a3, out double b3);
            Bez(uu, a0, b0, a1, b1, a2, b2, a3, b3, out px, out py);
            // derivative
            double w = 1 - uu;
            double dx = 3 * w * w * (a1 - a0) + 6 * w * uu * (a2 - a1) + 3 * uu * uu * (a3 - a2);
            double dy = 3 * w * w * (b1 - b0) + 6 * w * uu * (b2 - b1) + 3 * uu * uu * (b3 - b2);
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) { dx = a3 - a0; dy = b3 - b0; len = Math.Sqrt(dx * dx + dy * dy); }
            if (len > 1e-12) { tx = dx / len; ty = dy / len; }
        }

        void GetSeg(int s, out double x0, out double y0, out double x1, out double y1, out double x2, out double y2, out double x3, out double y3)
        {
            int a = s, b = (s + 1) % Count;
            x0 = V[a * 2]; y0 = V[a * 2 + 1];
            x1 = x0 + O[a * 2]; y1 = y0 + O[a * 2 + 1];
            x3 = V[b * 2]; y3 = V[b * 2 + 1];
            x2 = x3 + I[b * 2]; y2 = y3 + I[b * 2 + 1];
        }

        static void Bez(double u, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3, out double x, out double y)
        {
            double w = 1 - u;
            x = w * w * w * x0 + 3 * w * w * u * x1 + 3 * w * u * u * x2 + u * u * u * x3;
            y = w * w * w * y0 + 3 * w * w * u * y1 + 3 * w * u * u * y2 + u * u * u * y3;
        }

        JsArray ToArray(double[] src)
        {
            var a = new JsArray(Count);
            for (int k = 0; k < Count; k++) a.Items.Add(JsArray.FromDoubles(new[] { src[k * 2], src[k * 2 + 1] }));
            return a;
        }

        public override JsValue Get(string key)
        {
            switch (key)
            {
                case "points": return new JsNativeFunction(key, (i, t, a) => ToArray(V));
                case "inTangents": return new JsNativeFunction(key, (i, t, a) => ToArray(I));
                case "outTangents": return new JsNativeFunction(key, (i, t, a) => ToArray(O));
                case "isClosed": return new JsNativeFunction(key, (i, t, a) => JsValue.Bool(Closed));
                case "pointOnPath":
                case "tangentOnPath":
                case "normalOnPath":
                    return new JsNativeFunction(key, (i, t, a) =>
                    {
                        double perc = a.Length > 0 ? JsOps.ToNumber(i, a[0]) : 0.5;
                        Sample(Math.Max(0, Math.Min(1, perc)), out double px, out double py, out double tx, out double ty);
                        if (key == "pointOnPath") return JsArray.FromDoubles(new[] { px, py });
                        if (key == "tangentOnPath") return JsArray.FromDoubles(new[] { tx, ty });
                        return JsArray.FromDoubles(new[] { -ty, tx });
                    });
                case "v": return ToArray(V);
                case "i": return ToArray(I);
                case "o": return ToArray(O);
                case "c": return JsValue.Bool(Closed);
                case "_length": return Count;
            }
            return JsValue.Undefined;
        }
    }

    /// <summary>Ken Perlin's improved noise (used by the AE noise() expression function).</summary>
    static class Perlin
    {
        static readonly int[] P = BuildPermutation();

        static int[] BuildPermutation()
        {
            int[] basePerm =
            {
                151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142, 8, 99, 37, 240, 21, 10,
                23, 190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32, 57, 177, 33, 88, 237, 149, 56, 87,
                174, 20, 125, 136, 171, 168, 68, 175, 74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122, 60, 211,
                133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102, 143, 54, 65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208,
                89, 18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173, 186, 3, 64, 52, 217, 226, 250, 124, 123, 5,
                202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17, 182, 189, 28, 42, 223, 183, 170, 213, 119,
                248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9, 129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232,
                178, 185, 112, 104, 218, 246, 97, 228, 251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249,
                14, 239, 107, 49, 192, 214, 31, 181, 199, 106, 157, 184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205,
                93, 222, 114, 67, 29, 24, 72, 243, 141, 128, 195, 78, 66, 215, 61, 156, 180,
            };
            var p = new int[512];
            for (int i = 0; i < 512; i++) p[i] = basePerm[i & 255];
            return p;
        }

        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        static double Lerp(double t, double a, double b) => a + t * (b - a);

        static double Grad(int hash, double x, double y, double z)
        {
            int h = hash & 15;
            double u = h < 8 ? x : y;
            double v = h < 4 ? y : h == 12 || h == 14 ? x : z;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        public static double Noise(double x, double y, double z)
        {
            int X = (int)Math.Floor(x) & 255, Y = (int)Math.Floor(y) & 255, Z = (int)Math.Floor(z) & 255;
            x -= Math.Floor(x);
            y -= Math.Floor(y);
            z -= Math.Floor(z);
            double u = Fade(x), v = Fade(y), w = Fade(z);
            int A = P[X] + Y, AA = P[A] + Z, AB = P[A + 1] + Z, B = P[X + 1] + Y, BA = P[B] + Z, BB = P[B + 1] + Z;
            return Lerp(w, Lerp(v, Lerp(u, Grad(P[AA], x, y, z), Grad(P[BA], x - 1, y, z)),
                                   Lerp(u, Grad(P[AB], x, y - 1, z), Grad(P[BB], x - 1, y - 1, z))),
                           Lerp(v, Lerp(u, Grad(P[AA + 1], x, y, z - 1), Grad(P[BA + 1], x - 1, y, z - 1)),
                                   Lerp(u, Grad(P[AB + 1], x, y - 1, z - 1), Grad(P[BB + 1], x - 1, y - 1, z - 1))));
        }
    }
}
