using UnityEngine;
using UnityEngine.EventSystems;

namespace Mynell.Lottie.Samples
{
    /// <summary>Sample: controls a <see cref="LottieImage"/> when it is clicked (needs Raycast Target and an EventSystem).</summary>
    [RequireComponent(typeof(LottieImage))]
    public class LottieClickControl : MonoBehaviour, IPointerClickHandler
    {
        public enum ClickAction
        {
            /// <summary>Pause a playing animation, resume a paused one.</summary>
            TogglePause,
            /// <summary>Play again from the start.</summary>
            Restart,
            /// <summary>Play the listed markers in turn, one per click.</summary>
            PlayMarkers,
        }

        [SerializeField] ClickAction m_Action;
        [SerializeField] string[] m_Markers = { "beat", "fade" };

        LottieImage m_Image;
        int m_NextMarker;

        void Awake() => m_Image = GetComponent<LottieImage>();

        public void OnPointerClick(PointerEventData eventData)
        {
            switch (m_Action)
            {
                case ClickAction.TogglePause:
                    if (m_Image.IsPlaying) m_Image.Pause();
                    else m_Image.Play();
                    break;
                case ClickAction.Restart:
                    m_Image.Stop();
                    m_Image.Play();
                    break;
                case ClickAction.PlayMarkers:
                    if (m_Markers.Length == 0) return;
                    m_Image.PlayMarker(m_Markers[m_NextMarker]);
                    m_NextMarker = (m_NextMarker + 1) % m_Markers.Length;
                    break;
            }
        }
    }
}
