using Mynell.Lottie.Eval;
using Unity.Collections.LowLevel.Unsafe;
using Mynell.Lottie.Model;
using Unity.Mathematics;

namespace Mynell.Lottie.Scene
{
    // Layer effects and layer styles: resolves effect parameters for the current frame into device-space
    // EffectOps. lottie-web's effects follow its SVG filters; the others follow After Effects as Skottie renders them.
    public unsafe partial struct SceneBuilder
    {
        bool HasRenderableEffects(in LayerData L)
        {
            for (int i = 0; i < L.EffectCount; i++)
            {
                var e = Effects[L.EffectFirst + i];
                if (e.Enabled == 0) continue;
                if (IsPixelEffect(e.Kind)) return true;
            }
            return false;
        }

        static bool IsPixelEffect(EffectKind k)
        {
            switch (k)
            {
                case EffectKind.GaussianBlur:
                case EffectKind.DropShadow:
                case EffectKind.Fill:
                case EffectKind.Tint:
                case EffectKind.Tritone:
                case EffectKind.ProLevels:
                case EffectKind.RadialWipe:
                case EffectKind.GradientRamp:
                case EffectKind.HueSaturation:
                case EffectKind.BrightnessContrast:
                case EffectKind.Invert:
                case EffectKind.Threshold:
                case EffectKind.BlackAndWhite:
                case EffectKind.CCToner:
                case EffectKind.ShiftChannels:
                case EffectKind.EasyLevels:
                case EffectKind.LinearWipe:
                case EffectKind.VenetianBlinds:
                case EffectKind.MotionTile:
                case EffectKind.CornerPin:
                case EffectKind.Bulge:
                case EffectKind.DirectionalBlur:
                case EffectKind.Sharpen:
                case EffectKind.FractalNoise:
                case EffectKind.Sphere:
                case EffectKind.StyleStroke:
                case EffectKind.StyleDropShadow:
                case EffectKind.StyleInnerShadow:
                case EffectKind.StyleOuterGlow:
                case EffectKind.StyleInnerGlow:
                case EffectKind.StyleColorOverlay:
                    return true;
            }
            return false;
        }

        float EffectScalar(in EffectData e, int param, float t, float def) =>
            param < e.ParamCount ? P.Scalar(EffectParams[e.ParamFirst + param].Prop, t, def) : def;

        float4 EffectColor(in EffectData e, int param, float t, float4 def) =>
            param < e.ParamCount ? P.Vec4(EffectParams[e.ParamFirst + param].Prop, t, def) : def;

        float2 EffectPoint(in EffectData e, int param, float t, float2 def) =>
            param < e.ParamCount ? P.Vec2(EffectParams[e.ParamFirst + param].Prop, t, def) : def;

        bool HasEffectParam(in EffectData e, int param) =>
            param < e.ParamCount && EffectParams[e.ParamFirst + param].Prop >= 0;

        /// <summary>Layer types whose effects work in layer space (the others: in composition space, After Effects).</summary>
        static bool EffectsInLayerSpace(LayerKind k) => k == LayerKind.Precomp || k == LayerKind.Solid || k == LayerKind.Image;

        /// <summary>Effect space -> device pixels.</summary>
        static Affine2 EffectSpace(in LayerData L, in Affine2 world, in Affine2 compToDevice) =>
            EffectsInLayerSpace(L.Kind) ? world : compToDevice;

        bool HasNonLocalEffects(in LayerData L)
        {
            for (int i = 0; i < L.EffectCount; i++)
            {
                var e = Effects[L.EffectFirst + i];
                if (e.Enabled == 0) continue;
                if (e.Kind == EffectKind.MotionTile || e.Kind == EffectKind.CornerPin || e.Kind == EffectKind.Bulge ||
                    e.Kind == EffectKind.Sphere || e.Kind == EffectKind.DisplacementMap) return true;
            }
            return false;
        }

        /// <summary>
        /// Combined matrix and opacity of the layer's Transform effects. lottie-web (effect 'ty' 35) applies them before
        /// the layer transform; the After Effects transform effect found by match name does so only for layers whose
        /// effects work in layer space, otherwise after the layer transform (<paramref name="compSpace"/>).
        /// </summary>
        bool TransformEffects(in LayerData L, float t, bool compSpace, out Affine2 matrix, out float opacity)
        {
            matrix = Affine2.Identity;
            opacity = 1f;
            bool any = false;
            for (int i = 0; i < L.EffectCount; i++)
            {
                var e = Effects[L.EffectFirst + i];
                if (e.Enabled == 0 || e.Kind != EffectKind.Transform) continue;
                bool inCompSpace = (e.Flags & EffectFlags.ByMatchName) != 0 && !EffectsInLayerSpace(L.Kind);
                if (inCompSpace != compSpace) continue;
                float2 anchor = EffectPoint(e, 0, t, float2.zero);
                float2 position = EffectPoint(e, 1, t, float2.zero);
                bool uniform = EffectScalar(e, 2, t, 1) == 1;
                float scaleH = EffectScalar(e, 3, t, 100);
                float scaleW = uniform ? scaleH : EffectScalar(e, 4, t, 100);
                float skew = EffectScalar(e, 5, t, 0);
                float skewAxis = EffectScalar(e, 6, t, 0);
                float rotation = EffectScalar(e, 7, t, 0);
                Affine2 m = Affine2.Translate(-anchor.x, -anchor.y);
                m = m.Then(Affine2.Scale(scaleW * 0.01f, scaleH * 0.01f));
                if (rotation != 0) m = m.Then(Affine2.Rotate(-math.radians(rotation)));
                if (skew != 0) m = m.Then(Affine2.SkewFromAxis(-math.radians(skew), math.radians(skewAxis + 90)));
                m = m.Then(Affine2.Translate(position.x, position.y));
                // lottie-web: localTransforms[0] * localTransforms[1] * ... * layer matrix.
                matrix = any ? matrix.Then(m) : m;
                opacity *= EffectScalar(e, 8, t, 100) * 0.01f;
                any = true;
            }
            return any;
        }

        /// <summary>Size of the layer box: precomp / solid / image size, the composition size otherwise (Skottie).</summary>
        float2 LayerBoxSize(in LayerData L)
        {
            switch (L.Kind)
            {
                case LayerKind.Precomp:
                case LayerKind.Solid:
                    if (L.Width > 0 && L.Height > 0) return new float2(L.Width, L.Height);
                    break;
                case LayerKind.Image:
                    if (L.Image >= 0) return new float2(Images[L.Image].Width, Images[L.Image].Height);
                    break;
            }
            CompData c = Comps[L.Comp];
            return new float2(c.Width, c.Height);
        }

