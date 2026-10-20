using System;
using System.Collections.Generic;
using System.IO;
using Mynell.Lottie.Compiler;

namespace Mynell.Lottie.Text
{
    /// <summary>
    /// Finds font files for Lottie text (fonts that are not exported as glyphs) in given folders and in the
    /// operating system's font folders. Used at import time so the glyphs get baked into the asset.
    /// </summary>
    public sealed class FontLocator
    {
        struct Entry
        {
            public string Path;
            public string PostScript, Family, Style, TypoFamily, TypoStyle, FullName;
        }

        readonly List<string> _dirs = new List<string>();
        List<Entry> _index;
        readonly Dictionary<string, byte[]> _bytes = new Dictionary<string, byte[]>();

        static FontLocator s_System;

        /// <summary>Locator over the operating system font folders (cached).</summary>
        public static FontLocator System => s_System ??= new FontLocator(SystemFontDirectories());

        public FontLocator(IEnumerable<string> directories)
        {
            foreach (var d in directories)
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) _dirs.Add(d);
        }

        public static IEnumerable<string> SystemFontDirectories()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return "/System/Library/Fonts";
            yield return "/System/Library/Fonts/Supplemental";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library/Fonts");
            string win = Environment.GetEnvironmentVariable("WINDIR");
            if (!string.IsNullOrEmpty(win)) yield return Path.Combine(win, "Fonts");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Microsoft/Windows/Fonts");
            yield return "/system/fonts";          // Android
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local/share/fonts");
        }

        static bool IsFontFile(string p)
        {
            string e = Path.GetExtension(p).ToLowerInvariant();
            return e == ".ttf" || e == ".otf" || e == ".ttc";
        }

        void BuildIndex()
        {
            _index = new List<Entry>();
            foreach (var dir in _dirs)
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories); }
                catch (Exception) { continue; }
                foreach (var f in files)
                {
                    if (!IsFontFile(f)) continue;
                    try
                    {
                        foreach (var face in TrueTypeFont.ReadNames(f))
                        {
                            _index.Add(new Entry
                            {
                                Path = f,
                                PostScript = face.PostScriptName,
                                Family = face.Family,
                                Style = face.SubFamily,
                                TypoFamily = face.TypographicFamily,
                                TypoStyle = face.TypographicSubFamily,
                                FullName = face.FullName,
                            });
                        }
                    }
                    catch (Exception)
                    {
                        // unreadable file
                    }
                }
            }
        }

        static bool Eq(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        static string Squash(string s) => s == null ? "" : s.Replace(" ", "").Replace("-", "").ToLowerInvariant();

        /// <summary>Font file bytes for a Lottie font request, or null.</summary>
        public byte[] Resolve(LottieFontRequest req)
        {
            if (_index == null) BuildIndex();
            string path = null;
            // 1. PostScript name (fName), the most specific.
            foreach (var e in _index) if (Eq(e.PostScript, req.Name)) { path = e.Path; break; }
            // 2. family + style (typographic names first).
            if (path == null)
                foreach (var e in _index)
                {
                    string fam = string.IsNullOrEmpty(e.TypoFamily) ? e.Family : e.TypoFamily;
                    string sty = string.IsNullOrEmpty(e.TypoStyle) ? e.Style : e.TypoStyle;
                    if (Eq(fam, req.Family) && Eq(sty, req.Style)) { path = e.Path; break; }
                }
            // 3. full name "Family Style" or squashed PostScript name.
            if (path == null)
                foreach (var e in _index)
                    if (Eq(e.FullName, req.Family + " " + req.Style) || Squash(e.PostScript) == Squash(req.Family + req.Style)) { path = e.Path; break; }
            // 4. family only (regular preferred).
            if (path == null)
            {
                foreach (var e in _index)
                    if (Eq(e.Family, req.Family) && (Eq(e.Style, "Regular") || string.IsNullOrEmpty(req.Style))) { path = e.Path; break; }
                if (path == null)
                    foreach (var e in _index)
                        if (Eq(e.Family, req.Family) || Eq(e.TypoFamily, req.Family)) { path = e.Path; break; }
            }
            if (path == null) return null;
            if (!_bytes.TryGetValue(path, out var bytes))
            {
                bytes = File.ReadAllBytes(path);
                _bytes[path] = bytes;
            }
            return bytes;
        }

        /// <summary>Chains resolvers: returns the first non-null result.</summary>
        public static Func<LottieFontRequest, byte[]> Chain(params Func<LottieFontRequest, byte[]>[] resolvers) => req =>
        {
            foreach (var r in resolvers)
            {
                if (r == null) continue;
                var b = r(req);
                if (b != null) return b;
            }
            return null;
        };
    }
}
