using Unity.Burst;
using Unity.Mathematics;

namespace Mynell.Lottie.Eval
{
    /// <summary>
    /// Port of lottie-web's BezierEasing (Gaëtan Renaudeau's bezier-easing, 11-sample table,
    /// 4 Newton iterations, binary subdivision fallback) so keyframe timing matches exactly.
    /// </summary>
    [BurstCompile]
    public static class Easing
    {
        const int NewtonIterations = 4;
        const double NewtonMinSlope = 0.001;
        const double SubdivisionPrecision = 0.0000001;
        const int SubdivisionMaxIterations = 10;
        const int SplineTableSize = 11;
        const double SampleStepSize = 1.0 / (SplineTableSize - 1.0);

        static double A(double a1, double a2) => 1.0 - 3.0 * a2 + 3.0 * a1;
        static double B(double a1, double a2) => 3.0 * a2 - 6.0 * a1;
        static double C(double a1) => 3.0 * a1;
        static double CalcBezier(double t, double a1, double a2) => ((A(a1, a2) * t + B(a1, a2)) * t + C(a1)) * t;
        static double GetSlope(double t, double a1, double a2) => 3.0 * A(a1, a2) * t * t + 2.0 * B(a1, a2) * t + C(a1);

        /// <summary>Evaluates the easing curve (x1, y1, x2, y2) at progress <paramref name="x"/>.</summary>
        public static double Evaluate(double x1, double y1, double x2, double y2, double x)
        {
            if (x1 == y1 && x2 == y2) return x; // linear
            if (x == 0) return 0;
            if (x == 1) return 1;
            return CalcBezier(GetTForX(x, x1, x2), y1, y2);
        }

        static unsafe double GetTForX(double aX, double x1, double x2)
        {
            // lottie-web stores the sample table in a Float32Array.
            float* samples = stackalloc float[SplineTableSize];
            for (int i = 0; i < SplineTableSize; i++) samples[i] = (float)CalcBezier(i * SampleStepSize, x1, x2);

            double intervalStart = 0.0;
            int currentSample = 1;
            const int lastSample = SplineTableSize - 1;
            for (; currentSample != lastSample && samples[currentSample] <= aX; ++currentSample)
                intervalStart += SampleStepSize;
            --currentSample;

            double dist = (aX - samples[currentSample]) / ((double)samples[currentSample + 1] - samples[currentSample]);
            double guessForT = intervalStart + dist * SampleStepSize;
            double initialSlope = GetSlope(guessForT, x1, x2);
            if (initialSlope >= NewtonMinSlope)
            {
                for (int i = 0; i < NewtonIterations; ++i)
                {
                    double slope = GetSlope(guessForT, x1, x2);
                    if (slope == 0.0) return guessForT;
                    double cx = CalcBezier(guessForT, x1, x2) - aX;
                    guessForT -= cx / slope;
                }
                return guessForT;
            }
            if (initialSlope == 0.0) return guessForT;

            double a = intervalStart, b = intervalStart + SampleStepSize;
            double currentX, currentT;
            int it = 0;
            do
            {
                currentT = a + (b - a) / 2.0;
                currentX = CalcBezier(currentT, x1, x2) - aX;
                if (currentX > 0.0) b = currentT;
                else a = currentT;
            } while (math.abs(currentX) > SubdivisionPrecision && ++it < SubdivisionMaxIterations);
            return currentT;
        }
    }
}
