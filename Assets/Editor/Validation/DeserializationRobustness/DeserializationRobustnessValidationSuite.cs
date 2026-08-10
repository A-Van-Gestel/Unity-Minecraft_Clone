using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Data;
using Editor.Validation.Framework;
using Jobs.BurstData;
using Serialization;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.DeserializationRobustness
{
    /// <summary>
    /// Validation suite for the CP-3 load-boundary robustness contract (design doc
    /// <c>CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md</c> §3.3/§4.3, finding F1; roadmap NS-1 seed):
    /// <see cref="ChunkSerializer.Deserialize"/> fed a truncated / garbage / wrong-version payload must
    /// return null without throwing and without leaking the pooled shell or its attached sections
    /// (asserted via the concurrent pools' active counts), and <see cref="ChunkStorageManager.LoadChunkAsync"/>
    /// must keep a thrown I/O fault distinct from the null "not on disk" result — a fault surfaced as null
    /// would regenerate the chunk over saved data. Faults are injected via the dev-only
    /// <see cref="ChunkStorageManager.InjectLoadFaults"/> seam; each scenario stands up an isolated stub
    /// <c>World.Instance</c> + a volatile-path <see cref="ChunkStorageManager"/>.
    /// <para>All scenarios are <b>baselines</b> (must stay green); a failure is a regression of the F1
    /// load-boundary contract (leaked pooled shells or a fault masquerading as "not on disk").</para>
    /// <para><b>Prove-red:</b> removing the shell-return in <c>ChunkSerializer.ReadChunkInternal</c>'s
    /// catch reds exactly B2/B5 (pool balance — the leak this suite guards); routing
    /// <c>LoadChunkAsync</c> faults to a null return instead of a faulted task reds B6.</para>
    /// </summary>
    public static class DeserializationRobustnessValidationSuite
    {
        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Deserialization Robustness")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the robustness scenarios, returning the categorized result (the headless/CI
        /// entry point). Uses <see cref="KnownBugChannel.Unimplemented"/> for parity with the other
        /// pure-logic suites; the channel is currently unused (baselines only).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1: round-trip sanity — valid payload deserializes, edit intact, pools balanced", RoundTripSanity),
                new Scenario("B2: truncated payload (mid-heightmap) — null, no throw, shell not leaked", TruncatedPayloadReturnsShell),
                new Scenario("B3: garbage payload — null, no throw, pools balanced", GarbagePayloadIsNull),
                new Scenario("B4: wrong chunk-format version — null, no throw, pools balanced", WrongVersionIsNull),
                new Scenario("B5: corrupt light-queue count — null, shell AND attached sections not leaked", CorruptTailReturnsShellAndSections),
                new Scenario("B6: thrown load fault — task faults (retry), never the null 'not on disk' result", LoadFaultIsNotNull),
                new Scenario("B7: corrupt payload on disk — LoadChunkAsync returns null (regenerate by design)", CorruptOnDiskLoadsNull),
                new Scenario("B8: level.dat v13→v14 — the worldState.environment/wind section is injected at the historical default and every other field survives", LevelDatV13ToV14EnvironmentWind),
                new Scenario("B9: level.dat v14→v15 — timeOfDay is retired for a noon-seeded world clock, the document's own wind carries through, and a current document passes through untouched", LevelDatV14ToV15TimeOfDay),
            };
            return ValidationSuiteRunner.Execute("Deserialization Robustness", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
        }

        // --- Fixture -----------------------------------------------------------------------------

        /// <summary>Chunk-local coordinates of the low edit (section 0) and high edit (section 2) —
        /// two sections so a tail corruption exercises the attached-section return path.</summary>
        private const int EDIT_X = 3, EDIT_LOW_Y = 8, EDIT_HIGH_Y = 40, EDIT_Z = 7;

        /// <summary>Serialized layout: version byte + two int coords + NeedsInitialLighting bool —
        /// the bytes read before the pooled shell is acquired. A truncation past this offset lands
        /// inside the heightmap read, i.e. after shell acquisition (the leak path under test).</summary>
        private const int HEADER_BYTES = 1 + 4 + 4 + 1;

        /// <summary>Truncation point inside the 512-byte heightmap block (well past the header).</summary>
        private const int TRUNCATE_AT = HEADER_BYTES + 100;

        /// <summary>Suite fixture: the shared <see cref="StorageValidationFixture"/> (stub
        /// <c>World.Instance</c> + volatile-path storage + all-seam disarm) under this suite's prefix.</summary>
        private sealed class Fixture : StorageValidationFixture
        {
            public Fixture() : base("DeserRobustnessTest")
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

            /// <summary>Asserts the active counts match this snapshot (no shell/section leaked).</summary>
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

        // --- Helpers -----------------------------------------------------------------------------

        /// <summary>Builds a pooled chunk at <paramref name="pos"/> carrying two edits (sections 0 and 2).</summary>
        private static ChunkData MakeEditedChunk(Vector2Int pos, ushort blockId)
        {
            ChunkData data = World.Instance.ChunkPool.GetChunkData(pos);
            data.SetVoxel(EDIT_X, EDIT_LOW_Y, EDIT_Z, BurstVoxelDataBitMapping.PackVoxelData(blockId, 0));
            data.SetVoxel(EDIT_X, EDIT_HIGH_Y, EDIT_Z, BurstVoxelDataBitMapping.PackVoxelData(blockId, 0));
            return data;
        }

        /// <summary>Serializes a fresh fixture chunk with <see cref="CompressionAlgorithm.None"/> (so byte
        /// offsets are stable for surgical corruption) and returns the chunk to the pool.</summary>
        /// <param name="pos">The chunk position to serialize.</param>
        /// <returns>The exact serialized payload (defensive copy — safe to corrupt).</returns>
        private static byte[] BuildValidPayload(Vector2Int pos)
        {
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);
            byte[] buffer = SerializationBufferPool.Get();
            try
            {
                int length = ChunkSerializer.Serialize(data, buffer, CompressionAlgorithm.None);
                byte[] payload = new byte[length];
                Array.Copy(buffer, payload, length);
                return payload;
            }
            finally
            {
                SerializationBufferPool.Return(buffer);
                World.Instance.ChunkPool.ReturnChunkData(data);
            }
        }

        /// <summary>Runs <see cref="ChunkSerializer.Deserialize"/> on a corrupt payload and asserts the
        /// full robustness contract: null result, no throw, parse-failure counter incremented, and both
        /// concurrent pools balanced (no leaked shell/section).</summary>
        /// <param name="payload">The (corrupted) payload bytes.</param>
        /// <param name="pos">The expected chunk position.</param>
        /// <param name="label">Scenario label prefix for the assertions.</param>
        private static bool AssertCorruptPayloadContract(byte[] payload, Vector2Int pos, string label)
        {
            PoolBalance balance = PoolBalance.Capture();
            long failuresBefore = ChunkSerializer.DeserializeFailures;

            ChunkData result;
            try
            {
                result = ChunkSerializer.Deserialize(payload, CompressionAlgorithm.None, pos);
            }
            catch (Exception e)
            {
                return Check($"{label}: Deserialize must not throw (threw {e.GetType().Name})", false);
            }

            bool ok = Check($"{label}: returns null", result == null);
            // Guards a vacuous pass: proves the parse path actually ran and failed (not e.g. an empty payload).
            ok &= Check($"{label}: parse failure counted", ChunkSerializer.DeserializeFailures > failuresBefore);
            ok &= balance.AssertUnchanged($"{label}: pools balanced (no shell/section leak)");
            if (result != null) World.Instance.ChunkPool.ReturnChunkData(result);
            return ok;
        }

        /// <summary>Runs <see cref="ChunkStorageManager.SaveChunkAsync"/> to completion. Wrapped in
        /// <see cref="Task.Run(Func{Task})"/> so its continuations resume on the ThreadPool instead of
        /// being posted to the (blocked) editor main thread — blocking directly would deadlock.</summary>
        private static ChunkSaveResult RunSave(Fixture fx, ChunkData data) =>
            Task.Run(() => fx.Storage.SaveChunkAsync(data)).GetAwaiter().GetResult();

        /// <summary>Runs <see cref="ChunkStorageManager.LoadChunkAsync"/> to completion (same wrapping as <see cref="RunSave"/>).</summary>
        private static ChunkData RunLoad(ChunkStorageManager storage, Vector2Int pos) =>
            Task.Run(() => storage.LoadChunkAsync(pos)).GetAwaiter().GetResult();

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>B1. Red when: the happy deserialize path breaks (harness sanity — guards the other
        /// scenarios against passing vacuously on a payload that was never valid).</summary>
        private static bool RoundTripSanity()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 0);
            byte[] payload = BuildValidPayload(pos);

            PoolBalance balance = PoolBalance.Capture();
            ChunkData loaded = ChunkSerializer.Deserialize(payload, CompressionAlgorithm.None, pos);
            if (loaded == null) return Check("valid payload deserializes", false);

            ushort id = BurstVoxelDataBitMapping.GetId(loaded.GetVoxel(EDIT_X, EDIT_HIGH_Y, EDIT_Z));
            bool ok = Check($"edit round-trips (expected {BlockIDs.Stone.ToString()}, got {id.ToString()})", id == BlockIDs.Stone);
            World.Instance.ChunkPool.ReturnChunkData(loaded);
            ok &= balance.AssertUnchanged("pools balanced after round trip");
            return ok;
        }

        /// <summary>B2. The core F1 leak witness. Red when: a payload truncated after the header (the
        /// shell is already acquired when the heightmap read fails) leaks the pooled shell.</summary>
        private static bool TruncatedPayloadReturnsShell()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(16, 0);
            byte[] payload = BuildValidPayload(pos);

            byte[] truncated = new byte[TRUNCATE_AT];
            Array.Copy(payload, truncated, TRUNCATE_AT);
            return AssertCorruptPayloadContract(truncated, pos, "truncated payload");
        }

        /// <summary>B3. Red when: an arbitrary garbage payload throws out of Deserialize or leaks.</summary>
        private static bool GarbagePayloadIsNull()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 16);

            System.Random rng = new System.Random(12345); // deterministic garbage
            byte[] garbage = new byte[256];
            rng.NextBytes(garbage);
            garbage[0] = 0xAB; // never a valid chunk-format version byte

            return AssertCorruptPayloadContract(garbage, pos, "garbage payload");
        }

        /// <summary>B4. Red when: an unsupported chunk-format version escapes as a throw or leaks
        /// (the strict version check must stay a warn-and-null, feeding the regenerate arm).</summary>
        private static bool WrongVersionIsNull()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(16, 16);
            byte[] payload = BuildValidPayload(pos);

            payload[0] = 0xFF; // bogus version byte
            return AssertCorruptPayloadContract(payload, pos, "wrong-version payload");
        }

        /// <summary>B5. The attached-section leak witness. Red when: a corruption hit AFTER sections were
        /// parsed and attached (bogus trailing light-queue count) leaks the shell or those sections.</summary>
        private static bool CorruptTailReturnsShellAndSections()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(32, 0);
            byte[] payload = BuildValidPayload(pos);

            // The payload ends with two int light-queue counts (both 0 for a fresh chunk). Overwrite the
            // final count with int.MaxValue — the reader's sanity check throws with sections attached.
            payload[^4] = 0xFF;
            payload[^3] = 0xFF;
            payload[^2] = 0xFF;
            payload[^1] = 0x7F;
            return AssertCorruptPayloadContract(payload, pos, "corrupt light-queue count");
        }

        /// <summary>B6. The fault ≠ "not on disk" contract. Red when: a thrown load fault surfaces as a
        /// null result — the caller would regenerate the chunk over its saved data instead of retrying.</summary>
        private static bool LoadFaultIsNotNull()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 32);

            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);
            bool ok = Check("seed save reports Written", RunSave(fx, data) == ChunkSaveResult.Written);
            World.Instance.ChunkPool.ReturnChunkData(data);

            ChunkStorageManager.InjectLoadFaults(1);
            bool faulted;
            try
            {
                ChunkData result = RunLoad(fx.Storage, pos);
                faulted = false;
                if (result != null) World.Instance.ChunkPool.ReturnChunkData(result);
            }
            catch (IOException)
            {
                faulted = true;
            }

            ok &= Check("injected fault surfaces as a faulted task, not a result", faulted);

            // Fault consumed — the same load must now succeed (the retryable-transient shape).
            ChunkData retry = RunLoad(fx.Storage, pos);
            if (retry == null) return Check("load succeeds after the transient fault", false);
            ushort id = BurstVoxelDataBitMapping.GetId(retry.GetVoxel(EDIT_X, EDIT_HIGH_Y, EDIT_Z));
            ok &= Check("retried load returns the saved edit", id == BlockIDs.Stone);
            World.Instance.ChunkPool.ReturnChunkData(retry);

            // And a genuinely absent chunk still reports null without throwing (the generate arm).
            ok &= Check("not-on-disk load returns null without throwing", RunLoad(fx.Storage, new Vector2Int(480, 480)) == null);
            return ok;
        }

        /// <summary>B7. Red when: a corrupt on-disk payload no longer takes the deliberate
        /// warn → null → regenerate arm through the full storage stack (or leaks the shell).</summary>
        private static bool CorruptOnDiskLoadsNull()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 0); // region (0,0), local slot 0 → record at sector 2

            ChunkData data = MakeEditedChunk(pos, BlockIDs.Grass);
            bool ok = Check("seed save reports Written", RunSave(fx, data) == ChunkSaveResult.Written);
            World.Instance.ChunkPool.ReturnChunkData(data);

            // Corrupt the record's payload on disk (skip the 4-byte length + 1-byte algorithm header).
            fx.Storage.Dispose(); // release the region file handle
            string regionPath = Path.Combine(SaveSystem.GetSavePath(fx.WorldName, useVolatilePath: true), "Region", "r.0.0.bin");
            const int RECORD_OFFSET = 2 * 4096 + 5;
            using (FileStream fs = new FileStream(regionPath, FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Seek(RECORD_OFFSET, SeekOrigin.Begin);
                for (int i = 0; i < 32; i++) fs.WriteByte(0xEE);
            }

            ChunkStorageManager second = new ChunkStorageManager(fx.WorldName, useVolatilePath: true, SaveSystem.CURRENT_VERSION);
            try
            {
                PoolBalance balance = PoolBalance.Capture();
                ChunkData result = RunLoad(second, pos);
                ok &= Check("corrupt on-disk payload loads as null (regenerate arm)", result == null);
                ok &= balance.AssertUnchanged("pools balanced after corrupt disk load");
                if (result != null) World.Instance.ChunkPool.ReturnChunkData(result);
            }
            finally
            {
                second.Dispose();
            }

            return ok;
        }

        /// <summary>The wind every pre-v14 world was rendered with — what the v13→v14 step must inject.</summary>
        private const float HISTORICAL_WIND_X = -0.6f;

        /// <summary>
        /// A handwritten v13 <c>level.dat</c>: the last shape before the environment section, with a
        /// distinct non-default value in every field so an additive migration that silently drops one
        /// is caught rather than passing on defaults.
        /// </summary>
        private const string V13_LEVEL_DAT = @"{
  ""version"": 13, ""worldName"": ""MigrationProbe"", ""seed"": 1234,
  ""chunkHeight"": 128, ""chunkWidth"": 16, ""worldSizeInChunks"": 100, ""worldType"": 1,
  ""spawnPosition"": { ""_chunkX"": 2, ""_chunkZ"": -3, ""localPosition"": { ""x"": 1.5, ""y"": 64.0, ""z"": 2.5 } },
  ""borderRadius"": 512, ""creationDate"": 111, ""lastPlayed"": 222,
  ""worldState"": { ""timeOfDay"": 0.25 },
  ""player"": {
    ""position"": { ""_chunkX"": 5, ""_chunkZ"": 6, ""localPosition"": { ""x"": 1.0, ""y"": 70.0, ""z"": 2.0 } },
    ""rotation"": { ""x"": 0.0, ""y"": 90.0, ""z"": 0.0 },
    ""capabilities"": { ""isFlying"": true, ""isNoclipping"": false },
    ""inventory"": [ { ""slotIndex"": 0, ""itemID"": 3, ""amount"": 7 } ],
    ""cursorItem"": null
  }
}";

        /// <summary>Day tick the v14→v15 step seeds; a stored light level carries no recoverable time.</summary>
        private const long MIGRATED_NOON_TICKS = 6000L;

        /// <summary>
        /// A handwritten v14 <c>level.dat</c> in the shape the (revised) v13→v14 step produces:
        /// <c>environment</c> nested under <c>worldState</c>, alongside the <c>timeOfDay</c> field v15
        /// retires. Distinct values throughout so a dropped field is caught rather than passing on defaults.
        /// </summary>
        private const string V14_LEVEL_DAT = @"{
  ""version"": 14, ""worldName"": ""MigrationProbe14"", ""seed"": 4321,
  ""chunkHeight"": 128, ""chunkWidth"": 16, ""worldSizeInChunks"": 100, ""worldType"": 1,
  ""spawnPosition"": { ""_chunkX"": 7, ""_chunkZ"": -8, ""localPosition"": { ""x"": 1.5, ""y"": 64.0, ""z"": 2.5 } },
  ""borderRadius"": 256, ""creationDate"": 333, ""lastPlayed"": 444,
  ""worldState"": { ""timeOfDay"": 0.75, ""environment"": { ""windX"": 1.5, ""windZ"": -2.25 } },
  ""player"": {
    ""position"": { ""_chunkX"": 9, ""_chunkZ"": 10, ""localPosition"": { ""x"": 1.0, ""y"": 70.0, ""z"": 2.0 } },
    ""rotation"": { ""x"": 0.0, ""y"": 90.0, ""z"": 0.0 },
    ""capabilities"": { ""isFlying"": true, ""isNoclipping"": false },
    ""inventory"": [ { ""slotIndex"": 0, ""itemID"": 3, ""amount"": 7 } ],
    ""cursorItem"": null
  }
}";

        /// <summary>B8. Red when: the v13→v14 step is unregistered, injects the wrong default, nests the
        /// environment section at the wrong path, or the frozen DTO drops a field it should have carried
        /// through (the silent-blanking failure the v12→v13 header documents).</summary>
        private static bool LevelDatV13ToV14EnvironmentWind()
        {
            WorldSaveData upgraded = LevelDatCodec.ReadNormalized(V13_LEVEL_DAT);

            bool ok = Check("a v13 document gains a worldState.environment section",
                upgraded.worldState?.environment != null);
            if (upgraded.worldState?.environment != null)
            {
                ok &= Check($"the injected wind is the historical default, got ({upgraded.worldState.environment.windX}, {upgraded.worldState.environment.windZ})",
                    Mathf.Approximately(upgraded.worldState.environment.windX, HISTORICAL_WIND_X) &&
                    Mathf.Approximately(upgraded.worldState.environment.windZ, 0f));
            }

            // The codec deliberately keeps the ON-DISK version so the menu still offers a real migration.
            ok &= Check("the reported version stays the disk's v13", upgraded.version == 13);

            // Every other field must survive the chain untouched.
            ok &= Check("worldName survives", upgraded.worldName == "MigrationProbe");
            ok &= Check("seed survives", upgraded.seed == 1234);
            ok &= Check("borderRadius survives", upgraded.borderRadius == 512);
            ok &= Check("creation/lastPlayed survive", upgraded.creationDate == 111 && upgraded.lastPlayed == 222);
            ok &= Check("spawn chunk survives", upgraded.spawnPosition.Chunk.X == 2 && upgraded.spawnPosition.Chunk.Z == -3);
            ok &= Check("player chunk survives (the v13 re-type is not re-applied)",
                upgraded.player.position.Chunk.X == 5 && upgraded.player.position.Chunk.Z == 6);
            ok &= Check("player local Y survives", Mathf.Approximately(upgraded.player.position.localPosition.y, 70f));
            ok &= Check("player capabilities survive", upgraded.player.capabilities.isFlying);
            ok &= Check("inventory survives",
                upgraded.player.inventory.Count == 1 && upgraded.player.inventory[0].amount == 7);
            return ok;
        }

        /// <summary>B9. Red when: the v14→v15 step is unregistered, fails to seed the clock, drops the
        /// wind it must carry through, or leaves the retired <c>timeOfDay</c> driving anything.</summary>
        private static bool LevelDatV14ToV15TimeOfDay()
        {
            WorldSaveData upgraded = LevelDatCodec.ReadNormalized(V14_LEVEL_DAT);

            bool ok = Check("a v14 document gains a worldState.time section", upgraded.worldState?.time != null);
            if (upgraded.worldState?.time != null)
            {
                ok &= Check($"the migrated world starts at noon, got tick {upgraded.worldState.time.ticks}",
                    upgraded.worldState.time.ticks == MIGRATED_NOON_TICKS);
                ok &= Check("the migrated world is not frozen", !upgraded.worldState.time.frozen);
            }

            // The v14 wind is carried through, not reset to the v13 historical default.
            ok &= Check($"the document's own wind survives, got ({upgraded.worldState?.environment?.windX}, {upgraded.worldState?.environment?.windZ})",
                upgraded.worldState?.environment != null &&
                Mathf.Approximately(upgraded.worldState.environment.windX, 1.5f) &&
                Mathf.Approximately(upgraded.worldState.environment.windZ, -2.25f));

            ok &= Check("the reported version stays the disk's v14", upgraded.version == 14);
            ok &= Check("worldName survives", upgraded.worldName == "MigrationProbe14");
            ok &= Check("seed survives", upgraded.seed == 4321);
            ok &= Check("borderRadius survives", upgraded.borderRadius == 256);
            ok &= Check("creation/lastPlayed survive", upgraded.creationDate == 333 && upgraded.lastPlayed == 444);
            ok &= Check("spawn chunk survives", upgraded.spawnPosition.Chunk.X == 7 && upgraded.spawnPosition.Chunk.Z == -8);
            ok &= Check("player chunk survives", upgraded.player.position.Chunk.X == 9 && upgraded.player.position.Chunk.Z == 10);
            ok &= Check("player capabilities survive", upgraded.player.capabilities.isFlying);
            ok &= Check("inventory survives",
                upgraded.player.inventory.Count == 1 && upgraded.player.inventory[0].amount == 7);

            // A current-version document passes straight through with its own wind and clock intact.
            WorldSaveData current = new WorldSaveData
            {
                version = SaveSystem.CURRENT_VERSION,
                worldName = "CurrentProbe",
                worldState = new WorldStateData
                {
                    environment = new EnvironmentData { windX = 1.25f, windZ = -2.5f },
                    time = new WorldTimeData { ticks = 123456L, frozen = true },
                },
            };
            WorldSaveData passthrough = LevelDatCodec.ReadNormalized(JsonUtility.ToJson(current));
            ok &= Check($"a v{SaveSystem.CURRENT_VERSION} document keeps its own wind, got ({passthrough.worldState.environment.windX}, {passthrough.worldState.environment.windZ})",
                Mathf.Approximately(passthrough.worldState.environment.windX, 1.25f) &&
                Mathf.Approximately(passthrough.worldState.environment.windZ, -2.5f));
            ok &= Check($"a v{SaveSystem.CURRENT_VERSION} document keeps its own clock, got tick {passthrough.worldState.time.ticks} frozen {passthrough.worldState.time.frozen}",
                passthrough.worldState.time.ticks == 123456L && passthrough.worldState.time.frozen);
            return ok;
        }
    }
}
