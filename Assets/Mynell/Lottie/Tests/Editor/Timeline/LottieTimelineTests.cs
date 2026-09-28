using Mynell.Lottie.Timeline;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace Mynell.Lottie.Tests
{
    public class LottieTimelineTests
    {
        const string Json =
            "{\"v\":\"5.7.0\",\"fr\":30,\"ip\":0,\"op\":60,\"w\":100,\"h\":100,\"layers\":[{\"ty\":4,\"ind\":1,\"ip\":0,\"op\":60,\"st\":0," +
            "\"ks\":{\"p\":{\"a\":0,\"k\":[50,50]},\"a\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[100,100]},\"r\":{\"a\":0,\"k\":0},\"o\":{\"a\":0,\"k\":100}}," +
            "\"shapes\":[{\"ty\":\"rc\",\"p\":{\"a\":0,\"k\":[0,0]},\"s\":{\"a\":0,\"k\":[40,40]},\"r\":{\"a\":0,\"k\":0}}," +
            "{\"ty\":\"fl\",\"c\":{\"a\":0,\"k\":[1,0,0,1]},\"o\":{\"a\":0,\"k\":100}}]}]," +
            "\"markers\":[{\"tm\":20,\"cm\":\"middle\",\"dr\":10}]}";

        [Test]
        public void Clip_DrivesTheAnimationFrame()
        {
            var asset = LottieAsset.CreateFromJson(Json);
            var go = new GameObject("Lottie", typeof(MeshFilter), typeof(MeshRenderer), typeof(LottieRenderer));
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                var renderer = go.GetComponent<LottieRenderer>();
                renderer.Asset = asset;
                var track = timeline.CreateTrack<LottieTrack>(null, "Lottie");
                var timelineClip = track.CreateClip<LottieClip>();
                timelineClip.start = 1;
                timelineClip.duration = 4;
                var director = go.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                director.SetGenericBinding(track, go);

                director.time = 1.5;        // 0.5 s into the clip
                director.Evaluate();
                Assert.AreEqual(15f, renderer.Player.Frame, 1e-2f);

                director.time = 4;          // 3 s into the clip: loops over the 2 s animation
                director.Evaluate();
                Assert.AreEqual(30f, renderer.Player.Frame, 1e-2f);

                ((LottieClip)timelineClip.asset).Marker = "middle";
                director.RebuildGraph();
                director.time = 1.2;        // 6 frames into the marker's 10-frame range
                director.Evaluate();
                Assert.AreEqual(26f, renderer.Player.Frame, 1e-2f);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(timeline);
                Object.DestroyImmediate(asset);
            }
        }
    }
}
