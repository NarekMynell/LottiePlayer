using UnityEditor;
using UnityEditor.UI;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary>Shared inspector pieces of the Lottie components.</summary>
    static class LottieEditorGUI
    {
        static GUIContent s_Play, s_Pause, s_Stop;

        public static void Header(string title)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        /// <summary>Unsupported features / import notes of the assigned asset.</summary>
        public static void AssetNotes(LottieAsset asset)
        {
            if (asset == null) return;
            if (asset.Unsupported.Length > 0)
                EditorGUILayout.HelpBox("Not rendered: " + string.Join(", ", asset.Unsupported), MessageType.Warning);
        }

        /// <summary>Loop count is only meaningful for looping modes.</summary>
        public static void Playback(SerializedProperty playOnEnable, SerializedProperty loopMode, SerializedProperty loopCount,
            SerializedProperty speed, SerializedProperty unscaledTime, SerializedProperty subframes)
        {
            Header("Playback");
            EditorGUILayout.PropertyField(playOnEnable);
            EditorGUILayout.PropertyField(loopMode);
            if (loopMode.hasMultipleDifferentValues || loopMode.enumValueIndex != (int)LottieLoopMode.Once)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(loopCount, new GUIContent("Loop Count", "Number of loops before completing; 0 = forever."));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.PropertyField(speed);
            EditorGUILayout.PropertyField(unscaledTime);
            EditorGUILayout.PropertyField(subframes, new GUIContent("Subframes", "Render fractional frames (smooth motion). Off snaps to whole frames."));
        }

        /// <summary>
        /// Edit mode: a scrubber that previews any frame (stored in the component). Play mode: transport controls.
        /// </summary>
        public static void Preview(SerializedObject so, SerializedProperty previewTime, LottieAsset asset, LottiePlayer player,
            System.Action play, System.Action pause, System.Action stop)
        {
            if (asset == null) return;
            Header("Preview");
            if (!Application.isPlaying)
            {
                EditorGUI.BeginChangeCheck();
                float frames = Mathf.Max(0, asset.TotalFrames - 1);
                float frame = Mathf.Round(previewTime.floatValue * frames * 100f) / 100f;
                frame = EditorGUILayout.Slider(new GUIContent("Frame", "Frame shown in edit mode."), frame, 0, frames);
                if (EditorGUI.EndChangeCheck()) previewTime.floatValue = frames > 0 ? frame / frames : 0;
                MarkerPopup(asset, f => previewTime.floatValue = frames > 0 ? Mathf.Clamp01((f - asset.InPoint) / frames) : 0);
                return;
            }
            if (player == null) return;
            s_Play ??= EditorGUIUtility.TrIconContent("PlayButton", "Play");
            s_Pause ??= EditorGUIUtility.TrIconContent("PauseButton", "Pause");
            s_Stop ??= EditorGUIUtility.TrIconContent("PreMatte", "Stop (rewind)");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(player.IsPlaying ? s_Pause : s_Play, EditorStyles.miniButtonLeft, GUILayout.Width(32)))
                {
                    if (player.IsPlaying) pause();
                    else play();
                }
                if (GUILayout.Button(s_Stop, EditorStyles.miniButtonRight, GUILayout.Width(32))) stop();
                EditorGUI.BeginChangeCheck();
                float t = GUILayout.HorizontalSlider(player.NormalizedTime, 0, 1);
                if (EditorGUI.EndChangeCheck())
                {
                    pause();
                    player.NormalizedTime = t;
                }
                GUILayout.Label($"{player.Frame:0.0}", EditorStyles.miniLabel, GUILayout.Width(44));
            }
            MarkerPopup(asset, f => player.GoToAndStop(f));
        }

        static void MarkerPopup(LottieAsset asset, System.Action<float> goTo)
        {
            if (asset.MarkerCount == 0) return;
            var names = new string[asset.MarkerCount + 1];
            names[0] = "Go to marker...";
            for (int i = 0; i < asset.MarkerCount; i++) names[i + 1] = asset.GetMarkerName(i);
            int pick = EditorGUILayout.Popup(" ", 0, names);
            if (pick > 0) goTo(asset.GetMarkerTime(pick - 1));
        }
    }

    [CustomEditor(typeof(LottieImage))]
    [CanEditMultipleObjects]
    sealed class LottieImageEditor : GraphicEditor
    {
        SerializedProperty m_Asset, m_PlayOnEnable, m_LoopMode, m_LoopCount, m_Speed, m_UnscaledTime, m_Subframes;
        SerializedProperty m_PreserveAspect, m_ResolutionScale, m_MaxTextureSize, m_EditorPreviewTime, m_OnComplete;

        protected override void OnEnable()
        {
            base.OnEnable();
            m_Asset = serializedObject.FindProperty("m_Asset");
            m_PlayOnEnable = serializedObject.FindProperty("m_PlayOnEnable");
            m_LoopMode = serializedObject.FindProperty("m_LoopMode");
            m_LoopCount = serializedObject.FindProperty("m_LoopCount");
            m_Speed = serializedObject.FindProperty("m_Speed");
            m_UnscaledTime = serializedObject.FindProperty("m_UnscaledTime");
            m_Subframes = serializedObject.FindProperty("m_Subframes");
            m_PreserveAspect = serializedObject.FindProperty("m_PreserveAspect");
            m_ResolutionScale = serializedObject.FindProperty("m_ResolutionScale");
            m_MaxTextureSize = serializedObject.FindProperty("m_MaxTextureSize");
            m_EditorPreviewTime = serializedObject.FindProperty("m_EditorPreviewTime");
            m_OnComplete = serializedObject.FindProperty("m_OnComplete");
            SetShowNativeSize(true, true);
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var image = (LottieImage)target;
            EditorGUILayout.PropertyField(m_Asset);
            if (!m_Asset.hasMultipleDifferentValues) LottieEditorGUI.AssetNotes(image.Asset);
            AppearanceControlsGUI();
            RaycastControlsGUI();
            MaskableControlsGUI();
            EditorGUILayout.PropertyField(m_PreserveAspect);
            NativeSizeButtonGUI();

            LottieEditorGUI.Playback(m_PlayOnEnable, m_LoopMode, m_LoopCount, m_Speed, m_UnscaledTime, m_Subframes);

            LottieEditorGUI.Header("Resolution");
            EditorGUILayout.PropertyField(m_ResolutionScale, new GUIContent("Resolution Scale",
                "Texture resolution relative to the element's on-screen pixel size (times the project's Resolution Scale)."));
            EditorGUILayout.PropertyField(m_MaxTextureSize);
            if (targets.Length == 1 && image.isActiveAndEnabled && image.Player.Texture != null)
                EditorGUILayout.LabelField(" ", $"{image.Player.Width} x {image.Player.Height} px", EditorStyles.miniLabel);

            if (targets.Length == 1)
                LottieEditorGUI.Preview(serializedObject, m_EditorPreviewTime, image.Asset, image.isActiveAndEnabled ? image.Player : null,
                    image.Play, image.Pause, image.Stop);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(m_OnComplete);
            serializedObject.ApplyModifiedProperties();
        }
    }

    [CustomEditor(typeof(LottieRenderer))]
    [CanEditMultipleObjects]
    sealed class LottieRendererEditor : UnityEditor.Editor
    {
        SerializedProperty m_Asset, m_PlayOnEnable, m_LoopMode, m_LoopCount, m_Speed, m_UnscaledTime, m_Subframes;
        SerializedProperty m_Color, m_PixelsPerUnit, m_Pivot, m_TextureScale, m_MaxTextureSize, m_FlipX, m_FlipY;
        SerializedProperty m_EditorPreviewTime, m_OnComplete;

        void OnEnable()
        {
            m_Asset = serializedObject.FindProperty("m_Asset");
            m_PlayOnEnable = serializedObject.FindProperty("m_PlayOnEnable");
            m_LoopMode = serializedObject.FindProperty("m_LoopMode");
            m_LoopCount = serializedObject.FindProperty("m_LoopCount");
            m_Speed = serializedObject.FindProperty("m_Speed");
            m_UnscaledTime = serializedObject.FindProperty("m_UnscaledTime");
            m_Subframes = serializedObject.FindProperty("m_Subframes");
            m_Color = serializedObject.FindProperty("m_Color");
            m_PixelsPerUnit = serializedObject.FindProperty("m_PixelsPerUnit");
            m_Pivot = serializedObject.FindProperty("m_Pivot");
            m_TextureScale = serializedObject.FindProperty("m_TextureScale");
            m_MaxTextureSize = serializedObject.FindProperty("m_MaxTextureSize");
            m_FlipX = serializedObject.FindProperty("m_FlipX");
            m_FlipY = serializedObject.FindProperty("m_FlipY");
            m_EditorPreviewTime = serializedObject.FindProperty("m_EditorPreviewTime");
            m_OnComplete = serializedObject.FindProperty("m_OnComplete");
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var renderer = (LottieRenderer)target;
            EditorGUILayout.PropertyField(m_Asset);
            if (!m_Asset.hasMultipleDifferentValues) LottieEditorGUI.AssetNotes(renderer.Asset);
            EditorGUILayout.PropertyField(m_Color);

            LottieEditorGUI.Header("Size");
            EditorGUILayout.PropertyField(m_PixelsPerUnit);
            EditorGUILayout.PropertyField(m_Pivot);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Flip");
                m_FlipX.boolValue = GUILayout.Toggle(m_FlipX.boolValue, "X", EditorStyles.miniButtonLeft, GUILayout.Width(32));
                m_FlipY.boolValue = GUILayout.Toggle(m_FlipY.boolValue, "Y", EditorStyles.miniButtonRight, GUILayout.Width(32));
            }

            LottieEditorGUI.Playback(m_PlayOnEnable, m_LoopMode, m_LoopCount, m_Speed, m_UnscaledTime, m_Subframes);

            LottieEditorGUI.Header("Resolution");
            EditorGUILayout.PropertyField(m_TextureScale, new GUIContent("Texture Scale", "Texture resolution relative to the composition size (times the project's Resolution Scale)."));
            EditorGUILayout.PropertyField(m_MaxTextureSize);
            if (targets.Length == 1 && renderer.isActiveAndEnabled && renderer.Player.Texture != null)
                EditorGUILayout.LabelField(" ", $"{renderer.Player.Width} x {renderer.Player.Height} px", EditorStyles.miniLabel);

            if (targets.Length == 1)
                LottieEditorGUI.Preview(serializedObject, m_EditorPreviewTime, renderer.Asset, renderer.isActiveAndEnabled ? renderer.Player : null,
                    renderer.Play, renderer.Pause, renderer.Stop);

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(m_OnComplete);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
