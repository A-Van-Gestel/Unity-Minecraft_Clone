using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// Identifies the world generation algorithm used by a world.
    /// The byte value is serialized into level.dat and must remain stable.
    /// </summary>
    public enum WorldTypeID : byte
    {
        /// <summary>Maps to the old Mathf.PerlinNoise generation. 0 is crucial for implicit JSON backwards compatibility.</summary>
        Legacy = 0,

        /// <summary>Maps to the new FastNoiseLite Burst generation.</summary>
        Standard = 1,

        /// <summary>Reserved for future expansion.</summary>
        Amplified = 2
    }

    /// <summary>
    /// A ScriptableObject that defines the configuration for a single world generation type.
    /// </summary>
    [CreateAssetMenu(fileName = "New World Type", menuName = "Minecraft/World Type Definition")]
    public class WorldTypeDefinition : ScriptableObject
    {
        [Tooltip("The unique identifier for this world type. Must match the enum value exactly.")]
        public WorldTypeID TypeID;

        [Tooltip("The user-facing name shown in the world creation UI.")]
        public string DisplayName;

        [Tooltip("The specific biomes available to this world type.")]
        public BiomeBase[] Biomes;

        [Tooltip("Global terrain scaling parameters for this specific world type.")]
        public float BaseTerrainHeight = 42f;
    }
}
