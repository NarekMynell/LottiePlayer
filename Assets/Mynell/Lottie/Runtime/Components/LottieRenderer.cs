using UnityEngine;
using UnityEngine.Events;

namespace Mynell.Lottie
{
    /// <summary>
    /// Plays a <see cref="LottieAsset"/> on a quad in world space (2D games, 3D scenes). Size is
    /// composition size / <see cref="PixelsPerUnit"/>; the texture resolution is composition size × <see cref="TextureScale"/>.
    /// Sorting layer / order are taken from the MeshRenderer.
    /// </summary>
    [AddComponentMenu("Rendering/Lottie Renderer")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class LottieRenderer : MonoBehaviour
    {
        [SerializeField] LottieAsset m_Asset;
        [SerializeField] bool m_PlayOnEnable = true;
        [SerializeField] LottieLoopMode m_LoopMode = LottieLoopMode.Loop;
        [SerializeField, Min(0)] int m_LoopCount;
        [SerializeField] float m_Speed = 1f;
        [SerializeField] bool m_UnscaledTime;
        [SerializeField] bool m_Subframes = true;
        [SerializeField] Color m_Color = Color.white;
        [SerializeField, Min(0.0001f)] float m_PixelsPerUnit = 100f;
        [SerializeField] Vector2 m_Pivot = new Vector2(0.5f, 0.5f);
        [SerializeField, Range(0.1f, 8f)] float m_TextureScale = 1f;
        [SerializeField, Range(16, 8192)] int m_MaxTextureSize = 2048;
        [SerializeField] bool m_FlipX, m_FlipY;
        [SerializeField, Range(0f, 1f)] float m_EditorPreviewTime;
        [SerializeField] UnityEvent m_OnComplete = new UnityEvent();

        LottiePlayer m_Player;
        Mesh m_Mesh;
        MaterialPropertyBlock m_Block;
        MeshRenderer m_Renderer;
        static Material s_DefaultMaterial;
        static readonly int s_MainTex = Shader.PropertyToID("_MainTex");
        static readonly int s_ColorId = Shader.PropertyToID("_Color");

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
                if (m_Player == null) return;
                m_Player.Asset = value;
                Rebuild();
                if (Application.isPlaying && m_PlayOnEnable && isActiveAndEnabled) m_Player.Play();
                else m_Player.SetDirty();
            }
        }

        public bool PlayOnEnable { get => m_PlayOnEnable; set => m_PlayOnEnable = value; }
        public LottieLoopMode LoopMode { get => m_LoopMode; set { m_LoopMode = value; ApplySettings(); } }
        public int LoopCount { get => m_LoopCount; set { m_LoopCount = Mathf.Max(0, value); ApplySettings(); } }
        public float Speed { get => m_Speed; set { m_Speed = value; ApplySettings(); } }
        public bool UnscaledTime { get => m_UnscaledTime; set { m_UnscaledTime = value; ApplySettings(); } }
        public Color Color { get => m_Color; set { m_Color = value; UpdateBlock(); } }
        public float PixelsPerUnit { get => m_PixelsPerUnit; set { m_PixelsPerUnit = Mathf.Max(0.0001f, value); Rebuild(); } }
        public Vector2 Pivot { get => m_Pivot; set { m_Pivot = value; Rebuild(); } }
        public float TextureScale { get => m_TextureScale; set { m_TextureScale = Mathf.Clamp(value, 0.1f, 8f); Rebuild(); } }
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

