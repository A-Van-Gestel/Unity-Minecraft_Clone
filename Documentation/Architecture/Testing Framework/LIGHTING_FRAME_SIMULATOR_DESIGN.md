# Lighting Frame Simulator

**Status:** Implemented (June 2026)
**Purpose:** Deterministic reproduction of orchestration-layer lighting timing — the frame-loop scheduling decisions in `World.Update` / `WorldJobManager` (job in-flight guard, per-frame budget, mid-flight voxel edits, completion order) that the `LightingTestWorld` algebra harness alone cannot model. Built to hunt Bug 09 (cross-chunk blocklight race), which remains open/unreproduced; see §4 for current status.

---

## 1. Problem Statement

The existing `LightingTestWorld` harness exercises the **Burst job algebra** (BFS propagation, cross-chunk mod defer/drain, oracle comparison) but models an idealized scheduler: every chunk with pending work gets a job every round, jobs always schedule successfully, and there's no concept of frame boundaries or scheduling contention.

Production bugs like Bug 09 occur in the **orchestration layer** — the frame-loop logic in `World.Update` and `WorldJobManager` that decides *when* and *whether* a lighting job runs. The harness can't reproduce them because it lacks four production behaviors:

| # | Production behavior                                                                                                                                                                                             | Harness equivalent                                                                                         | Gap                                                                   |
|---|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------|
| 1 | **Scheduling guard** — `LightingJobs.ContainsKey(coord)` rejects scheduling while a job is in-flight                                                                                                            | `BeginLightingJob` throws on double-schedule (setup error, not a modeling choice)                          | No way to model "try to schedule, get rejected, BFS nodes accumulate" |
| 2 | **Frame budget** — `maxLightJobsPerFrame` caps how many jobs schedule per frame; excess chunks wait                                                                                                             | `RunToConvergence` processes every pending chunk every round                                               | No starvation / delayed scheduling                                    |
| 3 | **Concurrent voxel mutations** — fluid flow injects `AddToBlockLightQueue` + `HasLightChangesToProcess = true` while a lighting job is in-flight                                                                | No secondary modification source                                                                           | No way to model mid-flight voxel edits that re-flag a chunk           |
| 4 | **Non-deterministic completion order** — `ProcessLightingJobs` iterates a `Dictionary`, so completion order within a frame is unstable; `_completedLightJobs` makes the defer-vs-apply decision order-dependent | `RunToConvergence`: sequential row-major; `RunWaveToConvergence`: all-Begin then all-Complete in row-major | No ordering variation                                                 |

## 2. Design Overview

Add a **`LightingFrameSimulator`** class that wraps `LightingTestWorld` and replays the production orchestration loop in deterministic, controllable frame ticks. The existing `LightingTestWorld` API (`BeginLightingJob`, `CompleteLightingJob`, cross-chunk mod deferral) remains the execution engine — the simulator only adds the scheduling-decision layer on top.

```
┌─────────────────────────────────────────────────┐
│              Test Scenario (B15, B22, …)         │
│  Setup world → inject actions → run frames      │
├─────────────────────────────────────────────────┤
│            LightingFrameSimulator                │
│  Per-chunk scheduling state (in-flight flag,     │
│  HasChanges, managed BFS queues)                 │
│  RunFrame(budget, completionOrder) → schedule    │
│  up to budget, complete finished, drain mods     │
│  InjectVoxelEdit() between frames                │
├─────────────────────────────────────────────────┤
│              LightingTestWorld                    │
│  BeginLightingJob / CompleteLightingJob           │
│  Cross-chunk mod defer/drain                      │
│  Oracle, assertions                               │
└─────────────────────────────────────────────────┘
```

### 2.1 What changes vs. what stays

