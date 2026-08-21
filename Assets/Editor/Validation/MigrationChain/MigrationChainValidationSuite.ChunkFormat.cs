using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Data;
using Editor.Validation.Framework;
using Helpers;
using Jobs.BurstData;
using Serialization;
using Serialization.Migration;
using Serialization.Migration.Steps;
using UnityEngine;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// The chunk-payload half of roadmap NS-7b: the five migration steps that rewrite chunk bytes
    /// (v2→v3, v5→v6, v7→v8, v8→v9, v9→v10 — including all three of the high-risk rewrites), driven from an
    /// authored chunk-format v1/v2 fixture.
    /// <para>
    /// The decisive assertion is <b>B16</b>: after the chain, the payload is handed to the real
    /// <see cref="ChunkSerializer.Deserialize"/> and the seeded values must be recoverable from the resulting
    /// <see cref="ChunkData"/>. That makes the production reader the oracle for the chain's output, so a
    /// one-byte misalignment anywhere in the fixture or the chain corrupts a value rather than passing quietly.
    /// </para>
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        /// <summary>Chunk format the chain must terminate at (<c>ChunkSerializer.CURRENT_CHUNK_VERSION</c>).</summary>
        private const byte EXPECTED_FINAL_CHUNK_VERSION = 7;

        /// <summary>Legacy light mask v9→v10 must clear from every voxel word (bits 16–23).</summary>
        private const uint LEGACY_LIGHT_BITS = 0x00FF0000u;

        /// <summary>Sky nibble position inside a v6+ <c>LightData</c> entry (v8→v9's <c>SUN_SHIFT</c>).</summary>
        private const int LIGHTDATA_SKY_SHIFT = 0;

        /// <summary>Red nibble position inside a <c>LightData</c> entry (v8→v9's <c>BLOCK_R_SHIFT</c>).</summary>
        private const int LIGHTDATA_R_SHIFT = 4;

        /// <summary>Green nibble position inside a <c>LightData</c> entry.</summary>
        private const int LIGHTDATA_G_SHIFT = 8;

        /// <summary>Blue nibble position inside a <c>LightData</c> entry.</summary>
        private const int LIGHTDATA_B_SHIFT = 12;

        /// <summary>Byte offset of the section bitmask in an era-v2 payload: header(10) + heightmap(512).</summary>
        private const int FIXTURE_BITMASK_OFFSET = 10 + ERA_V2_HEIGHTMAP_BYTES;

        /// <summary>Byte offset of section 0's non-air count: past the bitmask(4) and the section version byte(1).</summary>
        private const int FIXTURE_SECTION0_NON_AIR_OFFSET = FIXTURE_BITMASK_OFFSET + 4 + 1;

        /// <summary>
        /// Golden SHA-256 of the era-v2 fixture payload. Empty string = capture mode.
        /// Re-pinned when the fixture began stamping the voxel-space origin (index × ChunkWidth) rather than
        /// the raw chunk index, matching what on-disk era saves actually store.
        /// </summary>
        private const string GOLDEN_V2_FIXTURE_HASH =
            "2e76a440dad6332ef3f8921852a49d8c51b8aad6e1f9c5e2b52ceb122b6203ae";

        // --- Chain driver ---------------------------------------------------------------------------

        /// <summary>
        /// Applies the chunk-format steps in registration order using the manager's own rule — call
        /// <c>MigrateChunk</c> only when the step declares a chunk-format target above the payload's current
        /// version byte, re-reading that byte between steps.
        /// <para>
        /// This mirrors <c>MigrationManager.MigrateSingleRegion</c>'s inner loop rather than calling it,
        /// because the production loop is buried inside a per-region file walk and cannot report per-step
        /// state. The production loop itself is covered end-to-end by <b>B21</b> (a real v2 world through
        /// <c>RunAOTMigrationAsync</c>), so nothing here is the only evidence for it.
        /// </para>
        /// </summary>
        /// <param name="payload">The starting payload; not mutated.</param>
        /// <param name="observations">Optional sink; one entry appended per step that ran. A collected record
        /// rather than a callback on purpose — a caller-supplied closure would have to assert from inside this
        /// method's execution, which only works while the invocation stays synchronous and hides that dependency
        /// from the type. Recording lets the caller assert after the chain has finished.</param>
        /// <returns>The payload after every applicable chunk-format step.</returns>
        private static byte[] RunChunkFormatChain(
            byte[] payload,
            List<ChunkStepObservation> observations = null)
        {
            byte[] current = payload;
            List<WorldMigrationStep> path = new MigrationManager().GetRequiredMigrations(1);

            foreach (WorldMigrationStep step in path)
            {
                if (!step.TargetChunkFormatVersion.HasValue) continue;

                // Read once inside the HasValue guard, so the declared version travels as a plain byte.
                byte declared = step.TargetChunkFormatVersion.Value;
                if (current[0] >= declared) continue;

                current = step.MigrateChunk(current);
                observations?.Add(new ChunkStepObservation(step.GetType().Name, declared, current[0]));
            }

            return current;
        }

        /// <summary>One step's observed version transition, captured by the chain driver for later assertion.</summary>
        private readonly struct ChunkStepObservation
        {
            /// <summary>Type name of the step that ran.</summary>
            public readonly string StepName;

            /// <summary>Chunk format the step declares via <c>TargetChunkFormatVersion</c>.</summary>
            public readonly byte DeclaredVersion;

            /// <summary>Version byte the step actually wrote at offset 0.</summary>
            public readonly byte ObservedVersion;

            /// <summary>Records one transition.</summary>
            /// <param name="stepName">Type name of the step that ran.</param>
            /// <param name="declaredVersion">The step's declared target chunk format.</param>
            /// <param name="observedVersion">The version byte it actually wrote.</param>
            public ChunkStepObservation(string stepName, byte declaredVersion, byte observedVersion)
            {
                StepName = stepName;
                DeclaredVersion = declaredVersion;
                ObservedVersion = observedVersion;
            }
        }

        /// <summary>
        /// Runs the chain and deserializes the result through the production reader.
        /// </summary>
        /// <param name="eraVersion">Chunk format of the starting fixture (1 or 2).</param>
        /// <returns>The deserialized chunk, or null if the reader rejected the migrated payload.</returns>
        private static ChunkData MigrateAndDeserialize(byte eraVersion)
        {
            byte[] migrated = RunChunkFormatChain(BuildHistoricalChunkPayload(eraVersion));
            return ChunkSerializer.Deserialize(
                migrated, CompressionAlgorithm.None,
                new Vector2Int(FIXTURE_CHUNK_X * ERA_CHUNK_WIDTH, FIXTURE_CHUNK_Z * ERA_CHUNK_WIDTH));
        }

        // --- Scenarios ------------------------------------------------------------------------------

        /// <summary>B14. Red when: the authored fixture stops matching the layout the steps read, or is edited
        /// into something degenerate. Two-sided and non-vacuous — the payload length must equal what the layout
        /// arithmetic predicts, its bitmask and non-air count must be non-zero (an empty chunk would sail
        /// through every step and prove nothing), the golden hash must match, and a truncated variant must be
        /// REJECTED by the first step rather than silently migrated.</summary>
        private static bool ChunkFixtureIntegrity()
        {
            byte[] v2 = BuildHistoricalChunkPayload(2);

            // header(1+4+4+1) + heightmap(512) + bitmask(4) + 8×(1+2+16384) + queues
            const int expectedSections = FIXTURE_SECTION_COUNT * (1 + 2 + ERA_SECTION_VOXEL_BYTES);
            const int expectedQueues = 4 + FIXTURE_SUN_QUEUE_ENTRIES * ERA_LIGHT_ENTRY_BYTES
                                         + 4 + FIXTURE_BLOCK_QUEUE_ENTRIES * ERA_LIGHT_ENTRY_BYTES;
            const int expectedLength = 10 + ERA_V2_HEIGHTMAP_BYTES + 4 + expectedSections + expectedQueues;

            bool ok = Check($"the era-v2 payload is exactly the length the layout predicts ({expectedLength.ToString()}), got {v2.Length.ToString()}",
                v2.Length == expectedLength);
            ok &= Check("the payload declares chunk format 2", v2[0] == 2);
            // Read the bitmask and section-0 non-air count back OUT of the payload. Comparing the constants to
            // each other would be a tautology that never inspects the bytes — the exact vacuous pass this
            // scenario exists to prevent.
            int bitmask = System.BitConverter.ToInt32(v2, FIXTURE_BITMASK_OFFSET);
            ushort nonAir = System.BitConverter.ToUInt16(v2, FIXTURE_SECTION0_NON_AIR_OFFSET);
            ok &= Check($"the payload's section bitmask marks all {FIXTURE_SECTION_COUNT.ToString()} sections, got 0x{bitmask:X2}",
                bitmask == (1 << FIXTURE_SECTION_COUNT) - 1);
            ok &= Check($"the payload's section 0 declares {FIXTURE_NON_AIR_COUNT.ToString()} non-air voxels (non-vacuity), got {nonAir.ToString()}",
                nonAir == FIXTURE_NON_AIR_COUNT);

            byte[] v1 = BuildHistoricalChunkPayload(1);
            ok &= Check($"the era-v1 payload is 256 bytes shorter (byte-per-column heightmap), got {(v2.Length - v1.Length).ToString()}",
                v2.Length - v1.Length == ERA_V2_HEIGHTMAP_BYTES - ERA_V1_HEIGHTMAP_BYTES);

            ok &= GoldenMaster.AssertOrCapture("B14 era-v2 fixture bytes", HashPayload(v2), GOLDEN_V2_FIXTURE_HASH);

            // The other side: a truncated fixture must not survive the first step.
            byte[] truncated = new byte[v2.Length / 2];
            System.Array.Copy(v2, truncated, truncated.Length);
            bool rejected;
            try
            {
                RunChunkFormatChain(truncated);
                rejected = false;
            }
            catch (System.Exception)
            {
                rejected = true;
            }

            ok &= Check("a truncated payload is rejected by the chain, not silently migrated", rejected);
            return ok;
        }

        /// <summary>B15. Red when: a step stops writing its declared version byte as byte 0, or the chain stops
        /// composing (a step that no longer advances the version would leave a later step skipping its work).
        /// Asserts the postcondition the manager's own fail-fast guard enforces, for every step in turn.</summary>
        private static bool ChunkChainPerStepVersions()
        {
            List<ChunkStepObservation> observed = new List<ChunkStepObservation>();
            byte[] final = RunChunkFormatChain(BuildHistoricalChunkPayload(2), observed);

            bool ok = Check($"all five chunk-format steps ran, got {observed.Count.ToString()}",
                observed.Count == 5);

            foreach (ChunkStepObservation step in observed)
            {
                ok &= Check($"{step.StepName} writes its declared version {step.DeclaredVersion.ToString()}, got {step.ObservedVersion.ToString()}",
                    step.ObservedVersion == step.DeclaredVersion);
            }

            ok &= Check($"the chain terminates at the current chunk format {EXPECTED_FINAL_CHUNK_VERSION.ToString()}, got {final[0].ToString()}",
                final[0] == EXPECTED_FINAL_CHUNK_VERSION);
            return ok;
        }

        /// <summary>B16. Red when: any step in the chain misaligns the byte stream, or the schema-meta rewrite
        /// stops routing through <c>ConvertLegacyMeta</c>. The migrated payload is read by the REAL
        /// <see cref="ChunkSerializer.Deserialize"/> — an independent oracle for the output — and the seeded
        /// probes must come back exactly. Expected metas are hand-derived from the documented schema mapping,
        /// not from the converter, so this does not merely re-assert the function against itself.</summary>
        private static bool ChunkChainOutputIsReadable()
        {
            using MigrationFixture fx = new MigrationFixture();
            ChunkData chunk = MigrateAndDeserialize(2);

            bool ok = Check("the migrated payload deserializes through the production reader", chunk != null);
            if (chunk == null) return false;

            try
            {
                // The label used to claim 8 sections while the predicate accepted 1 — so a chain step that
                // dropped sections 1-7 would log a PASS, and every later assertion only reads section 0.
                int populated = 0;
                if (chunk.sections != null)
                {
                    foreach (ChunkSection section in chunk.sections)
                        if (section != null)
                            populated++;
                }

                ok &= Check($"the chunk carries {FIXTURE_SECTION_COUNT.ToString()} section slots, got {(chunk.sections?.Length ?? -1).ToString()}",
                    chunk.sections != null && chunk.sections.Length == FIXTURE_SECTION_COUNT);
                ok &= Check($"all {FIXTURE_SECTION_COUNT.ToString()} seeded sections survived the chain, got {populated.ToString()}",
                    populated == FIXTURE_SECTION_COUNT);

                uint[] voxels = chunk.sections[0]?.voxels;
                ok &= Check("section 0 has a voxel array", voxels != null);
                if (voxels == null) return false;

                // Ids must survive every rewrite untouched.
                ok &= Check($"the Facade id survives, got {BurstVoxelDataBitMapping.GetId(voxels[SLOT_FACADE]).ToString()}",
                    BurstVoxelDataBitMapping.GetId(voxels[SLOT_FACADE]) == V5_ID_FACADE);
                ok &= Check($"the OakLog id survives, got {BurstVoxelDataBitMapping.GetId(voxels[SLOT_OAK_LOG]).ToString()}",
                    BurstVoxelDataBitMapping.GetId(voxels[SLOT_OAK_LOG]) == V5_ID_OAK_LOG);
                ok &= Check($"the Water id survives, got {BurstVoxelDataBitMapping.GetId(voxels[SLOT_WATER]).ToString()}",
                    BurstVoxelDataBitMapping.GetId(voxels[SLOT_WATER]) == V5_ID_WATER);
                ok &= Check("air stays air outside the seeded slots",
                    BurstVoxelDataBitMapping.GetId(voxels[SLOT_LIGHT_CARRIER + 1]) == 0);

                // Meta arms whose expected value is hand-derivable from the documented mapping.
                ok &= Check($"SCHEMA_NONE forces the Facade meta to 0, got {BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_FACADE]).ToString()}",
                    BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_FACADE]) == 0);
                ok &= Check($"SCHEMA_KEEP_LEGACY leaves the half-slab meta verbatim at {LEGACY_META_VERBATIM.ToString()}, got {BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_HALF_SLAB]).ToString()}",
                    BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_HALF_SLAB]) == LEGACY_META_VERBATIM);
                ok &= Check($"every OakLog normalizes to Axis3.Y ({EXPECTED_OAK_LOG_AXIS.ToString()}), got {BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_OAK_LOG]).ToString()}",
                    BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_OAK_LOG]) == EXPECTED_OAK_LOG_AXIS);
                ok &= Check($"HorizontalOnly converts 0x{LEGACY_META_HORIZONTAL_INPUT:X2} to yaw {EXPECTED_HORIZONTAL_YAW.ToString()} (high bits masked), got {BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_STONE]).ToString()}",
                    BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_STONE]) == EXPECTED_HORIZONTAL_YAW);

                // Light queues: the fixture's 13-byte entries must survive v7→v8's widening to 16.
                ok &= Check($"the sunlight queue survives the entry widening, got {chunk.SunLightQueueCount.ToString()}",
                    chunk.SunLightQueueCount == FIXTURE_SUN_QUEUE_ENTRIES);
                ok &= Check($"the blocklight queue survives, got {chunk.BlockLightQueueCount.ToString()}",
                    chunk.BlockLightQueueCount == FIXTURE_BLOCK_QUEUE_ENTRIES);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(chunk);
            }

            return ok;
        }

        /// <summary>B17. Red when: v8→v9 stops lifting the legacy light nibbles into the per-section
        /// <c>LightData</c> array, or reads them from the wrong bit positions. A pre-v9 world's lighting lives
        /// only in those bits, so getting this wrong means every migrated world loads dark until it re-lights.</summary>
        private static bool LegacyLightIsLiftedIntoLightData()
        {
            using MigrationFixture fx = new MigrationFixture();
            ChunkData chunk = MigrateAndDeserialize(2);

            bool ok = Check("the migrated payload deserializes", chunk != null);
            if (chunk == null) return false;

            try
            {
                ushort[] light = chunk.sections[0]?.LightData;
                ok &= Check("section 0 carries a LightData array (flag 0x01, not uniform-sky)", light != null);
                if (light == null) return false;

                ushort entry = light[SLOT_LIGHT_CARRIER];
                int sky = (entry >> LIGHTDATA_SKY_SHIFT) & 0xF;
                int r = (entry >> LIGHTDATA_R_SHIFT) & 0xF;
                int g = (entry >> LIGHTDATA_G_SHIFT) & 0xF;
                int b = (entry >> LIGHTDATA_B_SHIFT) & 0xF;

                ok &= Check($"the legacy sunlight nibble becomes sky {LEGACY_SUN_LEVEL.ToString()}, got {sky.ToString()}",
                    sky == LEGACY_SUN_LEVEL);
                ok &= Check($"the legacy blocklight nibble becomes grey RGB ({LEGACY_BLOCK_LEVEL.ToString()},{LEGACY_BLOCK_LEVEL.ToString()},{LEGACY_BLOCK_LEVEL.ToString()}), got ({r.ToString()},{g.ToString()},{b.ToString()})",
                    r == LEGACY_BLOCK_LEVEL && g == LEGACY_BLOCK_LEVEL && b == LEGACY_BLOCK_LEVEL);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(chunk);
            }

            return ok;
        }

        /// <summary>B18. Red when: v9→v10 stops clearing the legacy light bits. Bits 16–23 are reserved in the
        /// current layout, and a voxel that still carries stale light there is a voxel whose future meaning
        /// changes the moment those bits are used for anything.</summary>
        private static bool LegacyLightBitsAreStripped()
        {
            using MigrationFixture fx = new MigrationFixture();
            ChunkData chunk = MigrateAndDeserialize(2);

            bool ok = Check("the migrated payload deserializes", chunk != null);
            if (chunk == null) return false;

            try
            {
                uint[] voxels = chunk.sections[0]?.voxels;
                ok &= Check("section 0 has a voxel array", voxels != null);
                if (voxels == null) return false;

                uint carrier = voxels[SLOT_LIGHT_CARRIER];
                ok &= Check($"the light carrier's reserved bits 16–23 are clear, got 0x{(carrier & LEGACY_LIGHT_BITS):X8}",
                    (carrier & LEGACY_LIGHT_BITS) == 0);
                ok &= Check("stripping light did not disturb the id",
                    BurstVoxelDataBitMapping.GetId(carrier) == V5_ID_STONE);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(chunk);
            }

            return ok;
        }

        /// <summary>B19. Red when: the v2→v3 step's v1 heightmap widening (256 bytes of per-column bytes → 512
        /// bytes of ushorts) breaks. A wrong widening shifts every byte after the heightmap, so this also acts
        /// as an alignment check on the whole v1 path.</summary>
        private static bool V1HeightmapWidening()
        {
            using MigrationFixture fx = new MigrationFixture();
            ChunkData chunk = MigrateAndDeserialize(1);

            bool ok = Check("an era-v1 payload survives the chain and deserializes", chunk != null);
            if (chunk == null) return false;

            try
            {
                ok &= Check($"column 0's height widens to {FIXTURE_HEIGHT_COLUMN_0.ToString()}, got {chunk.heightMap[0].ToString()}",
                    chunk.heightMap[0] == FIXTURE_HEIGHT_COLUMN_0);
                ok &= Check($"column 1's height widens to {FIXTURE_HEIGHT_COLUMN_1.ToString()}, got {chunk.heightMap[1].ToString()}",
                    chunk.heightMap[1] == FIXTURE_HEIGHT_COLUMN_1);
                uint[] voxels = chunk.sections[0]?.voxels;
                ok &= Check("section 0 has a voxel array", voxels != null);
                if (voxels == null) return false;

                ok &= Check("the v1 path stays aligned — the seeded ids still land in section 0",
                    BurstVoxelDataBitMapping.GetId(voxels[SLOT_OAK_LOG]) == V5_ID_OAK_LOG);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(chunk);
            }

            return ok;
        }

        /// <summary>B20. Red when: <c>ConvertLegacyMeta</c>'s shipped table changes. Its own remarks say the
        /// behavior "must never change once shipped" (§9.6) and that it is public precisely so an editor-time
        /// validator can pin it — this is that validator. Covers the arms B16 cannot hand-derive.</summary>
        private static bool ConvertLegacyMetaTableIsPinned()
        {
            // Axis3: the documented §9.5.A mapping — N/S → Z(2), W/E → X(1), T/B/default → Y(0).
            bool ok = Check("Axis3 maps North(0) → Z", MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(0) == 2);
            ok &= Check("Axis3 maps South(1) → Z", MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(1) == 2);
            ok &= Check("Axis3 maps West(2) → X", MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(2) == 1);
            ok &= Check("Axis3 maps East(3) → X", MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMetaToAxis3(3) == 1);

            // Schema routing per block id.
            ok &= Check("Air routes to SCHEMA_NONE (meta forced 0)",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(0, 7) == 0);
            ok &= Check("Facade routes to SCHEMA_NONE (meta forced 0)",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_FACADE, 7) == 0);
            ok &= Check("StoneHalfSlab is deferred — meta verbatim",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_STONE_HALF_SLAB, 7) == 7);
            ok &= Check("an unknown/fork id is left alone rather than zeroed",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(60000, 7) == 7);
            // OakLog has its OWN frozen converter: every legacy value normalizes to Y, deliberately, because
            // historical oak logs never stored a meaningful axis. It does NOT take the generic Axis3 mapping.
            ok &= Check($"every OakLog legacy value normalizes to Axis3.Y, got ({MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_OAK_LOG, 0).ToString()}, {MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_OAK_LOG, 3).ToString()})",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_OAK_LOG, 0) == EXPECTED_OAK_LOG_AXIS &&
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_OAK_LOG, 3) == EXPECTED_OAK_LOG_AXIS);

            // HorizontalOnly: identity for the four horizontal indices, clamp to North above them.
            ok &= Check("HorizontalOnly is the identity for storage indices 0-3",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(0) == 0 &&
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(1) == 1 &&
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(2) == 2 &&
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(3) == 3);
            ok &= Check("HorizontalOnly clamps Top/Bottom/invalid (4-7) to North",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(4) == 0 &&
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(5) == 0 &&
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyToHorizontalOnly(7) == 0);
            ok &= Check($"HorizontalOnly masks the high bits: 0x{LEGACY_META_HORIZONTAL_INPUT:X2} → {EXPECTED_HORIZONTAL_YAW.ToString()}",
                MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_STONE, LEGACY_META_HORIZONTAL_INPUT) == EXPECTED_HORIZONTAL_YAW);

            // Fluids keep their level in the low nibble.
            ok &= Check($"Water keeps its fluid level in the low nibble, got {MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_WATER, 3).ToString()}",
                (MigrationV5ToV6LegacyToSchemaBased.ConvertLegacyMeta(V5_ID_WATER, 3) & 0x0F) == 3);
            return ok;
        }

        /// <summary>B21. Red when: the manager's production per-chunk format loop stops applying the chain — the
        /// thing B15/B16 exercise through a mirrored loop. Drives a real <b>v2</b> world (chunk payloads stored
        /// in the era-v2 layout) through <c>RunAOTMigrationAsync</c> and reads the result back through the full
        /// storage stack. A v2 world is used deliberately: it has no region-layout step in its path, so it needs
        /// only the per-chunk pass, where a v1 world needs that pass <i>and</i> the layout repack — the pairing
        /// <c>B25</c> guards (see <c>_FIXED_BUGS.md</c> Serialization 07).</summary>
        private static bool RealManagerAppliesChunkFormatChain()
        {
            using MigrationFixture fx = new MigrationFixture();
            Vector2Int chunkVoxelPos = SeedEraChunkOnDisk(fx, 2);

            // A v12 document relabeled v2, not a faithful v2 level.dat — it carries fields v2 never had, which
            // the chain simply overwrites. Deliberate: this scenario owns the CHUNK loop, and level.dat fidelity
            // per era is B1-B4's job. The relabeling only has to make the manager choose the format branch.
            File.WriteAllText(fx.LevelDatPath, V12_LEVEL_DAT.Replace("\"version\": 12", "\"version\": 2"));

            ProgressRecorder progress = new ProgressRecorder();
            RunMigration(new MigrationManager(), fx, 2, progress);

            bool ok = Check($"the manager processed the seeded chunk (non-vacuity), got {progress.Last.ProcessedItems.ToString()}",
                progress.Last.ProcessedItems == 1);

            ChunkData loaded = null;
            ChunkStorageManager reader = new ChunkStorageManager(fx.WorldName, true, SaveSystem.CURRENT_VERSION);
            try
            {
                loaded = Task.Run(() => reader.LoadChunkAsync(chunkVoxelPos)).GetAwaiter().GetResult();
                ok &= Check("the migrated chunk loads through the real storage stack (not regenerated)", loaded != null);

                uint[] voxels = loaded?.sections?[0]?.voxels;
                ok &= Check("the loaded chunk's section 0 has a voxel array", voxels != null);
                if (voxels != null)
                {
                    ok &= Check($"the seeded OakLog survived the production chain, got id {BurstVoxelDataBitMapping.GetId(voxels[SLOT_OAK_LOG]).ToString()}",
                        BurstVoxelDataBitMapping.GetId(voxels[SLOT_OAK_LOG]) == V5_ID_OAK_LOG);
                    ok &= Check($"the HorizontalOnly probe's meta was schema-converted through the production loop, got {BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_STONE]).ToString()}",
                        BurstVoxelDataBitMapping.GetMeta(voxels[SLOT_STONE]) == EXPECTED_HORIZONTAL_YAW);
                }
            }
            finally
            {
                if (loaded != null) World.Instance.ChunkPool.ReturnChunkData(loaded);
                reader.Dispose();
            }

            return ok;
        }

        /// <summary>
        /// Writes one era-format chunk payload straight into the fixture world's region file, bypassing
        /// <c>ChunkSerializer</c> (which can only write the current format).
        /// </summary>
        /// <param name="fx">Fixture world to write into.</param>
        /// <param name="eraVersion">Chunk format to author (1 or 2).</param>
        /// <returns>The chunk's voxel-space origin, for loading it back later.</returns>
        private static Vector2Int SeedEraChunkOnDisk(MigrationFixture fx, byte eraVersion)
        {
            Vector2Int chunkVoxelPos =
                new Vector2Int(FIXTURE_CHUNK_X * ERA_CHUNK_WIDTH, FIXTURE_CHUNK_Z * ERA_CHUNK_WIDTH);
            (Vector2Int regionCoord, int localX, int localZ) =
                RegionAddressCodec.ForVersion(2).ChunkVoxelPosToRegionAddress(chunkVoxelPos);

            Directory.CreateDirectory(fx.RegionPath);
            byte[] payload = BuildHistoricalChunkPayload(eraVersion);

            // Stored uncompressed so the fixture needs no compressor; the migration decompresses using the
            // algorithm recorded in the record header, then recompresses to the target.
            using RegionFile region = new RegionFile(
                Path.Combine(fx.RegionPath, $"r.{regionCoord.x.ToString()}.{regionCoord.y.ToString()}.bin"));
            region.SaveChunkData(localX, localZ, payload, payload.Length, CompressionAlgorithm.None);

            return chunkVoxelPos;
        }
    }
}