        static Material DefaultMaterial
        {
            get
            {
                if (s_DefaultMaterial == null)
                {
                    var shader = Resources.Load<Shader>("MynellLottie/LottieUnlit");
                    if (shader == null) return null;
                    s_DefaultMaterial = new Material(shader) { name = "Lottie Unlit (Default)", hideFlags = HideFlags.HideAndDontSave };
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
            m_Player.ScaleMode = LottieScaleMode.Stretch;
        }

        void OnEnable()
        {
            m_Renderer = GetComponent<MeshRenderer>();
            if (m_Renderer.sharedMaterial == null && DefaultMaterial != null) m_Renderer.sharedMaterial = DefaultMaterial;
            EnsurePlayer();
            m_Player.Active = true;
            Rebuild();
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

        void OnDisable()
        {
            if (m_Player == null) return;
            m_Player.Active = false;
            if (Application.isPlaying) m_Player.Pause();
        }

        void OnDestroy()
        {
            if (m_Player != null)
            {
                m_Player.Completed -= OnPlayerCompleted;
                m_Player.FrameRendered -= OnFrameRendered;
                m_Player.Dispose();
                m_Player = null;
            }
            LottieUtil.DestroySafe(m_Mesh);
            m_Mesh = null;
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (m_Player == null) return;
            if (m_Player.Asset != m_Asset) m_Player.Asset = m_Asset;
            ApplySettings();
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || m_Player == null || m_Player.IsDisposed) return;
                Rebuild();
                if (!Application.isPlaying)
                {
                    m_Player.NormalizedTime = m_EditorPreviewTime;
                    m_Player.SetDirty();
                    m_Player.RenderNow();
                }
            };
        }
#endif

        void OnPlayerCompleted(LottiePlayer p) => m_OnComplete?.Invoke();

        void OnFrameRendered(LottiePlayer p)
        {
            UpdateBlock();
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void UpdateBlock()
        {
            if (m_Renderer == null) m_Renderer = GetComponent<MeshRenderer>();
            if (m_Renderer == null) return;
            m_Block ??= new MaterialPropertyBlock();
            m_Renderer.GetPropertyBlock(m_Block);
            var tex = m_Player != null ? m_Player.Texture : null;
            m_Block.SetTexture(s_MainTex, tex != null ? tex : Texture2D.blackTexture);
            m_Block.SetColor(s_ColorId, m_Color);
            m_Renderer.SetPropertyBlock(m_Block);
        }

        /// <summary>Rebuilds the quad and the render target size (after changing size-related settings).</summary>
        public void Rebuild()
        {
            var filter = GetComponent<MeshFilter>();
            if (m_Mesh == null)
            {
                m_Mesh = new Mesh { name = "Lottie Quad", hideFlags = HideFlags.DontSave };
                m_Mesh.MarkDynamic();
            }
            float w = m_Asset != null ? m_Asset.Width / m_PixelsPerUnit : 1f;
            float h = m_Asset != null ? m_Asset.Height / m_PixelsPerUnit : 1f;
            float x0 = -m_Pivot.x * w, y0 = -m_Pivot.y * h;
            float u0 = m_FlipX ? 1 : 0, u1 = m_FlipX ? 0 : 1, v0 = m_FlipY ? 1 : 0, v1 = m_FlipY ? 0 : 1;
            m_Mesh.Clear();
            m_Mesh.SetVertices(new[]
            {
                new Vector3(x0, y0), new Vector3(x0, y0 + h), new Vector3(x0 + w, y0 + h), new Vector3(x0 + w, y0),
            });
            m_Mesh.SetUVs(0, new[] { new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v1), new Vector2(u1, v0) });
            m_Mesh.SetTriangles(new[] { 0, 1, 2, 2, 3, 0 }, 0);
            m_Mesh.RecalculateBounds();
            filter.sharedMesh = m_Mesh;

            if (m_Player != null && m_Asset != null)
            {
                float tw = m_Asset.Width * m_TextureScale, th = m_Asset.Height * m_TextureScale;
                float max = Mathf.Max(tw, th);
                if (max > m_MaxTextureSize)
                {
                    tw *= m_MaxTextureSize / max;
                    th *= m_MaxTextureSize / max;
                }
                m_Player.SetSize(Mathf.Max(1, Mathf.RoundToInt(tw)), Mathf.Max(1, Mathf.RoundToInt(th)));
            }
            UpdateBlock();
        }
    }
}
