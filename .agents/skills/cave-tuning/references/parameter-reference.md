# Cave Generation Parameters — Catalog, Property Paths & Formulas

Companion reference for the `cave-tuning` skill. Cave config lives on `StandardBiomeAttributes`
ScriptableObjects under `Assets/Data/WorldGen/Biomes/`.

## Key parameters

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

## SerializedObject property names

When using `FindPropertyRelative` to modify layers via `Unity_RunCommand`, the correct paths are:

- Mode field: `"mode"` (enum `CaveMode`: Cheese=0, Spaghetti2D=1, WormCarver=2, Noodle=3, Spaghetti3D=4)
- WormCarver properties: `"wormSpawnChance"`, `"maxWormsPerChunk"`, `"wormShape.radiusMin"`, `"wormShape.radiusMax"`, `"wormShape.squashAxis"`, `"wormShape.squashFactor"`, `"wormShape.radiusWaveCount"`, `"wormShape.radiusNoiseStrength"`, `"wormShape.radiusNoiseFrequency"`, `"wormWaviness"`, `"wormHorizontalBias"`, `"wormYAttraction.strength"`, `"wormYAttraction.minY"`, `"wormYAttraction.maxY"`, `"wormMinLength"`, `"wormMaxLength"`, `"wormBranching.branchChance"`, `"wormBranching.maxBranchDepth"`, `"wormNoiseSeeking.checkInterval"`, `"wormNoiseSeeking.seekDistance"`, `"wormNoiseSeeking.seekChance"`
- Cheese/Noodle/Spaghetti properties: `"threshold"`, `"noiseConfig.frequency"`, `"zoneAttenuation"`, `"isSeekableByLocalWorms"`, `"isSeekableByTrunkWorms"`, `"minHeight"`, `"maxHeight"`, `"depthFadeMargin"`

## Critical formulas

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

## Full suppression requirements

For a zone to have zero caves, the boosted threshold must exceed the max carve value:

- **Cheese caves**: need `zoneBoostedThreshold >= 1.0` → `attenuation > (1.0 - cheeseThreshold)`
- **Noodle caves**: need `zoneBoostedThreshold >= 1.0` → `attenuation > (1.0 - noodleThreshold)`

Example: noodle threshold 0.93, need attenuation > 0.07 for full suppression in quiet zones.

**Cheese attenuation safety range**: Even if `thresh + 0.5 × attn < 1.0` (below full suppression), a specific seed/origin can hit a zone noise cluster that's consistently low, causing cheese pockets to vanish entirely in the analyzed region. For cheese to reliably appear in any 8×8 analysis grid, keep attenuation ≤ 0.26–0.28 for typical thresholds (~0.83–0.84). This still gives good zone clustering without risking invisible cheese at some world locations.

## Trunk worm shape parameters that inflate volume

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

## Cross-biome transition guidelines

Adjacent biomes should use similar zone frequencies to avoid abrupt cave boundary shifts:

- Keep zone frequencies within 2x of neighbors (e.g. 0.008 and 0.010, not 0.008 and 0.040)
- Exception: Mountain biome can use higher frequency (0.04) since its terrain already creates natural visual boundaries
