# World Generation Performance TODOs

This document outlines planned improvements to the world generation system. The primary goal is to make the entire pipeline, especially the `ChunkGenerationJob`, fully Burst-compatible for a 5-10x performance increase.

## 1. Making `ChunkGenerationJob` Burst-Compatible

The current `ChunkGenerationJob` provides a minor performance uplift by multithreading the generation of voxel columns. However, it is limited by managed code dependencies. To unlock Burst compilation, we must remove these dependencies.

> **Breaking Change Notice:** Implementing these changes will alter the noise algorithm. Worlds generated with the same seed **will not match** previous versions. This work should be scheduled for a major version update.

### The Core Problem: `Mathf.PerlinNoise`

`Mathf.PerlinNoise` is a managed Unity API. It cannot be called from Burst. Furthermore, standard Unity noise lacks the variety (Cellular, Cubic, White Noise) required for complex biomes.

### Implementation Steps

#### Step 1: Choose and Integrate a Burst-Compatible Noise Library

We need a raw C# or High-Performance C# (HPC#) noise solution. There are two primary paths:

1. **Unity.Mathematics.noise (Built-in):**

* *Pros:* Zero dependencies, highly optimized intrinsics.
* *Cons:* Limited noise types (mostly Perlin/Simplex), no fractal/cellular features out of the box.

2. **FastNoiseLite (Burst Port)** - *Selected Strategy*:

* *Pros:* Extensive features (Cellular, Domain Warp, Fractal Ridged), identical algorithm to standard C++ FastNoise.
* *Implementation:* We will implement a custom port using `SharedStatic` to handle lookup tables safely within Burst.

#### Step 2: Create a `BurstNoise` Abstraction Layer

To keep the code clean and allow for easy switching of noise algorithms later without rewriting the Jobs, we will create a static helper class. This class acts as the API for our jobs.

**Example: `Assets/Scripts/Jobs/BurstData/BurstNoise.cs`**

```csharp
using Unity.Mathematics;

/// <summary>
/// Static Burst-compatible wrapper for noise generation.
/// Abstracts the underlying library (FastNoiseLite) from the logic.
/// </summary>
public static class BurstNoise
{
    // We can hold a default configuration or pass it in.
    // For pure Burst, we usually pass the state in, but static helpers are useful for math.

    public static float GetTerrainNoise(FastNoiseLite noise, float2 pos, float scale, float offset)
    {
        // Unity Mathematics optimization: avoid heavy division in loops if possible, 
        // but here we prioritize readability for the example.
        float2 p = (pos + offset) * scale;
        return noise.GetNoise(p.x, p.y);
    }

    public static float Get3DMask(FastNoiseLite noise, float3 pos, float scale, int seedOffset)
    {
        // Create a local copy to modify seed without affecting the main state
        var localNoise = noise; 
        localNoise.SetSeed(noise.mSeed + seedOffset);
        
        return localNoise.GetNoise(pos.x * scale, pos.y * scale, pos.z * scale);
    }
}
```

#### Step 3: Pass Noise State via Job Data

We cannot access global static fields inside a Job. We must pass the noise configuration struct into the job.

```csharp
// The Job Definition
[BurstCompile]
public struct ChunkGenerationJob : IJobFor
{
    // Inputs
    [ReadOnly] public FastNoiseLite NoiseState; // Passed by Value (it's a struct)
    [ReadOnly] public NativeArray<BiomeData> Biomes;
    
    // Outputs
    [WriteOnly] public NativeArray<BlockType> OutputVoxels;

    public void Execute(int index)
    {
        // ... Calculation logic
        // float noiseVal = BurstNoise.GetTerrainNoise(NoiseState, pos.xz, 0.01f, 0);
    }
}
```

#### Step 4: Refactor `WorldGen.GetVoxel`

The logic inside `WorldGen.GetVoxel` must be moved inside the Job struct (inline) or into the `BurstNoise` static helper. The dependency on `UnityEngine.Vector3` must be replaced with `Unity.Mathematics.float3`.

---

## 2. Algorithmic Optimizations (Logic)

These improvements focus on *what* we calculate, ensuring we don't waste cycles on invisible blocks.

