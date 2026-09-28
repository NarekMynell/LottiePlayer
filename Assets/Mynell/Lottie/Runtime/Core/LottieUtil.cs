using UnityEngine;

namespace Mynell.Lottie
{
    internal static class LottieUtil
    {
        /// <summary>Destroys an object with Destroy in play mode and DestroyImmediate in edit mode.</summary>
        public static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
