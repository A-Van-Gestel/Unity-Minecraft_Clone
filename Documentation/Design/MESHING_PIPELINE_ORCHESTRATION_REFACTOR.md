# Meshing Pipeline Orchestration Refactor (MP-*)

**Version:** 1.0
**Date:** 2026-07-06
**Status:** Proposed design — not implemented.
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

> Clean-up / refactor plan for the meshing pipeline's **orchestration layer** — request routing,
> the `MeshBuildQueue` drain, the `ScheduleMeshing` gates, `ProcessMeshJobs`, and the apply/draw
> tail — the sibling of [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md)
> (LP-*). The job/post-process/renderer *stages* are already strongly suite-guarded (B1–B21 +
> MR-1..7 shipped); the gap is everything *between* them: **no suite covers request → queue →
> gate → completion, and that untested loop is exactly where GS-5 (graph visibility culling) will
> land.** The two most important decisions: **a rebuild request arriving while that chunk's mesh
> job is in flight is currently dequeued and dropped against the pre-request snapshot (a
> lost-update window — fixed by leaving it queued), and the GS-5 §7.3 renderer-ownership split is
> scheduled here as its own phase so the culler never lands on a three-owner `SetActive` surface.**
> PRIMARY goal is clarity/testability + culling-readiness; performance is SECONDARY (one deferred,
> measure-first extension). Zero on-disk change in every phase — meshes and any future
> connectivity masks are derived data (culling doc §8), so no AOT migration exists anywhere in
> this plan.

**Audited:** 2026-07-06, at commit `72ad121` (branch `feat/async-lighting-validation-suite`).
Findings are from static review of `World.cs` (Update mesh drain L1686–1743, `ChunksToDraw` drain
L1731–1741, `RequestChunkMeshRebuild` L2273–2283 + all 10 call sites, `NotifyChunkModified`
L1798–1839, `UnloadChunks` mesh-queue removals L2449/L2590, `CompleteAndProcessMeshJobs`
L1351–1361), `WorldJobManager.cs` (`ScheduleMeshing` L297–420, `ProcessMeshJobs` L875–929,
`ReleaseMeshingJobInputs` L959–966), `Helpers/MeshBuildQueue.cs` (full),
`Chunk.cs` (`ApplyMeshData`/`CreateMesh` L535–595, `Reset`/`Release` L86–165,
`PlayChunkLoadAnimation` L627–644), `SectionRenderer.cs` (full — `UpdateMeshNative`, `Clear`,
MR-2/3/4 state), and `Data/WorldData.cs:223`. Verified: **no `forceRenderingOff` exists in the
codebase** (the culling doc's Phase 0.5 is still open, re-confirming its 2026-07-02 note). Line
numbers are anchors for the executor, not contracts — re-verify before editing.

> **Drift note (2026-07-22):** `CompleteAndProcessMeshJobs` (censused above) was deleted as dead
> code out of band — Rider `safe_delete` confirmed zero usages. Runtime mesh completion lives in
> the per-frame `ProcessMeshJobs` drain; shutdown completion in `WorldJobManager.Dispose()`. No
> MP phase is affected; the method appears in no phase's scope.

**Relationship to other documents:**

- [`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) — the future GS-5
  design this plan makes room for: MP-5 executes its **Phase 0.5 ownership split** (§7.3), and §5
  here bakes its **§7.4 staleness rule** (mask published atomically with the mesh apply) and
  **GS-6 presentation seam** into the phases as design constraints. GS-5's Phases 1–3 stay in that
  doc — this plan does not build the culler.
- [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md) — the LP-* sibling;
  shared patterns (pure-decision extraction, invariant probes, completion-pass skeleton) and two
  explicit coordination points (§7 notes on LP-2's gate predicate and LP-3's lighting driver).
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) —
  §5.3 (meshing pipeline flow) and §9.5 (mesh-queue population race) are restructured/answered by
  MP-1/MP-3; every phase doc-syncs it.
- [`../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md)
  — the section-meshing architecture (its §8 already points at the culling design); MP-5/MP-6
  touch behavior it describes.
- [`../Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
  — the suite this plan extends (tip **B21**; MH-7/MH-8 stay owned there). MP-2/MP-4 add the
  orchestration coverage that doc's §4 scoped out of the *job* harness.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — MR-1..7 shipped
  (guarded by this suite); MR-8/GS-5/GS-6 keep their IDs and sequencing (report §recommendation:
  ownership split early → GS-5 → GS-6 → MR-8). MP-5 is that "ownership split early" item.
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — NS-3's
  convergence assertion family ("every chunk eventually reaches lit + meshed"); MP-1's probes and
  MP-2's scheduling harness are its meshing-side groundwork.

---

## 1. Goals & non-goals

### Goals

1. **Close the orchestration coverage gap** — the request → queue → gate → schedule → completion
   loop is production-only code today (§2.3); extract its decisions into shared pure code and
   baseline them, the LP/HF-4 pattern.
2. **Fix the in-flight lost-update window** (§2.4 F1) — a rebuild request during a chunk's mesh
   flight must survive to a post-completion rebuild, not be silently dropped against the stale
   snapshot.
3. **Make the pipeline GS-5-ready** — execute the §7.3 ownership split (MP-5), preserve the single
   apply site as the future mask-publish point (§5), and keep per-section derived data shapes.
4. **Retire the vestigial draw stage's staleness** (§2.4 F4) — the post-MR-5 `ChunksToDraw` stage
   only triggers load animations and can act on a recycled chunk's wrong lifecycle.
5. **Preserve behavior at every phase boundary except the two named fixes (MP-3, MP-6)** — meshing
   suite B1–B21 + mesh-queue suite green throughout; the two behavior changes ship with their own
   prove-red baselines and in-game confirmation.
6. *(SECONDARY)* A measured-only extension for the drain's O(queue) gate re-probing (§8 roadmap).

### Non-goals (v1)

- **Building GS-5 itself** (connectivity masks, `VisibilityManager`, BFS/PVS) — owned by
  [`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) Phases 1–3. MP-5
  delivers only its Phase 0.5 prerequisite.
- **GS-6 (BatchRendererGroup) and MR-8 (greedy meshing)** — own design docs per the performance
  report; MH-8's merge-invariant oracle stays gated on MR-8's doc.
- **MH-7 (custom/cross/lava palette)** — owned by the meshing fidelity doc; built alongside the
  feature it guards.
- **Changing any gate's semantics** — `AreNeighborsMeshReady` stays deliberately relaxed (the
  wave-front deadlock fix, pipeline doc §9.3); the center light-flag gate stays. MP-2 re-houses,
  never redesigns. (LP-2 separately re-houses the *neighbor* predicate — see the §7 coordination
  note.)
