# Sound Engine Design

**Version:** 1.6  
**Date:** 2026-08-29  
**Status:** **Partially implemented — S0, S1 and S2's runtime shipped.** The `SoundMaterial`
channel, the shared `BlockSoundDatabase`, the BlockEditor dropdown and prefill, the volume settings, the
pooled one-shot voices and the break / place / footstep triggers all exist; the `AudioMixer` is authored
with its seven exposed volume parameters; two CC0 packs supply content, so all 13 sounding materials have
break and step clips. Footsteps sample two cells, so wading and cross-mesh flora sound (§5.1). The
`Validate Sound Engine` suite guards the resolution chain and the ambience decisions (31 baselines).
**S2's runtime shipped on 2026-08-29** — `AudioContext`, the `AmbienceResolution` decision layer, the
`AmbienceDirector` bed pair with its cave layer, the `MusicScheduler` and the underwater low-pass — on top of
the §6.2 managed biome query, which shipped the same day and is guarded by its own `Validate Biome Selection`
suite (15 baselines). **Ambience content is in (§9): six CC0 loops cover the cave bed, the fallback bed and
four of the six biomes.** Music has no content yet, so the scheduler runs and picks nothing. S3 and the
remainder of S4 are still outstanding.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> Design for the VoxelEngine's audio system: block sounds (break / place / step), fluid and
> ambient loop emitters, world-layer ambience & music, and the mixer/settings plumbing that ties
> them together. The core data-model decision — **a dedicated per-block `SoundMaterial` channel
> instead of reusing `BlockTags`** — is settled in §3; the rest of the document layers the runtime
> on top of existing project patterns (ScriptableObject databases, pooling,
> Burst-job-produces / main-thread-consumes).
>
>
> Status: **S0 + S1 shipped** (2026-08-28), S4's two-cell footstep sampling and **S2** — runtime and
> ambience content — (2026-08-29); S2's *music* content, S3 and the rest of S4 outstanding. Section 2's "current state" table describes
> the project *before* that work — it is kept as the historical audit it was written as.

**Audited:** 2026-07-03, at commit `2dde457` (branch `main`).
Findings are from static review of `BlockType` / `BlockDatabase` / `BlockTagPreset`,
`PlacementRules.cs` (the `BlockTags` enum and `VoxelModSource`), `PlayerInteraction` /
`PlacementController`, `BlockTypeJobData`, the fluid tick path (`FluidTickJob`, TG-4), and the
biome data model (`BiomeBase` / `StandardBiomeAttributes`, `BiomeBlender`).

**Relationship to other documents:**

- [`../Architecture/DATA_STRUCTURES.md`](../Architecture/DATA_STRUCTURES.md) — the packed-`uint`
  voxel model this design must not violate: sound data lives on `BlockType` (per block *type*),
  never per voxel.
- [`../Architecture/DATA_DRIVEN_SETTINGS_UI.md`](../Architecture/DATA_DRIVEN_SETTINGS_UI.md) —
  where the §5.4 volume sliders surface as settings.
- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  — RF-1 (day/night) and RF-7 (weather) are *future inputs* to the §6 ambience context; this
  design depends on neither.
- [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) — TF-3's
  climate axes would eventually refine biome ambience selection.
- [`../Guides/GENERAL_OPTIMIZATION_GUIDE.md`](../Guides/GENERAL_OPTIMIZATION_GUIDE.md) — pooling
  and zero-GC rules the runtime layers follow.

---

## 1. Goals & non-goals

### Goals

1. **Block sounds** — break, place, footstep (and later hit/mining-progress) sounds per block
   material, played **positionally** (3D spatialized at the voxel).
2. **Fluid sounds** — looping flow/waterfall/lava emitters near the listener, fully decoupled
   from the Burst fluid simulation.
3. **World-layer sounds** — biome ambience beds, cave ambience, music scheduling; designed so
   time-of-day (RF-1) and weather (RF-7) plug in later without restructuring.
4. **One mixer + settings surface** — per-category volume control through the existing
   data-driven settings UI.

### Non-goals (v1)

- No per-voxel audio state of any kind (violates the packed-`uint` architecture).
- No audio triggered *from inside* Burst jobs — jobs may only *produce data* that the main
  thread consumes (§5.2).
- No third-party audio middleware (FMOD/Wwise). Unity's built-in `AudioSource` + `AudioMixer`
  stack is sufficient at this scope and keeps the lean package set intact.
- No mob/entity sounds — there are no mobs yet; the one-shot layer (§5.1) is where they will
  hook in when they exist.
- No occlusion/reverb-zone simulation (sounds through walls). Planned as a **v2 extension**, with
  true reflection (Steam Audio) as v3+ — see the §8 extension roadmap.

---

## 2. Current state (what exists today)

| Area             | State                                                                                                                                                                                                                                                                                                                                                                 |
|------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Audio code       | **None** at audit time. No `AudioSource`/`AudioClip`/mixer usage anywhere in `Assets/Scripts/`.                                                                                                                                                                                                                                                                                     |
| Block data       | `BlockType` (serializable class) inside `BlockDatabase.asset`, authored via the BlockEditor window; `BlockTagPreset` assets as authoring helpers; `BlockIDs` auto-generated constants.                                                                                                                                                                                |
| Tags             | `BlockTags : uint` — 17 flags used of 32. Material flags (`SOIL`, `WOOD`, `PLANT`, `LEAVES`, `ROCK`, `MINERAL`, `ORGANIC`) carry a comment "for tools, sounds, interactions" but were never consumed by audio. The recent worldGen/placement split affected only the two `canReplaceTags` masks; the base `tags` mask is shared by placement, fluids, and raycasting. |
| Break/place path | `PlayerInteraction` → `World.AddModification(VoxelMod)` (`World.cs:1807`), with `VoxelModSource.Live` vs `WorldGen` already distinguishing player edits from generation.                                                                                                                                                                                              |
| Footsteps        | No hook, but `World.GetVoxelState` makes "block under feet" a trivial query.                                                                                                                                                                                                                                                                                          |
| Fluids           | `FluidTickJob` — Burst, worker thread. **Cannot touch managed audio.**                                                                                                                                                                                                                                                                                                |
| Biomes           | `StandardBiomeAttributes : BiomeBase` ScriptableObjects. At audit time biome-at-position was computed inside Burst worldgen jobs, with no purpose-built managed query — §6.2 made one a prerequisite. **Shipped 2026-08-29** (§6.2).                                                                                                                     |
| Sky light        | Per-voxel sky light is queryable at the listener — a free "how underground am I" signal for cave ambience (§6.1).                                                                                                                                                                                                                                                     |
| Pooling          | `Helpers/DynamicPool<T>` / `ConcurrentDynamicPool<T>` — the pooled `AudioSource` set (§5.1) follows these conventions.                                                                                                                                                                                                                                                |

---

## 3. Decision: how blocks map to sounds

The pivotal data-model decision. Three options were evaluated:

### Option A — derive sounds from `BlockTags` (rejected)

Map tag bits → sound sets at runtime (`WOOD` → wood sounds, …).

