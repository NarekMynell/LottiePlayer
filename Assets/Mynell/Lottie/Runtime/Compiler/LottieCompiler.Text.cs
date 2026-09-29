using System;
using System.Collections.Generic;
using System.Globalization;
using Mynell.Lottie.Json;
using Mynell.Lottie.Model;
using Mynell.Lottie.Text;
using Unity.Mathematics;

namespace Mynell.Lottie.Compiler
{
    /// <summary>Font request passed to a <see cref="LottieCompiler.FontResolver"/>.</summary>
    public struct LottieFontRequest
    {
        /// <summary>PostScript-style name used by the animation (fName), e.g. "Montserrat-Bold".</summary>
        public string Name;
        public string Family;
        public string Style;
        /// <summary>Optional font path / URL from the file (fPath), may be a data: URI.</summary>
        public string Path;
    }

    public sealed partial class LottieCompiler
    {
        /// <summary>Returns font file bytes (.ttf / .otf / .ttc) for fonts whose glyphs are not embedded, or null.</summary>
        public Func<LottieFontRequest, byte[]> FontResolver;

        sealed class FontInfo
        {
            public string Name, Family, Style, Path;
            public float Ascent;
            public TrueTypeFont File;
            public bool FileLoaded;
            public readonly Dictionary<string, int> GlyphCache = new Dictionary<string, int>();
        }

        readonly List<FontInfo> _fonts = new List<FontInfo>();
        readonly Dictionary<string, int> _charGlyphs = new Dictionary<string, int>(); // "family\nstyle\nch" -> glyph
        readonly Dictionary<string, float> _charWidths = new Dictionary<string, float>();
        bool _glyphMode;
        readonly List<GlyphData> _glyphs = new List<GlyphData>();
        readonly List<GlyphContour> _glyphContours = new List<GlyphContour>();
        readonly List<float2> _glyphPoints = new List<float2>();
        readonly List<TextDocData> _textDocs = new List<TextDocData>();
        readonly List<TextLetter> _textLetters = new List<TextLetter>();
        readonly List<int> _textInts = new List<int>();
        readonly List<float> _textFloats = new List<float>();
        readonly List<TextDocKey> _textDocKeys = new List<TextDocKey>();
        readonly List<TextAnimatorData> _textAnimators = new List<TextAnimatorData>();
        readonly List<TextSelectorData> _textSelectors = new List<TextSelectorData>();
        /// <summary>Expression selectors to bake after compilation: (selector index, global layer index, code, letter count).</summary>
        internal readonly List<(int selector, int layer, string code, int total)> TextExpressionSelectors = new List<(int, int, string, int)>();
        int _currentLayerIndex = -1;
        readonly List<TextLayerInfo> _textLayers = new List<TextLayerInfo>();
        readonly HashSet<string> _missingFonts = new HashSet<string>();

        // ------------------------------------------------------------------ fonts & chars

        void CompileFontsAndChars(JsonValue root)
        {
            foreach (var f in root["fonts"]["list"])
            {
                _fonts.Add(new FontInfo
                {
                    Name = f["fName"].AsString(""),
                    Family = f["fFamily"].AsString(""),
                    Style = f["fStyle"].AsString(""),
                    Path = f["fPath"].AsString(""),
                    Ascent = f["ascent"].AsFloat(75),
                });
            }
            var chars = root["chars"];
            _glyphMode = chars.IsArray && chars.Count > 0;
            foreach (var c in chars)
            {
                string key = CharKey(c["fFamily"].AsString(""), c["style"].AsString(""), c["ch"].AsString(""));
                if (_charGlyphs.ContainsKey(key)) continue; // lottie-web addChars keeps the first
                float w = c["w"].AsFloat(0);
                int g = AddGlyph(w);
                // 't': 1 -> the glyph is an animated precomposition (compiled once all compositions exist).
                if (c["t"].AsInt(0) == 1) _glyphComps.Add((g, c["data"]));
                else CompileGlyphShapes(c["data"]["shapes"], g, Affine(1, 0, 0, 1, 0, 0), 0);
                FinishGlyph(g);
                _charGlyphs[key] = g;
            }
        }

        static string CharKey(string family, string style, string ch) => family + "\n" + style + "\n" + ch;

        FontInfo FontByName(string name)
        {
            foreach (var f in _fonts) if (f.Name == name) return f;
            if (_fonts.Count > 0) return _fonts[0];
            var def = new FontInfo { Name = name, Family = name, Style = "", Ascent = 75 };
            _fonts.Add(def);
            return def;
        }

        int _glyphContourStart;

        int AddGlyph(float advance)
        {
            _glyphContourStart = _glyphContours.Count;
            _glyphs.Add(new GlyphData { ContourFirst = _glyphContours.Count, Advance = advance, Layer = -1, Image = -1 });
            return _glyphs.Count - 1;
        }

        readonly List<(int glyph, JsonValue data)> _glyphComps = new List<(int, JsonValue)>();

