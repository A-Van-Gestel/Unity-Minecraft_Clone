using System;
using Jobs.Data;
using UnityEngine;
using UnityEngine.Serialization;

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

        [Tooltip("2D noise defining coherent regions (groves/forests) where flora can generate. " +
                 "Only positions where this noise exceeds MajorFloraZoneThreshold are eligible for placement.")]
        public FastNoiseConfig MajorFloraZoneNoiseConfig;

        [Tooltip("Percentage of the biome covered by flora zones. " +
                 "Larger = larger/more frequent zones, lower = smaller/rarer zones. 1.0 = entire biome is a zone.")]
        [Range(0f, 1f)]
        [FormerlySerializedAs("MajorFloraZoneThreshold")]
        public float MajorFloraZoneCoverage = 0.4f;

        [Header("Flora Placement (Spacing)")]
        [Tooltip("The minimum grid size for flora. Smaller = denser forest, Larger = sparser forest. Evaluated deterministically.")]
        [Range(1, 64)]
        public int MajorFloraPlacementSpacing = 5;
        
        [Tooltip("Minimum empty blocks to maintain between the tree and the grid cell edges. " +
                 "-1 = Automatic (Allows touching in dense small grids, prevents touching in larger grids >= 5). " +
                 "0 = Full random placement allowing trees to naturally clump and touch. " +
                 "Higher values force trees closer to the exact center of the grid cell.")]
        [FormerlySerializedAs("MajorFloraPlacementJitter")]
        public int MajorFloraPlacementPadding = -1;

        [Tooltip("Probability that a valid spacing slot will actually spawn a tree. 1.0 = every slot is filled, lower = sparser.")]
        [Range(0f, 1f)]
        [FormerlySerializedAs("MajorFloraPlacementThreshold")]
        public float MajorFloraPlacementChance = 0.5f;

        [Tooltip("Flora type index dispatched to ExpandFlora (0 = tree, 1 = cactus, etc.).")]
        public byte MajorFloraIndex;

        [Header("Lodes (Ore Veins)")]
        [Tooltip("Ore vein configurations for this biome.")]
        public StandardLode[] Lodes;

        [Header("Cave Generation")]
        [Tooltip("Layered noise configurations for generating 3D caves (e.g., cheese and spaghetti networks).")]
        public StandardCaveLayer[] CaveLayers;
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

    /// <summary>
    /// Determines the noise evaluation strategy for a cave layer.
    /// </summary>
    public enum CaveMode : byte
    {
        /// <summary>Blob (Single Noise) — Standard single 3D noise threshold. Produces chambers and pockets.</summary>
        Blob,

        /// <summary>Spaghetti (Axis-Pair Average) — Legacy-style 6-way 2D noise averaging. Produces interconnected tunnel networks.</summary>
        Spaghetti,
    }

    /// <summary>
    /// Authoring class for a 3D cave layer.
    /// Evaluates 3D noise locally inside a biome to carve out solid terrain blocks.
    /// Supports multiple evaluation modes via <see cref="CaveMode"/>.
    /// </summary>
    [Serializable]
    public class StandardCaveLayer
    {
        [Tooltip("Name of the cave layer configuration.")]
        public string LayerName = "New Cave Layer";

        [Tooltip("Blob (Single Noise) produces chambers. Spaghetti (Axis-Pair Average) produces interconnected tunnel networks.")]
        public CaveMode Mode = CaveMode.Blob;

        [Tooltip("FastNoiseLite noise configuration for defining the cave shapes.")]
        public FastNoiseConfig NoiseConfig;

        [Tooltip("If the evaluated noise exceeds this threshold, the block is carved into air.")]
        public float Threshold = 0.5f;

        [Header("Depth Bounds")]
        [Tooltip("Caves will not generate below this Y level.")]
        public int MinHeight = 5;

        [Tooltip("Caves will not generate above this Y level.")]
        public int MaxHeight = 60;

        [Tooltip("Number of blocks over which the carving fades in/out near MinHeight and MaxHeight bounds. 0 = hard cutoff.")]
        [Range(0, 32)]
        public int DepthFadeMargin = 8;
    }
}
