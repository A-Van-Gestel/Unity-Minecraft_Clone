# Sound Engine Design

**Version:** 1.15  
**Date:** 2026-08-30  
**Status:** **Partially implemented — S0–S3 and S5–S8 shipped; all confirmed in game except S8, which is
awaiting its listening pass.** The `SoundMaterial`
channel, the shared `BlockSoundDatabase`, the BlockEditor dropdown and prefill, the volume settings, the
pooled one-shot voices and the break / place / footstep triggers all exist; the `AudioMixer` is authored
with its seven exposed volume parameters; two CC0 packs supply content, so all 13 sounding materials have
break and step clips. Footsteps sample two cells, so wading and cross-mesh flora sound (§5.1). The
`Validate Sound Engine` suite guards the resolution chain and the ambience decisions (38 baselines).
**S2's runtime shipped on 2026-08-29** — `AudioContext`, the `AmbienceResolution` decision layer, the
`AmbienceDirector` bed pair with its cave layer, the `MusicScheduler` and the underwater low-pass — on top of
the §6.2 managed biome query, which shipped the same day and is guarded by its own `Validate Biome Selection`
suite (17 baselines). **Ambience content is in (§9): six CC0 loops cover the cave bed, the fallback bed and
four of the six biomes.** Music has no content yet, so the scheduler runs and picks nothing. **S5 and S6 shipped on
2026-08-29**: the biome beds are now placed at their biome's bearing rather than played flat (§10), and
`BiomeBase.ambientLoop` has become a list of altitude-banded, weighted `AmbienceTrack`s (§11). **Both are
confirmed in game**, S5 after its placement defaults were retuned by ear. **S3's runtime shipped on
2026-08-30** (§5.2): the per-section `emitterFluidCount` predicate, the Burst `FluidEmitterScanJob`
binning into a world-anchored grid, the pure `FluidEmitterResolution` and the six-source
`FluidEmitterDirector`, guarded by 20 more suite baselines (58 total), **with content for all four emitter
kinds** (§9). **Confirmed in game**, including an ear pass that cut lava's audible radius to 10 blocks — the same
kind of retune S5's placement defaults got — and a review pass whose fixes were confirmed the same day with
no noticeable regressions; restoring the single-root gain made the mix *better*, not merely more correct.
Per-kind volume trims are still all 1.0 and have not been balanced against each other. **S7 shipped on
2026-08-30** (§12): ambience beds carry a per-track gain, so the Loudness tab can normalize the Ambient role
the way it already does Blocks and Fluids; music is deliberately excluded until it has content. The remainder of S4 is still outstanding.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> Design for the VoxelEngine's audio system: block sounds (break / place / step), fluid and
> ambient loop emitters, world-layer ambience & music, and the mixer/settings plumbing that ties
> them together. The core data-model decision — **a dedicated per-block `SoundMaterial` channel
> instead of reusing `BlockTags`** — is settled in §3; the rest of the document layers the runtime
> on top of existing project patterns (ScriptableObject databases, pooling,
> Burst-job-produces / main-thread-consumes).
>
>
> Status: **S0 + S1 shipped** (2026-08-28), S4's two-cell footstep sampling, **S2** — runtime and
> ambience content — and **S5 + S6** (2026-08-29), **S3** — runtime and emitter content — (2026-08-30);
> S2's *music* content and the rest of S4 outstanding. Section 2's "current state" table describes
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

> **Known, deliberately unfixed: the one-shot voices never reach silence.** They use
> `AudioRolloffMode.Logarithmic`, where `maxDistance` is where attenuation *stops*, not where the sound
> becomes inaudible — so a voice sits at `minDistance / maxDistance` (1/20, about −26 dB) at *every*
> distance beyond it. On a 0.3 s clip that is inaudible in practice, which is why it has been left alone;
> the same defect was audible enough on the §5.2 looping emitters to need the custom curve they now use.
> If one-shots ever gain a long clip, or the roster grows enough for the floors to sum, fix it there too.

### 5.2 Layer 2 — fluid & ambient loop emitters ✅ *runtime shipped 2026-08-30*

The one genuinely hard problem: fluid simulation runs in `FluidTickJob` (Burst, worker thread) —
audio cannot be triggered from it, and per-flow-event one-shots would be spam anyway. The design
is **listener-centric emitter scanning**, fully decoupled from the simulation (this is also what
Minecraft effectively does):

1. **Select** (main thread, every 0.5–1 s): walk the chunk columns within the ~2-chunk radius and
   snapshot only the sections that hold a *sounding* fluid, nearest first and capped at 48. The
   predicate is `ChunkSection.emitterFluidCount`, maintained incrementally by `ChunkData.SetVoxel`
   through the palette-independent `FluidBlockLookup`, exactly as `emissiveCount` is. Runtime-only,
   never serialized, so the save format is untouched. The snapshot copies the block palette too rather
   than referencing `World`'s: the job outlives the frame that scheduled it, and world teardown frees
   that array with no ordering guarantee against the director's own.

   A count is only meaningful under the palette it was computed with — the same id can be a fluid in
   one and not another — so each section also records `FluidBlockLookup.Generation`, and the scan
   recomputes any section whose stamp is stale. Without it a rebind leaves counts permanently wrong in
   the *silent* direction, which is the one direction with no symptom.

   **Water and lava are deliberately asymmetric.** Water sounds only when it *moves* (level nibble
   non-zero): a still ocean is all source blocks, counts zero, is never copied, and gets its
   ambience from the §5.3 `Sea` bed instead. Lava sounds in **every** state, including a level-0
   pool — there is no lava bed to carry it, and it is a hazard the player should hear before
   seeing. The test is keyed on `FluidType`, which is already the category axis rather than a block
   identity, so a future lava-like fluid inherits the behaviour without touching the predicate.
   Counting sounding rather than *all* fluid voxels is what keeps the common expensive case free,
   and lava costs nothing extra today because **no biome or lode places it** — it is player-built
   only.
2. **Scan** (`FluidEmitterScanJob`, Burst `IJob`): read the snapshot linearly and accumulate every
   sounding voxel into a bin of a **world-anchored** grid (8-block cells, one slot per kind), as a
   weight plus a position sum. Kinds are `WaterFlow` / `WaterFall` / `LavaFlow` / `LavaFall`,
   split by `BlockTypeJobData.FluidType` and the falling flag — so a still lava pool, which is not
   falling, resolves to `LavaFlow` and needs no fifth kind or extra clip. The scan **reads** voxel data only
   — same read pattern as the meshing gather; it never touches the fluid tick, and it is completed
   a frame after scheduling (produce-on-worker / consume-on-main).
3. **Rank** (`FluidEmitterResolution.Collect`): merge vertically adjacent bins of one kind into a
   single candidate — a 20-block waterfall is one sound, not three stacked copies of itself — then
   keep the heaviest few. Integer sums and grid order make the result order-independent, so an
   unchanged world always resolves to the same ordered set. Horizontally adjacent bins stay
   separate: a wide river *should* occupy more than one point in the mix.