        EffectOp NewOp(EffectOpKind kind, in Affine2 effectToDevice, float2 layerSize)
        {
            return new EffectOp
            {
                Kind = kind,
                PostMask = 1,
                TableR = -1, TableG = -1, TableB = -1, TableA = -1, TableRGB = -1,
                Data = -1,
                EffectToDevice = effectToDevice,
                DeviceToEffect = effectToDevice.Inverse(),
                LayerSize = layerSize,
                OutputRect = EmptyRect,
            };
        }

        /// <summary>Resolves the layer's pixel effects and styles into <see cref="FrameData.Effects"/> (device units).</summary>
        void EmitEffects(in LayerData L, float t, in Affine2 world, in Affine2 compToDevice)
        {
            float sx = math.length(new float2(world.a, world.b));
            float sy = math.length(new float2(world.c, world.d));
            // After Effects applies the effects of precomp / solid / image layers in layer space, and those of the
            // other layers in composition space (after the layer transform). Layer styles are always in composition space.
            Affine2 fx = EffectSpace(L, world, compToDevice);
            float2 box = LayerBoxSize(L);
            float fxs = math.sqrt(math.abs(fx.Determinant));
            float2 cs = new float2(math.length(new float2(compToDevice.a, compToDevice.b)), math.length(new float2(compToDevice.c, compToDevice.d)));
            for (int i = 0; i < L.EffectCount; i++)
            {
                var e = Effects[L.EffectFirst + i];
                if (e.Enabled == 0) continue;
                var op = NewOp(EffectOpKind.Fill, fx, box);
                switch (e.Kind)
                {
                    // ------------------------------------------------ lottie-web SVG filters
                    case EffectKind.Fill:
                    {
                        op.Kind = EffectOpKind.Fill;
                        op.PostMask = 0;
                        float4 c = EffectColor(e, 2, t, new float4(1, 0, 0, 1));
                        op.A = new float4(c.xyz, EffectScalar(e, 6, t, 1));
                        break;
                    }
                    case EffectKind.Tint:
                        op.Kind = EffectOpKind.Tint;
                        op.PostMask = 0;
                        op.A = EffectColor(e, 0, t, new float4(0, 0, 0, 1));
                        op.B = EffectColor(e, 1, t, new float4(1, 1, 1, 1));
                        op.X = EffectScalar(e, 2, t, 100) * 0.01f;
                        break;
                    case EffectKind.Tritone:
                        op.Kind = EffectOpKind.Tritone;
                        op.PostMask = 0;
                        op.C = EffectColor(e, 0, t, new float4(1, 1, 1, 1));   // highlights
                        op.B = EffectColor(e, 1, t, new float4(0.5f, 0.5f, 0.5f, 1)); // midtones
                        op.A = EffectColor(e, 2, t, new float4(0, 0, 0, 1));   // shadows
                        op.X = EffectScalar(e, 3, t, 0) * 0.01f;
                        break;
                    case EffectKind.ProLevels:
                        op.Kind = EffectOpKind.Levels;
                        op.PostMask = 0;
                        op.TableR = LevelsTable(e, 10, t);
                        op.TableG = LevelsTable(e, 17, t);
                        op.TableB = LevelsTable(e, 24, t);
                        op.TableA = LevelsTable(e, 31, t);
                        op.TableRGB = LevelsTable(e, 3, t);
                        if (op.TableR < 0 && op.TableG < 0 && op.TableB < 0 && op.TableA < 0 && op.TableRGB < 0) continue;
                        break;
                    case EffectKind.GaussianBlur:
                    {
                        op.Kind = EffectOpKind.GaussianBlur;
                        op.PostMask = 0;
                        float sigma = EffectScalar(e, 0, t, 0) * 0.3f; // lottie-web kBlurrinessToSigma
                        int dims = (int)EffectScalar(e, 1, t, 1);
                        op.Z = dims == 3 ? 0 : sigma * sx;
                        op.W = dims == 2 ? 0 : sigma * sy;
                        op.Flags = (byte)(EffectScalar(e, 2, t, 0) == 1 ? 1 : 0);
                        if (!(op.Z > 0.01f) && !(op.W > 0.01f)) continue;
                        break;
                    }
                    case EffectKind.DropShadow:
                    {
                        op.Kind = EffectOpKind.DropShadow;
                        op.PostMask = 0;
                        float4 c = EffectColor(e, 0, t, new float4(0, 0, 0, 1));
                        // lottie-web: flood colour rounded to bytes, opacity in 0..255.
                        c.xyz = math.round(math.saturate(c.xyz) * 255f) / 255f;
                        op.A = new float4(c.xyz, math.saturate(EffectScalar(e, 1, t, 127.5f) / 255f));
                        float distance = EffectScalar(e, 3, t, 5);
                        float angle = math.radians(EffectScalar(e, 2, t, 135) - 90);
                        float2 offset = world.ApplyVector(new float2(distance * math.cos(angle), distance * math.sin(angle)));
                        op.X = offset.x;
                        op.Y = offset.y;
                        float sigma = EffectScalar(e, 4, t, 0) / 4f;
                        op.Z = sigma * sx;
                        op.W = sigma * sy;
                        op.Flags = (byte)(EffectScalar(e, 5, t, 0) == 1 ? 1 : 0);
                        break;
                    }

                    // ------------------------------------------------ colour corrections (Skottie)
                    case EffectKind.HueSaturation:
                    {
                        if ((int)math.round(EffectScalar(e, 0, t, 1)) != 1) continue; // master channel only
                        float hue = EffectScalar(e, 2, t, 0), sat = EffectScalar(e, 3, t, 0), light = EffectScalar(e, 4, t, 0);
                        // Skottie composes lightness -> saturation -> hue.
                        if (math.abs(light) > 1e-6f)
                        {
                            float l = math.clamp(light / 100f, -1f, 1f), ls = 1 - math.abs(l), lo = l < 0 ? 0 : 1 - ls;
                            op.Kind = EffectOpKind.ColorMatrix;
                            op.Data = AddColorMatrix(ls, 0, 0, 0, lo, 0, ls, 0, 0, lo, 0, 0, ls, 0, lo, 0, 0, 0, 1, 0);
                            Out.Effects.Add(op);
                        }
                        if (math.abs(sat) > 1e-6f)
                        {
                            float s = math.clamp(sat / 100f, -1f, 1f);
                            op.Kind = EffectOpKind.Saturate;
                            op.X = s < 0 ? s + 1 : math.min(1 / (1 - s), 126f);
                            Out.Effects.Add(op);
                        }
                        if (math.abs(hue) > 1e-6f)
                        {
                            op.Kind = EffectOpKind.HslMatrix;
                            op.Data = AddColorMatrix(1, 0, 0, 0, hue / 360f, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0);
                            Out.Effects.Add(op);
                        }
                        continue;
                    }
                    case EffectKind.BrightnessContrast:
                    {
                        float bright = EffectScalar(e, 0, t, 0), contrast = EffectScalar(e, 1, t, 0);
                        if ((int)math.round(EffectScalar(e, 2, t, 0)) != 0)
                        {
                            // Legacy mode: component-wise scale + offset.
                            float b = math.clamp(bright, -100f, 100f) / 255f, c = math.clamp(contrast, -100f, 100f) / 100f;
                            float S = c > 0 ? 1 / math.max(1 - c, 1f / 4096f) : 1 + c;
                            float B = 0.5f * (1 - S) + b * math.max(S, 1f);
                            op.Kind = EffectOpKind.ColorMatrix;
                            op.Data = AddColorMatrix(S, 0, 0, 0, B, 0, S, 0, 0, B, 0, 0, S, 0, B, 0, 0, 0, 1, 0);
                            break;
                        }
                        float bn = math.clamp(bright, -150f, 150f) / 150f, cn = math.clamp(contrast, -50f, 100f) / 100f;
                        if (math.abs(bn) > 1e-6f)
                        {
                            op.Kind = EffectOpKind.Brightness;
                            op.X = math.pow(2f, bn * 1.8f);
                            Out.Effects.Add(op);
                        }
                        if (math.abs(contrast) > 1e-6f)
                        {
                            op.Kind = EffectOpKind.Contrast;
                            float cb = math.PI * cn;
                            op.X = -2 * cb / 3; // a
                            op.Y = cb;          // b
                            op.Z = 1 - cb / 3;  // c
                            Out.Effects.Add(op);
                        }
                        continue;
                    }
                    case EffectKind.Invert:
                        if (!EmitInvert(ref op, (int)EffectScalar(e, 0, t, 0))) continue;
                        break;
                    case EffectKind.Threshold:
                        op.Kind = EffectOpKind.Threshold;
                        op.X = EffectScalar(e, 0, t, 0);
                        break;
                    case EffectKind.BlackAndWhite:
                        op.Kind = EffectOpKind.BlackAndWhite;
                        op.A = new float4(EffectScalar(e, 0, t, 0), EffectScalar(e, 1, t, 0), EffectScalar(e, 2, t, 0), EffectScalar(e, 3, t, 0)) / 100f;
                        op.B = new float4(EffectScalar(e, 4, t, 0), EffectScalar(e, 5, t, 0), 0, 0) / 100f;
                        break;
                    case EffectKind.CCToner:
                        if (!EmitToner(ref op, e, t)) continue;
                        break;
                    case EffectKind.ShiftChannels:
                        EmitShiftChannels(ref op, e, t);
                        break;
                    case EffectKind.EasyLevels:
                        if (!EmitEasyLevels(ref op, e, t)) continue;
                        break;

                    // ------------------------------------------------ generators / transitions (Skottie)
                    case EffectKind.GradientRamp:
                    {
                        op.Kind = EffectOpKind.GradientRamp;
                        op.A = new float4(EffectPoint(e, 0, t, float2.zero), EffectPoint(e, 2, t, float2.zero));
                        op.B = math.saturate(EffectColor(e, 1, t, new float4(0, 0, 0, 1)));
                        op.C = math.saturate(EffectColor(e, 3, t, new float4(1, 1, 1, 1)));
                        op.Flags = (byte)((int)math.round(EffectScalar(e, 4, t, 1)) == 1 ? 0 : 1); // 1 = radial
                        break;
                    }
                    case EffectKind.LinearWipe:
                    {
                        float completion = EffectScalar(e, 0, t, 0);
                        if (completion <= 0) continue;
                        op.Kind = EffectOpKind.LinearMask;
                        if (completion >= 100) { op.Flags = 1; break; } // fully hidden
                        float tt = math.saturate(completion * 0.01f), feather = math.max(EffectScalar(e, 2, t, 0), 0);
                        float angle = math.radians(90 - EffectScalar(e, 1, t, 0));
                        float2 av = new float2(math.cos(angle), math.sin(angle));
                        float2 diag = new float2(Sign(av.x) * box.x, Sign(av.y) * box.y);
                        float len = math.dot(diag, av), gradLen = len + feather * 2;
                        float2 gv = new float2(av.x, -av.y) * gradLen, center = box * 0.5f;
                        op.A = new float4(center - gv * 0.5f, center + gv * 0.5f);
                        float at = tt * (len + feather) / gradLen;
                        // alpha 0 below X, 1 above Y (linear in between)
                        op.X = at;
                        op.Y = at + feather / gradLen;
                        break;
                    }
                    case EffectKind.VenetianBlinds:
                    {
                        float completion = EffectScalar(e, 0, t, 0);
                        if (completion <= 0) continue;
                        op.Kind = EffectOpKind.LinearMask;
                        if (completion >= 100) { op.Flags = 1; break; }
                        float tt = completion * 0.01f, size = math.max(1f, EffectScalar(e, 2, t, 0));
                        float angle = math.radians(-EffectScalar(e, 1, t, 0));
                        float feather = math.max(EffectScalar(e, 3, t, 0) * 3f, 0.5f), df = feather / size;
                        float df0 = 0.5f * math.min(df, tt), df1 = 0.5f * math.min(df, 1 - tt);
                        float g01 = math.max(0f, 0.5f * (1 + (0 - tt) / df)), g23 = math.min(1f, 0.5f * (1 + (1 - tt) / df));
                        float2 center = box * 0.5f, gv = new float2(size * math.cos(angle), -size * math.sin(angle));
                        op.A = new float4(center + gv * df0, center + gv * (df0 + 1));
                        op.Flags = 2; // repeating, piecewise linear stops
                        op.B = new float4(tt - df0 - df0, tt + df1 - df0, 1 - df1 - df0, 1);
                        op.C = new float4(g01, g23, g23, g01);
                        break;
                    }
                    case EffectKind.RadialWipe:
                    {
                        float completion = EffectScalar(e, 0, t, 0);
                        if (completion <= 0) continue;
                        op.Kind = EffectOpKind.SweepMask;
                        if (completion >= 100) { op.Flags = 1; break; }
                        float tt = completion * 0.01f;
                        int wipe = (int)math.round(EffectScalar(e, 3, t, 0));
                        float align = wipe == 2 ? -360f : wipe == 3 ? -180f : 0f;
                        float a0 = SanitizeAngle(EffectScalar(e, 1, t, 0) - 90 + tt * align), a1 = SanitizeAngle(a0 + tt * 360);
                        op.A.xy = EffectPoint(e, 2, t, float2.zero);
                        // Inside [a0, a1] the layer is hidden; swapped ranges show the inside instead.
                        op.Flags = (byte)(a0 > a1 ? 2 : 0);
                        op.X = math.min(a0, a1);
                        op.Y = math.max(a0, a1);
                        break;
                    }
                    case EffectKind.FractalNoise:
                        EmitFractalNoise(ref op, e, t);
                        break;

                    // ------------------------------------------------ distortions (Skottie)
                    case EffectKind.MotionTile:
                    {
                        op.Kind = EffectOpKind.MotionTile;
                        float tw = EffectScalar(e, 1, t, 100), th = EffectScalar(e, 2, t, 100);
                        if (tw <= 0 && th <= 0) { op.Kind = EffectOpKind.LinearMask; op.Flags = 1; break; }
                        float2 tile = new float2(math.max(math.clamp(tw, 0, 100) * 0.01f * box.x, 1f), math.max(math.clamp(th, 0, 100) * 0.01f * box.y, 1f));
                        float2 tc = EffectPoint(e, 0, t, float2.zero);
                        op.A = new float4(tc - tile * 0.5f, tile);
                        float2 output = new float2(EffectScalar(e, 3, t, 100), EffectScalar(e, 4, t, 100)) * 0.01f * box;
                        op.B = new float4((box - output) * 0.5f, (box + output) * 0.5f);
                        op.Flags = (byte)(EffectScalar(e, 5, t, 0) != 0 ? 1 : 0);
                        float phase = EffectScalar(e, 6, t, 0);
                        op.X = phase == 0 ? 0 : phase / 360f - math.trunc(phase / 360f);
                        op.Y = EffectScalar(e, 7, t, 0) != 0 ? 1 : 0;
                        op.OutputRect = TransformRectBounds(fx, op.B);
                        break;
                    }
                    case EffectKind.CornerPin:
                    {
                        op.Kind = EffectOpKind.CornerPin;
                        float2 ul = EffectPoint(e, 0, t, float2.zero), ur = EffectPoint(e, 1, t, new float2(box.x, 0));
                        float2 ll = EffectPoint(e, 2, t, new float2(0, box.y)), lr = EffectPoint(e, 3, t, box);
                        if (!Homography.RectToQuad(box, ul, ur, lr, ll, out float3x3 h)) continue;
                        op.Data = AddMatrix3(math.inverse(h));
                        float2 mn = math.min(math.min(ul, ur), math.min(ll, lr)), mx = math.max(math.max(ul, ur), math.max(ll, lr));
                        op.OutputRect = TransformRectBounds(fx, new float4(mn, mx));
                        break;
                    }
                    case EffectKind.Bulge:
                    {
                        float height = EffectScalar(e, 3, t, 0);
                        if (height == 0) continue;
                        op.Kind = EffectOpKind.Bulge;
                        float adj = math.abs(height) / 4f;
                        float r = (1f + adj) / 2f / math.sqrt(adj);
                        op.A = new float4(EffectPoint(e, 2, t, float2.zero), EffectScalar(e, 0, t, 0), EffectScalar(e, 1, t, 0));
                        op.X = math.pow(adj, 3) * 1.3f;            // h
                        op.Y = 1f / r;                              // 1 / R
                        op.Z = 1f / math.asin(1 / r);               // 1 / asin(1 / R)
                        op.W = height > 0 ? 1 : -1;                 // selector
                        if (op.A.z == 0 || op.A.w == 0) continue;
                        break;
                    }
                    case EffectKind.DirectionalBlur:
                    {
                        float length = EffectScalar(e, 1, t, 0);
                        if (!(length > 0)) continue;
                        op.Kind = EffectOpKind.DirectionalBlur;
                        float rot = math.radians(EffectScalar(e, 0, t, 0) - 90);
                        float2 dir = fx.ApplyVector(new float2(math.cos(rot), math.sin(rot)));
                        float dl = math.length(dir);
                        if (!(dl > 1e-6f)) continue;
                        op.A.xy = dir / dl;
                        op.X = length * 0.3f * dl; // sigma along the direction, device pixels
                        break;
                    }
                    case EffectKind.Sharpen:
                    {
                        float amount = EffectScalar(e, 0, t, 0);
                        if (amount == 0) continue;
                        op.Kind = EffectOpKind.Sharpen;
                        op.X = 1 + amount * 0.01f;
                        op.Y = (1 - op.X) / 8f;
                        break;
                    }
                    case EffectKind.Sphere:
                        if (!EmitSphere(ref op, e, t)) continue;
                        op.OutputRect = TransformRectBounds(fx, new float4(op.A.xy - op.A.z, op.A.xy + op.A.z));
                        break;

                    // ------------------------------------------------ layer styles (Skottie)
                    case EffectKind.StyleDropShadow:
                    case EffectKind.StyleInnerShadow:
                    {
                        op = NewOp(EffectOpKind.StyleShadow, compToDevice, box);
                        op.Flags = (byte)(e.Kind == EffectKind.StyleInnerShadow ? 1 : 0);
                        float4 c = math.saturate(EffectColor(e, 0, t, new float4(0, 0, 0, 1)));
                        float opacity = math.saturate(EffectScalar(e, 1, t, 100) / 100f);
                        op.A = new float4(c.xyz, opacity * c.w);
                        float rad = math.radians(180 + EffectScalar(e, 2, t, 0)), distance = EffectScalar(e, 4, t, 0);
                        float2 offset = compToDevice.ApplyVector(new float2(distance * math.cos(rad), -distance * math.sin(rad)));
                        op.X = offset.x;
                        op.Y = offset.y;
                        float sigma = EffectScalar(e, 3, t, 0) * 0.3f;
                        op.Z = sigma * cs.x;
                        op.W = sigma * cs.y;
                        break;
                    }
                    case EffectKind.StyleOuterGlow:
                    case EffectKind.StyleInnerGlow:
                    {
                        op = NewOp(EffectOpKind.StyleGlow, compToDevice, box);
                        bool inner = e.Kind == EffectKind.StyleInnerGlow;
                        float4 c = math.saturate(EffectColor(e, 0, t, new float4(1, 1, 1, 1)));
                        float opacity = math.saturate(EffectScalar(e, 1, t, 100) / 100f);
                        op.A = new float4(c.xyz, opacity * c.w);
                        float sigma = EffectScalar(e, 3, t, 0) * 0.3f;
                        op.Z = sigma * cs.x;
                        op.W = sigma * cs.y;
                        float choke = math.saturate(EffectScalar(e, 5, t, 0) / 100f);
                        // Alpha choke (only with a blur): scale the blurred alpha by 1 / (1 - choke^0.2).
                        op.X = sigma > 0 && choke > 0 ? math.min(1f / (1f - math.pow(choke, 0.2f)), 1e6f) : 0;
                        bool edge = (int)math.round(EffectScalar(e, 6, t, 1)) == 1;
                        op.Flags = (byte)((inner ? 1 : 0) | (inner && !edge ? 2 : 0));
                        break;
                    }
                    case EffectKind.StyleColorOverlay:
                    {
                        op = NewOp(EffectOpKind.ColorMatrix, compToDevice, box);
                        float4 c = math.saturate(EffectColor(e, 0, t, new float4(1, 0, 0, 1)));
                        float o = math.saturate(EffectScalar(e, 7, t, 100) / 100f);
                        op.Data = AddColorMatrix(1 - o, 0, 0, 0, c.x * o, 0, 1 - o, 0, 0, c.y * o, 0, 0, 1 - o, 0, c.z * o, 0, 0, 0, c.w, 0);
                        break;
                    }
                    case EffectKind.StyleStroke:
                    {
                        // After Effects' default (outside) stroke: the dilated layer alpha, painted below the layer.
                        float size = EffectScalar(e, 3, t, 3);
                        if (!(size > 0)) continue;
                        op = NewOp(EffectOpKind.StyleGlow, compToDevice, box);
                        float4 c = math.saturate(EffectColor(e, 0, t, new float4(0, 0, 0, 1)));
                        float opacity = HasEffectParam(e, 1) ? math.saturate(EffectScalar(e, 1, t, 100) / 100f) : 1f;
                        op.A = new float4(c.xyz, opacity * c.w);
                        op.Flags = 4; // dilate instead of blur
                        op.Z = size * cs.x;
                        op.W = size * cs.y;
                        break;
                    }
                    default:
                        continue;
                }
                if ((op.Kind == EffectOpKind.ColorMatrix || op.Kind == EffectOpKind.HslMatrix) && op.Flags == 0 &&
                    EffectsInLayerSpace(L.Kind) && Out.PaintFloats[op.Data + 19] > 0)
                {
                    // The filter turns transparent pixels opaque (alpha inversion): it covers the whole layer box.
                    op.Flags = 1;
                    op.OutputRect = TransformRectBounds(fx, new float4(0, 0, box));
                }
                Out.Effects.Add(op);
            }
        }

