using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Data;
using Editor.Validation.Framework;
using Jobs.BurstData;
using Serialization;
using Serialization.Migration;
using UnityEngine;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// The on-disk half of the chain: <see cref="MigrationManager.RunAOTMigrationAsync"/> driven over a real
    /// volatile-path world — the version stamp, the chunk loop, the backup, the corruption prompt and the
    /// rollback. These scenarios are the first coverage <see cref="MigrationManager"/> has of any kind.
    /// <para>
    /// The fixture world is written at v12 and migrated to current, which is deliberately the cheapest
    /// faithful shape: no step between v12 and v15 touches the chunk payload or the region layout, so real
    /// current-format chunks written by <see cref="ChunkStorageManager"/> are a byte-faithful stand-in for a
    /// v12 world's region files (v12 and v15 resolve the same V2 address codec). Covering a source version
    /// whose chunk payload DOES change needs a historical chunk-format writer per era — roadmap NS-7b.
    /// </para>
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        /// <summary>Chunks the fixture world carries, so a migration that processes none cannot pass vacuously.</summary>
        private const int FIXTURE_CHUNK_COUNT = 3;

        /// <summary>Chunk count for the fault-seam scenario: enough that its 1%-per-chunk injection lands.</summary>
        private const int SEAM_CHUNK_COUNT = 40;

        /// <summary>Seed pinning the fault seam's injection sequence, so the scenario is deterministic.</summary>
        private const int SEAM_RANDOM_SEED = 20260820;

        /// <summary>Chunk-local coordinates of the fixture edit, read back after migration to prove the payload survived.</summary>
        private const int EDIT_X = 5, EDIT_Y = 12, EDIT_Z = 9;

        /// <summary>Block id written at the fixture edit — a synthetic value, only ever compared to itself.</summary>
        private const ushort EDIT_BLOCK_ID = 42;

        // --- Fixture -------------------------------------------------------------------------------

        /// <summary>
        /// Suite fixture: the shared <see cref="StorageValidationFixture"/> plus the teardown the migration
        /// pipeline needs and the base class cannot know about — the sibling backup folders
        /// <see cref="MigrationManager"/> creates outside the save path, the dev corruption-simulation flag on
        /// the cached settings singleton, and the global <see cref="UnityEngine.Random"/> state its injection
        /// consumes. All three outlive a play session if left set.
        /// </summary>
        private sealed class MigrationFixture : StorageValidationFixture
        {
            private readonly bool _previousSimulateCorruption;
            private readonly UnityEngine.Random.State _previousRandomState;

            /// <summary>Whether the globals were captured — false if the base constructor failed first, in which
            /// case there is nothing to restore and the captured fields still hold their defaults.</summary>
            private readonly bool _captured;

            /// <summary>Creates the stub world, capturing the globals the migration pipeline mutates.</summary>
            public MigrationFixture() : base("MigrationChainTest")
            {
                _previousSimulateCorruption = SettingsManager.LoadSettings().Dev.simulateMigrationCorruption;
                _previousRandomState = UnityEngine.Random.state;
                _captured = true;
            }

            /// <summary>Absolute path of the fixture world's save folder.</summary>
            public string SavePath => SaveSystem.GetSavePath(WorldName, useVolatilePath: true);

            /// <summary>Absolute path of the fixture world's <c>level.dat</c>.</summary>
            public string LevelDatPath => Path.Combine(SavePath, "level.dat");

            /// <summary>Absolute path of the fixture world's region folder.</summary>
            public string RegionPath => Path.Combine(SavePath, "Region");

            /// <summary>
            /// Arms the dev-only migration corruption injector on the cached settings singleton (in memory —
            /// nothing is written to the user's settings file) with a pinned RNG sequence.
            /// </summary>
            public static void ArmCorruptionSeam()
            {
                SettingsManager.LoadSettings().Dev.simulateMigrationCorruption = true;
                UnityEngine.Random.InitState(SEAM_RANDOM_SEED);
            }

            /// <summary>Restores the mutated globals, sweeps the migration backups, then tears the base fixture down.</summary>
            public override void Dispose()
            {
                if (_captured)
                {
                    SettingsManager.LoadSettings().Dev.simulateMigrationCorruption = _previousSimulateCorruption;
                    UnityEngine.Random.state = _previousRandomState;
                }

                DeleteBackups();
                base.Dispose();
            }

            /// <summary>
            /// Deletes the timestamped backup folders the manager writes as SIBLINGS of the save path — the base
            /// fixture only removes the save folder itself, so without this every run leaks one per migration.
            /// </summary>
            private void DeleteBackups()
            {
                try
                {
                    DirectoryInfo parent = Directory.GetParent(SavePath);
                    if (parent == null || !parent.Exists) return;

                    foreach (DirectoryInfo backup in parent.GetDirectories($"{WorldName}_Backup_*"))
                        backup.Delete(recursive: true);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MigrationChain] Could not sweep migration backups: {e.Message}");
                }
            }
        }

        /// <summary>Captures the last progress report, which carries the manager's processed-chunk total.</summary>
        private sealed class ProgressRecorder : IProgress<MigrationProgress>
        {
            /// <summary>The most recent report received.</summary>
            public MigrationProgress Last;

            /// <summary>Records a progress report.</summary>
            /// <param name="value">The report from the migration pipeline.</param>
            public void Report(MigrationProgress value) => Last = value;
        }

        /// <summary>
        /// A synthetic chunk-format step standing in for v14→v15, declaring a chunk-format bump so the manager
        /// calls <see cref="MigrateChunk"/>, and then misbehaving in a chosen way. Exercises the manager's
        /// fail-fast guards with real inputs — the shipped dev seam only ever throws, so nothing else can
        /// reach the "step ran but did not transform" path.
        /// </summary>
        private sealed class MisbehavingStep : WorldMigrationStep
        {
            /// <summary>How the step should misbehave.</summary>
            public enum Mode
            {
                /// <summary>Returns its input unchanged — the silent no-op.</summary>
                NoOp,

                /// <summary>Returns an empty array.</summary>
                Empty,

                /// <summary>Transforms but writes the wrong version byte.</summary>
                WrongVersionByte,
            }

            /// <summary>
            /// Chunk-format version this step claims to write. Its only contract is being *above* the real
            /// <c>ChunkSerializer.CURRENT_CHUNK_VERSION</c>, so the manager's version check always calls
            /// <see cref="MigrateChunk"/> in; the specific value carries no meaning.
            /// </summary>
            private const byte SYNTHETIC_TARGET_CHUNK_VERSION = 0xFE;

            /// <summary>Version byte the <see cref="Mode.WrongVersionByte"/> arm writes — any value that is
            /// neither the input's nor the declared target.</summary>
            private const byte MISMATCHED_VERSION_BYTE = 0x7F;

            private readonly Mode _mode;

            /// <summary>Creates the step in the given failure mode.</summary>
            /// <param name="mode">The misbehavior to exhibit.</param>
            public MisbehavingStep(Mode mode) => _mode = mode;

            /// <inheritdoc />
            public override int SourceWorldVersion => SaveSystem.CURRENT_VERSION - 1;

            /// <inheritdoc />
            public override int TargetWorldVersion => SaveSystem.CURRENT_VERSION;

            /// <inheritdoc />
            public override string Description => "Synthetic misbehaving step (validation only)";

            /// <inheritdoc />
            public override string ChangeSummary => "Validation fixture — never registered in production.";

            /// <summary>Declares a format bump past the current chunk version, so the manager always calls in.</summary>
            public override byte? TargetChunkFormatVersion => SYNTHETIC_TARGET_CHUNK_VERSION;

            /// <summary>Misbehaves per the configured <see cref="Mode"/>.</summary>
            /// <param name="uncompressedChunkData">The chunk payload handed over by the manager.</param>
            /// <returns>Deliberately invalid output.</returns>
            public override byte[] MigrateChunk(byte[] uncompressedChunkData)
            {
                switch (_mode)
                {
                    case Mode.Empty:
                        return Array.Empty<byte>();
                    case Mode.WrongVersionByte:
                        byte[] wrong = (byte[])uncompressedChunkData.Clone();
                        wrong[0] = MISMATCHED_VERSION_BYTE; // Transformed, but not to the declared target.
                        return wrong;
                    case Mode.NoOp:
                    default:
                        return uncompressedChunkData; // The version byte never advances.
                }
            }
        }

        // --- Helpers -------------------------------------------------------------------------------

        /// <summary>
        /// Writes <paramref name="count"/> real current-format chunks into the fixture world's region files,
        /// each carrying a distinct edit, and disposes the writer so its region handles are released before
        /// the migration opens the same files.
        /// </summary>
        /// <param name="fx">The fixture whose world is being populated.</param>
        /// <param name="count">How many chunks to write.</param>
        /// <returns>The voxel-space origins of the chunks written, in write order.</returns>
        private static List<Vector2Int> SeedChunks(MigrationFixture fx, int count)
        {
            List<Vector2Int> written = new List<Vector2Int>(count);

            // ChunkStorageManager exposes Dispose() without implementing IDisposable, so `using` is unavailable;
            // the explicit teardown matters here because it releases the region handles the migration reopens.
            ChunkStorageManager writer = new ChunkStorageManager(fx.WorldName, true, SaveSystem.CURRENT_VERSION);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Vector2Int pos = new Vector2Int(i * 16, 0);
                    ChunkData data = World.Instance.ChunkPool.GetChunkData(pos);
                    data.SetVoxel(EDIT_X, EDIT_Y, EDIT_Z, BurstVoxelDataBitMapping.PackVoxelData(EDIT_BLOCK_ID, 0));
                    Task.Run(() => writer.SaveChunkAsync(data)).GetAwaiter().GetResult();

                    // SaveChunkAsync serializes its OWN snapshot and returns only that one to the pool, so the
                    // lease above is still ours to give back (the Deserialization Robustness suite pairs its
                    // MakeEditedChunk with the same explicit return). Without this the seam scenario alone
                    // strands 40 pooled ChunkData, and no pool-balance assertion could ever be added here.
                    World.Instance.ChunkPool.ReturnChunkData(data);
                    written.Add(pos);
                }
            }
            finally
            {
                writer.Dispose();
            }

            return written;
        }

        /// <summary>Writes the fixture's v12 <c>level.dat</c> so the manager has a real document to migrate.</summary>
        /// <param name="fx">The fixture whose world is being populated.</param>
        private static void SeedLevelDat(MigrationFixture fx) => File.WriteAllText(fx.LevelDatPath, V12_LEVEL_DAT);

        /// <summary>
        /// Runs the migration to completion.
        /// <para>
        /// Unlike the other storage suites' helpers, this must NOT wrap the call in
        /// <see cref="Task.Run(Func{Task})"/>: <see cref="MigrationManager.RunAOTMigrationAsync"/> resolves
        /// <see cref="SaveSystem.GetSavePath"/> in its synchronous prologue — deliberately, so the Unity API is
        /// touched on the main thread before anything is offloaded — and that throws on a worker. So the call is
        /// started here on the main thread with the synchronization context suppressed, which sends its internal
        /// <c>await</c> continuations to the ThreadPool and makes blocking on the result deadlock-free.
        /// </para>
        /// <para>
        /// The fixture constructor's <c>LoadSettings()</c> is load-bearing for the same reason: it warms the
        /// settings singleton on the main thread, so the migration loop's off-thread read of the dev corruption
        /// flag hits the cache instead of resolving a Unity path from a worker.
        /// </para>
        /// </summary>
        /// <param name="manager">The manager instance to drive.</param>
        /// <param name="fx">The fixture world to migrate.</param>
        /// <param name="startVersion">The world version the fixture's level.dat declares.</param>
        /// <param name="progress">Progress recorder capturing the processed-chunk total.</param>
        /// <param name="onCorruption">Optional corruption-prompt handler.</param>
        private static void RunMigration(
            MigrationManager manager,
            MigrationFixture fx,
            int startVersion,
            ProgressRecorder progress,
            Func<int, int, Task<bool>> onCorruption = null)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                manager.RunAOTMigrationAsync(fx.WorldName, true, CompressionAlgorithm.LZ4, startVersion, progress, onCorruption)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        /// <summary>
        /// Swaps a misbehaving synthetic step into a LOCAL manager instance's step list, replacing the real
        /// step with the same version pair. The list is a private instance field, so nothing global is
        /// touched — a second manager built in the same session gets the production chain.
        /// </summary>
        /// <param name="manager">The manager instance to sabotage.</param>
        /// <param name="mode">The misbehavior the substituted step should exhibit.</param>
        private static void SubstituteMisbehavingStep(MigrationManager manager, MisbehavingStep.Mode mode)
        {
            List<WorldMigrationStep> steps = (List<WorldMigrationStep>)ValidationReflection.GetInstanceField(manager, "_steps");
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].SourceWorldVersion != SaveSystem.CURRENT_VERSION - 1) continue;
                steps[i] = new MisbehavingStep(mode);
                return;
            }

            throw new InvalidOperationException(
                $"No step with SourceWorldVersion {SaveSystem.CURRENT_VERSION - 1} to substitute.");
        }

        /// <summary>Reads the migrated <c>level.dat</c> straight off disk (no codec normalization).</summary>
        /// <param name="fx">The fixture whose level.dat to read.</param>
        /// <returns>The parsed document as the manager left it.</returns>
        private static WorldSaveData ReadLevelDatFromDisk(MigrationFixture fx) =>
            JsonUtility.FromJson<WorldSaveData>(File.ReadAllText(fx.LevelDatPath));

        /// <summary>Counts the region files in the fixture world, or -1 when the folder is gone.</summary>
        /// <param name="fx">The fixture to inspect.</param>
        /// <returns>The number of <c>r.*.*.bin</c> files present.</returns>
        private static int RegionFileCount(MigrationFixture fx) =>
            Directory.Exists(fx.RegionPath) ? Directory.GetFiles(fx.RegionPath, "r.*.*.bin").Length : -1;

        /// <summary>Asserts every seeded chunk still loads and still carries its fixture edit.</summary>
        /// <param name="fx">The migrated fixture world.</param>
        /// <param name="positions">The chunk origins that were seeded.</param>
        /// <param name="label">Assertion label prefix.</param>
        /// <returns>True when every chunk survived intact.</returns>
        private static bool AssertChunksSurvived(MigrationFixture fx, List<Vector2Int> positions, string label)
        {
            int intact = 0;
            ChunkStorageManager reader = new ChunkStorageManager(fx.WorldName, true, SaveSystem.CURRENT_VERSION);
            try
            {
                foreach (Vector2Int pos in positions)
                {
                    ChunkData loaded = Task.Run(() => reader.LoadChunkAsync(pos)).GetAwaiter().GetResult();
                    if (loaded == null) continue;

                    uint voxel = loaded.GetVoxel(EDIT_X, EDIT_Y, EDIT_Z);
                    if (BurstVoxelDataBitMapping.GetId(voxel) == EDIT_BLOCK_ID) intact++;
                    World.Instance.ChunkPool.ReturnChunkData(loaded);
                }
            }
            finally
            {
                reader.Dispose();
            }

            return Check($"{label} ({intact.ToString()}/{positions.Count.ToString()} chunks reload with their edit intact)",
                intact == positions.Count);
        }
    }
}
