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

        [Tooltip("Color used in editor preview tools and the in-game terrain debug overlay to identify this biome.")]
        public Color debugPreviewColor = Color.green;

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
        [Tooltip("Base terrain height in blocks. Multi-Noise offsets are added to this value.")]
        public float baseTerrainHeight = 42f;

        [Header("Terrain Shape (Multi-Noise)")]
        [Tooltip("Noise controlling macro landmass scale (Oceans vs Continents).")]
        public FastNoiseConfig continentalnessNoiseConfig;

        [Tooltip("Curve mapping Continentalness [-1, 1] to base height offset.")]
        public AnimationCurve continentalnessCurve;

        [Tooltip("Noise controlling weathering.")]
        public FastNoiseConfig erosionNoiseConfig;

        [Tooltip("Curve mapping Erosion [-1, 1] to height multiplier.")]
        public AnimationCurve erosionCurve;

        [Tooltip("Noise controlling localized hills and valleys.")]
        public FastNoiseConfig peaksAndValleysNoiseConfig;

        [Tooltip("Curve mapping P&V [-1, 1] to local amplitude.")]
        public AnimationCurve peaksAndValleysCurve;

        [Header("3D Density (Overhangs & Arches)")]
        [Tooltip("Enable volumetric 3D density evaluation for terrain overhangs and arches.")]
        public bool enable3DDensity;

        [Tooltip("Noise configuration for the 3D density field.")]
        public FastNoiseConfig densityNoiseConfig;

        [Tooltip("Max height variation of 3D noise. Dynamically defines the Density Band.")]
        public float densityAmplitude = 15f;

        [Header("Domain Warping (Organic Distortion)")]
        [Tooltip("Apply domain warping to the 3D density noise coordinates for organic terrain shapes.")]
        public bool enableDensityWarp;

        [Tooltip("Noise configuration for the density domain warp. Requires its own frequency and amplitude settings.")]
        public FastNoiseConfig densityWarpConfig;

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
        [Tooltip("2D noise field (range [-1, 1]) controlling spatial cave density variation. " +
                 "High noise regions produce full cave networks; low noise regions produce fewer, smaller caves. " +
                 "The gradient is smooth — no hard boundaries between cave-dense and cave-sparse areas.")]
        public FastNoiseConfig caveZoneNoiseConfig;

        [Tooltip("How much the cave zone noise attenuates cave generation in low-noise regions. " +
                 "0 = uniform caves everywhere (zone noise ignored). " +
                 "Higher values create more spatial variation between cave-dense clusters and cave-sparse gaps. " +
                 "Caves are never fully gated — even low-noise areas can generate smaller networks.")]
        [Range(0f, 1f)]
        public float caveZoneAttenuation;

        [Range(0, 64)]
        [Tooltip("Minimum connected air volume (in blocks) for a cave pocket to survive the post-carve filter. " +
                 "Connected regions smaller than this are filled back with their original terrain blocks. " +
                 "0 = disabled. 4 = removes pockets of 1-3 blocks. Higher values filter larger isolated pockets.")]
        public int minCavePocketSize;

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
        /// <summary>Cheese (Single Noise) — Large open caverns via single 3D noise threshold. Renamed from Blob.</summary>
        Cheese,

        /// <summary>Spaghetti (Axis-Pair Average) — Legacy-style 6-way 2D noise averaging. Produces interconnected tunnel networks.</summary>
        Spaghetti,

        /// <summary>Worm Carver (Random Walk) — Legacy-style recursive turtle generator for highly organic cave networks.</summary>
        WormCarver,

        /// <summary>Noodle (Isoband) — Winding tubular corridors where |noise3D| is close to zero.</summary>
        Noodle,
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
        public CaveMode mode = CaveMode.Cheese;

        [Tooltip("FastNoiseLite noise configuration for defining the cave shapes.")]
        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        public FastNoiseConfig noiseConfig;

        [Tooltip("Cheese/Spaghetti: carves when noise > threshold (higher = rarer caves). " +
                 "Noodle: carves when (1 - |noise|) > threshold, so higher = narrower tubes (e.g. 0.93 = tight corridors, 0.85 = wide tunnels).")]
        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        public float threshold = 0.5f;

        [Header("Cave Domain Warping")]
        [Tooltip("Apply domain warping to this cave layer's noise coordinates. Only affects Cheese and Noodle modes (3D evaluation). Ignored for Spaghetti (2D legacy).")]
        public bool enableWarp;

        [ConditionalField(nameof(enableWarp))]
        [Tooltip("Noise configuration for the cave domain warp. Requires its own frequency and amplitude settings.")]
        public FastNoiseConfig warpConfig;

        [Header("Zone Attenuation")]
        [Range(0f, 1f)]
        [Tooltip("Per-layer cave zone attenuation strength. " +
                 "How much the biome's cave zone noise suppresses this layer in low-noise regions. " +
                 "0 = no zone effect (uniform density). " +
                 "Higher values create more spatial variation. " +
                 "Typically used on Noodle layers; Worm and Cheese layers usually leave this at 0.")]
        public float zoneAttenuation;

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
        [Tooltip("The base radius of the worm cave in blocks. Superseded by wormRadiusMin/Max when radius variation is enabled.")]
        [Range(1f, 10f)]
        public float wormBaseRadius = 3f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Range(1f, 8f)]
        [Tooltip("Minimum carving radius. Narrow squeezes along the tunnel.")]
        public float wormRadiusMin = 2f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Range(2f, 12f)]
        [Tooltip("Maximum carving radius. Wide chambers along the tunnel.")]
        public float wormRadiusMax = 4f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Range(1, 8)]
        [Tooltip("How many wide/narrow cycles occur along the worm's length. " +
                 "1 = one pinch point. 4 = alternating every ~50 steps.")]
        public int wormRadiusWaveCount = 3;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("How strongly the worm perturbs its pitch/yaw angles per step.")]
        [Range(0.1f, 1f)]
        public float wormWaviness = 0.5f;

        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Range(0f, 1f)]
        [Tooltip("How strongly worms are pulled toward horizontal. " +
                 "0 = no bias (original behavior). " +
                 "0.5 = gentle leveling. " +
                 "1.0 = strongly horizontal with only brief vertical dips.")]
        public float wormHorizontalBias = 0.5f;

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