| Component                             | Changes?         | Notes                                                                                                                                                                             |
|---------------------------------------|------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `LightingTestWorld`                   | **Minimal**      | One new method: `TryBeginLightingJob` (returns null instead of throwing when chunk is in-flight). Minor: expose `_inFlightCoords` read-only for the simulator's scheduling guard. |
| `LightingTestWorld.Builder` (partial) | **None**         | `SetBlock`, `PlaceBlock`, `BreakBlock` continue to enqueue BFS nodes on `TestChunk.BlockQueue`/`SunQueue` and set `HasLightWork`.                                                 |
| `LightingValidationSuite.Baseline`    | **Additive**     | New scenarios using `LightingFrameSimulator`. Existing B1–B16 untouched.                                                                                                          |
| `LightingValidationSuite.KnownBugs`   | **Additive**     | Bug 09 repro scenarios using the simulator.                                                                                                                                       |
| `WorldJobManager`                     | **Extract only** | Factor scheduling-guard decision into a static pure function (shared with simulator).                                                                                             |

## 3. Detailed Design

### 3.1 Extraction: `LightingScheduleDecision` (shared logic)

To prevent the simulator from *reimplementing* the scheduling guard and silently drifting from production, extract the scheduling decision into a small static class that both `WorldJobManager.ScheduleLightingUpdate` and the simulator call.

```csharp
// Assets/Scripts/Helpers/LightingScheduleDecision.cs
public static class LightingScheduleDecision
{
    public enum Result { Schedule, AlreadyInFlight, NeighborsNotReady }

    /// <summary>
    /// Pure decision function: should a lighting job be scheduled for this chunk?
    /// Both production (WorldJobManager) and the editor validation simulator
    /// call this to ensure identical guard logic.
    /// </summary>
    public static Result Evaluate(bool hasJobInFlight, bool hasLightChanges, bool neighborsReady)
    {
        if (hasJobInFlight) return Result.AlreadyInFlight;
        if (!hasLightChanges) return Result.Schedule; // nothing to do, but caller may force
        if (!neighborsReady) return Result.NeighborsNotReady;
        return Result.Schedule;
    }
}
```

**Production callsite** (`WorldJobManager.ScheduleLightingUpdate`):

```csharp
// Before (current):
if (LightingJobs.ContainsKey(chunkCoord)) return false;
if (!_world.AreNeighborsDataReady(chunkCoord)) { ... return false; }

// After (refactored):
var decision = LightingScheduleDecision.Evaluate(
    LightingJobs.ContainsKey(chunkCoord),
    chunkData.HasLightChangesToProcess,
    _world.AreNeighborsDataReady(chunkCoord));
if (decision != LightingScheduleDecision.Result.Schedule) { ... return false; }
```

This is a **behavior-neutral refactor** — committed alone before any simulator code. The guard logic moves from inline to a shared pure function; the production behavior is identical.

### 3.2 `LightingFrameSimulator`

```
Assets/Editor/Validation/Lighting/Framework/LightingFrameSimulator.cs
```

#### Per-chunk scheduling state

No separate scheduling-state class is needed. The simulator reuses the harness's existing per-chunk state as the single source of truth:

- `TestChunk.HasLightWork` — "chunk has pending BFS work / should attempt to schedule" (already exists)
- `LightingTestWorld._inFlightCoords` — "a job is in-flight for this chunk" (already exists; the simulator reads it via `IsChunkInFlight`)

The scheduling guard is therefore simply `HasLightWork && !IsChunkInFlight(coord)`. This faithfully mirrors production's `HasLightChangesToProcess` lifecycle: the flag is cleared at schedule time (when `BeginLightingJob` drains the managed queues) but re-set by any `AddToBlockLightQueue` call that lands during a flight — including the redundant-set case where a fluid edit re-flags an already-in-flight chunk. `CompleteLightingJob` leaves `HasLightWork` true if new nodes were enqueued mid-flight, so the chunk re-schedules on the next frame. `HasLightWork`
thus serves both roles (pending-BFS-nodes and scheduling-trigger) without a parallel flag.

#### Core API

