# Design Document: AOT World Migration System

**Version:** 1.1  
**Date:** 2026-02-24  
**Status:** Implemented (Stable)  
**Target:** Unity 6.4 (Mono Backend)  
**Context:** Infinite Voxel Engine Serialization (Region-Based)

---

## 1. Architectural Philosophy

1. **AOT Execution:** Migrations run entirely outside the game loop — before the `World` scene is loaded — to prevent stuttering and protect runtime memory pools. Heavy I/O is offloaded to `Task.Run()` to keep the UI responsive.
2. **Historical DTOs:** Migrations operate on byte arrays and private, frozen data structures defined *inside* the migration file itself. They have zero coupling to live engine classes (`ChunkData`, `ChunkSection`, `VoxelState`). A complete rewrite of those classes in the future
   cannot break old migrations.
3. **Master Versioning:** The `level.dat` version field (`SaveSystem.CURRENT_VERSION`) is the single source of truth that triggers the migration pipeline. Chunk format versions are handled as sub-routines governed by each step's `TargetChunkFormatVersion` property.
4. **Crash & Downgrade Safety:** Explicit validation prevents newer saves from loading on older clients. The backup is created atomically using a rename-based swap to prevent a partial backup from replacing a good one. Region files are written to a temp folder before being
   swapped in, preventing corruption from mid-migration power loss.

---

## 2. Migration Interface (`WorldMigrationStep.cs`)

A single abstract class handles all file types. The manager calls each method; the default implementations are no-ops so a step only needs to override what it changes.

The `TargetChunkFormatVersion` property is the key design point. It inverts control: **the manager decides whether to call `MigrateChunk`**, removing the responsibility (and the footgun) from the individual migration author. If a world version bump only touches `level.dat` — say,
because inventory item IDs changed — the developer simply does not override `TargetChunkFormatVersion`, and the manager will skip all chunk I/O entirely.

```csharp
using System;

namespace Serialization.Migration
{
    /// <summary>
    /// Represents a complete, atomic transition from one World Version to the next.
    /// Each concrete subclass defines exactly what changes between two versions.
    /// Naming convention: Migration_v{Source}_to_v{Target}_{ShortDescription}
    /// </summary>
    public abstract class WorldMigrationStep
    {
        public abstract int SourceWorldVersion { get; }
        public abstract int TargetWorldVersion { get; }

        /// <summary>
        /// Human-readable description shown in the migration progress UI.
        /// Example: "Upgrading chunk lighting format..."
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        /// Declares the chunk format version this step writes as output.
        /// Return null if this world version bump does not alter the chunk binary layout.
        /// </summary>
        /// <remarks>
        /// ⚠️ If you return a non-null value here, you MUST override <see cref="MigrateChunk"/>
        /// and write the new version byte as the FIRST byte of the returned array.
        /// Failure to do so will cause an <see cref="InvalidDataException"/> at runtime,
        /// which is intentional fail-fast behaviour during development.
        /// </remarks>
        public virtual byte? TargetChunkFormatVersion => null;

        /// <summary>
        /// Migrates the full JSON content of level.dat.
        /// After all steps run, the manager stamps the final version number — do not set it here.
        /// </summary>
        public virtual string MigrateLevelDat(string oldJson) => oldJson;

        /// <summary>
        /// Migrates the raw bytes of pending_mods.bin.
        /// </summary>
        public virtual byte[] MigratePendingMods(byte[] rawOldData) => rawOldData;

        /// <summary>
        /// Migrates the raw bytes of lighting_pending.bin.
        /// </summary>
        public virtual byte[] MigratePendingLighting(byte[] rawOldData) => rawOldData;

        /// <summary>
        /// Migrates a single UNCOMPRESSED chunk payload.
        /// The manager handles decompression before calling this and recompression after.
        /// Only called when TargetChunkFormatVersion is non-null AND the chunk's current
        /// version byte is less than TargetChunkFormatVersion.
        /// </summary>
        public virtual byte[] MigrateChunk(byte[] uncompressedChunkData)
        {
            return uncompressedChunkData;
        }
    }
}
```

