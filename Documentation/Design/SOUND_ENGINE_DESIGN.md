# Sound Engine Design

**Version:** 1.1  
**Date:** 2026-08-28  
**Status:** **Partially implemented — S0 and S1 shipped and confirmed in game.** The `SoundMaterial`
channel, the shared `BlockSoundDatabase`, the BlockEditor dropdown and prefill, the volume settings, the
pooled one-shot voices and the break / place / footstep triggers all exist; the `AudioMixer` is authored
with its seven exposed volume parameters; two CC0 packs supply content, so all 13 sounding materials have
break and step clips. The `Validate Sound Engine` suite guards the resolution chain (11 baselines). Not
yet done: phases S2–S4, and the §5.1 two-cell footstep sampling that water and cross-mesh flora need.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> Design for the VoxelEngine's audio system: block sounds (break / place / step), fluid and
> ambient loop emitters, world-layer ambience & music, and the mixer/settings plumbing that ties
> them together. The core data-model decision — **a dedicated per-block `SoundMaterial` channel
> instead of reusing `BlockTags`** — is settled in §3; the rest of the document layers the runtime
> on top of existing project patterns (ScriptableObject databases, pooling,
> Burst-job-produces / main-thread-consumes).
>
>
> Status: **S0 + S1 shipped** (2026-08-28); S2–S4 outstanding. Section 2's "current state" table describes
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
| Audio code       | **None.** No `AudioSource`/`AudioClip`/mixer usage anywhere in `Assets/Scripts/`.                                                                                                                                                                                                                                                                                     |
| Block data       | `BlockType` (serializable class) inside `BlockDatabase.asset`, authored via the BlockEditor window; `BlockTagPreset` assets as authoring helpers; `BlockIDs` auto-generated constants.                                                                                                                                                                                |
| Tags             | `BlockTags : uint` — 17 flags used of 32. Material flags (`SOIL`, `WOOD`, `PLANT`, `LEAVES`, `ROCK`, `MINERAL`, `ORGANIC`) carry a comment "for tools, sounds, interactions" but were never consumed by audio. The recent worldGen/placement split affected only the two `canReplaceTags` masks; the base `tags` mask is shared by placement, fluids, and raycasting. |
| Break/place path | `PlayerInteraction` → `World.AddModification(VoxelMod)` (`World.cs:1807`), with `VoxelModSource.Live` vs `WorldGen` already distinguishing player edits from generation.                                                                                                                                                                                              |
| Footsteps        | No hook, but `World.GetVoxelState` makes "block under feet" a trivial query.                                                                                                                                                                                                                                                                                          |
| Fluids           | `FluidTickJob` — Burst, worker thread. **Cannot touch managed audio.**                                                                                                                                                                                                                                                                                                |
| Biomes           | `StandardBiomeAttributes : BiomeBase` ScriptableObjects. Biome-at-position is currently computed **only inside Burst worldgen jobs** (`BiomeBlender`, hash-based); there is **no managed "biome under the listener" query** — §6.2 makes this a prerequisite.                                                                                                         |
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
~1.5 blocks traveled, query the block under the feet (`GetVoxelState` at
`floor(position) + down`), resolve `soundMaterial`, `PlayBlockSound(mat, Step, feetPos)`. Jump-land plays
an immediate step (slightly louder) and resets the accumulator.

