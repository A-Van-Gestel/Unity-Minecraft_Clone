using Data;
using UnityEditor;
using UnityEngine;

namespace Editor.DataGeneration
{
    /// <summary>
    /// Maintains a cached reference to the <see cref="CreditsDatabase"/> asset for Editor-only tools.
    /// Automatically refreshes on domain reload.
    /// </summary>
    public static class EditorCreditsDatabaseCache
    {
#pragma warning disable UDR0001
        private static CreditsDatabase s_database;
#pragma warning restore UDR0001

        public static CreditsDatabase Database
        {
            get
            {
                if (s_database == null) RefreshCache();
                return s_database;
            }
        }

        [InitializeOnLoadMethod]
        public static void RefreshCache()
        {
            string[] guids = AssetDatabase.FindAssets("t:CreditsDatabase");
            if (guids.Length == 0)
            {
                Debug.LogWarning("[EditorCreditsDatabaseCache] No CreditsDatabase found. " +
                                 "Create one via: Right-click in Project > Create > Minecraft > Credits Database.");
                s_database = null;
                return;
            }

            if (guids.Length > 1)
            {
                Debug.LogWarning("[EditorCreditsDatabaseCache] Multiple CreditsDatabase assets found. Using the first one.");
            }

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            s_database = AssetDatabase.LoadAssetAtPath<CreditsDatabase>(path);
        }
    }
}
