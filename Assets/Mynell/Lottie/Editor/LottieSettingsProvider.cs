using UnityEditor;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary><i>Edit &gt; Project Settings &gt; Mynell Lottie</i>: edits the settings asset (created on the first change).</summary>
    static class LottieSettingsProvider
    {
        const string PagePath = "Project/Mynell Lottie";
        const string AssetPath = "Assets/Resources/" + LottieSettings.AssetName + ".asset";

        static readonly GUIContent s_Reset = new GUIContent("Reset to Defaults", "Set every value back to its default.");

        static LottieSettingsAsset s_Defaults;
        static SerializedObject s_Object;
        static GUIStyle s_Margins;

        [SettingsProvider]
        static SettingsProvider Create() => new SettingsProvider(PagePath, SettingsScope.Project)
        {
            label = "Mynell Lottie",
            guiHandler = _ => Draw(),
            keywords = LottieSettingsGUI.Keywords,
        };

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
                    // Not HideAndDontSave: that includes NotEditable, which greys out every field.
                    s_Defaults.hideFlags = HideFlags.DontSave;
                }
                asset = s_Defaults;
            }
            if (s_Object == null || s_Object.targetObject != asset) s_Object = new SerializedObject(asset);
            else s_Object.Update();

            s_Margins ??= new GUIStyle { padding = new RectOffset(10, 10, 6, 10) };
            using var scope = new EditorGUILayout.VerticalScope(s_Margins);
            if (LottieSettingsGUI.Draw(s_Object))
            {
                if (exists)
                {
                    s_Object.ApplyModifiedProperties();
                    AssetDatabase.SaveAssetIfDirty(asset);
                }
                else
                {
                    s_Object.ApplyModifiedPropertiesWithoutUndo();
                    asset = CreateAsset(s_Defaults);
                    exists = true;
                    Object.DestroyImmediate(s_Defaults);
                    s_Defaults = null;
                    LottieSettings.Reload();
                }
            }
            Footer(asset, exists);
        }

        static void Footer(LottieSettingsAsset asset, bool exists)
        {
            EditorGUILayout.Space(12);
            using var row = new EditorGUILayout.HorizontalScope();
            using (new EditorGUI.DisabledScope(!exists || LottieSettings.Read(asset).Equals(LottieSettings.Values.Default)))
            {
                if (GUILayout.Button(s_Reset, GUILayout.Width(140))) ResetToDefaults(asset);
            }
            GUILayout.FlexibleSpace();
            if (!exists) return;
            var path = new GUIContent("Saved in " + AssetDatabase.GetAssetPath(asset),
                "Click to show the file in the Project window. Keep it in version control, so builds and teammates use the same settings.");
            if (GUILayout.Button(path, EditorStyles.linkLabel)) EditorGUIUtility.PingObject(asset);
        }

        static LottieSettingsAsset CreateAsset(LottieSettingsAsset values)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
            var asset = Object.Instantiate(values);
            asset.name = LottieSettings.AssetName;
            asset.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(asset, AssetPath);
            AssetDatabase.SaveAssets();
            return asset;
        }

        static void ResetToDefaults(LottieSettingsAsset asset)
        {
            var fresh = ScriptableObject.CreateInstance<LottieSettingsAsset>();
            try
            {
                var so = new SerializedObject(asset);
                var it = new SerializedObject(fresh).GetIterator();
                for (bool enter = true; it.NextVisible(enter); enter = false)
                    if (it.propertyPath != "m_Script") so.CopyFromSerializedProperty(it);
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssetIfDirty(asset);
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }
        }
    }

    /// <summary>The settings controls, shared by the Project Settings page and the Inspector of the settings asset.</summary>
    static class LottieSettingsGUI
    {
        // Where a limit starts when it is switched on.
        const int FirstFrameRateLimit = 30, FirstThreadLimit = 2;

        public static readonly string[] Keywords =
            { "Lottie", "Resolution", "Scale", "Quality", "Frame Rate", "FPS", "Threads", "Cores", "Performance", "Cache", "Shapes" };

        static readonly GUIContent s_Intro = new GUIContent(
            "These settings apply to every Lottie animation in the project. Scripts can change them while the game runs, " +
            "through the LottieSettings class.");
        static readonly GUIContent s_Quality = new GUIContent("Quality");
        static readonly GUIContent s_Performance = new GUIContent("Performance");
        static readonly GUIContent s_ResolutionScale = new GUIContent("Resolution Scale", LottieSettingsAsset.ResolutionScaleTip);
        static readonly GUIContent s_LimitFrameRate = new GUIContent("Limit Frame Rate",
            "Update playing animations less often, to save CPU time and battery.\n\nWhen off, a playing animation draws a " +
            "new image every frame the game renders.");
        static readonly GUIContent s_MaxFrameRate = new GUIContent("Max Frame Rate", LottieSettingsAsset.MaxFrameRateTip);
        static readonly GUIContent s_LimitThreads = new GUIContent("Limit Threads",
            "Split each frame across fewer CPU threads, to leave threads free for the rest of the game.\n\nWhen off, one " +
            "frame can use every worker thread, which is the fastest for large animations.");
        static readonly GUIContent s_Threads = new GUIContent("Threads Per Animation", LottieSettingsAsset.ThreadsPerAnimationTip);
        static readonly GUIContent s_CacheShapes = new GUIContent("Cache Shapes", LottieSettingsAsset.CacheShapesTip);

        /// <summary>Draws the controls of a <see cref="LottieSettingsAsset"/>. Returns true when the user changed a value.</summary>
        public static bool Draw(SerializedObject so)
        {
            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(labelWidth, 200);
            EditorGUI.BeginChangeCheck();
            try
            {
                EditorGUILayout.LabelField(s_Intro, EditorStyles.wordWrappedMiniLabel);

                Header(s_Quality);
                EditorGUILayout.Slider(so.FindProperty("m_ResolutionScale"), 0.25f, 2f, s_ResolutionScale);

                Header(s_Performance);
                Limit(so.FindProperty("m_MaxFrameRate"), s_LimitFrameRate, s_MaxFrameRate, 1, 120, FirstFrameRateLimit);
                Limit(so.FindProperty("m_ThreadsPerAnimation"), s_LimitThreads, s_Threads, 1, 16, FirstThreadLimit);
                EditorGUILayout.PropertyField(so.FindProperty("m_CacheShapes"), s_CacheShapes);
            }
            finally
            {
                EditorGUIUtility.labelWidth = labelWidth;
            }
            return EditorGUI.EndChangeCheck();
        }

        static void Header(GUIContent label)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        }

        /// <summary>An int setting where 0 means "no limit": a toggle, and a slider for the limit while it is on.</summary>
        static void Limit(SerializedProperty value, GUIContent toggle, GUIContent label, int min, int max, int first)
        {
            EditorGUI.BeginChangeCheck();
            bool on = EditorGUILayout.Toggle(toggle, value.intValue > 0);
            if (EditorGUI.EndChangeCheck()) value.intValue = on ? first : 0;
            if (value.intValue <= 0) return;
            EditorGUI.indentLevel++;
            EditorGUILayout.IntSlider(value, min, max, label);
            EditorGUI.indentLevel--;
        }
    }

    /// <summary>The settings asset shows the same controls as the Project Settings page.</summary>
    [CustomEditor(typeof(LottieSettingsAsset))]
    sealed class LottieSettingsAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            LottieSettingsGUI.Draw(serializedObject);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
