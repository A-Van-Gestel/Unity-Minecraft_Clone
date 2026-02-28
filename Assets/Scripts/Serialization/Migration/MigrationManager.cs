using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serialization.Migration.Steps;
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
            // new MigrationV1ToV2Dummy()
            new MigrationV1ToV2RegionRepack()
        };

        // Track the path of the backup we create so we can roll it back if needed.
        private string _currentBackupPath;

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
                    $"World was saved with a newer version of the game's save-system (v{savedVersion}),\n" +
                    $"but this build only supports versions up to v{SaveSystem.CURRENT_VERSION}.\n\n" +
                    $"Please update your game to play this world."
                );
            }

            if (savedVersion < SaveSystem.CURRENT_VERSION)
            {
                Debug.Log($"[MigrationManager] World requires migration from v{savedVersion} to v{SaveSystem.CURRENT_VERSION}");
                return true;
            }

            Debug.Log($"[MigrationManager] World is up to date (v{savedVersion})");
            return false;
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
            Debug.Log($"[MigrationManager] Starting AOT Migration for world '{worldName}' (v{startVersion} -> v{SaveSystem.CURRENT_VERSION})...");

            string savePath = SaveSystem.GetSavePath(worldName, useVolatilePath);

            // EVALUATE UNITY APIs ON THE MAIN THREAD before offloading anything:
            string basePath = useVolatilePath
                ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
                : Path.Combine(Application.persistentDataPath, "Saves");

            // Add a timestamp to the backup to guarantee uniqueness and prevent overwrites
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _currentBackupPath = Path.Combine(basePath, $"{worldName}_Backup_v{startVersion}_{timestamp}");

            // --- Step 1: Atomic Backup ---
            // Offloaded to ThreadPool. We await it so the backup is 100% complete before any migration writes begin.
            progress?.Report(new MigrationProgress { CurrentTask = "Creating Backup...", PercentComplete = 0f });
            await Task.Run(() => CreateAtomicBackup(savePath, _currentBackupPath));

            // --- Step 2: Build Migration Path ---
            var migrationPath = BuildMigrationPath(startVersion, SaveSystem.CURRENT_VERSION);
            Debug.Log($"[MigrationManager] Built migration path with {migrationPath.Count} step(s).");

            // --- Step 3: Migrate Global Metadata Files ---
            // level.dat, pending_mods.bin, lighting_pending.bin
            progress?.Report(new MigrationProgress { CurrentTask = "Migrating World Metadata...", PercentComplete = 0.05f });
            await Task.Run(() => MigrateGlobalFiles(savePath, migrationPath));

            // --- Step 4: Migrate Region Files ---
            string regionPath = Path.Combine(savePath, "Region");
            string tempRegionPath = Path.Combine(savePath, "Region_TempMigration");
            int processedChunksTotal = 0;

            bool needsLayoutMigration = migrationPath.Any(s => s.RequiresRegionLayoutMigration);

            if (needsLayoutMigration)
            {
                // ── Layout migration: chunks may move between region files ──────────
                // A dedicated step reads the entire old Region folder and writes a
                // fully restructured set of new region files into a temp directory.
                // We then atomically swap the directories.

                if (!Directory.Exists(tempRegionPath))
                    Directory.CreateDirectory(tempRegionPath);

                foreach (var step in migrationPath.Where(s => s.RequiresRegionLayoutMigration))
                {
                    progress?.Report(new MigrationProgress
                    {
                        CurrentTask = step.Description,
                        PercentComplete = 0.1f,
                    });

                    int chunksFromStep = await Task.Run(() =>
                        step.PerformRegionLayoutMigration(regionPath, tempRegionPath, targetCompression));

                    processedChunksTotal += chunksFromStep;
                }

                progress?.Report(new MigrationProgress
                {
                    CurrentTask = "Finalising region layout...",
                    PercentComplete = 0.95f,
                    ProcessedItems = processedChunksTotal,
                    TotalItems = processedChunksTotal
                });

                // Atomic folder swap:
                // - The backup is intact.
                // - The new region folder is fully written.
                // - If the game crashes between these two lines, the temp folder is orphaned
                //   and the original (backed-up) region folder still exists.
                await Task.Run(() =>
                {
                    Directory.Delete(regionPath, recursive: true);
                    Directory.Move(tempRegionPath, regionPath);
                });
            }
            else
            {
                // ── Format-only migration: chunks stay in the same region files ────
                // Each file is migrated in-place using a temp-file-then-swap pattern.

                string[] regionFiles = Directory.GetFiles(regionPath, "r.*.*.bin");
                int totalRegions = regionFiles.Length;

                Debug.Log($"[MigrationManager] Found {totalRegions} region file(s) to migrate.");

                if (!Directory.Exists(tempRegionPath))
                    Directory.CreateDirectory(tempRegionPath);

                for (int i = 0; i < totalRegions; i++)
                {
                    string oldFile = regionFiles[i];
                    string fileName = Path.GetFileName(oldFile);
                    string tempFile = Path.Combine(tempRegionPath, fileName);

                    // Progress is reported as a fraction of regions completed.
                    progress?.Report(new MigrationProgress
                    {
                        CurrentTask = $"Migrating {fileName}... ({i + 1}/{totalRegions})",
                        PercentComplete = totalRegions == 0 ? 1f : (float)i / totalRegions,
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
            }

            progress?.Report(new MigrationProgress
            {
                CurrentTask = "Migration Complete",
                PercentComplete = 1f,
                ProcessedItems = processedChunksTotal,
                TotalItems = processedChunksTotal
            });

            Debug.Log($"[MigrationManager] Migration complete! Successfully processed {processedChunksTotal} chunk(s).");
        }

        /// <summary>
        /// Deletes the failed migration attempt and restores the backup to the original save path.
        /// </summary>
        public void RollbackMigration(string worldName, bool useVolatilePath)
        {
            if (string.IsNullOrEmpty(_currentBackupPath) || !Directory.Exists(_currentBackupPath))
                return;

            string savePath = SaveSystem.GetSavePath(worldName, useVolatilePath);

            try
            {
                // 1. Delete the corrupted / half-migrated world
                if (Directory.Exists(savePath))
                {
                    Directory.Delete(savePath, true);
                }

                // 2. Restore the backup by renaming it back to the original save path.
                // This effectively "deletes" the backup folder by consuming it.
                Directory.Move(_currentBackupPath, savePath);

                Debug.Log($"[MigrationManager] Successfully rolled back world '{worldName}' to its original state.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MigrationManager] CRITICAL: Failed to rollback migration for '{worldName}'. Manual intervention required. Error: {e.Message}");
            }
        }

        // -------------------------------------------------------------------------
        // Private: Format-Only Region Migration (single file)
        // -------------------------------------------------------------------------

        private int MigrateSingleRegion(
            string oldFile,
            string tempFile,
            CompressionAlgorithm targetCompression,
            List<WorldMigrationStep> path)
        {
            using var oldRegion = new RegionFile(oldFile);
            // Writing into a fresh RegionFile defragments it: chunks are written
            // sequentially with no dead sectors, reducing final file size at no extra cost.
            using var newRegion = new RegionFile(tempFile);
            int chunksProcessed = 0;

            foreach (Vector2Int localCoord in oldRegion.GetAllChunkCoords())
            {
                var (compressedData, oldCompression) = oldRegion.LoadChunkData(localCoord.x, localCoord.y);
                if (compressedData == null) continue;

                // Decompress using the algorithm stored in the region file's chunk header.
                byte[] currentData = Decompress(compressedData, oldCompression);

                // Run through the migration chain.
                foreach (var step in path)
                {
                    if (!step.TargetChunkFormatVersion.HasValue)
                        continue; // This world version bump didn't change chunk format. Skip.

                    // Re-read the version byte from the live data each iteration.
                    byte currentChunkVersion = currentData[0];

                    if (currentChunkVersion < step.TargetChunkFormatVersion.Value)
                    {
                        currentData = step.MigrateChunk(currentData);

                        // Fail fast: catch null/empty returns before the next array access.
                        if (currentData == null || currentData.Length == 0)
                            throw new InvalidDataException(
                                $"Migration step '{step.GetType().Name}' returned null or empty data.");

                        // Fail fast: catch forgotten version bumps
                        if (currentData[0] != step.TargetChunkFormatVersion.Value)
                            throw new InvalidDataException(
                                $"Migration step '{step.GetType().Name}' ran but its output " +
                                $"version byte ({currentData[0]}) does not match its declared " +
                                $"TargetChunkFormatVersion ({step.TargetChunkFormatVersion.Value}). " +
                                $"Ensure MigrateChunk writes the new version as byte 0 of the output.");
                    }
                }

                // Recompress using the player's current target algorithm.
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
            Debug.Log("[MigrationManager] Migrating level.dat...");
            string levelDatPath = Path.Combine(savePath, "level.dat");
            if (File.Exists(levelDatPath))
            {
                string json = File.ReadAllText(levelDatPath);

                foreach (var step in path)
                    json = step.MigrateLevelDat(json);

                // Stamp the authoritative final version AFTER all steps have run.
                var saveData = JsonUtility.FromJson<WorldSaveData>(json);
                saveData.version = SaveSystem.CURRENT_VERSION;
                json = JsonUtility.ToJson(saveData, true);

                File.WriteAllText(levelDatPath, json);
            }

            // --- pending_mods.bin ---
            Debug.Log("[MigrationManager] Migrating pending_mods.bin...");
            string modsPath = Path.Combine(savePath, "pending_mods.bin");
            if (File.Exists(modsPath))
            {
                byte[] bytes = File.ReadAllBytes(modsPath);
                foreach (var step in path) bytes = step.MigratePendingMods(bytes);
                File.WriteAllBytes(modsPath, bytes);
            }

            // --- lighting_pending.bin ---
            Debug.Log("[MigrationManager] Migrating lighting_pending.bin...");
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
        /// </summary>
        private void CreateAtomicBackup(string savePath, string backupPath)
        {
            Debug.Log($"[MigrationManager] Creating backup at: {backupPath}");
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
            Debug.Log("[MigrationManager] Backup created successfully.");
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
