using System;
using VRageMath;

namespace SpaceEngineersVR.Common
{
    /// <summary>Small easing helpers shared by the hand and body features that blend a pose over time.</summary>
    internal static class Ease
    {
        /// <summary>Moves <paramref name="value"/> toward <paramref name="target"/> by at most <paramref name="step"/>, without overshooting.</summary>
        public static double Approach(double value, double target, double step) =>
            value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

        /// <summary>The single-precision form of <see cref="Approach(double, double, double)"/>.</summary>
        public static float Approach(float from, float to, float step) =>
            from < to ? Math.Min(to, from + step) : Math.Max(to, from - step);

        /// <summary>Smoothstep: 0 at or below 0, 1 at or above 1, with zero slope at both ends.</summary>
        public static double Smooth01(double x)
        {
            x = MathHelper.Clamp(x, 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }
    }
}
