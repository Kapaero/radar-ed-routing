using UnityEngine;

namespace RadarCrowd
{
    /// <summary>
    /// Pedestrian fundamental diagram of Weidmann (1993): v(rho) = v0 [1 - exp(-gamma (1/rho - 1/rho_max))].
    /// Used by the router to turn an estimated density into an expected walking speed.
    /// </summary>
    public static class Weidmann
    {
        public const float FreeSpeed = 1.34f;     // m/s
        public const float FreeSpeedSd = 0.26f;   // m/s, spread of free walking speeds
        public const float Gamma = 1.913f;        // 1/m^2
        public const float JamDensity = 5.4f;     // 1/m^2

        /// <summary>Lowest speed the router assumes for a passable segment, so travel times stay finite.</summary>
        public const float MinSpeed = 0.05f;

        public static float Speed(float density)
        {
            if (density <= 0.01f)
                return FreeSpeed;
            if (density >= JamDensity)
                return MinSpeed;
            float v = FreeSpeed * (1f - Mathf.Exp(-Gamma * (1f / density - 1f / JamDensity)));
            return Mathf.Max(MinSpeed, v);
        }
    }
}
