# Known Library / Internal API Bugs and Improvements

This document outlines **open** bugs and architectural improvements related to the project's internal libraries and shared utilities. Resolved items are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** May 2026

---

## TODO: FastNoiseLite Simplified API — Lightweight Config and Factory Methods

**Severity:** Architecture Improvement  
**Files:** `Assets/Scripts/Jobs/Data/FastNoiseConfig.cs`, `Assets/Scripts/Jobs/Generators/FastNoiseFactory.cs`, `Assets/Scripts/Libraries/FastNoiseLite.cs`

### Problem

`FastNoiseConfig` is a comprehensive struct with 16+ fields covering every FastNoiseLite feature (noise type, fractal type, octaves, lacunarity, gain, cellular settings, domain warp, normalization). This is appropriate for complex noise layers like terrain continentalness or cave shapes, but it creates unnecessary friction for simple use cases:

1. **Tweaking fatigue:** A designer adding a simple 3D noise for radius modulation must configure (or consciously skip) 16 fields in the Inspector. Most fields are irrelevant — they only need noise type + frequency, yet the Inspector shows fractal, cellular, and domain warp sections with default-but-visible values.

2. **Initialization overhead:** Each `FastNoiseConfig` requires a `FastNoiseLite` instance created via `FastNoiseFactory.CreateNoiseFromConfig()`, which must be called on the main thread during `StandardChunkGenerator.Initialize()`. The instance must then be stored in a `NativeArray<FastNoiseLite>` and passed to the job. For a feature that needs "just sample some noise at this position", this is a multi-file pipeline change spanning authoring → factory → job data → job.

3. **Inconsistency pressure:** When the full pipeline is too heavy, developers reach for alternatives like `Unity.Mathematics.noise.snoise()` — a single function call with no setup. This works but introduces a second noise system with different characteristics (no seed control, no fractal, different gradient distribution). The worm radius noise feature (`StandardWormCarverJob`, line ~317) currently uses `noise.snoise` for this reason, with a TODO noting the inconsistency.

### Proposed Solution: Lightweight Factory Methods + Optional Lite Config

#### Option A: Static Factory Methods on FastNoiseLite (Preferred)

Add Burst-compatible static methods that create pre-configured `FastNoiseLite` instances in a single call:

```csharp
// In FastNoiseLite.cs or a new FastNoiseLitePresets.cs

/// <summary>Creates a simple single-octave 3D simplex noise.</summary>
public static FastNoiseLite CreateSimple(int seed, float frequency)
{
    FastNoiseLite n = Create(seed);
    n.SetNoiseType(NoiseType.OpenSimplex2);
    n.SetFrequency(frequency);
    return n;
}

/// <summary>Creates a standard FBm noise with sensible defaults (3 octaves, gain 0.5, lacunarity 2.0).</summary>
public static FastNoiseLite CreateFBm(int seed, float frequency, int octaves = 3)
{
    FastNoiseLite n = Create(seed);
    n.SetNoiseType(NoiseType.OpenSimplex2);
    n.SetFrequency(frequency);
    n.SetFractalType(FractalType.FBm);
    n.SetFractalOctaves(octaves);
    n.SetFractalGain(0.5f);
    n.SetFractalLacunarity(2.0f);
    return n;
}
```

**Pros:**

- Zero new types — uses the existing `FastNoiseLite` struct directly
- Burst-compatible (struct construction, no managed types)
- Can be called inline in job `Execute()` or precomputed in `Initialize()`
- No Inspector overhead — these are code-only noise sources
- Seed-aware — unlike `noise.snoise`, these participate in the world seed system

**Cons:**

- No Inspector exposure — designers can't tweak the noise without code changes
- Parameters are hardcoded at call site (but that's the point for simple use cases)

#### Option B: FastNoiseConfigLite Struct

A minimal config struct for simple noise use cases:

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

With a corresponding factory method:

```csharp
public static FastNoiseLite CreateNoiseFromConfig(FastNoiseConfigLite config, int baseSeed)
{
    FastNoiseLite n = FastNoiseLite.Create(baseSeed + config.seedOffset);
    n.SetNoiseType(config.noiseType);
    n.SetFrequency(config.frequency);
    return n;
}
```

**Pros:**

- Inspector-friendly — designers see 3 fields instead of 16
- Serializable — can live on ScriptableObjects alongside existing `FastNoiseConfig` fields
- Clear intent — seeing `FastNoiseConfigLite` signals "this is simple noise, don't over-engineer it"

**Cons:**

- New type to maintain — must stay compatible with `FastNoiseLite` as the library evolves
- Risk of scope creep — fields get added "just this once" until it becomes a second `FastNoiseConfig`

#### Option C: Hybrid (Factory Methods + Lite Config)

Use factory methods (Option A) for code-only noise (job-internal use like radius modulation), and `FastNoiseConfigLite` (Option B) for designer-facing noise that needs Inspector exposure but not full config complexity.

### Migration Path

1. **Phase 1:** Add the static factory methods to `FastNoiseLite` (or a `FastNoiseLitePresets` utility class). Migrate the `noise.snoise` call in `StandardWormCarverJob` to use `FastNoiseLite.CreateSimple()` — created once per worm group in `SimulateWormStack`, not per step.
2. **Phase 2:** Evaluate whether `FastNoiseConfigLite` adds value for any existing or upcoming designer-facing noise fields. If yes, introduce it; if all simple cases are code-internal, factory methods alone suffice.
3. **Phase 3:** Audit all `noise.snoise` / `noise.cnoise` usage in the codebase (if any beyond the radius noise) and migrate to the unified `FastNoiseLite` API.

### Impact on Existing Code

- The full `FastNoiseConfig` remains unchanged — complex noise layers (terrain, caves, biome selection) continue to use it.
- `FastNoiseFactory.CreateNoiseFromConfig(FastNoiseConfig, int)` remains the standard path for full configs.
- The new API is additive — no breaking changes to existing code.

---