4. **Assign** a fixed budget of 6 pooled **looping** 3D sources (`FluidEmitterDirector`), keyed by
   the candidate's stable world cell so a stream that is still there keeps its own source and its
   fade instead of restarting. Fade in on appear, fade out on disappear, chase the centroid when
   it drifts. Never hard-cut a loop — **except on a teleport**, below.

   **How far a kind carries is content, not a director setting.** `EmitterSoundEntry.audibleRadius`
   authors the silence distance per kind (0 falls back to the director's `_defaultAudibleRadius`,
   24 blocks). Lava authors **10**: it reads as too present at the shared default, which is a
   property of the recording and of what lava is, not of the emitter machinery. Every kind shares
   one rolloff curve even so — `minDistance` is always a fixed fraction of the radius, so the
   shape over *normalized* distance is radius-independent and only the two distances change.
   That coupling is invisible and therefore pinned by a baseline.

**Emitters must be able to stop.** Four rules exist only for that, the first three added after the
first in-game pass (2026-08-30) found emitters that outlived the water, and **confirmed in game** the
same day — emitters now fall silent as the listener leaves them:

- **A scan that finds nothing still runs.** Skipping the job when no nearby section holds flow left
  the previous scan's targets standing, so emitters kept sounding at their old positions until the
  listener wandered back into flowing water. Finding nothing is a result, not a reason to skip.
- **The rolloff curve reaches zero.** Unity's built-in logarithmic mode does not: `maxDistance` is
  where it *stops attenuating*, so a 6 m / 24 m source sits at a quarter of full volume at *every*
  distance beyond 24 m. The emitters use `AudioRolloffMode.Custom` with a curve
  (`FluidEmitterResolution.BuildRolloffCurve`) that keeps the inverse-distance shape and lands on
  silence, interpolated piecewise-linearly so it cannot bulge above 1 or rise with distance.
- **Distance is re-checked every frame, and a teleport cuts immediately.** Scans are ~0.75 s apart
  while the listener moves continuously, so an emitter left behind at speed has its target zeroed
  the frame it leaves audible range rather than waiting for a scan. A jump further than one scan
  radius in a single frame (`/spawn`, a world teleport) silences the roster outright and forces a
  rescan: fading a waterfall out over seconds from a place the player is no longer standing reads
  as the sound following them. That test is in **voxel** space, never Unity space — the engine
  re-anchors its render origin as the player travels (WS-*), and a Unity-space test would read a
  re-anchor as a teleport and cut the world's emitters at random.
- **A world re-anchor translates the roster.** `World.ShiftOrigin` re-derives chunks and borders and
  patches the player, but it cannot know about these sources. Their *voxel* positions survive a shift
  by construction — it is the Unity transforms that go stale, by a chunk-aligned jump — so the director
  watches `WorldOrigin.OriginVoxel` and offsets every source by the delta, exactly as the player is
  offset. Nothing else in the system would notice, which is why it has its own baseline.

**One square root, on the fade.** A source's volume is
`FluidEmitterResolution.SourceVolume(fade, clusterGain, trim, categoryGain)`. `GainFromFade` is an
equal-power crossfade curve and `GainFromWeight` is already the perceptual shaping of cluster size, so
the cluster term enters *linearly*. Folding it into the fade target instead — as the first
implementation did — applies both roots to it (`(w/sat)^0.25`), which flattens cluster size almost out
of existence and, because the fade then travels a shorter distance, collapses a quiet emitter's fade-out
to a fraction of the authored time. Composed in the pure layer so a suite can see the whole product; a
test of `GainFromWeight` alone cannot.

The grid is anchored to world coordinates rather than to the listener on purpose. A
listener-relative grid re-cuts its cell boundaries every time the player moves, so voxels crossing
a boundary jump the centroid they contribute to; snapping to the world lattice keeps a given
river's bins identical from scan to scan, and an emitter moves only when the water does.

**Future ambient blocks** (fire, portals, buzzing ore…) are not wired: the kind taxonomy is a
fluid one today. Making them data rather than code wants an emitter field on `BlockType`, which is
a `BlockDatabase` schema change and belongs with S4.

**Performance — by construction, then profiled.** The scan is written to the project's hot-path
standards: Burst-compiled, linear voxel-array iteration, reused persistent native scratch (no
per-scan allocation), the section-count early-out above, and the whole scan off the main thread.
The per-tick main-thread cost is the snapshot memcpy alone, bounded by the sounding-section count
and hard-capped at 48 × 16 KB. Cadence and radius are tuned against the profiler now that the
layer exists; the scan is a candidate for the existing benchmark-harness pattern.

### 5.3 Layer 3 — world-layer ambience & music ✅ *runtime shipped 2026-08-29*

> **Extended the same day by S5 and S6.** The beds described below are now *placed* at each biome's bearing
> rather than played flat (§10), and each biome offers a *pool* of altitude-banded tracks rather than one
> loop (§11). Everything else here — the weighted mix, the per-source fades, the rest cycle, the two ducks,
> the constant-power gain — is unchanged, and deliberately so: S5 pans without re-gaining and S6 changes only
> which clip a biome resolves to, so neither disturbs the arithmetic this section describes.

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

