---
name: cave-tuning
description: Use when tuning cave generation parameters for biomes, analyzing cave density, or debugging why caves appear too dense, too sparse, or fail to suppress in certain zones.
---

# Cave Tuning Protocol

## When to use this skill

- "Caves are everywhere / there's no variation."
- "This biome needs different cave character."
- "The cave zone attenuation isn't working."
- Any request to tune, analyze, or debug cave generation parameters on `StandardBiomeAttributes`.

## Cave Density Analyzer Tool

The project includes a dedicated analysis tool at `Assets/Editor/Dev/CaveDensityAnalyzer.cs`.

### How to run it

Use `Unity_RunCommand` to invoke the static API:

```csharp
// Single biome (in WorldTypeDefinition)
using Editor.Dev;
return CaveDensityAnalyzer.RunAnalysis(8, 42, 0, 0, "Grasslands");

// Single biome NOT in WorldTypeDefinition (e.g. Steep Grasslands)
using Editor.Dev;
using Data.WorldTypes;
using UnityEditor;
var guids = AssetDatabase.FindAssets("Steep Grasslands t:StandardBiomeAttributes");
var biome = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(
    AssetDatabase.GUIDToAssetPath(guids[0]));
return CaveDensityAnalyzer.RunAnalysis(8, 42, 0, 0, true, biome);
```

Parameters: `RunAnalysis(gridSize, seed, originX, originZ, singleBiomeMode, biome)`

### What it reports

