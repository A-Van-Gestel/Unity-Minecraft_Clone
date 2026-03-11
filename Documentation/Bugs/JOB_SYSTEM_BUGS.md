# Known Job System related bugs

This document outlines **open** bugs related to the Unity Job System integration for chunk generation, meshing, and lighting. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 02. `ProcessGenerationJobs` always uses `biomes[0]` for flora generation

**Severity:** Bug  
**Files:** `WorldJobManager.cs` — `ProcessGenerationJobs` (line 352)

> [!CAUTION]
> **SEED BREAKING:** Fixing this changes tree height distributions in non-default biomes. Already-explored chunks are unaffected (trees are baked into saved voxel data), but **newly generated chunks** in an existing world would have different tree patterns, creating a visible
> seam at the old/new terrain boundary.

When processing tree generation mods, the code uses `_world.biomes[0].minHeight` and `_world.biomes[0].maxHeight` for ALL flora positions, regardless of which biome they fall in. In a multi-biome world, trees in non-default biomes get incorrect height ranges.

```csharp
// Current: always uses biomes[0]
IEnumerable<VoxelMod> floraMods = Structure.GenerateMajorFlora(
    mod.ID, mod.GlobalPosition,
    _world.biomes[0].minHeight,    // Should be the actual biome at mod.GlobalPosition
    _world.biomes[0].maxHeight);
```

---
