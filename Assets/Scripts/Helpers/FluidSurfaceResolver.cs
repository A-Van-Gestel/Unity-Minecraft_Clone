using System.Runtime.CompilerServices;
using Data;
using Unity.Collections;
using Unity.Mathematics;

namespace Helpers
{
    /// <summary>
    /// The four corner heights of one fluid cell's drawn surface, in block-local units above the cell floor.
    /// </summary>
    /// <remarks>
    /// Corner naming follows the mesher's top-face vertex order: <c>BL</c> is the cell's <c>(0, 0)</c> XZ
    /// corner, <c>BR</c> is <c>(1, 0)</c>, <c>TL</c> is <c>(0, 1)</c> and <c>TR</c> is <c>(1, 1)</c>.
    /// </remarks>
    public struct FluidCornerHeights
    {
        /// <summary>Height at the cell's (0, 0) corner.</summary>
        public float BL;

        /// <summary>Height at the cell's (1, 0) corner.</summary>
        public float BR;

        /// <summary>Height at the cell's (0, 1) corner.</summary>
        public float TL;

        /// <summary>Height at the cell's (1, 1) corner.</summary>
        public float TR;
    }

    /// <summary>
    /// Where a fluid cell's <b>drawn</b> surface sits: the corner-smoothed heights the mesher emits as
    /// top-face vertices, and the bilinear surface between them.
    /// <para>
    /// The single source of that geometry. <see cref="VoxelMeshHelper.GenerateFluidMeshData"/> builds its
    /// vertices from these functions and <c>World.GatherEyeSubmersion</c> samples the same ones, so the
    /// surface a query reports and the surface the player sees cannot drift apart.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The rendering-side counterpart to <see cref="Physics.FluidContactResolver"/>, and deliberately not the
    /// same math: that one answers the <i>logical</i> per-cell height a body's buoyancy uses, which must not
    /// depend on the smoothing a cell's neighbors happen to induce. Both are pure static functions over value
    /// types and <see cref="NativeArray{T}"/>, with no managed references, so either stays job-callable.
    /// </remarks>
    public static class FluidSurfaceResolver
    {
        /// <summary>
        /// Floor applied to every smoothed corner, in block-local units, so a near-empty cell still has a
        /// surface above its own floor to draw.
        /// </summary>
        /// <remarks>Prevents the top face z-fighting the block below it at vanishing fluid levels.</remarks>
        public const float MinSurfaceHeight = 0.005f;

        /// <summary>
        /// Whether the same fluid occupies the cell directly above, which makes this cell's surface interior
        /// to the fluid body rather than a drawn boundary.
        /// </summary>
        /// <param name="above">The cell directly above.</param>
        /// <param name="centerProps">Properties of the cell being resolved.</param>
        /// <param name="blockTypes">The job-side block palette.</param>
        /// <returns>True when the cell above holds the same fluid.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasSameFluidAbove(OptionalVoxelState above, in BlockTypeJobData centerProps,
            in NativeArray<BlockTypeJobData> blockTypes)
        {
            return above.HasValue && blockTypes[above.State.ID].FluidType == centerProps.FluidType;
        }

        /// <summary>
        /// The cell's four corner heights after neighbor smoothing, clamped to <see cref="MinSurfaceHeight"/>.
        /// </summary>
        /// <param name="centerProps">Properties of the cell being resolved.</param>
        /// <param name="centerLevel">The cell's raw 4-bit fluid level, falling flag included.</param>
        /// <param name="nN">Neighbor at +Z.</param>
        /// <param name="nE">Neighbor at +X.</param>
        /// <param name="nS">Neighbor at -Z.</param>
        /// <param name="nW">Neighbor at -X.</param>
        /// <param name="nNE">Neighbor at (+X, +Z).</param>
        /// <param name="nSE">Neighbor at (+X, -Z).</param>
        /// <param name="nSW">Neighbor at (-X, -Z).</param>
        /// <param name="nNW">Neighbor at (-X, +Z).</param>
        /// <param name="templates">The 16-entry vertex-height template for this fluid.</param>
        /// <param name="blockTypes">The job-side block palette.</param>
        /// <returns>The clamped, smoothed corner heights.</returns>
        /// <remarks>
        /// Still the surface a <i>side</i> face rises to, which is why the <c>hasFluidAbove</c> override lives
        /// in <see cref="SurfaceCornerHeights"/> rather than here — the mesher needs both stages.
        /// </remarks>
        public static FluidCornerHeights SmoothedCornerHeights(
            in BlockTypeJobData centerProps, byte centerLevel,
            OptionalVoxelState nN, OptionalVoxelState nE, OptionalVoxelState nS, OptionalVoxelState nW,
            OptionalVoxelState nNE, OptionalVoxelState nSE, OptionalVoxelState nSW, OptionalVoxelState nNW,
            in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            return new FluidCornerHeights
            {
                TR = math.max(MinSurfaceHeight, SmoothedCornerHeight(in centerProps, centerLevel, nN, nE, nNE, in templates, in blockTypes)),
                TL = math.max(MinSurfaceHeight, SmoothedCornerHeight(in centerProps, centerLevel, nN, nW, nNW, in templates, in blockTypes)),
                BR = math.max(MinSurfaceHeight, SmoothedCornerHeight(in centerProps, centerLevel, nS, nE, nSE, in templates, in blockTypes)),
                BL = math.max(MinSurfaceHeight, SmoothedCornerHeight(in centerProps, centerLevel, nS, nW, nSW, in templates, in blockTypes)),
            };
        }

