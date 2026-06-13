# Lighting Frame Simulator — Design Draft

**Status:** Implemented
**Date:** June 2026
**Purpose:** Enable deterministic reproduction of orchestration-layer lighting bugs (Bug 09, potentially Bug 05 variants) that the current `LightingTestWorld` harness cannot model.

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
│              Test Scenario (B17, K09c, …)        │
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

#### Per-chunk state (mirrors `ChunkData` scheduling fields)

```csharp
private class ChunkSchedulingState
{
    public bool HasLightChangesToProcess;
    // In-flight tracking is delegated to LightingTestWorld._inFlightCoords
}
```

The `HasLightChangesToProcess` flag in the simulator mirrors the production flag. It's set when:

- A voxel modification enqueues BFS nodes (via `LightingTestWorld.Builder.PlaceBlock` / `BreakBlock` / `SetBlock`)
- Cross-chunk mods are applied (wake-up nodes set `HasLightWork`)
- A job completes not-stable

It's cleared when a job is successfully scheduled (matching production's line 434: `chunkData.HasLightChangesToProcess = false`).

**Why not reuse `TestChunk.HasLightWork`?** `HasLightWork` serves double duty in the current harness — it's both the "has pending BFS nodes" flag and the scheduling trigger. The simulator needs to separate these: `HasLightWork` drives whether there are BFS nodes to drain into a job, while `HasLightChangesToProcess` drives whether the scheduler *tries* to schedule. In production these are the same field, but the simulator needs to model the case where `HasLightChangesToProcess` is true but scheduling fails (ContainsKey guard), and then a *new* voxel edit
sets it again redundantly.

After more thought: we can keep using `TestChunk.HasLightWork` as the single source of truth (it already tracks "chunk has pending BFS work"), and the simulator's scheduling guard is simply: `HasLightWork && !InFlight`. The redundant-set case (fluid edit while in-flight re-sets `HasLightWork`) is naturally handled because `CompleteLightingJob` doesn't clear `HasLightWork` if new nodes were enqueued during the flight. This matches production: `HasLightChangesToProcess` is cleared at schedule time, but re-set by any `AddToBlockLightQueue` call during the
flight.

**Revised:** No separate `ChunkSchedulingState` class needed. The simulator uses:

- `TestChunk.HasLightWork` — "should attempt to schedule" (already exists)
- `LightingTestWorld._inFlightCoords` — "is in-flight" (already exists, needs read access)

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

## 4. Test Scenarios Enabled

### K09c: Single-frame break+place with ContainsKey rejection

The simplest Bug 09 repro attempt that the current harness can't model:

```
Setup: 3×3 grid, superflat, lamp at (31, 64, 16) — chunk A border with chunk B.

Frame 0: Break lamp → removal BFS nodes queued → job scheduled for chunk A.
         Place lamp (same position) → emission BFS nodes queued.
         ContainsKey guard prevents re-scheduling.
         (Chunk A now has removal job in-flight + pending emission nodes in managed queue.)

Frame 1: Removal job completes → cross-chunk removal mods sent to chunk B.
         Emission job re-schedules for chunk A (HasLightWork still true).
         Chunk B scheduled with removal mods applied.

Frame 2+: Convergence.

Assert: Oracle comparison. The emission should propagate to chunk B.
```

### K09d: Break+place with fluid contention under budget pressure

```
Setup: Same as K09c but budget = 1 (one job per frame).

Frame 0: Break lamp → job scheduled for chunk A. Budget exhausted.
         Fluid fills broken position (InjectVoxelEdit: water at lamp pos).
         Place lamp again → emission nodes queued. Can't schedule (in-flight + budget).

Frame 1: Removal job completes. Chunk A has fluid + emission nodes pending.
         Budget = 1: only chunk A or chunk B can schedule, not both.
         Cross-chunk removal mods deferred for chunk B (or applied, depending on order).

Frame 2+: Convergence under single-slot pressure.

Assert: Oracle comparison.
```

### K09e: Completion order sensitivity

```
Setup: Same as K09c but both chunk A and chunk B have jobs in-flight.
Run with CompletionOrder = Reverse to exercise the case where chunk B
completes before chunk A (so A's cross-chunk mods target a completed chunk
rather than a deferred one).
```

### B17+: Budget-pressure convergence baseline

Even if K09c/d/e converge correctly (meaning the bug needs even more specific timing), the scenarios become baselines guarding that the engine converges under budget-limited scheduling — something no current baseline tests.

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

4. **Write K09c/d/e scenarios** — Bug 09 repro attempts using the simulator. If they reproduce (fail), they stay as known-bug scenarios. If they converge correctly, promote to baselines (B17+) per the validation-driven-bugfix protocol. Own commit.

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

2. **Should the simulator model the `_chunksNeedingLightWork` HashSet iteration order?** In production, the dirty-set is iterated as a `HashSet` (non-deterministic). The simulator could take an explicit scheduling-order parameter (like completion order) to test sensitivity. Worth adding if the basic repro attempts still converge.

3. **Should we model `RemainingEdgeCheckRounds` in the simulator?** Only if we're trying to reproduce Bug 05 variants. For Bug 09 (blocklight at a border), edge checks are not involved. Add later if needed.

4. **Multi-frame flights**: ~~Should the simulator support holding a flight across multiple frames?~~ **Implemented (June 2026).** `RunFrame` accepts an optional `completionPredicate` (`Func<LightingJobFlight, int, bool>`) that controls which flights complete each frame. Flights rejected by the predicate carry over to the next frame with their in-flight status preserved. Static factories `MinAge(n)`, `OnlyChunks(...)`, and `ExceptChunks(...)` cover common patterns. Three scenarios (B20, B21, B22) exercise multi-frame flight lifetimes with stale-snapshot
   interleavings — all converge to the oracle.
