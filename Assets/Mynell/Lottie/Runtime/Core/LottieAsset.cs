using System;
using Mynell.Lottie.Model;
using UnityEngine;

namespace Mynell.Lottie
{
    /// <summary>
    /// An imported Lottie animation (compiled at import time). Assign it to a <see cref="LottieImage"/> (UGUI) or
    /// <see cref="LottieRenderer"/> (world space), or create one at runtime with <see cref="CreateFromJson"/>.
    /// </summary>
    [PreferBinarySerialization]
    public sealed class LottieAsset : ScriptableObject
    {
        [SerializeField, HideInInspector] byte[] m_Data = Array.Empty<byte>();
        [SerializeField] float m_Width, m_Height, m_FrameRate, m_InPoint, m_OutPoint;
        [SerializeField] string m_LottieVersion;
        [SerializeField] string[] m_MarkerNames = Array.Empty<string>();
        [SerializeField] float[] m_MarkerTimes = Array.Empty<float>();
        [SerializeField] float[] m_MarkerDurations = Array.Empty<float>();
        [SerializeField] string[] m_Warnings = Array.Empty<string>();
        [SerializeField] string[] m_Unsupported = Array.Empty<string>();

        [NonSerialized] LottieAnimation m_Animation;
        [NonSerialized] bool m_LoadFailed;

        /// <summary>Composition size in Lottie units (pixels).</summary>
        public Vector2 Size => new Vector2(m_Width, m_Height);
        public float Width => m_Width;
        public float Height => m_Height;
        public float FrameRate => m_FrameRate;
        /// <summary>First frame (inclusive).</summary>
        public float InPoint => m_InPoint;
        /// <summary>Last frame (exclusive).</summary>
        public float OutPoint => m_OutPoint;
        public float TotalFrames => m_OutPoint - m_InPoint;
        /// <summary>Duration in seconds.</summary>
        public float Duration => m_FrameRate > 0 ? (m_OutPoint - m_InPoint) / m_FrameRate : 0;
        public string LottieVersion => m_LottieVersion;
        public int MarkerCount => m_MarkerNames.Length;
        public string GetMarkerName(int i) => m_MarkerNames[i];
        public float GetMarkerTime(int i) => m_MarkerTimes[i];
        public float GetMarkerDuration(int i) => m_MarkerDurations[i];
        /// <summary>Import-time notes (approximations, ignored features).</summary>
        public string[] Warnings => m_Warnings;
        /// <summary>Features used by the file that are not rendered.</summary>
        public string[] Unsupported => m_Unsupported;
        public int DataSize => m_Data?.Length ?? 0;

        /// <summary>Index of the marker named <paramref name="name"/>, or -1.</summary>
        public int FindMarker(string name) => Array.IndexOf(m_MarkerNames, name);

        /// <summary>The compiled animation (created on first use, shared by all players of this asset).</summary>
        public LottieAnimation Animation
        {
            get
            {
                if (m_Animation != null && !m_Animation.IsDisposed) return m_Animation;
                if (m_LoadFailed || m_Data == null || m_Data.Length == 0) return null;
                m_Animation = LottieAnimation.FromCompiled(m_Data);
                if (m_Animation == null)
                {
                    m_LoadFailed = true;
                    Debug.LogError($"[Lottie] '{name}' contains data from an incompatible version. Reimport the source file.", this);
                }
                return m_Animation;
            }
        }

        /// <summary>Creates an asset from Lottie JSON at runtime. External images are loaded through <paramref name="imageResolver"/>.</summary>
        public static LottieAsset CreateFromJson(string json, string assetName = "Lottie", Func<string, byte[]> imageResolver = null,
            Compiler.LottieCompileOptions options = null)
        {
            var data = Compiler.LottieCompiler.Compile(json, options);
            if (imageResolver != null) ResolveImages(data, imageResolver);
            var asset = CreateInstance<LottieAsset>();
            asset.name = assetName;
            asset.SetData(data);
            return asset;
        }

        /// <summary>Creates an asset from UTF-8 Lottie JSON bytes at runtime.</summary>
        public static LottieAsset CreateFromJson(byte[] utf8Json, string assetName = "Lottie", Func<string, byte[]> imageResolver = null,
            Compiler.LottieCompileOptions options = null)
        {
            var data = Compiler.LottieCompiler.Compile(utf8Json, options);
            if (imageResolver != null) ResolveImages(data, imageResolver);
            var asset = CreateInstance<LottieAsset>();
            asset.name = assetName;
            asset.SetData(data);
            return asset;
        }

        /// <summary>
        /// Creates an asset from a dotLottie archive (.lottie bytes) at runtime: the animation with id
        /// <paramref name="animationId"/>, or the first one. Returns null when the archive has no such animation.
        /// </summary>
        public static LottieAsset CreateFromDotLottie(byte[] archive, string animationId = null, Compiler.LottieCompileOptions options = null)
        {
            var animations = DotLottie.Load(archive, options?.FontResolver);
            foreach (var a in animations)
            {
                if (!string.IsNullOrEmpty(animationId) && a.Id != animationId) continue;
                var asset = CreateInstance<LottieAsset>();
                asset.name = a.Id;
                asset.SetData(a.Data);
                return asset;
            }
            return null;
        }

        /// <summary>Embeds external image files into the compiled data (so the asset is self-contained).</summary>
        public static void ResolveImages(LottieData data, Func<string, byte[]> resolver)
        {
            for (int i = 0; i < data.ImageBytes.Length; i++)
            {
                if (data.ImageBytes[i] != null && data.ImageBytes[i].Length > 0) continue;
                string path = i < data.ImagePaths.Length ? data.ImagePaths[i] : null;
                if (string.IsNullOrEmpty(path)) continue;
                try { data.ImageBytes[i] = resolver(path); }
                catch (Exception e) { data.Warnings.Add($"Image '{path}' could not be loaded: {e.Message}"); }
                if (data.ImageBytes[i] == null) data.Warnings.Add($"Image '{path}' was not found.");
            }
        }

        /// <summary>Replaces the content with compiled data (used by the importer and runtime creation).</summary>
        public void SetData(LottieData data)
        {
            ReleaseAnimation();
            m_Data = data.Serialize();
            m_Width = data.Width;
            m_Height = data.Height;
            m_FrameRate = data.FrameRate;
            m_InPoint = data.InPoint;
            m_OutPoint = data.OutPoint;
            m_LottieVersion = data.LottieVersion;
            int n = data.Markers.Length;
            m_MarkerNames = new string[n];
            m_MarkerTimes = new float[n];
            m_MarkerDurations = new float[n];
            for (int i = 0; i < n; i++)
            {
                m_MarkerNames[i] = data.Markers[i].Name;
                m_MarkerTimes[i] = data.Markers[i].Time;
                m_MarkerDurations[i] = data.Markers[i].Duration;
            }
            m_Warnings = data.Warnings.ToArray();
            m_Unsupported = data.Unsupported.ToArray();
            m_LoadFailed = false;
        }

        void ReleaseAnimation()
        {
            if (m_Animation == null) return;
            m_Animation.Dispose();
            m_Animation = null;
        }

        void OnDisable() => ReleaseAnimation();
        void OnDestroy() => ReleaseAnimation();
    }
}
