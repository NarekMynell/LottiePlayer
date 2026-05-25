// Editor-only pipeline smoke test — logs Stage 1/2 counts for the selected MynellLottieUGUI.

using Mynell.LottiePlayer.Components;
using UnityEditor;
using UnityEngine;

namespace Mynell.LottiePlayer.Editor
{
    internal static class LottiePipelineDiagnostics
    {
        [MenuItem("Mynell/Lottie/Diagnose Selected MynellLottieUGUI", priority = 300)]
        private static void DiagnoseSelected()
        {
            var comp = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<MynellLottieUGUI>()
                : null;
            if (comp == null)
            {
                EditorUtility.DisplayDialog("Mynell Lottie", "Select a GameObject with MynellLottieUGUI.", "OK");
                return;
            }

            comp.ForceRebuildNow();
            Debug.Log($"[Mynell.Lottie Diagnose] Finished — enable Verbose on the component for per-frame logs.", comp);
        }
    }
}