```csharp
public sealed class LightingFrameSimulator : IDisposable
{
    private readonly LightingTestWorld _world;

    /// <summary>
    /// Constructs a frame simulator wrapping the given test world.
    /// The test world must already be set up (chunks created, initial lighting done).
    /// </summary>
    public LightingFrameSimulator(LightingTestWorld world) { _world = world; }

    /// <summary>
    /// Executes one simulated frame tick:
    /// 1. Complete all jobs whose flights are "done" (all flights in the harness complete
    ///    synchronously on Begin, but we hold them in a pending list to control completion order).
    /// 2. Apply cross-chunk mods (deferred mods drain automatically via CompleteLightingJob).
    /// 3. Schedule new jobs for chunks with HasLightWork, up to the budget.
    ///
    /// Returns the number of jobs completed + scheduled this frame.
    /// </summary>
    /// <param name="budget">Max jobs to schedule this frame (mirrors maxLightJobsPerFrame).</param>
    /// <param name="completionOrder">Order in which to complete pending flights. Null = FIFO.</param>
    public FrameResult RunFrame(int budget = 32, int[] completionOrder = null);

    /// <summary>
    /// Injects a voxel modification between frames, simulating fluid flow or a player edit.
    /// Calls through to LightingTestWorld.Builder methods and ensures HasLightWork is set.
    /// Unlike PlaceBlock/BreakBlock (which model a full block swap), this models a targeted
    /// voxel change (e.g., water flowing into a position) that enqueues BFS nodes.
    /// </summary>
    public void InjectVoxelEdit(Vector2Int chunkCoord, Vector3Int localPos,
                                 ushort newBlockId, bool affectsLight = true);

    /// <summary>
    /// Runs frames until all chunks converge (no HasLightWork anywhere) or the frame
    /// budget is exhausted. This is the frame-aware equivalent of RunToConvergence.
    /// </summary>
    /// <param name="maxFrames">Maximum number of frame ticks.</param>
    /// <param name="budgetPerFrame">Jobs per frame.</param>
    /// <returns>Number of frames taken, or -1 if not converged.</returns>
    public int RunToConvergence(int maxFrames = 100, int budgetPerFrame = 32);

    /// <summary>
    /// Runs frames with a budget of 1 job per frame — maximum starvation pressure.
    /// This models the worst case where other systems consume all but one lighting slot.
    /// </summary>
    public int RunToConvergenceSingleSlot(int maxFrames = 200);
}

public struct FrameResult
{
    public int JobsCompleted;
    public int JobsScheduled;
    public int ChunksStarved; // had work but couldn't schedule (budget or in-flight)
}
```

#### Frame tick internals

Each `RunFrame` call models one iteration of `World.Update`'s lighting phase:

```
RunFrame(budget):
    // Phase 1: Complete pending flights (mirrors ProcessLightingJobs)
    // The completion order can be controlled to test ordering sensitivity.
    completedThisFrame = []
    for flight in pendingFlights (in completionOrder):
        result = _world.CompleteLightingJob(flight)
        completedThisFrame.add(flight.Coord)
        // CompleteLightingJob already handles:
        //   - Merging light output
        //   - Draining deferred mods
        //   - Applying cross-chunk mods (deferring those targeting in-flight chunks)
        //   - Setting HasLightWork on affected chunks
        // No additional logic needed here.

    // Phase 2: Schedule new jobs (mirrors World.Update lighting scan)
    scheduled = 0
    for coord in allChunks (deterministic order):
        if scheduled >= budget: break
        chunk = _world.GetChunk(coord)
        if !chunk.HasLightWork: continue
        if _world.IsInFlight(coord): continue  // ContainsKey guard
        flight = _world.BeginLightingJob(coord)
        pendingFlights.add(flight)
        scheduled++

    return FrameResult { ... }
```

**Key detail — "pending flights" for completion order control:**

In the current `LightingTestWorld`, `BeginLightingJob` doesn't actually schedule an async job — it creates the `NeighborhoodLightingJob` struct, and `CompleteLightingJob` calls `.Run()` synchronously. The "flight" is purely a logical concept for snapshot isolation.

The simulator holds flights in a `_pendingFlights` list between frames. On the next `RunFrame`, it completes them (which runs the Burst job synchronously and merges results). This lets us control:

- **Completion order**: shuffle or reverse the list to test order sensitivity
- **Multi-frame flights**: hold a flight across multiple frames (skip its completion) to model a job that takes longer than one frame to complete

#### Completion order strategies

