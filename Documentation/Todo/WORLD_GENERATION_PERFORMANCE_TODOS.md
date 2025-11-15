# World Generation Performance TODO's

This document outlines planned improvements to the world generation system. The primary goal is to make the entire pipeline, especially the `ChunkGenerationJob`, fully Burst-compatible for a significant performance increase. Secondary optimizations are also listed for further
refinement.

## 1. Making `ChunkGenerationJob` Burst-Compatible

The current `ChunkGenerationJob` provides a minor performance uplift by multithreading the generation of voxel columns. However, it cannot be compiled with the Burst compiler, which prevents a 5-10x performance gain. The changes outlined below are the steps required to enable
Burst compilation.

**Breaking Change Notice:** Implementing these changes will alter the noise algorithm. As a result, worlds generated with the same seed integer **will not be the same** as worlds generated before this change. This work should be scheduled for a major version update where breaking
changes are permissible.

### The Core Problem: `Mathf.PerlinNoise`

The single blocker for Burst compatibility is the use of `Mathf.PerlinNoise()` within the `Noise.cs` and `WorldGen.cs` static classes. This is a managed API call from the `UnityEngine` namespace and is inaccessible to Burst-compiled jobs.

### Implementation Steps

The migration process involves replacing the noise source and updating the code that consumes it.

#### Step 1: Integrate a Burst-Compatible Noise Library

A Burst-compatible noise library is required. Two excellent options are:

1. **Unity.Mathematics.noise:** Unity's built-in noise library. It is highly optimized for Burst and provides functions like `noise.snoise()` (Simplex Noise), which is a high-quality alternative to Perlin noise. This is the recommended, dependency-free approach.
2. **Third-Party Libraries:** A library like a Burst-compatible port of FastNoise can be used if specific noise types (e.g., Cellular, Worley) are desired.

#### Step 2: Create a New, Burst-Safe Noise Helper

The existing `Noise.cs` should be left as-is for legacy compatibility if needed, or deprecated. A new, clean static class must be created. This class will use the `Unity.Mathematics` library and its associated types (`float2`, `float3`, `math`).

**Example: `Assets/Scripts/Jobs/BurstData/BurstNoise.cs`**

```csharp
using Unity.Mathematics;
using static Unity.Mathematics.noise;

public static class BurstNoise
{
    /// <summary>
    /// A Burst-compatible 2D Simplex noise function.
    /// </summary>
    public static float Get2D(float2 position, float offset, float scale)
    {
        // Note: Unity.Mathematics.noise does not require a seed/offset to be added manually.
        // It's often better to offset the input position.
        position += offset; 
        return snoise(position / scale);
    }

    /// <summary>
    /// A Burst-compatible 3D Simplex noise function.
    /// </summary>
    public static bool Get3D(float3 position, float offset, float scale, float threshold)
    {
        position += offset;
        position *= scale;

        // A common 3D noise technique using Simplex noise.
        float noiseVal = (snoise(position) + snoise(position + new float3(100.3f, 203.1f, 50.7f))) / 2f;
        
        return noiseVal > threshold;
    }
}
```

#### Step 3: Refactor `WorldGen.cs`

The `WorldGen.GetVoxel` method must be updated to call the new `BurstNoise` helper instead of the old `Noise` class.

**Example Change:**

```csharp
// In WorldGen.cs

// BEFORE:
float weight = Noise.Get2DPerlin(new Vector2(pos.x, pos.z), biomes[i].Offset, biomes[i].Scale);

// AFTER:
float weight = BurstNoise.Get2D(new float2(pos.x, pos.z), biomes[i].Offset, biomes[i].Scale);
```

#### Step 4: Enable Burst on `ChunkGenerationJob`

Once all managed code references are removed, the `[BurstCompile]` attribute can be added to the job struct in `ChunkGenerationJob.cs`. This is the final step that unlocks the massive performance gain.

```csharp
// In Assets/Scripts/Jobs/ChunkGenerationJob.cs

[BurstCompile]
public struct ChunkGenerationJob : IJobFor
{
    // ... job contents
}
```

---

## 2. Other Potential Performance Improvements

These are additional optimizations that can be explored independently of the Burst compatibility migration.

### A. Pre-calculating a Biome Map

* **Problem:** The current `ChunkGenerationJob` calculates the biome for every single voxel column by looping through all biome types and evaluating their noise weights. This is inefficient, especially as more biomes are added.
* **Solution:** Introduce a preceding, lightweight "Biome Job". This job would run first and generate a low-resolution 2D `NativeArray<byte>` representing the biome for each column in a chunk. The main `ChunkGenerationJob` would then simply read from this pre-calculated map
  instead of running the expensive biome selection loop for every column.
* **Benefit:** Decouples biome selection from voxel generation and reduces redundant noise calculations, leading to a faster `ChunkGenerationJob`.

### B. Generating Structures Directly in a Job

* **Problem:** Currently, the `ChunkGenerationJob` only identifies a *location* for a tree and enqueues a `VoxelMod`. The main thread then has to process this mod and run the `Structure.MakeTree` logic, which generates a large queue of new `VoxelMod`s. This moves significant work
  back to the main thread and adds overhead through the modification queue.
* **Solution (Advanced):** Create a dedicated "Structure Generation Job" that runs after the main terrain generation. This job would take the generated `OutputMap` as input. When it identifies a location for a tree, it would directly write the log and leaf block IDs into the
  `NativeArray<uint>`.
* **Benefit:** Keeps the entire structure generation process off the main thread, significantly reducing frame hitches when new chunks with many trees are generated.
* **Complexity:** This is a more complex task, as it requires careful handling of writes that cross chunk boundaries.

### C. Chaining Jobs with Dependencies

* **Problem:** The current `WorldJobManager` uses the `Update` loop to check if jobs are complete and then schedule the next ones (e.g., check if generation is done, then schedule lighting). This can introduce a 1-frame delay between dependent stages.
* **Solution:** Refactor the `WorldJobManager` to use `JobHandle` dependencies. When a generation job is scheduled, its `JobHandle` can be passed to the `Schedule()` call of the lighting job.
  ```csharp
  // Conceptual example
  JobHandle generationHandle = generationJob.Schedule();
  JobHandle lightingHandle = lightingJob.Schedule(generationHandle);
  ```
* **Benefit:** This allows the Job System to automatically start the lighting job as soon as the generation job is complete, without waiting for the main thread to intervene. It improves job throughput and reduces the latency from generation to a fully-meshed chunk appearing on
  screen.
