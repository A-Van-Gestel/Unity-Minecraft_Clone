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
        // Declared as a chunk index, never as a voxel coordinate divided down — see the matching note in
        // BehaviorValidationSuite.Baseline.FarCoordinate.cs. 134,187,500 × 16 = voxel x 2,147,000,000, the
        // coordinate Fluid #17 was reported at, where float's ULP is 128 so the rounded cell lands chunks away.
        private const int FAR_CHUNK_X = 134187500;
        private const int FAR_CHUNK_Z = 8;

        /// <summary>The far-lands origin chunk the controller is driven at (chunk indices, so alignment is implicit).</summary>
        private static readonly ChunkCoord s_farOriginChunk = new ChunkCoord(FAR_CHUNK_X, FAR_CHUNK_Z);

        /// <summary>
        /// The occupancy veto must behave identically in the far lands and at the origin — asserted in <b>both</b>
        /// directions at each magnitude, because a one-sided "it rejects a solid cell" assertion passes just as well
        /// when something rejects <i>everything</i> out there (a border or bounds regression on the same
        /// <c>CanPlaceAt</c> path would do exactly that, and the veto would never be consulted).
        /// <para><b>Prove-red:</b> retype <c>World.IsCellOccupiedForPlacement</c> back to <c>Vector3</c>. The
        /// origin legs and the far <i>empty-cell</i> leg stay green; the far <i>solid-cell</i> leg goes red — the veto
        /// reads an unseeded chunk, calls the cell empty, and allows a placement straight into stone.</para>
        /// </summary>
        /// <returns>True when all four legs hold.</returns>
        private static bool FarCoordinateOccupancyVetoRejectsSolid()
        {
            bool ok = true;

            using (PlacementTestWorld control = new PlacementTestWorld(TestPlacementBlockPalette.Create()))
            {
                ok &= Expect(control.EvaluatePlacementAt(Id.Ground, new Vector3Int(COL_X, TARGET_Y, COL_Z)),
                    "control (origin chunk): an empty cell must be placeable — if this fails the fixture rejects " +
                    "everything and the occupancy assertions below prove nothing");

                control.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground);
                ok &= Expect(!control.EvaluatePlacementAt(Id.Ground, new Vector3Int(COL_X, TARGET_Y, COL_Z)),
                    "control (origin chunk): the occupancy veto must reject a placement into a solid cell");
            }

            using (PlacementTestWorld far =
                   new PlacementTestWorld(TestPlacementBlockPalette.Create(), s_farOriginChunk))
            {
                // Non-vacuity: proves the far cell is reachable at all — that nothing on the CanPlaceAt path
                // (in-world bound, TF-14 border) is rejecting far coordinates outright and masking the veto.
                ok &= Expect(far.EvaluatePlacementAt(Id.Ground, new Vector3Int(COL_X, TARGET_Y, COL_Z)),
                    "far lands: an empty cell must still be placeable — something on the CanPlaceAt path is " +
                    "rejecting far coordinates outright, so the occupancy assertion below is vacuous");

                far.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground);
                ok &= Expect(!far.EvaluatePlacementAt(Id.Ground, new Vector3Int(COL_X, TARGET_Y, COL_Z)),
                    "far lands: the occupancy veto resolved the wrong cell past ±2²⁴ and allowed a placement into a " +
                    "solid block (BLOCK_BEHAVIOR #05)");
            }

            return ok;
        }
    }
}
