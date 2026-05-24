// Mynell Lottie Player — Editor utilities to convert .json files into LottieAsset assets.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Mynell.LottiePlayer.Editor
{
    internal static class LottieImportMenu
    {
        [MenuItem("Assets/Mynell/Bake Selected Lottie JSON → LottieAsset", priority = 200)]
        private static void BakeSelected() => BakeSelection(forceClear: false);

        [MenuItem("Assets/Mynell/Force Clear & Re-Bake Selected Lottie JSON → LottieAsset", priority = 201)]
        private static void ForceClearAndBakeSelected() => BakeSelection(forceClear: true);

        private static void BakeSelection(bool forceClear)
        {
            var sel = Selection.objects;
            if (sel == null || sel.Length == 0)
            {
                EditorUtility.DisplayDialog("Mynell Lottie Player",
                    forceClear
                        ? "Select one or more .json or .txt Lottie files to force-clear and re-bake."
                        : "Select one or more .json or .txt Lottie files.",
                    "OK");
                return;
            }

            int ok = 0, fail = 0, cleared = 0;
            foreach (var o in sel)
            {
                string path = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(path)) continue;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".json" && ext != ".txt") continue;
                try
                {
                    if (BakeFile(path, forceClear, out bool didClear))
                    {
                        ok++;
                        if (didClear) cleared++;
                    }
                    else fail++;
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[Mynell.LottiePlayer] Failed to bake '{path}': {ex.Message}");
                    fail++;
                }
            }

            string summary = forceClear
                ? $"Force-cleared & re-baked {ok} asset(s)" + (cleared > 0 ? $" ({cleared} deleted first)" : "") + "."
                : $"Baked {ok} asset(s).";
            if (fail > 0) summary += $"\n{fail} failed (see Console).";
            EditorUtility.DisplayDialog("Mynell Lottie Player", summary, "OK");
        }

        /// <summary>
        /// Parse JSON at <paramref name="jsonAssetPath"/> and write/update the sibling
        /// <c>.lottie.asset</c>. When <paramref name="forceClear"/> is true the existing
        /// ScriptableObject is deleted before a brand-new instance is created.
        /// </summary>
        private static bool BakeFile(string jsonAssetPath, bool forceClear, out bool didClear)
        {
            didClear = false;
            string json = File.ReadAllText(jsonAssetPath);

            LottieZeroOffsetReport zeroReport;
            Dictionary<string, object> root;
            try
            {
                root = LottieBakeZeroOffset.DeserializeApplyAndGetRoot(json, out zeroReport);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Mynell.LottiePlayer] JSON preprocess failed for '{jsonAssetPath}': {ex.Message}");
                return false;
            }

            if (!string.IsNullOrEmpty(zeroReport.RootLayerName))
            {
                Debug.Log(
                    $"[Mynell.Lottie Zero-Offset Bake] '{Path.GetFileName(jsonAssetPath)}'\n" +
                    $"  Root layer: '{zeroReport.RootLayerName}'\n" +
                    $"  BEFORE  p={zeroReport.PositionBefore}  a={zeroReport.AnchorBefore}\n" +
                    $"  Offset  p=[{zeroReport.OffsetX:F3}, {zeroReport.OffsetY:F3}]  a=[{zeroReport.OffsetAnchorX:F3}, {zeroReport.OffsetAnchorY:F3}]\n" +
                    $"  AFTER   p={zeroReport.PositionAfter}  a={zeroReport.AnchorAfter}",
                    AssetDatabase.LoadAssetAtPath<Object>(jsonAssetPath));
            }
            else
            {
                Debug.LogWarning($"[Mynell.Lottie Zero-Offset Bake] '{Path.GetFileName(jsonAssetPath)}': no root layer found — skipped centering.");
            }

            var blob = LottieParser.ParseRoot(root, out var report);
            string assetPath = Path.ChangeExtension(jsonAssetPath, ".lottie.asset");

            if (forceClear)
            {
                var existing = AssetDatabase.LoadAssetAtPath<LottieAsset>(assetPath);
                if (existing != null)
                {
                    if (!AssetDatabase.DeleteAsset(assetPath))
                    {
                        blob.Dispose();
                        Debug.LogError($"[Mynell.LottiePlayer] Force Clear failed — could not delete '{assetPath}'.");
                        return false;
                    }
                    didClear = true;
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    Debug.Log($"[Mynell.LottiePlayer] Force Clear: deleted stale asset '{assetPath}'.");
                }
            }

            LottieAsset lottieAsset;
            if (forceClear || AssetDatabase.LoadAssetAtPath<LottieAsset>(assetPath) == null)
                lottieAsset = ScriptableObject.CreateInstance<LottieAsset>();
            else
                lottieAsset = AssetDatabase.LoadAssetAtPath<LottieAsset>(assetPath);

            lottieAsset.StoreBlob(blob, report);

            if (AssetDatabase.LoadAssetAtPath<LottieAsset>(assetPath) == null)
                AssetDatabase.CreateAsset(lottieAsset, assetPath);

            EditorUtility.SetDirty(lottieAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            blob.Dispose();
            return true;
        }

        [MenuItem("Assets/Mynell/Bake Selected Lottie JSON → LottieAsset", true)]
        [MenuItem("Assets/Mynell/Force Clear & Re-Bake Selected Lottie JSON → LottieAsset", true)]
        private static bool BakeSelectedValidate()
        {
            foreach (var o in Selection.objects)
            {
                if (o == null) continue;
                string path = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(path)) continue;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".json" || ext == ".txt") return true;
            }
            return false;
        }
    }
}