        /// <summary>
        /// lottie-web SVGTextLottieElement: glyphs with 't': 1 are precomp layers ('data': refId, ks, ip, op, st, sr) drawn
        /// in the letter's place at size / 100. They get layers of their own after every composition's layers.
        /// </summary>
        void CompileGlyphCompositions()
        {
            foreach (var (glyph, data) in _glyphComps)
            {
                string refId = data["refId"].AsString("");
                if (!_compById.TryGetValue(refId, out int comp))
                {
                    Warn($"Animated glyph references missing composition '{refId}'.");
                    continue;
                }
                var L = new LayerData
                {
                    Kind = LayerKind.Precomp,
                    Comp = 0,
                    Parent = -1,
                    MatteSource = -1,
                    TimeRemap = -1,
                    RefComp = comp,
                    Image = -1,
                    ShapeProgram = -1,
                    Text = -1,
                    CameraZoom = -1,
                    Ind = int.MinValue,
                    InPoint = data["ip"].AsFloat(0),
                    OutPoint = data["op"].AsFloat(float.MaxValue),
                    StartTime = data["st"].AsFloat(0),
                    Stretch = data["sr"].AsFloat(1) == 0 ? 1 : data["sr"].AsFloat(1),
                    Width = data["w"].AsFloat(0),
                    Height = data["h"].AsFloat(0),
                };
                var ks = data["ks"];
                if (ks.IsObject) L.Flags |= LayerFlags.HasTransform;
                L.Transform = CompileTransform(ks);
                var gd = _glyphs[glyph];
                gd.Layer = _layers.Count;
                _glyphs[glyph] = gd;
                _layers.Add(L);
                _layerNames.Add("");
                _layerShapeRoots.Add(new int2(-1, 0));
            }
        }

        void FinishGlyph(int g)
        {
            var gd = _glyphs[g];
            gd.ContourCount = _glyphContours.Count - gd.ContourFirst;
            _glyphs[g] = gd;
        }

        static double[] Affine(double a, double b, double c, double d, double tx, double ty) => new[] { a, b, c, d, tx, ty };

        /// <summary>Glyph shapes from a chars entry: groups (with transforms) of static bezier paths.</summary>
        void CompileGlyphShapes(JsonValue items, int glyph, double[] m, int depth)
        {
            if (depth > 16) return;
            foreach (var it in items)
            {
                string ty = it["ty"].AsString("");
                if (ty == "gr")
                {
                    double[] gm = m;
                    foreach (var sub in it["it"])
                    {
                        if (sub["ty"].AsString("") != "tr") continue;
                        gm = Mul(GroupMatrix(sub), m);
                    }
                    CompileGlyphShapes(it["it"], glyph, gm, depth + 1);
                }
                else if (ty == "sh")
                {
                    var ks = it["ks"];
                    var k = ks.IsObject && ks.Has("k") ? ks["k"] : ks;
                    JsonValue path = k.IsObject ? k : k.IsArray && k.Count > 0 && k[0].IsObject ? (k[0].Has("s") ? PathObj(k[0]["s"]) : k[0]) : default;
                    if (path.IsMissing) continue;
                    var flat = ReadPath(path, out bool closed, -1);
                    int n = flat.Length / 6;
                    if (n == 0) continue;
                    var gc = new GlyphContour { PointFirst = _glyphPoints.Count, Count = n, Closed = (byte)(closed ? 1 : 0) };
                    for (int v = 0; v < n; v++)
                    {
                        _glyphPoints.Add(Apply(m, flat[v * 6], flat[v * 6 + 1]));
                        _glyphPoints.Add(Apply(m, flat[v * 6 + 2], flat[v * 6 + 3]));
                        _glyphPoints.Add(Apply(m, flat[v * 6 + 4], flat[v * 6 + 5]));
                    }
                    _glyphContours.Add(gc);
                }
            }
        }

        static float2 Apply(double[] m, double x, double y) => new float2((float)(m[0] * x + m[2] * y + m[4]), (float)(m[1] * x + m[3] * y + m[5]));

        static double[] Mul(double[] a, double[] b) => new[]
        {
            a[0] * b[0] + a[1] * b[2], a[0] * b[1] + a[1] * b[3],
            a[2] * b[0] + a[3] * b[2], a[2] * b[1] + a[3] * b[3],
            a[4] * b[0] + a[5] * b[2] + b[4], a[4] * b[1] + a[5] * b[3] + b[5],
        };

