// Mynell Lottie Player — Phase D hand-off record.
// Produced by BuildDrawCommandsJob (Stage 1, IJob, sequential), consumed in parallel
// by TessellateCommandsJob (Stage 2, IJobParallelFor). Blittable, Burst-safe, 60 bytes.

using System.Runtime.InteropServices;

namespace Mynell.LottiePlayer
{
    /// <summary>
    /// Type of geometry produced by a single <see cref="DrawCommand"/>.
    /// One Lottie shape can spawn 0, 1 or 2 commands (fill + stroke = 2).
    /// </summary>
    public enum DrawCommandKind : byte
    {
        Fill = 0,    // ear-clip triangulate the polyline
        Stroke = 1,  // quad-extrude the polyline
    }

    /// <summary>
    /// One unit of work for the parallel tessellator. Stage 1 fills:
    ///   • <see cref="World"/>: composed parent · ancestor · self matrix at the current frame.
    ///   • <see cref="Color"/>: pre-packed RGBA uint matching VertexAttributeFormat.UNorm8.
    ///   • <see cref="Kind"/> + <see cref="Closed"/> + <see cref="StrokeWidth"/>: emission semantics.
    ///   • <see cref="PolyOffset"/>+<see cref="PolyCount"/>: window into the shared PolyPool
    ///     (a flat NativeArray&lt;float2&gt; populated by Stage 1 with the flattened, optionally
    ///     trimmed and compacted polyline in Lottie-internal space).
    ///   • <see cref="VertexOffset"/>/<see cref="VertexCount"/> + <see cref="IndexOffset"/>/<see cref="IndexCount"/>:
    ///     the disjoint, prefix-summed slot reserved in the global vertex/index buffers.
    ///     Stage 2 writes EXACTLY into [VertexOffset, VertexOffset+VertexCount) and
    ///     [IndexOffset, IndexOffset+IndexCount) — no atomics, no contention.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DrawCommand
    {
        public AffineMatrix World;     // 24 B
        public uint Color;             //  4 B — packed RGBA (R = byte0)
        public DrawCommandKind Kind;   //  1 B
        public byte Closed;            //  1 B — 1 if stroke polyline closes back to start (fill: always 1)
        public byte _pad0;             //  1 B
        public byte _pad1;             //  1 B
        public float StrokeWidth;      //  4 B — composition units; ignored for Fill
        public int PolyOffset;         //  4 B
        public int PolyCount;          //  4 B
        public int VertexOffset;       //  4 B
        public int VertexCount;        //  4 B — exact count Stage 2 will write
        public int IndexOffset;        //  4 B
        public int IndexCount;         //  4 B — exact count Stage 2 will write
        // Total: 60 B, 4-byte aligned, no padding tail.
    }
}
