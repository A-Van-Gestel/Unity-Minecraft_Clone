using Data;
using Editor.Validation.Placement.Framework;
using UnityEngine;
using Id = Editor.Validation.Placement.Framework.TestPlacementBlockPalette.Id;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Far-coordinate baseline for the placement occupancy veto. <c>World.IsCellOccupiedForPlacement</c> used to take
    /// a <see cref="Vector3"/>, so <c>PlacementController.CanPlaceAt</c>'s integer cell converted implicitly and the
    /// veto consulted a cell up to ±64 voxels away past ±2²⁴ (BLOCK_BEHAVIOR #05).
    /// </summary>
    /// <remarks>
    /// This is the case open-air play testing cannot surface: the veto only misbehaves where the true cell and the
    /// rounded cell <i>differ in occupancy</i>. In open air both are air, the veto passes either way, and placement
    /// looks correct — which is why this needs a scenario that places directly against geometry.
    /// </remarks>
    public static partial class PlacementValidationSuite
    {
        /// <summary>
        /// A chunk-aligned far origin chunk (voxel origin x = 2,147,000,000 — the coordinate Fluid #17 was reported
        /// at, where <c>float</c>'s ULP is 128 so the rounded cell lands several chunks away).
        /// </summary>
        private static readonly ChunkCoord s_farOriginChunk = new ChunkCoord(2147000000 / VoxelData.ChunkWidth, 8);

        /// <summary>
        /// The occupancy veto must reject a placement into a solid cell in the far lands exactly as it does at the
        /// origin. Asserted as a near/far pair: the control proves the fixture geometry actually produces a rejection,
        /// so a far-only failure isolates the coordinate magnitude as the cause.
        /// <para><b>Prove-red:</b> retype <c>World.IsCellOccupiedForPlacement</c> back to <c>Vector3</c>. The control
        /// stays green and the far assertion goes red — the veto reads an unseeded chunk, calls the cell empty, and
        /// allows a placement straight into stone.</para>
        /// </summary>
        /// <returns>True when both the control and the far assertion hold.</returns>
        private static bool FarCoordinateOccupancyVetoRejectsSolid()
        {
            bool ok = true;

            using (PlacementTestWorld control = new PlacementTestWorld(TestPlacementBlockPalette.Create()))
            {
                control.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground);
                bool placeable = control.EvaluatePlacementAt(Id.Ground, new Vector3Int(COL_X, TARGET_Y, COL_Z));
                ok &= Expect(!placeable,
                    "control (origin chunk): the occupancy veto must reject a placement into a solid cell — if this " +
                    "fails the fixture never produced a rejection and the far assertion proves nothing");
            }

            using (PlacementTestWorld far =
                   new PlacementTestWorld(TestPlacementBlockPalette.Create(), s_farOriginChunk))
            {
                far.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground);
                bool placeable = far.EvaluatePlacementAt(Id.Ground, new Vector3Int(COL_X, TARGET_Y, COL_Z));
                ok &= Expect(!placeable,
                    "far lands: the occupancy veto resolved the wrong cell past ±2²⁴ and allowed a placement into a " +
                    "solid block (BLOCK_BEHAVIOR #05)");
            }

            return ok;
        }
    }
}
