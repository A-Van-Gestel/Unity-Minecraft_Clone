# Chunk Pre-Generation (CMD-6) Design

**Version:** 1.1  
**Date:** 2026-10-17  
**Status:** In implementation — PG-0…PG-2 landed; PG-3/PG-4 next; PG-5 on the build owner's go-ahead.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> A deterministic "make every chunk in this rectangle exist on disk" operation, shared by a new
> `/chunk generate` console command and the benchmark's Ensure Generated step, which it replaces.
> The chunk pipeline keeps terrain resident only around the player, so **the pre-generator does not
> bypass or extend the pipeline — it walks the player anchor through a grid of stations and, at
> each, waits until every still-missing chunk in reach is populated and its lighting has settled.**
> The pipeline itself is untouched. In the benchmark this turns a fixed ~198 s flight that is still
> short of 100 % coverage at view distance 32 into a step whose cost scales with the chunks actually
> missing: an Editor prototype finished a realistic 531-chunk gap in 8.1 s, and a fully fresh
> 8 100-chunk tour area in 87.9 s.

**Audited:** 2026-10-17, at commit `a01e953e` (branch `main`).
Findings are from reading `BenchmarkController`, `BenchmarkRouteGeometry`, `BenchmarkTourCoverage`,
`PipelineTelemetry.EstimateTraceCapacity`, `World` (`CheckViewDistance`, `DrainGenerationRequests`,
`LoadOrGenerateChunkInner`, `UnloadChunks`, `IsBeyondUnloadDistance`, `ApplyModifications`,
`ForceUnloadAllChunks`, `SaveAllModifiedChunks`, `TeleportPlayer`, `UpdateTeleportHold`, the `Update`
order), `WorldJobManager` (`ScheduleGeneration`, `ProcessGenerationJobs`), `ChunkData.Populate` /
`ModifyVoxel`, `ChunkSerializer` (lighting flag), `ChunkStorageManager` (`LoadChunkAsync`, saves,
`GetRegion`), `RegionFile` (header, `GetAllChunkMetadata`), `VoxelRigidbody.IsTeleportHeld`, and the
command layer (`IConsoleCommand`, `CommandContext`, `CommandEngine.PostLine`,
`ConsoleCommandInstaller`, `ChunkInfoCommand`, `TeleportCommand`). The cost figures in §2.1 come from
the IL2CPP benchmark reports of 2026-10-06 … 2026-10-11; the §2.2 prototype ran live in Editor Play
mode on a fresh seed-4242 world through the Unity CLI.

**Relationship to other documents:**

- [`../Architecture/COMMAND_CONSOLE_SYSTEM.md`](../Architecture/COMMAND_CONSOLE_SYSTEM.md) — §8 files
  this work as **CMD-6**; the command half follows its `CommandContext` facade and late-output
  (`PostLine`) precedents.
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the
  pipeline the pre-generator drives without modifying: §4's main loop, §5.1's generation path and
  §9.6's unload rules are what the readiness predicate (§4.3) is built on.
- [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) — §7.0
  item 3 deferred the vd 26/32 tour-coverage gap to this command.
- [`FLIGHT_PROFILE_CAPTURE.md`](FLIGHT_PROFILE_CAPTURE.md) — FP-9b/FP-11a define the ensure pass this
  replaces and the coverage instrument that stays as its independent check.
