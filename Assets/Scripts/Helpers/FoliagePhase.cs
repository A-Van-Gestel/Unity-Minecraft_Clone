using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// The world origin's contribution to the FL-1 foliage wave phase.
    /// <para>
    /// The sway wave is anchored in voxel space so its pattern survives a floating-origin re-anchor (WS-3). Feeding
    /// the shader an absolute voxel coordinate to achieve that would put a value proportional to the player's
    /// distance from the world center inside a sine argument, where float32's distance-proportional resolution
    /// coarsens the phase until the wave visibly steps and finally stops. Reducing the origin's whole contribution
    /// modulo a cycle here — in double precision, from the integer origin — is exact for a sine, so the shader adds
    /// a small constant to a small render-space term and the wave animates identically at any distance.
    /// </para>
    /// </summary>
    /// <remarks>Lives outside <c>FoliageSway</c> so the Chunk Math validation suite pins the real production
    /// arithmetic rather than a re-derived copy of it.</remarks>
    public static class FoliagePhase
    {
        /// <summary>One full wave cycle in radians — the period the origin's contribution reduces modulo.</summary>
        public const double TwoPi = 2.0 * System.Math.PI;

        /// <summary>
        /// Reduces the origin's contribution to each sway wave's phase modulo a full cycle.
        /// </summary>
        /// <param name="originVoxel">The voxel-space coordinate of the Unity-space origin (<c>WorldOrigin.OriginVoxel</c>).</param>
        /// <param name="windVector">The wind vector exactly as the shader receives it (direction scaled by strength).</param>
        /// <param name="spatialFrequency">Radians per block along the wind, exactly as the shader receives it.</param>
        /// <param name="gustSpatialMultiplier">The gust's spatial frequency relative to the primary wave's.</param>
        /// <returns>X = the primary wave's origin phase in radians, Y = the gust's.</returns>
        public static Vector2 OriginPhase(Vector3Int originVoxel, Vector2 windVector, float spatialFrequency,
            float gustSpatialMultiplier)
        {
            double alongWind = originVoxel.x * (double)windVector.x + originVoxel.z * (double)windVector.y;

            // The gust rides the same wind line at its own spatial frequency, so it needs its own reduction:
            // scaling the primary's already-reduced phase would not preserve it modulo a cycle.
            double primary = spatialFrequency * alongWind % TwoPi;
            double gust = spatialFrequency * gustSpatialMultiplier * alongWind % TwoPi;

            return new Vector2((float)primary, (float)gust);
        }
    }
}