        static float Sign(float v) => v < 0 ? -1f : 1f;

        static float SanitizeAngle(float a)
        {
            a %= 360f;
            if (a < 0) a += 360f;
            return a;
        }

        int AddColorMatrix(float m0, float m1, float m2, float m3, float m4, float m5, float m6, float m7, float m8, float m9,
            float m10, float m11, float m12, float m13, float m14, float m15, float m16, float m17, float m18, float m19)
        {
            int offset = Out.PaintFloats.Length;
            Out.PaintFloats.Add(m0); Out.PaintFloats.Add(m1); Out.PaintFloats.Add(m2); Out.PaintFloats.Add(m3); Out.PaintFloats.Add(m4);
            Out.PaintFloats.Add(m5); Out.PaintFloats.Add(m6); Out.PaintFloats.Add(m7); Out.PaintFloats.Add(m8); Out.PaintFloats.Add(m9);
            Out.PaintFloats.Add(m10); Out.PaintFloats.Add(m11); Out.PaintFloats.Add(m12); Out.PaintFloats.Add(m13); Out.PaintFloats.Add(m14);
            Out.PaintFloats.Add(m15); Out.PaintFloats.Add(m16); Out.PaintFloats.Add(m17); Out.PaintFloats.Add(m18); Out.PaintFloats.Add(m19);
            return offset;
        }

