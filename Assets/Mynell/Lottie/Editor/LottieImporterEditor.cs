using System;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary>Import settings of Lottie files. Only .lottie archives have one; JSON files show just the animation.</summary>
    [CustomEditor(typeof(LottieImporter))]
    [CanEditMultipleObjects]
    sealed class LottieImporterEditor : ScriptedImporterEditor
    {
        static readonly GUIContent s_MainAnimation = new GUIContent("Main Animation",
            "For .lottie files with several animations: which one becomes the main asset, counting from 0. The others " +
            "are added as sub-assets of the file.");

        SerializedProperty m_MainAnimation;

        public override void OnEnable()
        {
            base.OnEnable();
            m_MainAnimation = serializedObject.FindProperty("m_MainAnimation");
        }

        protected override bool needsApplyRevert => HasArchive();

        public override void OnInspectorGUI()
        {
            if (!HasArchive()) return;
            serializedObject.Update();
            EditorGUILayout.PropertyField(m_MainAnimation, s_MainAnimation);
            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        bool HasArchive()
        {
            foreach (var t in targets)
                if (t is AssetImporter importer && importer.assetPath.EndsWith(".lottie", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
