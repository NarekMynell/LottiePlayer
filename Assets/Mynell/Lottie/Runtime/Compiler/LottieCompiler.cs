using System;
using System.Collections.Generic;
using System.Globalization;
using Mynell.Lottie.Json;
using Mynell.Lottie.Model;
using Unity.Mathematics;

namespace Mynell.Lottie.Compiler
{
    /// <summary>Options for <see cref="LottieCompiler.Compile(string, LottieCompileOptions)"/>.</summary>
    public sealed class LottieCompileOptions
    {
        /// <summary>Evaluate After Effects expressions and bake them into keyframes (default true).</summary>
        public bool BakeExpressions = true;
        /// <summary>Supplies font files for text whose glyphs are not embedded in the animation.</summary>
        public System.Func<LottieFontRequest, byte[]> FontResolver;
    }

    /// <summary>
    /// Compiles Lottie (Bodymovin) JSON into the flat <see cref="LottieData"/> model.
    /// Normalisation mirrors lottie-web's data manager so that evaluation matches the reference player.
    /// </summary>
    public sealed partial class LottieCompiler
    {
        readonly List<CompData> _comps = new List<CompData>();
        readonly List<LayerData> _layers = new List<LayerData>();
        readonly List<ShapeNode> _nodes = new List<ShapeNode>();
        readonly List<string> _nodeNames = new List<string>();
        readonly List<string> _layerNames = new List<string>();
        readonly List<int2> _layerShapeRoots = new List<int2>();
        int2 _currentShapeRoots;
        readonly List<PropDesc> _props = new List<PropDesc>();
        readonly List<KeyframeData> _keys = new List<KeyframeData>();
        readonly List<float> _f = new List<float>();
        readonly List<MaskData> _masks = new List<MaskData>();
        readonly List<DashData> _dashes = new List<DashData>();
        readonly List<ImageAsset> _images = new List<ImageAsset>();
        readonly List<EffectData> _effects = new List<EffectData>();
        readonly List<EffectParam> _effectParams = new List<EffectParam>();
        readonly List<string> _imageIds = new List<string>();
        readonly List<string> _imagePaths = new List<string>();
        readonly List<byte[]> _imageBytes = new List<byte[]>();
        readonly Dictionary<string, int> _compById = new Dictionary<string, int>();
        readonly Dictionary<string, int> _imageById = new Dictionary<string, int>();
        readonly HashSet<string> _unsupported = new HashSet<string>();
        readonly List<string> _warnings = new List<string>();
        readonly ShapeProgramBuilder _programs;
        int[] _version = { 5, 0, 0 };
        float _frameRate = 30;
        int _expressionCount;
        /// <summary>JSON property node -> compiled property (used by the expression baker).</summary>
        internal readonly Dictionary<int, int> _propByJson = new Dictionary<int, int>();
        readonly List<JsonValue> _compJson = new List<JsonValue>();

        LottieCompiler() { _programs = new ShapeProgramBuilder(this); }

        public static LottieData Compile(string json, LottieCompileOptions options = null) => Compile(JsonDocument.Parse(json), options);
        public static LottieData Compile(byte[] utf8Json, LottieCompileOptions options = null) => Compile(JsonDocument.Parse(utf8Json), options);

        /// <summary>Compiles a parsed document.</summary>
        public static LottieData Compile(JsonDocument doc, LottieCompileOptions options = null)
        {
            var c = new LottieCompiler
            {
                BakeExpressions = options?.BakeExpressions ?? true,
                FontResolver = options?.FontResolver,
            };
            return c.Run(doc.Root);
        }

        bool BakeExpressions = true;

        internal static LottieCompiler CreateForDebug(JsonDocument doc, out LottieData data, out List<JsonValue> compJson, out Dictionary<int, int> propByJson)
        {
            var c = new LottieCompiler { BakeExpressions = false };
            data = c.Run(doc.Root);
            compJson = c._compJson;
            propByJson = c._propByJson;
            return c;
        }

        // ------------------------------------------------------------------------------ root