- ✅ Zero new authoring; every block already has tags.
- ❌ **Tags are a bitmask; sounds need exactly one answer.** Grass is `SOIL | ORGANIC`, leaves
  are `PLANT | LEAVES | ORGANIC` — a priority-ordered resolver is required, and every new block
  risks silently falling through to the wrong branch.
- ❌ **Wrong granularity.** `SOIL` covers dirt, sand, and gravel — which sound very different
  (Minecraft gives each its own sound group). Glass, ice, wool, and metal have *no*
  distinguishing tag at all, so new tags would be needed anyway.
- ❌ **Couples audio to gameplay semantics.** The `canReplaceTags` field was recently split
  precisely because one field served two masters; retuning a tag for placement or fluid behavior
  would silently change sounds — the same design smell again.
- ❌ Bit budget: 17 of 32 `uint` bits used; sound variants would burn several more for zero
  gameplay value.

### Option B — dedicated `soundMaterial` enum + shared sound database ✅ **CHOSEN**

One new `byte`-backed enum field on `BlockType` indexing into a shared `BlockSoundDatabase`
ScriptableObject (§4). This is Minecraft's proven model (`SoundType` per block): ~10–15 shared
groups cover hundreds of blocks, sounds are tuned in one place, every block gives an unambiguous
single answer, and audio is fully decoupled from gameplay tags. Tags remain useful as an
**editor-time authoring hint** (§4.5) — never a runtime source.

### Option C — `AudioClip` arrays directly on each `BlockType` (rejected)

Maximum flexibility, but massive duplication (dirt/grass/farmland share sounds), bloats
`BlockDatabase.asset` with dozens of clip references per block, and retuning "all stone-ish
sounds" means touching every block.

---

## 4. Data model

### 4.1 `SoundMaterial` enum

`Assets/Scripts/Data/SoundMaterial.cs`:

```csharp
/// <summary>
/// The sound group a block resolves to for break / place / step events. Indexes into
/// <see cref="BlockSoundDatabase"/>. One value per block — deliberately independent of
/// <see cref="BlockTags"/> (see SOUND_ENGINE_DESIGN.md §3).
/// </summary>
public enum SoundMaterial : byte
{
    None = 0,   // silent (Air, Barrier-like debug blocks)
    Stone,      // stone, cobble, ores, bricks
    Dirt,       // dirt, farmland, mud
    Grass,      // grass block top-feel, podzol
    Sand,
    Gravel,
    Wood,       // logs, planks, crafted wood
    Leaves,     // leaves, bushes
    Plant,      // small flora: flowers, saplings, grass blades, crops
    Glass,      // glass, ice (split Ice out later if it needs distinct clips)
    Wool,
    Metal,
    Liquid,     // bucket-style place/remove; NOT the flow loops (§5.2)
    Snow,
}
```

Start with exactly the values the current block palette needs; the enum is trivially extensible
(values are serialized by number — **append only, never reorder**, same discipline as every other
serialized enum in the project).

### 4.2 `BlockSoundGroup` + `BlockSoundDatabase`

`Assets/Scripts/Data/BlockSoundGroup.cs` / `BlockSoundDatabase.cs`:

```csharp
[Serializable]
public class BlockSoundGroup
{
    public AudioClip[] breakClips;   // random pick per event
    public AudioClip[] placeClips;   // empty ⇒ fall back to breakClips (MC does the same)
    public AudioClip[] stepClips;
    public AudioClip[] hitClips;     // punching / mining progress (future; may stay empty in v1)
    [Range(0f, 1f)] public float volume = 1f;
    public float pitchMin = 0.9f;
    public float pitchMax = 1.1f;
}

[CreateAssetMenu(fileName = "BlockSoundDatabase", menuName = "Minecraft/Block Sound Database")]
public class BlockSoundDatabase : ScriptableObject
{
    [Tooltip("Indexed by (byte)SoundMaterial — keep in enum order.")]
    [SerializeField] private BlockSoundGroup[] _groups;

    public BlockSoundGroup Get(SoundMaterial material) => _groups[(byte)material];
}
```

A custom inspector (or a light `OnValidate`) should pin `_groups.Length` to the enum length and
label each element with its enum name, so authoring stays index-safe. Follows the
`BlockDatabase.asset` pattern: one project-level asset, referenced by the `SoundManager`.

### 4.3 `BlockType` field

One addition, in the vein of the existing headers:

```csharp
[Header("Sound")]
[Tooltip("Which sound group this block uses for break/place/step. Independent of tags.")]
public SoundMaterial soundMaterial;
```

- **No `BlockTypeJobData` mirror.** Audio is entirely managed-side; every trigger site (§5)
  resolves the block ID → `BlockType` on the main thread. If a Burst consumer ever appears, the
  `byte` copies over trivially — but don't add it speculatively.
- **No save-format impact.** `soundMaterial` is a property of the block *type* (asset data), not
  of stored voxels — nothing on disk changes. Seed-safe, save-safe.

### 4.4 `BlockTagPreset` default

`BlockTagPreset` gains a `public SoundMaterial soundMaterial;` field so applying a preset in the
BlockEditor also sets the sound group — presets remain the "configure a block in one click"
workflow helper.

### 4.5 BlockEditor integration & prefill

- BlockEditor window: one enum dropdown in the block form (next to the tag fields), plus the
  preset copy-down in 4.4.
- **One-time prefill** for the existing database: an editor utility
  (`Minecraft Clone/Dev/Audio/Prefill Sound Materials`) that suggests a `SoundMaterial` from existing
  data — tag heuristic (`ROCK|MINERAL → Stone`, `WOOD → Wood`, `LEAVES → Leaves`,
  `PLANT → Plant`, `SOIL → Dirt`, `LIQUID → Liquid`, name-based overrides for sand/gravel/glass
  where tags are too coarse) — writes it into `BlockDatabase.asset`, and logs every assignment
  for manual review. Tags seed the value **once at author time**; the runtime never consults tags
  for audio.

---

## 5. Runtime architecture

One `SoundManager` (scene singleton alongside `World`, owning the mixer reference, the databases,
and the pools) with four independent layers. They differ in *how sounds are triggered* — which is
where all the real constraints live.

```
                        ┌───────────────────────────────┐
                        │          SoundManager         │
                        │  mixer · databases · pools    │
                        └──┬─────────┬────────┬─────────┘
     one-shot events ──────┘         │        └────── context snapshot (1/s)
   (break/place/step)         emitter scan              (biome, skylight, …)
           │                  (0.5–1 s, Burst)                  │
   L1: pooled 3D            L2: pooled looping          L3: 2D ambience beds
   AudioSources             3D AudioSources                 + music scheduler
           └─────────────────────┴──────────────────────────────┘
                                  L4: AudioMixer groups → settings UI
```

### 5.1 Layer 1 — positional one-shots (break / place / step)

**Pool.** ~16–32 pooled 3D `AudioSource`s (one prefab: `spatialBlend = 1`, `dopplerLevel = 0`,
logarithmic rolloff, `maxDistance` ~16–24 blocks) managed with the `DynamicPool<T>` conventions:
fetch → position at voxel center → set clip / volume / **randomized pitch** → play → auto-return
when done. A single shared `PlayOneShot` source is explicitly ruled out: it loses per-event pitch
jitter and spatial position, and the Minecraft sound feel depends heavily on pitch jitter.
When the pool is exhausted, steal the oldest playing source (voice limiting — never grow
unboundedly, never skip the newest event: the block the player just broke must always sound).

