using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Data;
using Serialization;
using Serialization.Migration;
using Serialization.Migration.Exceptions;
using UnityEngine;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// The <see cref="MigrationManager"/> orchestration scenarios: the happy path over a real world, the
    /// documented no-chunks path, the three fail-fast guards, the abort-and-rollback pair, and the dev
    /// corruption seam.
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        /// <summary>Folder the manager stages migrated regions in; it must never survive a completed run.</summary>
        private const string TEMP_REGION_FOLDER = "Region_TempMigration";

        /// <summary>B6. Red when: the manager fails to stamp the migrated version (an old world would be
        /// re-migrated on every load), fails to migrate level.dat's contents on disk, drops or corrupts chunk
        /// payloads that no step in the path touches, or leaves its staging folder behind. Asserts a non-zero
        /// processed-chunk count first: a fixture whose region files went missing would otherwise sail through
        /// the manager's "no chunks generated" branch and pass having migrated nothing.</summary>
        private static bool EndToEndV12World()
        {
            using MigrationFixture fx = new MigrationFixture();
            List<Vector2Int> chunks = SeedChunks(fx, FIXTURE_CHUNK_COUNT);
            SeedLevelDat(fx);

            ProgressRecorder progress = new ProgressRecorder();
            RunMigration(new MigrationManager(), fx, 12, progress);

            bool ok = Check($"the migration processed all {FIXTURE_CHUNK_COUNT.ToString()} fixture chunks (non-vacuity), got {progress.Last.ProcessedItems.ToString()}",
                progress.Last.ProcessedItems == FIXTURE_CHUNK_COUNT);
            ok &= Check($"the run reports completion, got {progress.Last.PercentComplete}",
                Mathf.Approximately(progress.Last.PercentComplete, 1f));

            WorldSaveData onDisk = ReadLevelDatFromDisk(fx);
            ok &= Check($"level.dat is stamped the current version, got v{onDisk.version.ToString()}",
                onDisk.version == SaveSystem.CURRENT_VERSION);
            ok &= Check($"the on-disk clock is seeded at noon, got tick {onDisk.worldState?.time?.ticks}",
                onDisk.worldState?.time != null && onDisk.worldState.time.ticks == MIGRATED_NOON_TICKS);
            ok &= Check($"the on-disk wind is the historical default, got {onDisk.worldState?.environment?.windX}",
                onDisk.worldState?.environment != null &&
                Mathf.Approximately(onDisk.worldState.environment.windX, HISTORICAL_WIND_X));
            ok &= Check("the on-disk player position is re-typed, not blanked",
                onDisk.player.position.Chunk.X == V12_EXPECTED_CHUNK_X &&
                onDisk.player.position.Chunk.Z == V12_EXPECTED_CHUNK_Z);
            ok &= Check($"the on-disk border radius survives to disk, got {onDisk.borderRadius.ToString()}",
                onDisk.borderRadius == 768);

            ok &= Check("the staging region folder is gone", !Directory.Exists(Path.Combine(fx.SavePath, TEMP_REGION_FOLDER)));
            ok &= Check($"the region files are still present, got {RegionFileCount(fx).ToString()}", RegionFileCount(fx) >= 1);
            ok &= AssertChunksSurvived(fx, chunks, "chunk payloads survive a level.dat-only migration");

            // The backup is the player's only recourse, so it must exist AND still hold the pre-migration document.
            DirectoryInfo[] backups = Directory.GetParent(fx.SavePath)?.GetDirectories($"{fx.WorldName}_Backup_*");
            ok &= Check($"exactly one backup folder was created, got {(backups?.Length ?? 0).ToString()}",
                backups != null && backups.Length == 1);
            if (backups is { Length: 1 })
            {
                string backupLevelDat = Path.Combine(backups[0].FullName, "level.dat");
                bool preserved = File.Exists(backupLevelDat) &&
                                 JsonUtility.FromJson<WorldSaveData>(File.ReadAllText(backupLevelDat)).version == 12;
                ok &= Check("the backup still holds the pre-migration v12 level.dat", preserved);
            }

            return ok;
        }

        /// <summary>B7. Red when: a world with no generated chunks throws instead of taking the manager's
        /// documented skip branch, or skips the level.dat stamp along with the regions (which would re-run the
        /// migration on every subsequent load). This is also why B6 asserts a non-zero chunk count — this path
        /// is what a silently empty fixture would fall into.</summary>
        private static bool NoRegionFolderCompletes()
        {
            using MigrationFixture fx = new MigrationFixture();
            SeedLevelDat(fx);

            // The storage manager's constructor creates the folder, so remove it to reach the skip branch.
            if (Directory.Exists(fx.RegionPath)) Directory.Delete(fx.RegionPath, recursive: true);

            ProgressRecorder progress = new ProgressRecorder();
            RunMigration(new MigrationManager(), fx, 12, progress);

            bool ok = Check("a chunkless world still reports completion",
                Mathf.Approximately(progress.Last.PercentComplete, 1f));
            ok &= Check("no chunks were processed", progress.Last.ProcessedItems == 0);
            ok &= Check("the region folder is still absent (nothing was fabricated)", !Directory.Exists(fx.RegionPath));

            WorldSaveData onDisk = ReadLevelDatFromDisk(fx);
            ok &= Check($"level.dat is still stamped current, got v{onDisk.version.ToString()}",
                onDisk.version == SaveSystem.CURRENT_VERSION);
            ok &= Check($"the clock is still seeded, got tick {onDisk.worldState?.time?.ticks}",
                onDisk.worldState?.time != null && onDisk.worldState.time.ticks == MIGRATED_NOON_TICKS);
            return ok;
        }

        /// <summary>B8. Red when: the manager writes a silently un-migrated chunk through as if it had been
        /// migrated. This is the failure the version-byte guard exists for and the one the shipped dev seam
        /// cannot reach (it throws rather than no-opping): a step that runs, returns its input unchanged and
        /// is trusted would leave old-format payloads inside a world stamped current — unreadable, and
        /// recoverable only from the pre-migration backup.</summary>
        private static bool SilentNoOpStepIsCaught() =>
            MisbehavingStepIsCaught(MisbehavingStep.Mode.NoOp, "a step that returns its input unchanged");

        /// <summary>B9. Red when: an empty/null step return is not caught before the next array access (the
        /// guard that turns an IndexOutOfRangeException into an actionable message).</summary>
        private static bool EmptyStepOutputIsCaught() =>
            MisbehavingStepIsCaught(MisbehavingStep.Mode.Empty, "a step that returns an empty array");

        /// <summary>B10. Red when: a step that transforms the payload but forgets to write its declared version
        /// byte is trusted — the forgotten-version-bump footgun the manager's fail-fast message names.</summary>
        private static bool WrongVersionByteIsCaught() =>
            MisbehavingStepIsCaught(MisbehavingStep.Mode.WrongVersionByte, "a step that writes the wrong version byte");

        /// <summary>
        /// Drives a migration whose single step misbehaves, asserting the manager counts every chunk as
        /// corrupted, prompts the caller rather than deciding silently, and does not write the bad payloads
        /// through. The substituted step lives on a local manager instance only.
        /// </summary>
        /// <param name="mode">The misbehavior to inject.</param>
        /// <param name="label">Human-readable description of the misbehavior, for the assertion log.</param>
        /// <returns>True when the fail-fast contract held.</returns>
        private static bool MisbehavingStepIsCaught(MisbehavingStep.Mode mode, string label)
        {
            using MigrationFixture fx = new MigrationFixture();
            List<Vector2Int> chunks = SeedChunks(fx, FIXTURE_CHUNK_COUNT);
            SeedLevelDat(fx);

            MigrationManager manager = new MigrationManager();
            SubstituteMisbehavingStep(manager, mode);

            int promptedCorrupted = -1, promptedProcessed = -1;
            int promptCount = 0;
            ProgressRecorder progress = new ProgressRecorder();
            RunMigration(manager, fx, SaveSystem.CURRENT_VERSION - 1, progress, (corrupted, processed) =>
            {
                promptCount++;
                promptedCorrupted = corrupted;
                promptedProcessed = processed;
                return Task.FromResult(true); // Continue, as the UI does when the player accepts the loss.
            });

            bool ok = Check($"{label}: the caller is prompted exactly once, got {promptCount.ToString()}", promptCount == 1);
            ok &= Check($"{label}: all {FIXTURE_CHUNK_COUNT.ToString()} chunks are counted corrupted, got {promptedCorrupted.ToString()}",
                promptedCorrupted == FIXTURE_CHUNK_COUNT);
            ok &= Check($"{label}: no chunk is counted as processed, got {promptedProcessed.ToString()}",
                promptedProcessed == 0);

            // The corrupted chunks are dropped by design (regenerated from seed), so nothing bad reaches disk.
            int reloaded = 0;

            // ChunkStorageManager exposes Dispose() without implementing IDisposable, so its teardown (which
            // flushes and releases the region file handles) has to be driven by hand here.
            ChunkStorageManager reader = new ChunkStorageManager(fx.WorldName, true, SaveSystem.CURRENT_VERSION);
            try
            {
                foreach (Vector2Int pos in chunks)
                {
                    ChunkData loaded = Task.Run(() => reader.LoadChunkAsync(pos)).GetAwaiter().GetResult();
                    if (loaded == null) continue;
                    reloaded++;
                    World.Instance.ChunkPool.ReturnChunkData(loaded);
                }
            }
            finally
            {
                reader.Dispose();
            }

            ok &= Check($"{label}: no un-migrated payload is written through, got {reloaded.ToString()} chunk(s) on disk",
                reloaded == 0);
            ok &= Check($"{label}: the staging folder is cleaned up",
                !Directory.Exists(Path.Combine(fx.SavePath, TEMP_REGION_FOLDER)));
            return ok;
        }

        /// <summary>B11. Red when: answering the corruption prompt with "rollback" does not abort, leaves the
        /// staging folder behind, or does not fully restore the world once the caller's rollback runs. Asserts
        /// the COMPOSED contract, because that is what a player experiences: the manager throws and leaves
        /// level.dat already stamped (global files are migrated before the chunk loop), and
        /// <see cref="MigrationManager.RollbackMigration"/> — which the world-select menu calls from its
        /// <c>finally</c> on any non-success — is what puts the world back.</summary>
        private static bool AbortRestoresTheWorld()
        {
            using MigrationFixture fx = new MigrationFixture();
            List<Vector2Int> chunks = SeedChunks(fx, FIXTURE_CHUNK_COUNT);
            SeedLevelDat(fx);

            MigrationManager manager = new MigrationManager();
            SubstituteMisbehavingStep(manager, MisbehavingStep.Mode.NoOp);

            bool aborted = false;
            try
            {
                RunMigration(manager, fx, SaveSystem.CURRENT_VERSION - 1, new ProgressRecorder(),
                    (_, _) => Task.FromResult(false));
            }
            catch (MigrationAbortedException)
            {
                aborted = true;
            }

            bool ok = Check("answering the prompt with rollback aborts the migration", aborted);
            ok &= Check("the staging folder is deleted on abort",
                !Directory.Exists(Path.Combine(fx.SavePath, TEMP_REGION_FOLDER)));

            // The manager stamps level.dat before the chunk loop, so the abort alone leaves it current-stamped
            // over un-migrated regions — an inconsistent state the caller's rollback is required to undo.
            ok &= Check($"the aborted run leaves level.dat already stamped current, got v{ReadLevelDatFromDisk(fx).version.ToString()}",
                ReadLevelDatFromDisk(fx).version == SaveSystem.CURRENT_VERSION);

            manager.RollbackMigration(fx.WorldName, useVolatilePath: true);

            ok &= Check($"rollback restores the v12 level.dat, got v{ReadLevelDatFromDisk(fx).version.ToString()}",
                ReadLevelDatFromDisk(fx).version == 12);
            ok &= AssertChunksSurvived(fx, chunks, "rollback restores every chunk");
            ok &= Check("rollback consumes the backup folder",
                (Directory.GetParent(fx.SavePath)?.GetDirectories($"{fx.WorldName}_Backup_*").Length ?? -1) == 0);
            return ok;
        }

        /// <summary>B12. Red when: a successful migration cannot be undone — rollback must restore the original
        /// level.dat and every chunk even when nothing went wrong, since the world-select menu calls it on any
        /// path that did not reach success.</summary>
        private static bool RollbackAfterSuccessRestoresOriginal()
        {
            using MigrationFixture fx = new MigrationFixture();
            List<Vector2Int> chunks = SeedChunks(fx, FIXTURE_CHUNK_COUNT);
            SeedLevelDat(fx);

            MigrationManager manager = new MigrationManager();
            RunMigration(manager, fx, 12, new ProgressRecorder());

            bool ok = Check("the migration stamped the world current before rollback",
                ReadLevelDatFromDisk(fx).version == SaveSystem.CURRENT_VERSION);

            manager.RollbackMigration(fx.WorldName, useVolatilePath: true);

            WorldSaveData restored = ReadLevelDatFromDisk(fx);
            ok &= Check($"level.dat is back at v12, got v{restored.version.ToString()}", restored.version == 12);
            ok &= Check("the restored document has no v15 clock section seeded",
                restored.worldState?.time == null || restored.worldState.time.ticks == 0);
            ok &= Check($"the restored border radius is the original, got {restored.borderRadius.ToString()}",
                restored.borderRadius == 768);
            ok &= AssertChunksSurvived(fx, chunks, "every chunk is restored");
            ok &= Check("the backup folder is consumed by the restore",
                (Directory.GetParent(fx.SavePath)?.GetDirectories($"{fx.WorldName}_Backup_*").Length ?? -1) == 0);
            return ok;
        }

        /// <summary>B13. Red when: the shipped dev fault injector (<c>Dev.simulateMigrationCorruption</c>) stops
        /// reaching the migration loop, or a chunk it faults is neither migrated nor reported — the accounting
        /// invariant "every chunk is either processed or counted corrupted" must hold whatever the seam does.
        /// <para>
        /// <b>The seam does not do what it reads like, and this pins what it actually does.</b> Its guard reads
        /// <c>UnityEngine.Random.value</c>, but <c>MigrateSingleRegion</c> runs on the ThreadPool, where that
        /// property throws ("get_value can only be called from the main thread"). The throw lands in the
        /// per-chunk catch, so an armed seam corrupts <b>every</b> chunk rather than the 1% its
        /// <c>Random.value &lt; 0.01f</c> suggests. Nothing seeds an RNG here for that reason — a pinned seed
        /// would be theater. This is an editor-only dev seam, so it is a broken tool rather than a shipping
        /// defect; recorded rather than filed.
        /// </para></summary>
        private static bool DevCorruptionSeamIsWired()
        {
            using MigrationFixture fx = new MigrationFixture();
            SeedChunks(fx, SEAM_CHUNK_COUNT);
            SeedLevelDat(fx);
            MigrationFixture.ArmCorruptionSeam();

            int promptedCorrupted = 0;
            ProgressRecorder progress = new ProgressRecorder();
            RunMigration(new MigrationManager(), fx, 12, progress, (corrupted, _) =>
            {
                promptedCorrupted = corrupted;
                return Task.FromResult(true);
            });

            int processed = progress.Last.ProcessedItems;
            bool ok = Check($"every chunk is accounted for: {processed.ToString()} processed + {promptedCorrupted.ToString()} corrupted == {SEAM_CHUNK_COUNT.ToString()}",
                processed + promptedCorrupted == SEAM_CHUNK_COUNT);
            ok &= Check($"the armed seam faults every chunk rather than a sampled fraction, got {promptedCorrupted.ToString()} of {SEAM_CHUNK_COUNT.ToString()}",
                promptedCorrupted == SEAM_CHUNK_COUNT);
            ok &= Check("the run still completes after faulting every chunk",
                Mathf.Approximately(progress.Last.PercentComplete, 1f));
            ok &= Check("the staging folder is cleaned up",
                !Directory.Exists(Path.Combine(fx.SavePath, TEMP_REGION_FOLDER)));
            return ok;
        }
    }
}
