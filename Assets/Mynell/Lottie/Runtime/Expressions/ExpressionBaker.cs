using System;
using System.Collections.Generic;
using System.Globalization;
using Mynell.Lottie.Eval;
using Mynell.Lottie.Json;
using Mynell.Lottie.Model;
using Mynell.Lottie.Scene;
using Unity.Mathematics;

namespace Mynell.Lottie.Expressions
{
    /// <summary>
    /// Evaluates Bodymovin / After Effects expressions with a small JavaScript interpreter and bakes the results into
    /// linear keyframes (one sample per frame, redundant samples removed), so the runtime stays Burst-only.
    /// Semantics follow lottie-web's expression plugin; where lottie-web deviates from After Effects in ways that are
    /// clearly unintended (non-deterministic wiggle, loopIn with a late first key) After Effects behaviour is used.
    /// </summary>
    public sealed unsafe partial class ExpressionBaker : IDisposable
    {
        const int MaxSamples = 20000;

        readonly LottieData _data;
        readonly Dictionary<int, int> _propByJson;
        readonly List<string> _warnings;
        readonly float _fr;
        readonly JsInterpreter _it = new JsInterpreter();
        readonly List<CompHost> _comps = new List<CompHost>();
        readonly LayerHost[] _layers;
        readonly Dictionary<int, Site> _sites = new Dictionary<int, Site>();
        readonly List<Frame> _stack = new List<Frame>();
        readonly Dictionary<string, List<Stmt>> _programCache = new Dictionary<string, List<Stmt>>();
        LottieNativeData _native;
        PropReader _p;
        SceneBuilder _scene;
        float[] _buf = new float[64];
        int _errors;
        List<float> _outFloats;
        List<KeyframeData> _outKeys;
        readonly Dictionary<(int, double), JsValue> _memo = new Dictionary<(int, double), JsValue>();

        sealed class Site
        {
            public int Prop;
            public JsonValue Json;
            public LayerHost Layer;
            public string Code;
            public List<Stmt> Program;
            public bool Failed;
            public bool Busy;
            public uint Seed;
            public Func<JsObject> Container;
        }

        struct Frame
        {
            public Site Site;
            public double Time;      // seconds (comp time)
            public JsValue Value;    // pre-expression value
            public PropRef This;
            public double TextIndex, TextTotal;  // text expression selectors (1-based index), 0 otherwise
        }

        sealed class CompHost
        {
            public int Index;
            public JsonValue Json;
            public string Name;
            public float Width, Height;
            public List<LayerHost> Layers = new List<LayerHost>();
        }

        sealed class LayerHost
        {
            public int Index;        // global layer index
            public int IndexInComp;  // 1-based (AE layer index)
            public CompHost Comp;
            public JsonValue Json;
            public string Name;
            public LayerHost Parent;
        }

        ExpressionBaker(LottieData data, List<JsonValue> compJson, Dictionary<int, int> propByJson, List<string> warnings)
        {
            _data = data;
            _propByJson = propByJson;
            _warnings = warnings;
            _fr = data.FrameRate > 0 ? data.FrameRate : 30;
            _layers = new LayerHost[data.Layers.Length];
            for (int c = 0; c < compJson.Count && c < data.Comps.Length; c++)
            {
                var cj = compJson[c];
                var ch = new CompHost
                {
                    Index = c,
                    Json = cj,
                    Name = cj["nm"].AsString(c == 0 ? data.Name : cj["id"].AsString("")),
                    Width = data.Comps[c].Width,
                    Height = data.Comps[c].Height,
                };
                var layersJson = cj["layers"];
                var cd = data.Comps[c];
                for (int i = 0; i < cd.LayerCount; i++)
                {
                    var lh = new LayerHost
                    {
                        Index = cd.FirstLayer + i,
                        IndexInComp = i + 1,
                        Comp = ch,
                        Json = layersJson[i],
                        Name = layersJson[i]["nm"].AsString(""),
                    };
                    _layers[lh.Index] = lh;
                    ch.Layers.Add(lh);
                }
                _comps.Add(ch);
            }
            foreach (var l in _layers)
            {
                if (l == null) continue;
                int p = data.Layers[l.Index].Parent;
                if (p >= 0 && p < _layers.Length) l.Parent = _layers[p];
            }
            _it.GlobalResolver = Resolve;
            _it.StepLimit = 200_000;
        }

