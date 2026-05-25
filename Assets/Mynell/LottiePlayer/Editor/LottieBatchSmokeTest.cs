// Batch-mode smoke test: parse baked asset + run Stage 1 job, log command counts.

#if UNITY_EDITOR
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Mynell.LottiePlayer.Editor
{
    internal static class LottieBatchSmokeTest
    {
        private const string kAssetPath = "Assets/Josns/Interactive volume.lottie.asset";

        public static void Run()
        {
            var asset = AssetDatabase.LoadAssetAtPath<LottieAsset>(kAssetPath);
            if (asset == null)
            {
                Debug.LogError($"[Mynell Smoke] Missing asset at {kAssetPath}");
                EditorApplication.Exit(1);
                return;
            }

            LottieAnimationBlob blob = default;
            try
            {
                blob = asset.CreateRuntimeBlob(Allocator.Persistent);
                int maxCmd = 4096;
                int maxVert = 65536;
                int maxIdx = 196608;
                int maxPoly = 32768;
                int maxPerShape = 1024;

                var commands = new NativeList<DrawCommand>(maxCmd, Allocator.TempJob);
                var counts = new NativeArray<int>(5, Allocator.TempJob);
                var polyPool = new NativeArray<float2>(maxPoly, Allocator.TempJob);
                var trimScratch = new NativeArray<float2>(maxPerShape, Allocator.TempJob);

                float2 layout = new float2(300f, 300f);
                var bridge = AffineMatrix.ComputeRootBridge(
                    new float2(asset.Width, asset.Height), layout, preserveAspect: true);

                var job = new BuildDrawCommandsJob
                {
                    Header = blob.Header,
                    Layers = blob.Layers,
                    Precomps = blob.Precomps,
                    Shapes = blob.Shapes,
                    Properties = blob.Properties,
                    Keyframes = blob.Keyframes,
                    PathProperties = blob.PathProperties,
                    PathSnapshots = blob.PathSnapshots,
                    BezierVertices = blob.BezierVertices,
                    ScalarPool = blob.ScalarPool,
                    Vec2Pool = blob.Vec2Pool,
                    Vec4Pool = blob.Vec4Pool,
                    CurrentFrame = asset.InFrame,
                    Tolerance = 1f,
                    RootBridge = bridge,
                    MaxVertices = maxVert,
                    MaxIndices = maxIdx,
                    MaxVerticesPerShape = maxPerShape,
                    PolyPool = polyPool,
                    Commands = commands,
                    Counts = counts,
                    TrimScratch = trimScratch,
                };

                job.Run();

                Debug.Log(
                    $"[Mynell Smoke] asset={asset.name} v{asset.BlobVersion} " +
                    $"cmds={commands.Length} verts={counts[0]} idx={counts[1]} overflow={counts[2]} poly={counts[4]}");

                trimScratch.Dispose();
                polyPool.Dispose();
                counts.Dispose();
                commands.Dispose();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Mynell Smoke] FAILED: {ex.Message}\n{ex.StackTrace}");
                EditorApplication.Exit(1);
                return;
            }
            finally
            {
                if (blob.Layers.IsCreated) blob.Dispose();
            }

            EditorApplication.Exit(0);
        }
    }
}
#endif
