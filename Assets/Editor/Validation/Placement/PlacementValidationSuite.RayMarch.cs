using System.Collections.Generic;
using Editor.Validation.Placement.Framework;
using Helpers;
using Unity.Mathematics;
using UnityEngine;
using Id = Editor.Validation.Placement.Framework.TestPlacementBlockPalette.Id;
using Random = Unity.Mathematics.Random;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Placement
{
    /// <summary>
    /// Ray-march <b>geometry</b> scenarios (VQ-2) — the half of the placement decision every other scenario in this
    /// suite is blind to. All of those probe straight down through cell centres, where any traversal that advances one
    /// cell per step is correct by construction; only an oblique ray can distinguish an exact voxel traversal from
    /// fixed-increment sampling. These assert the two properties the march owes its callers:
    /// <list type="number">
    /// <item><b>No skipped cells</b> — the reported hit is the <i>first</i> hittable cell along the ray, not merely
    /// some hittable cell (a fixed step can straddle a corner-clipped cell entirely).</item>
    /// <item><b>An exact entered face</b> — the normal names the face the ray actually crossed. It is not cosmetic:
    /// <c>PlayerInteraction.ComputePlacementMeta</c> feeds it to <c>Facing6FromHitNormal</c>, so a wrong face writes
    /// wrong orientation metadata into a <b>persisted</b> <c>VoxelMod</c>.</item>
    /// </list>
    /// </summary>
    public static partial class PlacementValidationSuite
    {
        /// <summary>Seed for the no-skip fuzz sweep — fixed so a failure is reproducible from the log alone.</summary>
        private const uint RAY_FUZZ_SEED = 0x5EED_1A11;

        /// <summary>Rays per fuzz sweep. Large enough that corner-clipped cells occur, small enough to stay instant.</summary>
        private const int RAY_FUZZ_COUNT = 500;

        /// <summary>Cell range the fuzz sweep seeds blocks in and confines its rays to (inside the origin chunk).</summary>
        private const int FUZZ_EXTENT = 12;

        /// <summary>Fraction of cells the fuzz sweep fills, tuned so rays usually hit but rarely on the first cell.</summary>
        private const float FUZZ_FILL_CHANCE = 0.08f;

        /// <summary>
        /// Offset applied to a 45-degree ray's constant <c>x - y</c> so it grazes cells through a corner. The
        /// complement (1 - this) is how far into the cell the ray reaches, hence how briefly it is inside.
        /// </summary>
        private const float GRAZE_OFFSET = 0.999f;

        /// <summary>
        /// How far past a face a ray enters, as a fraction of a cell, for the entered-face scenarios. Small enough
        /// that the crossed face's own fractional offset is the <i>largest</i> of the three shortly after entry —
        /// the configuration a fractional-offset heuristic reads backwards.
        /// </summary>
        private const float EDGE_INSET = 0.001f;

        /// <summary>
        /// Distance a face-probe ray starts back from its entry point. Deliberately not a multiple of any plausible
        /// sampling step, so no sample can land exactly on the face plane (where a fractional offset of zero would
        /// make an after-the-fact derivation accidentally correct).
        /// </summary>
        private const float FACE_PROBE_BACKOFF = 3.023f;

        /// <summary>Transverse velocity fraction for the face probes — enough to skew the offsets, too little to change which face is crossed.</summary>
        private const float FACE_PROBE_SKEW = 0.01f;

        /// <summary>
        /// Step ceiling for the degenerate-input cases. Far above any legitimate traversal (a reach-8 ray crosses
        /// ~14 cells), so reaching it means the walk is not self-terminating.
        /// </summary>
        private const int DEGENERATE_STEP_CAP = 4000;

        static partial void AddRayMarchScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("VQ-2: corner-clipped cell is not skipped",
                CornerClippedCellIsNotSkipped));
            scenarios.Add(new Scenario("VQ-2: fuzz — reported hit is the first hittable cell on the ray",
                FuzzReportedHitIsFirstHittableCell));
            scenarios.Add(new Scenario("VQ-2: entered face is exact for all six faces",
                EnteredFaceIsExactForAllSixFaces));
            scenarios.Add(new Scenario("VQ-2: normal and adjacent cell agree with the ray's approach",
                NormalAndAdjacentCellAgreeWithApproach));
            scenarios.Add(new Scenario("VQ-2: ray starting inside a block reports a defined entered face",
                RayStartingInsideBlockHasDefinedFace));
            scenarios.Add(new Scenario("VQ-2: degenerate ray input terminates the traversal",
                DegenerateRayInputTerminates));
        }

        /// <summary>
        /// A ray that clips one corner of a near block before reaching a far block. The near block is crossed over a
        /// parameter span far shorter than one sampling step, so fixed-increment sampling steps over it and reports
        /// the far block; an exact traversal reports the near one.
        /// </summary>
        private static bool CornerClippedCellIsNotSkipped()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());

            // A 45-degree ray in XY holds x - y constant. Offsetting that constant to just under 1 makes the ray
            // graze each (n, n) cell through a corner: it is inside for a span of GRAZE_SPAN, two orders of magnitude
            // below any sane sampling step, while the (n, n-1) cells between them are crossed almost end to end.
            const int nearX = 6, nearY = 6, z = 6;
            const int backstopX = 10, backstopY = 9;
            world.SetBlock(nearX, nearY, z, Id.Ground);
            world.SetBlock(backstopX, backstopY, z, Id.Ground);

            // The start Y is deliberately not a round number, so the cell boundaries fall at irregular ray
            // parameters: a sampling traversal cannot pass this by landing a step exactly on the entry point.
            const float startY = 3.017f;
            Vector3 origin = new Vector3(startY + GRAZE_OFFSET, startY, z + 0.5f);
            Vector3 dir = new Vector3(1f, 1f, 0f).normalized;

            bool hit = world.MarchRay(origin, dir, out Vector3Int hitCell, out int3 _, out Vector3Int _);

            bool ok = Expect(hit, "the ray should hit something (the far backstop at minimum)");
            ok &= Expect(hitCell == new Vector3Int(nearX, nearY, z),
                $"the corner-grazed near cell ({nearX}, {nearY}, {z}) must be the reported hit, got {hitCell} " +
                $"(the backstop is ({backstopX}, {backstopY}, {z}))");
            return ok;
        }

        /// <summary>
        /// Fuzzes oblique rays over a randomly-seeded field and checks the march against an <b>independent</b>
        /// property rather than a reimplementation: whatever cell the march reports, no <i>earlier</i> cell on the
        /// same ray may be hittable. Finds skipped cells regardless of which sampling artifact caused them.
        /// </summary>
        private static bool FuzzReportedHitIsFirstHittableCell()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());

            Random rng = new Random(RAY_FUZZ_SEED);
            HashSet<Vector3Int> solid = new HashSet<Vector3Int>();

            for (int x = 0; x < FUZZ_EXTENT; x++)
            for (int y = 0; y < FUZZ_EXTENT; y++)
            for (int z = 0; z < FUZZ_EXTENT; z++)
            {
                if (rng.NextFloat() >= FUZZ_FILL_CHANCE) continue;
                world.SetBlock(x, y, z, Id.Ground);
                solid.Add(new Vector3Int(x, y, z));
            }

            int failures = 0;
            for (int i = 0; i < RAY_FUZZ_COUNT && failures < 3; i++)
            {
                Vector3 origin = new Vector3(rng.NextFloat(0f, FUZZ_EXTENT), rng.NextFloat(0f, FUZZ_EXTENT),
                    rng.NextFloat(0f, FUZZ_EXTENT));
                float3 d = rng.NextFloat3Direction();
                Vector3 dir = new Vector3(d.x, d.y, d.z);

                if (!world.MarchRay(origin, dir, out Vector3Int hitCell, out int3 _, out Vector3Int _))
                {
                    // A miss is only correct if the ray crosses no solid cell at all.
                    if (FirstSolidOnRay(origin, dir, solid, out Vector3Int missed))
                    {
                        failures++;
                        Expect(false, $"ray from {origin} dir {dir} missed, but crosses solid cell {missed}");
                    }

                    continue;
                }

                if (!FirstSolidOnRay(origin, dir, solid, out Vector3Int expected) || expected == hitCell) continue;

                failures++;
                Expect(false,
                    $"ray from {origin} dir {dir} reported {hitCell}, but crosses {expected} first (cell skipped)");
            }

            return failures == 0;
        }

        /// <summary>
        /// Independently determines the first solid cell a ray crosses, by intersecting the ray analytically with
        /// each seeded cell's AABB (slab method) and taking the nearest entry. Shares neither logic nor
        /// <i>failure mode</i> with the traversal under test: any oracle that walks the ray in steps can itself skip a
        /// cell whose chord is shorter than one step — which would blame the code under test for the oracle's miss —
        /// whereas a closed-form intersection cannot miss a crossing at any chord length.
        /// </summary>
        /// <param name="origin">Ray start, Unity space.</param>
        /// <param name="dir">Ray direction (unit length).</param>
        /// <param name="solid">The cells seeded solid.</param>
        /// <param name="cell">The first solid cell crossed, when one exists.</param>
        /// <returns>True if the ray crosses a solid cell within the fuzz extent.</returns>
        private static bool FirstSolidOnRay(Vector3 origin, Vector3 dir, HashSet<Vector3Int> solid,
            out Vector3Int cell)
        {
            float3 o = new float3(origin.x, origin.y, origin.z);
            float3 d = new float3(dir.x, dir.y, dir.z);
            float nearest = float.PositiveInfinity;
            cell = default;

            foreach (Vector3Int candidate in solid)
            {
                float3 lo = new float3(candidate.x, candidate.y, candidate.z);

                // Per-axis slab entry/exit. A zero direction component yields +/-infinity here, which min/max carry
                // through correctly: the ray is parallel to that slab and is bounded by the other two.
                float3 t1 = (lo - o) / d;
                float3 t2 = (lo + 1f - o) / d;
                float entry = math.cmax(math.min(t1, t2));
                float exit = math.cmin(math.max(t1, t2));

                // Behind the origin, missed entirely, or beyond the sweep: not a candidate.
                if (exit < 0f || entry > exit || entry >= FUZZ_EXTENT * 2f) continue;

                // A ray starting inside a solid cell enters it at t = 0 by convention, matching the traversal's
                // treatment of the origin cell.
                float t = math.max(entry, 0f);
                if (t >= nearest) continue;

                nearest = t;
                cell = candidate;
            }

            return !float.IsPositiveInfinity(nearest);
        }

        /// <summary>
        /// Fires a steeply-angled ray at each of a lone block's six faces, aimed near the face's edge where the
        /// fractional-offset heuristic is least reliable. The reported normal must be the outward normal of the face
        /// the ray actually crossed.
        /// </summary>
        private static bool EnteredFaceIsExactForAllSixFaces()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());

            const int bx = 8, by = 8, bz = 8;
            world.SetBlock(bx, by, bz, Id.Ground);

            Vector3 block = new Vector3(bx, by, bz);
            const float e = EDGE_INSET;
            const float s = FACE_PROBE_SKEW;
            bool ok = true;

            // Each ray crosses its face just inside one edge, drifting along that edge as it goes. Shortly after
            // entry the crossed face's own offset is the largest of the three, so any derivation that reads the
            // smallest offset names one of the two faces the ray never touched.
            ok &= ExpectFace(world, block + new Vector3(0f, e, 0.5f), new Vector3(1f, s, 0f),
                new int3(-1, 0, 0), "-X face");
            ok &= ExpectFace(world, block + new Vector3(1f, e, 0.5f), new Vector3(-1f, s, 0f),
                new int3(1, 0, 0), "+X face");
            ok &= ExpectFace(world, block + new Vector3(e, 0f, 0.5f), new Vector3(s, 1f, 0f),
                new int3(0, -1, 0), "-Y face");
            ok &= ExpectFace(world, block + new Vector3(e, 1f, 0.5f), new Vector3(s, -1f, 0f),
                new int3(0, 1, 0), "+Y face");
            ok &= ExpectFace(world, block + new Vector3(e, 0.5f, 0f), new Vector3(s, 0f, 1f),
                new int3(0, 0, -1), "-Z face");
            ok &= ExpectFace(world, block + new Vector3(e, 0.5f, 1f), new Vector3(s, 0f, -1f),
                new int3(0, 0, 1), "+Z face");
            return ok;
        }

        /// <summary>
        /// Marches backwards from a point just inside a target face and asserts the reported entered face.
        /// </summary>
        /// <param name="world">The harness.</param>
        /// <param name="entryPoint">A point just inside the block, on the face under test.</param>
        /// <param name="dir">The direction the ray travels (from outside, through the face).</param>
        /// <param name="expected">The expected outward face normal.</param>
        /// <param name="label">Human-readable face name for the failure message.</param>
        /// <returns>True when the reported normal matches.</returns>
        private static bool ExpectFace(PlacementTestWorld world, Vector3 entryPoint, Vector3 dir, int3 expected,
            string label)
        {
            Vector3 unit = dir.normalized;
            Vector3 origin = entryPoint - unit * FACE_PROBE_BACKOFF;

            if (!world.MarchRay(origin, unit, out Vector3Int _, out int3 normal, out Vector3Int _))
                return Expect(false, $"{label}: ray should have hit the block");

            return Expect(normal.Equals(expected),
                $"{label}: entered face should be {expected}, got {normal}");
        }

        /// <summary>
        /// Pins the structural relationship the placement path relies on: the adjacent cell is the hit cell offset by
        /// the normal, that cell is the one the ray occupied immediately before the hit, and it is not itself
        /// hittable (otherwise the march should have stopped there).
        /// </summary>
        private static bool NormalAndAdjacentCellAgreeWithApproach()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());

            const int bx = 8, by = 8, bz = 8;
            world.SetBlock(bx, by, bz, Id.Ground);

            Vector3 dir = new Vector3(0.6f, -1f, 0.45f).normalized;
            Vector3 origin = new Vector3(bx + 0.5f, by + 1f, bz + 0.5f) - dir * 3f;

            if (!world.MarchRay(origin, dir, out Vector3Int hitCell, out int3 normal, out Vector3Int adjacentCell))
                return Expect(false, "the ray should have hit the seeded block");

            bool ok = Expect(hitCell == new Vector3Int(bx, by, bz), $"should hit the seeded block, got {hitCell}");
            ok &= Expect(adjacentCell == hitCell + new Vector3Int(normal.x, normal.y, normal.z),
                $"adjacent cell {adjacentCell} must be the hit cell offset by the normal {normal}");
            ok &= Expect(math.abs(normal.x) + math.abs(normal.y) + math.abs(normal.z) == 1,
                $"the normal must be a single unit axis, got {normal}");
            // The face must be one the ray could actually enter through: it opposes the direction of travel.
            ok &= Expect(math.dot(new float3(normal.x, normal.y, normal.z), new float3(dir.x, dir.y, dir.z)) < 0f,
                $"the entered face {normal} must oppose the ray direction {dir}");
            return ok;
        }

        /// <summary>
        /// Every degenerate ray input must end the traversal instead of walking forever. Asserted against
        /// <see cref="VoxelRayDDA"/> directly, under an explicit step cap, rather than through
        /// <c>PlacementController.MarchRay</c>: a scenario that hands hostile input to an unbounded production loop
        /// would <b>hang the suite</b> rather than fail it, and a hang is not a test result. The cap is the assertion.
        /// <para>
        /// The cases are not interchangeable. Float bounds are false for NaN in both directions, and the traversal's
        /// axis selection resolves ties toward Z — so a NaN on Z is chosen every iteration while a NaN on X or Y is
        /// never chosen, and a non-finite <c>reach</c> escapes any origin/direction check entirely.
        /// </para>
        /// </summary>
        private static bool DegenerateRayInputTerminates()
        {
            float3 origin = new float3(5.5f, 9.5f, 5.5f);
            float3 direction = new float3(0.3f, -1f, 0.2f);
            const float reach = 8f;

            bool ok = true;
            ok &= ExpectTerminates("NaN origin.x", new float3(float.NaN, 9.5f, 5.5f), direction, reach);
            ok &= ExpectTerminates("NaN origin.z", new float3(5.5f, 9.5f, float.NaN), direction, reach);
            ok &= ExpectTerminates("NaN origin (all axes)", float.NaN, direction, reach);
            ok &= ExpectTerminates("infinite origin.y", new float3(5.5f, float.PositiveInfinity, 5.5f), direction, reach);
            ok &= ExpectTerminates("NaN direction.z", origin, new float3(0.3f, -1f, float.NaN), reach);
            ok &= ExpectTerminates("zero direction", origin, float3.zero, reach);
            ok &= ExpectTerminates("NaN reach", origin, direction, float.NaN);
            ok &= ExpectTerminates("infinite reach", origin, direction, float.PositiveInfinity);

            // The guard must not have bought termination by breaking ordinary traversal.
            VoxelRayDDA sane = VoxelRayDDA.Create(origin, direction, reach);
            int cells = 0;
            while (cells < DEGENERATE_STEP_CAP && sane.MoveNext(out int3 _, out int3 _)) cells++;
            ok &= Expect(cells > 1 && cells < DEGENERATE_STEP_CAP,
                $"a well-formed ray must still traverse normally, visited {cells} cells");
            return ok;
        }

        /// <summary>Asserts one degenerate input terminates within <see cref="DEGENERATE_STEP_CAP"/> steps.</summary>
        /// <param name="label">Case name for the failure message.</param>
        /// <param name="rayOrigin">Ray origin under test.</param>
        /// <param name="rayDir">Ray direction under test.</param>
        /// <param name="reach">Reach under test.</param>
        /// <returns>True when the traversal ended on its own.</returns>
        private static bool ExpectTerminates(string label, float3 rayOrigin, float3 rayDir, float reach)
        {
            VoxelRayDDA dda = VoxelRayDDA.Create(rayOrigin, rayDir, reach);

            int steps = 0;
            while (steps < DEGENERATE_STEP_CAP && dda.MoveNext(out int3 _, out int3 _)) steps++;

            return Expect(steps < DEGENERATE_STEP_CAP,
                $"{label}: traversal did not terminate (hit the {DEGENERATE_STEP_CAP}-step cap)");
        }

        /// <summary>
        /// A ray whose origin is already inside a hittable block crosses no face at all. The march must still report a
        /// single unit normal — <c>Facing6FromHitNormal</c> silently folds a zero normal to North, so an undefined
        /// value here surfaces as a mis-oriented placed block rather than an error.
        /// </summary>
        private static bool RayStartingInsideBlockHasDefinedFace()
        {
            using PlacementTestWorld world = new PlacementTestWorld(TestPlacementBlockPalette.Create());

            const int bx = 8, by = 8, bz = 8;
            world.SetBlock(bx, by, bz, Id.Ground);

            Vector3 dir = new Vector3(0.2f, -1f, 0.15f).normalized;
            Vector3 origin = new Vector3(bx + 0.5f, by + 0.5f, bz + 0.5f);

            if (!world.MarchRay(origin, dir, out Vector3Int hitCell, out int3 normal, out Vector3Int _))
                return Expect(false, "a ray starting inside a hittable block should report that block as the hit");

            bool ok = Expect(hitCell == new Vector3Int(bx, by, bz),
                $"the origin cell {new Vector3Int(bx, by, bz)} should be the hit, got {hitCell}");
            ok &= Expect(math.abs(normal.x) + math.abs(normal.y) + math.abs(normal.z) == 1,
                $"the entered face must be a single unit axis even with no face crossed, got {normal}");
            // With no crossing to read, the defined answer is the face the ray would have entered through had it
            // come from outside: the dominant travel axis, negated.
            ok &= Expect(normal.Equals(new int3(0, 1, 0)),
                $"a predominantly downward ray should report the +Y face, got {normal}");
            return ok;
        }
    }
}
