using System;
using System.Collections.Generic;
using Mynell.Lottie.Json;
using Mynell.Lottie.Model;

namespace Mynell.Lottie.Compiler
{
    enum PropHint : byte
    {
        Scalar,
        Vector,
        VectorNoSpatial,
    }

    public sealed partial class LottieCompiler
    {
        // ------------------------------------------------------------------ generic properties

        int AddStatic(float[] values, PropFlags flags = PropFlags.None)
        {
            int off = _f.Count;
            _f.AddRange(values);
            _props.Add(new PropDesc { Offset = off, KeyCount = 0, Dim = (ushort)values.Length, Flags = flags });
            return _props.Count - 1;
        }

        static float[] ReadValue(JsonValue v)
        {
            if (v.IsNumber) return new[] { v.AsFloat() };
            if (v.IsArray) return v.ToFloatArray();
            return null;
        }

        static float[] Fit(float[] v, int dim)
        {
            if (v == null) return new float[dim];
            if (v.Length == dim) return v;
            var r = new float[dim];
            Array.Copy(v, r, Math.Min(dim, v.Length));
            return r;
        }

        /// <summary>
        /// Compiles a Lottie animatable property ({a, k} object). Static vs keyframed is decided from
        /// the structure of 'k' exactly like lottie-web's PropertyFactory.getProp.
        /// </summary>
        int CompileProp(JsonValue p, PropHint hint, float scale = 1f)
        {
            int r = CompilePropCore(p, hint, scale);
            if (r >= 0 && !p.IsMissing) _propByJson[p.NodeId] = r;
            return r;
        }

        int CompilePropCore(JsonValue p, PropHint hint, float scale)
        {
            if (p.IsMissing || p.Type == JsonType.Null) return -1;
            JsonValue k = p.IsObject ? p["k"] : p;
            if (p.IsObject && p["x"].IsString) _expressionCount++;
            if (k.IsMissing || k.Type == JsonType.Null) return -1;
            if (k.IsNumber)
                return AddStatic(new[] { k.AsFloat() * scale });
            if (!k.IsArray || k.Count == 0) return -1;
            var first = k[0];
            if (first.IsNumber)
            {
                var v = k.ToFloatArray();
                if (scale != 1f) for (int i = 0; i < v.Length; i++) v[i] *= scale;
                return AddStatic(v);
            }
            if (!first.IsObject) return -1;
            return CompileKeyframes(k, hint, scale);
        }

        int CompileColorProp(JsonValue p)
        {
            // lottie-web checkColors: animations older than 4.1.9 store 0-255 colors.
            float scale = OlderThan(4, 1, 9) ? 1f / 255f : 1f;
            return CompileProp(p, PropHint.VectorNoSpatial, scale);
        }

