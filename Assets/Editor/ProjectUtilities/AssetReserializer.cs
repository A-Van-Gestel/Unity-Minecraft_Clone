using UnityEditor;
using UnityEngine;

namespace Editor.ProjectUtilities
{
    /// <summary>
    /// Provides utility methods for managing asset serialization within the project.
    /// </summary>
    public static class AssetReserializer
    {
        /// <summary>
        /// Forcibly loads and re-serializes all assets, flushing any outstanding data changes to disk.
        /// This is useful when changes in serialize field names or structs occur, and assets need to be updated.
        /// </summary>
        [MenuItem("Tools/Voxel Engine/Force Reserialize All Assets")]
        public static void ForceReserializeAllAssets()
        {
            if (EditorUtility.DisplayDialog("Force Reserialize Assets",
                    "This will forcibly load and re-serialize all assets in the project.\n\n" +
                    "This might take a while depending on the project size. Do you want to continue?",
                    "Yes, Reserialize", "Cancel"))
            {
                Debug.Log("Starting to force reserialize all assets...");
                AssetDatabase.ForceReserializeAssets();
                Debug.Log("Finished reserializing all assets.");
            }
        }
    }
}