        static double[] GroupMatrix(JsonValue tr)
        {
            double[] V(JsonValue p, double d0, double d1)
            {
                var k = p["k"];
                if (k.IsArray && k.Count > 0 && k[0].IsNumber) return new[] { k[0].AsDouble(d0), k.Count > 1 ? k[1].AsDouble(d1) : d1 };
                if (k.IsNumber) return new[] { k.AsDouble(d0), k.AsDouble(d0) };
                return new[] { d0, d1 };
            }
            var a = V(tr["a"], 0, 0);
            var p0 = V(tr["p"], 0, 0);
            var s = V(tr["s"], 100, 100);
            double r = tr["r"]["k"].AsDouble(0) * Math.PI / 180;
            var m = Affine(1, 0, 0, 1, -a[0], -a[1]);
            m = Mul(m, Affine(s[0] / 100, 0, 0, s[1] / 100, 0, 0));
            double cs = Math.Cos(-r), sn = Math.Sin(-r);
            m = Mul(m, Affine(cs, -sn, sn, cs, 0, 0));
            m = Mul(m, Affine(1, 0, 0, 1, p0[0], p0[1]));
            return m;
        }

        /// <summary>
        /// Glyph for a character: from the exported chars (lottie-web getCharData) in glyph mode, otherwise from a
        /// resolved font file. Returns the glyph index and its advance (font size 100 units).
        /// </summary>
        int GlyphFor(FontInfo font, string ch, out float advance)
        {
            advance = 0;
            if (_glyphMode)
            {
                if (_charGlyphs.TryGetValue(CharKey(font.Family, font.Style, ch), out int g))
                {
                    advance = _glyphs[g].Advance;
                    return g;
                }
                return -1; // lottie-web emptyChar (w = 0)
            }
            if (font.GlyphCache.TryGetValue(ch, out int cached))
            {
                advance = cached >= 0 ? _glyphs[cached].Advance : 0;
                return cached;
            }
            var file = LoadFontFile(font);
            int result = -1;
            int firstCp = ch.Length == 0 ? 0 : char.IsSurrogatePair(ch, 0) ? char.ConvertToUtf32(ch, 0) : ch[0];
            // Emoji presentation (like the browser): pictographs, sequences with VS16, or characters the font lacks.
            bool wantsEmoji = ch.IndexOf('\uFE0F') >= 0 || firstCp >= 0x1F000 || (file != null && file.GlyphIndex(firstCp) == 0 && firstCp > 0x7F);
            if (wantsEmoji && EmojiGlyph(ch, out int emoji, out advance))
            {
                font.GlyphCache[ch] = emoji;
                return emoji;
            }
            if (file != null)
            {
                int cp = firstCp;
                int gi = file.GlyphIndex(cp);
                bool missing = gi == 0;
                float adv = file.Advance(gi) * 100f / file.UnitsPerEm;
                result = AddGlyph(adv);
                foreach (var contour in missing ? new List<GlyphOutlineContour>() : file.Outline(gi))
                {
                    var gc = new GlyphContour { PointFirst = _glyphPoints.Count, Count = contour.Vertices.Count, Closed = (byte)(contour.Closed ? 1 : 0) };
                    foreach (var v in contour.Vertices)
                    {
                        _glyphPoints.Add(new float2(v.X, v.Y));
                        _glyphPoints.Add(new float2(v.InX, v.InY));
                        _glyphPoints.Add(new float2(v.OutX, v.OutY));
                    }
                    _glyphContours.Add(gc);
                }
                FinishGlyph(result);
                advance = adv;
            }
            font.GlyphCache[ch] = result;
            return result;
        }

        internal string EmojiFontPath;
        internal readonly List<Text.EmojiFont> _emoji = new List<Text.EmojiFont>();
        bool _emojiLoaded;
        readonly Dictionary<string, int> _emojiGlyphs = new Dictionary<string, int>();

        /// <summary>A colour emoji glyph (bitmap stored as an image asset of the animation).</summary>
        bool EmojiGlyph(string ch, out int glyph, out float advance)
        {
            glyph = -1;
            advance = 0;
            if (_emojiGlyphs.TryGetValue(ch, out int cached))
            {
                glyph = cached;
                advance = glyph >= 0 ? _glyphs[glyph].Advance : 0;
                return glyph >= 0;
            }
            if (!_emojiLoaded)
            {
                _emojiLoaded = true;
                var paths = EmojiFontPath != null ? new List<string> { EmojiFontPath } : Text.EmojiFont.SystemFontPaths();
                foreach (var path in paths)
                {
                    var font = string.IsNullOrEmpty(path) ? null : Text.EmojiFont.Open(path);
                    if (font != null) _emoji.Add(font);
                }
            }
            Text.EmojiFont.Glyph bitmap = default;
            bool found = false;
            foreach (var font in _emoji)
                if (font.TryGetGlyph(ch, out bitmap))
                {
                    found = true;
                    break;
                }
            if (!found)
            {
                _emojiGlyphs[ch] = -1;
                return false;
            }
            int image = _images.Count;
            ReadPngSize(bitmap.Png, out int w, out int h);
            _images.Add(new ImageAsset { Width = w, Height = h, Texture = -1 });
            _imageIds.Add("emoji:" + ch);
            _imagePaths.Add("");
            _imageBytes.Add(bitmap.Png);
            glyph = AddGlyph(bitmap.Advance);
            FinishGlyph(glyph);
            var gd = _glyphs[glyph];
            gd.Image = image;
            gd.ImageRect = new float4(bitmap.X, bitmap.Y, bitmap.Width, bitmap.Height);
            _glyphs[glyph] = gd;
            _emojiGlyphs[ch] = glyph;
            advance = bitmap.Advance;
            return true;
        }

