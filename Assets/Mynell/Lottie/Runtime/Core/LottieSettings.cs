using System;
using UnityEngine;

namespace Mynell.Lottie
{
    /// <summary>
    /// Settings for all animations of the project. Edit them in <i>Edit &gt; Project Settings &gt; Mynell Lottie</i>
    /// (saved in a <see cref="LottieSettingsAsset"/> named <see cref="AssetName"/> in a Resources folder, so builds load
    /// it) or set them from code at runtime, on the main thread. A change applies from the next frame; changes made from
    /// code are not saved.
    /// </summary>
    public static class LottieSettings
    {
        /// <summary>Name of the settings asset in a Resources folder.</summary>
        public const string AssetName = "MynellLottieSettings";

        internal struct Values : IEquatable<Values>
        {
            public float ResolutionScale;
            public int MaxFrameRate;
            public int ThreadsPerAnimation;
            public bool CacheShapes;

            public static Values Default => new Values { ResolutionScale = 1f, CacheShapes = true };

            public bool Equals(Values o) => ResolutionScale == o.ResolutionScale && MaxFrameRate == o.MaxFrameRate &&
                                             ThreadsPerAnimation == o.ThreadsPerAnimation && CacheShapes == o.CacheShapes;
        }

        static bool s_Loaded;
        static Values s_Current, s_Saved;

        /// <summary>Raised on the main thread after a setting changed.</summary>
        public static event Action Changed;

        /// <summary>
        /// Multiplies the texture resolution of every Lottie Image, Lottie Renderer and Lottie Element (0.25 to 2,
        /// default 1). A frame costs about in proportion to its pixel count.
        /// </summary>
        public static float ResolutionScale
        {
            get { Load(); return s_Current.ResolutionScale; }
            set
            {
                Load();
                value = ClampScale(value);
                if (s_Current.ResolutionScale == value) return;
                s_Current.ResolutionScale = value;
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Most frames per second a playing animation is rendered (0 = every frame, the default). Playback time is not
        /// affected, only how often the texture is updated.
        /// </summary>
        public static int MaxFrameRate
        {
            get { Load(); return s_Current.MaxFrameRate; }
            set
            {
                Load();
                value = Math.Max(0, value);
                if (s_Current.MaxFrameRate == value) return;
                s_Current.MaxFrameRate = value;
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Most threads one frame is split across (0 = all worker threads, the default; 1 = one job per frame).
        /// Animations that render at the same time share the worker threads either way.
        /// </summary>
        public static int ThreadsPerAnimation
        {
            get { Load(); return s_Current.ThreadsPerAnimation; }
            set
            {
                Load();
                value = Math.Max(0, value);
                if (s_Current.ThreadsPerAnimation == value) return;
                s_Current.ThreadsPerAnimation = value;
                Changed?.Invoke();
            }
        }

        /// <summary>
        /// Keep the shapes of the last frame so that only what changed is rasterized again (default on). The pixels are
        /// the same either way; turning it off frees the memory the cache uses.
        /// </summary>
        public static bool CacheShapes
        {
            get { Load(); return s_Current.CacheShapes; }
            set
            {
                Load();
                if (s_Current.CacheShapes == value) return;
                s_Current.CacheShapes = value;
                Changed?.Invoke();
            }
        }

        /// <summary>Goes back to the saved project values (or the defaults), dropping the changes made from code.</summary>
        public static void Reload()
        {
            s_Loaded = false;
            Load();
            Changed?.Invoke();
        }

        internal static float ClampScale(float scale) => float.IsNaN(scale) ? 1f : Mathf.Clamp(scale, 0.25f, 2f);

        /// <summary>The saved values were edited (Inspector, Project Settings, undo): the running values follow.</summary>
        internal static void OnAssetEdited(LottieSettingsAsset asset)
        {
            if (s_Loaded && !Read(asset).Equals(s_Saved)) Reload();
        }

        static void Load()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            var asset = Resources.Load<LottieSettingsAsset>(AssetName);
            s_Saved = asset != null ? Read(asset) : Values.Default;
            s_Current = s_Saved;
        }

        /// <summary>An asset's values, brought into range (hand-edited or older assets).</summary>
        internal static Values Read(LottieSettingsAsset asset) => new Values
        {
            ResolutionScale = ClampScale(asset.ResolutionScale),
            MaxFrameRate = Math.Max(0, asset.MaxFrameRate),
            ThreadsPerAnimation = Math.Max(0, asset.ThreadsPerAnimation),
            CacheShapes = asset.CacheShapes,
        };

        // Entering play mode without a domain reload: start again from the saved values.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void OnSubsystemRegistration() => s_Loaded = false;
    }
}
