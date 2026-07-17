using System;
using System.Collections.Generic;
using System.IO;
using Data;
using Data.Enums;
using Data.WorldTypes;
using Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

public static class SaveSystem
{
    // v1 → v2: Fixed region file layout (voxel-space → chunk-index-space coordinates).
    //          All V1 worlds are automatically migrated by MigrationV1ToV2RegionRepack.
    // v2 → v3: Removed bug where 'IsEmpty' sections (all-air w/ light) were skipped.
    //          Migration triggers full initial lighting job for existing chunks to restore sky & cave light.
    // v3 → v4: Added WorldType metadata to level.dat. See Migration_v3_to_v4_WorldTypes.cs.
    // v4 → v5: Collapsed VoxelMod's (Orientation, FluidLevel) byte pair into a single Meta byte
    //          per PER_BLOCK_METADATA_SCHEMAS.md §7.4. Rewrites pending_mods.bin layout.
    //          See Migration_v4_to_v5_VoxelModMeta.cs.
    // v5 → v6: Converted every voxel's metadata to schema-aware encoding per its target schema:
    //          OakLog → Axis3, Water/Lava → FluidLevel4, ordinary cubes → HorizontalOnly,
    //          Air/Facade/Cactus/GrassBlades → None. StoneHalfSlab and DirectionalBlock kept on
    //          legacy semantics; their schema migration is deferred to a future version.
    //          First chunk-format migration in the project; bumps chunk format version to 4.
    //          Affects chunks AND pending mods. See Migration_v5_to_v6_LegacyToSchemaBased.cs.
    // v6 → v7: Added global structural dimensions (chunkHeight, chunkWidth, worldSizeInChunks)
    //          to level.dat for future extensibility, and standardized the naming of the
    //          pending_lighting.bin file. See Migration_v6_to_v7_SaveFormatExtensibility.cs.
    // v8 (RGB): Expanded light queue entries from 13 to 16 bytes per entry (added OldBlockR/G/B).
    //           See Migration_v7_to_v8_RGBLightQueues.cs.
    // v9 (RGB): Persists ushort[] LightData per section with flag-based format (0x00 voxels-only,
    //           0x01 voxels+light, 0x02 light-only). See Migration_v8_to_v9_LightDataSerialization.cs.
    // v10 (RGB): Strips legacy light bits from uint voxels (bits 16-23 zeroed/reserved). Introduces
    //            uniform-sky-level optimization (flags 0x00/0x02 store 1B sky level instead of full
    //            LightData). New flag 0x03 for light-only+full. See Migration_v9_to_v10_StripLightBitsAndNewFlags.cs.
    // v10 → v11: Added spawnPosition (ChunkRelativePosition: _chunkX/_chunkZ ints + Vector3 localPosition)
    //            to level.dat for persistent spawn point support. See Migration_v10_to_v11_SpawnPosition.cs.
    // v11 → v12: Added borderRadius (float) to level.dat for the optional per-world gameplay border
    //            (TF-14). Defaults to 0 (disabled) for existing worlds. See Migration_v11_to_v12_WorldBorder.cs.
    // v12 → v13: Re-typed PlayerSaveData.position from an absolute Vector3 to ChunkRelativePosition (WS-4c), so a
    //            saved position stays exact past ±2^24 instead of rounding to whole voxels and beyond. The FIRST
    //            level.dat change that is not purely additive — hence the frozen LegacyLevelDat DTO the pre-v13
    //            steps now read, without which they would silently blank this field.
    //            See Migration_v12_to_v13_PlayerChunkRelativePosition.cs.
    public const int CURRENT_VERSION = 13;

