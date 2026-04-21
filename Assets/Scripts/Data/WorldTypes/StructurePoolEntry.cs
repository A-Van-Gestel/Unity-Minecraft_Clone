using System;
using Data.Structures;
using Jobs.Data;
using MyBox;
using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// Defines a single entry in a biome's structure spawning pool.
    /// Each entry has its own independent placement grid, allowing different
    /// structure types (trees, boulders, flowers) to coexist at different densities
    /// within the same biome.
    /// </summary>
    [Serializable]
    public struct StructurePoolEntry
    {
        [Tooltip("The composite structure template to spawn.")]
        public CompositeStructureTemplate template;

        [Header("Placement Grid")]
        [Tooltip("The grid cell size for this structure type. Smaller = denser placement, larger = sparser.")]
        [Range(1, 64)]
        public int spacing;

        [Tooltip("Minimum empty blocks from grid cell edges. " +
                 "-1 = Automatic (prevents touching in grids >= 5, allows clustering in smaller grids). " +
                 "0 = Full random within cell. Higher = closer to cell center.")]
        public int padding;

        [Tooltip("Probability [0, 1] that an elected grid cell actually spawns this structure.")]
        [Range(0f, 1f)]
        public float chance;

        [Header("Height Bounds")]
        [Tooltip("Structure will only spawn if the surface Y is at or above this value.")]
        public int minPlacementHeight;

        [Tooltip("Structure will only spawn if the surface Y is at or below this value.")]
        public int maxPlacementHeight;

        [Header("Zone Filtering")]
        [Tooltip("If true, this entry only spawns inside the biome's flora zone (controlled by the biome's Flora Zone Noise).")]
        public bool useFloraZone;

        [ConditionalField(nameof(useFloraZone))]
        [Tooltip("If true, uses a custom noise and coverage threshold instead of the biome's shared flora zone noise.")]
        public bool useOverrideFloraZoneNoise;

        [ConditionalField(nameof(useOverrideFloraZoneNoise))]
        [Tooltip("Custom 2D noise defining this entry's independent zone regions.")]
        public FastNoiseConfig overrideFloraZoneNoise;

        [ConditionalField(nameof(useOverrideFloraZoneNoise))]
        [Tooltip("Coverage percentage for this entry's override zone. Larger = larger zones, 1.0 = entire biome.")]
        [Range(0f, 1f)]
        public float overrideFloraZoneCoverage;
    }
}
