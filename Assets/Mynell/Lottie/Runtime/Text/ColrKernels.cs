using Mynell.Lottie.Raster;
using Unity.Burst;
using Unity.Mathematics;

namespace Mynell.Lottie.Text
{
    /// <summary>Burst-compiled pixel loops of <see cref="ColrGlyphs"/> (premultiplied RGBA canvas, float coverage).</summary>
    [BurstCompile]
    static unsafe class ColrKernels
    {
        /// <summary>A gradient fill: pixel centre → paint space, the geometry and the colour line lookup table.</summary>
        public struct Gradient
        {
            public float A, B, C, D, E, F;
            public int Kind;                        // 0 linear, 1 radial (two-point conical), 2 sweep
            public float2 P0, Dir;                  // linear: t = (q - p0) · dir
            public float2 C0, Cd;                   // radial: circles (c0, r0) -> (c0 + cd, r0 + dr)
            public float R0, Dr, Qa;
            public float Start, Sweep;              // sweep: degrees, counter-clockwise
            public float First, Scale;              // t -> LUT position (t - first) * scale; scale 0: a step at first
            public int Extend, LutSize;             // 0 pad, 1 repeat, 2 reflect
            public float4 FirstColor, LastColor;
        }

        /// <summary>Coverage rows from the signed area accumulation buffer (which is left zeroed).</summary>
        [BurstCompile]
        public static void Resolve(float* acc, float* cov, int stride, int w, int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                float* row = acc + y * stride;
                float sum = 0f;
                for (int x = 0; x < x0; x++) sum += row[x];
                float* dst = cov + y * w;
                for (int x = x0; x < x1; x++)
                {
                    sum += row[x];
                    dst[x] = math.min(1f, math.abs(sum));
                }
                for (int x = 0; x < stride; x++) row[x] = 0f;
            }
        }

        /// <summary>mask *= clip inside the clip's extent, 0 elsewhere in the mask's extent.</summary>
        [BurstCompile]
        public static void Intersect(float* mask, float* clip, int w, int mx0, int my0, int mx1, int my1, int cx0, int cy0, int cx1, int cy1)
        {
            for (int y = my0; y < my1; y++)
            {
                float* m = mask + y * w;
                float* c = clip + y * w;
                bool rowIn = y >= cy0 && y < cy1;
                for (int x = mx0; x < mx1; x++) m[x] = rowIn && x >= cx0 && x < cx1 ? m[x] * c[x] : 0f;
            }
        }

