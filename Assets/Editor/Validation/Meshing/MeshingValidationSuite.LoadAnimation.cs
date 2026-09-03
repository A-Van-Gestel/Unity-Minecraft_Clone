using System.Collections.Generic;
using Editor.Validation.Meshing.Framework;
using UnityEngine;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// Chunk load-animation baselines (B34–B36) — the guard for the <c>enableChunkLoadAnimations</c>
    /// toggle regression documented in Documentation/Bugs/_FIXED_BUGS.md.
    /// <list type="bullet">
    /// <item><b>B34</b> — a chunk built while animations were OFF still animates once the setting is turned
    /// on mid-session (the regression: the component used to be creatable only in the constructor, so
    /// pooled chunks could never acquire one).</item>
    /// <item><b>B35</b> — a component added mid-session is <i>seeded</i> with this chunk's position, so it
    /// rises into place instead of lerping toward the world origin.</item>
    /// <item><b>B36</b> — controls: ON at construction still pre-adds, and OFF still snaps and creates
    /// nothing, so B34/B35 cannot pass vacuously.</item>
    /// </list>
    /// The unit is <see cref="Chunk"/>, so these use <see cref="ChunkLoadAnimationTestFixture"/> rather than
    /// the meshing-job harness or the <see cref="SectionRenderer"/> fixture. They pair with B31/B32, which
    /// pin <i>when</i> the completion pass calls <c>TriggerLoadAnimation</c>; these pin what it then does.
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        // A resting position that is distinctive on all three axes and far from the origin, so "parked
        // underground relative to this chunk" and "left at Vector3.zero" can never be confused.
        private static readonly Vector3 s_animTestRestingPosition = new Vector3(320f, 64f, -192f);

        /// <summary>Registers the chunk load-animation baselines (called from <c>Execute</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddLoadAnimationScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B34: enabling chunk load animations mid-session animates a chunk built while they were off (2026-04-09 toggle regression)", B34_ToggleOnMidSessionAnimates));
            scenarios.Add(new Scenario("B35: an animation component added mid-session is seeded with the chunk's own position, not the world origin", B35_MidSessionAddIsSeeded));
            scenarios.Add(new Scenario("B36: controls — animations on at construction pre-add the component; off snaps without creating one", B36_ConstructionTimeControls));
        }

        /// <summary>
        /// B34 — the regression itself. Build a chunk with animations OFF (so the constructor adds nothing),
        /// turn the setting ON, then trigger: the chunk must acquire an enabled animation component.
        /// Prove-red: restrict component creation to the constructor again → the component never appears.
        /// </summary>
        private static bool B34_ToggleOnMidSessionAnimates()
        {
            using ChunkLoadAnimationTestFixture fixture = new ChunkLoadAnimationTestFixture(animationsEnabled: false);
            bool ok = true;

            // Precondition — stated as its own assertion so that a fixture change which starts pre-adding the
            // component turns into an explicit failure instead of quietly making the rest of B34 vacuous.
            ok &= MeshAssert.IsTrue(
                "B34.1 precondition: a chunk built with animations off has no animation component",
                !fixture.HasAnimationComponent,
                !fixture.HasAnimationComponent
                    ? "no ChunkLoadAnimation on the fresh chunk — the regression's starting state"
                    : "the chunk already had an animation component; B34 cannot observe the toggle from here");

            fixture.Chunk.UnityPosition = s_animTestRestingPosition;
            fixture.AnimationsEnabled = true;
            fixture.Chunk.TriggerLoadAnimation();

            ok &= MeshAssert.IsTrue(
                "B34.2 the chunk acquires an enabled animation component after the toggle",
                fixture.AnimationEnabled,
                fixture.AnimationEnabled
                    ? "ChunkLoadAnimation present and enabled — the mid-session toggle took effect"
                    : $"expected an enabled ChunkLoadAnimation; component present={fixture.HasAnimationComponent.ToString()}. " +
                      "This is the 2026-04-09 regression: the component is only creatable in the constructor.");

            return ok;
        }

        /// <summary>
        /// B35 — the seeding trap. A component added mid-session has no target position of its own, so the
        /// chunk must be parked underground relative to <see cref="Chunk.UnityPosition"/>. Prove-red: drop the
        /// <c>ResetToUnderground</c> seed from <c>Chunk.EnsureLoadAnimation</c> → the chunk sits at the origin
        /// and lerps there, while B34's existence check still passes.
        /// </summary>
        private static bool B35_MidSessionAddIsSeeded()
        {
            using ChunkLoadAnimationTestFixture fixture = new ChunkLoadAnimationTestFixture(animationsEnabled: false);

            fixture.Chunk.UnityPosition = s_animTestRestingPosition;
            fixture.AnimationsEnabled = true;
            fixture.Chunk.TriggerLoadAnimation();

            Vector3 expected = ChunkLoadAnimationTestFixture.UndergroundOf(s_animTestRestingPosition);
            Vector3 actual = fixture.Position;
            bool seeded = Vector3.Distance(actual, expected) < 0.001f;

            return MeshAssert.IsTrue(
                "B35 a mid-session animation add parks the chunk underground relative to its own position",
                seeded,
                seeded
                    ? $"parked at {actual.ToString()} — one chunk height below its resting position, ready to rise"
                    : $"expected {expected.ToString()}, got {actual.ToString()}. The world origin means the new " +
                      "component was never seeded with a target, so the chunk would animate toward world zero; " +
                      "the resting position means no animation was set up at all.");
        }

        /// <summary>
        /// B36 — the two construction-time controls, so B34/B35 cannot pass for the wrong reason: animations ON
        /// pre-adds in the constructor (the path that always worked), and animations OFF snaps to the resting
        /// position without creating anything.
        /// </summary>
        private static bool B36_ConstructionTimeControls()
        {
            bool ok = true;

            using (ChunkLoadAnimationTestFixture on = new ChunkLoadAnimationTestFixture(animationsEnabled: true))
            {
                ok &= MeshAssert.IsTrue(
                    "B36.1 animations on at construction pre-add the component",
                    on.HasAnimationComponent,
                    on.HasAnimationComponent
                        ? "ChunkLoadAnimation added by the constructor — the common path is unchanged"
                        : "no component after constructing with animations enabled");
            }

            using (ChunkLoadAnimationTestFixture off = new ChunkLoadAnimationTestFixture(animationsEnabled: false))
            {
                off.Chunk.UnityPosition = s_animTestRestingPosition;
                off.Chunk.TriggerLoadAnimation();

                bool snapped = Vector3.Distance(off.Position, s_animTestRestingPosition) < 0.001f;
                bool stayedClean = !off.HasAnimationComponent;

                ok &= MeshAssert.IsTrue(
                    "B36.2 animations off snap to the resting position and create no component",
                    snapped && stayedClean,
                    snapped && stayedClean
                        ? "snapped straight to the resting position, no ChunkLoadAnimation created"
                        : $"snapped={snapped.ToString()} (at {off.Position.ToString()}, expected {s_animTestRestingPosition.ToString()}), " +
                          $"componentCreated={(!stayedClean).ToString()}");
            }

            return ok;
        }
    }
}