- **Redesigning `MeshBuildQueue`** — MT-1 shipped, guarded by its own 9-baseline suite; the class
  survives unchanged. Park/promote for the drain is a v2 extension, measure-first (§8).
- **Mesh data-format or job-internals work** — MR-2..7 shipped and suite-pinned; the job stages
  are not touched by any phase (except MP-7's naming-only field rename).

---

## 2. Current state — the orchestration surface

### 2.1 Stage map (who does what, today)

| # | Stage          | Code                                                                                                                                                                                             | Suite coverage today                                                       |
|---|----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------|
| 1 | **Request**    | `World.RequestChunkMeshRebuild(Chunk, immediate)` (W:2273) — drops null/inactive chunks                                                                                                          | ❌ none                                                                     |
| 2 | **Queue**      | `MeshBuildQueue` (MT-1): O(1) dedup, immediate→head+promote, normal→tail, O(1) remove                                                                                                            | ✅ own suite (9 baselines, incl. B9 promotion)                              |
| 3 | **Drain**      | `World.Update` step 7 (W:1694–1728): per-frame budget + OM-1 in-flight cap (re-checked per iteration), null/inactive→remove, schedule-ok→remove, gate-fail→leave in place                        | ❌ none                                                                     |
| 4 | **Gates**      | `WorldJobManager.ScheduleMeshing` (WJM:297–322): in-flight → `return true` (!); center `HasLightChangesToProcess/NeedsInitialLighting` (skipped when lighting disabled); `AreNeighborsMeshReady` | ❌ none (the lighting fidelity doc's **B5** scoped this out of *its* suite) |
| 5 | **Jobs**       | `MeshGenerationJob` + chained `MeshPostProcessJob` (MR-5), pooled inputs + `MeshOutputPool` output (MR-6)                                                                                        | ✅ meshing suite B1–B11, B17–B21 (incl. cross-chunk substrate MH-10/11)     |
| 6 | **Completion** | `ProcessMeshJobs` (WJM:875–929): HF-2 two-stage fault isolation **inline**, release-inside/remove-after, central output return                                                                   | ❌ pass bookkeeping production-only (mesh analog of lighting fidelity B7)   |
| 7 | **Apply**      | `Chunk.ApplyMeshData` → per-section `SectionRenderer.UpdateMeshNative` (MR-2 layout, MR-3 materials, MR-4 bounds; `SetActive` by vertex count)                                                   | ✅ renderer fixture B12–B16                                                 |
| 8 | **Draw tail**  | `ChunksToDraw.Enqueue` in ApplyMeshData → `World.Update` step 8 dequeues **one per frame** → `Chunk.CreateMesh` → `PlayChunkLoadAnimation` (once per lifecycle)                                  | ❌ none                                                                     |

### 2.2 Request-site census (stage 1 inputs — the ground truth for MP-1/MP-2)

| Trigger                                                   | Site                                                         | Priority                   |
|-----------------------------------------------------------|--------------------------------------------------------------|----------------------------|
| Generation completed (chunk has active visual)            | `WJM:836`                                                    | normal                     |
| Lighting stabilized — center                              | `WJM:1074` (via `_chunksToRebuildMesh`)                      | **immediate**              |
| Lighting stabilized — 4 cardinal neighbors                | `World.RequestNeighborMeshRebuilds` → `QueueNeighborRebuild` | normal                     |
| Disk-load-stable chunk                                    | `World.cs:926`                                               | normal                     |
| Voxel edit — center + border cardinals + corner diagonals | `NotifyChunkModified` (W:1798–1839)                          | from `mod.ImmediateUpdate` |
| Cross-chunk voxel write landing in a loaded chunk         | `WorldData.cs:223`                                           | **immediate**              |
| View-distance activation (new / re-activated, populated)  | `World.cs:2616` / `:2635`                                    | normal                     |
| `smoothLighting` setting change (all active chunks)       | `World.cs:3669`                                              | normal                     |

Removal sites: `UnloadChunks` (W:2449) and view-distance deactivation (W:2590) call
`_meshBuildQueue.Remove(coord)`; `Clear()` on world teardown.

### 2.3 What no suite can currently red

- **Request routing** — that each census row fires with the right priority (an accidentally-lost
  `immediate` silently degrades player-edit latency; a lost re-request site recreates pipeline-doc
  §9.5's orphaning).
- **Drain policy** — budget/cap interplay (the OM-1 per-iteration cap re-check), leave-in-place
  retry, null/inactive purge.
- **Gate composition** — the order and effect of the three `ScheduleMeshing` gates, including the
  `enableLighting=false` bypass and the in-flight arm's *dequeue* consequence (F1).
- **Completion-pass bookkeeping** — the mesh pass's HF-2 fault isolation is inline; the lighting
  twin was extracted (`LightingCompletionPass`) precisely so its suite could replay multi-job
  fault ordering (baseline B65). The mesh pass has no such replay.
- **Draw-tail lifecycle** — stale/recycled `Chunk` references in `ChunksToDraw` (F4).

This is the meshing analog of the lighting suite's pre-AS-2 B6/B7 state, and the meshing half of
NS-3's "every chunk eventually reaches lit + meshed" convergence family.

### 2.4 Findings

| #  | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |      Addressed by      |
|----|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:----------------------:|
| F1 | **In-flight request drop (lost update).** `ScheduleMeshing` returns `true` when `MeshJobs.ContainsKey` (WJM:301–302), and the drain treats `true` as scheduled → `RemoveCurrent()` (W:1722–1724). A rebuild requested *while that chunk's mesh job is in flight* is therefore dequeued and dropped — but the in-flight job snapshotted its inputs before the request, so the on-screen mesh stays stale. Masked in practice because most edits also dirty lighting, whose stabilization re-requests the mesh (WJM:1074); exposed with `enableLighting = false`, and any light-neutral remesh trigger. Under GS-5 the same window would also drop a connectivity-mask refresh (§5).                                                         | MP-1 (evidence) → MP-3 |
| F2 | **Zero orchestration coverage** (§2.3). Stages 1/3/4/6/8 are production-only logic; the meshing suite starts at the job's inputs, the queue suite ends at the queue's API.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |       MP-2, MP-4       |
| F3 | **Three owners flip section visibility via `SetActive`** — `UpdateMeshNative` (vertex-count toggle, SR:122–128), `SectionRenderer.Clear()` (SR:256), `Chunk.Release`/`Reset` (parent object + renderer clears). This is the culling doc's §7.3 conflict, named there as a likely source of the previous culling attempt's corruption; its Phase 0.5 split (`forceRenderingOff` for occlusion, owned exclusively by the future `VisibilityManager`) is verified still unimplemented.                                                                                                                                                                                                                                                        |          MP-5          |
| F4 | **`ChunksToDraw` / `CreateMesh` is a vestigial stage with a lifecycle hole.** Post-MR-5, `ApplyMeshData` uploads everything and the section objects are already active — `CreateMesh` only triggers the one-shot load animation, drained **one chunk per frame** (W:1731–1741). The names lie about what the stage does; the queue holds `Chunk` references that survive pool recycling (the guard checks *destroyed*, not *recycled*, W:1737), so a drain can trigger the animation for the slot's **new** lifecycle (whose `_hasPlayedLoadAnimation` was reset) before its own mesh exists; and the queue is never cleared on unload. Benign today (animation-only) but it is exactly the stale-visibility-actor class §7.3 warns about. |          MP-6          |
| F5 | **`ProcessMeshJobs` duplicates the completion-pass skeleton inline.** The HF-2 two-stage isolation + release-inside/remove-after ordering is hand-written (WJM:875–929) while the identical structure was extracted for lighting (`LightingCompletionPass` — already fully generic over `TKey`). The harness cannot replay mesh pass bookkeeping (the mesh analog of lighting fidelity **B7**, which took an in-game `ObjectDisposedException` cascade to discover).                                                                                                                                                                                                                                                                       |          MP-4          |
| F6 | **Neighbor naming asymmetry.** `MeshGenerationJob` fields use Back/Front/Left/Right(+combos) while `NeighborMapSet` uses compass N/S/E/W; the mapping is a hand-written 16-line wiring table (WJM:355–371). B18–B21 pin only the +X plane; a swapped pair on another face would be a seam-culling bug no baseline reds.                                                                                                                                                                                                                                                                                                                                                                                                                    |          MP-7          |
| F7 | **Drain re-probes gates O(queue) per frame under backlog.** Gate-failing chunks stay in place and are re-tested (8-neighbor probes each) every frame — the pre-MT-2 lighting shape. No starvation (the walk continues past them) and queue depths are moderate, so this is SECONDARY: an event-promoted parked set is sketched as a v2 extension, measure-first (§8).                                                                                                                                                                                                                                                                                                                                                                      |        §8 (v2)         |
| F8 | **Request-drop safety is convention-only** (pipeline doc §9.5, still rated Medium). `RequestChunkMeshRebuild` silently drops null/inactive chunks; correctness relies on every drop having a later re-request (activation, load, gen-complete sites). Nothing observes drops.                                                                                                                                                                                                                                                                                                                                                                                                                                                              |          MP-1          |

---

## 3. Decisions

### 3.1 Decision: how to close the orchestration gap (F2)

#### Option A — a full meshing frame simulator (lighting-style) (rejected)

- ✅ Maximum fidelity; proven pattern (AS-2).
- ❌ **Over-build for this loop's actual complexity.** The lighting simulator earns its size from
  multi-pass convergence, cross-chunk mod routing, and promotion events. The mesh loop is a single
  queue + three pure gates + a one-shot completion; its hard parts (job output, renderer apply)
  are *already* suite-covered. A simulator would mostly re-test the queue suite.

#### Option B — pure-decision extraction + thin scheduling scenarios ✅ **CHOSEN**

Extract the `ScheduleMeshing` gate composition into a pure `MeshingScheduleDecision` (the exact
pattern of `LightingScheduleDecision`), drive `ProcessMeshJobs` through the already-generic
completion-pass skeleton (F5), and write scheduling scenarios directly against
`MeshBuildQueue` + the pure decision with hand-supplied facts — no stub `World` needed, because
the decisions take booleans, not world references. Cheap, allocation-free, and every future gate
change becomes a named baseline red. GS-5's integration (§5) then lands on tested seams.

#### Option C — leave production-only, verify in-game (rejected)

- ✅ Zero effort now.
- ❌ **GS-5 rewires visibility on top of this exact loop.** The culling doc's §7 is a post-mortem
  of landing a culler on untested visibility orchestration; repeating that with the scheduling
  loop untested invites the same class of corruption hunt.

### 3.2 Decision: the in-flight request policy (F1)

#### Option A — keep the drop (status quo) (rejected)

- ✅ Fewest rebuilds; the lighting-stabilization re-request masks most cases.
- ❌ **A structural lost-update class.** "Most cases" excludes lighting-disabled worlds and any
  light-neutral remesh trigger, and under GS-5 a dropped rebuild is also a dropped
  connectivity-mask refresh — a stale-culling seed the §7.4 staleness rule exists to prevent.

#### Option B — leave it queued (`return false` on in-flight) ✅ **CHOSEN**

The drain already has retry semantics for `false` ("leave in place, try next frame" —
W:1720–1721). The chunk re-schedules on the first frame after its flight completes; worst case is
one extra rebuild per edit-during-flight, bounded by the 1–2-frame flight window. No new state,
no new machinery — the fix is making the in-flight arm tell the truth to the drain. (The
`return true` also currently makes a *direct* caller believe it scheduled; there are no direct
callers besides the drain today, which MP-2's decision extraction pins.)

#### Option C — dirty-while-in-flight flag + re-enqueue at completion (rejected for v1)

- ✅ The lighting pipeline's own pattern (re-flag mid-flight → completion re-schedules); zero
  redundant gate probes while in flight.
- ❌ More state (a per-chunk flag + a completion hook) than the problem needs at mesh-flight
  timescales. Revisit as v2 **only if** MP-1's counters show Option B's retry probes are a
  measurable cost (§8).

### 3.3 Decision: renderer visibility ownership — pre-decided, scheduled here

The mechanism is already decided in
[`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) §7.3 (occlusion on
`MeshRenderer.forceRenderingOff` owned exclusively by the future `VisibilityManager`; "has
geometry" stays on `SetActive` owned by `SectionRenderer`) and endorsed by
`PERFORMANCE_IMPROVEMENTS_REPORT.md` ("do it early — independently harmless"). This plan does not
re-litigate it; MP-5 is its executor packet, with the baselines the culling doc doesn't specify.

---

## 4. Target architecture (the extraction shapes)

### 4.1 `MeshingScheduleDecision` (MP-2)

```csharp
/// <summary>Pure decision for whether a mesh job may be scheduled for a chunk — mirrors the
/// gate order of WorldJobManager.ScheduleMeshing so the validation suite and production can
/// never disagree (the meshing sibling of LightingScheduleDecision).</summary>
public static class MeshingScheduleDecision
{
    public enum Result : byte
    {
        Schedule,           // all gates pass — build the job
        AlreadyInFlight,    // a mesh job is running for this chunk (MP-3: caller leaves it queued)
        CenterNotLightReady,// center chunk has unscheduled light work (gate skipped when lighting disabled)
        NeighborsNotReady,  // AreNeighborsMeshReady failed
    }

    public static Result Evaluate(
        bool jobInFlight, bool lightingEnabled,
        bool centerHasLightWork, bool centerNeedsInitialLighting,
        bool neighborsMeshReady);
}
```

`ScheduleMeshing` becomes: evaluate → early-out per result (MP-3 makes `AlreadyInFlight` return
`false`) → existing snapshot/schedule body unchanged. The drain's own policy (budget, per-iteration
cap re-check, null/inactive purge, remove-on-schedule vs leave-on-decline) stays in `World.Update`
but is now exercisable in scenarios because the per-chunk decision is pure.

### 4.2 Completion-pass reuse (MP-4)

`LightingCompletionPass.RunMergeLoop`/`RunRemoveAndPromote` are already generic over `TKey`; the
"lighting" in the name is the only lighting-specific thing about the skeleton. Generalize the home
(rename to `JobCompletionPass` + `IJobCompletionDriver<TKey>` via the `refactor-safely` skill, or
introduce the neutral name and keep a delegating alias — executor's call, both suites decide), and
give `WorldJobManager` a second, cached driver for the mesh pass:

| Driver hook        | Mesh pass mapping                                                                        |
|--------------------|------------------------------------------------------------------------------------------|
| `IsComplete`       | `MeshJobs[key].Handle.IsCompleted`                                                       |
| `CompleteJob`      | `Handle.Complete()` (stage-1 fault → left enrolled for retry, as today)                  |
| `MergeJob`         | resolve `Chunk` + `ApplyMeshData(output)` (chunk gone → discard, as today)               |
| `OnMergeFault`     | one `Debug.LogError`; chunk keeps its previous mesh (as today)                           |
| `ReleaseJob`       | `_meshOutputPool.Return(output)` + `ReleaseMeshingJobInputs` (the MR-6 central return)   |
| `RemoveAndPromote` | `MeshJobs.Remove(key)` only — the mesh pipeline has no promotion concept (queue retries) |

Note `WorldJobManager` already implements `ILightingCompletionDriver<ChunkCoord>` on `this`; the
mesh driver must be a separate cached adapter object (one class cannot implement the same generic
interface twice with one type argument) — a small private nested class instantiated once.

### 4.3 GS-5 seams this plan must leave in place (see §5)

- **Single apply site**: `ProcessMeshJobs` → `ApplyMeshData` remains the only path meshes reach
  renderers — it is the §7.4 publish point where connectivity masks will be applied atomically
  with the mesh they describe.
- **Per-section derived data** rides `MeshDataJobOutput` (`SectionStats` precedent); anything GS-5
  adds there inherits `MeshOutputPool.ClearForReuse` obligations (the MH-2/B17 stale-reuse guard
  pattern).
- **Presentation seam**: MP-5's occlusion toggle is a method on `SectionRenderer`, so a later
  GS-6/BRG conversion swaps the toggle's implementation, not its callers (the culling doc's
  "visible-section set consumed by a thin presentation layer").

---

## 5. Visibility-culling readiness (the GS-5 contract)

What each named GS-5 requirement needs from this plan, and where it lands:

| Culling-doc requirement                                                                  | Where honored                                                                                                        |
|------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------|
| §7.3 ownership split (`forceRenderingOff` vs `SetActive`) — hard prerequisite, Phase 0.5 | **MP-5** executes it, with fixture baselines asserting the non-interference invariant                                |
| §7.4 staleness rule — mask published in the same main-thread step that applies the mesh  | §4.3: the single apply site is preserved by MP-4 (the skeleton keeps merge = apply) and named in doc-sync            |
| §7.4 corollary — while a mesh job is in flight, the culler uses the *old* mask           | **MP-3**: a request during flight stays queued, so the post-edit mask is never silently skipped (the F1 fix)         |
| §7.5 conservative defaults (unknown ⇒ render)                                            | Out of scope here (culler-side), but MP-5's default `forceRenderingOff = false` on every reset path is its substrate |
| §8 no save-format impact (masks derived, never persisted)                                | §6 row: nothing in MP-* touches serialization; tripwire stated                                                       |
| GS-6 ordering note — visibility expressed through a swappable presentation layer         | §4.3: occlusion toggle is one `SectionRenderer` method, not scattered renderer pokes                                 |

---

## 6. Constraint compliance checklist

| Project constraint                              | How this plan complies                                                                                                                                                                           |
|-------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — orchestration + renderer-ownership only.                                                                                                                                             |
| Burst jobs 100 % Burst-compatible               | Job logic untouched; MP-7 renames job *fields* only (naming, no semantics — suite pins output).                                                                                                  |
| No GC / LINQ in hot paths                       | Decisions are static pure functions over bools; the mesh completion driver is one cached object; no per-frame delegates/allocs.                                                                  |
| Pooling conventions                             | MP-4 preserves the MR-6 central return ordering (release-inside/remove-after); `MeshOutputPool` semantics unchanged; B17 guards it.                                                              |
| No BinaryFormatter/JSON for terrain             | No serialization surface anywhere in MP-* (meshes + future masks are derived data — culling doc §8). Tripwire: if any phase wants to persist derived render data, stop — that is a scope change. |
| BlockIDs constants, no raw IDs                  | N/A — no block-level code touched.                                                                                                                                                               |

---

## 7. Phased implementation plan

**Universal regression gate for every phase**: `Minecraft Clone/Dev/Validate Meshing` (tip
**B21** — new baselines take **B22+**) green, `Minecraft Clone/Dev/Validate Mesh Build Queue`
(9 baselines) green, and — for phases touching shared `WorldJobManager`/helper surfaces (MP-4) —
`Minecraft Clone/Dev/Validate Lighting Engine` (62 baselines, both modes) green;
`dotnet build "Assembly-CSharp.csproj"` AND `dotnet build "Assembly-CSharp-Editor.csproj"` clean.
Workflow gotchas apply (new-file Unity import before `dotnet build`; menu suites can run stale
code — confirm red/green flips after `RequestScriptCompilation` with a fresh `Unity_RunCommand`
wave). Every behavior-changing phase (MP-3, MP-6) additionally needs in-game confirmation before
its baseline is trusted (validation-driven-bugfix discipline).

| Phase                                                       | Scope (files)                                                                                                                     | Effort | Depends on            |
|-------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------|:------:|-----------------------|
| **MP-1 — Request/drop observability probes**                | `World.cs`, `WorldJobManager.cs` (editor-only diagnostics)                                                                        |   🟢   | —                     |
| **MP-2 — `MeshingScheduleDecision` + scheduling baselines** | new `Helpers/MeshingScheduleDecision.cs`; `WorldJobManager.ScheduleMeshing`; new suite partial                                    |   🟡   | —                     |
| **MP-3 — In-flight request policy fix**                     | `WorldJobManager.ScheduleMeshing` (one arm); prove-red baseline                                                                   |   🟡   | MP-1 (evidence), MP-2 |
| **MP-4 — Completion-pass unification**                      | `Helpers/LightingCompletionPass.cs` (generalize/rename); `WorldJobManager.ProcessMeshJobs` + mesh driver; skeleton-order baseline |   🟡   | —                     |
| **MP-5 — GS-5 Phase 0.5 ownership split**                   | `SectionRenderer.cs`; renderer-fixture baselines; culling-doc + perf-report checkbox flips                                        |   🟢   | —                     |
| **MP-6 — Draw-tail re-home (`ChunksToDraw`)**               | `Chunk.cs`, `World.cs` step 8                                                                                                     |   🟢   | MP-1 (evidence)       |
| **MP-7 — Naming & wiring hygiene**                          | `Jobs/MeshGenerationJob.cs` field rename; `WorldJobManager.cs` wiring; pipeline-doc §9.5 refresh                                  |   🟢   | —                     |

**Minimal standalone-value set:** MP-1 + MP-2 (coverage) or MP-5 alone (unblocks GS-5 — it has no
dependency on the others and the performance report asks for it early). **Validation is built
alongside, not after** — MP-2/3/4/5 each add their baselines in the same commit as the code.

### MP-1 — Request/drop observability probes (🟢, no behavior change)

- **Scope:** editor/dev-only (`[Conditional]` dual-gate, the HF-1/LP-1 pattern):
    1. `RequestChunkMeshRebuild`: count silently-dropped requests (null vs inactive), warn-once with
       coord (F8 — makes pipeline-doc §9.5's risk observable).
    2. `ScheduleMeshing` in-flight arm: count requests consumed against an in-flight job (F1's
       frequency evidence — how often the window fires in a real session, and in a
       `enableLighting=false` session).
    3. `ChunksToDraw` drain: count dequeued entries whose `Chunk.Coord` no longer matches a live
       `_chunkMap` entry for that chunk instance (F4's recycled-ref evidence).
- **Acceptance:** universal gate + an in-game soak (streaming, edits, a fluid flood, one
  lighting-disabled session); record counter results here as an Amended line. MP-3 and MP-6 read
  this evidence.
- **Doc-sync:** none (no behavior). **Serialization:** none.

### MP-2 — `MeshingScheduleDecision` + scheduling baselines (🟡)

- **Scope:** new `Assets/Scripts/Helpers/MeshingScheduleDecision.cs` (§4.1; runtime assembly,
  `LightingScheduleDecision` precedent); `ScheduleMeshing` routes its three gates through it
  (behavior-identical, including today's `AlreadyInFlight → true` — MP-3 changes that separately);
  new editor suite partial `MeshingValidationSuite.Scheduling.cs`:
    - **B22** — decision census: every `Evaluate` input combination maps to the documented result
      (oracle-free truth-table baseline, the LP transition-census style), including the
      `lightingEnabled=false` bypass.
    - **B23** — drain-policy scenario: a real `MeshBuildQueue` + scripted per-coord decision facts
      replaying the drain's rules (budget stop, cap stop, null/inactive purge,
      remove-on-schedule, leave-on-decline, immediate-ahead-of-normal order). Drive the queue
      directly; the drain body itself stays in `World.Update` — the scenario pins the *policy* via
      the same primitives it uses. (If extracting the drain body into a testable helper turns out
      cheap during implementation, prefer that; do not force it.)
- **Prove-red:** invert the center-gate term inside `Evaluate` → B22 reds (and only the new
  baselines red — job baselines unaffected); restore → green.
- **Acceptance:** universal gate + in-game smoke (streaming + edit responsiveness unchanged).
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §5.3 (shared-decision pointer, mirroring §4's
  lighting note); meshing fidelity doc gains an "orchestration coverage" entry (new §; tag CLOSED
  for the decision layer). **Serialization:** none.

### MP-3 — In-flight request policy fix (🟡, behavior change — the F1 fix)

- **Precondition:** MP-1's counter shows the window fires in practice (any nonzero count
  justifies; a zero count across long soaks including lighting-disabled would instead demote this
  phase to a doc-note — record either way).
- **Scope:** `ScheduleMeshing`'s in-flight arm returns `false` (decision result `AlreadyInFlight`
  → leave queued), one line + the decision mapping in MP-2's helper. The drain then naturally
  retries after the flight completes.
- **Prove-red first (B24):** scheduling scenario — request chunk X; schedule it (in flight);
  request X again; assert the second request survives in the queue and schedules after the
  flight completes. Red under today's drop, green after. Plus an end-to-end in-game repro:
  `enableLighting=false`, place a block, immediately place a second in the same chunk within the
  flight window — pre-fix the second edit's mesh update can be lost until an unrelated trigger;
  post-fix it appears.
- **Watch:** MP-1's in-flight counter becomes a *retry* counter — confirm no runaway re-meshing
  (fluid-stress session: rebuild counts should rise only marginally; if they spike, the v2
  dirty-flag option in §3.2/§8 is the escape hatch — stop and record, don't improvise).
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §5.3 mesh-scheduling flowchart + §9 (new resolved
  entry referencing this doc); `SUB_CHUNK_MESHING_ARCHITECTURE.md` §4.4 (modification workflow)
  if it describes the old behavior. **Serialization:** none.

### MP-4 — Completion-pass unification (🟡)

- **Scope:** §4.2. Generalize the skeleton's home (`refactor-safely` for the rename;
  lighting call sites + the frame simulator's driver update mechanically); add the cached mesh
  driver in `WorldJobManager`; `ProcessMeshJobs` becomes snapshot-keys → `RunMergeLoop` →
  `RunRemoveAndPromote` (candidates snapshot is byte-identical here for the same reason as
  lighting: the loop never adds to `MeshJobs`, removal is already after-loop).
  `ProcessGenerationJobs` is explicitly **excluded** — its budget-retry `continue` semantics
  don't fit the skeleton (same verdict as HF-2's audit).
- **New baseline (B25):** skeleton-order replay with a recording fake driver (pure — no world
  needed): 4 candidates, one stage-1 fault, one stage-2 fault; assert carried-over vs
  released+enrolled vs removed-after ordering matches the contract (the mesh-side B65 analog —
  and it doubles as a regression pin for the *lighting* skeleton after the rename).
- **Coordination note (LP-*):** LP-3 (lighting doc) edits the lighting driver's `ReleaseJob`; if
  both plans are in flight, land the rename first or rebase the smaller change — the suites
  arbitrate either order.
- **Acceptance:** universal gate **including the full lighting suite** (shared skeleton renamed)
    + in-game smoke.
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §4 (the HF-2 fault-isolation section names the
  shared skeleton for lighting — extend to meshing); lighting fidelity doc B7 entry gains the
  mesh-side note. **Serialization:** none.

### MP-5 — GS-5 Phase 0.5: renderer-ownership split (🟢, independently harmless)

- **Scope:** `SectionRenderer.cs` only:
    1. Add the occlusion seam: `SetOcclusionCulled(bool)` writing `_meshRenderer.forceRenderingOff`
       — the **only** writer of that flag in the codebase, reserved for the future
       `VisibilityManager` (unused by production until GS-5 Phase 2/3).
    2. Guarantee the reset invariant: `Clear()` (pool recycle) resets `forceRenderingOff = false`
       (a recycled section must never inherit a culled state — the pool-reset-safety rule; the
       conservative direction is "render", per culling doc §7.5).
    3. Confirm-and-document: `UpdateMeshNative` and `Clear()` keep owning **only** `SetActive`
       ("has geometry"); XML-doc the two-axis ownership contract on the class.
- **New baselines (renderer fixture, B26+):** (a) `UpdateMeshNative` never writes
  `forceRenderingOff` (set it true externally, run a non-empty then an empty update, assert it
  survived both — the non-interference invariant); (b) `Clear()` resets it false; (c)
  `SetOcclusionCulled` round-trips and does not touch `activeSelf`. Prove-red: temporarily make
  `UpdateMeshNative` clear the flag → (a) reds.
- **Acceptance:** universal gate (renderer baselines B12–B16 especially) + in-game smoke (no
  visual change — the flag is never set in production yet). Verify against
  `mcp__unity-api__get_class_reference("MeshRenderer")` that `forceRenderingOff` is the correct
  member/signature before writing code (per CLAUDE.md API rules).
- **Doc-sync (same commit):** flip `VISIBILITY_CULLING_ARCHITECTURE.md` §5 Phase 0.5 checkbox +
  §7.3/§8 "still open" notes; update `PERFORMANCE_IMPROVEMENTS_REPORT.md`'s GS-5 prerequisite
  line (report edit is a status-line flip, not a re-audit); `SUB_CHUNK_MESHING_ARCHITECTURE.md`
  §3.2 rendering-strategy note. **Serialization:** none.

### MP-6 — Draw-tail re-home (🟢, small behavior change)

- **Scope:** retire F4's staleness while keeping the visual behavior decision explicit:
    - **Default (recommended): keep the paced queue, fix its lifecycle.** Store `(Chunk, ChunkCoord)`
      (or re-resolve via `_chunkMap` at drain) and skip entries whose chunk no longer occupies that
      coord; clear `ChunksToDraw` in the same teardown paths that clear `_meshBuildQueue`; rename
      the stage to what it is (`_loadAnimationQueue` / `TriggerLoadAnimation()` — `refactor-safely`),
      and move the enqueue out of `Chunk.ApplyMeshData` into `ProcessMeshJobs` (the chunk stays
      queue-agnostic, matching the MR-6 ownership style).
    - **Option (needs user sign-off — visual change):** drop the one-per-frame pacing and trigger
      the animation directly at apply time. Do NOT take this silently; the stagger may be a
      deliberate aesthetic. Ask when executing.
- **Acceptance:** universal gate + in-game visual check: load animations play once, at the right
  position, under streaming + a pool-churn session (sprint one direction so recycling is hot);
  MP-1's probe-3 counter goes to zero.
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §4 step 8 + §5.3 final-draw subgraph (rename +
  actual semantics); `SUB_CHUNK_MESHING_ARCHITECTURE.md` if it names `CreateMesh`.
  **Serialization:** none.

### MP-7 — Naming & wiring hygiene (🟢)

- **Scope:** rename `MeshGenerationJob`'s neighbor fields to compass names matching
  `NeighborMapSet` (`NeighborBack→NeighborS`, `NeighborFront→NeighborN`, `NeighborLeft→NeighborW`,
  `NeighborRight→NeighborE`, + the four diagonals and the eight light twins) via `refactor-safely`
  — naming-only inside a Burst job (no semantic change; B18–B21 + full suite pin the +X plane and
  output equality). Update the WJM:355–371 wiring block, which becomes self-checking
  (`NeighborS = jobData.Neighbors.NeighborS`). Refresh `CHUNK_LIFECYCLE_PIPELINE.md` §9.5's text
  with MP-1's probe reality (convention now observable).
- **Acceptance:** universal gate (byte-identical output — `OutputsEqual` across the suite is the
  real guard) + in-game seam check (fly a chunk border; no doubled/missing border faces).
- **Doc-sync:** pipeline doc §9.5; meshing fidelity doc §2 if it names the old fields.
  **Serialization:** none.

---

## 8. Extension roadmap (post-MP-7, in intended order)

| Version | Extension                                                                                                                                                                                                                                                                                                                 |
|---------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | **Drain park/promote** (F7): parked set for gate-failing queued chunks, promoted by the events lighting already hooks (generation/load/lighting completion). Only with MP-1 counter + profiler evidence that gate re-probing costs real frame time; rides LP-2's shared `NeighborReadinessDecision` facts if LP-2 landed. |
| **v2**  | **Dirty-while-in-flight re-enqueue** (§3.2 Option C) — only if MP-3's leave-queued retry shows measurable redundant-rebuild cost.                                                                                                                                                                                         |
| **v3+** | **GS-5 Phases 1–3** (connectivity masks in `MeshDataJobOutput`, `VisibilityManager`, PVS) — owned by `VISIBILITY_CULLING_ARCHITECTURE.md`; lands on MP-5's seam and §4.3's publish point. GS-6 / MR-8 follow per the performance report's sequencing.                                                                     |

---

## 9. Open questions

1. **MP-1 probe results** — how often do the in-flight drop (F1), silent request drops (F8), and
   recycled draw-queue refs (F4) fire in real sessions? Gates MP-3's go/no-go and MP-6's urgency;
   answers land here as Amended lines.
2. **MP-6 pacing** — keep the one-chunk-per-frame load-animation stagger or trigger at apply
   time? User call at execution (visual preference); the default scope assumes keep-and-fix.
3. **Skeleton rename shape (MP-4)** — hard rename to `JobCompletionPass` vs neutral new home +
   delegating alias. Executor decides by diff size; both must leave lighting + meshing suites
   green.

---

## Document History

* **v1.0** - Initial design (orchestration census + F1–F8 findings + MP-1…MP-7 phased plan at `72ad121`)

---

**Last Updated:** 2026-07-06
**Next Review:** when MP-1 starts (re-verify §2 line anchors against HEAD), or when GS-5 Phase 1 is scheduled (re-check §5 contract)
