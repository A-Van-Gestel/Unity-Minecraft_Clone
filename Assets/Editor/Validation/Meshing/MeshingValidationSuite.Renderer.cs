using System.Collections.Generic;
using Editor.Validation.Meshing.Framework;
using UnityEngine;

namespace Editor.Validation.Meshing
{
    /// <summary>
    /// MH-6 — renderer apply-path baselines for <see cref="SectionRenderer.UpdateMeshNative"/>, the
    /// MonoBehaviour path the meshing-<i>job</i> suite never instantiates. Built test-first as the
    /// regression guards that survive the optimizations they protect (they target invariants, not the
    /// soon-to-change behavior):
    /// <list type="bullet">
    /// <item><b>B12</b> pins material-array selection per submesh-presence bitmask — the load-bearing
    /// guard for <b>MR-3</b> (material-combination caching).</item>
    /// <item><b>B13</b> pins the empty-section short-circuit (inactive GameObject, no material assignment).</item>
    /// <item><b>B14</b> pins that <see cref="Mesh.bounds"/> CONTAIN every emitted vertex — a containment
    /// invariant stable across <b>MR-4</b> (constant section-cell bounds); MH-1 already proved the geometry
    /// premise from job output.</item>
    /// </list>
    /// <para>
    /// <b>Optimization-time follow-ups (NOT baselinable pre-optimization — build alongside the change):</b>
    /// (1) "sharedMaterials is NOT reassigned when the present-submesh bitmask is unchanged" — MR-3's new
    /// postcondition once the 7 material combinations are cached; (2) "bounds == the constant section cell"
    /// — MR-4's new postcondition once <c>RecalculateBounds()</c> is replaced. Both assert the post-change
    /// behavior, so they belong in the same PR as MR-3 / MR-4 (see MESHING_VALIDATION_HARNESS_FIDELITY.md
    /// §3 MH-6).
    /// </para>
    /// </summary>
    public static partial class MeshingValidationSuite
    {
        /// <summary>A 2×2×2 spread of vertex positions used by the bounds scenario (no two share an axis value).</summary>
        private static readonly Vector3[] s_rendererProbeVerts =
        {
            new Vector3(1f, 2f, 3f),
            new Vector3(5f, 6f, 7f),
            new Vector3(2f, 8f, 4f),
            new Vector3(9f, 1f, 6f),
        };

