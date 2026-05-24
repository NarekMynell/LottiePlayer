// Mynell Lottie Player — Bake-time zero-offset centering.
// Subtracts the root layer's ks.p / ks.a from unparented (composition-absolute) layer transforms.
// Parented layers and shape-group tr are left untouched — their coords are parent-/layer-local.

using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Mynell.LottiePlayer
{
    /// <summary>Log payload for bake-time zero-offset validation.</summary>
    public struct LottieZeroOffsetReport
    {
        public string RootLayerName;
        public string PositionBefore;
        public string AnchorBefore;
        public string PositionAfter;
        public string AnchorAfter;
        public float OffsetX;
        public float OffsetY;
        public float OffsetAnchorX;
        public float OffsetAnchorY;
    }

    /// <summary>
    /// Centers a Lottie JSON document on its root layer origin by subtracting the root
    /// layer <c>ks.p</c> / <c>ks.a</c> from unparented layer transforms only.
    /// </summary>
    public static class LottieBakeZeroOffset
    {
        /// <summary>Deserialize JSON, apply zero-offset centering, return mutated root for parsing.</summary>
        public static Dictionary<string, object> DeserializeApplyAndGetRoot(string json, out LottieZeroOffsetReport report)
        {
            report = default;
            if (string.IsNullOrEmpty(json))
                throw new System.ArgumentException("Empty Lottie JSON");

            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (root == null)
                throw new System.Exception("Invalid JSON (no root object)");

            Apply(root, out report);
            return root;
        }

        /// <summary>Mutates <paramref name="root"/> in place. Returns false if no root layer found.</summary>
        public static bool Apply(Dictionary<string, object> root, out LottieZeroOffsetReport report)
        {
            report = default;
            if (root == null) return false;

            if (!TryFindRootLayer(root, out var rootLayer, out string rootName))
                return false;

            if (!TryReadStaticVec2(GetNestedDict(rootLayer, "ks", "p"), out float offsetPx, out float offsetPy))
            {
                float w = ReadFloat(root, "w", 512f);
                float h = ReadFloat(root, "h", 512f);
                offsetPx = w * 0.5f;
                offsetPy = h * 0.5f;
            }

            if (!TryReadStaticVec2(GetNestedDict(rootLayer, "ks", "a"), out float offsetAx, out float offsetAy))
            {
                offsetAx = offsetPx;
                offsetAy = offsetPy;
            }

            report.RootLayerName = rootName;
            report.PositionBefore = FormatVec2(GetNestedDict(rootLayer, "ks", "p"));
            report.AnchorBefore = FormatVec2(GetNestedDict(rootLayer, "ks", "a"));
            report.OffsetX = offsetPx;
            report.OffsetY = offsetPy;
            report.OffsetAnchorX = offsetAx;
            report.OffsetAnchorY = offsetAy;

            if (root.TryGetValue("layers", out var topLayers) && topLayers is List<object> topList)
                ApplyToLayers(topList, offsetPx, offsetPy, offsetAx, offsetAy);

            if (root.TryGetValue("assets", out var assetsObj) && assetsObj is List<object> assets)
            {
                for (int i = 0; i < assets.Count; i++)
                {
                    if (assets[i] is not Dictionary<string, object> asset) continue;
                    if (asset.TryGetValue("layers", out var assetLayers) && assetLayers is List<object> assetList)
                        ApplyToLayers(assetList, offsetPx, offsetPy, offsetAx, offsetAy);
                }
            }

            // Root layer static ks → [0,0] after global subtract.
            ZeroStaticKs(rootLayer, "p");
            ZeroStaticKs(rootLayer, "a");

            report.PositionAfter = FormatVec2(GetNestedDict(rootLayer, "ks", "p"));
            report.AnchorAfter = FormatVec2(GetNestedDict(rootLayer, "ks", "a"));
            return true;
        }

        private static void ApplyToLayers(List<object> layers, float offsetPx, float offsetPy, float offsetAx, float offsetAy)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] is not Dictionary<string, object> layer) continue;
                // Parented layer ks.p is in parent space — only unparented layers are comp-absolute.
                bool hasParent = layer.ContainsKey("parent");
                if (layer.TryGetValue("ks", out var ksObj) && ksObj is Dictionary<string, object> ks && !hasParent)
                {
                    OffsetProperty(ks, "p", offsetPx, offsetPy);
                    OffsetProperty(ks, "a", offsetAx, offsetAy);
                }

                // Group tr transforms live in layer-local space — never apply comp-wide offset.
            }
        }

        private static void ApplyToShapes(List<object> shapes, float offsetPx, float offsetPy, float offsetAx, float offsetAy)
        {
            // Shape-group transforms are layer-local; comp-wide zero-offset must not touch them.
        }

        private static void OffsetProperty(Dictionary<string, object> owner, string key, float dx, float dy)
        {
            if (!owner.TryGetValue(key, out var propObj) || propObj is not Dictionary<string, object> prop)
                return;

            if (IsAnimated(prop))
            {
                if (!prop.TryGetValue("k", out var kObj) || kObj is not List<object> kList) return;
                for (int i = 0; i < kList.Count; i++)
                {
                    if (kList[i] is not Dictionary<string, object> kf) continue;
                    if (kf.TryGetValue("s", out var sObj)) SubtractVec2InPlace(sObj, dx, dy);
                    if (kf.TryGetValue("e", out var eObj)) SubtractVec2InPlace(eObj, dx, dy);
                }
                return;
            }

            if (prop.TryGetValue("k", out var staticK))
                SubtractVec2InPlace(staticK, dx, dy);
        }

        private static void ZeroStaticKs(Dictionary<string, object> layer, string key)
        {
            if (layer.TryGetValue("ks", out var ksObj) && ksObj is Dictionary<string, object> ks &&
                ks.TryGetValue(key, out var propObj) && propObj is Dictionary<string, object> prop &&
                !IsAnimated(prop))
            {
                prop["k"] = new List<object> { 0.0, 0.0 };
            }
        }

        private static bool IsAnimated(Dictionary<string, object> prop)
        {
            if (ReadFloat(prop, "a", 0f) > 0.5f) return true;
            if (prop.TryGetValue("k", out var k) && k is List<object> list && list.Count > 0 && list[0] is Dictionary<string, object>)
                return true;
            return false;
        }

        private static void SubtractVec2InPlace(object vecObj, float dx, float dy)
        {
            if (vecObj is not List<object> list || list.Count < 2) return;
            list[0] = ToDouble(list[0]) - dx;
            list[1] = ToDouble(list[1]) - dy;
        }

        private static bool TryFindRootLayer(Dictionary<string, object> root, out Dictionary<string, object> rootLayer, out string name)
        {
            rootLayer = null;
            name = null;
            if (!root.TryGetValue("layers", out var layersObj) || layersObj is not List<object> layers || layers.Count == 0)
                return false;

            float cx = ReadFloat(root, "w", 512f) * 0.5f;
            float cy = ReadFloat(root, "h", 512f) * 0.5f;
            Dictionary<string, object> firstPrecomp = null;

            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i] is not Dictionary<string, object> ld) continue;
                if (ld.ContainsKey("parent")) continue;

                int ty = (int)ReadFloat(ld, "ty", -1f);
                string nm = ld.TryGetValue("nm", out var nmObj) ? nmObj as string : $"layer_{i}";

                if (ty == 0 && firstPrecomp == null)
                {
                    firstPrecomp = ld;
                    name = nm;
                }

                if (TryReadStaticVec2(GetNestedDict(ld, "ks", "p"), out float px, out float py) &&
                    TryReadStaticVec2(GetNestedDict(ld, "ks", "a"), out float ax, out float ay) &&
                    Mathf.Abs(px - cx) < 2f && Mathf.Abs(py - cy) < 2f && Mathf.Abs(px - ax) < 0.01f && Mathf.Abs(py - ay) < 0.01f)
                {
                    rootLayer = ld;
                    name = nm;
                    return true;
                }
            }

            if (firstPrecomp != null)
            {
                rootLayer = firstPrecomp;
                return true;
            }

            if (layers[0] is Dictionary<string, object> fallback)
            {
                rootLayer = fallback;
                name = fallback.TryGetValue("nm", out var nmObj) ? nmObj as string : "layer_0";
                return true;
            }

            return false;
        }

        private static bool TryReadStaticVec2(Dictionary<string, object> prop, out float x, out float y)
        {
            x = y = 0f;
            if (prop == null || IsAnimated(prop)) return false;
            if (!prop.TryGetValue("k", out var kObj) || kObj is not List<object> list || list.Count < 2) return false;
            x = (float)ToDouble(list[0]);
            y = (float)ToDouble(list[1]);
            return true;
        }

        private static string FormatVec2(Dictionary<string, object> prop)
        {
            if (prop == null) return "<missing>";
            if (IsAnimated(prop)) return "<animated>";
            if (!prop.TryGetValue("k", out var kObj) || kObj is not List<object> list || list.Count < 2)
                return "<invalid>";
            return $"[{ToDouble(list[0]):F3}, {ToDouble(list[1]):F3}]";
        }

        private static Dictionary<string, object> GetNestedDict(Dictionary<string, object> owner, string k1, string k2)
        {
            if (owner == null || !owner.TryGetValue(k1, out var o1) || o1 is not Dictionary<string, object> d1) return null;
            if (!d1.TryGetValue(k2, out var o2) || o2 is not Dictionary<string, object> d2) return null;
            return d2;
        }

        private static float ReadFloat(Dictionary<string, object> d, string key, float def)
        {
            if (d == null || !d.TryGetValue(key, out var o) || o == null) return def;
            try { return (float)ToDouble(o); } catch { return def; }
        }

        private static double ToDouble(object o)
        {
            if (o == null) return 0d;
            if (o is double d) return d;
            if (o is float f) return f;
            if (o is long l) return l;
            if (o is int i) return i;
            return double.Parse(o.ToString(), CultureInfo.InvariantCulture);
        }
    }
}