        LottieData Run(JsonValue root)
        {
            if (!root.IsObject) throw new FormatException("Lottie JSON root must be an object.");
            var data = new LottieData();
            data.LottieVersion = root["v"].AsString("5.0.0");
            data.Name = root["nm"].AsString("");
            _version = ParseVersion(data.LottieVersion);
            data.FrameRate = _frameRate = root["fr"].AsFloat(30);
            if (_frameRate <= 0) _frameRate = data.FrameRate = 30;
            data.InPoint = root["ip"].AsFloat(0);
            data.OutPoint = root["op"].AsFloat(data.InPoint + 1);
            data.Width = root["w"].AsFloat(512);
            data.Height = root["h"].AsFloat(512);

            // Pass 1: register assets.
            var assets = root["assets"];
            var precomps = new List<JsonValue>();
            _comps.Add(default); // root composition = 0
            _compJson.Add(root);
            foreach (var a in assets)
            {
                string id = a["id"].AsString();
                if (a["layers"].IsArray)
                {
                    if (id != null && !_compById.ContainsKey(id)) _compById[id] = _comps.Count;
                    _comps.Add(default);
                    _compJson.Add(a);
                    precomps.Add(a);
                }
                else if (a.Has("p") || a.Has("w"))
                {
                    RegisterImage(a);
                }
            }

            // Fonts and exported glyphs (text layers).
            CompileFontsAndChars(root);

            // Pass 2: compile compositions (layers must be contiguous per composition).
            CompileComposition(0, root["layers"], data.Width, data.Height);
            for (int i = 0; i < precomps.Count; i++)
                CompileComposition(i + 1, precomps[i]["layers"], precomps[i]["w"].AsFloat(0), precomps[i]["h"].AsFloat(0));

            // Markers.
            var markers = new List<LottieMarker>();
            foreach (var m in root["markers"])
            {
                markers.Add(new LottieMarker
                {
                    Name = m["cm"].AsString(""),
                    Time = m["tm"].AsFloat(0),
                    Duration = m["dr"].AsFloat(0),
                });
            }


            data.Comps = _comps.ToArray();
            data.Layers = _layers.ToArray();
            data.Nodes = _nodes.ToArray();
            data.NodeNames = _nodeNames.ToArray();
            data.LayerNames = _layerNames.ToArray();
            data.LayerShapeRoots = _layerShapeRoots.ToArray();
            data.Props = _props.ToArray();
            data.Keys = _keys.ToArray();
            data.Floats = _f.ToArray();
            data.Masks = _masks.ToArray();
            data.Dashes = _dashes.ToArray();
            data.Images = _images.ToArray();
            data.Effects = _effects.ToArray();
            data.EffectParams = _effectParams.ToArray();
            data.ImageIds = _imageIds.ToArray();
            data.ImagePaths = _imagePaths.ToArray();
            data.ImageBytes = _imageBytes.ToArray();
            data.Markers = markers.ToArray();
            _programs.Export(data);
            ExportText(data);
            if (_expressionCount > 0 && BakeExpressions)
            {
                try
                {
                    int baked = Expressions.ExpressionBaker.Bake(data, _compJson, _propByJson, _warnings);
                    if (baked > 0) _warnings.Add($"{baked} of {_expressionCount} expression(s) evaluated and baked into keyframes.");
                }
                catch (Exception e)
                {
                    _warnings.Add($"Expressions could not be evaluated ({e.Message}); keyframed values are used.");
                }
            }
            else if (_expressionCount > 0)
                _warnings.Add($"{_expressionCount} expression(s) ignored (expression baking disabled).");
            data.Warnings.AddRange(_warnings);
            data.Unsupported.AddRange(_unsupported);
            return data;
        }

        internal void Unsupported(string feature) => _unsupported.Add(feature);

        static readonly string[] s_LayerStyleNames =
            { "Stroke", "Drop Shadow", "Inner Shadow", "Outer Glow", "Inner Glow", "Bevel and Emboss", "Satin", "Color Overlay", "Gradient Overlay" };

        /// <summary>Layer style properties, compiled in this order as effect parameters.</summary>
        internal static readonly string[] StyleKeys = { "c", "o", "a", "s", "d", "ch", "sr", "so", "r", "j", "bm", "no", "lc" };