        int AddColorMatrix(in ColorMatrix m)
        {
            int offset = Out.PaintFloats.Length;
            for (int r = 0; r < 4; r++)
            {
                float4 row = m.Row(r);
                Out.PaintFloats.Add(row.x); Out.PaintFloats.Add(row.y); Out.PaintFloats.Add(row.z); Out.PaintFloats.Add(row.w);
                Out.PaintFloats.Add(m.Offset[r]);
            }
            return offset;
        }

        int AddMatrix3(float3x3 m)
        {
            int offset = Out.PaintFloats.Length;
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Out.PaintFloats.Add(m[c][r]);
            return offset;
        }

        /// <summary>Skottie InvertEffect: channel inversion in RGB, HSL or YIQ.</summary>
        bool EmitInvert(ref EffectOp op, int channel)
        {
            float4 scale, trans;
            int space = 0; // 0 rgb, 1 hsl, 2 yiq
            switch (channel)
            {
                case 2: scale = new float4(-1, 1, 1, 1); trans = new float4(1, 0, 0, 0); break;
                case 3: scale = new float4(1, -1, 1, 1); trans = new float4(0, 1, 0, 0); break;
                case 4: scale = new float4(1, 1, -1, 1); trans = new float4(0, 0, 1, 0); break;
                case 16: scale = new float4(1, 1, 1, -1); trans = new float4(0, 0, 0, 1); break;
                case 1: scale = new float4(-1, -1, -1, 1); trans = new float4(1, 1, 1, 0); break;
                case 7: scale = new float4(-1, 1, 1, 1); trans = new float4(0.5f, 0, 0, 0); space = 1; break;
                case 9: scale = new float4(1, -1, 1, 1); trans = new float4(0, 1, 0, 0); space = 1; break;
                case 8: scale = new float4(1, 1, -1, 1); trans = new float4(0, 0, 1, 0); space = 1; break;
                case 6: scale = new float4(-1, -1, -1, 1); trans = new float4(0.5f, 1, 1, 0); space = 1; break;
                case 12: scale = new float4(-1, 1, 1, 1); trans = new float4(1, 0, 0, 0); space = 2; break;
                case 13: scale = new float4(1, -1, 1, 1); trans = float4.zero; space = 2; break;
                case 14: scale = new float4(1, 1, -1, 1); trans = float4.zero; space = 2; break;
                case 11: scale = new float4(-1, -1, -1, 1); trans = new float4(1, 0, 0, 0); space = 2; break;
                default: return false;
            }
            var m = ColorMatrix.Diagonal(scale, trans);
            if (space == 2)
            {
                var rgb2yiq = new ColorMatrix(new float4(0.2990f, 0.5870f, 0.1140f, 0), new float4(0.5959f, -0.2746f, -0.3213f, 0),
                    new float4(0.2115f, -0.5227f, 0.3112f, 0), new float4(0, 0, 0, 1), float4.zero);
                var yiq2rgb = new ColorMatrix(new float4(1, 0.9560f, 0.6190f, 0), new float4(1, -0.2720f, -0.6470f, 0),
                    new float4(1, -1.1060f, 1.7030f, 0), new float4(0, 0, 0, 1), float4.zero);
                m = ColorMatrix.Mul(yiq2rgb, ColorMatrix.Mul(m, rgb2yiq));
            }
            op.Kind = space == 1 ? EffectOpKind.HslMatrix : EffectOpKind.ColorMatrix;
            op.Data = AddColorMatrix(m);
            return true;
        }

