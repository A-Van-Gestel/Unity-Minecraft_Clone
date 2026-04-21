using System;
using Attributes;
using Jobs.Data;
using Libraries;
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

        [Header("Biome Blending")]
        [Range(0.01f, 1.0f)]
        [Tooltip("Controls the width of the transition zone at Voronoi boundaries. " +
                 "Larger values produce wider, more gradual transitions. Smaller values produce tighter, more abrupt transitions.")]
        public float blendRadius = 0.2f;

        [Range(0.01f, 1.0f)]
        [Tooltip("Controls how strongly this biome's terrain height bleeds into neighboring biomes during blending. " +
                 "1.0 = full influence (default). Lower values suppress this biome's height contribution at boundaries, " +
                 "keeping neighboring terrain flatter. Useful for high-amplitude biomes like Mountains.")]
        public float blendWeight = 1.0f;

        [Tooltip("Controls the interpolation curve shape at Voronoi boundaries. " +
                 "Linear = most gradual (good for Mountains). SmoothStep = standard S-curve (default). " +
                 "SmootherStep = sharper S-curve with flatter plateaus.")]
        public BlendCurve blendCurve = BlendCurve.SmoothStep;

        [Range(0f, 1.0f)]
        [Tooltip("The cellular boundary distance threshold below which surface blocks are blended (dithered). 0 = hard cutoff, larger = wider transition.")]
        public float surfaceBlockDitheringWidth = 0.10f;

        [Header("Terrain Shape")]
        [Tooltip("Base terrain height in blocks. Noise output is added to this value.")]
        public float baseTerrainHeight = 42f;

        [Tooltip("Vertical multiplier for terrain noise (e.g., 20 means hills reach BaseTerrainHeight ± 20). " +
                 "FastNoiseLite returns normalized -1.0 to 1.0; this gives it physical scale.")]
        public float terrainAmplitude = 20f;

        [Header("Surface Blocks")]
        [BlockID]
        [Tooltip("Block ID for the surface layer (e.g., Grass).")]
        public ushort surfaceBlockID;

        [BlockID]
        [Tooltip("Block ID for the sub-surface layers (e.g., Dirt).")]
        [Obsolete("Replaced by terrainLayers")]
        [HideInInspector]
        public ushort subSurfaceBlockID;

        [Header("Terrain Layers")]
        [Tooltip("The blocks evaluated progressively downwards from the surface block (e.g. 3 blocks of Dirt).")]
        public StandardTerrainLayer[] terrainLayers;

        [Tooltip("Noise configuration for strata depth jitter. Evaluated locally to organically vary the thickness of the subsurface terrain layers.")]
        public FastNoiseConfig strataDepthNoiseConfig = new FastNoiseConfig { noiseType = FastNoiseLite.NoiseType.OpenSimplex2, frequency = 0.05f };

        [BlockID]
        [Tooltip("Block ID to swap the Surface Block with if generating under the Sea Level (e.g. Sand instead of Grass).")]
        public ushort underwaterSurfaceBlockID = 9; // Sand

        [Header("Flora Zone")]
        [Tooltip("2D noise defining coherent regions (groves/forests) where flora can generate. " +
                 "Only pool entries with 'Use Flora Zone' enabled are affected by this noise.")]
        public FastNoiseConfig floraZoneNoiseConfig;

        [Tooltip("Percentage of the biome covered by flora zones. " +
                 "Larger = larger/more frequent zones, lower = smaller/rarer zones. 1.0 = entire biome is a zone.")]
        [Range(0f, 1f)]
        public float floraZoneCoverage = 0.4f;

        [Header("Structure Pools")]
        [Tooltip("Major structures (trees, boulders, etc.) with independent placement grids.")]
        public StructurePoolEntry[] majorFloraPool;

        [Tooltip("Minor structures (grass, flowers, etc.) with independent placement grids.")]
        public StructurePoolEntry[] minorFloraPool;

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

        [BlockID]
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
    /// Controls the interpolation curve shape used when blending this biome's weight at Voronoi boundaries.
    /// </summary>
    public enum BlendCurve : byte
    {
        /// <summary>Uniform blend rate across the entire transition zone. Most gradual. Suited for high-amplitude biomes like Mountains.</summary>
        Linear,

        /// <summary>Hermite S-curve (3t² − 2t³). Concentrates transition at the boundary midpoint. Default for most biomes.</summary>
        SmoothStep,

        /// <summary>Quintic S-curve (6t⁵ − 15t⁴ + 10t³). Sharper than SmoothStep with flatter plateaus at extremes.</summary>
        SmootherStep,
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

        /// <summary>Worm Carver (Random Walk) — Legacy-style recursive turtle generator for highly organic cave networks.</summary>
        WormCarver,
    }

    /// <summary>
    /// Authoring class for a terrain layer block substitution (e.g. 4 blocks of Dirt below Grass).
    /// </summary>
    [Serializable]
    public class StandardTerrainLayer
    {
        [BlockID]
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
        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        public FastNoiseConfig noiseConfig;

        [Tooltip("If the evaluated noise exceeds this threshold, the block is carved into air.")]
        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        public float threshold = 0.5f;

        [Header("Depth Bounds")]
        [Tooltip("Caves will not generate below this Y level.")]
        public int minHeight = 5;

        [Tooltip("Caves will not generate above this Y level.")]
        public int maxHeight = 60;

        [Tooltip("Number of blocks over which the carving fades in/out near MinHeight and MaxHeight bounds. 0 = hard cutoff.")]
        [Range(0, 32)]
        public int depthFadeMargin = 8;

        [Header("Worm Carver Settings")]
        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Probability [0, 1] that this chunk will spawn a worm system.")]
        [Range(0f, 1f)]
        public float wormSpawnChance = 1.0f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Maximum number of worms that can spawn in a single chunk if it succeeds the spawn chance.")]
        [Range(1, 10)]
        public int maxWormsPerChunk = 3;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("The base radius of the worm cave in blocks.")]
        [Range(1f, 10f)]
        public float wormBaseRadius = 3f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("How strongly the worm perturbs its pitch/yaw angles per step.")]
        [Range(0.1f, 1f)]
        public float wormWaviness = 0.5f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Minimum number of steps the worm will march.")]
        [Range(10, 200)]
        public int wormMinLength = 50;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Maximum number of steps the worm will march.")]
        [Range(50, 500)]
        public int wormMaxLength = 200;

        [Header("Worm Carver Branching")]
        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Probability [0, 1] per step that a worm will split and spawn a child worm.")]
        [Range(0f, 0.2f)]
        public float wormBranchChance = 0.05f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("How many generations of children are allowed (e.g., 0 = single worm, 1 = children allowed, 2 = grandchildren allowed).")]
        [Range(0, 5)]
        public int maxBranchDepth = 2;

        [Header("Worm Carver Noise Seeking")]
        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("How many steps the worm takes between seeking checks. 0 = disabled.")]
        [Range(0, 50)]
        public int wormSeekInterval = 10;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("How far ahead the worm looks for Blob/Spaghetti caves.")]
        [Range(1f, 30f)]
        public float wormSeekDistance = 10f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Probability [0, 1] that the worm actually steers towards a detected cave.")]
        [Range(0f, 1f)]
        public float wormSeekChance = 0.5f;
    }
}
