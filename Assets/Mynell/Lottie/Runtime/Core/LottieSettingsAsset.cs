using UnityEngine;

namespace Mynell.Lottie
{
    /// <summary>
    /// The saved project values of <see cref="LottieSettings"/> (<i>Edit &gt; Project Settings &gt; Mynell Lottie</i>).
    /// It is kept in a Resources folder, so builds load it.
    /// </summary>
    public sealed class LottieSettingsAsset : ScriptableObject
    {
        [SerializeField, Range(0.25f, 2f), Tooltip("Multiplies the texture resolution of every Lottie Image, Lottie Renderer and Lottie Element.")]
        float m_ResolutionScale = 1f;

        [SerializeField, Min(0), Tooltip("Most frames per second a playing animation is rendered. 0 = every frame. Playback speed doesn't change.")]
        int m_MaxFrameRate;

        [SerializeField, Min(0), Tooltip("Most threads one frame is split across. 0 = all worker threads, 1 = one job per frame.")]
        int m_ThreadsPerAnimation;

        [SerializeField, Tooltip("Keep the shapes of the last frame, so only what changed is rasterized again. Same pixels; turn off to save the memory it uses.")]
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
