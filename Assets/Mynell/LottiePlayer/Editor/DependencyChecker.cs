// Mynell Lottie Player — Auto-validates required Unity packages on script reload.
// On missing dependency, opens DependencyResolverWindow with one-click install.

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace Mynell.LottiePlayer.Editor
{
    [InitializeOnLoad]
    internal static class DependencyChecker
    {
        // (package id, minimum version, friendly name, purpose)
        internal static readonly (string id, string version, string label, string purpose)[] Required =
        {
            ("com.unity.burst",        "1.8.0",  "Burst",        "JIT/AOT compilation of math/tessellation jobs (60+ FPS)."),
            ("com.unity.mathematics",  "1.3.0",  "Mathematics",  "float2/float4/math intrinsics for vector geometry."),
            ("com.unity.collections",  "2.1.0",  "Collections",  "NativeArray<T> / Allocator.Persistent backing storage."),
        };

        private const string SessionKey = "Mynell.LottiePlayer.DepsChecked";

        private static ListRequest _listRequest;

        static DependencyChecker()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            // Defer to avoid blocking script reload.
            EditorApplication.delayCall += Begin;
        }

        private static void Begin()
        {
            _listRequest = Client.List(offlineMode: true, includeIndirectDependencies: false);
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (_listRequest == null || !_listRequest.IsCompleted) return;
            EditorApplication.update -= Poll;
            SessionState.SetBool(SessionKey, true);

            if (_listRequest.Status != StatusCode.Success)
            {
                UnityEngine.Debug.LogWarning("[Mynell.LottiePlayer] Could not query Package Manager: " + _listRequest.Error?.message);
                return;
            }

            var installed = new HashSet<string>();
            foreach (var p in _listRequest.Result) installed.Add(p.name);

            var missing = new List<(string id, string version, string label, string purpose)>();
            foreach (var req in Required)
            {
                if (!installed.Contains(req.id)) missing.Add(req);
            }

            _listRequest = null;
            if (missing.Count > 0)
            {
                DependencyResolverWindow.Open(missing);
            }
        }

        /// <summary>Public re-check from menu.</summary>
        [MenuItem("Mynell/Lottie Player/Check Dependencies", priority = 100)]
        public static void ReCheck()
        {
            SessionState.SetBool(SessionKey, false);
            Begin();
        }
    }
}