        /// <summary>Layer styles ("sy") become effects after the layer's own effects (After Effects / Skottie order).</summary>
        void CompileStyles(JsonValue sy)
        {
            if (!sy.IsArray) return;
            foreach (var style in sy)
            {
                if (!style.IsObject) continue;
                int ty = style["ty"].AsInt(-1);
                string name = ty >= 0 && ty < s_LayerStyleNames.Length ? s_LayerStyleNames[ty] : $"type {ty}";
                var kind = (EffectKind)(100 + math.clamp(ty, 0, 100));
                if (kind != EffectKind.StyleDropShadow && kind != EffectKind.StyleInnerShadow && kind != EffectKind.StyleOuterGlow &&
                    kind != EffectKind.StyleInnerGlow && kind != EffectKind.StyleColorOverlay && kind != EffectKind.StyleStroke)
                {
                    Unsupported($"Layer style '{name}'");
                    continue;
                }
                var ed = new EffectData { Kind = kind, Enabled = 1, ParamFirst = _effectParams.Count };
                foreach (var key in StyleKeys)
                {
                    var v = style[key];
                    int prop = v.IsMissing ? -1 : CompileProp(v, key == "c" ? PropHint.Vector : PropHint.Scalar);
                    _effectParams.Add(new EffectParam { Type = (byte)(key == "c" ? 2 : 0), Prop = prop });
                }
                ed.ParamCount = _effectParams.Count - ed.ParamFirst;
                _effects.Add(ed);
            }
        }

        internal void Warn(string warning) { if (_warnings.Count < 64) _warnings.Add(warning); }

        static int[] ParseVersion(string v)
        {
            var r = new[] { 100, 100, 100 };
            if (string.IsNullOrEmpty(v)) return r;
            var parts = v.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) r[i] = n;
            return r;
        }

        /// <summary>lottie-web checkVersion: true when the animation version is older than <paramref name="minimum"/>.</summary>
        bool OlderThan(int a, int b, int c)
        {
            if (a > _version[0]) return true;
            if (_version[0] > a) return false;
            if (b > _version[1]) return true;
            if (_version[1] > b) return false;
            if (c > _version[2]) return true;
            return false;
        }

        void RegisterImage(JsonValue a)
        {
            string id = a["id"].AsString("");
            if (_imageById.ContainsKey(id)) return;
            _imageById[id] = _images.Count;
            string u = a["u"].AsString("");
            string p = a["p"].AsString("");
            byte[] bytes = null;
            string path = p;
            if (p.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = p.IndexOf(',');
                if (comma > 0)
                {
                    try { bytes = Convert.FromBase64String(p.Substring(comma + 1)); }
                    catch (FormatException) { Warn($"Image '{id}': invalid base64 data."); }
                }
                path = "";
            }
            else if (!string.IsNullOrEmpty(u))
            {
                path = u + p;
            }
            _images.Add(new ImageAsset { Width = a["w"].AsFloat(0), Height = a["h"].AsFloat(0), Texture = -1 });
            _imageIds.Add(id);
            _imagePaths.Add(path);
            _imageBytes.Add(bytes);
        }

        // ------------------------------------------------------------------------------ layers

        void CompileComposition(int compIndex, JsonValue layers, float w, float h)
        {
            int first = _layers.Count;
            int count = layers.Count;
            var comp = new CompData { FirstLayer = first, LayerCount = count, Width = w, Height = h };
            _comps[compIndex] = comp;
            // Reserve slots so that shape/mask compilation of one layer can't interleave with another.
            for (int i = 0; i < count; i++)
            {
                _layers.Add(default);
                _layerNames.Add("");
                _layerShapeRoots.Add(new int2(-1, 0));
            }
            int li = 0;
            foreach (var l in layers)
            {
                _currentShapeRoots = new int2(-1, 0);
                _layers[first + li] = CompileLayer(l, compIndex);
                _layerNames[first + li] = l["nm"].AsString("");
                _layerShapeRoots[first + li] = _currentShapeRoots;
                li++;
            }

            // Resolve parenting and track mattes (indices are global).
            for (int i = 0; i < count; i++)
            {
                var src = layers[i];
                var L = _layers[first + i];
                L.Parent = -1;
                var parent = src["parent"];
                if (parent.IsNumber)
                {
                    int pind = parent.AsInt();
                    for (int j = 0; j < count; j++)
                        if (_layers[first + j].Ind == pind && j != i) { L.Parent = first + j; break; }
                }
                L.MatteSource = -1;
                if (L.MatteMode != 0)
                {
                    var tp = src["tp"];
                    int mi = -1;
                    if (!tp.IsMissing)
                    {
                        int tpi = tp.AsInt();
                        for (int j = 0; j < count; j++)
                            if (_layers[first + j].Ind == tpi) { mi = j; break; }
                    }
                    else mi = i - 1;
                    L.MatteSource = mi >= 0 ? first + mi : -1;
                    if (mi < 0) L.MatteMode = 0;
                }
                _layers[first + i] = L;
            }
        }