- **Overview**: total cave air, overall/median/min/max density, chunks with no caves
- **Density Distribution**: bucket counts (0%, <2%, 2-5%, 5-10%, >10%)
- **Pocket Analysis**: per-chunk pocket counts, sizes (smallest/median), large pocket distribution
- **Cross-Chunk Networks**: global network stats after merging pockets across chunk boundaries via union-find (network count, sizes, chunks spanned, merge amplification, global connectivity)
- **Network Y-Range**: true vertical extent of merged networks (min/median/avg/max Y-span, largest network's Y-range)
- **Network Isolation**: nearest-neighbor centroid distance between networks in chunk units (min/median/avg)
- **Shape Quality**: tip/thin/open block ratios with quality assessment
- **Y-Level Histogram**: vertical density profile showing cave air count per Y-level
- **Heatmap**: spatial density grid (X/Z) for visualizing cave zone clustering

### Interpreting results

| Metric                | Good range | Problem indicator                                          |
|-----------------------|------------|------------------------------------------------------------|
| Overall density       | 1-6%       | >10% = too hollow, 0% = caves suppressed                   |
| Chunks with no caves  | 15-40%     | 0% = no zone variation, >60% = too sparse                  |
| Tip blocks            | <15%       | >30% = heavy artifacting                                   |
| Open blocks           | >25%       | <10% = all narrow tunnels                                  |
| Global connectivity   | 0.1-0.5    | >0.7 = one dominant cave system, <0.05 = highly fragmented |
| Merge amplification   | 2-10x      | >20x = per-chunk stats massively understate network scale  |
| Max chunks spanned    | 3-15       | >30 = cave system spans most of the grid                   |
| Network median Y-span | 15-40      | >60 = full-depth systems, <5 = flat horizontal layers      |
| Network isolation     | 2-4 chunks | >6 = very sparse, <1 = networks feel continuous            |
| Median Y-span         | 5-15       | >25 = mostly vertical shafts, <=3 = flat pancake caves     |

### Grid size vs zone frequency

The analysis grid must span enough zone noise wavelengths to observe variation:

| Zone freq | 256-block grid (8 chunks) | Wavelengths                          |
|-----------|---------------------------|--------------------------------------|
| 0.003     | ~0.77                     | Too few — can't see clustering       |
| 0.006     | ~1.5                      | Minimal variation visible            |
| 0.008     | ~2.0                      | Good — multiple cave/no-cave regions |
| 0.010     | ~2.6                      | Good                                 |
| 0.040     | ~10.2                     | Many small clusters                  |

Rule: use grid size 8+ with zone frequencies 0.006+, or increase grid size for lower frequencies.

## Cave Generation Parameters

Cave config lives on `StandardBiomeAttributes` ScriptableObjects under `Assets/Data/WorldGen/Biomes/`.

### Key parameters

- **`caveZoneNoiseConfig.frequency`**: Controls spatial clustering of cave zones. Higher = more frequent cave/no-cave transitions.
- **`caveZoneAttenuation`**: How strongly the zone noise suppresses caves. Range 0-1. Higher = more suppression in quiet zones.
- **Cave layers** (array of `StandardCaveLayer`):
    - **Type**: `Cheese` (3D blob carving) or `Noodle` (tunnel carving along noise zero-crossings)
    - **Threshold**: Base carve threshold. Higher = less carving.
    - **Height range**: `minHeight` / `maxHeight` with `fadeHeight` for smooth vertical transitions.
    - **3D Density**: Optional density noise that modulates cave size.
    - **Domain Warp**: Optional noise-based position distortion for organic shapes.

### Critical formulas

**Zone threshold boost:**

```
caveZoneThresholdBoost = (1 - zoneNoise) * 0.5 * attenuation
zoneBoostedThreshold = baseThreshold + caveZoneThresholdBoost
```

**Effective threshold with depth fade:**

```
effectiveThreshold = 1 - depthFade * (1 - zoneBoostedThreshold)
```

**Noodle carve value** (peaks at exactly 1.0 at noise zero-crossings):

```
noodleValue = 1.0 - (sqrt(raw^2 + 0.0036) - 0.06)
```

### Critical knowledge: the 0.99f threshold cap

The codebase previously capped `zoneBoostedThreshold` at `0.99f` via `math.min(..., 0.99f)`. This prevented full cave suppression because noodle values peak at exactly `1.0` — so even a `0.99` threshold always allows a thin band of carving at every zero-crossing.

**This cap was removed** from all three evaluation paths:

- `StandardChunkGenerationJob.cs` (line ~399)
- `StandardChunkGenerator.cs` (line ~514)
- `WorldGenPreviewWindow.CrossSection.cs` (line ~1224)

If caves appear everywhere despite high attenuation, check whether this cap has been reintroduced.

### Full suppression requirements

For a zone to have zero caves, the boosted threshold must exceed the max carve value:

- **Cheese caves**: need `zoneBoostedThreshold >= 1.0` → `attenuation > (1.0 - cheeseThreshold)`
- **Noodle caves**: need `zoneBoostedThreshold >= 1.0` → `attenuation > (1.0 - noodleThreshold)`

Example: noodle threshold 0.93, need attenuation > 0.07 for full suppression in quiet zones.

## Tuning methodology

1. **Start with the analyzer** — run a baseline to understand current state.
2. **Set zone frequency** based on desired clustering scale (see table above).
3. **Set attenuation** high enough to allow full suppression in quiet zones.
4. **Adjust thresholds** — higher = less carving. Cheese 0.75-0.85, Noodle 0.90-0.95.
5. **Run analyzer again** — check density, pocket sizes, Y-histogram, heatmap.
6. **Iterate** — typically 2-5 rounds per biome.

## Cross-biome transition guidelines

Adjacent biomes should use similar zone frequencies to avoid abrupt cave boundary shifts:

- Keep zone frequencies within 2x of neighbors (e.g. 0.008 and 0.010, not 0.008 and 0.040)
- Exception: Mountain biome can use higher frequency (0.04) since its terrain already creates natural visual boundaries

## Modifying biome .asset files

Per CLAUDE.md rules, never edit `.asset` files directly. Use `Unity_RunCommand` with `SerializedObject`:

```csharp
using Data.WorldTypes;
using UnityEditor;
var guids = AssetDatabase.FindAssets("Grasslands t:StandardBiomeAttributes");
var biome = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(
    AssetDatabase.GUIDToAssetPath(guids[0]));
var so = new SerializedObject(biome);
so.FindProperty("caveZoneNoiseConfig.frequency").floatValue = 0.008f;
so.FindProperty("caveZoneAttenuation").floatValue = 0.20f;
// For cave layer array elements:
var layers = so.FindProperty("caveLayers");
layers.GetArrayElementAtIndex(0).FindPropertyRelative("threshold").floatValue = 0.82f;
so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();
return "Done";
```