```csharp
public enum CompletionOrderStrategy
{
    /// <summary>FIFO — same order they were scheduled. Deterministic baseline.</summary>
    Fifo,

    /// <summary>Reverse of scheduling order. Exercises the _completedLightJobs
    /// defer-vs-apply ordering dependency in the worst direction.</summary>
    Reverse,

    /// <summary>Seeded shuffle for reproducible randomized testing.</summary>
    Seeded,
}
```

The `Reverse` strategy is particularly important for Bug 09: if Chunk A's job completes before Chunk B's, A's cross-chunk mods targeting B are deferred. If B completes first, A's mods apply directly. Reversing the order flips which path is taken.

### 3.3 Interleaved mutation hooks

For Bug 09, the critical sequence is:

```
Frame 0: Player breaks lamp at chunk border → BFS removal nodes queued → job scheduled for Chunk A
Frame 0: Fluid flows into vacated position → new BFS nodes queued for Chunk A
         (but Chunk A's job is already in-flight with the OLD BFS nodes)
Frame 0: Player places lamp again → BFS emission nodes queued for Chunk A
         (HasLightChangesToProcess re-set, but job can't re-schedule: ContainsKey guard)

Frame 1: Chunk A's removal job completes → cross-chunk removal mods sent to Chunk B
         Chunk A re-scheduled with fluid + placement BFS nodes
         But: Chunk B might also have been scheduled with the removal mods as input...
```

The simulator models this via explicit `InjectVoxelEdit` calls between `RunFrame` invocations:

```csharp
// Scenario setup
sim.RunFrame(budget: 32);  // Schedules removal job for chunk A

// Between frames: fluid fills the broken position + player re-places lamp
sim.InjectVoxelEdit(chunkA, brokenPos, BlockIDs.Water);
world.PlaceBlock(lampWorldPos, TestBlockPalette.LampWhite);

sim.RunFrame(budget: 32);  // Completes removal job, schedules new job with accumulated nodes
// ...
```

### 3.4 Changes to `LightingTestWorld`

Minimal — the simulator needs read access to two pieces of internal state:

1. **`IsChunkInFlight(Vector2Int coord)`** — public read-only check against `_inFlightCoords`. This replaces the current `throw` behavior with a queryable guard.

```csharp
// New method on LightingTestWorld
public bool IsChunkInFlight(Vector2Int coord) => _inFlightCoords.Contains(coord);
```

2. **`TryBeginLightingJob`** — optional convenience that returns null instead of throwing when a chunk is in-flight. The simulator can also just check `IsChunkInFlight` before calling `BeginLightingJob`.

The existing `BeginLightingJob` **keeps its throw** — direct test scenarios that double-schedule have a setup error and should fail loudly. The simulator handles the guard itself.

### 3.5 `HasLightWork` lifecycle in the simulator

This is the subtlest part. In production:

| Event                                           | `HasLightChangesToProcess`              | Managed BFS queue                                         |
|-------------------------------------------------|-----------------------------------------|-----------------------------------------------------------|
| `AddToBlockLightQueue` (voxel edit)             | Set `true`                              | Node enqueued                                             |
| `ScheduleLightingUpdate` succeeds               | Cleared `false`                         | Drained into NativeQueue for job                          |
| `ScheduleLightingUpdate` rejected (ContainsKey) | Stays `true`                            | Nodes stay in managed queue                               |
| Cross-chunk mod applied                         | Set `true` (via `AddToBlockLightQueue`) | Wake-up node enqueued                                     |
| Job completes not-stable                        | Set `true`                              | (no new nodes; the job re-runs with the same light field) |

In the harness, `TestChunk.HasLightWork` and the managed queues (`SunQueue`, `BlockQueue`) already model this correctly:

- `PlaceBlock`/`BreakBlock` enqueue nodes and set `HasLightWork = true`
- `BeginLightingJob` drains queues and sets `HasLightWork = false`
- `CompleteLightingJob` sets `HasLightWork = true` if not stable or if cross-chunk mods wake up the chunk

The only thing the simulator adds is: **when `BeginLightingJob` is skipped (ContainsKey guard), `HasLightWork` stays true and BFS nodes stay in the managed queue.** This happens naturally — the simulator simply doesn't call `BeginLightingJob` for that chunk, so nothing drains the queue or clears the flag.