        int CompileKeyframes(JsonValue arr, PropHint hint, float scale)
        {
            int n = arr.Count;
            var src = new List<JsonValue>(n);
            foreach (var kf in arr) src.Add(kf);

            // Dimension: first keyframe providing a start value.
            int dim = 0;
            foreach (var kf in src)
            {
                var s = ReadValue(kf["s"]);
                if (s != null) { dim = s.Length; break; }
            }
            if (dim == 0)
            {
                foreach (var kf in src)
                {
                    var e = ReadValue(kf["e"]);
                    if (e != null) { dim = e.Length; break; }
                }
            }
            if (dim == 0) return -1;

            // Start values (missing trailing 's' falls back to the previous keyframe's 'e').
            var starts = new float[n][];
            var ends = new float[n][];
            for (int i = 0; i < n; i++)
            {
                var s = ReadValue(src[i]["s"]);
                var e = ReadValue(src[i]["e"]);
                starts[i] = s != null ? Fit(s, dim) : null;
                ends[i] = e != null ? Fit(e, dim) : null;
            }
            for (int i = 0; i < n; i++)
            {
                if (starts[i] != null) continue;
                if (i > 0 && ends[i - 1] != null) starts[i] = ends[i - 1];
                else if (i > 0) starts[i] = starts[i - 1];
                else starts[i] = ends[i] ?? new float[dim];
            }
            if (scale != 1f)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int d = 0; d < dim; d++) starts[i][d] *= scale;
                    if (ends[i] != null && !ReferenceEquals(ends[i], starts[i]))
                        for (int d = 0; d < dim; d++) ends[i][d] *= scale;
                }
            }

            int keyFirst = _keys.Count;
            var startOffsets = new int[n];
            for (int i = 0; i < n; i++)
            {
                startOffsets[i] = _f.Count;
                _f.AddRange(starts[i]);
            }

            bool anySpatial = false;
            for (int i = 0; i < n; i++)
            {
                var kf = src[i];
                var key = new KeyframeData
                {
                    Time = kf["t"].AsFloat(0),
                    Start = startOffsets[i],
                    End = -1,
                    Ease = -1,
                    Spatial = -1,
                    Table = -1,
                    Hold = (byte)(kf["h"].AsInt(0) == 1 ? 1 : 0),
                };
                // End value: next keyframe's 's', else this keyframe's legacy 'e'.
                bool nextHasS = i + 1 < n && ReadValue(src[i + 1]["s"]) != null;
                if (i + 1 < n)
                {
                    if (nextHasS) key.End = startOffsets[i + 1];
                    else if (ends[i] != null) { key.End = _f.Count; _f.AddRange(ends[i]); }
                    else key.End = startOffsets[i + 1];
                }
                else if (ends[i] != null)
                {
                    key.End = _f.Count;
                    _f.AddRange(ends[i]);
                }

                // Easing.
                var o = kf["o"];
                var ii = kf["i"];
                var ox = o["x"];
                if (ox.IsArray && ox.Count > 0)
                {
                    key.EaseDims = (byte)Math.Min(dim, 255);
                    key.Ease = _f.Count;
                    for (int d = 0; d < key.EaseDims; d++)
                    {
                        _f.Add(ArrAt(o["x"], d));
                        _f.Add(ArrAt(o["y"], d));
                        _f.Add(ArrAt(ii["x"], d));
                        _f.Add(ArrAt(ii["y"], d));
                    }
                }
                else if (ox.IsNumber)
                {
                    key.EaseDims = 1;
                    key.Ease = _f.Count;
                    _f.Add(ox.AsFloat());
                    _f.Add(o["y"].AsFloat());
                    _f.Add(ii["x"].AsFloat());
                    _f.Add(ii["y"].AsFloat());
                }

                // Spatial tangents (KeyframedMultidimensionalProperty semantics).
                if (hint == PropHint.Vector && dim >= 2 && key.Hold == 0 && key.End >= 0)
                {
                    var to = ReadValue(kf["to"]);
                    var ti = ReadValue(kf["ti"]);
                    if (to != null && ti != null && to.Length >= 2 && ti.Length >= 2)
                    {
                        var s = starts[i];
                        float[] e = nextHasS ? starts[i + 1] : ends[i] ?? starts[i + 1];
                        to = Fit(to, 3);
                        ti = Fit(ti, 3);
                        bool keep = true;
                        if (nextHasS)
                        {
                            if (dim == 2 && !(s[0] == e[0] && s[1] == e[1]) &&
                                PointOnLine2D(s[0], s[1], e[0], e[1], s[0] + to[0], s[1] + to[1]) &&
                                PointOnLine2D(s[0], s[1], e[0], e[1], e[0] + ti[0], e[1] + ti[1]))
                                keep = false;
                            else if (dim >= 3 && !(s[0] == e[0] && s[1] == e[1] && s[2] == e[2]) &&
                                     PointOnLine3D(s[0], s[1], s[2], e[0], e[1], e[2], s[0] + to[0], s[1] + to[1], s[2] + to[2]) &&
                                     PointOnLine3D(s[0], s[1], s[2], e[0], e[1], e[2], e[0] + ti[0], e[1] + ti[1], e[2] + ti[2]))
                                keep = false;
                            if (s[0] == e[0] && s[1] == e[1] && to[0] == 0 && to[1] == 0 && ti[0] == 0 && ti[1] == 0)
                            {
                                if (dim == 2 || (s[2] == e[2] && to[2] == 0 && ti[2] == 0)) keep = false;
                            }
                        }
                        if (keep)
                        {
                            key.Spatial = _f.Count;
                            _f.Add(dim);
                            _f.Add(to[0]); _f.Add(to[1]); _f.Add(to[2]);
                            _f.Add(ti[0]); _f.Add(ti[1]); _f.Add(ti[2]);
                            anySpatial = true;
                        }
                    }
                }
                _keys.Add(key);
            }

            _props.Add(new PropDesc
            {
                Offset = keyFirst,
                KeyCount = n,
                Dim = (ushort)dim,
                Flags = anySpatial ? PropFlags.Spatial : PropFlags.None,
            });
            return _props.Count - 1;
        }

        static float ArrAt(JsonValue a, int i)
        {
            if (a.IsNumber) return a.AsFloat();
            if (!a.IsArray || a.Count == 0) return 0;
            var v = a[i];
            return v.IsMissing ? a[0].AsFloat() : v.AsFloat();
        }

        static bool PointOnLine2D(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            double det1 = x1 * y2 + y1 * x3 + x2 * y3 - x3 * y2 - y3 * x1 - x2 * y1;
            return det1 > -0.001 && det1 < 0.001;
        }

        static bool PointOnLine3D(double x1, double y1, double z1, double x2, double y2, double z2, double x3, double y3, double z3)
        {
            if (z1 == 0 && z2 == 0 && z3 == 0) return PointOnLine2D(x1, y1, x2, y2, x3, y3);
            double dist1 = Math.Sqrt(Math.Pow(x2 - x1, 2) + Math.Pow(y2 - y1, 2) + Math.Pow(z2 - z1, 2));
            double dist2 = Math.Sqrt(Math.Pow(x3 - x1, 2) + Math.Pow(y3 - y1, 2) + Math.Pow(z3 - z1, 2));
            double dist3 = Math.Sqrt(Math.Pow(x3 - x2, 2) + Math.Pow(y3 - y2, 2) + Math.Pow(z3 - z2, 2));
            double diff;
            if (dist1 > dist2) diff = dist1 > dist3 ? dist1 - dist2 - dist3 : dist3 - dist2 - dist1;
            else if (dist3 > dist2) diff = dist3 - dist2 - dist1;
            else diff = dist2 - dist1 - dist3;
            return diff > -0.0001 && diff < 0.0001;
        }

        // ------------------------------------------------------------------ bezier paths

        /// <summary>
        /// Path layout in the float pool (Dim = 6 * N): per vertex v.x, v.y, in.x, in.y, out.x, out.y,
        /// with tangents converted to absolute coordinates (lottie-web convertPathsToAbsoluteValues).
        /// </summary>
        int CompilePathProp(JsonValue p, bool? closedOverride)
        {
            int r = CompilePathPropCore(p, closedOverride);
            if (r >= 0 && !p.IsMissing) _propByJson[p.NodeId] = r;
            return r;
        }

        int CompilePathPropCore(JsonValue p, bool? closedOverride)
        {
            if (p.IsMissing) return -1;
            JsonValue k = p.IsObject && p.Has("k") ? p["k"] : p;
            if (p.IsObject && p["x"].IsString) _expressionCount++;
            if (k.IsObject)
            {
                var path = ReadPath(k, out bool closed, -1);
                if (closedOverride.HasValue) closed = closedOverride.Value;
                return AddStatic(path, PropFlags.Path | (closed ? PropFlags.Closed : 0));
            }
            if (!k.IsArray || k.Count == 0) return -1;
            var first = k[0];
            if (first.IsObject && first.Has("v") && !first.Has("t"))
            {
                var path = ReadPath(first, out bool closed, -1);
                if (closedOverride.HasValue) closed = closedOverride.Value;
                return AddStatic(path, PropFlags.Path | (closed ? PropFlags.Closed : 0));
            }

            // Keyframed path.
            int n = k.Count;
            var src = new List<JsonValue>(n);
            foreach (var kf in k) src.Add(kf);
            int count = -1;
            bool isClosed = false;
            foreach (var kf in src)
            {
                var s = PathObj(kf["s"]);
                if (!s.IsMissing)
                {
                    count = s["v"].Count;
                    isClosed = s["c"].AsBool(false);
                    break;
                }
            }
            if (count < 0) return -1;
            if (closedOverride.HasValue) isClosed = closedOverride.Value;
            int dim = count * 6;

            var starts = new float[n][];
            var ends = new float[n][];
            for (int i = 0; i < n; i++)
            {
                var s = PathObj(src[i]["s"]);
                var e = PathObj(src[i]["e"]);
                starts[i] = s.IsMissing ? null : ReadPath(s, out _, count);
                ends[i] = e.IsMissing ? null : ReadPath(e, out _, count);
            }
            for (int i = 0; i < n; i++)
            {
                if (starts[i] != null) continue;
                if (i > 0 && ends[i - 1] != null) starts[i] = ends[i - 1];
                else if (i > 0) starts[i] = starts[i - 1];
                else starts[i] = ends[i] ?? new float[dim];
            }

            int keyFirst = _keys.Count;
            var startOffsets = new int[n];
            for (int i = 0; i < n; i++)
            {
                startOffsets[i] = _f.Count;
                _f.AddRange(starts[i]);
            }
            for (int i = 0; i < n; i++)
            {
                var kf = src[i];
                var key = new KeyframeData
                {
                    Time = kf["t"].AsFloat(0),
                    Start = startOffsets[i],
                    End = -1,
                    Ease = -1,
                    Spatial = -1,
                    Table = -1,
                    Hold = (byte)(kf["h"].AsInt(0) == 1 ? 1 : 0),
                };
                bool nextHasS = i + 1 < n && !PathObj(src[i + 1]["s"]).IsMissing;
                if (i + 1 < n)
                {
                    if (nextHasS) key.End = startOffsets[i + 1];
                    else if (ends[i] != null) { key.End = _f.Count; _f.AddRange(ends[i]); }
                    else key.End = startOffsets[i + 1];
                }
                else if (ends[i] != null)
                {
                    key.End = _f.Count;
                    _f.AddRange(ends[i]);
                }
                var o = kf["o"];
                var ii = kf["i"];
                if (!o["x"].IsMissing)
                {
                    key.EaseDims = 1;
                    key.Ease = _f.Count;
                    _f.Add(ArrAt(o["x"], 0));
                    _f.Add(ArrAt(o["y"], 0));
                    _f.Add(ArrAt(ii["x"], 0));
                    _f.Add(ArrAt(ii["y"], 0));
                }
                _keys.Add(key);
            }
            _props.Add(new PropDesc
            {
                Offset = keyFirst,
                KeyCount = n,
                Dim = (ushort)Math.Min(dim, ushort.MaxValue),
                Flags = PropFlags.Path | (isClosed ? PropFlags.Closed : 0),
            });
            return _props.Count - 1;
        }

        static JsonValue PathObj(JsonValue s)
        {
            if (s.IsObject) return s;
            if (s.IsArray && s.Count > 0 && s[0].IsObject) return s[0];
            return default;
        }

        /// <summary>Reads a path object into the flat absolute layout (optionally forcing a vertex count).</summary>
        static float[] ReadPath(JsonValue path, out bool closed, int forceCount)
        {
            closed = path["c"].AsBool(false);
            var v = path["v"];
            var iT = path["i"];
            var oT = path["o"];
            int count = forceCount >= 0 ? forceCount : v.Count;
            int available = v.Count;
            var r = new float[count * 6];
            if (available == 0) return r;
            var vl = new List<JsonValue>(available);
            var il = new List<JsonValue>(available);
            var ol = new List<JsonValue>(available);
            foreach (var x in v) vl.Add(x);
            foreach (var x in iT) il.Add(x);
            foreach (var x in oT) ol.Add(x);
            for (int j = 0; j < count; j++)
            {
                int src = Math.Min(j, available - 1);
                float vx = vl[src][0].AsFloat(), vy = vl[src][1].AsFloat();
                float ix = src < il.Count ? il[src][0].AsFloat() : 0, iy = src < il.Count ? il[src][1].AsFloat() : 0;
                float ox = src < ol.Count ? ol[src][0].AsFloat() : 0, oy = src < ol.Count ? ol[src][1].AsFloat() : 0;
                int b = j * 6;
                r[b + 0] = vx;
                r[b + 1] = vy;
                r[b + 2] = vx + ix;
                r[b + 3] = vy + iy;
                r[b + 4] = vx + ox;
                r[b + 5] = vy + oy;
            }
            return r;
        }

        /// <summary>Maximum value a property takes (static value or any keyframe start/end). Used for repeater copies.</summary>
        internal float MaxScalar(int prop, float fallback)
        {
            if (prop < 0) return fallback;
            var p = _props[prop];
            if (p.KeyCount == 0) return _f[p.Offset];
            float m = float.MinValue;
            for (int i = 0; i < p.KeyCount; i++)
            {
                var k = _keys[p.Offset + i];
                m = Math.Max(m, _f[k.Start]);
                if (k.End >= 0) m = Math.Max(m, _f[k.End]);
            }
            return m == float.MinValue ? fallback : m;
        }
    }
}
