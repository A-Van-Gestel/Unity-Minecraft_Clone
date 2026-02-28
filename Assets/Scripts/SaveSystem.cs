using System;
using System.Collections.Generic;
using System.IO;
using Serialization;
using UnityEngine;
using Object = UnityEngine.Object;

public static class SaveSystem
{
    // v1 → v2: Fixed region file layout (voxel-space → chunk-index-space coordinates).
    //          All V1 worlds are automatically migrated by MigrationV1ToV2RegionRepack.
    public const int CURRENT_VERSION = 2;

    public static string GetSavePath(string worldName, bool useVolatilePath)
    {
        string baseFolder = useVolatilePath
            ? Path.Combine(Application.persistentDataPath, "Editor_Temp_Saves")
            : Path.Combine(Application.persistentDataPath, "Saves");

        return Path.Combine(baseFolder, worldName);
    }

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
            creationDate = world.worldData.creationDate > 0 ? world.worldData.creationDate : DateTime.Now.Ticks,
            lastPlayed = DateTime.Now.Ticks,

            worldState = new WorldStateData
            {
                timeOfDay = world.globalLightLevel
            }
        };

        // --- 2. Gather Player Data ---
        if (world.player != null)
        {
            saveData.player = world.player.GetSaveData();

            // Gather Inventory
            Toolbar toolbar = Object.FindFirstObjectByType<Toolbar>();
            if (toolbar != null)
            {
                saveData.player.inventory = toolbar.GetInventoryData();
            }

            // Gather Cursor Item
            DragAndDropHandler cursorHandler = Object.FindFirstObjectByType<DragAndDropHandler>();
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

    public static WorldSaveData LoadWorldMetadata(string worldName, bool useVolatilePath)
    {
        string path = Path.Combine(GetSavePath(worldName, useVolatilePath), "level.dat");
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            return JsonUtility.FromJson<WorldSaveData>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load level.dat for {worldName}: {e.Message}");
            return null;
        }
    }

    public static void LoadWorldGameState(World world, WorldSaveData data)
    {
        if (data == null) return;

        // 1. Apply Global State
        world.globalLightLevel = data.worldState.timeOfDay;
        world.SetGlobalLightValue(); // Apply to shader immediately

        // 2. Apply Player State
        if (world.player != null)
        {
            world.player.LoadSaveData(data.player);

            // Apply Inventory
            Toolbar toolbar = Object.FindFirstObjectByType<Toolbar>();
            if (toolbar != null)
            {
                toolbar.LoadInventoryData(data.player.inventory);
            }

            // Apply Cursor Item
            DragAndDropHandler cursorHandler = Object.FindFirstObjectByType<DragAndDropHandler>();
            if (cursorHandler != null)
            {
                cursorHandler.LoadCursorData(data.player.cursorItem);
            }
        }
    }

    /// <summary>
    /// Returns a list of metadata for all valid saves found in the save directory.
    /// </summary>
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
                Debug.LogWarning($"Backup world: {worldName}");
                continue;
            }

            worlds.Add(data);
        }

        // Sort by Last Played (Newest first)
        worlds.Sort((a, b) => b.lastPlayed.CompareTo(a.lastPlayed));

        return worlds;
    }

    public static void DeleteWorld(string worldName, bool useVolatilePath)
    {
        string path = GetSavePath(worldName, useVolatilePath);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true); // Recursive delete
            Debug.Log($"Deleted world: {worldName}");
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