**API.**

```csharp
public void PlayBlockSound(SoundMaterial material, BlockSoundEvent evt, Vector3 worldPos);
// evt ∈ { Break, Place, Step, Hit }
```

**Break/place hook — v1:** directly in `PlayerInteraction` at the two `AddModification` call
sites (destroy → the *removed* block's material; place → the *placed* block's material). This
gives immediate, reliable feedback with zero pipeline coupling.

**Break/place hook — v2 (when block behaviors need audio):** move the trigger to the `VoxelMod`
*apply* site, filtered to `VoxelModSource.Live` — behavior-driven changes (gravity blocks
landing, grass spreading) then sound automatically, and WorldGen mods stay silent for free.
Guard against replayed-save mods and off-screen behavior storms with a per-frame event budget and
a listener-distance cull *at the trigger site* (cheaper than instantiating a source that nobody
can hear). v1 ships without this; the API above is already shaped for it.

**Footsteps:** in the player controller — accumulate horizontal distance while grounded; every
~1.5 blocks traveled, read the two cells at the feet (`TryGetVoxel` at `floor(position)` and one below),
resolve their materials and play them layered (see the two-cell note below). Jump-land plays
an immediate step (slightly louder) and resets the accumulator.

> **Two-cell sampling** (`S4`, shipped 2026-08-29). A step reads **two** voxels rather than one:
> `SoundResolution.StepCells` selects the cell the player occupies (`floor(feetY)`) and the cell supporting
> them (one below), and `SoundResolution.ResolveStepMaterials` returns **both layers** — the supporting
> block always, plus a non-solid occupant played *over* it. Wading is a `Liquid` splash on top of the
> riverbed's `Sand`/`Dirt`, not instead of it; walking through cross-mesh flora layers `Plant` over the
> ground. The two one-shots each take their own voice and event salt, so they get independent clips and
> pitch rather than flanging, and `PlayerFootsteps` scales the occupant layer by a serialized
> `_occupantLayerVolume` (default 0.9).
>
> Layering rather than a winner-takes-all priority: sounding only the occupant severs the step from the
> ground the player is actually walking on, which reads as a disconnect the moment the two materials differ
> — the same reason Minecraft plays a splash *alongside* the footfall rather than replacing it.
>
> Three cases add no second layer: a *solid* occupant, a silent one, and one whose material already matches
> the support (two voices of one material flange rather than layer). An unloaded or out-of-world cell reads as
> air, so a missing occupant never silences a known supporting block.
>
> **Sub-voxel support** (fixed and confirmed in game 2026-08-29). The original rule mis-stated why a solid occupant is skipped —
> it claimed such a cell could only be occupied by standing on a half slab, "which would layer a footfall over
> itself". That was wrong, and it hid a real bug: the support cell is always *one below* the occupied one, so
> standing on a slab sounded whatever the slab was placed on. A stone slab over dirt played **dirt**.
> `SoundResolution.OccupantCarriesFeet` now asks the shared `BlockCollisionBoundsUtility.GetBounds` — the same
> sub-voxel resolver the physics solver and the interaction ray read — whether the occupied cell's block has
> its collision surface at the feet, using the solver's own `VoxelRigidbody.GroundProbeSkin` tolerance. When it
> does, `ResolveStep` promotes that block to the support and layers nothing over it. One definition of "what am
> I standing on", shared by the ear and the collision response.
>
> The tolerance is one-sided on purpose: the vertical resolve parks a resting body `COLLISION_EPSILON` *above*
> its surface, so an exact-equality test would never fire in game. This covers any single-AABB partial block
> whose top meets the feet — `BlockCollisionBounds` is one box per block, so a shape needing two (stairs) is
> still approximated by its enclosing volume.
>
> Both halves live in the pure `SoundResolution` layer, which is why the suite can pin them (the two
> `Step ...` baselines); the wiring in `PlayerFootsteps.PlayStep` stays an in-game check.
>
> **Still open:** `Update` returns early whenever `IsGrounded` is false, so swimming produces no footsteps.
> Deliberately deferred rather than fixed here — there is no swimming *mechanic* to sound
> ([`../Bugs/FLUID_BUGS.md`](../Bugs/FLUID_BUGS.md) §02 "No player effect": no buoyancy or swimming
> simulation, fluids are merely non-solid), and strokes would want their own clips distinct from walking and
> wading. `Assets/Scripts/Physics/` still computes **no** liquid contact state at all — but that turned out not to
> block `AudioContext.Submerged` (§5.3): S2 reads the block filling the listener's head cell and asks whether its
> `fluidType` is anything but `None`, the same read-only posture footsteps already take. The prerequisite this
> section recorded was real for the *footstep* case and overstated for the submerged one. What the cell-level read
> costs is precision at the surface: a fluid voxel is only partly filled, so a head just under the waterline reads
> dry until it enters the cell below.

**Directionality** is free: 3D sources + the `AudioListener` on the player camera.

### 5.2 Layer 2 — fluid & ambient loop emitters

The one genuinely hard problem: fluid simulation runs in `FluidTickJob` (Burst, worker thread) —
audio cannot be triggered from it, and per-flow-event one-shots would be spam anyway. The design
is **listener-centric emitter scanning**, fully decoupled from the simulation (this is also what
Minecraft effectively does):

1. **Scan** (every 0.5–1 s): a small Burst `IJob` over the resident `ChunkData` of the ~2-chunk
   radius around the listener, collecting *sound-emitting voxel candidates* into a
   `NativeList<SoundEmitterCandidate>` (`position : int3`, `kind : byte`):
    - flowing water / lava (fluid voxel with level < source level),
    - waterfall columns (falling-fluid flag / vertical flow),
    - future ambient blocks (fire, portals, buzzing ore…) — table-driven off a
      `BlockTypeJobData` predicate so new kinds are data, not code.
      The scan **reads** voxel data only — same read pattern as the meshing gather; it never touches
      the fluid tick. Schedule it alongside other frame jobs and consume the list next frame
      (produce-on-worker / consume-on-main, the standard project pattern).
2. **Cluster** (main thread): greedy distance clustering (~4–6 block radius) of candidates per
   kind. A 20-block waterfall becomes **one** emitter at the centroid, not 20.
3. **Assign** a fixed budget (~4–8) of pooled **looping** 3D sources to the nearest/loudest
   clusters; fade in on appear, fade out on disappear, lerp position when a cluster centroid
   drifts (listener moved, flood advanced). Never hard-cut a loop.

**Performance requirements — by construction, then profiled.** The scan is not a "tune it later"
prototype: it is written to the project's hot-path standards from the start — Burst-compiled,
linear voxel-array iteration (no per-voxel virtual/managed calls), a reused `NativeList` (no
per-scan allocation), early-out on chunks with no fluid sections (**no such flag exists yet** —
`ChunkSection` tracks only `nonAirCount`/`IsEmpty`, so S3 must add one or pick another predicate), and the whole scan off the main thread. Cadence (0.5–1 s) and radius (~2 chunks)
are then tuned against the profiler once the layer exists; the scan is a candidate for the
existing benchmark-harness pattern.

