using Data;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;

namespace Physics
{
    /// <summary>
    /// The per-cell half of the fluid contact query: how high a fluid cell's surface sits, how much of a body
    /// that submerges, and which way the current through it runs.
    /// <para>
    /// Shared rather than inlined, for the same reason as <see cref="PhysicsCollisionCells"/>: the voxel
    /// lookups and the managed palette live at the query site, while the geometry and the flow derivative
    /// are pure functions over value types and belong somewhere a second implementation cannot appear.
    /// </para>
    /// </summary>
    public static class FluidContactResolver
    {
        /// <summary>
        /// Surface height of a fluid cell, in block-local units above the cell's floor.
        /// </summary>
        /// <param name="fluidLevel">The voxel's raw 4-bit fluid level, falling flag included.</param>
        /// <param name="templates">The 16-entry vertex-height template for this fluid.</param>
        /// <returns>The surface height, 0–1.</returns>
        /// <remarks>
        /// Reads the same authored curve the renderer draws (<see cref="FluidMeshData"/>), indexed by the raw
        /// nibble so the falling flag keeps its meaning: levels 8–15 are a falling column and fill the cell to
        /// 1.0, which is why the flag must not be stripped before the lookup. Deliberately the <i>logical</i>
        /// per-cell height rather than the renderer's corner-smoothed surface — a body's waterline should not
        /// depend on the smoothing its neighbors happen to induce.
        /// </remarks>
        public static float SurfaceHeight(byte fluidLevel, in NativeArray<float> templates)
        {
            return templates[fluidLevel];
        }

        /// <summary>
        /// How much of a body's collider height sits below a fluid surface.
        /// </summary>
        /// <param name="surfaceY">Unity-space Y of the fluid surface.</param>
        /// <param name="bodyMinY">Unity-space Y of the body's feet.</param>
        /// <param name="colliderHeight">The body's total collider height.</param>
        /// <returns>The submerged fraction, clamped to 0–1.</returns>
        public static float SubmergedFraction(float surfaceY, float bodyMinY, float colliderHeight)
        {
            if (colliderHeight <= 0f) return 0f;

            return Mathf.Clamp01((surfaceY - bodyMinY) / colliderHeight);
        }

        /// <summary>
        /// The current running through one fluid cell, as a Unity-space XZ direction.
        /// </summary>
        /// <param name="center">The fluid cell itself.</param>
        /// <param name="north">Neighbor at +Z.</param>
        /// <param name="south">Neighbor at -Z.</param>
        /// <param name="east">Neighbor at +X.</param>
        /// <param name="west">Neighbor at -X.</param>
        /// <param name="northEast">Neighbor at (+X, +Z).</param>
        /// <param name="northWest">Neighbor at (-X, +Z).</param>
        /// <param name="southEast">Neighbor at (+X, -Z).</param>
        /// <param name="southWest">Neighbor at (-X, -Z).</param>
        /// <param name="fluidType">The center cell's fluid type.</param>
        /// <param name="templates">The 16-entry vertex-height template for this fluid.</param>
        /// <param name="blockTypes">The job-side block palette.</param>
        /// <returns>The push direction and strength; zero in still fluid.</returns>
        /// <remarks>
        /// <para>
        /// Averages the cell's four corner vectors from the shared
        /// <see cref="BurstFluidFlowUtility.CalculateSymmetricCornerFlow"/>, the same function meshing uses
        /// for its flow UVs. Their mean is the bilinear value at the cell center, so the push a body feels is
        /// the current drawn where it stands rather than a second approximation.
        /// </para>
        /// <para>
        /// <b>The negation is the whole point of this wrapper.</b> The meshing vector is a UV scroll offset
        /// pointing <i>uphill</i> — the shader adds it to its noise sample position, so the visible current
        /// runs opposite. Water flows downhill, so the physical current is the negated vector.
        /// </para>
        /// </remarks>
        public static Vector2 ResolveFlow(
            OptionalVoxelState center,
            OptionalVoxelState north, OptionalVoxelState south,
            OptionalVoxelState east, OptionalVoxelState west,
            OptionalVoxelState northEast, OptionalVoxelState northWest,
            OptionalVoxelState southEast, OptionalVoxelState southWest,
            FluidType fluidType,
            in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            // Corner argument order is (-x,-z), (+x,-z), (-x,+z), (+x,+z) relative to each corner, matching
            // VoxelMeshHelper's four calls exactly — the shared function is only seam-free while every caller
            // presents the same neighborhood in the same order.
            Vector2 bl = BurstFluidFlowUtility.CalculateSymmetricCornerFlow(
                southWest, south, west, center, fluidType, in templates, in blockTypes);
            Vector2 tl = BurstFluidFlowUtility.CalculateSymmetricCornerFlow(
                west, center, northWest, north, fluidType, in templates, in blockTypes);
            Vector2 br = BurstFluidFlowUtility.CalculateSymmetricCornerFlow(
                south, southEast, center, east, fluidType, in templates, in blockTypes);
            Vector2 tr = BurstFluidFlowUtility.CalculateSymmetricCornerFlow(
                center, east, north, northEast, fluidType, in templates, in blockTypes);

            Vector2 cornerMean = (bl + tl + br + tr) * 0.25f;

            return -cornerMean;
        }
    }
}
