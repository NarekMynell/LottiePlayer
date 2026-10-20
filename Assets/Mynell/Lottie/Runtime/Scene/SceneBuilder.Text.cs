using Mynell.Lottie.Eval;
using Mynell.Lottie.Model;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Scene
{
    /// <summary>Text layers: lottie-web TextAnimatorProperty.getMeasures + SVG glyph rendering.</summary>
    public unsafe partial struct SceneBuilder
    {
        [NativeDisableUnsafePtrRestriction] public GlyphData* Glyphs;
        public int GlyphCount;
        [NativeDisableUnsafePtrRestriction] public GlyphContour* GlyphContours;
        [NativeDisableUnsafePtrRestriction] public float2* GlyphPoints;
        [NativeDisableUnsafePtrRestriction] public TextDocData* TextDocs;
        [NativeDisableUnsafePtrRestriction] public TextLetter* TextLetters;
        [NativeDisableUnsafePtrRestriction] public int* TextInts;
        [NativeDisableUnsafePtrRestriction] public float* TextFloats;
        [NativeDisableUnsafePtrRestriction] public TextDocKey* TextDocKeys;
        [NativeDisableUnsafePtrRestriction] public TextAnimatorData* TextAnimators;
        [NativeDisableUnsafePtrRestriction] public TextLayerInfo* TextLayers;
        [NativeDisableUnsafePtrRestriction] public TextSelectorData* TextSelectors;

        void InitText(in LottieNativeData d)
        {
            Glyphs = (GlyphData*)d.Glyphs.GetUnsafeReadOnlyPtr();
            GlyphCount = d.Glyphs.Length;
            GlyphContours = (GlyphContour*)d.GlyphContours.GetUnsafeReadOnlyPtr();
            GlyphPoints = (float2*)d.GlyphPoints.GetUnsafeReadOnlyPtr();
            TextDocs = (TextDocData*)d.TextDocs.GetUnsafeReadOnlyPtr();
            TextLetters = (TextLetter*)d.TextLetters.GetUnsafeReadOnlyPtr();
            TextInts = (int*)d.TextInts.GetUnsafeReadOnlyPtr();
            TextFloats = (float*)d.TextFloats.GetUnsafeReadOnlyPtr();
            TextDocKeys = (TextDocKey*)d.TextDocKeys.GetUnsafeReadOnlyPtr();
            TextAnimators = (TextAnimatorData*)d.TextAnimators.GetUnsafeReadOnlyPtr();
            TextLayers = (TextLayerInfo*)d.TextLayers.GetUnsafeReadOnlyPtr();
            TextSelectors = (TextSelectorData*)d.TextSelectors.GetUnsafeReadOnlyPtr();
        }

        /// <summary>Per-frame values of one text selector (lottie-web TextSelectorProp).</summary>
        struct SelectorFrame
        {
            public float S, E, X1, Y1, X2, Y2, Smooth, Amount;
            public byte Shape, Kind;
            // Expression selectors: baked amounts (percent) of the two frames around t.
            public int Expr0, Expr1, ExprTotal;
            public float ExprLerp;
        }

        /// <summary>Per-frame values of one text animator (lottie-web TextAnimatorDataProperty + its selectors).</summary>
        struct AnimatorFrame
        {
            public SelectorFrame Sel0, Sel1, Sel2, Sel3;
            public int SelCount;
            public float R, Rx, Ry, Sk, Sa, Opacity, StrokeWidth, Hue, Sat, Bright, Tracking;
            public float2 Scale, Blur;
            public float3 Anchor, Position;
            public float4 StrokeColor, FillColor;
            public byte HasR, HasRx, HasRy, HasSk, HasScale, HasAnchor, HasOpacity, HasPosition;
            public byte HasSw, HasSc, HasFc, HasFh, HasFs, HasFb, HasT, HasBl;
        }

        SelectorFrame EvalSelector(in TextSelectorData a, float t, int totalChars, int letterCount)
        {
            var f = new SelectorFrame { Kind = a.Kind, Expr0 = -1, Expr1 = -1 };
            if (a.Kind == 1)
            {
                if (a.ExprFirst >= 0 && a.ExprFrames > 0 && a.ExprTotal > 0)
                {
                    float x = math.clamp(t - a.ExprT0, 0, a.ExprFrames - 1);
                    int k0 = (int)math.floor(x), k1 = math.min(k0 + 1, a.ExprFrames - 1);
                    f.Expr0 = a.ExprFirst + k0 * a.ExprTotal;
                    f.Expr1 = a.ExprFirst + k1 * a.ExprTotal;
                    f.ExprLerp = x - k0;
                    f.ExprTotal = a.ExprTotal;
                }
                return f;
            }
            float sv = P.Scalar(a.S, t, 0);
            float ev = a.HasE != 0 ? P.Scalar(a.E, t, 100) : (a.Range == 2 ? letterCount : 100);
            float divisor = a.Range == 2 ? 1f : 100f / totalChars;
            float o = P.Scalar(a.O, t, 0) / divisor;
            float s = sv / divisor + o, e = ev / divisor + o;
            if (s > e) { float tmp = s; s = e; e = tmp; }
            f.S = s;
            f.E = e;
            float ne = P.Scalar(a.Ne, t, 0), xe = P.Scalar(a.Xe, t, 0);
            f.X1 = 0; f.Y1 = 0; f.X2 = 1; f.Y2 = 1;
            if (ne > 0) f.X1 = ne / 100f; else f.Y1 = -ne / 100f;
            if (xe > 0) f.X2 = 1f - xe / 100f; else f.Y2 = 1f + xe / 100f;
            f.Smooth = P.Scalar(a.Sm, t, 100);
            f.Amount = P.Scalar(a.A, t, 100) * 0.01f;
            f.Shape = a.Shape;
            return f;
        }

        AnimatorFrame EvalAnimator(in TextAnimatorData a, float t, int totalChars, int letterCount)
        {
            var f = new AnimatorFrame { SelCount = math.min(a.SelCount, 4) };
            for (int k = 0; k < f.SelCount; k++)
            {
                var sf = EvalSelector(TextSelectors[a.SelFirst + k], t, totalChars, letterCount);
                if (k == 0) f.Sel0 = sf;
                else if (k == 1) f.Sel1 = sf;
                else if (k == 2) f.Sel2 = sf;
                else f.Sel3 = sf;
            }

            const float Deg = math.PI / 180f;
            if (a.R >= 0) { f.HasR = 1; f.R = P.Scalar(a.R, t, 0) * Deg; }
            if (a.Rx >= 0) { f.HasRx = 1; f.Rx = P.Scalar(a.Rx, t, 0) * Deg; }
            if (a.Ry >= 0) { f.HasRy = 1; f.Ry = P.Scalar(a.Ry, t, 0) * Deg; }
            if (a.Sk >= 0) { f.HasSk = 1; f.Sk = P.Scalar(a.Sk, t, 0) * Deg; f.Sa = P.Scalar(a.Sa, t, 0) * Deg; }
            if (a.Scale >= 0) { f.HasScale = 1; f.Scale = P.Vec2(a.Scale, t, new float2(100, 100)) * 0.01f; }
            if (a.Anchor >= 0) { f.HasAnchor = 1; f.Anchor = P.Vec3(a.Anchor, t, float3.zero); }
            if (a.Opacity >= 0) { f.HasOpacity = 1; f.Opacity = P.Scalar(a.Opacity, t, 100) * 0.01f; }
            if (a.Position >= 0) { f.HasPosition = 1; f.Position = P.Vec3(a.Position, t, float3.zero); }
            if (a.StrokeWidth >= 0) { f.HasSw = 1; f.StrokeWidth = P.Scalar(a.StrokeWidth, t, 0); }
            if (a.StrokeColor >= 0) { f.HasSc = 1; f.StrokeColor = P.Vec4(a.StrokeColor, t, new float4(0, 0, 0, 1)); }
            if (a.FillColor >= 0) { f.HasFc = 1; f.FillColor = P.Vec4(a.FillColor, t, new float4(0, 0, 0, 1)); }
            if (a.FillHue >= 0) { f.HasFh = 1; f.Hue = P.Scalar(a.FillHue, t, 0); }
            if (a.FillSat >= 0) { f.HasFs = 1; f.Sat = P.Scalar(a.FillSat, t, 0) * 0.01f; }
            if (a.FillBright >= 0) { f.HasFb = 1; f.Bright = P.Scalar(a.FillBright, t, 0) * 0.01f; }
            if (a.Tracking >= 0) { f.HasT = 1; f.Tracking = P.Scalar(a.Tracking, t, 0); }
            if (a.Blur >= 0) { f.HasBl = 1; f.Blur = P.Vec2(a.Blur, t, float2.zero); }
            return f;
        }

        /// <summary>
        /// Amount of an animator for a letter: its single selector (lottie-web), or its selectors added and clamped to
        /// [-1, 1] (After Effects "Add" mode, as Skottie).
        /// </summary>
        float GetMult(in AnimatorFrame f, float ind)
        {
            if (f.SelCount == 0) return 1;
            if (f.SelCount == 1) return SelectorMult(f.Sel0, ind);
            float m = SelectorMult(f.Sel0, ind) + SelectorMult(f.Sel1, ind);
            if (f.SelCount > 2) m += SelectorMult(f.Sel2, ind);
            if (f.SelCount > 3) m += SelectorMult(f.Sel3, ind);
            return math.clamp(m, -1f, 1f);
        }

        /// <summary>lottie-web TextSelectorProp.getMult / TextExpressionSelectorProp.getMult.</summary>
        float SelectorMult(in SelectorFrame f, float ind)
        {
            if (f.Kind == 1)
            {
                if (f.Expr0 < 0) return 1;
                int i = math.clamp((int)ind, 0, f.ExprTotal - 1);
                return math.lerp(TextFloats[f.Expr0 + i], TextFloats[f.Expr1 + i], f.ExprLerp) * 0.01f;
            }
            float s = f.S, e = f.E;
            float mult = 0;
            switch (f.Shape)
            {
                case 2:
                    if (e == s) mult = ind >= e ? 1 : 0;
                    else mult = math.max(0, math.min(0.5f / (e - s) + (ind - s) / (e - s), 1));
                    break;
                case 3:
                    if (e == s) mult = ind >= e ? 0 : 1;
                    else mult = 1 - math.max(0, math.min(0.5f / (e - s) + (ind - s) / (e - s), 1));
                    break;
                case 4:
                    if (e == s) mult = 0;
                    else
                    {
                        mult = math.max(0, math.min(0.5f / (e - s) + (ind - s) / (e - s), 1));
                        mult = mult < 0.5f ? mult * 2 : 1 - 2 * (mult - 0.5f);
                    }
                    break;
                case 5:
                    if (e == s) mult = 0;
                    else
                    {
                        float tot = e - s;
                        float i2 = math.min(math.max(0, ind + 0.5f - s), e - s);
                        float x = -tot / 2 + i2;
                        float a = tot / 2;
                        mult = math.sqrt(math.max(0, 1 - x * x / (a * a)));
                    }
                    break;
                case 6:
                    if (e == s) mult = 0;
                    else
                    {
                        float i2 = math.min(math.max(0, ind + 0.5f - s), e - s);
                        mult = (1 + math.cos(math.PI + math.PI * 2 * i2 / (e - s))) / 2;
                    }
                    break;
                default:
                    if (ind >= math.floor(s))
                    {
                        if (ind - s < 0) mult = math.max(0, math.min(math.min(e, 1) - (s - ind), 1));
                        else mult = math.max(0, math.min(e - ind, 1));
                    }
                    break;
            }
            mult = (float)Easing.Evaluate(f.X1, f.Y1, f.X2, f.Y2, mult);
            if (f.Smooth != 100)
            {
                float smoothness = f.Smooth * 0.01f;
                if (smoothness == 0) smoothness = 0.00000001f;
                float threshold = 0.5f - smoothness * 0.5f;
                if (mult < threshold) mult = 0;
                else mult = math.min(1, (mult - threshold) / smoothness);
            }
            return mult * f.Amount;
        }

        // ------------------------------------------------------------------ colour helpers (lottie-web)

        static float3 RgbToHsv(float3 c)
        {
            float r = c.x * 255, g = c.y * 255, b = c.z * 255;
            float max = math.max(r, math.max(g, b)), min = math.min(r, math.min(g, b));
            float d = max - min;
            float h;
            float s = max == 0 ? 0 : d / max;
            float v = max / 255;
            if (max == min) h = 0;
            else if (max == r) h = (g - b + d * (g < b ? 6 : 0)) / (6 * d);
            else if (max == g) h = (b - r + d * 2) / (6 * d);
            else h = (r - g + d * 4) / (6 * d);
            return new float3(h, s, v);
        }

        static float3 HsvToRgb(float3 hsv)
        {
            float h = hsv.x, s = hsv.y, v = hsv.z;
            int i = (int)math.floor(h * 6);
            float f = h * 6 - i;
            float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            switch (((i % 6) + 6) % 6)
            {
                case 0: return new float3(v, t, p);
                case 1: return new float3(q, v, p);
                case 2: return new float3(p, v, t);
                case 3: return new float3(p, q, v);
                case 4: return new float3(t, p, v);
                default: return new float3(v, p, q);
            }
        }

        // ------------------------------------------------------------------ rendering

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void RenderText(in LayerData L, float t, in Affine2 world, float4 clip, float layerOpacity, int depth)
        {
            if (L.Text < 0) return;
            TextLayerInfo info = TextLayers[L.Text];
            if (info.DocKeyCount == 0) return;
            int k = 0;
            while (k < info.DocKeyCount - 1 && TextDocKeys[info.DocKeyFirst + k + 1].Time <= t) k++;
            TextDocData doc = TextDocs[TextDocKeys[info.DocKeyFirst + k].Doc];
            int n = doc.LetterCount;
            if (n == 0) return;
            TextLetter* letters = TextLetters + doc.LetterFirst;
            float2 alignment = info.Alignment >= 0 ? P.Vec2(info.Alignment, t, float2.zero) : float2.zero;

            int na = info.AnimatorCount;
            var anim = new NativeArray<AnimatorFrame>(math.max(1, na), Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int a = 0; a < na; a++)
                anim[a] = EvalAnimator(TextAnimators[info.AnimatorFirst + a], t, TextInts[doc.AnimatorTotalFirst + a], n);

            // Animator-driven justification offsets (tracking animators on right / centre aligned text).
            var justify = new NativeArray<float>(n, Allocator.Temp, NativeArrayOptions.ClearMemory);
            int j = doc.Justify;
            if (j == 1 || j == 2)
            {
                float animatorJustifyOffset = 0, animatorFirstCharOffset = 0;
                float justifyOffsetMult = j == 2 ? -0.5f : -1f;
                int lastIndex = 0;
                bool isNewLine = true;
                int i = 0;
                for (; i < n; i++)
                {
                    if (letters[i].NewLine != 0)
                    {
                        if (animatorJustifyOffset != 0) animatorJustifyOffset += animatorFirstCharOffset;
                        while (lastIndex < i) justify[lastIndex++] = animatorJustifyOffset;
                        animatorJustifyOffset = 0;
                        isNewLine = true;
                    }
                    else
                    {
                        for (int a = 0; a < na; a++)
                        {
                            var f = anim[a];
                            if (f.HasT == 0) continue;
                            if (isNewLine && j == 2) animatorFirstCharOffset += f.Tracking * justifyOffsetMult;
                            float mult = GetMult(f, TextInts[letters[i].AnIndexFirst + a]);
                            animatorJustifyOffset += f.Tracking * mult * justifyOffsetMult;
                        }
                        isNewLine = false;
                    }
                }
                if (animatorJustifyOffset != 0) animatorJustifyOffset += animatorFirstCharOffset;
                while (lastIndex < i) justify[lastIndex++] = animatorJustifyOffset;
            }

            float yOff = doc.Size * 1.2f * 0.714f;
            float xPos = 0, yPos = 0;
            bool firstLine = true;
            float trackingStep = doc.Tracking * 0.001f * doc.Size;
            float glyphScale = doc.Size / 100f;

            // Text on a path (lottie-web getMeasures, masked path branch).
            var path = new TextPath();
            bool onPath = info.HasPath != 0 && BuildTextPath(L, info, t, ref path);
            float currentLength = 0, segmentLength = 0, partialLength = 0, totalLength = path.TotalLength;
            int segmentInd = 0, pointInd = 1;
            bool hasPoints = true;
            float firstMargin = onPath ? P.Scalar(info.PathFirstMargin, t, 0) : 0;
            bool forceAlign = onPath && P.Scalar(info.PathForceAlign, t, 0) != 0;
            bool perpendicular = onPath && P.Scalar(info.PathPerpendicular, t, 0) != 0;
            float2 prevPoint = default, currentPoint = default;
            float currentPartial = 0;
            if (onPath)
            {
                currentLength = firstMargin;
                if (currentLength < 0 && path.Closed)
                {
                    if (totalLength < math.abs(currentLength)) currentLength = -math.abs(currentLength) % totalLength;
                    segmentInd = path.Segments.Length - 1;
                    pointInd = path.Segments[segmentInd].y - 1;
                    while (currentLength < 0)
                    {
                        currentLength += path.Partial[path.Segments[segmentInd].x + pointInd];
                        pointInd--;
                        if (pointInd < 0)
                        {
                            segmentInd--;
                            if (segmentInd < 0) { segmentInd = 0; pointInd = 0; break; }
                            pointInd = path.Segments[segmentInd].y - 1;
                        }
                    }
                }
                LoadPathPoint(path, segmentInd, pointInd, ref prevPoint, ref currentPoint, ref currentPartial);
                partialLength = currentPartial;
            }
            float initPathPos = currentLength;
            int initSegmentInd = segmentInd, initPointInd = pointInd;
            int currentLine = -1, groupInd = -1;

            for (int i = 0; i < n; i++)
            {
                TextLetter lt = letters[i];
                if (lt.NewLine != 0)
                {
                    xPos = 0;
                    yPos += doc.YOffset;
                    yPos += firstLine ? 1 : 0;
                    firstLine = false;
                    currentLength = initPathPos;
                    if (onPath)
                    {
                        segmentInd = initSegmentInd;
                        pointInd = initPointInd;
                        hasPoints = true;
                        LoadPathPoint(path, segmentInd, pointInd, ref prevPoint, ref currentPoint, ref currentPartial);
                        partialLength = currentPartial;
                        segmentLength = 0;
                    }
                    continue;
                }
                Affine2 m = Affine2.Identity;
                float elemOpacity = 1;
                float offf = lt.An / 2 - lt.Add;
                float2 pathPos = default;
                if (onPath)
                {
                    if (currentLine != lt.Line)
                    {
                        float lw = lt.Line < doc.LineCount ? TextFloats[doc.LineFirst + lt.Line] : 0;
                        if (j == 1) currentLength += totalLength - lw;
                        else if (j == 2) currentLength += (totalLength - lw) / 2;
                        currentLine = lt.Line;
                    }
                    if (groupInd != lt.Ind)
                    {
                        if (groupInd >= 0 && groupInd < n) currentLength += letters[groupInd].Extra;
                        currentLength += lt.An / 2;
                        groupInd = lt.Ind;
                    }
                    currentLength += alignment.x * lt.An * 0.005f;
                    float animatorOffset = 0;
                    for (int a = 0; a < na; a++)
                    {
                        var f = anim[a];
                        if (f.HasPosition == 0 && f.HasAnchor == 0) continue;
                        float mult = GetMult(f, TextInts[lt.AnIndexFirst + a]);
                        if (f.HasPosition != 0) animatorOffset += f.Position.x * mult;
                        if (f.HasAnchor != 0) animatorOffset += f.Anchor.x * mult;
                    }
                    if (forceAlign && n > 1)
                    {
                        currentLength = letters[0].An * 0.5f + (totalLength - firstMargin - letters[0].An * 0.5f - letters[n - 1].An * 0.5f) * groupInd / (n - 1);
                        currentLength += firstMargin;
                    }
                    int guard = 0;
                    while (guard++ < 1 << 20)
                    {
                        if (segmentLength + partialLength >= currentLength + animatorOffset || !hasPoints)
                        {
                            float perc = currentPartial != 0 ? (currentLength + animatorOffset - segmentLength) / currentPartial : 0;
                            pathPos = prevPoint + (currentPoint - prevPoint) * perc;
                            m = m.Then(Affine2.Translate(-alignment.x * lt.An * 0.005f, -(alignment.y * yOff) * 0.01f));
                            break;
                        }
                        segmentLength += currentPartial;
                        pointInd++;
                        if (pointInd >= path.Segments[segmentInd].y)
                        {
                            pointInd = 0;
                            segmentInd++;
                            if (segmentInd >= path.Segments.Length)
                            {
                                if (path.Closed) segmentInd = 0;
                                else
                                {
                                    segmentLength -= currentPartial;
                                    hasPoints = false;
                                }
                            }
                        }
                        if (hasPoints)
                        {
                            prevPoint = currentPoint;
                            currentPoint = path.Points[path.Segments[segmentInd].x + pointInd];
                            currentPartial = path.Partial[path.Segments[segmentInd].x + pointInd];
                            partialLength = currentPartial;
                        }
                    }
                    m = m.Then(Affine2.Translate(-offf, 0));
                }
                else
                {
                    m = m.Then(Affine2.Translate(-offf, 0));
                    m = m.Then(Affine2.Translate(-alignment.x * lt.An * 0.005f, -alignment.y * yOff * 0.01f));
                }

                for (int a = 0; a < na; a++)
                {
                    var f = anim[a];
                    if (f.HasT == 0) continue;
                    float mult = GetMult(f, TextInts[lt.AnIndexFirst + a]);
                    if (xPos != 0 || j != 0)
                    {
                        if (onPath) currentLength += f.Tracking * mult;
                        else xPos += f.Tracking * mult;
                    }
                }
                float sw = doc.StrokeWidth;
                float3 sc = doc.HasStroke != 0 ? doc.StrokeColor.xyz : float3.zero;
                float3 fc = doc.FillColor.xyz;
                for (int a = 0; a < na; a++)
                {
                    var f = anim[a];
                    if (f.HasAnchor == 0) continue;
                    float mult = GetMult(f, TextInts[lt.AnIndexFirst + a]);
                    m = m.Then(Affine2.Translate(-f.Anchor.x * mult, -f.Anchor.y * mult));
                }
                for (int a = 0; a < na; a++)
                {
                    var f = anim[a];
                    if (f.HasScale == 0) continue;
                    float mult = GetMult(f, TextInts[lt.AnIndexFirst + a]);
                    m = m.Then(Affine2.Scale(1 + (f.Scale.x - 1) * mult, 1 + (f.Scale.y - 1) * mult));
                }
                for (int a = 0; a < na; a++)
                {
                    var f = anim[a];
                    float mult = GetMult(f, TextInts[lt.AnIndexFirst + a]);
                    if (f.HasSk != 0) m = m.Then(Affine2.SkewFromAxis(-f.Sk * mult, f.Sa * mult));
                    if (f.HasR != 0) m = m.Then(Affine2.Rotate(-f.R * mult));
                    // 3D letter rotations projected onto the plane (lottie-web to2dCSS).
                    if (f.HasRy != 0) m = m.Then(Affine2.Scale(math.cos(f.Ry * mult), 1));
                    if (f.HasRx != 0) m = m.Then(Affine2.Scale(1, math.cos(f.Rx * mult)));
                    if (f.HasOpacity != 0) elemOpacity += (f.Opacity * mult - elemOpacity) * mult;
                    if (doc.StrokeWidthAnim != 0 && f.HasSw != 0) sw += f.StrokeWidth * mult;
                    if (doc.StrokeAnim != 0 && f.HasSc != 0) sc += (f.StrokeColor.xyz - sc) * mult;
                    if (doc.FillAnim != 0 && doc.HasFill != 0)
                    {
                        if (f.HasFc != 0) fc += (f.FillColor.xyz - fc) * mult;
                        if (f.HasFh != 0)
                        {
                            float3 hsv = RgbToHsv(fc);
                            hsv.x += f.Hue * mult / 360f;
                            if (hsv.x > 1) hsv.x -= 1;
                            else if (hsv.x < 0) hsv.x += 1;
                            fc = HsvToRgb(hsv);
                        }
                        if (f.HasFs != 0)
                        {
                            float3 hsv = RgbToHsv(fc);
                            hsv.y = math.clamp(hsv.y + f.Sat * mult, 0, 1);
                            fc = HsvToRgb(hsv);
                        }
                        if (f.HasFb != 0)
                        {
                            float3 hsv = RgbToHsv(fc);
                            hsv.z = math.clamp(hsv.z + f.Bright * mult, 0, 1);
                            fc = HsvToRgb(hsv);
                        }
                    }
                }
                for (int a = 0; a < na; a++)
                {
                    var f = anim[a];
                    if (f.HasPosition == 0) continue;
                    float mult = GetMult(f, TextInts[lt.AnIndexFirst + a]);
                    m = m.Then(Affine2.Translate(onPath ? 0 : f.Position.x * mult, f.Position.y * mult));
                }
                if (onPath)
                {
                    m = m.Then(Affine2.Translate(0, -doc.BaselineShift));
                    m = m.Then(Affine2.Translate(0, alignment.y * yOff * 0.01f + yPos));
                    if (perpendicular)
                    {
                        float2 d = currentPoint - prevPoint;
                        float rot = d.x != 0 || d.y != 0 ? math.atan(d.y / d.x) : 0;
                        if (currentPoint.x < prevPoint.x) rot += math.PI;
                        m = m.Then(Affine2.Rotate(-rot));
                    }
                    m = m.Then(Affine2.Translate(pathPos.x, pathPos.y));
                    currentLength -= alignment.x * lt.An * 0.005f;
                    if (i + 1 < n && groupInd != letters[i + 1].Ind)
                    {
                        currentLength += lt.An / 2;
                        currentLength += trackingStep;
                    }
                }
                else
                {
                    m = m.Then(Affine2.Translate(xPos, yPos));
                    if (doc.HasBox != 0) m = m.Then(Affine2.Translate(doc.BoxPos.x, doc.BoxPos.y + doc.Ascent));
                    float lineWidth = lt.Line < doc.LineCount ? TextFloats[doc.LineFirst + lt.Line] : 0;
                    if (j == 1) m = m.Then(Affine2.Translate(justify[i] + doc.JustifyOffset + (doc.BoxWidth - lineWidth), 0));
                    else if (j == 2) m = m.Then(Affine2.Translate(justify[i] + doc.JustifyOffset + (doc.BoxWidth - lineWidth) / 2, 0));
                    m = m.Then(Affine2.Translate(0, -doc.BaselineShift));
                    m = m.Then(Affine2.Translate(offf, 0));
                    m = m.Then(Affine2.Translate(alignment.x * lt.An * 0.005f, alignment.y * yOff * 0.01f));
                    xPos += lt.L + trackingStep;
                }

                if (lt.Glyph < 0 || lt.Glyph >= GlyphCount) continue;
                GlyphData g = Glyphs[lt.Glyph];
                if (g.Layer >= 0)
                {
                    // Animated glyph: its composition, on the text layer's local time, scaled to the font size.
                    float glyphOpacity = elemOpacity * layerOpacity;
                    float glyphTime = t - L.StartTime;
                    if (glyphOpacity <= 0 || depth >= MaxDepth || !InRange(Layers[g.Layer], glyphTime)) continue;
                    Affine2 glyphToDevice = Affine2.Scale(doc.Size / 100f, doc.Size / 100f).Then(m).Then(world);
                    int gg = glyphOpacity < 1f ? BeginGroup(glyphOpacity, 0, 0, clip, 6) : -1;
                    RenderLayer(g.Layer, glyphTime, glyphToDevice, clip, depth + 1);
                    if (gg >= 0) EndGroup(gg);
                    continue;
                }
                if (g.Image >= 0)
                {
                    // Colour emoji: the glyph bitmap, not tinted by the fill colour (like browsers).
                    float emojiOpacity = elemOpacity * layerOpacity;
                    if (emojiOpacity > 0) EmitGlyphImage(g, glyphScale, m.Then(world), emojiOpacity, clip);
                    continue;
                }
                if (g.ContourCount == 0) continue;
                float opacity = elemOpacity * layerOpacity;
                if (opacity <= 0) continue;
                bool fill = doc.HasFill != 0;
                bool stroke = (doc.HasStroke != 0 || doc.StrokeAnim != 0) && sw > 0;
                if (!fill && !stroke) continue;
                // lottie-web: rgb(round(c * 255)).
                float4 fillColor = new float4(math.round(math.saturate(fc) * 255f) / 255f, 1);
                float4 strokeColor = new float4(math.round(math.saturate(sc) * 255f) / 255f, 1);
                Affine2 letterToDevice = m.Then(world);

                // Animator blur (Skottie): each glyph blurred in its own space, sigma = blur x 0.3.
                float2 blur = float2.zero;
                for (int a = 0; a < na; a++)
                {
                    var f = anim[a];
                    if (f.HasBl != 0) blur += f.Blur * GetMult(f, TextInts[lt.AnIndexFirst + a]);
                }
                int blurGroup = -1;
                if (math.any(blur > 0.01f))
                {
                    blurGroup = BeginGroup(1f, 0, 0, clip, 7);
                    var bd = Out.Groups[blurGroup];
                    bd.EffectFirst = Out.Effects.Length;
                    float2 sigma = math.max(blur, 0) * 0.3f;
                    Out.Effects.Add(new EffectOp
                    {
                        Kind = EffectOpKind.GaussianBlur,
                        Z = sigma.x * math.length(new float2(letterToDevice.a, letterToDevice.b)),
                        W = sigma.y * math.length(new float2(letterToDevice.c, letterToDevice.d)),
                        TableR = -1, TableG = -1, TableB = -1, TableA = -1, TableRGB = -1,
                        Data = -1,
                        OutputRect = EmptyRect,
                    });
                    bd.EffectCount = 1;
                    Out.Groups[blurGroup] = bd;
                }

                // Group opacity applies to fill + stroke together.
                int grp = -1;
                float paintOpacity = opacity;
                if (fill && stroke && opacity < 1f)
                {
                    grp = BeginGroup(opacity, 0, 0, clip, 5);
                    paintOpacity = 1f;
                }
                bool strokeOnTop = doc.StrokeOverFill != 0;
                if (fill && (strokeOnTop || !stroke)) EmitGlyphFill(g, glyphScale, letterToDevice, fillColor, paintOpacity, clip);
                if (stroke) EmitGlyphStroke(g, glyphScale, letterToDevice, strokeColor, math.max(0, sw), paintOpacity, clip);
                if (fill && stroke && !strokeOnTop) EmitGlyphFill(g, glyphScale, letterToDevice, fillColor, paintOpacity, clip);
                if (grp >= 0) EndGroup(grp);
                if (blurGroup >= 0) EndGroup(blurGroup);
            }
            path.Dispose();
            justify.Dispose();
            anim.Dispose();
        }

        /// <summary>A mask path sampled like lottie-web's bez.buildBezierData (per segment: points and partial lengths).</summary>
        struct TextPath
        {
            public NativeList<float2> Points;
            public NativeList<float> Partial;
            public NativeList<int2> Segments;    // (first point, count)
            public float TotalLength;
            public bool Closed;

            public void Dispose()
            {
                if (Points.IsCreated) Points.Dispose();
                if (Partial.IsCreated) Partial.Dispose();
                if (Segments.IsCreated) Segments.Dispose();
            }
        }

        static void LoadPathPoint(in TextPath path, int segment, int point, ref float2 prev, ref float2 current, ref float partial)
        {
            int2 seg = path.Segments[segment];
            int k = math.clamp(point, 0, seg.y - 1);
            current = path.Points[seg.x + k];
            partial = path.Partial[seg.x + k];
            prev = path.Points[seg.x + math.max(0, k - 1)];
        }

        bool BuildTextPath(in LayerData L, in TextLayerInfo info, float t, ref TextPath path)
        {
            if (info.PathMask < 0 || info.PathMask >= L.MaskCount) return false;
            MaskData md = Masks[L.MaskFirst + info.PathMask];
            int pFirst = Out.Points.Length, cFirst = Out.Contours.Length;
            bool closed = AppendPathProp(md.Path, t, Affine2.Identity);
            if (Out.Contours.Length == cFirst) return false;
            int count = Out.Contours[cFirst].Count;
            // (v, in, out) per vertex, reversed like lottie-web ShapePath.reverse when requested.
            var v = new NativeArray<float2>(count * 3, Allocator.Temp);
            bool reverse = P.Scalar(info.PathReverseProp, t, 0) != 0;
            for (int i = 0; i < count; i++)
            {
                int src = i;
                if (reverse) src = closed ? (i == 0 ? 0 : count - i) : count - 1 - i;
                float2 pv = Out.Points[pFirst + src * 3], pi = Out.Points[pFirst + src * 3 + 1], po = Out.Points[pFirst + src * 3 + 2];
                v[i * 3] = pv;
                v[i * 3 + 1] = reverse ? po : pi;
                v[i * 3 + 2] = reverse ? pi : po;
            }
            Out.Points.ResizeUninitialized(pFirst);
            Out.Contours.ResizeUninitialized(cFirst);

            path.Points = new NativeList<float2>(256, Allocator.Temp);
            path.Partial = new NativeList<float>(256, Allocator.Temp);
            path.Segments = new NativeList<int2>(8, Allocator.Temp);
            path.Closed = closed;
            double total = 0;
            int segs = closed ? count : count - 1;
            for (int s = 0; s < segs; s++)
            {
                int a = s, b = (s + 1) % count;
                total += AddBezierData(ref path, v[a * 3], v[b * 3], v[a * 3 + 2], v[b * 3 + 1]);
            }
            v.Dispose();
            path.TotalLength = (float)total;
            return path.Segments.Length > 0;
        }

        /// <summary>lottie-web bez.buildBezierData: 150 samples (2 for straight segments) with distances between them.</summary>
        static double AddBezierData(ref TextPath path, float2 p1, float2 p2, float2 out1, float2 in2)
        {
            double2 pt1 = p1, pt2 = p2, c1 = out1, c2 = in2;
            int curveSegments = LottieFormat.CurveSegments;
            if ((pt1.x != pt2.x || pt1.y != pt2.y) && PointOnLine(pt1, pt2, c1) && PointOnLine(pt1, pt2, c2)) curveSegments = 2;
            int first = path.Points.Length;
            double added = 0;
            double2 last = default;
            for (int k = 0; k < curveSegments; k++)
            {
                double perc = k / (double)(curveSegments - 1);
                double u = 1 - perc;
                double2 pt = u * u * u * pt1 + 3 * u * u * perc * c1 + 3 * u * perc * perc * c2 + perc * perc * perc * pt2;
                double d = k > 0 ? math.sqrt(math.lengthsq(pt - last)) : 0;
                added += d;
                path.Points.Add((float2)pt);
                path.Partial.Add((float)d);
                last = pt;
            }
            path.Segments.Add(new int2(first, curveSegments));
            return added;
        }

        static bool PointOnLine(double2 a, double2 b, double2 c)
        {
            double det = a.x * b.y + a.y * c.x + b.x * c.y - c.x * b.y - c.y * a.x - b.x * a.y;
            return det > -0.001 && det < 0.001;
        }

        void AppendGlyphContours(in GlyphData g, float scale, in Affine2 m)
        {
            for (int c = 0; c < g.ContourCount; c++)
            {
                GlyphContour gc = GlyphContours[g.ContourFirst + c];
                if (gc.Count == 0) continue;
                int first = Out.Points.Length;
                float2* src = GlyphPoints + gc.PointFirst;
                for (int v = 0; v < gc.Count * 3; v++) Out.Points.Add(m.Apply(src[v] * scale));
                Out.Contours.Add(new Contour { First = first, Count = gc.Count, Closed = 1 });
            }
        }

        void EmitGlyphImage(in GlyphData g, float scale, in Affine2 letterToDevice, float opacity, float4 clip)
        {
            ImageAsset img = Images[g.Image];
            if (img.Width <= 0 || img.Height <= 0) return;
            float4 r = g.ImageRect;
            Affine2 boxToDevice = new Affine2(r.z / img.Width, 0, 0, r.w / img.Height, r.x, r.y)
                .Then(Affine2.Scale(scale, scale)).Then(letterToDevice);
            int cFirst = Out.Contours.Length;
            AppendRectContour(boxToDevice, 0, 0, img.Width, img.Height, false);
            int paint = Out.Paints.Length;
            Out.Paints.Add(new PaintData
            {
                Kind = PaintKind.Image,
                Color = new float4(1, 1, 1, 1),
                Opacity = opacity,
                DeviceToPaint = boxToDevice.Inverse(),
                Image = g.Image,
                ImageSize = new float2(img.Width, img.Height),
            });
            AddDraw(new DrawItem
            {
                Kind = ItemKind.Fill,
                ContourFirst = cFirst,
                ContourCount = 1,
                ToDevice = Affine2.Identity,
                Paint = paint,
                Clip = clip,
                Group = _group,
            });
        }

        void EmitGlyphFill(in GlyphData g, float scale, in Affine2 letterToDevice, float4 color, float opacity, float4 clip)
        {
            int cFirst = Out.Contours.Length;
            AppendGlyphContours(g, scale, letterToDevice);
            int count = Out.Contours.Length - cFirst;
            if (count == 0) return;
            int paint = AddSolidPaint(color, opacity, Affine2.Identity);
            AddDraw(new DrawItem
            {
                Kind = ItemKind.Fill,
                ContourFirst = cFirst,
                ContourCount = count,
                ToDevice = Affine2.Identity,
                Paint = paint,
                Clip = clip,
                Group = _group,
            });
        }

        void EmitGlyphStroke(in GlyphData g, float scale, in Affine2 letterToDevice, float4 color, float width, float opacity, float4 clip)
        {
            int cFirst = Out.Contours.Length;
            AppendGlyphContours(g, scale, Affine2.Identity);
            int count = Out.Contours.Length - cFirst;
            if (count == 0) return;
            int paint = AddSolidPaint(color, opacity, Affine2.Identity);
            AddDraw(new DrawItem
            {
                Kind = ItemKind.Stroke,
                ContourFirst = cFirst,
                ContourCount = count,
                ToDevice = letterToDevice,
                StrokeWidth = width,
                Cap = 1,       // butt
                Join = 2,      // round
                MiterLimit = 4,
                Paint = paint,
                Clip = clip,
                Group = _group,
            });
        }
    }
}