## 4. Test Scenarios — Status

The simulator was built to reproduce Bug 09 (orchestration-layer cross-chunk blocklight races). A family of repro attempts (the K09 series) was authored, each layering more production pressure: ContainsKey scheduling rejection, single-slot budget starvation, fluid contention mid-flight, held multi-frame flights, and seeded shuffling of both completion and scheduling order.

**Status so far: none of the attempts has reproduced Bug 09 — every modeled scenario converges to the oracle.** This is **not** a proof that the orchestration layer is correct; it only means the specific timing configurations modeled to date do not trigger the bug. **Bug 09 therefore remains open / unreproduced — not fixed.** Per the validation-driven-bugfix protocol, each non-reproducing attempt was promoted to a permanent baseline so the scenario it covers is guarded against future regressions. (A scenario that *did* reproduce the bug would instead
remain in `LightingValidationSuite.KnownBugs` as a failing repro until fixed; none currently do.) Reproducing Bug 09 likely needs a configuration not yet modeled — the simulator is the tool to keep trying.

### Regression baselines (non-reproducing Bug 09 scenarios)

| Baseline | What it exercises                                                                                |
|----------|--------------------------------------------------------------------------------------------------|
| **B15**  | Direct-harness break+place race — single- then both-chunk in-flight                              |
| **B16**  | Fluid break→water→place under a held flight + single-slot budget                                 |
| **B22**  | Dual-chunk held flights with interleaved completion (from K09f)                                  |
| **B26**  | "Kitchen sink": shuffled completion+scheduling with fluid contention, 50 seeds (from K09j)       |
| **B27**  | Shuffled scheduling under extreme budget pressure, 1 job/frame, 50 seeds (from K09k)             |
| **B28**  | Shuffled dual-chunk interleaved flights, 50 seeds (from K09l)                                    |
| **B29**  | Combined stress — every harness layer simultaneously, 50 seeds (from K09m)                       |
| **B40**  | Cross-chunk geometry fuzz — 50 randomized border geometries (geometry-fuzzing `FindFailingSeed`) |

The authoritative list lives in `LightingValidationSuite.Baseline.cs`; the titles above summarize the registered `Scenario` descriptions. The seeded scenarios (B26–B29) sweep 50 seeds each via `LightingFrameSimulator.FindFailingSeed`, randomizing both `RunFrame`'s completion order (`CompletionOrder.Shuffled`) and the per-frame scheduling order; B40 additionally fuzzes the border geometry.

**Non-linear numbering.** The original K09 series produced more promoted baselines than survive today. Because many single-instance permutations overlapped in coverage, they were **consolidated on 2026-06-14** (see `LIGHTING_VALIDATION_HARNESS_FIDELITY.md` §5) — B15 and B16 are the deterministic *representatives* the permutations folded into, backed by the seeded sweeps (B26–B29) and the geometry fuzz (B40). The retired numbers **B17–B21 / B23–B25 are intentionally unused**; every behavior they exercised is still covered by the survivors. The Bug 09
entry in `LIGHTING_BUGS.md` is the authoritative cross-reference for this mapping. (Note: the `B9` baseline is also tagged "Bug 09" but guards a separate opaque-volume containment defect, not the orchestration race — it is not a frame-simulator scenario.)

## 5. File Layout

```
Assets/Editor/Validation/Lighting/Framework/
├── LightingTestWorld.cs              (existing — add IsChunkInFlight)
├── LightingTestWorld.Builder.cs      (existing — no changes)
├── LightingFrameSimulator.cs         (NEW)
├── LightingAssert.cs                 (existing — no changes)
├── LightingOracle.cs                 (existing — no changes)
├── TestBlockPalette.cs               (existing — no changes)

Assets/Scripts/Helpers/
├── LightingScheduleDecision.cs       (NEW — extracted from WorldJobManager)

Assets/Scripts/WorldJobManager.cs     (refactor to use LightingScheduleDecision)
```

## 6. Implementation Plan