        /// <summary>Skottie ShiftChannels: channel sources as a colour matrix.</summary>
        void EmitShiftChannels(ref EffectOp op, in EffectData e, float t)
        {
            float4 Coeffs(float src, out float off)
            {
                off = 0;
                switch ((int)math.clamp(src, 1, 10))
                {
                    case 1: return new float4(0, 0, 0, 1);
                    case 2: return new float4(1, 0, 0, 0);
                    case 3: return new float4(0, 1, 0, 0);
                    case 4: return new float4(0, 0, 1, 0);
                    case 5: return new float4(0.2126f, 0.7152f, 0.0722f, 0);
                    case 9: off = 1; return float4.zero;
                    default: return float4.zero;
                }
            }
            float fa = EffectScalar(e, 0, t, 1);
            float4 r = Coeffs(EffectScalar(e, 1, t, 2), out float ro), g = Coeffs(EffectScalar(e, 2, t, 3), out float go);
            float4 b = Coeffs(EffectScalar(e, 3, t, 4), out float bo), a = Coeffs(fa, out float ao);
            op.Kind = EffectOpKind.ColorMatrix;
            op.Data = AddColorMatrix(new ColorMatrix(r, g, b, a, new float4(ro, go, bo, ao)));
            // Full-on alpha fills the content bounding box (the surface) too.
            op.Flags = (byte)((int)fa == 9 ? 1 : 0);
        }

