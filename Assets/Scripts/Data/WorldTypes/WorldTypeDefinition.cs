using System;
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

        [Header("Trunk Worm Layer")]
        [Tooltip("World-level trunk worm configuration. Trunk worms create long cross-biome cave highways " +
                 "that provide the exploration backbone. Leave disabled for worlds without trunk caves.")]
        public TrunkWormConfig trunkWormConfig;
    }

    /// <summary>
    /// World-level configuration for Tier 1 (Trunk) worm carvers.
    /// Trunk worms create long cross-biome cave highways using a deterministic
    /// world-level scatter grid, independent of per-biome cave layers.
    /// </summary>
    [Serializable]
    public class TrunkWormConfig
    {
        [Tooltip("Enable trunk worm generation for this world type.")]
        public bool enabled;

        [Header("Spawn")]
        [Range(0f, 1f)]
        [Tooltip("Probability [0, 1] that a scatter grid cell spawns a trunk worm system. " +
                 "0.005-0.01 = sparse highway network. 0.02+ = dense network.")]
        public float spawnChance = 0.008f;

        [Range(1, 5)]
        [Tooltip("Maximum number of trunk worms per scatter grid cell if it passes the spawn check.")]
        public int maxWormsPerCell = 1;

        [Header("Shape")]
        [Tooltip("Cross-section shape configuration controlling radius variation, squash profile, and noise modulation.")]
        public WormShape shape = WormShape.TrunkDefault;

        [Range(0.1f, 1f)]
        [Tooltip("How strongly the trunk worm perturbs its pitch/yaw angles per step.")]
        public float waviness = 0.33f;

        [Range(0f, 1f)]
        [Tooltip("How strongly trunk worms are pulled toward horizontal. " +
                 "0.6-0.8 recommended for maximum biome crossings.")]
        public float horizontalBias = 0.7f;

        [Header("Length")]
        [Range(50, 500)]
        [Tooltip("Minimum number of steps the trunk worm will march.")]
        public int minLength = 200;

        [Range(100, 800)]
        [Tooltip("Maximum number of steps the trunk worm will march.")]
        public int maxLength = 400;

        [Header("Depth Bounds")]
        [Tooltip("Trunk worms will not spawn below this Y level.")]
        public int minHeight = 10;

        [Tooltip("Trunk worms will not spawn above this Y level.")]
        public int maxHeight = 50;

        [Range(0, 32)]
        [Tooltip("Number of blocks over which trunk worm carving fades in near the MinHeight (bottom) bound. 0 = hard cutoff.")]
        public int depthFadeMarginBottom = 16;

        [Range(0, 32)]
        [Tooltip("Number of blocks over which trunk worm carving fades out near the MaxHeight (top) bound. 0 = hard cutoff.")]
        public int depthFadeMarginTop = 16;

        [Header("Y-Level Attraction")]
        [Tooltip("Y-level attraction configuration controlling how trunk worms are pulled toward a target depth band.")]
        public WormYAttraction yAttraction = WormYAttraction.TrunkDefault;

        [Header("Branching")]
        [Tooltip("Branching configuration controlling how trunk worms split into child tunnels.")]
        public WormBranching branching = WormBranching.TrunkDefault;

        [Header("Noise Seeking")]
        [Tooltip("Noise seeking configuration for trunk worms. Trunk worms seek layers flagged with isSeekableByTrunkWorms.")]
        public WormNoiseSeeking noiseSeeking = WormNoiseSeeking.Default;
    }
}