Cost is bounded and independent of fluid activity (the scan volume is constant); the simulation
is untouched. This is the highest-effort layer and ships **last** (§8).

### 5.3 Layer 3 — world-layer ambience & music ✅ *runtime shipped 2026-08-29*

2D (non-spatial) layered sources with slow crossfades, driven by an **`AudioContext`** snapshot
sampled at the listener.

**As shipped.** `AudioContext` is a `readonly struct` carrying `BiomeIndex`, the resolved `BiomeBase`,
`HasBiome`, `SkylightAtHead` and `Submerged`, with the RF-1/RF-7 seats reserved as comments. Four
decisions differ from the sketch above, each for a reason worth keeping:

- **`BiomeIndex` is an `int`, not a `byte`.** The query answers in `int`; a cast at the call site
  would be a truncation waiting for the biome list to grow.
- **The struct carries the biome *asset*, not only its index.** Bed and pool selection read the
  authored clips directly rather than each re-deriving a lookup — the same reason `BiomeSample` does it.
- **`HasBiome` is a field of its own.** The legacy generator's `TryGetBiomeAt` returns false for a whole
  session, and "no biome answer" must select the fallback bed rather than degrade to silence.
- **Sky light is read from the stored *exposure* channel, never `World.GetEffectiveSkylight`.** The
  effective value is time-darkened, so since RF-1 shipped it falls to zero across the whole open surface at
  night — keying the cave bed off it would fade caves in over the entire world every evening. §6.1's claim
  that sky light is a "free, already-correct underground signal" holds only for the exposure channel.

`SoundManager` owns the sampling and publishes `Context` once, rather than each consumer running its own
timer: the beds, the scheduler and the underwater filter have to agree about where the listener is, and
independent timers disagree at exactly the moments that matter — a cave mouth, a shoreline, a biome border.
It holds the `AmbienceDatabase` for the same reason — one slot to wire, so the two consumers cannot end up
half-wired with the beds working and music silently missing its fallback pool.

All of the selection arithmetic lives in the pure `AmbienceResolution` layer, which is why the suite can pin
it (10 `S2` baselines); the audible result stays an in-game judgment.