        static void ReadPngSize(byte[] png, out int w, out int h)
        {
            w = h = 1;
            if (png == null || png.Length < 24) return;
            w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        }

        TrueTypeFont LoadFontFile(FontInfo font)
        {
            if (font.FileLoaded) return font.File;
            font.FileLoaded = true;
            byte[] bytes = null;
            if (!string.IsNullOrEmpty(font.Path) && font.Path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = font.Path.IndexOf(',');
                if (comma > 0)
                {
                    try { bytes = Convert.FromBase64String(font.Path.Substring(comma + 1)); }
                    catch (FormatException) { }
                }
            }
            if (bytes == null && FontResolver != null)
            {
                try
                {
                    bytes = FontResolver(new LottieFontRequest { Name = font.Name, Family = font.Family, Style = font.Style, Path = font.Path });
                }
                catch (Exception e)
                {
                    Warn($"Font resolver failed for '{font.Name}': {e.Message}");
                }
            }
            if (bytes != null)
            {
                var faces = TrueTypeFont.Load(bytes);
                font.File = PickFace(faces, font);
            }
            if (font.File == null && FontResolver != null) font.File = LoadFallbackFont(font);
            if (font.File == null && _missingFonts.Add(font.Name))
                Unsupported($"Font '{font.Family} {font.Style}' ({font.Name}) is not embedded as glyphs and no font file was found");
            return font.File;
        }

        /// <summary>Common sans-serif families tried when a font is not available (the text is still drawn).</summary>
        static readonly string[] s_FallbackFamilies =
            { "Helvetica", "Arial", "Helvetica Neue", "Roboto", "Noto Sans", "Liberation Sans", "DejaVu Sans", "Segoe UI" };

