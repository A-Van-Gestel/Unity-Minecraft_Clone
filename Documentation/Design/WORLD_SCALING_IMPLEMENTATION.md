# World Scaling — Implementation Roadmap (Tier B: unbounded XZ)

**Version:** 2.2
**Date:** 2026-07-20
**Status:** **Tier B COMPLETE. WS-2 + WS-3 SHIPPED (2026-07-13) — XZ fully unbounded, both signs. WS-4a + WS-4b + WS-4c SHIPPED (2026-07-17/18, all in-game confirmed) — far travel is stable, saved player position chunk-relative (level.dat v13), `/teleport` live. The v2 noise rider is IMPLEMENTED (2026-07-20, suite-guarded — FNL `Precise64` double coordinate pipeline behind the global "Far Lands" setting, default precise; in-game far verification pending): generation is artifact-free past ±2²⁴ with the classic float pipeline preserved bit-identically as the opt-in Far Lands mode. OQ-1..7 all resolved in code.**
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The decided execution path for scaling the world horizontally. Analysis (`WORLD_SCALING_ANALYSIS.md`)
> says *what breaks per tier*; this doc says *how we ship it*. **Single most important decision:
> global unbounded (no per-world gate), split on the sign of the coordinate** — Phase 1 (`WS-2`)
> removes only the XZ *upper* bound (keeps `>= 0`), which needs no save-format change, no region-codec
> bump, and none of the negative-coordinate structure/seed work; Phase 2 (`WS-3`) drops the `>= 0`
> floor and pays for all of it at once.

**Audited:** 2026-07-12, at commit `1cb1e5b` (branch `feat/world-scaling`). Verified in code:
`WorldData.IsVoxelInWorld` (:204), `World.IsChunkInWorld` overloads (:3528/:3540) and their ~10 call
sites, the player-relative streaming loops (`World.cs` :664/:2372/:2577 — all `Mathf.Abs(coord −
playerCoord) <= dist`, safe for negatives), `ChunkCoord`'s "0–99" range contract (4 sites, all doc
comments — zero runtime coupling), the `WorldCentre` fallback-spawn path (`World.cs:629` behind the
persisted `WorldSpawnPoint`/`ChunkRelativePosition`), and the `v10→v11` spawn-persistence migration.
WS-1 shift/mask centralization (shipped) and VQ-1 `TryGetVoxel` (shipped) are the foundation this
builds on.

**Second audit (open questions):** 2026-07-13, same branch. All seven §7 questions resolved by
reading the owning code paths — answers inline in §7; the §4/§5 premises they corrected are edited
in place below.

**Relationship to other documents:**

- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — parent analysis; this doc executes its
  Tier B §3 (border removal, negative audit, floating origin). §3.2's floor-div audit already shipped
  as WS-1.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — owns the `WS-1`/`VQ-1`
  rows this roadmap continues; Tier A's perf prerequisites (`P-2`, `LI-2`, palettes) live there and do
  **not** gate the XZ track (they solve height/volume, not XZ fan-out).