- **Biome ambience beds — a weighted mix, not a selection:** `BiomeBase` carries `AudioClip ambientLoop`
  and `AudioClip[] musicPool`; `AmbienceDirector` owns a roster of four bed sources, each running **its own
  fade**, and drives every one of them from `BiomeWeights` (§6.2) rather than from a single chosen biome.
  Each contributing biome's bed targets its share of the mix, so standing on a shore keeps the ocean audible
  and quiet under the forest instead of switching between them one block apart. Gain is `√fade`, so two beds
  handing over — holding complementary fades — sum to **constant power**; a linear amplitude pair dips
  audibly at the midpoint.
  <br>
  Three rules the mix resolution applies, each guarding a defect that is inaudible as a bug and obvious as a
  symptom: contributors below a floor are **dropped and the rest renormalized** (otherwise a 2% neighbour
  quietly ducks everything else by 2%); biomes resolving to the **same clip merge onto one source**
  (two sources playing one loop flange rather than layer — the rule `SoundResolution` already applies to a
  footstep's two cells); and a world with **no weighted answer** falls back to a single default bed rather
  than to silence, which is the legacy generator's whole session.
  <br>
  Independent per-source fades rather than one shared crossfade timer, because a paired timer has no answer
  for a change arriving *before the previous handover finished*: whichever source the pair reassigns gets cut
  at whatever gain it happened to hold. With per-source fades that case is ordinary — a bed the listener
  moves back toward is still playing, so its target simply rises again from where it had reached.
  <br>
  **No debounce.** Ambience does not read `BiomeTracker.Current`: a weight moves continuously with the
  listener (measured at ≤0.005 per block), so there is no jump to debounce, and a dwell would only delay a
  change that was never abrupt. The tracker's 3 s hysteresis still serves what it was built for — the biome
  readout and RF-7, where a flickering name is worse than a late one.
- **Rest cycle:** the bed layer alternates audible and silent stretches on randomized durations, so
  ambience has quiet in it rather than running continuously. Layer-wide rather than per bed: the mix already
  varies with the listener's position, and a second independent variation per source reads as randomness
  rather than as the world going quiet. The **cave bed is never gated** — a cave that falls silent reads as
  broken, not restful.
- **Bed level:** `AmbienceDatabase.BedVolume` trims every bed before the category gain. A content trim, not
  a lower default on the Ambient slider, for the same reason `BlockSoundGroup.volume` is one — the pack is
  mastered hot relative to the block one-shots, which is a fact about the clips. A slider default would
  leave 100% meaning "too loud" and would never reach a settings file that already exists.
- **Depth gate:** the biome beds fade out entirely as the listener descends below the terrain surface,
  tapering to silence past an authored depth. Distinct from the cave duck and needed alongside it, because
  sky exposure alone cannot tell a deep cavern from a covered shed — both read zero, so a layer keyed only on
  exposure left the surface bed playing at the cave duck's leftover share far underground. Depth comes from
  `World.TryGetSurfaceHeight`, a free read of the per-column heightmap the lighting system already maintains;
  the stronger of the two ducks applies rather than the two compounding, since in a deep cave the cave bed is
  fading in *because* the listener is deep. The cave duck is **1** — a fully committed cave bed leaves no
  surface bed behind at all. At 0.7 it left 30% playing, which is audible as birdsong in a dark cave and was
  the shape of the original complaint; the depth gate does not reach that case on its own, because a cave
  twelve blocks down is barely into the taper.
- **Cave ambience:** a sustained underground reading fades in a cave bed and ducks the biome bed. The
  test is a **threshold** (`SkylightAtHead <= caveMaxSkylight`, authored at 0) rather than a strict
  `== 0`, so an overhang or a one-block shaft does not disqualify a space that plainly reads as a cave,
  and it rides its own dwell filter so a cave mouth cannot flap the layer.
- **Music scheduler:** deliberately simple — pick a track from the context's pool, play, then wait a
  randomized silence gap; re-resolve the pool at each pick so biome changes influence the *next* track,
  never interrupt the current one. The repeat guard compares the **clip**, not the index it sat at: the pool
  changes with the biome, so an index carried across pools names a different track. It then steps to the
  neighbouring index rather than re-rolling, since a re-roll can repeat — with a two-track pool, half the time.
- **Wind in grass/trees:** v1 = a biome ambient loop whose volume is modulated by listener sky
  exposure (already in the context). An honest per-tree emitter version would be a `LEAVES`
  emitter kind in the §5.2 scan — deferred.

### 5.4 Layer 4 — mixer & settings

One `AudioMixer` asset with groups:

```
Master
├── Music
├── Ambient      (biome/cave beds, wind)
├── Blocks       (break/place/step one-shots)
├── Fluids       (loop emitters)
├── Weather      (reserved — RF-7)
└── UI
```

Exposed volume parameters wired into the data-driven settings UI. **As shipped:** a dedicated
`SettingsTab.Audio` holds the six sliders (the `Group` property on `SettingFieldAttribute` remains only a
proposal), and `AudioVolumes` is the single source of truth for category gain — it drives the mixer when
one is assigned and is applied per source when one is not, so the mixer asset can be authored at any time
without a code change. Sliders map linearly 0–1 → dB via the standard `20 * log10(x)` conversion with a
floor at −80 dB.

**Underwater ✅ shipped, but not as a snapshot.** `AudioContext.Submerged` drives an `AudioLowPassFilter`
on each non-UI source — the one-shot voices, the ambience beds and the music source — swept between a dry and a
wet cutoff over a short fade. A mixer snapshot was the original design and was **not** built: the mixer asset
carries a single snapshot and no effects, authoring one needs editor API the §5.4 setup tool does not cover, and a
per-source filter keeps the whole layer mixer-optional exactly as `AudioVolumes` already is. `SoundManager` owns
the fade and hands the cutoff out through `ApplySubmersionFilter`, so every source muffles together; the filters
are **disabled** while dry rather than parked at a transparent cutoff, so a state the player is almost never in
costs no DSP block per voice. The sweep interpolates in **log space** — a linear ramp from 22 kHz to 900 Hz spends
nearly all its travel in a range the ear cannot distinguish, then slams shut at the end. A snapshot remains a
valid later refactor and would change no calling code.

---

## 6. Prerequisites & integration points

### 6.1 Skylight at the listener

Already queryable per-voxel — no work needed beyond a helper on `SoundManager`.

### 6.2 Managed biome-at-position query ✅ *shipped and confirmed in game 2026-08-29*

> **Correction to the original audit.** This section claimed there was *no* managed biome query at all.
> That overstated the gap: `IChunkGenerator.GetTerrainDebugInfo` → `WorldJobManager.GetTerrainDebugInfo`
> already returned a biome index **and** name on the main thread, and `TerrainGenDebugOverlay` consumed it
> on every column change. What was missing was a *purpose-built* query — the debug path also runs the full
> multi-noise spline blend to produce diagnostics no audio consumer wants. The prerequisite was real; it was
> smaller than written.

**As shipped.** Option (a) below, with the selection arithmetic extracted rather than duplicated:

- `Jobs/Helpers/BiomeSelection` is now the single definition of "which biome owns this column"
  (`SelectIndex` for the primary Voronoi cell, `SelectSurfaceIndex` for the snoise-dithered surface
  index). It replaced **seven** copies of the same four lines across the generation job, the worm
  carver, the generator's three managed paths and the editor cross-section preview — so the managed
  query cannot drift from the job path, because there is only one path.
- `IChunkGenerator.TryGetBiomeAt(voxelX, voxelZ, out BiomeSample)`, surfaced through
  `WorldJobManager` and `World`. `BiomeSample` carries `Index`, `SurfaceIndex`, `Name` and the
  authored `BiomeBase`, so consumers read biome data directly instead of re-deriving a lookup.
  The legacy generator returns false (it selects by per-biome Perlin weight and has no answer of
  this shape); callers must handle that.
- `BiomeTracker` (a plain manager on `World`, `WorldTimeManager` pattern) samples at 1 Hz and holds a
  new biome for a 3 s dwell before committing, raising `BiomeChanged`. This is what §5.3's crossfade
  hysteresis should subscribe to rather than implementing its own timer — RF-7 and the debug readout
  use the same instance.
- **Parity is pinned by a golden captured from the pre-extraction inline code**, not by the helper
  checking itself: `Validate Biome Selection` compares both the helper and `TryGetBiomeAt` against
  2560 recorded columns spanning ±2²⁴ in both coordinate precisions.

> **`SurfaceIndex` is approximate; `Index` is exact.** The dithered surface index re-samples through
> `noise.snoise`, whose Burst codegen differs from the managed one this query runs under, so ~0.4% of
> columns (those whose jittered sample straddles a Voronoi edge) report a different surface biome than
> the generator placed. The primary index is bit-stable — `FastNoiseLite`'s cellular path agrees exactly.
> Ambience selection reads `Index`, so S2 is unaffected; the caveat matters only if something later tries
> to use `SurfaceIndex` as ground truth for the block underfoot (read the voxel instead).

**Weighted neighbourhood (added 2026-08-29).** `IChunkGenerator.TryGetBiomeWeights(voxelX, voxelZ,
falloffRadius, out BiomeWeights)` answers *what is around this column*, where `TryGetBiomeAt` answers what it
sits in. `BiomeSelection.SelectWeights` walks the cellular neighbourhood `FastNoiseLite.GetCellularEdgeData`
already returns, maps each cell through the shared `IndexFromCellHash`, and accumulates **per biome** — 25
cells routinely share a handful of biomes, and a per-biome consumer wants one weight each. It is deliberately
*not* `BiomeBlender`'s terrain weighting: that one is tuned per biome (`BlendRadius`, `BlendWeight`,
`BlendCurve`) to shape how landforms bleed together, and coupling the two would make retuning a mountain's
silhouette silently retune what the player hears.

That fold is what put `IndexFromCellHash` in `BiomeSelection` at all: `BiomeBlender` carried a private copy
of the mapping, an eighth survivor of the seven the helper originally replaced. Because it runs per column in
the generation job and no suite covered blended height, the fold was gated by a **terrain-height golden
captured before it** (`Validate Biome Selection` B13, 1920 rows, bit-identical after) rather than by
inspection. B14 pins that the weighted primary agrees with `SelectIndex` on every sampled column, and B15
that no biome's weight moves more than 0.15 per block — the property the beds' smoothness rests on.

`AudioContext.BiomeIndex` (§5.3) is a read of `BiomeTracker.Current.Index`. Note the field is a
`byte` in the §5.3 sketch while the query returns `int` — widen the struct field when S2 is written
rather than casting at the call site.

The two options as originally evaluated:

| Option                                                | How                                                                                                                                                     | Trade-off                                                                                                        |
|-------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------|
| **(a) Re-evaluate on demand**                         | Extract the `BiomeBlender` index selection into a Burst-compatible static helper callable from managed code for a single XZ (1/s — cost is irrelevant). | No storage; must keep the helper bit-identical with the job path (one shared method, not a copy). ✅ Preferred.   |
| (b) Cache per-chunk dominant biome at generation time | Store a `byte` per chunk (or per column) during generation.                                                                                             | Touches chunk data/serialization for a 1 Hz query — not worth a save-format conversation. Rejected for this use. |

Option (a) is a small, self-contained refactor (shared static selection method used by both the
job and the managed query) and is seed-safe by construction.

### 6.3 Future feature inputs (no dependency, reserved seats)

- **RF-1 day/night** → `AudioContext.TimeOfDay`: night ambience variants, music gating.
- **RF-7 weather** → `AudioContext.Weather` + the reserved `Weather` mixer group: rain is a 2D
  bed modulated by listener exposure (sky light again), thunder is a Layer-1 one-shot at a
  random offset position.
- **Mobs/entities** → Layer-1 pool via `PlayBlockSound`-style API with their own clip database.

---

