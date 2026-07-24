using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Editor.Validation.Framework;
using Jobs.BurstData;
using Serialization;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.SaveDurability
{
    /// <summary>
    /// Validation suite for the CP-6 save durability contract (design doc
    /// <c>CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md</c> §4.3, finding F5): a failed chunk save must
    /// surface <see cref="ChunkSaveResult.Failed"/> to its caller and hand its serialization snapshot —
    /// the edits' only surviving copy once the live <see cref="ChunkData"/> is pool-recycled — to the
    /// failed-save retry registry, which re-attempts the write until it lands (per-frame drain, reload
    /// guard, quit-time flush). Faults are injected via the dev-only
    /// <see cref="ChunkStorageManager.InjectSaveFaults"/> (+ zero-length / too-large) seams; each scenario stands up an isolated stub
    /// <c>World.Instance</c> + a volatile-path <see cref="ChunkStorageManager"/> and round-trips real
    /// region files.
    /// <para>All scenarios are <b>baselines</b> (must stay green); a failure is a regression of the F5
    /// durability hole (silently lost session edits).</para>
    /// <para><b>Prove-red:</b> routing the Failed/Canceled arms' snapshot back to the pool instead of
    /// <c>StageFailedSave</c> in <see cref="ChunkStorageManager.SaveChunkAsync"/> reds B2–B8 (the edit
    /// no longer survives); routing FailedPermanent into the registry reds B9 (retry loop); B1 stays
    /// green (happy path untouched).</para>
    /// </summary>
    public static class SaveDurabilityValidationSuite
    {
        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Save Durability")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the durability scenarios, returning the categorized result (the headless/CI
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
                new Scenario("B1: happy path — save Written, nothing pending, edit round-trips", HappyRoundTrip),
                new Scenario("B2: injected fault — Failed surfaced, snapshot owned by retry registry", FailureSurfacesAndRegisters),
                new Scenario("B3: drain retry recovers the failed save — edit survives on disk", DrainRecoversFailedSave),
                new Scenario("B4: persistent fault — entry retained across a failed retry, recovered after", PersistentFaultRetainsEntry),
                new Scenario("B5: newer failed save supersedes older for the same coord", NewerFailedSaveSupersedes),
                new Scenario("B6: reload guard — pending retry flushed before the chunk load reads disk", ReloadGuardFlushesBeforeLoad),
                new Scenario("B7: quit flush — FlushFailedSavesSync lands the pending save", QuitFlushLandsPendingSave),
                new Scenario("B8: canceled save — Canceled surfaced, snapshot staged, quit flush recovers it", CanceledSaveStagedForQuitFlush),
                new Scenario("B9: deterministic zero-length failure — FailedPermanent, never enters the retry loop", PermanentFailureNeverRetries),
                new Scenario("B10: successful write supersedes older pending entry; newer failure survives (FIFO)", SuccessfulWriteSupersedesPendingEntry),
                new Scenario("B11: Dispose makes a final attempt on pending saves — edits survive a manager swap", DisposeFlushesPendingSaves),
                new Scenario("B12: sync SaveChunk failure stages a snapshot — edits recoverable, not just logged", SyncSaveFailureStagesSnapshot),
                new Scenario("B13: too-large chunk write — FailedPermanent, never staged, no retry loop", TooLargeWriteIsPermanent),
            };
            return ValidationSuiteRunner.Execute("Save Durability", scenarios, KnownBugChannel.Unimplemented, logToConsole, showProgress);
        }

        // --- Fixture -----------------------------------------------------------------------------

        /// <summary>Chunk-local coordinates of the scenario edit (arbitrary interior voxel).</summary>
        private const int EDIT_X = 3, EDIT_Y = 40, EDIT_Z = 7;

        /// <summary>Suite fixture: the shared <see cref="StorageValidationFixture"/> (stub
        /// <c>World.Instance</c> + volatile-path storage + all-seam disarm) under this suite's prefix.</summary>
        private sealed class Fixture : StorageValidationFixture
        {
            public Fixture() : base("SaveDurabilityTest")
            {
            }
        }

        // --- Helpers -----------------------------------------------------------------------------

        /// <summary>Builds a pooled chunk at <paramref name="pos"/> carrying one edit of <paramref name="blockId"/>.</summary>
        private static ChunkData MakeEditedChunk(Vector2Int pos, ushort blockId)
        {
            ChunkData data = World.Instance.ChunkPool.GetChunkData(pos);
            data.SetVoxel(EDIT_X, EDIT_Y, EDIT_Z, BurstVoxelDataBitMapping.PackVoxelData(blockId, 0));
            return data;
        }

        /// <summary>
        /// Runs <see cref="ChunkStorageManager.SaveChunkAsync"/> to completion. Wrapped in
        /// <see cref="Task.Run(Func{Task})"/> so its continuations resume on the ThreadPool instead of
        /// being posted to the (blocked) editor main thread — blocking directly would deadlock.
        /// </summary>
        private static ChunkSaveResult RunSave(Fixture fx, ChunkData data, CancellationToken token = default) =>
            // Deliberately NOT Task.Run's token overload: a pre-canceled token there would skip the delegate entirely, and B8 needs SaveChunkAsync itself to observe the cancellation.
            // ReSharper disable once MethodSupportsCancellation
            Task.Run(() => fx.Storage.SaveChunkAsync(data, token)).GetAwaiter().GetResult();

        /// <summary>Runs <see cref="ChunkStorageManager.LoadChunkAsync"/> to completion (same wrapping as <see cref="RunSave"/>).</summary>
        private static ChunkData RunLoad(Fixture fx, Vector2Int pos) =>
            Task.Run(() => fx.Storage.LoadChunkAsync(pos)).GetAwaiter().GetResult();

        /// <summary>Loads the chunk and checks its edit voxel matches <paramref name="expectedBlockId"/> (returns the shell to the pool).</summary>
        private static bool LoadedEditEquals(Fixture fx, Vector2Int pos, ushort expectedBlockId, string label)
        {
            ChunkData loaded = RunLoad(fx, pos);
            if (loaded == null) return Check($"{label} — chunk missing on disk", false);

            ushort id = BurstVoxelDataBitMapping.GetId(loaded.GetVoxel(EDIT_X, EDIT_Y, EDIT_Z));
            bool ok = Check($"{label} (expected id {expectedBlockId.ToString()}, got {id.ToString()})", id == expectedBlockId);
            World.Instance.ChunkPool.ReturnChunkData(loaded);
            return ok;
        }

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>B1. Red when: the happy save path breaks (contract, write, or round-trip).</summary>
        private static bool HappyRoundTrip()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 0);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);

            bool ok = Check("save reports Written", RunSave(fx, data) == ChunkSaveResult.Written);
            ok &= Check("nothing pending in the retry registry", fx.Storage.PendingFailedSaves == 0);
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Stone, "edit round-trips through the region file");

            World.Instance.ChunkPool.ReturnChunkData(data);
            return ok;
        }

        /// <summary>B2. Red when: a failed save is swallowed again (F5) or its snapshot is returned to the pool instead of the registry.</summary>
        private static bool FailureSurfacesAndRegisters()
        {
            using Fixture fx = new Fixture();
            ChunkData data = MakeEditedChunk(new Vector2Int(0, 0), BlockIDs.Stone);
            long failedBefore = ChunkStorageManager.SavesFailed;

            ChunkStorageManager.InjectSaveFaults(1);
            bool ok = Check("save reports Failed", RunSave(fx, data) == ChunkSaveResult.Failed);
            ok &= Check("SavesFailed counted the failure", ChunkStorageManager.SavesFailed > failedBefore);
            ok &= Check("registry owns exactly one pending snapshot", fx.Storage.PendingFailedSaves == 1);

            // The live data is recycled immediately in production (UnloadChunks) — mirror that here to
            // prove the registry's snapshot, not this object, carries the edits.
            World.Instance.ChunkPool.ReturnChunkData(data);
            return ok;
        }

        /// <summary>B3. The core F5 durability guarantee. Red when: the retry drain cannot land a failed save's edits on disk.</summary>
        private static bool DrainRecoversFailedSave()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(16, 0);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Grass);

            ChunkStorageManager.InjectSaveFaults(1);
            bool ok = Check("save reports Failed", RunSave(fx, data) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(data); // production recycles before the retry runs

            ok &= Check("drain recovers one save", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 1);
            ok &= Check("registry empty after recovery", fx.Storage.PendingFailedSaves == 0);
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Grass, "edit survived the failed save via retry");
            return ok;
        }

        /// <summary>B4. Red when: a failed retry drops the entry (edits lost) instead of retaining it for a later attempt.</summary>
        private static bool PersistentFaultRetainsEntry()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 16);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);

            ChunkStorageManager.InjectSaveFaults(2); // initial save + first retry both fault
            bool ok = Check("save reports Failed", RunSave(fx, data) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(data);

            ok &= Check("faulted retry recovers nothing", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 0);
            ok &= Check("entry retained after the failed retry", fx.Storage.PendingFailedSaves == 1);
            ok &= Check("next retry recovers the save", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 1);
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Stone, "edit survived two consecutive faults");
            return ok;
        }

        /// <summary>B5. Red when: a duplicate-coord failure keeps the stale snapshot (older edits would overwrite newer ones).</summary>
        private static bool NewerFailedSaveSupersedes()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(16, 16);

            ChunkData v1 = MakeEditedChunk(pos, BlockIDs.Stone);
            ChunkStorageManager.InjectSaveFaults(1);
            bool ok = Check("first save reports Failed", RunSave(fx, v1) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(v1);

            ChunkData v2 = MakeEditedChunk(pos, BlockIDs.Grass);
            ChunkStorageManager.InjectSaveFaults(1);
            ok &= Check("second save reports Failed", RunSave(fx, v2) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(v2);

            ok &= Check("drain recovers the coord once", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 1);
            ok &= Check("registry empty (older snapshot superseded, not queued)", fx.Storage.PendingFailedSaves == 0);
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Grass, "disk carries the newer edit");
            return ok;
        }

        /// <summary>B6. Red when: a load can read pre-edit bytes while that coord's failed save is still pending (stale-reload race).</summary>
        private static bool ReloadGuardFlushesBeforeLoad()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(32, 0);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Grass);

            ChunkStorageManager.InjectSaveFaults(1);
            bool ok = Check("save reports Failed", RunSave(fx, data) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(data);

            // No drain in between — the load itself must flush the pending retry first.
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Grass, "load returns the edited data, not stale disk");
            ok &= Check("registry drained by the reload guard", fx.Storage.PendingFailedSaves == 0);
            return ok;
        }

        /// <summary>B7. Red when: the quit-time flush no longer makes the final attempt on pending failed saves.</summary>
        private static bool QuitFlushLandsPendingSave()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 32);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);

            ChunkStorageManager.InjectSaveFaults(1);
            bool ok = Check("save reports Failed", RunSave(fx, data) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(data);

            ok &= Check("quit flush recovers one save", fx.Storage.FlushFailedSavesSync() == 1);
            ok &= Check("registry empty after flush", fx.Storage.PendingFailedSaves == 0);
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Stone, "edit persisted by the final flush");
            return ok;
        }

        /// <summary>B8. The manual-save-then-quit hole: a save canceled by the quit token may belong to a
        /// chunk already cleared from ModifiedChunks, so its snapshot must be staged and written by the
        /// quit-time flush. Red when: a Canceled save's snapshot is dropped (edits silently lost at quit).</summary>
        private static bool CanceledSaveStagedForQuitFlush()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(48, 0);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);

            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            bool ok = Check("save reports Canceled", RunSave(fx, data, cts.Token) == ChunkSaveResult.Canceled);
            World.Instance.ChunkPool.ReturnChunkData(data); // chunk gone from ModifiedChunks in production

            ok &= Check("canceled snapshot staged in the registry", fx.Storage.PendingFailedSaves == 1);
            ok &= Check("quit flush writes the canceled save", fx.Storage.FlushFailedSavesSync() == 1);
            ok &= Check("registry empty after flush", fx.Storage.PendingFailedSaves == 0);
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Stone, "edit persisted by the quit flush");
            return ok;
        }

        /// <summary>B9. Red when: a deterministic (zero-length serialization) failure enters the retry
        /// registry — an infinite retry loop with a permanently pinned snapshot.</summary>
        private static bool PermanentFailureNeverRetries()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(0, 48);

            // Direct save: zero-length serialization → FailedPermanent, snapshot released, nothing staged.
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);
            ChunkStorageManager.InjectZeroLengthSerializes(1);
            bool ok = Check("save reports FailedPermanent", RunSave(fx, data) == ChunkSaveResult.FailedPermanent);
            ok &= Check("nothing entered the retry registry", fx.Storage.PendingFailedSaves == 0);
            ok &= Check("nothing written to disk", RunLoad(fx, pos) == null);
            World.Instance.ChunkPool.ReturnChunkData(data);

            // Retry path: a retryable failure whose RETRY turns deterministic must drop the entry, not loop.
            ChunkData data2 = MakeEditedChunk(pos, BlockIDs.Grass);
            ChunkStorageManager.InjectSaveFaults(1);
            ok &= Check("second save reports Failed (retryable)", RunSave(fx, data2) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(data2);

            ChunkStorageManager.InjectZeroLengthSerializes(1);
            ok &= Check("deterministic retry recovers nothing", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 0);
            ok &= Check("entry dropped (no infinite retry loop)", fx.Storage.PendingFailedSaves == 0);
            ok &= Check("still nothing written to disk", RunLoad(fx, pos) == null);
            return ok;
        }

        /// <summary>B10. Red when: a pending stale registry entry is not invalidated by a NEWER
        /// successful write for its coord (the retry would regress the newer bytes), or when a failure
        /// staged AFTER a success is wrongly dropped (FIFO order broken).</summary>
        private static bool SuccessfulWriteSupersedesPendingEntry()
        {
            using Fixture fx = new Fixture();

            // Phase A: fail v1, then a newer save succeeds — the stale entry must be dropped.
            Vector2Int posA = new Vector2Int(16, 48);
            ChunkData v1 = MakeEditedChunk(posA, BlockIDs.Stone);
            ChunkStorageManager.InjectSaveFaults(1);
            bool ok = Check("v1 save reports Failed", RunSave(fx, v1) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(v1);

            ChunkData v2 = MakeEditedChunk(posA, BlockIDs.Grass);
            ok &= Check("v2 save reports Written", RunSave(fx, v2) == ChunkSaveResult.Written);
            World.Instance.ChunkPool.ReturnChunkData(v2);

            ok &= Check("drain replays nothing (stale entry superseded)", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 0);
            ok &= Check("registry empty", fx.Storage.PendingFailedSaves == 0);
            ok &= LoadedEditEquals(fx, posA, BlockIDs.Grass, "disk keeps the newer write (v1 not replayed)");

            // Phase B: fail v1 → success v2 → fail v3, drained in ONE pass — FIFO must keep v3 alive.
            Vector2Int posB = new Vector2Int(48, 16);
            ChunkData b1 = MakeEditedChunk(posB, BlockIDs.Stone);
            ChunkStorageManager.InjectSaveFaults(1);
            ok &= Check("b1 save reports Failed", RunSave(fx, b1) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(b1);

            ChunkData b2 = MakeEditedChunk(posB, BlockIDs.Stone);
            ok &= Check("b2 save reports Written", RunSave(fx, b2) == ChunkSaveResult.Written);
            World.Instance.ChunkPool.ReturnChunkData(b2);

            ChunkData b3 = MakeEditedChunk(posB, BlockIDs.Grass);
            ChunkStorageManager.InjectSaveFaults(1);
            ok &= Check("b3 save reports Failed", RunSave(fx, b3) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(b3);

            ok &= Check("drain recovers the newer failure (b3)", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 1);
            ok &= Check("registry empty after recovery", fx.Storage.PendingFailedSaves == 0);
            ok &= LoadedEditEquals(fx, posB, BlockIDs.Grass, "newer failure survived the earlier supersede");
            return ok;
        }

        /// <summary>B11. Red when: disposing the storage manager (quit / world switch) discards pending
        /// registry entries without a final write attempt — a manager swap would silently lose edits.</summary>
        private static bool DisposeFlushesPendingSaves()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(64, 0);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);

            ChunkStorageManager.InjectSaveFaults(1);
            bool ok = Check("save reports Failed", RunSave(fx, data) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(data);

            // Manager teardown with a pending entry: Dispose's final attempt must land the write.
            fx.Storage.Dispose();

            // A fresh manager on the same world (the swap) must see the edit on disk.
            ChunkStorageManager second = new ChunkStorageManager(fx.WorldName, useVolatilePath: true, SaveSystem.CURRENT_VERSION);
            try
            {
                ChunkData loaded = Task.Run(() => second.LoadChunkAsync(pos)).GetAwaiter().GetResult();
                if (loaded == null) return Check("edit survived the manager swap — chunk missing on disk", false);
                ushort id = BurstVoxelDataBitMapping.GetId(loaded.GetVoxel(EDIT_X, EDIT_Y, EDIT_Z));
                ok &= Check($"edit survived the manager swap (expected {BlockIDs.Stone.ToString()}, got {id.ToString()})", id == BlockIDs.Stone);
                World.Instance.ChunkPool.ReturnChunkData(loaded);
            }
            finally
            {
                second.Dispose();
            }

            return ok;
        }

        /// <summary>B12. Red when: a failed synchronous save (quit / force-unload loop) only logs and
        /// loses the edits instead of staging a snapshot into the retry registry.</summary>
        private static bool SyncSaveFailureStagesSnapshot()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(64, 16);
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Grass);

            ChunkStorageManager.InjectSaveFaults(1);
            fx.Storage.SaveChunk(data); // sync path — returns void; failure must stage internally
            World.Instance.ChunkPool.ReturnChunkData(data);

            bool ok = Check("failed sync save staged a snapshot", fx.Storage.PendingFailedSaves == 1);
            ok &= Check("flush recovers the sync failure", fx.Storage.FlushFailedSavesSync() == 1);
            ok &= LoadedEditEquals(fx, pos, BlockIDs.Grass, "edit survived the failed sync save");
            return ok;
        }

        /// <summary>B13. Red when: a too-large chunk write (region record limit) reports a false
        /// Written, enters the retry registry (infinite loop), or a retryable entry whose RETRY turns
        /// too-large is kept — the deterministic failure must be FailedPermanent everywhere.</summary>
        private static bool TooLargeWriteIsPermanent()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(80, 0);

            // Direct save: record-limit throw → FailedPermanent, snapshot released, nothing staged.
            ChunkData data = MakeEditedChunk(pos, BlockIDs.Stone);
            ChunkStorageManager.InjectTooLargeSaves(1);
            bool ok = Check("save reports FailedPermanent", RunSave(fx, data) == ChunkSaveResult.FailedPermanent);
            ok &= Check("nothing entered the retry registry", fx.Storage.PendingFailedSaves == 0);
            ok &= Check("nothing written to disk", RunLoad(fx, pos) == null);
            World.Instance.ChunkPool.ReturnChunkData(data);

            // Retry path: a retryable failure whose RETRY turns too-large must drop the entry, not loop.
            ChunkData data2 = MakeEditedChunk(pos, BlockIDs.Grass);
            ChunkStorageManager.InjectSaveFaults(1);
            ok &= Check("second save reports Failed (retryable)", RunSave(fx, data2) == ChunkSaveResult.Failed);
            World.Instance.ChunkPool.ReturnChunkData(data2);

            ChunkStorageManager.InjectTooLargeSaves(1);
            ok &= Check("too-large retry recovers nothing", fx.Storage.DrainFailedSaveRetries(ignoreBackoff: true) == 0);
            ok &= Check("entry dropped (no infinite retry loop)", fx.Storage.PendingFailedSaves == 0);
            ok &= Check("still nothing written to disk", RunLoad(fx, pos) == null);
            return ok;
        }
    }
}
