using System;
using System.ComponentModel;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Mynell.Lottie.Timeline
{
    /// <summary>
    /// Timeline track that drives a <see cref="LottieImage"/> or <see cref="LottieRenderer"/> (bind the GameObject):
    /// while a clip is active the animation shows the frame given by the timeline, so it scrubs and stays in sync.
    /// </summary>
    [Serializable]
    [DisplayName("Mynell/Lottie Track")]
    [TrackColor(0.98f, 0.55f, 0.22f)]
    [TrackClipType(typeof(LottieClip))]
    [TrackBindingType(typeof(GameObject))]
    public class LottieTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount) =>
            ScriptPlayable<LottieMixerBehaviour>.Create(graph, inputCount);
    }

    /// <summary>A range of a Lottie animation on a <see cref="LottieTrack"/>.</summary>
    [Serializable]
    [DisplayName("Lottie Clip")]
    public class LottieClip : PlayableAsset, ITimelineClipAsset
    {
        [Tooltip("Animation shown during the clip. Empty: keep the component's own asset.")]
        public LottieAsset Asset;

        [Tooltip("Marker whose range the clip plays (overrides the frame range).")]
        public string Marker = "";

        [Tooltip("First frame (absolute). Negative: the animation's in point.")]
        public float StartFrame = -1;

        [Tooltip("End frame (absolute, exclusive). Negative: the animation's out point.")]
        public float EndFrame = -1;

        [Tooltip("Repeat the range while the clip is longer than it.")]
        public bool Loop = true;

        public ClipCaps clipCaps => ClipCaps.Looping | ClipCaps.SpeedMultiplier | ClipCaps.ClipIn | ClipCaps.Extrapolation;

        public override double duration
        {
            get
            {
                if (Asset == null || Asset.FrameRate <= 0) return base.duration;
                ResolveRange(Asset, out float start, out float end);
                return Math.Max(1e-3, (end - start) / Asset.FrameRate);
            }
        }

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            var playable = ScriptPlayable<LottieClipBehaviour>.Create(graph);
            playable.GetBehaviour().Clip = this;
            return playable;
        }

        /// <summary>Frame range of the clip for an asset (absolute frames, end exclusive).</summary>
        public void ResolveRange(LottieAsset asset, out float start, out float end)
        {
            start = asset.InPoint;
            end = asset.OutPoint;
            int m = string.IsNullOrEmpty(Marker) ? -1 : asset.FindMarker(Marker);
            if (m >= 0)
            {
                start = asset.GetMarkerTime(m);
                float d = asset.GetMarkerDuration(m);
                end = d > 0 ? start + d : asset.OutPoint;
                return;
            }
            if (StartFrame >= 0) start = Mathf.Clamp(StartFrame, asset.InPoint, asset.OutPoint);
            if (EndFrame >= 0) end = Mathf.Clamp(EndFrame, start, asset.OutPoint);
        }
    }

    /// <summary>Per-clip data of a <see cref="LottieClip"/>.</summary>
    public class LottieClipBehaviour : PlayableBehaviour
    {
        public LottieClip Clip;
    }

    /// <summary>Applies the dominant clip of a <see cref="LottieTrack"/> to the bound animation.</summary>
    public class LottieMixerBehaviour : PlayableBehaviour
    {
        LottiePlayer m_Player;
        bool m_WasPlaying, m_WasAutoAdvance;
        LottieAsset m_OriginalAsset;
        bool m_Driving;

        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            var go = playerData as GameObject;
            if (go == null) return;
            var player = FindPlayer(go, out LottieAsset componentAsset);
            if (player == null) return;

            int best = -1;
            float bestWeight = 0;
            for (int i = 0; i < playable.GetInputCount(); i++)
            {
                float w = playable.GetInputWeight(i);
                if (w > bestWeight)
                {
                    bestWeight = w;
                    best = i;
                }
            }
            if (best < 0)
            {
                Release();
                return;
            }
            var input = (ScriptPlayable<LottieClipBehaviour>)playable.GetInput(best);
            var clip = input.GetBehaviour().Clip;
            if (clip == null) return;

            if (!m_Driving || m_Player != player)
            {
                Release();
                m_Player = player;
                m_WasPlaying = player.IsPlaying;
                m_WasAutoAdvance = player.AutoAdvance;
                m_OriginalAsset = player.Asset;
                m_Driving = true;
            }
            player.AutoAdvance = false;
            var asset = clip.Asset != null ? clip.Asset : componentAsset;
            if (asset == null) return;
            if (player.Asset != asset) player.Asset = asset;
            if (asset.FrameRate <= 0) return;

            clip.ResolveRange(asset, out float start, out float end);
            double time = input.GetTime();
            float frame = start + (float)(time * asset.FrameRate);
            float length = end - start;
            if (length > 0)
            {
                if (clip.Loop) frame = start + Mathf.Repeat(frame - start, length);
                else frame = Mathf.Clamp(frame, start, end - 1e-3f);
            }
            player.Frame = frame;
            if (!Application.isPlaying) player.RenderNow();
        }

        public override void OnPlayableDestroy(Playable playable) => Release();

        /// <summary>Gives the animation back to its component (asset and playback state).</summary>
        void Release()
        {
            if (!m_Driving) return;
            m_Driving = false;
            if (m_Player == null || m_Player.IsDisposed) return;
            if (m_OriginalAsset != null && m_Player.Asset != m_OriginalAsset) m_Player.Asset = m_OriginalAsset;
            m_Player.AutoAdvance = m_WasAutoAdvance;
            if (m_WasPlaying) m_Player.Play();
            m_Player = null;
        }

        static LottiePlayer FindPlayer(GameObject go, out LottieAsset asset)
        {
            if (go.TryGetComponent(out LottieImage image))
            {
                asset = image.Asset;
                return image.Player;
            }
            if (go.TryGetComponent(out LottieRenderer renderer))
            {
                asset = renderer.Asset;
                return renderer.Player;
            }
            asset = null;
            return null;
        }
    }
}
