using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Data;
using Editor.Validation.Framework;
using Helpers;
using Jobs.BurstData;
using Serialization;
using UnityEngine;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Fixture half of the serialization round-trip suite: the stub-world fixture, the chunk builder that
    /// can express every section-flag class the v7 layout defines, and the independent payload parser the
    /// flag-classification assertions compare against.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        // --- Fixture -----------------------------------------------------------------------------

        /// <summary>Suite fixture: the shared <see cref="StorageValidationFixture"/> (stub
        /// <c>World.Instance</c> + volatile-path storage + all-seam disarm) under this suite's prefix.</summary>
        private sealed class Fixture : StorageValidationFixture
        {
            public Fixture() : base("SerializationRoundTripTest")
            {
            }
        }

        /// <summary>Snapshot of the concurrent data/section pools' active counts, for leak balance checks.</summary>
        private readonly struct PoolBalance
        {
            private readonly int _activeData;
            private readonly int _activeSections;

            /// <summary>Captures the current active counts.</summary>
            public static PoolBalance Capture() => new PoolBalance(
                World.Instance.ChunkPool.ActiveData, World.Instance.ChunkPool.ActiveSections);

            private PoolBalance(int activeData, int activeSections)
            {
                _activeData = activeData;
                _activeSections = activeSections;
            }

            /// <summary>Asserts the active counts match this snapshot (nothing leaked, nothing over-returned).</summary>
            /// <param name="label">Assertion label for the log.</param>
            /// <returns>True when both pools are balanced.</returns>
            public bool AssertUnchanged(string label)
            {
                PoolBalance now = Capture();
                return Check(
                    $"{label} (data {_activeData.ToString()}→{now._activeData.ToString()}, sections {_activeSections.ToString()}→{now._activeSections.ToString()})",
                    now._activeData == _activeData && now._activeSections == _activeSections);
            }
        }

        // --- Fixture palette ---------------------------------------------------------------------

        // Test-local voxel ids, NOT BlockIDs constants. The suite pins serialized BYTES (the part-2 golden
        // hash) and byte-level section classification, so the fixture must be independent of
        // BlockDatabase.asset: a `Generate Block IDs` run that shifts a real id would otherwise move the
        // golden hash and red this suite for a reason that has nothing to do with the save format. This is
        // the same test-local-palette exemption the lighting/meshing suites take; these numbers deliberately
        // identify no real block.
        private const ushort FIXTURE_SOLID_ID = 0x0101;
        private const ushort FIXTURE_ALT_ID = 0x0202;
        private const byte FIXTURE_META = 0x05;

        /// <summary>Uniform sky level stamped on the flag-0x00 section (voxels + compact sky).</summary>
        private const byte UNIFORM_SKY_VOXEL_SECTION = 7;

        /// <summary>Uniform sky level stamped on the flag-0x02 section (compact light-only, no voxels).</summary>
        private const byte UNIFORM_SKY_LIGHT_ONLY_SECTION = 11;

        // Section slots of the reference fixture chunk, one per outcome the writer can produce.
        private const int SECTION_VOXELS_UNIFORM_SKY = 0; // → flag 0x00
        private const int SECTION_VOXELS_AND_LIGHT = 1; // → flag 0x01
        private const int SECTION_LIGHT_ONLY_UNIFORM = 2; // → flag 0x02
        private const int SECTION_LIGHT_ONLY_FULL = 3; // → flag 0x03
        private const int SECTION_EMPTY_ATTACHED = 4; // attached but data-less → excluded from the bitmask
        private const int SECTION_NULL = 5; // never attached → excluded from the bitmask

        /// <summary>The section flags the reference chunk must serialize to, indexed by section slot;
        /// <see cref="FLAG_ABSENT"/> marks a slot that must be excluded from the section bitmask.</summary>
        private static readonly byte[] s_expectedFixtureFlags =
        {
            0x00, 0x01, 0x02, 0x03, FLAG_ABSENT, FLAG_ABSENT, FLAG_ABSENT, FLAG_ABSENT,
        };

        /// <summary>Sentinel in a parsed flag map for "this section was not present in the bitmask".</summary>
        private const byte FLAG_ABSENT = 0xFF;

        // --- Fixture chunk builder ---------------------------------------------------------------

        /// <summary>
        /// Builds the reference fixture chunk: one section per flag class the v7 writer can emit, two
        /// sections that must be excluded from the bitmask (attached-but-empty, and never attached), a
        /// heightmap with distinct per-column values, both light queues populated, and
        /// <c>NeedsInitialLighting</c> set — so a single payload exercises every branch of
        /// <c>WriteChunkInternal</c>/<c>ReadChunkInternal</c>.
        /// </summary>
        /// <param name="pos">Chunk position (voxel-space chunk coordinate, as stored on disk).</param>
        /// <returns>A pooled <see cref="ChunkData"/> the caller must return to the pool.</returns>
        private static ChunkData BuildReferenceChunk(Vector2Int pos)
        {
            ChunkData data = World.Instance.ChunkPool.GetChunkData(pos);

            // Heightmap: distinct per column, so a partially-copied or transposed heightmap is visible.
            for (int i = 0; i < data.heightMap.Length; i++)
                data.heightMap[i] = (ushort)(40 + (i % 61));

            data.NeedsInitialLighting = true;

            // 0x00 — voxels + compact uniform sky. Voxels FIRST: SetVoxel promotes a compact section back
            // to full LightData, so stamping the sky byte before the writes would be undone.
            WriteVoxelPattern(data, SECTION_VOXELS_UNIFORM_SKY, FIXTURE_SOLID_ID);
            data.SectionUniformSkyLevel[SECTION_VOXELS_UNIFORM_SKY] = UNIFORM_SKY_VOXEL_SECTION;

            // 0x01 — voxels + full LightData (blocklight present, so it cannot compact to a sky byte).
            WriteVoxelPattern(data, SECTION_VOXELS_AND_LIGHT, FIXTURE_ALT_ID);
            StampVariedLight(data.sections[SECTION_VOXELS_AND_LIGHT]);

            // 0x02 — compact light-only: no section object at all, just the sky byte.
            data.SectionUniformSkyLevel[SECTION_LIGHT_ONLY_UNIFORM] = UNIFORM_SKY_LIGHT_ONLY_SECTION;

            // 0x03 — light-only with full LightData: an attached section carrying no voxels.
            ChunkSection lightOnly = World.Instance.ChunkPool.GetChunkSection();
            StampVariedLight(lightOnly);
            data.sections[SECTION_LIGHT_ONLY_FULL] = lightOnly;

            // Attached but data-less (no voxels, all-zero light) — must be excluded from the bitmask.
            data.sections[SECTION_EMPTY_ATTACHED] = World.Instance.ChunkPool.GetChunkSection();

            // SECTION_NULL and the remaining slots stay null.

            SeedLightQueues(data);
            return data;
        }

        /// <summary>Number of voxels written into each populated fixture section.</summary>
        private const int FIXTURE_VOXELS_PER_SECTION = 9;

        /// <summary>Writes a small deterministic voxel pattern into one section of the fixture chunk.</summary>
        /// <param name="data">The chunk being built.</param>
        /// <param name="sectionIndex">The section slot to populate.</param>
        /// <param name="blockId">The test-local voxel id to write.</param>
        private static void WriteVoxelPattern(ChunkData data, int sectionIndex, ushort blockId)
        {
            int baseY = sectionIndex * ChunkMath.SECTION_SIZE;
            for (int i = 0; i < FIXTURE_VOXELS_PER_SECTION; i++)
            {
                int x = (i * 3) % ChunkMath.SECTION_SIZE;
                int z = (i * 5) % ChunkMath.SECTION_SIZE;
                int y = baseY + (i % ChunkMath.SECTION_SIZE);
                data.SetVoxel(x, y, z, BurstVoxelDataBitMapping.PackVoxelData(blockId, FIXTURE_META));
            }
        }

        /// <summary>
        /// Fills a section's <see cref="ChunkSection.LightData"/> with a varied sky level plus blocklight, so
        /// the writer classifies it as "needs full LightData" (flag 0x01/0x03) rather than compacting it.
        /// </summary>
        /// <param name="section">The section to stamp.</param>
        private static void StampVariedLight(ChunkSection section)
        {
            for (int i = 0; i < section.LightData.Length; i++)
            {
                byte sky = (byte)(i % 16);
                byte r = (byte)((i / 16) % 16);
                byte g = (byte)((i / 32) % 16);
                byte b = (byte)((i / 64) % 16);
                section.LightData[i] = LightBitMapping.PackLightData(sky, r, g, b);
            }
        }

        /// <summary>Number of nodes seeded into each of the two BFS light queues.</summary>
        private const int FIXTURE_QUEUE_NODES = 5;

        /// <summary>Seeds both BFS light queues with deterministic, mutually distinct nodes.</summary>
        /// <param name="data">The chunk being built.</param>
        private static void SeedLightQueues(ChunkData data)
        {
            for (int i = 0; i < FIXTURE_QUEUE_NODES; i++)
            {
                data.SunlightBfsQueue.Enqueue(new LightQueueNode
                {
                    Position = new Vector3Int(i, 20 + i, 15 - i),
                    OldLightLevel = (byte)(i + 1),
                    OldBlockR = (byte)(i + 2),
                    OldBlockG = (byte)(i + 3),
                    OldBlockB = (byte)(i + 4),
                });
                data.BlocklightBfsQueue.Enqueue(new LightQueueNode
                {
                    Position = new Vector3Int(15 - i, 60 + i, i),
                    OldLightLevel = (byte)(i + 5),
                    OldBlockR = (byte)(i + 6),
                    OldBlockG = (byte)(i + 7),
                    OldBlockB = (byte)(i + 8),
                });
            }
        }

        // --- Payload helpers ---------------------------------------------------------------------

        /// <summary>
        /// Serializes a chunk with <see cref="CompressionAlgorithm.None"/> and returns the exact bytes
        /// (a defensive copy — safe to corrupt, hash, or parse; the pooled buffer is returned immediately).
        /// </summary>
        /// <param name="data">The chunk to serialize.</param>
        /// <returns>The exact serialized payload.</returns>
        private static byte[] SerializeUncompressed(ChunkData data) => Serialize(data, CompressionAlgorithm.None);

        /// <summary>
        /// Serializes a chunk with the given algorithm and returns the exact bytes (a defensive copy — the
        /// pooled buffer is returned immediately).
        /// </summary>
        /// <param name="data">The chunk to serialize.</param>
        /// <param name="algorithm">The compression algorithm to encode with.</param>
        /// <returns>The exact serialized payload.</returns>
        private static byte[] Serialize(ChunkData data, CompressionAlgorithm algorithm)
        {
            byte[] buffer = SerializationBufferPool.Get();
            try
            {
                int length = ChunkSerializer.Serialize(data, buffer, algorithm);
                byte[] payload = new byte[length];
                Array.Copy(buffer, payload, length);
                return payload;
            }
            finally
            {
                SerializationBufferPool.Return(buffer);
            }
        }

        // v7 layout constants for the independent payload parser below. These deliberately RE-STATE the
        // layout rather than calling into ChunkSerializer: a parser that reused the reader would share any
        // fault the reader has, and the flag map would then be a tautology instead of an oracle.
        private const int PAYLOAD_HEADER_BYTES = 1 + 4 + 4 + 1; // version + x + z + needsLight
        private const int PAYLOAD_HEIGHTMAP_BYTES = VoxelData.ChunkWidth * VoxelData.ChunkWidth * sizeof(ushort);
        private const int PAYLOAD_VOXEL_BYTES = ChunkMath.SECTION_VOLUME * sizeof(uint);
        private const int PAYLOAD_LIGHT_BYTES = ChunkMath.SECTION_VOLUME * sizeof(ushort);
        private const int PAYLOAD_NON_AIR_BYTES = sizeof(ushort);
        private const int PAYLOAD_SKY_BYTES = 1;

        /// <summary>
        /// Walks an uncompressed payload and returns the section flag actually written for each section
        /// slot, or <see cref="FLAG_ABSENT"/> where the slot was excluded from the section bitmask.
        /// <para>This is the oracle for the flag-classification assertions: an accessor-level round-trip
        /// compare cannot see WHICH encoding the writer chose, so a regression that emitted every section
        /// as flag 0x01 would round-trip perfectly and still bloat every save on disk.</para>
        /// </summary>
        /// <param name="payload">An uncompressed (<see cref="CompressionAlgorithm.None"/>) chunk payload.</param>
        /// <param name="sectionCount">The number of section slots the chunk carries.</param>
        /// <returns>One flag byte per section slot.</returns>
        private static byte[] ParseSectionFlags(byte[] payload, int sectionCount)
        {
            byte[] flags = new byte[sectionCount];
            for (int i = 0; i < sectionCount; i++) flags[i] = FLAG_ABSENT;

            int offset = PAYLOAD_HEADER_BYTES + PAYLOAD_HEIGHTMAP_BYTES;
            int bitmask = BitConverter.ToInt32(payload, offset);
            offset += sizeof(int);

            for (int i = 0; i < sectionCount; i++)
            {
                if ((bitmask & (1 << i)) == 0) continue;

                byte flag = payload[offset];
                offset += 1;
                flags[i] = flag;

                switch (flag)
                {
                    case 0x00: offset += PAYLOAD_SKY_BYTES + PAYLOAD_NON_AIR_BYTES + PAYLOAD_VOXEL_BYTES; break;
                    case 0x01: offset += PAYLOAD_NON_AIR_BYTES + PAYLOAD_VOXEL_BYTES + PAYLOAD_LIGHT_BYTES; break;
                    case 0x02: offset += PAYLOAD_SKY_BYTES; break;
                    case 0x03: offset += PAYLOAD_LIGHT_BYTES; break;
                    default: throw new InvalidOperationException($"Unknown section flag 0x{flag:X2} at section {i.ToString()}");
                }
            }

            return flags;
        }

        /// <summary>Formats a flag map for a failure line (absent slots as "--").</summary>
        /// <param name="flags">The flag map to format.</param>
        /// <returns>A compact "[00 01 -- ...]" rendering.</returns>
        private static string FormatFlags(IReadOnlyList<byte> flags)
        {
            string[] parts = new string[flags.Count];
            for (int i = 0; i < flags.Count; i++)
                parts[i] = flags[i] == FLAG_ABSENT ? "--" : $"{flags[i]:X2}";
            return $"[{string.Join(" ", parts)}]";
        }

        /// <summary>Runs <see cref="ChunkStorageManager.SaveChunkAsync"/> to completion. Wrapped in
        /// <see cref="Task.Run(Func{Task})"/> so its continuations resume on the ThreadPool instead of being
        /// posted to the (blocked) editor main thread — blocking directly would deadlock.</summary>
        /// <param name="storage">The storage manager to save through.</param>
        /// <param name="data">The chunk to save.</param>
        /// <returns>The save outcome.</returns>
        private static ChunkSaveResult RunSave(ChunkStorageManager storage, ChunkData data) =>
            Task.Run(() => storage.SaveChunkAsync(data)).GetAwaiter().GetResult();

        /// <summary>Runs <see cref="ChunkStorageManager.LoadChunkAsync"/> to completion (same wrapping as <see cref="RunSave"/>).</summary>
        /// <param name="storage">The storage manager to load through.</param>
        /// <param name="pos">The chunk position to load.</param>
        /// <returns>The loaded chunk, or null when it is not on disk.</returns>
        private static ChunkData RunLoad(ChunkStorageManager storage, Vector2Int pos) =>
            Task.Run(() => storage.LoadChunkAsync(pos)).GetAwaiter().GetResult();

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        /// <param name="label">The assertion label.</param>
        /// <param name="condition">The asserted condition.</param>
        /// <returns><paramref name="condition"/>, unchanged.</returns>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }
    }
}
