using Mynell.Lottie.Model;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Scene
{
    /// <summary>
    /// Shape generators and shape modifiers (ports of lottie-web's ShapePropertyFactory and
    /// ShapeModifiers). Paths live in <c>Out.Points</c> as (v, in, out) triples; contour ranges are
    /// stored in <c>S.TempContours</c> and identified by int2(first, count).
    /// </summary>
    public static unsafe class PathOps
    {
        const float RoundCorner = LottieFormat.RoundCorner;
        const int CurveSegments = LottieFormat.CurveSegments;

        /// <summary>JavaScript Math.round (half up).</summary>
        public static double JsRound(double x) => math.floor(x + 0.5);

        // ------------------------------------------------------------------ path writer

        /// <summary>Incrementally built contour (vertices appended at the end of Out.Points).</summary>
        struct Writer
        {
            public int First;
            public int Count;
            public bool Closed;
        }

        static Writer Begin(ref SceneBuilder b)
        {
            return new Writer { First = b.Out.Points.Length, Count = 0 };
        }

        /// <summary>lottie-web ShapePath.setXYAt (type: 0 v, 1 i, 2 o).</summary>
        static void SetXY(ref SceneBuilder b, ref Writer w, float2 p, int type, int pos)
        {
            while (w.Count <= pos)
            {
                b.Out.Points.Add(float2.zero);
                b.Out.Points.Add(float2.zero);
                b.Out.Points.Add(float2.zero);
                w.Count++;
            }
            b.Out.Points[w.First + pos * 3 + type] = p;
        }

        /// <summary>lottie-web setTripleAt(v, o, i).</summary>
        static void SetTriple(ref SceneBuilder b, ref Writer w, float2 v, float2 o, float2 i, int pos)
        {
            SetXY(ref b, ref w, v, 0, pos);
            SetXY(ref b, ref w, o, 2, pos);
            SetXY(ref b, ref w, i, 1, pos);
        }

        static void End(ref SceneBuilder b, in Writer w)
        {
            b.S.TempContours.Add(new Contour { First = w.First, Count = w.Count, Closed = (byte)(w.Closed ? 1 : 0) });
        }

        static float2 V(ref SceneBuilder b, in Contour c, int k) => b.Out.Points[c.First + k * 3];
        static float2 I(ref SceneBuilder b, in Contour c, int k) => b.Out.Points[c.First + k * 3 + 1];
        static float2 O(ref SceneBuilder b, in Contour c, int k) => b.Out.Points[c.First + k * 3 + 2];

        // ------------------------------------------------------------------ generators

        public static void Rect(ref SceneBuilder b, in ShapeNode n, float t)
        {
            float2 p = b.P.Vec2(n.P0, t, float2.zero);
            float2 size = b.P.Vec2(n.P1, t, float2.zero);
            float r = b.P.Scalar(n.P2, t, 0);
            float p0 = p.x, p1 = p.y;
            float v0 = size.x / 2, v1 = size.y / 2;
            float round = math.min(math.min(v0, v1), r);
            float cPoint = round * (1 - RoundCorner);
            var w = Begin(ref b);
            w.Closed = true;
            if (n.Direction == 2 || n.Direction == 1)
            {
                SetTriple(ref b, ref w, new float2(p0 + v0, p1 - v1 + round), new float2(p0 + v0, p1 - v1 + round), new float2(p0 + v0, p1 - v1 + cPoint), 0);
                SetTriple(ref b, ref w, new float2(p0 + v0, p1 + v1 - round), new float2(p0 + v0, p1 + v1 - cPoint), new float2(p0 + v0, p1 + v1 - round), 1);
                if (round != 0)
                {
                    SetTriple(ref b, ref w, new float2(p0 + v0 - round, p1 + v1), new float2(p0 + v0 - round, p1 + v1), new float2(p0 + v0 - cPoint, p1 + v1), 2);
                    SetTriple(ref b, ref w, new float2(p0 - v0 + round, p1 + v1), new float2(p0 - v0 + cPoint, p1 + v1), new float2(p0 - v0 + round, p1 + v1), 3);
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 + v1 - round), new float2(p0 - v0, p1 + v1 - round), new float2(p0 - v0, p1 + v1 - cPoint), 4);
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 - v1 + round), new float2(p0 - v0, p1 - v1 + cPoint), new float2(p0 - v0, p1 - v1 + round), 5);
                    SetTriple(ref b, ref w, new float2(p0 - v0 + round, p1 - v1), new float2(p0 - v0 + round, p1 - v1), new float2(p0 - v0 + cPoint, p1 - v1), 6);
                    SetTriple(ref b, ref w, new float2(p0 + v0 - round, p1 - v1), new float2(p0 + v0 - cPoint, p1 - v1), new float2(p0 + v0 - round, p1 - v1), 7);
                }
                else
                {
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 + v1), new float2(p0 - v0 + cPoint, p1 + v1), new float2(p0 - v0, p1 + v1), 2);
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 - v1), new float2(p0 - v0, p1 - v1 + cPoint), new float2(p0 - v0, p1 - v1), 3);
                }
            }
            else
            {
                SetTriple(ref b, ref w, new float2(p0 + v0, p1 - v1 + round), new float2(p0 + v0, p1 - v1 + cPoint), new float2(p0 + v0, p1 - v1 + round), 0);
                if (round != 0)
                {
                    SetTriple(ref b, ref w, new float2(p0 + v0 - round, p1 - v1), new float2(p0 + v0 - round, p1 - v1), new float2(p0 + v0 - cPoint, p1 - v1), 1);
                    SetTriple(ref b, ref w, new float2(p0 - v0 + round, p1 - v1), new float2(p0 - v0 + cPoint, p1 - v1), new float2(p0 - v0 + round, p1 - v1), 2);
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 - v1 + round), new float2(p0 - v0, p1 - v1 + round), new float2(p0 - v0, p1 - v1 + cPoint), 3);
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 + v1 - round), new float2(p0 - v0, p1 + v1 - cPoint), new float2(p0 - v0, p1 + v1 - round), 4);
                    SetTriple(ref b, ref w, new float2(p0 - v0 + round, p1 + v1), new float2(p0 - v0 + round, p1 + v1), new float2(p0 - v0 + cPoint, p1 + v1), 5);
                    SetTriple(ref b, ref w, new float2(p0 + v0 - round, p1 + v1), new float2(p0 + v0 - cPoint, p1 + v1), new float2(p0 + v0 - round, p1 + v1), 6);
                    SetTriple(ref b, ref w, new float2(p0 + v0, p1 + v1 - round), new float2(p0 + v0, p1 + v1 - round), new float2(p0 + v0, p1 + v1 - cPoint), 7);
                }
                else
                {
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 - v1), new float2(p0 - v0 + cPoint, p1 - v1), new float2(p0 - v0, p1 - v1), 1);
                    SetTriple(ref b, ref w, new float2(p0 - v0, p1 + v1), new float2(p0 - v0, p1 + v1 - cPoint), new float2(p0 - v0, p1 + v1), 2);
                    SetTriple(ref b, ref w, new float2(p0 + v0, p1 + v1), new float2(p0 + v0 - cPoint, p1 + v1), new float2(p0 + v0, p1 + v1), 3);
                }
            }
            End(ref b, w);
        }

        public static void Ellipse(ref SceneBuilder b, in ShapeNode n, float t)
        {
            float2 p = b.P.Vec2(n.P0, t, float2.zero);
            float2 size = b.P.Vec2(n.P1, t, float2.zero);
            float p0 = p.x, p1 = p.y;
            float s0 = size.x / 2, s1 = size.y / 2;
            bool cw = n.Direction != 3;
            const float c = RoundCorner;
            var w = Begin(ref b);
            w.Closed = true;
            SetXY(ref b, ref w, new float2(p0, p1 - s1), 0, 0);
            SetXY(ref b, ref w, new float2(cw ? p0 + s0 : p0 - s0, p1), 0, 1);
            SetXY(ref b, ref w, new float2(p0, p1 + s1), 0, 2);
            SetXY(ref b, ref w, new float2(cw ? p0 - s0 : p0 + s0, p1), 0, 3);
            SetXY(ref b, ref w, new float2(cw ? p0 - s0 * c : p0 + s0 * c, p1 - s1), 1, 0);
            SetXY(ref b, ref w, new float2(cw ? p0 + s0 : p0 - s0, p1 - s1 * c), 1, 1);
            SetXY(ref b, ref w, new float2(cw ? p0 + s0 * c : p0 - s0 * c, p1 + s1), 1, 2);
            SetXY(ref b, ref w, new float2(cw ? p0 - s0 : p0 + s0, p1 + s1 * c), 1, 3);
            SetXY(ref b, ref w, new float2(cw ? p0 + s0 * c : p0 - s0 * c, p1 - s1), 2, 0);
            SetXY(ref b, ref w, new float2(cw ? p0 + s0 : p0 - s0, p1 + s1 * c), 2, 1);
            SetXY(ref b, ref w, new float2(cw ? p0 - s0 * c : p0 + s0 * c, p1 + s1), 2, 2);
            SetXY(ref b, ref w, new float2(cw ? p0 - s0 : p0 + s0, p1 - s1 * c), 2, 3);
            End(ref b, w);
        }

        public static void Star(ref SceneBuilder b, in ShapeNode n, float t)
        {
            float2 p = b.P.Vec2(n.P0, t, float2.zero);
            float outerRad = b.P.Scalar(n.P1, t, 0);
            float outerRound = b.P.Scalar(n.P2, t, 0) * 0.01f;
            float rot = math.radians(b.P.Scalar(n.P3, t, 0));
            float pts = b.P.Scalar(n.P4, t, 5);
            float dir = n.Direction == 3 ? -1 : 1;
            var w = Begin(ref b);
            w.Closed = true;
            if (n.Mode == 1)
            {
                float innerRad = b.P.Scalar(n.P5, t, 0);
                float innerRound = b.P.Scalar(n.P6, t, 0) * 0.01f;
                int numPts = (int)math.floor(pts) * 2;
                if (numPts <= 0) { End(ref b, w); return; }
                double angle = math.PI_DBL * 2 / numPts;
                bool longFlag = true;
                double longPerim = 2 * math.PI_DBL * outerRad / (numPts * 2);
                double shortPerim = 2 * math.PI_DBL * innerRad / (numPts * 2);
                double currentAng = -math.PI_DBL / 2 + rot;
                for (int i = 0; i < numPts; i++)
                {
                    double rad = longFlag ? outerRad : innerRad;
                    double roundness = longFlag ? outerRound : innerRound;
                    double perim = longFlag ? longPerim : shortPerim;
                    double x = rad * math.cos(currentAng);
                    double y = rad * math.sin(currentAng);
                    double len = math.sqrt(x * x + y * y);
                    double ox = x == 0 && y == 0 ? 0 : y / len;
                    double oy = x == 0 && y == 0 ? 0 : -x / len;
                    x += p.x;
                    y += p.y;
                    double k = perim * roundness * dir;
                    SetTriple(ref b, ref w, new float2((float)x, (float)y), new float2((float)(x - ox * k), (float)(y - oy * k)),
                        new float2((float)(x + ox * k), (float)(y + oy * k)), i);
                    longFlag = !longFlag;
                    currentAng += angle * dir;
                }
            }
            else
            {
                int numPts = (int)math.floor(pts);
                if (numPts <= 0) { End(ref b, w); return; }
                double angle = math.PI_DBL * 2 / numPts;
                double rad = outerRad;
                double roundness = outerRound;
                double perim = 2 * math.PI_DBL * rad / (numPts * 4);
                double currentAng = -math.PI_DBL * 0.5 + rot;
                for (int i = 0; i < numPts; i++)
                {
                    double x = rad * math.cos(currentAng);
                    double y = rad * math.sin(currentAng);
                    double len = math.sqrt(x * x + y * y);
                    double ox = x == 0 && y == 0 ? 0 : y / len;
                    double oy = x == 0 && y == 0 ? 0 : -x / len;
                    x += p.x;
                    y += p.y;
                    double k = perim * roundness * dir;
                    SetTriple(ref b, ref w, new float2((float)x, (float)y), new float2((float)(x - ox * k), (float)(y - oy * k)),
                        new float2((float)(x + ox * k), (float)(y + oy * k)), i);
                    currentAng += angle * dir;
                }
            }
            End(ref b, w);
        }

        // ------------------------------------------------------------------ round corners

        public static int2 RoundCorners(ref SceneBuilder b, int2 range, float round)
        {
            int outFirst = b.S.TempContours.Length;
            for (int ci = 0; ci < range.y; ci++)
            {
                Contour path = b.S.TempContours[range.x + ci];
                var w = Begin(ref b);
                w.Closed = path.Closed != 0;
                int len = path.Count;
                int index = 0;
                for (int i = 0; i < len; i++)
                {
                    float2 cv = V(ref b, path, i), co = O(ref b, path, i), cI = I(ref b, path, i);
                    if (cv.x == co.x && cv.y == co.y && cv.x == cI.x && cv.y == cI.y)
                    {
                        if ((i == 0 || i == len - 1) && path.Closed == 0)
                        {
                            SetTriple(ref b, ref w, cv, co, cI, index);
                            index++;
                        }
                        else
                        {
                            float2 closer = i == 0 ? V(ref b, path, len - 1) : V(ref b, path, i - 1);
                            float distance = math.sqrt(math.pow(cv.x - closer.x, 2) + math.pow(cv.y - closer.y, 2));
                            float newPosPerc = distance != 0 ? math.min(distance / 2, round) / distance : 0;
                            float iX = cv.x + (closer.x - cv.x) * newPosPerc;
                            float vX = iX;
                            float iY = cv.y - (cv.y - closer.y) * newPosPerc;
                            float vY = iY;
                            float oX = vX - (vX - cv.x) * RoundCorner;
                            float oY = vY - (vY - cv.y) * RoundCorner;
                            SetTriple(ref b, ref w, new float2(vX, vY), new float2(oX, oY), new float2(iX, iY), index);
                            index++;
                            closer = i == len - 1 ? V(ref b, path, 0) : V(ref b, path, i + 1);
                            distance = math.sqrt(math.pow(cv.x - closer.x, 2) + math.pow(cv.y - closer.y, 2));
                            newPosPerc = distance != 0 ? math.min(distance / 2, round) / distance : 0;
                            oX = cv.x + (closer.x - cv.x) * newPosPerc;
                            vX = oX;
                            oY = cv.y + (closer.y - cv.y) * newPosPerc;
                            vY = oY;
                            iX = vX - (vX - cv.x) * RoundCorner;
                            iY = vY - (vY - cv.y) * RoundCorner;
                            SetTriple(ref b, ref w, new float2(vX, vY), new float2(oX, oY), new float2(iX, iY), index);
                            index++;
                        }
                    }
                    else
                    {
                        SetTriple(ref b, ref w, cv, co, cI, index);
                        index++;
                    }
                }
                End(ref b, w);
            }
            return new int2(outFirst, b.S.TempContours.Length - outFirst);
        }

        // ------------------------------------------------------------------ pucker & bloat

        public static int2 PuckerBloat(ref SceneBuilder b, int2 range, float amount)
        {
            int outFirst = b.S.TempContours.Length;
            float percent = amount / 100f;
            for (int ci = 0; ci < range.y; ci++)
            {
                Contour path = b.S.TempContours[range.x + ci];
                int len = path.Count;
                float2 center = float2.zero;
                for (int i = 0; i < len; i++) center += V(ref b, path, i);
                if (len > 0) center /= len;
                var w = Begin(ref b);
                w.Closed = path.Closed != 0;
                for (int i = 0; i < len; i++)
                {
                    float2 v = V(ref b, path, i), o = O(ref b, path, i), iv = I(ref b, path, i);
                    float2 nv = v + (center - v) * percent;
                    float2 no = o + (center - o) * -percent;
                    float2 ni = iv + (center - iv) * -percent;
                    SetTriple(ref b, ref w, nv, no, ni, i);
                }
                End(ref b, w);
            }
            return new int2(outFirst, b.S.TempContours.Length - outFirst);
        }

        // ------------------------------------------------------------------ zig zag

        struct PolyBez
        {
            public double2 a, bb, c, d;
            public double2 p0, p1, p2, p3;
        }

        static bool FloatEqual(double a, double b) => math.abs(a - b) * 100000 <= math.min(math.abs(a), math.abs(b));
        static bool FloatZero(double f) => math.abs(f) <= 0.00001;
        static double2 LerpPoint(double2 p0, double2 p1, double t) => new double2(p0.x * (1 - t) + p1.x * t, p0.y * (1 - t) + p1.y * t);
        static bool PointEqual(double2 p1, double2 p2) => FloatEqual(p1.x, p2.x) && FloatEqual(p1.y, p2.y);

        static PolyBez MakeBez(double2 p0, double2 p1, double2 p2, double2 p3, bool linearize)
        {
            if (linearize && PointEqual(p0, p1)) p1 = LerpPoint(p0, p3, 1.0 / 3.0);
            if (linearize && PointEqual(p2, p3)) p2 = LerpPoint(p0, p3, 2.0 / 3.0);
            var r = new PolyBez
            {
                a = -p0 + 3 * p1 - 3 * p2 + p3,
                bb = 3 * p0 - 6 * p1 + 3 * p2,
                c = -3 * p0 + 3 * p1,
                d = p0,
                p0 = p0, p1 = p1, p2 = p2, p3 = p3,
            };
            return r;
        }

        static double2 BezPoint(in PolyBez z, double t) => ((z.a * t + z.bb) * t + z.c) * t + z.d;
        static double2 BezDerivative(in PolyBez z, double t) => (3 * t * z.a + 2 * z.bb) * t + z.c;
        static double NormalAngle(in PolyBez z, double t) { var p = BezDerivative(z, t); return math.atan2(p.x, p.y); }

        static PolyBez ShapeSegment(ref SceneBuilder b, in Contour path, int index)
        {
            int next = (index + 1) % path.Count;
            return MakeBez(V(ref b, path, index), O(ref b, path, index), I(ref b, path, next), V(ref b, path, next), true);
        }

        static void ZzSetPoint(ref SceneBuilder b, ref Writer w, double2 point, double angle, double direction, double amplitude, double outAmp, double inAmp)
        {
            double angO = angle - math.PI_DBL / 2;
            double angI = angle + math.PI_DBL / 2;
            double px = point.x + math.cos(angle) * direction * amplitude;
            double py = point.y - math.sin(angle) * direction * amplitude;
            SetTriple(ref b, ref w, new float2((float)px, (float)py),
                new float2((float)(px + math.cos(angO) * outAmp), (float)(py - math.sin(angO) * outAmp)),
                new float2((float)(px + math.cos(angI) * inAmp), (float)(py - math.sin(angI) * inAmp)), w.Count);
        }

        static double ProjectingAngle(ref SceneBuilder b, in Contour path, int cur)
        {
            int prev = cur == 0 ? path.Count - 1 : cur - 1;
            int next = (cur + 1) % path.Count;
            float2 pp = V(ref b, path, prev), np = V(ref b, path, next);
            double2 vec = new double2(np.x - pp.x, np.y - pp.y);
            double rot = -math.PI_DBL * 0.5;
            double2 rv = new double2(math.cos(rot) * vec.x - math.sin(rot) * vec.y, math.sin(rot) * vec.x + math.cos(rot) * vec.y);
            return math.atan2(0, 1) - math.atan2(rv.y, rv.x);
        }

        static void ZzCorner(ref SceneBuilder b, ref Writer w, in Contour path, int cur, double amplitude, double frequency, float pointType, double direction)
        {
            double angle = ProjectingAngle(ref b, path, cur);
            float2 point = V(ref b, path, cur % path.Count);
            float2 prevPoint = V(ref b, path, cur == 0 ? path.Count - 1 : cur - 1);
            float2 nextPoint = V(ref b, path, (cur + 1) % path.Count);
            double prevDist = pointType == 2 ? math.distance((double2)point, (double2)prevPoint) : 0;
            double nextDist = pointType == 2 ? math.distance((double2)point, (double2)nextPoint) : 0;
            ZzSetPoint(ref b, ref w, point, angle, direction, amplitude, nextDist / ((frequency + 1) * 2), prevDist / ((frequency + 1) * 2));
        }

        static double ZzSegment(ref SceneBuilder b, ref Writer w, in PolyBez seg, double amplitude, int frequency, float pointType, double direction)
        {
            for (int i = 0; i < frequency; i++)
            {
                double t = (i + 1) / (double)(frequency + 1);
                double dist = pointType == 2 ? math.distance(seg.p3, seg.p0) : 0;
                double angle = NormalAngle(seg, t);
                double2 point = BezPoint(seg, t);
                ZzSetPoint(ref b, ref w, point, angle, direction, amplitude, dist / ((frequency + 1) * 2), dist / ((frequency + 1) * 2));
                direction = -direction;
            }
            return direction;
        }

        public static int2 ZigZag(ref SceneBuilder b, int2 range, float amplitude, int frequency, float pointType)
        {
            int outFirst = b.S.TempContours.Length;
            for (int ci = 0; ci < range.y; ci++)
            {
                Contour path = b.S.TempContours[range.x + ci];
                var w = Begin(ref b);
                w.Closed = path.Closed != 0;
                int count = path.Count;
                if (path.Closed == 0) count -= 1;
                if (count <= 0) { End(ref b, w); continue; }
                double direction = -1;
                PolyBez segment = ShapeSegment(ref b, path, 0);
                ZzCorner(ref b, ref w, path, 0, amplitude, frequency, pointType, direction);
                for (int i = 0; i < count; i++)
                {
                    direction = ZzSegment(ref b, ref w, segment, amplitude, frequency, pointType, -direction);
                    if (!(i == count - 1 && path.Closed == 0))
                        segment = ShapeSegment(ref b, path, (i + 1) % count);
                    ZzCorner(ref b, ref w, path, i + 1, amplitude, frequency, pointType, direction);
                }
                End(ref b, w);
            }
            return new int2(outFirst, b.S.TempContours.Length - outFirst);
        }

        // ------------------------------------------------------------------ offset path

        // lottie-web OffsetPathModifier + PolynomialBezier (split at inflections, offset control polygons, prune
        // intersections between consecutive segments, join with miter / round / bevel).

        const double RoundCornerD = 0.5519;

        static PolyBez SinglePoint(double2 p) => MakeBez(p, p, p, p, false);

        static double TangentAngle(in PolyBez z, double t)
        {
            double2 p = BezDerivative(z, t);
            return math.atan2(p.y, p.x);
        }

        static int InflectionPoints(in PolyBez z, double* t)
        {
            double denom = z.a.y * z.bb.x - z.a.x * z.bb.y;
            if (FloatZero(denom)) return 0;
            double tcusp = -0.5 * (z.a.y * z.c.x - z.a.x * z.c.y) / denom;
            double square = tcusp * tcusp - 1.0 / 3.0 * (z.bb.y * z.c.x - z.bb.x * z.c.y) / denom;
            if (square < 0) return 0;
            double root = math.sqrt(square);
            if (FloatZero(root))
            {
                if (root > 0 && root < 1) { t[0] = tcusp; return 1; }
                return 0;
            }
            int n = 0;
            double r0 = tcusp - root, r1 = tcusp + root;
            if (r0 > 0 && r0 < 1) t[n++] = r0;
            if (r1 > 0 && r1 < 1) t[n++] = r1;
            return n;
        }

        static void Split(in PolyBez z, double t, out PolyBez left, out PolyBez right)
        {
            if (t <= 0) { left = SinglePoint(z.p0); right = z; return; }
            if (t >= 1) { left = z; right = SinglePoint(z.p3); return; }
            double2 p10 = LerpPoint(z.p0, z.p1, t);
            double2 p11 = LerpPoint(z.p1, z.p2, t);
            double2 p12 = LerpPoint(z.p2, z.p3, t);
            double2 p20 = LerpPoint(p10, p11, t);
            double2 p21 = LerpPoint(p11, p12, t);
            double2 p3 = LerpPoint(p20, p21, t);
            left = MakeBez(z.p0, p10, p20, p3, true);
            right = MakeBez(p3, p21, p12, z.p3, true);
        }

        /// <summary>(min, max) of one coordinate over the curve.</summary>
        static double2 Extrema(in PolyBez z, int comp)
        {
            double min = z.p0[comp], max = z.p3[comp];
            if (min > max) { double e = max; max = min; min = e; }
            double qa = 3 * z.a[comp], qb = 2 * z.bb[comp], qc = z.c[comp];
            if (qa == 0) return new double2(min, max);
            double s = qb * qb - 4 * qa * qc;
            if (s < 0) return new double2(min, max);
            double single = -qb / (2 * qa);
            if (s == 0)
            {
                ExtremaCandidate(z, comp, single, ref min, ref max);
                return new double2(min, max);
            }
            double delta = math.sqrt(s) / (2 * qa);
            ExtremaCandidate(z, comp, single - delta, ref min, ref max);
            ExtremaCandidate(z, comp, single + delta, ref min, ref max);
            return new double2(min, max);
        }

        static void ExtremaCandidate(in PolyBez z, int comp, double f, ref double min, ref double max)
        {
            if (!(f > 0 && f < 1)) return;
            double val = BezPoint(z, f)[comp];
            if (val < min) min = val;
            else if (val > max) max = val;
        }

        struct IsectData
        {
            public double Cx, Cy, Width, Height, T, T1, T2;
            public PolyBez Bez;
        }

        static IsectData MakeIsect(in PolyBez bez, double t1, double t2)
        {
            double2 ex = Extrema(bez, 0), ey = Extrema(bez, 1);
            return new IsectData
            {
                Cx = (ex.y + ex.x) / 2,
                Cy = (ey.y + ey.x) / 2,
                Width = ex.y - ex.x,
                Height = ey.y - ey.x,
                Bez = bez,
                T = (t1 + t2) / 2,
                T1 = t1,
                T2 = t2,
            };
        }

        static bool BoxIntersect(in IsectData b1, in IsectData b2) =>
            math.abs(b1.Cx - b2.Cx) * 2 < b1.Width + b2.Width && math.abs(b1.Cy - b2.Cy) * 2 < b1.Height + b2.Height;

        /// <summary>First <paramref name="maxResults"/> intersections (t on a, t on b) in lottie-web's recursion order.</summary>
        static int Intersections(in PolyBez a, in PolyBez b, double tolerance, int maxRecursion, double2* results, int maxResults)
        {
            const int Cap = 64;
            IsectData* s1 = stackalloc IsectData[Cap];
            IsectData* s2 = stackalloc IsectData[Cap];
            int* depth = stackalloc int[Cap];
            s1[0] = MakeIsect(a, 0, 1);
            s2[0] = MakeIsect(b, 0, 1);
            depth[0] = 0;
            int sp = 1, n = 0;
            while (sp > 0)
            {
                sp--;
                IsectData d1 = s1[sp], d2 = s2[sp];
                int dp = depth[sp];
                if (!BoxIntersect(d1, d2)) continue;
                if (dp >= maxRecursion || (d1.Width <= tolerance && d1.Height <= tolerance && d2.Width <= tolerance && d2.Height <= tolerance))
                {
                    results[n++] = new double2(d1.T, d2.T);
                    if (n >= maxResults) return n;
                    continue;
                }
                if (sp + 4 > Cap) continue;
                Split(d1.Bez, 0.5, out PolyBez l1, out PolyBez r1);
                Split(d2.Bez, 0.5, out PolyBez l2, out PolyBez r2);
                IsectData d1a = MakeIsect(l1, d1.T1, d1.T), d1b = MakeIsect(r1, d1.T, d1.T2);
                IsectData d2a = MakeIsect(l2, d2.T1, d2.T), d2b = MakeIsect(r2, d2.T, d2.T2);
                // Pushed in reverse so that (first, first) is visited first.
                s1[sp] = d1b; s2[sp] = d2b; depth[sp++] = dp + 1;
                s1[sp] = d1b; s2[sp] = d2a; depth[sp++] = dp + 1;
                s1[sp] = d1a; s2[sp] = d2b; depth[sp++] = dp + 1;
                s1[sp] = d1a; s2[sp] = d2a; depth[sp++] = dp + 1;
            }
            return n;
        }

        static bool GetIntersection(in PolyBez a, in PolyBez b, out double2 result)
        {
            double2* found = stackalloc double2[2];
            int n = Intersections(a, b, 2, 7, found, 2);
            int k = n > 0 && FloatEqual(found[0].x, 1) ? 1 : 0;
            result = n > k ? found[k] : default;
            return n > k;
        }

        static double3 Cross(double3 a, double3 b) =>
            new double3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);

        static bool LineIntersection(double2 start1, double2 end1, double2 start2, double2 end2, out double2 result)
        {
            double3 r = Cross(Cross(new double3(start1, 1), new double3(end1, 1)), Cross(new double3(start2, 1), new double3(end2, 1)));
            if (FloatZero(r.z)) { result = default; return false; }
            result = new double2(r.x / r.z, r.y / r.z);
            return true;
        }

        static double2 PolarOffset(double2 p, double angle, double length) =>
            new double2(p.x + math.cos(angle) * length, p.y - math.sin(angle) * length);

        static double PointDistance(double2 p1, double2 p2)
        {
            double dx = p1.x - p2.x, dy = p1.y - p2.y;
            return math.sqrt(dx * dx + dy * dy);
        }

        static void LinearOffset(double2 p1, double2 p2, double amount, out double2 o1, out double2 o2)
        {
            double angle = math.atan2(p2.x - p1.x, p2.y - p1.y);
            o1 = PolarOffset(p1, angle, amount);
            o2 = PolarOffset(p2, angle, amount);
        }

        static PolyBez OffsetSegment(in PolyBez seg, double amount)
        {
            LinearOffset(seg.p0, seg.p1, amount, out double2 p0, out double2 p1a);
            LinearOffset(seg.p1, seg.p2, amount, out double2 p1b, out double2 p2b);
            LinearOffset(seg.p2, seg.p3, amount, out double2 p2a, out double2 p3);
            if (!LineIntersection(p0, p1a, p1b, p2b, out double2 p1)) p1 = p1a;
            if (!LineIntersection(p2a, p3, p1b, p2b, out double2 p2)) p2 = p2a;
            return MakeBez(p0, p1, p2, p3, false);
        }

        static void OffsetSegmentSplit(in PolyBez segment, double amount, ref NativeList<PolyBez> segs, ref NativeList<int2> groups)
        {
            double* flex = stackalloc double[2];
            int n = InflectionPoints(segment, flex);
            int first = segs.Length;
            if (n == 0)
            {
                segs.Add(OffsetSegment(segment, amount));
            }
            else if (n == 1 || FloatEqual(flex[1], 1))
            {
                Split(segment, flex[0], out PolyBez left, out PolyBez right);
                segs.Add(OffsetSegment(left, amount));
                segs.Add(OffsetSegment(right, amount));
            }
            else
            {
                Split(segment, flex[0], out PolyBez left, out PolyBez rest);
                double t = (flex[1] - flex[0]) / (1 - flex[0]);
                Split(rest, t, out PolyBez mid, out PolyBez right);
                segs.Add(OffsetSegment(left, amount));
                segs.Add(OffsetSegment(mid, amount));
                segs.Add(OffsetSegment(right, amount));
            }
            groups.Add(new int2(first, segs.Length - first));
        }

        static void PruneSegmentIntersection(ref NativeList<PolyBez> segs, ref NativeList<int2> groups, int ia, int ib)
        {
            int2 a = groups[ia], bg = groups[ib];
            PolyBez aFirst = segs[a.x], aLast = segs[a.x + a.y - 1];
            PolyBez bFirst = segs[bg.x], bLast = segs[bg.x + bg.y - 1];
            if (GetIntersection(aLast, bFirst, out double2 it))
            {
                Split(aLast, it.x, out PolyBez l, out _);
                Split(bFirst, it.y, out _, out PolyBez r);
                segs[a.x + a.y - 1] = l;
                segs[bg.x] = r;
            }
            if (a.y > 1 && bg.y > 1 && GetIntersection(aFirst, bLast, out it))
            {
                Split(aFirst, it.x, out PolyBez l, out _);
                Split(bLast, it.y, out _, out PolyBez r);
                segs[a.x] = l;
                groups[ia] = new int2(a.x, 1);
                segs[bg.x] = r;
                groups[ib] = new int2(bg.x, 1);
            }
        }

        static double2 JoinLines(ref SceneBuilder b, ref Writer w, in PolyBez seg1, in PolyBez seg2, int lineJoin, double miterLimit)
        {
            double2 p0 = seg1.p3, p1 = seg2.p0;
            if (lineJoin == 3) return p0;                 // bevel
            if (PointEqual(p0, p1)) return p0;            // connected
            if (lineJoin == 2)                            // round
            {
                double angleOut = -TangentAngle(seg1, 1);
                double angleIn = -TangentAngle(seg2, 0) + math.PI_DBL;
                bool hasCenter = LineIntersection(p0, PolarOffset(p0, angleOut + math.PI_DBL / 2, 100),
                    p1, PolarOffset(p1, angleOut + math.PI_DBL / 2, 100), out double2 center);
                double radius = hasCenter ? PointDistance(center, p0) : PointDistance(p0, p1) / 2;
                double2 tan = PolarOffset(p0, angleOut, 2 * radius * RoundCornerD);
                if (w.Count > 0) SetXY(ref b, ref w, (float2)tan, 2, w.Count - 1);
                tan = PolarOffset(p1, angleIn, 2 * radius * RoundCornerD);
                SetTriple(ref b, ref w, (float2)p1, (float2)p1, (float2)tan, w.Count);
                return p1;
            }
            // miter
            double2 t0 = PointEqual(p0, seg1.p2) ? seg1.p0 : seg1.p2;
            double2 t1 = PointEqual(p1, seg2.p1) ? seg2.p3 : seg2.p1;
            if (LineIntersection(t0, p0, p1, t1, out double2 x) && PointDistance(x, p0) < miterLimit)
            {
                SetTriple(ref b, ref w, (float2)x, (float2)x, (float2)x, w.Count);
                return x;
            }
            return p0;
        }

        static PolyBez ShapeSegmentInverted(ref SceneBuilder b, in Contour path, int index)
        {
            int next = (index + 1) % path.Count;
            return MakeBez(V(ref b, path, next), I(ref b, path, next), O(ref b, path, index), V(ref b, path, index), true);
        }

        public static int2 OffsetPath(ref SceneBuilder b, int2 range, float amount, int lineJoin, float miterLimit)
        {
            int outFirst = b.S.TempContours.Length;
            var segs = new NativeList<PolyBez>(32, Allocator.Temp);
            var groups = new NativeList<int2>(16, Allocator.Temp);
            for (int ci = 0; ci < range.y; ci++)
            {
                Contour path = b.S.TempContours[range.x + ci];
                bool closed = path.Closed != 0;
                segs.Clear();
                groups.Clear();
                int count = closed ? path.Count : path.Count - 1;
                for (int i = 0; i < count; i++) OffsetSegmentSplit(ShapeSegment(ref b, path, i), amount, ref segs, ref groups);
                if (!closed)
                    for (int i = count - 1; i >= 0; i--) OffsetSegmentSplit(ShapeSegmentInverted(ref b, path, i), amount, ref segs, ref groups);

                for (int i = 1; i < groups.Length; i++) PruneSegmentIntersection(ref segs, ref groups, i - 1, i);
                if (groups.Length > 1) PruneSegmentIntersection(ref segs, ref groups, groups.Length - 1, 0);

                var w = Begin(ref b);
                w.Closed = closed;
                bool hasLastPoint = false;
                double2 lastPoint = default;
                PolyBez lastSeg = default;
                for (int i = 0; i < groups.Length; i++)
                {
                    int2 g = groups[i];
                    if (i > 0)
                    {
                        lastPoint = JoinLines(ref b, ref w, lastSeg, segs[g.x], lineJoin, miterLimit);
                        hasLastPoint = true;
                    }
                    lastSeg = segs[g.x + g.y - 1];
                    for (int j = 0; j < g.y; j++)
                    {
                        PolyBez seg = segs[g.x + j];
                        if (hasLastPoint && PointEqual(seg.p0, lastPoint)) SetXY(ref b, ref w, (float2)seg.p1, 2, w.Count - 1);
                        else SetTriple(ref b, ref w, (float2)seg.p0, (float2)seg.p1, (float2)seg.p0, w.Count);
                        SetTriple(ref b, ref w, (float2)seg.p3, (float2)seg.p3, (float2)seg.p2, w.Count);
                        lastPoint = seg.p3;
                        hasLastPoint = true;
                    }
                }
                if (groups.Length > 0) JoinLines(ref b, ref w, lastSeg, segs[groups[0].x], lineJoin, miterLimit);
                End(ref b, w);
            }
            segs.Dispose();
            groups.Dispose();
            return new int2(outFirst, b.S.TempContours.Length - outFirst);
        }

        // ------------------------------------------------------------------ trim paths

        /// <summary>Per-contour length data inside S.Lengths: [total, (addedLength, 150 lengths) per segment].</summary>
        struct PathLen
        {
            public int Offset;     // index in Lengths of the first segment block
            public int Segments;
            public double Total;
        }

        const int SegBlock = CurveSegments + 1;

        static double BuildSegmentLength(ref SceneBuilder b, float2 pt1, float2 pt2, float2 pt3, float2 pt4)
        {
            // lottie-web getBezierLength (pt3 = out of start, pt4 = in of end, both absolute).
            int at = b.S.Lengths.Length;
            b.S.Lengths.ResizeUninitialized(at + SegBlock);
            float* dst = (float*)b.S.Lengths.GetUnsafePtr() + at;
            double added = 0;
            double lx = 0, ly = 0;
            for (int k = 0; k < CurveSegments; k++)
            {
                double perc = k / (double)(CurveSegments - 1);
                double u = 1 - perc;
                double x = u * u * u * pt1.x + 3 * u * u * perc * pt3.x + 3 * u * perc * perc * pt4.x + perc * perc * perc * pt2.x;
                double y = u * u * u * pt1.y + 3 * u * u * perc * pt3.y + 3 * u * perc * perc * pt4.y + perc * perc * perc * pt2.y;
                if (k > 0)
                {
                    double dd = (x - lx) * (x - lx) + (y - ly) * (y - ly);
                    if (dd != 0) added += math.sqrt(dd);
                }
                dst[1 + k] = (float)added;
                lx = x; ly = y;
            }
            dst[0] = (float)added;
            return (float)added;
        }

        static PathLen BuildContourLengths(ref SceneBuilder b, in Contour c)
        {
            var pl = new PathLen { Offset = b.S.Lengths.Length };
            int len = c.Count;
            double total = 0;
            for (int i = 0; i < len - 1; i++)
            {
                total += BuildSegmentLength(ref b, V(ref b, c, i), V(ref b, c, i + 1), O(ref b, c, i), I(ref b, c, i + 1));
                pl.Segments++;
            }
            if (c.Closed != 0 && len > 0)
            {
                total += BuildSegmentLength(ref b, V(ref b, c, len - 1), V(ref b, c, 0), O(ref b, c, len - 1), I(ref b, c, 0));
                pl.Segments++;
            }
            pl.Total = total;
            return pl;
        }

        static float SegAdded(ref SceneBuilder b, in PathLen pl, int seg) => b.S.Lengths[pl.Offset + seg * SegBlock];

        /// <summary>lottie-web bez.getDistancePerc.</summary>
        static double DistancePerc(ref SceneBuilder b, int blockOffset, double perc)
        {
            float* lengths = (float*)b.S.Lengths.GetUnsafeReadOnlyPtr() + blockOffset + 1;
            float addedLength = b.S.Lengths[blockOffset];
            const int len = CurveSegments;
            int initPos = (int)math.floor((len - 1) * perc);
            double lengthPos = perc * addedLength;
            double lPerc = 0;
            if (initPos == len - 1 || initPos == 0 || lengthPos == lengths[math.clamp(initPos, 0, len - 1)])
                return PercentAt(math.clamp(initPos, 0, len - 1));
            int dir = lengths[initPos] > lengthPos ? -1 : 1;
            bool flag = true;
            while (flag)
            {
                if (lengths[initPos] <= lengthPos && lengths[initPos + 1] > lengthPos)
                {
                    lPerc = (lengthPos - lengths[initPos]) / (lengths[initPos + 1] - lengths[initPos]);
                    flag = false;
                }
                else initPos += dir;
                if (initPos < 0 || initPos >= len - 1)
                {
                    if (initPos == len - 1) return PercentAt(initPos);
                    flag = false;
                }
            }
            if (initPos < 0) initPos = 0;
            return PercentAt(initPos) + (PercentAt(initPos + 1) - PercentAt(initPos)) * lPerc;
        }

        static double PercentAt(int k) => (float)(k / (double)(CurveSegments - 1));

        /// <summary>lottie-web bez.getNewSegment: sub-curve between two length fractions (values rounded to 1/1000).</summary>
        static void NewSegment(ref SceneBuilder b, float2 pt1, float2 pt2, float2 pt3, float2 pt4, double startPerc, double endPerc,
            int blockOffset, out float2 r0, out float2 r1, out float2 r2, out float2 r3)
        {
            if (startPerc < 0) startPerc = 0;
            else if (startPerc > 1) startPerc = 1;
            double t0 = DistancePerc(ref b, blockOffset, startPerc);
            endPerc = endPerc > 1 ? 1 : endPerc;
            double t1 = DistancePerc(ref b, blockOffset, endPerc);
            double u0 = 1 - t0, u1 = 1 - t1;
            double u0u0u0 = u0 * u0 * u0;
            double t0u0u0_3 = t0 * u0 * u0 * 3;
            double t0t0u0_3 = t0 * t0 * u0 * 3;
            double t0t0t0 = t0 * t0 * t0;
            double u0u0u1 = u0 * u0 * u1;
            double t0u0u1_3 = t0 * u0 * u1 + u0 * t0 * u1 + u0 * u0 * t1;
            double t0t0u1_3 = t0 * t0 * u1 + u0 * t0 * t1 + t0 * u0 * t1;
            double t0t0t1 = t0 * t0 * t1;
            double u0u1u1 = u0 * u1 * u1;
            double t0u1u1_3 = t0 * u1 * u1 + u0 * t1 * u1 + u0 * u1 * t1;
            double t0t1u1_3 = t0 * t1 * u1 + u0 * t1 * t1 + t0 * u1 * t1;
            double t0t1t1 = t0 * t1 * t1;
            double u1u1u1 = u1 * u1 * u1;
            double t1u1u1_3 = t1 * u1 * u1 + u1 * t1 * u1 + u1 * u1 * t1;
            double t1t1u1_3 = t1 * t1 * u1 + u1 * t1 * t1 + t1 * u1 * t1;
            double t1t1t1 = t1 * t1 * t1;
            double2 p1 = new double2(pt1.x, pt1.y), p2 = new double2(pt2.x, pt2.y), p3 = new double2(pt3.x, pt3.y), p4 = new double2(pt4.x, pt4.y);
            double2 a0 = u0u0u0 * p1 + t0u0u0_3 * p3 + t0t0u0_3 * p4 + t0t0t0 * p2;
            double2 a1 = u0u0u1 * p1 + t0u0u1_3 * p3 + t0t0u1_3 * p4 + t0t0t1 * p2;
            double2 a2 = u0u1u1 * p1 + t0u1u1_3 * p3 + t0t1u1_3 * p4 + t0t1t1 * p2;
            double2 a3 = u1u1u1 * p1 + t1u1u1_3 * p3 + t1t1u1_3 * p4 + t1t1t1 * p2;
            r0 = R3(a0); r1 = R3(a1); r2 = R3(a2); r3 = R3(a3);
        }

        static float2 R3(double2 p) => new float2((float)(math.floor(p.x * 1000 + 0.5) / 1000), (float)(math.floor(p.y * 1000 + 0.5) / 1000));

        public static void Trim(ref SceneBuilder b, int* list, int count, int2* ranges, double s, double e, int mode)
        {
            int lengthBase = b.S.Lengths.Length;
            // Per shape: contour length data (stored in a temp native list of PathLen).
            var pls = new NativeList<PathLen>(16, Allocator.Temp);
            var shapeInfo = new NativeArray<int3>(count, Allocator.Temp); // (plFirst, plCount, _)
            var shapeLen = new NativeArray<double>(count, Allocator.Temp);
            double totalModifierLength = 0;
            for (int i = 0; i < count; i++)
            {
                int2 r = ranges[list[i]];
                int plFirst = pls.Length;
                double total = 0;
                for (int c = 0; c < r.y; c++)
                {
                    var pl = BuildContourLengths(ref b, b.S.TempContours[r.x + c]);
                    total += pl.Total;
                    pls.Add(pl);
                }
                shapeInfo[i] = new int3(plFirst, r.y, 0);
                shapeLen[i] = total;
                totalModifierLength += total;
            }

            double addedLength = 0;
            var edges = new NativeList<double2>(4, Allocator.Temp);
            for (int i = count - 1; i >= 0; i--)
            {
                int shape = list[i];
                int2 srcRange = ranges[shape];
                double shapeLength = shapeLen[i];
                edges.Clear();
                if (mode == 2 && count > 1)
                {
                    CalculateShapeEdges(s, e, shapeLength, addedLength, totalModifierLength, ref edges);
                    addedLength += shapeLength;
                }
                else edges.Add(new double2(s, e));

                int outFirst = b.S.TempContours.Length;
                for (int j = 0; j < edges.Length; j++)
                {
                    double shapeS = edges[j].x, shapeE = edges[j].y;
                    double2 seg0, seg1 = default;
                    int segCount;
                    if (shapeE <= 1) { seg0 = new double2(shapeLength * shapeS, shapeLength * shapeE); segCount = 1; }
                    else if (shapeS >= 1) { seg0 = new double2(shapeLength * (shapeS - 1), shapeLength * (shapeE - 1)); segCount = 1; }
                    else { seg0 = new double2(shapeLength * shapeS, shapeLength); seg1 = new double2(0, shapeLength * (shapeE - 1)); segCount = 2; }

                    int pathsStart = b.S.TempContours.Length;
                    int writerPos = AddShapes(ref b, srcRange, pls, shapeInfo[i], seg0, -1);
                    if (seg0.x != seg0.y)
                    {
                        if (segCount > 1)
                        {
                            Contour lastInCollection = b.S.TempContours[srcRange.x + srcRange.y - 1];
                            if (lastInCollection.Closed != 0 && b.S.TempContours.Length > pathsStart)
                            {
                                // Continue the last produced path with the wrapped segment.
                                int lastIdx = b.S.TempContours.Length - 1;
                                AddShapes(ref b, srcRange, pls, shapeInfo[i], seg1, lastIdx);
                            }
                            else AddShapes(ref b, srcRange, pls, shapeInfo[i], seg1, -1);
                        }
                    }
                    else
                    {
                        // Discard the produced (empty-length) shapes.
                        b.S.TempContours.ResizeUninitialized(pathsStart);
                    }
                }
                ranges[shape] = new int2(outFirst, b.S.TempContours.Length - outFirst);
            }
            edges.Dispose();
            pls.Dispose();
            shapeInfo.Dispose();
            shapeLen.Dispose();
            // Length tables are scratch only.
            b.S.Lengths.ResizeUninitialized(lengthBase);
        }

        static void CalculateShapeEdges(double s, double e, double shapeLength, double addedLength, double totalModifierLength, ref NativeList<double2> result)
        {
            var segs = new NativeList<double2>(2, Allocator.Temp);
            if (e <= 1) segs.Add(new double2(s, e));
            else if (s >= 1) segs.Add(new double2(s - 1, e - 1));
            else { segs.Add(new double2(s, 1)); segs.Add(new double2(0, e - 1)); }
            for (int i = 0; i < segs.Length; i++)
            {
                double2 so = segs[i];
                if (!(so.y * totalModifierLength < addedLength || so.x * totalModifierLength > addedLength + shapeLength))
                {
                    double shapeS, shapeE;
                    if (so.x * totalModifierLength <= addedLength) shapeS = 0;
                    else shapeS = (so.x * totalModifierLength - addedLength) / shapeLength;
                    if (so.y * totalModifierLength >= addedLength + shapeLength) shapeE = 1;
                    else shapeE = (so.y * totalModifierLength - addedLength) / shapeLength;
                    result.Add(new double2(shapeS, shapeE));
                }
            }
            if (result.Length == 0) result.Add(new double2(0, 0));
            segs.Dispose();
        }

        /// <summary>
        /// lottie-web TrimModifier.addShapes. Appends resulting contours to TempContours. When
        /// <paramref name="continueContour"/> >= 0 the first output continues that (last) contour.
        /// </summary>
        static int AddShapes(ref SceneBuilder b, int2 srcRange, NativeList<PathLen> pls, int3 info, double2 seg, int continueContour)
        {
            int len = srcRange.y;
            double addedLength = 0;
            bool newShape = true;
            Writer w;
            int segmentCount, initPos;
            if (continueContour < 0)
            {
                w = Begin(ref b);
                segmentCount = 0;
                initPos = 0;
            }
            else
            {
                Contour cc = b.S.TempContours[continueContour];
                b.S.TempContours.RemoveAt(continueContour);
                w = new Writer { First = cc.First, Count = cc.Count, Closed = cc.Closed != 0 };
                // The continued contour must be the last one in Points for in-place growth.
                if (cc.First + cc.Count * 3 != b.Out.Points.Length)
                {
                    int nf = b.Out.Points.Length;
                    for (int k = 0; k < cc.Count * 3; k++) b.Out.Points.Add(b.Out.Points[cc.First + k]);
                    w.First = nf;
                }
                segmentCount = w.Count;
                initPos = w.Count;
            }
            for (int i = 0; i < len; i++)
            {
                Contour path = b.S.TempContours[srcRange.x + i];
                PathLen pl = pls[info.x + i];
                int lengthsCount = pl.Segments;
                w.Closed = path.Closed != 0;
                int jLen = path.Closed != 0 ? lengthsCount : lengthsCount + 1;
                int j;
                for (j = 1; j < jLen; j++)
                {
                    double cur = SegAdded(ref b, pl, j - 1);
                    if (addedLength + cur < seg.x)
                    {
                        addedLength += cur;
                        w.Closed = false;
                    }
                    else if (addedLength > seg.y)
                    {
                        w.Closed = false;
                        break;
                    }
                    else
                    {
                        if (seg.x <= addedLength && seg.y >= addedLength + cur)
                        {
                            AddSegment(ref b, ref w, V(ref b, path, j - 1), O(ref b, path, j - 1), I(ref b, path, j), V(ref b, path, j), segmentCount, newShape);
                            newShape = false;
                        }
                        else
                        {
                            NewSegment(ref b, V(ref b, path, j - 1), V(ref b, path, j), O(ref b, path, j - 1), I(ref b, path, j),
                                (seg.x - addedLength) / cur, (seg.y - addedLength) / cur, pl.Offset + (j - 1) * SegBlock,
                                out float2 q0, out float2 q1, out float2 q2, out float2 q3);
                            AddSegment(ref b, ref w, q0, q1, q2, q3, segmentCount, newShape);
                            newShape = false;
                            w.Closed = false;
                        }
                        addedLength += cur;
                        segmentCount += 1;
                    }
                }
                if (path.Closed != 0 && lengthsCount > 0)
                {
                    int lastSeg = math.min(j - 1, lengthsCount - 1);
                    double curLen = SegAdded(ref b, pl, lastSeg);
                    if (addedLength <= seg.y)
                    {
                        double segmentLength = SegAdded(ref b, pl, j - 1 < lengthsCount ? j - 1 : lengthsCount - 1);
                        int last = path.Count - 1;
                        if (seg.x <= addedLength && seg.y >= addedLength + segmentLength)
                        {
                            AddSegment(ref b, ref w, V(ref b, path, last), O(ref b, path, last), I(ref b, path, 0), V(ref b, path, 0), segmentCount, newShape);
                            newShape = false;
                        }
                        else
                        {
                            NewSegment(ref b, V(ref b, path, last), V(ref b, path, 0), O(ref b, path, last), I(ref b, path, 0),
                                (seg.x - addedLength) / segmentLength, (seg.y - addedLength) / segmentLength, pl.Offset + (lengthsCount - 1) * SegBlock,
                                out float2 q0, out float2 q1, out float2 q2, out float2 q3);
                            AddSegment(ref b, ref w, q0, q1, q2, q3, segmentCount, newShape);
                            newShape = false;
                            w.Closed = false;
                        }
                    }
                    else w.Closed = false;
                    addedLength += curLen;
                    segmentCount += 1;
                }
                if (w.Count > 0)
                {
                    SetXY(ref b, ref w, b.Out.Points[w.First + initPos * 3], 1, initPos);
                    SetXY(ref b, ref w, b.Out.Points[w.First + (w.Count - 1) * 3], 2, w.Count - 1);
                }
                bool stop = addedLength > seg.y;
                if (stop || i == len - 1)
                {
                    End(ref b, w);
                    break;
                }
                End(ref b, w);
                w = Begin(ref b);
                newShape = true;
                segmentCount = 0;
                initPos = 0;
            }
            if (len == 0) End(ref b, w);
            return 0;
        }

        static void AddSegment(ref SceneBuilder b, ref Writer w, float2 pt1, float2 pt2, float2 pt3, float2 pt4, int pos, bool newShape)
        {
            SetXY(ref b, ref w, pt2, 2, pos);
            SetXY(ref b, ref w, pt3, 1, pos + 1);
            if (newShape) SetXY(ref b, ref w, pt1, 0, pos);
            SetXY(ref b, ref w, pt4, 0, pos + 1);
        }
    }
}
