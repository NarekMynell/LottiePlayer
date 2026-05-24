// Mynell Lottie Player — Mathematical core for affine transformations.
// Phase A foundation: replaces the previous scalar (anchor + position + scale + rotation)
// approximation that broke under non-uniform scale combined with parent rotation. Uses
// standard 3x3 matrix concatenation expressed as 2x3 (last row implicit [0 0 1]).
// All operations are unmanaged, allocation-free and aggressively inlined for Burst.

using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Mynell.LottiePlayer
{
    /// <summary>
    /// 2x3 column-major affine transform. 24 bytes, blittable, Burst-safe
    /// (no managed references). Layout:
    /// <code>
    /// | m00 m01 m02 |   | c0.x c1.x c2.x |
    /// | m10 m11 m12 | = | c0.y c1.y c2.y |
    /// |  0   0   1  |
    /// </code>
    /// <para>Convention: <c>Apply(v) = c0 · v.x + c1 · v.y + c2</c>.
    /// Composition: <c>(a · b).Apply(v) == a.Apply(b.Apply(v))</c>.</para>
    /// </summary>
    public struct AffineMatrix
    {
        /// <summary>First column = transformed X basis (linear part).</summary>
        public float2 c0;
        /// <summary>Second column = transformed Y basis (linear part).</summary>
        public float2 c1;
        /// <summary>Third column = translation.</summary>
        public float2 c2;

        public static AffineMatrix Identity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new AffineMatrix
            {
                c0 = new float2(1f, 0f),
                c1 = new float2(0f, 1f),
                c2 = float2.zero,
            };
        }

        /// <summary>
        /// Standard Bodymovin / After Effects transform for layer <c>ks</c> or group <c>tr</c>.
        /// Evaluated strictly as <c>T(pos) · R(rot) · S(scale/100) · T(-anchor)</c>, which
        /// moves the anchor to the origin, scales, rotates, then places the anchor at
        /// <paramref name="pos"/> in parent space. When scale is identity and rotation is
        /// zero this reduces to a pure translation by <c>pos - anchor</c>.
        /// <para>The result lives in Lottie's native Y-down composition space. The
        /// composition → RectTransform bridge (<see cref="NormalizationBridge"/>) is
        /// applied once at the root — never per-layer.</para>
        /// </summary>
        /// <param name="anchor">Pivot in local space ('a').</param>
        /// <param name="position">Anchor placement in parent space ('p').</param>
        /// <param name="scalePerc">Lottie scale as percent ([100,100] = identity).</param>
        /// <param name="rotDeg">Rotation in degrees, CW-positive (Lottie / AE convention).</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix GetMatrixFromTransform(float2 anchor, float2 pos, float2 scalePerc, float rotDeg)
        {
            // 1. Normalize Scale from percentage (100% -> 1.0)
            float2 s = scalePerc / 100f;

            // 2. Convert rotation to radians
            float rad = math.radians(rotDeg);
            float sin = math.sin(rad);
            float cos = math.cos(rad);

            AffineMatrix m;
            // Basis vectors (Rotation * Scale)
            m.c0 = new float2(cos * s.x, sin * s.x);
            m.c1 = new float2(-sin * s.y, cos * s.y);

            // 3. Complete the TRS chain: T(pos) · R · S · T(-anchor)
            m.c2 = new float2(
                pos.x - (m.c0.x * anchor.x + m.c1.x * anchor.y),
                pos.y - (m.c0.y * anchor.x + m.c1.y * anchor.y));

            return m;
        }

        /// <summary>Alias for <see cref="GetMatrixFromTransform"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix FromLottieTransform(float2 anchor, float2 pos, float2 scalePerc, float rotDeg)
            => GetMatrixFromTransform(anchor, pos, scalePerc, rotDeg);

        /// <summary>Pure translation.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix Translation(float2 t)
        {
            AffineMatrix m;
            m.c0 = new float2(1f, 0f);
            m.c1 = new float2(0f, 1f);
            m.c2 = t;
            return m;
        }

        /// <summary>Pure per-axis scale.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix Scale(float2 s)
        {
            AffineMatrix m;
            m.c0 = new float2(s.x, 0f);
            m.c1 = new float2(0f, s.y);
            m.c2 = float2.zero;
            return m;
        }

        /// <summary>Pure rotation, degrees (CW-positive in Lottie's Y-down space).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix RotationDegrees(float deg)
        {
            math.sincos(math.radians(deg), out float sn, out float cs);
            AffineMatrix m;
            m.c0 = new float2( cs,  sn);
            m.c1 = new float2(-sn,  cs);
            m.c2 = float2.zero;
            return m;
        }

        /// <summary>
        /// Map a Lottie composition coordinate (top-left origin, Y-down) to 0..1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 CompToNormalized(float2 compPoint, float2 compSize)
        {
            float iw = 1f / math.max(compSize.x, 1e-6f);
            float ih = 1f / math.max(compSize.y, 1e-6f);
            return new float2(compPoint.x * iw, compPoint.y * ih);
        }

        /// <summary>
        /// Normalization bridge: Lottie normalized position → RectTransform local space
        /// centered at the pivot. Implements
        /// <c>(normalized * rectDimensions) - (rectDimensions * 0.5)</c> with Y-up output.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 NormalizedToRectCentered(float2 normalized, float2 rectDimensions)
            => normalized * rectDimensions - rectDimensions * 0.5f;

        /// <summary>
        /// Full comp-pixel → centered RectTransform local point (normalize, flip Y, center).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float2 CompToRectCentered(float2 compPoint, float2 compSize, float2 rectDimensions)
        {
            float2 norm = CompToNormalized(compPoint, compSize);
            norm.y = 1f - norm.y;
            return NormalizedToRectCentered(norm, rectDimensions);
        }

        /// <summary>
        /// Root bridge applied once when traversing the layer tree. Maps Lottie composition
        /// pixels (top-left, Y-down) into RectTransform local units (pivot-centered, Y-up).
        /// Uses only composition size and rect dimensions — no canvas / screen offsets.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix NormalizationBridge(float2 compSize, float2 rectDimensions)
        {
            float iw = 1f / math.max(compSize.x, 1e-6f);
            float ih = 1f / math.max(compSize.y, 1e-6f);
            AffineMatrix m;
            m.c0 = new float2(rectDimensions.x * iw, 0f);
            m.c1 = new float2(0f, -rectDimensions.y * ih);
            m.c2 = new float2(-rectDimensions.x * 0.5f, rectDimensions.y * 0.5f);
            return m;
        }

        /// <summary>
        /// Pure linear map from baked comp pixels to RectTransform local units:
        /// <c>FinalX = BakedX * (RectW / AssetW)</c>, same for Y. No translation / rotation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix RectLinearMap(float2 assetSize, float2 rectSize)
        {
            float iw = 1f / math.max(assetSize.x, 1e-6f);
            float ih = 1f / math.max(assetSize.y, 1e-6f);
            AffineMatrix m;
            m.c0 = new float2(rectSize.x * iw, 0f);
            m.c1 = new float2(0f, rectSize.y * ih);
            m.c2 = float2.zero;
            return m;
        }

        /// <summary>
        /// Standard 3x3 matrix product (implicit [0 0 1] bottom row).
        /// Result satisfies <c>(a · b).Apply(v) == a.Apply(b.Apply(v))</c>.
        /// Use to walk parent → child chains: <c>world_child = world_parent · local_child</c>.
        /// When <see cref="DebugForceIdentityMultiply"/> is true, always returns
        /// <see cref="Identity"/> (diagnostic — set by <c>MynellLottieUGUI</c>).
        /// </summary>
        public static bool DebugForceIdentityMultiply;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix Multiply(in AffineMatrix parent, in AffineMatrix local)
        {
            if (DebugForceIdentityMultiply)
                return Identity;

            AffineMatrix m;
            m.c0 = parent.c0 * local.c0.x + parent.c1 * local.c0.y;
            m.c1 = parent.c0 * local.c1.x + parent.c1 * local.c1.y;
            m.c2 = parent.c0 * local.c2.x + parent.c1 * local.c2.y + parent.c2;
            return m;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AffineMatrix operator *(AffineMatrix a, AffineMatrix b) => Multiply(in a, in b);

        /// <summary>Transform a point (full affine, includes translation).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2 Apply(float2 p) => c0 * p.x + c1 * p.y + c2;

        /// <summary>
        /// Transform a direction / tangent — linear part only (translation invariant).
        /// Use for stroke normals and bezier tangents that must scale + rotate without
        /// being translated.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2 ApplyDirection(float2 d) => c0 * d.x + c1 * d.y;

        /// <summary>
        /// Determinant of the 2x2 linear part. Sign = orientation
        /// (negative ⇒ winding flipped, e.g. after Y-flip), magnitude = area scale factor.
        /// Zero ⇒ transform collapses the plane to a line or a point.
        /// </summary>
        public float Determinant
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => c0.x * c1.y - c0.y * c1.x;
        }

        /// <summary>
        /// True when the linear part is (near-)singular. Used by the Phase B path
        /// validator to skip shapes whose tessellation would emit NaN / infinity.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsDegenerate(float epsilon = 1e-8f) => math.abs(Determinant) < epsilon;

        /// <summary>
        /// Full affine inverse. Caller MUST guard with <see cref="IsDegenerate"/>.
        /// Intended for editor hit-testing and gizmo coordinate transforms; not for
        /// the per-vertex hot path (the renderer uses forward Apply only).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AffineMatrix Inverse()
        {
            float invDet = 1f / Determinant;
            AffineMatrix r;
            r.c0 = new float2( c1.y, -c0.y) * invDet;
            r.c1 = new float2(-c1.x,  c0.x) * invDet;
            r.c2 = -(r.c0 * c2.x + r.c1 * c2.y);
            return r;
        }
    }
}
