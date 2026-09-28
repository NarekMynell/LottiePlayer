using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Mynell.Lottie.Compiler;
using Mynell.Lottie.Model;

namespace Mynell.Lottie
{
    /// <summary>Reads dotLottie archives (.lottie, versions 1 and 2): animations with their images and fonts.</summary>
    public static class DotLottie
    {
        /// <summary>A compiled animation of an archive.</summary>
        public readonly struct Animation
        {
            public readonly string Id;
            public readonly LottieData Data;

            public Animation(string id, LottieData data)
            {
                Id = id;
                Data = data;
            }
        }

        /// <summary>
        /// Compiles every animation of a dotLottie archive (manifest order). Fonts are looked up in the archive first,
        /// then through <paramref name="fontResolver"/>.
        /// </summary>
        public static List<Animation> Load(byte[] archive, Func<LottieFontRequest, byte[]> fontResolver = null)
        {
            var result = new List<Animation>();
            using var stream = new MemoryStream(archive, false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries) entries[e.FullName.TrimStart('/')] = e;

            var animations = new List<string>();
            if (entries.TryGetValue("manifest.json", out var manifestEntry))
            {
                var manifest = Json.JsonDocument.Parse(ReadAll(manifestEntry));
                foreach (var a in manifest.Root["animations"])
                {
                    string id = a["id"].AsString("");
                    foreach (var candidate in new[] { $"animations/{id}.json", $"a/{id}.json" })
                        if (entries.ContainsKey(candidate))
                        {
                            animations.Add(candidate);
                            break;
                        }
                }
            }
            if (animations.Count == 0)
                foreach (var name in entries.Keys)
                    if ((name.StartsWith("animations/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("a/", StringComparison.OrdinalIgnoreCase)) &&
                        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        animations.Add(name);

            byte[] ResolveImage(string rel)
            {
                string file = Path.GetFileName(rel.Replace('\\', '/'));
                foreach (var folder in new[] { "images/", "i/", "" })
                    if (entries.TryGetValue(folder + file, out var e)) return ReadAll(e);
                return null;
            }

            byte[] ResolveFont(LottieFontRequest req)
            {
                foreach (var e in entries)
                {
                    if (!(e.Key.StartsWith("f/", StringComparison.OrdinalIgnoreCase) || e.Key.StartsWith("fonts/", StringComparison.OrdinalIgnoreCase))) continue;
                    string stem = Path.GetFileNameWithoutExtension(e.Key);
                    if (stem.Equals(req.Name, StringComparison.OrdinalIgnoreCase) || stem.Equals(req.Family, StringComparison.OrdinalIgnoreCase))
                        return ReadAll(e.Value);
                }
                return null;
            }

            foreach (var path in animations)
            {
                var data = LottieCompiler.Compile(ReadAll(entries[path]), new LottieCompileOptions
                {
                    FontResolver = Text.FontLocator.Chain(ResolveFont, fontResolver),
                });
                data.Name = Path.GetFileNameWithoutExtension(path);
                LottieAsset.ResolveImages(data, ResolveImage);
                result.Add(new Animation(data.Name, data));
            }
            return result;
        }

        static byte[] ReadAll(ZipArchiveEntry e)
        {
            using var s = e.Open();
            using var ms = new MemoryStream((int)Math.Max(0, e.Length));
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