- [`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
  — the region codec (`RegionAddressCodec.V2`) and the AOT migration protocol `WS-3` needs for its V3 bump.
- [`CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md`](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) — `CP-7`
  owns the `ChunkHeight`/`CHUNK_HEIGHT` constant unification (a Tier A prerequisite, not on this track).
- [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) — the WS-4 execution
  design (2026-07-16): `WorldOrigin` re-anchor at 64 chunks, full boundary inventory, WS-4a/b/c
  phasing. Supersedes §6's sketch; the noise-precision rider stays deferred to its v2 extension.

---

## 1. Goals & non-goals

**Goals:**

- Remove the hard-coded XZ world border so the world extends horizontally without an artificial cap.
- Do it in low-risk, independently shippable phases guarded by validation baselines (the WS-1 pattern).
- Keep every existing save loadable; changes to generated terrain / addressing are versioned, never
  reinterpreted in place.

**Non-goals (with their home):**

- **Taller/deeper world (Tier A).** Separate track; its 5× volume perf prerequisites are unrelated to
  XZ. See `WORLD_SCALING_ANALYSIS.md` §2.
- **Float-precision correctness far from origin (floating origin, §3.3).** Deferred to **Phase 3** —
  precision only degrades visibly at ~16k–65k units; accepted as a known limitation until then.
- **Cubic chunks (Tier C).** Out of scope entirely; `WORLD_SCALING_ANALYSIS.md` §4.

---

## 2. The decided approach

### 2.1 Global unbounded, not per-world gated ✅ **CHOSEN**

The XZ border is removed for **all** worlds, not behind a per-world capability flag.

- ✅ No flag threading through `IsVoxelInWorld`/`IsChunkInWorld`/spawn/codec; one behavior.
- ✅ Matches the long-term intent — existing worlds *should* be able to grow past their old edge.
- ❌ Existing worlds silently gain the ability to generate past their former border. Accepted: under
  global-unbounded this is the desired behavior, not a surprise to design around. *(Softened
  2026-07-13: the per-world configurable border — `WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md` TF-14,
  shipped as a post-WS-2 follow-up — adds a gameplay fence on top; terrain stays border-blind, and the
  player is fenced in gameplay terms: movement is clamped and, since 2026-07-17, place/break edits are
  gated via `World.IsVoxelInsideBorder` at the interaction boundary. Upgraded worlds default to **no**
  fence (the border is opt-in), so WS-2's grow-past-the-edge behavior is unchanged.)*
- Existing-world spawn on upgrade resolves to the **player's last stored location** — already
  persisted as `WorldSaveData.player.position` (absolute `Vector3` in level.dat, restored on every
  load; verified, see OQ-5) — not the old `WorldCentre`. No new field or migration needed.

*Rejected — per-world gate:* keeps old saves bounded but costs a flag on every border/spawn/codec
decision and a two-behavior test matrix, for a "safety" the global choice doesn't want.

### 2.2 Split on the sign of the coordinate ✅ **CHOSEN**

The fault line is the sign of the coordinate, because nearly all the expensive negative-coordinate
work (structure-RNG mirroring, seed hygiene, defensive codec bump) is triggered *only* when
coordinates go negative. Positive-only expansion sidesteps every one of them.

---

## 3. Phased plan

| Phase                        | Scope                                                                                                                                                                                                                                                                                                                                                                                  |  Effort   | Save impact                               | Depends on     |
|------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:---------:|-------------------------------------------|----------------|
| **WS-2** — unbounded +XZ ✅   | Relax XZ *upper* bound only (keep `>= 0`); neighbor-guard flip; reconceive `WorldCentre` as spawn const                                                                                                                                                                                                                                                                                | ✅ SHIPPED | None (V2 byte-identical)                  | WS-1 ✅, VQ-1 ✅ |
| **WS-3** — negative XZ ✅     | Drop `>= 0` floor (bounds only); fresh spawn → origin. V3 bump SKIPPED (V2 already neg-correct); seed hygiene + floor-div kept separate (§5)                                                                                                                                                                                                                                           | ✅ SHIPPED | None (V2 byte-identical, save v11)        | WS-2 ✅         |
| **WS-4** — floating origin ✅ | Periodic origin shift; `ChunkRelativePosition` for the player; `_WorldOriginOffset` shader continuity; rider deferred to v2. **WS-4a + WS-4b + WS-4c persistence ✅ SHIPPED 2026-07-17** (in-game confirmed to ~20k, no jitter; v13 migration confirmed over multiple saves). Only `/teleport` (CMD-2) is left → [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) | ✅ SHIPPED | level.dat **v13** (player position → CRP) | WS-3 ✅         |

**Validation is built alongside each phase** (WS-1 precedent: its equivalence guard shipped in the
"Chunk Math" suite, not after). WS-2 adds an unbounded-streaming / positive-past-border determinism
scenario; WS-3 adds a negative-quadrant generation-parity + mirroring scenario and rides the
`serialization-migration` skill for the V3 bump.

---

## 4. WS-2 — unbounded positive XZ (Phase 1, ✅ SHIPPED 2026-07-13)

> **Status (2026-07-13, shipped):** All four steps done. Steps 1–2 — bounds baselines + relaxation
> (`IsVoxelInWorld`, `IsChunkInWorld` ×2, `TryGetVoxel` fold-split) with the same-commit parity mirror,
> `ChunkCoord` doc comments, and §3 rationale-cell sync. Step 3 — `WorldCentre` →
> `DefaultSpawnPosition = 800` across its three consumers + minimap west/south-floor walls +
> spawn-derived crosshair + legend relabel. Step 4 (in-game) confirmed: fresh world generated to
> 6000+ voxels (`r.9.1.bin`), existing world showed no terrain seam at the old border. Suites: Chunk
> Math 24/24, Validate All 195/0.
>
> **Existing-world note — border-overflow structure mods (verified benign):** structures near the old
> east/north edge (chunks 96–99) whose parts spilled past x=1600 emitted `VoxelMod`s that
> `World.ApplyModifications` routed to `ModificationManager.AddPendingMod` (no world-bounds guard) and
> persisted to `pending_mods.bin` with absolute coordinates — **orphaned, never discarded**, because
> the target chunk could not generate under the old border. Under WS-2 those chunks now generate, so
> `TryGetModsForChunk` replays the orphaned mods on top of the fresh terrain, *completing* the clipped
> structures. No data loss or corruption; `CanReplaceForWorldGen` resolves any overlap.

### 4.1 What changes

| Change                                                                                 | Notes                                                                                                               |
|----------------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| `WorldData.IsVoxelInWorld` XZ test → `>= 0` only (drop `< WorldSizeInVoxels`)          | Keep the Y test unchanged. West/south walls stay; east/north walls vanish → unbounded positive quadrant             |
| `World.IsChunkInWorld` (both overloads, :3528/:3540) → `>= 0` only                     | Same relaxation at chunk granularity — this is the actual border gate                                               |
| **Neighbor-guard semantics flip** (`World.cs` :1909/:1952/:1989/:2028, :2324/:2344)    | `if (!IsChunkInWorld(neighbor)) continue;` at the old +X/+Z edge flips from "absent" → "present-but-maybe-unloaded" |
| Reconceive `WorldCentre` (`VoxelData.cs`) as an explicit **default fresh-world spawn** | Keep its current value (~chunk (50,50)) so behavior is unchanged; decouple from the now-defunct world-size          |

### 4.2 Why WS-2 needs none of the Phase 2 machinery

- **No V3 codec bump / no migration.** WS-1 already routes `RegionAddressCodec.V2` through shift/mask,
  correct for any *positive* int including chunk coords past 99 → large positive addresses are
  byte-identical under V2. Old saves keep working; they simply gain +XZ room.
- **No structure-RNG work.** Coordinates stay `>= 0`, so no ± collision — existing all-positive
  structure/decorator hashing stays valid.
- **No seed / noise-precision work.** All negative or far-out concerns.

### 4.3 WS-2 blast radius (from the audits)

Small and fully characterized after the 2026-07-13 OQ audit:

- **Bounds tests:** `World.IsChunkInWorld` ×2 (:3528/:3540), `WorldData.IsVoxelInWorld` (:204), and
  VQ-1's `TryGetVoxel` (:226) — the latter folds "< 0" and ">= size" into one unsigned compare per
  axis (`(uint)x >= WorldSizeInVoxels`), so the relaxation must split it back into an explicit
  `x < 0` test on XZ (keep the folded form for Y).
- **Neighbor guards:** the three gates (`AreNeighborsDataReady` / `AreNeighborsReadyAndLit` /
  `AreNeighborsMeshReady`, `World.cs` :1909/:1952/:1989/:2028) plus `WorldJobManager` :203/:1305/:1755
  — all resolve cleanly, see OQ-2.
- **`WorldCentre`** (`VoxelData.cs:35`) — see OQ-3.
- **Cosmetic finite-world UI:** `WorldInfoUtility`'s orange "valid world border" rectangle and
  center crosshair — see OQ-4 (no functional breakage anywhere).

Streaming is already player-relative (verified), the `ChunkCoord` "0–99" contract is doc-only, and
no fixed-size `[WorldSizeInChunks]` array exists anywhere (OQ-1).

### 4.4 Approved execution plan (2026-07-13; decision menu closed)

**Step 1 — baselines first, prove-red.** In the "Chunk Math" suite (`ChunkRelativePositionTests.cs`):
(a) unbounded-bounds baseline — `IsVoxelInWorld`/`TryGetVoxel` at tuples straddling the old border
(`1599.75, 1600, 1600.25`, far-out `1_000_000`; negatives stay out-of-world) — **must be red on tip**
(the existing parity sweep only covers −32…+512 and never touches the bound); (b) V2 codec identity
baseline — encode/decode round-trip for chunk origins past 99 (100, 1000, 100_000) — **a pin, not a
prove-red**: it already passes today and exists to hold the no-V3-bump claim. Keep all test
coordinates ≤ ~2²⁰ chunks (`ToVoxelOrigin`'s `×16` wraps `int` above ~134M chunks — a baseline in
the overflow zone fails for the wrong reason).

**Step 2 — bounds relaxation** (§4.1/§4.3 sites) **+ same-commit companions:** the parity-test
mirror (`ChunkRelativePositionTests` :422-424 inline bounds + :398 edge-tuple verdicts) re-implements
production bounds and goes red otherwise; `ChunkCoord` doc comments;
`CHUNK_LIFECYCLE_PIPELINE.md` §3 rationale cells; this doc's §4 status flip. The `TryGetVoxel`
fold-split (§4.3) is the flagged fragility hotspot. Gate: Chunk Math suite green + **Validate All**
green.

**Step 3 — spawn constant + UI cosmetics.** `WorldCentre` → **`DefaultSpawnPosition = 800`**
(literal; decided) across all three consumers + the `WorldSelectMenu:620` "World Center" legend
label (OQ-3); minimap border → **west/south walls only**, crosshair from the spawn constant (OQ-4,
decided); `SettingsManager:436` comment reword. **`WorldSizeInChunks` stays** until WS-3 (decided);
`WorldSaveData.worldSizeInChunks`/`chunkWidth`/`chunkHeight` are write-only level.dat metadata —
leave untouched.

**Step 4 — in-game gate.** Teleport past x=1600: generate → light → mesh with no stuck frontier and
no fail-safe promotion storms; no seam artifacts at chunk 99↔100; save + reload round-trips the
past-border chunks (new `r.3.*.bin` region). Ends on user confirmation.

Two bisectable commits: (1) baselines + relaxation + doc-sync; (2) spawn constant + minimap.
Compile check = Unity compiler (`RequestScriptCompilation` → `IsCompiling == false` → console), not
`dotnet build`. The seed-magnitude finding is filed-only (`WORLD_GENERATION_BUGS.md` Bug 04,
2026-07-13 note) — no fix in WS-2 (decided).

---

## 5. WS-3 — negative quadrants (Phase 2, ✅ SHIPPED 2026-07-13)

> **Status (2026-07-13, shipped, in-game confirmed):** re-verifying §5's premises against post-WS-2 code
> collapsed the phase to a lean bounds relaxation (mirror of WS-2 to the negative side). Decisions:
> **(a) No V3 codec bump / no migration** — `V2Codec` addresses via WS-1's negative-correct `ChunkMath`
> and region filenames (`r.-1.0.bin`) round-trip negatives with zero format change; save version stays
> 11. **(b) Seed-hygiene (Bug 04) + structure floor-div stay separate** — both are magnitude/far-out and
      > sign-independent, not negative-triggered; keeping them out leaves WS-3 non-seed-breaking (existing-world
      > generation byte-identical). **(c) Fresh-world spawn → origin (0,0).** Landed: `IsVoxelInWorld` → Y-only,
      > `TryGetVoxel` → Y-only, `IsChunkInWorld` ×2 → always-true (kept as the bounds chokepoint), the
      > negative-quadrant prove-red baseline + negative V2 codec pin, parity-mirror/`ChunkCoord`-doc/§3-rationale
      > sync; then spawn→origin + minimap floor-wall removal. **In-game confirmed:** fresh world spawned at (0,0),
      > negative chunks generated correctly, flew to ≈(−10 000, −10 000) with block/lava edits + fluid sim, and
      > negative-region files persist (`r.-20.-18.bin`). Suites: Chunk Math 26/26, Validate All 197/0.
      > **Limitation:** no automated negative-quadrant *generation-parity* scenario (no generation suite exists to
      > extend) — negative terrain determinism is verified in-game only.
>
> The original §5 prerequisite list below is retained for the deferred riders (their homes are noted inline).

Drops the `>= 0` XZ floor, at which point negative coordinates become reachable. The items below were
§5's assumed hard prerequisites; the execution re-scope (above) reclassified them — **only the floor
drop + validation is WS-3**; the rest are separable riders with the homes noted:

- **Structure-RNG negative-quadrant verification (§3.4) — rewrite NOT needed (OQ-6, verified).**
  The assumed multiplicative quadrant-mirroring hashing does not exist: every structure/decorator/
  cave RNG site already uses sign-safe avalanche hashing (`Unity.Mathematics.math.hash` on
  `int3`/`int4` — structure cell election `StandardChunkGenerationJob:577`, per-structure seed
  `StandardChunkGenerator:857`, worm-carver trunk/local `StandardWormCarverJob:174/:239`, fluid tick
  `FluidTickJob:579`). The phase's gating item downgrades to: (a) ✅ **fixed 2026-07-18** — the
  structure grid-cell derivation `(int)math.floor((float)globalX / spacing)`
  (`StandardChunkGenerationJob:573-574`, floor-correct for negatives but float-precision-capped at
  ±2²⁴) now uses `ChunkMath.FloorDiv` (general integer floor-div — spacing is not power-of-two, so
  shift/mask does not apply), exact to the ±2³¹ edge. Shipped **ungated and non-seed-breaking**: an
  exhaustive sweep (`Tools/Python/verify_floordiv_parity.py` — every |x| ≤ 2²⁴ × every spacing 1–64)
  proved the float idiom and the integer floor-div bit-identical in-band, so existing-world structure
  placement is unchanged everywhere generation is non-degenerate; past ±2²⁴ (the already-degenerate
  FNL band) election becomes exact instead of drifting. Guarded by three Chunk Math baselines
  (oracle sweep × spacings, in-band float-parity bands, out-of-band divergence teeth; suite 35→38,
  prove-red via sabotage — the two negative-correction scenarios flip red on truncating division);
  (b) a negative-quadrant generation-parity
    + structure-placement validation scenario — **still open** (no generation suite exists to extend).
- **V3 codec defensive bump (§3.2).** Rides the border-floor removal so it protects real
  negative-addressed data instead of stamping a no-op version. ⚠️ AOT frozen-DTO migration.
- **Seed hygiene (§3.4) — root cause identified (OQ-7).** The `Abs(hash)/10000` hack is a
  **magnitude cap**, not sign handling: the seed is added directly to *float noise coordinates* in
  `StandardChunkGenerationJob:238-239` (surface dither, `snoise(... ± BaseSeed)`) and throughout
  `LegacyNoise` (`position.x += offset + Seed`), so a large |seed| pushes noise inputs past float
  precision and degenerates generation. The hack caps only the random/string-hash paths (≤ ~214k);
  the integer-parse path returns any typed int untouched — a user typing `2000000000` hits the
  breakage **today**, independent of WS-3. Fix = stop using the seed as a coordinate offset (derive
  small offsets by hashing the seed); touches the Standard dither pattern → ⚠️ seed-breaking by
  definition → world-version-gated; Legacy generator stays frozen. The FNL core path is already
  clean (`FastNoiseLite.Create(baseSeed + seedOffset)` uses the seed as an int).
- **Spawn policy (OQ-5, simplified — no migration needed).** Fresh worlds spawn at **(0, 0)**
  (surface-search for Y) — a constant change only. Existing worlds already restore the player to
  `WorldSaveData.player.position` (absolute `Vector3`, persisted in level.dat) on every load, and
  their canonical `spawnPosition` (~voxel (800, 800)) stays valid because the positive quadrant
  survives WS-3 unchanged. The former "level.dat spawn migration" item is dropped.

---

## 6. WS-4 — floating origin (Phase 3, **WS-4a + WS-4b shipped**; WS-4c open)

> **2026-07-16:** the full execution design now lives in
> [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) (WS-4a plumbing /
> WS-4b shift / WS-4c persistence+teleport; decision menu closed). The sketch below is retained
> for the rider spec; where they differ, the child doc wins. Jitter was observed in-game at
> ~10 000 voxels (2026-07-15), earlier than the ~16k estimate below.
>
> **2026-07-17:** the origin plumbing (WS-4a) and the shift itself (WS-4b) are **shipped and in-game
> confirmed** — a ~20k flight through multiple re-anchors renders and behaves correctly with no jitter,
> so the ~10k jitter onset this section describes is gone in practice. **WS-4c** (`PlayerSaveData.position`
> → `ChunkRelativePosition`, level.dat v12→v13, + `/teleport`) is what remains, and the ±2²⁴ cap below
> still applies to the *saved* position until it lands.

The far-travel precision follow-up. Independent of WS-2/WS-3 — a bigger near-origin world with
negative quadrants is fully usable without it. Full design in `WORLD_SCALING_ANALYSIS.md` §3.3
(periodic origin shift by integer chunk multiples, `ChunkRelativePosition` for player/camera/entities,
`_WorldOriginOffset` shader global for noise continuity, the "must-not-shift" list). Its natural
trigger is the first time a player travels far enough for vertex jitter to be visible (~16k units).

**Rider — generation noise precision (assigned 2026-07-13; was unowned).** WS-4 makes far travel
*visually/physically* stable but deliberately doesn't touch generation — so the ~2²⁴ (≈16.7M-voxel)
"Far Lands" band survives it: `FastNoiseLite` sampled at absolute float coordinates degenerates into
striped terrain there (`WORLD_SCALING_ANALYSIS.md` §3.4), and the surface-dither `snoise` sites share
the failure. Fix (rides WS-4, the far-travel phase): pass chunk-local coordinates plus a per-chunk
**double** base offset into the generation jobs, sampling at `(double)(base + local) * freq` narrowed
once per column (measure the Burst double-width cost; FNL's double switch is the fallback).
⚠️ **Seed-breaking by definition** → world-version-gated exactly like WS-3's seed-hygiene fix — old
worlds keep the old sampler. Without this rider the usable radius stays capped at ~16.7M voxels no
matter what WS-4 ships; with it (plus WS-3's negatives) the stable range extends to the natural
integer edge at ±2³¹ voxels, where `×16` chunk-origin wrap makes the world end with old-border wall
semantics — accepted as the permanent world limit.

> ✅ **IMPLEMENTED 2026-07-20** (suite-guarded; in-game far verification pending), with **two spec
> deviations**, both deliberate:
>
> 1. **The primary mechanism above was found insufficient at execution and is superseded by the
>    fallback.** Narrowing `(double)(base + local) * freq` back to float still quantizes *relative
>    to the noise lattice* — at ±2³¹ with freq 0.005 the product is ~10⁷, where float's step is a
>    full lattice cell, so Far Lands persist wholesale (the fractal octaves' lacunarity multiplies
>    re-amplify the magnitude besides). What shipped is the fallback made mode-switched:
>    `FastNoiseLite` gained a **`CoordinatePrecision`** field (`Classic32`/`Precise64`) and a
>    **double public API** (`GetNoise`/`DomainWarp`/`GetCellularEdgeData`/`GetNoiseGrid` — replaced,
>    not overloaded: an int argument would silently bind to a float overload). `Precise64` carries
>    the coordinate in double through the frequency multiply, fractal chain, and lattice floor,
>    narrowing only the in-`[0,1)` fractional deltas; gradient/hash math stays float. `Classic32`
>    narrows at entry and runs the untouched float pipeline — **bit-identical, golden-file-proven**
>    (the far-lands aesthetic is preserved exactly). Generators split head/core; cellular got
>    dedicated double bodies (its loops consume the absolute coordinate). The float-only `snoise`
>    dither/wiggle sites wrap their inputs to 2¹⁸/2²² -block periods (seams half-period offset from
>    spawn) on the precise path only.
> 2. **Global setting instead of world-version gating** (user decision 2026-07-20): World tab →
>    "Far Lands (Classic Noise)", **default off = Precise64**, read at generator init
>    (`WorldJobManager` → `FastNoiseFactory.GlobalCoordinatePrecision`). Consequences accepted:
>    existing worlds' *new* far chunks change on first post-update load, and chunks generated under
>    different modes may mismatch at their borders far out (in-band, Precise64 tracks Classic32
>    within ULP-level drift — suite-pinned ≤ 5e-3).
>
> Guards: FNL suite 13 994 → **15 050** (in-band parity, far-band ±2³⁰ variance, classic-collapse
> pin — the last asserts Far Lands *still exist* in classic mode), prove-red via entry-narrow
> sabotage. Deferred: worm-carver worm positions stay float (far-out worm caves keep some
> degradation in precise mode); `LegacyNoise` frozen; the OQ-7 seed-magnitude fix remains separate.

---

## 7. Open questions — **all resolved 2026-07-13** (read-before-claim: every answer verified in code)

| ID   | Phase | Question (abridged)                                              | Resolution                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
|------|:-----:|------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| OQ-1 | WS-2  | Fixed-size `[WorldSizeInChunks]` arrays?                         | ✅ **Clean — none exist.** Every `WorldSizeInChunks/InVoxels` reference is a bounds test, doc comment, frozen migration constant, benchmark-region clamp (`BenchmarkController:513`, a `Mathf.Min`, not an array), or `WorldInfoUtility` map math whose arrays are sized by *texture* dimensions derived from actual chunk min/max bounds. WS-2's low-risk rating stands.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| OQ-2 | WS-2  | Neighbor-guard flip: do gates separate "in-world" from "loaded"? | ✅ **Yes — cleanly.** All three gates skip out-of-world neighbors but return not-ready for in-world-unloaded ones, and "in-world but unloaded" is already the *normal frontier state* everywhere in the interior (streaming is player-relative). The flip just makes old-border chunks ordinary frontier chunks: they park (event promotions + ~1 s fail-safe scan already cover this), and `AreNeighborsMeshReady` already killed the wave-front ping-pong generically. Two behavior shifts to encode in the WS-2 validation scenario: old-border chunks stop meshing-with-void until outward neighbors populate, and their cross-chunk light mods reroute `DropOutOfWorld` → `PersistUndeliverable` (`LightingJobProcessor.RouteCrossChunkMod:61`) — the standard frontier path. The `IsEffectivelyStable` out-of-world override stops firing on east/north edges (stays live for negative XZ until WS-3). |
| OQ-3 | WS-2  | Default-spawn constant after decoupling `WorldCentre`?           | ✅ **Keep 800 verbatim, rename to `DefaultSpawnPosition` (decided).** Consumers *(corrected in the 2026-07-13 re-review — the v1.1 "World.cs:629 only" claim missed two)*: `World.cs:629` (fresh-world spawn), `Clouds.cs:42` (initial plane anchor — benign, tiles re-anchor to the player in `UpdateClouds`, verified player-following), `FluidStressController.cs:98/:100` (benchmark start position). All three are mechanical renames. The v10→v11 migration carries its own frozen copy; `WorldInfoUtility` derives (50,50) independently; `WorldSelectMenu:620` labels that point "World Center" in the legend → relabel with the rename. Moving spawn to (0,0) is WS-3's job.                                                                                                                                                                                                                        |
| OQ-4 | WS-2  | Does `WorldInfoUtility`/minimap assume finite extent?            | ✅ **No functional breakage.** Bounds come from actual saved chunks (min/max scan) with dynamic downsampling explicitly written "to prevent VRAM overflow on infinite worlds". Two cosmetic staleness items: the orange "valid world border" rectangle (draws the defunct 0–99 box → reduce to the surviving west/south walls) and the red centre crosshair at (50,50) (→ draw at the spawn constant). `SettingsManager:436` is a doc comment; the benchmark clamp is harmless.                                                                                                                                                                                                                                                                                                                                                                                                                              |
| OQ-5 | WS-3  | Player's last location already persisted?                        | ✅ **Yes — `WorldSaveData.player.position`** (absolute `Vector3` in level.dat, not `ChunkRelativePosition`), saved and restored on every load (`World.cs:721`). No new field, no migration. (WS-4 will want it converted to `ChunkRelativePosition` for origin shifts — deferred with WS-4.)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| OQ-6 | WS-3  | Locate the structure/decorator RNG; is it quadrant-mirroring?    | ✅ **Found — and it is already sign-safe.** All sites use `math.hash` avalanche on `int3`/`int4` (see §5); no multiplicative mirroring exists anywhere. The "rewrite" collapses to an integer floor-div fix for the float grid-cell derivation (`StandardChunkGenerationJob:574`) + a negative-quadrant parity scenario.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| OQ-7 | WS-3  | Root cause of the `CalculateSeed` `Abs/10000` hack?              | ✅ **Magnitude sensitivity — seed used as a float noise-coordinate offset** (Standard surface dither `StandardChunkGenerationJob:238-239`, `LegacyNoise`). Not a negative-seed issue; the hack caps only random/string seeds while typed integer seeds bypass it entirely (large typed seeds break generation today). Fix is a generator change (hash the seed into small offsets) → world-version-gated as planned (see §5).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |

---

## 8. Constraint compliance

| Constraint                              | How this roadmap satisfies it                                                                                       |
|-----------------------------------------|---------------------------------------------------------------------------------------------------------------------|
| Packed-`uint` voxels                    | Untouched — this is coordinate/bounds/addressing work, not voxel-data layout.                                       |
| Burst compatibility                     | WS-1's `ChunkMath` shift/mask helpers (already Burst-safe) carry the negative-correct math; no managed types added. |
| No hot-path GC                          | Bounds relaxation removes comparisons, adds none; VQ-1's cache already covers the query path.                       |
| Serialization (no BinaryFormatter/JSON) | WS-2 makes no format change; WS-3's V3 codec + spawn migration use the AOT frozen-DTO protocol.                     |

---

## Document History

* **v2.2** - **v2 noise rider IMPLEMENTED** (2026-07-20, suite-guarded, in-game far verification
  pending): FNL `CoordinatePrecision.Precise64` double coordinate pipeline + double public API,
  classic float path preserved bit-identically (golden file, 15 050 tests) as the opt-in global
  "Far Lands (Classic Noise)" setting (default precise — deviation from the world-version-gating
  spec, user decision). §6 annotated with the shipped design and the finding that the spec's
  primary per-chunk-base-offset mechanism is mathematically insufficient (fallback promoted).
  snoise dither/wiggle period-wrapped on the precise path; worm-carver float positions deferred.
* **v2.1** - §5 rider (a) — the structure cell-election floor-div fix — **shipped 2026-07-18**:
  `StandardChunkGenerationJob:573-574` swapped from the float idiom to the new `ChunkMath.FloorDiv`,
  ungated (exhaustive in-band parity proven by `Tools/Python/verify_floordiv_parity.py`, so it is
  non-seed-breaking by measurement, not just argument), guarded by three new Chunk Math baselines
  (suite 35→38, prove-red via sabotage). Rider (b), the negative-quadrant generation-parity
  scenario, stays open. Version/date bumped only for this §5 annotation.
* **v2.0** - **Tier B COMPLETE** (2026-07-17). WS-4c's persistence half shipped and in-game confirmed over multiple
  migrated saves + the fresh-world flow: the player position is a `ChunkRelativePosition` on disk (**level.dat
  v13**) and stays chunk-relative to the transform, so it is exact to the ±2³¹ edge. Together with WS-4a/WS-4b
  earlier the same day, the horizontal scaling track is done: unbounded both signs, stable far travel, exact
  persistence. Deferred by choice, not blocked: `/teleport` (CMD-2, wants the console first) and the v2 noise rider
  (§6, ⚠️ seed-breaking) — **travel is stable but generation still degrades at ~±2²⁴**, which is the honest ceiling
  today. Status line, §3 row, and Next Review updated; detail in the child doc (v1.7), including the four shipped
  migrations the v13 retype forced onto frozen DTOs.
* **v1.9** - **WS-4a + WS-4b SHIPPED** (2026-07-17, in-game confirmed): the floating origin is live — an existing
  ~10k save flown to ~20k through multiple re-anchors renders and behaves correctly with **no jitter**, closing the
  ~10k onset §6 describes. Far *travel* is now stable; far *generation* still degrades at ~±2²⁴ until the v2 noise
  rider, and the *saved* position keeps its ±2²⁴ cap until WS-4c (level.dat v12→v13 + `/teleport`), which is all
  that remains of WS-4. Status line, §3 row, §6 header, and Next Review updated; full detail in the child doc (v1.6).
* **v1.8** - WS-4 design authored as child doc [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md)
  (2026-07-16): `WorldOrigin` explicit-helper conversions, 64-chunk re-anchor, player save →
  `ChunkRelativePosition` (v12→v13), dev teleport in scope; noise rider stays a WS-4 v2 extension.
  §6 annotated; observed jitter onset ~10k recorded. §3 row + status line updated.
* **v1.7** - **WS-3 shipped** (2026-07-13, in-game confirmed). Re-scoped lean at execution: XZ floor fully
  dropped (`IsVoxelInWorld`/`TryGetVoxel` → Y-only, `IsChunkInWorld` → always-true), fresh spawn → origin,
  minimap floor removed. **V3 codec bump SKIPPED** (V2 + region filenames already round-trip negatives —
  verified; save version stays 11); seed hygiene (Bug 04) + structure floor-div kept separate/non-seed-breaking.
  §5 + §3 row flipped SHIPPED. Chunk Math 26/26, Validate All 197/0; in-game to ≈(−10 000,−10 000), `r.-20.-18.bin`.
* **v1.6** - **WS-2 shipped** (2026-07-13, in-game confirmed). Bounds relaxation + prove-red bounds
  baseline + V2 codec pin, `WorldCentre` → `DefaultSpawnPosition = 800`, minimap west/south-floor
  cosmetics. §4 status + §3 row flipped to SHIPPED. Added the verified-benign existing-world note:
  border-overflow structure mods were persisted (orphaned) to `pending_mods.bin`, not discarded, and
  replay to complete clipped structures under WS-2. Chunk Math 24/24, Validate All 195/0.
* **v1.5** - §4.4 added: the approved WS-2 execution plan (baselines-first prove-red with the
  codec-pin caveat and ≤2²⁰-chunk coordinate cap, same-commit parity-mirror rule, closed decision
  menu, two-commit sequence, in-game gate) — persisted for the cold execution session.
* **v1.4** - Noise-precision item assigned an owner (was unowned in the phasing): double-precision
  per-chunk noise base offsets ride WS-4 as an explicit ⚠️ seed-breaking, world-version-gated rider
  (§6). Documents the resulting end-state: WS-3 + WS-4 + rider = stable range to the natural ±2³¹
  integer edge (old-border wall semantics there, accepted as the permanent world limit).
* **v1.3** - WS-2 plan re-review corrections: OQ-3 consumer list was wrong (`WorldCentre` has three
  runtime consumers — `World.cs:629`, `Clouds.cs:42` [verified benign, clouds are player-following],
  `FluidStressController.cs:98/:100` — plus the `WorldSelectMenu:620` "World Center" legend label).
  Noted: `WorldSaveData.worldSizeInChunks`/`chunkWidth`/`chunkHeight` are write-only level.dat
  metadata (never read at runtime) — left untouched by WS-2; `worldSizeInChunks = 100` is a
  candidate "legacy extent" source for TF-14's default-fence question.
* **v1.2** - TF-14 alignment: per-world configurable world border decided as a separate post-WS-2
  follow-up (gameplay fence only — terrain/pipeline stay border-blind; level.dat field rides the
  TF-12/TF-13 v12 wave). §2.1 trade-off note softened; WS-2 plan itself unchanged.
* **v1.1** - OQ-1..7 all resolved from code (2026-07-13 audit): no fixed-size world arrays (OQ-1);
  neighbor-guard flip = ordinary frontier semantics, no new deadlock surface (OQ-2); keep spawn 800
  verbatim under a decoupled name (OQ-3); minimap already infinite-ready, two cosmetic items (OQ-4);
  player position already persisted as `player.position` Vector3 → WS-3 spawn migration dropped
  (OQ-5); structure RNG already sign-safe `math.hash` → mirroring rewrite descoped to floor-div fix
    + parity scenario (OQ-6); seed hack root-caused as seed-as-float-coordinate magnitude issue (OQ-7).
      Status flipped Draft → Proposed design.
* **v1.0** - Initial draft — global-unbounded + sign-split phasing (WS-2/WS-3/WS-4) captured from the
  2026-07-12 design session; WS-2 plan-ready, WS-3+ direction-captured with open questions.

---

**Last Updated:** 2026-07-18
**Next Review:** when the v2 noise rider (§6) or `/teleport` (CMD-2) is scheduled, or when Bug 04 / the
seed-hygiene fix is. *(TF-14 world border shipped 2026-07-13; WS-4a+b+c shipped 2026-07-17 — Tier B is complete.)*
