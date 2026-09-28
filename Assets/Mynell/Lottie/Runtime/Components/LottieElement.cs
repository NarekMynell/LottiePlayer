using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Mynell.Lottie
{
    /// <summary>
    /// UI Toolkit element that plays a <see cref="LottieAsset"/> (UXML: <c>&lt;Mynell.Lottie.LottieElement asset="..." /&gt;</c>,
    /// or UI Builder ▸ Library ▸ Project ▸ Custom Controls). The animation is rasterized at the element's size in pixels.
    /// </summary>
    [UxmlElement]
    public partial class LottieElement : VisualElement
    {
        /// <summary>USS class added to every Lottie element.</summary>
        public const string UssClassName = "lottie-element";

        LottieAsset m_Asset;
        bool m_PlayOnAttach = true;
        LottieLoopMode m_LoopMode = LottieLoopMode.Loop;
        int m_LoopCount;
        float m_Speed = 1f;
        bool m_UnscaledTime;
        bool m_Subframes = true;
        bool m_PreserveAspect = true;
        float m_ResolutionScale = 1f;
        int m_MaxTextureSize = 2048;
        Color m_Tint = Color.white;

        LottiePlayer m_Player;
        RenderTexture m_Target;
        // Playback state kept while detached (the player is released with the panel).
        float m_SavedFrame = float.NaN;
        bool m_SavedPlaying;

        static Material s_Convert;
        static readonly ushort[] s_Indices = { 0, 1, 2, 2, 3, 0 };

        /// <summary>Raised when a non-looping playback ends (or the loop count is reached).</summary>
        public event Action<LottieElement> Completed;

        public LottieElement()
        {
            AddToClassList(UssClassName);
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
            RegisterCallback<GeometryChangedEvent>(_ => UpdateSize());
            generateVisualContent += OnGenerateVisualContent;
        }

        public LottieElement(LottieAsset asset) : this() => Asset = asset;

        // ------------------------------------------------------------------ attributes

        [UxmlAttribute]
        public LottieAsset Asset
        {
            get => m_Asset;
            set
            {
                if (m_Asset == value) return;
                m_Asset = value;
                if (m_Player == null) return;
                m_Player.Asset = value;
                UpdateSize();
                if (Application.isPlaying && m_PlayOnAttach) m_Player.Play();
                else RenderInEditor();
                MarkDirtyRepaint();
            }
        }

        [UxmlAttribute] public bool PlayOnAttach { get => m_PlayOnAttach; set => m_PlayOnAttach = value; }
        [UxmlAttribute] public LottieLoopMode LoopMode { get => m_LoopMode; set { m_LoopMode = value; ApplySettings(); } }
        [UxmlAttribute] public int LoopCount { get => m_LoopCount; set { m_LoopCount = Mathf.Max(0, value); ApplySettings(); } }
        [UxmlAttribute] public float Speed { get => m_Speed; set { m_Speed = value; ApplySettings(); } }
        [UxmlAttribute] public bool UnscaledTime { get => m_UnscaledTime; set { m_UnscaledTime = value; ApplySettings(); } }
        [UxmlAttribute] public bool Subframes { get => m_Subframes; set { m_Subframes = value; ApplySettings(); } }

        [UxmlAttribute]
        public bool PreserveAspect
        {
            get => m_PreserveAspect;
            set
            {
                m_PreserveAspect = value;
                UpdateSize();
                MarkDirtyRepaint();
            }
        }

        /// <summary>Texture resolution relative to the element's size in pixels.</summary>
        [UxmlAttribute]
        public float ResolutionScale
        {
            get => m_ResolutionScale;
            set
            {
                m_ResolutionScale = Mathf.Clamp(value, 0.1f, 4f);
                UpdateSize();
            }
        }

        [UxmlAttribute]
        public int MaxTextureSize
        {
            get => m_MaxTextureSize;
            set
            {
                m_MaxTextureSize = Mathf.Clamp(value, 16, 8192);
                UpdateSize();
            }
        }

        [UxmlAttribute]
        public Color Tint
        {
            get => m_Tint;
            set
            {
                m_Tint = value;
                MarkDirtyRepaint();
            }
        }

        // ------------------------------------------------------------------ playback

        /// <summary>The playback / render controller (released while the element is not in a panel).</summary>
        public LottiePlayer Player
        {
            get
            {
                EnsurePlayer();
                return m_Player;
            }
        }

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
            m_Player.ScaleMode = LottieScaleMode.Stretch;
        }

        void OnPlayerCompleted(LottiePlayer p) => Completed?.Invoke(this);

        void OnAttachToPanel(AttachToPanelEvent e)
        {
            EnsurePlayer();
            m_Player.Active = true;
            UpdateSize();
            if (!float.IsNaN(m_SavedFrame))
            {
                m_Player.Frame = m_SavedFrame;
                if (m_SavedPlaying) m_Player.Play();
                else m_Player.SetDirty();
                m_SavedFrame = float.NaN;
            }
            else if (Application.isPlaying && m_PlayOnAttach) m_Player.Play();
            RenderInEditor();
        }

        void OnDetachFromPanel(DetachFromPanelEvent e)
        {
            if (m_Player != null)
            {
                m_SavedFrame = m_Player.Frame;
                m_SavedPlaying = m_Player.IsPlaying;
                m_Player.Completed -= OnPlayerCompleted;
                m_Player.FrameRendered -= OnFrameRendered;
                m_Player.Dispose();
                m_Player = null;
            }
            if (m_Target != null)
            {
                m_Target.Release();
                LottieUtil.DestroySafe(m_Target);
                m_Target = null;
            }
        }

        /// <summary>Outside play mode nothing advances the scheduler: render the current frame directly.</summary>
        void RenderInEditor()
        {
            if (Application.isPlaying || m_Player == null || panel == null) return;
            m_Player.RenderNow();
        }

        // ------------------------------------------------------------------ rendering

        Rect DrawRect()
        {
            Rect r = contentRect;
            if (!m_PreserveAspect || m_Asset == null || m_Asset.Width <= 0 || m_Asset.Height <= 0) return r;
            float aspect = m_Asset.Width / m_Asset.Height;
            if (r.width / Mathf.Max(r.height, 1e-6f) > aspect)
            {
                float w = r.height * aspect;
                r.x += (r.width - w) * 0.5f;
                r.width = w;
            }
            else
            {
                float h = r.width / aspect;
                r.y += (r.height - h) * 0.5f;
                r.height = h;
            }
            return r;
        }

        void UpdateSize()
        {
            if (m_Player == null || m_Asset == null || panel == null) return;
            Rect r = DrawRect();
            if (!(r.width > 0) || !(r.height > 0)) return;
            float scale = panel.scaledPixelsPerPoint * m_ResolutionScale;
            float w = r.width * scale, h = r.height * scale;
            float max = Mathf.Max(w, h);
            if (max > m_MaxTextureSize)
            {
                w *= m_MaxTextureSize / max;
                h *= m_MaxTextureSize / max;
            }
            int iw = Mathf.Max(1, Mathf.CeilToInt(w - 0.01f)), ih = Mathf.Max(1, Mathf.CeilToInt(h - 0.01f));
            if (iw == m_Player.Width && ih == m_Player.Height) return;
            m_Player.SetSize(iw, ih);
            RenderInEditor();
        }

        void OnFrameRendered(LottiePlayer p)
        {
            var src = p.Texture;
            if (src == null) return;
            if (m_Target == null || m_Target.width != src.width || m_Target.height != src.height)
            {
                if (m_Target != null)
                {
                    m_Target.Release();
                    LottieUtil.DestroySafe(m_Target);
                }
                m_Target = new RenderTexture(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
                {
                    name = "Lottie (UI Toolkit)",
                    hideFlags = HideFlags.DontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_Target.Create();
                MarkDirtyRepaint();
            }
            // UI Toolkit expects straight alpha: convert the premultiplied texture on the GPU.
            if (s_Convert == null)
            {
                var shader = Resources.Load<Shader>("MynellLottie/LottieStraight");
                if (shader != null && shader.isSupported) s_Convert = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (s_Convert != null) Graphics.Blit(src, m_Target, s_Convert);
            else Graphics.Blit(src, m_Target);
#if UNITY_EDITOR
            if (!Application.isPlaying) MarkDirtyRepaint();
#endif
        }

        void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            if (m_Target == null || m_Asset == null) return;
            Rect r = DrawRect();
            if (!(r.width > 0) || !(r.height > 0)) return;
            Color32 tint = m_Tint;
            var mesh = mgc.Allocate(4, 6, m_Target);
            // Texture rows start at the bottom (uv.y = 0).
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMin, r.yMax, Vertex.nearZ), uv = new Vector2(0, 0), tint = tint });
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMin, r.yMin, Vertex.nearZ), uv = new Vector2(0, 1), tint = tint });
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMax, r.yMin, Vertex.nearZ), uv = new Vector2(1, 1), tint = tint });
            mesh.SetNextVertex(new Vertex { position = new Vector3(r.xMax, r.yMax, Vertex.nearZ), uv = new Vector2(1, 0), tint = tint });
            mesh.SetAllIndices(s_Indices);
        }
    }
}
