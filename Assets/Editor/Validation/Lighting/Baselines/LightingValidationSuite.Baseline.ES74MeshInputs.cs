using System.Collections.Generic;
using Data;
using Diagnostics;
using Editor.Validation.Lighting.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using Random = System.Random;
using Scenario = Editor.Validation.Framework.Scenario;

namespace Editor.Validation.Lighting
{
    /// <summary>
    /// Baselines for ES-7.4 (<c>ENGINE_SCALING_PERFORMANCE_ROADMAP.md</c>): <c>ChunkData</c> carries whether a
    /// mesh snapshot still matches the inputs the mesher reads, and which of its neighbors read a border column whose
    /// light just changed. B132 pins the direction-bit geometry (who reads which column); B133 and B134 pin that a
    /// merge or a light write reports exactly those border bits and no others; B135 pins the field's lifecycle
    /// (populate, consume, pool reset); B136 runs the whole chain in the harness. Self-registered via the
    /// <see cref="AddEs74MeshInputBaselineScenarios"/> hook.
    /// </summary>
    public static partial class LightingValidationSuite
    {
        // B132: the column census of a chunk's own grid, counted independently of Of.
        private const int B132_LAST_COLUMN = VoxelData.ChunkWidth - 1;
        private const int B132_CHUNK_COLUMNS = VoxelData.ChunkWidth * VoxelData.ChunkWidth;
        private const int B132_INTERIOR_COLUMNS = (VoxelData.ChunkWidth - 2) * (VoxelData.ChunkWidth - 2);
        private const int B132_BORDER_COLUMNS = B132_CHUNK_COLUMNS - B132_INTERIOR_COLUMNS;
        private const int B132_NEIGHBOR_ORIGIN_X = 7;
        private const int B132_NEIGHBOR_ORIGIN_Z = -3;

        // B133: the two case shapes the shared ES-7 fuzz cannot be relied on to produce, plus the observation
        // census that keeps its coverage assertions from passing vacuously.
        private const byte B133_FULL_SKY = 15;
        private const byte B133_DIM_SKY = 7;
        private const int B133_INTERIOR_X = 6;
        private const int B133_INTERIOR_Y = 40; // mid-section: away from the section seams the band cases cross
        private const int B133_INTERIOR_Z = 6;

        private const byte B133_UNCOMPACTED = 0xFF; // <c>ChunkData.UNIFORM_SKY_NONE</c>, internal to the runtime assembly

        private const int B133_SEEN_NONZERO_BORDER_MASK = 1;
        private const int B133_SEEN_INTERIOR_ONLY_CHANGE = 2;
        private const int B133_SEEN_ABSENT_SECTION_TO_UNIFORM_SKY = 4;

        // B134: every write lands in section 0, one voxel per case.
        private const int B134_Y = 5;
        private const int B134_LAST = VoxelData.ChunkWidth - 1;
        private const int B134_EDGE_MID = VoxelData.ChunkWidth / 2;
        private const byte B134_SKY = 9;
        private const byte B134_OTHER_SKY = 7;

        // B135: the border write that dirties the recycled chunk sits on the +X edge, so its bit is known.
        private const int B135_Y = 5;
        private const int B135_BORDER_X = VoxelData.ChunkWidth - 1;
        private const int B135_MID_Z = VoxelData.ChunkWidth / 2;
        private const byte B135_SKY = 9;

        // B136: a 3x3 grid of solid stone to y=24; the buried air cell sits in the center chunk's east border
        // column, mid-edge (not a corner), sixteen voxels below the surface so its sky level stays below full.
        private const int B136_GRID = 3;
        private const int B136_CENTER = 1;
        private const int B136_SOLID_TOP = 24;
        private const int B136_POCKET_Y = 8;
        private const int B136_POCKET_X = (B136_CENTER + 1) * VoxelData.ChunkWidth - 1;
        private const int B136_POCKET_Z = B136_CENTER * VoxelData.ChunkWidth + VoxelData.ChunkWidth / 2;
        private const byte B136_STAMP_SKY = 15;

        // B137: stop collecting once the detail would only repeat the same broken rule.
        private const int B137_MAX_FAILURES = 16;

