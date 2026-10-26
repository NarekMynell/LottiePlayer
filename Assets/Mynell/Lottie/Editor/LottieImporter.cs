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
    [ScriptedImporter(8, new[] { "lottie" }, new[] { "json" })]
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

        // Keys every Lottie animation has at the top level of its JSON.
        static readonly byte[][] s_RequiredKeys = { Ascii("layers"), Ascii("fr"), Ascii("ip"), Ascii("op"), Ascii("w"), Ascii("h") };

        static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        /// <summary>
        /// Whether a JSON file is a Lottie (Bodymovin) animation: an object with the top-level keys every animation has
        /// ("layers", "fr", "ip", "op", "w", "h"), in any order. The file is read only until they are all found.
        /// </summary>
        public static bool LooksLikeLottie(string path)
        {
            try
            {
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                return HasLottieKeys(f);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal static bool HasLottieKeys(Stream json)
        {
            var buf = new byte[1 << 16];
            var key = new byte[8];
            int all = (1 << s_RequiredKeys.Length) - 1, found = 0, depth = 0, keyLength = 0;
            bool started = false, inString = false, escape = false, capture = false, afterString = false;
            for (int n; (n = json.Read(buf, 0, buf.Length)) > 0;)
            {
                for (int i = 0; i < n; i++)
                {
                    byte b = buf[i];
                    if (inString)
                    {
                        if (escape) { escape = false; keyLength = int.MaxValue; }
                        else if (b == '\\') escape = true;
                        else if (b == '"') { inString = false; afterString = capture; }
                        else if (capture && keyLength < key.Length) key[keyLength++] = b;
                        else keyLength = int.MaxValue;
                        continue;
                    }
                    if (b == ' ' || b == '\n' || b == '\r' || b == '\t') continue;
                    if (afterString)
                    {
                        // A string at the top level followed by ':' is a key.
                        afterString = false;
                        if (b == ':')
                        {
                            found |= KeyBit(key, keyLength);
                            if (found == all) return true;
                            continue;
                        }
                    }
                    switch (b)
                    {
                        case (byte)'{':
                        case (byte)'[':
                            if (!started && b != '{') return false;
                            started = true;
                            depth++;
                            break;
                        case (byte)'}':
                        case (byte)']':
                            if (--depth <= 0) return false;
                            break;
                        case (byte)'"':
                            inString = true;
                            capture = depth == 1;
                            keyLength = 0;
                            break;
                        default:
                            // Anything before the root object other than a UTF-8 byte order mark: not an object.
                            if (!started && b != 0xEF && b != 0xBB && b != 0xBF) return false;
                            break;
                    }
                }
            }
            return false;
        }

        static int KeyBit(byte[] key, int length)
        {
            for (int k = 0; k < s_RequiredKeys.Length; k++)
            {
                var required = s_RequiredKeys[k];
                if (required.Length != length) continue;
                int i = 0;
                while (i < length && key[i] == required[i]) i++;
                if (i == length) return 1 << k;
            }
            return 0;
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

        // Per project and user (UserSettings folder): whether the JSON files that were here before the player were checked.
        const string CheckedKey = "Mynell.Lottie.CheckedExistingJson";

        [InitializeOnLoadMethod]
        static void OnLoad()
        {
            if (SessionState.GetString(PendingKey, "").Length > 0) EditorApplication.delayCall += ApplyPending;
            UnityEditor.Editor.finishedDefaultHeaderGUI += ImportButton;
            if (!Application.isBatchMode && string.IsNullOrEmpty(EditorUserSettings.GetConfigValue(CheckedKey)))
                EditorApplication.delayCall += OfferExistingFiles;
        }

        static void ApplyPending()
        {
            string pending = SessionState.GetString(PendingKey, "");
            SessionState.EraseString(PendingKey);
            if (pending.Length == 0) return;
            var paths = new List<string>();
            foreach (var p in pending.Split('\n'))
                if (p.Length > 0 && File.Exists(p) && AssetDatabase.GetImporterOverride(p) != typeof(LottieImporter)) paths.Add(p);
            ImportAsLottie(paths);
        }

        static void ImportAsLottie(List<string> paths)
        {
            if (paths.Count == 0) return;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var p in paths) AssetDatabase.SetImporterOverride<LottieImporter>(p);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>
        /// The first time the player loads in a project: Lottie JSON files that were imported as plain text before it was
        /// installed are offered for import as animations. Scripts may use them as TextAsset, so it asks first.
        /// </summary>
        static void OfferExistingFiles()
        {
            EditorUserSettings.SetConfigValue(CheckedKey, "1");
            // Files added just now are switched without asking: only the ones that were here before are in question.
            var added = new HashSet<string>(SessionState.GetString(PendingKey, "").Split('\n'));
            ApplyPending();
            var found = LottieFilesImportedAsText();
            found.RemoveAll(added.Contains);
            if (found.Count == 0) return;
            var list = new StringBuilder();
            for (int i = 0; i < found.Count && i < 8; i++) list.Append(found[i]).Append('\n');
            if (found.Count > 8) list.Append($"and {found.Count - 8} more\n");
            string count = found.Count == 1 ? "1 Lottie animation that is" : $"{found.Count} Lottie animations that are";
            if (EditorUtility.DisplayDialog("Mynell Lottie",
                    $"This project has {count} imported as plain text, because they were added before Mynell Lottie:\n\n{list}\n" +
                    "Import them as Lottie animations? Scripts that read them as TextAsset would get a Lottie Asset instead.",
                    "Import as Lottie", "Keep as Text"))
                ImportAsLottie(found);
        }

        /// <summary>Lottie JSON files under Assets that are imported as plain text.</summary>
        internal static List<string> LottieFilesImportedAsText()
        {
            var found = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:TextAsset", new[] { "Assets" }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && LottieImporter.LooksLikeLottie(p)) found.Add(p);
            }
            return found;
        }

        static string s_ButtonPath;
        static bool s_ButtonIsLottie;
        static readonly GUIContent s_ImportButton = new GUIContent("Import as Lottie",
            "This JSON file is a Lottie animation imported as plain text. Import it as a Lottie Asset, so components can play it.");

        /// <summary>A Lottie JSON file imported as plain text gets an "Import as Lottie" button in its Inspector header.</summary>
        static void ImportButton(UnityEditor.Editor editor)
        {
            if (editor.targets.Length != 1 || !(editor.target is TextAsset)) return;
            string path = AssetDatabase.GetAssetPath(editor.target);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
            if (path != s_ButtonPath)
            {
                s_ButtonPath = path;
                s_ButtonIsLottie = LottieImporter.LooksLikeLottie(path);
            }
            if (!s_ButtonIsLottie) return;
            if (GUILayout.Button(s_ImportButton))
            {
                s_ButtonPath = null;
                ImportAsLottie(new List<string> { path });
            }
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