        /// <summary>Skottie EasyLevels: one 8-bit table applied to the selected channel(s).</summary>
        bool EmitEasyLevels(ref EffectOp op, in EffectData e, float t)
        {
            int channel = (int)EffectScalar(e, 0, t, 1);
            if (channel < 1 || channel > 5) return false;
            int table = LevelsLut(EffectScalar(e, 2, t, 0), EffectScalar(e, 3, t, 1), EffectScalar(e, 4, t, 1),
                EffectScalar(e, 5, t, 0), EffectScalar(e, 6, t, 1), EffectScalar(e, 7, t, 1), EffectScalar(e, 8, t, 1));
            if (table < 0) return false;
            op.Kind = EffectOpKind.ColorTables;
            if (channel == 1) op.TableRGB = table;
            else if (channel == 2) op.TableR = table;
            else if (channel == 3) op.TableG = table;
            else if (channel == 4) op.TableB = table;
            else op.TableA = table;
            return true;
        }

        /// <summary>Skottie LevelsEffect ChannelMapper::build_lut (values quantized to bytes), -1 for the identity.</summary>
        int LevelsLut(float in0, float in1, float gamma, float out0, float out1, float clipBlack, float clipWhite)
        {
            float g = 1f / math.max(gamma, 0f);
            float2 clip = new float2(0, 1);
            if ((int)clipBlack == 1) clip[out0 <= out1 ? 0 : 1] = math.saturate(out0);
            if ((int)clipWhite == 1) clip[out0 <= out1 ? 1 : 0] = math.saturate(out1);
            const float NearlyZero = 1f / 4096f;
            if (math.abs(in0 - out0) <= NearlyZero && math.abs(in1 - out1) <= NearlyZero && math.abs(g - 1) <= NearlyZero) return -1;
            float dIn = in1 - in0, dOut = out1 - out0;
            if (math.abs(dIn) <= NearlyZero)
            {
                const float Eps = 2 * NearlyZero;
                dIn += CopySign(Eps, dIn);
                in0 += CopySign(Eps, 0.5f - in0);
            }
            float tt = -in0 / dIn, dT = 1f / 255f / dIn;
            int offset = Out.PaintFloats.Length;
            for (int i = 0; i < 256; i++)
            {
                float v = out0 + dOut * math.pow(math.max(tt, 0f), g);
                Out.PaintFloats.Add(math.round(math.clamp(v, clip.x, clip.y) * 255f) / 255f);
                tt += dT;
            }
            return offset;
        }

        static float CopySign(float mag, float sign) => sign < 0 || (sign == 0 && 1f / sign < 0) ? -math.abs(mag) : math.abs(mag);

        /// <summary>Skottie CCToner: luminance mapped through a 5-colour gradient table, blended with the source.</summary>
        bool EmitToner(ref EffectOp op, in EffectData e, float t)
        {
            float weight = (100 - EffectScalar(e, 6, t, 0)) / 100f;
            if (weight <= 0) return false;
            float4 hi = Byte(EffectColor(e, 1, t, new float4(1, 1, 1, 1))), br = Byte(EffectColor(e, 2, t, new float4(1, 1, 1, 1)));
            float4 mid = Byte(EffectColor(e, 3, t, new float4(0.5f, 0.5f, 0.5f, 1))), dk = Byte(EffectColor(e, 4, t, new float4(0, 0, 0, 1)));
            float4 sh = Byte(EffectColor(e, 5, t, new float4(0, 0, 0, 1)));
            var colors = stackalloc float4[5];
            switch ((int)math.round(EffectScalar(e, 0, t, 0)))
            {
                case 1:
                    colors[0] = sh; colors[1] = LerpByte(sh, hi, 0.25f); colors[2] = LerpByte(sh, hi, 0.5f);
                    colors[3] = LerpByte(sh, hi, 0.75f); colors[4] = hi;
                    break;
                case 2:
                    colors[0] = sh; colors[1] = LerpByte(sh, mid, 0.5f); colors[2] = mid;
                    colors[3] = LerpByte(mid, hi, 0.5f); colors[4] = hi;
                    break;
                case 3:
                    colors[0] = sh; colors[1] = dk; colors[2] = mid; colors[3] = br; colors[4] = hi;
                    break;
                default:
                    colors[0] = colors[1] = colors[2] = colors[3] = colors[4] = mid;
                    break;
            }
            // sksg::GradientColorFilter MakeNColorGradient: 256-entry byte tables over 4 spans.
            int offset = Out.PaintFloats.Length;
            Out.PaintFloats.ResizeUninitialized(offset + 3 * 256);
            float* tab = Out.PaintFloats.GetUnsafePtr() + offset;
            int spanStart = 0;
            for (int i = 0; i < 4; i++)
            {
                int spanStop = (int)math.round((i + 1) * 255f / 4), spanSize = spanStop - spanStart;
                if (spanStart > spanStop) continue;
                float3 c0 = colors[i].xyz * 255f, c1 = colors[i + 1].xyz * 255f;
                float3 d = (c1 - c0) / spanSize, c = c0;
                for (int j = spanStart; j <= spanStop; j++)
                {
                    float3 r = math.round(c) / 255f;
                    tab[j] = r.x; tab[256 + j] = r.y; tab[512 + j] = r.z;
                    c += d;
                }
                spanStart = spanStop + 1;
            }
            op.Kind = EffectOpKind.GradientMap;
            op.Data = offset;
            op.X = math.saturate(weight);
            return true;
        }

