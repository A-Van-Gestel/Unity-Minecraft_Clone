using System.Collections.Generic;
using Data;
using Editor.Validation.Placement.Framework;
using UnityEngine;
using Id = Editor.Validation.Placement.Framework.TestPlacementBlockPalette.Id;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// WS-4a floating-origin scenarios for the placement suite — the <b>call-site</b> guard for the origin plumbing.
    /// <para>
    /// The Chunk Math suite proves <c>WorldOrigin</c>'s arithmetic; these prove the arithmetic is actually <i>applied</i>
    /// where it matters. WS-4a ships with the origin pinned at (0, 0), where a conversion that was never threaded is
    /// invisible — so the only way to falsify the plumbing before WS-4b is to drive the real
    /// <see cref="PlacementController"/> at a non-zero origin and watch what it resolves. This is that test, aimed at
    /// the fragility hotspot: the ray march queries the world once per step, and the probe returns cells that become
    /// persisted <c>VoxelMod.GlobalPosition</c> values.
    /// </para>
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        // Origins the battery is replayed at. Each moves the world's voxel coordinates far from the render origin
        // while the harness keeps addressing the same small Unity-space cells.
        private static readonly ChunkCoord[] s_placementOriginCases =
        {
            new ChunkCoord(0, 0), // identity — the shipped WS-4a path
            new ChunkCoord(625, 625), // ~10k voxels — the observed in-game jitter onset
            new ChunkCoord(-625, -625), // negative quadrant (WS-3)
            new ChunkCoord(1 << 26, -(1 << 26)), // the permanent ±2^31 voxel edge
        };

        static partial void AddWorldOriginScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("WS-4a: placement decisions are origin-invariant", PlacementIsOriginInvariant));
            scenarios.Add(new Scenario("WS-4a: probe returns Unity-space cells at a far origin", ProbeCellsStayUnitySpace));
        }

        /// <summary>
        /// The whole placement decision must be origin-invariant: the same model probed through the same Unity-space
        /// ray resolves the same outcome at every origin. A query that failed to convert would look for the seeded
        /// blocks at the render origin, find no chunk there, and stop hitting anything — so this goes red.
        /// </summary>
        private static bool PlacementIsOriginInvariant()
        {
            PlacementOutcome[] expected = RunPlacementBattery(s_placementOriginCases[0]);
            bool ok = true;

            for (int i = 1; i < s_placementOriginCases.Length; i++)
            {
                ChunkCoord origin = s_placementOriginCases[i];
                PlacementOutcome[] actual = RunPlacementBattery(origin);

                for (int c = 0; c < expected.Length; c++)
                {
                    ok &= Expect(actual[c].Equals(expected[c]),
                        $"origin ({origin.X}, {origin.Z}) case {c}: expected {Describe(expected[c])}, got {Describe(actual[c])}");
                }
            }

            return ok;
        }

        /// <summary>
        /// The probe's cells cross back out to the highlight transforms and the player-AABB veto, so they must stay
        /// Unity space (small, near the render origin) — never the far absolute voxel coordinate.
        /// </summary>
        private static bool ProbeCellsStayUnitySpace()
        {
            ChunkCoord farOrigin = s_placementOriginCases[^1];
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create(), farOrigin);
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground);

            PlacementOutcome outcome = world.ResolveTopDownPlacement(Id.Ground, COL_X, COL_Z);

            bool ok = Expect(outcome.DidHit, "the probe must still hit the seeded block at a far origin");
            ok &= Expect(outcome.HitCell == new Vector3Int(COL_X, TARGET_Y, COL_Z),
                $"HitCell must be the Unity-space cell ({COL_X}, {TARGET_Y}, {COL_Z}), got {outcome.HitCell}");
            ok &= Expect(outcome.PlaceCell == new Vector3Int(COL_X, TARGET_Y + 1, COL_Z),
                $"PlaceCell must be the Unity-space cell ({COL_X}, {TARGET_Y + 1}, {COL_Z}), got {outcome.PlaceCell}");
            return ok;
        }

        /// <summary>
        /// Runs a fixed battery of placement decisions against a freshly-seeded model at the given origin. The model
        /// and the probes are addressed identically at every origin, so the outcomes are directly comparable.
        /// </summary>
        /// <param name="origin">The floating-origin anchor to drive the controller at.</param>
        /// <returns>One outcome per battery case, in a stable order.</returns>
        private static PlacementOutcome[] RunPlacementBattery(ChunkCoord origin)
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create(), origin);

            // Column A: a plain solid — the held block should land on top.
            world.SetBlock(COL_X, TARGET_Y, COL_Z, Id.Ground);

            // Column B: a soft plant over solid ground — the held block should replace the plant.
            world.SetBlock(COL_X + 2, TARGET_Y, COL_Z, Id.Ground);
            world.SetBlock(COL_X + 2, TARGET_Y + 1, COL_Z, Id.SoftPlant);

            // Column C: an unbreakable block — never replaced.
            world.SetBlock(COL_X + 4, TARGET_Y, COL_Z, Id.Unbreakable);

            // Column D: fluid over ground — a REQUIRES_SUPPORT block must not float on it.
            world.SetBlock(COL_X + 6, TARGET_Y, COL_Z, Id.Fluid);

            return new[]
            {
                world.ResolveTopDownPlacement(Id.Ground, COL_X, COL_Z),
                world.ResolveTopDownPlacement(Id.Ground, COL_X + 2, COL_Z),
                world.ResolveTopDownPlacement(null, COL_X, COL_Z),
                world.ResolveTopDownPlacement(Id.Ground, COL_X + 4, COL_Z),
                world.ResolveTopDownPlacement(Id.SupportNeeding, COL_X + 6, COL_Z),

                // An empty column: the probe must miss identically at every origin (not "miss because the chunk
                // wasn't found", which is what an unconverted query would produce everywhere).
                world.ResolveTopDownPlacement(Id.Ground, COL_X + 8, COL_Z),
            };
        }

        /// <summary>Renders an outcome compactly for assertion messages.</summary>
        private static string Describe(PlacementOutcome o) =>
            $"[hit={o.DidHit} cell={o.HitCell} replaces={o.Replaces} place={o.PlaceCell} placeable={o.Placeable}]";
    }
}
