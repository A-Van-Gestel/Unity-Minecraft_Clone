using System.Collections.Generic;
using Data;
using Editor.Validation.PhysicsSolver.Framework;
using Helpers;
using Physics;
using UnityEngine;
using Id = Editor.Validation.PhysicsSolver.Framework.TestPhysicsBlockPalette.Id;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.PhysicsSolver
{
    /// <summary>
    /// Baseline (must-stay-green) scenarios for the collision solver — the automated form of
    /// <c>SUB_VOXEL_COLLISION_SYSTEM.md</c> §5 Phase 6c's six unchecked regression tests plus its §2.2 failure
    /// table, which the NS-4 roadmap entry names as this suite's ready-made baseline list.
    /// <para>
    /// <b>What each baseline actually discriminates.</b> These baselines were authored against already-shipped code,
    /// so none of them had ever been observed failing — and a baseline never seen red proves nothing. Each engine
    /// mutation below was applied in isolation, run, and reverted; the recorded reds are what was <i>observed</i>,
    /// not predicted:
    /// <list type="bullet">
    /// <item>Force the full-block fast path in <c>BlockCollisionBoundsUtility.GetBounds</c> (lose sub-voxel bounds
    /// entirely) → <b>B6, B10, B11, B12, B13</b>. The full-cube baselines stay green, as they should.</item>
    /// <item>Collapse <c>CheckPhysicsCollision</c>'s Y scan to the AABB's minimum cell → <b>B4, B5</b> — the two
    /// baselines that assert the sweep spans the entity's whole height.</item>
    /// <item>Remove the substep chain from <c>CalculateVelocity</c> → <b>B6</b>.</item>
    /// <item>Remove the step-up pre-pass from <c>ResolveMovement</c> → <b>B8, B9</b>.</item>
    /// <item>Aggregate the <i>first</i> contact instead of the largest correction → <b>B7</b>, and only B7 — with
    /// <i>both</i> of its geometries (half-slab + cube, and two custom volumes) failing independently.</item>
    /// <item>Drop the per-substep <c>transform.position</c> accumulation → <b>B6, B15</b>.</item>
    /// <item>Drop the <see cref="WorldOrigin"/> offset from the scan's voxel lookup → <b>B17</b>, and only B17.</item>
    /// <item>Drop the <c>fluidType != None</c> filter → <b>B14</b>, and only B14.</item>
    /// <item>Halve the reported correction → <b>B2, B3, B4, B5, B6, B7, B10, B12, B13, B15, B16, B17</b>, i.e. every
    /// baseline that pins a contact face is sensitive to the correction magnitude.</item>
    /// <item><i>Fixture</i> mutation — author the half-slab at a quarter height while leaving the scenarios' expected
    /// boundary at a half → <b>B10, B11, B13</b>. This is what proves B11's blocked half is real: with the volume no
    /// longer reaching the entity's feet it walks straight through. The engine mutations above cannot show that,
    /// because they change the correction rather than the volume's extent.</item>
    /// </list>
    /// <b>B1 is the one baseline no mutation reds</b>, by design: it is the fixture-integrity guard, and it is
    /// two-sided (a seeded AABB must hit <i>and</i> an open-air AABB must not), so it cannot pass vacuously itself.
    /// <b>B15 does not detect the absence of substepping</b> (removing it left B15 green — B6 owns that); what B15
    /// discriminates is the substep loop's <i>composition</i> and the landing snap's independence from displacement
    /// size.
    /// </para>
    /// <para>
    /// <b>Deliberately not asserted:</b> <c>IsGrounded</c> after a <i>high-speed</i> landing (B6) or after a
    /// horizontal-only resolve (B3, B11, B16). The grounded verdict in those cases is the subject of
    /// <c>PLAYER_BUGS</c> §04 and is owned by that entry's repro, not pinned here — pinning today's answer would
    /// encode the bug as a baseline.
    /// </para>
    /// </summary>
    public static partial class PhysicsSolverValidationSuite
    {
        /// <summary>Cell Y of the ground layer most scenarios stand on.</summary>
        private const int GROUND_Y = 4;

        /// <summary>Unity-space height of the ground layer's top surface.</summary>
        private const float GROUND_TOP = GROUND_Y + 1f;

        /// <summary>Cell X of the wall / obstacle plane the horizontal scenarios push into.</summary>
        private const int WALL_X = 10;

        /// <summary>Cell Z of the second wall plane, for the inside-corner scenario.</summary>
        private const int WALL_Z = 10;

        /// <summary>
        /// Tolerance for assertions about an exactly-preserved or exactly-zero displacement — loose enough only to
        /// absorb float accumulation across a chain of resolves.
        /// <para>
        /// <b>Invariant: this must stay strictly below the solver's <c>COLLISION_EPSILON</c> (0.001).</b> At or above
        /// it, B15's differential can no longer see a substep chain that applied the stand-off epsilon one extra time
        /// — which is precisely the mis-accumulation defect B15 exists to catch.
        /// </para>
        /// </summary>
        private const float EXACT_TOLERANCE = 1e-4f;

        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B1: fixture reaches the seeded world (origin pinned, non-zero hit)",
                FixtureIntegrity));
            scenarios.Add(new Scenario("B2: landing on full-block ground rests on its top face", LandOnFullBlock));
            scenarios.Add(new Scenario("B3: full-block wall stops X without pushing Z", WallStopsOneAxisOnly));
            scenarios.Add(new Scenario("B4: ceiling stops the head and kills upward momentum", CeilingBump));
            scenarios.Add(new Scenario("B5: obstacle at head height only still blocks (#526)", HeadHeightObstacle));
            scenarios.Add(new Scenario("B6: no tunneling through a quarter slab at extreme fall speed",
                NoTunnelingThroughQuarterSlab));
            scenarios.Add(new Scenario("B7: multi-contact aggregation rests on the taller support",
                MultiContactPicksTallestSupport));
            scenarios.Add(new Scenario("B8: horizontal displacement preserved through a step-up",
                StepUpPreservesHorizontal));
            scenarios.Add(new Scenario("B9: step-up from a half-slab onto a full block finds its top",
                StepUpFindsSupport));
            scenarios.Add(new Scenario("B10: standing on a bottom half-slab rests at half height",
                RestOnBottomHalfSlab));
            scenarios.Add(new Scenario("B11: the empty top half of a bottom slab is walkable",
                WalkThroughEmptySlabTop));
            scenarios.Add(new Scenario("B12: a rotated wall-slab blocks from its occupied side only",
                RotatedWallSlabBlocksOneSide));
            scenarios.Add(new Scenario("B13: two oppositely-rotated slabs fill the space with no seam gap",
                AdjacentRotatedSlabsFillSpace));
            scenarios.Add(new Scenario("B14: fluids never collide", FluidsDoNotCollide));
            scenarios.Add(new Scenario("B15: substep invariance — one resolve, N resolves and the real substep chain agree",
                SubstepInvariance));
            scenarios.Add(new Scenario("B16: an inside corner stops both axes and settles without jitter",
                InsideCornerSettles));
            scenarios.Add(new Scenario("B17: the same landing resolves at a shifted floating origin",
                ShiftedFloatingOrigin));
        }

        /// <summary>Builds a fixture over the controlled solver palette at the identity origin.</summary>
        /// <returns>A fresh fixture the caller owns and must dispose.</returns>
        private static PhysicsTestWorld NewFixture() =>
            new PhysicsTestWorld(TestPhysicsBlockPalette.Create());

        /// <summary>
        /// The vacuous-pass guard, and the reason it is <b>B1</b>: <c>World.CheckPhysicsCollision</c> offsets its
        /// lookup by the <see cref="WorldOrigin"/> static, which survives play sessions, so a fixture that inherited
        /// a stale anchor would query cells far from the seeded blocks — every sweep would return zero hits and
        /// every scenario below would pass without testing anything. This asserts the anchor is pinned, that an AABB
        /// straddling the seeded surface really does report a hit on the expected face, and that an AABB in open air
        /// does <i>not</i> — so neither a blind miss nor a blind hit can hide.
        /// </summary>
        private static bool FixtureIntegrity()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);

            bool ok = Expect(WorldOrigin.IsIdentity,
                $"the fixture must pin the floating origin to the identity, got {WorldOrigin.OriginVoxel}");

            Bounds straddling = PhysicsTestWorld.EntityBoundsAt(new Vector3(8.5f, GROUND_TOP - 0.5f, 8.5f));
            bool hit = world.Probe(straddling, axis: 1, directionSign: -1, out CollisionContact contact);
            ok &= Expect(hit, "an AABB straddling the seeded ground must report a hit — a zero-hit sweep would make " +
                              "every other baseline pass vacuously");
            ok &= ExpectApprox(contact.ContactFace, GROUND_TOP, "ground contact face");

            Bounds inAir = PhysicsTestWorld.EntityBoundsAt(new Vector3(8.5f, GROUND_TOP + 10f, 8.5f));
            ok &= Expect(!world.Probe(inAir, axis: 1, directionSign: -1, out _),
                "an AABB in open air must NOT report a hit — otherwise every hit assertion is trivially true");
            return ok;
        }

        /// <summary>
        /// The full-block happy path (Phase 6c "existing full-block movement is unchanged"): a fall onto flat ground
        /// settles on the block's top face and grounds the entity.
        /// </summary>
        private static bool LandOnFullBlock()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + 1f, 8.5f));
            world.SetGrounded(false);

            world.Step(new Vector3(0f, -1.5f, 0f));

            bool ok = ExpectApprox(world.Position.y, GROUND_TOP, "rest height on full-block ground");
            ok &= Expect(world.Position.y >= GROUND_TOP, $"the entity must never settle below the surface " +
                                                         $"({GROUND_TOP}), got {world.Position.y}");
            ok &= Expect(world.IsGrounded, "the solver must report grounded after landing");
            return ok;
        }

        /// <summary>
        /// A full-block wall stops movement at its face — and, crucially, the blocked X axis must not leak a
        /// correction into the free Z axis. A diagonal push into an X-only wall is the case that observes it: the
        /// step-up pre-pass probes an AABB displaced on <i>both</i> axes and reports Z as blocked, yet the horizontal
        /// resolve restarts from the undisplaced body and finds Z clear.
        /// <para>
        /// <b>Scope of what this pins.</b> The horizontal resolve resets to <c>currentAABB</c> once, then shifts its
        /// sweep AABB <i>cumulatively</i> — Z first, then X against the already-Z-resolved box. So this baseline pins
        /// the Z-clear-then-X ordering only; genuine per-axis independence (X blocked first, then Z) is <b>not</b>
        /// asserted by any scenario here.
        /// </para>
        /// </summary>
        private static bool WallStopsOneAxisOnly()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            FillWallPlaneX(world, WALL_X);
            world.PlaceEntity(new Vector3(WALL_X - 0.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(true);

            Vector3 resolved = world.Step(new Vector3(0.2f, 0f, 0.2f));

            bool ok = ExpectApprox(world.Position.x, WALL_X - PhysicsTestWorld.EntityHalfWidthX,
                "X must stop with the body's face against the wall");
            ok &= ExpectApprox(resolved.z, 0.2f, "Z must pass through untouched (no cross-axis push)",
                EXACT_TOLERANCE);
            return ok;
        }

        /// <summary>
        /// Upward movement resolves against the block's <i>bottom</i> face, and the ceiling hit clears the
        /// accumulated upward momentum — otherwise the entity keeps pressing into the ceiling for several ticks.
        /// </summary>
        private static bool CeilingBump()
        {
            const int CEILING_Y = 7;

            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            world.FillLayer(CEILING_Y, Id.Ground);
            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(true);
            world.SetVerticalMomentum(5f);

            world.Step(new Vector3(0f, 0.5f, 0f));

            bool ok = ExpectApprox(world.Position.y, CEILING_Y - PhysicsTestWorld.EntityHeight,
                "the head must stop at the ceiling's underside");
            ok &= ExpectApprox(world.VerticalMomentum, 0f, "upward momentum after a ceiling hit", EXACT_TOLERANCE);
            return ok;
        }

        /// <summary>
        /// The regression guard for fixed bug #526: an obstacle that overlaps only the entity's <b>upper</b> body
        /// band must still block it. The pre-Phase-6 solver probed a few corner points at fixed heights and could
        /// miss such an obstacle entirely; the AABB sweep spans the full collider height. The scenario asserts the
        /// feet band really is clear first, so it cannot pass for the wrong reason.
        /// </summary>
        private static bool HeadHeightObstacle()
        {
            const int OBSTACLE_Y = 6; // spans 6..7 — above the feet, inside the 1.8 m tall body at GROUND_TOP

            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                world.SetBlock(WALL_X, OBSTACLE_Y, z, Id.Ground);

            world.PlaceEntity(new Vector3(WALL_X - 0.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(true);

            // Fixture check: a knee-high box at the destination must find nothing, so the block below is genuinely
            // absent and the stop below can only come from the head-height obstacle.
            Bounds feetBand = new Bounds();
            feetBand.SetMinMax(
                new Vector3(WALL_X - 0.9f, GROUND_TOP + 0.01f, 8.1f),
                new Vector3(WALL_X + 0.2f, GROUND_TOP + 0.5f, 8.9f));
            bool ok = Expect(!world.Probe(feetBand, axis: 0, directionSign: 1, out _),
                "fixture: the feet band must be clear — this scenario only means something if the obstacle is " +
                "exclusively at head height");

            world.Step(new Vector3(0.2f, 0f, 0f));

            ok &= ExpectApprox(world.Position.x, WALL_X - PhysicsTestWorld.EntityHalfWidthX,
                "an obstacle at head height only must still stop horizontal movement");
            return ok;
        }

        /// <summary>
        /// Phase 6c's tunneling guard, at the speed where it actually bites. A displacement smaller than the
        /// entity's own height cannot tunnel a thin slab: the 1.8 m tall AABB still straddles the slab at its
        /// destination, so the non-swept overlap test finds it anyway. Only once one tick's displacement exceeds the
        /// collider height does the destination AABB clear the slab entirely — reachable in game because
        /// <c>flyingSpeed</c> is unbounded (<c>IncrementFlyingSpeed</c>). With the substep chain the entity lands on
        /// the quarter slab's authored top; without it, it passes straight through.
        /// </summary>
        private static bool NoTunnelingThroughQuarterSlab()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.QuarterSlab);
            const float slabTop = GROUND_Y + TestPhysicsBlockPalette.QuarterSlabHeight;

            world.PlaceEntity(new Vector3(8.5f, slabTop + 0.05f, 8.5f));
            world.SetGrounded(false);
            // One tick's displacement = collider height + margin, so an unsubstepped resolve would land the whole
            // body below the slab. Derived from the project's fixed timestep rather than assuming 50 Hz.
            world.SetVerticalMomentum(-(PhysicsTestWorld.EntityHeight + 0.5f) / PhysicsTestWorld.FixedDeltaTime);

            world.Tick();

            bool ok = Expect(world.Position.y >= slabTop - PositionTolerance,
                $"tunneled through the quarter slab: expected to rest at or above {slabTop}, got {world.Position.y}");
            ok &= ExpectApprox(world.Position.y, slabTop, "rest height on a quarter slab after a high-speed fall");
            return ok;
        }

        /// <summary>
        /// Direction-specific multi-contact aggregation (Phase 6c): with the footprint spanning two supports of
        /// different heights, the downward resolve must pick the contact producing the largest correction — the entity
        /// rests on the <i>taller</i> one. Picking the shallowest contact instead would leave it embedded in the
        /// taller support.
        /// <para>
        /// Run over two geometries. The first mixes a sub-voxel volume with a full cube; the second uses <b>two
        /// custom volumes</b> — a bottom half-slab beside a rotated top half-slab — which is the case where both
        /// contacts come out of the rotation path, and the one a compound-bounds change (<c>VQ-4</c>) would most
        /// plausibly regress.
        /// </para>
        /// </summary>
        private static bool MultiContactPicksTallestSupport()
        {
            // x = 9.0 puts the footprint across cells 8 and 9; z stays inside cell 8.
            Vector3 start = new Vector3(9f, GROUND_TOP + 1f, 8.5f);

            bool ok;
            using (PhysicsTestWorld world = NewFixture())
            {
                world.SetBlock(8, GROUND_Y, 8, Id.HalfSlab); // top at GROUND_Y + 0.5
                world.SetBlock(9, GROUND_Y, 8, Id.Ground); // top at GROUND_TOP
                world.PlaceEntity(start);
                world.SetGrounded(false);

                world.Step(new Vector3(0f, -1.6f, 0f));

                // Both geometries always run — short-circuiting here would hide whether the second one discriminates.
                ok = ExpectApprox(world.Position.y, GROUND_TOP,
                    "must rest on the taller of the two supports (the full block), not on the half slab");
            }

            // Two sub-voxel volumes at different heights: bottom half (GROUND_Y..+0.5) vs top half (+0.5..GROUND_TOP).
            BlockType[] palette = TestPhysicsBlockPalette.Create();
            if (!TestPhysicsBlockPalette.TryFindMeta(palette[Id.HalfSlab], OccupiesUpperHalf, out byte topMeta))
                return Expect(false, "fixture: no Facing6Roll2 metadata rotates the slab into the cell's upper half")
                       && ok;

            using PhysicsTestWorld slabs = new PhysicsTestWorld(palette);
            slabs.SetBlock(8, GROUND_Y, 8, Id.HalfSlab); // top at GROUND_Y + 0.5
            slabs.SetBlock(9, GROUND_Y, 8, Id.HalfSlab, topMeta); // top at GROUND_TOP
            slabs.PlaceEntity(start);
            slabs.SetGrounded(false);

            slabs.Step(new Vector3(0f, -1.6f, 0f));

            ok &= ExpectApprox(slabs.Position.y, GROUND_TOP,
                "with two custom volumes, must rest on the taller (the rotated top slab), not on the bottom slab");
            return ok;
        }

        /// <summary>
        /// Phase 6c's "horizontal velocity preserved after a successful step-up": the step-up pre-pass probes the
        /// <i>original</i> desired position, so on success no horizontal correction is applied at all. The scenario
        /// also asserts the step actually fired, so a solver that silently stopped stepping cannot pass it by
        /// leaving the displacement untouched for the wrong reason.
        /// </summary>
        private static bool StepUpPreservesHorizontal()
        {
            using PhysicsTestWorld world = BuildStepUpFixture();

            Vector3 resolved = world.Step(new Vector3(0.2f, 0f, 0f));

            bool ok = Expect(resolved.y > 0.1f,
                $"fixture: the step-up must actually have fired (expected a vertical lift, got {resolved.y})");
            ok &= ExpectApprox(resolved.x, 0.2f, "horizontal displacement through a successful step-up",
                EXACT_TOLERANCE);
            return ok;
        }

        /// <summary>
        /// Phase 6c's "step-up from a half-slab to a full block correctly finds support": the downward sweep after
        /// the lift must land the entity on the target block's top face (not at the lifted height, and not back on
        /// the half-slab it came from).
        /// </summary>
        private static bool StepUpFindsSupport()
        {
            using PhysicsTestWorld world = BuildStepUpFixture();

            world.Step(new Vector3(0.2f, 0f, 0f));

            bool ok = ExpectApprox(world.Position.y, GROUND_Y + 2f,
                "the step-up's downward sweep must land the entity on the full block's top face");
            ok &= Expect(world.IsGrounded, "a step-up onto support must leave the entity grounded");
            return ok;
        }

        /// <summary>
        /// §2.2 row 1: the entity stands on the authored surface of a bottom half-slab, not on the cell's top.
        /// </summary>
        private static bool RestOnBottomHalfSlab()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.HalfSlab);
            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + 1f, 8.5f));
            world.SetGrounded(false);

            world.Step(new Vector3(0f, -1.6f, 0f));

            bool ok = ExpectApprox(world.Position.y, GROUND_Y + TestPhysicsBlockPalette.HalfSlabHeight,
                "rest height on a bottom half-slab");
            ok &= Expect(world.IsGrounded, "the solver must report grounded after landing on a slab");
            return ok;
        }

        /// <summary>
        /// §2.2 row 2: the empty upper half of a bottom slab is free space. The discriminating geometry is an entity
        /// whose feet are level with the slab's top — inside the cell but above its volume. A cell-level (full-cube)
        /// test blocks it; the sub-voxel test lets it pass. Grounded state is off so the step-up pre-pass cannot
        /// mask the horizontal decision by lifting the entity over the obstruction.
        /// <para>
        /// Asserted as a <b>pair</b> over identical geometry: free with the feet at the authored top, blocked with the
        /// feet just below it. The free case alone would only show that the entity passes through <i>somewhere</i> —
        /// the contrast is what pins the free/blocked boundary to the authored height. (Verified by authoring the slab
        /// at a quarter instead of a half, which flips both halves.)
        /// </para>
        /// </summary>
        private static bool WalkThroughEmptySlabTop()
        {
            using PhysicsTestWorld world = NewFixture();
            const float slabTop = GROUND_Y + TestPhysicsBlockPalette.HalfSlabHeight;
            // Only the target cell is seeded, so the two halves below differ in exactly one variable: the feet
            // height. Seeding a slab under the entity too would put the lower half's body INSIDE its own footing,
            // whose far X face then dominates the aggregation and ejects the entity a full block backwards — a real
            // solver behavior (see PLAYER_BUGS §01/§04), but not what this baseline is about.
            world.SetBlock(WALL_X, GROUND_Y, 8, Id.HalfSlab);

            // Feet level with the authored top: the body is inside the cell but above its volume — free.
            world.PlaceEntity(new Vector3(9.5f, slabTop, 8.5f));
            world.SetGrounded(false);

            Vector3 above = world.Step(new Vector3(0.2f, 0f, 0f));

            bool ok = ExpectApprox(above.x, 0.2f,
                "the empty upper half of a bottom slab must not block horizontal movement", EXACT_TOLERANCE);

            // Same blocks, same push, feet just inside the volume — must now stop at the volume's face.
            world.PlaceEntity(new Vector3(9.5f, slabTop - 0.1f, 8.5f));
            world.SetGrounded(false);

            world.Step(new Vector3(0.2f, 0f, 0f));

            ok &= ExpectApprox(world.Position.x, WALL_X - PhysicsTestWorld.EntityHalfWidthX,
                "with the feet inside the slab's volume the same slab must block — without this half, the free case " +
                "above says nothing about where the boundary sits");
            return ok;
        }

        /// <summary>
        /// §2.2 row 3: a slab rotated into a wall occupies half its cell, so it blocks from the occupied side and
        /// not from the empty one. The orientation is discovered through the production bounds resolver rather than
        /// assuming a <see cref="MetadataSchema.Facing6Roll2"/> encoding.
        /// </summary>
        private static bool RotatedWallSlabBlocksOneSide()
        {
            BlockType[] palette = TestPhysicsBlockPalette.Create();
            if (!TestPhysicsBlockPalette.TryFindMeta(palette[Id.HalfSlab], OccupiesEastHalf, out byte eastMeta))
                return Expect(false, "fixture: no Facing6Roll2 metadata rotates the slab into the cell's +X half");

            using PhysicsTestWorld world = new PhysicsTestWorld(palette);
            world.FillLayer(GROUND_Y, Id.Ground);
            world.SetBlock(WALL_X, GROUND_Y + 1, 8, Id.HalfSlab, eastMeta);

            // From the west, the near half of the cell is empty — the entity walks into it unobstructed.
            world.PlaceEntity(new Vector3(WALL_X - 0.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(false);
            Vector3 fromWest = world.Step(new Vector3(0.2f, 0f, 0f));
            bool ok = ExpectApprox(fromWest.x, 0.2f, "the slab's empty -X half must not block", EXACT_TOLERANCE);

            // From the east, the same block's volume is in the way.
            world.PlaceEntity(new Vector3(WALL_X + 1.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(false);
            world.Step(new Vector3(-0.2f, 0f, 0f));
            ok &= ExpectApprox(world.Position.x, WALL_X + 1f + PhysicsTestWorld.EntityHalfWidthX,
                "the slab's solid +X half must stop the entity at its outer face");
            return ok;
        }

        /// <summary>
        /// §2.2 row 5: two adjacent, oppositely-rotated slabs together fill the space between their cell centers —
        /// there is no gap at the seam, and neither is over-sized. Asserted from both approach directions plus a
        /// direct probe straddling the seam.
        /// </summary>
        private static bool AdjacentRotatedSlabsFillSpace()
        {
            BlockType[] palette = TestPhysicsBlockPalette.Create();
            if (!TestPhysicsBlockPalette.TryFindMeta(palette[Id.HalfSlab], OccupiesEastHalf, out byte eastMeta))
                return Expect(false, "fixture: no Facing6Roll2 metadata rotates the slab into the cell's +X half");
            if (!TestPhysicsBlockPalette.TryFindMeta(palette[Id.HalfSlab], OccupiesWestHalf, out byte westMeta))
                return Expect(false, "fixture: no Facing6Roll2 metadata rotates the slab into the cell's -X half");

            using PhysicsTestWorld world = new PhysicsTestWorld(palette);
            world.FillLayer(GROUND_Y, Id.Ground);
            world.SetBlock(WALL_X, GROUND_Y + 1, 8, Id.HalfSlab, eastMeta); // occupies WALL_X + 0.5 .. + 1
            world.SetBlock(WALL_X + 1, GROUND_Y + 1, 8, Id.HalfSlab, westMeta); // occupies WALL_X + 1 .. + 1.5

            const float westFace = WALL_X + 0.5f;
            const float eastFace = WALL_X + 1.5f;

            // The seam itself must be solid — the two volumes meet at WALL_X + 1 with no gap.
            Bounds seam = new Bounds();
            seam.SetMinMax(
                new Vector3(WALL_X + 0.9f, GROUND_TOP + 0.2f, 8.2f),
                new Vector3(WALL_X + 1.1f, GROUND_TOP + 0.4f, 8.8f));
            bool ok = Expect(world.Probe(seam, axis: 0, directionSign: 1, out _),
                "the seam between the two rotated slabs must be solid — a gap there is the pre-Phase-6 failure");

            world.PlaceEntity(new Vector3(WALL_X - 0.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(false);
            world.Step(new Vector3(1f, 0f, 0f));
            ok &= ExpectApprox(world.Position.x, westFace - PhysicsTestWorld.EntityHalfWidthX,
                "from the west, the entity must stop at the near volume's face, not at the cell boundary");

            world.PlaceEntity(new Vector3(WALL_X + 2.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(false);
            world.Step(new Vector3(-1f, 0f, 0f));
            ok &= ExpectApprox(world.Position.x, eastFace + PhysicsTestWorld.EntityHalfWidthX,
                "from the east, the entity must stop at the far volume's face, not at the cell boundary");
            return ok;
        }

        /// <summary>
        /// Fluids are occupied cells that must never produce a contact — a fluid that collided would ground the
        /// entity mid-air and wall it in. Asserted on both a vertical and a horizontal resolve.
        /// <para>
        /// The column mixes an ordinary non-solid fluid with the fixture's <see cref="Id.SolidFlaggedFluid"/>, so
        /// <i>both</i> of the solver's filter clauses are covered: the shipping fluids are already non-solid, which
        /// leaves the <c>fluidType != None</c> clause unexercised by any real block.
        /// </para>
        /// </summary>
        private static bool FluidsDoNotCollide()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            world.SetBlock(8, 5, 8, Id.SolidFlaggedFluid);
            world.SetBlock(8, 6, 8, Id.Fluid);
            world.SetBlock(9, 5, 8, Id.SolidFlaggedFluid);

            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + 0.5f, 8.5f));
            world.SetGrounded(false);

            Vector3 down = world.Step(new Vector3(0f, -0.4f, 0f));
            bool ok = ExpectApprox(down.y, -0.4f, "a fluid must not correct vertical movement", EXACT_TOLERANCE);
            ok &= Expect(!world.IsGrounded, "a fluid must not ground the entity");

            Vector3 across = world.Step(new Vector3(0.2f, 0f, 0f));
            ok &= ExpectApprox(across.x, 0.2f, "a fluid must not block horizontal movement", EXACT_TOLERANCE);
            return ok;
        }

        /// <summary>
        /// The substep-consistency property from NS-4's scope sketch, three ways: one resolve of the whole
        /// displacement, four resolves of a quarter each, and the <b>real</b> substep chain in
        /// <c>CalculateVelocity</c> driving the same total displacement. All three must land on the same surface.
        /// <para>
        /// This is the suite's one genuine differential — it needs no hand-computed expectation, and it fails if the
        /// landing snap is ever made displacement-dependent (e.g. derived from the pre-step cell rather than from
        /// the contact face) or if the substep loop mis-accumulates position between iterations.
        /// </para>
        /// </summary>
        private static bool SubstepInvariance()
        {
            const float START_Y = GROUND_TOP + 0.3f;
            const float TOTAL_DROP = -0.4f;
            const float TOTAL_SLIDE = 0.06f;
            const int LEGS = 4;

            float single, quartered, chained;
            float singleX, quarteredX;

            using (PhysicsTestWorld world = NewFixture())
            {
                world.FillLayer(GROUND_Y, Id.Ground);
                world.PlaceEntity(new Vector3(9f, START_Y, 9f));
                world.SetGrounded(false);
                world.Step(new Vector3(TOTAL_SLIDE, TOTAL_DROP, 0f));
                single = world.Position.y;
                singleX = world.Position.x;
            }

            using (PhysicsTestWorld world = NewFixture())
            {
                world.FillLayer(GROUND_Y, Id.Ground);
                world.PlaceEntity(new Vector3(9f, START_Y, 9f));
                world.SetGrounded(false);
                for (int i = 0; i < LEGS; i++)
                    world.Step(new Vector3(TOTAL_SLIDE / LEGS, TOTAL_DROP / LEGS, 0f));
                quartered = world.Position.y;
                quarteredX = world.Position.x;
            }

            using (PhysicsTestWorld world = NewFixture())
            {
                world.FillLayer(GROUND_Y, Id.Ground);
                world.PlaceEntity(new Vector3(9f, START_Y, 9f));
                world.SetGrounded(false);
                // A momentum past `gravity` is not accelerated further, so this tick's displacement is exactly
                // TOTAL_DROP — which exceeds the tunneling threshold and therefore runs the substep chain.
                world.SetVerticalMomentum(TOTAL_DROP / PhysicsTestWorld.FixedDeltaTime);
                world.Tick();
                chained = world.Position.y;
            }

            bool ok = ExpectApprox(quartered, single, "quartered resolves must land where one resolve does",
                EXACT_TOLERANCE);
            ok &= ExpectApprox(chained, single, "the real substep chain must land where one resolve does",
                EXACT_TOLERANCE);
            ok &= ExpectApprox(quarteredX, singleX, "horizontal travel must be the same either way",
                EXACT_TOLERANCE);
            ok &= ExpectApprox(single, GROUND_TOP, "and all three must be resting on the ground");
            return ok;
        }

        /// <summary>
        /// The corner case behind the <c>COLLISION_EPSILON</c> / jitter-tolerance edges: pushed diagonally into an
        /// inside corner, the entity stops against both faces, and pushing again produces <b>no</b> further
        /// movement — neither an overshoot into the wall nor a jitter back out of it.
        /// </summary>
        private static bool InsideCornerSettles()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            FillWallPlaneX(world, WALL_X);
            FillWallPlaneZ(world, WALL_Z);
            world.PlaceEntity(new Vector3(WALL_X - 0.5f, GROUND_TOP, WALL_Z - 0.5f));
            world.SetGrounded(true);

            world.Step(new Vector3(0.2f, 0f, 0.2f));
            bool ok = ExpectApprox(world.Position.x, WALL_X - PhysicsTestWorld.EntityHalfWidthX,
                "X must stop against the corner's X face");
            ok &= ExpectApprox(world.Position.z, WALL_Z - PhysicsTestWorld.EntityHalfDepthZ,
                "Z must stop against the corner's Z face");

            Vector3 settled = world.Position;
            Vector3 again = world.Step(new Vector3(0.2f, 0f, 0.2f));
            ok &= ExpectApprox(again.x, 0f, "a second push into the corner must not move X", EXACT_TOLERANCE);
            ok &= ExpectApprox(again.z, 0f, "a second push into the corner must not move Z", EXACT_TOLERANCE);
            ok &= Expect((world.Position - settled).magnitude <= EXACT_TOLERANCE,
                $"the entity must stay settled in the corner, moved from {settled} to {world.Position}");
            return ok;
        }

        /// <summary>
        /// WS-4: the solver's grid scan works in Unity space and offsets only the voxel <i>lookup</i> by
        /// <see cref="WorldOrigin"/>. Repeating B2's landing at a far-out anchor must give an identical result;
        /// dropping the offset would query an unloaded chunk, find nothing, and let the entity fall through. The
        /// scenario also asserts the fixture restores the previous anchor on dispose — a leaked origin would offset
        /// every suite that runs after it in <c>Validate All</c>.
        /// </summary>
        private static bool ShiftedFloatingOrigin()
        {
            ChunkCoord before = WorldOrigin.OriginChunk;
            ChunkCoord farAnchor = new ChunkCoord(100, -100);
            bool ok;

            PhysicsTestWorld world = new PhysicsTestWorld(TestPhysicsBlockPalette.Create(), farAnchor);
            try
            {
                ok = Expect(!WorldOrigin.IsIdentity,
                    "fixture: the anchor must actually be shifted for this scenario to mean anything");

                world.FillLayer(GROUND_Y, Id.Ground);
                world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + 1f, 8.5f));
                world.SetGrounded(false);
                world.Step(new Vector3(0f, -1.5f, 0f));

                ok &= ExpectApprox(world.Position.y, GROUND_TOP,
                    "the same landing must resolve identically at a shifted floating origin");
                ok &= Expect(world.IsGrounded, "the solver must report grounded after landing at a shifted origin");
            }
            finally
            {
                world.Dispose();
            }

            ok &= Expect(WorldOrigin.OriginChunk.X == before.X && WorldOrigin.OriginChunk.Z == before.Z,
                $"the fixture must restore the previous anchor on dispose, got {WorldOrigin.OriginChunk}");
            return ok;
        }

        #region Shared fixture geometry

        /// <summary>
        /// Builds the step-up geometry §2.2 row 4 describes: the entity stands on a bottom half-slab and faces a
        /// full block one cell along +X, a 0.5 m rise — exactly <c>stepHeight</c>.
        /// </summary>
        /// <returns>A fixture with the entity placed and grounded, ready for a +X push.</returns>
        private static PhysicsTestWorld BuildStepUpFixture()
        {
            PhysicsTestWorld world = NewFixture();
            try
            {
                world.FillLayer(GROUND_Y, Id.Ground);
                world.SetBlock(WALL_X - 1, GROUND_Y + 1, 8, Id.HalfSlab); // the footing: top at GROUND_Y + 1.5
                for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                    world.SetBlock(WALL_X, GROUND_Y + 1, z, Id.Ground); // the step: top at GROUND_Y + 2

                world.PlaceEntity(new Vector3(WALL_X - 0.5f,
                    GROUND_Y + 1f + TestPhysicsBlockPalette.HalfSlabHeight, 8.5f));
                world.SetGrounded(true);
                return world;
            }
            catch
            {
                world.Dispose();
                throw;
            }
        }

        /// <summary>Fills a two-cell-tall wall plane perpendicular to X, standing on the ground layer.</summary>
        /// <param name="world">The fixture to seed.</param>
        /// <param name="x">Cell X of the plane.</param>
        private static void FillWallPlaneX(PhysicsTestWorld world, int x)
        {
            for (int y = GROUND_Y + 1; y <= GROUND_Y + 2; y++)
            for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                world.SetBlock(x, y, z, Id.Ground);
        }

        /// <summary>Fills a two-cell-tall wall plane perpendicular to Z, standing on the ground layer.</summary>
        /// <param name="world">The fixture to seed.</param>
        /// <param name="z">Cell Z of the plane.</param>
        private static void FillWallPlaneZ(PhysicsTestWorld world, int z)
        {
            for (int y = GROUND_Y + 1; y <= GROUND_Y + 2; y++)
            for (int x = 0; x < ChunkMath.CHUNK_WIDTH; x++)
                world.SetBlock(x, y, z, Id.Ground);
        }

        /// <summary>True when a rotated volume fills the cell's +X half and spans it fully on Y and Z.</summary>
        /// <param name="bounds">Cell-local bounds from the production resolver.</param>
        /// <returns>True for an east-facing wall slab.</returns>
        private static bool OccupiesEastHalf(Bounds bounds) =>
            bounds.min.x > 0.49f && bounds.max.x > 0.99f && bounds.min.y < 0.01f && bounds.max.y > 0.99f;

        /// <summary>True when a rotated volume fills the cell's upper half — a top slab.</summary>
        /// <param name="bounds">Cell-local bounds from the production resolver.</param>
        /// <returns>True for a flipped (top) slab.</returns>
        private static bool OccupiesUpperHalf(Bounds bounds) =>
            bounds.min.y > 0.49f && bounds.max.y > 0.99f;

        /// <summary>True when a rotated volume fills the cell's -X half and spans it fully on Y and Z.</summary>
        /// <param name="bounds">Cell-local bounds from the production resolver.</param>
        /// <returns>True for a west-facing wall slab.</returns>
        private static bool OccupiesWestHalf(Bounds bounds) =>
            bounds.min.x < 0.01f && bounds.max.x < 0.51f && bounds.min.y < 0.01f && bounds.max.y > 0.99f;

        #endregion
    }
}
