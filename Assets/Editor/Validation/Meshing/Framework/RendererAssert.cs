using System.Text;
using UnityEngine;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// MH-6 assertion helpers for the <see cref="SectionRenderer"/> apply-path fixture. Same
    /// <c>[PASS]</c>/<c>[FAIL]</c> logging contract as <see cref="MeshAssert"/>, but these inspect the
    /// rendered <see cref="MeshRenderer.sharedMaterials"/> and <see cref="Mesh.bounds"/> rather than a
    /// <see cref="Data.MeshDataJobOutput"/>.
    /// </summary>
    public static class RendererAssert
    {
        /// <summary>
        /// Asserts <paramref name="actual"/> equals <paramref name="expected"/> as an <b>ordered</b>
        /// sequence of materials by reference identity — the load-bearing MR-3 guard: the renderer must
        /// assign exactly the present submeshes' materials, in opaque → transparent → fluid order.
        /// </summary>
        /// <param name="label">Scenario label for logging.</param>
        /// <param name="actual">The renderer's current <c>sharedMaterials</c>.</param>
        /// <param name="expected">The expected ordered material array.</param>
        public static bool MaterialsEqual(string label, Material[] actual, Material[] expected)
        {
            if (actual == null)
            {
                Debug.LogError($"[FAIL] {label}: sharedMaterials is null (expected {expected.Length} material(s)).");
                return false;
            }

            if (actual.Length != expected.Length)
            {
                Debug.LogError($"[FAIL] {label}: expected {expected.Length} material(s) [{Names(expected)}], got {actual.Length} [{Names(actual)}].");
                return false;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (!ReferenceEquals(actual[i], expected[i]))
                {
                    Debug.LogError($"[FAIL] {label}: material[{i}] expected '{Name(expected[i])}' got '{Name(actual[i])}' (full expected [{Names(expected)}], actual [{Names(actual)}]).");
                    return false;
                }
            }

            Debug.Log($"[PASS] {label}: sharedMaterials == [{Names(expected)}].");
            return true;
        }

        /// <summary>
        /// Pure containment predicate (no logging) so a positive control can prove it actually observes an
        /// out-of-bounds vertex. Returns true iff every vertex lies within <paramref name="bounds"/>
        /// (inclusive, within <see cref="MeshAssert.VertexEpsilon"/>); on failure reports the first
        /// offending index via <paramref name="firstOutside"/>.
        /// </summary>
        public static bool BoundsContainAll(Bounds bounds, Vector3[] verts, out int firstOutside)
        {
            Vector3 min = bounds.min, max = bounds.max;

            for (int i = 0; i < verts.Length; i++)
            {
                // Single source of truth for the containment math — see MeshAssert.IsWithin.
                if (!MeshAssert.IsWithin(verts[i], min, max, MeshAssert.VertexEpsilon))
                {
                    firstOutside = i;
                    return false;
                }
            }

            firstOutside = -1;
            return true;
        }

        /// <summary>
        /// MH-6 — asserts the mesh <paramref name="bounds"/> CONTAIN every emitted vertex. A containment
        /// invariant (not tightness), so it survives MR-4 replacing <c>RecalculateBounds()</c> with a
        /// constant section-cell <see cref="Bounds"/> — that constant box must still contain the geometry.
        /// </summary>
        public static bool BoundsContainAllVerts(string label, Bounds bounds, Vector3[] verts)
        {
            if (BoundsContainAll(bounds, verts, out int firstOutside))
            {
                Debug.Log($"[PASS] {label}: bounds (center {bounds.center}, size {bounds.size}) contain all {verts.Length} vertices.");
                return true;
            }

            Vector3 bad = verts[firstOutside];
            Debug.LogError($"[FAIL] {label}: vert[{firstOutside}] ({bad.x:F4},{bad.y:F4},{bad.z:F4}) lies outside bounds [{bounds.min} .. {bounds.max}].");
            return false;
        }

        /// <summary>
        /// MR-4 postcondition — asserts the mesh <paramref name="actual"/> bounds EQUAL the expected
        /// constant section-cell box (center and size, within <see cref="MeshAssert.VertexEpsilon"/>).
        /// Stronger than <see cref="BoundsContainAllVerts"/>: it pins the exact constant produced once
        /// <c>RecalculateBounds()</c> is replaced, so it can only be baselined after MR-4 ships.
        /// </summary>
        public static bool BoundsEqual(string label, Bounds actual, Bounds expected)
        {
            const float eps = MeshAssert.VertexEpsilon;
            bool ok =
                Mathf.Abs(actual.center.x - expected.center.x) <= eps &&
                Mathf.Abs(actual.center.y - expected.center.y) <= eps &&
                Mathf.Abs(actual.center.z - expected.center.z) <= eps &&
                Mathf.Abs(actual.size.x - expected.size.x) <= eps &&
                Mathf.Abs(actual.size.y - expected.size.y) <= eps &&
                Mathf.Abs(actual.size.z - expected.size.z) <= eps;

            if (ok)
            {
                Debug.Log($"[PASS] {label}: bounds (center {actual.center}, size {actual.size}) == expected (center {expected.center}, size {expected.size}).");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: bounds (center {actual.center}, size {actual.size}) != expected (center {expected.center}, size {expected.size}).");
            return false;
        }

        private static string Name(Material m) => m == null ? "null" : m.name;

        private static string Names(Material[] mats)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < mats.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Name(mats[i]));
            }

            return sb.ToString();
        }
    }
}
