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

        [Range(0, 64)]
        [Tooltip("Minimum connected air volume (in blocks) for a cave pocket to survive the post-carve filter. " +
                 "Connected regions smaller than this are filled back with their original terrain blocks. " +
                 "0 = disabled. 4 = removes pockets of 1-3 blocks. Higher values filter larger isolated pockets.")]
        public int minCavePocketSize;

        [Header("Trunk Worm Modifiers")]
        [Tooltip("Biome-local overrides for world-level trunk worms. Controls spawn suppression, " +
                 "per-step parameter overrides, and traversal blocking.")]
        public TrunkWormModifiers trunkWormModifiers = TrunkWormModifiers.Default;

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
    /// Selects which axis is compressed by the worm squash factor.
    /// </summary>
    public enum WormSquashAxis : byte
    {
        /// <summary>Squash the vertical (Y) axis — caves are wider than tall (natural hallway profile).</summary>
        Vertical,

        /// <summary>Squash the horizontal (XZ) axes — caves are taller than wide (vertical fissure profile).</summary>
        Horizontal,
    }

    /// <summary>
    /// Converts a raw squash value + axis selection into the effective vertical squash factor
    /// used by the worm carver job. Centralizes the axis-dependent inversion logic.
    /// </summary>
    public static class WormSquashAxisHelper
    {
        /// <summary>
        /// Returns the effective vertical squash factor for the carving ellipsoid.
        /// <see cref="WormSquashAxis.Vertical"/> passes the raw value through (values &lt; 1 compress Y).
        /// <see cref="WormSquashAxis.Horizontal"/> inverts the value (1/raw), producing values &gt; 1 that stretch Y.
        /// </summary>
        public static float ToEffectiveSquash(WormSquashAxis axis, float rawSquash)
        {
            return axis == WormSquashAxis.Horizontal ? 1f / rawSquash : rawSquash;
        }
    }

    /// <summary>
    /// Determines the noise evaluation strategy for a cave layer.
    /// </summary>
    public enum CaveMode : byte
    {
        /// <summary>Cheese (Single Noise) — Large open caverns via single 3D noise threshold. Renamed from Blob.</summary>
        Cheese,

        /// <summary>Spaghetti 2D (Axis-Pair Average) — 6-way 2D noise averaging. Produces highly interconnected tunnel networks. Limitation: 2D source creates visible grid-like repetition at large scales.</summary>
        Spaghetti2D,

        /// <summary>Worm Carver (Random Walk) — Recursive turtle generator for highly organic cave networks with branching and noise seeking.</summary>
        WormCarver,

        /// <summary>Noodle (Isoband) — Winding tubular corridors where |noise3D| is close to zero.</summary>
        Noodle,

        /// <summary>Spaghetti 3D (Dual Zero-Crossing) — Interconnected tunnel networks formed at the intersection of two independent 3D noise field zero-crossings. No axis-alignment artifacts.</summary>
        Spaghetti3D,
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

        [Tooltip("Cheese (Single Noise) produces chambers. Spaghetti 2D (Axis-Pair Average) produces interconnected tunnel networks (note: has grid repetition at large scales). " +
                 "Spaghetti 3D (Dual Zero-Crossing) produces interconnected tunnels via two 3D noise fields without axis-alignment artifacts. " +
                 "Noodle (Isoband) produces thin winding corridors.")]
        public CaveMode mode = CaveMode.Cheese;

        [Tooltip("Primary FastNoiseLite noise configuration for defining the cave shapes.")]
        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        public FastNoiseConfig noiseConfig;

        [Tooltip("Secondary 3D noise for Spaghetti3D mode. " +
                 "Tunnels form where both primary and secondary noise fields cross zero simultaneously. " +
                 "Use a different seed offset from the primary noise to ensure independent fields.")]
        [ConditionalField(nameof(mode), false, CaveMode.Spaghetti3D)]
        public FastNoiseConfig secondaryNoiseConfig;

        [Tooltip("Cheese/Spaghetti2D: carves when noise > threshold (higher = rarer caves). " +
                 "Noodle/Spaghetti3D: carves when tube value > threshold, so higher = narrower tubes (e.g. 0.93 = tight corridors, 0.85 = wide tunnels).")]
        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        public float threshold = 0.5f;

        [Header("Cave Domain Warping")]
        [Tooltip("Apply domain warping to this cave layer's noise coordinates. Affects Cheese, Noodle, and Spaghetti3D modes (3D evaluation). Not applicable to Spaghetti2D (uses 2D noise pairs).")]
        public bool enableWarp;

        [ConditionalField(nameof(enableWarp))]
        [Tooltip("Noise configuration for the cave domain warp. Requires its own frequency and amplitude settings.")]
        public FastNoiseConfig warpConfig;

        [Header("Zone Attenuation")]
        [Range(0f, 1f)]
        [Tooltip("Per-layer cave zone attenuation strength. " +
                 "How much the biome's cave zone noise suppresses this layer in low-noise regions. " +
                 "0 = no zone effect (uniform density). " +
                 "Higher values create more spatial variation — clusters caves into denser/sparser regions. " +
                 "Noodle/Spaghetti: boost threshold toward 1.0 to suppress carving (need attn > 1-threshold for full suppression). " +
                 "WormCarver: multiplies spawn probability (formula: spawnChance * (1 - (1-zoneNoise) * 0.5 * attn)). " +
                 "Cheese: same threshold boost as Noodle — keep attn <= 0.26 to prevent seed-specific vanishing. " +
                 "Recommended ranges: Noodle 0.4-0.6, WormCarver 0.25-0.5, Cheese 0.20-0.26.")]
        public float zoneAttenuation;

        [Header("Noise Seekability")]
        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        [Tooltip("When enabled, world-level trunk worms will steer toward this layer's " +
                 "cave features during noise seeking. Typically enabled for Cheese layers " +
                 "(to connect trunks to chambers) and disabled for Noodle layers.")]
        public bool isSeekableByTrunkWorms;

        [ConditionalField(nameof(mode), true, CaveMode.WormCarver)]
        [Tooltip("When enabled, per-biome local worms will steer toward this layer's " +
                 "cave features during noise seeking. Typically enabled for Cheese layers " +
                 "(worms connect to chambers) and disabled for Noodle and other Worm layers.")]
        public bool isSeekableByLocalWorms;

        [Header("Depth Bounds")]
        [Tooltip("Caves will not generate below this Y level.")]
        public int minHeight = 5;

        [Tooltip("Caves will not generate above this Y level.")]
        public int maxHeight = 60;

        [Tooltip("Number of blocks over which carving fades in near the MinHeight (bottom) bound. 0 = hard cutoff.")]
        [Range(0, 32)]
        public int depthFadeMarginBottom = 8;

        [Tooltip("Number of blocks over which carving fades out near the MaxHeight (top) bound. 0 = hard cutoff.")]
        [Range(0, 32)]
        public int depthFadeMarginTop = 8;

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
        [Tooltip("The base radius of the worm cave in blocks. Superseded by wormShape.radiusMin/radiusMax when radius variation is enabled.")]
        [Range(1f, 10f)]
        public float wormBaseRadius = 3f;

        [Header("Worm Carver Shape")]
        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Cross-section shape configuration controlling radius variation, squash profile, and noise modulation.")]
        public WormShape wormShape = WormShape.Default;

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

        [Header("Worm Carver Y-Level Attraction")]
        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Y-level attraction configuration controlling how worms are pulled toward a target depth band.")]
        public WormYAttraction wormYAttraction = WormYAttraction.Default;

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
        [Tooltip("Branching configuration controlling how worms split into child tunnels.")]
        public WormBranching wormBranching = WormBranching.Default;

        [Header("Worm Carver Noise Seeking")]
        [ConditionalField(nameof(mode), false, CaveMode.WormCarver)]
        [Tooltip("Noise seeking configuration controlling how worms detect and steer toward nearby cave features.")]
        public WormNoiseSeeking wormNoiseSeeking = WormNoiseSeeking.Default;
    }

    /// <summary>
    /// Groups the per-biome trunk worm modifier fields.
    /// These override or gate world-level trunk worm behavior within a specific biome.
    /// </summary>
    [Serializable]
    public struct TrunkWormModifiers
    {
        [Range(0f, 1f)]
        [Tooltip("Reduces the chance of a trunk worm originating in this biome. " +
                 "0 = trunk spawns allowed normally. 1 = no trunk spawns originate here. " +
                 "Trunks from neighboring biomes can still pass through regardless of this value.")]
        public float spawnSuppression;

        [Tooltip("Per-step override of the trunk worm's horizontal bias while it passes through this biome. " +
                 "-1 = disabled (use the world-level trunk config value). " +
                 "0-1 = override value (e.g., Mountain 0.3 makes trunks dip vertically through mountain rock).")]
        [Range(-1f, 1f)]
        public float verticalBiasOverride;

        [Tooltip("Per-biome override of the trunk worm's Y attraction band center. " +
                 "-1 = disabled (use the world-level trunk config band). " +
                 "Shifts the band center while preserving the global band width. " +
                 "E.g., Mountain sets 20 to attract trunks deeper; Desert sets 40 for shallower highways.")]
        public float yAttractionCenterOverride;

        [Tooltip("When disabled, trunk worms that enter this biome are terminated. " +
                 "Trunks from neighboring biomes will not carve through. " +
                 "Does not affect local (per-biome) worms. " +
                 "Use Traversal Fade Steps to control how gradually the tunnel narrows before termination.")]
        public bool traversalAllowed;

        [Range(0, 30)]
        [Tooltip("Steps over which a blocked trunk worm tapers its radius to zero before terminating. " +
                 "0 = hard cut (immediate termination). 8-12 = natural-looking tunnel narrowing. " +
                 "Only used when Traversal Allowed is false.")]
        public int traversalFadeSteps;

        /// <summary>Default values: no suppression, no overrides, traversal allowed.</summary>
        public static TrunkWormModifiers Default => new TrunkWormModifiers
        {
            spawnSuppression = 0f,
            verticalBiasOverride = -1f,
            yAttractionCenterOverride = -1f,
            traversalAllowed = true,
            traversalFadeSteps = 0,
        };
    }

    /// <summary>
    /// Groups the noise-seeking parameters for worm carvers.
    /// These three fields always belong together and are meaningless individually.
    /// </summary>
    [Serializable]
    public struct WormNoiseSeeking
    {
        [Range(0, 30)]
        [Tooltip("Steps between noise-seeking checks. 0 = seeking disabled.")]
        public int checkInterval;

        [Range(1f, 30f)]
        [Tooltip("How far ahead the worm looks when seeking (in blocks).")]
        public float seekDistance;

        [Range(0f, 1f)]
        [Tooltip("Probability of performing a seek check when the interval fires.")]
        public float seekChance;

        [Header("Worm Mask Seeking")]
        [Range(0f, 1f)]
        [Tooltip("Probability of steering toward already-carved worm tunnels " +
                 "when a mask-seek check fires. 0 = disabled. " +
                 "Only effective within the current chunk's worm mask.")]
        public float maskSeekChance;

        [Range(0, 100)]
        [Tooltip("Minimum steps before mask seeking activates. Prevents worms " +
                 "from immediately latching onto nearby tunnels at spawn.")]
        public int maskSeekMinSteps;

        /// <summary>Default values matching the original separate field defaults.</summary>
        public static WormNoiseSeeking Default => new WormNoiseSeeking
        {
            checkInterval = 10,
            seekDistance = 10f,
            seekChance = 0.5f,
            maskSeekChance = 0f,
            maskSeekMinSteps = 30,
        };
    }

    /// <summary>
    /// Groups the cross-section shape parameters for worm carvers.
    /// Controls radius variation, elliptical squash, and noise-modulated width along the tunnel.
    /// </summary>
    [Serializable]
    public struct WormShape
    {
        [Range(1f, 8f)]
        [Tooltip("Minimum carving radius. Narrow squeezes along the tunnel.")]
        public float radiusMin;

        [Range(2f, 12f)]
        [Tooltip("Maximum carving radius. Wide chambers along the tunnel.")]
        public float radiusMax;

        [Tooltip("Which axis the squash factor compresses. " +
                 "Vertical = wider-than-tall hallways. Horizontal = taller-than-wide fissures.")]
        public WormSquashAxis squashAxis;

        [Range(0.3f, 1f)]
        [Tooltip("How much to compress the selected axis. " +
                 "1.0 = spherical (circular cross-section, no squash). " +
                 "0.6 = 40% compression along the selected axis. " +
                 "Only affects the carved shape, not the worm's navigation path.")]
        public float squashFactor;

        [Range(1, 8)]
        [Tooltip("How many wide/narrow cycles occur along the worm's length. " +
                 "1 = one pinch point. 4 = alternating every ~50 steps.")]
        public int radiusWaveCount;

        [Range(0f, 1f)]
        [Tooltip("How much Perlin noise replaces the deterministic sine wave for radius variation. " +
                 "0 = pure sine wave. 0.5 = structured rhythm + organic variation. " +
                 "1.0 = fully noise-driven (unpredictable width changes).")]
        public float radiusNoiseStrength;

        [Range(0.01f, 0.5f)]
        [Tooltip("Spatial frequency of the radius noise. Lower values produce long, gradual width changes. " +
                 "Higher values produce more frequent, localized pinches and bulges. " +
                 "Only used when radiusNoiseStrength > 0.")]
        public float radiusNoiseFrequency;

        /// <summary>Default values for local (per-biome) worms: radius [2, 4], no squash, no noise.</summary>
        public static WormShape Default => new WormShape
        {
            radiusMin = 2f,
            radiusMax = 4f,
            squashAxis = WormSquashAxis.Vertical,
            squashFactor = 1f,
            radiusWaveCount = 3,
            radiusNoiseStrength = 0f,
            radiusNoiseFrequency = 0.1f,
        };

        /// <summary>Default values for trunk worms: radius [3, 5], no squash, no noise.</summary>
        public static WormShape TrunkDefault => new WormShape
        {
            radiusMin = 3f,
            radiusMax = 5f,
            squashAxis = WormSquashAxis.Vertical,
            squashFactor = 1f,
            radiusWaveCount = 3,
            radiusNoiseStrength = 0f,
            radiusNoiseFrequency = 0.1f,
        };
    }

    /// <summary>
    /// Groups the branching parameters for worm carvers.
    /// Controls how worms split into child tunnels during their random walk.
    /// </summary>
    [Serializable]
    public struct WormBranching
    {
        [Range(0f, 0.2f)]
        [Tooltip("Probability [0, 1] per step that a worm will split and spawn a child worm.")]
        public float branchChance;

        [Range(0, 5)]
        [Tooltip("How many generations of children are allowed (e.g., 0 = single worm, 1 = children allowed, 2 = grandchildren allowed).")]
        public int maxBranchDepth;

        /// <summary>Default values for local (per-biome) worms: 5% chance, depth 2.</summary>
        public static WormBranching Default => new WormBranching
        {
            branchChance = 0.05f,
            maxBranchDepth = 2,
        };

        /// <summary>Default values for trunk worms: 3% chance, depth 1.</summary>
        public static WormBranching TrunkDefault => new WormBranching
        {
            branchChance = 0.03f,
            maxBranchDepth = 1,
        };
    }

    /// <summary>
    /// Groups the Y-level attraction parameters for worm carvers.
    /// These three fields always belong together: strength gates the feature, min/max define the target band.
    /// </summary>
    [Serializable]
    public struct WormYAttraction
    {
        [Range(0f, 1f)]
        [Tooltip("How strongly the worm is pulled toward the Y attraction band. " +
                 "0 = disabled (default, no vertical preference). " +
                 "0.3 = gentle drift toward band. " +
                 "0.7 = strong channeling into band.")]
        public float strength;

        [Tooltip("Lower bound of the target Y band. No vertical force when inside [min, max]. " +
                 "Set equal to max for single-level attraction.")]
        public float minY;

        [Tooltip("Upper bound of the target Y band. No vertical force when inside [min, max]. " +
                 "Set equal to min for single-level attraction.")]
        public float maxY;

        /// <summary>Default values: disabled (strength 0), band [20, 40].</summary>
        public static WormYAttraction Default => new WormYAttraction
        {
            strength = 0f,
            minY = 20f,
            maxY = 40f,
        };

        /// <summary>Default values for trunk worms: disabled (strength 0), band [15, 35].</summary>
        public static WormYAttraction TrunkDefault => new WormYAttraction
        {
            strength = 0f,
            minY = 15f,
            maxY = 35f,
        };
    }
}
