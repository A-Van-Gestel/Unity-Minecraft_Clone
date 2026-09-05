using System.Collections.Generic;
using Data;
using Editor.Validation.PhysicsSolver.Framework;
using Physics;
using UnityEngine;
using Id = Editor.Validation.PhysicsSolver.Framework.TestPhysicsBlockPalette.Id;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.PhysicsSolver
{
    /// <summary>
    /// Fluid buoyancy, drag and flow-push baselines (<c>FLUID_BUGS</c> #14 / <c>_FIXED_BUGS</c> Fluid #21) — the solver reading fluid
    /// cells, which every other query in the engine deliberately discards.
    /// <para>
    /// <b>B27 is the positive control and must be read first.</b> The other 26 baselines would all stay green
    /// if the fluid path did nothing whatsoever — they were authored when fluids were invisible to physics
    /// and none of them places a body in one. Worse, <c>World.GatherFluidContact</c> fails <i>soft</i>: it
    /// returns "no fluid" when the job palette or the height templates are missing, so a fixture that forgot
    /// to wire them would satisfy every "the fluid did not do X" assertion here. B27 asserts a measured,
    /// non-zero submersion before anything else is trusted.
    /// </para>
    /// <para>
    /// Expected waterlines come from <see cref="PhysicsTestWorld.SurfaceHeightOf"/>, which calls the shared
    /// <c>FluidMeshData.BuildVertexHeightTemplate</c> the fixture's own templates are built from — the
    /// authored curve, not a second copy of it transcribed into the test.
    /// </para>
    /// </summary>
    public static partial class PhysicsSolverValidationSuite
    {
        /// <summary>Cell Y the fluid column is seeded at — directly on top of the baselines' ground layer.</summary>
        private const int FLUID_Y = 5;

        /// <summary>Cell Y of the second fluid layer, for scenarios needing a body fully under the surface.</summary>
        private const int FLUID_Y_UPPER = 6;

        /// <summary>Chunk-local X/Z the fluid scenarios stand their body on.</summary>
        private const int FLUID_X = 8;

        /// <summary>Chunk-local Z the fluid scenarios stand their body on.</summary>
        private const int FLUID_Z = 8;

        /// <summary>Half-width of the seeded fluid pool, in cells, so the body's whole AABB is inside it.</summary>
        private const int FLUID_POOL_RADIUS = 2;

        /// <summary>A source fluid voxel: full-strength, no falling flag.</summary>
        private const byte FLUID_SOURCE_LEVEL = 0;

        /// <summary>A decayed horizontal level — a visibly shorter column than the source.</summary>
        private const byte FLUID_DECAYED_LEVEL = 4;

        /// <summary>A falling-flagged level, which fills its cell to a full 1.0 regardless of the low bits.</summary>
        private const byte FLUID_FALLING_LEVEL = 8;

        /// <summary>
        /// Mirrors <c>VoxelRigidbody.WATERFALL_CLIMB_FLOOR</c>, which is private to the solver.
        /// </summary>
        /// <remarks>
        /// Duplicated rather than exposed: the constant is a gameplay rule the solver owns, and widening its
        /// visibility so a test can read it would make it look like part of the public contract. The pairing
        /// is asserted loosely (see <see cref="CLIMB_FLOOR_SLACK"/>), so a retune of the solver's value
        /// reddens here rather than silently agreeing with whatever it became.
        /// </remarks>
        private const float WATERFALL_CLIMB_FLOOR = 0.35f;

        /// <summary>
        /// How much of the guaranteed climb distance the assertion demands. Below 1 because the first ticks
        /// of a climb are spent accelerating up to the floor from a standing start, so the average over a
        /// short run sits under the steady-state rate.
        /// </summary>
        private const float CLIMB_FLOOR_SLACK = 0.6f;

        /// <summary>
        /// A far-from-origin anchor for the WS-4 leg. Large enough that a scan reading Unity coordinates as
        /// voxel coordinates lands in a different chunk entirely.
        /// </summary>
        private static readonly ChunkCoord s_farOriginChunk = new ChunkCoord(4096, -4096);

        /// <summary>Registers the fluid buoyancy / drag / push baselines.</summary>
        /// <param name="scenarios">The suite's scenario list.</param>
        static partial void AddFluidBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B27: positive control — a body in a seeded pool measures real submersion",
                FluidPositiveControl));
            scenarios.Add(new Scenario("B28: the waterline follows the authored level curve (source / decayed / falling)",
                SubmersionFollowsLevelCurve));
            scenarios.Add(new Scenario("B29: the fluid query converts to voxel space (far-from-origin anchor)",
                FluidContactSurvivesFarOrigin));
            scenarios.Add(new Scenario("B30: a dry world leaves the solver's fluid path completely inert",
                DryWorldHasNoFluidContact));
            scenarios.Add(new Scenario("B31: buoyancy cancels gravity in proportion to submersion",
                BuoyancyOpposesGravity));
            scenarios.Add(new Scenario("B32: a flowing pool pushes a passive body downstream, toward the low side",
                FlowPushesDownstream));
            scenarios.Add(new Scenario("B33: the swim stroke drives a submerged body upward, capped by submersion",
                SwimStrokeAscends));
            scenarios.Add(new Scenario("B34: the downward swim stroke sinks a submerged body",
                SwimStrokeDescends));
            scenarios.Add(new Scenario("B35: a falling column carries a body down, and never sideways",
                FallingColumnPushesDownNotSideways));
            scenarios.Add(new Scenario("B36: a held upward stroke climbs a falling column",
                SwimStrokeClimbsAFallingColumn));
            scenarios.Add(new Scenario("B37: wading at moderate depth is slowed to the authored multiplier",
                WadingIsSlowedAtModerateDepth));
            scenarios.Add(new Scenario("B38: a swimming body can step out of water onto the bank beside it",
                SwimmerStepsOutOntoTheBank));
            scenarios.Add(new Scenario("B39: a body holding the swim stroke floats mostly submerged, not on top",
                SwimmerFloatsMostlySubmerged));
            scenarios.Add(new Scenario("B41: a jump held while leaving water waits for the button to be released",
                JumpIsDeferredAfterLeavingWater));
            scenarios.Add(new Scenario("B42: climbing out of water requires asking to rise, not just contact",
                ClimbingOutRequiresAskingToRise));
            scenarios.Add(new Scenario("B43: climbing out of water rises by the climb alone — no jump on top",
                ClimbingOutDoesNotAlsoJump));
            scenarios.Add(new Scenario("B44: the fluid query survives job-data teardown instead of reading freed memory",
                FluidContactSurvivesTeardown));
        }

        /// <summary>
        /// B27 — the vacuous-pass guard for this whole family, in the spirit of <c>B1</c>.
        /// <para>
        /// Asserts the query actually reached the seeded fluid: the right fluid type, a submersion strictly
        /// between nothing and everything, and the authored coefficients carried through. A soft-failed
        /// gather (missing palette, missing templates, wrong origin) returns <c>default</c> and reddens here
        /// rather than silently satisfying the negative assertions in the scenarios that follow.
        /// </para>
        /// </summary>
        /// <returns>True when the fixture genuinely put the body in fluid.</returns>
        private static bool FluidPositiveControl()
        {
            using PhysicsTestWorld world = NewFixture();
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            FluidContact contact = world.ResolveFluidContact();

            bool ok = Expect(contact.Type == FluidType.WaterLike,
                $"the body must report the seeded fluid type, got {contact.Type} " +
                "(None means the gather found nothing — check the fixture's job palette and templates)");

            ok &= Expect(contact.InFluid, "the body must report being in fluid");

            ok &= Expect(contact.SubmergedFraction > 0f && contact.SubmergedFraction < 1f,
                $"a body standing in one fluid layer must be partly submerged, got " +
                $"{contact.SubmergedFraction:F4} (0 = the gather missed; 1 = it over-reports)");

            ok &= ExpectApprox(contact.Buoyancy, TestPhysicsBlockPalette.FluidBuoyancy,
                "the authored buoyancy must reach the contact", EXACT_TOLERANCE);

            return ok;
        }

        /// <summary>
        /// B28 — the waterline is the authored height curve, including the falling flag's discontinuity.
        /// <para>
        /// A source, a decayed level and a falling voxel produce three different surface heights, and the
        /// falling one is <b>taller</b> than the decayed one despite carrying a numerically larger level:
        /// level 8 is the falling flag, not "level 8 of a decay ramp". A resolver that masked the flag off,
        /// or that indexed the curve with the effective level, reads the falling column as the shortest
        /// instead of the tallest.
        /// </para>
        /// </summary>
        /// <returns>True when all three levels land on their authored heights.</returns>
        private static bool SubmersionFollowsLevelCurve()
        {
            bool ok = true;

            foreach (byte level in new[] { FLUID_SOURCE_LEVEL, FLUID_DECAYED_LEVEL, FLUID_FALLING_LEVEL })
            {
                using PhysicsTestWorld world = NewFixture();
                SeedPool(world, FLUID_Y, level);

                world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
                FluidContact contact = world.ResolveFluidContact();

                float expectedSurface = FLUID_Y + PhysicsTestWorld.SurfaceHeightOf(level,
                    PhysicsTestWorld.WaterFlowLevels, PhysicsTestWorld.WaterDecayStep);
                float expectedFraction = (expectedSurface - FLUID_Y) / PhysicsTestWorld.EntityHeight;

                ok &= ExpectApprox(contact.SubmergedFraction, expectedFraction,
                    $"submersion at fluid level {level}", PositionTolerance);
            }

            // The ordering property, stated independently of the numbers above: a falling column fills its
            // cell, so it must submerge more than a decayed one even though its level is the larger number.
            float decayed = PhysicsTestWorld.SurfaceHeightOf(FLUID_DECAYED_LEVEL,
                PhysicsTestWorld.WaterFlowLevels, PhysicsTestWorld.WaterDecayStep);
            float falling = PhysicsTestWorld.SurfaceHeightOf(FLUID_FALLING_LEVEL,
                PhysicsTestWorld.WaterFlowLevels, PhysicsTestWorld.WaterDecayStep);

            ok &= Expect(falling > decayed,
                $"a falling column ({falling:F4}) must stand taller than a decayed one ({decayed:F4}) — " +
                "equal or lower means the falling flag was masked off before the curve lookup");

            return ok;
        }

        /// <summary>
        /// B29 — the fluid scan converts Unity space to voxel space, like every other query since WS-4.
        /// <para>
        /// The <c>B17</c> argument, applied to the new scan: with the anchor far from the identity, a scan
        /// that passed its Unity coordinates straight through as voxel coordinates would look up a chunk that
        /// does not exist, find nothing, and report a perfectly plausible "not in fluid".
        /// </para>
        /// </summary>
        /// <returns>True when the far-anchored fixture measures the same submersion as the identity one.</returns>
        private static bool FluidContactSurvivesFarOrigin()
        {
            float atIdentity;
            using (PhysicsTestWorld world = NewFixture())
            {
                SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
                world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
                atIdentity = world.ResolveFluidContact().SubmergedFraction;
            }

            using PhysicsTestWorld far = new PhysicsTestWorld(TestPhysicsBlockPalette.Create(), s_farOriginChunk);
            SeedPool(far, FLUID_Y, FLUID_SOURCE_LEVEL);
            far.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            FluidContact contact = far.ResolveFluidContact();

            bool ok = Expect(contact.InFluid,
                $"the far-anchored body must still find the fluid at {s_farOriginChunk} " +
                "(not in fluid means the scan read Unity coordinates as voxel coordinates)");

            ok &= ExpectApprox(contact.SubmergedFraction, atIdentity,
                "submersion must not depend on the floating-origin anchor", EXACT_TOLERANCE);

            return ok;
        }

        /// <summary>
        /// B30 — the negative control: with no fluid seeded, the contact stays empty and the solver's motion
        /// is untouched. Pairs with B27; alone it would be satisfied by a fluid path that never runs.
        /// </summary>
        /// <returns>True when a dry world produces no contact and no force.</returns>
        private static bool DryWorldHasNoFluidContact()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, GROUND_TOP + 2f, FLUID_Z + 0.5f));
            FluidContact contact = world.ResolveFluidContact();

            bool ok = Expect(!contact.InFluid, "a body in open air must not report fluid contact");
            ok &= Expect(contact.Type == FluidType.None, $"expected FluidType.None, got {contact.Type}");
            ok &= ExpectApprox(contact.SubmergedFraction, 0f, "submersion in air", EXACT_TOLERANCE);
            ok &= Expect(contact.FlowDirection == Vector3.zero,
                $"a dry body must feel no current, got {contact.FlowDirection}");

            // And the force path must be inert, not merely zero-valued: a dry fall accelerates at exactly g.
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);
            world.CalculateVelocityOnly();

            ok &= ExpectApprox(world.VerticalMomentum,
                PhysicsTestWorld.EntityGravity * PhysicsTestWorld.FixedDeltaTime,
                "a dry body must fall at exactly gravity", EXACT_TOLERANCE);

            return ok;
        }

        /// <summary>
        /// B31 — buoyancy opposes gravity in proportion to submersion.
        /// <para>
        /// The fixture's fluid is authored at buoyancy 1 (<see cref="TestPhysicsBlockPalette.FluidBuoyancy"/>),
        /// so a <b>fully</b> submerged body has gravity exactly canceled and, with its momentum starting at
        /// zero and drag acting on zero, stays at zero. That expectation is a clean constant rather than a
        /// restatement of the gravity integration, which is what makes this leg discriminating: a buoyancy
        /// term that ignored submersion, used the wrong sign, or was never applied all leave a non-zero
        /// momentum here.
        /// </para>
        /// <para>
        /// The partial leg then pins the proportionality: a body submerged only part way must still sink,
        /// but strictly more slowly than the same body in air.
        /// </para>
        /// </summary>
        /// <returns>True when both legs hold.</returns>
        private static bool BuoyancyOpposesGravity()
        {
            float dryFall = PhysicsTestWorld.EntityGravity * PhysicsTestWorld.FixedDeltaTime;

            // --- Fully submerged: two fluid layers, body standing on the lower one. ---
            float fullySubmerged;
            using (PhysicsTestWorld world = NewFixture())
            {
                SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
                SeedPool(world, FLUID_Y_UPPER, FLUID_SOURCE_LEVEL); // a second still layer puts the body fully under
                // Deliberately NOT a falling level: that is a waterfall, and would add a downward
                // current on top of the buoyancy this scenario is measuring.
                world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
                world.SetGrounded(false);
                world.SetVerticalMomentum(0f);
                world.CalculateVelocityOnly();
                fullySubmerged = world.VerticalMomentum;
            }

            bool ok = ExpectApprox(fullySubmerged, 0f,
                "a fully submerged body in neutrally buoyant fluid must hold its vertical momentum at zero",
                PositionTolerance);

            // --- Partly submerged: one layer only. ---
            float partlySubmerged;
            float submersion;
            using (PhysicsTestWorld world = NewFixture())
            {
                SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
                world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
                world.SetGrounded(false);
                world.SetVerticalMomentum(0f);
                world.CalculateVelocityOnly();
                partlySubmerged = world.VerticalMomentum;
                submersion = world.FluidContact.SubmergedFraction;
            }

            ok &= Expect(submersion > 0f && submersion < 1f,
                $"the partial leg must actually be partly submerged, got {submersion:F4}");

            ok &= Expect(partlySubmerged < 0f,
                $"a partly submerged body must still sink, got {partlySubmerged:F4}");

            ok &= Expect(partlySubmerged > dryFall,
                $"a partly submerged body must sink more slowly than in air: got {partlySubmerged:F4} " +
                $"against a dry {dryFall:F4}");

            return ok;
        }

        /// <summary>
        /// B32 — a sloped pool carries a passive body toward its <b>low</b> side.
        /// <para>
        /// This is the sign guard for the whole flow path, and the one defect most likely to ship unnoticed:
        /// the meshing flow vector is a UV scroll offset pointing uphill, so a resolver that forwarded it
        /// unnegated would push swimmers upstream — visibly wrong in play, yet indistinguishable from correct
        /// in any test that only checks the axis or the magnitude.
        /// </para>
        /// <para>
        /// The body is given no movement intent at all, so every bit of the resulting displacement is the
        /// current's doing.
        /// </para>
        /// </summary>
        /// <returns>True when the body drifts toward the shallow side.</returns>
        private static bool FlowPushesDownstream()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);

            // Levels rise with +X, and a higher level is a shorter column, so the surface falls toward +X and
            // the current runs that way.
            for (int dx = -FLUID_POOL_RADIUS; dx <= FLUID_POOL_RADIUS; dx++)
            for (int dz = -FLUID_POOL_RADIUS; dz <= FLUID_POOL_RADIUS; dz++)
            {
                byte level = (byte)(dx + FLUID_POOL_RADIUS);
                world.SetBlock(FLUID_X + dx, FLUID_Y, FLUID_Z + dz, Id.Fluid, level);
            }

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);
            world.SetMovementIntent(Vector3.zero);

            FluidContact contact = world.ResolveFluidContact();

            bool ok = Expect(contact.InFluid, "the sloped pool must submerge the body at all");

            ok &= Expect(contact.FlowDirection.x > 0f,
                $"the current must run toward the low side (+X), got flow {contact.FlowDirection} — " +
                "a negative X here means the meshing UV offset was forwarded without being negated");

            ok &= Expect(Mathf.Abs(contact.FlowDirection.z) < Mathf.Abs(contact.FlowDirection.x),
                $"the current must run mainly along the slope, got {contact.FlowDirection}");

            Vector3 displacement = world.CalculateVelocityOnly();

            ok &= Expect(displacement.x > 0f,
                $"a passive body must be carried downstream, got displacement {displacement}");

            return ok;
        }

        /// <summary>
        /// B33 — holding jump under water swims upward, on a body the ordinary jump gate refuses.
        /// <para>
        /// The body is explicitly <b>not</b> grounded, which is exactly the state <c>RequestJump</c> declines
        /// (<c>PLAYER_BUGS</c> §04's distinction). So this also pins that the swim stroke is a genuinely
        /// separate entry point rather than a relaxation of the jump gate.
        /// </para>
        /// </summary>
        /// <returns>True when the stroke produces upward momentum.</returns>
        private static bool SwimStrokeAscends()
        {
            using PhysicsTestWorld world = NewFixture();
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
            SeedPool(world, FLUID_Y_UPPER, FLUID_SOURCE_LEVEL);

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);

            // Establish the contact first: the stroke is a stored intent, gated where it is USED, so the
            // submersion it scales against has to exist before the tick that consumes it.
            FluidContact contact = world.ResolveFluidContact();
            world.SetSwimVerticalIntent(1f);
            world.CalculateVelocityOnly();

            bool ok = Expect(world.VerticalMomentum > 0f,
                $"the swim stroke must drive the body upward, got {world.VerticalMomentum:F4}");

            ok &= Expect(!world.JumpRequested,
                "the swim stroke must not latch an ordinary jump — the two gates are deliberately separate");

            // The stroke is a swim, not a jump out of the pool: it may never exceed the authored speed, and
            // that speed is itself scaled by submersion. Shipping without this let a held jump launch the
            // body clear of the water, where it skipped across the surface untouched by drag.
            float ceiling = TestPhysicsBlockPalette.FluidSwimAscendSpeed * contact.SubmergedFraction;
            ok &= Expect(world.VerticalMomentum <= ceiling + PositionTolerance,
                $"the stroke must not exceed its submersion-scaled target: got {world.VerticalMomentum:F4} " +
                $"against a ceiling of {ceiling:F4} (submersion {contact.SubmergedFraction:F4})");

            return ok;
        }

        /// <summary>
        /// B34 — crouch swims a submerged body <b>down</b>, the mirror of B33.
        /// <para>
        /// Its own baseline rather than a leg of B33 because the descend path shipped missing entirely: the
        /// solver had an ascend-only entry point, so holding crouch under water did nothing at all. A
        /// single-sided stroke API passes every ascend assertion.
        /// </para>
        /// </summary>
        /// <returns>True when the downward stroke drives the body down.</returns>
        private static bool SwimStrokeDescends()
        {
            using PhysicsTestWorld world = NewFixture();
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
            SeedPool(world, FLUID_Y_UPPER, FLUID_SOURCE_LEVEL);

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);

            world.ResolveFluidContact();
            world.SetSwimVerticalIntent(-1f);
            world.CalculateVelocityOnly();

            float withStroke = world.VerticalMomentum;

            // Compared against the same body with no stroke, so this cannot be satisfied by gravity alone —
            // a body in neutrally buoyant fluid holds at zero, and only the stroke can take it below.
            float withoutStroke;
            using (PhysicsTestWorld idle = NewFixture())
            {
                SeedPool(idle, FLUID_Y, FLUID_SOURCE_LEVEL);
                SeedPool(idle, FLUID_Y_UPPER, FLUID_SOURCE_LEVEL);
                idle.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
                idle.SetGrounded(false);
                idle.SetVerticalMomentum(0f);
                idle.ResolveFluidContact();
                idle.CalculateVelocityOnly();
                withoutStroke = idle.VerticalMomentum;
            }

            return Expect(withStroke < withoutStroke - PositionTolerance,
                $"crouch must swim the body down: got {withStroke:F4} with the stroke against " +
                $"{withoutStroke:F4} idle (equal means the descend intent is ignored)");
        }

        /// <summary>
        /// B35 — a falling column carries a body <b>down</b>, and does not shove it sideways.
        /// <para>
        /// A falling voxel stands at full height beside air reporting <c>BurstFluidFlowUtility.DropHeight</c>,
        /// so the shared corner derivative yields a saturated <i>outward</i> vector — right for the renderer,
        /// which should scroll the texture off the column, and a wall if physics forwards it unchanged.
        /// </para>
        /// <para>
        /// The horizontal assertion is the load-bearing one: a downward push could also be produced by simply
        /// zeroing the current, which this pairs with B32 to rule out.
        /// </para>
        /// </summary>
        /// <returns>True when the falling column's current runs downward only.</returns>
        private static bool FallingColumnPushesDownNotSideways()
        {
            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);

            // A falling column running down a cliff face — solid on -X, open air elsewhere. The asymmetry
            // is load-bearing: a column open on all four sides produces four outward corner vectors that
            // cancel in the mean, so a symmetric fixture measures zero and passes without the fix.
            world.SetBlock(FLUID_X - 1, FLUID_Y, FLUID_Z, Id.Ground);
            world.SetBlock(FLUID_X - 1, FLUID_Y_UPPER, FLUID_Z, Id.Ground);
            world.SetBlock(FLUID_X, FLUID_Y, FLUID_Z, Id.Fluid, FLUID_FALLING_LEVEL);
            world.SetBlock(FLUID_X, FLUID_Y_UPPER, FLUID_Z, Id.Fluid, FLUID_FALLING_LEVEL);

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            FluidContact contact = world.ResolveFluidContact();

            bool ok = Expect(contact.InFluid, "the falling column must submerge the body at all");

            ok &= Expect(contact.IsFalling,
                "the waterline fluid must be recognized as a falling column");

            ok &= Expect(contact.FlowDirection == Vector3.zero,
                $"a falling column must carry no horizontal current, got {contact.FlowDirection} — " +
                "a non-zero value here is the renderer's outward scroll leaking into physics");

            // The pull itself is a vertical-momentum target, not part of the current, so it is observed by
            // ticking: the same body in a still pool is the control, which stops "no horizontal push" from
            // being satisfiable by a resolver that simply returns nothing at all.
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);
            world.CalculateVelocityOnly();
            float inFallingColumn = world.VerticalMomentum;

            float inStillPool;
            using (PhysicsTestWorld still = NewFixture())
            {
                SeedPool(still, FLUID_Y, FLUID_SOURCE_LEVEL);
                SeedPool(still, FLUID_Y_UPPER, FLUID_SOURCE_LEVEL);
                still.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
                still.SetGrounded(false);
                still.SetVerticalMomentum(0f);
                still.ResolveFluidContact();
                still.CalculateVelocityOnly();
                inStillPool = still.VerticalMomentum;
            }

            ok &= Expect(inFallingColumn < inStillPool - PositionTolerance,
                $"a falling column must drag the body down harder than still fluid: got " +
                $"{inFallingColumn:F4} against {inStillPool:F4} in a still pool");

            return ok;
        }

        /// <summary>
        /// B36 — a swimmer can climb a waterfall, slowly.
        /// <para>
        /// The falling current and the swim stroke both act on vertical momentum, and the stroke's
        /// acceleration is the larger, so holding "up" gains on the current every tick. A current applied as
        /// displacement instead would bypass momentum entirely and no stroke could touch it.
        /// </para>
        /// <para>
        /// Asserted over several ticks rather than one, because the point is that the stroke <i>gains</i> on
        /// the current rather than instantly beating it; a single tick cannot tell a slow climb from a stall.
        /// </para>
        /// </summary>
        /// <returns>True when a held upward stroke climbs against the falling current.</returns>
        private static bool SwimStrokeClimbsAFallingColumn()
        {
            const int TICKS = 30;

            using PhysicsTestWorld world = NewFixture();
            world.FillLayer(GROUND_Y, Id.Ground);

            // SinkingFluid, NOT the neutral Fluid: Id.Fluid's buoyancy of 1 cancels gravity outright, so a
            // body in it wins any vertical contest for free. This buoyancy is the case that can actually lose.
            for (int dy = 0; dy < 6; dy++)
                world.SetBlock(FLUID_X, FLUID_Y + dy, FLUID_Z, Id.SinkingFluid, FLUID_FALLING_LEVEL);

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y + 2, FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);
            world.SetSwimVerticalIntent(1f);

            float startY = world.Position.y;
            for (int i = 0; i < TICKS; i++)
                world.Tick();

            bool ok = Expect(world.FluidContact.IsFalling,
                "the body must still be inside the falling column at the end of the climb");

            ok &= Expect(world.Position.y > startY,
                $"a held upward stroke must climb the column: went from {startY:F4} to {world.Position.y:F4}");

            // The floor is the actual contract — "went up at all" would also be satisfied by a body that
            // crawls up at a rate no player would recognize as escaping.
            float climbed = world.Position.y - startY;
            const float floorSpeed = TestPhysicsBlockPalette.FluidSwimAscendSpeed * WATERFALL_CLIMB_FLOOR;
            float expectedAtLeast = floorSpeed * TICKS * PhysicsTestWorld.FixedDeltaTime * CLIMB_FLOOR_SLACK;

            ok &= Expect(climbed >= expectedAtLeast,
                $"the climb must hold the guaranteed floor: rose {climbed:F4} over {TICKS} ticks, expected " +
                $"at least {expectedAtLeast:F4} (floor {floorSpeed:F4} m/s)");

            return ok;
        }

        /// <summary>
        /// B37 — a body wading at moderate depth is meaningfully slowed.
        /// <para>
        /// The speed penalty ramps to full strength over
        /// <c>VoxelRigidbody.FULL_HORIZONTAL_DRAG_SUBMERSION</c> rather than across the whole collider.
        /// Scaling it linearly over the full height meant a body floating at the surface — where submersion
        /// is small by construction — kept almost all of its walking speed and slid across the top of the
        /// water, which is what this pins against.
        /// </para>
        /// </summary>
        /// <returns>True when submerged horizontal travel is close to the authored multiplier.</returns>
        private static bool WadingIsSlowedAtModerateDepth()
        {
            float dryTravel;
            using (PhysicsTestWorld dry = NewFixture())
            {
                dry.FillLayer(GROUND_Y, Id.Ground);
                dry.PlaceEntity(new Vector3(FLUID_X + 0.5f, GROUND_TOP, FLUID_Z + 0.5f));
                dry.SetGrounded(true);
                dry.SetMovementIntent(Vector3.right);
                dryTravel = dry.CalculateVelocityOnly().x;
            }

            float wetTravel;
            float submersion;
            using (PhysicsTestWorld wet = NewFixture())
            {
                SeedPool(wet, FLUID_Y, FLUID_SOURCE_LEVEL);
                wet.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
                wet.SetGrounded(true);
                wet.SetMovementIntent(Vector3.right);
                wet.ResolveFluidContact();
                wetTravel = wet.CalculateVelocityOnly().x;
                submersion = wet.FluidContact.SubmergedFraction;
            }

            bool ok = Expect(submersion > 0f, $"the wading leg must actually be in fluid, got {submersion:F4}");

            // One fluid layer already exceeds the ramp's knee, so the authored multiplier applies in full.
            float expected = dryTravel * TestPhysicsBlockPalette.FluidSubmergedSpeedMultiplier;
            ok &= ExpectApprox(wetTravel, expected,
                $"submerged horizontal travel (dry {dryTravel:F4}, submersion {submersion:F4})",
                PositionTolerance);

            return ok;
        }

        /// <summary>
        /// B38 — a swimmer pressing into the bank steps out of the water instead of swimming into it forever.
        /// <para>
        /// The step-up pre-pass is the engine's existing "climb a slab" mechanism, and it was gated on
        /// <c>IsGrounded</c>. A floating body is never grounded, so the gate silently made leaving a pool
        /// impossible: the body would push into the shore, be blocked, and stay at the waterline. Buoyancy
        /// now counts as support for that pre-pass, which is what this pins.
        /// </para>
        /// <para>
        /// The body is explicitly <b>not</b> grounded, so a step-up here can only come from the fluid arm of
        /// the gate — the assertion cannot be satisfied by ordinary standing behavior.
        /// </para>
        /// </summary>
        /// <returns>True when the blocked swimmer is lifted toward the bank top.</returns>
        private static bool SwimmerStepsOutOntoTheBank()
        {
            const int SETTLE_TICKS = 300;

            using PhysicsTestWorld world = NewFixture();

            // Two-deep water with a one-block bank. The body settles to its own float equilibrium rather
            // than being placed: that equilibrium is what puts the bank out of walking-step reach, so a
            // fixture parking it near the bank top would prove nothing.
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
            SeedPool(world, FLUID_Y + 1, FLUID_SOURCE_LEVEL);

            const int bankX = FLUID_X + FLUID_POOL_RADIUS + 1;
            for (int dz = -FLUID_POOL_RADIUS; dz <= FLUID_POOL_RADIUS; dz++)
            for (int dy = 0; dy <= 1; dy++)
                world.SetBlock(bankX, FLUID_Y + dy, FLUID_Z + dz, Id.Ground);

            world.PlaceEntity(new Vector3(bankX - PhysicsTestWorld.EntityHalfWidthX - 0.01f, FLUID_Y,
                FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);
            // Held through the Resolve below, not just during the settle: since the climb became
            // player-driven, the stroke is what admits it (B42 drives the same fixture without it).
            world.SetSwimVerticalIntent(1f);

            for (int i = 0; i < SETTLE_TICKS; i++)
                world.Tick();

            const float bankTop = FLUID_Y + 2f;
            float reachFromFeet = bankTop - world.Position.y;

            bool ok = Expect(world.FluidContact.InFluid,
                "the settled body must still be in the water it is trying to leave");

            // States the problem in the assertion itself: the bank is genuinely out of WALKING reach, so a
            // pass cannot come from the ordinary step height.
            ok &= Expect(reachFromFeet > PhysicsTestWorld.EntityStepHeight,
                $"fixture: the bank must be out of walking-step reach to be worth testing — it sits " +
                $"{reachFromFeet:F4} above the feet against a step height of " +
                $"{PhysicsTestWorld.EntityStepHeight:F4}");

            Vector3 resolved = world.Resolve(new Vector3(0.1f, 0f, 0f));

            ok &= Expect(resolved.y > 0f,
                $"a swimmer pressing into the bank must be stepped up onto it, got a resolved {resolved} " +
                "(zero Y means the pre-pass refused it — either for not being grounded, or for a bank " +
                "beyond the swimming step height)");

            return ok;
        }

        /// <summary>
        /// B41 — a jump held while climbing out of water does not fire the instant the body lands.
        /// <para>
        /// Swimming up is done by holding jump, so the same input that lifts the body out is still held when
        /// it arrives on the bank. Without the block the jump fires on that frame, fusing the climb and a
        /// full jump into one launch.
        /// </para>
        /// <para>
        /// Both halves are asserted, because a delay that never expires would also pass the first: the
        /// request is refused immediately after the exit, and accepted again once the delay elapses.
        /// </para>
        /// </summary>
        /// <returns>True when the jump is deferred and then allowed.</returns>
        private static bool JumpIsDeferredAfterLeavingWater()
        {
            using PhysicsTestWorld world = NewFixture();
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);

            // In the water with jump held, then out of it: that transition is what arms the block.
            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            world.Body.SetJumpHeld(true);
            world.ResolveFluidContact();

            bool ok = Expect(world.FluidContact.InFluid, "fixture: the body must start in the water");

            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, GROUND_TOP + 4f, FLUID_Z + 0.5f));
            world.ResolveFluidContact();

            ok &= Expect(!world.FluidContact.InFluid, "fixture: the body must have left the water");

            world.SetGrounded(true);
            world.Body.RequestJump();

            ok &= Expect(!world.JumpRequested,
                "a jump held across the exit from water must not latch on the landing frame");

            // Releasing is the only thing that clears it: a time-based expiry would let the jump fire while
            // the button stayed down, which is the behavior being ruled out.
            world.Body.SetJumpHeld(false);
            world.SetGrounded(true);
            world.Body.RequestJump();

            ok &= Expect(world.JumpRequested,
                "releasing and pressing again must be accepted — a block that never clears would pass the " +
                "assertion above while breaking jumping outright");

            return ok;
        }

        /// <summary>
        /// B42 — a swimmer that is not asking to go up is not hauled out of the water by walking into the
        /// bank.
        /// <para>
        /// The climb out is a much larger movement than a slab step — most of a block against half of one —
        /// so triggering it on contact alone fires it without the player asking, including for a body falling
        /// back toward the water that brushes the bank while still partly submerged.
        /// </para>
        /// <para>
        /// Paired with <c>B38</c>, which drives the identical fixture <i>with</i> the stroke held. Alone,
        /// either one is satisfiable by a step-up that is simply broken.
        /// </para>
        /// </summary>
        /// <returns>True when the un-asked climb is refused.</returns>
        private static bool ClimbingOutRequiresAskingToRise()
        {
            const int SETTLE_TICKS = 300;

            using PhysicsTestWorld world = NewFixture();
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
            SeedPool(world, FLUID_Y + 1, FLUID_SOURCE_LEVEL);

            const int bankX = FLUID_X + FLUID_POOL_RADIUS + 1;
            for (int dz = -FLUID_POOL_RADIUS; dz <= FLUID_POOL_RADIUS; dz++)
            for (int dy = 0; dy <= 1; dy++)
                world.SetBlock(bankX, FLUID_Y + dy, FLUID_Z + dz, Id.Ground);

            world.PlaceEntity(new Vector3(bankX - PhysicsTestWorld.EntityHalfWidthX - 0.01f, FLUID_Y,
                FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);

            // Settle to the float line holding the stroke, exactly as B38 does — then LET GO before pressing
            // into the bank. The settled state is identical; only the intent at the moment of contact differs.
            world.SetSwimVerticalIntent(1f);
            for (int i = 0; i < SETTLE_TICKS; i++)
                world.Tick();

            world.SetSwimVerticalIntent(0f);

            bool ok = Expect(world.FluidContact.InFluid,
                "the settled body must still be in the water it is declining to leave");

            Vector3 resolved = world.Resolve(new Vector3(0.1f, 0f, 0f));

            ok &= ExpectApprox(resolved.y, 0f,
                "a swimmer not asking to rise must not be stepped out of the water", PositionTolerance);

            return ok;
        }

        /// <summary>
        /// B39 — a body holding the swim stroke settles <b>in</b> the water, not on top of it.
        /// <para>
        /// The float height is an emergent balance, not a number anyone sets, which is exactly why it needs
        /// pinning: every individual force was behaving correctly while the equilibrium they produced was
        /// wrong. The stroke's per-tick authority is worth about as much as one tick of gravity, so while it
        /// kept full strength as the body rose it canceled gravity at <i>any</i> depth and the body settled
        /// around a tenth submerged — floating on the surface, keeping most of its walking speed, and
        /// skimming across the top. Scaling the authority by submersion is what moves the balance under.
        /// </para>
        /// <para>
        /// Asserts a band rather than a value: the exact height depends on buoyancy, drag and stroke speed
        /// together, so pinning it precisely would redden on any authored retune. The band is what the
        /// defect violated by a wide margin.
        /// </para>
        /// </summary>
        /// <returns>True when the settled body is mostly, but not entirely, under the surface.</returns>
        private static bool SwimmerFloatsMostlySubmerged()
        {
            const int SETTLE_TICKS = 300;
            const float MINIMUM_SUBMERSION = 0.4f;

            using PhysicsTestWorld world = NewFixture();
            for (int dy = 0; dy < 3; dy++)
                SeedPool(world, FLUID_Y + dy, FLUID_SOURCE_LEVEL);

            // Start deep and let it rise to its own equilibrium, so the result is the balance the forces
            // produce rather than wherever the body happened to be placed.
            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);
            world.SetSwimVerticalIntent(1f);

            for (int i = 0; i < SETTLE_TICKS; i++)
                world.Tick();

            float settled = world.FluidContact.SubmergedFraction;

            bool ok = Expect(settled >= MINIMUM_SUBMERSION,
                $"a floating body must stay mostly submerged, got {settled:F4} (below {MINIMUM_SUBMERSION:F2} " +
                "means it is riding on top of the water, where the speed penalty barely applies)");

            ok &= Expect(settled < 1f,
                $"a body holding the stroke must reach the surface, not stay fully under, got {settled:F4}");

            return ok;
        }

        /// <summary>
        /// B43 — climbing out of water rises by the climb and nothing more.
        /// <para>
        /// The end-to-end counterpart of <c>B41</c>, which asserts only that a jump request is not
        /// <i>latched</i> and so cannot observe an ordering that latches it a tick early: the step-up grounds
        /// the body inside its own tick, letting the input layer ask for a jump a render frame before the
        /// fluid-exit edge arms the block.
        /// </para>
        /// <para>
        /// Asserted as <b>peak equals final</b> rather than "no jump fired": a jump is only one way to gain
        /// height that is given back, and this catches any of them. The jump counter is checked too, so a
        /// failure says which.
        /// </para>
        /// </summary>
        /// <returns>True when the body ends where it peaked, having jumped zero times.</returns>
        private static bool ClimbingOutDoesNotAlsoJump()
        {
            const int SETTLE_TICKS = 300;
            const int CLIMB_TICKS = 60;

            using PhysicsTestWorld world = NewFixture();
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
            SeedPool(world, FLUID_Y + 1, FLUID_SOURCE_LEVEL);

            // The bank runs several cells deep, so the body cannot walk off its far side and fall — which
            // would look exactly like the overshoot this is measuring.
            const int bankX = FLUID_X + FLUID_POOL_RADIUS + 1;
            for (int dz = -FLUID_POOL_RADIUS; dz <= FLUID_POOL_RADIUS; dz++)
            for (int dy = 0; dy <= 1; dy++)
            for (int dx = 0; dx < 4; dx++)
                world.SetBlock(bankX + dx, FLUID_Y + dy, FLUID_Z + dz, Id.Ground);

            world.PlaceEntity(new Vector3(bankX - PhysicsTestWorld.EntityHalfWidthX - 0.05f, FLUID_Y,
                FLUID_Z + 0.5f));
            world.SetGrounded(false);
            world.SetVerticalMomentum(0f);

            // Jump is held for the whole scenario, exactly as it is when a player swims up and out.
            world.Body.SetJumpHeld(true);
            world.SetSwimVerticalIntent(1f);
            for (int i = 0; i < SETTLE_TICKS; i++)
                world.Tick();

            float startY = world.Position.y;
            float peakY = startY;
            uint jumpsBefore = world.Body.JumpCount;

            for (int i = 0; i < CLIMB_TICKS; i++)
            {
                // Mirrors Player.Update's branch order, which is what creates the one-tick window.
                world.Body.SetJumpHeld(true);
                if (world.IsGrounded) world.Body.RequestJump();
                else world.SetSwimVerticalIntent(1f);
                world.SetMovementIntent(Vector3.right);

                world.Tick();
                peakY = Mathf.Max(peakY, world.Position.y);
            }

            float climbed = world.Position.y - startY;
            float overshoot = peakY - world.Position.y;

            bool ok = Expect(climbed > PhysicsTestWorld.EntityStepHeight,
                $"fixture: the body must actually have climbed out, got {climbed:F4}");

            ok &= Expect(world.Body.JumpCount == jumpsBefore,
                $"climbing out must not jump: the counter went from {jumpsBefore} to {world.Body.JumpCount}");

            ok &= ExpectApprox(overshoot, 0f,
                $"the body must end where it peaked (climbed {climbed:F4}, peaked {peakY - startY:F4})",
                PositionTolerance);

            return ok;
        }

        /// <summary>
        /// B44 — the fluid query refuses to run once its native data is gone, rather than reading freed memory.
        /// <para>
        /// <c>World.OnDestroy</c> disposes the job palette and the fluid templates without nulling either
        /// field, and a body's <c>FixedUpdate</c> can outlive that during a world unload. The guard that was
        /// supposed to catch this tested <c>NativeArray.IsCreated</c>, which <b>stays true after
        /// disposal</b> — the arrays are disposed through hoisted copies, so each field keeps its original
        /// pointer. The guard therefore never fired, and the next read would have thrown
        /// <see cref="System.ObjectDisposedException"/> in the editor and been undefined under IL2CPP.
        /// </para>
        /// <para>
        /// The in-fluid assertion first is not decoration: without it, "returns no fluid after teardown"
        /// is satisfied by a fixture that never had any fluid to begin with.
        /// </para>
        /// </summary>
        /// <returns>True when the query reports fluid, then reports none after teardown, without throwing.</returns>
        private static bool FluidContactSurvivesTeardown()
        {
            using PhysicsTestWorld world = NewFixture();
            SeedPool(world, FLUID_Y, FLUID_SOURCE_LEVEL);
            world.PlaceEntity(new Vector3(FLUID_X + 0.5f, FLUID_Y, FLUID_Z + 0.5f));

            bool ok = Expect(world.ResolveFluidContact().InFluid,
                "the body must be in fluid before teardown, or the assertion below proves nothing");

            world.DisposeJobDataEarly();

            FluidContact afterTeardown;
            try
            {
                afterTeardown = world.ResolveFluidContact();
            }
            catch (System.Exception e)
            {
                return Expect(false,
                    $"the fluid query threw after teardown ({e.GetType().Name}) — it must detect the " +
                    "disposed native data and return no contact");
            }

            ok &= Expect(!afterTeardown.InFluid,
                $"after teardown the query must report no fluid, got {afterTeardown.Type} at " +
                $"{afterTeardown.SubmergedFraction:F4} submersion");

            return ok;
        }

        /// <summary>
        /// Seeds a square fluid pool wide enough to contain the body's whole AABB plus the one-cell ring the
        /// flow derivative reads.
        /// </summary>
        /// <param name="world">The fixture to seed.</param>
        /// <param name="y">Cell Y of the layer.</param>
        /// <param name="level">Raw fluid level to write, falling flag included.</param>
        private static void SeedPool(PhysicsTestWorld world, int y, byte level)
        {
            world.FillLayer(GROUND_Y, Id.Ground);

            for (int dx = -FLUID_POOL_RADIUS; dx <= FLUID_POOL_RADIUS; dx++)
            for (int dz = -FLUID_POOL_RADIUS; dz <= FLUID_POOL_RADIUS; dz++)
                world.SetBlock(FLUID_X + dx, y, FLUID_Z + dz, Id.Fluid, level);
        }
    }
}
