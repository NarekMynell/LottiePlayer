using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Mynell.Lottie.Eval
{
    /// <summary>
    /// 2D affine transform using lottie-web's row-vector convention:
    /// x' = a*x + c*y + tx, y' = b*x + d*y + ty.
    /// <c>A.Then(B)</c> applies A first, then B (lottie-web: A.multiply(B)).
    /// </summary>
    public struct Affine2
    {
        public float a, b, c, d, tx, ty;

        public static Affine2 Identity => new Affine2 { a = 1, d = 1 };

        public Affine2(float a, float b, float c, float d, float tx, float ty)
        {
            this.a = a; this.b = b; this.c = c; this.d = d; this.tx = tx; this.ty = ty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2 Apply(float2 p) => new float2(a * p.x + c * p.y + tx, b * p.x + d * p.y + ty);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float2 ApplyVector(float2 v) => new float2(a * v.x + c * v.y, b * v.x + d * v.y);

        /// <summary>Returns the transform that applies this, then <paramref name="n"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Affine2 Then(in Affine2 n)
        {
            return new Affine2(
                a * n.a + b * n.c,
                a * n.b + b * n.d,
                c * n.a + d * n.c,
                c * n.b + d * n.d,
                tx * n.a + ty * n.c + n.tx,
                tx * n.b + ty * n.d + n.ty);
        }

        public float Determinant => a * d - b * c;

        public Affine2 Inverse()
        {
            float det = a * d - b * c;
            if (math.abs(det) < 1e-20f) return Identity;
            float id = 1f / det;
            var r = new Affine2(d * id, -b * id, -c * id, a * id, 0, 0);
            r.tx = -(tx * r.a + ty * r.c);
            r.ty = -(tx * r.b + ty * r.d);
            return r;
        }

        /// <summary>Largest scale factor applied to lengths (for tolerance / stroke width computations).</summary>
        public float MaxScale
        {
            get
            {
                // Singular values of [[a c][b d]]: sqrt of eigenvalues of M^T M.
                float e = (a * a + b * b + c * c + d * d) * 0.5f;
                float f = (a * a + b * b - c * c - d * d) * 0.5f;
                float g = a * c + b * d;
                float h = math.sqrt(f * f + g * g);
                return math.sqrt(math.max(0, e + h));
            }
        }

        public float MinScale
        {
            get
            {
                float e = (a * a + b * b + c * c + d * d) * 0.5f;
                float f = (a * a + b * b - c * c - d * d) * 0.5f;
                float g = a * c + b * d;
                float h = math.sqrt(f * f + g * g);
                return math.sqrt(math.max(0, e - h));
            }
        }

        public bool IsAxisAligned => math.abs(b) < 1e-6f && math.abs(c) < 1e-6f;

        public static Affine2 Translate(float x, float y) => new Affine2(1, 0, 0, 1, x, y);
        public static Affine2 Scale(float x, float y) => new Affine2(x, 0, 0, y, 0, 0);

        /// <summary>lottie-web Matrix.rotate(angle): x' = x cos + y sin, y' = -x sin + y cos.</summary>
        public static Affine2 Rotate(float angle)
        {
            math.sincos(angle, out float s, out float co);
            return new Affine2(co, -s, s, co, 0, 0);
        }

        /// <summary>lottie-web Matrix.skewFromAxis(ax, angle).</summary>
        public static Affine2 SkewFromAxis(float ax, float angle)
        {
            math.sincos(angle, out float s, out float co);
            var r1 = new Affine2(co, s, -s, co, 0, 0);
            var sk = new Affine2(1, 0, math.tan(ax), 1, 0, 0);
            var r2 = new Affine2(co, -s, s, co, 0, 0);
            return r1.Then(sk).Then(r2);
        }
    }

    /// <summary>Helpers reproducing lottie-web's 4x4 Matrix operations (row-vector convention).</summary>
    public static class LMat4
    {
        // Element (r, c) = lottie props[4r + c]; lottie "m._t(M2)" == math.mul(m, M2).

        public static float4x4 Identity => float4x4.identity;

        public static float4x4 Translate(in float4x4 m, float x, float y, float z)
        {
            if (x == 0 && y == 0 && z == 0) return m;
            var t = new float4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1);
            return math.mul(m, t);
        }

        public static float4x4 Scale(in float4x4 m, float x, float y, float z)
        {
            if (x == 1 && y == 1 && z == 1) return m;
            var t = new float4x4(x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1);
            return math.mul(m, t);
        }

        public static float4x4 Rotate(in float4x4 m, float angle)
        {
            if (angle == 0) return m;
            math.sincos(angle, out float s, out float c);
            var t = new float4x4(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            return math.mul(m, t);
        }

        public static float4x4 RotateX(in float4x4 m, float angle)
        {
            if (angle == 0) return m;
            math.sincos(angle, out float s, out float c);
            var t = new float4x4(1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1);
            return math.mul(m, t);
        }

        public static float4x4 RotateY(in float4x4 m, float angle)
        {
            if (angle == 0) return m;
            math.sincos(angle, out float s, out float c);
            var t = new float4x4(c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1);
            return math.mul(m, t);
        }

        public static float4x4 RotateZ(in float4x4 m, float angle) => Rotate(m, angle);

        public static float4x4 SkewFromAxis(in float4x4 m, float ax, float angle)
        {
            math.sincos(angle, out float s, out float c);
            var r1 = new float4x4(c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            var sk = new float4x4(1, 0, 0, 0, math.tan(ax), 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            var r2 = new float4x4(c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            return math.mul(math.mul(math.mul(m, r1), sk), r2);
        }

        /// <summary>2D part used by lottie-web's to2dCSS (props 0, 1, 4, 5, 12, 13).</summary>
        public static Affine2 To2D(in float4x4 m)
        {
            // float4x4 is column-major: element (r, c) is m[c][r].
            return new Affine2(m.c0.x, m.c1.x, m.c0.y, m.c1.y, m.c0.w, m.c1.w);
        }
    }
}
