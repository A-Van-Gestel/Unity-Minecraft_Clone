using System.Collections.Generic;
using System.Text;
using Data;
using Helpers;
using Jobs.BurstData;
using Serialization;
using UnityEngine;
using Random = System.Random;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Part 1 of the suite (roadmap <c>NS-1</c>): round-trip identity. A chunk written by
    /// <see cref="ChunkSerializer"/> and read back must be semantically identical through the public
    /// accessors, must re-derive (not carry) the state the format does not persist, and must re-serialize
    /// to the same bytes — the read/write pair has to agree on the encoding, not merely on the contents.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        /// <summary>Maximum per-scenario mismatches reported before the diff is truncated.</summary>
        private const int MAX_REPORTED_DIFFS = 5;

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>
        /// B2. The core identity assertion. Red when: any persisted field fails to survive the round trip —
        /// voxels, per-voxel light (through either the compact-sky or the full-LightData path), the
        /// heightmap, <c>NeedsInitialLighting</c>, or either BFS light queue's contents/order.
        /// </summary>
        /// <returns>True when the reloaded chunk is accessor-identical to the source.</returns>
        private static bool RoundTripPreservesEveryPersistedField()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 0);
            PoolBalance balance = PoolBalance.Capture();

            ChunkData source = BuildReferenceChunk(pos);
            ChunkData loaded = null;
            bool ok;
            try
            {
                byte[] payload = SerializeUncompressed(source);
                loaded = ChunkSerializer.Deserialize(payload, CompressionAlgorithm.None, pos);
                if (loaded == null) return Check("reference payload deserializes", false);

                ok = Check($"position survives (expected {pos.ToString()}, got {loaded.Position.ToString()})",
                    loaded.Position == pos);
                ok &= AssertChunksEquivalent(source, loaded, "reference chunk");
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(source);
                if (loaded != null) World.Instance.ChunkPool.ReturnChunkData(loaded);
            }

            ok &= balance.AssertUnchanged("pools balanced after the round trip");
            return ok;
        }

        /// <summary>
        /// B3. The non-persisted half of the contract, which an identity compare cannot see: state the format
        /// deliberately omits must be RE-DERIVED on load, not inherited from a recycled shell. Red when:
        /// <c>HasLightChangesToProcess</c> stops tracking the restored queues, <c>IsPopulated</c> is not set,
        /// or a section that carries no data starts being materialized on load.
        /// </summary>
        /// <returns>True when derived state matches what the loaded contents imply.</returns>
        private static bool RoundTripReDerivesNonPersistedState()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(16, -16);

            ChunkData source = BuildReferenceChunk(pos);
            ChunkData loaded = null;
            bool ok;
            try
            {
                byte[] payload = SerializeUncompressed(source);
                loaded = ChunkSerializer.Deserialize(payload, CompressionAlgorithm.None, pos);
                if (loaded == null) return Check("reference payload deserializes", false);

                ok = Check("IsPopulated is set on load", loaded.IsPopulated);
                ok &= Check("HasLightChangesToProcess is derived from the restored queues (non-empty here)",
                    loaded.HasLightChangesToProcess);
                ok &= Check($"NeedsInitialLighting survives (expected true, got {loaded.NeedsInitialLighting.ToString()})",
                    loaded.NeedsInitialLighting);

                // Data-less sections must NOT be materialized: the writer excludes them from the bitmask, so
                // the reader has nothing to allocate. A section object here would be pooled memory held for a
                // section that contains nothing.
                ok &= Check("an attached-but-data-less section is not materialized on load",
                    loaded.sections[SECTION_EMPTY_ATTACHED] == null);
                ok &= Check("a never-attached section stays null on load",
                    loaded.sections[SECTION_NULL] == null);

                // The compact light-only section must come back compact (a sky byte, no section object) —
                // materializing it would silently multiply a 2-byte section into 8 KB of LightData.
                ok &= Check($"the compact light-only section stays compact (sky {loaded.SectionUniformSkyLevel[SECTION_LIGHT_ONLY_UNIFORM].ToString()}, section {(loaded.sections[SECTION_LIGHT_ONLY_UNIFORM] == null ? "null" : "allocated")})",
                    loaded.sections[SECTION_LIGHT_ONLY_UNIFORM] == null &&
                    loaded.SectionUniformSkyLevel[SECTION_LIGHT_ONLY_UNIFORM] == UNIFORM_SKY_LIGHT_ONLY_SECTION);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(source);
                if (loaded != null) World.Instance.ChunkPool.ReturnChunkData(loaded);
            }

            return ok;
        }

        /// <summary>
        /// B4. Encoding stability: re-serializing a chunk that was just read back must reproduce the original
        /// bytes exactly. An accessor-level compare (B2) is blind to WHICH of the four section encodings the
        /// writer chose, so a reader that loses the compact-sky distinction — or a writer that stops choosing
        /// it — round-trips perfectly while silently inflating every save on disk. Red when: write∘read is not
        /// the identity on the payload, or the re-written flag map differs from the fixture contract.
        /// </summary>
        /// <returns>True when the second payload is byte-identical and carries the same flag map.</returns>
        private static bool ReSerializationIsByteIdentical()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(-32, 48);

            ChunkData source = BuildReferenceChunk(pos);
            ChunkData loaded = null;
            bool ok;
            try
            {
                byte[] first = SerializeUncompressed(source);
                loaded = ChunkSerializer.Deserialize(first, CompressionAlgorithm.None, pos);
                if (loaded == null) return Check("reference payload deserializes", false);

                byte[] second = SerializeUncompressed(loaded);
                ok = AssertPayloadsIdentical(first, second, "re-serialized payload");

                byte[] flags = ParseSectionFlags(second, loaded.sections.Length);
                ok &= Check($"the re-written section flag map is unchanged (expected {FormatFlags(s_expectedFixtureFlags)}, got {FormatFlags(flags)})",
                    FlagMapsEqual(s_expectedFixtureFlags, flags));
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(source);
                if (loaded != null) World.Instance.ChunkPool.ReturnChunkData(loaded);
            }

            return ok;
        }

        /// <summary>Fixed seed for the fuzz chunk — deterministic, so a failure is reproducible.</summary>
        private const int FUZZ_SEED = 0x5E71A1;

        /// <summary>How many independently randomized chunks the fuzz scenario round-trips.</summary>
        private const int FUZZ_CHUNK_COUNT = 8;

        /// <summary>
        /// B5. The same identity and encoding-stability contract as B2/B4, over randomized chunks whose
        /// section-mode mix, voxel/light contents, heightmap and queue depths vary — so the guarantee is not
        /// pinned to the one hand-built section layout the reference fixture happens to use. Red when: some
        /// section-mode combination the reference chunk does not contain fails to round-trip.
        /// </summary>
        /// <returns>True when every fuzz chunk round-trips identically and re-serializes byte-identically.</returns>
        private static bool FuzzChunksRoundTripIdentically()
        {
            using Fixture fx = new Fixture();
            PoolBalance balance = PoolBalance.Capture();
            Random rng = new Random(FUZZ_SEED);
            bool ok = true;

            for (int i = 0; i < FUZZ_CHUNK_COUNT; i++)
            {
                Vector2Int pos = new Vector2Int(i * 16, -i * 16);
                ChunkData source = BuildFuzzChunk(pos, rng);
                ChunkData loaded = null;
                try
                {
                    byte[] first = SerializeUncompressed(source);
                    loaded = ChunkSerializer.Deserialize(first, CompressionAlgorithm.None, pos);
                    if (loaded == null)
                    {
                        ok &= Check($"fuzz chunk {i.ToString()} deserializes", false);
                        continue;
                    }

                    ok &= AssertChunksEquivalent(source, loaded, $"fuzz chunk {i.ToString()}");
                    ok &= AssertPayloadsIdentical(first, SerializeUncompressed(loaded), $"fuzz chunk {i.ToString()} re-serialized");
                }
                finally
                {
                    World.Instance.ChunkPool.ReturnChunkData(source);
                    if (loaded != null) World.Instance.ChunkPool.ReturnChunkData(loaded);
                }
            }

            ok &= balance.AssertUnchanged("pools balanced after the fuzz sweep");
            return ok;
        }

        // --- Fuzz fixture ------------------------------------------------------------------------

        /// <summary>The section shapes the fuzz builder picks from — one per writer outcome, plus absent.</summary>
        private enum FuzzSectionMode
        {
            Absent = 0,
            VoxelsUniformSky = 1,
            VoxelsAndLight = 2,
            LightOnlyUniformSky = 3,
            LightOnlyFull = 4,
        }

        /// <summary>Upper bound (exclusive) for randomized queue node counts per queue.</summary>
        private const int FUZZ_MAX_QUEUE_NODES = 9;

        /// <summary>
        /// Builds a randomized chunk: each section slot independently takes one of the
        /// <see cref="FuzzSectionMode"/> shapes, with randomized voxels, light, heightmap and queue depths.
        /// </summary>
        /// <param name="pos">Chunk position.</param>
        /// <param name="rng">The seeded random source (shared across the sweep, so chunks differ).</param>
        /// <returns>A pooled <see cref="ChunkData"/> the caller must return to the pool.</returns>
        private static ChunkData BuildFuzzChunk(Vector2Int pos, Random rng)
        {
            ChunkData data = World.Instance.ChunkPool.GetChunkData(pos);

            for (int i = 0; i < data.heightMap.Length; i++)
                data.heightMap[i] = (ushort)rng.Next(0, VoxelData.ChunkHeight);

            if (rng.Next(2) == 0) data.FlagInitialLighting();

            for (int s = 0; s < data.sections.Length; s++)
            {
                FuzzSectionMode mode = (FuzzSectionMode)rng.Next(0, 5);
                switch (mode)
                {
                    case FuzzSectionMode.Absent:
                        break;

                    case FuzzSectionMode.VoxelsUniformSky:
                        FuzzVoxels(data, s, rng);
                        // Sky byte AFTER the voxel writes: SetVoxel promotes a compact section back to full light.
                        data.SectionUniformSkyLevel[s] = (byte)rng.Next(1, 16);
                        break;

                    case FuzzSectionMode.VoxelsAndLight:
                        FuzzVoxels(data, s, rng);
                        FuzzLight(data.sections[s], rng);
                        break;

                    case FuzzSectionMode.LightOnlyUniformSky:
                        data.SectionUniformSkyLevel[s] = (byte)rng.Next(1, 16);
                        break;

                    case FuzzSectionMode.LightOnlyFull:
                        ChunkSection lightOnly = World.Instance.ChunkPool.GetChunkSection();
                        FuzzLight(lightOnly, rng);
                        data.sections[s] = lightOnly;
                        break;
                }
            }

            FuzzQueue(data.SkylightBfsQueue, rng);
            FuzzQueue(data.BlocklightBfsQueue, rng);
            return data;
        }

        /// <summary>Writes a random scattering of voxels into one section (creating it via the real setter).</summary>
        /// <param name="data">The chunk being built.</param>
        /// <param name="sectionIndex">The section slot to populate.</param>
        /// <param name="rng">The seeded random source.</param>
        private static void FuzzVoxels(ChunkData data, int sectionIndex, Random rng)
        {
            int count = rng.Next(1, 24);
            int baseY = sectionIndex * ChunkMath.SECTION_SIZE;
            for (int i = 0; i < count; i++)
            {
                int x = rng.Next(ChunkMath.SECTION_SIZE);
                int z = rng.Next(ChunkMath.SECTION_SIZE);
                int y = baseY + rng.Next(ChunkMath.SECTION_SIZE);
                ushort id = (ushort)rng.Next(1, 0x0400);
                data.SetVoxel(x, y, z, BurstVoxelDataBitMapping.PackVoxelData(id, (byte)rng.Next(256)));
            }
        }

        /// <summary>Fills a section's light with randomized sky + blocklight (never uniformly zero).</summary>
        /// <param name="section">The section to stamp.</param>
        /// <param name="rng">The seeded random source.</param>
        private static void FuzzLight(ChunkSection section, Random rng)
        {
            for (int i = 0; i < section.LightData.Length; i++)
            {
                section.LightData[i] = LightBitMapping.PackLightData(
                    (byte)rng.Next(16), (byte)rng.Next(16), (byte)rng.Next(16), (byte)rng.Next(16));
            }
        }

        /// <summary>Enqueues a random number of random nodes onto one BFS queue.</summary>
        /// <param name="queue">The queue to seed.</param>
        /// <param name="rng">The seeded random source.</param>
        private static void FuzzQueue(Queue<LightQueueNode> queue, Random rng)
        {
            int count = rng.Next(0, FUZZ_MAX_QUEUE_NODES);
            for (int i = 0; i < count; i++)
            {
                queue.Enqueue(new LightQueueNode
                {
                    Position = new Vector3Int(rng.Next(16), rng.Next(VoxelData.ChunkHeight), rng.Next(16)),
                    OldLightLevel = (byte)rng.Next(16),
                    OldBlockR = (byte)rng.Next(16),
                    OldBlockG = (byte)rng.Next(16),
                    OldBlockB = (byte)rng.Next(16),
                });
            }
        }

        // --- Assertions --------------------------------------------------------------------------

        /// <summary>
        /// Compares two chunks through the public accessors — the semantic identity that matters, rather than
        /// raw array equality (a compact-sky section and a full-LightData section holding the same values are
        /// legitimately different in memory and identical in meaning).
        /// </summary>
        /// <param name="expected">The source chunk.</param>
        /// <param name="actual">The reloaded chunk.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when every persisted field matches.</returns>
        private static bool AssertChunksEquivalent(ChunkData expected, ChunkData actual, string label)
        {
            bool ok = Check($"{label}: NeedsInitialLighting matches (expected {expected.NeedsInitialLighting.ToString()}, got {actual.NeedsInitialLighting.ToString()})",
                expected.NeedsInitialLighting == actual.NeedsInitialLighting);

            ok &= AssertHeightmapsEqual(expected, actual, label);
            ok &= AssertVoxelsAndLightEqual(expected, actual, label);
            ok &= AssertQueuesEqual(expected.SkylightBfsQueue, actual.SkylightBfsQueue, $"{label}: skylight queue");
            ok &= AssertQueuesEqual(expected.BlocklightBfsQueue, actual.BlocklightBfsQueue, $"{label}: blocklight queue");
            return ok;
        }

        /// <summary>Compares the two heightmaps element-wise, reporting bounded diffs.</summary>
        /// <param name="expected">The source chunk.</param>
        /// <param name="actual">The reloaded chunk.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when the heightmaps match.</returns>
        private static bool AssertHeightmapsEqual(ChunkData expected, ChunkData actual, string label)
        {
            int mismatches = 0;
            StringBuilder diff = new StringBuilder();
            for (int i = 0; i < expected.heightMap.Length; i++)
            {
                if (expected.heightMap[i] == actual.heightMap[i]) continue;
                mismatches++;
                if (mismatches <= MAX_REPORTED_DIFFS)
                    diff.Append($"\n    [{i.ToString()}] expected {expected.heightMap[i].ToString()}, got {actual.heightMap[i].ToString()}");
            }

            return Check($"{label}: heightmap matches ({mismatches.ToString()} mismatching columns){diff}", mismatches == 0);
        }

        /// <summary>
        /// Compares every voxel and every per-voxel light value in the chunk, reporting bounded diffs.
        /// </summary>
        /// <param name="expected">The source chunk.</param>
        /// <param name="actual">The reloaded chunk.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when both fields match at every position.</returns>
        private static bool AssertVoxelsAndLightEqual(ChunkData expected, ChunkData actual, string label)
        {
            int voxelMismatches = 0, lightMismatches = 0;
            StringBuilder voxelDiff = new StringBuilder();
            StringBuilder lightDiff = new StringBuilder();

            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            {
                for (int z = 0; z < VoxelData.ChunkWidth; z++)
                {
                    for (int x = 0; x < VoxelData.ChunkWidth; x++)
                    {
                        uint expectedVoxel = expected.GetVoxel(x, y, z);
                        uint actualVoxel = actual.GetVoxel(x, y, z);
                        if (expectedVoxel != actualVoxel)
                        {
                            voxelMismatches++;
                            if (voxelMismatches <= MAX_REPORTED_DIFFS)
                                voxelDiff.Append($"\n    ({x.ToString()},{y.ToString()},{z.ToString()}) expected 0x{expectedVoxel:X8}, got 0x{actualVoxel:X8}");
                        }

                        ushort expectedLight = expected.GetLightData(x, y, z);
                        ushort actualLight = actual.GetLightData(x, y, z);
                        if (expectedLight != actualLight)
                        {
                            lightMismatches++;
                            if (lightMismatches <= MAX_REPORTED_DIFFS)
                                lightDiff.Append($"\n    ({x.ToString()},{y.ToString()},{z.ToString()}) expected 0x{expectedLight:X4}, got 0x{actualLight:X4}");
                        }
                    }
                }
            }

            bool ok = Check($"{label}: voxels match ({voxelMismatches.ToString()} mismatches){voxelDiff}", voxelMismatches == 0);
            ok &= Check($"{label}: per-voxel light matches ({lightMismatches.ToString()} mismatches){lightDiff}", lightMismatches == 0);
            return ok;
        }

        /// <summary>Compares two BFS light queues by contents AND order (the replay order is load-bearing).</summary>
        /// <param name="expected">The source queue.</param>
        /// <param name="actual">The reloaded queue.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when both queues hold the same nodes in the same order.</returns>
        private static bool AssertQueuesEqual(
            Queue<LightQueueNode> expected,
            Queue<LightQueueNode> actual,
            string label)
        {
            LightQueueNode[] expectedNodes = expected.ToArray();
            LightQueueNode[] actualNodes = actual.ToArray();

            if (expectedNodes.Length != actualNodes.Length)
                return Check($"{label}: node count matches (expected {expectedNodes.Length.ToString()}, got {actualNodes.Length.ToString()})", false);

            int mismatches = 0;
            StringBuilder diff = new StringBuilder();
            for (int i = 0; i < expectedNodes.Length; i++)
            {
                if (expectedNodes[i].Equals(actualNodes[i])) continue;
                mismatches++;
                if (mismatches <= MAX_REPORTED_DIFFS)
                    diff.Append($"\n    [{i.ToString()}] expected {DescribeNode(expectedNodes[i])}, got {DescribeNode(actualNodes[i])}");
            }

            return Check($"{label}: {expectedNodes.Length.ToString()} node(s) match in order ({mismatches.ToString()} mismatches){diff}", mismatches == 0);
        }

        /// <summary>Renders a light-queue node for a failure line.</summary>
        /// <param name="node">The node to describe.</param>
        /// <returns>A compact "pos level/r/g/b" rendering.</returns>
        private static string DescribeNode(LightQueueNode node) =>
            $"{node.Position.ToString()} {node.OldLightLevel.ToString()}/{node.OldBlockR.ToString()}/{node.OldBlockG.ToString()}/{node.OldBlockB.ToString()}";

        /// <summary>Compares two payloads byte for byte, reporting the first divergence.</summary>
        /// <param name="expected">The first payload.</param>
        /// <param name="actual">The second payload.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when the payloads are identical.</returns>
        private static bool AssertPayloadsIdentical(byte[] expected, byte[] actual, string label)
        {
            if (expected.Length != actual.Length)
                return Check($"{label}: length matches (expected {expected.Length.ToString()}, got {actual.Length.ToString()})", false);

            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] == actual[i]) continue;
                return Check($"{label}: bytes match (first divergence at offset {i.ToString()}: expected 0x{expected[i]:X2}, got 0x{actual[i]:X2})", false);
            }

            return Check($"{label}: bytes match ({expected.Length.ToString()} bytes)", true);
        }
    }
}