        static float4 Byte(float4 c) => math.round(math.saturate(c) * 255f) / 255f;

        static float4 LerpByte(float4 a, float4 b, float t) => Byte(a + (b - a) * t);

        /// <summary>Skottie FractalNoiseEffect parameters.</summary>
        void EmitFractalNoise(ref EffectOp op, in EffectData e, float t)
        {
            op.Kind = EffectOpKind.FractalNoise;
            int fractal = (int)math.round(EffectScalar(e, 0, t, 0));
            int noise = (int)math.round(EffectScalar(e, 1, t, 0));
            // filter: 0 nearest, 1 linear, 2 soft linear; fractal: 0 basic, 1 turbulent basic, 2 turbulent smooth, 3 sharp
            int filter = noise == 1 ? 0 : noise == 2 ? 1 : 2;
            int fr = fractal == 1 ? 0 : fractal == 3 ? 2 : fractal == 4 ? 1 : 3;
            op.Flags = (byte)(filter | (fr << 2));

            // Evolution -> two noise planes and a lerp weight.
            const float EvolutionScale = 0.25f;
            float evolution = EffectScalar(e, 23, t, 0), revolutions = EffectScalar(e, 26, t, 0);
            bool cycleEvolution = EffectScalar(e, 25, t, 0) != 0;
            float evoRad = math.radians(evolution), revRad = math.max(revolutions, 1f) * math.PI * 2;
            float cycle = cycleEvolution ? math.round(revRad * EvolutionScale) : float.MaxValue;
            float scale = cycleEvolution ? cycle / revRad : EvolutionScale;
            float offset = SkRandom.RangeU((uint)(int)EffectScalar(e, 27, t, 0), 0, 100);
            float evo = evoRad * scale, evoFloor = math.floor(evo);
            op.A = new float4(GlslMod(evoFloor, cycle) + offset, GlslMod(evoFloor + 1, cycle) + offset, evo - evoFloor, 0);
            op.X = math.clamp(EffectScalar(e, 15, t, 1), 1f, 20f);                 // octaves
            op.Y = math.clamp(EffectScalar(e, 17, t, 100) * 0.01f, 0f, 100f);      // persistence

            // Shader matrix: T(offset) * S(scale%) * R(rotation) * S(64); the noise is evaluated at its inverse.
            bool uniform = (int)math.round(EffectScalar(e, 8, t, 0)) == 1;
            float s = EffectScalar(e, 9, t, 100);
            float2 sc = uniform ? new float2(s, s) : new float2(EffectScalar(e, 10, t, 100), EffectScalar(e, 11, t, 100));
            sc = math.clamp(sc, 1f, 10000f) * 0.01f;
            float2 off = EffectPoint(e, 12, t, float2.zero);
            // (Affine2.Rotate(-a) is SkMatrix::RotateDeg(a).)
            float rot = math.radians(EffectScalar(e, 7, t, 0));
            Affine2 m = Affine2.Scale(64, 64).Then(Affine2.Rotate(-rot)).Then(Affine2.Scale(sc.x, sc.y)).Then(Affine2.Translate(off.x, off.y));
            Affine2 inv = m.Inverse();
            // Sub-layer matrix: T(-subOffset%) * R(-subRotation) * S(100 / subScale).
            float subScale = 100f / math.clamp(EffectScalar(e, 18, t, 50), 10f, 10000f);
            float2 subOff = EffectPoint(e, 20, t, float2.zero);
            Affine2 sub = Affine2.Scale(subScale, subScale).Then(Affine2.Rotate(math.radians(EffectScalar(e, 19, t, 0))))
                .Then(Affine2.Translate(-subOff.x * 0.01f, -subOff.y * 0.01f));
            int data = Out.PaintFloats.Length;
            Out.PaintFloats.Add(inv.a); Out.PaintFloats.Add(inv.b); Out.PaintFloats.Add(inv.c); Out.PaintFloats.Add(inv.d); Out.PaintFloats.Add(inv.tx); Out.PaintFloats.Add(inv.ty);
            Out.PaintFloats.Add(sub.a); Out.PaintFloats.Add(sub.b); Out.PaintFloats.Add(sub.c); Out.PaintFloats.Add(sub.d); Out.PaintFloats.Add(sub.tx); Out.PaintFloats.Add(sub.ty);
            op.Data = data;
        }

        static float GlslMod(float x, float y) => x - y * math.floor(x / y);

        /// <summary>Skottie SphereEffect parameters (A = center.xy, radius; Data = rotation + lighting).</summary>
        bool EmitSphere(ref EffectOp op, in EffectData e, float t)
        {
            float radius = EffectScalar(e, 6, t, 0);
            if (!(radius > 0)) return false;
            op.Kind = EffectOpKind.Sphere;
            op.A = new float4(EffectPoint(e, 7, t, float2.zero), radius, 0);
            int render = (int)math.round(EffectScalar(e, 8, t, 1));
            op.Flags = (byte)(render == 1 ? 0 : render == 2 ? 1 : 2); // 0 full, 1 outside, 2 inside

            float rx = math.radians(EffectScalar(e, 1, t, 0)), ry = math.radians(EffectScalar(e, 2, t, 0)), rz = math.radians(-EffectScalar(e, 3, t, 0));
            float3x3 mx = float3x3.RotateX(rx), my = float3x3.RotateY(ry), mz = float3x3.RotateZ(rz);
            float3x3 rot;
            switch ((int)math.round(EffectScalar(e, 4, t, 1)))
            {
                case 1: rot = math.mul(mx, math.mul(my, mz)); break;
                case 2: rot = math.mul(mx, math.mul(mz, my)); break;
                case 3: rot = math.mul(my, math.mul(mx, mz)); break;
                case 4: rot = math.mul(my, math.mul(mz, mx)); break;
                case 5: rot = math.mul(mz, math.mul(mx, my)); break;
                default: rot = math.mul(mz, math.mul(my, mx)); break;
            }
            float ambient = math.clamp(EffectScalar(e, 16, t, 100) * 0.01f, 0f, 2f);
            float intensity = math.clamp(EffectScalar(e, 10, t, 0) * 0.01f, 0f, 10f);
            float diffuse = math.saturate(EffectScalar(e, 17, t, 0) * 0.01f) * intensity;
            float specular = math.saturate(EffectScalar(e, 18, t, 0) * 0.01f) * intensity;
            float height = math.clamp(EffectScalar(e, 12, t, 0) * 0.01f, -1f, 1f);
            float direction = math.radians(EffectScalar(e, 13, t, 0) - 90);
            float lz = math.sin(height * math.PI / 2), lr = math.sqrt(1 - lz * lz);
            float3 lightVec = new float3(math.cos(direction) * lr, math.sin(direction) * lr, lz);
            float4 lightColor = EffectColor(e, 11, t, new float4(1, 1, 1, 1));
            float specExp = 1f / math.clamp(EffectScalar(e, 19, t, 0.5f), 0.001f, 0.5f);
            int data = Out.PaintFloats.Length;
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Out.PaintFloats.Add(rot[c][r]);
            Out.PaintFloats.Add(lightVec.x); Out.PaintFloats.Add(lightVec.y); Out.PaintFloats.Add(lightVec.z);
            Out.PaintFloats.Add(lightColor.x); Out.PaintFloats.Add(lightColor.y); Out.PaintFloats.Add(lightColor.z);
            Out.PaintFloats.Add(ambient); Out.PaintFloats.Add(diffuse); Out.PaintFloats.Add(specular); Out.PaintFloats.Add(specExp);
            op.Data = data;
            return true;
        }