---

## 3. The AOT Migration Manager (`MigrationManager.cs`)

This is the single class responsible for all I/O orchestration. It never touches `World.Instance` — all dependencies are injected explicitly by the UI caller.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Serialization.Migration
{
    public struct MigrationProgress
    {
        public string CurrentTask;
        public float PercentComplete; // 0.0f to 1.0f
        public int TotalItems;
        public int ProcessedItems;
    }

    public class MigrationManager
    {
        // Explicit registration avoids IL2CPP/AOT reflection issues.
        // Add new steps here in ascending version order.
        private readonly List<WorldMigrationStep> _steps = new List<WorldMigrationStep>
        {
            // new Migration_v1_to_v2_RemoveNeedsLight(),
        };

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Returns true if the save requires migration. Throws if the save is from the future.
        /// </summary>
        public bool RequiresMigration(int savedVersion)
        {
            if (savedVersion > SaveSystem.CURRENT_VERSION)
            {
                throw new InvalidOperationException(
                    $"World was saved with a newer version of the game (v{savedVersion}), " +
                    $"but this build only supports up to v{SaveSystem.CURRENT_VERSION}. " +
                    $"Please update your game to play this world."
                );
            }

            return savedVersion < SaveSystem.CURRENT_VERSION;
        }

        /// <summary>
        /// Runs the full AOT migration pipeline for the given world.
        /// CRITICAL: targetCompression must come from the settings file, NOT World.Instance.
        /// </summary>
        public async Task RunAOTMigrationAsync(
            string worldName,
            bool useVolatilePath,
            CompressionAlgorithm targetCompression,
            int startVersion,
            IProgress<MigrationProgress> progress)
        {
            string savePath = SaveSystem.GetSavePath(worldName, useVolatilePath);
            var migrationPath = BuildMigrationPath(startVersion, SaveSystem.CURRENT_VERSION);

            // --- Step 1: Atomic Backup ---
            // Offloaded to ThreadPool. We await it so the backup is 100% complete
            // before any migration writes begin. Uses a rename-based swap to prevent
            // a failed copy from overwriting a known-good previous backup.
            progress?.Report(new MigrationProgress { CurrentTask = "Creating Backup...", PercentComplete = 0f });
            await Task.Run(() => CreateAtomicBackup(savePath, worldName, useVolatilePath));

            // --- Step 2: Migrate Global Metadata Files ---
            // level.dat, pending_mods.bin, lighting_pending.bin
            progress?.Report(new MigrationProgress { CurrentTask = "Migrating World Metadata...", PercentComplete = 0f });
            await Task.Run(() => MigrateGlobalFiles(savePath, migrationPath));

            // --- Step 3: Migrate Region Files (Chunk Payload AOT) ---
            string regionPath = Path.Combine(savePath, "Region");
            string tempRegionPath = Path.Combine(savePath, "Region_TempMigration");

            if (!Directory.Exists(tempRegionPath))
                Directory.CreateDirectory(tempRegionPath);

            string[] regionFiles = Directory.GetFiles(regionPath, "r.*.*.bin");
            int totalRegions = regionFiles.Length;
            int processedChunksTotal = 0;

            for (int i = 0; i < totalRegions; i++)
            {
                string oldFile = regionFiles[i];
                string fileName = Path.GetFileName(oldFile);
                string tempFile = Path.Combine(tempRegionPath, fileName);

                // Progress is reported as a fraction of regions completed.
                // This is exact and avoids a second pass over all region files just to count chunks.
                progress?.Report(new MigrationProgress
                {
                    CurrentTask = $"Migrating {fileName}... ({i + 1}/{totalRegions})",
                    PercentComplete = (float)i / totalRegions,
                    ProcessedItems = processedChunksTotal,
                    TotalItems = totalRegions // Report regions as the unit of total work
                });

                // Process on a background thread; await ensures sequential, crash-safe writes.
                int chunksInRegion = await Task.Run(() =>
                    MigrateSingleRegion(oldFile, tempFile, targetCompression, migrationPath)
                );

                processedChunksTotal += chunksInRegion;

                // Safe Swap: The new region file is fully written before we touch the original.
                // If the game is force-closed between these two lines, the temp file is orphaned
                // and the original is intact. The backup also remains intact.
                File.Delete(oldFile);
                File.Move(tempFile, oldFile);
            }

            Directory.Delete(tempRegionPath);

            progress?.Report(new MigrationProgress
            {
                CurrentTask = "Migration Complete",
                PercentComplete = 1f,
                ProcessedItems = processedChunksTotal,
                TotalItems = processedChunksTotal
            });
        }

        // -------------------------------------------------------------------------
        // Private: Region Migration
        // -------------------------------------------------------------------------

        private int MigrateSingleRegion(
            string oldFile,
            string tempFile,
            CompressionAlgorithm targetCompression,
            List<WorldMigrationStep> path)
        {
            using var oldRegion = new RegionFile(oldFile);
            // Writing into a fresh RegionFile also defragments it: chunks are written
            // sequentially with no dead sectors, reducing final file size at no extra cost.
            using var newRegion = new RegionFile(tempFile);
            int chunksProcessed = 0;

            foreach (Vector2Int localCoord in oldRegion.GetAllChunkCoords())
            {
                var (compressedData, oldCompression) = oldRegion.LoadChunkData(localCoord.x, localCoord.y);
                if (compressedData == null) continue;

                // Decompress using the algorithm stored in the region file's chunk header.
                byte[] currentData = Decompress(compressedData, oldCompression);

                // Run through the migration chain. The manager owns all version checks;
                // individual steps never need to inspect the version byte themselves.
                foreach (var step in path)
                {
                    if (!step.TargetChunkFormatVersion.HasValue)
                        continue; // This world version bump didn't change chunk format. Skip.

                    // Re-read the version byte from the live data each iteration.
                    // Essential for multi-step chains: after step N runs, currentData[0]
                    // reflects the new version and step N+1 must evaluate against it, not
                    // the version from before the chain began.
                    byte currentChunkVersion = currentData[0];

                    if (currentChunkVersion < step.TargetChunkFormatVersion.Value)
                    {
                        currentData = step.MigrateChunk(currentData);

                        // Fail fast: catch null/empty returns before the next array access.
                        if (currentData == null || currentData.Length == 0)
                            throw new InvalidDataException(
                                $"Migration step '{step.GetType().Name}' returned null or empty data.");

                        // Fail fast: catch a developer forgetting to write the new version byte.
                        // The error is explicit enough to identify the offending step immediately.
                        if (currentData[0] != step.TargetChunkFormatVersion.Value)
                            throw new InvalidDataException(
                                $"Migration step '{step.GetType().Name}' ran but its output " +
                                $"version byte ({currentData[0]}) does not match its declared " +
                                $"TargetChunkFormatVersion ({step.TargetChunkFormatVersion.Value}). " +
                                $"Ensure MigrateChunk writes the new version as byte 0 of the output.");
                    }
                }

                // Recompress using the player's current target algorithm.
                // This also acts as a bulk repacker: a world saved under GZip will be
                // transparently re-compressed to LZ4 (or vice versa) during migration.
                byte[] finalCompressedData = Compress(currentData, targetCompression);
                newRegion.SaveChunkData(localCoord.x, localCoord.y, finalCompressedData, finalCompressedData.Length, targetCompression);

                chunksProcessed++;
            }

            return chunksProcessed;
        }

        // -------------------------------------------------------------------------
        // Private: Global File Migration
        // -------------------------------------------------------------------------

        private void MigrateGlobalFiles(string savePath, List<WorldMigrationStep> path)
        {
            // --- level.dat ---
            string levelDatPath = Path.Combine(savePath, "level.dat");
            if (File.Exists(levelDatPath))
            {
                string json = File.ReadAllText(levelDatPath);

                foreach (var step in path)
                    json = step.MigrateLevelDat(json);

                // Stamp the authoritative final version AFTER all steps have run.
                // Without this, RequiresMigration() would return true on every subsequent
                // load, triggering a redundant full migration pass every time.
                var saveData = JsonUtility.FromJson<WorldSaveData>(json);
                saveData.version = SaveSystem.CURRENT_VERSION;
                json = JsonUtility.ToJson(saveData, true);

                File.WriteAllText(levelDatPath, json);
            }

            // --- pending_mods.bin ---
            string modsPath = Path.Combine(savePath, "pending_mods.bin");
            if (File.Exists(modsPath))
            {
                byte[] bytes = File.ReadAllBytes(modsPath);
                foreach (var step in path) bytes = step.MigratePendingMods(bytes);
                File.WriteAllBytes(modsPath, bytes);
            }

            // --- lighting_pending.bin ---
            string lightPath = Path.Combine(savePath, "lighting_pending.bin");
            if (File.Exists(lightPath))
            {
                byte[] bytes = File.ReadAllBytes(lightPath);
                foreach (var step in path) bytes = step.MigratePendingLighting(bytes);
                File.WriteAllBytes(lightPath, bytes);
            }
        }

        // -------------------------------------------------------------------------
        // Private: Atomic Backup
        // -------------------------------------------------------------------------

        /// <summary>
        /// Creates a single rolling backup per world using a rename-based atomic swap.
        ///
        /// Why rename-based? A direct Delete → CopyDirectory sequence has a failure window:
        /// if CopyDirectory throws after the delete (e.g., disk full), the player has lost
        /// their only backup. The pattern here avoids that:
        ///   1. Write the new backup to a temp path.      (safe: old backup is untouched)
        ///   2. Delete the old backup.                    (only after new is confirmed complete)
        ///   3. Rename temp to canonical backup path.     (fast, near-atomic on same volume)
        /// </summary>
        private void CreateAtomicBackup(string savePath, string worldName, bool useVolatilePath)
        {
            string basePath = useVolatilePath
                ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
                : Path.Combine(Application.persistentDataPath, "Saves");

            string backupPath = Path.Combine(basePath, worldName + "_Backup");
            string tempBackupPath = backupPath + "_tmp";

            // Clean up any orphaned temp backup from a previous crashed attempt.
            if (Directory.Exists(tempBackupPath))
                Directory.Delete(tempBackupPath, true);

            // Step 1: Write new backup fully before touching the old one.
            CopyDirectory(savePath, tempBackupPath);

            // Step 2: Remove old backup only after new one is confirmed complete.
            if (Directory.Exists(backupPath))
                Directory.Delete(backupPath, true);

            // Step 3: Promote the new backup to the canonical path.
            Directory.Move(tempBackupPath, backupPath);
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
                file.CopyTo(Path.Combine(destinationDir, file.Name), overwrite: true);

            foreach (DirectoryInfo subDir in dir.GetDirectories())
                CopyDirectory(subDir.FullName, Path.Combine(destinationDir, subDir.Name));
        }

        // -------------------------------------------------------------------------
        // Private: Helpers
        // -------------------------------------------------------------------------

        private List<WorldMigrationStep> BuildMigrationPath(int start, int target)
        {
            var path = new List<WorldMigrationStep>();
            int current = start;

            while (current < target)
            {
                var step = _steps.FirstOrDefault(s => s.SourceWorldVersion == current);
                if (step == null)
                    throw new InvalidOperationException(
                        $"Missing migration step for v{current} → v{current + 1}. " +
                        $"Register a WorldMigrationStep with SourceWorldVersion = {current}.");

                path.Add(step);
                current = step.TargetWorldVersion;
            }

            return path;
        }

        private static byte[] Decompress(byte[] data, CompressionAlgorithm algo)
        {
            using var inMs = new MemoryStream(data);
            using var decompressor = CompressionFactory.CreateInputStream(inMs, algo);
            using var outMs = new MemoryStream();
            decompressor.CopyTo(outMs);
            return outMs.ToArray();
        }

        private static byte[] Compress(byte[] data, CompressionAlgorithm algo)
        {
            using var outMs = new MemoryStream();
            using (var compressor = CompressionFactory.CreateOutputStream(outMs, algo, leaveOpen: true))
            {
                compressor.Write(data, 0, data.Length);
            }
            return outMs.ToArray();
        }
    }
}
```

---

## 4. UI Integration (`WorldSelectMenu.cs`)

The UI is the migration system's entry point. It is responsible for:

- Reading compression settings from the settings file directly (**never** from `World.Instance`, which does not exist yet).
- Wrapping the `async void` method in a full `try/catch` so disk errors, downgrade exceptions, and migration failures surface as player-readable messages.
- Using a `migrationSucceeded` flag to gate the `finally` block, preventing a UI state reset from firing during a successful scene transition.

```csharp
using System;
using Serialization;
using Serialization.Migration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UI
{
    public partial class WorldSelectMenu : MonoBehaviour
    {
        public async void OnLoadClicked()
        {
            if (_selectedWorld == null) return;

            var migrationManager = new MigrationManager();
            bool migrationSucceeded = false;

            try
            {
                // Check for downgrades BEFORE touching UI state.
                // RequiresMigration throws InvalidOperationException for future-version saves.
                if (migrationManager.RequiresMigration(_selectedWorld.version))
                {
                    selectPanel.SetActive(false);
                    // migrationProgressPanel.SetActive(true);

                    var progress = new Progress<MigrationProgress>(status =>
                    {
                        // progressBar.value = status.PercentComplete;
                        // progressText.text = $"{status.CurrentTask} ({status.ProcessedItems}/{status.TotalItems})";
                    });

                    // CRITICAL: _settings.saveCompression is read from the settings JSON file,
                    // loaded during WorldSelectMenu.Awake(). World.Instance does not exist yet
                    // and must not be referenced here.
                    await migrationManager.RunAOTMigrationAsync(
                        _selectedWorld.worldName,
                        IsVolatileMode,
                        _settings.saveCompression,
                        _selectedWorld.version,
                        progress
                    );
                }

                // Migration completed (or was not needed). Set flag before LoadScene.
                // LoadScene begins an async transition — it does not halt execution here —
                // so without this flag the finally block would incorrectly reset UI state
                // on successful transitions.
                migrationSucceeded = true;

                WorldLaunchState.WorldName = _selectedWorld.worldName;
                WorldLaunchState.Seed = _selectedWorld.seed;
                WorldLaunchState.IsNewGame = false;

                SceneManager.LoadScene("Scenes/World", LoadSceneMode.Single);
            }
            catch (InvalidOperationException downgradeEx)
            {
                // Thrown by RequiresMigration for saves from a newer game version.
                Debug.LogError(downgradeEx.Message);
                // ShowErrorPrompt("Version Error", downgradeEx.Message);
            }
            catch (Exception ex)
            {
                // Covers: disk full, corrupted region, missing migration step, etc.
                Debug.LogError($"[Migration] Failed: {ex}");
                // ShowErrorPrompt("Migration Failed",
                //     $"An error occurred. Your backup is safe in the Saves folder.\n\nError: {ex.Message}");
            }
            finally
            {
                // Only restore the UI if we did NOT successfully transition to the World scene.
                // On success, the scene is loading; touching these panels would cause a flash.
                if (!migrationSucceeded)
                {
                    // migrationProgressPanel?.SetActive(false);
                    selectPanel.SetActive(true);
                }
            }
        }
    }
}
```

---

## 5. The "True DTO" Migration Example (`Migration_v1_to_v2_RemoveNeedsLight.cs`)

This file is a complete, self-contained historical record of the v1 chunk binary layout. A developer working on this codebase in three years can open this single file and know exactly what a v1 chunk looked like on disk — no other files are needed. Every magic number is
mathematically traced to its source.

```csharp
using System.IO;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// Hypothetical example: Removes the 'NeedsInitialLighting' boolean field from the chunk header.
    /// This field was made redundant in World v2 by the introduction of the lighting state manager.
    /// </summary>
    public class Migration_v1_to_v2_RemoveNeedsLight : WorldMigrationStep
    {
        public override int SourceWorldVersion => 1;
        public override int TargetWorldVersion => 2;
        public override string Description => "Upgrading chunk format: removing redundant lighting flag...";

        // Declare that this step writes chunk format version 2.
        // The manager uses this to decide whether to call MigrateChunk and to validate the output.
        public override byte? TargetChunkFormatVersion => 2;

        public override byte[] MigrateChunk(byte[] uncompressedData)
        {
            using var inStream = new MemoryStream(uncompressedData);
            using var reader = new BinaryReader(inStream);

            // =================================================================
            // V1 READ DEFINITION
            // Historical Reference: ChunkSerializer.cs, WriteChunkInternal()
            // =================================================================

            byte oldVersion  = reader.ReadByte();    // 1 byte  | always 1 for v1 chunks
            int  x           = reader.ReadInt32();   // 4 bytes | chunk X coordinate
            int  z           = reader.ReadInt32();   // 4 bytes | chunk Z coordinate
            bool needsLight  = reader.ReadBoolean(); // 1 byte  | THE FIELD BEING REMOVED in v2
            byte[] heightMap = reader.ReadBytes(256);// 256 bytes | 16*16 height map
            int sectionBitmask = reader.ReadInt32(); // 4 bytes | bitmask of non-empty sections (max 8)

            // --- Sections ---
            // Historical Reference: ChunkSerializer.cs, WriteSection()
            //
            // Explicit layout per section:
            //   byte       (1)     : CURRENT_SECTION_VERSION (was 1)
            //   ushort     (2)     : section.nonAirCount
            //   uint[4096] (16384) : MemoryMarshal.AsBytes(section.voxels.AsSpan())
            //                        ChunkSection.voxels is uint[ChunkMath.SECTION_VOLUME]
            //                        SECTION_VOLUME = 16*16*16 = 4096
            //                        sizeof(uint) = 4 bytes → 4096 * 4 = 16384 bytes
            //
            // Total per section: 1 + 2 + 16384 = 16387 bytes.
            //
            // Note: Only sections whose bit is set in sectionBitmask are written to disk.
            const int MAX_SECTIONS = 8;
            byte[][] v1Sections = new byte[MAX_SECTIONS][];

            for (int i = 0; i < MAX_SECTIONS; i++)
            {
                if ((sectionBitmask & (1 << i)) == 0) continue;

                byte   secVersion  = reader.ReadByte();
                ushort nonAirCount = reader.ReadUInt16();
                byte[] voxelData   = reader.ReadBytes(16384);

                using var secMs     = new MemoryStream();
                using var secWriter = new BinaryWriter(secMs);
                secWriter.Write(secVersion);
                secWriter.Write(nonAirCount);
                secWriter.Write(voxelData);
                v1Sections[i] = secMs.ToArray();
            }

            // --- Lighting Queues ---
            // Historical Reference: ChunkSerializer.cs, WriteLightQueue()
            //
            // Explicit layout:
            //   int (4) : queue.Count
            //   Per node:
            //     int  (4) : node.Position.x       (LightQueueNode.Position is Vector3Int)
            //     int  (4) : node.Position.y
            //     int  (4) : node.Position.z
            //     byte (1) : node.OldLightLevel     (LightQueueNode.OldLightLevel is byte)
            //   Total per node: 4 + 4 + 4 + 1 = 13 bytes
            //
            // Two queues are written consecutively: SunlightBfsQueue, then BlocklightBfsQueue.
            int    sunCount      = reader.ReadInt32();
            byte[] sunQueueData  = reader.ReadBytes(sunCount * 13);
            int    blockCount    = reader.ReadInt32();
            byte[] blockQueueData = reader.ReadBytes(blockCount * 13);

            // =================================================================
            // V2 WRITE DEFINITION
            // Key change: the 'needsLight' boolean is no longer written.
            // All other fields are identical to v1.
            // =================================================================

            using var outStream = new MemoryStream();
            using var writer    = new BinaryWriter(outStream);

            writer.Write((byte)2);      // NEW CHUNK VERSION — must match TargetChunkFormatVersion
            writer.Write(x);
            writer.Write(z);
            // needsLight intentionally omitted
            writer.Write(heightMap);
            writer.Write(sectionBitmask);

            for (int i = 0; i < MAX_SECTIONS; i++)
            {
                if (v1Sections[i] != null)
                    writer.Write(v1Sections[i]);
            }

            writer.Write(sunCount);
            writer.Write(sunQueueData);
            writer.Write(blockCount);
            writer.Write(blockQueueData);

            return outStream.ToArray();
        }
    }
}
```

---

## 6. Authoring Guidelines for Future Migrations

These rules apply to every new `WorldMigrationStep` written against this system.

**Always fully parse every field.** The `remainder` pattern — reading everything after a known point as an opaque blob — is forbidden. It is only safe for changes to the last field in a struct. Any change anywhere other than the final field will silently misalign all subsequent
bytes in every affected chunk. Read every field explicitly and write them in the new order.

**Always trace every magic number.** Every byte count in a DTO read must include a comment referencing the exact method and file it was derived from, plus the explicit arithmetic. `reader.ReadBytes(16384)` with no comment is not acceptable.
`reader.ReadBytes(16384) // uint[4096] * 4 = 16384, see ChunkSerializer.cs WriteSection()` is the standard.

