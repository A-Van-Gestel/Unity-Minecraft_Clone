using System.Runtime.CompilerServices;
using Data;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Jobs.BurstData
{
    /// <summary>
    /// Burst-safe fluid flow-field queries: the corner-symmetric flow derivative and the neighbor
    /// classification it rests on.
    /// <para>
    /// The sole implementation of "which way does this fluid run" — a second one would let the direction
    /// water is drawn moving and the direction it pushes drift apart.
    /// </para>
    /// <para>
    /// <b>The neighborhood is absolute, which is what makes it shareable.</b> A corner's flow is a function
    /// of the four blocks touching it, identified by world position alone — no chunk, no vertex, no meshing
    /// state. Two adjacent quads therefore agree exactly at their shared corner.
    /// </para>
    /// </summary>
    public static class BurstFluidFlowUtility
    {
        /// <summary>
        /// Effective height reported for a neighbor that is a solid obstacle — above the fluid maximum of 1.0,
        /// so a wall reads as something flow runs away from rather than toward.
        /// </summary>
        public const float WallHeight = 2.0f;

        /// <summary>
        /// Effective height reported for an open drop (a non-solid, non-fluid neighbor the fluid can fall
        /// into) — negative, so an edge pulls flow over it.
        /// </summary>
        public const float DropHeight = -1.0f;

        /// <summary>
        /// Calculates a discrete 2D flow-direction vector for a specific corner of a fluid block symmetrically.
        /// By evaluating the 4 blocks that share this corner together, it guarantees mathematically identical
        /// flow vectors across chunk and block boundaries, eliminating UV seams.
        /// </summary>
        /// <param name="b00">The block at local (-x, -z) of the corner.</param>
        /// <param name="b10">The block at local (+x, -z) of the corner.</param>
        /// <param name="b01">The block at local (-x, +z) of the corner.</param>
        /// <param name="b11">The block at local (+x, +z) of the corner.</param>
        /// <param name="fluidType">The fluid type being evaluated.</param>
        /// <param name="templates">The pre-computed height templates for this fluid type.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>A 2D vector representing the XZ flow direction at this corner.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 CalculateSymmetricCornerFlow(
            OptionalVoxelState b00, OptionalVoxelState b10,
            OptionalVoxelState b01, OptionalVoxelState b11,
            FluidType fluidType,
            in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            bool w00 = IsSolidWall(b00, in blockTypes);
            bool w10 = IsSolidWall(b10, in blockTypes);
            bool w01 = IsSolidWall(b01, in blockTypes);
            bool w11 = IsSolidWall(b11, in blockTypes);

            // Accessibility guard: a non-wall, non-fluid block (e.g., air) is only included
            // if at least one of its two grid-adjacent neighbors is matching fluid. This prevents
            // isolated non-fluid blocks (diagonal air behind two walls) from creating artificial
            // pull gradients, while preserving the natural pull toward waterfall edges and drops
            // where the air IS accessible from the fluid surface.
            bool f00 = IsMatchingFluid(b00, fluidType, in blockTypes);
            bool f10 = IsMatchingFluid(b10, fluidType, in blockTypes);
            bool f01 = IsMatchingFluid(b01, fluidType, in blockTypes);
            bool f11 = IsMatchingFluid(b11, fluidType, in blockTypes);

            // b00 adjacent to b10, b01 — inaccessible if neither is fluid
            if (!w00 && !f00 && !f10 && !f01) w00 = true;
            // b10 adjacent to b00, b11 — inaccessible if neither is fluid
            if (!w10 && !f10 && !f00 && !f11) w10 = true;
            // b01 adjacent to b00, b11 — inaccessible if neither is fluid
            if (!w01 && !f01 && !f00 && !f11) w01 = true;
            // b11 adjacent to b10, b01 — inaccessible if neither is fluid
            if (!w11 && !f11 && !f10 && !f01) w11 = true;

            float h00 = w00 ? 0 : GetEffectiveFluidHeight(b00, fluidType, templates, blockTypes);
            float h10 = w10 ? 0 : GetEffectiveFluidHeight(b10, fluidType, templates, blockTypes);
            float h01 = w01 ? 0 : GetEffectiveFluidHeight(b01, fluidType, templates, blockTypes);
            float h11 = w11 ? 0 : GetEffectiveFluidHeight(b11, fluidType, templates, blockTypes);

            float dx = 0f;
            int dx_count = 0;
            // Only calculate the X derivative if the fluid actually exists across the boundary.
            // This prevents walls from creating artificial slopes that pull flow backward!
            if (!w01 && !w11)
            {
                dx += h11 - h01;
                dx_count++;
            }

            if (!w00 && !w10)
            {
                dx += h10 - h00;
                dx_count++;
            }

            if (dx_count > 0) dx /= dx_count;

            float dz = 0f;
            int dz_count = 0;
            // Only calculate the Z derivative if the fluid actually exists across the boundary.
            if (!w10 && !w11)
            {
                dz += h11 - h10;
                dz_count++;
            }

            if (!w00 && !w01)
            {
                dz += h01 - h00;
                dz_count++;
            }

            if (dz_count > 0) dz /= dz_count;

            Vector2 cornerFlow = new Vector2(dx, dz);
            float sqrMag = cornerFlow.sqrMagnitude;

            if (sqrMag < 0.0001f) return Vector2.zero;

            // Get the pure normalized direction
            float mag = math.sqrt(sqrMag);
            Vector2 dir = cornerFlow / mag;

            // Apply a smooth speed curve to the magnitude.
            // Gentle slopes (mag 0.25) get boosted to a standard speed of 1.0.
            // Steep drops/waterfalls (mag 1.0+) get boosted to 1.5.
            float speed = math.smoothstep(0.0f, 0.25f, mag) + math.smoothstep(0.8f, 1.2f, mag) * 0.5f;

            return dir * speed;
        }

        /// <summary>
        /// Determines the effective visual height of a neighboring block for fluid smoothing and flow calculations.
        /// Treats solid obstacles as high walls (2.0) and open drops as strong pulls (-1.0).
        /// </summary>
        /// <param name="neighbor">The neighbor voxel state to evaluate.</param>
        /// <param name="centerFluidType">The fluid type of the center block (Water/Lava).</param>
        /// <param name="templates">The pre-computed height templates for this fluid type.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>The effective relative height of the neighbor.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetEffectiveFluidHeight(OptionalVoxelState neighbor, FluidType centerFluidType,
            in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            if (!neighbor.HasValue) return 0f; // Neutral chunk edge

            BlockTypeJobData nbProps = blockTypes[neighbor.State.ID];

            // Solid obstacle
            if (nbProps.IsSolid && !nbProps.IsTransparentForMesh) return WallHeight; // Represents a solid wall (higher than fluid 1.0)

            // Open Drop / Pit
            if (nbProps.FluidType == FluidType.None && !nbProps.IsSolid) return DropHeight; // Massive pull

            // Same fluid type
            if (nbProps.FluidType == centerFluidType) return templates[neighbor.State.FluidLevel];

            return 0f;
        }

        /// <summary>
        /// Returns true if the given voxel is a solid, non-fluid block — a wall, for flow and shore purposes.
        /// </summary>
        /// <param name="state">The voxel state to classify; a value-less state is never a wall.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>True when the block walls off fluid.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsSolidWall(OptionalVoxelState state, in NativeArray<BlockTypeJobData> blockTypes)
        {
            return state.HasValue && blockTypes[state.State.ID].IsSolid && blockTypes[state.State.ID].FluidType == FluidType.None;
        }

        /// <summary>
        /// Returns true if the given voxel contains the same type of fluid as the center block.
        /// Used by <see cref="CalculateSymmetricCornerFlow"/> to restrict derivative computation
        /// to same-type fluid blocks, preventing air and walls from creating artificial gradients.
        /// </summary>
        /// <param name="state">The voxel state to classify; a value-less state never matches.</param>
        /// <param name="fluidType">The center block's fluid type.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>True when the block carries the same fluid type.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsMatchingFluid(OptionalVoxelState state, FluidType fluidType, in NativeArray<BlockTypeJobData> blockTypes)
        {
            return state.HasValue && blockTypes[state.State.ID].FluidType == fluidType;
        }
    }
}