## 7. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                                                                                    |
|-------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | All sound data hangs off `BlockType` (per *type*); emitters are pooled scene objects budgeted by count, never per voxel.                                                                    |
| Burst jobs 100 % Burst-compatible               | The only job is the §5.2 emitter scan: reads voxel data, writes a `NativeList` of blittable candidates. No managed types cross the boundary.                                                |
| No GC / LINQ in hot paths                       | Pooled `AudioSource`s; clip *arrays* indexed randomly (no LINQ); the scan consumes a reused `NativeList`; clustering uses pooled lists (`ListPool<T>`). One-shot triggers allocate nothing. |
| Pooling conventions                             | `DynamicPool<T>`-style pools for one-shot and loop sources.                                                                                                                                 |
| No BinaryFormatter/JSON for terrain             | No serialization impact at all (§4.3).                                                                                                                                                      |
| BlockIDs constants, no raw IDs                  | Trigger sites resolve `BlockType` from IDs they already hold; no new raw literals.                                                                                                          |

---

## 8. Phased implementation plan

| Phase                     | Scope                                                                                                                                                                                                                                                                                                                                                      | Effort | Depends on        |
|---------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|-------------------|
| **S0 — Data foundation** ✅ | `SoundMaterial` enum, `BlockSoundGroup`/`BlockSoundDatabase`, `BlockType.soundMaterial` + `BlockTagPreset` field, BlockEditor dropdown, prefill utility, mixer asset + settings wiring (§5.4). Credits plumbing (§9): append `Audio` to `CreditCategory`, "🔊 Audio" section in `REFERENCES_AND_CREDITS.md` + `CreditsDatabase` entries per imported pack. |   🟢   | —                 |
| **S1 — One-shots** ✅       | `SoundManager` + pooled 3D sources, break/place hooks in `PlayerInteraction`, footsteps in the player controller.                                                                                                                                                                                                                                          |   🟢   | S0                |
| **S2 — Ambience & music** ✅ | **Shipped 2026-08-29.** Runtime: `AudioContext` + the pure `AmbienceResolution` layer, `AmbienceDatabase`, biome audio fields on `BiomeBase`, `AmbienceDirector` (four-source bed roster weighted by `BiomeWeights` + rest cycle + cave layer + duck), `MusicScheduler`, per-source underwater low-pass, 16 suite baselines. Ambience content imported (§9): 6 CC0 loops covering the cave bed, the fallback bed and 4 of 6 biomes. **Music content outstanding** — the scheduler runs and finds an empty pool. |   🟡   | S0; §6.2 ✅        |
| **S3 — Fluid emitters**   | Burst emitter scan job, clustering, looping emitter pool with fades.                                                                                                                                                                                                                                                                                       |   🟡   | S1 (pool infra)   |
| **S4 — Later**            | **Two-cell footstep sampling** ✅ (occupied cell + supporting cell, a non-solid occupant layered over the support — see the §5.1 note; shipped 2026-08-29). Still open: ungrounded/swimming steps (deferred — no swimming mechanic exists, `FLUID_BUGS.md` §02), v2 apply-site break/place hook (`VoxelModSource.Live` filter), hit/mining sounds, weather (RF-7), time-of-day (RF-1), `LEAVES` wind emitters.                                                                                                                                                                                                              |   —    | feature-gated     |

S0+S1 alone deliver the largest perceived-quality jump (block feedback + footsteps) and validate
the whole data model; S2 and S3 are independent of each other and can land in either order.

**S2's remaining half is music content.** The bed layer is authored (§9); the scheduler is not, and finds an
empty pool at every pick. Filling `AmbienceDatabase.DefaultMusicPool` and the per-biome `musicPool` fields
touches no code — but the §9 policy makes music the slower half, since the candidate sources need per-pack
licence clearance (and, for two of them, an email) where the ambience beds did not.

**Validation is built alongside, not after**: this is a core system, so each phase adds
its baselines to a `Validate Sound Engine` editor suite in the established validation-suite style
as the phase lands — S0 pins the resolution chain (material → group → clip pick, place→break
fallback, prefill heuristic output), S1 pins trigger-site decisions (which material/event a given
break/place/step resolves to — assertable without playing audio, extended by S4's two step baselines: the
sampled cell pair and the occupant layering rule), S2 pins the ambience decisions — the cave dwell, bed and music-pool selection including both fallback
holes, the bed roster's slot choice and per-source fade convergence, the constant-power gain identity, the duck,
the submersion test and its log-space cutoff sweep, and the scheduler's gap and clip-based no-repeat pick
(biome-query parity is pinned separately by `Validate Biome Selection`, §6.2) — S3 pins the
scan/cluster output (candidate sets and cluster centroids for fixture worlds). The audible layer
on top stays verified in-game, as with every other suite.

### Extension roadmap (post-S4, in intended order)

| Version | Extension                                                                                                                                                                                                                                                                                                                                                                                                                           |
|---------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | **Occlusion** — muffle sounds behind walls: cheap voxel raycast(s) from emitter to listener feeding a per-source low-pass amount. Fits the engine's existing ray-march primitives; no new packages.                                                                                                                                                                                                                                 |
| **v3+** | **Reflection / physical acoustics** — Steam Audio (Valve, Apache-2.0) integration for HRTF, real occlusion, reflection, and reverb from the voxel world. Draft design: [`STEAM_AUDIO_INTEGRATION.md`](STEAM_AUDIO_INTEGRATION.md) — preferred direction is the custom-ray-tracer callback API answering acoustic rays straight from voxel data (no acoustic mesh), with native bindings following the NativeCompressions precedent. |

---

## 9. Content sourcing & licensing

Clip content comes from free/CC0 sources; `BlockSoundDatabase` isolates content from
architecture, so clips can be swapped or upgraded at any time without code changes.