        static partial void AddRendererScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("B12: renderer assigns the correct material combination per submesh-presence bitmask (MH-6 / MR-3 guard)", B12_MaterialCombinationPerBitmask));
            scenarios.Add(new Scenario("B13: empty section deactivates the renderer and assigns no materials (MH-6)", B13_EmptySectionNoAssignment));
            scenarios.Add(new Scenario("B14: mesh bounds contain every emitted vertex (MH-6 / MR-4 containment premise)", B14_BoundsContainVertices));
        }

        /// <summary>
        /// B12 — for all 7 non-empty submesh-presence combinations (bit0=opaque, bit1=transparent,
        /// bit2=fluid), <c>UpdateMeshNative</c> must set <c>sharedMaterials</c> to exactly the present
        /// submeshes' materials in opaque → transparent → fluid order. Positive control: the three stub
        /// materials are pairwise distinct AND two different bitmasks (opaque-only vs fluid-only) yield
        /// different arrays — so the ordered comparison cannot pass vacuously with aliased materials.
        /// </summary>
        private static bool B12_MaterialCombinationPerBitmask()
        {
            bool allPassed = true;

            using SectionRendererTestFixture fixture = new SectionRendererTestFixture();

            // Positive control (a): the three submesh materials are distinct identities.
            allPassed &= MeshAssert.IsTrue("B12 control: stub materials are pairwise distinct",
                !ReferenceEquals(fixture.OpaqueMaterial, fixture.TransparentMaterial)
                && !ReferenceEquals(fixture.OpaqueMaterial, fixture.LiquidMaterial)
                && !ReferenceEquals(fixture.TransparentMaterial, fixture.LiquidMaterial),
                "opaque, transparent, and fluid stub materials are three different objects");

            Material[] opaqueOnly = null;
            Material[] fluidOnly = null;

            // bit0=opaque, bit1=transparent, bit2=fluid; masks 1..7 are the non-empty combinations.
            for (int mask = 1; mask <= 7; mask++)
            {
                bool hasOpaque = (mask & 1) != 0;
                bool hasTransparent = (mask & 2) != 0;
                bool hasFluid = (mask & 4) != 0;

                // A present submesh gets 3 indices over a 4-vertex quad; absent submeshes get 0.
                fixture.RunUpdate(s_rendererProbeVerts,
                    opaqueCount: hasOpaque ? 3 : 0,
                    transparentCount: hasTransparent ? 3 : 0,
                    fluidCount: hasFluid ? 3 : 0);

                List<Material> expected = new List<Material>(3);
                if (hasOpaque) expected.Add(fixture.OpaqueMaterial);
                if (hasTransparent) expected.Add(fixture.TransparentMaterial);
                if (hasFluid) expected.Add(fixture.LiquidMaterial);

                Material[] actual = fixture.SharedMaterials;
                allPassed &= RendererAssert.MaterialsEqual($"B12 mask {mask} (o={hasOpaque},t={hasTransparent},f={hasFluid})",
                    actual, expected.ToArray());

                if (mask == 1) opaqueOnly = actual;
                if (mask == 4) fluidOnly = actual;
            }

            // Positive control (b): two different bitmasks produce genuinely different material arrays.
            allPassed &= MeshAssert.IsTrue("B12 control: opaque-only != fluid-only material array",
                opaqueOnly != null && fluidOnly != null
                && opaqueOnly.Length == 1 && fluidOnly.Length == 1
                && !ReferenceEquals(opaqueOnly[0], fluidOnly[0]),
                "bitmask 1 → [opaque] and bitmask 4 → [fluid] differ (selection is bitmask-sensitive)");

            return allPassed;
        }

        /// <summary>
        /// B13 — the empty-section short-circuit: when <c>vertexCount == 0</c>, the renderer must
        /// deactivate its GameObject and return WITHOUT touching <c>sharedMaterials</c>. Verified by
        /// priming the renderer with a recognizable single-material state, then issuing an empty update
        /// and asserting both the GameObject went inactive and the materials are unchanged (a regression
        /// that dropped the early-return would deactivate-less and overwrite materials with the zero-count
        /// result). Positive control: a fresh fixture given a non-empty update activates and DOES assign a
        /// (different) material — proving the "inactive + unchanged" assertion isn't vacuous.
        /// </summary>
        private static bool B13_EmptySectionNoAssignment()
        {
            bool allPassed = true;

            using SectionRendererTestFixture fixture = new SectionRendererTestFixture();

            // Prime with a recognizable, non-empty state: transparent-only → sharedMaterials == [transparent], active.
            fixture.RunUpdate(s_rendererProbeVerts, opaqueCount: 0, transparentCount: 3, fluidCount: 0);
            Material[] primed = fixture.SharedMaterials;
            allPassed &= RendererAssert.MaterialsEqual("B13 setup: primed transparent-only",
                primed, new[] { fixture.TransparentMaterial });
            allPassed &= MeshAssert.IsTrue("B13 setup: primed renderer is active",
                fixture.IsActive, "non-empty update activated the GameObject");

            // Empty update: must deactivate and must NOT reassign materials.
            fixture.RunUpdate(new Vector3[0], opaqueCount: 0, transparentCount: 0, fluidCount: 0);
            allPassed &= MeshAssert.IsTrue("B13: empty section deactivates the renderer",
                !fixture.IsActive, "vertexCount==0 set the GameObject inactive");
            allPassed &= RendererAssert.MaterialsEqual("B13: empty section left materials untouched",
                fixture.SharedMaterials, new[] { fixture.TransparentMaterial });

            // Positive control: a fresh fixture proves a non-empty update DOES activate + assign a material
            // (a different one), so the "inactive + unchanged" assertions above can genuinely observe change.
            using SectionRendererTestFixture control = new SectionRendererTestFixture();
            control.RunUpdate(s_rendererProbeVerts, opaqueCount: 3, transparentCount: 0, fluidCount: 0);
            allPassed &= MeshAssert.IsTrue("B13 control: non-empty update activates the renderer",
                control.IsActive, "opaque-only update activated the GameObject");
            allPassed &= RendererAssert.MaterialsEqual("B13 control: non-empty update assigns the opaque material",
                control.SharedMaterials, new[] { control.OpaqueMaterial });

            return allPassed;
        }

        /// <summary>
        /// B14 — after a non-empty update, the mesh's <see cref="Mesh.bounds"/> must CONTAIN every vertex
        /// fed to the renderer (containment, not tight equality — so it survives MR-4 swapping
        /// <c>RecalculateBounds()</c> for a constant section-cell box). Positive control / tripwire: the
        /// containment predicate returns FALSE for a deliberately-too-small box that excludes a known
        /// vertex (and TRUE for a generous box), proving it can actually observe an out-of-bounds vertex.
        /// </summary>
        private static bool B14_BoundsContainVertices()
        {
            bool allPassed = true;

            using SectionRendererTestFixture fixture = new SectionRendererTestFixture();
            fixture.RunUpdate(s_rendererProbeVerts, opaqueCount: 3, transparentCount: 0, fluidCount: 0);

            Bounds bounds = fixture.MeshBounds;
            allPassed &= RendererAssert.BoundsContainAllVerts("B14: mesh bounds contain all emitted vertices",
                bounds, s_rendererProbeVerts);

            // Positive control / tripwire: a box around only the first vertex (size 0.02) must NOT contain
            // the others — proving the containment check observes out-of-bounds vertices rather than always
            // passing — while a generous box around the same set must contain them all.
            Bounds tooSmall = new Bounds(s_rendererProbeVerts[0], new Vector3(0.02f, 0.02f, 0.02f));
            bool tripwireFires = !RendererAssert.BoundsContainAll(tooSmall, s_rendererProbeVerts, out int firstOutside);
            allPassed &= MeshAssert.IsTrue("B14 control: too-small bounds excludes a vertex (tripwire)",
                tripwireFires, $"a 0.02-unit box around vert[0] reports vert[{firstOutside}] outside");

            Bounds generous = new Bounds(new Vector3(5f, 5f, 5f), new Vector3(100f, 100f, 100f));
            bool generousContains = RendererAssert.BoundsContainAll(generous, s_rendererProbeVerts, out _);
            allPassed &= MeshAssert.IsTrue("B14 control: generous bounds contains all vertices",
                generousContains, "a 100-unit box reports every vertex inside (predicate isn't constant-false)");

            return allPassed;
        }
    }
}