        /// <summary>Bakes every expression of <paramref name="data"/> in place. Returns the number of baked properties.</summary>
        internal static int Bake(LottieData data, List<JsonValue> compJson, Dictionary<int, int> propByJson, List<string> warnings,
            List<(int selector, int layer, string code, int total)> textSelectors = null)
        {
            using var baker = new ExpressionBaker(data, compJson, propByJson, warnings);
            int n = baker.Run();
            if (textSelectors != null && textSelectors.Count > 0) n += baker.BakeTextSelectors(textSelectors);
            return n;
        }

        /// <summary>
        /// lottie-web TextExpressionSelectorProp: evaluates each expression selector for every frame of its layer and every
        /// letter index (textIndex 1..textTotal, selectorValue 100) into a table of amounts (percent) in TextFloats.
        /// </summary>
        int BakeTextSelectors(List<(int selector, int layer, string code, int total)> jobs)
        {
            if (!_native.IsCreated)
            {
                _native = LottieNativeData.Create(_data);
                _p = PropReader.Create(_native);
                _scene = SceneBuilder.Create(_native, default, default);
            }
            var floats = new List<float>(_data.TextFloats);
            var selectors = _data.TextSelectors;
            int baked = 0;
            foreach (var job in jobs)
            {
                if (job.layer < 0 || job.layer >= _layers.Length || _layers[job.layer] == null || job.total <= 0) continue;
                var layer = _layers[job.layer];
                var site = new Site { Prop = -1, Json = default, Layer = layer, Code = job.code, Seed = (uint)(job.selector * 2654435761u) };
                try { site.Program = JsParser.Parse(job.code); }
                catch (Exception e) { Fail(site, e); continue; }
                var L = _data.Layers[job.layer];
                double f0 = Math.Ceiling(Math.Max(L.InPoint, -1e6));
                double f1 = Math.Floor(Math.Min(L.OutPoint, f0 + MaxSamples));
                if (layer.Comp.Index == 0)
                {
                    f0 = Math.Max(f0, Math.Floor(_data.InPoint));
                    f1 = Math.Min(f1, Math.Ceiling(_data.OutPoint));
                }
                if (f1 < f0) f1 = f0;
                int frames = (int)(f1 - f0) + 1;
                if ((long)frames * job.total > 2_000_000) frames = Math.Max(1, 2_000_000 / job.total);
                int first = floats.Count;
                for (int f = 0; f < frames && !site.Failed; f++)
                for (int i = 0; i < job.total; i++)
                    floats.Add(EvaluateTextSelector(site, f0 + f, i + 1, job.total));
                if (site.Failed)
                {
                    floats.RemoveRange(first, floats.Count - first);
                    continue;
                }
                var sd = selectors[job.selector];
                sd.ExprFirst = first;
                sd.ExprFrames = frames;
                sd.ExprTotal = job.total;
                sd.ExprT0 = (float)f0;
                selectors[job.selector] = sd;
                baked++;
            }
            _data.TextFloats = floats.ToArray();
            return baked;
        }

