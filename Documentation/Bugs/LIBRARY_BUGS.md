# Known Library / Internal API Bugs and Improvements

This document outlines **open** bugs and architectural improvements related to the project's internal libraries and shared utilities. Resolved items are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** May 2026

---

## NativeCompressions (LZ4) — Version pinned to 0.6.0

**Severity:** Critical version constraint
**Status:** Pinned / monitoring upstream
**Files:** `Assets/packages.config`, `Assets/Scripts/Serialization/CompressionFactory.cs`

**Do NOT upgrade `NativeCompressions.LZ4.*` past 0.6.0.** Version 0.6.1's `LZ4Stream` is
asymmetric: the compressor writes raw block format while the decompressor only parses LZ4 frame
format — and it **hangs forever at 100% CPU** on non-frame input instead of throwing. This
bricked every world saved/migrated under 0.6.1 and caused full-system hangs on load.
Full analysis, standalone repro, and mitigation: `SERIALIZATION_BUGS.md` #03.

`CompressionFactory.ValidateLz4FrameMagic` now fail-fasts on non-frame payloads before they
reach the native decompressor; keep that guard regardless of library version.

### Alternative library (for future research): K4os.Compression.LZ4

Not evaluated in the original LZ4 library research; documented here after the 0.6.1 incident:

- NuGet: `K4os.Compression.LZ4` (block API) + `K4os.Compression.LZ4.Streams` (frame/stream API). MIT.
- The de-facto standard .NET LZ4 port — widely battle-tested (used by major .NET projects).
- Pure managed (with unsafe fast paths): **no per-platform native binaries**, which removes the
  android-arm/arm64/x64/linux/win runtime-package matrix NativeCompressions requires.
- Reads/writes the standard LZ4 frame format → compatible with existing 0.6.0-era saves.
- Trade-off: somewhat lower throughput than a native lz4 binding; chunk loads are not
  decompression-bound, so this is unlikely to matter in practice.

---

## FastNoiseLite Simplified API — Lightweight Config and Factory Methods

**Severity:** Architecture Improvement  
**Status:** Phase 1 Implemented  
**Files:** `Assets/Scripts/Libraries/FastNoiseLite.cs`, `Assets/Scripts/Jobs/Generators/FastNoiseFactory.cs`, `Assets/Scripts/Jobs/Data/FastNoiseConfig.cs`

### Problem

`FastNoiseConfig` is a comprehensive struct with 16+ fields covering every FastNoiseLite feature (noise type, fractal type, octaves, lacunarity, gain, cellular settings, domain warp, normalization). This is appropriate for complex noise layers like terrain continentalness or cave shapes, but it creates unnecessary friction for simple use cases:

1. **Tweaking fatigue:** A designer adding a simple 3D noise for radius modulation must configure (or consciously skip) 16 fields in the Inspector. Most fields are irrelevant — they only need noise type + frequency, yet the Inspector shows fractal, cellular, and domain warp sections with default-but-visible values.

2. **Initialization overhead:** Each `FastNoiseConfig` requires a `FastNoiseLite` instance created via `FastNoiseFactory.CreateNoiseFromConfig()`, which must be called on the main thread during `StandardChunkGenerator.Initialize()`. The instance must then be stored in a `NativeArray<FastNoiseLite>` and passed to the job. For a feature that needs "just sample some noise at this position", this is a multi-file pipeline change spanning authoring → factory → job data → job.

3. **Inconsistency pressure:** When the full pipeline is too heavy, developers reach for `Unity.Mathematics.noise.snoise()` — a single function call with no setup. This works but introduces a second noise system with different characteristics (no seed control, no fractal, different gradient distribution). Several call sites still use `noise.snoise` (biome boundary dithering in `StandardChunkGenerationJob`, biome blending wiggle in `BiomeBlender` and `WorldBlendingPreviewJob`).

### Implemented: Static Factory Methods on FastNoiseLite

Two static factory methods have been added to `FastNoiseLite` for creating pre-configured instances in a single call:

