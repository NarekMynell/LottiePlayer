using UnityEngine;

namespace Mynell.Lottie
{
    /// <summary>
    /// The saved project values of <see cref="LottieSettings"/> (<i>Edit &gt; Project Settings &gt; Mynell Lottie</i>).
    /// It is kept in a Resources folder, so builds load it.
    /// </summary>
    public sealed class LottieSettingsAsset : ScriptableObject
    {
        // Shared with the settings page, so the page and the asset's fields explain the same thing.
        internal const string ResolutionScaleTip =
            "How sharp every Lottie animation in the project is drawn. 1 renders one texture pixel per screen pixel. " +
            "Lower values render faster and use less memory but look softer; higher values stay sharp when an animation " +
            "is zoomed or scaled up, but render slower.\n\nMultiplied by the Resolution Scale of each component.";
        internal const string MaxFrameRateTip =
            "How many times per second, at most, a playing animation draws a new image. Playback speed doesn't " +
            "change: the animation keeps its timing, only its image updates less often.";
        internal const string ThreadsPerAnimationTip =
            "How many CPU threads, at most, one frame of one animation is split across. 1 draws each frame as a " +
            "single job. Animations that play at the same time still render in parallel.";
        internal const string CacheShapesTip =
            "Keep the shapes of the last frame and draw again only the ones that changed. The image is exactly the " +
            "same either way. Turning it off saves a little memory but renders slower.";

        [SerializeField, Range(0.25f, 2f), Tooltip(ResolutionScaleTip)]
        float m_ResolutionScale = 1f;

        [SerializeField, Min(0), Tooltip(MaxFrameRateTip + "\n\n0 = no limit.")]
        int m_MaxFrameRate;

        [SerializeField, Min(0), Tooltip(ThreadsPerAnimationTip + "\n\n0 = all worker threads.")]
        int m_ThreadsPerAnimation;

        [SerializeField, Tooltip(CacheShapesTip)]
        bool m_CacheShapes = true;

        internal float ResolutionScale => m_ResolutionScale;
        internal int MaxFrameRate => m_MaxFrameRate;
        internal int ThreadsPerAnimation => m_ThreadsPerAnimation;
        internal bool CacheShapes => m_CacheShapes;

#if UNITY_EDITOR
        // Edited in the Inspector or Project Settings, or undone: the running values follow once the edit is done.
        void OnValidate()
        {
            if (name != LottieSettings.AssetName) return;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null) LottieSettings.OnAssetEdited(this);
            };
        }
#endif
    }
}
