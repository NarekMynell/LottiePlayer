using Mynell.Lottie.Scene;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Raster
{
    // After Effects effects and layer styles, following Skottie's implementations (colour filters on unpremultiplied
    // colours, SkSL filters on premultiplied colours, image filters for blurs and shadows).
    public static unsafe partial class Effects
    {
        /// <summary>A pixel surface: <see cref="Px"/> rows of <see cref="W"/> pixels, device origin (X0, Y0).</summary>
        public struct Target
        {
            public uint* Px;
            public int X0, Y0, W, H;
            public int Count => W * H;
            public float2 Center(int x, int y) => new float2(X0 + x + 0.5f, Y0 + y + 0.5f);
        }

        /// <summary>Effects that read pixels anywhere in the layer (the frame is then rendered as a single band).</summary>
        public static bool IsNonLocal(EffectOpKind k) =>
            k == EffectOpKind.MotionTile || k == EffectOpKind.CornerPin || k == EffectOpKind.Bulge ||
            k == EffectOpKind.Sphere || k == EffectOpKind.DisplacementMap;

        /// <summary>Effects that need an unmodified copy of the source while writing the output.</summary>
        public static bool NeedsSourceCopy(EffectOpKind k) =>
            IsNonLocal(k) || k == EffectOpKind.DirectionalBlur || k == EffectOpKind.Sharpen;

        static float4 Unpremul(uint p)
        {
            uint a8 = p >> 24;
            if (a8 == 0) return float4.zero;
            float a = a8 * Blend.Inv255;
            return new float4(math.saturate(new float3(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF) * Blend.Inv255 / a), a);
        }

        static uint PackUnpremul(float4 c)
        {
            c = math.saturate(c);
            return Px8.Pack(new float4(c.xyz * c.w, c.w));
        }

        static uint PackPremul(float4 c)
        {
            c.w = math.saturate(c.w);
            c.xyz = math.clamp(c.xyz, 0f, c.w);
            return Px8.Pack(c);
        }

        // ------------------------------------------------------------------ colour filters

        /// <summary>SkColorFilters::Matrix (4x5, unpremultiplied); HSL variant = SkColorFilters::HSLAMatrix.</summary>
        public static void ColorMatrix(in EffectOp op, in Target t, float* m, bool hsl)
        {
            float4 r0 = new float4(m[0], m[1], m[2], m[3]), r1 = new float4(m[5], m[6], m[7], m[8]);
            float4 r2 = new float4(m[10], m[11], m[12], m[13]), r3 = new float4(m[15], m[16], m[17], m[18]);
            float4 off = new float4(m[4], m[9], m[14], m[19]);
            bool fill = op.Flags == 1;
            int n = t.Count;
            for (int i = 0; i < n; i++)
            {
                uint p = t.Px[i];
                if (p == 0 && !fill) continue;
                float4 c = Unpremul(p);
                if (hsl) c.xyz = RgbToHsl(c.xyz);
                float4 o = new float4(math.dot(r0, c), math.dot(r1, c), math.dot(r2, c), math.dot(r3, c)) + off;
                if (hsl) o.xyz = HslToRgb(new float3(o.x, math.saturate(o.y), math.saturate(o.z)));
                t.Px[i] = PackUnpremul(o);
            }
        }

        /// <summary>Skia's rgb_to_hsl stage (h, s, l in 0..1).</summary>
        static float3 RgbToHsl(float3 c)
        {
            float mx = math.cmax(c), mn = math.cmin(c), d = mx - mn;
            float h = 0, s = 0, l = (mx + mn) * 0.5f;
            if (d > 0)
            {
                float dr = 1f / d;
                if (mx == c.x) h = (c.y - c.z) * dr + (c.y < c.z ? 6 : 0);
                else if (mx == c.y) h = (c.z - c.x) * dr + 2;
                else h = (c.x - c.y) * dr + 4;
                h *= 1f / 6f;
                s = d / (l > 0.5f ? 2 - mx - mn : mx + mn);
            }
            return new float3(h, s, l);
        }

        /// <summary>Skia's hsl_to_rgb stage (hue wraps).</summary>
        static float3 HslToRgb(float3 hsl)
        {
            float h = hsl.x, s = hsl.y, l = hsl.z;
            if (s <= 0) return new float3(l);
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            return new float3(HueToRgb(p, q, h + 1f / 3f), HueToRgb(p, q, h), HueToRgb(p, q, h - 1f / 3f));
        }

        static float HueToRgb(float p, float q, float t)
        {
            t -= math.floor(t);
            if (t < 1f / 6f) return p + (q - p) * 6 * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6;
            return p;
        }

        /// <summary>Skottie SkSL filters working on premultiplied colours (saturation, brightness, contrast, B&amp;W).</summary>
        public static void PremulFilter(in EffectOp op, in Target t)
        {
            int n = t.Count;
            for (int i = 0; i < n; i++)
            {
                uint p = t.Px[i];
                if (p == 0) continue;
                float4 c = Blend.Unpack(p);
                switch (op.Kind)
                {
                    case EffectOpKind.Saturate:
                    {
                        float2 rg = c.x < c.y ? c.xy : c.yx;
                        float cmin = math.min(rg.x, c.z), cmax = math.max(rg.y, c.z);
                        float ch = math.max(cmax - cmin, 0.0001f), mid = (cmin + cmax) * 0.5f;
                        float scaleMax = math.min(mid, c.w - mid) / ch * 2;
                        float scale = math.min(op.X, scaleMax);
                        c.xyz = mid + (c.xyz - mid) * scale;
                        break;
                    }
                    case EffectOpKind.Brightness:
                        c.xyz = 1 - math.pow(math.max(1 - c.xyz, 0f), op.X);
                        break;
                    case EffectOpKind.Contrast:
                        c.xyz = ((op.X * c.xyz + op.Y) * c.xyz + op.Z) * c.xyz;
                        break;
                    case EffectOpKind.BlackAndWhite:
                    {
                        float m = math.cmin(c.xyz);
                        float3 d = c.xyz - m;
                        float wy = math.min(d.x, d.y), wc = math.min(d.y, d.z), wm = math.min(d.z, d.x);
                        float wr = d.x - wy - wm, wg = d.y - wy - wc, wb = d.z - wc - wm;
                        float l = m + op.A.x * wr + op.A.y * wy + op.A.z * wg + op.A.w * wc + op.B.x * wb + op.B.y * wm;
                        c.xyz = l;
                        break;
                    }
                }
                t.Px[i] = PackPremul(c);
            }
        }

        /// <summary>Skottie ThresholdEffect: black / white by luminance.</summary>
        public static void Threshold(in EffectOp op, in Target t)
        {
            int n = t.Count;
            for (int i = 0; i < n; i++)
            {
                uint p = t.Px[i];
                if (p == 0) continue;
                float4 c = Unpremul(p);
                float lum = math.dot(c.xyz, new float3(0.2126f, 0.7152f, 0.0722f));
                float bw = lum < op.X ? 0 : 1;
                t.Px[i] = Px8.Pack(new float4(bw * c.w, bw * c.w, bw * c.w, c.w));
            }
        }

        /// <summary>SkColorFilters::TableARGB with byte-valued tables (Easy Levels).</summary>
        public static void ColorTables(in EffectOp op, in Target t, float* floats)
        {
            float* tr = op.TableR >= 0 ? floats + op.TableR : null, tg = op.TableG >= 0 ? floats + op.TableG : null;
            float* tb = op.TableB >= 0 ? floats + op.TableB : null, ta = op.TableA >= 0 ? floats + op.TableA : null;
            float* trgb = op.TableRGB >= 0 ? floats + op.TableRGB : null;
            int n = t.Count;
            for (int i = 0; i < n; i++)
            {
                uint p = t.Px[i];
                if (p == 0) continue;
                float4 c = Unpremul(p);
                if (trgb != null) c.xyz = new float3(Lut(trgb, c.x), Lut(trgb, c.y), Lut(trgb, c.z));
                if (tr != null) c.x = Lut(tr, c.x);
                if (tg != null) c.y = Lut(tg, c.y);
                if (tb != null) c.z = Lut(tb, c.z);
                if (ta != null) c.w = Lut(ta, c.w);
                t.Px[i] = PackUnpremul(c);
            }
        }

        static float Lut(float* table, float v) => table[(int)(math.saturate(v) * 255f + 0.5f)];

        /// <summary>sksg::GradientColorFilter (CC Toner): luminance through RGB tables, lerped with the source.</summary>
        public static void GradientMap(in EffectOp op, in Target t, float* table)
        {
            int n = t.Count;
            for (int i = 0; i < n; i++)
            {
                uint p = t.Px[i];
                if (p == 0) continue;
                float4 c = Unpremul(p);
                float l = math.dot(c.xyz, new float3(0.2126f, 0.7152f, 0.0722f));
                int k = (int)(math.saturate(l) * 255f + 0.5f);
                float3 g = new float3(table[k], table[256 + k], table[512 + k]);
                c.xyz = math.lerp(c.xyz, g, op.X);
                t.Px[i] = PackUnpremul(c);
            }
        }

        // ------------------------------------------------------------------ generators & masks

        /// <summary>Skottie GradientRamp: linear / radial gradient painted through the content alpha.</summary>
        public static void GradientRamp(in EffectOp op, in Target t)
        {
            float2 s = op.A.xy, e = op.A.zw, d = e - s;
            float len2 = math.lengthsq(d), radius = math.sqrt(len2);
            bool radial = op.Flags == 1;
            for (int y = 0; y < t.H; y++)
            {
                uint* row = t.Px + y * t.W;
                for (int x = 0; x < t.W; x++)
                {
                    uint p = row[x];
                    if (p == 0) continue;
                    float2 q = op.DeviceToEffect.Apply(t.Center(x, y));
                    float u;
                    if (radial) u = radius > 0 ? math.length(q - s) / radius : 1;
                    else u = len2 > 0 ? math.dot(q - s, d) / len2 : 1;
                    float4 c = math.lerp(op.B, op.C, math.saturate(u));
                    float a = (p >> 24) * Blend.Inv255;
                    row[x] = Px8.Pack(new float4(c.xyz * c.w, c.w) * a);
                }
            }
        }

        /// <summary>Linear gradient masks: Linear Wipe (Flags 0), Venetian Blinds (Flags 2), fully hidden (Flags 1).</summary>
        public static void LinearMask(in EffectOp op, in Target t)
        {
            if (op.Flags == 1)
            {
                UnsafeUtility.MemClear(t.Px, (long)t.Count * 4);
                return;
            }
            float2 p0 = op.A.xy, d = op.A.zw - op.A.xy;
            float len2 = math.lengthsq(d);
            if (!(len2 > 0)) return;
            for (int y = 0; y < t.H; y++)
            {
                uint* row = t.Px + y * t.W;
                for (int x = 0; x < t.W; x++)
                {
                    uint p = row[x];
                    if (p == 0) continue;
                    float2 q = op.DeviceToEffect.Apply(t.Center(x, y));
                    float u = math.dot(q - p0, d) / len2, a;
                    if (op.Flags == 2)
                    {
                        u -= math.floor(u);
                        float4 pos = op.B, col = op.C;
                        if (u <= pos.x) a = col.x;
                        else if (u <= pos.y) a = math.lerp(col.x, col.y, (u - pos.x) / math.max(pos.y - pos.x, 1e-9f));
                        else if (u <= pos.z) a = col.z;
                        else a = math.lerp(col.z, col.w, (u - pos.z) / math.max(pos.w - pos.z, 1e-9f));
                    }
                    else if (u < op.X) a = 0;
                    else if (u >= op.Y) a = 1;
                    else a = (u - op.X) / (op.Y - op.X);
                    uint a8 = (uint)(math.saturate(a) * 255f + 0.5f);
                    if (a8 < 255) row[x] = Px8.Mul(p, a8);
                }
            }
        }

        /// <summary>Skottie RadialWipe: a sweep gradient mask with a hard edge (Flags 1 = hidden, 2 = show the inside).</summary>
        public static void SweepMask(in EffectOp op, in Target t)
        {
            if (op.Flags == 1)
            {
                UnsafeUtility.MemClear(t.Px, (long)t.Count * 4);
                return;
            }
            bool showInside = op.Flags == 2;
            for (int y = 0; y < t.H; y++)
            {
                uint* row = t.Px + y * t.W;
                for (int x = 0; x < t.W; x++)
                {
                    if (row[x] == 0) continue;
                    float2 q = op.DeviceToEffect.Apply(t.Center(x, y)) - op.A.xy;
                    float angle = math.degrees(math.atan2(q.y, q.x));
                    if (angle < 0) angle += 360f;
                    bool inside = angle >= op.X && angle <= op.Y;
                    if (inside != showInside) row[x] = 0;
                }
            }
        }

        /// <summary>Skottie FractalNoise: grey noise painted through the content alpha (SrcIn).</summary>
        public static void FractalNoise(in EffectOp op, in Target t, float* data)
        {
            var inv = new Mynell.Lottie.Eval.Affine2(data[0], data[1], data[2], data[3], data[4], data[5]);
            var sub = new Mynell.Lottie.Eval.Affine2(data[6], data[7], data[8], data[9], data[10], data[11]);
            int filter = op.Flags & 3, fractal = (op.Flags >> 2) & 3;
            float octaves = op.X, persistence = op.Y;
            float2 planes = op.A.xy;
            float weight = op.A.z;
            for (int y = 0; y < t.H; y++)
            {
                uint* row = t.Px + y * t.W;
                for (int x = 0; x < t.W; x++)
                {
                    uint p = row[x];
                    if (p == 0) continue;
                    float2 xy = inv.Apply(op.DeviceToEffect.Apply(t.Center(x, y)));
                    float oct = octaves, amp = 1, wacc = 0, n = 0;
                    for (int i = 0; i < 20; i++)
                    {
                        float w = amp * math.min(oct, 1f);
                        n += w * Fractal(NoiseFilter(xy, filter, planes, weight), fractal);
                        wacc += w;
                        if (oct <= 1f) break;
                        oct -= 1f;
                        amp *= persistence;
                        xy = sub.Apply(xy);
                    }
                    n = wacc > 0 ? n / wacc : 0;
                    float a = (p >> 24) * Blend.Inv255;
                    row[x] = Px8.Pack(new float4(n, n, n, 1) * a);
                }
            }
        }

        static float NoiseHash(float3 v)
        {
            v = math.frac(v * 0.1031f);
            v += math.dot(v, v.zxy + 31.32f);
            return math.frac((v.x + v.y) * v.z);
        }

        static float SampleNoise(float2 xy, float2 planes, float weight)
        {
            xy = math.floor(xy);
            float n0 = NoiseHash(new float3(xy, planes.x)), n1 = NoiseHash(new float3(xy, planes.y));
            return math.lerp(n0, n1, weight);
        }

        static float NoiseFilter(float2 xy, int filter, float2 planes, float weight)
        {
            if (filter == 0) return SampleNoise(xy, planes, weight);
            xy -= 0.5f;
            float n00 = SampleNoise(xy, planes, weight), n10 = SampleNoise(xy + new float2(1, 0), planes, weight);
            float n01 = SampleNoise(xy + new float2(0, 1), planes, weight), n11 = SampleNoise(xy + new float2(1, 1), planes, weight);
            float2 f = math.frac(xy);
            if (filter == 2) f = math.smoothstep(0f, 1f, f);
            return math.lerp(math.lerp(n00, n10, f.x), math.lerp(n01, n11, f.x), f.y);
        }

        static float Fractal(float n, int kind)
        {
            switch (kind)
            {
                case 1: return 2 * math.abs(0.5f - n);
                case 2: n = 2 * math.abs(0.5f - n); return n * n;
                case 3: return math.sqrt(2 * math.abs(0.5f - n));
                default: return n;
            }
        }

        // ------------------------------------------------------------------ resampling effects

        /// <summary>Bilinear sample (premultiplied) of a surface at a device point; transparent outside.</summary>
        static float4 Sample(in Target src, float2 device)
        {
            float2 p = device - new float2(src.X0, src.Y0) - 0.5f;
            float2 f = math.floor(p);
            int x0 = (int)f.x, y0 = (int)f.y;
            float2 w = p - f;
            float4 c00 = Fetch(src, x0, y0), c10 = Fetch(src, x0 + 1, y0), c01 = Fetch(src, x0, y0 + 1), c11 = Fetch(src, x0 + 1, y0 + 1);
            return math.lerp(math.lerp(c00, c10, w.x), math.lerp(c01, c11, w.x), w.y);
        }

        static float4 Fetch(in Target s, int x, int y)
        {
            if ((uint)x >= (uint)s.W || (uint)y >= (uint)s.H) return float4.zero;
            uint p = s.Px[y * s.W + x];
            return p == 0 ? float4.zero : Blend.Unpack(p);
        }

        static float Wrap(float v, float size, bool mirror)
        {
            if (!(size > 0)) return v;
            if (!mirror) return v - size * math.floor(v / size);
            float period = 2 * size, m = v - period * math.floor(v / period);
            return m <= size ? m : period - m;
        }

        /// <summary>Skottie MotionTile: the layer box mapped onto a tile, repeated (or mirrored) over the output box.</summary>
        public static void MotionTile(in EffectOp op, in Target dst, in Target src)
        {
            float2 tileXY = op.A.xy, tileWH = op.A.zw, box = op.LayerSize;
            bool mirror = (op.Flags & 1) != 0, horizontal = op.Y != 0;
            float2 shift = horizontal ? new float2(tileWH.x * op.X, 0) : new float2(0, tileWH.y * op.X);
            for (int y = 0; y < dst.H; y++)
            {
                uint* row = dst.Px + y * dst.W;
                for (int x = 0; x < dst.W; x++)
                {
                    float2 q = op.DeviceToEffect.Apply(dst.Center(x, y));
                    if (q.x < op.B.x || q.y < op.B.y || q.x >= op.B.z || q.y >= op.B.w)
                    {
                        row[x] = 0;
                        continue;
                    }
                    if (op.X != 0)
                    {
                        // Alternate columns (rows) are phase shifted: mask = hard stop at half of a 2-tile period.
                        float u = horizontal ? (q.y - tileXY.y) / (2 * tileWH.y) : (q.x - tileXY.x) / (2 * tileWH.x);
                        if (u - math.floor(u) >= 0.5f) q -= shift;
                    }
                    float2 l = (q - tileXY) / tileWH * box;
                    l = new float2(Wrap(l.x, box.x, mirror), Wrap(l.y, box.y, mirror));
                    row[x] = PackPremul(Sample(src, op.EffectToDevice.Apply(l)));
                }
            }
        }

        /// <summary>Skottie CornerPin: projective transform of the content (layer box onto the four points).</summary>
        public static void CornerPin(in EffectOp op, in Target dst, in Target src, float* inv)
        {
            float3x3 h = new float3x3(inv[0], inv[1], inv[2], inv[3], inv[4], inv[5], inv[6], inv[7], inv[8]);
            for (int y = 0; y < dst.H; y++)
            {
                uint* row = dst.Px + y * dst.W;
                for (int x = 0; x < dst.W; x++)
                {
                    float2 q = op.DeviceToEffect.Apply(dst.Center(x, y));
                    float3 s = math.mul(h, new float3(q, 1));
                    if (!(s.z > 1e-9f)) { row[x] = 0; continue; }
                    row[x] = PackPremul(Sample(src, op.EffectToDevice.Apply(s.xy / s.z)));
                }
            }
        }

        /// <summary>Skottie Bulge: spherical + exponential radial displacement inside an ellipse.</summary>
        public static void Bulge(in EffectOp op, in Target dst, in Target src)
        {
            float2 c = op.A.xy, r = op.A.zw, rInv = 1f / r, box = op.LayerSize;
            for (int y = 0; y < dst.H; y++)
            {
                uint* row = dst.Px + y * dst.W;
                for (int x = 0; x < dst.W; x++)
                {
                    float2 q = op.DeviceToEffect.Apply(dst.Center(x, y));
                    float2 v = (q - c) * rInv;
                    float tt = math.dot(v, v);
                    if (tt < 1 && tt > 1e-12f)
                    {
                        float len = math.sqrt(tt);
                        float arc = math.asin(math.min(len * op.Y, 1f)) * op.Z;
                        float2 dSph = v / len * arc - v;
                        float2 dExp = v * math.pow(tt, op.X) - v;
                        v += (dSph + dExp) * op.W;
                    }
                    float2 s = v * r + c;
                    // The content is sampled as a repeating picture of the layer box.
                    s = new float2(Wrap(s.x, box.x, false), Wrap(s.y, box.y, false));
                    row[x] = PackPremul(Sample(src, op.EffectToDevice.Apply(s)));
                }
            }
        }

        /// <summary>Skottie CC Sphere: the layer box wrapped on a rotated, lit sphere (inside, outside or both sides).</summary>
        public static void Sphere(in EffectOp op, in Target dst, in Target src, float* d)
        {
            float2 center = op.A.xy, box = op.LayerSize;
            float radius = op.A.z;
            float3 lightVec = new float3(d[9], d[10], d[11]), lightColor = new float3(d[12], d[13], d[14]);
            float ambient = d[15], diffuse = d[16], specular = d[17], specExp = d[18];
            bool fancy = math.length(lightVec) > 0 && (diffuse > 0 || specular > 0);
            int side = op.Flags;
            for (int y = 0; y < dst.H; y++)
            {
                uint* row = dst.Px + y * dst.W;
                for (int x = 0; x < dst.W; x++)
                {
                    float2 q = op.DeviceToEffect.Apply(dst.Center(x, y));
                    float2 xy = (q - center) / radius;
                    // Anti-aliased circle edge (drawCircle with AA).
                    float edge = math.saturate((1 - math.length(xy)) * radius * math.length(op.EffectToDevice.ApplyVector(new float2(1, 0))) + 0.5f);
                    if (edge <= 0) { row[x] = 0; continue; }
                    float4 c = float4.zero;
                    if (side != 1) c = SphereSide(xy, 1, d, lightVec, lightColor, ambient, diffuse, specular, specExp, fancy, box, op, src);
                    if (side != 2)
                    {
                        float4 front = SphereSide(xy, -1, d, lightVec, lightColor, ambient, diffuse, specular, specExp, fancy, box, op, src);
                        c = front + c * (1 - front.w);
                    }
                    row[x] = PackPremul(c * edge);
                }
            }
        }

        static float4 SphereSide(float2 xy, float sel, float* rot, float3 lightVec, float3 lightColor, float ambient, float diffuse,
            float specular, float specExp, bool fancy, float2 box, in EffectOp op, in Target src)
        {
            float3 eye = new float3(xy, -5.5f);
            float ez2 = eye.z * eye.z;
            float a = math.dot(eye, eye), b = -2 * ez2, cc = ez2 - 1;
            float disc = math.max(b * b - 4 * a * cc, 0f);
            float tt = (-b + sel * math.sqrt(disc)) / (2 * a);
            float3 n = new float3(0, 0, -eye.z) + eye * tt;
            // SkSL half3x3 from row-major values: rn = transpose(rot) * n.
            float3 rn = new float3(rot[0] * n.x + rot[3] * n.y + rot[6] * n.z,
                                   rot[1] * n.x + rot[4] * n.y + rot[7] * n.z,
                                   rot[2] * n.x + rot[5] * n.y + rot[8] * n.z);
            const float RPi = 1f / 3.1415927f;
            float2 uv = new float2(0.5f + RPi * 0.5f * math.atan2(rn.x, rn.z), 0.5f + RPi * math.asin(math.clamp(rn.y, -1f, 1f)));
            float2 s = uv * box;
            s = new float2(Wrap(s.x, box.x, false), Wrap(s.y, box.y, false));
            float4 c = Sample(src, op.EffectToDevice.Apply(s));
            if (!fancy)
            {
                c.xyz *= ambient;
                return c;
            }
            float3 l = lightVec * -sel;
            float3 lr = math.reflect(-l * sel, n);
            float sBase = math.max(math.dot(math.normalize(eye), lr), 0);
            float dd = diffuse * math.max(math.dot(l, n), 0);
            float ss = specular * math.saturate(math.pow(sBase, specExp));
            c.xyz = (ambient + dd * lightColor) * c.xyz + ss * lightColor * c.w;
            return c;
        }

        /// <summary>Skottie DirectionalBlur: a 1D gaussian along a direction (device space).</summary>
        public static void DirectionalBlur(in EffectOp op, in Target dst, in Target src)
        {
            float sigma = op.X;
            int radius = math.min((int)math.ceil(sigma * 3), 256);
            if (radius < 1) return;
            float* k = stackalloc float[2 * 256 + 1];
            float sum = 0;
            for (int i = -radius; i <= radius; i++)
            {
                float v = math.exp(-(i * i) / (2 * sigma * sigma));
                k[i + radius] = v;
                sum += v;
            }
            float2 dir = op.A.xy;
            for (int y = 0; y < dst.H; y++)
            {
                uint* row = dst.Px + y * dst.W;
                for (int x = 0; x < dst.W; x++)
                {
                    float2 p = dst.Center(x, y);
                    float4 acc = float4.zero;
                    for (int i = -radius; i <= radius; i++) acc += Sample(src, p + dir * i) * k[i + radius];
                    row[x] = PackPremul(acc / sum);
                }
            }
        }

        /// <summary>Skottie Sharpen: 3x3 convolution on premultiplied colours, repeating edges.</summary>
        public static void Sharpen(in EffectOp op, in Target dst, in Target src)
        {
            float center = op.X, around = op.Y;
            for (int y = 0; y < dst.H; y++)
            {
                uint* row = dst.Px + y * dst.W;
                for (int x = 0; x < dst.W; x++)
                {
                    float4 acc = float4.zero;
                    for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        int sx = x + i, sy = y + j;
                        sx = sx < 0 ? src.W - 1 : sx >= src.W ? 0 : sx;
                        sy = sy < 0 ? src.H - 1 : sy >= src.H ? 0 : sy;
                        uint p = src.Px[sy * src.W + sx];
                        if (p == 0) continue;
                        acc += Blend.Unpack(p) * (i == 0 && j == 0 ? center : around);
                    }
                    row[x] = PackPremul(acc);
                }
            }
        }

        // ------------------------------------------------------------------ layer styles

        /// <summary>
        /// Drop shadow (Flags 0: blurred, offset alpha below the source) or inner shadow (Flags 1: blurred, offset inverse
        /// alpha masked by the source, above it). <paramref name="tmp"/>: 2 w×h + 2 max(w, h) + 2 pixels.
        /// </summary>
        public static void StyleShadow(in EffectOp op, in Target t, uint* tmp)
        {
            int n = t.Count;
            uint* blur = tmp;
            for (int i = 0; i < n; i++) blur[i] = (t.Px[i] >> 24) * 0x01010101u;
            BlurRGBA(blur, t.W, t.H, op.Z, op.W, tmp + n);
            bool inner = (op.Flags & 1) != 0;
            float4 col = new float4(op.A.xyz * op.A.w, op.A.w);
            for (int y = 0; y < t.H; y++)
            {
                uint* row = t.Px + y * t.W;
                for (int x = 0; x < t.W; x++)
                {
                    // Shadow alpha at (x - dx, y - dy), bilinear; outside the surface the source is transparent.
                    float a = SampleAlpha(blur, t.W, t.H, x - op.X, y - op.Y, 0);
                    uint src = row[x];
                    if (inner)
                    {
                        if (src == 0) continue;
                        float s = (1 - a) * (src >> 24) * Blend.Inv255;
                        uint sh = Px8.Pack(col * s);
                        row[x] = Px8.Over(sh, src);
                    }
                    else
                    {
                        uint sh = a > 0 ? Px8.Pack(col * a) : 0;
                        row[x] = Px8.Over(src, sh);
                    }
                }
            }
        }

        /// <summary>
        /// Glows (Flags bit 0 inner, bit 1 inner from the centre) and the stroke style (bit 2: dilated alpha below the
        /// source). <paramref name="tmp"/>: 2 w×h + 2 max(w, h) + 2 pixels.
        /// </summary>
        public static void StyleGlow(in EffectOp op, in Target t, uint* tmp)
        {
            int n = t.Count;
            bool inner = (op.Flags & 1) != 0, center = (op.Flags & 2) != 0, dilate = (op.Flags & 4) != 0;
            float* a = (float*)tmp;
            if (dilate) Dilate(t, (op.Z + op.W) * 0.5f, a, (float*)(tmp + n));
            else
            {
                uint* blur = tmp;
                for (int i = 0; i < n; i++) blur[i] = (t.Px[i] >> 24) * 0x01010101u;
                if (op.Z > 0 || op.W > 0) BlurRGBA(blur, t.W, t.H, op.Z, op.W, tmp + n);
                for (int i = 0; i < n; i++)
                {
                    float v = (blur[i] >> 24) * Blend.Inv255;
                    if (inner && !center) v = 1 - v;
                    if (op.X > 0) v = math.min(v * op.X, 1f);
                    a[i] = v;
                }
            }
            float4 col = new float4(op.A.xyz * op.A.w, op.A.w);
            for (int i = 0; i < n; i++)
            {
                uint src = t.Px[i];
                if (inner)
                {
                    if (src == 0) continue;
                    uint g = Px8.Pack(col * (a[i] * (src >> 24) * Blend.Inv255));
                    t.Px[i] = Px8.Over(g, src);
                }
                else if (a[i] > 0) t.Px[i] = Px8.Over(src, Px8.Pack(col * a[i]));
            }
        }

        static float SampleAlpha(uint* buf, int w, int h, float x, float y, float outside)
        {
            float fx = math.floor(x), fy = math.floor(y);
            int x0 = (int)fx, y0 = (int)fy;
            float tx = x - fx, ty = y - fy;
            float a00 = AlphaAt(buf, w, h, x0, y0, outside), a10 = AlphaAt(buf, w, h, x0 + 1, y0, outside);
            float a01 = AlphaAt(buf, w, h, x0, y0 + 1, outside), a11 = AlphaAt(buf, w, h, x0 + 1, y0 + 1, outside);
            return math.lerp(math.lerp(a00, a10, tx), math.lerp(a01, a11, tx), ty);
        }

        static float AlphaAt(uint* buf, int w, int h, int x, int y, float outside)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return outside;
            return (buf[y * w + x] >> 24) * Blend.Inv255;
        }

        /// <summary>
        /// Outside stroke coverage: Euclidean distance to the shape (alpha >= 0.5), converted to an anti-aliased
        /// ring of <paramref name="radius"/> pixels (Felzenszwalb squared distance transform).
        /// </summary>
        static void Dilate(in Target t, float radius, float* outA, float* scratch)
        {
            int w = t.W, h = t.H, n = w * h;
            const float Inf = 1e20f;
            float* dist = outA;
            for (int i = 0; i < n; i++) dist[i] = (t.Px[i] >> 24) >= 128 ? 0 : Inf;
            int line = math.max(w, h);
            float* f = scratch;
            float* dd = scratch + line;
            int* v = (int*)(scratch + 2 * line);
            float* z = scratch + 3 * line;
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) f[y] = dist[y * w + x];
                Edt1D(f, h, dd, v, z);
                for (int y = 0; y < h; y++) dist[y * w + x] = dd[y];
            }
            for (int y = 0; y < h; y++)
            {
                float* rowp = dist + y * w;
                for (int x = 0; x < w; x++) f[x] = rowp[x];
                Edt1D(f, w, dd, v, z);
                for (int x = 0; x < w; x++) rowp[x] = dd[x];
            }
            for (int i = 0; i < n; i++)
            {
                float a = math.saturate(radius + 0.5f - math.sqrt(dist[i]));
                outA[i] = math.max(a, (t.Px[i] >> 24) * Blend.Inv255);
            }
        }

        static void Edt1D(float* f, int n, float* d, int* v, float* z)
        {
            int k = 0;
            v[0] = 0;
            z[0] = -1e20f;
            z[1] = 1e20f;
            for (int q = 1; q < n; q++)
            {
                float s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = 1e20f;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                float dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }
    }
}
