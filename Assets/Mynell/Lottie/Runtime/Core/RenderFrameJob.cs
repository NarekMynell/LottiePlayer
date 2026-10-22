using Mynell.Lottie.Eval;
using Mynell.Lottie.Model;
using Mynell.Lottie.Raster;
using Mynell.Lottie.Scene;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Mynell.Lottie
{
    /// <summary>Evaluates one frame of an animation and rasterizes it into an RGBA32 (premultiplied) buffer.</summary>
    [BurstCompile]
    public unsafe struct RenderFrameJob : IJob
    {
        [ReadOnly] public LottieNativeData Data;
        [ReadOnly] public NativeArray<uint> ImagePixels;
        [ReadOnly] public NativeArray<int4> ImageInfo;
        public FrameData Frame;
        public SceneScratch Scene;
        public RasterScratch Raster;
        /// <summary>Absolute frame of the root composition.</summary>
        public float Time;
        /// <summary>Root composition space -> buffer pixels (row 0 = first row in memory).</summary>
        public Affine2 CompToDevice;
        public int Width, Height;
        [NativeDisableUnsafePtrRestriction] public uint* Pixels;
        public float Tolerance;
        /// <summary>Debug: 0 full, 1 scene only, 2 scene + geometry, 3 nothing.</summary>
        public int Stage;
        [NativeDisableUnsafePtrRestriction] public long* Stats;

        /// <summary>
        /// Optional persistent status (4 values): [0..1] hash of the last rendered frame, [2] hash valid, [3] output:
        /// 1 when the frame was (or has to be) rasterized, 2 when it has to be rasterized as a single band (effects
        /// reading pixels anywhere in a layer), 0 when it was identical to the previous one.
        /// </summary>
        [NativeDisableUnsafePtrRestriction] public ulong* Status;
        /// <summary>Skip rasterization when the frame content equals the previous one (requires <see cref="Status"/>).</summary>
        public bool SkipUnchanged;
        /// <summary>Only build the scene and geometry; rows are rasterized by <see cref="RenderBandJob"/>s.</summary>
        public bool PrepareOnly;
        /// <summary>Optional property overrides (see <see cref="LottieOverrides"/>).</summary>
        [NativeDisableUnsafePtrRestriction] public int* OverrideSlots;
        [NativeDisableUnsafePtrRestriction] public float4* OverrideValues;

        public void Execute()
        {
            if (Stage == 3) return;
            var sb = SceneBuilder.Create(Data, Frame, Scene);
            sb.P.OverrideSlots = OverrideSlots;
            sb.P.OverrideValues = OverrideValues;
            sb.Build(Time, CompToDevice, new float4(0, 0, Width, Height));
            if (Stage == 1) return;
            if (Status != null)
            {
                uint4 h = HashFrame();
                ulong lo = h.x | ((ulong)h.y << 32), hi = h.z | ((ulong)h.w << 32);
                bool same = SkipUnchanged && Status[2] != 0 && Status[0] == lo && Status[1] == hi;
                Status[0] = lo;
                Status[1] = hi;
                Status[2] = 1;
                Status[3] = same ? 0UL : HasNonLocalEffects() ? 2UL : 1UL;
                if (same) return;
            }
            var fr = new FrameRenderer
            {
                F = Frame,
                R = Raster,
                G = Raster,
                RootPx = Pixels,
                RootW = Width,
                RootH = Height,
                Tolerance = Tolerance > 0 ? Tolerance : 0.03f,
                Banded = PrepareOnly,
                Stats = Stats,
                ImagePixels = (uint*)ImagePixels.GetUnsafeReadOnlyPtr(),
                ImageInfo = (int4*)ImageInfo.GetUnsafeReadOnlyPtr(),
                ImageCount = ImageInfo.Length,
            };
            if (Stage == 2 || PrepareOnly) fr.Prepare();
            else fr.Render();
        }

        /// <summary>Effects reading anywhere in a layer, or layers warped in perspective: the frame renders as one band.</summary>
        bool HasNonLocalEffects()
        {
            for (int i = 0; i < Frame.Effects.Length; i++)
                if (Effects.IsNonLocal(Frame.Effects[i].Kind)) return true;
            for (int i = 0; i < Frame.Groups.Length; i++)
                if (Frame.Groups[i].Projected != 0) return true;
            return false;
        }

        /// <summary>Hash of everything that determines the output pixels.</summary>
        uint4 HashFrame()
        {
            var st = new xxHash3.StreamingState(false);
            st.Update(new int4(Width, Height, math.asint(Tolerance), ImageInfo.Length));
            Add(ref st, Frame.Points);
            Add(ref st, Frame.Contours);
            Add(ref st, Frame.Items);
            Add(ref st, Frame.Paints);
            Add(ref st, Frame.PaintFloats);
            Add(ref st, Frame.Dashes);
            Add(ref st, Frame.Cmds);
            Add(ref st, Frame.Groups);
            Add(ref st, Frame.Effects);
            return st.DigestHash128();
        }

        static void Add<T>(ref xxHash3.StreamingState st, NativeList<T> list) where T : unmanaged
        {
            int n = list.Length;
            st.Update(n);
            if (n > 0) st.Update(list.GetUnsafeReadOnlyPtr(), n * sizeof(T));
        }
    }

    /// <summary>Rasterizes rows [Y0, Y1) of a frame prepared by <see cref="RenderFrameJob"/> (with PrepareOnly).</summary>
    [BurstCompile]
    public unsafe struct RenderBandJob : IJob
    {
        [ReadOnly] public NativeArray<uint> ImagePixels;
        [ReadOnly] public NativeArray<int4> ImageInfo;
        [NativeDisableUnsafePtrRestriction] public ulong* Status;
        /// <summary>Frame and geometry are shared (read-only) by all bands of a frame.</summary>
        [NativeDisableContainerSafetyRestriction] public FrameData Frame;
        [NativeDisableContainerSafetyRestriction] public RasterScratch Geometry;
        /// <summary>This band's own working memory.</summary>
        public RasterScratch Scratch;
        public int Width, Height, Y0, Y1;
        [NativeDisableUnsafePtrRestriction] public uint* Pixels;
        public float Tolerance;
        /// <summary>Debug: receives the worker thread index that executed this band (null = off).</summary>
        [NativeDisableUnsafePtrRestriction] public int* ThreadLog;
        [NativeSetThreadIndex] int _threadIndex;

        public void Execute()
        {
            if (ThreadLog != null) *ThreadLog = _threadIndex;
            if (Status != null && Status[3] == 0) return;
            int y0 = Y0, y1 = Y1;
            if (Status != null && Status[3] == 2)
            {
                // Non-local effects: the first band renders the whole frame.
                if (Y0 != 0) return;
                y1 = Height;
            }
            var fr = new FrameRenderer
            {
                F = Frame,
                R = Scratch,
                G = Geometry,
                RootPx = Pixels,
                RootW = Width,
                RootH = Height,
                Tolerance = Tolerance > 0 ? Tolerance : 0.03f,
                ImagePixels = (uint*)ImagePixels.GetUnsafeReadOnlyPtr(),
                ImageInfo = (int4*)ImageInfo.GetUnsafeReadOnlyPtr(),
                ImageCount = ImageInfo.Length,
            };
            fr.ExecuteBand(y0, y1);
        }
    }
}