**Do not set the version field in `level.dat` inside `MigrateLevelDat`.** The manager stamps `SaveSystem.CURRENT_VERSION` onto `level.dat` after all steps have run. Setting it inside a step can conflict with multi-step chains and will cause a discrepancy if a step is skipped.

**Register new steps in `MigrationManager._steps` in ascending version order.** Reflection-based auto-registration is not used because it has unreliable behaviour under IL2CPP with Unity's AOT compilation pipeline. Explicit registration is two lines and eliminates a class of
hard-to-diagnose runtime crashes on mobile and console builds.

**Do not use `World.Instance` anywhere in the migration pipeline.** The migration runs before the World scene loads. Any reference to `World.Instance` will be a null reference exception.

---

## 7. Architecture Decision Summary

| Decision                                             | Rationale                                                                                                                                                    |
|------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Manager owns version-check logic, not the step       | Removes the magic-number footgun from step authors. Steps declare intent; the manager enforces it.                                                           |
| Re-read `currentData[0]` each loop iteration         | Ensures consecutive steps in a chain evaluate against the actual current state, not a stale snapshot.                                                        |
| Null/empty guard before version-byte check           | Produces a clear, actionable error instead of a raw `IndexOutOfRangeException`.                                                                              |
| Post-migration version stamp in `MigrateGlobalFiles` | Prevents infinite re-migration on every subsequent load.                                                                                                     |
| Atomic backup via rename swap                        | Eliminates the window where both old and new backups are gone if `CopyDirectory` throws mid-run.                                                             |
| Region-count-based progress percentage               | Avoids opening every region file twice (once to count, once to process), halving file handle churn on large worlds.                                          |
| `Task.Run` per region, awaited sequentially          | Keeps the UI responsive without parallel writes that could produce partial regions on crash.                                                                 |
| Defragmentation as a side-effect                     | Writing into a fresh `RegionFile` eliminates dead sectors at no added cost.                                                                                  |
| Compression repack as a side-effect                  | Decompressing with the stored algorithm and recompressing with `targetCompression` transparently upgrades the entire world's compression format in one pass. |
| `migrationSucceeded` flag in `finally`               | Prevents UI state from being reset during a successful async scene transition, avoiding a visible flash on slower devices.                                   |