        TrueTypeFont LoadFallbackFont(FontInfo font)
        {
            foreach (var family in s_FallbackFamilies)
            {
                byte[] bytes;
                try { bytes = FontResolver(new LottieFontRequest { Name = "", Family = family, Style = font.Style }); }
                catch (Exception) { continue; }
                if (bytes == null) continue;
                var faces = TrueTypeFont.Load(bytes);
                TrueTypeFont pick = null;
                foreach (var f in faces)
                {
                    string fam = f.TypographicFamily.Length > 0 ? f.TypographicFamily : f.Family;
                    string sub = f.TypographicSubFamily.Length > 0 ? f.TypographicSubFamily : f.SubFamily;
                    if (!string.Equals(fam, family, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(sub, font.Style, StringComparison.OrdinalIgnoreCase)) { pick = f; break; }
                    if (pick == null && string.Equals(sub, "Regular", StringComparison.OrdinalIgnoreCase)) pick = f;
                }
                pick ??= faces.Count > 0 ? faces[0] : null;
                if (pick == null) continue;
                if (_missingFonts.Add(font.Name))
                    Warn($"Font '{font.Family} {font.Style}' ({font.Name}) was not found; text uses '{pick.FullName}' instead. " +
                         "Add the font file to the project and reimport for the exact look.");
                return pick;
            }
            return null;
        }

        static TrueTypeFont PickFace(List<TrueTypeFont> faces, FontInfo font)
        {
            if (faces.Count == 0) return null;
            foreach (var f in faces)
                if (string.Equals(f.PostScriptName, font.Name, StringComparison.OrdinalIgnoreCase)) return f;
            foreach (var f in faces)
            {
                string fam = f.TypographicFamily.Length > 0 ? f.TypographicFamily : f.Family;
                string sub = f.TypographicSubFamily.Length > 0 ? f.TypographicSubFamily : f.SubFamily;
                if (string.Equals(fam, font.Family, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(sub, font.Style, StringComparison.OrdinalIgnoreCase)) return f;
            }
            foreach (var f in faces)
                if (string.Equals(f.Family, font.Family, StringComparison.OrdinalIgnoreCase)) return f;
            return faces[0];
        }

        // ------------------------------------------------------------------ text layers

        int CompileTextLayer(JsonValue l)
        {
            var t = l["t"];
            var info = new TextLayerInfo
            {
                DocKeyFirst = _textDocKeys.Count,
                AnimatorFirst = _textAnimators.Count,
                Alignment = -1,
                PathMask = -1,
                PathFirstMargin = -1,
                PathLastMargin = -1,
                PathForceAlign = -1,
                PathPerpendicular = -1,
                PathReverseProp = -1,
            };
            var animators = new List<JsonValue>();
            var expressionSelectors = new List<int>();
            foreach (var a in t["a"])
            {
                animators.Add(a);
                _textAnimators.Add(CompileTextAnimator(a, expressionSelectors));
            }
            info.AnimatorCount = animators.Count;
            var m = t["m"];
            info.Grouping = (byte)m["g"].AsInt(1);
            if (m.Has("a")) info.Alignment = CompileProp(m["a"], PropHint.VectorNoSpatial);

            foreach (var kf in t["d"]["k"])
            {
                int doc = CompileTextDocument(kf["s"], animators, info.Grouping);
                _textDocKeys.Add(new TextDocKey { Time = kf["t"].AsFloat(0), Doc = doc });
            }
            info.DocKeyCount = _textDocKeys.Count - info.DocKeyFirst;
            if (t["d"].Has("x")) _expressionCount++;

            // Expression selectors are evaluated for every letter index of the (first) text document.
            if (expressionSelectors.Count > 0 && info.DocKeyCount > 0)
            {
                var firstDoc = _textDocs[_textDocKeys[info.DocKeyFirst].Doc];
                for (int a = 0; a < animators.Count; a++)
                {
                    var ad = _textAnimators[info.AnimatorFirst + a];
                    for (int k = 0; k < ad.SelCount; k++)
                    {
                        int sel = ad.SelFirst + k;
                        if (_textSelectors[sel].Kind != 1) continue;
                        TextExpressionSelectors.Add((sel, _currentLayerIndex, _pendingSelectorCode[sel], _textInts[firstDoc.AnimatorTotalFirst + a]));
                        _expressionCount++;
                    }
                }
            }

            var p = t["p"];
            if (p.IsObject && p.Has("m"))
            {
                info.HasPath = 1;
                info.PathMask = p["m"].AsInt(-1); // resolved against the layer's masks at render time
                info.PathFirstMargin = CompileProp(p["f"], PropHint.Scalar);
                info.PathLastMargin = CompileProp(p["l"], PropHint.Scalar);
                info.PathForceAlign = CompileProp(p["a"].IsNumber ? default : p["a"], PropHint.Scalar);
                info.PathPerpendicular = CompileProp(p["p"].IsNumber ? default : p["p"], PropHint.Scalar);
                info.PathReverseProp = CompileProp(p["r"].IsNumber ? default : p["r"], PropHint.Scalar);
                if (p["a"].IsNumber) info.PathForceAlign = AddStatic(new[] { p["a"].AsFloat() });
                if (p["p"].IsNumber) info.PathPerpendicular = AddStatic(new[] { p["p"].AsFloat() });
                if (p["r"].IsNumber) info.PathReverseProp = AddStatic(new[] { p["r"].AsFloat() });
            }
            _textLayers.Add(info);
            return _textLayers.Count - 1;
        }

        /// <summary>The first selector of an animator ('s' is an object, or an array when there are several).</summary>
        static JsonValue FirstSelector(JsonValue s) => s.IsArray ? (s.Count > 0 ? s[0] : default) : s;

        TextAnimatorData CompileTextAnimator(JsonValue a, List<int> expressionSelectors)
        {
            var s = a["s"];
            var p = a["a"];
            var first = FirstSelector(s);
            var d = new TextAnimatorData
            {
                SelFirst = _textSelectors.Count,
                Based = (byte)first["b"].AsInt(1),
                Randomize = (byte)first["rn"].AsInt(0),
                R = P(p["r"]), Rx = P(p["rx"]), Ry = P(p["ry"]), Sk = P(p["sk"]), Sa = P(p["sa"]),
                Scale = P(p["s"], PropHint.VectorNoSpatial), Anchor = P(p["a"], PropHint.VectorNoSpatial),
                Opacity = P(p["o"]), Position = P(p["p"], PropHint.VectorNoSpatial), StrokeWidth = P(p["sw"]),
                StrokeColor = P(p["sc"], PropHint.VectorNoSpatial), FillColor = P(p["fc"], PropHint.VectorNoSpatial),
                FillHue = P(p["fh"]), FillSat = P(p["fs"]), FillBright = P(p["fb"]), Tracking = P(p["t"]),
                Blur = P(p["bl"], PropHint.VectorNoSpatial),
            };
            // Bodymovin exports several selectors as an array; they add up (Skottie / After Effects "Add" mode).
            if (s.IsArray) foreach (var sel in s) CompileTextSelector(sel, expressionSelectors);
            else if (s.IsObject) CompileTextSelector(s, expressionSelectors);
            d.SelCount = _textSelectors.Count - d.SelFirst;
            return d;
        }

        void CompileTextSelector(JsonValue s, List<int> expressionSelectors)
        {
            if (!s.IsObject) return;
            int kind = s["t"].AsInt(0);
            if (kind == 1)
            {
                // Expression selector: its amount is baked per frame and letter (lottie-web TextExpressionSelectorProp).
                expressionSelectors.Add(_textSelectors.Count);
                _textSelectors.Add(new TextSelectorData { Kind = 1, ExprFirst = -1, S = -1, E = -1, O = -1, Xe = -1, Ne = -1, Sm = -1, A = -1 });
                _pendingSelectorCode.Add(s["x"].AsString(""));
                return;
            }
            if (kind != 0) Unsupported($"Text selector type {kind}");
            _textSelectors.Add(new TextSelectorData
            {
                S = s.Has("s") ? CompileProp(s["s"], PropHint.Scalar) : AddStatic(new[] { 0f }),
                E = s.Has("e") ? CompileProp(s["e"], PropHint.Scalar) : -1,
                O = s.Has("o") ? CompileProp(s["o"], PropHint.Scalar) : AddStatic(new[] { 0f }),
                Xe = s.Has("xe") ? CompileProp(s["xe"], PropHint.Scalar) : AddStatic(new[] { 0f }),
                Ne = s.Has("ne") ? CompileProp(s["ne"], PropHint.Scalar) : AddStatic(new[] { 0f }),
                Sm = s.Has("sm") ? CompileProp(s["sm"], PropHint.Scalar) : AddStatic(new[] { 100f }),
                A = s.Has("a") ? CompileProp(s["a"], PropHint.Scalar) : AddStatic(new[] { 100f }),
                Shape = (byte)s["sh"].AsInt(1),
                Range = (byte)s["r"].AsInt(1),
                HasE = (byte)(s.Has("e") ? 1 : 0),
                ExprFirst = -1,
            });
            _pendingSelectorCode.Add(null);
        }

        readonly List<string> _pendingSelectorCode = new List<string>();

        int P(JsonValue v, PropHint hint = PropHint.Scalar) => v.IsMissing ? -1 : CompileProp(v, hint);

        // ------------------------------------------------------------------ layout (lottie-web completeTextData)

        static readonly int[] CombinedCharacters =
        {
            2304, 2305, 2306, 2307, 2362, 2363, 2364, 2366, 2367, 2368, 2369, 2370, 2371, 2372, 2373, 2374, 2375, 2376, 2377,
            2378, 2379, 2380, 2381, 2382, 2383, 2387, 2388, 2389, 2390, 2391, 2402, 2403,
        };

        static int CodePointAt(string s, int i) =>
            i + 1 < s.Length && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[i + 1]) : 0;

        static bool IsRegionalCode(string s, int i)
        {
            int cp = CodePointAt(s, i);
            return cp >= 127462 && cp <= 127487;
        }

        static bool IsRegionalFlag(string text, int index)
        {
            if (CodePointAt(text, index) != 127988) return false;
            index += 2;
            for (int count = 0; count < 5; count++)
            {
                int cp = CodePointAt(text, index);
                if (cp < 917601 || cp > 917626) return false;
                index += 2;
            }
            return CodePointAt(text, index) == 917631;
        }

        static bool IsModifier(int a, int b) => a == 0xD83C && b >= 0xDFFB && b <= 0xDFFF;

        /// <summary>lottie-web TextProperty.buildFinalText: grapheme-ish splitting.</summary>
        static List<string> BuildFinalText(string text)
        {
            var result = new List<string>();
            int i = 0, len = text.Length;
            bool shouldCombineNext = false;
            while (i < len)
            {
                bool shouldCombine = shouldCombineNext;
                shouldCombineNext = false;
                int charCode = text[i];
                string current = text.Substring(i, 1);
                if (Array.IndexOf(CombinedCharacters, charCode) >= 0) shouldCombine = true;
                else if (charCode >= 0xD800 && charCode <= 0xDBFF)
                {
                    if (IsRegionalFlag(text, i)) current = text.Substring(i, Math.Min(14, len - i));
                    else if (i + 1 < len)
                    {
                        int second = text[i + 1];
                        if (second >= 0xDC00 && second <= 0xDFFF)
                        {
                            if (IsModifier(charCode, second))
                            {
                                current = text.Substring(i, 2);
                                shouldCombine = true;
                            }
                            else if (i + 3 < len && IsRegionalCode(text, i) && IsRegionalCode(text, i + 2)) current = text.Substring(i, 4);
                            else current = text.Substring(i, 2);
                        }
                    }
                }
                else if (charCode > 0xDBFF)
                {
                    if (charCode == 65039) shouldCombine = true;
                }
                else if (charCode == 8205)
                {
                    shouldCombine = true;
                    shouldCombineNext = true;
                }
                if (shouldCombine && result.Count > 0) result[result.Count - 1] += current;
                else result.Add(current);
                i += current.Length;
            }
            return result;
        }

        static float4 ReadTextColor(JsonValue c)
        {
            if (!c.IsArray) return new float4(0, 0, 0, 1);
            var v = c.ToFloatArray();
            float r = v.Length > 0 ? v[0] : 0, g = v.Length > 1 ? v[1] : 0, b = v.Length > 2 ? v[2] : 0;
            if (r > 1 || g > 1 || b > 1) { r /= 255f; g /= 255f; b /= 255f; }
            return new float4(r, g, b, 1);
        }

        int CompileTextDocument(JsonValue s, List<JsonValue> animators, int grouping)
        {
            var font = FontByName(s["f"].AsString(""));
            string text = s["t"].AsString("") ?? "";
            int caps = s["ca"].AsInt(0);
            if (caps == 1) text = text.ToUpperInvariant();
            float size = s["s"].AsFloat(10);
            float lh = s["lh"].AsFloat(0);
            float tr = s["tr"].AsFloat(0);
            float ls = s["ls"].AsFloat(0);
            int j = s["j"].AsInt(0);
            bool hasBox = s["sz"].IsArray && s["sz"].Count >= 2;
            float2 boxSize = hasBox ? new float2(s["sz"][0].AsFloat(), s["sz"][1].AsFloat()) : float2.zero;
            float2 boxPos = s["ps"].IsArray && s["ps"].Count >= 2 ? new float2(s["ps"][0].AsFloat(), s["ps"][1].AsFloat()) : float2.zero;

            float finalSize = size;
            float finalLineHeight = lh;
            var finalText = BuildFinalText(text);
            float trackingOffset = tr / 1000f * finalSize;

            if (hasBox)
            {
                // Word wrap into the paragraph box (canResize is off: single pass).
                float boxWidth = boxSize.x;
                var ft = BuildFinalText(text);
                float lineWidth = 0;
                int lastSpaceIndex = -1;
                int len = ft.Count;
                for (int i = 0; i < len; i++)
                {
                    int charCode = ft[i].Length > 0 ? ft[i][0] : 0;
                    bool newLine = false;
                    if (ft[i] == " ") lastSpaceIndex = i;
                    else if (charCode == 13 || charCode == 3)
                    {
                        lineWidth = 0;
                        newLine = true;
                    }
                    GlyphFor(font, ft[i], out float adv);
                    float cLength = newLine ? 0 : adv * finalSize / 100f;
                    if (lineWidth + cLength > boxWidth && ft[i] != " ")
                    {
                        if (lastSpaceIndex == -1) len += 1;
                        else i = lastSpaceIndex;
                        if (lastSpaceIndex == i) ft[i] = "\r";
                        else ft.Insert(i, "\r");
                        lastSpaceIndex = -1;
                        lineWidth = 0;
                    }
                    else
                    {
                        lineWidth += cLength;
                        lineWidth += trackingOffset;
                    }
                }
                finalText = ft;
            }

            var doc = new TextDocData
            {
                LetterFirst = _textLetters.Count,
                Size = finalSize,
                Tracking = tr,
                BaselineShift = ls,
                Justify = (byte)j,
                HasBox = (byte)(hasBox ? 1 : 0),
                BoxPos = boxPos,
                StrokeWidth = s["sw"].AsFloat(0),
                StrokeOverFill = (byte)(s["of"].AsBool(true) ? 1 : 0),
            };
            if (s.Has("fc")) { doc.HasFill = 1; doc.FillColor = ReadTextColor(s["fc"]); }
            if (s.Has("sc")) { doc.HasStroke = 1; doc.StrokeColor = ReadTextColor(s["sc"]); }

            int count = finalText.Count;
            var letters = new List<TextLetter>(count);
            var lineWidths = new List<float>();
            float lw = -trackingOffset;
            float maxLineWidth = 0;
            float uncollapsedSpaces = 0;
            int currentLine = 0, index = 0, currentPos = 0;
            float currentSize = 0;
            for (int i = 0; i < count; i++)
            {
                string currentChar = finalText[i];
                int charCode = currentChar.Length > 0 ? currentChar[0] : 0;
                bool newLine = false;
                string val;
                if (charCode == 13 || charCode == 3)
                {
                    uncollapsedSpaces = 0;
                    lineWidths.Add(lw);
                    maxLineWidth = Math.Max(maxLineWidth, lw);
                    lw = -2 * trackingOffset;
                    val = "";
                    newLine = true;
                    currentLine++;
                }
                else val = currentChar;
                int glyph = GlyphFor(font, currentChar, out float adv);
                float cLength = newLine ? 0 : adv * finalSize / 100f;
                if (currentChar == " ") uncollapsedSpaces += cLength + trackingOffset;
                else
                {
                    lw += cLength + trackingOffset + uncollapsedSpaces;
                    uncollapsedSpaces = 0;
                }
                letters.Add(new TextLetter
                {
                    Glyph = newLine ? -1 : glyph,
                    L = cLength,
                    An = cLength,
                    Add = currentSize,
                    NewLine = (byte)(newLine ? 1 : 0),
                    Line = currentLine,
                });
                if (grouping == 2)
                {
                    currentSize += cLength;
                    if (val == "" || val == " " || i == count - 1)
                    {
                        if (val == "" || val == " ") currentSize -= cLength;
                        while (currentPos <= i)
                        {
                            var lt = letters[currentPos];
                            lt.An = currentSize;
                            lt.Ind = index;
                            lt.Extra = cLength;
                            letters[currentPos] = lt;
                            currentPos++;
                        }
                        index++;
                        currentSize = 0;
                    }
                }
                else if (grouping == 3)
                {
                    currentSize += cLength;
                    if (val == "" || i == count - 1)
                    {
                        if (val == "") currentSize -= cLength;
                        while (currentPos <= i)
                        {
                            var lt = letters[currentPos];
                            lt.An = currentSize;
                            lt.Ind = index;
                            lt.Extra = cLength;
                            letters[currentPos] = lt;
                            currentPos++;
                        }
                        currentSize = 0;
                        index++;
                    }
                }
                else
                {
                    var lt = letters[index];
                    lt.Ind = index;
                    lt.Extra = 0;
                    letters[index] = lt;
                    index++;
                }
            }
            maxLineWidth = Math.Max(maxLineWidth, lw);
            lineWidths.Add(lw);
            if (hasBox)
            {
                doc.BoxWidth = boxSize.x;
                doc.JustifyOffset = 0;
            }
            else
            {
                doc.BoxWidth = maxLineWidth;
                doc.JustifyOffset = j == 1 ? -doc.BoxWidth : j == 2 ? -doc.BoxWidth / 2 : 0;
            }

            // Animator indexes (based on characters / words / lines).
            int nAnim = animators.Count;
            var anIndexes = new int[count * Math.Max(1, nAnim)];
            doc.AnimatorTotalFirst = _textInts.Count;
            var totals = new int[nAnim];
            var rng = new System.Random(unchecked(text.GetHashCode() * 31 + count));
            for (int a = 0; a < nAnim; a++)
            {
                var sel = FirstSelector(animators[a]["s"]);
                int based = sel["b"].AsInt(1);
                bool randomize = sel["rn"].AsInt(0) == 1;
                var aa = animators[a]["a"];
                if (aa.Has("sc")) doc.StrokeAnim = 1;
                if (aa.Has("sw")) doc.StrokeWidthAnim = 1;
                if (aa.Has("fc") || aa.Has("fh") || aa.Has("fs") || aa.Has("fb")) doc.FillAnim = 1;
                int ind = 0;
                var indexes = new List<int>();
                for (int i = 0; i < count; i++)
                {
                    var lt = letters[i];
                    string val = lt.NewLine != 0 ? "" : finalText[i];
                    anIndexes[i * nAnim + a] = ind;
                    bool counts = based == 1 && val != "" ||
                                  based == 2 && val != "" && val != " " ||
                                  based == 3 && (lt.NewLine != 0 || val == " " || i == count - 1) ||
                                  based == 4 && (lt.NewLine != 0 || i == count - 1);
                    if (counts)
                    {
                        if (randomize) indexes.Add(ind);
                        ind++;
                    }
                }
                totals[a] = ind;
                if (randomize)
                {
                    int currentInd = -1, newInd = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int k = anIndexes[i * nAnim + a];
                        if (currentInd != k)
                        {
                            currentInd = k;
                            if (indexes.Count > 0)
                            {
                                int pick = rng.Next(indexes.Count);
                                newInd = indexes[pick];
                                indexes.RemoveAt(pick);
                            }
                        }
                        anIndexes[i * nAnim + a] = newInd;
                    }
                }
            }
            for (int a = 0; a < nAnim; a++) _textInts.Add(totals[a]);
            for (int i = 0; i < count; i++)
            {
                var lt = letters[i];
                lt.AnIndexFirst = _textInts.Count;
                for (int a = 0; a < nAnim; a++) _textInts.Add(anIndexes[i * nAnim + a]);
                _textLetters.Add(lt);
            }
            doc.LetterCount = count;
            doc.LineFirst = _textFloats.Count;
            doc.LineCount = lineWidths.Count;
            _textFloats.AddRange(lineWidths);
            doc.YOffset = finalLineHeight != 0 ? finalLineHeight : finalSize * 1.2f;
            doc.Ascent = font.Ascent * finalSize / 100f;
            _textDocs.Add(doc);
            return _textDocs.Count - 1;
        }

        void ExportText(LottieData data)
        {
            data.Glyphs = _glyphs.ToArray();
            data.GlyphContours = _glyphContours.ToArray();
            data.GlyphPoints = _glyphPoints.ToArray();
            data.TextDocs = _textDocs.ToArray();
            data.TextLetters = _textLetters.ToArray();
            data.TextInts = _textInts.ToArray();
            data.TextFloats = _textFloats.ToArray();
            data.TextDocKeys = _textDocKeys.ToArray();
            data.TextAnimators = _textAnimators.ToArray();
            data.TextSelectors = _textSelectors.ToArray();
            data.TextLayers = _textLayers.ToArray();
        }
    }
}
