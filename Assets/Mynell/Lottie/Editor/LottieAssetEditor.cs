using UnityEditor;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary>Inspector for <see cref="LottieAsset"/>: metadata, diagnostics and an animated preview.</summary>
    [CustomEditor(typeof(LottieAsset))]
    [CanEditMultipleObjects]
    sealed class LottieAssetEditor : UnityEditor.Editor
    {
        LottiePlayer _player;
        Material _material;
        bool _playing = true;
        double _lastTime;
        static bool s_ShowKeyPaths;
        string[] _keyPaths;
        string _keyPathFilter = "";
        Vector2 _keyPathScroll;

        void OnEnable()
        {
            EditorApplication.update += OnUpdate;
            _lastTime = EditorApplication.timeSinceStartup;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnUpdate;
            _player?.Dispose();
            _player = null;
            if (_material != null) DestroyImmediate(_material);
        }

        void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _lastTime);
            _lastTime = now;
            if (_player == null || !_playing) return;
            _player.Advance(dt);
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var asset = (LottieAsset)target;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Size", $"{asset.Width} × {asset.Height}");
                EditorGUILayout.LabelField("Frame rate", $"{asset.FrameRate:0.##} fps");
                EditorGUILayout.LabelField("Frames", $"{asset.InPoint:0.##} – {asset.OutPoint:0.##} ({asset.TotalFrames:0.##})");
                EditorGUILayout.LabelField("Duration", $"{asset.Duration:0.###} s");
                EditorGUILayout.LabelField("Bodymovin version", string.IsNullOrEmpty(asset.LottieVersion) ? "-" : asset.LottieVersion);
                EditorGUILayout.LabelField("Compiled size", EditorUtility.FormatBytes(asset.DataSize));
            }
            if (asset.MarkerCount > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Markers", EditorStyles.boldLabel);
                for (int i = 0; i < asset.MarkerCount; i++)
                    EditorGUILayout.LabelField(asset.GetMarkerName(i), $"frame {asset.GetMarkerTime(i):0.##}, {asset.GetMarkerDuration(i):0.##} frames");
            }
            if (asset.Unsupported.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox("Not rendered:\n• " + string.Join("\n• ", asset.Unsupported), MessageType.Warning);
            }
            if (asset.Warnings.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(string.Join("\n", asset.Warnings), MessageType.Info);
            }
            if (targets.Length == 1) KeyPathsGUI(asset);
        }

        /// <summary>Key paths for dynamic properties (LottiePlayer.SetColor & co), filterable and selectable for copying.</summary>
        void KeyPathsGUI(LottieAsset asset)
        {
            EditorGUILayout.Space();
            s_ShowKeyPaths = EditorGUILayout.Foldout(s_ShowKeyPaths, "Key Paths", true);
            if (!s_ShowKeyPaths) return;
            if (_keyPaths == null)
            {
                var anim = asset.Animation;
                _keyPaths = anim != null ? LottieKeyPath.Enumerate(anim.Data).ToArray() : System.Array.Empty<string>();
            }
            _keyPathFilter = EditorGUILayout.TextField("Filter", _keyPathFilter);
            _keyPathScroll = EditorGUILayout.BeginScrollView(_keyPathScroll, GUILayout.MaxHeight(220));
            float line = EditorGUIUtility.singleLineHeight;
            foreach (var path in _keyPaths)
            {
                if (_keyPathFilter.Length > 0 && path.IndexOf(_keyPathFilter, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                EditorGUILayout.SelectableLabel(path, EditorStyles.miniLabel, GUILayout.Height(line));
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField("Use with LottiePlayer.SetColor / SetOpacity / ... ('*' and '**' wildcards).", EditorStyles.miniLabel);
        }

        public override bool HasPreviewGUI() => targets.Length == 1 && target is LottieAsset a && a.DataSize > 0;

        public override GUIContent GetPreviewTitle() => new GUIContent(target.name);

        public override void OnPreviewSettings()
        {
            if (GUILayout.Button(_playing ? "❚❚" : "▶", EditorStyles.toolbarButton, GUILayout.Width(28))) _playing = !_playing;
            if (_player != null)
            {
                float f = GUILayout.HorizontalSlider(_player.NormalizedTime, 0, 1, GUILayout.Width(120));
                if (!Mathf.Approximately(f, _player.NormalizedTime))
                {
                    _playing = false;
                    _player.NormalizedTime = f;
                }
                GUILayout.Label($"{_player.Frame:0.0}", EditorStyles.miniLabel, GUILayout.Width(40));
            }
        }

        public override void OnInteractivePreviewGUI(Rect r, GUIStyle background) => DrawAnimatedPreview(r);

        public override void OnPreviewGUI(Rect r, GUIStyle background) => DrawAnimatedPreview(r);

        void DrawAnimatedPreview(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            var asset = (LottieAsset)target;
            if (asset.Width <= 0 || asset.Height <= 0) return;
            float aspect = asset.Width / asset.Height;
            Rect fit = r;
            if (r.width / r.height > aspect)
            {
                fit.width = r.height * aspect;
                fit.x += (r.width - fit.width) * 0.5f;
            }
            else
            {
                fit.height = r.width / aspect;
                fit.y += (r.height - fit.height) * 0.5f;
            }
            if (fit.width < 2 || fit.height < 2) return;

            if (_player == null || _player.Asset != asset)
            {
                _player?.Dispose();
                _player = new LottiePlayer(asset) { LoopMode = LottieLoopMode.Loop };
                _player.Play();
            }
            float ppp = EditorGUIUtility.pixelsPerPoint;
            _player.SetSize(Mathf.CeilToInt(fit.width * ppp), Mathf.CeilToInt(fit.height * ppp));
            _player.RenderNow();
            if (_player.Texture == null) return;

            // Checkerboard shows transparency.
            EditorGUI.DrawTextureTransparent(fit, Texture2D.blackTexture, ScaleMode.StretchToFill);
            if (_material == null)
            {
                var shader = Resources.Load<Shader>("MynellLottie/LottieUnlit");
                if (shader != null) _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_material != null) EditorGUI.DrawPreviewTexture(fit, _player.Texture, _material, ScaleMode.StretchToFill);
            else GUI.DrawTexture(fit, _player.Texture, ScaleMode.StretchToFill);
        }

        public override Texture2D RenderStaticPreview(string assetPath, Object[] subAssets, int width, int height)
        {
            var asset = (LottieAsset)target;
            if (asset == null || asset.DataSize == 0 || asset.Width <= 0) return null;
            using var player = new LottiePlayer(asset);
            player.SetSize(width, height);
            player.ScaleMode = LottieScaleMode.Fit;
            player.NormalizedTime = 0.5f;
            player.RenderNow();
            if (player.Texture == null) return null;
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var src = player.Texture.GetRawTextureData<uint>();
            var dst = tex.GetRawTextureData<uint>();
            for (int i = 0; i < src.Length && i < dst.Length; i++) dst[i] = LottieDebugTools.Unpremultiply(src[i]);
            tex.Apply();
            return tex;
        }
    }
}
