# Improved Cave Generation

> Design document for the next iteration of the cave generation system.
> Captures lessons learned from the current implementation and outlines concrete improvements.

**Created:** 2026-05-26
**Status:** Draft

---

## 1. Retrospective: What We Learned

### 1.1 The Experimentation Arc

The cave generation system went through several iterations:

1. **Cheese (Blob) only** --- Large 3D noise chambers. Created underground pockets but no connective tissue between them. Caves felt like isolated rooms with no exploration flow.

2. **Cheese + Spaghetti** --- Added 6-axis 2D noise averaging for tunnel networks. Produced highly interconnected rooms, but the 2D source created a visible grid-like repetition. Every cave system looked the same from every angle.

3. **Cheese + Noodle** --- Replaced Spaghetti with isoband zero-crossing tunnels. Produced thin winding corridors, but because noodle values peak at exactly 1.0 at *every* zero-crossing, caves appeared uniformly everywhere. This forced the creation of Zone Attenuation to gate cave density spatially.

4. **Cheese + Noodle + Zone Attenuation** --- Added a 2D noise field to modulate cave thresholds. Successfully created cave-dense and cave-sparse regions, but the caves themselves remained samey within zones. The system also broke up large networks, making exploration feel fragmented rather than varied.

5. **Cheese + Noodle + Worm Carver** (Mountain only) --- The worm carver produced the most exploration-friendly caves: directional tunnels with branches, natural dead ends, and genuine player decisions. Combined with cheese chambers as destinations, this was the most promising configuration.

### 1.2 Key Findings from the CaveDensityAnalyzer

We built a cross-chunk-aware analyzer with union-find boundary merging. Running it across all 5 biomes revealed:

| Finding                                  | Detail                                                                                                                                                                                                                                                                                                                                                                           |
|------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Noodle dominates all biomes**          | 4 of 5 biomes rely on Noodle as their primary tunnel generator. Only Mountain has a Worm Carver.                                                                                                                                                                                                                                                                                 |
| **Zone Attenuation is a band-aid**       | It compensates for Noodle's lack of inherent spatial variation. Worm Carvers naturally produce varied density via spawn chance + random walk --- they don't need zone gating.                                                                                                                                                                                                    |
| **Connectivity is hard to control**      | In Noodle-heavy biomes, caves are either everywhere (low attenuation) or fragmented into disconnected pockets (high attenuation). There's no sweet spot for "large but varied" networks.                                                                                                                                                                                         |
| **Forrest was the worst offender**       | 8.1% density, 87% connectivity, 44-chunk spanning networks. Tuning improved density (4.2%) and zone variation (41% empty chunks), but connectivity remained high (89%) because the surviving cave zones merge into one supernetwork.                                                                                                                                             |
| **Mountain had zero variation**          | 0% empty chunks before tuning. Attenuation 0.13 couldn't suppress cheese caves (needs >0.25 for threshold 0.75). After raising to 0.28 and cheese threshold to 0.80, variation improved dramatically.                                                                                                                                                                            |
| **Network isolation is universally low** | All biomes show <1.0 chunk average nearest-neighbor distance. Cave networks feel continuous rather than being distinct, separated systems.                                                                                                                                                                                                                                       |
| **Analyzer limitation**                  | The `CaveDensityAnalyzer` cannot evaluate biomes not in the `WorldTypeDefinition` --- the `StandardChunkGenerator.Initialize()` always loads biomes from the WorldType array and does a reference equality check to find the force index. Biomes like Steep Grasslands that exist only as standalone assets produce identical results to the first WorldType biome (Grasslands). |

### 1.3 The Core Problem

The current cave system treats all generators uniformly and layers them additively. Each generator independently decides whether to carve, with no coordination. This produces two failure modes:

1. **Over-carving**: Multiple generators carve the same regions, producing swiss-cheese terrain. Zone Attenuation was added to suppress this, but it's a global dimmer switch, not a compositor.

2. **Under-variation**: Noise-based generators (Cheese, Noodle) produce statistically uniform output. Every chunk in a cave zone gets the same density, the same pocket sizes, the same tunnel widths. The player never encounters a surprising cave --- just more of the same.

The Worm Carver avoids both problems by construction: it's procedurally unique per chunk (random walk), naturally sparse (spawn chance), and creates directional flow (the player follows the tunnel).

---

## 2. Generator Taxonomy

### 2.1 Worm Carver --- Primary Tunnel Generator

**Purpose:** Creates the main tunnel network that players explore. Produces directional, branching passages with genuine decision points.

**Strengths:**

- Inherently unique per seed --- no two cave systems look alike
- Natural spatial variation via spawn chance (no zone gating needed)
- Branching creates exploration flow: main trunk + side passages
- Noise-seeking connects worms to other cave features (cheese chambers, other generators)
- Cross-chunk aware via scatter approach (searches neighboring chunks)

**Weaknesses:**

- Vertical bias: pitch is clamped to +/-72 degrees (`PI * 0.4`), but the random walk still trends vertical over long distances
- Uniform diameter: uses a fixed `wormBaseRadius` for the entire worm
- Spherical carving: each step carves a sphere, producing a "bead necklace" profile at low step counts

**Current Implementation:**

- Scatter-based pre-pass job (`StandardWormCarverJob`)
- Stack-based DFS for branching (no recursion --- Burst-safe)
- Noise seeking: periodically looks ahead for cheese/spaghetti/noodle caves and steers toward them
- Branch children get half the parent's remaining length
- Output: `NativeBitArray` worm mask consumed by the main generation job

**Current Config (Mountain only):**
`spawn=0.015 maxWorms=1 r=3 wave=0.33 len=50-200 branch=0.05 maxDepth=2 seek={interval=10, dist=10, chance=0.5}`

Note: The three noise-seeking fields (`wormSeekInterval`, `wormSeekDistance`, `wormSeekChance`) are currently separate fields on `StandardCaveLayer`. Section 3.5 proposes grouping them into a `WormNoiseSeeking` config struct for clarity.

### 2.2 Cheese (Blob) --- Chamber Generator

**Purpose:** Creates large open caverns and small pockets. Provides the "rooms" that tunnel generators connect.

**Strengths:**

- Simple, predictable: single 3D noise threshold
- Scales well: threshold controls density directly
- Creates explorable spaces with high "open block" ratios (>60%)
- Works well as destinations for worm carver noise-seeking

**Weaknesses:**

- No inherent connectivity --- chambers are isolated unless another generator connects them
- Uniform density at a given threshold --- no natural variation without zone gating
- Can produce very large contiguous air volumes at low thresholds

**Best used as:** Supporting layer. Cheese chambers are the *destinations* that worm tunnels lead to. Configure with moderate-to-high thresholds (0.80-0.85) to keep chambers from dominating.

### 2.3 Spaghetti --- Legacy Interconnected Network

**Purpose:** Creates interconnected room networks via 6-axis 2D noise averaging. The only noise-based generator that produces natural connectivity.

**Strengths:**

- Inherently interconnected --- rooms connect to rooms
- Multiple entry points per cave system
- Combined with Cheese, produces pleasant exploration flow

**Weaknesses:**

- 2D source creates visible grid-like repetition --- every cave looks the same
- Computationally expensive: 6 noise evaluations per voxel (+ early-out bound check)
- No directional flow --- the player wanders rather than explores

**Best used as:** Not recommended as primary. Could be revisited if the 2D repetition problem is solved (e.g., domain warping the 2D axes independently, or using 3D noise pairs instead of 2D).

### 2.4 Noodle (Isoband) --- Ravine / Fissure Generator

**Purpose:** Creates thin, winding corridors along noise zero-crossings. Produces narrow vertical fissures and underground ravines.

**Strengths:**

- Produces unique "underground ravine" aesthetic
- Smoothed isoband formula (`sqrt(raw^2 + 0.0036) - 0.06`) eliminates sharp carving edges
- Domain warp creates organic, non-linear fissure paths

**Weaknesses:**

