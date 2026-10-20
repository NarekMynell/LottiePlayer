using System;
using Mynell.Lottie.Compiler;
using Mynell.Lottie.Eval;
using Mynell.Lottie.Model;
using Mynell.Lottie.Raster;
using Mynell.Lottie.Scene;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Mynell.Lottie
{
    /// <summary>
    /// A compiled animation ready for rendering: Burst-side tables plus decoded image assets.
    /// Shared by any number of players. Create with <see cref="FromJson(string, Func{string, byte[]})"/> or through a
    /// <see cref="LottieAsset"/>; dispose when no longer needed.
    /// </summary>
    public sealed class LottieAnimation : IDisposable
    {
        public LottieData Data { get; private set; }
        LottieNativeData _native;
        NativeArray<uint> _imagePixels;
        NativeArray<int4> _imageInfo;

        /// <summary>Burst-side tables (valid until <see cref="Dispose"/>).</summary>
        public LottieNativeData Native => _native;
        /// <summary>Decoded image assets: premultiplied RGBA32 pixels of all images, back to back (valid until <see cref="Dispose"/>).</summary>
        public NativeArray<uint> ImagePixels => _imagePixels;
        /// <summary>Per image: (pixel offset, width, height, 0).</summary>
        public NativeArray<int4> ImageInfo => _imageInfo;

        public float Width => Data.Width;
        public float Height => Data.Height;
        public float FrameRate => Data.FrameRate;
        public float InPoint => Data.InPoint;
        public float OutPoint => Data.OutPoint;
        /// <summary>Duration in seconds.</summary>
        public float Duration => Data.Duration;
        public int MarkerCount => Data.Markers.Length;
        public LottieMarker GetMarker(int index) => Data.Markers[index];
        public bool IsDisposed => !_native.IsCreated;

        LottieAnimation(LottieData data, Func<string, byte[]> imageResolver)
        {
            Data = data;
            _native = LottieNativeData.Create(data);
            LoadImages(imageResolver);
        }

        /// <summary>
        /// Compiles Lottie JSON. <paramref name="imageResolver"/> maps external image paths (u + p) to file bytes;
        /// <paramref name="options"/> can supply fonts for text without embedded glyphs.
        /// </summary>
        public static LottieAnimation FromJson(string json, Func<string, byte[]> imageResolver = null, LottieCompileOptions options = null) =>
            new LottieAnimation(LottieCompiler.Compile(json, options), imageResolver);

        public static LottieAnimation FromJson(byte[] utf8, Func<string, byte[]> imageResolver = null, LottieCompileOptions options = null) =>
            new LottieAnimation(LottieCompiler.Compile(utf8, options), imageResolver);

        public static LottieAnimation FromData(LottieData data, Func<string, byte[]> imageResolver = null) =>
            new LottieAnimation(data, imageResolver);

        /// <summary>Loads a blob produced by <see cref="LottieData.Serialize"/>; null when the blob is invalid or outdated.</summary>
        public static LottieAnimation FromCompiled(byte[] blob, Func<string, byte[]> imageResolver = null)
        {
            if (!LottieData.TryDeserialize(blob, out var data)) return null;
            return new LottieAnimation(data, imageResolver);
        }

        /// <summary>Index of the marker named <paramref name="name"/>, or -1.</summary>
        public int FindMarker(string name)
        {
            var m = Data.Markers;
            for (int i = 0; i < m.Length; i++)
                if (string.Equals(m[i].Name, name, StringComparison.Ordinal)) return i;
            return -1;
        }

        void LoadImages(Func<string, byte[]> resolver)
        {
            int n = Data.Images.Length;
            _imageInfo = new NativeArray<int4>(math.max(1, n), Allocator.Persistent);
            var decoded = new Color32[n][];
            var sizes = new int2[n];
            int total = 0;
            Texture2D tex = null;
            for (int i = 0; i < n; i++)
            {
                _imageInfo[i] = new int4(-1, 0, 0, 0);
                byte[] bytes = i < Data.ImageBytes.Length ? Data.ImageBytes[i] : null;
                if ((bytes == null || bytes.Length == 0) && resolver != null && i < Data.ImagePaths.Length &&
                    !string.IsNullOrEmpty(Data.ImagePaths[i]))
                {
                    try { bytes = resolver(Data.ImagePaths[i]); }
                    catch (Exception e) { Debug.LogWarning($"[Lottie] Image '{Data.ImagePaths[i]}' could not be resolved: {e.Message}"); }
                }
                if (bytes == null || bytes.Length == 0) continue;
                if (tex == null) tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes, false)) continue;
                decoded[i] = tex.GetPixels32();
                sizes[i] = new int2(tex.width, tex.height);
                total += MipChainSize(tex.width, tex.height, out _);
            }
            if (tex != null) LottieUtil.DestroySafe(tex);

            _imagePixels = new NativeArray<uint>(math.max(1, total), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            int offset = 0;
            for (int i = 0; i < n; i++)
            {
                var px = decoded[i];
                if (px == null) continue;
                int w = sizes[i].x, h = sizes[i].y;
                // Premultiply and flip to y-down rows (Lottie space).
                for (int y = 0; y < h; y++)
                {
                    int src = (h - 1 - y) * w;
                    int dst = offset + y * w;
                    for (int x = 0; x < w; x++)
                    {
                        Color32 c = px[src + x];
                        uint a = c.a;
                        uint r = Px8.Mul8(c.r, a), g = Px8.Mul8(c.g, a), b = Px8.Mul8(c.b, a);
                        _imagePixels[dst + x] = r | (g << 8) | (b << 16) | (a << 24);
                    }
                }
                int chain = MipChainSize(w, h, out int levels);
                BuildMips(_imagePixels, offset, w, h, levels);
                _imageInfo[i] = new int4(offset, w, h, levels);
                offset += chain;
            }
        }

        /// <summary>Pixel count of an image and its mip levels (halved down to 1 x 1).</summary>
        static int MipChainSize(int w, int h, out int levels)
        {
            int total = 0;
            levels = 0;
            while (true)
            {
                total += w * h;
                levels++;
                if (w == 1 && h == 1) break;
                w = math.max(1, w >> 1);
                h = math.max(1, h >> 1);
            }
            return total;
        }

        /// <summary>Box-filters each level (premultiplied) from the previous one; images are sampled trilinearly when minified.</summary>
        static void BuildMips(NativeArray<uint> px, int offset, int w, int h, int levels)
        {
            for (int l = 1; l < levels; l++)
            {
                int nw = math.max(1, w >> 1), nh = math.max(1, h >> 1);
                int dst = offset + w * h;
                for (int y = 0; y < nh; y++)
                {
                    int y0 = math.min(2 * y, h - 1), y1 = math.min(2 * y + 1, h - 1);
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = math.min(2 * x, w - 1), x1 = math.min(2 * x + 1, w - 1);
                        uint a = px[offset + y0 * w + x0], b = px[offset + y0 * w + x1];
                        uint c = px[offset + y1 * w + x0], d = px[offset + y1 * w + x1];
                        uint rb = ((a & 0x00FF00FFu) + (b & 0x00FF00FFu) + (c & 0x00FF00FFu) + (d & 0x00FF00FFu) + 0x00020002u) >> 2;
                        uint ga = (((a >> 8) & 0x00FF00FFu) + ((b >> 8) & 0x00FF00FFu) + ((c >> 8) & 0x00FF00FFu) + ((d >> 8) & 0x00FF00FFu) + 0x00020002u) >> 2;
                        px[dst + y * nw + x] = (rb & 0x00FF00FFu) | ((ga & 0x00FF00FFu) << 8);
                    }
                }
                offset = dst;
                w = nw;
                h = nh;
            }
        }

        public void Dispose()
        {
            LottieScheduler.CompleteAll();
            if (_native.IsCreated) _native.Dispose();
            if (_imagePixels.IsCreated) _imagePixels.Dispose();
            if (_imageInfo.IsCreated) _imageInfo.Dispose();
        }
    }

    /// <summary>
    /// Per-instance native render state (reused buffers). Not thread-safe; one frame at a time.
    /// Frames identical to the previous one are detected (content hash) and not rasterized again; large targets are
    /// rasterized in horizontal bands on several worker threads.
    /// </summary>
    public sealed class LottieRenderState : IDisposable
    {
        FrameData _frame = FrameData.Create();
        SceneScratch _scene = SceneScratch.Create();
        RasterScratch _raster = RasterScratch.Create();
        RasterScratch[] _bands = Array.Empty<RasterScratch>();
        NativeArray<ulong> _status = new NativeArray<ulong>(4, Allocator.Persistent);
        JobHandle _pending;
        bool _hasPending;
        IntPtr _lastPixels;

        /// <summary>Flattening tolerance in device pixels (lower = smoother curves, more work).</summary>
        public float Tolerance = 0.03f;

        /// <summary>Skip rasterizing frames whose content equals the previously rendered frame (the target keeps its pixels).</summary>
        public bool SkipUnchanged = true;

        /// <summary>
        /// The destination keeps the last frame even when the CPU buffer moves (a texture whose upload is skipped for
        /// unchanged frames). When false, rendering into a different buffer always rasterizes.
        /// </summary>
        internal bool PersistentTarget;

        /// <summary>Property overrides applied to every frame rendered with this state (owned by the player).</summary>
        internal LottieOverrides Overrides;

        /// <summary>
        /// Upper limit of parallel bands per frame. 0 = automatic (a few bands per worker thread, for load balancing),
        /// 1 = single-threaded rasterization.
        /// </summary>
        public static int MaxBands = 0;

        /// <summary>Targets with fewer pixels are rasterized by a single job.</summary>
        public static int MinPixelsForBands = 512 * 256;

        /// <summary>Automatic mode: bands per thread (more bands than threads balance uneven content).</summary>
        internal const int BandsPerThread = 4;

        const int MinBandHeight = 48;

        /// <summary>Debug: when set, band jobs write the executing thread index here (one int per band).</summary>
        internal unsafe int* DebugThreadLog;
        internal int LastBandCount;

        public bool IsRendering => _hasPending;

        /// <summary>False when the last completed frame was identical to the previous one (the target was left untouched).</summary>
        public bool LastFrameChanged
        {
            get
            {
                Complete();
                return _status[3] != 0;
            }
        }

        /// <summary>Uniform "fit" transform (xMidYMid meet) from composition space to a pixel buffer.</summary>
        public static Affine2 FitTransform(float compW, float compH, int width, int height, bool flipY)
        {
            float s = math.min(width / compW, height / compH);
            float tx = (width - compW * s) * 0.5f;
            float ty = (height - compH * s) * 0.5f;
            if (!flipY) return new Affine2(s, 0, 0, s, tx, ty);
            return new Affine2(s, 0, 0, -s, tx, height - ty);
        }

        /// <summary>Non-uniform transform mapping the composition exactly onto the buffer.</summary>
        public static Affine2 StretchTransform(float compW, float compH, int width, int height, bool flipY)
        {
            float sx = width / compW, sy = height / compH;
            if (!flipY) return new Affine2(sx, 0, 0, sy, 0, 0);
            return new Affine2(sx, 0, 0, -sy, 0, height);
        }

        /// <summary>Forgets the previous frame: the next render rasterizes even if the content is unchanged.</summary>
        public void Invalidate()
        {
            Complete();
            _status[2] = 0;
        }

        /// <summary>Bands available per render when several players render in the same frame (0 = no limit).</summary>
        internal static int BandBudget;

        static int BandCount(int width, int height)
        {
            int workers = Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount;
            if (workers <= 0 || (long)width * height < MinPixelsForBands) return 1;
            int max = MaxBands > 0 ? MaxBands : BandsPerThread * (workers + 1);
            if (BandBudget > 0) max = math.min(max, BandBudget);
            return math.clamp(math.min(max, height / MinBandHeight), 1, 64);
        }

        /// <summary>Schedules rendering into <paramref name="pixels"/> (width x height RGBA32, premultiplied).</summary>
        public unsafe JobHandle Schedule(LottieAnimation anim, float absoluteFrame, Affine2 compToDevice,
            NativeArray<uint> pixels, int width, int height, JobHandle dependsOn = default)
        {
            Complete();
            var status = (ulong*)_status.GetUnsafePtr();
            var ptr = (uint*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(pixels);
            if ((IntPtr)ptr != _lastPixels && !PersistentTarget)
            {
                _status[2] = 0;
                _lastPixels = (IntPtr)ptr;
            }
            int bands = BandCount(width, height);
            LastBandCount = bands;
            var job = new RenderFrameJob
            {
                Data = anim.Native,
                ImagePixels = anim.ImagePixels,
                ImageInfo = anim.ImageInfo,
                Frame = _frame,
                Scene = _scene,
                Raster = _raster,
                Time = absoluteFrame,
                CompToDevice = compToDevice,
                Width = width,
                Height = height,
                Pixels = ptr,
                Tolerance = Tolerance,
                Status = status,
                SkipUnchanged = SkipUnchanged,
                PrepareOnly = bands > 1,
                OverrideSlots = Overrides != null ? Overrides.Slots : null,
                OverrideValues = Overrides != null ? Overrides.Values : null,
            };
            _pending = job.Schedule(dependsOn);
            if (bands > 1)
            {
                if (_bands.Length < bands)
                {
                    int old = _bands.Length;
                    Array.Resize(ref _bands, bands);
                    for (int b = old; b < bands; b++) _bands[b] = RasterScratch.Create();
                }
                var handles = new NativeArray<JobHandle>(bands, Allocator.Temp);
                for (int b = 0; b < bands; b++)
                {
                    handles[b] = new RenderBandJob
                    {
                        ImagePixels = anim.ImagePixels,
                        ImageInfo = anim.ImageInfo,
                        Status = status,
                        Frame = _frame,
                        Geometry = _raster,
                        Scratch = _bands[b],
                        Width = width,
                        Height = height,
                        Y0 = (int)((long)height * b / bands),
                        Y1 = (int)((long)height * (b + 1) / bands),
                        Pixels = ptr,
                        Tolerance = Tolerance,
                        ThreadLog = DebugThreadLog != null ? DebugThreadLog + b : null,
                    }.Schedule(_pending);
                }
                _pending = JobHandle.CombineDependencies(handles);
                handles.Dispose();
            }
            _hasPending = true;
            return _pending;
        }

        /// <summary>Renders synchronously (worker threads help with large targets).</summary>
        public void RenderImmediate(LottieAnimation anim, float absoluteFrame, Affine2 compToDevice,
            NativeArray<uint> pixels, int width, int height)
        {
            var handle = Schedule(anim, absoluteFrame, compToDevice, pixels, width, height);
            JobHandle.ScheduleBatchedJobs();
            // JobHandle.Complete() runs jobs that have not started yet on the calling thread, one after another:
            // let the worker threads pick up the bands first (bounded, in case they are busy with other work).
            if (LastBandCount > 1)
            {
                long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 50;
                while (!handle.IsCompleted && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                    System.Threading.Thread.SpinWait(64);
            }
            Complete();
        }

        public void Complete()
        {
            if (!_hasPending) return;
            _pending.Complete();
            _hasPending = false;
        }

        public void Dispose()
        {
            Complete();
            _frame.Dispose();
            _scene.Dispose();
            _raster.Dispose();
            foreach (var b in _bands) b.Dispose();
            _bands = Array.Empty<RasterScratch>();
            if (_status.IsCreated) _status.Dispose();
        }
    }
}