**Ambience beds (2026-08-29):** [NOX Sound — Essentials Series (Nature)](https://www.asoundeffect.com/sounddesigner/nox-sound/),
**CC0** under the same series README as the footsteps pack, 6 of its 18 loops under
`Assets/Audio/Ambience/nox_nature/`: `Cave_Dark` (cave bed), `Wind_Calm` (fallback bed), `Sea` (Ocean),
`Forest_Birds` (Forrest), `Cicadas` (Grasslands), `Wind_Forest` (Steep Grasslands). Mountain and Desert
deliberately ride the fallback rather than inventing a distinction — an exposed peak and a desert both read as
wind. Kept **stereo** and imported as **Streaming**, which is why `BlockAudioImportPostprocessor` now carries two
profiles: the mono / decompress-on-load one-shot profile for `Assets/Audio/Blocks/`, and a stereo / streaming
profile for `Assets/Audio/Ambience/`. Forcing a 2D bed to mono would discard the stereo image that makes it a
bed, and decompressing a 30 s stereo loop holds megabytes of PCM resident for no benefit.

The pack's other 12 loops are earmarked but **not** imported: rain ×2 → RF-7, `Night` → RF-1, the three fire
loops (already mono, right for 3D emitters) and the four river/stream/waterfall loops → S3. Two further NOX
packs sit beside it in the same download — `Iceland_Flows` (23 loops) and `São Miguel Flows` (14) — both strong
S3 material, but they are **separately branded, outside the Essentials Series, and carry their own datasheet**,
so §9's per-pack rule means each needs its own licence check before import.

**No music content is imported yet.** The music sources below carry the heaviest verification burden, which is
why the beds shipped ahead of them rather than waiting.

**Shipped content (2026-08-28):** [Kenney — Impact Sounds](https://kenney.nl/assets/impact-sounds) v1.0,
**CC0**, 75 of its 130 clips under `Assets/Audio/Blocks/kenney_impact/`, covering 12 of the 14 `SoundMaterial` groups
(5 variants each; `Wood`, `Glass` and `Metal` also carry distinct place clips), plus
[NOX Sound — Essentials Series (Footsteps)](https://www.asoundeffect.com/sounddesigner/nox-sound/), also
**CC0**, 114 clips under `Assets/Audio/Blocks/nox_footsteps/` supplying every material's footstep channel
and the `Leaves` / `Plant` / `Liquid` break sounds an impact pack cannot cover. **All 13 sounding materials
now have both break and step content**; only `None` is silent, by design. One folder per pack under
`Assets/Audio/Blocks/`. Recorded in `REFERENCES_AND_CREDITS.md` and `CreditsDatabase.asset`.

Candidate sources for further clip content. **License hygiene rule:** licensing on these sites is
per-asset (or per-pack), *not* per-site — verify the license of every individual download, and
record author + source URL + license per imported clip/pack in the project's **existing credit
infrastructure**: a new "🔊 Audio" section in
[`../REFERENCES_AND_CREDITS.md`](../REFERENCES_AND_CREDITS.md) (following the per-pack format of
the Graphics & Textures section) plus matching `CreditsDatabase.asset` entries for the in-game
credits screen — `CreditCategory.Audio` already exists (it was appended before this design was scheduled), and the
"🔊 Audio" section is now in place awaiting its first entry. This
also satisfies CC-BY attribution wherever a non-CC0 pack is knowingly accepted.

Since this is a free, non-commercial hobby project, attribution-required (CC-BY) and even NC
licenses are *usable* — but CC0/CC-BY remain preferred where an equivalent exists: it costs
nothing at selection time and keeps a future itch.io-style release from requiring a content
audit.

| Source                                              | What it offers                                       | License situation                                                                                                                                                                                                                                                                                                                               |
|-----------------------------------------------------|------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| [opengameart.org](https://opengameart.org/)         | Game-focused SFX + music packs                       | Per-asset, clearly labeled; filter searches to **CC0** directly. The safest browsing surface — several of the sites below are best consumed *via* their OGA-mirrored CC0 entries.                                                                                                                                                               |
| [freesound.org](https://freesound.org/)             | Huge raw-SFX library (field recordings, foley)       | Per-clip: mix of CC0, CC-BY, and CC-BY-NC. Filter to CC0 first; CC-BY needs a credit entry; CC-BY-NC is *usable* for this non-commercial project but last-resort (see the preference note above).                                                                                                                                               |
| [signaturesounds.org](https://signaturesounds.org/) | Curated SFX/music packs                              | ⚠️ Verify per pack before import — license terms are stated per collection, not assumed CC0.                                                                                                                                                                                                                                                    |
| [soundimage.org](https://soundimage.org/)           | Large royalty-free music + SFX library (Eric Matyas) | ⚠️ **Not CC0** — free use requires attribution per his terms (or a paid license to skip it). Fine as a music-bed source if the attribution requirement is accepted and recorded.                                                                                                                                                                |
| [sonic.tcpmusic.com](https://sonic.tcpmusic.com/)   | Music collection ("free to download")                | ⚠️ **Unverified** — individual OGA-mirrored entries exist as CC0, but no explicit license was found for the full downloadable pack. Until clarified: use only the OGA-hosted CC0 entries, not the site's full-pack download.                                                                                                                    |
| [pixelsphere.org](https://pixelsphere.org/)         | Music collection ("free to download", cynicmusic)    | ⚠️ **Mixed — see the policy below.** OGA entries carry per-work `License: CC0` (real, attached, irrevocable under OGA policy). The site's full-pack download has no attached license, and despite the author's profile-level "CC0 Public Domain" statement, at least one OGA entry gates site-hosted tracks behind "contact me for permission". |

**Pixelsphere / cynicmusic policy**: a CC0 dedication attaches per *work*,
not per author — a profile blurb is intent, not a license, and the more specific
"contact me for permission" statement governs the site-hosted pack. Therefore:

1. **Prefer the OGA-hosted version** of any wanted track, downloaded *from the OGA entry* (the
   entry page is the license artifact — record its URL in the credits doc; optionally archive a
   snapshot for heavily-used tracks). Credit `The Cynic Project / cynicmusic.com /
   pixelsphere.org` as he requests, even though CC0 doesn't require it.
2. **Pack-only tracks: email for permission first** (he explicitly invites contact + mailing-list
   signup). Ask to use the named tracks under the same CC0 terms as his OGA uploads; record the
   reply as `Permission granted via email, <date>` in `REFERENCES_AND_CREDITS.md`.
3. **No reply yet ⇒ pack-only tracks are off-limits** — "unlicensed but the author seems
   friendly" is exactly the state the credits system exists to prevent.

The same three-step policy applies to sonic.tcpmusic.com and any future "free to download but no
attached license" source.

---

## Document History

*Entries below the newest are reconstructed from git history — this document predates the
project's Document History convention, so they record what the commits changed rather than
contemporaneous notes.*

* **v1.6** - S2's runtime shipped (2026-08-29): `AudioContext`, the pure `AmbienceResolution` decision layer,
  `AmbienceDatabase`, `ambientLoop`/`musicPool` on `BiomeBase`, an `AmbienceDirector` running a four-source bed
  roster under a dwell-filtered cave layer, a `MusicScheduler`, and a per-source underwater low-pass; the suite
  grew from 14 to 27 baselines. The bed layer is **per-source fades, not a paired crossfade** — a review found the
  paired form hard-cut an audible source whenever a change arrived mid-handover, which no amount of remapping
  fixes for three beds; independent fades make the returning-bed case ordinary and reduce the interrupt to a
  four-deep pile-up. The music repeat guard likewise compares clips rather than pool indices, since the pool is
  re-resolved per pick. Four design points changed against the sketch, and all four are recorded in §5.3 and §5.4 rather
  than only in code: `BiomeIndex` widened to `int` and the struct now carries the biome asset and a `HasBiome`
  flag; the cave layer keys off **stored sky exposure**, because the effective value RF-1 introduced falls to zero
  across the whole surface at night; the bed crossfade is constant power; and the underwater treatment is a
  per-source filter, **not** the mixer snapshot the design called for (the mixer has one snapshot and no effects).
  §5.1's "no liquid contact state" note is corrected: it blocked nothing — submersion is a read of the head cell's
  `fluidType`, at the cost of cell-level rather than surface-level precision. Also corrects two stale counts the
  header carried (the Sound Engine suite was 14 baselines, not 13; Biome Selection is 12, not 10). **Content is
  deliberately not part of this**: no bed or track is imported, so the layer ships silent.
* **v1.7b** - `/sound` console readout added and the cave duck raised to 1 (2026-08-29). The depth gate alone
  did not close the complaint: at ~11 blocks down the taper has barely started, so `_caveDuck` at 0.7 was still
  the binding multiplier and left the biome bed at 30% under a fully committed cave bed. `/sound` exists
  because that took arithmetic to work out from a symptom — the bed gain is the product of five independent
  multipliers, and it now prints each of them plus which duck is binding. Command count 15→16, Command Console
  suite gains B33.
* **v1.7a** - Added the depth gate (2026-08-29), from in-game feedback that a biome bed stayed audible deep
  underground: `_caveDuck` at 0.7 left the surface bed at 30% by construction, and the cave layer's sky-exposure
  signal cannot distinguish a cavern from a roof. `AmbienceResolution.DepthDuck` + `World.TryGetSurfaceHeight`
  read the lighting heightmap for a true depth-below-surface, tapered so a cave mouth still blends. Suite 30→31.
* **v1.7** - Ambience became a weighted mix of the surrounding biomes rather than a selection of one
  (2026-08-29), from in-game feedback that a shoreline switched instead of blending. `SelectWeights` +
  `TryGetBiomeWeights` expose the cellular neighbourhood `BiomeBlender` was already computing for terrain;
  the bed roster drives one source per contributing biome at its share of the mix, merging biomes that
  resolve to the same clip and renormalizing after dropping sub-threshold ones. The `BiomeTracker` dwell was
  **removed from the ambience path** — a continuous weight has no jump to debounce — which also closed the
  "previous biome still audible deep into the new one" complaint. Added a layer-wide rest cycle so ambience
  has silence in it (cave bed exempt), and an `AmbienceDatabase.BedVolume` content trim at 0.35, chosen over
  a lower slider default because the pack is mastered hot and a default would not reach existing settings.
  Getting there needed the eighth copy of the cell-hash→index mapping folded onto `BiomeSelection`, which
  touches per-column generation code: gated by a **terrain-height golden captured beforehand** (B13, the
  first coverage blended height has ever had) and proven non-vacuous by mutation. Suites: Sound Engine 27→30,
  Biome Selection 12→15.
* **v1.6a** - Ambience content imported (2026-08-29): 6 CC0 loops from NOX Sound's Nature Essentials fill
  the cave bed, the fallback bed and four of the six biomes, so S2 is no longer silent.
  `BlockAudioImportPostprocessor` gained a second profile — its `AUDIO_ROOT` covered the whole audio tree and
  would have forced these beds to mono and decompress-on-load, which is correct for 3D one-shots and wrong for
  2D loops in both respects. `convert_audio_pack.py` gained `--stereo` and `--flat` for the same reason. Music
  content is still outstanding.
* **v1.5** - Footsteps became sub-voxel aware (2026-08-29, confirmed in game), fixing a bug the §5.1 note had recorded as
  correct design: the support cell was always one below the occupied one, so standing on a half slab sounded
  the block *under* the slab. `SoundResolution` gained `OccupantCarriesFeet` + `ResolveStep`, reading the
  shared collision-bounds resolver and the physics solver's own ground tolerance (now exposed as
  `VoxelRigidbody.GroundProbeSkin`), and the suite grew a 14th baseline pinning the slab case and the
  tolerance band. Also removed `PlayerFootsteps._landingEmphasis`: every sound group is authored at volume 1
  and the product is `Clamp01`'d, so the landing step was bit-identical to a walking one — the knob had never
  been audible, and expressing emphasis would mean scaling *walking* down instead.
* **v1.4** - The §6.2 managed biome query shipped (2026-08-29), unblocking S2: a shared `BiomeSelection`
  helper (replacing seven duplicated copies of the selection arithmetic), `IChunkGenerator.TryGetBiomeAt`
  returning a `BiomeSample`, a 1 Hz `BiomeTracker` with a 3 s dwell, and a `Validate Biome Selection` suite
  whose oracle is a golden captured from the pre-extraction code. §6.2 also corrects an overstatement in the
  original audit: a managed biome index/name was already reachable via `GetTerrainDebugInfo`, so the gap was
  a *purpose-built* query, not the capability. The query was built for three consumers at once — S2 ambience,
  RF-7 weather, and the debug readout — so §6.2 is no longer sound-specific.
* **v1.3** - Two-cell footstep sampling shipped (2026-08-29), closing the v1.2 limitation: `SoundResolution`
  gained `StepCells` + `ResolveStepMaterials`, `PlayerFootsteps` samples the occupied and the supporting cell,
  and the Sound Engine suite grew from 11 to 13 baselines. The occupant **layers over** the support rather
  than replacing it — winner-takes-all was implemented first and rejected on listening, because dropping the
  ground the player walks on reads as a disconnect whenever the two materials differ. Ungrounded/swimming steps were deliberately left
  out — no swimming mechanic exists to sound, and strokes need their own clips. §5.1's limitation blockquote
  is now the shipped rule, and the header's stale v1.1 stamp (it lagged the v1.2 entry) is corrected here.
* **v1.2** - Footstep sampling limitation recorded (2026-08-28), after S0+S1 were audited in game: steps
  sample only the supporting block, so water wading and cross-mesh flora never sound, and no step fires
  while ungrounded. Written up in §5.1 with the two-cell fix and tracked in `S4`. Also records the second
  content pack (NOX Sound, CC0) that closed the `Leaves` / `Plant` / `Liquid` silences.
* **v1.1** - S0 + S1 shipped (2026-08-28). Status flipped to *Partially implemented*, and four claims
  the original audit made were corrected against the code as built: `CreditCategory.Audio` already
  existed, the physics layer has **no** liquid contact state (§5.1 wading and the §5.3/§7 submerged
  context lose their source and move to S2), `ChunkSection` has **no** fluid-presence flag for §5.2's
  early-out, and §5.4 now records the shipped `SettingsTab.Audio` + `AudioVolumes` arrangement in place of
  the never-built `Group` property. The mixer asset itself is still unauthored — the runtime is
  deliberately mixer-optional so it can be dropped in later without a code change.
* **v1.0** - Mandatory header completed (2026-07-26): `Version`/`Date`/`Status`/`Target` added above the
  existing summary, `Audited` line and relationship list retained as written. Status made explicit —
  **Proposed design — not implemented** — which the original only stated in passing inside the summary
  blockquote. No design content changed. First versioned edition.
* *(2026-07-03, `0da76ddf`)* - Extension roadmap gained its v3+ row when
  [`STEAM_AUDIO_INTEGRATION.md`](STEAM_AUDIO_INTEGRATION.md) was drafted as a child document.
* *(2026-07-03, `39f3261c`)* - Initial design: the **`SoundMaterial`-per-`BlockType` decision** (§3 — a
  dedicated channel rather than overloading `BlockTags`) plus the four-layer runtime (block one-shots,
  fluid/ambient loop emitters, world ambience & music, mixer/settings plumbing).

---

**Last Updated:** 2026-08-29 (weighted biome mix + rest cycle + depth gate; music content still outstanding)  
**Next Review:** when S2's music content or S3 is scheduled. S2's runtime and its ambience beds are done and
need no further design work — what remains is a music pool under §9. S3 must re-verify the §5.2 scan against the fluid
tick as re-architected by the TG-4 arc (see
[`../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md`](../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md))
and settle the missing fluid-presence flag.