        /// <summary>lottie-web SVGProLevelsFilter.getTableValue: 256-entry table, or -1 for the identity mapping.</summary>
        int LevelsTable(in EffectData e, int first, float t)
        {
            if (first + 4 >= e.ParamCount) return -1;
            float inBlack = EffectScalar(e, first, t, 0), inWhite = EffectScalar(e, first + 1, t, 1), gamma = EffectScalar(e, first + 2, t, 1);
            float outBlack = EffectScalar(e, first + 3, t, 0), outWhite = EffectScalar(e, first + 4, t, 1);
            if (inBlack == 0 && inWhite == 1 && gamma == 1 && outBlack == 0 && outWhite == 1) return -1;
            int offset = Out.PaintFloats.Length;
            float mn = math.min(inBlack, inWhite), mx = math.max(inBlack, inWhite);
            float outDelta = outWhite - outBlack, inDelta = inWhite - inBlack;
            for (int k = 0; k < 256; k++)
            {
                float perc = k / 255f;
                float v;
                if (perc <= mn) v = inDelta < 0 ? outWhite : outBlack;
                else if (perc >= mx) v = inDelta < 0 ? outBlack : outWhite;
                else v = outBlack + outDelta * math.pow((perc - inBlack) / inDelta, 1f / gamma);
                Out.PaintFloats.Add(v);
            }
            return offset;
        }
    }

    /// <summary>4x5 colour matrix (Skia layout: rows R, G, B, A of 4 coefficients + offset, offsets normalized).</summary>
    public struct ColorMatrix
    {
        public float4x4 M;      // M[col][row]: math.mul(M, rgba) + Offset
        public float4 Offset;

        public ColorMatrix(float4 r, float4 g, float4 b, float4 a, float4 offset)
        {
            M = math.transpose(new float4x4(r, g, b, a));
            Offset = offset;
        }

        public float4 Row(int r) => math.transpose(M)[r];

        public static ColorMatrix Diagonal(float4 scale, float4 offset) =>
            new ColorMatrix { M = new float4x4(scale.x, 0, 0, 0, 0, scale.y, 0, 0, 0, 0, scale.z, 0, 0, 0, 0, scale.w), Offset = offset };

        /// <summary>a ∘ b: applies b first.</summary>
        public static ColorMatrix Mul(in ColorMatrix a, in ColorMatrix b) =>
            new ColorMatrix { M = math.mul(a.M, b.M), Offset = math.mul(a.M, b.Offset) + a.Offset };
    }

    /// <summary>Projective mapping helpers (Skia SkMatrix::PolyToPoly for 4 points).</summary>
    public static class Homography
    {
        /// <summary>Maps the rectangle (0,0)-(size) onto the quad (ul, ur, lr, ll).</summary>
        public static bool RectToQuad(float2 size, float2 ul, float2 ur, float2 lr, float2 ll, out float3x3 m)
        {
            m = float3x3.identity;
            if (!(size.x > 0) || !(size.y > 0)) return false;
            if (!SquareToQuad(ul, ur, lr, ll, out float3x3 sq)) return false;
            float3x3 toUnit = new float3x3(1f / size.x, 0, 0, 0, 1f / size.y, 0, 0, 0, 1);
            m = math.mul(sq, toUnit);
            return math.abs(math.determinant(m)) > 1e-12f;
        }

        /// <summary>Unit square (0,0),(1,0),(1,1),(0,1) -> quad p0..p3 (Heckbert).</summary>
        static bool SquareToQuad(float2 p0, float2 p1, float2 p2, float2 p3, out float3x3 m)
        {
            float2 d1 = p1 - p2, d2 = p3 - p2, s = p0 - p1 + p2 - p3;
            float g, h;
            if (math.abs(s.x) < 1e-9f && math.abs(s.y) < 1e-9f)
            {
                g = h = 0;
            }
            else
            {
                float den = d1.x * d2.y - d2.x * d1.y;
                if (math.abs(den) < 1e-12f) { m = float3x3.identity; return false; }
                g = (s.x * d2.y - d2.x * s.y) / den;
                h = (d1.x * s.y - s.x * d1.y) / den;
            }
            float a = p1.x - p0.x + g * p1.x, b = p3.x - p0.x + h * p3.x, c = p0.x;
            float d = p1.y - p0.y + g * p1.y, e = p3.y - p0.y + h * p3.y, f = p0.y;
            m = new float3x3(a, b, c, d, e, f, g, h, 1);
            return true;
        }
    }

    /// <summary>Skia's SkRandom (used by Skottie to derive the fractal noise seed offset).</summary>
    public static class SkRandom
    {
        public static uint RangeU(uint seed, uint min, uint max)
        {
            uint k = NextLcg(seed);
            if (k == 0) k = NextLcg(k);
            uint j = NextLcg(k);
            if (j == 0) j = NextLcg(j);
            k = 30345u * (k & 0xffff) + (k >> 16);
            j = 18000u * (j & 0xffff) + (j >> 16);
            uint next = ((k << 16) | (k >> 16)) + j;
            uint range = max - min + 1;
            return range == 0 ? next : min + next % range;
        }

        static uint NextLcg(uint seed) => 1664525u * seed + 1013904223u;
    }
}
