using System.Collections.Generic;
using Data;
using UnityEditor;
using UnityEngine;

namespace Editor.DataGeneration
{
    /// <summary>
    /// Maintains a fast Dictionary cache of the BlockDatabase asset for Editor-only tools.
    /// Replaces expensive AssetDatabase queries inside OnGUI loops.
    /// Automatically builds on editor compilation and domain reloads.
    /// </summary>
    public static class EditorBlockDatabaseCache
    {
#pragma warning disable UDR0001
        private static BlockDatabase s_database;
        private static Dictionary<ushort, BlockType> s_blockCache;
#pragma warning restore UDR0001

        public static BlockDatabase Database
        {
            get
            {
                if (s_database == null) RefreshCache();
                return s_database;
            }
        }

        public static IReadOnlyDictionary<ushort, BlockType> Cache
        {
            get
            {
                if (s_blockCache == null) RefreshCache();
                return s_blockCache;
            }
        }

        [InitializeOnLoadMethod]
        public static void RefreshCache()
        {
            s_blockCache = new Dictionary<ushort, BlockType>();

            string[] guids = AssetDatabase.FindAssets("t:BlockDatabase");
            if (guids.Length == 0)
            {
                Debug.LogWarning("[EditorBlockDatabaseCache] No BlockDatabase found in project.");
                s_database = null;
                return;
            }

            if (guids.Length > 1)
            {
                Debug.LogWarning("[EditorBlockDatabaseCache] Multiple BlockDatabase.asset files found. Using the first one.");
            }

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            s_database = AssetDatabase.LoadAssetAtPath<BlockDatabase>(path);

            if (s_database != null && s_database.blockTypes != null)
            {
                for (ushort i = 0; i < s_database.blockTypes.Length; i++)
                {
                    BlockType block = s_database.blockTypes[i];
                    if (block == null) continue;
                    s_blockCache[i] = block;
                }
            }
        }

        public static BlockType GetBlockType(ushort id)
        {
            if (s_blockCache == null) RefreshCache();
            return s_blockCache != null && s_blockCache.TryGetValue(id, out BlockType type) ? type : null;
        }
    }
}