    /// <summary>
    /// Resolves the absolute directory path where a world's save files are stored.
    /// </summary>
    /// <param name="worldName">The identifier name of the world.</param>
    /// <param name="useVolatilePath">If true, returns a temporary editor-only path instead of the persistent user path.</param>
    /// <returns>The absolute physical folder path.</returns>
    public static string GetSavePath(string worldName, bool useVolatilePath)
    {
        string baseFolder = WorldLaunchState.CurrentMode switch
        {
            RuntimeMode.Benchmark => Path.Combine(Application.persistentDataPath, "Benchmark_Saves"),
            RuntimeMode.FluidStress => Path.Combine(Application.persistentDataPath, "FluidStress_Saves"),
            _ => useVolatilePath ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves") : Path.Combine(Application.persistentDataPath, "Saves"),
        };

        return Path.Combine(baseFolder, worldName);
    }

    /// <summary>
    /// Consolidates and saves all world metadata, player state, and triggers the serialization of pending chunks/modifications to disk.
    /// </summary>
    /// <param name="world">The active world instance to snapshot.</param>
    public static void SaveWorld(World world)
    {
        string worldName = world.worldData.worldName;
        bool isVolatile = Application.isEditor && world.settings.enableVolatileSaveData;
        string path = GetSavePath(worldName, isVolatile);

        if (!Directory.Exists(path)) Directory.CreateDirectory(path);

        // --- 1. Gather Metadata ---
        WorldSaveData saveData = new WorldSaveData
        {
            version = CURRENT_VERSION,
            worldName = worldName,
            seed = world.worldData.seed,
            worldType = world.ActiveWorldType != null ? world.ActiveWorldType.typeID : WorldTypeID.Legacy,
            creationDate = world.worldData.creationDate > 0 ? world.worldData.creationDate : DateTime.UtcNow.Ticks,
            lastPlayed = DateTime.UtcNow.Ticks,
            spawnPosition = world.WorldSpawnPoint,
            borderRadius = world.BorderRadius,

            worldState = new WorldStateData
            {
                timeOfDay = world.globalLightLevel,
            },
        };

        // --- 2. Gather Player Data ---
        if (world.player != null)
        {
            saveData.player = world.player.GetSaveData();

            // Gather Inventory
            Toolbar toolbar = Object.FindAnyObjectByType<Toolbar>();
            if (toolbar != null)
            {
                saveData.player.inventory = toolbar.GetInventoryData();
            }

            // Gather Cursor Item
            DragAndDropHandler cursorHandler = Object.FindAnyObjectByType<DragAndDropHandler>();
            if (cursorHandler != null)
            {
                saveData.player.cursorItem = cursorHandler.GetCursorData();
            }
        }

        // --- 3. Write level.dat (JSON) ---
        string json = JsonUtility.ToJson(saveData, true);
        File.WriteAllText(Path.Combine(path, "level.dat"), json);

        // --- 4. Trigger Modification Manager Save ---
        // This saves pending trees/structures for unloaded chunks
        World.Instance.ModManager.Save();

        // --- 5. Trigger Lighting State Manager Save ---
        // This saves pending lighting updates for unloaded chunks
        World.Instance.LightingStateManager.Save();

        Debug.Log($"Saved World Metadata to {path}");
    }

    /// <summary>
    /// Reads and deserializes the world's core metadata file (<c>level.dat</c>) without loading heavy region terrain data.
    /// </summary>
    /// <param name="worldName">The name of the world.</param>
    /// <param name="useVolatilePath">If true, looks in the temporary editor path.</param>
    /// <returns>The deserialized <see cref="WorldSaveData"/> object, or null if the file does not exist.</returns>
    public static WorldSaveData LoadWorldMetadata(string worldName, bool useVolatilePath)
    {
        string path = Path.Combine(GetSavePath(worldName, useVolatilePath), "level.dat");
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);

