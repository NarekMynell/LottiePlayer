using Mynell.Lottie.Eval;
using Mynell.Lottie.Model;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Scene
{
    public unsafe partial struct SceneBuilder
    {
        // ------------------------------------------------------------------ shape programs

        void RenderShapeProgram(int programIndex, float t, in Affine2 layerWorld, float4 clip, float layerOpacity)
        {
            ShapeProgram prog = Programs[programIndex];
            int gc = prog.GroupCount;
            int sc = prog.ShapeCount;
            SPGroup* groups = PGroups + prog.GroupFirst;
            SPShape* shapes = PShapes + prog.ShapeFirst;
            SPModifier* mods = PModifiers + prog.ModifierFirst;
            SPStyle* styles = PStyles + prog.StyleFirst;
            SPOp* ops = POps + prog.OpFirst;
            int* idx = PIndices + prog.IndexFirst;

            // Scratch space for this program (may be nested? shape programs never recurse).
            S.GroupLocal.ResizeUninitialized(gc);
            S.GroupWorld.ResizeUninitialized(gc);
            S.GroupOpacity.ResizeUninitialized(gc);
            S.GroupHidden.ResizeUninitialized(gc);
            S.ShapeRanges.ResizeUninitialized(sc);
            S.ShapeSpace.ResizeUninitialized(sc);
            Affine2* gl = (Affine2*)S.GroupLocal.GetUnsafePtr();
            Affine2* gw = (Affine2*)S.GroupWorld.GetUnsafePtr();
            float* go = (float*)S.GroupOpacity.GetUnsafePtr();
            byte* gh = (byte*)S.GroupHidden.GetUnsafePtr();
            int2* ranges = (int2*)S.ShapeRanges.GetUnsafePtr();
            int* space = (int*)S.ShapeSpace.GetUnsafePtr();
            for (int s = 0; s < sc; s++) space[s] = shapes[s].Group;

            // 1. Group transforms & opacities.
            for (int g = 0; g < gc; g++)
            {
                SPGroup grp = groups[g];
                gh[g] = 0;
                if (grp.Transform >= 0)
                {
                    ShapeNode tn = Nodes[grp.Transform];
                    gl[g] = ShapeTransform(tn, t);
                    go[g] = P.Scalar(tn.P4, t, 100f) * 0.01f;
                }
                else
                {
                    gl[g] = Affine2.Identity;
                    go[g] = 1f;
                }
            }

            // 2. Shape paths (shape-local space; the points are stored in Out.Points but the contour
            //    ranges live in the temp contour list until styles consume them).
            int tempContourBase = S.TempContours.Length;
            for (int s = 0; s < sc; s++)
            {
                int cFirst = S.TempContours.Length;
                EvalShape(Nodes[shapes[s].Node], t);
                ranges[s] = new int2(cFirst, S.TempContours.Length - cFirst);
            }

            // 3. Modifiers (application order).
            for (int m = 0; m < prog.ModifierCount; m++)
            {
                SPModifier mod = mods[m];
                ShapeNode mn = Nodes[mod.Node];
                switch (mod.Kind)
                {
                    case ShapeKind.Trim:
                        ApplyTrim(mn, t, idx + mod.ShapesFirst, mod.ShapesCount, ranges);
                        break;
                    case ShapeKind.RoundCorners:
                    {
                        float r = P.Scalar(mn.P0, t, 0);
                        if (r != 0)
                            for (int k = 0; k < mod.ShapesCount; k++)
                                ranges[idx[mod.ShapesFirst + k]] = PathOps.RoundCorners(ref this, ranges[idx[mod.ShapesFirst + k]], r);
                        break;
                    }
                    case ShapeKind.PuckerBloat:
                    {
                        float a = P.Scalar(mn.P0, t, 0);
                        if (a != 0)
                            for (int k = 0; k < mod.ShapesCount; k++)
                                ranges[idx[mod.ShapesFirst + k]] = PathOps.PuckerBloat(ref this, ranges[idx[mod.ShapesFirst + k]], a);
                        break;
                    }
                    case ShapeKind.ZigZag:
                    {
                        float amp = P.Scalar(mn.P0, t, 0);
                        float freq = math.max(0, math.round(P.Scalar(mn.P1, t, 0)));
                        float ptType = P.Scalar(mn.P2, t, 1);
                        if (amp != 0)
                            for (int k = 0; k < mod.ShapesCount; k++)
                                ranges[idx[mod.ShapesFirst + k]] = PathOps.ZigZag(ref this, ranges[idx[mod.ShapesFirst + k]], amp, (int)freq, ptType);
                        break;
                    }
                    case ShapeKind.OffsetPath:
                    {
                        float amount = P.Scalar(mn.P0, t, 0);
                        float miter = P.Scalar(mn.P1, t, 4);
                        if (amount != 0)
                            for (int k = 0; k < mod.ShapesCount; k++)
                                ranges[idx[mod.ShapesFirst + k]] = PathOps.OffsetPath(ref this, ranges[idx[mod.ShapesFirst + k]], amount, mn.Mode3, miter);
                        break;
                    }
                    case ShapeKind.Repeater:
                        ApplyRepeater(mn, mod, t, idx, gl, go, gh);
                        break;
                    case ShapeKind.Merge:
                        ApplyMerge(mn, mod, idx, shapes, groups, ranges, gl, space, layerWorld);
                        break;
                }
            }

            // 4. World transforms (parents precede children in the group table).
            for (int g = 0; g < gc; g++)
            {
                int parent = groups[g].Parent;
                gw[g] = parent >= 0 ? gl[g].Then(gw[parent]) : gl[g].Then(layerWorld);
            }

            // 5. Paint ops.
            var groupStack = new NativeList<int>(8, Allocator.Temp);
            for (int o = 0; o < prog.OpCount; o++)
            {
                SPOp op = ops[o];
                switch (op.Kind)
                {
                    case SPOpKind.BeginGroup:
                    {
                        int g = op.Index;
                        float opacity = go[g];
                        byte blend = groups[g].BlendMode;
                        if (gh[g] != 0 || opacity <= 0)
                        {
                            // Skip the whole subtree.
                            int depth = 1;
                            while (depth > 0 && ++o < prog.OpCount)
                            {
                                if (ops[o].Kind == SPOpKind.BeginGroup) depth++;
                                else if (ops[o].Kind == SPOpKind.EndGroup) depth--;
                            }
                            break;
                        }
                        if (opacity < 1f || blend != 0)
                        {
                            int rg = BeginGroup(opacity, blend, 0, clip, 3);
                            groupStack.Add(rg);
                        }
                        else groupStack.Add(-1);
                        break;
                    }
                    case SPOpKind.EndGroup:
                    {
                        int rg = groupStack[groupStack.Length - 1];
                        groupStack.RemoveAt(groupStack.Length - 1);
                        if (rg >= 0) EndGroup(rg);
                        break;
                    }
                    case SPOpKind.Style:
                        EmitStyle(styles[op.Index], t, idx, shapes, groups, ranges, gw, clip, layerOpacity);
                        break;
                }
            }
            groupStack.Dispose();
            S.TempContours.ResizeUninitialized(tempContourBase);
        }

        Affine2 ShapeTransform(in ShapeNode tn, float t)
        {
            // lottie-web TransformProperty: translate(-a) scale(s) skewFromAxis(-sk, sa) rotate(-r) translate(p)
            float2 a = P.Vec2(tn.P0, t, float2.zero);
            float2 s = P.Vec2(tn.P2, t, new float2(100, 100)) * 0.01f;
            float r = math.radians(P.Scalar(tn.P3, t, 0));
            float2 p;
            if ((tn.Flags & ShapeFlags.SplitPosition) != 0)
                p = new float2(P.Scalar(tn.P7, t, 0), P.Scalar(tn.P8, t, 0));
            else
                p = P.Vec2(tn.P1, t, float2.zero);
            Affine2 m = Affine2.Translate(-a.x, -a.y);
            m = m.Then(Affine2.Scale(s.x, s.y));
            if (tn.P5 >= 0)
            {
                float sk = math.radians(P.Scalar(tn.P5, t, 0));
                float sa = math.radians(P.Scalar(tn.P6, t, 0));
                m = m.Then(Affine2.SkewFromAxis(-sk, sa));
            }
            if (r != 0) m = m.Then(Affine2.Rotate(-r));
            m = m.Then(Affine2.Translate(p.x, p.y));
            return m;
        }

        // ------------------------------------------------------------------ shape generators

        /// <summary>Appends contour(s) for a shape node to <see cref="SceneScratch.TempContours"/>.</summary>
        void EvalShape(in ShapeNode n, float t)
        {
            switch (n.Kind)
            {
                case ShapeKind.Path:
                {
                    int cFirst = Out.Contours.Length;
                    if (AppendPathProp(n.P0, t, Affine2.Identity) || Out.Contours.Length > cFirst)
                    {
                        S.TempContours.Add(Out.Contours[cFirst]);
                        Out.Contours.ResizeUninitialized(cFirst);
                    }
                    break;
                }
                case ShapeKind.Rect: PathOps.Rect(ref this, n, t); break;
                case ShapeKind.Ellipse: PathOps.Ellipse(ref this, n, t); break;
                case ShapeKind.Star: PathOps.Star(ref this, n, t); break;
            }
        }

        // ------------------------------------------------------------------ merge paths

        /// <summary>Flattening tolerance (device pixels) of paths combined by Merge Paths.</summary>
        const float MergeTolerance = 0.05f;

        /// <summary>
        /// After Effects Merge Paths (add / subtract / intersect / exclude): the paths above the modifier (topmost first,
        /// in the modifier's group space) are combined into one region, which replaces them. lottie-web ignores merges;
        /// this follows After Effects (and Skottie / lottie-android).
        /// </summary>
        void ApplyMerge(in ShapeNode mn, in SPModifier mod, int* idx, SPShape* shapes, SPGroup* groups, int2* ranges,
            Affine2* gl, int* space, in Affine2 layerWorld)
        {
            int count = mod.ShapesCount;
            if (count < 2 || mn.Mode < 2 || (mn.Flags & ShapeFlags.Hidden) != 0) return;
            Affine2 toDevice = Affine2.Identity;
            for (int g = mod.Group, guard = 0; g >= 0 && guard++ < 256; g = groups[g].Parent) toDevice = toDevice.Then(gl[g]);
            toDevice = toDevice.Then(layerWorld);
            float scale = toDevice.MaxScale;
            if (!(scale > 1e-6f)) return;
            float tol = MergeTolerance / scale;

            var fpts = new UnsafeList<float2>(256, Allocator.Temp);
            var fpolys = new UnsafeList<Geometry.Poly>(16, Allocator.Temp);
            var dpts = new NativeList<double2>(256, Allocator.Temp);
            var cts = new NativeList<int3>(16, Allocator.Temp);
            int operands = 0, rep = -1;
            float2* pts = (float2*)Out.Points.GetUnsafeReadOnlyPtr();
            for (int k = count - 1; k >= 0; k--)
            {
                int si = idx[mod.ShapesFirst + k];
                if (shapes[si].Hidden != 0) continue;
                if (rep < 0) rep = si;
                int op = operands++;
                // shape space -> merge group space
                Affine2 m = Affine2.Identity;
                for (int g = space[si], guard = 0; g >= 0 && g != mod.Group && guard++ < 256; g = groups[g].Parent) m = m.Then(gl[g]);
                int2 r = ranges[si];
                for (int c = 0; c < r.y; c++)
                {
                    Contour ct = S.TempContours[r.x + c];
                    if (ct.Count < 2) continue;
                    fpts.Clear();
                    fpolys.Clear();
                    Geometry.Flattener.Flatten(pts, ct.First, ct.Count, true, m, tol, ref fpts, ref fpolys);
                    for (int p = 0; p < fpolys.Length; p++)
                    {
                        Geometry.Poly poly = fpolys[p];
                        if (poly.Count < 3) continue;
                        int first = dpts.Length;
                        for (int q = 0; q < poly.Count; q++) dpts.Add(fpts[poly.First + q]);
                        cts.Add(new int3(first, poly.Count, op));
                    }
                }
            }
            if (operands >= 2)
            {
                var outPts = new NativeList<double2>(dpts.Length + 16, Allocator.Temp);
                var outCts = new NativeList<int2>(cts.Length + 4, Allocator.Temp);
                Geometry.PolyBoolean.Combine((double2*)dpts.GetUnsafePtr(), (int3*)cts.GetUnsafePtr(), cts.Length, operands, mn.Mode,
                    ref outPts, ref outCts);
                // The region, as straight-segment contours, replaces the merged paths.
                int cFirst = S.TempContours.Length;
                for (int c = 0; c < outCts.Length; c++)
                {
                    int2 oc = outCts[c];
                    int first = Out.Points.Length;
                    for (int q = 0; q < oc.y; q++)
                    {
                        float2 v = (float2)outPts[oc.x + q];
                        Out.Points.Add(v);
                        Out.Points.Add(v);
                        Out.Points.Add(v);
                    }
                    S.TempContours.Add(new Contour { First = first, Count = oc.y, Closed = 1 });
                }
                for (int k = 0; k < count; k++) ranges[idx[mod.ShapesFirst + k]] = new int2(cFirst, 0);
                ranges[rep] = new int2(cFirst, S.TempContours.Length - cFirst);
                space[rep] = mod.Group;
                outPts.Dispose();
                outCts.Dispose();
            }
            cts.Dispose();
            dpts.Dispose();
            fpolys.Dispose();
            fpts.Dispose();
        }

        // ------------------------------------------------------------------ repeater

        void ApplyRepeater(in ShapeNode rn, in SPModifier mod, float t, int* idx, Affine2* gl, float* go, byte* gh)
        {
            // lottie-web RepeaterModifier.processShapes
            float copiesF = P.Scalar(rn.P0, t, 3);
            int copies = (int)math.ceil(copiesF);
            float offset = P.Scalar(rn.P1, t, 0);
            float2 ta = P.Vec2(rn.P2, t, float2.zero);
            float2 tp = P.Vec2(rn.P3, t, float2.zero);
            float2 ts = P.Vec2(rn.P4, t, new float2(100, 100)) * 0.01f;
            float tr = math.radians(P.Scalar(rn.P5, t, 0));
            float so = P.Scalar(rn.P6, t, 100) * 0.01f;
            float eo = P.Scalar(rn.P7, t, 100) * 0.01f;

            int total = mod.CopiesCount;
            int current = math.min(copies, total);
            // Hidden copies (beyond the current count).
            for (int i = 0; i < total; i++)
            {
                int g = idx[mod.CopiesFirst + i];
                if (g < 0) continue;
                if (i >= current) { go[g] = 0; gh[g] = 1; }
            }
            if (current <= 0) return;

            Affine2 pM = Affine2.Identity, rM = Affine2.Identity, sM = Affine2.Identity;
            float offsetModulo = offset % 1f;
            float roundOffset = offset > 0 ? math.floor(offset) : math.ceil(offset);
            float iteration = 0;
            if (offset > 0)
            {
                while (iteration < roundOffset) { RepeaterStep(ref pM, ref rM, ref sM, ta, tp, ts, tr, 1, false); iteration += 1; }
                if (offsetModulo != 0) { RepeaterStep(ref pM, ref rM, ref sM, ta, tp, ts, tr, offsetModulo, false); iteration += offsetModulo; }
            }
            else if (offset < 0)
            {
                while (iteration > roundOffset) { RepeaterStep(ref pM, ref rM, ref sM, ta, tp, ts, tr, 1, true); iteration -= 1; }
                if (offsetModulo != 0) { RepeaterStep(ref pM, ref rM, ref sM, ta, tp, ts, tr, -offsetModulo, true); iteration -= offsetModulo; }
            }

            int mode = rn.Mode;
            int ii = mode == 1 ? 0 : current - 1;
            int dir = mode == 1 ? 1 : -1;
            int cont = current;
            while (cont > 0)
            {
                int g = idx[mod.CopiesFirst + ii];
                if (g >= 0)
                {
                    go[g] = current == 1 ? so : so + (eo - so) * (ii / (float)(current - 1));
                    if (iteration != 0)
                    {
                        if ((ii != 0 && dir == 1) || (ii != current - 1 && dir == -1))
                            RepeaterStep(ref pM, ref rM, ref sM, ta, tp, ts, tr, 1, false);
                        gl[g] = rM.Then(sM).Then(pM);
                    }
                    else gl[g] = Affine2.Identity;
                }
                iteration += 1;
                cont -= 1;
                ii += dir;
            }
        }

        static void RepeaterStep(ref Affine2 pM, ref Affine2 rM, ref Affine2 sM, float2 a, float2 p, float2 s, float r, float perc, bool inv)
        {
            float dir = inv ? -1 : 1;
            float scaleX = s.x + (1 - s.x) * (1 - perc);
            float scaleY = s.y + (1 - s.y) * (1 - perc);
            pM = pM.Then(Affine2.Translate(p.x * dir * perc, p.y * dir * perc));
            rM = rM.Then(Affine2.Translate(-a.x, -a.y));
            float ang = -r * dir * perc;
            if (ang != 0) rM = rM.Then(Affine2.Rotate(ang));
            rM = rM.Then(Affine2.Translate(a.x, a.y));
            sM = sM.Then(Affine2.Translate(-a.x, -a.y));
            float sx = inv ? 1 / scaleX : scaleX, sy = inv ? 1 / scaleY : scaleY;
            if (sx != 1 || sy != 1) sM = sM.Then(Affine2.Scale(sx, sy));
            sM = sM.Then(Affine2.Translate(a.x, a.y));
        }

        // ------------------------------------------------------------------ trim

        void ApplyTrim(in ShapeNode tn, float t, int* list, int count, int2* ranges)
        {
            // lottie-web TrimModifier.processShapes
            float sv = P.Scalar(tn.P0, t, 0) * 0.01f;
            float ev = P.Scalar(tn.P1, t, 100) * 0.01f;
            float ov = P.Scalar(tn.P2, t, 0);
            double o = (ov % 360.0) / 360.0;
            if (o < 0) o += 1;
            double s = sv > 1 ? 1 + o : sv < 0 ? 0 + o : sv + o;
            double e = ev > 1 ? 1 + o : ev < 0 ? 0 + o : ev + o;
            if (s > e) { double tmp = s; s = e; e = tmp; }
            s = math.round(s * 10000) * 0.0001;
            e = math.round(e * 10000) * 0.0001;
            if (e == s)
            {
                for (int i = 0; i < count; i++) ranges[list[i]] = new int2(S.TempContours.Length, 0);
                return;
            }
            if ((e == 1 && s == 0) || (e == 0 && s == 1)) return;
            PathOps.Trim(ref this, list, count, ranges, s, e, tn.Mode);
        }

        // ------------------------------------------------------------------ styles

        void EmitStyle(in SPStyle style, float t, int* idx, SPShape* shapes, SPGroup* groups, int2* ranges,
            Affine2* gw, float4 clip, float layerOpacity)
        {
            ShapeNode sn = Nodes[style.Node];
            bool isStroke = sn.Kind == ShapeKind.Stroke || sn.Kind == ShapeKind.GradientStroke;
            bool isGradient = sn.Kind == ShapeKind.GradientFill || sn.Kind == ShapeKind.GradientStroke;
            if (sn.Kind == ShapeKind.NoStyle) return;

            float opacity = P.Scalar(isGradient ? sn.P0 : sn.P1, t, 100f) * 0.01f;
            if (opacity <= 0) return;
            float width = 0;
            if (isStroke)
            {
                width = P.Scalar(sn.Kind == ShapeKind.Stroke ? sn.P2 : sn.P6, t, 0);
                if (width <= 0) return;
            }

            Affine2 styleWorld = gw[style.Group];
            int cFirst = Out.Contours.Length;
            // Gather the shapes' contours, transformed into style space (strokes) or device space (fills).
            for (int k = 0; k < style.ShapesCount; k++)
            {
                int si = idx[style.ShapesFirst + k];
                SPShape sh = shapes[si];
                if (sh.Hidden != 0) continue;
                int2 r = ranges[si];
                if (r.y == 0) continue;
                // shape space -> style group chain
                Affine2 m = Affine2.Identity;
                int g = ((int*)S.ShapeSpace.GetUnsafePtr())[si];
                int guard = 0;
                while (g >= 0 && g != style.Group && guard++ < 256)
                {
                    m = m.Then(PGroupLocal(g));
                    g = groups[g].Parent;
                }
                if (!isStroke) m = m.Then(styleWorld);
                for (int c = 0; c < r.y; c++)
                {
                    Contour src = S.TempContours[r.x + c];
                    if (src.Count == 0) continue;
                    int first = Out.Points.Length;
                    for (int v = 0; v < src.Count * 3; v++) Out.Points.Add(m.Apply(Out.Points[src.First + v]));
                    Out.Contours.Add(new Contour { First = first, Count = src.Count, Closed = src.Closed });
                }
            }
            int cCount = Out.Contours.Length - cFirst;
            if (cCount == 0) return;

            int paint;
            if (isGradient) paint = AddGradientPaint(sn, t, styleWorld, opacity * layerOpacity);
            else
            {
                float4 c = P.Vec4(sn.P0, t, new float4(0, 0, 0, 1));
                // lottie-web: rgb(floor(c * 255))
                c.xyz = math.floor(math.saturate(c.xyz) * 255f) / 255f;
                c.w = 1;
                paint = AddSolidPaint(c, opacity * layerOpacity, Affine2.Identity);
            }

            var item = new DrawItem
            {
                Kind = isStroke ? ItemKind.Stroke : ItemKind.Fill,
                FillRule = (byte)(sn.Mode == 2 ? 1 : 0),
                ContourFirst = cFirst,
                ContourCount = cCount,
                ToDevice = isStroke ? styleWorld : Affine2.Identity,
                Paint = paint,
                Clip = clip,
                Group = _group,
            };
            if (isStroke)
            {
                item.FillRule = 0;
                item.StrokeWidth = width;
                item.Cap = sn.LineCap == 0 ? (byte)2 : sn.LineCap;
                item.Join = sn.Mode3 == 0 ? (byte)2 : sn.Mode3;
                item.MiterLimit = sn.Value;
                if (sn.DashCount > 0) EvalDashes(sn, t, ref item);
            }
            if (sn.BlendMode != 0)
            {
                int rg = BeginGroup(1f, sn.BlendMode, 0, clip, 4);
                item.Group = rg;
                AddDraw(item);
                EndGroup(rg);
            }
            else AddDraw(item);
        }

        Affine2 PGroupLocal(int g) => ((Affine2*)S.GroupLocal.GetUnsafePtr())[g];

        void EvalDashes(in ShapeNode sn, float t, ref DrawItem item)
        {
            // lottie-web DashProperty: every non-offset entry goes to the dash array in order.
            int first = Out.Dashes.Length;
            float offset = 0;
            float sum = 0;
            for (int i = 0; i < sn.DashCount; i++)
            {
                DashData d = DashTable[sn.DashFirst + i];
                float v = P.Scalar(d.Prop, t, 0);
                if (d.Kind == 2) { offset = v; continue; }
                Out.Dashes.Add(v);
                sum += v;
            }
            int n = Out.Dashes.Length - first;
            if (n == 0 || sum <= 0) { Out.Dashes.ResizeUninitialized(first); return; }
            // SVG: odd dash arrays are repeated to become even.
            if ((n & 1) == 1)
            {
                for (int i = 0; i < n; i++) Out.Dashes.Add(Out.Dashes[first + i]);
                n *= 2;
            }
            // Negative values invalidate the dash array (SVG).
            for (int i = 0; i < n; i++)
                if (Out.Dashes[first + i] < 0) { Out.Dashes.ResizeUninitialized(first); return; }
            item.DashFirst = first;
            item.DashCount = n;
            item.DashOffset = offset;
        }

        int AddGradientPaint(in ShapeNode sn, float t, in Affine2 styleWorld, float opacity)
        {
            // Geometry (style space).
            float2 p0 = P.Vec2(sn.P1, t, float2.zero);
            float2 p1 = P.Vec2(sn.P2, t, float2.zero);
            var paint = new PaintData
            {
                Kind = sn.Mode2 == 2 ? PaintKind.Radial : PaintKind.Linear,
                Color = new float4(1, 1, 1, 1),
                Opacity = opacity,
                DeviceToPaint = styleWorld.Inverse(),
                P0 = p0,
                P1 = p1,
            };
            if (paint.Kind == PaintKind.Radial)
            {
                float rad = math.sqrt((p0.x - p1.x) * (p0.x - p1.x) + (p0.y - p1.y) * (p0.y - p1.y));
                float ang = math.atan2(p1.y - p0.y, p1.x - p0.x);
                float percent = P.Scalar(sn.P3, t, 0) * 0.01f;
                if (percent >= 1) percent = 0.99f;
                else if (percent <= -1) percent = -0.99f;
                float dist = rad * percent;
                float a = math.radians(P.Scalar(sn.P4, t, 0));
                paint.Radius = rad;
                paint.Focal = new float2(math.cos(ang + a) * dist + p0.x, math.sin(ang + a) * dist + p0.y);
            }

            // Stops: lottie-web rounds color offsets to integer percents and colors to bytes.
            int colorCount = math.max(0, sn.Extra);
            int dim = P.Dim(sn.P5);
            float* g = stackalloc float[math.max(dim, 4)];
            P.Eval(sn.P5, t, g, dim);
            int colorFloats = math.min(colorCount * 4, dim);
            colorCount = colorFloats / 4;
            int opacityCount = (dim - colorFloats) / 2;

            paint.StopFirst = Out.PaintFloats.Length;
            if (colorCount == 0)
            {
                Out.PaintFloats.Add(0); Out.PaintFloats.Add(0); Out.PaintFloats.Add(0); Out.PaintFloats.Add(0); Out.PaintFloats.Add(1);
                paint.StopCount = 1;
                Out.Paints.Add(paint);
                return Out.Paints.Length - 1;
            }

            // Collect color stops (offset, rgb) with SVG monotonic offset fix-up.
            float* cOff = stackalloc float[colorCount];
            float3* cCol = stackalloc float3[colorCount];
            float last = 0;
            for (int i = 0; i < colorCount; i++)
            {
                float off = math.round(g[i * 4] * 100f) / 100f;
                off = math.clamp(off, 0, 1);
                if (i > 0 && off < last) off = last;
                last = off;
                cOff[i] = off;
                cCol[i] = math.round(math.saturate(new float3(g[i * 4 + 1], g[i * 4 + 2], g[i * 4 + 3])) * 255f) / 255f;
            }
            float* aOff = stackalloc float[math.max(1, opacityCount)];
            float* aVal = stackalloc float[math.max(1, opacityCount)];
            last = 0;
            for (int i = 0; i < opacityCount; i++)
            {
                float off = math.round(g[colorFloats + i * 2] * 100f) / 100f;
                off = math.clamp(off, 0, 1);
                if (i > 0 && off < last) off = last;
                last = off;
                aOff[i] = off;
                aVal[i] = math.saturate(g[colorFloats + i * 2 + 1]);
            }

            if (opacityCount == 0)
            {
                for (int i = 0; i < colorCount; i++) AddStop(cOff[i], cCol[i], 1);
                paint.StopCount = colorCount;
            }
            else
            {
                // Merge color and opacity breakpoints; both channels stay piecewise linear per interval.
                int ci = 0, ai = 0, n = 0;
                float prevOff = -1;
                while (ci < colorCount || ai < opacityCount)
                {
                    float off;
                    if (ai >= opacityCount || (ci < colorCount && cOff[ci] <= aOff[ai])) off = cOff[ci];
                    else off = aOff[ai];
                    // Emit the value(s) at this offset. Duplicate offsets (hard stops) keep both sides.
                    float3 colBefore = SampleColor(cOff, cCol, colorCount, off, false);
                    float3 colAfter = SampleColor(cOff, cCol, colorCount, off, true);
                    float aBefore = SampleAlpha(aOff, aVal, opacityCount, off, false);
                    float aAfter = SampleAlpha(aOff, aVal, opacityCount, off, true);
                    if (!(off == prevOff))
                    {
                        AddStop(off, colBefore, aBefore);
                        n++;
                        if (math.any(colBefore != colAfter) || aBefore != aAfter) { AddStop(off, colAfter, aAfter); n++; }
                        prevOff = off;
                    }
                    while (ci < colorCount && cOff[ci] == off) ci++;
                    while (ai < opacityCount && aOff[ai] == off) ai++;
                }
                paint.StopCount = n;
            }
            Out.Paints.Add(paint);
            return Out.Paints.Length - 1;
        }

        void AddStop(float off, float3 c, float a)
        {
            Out.PaintFloats.Add(off);
            Out.PaintFloats.Add(c.x);
            Out.PaintFloats.Add(c.y);
            Out.PaintFloats.Add(c.z);
            Out.PaintFloats.Add(a);
        }

        /// <summary>SVG gradient colour at offset (before/after a hard stop).</summary>
        static float3 SampleColor(float* off, float3* col, int n, float x, bool after)
        {
            if (x < off[0] || (x == off[0] && !after)) return col[0];
            if (x > off[n - 1] || (x == off[n - 1] && after)) return col[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                float a = off[i], b = off[i + 1];
                if (after ? (x >= a && x < b) : (x > a && x <= b))
                {
                    if (b <= a) return after ? col[i + 1] : col[i];
                    return math.lerp(col[i], col[i + 1], (x - a) / (b - a));
                }
            }
            return col[n - 1];
        }

        static float SampleAlpha(float* off, float* val, int n, float x, bool after)
        {
            if (x < off[0] || (x == off[0] && !after)) return val[0];
            if (x > off[n - 1] || (x == off[n - 1] && after)) return val[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                float a = off[i], b = off[i + 1];
                if (after ? (x >= a && x < b) : (x > a && x <= b))
                {
                    if (b <= a) return after ? val[i + 1] : val[i];
                    return math.lerp(val[i], val[i + 1], (x - a) / (b - a));
                }
            }
            return val[n - 1];
        }
    }
}