        LayerData CompileLayer(JsonValue l, int compIndex)
        {
            var L = new LayerData
            {
                Comp = compIndex,
                Parent = -1,
                MatteSource = -1,
                TimeRemap = -1,
                RefComp = -1,
                Image = -1,
                ShapeProgram = -1,
                Text = -1,
            };
            int ty = l["ty"].AsInt(3);
            L.Kind = ty switch
            {
                0 => LayerKind.Precomp,
                1 => LayerKind.Solid,
                2 => LayerKind.Image,
                3 => LayerKind.Null,
                4 => LayerKind.Shape,
                5 => LayerKind.Text,
                6 => LayerKind.Audio,
                13 => LayerKind.Camera,
                15 => LayerKind.Data,
                _ => LayerKind.Unknown,
            };
            L.Ind = l["ind"].AsInt(int.MinValue);
            L.InPoint = l["ip"].AsFloat(0);
            L.OutPoint = l["op"].AsFloat(float.MaxValue);
            L.StartTime = l["st"].AsFloat(0);
            float sr = l["sr"].AsFloat(1);
            L.Stretch = sr == 0 ? 1 : sr;
            L.BlendMode = (byte)math.clamp(l["bm"].AsInt(0), 0, 255);
            L.MatteMode = (byte)math.clamp(l["tt"].AsInt(0), 0, 4);
            if (l["td"].AsInt(0) != 0) L.Flags |= LayerFlags.MatteSource;
            if (l["hd"].AsBool()) L.Flags |= LayerFlags.Hidden;
            if (l["ao"].AsInt(0) == 1) L.Flags |= LayerFlags.AutoOrient;
            if (l["ddd"].AsInt(0) == 1) L.Flags |= LayerFlags.Is3D;

            var ks = l["ks"];
            if (ks.IsObject) L.Flags |= LayerFlags.HasTransform;
            L.Transform = CompileTransform(ks);
            if (L.Transform.Split != 0) L.Flags |= LayerFlags.SplitPosition;
            if (L.Transform.Has3D != 0) L.Flags |= LayerFlags.Has3DRotation;
            if (L.Transform.Skew >= 0) L.Flags |= LayerFlags.HasSkew;

            switch (L.Kind)
            {
                case LayerKind.Precomp:
                {
                    string refId = l["refId"].AsString("");
                    L.RefComp = _compById.TryGetValue(refId, out int ci) ? ci : -1;
                    if (L.RefComp < 0) Warn($"Precomp layer '{l["nm"].AsString()}' references missing composition '{refId}'.");
                    L.Width = l["w"].AsFloat(0);
                    L.Height = l["h"].AsFloat(0);
                    if (l.Has("tm")) L.TimeRemap = CompileProp(l["tm"], PropHint.Scalar);
                    break;
                }
                case LayerKind.Solid:
                    L.Width = l["sw"].AsFloat(0);
                    L.Height = l["sh"].AsFloat(0);
                    L.SolidColor = ParseHexColor(l["sc"].AsString("#000000"));
                    break;
                case LayerKind.Image:
                {
                    string refId = l["refId"].AsString("");
                    L.Image = _imageById.TryGetValue(refId, out int ii) ? ii : -1;
                    if (L.Image < 0) Warn($"Image layer references missing asset '{refId}'.");
                    break;
                }
                case LayerKind.Shape:
                {
                    var shapes = l["shapes"];
                    int first = CompileShapeItems(shapes, out int count);
                    _currentShapeRoots = new int2(first, count);
                    L.ShapeProgram = _programs.Build(first, count);
                    break;
                }
                case LayerKind.Text:
                    L.Text = CompileTextLayer(l);
                    break;
                case LayerKind.Camera:
                    Unsupported("3D camera layers");
                    break;
            }

            // Masks.
            var masks = l["masksProperties"];
            if (masks.IsArray && masks.Count > 0)
            {
                L.MaskFirst = _masks.Count;
                foreach (var m in masks) _masks.Add(CompileMask(m));
                L.MaskCount = _masks.Count - L.MaskFirst;
                L.Flags |= LayerFlags.HasMasks;
            }

            // Effects, then layer styles.
            L.EffectFirst = _effects.Count;
            var ef = l["ef"];
            if (ef.IsArray)
                foreach (var e in ef) CompileEffect(e);
            CompileStyles(l["sy"]);
            L.EffectCount = _effects.Count - L.EffectFirst;
            return L;
        }