- Zero-crossings exist *everywhere* in noise fields --- noodle caves appear at every location unless actively suppressed
- This property forced the creation of Zone Attenuation as a compensation mechanism
- Produces flat, uniform density --- no natural hot spots or dead zones
- Narrow tunnel width with limited variation --- exploration feels constrained
- Peak value of exactly 1.0 means even a threshold of 0.99 still carves a thin band everywhere

**Best used as:** Special-purpose accent layer for specific biomes. Appropriate for:

- Mountain fissures (vertical ravines through alpine terrain)
- Desert sandstone channels (narrow, weathered passages)
- **Not** as a primary tunnel generator for general biomes

---

## 3. Proposed Changes

### 3.1 Worm Carver Improvements

#### 3.1.1 Horizontal Bias

**Problem:** Worms trend vertical over long distances. The pitch perturbation is symmetric around the current pitch, and the clamp at +/-72 degrees allows sustained vertical movement.

**Solution:** Add a configurable `horizontalBias` parameter (0.0-1.0) that applies a restoring force toward horizontal:

```
// After perturbation, blend pitch toward zero
pitch = math.lerp(pitch, 0f, horizontalBias * 0.1f);
```

At `horizontalBias = 0.5`, a worm that's been going vertical for several steps will gradually level out. The effect is subtle per-step but accumulates over the worm's lifetime, producing caves that are mostly horizontal with occasional vertical sections rather than sustained vertical shafts.

**New field on `StandardCaveLayer`:**

```csharp
[Range(0f, 1f)]
[Tooltip("How strongly worms are pulled toward horizontal. " +
         "0 = no bias (original behavior). " +
         "0.5 = gentle leveling. " +
         "1.0 = strongly horizontal with only brief vertical dips.")]
public float wormHorizontalBias = 0.5f;
```

#### 3.1.2 Radius Variation

**Problem:** Worms have a fixed `wormBaseRadius` for their entire length, producing uniform-width tunnels. Real caves have narrow squeezes and wide chambers.

**Solution:** Modulate radius per-step using a low-frequency sine wave with noise perturbation:

```
float t = (float)step / worm.LengthRemaining;
float wave = math.sin(t * math.PI * radiusWaveCount) * 0.5f + 0.5f;  // [0, 1]
float radius = math.lerp(radiusMin, radiusMax, wave);
```

This creates alternating wide and narrow sections along the tunnel. The `radiusWaveCount` controls how many wide/narrow cycles occur per worm.

**Implemented as** a `WormShape` struct on `StandardCaveLayer` (as `wormShape`) and `TrunkWormConfig` (as `shape`), grouping `radiusMin`, `radiusMax`, `squashAxis`, `squashFactor`, `radiusWaveCount`, `radiusNoiseStrength`, and `radiusNoiseFrequency` into a single collapsible inspector block. The struct provides `Default` (local worms: radius [2, 4]) and `TrunkDefault` (trunk worms: radius [3, 5]) static properties. The legacy `wormBaseRadius` field remains on `StandardCaveLayer` for backwards compatibility but is superseded by `radiusMin`/`radiusMax`.
Job data fields remain flat for Burst compatibility — the `StandardCaveLayerJobData` and `TrunkWormConfigJobData` constructors read from the struct.

**Noise-modulated radius (Implemented):** A hybrid blend mode replaces or augments the sine wave with 3D OpenSimplex2 noise (`FastNoiseLite.CreateSimple()`) sampled at the worm's world position. Two fields on `WormShape` control this: `radiusNoiseStrength` (0 = pure sine wave, 1 = pure noise) and `radiusNoiseFrequency` (spatial frequency of the noise). The blend formula is:

```
radiusFactor = lerp(sineWave, saturate(radiusNoise.GetNoise(pos) * 0.5 + 0.5), strength)
radius = lerp(radiusMin, radiusMax, radiusFactor)
```

Using the worm's 3D position as noise input makes each worm naturally unique (different paths sample different noise values) with spatially coherent variation (nearby steps produce smooth radius transitions). The sine wave remains available at strength 0 for structured rhythm. Trunk worms share the same `WormShape` struct (via `TrunkWormConfig.shape`), so both tiers use identical radius noise fields. `BiomeConfigValidator` warns when noise is enabled but frequency is very low (< 0.02), which produces features too large to create visible variation.

#### 3.1.3 Ellipsoidal Carving ~~(Optional, Lower Priority)~~ (Implemented)

**Problem:** Spherical carving produces a circular cross-section. Real caves have wider-than-tall profiles.

**Solution:** Scale the Y-axis of the carving test by a configurable `verticalSquash` factor (e.g., 0.6 = 40% shorter than wide):

```
float3 delta = blockPos - pos;
delta.y /= verticalSquash;  // Stretch Y distance = squash carving
if (math.lengthsq(delta) <= radSq) { /* carve */ }
```

This is a simple change but significantly affects cave feel --- tunnels become wider hallways rather than circular tubes.

**Implemented as:** `squashFactor` + `squashAxis` fields on the `WormShape` struct, shared by both `StandardCaveLayer.wormShape` (per-biome local worms) and `TrunkWormConfig.shape` (world-level trunk worms). Squash range `[0.3, 1.0]`, default `1.0` (sphere, backwards compatible). A `WormSquashAxis` enum (`Vertical` / `Horizontal`) selects the compressed axis: Vertical produces wider-than-tall hallways, Horizontal produces taller-than-wide fissures. The axis conversion is applied in the JobData constructors via `WormSquashAxisHelper.ToEffectiveSquash()` —
`Horizontal` inverts
the value (`1 / squash`) — so the Burst job always works with a single effective squash float. The Y bounding box of the carving loop is scaled to `radius * effectiveSquash`, and the chunk AABB early-out uses a bounding sphere that encloses the ellipsoid regardless of squash direction. `BiomeConfigValidator` warns when effective squash < 0.5 or > 2.0.

#### 3.1.4 Y-Level Attraction (Implemented)

**Problem:** Worms have no preference for which Y level they settle at after spawning. The horizontal bias (Section 3.1.1) keeps them level but doesn't steer toward a target depth. Over long worms (150+ steps), random-walk drift spreads caves uniformly across the height range instead of concentrating them at specific depth bands.

**Solution:** A Y-band attraction system applies a per-step restoring force toward a configurable `[yAttractionMin, yAttractionMax]` band. Inside the band, no force is applied. Outside, the worm's pitch is nudged toward the nearest band edge proportionally to distance:

```
float yDelta = select(0, yAttrMax - pos.y, pos.y > yAttrMax);
yDelta = select(yDelta, yAttrMin - pos.y, pos.y < yAttrMin);
float desiredPitch = clamp(atan2(yDelta, 16f), -PI * 0.3, PI * 0.3);
pitch = lerp(pitch, desiredPitch, yAttractionStrength * 0.1f);
```

The `16f` denominator models a virtual target 16 blocks ahead horizontally, producing gentle arcs. The `* 0.1f` scaling matches horizontal bias --- each step nudges pitch 5% at strength 0.5, accumulating over 40-50 steps into a smooth correction.

**Implemented as:** A `WormYAttraction` serializable struct (matching the `WormNoiseSeeking` grouping pattern) with fields `strength` (0-1, default 0 = disabled), `minY`, and `maxY`. Used as `wormYAttraction` on `StandardCaveLayer` (per-biome local worms, default band [20, 40]) and `yAttraction` on `TrunkWormConfig` (world-level trunk worms, default band [15, 35]). Per-biome override via `trunkYAttractionCenterOverride` on `StandardBiomeAttributes` --- shifts the trunk band center while preserving the global band width (same pattern as
`trunkVerticalBiasOverride`). Job data structs keep the fields flat (`WormYAttractionStrength`, `WormYAttractionMin`, `WormYAttractionMax`) for Burst blittability. Applied in `SimulateWormStack` after horizontal bias and before noise seeking. The runtime normalizes `min <= max` via `math.min`/`math.max` to guard against inverted config. `BiomeConfigValidator` warns when the attraction band is inverted, doesn't overlap the spawn height range, or strength exceeds 0.8.