- **Biome ambience beds — a weighted mix, not a selection:** `BiomeBase` carries
  `AmbienceTrack[] ambientTracks` (since S6, §11 — it was a single `AudioClip ambientLoop` when S2 shipped)
  and `MusicTrack[] musicTracks` (since S8 — it was a bare `AudioClip[] musicPool` before);
  `AmbienceDirector` owns a roster of four bed sources, each running **its own
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
- **Bed level:** `AmbienceDatabase.BedVolume` trims every bed before the category gain. *Since S7 (§12) a
  second, narrower trim sits beside it: each `AmbienceTrack` — and each of the database's two own beds —
  carries its own gain, which the mix carries per entry and `AmbienceResolution.BedSourceVolume` folds into
  the source volume. `BedVolume` still describes the whole pack; the per-track gain normalizes one loop
  against another.* A content trim, not
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
- **Music scheduler:** pick a track, play, then wait a randomized silence gap; re-resolve at each pick so
  biome changes influence the *next* track, never interrupt the current one. The repeat guard compares the
  **clip**, not an index: the candidate set changes with the biome, so an index carried across picks names a
  different track.
  <br>
  **Two pools, added rather than swapped (S8).** A biome's `MusicTrack[] musicTracks` are offered
  *alongside* `AmbienceDatabase._globalMusicTracks`, not instead of them — the original rule let a biome with
  a single regional track shadow the entire global pool for as long as the player stood there. Selection is
  **two rolls, not one roulette over the union**: `_biomeMusicShare` decides how often a pick prefers the
  biome pool when it offers anything, and the per-track `weight` decides which track wins inside whichever
  pool that roll chose. A union would make a biome track's share depend on the global pool's *size* — one
  regional track beside eighteen global ones surfaces about one pick in nineteen — so every biome weight
  would need re-tuning each time the global pool grew. The two rolls read different bit ranges of the same
  hash, so the pool choice and the track choice do not move together. Independence from the *gap* is a
  separate problem with a separate fix: `MusicResolution.PickHash` re-mixes the pick counter into its own
  hash, because `NextGapSeconds` consumes bits 8–23 and no remaining slice is wide enough for a roulette.
  Driving both decisions off one hash made the track a near-deterministic function of the silence before it,
  which is now pinned by a baseline rather than merely asserted here.
  <br>
  Both schedulers **seed their pick counters randomly per session**. The hashes are pure functions of those
  counters, so a counter starting at zero made every launch play the same tracks after the same gaps — and
  the ambience layer hold the same beds for the same stretches.
  <br>
  The repeat guard spans **both** pools: a spent single-track biome pool falls through to the global one
  rather than replaying itself, and a track is only ever repeated when nothing else is playable anywhere.
  Each track carries its own `volume`, folded into the source by `MusicResolution.SourceVolume` with the
  database's pack-wide `_musicVolume` and the category gain, so the Loudness tab can normalize music the way
  it normalizes every other role.
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
| **S3 — Fluid emitters** 🟡 | **Runtime shipped 2026-08-30.** `ChunkSection.emitterFluidCount` + `FluidBlockLookup` (the scan predicate), `FluidEmitterScanJob` (Burst, world-anchored bin grid), `FluidEmitterScanner` (section selection + snapshot + scheduling), the pure `FluidEmitterResolution` (vertical merge, ranking, slot assignment, gain) and `FluidEmitterDirector` (6 looping 3D sources on the `Fluids` group), content for all four kinds (§9), 20 suite baselines. Detail in §5.2. |   🟡   | S2 (director pattern) |
| **S5 — Directional beds** ✅ | **Shipped 2026-08-29.** `FastNoiseLite.CellularCellData` carries each cell's offset alongside its distance; `BiomeSelection.SelectWeightsDirectional` turns that into a per-biome bearing in blocks; `AmbienceDirector` places each bed on its bearing at a fixed radius. Detail in §10. |   🟡   | S2 ✅              |
| **S6 — Track pool** ✅     | **Shipped 2026-08-29.** `BiomeBase.ambientLoop` replaced by `AmbienceTrack[] ambientTracks` (clip + Y band + relative weight); the six Standard biome assets migrated; the pick is a weighted roulette re-rolled when the rest cycle wakes. Detail in §11. |   🟡   | S2 ✅              |
| **S7 — Per-track gain** ✅ | **Shipped 2026-08-30.** `AmbienceTrack.volume` plus per-clip trims for the database's own two beds, composed by `AmbienceResolution.BedSourceVolume`; the Loudness tab writes the Ambient role. Detail in §12. |   🟢   | S2 ✅; S6 ✅        |
| **S8 — Music pools** ✅   | **Shipped 2026-08-30.** `MusicTrack` (clip + weight + volume) replacing both bare `AudioClip[]` pools, the `MusicResolution` layer (biome-share pool roll, then a weighted roulette, with a cross-pool repeat guard), a fourth import profile, `/music`, and the first music content (§9). Detail in §13. |   🟡   | S2 ✅; S7 ✅        |
| **S4 — Later**            | **Two-cell footstep sampling** ✅ (occupied cell + supporting cell, a non-solid occupant layered over the support — see the §5.1 note; shipped 2026-08-29). Still open: ungrounded/swimming steps (deferred — no swimming mechanic exists, `FLUID_BUGS.md` §02), v2 apply-site break/place hook (`VoxelModSource.Live` filter), hit/mining sounds, weather (RF-7), time-of-day (RF-1), `LEAVES` wind emitters.                                                                                                                                                                                                              |   —    | feature-gated     |

S0+S1 alone deliver the largest perceived-quality jump (block feedback + footsteps) and validate
the whole data model; S2 and S3 are independent of each other and can land in either order.

**S3 depends on S2, not S1.** The phase table originally listed "S1 (pool infra)", but S1's voice
roster is a fixed set of short one-shots with stealing — the wrong lifetime model for long-lived
fading loops. The pattern S3 actually reuses is S2's: a director owning a source roster on top of
a pure, suite-pinnable resolution layer, and `AmbienceResolution.AdvanceFade`/`GainFromFade`
themselves.

**S2's music half closed on 2026-08-30 (S8, §13).** Both pools are authored and the scheduler plays from
them. It did not, in the end, "touch no code": the per-track weights and gains this design wanted meant
`MusicTrack` replacing the bare `AudioClip[]`, a `MusicResolution` layer, and scheduler plumbing — the shape
S7 had already established for ambience. The §9 licence policy did make music the slower half, and the pack
that landed came from outside the candidate list below, under its own non-CC0 terms.

**Validation is built alongside, not after**: this is a core system, so each phase adds
its baselines to a `Validate Sound Engine` editor suite in the established validation-suite style
as the phase lands — S0 pins the resolution chain (material → group → clip pick, place→break
fallback, prefill heuristic output), S1 pins trigger-site decisions (which material/event a given
break/place/step resolves to — assertable without playing audio, extended by S4's two step baselines: the
sampled cell pair and the occupant layering rule), S2 pins the ambience decisions — the cave dwell, bed selection with its fallback holes,
the bed roster's slot choice and per-source fade convergence, the constant-power gain identity, the duck,
the submersion test and its log-space cutoff sweep, and the scheduler's gap
(biome-query parity is pinned separately by `Validate Biome Selection`, §6.2; the music decisions moved to
their own partial file under S8, §13) — S3 pins the
scan/cluster output: the section-count differential (the incremental count against a full recount,
across an edit sequence and a pool recycle), the managed/job-side sounding-test parity including its
water/lava asymmetry, the grid's
world anchoring, the still-body and radius-cull negatives, the stream centroid, the waterfall
vertical merge, kind separation, slot reclaim and preference, and the gain curve. The audible layer
on top stays verified in-game, as with every other suite.

**Two S3 hazards are outside what any editor suite can reach**, and are recorded here rather than
counted as covered. The "an empty scan still runs" rule lives in `FluidEmitterScanner.Begin`'s control
flow, which needs a live `World`; the suite can only pin its consequence (an empty grid yields no
candidates). The palette-lifetime fix guards a teardown-ordering race between two GameObjects, which no
suite can stage at all. Both are in-game/inspection concerns.

The section-count differential is the load-bearing one. The count decides whether a section is
snapshotted at all, so an under-count produces *no* sound rather than a wrong one — indistinguishable
from "no water nearby" unless something checks. It was proved red by a mutation that skips falling
voxels (the waterfall-goes-silent failure) before being accepted.

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
`Assets/Audio/Ambience/nox_nature/`. **As wired (verified against the assets 2026-08-29):** `Cave_Dark` is the
cave bed and `Wind_Calm` the fallback bed on `AmbienceDatabase`; per biome, Ocean → `Sea`, Forrest →
`Forest_Birds`, Grasslands and Steep Grasslands → `Wind_Forest`, Desert and Mountain → `Wind_Calm`. All six
Standard biomes carry a bed, so the database fallback is reached only by a world type that answers no biome
(the Legacy generator). `Cicadas` was imported, then left **referenced by nothing** — it was the original Grasslands pick and was
swapped out in the editor as too repetitive at the bed layer's duty cycle. S6 (§11) is what let it back in:
it is now Grasslands' second track at a relative weight of 0.25, so it surfaces roughly one wake in five
rather than every time. Kept **stereo** and imported as **Streaming**, which is why `BlockAudioImportPostprocessor` now carries two
profiles: the mono / decompress-on-load one-shot profile for `Assets/Audio/Blocks/`, and a stereo / streaming
profile for `Assets/Audio/Ambience/`. Forcing a 2D bed to mono would discard the stereo image that makes it a
bed, and decompressing a 30 s stereo loop holds megabytes of PCM resident for no benefit.

