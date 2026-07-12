# World Scaling — Implementation Roadmap (Tier B: unbounded XZ)

**Version:** 1.0
**Date:** 2026-07-12
**Status:** **Draft — WS-2 is plan-ready; WS-3+ direction-captured (see Open Questions before building).**
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
  global-unbounded this is the desired behavior, not a surprise to design around.
- Existing-world spawn on upgrade resolves to the **player's last stored location** (the
  `ChunkRelativePosition`-style field), not the old `WorldCentre` — folded into Phase 2's spawn work.

*Rejected — per-world gate:* keeps old saves bounded but costs a flag on every border/spawn/codec
decision and a two-behavior test matrix, for a "safety" the global choice doesn't want.

### 2.2 Split on the sign of the coordinate ✅ **CHOSEN**

The fault line is the sign of the coordinate, because nearly all the expensive negative-coordinate
work (structure-RNG mirroring, seed hygiene, defensive codec bump) is triggered *only* when
coordinates go negative. Positive-only expansion sidesteps every one of them.

---

## 3. Phased plan

| Phase                      | Scope                                                                                                    | Effort | Save impact                   | Depends on                    |
|----------------------------|----------------------------------------------------------------------------------------------------------|:------:|-------------------------------|-------------------------------|
| **WS-2** — unbounded +XZ   | Relax XZ *upper* bound only (keep `>= 0`); neighbor-guard flip; reconceive `WorldCentre` as spawn const  |   🟡   | None (V2 byte-identical)      | WS-1 ✅, VQ-1 ✅                |
| **WS-3** — negative XZ     | Drop `>= 0` floor; V3 codec bump + migration; **structure-RNG mirroring rewrite**; seed hygiene; spawn   |   🔴   | ⚠️ V3 codec + level.dat spawn | WS-2                          |
| **WS-4** — floating origin | Periodic origin shift; `ChunkRelativePosition` for player/camera; `_WorldOriginOffset` shader continuity |   🔴   | None (presentation layer)     | Independent (far-travel gate) |

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

### 4.3 WS-2 blast radius (from the audit)

Small and already characterized: the two bounds tests, the ~6 neighbor-guard sites, and the
`WorldCentre` constant. Streaming is already player-relative (verified), and the `ChunkCoord` "0–99"
contract is doc-only. The remaining risk is concentrated in the open questions below.

---

## 5. WS-3 — negative quadrants (Phase 2, direction-captured)

Drops the `>= 0` XZ floor, at which point negative coordinates become reachable and the following
become hard prerequisites (each is a documented Tier B item in `WORLD_SCALING_ANALYSIS.md`):

- **Structure-RNG mirroring rewrite (§3.4).** `hash(chunkX·K1 + chunkZ·K2)`-style multiplicative
  hashing collides/mirrors across ±quadrants ("mirrored structures" bug). Replace with a
  sign-safe mixer (e.g. SplitMix avalanche on `(x, z)` packed into a `long`). **This is the phase's
  gating item** — it must land before negative generation is shippable, and it bites *near* origin,
  not just far out. Requires a generation-job audit first (not yet located in code).
- **V3 codec defensive bump (§3.2).** Rides the border-floor removal so it protects real
  negative-addressed data instead of stamping a no-op version. ⚠️ AOT frozen-DTO migration.
- **Seed hygiene (§3.4).** Root-cause `VoxelData.CalculateSeed`'s `Mathf.Abs(hash) / 10000` hack;
  ⚠️ seed-breaking by definition → world-version-gated, old worlds keep the old seed output.
- **Spawn policy.** Fresh worlds spawn at **(0, 0)** (surface-search for Y); existing worlds on
  upgrade spawn at the **player's last stored `ChunkRelativePosition`**. ⚠️ level.dat spawn migration.

---

## 6. WS-4 — floating origin (Phase 3, deferred)

The far-travel precision follow-up. Independent of WS-2/WS-3 — a bigger near-origin world with
negative quadrants is fully usable without it. Full design in `WORLD_SCALING_ANALYSIS.md` §3.3
(periodic origin shift by integer chunk multiples, `ChunkRelativePosition` for player/camera/entities,
`_WorldOriginOffset` shader global for noise continuity, the "must-not-shift" list). Its natural
trigger is the first time a player travels far enough for vertex jitter to be visible (~16k units).

---

## 7. Open questions (answer in future sessions before the owning phase builds)

| ID   | Phase | Question                                                                                                                                                                                                                                                                                      |
|------|:-----:|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| OQ-1 | WS-2  | Does anything index an **absolute chunk coordinate into a fixed-size `[WorldSizeInChunks]` array** (would overflow past 99)? Audit expected clean (streaming is player-relative), but this is the load-bearing "WS-2 is low-risk" assumption — **verify before building**.                    |
| OQ-2 | WS-2  | **Neighbor-guard flip:** do the lighting/meshing `IsChunkInWorld(neighbor)` sites already cleanly separate "in-world" from "loaded", or does making them always-in-world on XZ open a deadlock/perf surface? Consult the `chunk-lifecycle` skill; this is WS-2's only pipeline-touching risk. |
| OQ-3 | WS-2  | Exact **default-spawn constant** after `WorldCentre` is decoupled from world-size: keep the current ~chunk (50,50) value verbatim, or pick a fresh comfortable positive origin? (Naming: `WorldCentre` → `DefaultSpawn…`.)                                                                    |
| OQ-4 | WS-2  | Does `WorldInfoUtility` (5 `WorldSize*` refs) or any UI/minimap assume a **finite world extent** (progress %, map bounds) that breaks when +XZ is unbounded?                                                                                                                                  |
| OQ-5 | WS-3  | Is the player's last location **already persisted as `ChunkRelativePosition`** (reusable for the existing-world upgrade spawn), or does a new field + migration step need adding?                                                                                                             |
| OQ-6 | WS-3  | **Locate the structure/decorator RNG** in the generation jobs and confirm whether it actually uses quadrant-mirroring multiplicative hashing (assumed, not yet found in code). Scope of the "rewrite" depends on the answer.                                                                  |
| OQ-7 | WS-3  | Root-cause of the `CalculateSeed` `Abs/10000` hack — negative-seed handling vs. generator magnitude sensitivity? Determines whether the seed fix is isolated or drags a generator change.                                                                                                     |

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

* **v1.0** - Initial draft — global-unbounded + sign-split phasing (WS-2/WS-3/WS-4) captured from the
  2026-07-12 design session; WS-2 plan-ready, WS-3+ direction-captured with open questions.

---

**Last Updated:** 2026-07-12
**Next Review:** when WS-2 planning starts (resolve OQ-1..4 first).