        /// <summary>Source-over of a solid premultiplied colour through a coverage mask (null: everywhere).</summary>
        [BurstCompile]
        public static void FillSolid(float4* dst, float* cov, int w, int x0, int y0, int x1, int y1, in float4 color)
        {
            float4 c = color;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = y * w + x;
                    float k = cov == null ? 1f : cov[i];
                    if (k <= 0f) continue;
                    float4 s = c * k;
                    dst[i] = s + dst[i] * (1f - s.w);
                }
            }
        }

        /// <summary>Source-over of a gradient through a coverage mask (null: everywhere).</summary>
        [BurstCompile]
        public static void FillGradient(float4* dst, float* cov, int w, int x0, int y0, int x1, int y1, in Gradient g, float4* lut)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int i = y * w + x;
                    float k = cov == null ? 1f : cov[i];
                    if (k <= 0f) continue;
                    float px = x + 0.5f, py = y + 0.5f;
                    float2 q = new float2(g.A * px + g.C * py + g.E, g.B * px + g.D * py + g.F);
                    float t;
                    if (g.Kind == 0) t = math.dot(q - g.P0, g.Dir);
                    else if (g.Kind == 1)
                    {
                        if (!Conical(q - g.C0, g.Cd, g.R0, g.Dr, g.Qa, out t)) continue;
                    }
                    else
                    {
                        float2 d = q - g.C0;
                        float angle = math.degrees(math.atan2(d.y, d.x));
                        if (angle < 0f) angle += 360f;
                        t = g.Sweep == 0f ? (angle < g.Start ? -1f : 1f) * float.MaxValue : (angle - g.Start) / g.Sweep;
                    }
                    float4 c = Lookup(g, lut, t);
                    if (c.w <= 0f) continue;
                    float4 s = c * k;
                    dst[i] = s + dst[i] * (1f - s.w);
                }
            }
        }

        /// <summary>
        /// Premultiplied colours of a colour line (sorted stops, straight colours interpolated unpremultiplied like Skia)
        /// at size + 1 steps from its first to its last stop.
        /// </summary>
        [BurstCompile]
        public static void BuildLut(float* offsets, float4* colors, int count, float4* lut, int size)
        {
            float first = offsets[0], last = offsets[count - 1], range = last - first;
            int k = 1;
            for (int i = 0; i <= size; i++)
            {
                float t = first + range * i / size;
                float4 c;
                if (!(t > first)) c = colors[0];
                else if (!(t < last)) c = colors[count - 1];
                else
                {
                    while (k < count - 1 && offsets[k] <= t) k++;
                    float span = offsets[k] - offsets[k - 1];
                    c = math.lerp(colors[k - 1], colors[k], span > 0f ? (t - offsets[k - 1]) / span : 1f);
                }
                lut[i] = new float4(c.xyz * c.w, c.w);
            }
        }

        static float4 Lookup(in Gradient g, float4* lut, float t)
        {
            if (math.isnan(t)) return float4.zero;
            if (g.Scale == 0f) return t < g.First ? g.FirstColor : g.LastColor;
            float u = (t - g.First) * g.Scale;
            if (g.Extend == 1) u -= math.floor(u);
            else if (g.Extend == 2)
            {
                u -= 2f * math.floor(u * 0.5f);
                if (u > 1f) u = 2f - u;
            }
            u = math.clamp(u, 0f, 1f);
            return lut[(int)(u * g.LutSize + 0.5f)];
        }

        /// <summary>Two-point conical gradient: the largest t whose circle (radius ≥ 0) passes through the point.</summary>
        static bool Conical(float2 pd, float2 cd, float r0, float dr, float a, out float t)
        {
            float b = math.dot(pd, cd) + r0 * dr;
            float c = math.dot(pd, pd) - r0 * r0;
            t = 0;
            if (math.abs(a) < 1e-6f * math.max(1f, math.dot(cd, cd) + dr * dr))
            {
                if (b == 0f) return false;
                t = c / (2f * b);
                return r0 + t * dr >= 0f;
            }
            float disc = b * b - a * c;
            if (disc < 0f) return false;
            float sq = math.sqrt(disc);
            float t1 = (b + sq) / a, t2 = (b - sq) / a;
            float hi = math.max(t1, t2), lo = math.min(t1, t2);
            if (r0 + hi * dr >= 0f)
            {
                t = hi;
                return true;
            }
            if (r0 + lo * dr >= 0f)
            {
                t = lo;
                return true;
            }
            return false;
        }

        /// <summary>dst = composite(source, backdrop) source-over dst (COLRv1 composite modes).</summary>
        [BurstCompile]
        public static void Composite(float4* dst, float4* source, float4* backdrop, int count, int mode)
        {
            for (int i = 0; i < count; i++)
            {
                float4 r = CompositeMode(mode, source[i], backdrop[i]);
                dst[i] = r + dst[i] * (1f - r.w);
            }
        }

        static float4 CompositeMode(int mode, float4 s, float4 d)
        {
            switch (mode)
            {
                case 0: return float4.zero;                                         // clear
                case 1: return s;                                                   // src
                case 2: return d;                                                   // dest
                case 3: return s + d * (1f - s.w);                                  // src over
                case 4: return d + s * (1f - d.w);                                  // dest over
                case 5: return s * d.w;                                             // src in
                case 6: return d * s.w;                                             // dest in
                case 7: return s * (1f - d.w);                                      // src out
                case 8: return d * (1f - s.w);                                      // dest out
                case 9: return s * d.w + d * (1f - s.w);                            // src atop
                case 10: return d * s.w + s * (1f - d.w);                           // dest atop
                case 11: return s * (1f - d.w) + d * (1f - s.w);                    // xor
                case 12: return math.min(s + d, 1f);                                // plus
                case 23: return Blend.Composite(1, s, d);                           // multiply
                default:
                    // screen, overlay, darken, lighten, dodge, burn, hard light, soft light, difference, exclusion
                    // (lottie blend modes 2 to 11) and hue, saturation, color, luminosity (12 to 15).
                    if (mode >= 13 && mode <= 22) return Blend.Composite(mode - 11, s, d);
                    if (mode >= 24 && mode <= 27) return Blend.Composite(mode - 12, s, d);
                    return s + d * (1f - s.w);
            }
        }

        /// <summary>Premultiplied float RGBA to straight RGBA8.</summary>
        [BurstCompile]
        public static void ToRgba8(float4* src, byte* dst, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float4 c = src[i];
                if (!(c.w > 0f))
                {
                    dst[i * 4] = dst[i * 4 + 1] = dst[i * 4 + 2] = dst[i * 4 + 3] = 0;
                    continue;
                }
                float3 rgb = math.saturate(c.xyz / c.w) * 255f + 0.5f;
                dst[i * 4] = (byte)rgb.x;
                dst[i * 4 + 1] = (byte)rgb.y;
                dst[i * 4 + 2] = (byte)rgb.z;
                dst[i * 4 + 3] = (byte)(math.saturate(c.w) * 255f + 0.5f);
            }
        }
    }
}