1. **Extract `LightingScheduleDecision`** — behavior-neutral refactor of `WorldJobManager.ScheduleLightingUpdate`'s guard logic into a shared static function. Own commit. Build both assemblies.

2. **Add `IsChunkInFlight` to `LightingTestWorld`** — one-line public method. Own commit.

3. **Implement `LightingFrameSimulator`** — the frame-tick loop, pending-flight management, completion-order strategies, `InjectVoxelEdit`, `RunToConvergence` with budget. Own commit. Build editor assembly.

4. **Write Bug 09 repro scenarios (the K09 series)** — repro attempts using the simulator. Per the validation-driven-bugfix protocol, any that reproduce stay as known-bug scenarios; any that converge are promoted to baselines. Outcome: all converged and were promoted (B15, B16, B22, B26–B29) — see §4. Own commit.

5. **Update `LIGHTING_BUGS.md`** — note the new scenario IDs and whether they reproduced. Same commit as step 4.

## 7. Risks and Mitigations

| Risk                                                                     | Mitigation                                                                                                                                                                                                                                                                                                                                                                                                          |
|--------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Simulator drifts from production scheduling logic                        | The `LightingScheduleDecision` extraction ensures the guard logic is shared code. The completion/merge/defer path is already shared (both use `CompleteLightingJob` → `CrossChunkLightModApplier`).                                                                                                                                                                                                                 |
| Bug 09 still doesn't reproduce (needs even more specific timing)         | Promote scenarios to baselines. The budget-pressure and completion-order tests are valuable regression guards regardless. Note: if the bug is truly in the `Dictionary` iteration randomness of `ProcessLightingJobs`, the `Reverse` completion order should catch it.                                                                                                                                              |
| `InjectVoxelEdit` doesn't faithfully model fluid flow                    | Fluid flow is a sequence of `SetVoxel` + `QueueLightUpdate` calls. The simulator's `InjectVoxelEdit` calls the same `LightingTestWorld.Builder` methods that enqueue BFS nodes. The only thing missing is fluid *simulation logic* (which direction water flows, how many ticks it takes) — but for the lighting bug, all that matters is that *a voxel changes and BFS nodes are queued while a job is in-flight*. |
| Performance: frame-by-frame simulation is slower than `RunToConvergence` | Each frame tick runs 0–N Burst jobs synchronously (same as current baselines). Budget-limited scenarios run fewer jobs per frame but more frames. Expected total runtime increase: negligible (a few hundred milliseconds per scenario).                                                                                                                                                                            |

## 8. Open Questions

1. **Should the simulator model `maxMeshRebuildsPerFrame` or meshing gates?** Probably not — the lighting bug is purely in the lighting scheduling layer. Meshing is downstream and doesn't feed back into lighting decisions. Keep scope minimal.

2. ~~**Should the simulator model the `_chunksNeedingLightWork` HashSet iteration order?**~~ **Implemented (June 2026).** The constructor accepts an optional `int? seed` parameter. When set, Phase 2 (scheduling) shuffles the chunk iteration order via Fisher-Yates each frame, and `CompletionOrder.Shuffled` randomizes Phase 1 (completion) as well. `FindFailingSeed` sweeps many seeds to find any that produces an oracle mismatch (two overloads — one also fuzzes geometry). Four scenarios (B26–B29) exercise shuffled ordering with 50 seeds each — all converge
   to the oracle.

3. **Should we model `RemainingEdgeCheckRounds` in the simulator?** Only if we're trying to reproduce Bug 05 variants. For Bug 09 (blocklight at a border), edge checks are not involved. Add later if needed.

4. **Multi-frame flights**: ~~Should the simulator support holding a flight across multiple frames?~~ **Implemented (June 2026).** `RunFrame` accepts an optional `completionPredicate` (`Func<LightingJobFlight, int, bool>`) that controls which flights complete each frame. Flights rejected by the predicate carry over to the next frame with their in-flight status preserved. Static factories `MinAge(n)`, `OnlyChunks(...)`, and `ExceptChunks(...)` cover common patterns. B22 exercises multi-frame held flights with stale-snapshot interleavings (B28/B29 add
   seeded shuffling on top); B15 and B16 also hold flights across frames — all converge to the oracle.
