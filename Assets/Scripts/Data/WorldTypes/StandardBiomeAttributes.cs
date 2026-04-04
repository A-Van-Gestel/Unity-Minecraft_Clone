using System;
using Jobs.Data;
using UnityEngine;

namespace Data.WorldTypes
{
    /// <summary>
    /// Authoring ScriptableObject for a Standard (FastNoiseLite-based) biome.
    /// Fields map directly to <see cref="StandardBiomeAttributesJobData"/> for job consumption.
    /// </summary>
    [CreateAssetMenu(fileName = "New Standard Biome", menuName = "Minecraft/Standard Biome Attributes")]
    public class StandardBiomeAttributes : BiomeBase
    {
        [Tooltip("The name of the biome, mostly for debug purposes.")]
        public string BiomeName = "New Biome";

        [Header("Terrain Noise")]
        [Tooltip("Noise configuration for the terrain heightmap.")]
        public FastNoiseConfig TerrainNoiseConfig;

        [Tooltip("Noise configuration for biome weight / Voronoi selection.")]
        public FastNoiseConfig BiomeWeightNoiseConfig;

        [Header("Terrain Shape")]
        [Tooltip("Base terrain height in blocks. Noise output is added to this value.")]
        public float BaseTerrainHeight = 42f;

        [Tooltip("Vertical multiplier for terrain noise (e.g., 20 means hills reach BaseTerrainHeight ± 20). " +
                 "FastNoiseLite returns normalized -1.0 to 1.0; this gives it physical scale.")]
        public float TerrainAmplitude = 20f;

        [Header("Surface Blocks")]
        [Tooltip("Block ID for the surface layer (e.g., Grass).")]
        public byte SurfaceBlockID;

        [Tooltip("Block ID for the sub-surface layers (e.g., Dirt).")]
        public byte SubSurfaceBlockID;

        [Header("Major Flora")]
        [Tooltip("If true, flora like trees or cacti will be generated in this biome.")]
        public bool EnableMajorFlora = true;

        [Tooltip("Placement threshold for major flora. Higher value = fewer trees/cacti.")]
        public float MajorFloraPlacementThreshold;

        [Tooltip("Flora type index dispatched to ExpandFlora (0 = tree, 1 = cactus, etc.).")]
        public byte MajorFloraIndex;

        [Header("Lodes (Ore Veins)")]
        [Tooltip("Ore vein configurations for this biome.")]
        public StandardLode[] Lodes;
    }

    /// <summary>
    /// Authoring class for a Standard lode (ore vein).
    /// Uses <see cref="FastNoiseConfig"/> for full FastNoiseLite noise control.
    /// </summary>
    [Serializable]
    public class StandardLode
    {
        [Tooltip("Name of the lode.")]
        public string nodeName;

        [Tooltip("ID of the block that will be generated.")]
        public byte blockID;

        [Tooltip("Blocks will not be generated below this height.")]
        public int minHeight;

        [Tooltip("Blocks will not be generated above this height.")]
        public int maxHeight;

        [Tooltip("FastNoiseLite noise configuration for this lode's generation pattern.")]
        public FastNoiseConfig noiseConfig;
    }
}
