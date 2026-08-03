using System.Collections.Generic;
using Data;
using Editor.Validation.Placement.Framework;
using Helpers;
using Unity.Mathematics;
using UnityEngine;
using Id = Editor.Validation.Placement.Framework.TestPlacementBlockPalette.Id;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Sub-voxel targeting scenarios (VQ-3) — the ray's <b>narrow phase</b>. Every other scenario in this suite
    /// targets full-cube blocks, where cell occupancy and block volume coincide and the narrow phase cannot be
    /// observed at all. These use a half-slab: a solid block filling only the lower half of its cell, so the upper
    /// half is empty space a ray must pass through.
    /// <para>
    /// The rotated (top-slab) cases resolve their metadata through the production
    /// <see cref="BlockCollisionBoundsUtility"/> rather than hard-coding a byte, so the scenarios stay correct if
    /// the <see cref="MetadataSchema.Facing6Roll2"/> encoding is ever renumbered — and a failure to find a flipped
    /// orientation is reported as a fixture failure rather than silently testing the unrotated block twice.
    /// </para>
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        /// <summary>Cell the sub-voxel scenarios seed their slab in.</summary>
        private const int SLAB_X = 6;

        /// <summary>Cell the sub-voxel scenarios seed their slab in.</summary>
        private const int SLAB_Y = 6;

        /// <summary>Cell the sub-voxel scenarios seed their slab in.</summary>
        private const int SLAB_Z = 6;

        /// <summary>Distance behind the slab at which a backstop block is seeded, to catch a ray that passes through.</summary>
        private const int BACKSTOP_OFFSET = 4;

        static partial void AddSubVoxelScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("VQ-3: ray over a half-slab's empty top reaches the block behind it",
                RayOverSlabTopPassesThrough));
            scenarios.Add(new Scenario("VQ-3: ray into a half-slab's solid lower half hits it",
                RayIntoSlabBodyHits));
            scenarios.Add(new Scenario("VQ-3: hitting a half-slab's top face reports an interior +Y normal",
                SlabTopFaceReportsInteriorNormal));
            scenarios.Add(new Scenario("VQ-3: a rotated top slab occupies the upper half instead",
                RotatedTopSlabOccupiesUpperHalf));
            scenarios.Add(new Scenario("VQ-3: full-block targeting is unchanged by the narrow phase",
                FullBlockTargetingUnchanged));
        }

        /// <summary>
        /// A horizontal ray through the empty upper half of a half-slab must not stop on it — the cell is occupied,
        /// but the block's volume is not in the way. Without a narrow phase the ray stops on the slab's cell.
        /// </summary>
        private static bool RayOverSlabTopPassesThrough()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(SLAB_X, SLAB_Y, SLAB_Z, Id.HalfSlab);
            world.SetBlock(SLAB_X + BACKSTOP_OFFSET, SLAB_Y, SLAB_Z, Id.Ground);

            // Waist-height through the slab's empty top (y = 0.75 of the cell), aimed along +X.
            Vector3 origin = new Vector3(SLAB_X - 3f, SLAB_Y + 0.75f, SLAB_Z + 0.5f);
            bool hit = world.MarchRay(origin, Vector3.right, out Vector3Int hitCell, out int3 _, out Vector3Int _);

            bool ok = Expect(hit, "the ray should continue to the backstop, not miss entirely");
            ok &= Expect(hitCell == new Vector3Int(SLAB_X + BACKSTOP_OFFSET, SLAB_Y, SLAB_Z),
                $"expected the backstop at ({SLAB_X + BACKSTOP_OFFSET}, {SLAB_Y}, {SLAB_Z}), got {hitCell} " +
                "— the ray stopped on the half-slab's empty upper half");
            return ok;
        }

        /// <summary>The same ray, lowered into the slab's solid half, must stop on it.</summary>
        private static bool RayIntoSlabBodyHits()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(SLAB_X, SLAB_Y, SLAB_Z, Id.HalfSlab);
            world.SetBlock(SLAB_X + BACKSTOP_OFFSET, SLAB_Y, SLAB_Z, Id.Ground);

            Vector3 origin = new Vector3(SLAB_X - 3f, SLAB_Y + 0.25f, SLAB_Z + 0.5f);
            bool hit = world.MarchRay(origin, Vector3.right, out Vector3Int hitCell, out int3 normal, out Vector3Int _);

            bool ok = Expect(hit, "a ray through the slab's solid half should hit it");
            ok &= Expect(hitCell == new Vector3Int(SLAB_X, SLAB_Y, SLAB_Z),
                $"expected the slab cell, got {hitCell}");
            ok &= Expect(normal.Equals(new int3(-1, 0, 0)),
                $"a +X ray entering the slab's -X face should report (-1,0,0), got {normal}");
            return ok;
        }

        /// <summary>
        /// The reported face must be the face of the <i>block</i>, not of the cell. The discriminating case is an
        /// oblique ray that enters the cell through its <c>-X</c> side <i>above</i> the slab, then descends onto the
        /// slab's top at y = 0.5: the cell says <c>-X</c>, the block says <c>+Y</c>, and only the latter puts a
        /// placed block on top of the slab rather than beside it. A straight-down ray is asserted too, where the two
        /// answers coincide — that one pins the placement arithmetic rather than the face derivation.
        /// </summary>
        private static bool SlabTopFaceReportsInteriorNormal()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(SLAB_X, SLAB_Y, SLAB_Z, Id.HalfSlab);

            // Enters the cell at (SLAB_X, SLAB_Y + 0.7) — above the slab — and crosses y = SLAB_Y + 0.5 while still
            // inside it, so the cell face and the block face genuinely disagree.
            Vector3 obliqueOrigin = new Vector3(SLAB_X - 3f, SLAB_Y + 2.2f, SLAB_Z + 0.5f);
            Vector3 obliqueDir = new Vector3(1f, -0.5f, 0f).normalized;
            bool obliqueHit = world.MarchRay(obliqueOrigin, obliqueDir, out Vector3Int obliqueCell,
                out int3 obliqueNormal, out Vector3Int obliqueAdjacent);

            bool ok = Expect(obliqueHit, "the oblique ray should land on the slab");
            ok &= Expect(obliqueCell == new Vector3Int(SLAB_X, SLAB_Y, SLAB_Z),
                $"expected the slab cell, got {obliqueCell}");
            ok &= Expect(obliqueNormal.Equals(new int3(0, 1, 0)),
                $"the ray crosses the slab's TOP at y={SLAB_Y + 0.5f} after entering the cell through -X; " +
                $"expected (0,1,0), got {obliqueNormal} (the cell's own entry face)");
            ok &= Expect(obliqueAdjacent == new Vector3Int(SLAB_X, SLAB_Y + 1, SLAB_Z),
                $"a block placed here belongs on top of the slab, got {obliqueAdjacent}");

            // Straight down: cell face and block face agree, so this pins adjacency, not the derivation.
            bool downHit = world.MarchRay(new Vector3(SLAB_X + 0.5f, SLAB_Y + 4f, SLAB_Z + 0.5f), Vector3.down,
                out Vector3Int downCell, out int3 downNormal, out Vector3Int downAdjacent);
            ok &= Expect(downHit && downCell == new Vector3Int(SLAB_X, SLAB_Y, SLAB_Z)
                         && downNormal.Equals(new int3(0, 1, 0))
                         && downAdjacent == new Vector3Int(SLAB_X, SLAB_Y + 1, SLAB_Z),
                $"downward ray: got hit={downHit} cell={downCell} face={downNormal} adjacent={downAdjacent}");
            return ok;
        }

        /// <summary>
        /// Rotated into a top slab, the empty half moves to the bottom: the ray that passed over the unrotated slab
        /// now hits, and a ray through the lower half now passes through.
        /// </summary>
        private static bool RotatedTopSlabOccupiesUpperHalf()
        {
            BlockType[] palette = TestPlacementBlockPalette.Create();
            if (!TryFindFlippedSlabMeta(palette[Id.HalfSlab], out byte topMeta))
                return Expect(false, "fixture: no Facing6Roll2 metadata rotates the slab into the cell's upper half");

            using PlacementTestWorld world = new PlacementTestWorld(palette);
            world.SetBlock(SLAB_X, SLAB_Y, SLAB_Z, Id.HalfSlab, topMeta);
            world.SetBlock(SLAB_X + BACKSTOP_OFFSET, SLAB_Y, SLAB_Z, Id.Ground);

            // Upper half is now solid -> the high ray stops on the slab.
            bool highHit = world.MarchRay(new Vector3(SLAB_X - 3f, SLAB_Y + 0.75f, SLAB_Z + 0.5f), Vector3.right,
                out Vector3Int highCell, out int3 _, out Vector3Int _);
            bool ok = Expect(highHit && highCell == new Vector3Int(SLAB_X, SLAB_Y, SLAB_Z),
                $"a top slab should stop a ray through the cell's upper half, got hit={highHit} cell={highCell}");

            // Lower half is now empty -> the low ray reaches the backstop.
            bool lowHit = world.MarchRay(new Vector3(SLAB_X - 3f, SLAB_Y + 0.25f, SLAB_Z + 0.5f), Vector3.right,
                out Vector3Int lowCell, out int3 _, out Vector3Int _);
            ok &= Expect(lowHit && lowCell == new Vector3Int(SLAB_X + BACKSTOP_OFFSET, SLAB_Y, SLAB_Z),
                $"a top slab should let a ray through the cell's lower half, got hit={lowHit} cell={lowCell}");
            return ok;
        }

        /// <summary>
        /// The narrow phase must be invisible to full-cube blocks: they take the fast path and target exactly as
        /// they did before. Guards the 99%-of-terrain case the other scenarios do not touch.
        /// </summary>
        private static bool FullBlockTargetingUnchanged()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());
            world.SetBlock(SLAB_X, SLAB_Y, SLAB_Z, Id.Ground);

            bool ok = true;
            foreach ((Vector3 origin, Vector3 direction, int3 expectedFace) in new[]
                     {
                         (new Vector3(SLAB_X - 3f, SLAB_Y + 0.75f, SLAB_Z + 0.5f), Vector3.right, new int3(-1, 0, 0)),
                         (new Vector3(SLAB_X - 3f, SLAB_Y + 0.25f, SLAB_Z + 0.5f), Vector3.right, new int3(-1, 0, 0)),
                         (new Vector3(SLAB_X + 0.5f, SLAB_Y + 4f, SLAB_Z + 0.5f), Vector3.down, new int3(0, 1, 0)),
                     })
            {
                bool hit = world.MarchRay(origin, direction, out Vector3Int cell, out int3 normal, out Vector3Int _);
                ok &= Expect(hit && cell == new Vector3Int(SLAB_X, SLAB_Y, SLAB_Z) && normal.Equals(expectedFace),
                    $"full block from {origin} dir {direction}: expected cell ({SLAB_X},{SLAB_Y},{SLAB_Z}) " +
                    $"face {expectedFace}, got hit={hit} cell={cell} face={normal}");
            }

            return ok;
        }

        /// <summary>
        /// Finds a metadata byte that rotates the slab's authored lower-half volume into the cell's upper half, by
        /// asking the production bounds resolver rather than assuming an encoding.
        /// </summary>
        /// <param name="slab">The half-slab block type.</param>
        /// <param name="meta">The metadata byte producing an upper-half volume, when one exists.</param>
        /// <returns>True if a flipped orientation was found.</returns>
        private static bool TryFindFlippedSlabMeta(BlockType slab, out byte meta)
        {
            for (int candidate = 0; candidate <= byte.MaxValue; candidate++)
            {
                Bounds bounds = BlockCollisionBoundsUtility.GetBounds(slab, (byte)candidate, Vector3.zero);
                if (bounds.min.y <= 0.49f || bounds.max.y <= 0.99f) continue;

                meta = (byte)candidate;
                return true;
            }

            meta = 0;
            return false;
        }
    }
}