**Emitter loops (2026-08-30).** S3's four kinds are content-complete. Two more loops came from the same NOX
Nature pack — `Stream_Calm` → `WaterFlow`, `Waterfall_Calm` → `WaterFall` — under `Assets/Audio/Emitters/nox_nature/`.
Lava came from Freesound: [Audionautics — *Lava loop*](https://freesound.org/people/Audionautics/sounds/133901/)
(**CC BY 3.0**, attribution recorded) → `LavaFlow`, and
[Fission9 — *Lava Loop 4*](https://freesound.org/people/Fission9/sounds/474852/) (**CC0**) → `LavaFall`. Both
downloads shipped their own `license.txt` and source URL, which is the licence artifact §9 asks for. Taking the
attribution-required clip was a deliberate call: with only one lava recording both lava kinds would resolve to
the same clip and the flow/fall split would buy nothing.

All four are downmixed to **mono** and imported **CompressedInMemory**, which is the exact opposite of the bed
profile and for the opposite reason — they play from 3D sources, where a stereo clip does not spatialize, and
several can be audible at once, where streaming would cost a decoder each. `BlockAudioImportPostprocessor`
therefore now carries **three** profiles keyed by folder, each with its own stamp. The mono downmix happens at
encode time rather than being left to `forceToMono`: the result is identical and the file is half the size,
which matters in a repository with no Git LFS.

`Waterfall_Strong` and `River_Moderate` were auditioned and **not** imported — deliberately, per the `Cicadas`
lesson above: an unreferenced clip is dead weight nobody notices. The pack's other 10 loops are earmarked but
not imported either: rain ×2 → RF-7, `Night` → RF-1, and the three fire loops → a future ambient-block emitter
kind (S4), which needs an emitter field on `BlockType` before it has anywhere to hang. Two further NOX packs sit
beside it in the same download — `Iceland_Flows` (23 loops) and `São Miguel Flows` (14) — both strong material
for a richer water set, but they are **separately branded, outside the Essentials Series, and carry their own
datasheet**, so §9's per-pack rule means each needs its own licence check before import.

**Music content landed 2026-08-30 (S8, §13):** [Pizza Doggy — Cozy Tunes](https://pizzadoggy.itch.io/cozy-tunes)
v1.5.4, 18 OGG tracks under the pack's own licence — **not CC0**: use and modification in a game are
permitted, redistributing or bundling the assets themselves is not, and attribution is appreciated but not
required. Imported as shipped, no conversion. It came from outside the source list below, which is why the
per-pack rule matters more than the source: the licence is a PDF in the download, and it is recorded verbatim
in `CreditsDatabase` as `LicenseType.Custom` rather than approximated to a Creative Commons row.

The music sources below carry the heaviest verification burden, which is why the beds shipped ahead of them
rather than waiting; they remain the candidates for broadening the pool.

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

## 10. S5 — Directional ambience beds

**Status: shipped and confirmed in game 2026-08-29.** Filed the same day from in-game feedback: a player
should be able to turn on the spot and hear which way an unseen biome lies. Confirmed against that ask —
standing on a mountain with the ocean ahead and a forest to the right, the three beds are placeable by ear
with the eyes shut.

**Approach.** Place each bed's `AudioSource` at the bearing of its biome and let Unity pan it, rather than
scaling gain by a dot product against the listener's facing. A gain multiplier misbehaves the moment the head
turns — rotating in place would swell and mute a forest that has not moved — while a positioned source
responds to rotation for free and stays correct when the listener *walks*.

**The data already exists and is discarded.** `FastNoiseLite.SingleCellularEdgeData` computes
`vecX`/`vecY` — the offset from the sample point to each jittered cell centre — at
`FastNoiseLite.cs:1885` (Classic32) and `:1962` (Precise64), uses it for the distance, and drops it. Both
overloads need the change or the far bands lose direction.

**Decisions already taken** (2026-08-29, so a later session does not re-litigate them):

- **A separate query, not a wider `CellularEdgeData`.** That struct is built per column inside the generation
  job; adding two more 25-element arrays would roughly double a hot-path stack temporary for the benefit of a
  4 Hz audio read. The audio path gets its own method instead, and the generation path is untouched.
- **Beds stay stereo.** `AudioSource.spread` governs the width of a *3D stereo* source, so directionality does
  not force the mono downmix that would undo S2's stereo/streaming import profile. `spatialBlend` and `spread`
  become the two serialized knobs.

**Also needed:** `FastNoiseLite` has `SetFrequency` but no getter, and the offsets are in noise space — world
blocks are `offset ÷ frequency`.

**Verification. As shipped, and the plan's own gate was not enough.** The golden held: `FastNoiseLite.cs` is
**purely additive** (221 insertions, 0 deletions) and `FastNoiseLiteGoldenValues.txt` is byte-identical, with
all 15 050 library tests green. The self-consistency assertion this section originally named became **B16**
(`|offset| == Distances[i]`, both precision overloads, across Euclidean / EuclideanSq / Manhattan).

But the claim made for it here was **wrong**, and worth recording as the correction it is: `|offset|` is
invariant under a **sign flip** and under an **X↔Z transposition**, so B16 cannot catch "a wrong reference
frame" — the very failure it was filed to catch, and the most likely one. What does catch it is **B17**: step
32 blocks along a reported bearing, re-query, and assert the distance to that biome fell by about the step.
Only a correct bearing gets closer, so one assertion covers sign, axis order and the noise-space→blocks
conversion at once.

This was demonstrated rather than argued. Negating both offsets in both overloads left **B16 green and B17
red**; removing the offsets' shift from the insertion sort turned **both** red. B17 also carries a
sample-count floor, because a run with nothing measurable would otherwise pass every assertion it makes.

One measurement note the first run surfaced: an 8-block step at the ±2²⁴ band produced only ~6.4 blocks of
signal against ~1.6 blocks of Classic32 coordinate quantisation. The step is the signal and the tolerance is
the noise floor; they must not be the same size, which is why the probe walks 32 blocks and requires a
bearing of at least 64.

**Placement, as shipped.** Each bed source sits at a **fixed radius** on its bearing, with `minDistance` set
to that same radius — inside `minDistance` a logarithmic rolloff attenuates by exactly nothing, so the source
is *panned* and never re-gained. That is what keeps the §5.3 arithmetic honest: the mix weights, the
constant-power fade identity and both ducks still describe what is actually heard. Placing a source at the
biome's real distance would have multiplied all of them by a distance curve, silently. It also makes the
minimum-distance clamp this section originally asked for unnecessary.

A bearing that resolves to **zero** — the fallback bed, a merged entry whose contributors cancel, or a world
that answers no weighted query at all — drops that source to `spatialBlend = 0` rather than inventing a
heading. A bed with no direction should sound like it has none. The cave bed is never placed for the same
reason: it is the space the listener is *inside*.

Merged entries (two biomes resolving to one clip) carry the **weight-weighted mean** of their contributors'
bearings, so the single source lands between them in proportion — and cancels to "no bearing" when they are
opposed. Pinned by the `Bed Bearings Survive The Mix And Merge By Weight` baseline.

**Serialized knobs, as tuned by ear:** `spatialBlend` **1.0**, `spread` **0°**, radius 12 blocks, bearing
smoothing 6 s.

The first two shipped at 0.7 / 120° and the feature was **inaudible** — bearings correct in `/sound`, nothing
locatable. `spread` is not a free width control: it fans a stereo source's two channels apart across speaker
space, so it trades directly against the localization S5 exists to provide, and 120° had erased it well
before the beds sounded any wider. `spatialBlend` below 1 compounds that by leaving its remaining share
unpanned in the centre. A stereo bed keeps its image by being *stereo content*, not by being spread — which
inverts the reasoning in the "beds stay stereo" decision above: `spread` is what makes stereo compatible with
3D placement, not what preserves the image.

Diagnosing this took the live sources rather than the symptom: every value was already correct
(`spatialBlend` 0.7 applied, three beds playing at distinct 12-block offsets matching the biomes on screen),
which ruled out the whole placement path in one read and left tuning as the only remaining cause.

**Known limitation.** A biome's cells are scattered, so this points at a biome's *nearest cell*, not its
centroid. At a shoreline that reads correctly; standing inside a biome the nearest cell is close and its
bearing swings as the listener walks. The smoothing knob is the mitigation, and whether 6 s is enough is an
ears question, not an analysis one. Nothing in the suite covers the placement itself — every baseline stops
at the pure layer, exactly as the rendered output does in every other suite here.

---

## 11. S6 — Ambience track pool

**Status: shipped and confirmed in game 2026-08-29.** Filed the same day from in-game feedback: one clip per
biome repeats audibly, and a bed that suits sea level is wrong near build height. Confirmed in Grasslands —
`Wind_Forest` carries most wakes and `Cicadas` surfaces at roughly its authored quarter share. The Y ranges
are still placeholders: every migrated track spans the whole world, so the altitude half of §11 is
*implemented and unexercised* until someone authors a banded track.

**Approach.** `BiomeBase.ambientLoop` (a single `AudioClip`) becomes a list of entries:

```csharp
AmbienceTrack { AudioClip clip; Vector2 yRange; float playChance; }
```

One change covers both asks — variation within a biome, altitude-dependent beds, and a weight so a
characterful loop (the cicadas) surfaces occasionally rather than every time. It composes with the rest cycle
already shipped: each time the layer wakes from a rest stretch is the natural moment to re-roll, so the chance
field needs no timer of its own.

**Decision already taken** (2026-08-29): **replace the field and migrate the four wired assets**, rather than
adding the list alongside `ambientLoop`. Two fields describing one thing would need a precedence rule that
every future reader has to learn, and the old field would linger as a trap. The migration is an editor pass
moving each wired clip into a single-entry list, with the assets re-verified by read-back — the check that
caught a silently-null reference during S2's wiring. **Six** Standard biome assets carry a bed today (all of
them); the four Legacy assets have the field at null and need no migration.

**The pick is a weighted roulette** over the tracks eligible at the listener's altitude: `playChance` is a
weight relative to the biome's other eligible tracks, and exactly one always wins. The alternative — an
independent roll per track, falling through when all of them fail — was rejected because a bed losing its
roll is a *second*, hidden source of silence, indistinguishable in game from a missing clip. Making the layer
go quiet is the rest cycle's job and it already does it. All-zero weights fall back to a uniform pick: an
author who set nothing has said nothing about proportion, which is not a request for silence.

The roll is a **pure function of (roll generation, biome index, altitude)** — no cached selection to
invalidate. The generation advances when the rest cycle wakes, which §11 already identified as the one moment
nothing audible is cut; with the rest cycle switched off it advances on the same authored audible-stretch
timer, or every biome would keep its first track for the session. The biome index is folded into the hash so
two biomes in one mix do not roll in lockstep — a shoreline flipping both its beds in the same breath reads
as a glitch.

**Migration, as shipped.** The clip→biome mapping was **read off the six assets before the field was
replaced**, because the old field no longer exists to read afterwards; the pass then wrote each clip into a
single-entry list spanning the world's full Y range. Verified by reading the six `.asset` files **back from
disk** (an editor write reporting success is not evidence) and by a `git diff` showing 27 insertions and 6
deletions confined to the audio field. The four Legacy assets needed no migration (the field was
already null) and Unity reserialized them to `ambientTracks: []` on its own.

**Verification.** Three new baselines plus one extension, all proven red before they were trusted:
a track outside its band is never selected at any salt or altitude (`IsEligibleAt` stubbed to `true` turns it
red); the play chance spreads in proportion over 4 000 rolls, with every eligible track hit at least once and
a lockstep check between two biomes (a roulette stubbed to "first eligible" turns it red) — a spread
assertion, because "the pick is a valid index" is satisfied by a generator that returns the same index every
time, which is the bug §11 exists to fix; and `ResolveBedMix`'s merge rule now covers two biomes *rolling*
the same shared track across 64 salts, not only two biomes falling back to it.

One gap the migration exposed and closed: nothing in the suite read the shipped biome assets, so emptying
`ambientTracks` on all six would have left every baseline green and the world playing nothing but the
fallback. `Every Standard Biome Authors An Ambience Track` is the census that now fails instead.

**Not covered.** `AmbienceDatabase.DefaultLoop` and the cave loop are still single clips — §11 scopes the
track pool to `BiomeBase`, and the asymmetry is deliberate rather than overlooked. The music pool is
untouched.

**Authoring (added 2026-08-29).** Tracks and pools are no longer inspector-only. The Sound Editor gained an
**Ambience** tab — the first editor surface `AmbienceDatabase` has ever had, its five fields having been
reachable only through the raw inspector — and the World Gen Preview's Biome Editor gained an **Audio**
sub-tab. Both render one shared `AmbienceTrackListDrawer`, so the two cannot drift; they exist separately
because "what ambience content exists and does it sound right" is asked while auditioning a clip library and
"what should this place sound like" is asked while tuning a biome.

The drawer carries the one thing a raw inspector cannot: a **roll preview** that runs the shipped
`AmbienceResolution.SelectTrackIndex` across a salt sweep at an author-chosen altitude, reporting which track
wins and how often. That is what makes the altitude band authorable at all — before it, setting a Y range and
checking the result meant flying there in game. `BiomeConfigValidator` also gained audio checks (no track;
a clipless slot; a band outside the world's 0–`ChunkHeight`; a duplicated clip; all-zero weights), filed
under an **appended** sub-tab index so the existing constants keep their meaning.

---

## 12. S7 — Per-track ambience gain ✅ *shipped 2026-08-30*

**Status: shipped 2026-08-30, ambience only.** The Sound Editor's Loudness tab measures every shipped clip
and can write a normalizing trim into the authored volume. Before S7 only two roles *had* one — blocks carry
`BlockSoundGroup.volume` and emitters carry `EmitterSoundEntry.volume` — so ambience and music rows were
measured, compared, and then marked `no trim field`. Ambience now carries a gain per bed; **music still does
not**, deliberately (see the end of this section).

Measured role medians at filing (143 comparable clips of 199; the remainder are shorter than the meter's
400 ms gating block and have no integrated loudness): **Fluids −26.0**, **Ambient −34.1**,
**Blocks −36.7 LUFS**.

### What shipped

- **`AmbienceTrack.volume`**, a `[Range(0,1)]` content trim beside the clip, band and weight, read through
  **`AmbienceTrack.EffectiveVolume`**.
- **`AmbienceDatabase._caveLoopVolume` and `._defaultLoopVolume`**, the same field for the two beds the
  database owns rather than a biome. Without them the fallback bed — routinely the *same clip* as a biome's
  track — would have changed level depending on which path selected it.
- **`AmbienceResolution.BedSourceVolume(fade, trackVolume, duck, trim, categoryGain)`**, the composed bed
  gain as a function, mirroring `FluidEmitterResolution.SourceVolume`. The trim multiplies *outside* the
  equal-power curve: only the fade passes through `√`, because the rest are already gains.
- **A volume channel through `ResolveBedMix`**, index-aligned with the clips and merged the way the bearing
  is — as the weight-weighted mean of the contributors that landed on the entry. Entries merge **by clip**,
  so two biomes sharing a bed arrive as one source that can only be played at one level.
- **The authoring field** in `AmbienceTrackListDrawer`, so it reaches both the Sound Editor's Ambience tab
  and the WorldGen Biome Editor from one edit.
- **The Loudness tab writes it**: `CategoryHasTrimField` admits `Ambient`, and `ApplyAmbienceTrims` walks
  the biome assets and the database's two beds.
- **Three new suite baselines plus an extended asset census** (61 → 64 Sound Engine baselines).

### The three traps, and what each one cost

1. **A new serialized float defaults to 0, not 1.** Answered twice over: `EffectiveVolume` reads 0 as
   *unset* — the same defensive shape `EmitterSoundEntry.audibleRadius` uses — **and** the shipped assets
   were migrated to carry `volume: 1` explicitly, so the Loudness tab shows an authored number rather than
   an inferred one. The migration is **7 tracks across 6 Standard biome assets**, not the 10 assets this
   section estimated while scoping: the 4 Legacy biomes carry `ambientTracks: []`.
2. **A clip can be governed by several entries.** `Wind_Calm` is the database fallback bed *and* Desert's
   *and* Mountain's track. The tab's claim map accumulated per clip instead of last-writer-wins, and the
   volume column reports **`multi`** — with a tooltip naming the owners — when they disagree. Apply writes
   the same trim to *all* of them, which is sound only because `TryComputeTrim` derives the trim from the
   file's own loudness and never from the volume the entry happens to carry.
3. **The bed gain chain was *not* pinned by a baseline.** This section originally claimed it was. It was
   not: `RunBedGainCurve` asserts `GainFromFade` — a pure static function — in isolation, while the director
   composed the chain inline, where no editor scenario could reach it. Adding a multiplicand there would
   have left the suite green and proved nothing. Extracting `BedSourceVolume` is what made the trap's claim
   true; both halves of the chain were then prove-red confirmed, one by dropping the trim from the
   composition and one by stopping the mix carrying it.

### Review pass (2026-08-30)

A `/code-review` over the change found six items, all in the editor half — the runtime gain chain came
through clean. Three were mine, three predated S7:

- **The writability guard was drawn but never enforced.** `HasTrimField` was computed for the table and
  consulted by nothing; `ApplyAmbienceTrims` walked every track regardless. A row could say "Apply cannot act
  on it" and be written the moment the button was pressed. The claim record now lives in
  `Editor/Libraries/AudioClipClaim`, and the row and all three writers read `IsWritable` from that one object.
- **A clip claimed by two roles is never writable.** Each role normalizes against its own target, so such a
  clip has two different correct trims and Apply must not silently pick one. It is judged under the role that
  owns its gain — not whichever database was walked first, which is what the accumulating map did before.
  Unreachable from shipped content (there is no music yet), so it is pinned by two suite baselines built on
  synthetic claims instead. The load-bearing case is **two *writable* roles**: with one writable and one not,
  the "every entry writable" rule already blocks it, and a prove-red mutation of the cross-role guard alone
  moved nothing.
- **Block rows advertised a trim Apply would never write.** `ApplyBlockGroupTrims` anchors a group on the
  median of its own clips, while the row proposed that clip's own trim — so only a group's median clip could
  ever read `applied`, and the other ~114 sat on an arrow pressing the button did not clear. Row and writer
  now share `TryGroupAnchorLufs`. A clip in two groups (five footstep clips are in both Dirt and Grass) is
  genuinely governed by two volumes and reports `multi` rather than naming one of them.
- Pre-existing: the `unmeasured` and `too short` substitute labels never had the volume column's width added
  when that column was introduced, leaving their audition buttons out of line with every other row.

### Music: closed by S8

Deferred at filing because the pools were bare `AudioClip[]` with nowhere to hold a gain, and because no
music content existed to tune against. **Both were resolved on 2026-08-30** — see §13. Every sounding role
now carries a per-clip trim, so `AudioClipClaim.CategoryHasTrimField` admits all four and the Loudness tab
writes them all.

---

## 13. S8 — Music pools ✅ *shipped 2026-08-30*

**Status: shipped 2026-08-30.** Music gained the shape ambience already had — weighted, per-track-tunable
pools — plus the project's first music content.

### What shipped

- **`MusicTrack`** (`{clip, weight, volume}`), replacing the bare `AudioClip[]` on both
  `AmbienceDatabase._globalMusicTracks` and `BiomeBase.musicTracks`. Lossless: every pool was empty, so
  there was nothing to migrate.
- **`MusicResolution`**, a pure layer beside `AmbienceResolution` and `FluidEmitterResolution`: the
  two-stage pick (pool roll by `_biomeMusicShare`, then a weighted roulette inside it), the cross-pool
  repeat guard, and `SourceVolume`.
- **`AmbienceDatabase._biomeMusicShare`** (0.4) and **`._musicVolume`** (1.0, unmatched — the pack has not
  been level-matched against the rest of the mix yet).
- **A fourth import profile**: `Assets/Audio/Music/` gets stereo + `Streaming` + Vorbis, the ambience
  profile's shape with its own stamp.
- **`/music`** — lists both pools with each weight's resolved share, and `next` / `stop` / `play <name>`.
  Gaps run to eight minutes, so without it every by-ear check of a weight or a trim costs a silence.
- **Six baselines** replacing the three that asserted the old replace-semantics (69 total).
- **Content**: Pizza Doggy's *Cozy Tunes* v1.5.4, 18 OGG tracks, imported as shipped. Not CC0 — a custom
  licence permitting use and modification but no redistribution of the assets themselves; attribution is
  appreciated, not required. Recorded in `CreditsDatabase` as `LicenseType.Custom`.

### Two defects the new baselines caught

1. **A spent single-track biome pool repeated itself forever.** `TryPickFrom` fell back to "replay the last
   track rather than go silent" *inside* the preferred pool, so the global pool was never reached. The
   repeat allowance is now the last resort across **both** pools, not within one.
2. **The cross-role claim rule quietly stopped being exercised.** Its fixture paired Music (gainless) with
   Ambient; once music became writable the promotion it asserted no longer applied. Rewritten against `UI`,
   which is genuinely gainless — the same class of stale-fixture problem the S7 review's prove-red found.

### Authoring (reworked 2026-08-31)

Music has its **own Sound Editor tab**, and both it and the Ambience tab were restructured around a **scope
column**: one selectable list whose first row is `🌐 Global` and whose remaining rows are the biomes, with a
single detail pane showing whichever is selected.

The old shape put global content in a section stacked *above* the biome split. That holds only while the
global content stays short — at eighteen tracks the section pushed the biome list and its detail pane past
the bottom of the window, and both of that tab's scroll views were inside the panes it had displaced, so
nothing below was reachable. Capping the section's height restored reachability and looked worse: a row
clipped mid-height, and one scroll region directly above another.

Making global content a *selection* removes the failure mode rather than bounding it — there is only ever one
pane, so nothing can displace anything, and the two tabs now match the Blocks tab, which has always been
list-left / detail-right. The tabs keep **separate** selections but resolve their `SerializedObject` at draw
time, because a single editor rebound only on click points at the other tab's biome the moment you switch.

The WorldGen Biome Editor's Audio sub-tab still shows a biome's beds *and* its music together: it answers
"what should this biome sound like", where the split does not apply.

### Cave music (added 2026-08-31)

`MusicTrack.environment` (`Any` / `Daylight` / `Dark`) gates the **light** an entry belongs in. `Dark` never
plays in daylight; `Daylight` still plays in the dark but its weight is scaled by
`AmbienceDatabase._daylightWeightWhenDark` (0.25). Scaled rather than excluded because the dark pool is small
— barring every daylight track would loop the two dark pieces. Zero is the exception and means what it says:
no daylight music in the dark. `Any` is the zero enum value, so nothing needed migrating.

**Caves and night are one context.** A track written for the eerie quiet of a cave suits the surface after
dark for the same reason, so the flag names the light rather than the place and `AudioContext.IsDark` is the
union — `Underground || Night`. The enum's byte values never moved when night joined the definition, so no
asset migrated. The cave **bed** deliberately does not use the union: it answers to `Underground` alone,
because cave ambience under an open midnight sky would simply be wrong.

`Night` is read from `WorldTimeManager.SunElevation < 0` — pure day-fraction arithmetic over two constants,
where `GlobalLightLevel` would dereference a settings asset that may not be loaded yet. It fills the
`TimeOfDay` seat `AudioContext` reserved for RF-1. No dwell filter: unlike a cave mouth, sunset does not
flicker, and the music layer only reads it between tracks.

**The environment belongs to the entry, not the clip.** The same file can be an `Underground` entry in the
global pool and an ordinary one in a biome's, each with its own weight — which is why caves did **not** need
to become a biome to get cave music.

**The underground answer moved to `AudioContext`.** `AmbienceDirector` used to run the skylight test and its
3-second dwell privately, which was fine while the cave bed was the only consumer. §5.3 already argued the
general case: `SoundManager` publishes the context once because "the beds, the scheduler and the underwater
filter have to agree about where the listener is, and independent timers disagree at exactly the moments that
matter — a cave mouth". A second dwell timer in the music scheduler would have been exactly that bug, so the
skylight threshold and the dwell moved to `SoundManager` with the decision they drive, and the director now
reads `Context.Underground`. The dwell advances at the **sample** rate rather than per frame — it was
previously re-evaluating the same 4 Hz skylight reading every frame.

Authored: `Strange Worlds` and `Whispering Woods` are `Dark`; the other 15 are `Daylight`.

### Not done

Altitude bands or time-of-day gating for music, and a biome "exclusive" flag that would let a biome
*replace* the global pool rather than add to it. Neither was asked for; both are additive later.

---

## 14. S9 — Mood-driven dynamic pools (filed, not started)

**Status: filed 2026-08-31.** Raised while deciding how night should affect music selection, and filed
rather than built because it *subsumes that decision* — settling the night question under the current model
first would be work this replaces.

### The idea

Tag each track with the **mood it gives** rather than the places it belongs. Each *context* the listener is
in — biome, cave, time of day, later weather — declares which moods it wants and how strongly. The candidate
pool is assembled per pick from the moods the current context asks for.

### Why it is the right end state

The author reliably knows a song's **mood**; they do not reliably know every situation that mood fits. The
current `MusicTrack.environment` asks the second question, so every new context (weather, depth, combat,
boss) means revisiting every track. Mood tagging asks the first, and a new context is then a single
declaration of what it wants — the content stops needing edits as the game grows.

It also subsumes what exists: `Any` / `Surface` / `Underground` is a two-mood system with the
context-to-mood mapping hardcoded.

### Why it is deferred

Two contexts (cave, night) that both want the *same* mood, over 17 tracks. A mood system means a mood set, a
context-to-mood weight table as a new authoring surface, and a resolver rewrite — a matrix authored for a
2 x 2 problem. **Revisit when either is true:** a third context that wants something *different* from the
first two (weather and combat are the likely triggers), or a pool past roughly 40 tracks where hand-placing
each one stops being practical.

### The motivating case, settled 2026-08-31

Dark tracks should be favoured at **night** as well as in caves. Settled under the current model rather than
waiting for S9, because the honest form costs almost nothing: **caves and night are the same context —
darkness** — so the enum names the light (`Daylight` / `Dark`) and `AudioContext.IsDark` is the union. See
§13. The observation survives into the mood model: "dark" is one context, not two, whatever the tagging
scheme becomes.

### Traps found while scoping

1. **An unclassified track must not fall out of every pool.** Whatever mood enum arrives, its zero value has
   to mean "plays anywhere" and not "matches nothing" — the same defect `AmbienceTrack.volume` had, where a
   new serialized field deserialized to a value that silenced existing content. Every track authored before
   the mood field exists will read zero.
2. **`environment` is replaced, not extended.** Do not invest further in it meanwhile; a `Night` flag or a
   multiselect added now is churn this removes. The one exception is the darkness unification above, which
   the mood model keeps.
3. **The pool roll and the mood weights overlap.** `_biomeMusicShare` already decides biome-vs-global before
   any per-track weight applies. If a biome also expresses itself as mood preferences, the two mechanisms
   answer overlapping questions and one has to give — decide which *before* authoring a matrix against both.
4. **It has to stay explainable.** `/music` prints each weight's resolved share, and that readout is what
   made the current weighting tunable at all. With moods, "why is this track eligible" becomes a composed
   answer, and the command has to show the composition or tuning turns into guesswork.

---

## Document History

* **v1.15** - Cave music + S9 filed (2026-08-31). `MusicTrack.environment` gates where an entry may play,
  and the dwell-filtered underground answer moved out of `AmbienceDirector` into `AudioContext` so the beds
  and the scheduler cannot disagree at a cave mouth — §5.3 had already argued that rule for the sampled
  context generally, and a second dwell timer would have been exactly the bug it warns about. Music's night
  behaviour was deliberately **not** settled: mood-driven pools (§14) subsume the question, so answering it
  under the current model would have been work that gets replaced.

* **v1.14** - S8 review pass (2026-08-31). Three defects, all invisible until content existed. The pick
  counters started at **zero**, and every hash downstream is pure, so each launch replayed the same tracks
  after the same silences — true of the ambience rest cycle and bed rolls since S2 as well, and fixed in both.
  The gap and the track were drawn from **one hash** whose bit ranges overlap by sixteen bits, so the gap
  effectively chose the track; the v1.13 entry below asserted these rolls were independent, which was wrong,
  and a baseline now enforces what the prose claims. Both `poolRoll` and the track roll divided by an
  inclusive maximum, skipping the biome pool on 1 pick in 256 even at a share of 1. The lesson worth keeping:
  a design doc that asserts a statistical property without a baseline behind it is a claim, not a fact.

* **v1.13** - S8 music pools shipped (2026-08-30). The pool choice is a *ratio*, not a weight, and that is
  the whole design: folding "how often does the biome win" into the track weights makes it depend on the
  global pool's size, so importing a nineteenth global track silently halves every biome track's share. Two
  rolls off different bit ranges of one hash keep the pool choice and the track choice independent. The
  repeat guard had to be lifted OUT of the per-pool walk — inside it, a one-track biome pool always had an
  answer and the global pool was unreachable, which the new baselines caught immediately. Music content
  landed with it: 18 tracks under a custom (non-CC0) licence, imported at source quality.

* **v1.12** - S7 shipped, ambience only (2026-08-30). The per-track gain is a *separate multiplicand*, not a
  weight: the mix's weights are renormalized to sum to 1, so a gain folded into one would be divided straight
  back out. It therefore travels as its own channel through `ResolveBedMix`, merging by the weight-weighted
  mean the bearings already use, because entries merge by clip and a merged entry is one source. The scoping
  note's third trap was wrong in a way worth recording: the "baseline pinning the bed gain chain" pinned only
  `GainFromFade`, so the chain had to be *extracted* into `BedSourceVolume` before a baseline could reach it
  — a green suite over an inline chain would have proved nothing. The unset rule and an explicit asset
  migration were both taken rather than either alone, so that a track authored outside the drawer is still
  audible and the tab still shows a real authored number.

*Entries below the newest are reconstructed from git history — this document predates the
project's Document History convention, so they record what the commits changed rather than
contemporaneous notes.*

* **v1.9** - S5 and S6 shipped (2026-08-29). **S5:** `FastNoiseLite` gained `CellularCellData` — the same 5×5
  neighbourhood plus each cell's offset — as a *separate* struct, because `CellularEdgeData` is built per
  column inside the generation job and must not double in width for a 4 Hz audio read; `BiomeSelection`'s
  weight walk and bearing walk are one shared loop, affordable because neither entry point runs in that job.
  Beds are placed at a fixed radius with `minDistance` set to match, so the source pans without being
  re-gained and §5.3's mix arithmetic still describes what is heard. **S6:** `BiomeBase.ambientLoop` became
  `AmbienceTrack[] ambientTracks`, picked by weighted roulette over the tracks eligible at the listener's
  altitude and re-rolled when the rest cycle wakes; the six Standard assets were migrated from a mapping
  captured before the field was removed, and verified by reading the files back from disk. Suites: Sound
  Engine 31→35, Biome Selection 15→17, `Validate All` 625→631 across 27 suites. **§10's own verification
  claim was wrong and is corrected in place**: `|offset| == Distances[i]` is invariant under a sign flip and
  an axis swap, so it cannot catch the wrong reference frame it was filed to catch — a step-along-the-bearing
  assertion does, and the two mutations that separate them are recorded there. Both phases confirmed in game
  the same evening; S5's placement defaults were **wrong on first hearing** and were retuned from
  `spatialBlend` 0.7 / `spread` 120° to **1.0 / 0°**, in the scene as well as in code — see §10.

* **v1.11** - S3's runtime shipped (2026-08-30). The fluid-presence flag §5.2 had been waiting on became
  `ChunkSection.flowingFluidCount` (renamed `emitterFluidCount` later the same day, below) — flowing voxels
  only, not fluid voxels, which is what lets a still ocean cost the scan nothing — maintained incrementally through the palette-independent `FluidBlockLookup` on the
  `emissiveCount` precedent, and runtime-only, so the save format is untouched. Clustering went to a
  **world-anchored bin grid** rather than §5.2's original greedy distance clustering: integer weight/position
  sums are order-independent, so an unchanged world always resolves to the same ordered set, where greedy
  clustering's output shifts with candidate order as the listener moves. Vertical merging keeps a waterfall one
  emitter; horizontal spread is deliberately left as several. Also corrected §8's dependency — S3 builds on S2's
  director/resolution split, not on S1's one-shot voice roster, whose lifetime model is the opposite one. 11
  baselines added, plus an emitter census and an import-profile guard (51 total); the section-count differential
  was proved red first. Content shipped the same day (§9): the NOX `Stream_Calm` and `Waterfall_Calm` loops, plus
  two Freesound lava loops — one **CC BY 3.0**, knowingly accepted so flowing and falling lava are distinguishable
  rather than sharing one clip. All four are mono / CompressedInMemory, a third import profile that is the exact
  inverse of the ambience beds' stereo / streaming one. A first in-game pass the same day found emitters
  outliving the water they came from, in three separate ways — an empty scan skipping the job entirely, Unity's
  logarithmic rolloff never reaching silence, and per-scan-only distance checks — each now fixed and pinned
  (53 baselines). The rolloff bound assertion earned its keep immediately: smoothed curve tangents put gain at
  1.000149 across the plateau, so the curve is piecewise-linear instead. The stop behaviour is confirmed in
  game; the mix itself is not yet tuned by ear. A second in-game pass made the predicate **asymmetric** on the
  user's call: water still sounds only when it moves, but lava sounds at any level, including a still pool —
  it has no ambience bed of its own and is a hazard worth hearing early. The predicate is keyed on
  `FluidType`, so `flowingFluidCount` became `emitterFluidCount`; still lava resolves to `LavaFlow`, needing no
  fifth kind. Proved red on both sides before acceptance. A third pass moved the audible radius onto the
  content entry (`audibleRadius`, per kind) and cut lava to 10 blocks by ear; `_emitterRadius` became
  `_defaultAudibleRadius` and now means the *silence* distance rather than the full-volume one.

* **v1.10** - Ambience authoring UI (2026-08-29): a Sound Editor **Ambience** tab (the first editor surface
  for `AmbienceDatabase`), an **Audio** sub-tab in the Biome Editor, a shared `AmbienceTrackListDrawer` with
  in-place auditioning and a roll preview driven by the shipped picker, and audio entries in
  `BiomeConfigValidator`. No runtime code changed. Recorded in §11 under *Authoring*.

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
* **v1.8** - Filed S5 (directional beds) and S6 (track pool) as §10 and §11 (2026-08-29), from in-game
  feedback on the shipped S2 layer. Also corrects the header stamp, which had sat at 1.6 while the history ran
  through v1.7, v1.7a and v1.7b — the same lag this document's v1.3 entry recorded fixing once before. Both carry the decisions already taken — a separate noise query rather than
  a wider `CellularEdgeData`, stereo beds kept via `spread`, and `ambientLoop` replaced rather than shadowed —
  so a later session executes them instead of re-deciding them. Neither is started.
* **v1.7b** - `/sound` console readout added and the cave duck raised to 1 (2026-08-29, **confirmed in
  game**: birds no longer audible deep underground). The depth gate alone
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
  (2026-08-29, **confirmed in game**: overlapping biome beds and the crossfade between them both work),
  from in-game feedback that a shoreline switched instead of blending. `SelectWeights` +
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

**Last Updated:** 2026-08-30 (S3 fluid emitters complete and confirmed in game; music content still outstanding)  
**Next Review:** when S2's music content or S7 (per-track ambience gain, §12) is scheduled. S2's runtime and its ambience beds are done and
need no further design work — what remains is a music pool under §9. S3's runtime is done too: the fluid-presence flag it
was waiting on is now `ChunkSection.emitterFluidCount`, and the scan reads a main-thread voxel snapshot rather than the
tick's own state, so the TG-4 re-architecture (see
[`../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md`](../Architecture/BLOCK_BEHAVIOR_TICK_ARCHITECTURE.md)) does not
reach it. S3 is done and confirmed, radius included. The one loose thread is per-kind volume balance — every
`EmitterSoundEntry.volume` is still 1.0, so the four clips are only as level-matched as the recordings happen to
be.
