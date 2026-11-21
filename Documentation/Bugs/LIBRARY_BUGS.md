# Known Library / Internal API Bugs and Improvements

This document outlines **open** bugs and architectural improvements related to the project's internal libraries and shared utilities. Resolved items are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026 (documentation audit — every code-checkable claim re-verified against current source)

---

## NativeCompressions (LZ4) — Upgraded to 1.3.2 (0.6.0 pin lifted)

**Severity:** Version constraint (resolved)  
**Status:** Pin lifted 2026-11-13 — on 1.3.2, monitoring upstream  
**Files:** `Assets/packages.config`, `Assets/Scripts/Serialization/CompressionFactory.cs`

`NativeCompressions.LZ4.*` was pinned to 0.6.0 because 0.6.1's `LZ4Stream` was asymmetric: the
compressor wrote raw block format, the decompressor only parsed frame format, and it **hung forever
at 100% CPU** on non-frame input. That bricked every world saved under 0.6.1 (full record:
`_FIXED_BUGS.md`, Serialization & Storage #03). 1.x fixes both defects and the project now runs
1.3.2. **Never install anything from the 0.6.1 line.**

Keep `CompressionFactory.ValidateLz4FrameMagic` regardless of version: it rejects non-frame payloads
with a clear `InvalidDataException` before they reach the native decoder.

### Verification of 1.3.2 (2026-11-13)

Standalone, outside Unity: a net8 harness loading the exact `netstandard2.1` DLLs Unity loads
(1.3.2 with Pipelines 10.0.15; 0.6.0 as control), Python `lz4.frame` as the oracle, and every LZ4
record of five worlds written before the upgrade (39,663 payloads).

| Check                    | Result                                                                                                                                                                                      |
|--------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| A — output format        | 1.3.2 output starts with `04 22 4D 18` and decodes exactly with `lz4.frame` (empty, 1 byte, real chunk, 9 MB multi-block; bulk and small writes)                                            |
| B — reads existing saves | 39,663 / 39,663 payloads decode byte-identical to the oracle, with both `CopyTo` and small-`Read` patterns                                                                                  |
| C — rollback direction   | 0.6.0 decodes all 39,663 payloads re-encoded by 1.3.2; checksummed frames written by Python decode on both versions                                                                         |
| D — corrupt input (10 s) | 1.3.2 throws `LZ4Exception` on all 5 cases and disposes cleanly. 0.6.0 **hung** on 3 (valid header + corrupt body, garbage after the magic, raw 0.6.1 block) and returned partial data on 2 |

D shows 0.6.0 still had a hang the magic guard did not cover: a valid frame header over a corrupt
body. `Validate Deserialization Robustness` B10–B12 reproduce it through `ChunkSerializer.Deserialize`
and `LoadChunkAsync` (red by deadline under 0.6.0, green under 1.3.2).

In Unity: Serialization Round-Trip 17/17, Deserialization Robustness 12/12 and Migration Chain 25/25
green; native import settings unchanged (the Android `.so` now reports `Is16KbAligned: true`).
`Es74Retire`, written under 0.6.0, was loaded, edited, saved and reloaded on 1.3.2: the edits came
back, and all 5,354 of its records decode with `lz4.frame`. An IL2CPP Master Windows player
(`-mc-run startup-new`) loaded the native plugin with no errors, and all 729 records it wrote
decode with `lz4.frame`. The Android and Linux runtimes were checked for import settings only.

### Package set

- `Core` and the `android-arm64`, `linux-x64` and `win-x64` runtimes at 1.3.2;
  `System.IO.Pipelines` 10.0.15. `CompressionFactory` only uses the
  `LZ4Stream(stream, CompressionMode, leaveOpen)` constructor, which 1.x keeps (1.0.0 removed the
  multithreading support and 1.3.0 moved `LZ4Encoder` to the `OperationStatus` API; neither is used
  here). The hang was upstream issue `Cysharp/NativeCompressions#67`, closed for v1 on 2026-09-29.
- `System.Diagnostics.DiagnosticSource` and `System.Threading.Channels` were 0.6.0-only
  dependencies and are removed.
- `System.Runtime.CompilerServices.Unsafe` (≥ 6.1.2) is not a package here: NuGetForUnity treats it
  as provided by Unity, as it did for 0.6.0.
- **System.\* stay on the 10.x line** (.NET 10 is LTS). 11.x changes nothing in the `netstandard2.0`
  build Unity loads; revisit after the Unity 7 / CoreCLR Editor upgrade.
- `ProjectSettings/Packages/com.github-glitchenzo.nugetforunity/NativeRuntimeSettings.json` is
  upstream's `sandbox/UnityApp` copy, still required (NuGetForUnity's defaults lack several
  runtimes). NuGetForUnity reads it once per Editor start.

### Upgrading the library again

1. `Assets/Packages/` is gitignored: back it up with its `.meta` files, and back up the save
   folders. That backup is the only rollback.
2. The Editor keeps `lz4.dll` loaded: close it, edit `packages.config`, move the old version folders
   out, and start it again so NuGetForUnity restores the new versions.
