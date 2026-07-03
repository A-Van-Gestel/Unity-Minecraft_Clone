---
name: cave-tuning
description: Tunes cave generation on StandardBiomeAttributes biomes using the CaveDensityAnalyzer editor tool — baseline analysis, trunk-vs-local worm attribution, ranked density levers, and zone attenuation/suppression math. Use when tuning cave generation parameters for biomes, analyzing cave density, running the CaveDensityAnalyzer, or debugging why caves appear too dense, too sparse, or fail to suppress in certain zones.
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

// With forceRefresh — use immediately after a SerializedObject config change
// to guarantee the analysis reads the updated asset data.
return CaveDensityAnalyzer.RunAnalysis(8, 42, 0, 0, "Grasslands", forceRefresh: true);

// With trunkMode override — e.g. exclude trunk worms to isolate local-only density
return CaveDensityAnalyzer.RunAnalysis(8, 42, 0, 0, "Grasslands", TrunkWormMode.Exclude);

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

### Stale results after config changes — use `forceRefresh`

After modifying a biome via `SerializedObject.ApplyModifiedProperties()` + `AssetDatabase.SaveAssets()`, a subsequent analysis call at the same seed/origin has occasionally returned stale numbers. No explicit static cache was found in the analyzer code; the cause appears to be an in-flight asset state mismatch. The public API now exposes a `forceRefresh` bool (default `false`) that calls `AssetDatabase.Refresh()` before running:

```csharp
// Guarantee freshness immediately after a config change
return CaveDensityAnalyzer.RunAnalysis(8, 42, 0, 0, "Grasslands", forceRefresh: true);
```

Leave `forceRefresh: false` in normal tuning loops — `AssetDatabase.Refresh()` adds a measurable delay. Only pass `true` when you're verifying a config change at the exact same seed/origin you used for the baseline.

As a secondary practice: compare results at **the same seed/origin used for the baseline** — density varies naturally by location (observed swings of 6–9% between different origins for the same config), so same-origin comparison is the only clean regression check.

### What it reports

