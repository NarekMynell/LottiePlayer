// Mynell Lottie Player — Lottie (BodyMovin) JSON parser → blittable blob.
// Runs in Editor (importer) or at runtime (one-shot init). NOT in the hot path.
// Supports a focused, useful subset: Shape layers, groups, paths, rects, ellipses,
// solid fills, transforms, and animated keyframes with cubic-bezier easing.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Mynell.LottiePlayer
{
    public static class LottieParser
    {
        /// <summary>Convenience overload that discards the validation report.</summary>
        public static LottieAnimationBlob Parse(string json) => Parse(json, out _);

        /// <summary>
        /// Parse Lottie JSON into a runtime blob, returning a <see cref="LottieValidationReport"/>
        /// with parse-time diagnostics (dropped snapshots, unsupported layers/shapes, animated-path
        /// degradations). Importers should persist the report into the LottieAsset.
        /// </summary>
        public static LottieAnimationBlob Parse(string json, out LottieValidationReport report)
        {
            if (string.IsNullOrEmpty(json))
                throw new ArgumentException("Empty Lottie JSON");

            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (root == null) throw new Exception("Invalid JSON (no root object)");
            return ParseRoot(root, out report);
        }

        /// <summary>Parse an already-deserialized Lottie root object (used after bake-time preprocessing).</summary>
        public static LottieAnimationBlob ParseRoot(Dictionary<string, object> root, out LottieValidationReport report)
        {
            if (root == null) throw new ArgumentException("Null Lottie root");

            var ctx = new ParseContext();
            ctx.Header = new LottieHeader
            {
                FrameRate = ReadFloat(root, "fr", 30f),
                InFrame = ReadFloat(root, "ip", 0f),
                OutFrame = ReadFloat(root, "op", 60f),
                Width = ReadFloat(root, "w", 512f),
                Height = ReadFloat(root, "h", 512f),
                LayerOffset = 0,
                LayerCount = 0,
            };

            // Phase 1: pre-collect asset dictionaries (precomps + images). Parse lazily.
            if (root.TryGetValue("assets", out var assetsObj) && assetsObj is List<object> assets)
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    if (!(assets[i] is Dictionary<string, object> ad)) continue;
                    string id = GetString(ad, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!ad.ContainsKey("layers")) continue; // images skipped
                    ctx.AssetDicts[id] = ad;
                }
            }

            // Phase 2: parse top-level layers. Top-level range stays contiguous at [0, topCount).
            int topCount = 0;
            int precompLayerCount = 0;
            if (root.TryGetValue("layers", out var layersObj) && layersObj is List<object> layersList)
            {
                var pendingRefs = new List<(int layerIndex, string refId)>();
                var indMap = new Dictionary<int, int>();
                var hierarchyEntries = new List<LayerHierarchyEntry>();

                for (int i = 0; i < layersList.Count; i++)
                {
                    var layerDict = layersList[i] as Dictionary<string, object>;
                    if (layerDict == null) continue;
                    int ty = (int)ReadFloat(layerDict, "ty", 4f);
                    int layerIdx = ctx.Layers.Count;
                    int ind = ReadIntOrDefault(layerDict, "ind", -1);
                    int parentInd = ReadIntOrDefault(layerDict, "parent", -1);

                    ctx.Layers.Add(ParseLayer(layerDict, ctx, ind, parentInd));
                    topCount++;

                    if (ind >= 0) indMap[ind] = layerIdx;
                    hierarchyEntries.Add(new LayerHierarchyEntry
                    {
                        LocalIdx = layerIdx,
                        Ind = ind,
                        ParentInd = parentInd,
                        Name = GetString(layerDict, "nm") ?? "unnamed",
                    });

                    if (ty == 0) // Precomp
                    {
                        string refId = GetString(layerDict, "refId");
                        if (!string.IsNullOrEmpty(refId)) pendingRefs.Add((layerIdx, refId));
                    }
                    else if (ty != 4 && ty != 3) // Null layers are valid as parent transforms
                    {
                        ctx.Report.UnsupportedLayerCount++;
                        Debug.LogWarning($"[Mynell.Lottie] Layer '{GetString(layerDict, "nm")}' has ty={ty} ({LayerTypeName(ty)}) — not rendered.");
                    }
                }
                ctx.Header.LayerOffset = 0;
                ctx.Header.LayerCount = topCount;

                ResolveLayerHierarchy(hierarchyEntries, indMap, layerOffset: 0, ctx);

                // Phase 3: resolve precomp refs. EnsurePrecomp materializes precomp layers AFTER top-level,
                // which keeps the top-level range contiguous (and may recursively add nested precomps).
                foreach (var (layerIdx, refId) in pendingRefs)
                {
                    int pcIdx = EnsurePrecomp(refId, ctx);
                    if (pcIdx < 0)
                    {
                        Debug.LogWarning($"[Mynell.Lottie] Precomp refId='{refId}' not found in assets.");
                        continue;
                    }
                    var l = ctx.Layers[layerIdx];
                    l.PrecompId = pcIdx;
                    ctx.Layers[layerIdx] = l;
                }
                precompLayerCount = ctx.Layers.Count - topCount;
                if (ctx.Report.UnsupportedLayerCount > 0)
                    Debug.LogWarning($"[Mynell.Lottie] {ctx.Report.UnsupportedLayerCount} layer(s) will not render (unsupported types).");
            }

            // Finalize parse-time diagnostics.
            ctx.Report.TotalLayers = ctx.Layers.Count;
            ctx.Report.TotalShapes = ctx.Shapes.Count;
            ctx.Report.TotalPaths = ctx.PathProperties.Count;
            ctx.Report.UnsupportedShapeCount = ctx.UnsupportedShapeCount;
            report = ctx.Report;

#if UNITY_EDITOR
            Debug.Log($"[Mynell.Lottie] Parsed: top={topCount}, precompLayers={precompLayerCount}, precomps={ctx.Precomps.Count}, shapes={ctx.Shapes.Count} (unsup={ctx.UnsupportedShapeCount}, approx={ctx.Report.ApproximatedShapes}), paths={ctx.PathProperties.Count}, props={ctx.Properties.Count}, kfs={ctx.Keyframes.Count}, dropSnap={ctx.Report.DroppedPathSnapshots}, degrAnim={ctx.Report.DegradedAnimatedPaths}. Size={ctx.Header.Width}x{ctx.Header.Height} @ {ctx.Header.FrameRate}fps");
#endif
            return ctx.ToBlob();
        }

        /// <summary>
        /// Lazily materialize a precomp. Recursively parses nested precomps.
        /// Returns the index into ctx.Precomps, or -1 if asset missing.
        /// </summary>
        private static int EnsurePrecomp(string refId, ParseContext ctx)
        {
            if (ctx.ParsedPrecompIndex.TryGetValue(refId, out int existing)) return existing;
            if (!ctx.AssetDicts.TryGetValue(refId, out var assetDict)) return -1;

            // Reserve index FIRST (handles cyclic references, which shouldn't exist but defensive).
            int newIdx = ctx.Precomps.Count;
            ctx.Precomps.Add(new LottiePrecomp { LayerOffset = -1, LayerCount = 0 });
            ctx.ParsedPrecompIndex[refId] = newIdx;

            int layerStart = ctx.Layers.Count;
            int parsedCount = 0; // BUG-FIX: distinguishes this precomp's own layers from nested ones appended by phase 2.

            if (assetDict.TryGetValue("layers", out var lyrObj) && lyrObj is List<object> lyrList)
            {
                var nestedRefs = new List<(int layerIndex, string refId)>();
                var indMap = new Dictionary<int, int>();
                var hierarchyEntries = new List<LayerHierarchyEntry>();

                for (int i = 0; i < lyrList.Count; i++)
                {
                    if (!(lyrList[i] is Dictionary<string, object> ldd)) continue;
                    int ty = (int)ReadFloat(ldd, "ty", 4f);
                    int globalIdx = ctx.Layers.Count;
                    int localIdx = globalIdx - layerStart;
                    int ind = ReadIntOrDefault(ldd, "ind", -1);
                    int parentInd = ReadIntOrDefault(ldd, "parent", -1);

                    ctx.Layers.Add(ParseLayer(ldd, ctx, ind, parentInd));
                    parsedCount++;

                    if (ind >= 0) indMap[ind] = localIdx;
                    hierarchyEntries.Add(new LayerHierarchyEntry
                    {
                        LocalIdx = localIdx,
                        Ind = ind,
                        ParentInd = parentInd,
                        Name = GetString(ldd, "nm") ?? "unnamed",
                    });

                    if (ty == 0)
                    {
                        string nestedRef = GetString(ldd, "refId");
                        if (!string.IsNullOrEmpty(nestedRef)) nestedRefs.Add((globalIdx, nestedRef));
                    }
                    else if (ty != 4 && ty != 3)
                    {
                        ctx.Report.UnsupportedLayerCount++;
                    }
                }

                ResolveLayerHierarchy(hierarchyEntries, indMap, layerOffset: layerStart, ctx);

                // Phase 2: nested precomp refs. May append additional layers to ctx.Layers — those
                // belong to OTHER precomp ranges and must NOT be counted as ours.
                foreach (var (layerIdx, nrId) in nestedRefs)
                {
                    int nestedIdx = EnsurePrecomp(nrId, ctx);
                    if (nestedIdx < 0) continue;
                    var l = ctx.Layers[layerIdx];
                    l.PrecompId = nestedIdx;
                    ctx.Layers[layerIdx] = l;
                }
            }

            var pc = ctx.Precomps[newIdx];
            pc.LayerOffset = layerStart;
            pc.LayerCount = parsedCount; // CRITICAL: must NOT include nested-precomp layers.
            ctx.Precomps[newIdx] = pc;
            return newIdx;
        }

        /// <summary>Parse-time record for layer hierarchy resolution (parent links only).</summary>
        private struct LayerHierarchyEntry
        {
            public int LocalIdx;
            public int Ind;
            public int ParentInd;
            public string Name;
        }

        /// <summary>
        /// Resolve <see cref="LottieLayer.ParentLocalIndex"/> from ind/parent links.
        /// Transform properties stay as raw baked indices — matrix math runs at runtime in
        /// <see cref="BuildDrawCommandsJob"/>.
        /// </summary>
        private static void ResolveLayerHierarchy(
            List<LayerHierarchyEntry> entries,
            Dictionary<int, int> indMap,
            int layerOffset,
            ParseContext ctx)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.ParentInd < 0) continue;
                if (!indMap.TryGetValue(e.ParentInd, out int parentLocal))
                {
                    Debug.LogWarning($"[Mynell.Lottie] Layer '{e.Name}' (ind={e.Ind}) parent ind={e.ParentInd} not found in composition.");
                    continue;
                }
                if (parentLocal == e.LocalIdx) continue;

                var l = ctx.Layers[layerOffset + e.LocalIdx];
                l.ParentLocalIndex = parentLocal;
                ctx.Layers[layerOffset + e.LocalIdx] = l;
            }
        }

        private static int ReadIntOrDefault(Dictionary<string, object> d, string key, int def)
        {
            if (d == null || !d.TryGetValue(key, out var o) || o == null) return def;
            try { return (int)Convert.ToDouble(o); } catch { return def; }
        }

        private static string LayerTypeName(int ty) => ty switch
        {
            0 => "Precomp", 1 => "Solid", 2 => "Image", 3 => "Null",
            4 => "Shape", 5 => "Text", _ => $"Unknown({ty})",
        };

        // -------- Context with growing managed lists, baked to NativeArrays at the end. --------

        internal sealed class ParseContext
        {
            public LottieHeader Header;
            public List<LottieLayer> Layers = new List<LottieLayer>(8);
            public List<LottieShape> Shapes = new List<LottieShape>(64);
            public List<LottieProperty> Properties = new List<LottieProperty>(64);
            public List<LottieKeyframe> Keyframes = new List<LottieKeyframe>(64);
            public List<LottiePathProperty> PathProperties = new List<LottiePathProperty>(16);
            public List<LottiePathSnapshot> PathSnapshots = new List<LottiePathSnapshot>(16);
            public List<LottieBezierVertex> BezierVertices = new List<LottieBezierVertex>(512);
            public List<float> ScalarPool = new List<float>(128);
            public List<float2> Vec2Pool = new List<float2>(64);
            public List<float4> Vec4Pool = new List<float4>(16);
            public List<LottiePrecomp> Precomps = new List<LottiePrecomp>(4);
            public Dictionary<string, Dictionary<string, object>> AssetDicts = new Dictionary<string, Dictionary<string, object>>();
            public Dictionary<string, int> ParsedPrecompIndex = new Dictionary<string, int>();
            public int UnsupportedShapeCount;
            // Mutable parse-time report — counters are bumped throughout parsing,
            // then the final values are copied out by Parse().
            public LottieValidationReport Report;

            public LottieAnimationBlob ToBlob()
            {
                var blob = new LottieAnimationBlob
                {
                    Header = Header,
                    Layers = ToNative(Layers),
                    Precomps = ToNative(Precomps),
                    Shapes = ToNative(Shapes),
                    Properties = ToNative(Properties),
                    Keyframes = ToNative(Keyframes),
                    PathProperties = ToNative(PathProperties),
                    PathSnapshots = ToNative(PathSnapshots),
                    BezierVertices = ToNative(BezierVertices),
                    ScalarPool = ToNative(ScalarPool),
                    Vec2Pool = ToNative(Vec2Pool),
                    Vec4Pool = ToNative(Vec4Pool),
                };
                return blob;
            }

            private static NativeArray<T> ToNative<T>(List<T> list) where T : unmanaged
            {
                // See LottieAsset.BytesToNative comment: zero-length NativeArrays can break
                // job safety. Always allocate >= 1; consumers use stored counts.
                int allocLen = list.Count > 0 ? list.Count : 1;
                var arr = new NativeArray<T>(allocLen, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                for (int i = 0; i < list.Count; i++) arr[i] = list[i];
                return arr;
            }
        }

        // ---------------------------- Layer ----------------------------

        private static LottieLayer ParseLayer(Dictionary<string, object> d, ParseContext ctx, int layerInd, int parentInd)
        {
            // Fallback to animation in/out frames when layer doesn't specify ip/op
            // (otherwise default 0/0 would make the layer permanently inactive at runtime).
            float defIn = ctx.Header.InFrame;
            float defOut = ctx.Header.OutFrame;
            if (d.TryGetValue("td", out var tdObj) && ReadFloatFromObj(tdObj, 0f) > 0f)
            {
                return new LottieLayer
                {
                    Kind = (LottieLayerKind)(int)ReadFloat(d, "ty", 4f),
                    IsTrackMatte = 1,
                    InFrame = ReadFloat(d, "ip", defIn),
                    OutFrame = ReadFloat(d, "op", defOut),
                    StartFrame = ReadFloat(d, "st", 0f),
                    StretchTimeMul = 1f / Mathf.Max(0.0001f, ReadFloat(d, "sr", 1f)),
                    LayerInd = layerInd,
                    ParentInd = parentInd,
                    ParentLocalIndex = -1,
                };
            }

            var layer = new LottieLayer
            {
                Kind = (LottieLayerKind)(int)ReadFloat(d, "ty", 4f),
                InFrame = ReadFloat(d, "ip", defIn),
                OutFrame = ReadFloat(d, "op", defOut),
                StartFrame = ReadFloat(d, "st", 0f),
                StretchTimeMul = 1f / Mathf.Max(0.0001f, ReadFloat(d, "sr", 1f)),
                ShapeOffset = ctx.Shapes.Count,
                ShapeCount = 0,
                PrecompId = -1,
                PrecompWidth = ReadFloat(d, "w", 0f),
                PrecompHeight = ReadFloat(d, "h", 0f),
                LayerInd = layerInd,
                ParentInd = parentInd,
                ParentLocalIndex = -1,
                AnchorProp = -1,
                PositionProp = -1,
                ScaleProp = -1,
                RotationProp = -1,
                OpacityProp = -1,
            };
            // Final guard: if OutFrame is still bogus, force animation range.
            if (layer.OutFrame <= layer.InFrame)
            {
                layer.InFrame = defIn;
                layer.OutFrame = defOut > defIn ? defOut : defIn + 1f;
            }

            // Transform
            if (d.TryGetValue("ks", out var ksObj) && ksObj is Dictionary<string, object> ks)
            {
                layer.AnchorProp = ParseVec2Property(GetDict(ks, "a"), ctx);
                layer.PositionProp = ParseVec2Property(GetDict(ks, "p"), ctx);
                layer.ScaleProp = ParseVec2Property(GetDict(ks, "s"), ctx);
                layer.RotationProp = ParseScalarProperty(GetDict(ks, "r"), ctx);
                layer.OpacityProp = ParseScalarProperty(GetDict(ks, "o"), ctx, asPercent: true);
            }

            // Shapes (only for ty=4)
            if (layer.Kind == LottieLayerKind.Shape && d.TryGetValue("shapes", out var shapesObj) && shapesObj is List<object> shapesList)
            {
                int before = ctx.Shapes.Count;
                ParseShapeArray(shapesList, ctx);
                layer.ShapeOffset = before;
                layer.ShapeCount = ctx.Shapes.Count - before;
            }

            return layer;
        }

        // ---------------------------- Shapes ----------------------------

        private static void ParseShapeArray(List<object> arr, ParseContext ctx)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is Dictionary<string, object> dict)
                    ParseShapeItem(dict, ctx);
            }
        }

        private static void ParseShapeItem(Dictionary<string, object> dict, ParseContext ctx)
        {
            string ty = GetString(dict, "ty");
            if (string.IsNullOrEmpty(ty)) return;

            switch (ty)
            {
                case "gr": ParseGroup(dict, ctx); break;
                case "sh": ParseShapePath(dict, ctx); break;
                case "rc": ParseRect(dict, ctx); break;
                case "el": ParseEllipse(dict, ctx); break;
                case "fl": ParseFill(dict, ctx); break;
                case "st": ParseStroke(dict, ctx); break;
                case "tr": ParseTransform(dict, ctx); break;
                case "tm": ParseTrim(dict, ctx); break;
                case "gf": ParseGradientFillAsSolid(dict, ctx); break;
                case "gs": ParseGradientStrokeAsSolid(dict, ctx); break;
                case "mm": ctx.Report.ApproximatedShapes++; break;
                default:
                    ctx.UnsupportedShapeCount++;
                    Debug.LogWarning($"[Mynell.Lottie] Shape type '{ty}' not supported (skipped). Common unsupported: sr=star, rp=repeater.");
                    break;
            }
        }

        /// <summary>
        /// Shape group: hoist <c>tr</c> to the front of the child list so the renderer can
        /// bind one transform per group. Group <c>tr</c> is never merged into layer <c>ks</c>
        /// at parse time — Stage 1 composes <c>layer.ks</c> then each group's own <c>tr</c>
        /// when emitting that group's geometry (Lottie: T(p) · R · S/100 · T(-a) per transform).
        /// </summary>
        private static void ParseGroup(Dictionary<string, object> d, ParseContext ctx)
        {
            int slot = ctx.Shapes.Count;
            ctx.Shapes.Add(new LottieShape { Kind = LottieShapeKind.Group });
            int childStart = ctx.Shapes.Count;

            if (d.TryGetValue("it", out var itObj) && itObj is List<object> it)
            {
                // First pass: find and parse the transform
                for (int i = 0; i < it.Count; i++)
                {
                    if (it[i] is Dictionary<string, object> trDict && GetString(trDict, "ty") == "tr")
                    {
                        ParseTransform(trDict, ctx);
                        break;
                    }
                }
                
                // If no transform was found, add an identity transform to maintain the structure
                if (ctx.Shapes.Count == childStart)
                {
                    ctx.Shapes.Add(new LottieShape
                    {
                        Kind = LottieShapeKind.Transform,
                        P0 = AppendConstVec2(float2.zero, ctx), // anchor
                        P1 = AppendConstVec2(float2.zero, ctx), // position
                        P2 = AppendConstVec2(new float2(100f, 100f), ctx), // scale
                        P3 = AppendConstScalar(0f, ctx), // rotation
                        P4 = AppendConstScalar(1f, ctx), // opacity
                    });
                }

                // Second pass: parse the rest of the children
                for (int i = 0; i < it.Count; i++)
                {
                    if (!(it[i] is Dictionary<string, object> childDict)) continue;
                    if (GetString(childDict, "ty") == "tr") continue;
                    ParseShapeItem(childDict, ctx);
                }
            }

            var s = ctx.Shapes[slot];
            s.ChildOffset = childStart;
            s.ChildCount = ctx.Shapes.Count - childStart;
            ctx.Shapes[slot] = s;
        }

        private static void ParseShapePath(Dictionary<string, object> d, ParseContext ctx)
        {
            int pp = ParsePathProperty(GetDict(d, "ks"), ctx);
            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Path,
                Direction = (byte)(int)ReadFloat(d, "d", 1f),
                P0 = pp,
                P1 = -1, P2 = -1, P3 = -1, P4 = -1,
            });
        }

        private static void ParseRect(Dictionary<string, object> d, ParseContext ctx)
        {
            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Rect,
                Direction = (byte)(int)ReadFloat(d, "d", 1f),
                P0 = ParseVec2Property(GetDict(d, "p"), ctx),
                P1 = ParseVec2Property(GetDict(d, "s"), ctx),
                P2 = ParseScalarProperty(GetDict(d, "r"), ctx),
                P3 = -1, P4 = -1,
            });
        }

        private static void ParseEllipse(Dictionary<string, object> d, ParseContext ctx)
        {
            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Ellipse,
                Direction = (byte)(int)ReadFloat(d, "d", 1f),
                P0 = ParseVec2Property(GetDict(d, "p"), ctx),
                P1 = ParseVec2Property(GetDict(d, "s"), ctx),
                P2 = -1, P3 = -1, P4 = -1,
            });
        }

        private static void ParseFill(Dictionary<string, object> d, ParseContext ctx)
        {
            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Fill,
                P0 = ParseColorProperty(GetDict(d, "c"), ctx),
                P1 = ParseScalarProperty(GetDict(d, "o"), ctx, asPercent: true),
                P2 = -1, P3 = -1, P4 = -1,
            });
        }

        private static void ParseStroke(Dictionary<string, object> d, ParseContext ctx)
        {
            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Stroke,
                P0 = ParseColorProperty(GetDict(d, "c"), ctx),
                P1 = ParseScalarProperty(GetDict(d, "o"), ctx, asPercent: true),
                P2 = ParseScalarProperty(GetDict(d, "w"), ctx), // stroke width
                P3 = -1, P4 = -1,
            });
        }

        private static void ParseTransform(Dictionary<string, object> d, ParseContext ctx)
        {
            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Transform,
                P0 = ParseVec2Property(GetDict(d, "a"), ctx),
                P1 = ParseVec2Property(GetDict(d, "p"), ctx),
                P2 = ParseVec2Property(GetDict(d, "s"), ctx),
                P3 = ParseScalarProperty(GetDict(d, "r"), ctx),
                P4 = ParseScalarProperty(GetDict(d, "o"), ctx, asPercent: true),
            });
        }

        /// <summary>
        /// Trim Path modifier ("tm"). Stored as a sibling shape; renderer collects it during the
        /// group's first pass and applies to subsequent path emissions.
        /// P0 = start% (0..100), P1 = end% (0..100), P2 = offset deg, P3 = mode (1 simultaneous / 2 individual).
        /// </summary>
        private static void ParseTrim(Dictionary<string, object> d, ParseContext ctx)
        {
            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.TrimPath,
                P0 = ParseScalarProperty(GetDict(d, "s"), ctx, asPercent: true),
                P1 = ParseScalarProperty(GetDict(d, "e"), ctx, asPercent: true),
                P2 = ParseScalarProperty(GetDict(d, "o"), ctx),
                P3 = (int)ReadFloat(d, "m", 1f),
                P4 = -1,
            });
        }

        /// <summary>
        /// Gradient fill ("gf"). Approximated as a solid fill using the FIRST gradient stop's
        /// color. Acceptable fallback (better than skipping) — proper per-vertex / per-pixel
        /// gradient sampling is out of scope for the v1 mesh pipeline.
        /// Lottie stop encoding: flat float[] = [pos0, r0, g0, b0, pos1, r1, g1, b1, ...].
        /// </summary>
        private static void ParseGradientFillAsSolid(Dictionary<string, object> d, ParseContext ctx)
        {
            float4 firstColor = new float4(1, 1, 1, 1);
            var g = GetDict(d, "g");
            var k = GetDict(g, "k");
            if (k != null && k.TryGetValue("k", out var stopsObj))
            {
                List<object> stops = ExtractFirstGradientStops(stopsObj);
                if (stops != null && stops.Count >= 4)
                {
                    firstColor = NormalizeColor(new float4(
                        SafeFloat(stops[1]),
                        SafeFloat(stops[2]),
                        SafeFloat(stops[3]),
                        1f));
                }
            }

            int colorProp = AppendConstVec4(firstColor, ctx);
            int opacityProp = ParseScalarProperty(GetDict(d, "o"), ctx, asPercent: true);

            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Fill,
                P0 = colorProp,
                P1 = opacityProp,
                P2 = -1, P3 = -1, P4 = -1,
            });
            ctx.Report.ApproximatedShapes++;
        }

        /// <summary>
        /// Gradient stroke ("gs"). Mirror of <see cref="ParseGradientFillAsSolid"/> but
        /// emitting a <see cref="LottieShapeKind.Stroke"/>: per-pixel gradient sampling
        /// is out of scope, so the stroke is baked to the FIRST gradient stop's color
        /// and rendered through the existing quad-extruder. Visually 100% better than
        /// the previous "drop the whole stroke" behaviour. Width / opacity / cap-join
        /// follow the same conventions as a regular 'st' stroke.
        /// </summary>
        private static void ParseGradientStrokeAsSolid(Dictionary<string, object> d, ParseContext ctx)
        {
            float4 firstColor = new float4(1, 1, 1, 1);
            var g = GetDict(d, "g");
            var k = GetDict(g, "k");
            if (k != null && k.TryGetValue("k", out var stopsObj))
            {
                List<object> stops = ExtractFirstGradientStops(stopsObj);
                if (stops != null && stops.Count >= 4)
                {
                    firstColor = NormalizeColor(new float4(
                        SafeFloat(stops[1]),
                        SafeFloat(stops[2]),
                        SafeFloat(stops[3]),
                        1f));
                }
            }

            int colorProp = AppendConstVec4(firstColor, ctx);
            int opacityProp = ParseScalarProperty(GetDict(d, "o"), ctx, asPercent: true);
            // 'w' (width) is mandatory on every Lottie stroke; if missing fall back to 1px.
            int widthProp = ParseScalarProperty(GetDict(d, "w"), ctx);
            if (widthProp < 0) widthProp = AppendConstScalar(1f, ctx);

            ctx.Shapes.Add(new LottieShape
            {
                Kind = LottieShapeKind.Stroke,
                P0 = colorProp,
                P1 = opacityProp,
                P2 = widthProp,
                P3 = -1, P4 = -1,
            });
            ctx.Report.ApproximatedShapes++;
        }

        private static List<object> ExtractFirstGradientStops(object stopsObj)
        {
            // Either a flat list of floats (constant), or list-of-keyframes (animated → take first 's').
            if (!(stopsObj is List<object> list) || list.Count == 0) return null;
            if (list[0] is Dictionary<string, object> kfd)
            {
                if (kfd.TryGetValue("s", out var sObj) && sObj is List<object> sList) return sList;
                return null;
            }
            return list;
        }

        private static float SafeFloat(object o)
        {
            if (o == null) return 0f;
            try { return (float)Convert.ToDouble(o); } catch { return 0f; }
        }

        // ---------------------------- Properties ----------------------------

        /// <summary>
        /// True if the property is an animated array of keyframes.
        /// Robust against missing 'a' (some exporters omit it but supply object-array as k).
        /// </summary>
        private static bool IsAnimated(Dictionary<string, object> p)
        {
            if (p == null) return false;
            if (ReadFloat(p, "a", -1f) > 0.5f) return true;
            // Heuristic: if 'k' is a List of Dictionaries, it's keyframed.
            if (p.TryGetValue("k", out var k) && k is List<object> arr && arr.Count > 0 && arr[0] is Dictionary<string, object>)
                return true;
            return false;
        }

        /// <summary>Returns index into ctx.Properties, or -1 if not present.</summary>
        /// <param name="asPercent">
        /// When true, converts Lottie 0..100 scalars (opacity, trim %) to 0..1 at parse time.
        /// </param>
        private static int ParseScalarProperty(Dictionary<string, object> p, ParseContext ctx, bool asPercent = false)
        {
            if (p == null) return -1;
            if (!IsAnimated(p))
            {
                float v = p.TryGetValue("k", out var k) ? ScalarFromK(k) : 0f;
                if (asPercent) v = PercentScalarToUnit(v);
                return AppendConstScalar(v, ctx);
            }
            return ParseAnimatedScalar(p, ctx, asPercent);
        }

        private static int ParseVec2Property(Dictionary<string, object> p, ParseContext ctx, bool defaultPct = false)
        {
            if (p == null) return -1;

            // Split-component position: { s:true, x:{...}, y:{...} } — common in AE exports.
            // Not fully supported in v1; emit a constant from current static x/y values where possible.
            if (p.TryGetValue("s", out var sFlag) && sFlag is bool sb && sb)
            {
                Debug.LogWarning("[Mynell.Lottie] Split-component position not yet supported; falling back to static.");
                float sx = 0f, sy = 0f;
                if (p.TryGetValue("x", out var xObj) && xObj is Dictionary<string, object> xd && xd.TryGetValue("k", out var xk)) sx = ScalarFromK(xk);
                if (p.TryGetValue("y", out var yObj) && yObj is Dictionary<string, object> yd && yd.TryGetValue("k", out var yk)) sy = ScalarFromK(yk);
                return AppendConstVec2(new float2(sx, sy), ctx);
            }

            if (!IsAnimated(p))
            {
                float2 v = p.TryGetValue("k", out var k) ? Vec2FromK(k) : float2.zero;
                if (defaultPct) v = PercentVec2ToScale(v);
                return AppendConstVec2(v, ctx);
            }
            return ParseAnimatedVec2(p, ctx, defaultPct);
        }

        private static int ParseColorProperty(Dictionary<string, object> p, ParseContext ctx)
        {
            if (p == null) return -1;
            if (!IsAnimated(p))
            {
                float4 v = p.TryGetValue("k", out var k) ? NormalizeColor(Vec4FromK(k)) : new float4(1, 1, 1, 1);
                return AppendConstVec4(v, ctx);
            }
            return ParseAnimatedVec4(p, ctx);
        }

        private static int AppendConstScalar(float v, ParseContext ctx)
        {
            int idx = ctx.ScalarPool.Count;
            ctx.ScalarPool.Add(v);
            ctx.Properties.Add(new LottieProperty { Kind = LottiePropertyKind.Constant, ValueIndex = idx });
            return ctx.Properties.Count - 1;
        }

        private static int AppendConstVec2(float2 v, ParseContext ctx)
        {
            int idx = ctx.Vec2Pool.Count;
            ctx.Vec2Pool.Add(v);
            ctx.Properties.Add(new LottieProperty { Kind = LottiePropertyKind.Constant, ValueIndex = idx });
            return ctx.Properties.Count - 1;
        }

        private static int AppendConstVec4(float4 v, ParseContext ctx)
        {
            int idx = ctx.Vec4Pool.Count;
            ctx.Vec4Pool.Add(v);
            ctx.Properties.Add(new LottieProperty { Kind = LottiePropertyKind.Constant, ValueIndex = idx });
            return ctx.Properties.Count - 1;
        }

        private static int ParsePathProperty(Dictionary<string, object> p, ParseContext ctx)
        {
            if (p == null) return -1;
            bool animated = IsAnimated(p);

            if (!animated)
            {
                var snap = ParsePathSnapshot(p.TryGetValue("k", out var k) ? k as Dictionary<string, object> : null, ctx);
                int snapIdx = ctx.PathSnapshots.Count;
                ctx.PathSnapshots.Add(snap);
                ctx.PathProperties.Add(new LottiePathProperty
                {
                    Kind = LottiePropertyKind.Constant,
                    VertexCount = snap.VertexCount,
                    Closed = snap.Closed,
                    SnapshotOffset = snapIdx,
                    SnapshotCount = 1,
                    KeyframeOffset = -1,
                });
                return ctx.PathProperties.Count - 1;
            }

            // Animated path: kf array of {t, s:[{i,o,v,c}], i, o, h}.
            // Validation rules (Phase B):
            //   - prevSnap tracks the most recent FULLY VALID snapshot. Used to "hold" or
            //     substitute when subsequent keyframes are bad/missing.
            //   - vertCount locks in once the first valid snapshot is seen. Any later snapshot
            //     with a different vertex count is "degraded": replaced with a duplicate of
            //     prevSnap so the morph interpolator never sees mismatched topology.
            //   - Missing 's' with no prior snapshot → MalformedSnapshotKeyframes++ and skip.
            var kArr = p.TryGetValue("k", out var kObj) ? kObj as List<object> : null;
            int snapStart = ctx.PathSnapshots.Count;
            int kfStart = ctx.Keyframes.Count;
            int vertCount = -1;
            byte closed = 0;
            LottiePathSnapshot prevSnap = default;
            bool hasPrev = false;

            if (kArr != null)
            {
                for (int i = 0; i < kArr.Count; i++)
                {
                    var kfd = kArr[i] as Dictionary<string, object>;
                    if (kfd == null) continue;
                    Dictionary<string, object> sNode = null;
                    if (kfd.TryGetValue("s", out var sObj))
                    {
                        if (sObj is List<object> sList && sList.Count > 0) sNode = sList[0] as Dictionary<string, object>;
                        else sNode = sObj as Dictionary<string, object>;
                    }

                    LottiePathSnapshot snap;
                    if (sNode != null)
                    {
                        snap = ParsePathSnapshot(sNode, ctx);

                        if (snap.VertexCount == 0)
                        {
                            // Snapshot was rejected by ParsePathSnapshot's validation.
                            // Hold previous topology if we have one; otherwise drop the keyframe.
                            if (!hasPrev) { ctx.Report.MalformedSnapshotKeyframes++; continue; }
                            snap = DuplicatePathSnapshot(prevSnap, ctx);
                        }
                        else if (vertCount > 0 && snap.VertexCount != vertCount)
                        {
                            // Topology mismatch across animation frames → would crash morph
                            // interpolation. Replace with the locked-in topology.
                            ctx.Report.DegradedAnimatedPaths++;
                            snap = DuplicatePathSnapshot(prevSnap, ctx);
                        }
                        else
                        {
                            // Fully valid snapshot — promote to "previous valid".
                            prevSnap = snap;
                            hasPrev = true;
                        }
                    }
                    else if (hasPrev)
                    {
                        // Terminator keyframe without 's' — hold previous path data
                        // (re-emit the same vertices so the snapshot is independently addressable).
                        snap = DuplicatePathSnapshot(prevSnap, ctx);
                    }
                    else
                    {
                        ctx.Report.MalformedSnapshotKeyframes++;
                        continue; // No data yet and no previous to hold; skip.
                    }

                    ctx.PathSnapshots.Add(snap);
                    if (vertCount < 0) { vertCount = snap.VertexCount; closed = snap.Closed; }

                    ctx.Keyframes.Add(new LottieKeyframe
                    {
                        Time = ReadFloat(kfd, "t", 0f),
                        ValueOffset = ctx.PathSnapshots.Count - 1, // index into PathSnapshots
                        Hold = (byte)(ReadFloat(kfd, "h", 0f) > 0.5f ? 1 : 0),
                        OutTanIndex = AppendEasingTangent(kfd, "o", ctx, out byte hasOut),
                        InTanIndex = AppendEasingTangent(kfd, "i", ctx, out byte hasIn),
                        HasOutTan = hasOut,
                        HasInTan = hasIn,
                    });
                }
            }

            ctx.PathProperties.Add(new LottiePathProperty
            {
                Kind = LottiePropertyKind.AnimatedPath,
                VertexCount = math.max(0, vertCount),
                Closed = closed,
                SnapshotOffset = snapStart,
                SnapshotCount = ctx.PathSnapshots.Count - snapStart,
                KeyframeOffset = kfStart,
            });
            return ctx.PathProperties.Count - 1;
        }

        /// <summary>Copy a path snapshot's vertices and return a new snapshot pointing at the duplicate.</summary>
        private static LottiePathSnapshot DuplicatePathSnapshot(LottiePathSnapshot src, ParseContext ctx)
        {
            int newOffset = ctx.BezierVertices.Count;
            for (int i = 0; i < src.VertexCount; i++)
                ctx.BezierVertices.Add(ctx.BezierVertices[src.VertexOffset + i]);
            return new LottiePathSnapshot
            {
                VertexOffset = newOffset,
                VertexCount = src.VertexCount,
                Closed = src.Closed,
            };
        }

        /// <summary>
        /// Parse a single path snapshot ('s' node or static 'k' object).
        /// Validation rules (Phase B):
        ///   1. v.Count &lt; 2  → snapshot dropped (degenerate path).
        ///   2. v / i / o present and lengths disagree → dropped (malformed export).
        ///   3. Missing i or o → tolerated; treated as zero tangents (corner vertices).
        /// Per Lottie spec, <c>i</c>/<c>o</c> are RELATIVE to their matching <c>v</c> entry
        /// (not absolute positions). The tessellator adds them to <c>v</c> when flattening.
        /// Returns a zero-vertex placeholder snapshot on rejection so the caller can
        /// substitute a previous valid snapshot or skip the keyframe entirely.
        /// </summary>
        private static LottiePathSnapshot ParsePathSnapshot(Dictionary<string, object> shapeDict, ParseContext ctx)
        {
            var empty = new LottiePathSnapshot
            {
                VertexOffset = ctx.BezierVertices.Count,
                VertexCount = 0,
                Closed = 0,
            };
            if (shapeDict == null) return empty;

            var v = shapeDict.TryGetValue("v", out var vObj) ? vObj as List<object> : null;
            var i = shapeDict.TryGetValue("i", out var iObj) ? iObj as List<object> : null;
            var o = shapeDict.TryGetValue("o", out var oObj) ? oObj as List<object> : null;
            bool closed = shapeDict.TryGetValue("c", out var cObj) && cObj is bool cb && cb;

            int n = v?.Count ?? 0;

            // Rule 1: a path needs at least 2 vertices. Drop everything else.
            if (n < 2)
            {
                ctx.Report.DroppedPathSnapshots++;
                return empty;
            }
            // Rule 2: when present, in/out tangent arrays must match the vertex array.
            if ((i != null && i.Count != n) || (o != null && o.Count != n))
            {
                ctx.Report.DroppedPathSnapshots++;
                return empty;
            }

            for (int k = 0; k < n; k++)
            {
                ctx.BezierVertices.Add(new LottieBezierVertex
                {
                    V = Vec2FromArr(v[k]),
                    In = i != null ? Vec2FromArr(i[k]) : float2.zero,
                    Out = o != null ? Vec2FromArr(o[k]) : float2.zero,
                });
            }
            return new LottiePathSnapshot
            {
                VertexOffset = empty.VertexOffset,
                VertexCount = n,
                Closed = (byte)(closed ? 1 : 0),
            };
        }

        // ----- Animated scalar/vec2/vec4 keyframes -----

        private static int ParseAnimatedScalar(Dictionary<string, object> p, ParseContext ctx, bool asPercent = false)
        {
            var kArr = p.TryGetValue("k", out var kObj) ? kObj as List<object> : null;
            int kfStart = ctx.Keyframes.Count;
            int kfCount = 0;
            float prev = 0f;
            bool hasPrev = false;
            if (kArr != null)
            {
                for (int i = 0; i < kArr.Count; i++)
                {
                    var kfd = kArr[i] as Dictionary<string, object>;
                    if (kfd == null) continue;

                    float v;
                    if (kfd.TryGetValue("s", out var sObj))
                    {
                        v = ScalarFromAny(sObj);
                        prev = v; hasPrev = true;
                    }
                    else if (kfd.TryGetValue("e", out var eObj))
                    {
                        v = ScalarFromAny(eObj);
                        prev = v; hasPrev = true;
                    }
                    else if (hasPrev) v = prev;
                    else continue;

                    if (asPercent) v = PercentScalarToUnit(v);

                    int valIdx = ctx.ScalarPool.Count;
                    ctx.ScalarPool.Add(v);

                    ctx.Keyframes.Add(new LottieKeyframe
                    {
                        Time = ReadFloat(kfd, "t", 0f),
                        ValueOffset = valIdx,
                        Hold = (byte)(ReadFloat(kfd, "h", 0f) > 0.5f ? 1 : 0),
                        OutTanIndex = AppendEasingTangent(kfd, "o", ctx, out byte hasOut),
                        InTanIndex = AppendEasingTangent(kfd, "i", ctx, out byte hasIn),
                        HasOutTan = hasOut,
                        HasInTan = hasIn,
                    });
                    kfCount++;
                }
            }
            ctx.Properties.Add(new LottieProperty
            {
                Kind = LottiePropertyKind.AnimatedScalar,
                ValueIndex = -1,
                KeyframeOffset = kfStart,
                KeyframeCount = kfCount,
            });
            return ctx.Properties.Count - 1;
        }

        private static int ParseAnimatedVec2(Dictionary<string, object> p, ParseContext ctx, bool isPct)
        {
            var kArr = p.TryGetValue("k", out var kObj) ? kObj as List<object> : null;
            int kfStart = ctx.Keyframes.Count;
            int kfCount = 0;
            float2 prev = float2.zero;
            bool hasPrev = false;
            if (kArr != null)
            {
                for (int i = 0; i < kArr.Count; i++)
                {
                    var kfd = kArr[i] as Dictionary<string, object>;
                    if (kfd == null) continue;

                    float2 v;
                    if (kfd.TryGetValue("s", out var sObj))
                    {
                        v = Vec2FromK(sObj);
                        if (isPct) v = PercentVec2ToScale(v);
                        prev = v; hasPrev = true;
                    }
                    else if (kfd.TryGetValue("e", out var eObj))
                    {
                        v = Vec2FromK(eObj);
                        if (isPct) v = PercentVec2ToScale(v);
                        prev = v; hasPrev = true;
                    }
                    else if (hasPrev) v = prev;
                    else continue;

                    int valIdx = ctx.Vec2Pool.Count;
                    ctx.Vec2Pool.Add(v);

                    ctx.Keyframes.Add(new LottieKeyframe
                    {
                        Time = ReadFloat(kfd, "t", 0f),
                        ValueOffset = valIdx,
                        Hold = (byte)(ReadFloat(kfd, "h", 0f) > 0.5f ? 1 : 0),
                        OutTanIndex = AppendEasingTangent(kfd, "o", ctx, out byte hasOut),
                        InTanIndex = AppendEasingTangent(kfd, "i", ctx, out byte hasIn),
                        HasOutTan = hasOut,
                        HasInTan = hasIn,
                    });
                    kfCount++;
                }
            }
            ctx.Properties.Add(new LottieProperty
            {
                Kind = LottiePropertyKind.AnimatedVec2,
                ValueIndex = -1,
                KeyframeOffset = kfStart,
                KeyframeCount = kfCount,
            });
            return ctx.Properties.Count - 1;
        }

        private static int ParseAnimatedVec4(Dictionary<string, object> p, ParseContext ctx)
        {
            var kArr = p.TryGetValue("k", out var kObj) ? kObj as List<object> : null;
            int kfStart = ctx.Keyframes.Count;
            int kfCount = 0;
            float4 prev = new float4(1, 1, 1, 1);
            bool hasPrev = false;
            if (kArr != null)
            {
                for (int i = 0; i < kArr.Count; i++)
                {
                    var kfd = kArr[i] as Dictionary<string, object>;
                    if (kfd == null) continue;

                    float4 v;
                    if (kfd.TryGetValue("s", out var sObj))
                    {
                        v = NormalizeColor(Vec4FromK(sObj));
                        prev = v; hasPrev = true;
                    }
                    else if (kfd.TryGetValue("e", out var eObj))
                    {
                        v = NormalizeColor(Vec4FromK(eObj));
                        prev = v; hasPrev = true;
                    }
                    else if (hasPrev) v = prev;
                    else continue;

                    int valIdx = ctx.Vec4Pool.Count;
                    ctx.Vec4Pool.Add(v);

                    ctx.Keyframes.Add(new LottieKeyframe
                    {
                        Time = ReadFloat(kfd, "t", 0f),
                        ValueOffset = valIdx,
                        Hold = (byte)(ReadFloat(kfd, "h", 0f) > 0.5f ? 1 : 0),
                        OutTanIndex = AppendEasingTangent(kfd, "o", ctx, out byte hasOut),
                        InTanIndex = AppendEasingTangent(kfd, "i", ctx, out byte hasIn),
                        HasOutTan = hasOut,
                        HasInTan = hasIn,
                    });
                    kfCount++;
                }
            }
            ctx.Properties.Add(new LottieProperty
            {
                Kind = LottiePropertyKind.AnimatedVec4,
                ValueIndex = -1,
                KeyframeOffset = kfStart,
                KeyframeCount = kfCount,
            });
            return ctx.Properties.Count - 1;
        }

        // Easing tangent: returns index into ScalarPool where 2 floats (x,y) are stored, or -1.
        private static int AppendEasingTangent(Dictionary<string, object> kfd, string key, ParseContext ctx, out byte present)
        {
            present = 0;
            if (kfd == null || !kfd.TryGetValue(key, out var obj) || !(obj is Dictionary<string, object> tan)) return -1;
            float x = 0.33f, y = 0.33f;
            if (tan.TryGetValue("x", out var xObj))
            {
                if (xObj is List<object> xl && xl.Count > 0) x = (float)Convert.ToDouble(xl[0]);
                else x = (float)Convert.ToDouble(xObj);
            }
            if (tan.TryGetValue("y", out var yObj))
            {
                if (yObj is List<object> yl && yl.Count > 0) y = (float)Convert.ToDouble(yl[0]);
                else y = (float)Convert.ToDouble(yObj);
            }
            int idx = ctx.ScalarPool.Count;
            ctx.ScalarPool.Add(x);
            ctx.ScalarPool.Add(y);
            present = 1;
            return idx;
        }

        // ---------------------------- Spec normalization helpers ----------------------------

        /// <summary>Lottie opacity / trim scalars are 0..100 (100 = opaque / full span).</summary>
        private static float PercentScalarToUnit(float v) => v * 0.01f;

        /// <summary>Lottie transform scale is percent; [100, 100] means identity.</summary>
        private static float2 PercentVec2ToScale(float2 v) => v * 0.01f;

        /// <summary>
        /// Spec colors are 0..1 floats. Some exporters emit 0..255 — detect and rescale.
        /// </summary>
        private static float4 NormalizeColor(float4 c)
        {
            float m = math.max(math.max(c.x, c.y), math.max(c.z, c.w));
            if (m > 1.0001f)
                return new float4(c.x / 255f, c.y / 255f, c.z / 255f, c.w > 1.0001f ? c.w / 255f : c.w);
            return c;
        }

        // ---------------------------- Primitive readers ----------------------------

        private static float ReadFloat(Dictionary<string, object> d, string key, float def)
        {
            if (d == null || !d.TryGetValue(key, out var o) || o == null) return def;
            return ReadFloatFromObj(o, def);
        }

        private static float ReadFloatFromObj(object o, float def)
        {
            if (o == null) return def;
            try { return (float)Convert.ToDouble(o); } catch { return def; }
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var o)) return null;
            return o as string;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var o)) return null;
            return o as Dictionary<string, object>;
        }

        /// <summary>Scalar from static k or animated keyframe s/e (vector keyframes use [v]).</summary>
        private static float ScalarFromK(object k) => ScalarFromAny(k);

        private static float ScalarFromAny(object o)
        {
            if (o is Dictionary<string, object> d && d.TryGetValue("k", out var inner))
                return ScalarFromAny(inner);
            if (o is List<object> l && l.Count > 0)
                return (float)Convert.ToDouble(l[0]);
            try { return (float)Convert.ToDouble(o); } catch { return 0f; }
        }

        /// <summary>
        /// 2D vector from static k or keyframe s/e. Uses X/Y only when a 3D [x,y,z] is supplied.
        /// </summary>
        private static float2 Vec2FromK(object k)
        {
            if (k is Dictionary<string, object> d && d.TryGetValue("k", out var inner))
                return Vec2FromK(inner);
            if (k is List<object> l)
            {
                float x = l.Count > 0 ? (float)Convert.ToDouble(l[0]) : 0f;
                float y = l.Count > 1 ? (float)Convert.ToDouble(l[1]) : 0f;
                return new float2(x, y);
            }
            return float2.zero;
        }

        /// <summary>RGBA color vector; components are 0..1 per Lottie spec (see NormalizeColor).</summary>
        private static float4 Vec4FromK(object k)
        {
            if (k is Dictionary<string, object> d && d.TryGetValue("k", out var inner))
                return Vec4FromK(inner);
            if (k is List<object> l)
            {
                float r = l.Count > 0 ? (float)Convert.ToDouble(l[0]) : 0f;
                float g = l.Count > 1 ? (float)Convert.ToDouble(l[1]) : 0f;
                float b = l.Count > 2 ? (float)Convert.ToDouble(l[2]) : 0f;
                float a = l.Count > 3 ? (float)Convert.ToDouble(l[3]) : 1f;
                return new float4(r, g, b, a);
            }
            return new float4(0, 0, 0, 1);
        }

        private static float2 Vec2FromArr(object o)
        {
            return Vec2FromK(o);
        }

    }
}
