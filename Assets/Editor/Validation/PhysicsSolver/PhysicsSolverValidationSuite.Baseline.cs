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
    /// <item>Aggregate the <i>first</i> contact instead of the largest correction → <b>B7, B24</b>. B7 reds with
    /// <i>both</i> of its geometries (half-slab + cube, and two custom volumes) failing independently; B24 reds on
    /// its slab-first ordering only, since the scan reaches cells in ascending Z and the cube-first ordering hands
    /// the mutation the right answer by accident — which is why B24 runs both.</item>
    /// <item>Drop the per-substep position accumulation — before <c>PH-2</c> the staged
    /// <c>transform.position += currentSubMove</c>, since <c>PH-2</c> the local <c>runningPos += currentSubMove</c>
    /// in <c>CalculateVelocity</c> → <b>B6, B15, B19, B20</b>. The first recording of this mutation named only B6
    /// and B15; re-running it against the <c>PH-2</c> mechanism (2026-08-04) reddened <b>four</b> baselines, so the
    /// wider set is what was observed, not a prediction. <b>B19 is the interesting one</b>: its resolved
    /// displacement is <i>identical</i> either way (1.20 on X) and only the grounded verdict diverges — every
    /// substep re-resolving from the same start never leaves the support, so the body reports grounded when it
    /// should be airborne. A differential that compared displacement alone would not have seen it.</item>
    /// <item>Drop the <see cref="WorldOrigin"/> offset from the scan's voxel lookup → <b>B17</b>, and only B17.</item>
    /// <item>Drop the <c>fluidType != None</c> filter → <b>B14</b>, and only B14.</item>
    /// <item>Halve the reported correction → <b>B2, B3, B4, B5, B6, B7, B10, B12, B13, B15, B16, B17</b>, i.e. every
    /// baseline that pins a contact face is sensitive to the correction magnitude.</item>
    /// <item><i>Fixture</i> mutation — author the half-slab at a quarter height while leaving the scenarios' expected
    /// boundary at a half → <b>B10, B11, B13</b>. This is what proves B11's blocked half is real: with the volume no
    /// longer reaching the entity's feet it walks straight through. The engine mutations above cannot show that,
    /// because they change the correction rather than the volume's extent.</item>
    /// <item>Inflate <c>GROUND_PROBE_SKIN</c> from 0.002 to 0.2 → <b>B18</b>, and only B18.</item>
    /// <item>Latch <c>IsGrounded</c> across the tick (<c>IsGrounded |= groundedByStep</c>) instead of reassigning it —
    /// the fix shape <c>PLAYER_BUGS</c> §04 deliberately rejected → <b>B18, B19</b>.</item>
    /// <item><b>B20–B23 need no mutation</b>: they shipped as §04's known-bug reproductions and were observed red
    /// against the unfixed solver, which is the strongest form of this evidence. Promoted August 2026 after the fix
    /// was confirmed in game.</item>
    /// <item>Shrink <c>PH-1</c>'s gather envelope in <c>VoxelRigidbody.GatherCells</c> to the un-lifted body (drop
    /// the <c>stepHeight</c> head-room) → <b>B25</b>, and only B25 — 3 of its 4 sweeps fell back to a direct scan.
    /// <b>B8/B9 stayed green</b> despite being the step-up baselines, and that is the point: see B25's docstring
    /// for why their geometry cannot observe it.</item>
    /// <item>Restore <c>PH-2</c>'s staged <c>transform.position</c> writes in <c>CalculateVelocity</c> (the
    /// per-substep <c>+=</c> plus the trailing revert) → <b>B26</b>, and <i>only</i> B26 — 25 of 26 stayed green.
    /// That is the whole reason it exists: re-staging is <b>behavior-neutral</b>, so no assertion about position,
    /// displacement or grounded state can see it. A shadow-compare running alongside confirmed it directly —
    /// 7 comparisons, 0 mismatches, while B26 was red.</item>
    /// </list>
    /// <b>B1 is the one baseline no mutation reds</b>, by design: it is the fixture-integrity guard, and it is
    /// two-sided (a seeded AABB must hit <i>and</i> an open-air AABB must not), so it cannot pass vacuously itself.
    /// <b>B15 does not detect the absence of substepping</b> (removing it left B15 green — B6 owns that); what B15
    /// discriminates is the substep loop's <i>composition</i> and the landing snap's independence from displacement
    /// size.
    /// </para>
    /// <para>
    /// <b>Horizontal multi-cell aggregation — the coverage gap B24 closed (2026-08-04).</b> Before it, the
    /// first-contact-wins mutation reddened only <b>B7</b>, a <i>vertical</i> support case, and no baseline put two
    /// cells with <i>different blocking faces</i> on one horizontal axis: B3 is a uniform wall plane, B12/B13 vary
    /// rotation rather than depth, B16 spans two axes at equal faces. <b>PH-1</b>'s gather-once refactor could
    /// therefore have re-ordered horizontal contacts while the suite stayed green. What <b>B24</b> was observed to
    /// do under that mutation: its <i>slab-first</i> ordering stopped the body at the farther slab face — 10.10
    /// instead of 9.60, off by 0.50 — while its slab-only fixture check and its <i>cube-first</i> ordering stayed
    /// green, because the scan reaches cells in ascending Z and that ordering hands the mutation the right answer by
    /// accident. Both orderings run for exactly that reason.
    /// </para>
    /// <para>
    /// <b>Where the grounded verdict is pinned:</b> <b>B18–B23</b> own it, and the geometry baselines (B3, B6, B11,
    /// B16) deliberately stay silent about it — duplicating a state assertion across them would only make several
    /// baselines fail for one reason.
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
            scenarios.Add(new Scenario("B18: a body hovering above a surface is not grounded", HoveringIsNotGrounded));
            scenarios.Add(new Scenario("B19: leaving support mid-tick ends not grounded", LeavingSupportEndsAirborne));
            scenarios.Add(new Scenario("B20: a substepped high-speed landing ends grounded",
                FastLandingEndsGrounded));
            scenarios.Add(new Scenario("B21: grounded state survives the ticks after a high-speed landing",
                GroundedSurvivesAfterFastLanding));
            scenarios.Add(new Scenario("B22: a jump is accepted after a high-speed landing",
                JumpAcceptedAfterFastLanding));
            scenarios.Add(new Scenario("B23: a horizontal-only resolve on flat ground stays grounded",
                HorizontalResolveStaysGrounded));
            scenarios.Add(new Scenario("B24: horizontal multi-cell aggregation stops at the nearer blocking face",
                HorizontalAggregationPicksNearerFace));
            scenarios.Add(new Scenario("B25: the gather covers the step-height envelope (step-up from flat ground)",
                StepUpFromFlatGroundGathersLiftedCells));
            scenarios.Add(new Scenario("B26: CalculateVelocity resolves the substep chain without touching the transform",
                CalculateVelocityLeavesTransformUntouched));
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
        /// <para>
        /// Also carries half of <b>PH-1</b>'s envelope guard (see <see cref="ExpectGatherCovered"/>): the step-up
        /// sweeps read <i>lifted</i> boxes, so this is one of the two scenarios where an envelope sized to the
        /// un-lifted body would show up.
        /// </para>
        /// </summary>
        private static bool StepUpPreservesHorizontal()
        {
            using PhysicsTestWorld world = BuildStepUpFixture();

            PhysicsQueryStats.Reset();
            Vector3 resolved = world.Step(new Vector3(0.2f, 0f, 0f));

            bool ok = Expect(resolved.y > 0.1f,
                $"fixture: the step-up must actually have fired (expected a vertical lift, got {resolved.y})");
            ok &= ExpectApprox(resolved.x, 0.2f, "horizontal displacement through a successful step-up",
                EXACT_TOLERANCE);
            ok &= ExpectGatherCovered("step-up (horizontal preservation)");
            return ok;
        }

        /// <summary>
        /// Phase 6c's "step-up from a half-slab to a full block correctly finds support": the downward sweep after
        /// the lift must land the entity on the target block's top face (not at the lifted height, and not back on
        /// the half-slab it came from).
        /// <para>
        /// Carries the other half of <b>PH-1</b>'s envelope guard (see <see cref="ExpectGatherCovered"/>). This is
        /// the sharper of the two: the downward support sweep expands by <c>stepHeight</c> and shifts <i>down</i>,
        /// so it reads the widest box of the whole resolve.
        /// </para>
        /// </summary>
        private static bool StepUpFindsSupport()
        {
            using PhysicsTestWorld world = BuildStepUpFixture();

            PhysicsQueryStats.Reset();
            world.Step(new Vector3(0.2f, 0f, 0f));

            bool ok = ExpectApprox(world.Position.y, GROUND_Y + 2f,
                "the step-up's downward sweep must land the entity on the full block's top face");
            ok &= Expect(world.IsGrounded, "a step-up onto support must leave the entity grounded");
            ok &= ExpectGatherCovered("step-up (support sweep)");
            return ok;
        }

        /// <summary>
        /// Asserts the resolve just measured was answered <b>entirely</b> from <c>PH-1</c>'s gathered cells, and
        /// that the gather actually reached the step-height head-room the step-up sweeps read.
        /// <para>
        /// <b>Why this is not redundant with the geometry assertions beside it.</b> A sweep that escapes the
        /// gathered envelope falls back to a direct world scan, which produces the <i>correct</i> contact — so an
        /// under-sized envelope leaves every geometric assertion in this suite green while silently reverting the
        /// item's entire benefit.
        /// </para>
        /// <para>
        /// <b>Which scenario actually discriminates it</b> (measured 2026-08-04): containment is
        /// <i>cell-granular</i>, so an envelope shortfall is only observable when it crosses a cell boundary — and
        /// when it does not, it also costs nothing, so this is the exactly-right signal rather than a proxy. At
        /// <see cref="BuildStepUpFixture"/>'s geometry the body stands at 5.5, its top face is 7.3 and the lifted
        /// box reaches 7.8 — both cell 7 — so shrinking the envelope by the whole <c>stepHeight</c> changes no
        /// gathered cell and B8/B9 stay green. <see cref="StepUpFromFlatGroundGathersLiftedCells"/> exists for that
        /// reason: from flat ground the same lift spans cell 6 to cell 7 and the shortfall becomes visible.
        /// </para>
        /// <para>
        /// Two-sided on purpose: a zero-fallback count is trivially satisfied when nothing ran at all, so the
        /// gather count is asserted first.
        /// </para>
        /// </summary>
        /// <param name="what">Which resolve is being checked (logged on failure).</param>
        /// <returns>True when at least one gather ran and no sweep fell back.</returns>
        private static bool ExpectGatherCovered(string what)
        {
            bool ok = Expect(PhysicsQueryStats.Gathers > 0,
                $"{what}: no gather ran at all, so the zero-fallback assertion would pass vacuously");
            ok &= Expect(PhysicsQueryStats.Fallbacks == 0,
                $"{what}: {PhysicsQueryStats.Fallbacks} of {PhysicsQueryStats.SweepQueries} sweeps escaped the " +
                "gathered envelope and fell back to a direct scan. The envelope must cover every box this " +
                "resolve's sweeps read — the fallback keeps the RESULT correct, which is precisely why the " +
                "geometry assertions beside this one cannot see the regression");
            return ok;
        }

        /// <summary>
        /// <b>PH-1's envelope guard.</b> Walking from flat ground onto a half-slab — the commonest step in the
        /// game, and the one geometry in this suite where the step-up's lift crosses a cell boundary: the body
        /// spans 5.0–6.8 (cells 5–6) and the lifted box reaches 7.299 (cell 7). An envelope sized to the un-lifted
        /// body therefore fails to gather cell 7, and <b>3 of this scenario's 4 sweeps fell back</b> to a direct
        /// scan when that was tried — while the step still resolved correctly, which is the whole problem.
        /// <para>
        /// <b>Why this scenario exists rather than an assertion on B8/B9</b> (measured 2026-08-04). Both step-up
        /// baselines stand the body on a half-slab at 5.5, so its top face is 7.3 and the lifted box is 7.8 —
        /// <i>both cell 7</i>. Containment is cell-granular, so dropping the entire <c>stepHeight</c> term changed
        /// no gathered cell there and B8/B9 stayed green; they carry the zero-fallback assertion anyway, but they
        /// cannot discriminate this. Standing on flat ground is what makes the shortfall cross a cell line.
        /// </para>
        /// <para>
        /// The step itself is asserted too, so the coverage check cannot pass because nothing stepped.
        /// </para>
        /// </summary>
        private static bool StepUpFromFlatGroundGathersLiftedCells()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                world.SetBlock(WALL_X, GROUND_Y + 1, z, Id.HalfSlab); // top at GROUND_TOP + 0.5 — exactly stepHeight

            world.PlaceEntity(new Vector3(WALL_X - 0.5f, GROUND_TOP, 8.5f));
            world.SetGrounded(true);

            PhysicsQueryStats.Reset();
            Vector3 resolved = world.Step(new Vector3(0.2f, 0f, 0f));

            bool ok = ExpectApprox(world.Position.y, GROUND_TOP + TestPhysicsBlockPalette.HalfSlabHeight,
                "the entity must step up onto the slab's authored top");
            ok &= ExpectApprox(resolved.x, 0.2f, "horizontal displacement through the step-up", EXACT_TOLERANCE);
            ok &= ExpectGatherCovered("step-up from flat ground");
            return ok;
        }

        /// <summary>
        /// <b>PH-2's invariant.</b> <c>CalculateVelocity</c> resolves the whole substep chain without touching the
        /// transform: the running position is a local, and the body is moved exactly once, by the caller's
        /// <c>transform.Translate(Velocity)</c>. Before <c>PH-2</c> the loop staged each substep on the transform and
        /// subtracted the sum afterwards, so the transform held a not-yet-final position for the duration of the tick
        /// — and a throw inside the loop left the body teleported by the partial sum, because the revert never ran.
        /// <para>
        /// <b>Why <see cref="Transform.hasChanged"/> and not a before/after position compare.</b> The staging path
        /// <i>reverts</i> what it wrote, so the two positions can come out exactly equal by luck — the same
        /// blind spot <c>B25</c> documents for cell granularity, in value form, and it would make this baseline a
        /// false green. The dirty flag latches on any write and a revert does not clear it, so it reds against the
        /// staging path whatever the arithmetic does.
        /// </para>
        /// <para>
        /// The fall is through open air and fast enough that the <i>resolved</i> displacement still exceeds
        /// <c>maxStep</c>, and that is asserted first: below the threshold the tick takes the single-resolve branch,
        /// which never wrote the transform even before <c>PH-2</c>, and the guard would pass without exercising the
        /// chain at all.
        /// </para>
        /// </summary>
        private static bool CalculateVelocityLeavesTransformUntouched()
        {
            // The solver's tunneling threshold: MIN_COLLISION_THICKNESS (0.25) * 0.5.
            const float MAX_STEP = 0.125f;

            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            // Far above the ground, so the fall resolves against open air and the resolved displacement is the
            // intended one — no correction can shrink it back under the threshold.
            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + 30f, 8.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(-(PhysicsTestWorld.EntityHeight + 0.5f) / PhysicsTestWorld.FixedDeltaTime);

            world.ClearTransformChanged();
            Vector3 resolved = world.CalculateVelocityOnly();

            bool ok = Expect(Mathf.Abs(resolved.y) > MAX_STEP,
                $"this guard only means something if the tick ran the SUBSTEP chain: the resolved |dy| " +
                $"{Mathf.Abs(resolved.y)} must exceed maxStep {MAX_STEP}");
            ok &= Expect(!world.TransformChanged,
                "CalculateVelocity must not write the entity transform — PH-2 accumulates the substep chain's " +
                "running position in a local, and the body is moved once by the caller's transform.Translate");
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

        /// <summary>
        /// The upper bound on how generous the ground verdict may be: a body clearly airborne above a surface must
        /// read not-grounded, or it can jump in mid-air. Paired with <see cref="LeavingSupportEndsAirborne"/> these
        /// are the tripwires for <c>PLAYER_BUGS</c> §04's fix, which necessarily makes the ground probe reach
        /// <i>slightly</i> below the body's feet — this pins how far "slightly" may go.
        /// <para>
        /// The hover gap is two orders of magnitude above the solver's landing stand-off and two below the thinnest
        /// collision volume, so it sits in neither's noise.
        /// </para>
        /// </summary>
        private static bool HoveringIsNotGrounded()
        {
            const float HOVER_GAP = 0.05f;

            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + HOVER_GAP, 8.5f));
            world.SetGrounded(true);

            world.Resolve(new Vector3(0.05f, 0f, 0f));

            return Expect(!world.IsGrounded,
                $"a body hovering {HOVER_GAP} above the surface must not read grounded");
        }

        /// <summary>
        /// The grounded verdict must describe where the body <i>ended</i> the tick, not the best moment within it. A
        /// body that walks off a one-block ledge fast enough to substep is over support for the early substeps and
        /// over nothing for the last, and must end airborne.
        /// <para>
        /// This is the tripwire against "fix the substep chain by latching the verdict across it": a latch would keep
        /// the early substeps' grounded verdict and hand the player a mid-air jump window.
        /// </para>
        /// </summary>
        private static bool LeavingSupportEndsAirborne()
        {
            const int SUPPORT_X = 8;
            const int SUPPORT_Z = 8;
            // Fast enough for two things at once: the tick's displacement must exceed the solver's substep threshold
            // (so the walk off the ledge splits across several resolves — the only way this scenario can see a latch),
            // and it must clear the whole footprint off the one-cell support within that single tick.
            const float LEDGE_WALK_SPEED = 60f;

            using PhysicsTestWorld world = NewFixture();
            world.SetBlock(SUPPORT_X, GROUND_Y, SUPPORT_Z, Id.Ground);
            world.PlaceEntity(new Vector3(SUPPORT_X + 0.5f, GROUND_TOP, SUPPORT_Z + 0.5f));
            world.SetGrounded(true);
            world.Body.walkSpeed = LEDGE_WALK_SPEED;
            world.SetMovementIntent(Vector3.right);

            world.Tick();

            bool ok = Expect(world.Position.x - PhysicsTestWorld.EntityHalfWidthX > SUPPORT_X + 1f,
                "precondition: the tick must actually carry the whole footprint past the ledge, got feet-min X " +
                $"{Format(world.Position.x - PhysicsTestWorld.EntityHalfWidthX)}");
            ok &= Expect(!world.IsGrounded, "a body that left its support during the tick must end airborne");
            return ok;
        }

        #region PLAYER_BUGS §04 regression guards (promoted from K04a–K04d, fixed August 2026)

        // These four shipped as known-bug reproductions, were observed red against the unfixed solver, and were
        // promoted here after the fix was confirmed in game. They are the only baselines in this suite with a
        // documented red observation from the engine's real defect rather than from a deliberate mutation.

        /// <summary>
        /// Gap between the entity's feet and the surface at the start of the landing tick, chosen to sit inside one
        /// substep length at terminal fall speed (<c>|gravity| * fixedDeltaTime / substeps</c>) so contact happens on
        /// the <i>first</i> substep and two substeps remain after it. This was the measured trigger window for §04:
        /// contact any later in the chain left the verdict correct even before the fix, which is why the in-game report
        /// was intermittent.
        /// </summary>
        private const float STUCK_TRIGGER_GAP = 0.05f;

        /// <summary>
        /// Builds the §04 trigger: a body one sub-substep above flat ground, falling at the gravity clamp so the
        /// tick's displacement is large enough to substep.
        /// </summary>
        /// <param name="world">The fixture to seed and position.</param>
        private static void ArrangeFastLanding(PhysicsTestWorld world)
        {
            world.FillLayer(GROUND_Y, Id.Ground);
            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + STUCK_TRIGGER_GAP, 8.5f));
            world.SetGrounded(false);
            // At or past the clamp, gravity leaves the momentum alone, so this is an exact fall speed.
            world.SetVerticalMomentum(PhysicsTestWorld.EntityGravity);
        }

        /// <summary>
        /// One full tick that lands the body must leave it grounded — <c>ResolveMovement</c> runs once per substep and
        /// reassigns the verdict each time, so the substeps that trail a landing must not clear what the landing
        /// substep set. The body's <i>position</i> is asserted too: the §04 defect left the geometry correct and only
        /// the state wrong, so a future regression must not "pass" by moving where the body rests.
        /// </summary>
        private static bool FastLandingEndsGrounded()
        {
            using PhysicsTestWorld world = NewFixture();
            ArrangeFastLanding(world);

            world.Tick();

            bool ok = ExpectApprox(world.Position.y, GROUND_TOP, "rest height after a high-speed landing");
            ok &= Expect(world.IsGrounded,
                "the solver must report grounded after a substepped landing (the tail substeps must not clear the " +
                "verdict the landing substep set)");
            return ok;
        }

        /// <summary>
        /// The verdict must also survive the ticks that follow, and the momentum assertion is what makes this more
        /// than a repeat of <see cref="FastLandingEndsGrounded"/>: a not-grounded body never gets
        /// <c>_verticalMomentum</c> zeroed, so it keeps re-entering the substep path. A resting body whose fall speed
        /// is still pinned at the gravity clamp is the machinery that made §04 permanent rather than a one-tick blip.
        /// </summary>
        private static bool GroundedSurvivesAfterFastLanding()
        {
            const int SETTLE_TICKS = 8;

            using PhysicsTestWorld world = NewFixture();
            ArrangeFastLanding(world);

            world.Tick();
            for (int i = 0; i < SETTLE_TICKS; i++)
                world.Tick();

            bool ok = ExpectApprox(world.Position.y, GROUND_TOP, "rest height after settling");
            ok &= Expect(world.IsGrounded, $"the solver must still report grounded {SETTLE_TICKS} ticks after landing");
            ok &= Expect(world.VerticalMomentum > PhysicsTestWorld.EntityGravity + 1f,
                "a resting body's vertical momentum must not stay pinned at the gravity clamp " +
                $"({PhysicsTestWorld.EntityGravity}), got {Format(world.VerticalMomentum)}");
            return ok;
        }

        /// <summary>
        /// §04's user-visible symptom, through the real public entry point: <c>RequestJump</c> is a pure gate on
        /// <c>IsGrounded</c>, so a wrong verdict <b>refuses</b> the jump rather than applying and losing it.
        /// </summary>
        private static bool JumpAcceptedAfterFastLanding()
        {
            using PhysicsTestWorld world = NewFixture();
            ArrangeFastLanding(world);

            world.Tick();
            world.Body.RequestJump();

            return Expect(world.JumpRequested,
                "a jump requested while resting on the ground must be accepted, not refused");
        }

        /// <summary>
        /// The same grounded question with substepping removed entirely: a body the solver itself landed, then moved
        /// horizontally with no vertical component. The zero-vertical-movement branch owns the verdict here, and it
        /// must recognize the surface the body is standing on even though flush contact is not overlap.
        /// <para>
        /// The body is landed by a resolve rather than placed at a literal stand-off height on purpose — the solver's
        /// <c>COLLISION_EPSILON</c> is private and this suite does not mirror its value.
        /// </para>
        /// </summary>
        private static bool HorizontalResolveStaysGrounded()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);
            world.PlaceEntity(new Vector3(8.5f, GROUND_TOP + 1f, 8.5f));
            world.SetGrounded(false);
            world.Step(new Vector3(0f, -1.5f, 0f));

            bool ok = Expect(world.IsGrounded, "precondition: the landing resolve must ground the body");

            world.Resolve(new Vector3(0.05f, 0f, 0f));

            ok &= Expect(world.IsGrounded,
                "a horizontal-only resolve must not drop the grounded verdict for a body resting on a surface");
            return ok;
        }

        #endregion

        #region Horizontal multi-cell aggregation (B24)

        /// <summary>Feet-center X the depth-pairing push starts from — half a cell west of the obstacle column.</summary>
        private const float DEPTH_START_X = WALL_X - 0.5f;

        /// <summary>The east-half slab's blocking face: the <i>farther</i> of the pairing's two faces.</summary>
        private const float DEPTH_SLAB_FACE = WALL_X + 0.5f;

        /// <summary>Feet-center Z of the depth pairing — chosen so the footprint spans cells 8 and 9.</summary>
        private const float DEPTH_Z = 9f;

        /// <summary>How far past the farther face the push carries the body's leading edge.</summary>
        private const float DEPTH_OVERSHOOT = 0.1f;

        /// <summary>
        /// Push distance for the depth pairing. It must carry the body's leading face past the <b>farther</b> volume
        /// (the slab's), because a push that only reaches the nearer cube's face leaves the slab un-overlapped and
        /// the scenario degenerates into <see cref="WallStopsOneAxisOnly"/>. Derived from the pinned collider so a
        /// retune of the entity's width cannot silently under-shoot it.
        /// </summary>
        private const float DEPTH_PUSH =
            DEPTH_SLAB_FACE - (DEPTH_START_X + PhysicsTestWorld.EntityHalfWidthX) + DEPTH_OVERSHOOT;

        /// <summary>
        /// Multi-contact aggregation on a <b>horizontal</b> axis, discriminated by <i>depth</i>: with the footprint
        /// spanning two cells whose blocking faces differ on X — a full cube at <c>x = 10.0</c> beside an east-half
        /// slab at <c>x = 10.5</c> — the body must stop at the nearer face, <c>10.00</c>. Stopping at the slab's
        /// face instead would leave the body half a block inside the cube.
        /// <para>
        /// <b>Why this exists.</b> Until it landed, the first-contact-wins mutation reddened only <b>B7</b>, a
        /// <i>vertical</i> support case: <see cref="WallStopsOneAxisOnly"/> is a uniform wall plane,
        /// <see cref="RotatedWallSlabBlocksOneSide"/> / <see cref="AdjacentRotatedSlabsFillSpace"/> vary rotation
        /// rather than depth, and <see cref="InsideCornerSettles"/> spans two axes at equal faces — so no baseline
        /// observed the horizontal aggregation <c>PH-1</c>'s gather re-orders. Measured correct 2026-08-03 during the
        /// <c>PLAYER_BUGS</c> §05 analysis; this pins it.
        /// </para>
        /// <para>
        /// <b>Three parts, and the third is not redundant.</b> The slab-only run establishes that the two faces
        /// really are half a cell apart and that the slab is reachable at all — without it, "stopped at 10.00" could
        /// mean the slab was never in the sweep. The pairing then runs in <b>both Z orderings</b>: the scan visits
        /// cells in ascending Z, so only the slab-first ordering reds under first-contact-wins, and the swapped run
        /// is what keeps this baseline meaningful if the traversal order ever changes — which is exactly what
        /// <c>PH-1</c>'s gather-once refactor does.
        /// </para>
        /// </summary>
        private static bool HorizontalAggregationPicksNearerFace()
        {
            BlockType[] probe = TestPhysicsBlockPalette.Create();
            if (!TestPhysicsBlockPalette.TryFindMeta(probe[Id.HalfSlab], OccupiesEastHalf, out byte eastMeta))
                return Expect(false, "fixture: no Facing6Roll2 metadata rotates the slab into the cell's +X half");

            // Part 1: the slabs alone. Their face is half a cell farther out than the cube's, so the pairing below
            // has something to discriminate — and a body that reaches this face reaches both volumes.
            bool ok;
            using (PhysicsTestWorld world = NewDepthFixture())
            {
                world.SetBlock(WALL_X, GROUND_Y + 1, 8, Id.HalfSlab, eastMeta);
                world.SetBlock(WALL_X, GROUND_Y + 1, 9, Id.HalfSlab, eastMeta);

                world.Step(new Vector3(DEPTH_PUSH, 0f, 0f));
                ok = ExpectApprox(world.Position.x, DEPTH_SLAB_FACE - PhysicsTestWorld.EntityHalfWidthX,
                    "fixture: against the rotated slabs alone the body must stop at their +X-half face — if it does " +
                    "not, the pairing below is not testing two different faces");
            }

            // Parts 2 and 3: one cell of the pair becomes a full cube, in both scan orderings.
            ok &= DepthPairingStopsAtCube(eastMeta, cubeZ: 9, slabZ: 8,
                "with the slab visited first, the deeper cube's face must still win the aggregation");
            ok &= DepthPairingStopsAtCube(eastMeta, cubeZ: 8, slabZ: 9,
                "the same pairing in the opposite scan order must resolve identically — aggregation must not " +
                "depend on which cell the sweep reaches first");
            return ok;
        }

        /// <summary>
        /// Runs one ordering of the depth pairing: a full cube and an east-half slab in the same obstacle column,
        /// one cell apart on Z, both under the body's footprint.
        /// </summary>
        /// <param name="eastMeta">Metadata rotating the slab into its cell's +X half.</param>
        /// <param name="cubeZ">Cell Z of the full cube.</param>
        /// <param name="slabZ">Cell Z of the rotated slab.</param>
        /// <param name="what">What this ordering asserts (logged on failure).</param>
        /// <returns>True when the body stopped at the cube's face.</returns>
        private static bool DepthPairingStopsAtCube(byte eastMeta, int cubeZ, int slabZ, string what)
        {
            using PhysicsTestWorld world = NewDepthFixture();
            world.SetBlock(WALL_X, GROUND_Y + 1, cubeZ, Id.Ground);
            world.SetBlock(WALL_X, GROUND_Y + 1, slabZ, Id.HalfSlab, eastMeta);

            world.Step(new Vector3(DEPTH_PUSH, 0f, 0f));

            return ExpectApprox(world.Position.x, WALL_X - PhysicsTestWorld.EntityHalfWidthX, what);
        }

        /// <summary>
        /// Builds the depth-pairing fixture: flat ground, and the entity placed astride cells 8 and 9 with the
        /// grounded flag cleared so the step-up pre-pass cannot lift it over the obstacles instead of resolving
        /// against them (the same precaution <see cref="WalkThroughEmptySlabTop"/> takes).
        /// </summary>
        /// <returns>A fixture the caller owns and must dispose, ready for the obstacle column to be seeded.</returns>
        private static PhysicsTestWorld NewDepthFixture()
        {
            PhysicsTestWorld world = new PhysicsTestWorld(TestPhysicsBlockPalette.Create());
            try
            {
                world.FillLayer(GROUND_Y, Id.Ground);
                world.PlaceEntity(new Vector3(DEPTH_START_X, GROUND_TOP, DEPTH_Z));
                world.SetGrounded(false);
                return world;
            }
            catch
            {
                world.Dispose();
                throw;
            }
        }

        #endregion

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