### 3.2 Zone Attenuation: Per-Layer Opt-In

**Problem:** Zone Attenuation currently applies globally to all cave layers in a biome. This makes sense for suppressing Noodle everywhere, but it also suppresses Worm Carvers and Cheese chambers that don't need it.

**Solution:** Move the `caveZoneAttenuation` field from `StandardBiomeAttributes` to `StandardCaveLayer`, making it opt-in per layer.

**Migration:**

1. Add `zoneAttenuation` field to `StandardCaveLayer` (default 0.0 = no zone effect).
2. Keep the biome-level `caveZoneNoiseConfig` (the noise field itself is shared across layers --- only the attenuation strength varies).
3. Deprecate `StandardBiomeAttributes.caveZoneAttenuation`.
4. For biomes that currently use zone attenuation, set it only on the Noodle layer (where it's actually needed).

**Result:** Worm Carvers and Cheese chambers generate independently of zone noise. Only generators that inherently lack spatial variation (Noodle) opt into zone gating. This eliminates the "band-aid" quality of the current system.

**Job-side change:** The `caveZoneThresholdBoost` calculation moves inside the per-layer loop, gated by `caveLayer.ZoneAttenuation > 0f`:

```csharp
for (int i = 0; i < biome.CaveLayerCount; i++)
{
    // ... depth fade ...

    float zoneBoostedThreshold = caveLayer.Threshold;
    if (caveLayer.ZoneAttenuation > 0f)
    {
        float zoneNoise = CaveZoneNoises[biomeIndex].GetNoise(globalX, globalZ);
        float boost = (1f - zoneNoise) * 0.5f * caveLayer.ZoneAttenuation;
        zoneBoostedThreshold += boost;
    }
    float effectiveThreshold = zoneBoostedThreshold + (1f - depthFade) * (1f - zoneBoostedThreshold);
    // ... evaluate ...
}
```

The worm carver's spawn-chance modulation (`wormSpawnFactor`) would similarly become opt-in, reading from the worm layer's own `zoneAttenuation` instead of the biome-level field. This means a worm layer with `zoneAttenuation = 0` spawns purely based on `wormSpawnChance`, which is the natural, exploration-friendly behavior.

### 3.3 Recommended Biome Reconfiguration

Once worm carver improvements, per-layer zone attenuation, and the trunk worm layer (Section 3.4) are implemented, reconfigure all biomes to follow this strategy. Note: "Local Worm" refers to the per-biome Tier 2 worm carvers; "Trunk Worm" is the world-level Tier 1 layer configured on the `WorldTypeDefinition` (see Section 3.4.2).

| Layer                  | Generator  | Purpose                            | Zone Atten                          |
|------------------------|------------|------------------------------------|-------------------------------------|
| Cross-biome highways   | Trunk Worm | Exploration backbone (world-level) | 0 (world-level, no zone gating)     |
| Biome-specific tunnels | Local Worm | Per-biome cave personality         | 0 (spawn chance is sufficient)      |
| Chambers               | Cheese     | Destinations, large pockets        | 0 (threshold controls density)      |
| Accent (optional)      | Noodle     | Ravines, fissures (biome-specific) | 0.2-0.4 (prevents uniform coverage) |

**Per-biome notes:**

- **Grasslands / Steep Grasslands:** Trunk Worm (world-level) + Local Worm + Cheese. No Noodle. Moderate local worm spawn chance, gentle horizontal bias. These biomes should have the most "standard" cave feel. Default `trunkSpawnSuppression = 0` (trunk origins allowed).
- **Forrest:** Trunk Worm + Local Worm + Cheese. Consider slightly higher local branch chance for more complex networks (root-like caves under forests). Default trunk suppression.
- **Desert:** Trunk Worm + Local Worm + Cheese. Low local worm spawn chance (sparse caves). Optionally a Noodle layer with high zone attenuation for rare sandstone channels. Consider `trunkSpawnSuppression = 0.5` (fewer trunk origins, but trunks from neighbors still pass through).
- **Mountain:** Trunk Worm + Local Worm + Cheese + Noodle. Keep Noodle for alpine fissures with zone attenuation. Local worm carver with lower horizontal bias (allow more vertical shafts through mountain rock). Largest height range. Default trunk suppression.

### 3.4 Cross-Biome Worm Architecture (Trunk + Local)

#### 3.4.1 The Problem

When all biomes have worm carvers (Phase 4), the per-biome model creates N independent worm populations with no coordination. Worms from adjacent biomes can overlap redundantly, and cave networks remain local to their origin biome (typically 10-15 chunks). The player never encounters a cave system that spans a meaningful distance across the world --- exploration dead-ends at biome boundaries.

Three approaches were considered:

- **Parameter Blending at Boundaries:** Worms interpolate config as they cross biomes. Rejected --- multi-biome junctions (3+ biomes meeting) require the same Voronoi blending that `FastNoiseLite` handles internally, which is not easily adaptable to per-step worm parameter interpolation. Harder to implement properly than it appears.
- **Global Worm Layer with Biome Modifiers:** Single world-level config with per-biome overrides. Rejected as sole solution --- a shared baseline constrains biome personality, and any change to the global config forces recalibration of all biome overrides. Too rigid for biomes that need fundamentally different cave character.
- **Two-Tier System (Trunk + Local):** Recommended. Separates the cross-biome connectivity problem (trunks) from the biome personality problem (locals). Each tier has a clear role and can be tuned independently.

#### 3.4.2 Two-Tier Design

**Tier 1 --- Trunk Worms (World-Level)**

Trunk worms define the major cave highways that span multiple biomes. They provide the exploration backbone: the player follows a trunk tunnel, and it leads them through different underground environments.

| Property          | Value / Range         | Rationale                                                  |
|-------------------|-----------------------|------------------------------------------------------------|
| Config location   | World-level           | Shared across all biomes for continuity                    |
| Spawn grid        | Deterministic, sparse | Low spawn chance (~0.5-1%) on a world-level scatter grid   |
| Length            | 200-400 steps         | Long enough to span 20-40 chunks, crossing multiple biomes |
| Radius            | 3-5 (with variation)  | Wide enough to feel like a main passage                    |
| Branch chance     | Low (0.02-0.04)       | Occasional forks, not a dense network                      |
| Horizontal bias   | High (0.6-0.8)        | Mostly horizontal to maximize biome crossings              |
| Zone attenuation  | 0                     | Trunks should not be suppressed by zone noise              |
| Biome interaction | Simple modifiers only | Spawn suppression (0-1), vertical bias override            |

Trunk worms use the origin chunk's *world-level* config, not the biome config. As a trunk crosses biome boundaries, its parameters remain stable --- this is intentional. The trunk is infrastructure; it should feel consistent regardless of which biome the player is currently in. The biome's influence is felt through the local caves that branch off the trunk, not through the trunk itself.

**Biome modifiers for trunks** are deliberately minimal:

- `trunkSpawnSuppression` (0-1): Reduces the chance of a trunk *originating* in this biome. Set to 1.0 to prevent trunk spawns (the biome can still be *traversed* by trunks from neighbors). Example: Desert might suppress trunk spawns to keep caves sparse, but a trunk from Grasslands can still tunnel through.
- `trunkVerticalBiasOverride` (float, -1 = disabled): Allows a biome to locally override the trunk's horizontal bias as the worm steps through that biome. Applied per-step: when a trunk worm's current position falls in a biome with an active override, the horizontal bias lerp (Section 3.1.1) uses the override value instead of the global trunk config value. This means a trunk naturally transitions its vertical behavior as it crosses biome boundaries — no explicit blending needed, the per-step application handles it. Example: Mountain sets
  `trunkVerticalBiasOverride = 0.3` (lower than the global 0.6-0.8), so trunks dip vertically while passing through mountain rock, then level out again when they exit into Grasslands.

**Tier 2 --- Local Worms (Per-Biome)**

Local worms are the existing per-biome worm carver layers, unchanged from the current system. They create the biome-specific cave personality: root-like branching networks under forests, sparse wide caverns under deserts, vertical shafts through mountains.

| Property         | Value / Range             | Rationale                                            |
|------------------|---------------------------|------------------------------------------------------|
| Config location  | Per-biome (as today)      | Full biome control over all parameters               |
| Length           | 30-120 steps              | Short enough to stay mostly within origin biome      |
| Radius           | 2-4 (biome-dependent)     | Narrower than trunks --- side passages, not highways |
| Branch chance    | Biome-dependent           | Forest: high (0.08). Desert: low (0.02)              |
| Horizontal bias  | Biome-dependent           | Mountain: low (0.2). Grasslands: high (0.6)          |
| Zone attenuation | 0 (spawn chance suffices) | Per Section 3.2                                      |

Local worms don't need cross-biome coordination because they're short. A local worm that wanders 10-20 blocks into a neighboring biome is acceptable --- the visual discontinuity is minor and adds organic variety at biome borders.

#### 3.4.3 How Trunks and Locals Interact

Trunk worms and local worms carve independently into the same worm mask. Their interaction is emergent:

- **Indirect connections via shared noise targets:** Noise seeking steers worms toward high-value regions in noise-based cave layers (e.g., cheese chambers), not toward already-carved worm tunnels. When both a trunk and a local worm seek toward the same cheese chamber, they naturally converge on the same location, creating junction points without explicit coordination. The quality of these connections depends on cheese layer density and seek flag configuration (Section 3.4.4).
- **Direct worm-to-worm seeking (future):** Section 3.5.3 explores allowing worms to optionally seek toward the worm mask itself, which would create more reliable connections between trunks and locals. This is deferred pending feasibility analysis.
- **Redundant carving is acceptable:** When a local worm overlaps a trunk, the air volume doubles in the overlap zone. At the block level this is invisible (air is air). The only effect is slightly wider passages at junction points, which actually reads as a natural "intersection" or "chamber" where tunnels meet.
- **Exploration flow:** The player discovers a local cave (biome-specific), follows it, and it connects to a trunk (via a shared cheese chamber or coincidental path overlap). The trunk leads them across biome boundaries into new territory, where they discover different local caves. This creates a natural exploration loop: local → trunk → local (new biome).

#### 3.4.4 Worm Noise Seeking: Opt-In Seekable Layers

**Problem:** The current worm carver noise-seeking algorithm hardcodes which noise types it samples (cheese, noodle). This has two issues:

1. **Trunk worms cross biome boundaries** --- they don't have a single biome's noise configs to seek against. A trunk worm in Grasslands (cheese threshold 0.82) crossing into Mountain (cheese threshold 0.80, different noise config) would need to know which biome's noise to sample at each look-ahead position.
2. **Local worms lack seek configurability** --- a biome author can't control which layers attract local worms without touching code. With per-layer zone attenuation (Section 3.2) making layers more independent, seek targets should be equally configurable.

Three approaches were considered for trunk worms, but the chosen solution generalizes to both tiers:

- **No noise seeking for trunks:** Trunks carve via pure random walk. Simplest, but trunks might pass through cheese chambers without connecting, missing natural junction opportunities.
- **Trunk-specific seek noise:** The trunk config defines its own dedicated 3D noise as a steering guide, decoupled from actual cave features. Simple, no biome dependency, but seek targets have no relationship to real cave geometry --- trunks would steer toward phantom features that don't produce caves.
- **Opt-in seekable flags on cave layers (recommended):** Each `StandardCaveLayer` declares whether it can be sought by trunk worms, local worms, or both. Worms evaluate only flagged layers during noise seeking. This generalizes cleanly to both tiers.

**Recommended approach: per-tier seekable flags.**

Each `StandardCaveLayer` gains two new fields:

```csharp
[Tooltip("When enabled, world-level trunk worms will steer toward this layer's " +
         "cave features during noise seeking. Typically enabled for Cheese layers " +
         "(to connect trunks to chambers) and disabled for Noodle layers.")]
public bool isSeekableByTrunkWorms;

[Tooltip("When enabled, per-biome local worms will steer toward this layer's " +
         "cave features during noise seeking. Typically enabled for Cheese layers " +
         "(worms connect to chambers) and disabled for Noodle and other Worm layers.")]
public bool isSeekableByLocalWorms;
```

Two separate bools are preferred over a flags enum for inspector clarity --- there are only two tiers, and a third is unlikely.

**How it works at runtime:**

*Trunk worms (cross-biome seeking):*

1. The trunk worm reaches a noise-seeking check (every `checkInterval` steps).
2. For each look-ahead direction, the worm samples the biome at the look-ahead position.
3. It iterates the biome's cave layers, evaluating only those with `isSeekableByTrunkWorms == true`.
4. It takes the highest carve value found across all seekable layers at that position.
5. The worm steers toward the direction with the highest aggregate seek value, as the existing algorithm already does.

*Local worms (same-biome seeking):*

1. The local worm reaches a noise-seeking check (every `checkInterval` steps).
2. For each look-ahead direction, the worm evaluates its origin biome's cave layers with `isSeekableByLocalWorms == true`.
3. Same aggregation and steering as trunks.

The key difference: trunk worms look up the biome at each look-ahead position (cross-biome aware), while local worms use their origin biome for all look-ahead positions (simpler, no cross-biome lookup needed since local worms are short).

**Why this works well:**

- **Biome-aware without biome coupling.** Trunk worms don't need to understand biome configs --- they just evaluate whatever layers each biome has flagged. Grasslands flags its cheese layer; Mountain flags cheese + noodle; Desert flags nothing (trunks pass through without seeking). The trunk adapts automatically.
- **Configurable local seeking.** Local worms gain the same flexibility. A Forest biome can have local worms seek cheese chambers (`true`) but ignore noodle fissures (`false`). A Mountain biome can flag both. This replaces the current hardcoded seek behavior with biome-author control.
- **Per-layer granularity.** Each layer independently opts in or out for each worm tier. A cheese layer might be seekable by both trunks and locals, while a noodle layer is seekable by neither.
- **Scales to any number of layers.** If a biome has three cave layers, each worm tier evaluates whichever subset is flagged. No hardcoded assumptions about layer count or type.
- **Minimal performance cost.** Look-ahead only fires every `checkInterval` steps (currently 10). Each check evaluates a small number of seekable layers (typically 1-2) at a handful of look-ahead positions. The biome lookup for trunk look-ahead is the same lookup the generation job already performs per-voxel. Local worms skip the biome lookup entirely.

**Recommended defaults:**

| Layer type   | `isSeekableByTrunkWorms` | `isSeekableByLocalWorms` | Rationale                                                                                                                                                                                                            |
|--------------|--------------------------|--------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Cheese       | `true`                   | `true`                   | Chambers are natural destinations for all worms                                                                                                                                                                      |
| Noodle       | `false`                  | `false`                  | Too thin and uniform to be meaningful seek targets                                                                                                                                                                   |
| Worm (local) | N/A                      | N/A                      | Worm layers don't produce a noise field — seeking evaluates noise, not the worm mask. These flags should be hidden in the Biome Editor for Worm-type layers. See Section 3.5.3 for future worm-to-worm mask seeking. |

#### 3.4.5 Implementation Considerations

**Scatter grid separation:** Trunk worms use a separate scatter grid from local worms. The trunk grid is world-level (seeded from the world seed, not biome-dependent), while local grids remain per-biome as today. This ensures trunk spawns are evenly distributed across the world regardless of biome layout.

**Config storage:** Trunk worm config lives on the `WorldTypeDefinition` ScriptableObject (or a dedicated `WorldCaveConfig` asset referenced by it), not on individual biome assets. This reinforces that trunks are a world-level feature. Local worm config remains on `StandardBiomeAttributes` as today.

**Job integration:** The `StandardWormCarverJob` already processes multiple worm layers per chunk. Trunk worms are simply an additional layer evaluated before the per-biome local layers. The trunk layer's config comes from the world-level data rather than the biome data, but the carving mechanics are identical.

**Per-biome opt-out for global layers:** A biome can fully opt out of a global trunk layer by setting `trunkSpawnSuppression = 1.0`. This prevents trunks from *originating* in that biome but does not block trunks from *passing through* --- a trunk that spawned in a neighboring biome will still carve through the suppressed biome. To block traversal entirely (e.g., an ocean biome where underground caves make no sense), use the `trunkTraversalAllowed` flag (see Section 3.4.6).

#### 3.4.6 Trunk Traversal Blocking (Implemented)

**Problem:** `trunkSpawnSuppression` only prevents trunk *origins*. A trunk worm that spawned in a neighboring biome can still carve through a biome where underground caves make no sense (e.g., ocean, frozen tundra). There is no mechanism to stop a trunk worm mid-traversal.

**Solution:** Two fields on `StandardBiomeAttributes`:

- `trunkTraversalAllowed` (bool, default `true`): When `false`, any trunk worm entering this biome is terminated.
- `trunkTraversalFadeSteps` (int 0-30, default 0): Controls how the termination looks. `0` = hard cut (worm stops immediately). `> 0` = the worm's radius linearly tapers from full to `1/fadeSteps` over this many steps before terminating, producing a natural tunnel narrowing into a near-closed dead end.

**How it works at runtime:** The trunk worm's biome cache (updated every 16 steps) checks `TrunkTraversalAllowed`. When `false`:

- If `fadeSteps <= 0`: immediate `break` from the stepping loop (hard termination).
- If `fadeSteps > 0`: the worm enters fade mode. Each step scales the carving radius by `fadeRemaining / fadeTotal` then decrements the counter (so the first fade step carves at full radius, the last at `1/fadeTotal`). When the counter reaches zero, the worm terminates. During fade, noise seeking, branching, biome cache updates, and per-biome overrides (vertical bias, Y-attraction center) are all suppressed --- the worm follows its natural trajectory while narrowing.

**Fade state** is tracked per-worm via local variables `fadeRemaining` and `fadeTotal` inside `SimulateWormStack`. `fadeTotal` is captured once at fade start (from the blocking biome's config). The biome cache is frozen during fade, preventing the worm from picking up a different biome's overrides as it crosses boundaries while dying.

**Branch handling:** Branches already on the stack are processed independently. Each branch checks traversal on its own biome cache cycle --- if it starts inside a blocked biome, it terminates/fades on its own schedule. Branches are not spawned during fade (suppressed to prevent orphan tunnels).

**16-step leakage:** Because the biome cache updates every 16 steps, a worm may carve up to 15 steps into a blocked biome before detection. At biome boundaries this produces a natural-looking tunnel tapering into the coastline.

**Validation:** `BiomeConfigValidator` warns when traversal is blocked but `trunkSpawnSuppression < 1.0` (trunks may spawn and immediately terminate), when `trunkTraversalFadeSteps > 20` (long tapers extend deep into the blocked biome), and when `traversalFadeSteps > 0` with traversal allowed (dead configuration).

### 3.5 Noise Seeking Rework

#### 3.5.1 Config Struct Consolidation

**Problem:** The three noise-seeking fields (`wormSeekInterval`, `wormSeekDistance`, `wormSeekChance`) are currently separate top-level fields on `StandardCaveLayer`. They always belong together and are meaningless individually. Grouping them improves inspector readability and makes the relationship explicit.

**Solution:** Introduce a `WormNoiseSeeking` serializable struct:

```csharp
[Serializable]
public struct WormNoiseSeeking
{
    [Range(0, 30)]
    [Tooltip("Steps between noise-seeking checks. 0 = seeking disabled.")]
    public int checkInterval;

    [Range(1f, 30f)]
    [Tooltip("How far ahead the worm looks when seeking (in blocks).")]
    public float seekDistance;

    [Range(0f, 1f)]
    [Tooltip("Probability of performing a seek check when the interval fires.")]
    public float seekChance;
}
```

Replace the three fields on `StandardCaveLayer` with a single `wormNoiseSeeking` field of this type. Add corresponding fields to `StandardCaveLayerJobData` (flat struct, not nested — Burst requires blittable layout).

#### 3.5.2 Seek Target Rework: From Hardcoded to Flag-Based

**Current behavior:** The noise-seeking code in `StandardWormCarverJob` (lines 202-237) iterates *all* cave layers in the biome and seeks toward any layer whose mode is Cheese, Spaghetti, or Noodle. This is hardcoded — every non-worm layer is always a seek target with no opt-out.

**Proposed behavior:** Replace the hardcoded mode check with the `isSeekableByTrunkWorms` / `isSeekableByLocalWorms` flags introduced in Section 3.4.4. The seek loop becomes:

```csharp
for (int s = 0; s < biome.CaveLayerCount; s++)
{
    StandardCaveLayerJobData seekLayer = AllCaveLayers[biome.CaveLayerStartIndex + s];

    // Flag check replaces the old hardcoded mode filter
    if (!seekLayer.IsSeekableByLocalWorms) continue;  // or IsSeekableByTrunkWorms for trunk worms

    float noiseVal = EvaluateLayerNoise(seekLayer, lookPos);
    if (noiseVal > seekLayer.Threshold - 0.1f)
    {
        foundCave = true;
        break;
    }
}
```

This is a **narrowing** of the current behavior: today all non-worm layers are sought, after the rework only explicitly flagged layers are sought. The migration is: set `isSeekableByLocalWorms = true` on all existing Cheese layers (matching current behavior for the dominant seek target) and `false` on Noodle layers (which were always seekable before but produced poor seek signals due to their thin, uniform zero-crossings).

#### 3.5.3 Worm-to-Worm Seeking (Future Investigation)

**Idea:** Allow worms to optionally seek toward already-carved worm tunnels (the worm mask) in addition to noise-based cave layers. After a worm has carved for a minimum number of steps (e.g., 30+), it could sample the worm mask at look-ahead positions and steer toward existing tunnels. This would create natural junction points between independent worm systems, increasing network connectivity and exploration value — local worms would organically connect to trunk tunnels, and trunk worms could merge with other trunks.

**Open questions requiring analysis before implementation:**

- **Performance:** The worm mask is a `NativeBitArray` indexed by flattened voxel position. Sampling it at a look-ahead position requires converting world coordinates to local chunk coordinates and checking the correct bit. This is cheap per-query, but the look-ahead position may fall outside the current chunk's worm mask. Cross-chunk mask reads would require either expanding the mask data passed to the job or accepting that out-of-chunk seeks can't detect worms.
- **Order dependence:** Worms within the same chunk are carved sequentially (stack-based DFS), so a later worm can see an earlier worm's mask. But worms in *different* chunks are carved by independent job invocations. A worm in chunk A cannot see chunk B's worm mask unless we add a multi-pass approach or expand the mask data.
- **Interaction with trunk worms:** Should trunk worms seek other trunk worms? This could create mega-networks. Should local worms seek trunks? This is the most valuable case (natural side-passage connections) but requires the trunk mask to be available during local worm evaluation.
- **Config:** Add `wormMaskSeekChance` (0-1) and `wormMaskSeekMinSteps` (int) to `WormNoiseSeeking`. The Biome Editor tool should hide these options for non-worm layers.

**Verdict:** High exploration value, moderate implementation complexity. Defer to Phase 5 (optional enhancements) pending a prototype that measures cross-chunk mask availability and performance cost.

### 3.6 Biome Editor UI Changes

The `WorldGenPreviewWindow.BiomeEditor.cs` "Caves & Lodes" sub-tab (`DrawBeCavesLodesSubTab()`) currently renders cave zone config, the isolation filter, and the cave layers array via Unity's default `PropertyField`. Most new fields on `StandardCaveLayer` will appear automatically through the array drawer, but several changes require explicit UI work.

**Phase 1 — No BiomeEditor changes needed.** Worm shape fields are grouped in the `WormShape` struct on `StandardCaveLayer.wormShape` and render automatically as a foldout via the default array property drawer. `wormHorizontalBias` remains a standalone field.

**Phase 2 — Per-layer zone attenuation UI:**

- The biome-level `caveZoneAttenuation` field is deprecated but kept for backwards compatibility. The BiomeEditor should show it as read-only with a note pointing to the per-layer field.
- The new per-layer `zoneAttenuation` field renders automatically via the array drawer. Consider adding a help note in the "Cave Zone Attenuation" section explaining that attenuation is now per-layer.

**Phase 3 — Trunk modifier fields and conditional layer UI:**

- Add a new "Trunk Worm Modifiers" subsection to `DrawBeCavesLodesSubTab()` showing:
    - `trunkSpawnSuppression` (slider 0-1)
    - `trunkVerticalBiasOverride` (float field, -1 = disabled)
    - Brief help text explaining that trunk worm config is world-level and these are biome-local modifiers.
- The `WormNoiseSeeking` struct fields render automatically via the array drawer.
- **Conditional field visibility:** `isSeekableByTrunkWorms` and `isSeekableByLocalWorms` should be hidden when the layer's `mode` is `CaveMode.WormCarver` (worm layers don't produce noise fields — these flags don't apply). This requires either:
    - A custom `PropertyDrawer` for `StandardCaveLayer` that checks `mode` before drawing seekability fields, or
    - Custom drawing logic in `DrawBeCavesLodesSubTab()` that replaces the default `PropertyField` for the `caveLayers` array with per-element conditional rendering.
    - Recommendation: custom `PropertyDrawer` is cleaner and works in both the BiomeEditor and the raw Inspector.
- If worm-to-worm mask seeking (Section 3.5.3) is implemented later, the `wormMaskSeekChance` and `wormMaskSeekMinSteps` fields should only be visible for WormCarver-type layers (inverse of the seekability flag visibility).

---

## 4. Current System Inventory

### 4.1 Evaluation Paths (Must Stay in Sync)

Cave generation logic is evaluated in four independent code paths. Any formula change must be applied to all four:

| Path                     | File                                    | Purpose                                            |
|--------------------------|-----------------------------------------|----------------------------------------------------|
| **Burst Job**            | `StandardChunkGenerationJob.cs`         | Runtime chunk generation (Burst-compiled)          |
| **Main-Thread Fallback** | `StandardChunkGenerator.GetVoxel()`     | Spawn-point lookup (non-Burst)                     |
| **Editor Preview**       | `WorldGenPreviewWindow.CrossSection.cs` | Cross-section visualizer                           |
| **Worm Seek**            | `StandardWormCarverJob.cs`              | Noise-seeking evaluation for worm steering (Burst) |

> [!NOTE]
> The **Worm Seek** path only evaluates Spaghetti and Noodle noise for steering decisions — it does not carve voxels. A formula drift here won't produce incorrect terrain, but worms will seek toward phantom features (or miss real ones), leading to tunnels that dead-end into solid rock instead of connecting to open caves. See [WORLD_GENERATION_BUGS.md](../Bugs/WORLD_GENERATION_BUGS.md) for the tracking entry.

### 4.2 Key Formulas

**Zone threshold boost** (currently biome-level, proposed per-layer):

```
boost = (1 - zoneNoise) * 0.5 * attenuation
zoneBoostedThreshold = baseThreshold + boost
```

**Effective threshold with depth fade:**

```
effectiveThreshold = zoneBoostedThreshold + (1 - depthFade) * (1 - zoneBoostedThreshold)
```

This is the canonical form used in all three code paths. It is mathematically equivalent to the simplified form `1 - depthFade * (1 - zoneBoostedThreshold)` (used in the cave-tuning skill reference), but the expanded form is preferred in this doc because it matches the code directly.

At `depthFade = 0` (boundary): `effectiveThreshold = 1.0` (fully suppressed).
At `depthFade = 1` (inside range): `effectiveThreshold = zoneBoostedThreshold` (normal carving).

**Noodle carve value** (smoothed isoband):

```
noiseVal = 1.0 - (sqrt(raw^2 + 0.0036) - 0.06)
```

Peak at `raw = 0`: exactly `1.0`. Minimum at `|raw| = 1`: approximately `0.058`.

**The 0.99f threshold cap** was removed from all three evaluation paths. If reintroduced, noodle caves will leak through at every zero-crossing (peak = 1.0 > 0.99).

### 4.3 CaveDensityAnalyzer

Editor tool at `Assets/Editor/Dev/CaveDensityAnalyzer.cs`. Cross-chunk-aware with union-find boundary merging.

**Known limitation 1 — Standalone biomes:** Cannot evaluate biomes not in the `WorldTypeDefinition`. The `StandardChunkGenerator.Initialize()` loads biomes exclusively from the WorldType array and finds the force index via reference equality. A biome loaded independently from `AssetDatabase` will never match, defaulting to biome index 0.

**Fix (deferred):** Accept the biome object in `Initialize()` and, if no reference match is found, temporarily replace the array entry at `_forceBiomeIndex` with a copy whose cave config is overridden from the passed biome.

**Known limitation 2 — Trunk worms:** The analyzer currently only evaluates per-biome cave layers. Once world-level trunk worms are implemented (Phase 3), single-biome analysis mode will not capture trunk worm contributions — trunk worms use a separate world-level scatter grid and config that the analyzer doesn't know about. Multi-biome mode may partially capture them if the `StandardChunkGenerator` is updated to include trunk layer evaluation, but single-biome analysis will underreport total cave density and network connectivity.

**Fix (deferred to Phase 3 or later):** Update `EditorChunkPipelineRunner` and/or `CaveDensityAnalyzer` to include trunk worm layer evaluation. The analyzer should report trunk and local cave contributions separately so tuning can distinguish world-level vs biome-level effects.

**Known limitation 3 — Single-seed instability:** Worm-based cave systems are far more seed-sensitive than noise-based systems. A single worm spawn/no-spawn decision can swing density and connectivity by 2-4x (observed during Phase 4 tuning: seed 42 showed 79% connectivity for Grasslands at spawn=0.05, while seed 1337 showed 50%). Single-seed analysis produces misleading results that cannot reliably distinguish "config is wrong" from "seed is unlucky."

**Fix:** Add a multi-seed averaging mode. Run the analysis across 3-5 seeds and report mean ± standard deviation for key metrics (density, empty chunks, connectivity, max span). The UI should offer a "Multi-seed (N seeds)" toggle next to the existing seed field. The static API should accept an optional `int seedCount` parameter that defaults to 1 for backwards compatibility.

**Known limitation 4 — No per-layer-type breakdown:** The analyzer reports total cave air but does not distinguish contributions from Worm Carver, Cheese, and Noodle layers. During Phase 4 tuning, it was impossible to determine how much of Desert's density came from the new worm layer vs existing Cheese vs Noodle. This makes per-layer tuning a guessing game — adjusting worm spawn rates without knowing whether the observed density change came from worms or from coincidentally co-located Cheese chambers.

**Fix:** Run the analysis pipeline multiple times with individual layers enabled/disabled (via `GenerationFeatureFlags` or by temporarily zeroing layer configs), then diff the results. Report a per-layer-type breakdown table:

```
Layer Breakdown:
  Worm Carver:  4 210 blocks (42.1%)  — 14 chunks affected
  Cheese:       5 102 blocks (51.0%)  — 31 chunks affected
  Noodle:         688 blocks ( 6.9%)  — 18 chunks affected
```

**Known limitation 5 — No worm-specific diagnostics:** Since worms are now the primary cave generator for all biomes, the analyzer lacks visibility into worm behavior. During Phase 4, there was no way to verify whether worm spawn rates, branching, and noise seeking were producing the expected results. Key missing metrics:

- **Worm spawn count:** How many worms actually spawned in the grid vs how many chunks passed the spawn threshold. Validates that `wormSpawnChance` is calibrated correctly.
- **Actual worm length distribution:** Average and histogram of steps taken vs configured `minLength`/`maxLength`. Detects early termination (worms hitting chunk boundaries) or unexpected extensions (noise seeking adding too many steps).
- **Branch count and depth distribution:** How many branches were created per root worm, and at what depth. Validates `WormBranching.branchChance` and `WormBranching.maxBranchDepth` tuning.
- **Individual worm chunk span:** How many chunks each individual worm passes through (separate from the union-find merged network span, which conflates "one long worm" with "many short worms that happened to intersect").
- **Seek success rate:** How often noise seeking found a target vs fired and found nothing. A low success rate indicates seekable layers are too sparse or seek distance is too short.

**Fix:** Instrument the `StandardWormCarverJob` (or an editor-only variant) to output per-worm telemetry into a `NativeList<WormTelemetry>` struct. The analyzer would collect and aggregate these across all chunks. This requires either a debug-only job variant or a compile-time telemetry toggle (to avoid performance cost in production generation).

**Known limitation 6 — No cheese-worm connection rate:** The purpose of noise seeking is to connect worm tunnels to cheese chambers. The analyzer does not measure how many cheese pockets are actually reachable from a worm tunnel vs isolated. This is the most important quality metric for validating that the seek system works — without it, worms could be wandering randomly without connecting to anything, and the analyzer wouldn't detect it.

**Fix:** After the union-find merge pass, classify each network by which layer types contributed to it. A network containing both worm-carved and cheese-carved blocks is a "connected" network; a cheese-only network is "isolated." Report:

```
Cheese-Worm Connectivity:
  Total cheese pockets:           31
  Connected to worm tunnels:      18 (58.1%)
  Isolated (cheese-only):         13 (41.9%)
```

**Known limitation 7 — No network topology metrics:** The analyzer treats networks as blobs (size + chunk span) but does not report internal structure. A branching worm network and a swiss-cheese blob of the same size produce identical stats but have completely different exploration value. Missing topology metrics:

- **Dead-end ratio:** Fraction of network endpoints that don't connect to another passage. High dead-end ratio = more exploration decisions ("should I go down this side tunnel?").
- **Junction count:** Blocks where 3+ passages meet — these are exploration decision points.
- **Longest path through network:** The maximum distance a player can travel without backtracking. Measures exploration depth.

**Fix:** After flood fill, run a graph analysis pass on the network's block connectivity. Identify junction blocks (3+ air neighbors in non-coplanar directions) and dead-end blocks (1 air neighbor). Compute longest path via BFS from each dead-end. Report per-network and aggregate topology stats.

**Known limitation 8 — No horizontal/vertical tunnel characterization:** The analyzer reports network Y-span but cannot distinguish horizontal tunnels from vertical shafts. During Phase 4, `horizontalBias` was tuned from 0.5 to 0.2 for Mountain to encourage vertical shafts, but the only validation was the Y-span metric (which measures extent, not direction). A strongly horizontal tunnel that happens to descend gradually over many chunks can produce the same Y-span as a vertical shaft.

**Fix:** For each cave air block, classify the local passage direction by examining the distribution of air neighbors. A block surrounded by air primarily on the X/Z plane is in a horizontal passage; a block with air primarily above/below is in a vertical shaft. Report the horizontal/vertical ratio per network.

### 4.4 Current Biome Cave Configs (as of 2026-05-28, Phase 4)

```
Grasslands:       Worm(spawn=5%, r=2-3.5, hBias=0.6, len=40-120, branch=0.04, depth=2, seek=10/10/0.5) + Cheese(0.82, h5-40)
Forrest:          Worm(spawn=5%, r=1.5-3, hBias=0.55, len=40-100, branch=0.06, depth=2, seek=10/10/0.5) + Cheese(0.82, h5-40)
Desert:           Worm(spawn=2.5%, r=2.5-4.5, hBias=0.65, len=30-80, branch=0.02, depth=1, seek=12/12/0.4) + Cheese(0.84, h5-40) + Noodle(0.92, h8-58, warp, zoneAtt=0.38)
Mountain:         Worm(spawn=1.5%, r=2-4, hBias=0.2, len=50-150, branch=0.05, depth=2, seek=10/10/0.5) + Cheese(0.80, h5-40) + Noodle(0.93, h15-85, warp, zoneAtt=0.28)
Steep Grasslands: Worm(spawn=5%, r=2-3.5, hBias=0.5, len=35-100, branch=0.04, depth=2, seek=10/10/0.5) + Cheese(0.80, h5-40)

Trunk modifiers:  Desert trunkSpawnSuppression=0.5 | Mountain trunkVerticalBiasOverride=0.3 | Others: defaults
```

**Phase 4 CaveDensityAnalyzer results** (seed 42, 8x8 grid, single-biome mode — excludes trunk worms):

| Biome            | Density | Empty chunks | Connectivity | Max span | Shape quality |
|------------------|---------|--------------|--------------|----------|---------------|
| Grasslands       | 5.87%   | 20.3%        | 78.9%        | 38       | 83.6%         |
| Steep Grasslands | 5.56%   | 32.8%        | 81.2%        | 33       | 83.4%         |
| Forrest          | 3.85%   | 34.4%        | 69.8%        | 25       | 81.3%         |
| Desert           | 1.98%   | 54.7%        | 41.1%        | 7        | 75.3%         |
| Mountain         | 2.43%   | 10.9%        | 13.3%        | 8        | 68.6%         |

> [!NOTE]
> Connectivity and max-span exceed the original Phase 4 targets (10-50% / 3-15). This is inherent to worm-based systems: independently-spawned worms that spatially intersect are merged into larger networks by union-find. Each individual worm spans only 3-5 chunks, but random intersections create 20-40 chunk connected networks. Seed 1337 cross-check shows 50% connectivity / 22 span for Grasslands — these metrics are highly seed-dependent. The original targets were calibrated for Noodle-based systems where connectivity = homogeneity; for worm-based
> systems, higher connectivity = more interesting exploration flow.
>
> Desert's high empty-chunk rate (54.7%) and Mountain's low rate (10.9%) are intentional per Section 3.3. Trunk worms (not captured by the analyzer) will further reduce empty-chunk rates for all biomes.

---

## 5. Implementation Plan

### Phase 1: Worm Carver Improvements

1. Add `wormHorizontalBias` field and `WormShape wormShape` struct (containing `radiusMin`, `radiusMax`, `radiusWaveCount`, `squashAxis`, `squashFactor`, `radiusNoiseStrength`, `radiusNoiseFrequency`) to `StandardCaveLayer`.
2. Add corresponding flat fields to `StandardCaveLayerJobData`.
3. Implement horizontal bias (pitch lerp toward zero) in `StandardWormCarverJob`.
4. Implement radius variation (sine wave modulation) in `StandardWormCarverJob`.
5. Verify via `CaveDensityAnalyzer`: run Mountain biome before/after, compare network Y-span (should decrease with horizontal bias) and shape quality (should show more open blocks from wider sections).

Note: `StandardChunkGenerator.GetVoxel()` does not need updating for Phase 1 — worm carving is handled entirely in the `StandardWormCarverJob` pre-pass. `GetVoxel()` only evaluates noise-based layers (Cheese, Spaghetti, Noodle).

### Phase 2: Per-Layer Zone Attenuation

1. Add `zoneAttenuation` field to `StandardCaveLayer` (default 0).
2. Add corresponding field to `StandardCaveLayerJobData`.
3. Move boost calculation inside the per-layer loop in all three evaluation paths.
4. Move worm spawn factor to read from layer-level attenuation.
5. Deprecate (but keep for backwards compatibility) `StandardBiomeAttributes.caveZoneAttenuation`.
6. Migrate biome configs: set per-layer attenuation only on Noodle layers, zero everywhere else.
7. Update `WorldGenPreviewWindow.BiomeEditor.cs` UI to show per-layer attenuation (see Section 3.6, Phase 2).

### Phase 3: Trunk Worm Layer

1. Consolidate `wormSeekInterval`, `wormSeekDistance`, `wormSeekChance` into a `WormNoiseSeeking` struct on `StandardCaveLayer` (Section 3.5.1). Update `StandardCaveLayerJobData` with flat equivalents.
2. Design trunk worm config structure (fields: spawn chance, length range, radius range, branch chance, horizontal bias, `WormNoiseSeeking` params).
3. Add trunk config to `WorldTypeDefinition` (or a dedicated `WorldCaveConfig` asset).
4. Add `trunkSpawnSuppression` and `trunkVerticalBiasOverride` fields to `StandardBiomeAttributes`.
5. Add `isSeekableByTrunkWorms` and `isSeekableByLocalWorms` fields to `StandardCaveLayer` (both default `false`). Set both to `true` on Cheese layers for all biomes.
6. Refactor local worm noise seeking in `StandardWormCarverJob`: replace the hardcoded `CaveMode.Cheese || Spaghetti || Noodle` filter with the `isSeekableByLocalWorms` flag. This narrows the current "seek everything" behavior to "seek only flagged layers" (Section 3.5.2).
7. Implement world-level scatter grid for trunk worms in `StandardWormCarverJob` --- separate from the per-biome scatter grid, seeded from the world seed.
8. Implement trunk noise seeking: at each look-ahead step, sample the biome at the look-ahead position, evaluate only `isSeekableByTrunkWorms == true` layers, steer toward the highest carve value.
9. Evaluate trunk layer before per-biome local layers in the job, writing to the same worm mask.
10. Update `WorldGenPreviewWindow.CrossSection.cs` to include trunk layer visualization (note: `GetVoxel()` does not evaluate worm carving — it only handles noise-based layers).
11. Update `WorldGenPreviewWindow.BiomeEditor.cs`: add "Trunk Worm Modifiers" subsection with `trunkSpawnSuppression` and `trunkVerticalBiasOverride` fields. Create a custom `PropertyDrawer` for `StandardCaveLayer` that hides `isSeekableByTrunkWorms` / `isSeekableByLocalWorms` when the layer mode is `WormCarver` (see Section 3.6, Phase 3).
12. Verify via `CaveDensityAnalyzer`: run with trunk layer enabled, check that networks span 20+ chunks and cross biome boundaries. Monitor merge amplification to ensure trunks don't create a single world-spanning supernetwork.

### Phase 4: Biome Reconfiguration

1. Add local Worm Carver layers to all biomes (currently only Mountain has one).
2. Tune local worm parameters per biome (spawn chance, radius, length, branch chance, horizontal bias) --- these are shorter, biome-specific side passages.
3. Tune trunk worm world-level config for good cross-biome connectivity.
4. Set `trunkSpawnSuppression` per biome where appropriate (e.g., Desert = 0.5 for sparser trunk origins).
5. Demote Noodle to accent-only on biomes where it adds value (Mountain, optionally Desert).
6. Remove Noodle from Grasslands, Steep Grasslands, and Forrest.
7. Run `CaveDensityAnalyzer` on each biome after reconfiguration.
8. Iterate until metrics fall within target ranges:
    - Overall density: 1-6%
    - Chunks with no caves: 15-40%
    - Global connectivity: 10-50%
    - Max chunks spanned (local): 3-15
    - Max chunks spanned (trunk): 20-40
    - Shape quality (open blocks): >25%

### Phase 5: Optional Enhancements

- ~~**Ellipsoidal carving** for wider-than-tall worm profiles.~~ (Implemented --- see Section 3.1.3)
- ~~**Worm radius noise** (Perlin-modulated radius instead of sine wave) for less predictable width variation.~~ (Implemented --- see Section 3.1.2)
- ~~**Worm Y-level attraction** (tendency to carve toward specific Y bands, e.g., diamond-level in Minecraft terms).~~ (Implemented --- see Section 3.1.4)
- **Spaghetti revival** --- if the 2D repetition problem can be solved (3D noise pairs, per-axis domain warp), Spaghetti could return as an alternative connectivity generator.
- **CaveDensityAnalyzer fix** for non-WorldType biomes and trunk worm support (see Section 4.3 known limitations).
- ~~**Trunk traversal blocking** --- `trunkTraversalAllowed` flag per biome to terminate trunks entering biomes where underground caves make no sense (e.g., ocean).~~ (Implemented --- see Section 3.4.6)
- **Worm-to-worm mask seeking** --- Allow worms to seek toward already-carved worm tunnels for more reliable trunk-local connections. See Section 3.5.3 for full analysis.

---

## 6. Open Questions

1. **Should Spaghetti be deprecated or kept dormant?** No biome currently uses it. Keeping the code has no runtime cost, and it could be revived if the repetition problem is solved. Recommend: keep but mark as experimental.

2. **Worm carver performance at scale.** The scatter approach checks `(2 * chunkSearchRadius + 1)^2` neighboring chunks. With `maxLength=200` and `radius=3`, that's a 13-chunk search radius per worm layer. This is already capped at 8. As worm length or radius increases, monitor the search radius and consider tighter caps or spatial hashing.

3. **Cave isolation filter interaction.** The `minCavePocketSize` filter (currently 12 blocks) runs after all carving. With worm carvers producing long connected tunnels, the filter will rarely trigger. It's most useful for cleaning up tiny cheese pockets. Consider whether the filter threshold should vary per biome or per generator.

4. **Cross-biome cave transitions.** Addressed by the Trunk + Local architecture (Section 3.4). Trunk worms use stable world-level config as they cross biomes. `trunkVerticalBiasOverride` is applied per-step as the worm crosses biome boundaries, providing natural vertical behavior transitions without explicit blending (see Section 3.4.2). Local worms are short enough that cross-biome bleed is minor. Remaining question: does per-step biome lookup for the override add meaningful cost to the trunk worm job? Profile during Phase 3 implementation.

5. **Worm-to-worm interaction.** Partially addressed by the Trunk + Local architecture (Section 3.4). The two-tier separation reduces the coordination problem: trunk-to-trunk overlap is rare (sparse world-level spawn grid), and local-to-local overlap is acceptable (short worms, small overlap zones). Trunk-to-local connections happen indirectly when both worm tiers seek toward the same cheese chambers (Section 3.4.3), not by detecting each other's carved tunnels. For more reliable connections, worm-to-worm mask seeking is proposed as a future
   enhancement (Section 3.5.3). Monitor merge amplification after Phase 4 biome reconfiguration. If trunk+local merging produces single supernetworks spanning >50 chunks, consider reducing trunk spawn chance or local noise-seeking distance.

6. **Fluid placement in worm tunnels.** Lava and water are placed based on Y-level thresholds in carved air. Worm carvers create long connected tunnels, so a single fluid source block can propagate much farther than in an isolated cheese pocket. A lava lake in a worm tunnel could flood hundreds of blocks along the tunnel's length. This may be desirable (dramatic lava rivers) or problematic (performance cost of fluid simulation over long distances, player frustration). Monitor fluid behavior in worm-heavy biomes after Phase 4 and consider whether fluid
   source placement should account for tunnel connectivity or length.

7. **Ore vein visibility.** Worm carvers expose significantly more wall surface area per unit of air volume compared to cheese chambers (long tunnels have high surface-to-volume ratio vs spherical rooms). This means players encounter more exposed ore veins per cave, which affects game balance --- caves become more rewarding to explore but may reduce the need for branch mining. This is a game design consideration, not a code change. Document as a known consequence and evaluate after playtesting.

8. **Performance budget.** Open Question #2 notes the scatter search radius concern, but there is no concrete performance target. Key questions: How many worm evaluations per chunk are acceptable before the generation job exceeds its frame budget? What is the current generation time per chunk with vs without a worm layer? The Mountain biome already has a worm layer and can serve as the baseline. Before Phase 4 (adding worms to all biomes), profile generation time with the Unity Profiler tools and establish a per-chunk generation time ceiling (e.g., <2ms
   on target hardware). If adding worm layers to all biomes pushes past this ceiling, consider: reducing `maxWorms`, lowering `wormSpawnChance`, or tightening `chunkSearchRadius`.

9. **Save compatibility.** The Standard World generation system is still heavy WIP, so save migration is not required for these changes. Already-generated chunks in saved worlds will have different cave layouts than newly generated chunks at biome boundaries after config changes --- this is accepted as a known artifact during the WIP phase. When the generation system stabilizes for release, a world version bump and regeneration pass should be considered.
