using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

// The version defines of this assembly (see the .asmdef). Define constraints can't say "one of them is missing":
// Unity reads "!A || !B" as "neither A nor B".
#if !(MYNELL_LOTTIE_BURST && MYNELL_LOTTIE_COLLECTIONS && MYNELL_LOTTIE_MATHEMATICS && MYNELL_LOTTIE_UGUI)
namespace Mynell.Lottie.Editor
{
    /// <summary>
    /// Installs the packages the player needs when they are missing. That only happens when the player is copied into
    /// Assets: the Package Manager installs them by itself. The other assemblies of the player are compiled only when
    /// all of them are present, so a project without them has no compile errors. This assembly is always compiled,
    /// but its code only while a package is missing: once they are all there it is empty.
    /// </summary>
    static class LottieDependencies
    {
        // The same packages and versions as package.json and the version defines of the assemblies.
        static readonly (string Id, string Name, string MinVersion)[] Required =
        {
            ("com.unity.burst", "Burst", "1.8.0"),
            ("com.unity.collections", "Collections", "2.1.0"),
            ("com.unity.mathematics", "Mathematics", "1.3.0"),
            ("com.unity.ugui", "Unity UI", "2.0.0"),
        };

        const string Title = "Mynell Lottie";
        const string MenuPath = "Tools/Mynell Lottie/Install Required Packages";
        const string AskedKey = "Mynell.Lottie.AskedToInstall";

        static AddAndRemoveRequest s_Request;

        [InitializeOnLoadMethod]
        static void OnLoad()
        {
            // Asked once per editor session; the menu item is there for later.
            if (SessionState.GetBool(AskedKey, false)) return;
            SessionState.SetBool(AskedKey, true);
            EditorApplication.delayCall += Ask;
        }

        static void Ask()
        {
            var missing = Missing();
            if (missing.Count == 0) return;
            if (Application.isBatchMode)
            {
                Debug.LogWarning($"{Title} is turned off until these packages are installed: {List(missing, ", ")}. " +
                                 "Add them to Packages/manifest.json, or run " + MenuPath.Replace("/", " > ") + " in the editor.");
                return;
            }
            bool install = EditorUtility.DisplayDialog(Title,
                "Mynell Lottie needs these packages, and this project doesn't have them yet:\n\n" + List(missing, "\n") +
                "\n\nInstall them now? The player stays turned off until they are installed.",
                "Install", "Not Now");
            if (install) Install(missing);
            else
                Debug.LogWarning($"{Title} is turned off until {List(missing, ", ")} are installed. " +
                                 "Install them with " + MenuPath.Replace("/", " > ") + ".");
        }

        [MenuItem(MenuPath, false, 0)]
        static void InstallMenu()
        {
            var missing = Missing();
            if (missing.Count == 0) Debug.Log($"{Title}: all the packages it needs are installed.");
            else Install(missing);
        }

        static void Install(List<(string Id, string Name, string MinVersion)> missing)
        {
            if (s_Request != null && !s_Request.IsCompleted) return;
            Debug.Log($"{Title}: installing {List(missing, ", ")}.");
            // No version: the Package Manager picks the one recommended for this editor (it is newer than the minimums).
            s_Request = Client.AddAndRemove(missing.Select(p => p.Id).ToArray());
            EditorApplication.update += WaitForInstall;
        }

        static void WaitForInstall()
        {
            if (s_Request == null || !s_Request.IsCompleted) return;
            EditorApplication.update -= WaitForInstall;
            // On success the scripts recompile with the player turned on, and this class is gone.
            if (s_Request.Status == StatusCode.Failure)
            {
                string error = s_Request.Error?.message ?? "unknown error";
                Debug.LogError($"{Title}: couldn't install the packages it needs: {error}");
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog(Title, "Couldn't install the packages:\n\n" + error +
                                                       "\n\nYou can install them in Window > Package Manager.", "OK");
            }
            s_Request = null;
        }

        static List<(string Id, string Name, string MinVersion)> Missing()
        {
            var installed = new Dictionary<string, string>();
            foreach (var p in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()) installed[p.name] = p.version;
            return Required.Where(r => !installed.TryGetValue(r.Id, out var v) || !AtLeast(v, r.MinVersion)).ToList();
        }

        static string List(List<(string Id, string Name, string MinVersion)> packages, string separator) =>
            string.Join(separator, packages.Select(p => $"{p.Name} {p.MinVersion} or newer"));

        /// <summary><paramref name="version"/> &gt;= <paramref name="min"/>, where a pre-release is below its release.</summary>
        static bool AtLeast(string version, string min)
        {
            int[] a = Numbers(version, out bool preRelease), b = Numbers(min, out _);
            for (int i = 0; i < 3; i++)
                if (a[i] != b[i]) return a[i] > b[i];
            return !preRelease;
        }

        static int[] Numbers(string version, out bool preRelease)
        {
            int cut = version.IndexOfAny(new[] { '-', '+' });
            preRelease = cut >= 0 && version[cut] == '-';
            var parts = (cut >= 0 ? version.Substring(0, cut) : version).Split('.');
            var numbers = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++) int.TryParse(parts[i], out numbers[i]);
            return numbers;
        }
    }
}
#endif