- [`../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
  — the region files the saved-chunk query (§4.2) reads; no format change.
- [`REGION_FILE_CONCURRENCY.md`](REGION_FILE_CONCURRENCY.md) — the single region lock the query shares.

---

## 1. Goals & non-goals

### Goals

1. **Deterministic completion** — when the operation reports success, every chunk in the requested
   rectangle is populated and either on disk or resident and marked for saving. A shortfall is
   reported loudly with its count, never absorbed.
2. **Cost proportional to what is missing** — chunks already on disk are neither reloaded on
   purpose nor waited on; a fully generated rectangle costs one header scan.
3. **No pipeline changes** — no new state flags, readiness gates, unload arms or admission paths.
   The operation reads pipeline state and moves the anchor; the chunk-lifecycle invariants hold by
   construction.
4. **One implementation, two callers** — the benchmark (`yield return`) and the console command
   (fire-and-forget with progress and cancel) share the same coroutine.
5. **Benchmark comparability** — chunks it generates reach disk with settled lighting, as the
   flight's mostly did, so the loading pass keeps measuring loading.

### Non-goals (v1)

- **Generating without moving the player.** Needs a second residency anchor in `World` — planned as
  a **v2 extension**, §7.2.
- **Background generation during normal play.** Same v2 dependency.
- **Re-lighting chunks already on disk.** Terrain the generation pass saved with pending lighting
  stays that way; that disk state is already a per-run variable today and is unchanged here.
- **Redefining tour coverage.** `BenchmarkTourCoverage` keeps counting populated chunks; it is the
  check, not the mechanism.

---

## 2. Current state (what exists today)

| Area                   | State                                                                                                                                                                                                                                                                                                                                                                      |
|------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Ensure Generated pass  | `BenchmarkController.RunEnsureGeneratedPass` flies the closed 12-point loading tour once at `BenchmarkRouteGeometry.EnsureGeneratedSpeed` (50 m/s). Its duration is `TourLengthMeters / 50` — fixed by geometry, independent of how much terrain is actually missing.                                                                                                      |
| Residency              | Player-centric only. `CheckViewDistance` (World.cs:4418) queues the `(2·LoadDistance+1)²` square around the player nearest-first, and only on a chunk-boundary crossing. `UnloadChunks` and the §3.2 out-of-range discard in `ProcessGenerationJobs` both reclaim anything beyond `IsBeyondUnloadDistance` (`LoadDistance + 2`, World.cs:4040).                            |
| "Populated" vs "done"  | `ChunkData.Populate` sets `IsPopulated` **before** the job's structure mods are expanded; the per-frame mods quota can defer the rest of the job to a later frame (WorldJobManager.cs:1336) with the job still tracked. A completion check must also require "no generation job tracked".                                                                                  |
| Structure spill        | `ApplyModifications` (World.cs:3698) routes mods for an absent or unpopulated chunk to `ModManager.AddPendingMod`; they replay on that chunk's load or generation. Order-independent.                                                                                                                                                                                      |
| Save marking           | Only `Populate` and `ModifyVoxel` add to `ModifiedChunks` (ChunkData.cs:456/708). Lighting never does, so a reloaded, re-lit chunk is not re-saved — its disk copy keeps whatever lighting state it was unloaded with.                                                                                                                                                     |
| Lighting state on disk | `NeedsInitialLighting` is serialized (ChunkSerializer.cs:175/262). A modified chunk — freshly generated ones included, since `Populate` marks them — unloaded with pending light changes is flagged for a full re-light first (World.cs:4187), so leaving a station before lighting settles would hand the loading pass extra re-light work.                               |
| Saved-chunk existence  | No query exists. `RegionFile`'s header (`_offsets`) answers it, but `ChunkStorageManager.GetRegion` opens with `FileMode.OpenOrCreate` — a naive probe would create empty region files.                                                                                                                                                                                    |
| Anchor movement        | `World.TeleportPlayer` shifts the floating origin, parks the player and arms the arrival hold; `UpdateTeleportHold` releases on destination populated + mesh applied. `VoxelRigidbody.IsTeleportHeld` gates its whole `FixedUpdate` (VoxelRigidbody.cs:327). The benchmark moves its own transform (it lives on the player, `Player.cs:94`); `Update` re-anchors on drift. |
| Command layer          | `Commands` stays UnityEngine-free and reaches the world through `CommandContext.World`. `/chunk` exists with one subcommand (`ChunkInfoCommand`, `/chunk info`). Output that arrives after `Execute` returns goes through `CommandEngine.PostLine` (the `TeleportHoldEnded` → `WorldUIManager` precedent).                                                                 |
| Coverage instrument    | `BenchmarkTourCoverage` arms at the generation pass, counts footprint chunks on `StampPopulated`, snapshots after the ensure pass and freezes after the transition; ≥ 99 % is the admissibility bar.                                                                                                                                                                       |

### 2.1 What the ensure pass actually costs

From the IL2CPP benchmark reports (the `Chunks populated` line of the `Ensure Generated` phase):

| Capture                           | Total run | Ensure pass    | Generated by ensure | Reloaded by ensure | Coverage at loading start |
|-----------------------------------|-----------|----------------|---------------------|--------------------|---------------------------|
| vd 10, default speeds, 2026-10-10 | 7m 20s    | 197.8 s (45 %) | 99                  | 20 216             | 100.0 %                   |
| vd 32, default speeds, 2026-10-10 | 7m 59s    | 197.8 s (41 %) | 4 902               | 47 831             | 100.0 %                   |
| vd 32, 200 m/s only, 2026-10-11   | 4m 46s    | 197.8 s        | 8 629               | 45 478             | **99.7 % (58 short)**     |
| vd 10, 200 m/s only, 2026-10-11   | 3m 43s    | 160.7 s (72 %) | 292                 | 15 907             | 100.0 %                   |

Across the 2026-10-10/11 vd 10 captures the ensure pass generated between 1 and 2 363 chunks while
reloading 14 000–20 000 from disk. The pass is speed-bound, not work-bound: the generation pass has
already covered 95–99.9 % of the tour by the time it starts.

### 2.2 Prototype: the station walk, measured

A throwaway `run_script` probe (Editor Play mode, Mono, fresh world, vd 10 → `LoadDistance` 13, the
Editor running at ≈ 28 fps) moved the player through the §4.4 station grid and, at each station,
waited until every missing chunk within Chebyshev distance 12 was populated with initial lighting
scheduled and no generation or lighting job in flight:

| Scenario                                                  | Missing chunks | Stations | Time   | Stalls | Console errors |
|-----------------------------------------------------------|---------------:|---------:|--------|-------:|---------------:|
| Worst case: a fresh 90×90 rectangle (the vd 10 tour bbox) | 8 100          | 16       | 87.9 s | 0      | 0              |
| Realistic: the same rectangle shifted 3 chunks            | 531            | 7        | 8.1 s  | 0      | 0              |

Per station: 4.2–6.9 s for 625 fresh chunks, 0.9–1.8 s for 75 fresh chunks amid terrain reloaded
from disk. The Editor figures are frame-bound (≈ 28 fps against ≈ 440 fps in the vd 10 IL2CPP ensure
phase), so the player numbers should be lower. The vd 32 cost is **not yet measured** (§8).

> **Corrected by PG-2 (measured through the real API, three fresh seed-4242 worlds, vd 10).** The realistic
> row did not measure generation: its 531 strip chunks had already been generated by the first run's edge
> stations, which stream to `LoadDistance` (13) past their cover radius, and the prototype excluded the old
> rectangle by hand instead of reading disk — so it only waited for reloads, which arrive lit. Through the
> real API the shifted rectangle has **0** missing chunks. A genuine 531-chunk gap (rectangle (47..136)
> after a fresh (40..129) run) costs **16.8–22.6 s** over 7 stations; the fresh 90×90 costs **70.1–90.1 s**
> over 16 (0 stalls in every run, post-check clean). Per station ≈ 40–60 % populate, the rest the lighting
> tail, which is **initial lighting**, not the later edge-check rounds: the prototype's looser predicate is
> met within 0.4 s of full settle. Each move streams ≈ 620 new chunks (only 108 overlap at stride 23), whose
> lighting backlog (320–420 ready vs the vd 10 close threshold of 256) keeps the panic gate closed on 25–55 %
> of frames. The cost is the pipeline streaming a full square — inherent to §3.1 Option A.

**Projected effect** (estimate): vd 10 total ≈ 440 s → ≈ 260 s (−40 %); vd 32 ≈ 480 s → ≈ 300–340 s
(−30 … −37 %), coverage 100 % at both by construction.

---

## 3. Decisions

### 3.1 How chunks get generated

The pipeline has exactly one residency anchor — the player transform — and everything that decides
what is loaded, kept, discarded or unloaded measures distance from it.

#### Option A — Station walk over the single-anchor pipeline ✅ **CHOSEN**

Move the anchor to a station, let the ordinary pipeline stream the square around it, wait for the
targets in reach, move on.

- ✅ Zero pipeline changes: no flag, gate, admission or unload edit — the CP-\*/P-4/LP-\* arcs and
  their deadlock invariants are untouched. The operation only reads `ChunkData` flags and the job
  dictionaries, and moves a transform the benchmark already moves.
- ✅ Every downstream behavior is the real one: structure spill, pending mods, lighting columns,
  telemetry stamps, the panic gate, the save path.
- ✅ Memory stays bounded to one resident square plus its unload trail, whatever the region size.
- ✅ Measured feasible (§2.2).
- ❌ The anchor moves, so the console command teleports the player through the region and back.
- ❌ Wasted work: the square around each station is streamed and meshed in full even where only a
  few chunks were missing. Bounded by the station count; §2.2 shows it is cheap.

#### Option B — A second residency anchor ("chunk tickets") (deferred to v2)

Teach `World` a secondary keep-resident square: `CheckViewDistance`'s queue rebuild, the drain,
`IsBeyondUnloadDistance` (unload + discard + strand checks), unload triggering, and the panic-gate
residency scaling would all have to account for it.

- ✅ The player stays put; ticketed chunks are lit but never meshed (no visual outside view); the
  ticket radius becomes a free parameter.
- ❌ **Edits the residency predicate in the locked chunk-lifecycle code**, whose §9.6 unload-stranding
  history is exactly the deadlock class the `chunk-lifecycle` rules exist to prevent. The benchmark
  gains nothing over Option A from it.

#### Option C — Headless off-pipeline generation (rejected)

Schedule generation jobs directly, populate detached `ChunkData`, save, recycle.

- ✅ Fastest in principle: no lighting, meshing or residency.
- ❌ **Duplicates `ProcessGenerationJobs`** — structure expansion and its mods quota, pending-mod
  replay, lighting-column restore, telemetry, `ModifiedChunks` — outside `worldData`, so the two paths
  must agree forever (the FP-5 defect class).
- ❌ Saves chunks unlit, which changes what the loading pass measures (goal 5).
- ❌ Writes region files for coordinates the live pipeline may request concurrently.

### 3.2 When a target counts as done

Two tiers, because only one of them can be guaranteed:

- **Populated (required):** the chunk is resident, `IsPopulated`, and has **no tracked generation
  job** — the second clause closes the deferred-structure-mods window from §2.
- **Settled (best effort):** additionally no `LightingWork` and no lighting job in flight. Reachable
  only for chunks at Chebyshev distance ≤ `LoadDistance − 2` from the station: such a chunk's
  neighbors sit at ≤ `LoadDistance − 1`, whose own neighbors are all resident, so they can light and
  pass `AreNeighborsReadyAndLit`. The cover radius is therefore `LoadDistance − 2`.

The populated tier has a long no-progress watchdog and its expiry is an error; the settled tier has
a short one and its expiry is a counted warning. A no-progress watchdog rather than a fixed timeout,
because the time a station legitimately takes scales with view distance and with how long the panic
gate holds admissions.

### 3.3 Command surface

A subcommand of the existing `/chunk`, not a sibling `/chunks` — one command per noun, and the
registry dispatches on the name. `ChunkInfoCommand` becomes `ChunkCommand` (GUID kept):

```
/chunk info                              (unchanged)
/chunk generate <radius>                 square of chunks around the player, radius in chunks
/chunk generate <x1> <z1> <x2> <z2>      corners in voxel (block) coordinates; ~ allowed, any order
/chunk generate cancel
```

Voxel coordinates for corners, as `/teleport` and `/setblock` take; chunks for the radius, the unit
view distance is set in. A run over more than `PREGEN_CONFIRM_CHUNKS` (10 000) asks for confirmation
with the chunk count; above `PREGEN_MAX_CHUNKS` (250 000) it is refused.

### 3.4 No fallback to the flight

The flight is deleted, not kept behind a setting. Comparability with earlier captures is
established the way this project already does it — an A/B against a fresh build of the change's
exact base commit (PG-5) — and no new rollback flag is created that would later need retiring.

---

## 4. Architecture

### 4.1 Components

```
 /chunk generate ──► ChunkCommand ──► World.TryStartPregeneration ─┐
                                                                   ▼
 BenchmarkController ── yield return World.PregenerateRegion ──► ChunkPregenerator.Run (IEnumerator)
                                                                   │
            PregenerationPlan (Helpers, pure) ◄── station grid, cover radius, readiness decision
            ChunkStorageManager.CollectSavedChunks ◄── missing-set scan (read-only)
            World.MovePregenerationAnchor ◄── origin shift + transform (internal hook)
                                                                   │
                       World.PregenerationProgress / PregenerationEnded events
                                                                   ▼
                       WorldUIManager ──► CommandEngine.PostLine   (console output)
```

| Component                                | Location                                     | Role                                                                                                                                                                                                           |
|------------------------------------------|----------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `PregenerationPlan`                      | `Assets/Scripts/Helpers/`                    | Pure, stateless: rectangle normalization, cover radius from `LoadDistance`, serpentine station grid, station filter against the missing set, the readiness decision (§4.3).                                    |
| `ChunkStorageManager.CollectSavedChunks` | `Assets/Scripts/Serialization/`              | Read-only: adds every coordinate in a chunk rectangle that has a header entry on disk. No region file is created (§4.2).                                                                                       |
| `ChunkPregenerator`                      | `Assets/Scripts/` (beside `WorldJobManager`) | Owned by `World`. The station-walk coroutine, its watchdogs, the outcome, the progress counters.                                                                                                               |
| `World` facade                           | `World.cs`                                   | `PregenerateRegion(request)` (the benchmark yields it), `TryStartPregeneration(request, out error)` / `CancelPregeneration()` / `IsPregenerating`, the two events, and the internal `MovePregenerationAnchor`. |
| `ChunkCommand`                           | `Assets/Scripts/Commands/`                   | Parses, validates, confirms, starts or cancels. UnityEngine-free.                                                                                                                                              |
| `WorldUIManager`                         | `Assets/Scripts/UI/`                         | Subscribes to the events and posts progress lines.                                                                                                                                                             |

State lives on the `ChunkPregenerator` instance, not in statics, so no domain-reload reset is needed.

### 4.2 The missing set

At start, for the normalized rectangle:

```
missing = rectangle
        − { c : resident(c) ∧ IsPopulated(c) ∧ ¬GenerationJobs.Contains(c) }
        − CollectSavedChunks(rectangle)
```

`CollectSavedChunks` groups the rectangle by region through the version's `IRegionAddressCodec`. For
each region it uses the already-open `RegionFile` if there is one, opens it if the file exists, and
**skips it when no file exists** — the `OpenOrCreate` path is never taken for a region that was never
written. Each region's header is read once under its lock (`GetAllChunkMetadata`-style), not once per
chunk, so the scan costs a handful of lock acquisitions however large the rectangle.

A chunk whose asynchronous unload save has not landed yet reads as missing. That costs a redundant
visit, and the reload goes through the ordinary `LoadOrGenerateChunk` path — the same window a
player walking back into such a chunk already has. The query can never report a chunk as saved
that is not.

### 4.3 The readiness decision

`PregenerationPlan.Classify(facts) → Pending | Populated | Settled`, a pure function over:
resident, `IsPopulated`, generation job tracked, `HasAnyLightingWork`, lighting job tracked,
lighting enabled. With lighting disabled every populated chunk is `Settled`. The driver gathers the
facts on the main thread each frame and never mutates a flag. Its one write is to the save set: a target
that settles while outside `ModifiedChunks` (reloaded from a save an earlier station's unload made before it
lit) is added back, so its finished lighting reaches disk — 367 of 8 100 chunks on a fresh vd 10 run.

### 4.4 Station geometry

- Cover radius `r = LoadDistance − 2` (≥ 2, since `LoadDistance = viewDistance + 3`), stride
  `s = 2r + 1`, so cover squares tile the plane exactly.
- Station centers `minX + r + i·s` and `minZ + r + j·s` while the cover square still reaches the
  rectangle; rows alternate direction (serpentine), so consecutive stations are neighbors and each
  arrival finds the previous station's terrain resident.
- Stations whose cover square holds no missing chunk are dropped. A fully generated rectangle plans
  zero stations.
- Invariant (baselined in PG-1): every cell of the rectangle lies in exactly one cover square.

### 4.5 Run lifecycle

```
Validate ─► Scan (missing set) ─► Plan stations ─► Hold player ─┐
                                                                ▼
         ┌──────────────── for each station ──────────────────────┐
         │ MovePregenerationAnchor(station)                         │
         │ every frame: classify targets in the cover square        │
         │   populated tier: no progress for 30 s  → stall (error)  │
         │   settled tier:   no progress for 3 s   → settle-timeout │
         │ remove finished targets from the missing set; next       │
         └────────────────────────────────────────────────────────────┘
                                                                ▼
   One retry pass over stalled targets (re-planned) ─► Finish ─► Outcome + PregenerationEnded
```

**Validation refuses to start** when: no world or persistence is disabled (nothing would be stored,
and with unloading off the walk would accumulate every chunk in memory); a run is already active; a
teleport hold is active; the rectangle is empty or above `PREGEN_MAX_CHUNKS`; the world border is set
and any corner lies outside it (the fence would fight the anchor).

**Every frame** the driver also checks that `LoadDistance` still equals its snapshot; a mid-run view
distance change cancels the run (outcome `Canceled`, with a message) rather than waiting on targets
the new square no longer reaches.

**Anchor movement.** `MovePregenerationAnchor(station)` shifts the floating origin onto the station
chunk (the private `ShiftOrigin`, as `TeleportPlayer` does) and places the player at the station
center at the benchmark's flight height. The station change is a boundary crossing, so the next
`Update` runs `CheckViewDistance`, re-queues the square nearest-first and unloads the old one.

**Player handling.** The command run sets `IsTeleportHeld` for its duration, which gates the player
rigidbody's whole `FixedUpdate`. At the end it returns the player to the start cell through
`TeleportPlayer`, whose arrival hold releases the flag. The benchmark run sets
`ReturnToStart = false`: the controller teleports to the loading tour itself.

**Outcomes** — every cell has a disposition:

| Outcome               | When                                                | Disposition                                                                                                        |
|-----------------------|-----------------------------------------------------|--------------------------------------------------------------------------------------------------------------------|
| `Completed`           | missing set empty                                   | Command: async save of modified chunks (the manual-save path), player returned, "Generated N chunks in T s".       |
| `CompletedWithStalls` | targets still missing after the retry pass          | Same, plus one `Debug.LogError` naming the count and the first coordinates; the command posts it as an error line. |
| `Canceled`            | `/chunk generate cancel`, or a view-distance change | Stop at the current frame; save and return as above; report what finished.                                         |
| Torn down             | the world unloads (scene change, quit) mid-run      | The coroutine dies with `World`; quit's synchronous save covers resident chunks. Nothing to clean up.              |

The benchmark skips the closing save: its transition runs the synchronous `SaveAllModifiedChunks`
next.

### 4.6 Progress and output

`PregenerationProgress(done, total, station, stationCount)` fires once per station;
`WorldUIManager` posts a console line at each 10 % step and one on `PregenerationEnded`. The
benchmark maps `done / total` onto its HUD `Progress`.

### 4.7 Benchmark integration

- `RunEnsureGeneratedPass` keeps its name, group, phase and `regimeBearing: false`, and yields
  `World.Instance.PregenerateRegion` over `BenchmarkRouteGeometry.TourChunkBounds(loadDistance)` —
  a new method that `BuildTourChunkSet` also uses for its grid bounds, so the scanned rectangle and the
  coverage footprint cannot disagree. The footprint is a subset of that rectangle.
- The pre-phase pipeline settle goes: the pass is not a measurement.
- Trace capacity comes from the plan: `stations × (2·LoadDistance+1)² × headroom`, through a new
  `PipelineTelemetry.EstimateStationWalkCapacity`, in place of the flight-time estimate.
- `EnsureGeneratedSpeed` and `EnsureGeneratedSeconds` are deleted; `TourLengthMeters` stays — it
  describes the circuit the loading pass loops.
- The report's `Ensure sweep:` line becomes `Pre-generation:` — rectangle, missing at start, stations,
  generated, elapsed, stalls, settle timeouts — so a capture states which ensure method produced it
  (the FP-6 rule). The coverage line's "after the ensure sweep" becomes "after pre-generation";
  `BenchmarkTourCoverage` itself is unchanged.

### 4.8 CMD-6's open design questions, answered

| Question (COMMAND_CONSOLE_SYSTEM §8)               | Answer                                                                                                                                                                                             |
|----------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Normal request path under backpressure, or bypass? | The normal path, under the ordinary in-flight cap and panic gate (§3.1).                                                                                                                           |
| Blocks, reports progress, or cancellable?          | A coroutine: the benchmark yields it; the command runs it in the background with progress lines and `cancel` (§4.5–4.6).                                                                           |
| Interaction with the panic gate?                   | None designed in. The gate throttles admissions; the readiness wait simply lasts longer, and the no-progress watchdog is sized for it (§3.2).                                                      |
| Interaction with the region-file writer at scale?  | Saves go through the existing unload path, at most one resident square per station jump — the same pressure ordinary streaming produces. The only new reader is one header read per region (§4.2). |

---

## 5. Prerequisites & integration points

- **No blocking prerequisite.** Everything the design reads exists today.
- **Doc sync on landing:** `COMMAND_CONSOLE_SYSTEM.md` §8 (CMD-6 row → shipped, plus a §8.x command
  section), `CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md` §7.0 item 3 (the deferred gap closes),
  `FLIGHT_PROFILE_CAPTURE.md` (the ensure pass is no longer a flight).
- **Reserved seat for v2:** the `ChunkPregenerator` contract (rectangle in, outcome out, progress
  events) is anchor-agnostic. A future second residency anchor (§7.2) replaces
  `MovePregenerationAnchor` and the player hold without touching either caller.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                                                        |
|-------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched; the design works at chunk granularity.                                                                                                               |
| Burst jobs 100 % Burst-compatible               | No job changes — generation, lighting and meshing jobs run unmodified.                                                                                          |
| No GC / LINQ in hot paths                       | The per-frame classify loop walks a pooled list of the station's targets; sets and lists come from `HashSetPool`/`ListPool`. The one-time scan may allocate.    |
| Pooling conventions                             | `ListPool<ChunkCoord>` / `HashSetPool<ChunkCoord>` for the missing set and station targets, released on every outcome.                                          |
| No BinaryFormatter/JSON for terrain             | No format change; the saved-chunk query reads the existing region header.                                                                                       |
| BlockIDs constants, no raw IDs                  | No block references.                                                                                                                                            |
| Chunk-lifecycle invariants                      | No flag set or cleared, no gate added or bypassed; state is read on the main thread only.                                                                       |
| Coordinate spaces (WS-4)                        | Command corners arrive in voxel space and convert once via `ChunkMath.VoxelToChunk`; the plan works in `ChunkCoord`; the anchor converts through `WorldOrigin`. |
| `Commands` namespace is UnityEngine-free        | `ChunkCommand` talks only to the `World` facade.                                                                                                                |

---

## 7. Phased implementation plan

| Phase                        | Scope                                                                                                                                                                                                   | Effort | Depends on | Status        |
|------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|------------|---------------|
| **PG-0 — Saved-chunk query** | `ChunkStorageManager.CollectSavedChunks(rect, set)` + the per-region header read; no region file created for an absent region. Format unchanged.                                                        |   🟢   | —          | ✅ `238176c2` |
| **PG-1 — Plan**              | `Helpers/PregenerationPlan.cs`: normalization, cover radius, serpentine grid, station filter, `Classify`.                                                                                               |   🟢   | —          | ✅ `503189e8` |
| **PG-2 — Driver**            | `ChunkPregenerator` + the `World` facade, events, `MovePregenerationAnchor`, watchdogs, retry pass, outcomes. `Tools/UnityCli/PlayMode/PregenProbe.cs` kept as the Play-mode harness over the real API. |   🟡   | PG-0, PG-1 | ✅            |
| **PG-3 — `/chunk generate`** | `ChunkInfoCommand` → `ChunkCommand` (GUID kept, `unity-file-ops`), the generate/cancel grammar, confirmation and refusal tiers, `WorldUIManager` progress lines.                                        |   🟡   | PG-2       | —             |
| **PG-4 — Benchmark**         | `RunEnsureGeneratedPass` on the driver, `TourChunkBounds`, `EstimateStationWalkCapacity`, the report lines; delete `EnsureGeneratedSpeed`/`Seconds`; the §5 doc sync.                                   |   🟡   | PG-2       | —             |
| **PG-5 — IL2CPP A/B**        | Master captures of the base commit vs the change at vd 10 and vd 32 (`perf-benchmark`); requires a player build, so it runs only on the build owner's go-ahead.                                         |   🟡   | PG-4       | —             |

PG-0…PG-2 plus PG-4 deliver the benchmark win; PG-3 is independent of PG-4. Each phase is one
commit that compiles and keeps every suite green.

**Validation is built alongside, not after:**

- **PG-0 → `Validate Save Durability`:** a temp-directory storage manager — absent region yields
  nothing **and leaves no file behind**; a saved chunk is found; its unsaved neighbors are not.
- **PG-1 → `Validate Pipeline Backpressure`** (home of the route-geometry and coverage baselines):
  exact-tiling cover property for radii 2…40 over 1×1, 1×N, N×1, non-multiple, negative-coordinate
  and inverted-corner rectangles; empty missing set → zero stations; serpentine adjacency; the
  `Classify` truth table including lighting-disabled. One mutation proven red per new baseline group
  (`prove_red.py`).
- **PG-2 → Play mode:** `PregenProbe` re-runs both §2.2 scenarios through the real API; expected
  close to 87.9 s / 8.1 s, zero stalls, zero console errors, and a missing set that is empty on
  `Completed`. Then `Validate All`.
- **PG-3 → `Validate Command Console`:** the argument matrix against the existing stub world —
  no world, arity, radius bounds, corner normalization, `~`, confirmation above the threshold,
  refusal above the cap, persistence off, border, `cancel` when idle. `InstalledCommandCount` stays
  19 (a subcommand adds no registration). In game: `/chunk generate 8` and a cancel.
- **PG-4 → Editor benchmark**, generation speeds `200`, loading speeds `50`, at vd 10 and vd 32: the
  report must show coverage **100.0 %** when the loading pass starts, `Pre-generation: … 0 stalled`,
  and `0 generated` in the loading pass's `Chunks populated` line. The last two come from code paths
  the pre-generator does not own, so it cannot pass them by reporting its own success. Plus
  `Validate Pipeline Backpressure`.
- **PG-5 →** total-runtime reduction, and loading-pass rows within the noise rules `perf-benchmark`
  sets for a same-session base-commit A/B.

### 7.2 Extension roadmap

| Version | Extension                                                                                                                                                                                                                                                    |
|---------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | **Second residency anchor** (§3.1 Option B): pre-generate without moving the player, with no meshing cost and a free ticket radius. Needs its own `chunk-lifecycle` design pass over `IsBeyondUnloadDistance`, the request queue and the panic-gate scaling. |
| **v2**  | Background / idle pre-generation (a "prepare world" spinner, WORLDGEN report's pre-generation note) — rides on the v2 anchor.                                                                                                                                |
| **v3+** | Persisted resumable runs for very large regions — gets its own design when it becomes concrete.                                                                                                                                                              |

---

## 8. Open questions

1. **vd 32 cost and convergence.** Only vd 10 was prototyped. At vd 32 a station square is 5 041
   chunks (`LoadDistance` 35) and the panic gate closes often; PG-4's vd 32 run measures the station time and stall
   count. If stations converge slowly, the saving shrinks but completion still holds.
2. **Settled-tier timeout rate at `LoadDistance − 2`.** *Answered for vd 10 by PG-2:* 0, 0 and 4 settle
   timeouts over three fresh 8 100-chunk runs. An initial-lit tier would not help — it is met within 0.4 s of
   full settle (§2.2 note). Still open at vd 32 (PG-4).
3. **Does fully settled pre-generated terrain move the loading-pass numbers?** The flight saved its
   few generated chunks mostly settled; the walk saves them settled. PG-5's A/B answers whether the
   difference is inside the noise.

---

## 9. Rejected alternatives

| Alternative                                                | Why rejected                                                                                                                                                            | Date       |
|------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------|
| Headless off-pipeline generator (§3.1 Option C)            | Duplicates `ProcessGenerationJobs` outside `worldData`, saves unlit terrain, races the live writer.                                                                     | 2026-10-17 |
| Keeping the 50 m/s flight behind a setting                 | A second ensure path and a rollback flag to retire later; a base-commit A/B answers comparability without either.                                                       | 2026-10-17 |
| A separate `/chunks` command                               | Two commands for one noun beside the existing `/chunk`.                                                                                                                 | 2026-10-17 |
| "Populated" as the only completion tier                    | Leaving a station before lighting settles saves freshly generated chunks flagged for a full re-light (World.cs:4187), handing the loading pass work the flight did not. | 2026-10-17 |
| Waiting for global quiescence (`JobManager.HasActiveJobs`) | Also waits for meshing the whole view square at every station, which pre-generation does not need.                                                                      | 2026-10-17 |
| A fixed per-station timeout                                | The legitimate station time scales with view distance and panic-gate closures; a no-progress watchdog does not.                                                         | 2026-10-17 |
| Flying the ensure sweep twice (the interim workaround)     | Doubles a ~198 s pass and is still probabilistic — the 99.7 % vd 32 capture fell 58 chunks short after one sweep.                                                       | 2026-10-17 |

---

## Document History

* **v1.1** - PG-0…PG-2 landed: §2.2 corrected with the real-API measurements (the realistic prototype row
  measured reloads), §8 Q1's station-square size fixed (5 041 at vd 32), Q2 answered for vd 10; the driver's
  one write (re-marking reloaded settled chunks for saving, option A) is documented in §4.3.
* **v1.0** - Initial design: station-walk pre-generator shared by `/chunk generate` and the
  benchmark's Ensure Generated step; IL2CPP ensure-pass cost table and the Editor prototype
  measurements; PG-0…PG-5.

---

**Last Updated:** 2026-10-17  
**Next Review:** when PG-0 starts
