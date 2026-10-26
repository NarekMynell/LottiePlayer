using UnityEditor;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary><i>Edit &gt; Project Settings &gt; Mynell Lottie</i>: edits the settings asset (created on the first change).</summary>
    static class LottieSettingsProvider
    {
        const string PagePath = "Project/Mynell Lottie";
        const string AssetPath = "Assets/Resources/" + LottieSettings.AssetName + ".asset";

        static LottieSettingsAsset s_Defaults;
        static GUIStyle s_Margins;

        [SettingsProvider]
        static SettingsProvider Create() => new SettingsProvider(PagePath, SettingsScope.Project)
        {
            label = "Mynell Lottie",
            guiHandler = _ => Draw(),
            keywords = Keywords(),
        };

        static string[] Keywords()
        {
            var fresh = ScriptableObject.CreateInstance<LottieSettingsAsset>();
            try
            {
                return new System.Collections.Generic.List<string>(
                    SettingsProvider.GetSearchKeywordsFromSerializedObject(new SerializedObject(fresh))) { "Lottie" }.ToArray();
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }

        [MenuItem("Tools/Mynell Lottie/Settings", false, 0)]
        static void Open() => SettingsService.OpenProjectSettings(PagePath);

        static void Draw()
        {
            var asset = Resources.Load<LottieSettingsAsset>(LottieSettings.AssetName);
            bool exists = asset != null;
            if (!exists)
            {
                if (s_Defaults == null)
                {
                    s_Defaults = ScriptableObject.CreateInstance<LottieSettingsAsset>();
                    s_Defaults.hideFlags = HideFlags.HideAndDontSave;
                }
                asset = s_Defaults;
            }
            var so = new SerializedObject(asset);
            s_Margins ??= new GUIStyle { padding = new RectOffset(10, 10, 10, 0) };
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 180;
            try
            {
                using var scope = new EditorGUILayout.VerticalScope(s_Margins);
                EditorGUI.BeginChangeCheck();
                foreach (var p in Fields(so)) EditorGUILayout.PropertyField(p);
                if (EditorGUI.EndChangeCheck())
                {
                    if (exists)
                    {
                        so.ApplyModifiedProperties();
                        AssetDatabase.SaveAssetIfDirty(asset);
                    }
                    else
                    {
                        so.ApplyModifiedPropertiesWithoutUndo();
                        CreateAsset(s_Defaults);
                        Object.DestroyImmediate(s_Defaults);
                        s_Defaults = null;
                        LottieSettings.Reload();
                    }
                }
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(exists
                    ? $"Saved in {AssetDatabase.GetAssetPath(asset)}. Scripts can change these values at runtime with LottieSettings."
                    : $"Default values. Changing one creates {AssetPath}.", MessageType.None);
                if (exists && GUILayout.Button("Reset to Defaults", GUILayout.Width(140))) ResetToDefaults(so);
            }
            finally
            {
                EditorGUIUtility.labelWidth = labelWidth;
            }
        }

        static void CreateAsset(LottieSettingsAsset values)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            var asset = Object.Instantiate(values);
            asset.name = LottieSettings.AssetName;
            asset.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();
        }

        static void ResetToDefaults(SerializedObject so)
        {
            var fresh = ScriptableObject.CreateInstance<LottieSettingsAsset>();
            foreach (var p in Fields(new SerializedObject(fresh))) so.CopyFromSerializedProperty(p);
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(so.targetObject);
            Object.DestroyImmediate(fresh);
        }

        /// <summary>The settings of the asset, in declaration order (labels and tooltips come from the fields).</summary>
        static System.Collections.Generic.IEnumerable<SerializedProperty> Fields(SerializedObject so)
        {
            var it = so.GetIterator();
            for (bool enter = true; it.NextVisible(enter); enter = false)
                if (it.propertyPath != "m_Script") yield return it.Copy();
        }
    }
}
