using System;
using Jobs.Data;
using MyBox;
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
        public string biomeName = "New Biome";

        [Header("Terrain Noise")]
        [Tooltip("Noise configuration for the terrain heightmap.")]
        public FastNoiseConfig terrainNoiseConfig;

        [Tooltip("Noise configuration for biome weight / Voronoi selection.")]
        public FastNoiseConfig biomeWeightNoiseConfig;

        [Range(0.01f, 1.0f)]
        [Tooltip("Defines how far this biome pushes its height influence onto neighboring biomes out from the Voronoi edge.")]
        public float blendRadius = 0.2f;

        [Header("Terrain Shape")]
        [Tooltip("Base terrain height in blocks. Noise output is added to this value.")]
        public float baseTerrainHeight = 42f;

        [Tooltip("Vertical multiplier for terrain noise (e.g., 20 means hills reach BaseTerrainHeight ± 20). " +
                 "FastNoiseLite returns normalized -1.0 to 1.0; this gives it physical scale.")]
        public float terrainAmplitude = 20f;

        [Header("Surface Blocks")]
        [ConstantsSelection(typeof(BlockIDs))]
        [Tooltip("Block ID for the surface layer (e.g., Grass).")]
        public ushort surfaceBlockID;

        [ConstantsSelection(typeof(BlockIDs))]
        [Tooltip("Block ID for the sub-surface layers (e.g., Dirt).")]
        [Obsolete("Replaced by terrainLayers")]
        [HideInInspector]
        public ushort subSurfaceBlockID;

        [Header("Terrain Layers")]
        [Tooltip("The blocks evaluated progressively downwards from the surface block (e.g. 3 blocks of Dirt).")]
        public StandardTerrainLayer[] terrainLayers;

        [ConstantsSelection(typeof(BlockIDs))]
        [Tooltip("Block ID to swap the Surface Block with if generating under the Sea Level (e.g. Sand instead of Grass).")]
        public ushort underwaterSurfaceBlockID = 9; // Sand

        [Header("Major Flora")]
        [Tooltip("If true, flora like trees or cacti will be generated in this biome.")]
        public bool enableMajorFlora = true;

        [Tooltip("2D noise defining coherent regions (groves/forests) where flora can generate. " +
                 "Only positions where this noise exceeds MajorFloraZoneThreshold are eligible for placement.")]
        public FastNoiseConfig majorFloraZoneNoiseConfig;

        [Tooltip("Percentage of the biome covered by flora zones. " +
                 "Larger = larger/more frequent zones, lower = smaller/rarer zones. 1.0 = entire biome is a zone.")]
        [Range(0f, 1f)]
        public float majorFloraZoneCoverage = 0.4f;

        [Header("Flora Height Constraints")]
        public int majorFloraPlacementMinHeight = 0;

        public int majorFloraPlacementMaxHeight = 256;

        [Tooltip("Minimum baseline trunk segments or column blocks to generate per tree/cactus.")]
        public int majorFloraMinPhysicalHeight = 5;

        [Tooltip("Maximum baseline trunk segments or column blocks to generate per tree/cactus.")]
        public int majorFloraMaxPhysicalHeight = 12;

        [Header("Flora Placement (Spacing)")]
        [Tooltip("The minimum grid size for flora. Smaller = denser forest, Larger = sparser forest. Evaluated deterministically.")]
        [Range(1, 64)]
        public int majorFloraPlacementSpacing = 5;

        [Tooltip("Minimum empty blocks to maintain between the tree and the grid cell edges. " +
                 "-1 = Automatic (Allows touching in dense small grids, prevents touching in larger grids >= 5). " +
                 "0 = Full random placement allowing trees to naturally clump and touch. " +
                 "Higher values force trees closer to the exact center of the grid cell.")]
        public int majorFloraPlacementPadding = -1;

        [Tooltip("Probability that a valid spacing slot will actually spawn a tree. 1.0 = every slot is filled, lower = sparser.")]
        [Range(0f, 1f)]
        public float majorFloraPlacementChance = 0.5f;

        [Tooltip("Flora type index dispatched to ExpandFlora (0 = tree, 1 = cactus, etc.).")]
        public byte majorFloraIndex;

        [Header("Lodes (Ore Veins)")]
        [Tooltip("Ore vein configurations for this biome.")]
        public StandardLode[] lodes;

        [Header("Cave Generation")]
        [Tooltip("Layered noise configurations for generating 3D caves (e.g., cheese and spaghetti networks).")]
        public StandardCaveLayer[] caveLayers;
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

        [Tooltip("Editor Preview Color for Composite visualizer tool.")]
        public Color previewColor = Color.yellow;

        [ConstantsSelection(typeof(BlockIDs))]
        [Tooltip("ID of the block that will be generated.")]
        public ushort blockID;

        [Range(0f, 1f)]
        [Tooltip("The noise value must exceed this threshold to spawn the block. Larger numbers = rarer/smaller veins. Smaller numbers = massive veins.")]
        public float threshold = 0.5f;

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
    /// Authoring class for a terrain layer block substitution (e.g. 4 blocks of Dirt below Grass).
    /// </summary>
    [Serializable]
    public class StandardTerrainLayer
    {
        [ConstantsSelection(typeof(BlockIDs))]
        [Tooltip("ID of the block that will be generated for this subsurface strata.")]
        public ushort blockID;

        [Tooltip("How many blocks deep this strata extends before checking the next one.")]
        [Range(1, 20)]
        public int depth = 3;
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
        public string layerName = "New Cave Layer";

        [Tooltip("Editor Preview Color for Composite visualizer tool.")]
        public Color previewColor = Color.red;

        [Tooltip("Blob (Single Noise) produces chambers. Spaghetti (Axis-Pair Average) produces interconnected tunnel networks.")]
        public CaveMode mode = CaveMode.Blob;

        [Tooltip("FastNoiseLite noise configuration for defining the cave shapes.")]
        public FastNoiseConfig noiseConfig;

        [Tooltip("If the evaluated noise exceeds this threshold, the block is carved into air.")]
        public float threshold = 0.5f;

        [Header("Depth Bounds")]
        [Tooltip("Caves will not generate below this Y level.")]
        public int minHeight = 5;

        [Tooltip("Caves will not generate above this Y level.")]
        public int maxHeight = 60;

        [Tooltip("Number of blocks over which the carving fades in/out near MinHeight and MaxHeight bounds. 0 = hard cutoff.")]
        [Range(0, 32)]
        public int depthFadeMargin = 8;
    }
}
