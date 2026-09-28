using Mynell.Lottie.Model;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Mynell.Lottie.Eval
{
    /// <summary>
    /// Burst-friendly property evaluator reproducing lottie-web's interpolateValue / interpolateShape.
    /// </summary>
    public unsafe struct PropReader
    {
        [NativeDisableUnsafePtrRestriction] public PropDesc* Props;
        [NativeDisableUnsafePtrRestriction] public KeyframeData* Keys;
        [NativeDisableUnsafePtrRestriction] public float* F;
        [NativeDisableUnsafePtrRestriction] public float* Tables;
        public int PropCount;
        /// <summary>Optional per-instance overrides: slot per property (-1 = none) into <see cref="OverrideValues"/>.</summary>
        [NativeDisableUnsafePtrRestriction] public int* OverrideSlots;
        [NativeDisableUnsafePtrRestriction] public float4* OverrideValues;

        public static PropReader Create(in LottieNativeData d)
        {
            return new PropReader
            {
                Props = (PropDesc*)d.Props.GetUnsafeReadOnlyPtr(),
                Keys = (KeyframeData*)d.Keys.GetUnsafeReadOnlyPtr(),
                F = (float*)d.Floats.GetUnsafeReadOnlyPtr(),
                Tables = (float*)d.Tables.GetUnsafeReadOnlyPtr(),
                PropCount = d.Props.Length,
            };
        }

        public int Dim(int prop) => prop < 0 ? 0 : Props[prop].Dim;
        public bool IsAnimated(int prop) => prop >= 0 && Props[prop].KeyCount > 0;
        public PropFlags Flags(int prop) => prop < 0 ? PropFlags.None : Props[prop].Flags;

        /// <summary>
        /// Evaluates <paramref name="prop"/> at <paramref name="frame"/> (layer-local frame) into
        /// <paramref name="dst"/> (at most <paramref name="maxDim"/> values). Returns the dimension.
        /// </summary>
        public int Eval(int prop, float frame, float* dst, int maxDim)
        {
            if (prop < 0) return 0;
            PropDesc p = Props[prop];
            int dim = math.min((int)p.Dim, maxDim);
            if (OverrideSlots != null && OverrideSlots[prop] >= 0 && dim <= 4)
            {
                float4 v = OverrideValues[OverrideSlots[prop]];
                for (int i = 0; i < dim; i++) dst[i] = v[i];
                return dim;
            }
            if (p.KeyCount == 0)
            {
                float* src = F + p.Offset;
                for (int i = 0; i < dim; i++) dst[i] = src[i];
                return dim;
            }
            KeyframeData* keys = Keys + p.Offset;
            int n = p.KeyCount;
            if (n == 1)
            {
                float* src = F + keys[0].Start;
                for (int i = 0; i < dim; i++) dst[i] = src[i];
                return dim;
            }

            // lottie-web segment search (fresh cache): first i with keys[i+1].t > frame, else last segment.
            int len = n - 1;
            int k = 0;
            bool afterEnd = false;
            while (true)
            {
                if (k == len - 1 && frame >= keys[k + 1].Time) { afterEnd = true; break; }
                if (keys[k + 1].Time > frame) break;
                if (k < len - 1) k++;
                else break;
            }
            KeyframeData key = keys[k];
            KeyframeData next = keys[k + 1];
            if (afterEnd && key.Hold != 0) key = next;
            float keyTime = key.Time;
            float nextTime = next.Time;

            if (key.Spatial >= 0 && key.Table >= 0 && (p.Flags & PropFlags.Path) == 0)
            {
                EvalSpatial(key, keyTime, nextTime, frame, dst, dim);
                return dim;
            }

            float* s = F + key.Start;
            float* e = key.End >= 0 ? F + key.End : s;
            if (key.Hold != 0)
            {
                for (int i = 0; i < dim; i++) dst[i] = s[i];
                return dim;
            }
            if (frame >= nextTime || frame < keyTime || key.EaseDims <= 1)
            {
                double perc;
                if (frame >= nextTime) perc = 1;
                else if (frame < keyTime) perc = 0;
                else perc = EaseAt(key, 0, (frame - keyTime) / (double)(nextTime - keyTime));
                float pf = (float)perc;
                for (int i = 0; i < dim; i++) dst[i] = (float)(s[i] + (e[i] - s[i]) * perc);
                return dim;
            }
            double t = (frame - keyTime) / (double)(nextTime - keyTime);
            for (int i = 0; i < dim; i++)
            {
                double perc = EaseAt(key, math.min(i, key.EaseDims - 1), t);
                dst[i] = (float)(s[i] + (e[i] - s[i]) * perc);
            }
            return dim;
        }

        double EaseAt(in KeyframeData key, int dimIndex, double t)
        {
            if (key.Ease < 0 || key.EaseDims == 0) return t;
            float* ep = F + key.Ease + dimIndex * 4;
            return Easing.Evaluate(ep[0], ep[1], ep[2], ep[3], t);
        }

        void EvalSpatial(in KeyframeData key, float keyTime, float nextTime, float frame, float* dst, int dim)
        {
            float* table = Tables + key.Table;
            const int count = LottieFormat.CurveSegments;
            float segmentLength = table[0];
            float* pts = table + 1; // (partial, x, y, z) * count
            int outDim = math.min(dim, 3);
            if (frame >= nextTime || frame < keyTime)
            {
                int ind = frame >= nextTime ? count - 1 : 0;
                for (int c = 0; c < outDim; c++) dst[c] = pts[ind * 4 + 1 + c];
                return;
            }
            double perc = EaseAt(key, 0, (frame - keyTime) / (double)(nextTime - keyTime));
            double distanceInLine = segmentLength * perc;
            double addedLength = 0;
            for (int j = 0; j < count; j++)
            {
                addedLength += pts[j * 4];
                if (distanceInLine == 0 || perc == 0 || j == count - 1)
                {
                    for (int c = 0; c < outDim; c++) dst[c] = pts[j * 4 + 1 + c];
                    return;
                }
                double nextPartial = pts[(j + 1) * 4];
                if (distanceInLine >= addedLength && distanceInLine < addedLength + nextPartial)
                {
                    double segmentPerc = (distanceInLine - addedLength) / nextPartial;
                    for (int c = 0; c < outDim; c++)
                    {
                        double a = pts[j * 4 + 1 + c];
                        double b = pts[(j + 1) * 4 + 1 + c];
                        dst[c] = (float)(a + (b - a) * segmentPerc);
                    }
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ typed helpers

        public float Scalar(int prop, float frame, float def)
        {
            if (prop < 0) return def;
            float v;
            return Eval(prop, frame, &v, 1) > 0 ? v : def;
        }

        public float2 Vec2(int prop, float frame, float2 def)
        {
            if (prop < 0) return def;
            float* v = stackalloc float[3];
            int d = Eval(prop, frame, v, 3);
            if (d <= 0) return def;
            return d == 1 ? new float2(v[0], v[0]) : new float2(v[0], v[1]);
        }

        public float3 Vec3(int prop, float frame, float3 def)
        {
            if (prop < 0) return def;
            float* v = stackalloc float[3];
            int d = Eval(prop, frame, v, 3);
            if (d <= 0) return def;
            if (d == 1) return new float3(v[0], v[0], def.z);
            if (d == 2) return new float3(v[0], v[1], def.z);
            return new float3(v[0], v[1], v[2]);
        }

        public float4 Vec4(int prop, float frame, float4 def)
        {
            if (prop < 0) return def;
            float* v = stackalloc float[4];
            int d = Eval(prop, frame, v, 4);
            if (d <= 0) return def;
            var r = def;
            if (d > 0) r.x = v[0];
            if (d > 1) r.y = v[1];
            if (d > 2) r.z = v[2];
            if (d > 3) r.w = v[3];
            return r;
        }
    }
}
