using Unity.Mathematics;

namespace Mynell.Lottie.Raster
{
    /// <summary>Premultiplied RGBA8 pixel helpers and W3C compositing / blend modes (lottie <c>bm</c> values).</summary>
    public static class Blend
    {
        public const float Inv255 = 1f / 255f;

        public static float4 Unpack(uint p) =>
            new float4(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, p >> 24) * Inv255;

        public static uint Pack(float4 c)
        {
            uint4 u = (uint4)(math.saturate(c) * 255f + 0.5f);
            return u.x | (u.y << 8) | (u.z << 16) | (u.w << 24);
        }

        /// <summary>Source-over of a premultiplied source onto a premultiplied destination.</summary>
        public static float4 Over(float4 s, float4 d) => s + d * (1f - s.w);

        /// <summary>
        /// Composites premultiplied <paramref name="s"/> onto <paramref name="d"/> with blend mode <paramref name="mode"/>
        /// (0 normal, 1 multiply, 2 screen, 3 overlay, 4 darken, 5 lighten, 6 color-dodge, 7 color-burn,
        /// 8 hard-light, 9 soft-light, 10 difference, 11 exclusion, 12 hue, 13 saturation, 14 color, 15 luminosity).
        /// </summary>
        public static float4 Composite(int mode, float4 s, float4 d)
        {
            if (mode == 0 || mode > 15 || d.w <= 0f) return Over(s, d);
            if (s.w <= 0f) return d;
            float3 cs = s.xyz / s.w;
            float3 cb = d.xyz / d.w;
            float3 b;
            switch (mode)
            {
                case 1: b = cb * cs; break;
                case 2: b = cb + cs - cb * cs; break;
                case 3: b = HardLight(cs: cb, cb: cs); break;
                case 4: b = math.min(cb, cs); break;
                case 5: b = math.max(cb, cs); break;
                case 6: b = new float3(Dodge(cb.x, cs.x), Dodge(cb.y, cs.y), Dodge(cb.z, cs.z)); break;
                case 7: b = new float3(Burn(cb.x, cs.x), Burn(cb.y, cs.y), Burn(cb.z, cs.z)); break;
                case 8: b = HardLight(cb, cs); break;
                case 9: b = new float3(Soft(cb.x, cs.x), Soft(cb.y, cs.y), Soft(cb.z, cs.z)); break;
                case 10: b = math.abs(cb - cs); break;
                case 11: b = cb + cs - 2f * cb * cs; break;
                case 12: b = SetLum(SetSat(cs, Sat(cb)), Lum(cb)); break;
                case 13: b = SetLum(SetSat(cb, Sat(cs)), Lum(cb)); break;
                case 14: b = SetLum(cs, Lum(cb)); break;
                default: b = SetLum(cb, Lum(cs)); break;
            }
            float sa = s.w, da = d.w;
            float3 rgb = s.xyz * (1f - da) + d.xyz * (1f - sa) + sa * da * math.saturate(b);
            return new float4(rgb, sa + da - sa * da);
        }

        static float3 HardLight(float3 cb, float3 cs)
        {
            float3 mul = cb * 2f * cs;
            float3 cs2 = 2f * cs - 1f;
            float3 scr = cb + cs2 - cb * cs2;
            return math.select(scr, mul, cs <= 0.5f);
        }

        static float Dodge(float cb, float cs)
        {
            if (cb <= 0f) return 0f;
            if (cs >= 1f) return 1f;
            return math.min(1f, cb / (1f - cs));
        }

        static float Burn(float cb, float cs)
        {
            if (cb >= 1f) return 1f;
            if (cs <= 0f) return 0f;
            return 1f - math.min(1f, (1f - cb) / cs);
        }

        static float Soft(float cb, float cs)
        {
            if (cs <= 0.5f) return cb - (1f - 2f * cs) * cb * (1f - cb);
            float dd = cb <= 0.25f ? ((16f * cb - 12f) * cb + 4f) * cb : math.sqrt(cb);
            return cb + (2f * cs - 1f) * (dd - cb);
        }

        static float Lum(float3 c) => 0.3f * c.x + 0.59f * c.y + 0.11f * c.z;

        static float3 ClipColor(float3 c)
        {
            float l = Lum(c);
            float n = math.cmin(c);
            float x = math.cmax(c);
            if (n < 0f) c = l + (c - l) * l / (l - n);
            if (x > 1f) c = l + (c - l) * (1f - l) / (x - l);
            return c;
        }

        static float3 SetLum(float3 c, float l) => ClipColor(c + (l - Lum(c)));

        static float Sat(float3 c) => math.cmax(c) - math.cmin(c);

        static float3 SetSat(float3 c, float s)
        {
            float mx = math.cmax(c), mn = math.cmin(c);
            if (mx <= mn) return float3.zero;
            return (c - mn) * s / (mx - mn);
        }

        /// <summary>Luminance used by luma mattes (Skia's SkLumaColorFilter coefficients) of a premultiplied colour.</summary>
        public static float Luma(float4 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
    }
}