```csharp
// Simple single-octave OpenSimplex2 noise (replaces noise.snoise for seed-aware use cases)
FastNoiseLite radiusNoise = FastNoiseLite.CreateSimple(seed, frequency);

// FBm noise with sensible defaults (3 octaves, gain 0.5, lacunarity 2.0)
FastNoiseLite terrainNoise = FastNoiseLite.CreateFBm(seed, frequency, octaves: 4);
```

**Properties:**

- Zero new types — uses the existing `FastNoiseLite` struct directly
- Burst-compatible (struct construction, no managed types)
- Can be called inline in job `Execute()` or precomputed before the hot loop
- Seed-aware — participates in the world seed system (unlike `noise.snoise`)
- No Inspector overhead — these are code-only noise sources

**First migration:** The worm radius noise in `StandardWormCarverJob.SimulateWormStack` now uses `FastNoiseLite.CreateSimple()` instead of `noise.snoise()`, created once per worm group rather than evaluating a stateless function per step.

### Future Improvement: FastNoiseConfigLite Struct

For designer-facing noise that needs Inspector exposure but not full `FastNoiseConfig` complexity, a minimal config struct could complement the factory methods:

```csharp
[Serializable]
public struct FastNoiseConfigLite
{
    [Tooltip("Added to the world seed to differentiate this noise layer.")]
    public int seedOffset;

    [Tooltip("Base frequency for noise evaluation.")]
    public float frequency;

    [Tooltip("The noise algorithm to use.")]
    public FastNoiseLite.NoiseType noiseType;

    public static FastNoiseConfigLite Default => new FastNoiseConfigLite
    {
        noiseType = FastNoiseLite.NoiseType.OpenSimplex2,
        frequency = 0.1f,
    };
}
```

With a corresponding factory overload on `FastNoiseFactory`:

```csharp
public static FastNoiseLite CreateNoiseFromConfig(FastNoiseConfigLite config, int baseSeed)
{
    FastNoiseLite n = FastNoiseLite.Create(baseSeed + config.seedOffset);
    n.SetNoiseType(config.noiseType);
    n.SetFrequency(config.frequency);
    return n;
}
```

**When to introduce:** When a new feature needs simple noise exposed in the Inspector (e.g. ore distribution frequency, decoration density). If all future simple noise cases remain code-internal, the factory methods alone suffice and this struct is unnecessary.

**Trade-offs:**

- Inspector-friendly — designers see 3 fields instead of 16
- Serializable — can live on ScriptableObjects alongside existing `FastNoiseConfig` fields
- Risk of scope creep — fields get added "just this once" until it becomes a second `FastNoiseConfig`

### Future Improvement: Remaining `noise.snoise` Migration

The following call sites still use `Unity.Mathematics.noise.snoise` instead of `FastNoiseLite`:

| File                                            | Usage                     | Priority                                        |
|-------------------------------------------------|---------------------------|-------------------------------------------------|
| `StandardChunkGenerationJob.cs` (line ~234-235) | Biome boundary dithering  | Low — 2D noise, fixed frequency, no seed needed |
| `BiomeBlender.cs` (line ~69)                    | Biome blend radius wiggle | Low — 2D noise, cosmetic, no seed needed        |
| `WorldBlendingPreviewJob.cs` (line ~191)        | Editor preview wiggle     | Low — editor-only, mirrors `BiomeBlender`       |

These are all 2D `noise.snoise(float2)` calls with hardcoded offsets acting as ad-hoc seeds. Migration would replace them with `FastNoiseLite.CreateSimple()` for seed consistency, but the visual impact is negligible — they produce small cosmetic perturbations. Migrate opportunistically when touching these files, rather than as a dedicated effort.

### Impact on Existing Code

- The full `FastNoiseConfig` remains unchanged — complex noise layers (terrain, caves, biome selection) continue to use it.
- `FastNoiseFactory.CreateNoiseFromConfig(FastNoiseConfig, int)` remains the standard path for full configs.
- The factory methods are additive — no breaking changes to existing code.

---