        /// <summary>
        /// The cell's drawn top-face corner heights: <see cref="SmoothedCornerHeights"/>, forced to a full
        /// 1.0 when the same fluid sits directly above.
        /// </summary>
        /// <param name="smoothed">The cell's smoothed corner heights.</param>
        /// <param name="hasFluidAbove">Whether the same fluid occupies the cell above.</param>
        /// <returns>The heights the mesher emits as top-face vertices.</returns>
        /// <remarks>
        /// Forcing the corners flat is what lets a submerged cell connect seamlessly to the one above it
        /// instead of leaving a smoothed lip inside the fluid body.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FluidCornerHeights SurfaceCornerHeights(in FluidCornerHeights smoothed, bool hasFluidAbove)
        {
            if (!hasFluidAbove) return smoothed;

            return new FluidCornerHeights { BL = 1f, BR = 1f, TL = 1f, TR = 1f };
        }

        /// <summary>
        /// The surface height at a point inside the cell, bilinear between the four corners.
        /// </summary>
        /// <param name="corners">The cell's drawn corner heights.</param>
        /// <param name="fracX">Position across the cell on X, 0–1.</param>
        /// <param name="fracZ">Position across the cell on Z, 0–1.</param>
        /// <returns>The surface height in block-local units above the cell floor.</returns>
        /// <remarks>
        /// An approximation of a quad the GPU rasterizes as <b>two triangles</b>, whose diagonal the mesher
        /// may flip by light value — so this can differ from the drawn surface by a small amount along that
        /// diagonal. The bound is accepted in the design doc's §8.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SampleSurfaceAt(in FluidCornerHeights corners, float fracX, float fracZ)
        {
            float back = math.lerp(corners.BL, corners.BR, fracX);
            float front = math.lerp(corners.TL, corners.TR, fracX);

            return math.lerp(back, front, fracZ);
        }

        /// <summary>
        /// Calculates the smoothed height for a fluid block's corner by averaging its height
        /// with adjacent and diagonal fluid neighbors. Prevents height smoothing through solid walls.
        /// </summary>
        /// <param name="centerProps">The properties of the center fluid block.</param>
        /// <param name="centerLevel">The fluid level of the center block.</param>
        /// <param name="n1">The first adjacent orthogonal neighbor.</param>
        /// <param name="n2">The second adjacent orthogonal neighbor.</param>
        /// <param name="nDiag">The diagonal neighbor shared by n1 and n2.</param>
        /// <param name="templates">The pre-computed height templates for this fluid type.</param>
        /// <param name="blockTypes">The global block types data array.</param>
        /// <returns>The averaged height for the evaluated corner.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SmoothedCornerHeight(in BlockTypeJobData centerProps, byte centerLevel,
            OptionalVoxelState n1, OptionalVoxelState n2, OptionalVoxelState nDiag,
            in NativeArray<float> templates, in NativeArray<BlockTypeJobData> blockTypes)
        {
            float totalHeight = templates[centerLevel];
            int count = 1;

            // Track if adjacent neighbors are fluids to determine if the diagonal path is open ---
            bool n1IsFluid = n1.HasValue && blockTypes[n1.State.ID].FluidType == centerProps.FluidType;
            bool n2IsFluid = n2.HasValue && blockTypes[n2.State.ID].FluidType == centerProps.FluidType;

            if (n1IsFluid)
            {
                totalHeight += templates[n1.State.FluidLevel];
                count++;
            }

            if (n2IsFluid)
            {
                totalHeight += templates[n2.State.FluidLevel];
                count++;
            }

            // Only consider the diagonal neighbor for smoothing if at least one of the
            // adjacent neighbors is also a fluid. This prevents height smoothing "through" solid corners.
            bool nDiagIsFluid = nDiag.HasValue && blockTypes[nDiag.State.ID].FluidType == centerProps.FluidType;
            if ((n1IsFluid || n2IsFluid) && nDiagIsFluid)
            {
                totalHeight += templates[nDiag.State.FluidLevel];
                count++;
            }

            return totalHeight / count;
        }
    }
}