- **Overview**: total cave air, overall/median/min/max density, chunks with no caves
- **Density Distribution**: bucket counts (0%, <2%, 2-5%, 5-10%, >10%)
- **Pocket Analysis**: per-chunk pocket counts, sizes (smallest/median), large pocket distribution
- **Cross-Chunk Networks**: global network stats after merging pockets across chunk boundaries via union-find (network count, sizes, chunks spanned, merge amplification, global connectivity)
- **Network Y-Range**: true vertical extent of merged networks (min/median/avg/max Y-span, largest network's Y-range)
- **Network Isolation**: nearest-neighbor centroid distance between networks in chunk units (min/median/avg)
- **Shape Quality**: tip/thin/open block ratios with quality assessment
- **Y-Level Histogram**: vertical density profile showing cave air count per Y-level — use this to verify surface penetration (check for nonzero carve counts at/above the reported avg surface height)
- **Heatmap**: spatial density grid (X/Z) for visualizing cave zone clustering
- **Layer Breakdown**: per-layer block counts and chunk coverage — essential for spotting when one layer dominates

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
- **Cave layers** (array of `StandardCaveLayer`):
    - **Type** (`mode` field): `Cheese` (3D blob carving), `Spaghetti2D` (6-way 2D axis-pair average), `Spaghetti3D` (dual 3D noise zero-crossing), `Noodle` (tunnel carving along noise zero-crossings), or `WormCarver` (random-walk tunnels)
    - **Zone Attenuation** (per-layer): How strongly the zone noise suppresses this layer. Range 0-1. Higher = more suppression in quiet zones. Works for all modes:
    - **Noodle/Spaghetti/Cheese**: boosts the noise threshold (`threshold += (1 - zoneNoise) * 0.5 * attn`). Full suppression when `threshold + 0.5*attn >= 1.0`.
    - **WormCarver**: multiplies spawn probability (`effectiveSpawn = spawnChance * (1 - (1 - zoneNoise) * 0.5 * attn)`). This only affects *local* worms — trunk worms are not modulated. The impact on overall density is therefore modest in worlds where trunks dominate. Useful for adding biome-specific local clustering on top of the trunk-worm baseline.
    - **Threshold**: Base carve threshold. Higher = less carving.
    - **Height range**: `minHeight` / `maxHeight` with `depthFadeMargin` for smooth vertical transitions.
    - **3D Density**: Optional density noise that modulates cave size.
    - **Domain Warp**: Optional noise-based position distortion for organic shapes.

### SerializedObject property names

When using `FindPropertyRelative` to modify layers via `Unity_RunCommand`, the correct paths are:

- Mode field: `"mode"` (enum `CaveMode`: Cheese=0, Spaghetti2D=1, WormCarver=2, Noodle=3, Spaghetti3D=4)
- WormCarver properties: `"wormSpawnChance"`, `"maxWormsPerChunk"`, `"wormShape.radiusMin"`, `"wormShape.radiusMax"`, `"wormShape.squashAxis"`, `"wormShape.squashFactor"`, `"wormShape.radiusWaveCount"`, `"wormShape.radiusNoiseStrength"`, `"wormShape.radiusNoiseFrequency"`, `"wormWaviness"`, `"wormHorizontalBias"`, `"wormYAttraction.strength"`, `"wormYAttraction.minY"`, `"wormYAttraction.maxY"`, `"wormMinLength"`, `"wormMaxLength"`, `"wormBranching.branchChance"`, `"wormBranching.maxBranchDepth"`, `"wormNoiseSeeking.checkInterval"`, `"wormNoiseSeeking.seekDistance"`, `"wormNoiseSeeking.seekChance"`
- Cheese/Noodle/Spaghetti properties: `"threshold"`, `"noiseConfig.frequency"`, `"zoneAttenuation"`, `"isSeekableByLocalWorms"`, `"isSeekableByTrunkWorms"`, `"minHeight"`, `"maxHeight"`, `"depthFadeMargin"`

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

**WormCarver density approximation:**

```
density ∝ worm_count × avg_length × avg_radius²
```

The r² factor dominates. A radius change from 4 to 6 triples volume (~2.25x from r² alone). This is the most sensitive lever for WormCarver density.

### Critical knowledge: no threshold cap may exist

`zoneBoostedThreshold` must NOT be capped below `1.0` (e.g. `math.min(..., 0.99f)`). Noodle carve values peak at exactly `1.0` at noise zero-crossings, so any cap below `1.0` always allows a thin band of carving at every zero-crossing and makes full cave suppression impossible.

If caves appear everywhere despite high attenuation, grep the three evaluation paths — `StandardChunkGenerationJob.cs`, `StandardChunkGenerator.cs`, and `WorldGenPreviewWindow.CrossSection.cs` — for a `math.min` cap on the boosted threshold; none may exist.

### Full suppression requirements

For a zone to have zero caves, the boosted threshold must exceed the max carve value:

- **Cheese caves**: need `zoneBoostedThreshold >= 1.0` → `attenuation > (1.0 - cheeseThreshold)`
- **Noodle caves**: need `zoneBoostedThreshold >= 1.0` → `attenuation > (1.0 - noodleThreshold)`

Example: noodle threshold 0.93, need attenuation > 0.07 for full suppression in quiet zones.

**Cheese attenuation safety range**: Even if `thresh + 0.5 × attn < 1.0` (below full suppression), a specific seed/origin can hit a zone noise cluster that's consistently low, causing cheese pockets to vanish entirely in the analyzed region. For cheese to reliably appear in any 8×8 analysis grid, keep attenuation ≤ 0.26–0.28 for typical thresholds (~0.83–0.84). This still gives good zone clustering without risking invisible cheese at some world locations.

## WormCarver density levers (ranked by impact)

When density is too high or too low, adjust these levers in order:

1. **`wormShape.radiusMax`** — largest impact (volume ∝ r²). Changing radiusMax from 4 to 6 roughly triples effective volume. Always tune this first.
2. **`wormShape.radiusNoiseStrength`** — noise variation creates radius bulges at the max end. Because volume ∝ r², time spent at high radius is disproportionately expensive. Set to 0 for predictable behavior; use sparingly (≤0.25) when organic variation is needed.
3. **`wormMaxLength`** — scales linearly with density. More predictable to change than spawn rate.
4. **`wormSpawnChance`** — linear relationship when nothing else changes. However, this interacts with height range (see below).
5. **`maxHeight` (height range expansion)** — worm spawn checks fire for every position in [minHeight, maxHeight]. Extending from 50 to 78 adds ~62% more spawn positions. Always compensate `wormSpawnChance` downward when raising maxHeight significantly.
6. **`wormBranching.maxBranchDepth`** — exponential. Depth 3 creates ~5× more worm paths than depth 2 at the same branchChance. Never use depth ≥ 3 without dramatically reducing spawn rate.

## Trunk worm volume domination — diagnostic test

Trunk worms contribute to **every biome** they traverse, independent of per-biome local spawn. In worlds with long trunk worms, they can silently dominate density even when local spawn is near zero.

**Symptom**: density stays high even after reducing `wormSpawnChance` to very low values (e.g. 0.004–0.008). The Layer Breakdown section will show worm blocks unchanged.

**Diagnostic test** — run this before any further spawn tuning:

```csharp
using Editor.Dev;
using Data.WorldTypes;
using UnityEditor;

// 1. Temporarily disable trunk worms
var wtd = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(
    AssetDatabase.GUIDToAssetPath(
        AssetDatabase.FindAssets("Standard t:WorldTypeDefinition")[0]));
var so = new SerializedObject(wtd);
so.FindProperty("trunkWormConfig.enabled").boolValue = false;
so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();

// 2. Analyze the biome (use fresh origin to bypass cache)
var result = CaveDensityAnalyzer.RunAnalysis(8, 42, 200, 200, "Grasslands");

// 3. Re-enable trunk worms
so.FindProperty("trunkWormConfig.enabled").boolValue = true;
so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();
return result;
```

**Interpreting the result**:
- Density drops to **<1%** → trunk worms are the floor. Fix the **trunk worm shape** (reduce `radiusMax`, clear `radiusNoiseStrength`, shorten `maxLength`), not local spawn.
- Density stays **similar** → local worm volume or per-biome Cheese/Noodle is the culprit. Tune local spawn + layer parameters.

### Trunk worm shape parameters that inflate volume

| Parameter | Risk | Recommendation |
|-----------|------|----------------|
| `shape.radiusMax` | Cubic effect via r² | Keep ≤ 4–5; trunk default was 5.0, not 6.0 |
| `shape.radiusNoiseStrength` | Bulge spikes at max radius | Keep 0; add ≤ 0.15 for mild organic variation |
| `maxLength` | Linearly inflates coverage | 300–400 is sufficient for cross-biome traversal |
| `yAttraction.strength` | Packs all trunks into one depth band, stacking volume | Keep 0 or use very weak (≤ 0.15); let trunks wander |

## WormCarver zone attenuation — scope and expectations

WormCarver `zoneAttenuation` (implemented in `StandardWormCarverJob.cs`) modulates the **local worm spawn probability** per chunk using:

```
effectiveSpawnChance = wormSpawnChance * (1 - (1 - zoneNoise) * 0.5 * attn)
```

At zoneNoise=-1 (minimum): spawn factor = `1 - attn`
At zoneNoise=0 (neutral): spawn factor = `1 - 0.5*attn`
At zoneNoise=+1 (maximum): spawn factor = 1.0 (always full spawn)

**Important limitation**: this only modulates *local* worms. Trunk worms traverse all biomes uniformly and are not affected by per-biome `zoneAttenuation`. In worlds where trunk worms generate the majority of cave density (as they do by default), WormCarver attenuation produces modest spatial variation in local worms layered on top of the flat trunk baseline. It will not significantly move the "chunks with no caves" metric if trunks blanket every chunk. Use the `TrunkWormMode.Exclude` analysis to measure the local-only effect before tuning.

## Cheese-Worm connectivity

The `isSeekableByLocalWorms` and `isSeekableByTrunkWorms` flags live on **Cheese, Noodle, and Spaghetti** layers (they are hidden for WormCarver layers, which are the seekers, not the sought).

Set **both to `true` on all Cheese layers** so that local worms and trunk worms steer toward cheese pockets. Without this, cheese pockets are almost always isolated — the worm seeker checks fire but find nothing to home in on.

```csharp
var l1 = so.FindProperty("caveLayers").GetArrayElementAtIndex(1); // Cheese layer
l1.FindPropertyRelative("isSeekableByLocalWorms").boolValue = true;
l1.FindPropertyRelative("isSeekableByTrunkWorms").boolValue = true;
```

Noodle layers should generally remain `false` for both (no value in worms seeking thin fissures).

## Surface penetration

To allow a small fraction of caves to breach the terrain surface (for exploration):

1. Set worm `maxHeight` = terrain surface height + 15–25 (e.g. surface ~58 → maxHeight 75–80)
2. Set `depthFadeMargin` = 18–22 so the fade starts right at the surface and tapers above
3. Use a weak `wormYAttraction.strength` (0.15–0.20) with `wormYAttraction.maxY` close to but below the surface (e.g. surface ~58 → maxY 50–54)

This keeps most carving underground, while the natural drift of unconstrained worm steps occasionally reaches surface level. Verify by checking the Y-Level Histogram for nonzero counts at/above the reported `Avg surface height`.

## Tuning methodology

1. **Start with the analyzer** — run a baseline at a consistent seed/origin (e.g. seed=42, origin=0,0). Record all metrics.
2. **Run the trunk worm diagnostic** — disable trunks and re-run to see the local-only density. This splits the problem into trunk vs local contributions before you turn any knobs.
3. **Fix trunk shape first** if trunk contribution is dominant.
4. **Set zone frequency** based on desired clustering scale (see table above).
5. **Adjust thresholds and attenuation** — Cheese 0.78–0.85, Noodle 0.93–0.955. For full zone suppression: `thresh + 0.5 × attn ≥ 1.0`. Keep cheese attenuation ≤ 0.26–0.28 to avoid seed-specific vanishing.
6. **Adjust WormCarver density levers** (see ranked list above) — change radiusMax before spawn rate.
7. **Re-run analyzer with a fresh origin** — compare against the same-origin baseline. Check density, empty%, Y-histogram for surface penetration.
8. **Iterate** — typically 2–5 rounds per biome. Avoid changing more than 2–3 parameters per round so you can attribute changes.

## Cross-biome transition guidelines

Adjacent biomes should use similar zone frequencies to avoid abrupt cave boundary shifts:

- Keep zone frequencies within 2x of neighbors (e.g. 0.008 and 0.010, not 0.008 and 0.040)
- Exception: Mountain biome can use higher frequency (0.04) since its terrain already creates natural visual boundaries

## Modifying biome .asset files

Per CLAUDE.md rules, never edit `.asset` files directly. Use `Unity_RunCommand` with `SerializedObject`:

```csharp
using Data.WorldTypes;
using UnityEditor;
using System.IO;

// Safe biome lookup — FindAssets("Grasslands") also matches "Steep Grasslands"
// (substring match). Always filter by exact asset filename.
var guids = AssetDatabase.FindAssets("Grasslands t:StandardBiomeAttributes");
string targetPath = null;
foreach (var g in guids)
{
    var p = AssetDatabase.GUIDToAssetPath(g);
    if (Path.GetFileNameWithoutExtension(p) == "Grasslands") { targetPath = p; break; }
}
var biome = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(targetPath);
var so = new SerializedObject(biome);

so.FindProperty("caveZoneNoiseConfig.frequency").floatValue = 0.008f;

var layers = so.FindProperty("caveLayers");
var l0 = layers.GetArrayElementAtIndex(0); // WormCarver layer
l0.FindPropertyRelative("wormSpawnChance").floatValue = 0.025f;
l0.FindPropertyRelative("wormShape.radiusMax").floatValue = 3.5f;
l0.FindPropertyRelative("wormShape.radiusNoiseStrength").floatValue = 0.15f;
l0.FindPropertyRelative("wormYAttraction.strength").floatValue = 0.18f;
l0.FindPropertyRelative("wormYAttraction.maxY").floatValue = 52.0f;

var l1 = layers.GetArrayElementAtIndex(1); // Cheese layer
l1.FindPropertyRelative("threshold").floatValue = 0.84f;
l1.FindPropertyRelative("zoneAttenuation").floatValue = 0.26f;
l1.FindPropertyRelative("isSeekableByLocalWorms").boolValue = true;
l1.FindPropertyRelative("isSeekableByTrunkWorms").boolValue = true;

so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();
return "Done";
```
