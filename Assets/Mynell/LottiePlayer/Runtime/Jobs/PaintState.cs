// Mynell Lottie Player — Paint accumulator that flows through the hierarchy walk.
// Kept separate from the geometry-side AffineMatrix (LottieMath.cs) so each layer
// of state evolves independently: paint multiplies down the group chain, transform
// composes via matrix multiplication.

using Unity.Mathematics;

namespace Mynell.LottiePlayer
{
    /// <summary>
    /// Per-group paint accumulator. Geometry/transform state is expressed as a true
    /// 2x3 <see cref="AffineMatrix"/>; this struct only carries the paint-pipeline
    /// data that flows alongside the matrix.
    /// 16 bytes (float + float4 + 4 padding bytes for HasFill).
    /// </summary>
    public struct PaintState
    {
        /// <summary>Accumulated layer/group opacity in [0, 1] (already multiplied through ancestry).</summary>
        public float Opacity;
        /// <summary>Current fill color RGBA in [0, 1]. Alpha already folded with Opacity.</summary>
        public float4 Color;
        /// <summary>1 if a Fill shape established a color in this group, 0 otherwise.</summary>
        public byte HasFill;
        public byte _pad0;
        public byte _pad1;
        public byte _pad2;

        public static PaintState Default => new PaintState
        {
            Opacity = 1f,
            Color = new float4(1f, 1f, 1f, 1f),
            HasFill = 0,
        };
    }
}
