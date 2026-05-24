// Mynell Lottie Player — Inspector for the UGUI component.

using Mynell.LottiePlayer.Components;
using UnityEditor;
using UnityEditor.UI;
using UnityEngine;

namespace Mynell.LottiePlayer.Editor
{
    [CustomEditor(typeof(MynellLottieUGUI))]
    [CanEditMultipleObjects]
    public sealed class MynellLottieUGUIEditor : GraphicEditor
    {
        public override void OnInspectorGUI()
        {
            DrawValidationReport();
            EditorGUILayout.Space();
            DrawDefaultInspector();
        }

        private void DrawValidationReport()
        {
            var tgt = (MynellLottieUGUI)target;
            var asset = tgt.Asset;

            if (asset == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign a baked Lottie Asset. Select your .json → Assets → Mynell → Bake Selected Lottie JSON → LottieAsset.",
                    MessageType.Info);
                return;
            }

            if (!asset.IsBaked)
            {
                EditorGUILayout.HelpBox(
                    "Lottie Asset is empty (FrameRate/Width/Height are 0). Re-bake the source JSON from the Project menu.",
                    MessageType.Warning);
                return;
            }

            var rep = asset.ValidationReport;
            if (rep.HasIssues)
            {
                bool hard = rep.UnsupportedLayerCount > 0 || rep.UnsupportedShapeCount > 0 ||
                            rep.DroppedPathSnapshots > 0 || rep.DegradedAnimatedPaths > 0 ||
                            rep.MalformedSnapshotKeyframes > 0;
                var msgType = hard ? MessageType.Warning : MessageType.Info;
                var lines = new System.Text.StringBuilder();
                if (rep.UnsupportedLayerCount > 0) lines.AppendLine($"• {rep.UnsupportedLayerCount} unsupported layer(s)");
                if (rep.UnsupportedShapeCount > 0) lines.AppendLine($"• {rep.UnsupportedShapeCount} unsupported shape(s)");
                if (rep.DroppedPathSnapshots > 0) lines.AppendLine($"• {rep.DroppedPathSnapshots} path snapshot(s) dropped");
                if (rep.DegradedAnimatedPaths > 0) lines.AppendLine($"• {rep.DegradedAnimatedPaths} animated path(s) topology-locked");
                if (rep.MalformedSnapshotKeyframes > 0) lines.AppendLine($"• {rep.MalformedSnapshotKeyframes} malformed keyframe(s)");
                if (rep.ApproximatedShapes > 0) lines.AppendLine($"• {rep.ApproximatedShapes} approximated shape(s) (gf/gs/mm)");
                EditorGUILayout.HelpBox(lines.ToString().TrimEnd(), msgType);
            }
        }
    }
}