3. Compare each native library's import `.meta` with the backup.
4. Redo checks A–D standalone: build `Tools/Lz4Check` against the old and the new DLLs (its `.csproj`
   header has the command), then `python Tools/Python/lz4_upgrade_check.py run` (usage in its
   docstring). The suites alone are not enough: a single-version round trip stays green for a library
   that writes and reads a non-standard format.

### Alternative library (for future research): K4os.Compression.LZ4

Not evaluated in the original LZ4 library research; documented here after the 0.6.1 incident:

- NuGet: `K4os.Compression.LZ4` (block API) + `K4os.Compression.LZ4.Streams` (frame/stream API). MIT.
- The de-facto standard .NET LZ4 port — widely battle-tested (used by major .NET projects).
- Pure managed (with unsafe fast paths): **no per-platform native binaries**, which removes the
  runtime-package matrix NativeCompressions requires — currently `Core` plus `android-arm64`,
  `linux-x64` and `win-x64` in `Assets/packages.config`, one more per platform added.
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
| `StandardChunkGenerationJob.cs` (line ~250-251) | Biome boundary dithering  | Low — 2D noise, fixed frequency, no seed needed |
| `BiomeBlender.cs` (line ~80)                    | Biome blend radius wiggle | Low — 2D noise, cosmetic, no seed needed        |
| `WorldBlendingPreviewJob.cs` (line ~191)        | Editor preview wiggle     | Low — editor-only, mirrors `BiomeBlender`       |

These are all 2D `noise.snoise(float2)` calls with hardcoded offsets acting as ad-hoc seeds. Migration would replace them with `FastNoiseLite.CreateSimple()` for seed consistency, but the visual impact is negligible — they produce small cosmetic perturbations. Migrate opportunistically when touching these files, rather than as a dedicated effort.

> ⚠️ **The dithering row is not purely cosmetic — see [`WORLD_GENERATION_BUGS.md`](./WORLD_GENERATION_BUGS.md) #04.** Unlike the other two, that site feeds `BaseSeed` in as a **float coordinate**, which collapses the dither to a constant for seeds above ~2²⁴. Migrating it is therefore a **seed-breaking** behavior fix rather than an opportunistic tidy-up, and it is gated behind the same world-version moment as `WORLD_GENERATION_BUGS.md` #01. The "no seed needed" rating above applies to `BiomeBlender` / `WorldBlendingPreviewJob` only.

### Impact on Existing Code

- The full `FastNoiseConfig` remains unchanged — complex noise layers (terrain, caves, biome selection) continue to use it.
- `FastNoiseFactory.CreateNoiseFromConfig(FastNoiseConfig, int)` remains the standard path for full configs.
- The factory methods are additive — no breaking changes to existing code.

---

## URP — `Blitter.Initialize` null shader when a player quits in its first frames

**Severity:** Bug (log noise at exit) — **open, cause not investigated**  
**Status:** Observed 2026-11-21 in a Windows IL2CPP Master build (`Windows - Production`) during the agent harness
`AH-2` failure-path test. No visible effect: it is logged after the run's outcome line, while the process exits.  
**Files:** none identified — Unity URP (`UniversalRenderPipeline` constructor → `Blitter.Initialize`); the early
quit goes through `Assets/Scripts/Launch/LaunchSession.cs` (`EndRun` → `Application.Quit`).

**Symptom.** A player that quits during its first frames logs, after `[Launch] Run finished …`:

```
Cannot create required material because shader is null   (×3)
Cannot initialize a LocalKeyword with a null Shader.
NullReferenceException: Object reference not set to an instance of an object.
  at UnityEngine.Rendering.LocalKeyword..ctor (UnityEngine.Shader shader, System.String name)
  at UnityEngine.Rendering.Blitter.Initialize (UnityEngine.Shader blitPS, UnityEngine.Shader blitColorAndDepthPS)
  at UnityEngine.Rendering.Universal.UniversalRenderPipeline..ctor (UniversalRenderPipelineAsset asset)
  at UnityEngine.Rendering.RenderPipelineManager.TryPrepareRenderPipeline (RenderPipelineAsset pipelineAsset)
```

**Repro** (one occurrence each, Master build of 2026-11-21):

- `python Tools/Python/run_player.py --exe "<Master build>/Minecraft Clone.exe" --tag t -- -mc-run bogus` —
  an unknown action, so no harness code runs: the arguments are rejected in `RunPendingOnce` and the player quits.
- `… -- -mc-run agent` — the agent action refuses in a build without the CLI runtime server and ends the run.

Not seen in the Development builds (2 and 4) that quit via `-mc-run agent` + `voxel-engine quit`. A Master run that
lives past its first frames was not checked.

**Not the same as** a player built while `Assets/UniversalRenderPipelineGlobalSettings.asset` was restored
mid-build: that player logged the same exception tens of thousands of times and rendered black frames (Development
build, 2026-11-21). The `player-runner` agent now restores assets only after the build completes.

**Open questions.** Whether URP is (re)creating its pipeline during shutdown after `Application.Quit` from the main
menu's first `Start`, and whether a quit deferred by one frame avoids it.

---
