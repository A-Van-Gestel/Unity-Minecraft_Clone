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

## Reference files

Load on demand — not needed for every activation:

| Need                                                                                       | Reference                                                    |
|--------------------------------------------------------------------------------------------|----------------------------------------------------------------|
| Analyzer report sections, metric interpretation ranges, grid-size vs zone-frequency table | [references/analyzer-metrics.md](references/analyzer-metrics.md)     |
| Parameter catalog, SerializedObject property paths, formulas, suppression math, guidelines | [references/parameter-reference.md](references/parameter-reference.md) |
| Ready-to-adapt `Unity_RunCommand` scripts (asset edits, trunk diagnostic, biome lookup)    | [references/runcommand-scripts.md](references/runcommand-scripts.md)  |

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
```

Parameters: `RunAnalysis(gridSize, seed, originX, originZ, singleBiomeMode, biome)`. For biomes
not registered in the `WorldTypeDefinition`, load the asset directly — script in
[references/runcommand-scripts.md](references/runcommand-scripts.md).

What each report section means and the good/problem ranges for every metric are in
[references/analyzer-metrics.md](references/analyzer-metrics.md).

### Stale results after config changes — use `forceRefresh`

After modifying a biome via `SerializedObject.ApplyModifiedProperties()` + `AssetDatabase.SaveAssets()`, a subsequent analysis call at the same seed/origin has occasionally returned stale numbers (in-flight asset state mismatch; no static cache in the analyzer itself). Pass `forceRefresh: true` — which calls `AssetDatabase.Refresh()` before running — when verifying a config change at the exact same seed/origin used for the baseline. Leave it `false` in normal tuning loops; the refresh adds a measurable delay.

As a secondary practice: compare results at **the same seed/origin used for the baseline** — density varies naturally by location (observed swings of 6–9% between different origins for the same config), so same-origin comparison is the only clean regression check.

## Tuning methodology

1. **Start with the analyzer** — run a baseline at a consistent seed/origin (e.g. seed=42, origin=0,0). Record all metrics.
2. **Run the trunk worm diagnostic** — disable trunks and re-run to see the local-only density (see below). This splits the problem into trunk vs local contributions before you turn any knobs.
3. **Fix trunk shape first** if trunk contribution is dominant (shape-parameter table in [references/parameter-reference.md](references/parameter-reference.md)).
4. **Set zone frequency** based on desired clustering scale (grid-size table in [references/analyzer-metrics.md](references/analyzer-metrics.md)).
5. **Adjust thresholds and attenuation** — Cheese 0.78–0.85, Noodle 0.93–0.955. For full zone suppression: `thresh + 0.5 × attn ≥ 1.0`. Keep cheese attenuation ≤ 0.26–0.28 to avoid seed-specific vanishing (details in [references/parameter-reference.md](references/parameter-reference.md)).
6. **Adjust WormCarver density levers** (ranked list below) — change radiusMax before spawn rate.
7. **Re-run analyzer with a fresh origin** — compare against the same-origin baseline. Check density, empty%, Y-histogram for surface penetration.
8. **Iterate** — typically 2–5 rounds per biome. Avoid changing more than 2–3 parameters per round so you can attribute changes.

## WormCarver density levers (ranked by impact)

When density is too high or too low, adjust these levers in order (volume ∝ count × length × r²):

1. **`wormShape.radiusMax`** — largest impact (volume ∝ r²). Changing radiusMax from 4 to 6 roughly triples effective volume. Always tune this first.
2. **`wormShape.radiusNoiseStrength`** — noise variation creates radius bulges at the max end. Because volume ∝ r², time spent at high radius is disproportionately expensive. Set to 0 for predictable behavior; use sparingly (≤0.25) when organic variation is needed.
3. **`wormMaxLength`** — scales linearly with density. More predictable to change than spawn rate.
4. **`wormSpawnChance`** — linear relationship when nothing else changes. However, this interacts with height range (see below).
5. **`maxHeight` (height range expansion)** — worm spawn checks fire for every position in [minHeight, maxHeight]. Extending from 50 to 78 adds ~62% more spawn positions. Always compensate `wormSpawnChance` downward when raising maxHeight significantly.
6. **`wormBranching.maxBranchDepth`** — exponential. Depth 3 creates ~5× more worm paths than depth 2 at the same branchChance. Never use depth ≥ 3 without dramatically reducing spawn rate.

## Trunk worm volume domination — diagnostic test

Trunk worms contribute to **every biome** they traverse, independent of per-biome local spawn. In worlds with long trunk worms, they can silently dominate density even when local spawn is near zero.

**Symptom**: density stays high even after reducing `wormSpawnChance` to very low values (e.g. 0.004–0.008). The Layer Breakdown section will show worm blocks unchanged.

**Diagnostic test** — run this before any further spawn tuning: temporarily disable `trunkWormConfig.enabled` on the `WorldTypeDefinition`, analyze at a fresh origin, re-enable (full script in [references/runcommand-scripts.md](references/runcommand-scripts.md)).

**Interpreting the result**:

- Density drops to **<1%** → trunk worms are the floor. Fix the **trunk worm shape** (reduce `radiusMax`, clear `radiusNoiseStrength`, shorten `maxLength`), not local spawn.
- Density stays **similar** → local worm volume or per-biome Cheese/Noodle is the culprit. Tune local spawn + layer parameters.

## Critical knowledge: no threshold cap may exist

`zoneBoostedThreshold` must NOT be capped below `1.0` (e.g. `math.min(..., 0.99f)`). Noodle carve values peak at exactly `1.0` at noise zero-crossings, so any cap below `1.0` always allows a thin band of carving at every zero-crossing and makes full cave suppression impossible.

If caves appear everywhere despite high attenuation, grep the three evaluation paths — `StandardChunkGenerationJob.cs`, `StandardChunkGenerator.cs`, and `WorldGenPreviewWindow.CrossSection.cs` — for a `math.min` cap on the boosted threshold; none may exist.

## Modifying biome .asset files (hard rule)

Per CLAUDE.md rules, never edit `.asset` files directly. Use `Unity_RunCommand` with `SerializedObject` — a ready-to-adapt script (including the exact-filename biome lookup that avoids the "Grasslands" / "Steep Grasslands" substring-match trap) is in [references/runcommand-scripts.md](references/runcommand-scripts.md), and the full property-path catalog is in [references/parameter-reference.md](references/parameter-reference.md).
