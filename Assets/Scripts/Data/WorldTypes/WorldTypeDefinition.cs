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
        Amplified = 2,
    }

    /// <summary>
    /// A ScriptableObject that defines the configuration for a single world generation type.
    /// </summary>
    [CreateAssetMenu(fileName = "New World Type", menuName = "Minecraft/World Type Definition")]
    public class WorldTypeDefinition : ScriptableObject
    {
        [Tooltip("The unique identifier for this world type. Must match the enum value exactly.")]
        public WorldTypeID typeID;

        [Tooltip("The user-facing name shown in the world creation UI.")]
        public string displayName;

        [Tooltip("The specific biomes available to this world type.")]
        public BiomeBase[] biomes;

        [Header("Global Settings")]
        [Tooltip("The global sea level for this world type. Empty spaces below this level generate as water. Defaults to 45 (Minecraft is 62).")]
        public int seaLevel = 45;

        [Tooltip("Legacy field. Only used by LegacyWorldGen format. Standard generation uses Biome-specific BaseTerrainHeight.")]
        public int solidGroundHeight = 42;
    }
}
