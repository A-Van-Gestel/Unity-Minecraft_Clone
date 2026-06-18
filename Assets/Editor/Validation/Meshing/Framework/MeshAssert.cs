using System;
using System.Text;
using Data;
using Unity.Collections;
using UnityEngine;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// Assertion helpers for the meshing validation suite. Every method returns a pass/fail bool and
    /// logs a single <c>[PASS]</c>/<c>[FAIL]</c> line; failures include bounded per-element diffs so a
    /// regression is debuggable straight from the Unity console (matching the lighting suite style).
    /// </summary>
    public static class MeshAssert
    {
        /// <summary>Max position delta (block units) tolerated between engine and oracle vertices.</summary>
        public const float VertexEpsilon = 1e-4f;

        /// <summary>Max number of element diffs printed before truncation.</summary>
        private const int MAX_DIFFS = 8;

        /// <summary>
        /// Asserts the four vertices and the normal of one emitted quad match the oracle's expectation.
        /// </summary>
        /// <param name="label">Scenario/quad label for logging.</param>
        /// <param name="verts">Engine vertex list.</param>
        /// <param name="normals">Engine normal list.</param>
        /// <param name="startVert">Index of the first of the quad's 4 vertices.</param>
        /// <param name="expected">Oracle vertex positions (length 4).</param>
        /// <param name="expectedNormal">Oracle face normal.</param>
        public static bool QuadMatchesOracle(string label, NativeList<Vector3> verts, NativeList<Vector3> normals,
            int startVert, Vector3[] expected, Vector3 expectedNormal)
        {
            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            if (startVert + 4 > verts.Length)
            {
                Debug.LogError($"[FAIL] {label}: expected 4 vertices at index {startVert} but list has only {verts.Length}.");
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                Vector3 actual = verts[startVert + i];
                if (Vector3.Distance(actual, expected[i]) > VertexEpsilon && diffCount < MAX_DIFFS)
                {
                    diffs.AppendLine($"    vert[{i}] expected {Fmt(expected[i])} actual {Fmt(actual)} (Δ={Vector3.Distance(actual, expected[i]):G4})");
                    diffCount++;
                }

                Vector3 actualNormal = normals[startVert + i];
                if (Vector3.Distance(actualNormal, expectedNormal) > VertexEpsilon && diffCount < MAX_DIFFS)
                {
                    diffs.AppendLine($"    normal[{i}] expected {Fmt(expectedNormal)} actual {Fmt(actualNormal)}");
                    diffCount++;
                }
            }

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} vertex/normal mismatch(es)\n{diffs}");
            return false;
        }

        /// <summary>
        /// MH-1 — asserts every emitted vertex lies within the axis-aligned box
        /// [<paramref name="min"/>, <paramref name="max"/>] (a section's unit-cell-derived bounds). This
        /// proves the premise behind MR-4 — replacing the per-section <c>RecalculateBounds()</c> with a
        /// constant, section-sized <c>Bounds</c> — namely that emitted geometry never spills outside the
        /// section cell it belongs to. A regression that emitted a vertex beyond the cell would make the
        /// proposed constant bounds wrongly cull visible geometry.
        /// </summary>
        /// <param name="label">Scenario label for logging.</param>
        /// <param name="o">Meshing job output to inspect.</param>
        /// <param name="min">Inclusive lower corner of the allowed box (block units).</param>
        /// <param name="max">Inclusive upper corner of the allowed box (block units).</param>
        public static bool BoundsWithin(string label, MeshDataJobOutput o, Vector3 min, Vector3 max)
        {
            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            for (int i = 0; i < o.Vertices.Length && diffCount < MAX_DIFFS; i++)
            {
                Vector3 v = o.Vertices[i];
                if (!IsWithin(v, min, max, VertexEpsilon))
                {
                    diffs.AppendLine($"    vert[{i}] {Fmt(v)} outside [{Fmt(min)} .. {Fmt(max)}]");
                    diffCount++;
                }
            }

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}: all {o.Vertices.Length} vertices within [{Fmt(min)} .. {Fmt(max)}].");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} vertex/vertices outside bounds\n{diffs}");
            return false;
        }

        /// <summary>
        /// Per-vertex containment test (the single source of truth shared by <see cref="BoundsWithin"/> on job
        /// output and <see cref="RendererAssert.BoundsContainAll"/> on renderer mesh bounds): true iff
        /// <paramref name="v"/> lies within the inclusive box [<paramref name="min"/>, <paramref name="max"/>]
        /// expanded by <paramref name="epsilon"/> on each axis. Keeping one copy means the epsilon and the
        /// inclusive-boundary convention can never silently diverge between the two callers.
        /// </summary>
        /// <param name="v">The point to test.</param>
        /// <param name="min">Inclusive lower corner.</param>
        /// <param name="max">Inclusive upper corner.</param>
        /// <param name="epsilon">Per-axis tolerance added outside the box.</param>
        public static bool IsWithin(Vector3 v, Vector3 min, Vector3 max, float epsilon)
        {
            return v.x >= min.x - epsilon && v.x <= max.x + epsilon
                                          && v.y >= min.y - epsilon && v.y <= max.y + epsilon
                                          && v.z >= min.z - epsilon && v.z <= max.z + epsilon;
        }

        /// <summary>
        /// MH-4 — asserts the four UVs of one emitted quad match the oracle's expected atlas coordinates
        /// (per-vertex, within <see cref="VertexEpsilon"/>). Pairs with
        /// <see cref="MeshOracle.ExpectedFaceUVs"/> to pin that a face shows the correct texture's atlas
        /// cell — the encoding finding MR-2 must preserve.
        /// </summary>
        /// <param name="label">Scenario/quad label for logging.</param>
        /// <param name="uvs">Engine UV stream.</param>
        /// <param name="startVert">Index of the first of the quad's 4 vertices.</param>
        /// <param name="expected">Oracle UVs (length 4).</param>
        public static bool UVsMatch(string label, NativeList<Vector4> uvs, int startVert, Vector4[] expected)
        {
            if (startVert + 4 > uvs.Length)
            {
                Debug.LogError($"[FAIL] {label}: expected 4 UVs at index {startVert} but list has only {uvs.Length}.");
                return false;
            }

            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            for (int i = 0; i < 4; i++)
            {
                Vector4 a = uvs[startVert + i];
                Vector4 e = expected[i];
                if ((Mathf.Abs(a.x - e.x) > VertexEpsilon || Mathf.Abs(a.y - e.y) > VertexEpsilon ||
                     Mathf.Abs(a.z - e.z) > VertexEpsilon || Mathf.Abs(a.w - e.w) > VertexEpsilon)
                    && diffCount < MAX_DIFFS)
                {
                    diffs.AppendLine($"    uv[{i}] expected ({e.x:F4},{e.y:F4},{e.z:F4},{e.w:F4}) actual ({a.x:F4},{a.y:F4},{a.z:F4},{a.w:F4})");
                    diffCount++;
                }
            }

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} UV mismatch(es)\n{diffs}");
            return false;
        }

        /// <summary>
        /// MH-5 (assertion a) — asserts the post-processed output is the chunk-space geometry rewritten to
        /// section-space: per emitting section <c>s</c> (read from <see cref="MeshDataJobOutput.SectionStats"/>),
        /// every vertex equals its chunk-space original with <c>y</c> shifted down by
        /// <c>s * <paramref name="sectionHeight"/></c> and <c>x</c>/<c>z</c> unchanged. The section a vertex
        /// belongs to is derived from <c>SectionStats</c> (the same ranges the job uses, MH-9/B9-guarded), so
        /// this re-derives only the trivial offset arithmetic — not the engine's transform code.
        /// </summary>
        /// <param name="label">Scenario label for logging.</param>
        /// <param name="sectionSpace">The post-processed (section-space) output to inspect.</param>
        /// <param name="chunkSpaceVerts">The chunk-space vertices captured from a gen-only run (same fixture).</param>
        /// <param name="sectionHeight">The section height the post-process subtracts per section index.</param>
        public static bool SectionSpaceVertices(string label, MeshDataJobOutput sectionSpace,
            Vector3[] chunkSpaceVerts, int sectionHeight)
        {
            if (sectionSpace.Vertices.Length != chunkSpaceVerts.Length)
            {
                Debug.LogError($"[FAIL] {label}: section-space vertex count {sectionSpace.Vertices.Length} != chunk-space {chunkSpaceVerts.Length}.");
                return false;
            }

            if (!sectionSpace.SectionStats.IsCreated)
            {
                Debug.LogError($"[FAIL] {label}: SectionStats not created (cannot map vertices to sections).");
                return false;
            }

            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            for (int s = 0; s < sectionSpace.SectionStats.Length && diffCount < MAX_DIFFS; s++)
            {
                MeshSectionStats stats = sectionSpace.SectionStats[s];
                if (stats.VertexCount == 0) continue;

                float yOffset = s * sectionHeight;
                for (int v = 0; v < stats.VertexCount && diffCount < MAX_DIFFS; v++)
                {
                    int idx = stats.VertexStartIndex + v;
                    Vector3 expected = new Vector3(chunkSpaceVerts[idx].x, chunkSpaceVerts[idx].y - yOffset, chunkSpaceVerts[idx].z);
                    Vector3 actual = sectionSpace.Vertices[idx];
                    if (Vector3.Distance(actual, expected) > VertexEpsilon)
                    {
                        diffs.AppendLine($"    section {s} vert[{idx}] expected {Fmt(expected)} actual {Fmt(actual)} (chunk-space {Fmt(chunkSpaceVerts[idx])}, yOffset {yOffset})");
                        diffCount++;
                    }
                }
            }

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}: all {sectionSpace.Vertices.Length} vertices match chunk-space − section origin.");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} section-space coordinate mismatch(es)\n{diffs}");
            return false;
        }

        /// <summary>
        /// MH-5 (assertion b) — asserts <see cref="MeshDataJobOutput.InterleavedStream3"/> is the
        /// element-wise interleave of <c>Normals</c> + <c>LightData</c> the post-process builds: same length
        /// as the vertex stream, and each entry's <c>Normal</c>/<c>LightData</c> equal the source streams at
        /// that index. (Normals/LightData are read-only inputs to the post job, so they are still the truth.)
        /// </summary>
        /// <param name="label">Scenario label for logging.</param>
        /// <param name="o">The post-processed output to inspect.</param>
        public static bool InterleavedMatches(string label, MeshDataJobOutput o)
        {
            int n = o.Vertices.Length;
            if (o.InterleavedStream3.Length != n)
            {
                Debug.LogError($"[FAIL] {label}: InterleavedStream3 length {o.InterleavedStream3.Length} != vertices {n}.");
                return false;
            }

            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            for (int i = 0; i < n && diffCount < MAX_DIFFS; i++)
            {
                NormalLightVertex packed = o.InterleavedStream3[i];
                Vector3 normal = o.Normals[i];
                Color32 light = o.LightData[i];
                bool normalOk = Vector3.Distance(packed.Normal, normal) <= VertexEpsilon;
                bool lightOk = packed.LightData.r == light.r && packed.LightData.g == light.g
                                                             && packed.LightData.b == light.b && packed.LightData.a == light.a;
                if (!normalOk || !lightOk)
                {
                    diffs.AppendLine($"    [{i}] interleaved (N {Fmt(packed.Normal)}, L ({packed.LightData.r},{packed.LightData.g},{packed.LightData.b},{packed.LightData.a})) != source (N {Fmt(normal)}, L ({light.r},{light.g},{light.b},{light.a}))");
                    diffCount++;
                }
            }

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}: InterleavedStream3 == interleave(Normals, LightData) over {n} verts.");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} interleave mismatch(es)\n{diffs}");
            return false;
        }

        /// <summary>
        /// MH-5 (assertion c) — asserts two outputs' <see cref="MeshDataJobOutput.InterleavedStream3"/> are
        /// element-for-element identical (<c>Normal</c> within <see cref="VertexEpsilon"/>, <c>LightData</c>
        /// exact). Pairs with <see cref="OutputsEqual"/> to make the chained-vs-separate post-process guard
        /// total: <c>OutputsEqual</c> covers the base streams, this covers the interleaved stream it omits.
        /// </summary>
        public static bool InterleavedStreamsEqual(string label, MeshDataJobOutput a, MeshDataJobOutput b)
        {
            if (a.InterleavedStream3.Length != b.InterleavedStream3.Length)
            {
                Debug.LogError($"[FAIL] {label}: InterleavedStream3 length {a.InterleavedStream3.Length} != {b.InterleavedStream3.Length}.");
                return false;
            }

            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            for (int i = 0; i < a.InterleavedStream3.Length && diffCount < MAX_DIFFS; i++)
            {
                NormalLightVertex x = a.InterleavedStream3[i], y = b.InterleavedStream3[i];
                bool normalOk = Vector3.Distance(x.Normal, y.Normal) <= VertexEpsilon;
                bool lightOk = x.LightData.r == y.LightData.r && x.LightData.g == y.LightData.g
                                                              && x.LightData.b == y.LightData.b && x.LightData.a == y.LightData.a;
                if (!normalOk || !lightOk)
                {
                    diffs.AppendLine($"    [{i}] (N {Fmt(x.Normal)}, L ({x.LightData.r},{x.LightData.g},{x.LightData.b},{x.LightData.a})) vs (N {Fmt(y.Normal)}, L ({y.LightData.r},{y.LightData.g},{y.LightData.b},{y.LightData.a}))");
                    diffCount++;
                }
            }

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}: InterleavedStream3 identical across runs ({a.InterleavedStream3.Length} verts).");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} InterleavedStream3 difference(s)\n{diffs}");
            return false;
        }

        /// <summary>
        /// MH-3 — asserts every emitted vertex carries the same expected smooth-light <see cref="Color32"/>
        /// in <see cref="MeshDataJobOutput.LightData"/> (exact per-channel). Pairs with
        /// <see cref="MeshOracle.ExpectedUniformCornerLight"/> for a uniform-light fixture: the oracle gives
        /// the single value all corners must equal, this checks the whole stream against it. A zeroed or
        /// saturated read, a wrong UNorm8 scale, or a channel swap all fail.
        /// </summary>
        /// <param name="label">Scenario label for logging.</param>
        /// <param name="o">The meshing output to inspect.</param>
        /// <param name="expected">The light value every vertex must carry.</param>
        public static bool LightDataMatches(string label, MeshDataJobOutput o, Color32 expected)
        {
            if (o.LightData.Length != o.Vertices.Length)
            {
                Debug.LogError($"[FAIL] {label}: LightData length {o.LightData.Length} != vertices {o.Vertices.Length}.");
                return false;
            }

            if (o.LightData.Length == 0)
            {
                Debug.LogError($"[FAIL] {label}: no LightData to check (empty output).");
                return false;
            }

            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            for (int i = 0; i < o.LightData.Length && diffCount < MAX_DIFFS; i++)
            {
                Color32 a = o.LightData[i];
                if (a.r != expected.r || a.g != expected.g || a.b != expected.b || a.a != expected.a)
                {
                    diffs.AppendLine($"    LightData[{i}] expected ({expected.r},{expected.g},{expected.b},{expected.a}) actual ({a.r},{a.g},{a.b},{a.a})");
                    diffCount++;
                }
            }

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}: all {o.LightData.Length} verts carry ({expected.r},{expected.g},{expected.b},{expected.a}).");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} LightData mismatch(es)\n{diffs}");
            return false;
        }

        /// <summary>Asserts the output has exactly <paramref name="expected"/> vertices.</summary>
        public static bool VertexCount(string label, MeshDataJobOutput o, int expected)
        {
            if (o.Vertices.Length == expected)
            {
                Debug.Log($"[PASS] {label}: {expected} vertices.");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: expected {expected} vertices, got {o.Vertices.Length}.");
            return false;
        }

        /// <summary>
        /// Asserts the per-vertex streams are length-consistent and every triangle index is a valid,
        /// well-formed (multiple-of-3) reference into the vertex array.
        /// </summary>
        public static bool StructuralInvariants(string label, MeshDataJobOutput o)
        {
            StringBuilder problems = new StringBuilder();
            int n = o.Vertices.Length;

            if (o.Uvs.Length != n) problems.AppendLine($"    Uvs length {o.Uvs.Length} != vertices {n}");
            if (o.Colors.Length != n) problems.AppendLine($"    Colors length {o.Colors.Length} != vertices {n}");
            if (o.Normals.Length != n) problems.AppendLine($"    Normals length {o.Normals.Length} != vertices {n}");
            if (o.LightData.Length != n) problems.AppendLine($"    LightData length {o.LightData.Length} != vertices {n}");

            CheckTriangleList(problems, "OpaqueTris", o.Triangles, n);
            CheckTriangleList(problems, "TransparentTris", o.TransparentTriangles, n);
            CheckTriangleList(problems, "FluidTris", o.FluidTriangles, n);

            // MH-9: the per-section ranges the job writes into SectionStats must tile each stream
            // exactly — walking sections in order, every geometry-emitting section's [start, start+count)
            // range must be contiguous, non-overlapping, and the ranges must sum to the stream's length.
            // The global checks above pass even if a refactor mis-partitions sections (wrong start/count);
            // this catches that. Sections that emit nothing carry no meaningful start index (a skipped
            // section is written as `default`, i.e. start 0 / count 0) and are correctly ignored.
            CheckSectionTiling(problems, "Vertices", o.SectionStats,
                static s => s.VertexStartIndex, static s => s.VertexCount, n);
            CheckSectionTiling(problems, "OpaqueTris", o.SectionStats,
                static s => s.OpaqueTriStartIndex, static s => s.OpaqueTriCount, o.Triangles.Length);
            CheckSectionTiling(problems, "TransparentTris", o.SectionStats,
                static s => s.TransparentTriStartIndex, static s => s.TransparentTriCount, o.TransparentTriangles.Length);
            CheckSectionTiling(problems, "FluidTris", o.SectionStats,
                static s => s.FluidTriStartIndex, static s => s.FluidTriCount, o.FluidTriangles.Length);

            if (problems.Length == 0)
            {
                Debug.Log($"[PASS] {label}: structural invariants hold ({n} verts).");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: structural invariant violations\n{problems}");
            return false;
        }

        /// <summary>
        /// Asserts two outputs are element-for-element identical across every stream (determinism guard).
        /// </summary>
        public static bool OutputsEqual(string label, MeshDataJobOutput a, MeshDataJobOutput b)
        {
            StringBuilder diffs = new StringBuilder();
            int diffCount = 0;

            CompareVec3(diffs, ref diffCount, "Vertices", a.Vertices, b.Vertices);
            CompareInt(diffs, ref diffCount, "Triangles", a.Triangles, b.Triangles);
            CompareInt(diffs, ref diffCount, "TransparentTriangles", a.TransparentTriangles, b.TransparentTriangles);
            CompareInt(diffs, ref diffCount, "FluidTriangles", a.FluidTriangles, b.FluidTriangles);
            CompareVec3(diffs, ref diffCount, "Normals", a.Normals, b.Normals);
            // UVs, vertex colors, and packed light are exactly the streams an uninitialized hoisted
            // scratch buffer would make nondeterministic, so a determinism guard must cover them too.
            CompareVec4(diffs, ref diffCount, "Uvs", a.Uvs, b.Uvs);
            CompareColor(diffs, ref diffCount, "Colors", a.Colors, b.Colors);
            CompareColor32(diffs, ref diffCount, "LightData", a.LightData, b.LightData);

            if (diffCount == 0)
            {
                Debug.Log($"[PASS] {label}: outputs identical across runs.");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {diffCount} difference(s) between runs\n{diffs}");
            return false;
        }

        /// <summary>Generic boolean assertion with a descriptive detail line.</summary>
        public static bool IsTrue(string label, bool condition, string detail)
        {
            if (condition)
            {
                Debug.Log($"[PASS] {label}: {detail}");
                return true;
            }

            Debug.LogError($"[FAIL] {label}: {detail}");
            return false;
        }

        /// <summary>
        /// MH-9 — asserts the per-section ranges of one stream tile it exactly. Walking the sections in
        /// order, every section that emitted geometry (<paramref name="count"/> &gt; 0) must start where
        /// the previous emitting section ended (no gap, no overlap), and the ranges must sum to
        /// <paramref name="total"/>. Sections with a zero count carry no meaningful start index (a skipped
        /// section is written as <c>default</c>), so they are skipped rather than checked.
        /// </summary>
        /// <param name="problems">Accumulator for violation messages.</param>
        /// <param name="name">Stream name for diagnostics.</param>
        /// <param name="stats">Per-section start/count ranges written by the meshing job.</param>
        /// <param name="start">Selector for this stream's per-section start index.</param>
        /// <param name="count">Selector for this stream's per-section element count.</param>
        /// <param name="total">Expected total element count (the stream's length).</param>
        private static void CheckSectionTiling(StringBuilder problems, string name,
            NativeArray<MeshSectionStats> stats, Func<MeshSectionStats, int> start,
            Func<MeshSectionStats, int> count, int total)
        {
            if (!stats.IsCreated)
            {
                problems.AppendLine($"    SectionStats not created (cannot validate {name} tiling)");
                return;
            }

            int running = 0;
            for (int s = 0; s < stats.Length; s++)
            {
                int c = count(stats[s]);
                if (c < 0)
                {
                    problems.AppendLine($"    {name} section {s} has negative count {c}");
                    return;
                }

                if (c == 0) continue; // skipped/empty section: start index is meaningless

                int st = start(stats[s]);
                if (st != running)
                {
                    problems.AppendLine($"    {name} section {s} start {st} != expected {running} (non-contiguous tiling)");
                    return;
                }

                running += c;
            }

            if (running != total)
                problems.AppendLine($"    {name} section ranges sum to {running} but stream length is {total}");
        }

        private static void CheckTriangleList(StringBuilder problems, string name, NativeList<int> tris, int vertCount)
        {
            if (tris.Length % 3 != 0)
                problems.AppendLine($"    {name} length {tris.Length} is not a multiple of 3");

            for (int i = 0; i < tris.Length; i++)
            {
                if (tris[i] < 0 || tris[i] >= vertCount)
                {
                    problems.AppendLine($"    {name}[{i}] = {tris[i]} out of range [0,{vertCount})");
                    break; // one example is enough
                }
            }
        }

        private static void CompareVec3(StringBuilder diffs, ref int diffCount, string name,
            NativeList<Vector3> a, NativeList<Vector3> b)
        {
            if (a.Length != b.Length)
            {
                if (diffCount < MAX_DIFFS) diffs.AppendLine($"    {name} length {a.Length} != {b.Length}");
                diffCount++;
                return;
            }

            for (int i = 0; i < a.Length && diffCount < MAX_DIFFS; i++)
            {
                if (Vector3.Distance(a[i], b[i]) > VertexEpsilon)
                {
                    diffs.AppendLine($"    {name}[{i}] {Fmt(a[i])} vs {Fmt(b[i])}");
                    diffCount++;
                }
            }
        }

        private static void CompareInt(StringBuilder diffs, ref int diffCount, string name,
            NativeList<int> a, NativeList<int> b)
        {
            if (a.Length != b.Length)
            {
                if (diffCount < MAX_DIFFS) diffs.AppendLine($"    {name} length {a.Length} != {b.Length}");
                diffCount++;
                return;
            }

            for (int i = 0; i < a.Length && diffCount < MAX_DIFFS; i++)
            {
                if (a[i] != b[i])
                {
                    diffs.AppendLine($"    {name}[{i}] {a[i]} vs {b[i]}");
                    diffCount++;
                }
            }
        }

        private static void CompareVec4(StringBuilder diffs, ref int diffCount, string name,
            NativeList<Vector4> a, NativeList<Vector4> b)
        {
            if (a.Length != b.Length)
            {
                if (diffCount < MAX_DIFFS) diffs.AppendLine($"    {name} length {a.Length} != {b.Length}");
                diffCount++;
                return;
            }

            for (int i = 0; i < a.Length && diffCount < MAX_DIFFS; i++)
            {
                // Exact comparison: a determinism guard must catch any bit-level divergence.
                Vector4 x = a[i], y = b[i];
                // ReSharper disable CompareOfFloatsByEqualityOperator
                if (x.x != y.x || x.y != y.y || x.z != y.z || x.w != y.w)
                    // ReSharper restore CompareOfFloatsByEqualityOperator
                {
                    diffs.AppendLine($"    {name}[{i}] ({x.x:F4},{x.y:F4},{x.z:F4},{x.w:F4}) vs ({y.x:F4},{y.y:F4},{y.z:F4},{y.w:F4})");
                    diffCount++;
                }
            }
        }

        private static void CompareColor(StringBuilder diffs, ref int diffCount, string name,
            NativeList<Color> a, NativeList<Color> b)
        {
            if (a.Length != b.Length)
            {
                if (diffCount < MAX_DIFFS) diffs.AppendLine($"    {name} length {a.Length} != {b.Length}");
                diffCount++;
                return;
            }

            for (int i = 0; i < a.Length && diffCount < MAX_DIFFS; i++)
            {
                // Exact comparison: a determinism guard must catch any bit-level divergence.
                Color x = a[i], y = b[i];
                // ReSharper disable CompareOfFloatsByEqualityOperator
                if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a)
                    // ReSharper restore CompareOfFloatsByEqualityOperator
                {
                    diffs.AppendLine($"    {name}[{i}] ({x.r:F4},{x.g:F4},{x.b:F4},{x.a:F4}) vs ({y.r:F4},{y.g:F4},{y.b:F4},{y.a:F4})");
                    diffCount++;
                }
            }
        }

        private static void CompareColor32(StringBuilder diffs, ref int diffCount, string name,
            NativeList<Color32> a, NativeList<Color32> b)
        {
            if (a.Length != b.Length)
            {
                if (diffCount < MAX_DIFFS) diffs.AppendLine($"    {name} length {a.Length} != {b.Length}");
                diffCount++;
                return;
            }

            for (int i = 0; i < a.Length && diffCount < MAX_DIFFS; i++)
            {
                Color32 x = a[i], y = b[i];
                if (x.r != y.r || x.g != y.g || x.b != y.b || x.a != y.a)
                {
                    diffs.AppendLine($"    {name}[{i}] ({x.r},{x.g},{x.b},{x.a}) vs ({y.r},{y.g},{y.b},{y.a})");
                    diffCount++;
                }
            }
        }

        private static string Fmt(Vector3 v) => $"({v.x:F4}, {v.y:F4}, {v.z:F4})";
    }
}
