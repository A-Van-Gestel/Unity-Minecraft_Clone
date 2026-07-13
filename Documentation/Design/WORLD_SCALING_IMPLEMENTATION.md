# World Scaling — Implementation Roadmap (Tier B: unbounded XZ)

**Version:** 1.4
**Date:** 2026-07-13
**Status:** **Proposed design — not implemented. OQ-1..7 all resolved in code (2026-07-13); WS-2 plan-ready, WS-3 direction-captured.**
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
  decided as a post-WS-2 follow-up — adds a gameplay fence on top; terrain stays border-blind, only
  player movement is clamped. Whether upgraded worlds default to a fence at their legacy extent is
  TF-14's open question, not WS-2's.)*
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

| Phase                      | Scope                                                                                                                                                         | Effort | Save impact                         | Depends on                    |
|----------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|-------------------------------------|-------------------------------|
| **WS-2** — unbounded +XZ   | Relax XZ *upper* bound only (keep `>= 0`); neighbor-guard flip; reconceive `WorldCentre` as spawn const                                                       |   🟡   | None (V2 byte-identical)            | WS-1 ✅, VQ-1 ✅                |
| **WS-3** — negative XZ     | Drop `>= 0` floor; V3 codec bump + migration; structure-RNG negative verification; seed hygiene; spawn                                                        |   🟡   | ⚠️ V3 codec bump                    | WS-2                          |
| **WS-4** — floating origin | Periodic origin shift; `ChunkRelativePosition` for player/camera; `_WorldOriginOffset` shader continuity; **noise-precision rider** (double base offsets, §6) |   🔴   | None (presentation) / rider ⚠️ seed | Independent (far-travel gate) |

**Validation is built alongside each phase** (WS-1 precedent: its equivalence guard shipped in the
"Chunk Math" suite, not after). WS-2 adds an unbounded-streaming / positive-past-border determinism
scenario; WS-3 adds a negative-quadrant generation-parity + mirroring scenario and rides the
`serialization-migration` skill for the V3 bump.

---

## 4. WS-2 — unbounded positive XZ (Phase 1, plan-ready)

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
  centre crosshair — see OQ-4 (no functional breakage anywhere).

Streaming is already player-relative (verified), the `ChunkCoord` "0–99" contract is doc-only, and
no fixed-size `[WorldSizeInChunks]` array exists anywhere (OQ-1).

---

## 5. WS-3 — negative quadrants (Phase 2, direction-captured)

Drops the `>= 0` XZ floor, at which point negative coordinates become reachable and the following
become hard prerequisites (each is a documented Tier B item in `WORLD_SCALING_ANALYSIS.md`):

- **Structure-RNG negative-quadrant verification (§3.4) — rewrite NOT needed (OQ-6, verified).**
  The assumed multiplicative quadrant-mirroring hashing does not exist: every structure/decorator/
  cave RNG site already uses sign-safe avalanche hashing (`Unity.Mathematics.math.hash` on
  `int3`/`int4` — structure cell election `StandardChunkGenerationJob:577`, per-structure seed
  `StandardChunkGenerator:857`, worm-carver trunk/local `StandardWormCarverJob:174/:239`, fluid tick
  `FluidTickJob:579`). The phase's gating item downgrades to: (a) fix the structure grid-cell
  derivation `(int)math.floor((float)globalX / spacing)` (`StandardChunkGenerationJob:574`) —
  floor-correct for negatives but float-precision-capped at ±2²⁴ (spacing is not power-of-two, so
  the fix is an integer floor-div helper, not shift/mask); (b) a negative-quadrant generation-parity
    + structure-placement validation scenario.
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

## 6. WS-4 — floating origin (Phase 3, deferred)

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

**Last Updated:** 2026-07-13
**Next Review:** at the WS-2 plan-approval gate (plan authored 2026-07-13, awaiting decision menu).