        /// <summary>Registers the ES-7.4 mesh-input baselines (called from <c>AddBaselineScenarios</c>).</summary>
        /// <param name="scenarios">The scenario list to append to.</param>
        static partial void AddEs74MeshInputBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario(
                "B132: ChunkBorderMask names, for every column and every direction bit, exactly the neighbor chunks whose mesh reads it — Of matches an independently derived mask, Offset and Neighbor carry the literal chunk offset, Opposite inverts it, TryTakeNext visits each set bit once (ES-7.4)",
                Baseline_ChunkBorderMaskNamesTheNeighborsThatReadEachColumn));
            scenarios.Add(new Scenario(
                "B133: a merge's pending border mask is the OR of Of(x, z) over exactly the voxels whose effective light it moved, and its change report equals that diff — over the ES-7 fuzz plus a deterministic interior-only case and an absent-section-to-uniform-sky case (ES-7.4)",
                Baseline_MergeBorderMaskMatchesTheLightItMoved));
            scenarios.Add(new Scenario(
                "B134: ChunkData.SetLightData dirties the mesh only for a value that really changed, and names only the neighbors facing the written column — interior, the four edge middles, the four corners, and both no-op writes (ES-7.4)",
                Baseline_SetLightDataMarksRealChangesAtTheRightBorders));
            scenarios.Add(new Scenario(
                "B135: a ChunkData's mesh-input state across its lifecycle — fresh values, a populate flagging every neighbor, consume handing the bits back once, and Reset leaving nothing for the recycled chunk (ES-7.4)",
                Baseline_MeshInputStateFollowsTheChunkLifecycle));
            scenarios.Add(new Scenario(
                "B136: in the harness, one chunk's lighting pass leaves stale exactly the neighbors whose facing border light changed — nothing for a pass that changes no light, the east neighbor for a write on the east border column, and every other neighbor's state explained (ES-7.4)",
                Baseline_LightingPassTellsOnlyNeighborsWhoseFacingBorderChanged));
            scenarios.Add(new Scenario(
                "B137: MergeRemeshDecision over every stale-neighbor mask — ungated always the four cardinals, gated exactly the stale ones, diagonals only when included (ES-7.4)",
                Baseline_MergeRemeshDecisionTruthTable));
        }

        /// <summary>
        /// B137: for all 256 stale masks and both diagonal settings, the requested neighbors match a per-bit expectation.
        /// <para><b>Prove-red:</b> returning the stale mask when the gate is off; never admitting diagonals when gated.</para>
        /// </summary>
        private static bool Baseline_MergeRemeshDecisionTruthTable()
        {
            List<string> failures = new List<string>();
            for (int stale = 0; stale <= byte.MaxValue; stale++)
            {
                foreach (bool includeDiagonals in new[] { false, true })
                {
                    byte off = MergeRemeshDecision.NeighborRequests(false, includeDiagonals, (byte)stale);
                    if (off != ChunkBorderMask.Cardinals)
                        failures.Add($"off, diagonals {includeDiagonals}, stale {DescribeMask((byte)stale)}: {DescribeMask(off)}, expected the four cardinals");

                    byte on = MergeRemeshDecision.NeighborRequests(true, includeDiagonals, (byte)stale);
                    for (int i = 0; i < ChunkBorderMask.DirectionCount; i++)
                    {
                        byte bit = (byte)(1 << i);
                        bool isDiagonal = (ChunkBorderMask.Diagonals & bit) != 0;
                        bool expected = (stale & bit) != 0 && (!isDiagonal || includeDiagonals);
                        if (((on & bit) != 0) != expected)
                            failures.Add($"on, diagonals {includeDiagonals}, stale {DescribeMask((byte)stale)}: {DirectionName(bit)} requested {(on & bit) != 0}, expected {expected}");
                    }

                    if (failures.Count > B137_MAX_FAILURES) break;
                }
            }

            return LightingAssert.IsTrue(failures.Count == 0,
                "B137: the requested neighbors match the gate, the diagonal setting and the stale mask for every input",
                failures.Count == 0 ? null : string.Join("\n", failures));
        }

        /// <summary>
        /// B132: across all 256 columns of a chunk, <c>ChunkBorderMask.Of</c> equals a mask derived from the
        /// column's own position — a cardinal bit when the column lies on that edge, the diagonal bit only when
        /// both of its cardinals hold — and only the 64 border columns name any neighbor. Each direction bit's
        /// <c>Offset</c> and <c>Neighbor</c> are checked against a literal chunk offset, <c>Opposite</c> against its own
        /// inverse, and <c>TryTakeNext</c> must yield each set bit once, lowest first, then stop.
        /// <para><b>Prove-red:</b> swapping <c>East</c> for <c>West</c> in <c>Of</c>; <c>TryTakeNext</c> not clearing the taken bit.</para>
        /// </summary>
        private static bool Baseline_ChunkBorderMaskNamesTheNeighborsThatReadEachColumn()
        {
            List<string> failures = new List<string>();
            int interiorColumns = 0;
            int borderColumns = 0;

            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    byte actual = ChunkBorderMask.Of(x, z);
                    byte expected = ExpectedReadersOfColumn(x, z);
                    if (actual == 0) interiorColumns++;
                    else borderColumns++;

                    if (actual != expected)
                        failures.Add($"Of({x}, {z}): {DescribeMask(actual)}, expected {DescribeMask(expected)}");
                }
            }

            // Literal chunk offset per direction bit, so a bit's identity is pinned to the seam direction rather
            // than to Of's reading of it — and the two Opposite laws an inverse must obey.
            AssertDirectionBitGeometry(failures, ChunkBorderMask.North, 0, 1);
            AssertDirectionBitGeometry(failures, ChunkBorderMask.South, 0, -1);
            AssertDirectionBitGeometry(failures, ChunkBorderMask.East, 1, 0);
            AssertDirectionBitGeometry(failures, ChunkBorderMask.West, -1, 0);
            AssertDirectionBitGeometry(failures, ChunkBorderMask.NorthEast, 1, 1);
            AssertDirectionBitGeometry(failures, ChunkBorderMask.NorthWest, -1, 1);
            AssertDirectionBitGeometry(failures, ChunkBorderMask.SouthEast, 1, -1);
            AssertDirectionBitGeometry(failures, ChunkBorderMask.SouthWest, -1, -1);

            AssertTakesEachBitOnce(failures, ChunkBorderMask.All);
            AssertTakesEachBitOnce(failures, ChunkBorderMask.East | ChunkBorderMask.SouthWest);
            AssertTakesEachBitOnce(failures, 0);

            bool passed = LightingAssert.IsTrue(interiorColumns == B132_INTERIOR_COLUMNS && borderColumns == B132_BORDER_COLUMNS,
                "B132: an interior column names no neighbor, and only the border columns name one",
                $"interior {interiorColumns} (expected {B132_INTERIOR_COLUMNS}), border {borderColumns} (expected {B132_BORDER_COLUMNS})");
            passed &= LightingAssert.IsTrue(failures.Count == 0,
                "B132: Of, Offset, Neighbor, Opposite and TryTakeNext agree with the seam geometry derived independently here",
                failures.Count == 0 ? null : string.Join("\n", failures));
            return passed;
        }

        /// <summary>
        /// B133: each trial marks the chunk mesh-current, consumes its accumulated border writes, merges a job map
        /// (a band of rewritten rows, as B125 builds it) and compares the pending mask with the set of border
        /// columns whose effective light actually moved — the per-voxel diff is the oracle, so the band clamp and the
        /// absent-section branch both land inside it. Two deterministic cases add the shapes the fuzz cannot be
        /// relied on to produce; an observation census keeps every coverage claim non-vacuous.
        /// <para><b>Prove-red:</b> <c>i >> 4</c> for <c>i >> SECTION_Z_SHIFT</c> in the merge's per-element loop,
        /// and deleting <c>changedBorders = ChunkBorderMask.All</c> in its absent uniform-sky branch.</para>
        /// </summary>
        private static bool Baseline_MergeBorderMaskMatchesTheLightItMoved()
        {
            bool passed = true;
            NativeArray<ushort> jobLight = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> beforeLight = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            NativeArray<ushort> afterLight = new NativeArray<ushort>(ChunkMath.CHUNK_VOLUME, Allocator.Temp);
            List<string> failures = new List<string>();
            int observations = 0;
            try
            {
                Random rng = new Random(ES7_FUZZ_SEED);
                for (int trial = 0; trial < ES7_FUZZ_TRIALS && failures.Count == 0; trial++)
                {
                    ChunkData chunk = BuildFuzzChunk(rng.Next(), jobLight);
                    int bandMinY = rng.Next(0, ChunkMath.CHUNK_HEIGHT);
                    int bandHeight = rng.Next(bandMinY + 1, ChunkMath.CHUNK_HEIGHT + 1);

                    chunk.FillJobLightMap(jobLight);
                    WriteBandRows(rng, jobLight, bandMinY, bandHeight);
                    RunMergeBorderMaskTrial(chunk, jobLight, beforeLight, afterLight, bandMinY, bandHeight,
                        $"trial {trial}, band [{bandMinY}, {bandHeight})", failures, ref observations);
                }

                // A change with no border column involved is what lets a stale mesh be kept, so pin one: every
                // section compact at full sky, one interior voxel re-darkened in the job map.
                ChunkData interiorChunk = new ChunkData(Vector2Int.zero);
                FillUniform(jobLight, LightBitMapping.PackLightData(B133_FULL_SKY, 0, 0, 0));
                interiorChunk.ApplyJobLightMap(jobLight, null);
                interiorChunk.FillJobLightMap(jobLight);
                jobLight[ChunkMath.GetFlattenedIndexInChunk(B133_INTERIOR_X, B133_INTERIOR_Y, B133_INTERIOR_Z)] =
                    LightBitMapping.PackLightData(B133_DIM_SKY, 0, 0, 0);
                RunMergeBorderMaskTrial(interiorChunk, jobLight, beforeLight, afterLight, 0,
                    ChunkMath.CHUNK_HEIGHT, "interior-only case", failures, ref observations);

                // An absent dark section merged under uniform sky light moves every voxel of the section at once,
                // which is the one branch that reports all eight neighbors without a per-element loop.
                ChunkData emptyChunk = new ChunkData(Vector2Int.zero);
                FillUniform(jobLight, LightBitMapping.PackLightData(B133_FULL_SKY, 0, 0, 0));
                RunMergeBorderMaskTrial(emptyChunk, jobLight, beforeLight, afterLight, 0,
                    ChunkMath.CHUNK_HEIGHT, "absent-section case", failures, ref observations);

                passed &= LightingAssert.IsTrue((observations & B133_SEEN_NONZERO_BORDER_MASK) != 0,
                    "B133: at least one merge reported a nonzero border mask",
                    $"observations 0x{observations:X2}");
                passed &= LightingAssert.IsTrue((observations & B133_SEEN_INTERIOR_ONLY_CHANGE) != 0,
                    "B133: at least one merge changed light with no border column involved",
                    $"observations 0x{observations:X2}");
                passed &= LightingAssert.IsTrue((observations & B133_SEEN_ABSENT_SECTION_TO_UNIFORM_SKY) != 0,
                    "B133: at least one merge took the absent-section-to-uniform-sky branch",
                    $"observations 0x{observations:X2}");
                passed &= LightingAssert.IsTrue(failures.Count == 0,
                    "B133: every merge's border mask and change report match the light it moved",
                    failures.Count == 0 ? null : string.Join("\n", failures));
            }
            finally
            {
                jobLight.Dispose();
                beforeLight.Dispose();
                afterLight.Dispose();
            }

            return passed;
        }

        /// <summary>
        /// Runs one B133 merge through the mask check, recording which case shapes it observed.
        /// </summary>
        /// <param name="chunk">The chunk to merge into; its mesh is marked current first, as a mesher's snapshot would.</param>
        /// <param name="jobLight">The job's light map, already prepared by the caller.</param>
        /// <param name="beforeLight">Buffer receiving the pre-merge effective light.</param>
        /// <param name="afterLight">Buffer receiving the post-merge effective light.</param>
        /// <param name="bandMinY">The merge's first gathered row.</param>
        /// <param name="bandHeight">The merge's last gathered row, exclusive.</param>
        /// <param name="where">Names the trial for a failure detail.</param>
        /// <param name="failures">Accumulated failure details.</param>
        /// <param name="observations">The census of case shapes seen so far.</param>
        private static void RunMergeBorderMaskTrial(ChunkData chunk, NativeArray<ushort> jobLight,
            NativeArray<ushort> beforeLight, NativeArray<ushort> afterLight, int bandMinY, int bandHeight,
            string where, List<string> failures, ref int observations)
        {
            chunk.MarkMeshInputsCurrent();
            chunk.ConsumePendingBorderWrites();

            bool[] sectionWasAbsentDark = new bool[ChunkMath.SECTIONS_PER_CHUNK];
            for (int s = 0; s < sectionWasAbsentDark.Length; s++)
                sectionWasAbsentDark[s] = chunk.sections[s] == null
                    && chunk.SectionUniformSkyLevel[s] == B133_UNCOMPACTED;

            chunk.FillJobLightMap(beforeLight);

            PerfMergeTotals unusedTotals = default;
            bool changed = chunk.ApplyJobLightMap(jobLight, null, bandMinY, bandHeight, ref unusedTotals);
            chunk.FillJobLightMap(afterLight);

            byte expectedMask = ChangedBorderMask(beforeLight, afterLight, out bool lightMoved);
            byte pending = chunk.PendingBorderWrites;
            if (pending != expectedMask)
                failures.Add($"{where}: pending {DescribeMask(pending)}, expected {DescribeMask(expectedMask)} from the light diff");
            if (changed != lightMoved || chunk.MeshInputsCurrent != !changed)
                failures.Add($"{where}: change report {changed}, light moved {lightMoved}, mesh current {chunk.MeshInputsCurrent}");

            if (pending != 0) observations |= B133_SEEN_NONZERO_BORDER_MASK;
            if (changed && pending == 0) observations |= B133_SEEN_INTERIOR_ONLY_CHANGE;
            for (int s = 0; s < sectionWasAbsentDark.Length; s++)
            {
                if (!sectionWasAbsentDark[s] || chunk.sections[s] != null
                    || chunk.SectionUniformSkyLevel[s] == B133_UNCOMPACTED)
                    continue;

                observations |= B133_SEEN_ABSENT_SECTION_TO_UNIFORM_SKY;
                break;
            }
        }

        /// <summary>
        /// B134: <c>SetLightData</c> marks the mesh stale only when the stored value really changes, and its pending
        /// mask is the literal set of neighbors facing the written column: none for an interior column, one for an
        /// edge middle, three (two cardinals plus their diagonal) for a corner.
        /// <para><b>Prove-red:</b> deleting the <c>if (lightData[index] == value) return;</c> early-out in
        /// <c>SetLightData</c>, and swapping <c>East</c> for <c>West</c> in <c>ChunkBorderMask.Of</c>.</para>
        /// </summary>
        private static bool Baseline_SetLightDataMarksRealChangesAtTheRightBorders()
        {
            bool passed = true;
            ChunkData chunk = new ChunkData(Vector2Int.zero);
            ushort stored = LightBitMapping.PackLightData(B134_SKY, 0, 0, 0);

            // A dark value where no section exists: nothing is allocated, nothing is written, so nothing dirties.
            chunk.MarkMeshInputsCurrent();
            chunk.ConsumePendingBorderWrites();
            chunk.SetLightData(B134_EDGE_MID, B134_Y, B134_EDGE_MID, 0);
            passed &= LightingAssert.IsTrue(chunk.MeshInputsCurrent && chunk.PendingBorderWrites == 0,
                "B134: a dark write into an unallocated section dirties nothing",
                $"mesh current {chunk.MeshInputsCurrent}, pending {DescribeMask(chunk.PendingBorderWrites)}");

            // Re-writing the value a voxel already holds: the mesh stays current.
            chunk.SetLightData(3, B134_Y, 3, stored);
            chunk.MarkMeshInputsCurrent();
            chunk.ConsumePendingBorderWrites();
            chunk.SetLightData(3, B134_Y, 3, stored);
            passed &= LightingAssert.IsTrue(chunk.MeshInputsCurrent && chunk.PendingBorderWrites == 0,
                "B134: rewriting a voxel's current light dirties nothing",
                $"mesh current {chunk.MeshInputsCurrent}, pending {DescribeMask(chunk.PendingBorderWrites)}");

            // An interior column: this chunk's mesh is stale, and no neighbor reads that column.
            chunk.MarkMeshInputsCurrent();
            chunk.ConsumePendingBorderWrites();
            chunk.SetLightData(6, B134_Y, 6, LightBitMapping.PackLightData(B134_OTHER_SKY, 0, 0, 0));
            passed &= LightingAssert.IsTrue(!chunk.MeshInputsCurrent && chunk.PendingBorderWrites == 0,
                "B134: an interior column change dirties this chunk's mesh and names no neighbor",
                $"mesh current {chunk.MeshInputsCurrent}, pending {DescribeMask(chunk.PendingBorderWrites)}");

            // Each edge middle names one cardinal; each corner names its two cardinals plus the diagonal. The
            // expectations are written out, not derived from Of, so a transposed direction shows up.
            List<string> failures = new List<string>();
            ExpectBorderWriteOnChunk(chunk, failures, 0, B134_EDGE_MID, stored, ChunkBorderMask.West);
            ExpectBorderWriteOnChunk(chunk, failures, B134_LAST, B134_EDGE_MID, stored, ChunkBorderMask.East);
            ExpectBorderWriteOnChunk(chunk, failures, B134_EDGE_MID, 0, stored, ChunkBorderMask.South);
            ExpectBorderWriteOnChunk(chunk, failures, B134_EDGE_MID, B134_LAST, stored, ChunkBorderMask.North);
            ExpectBorderWriteOnChunk(chunk, failures, 0, 0, stored,
                (byte)(ChunkBorderMask.West | ChunkBorderMask.South | ChunkBorderMask.SouthWest));
            ExpectBorderWriteOnChunk(chunk, failures, B134_LAST, 0, stored,
                (byte)(ChunkBorderMask.East | ChunkBorderMask.South | ChunkBorderMask.SouthEast));
            ExpectBorderWriteOnChunk(chunk, failures, 0, B134_LAST, stored,
                (byte)(ChunkBorderMask.West | ChunkBorderMask.North | ChunkBorderMask.NorthWest));
            ExpectBorderWriteOnChunk(chunk, failures, B134_LAST, B134_LAST, stored,
                (byte)(ChunkBorderMask.East | ChunkBorderMask.North | ChunkBorderMask.NorthEast));

            passed &= LightingAssert.IsTrue(failures.Count == 0,
                "B134: an edge or corner light write names exactly the neighbors facing that column",
                failures.Count == 0 ? null : string.Join("\n", failures));
            return passed;
        }

        /// <summary>
        /// B135: the mesh-input fields across one chunk's lifecycle — a fresh instance reports a stale mesh and no
        /// pending border write; populating flags every neighbor's mesh stale (none was built from this data, and
        /// the old data may still be meshed); <c>ConsumePendingBorderWrites</c> hands the accumulated bits back once;
        /// a pool <c>Reset</c> leaves neither flag.
        /// <para><b>Prove-red:</b> deleting <c>NoteRepopulated();</c> in <c>PopulateFromFlattened</c> and
        /// <c>_pendingBorderWrites = 0;</c> in <c>ChunkData.Reset</c>.</para>
        /// </summary>
        private static bool Baseline_MeshInputStateFollowsTheChunkLifecycle()
        {
            bool passed = true;
            ChunkData chunk = new ChunkData(Vector2Int.zero);

            passed &= LightingAssert.IsTrue(!chunk.MeshInputsCurrent && chunk.PendingBorderWrites == 0,
                "B135: a fresh ChunkData reports a stale mesh and no pending border write",
                $"mesh current {chunk.MeshInputsCurrent}, pending {DescribeMask(chunk.PendingBorderWrites)}");

            // All-air voxel data: every section reads empty, so with no live World the populate touches no world state.
            NativeArray<uint> allAir = new NativeArray<uint>(ChunkMath.CHUNK_VOLUME, Allocator.Temp,
                NativeArrayOptions.ClearMemory);
            try
            {
                chunk.PopulateFromFlattened(allAir);
                passed &= LightingAssert.IsTrue(!chunk.MeshInputsCurrent && chunk.PendingBorderWrites == ChunkBorderMask.All,
                    "B135: populating flags every neighbor's mesh stale, since none was built from this data",
                    $"mesh current {chunk.MeshInputsCurrent}, pending {DescribeMask(chunk.PendingBorderWrites)}");

                chunk.MarkMeshInputsCurrent();
                bool allHandedBack = chunk.ConsumePendingBorderWrites() == ChunkBorderMask.All;
                bool secondConsumeEmpty = chunk.ConsumePendingBorderWrites() == 0;
                passed &= LightingAssert.IsTrue(chunk.MeshInputsCurrent && allHandedBack && secondConsumeEmpty,
                    "B135: marking current plus consuming hands the accumulated bits back exactly once",
                    $"mesh current {chunk.MeshInputsCurrent}, all {allHandedBack}, second {secondConsumeEmpty}");

                // Dirty both again — a border light write is a real change for this chunk and for the neighbor
                // facing that column — then recycle.
                chunk.MarkMeshInputsCurrent();
                chunk.SetLightData(B135_BORDER_X, B135_Y, B135_MID_Z, LightBitMapping.PackLightData(B135_SKY, 0, 0, 0));
                passed &= LightingAssert.IsTrue(!chunk.MeshInputsCurrent && chunk.PendingBorderWrites == ChunkBorderMask.East,
                    "B135: setup — the border write dirties this mesh and records the east neighbor",
                    $"mesh current {chunk.MeshInputsCurrent}, pending {DescribeMask(chunk.PendingBorderWrites)}");

                chunk.Reset(Vector2Int.zero);
                passed &= LightingAssert.IsTrue(!chunk.MeshInputsCurrent && chunk.PendingBorderWrites == 0,
                    "B135: Reset leaves no mesh-input state for the recycled chunk",
                    $"mesh current {chunk.MeshInputsCurrent}, pending {DescribeMask(chunk.PendingBorderWrites)}");
            }
            finally
            {
                allAir.Dispose();
            }

            return passed;
        }

        /// <summary>
        /// B136: with every settled chunk's mesh marked current and its pending border mask consumed, re-running the
        /// center chunk's job tells nobody; stamping light on the center's east border column and running it again
        /// tells the east neighbor, and every remaining neighbor's state is explained by a light write that merge
        /// made in that chunk — the full eight-neighbor picture, not just the east bit.
        /// <para><b>Prove-red:</b> deleting the <c>MeshInputPropagation.Propagate(...)</c> line in the harness merge.</para>
        /// </summary>
        private static bool Baseline_LightingPassTellsOnlyNeighborsWhoseFacingBorderChanged()
        {
            using LightingTestWorld world = new LightingTestWorld(B136_GRID);
            world.FillSuperflatFloor(B136_SOLID_TOP, TestBlockPalette.Stone);

            Vector3Int pocket = new Vector3Int(B136_POCKET_X, B136_POCKET_Y, B136_POCKET_Z);
            world.SetBlock(pocket, TestBlockPalette.Air); // buried cell, stone on all six faces
            world.RecalculateHeightmaps();

            bool passed = LightingAssert.Converged(world.RunInitialLighting(), "B136: initial lighting converges");

            Vector2Int center = new Vector2Int(B136_CENTER, B136_CENTER);
            ChunkCoord centerCoord = new ChunkCoord(B136_CENTER, B136_CENTER);

            foreach (Vector2Int coord in world.AllChunkCoords())
            {
                ChunkData data = world.GetChunkData(coord);
                data.MarkMeshInputsCurrent();
                data.ConsumePendingBorderWrites();
            }

            passed &= LightingAssert.IsTrue(MeshInputPropagation.StaleNeighbors(centerCoord, world) == 0,
                "B136: positive control — no neighbor is stale once the settled masks are consumed",
                DescribeMask(MeshInputPropagation.StaleNeighbors(centerCoord, world)));

            // A settled chunk's own re-run changes no light, so it tells nobody; the light-diff expectation in the
            // failure detail separates "the pass changed border light" from "the pass over-told".
            Dictionary<Vector2Int, ushort[]> beforeQuietRun = world.SnapshotLightField();
            world.RunLightingJob(center);
            passed &= LightingAssert.IsTrue(MeshInputPropagation.StaleNeighbors(centerCoord, world) == 0,
                "B136: a pass whose merge changed no light tells no neighbor",
                $"stale {DescribeMask(MeshInputPropagation.StaleNeighbors(centerCoord, world))}, but the merge's own " +
                $"light diff would explain {DescribeMask(ExpectedStaleNeighborBits(world, beforeQuietRun, center, 0))}");

            byte skyBeforeStamp = world.GetSkylight(pocket);
            passed &= LightingAssert.IsTrue(skyBeforeStamp < B136_STAMP_SKY,
                "B136: setup — the buried cell holds less than full sky, so raising it is a real change",
                $"sky {skyBeforeStamp}");
            passed &= LightingAssert.IsTrue((MeshInputPropagation.StaleNeighbors(centerCoord, world) & ChunkBorderMask.East) == 0,
                "B136: positive control — the east neighbor's mesh is current before the border write");

            // The stamp is a raw light write on the border column, so the writer's own pending bit is the east
            // neighbor's bit; the merge's own diff is added by the oracle below.
            world.SetSkylightAt(pocket, B136_STAMP_SKY);
            passed &= LightingAssert.IsTrue(world.GetSkylight(pocket) == B136_STAMP_SKY,
                "B136: setup — the stamp lands on the center chunk's east border column",
                $"sky {world.GetSkylight(pocket)}");
            Dictionary<Vector2Int, ushort[]> before = world.SnapshotLightField();
            world.RunLightingJob(center);

            byte expected = ExpectedStaleNeighborBits(world, before, center, ChunkBorderMask.East);
            byte actual = MeshInputPropagation.StaleNeighbors(centerCoord, world);
            passed &= LightingAssert.IsTrue((actual & ChunkBorderMask.East) != 0,
                "B136: the east neighbor is told — its mesh reads this border column",
                $"stale {DescribeMask(actual)}");
            passed &= LightingAssert.IsTrue(actual == expected,
                "B136: exactly the neighbors whose facing border light changed are stale",
                $"expected {DescribeMask(expected)}, got {DescribeMask(actual)}");
            return passed;
        }

        /// <summary>
        /// Checks one direction bit's three geometric laws: its chunk offset, <c>Opposite</c> being an involution,
        /// and <c>Offset</c> of the opposite bit being the negation. Failures go to the caller's list.
        /// </summary>
        /// <param name="failures">Accumulated failure details.</param>
        /// <param name="bit">The direction bit under check.</param>
        /// <param name="dx">The chunk offset along X that bit must carry.</param>
        /// <param name="dz">The chunk offset along Z that bit must carry.</param>
        private static void AssertDirectionBitGeometry(List<string> failures, byte bit, int dx, int dz)
        {
            ChunkBorderMask.Offset(bit, out int gotX, out int gotZ);
            if (gotX != dx || gotZ != dz)
                failures.Add($"Offset({DirectionName(bit)}): ({gotX}, {gotZ}), expected ({dx}, {dz})");

            byte opposite = ChunkBorderMask.Opposite(bit);
            if (ChunkBorderMask.Opposite(opposite) != bit)
                failures.Add($"Opposite(Opposite({DirectionName(bit)})): {DescribeMask(opposite)}");

            ChunkBorderMask.Offset(opposite, out int oppositeX, out int oppositeZ);
            if (oppositeX != -dx || oppositeZ != -dz)
                failures.Add($"Offset(Opposite({DirectionName(bit)})): ({oppositeX}, {oppositeZ}), expected ({-dx}, {-dz})");

            ChunkCoord origin = new ChunkCoord(B132_NEIGHBOR_ORIGIN_X, B132_NEIGHBOR_ORIGIN_Z);
            ChunkCoord neighbor = ChunkBorderMask.Neighbor(origin, bit);
            if (neighbor.X != origin.X + dx || neighbor.Z != origin.Z + dz)
                failures.Add($"Neighbor({origin}, {DirectionName(bit)}): {neighbor}, expected offset ({dx}, {dz})");
        }

        /// <summary>
        /// Drains <paramref name="mask"/> with <c>TryTakeNext</c> and checks it yields every set bit once in ascending
        /// order, leaves the mask empty, and then reports nothing left. Failures go to the caller's list.
        /// </summary>
        /// <param name="failures">Accumulated failure details.</param>
        /// <param name="mask">The direction bits to drain.</param>
        private static void AssertTakesEachBitOnce(List<string> failures, byte mask)
        {
            byte remaining = mask;
            int taken = 0;
            int previous = 0;
            int guard = ChunkBorderMask.DirectionCount + 1;
            while (guard-- > 0 && ChunkBorderMask.TryTakeNext(ref remaining, out byte bit))
            {
                if ((mask & bit) == 0 || (taken & bit) != 0 || bit <= previous)
                    failures.Add($"TryTakeNext({DescribeMask(mask)}): took {DescribeMask(bit)} after {DescribeMask((byte)taken)}");
                taken |= bit;
                previous = bit;
            }

            if (taken != mask || remaining != 0)
                failures.Add($"TryTakeNext({DescribeMask(mask)}): took {DescribeMask((byte)taken)}, left {DescribeMask(remaining)}");
            if (ChunkBorderMask.TryTakeNext(ref remaining, out byte extra))
                failures.Add($"TryTakeNext({DescribeMask(mask)}): took {DescribeMask(extra)} from a drained mask");
        }

        /// <summary>
        /// Marks a chunk's mesh current, writes one light value at (x, z) in the row <c>B134_Y</c>, and checks the
        /// mask that write reports. Failures go to the caller's list.
        /// </summary>
        /// <param name="chunk">The chunk being written.</param>
        /// <param name="failures">Accumulated failure details.</param>
        /// <param name="x">Chunk-local X of the write.</param>
        /// <param name="z">Chunk-local Z of the write.</param>
        /// <param name="value">The packed light value to write.</param>
        /// <param name="expected">The <see cref="ChunkBorderMask"/> bits that write must report.</param>
        private static void ExpectBorderWriteOnChunk(ChunkData chunk, List<string> failures, int x, int z, ushort value,
            byte expected)
        {
            chunk.MarkMeshInputsCurrent();
            chunk.ConsumePendingBorderWrites();
            chunk.SetLightData(x, B134_Y, z, value);
            if (chunk.PendingBorderWrites != expected || chunk.MeshInputsCurrent)
                failures.Add($"({x}, {z}): pending {DescribeMask(chunk.PendingBorderWrites)}, expected {DescribeMask(expected)}, mesh current {chunk.MeshInputsCurrent}");
        }

        /// <summary>The neighbors a mesh reads the column at (localX, localZ), derived from the column's position.</summary>
        /// <param name="localX">Chunk-local X, 0..15.</param>
        /// <param name="localZ">Chunk-local Z, 0..15.</param>
        /// <returns>One bit per neighbor whose seam read reaches that column.</returns>
        private static byte ExpectedReadersOfColumn(int localX, int localZ)
        {
            bool west = localX == 0;
            bool east = localX == B132_LAST_COLUMN;
            bool south = localZ == 0;
            bool north = localZ == B132_LAST_COLUMN;

            int mask = 0;
            if (west) mask |= ChunkBorderMask.West;
            if (east) mask |= ChunkBorderMask.East;
            if (south) mask |= ChunkBorderMask.South;
            if (north) mask |= ChunkBorderMask.North;
            if (north && east) mask |= ChunkBorderMask.NorthEast;
            if (north && west) mask |= ChunkBorderMask.NorthWest;
            if (south && east) mask |= ChunkBorderMask.SouthEast;
            if (south && west) mask |= ChunkBorderMask.SouthWest;
            return (byte)mask;
        }

        /// <summary>
        /// The border columns whose effective light differs between two snapshots of one chunk, plus whether any
        /// voxel moved at all (the merge's own change report must agree with that, not with a representation change).
        /// </summary>
        /// <param name="before">The pre-merge effective light, in the flattened chunk layout.</param>
        /// <param name="after">The post-merge effective light, same layout.</param>
        /// <param name="anyVoxelChanged">True when at least one voxel's light differs.</param>
        /// <returns>The <see cref="ChunkBorderMask"/> bits of the columns the difference touches.</returns>
        private static byte ChangedBorderMask(NativeArray<ushort> before, NativeArray<ushort> after,
            out bool anyVoxelChanged)
        {
            int mask = 0;
            anyVoxelChanged = false;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;

                anyVoxelChanged = true;
                ChunkMath.GetLocalPositionFromFlattenedIndex(i, out int x, out _, out int z);
                mask |= ChunkBorderMask.Of(x, z);
            }

            return (byte)mask;
        }

        /// <summary>The <see cref="ChunkBorderMask"/> bits of the columns a chunk's light diff touches.</summary>
        /// <param name="before">A snapshot from <see cref="LightingTestWorld.SnapshotLightField"/>.</param>
        /// <param name="data">The chunk's live data now.</param>
        /// <returns>The bits of the columns whose light differs from the snapshot.</returns>
        private static byte ChangedBorderMask(ushort[] before, ChunkData data)
        {
            int mask = 0;
            for (int i = 0; i < before.Length; i++)
            {
                ChunkMath.GetLocalPositionFromFlattenedIndex(i, out int x, out int y, out int z);
                if (before[i] == data.GetLightData(x, y, z)) continue;

                mask |= ChunkBorderMask.Of(x, z);
            }

            return (byte)mask;
        }

        /// <summary>
        /// The neighbors whose mesh state a single lighting pass of <paramref name="center"/> leaves stale: the
        /// ones facing a border column the writer changed, plus any other chunk that received a light write of
        /// its own during the pass and is therefore stale for that reason alone.
        /// </summary>
        /// <param name="world">The harness after the pass.</param>
        /// <param name="before">The light field snapshot taken just before the pass.</param>
        /// <param name="center">The grid coordinate of the chunk whose job ran.</param>
        /// <param name="directWriteBits">The writer's own pending bits from the direct light write.</param>
        /// <returns>The expected <see cref="ChunkBorderMask"/> bits of stale neighbors of <c>center</c>.</returns>
        private static byte ExpectedStaleNeighborBits(LightingTestWorld world, Dictionary<Vector2Int, ushort[]> before,
            Vector2Int center, byte directWriteBits)
        {
            int stale = directWriteBits;
            foreach (KeyValuePair<Vector2Int, ushort[]> entry in before)
            {
                byte mask = ChangedBorderMask(entry.Value, world.GetChunkData(entry.Key));
                if (mask == 0) continue;

                if (entry.Key == center) stale |= mask; // the writer tells the neighbors facing a changed border
                else stale |= NeighborBitOf(center, entry.Key); // a chunk written directly is stale by itself
            }

            return (byte)stale;
        }

        /// <summary>The direction bit naming a chunk relative to another, by its chunk-index offset.</summary>
        /// <param name="from">The chunk the offset is measured from.</param>
        /// <param name="to">The chunk to name.</param>
        /// <returns>Its bit, or 0 when it is not one of the eight neighbors.</returns>
        private static byte NeighborBitOf(Vector2Int from, Vector2Int to)
        {
            Vector2Int delta = to - from;
            for (int i = 0; i < ChunkBorderMask.DirectionCount; i++)
            {
                byte bit = (byte)(1 << i);
                ChunkBorderMask.Offset(bit, out int dx, out int dz);
                if (dx == delta.x && dz == delta.y) return bit;
            }

            return 0;
        }

        /// <summary>A direction bit as the direction it names.</summary>
        /// <param name="bit">Exactly one direction bit.</param>
        /// <returns>The direction's name.</returns>
        private static string DirectionName(byte bit) => bit switch
        {
            ChunkBorderMask.North => "North",
            ChunkBorderMask.South => "South",
            ChunkBorderMask.East => "East",
            ChunkBorderMask.West => "West",
            ChunkBorderMask.NorthEast => "NorthEast",
            ChunkBorderMask.NorthWest => "NorthWest",
            ChunkBorderMask.SouthEast => "SouthEast",
            ChunkBorderMask.SouthWest => "SouthWest",
            _ => $"unnamed bit 0x{bit:X2}",
        };

        /// <summary>A neighbor mask readable in a failure detail.</summary>
        /// <param name="mask">A <see cref="ChunkBorderMask"/> mask.</param>
        /// <returns>"none" for the empty mask, else the direction names joined by "|".</returns>
        private static string DescribeMask(byte mask)
        {
            if (mask == 0) return "none";

            string names = string.Empty;
            for (int i = 0; i < ChunkBorderMask.DirectionCount; i++)
            {
                byte bit = (byte)(1 << i);
                if ((mask & bit) == 0) continue;
                names = names.Length == 0 ? DirectionName(bit) : $"{names}|{DirectionName(bit)}";
            }

            return names;
        }
    }
}
