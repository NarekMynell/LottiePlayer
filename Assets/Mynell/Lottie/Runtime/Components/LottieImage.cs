using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Mynell.Lottie
{
    /// <summary>
    /// UGUI graphic that plays a <see cref="LottieAsset"/>. The animation is rasterized at the element's on-screen pixel
    /// size (times <see cref="ResolutionScale"/>), so it stays sharp at any canvas scale. Works with Mask / RectMask2D.
    /// </summary>
    [AddComponentMenu("UI/Lottie Image")]
    [RequireComponent(typeof(CanvasRenderer))]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class LottieImage : MaskableGraphic
    {
        [SerializeField] LottieAsset m_Asset;
        [SerializeField] bool m_PlayOnEnable = true;
        [SerializeField] LottieLoopMode m_LoopMode = LottieLoopMode.Loop;
        [SerializeField, Min(0)] int m_LoopCount;
        [SerializeField] float m_Speed = 1f;
        [SerializeField] bool m_UnscaledTime;
        [SerializeField] bool m_Subframes = true;
        [SerializeField] bool m_PreserveAspect = true;
        [SerializeField, Range(0.1f, 4f)] float m_ResolutionScale = 1f;
        [SerializeField, Range(16, 8192)] int m_MaxTextureSize = 2048;
        [SerializeField, Range(0f, 1f)] float m_EditorPreviewTime;
        [SerializeField] UnityEvent m_OnComplete = new UnityEvent();

        LottiePlayer m_Player;
        Texture m_BoundTexture;
        static Material s_DefaultMaterial;

        /// <summary>The playback / render controller behind this graphic.</summary>
        public LottiePlayer Player
        {
            get
            {
                EnsurePlayer();
                return m_Player;
            }
        }

        public LottieAsset Asset
        {
            get => m_Asset;
            set
            {
                if (m_Asset == value) return;
                m_Asset = value;
                if (m_Player != null)
                {
                    m_Player.Asset = value;
                    UpdateRenderSize();
                    if (Application.isPlaying && m_PlayOnEnable && isActiveAndEnabled) m_Player.Play();
                    else m_Player.SetDirty();
                }
                SetAllDirty();
            }
        }

        public bool PlayOnEnable { get => m_PlayOnEnable; set => m_PlayOnEnable = value; }
        public LottieLoopMode LoopMode { get => m_LoopMode; set { m_LoopMode = value; ApplySettings(); } }
        public int LoopCount { get => m_LoopCount; set { m_LoopCount = Mathf.Max(0, value); ApplySettings(); } }
        public float Speed { get => m_Speed; set { m_Speed = value; ApplySettings(); } }
        public bool UnscaledTime { get => m_UnscaledTime; set { m_UnscaledTime = value; ApplySettings(); } }
        public bool Subframes { get => m_Subframes; set { m_Subframes = value; ApplySettings(); } }
        public bool PreserveAspect { get => m_PreserveAspect; set { m_PreserveAspect = value; UpdateRenderSize(); SetVerticesDirty(); } }
        public float ResolutionScale { get => m_ResolutionScale; set { m_ResolutionScale = Mathf.Clamp(value, 0.1f, 4f); UpdateRenderSize(); } }
        public int MaxTextureSize { get => m_MaxTextureSize; set { m_MaxTextureSize = Mathf.Clamp(value, 16, 8192); UpdateRenderSize(); } }
        public UnityEvent OnComplete => m_OnComplete;

        public bool IsPlaying => m_Player != null && m_Player.IsPlaying;
        public float Frame { get => Player.Frame; set => Player.Frame = value; }
        public float NormalizedTime { get => Player.NormalizedTime; set => Player.NormalizedTime = value; }

        public void Play() => Player.Play();
        public void Pause() => Player.Pause();
        public void Stop() => Player.Stop();
        public void GoToAndStop(float frame) => Player.GoToAndStop(frame);
        public void GoToAndPlay(float frame) => Player.GoToAndPlay(frame);
        public void PlaySegment(float startFrame, float endFrame) => Player.PlaySegment(startFrame, endFrame);
        public bool PlayMarker(string markerName) => Player.PlayMarker(markerName);

        public override Texture mainTexture
        {
            get
            {
                var t = m_Player != null ? m_Player.Texture : null;
                return t != null ? t : Texture2D.blackTexture;
            }
        }

        public override Material defaultMaterial
        {
            get
            {
                if (s_DefaultMaterial == null)
                {
                    var shader = Resources.Load<Shader>("MynellLottie/LottieUI");
                    if (shader == null || !shader.isSupported) return Canvas.GetDefaultCanvasMaterial();
                    s_DefaultMaterial = new Material(shader) { name = "Lottie UI (Default)", hideFlags = HideFlags.HideAndDontSave };
                }
                return s_DefaultMaterial;
            }
        }

        void EnsurePlayer()
        {
            if (m_Player != null && !m_Player.IsDisposed) return;
            m_Player = new LottiePlayer(m_Asset);
            m_Player.Completed += OnPlayerCompleted;
            m_Player.FrameRendered += OnFrameRendered;
            ApplySettings();
        }

        void ApplySettings()
        {
            if (m_Player == null) return;
            m_Player.LoopMode = m_LoopMode;
            m_Player.LoopCount = m_LoopCount;
            m_Player.Speed = m_Speed;
            m_Player.UnscaledTime = m_UnscaledTime;
            m_Player.Subframes = m_Subframes;
            // The quad always has the texture's aspect: fitted when preserving aspect, stretched otherwise.
            m_Player.ScaleMode = LottieScaleMode.Stretch;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            EnsurePlayer();
            m_Player.Active = true;
            LottieSettings.Changed += OnSettingsChanged;
            UpdateRenderSize();
            if (Application.isPlaying)
            {
                if (m_PlayOnEnable) m_Player.Play();
                else m_Player.SetDirty();
            }
            else
            {
                m_Player.NormalizedTime = m_EditorPreviewTime;
                m_Player.RenderNow();
            }
        }

        protected override void OnDisable()
        {
            LottieSettings.Changed -= OnSettingsChanged;
            if (m_Player != null)
            {
                m_Player.Active = false;
                if (Application.isPlaying) m_Player.Pause();
            }
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            if (m_Player != null)
            {
                m_Player.Completed -= OnPlayerCompleted;
                m_Player.FrameRendered -= OnFrameRendered;
                m_Player.Dispose();
                m_Player = null;
            }
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            if (m_Player == null) return;
            if (m_Player.Asset != m_Asset) m_Player.Asset = m_Asset;
            ApplySettings();
            UpdateRenderSize();
            if (!Application.isPlaying)
            {
                m_Player.NormalizedTime = m_EditorPreviewTime;
                m_Player.SetDirty();
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (this != null && m_Player != null && !m_Player.IsDisposed) m_Player.RenderNow();
                };
            }
        }
#endif

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            UpdateRenderSize();
        }

        protected override void OnCanvasHierarchyChanged()
        {
            base.OnCanvasHierarchyChanged();
            UpdateRenderSize();
        }

        void OnPlayerCompleted(LottiePlayer p) => m_OnComplete?.Invoke();

        void OnSettingsChanged()
        {
            if (m_Player == null) return;
            UpdateRenderSize();
            if (!Application.isPlaying) m_Player.RenderNow();
        }

        void OnFrameRendered(LottiePlayer p)
        {
            if (m_BoundTexture != p.Texture)
            {
                m_BoundTexture = p.Texture;
                SetMaterialDirty();
            }
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        Rect DrawRect()
        {
            Rect r = GetPixelAdjustedRect();
            if (!m_PreserveAspect || m_Asset == null || m_Asset.Width <= 0 || m_Asset.Height <= 0) return r;
            float aspect = m_Asset.Width / m_Asset.Height;
            float w = r.width, h = r.height;
            if (w / Mathf.Max(h, 1e-6f) > aspect)
            {
                float nw = h * aspect;
                r.x += (w - nw) * rectTransform.pivot.x;
                r.width = nw;
            }
            else
            {
                float nh = w / aspect;
                r.y += (h - nh) * rectTransform.pivot.y;
                r.height = nh;
            }
            return r;
        }

        void UpdateRenderSize()
        {
            if (m_Player == null || m_Asset == null) return;
            Rect r = DrawRect();
            float scale = canvas != null ? canvas.scaleFactor : 1f;
            scale *= m_ResolutionScale * LottieSettings.ResolutionScale;
            float w = r.width * scale, h = r.height * scale;
            float max = Mathf.Max(w, h);
            if (max > m_MaxTextureSize)
            {
                float k = m_MaxTextureSize / max;
                w *= k;
                h *= k;
            }
            m_Player.SetSize(Mathf.Max(1, Mathf.CeilToInt(w - 0.01f)), Mathf.Max(1, Mathf.CeilToInt(h - 0.01f)));
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (m_Asset == null) return;
            Rect r = DrawRect();
            Color32 c = color;
            vh.AddVert(new Vector3(r.xMin, r.yMin), c, new Vector2(0, 0));
            vh.AddVert(new Vector3(r.xMin, r.yMax), c, new Vector2(0, 1));
            vh.AddVert(new Vector3(r.xMax, r.yMax), c, new Vector2(1, 1));
            vh.AddVert(new Vector3(r.xMax, r.yMin), c, new Vector2(1, 0));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        /// <summary>Resizes the RectTransform to the animation's composition size.</summary>
        public override void SetNativeSize()
        {
            if (m_Asset == null) return;
            rectTransform.anchorMax = rectTransform.anchorMin;
            rectTransform.sizeDelta = new Vector2(m_Asset.Width, m_Asset.Height);
            SetAllDirty();
        }
    }
}
