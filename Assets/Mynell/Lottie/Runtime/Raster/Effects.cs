using Mynell.Lottie.Scene;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Raster
{
    /// <summary>
    /// Layer effects applied to an isolated group surface (premultiplied RGBA8), following the SVG filters
    /// lottie-web builds (feColorMatrix / feComponentTransfer / feGaussianBlur / drop shadow chain).
    /// </summary>
    public static unsafe partial class Effects
    {
        /// <summary>How far an effect can push content outside its source bounds (device pixels, x / y).</summary>
        public static float2 Reach(in EffectOp op)
        {
            switch (op.Kind)
            {
                case EffectOpKind.GaussianBlur:
                    return new float2(op.Z, op.W) * 3f + 2f;
                case EffectOpKind.DropShadow:
                    return math.abs(new float2(op.X, op.Y)) + new float2(op.Z, op.W) * 3f + 2f;
                case EffectOpKind.StyleShadow:
                    return (op.Flags & 1) != 0 ? float2.zero : math.abs(new float2(op.X, op.Y)) + new float2(op.Z, op.W) * 3f + 2f;
                case EffectOpKind.StyleGlow:
                    if ((op.Flags & 1) != 0) return float2.zero;
                    return (op.Flags & 4) != 0 ? new float2(op.Z, op.W) + 2f : new float2(op.Z, op.W) * 3f + 2f;
                case EffectOpKind.DirectionalBlur:
                    return math.abs(op.A.xy) * op.X * 3f + 2f;
                case EffectOpKind.Sharpen:
                    return new float2(1, 1);
                default:
                    return float2.zero;
            }
        }

        // ------------------------------------------------------------------ colour operations

        static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : math.pow((c + 0.055f) / 1.055f, 2.4f);
        static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * math.pow(c, 1f / 2.4f) - 0.055f;

        /// <summary>Applies a per-pixel colour effect (Fill, Tint, Tritone, Levels).</summary>
        public static void ColorOp(in EffectOp op, uint* px, int count, float* paintFloats)
        {
            // lottie-web greys through linearRGB: gray = toSRGB(mean(toLinear(r, g, b))).
            float* toLin = stackalloc float[256];
            const int EncSize = 4096;
            float* toSrgb = stackalloc float[EncSize + 1];
            if (op.Kind == EffectOpKind.Tint || op.Kind == EffectOpKind.Tritone)
            {
                for (int i = 0; i < 256; i++) toLin[i] = SrgbToLinear(i / 255f);
                for (int i = 0; i <= EncSize; i++) toSrgb[i] = LinearToSrgb(i / (float)EncSize);
            }

            float* tr = op.TableR >= 0 ? paintFloats + op.TableR : null;
            float* tg = op.TableG >= 0 ? paintFloats + op.TableG : null;
            float* tb = op.TableB >= 0 ? paintFloats + op.TableB : null;
            float* ta = op.TableA >= 0 ? paintFloats + op.TableA : null;
            float* trgb = op.TableRGB >= 0 ? paintFloats + op.TableRGB : null;

            // The result depends on the pixel only: runs of equal pixels reuse the previous result.
            uint lastIn = 0, lastOut = 0;
            bool haveLast = false;
            for (int i = 0; i < count; i++)
            {
                uint p = px[i];
                if (haveLast && p == lastIn)
                {
                    px[i] = lastOut;
                    continue;
                }
                uint a8 = p >> 24;
                if (a8 == 0 && op.Kind != EffectOpKind.Levels) continue;
                float a = a8 * Blend.Inv255;
                float3 c = a8 == 0 ? float3.zero : new float3(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF) * (1f / 255f) / a;
                c = math.saturate(c);
                switch (op.Kind)
                {
                    case EffectOpKind.Fill:
                        c = op.A.xyz;
                        a *= math.saturate(op.A.w);
                        break;
                    case EffectOpKind.Tint:
                    {
                        float g = Gray(c, toLin, toSrgb, EncSize);
                        float3 tinted = op.A.xyz + (op.B.xyz - op.A.xyz) * g;
                        c = math.lerp(c, tinted, math.saturate(op.X));
                        break;
                    }
                    case EffectOpKind.Tritone:
                    {
                        float g = Gray(c, toLin, toSrgb, EncSize);
                        // feFuncX table [shadows, midtones, highlights]
                        float3 r = g < 0.5f ? math.lerp(op.A.xyz, op.B.xyz, g * 2f) : math.lerp(op.B.xyz, op.C.xyz, (g - 0.5f) * 2f);
                        c = math.lerp(r, c, math.saturate(op.X));
                        break;
                    }
                    case EffectOpKind.Levels:
                    {
                        if (tr != null) c.x = Table(tr, c.x);
                        if (tg != null) c.y = Table(tg, c.y);
                        if (tb != null) c.z = Table(tb, c.z);
                        if (ta != null) a = Table(ta, a);
                        if (trgb != null) c = new float3(Table(trgb, c.x), Table(trgb, c.y), Table(trgb, c.z));
                        break;
                    }
                }
                a = math.saturate(a);
                lastIn = p;
                lastOut = Px8.Pack(new float4(math.saturate(c) * a, a));
                haveLast = true;
                px[i] = lastOut;
            }
        }

        static float Gray(float3 c, float* toLin, float* toSrgb, int encSize)
        {
            int3 i = (int3)(c * 255f + 0.5f);
            float lin = (toLin[i.x] + toLin[i.y] + toLin[i.z]) * (1f / 3f);
            return toSrgb[(int)(math.saturate(lin) * encSize + 0.5f)];
        }

        /// <summary>feComponentTransfer type="table" with 256 entries.</summary>
        static float Table(float* t, float v)
        {
            float x = math.saturate(v) * 255f;
            int k = math.min((int)x, 254);
            return math.lerp(t[k], t[k + 1], x - k);
        }

        // ------------------------------------------------------------------ gaussian blur (SVG 1.1 approximation)

        /// <summary>
        /// Blurs a premultiplied RGBA surface in place. Uses the SVG specification's three box blurs for sigma >= 2
        /// (as Chrome / Skia do) and an exact Gaussian kernel below.
        /// </summary>
        public static void BlurRGBA(uint* px, int w, int h, float sigmaX, float sigmaY, uint* tmp)
        {
            if (sigmaX > 0.01f) BlurPass(px, w, h, 1, w, sigmaX, tmp);
            if (sigmaY > 0.01f) BlurPass(px, h, w, w, 1, sigmaY, tmp);
        }

        /// <summary>One direction: <paramref name="lines"/> lines of <paramref name="len"/> pixels.</summary>
        static void BlurPass(uint* px, int len, int lines, int step, int lineStep, float sigma, uint* tmp)
        {
            for (int l = 0; l < lines; l++)
            {
                uint* line = px + l * lineStep;
                // Skip empty lines quickly.
                bool any = false;
                for (int i = 0; i < len && !any; i++) any = line[i * step] != 0;
                if (!any) continue;
                for (int i = 0; i < len; i++) tmp[i] = line[i * step];
                BlurLine(tmp, len, sigma, tmp + len);
                for (int i = 0; i < len; i++) line[i * step] = tmp[i];
            }
        }

        static void BlurLine(uint* data, int n, float sigma, uint* scratch)
        {
            if (sigma < 2f)
            {
                GaussianLine(data, n, sigma, scratch);
                return;
            }
            int d = (int)math.floor(sigma * 3f * math.sqrt(2f * math.PI) / 4f + 0.5f);
            if (d < 1) return;
            if ((d & 1) == 1)
            {
                BoxLine(data, n, d, d / 2, scratch);
                BoxLine(data, n, d, d / 2, scratch);
                BoxLine(data, n, d, d / 2, scratch);
            }
            else
            {
                BoxLine(data, n, d, d / 2, scratch);      // centred between the pixel and its left neighbour
                BoxLine(data, n, d, d / 2 - 1, scratch);  // centred between the pixel and its right neighbour
                BoxLine(data, n, d + 1, d / 2, scratch);  // centred on the pixel
            }
        }

        /// <summary>Box blur: out[i] = mean(in[i - left .. i - left + size - 1]) with transparent outside.</summary>
        static void BoxLine(uint* data, int n, int size, int left, uint* scratch)
        {
            int r = 0, g = 0, b = 0, a = 0;
            // window for i = 0 covers [-left, size - left - 1]
            for (int k = -left; k < size - left; k++)
            {
                if (k < 0 || k >= n) continue;
                uint p = data[k];
                r += (int)(p & 0xFF); g += (int)((p >> 8) & 0xFF); b += (int)((p >> 16) & 0xFF); a += (int)(p >> 24);
            }
            uint half = (uint)size / 2;
            for (int i = 0; i < n; i++)
            {
                scratch[i] = (uint)((r + half) / size) | ((uint)((g + half) / size) << 8) | ((uint)((b + half) / size) << 16) | ((uint)((a + half) / size) << 24);
                int outK = i - left;
                int inK = i - left + size;
                if (outK >= 0 && outK < n)
                {
                    uint p = data[outK];
                    r -= (int)(p & 0xFF); g -= (int)((p >> 8) & 0xFF); b -= (int)((p >> 16) & 0xFF); a -= (int)(p >> 24);
                }
                if (inK >= 0 && inK < n)
                {
                    uint p = data[inK];
                    r += (int)(p & 0xFF); g += (int)((p >> 8) & 0xFF); b += (int)((p >> 16) & 0xFF); a += (int)(p >> 24);
                }
            }
            UnsafeUtility.MemCpy(data, scratch, n * 4L);
        }

        static void GaussianLine(uint* data, int n, float sigma, uint* scratch)
        {
            int radius = math.max(1, (int)math.ceil(sigma * 3f));
            float* k = stackalloc float[2 * 64 + 1];
            radius = math.min(radius, 64);
            float sum = 0;
            for (int i = -radius; i <= radius; i++)
            {
                float v = math.exp(-(i * i) / (2f * sigma * sigma));
                k[i + radius] = v;
                sum += v;
            }
            for (int i = 0; i <= 2 * radius; i++) k[i] /= sum;
            for (int i = 0; i < n; i++)
            {
                float4 acc = float4.zero;
                int lo = math.max(0, i - radius), hi = math.min(n - 1, i + radius);
                for (int j = lo; j <= hi; j++)
                {
                    uint p = data[j];
                    if (p == 0) continue;
                    acc += new float4(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, p >> 24) * k[j - i + radius];
                }
                uint4 u = (uint4)math.min(acc + 0.5f, 255f);
                scratch[i] = u.x | (u.y << 8) | (u.z << 16) | (u.w << 24);
            }
            UnsafeUtility.MemCpy(data, scratch, n * 4L);
        }

        // ------------------------------------------------------------------ drop shadow

        /// <summary>
        /// feGaussianBlur(SourceAlpha) -> feOffset -> flood colour "in" -> merge(shadow, source).
        /// <paramref name="shadow"/> and <paramref name="tmp"/> are scratch buffers of w x h and 2 x max(w, h) pixels.
        /// </summary>
        public static void DropShadow(in EffectOp op, uint* px, int w, int h, uint* shadow, uint* tmp)
        {
            int n = w * h;
            // Shadow colour (premultiplied) scaled by the source alpha.
            float4 col = new float4(op.A.xyz * op.A.w, op.A.w);
            uint colPacked = Px8.Pack(col);
            // alpha only, stored as a grey premultiplied pixel so the RGBA blur can be reused
            for (int i = 0; i < n; i++)
            {
                uint a = px[i] >> 24;
                shadow[i] = a * 0x01010101u;
            }
            BlurRGBA(shadow, w, h, op.Z, op.W, tmp);

            // Offset with bilinear sampling, then composite source over shadow.
            float dx = op.X, dy = op.Y;
            int ix = (int)math.floor(dx), iy = (int)math.floor(dy);
            uint fx = (uint)((dx - ix) * 256f + 0.5f), fy = (uint)((dy - iy) * 256f + 0.5f);
            bool shadowOnly = op.Flags == 1;
            for (int y = 0; y < h; y++)
            {
                uint* row = px + y * w;
                for (int x = 0; x < w; x++)
                {
                    // sample shadow at (x - dx, y - dy)
                    int sx0 = x - ix - 1, sy0 = y - iy - 1;
                    uint a00 = Alpha(shadow, w, h, sx0, sy0), a10 = Alpha(shadow, w, h, sx0 + 1, sy0);
                    uint a01 = Alpha(shadow, w, h, sx0, sy0 + 1), a11 = Alpha(shadow, w, h, sx0 + 1, sy0 + 1);
                    // weights: position x - dx = (x - ix - 1) + (1 - fx/256)
                    uint wx = 256 - fx, wy = 256 - fy;
                    uint top = (a00 * (256 - wx) + a10 * wx);
                    uint bot = (a01 * (256 - wx) + a11 * wx);
                    uint sa = (top * (256 - wy) + bot * wy + 32768) >> 16;
                    uint s = sa == 0 ? 0 : Px8.Mul(colPacked, sa);
                    uint src = row[x];
                    row[x] = shadowOnly ? s : Px8.Over(src, s);
                }
            }
        }

        static uint Alpha(uint* buf, int w, int h, int x, int y)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return 0;
            return buf[y * w + x] >> 24;
        }
    }
}
