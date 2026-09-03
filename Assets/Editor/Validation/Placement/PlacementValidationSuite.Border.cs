using System.Collections.Generic;
using Data;
using Editor.Validation.Placement.Framework;
using UnityEngine;
using Id = Editor.Validation.Placement.Framework.TestPlacementBlockPalette.Id;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// TF-14 world-border scenarios — the guard for the border <b>edit gate</b>: player placement must be refused
    /// outside the per-world gameplay fence, while a disabled border (radius 0, every pre-existing scenario) changes
    /// nothing. The border is a voxel-space AABB centered on the world origin, so the gate must also convert
    /// through the floating origin (WS-4) rather than comparing Unity-space cells against the radius.
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        // Border-scenario geometry: a radius-8 fence puts voxel cells [-8, 7] inside on each axis, so within the
        // harness's single 0-15 chunk the low columns are inside and the high columns are outside.
        private const int BORDER_RADIUS = 8;
        private const int BORDER_IN_X = 2; // inside the fence
        private const int BORDER_OUT_X = 12; // outside the fence
        private const int BORDER_EDGE_IN_X = 7; // last cell inside ([-r, r) semantics)
        private const int BORDER_EDGE_OUT_X = 8; // first cell outside
        private const int BORDER_Z = 2;

        static partial void AddBorderScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("TF-14: border gate blocks placement outside the fence",
                BorderBlocksPlacementOutsideFence));
            scenarios.Add(new Scenario("TF-14: border gate converts through the floating origin",
                BorderGateIsOriginAware));
        }

        /// <summary>
        /// At the identity origin with a radius-8 border: placement inside the fence stays valid, placement outside
        /// is refused by the world gate (<c>PlacementController.CanPlaceAt</c> → <c>World.IsVoxelInsideBorder</c>),
        /// and the boundary lands exactly on the <c>[-radius, radius)</c> cell edge the rendered wall sits on.
        /// </summary>
        private static bool BorderBlocksPlacementOutsideFence()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBorderRadius(BORDER_RADIUS);

            world.SetBlock(BORDER_IN_X, TARGET_Y, BORDER_Z, Id.Ground);
            world.SetBlock(BORDER_OUT_X, TARGET_Y, BORDER_Z, Id.Ground);

            PlacementOutcome inside = world.ResolveTopDownPlacement(Id.Ground, BORDER_IN_X, BORDER_Z);
            bool ok = Expect(inside.LandsOnTop, "placement inside the border must stay valid");

            PlacementOutcome outside = world.ResolveTopDownPlacement(Id.Ground, BORDER_OUT_X, BORDER_Z);
            ok &= Expect(outside.DidHit, "the probe still hits terrain outside the border (aiming is not gated)");
            ok &= Expect(!outside.Placeable, "placement outside the border must be refused by the world gate");

            // The gate's edge must match the rendered wall: [-radius, radius) — cell 7 is the last inside, cell 8
            // (flush against the +X wall's outer side) the first outside.
            ok &= Expect(world.EvaluatePlacementAt(Id.Ground, new Vector3Int(BORDER_EDGE_IN_X, TARGET_Y, BORDER_Z)),
                $"cell x={BORDER_EDGE_IN_X} is the last cell inside a radius-{BORDER_RADIUS} border");
            ok &= Expect(!world.EvaluatePlacementAt(Id.Ground, new Vector3Int(BORDER_EDGE_OUT_X, TARGET_Y, BORDER_Z)),
                $"cell x={BORDER_EDGE_OUT_X} is the first cell outside a radius-{BORDER_RADIUS} border");

            return ok;
        }

        /// <summary>
        /// The border is voxel space: at a far floating origin the harness's small Unity-space cells sit at far
        /// voxel coordinates, so a small border refuses them all — while a border wide enough to contain the far
        /// chunk accepts them. A gate comparing unconverted Unity-space cells would get both cases wrong.
        /// </summary>
        private static bool BorderGateIsOriginAware()
        {
            ChunkCoord farOrigin = new ChunkCoord(625, 625); // ~10k voxels out
            Vector3Int placeCell = new Vector3Int(BORDER_IN_X, TARGET_Y, BORDER_Z); // Unity-space cell

            using (PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create(), farOrigin))
            {
                world.SetBorderRadius(BORDER_RADIUS);
                bool refused = !world.EvaluatePlacementAt(Id.Ground, placeCell);
                if (!Expect(refused,
                        "a radius-8 border must refuse cells ~10k voxels out, however small their Unity-space value"))
                    return false;
            }

            using (PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create(), farOrigin))
            {
                const int wideRadius = 625 * 16 + 64; // contains the far chunk
                world.SetBorderRadius(wideRadius);
                return Expect(world.EvaluatePlacementAt(Id.Ground, placeCell),
                    "a border containing the far chunk must accept its cells");
            }
        }
    }
}