            // Version-tolerant read: old documents are upgraded in memory (never on disk) so live-type parsing
            // stays correct across non-additive schema changes like the v13 position re-type.
            return LevelDatCodec.ReadNormalized(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load level.dat for {worldName}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Applies previously loaded player state (position, inventory, capabilities) and world parameters (time of day) directly to the active game state.
    /// </summary>
    /// <param name="world">The active world singleton.</param>
    /// <param name="data">The pre-loaded save data representing the state to restore.</param>
    public static void LoadWorldGameState(World world, WorldSaveData data)
    {
        if (data == null) return;

        // 1. Apply Global State
        world.globalLightLevel = data.worldState.timeOfDay;
        world.SetGlobalLightValue(); // Apply to shader immediately

        // 2. Restore Spawn Point
        world.SetSpawnPoint(data.spawnPosition);

        // Restore per-world gameplay border (0 = disabled).
        world.SetBorderRadius(data.borderRadius);

        // If the player doesn't exist, do nothing
        if (world.player == null) return;

        // 3. Apply Player State
        world.player.LoadSaveData(data.player);

        // Apply Inventory
        Toolbar toolbar = Object.FindAnyObjectByType<Toolbar>();
        if (toolbar != null)
        {
            toolbar.LoadInventoryData(data.player.inventory);
        }

        // Apply Cursor Item
        DragAndDropHandler cursorHandler = Object.FindAnyObjectByType<DragAndDropHandler>();
        if (cursorHandler != null)
        {
            cursorHandler.LoadCursorData(data.player.cursorItem);
        }
    }

    /// <summary>
    /// Returns a list of metadata for all valid saves found in the save directory.
    /// </summary>
    /// <param name="useVolatilePath">If true, targets the temporary editor-only path.</param>
    /// <returns>A list containing the <see cref="WorldSaveData"/> for all found worlds.</returns>
    public static List<WorldSaveData> GetAvailableWorlds(bool useVolatilePath)
    {
        string baseFolder = useVolatilePath
            ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
            : Path.Combine(Application.persistentDataPath, "Saves");

        if (!Directory.Exists(baseFolder)) return new List<WorldSaveData>();

        string[] directories = Directory.GetDirectories(baseFolder);
        List<WorldSaveData> worlds = new List<WorldSaveData>();

        foreach (string dir in directories)
        {
            string worldName = new DirectoryInfo(dir).Name;
            WorldSaveData data = LoadWorldMetadata(worldName, useVolatilePath);

            // Skip invalid worlds
            if (!IsWorldValid(data))
            {
                Debug.LogWarning($"Invalid world: {worldName}");
                continue;
            }

            // Skip backup worlds
            if (IsWorldBackup(worldName))
            {
                Debug.Log($"Backup world: {worldName}");
                continue;
            }

            worlds.Add(data);
        }

        // Sort by Last Played (Newest first)
        worlds.Sort((a, b) => b.lastPlayed.CompareTo(a.lastPlayed));

        return worlds;
    }

    /// <summary>
    /// Permanently deletes a world's save directory and all associated region/metadata files.
    /// </summary>
    /// <param name="worldName">The name of the world to delete.</param>
    /// <param name="useVolatilePath">If true, targets the temporary editor-only path.</param>
    public static void DeleteWorld(string worldName, bool useVolatilePath)
    {
        string path = GetSavePath(worldName, useVolatilePath);

        // If the world doesn't exist, do nothing
        if (!Directory.Exists(path)) return;

        Directory.Delete(path, true); // Recursive delete
        Debug.Log($"Deleted world: {worldName}");
    }

    /// <summary>
    /// Permanently deletes all benchmark saves to free up disk space.
    /// </summary>
    public static void ClearAllBenchmarks()
    {
        string path = Path.Combine(Application.persistentDataPath, "Benchmark_Saves");
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
            Debug.Log("Cleared all benchmark saves.");
        }
    }

    // --- Helper methods ---
    private static bool IsWorldBackup(string worldName)
    {
        return worldName.Contains("_Backup_v");
    }

    private static bool IsWorldValid(WorldSaveData data)
    {
        return data != null;
    }
}
