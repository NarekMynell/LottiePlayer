using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mynell.Lottie.Compiler;
using Mynell.Lottie.Model;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary>
    /// Imports Lottie animations: dotLottie archives (.lottie) natively and Bodymovin JSON (.json) as an importer
    /// override (set automatically for newly added Lottie JSON files, or via the importer drop-down).
    /// </summary>
    [ScriptedImporter(5, new[] { "lottie" }, new[] { "json" })]
    [HelpURL("https://airbnb.io/lottie/")]
    public sealed class LottieImporter : ScriptedImporter
    {
        [Tooltip("For .lottie archives with several animations: which one becomes the main asset (others are sub-assets).")]
        [SerializeField] int m_MainAnimation;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string ext = Path.GetExtension(ctx.assetPath).ToLowerInvariant();
            try
            {
                if (ext == ".lottie") ImportDotLottie(ctx);
                else ImportJson(ctx);
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Lottie import failed: {e.Message}");
                Debug.LogException(e);
            }
        }

        static void ImportJson(AssetImportContext ctx)
        {
            byte[] json = File.ReadAllBytes(ctx.assetPath);
            string dir = Path.GetDirectoryName(ctx.assetPath)?.Replace('\\', '/') ?? "";
            var data = LottieCompiler.Compile(json, new LottieCompileOptions { FontResolver = FontResolver(ctx) });
            data.Name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            LottieAsset.ResolveImages(data, rel =>
            {
                string path = CombineAssetPath(dir, rel);
                ctx.DependsOnSourceAsset(path);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            });
            var asset = CreateAsset(data, Path.GetFileNameWithoutExtension(ctx.assetPath));
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);
            Report(ctx, data);
        }

        void ImportDotLottie(AssetImportContext ctx)
        {
            var animations = DotLottie.Load(File.ReadAllBytes(ctx.assetPath), FontResolver(ctx));
            if (animations.Count == 0)
            {
                ctx.LogImportError("The .lottie archive contains no animation.");
                return;
            }
            int main = Mathf.Clamp(m_MainAnimation, 0, animations.Count - 1);
            for (int i = 0; i < animations.Count; i++)
            {
                var data = animations[i].Data;
                var asset = CreateAsset(data, i == main ? Path.GetFileNameWithoutExtension(ctx.assetPath) : animations[i].Id);
                ctx.AddObjectToAsset("anim:" + animations[i].Id, asset);
                if (i == main) ctx.SetMainObject(asset);
                Report(ctx, data);
            }
        }

        /// <summary>Fonts from the project (any .ttf / .otf / .ttc under Assets) first, then the OS font folders.</summary>
        static Func<LottieFontRequest, byte[]> FontResolver(AssetImportContext ctx)
        {
            s_ProjectFonts ??= new Text.FontLocator(new[] { Path.GetFullPath("Assets") });
            return Text.FontLocator.Chain(s_ProjectFonts.Resolve, Text.FontLocator.System.Resolve);
        }

        static Text.FontLocator s_ProjectFonts;

        static LottieAsset CreateAsset(LottieData data, string name)
        {
            var asset = ScriptableObject.CreateInstance<LottieAsset>();
            asset.name = name;
            asset.SetData(data);
            return asset;
        }

        static void Report(AssetImportContext ctx, LottieData data)
        {
            foreach (var u in data.Unsupported) ctx.LogImportWarning($"Unsupported Lottie feature: {u}");
        }

        static string CombineAssetPath(string dir, string rel)
        {
            rel = rel.Replace('\\', '/');
            if (rel.StartsWith("/")) rel = rel.Substring(1);
            string combined = string.IsNullOrEmpty(dir) ? rel : dir + "/" + rel;
            // Normalize "a/b/../c".
            var parts = new List<string>();
            foreach (var p in combined.Split('/'))
            {
                if (p == "." || p.Length == 0) continue;
                if (p == ".." && parts.Count > 0) { parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(p);
            }
            return string.Join("/", parts);
        }

        /// <summary>Cheap check whether a JSON file is a Lottie (Bodymovin) animation.</summary>
        public static bool LooksLikeLottie(string path)
        {
            try
            {
                using var f = File.OpenRead(path);
                var buf = new byte[Math.Min(4096, (int)f.Length)];
                int n = f.Read(buf, 0, buf.Length);
                string head = Encoding.UTF8.GetString(buf, 0, n);
                int hits = 0;
                foreach (var key in new[] { "\"v\"", "\"fr\"", "\"ip\"", "\"op\"", "\"w\"", "\"h\"", "\"layers\"", "\"assets\"", "\"ddd\"" })
                    if (head.Contains(key)) hits++;
                return hits >= 5;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Switches newly added Lottie JSON files to <see cref="LottieImporter"/>.</summary>
    sealed class LottieJsonDetector : AssetPostprocessor
    {
        // Pending paths survive domain reloads (a refresh can import JSON files and recompile scripts at once).
        const string PendingKey = "Mynell.Lottie.PendingJsonImports";

        void OnPreprocessAsset()
        {
            if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
            if (!assetImporter.importSettingsMissing) return; // only brand-new files; respect explicit choices
            if (assetImporter is LottieImporter) return;
            if (!LottieImporter.LooksLikeLottie(assetPath)) return;
            string pending = SessionState.GetString(PendingKey, "");
            SessionState.SetString(PendingKey, pending.Length == 0 ? assetPath : pending + "\n" + assetPath);
            EditorApplication.delayCall -= ApplyPending;
            EditorApplication.delayCall += ApplyPending;
        }

        [InitializeOnLoadMethod]
        static void OnLoad()
        {
            if (SessionState.GetString(PendingKey, "").Length > 0) EditorApplication.delayCall += ApplyPending;
        }

        static void ApplyPending()
        {
            string pending = SessionState.GetString(PendingKey, "");
            SessionState.EraseString(PendingKey);
            if (pending.Length == 0) return;
            foreach (var p in pending.Split('\n'))
                if (p.Length > 0 && File.Exists(p) && AssetDatabase.GetImporterOverride(p) != typeof(LottieImporter))
                    AssetDatabase.SetImporterOverride<LottieImporter>(p);
        }

        [MenuItem("Assets/Lottie/Import Selected JSON as Lottie", false, 1000)]
        static void ImportSelectedAsLottie()
        {
            foreach (var guid in Selection.assetGUIDs)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) AssetDatabase.SetImporterOverride<LottieImporter>(p);
            }
        }

        [MenuItem("Assets/Lottie/Import Selected JSON as Lottie", true)]
        static bool ImportSelectedAsLottieValidate()
        {
            foreach (var guid in Selection.assetGUIDs)
                if (AssetDatabase.GUIDToAssetPath(guid).EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [MenuItem("Assets/Lottie/Import Selected JSON as Text", false, 1001)]
        static void ImportSelectedAsText()
        {
            foreach (var guid in Selection.assetGUIDs)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.GetImporterOverride(p) == typeof(LottieImporter)) AssetDatabase.ClearImporterOverride(p);
            }
        }
    }
}