        static float4 ParseHexColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new float4(0, 0, 0, 1);
            hex = hex.Trim();
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length == 3) hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
            if (hex.Length < 6) return new float4(0, 0, 0, 1);
            int r = int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new float4(r / 255f, g / 255f, b / 255f, 1);
        }

        TransformDesc CompileTransform(JsonValue ks)
        {
            var t = TransformDesc.Empty;
            if (!ks.IsObject) return t;
            t.Anchor = CompileProp(ks["a"], PropHint.Vector);
            var p = ks["p"];
            if (p.IsObject && p["s"].AsBool())
            {
                t.Split = 1;
                t.PosX = CompileProp(p["x"], PropHint.Scalar);
                t.PosY = CompileProp(p["y"], PropHint.Scalar);
                if (p.Has("z")) t.PosZ = CompileProp(p["z"], PropHint.Scalar);
            }
            else
            {
                t.Position = CompileProp(p, PropHint.Vector);
            }
            t.Scale = CompileProp(ks["s"], PropHint.Vector);
            if (!ks["rx"].IsMissing)
            {
                t.Has3D = 1;
                t.RotX = CompileProp(ks["rx"], PropHint.Scalar);
                t.RotY = CompileProp(ks["ry"], PropHint.Scalar);
                t.RotZ = CompileProp(ks["rz"], PropHint.Scalar);
                t.Orientation = CompileProp(ks["or"], PropHint.VectorNoSpatial);
            }
            else
            {
                t.Rotation = CompileProp(ks["r"], PropHint.Scalar);
                if (t.Rotation < 0 && !ks["rz"].IsMissing) t.Rotation = CompileProp(ks["rz"], PropHint.Scalar);
            }
            t.Opacity = CompileProp(ks["o"], PropHint.Scalar);
            if (!ks["sk"].IsMissing)
            {
                t.Skew = CompileProp(ks["sk"], PropHint.Scalar);
                t.SkewAxis = CompileProp(ks["sa"], PropHint.Scalar);
            }
            return t;
        }

        MaskData CompileMask(JsonValue m)
        {
            string mode = m["mode"].AsString("a");
            var md = new MaskData
            {
                Mode = mode switch
                {
                    "a" => MaskMode.Add,
                    "s" => MaskMode.Subtract,
                    "i" => MaskMode.Intersect,
                    "l" => MaskMode.Lighten,
                    "d" => MaskMode.Darken,
                    "f" => MaskMode.Difference,
                    "n" => MaskMode.None,
                    _ => MaskMode.Add,
                },
                Inverted = (byte)(m["inv"].AsBool() ? 1 : 0),
            };
            bool? closed = OlderThan(4, 4, 18) ? m["cl"].AsBool(true) : (bool?)null;
            md.Path = CompilePathProp(m["pt"], closed);
            md.Opacity = CompileProp(m["o"], PropHint.Scalar);
            md.Expansion = CompileProp(m["x"], PropHint.Scalar);
            return md;
        }

        /// <summary>After Effects match names of the effects rendered beyond lottie-web's set (Skottie's list).</summary>
        static readonly Dictionary<string, EffectKind> s_EffectsByMatchName = new Dictionary<string, EffectKind>
        {
            { "ADBE Black&White", EffectKind.BlackAndWhite },
            { "ADBE Brightness & Contrast 2", EffectKind.BrightnessContrast },
            { "ADBE Bulge", EffectKind.Bulge },
            { "ADBE Corner Pin", EffectKind.CornerPin },
            { "ADBE Displacement Map", EffectKind.DisplacementMap },
            { "ADBE Drop Shadow", EffectKind.DropShadow },
            { "ADBE Easy Levels2", EffectKind.EasyLevels },
            { "ADBE Fill", EffectKind.Fill },
            { "ADBE Fractal Noise", EffectKind.FractalNoise },
            { "ADBE Gaussian Blur 2", EffectKind.GaussianBlur },
            { "ADBE Geometry2", EffectKind.Transform },
            { "ADBE HUE SATURATION", EffectKind.HueSaturation },
            { "ADBE Invert", EffectKind.Invert },
            { "ADBE Linear Wipe", EffectKind.LinearWipe },
            { "ADBE Motion Blur", EffectKind.DirectionalBlur },
            { "ADBE Pro Levels2", EffectKind.ProLevels },
            { "ADBE Radial Wipe", EffectKind.RadialWipe },
            { "ADBE Ramp", EffectKind.GradientRamp },
            { "ADBE Set Matte3", EffectKind.Matte3 },
            { "ADBE Sharpen", EffectKind.Sharpen },
            { "ADBE Shift Channels", EffectKind.ShiftChannels },
            { "ADBE Stroke", EffectKind.Stroke },
            { "ADBE Threshold2", EffectKind.Threshold },
            { "ADBE Tile", EffectKind.MotionTile },
            { "ADBE Tint", EffectKind.Tint },
            { "ADBE Tritone", EffectKind.Tritone },
            { "ADBE Venetian Blinds", EffectKind.VenetianBlinds },
            { "CC Sphere", EffectKind.Sphere },
            { "CC Toner", EffectKind.CCToner },
        };

        /// <summary>Effects rendered by this player (the others are reported as unsupported).</summary>
        internal static bool IsRenderedEffect(EffectKind k) => k switch
        {
            EffectKind.Tint or EffectKind.Fill or EffectKind.Stroke or EffectKind.Tritone or EffectKind.ProLevels or
            EffectKind.DropShadow or EffectKind.RadialWipe or EffectKind.DisplacementMap or EffectKind.Matte3 or
            EffectKind.GaussianBlur or EffectKind.Transform or
            EffectKind.GradientRamp or EffectKind.HueSaturation or EffectKind.BrightnessContrast or EffectKind.Invert or
            EffectKind.Threshold or EffectKind.BlackAndWhite or EffectKind.CCToner or EffectKind.ShiftChannels or
            EffectKind.EasyLevels or EffectKind.LinearWipe or EffectKind.VenetianBlinds or EffectKind.MotionTile or
            EffectKind.CornerPin or EffectKind.Bulge or EffectKind.DirectionalBlur or EffectKind.Sharpen or
            EffectKind.FractalNoise or EffectKind.Sphere => true,
            _ => false,
        };

        void CompileEffect(JsonValue e)
        {
            int ty = e["ty"].AsInt(0);
            string mn = e["mn"].AsString("");
            var kind = (EffectKind)math.clamp(ty, 0, 63);
            var flags = EffectFlags.None;
            // lottie-web dispatches on 'ty'; effects it doesn't know are identified by match name (like Skottie).
            if (!IsRenderedEffect(kind) && s_EffectsByMatchName.TryGetValue(mn, out var byName))
            {
                kind = byName;
                flags |= EffectFlags.ByMatchName;
            }
            var ed = new EffectData
            {
                Kind = kind,
                Flags = flags,
                Enabled = (byte)(e["en"].AsInt(1) != 0 ? 1 : 0),
                ParamFirst = _effectParams.Count,
            };
            foreach (var p in e["ef"])
            {
                int type = p["ty"].AsInt(0);
                int prop = -1;
                var v = p["v"];
                if (!v.IsMissing && type != 5 && type != 6)
                    prop = CompileProp(v, type == 2 || type == 3 ? PropHint.Vector : PropHint.Scalar);
                _effectParams.Add(new EffectParam { Type = (byte)type, Prop = prop });
            }
            ed.ParamCount = _effectParams.Count - ed.ParamFirst;
            if (IsRenderedEffect(kind) || kind == EffectKind.Unknown) { }
            else if (kind == (EffectKind)5)
            {
                // Expression controls (sliders, points, layers...) render nothing; other custom effects are unsupported.
                if (!(mn.EndsWith(" Control") || mn.StartsWith("Pseudo/") || mn.Length == 0))
                    Unsupported($"Effect '{e["nm"].AsString(mn)}' ({mn})");
            }
            else if (kind == EffectKind.Puppet) Warn("Puppet effect is ignored (lottie-web behaviour).");
            else Unsupported(mn.Length > 0 ? $"Effect '{e["nm"].AsString(mn)}' ({mn})" : $"Effect type {ty}");
            _effects.Add(ed);
        }

        // ------------------------------------------------------------------------------ shapes

        /// <summary>Compiles an item array into a contiguous block of nodes; returns the first node index.</summary>
        internal int CompileShapeItems(JsonValue items, out int count)
        {
            count = items.IsArray ? items.Count : 0;
            int first = _nodes.Count;
            for (int i = 0; i < count; i++)
            {
                _nodes.Add(default);
                _nodeNames.Add("");
            }
            int k = 0;
            foreach (var it in items)
            {
                var n = CompileShapeNode(it);
                _nodes[first + k] = n;
                _nodeNames[first + k] = it["nm"].AsString("");
                k++;
            }
            return first;
        }

        ShapeNode CompileShapeNode(JsonValue it)
        {
            var n = new ShapeNode
            {
                P0 = -1, P1 = -1, P2 = -1, P3 = -1, P4 = -1, P5 = -1, P6 = -1, P7 = -1, P8 = -1,
                Extra = -1,
            };
            string ty = it["ty"].AsString("");
            if (it["hd"].AsBool()) n.Flags |= ShapeFlags.Hidden;
            int d = it["d"].AsInt(0);
            n.Direction = (byte)math.clamp(d, 0, 255);
            if (d == 3) n.Flags |= ShapeFlags.Reversed;
            n.BlendMode = (byte)math.clamp(it["bm"].AsInt(0), 0, 255);
            switch (ty)
            {
                case "gr":
                    n.Kind = ShapeKind.Group;
                    n.First = CompileShapeItems(it["it"], out n.Count);
                    break;
                case "sh":
                {
                    n.Kind = ShapeKind.Path;
                    bool? closed = OlderThan(4, 4, 18) ? it["closed"].AsBool(false) : (bool?)null;
                    n.P0 = CompilePathProp(it["ks"], closed);
                    break;
                }
                case "rc":
                    n.Kind = ShapeKind.Rect;
                    n.P0 = CompileProp(it["p"], PropHint.Vector);
                    n.P1 = CompileProp(it["s"], PropHint.Vector);
                    n.P2 = CompileProp(it["r"], PropHint.Scalar);
                    break;
                case "el":
                    n.Kind = ShapeKind.Ellipse;
                    n.P0 = CompileProp(it["p"], PropHint.Vector);
                    n.P1 = CompileProp(it["s"], PropHint.Vector);
                    break;
                case "sr":
                    n.Kind = ShapeKind.Star;
                    n.Mode = (byte)it["sy"].AsInt(1);
                    n.P0 = CompileProp(it["p"], PropHint.Vector);
                    n.P1 = CompileProp(it["or"], PropHint.Scalar);
                    n.P2 = CompileProp(it["os"], PropHint.Scalar);
                    n.P3 = CompileProp(it["r"], PropHint.Scalar);
                    n.P4 = CompileProp(it["pt"], PropHint.Scalar);
                    n.P5 = CompileProp(it["ir"], PropHint.Scalar);
                    n.P6 = CompileProp(it["is"], PropHint.Scalar);
                    break;
                case "fl":
                    n.Kind = ShapeKind.Fill;
                    n.Mode = (byte)it["r"].AsInt(1);
                    n.P0 = CompileColorProp(it["c"]);
                    n.P1 = CompileProp(it["o"], PropHint.Scalar);
                    break;
                case "st":
                    n.Kind = ShapeKind.Stroke;
                    n.P0 = CompileColorProp(it["c"]);
                    n.P1 = CompileProp(it["o"], PropHint.Scalar);
                    n.P2 = CompileProp(it["w"], PropHint.Scalar);
                    CompileStrokeStyle(it, ref n);
                    break;
                case "gf":
                case "gs":
                {
                    n.Kind = ty == "gf" ? ShapeKind.GradientFill : ShapeKind.GradientStroke;
                    n.Mode = (byte)it["r"].AsInt(1);
                    n.Mode2 = (byte)it["t"].AsInt(1);
                    n.P0 = CompileProp(it["o"], PropHint.Scalar);
                    n.P1 = CompileProp(it["s"], PropHint.Vector);
                    n.P2 = CompileProp(it["e"], PropHint.Vector);
                    n.P3 = CompileProp(it["h"], PropHint.Scalar);
                    n.P4 = CompileProp(it["a"], PropHint.Scalar);
                    var g = it["g"];
                    n.Extra = g["p"].AsInt(0);
                    n.P5 = CompileProp(g["k"], PropHint.VectorNoSpatial);
                    if (n.Kind == ShapeKind.GradientStroke)
                    {
                        n.P6 = CompileProp(it["w"], PropHint.Scalar);
                        CompileStrokeStyle(it, ref n);
                    }
                    break;
                }
                case "no":
                    n.Kind = ShapeKind.NoStyle;
                    break;
                case "tr":
                    n.Kind = ShapeKind.Transform;
                    CompileShapeTransform(it, ref n);
                    break;
                case "tm":
                    n.Kind = ShapeKind.Trim;
                    n.Mode = (byte)it["m"].AsInt(1);
                    n.P0 = CompileProp(it["s"], PropHint.Scalar);
                    n.P1 = CompileProp(it["e"], PropHint.Scalar);
                    n.P2 = CompileProp(it["o"], PropHint.Scalar);
                    break;
                case "rd":
                    n.Kind = ShapeKind.RoundCorners;
                    n.P0 = CompileProp(it["r"], PropHint.Scalar);
                    break;
                case "rp":
                {
                    n.Kind = ShapeKind.Repeater;
                    n.Mode = (byte)it["m"].AsInt(1);
                    n.P0 = CompileProp(it["c"], PropHint.Scalar);
                    n.P1 = CompileProp(it["o"], PropHint.Scalar);
                    var tr = it["tr"];
                    n.P2 = CompileProp(tr["a"], PropHint.Vector);
                    n.P3 = CompileProp(tr["p"], PropHint.Vector);
                    n.P4 = CompileProp(tr["s"], PropHint.Vector);
                    n.P5 = CompileProp(tr["r"], PropHint.Scalar);
                    n.P6 = CompileProp(tr["so"], PropHint.Scalar);
                    n.P7 = CompileProp(tr["eo"], PropHint.Scalar);
                    break;
                }
                case "pb":
                    n.Kind = ShapeKind.PuckerBloat;
                    n.P0 = CompileProp(it["a"], PropHint.Scalar);
                    break;
                case "zz":
                    n.Kind = ShapeKind.ZigZag;
                    n.P0 = CompileProp(it["s"], PropHint.Scalar);
                    n.P1 = CompileProp(it["r"], PropHint.Scalar);
                    n.P2 = CompileProp(it["pt"], PropHint.Scalar);
                    break;
                case "op":
                    n.Kind = ShapeKind.OffsetPath;
                    n.P0 = CompileProp(it["a"], PropHint.Scalar);
                    n.P1 = CompileProp(it["ml"], PropHint.Scalar);
                    n.Mode3 = (byte)it["lj"].AsInt(1);
                    break;
                case "mm":
                    n.Kind = ShapeKind.Merge;
                    n.Mode = (byte)it["mm"].AsInt(1);
                    break;
                case "tw":
                    n.Kind = ShapeKind.Twist;
                    Unsupported("Twist shape modifier");
                    break;
                default:
                    n.Kind = ShapeKind.Unknown;
                    if (!string.IsNullOrEmpty(ty)) Unsupported($"Shape item '{ty}'");
                    break;
            }
            return n;
        }

        void CompileStrokeStyle(JsonValue it, ref ShapeNode n)
        {
            n.LineCap = (byte)it["lc"].AsInt(2);
            if (n.LineCap == 0) n.LineCap = 2;
            n.Mode3 = (byte)it["lj"].AsInt(2);
            if (n.Mode3 == 0) n.Mode3 = 2;
            n.Value = it["ml"].IsMissing ? 4f : it["ml"].AsFloat(4);
            if (n.Mode3 != 1) n.Value = 4f; // lottie-web only sets stroke-miterlimit for miter joins (SVG default 4)
            var dashes = it["d"];
            if (dashes.IsArray && dashes.Count > 0)
            {
                n.DashFirst = _dashes.Count;
                foreach (var dd in dashes)
                {
                    string nk = dd["n"].AsString("d");
                    _dashes.Add(new DashData
                    {
                        Kind = (byte)(nk == "o" ? 2 : nk == "g" ? 1 : 0),
                        Prop = CompileProp(dd["v"], PropHint.Scalar),
                    });
                }
                n.DashCount = _dashes.Count - n.DashFirst;
            }
        }

        void CompileShapeTransform(JsonValue it, ref ShapeNode n)
        {
            n.P0 = CompileProp(it["a"], PropHint.Vector);
            var p = it["p"];
            if (p.IsObject && p["s"].AsBool())
            {
                n.Flags |= ShapeFlags.SplitPosition;
                n.P7 = CompileProp(p["x"], PropHint.Scalar);
                n.P8 = CompileProp(p["y"], PropHint.Scalar);
            }
            else n.P1 = CompileProp(p, PropHint.Vector);
            n.P2 = CompileProp(it["s"], PropHint.Vector);
            n.P3 = CompileProp(it["r"], PropHint.Scalar);
            if (n.P3 < 0 && !it["rz"].IsMissing) n.P3 = CompileProp(it["rz"], PropHint.Scalar);
            n.P4 = CompileProp(it["o"], PropHint.Scalar);
            if (!it["sk"].IsMissing)
            {
                n.P5 = CompileProp(it["sk"], PropHint.Scalar);
                n.P6 = CompileProp(it["sa"], PropHint.Scalar);
            }
        }

        internal ShapeNode GetNode(int index) => _nodes[index];
        internal int NodeCount => _nodes.Count;
        internal PropDesc GetProp(int index) => _props[index];
        internal List<float> Floats => _f;
        internal List<KeyframeData> Keys => _keys;
    }
}