### A. Heightmap Early Exit (The "Sky Skip" Optimization)

* **Problem:** Currently, we loop `y` from 0 to `ChunkHeight` (e.g., 128) for every column. We calculate expensive 3D noise for air blocks high in the sky, which is wasteful.
* **Solution:**
    1. Calculate a cheap 2D "Terrain Height" first: `int height = GetHeight2D(x, z);`
    2. **Below Height (0 to height):** Run the full 3D Noise logic (Density check). This ensures we generate Stone, Dirt, or **Caves** (air pockets underground).
    3. **Above Height (height to 128):** Skip 3D noise entirely. Simply fill with Water (if below sea level) or Air.
* **Benefit:** Reduces noise calculations from **O(Volume)** to **O(Surface Area)** for the sky portion (approx. 40-60% reduction in math).
* **Critical Logic:** You must *not* simply fill `0` to `height` with solid blocks, or you will lose caves. The loop below the heightmap must still check density.
* **Complexity:** Moderate. Requires separating 2D terrain logic from 3D cave/overhang logic.

### B. Pre-calculated Biome Map

* **Problem:** We currently calculate the biome type for every voxel column inside the main generation loop. This involves checking temperature/humidity noise repeatedly.
* **Solution:** Run a lightweight "Biome Job" *before* the Chunk Generation Job. This produces a `NativeArray<byte>` (size 16x16) representing the biome ID for each column.
* **Benefit:** The heavy 3D generation job simply reads `BiomeMap[x + z * 16]` (O(1) lookup) instead of recalculating noise weights.
* **Complexity:** Low.

---

## 3. Architectural Improvements (The Pipeline)

### A. Job Chaining (Internal Dependencies)

* **Problem:** The `WorldJobManager` uses `Update()` to check if jobs are done, introducing latency.
* **Solution:** Use `JobHandle` chaining for dependent *internal* steps of generation.

  ```csharp
  // 1. Biome Job
  JobHandle biomeHandle = biomeJob.Schedule();
  
  // 2. Voxel Gen Job (depends on Biome data)
  JobHandle voxelHandle = voxelJob.Schedule(biomeHandle);
  ```

* **Constraint:** You generally **cannot** chain the `MeshingJob` directly to the `GenerationJob` of the same chunk. Meshing requires neighbor data (for occlusion culling), so the Meshing Job must wait for a "batch" of neighbors to complete generation first.
* **Benefit:** Increases throughput. The CPU never waits for the Main Thread to tell it to start the next task.
* **Complexity:** Low to Moderate. Requires refactoring the JobManager.

### B. Deferred Structure Generation

* **Problem:** Trees are generated on the Main Thread via `VoxelMod` queues to avoid cross-chunk threading issues (e.g., a tree modifying a chunk that isn't loaded yet).
* **Solution:**

1. **Gen Pass:** Generate terrain only. If a block is a valid tree spot, write coordinates to a `NativeList<float3> TreePoints`.
2. **Decoration Pass:** A separate system processes `TreePoints` only after the chunk *and its neighbors* are loaded.

* **Benefit:** Removes the massive Main Thread spikes caused by `Structure.MakeTree`.
* **Complexity:** High. Requires a state machine to track "Chunk Loaded" vs "Neighbors Loaded".

---

## 4. Micro-Optimizations (Math)

### A. SIMD / Vectorization

* **Observation:** Noise libraries often support calculating noise for 4 points at once (`float4`) using AVX/SSE instructions.
* **Plan:** If possible, rewrite the inner Y-loop to process `y, y+1, y+2, y+3` simultaneously.
* **Benefit:** Potential 4x speedup on the noise calculation portion.
* **Complexity:** High. Makes code harder to read. Only do this if the standard Burst optimization isn't fast enough.

### B. Look Up Tables (LUT)

* **Observation:** We use trig functions (`Mathf.Sin`, `cos`) for biome blending and offsets.
* **Plan:** Bake these values into a `static readonly NativeArray<float>` or use `SharedStatic` arrays if the math is complex and a possible precision loss is acceptable.
* **Benefit:** Memory lookup is often faster than transcendental math instructions.