        float EvaluateTextSelector(Site site, double frame, int index, int total)
        {
            if (site.Failed) return 100;
            _stack.Add(new Frame { Site = site, Time = frame / _fr, Value = 100, TextIndex = index, TextTotal = total });
            try
            {
                _it.Random.Seed(site.Seed + frame / _fr + index * 0.001);
                var vars = _it.Run(site.Program);
                JsValue r = vars.TryGetValue("$bm_rt", out var rt) && !rt.IsUndefined ? rt : _it.LastExpressionValue;
                r = JsOps.Prim(_it, r);
                if (r.Obj is JsArray arr) r = arr.Items.Count > 0 ? arr.Items[0] : (JsValue)0;
                double d = JsOps.ToNumber(_it, r);
                return double.IsNaN(d) || double.IsInfinity(d) ? 100 : (float)d;
            }
            catch (Exception e) when (e is JsRuntimeError || e is JsThrow || e is JsSyntaxError || e is InvalidCastException ||
                                      e is IndexOutOfRangeException || e is ArgumentException || e is NullReferenceException)
            {
                Fail(site, e);
                return 100;
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
        }

        public void Dispose()
        {
            if (_native.IsCreated) _native.Dispose();
        }

        /// <summary>Debug helper: evaluates <paramref name="code"/> as an expression on layer <paramref name="layerName"/>'s rotation at a frame.</summary>
        public static string DebugEvaluate(byte[] json, string layerName, string code, float frame)
        {
            var doc = JsonDocument.Parse(json);
            var compiler = Compiler.LottieCompiler.CreateForDebug(doc, out var data, out var compJson, out var propByJson);
            using var b = new ExpressionBaker(data, compJson, propByJson, new List<string>());
            b._native = LottieNativeData.Create(data);
            b._p = PropReader.Create(b._native);
            b._scene = SceneBuilder.Create(b._native, default, default);
            LayerHost layer = null;
            foreach (var l in b._layers) if (l != null && l.Name == layerName) { layer = l; break; }
            if (layer == null) return "layer not found";
            int prop = propByJson.TryGetValue(layer.Json["ks"]["r"].NodeId, out int pr) ? pr : 0;
            var site = new Site { Prop = prop, Json = layer.Json["ks"]["r"], Layer = layer, Code = code, Seed = 1 };
            var raw = b.RawValue(prop, frame);
            try
            {
                site.Program = JsParser.Parse(code);
                b.Fail(site, new Exception("dummy"));
                site.Failed = false;
                b._errors = 0;
                var r = b.EvaluateSite(site, frame, raw);
                return JsOps.ToStr(b._it, r) + (site.Failed ? " (failed: " + string.Join(" | ", b._warnings) + ")" : "");
            }
            catch (Exception e)
            {
                return "EXCEPTION " + e;
            }
        }

        // ================================================================== driver

        int Run()
        {
            foreach (var l in _layers)
                if (l != null) FindSites(l.Json, l, 0);
            if (_sites.Count == 0) return 0;

            _native = LottieNativeData.Create(_data);
            _p = PropReader.Create(_native);
            _scene = SceneBuilder.Create(_native, default, default);

            var results = new List<(int prop, float[] times, float[][] values)>();
            foreach (var site in _sites.Values)
            {
                if (site.Failed) continue;
                var L = _data.Layers[site.Layer.Index];
                double f0 = Math.Ceiling(Math.Max(L.InPoint, -1e6));
                double f1 = Math.Floor(Math.Min(L.OutPoint, f0 + MaxSamples));
                if (site.Layer.Comp.Index == 0)
                {
                    f0 = Math.Max(f0, Math.Floor(_data.InPoint));
                    f1 = Math.Min(f1, Math.Ceiling(_data.OutPoint));
                }
                if (f1 < f0) f1 = f0;
                int n = (int)(f1 - f0) + 1;
                var times = new float[n];
                var values = new float[n][];
                bool ok = true;
                for (int s = 0; s < n && ok; s++)
                {
                    double frame = f0 + s;
                    times[s] = (float)frame;
                    values[s] = EvaluateSiteFloats(site, frame);
                    if (site.Failed) ok = false;
                }
                if (ok) results.Add((site.Prop, times, values));
            }
            _outFloats = new List<float>(_data.Floats);
            _outKeys = new List<KeyframeData>(_data.Keys);
            foreach (var r in results) WriteBaked(r.prop, r.times, r.values);
            _data.Floats = _outFloats.ToArray();
            _data.Keys = _outKeys.ToArray();
            if (_errors > 0) _warnings.Add($"{_errors} expression(s) could not be evaluated and use their keyframed values.");
            return results.Count;
        }

        void FindSites(JsonValue v, LayerHost layer, int depth) => FindSites(v, layer, depth, () => new LayerRef(this, layer));

        /// <summary>Registers every expression property, tracking its containing property group.</summary>
        void FindSites(JsonValue v, LayerHost layer, int depth, Func<JsObject> container)
        {
            if (depth > 64) return;
            if (v.IsObject)
            {
                var x = v["x"];
                if (x.IsString && (v.Has("k") || v.Has("a")))
                {
                    if (_propByJson.TryGetValue(v.NodeId, out int prop) && !_sites.ContainsKey(prop))
                    {
                        _sites[prop] = new Site
                        {
                            Prop = prop,
                            Json = v,
                            Layer = layer,
                            Code = x.AsString(),
                            Seed = (uint)(prop * 2654435761u) ^ (uint)(layer.Index * 40503u),
                            Container = container,
                        };
                    }
                }
                foreach (var entry in EnumerateMembers(v))
                {
                    var child = entry.Value;
                    if (!child.IsObject && !child.IsArray) continue;
                    var next = container;
                    switch (entry.Key)
                    {
                        case "ks" when depth == 0:
                        {
                            var ks = child;
                            next = () => new TransformRef(this, layer, ks, new LayerRef(this, layer));
                            break;
                        }
                        case "ef" when depth == 0:
                            foreach (var e in child)
                            {
                                var eff = e;
                                FindSites(e, layer, depth + 2, () => new EffectRef(this, layer, eff));
                            }
                            continue;
                        case "masksProperties" when depth == 0:
                            foreach (var m in child)
                            {
                                var mask = m;
                                FindSites(m, layer, depth + 2, () => MaskObject(layer, mask));
                            }
                            continue;
                        case "shapes" when depth == 0:
                        case "it":
                            foreach (var item in child)
                            {
                                var it = item;
                                string ty = item["ty"].AsString("");
                                Func<JsObject> c = ty == "gr" || ty == "tr"
                                    ? container
                                    : () => ShapeItem(layer, it, null).Obj;
                                if (ty == "tr") c = () => new TransformRef(this, layer, it, null);
                                FindSites(item, layer, depth + 2, c);
                            }
                            continue;
                    }
                    FindSites(child, layer, depth + 1, next);
                }
            }
            else if (v.IsArray)
            {
                foreach (var child in v)
                    if (child.IsObject || child.IsArray) FindSites(child, layer, depth + 1, container);
            }
        }

        static IEnumerable<KeyValuePair<string, JsonValue>> EnumerateMembers(JsonValue obj)
        {
            foreach (var child in obj) yield return new KeyValuePair<string, JsonValue>(child.Key ?? "", child);
        }

        // ================================================================== evaluation

        float[] EvaluateSiteFloats(Site site, double frame)
        {
            var raw = RawValue(site.Prop, frame);
            var result = ValueAt(site.Prop, frame);
            return ToFloats(site.Prop, result, raw);
        }

        /// <summary>Runs a property's expression at <paramref name="frame"/>; returns the raw value on failure / recursion.</summary>
        JsValue EvaluateSite(Site site, double frame, JsValue raw)
        {
            if (site.Failed || site.Busy) return raw;
            if (site.Program == null)
            {
                try
                {
                    if (!_programCache.TryGetValue(site.Code, out site.Program))
                    {
                        site.Program = JsParser.Parse(site.Code);
                        _programCache[site.Code] = site.Program;
                    }
                }
                catch (Exception e)
                {
                    Fail(site, e);
                    return raw;
                }
            }
            site.Busy = true;
            _stack.Add(new Frame
            {
                Site = site,
                Time = frame / _fr,
                Value = raw,
                This = new PropRef(this, site.Prop, site.Layer, site.Json, site.Container?.Invoke()),
            });
            try
            {
                _it.Random.Seed(site.Seed + frame / _fr);
                var vars = _it.Run(site.Program);
                if (vars.TryGetValue("$bm_rt", out var rt) && !rt.IsUndefined) return Unwrap(rt);
                if (_it.LastExpressionValue.Type != JsType.Undefined) return Unwrap(_it.LastExpressionValue);
                return raw;
            }
            catch (Exception e) when (e is JsRuntimeError || e is JsThrow || e is JsSyntaxError || e is InvalidCastException ||
                                      e is IndexOutOfRangeException || e is ArgumentException || e is NullReferenceException)
            {
                Fail(site, e);
                return raw;
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
                site.Busy = false;
            }
        }

        void Fail(Site site, Exception e)
        {
            if (site.Failed) return;
            site.Failed = true;
            _errors++;
            if (_errors <= 8)
            {
                string code = site.Code.Replace("\n", " ");
                if (code.Length > 120) code = code.Substring(0, 120) + "…";
                _warnings.Add($"Expression on layer '{site.Layer.Name}' failed ({e.Message}): {code}");
            }
        }

        JsValue Unwrap(JsValue v) => v.Obj is PropRef || (v.Obj != null && !(v.Obj is JsArray) && !(v.Obj is PathValue)) ? JsOps.Prim(_it, v) : v;

        Frame Top => _stack[_stack.Count - 1];
        double CurrentFrame => Top.Time * _fr;

        /// <summary>Property value (with its expression) at a frame.</summary>
        JsValue ValueAt(int prop, double frame)
        {
            if (!_sites.TryGetValue(prop, out var site)) return RawValue(prop, frame);
            // Expressions are pure functions of time: memoize (prop, frame) so linked rigs don't re-run whole chains.
            var key = (prop, frame);
            if (_memo.TryGetValue(key, out var cached)) return CloneValue(cached);
            var raw = RawValue(prop, frame);
            var v = EvaluateSite(site, frame, raw);
            if (!site.Busy) _memo[key] = CloneValue(v);
            return v;
        }

        static JsValue CloneValue(JsValue v) => v.Obj is JsArray a ? a.Clone() : v;

        /// <summary>Pre-expression (keyframed / static) value.</summary>
        JsValue RawValue(int prop, double frame)
        {
            if (prop < 0) return 0;
            var pd = _data.Props[prop];
            int dim = pd.Dim;
            if (_buf.Length < dim) _buf = new float[dim];
            fixed (float* b = _buf) _p.Eval(prop, (float)frame, b, dim);
            if ((pd.Flags & PropFlags.Path) != 0) return PathValue.FromLayout(_buf, dim / 6, (pd.Flags & PropFlags.Closed) != 0);
            if (dim == 1) return _buf[0];
            return JsArray.FromFloats(_buf, 0, dim);
        }

        float[] ToFloats(int prop, JsValue v, JsValue raw)
        {
            var pd = _data.Props[prop];
            int dim = pd.Dim;
            if ((pd.Flags & PropFlags.Path) != 0)
            {
                if (v.Obj is PathValue pv) return pv.ToLayout();
                if (raw.Obj is PathValue rp) return rp.ToLayout();
                return new float[dim];
            }
            var r = new float[dim];
            var rawArr = raw.Obj as JsArray;
            for (int i = 0; i < dim; i++) r[i] = rawArr != null ? (float)JsOps.ToNumber(_it, rawArr.Items[i]) : dim == 1 ? (float)JsOps.ToNumber(_it, raw) : 0;
            v = JsOps.Prim(_it, v);
            if (v.Obj is JsArray arr)
            {
                for (int i = 0; i < dim && i < arr.Items.Count; i++)
                {
                    double d = JsOps.ToNumber(_it, arr.Items[i]);
                    if (!double.IsNaN(d) && !double.IsInfinity(d)) r[i] = (float)d;
                }
            }
            else if (v.IsNumber || v.Type == JsType.Boolean || v.IsString)
            {
                double d = JsOps.ToNumber(_it, v);
                if (!double.IsNaN(d) && !double.IsInfinity(d)) r[0] = (float)d;
            }
            return r;
        }

        // ================================================================== writing baked keyframes

        void WriteBaked(int prop, float[] times, float[][] values)
        {
            var pd = _data.Props[prop];
            bool path = (pd.Flags & PropFlags.Path) != 0;
            int dim = 0;
            foreach (var v in values) dim = Math.Max(dim, v.Length);
            if (path)
            {
                // Equalise vertex counts by repeating the last vertex (zero-length segments).
                for (int s = 0; s < values.Length; s++)
                {
                    var v = values[s];
                    if (v.Length == dim) continue;
                    var nv = new float[dim];
                    Array.Copy(v, nv, v.Length);
                    for (int k = v.Length; k < dim; k += 6)
                    {
                        int src = Math.Max(0, v.Length - 6);
                        for (int j = 0; j < 6; j++) nv[k + j] = v.Length >= 6 ? v[src + (j % 2 == 0 ? 0 : 1)] : 0;
                    }
                    values[s] = nv;
                }
            }

            // Drop samples reproduced by linear interpolation of their neighbours.
            var keep = new List<int> { 0 };
            for (int s = 1; s < values.Length - 1; s++)
            {
                int a = keep[keep.Count - 1];
                int b = s + 1;
                float t = (times[s] - times[a]) / (times[b] - times[a]);
                bool needed = false;
                for (int d = 0; d < dim && !needed; d++)
                {
                    float lin = values[a][d] + (values[b][d] - values[a][d]) * t;
                    float tol = 1e-4f * Math.Max(1f, Math.Abs(values[s][d]));
                    if (Math.Abs(lin - values[s][d]) > tol) needed = true;
                }
                if (needed) keep.Add(s);
            }
            if (values.Length > 1) keep.Add(values.Length - 1);

            var floats = _outFloats;
            var keys = _outKeys;
            bool constant = true;
            for (int k = 1; k < keep.Count && constant; k++)
                for (int d = 0; d < dim; d++)
                    if (values[keep[k]][d] != values[keep[0]][d]) { constant = false; break; }

            var nd = new PropDesc { Dim = (ushort)dim, Flags = pd.Flags & ~PropFlags.Spatial };
            if (constant)
            {
                nd.Offset = floats.Count;
                nd.KeyCount = 0;
                floats.AddRange(values[keep[0]]);
            }
            else
            {
                int first = keys.Count;
                int[] starts = new int[keep.Count];
                for (int k = 0; k < keep.Count; k++)
                {
                    starts[k] = floats.Count;
                    floats.AddRange(values[keep[k]]);
                }
                for (int k = 0; k < keep.Count; k++)
                {
                    keys.Add(new KeyframeData
                    {
                        Time = times[keep[k]],
                        Start = starts[k],
                        End = k + 1 < keep.Count ? starts[k + 1] : -1,
                        Ease = -1,
                        Spatial = -1,
                        Table = -1,
                    });
                }
                nd.Offset = first;
                nd.KeyCount = keep.Count;
            }
            _data.Props[prop] = nd;
        }

        // ================================================================== layer transforms

        /// <summary>Layer-to-composition matrix at a frame, honouring expressions on transform properties.</summary>
        Affine2 LayerMatrix(LayerHost layer, double frame)
        {
            // Auto-orient needs the renderer's velocity sampling: use its matrix (without expressions) then.
            for (var q = layer; q != null; q = q.Parent)
                if ((_data.Layers[q.Index].Flags & LayerFlags.AutoOrient) != 0) return _scene.LayerWorld(layer.Index, (float)frame);
            float4x4 m = LayerLocal(layer, frame);
            int guard = 0;
            for (var p = layer.Parent; p != null && guard++ < 64; p = p.Parent) m = math.mul(m, LayerLocal(p, frame));
            return LMat4.To2D(m);
        }

        float3 Vec3At(JsonValue prop, double frame, float3 def)
        {
            if (prop.IsMissing || !_propByJson.TryGetValue(prop.NodeId, out int idx)) return def;
            var v = Vec(ValueAt(idx, frame), 3);
            return new float3((float)v[0], (float)v[1], v.Length > 2 ? (float)v[2] : def.z);
        }

        float ScalarAt(JsonValue prop, double frame, float def)
        {
            if (prop.IsMissing || !_propByJson.TryGetValue(prop.NodeId, out int idx)) return def;
            return (float)Vec(ValueAt(idx, frame))[0];
        }

        float4x4 LayerLocal(LayerHost layer, double frame)
        {
            var ks = layer.Json["ks"];
            float4x4 m = float4x4.identity;
            if (!ks.IsObject) return m;
            float3 a = Vec3At(ks["a"], frame, float3.zero);
            m = LMat4.Translate(m, -a.x, -a.y, a.z);
            float3 s = Vec3At(ks["s"], frame, new float3(100, 100, 100)) * 0.01f;
            m = LMat4.Scale(m, s.x, s.y, s.z);
            if (ks.Has("sk"))
                m = LMat4.SkewFromAxis(m, -math.radians(ScalarAt(ks["sk"], frame, 0)), math.radians(ScalarAt(ks["sa"], frame, 0)));
            if (ks.Has("rx") || ks.Has("ry") || ks.Has("rz") || ks.Has("or"))
            {
                float rz = math.radians(ScalarAt(ks.Has("rz") ? ks["rz"] : ks["r"], frame, 0));
                float ry = math.radians(ScalarAt(ks["ry"], frame, 0));
                float rx = math.radians(ScalarAt(ks["rx"], frame, 0));
                float3 or = math.radians(Vec3At(ks["or"], frame, float3.zero));
                m = LMat4.RotateZ(m, -rz);
                m = LMat4.RotateY(m, ry);
                m = LMat4.RotateX(m, rx);
                m = LMat4.RotateZ(m, -or.z);
                m = LMat4.RotateY(m, or.y);
                m = LMat4.RotateX(m, or.x);
            }
            else m = LMat4.Rotate(m, -math.radians(ScalarAt(ks["r"], frame, 0)));
            var p = ks["p"];
            float3 pos;
            if (p["s"].AsBool()) pos = new float3(ScalarAt(p["x"], frame, 0), ScalarAt(p["y"], frame, 0), ScalarAt(p["z"], frame, 0));
            else pos = Vec3At(p, frame, float3.zero);
            m = LMat4.Translate(m, pos.x, pos.y, -pos.z);
            return m;
        }
    }
}
