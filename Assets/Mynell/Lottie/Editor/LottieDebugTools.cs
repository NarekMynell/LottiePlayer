using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace Mynell.Lottie.Editor
{
    /// <summary>Developer utilities: offline rendering of animations to PNG files (used by the reference tests).</summary>
    public static class LottieDebugTools
    {
        /// <summary>
        /// Renders absolute <paramref name="frames"/> into <c>outDir/f{frame}.png</c> (straight alpha). Returns a short report.
        /// </summary>
        public static string RenderPng(string jsonPath, string outDir, int width, int height, params float[] frames)
        {
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            string baseDir = Path.GetDirectoryName(jsonPath);
            using var anim = LottieAnimation.FromJson(File.ReadAllBytes(jsonPath), rel =>
            {
                string p = Path.Combine(baseDir, rel.TrimStart('/'));
                return File.Exists(p) ? File.ReadAllBytes(p) : null;
            }, new Mynell.Lottie.Compiler.LottieCompileOptions { FontResolver = Mynell.Lottie.Text.FontLocator.System.Resolve });
            sb.AppendLine($"compile {sw.Elapsed.TotalMilliseconds:F2} ms  size {anim.Width}x{anim.Height} fps {anim.FrameRate} ip {anim.InPoint} op {anim.OutPoint}");
            foreach (var w in anim.Data.Warnings) sb.AppendLine("warn: " + w);
            foreach (var u in anim.Data.Unsupported) sb.AppendLine("unsupported: " + u);
            Directory.CreateDirectory(outDir);
            using var state = new LottieRenderState();
            var pixels = new NativeArray<uint>(width * height, Allocator.Persistent);
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var m = LottieRenderState.FitTransform(anim.Width, anim.Height, width, height, true);
                foreach (float f in frames)
                {
                    sw.Restart();
                    state.RenderImmediate(anim, f, m, pixels, width, height);
                    double ms = sw.Elapsed.TotalMilliseconds;
                    // Un-premultiply for PNG output.
                    var raw = tex.GetRawTextureData<uint>();
                    for (int i = 0; i < pixels.Length; i++) raw[i] = Unpremultiply(pixels[i]);
                    tex.Apply(false);
                    File.WriteAllBytes(Path.Combine(outDir, $"f{f}.png"), tex.EncodeToPNG());
                    sb.AppendLine($"frame {f}: {ms:F2} ms");
                }
            }
            finally
            {
                pixels.Dispose();
                Object.DestroyImmediate(tex);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Renders every item of a test manifest (JSON array of {id, json, w, h, frames}) into outRoot/id/f{frame}.png.
        /// </summary>
        public static string RenderManifest(string manifestPath, string outRoot, string filter = null)
        {
            var summary = new StringBuilder();
            var doc = Mynell.Lottie.Json.JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            int count = 0, failed = 0;
            double totalMs = 0;
            foreach (var it in doc.Root)
            {
                string id = it["id"].AsString();
                if (!string.IsNullOrEmpty(filter) && !id.Contains(filter)) continue;
                string json = it["json"].AsString();
                int w = it["w"].AsInt(), h = it["h"].AsInt();
                var frames = it["frames"].ToFloatArray();
                string dir = Path.Combine(outRoot, id);
                try
                {
                    string report = RenderPng(json, dir, w, h, frames);
                    File.WriteAllText(Path.Combine(dir, "unity.txt"), report);
                    foreach (var line in report.Split('\n'))
                        if (line.StartsWith("frame ") && line.EndsWith(" ms"))
                        {
                            var part = line.Substring(line.IndexOf(':') + 1).Trim();
                            totalMs += double.Parse(part.Substring(0, part.Length - 3), System.Globalization.CultureInfo.InvariantCulture);
                        }
                    count++;
                }
                catch (System.Exception e)
                {
                    failed++;
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "unity.txt"), "EXCEPTION: " + e);
                    summary.AppendLine($"{id}: EXCEPTION {e.GetType().Name}: {e.Message}");
                }
            }
            summary.AppendLine($"rendered {count}, failed {failed}, total raster {totalMs:F1} ms");
            return summary.ToString();
        }

        /// <summary>Batch-mode entry: -manifest path -out dir [-filter text] [-bands n]. Writes outRoot/summary.txt.</summary>
        public static void RunManifestCli()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
                return null;
            }
            string manifest = Arg("-manifest"), outRoot = Arg("-out"), filter = Arg("-filter");
            if (int.TryParse(Arg("-bands"), out int bands)) LottieRenderState.MaxBands = bands;
            int code = 0;
            try
            {
                string summary = RenderManifest(manifest, outRoot, filter);
                Directory.CreateDirectory(outRoot);
                File.WriteAllText(Path.Combine(outRoot, "summary.txt"), summary);
                UnityEngine.Debug.Log(summary);
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogException(e);
                code = 1;
            }
            UnityEditor.EditorApplication.Exit(code);
        }

        /// <summary>
        /// Batch-mode benchmark: -bench file1;file2 -size N [-out report.txt]. Measures ms per frame of
        /// <see cref="LottieRenderState.RenderImmediate"/> single-threaded vs banded, over the whole animation.
        /// </summary>
        public static void RunBenchCli()
        {
            var args = System.Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
                return null;
            }
            var sb = new StringBuilder();
            int size = int.TryParse(Arg("-size"), out int sz) ? sz : 1024;
            int code = 0;
            try
            {
                foreach (var path in (Arg("-bench") ?? "").Split(';'))
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    using var anim = LottieAnimation.FromJson(File.ReadAllBytes(path));
                    var px = new NativeArray<uint>(size * size, Allocator.Persistent);
                    using var st = new LottieRenderState { SkipUnchanged = false };
                    var m = LottieRenderState.FitTransform(anim.Width, anim.Height, size, size, true);
                    sb.Append(Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path) + $" {size}px:");
                    foreach (int bands in new[] { 1, 0, 1, 0 })
                    {
                        LottieRenderState.MaxBands = bands;
                        for (int w = 0; w < 5; w++) st.RenderImmediate(anim, anim.InPoint + w, m, px, size, size);
                        var sw = Stopwatch.StartNew();
                        int n = 0;
                        for (float f = anim.InPoint; f < anim.OutPoint; f += 1, n++) st.RenderImmediate(anim, f, m, px, size, size);
                        sb.Append($"  {(bands == 1 ? "single" : "banded")} {sw.Elapsed.TotalMilliseconds / n:F3}");
                    }
                    sb.AppendLine(" ms/frame");
                    unsafe
                    {
                        LottieRenderState.MaxBands = 0;
                        int* log = stackalloc int[64];
                        for (int k = 0; k < 64; k++) log[k] = -1;
                        st.DebugThreadLog = log;
                        st.RenderImmediate(anim, anim.InPoint + 1, m, px, size, size);
                        var threads = new System.Collections.Generic.List<int>();
                        for (int k = 0; k < st.LastBandCount; k++) threads.Add(log[k]);
                        sb.AppendLine($"    immediate: bands {st.LastBandCount}, threads [{string.Join(",", threads)}]");
                        // Scheduled without an immediate wait (as the scheduler does).
                        var sw2 = Stopwatch.StartNew();
                        int n2 = 0;
                        for (float f = anim.InPoint; f < anim.OutPoint; f += 1, n2++)
                        {
                            var h = st.Schedule(anim, f, m, px, size, size);
                            Unity.Jobs.JobHandle.ScheduleBatchedJobs();
                            while (!h.IsCompleted) { }
                            st.Complete();
                        }
                        threads.Clear();
                        for (int k = 0; k < st.LastBandCount; k++) threads.Add(log[k]);
                        st.DebugThreadLog = null;
                        sb.AppendLine($"    spin-wait: {sw2.Elapsed.TotalMilliseconds / n2:F3} ms/frame, threads [{string.Join(",", threads)}]");
                    }
                    px.Dispose();
                }
                LottieRenderState.MaxBands = 0;
                sb.AppendLine($"workers {Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount}, burst {Unity.Burst.BurstCompiler.IsEnabled}");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("EXCEPTION: " + e);
                code = 1;
            }
            string outPath = Arg("-out");
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, sb.ToString());
            UnityEngine.Debug.Log(sb.ToString());
            UnityEditor.EditorApplication.Exit(code);
        }

        /// <summary>Times the Burst job per stage (scene / +geometry / full) averaged over the given frames.</summary>
        public static string Profile(string jsonPath, int width, int height, int iterations, params float[] frames)
        {
            var sb = new StringBuilder();
            using var anim = LottieAnimation.FromJson(File.ReadAllBytes(jsonPath));
            var fd = Mynell.Lottie.Scene.FrameData.Create();
            var sc = Mynell.Lottie.Scene.SceneScratch.Create();
            var rs = Mynell.Lottie.Raster.RasterScratch.Create();
            var px = new NativeArray<uint>(width * height, Allocator.Persistent);
            try
            {
                var m = LottieRenderState.FitTransform(anim.Width, anim.Height, width, height, true);
                unsafe
                {
                    var job = new RenderFrameJob
                    {
                        Data = anim.Native, ImagePixels = anim.ImagePixels, ImageInfo = anim.ImageInfo,
                        Frame = fd, Scene = sc, Raster = rs, CompToDevice = m, Width = width, Height = height,
                        Pixels = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(px), Tolerance = 0.03f,
                    };
                    // warm up (Burst compilation happens asynchronously in the Editor)
                    for (int w = 0; w < 3; w++) { job.Time = frames[0]; job.Run(); }
                    double[] totals = new double[4];
                    var sw = new Stopwatch();
                    foreach (int stage in new[] { 3, 1, 2, 0 })
                    {
                        job.Stage = stage;
                        sw.Restart();
                        for (int it = 0; it < iterations; it++)
                            foreach (float f in frames) { job.Time = f; job.Run(); }
                        totals[stage] = sw.Elapsed.TotalMilliseconds / (iterations * frames.Length);
                    }
                    long* stats = stackalloc long[8];
                    for (int k = 0; k < 8; k++) stats[k] = 0;
                    job.Stats = stats; job.Stage = 0;
                    foreach (float f in frames) { job.Time = f; job.Run(); }
                    job.Stats = null;
                    sb.AppendLine($"per frame: span px {stats[0] / frames.Length}, group px {stats[1] / frames.Length}, cleared px {stats[2] / frames.Length}, edges {stats[3] / frames.Length}, items {stats[4] / frames.Length}, gradient px {stats[5] / frames.Length}, opaque fill px {stats[6] / frames.Length}");
                    sb.AppendLine($"burst={Unity.Burst.BurstCompiler.IsEnabled} overhead {totals[3]:F3} ms  scene {totals[1]:F3} ms  geometry {totals[2] - totals[1]:F3} ms  raster {totals[0] - totals[2]:F3} ms  total {totals[0]:F3} ms  (items {fd.Items.Length}, groups {fd.Groups.Length}, geoPts {rs.GeoPts.Length})");
                }
            }
            finally
            {
                px.Dispose(); rs.Dispose(); sc.Dispose(); fd.Dispose();
            }
            return sb.ToString();
        }

        /// <summary>Byte-compares the frame data of two frames (debugging the unchanged-frame detection).</summary>
        public static unsafe string CompareFrames(string jsonPath, float frameA, float frameB, int size)
        {
            var sb = new StringBuilder();
            using var anim = LottieAnimation.FromJson(File.ReadAllBytes(jsonPath));
            var fa = Mynell.Lottie.Scene.FrameData.Create();
            var fb = Mynell.Lottie.Scene.FrameData.Create();
            var sa = Mynell.Lottie.Scene.SceneScratch.Create();
            var sc = Mynell.Lottie.Scene.SceneScratch.Create();
            try
            {
                var m = LottieRenderState.FitTransform(anim.Width, anim.Height, size, size, true);
                var vp = new Unity.Mathematics.float4(0, 0, size, size);
                Mynell.Lottie.Scene.SceneBuilder.Create(anim.Native, fa, sa).Build(frameA, m, vp);
                Mynell.Lottie.Scene.SceneBuilder.Create(anim.Native, fb, sc).Build(frameB, m, vp);
                sb.Append(CompareList("Points", fa.Points, fb.Points));
                sb.Append(CompareList("Contours", fa.Contours, fb.Contours));
                sb.Append(CompareList("Items", fa.Items, fb.Items));
                sb.Append(CompareList("Paints", fa.Paints, fb.Paints));
                sb.Append(CompareList("PaintFloats", fa.PaintFloats, fb.PaintFloats));
                sb.Append(CompareList("Dashes", fa.Dashes, fb.Dashes));
                sb.Append(CompareList("Cmds", fa.Cmds, fb.Cmds));
                sb.Append(CompareList("Groups", fa.Groups, fb.Groups));
                sb.Append(CompareList("Effects", fa.Effects, fb.Effects));
            }
            finally
            {
                fa.Dispose(); fb.Dispose(); sa.Dispose(); sc.Dispose();
            }
            return sb.ToString();
        }

        static unsafe string CompareList<T>(string name, NativeList<T> x, NativeList<T> y) where T : unmanaged
        {
            if (x.Length != y.Length) return $"{name}: length {x.Length} vs {y.Length}\n";
            long bytes = (long)x.Length * sizeof(T);
            byte* px = (byte*)x.GetUnsafeReadOnlyPtr();
            byte* py = (byte*)y.GetUnsafeReadOnlyPtr();
            for (long i = 0; i < bytes; i++)
                if (px[i] != py[i])
                    return $"{name}: differs at element {i / sizeof(T)} of {x.Length} (size {sizeof(T)}, byte offset {i % sizeof(T)})\n";
            return $"{name}: same ({x.Length})\n";
        }

        /// <summary>Step-by-step timing probe (compile, scene build, raster) for one frame.</summary>
        public static string Probe(string jsonPath, float frame, int width, int height)
        {
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            try
            {
                var bytes = File.ReadAllBytes(jsonPath);
                var data = Mynell.Lottie.Compiler.LottieCompiler.Compile(bytes);
                sb.AppendLine($"compile {sw.Elapsed.TotalMilliseconds:F2} ms");
                sw.Restart();
                using var anim = LottieAnimation.FromData(data);
                sb.AppendLine($"native {sw.Elapsed.TotalMilliseconds:F2} ms");
                var fd = Mynell.Lottie.Scene.FrameData.Create();
                var sc = Mynell.Lottie.Scene.SceneScratch.Create();
                var rs = Mynell.Lottie.Raster.RasterScratch.Create();
                var px = new NativeArray<uint>(width * height, Allocator.Persistent);
                try
                {
                    var m = LottieRenderState.FitTransform(anim.Width, anim.Height, width, height, true);
                    sw.Restart();
                    var builder = Mynell.Lottie.Scene.SceneBuilder.Create(anim.Native, fd, sc);
                    builder.Build(frame, m, new Unity.Mathematics.float4(0, 0, width, height));
                    sb.AppendLine($"scene {sw.Elapsed.TotalMilliseconds:F2} ms items={fd.Items.Length} groups={fd.Groups.Length} cmds={fd.Cmds.Length} pts={fd.Points.Length}");
                    sw.Restart();
                    unsafe
                    {
                        var fr = new Mynell.Lottie.Raster.FrameRenderer
                        {
                            F = fd, R = rs, RootPx = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(px),
                            RootW = width, RootH = height, Tolerance = 0.1f,
                        };
                        fr.Render();
                    }
                    sb.AppendLine($"raster(managed) {sw.Elapsed.TotalMilliseconds:F2} ms geoPts={rs.GeoPts.Length}");
                }
                finally
                {
                    px.Dispose(); rs.Dispose(); sc.Dispose(); fd.Dispose();
                }
            }
            catch (System.Exception e)
            {
                sb.AppendLine("EXCEPTION: " + e);
            }
            return sb.ToString();
        }

        public static uint Unpremultiply(uint p)
        {
            uint a = p >> 24;
            if (a == 0) return 0;
            if (a == 255) return p;
            uint r = ((p & 0xFF) * 255 + a / 2) / a;
            uint g = (((p >> 8) & 0xFF) * 255 + a / 2) / a;
            uint b = (((p >> 16) & 0xFF) * 255 + a / 2) / a;
            if (r > 255) r = 255;
            if (g > 255) g = 255;
            if (b > 255) b = 255;
            return r | (g << 8) | (b << 16) | (a << 24);
        }
    }
}