> **Known limitation — the occupied cell is never sampled** (observed in game, 2026-08-28; `S1` as
> shipped). `PlayerFootsteps.PlayStep` reads exactly one voxel: `floor(y) - 1`, the block *supporting*
> the player. Anything occupying the cell the player stands **in** is therefore inaudible, which is a
> real gap rather than a tuning issue:
>
> - **Water** fills the player's own cell; the supporting block is the riverbed, so wading through
>   water plays `Sand`/`Dirt` and the `Liquid` step clips effectively never sound.
> - **Cross-mesh flora** (grass blades) is non-solid and occupies the player's cell too, so walking
>   through it plays the `Grass`/`Dirt` block beneath and never `Plant`.
>
> Compounding it, `Update` returns early whenever `IsGrounded` is false, so swimming — or wading deep
> enough to lose ground contact — produces no footsteps at all.
>
> **The fix is a two-cell sample with a priority rule** (Minecraft's model): read the occupied cell as
> well as the supporting one, and let a non-solid occupant — liquid, flora, snow layer — win. That also
> supplies the wading trigger, and it needs no physics-layer change, which matters because
> `Assets/Scripts/Physics/` computes **no** liquid contact state at all (contrary to the original audit)
> — the same gap that leaves `AudioContext.Submerged` (§5.3) and the §7 underwater snapshot without a
> source. Tracked in `S4`.

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

### 5.3 Layer 3 — world-layer ambience & music

2D (non-spatial) layered sources with slow crossfades, driven by an **`AudioContext`** snapshot
sampled ~1/s at the listener:

```csharp
public struct AudioContext
{
    public byte BiomeIndex;      // §6.2 — dominant biome at the listener
    public byte SkylightAtHead;  // 0–15; low ⇒ underground
    public bool Submerged;       // head in fluid ⇒ underwater snapshot (§7)
    // Future inputs — reserved, not implemented:
    // public float TimeOfDay;   // RF-1
    // public byte Weather;      // RF-7
}
```

Design the struct now so RF-1/RF-7 *plug in* rather than bolt on; v1 populates biome + sky light

+ submerged only.

- **Biome ambience beds:** `BiomeBase` gains optional audio fields (`AudioClip ambientLoop`,
  `AudioClip[] musicPool`) — ScriptableObjects are the natural home, mirroring how biomes already
  carry generation parameters. Crossfade beds over ~2–4 s on biome change (with a short
  hysteresis so border-strolling doesn't flap).
- **Cave ambience:** `SkylightAtHead == 0` (sustained for a few seconds) fades in a cave bed and
  ducks the biome bed — the sky-light value is a free, already-correct "underground" signal.
- **Music scheduler:** deliberately simple v1 — pick a random clip from the context's pool, play,
  then wait a randomized silence gap (e.g. 3–8 min); re-resolve the pool at each pick so biome
  changes influence the *next* track, never interrupt the current one.
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

**Underwater:** `AudioContext.Submerged` drives an `AudioMixer` snapshot transition applying a
low-pass on everything except UI — cheap and dramatic.

---

## 6. Prerequisites & integration points

### 6.1 Skylight at the listener

Already queryable per-voxel — no work needed beyond a helper on `SoundManager`.

### 6.2 Managed biome-at-position query ⚠️ *the one real prerequisite*

Biome selection currently exists **only inside Burst worldgen jobs** (`BiomeBlender`,
hash-based). Layer 3 needs "dominant biome at the listener XZ" on the main thread. Two options:

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
| **S2 — Ambience & music** | `AudioContext`, biome audio fields on `BiomeBase`, managed biome query (§6.2 option a), beds + crossfades, cave ambience, music scheduler, underwater snapshot.                                                                                                                                                                                            |   🟡   | S0; §6.2 refactor |
| **S3 — Fluid emitters**   | Burst emitter scan job, clustering, looping emitter pool with fades.                                                                                                                                                                                                                                                                                       |   🟡   | S1 (pool infra)   |
| **S4 — Later**            | **Two-cell footstep sampling** (occupied cell + supporting cell, non-solid occupant wins — fixes silent water wading and cross-mesh flora, and supplies the wading trigger; see the §5.1 limitation note). v2 apply-site break/place hook (`VoxelModSource.Live` filter), hit/mining sounds, weather (RF-7), time-of-day (RF-1), `LEAVES` wind emitters.                                                                                                                                                                                                              |   —    | feature-gated     |

S0+S1 alone deliver the largest perceived-quality jump (block feedback + footsteps) and validate
the whole data model; S2 and S3 are independent of each other and can land in either order.

**Validation is built alongside, not after**: this is a core system, so each phase adds
its baselines to a `Validate Sound Engine` editor suite in the established validation-suite style
as the phase lands — S0 pins the resolution chain (material → group → clip pick, place→break
fallback, prefill heuristic output), S1 pins trigger-site decisions (which material/event a given
break/place/step resolves to — assertable without playing audio), S2 pins the `AudioContext`
derivation and biome-query parity (§6.2: managed helper vs. job path bit-identical), S3 pins the
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

**Last Updated:** 2026-08-28 (S0 + S1 shipped and audited in game; footstep sampling limitation recorded)  
**Next Review:** when S2 or S3 is scheduled. S2 must first build the §6.2 managed biome query and decide
where liquid contact state lives, since neither exists. S3 must re-verify the §5.2 scan against the fluid
tick as re-architected by the TG-4 arc (see
[`../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md`](../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md))
and settle the missing fluid-presence flag.
