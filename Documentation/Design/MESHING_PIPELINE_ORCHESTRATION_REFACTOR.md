# Meshing Pipeline Orchestration Refactor (MP-*)

**Version:** 1.0 **Date:** 2026-07-06 **Status:** Proposed design — not implemented. **Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

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

**Audited:** 2026-07-06, at commit `72ad121` (branch `feat/async-lighting-validation-suite`). Findings are from static review of `World.cs` (Update mesh drain L1686–1743, `ChunksToDraw` drain L1731–1741, `RequestChunkMeshRebuild` L2273–2283 + all 10 call sites, `NotifyChunkModified`
L1798–1839, `UnloadChunks` mesh-queue removals L2449/L2590, `CompleteAndProcessMeshJobs`
L1351–1361), `WorldJobManager.cs` (`ScheduleMeshing` L297–420, `ProcessMeshJobs` L875–929,
`ReleaseMeshingJobInputs` L959–966), `Helpers/MeshBuildQueue.cs` (full),
`Chunk.cs` (`ApplyMeshData`/`CreateMesh` L535–595, `Reset`/`Release` L86–165,
`PlayChunkLoadAnimation` L627–644), `SectionRenderer.cs` (full — `UpdateMeshNative`, `Clear`, MR-2/3/4 state), and `Data/WorldData.cs:223`. Verified: **no `forceRenderingOff` exists in the codebase** (the culling doc's Phase 0.5 is still open, re-confirming its 2026-07-02 note). Line numbers are anchors for the executor, not contracts — re-verify before editing.

> **Drift note (2026-07-22):** `CompleteAndProcessMeshJobs` (censused above) was deleted as dead
> code out of band — Rider `safe_delete` confirmed zero usages. Runtime mesh completion lives in
> the per-frame `ProcessMeshJobs` drain; shutdown completion in `WorldJobManager.Dispose()`. No
> MP phase is affected; the method appears in no phase's scope.
>
> **Drift update (2026-07-24, P-4 backpressure landed in this exact surface).** Re-verified §2
> against HEAD before MP-1: every finding **F1–F8 still holds** (F1 in-flight `return true`, F4
> `ChunksToDraw` recycled-ref guard, F8 silent null/inactive drop all intact in current code). But
> the **P-4 generation-backpressure family** (shipped 2026-07-21/23, *after* this audit) rewrote
> the drain and completion stages the plan targets. §2 line anchors have shifted — re-verify per
> phase, as already mandated. The substantive per-phase deltas:
> - **Drain (§2.1 stage 3 / MP-2):** now runs P-4 time budgets — a rate quota (`ComputeQuota`) plus
>   an ms ceiling (`meshWindow.Expired` / `ScaleCeilingMs`), gated on `enablePipelineTimeBudgets`,
>   *on top of* the OM-1 in-flight cap. **MP-2's drain scenario must model the quota/ceiling
    > stops AND the budgets-off legacy leg**, not just the original budget/cap. *(Landed as B25 — see the
    > MP-2 §Amended note; B22/B23 were already FL sway baselines.)*
> - **Completion (§2.1 stage 6 / MP-4):** `ProcessMeshJobs` gained a `PipelinePassBudget.Window`
>   parameter and **rotating-start snapshot iteration** (`_meshScanKeys` / `_meshScanCursor`,
>   P-4 §3.4 fairness). The shared `LightingCompletionPass` skeleton predates both, so MP-4 is no
>   longer a straight reuse — it must first reconcile the window-break + rotating cursor against the
>   lighting skeleton (which may itself have gained them) and generalize the skeleton to carry them.
>   **MP-4 likely exceeds its 🟡 estimate.** Decision (2026-07-24): keep MP-4 in scope, reconcile
>   the skeleton first.
>   *(✅ **Resolved 2026-07-25** — the reconcile was real (the lighting skeleton had neither knob) but small:
    > two optional `RunMergeLoop` parameters, the per-job body already identical. **MP-4 did not exceed 🟡.**
    > The skeleton is now `Helpers/JobCompletionPass.cs`. See the MP-4 Amended note.)*
> - **Draw tail (§2.1 stage 8 / MP-6):** the P-4 §5.3 rider already replaced the unconditional
>   one-per-frame dequeue with a flag-gated one-vs-drain-many drain (`drawApplyBudgetMs`); stale
>   dequeues already "don't consume the guaranteed draw." F4's recycled-lifecycle hole still
>   persists (the guard checks *destroyed*, not *recycled*). MP-6 now layers onto that P-4 logic,
>   not the old code. Decision (2026-07-24, user sign-off on the §9-Q2 visual change): **drop the
    > load-animation stagger at apply time** AND fix the recycled-ref hole + clear-on-unload.
> - **MP-1 rider (2026-07-23 backlog):** add an **out-of-range / gone-chunk mesh-discard counter**
>   to the probe set. The P-4 IL2CPP capture proved the lighting fail-safe re-promotes dead-area
>   work forever (OFF legs never drained); the mesh merge already discards gone chunks
>   (`if (chunk != null)`), so MP-1 should count how often that fires. Feeds MP-4's merge seam.

**Relationship to other documents:**

- [`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) — the future GS-5 design this plan makes room for: MP-5 executes its **Phase 0.5 ownership split** (§7.3), and §5 here bakes its **§7.4 staleness rule** (mask published atomically with the mesh apply) and **GS-6 presentation seam** into the phases as design constraints. GS-5's Phases 1–3 stay in that doc — this plan does not build the culler.
- [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md) — the LP-* sibling; shared patterns (pure-decision extraction, invariant probes, completion-pass skeleton) and two explicit coordination points (§7 notes on LP-2's gate predicate and LP-3's lighting driver).
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — §5.3 (meshing pipeline flow) and §9.5 (mesh-queue population race) are restructured/answered by MP-1/MP-3; every phase doc-syncs it.
- [`../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`](../Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md)
  — the section-meshing architecture (its §8 already points at the culling design); MP-5/MP-6 touch behavior it describes.
- [`../Architecture/Testing Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
  — the suite this plan extends (tip **B21**; MH-7/MH-8 stay owned there). MP-2/MP-4 add the orchestration coverage that doc's §4 scoped out of the *job* harness.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — MR-1..7 shipped (guarded by this suite); MR-8/GS-5/GS-6 keep their IDs and sequencing (report §recommendation:
  ownership split early → GS-5 → GS-6 → MR-8). MP-5 is that "ownership split early" item.
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — NS-3's convergence assertion family ("every chunk eventually reaches lit + meshed"); MP-1's probes and MP-2's scheduling harness are its meshing-side groundwork.

---

## 1. Goals & non-goals

### Goals

1. **Close the orchestration coverage gap** — the request → queue → gate → schedule → completion loop is production-only code today (§2.3); extract its decisions into shared pure code and baseline them, the LP/HF-4 pattern.
2. **Fix the in-flight lost-update window** (§2.4 F1) — a rebuild request during a chunk's mesh flight must survive to a post-completion rebuild, not be silently dropped against the stale snapshot.
3. **Make the pipeline GS-5-ready** — execute the §7.3 ownership split (MP-5), preserve the single apply site as the future mask-publish point (§5), and keep per-section derived data shapes.
4. **Retire the vestigial draw stage's staleness** (§2.4 F4) — the post-MR-5 `ChunksToDraw` stage only triggers load animations and can act on a recycled chunk's wrong lifecycle. ✅ **MP-6 (2026-07-25) retired the stage itself**, which is what closes the staleness.
5. **Preserve behavior at every phase boundary except the two named fixes (MP-3, MP-6)** — meshing suite B1–B21 + mesh-queue suite green throughout; the two behavior changes ship with their own prove-red baselines and in-game confirmation.
6. *(SECONDARY)* A measured-only extension for the drain's O (queue) gate re-probing (§8 roadmap).

### Non-goals (v1)

- **Building GS-5 itself** (connectivity masks, `VisibilityManager`, BFS/PVS) — owned by
  [`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) Phases 1–3. MP-5 delivers only its Phase 0.5 prerequisite.
- **GS-6 (BatchRendererGroup) and MR-8 (greedy meshing)** — own design docs per the performance report; MH-8's merge-invariant oracle stays gated on MR-8's doc.
- **MH-7 (custom/cross/lava palette)** — owned by the meshing fidelity doc; built alongside the feature it guards.
- **Changing any gate's semantics** — `AreNeighborsMeshReady` stays deliberately relaxed (the wave-front deadlock fix, pipeline doc §9.3); the center light-flag gate stays. MP-2 re-houses, never redesigns. (LP-2 separately re-houses the *neighbor* predicate — see the §7 coordination note.)
- **Redesigning `MeshBuildQueue`** — MT-1 shipped, guarded by its own 9-baseline suite; the class survives unchanged. Park/promote for the drain is a v2 extension, measure-first (§8).
- **Mesh data-format or job-internals work** — MR-2..7 shipped and suite-pinned; the job stages are not touched by any phase (except MP-7's naming-only field rename).

---

## 2. Current state — the orchestration surface

### 2.1 Stage map (who does what, today)

| # | Stage             | Code                                                                                                                                                                                                                                         | Suite coverage today                                                                 |
|---|-------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------|
| 1 | **Request**       | `World.RequestChunkMeshRebuild(Chunk, immediate)` (W:2273) — drops null/inactive chunks                                                                                                                                                      | ❌ none                                                                              |
| 2 | **Queue**         | `MeshBuildQueue` (MT-1): O(1) dedup, immediate→head+promote, normal→tail, O(1) remove                                                                                                                                                        | ✅ own suite (9 baselines, incl. B9 promotion)                                       |
| 3 | **Drain**         | `World.Update` step 7 (W:1694–1728): per-frame budget + OM-1 in-flight cap (re-checked per iteration), null/inactive→remove, schedule-ok→remove, gate-fail→leave in place                                                                    | ❌ none                                                                              |
| 4 | **Gates**         | `WorldJobManager.ScheduleMeshing` (WJM:297–322): in-flight → `return true` (!); center `HasLightChangesToProcess/NeedsInitialLighting` (skipped when lighting disabled); `AreNeighborsMeshReady`                                             | ❌ none (the lighting fidelity doc's **B5** scoped this out of *its* suite)          |
| 5 | **Jobs**          | `MeshGenerationJob` + chained `MeshPostProcessJob` (MR-5), pooled inputs + `MeshOutputPool` output (MR-6)                                                                                                                                    | ✅ meshing suite B1–B11, B17–B21 (incl. cross-chunk substrate MH-10/11)              |
| 6 | **Completion**    | `ProcessMeshJobs`: HF-2 two-stage fault isolation ~~inline~~ → shared `JobCompletionPass` via a cached mesh driver (MP-4), release-inside/remove-after, central output return                                                                | ✅ **B27** skeleton-order replay (MP-4; was the mesh analog of lighting fidelity B7) |
| 7 | **Apply**         | `Chunk.ApplyMeshData` → per-section `SectionRenderer.UpdateMeshNative` (MR-2 layout, MR-3 materials, MR-4 bounds; `SetActive` by vertex count)                                                                                               | ✅ renderer fixture B12–B16                                                          |
| 8 | ~~**Draw tail**~~ | **RETIRED (MP-6, 2026-07-25).** Was: `ChunksToDraw.Enqueue` in ApplyMeshData → `World.Update` step 8 dequeue → `Chunk.CreateMesh` → `PlayChunkLoadAnimation`. Now `Chunk.TriggerLoadAnimation()` inside stage 7's apply — no stage, no queue | ✅ **B31–B33** (the driver branch, via `IMeshCompletionHost`)                        |

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
  `immediate` silently degrades player-edit latency; a lost re-request site recreates pipeline-doc §9.5's orphaning).
- **Drain policy** — budget/cap interplay (the OM-1 per-iteration cap re-check), leave-in-place retry, null/inactive purge.
- **Gate composition** — the order and effect of the three `ScheduleMeshing` gates, including the
  `enableLighting=false` bypass and the in-flight arm's *dequeue* consequence (F1).
- **Completion-pass bookkeeping** — the mesh pass's HF-2 fault isolation is inline; the lighting twin was extracted (`LightingCompletionPass`) precisely so its suite could replay multi-job fault ordering (baseline B65). The mesh pass has no such replay.
- ~~**Draw-tail lifecycle** — stale/recycled `Chunk` references in `ChunksToDraw` (F4).~~ **CLOSED (MP-6):**
  the stage no longer exists, so there is no cross-frame reference to go stale.

This is the meshing analog of the lighting suite's pre-AS-2 B6/B7 state, and the meshing half of NS-3's "every chunk eventually reaches lit + meshed" convergence family.

### 2.4 Findings

| #  | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |      Addressed by      |
|----|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:----------------------:|
| F1 | **In-flight request drop (lost update).** `ScheduleMeshing` returns `true` when `MeshJobs.ContainsKey` (WJM:301–302), and the drain treats `true` as scheduled → `RemoveCurrent()` (W:1722–1724). A rebuild requested *while that chunk's mesh job is in flight* is therefore dequeued and dropped — but the in-flight job snapshotted its inputs before the request, so the on-screen mesh stays stale. Masked in practice because most edits also dirty lighting, whose stabilization re-requests the mesh (WJM:1074); exposed with `enableLighting = false`, and any light-neutral remesh trigger. Under GS-5 the same window would also drop a connectivity-mask refresh (§5).                                                                                                                                                                                                                          | MP-1 (evidence) → MP-3 |
| F2 | **Zero orchestration coverage** (§2.3). Stages 1/3/4/6/8 are production-only logic; the meshing suite starts at the job's inputs, the queue suite ends at the queue's API.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |       MP-2, MP-4       |
| F3 | **Three owners flip section visibility via `SetActive`** — `UpdateMeshNative` (vertex-count toggle, SR:122–128), `SectionRenderer.Clear()` (SR:256), `Chunk.Release`/`Reset` (parent object + renderer clears). This is the culling doc's §7.3 conflict, named there as a likely source of the previous culling attempt's corruption; its Phase 0.5 split (`forceRenderingOff` for occlusion, owned exclusively by the future `VisibilityManager`) is verified still unimplemented.                                                                                                                                                                                                                                                                                                                                                                                                                         |          MP-5          |
| F4 | **`ChunksToDraw` / `CreateMesh` is a vestigial stage with a lifecycle hole.** Post-MR-5, `ApplyMeshData` uploads everything and the section objects are already active — `CreateMesh` only triggers the one-shot load animation, drained **one chunk per frame** (W:1731–1741). The names lie about what the stage does; the queue holds `Chunk` references that survive pool recycling (the guard checks *destroyed*, not *recycled*, W:1737), so a drain can trigger the animation for the slot's **new** lifecycle (whose `_hasPlayedLoadAnimation` was reset) before its own mesh exists; and the queue is never cleared on unload. Benign today (animation-only) but it is exactly the stale-visibility-actor class §7.3 warns about. **✅ CLOSED (MP-6, 2026-07-25) by deleting the stage** — the trigger moved into the apply, so there is no queue, no cross-frame reference, and nothing to clear. |          MP-6          |
| F5 | **`ProcessMeshJobs` duplicates the completion-pass skeleton inline.** The HF-2 two-stage isolation + release-inside/remove-after ordering is hand-written (WJM:875–929) while the identical structure was extracted for lighting (`LightingCompletionPass` — already fully generic over `TKey`). The harness cannot replay mesh pass bookkeeping (the mesh analog of lighting fidelity **B7**, which took an in-game `ObjectDisposedException` cascade to discover).                                                                                                                                                                                                                                                                                                                                                                                                                                        |          MP-4          |
| F6 | **Neighbor naming asymmetry.** `MeshGenerationJob` fields use Back/Front/Left/Right(+combos) while `NeighborMapSet` uses compass N/S/E/W; the mapping is a hand-written 16-line wiring table (WJM:355–371). B18–B21 pin only the +X plane; a swapped pair on another face would be a seam-culling bug no baseline reds.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |          MP-7          |
| F7 | **Drain re-probes gates O(queue) per frame under backlog.** Gate-failing chunks stay in place and are re-tested (8-neighbor probes each) every frame — the pre-MT-2 lighting shape. No starvation (the walk continues past them) and queue depths are moderate, so this is SECONDARY: an event-promoted parked set is sketched as a v2 extension, measure-first (§8).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |        §8 (v2)         |
| F8 | **Request-drop safety is convention-only** (pipeline doc §9.5, still rated Medium). `RequestChunkMeshRebuild` silently drops null/inactive chunks; correctness relies on every drop having a later re-request (activation, load, gen-complete sites). Nothing observes drops.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |          MP-1          |

---

## 3. Decisions

### 3.1 Decision: how to close the orchestration gap (F2)

#### Option A — a full meshing frame simulator (lighting-style) (rejected)

- ✅ Maximum fidelity; proven pattern (AS-2).
- ❌ **Over-build for this loop's actual complexity.** The lighting simulator earns its size from multi-pass convergence, cross-chunk mod routing, and promotion events. The mesh loop is a single queue + three pure gates + a one-shot completion; its hard parts (job output, renderer apply)
  are *already* suite-covered. A simulator would mostly re-test the queue suite.

#### Option B — pure-decision extraction + thin scheduling scenarios ✅ **CHOSEN**

Extract the `ScheduleMeshing` gate composition into a pure `MeshingScheduleDecision` (the exact pattern of `LightingScheduleDecision`), drive `ProcessMeshJobs` through the already-generic completion-pass skeleton (F5), and write scheduling scenarios directly against
`MeshBuildQueue` + the pure decision with hand-supplied facts — no stub `World` needed, because the decisions take booleans, not world references. Cheap, allocation-free, and every future gate change becomes a named baseline red. GS-5's integration (§5) then lands on tested seams.

#### Option C — leave production-only, verify in-game (rejected)

- ✅ Zero effort now.
- ❌ **GS-5 rewires visibility on top of this exact loop.** The culling doc's §7 is a post-mortem of landing a culler on untested visibility orchestration; repeating that with the scheduling loop untested invites the same class of corruption hunt.

### 3.2 Decision: the in-flight request policy (F1)

#### Option A — keep the drop (status quo) (rejected)

- ✅ Fewest rebuilds; the lighting-stabilization re-request masks most cases.
- ❌ **A structural lost-update class.** "Most cases" excludes lighting-disabled worlds and any light-neutral remesh trigger, and under GS-5 a dropped rebuild is also a dropped connectivity-mask refresh — a stale-culling seed the §7.4 staleness rule exists to prevent.

#### Option B — leave it queued (`return false` on in-flight) ✅ **CHOSEN**

The drain already has retry semantics for `false` ("leave in place, try next frame" — W:1720–1721). The chunk re-schedules on the first frame after its flight completes; worst case is one extra rebuild per edit-during-flight, bounded by the 1–2-frame flight window. No new state, no new machinery — the fix is making the in-flight arm tell the truth to the drain. (The
`return true` also currently makes a *direct* caller believe it scheduled; there are no direct callers besides the drain today, which MP-2's decision extraction pins.)

#### Option C — dirty-while-in-flight flag + re-enqueue at completion (rejected for v1)

- ✅ The lighting pipeline's own pattern (re-flag mid-flight → completion re-schedules); zero redundant gate probes while in flight.
- ❌ More state (a per-chunk flag + a completion hook) than the problem needs at mesh-flight timescales. Revisit as v2 **only if** MP-1's counters show Option B's retry probes are a measurable cost (§8).

### 3.3 Decision: renderer visibility ownership — pre-decided, scheduled here

The mechanism is already decided in
[`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) §7.3 (occlusion on
`MeshRenderer.forceRenderingOff` owned exclusively by the future `VisibilityManager`; "has geometry" stays on `SetActive` owned by `SectionRenderer`) and endorsed by
`PERFORMANCE_IMPROVEMENTS_REPORT.md` ("do it early — independently harmless"). This plan does not re-litigate it; MP-5 is its executor packet, with the baselines the culling doc doesn't specify.

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
`false`) → existing snapshot/schedule body unchanged. The drain's own policy (budget, per-iteration cap re-check, null/inactive purge, remove-on-schedule vs leave-on-decline) stays in `World.Update`
but is now exercisable in scenarios because the per-chunk decision is pure.

### 4.2 Completion-pass reuse (MP-4)

> ✅ **Implemented 2026-07-25 essentially as specified below** — the hard-rename branch, plus the P-4
> window/rotating-start generalization. Names below are pre-rename (`LightingCompletionPass` /
> `ILightingCompletionDriver`); the shipped names are `JobCompletionPass` / `IJobCompletionDriver`.
> See the MP-4 Amended note in §7.

`LightingCompletionPass.RunMergeLoop`/`RunRemoveAndPromote` are already generic over `TKey`; the
"lighting" in the name is the only lighting-specific thing about the skeleton. Generalize the home (rename to `JobCompletionPass` + `IJobCompletionDriver<TKey>` via the `refactor-safely` skill, or introduce the neutral name and keep a delegating alias — executor's call, both suites decide), and give `WorldJobManager` a second, cached driver for the mesh pass:

| Driver hook        | Mesh pass mapping                                                                        |
|--------------------|------------------------------------------------------------------------------------------|
| `IsComplete`       | `MeshJobs[key].Handle.IsCompleted`                                                       |
| `CompleteJob`      | `Handle.Complete()` (stage-1 fault → left enrolled for retry, as today)                  |
| `MergeJob`         | resolve `Chunk` + `ApplyMeshData(output)` (chunk gone → discard, as today)               |
| `OnMergeFault`     | one `Debug.LogError`; chunk keeps its previous mesh (as today)                           |
| `ReleaseJob`       | `_meshOutputPool.Return(output)` + `ReleaseMeshingJobInputs` (the MR-6 central return)   |
| `RemoveAndPromote` | `MeshJobs.Remove(key)` only — the mesh pipeline has no promotion concept (queue retries) |

Note `WorldJobManager` already implements `ILightingCompletionDriver<ChunkCoord>` on `this`; the mesh driver must be a separate cached adapter object (one class cannot implement the same generic interface twice with one type argument) — a small private nested class instantiated once.

### 4.3 GS-5 seams this plan must leave in place (see §5)

- **Single apply site**: `ProcessMeshJobs` → `ApplyMeshData` remains the only path meshes reach renderers — it is the §7.4 publish point where connectivity masks will be applied atomically with the mesh they describe.
- **Per-section derived data** rides `MeshDataJobOutput` (`SectionStats` precedent); anything GS-5 adds there inherits `MeshOutputPool.ClearForReuse` obligations (the MH-2/B17 stale-reuse guard pattern).
- **Presentation seam**: MP-5's occlusion toggle is a method on `SectionRenderer`, so a later GS-6/BRG conversion swaps the toggle's implementation, not its callers (the culling doc's
  "visible-section set consumed by a thin presentation layer").

---

## 5. Visibility-culling readiness (the GS-5 contract)

What each named GS-5 requirement needs from this plan, and where it lands:

| Culling-doc requirement                                                                  | Where honored                                                                                                                                        |
|------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------|
| §7.3 ownership split (`forceRenderingOff` vs `SetActive`) — hard prerequisite, Phase 0.5 | ✅ **MP-5 (2026-07-25)** — `SectionRenderer.SetOcclusionCulled` is the sole writer; B28–B30 pin non-interference, recycle reset, and axis separation |
| §7.4 staleness rule — mask published in the same main-thread step that applies the mesh  | §4.3: the single apply site is preserved by MP-4 (the skeleton keeps merge = apply) and named in doc-sync                                            |
| §7.4 corollary — while a mesh job is in flight, the culler uses the *old* mask           | **MP-3**: a request during flight stays queued, so the post-edit mask is never silently skipped (the F1 fix)                                         |
| §7.5 conservative defaults (unknown ⇒ render)                                            | Out of scope here (culler-side), but MP-5's default `forceRenderingOff = false` on every reset path is its substrate                                 |
| §8 no save-format impact (masks derived, never persisted)                                | §6 row: nothing in MP-* touches serialization; tripwire stated                                                                                       |
| GS-6 ordering note — visibility expressed through a swappable presentation layer         | §4.3: occlusion toggle is one `SectionRenderer` method, not scattered renderer pokes                                                                 |

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

**Universal regression gate for every phase**: `Minecraft Clone/Dev/Validate Meshing` (tip **B30** after MP-5 — new baselines take **B31+**) green, `Minecraft Clone/Dev/Validate Mesh Build Queue`
(9 baselines) green, and — for phases touching shared `WorldJobManager`/helper surfaces (MP-4) —
`Minecraft Clone/Dev/Validate Lighting Engine` (62 baselines, both modes) green;
`dotnet build "Assembly-CSharp.csproj"` AND `dotnet build "Assembly-CSharp-Editor.csproj"` clean. Workflow gotchas apply (new-file Unity import before `dotnet build`; menu suites can run stale code — confirm red/green flips after `RequestScriptCompilation` with a fresh `Unity_RunCommand`
wave). Every behavior-changing phase (MP-3, MP-6) additionally needs in-game confirmation before its baseline is trusted (validation-driven-bugfix discipline).

| Phase                                                                   | Scope (files)                                                                                                                                                | Effort | Depends on            |
|-------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|-----------------------|
| **MP-1 — Request/drop observability probes** ✅ **DONE**                | `World.cs`, `WorldJobManager.cs` (editor-only diagnostics)                                                                                                   |   🟢   | —                     |
| **MP-2 — `MeshingScheduleDecision` + scheduling baselines** ✅ **DONE** | new `Helpers/MeshingScheduleDecision.cs`; `WorldJobManager.ScheduleMeshing`; new suite partial                                                               |   🟡   | —                     |
| **MP-3 — In-flight request policy fix** ✅ **DONE**                     | `WorldJobManager.ScheduleMeshing` (one arm); prove-red baseline **B26**                                                                                      |   🟡   | MP-1 (evidence), MP-2 |
| **MP-4 — Completion-pass unification** ✅ **DONE**                      | `Helpers/JobCompletionPass.cs` (generalized/renamed); `WorldJobManager.ProcessMeshJobs` + mesh driver; B27 skeleton-order baseline                           |   🟡   | —                     |
| **MP-5 — GS-5 Phase 0.5 ownership split** ✅ **DONE**                   | `SectionRenderer.cs`; renderer-fixture baselines B28–B30; culling-doc + perf-report checkbox flips                                                           |   🟢   | —                     |
| **MP-6 — Draw-tail re-home (`ChunksToDraw`)** ✅ **DONE**               | `Chunk.cs`, `World.cs` step 8, `WorldJobManager.cs`, `SettingsManager.cs`; new `Helpers/IMeshCompletionHost.cs` + `Helpers/MeshCompletionDriver.cs`; B31–B33 |   🟢   | MP-1 (evidence)       |
| **MP-7 — Naming & wiring hygiene**                                      | `Jobs/MeshGenerationJob.cs` field rename; `WorldJobManager.cs` wiring; pipeline-doc §9.5 refresh                                                             |   🟢   | —                     |

**Minimal standalone-value set:** MP-1 + MP-2 (coverage) or MP-5 alone (unblocks GS-5 — it has no dependency on the others and the performance report asks for it early). **Validation is built alongside, not after** — MP-2/3/4/5 each add their baselines in the same commit as the code.

### MP-1 — Request/drop observability probes (🟢, no behavior change)

- **Scope:** editor/dev-only (`[Conditional]` dual-gate, the HF-1/LP-1 pattern):
    1. `RequestChunkMeshRebuild`: count silently-dropped requests (null vs inactive), warn-once with coord (F8 — makes pipeline-doc §9.5's risk observable).
    2. `ScheduleMeshing` in-flight arm: count requests consumed against an in-flight job (F1's frequency evidence — how often the window fires in a real session, and in a
       `enableLighting=false` session).
    3. *(Retired with the stage by MP-6, 2026-07-25 — there are no dequeues left to count.)*
       `ChunksToDraw` drain: count dequeued entries whose `Chunk.Coord` no longer matches a live
       `_chunkMap` entry for that chunk instance (F4's recycled-ref evidence).
- **Acceptance:** universal gate + an in-game soak (streaming, edits, a fluid flood, one lighting-disabled session); record counter results here as an Amended line. MP-3 and MP-6 read this evidence.
- **Doc-sync:** none (no behavior). **Serialization:** none.

> **Amended (2026-07-24) — MP-1 implemented (uncommitted); soak evidence, both legs.**
> Landed as four `[Conditional("UNITY_EDITOR")]`+`[Conditional("DEVELOPMENT_BUILD")]` instance
> counters with denominators — F1 in-flight-consume + gone-chunk-discard on `WorldJobManager`, F8
> request-drop (warn-once) + F4 recycled-draw-ref on `World` — surfaced via
> `World.BuildMeshOrchestrationDiagnostics` and the `Minecraft Clone/Dev/Dump Mesh Orchestration
> Diagnostics` menu item. Gates: `dotnet build` clean; `Validate Meshing` 23/23 + `Validate Mesh
> Build Queue` 9/9 unchanged-green; Rider inspections 0 items. Two soaks:
>
> | Probe | Lighting ENABLED (stream + edits + flood) | Lighting DISABLED (stress-fly, speed 353) |
> |---|---|---|
> | F1 in-flight consumed | 449 / 148,181 (0.30 %) | **0** / 7,800 (0 %) |
> | gone-chunk merge discards | 43 / 27,356 (0.16 %) | **1,634 / 7,763 (21 %)** |
> | F8 request drops (null / inactive) | 0 / 531,774 | 0 / 3,658,062 |
> | F4 recycled draw-refs | 0 / 27,313 | 0 / 6,129 |
>
> **Verdicts:**
> - **F1 → MP-3 GO, and §2.4 F1's exposure model is corrected.** F1 fired 449× with lighting
>   *enabled* and 0× *disabled* — the reverse of the doc's "enableLighting=false exposes F1." Cause:
>   the in-flight window needs *repeated* rebuild requests to one chunk during its flight, and the
>   dominant generator of those is the lighting/neighbor re-request cascade (`WJM:1074`) — which a
>   lighting-disabled *fly* soak has none of (each chunk schedules ~once; note 7.8 k attempts vs
>   148 k). A fly/streaming soak therefore cannot exercise F1; the discriminating test is
>   lighting-disabled **+ rapid same-chunk edits**, i.e. exactly MP-3's own B24 + in-game repro. MP-3
>   stays GO: the 449 are real drops (upper bound — also counts redundant no-new-data re-requests),
>   and with lighting disabled there is *no* re-request recovery, so any drop is permanent. **The MP-3
    > executor must NOT expect a fly soak to show F1.**
> - **gone-chunk discards → strong MP-4 signal.** 21 % of completed mesh jobs discarded under
>   stress-fly (chunks unloaded before their mesh merged) decisively validates the 2026-07-23
>   out-of-range-discard rider and MP-4's merge driver; also wasted mesh compute (P-4-adjacent).
> - **F8 = 0 over 4.2 M combined requests → empirically a non-issue** (latent risk remains; warn-once
>   never fired even under extreme stress).
> - **F4 = 0 even under speed-353 pool churn → MP-6's lifecycle-hole fix is low urgency** (the probe
>   misses reused-at-new-coord, so F4 is not fully retired; MP-6 still proceeds for the drop-stagger
>   decision).

### MP-2 — `MeshingScheduleDecision` + scheduling baselines (🟡) · ✅ **IMPLEMENTED 2026-07-24 (committed)**

- **Scope:** new `Assets/Scripts/Helpers/MeshingScheduleDecision.cs` (§4.1; runtime assembly,
  `LightingScheduleDecision` precedent); `ScheduleMeshing` routes its three gates through it (behavior-identical, including today's `AlreadyInFlight → true` — MP-3 changes that separately); new editor suite partial `MeshingValidationSuite.Scheduling.cs`:
    - **B24** — decision census: every `Evaluate` input combination maps to the documented result (oracle-free truth-table baseline, the LP transition-census style), including the
      `lightingEnabled=false` bypass.
    - **B25** — drain-policy scenario: a real `MeshBuildQueue` + scripted per-coord decision facts replaying the drain's rules (budget stop, cap stop, **P-4 rate-quota + ms-ceiling stops, and the budgets-off legacy leg** — see the 2026-07-24 drift update, null/inactive purge, remove-on-schedule, leave-on-decline, immediate-ahead-of-normal order). Drive the queue directly; the drain body itself stays in `World.Update` — the scenario pins the *policy* via the same primitives it uses. (If extracting the drain body into a testable helper turns out cheap
      during implementation, prefer that; do not force it.)
- **Prove-red:** invert the center-gate term inside `Evaluate` → B24 reds (and only the new baselines red — job baselines unaffected); restore → green.
- **Acceptance:** universal gate + in-game smoke (streaming + edit responsiveness unchanged).
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §5.3 (shared-decision pointer, mirroring §4's lighting note); meshing fidelity doc gains an "orchestration coverage" entry (new §; tag CLOSED for the decision layer). **Serialization:** none.

> **Amended (2026-07-24) — MP-2 implemented (uncommitted).**
> - **B-number drift corrected.** The plan said "tip B21, new baselines B22+", but FL-1/FL-2 sway had already
>   taken **B22/B23** (2026-07-19). MP-2's baselines therefore landed as **B24** (census) + **B25** (drain
>   policy); MP-3's prove-red becomes **B26**. `Validate Meshing` 23 → **25**.
> - **Drain-body extraction chosen (the "prefer it if cheap" branch, user sign-off).** The drain loop was
>   extracted into a pure `Helpers/MeshDrainPolicy.Drain(queue, quota, window, cap, IMeshDrainHost)` called by
>   BOTH `World.Update` and B25 — zero loop duplication, no oracle divergence. `World` implements
>   `IMeshDrainHost` on `this` (cached, zero per-frame alloc; the `ILightingCompletionDriver` pattern). The
>   in-flight-cap re-check reads the LIVE `host.InFlightCount` each iteration (a computed proxy would diverge
>   pre-MP-3, where an `AlreadyInFlight → true` doesn't grow `MeshJobs`). Budget *math* stays in `World.Update`
>   (owned by the Pipeline Backpressure suite); `MeshDrainPolicy` owns only the loop.
> - **B25 legs** (all deterministic): budgets-off drain, quota stop, in-flight-cap re-check, expired-window
>   stop (public `Window` ctor, no sleep), inactive purge, leave-on-decline (prove-red anchor), immediate-ahead
>   order. Both gate-eager reads (`AreNeighborsMeshReady` evaluated even when an earlier gate declines) verified
>   side-effect-free, matching the `LightingScheduleDecision` caller.
> - **Gates:** `dotnet build` both assemblies clean; `Validate Meshing` **25/25**, `Validate Mesh Build Queue`
>   9/9, `Validate All` **335/335** (Lighting 88/88 — shared `WorldJobManager` surface intact); prove-red fires
>   B24 + B25 (only those two), green after revert.

### MP-3 — In-flight request policy fix (🟡, behavior change — the F1 fix)

- **Precondition:** MP-1's counter shows the window fires in practice (any nonzero count justifies; a zero count across long soaks including lighting-disabled would instead demote this phase to a doc-note — record either way).
- **Scope:** `ScheduleMeshing`'s in-flight arm returns `false` (decision result `AlreadyInFlight`
  → leave queued), one line + the decision mapping in MP-2's helper. The drain then naturally retries after the flight completes.
- **Prove-red first (B26):** scheduling scenario — request chunk X; schedule it (in flight); request X again; assert the second request survives in the queue and schedules after the flight completes. Red under today's drop, green after. Plus an end-to-end in-game repro:
  `enableLighting=false`, place a block, immediately place a second in the same chunk within the flight window — pre-fix the second edit's mesh update can be lost until an unrelated trigger; post-fix it appears.
- **Watch:** MP-1's in-flight counter becomes a *retry* counter — confirm no runaway re-meshing (fluid-stress session: rebuild counts should rise only marginally; if they spike, the v2 dirty-flag option in §3.2/§8 is the escape hatch — stop and record, don't improvise).
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §5.3 mesh-scheduling flowchart + §9 (new resolved entry referencing this doc); `SUB_CHUNK_MESHING_ARCHITECTURE.md` §4.4 (modification workflow)
  if it describes the old behavior. **Serialization:** none.

> **Amended (2026-07-24) — MP-3 implemented; suite-verified. ✅ FULLY CLOSED 2026-07-25** (committed; the
> in-game repro was retired as unreachable — see the closure note below).
> - **The fix landed as the shared-mapping option (Option A, user sign-off).** Rather than a bare one-line switch
>   flip, the `ScheduleMeshing` return decision is now the pure `MeshingScheduleDecision.DequeuesChunk(Result)`
>   (`result == Result.Schedule`), read by BOTH production and B26 — so a revert edits one shared function and
>   reds the baseline in lockstep (the MP-2 anti-divergence rationale; the bare one-liner left the switch edit
>   un-reddable by the world-free suite). The `ScheduleMeshing` switch collapsed to `if AlreadyInFlight → count;
>   if !DequeuesChunk → return false; build`. The MP-1 in-flight counter was **relabeled consumed → retried**
>   (`MeshInFlightRetried` + the diagnostics line): same firing site, but post-fix it fires once per in-flight
>   frame per queued chunk (a retry), so the raw number is NOT comparable to MP-1's 449 drops.
> - **B26** (`Validate Meshing` 25 → **26**): part 1 the pure mapping (`DequeuesChunk(AlreadyInFlight)==false`, the
>   revert guard); part 2 a two-frame `MeshDrainPolicy` scenario driven through `DequeuesChunk(Evaluate(…))` — the
>   in-flight chunk survives the drain frame 1, schedules frame 2 after the flight completes.
> - **Prove-red confirmed:** temporarily restoring the pre-MP-3 mapping (`Schedule || AlreadyInFlight`) reds
>   **exactly B26** (`1 OF 26 FAILED`) — B26.1 `DequeuesChunk(AlreadyInFlight)` true and B26.3 "frame 1 scheduled 1,
>   queue left 0" (the in-flight request dequeued/dropped — the F1 signature); every other baseline green. Restored → green.
> - **Gates:** `dotnet build` both assemblies clean; `Validate Meshing` **26/26**, `Validate All` **336/336**
>   (Lighting 88/88 — shared `WorldJobManager` surface intact). `SUB_CHUNK_MESHING_ARCHITECTURE.md` doc-sync was a
>   **no-op** (verified: it does not describe the in-flight/dequeue behavior). Doc-synced `CHUNK_LIFECYCLE_PIPELINE.md`
>   §5.3 + §9.5 and this fidelity doc's §4.
> - ~~**PENDING (user):** the end-to-end in-game repro — `enableLighting = false`, place a block then immediately a
    > second in the same chunk within the flight window.~~
>   **✅ RETIRED 2026-07-25 — MP-3 is FULLY CLOSED without it (user decision).** Three independent attempts failed
>   to fire the in-flight arm, including MCP-driven ultra-high-speed edit sequences on top of the two scripted
>   probes below. That is the *predicted* outcome, not a gap: per the CORRECTION, F1 is **load-driven**, so no
>   edit-rate recipe can reach it — the arm needs many concurrent mesh jobs with deferred completions.
>   **The evidence that closes MP-3 is B26's prove-red** (restoring `Schedule || AlreadyInFlight` reds exactly
>   B26 with the F1 signature "frame 1 scheduled 1, queue left 0") **plus production telemetry** (`MeshInFlightRetried`
>   fired 273 / 814,801 in the MP-4 smoke session — the arm demonstrably runs in real sessions, with no visual
>   regression and no runaway). Do **not** spend further sessions on a repro rig for this.
>
> > **⚠️ CORRECTION (2026-07-25, measured — the recipe above does NOT work; do not retry it as written).**
> > Three scripted probes (frame-accurate `EditorApplication.update` state machines, lighting-disabled world,
> > edits enqueued every frame via the real `EnqueueVoxelModification` path) **never once fired the in-flight
> > arm**: v2 = 60 attempts / 0 window hits / 0 retries; v3 = 400 attempts / 0 hits, even with
> > `meshApplyBudgetMs` squeezed to `PipelinePassBudget.MinBudgetMs`. The diagnostic that explains it:
> > **`maxInFlight = 1`.**
> >
> > **Why rapid same-chunk edits cannot reach F1.** `World.Update` order is *3. ApplyModifications → 5.
> > ProcessMeshJobs → 6/7. drain*. An edit therefore reaches the drain only **after** `ProcessMeshJobs` has
> > already completed and removed the previous frame's job, so `ScheduleMeshing` sees no job in flight. The
> > entry survives into the drain only if the completion was **deferred**, and the P-4 window cannot defer a
> > *single* job (it is tested before the first candidate, and a fresh window is never already expired). So the
> > arm requires **many concurrent mesh jobs**, not fast edits.
> >
> > **Corrected exposure model: F1 is load-driven, not edit-rate-driven.** This finally reconciles MP-1's soak
> > table, which the original model contradicted: 449 hits with lighting *enabled* (the re-request cascade
> > produces many simultaneous in-flight jobs whose completions the budget defers), **0** in a lighting- *disabled*
> > fly soak, and 273 / 814,801 in the MP-4 smoke session — all from streaming waves, none from editing.
> >
> > **The only viable in-game driver** is therefore a heavy concurrent-meshing load (post-teleport streaming wave
> > or a large render-distance fill) *while* re-requesting a chunk that already has a deferred completion — not a
> > quiet world plus fast clicks. Cost/benefit before anyone builds that rig: **B26's prove-red is already the
> > decisive evidence** (restoring `Schedule || AlreadyInFlight` reds exactly B26 with the F1 signature "frame 1
> > scheduled 1, queue left 0"), and production shows the arm firing 273× post-fix with no visual regression and
> > no runaway (0.03 %). The remaining in-game repro confirms a *consequence* that the prove-red already pins.
> - Watch `MeshInFlightRetried` under a fluid-stress session for runaway re-meshing (the §3.2 Option C dirty-flag
>   escape hatch) — measured 0.03 % in the MP-4 smoke session, so the hatch stays deferred.

### MP-4 — Completion-pass unification (🟡, likely larger — see P-4 reconcile below)

- **P-4 reconcile (2026-07-24, see the drift update):** `ProcessMeshJobs` now takes a
  `PipelinePassBudget.Window` and iterates via a rotating-start snapshot (`_meshScanKeys` /
  `_meshScanCursor`). Before extracting, verify whether `LightingCompletionPass` already carries a window + rotating cursor; if it diverged, generalize the skeleton to carry them. This is the bulk of MP-4 now and likely pushes it past 🟡 — the rename shape (§9 Q3) is secondary to it. The
  "byte-identical candidates snapshot" note below still holds (removal is after-loop), but the *iteration order* is no longer raw dictionary order.
- **Scope:** §4.2. Generalize the skeleton's home (`refactor-safely` for the rename; lighting call sites + the frame simulator's driver update mechanically); add the cached mesh driver in `WorldJobManager`; `ProcessMeshJobs` becomes snapshot-keys → `RunMergeLoop` →
  `RunRemoveAndPromote` (candidates snapshot is byte-identical here for the same reason as lighting: the loop never adds to `MeshJobs`, removal is already after-loop).
  `ProcessGenerationJobs` is explicitly **excluded** — its budget-retry `continue` semantics don't fit the skeleton (same verdict as HF-2's audit).
- **New baseline (B27):** skeleton-order replay with a recording fake driver (pure — no world needed): 4 candidates, one stage-1 fault, one stage-2 fault; assert carried-over vs released+enrolled vs removed-after ordering matches the contract (the mesh-side B65 analog — and it doubles as a regression pin for the *lighting* skeleton after the rename). *(B-number: B24/B25 = MP-2, B26 = MP-3; MP-4 takes the next free number, B27 if it lands before MP-5.)*
- **Coordination note (LP-*):** LP-3 (lighting doc) edits the lighting driver's `ReleaseJob`; if both plans are in flight, land the rename first or rebase the smaller change — the suites arbitrate either order.
- **Acceptance:** universal gate **including the full lighting suite** (shared skeleton renamed)
    + in-game smoke.
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §4 (the HF-2 fault-isolation section names the shared skeleton for lighting — extend to meshing); lighting fidelity doc B7 entry gains the mesh-side note. **Serialization:** none.

> **Amended (2026-07-25) — MP-4 implemented, in-game smoke confirmed, committed. ✅ CLOSED.**
> - **§9 Q3 RESOLVED: hard rename (user sign-off).** `LightingCompletionPass` → `JobCompletionPass`,
>   `ILightingCompletionDriver<TKey>` → `IJobCompletionDriver<TKey>`, file + `.cs.meta` moved together via
>   `git mv` (GUID `958857f9…` preserved). No delegating alias. **The Rider MCP was not exposed in the
    > executing session**, so the rename was manual (`git mv` + a scoped `sed` over the 6 files / 36 occurrences
>   the grep census found) rather than `rename_refactoring` — the exhaustive grep + both builds + all 16 suites
>   are what arbitrate it. *Gotcha for the next executor:* renaming a `.cs` breaks `dotnet build` with
>   `CS2001: Source file … could not be found` until Unity regenerates the `.csproj` — refresh first.
> - **P-4 reconcile (the predicted bulk) was smaller than feared.** Verified against HEAD: the lighting
>   skeleton had **neither** a window nor a rotating cursor, while `ProcessMeshJobs` had both — but the
>   divergence was only *two parameters*; the per-job body was already line-for-line identical. Generalized as
>   optional `PipelinePassBudget.Window window = default, int startIndex = 0` (indexed loop, `count == 0`
>   early-out guarding the modulo). **The cursor stays owned by the caller** — advancing it is per-pass policy
>   (production gates it on `window.HasBudget` to keep the flag-off legs byte-exact), not a skeleton property.
>   Lighting + simulator call sites pass neither argument and are byte-identical. **MP-4 did not exceed 🟡.**
> - **The mesh driver is a private nested `MeshCompletionDriver`**, constructed once in the `WorldJobManager`
>   ctor (zero per-frame alloc), holding an owner ref. `RemoveAndPromote` is `MeshJobs.Remove` only.
> - **The out-of-range discard rider is CLOSED as provably redundant — do not re-add it.** Verified in code:
>   leaving view removes the chunk from `_chunkMap` **and** from `_meshBuildQueue` (`World.cs:3446–3454`), and
>   view distance is strictly inside the unload boundary. Therefore `IsBeyondUnloadDistance(coord) == true`
>   ⟹ `GetChunkFromChunkCoord` already returns null ⟹ the existing `chunk != null` guard already discards —
>   that IS the 21 % MP-1 measured. Skip-schedule-when-beyond-range cannot fire either (queued ⟹ in-view).
>   Adding either check would be dead code.
> - **What the rider became instead (D3, user sign-off: PROBE ONLY).** The live gap was the one
>   `WorldJobManager.cs`'s own counter docstring deferred to MP-4 by name: **stale-instance** merges — a live
>   chunk that is a *different lifecycle* than the job targeted. Captured as `MeshingJobData.TargetEpoch`, a
>   blittable `int` holding `ChunkData.LifecycleEpoch` at schedule time (**not** a `Chunk` reference in the
>   struct — it lives under `Assets/Scripts/Jobs/`, where the Burst rules ban managed fields), **paired with the
    > captured `ChunkData` reference** in `WorldJobManager._meshJobTargets`. Verified: the epoch is bumped only by
>   `ChunkData.Reset()` (pool recycle), **not** by view-distance deactivation. New counter
>   `MeshStaleInstanceMerges` + a diagnostics line; **the apply is unchanged** — the discard is evidence-gated on
>   an in-game reading, per MP-1's own method.
> - **Code-review round (`/code-review high`, 2 findings, both fixed — re-gated 337/337).**
>   - **Medium — the probe under-counted, which would have corrupted its own evidence.** The first cut compared
>     `LifecycleEpoch` *alone*. That counter is **per-instance**, and the dominant recycle path swaps the
>     instance rather than resetting it: `Chunk.Release()` nulls `ChunkData` and `Chunk.Reset()` re-links
>     whatever `RequestChunk` returns, so a freshly constructed successor starts at epoch 0 and compares
>     **equal** to a captured 0 → stale merge silently uncounted. Since §D3 gates the real discard on this
>     count, a spurious zero would have wrongly closed the rider. Fixed by restoring the **CP-3 pairing**
>     (`World.cs:928` — `current == admitted && epoch == admittedEpoch`): identity via `_meshJobTargets`
>     (same lifetime as `MeshJobs`: added in `ScheduleMeshing`, removed in `RemoveAndPromote`, cleared in
>     `Dispose`) **and** the epoch. *Lesson for future ABA guards here: `LifecycleEpoch` is never sufficient
      > on its own — always pair it with reference identity.*
>     - **Low — B27 leg 2 was flake-prone.** A 1 ms window armed just before `RunMergeLoop`, which also tests
>       `Expired` before the *first* candidate: any GC pause or preemption > 1 ms would break the pass with
>       nothing enrolled and red a baseline that gates `Validate All`. Widened to `WINDOW_BUDGET_MS = 50f` and
>       split the precondition ("job 1 was reached") from the conclusion, so a pre-emptive break now reports
>       itself explicitly instead of masquerading as a break-logic regression.
> - **Second code-review round (2026-07-25, during MP-5; 2 findings against MP-4 code, both fixed —
    > re-gated `Validate All` 340/340).** (1) *The probe's machinery outlived its own gate:* `CountMeshMerge`
>   is `[Conditional]`-gated, but the staleness expression and the `_meshJobTargets` insert/remove ran
>   unconditionally, so a release player maintained a dictionary for a counter that does not exist there.
>   Fixed without any `#if` — `[Conditional]` elides argument evaluation too, so the staleness test moved
>   *inside* `CountMeshMerge(key, chunk, targetEpoch)` and the dictionary writes became
>   `TrackMeshJobTarget`/`UntrackMeshJobTarget`. The load-bearing part is the field comment: **`_meshJobTargets`
    > is empty in release builds — never read it for behavior.** (2) *`MeshCompletionDriver._curJob` held its
    > released handles* until the next `CompleteJob` overwrote them — the fidelity-B7 stranded-container shape.
>   Now cleared (`= default`) at the end of `ReleaseJob`, **and symmetrically for `_curLightJob`** in the
>   lighting driver (user's call): the reviewer cited `_curLightChunk = null` as precedent, but that clears a
>   managed ref at job *start* — clearing the released job struct is a new convention, so it was applied to
>   both drivers rather than one. Neither `RemoveAndPromote` reads the scratch, and a stage-1 fault `continue`s
>   before `ReleaseJob`, so a retried job keeps its data. *Neither finding was reddable by any baseline — see
    > the §8.1 rider.*
> - **B27** (`Validate Meshing` 26 → **27**): the skeleton replayed world-free with a recording fake driver —
>   4 candidates (clean / not-complete / stage-1 fault / stage-2 fault) asserted as an exact hook-order string,
>   plus a deterministic mid-pass window break (spin-to-deadline, no sleep), the `startIndex` rotation, and an
>   empty-list + stale-cursor no-op leg.
> - **Prove-red confirmed, and it corrected a planning assumption.** Moving `ReleaseJob` out of the merge
>   `finally` reds **exactly B27** (`1 OF 27 FAILED`, the missing `Release(4)` after `MergeFault(4)`) — but
>   **lighting B65 stayed green (88/88)**, contrary to the plan's expectation that both would red. B65 pins
>   fault *isolation and recovery*; it never observes whether the release ran on the fault path. **B27 is
    > therefore not a duplicate of B65** — it closes the release-on-fault ordering hole, exactly the
>   stranded-container mechanism fidelity B7 was opened for. Restored → green.
> - **Gates:** `dotnet build` both assemblies clean; `Validate Meshing` **27/27**, `Validate Mesh Build Queue`
>   9/9, `Validate Lighting Engine` 88/88, **`Validate All` 337/337** across 16 suites.
> - **✅ In-game smoke CONFIRMED (2026-07-25, editor play mode).** Visual: streaming, fluid flow, lighting
>   updates, place/break all correct; no warnings observed. Objective, session-cumulative:
>
>   | Probe | Reading |
>                   |---|---|
>                   | merge attempts | **32,728** (routing demonstrably live — this is what a broken routing would flatline) |
>                   | gone-chunk discards | 406 (1.2 %) |
>                   | **stale-instance** | **0 / 32,728** |
>                   | F1 in-flight retries | 273 / 814,801 (0.03 % — no runaway; §3.2 Option C stays deferred) |
>                   | F8 request drops | 0 / 7,079,946 |
>                   | F4 recycled draw-refs | 0 / 32,322 |
>
>   Editor log for the whole session: **0 `[MESHING]` lines, 0 `ObjectDisposedException`, 0 NRE** — the
>   fidelity-B7 cascade falsifier came back empty. Pipeline drained to **0 in-flight across all three job
    > types** with 819 chunks loaded, confirming the window break still enrolls correctly (a broken break would
>   pin `MeshJobs` non-zero).
> - **✅ §D3 RESOLVED — the discard is NOT needed, and the reason is structural. Do not re-propose it.**
>   Driver: 8 teleport legs across the unload boundary (load distance 13 → 15-chunk boundary), deliberately
>   hopping *while jobs were in flight* (7–10 at each hop) — the discriminating driver, not a fly soak.
>   `stale-instance` stayed 0 throughout. The mechanism is visible in the numbers: leg 3's 7 in-flight jobs
>   became exactly **+7 gone-chunk discards** — those chunks left `_chunkMap` so the merge resolved null, while
>   their `ChunkData` stayed pinned. **`ChunkUnloadDecision.Evaluate` returns `DeferJobRunning` (line 103,
    > second arm, above every unload arm) whenever a mesh job is keyed on the chunk**, so an in-flight job
>   structurally prevents its own `ChunkData` from being recycled. The stale-instance window is therefore not
>   merely rare — it is closed by an existing invariant. The probe is retained as a **structural tripwire**:
>   a non-zero reading means that pin was violated, which is an unload-path bug to investigate, not a cue to
>   add a discard.
> - ~~**Still pending (unrelated to MP-4):** MP-3's own in-game repro.~~ **Retired 2026-07-25** — unreachable by
>   any edit-rate recipe (F1 is load-driven); MP-3 is closed on B26's prove-red + production telemetry. See its
>   Amended note.

### MP-5 — GS-5 Phase 0.5: renderer-ownership split (🟢, independently harmless)

- **Scope:** `SectionRenderer.cs` only:
    1. Add the occlusion seam: `SetOcclusionCulled(bool)` writing `_meshRenderer.forceRenderingOff`
       — the **only** writer of that flag in the codebase, reserved for the future
       `VisibilityManager` (unused by production until GS-5 Phase 2/3).
    2. Guarantee the reset invariant: `Clear()` (pool recycle) resets `forceRenderingOff = false`
       (a recycled section must never inherit a culled state — the pool-reset-safety rule; the conservative direction is "render", per culling doc §7.5).
    3. Confirm-and-document: `UpdateMeshNative` and `Clear()` keep owning **only** `SetActive`
       ("has geometry"); XML-doc the two-axis ownership contract on the class.
- **New baselines (renderer fixture — MP-4 took B27, so MP-5's are **B28/B29/B30**; suite tip is B27 / 27 baselines,
  `Validate All` 337 across 16 suites):** (a) `UpdateMeshNative` never writes
  `forceRenderingOff` (set it true externally, run a non-empty then an empty update, assert it survived both — the non-interference invariant); (b) `Clear()` resets it false; (c)
  `SetOcclusionCulled` round-trips and does not touch `activeSelf`. Prove-red: temporarily make
  `UpdateMeshNative` clear the flag → (a) reds.
- **Acceptance:** universal gate (renderer baselines B12–B16 especially) + in-game smoke (no visual change — the flag is never set in production yet). Verify against
  `mcp__unity-api__get_class_reference("MeshRenderer")` that `forceRenderingOff` is the correct member/signature before writing code (per CLAUDE.md API rules).
- **Doc-sync (same commit):** flip `VISIBILITY_CULLING_ARCHITECTURE.md` §5 Phase 0.5 checkbox + §7.3/§8 "still open" notes; update `PERFORMANCE_IMPROVEMENTS_REPORT.md`'s GS-5 prerequisite line (report edit is a status-line flip, not a re-audit); `SUB_CHUNK_MESHING_ARCHITECTURE.md`
  §3.2 rendering-strategy note. **Serialization:** none.

> **Amended (2026-07-25) — MP-5 implemented, in-game smoke confirmed, committed. ✅ CLOSED.**
> - **Shipped exactly as scoped, in `SectionRenderer.cs` only.** `SetOcclusionCulled(bool)` writes
>   `_meshRenderer.forceRenderingOff` and is the **only code that *sets* it** (pre-change re-verification:
>   `Grep` over `Assets/` returned 0 pre-existing writers; the only other repo hit is a Unity package's
>   own test under `Library/PackageCache/`). **`Clear()` is the one other writer and is reset-only** —
>   a code-review round (2026-07-25) caught the first cut's prose claiming a literal *sole* writer,
>   which the culler would have read as "nothing else ever touches this flag". The consequence is now
>   stated in culling doc §7.3: a `VisibilityManager` that memoizes its culled-set **must re-issue after
    > a pool recycle**, since `Clear()` un-culls (failure direction: over-render, never a hole).
>   `Clear()` resets the flag to false alongside its existing
>   `SetActive(false)`, so a recycled section can never inherit a culled state (pool-reset-safety;
>   §7.5's conservative "render" direction). The two-axis contract is XML-documented on the class
>   **and** pointed at from `UpdateMeshNative` ("owns the *has geometry* axis only") and `Clear()`
>   ("resets both axes") — F3's mistake was made at the member level, which is where the next editor
>   looks. **Nothing in production calls the setter**; the culler (GS-5 Phase 2/3) is its first caller.
> - **Decisions (user sign-off, all three the recommended option).** (1) **Bare write**, no cached
>   mirror + early-out: a second copy of the truth would need its own `Clear()` reset and can desync
>   against an external write, for a bool store that costs nothing — GS-5's manager memoizes on its
>   own side (the "thin, swappable presentation layer" of culling doc §8). (2) **Setter only**, no
>   `IsOcclusionCulled` getter — no consumer exists today and it is trivially additive later.
>   (3) **Class-level contract + per-member pointers** for the docs.
> - **API verified before writing code** (CLAUDE.md rule): `forceRenderingOff` is a settable `bool`
>   on `UnityEngine.Renderer`, **inherited** by `MeshRenderer` — `get_class_reference("MeshRenderer")`
>   lists only its own 9 members and does **not** show it; `get_class_reference("Renderer")` does.
> - **B28–B30** on the **MH-6 renderer fixture** (`SectionRendererTestFixture`, not `MeshingTestWorld`),
>   which gained a settable `OcclusionCulled` property so a baseline can stamp the flag *externally*,
>   as the future `VisibilityManager` would: **B28** non-interference (two independent legs — a
>   non-empty and an empty `UpdateMeshNative` — each re-stamping the flag first, plus a control that
>   the accessor observes a real change), **B29** `Clear()` resets both axes (flag asserted true
>   immediately before the call, so the post-condition can't pass vacuously), **B30** setter
>   round-trip with `activeSelf` unchanged. `Validate Meshing` 27 → **30**.
> - **Prove-red confirmed:** inserting `_meshRenderer.forceRenderingOff = false;` at
>   `UpdateMeshNative`'s **entry** (so it covers both the non-empty and the empty-return paths) reds
>   **exactly B28** (`29/30`), and **both** of its legs report — the empty-path leg is not vacuous.
>   B29/B30 and every job baseline stayed green. Restored → 30/30.
> - **Gates:** `dotnet build` both assemblies clean; `Validate Meshing` **30/30**, `Validate Mesh
>   Build Queue` 9/9, `Validate Lighting Engine` 88/88, **`Validate All` 340/340** across 16 suites.
> - **Note for GS-6 (BRG):** the flag is written through one `SectionRenderer` method, so a later
>   BatchRendererGroup conversion swaps the toggle's *implementation*, not its callers (§4.3).
> - **✅ In-game smoke (2026-07-25, high-speed fly-over, lighting disabled).** No visual change, as
>   predicted — production sets the flag nowhere. Objective, session-cumulative:
>   **merge attempts 7,969** (routing live — a broken driver flatlines this), gone-chunk discards 422
>   (5.3 %), **stale-instance 0**, F4 recycled draw-refs 0 / 754, F8 request drops 0 / 15,837.
>   *`retries=0` is the EXPECTED reading for this driver and says nothing about MP-3* — F1 is
>   load-driven via the lighting re-request cascade, which a lighting-disabled fly-over does not
>   generate (see the MP-3 CORRECTION note).
> - **Code-review round (`/code-review high`, 2026-07-25) — 1 MP-5 finding, fixed.** The prose claimed a
>   literal *sole* writer of `forceRenderingOff` while `Clear()` also writes it. Corrected everywhere the
>   claim appeared (see the first bullet); B29 already pinned the behavior, so this was a contract-clarity
>   fix, not a code fix. The round's other two findings were against MP-4 code — see the MP-4 note.

### MP-6 — Draw-tail re-home (🟢, small behavior change)

- **Decision (2026-07-24, user sign-off):** take the **drop-the-stagger option** below — trigger the animation at apply time — AND fix the lifecycle hole. Also note the pacing is now P-4's §5.3 drain (`drawApplyBudgetMs`), not the old one-per-frame code (see the drift update); MP-6 layers onto that. The two Scope options below are preserved for context, but the option is now chosen.
- **Scope:** retire F4's staleness while keeping the visual behavior decision explicit:
    - **Default (recommended): keep the paced queue, fix its lifecycle.** Store `(Chunk, ChunkCoord)`
      (or re-resolve via `_chunkMap` at drain) and skip entries whose chunk no longer occupies that coord; clear `ChunksToDraw` in the same teardown paths that clear `_meshBuildQueue`; rename the stage to what it is (`_loadAnimationQueue` / `TriggerLoadAnimation()` — `refactor-safely`), and move the enqueue out of `Chunk.ApplyMeshData` into `ProcessMeshJobs` (the chunk stays queue-agnostic, matching the MR-6 ownership style).
    - **Option (needs user sign-off — visual change):** drop the one-per-frame pacing and trigger the animation directly at apply time. Do NOT take this silently; the stagger may be a deliberate aesthetic. Ask when executing.
- **Optional rider (§8.1, executor's call):** MP-6 moves the draw-tail enqueue out of
  `Chunk.ApplyMeshData` into the completion pass, i.e. it edits the mesh driver's merge hook anyway — the cheapest moment to add `IMeshCompletionHost` and finally let baselines drive the **real**
  `MeshCompletionDriver` (B27 only replays the skeleton). Take it only if the rework genuinely lands in that hook; skip it otherwise, and do not schedule it standalone.
- **Acceptance:** universal gate + in-game visual check: load animations play once, at the right position, under streaming + a pool-churn session (sprint one direction so recycling is hot); MP-1's probe-3 counter goes to zero.
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §4 step 8 + §5.3 final-draw subgraph (rename + actual semantics); `SUB_CHUNK_MESHING_ARCHITECTURE.md` if it names `CreateMesh`. **Serialization:** none.

> **Amended (2026-07-25) — MP-6 implemented, suite-verified, in-game confirmed. ✅ CLOSED.**
> - **The queue was retired, not repaired (user sign-off).** Once §9 Q2's decision — trigger the animation at
>   apply time — is taken literally, the queue has no remaining purpose: post-MR-5 its only work was calling
>   `CreateMesh` → `PlayChunkLoadAnimation`. So `World.ChunksToDraw`, the step-8 drain, and `Chunk.CreateMesh`
>   are **gone**; `Chunk.TriggerLoadAnimation()` is called directly by the mesh completion pass right after
>   `ApplyMeshData`. Three of the scope bullets above are therefore satisfied *by construction* rather than
>   implemented: **F4's lifecycle hole is eliminated** (no cross-frame `Chunk` reference exists to go stale —
>   strictly stronger than the coord/epoch pairing the "keep the paced queue" option would have needed),
>   **clear-on-teardown is vacuous** (nothing survives a frame, so the three `_meshBuildQueue.Clear()` sites
>   need no sibling), and the enqueue did move out of `Chunk` — to nowhere. `Chunk` is queue-agnostic; so is
>   everything else.
> - **MP-1's F4 probe retired with it** (`DrawQueueRecycledRefs` / `DrawQueueDequeues` / `CountDrawQueueDequeue`
>   and its diagnostics line). It observed dequeues; there are none. Its 0-readings were never the evidence
>   anyway — see §9 Q1 and the MP-1 Amended note (the probe could not see reuse-at-a-new-coord).
> - **`Settings.drawApplyBudgetMs` retired** (field + its Performance-tab slider). Its subject no longer
>   exists, and its tooltip had been wrong since MR-5 — it claimed to bound "chunk activation"/GPU work when
>   the drain only triggered animations. Settings load via `JsonUtility.FromJsonOverwrite`, so a stale key in
>   an existing settings file is silently ignored — **no migration, and no serialization surface** (the §6
>   tripwire holds: nothing derived is persisted). `World.prefab`'s orphaned key is pruned on the Editor's
>   next reserialize. **Consequence to know:** `enablePipelineTimeBudgets = false` no longer restores a
>   one-per-frame draw trickle — that legacy leg went with the stage. Doc-synced into
>   [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) §5.3 (+ its §3.4 ceiling
>   list, now four). The standing `P4BackpressureBenchmark` was swept, not broken: its fill predicate drops the
>   `ChunksToDraw.Count` term because in-flight `MeshJobs` **is** the whole mesh tail now.
> - **The §8.1 `IMeshCompletionHost` rider was taken** (user sign-off) — its precondition held exactly: MP-6's
>   entire behavior change is one branch in the mesh driver's merge hook, so the seam that makes that branch
>   testable was cheapest here and nowhere else. `MeshCompletionDriver` moved out of `WorldJobManager` into
>   `Helpers/MeshCompletionDriver.cs` as a **public** class (the editor assembly cannot see `Assembly-CSharp`
>   internals) taking an `IMeshCompletionHost`; `WorldJobManager` implements the host on `this` (the
>   `IMeshDrainHost`/`World` pattern), and the ctor line is unchanged.
>   **The probes deliberately did NOT move onto the interface:** an interface member cannot be
>   `[Conditional]`, so routing `CountMeshMerge` through the host would have resurrected exactly the machinery
>   the 2026-07-25 review round removed from release builds. It stays inside the production `TryApplyMesh`
>   body. *(Folding resolve+apply into one `TryApplyMesh` is what keeps `Chunk` and `World` out of the
    > interface — the fake host's whole premise.)*
> - **B31–B33** (`Validate Meshing` 30 → **33**), in the `Completion` partial beside B27, driving the **real**
>   driver through the **real** skeleton with a recording fake host (jobs tagged by the blittable
>   `MeshingJobData.TargetEpoch`, so no native buffers, no `Chunk`, no `World`): **B31** apply → animate
>   mapping over 3 jobs incl. a gone chunk (which discards without animating **and still releases** — the MR-6
>   single-release-site invariant, evidence-only before this), **B32** a faulting apply never animates, still
>   releases, and does not abort the pass, **B33** the `_curJob` scratch — each release gets its own job, and
>   the scratch is cleared afterwards.
> - **Prove-red confirmed, one mutation per baseline, each reds EXACTLY its own (32 OF 33) with the diagnostic
    > signature spelled out:** hoisting `TriggerLoadAnimation` out of the `if` → B31, `Animate(2)` on the gone
>   chunk; animating from a `catch` → B32, `Animate(1)` on the faulted job; deleting `_curJob = default` →
>   B33, released epochs `[101, 102, 103, **103**]` — job 3's buffers returned twice. That last one is the
>   code-review finding of 2026-07-25 that **had to be accepted on reasoning because no baseline could observe
    > it**; §8.1's stated payoff is now cashed. Restored → 33/33.
> - **B32 logs one deliberate `[MESHING]` error per suite run** (it exercises the real driver's stage-2 fault
>   hook). Expected noise, the CP-6/NS-1 injected-fault precedent — noted in the partial's docstring and in the
>   scenario name so nobody chases it.
> - **Gates:** `dotnet build` both assemblies clean; `Validate Meshing` **33/33**, `Validate Mesh Build Queue`
>   9/9, `Validate Lighting Engine` 88/88 (shared `JobCompletionPass` intact), **`Validate All` 343/343** across
>   16 suites. The Rider MCP was **not exposed** (as in MP-4/MP-5), so the `CreateMesh` → `TriggerLoadAnimation`
>   rename was manual + an exhaustive `Grep` sweep; the single production call site made that trivial.
> - **Code-review round (`/code-review high`, 2026-07-25) — 7 findings, all 7 fixed, none dropped; re-gated
    > `Validate All` 343/343.** One Medium: **`IMeshCompletionHost` was implemented implicitly**, making
>   `ReleaseJobData` (which returns pooled native buffers) a *public* member of a type reachable as
>   `World.Instance.JobManager` — a stray second call leaves two in-flight jobs renting the same
>   `MeshDataJobOutput`. Both in-repo precedents are explicit (`World : IMeshDrainHost`, and this same file's
>   `IJobCompletionDriver<ChunkCoord>` with the comment "Explicit so they don't widen WorldJobManager's public
>   surface"); now matched. A grep sweep found zero callers outside the driver, so the narrowing was
>   call-site-free and the compiler is its exhaustive gate. Six Low: the region comment cited the
>   `IMeshDrainHost` pattern while misdescribing it; **`TrackMeshJobTarget` ran *after* `MeshJobs.Add`** under
>   a comment claiming the catch "rethrows without having added either" — false, the catch never removes from
>   `MeshJobs`, so a throw in that window left an enrolled job pointing at recycled buffers (**MP-4 code, not
    > MP-6** — the MP-5 round's pattern repeating); the stage-2 fault message asserted "previous mesh kept" when
>   the upload may well have landed and only the animation thrown; the retired drain's `ChunkGameObject != null`
>   guard was dropped by omission; "the **five** ms ceilings" went stale in three live places; and the new
>   pipeline-doc callout contradicted itself ("no step 8 … a *ninth* stage").
>   - **F5 decision (user):** the guard goes **inside `PlayChunkLoadAnimation`'s else branch** — the only line
>     that dereferences `ChunkGameObject` — rather than at the call site, so the chunk owns its own liveness
>     instead of every future caller re-checking it. Deliberately *not* an early return: `_hasPlayedLoadAnimation`
>     must still latch, since the retry path the old queue provided no longer exists. **It does not make teardown
      > crash-proof** — the preceding `ApplyMeshData` → `UpdateMeshNative` still dereferences the same GameObject
>     unguarded; if that path ever fires, the fix belongs in teardown ordering, not more null checks.
>   - **Deliberately NOT edited:** the fourth "five ceilings" hit,
>     [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) §3's *Review round 2*
>     record — that is history of what was true then, the same append-only reasoning that protects
>     `Documentation/Performance/*BENCHMARK.md`. Only current-behavior prose was corrected.
>   - **No finding got a new baseline, and none could:** F1 is a compile-time visibility narrowing, F3 an
>     OOM-only path, F5 needs a Unity-destroyed `GameObject` on a real `Chunk` — which the runner's
>     `World.Instance` isolation guard forbids standing up. The obligation was that the existing 343 stay green.
> - **✅ In-game visual check CONFIRMED (2026-07-25, user).** The deliberate visual change behaves correctly
>   under every driver tried: normal streaming, a high-speed fly-over, teleports, and rapid alternation
>   between `/teleport 0 0` and `/teleport 5000 5000` (the hardest pool-churn + concurrent-meshing case, and
>   the one the retired stagger would have most obviously masked). Chunks load and animate correctly
>   throughout. Toggle matrix, animations **enabled** at world load: on → correct; toggled **off** mid-session
>   → correctly reverts to instant pop-in; toggled back **on** → animation correct again.
> - **Pre-existing limitation surfaced by that matrix (NOT an MP-6 regression — do not attribute it here).**
>   Starting with animations **disabled** and enabling them mid-session does *not* retroactively animate:
>   `ChunkLoadAnimation` is `AddComponent`ed in **one place only**, `Chunk`'s constructor, behind
>   `if (settings.enableChunkLoadAnimations)` — a deliberate "avoid runtime AddComponent (boxing/GC)"
>   optimization from commit `a41834b8`, long predating this arc. A `Chunk` built while the setting was off
>   therefore has `_loadAnimation == null` for the rest of its life, and all three readers
>   (`Reset`, the re-anchor path, `PlayChunkLoadAnimation`) additionally require `_loadAnimation != null`, so
>   they fall to the snap branch no matter what the setting later says. The reverse direction works because
>   the component already exists and only the *setting* is re-read per call. Note the failure is
>   **inconsistent rather than total**: `Chunk`s constructed after the toggle (pool growth) do animate.
>   MP-6 touched none of this — it neither added nor moved the component-creation gate.

### MP-7 — Naming & wiring hygiene (🟢)

- **Scope:** rename `MeshGenerationJob`'s neighbor fields to compass names matching
  `NeighborMapSet` (`NeighborBack→NeighborS`, `NeighborFront→NeighborN`, `NeighborLeft→NeighborW`,
  `NeighborRight→NeighborE`, + the four diagonals and the eight light twins) via `refactor-safely`
  — naming-only inside a Burst job (no semantic change; B18–B21 + full suite pin the +X plane and output equality). Update the wiring block, which becomes self-checking (`NeighborS = jobData.Neighbors.NeighborS`). Refresh `CHUNK_LIFECYCLE_PIPELINE.md` §9.5's text with MP-1's probe reality (convention now observable).

    > **Anchors re-verified 2026-07-25 (the audit's `WJM:355–371` is ~170 lines stale — MP-4/MP-6 moved it).**
    > The wiring block is **`WorldJobManager.cs:531–547`** — 8 voxel lines, the `LightMap` line, then 8 light
    > lines. The job's fields are `MeshGenerationJob.cs:52–74` (voxel) and `:81–102` (light).
    > **The mapping is already unambiguous and three independent sources agree** — the job's own field
    > comments (`NeighborBack // South (-Z)`), the wiring block, and `Jobs/Data/NeighborMapSet.cs:16–19` fed
    > by `AcquireVoxelMap(center.Neighbor(dx, dz))` (N = `(0,+1)`, E = `(+1,0)`, S = `(0,-1)`, W = `(-1,0)`).
    > So: **Back→S, Front→N, Left→W, Right→E, FrontRight→NE, BackRight→SE, BackLeft→SW, FrontLeft→NW**, and
    > the eight `Light*` twins identically. The executor should re-confirm rather than re-derive.
- **Acceptance:** universal gate (byte-identical output — `OutputsEqual` across the suite is the real guard) + in-game seam check (fly a chunk border; no doubled/missing border faces).
- **Doc-sync:** pipeline doc §9.5; meshing fidelity doc §2 if it names the old fields. **Serialization:** none.

---

## 8. Extension roadmap (post-MP-7, in intended order)

| Version        | Extension                                                                                                                                                                                                                                                                                                                 |
|----------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**         | **Drain park/promote** (F7): parked set for gate-failing queued chunks, promoted by the events lighting already hooks (generation/load/lighting completion). Only with MP-1 counter + profiler evidence that gate re-probing costs real frame time; rides LP-2's shared `NeighborReadinessDecision` facts if LP-2 landed. |
| **v2**         | **Dirty-while-in-flight re-enqueue** (§3.2 Option C) — only if MP-3's leave-queued retry shows measurable redundant-rebuild cost.                                                                                                                                                                                         |
| **v3+**        | **GS-5 Phases 1–3** (connectivity masks in `MeshDataJobOutput`, `VisibilityManager`, PVS) — owned by `VISIBILITY_CULLING_ARCHITECTURE.md`; lands on MP-5's seam and §4.3's publish point. GS-6 / MR-8 follow per the performance report's sequencing.                                                                     |
| ~~MP-6 rider~~ | ✅ **DONE (2026-07-25, with MP-6)** — `IMeshCompletionHost` shipped; the suite drives the real `MeshCompletionDriver` via a recording fake host (B31–B33). See §8.1's closing note.                                                                                                                                       |

### 8.1 `IMeshCompletionHost` — closing the driver-coverage gap (proposed 2026-07-25)

**The gap.** B27 replays the completion *skeleton* with a recording fake driver over `int` keys, so nothing in any suite executes the production `MeshCompletionDriver`. Everything it does is reached through its `WorldJobManager` owner — `MeshJobs[key].Handle` (real `JobHandle`s),
`_world.GetChunkFromChunkCoord` (a live chunk map), `chunk.ApplyMeshData` (real `SectionRenderer`s → GPU upload), `_meshOutputPool` / `ReleaseMeshingJobInputs`, `_meshJobTargets` — and constructing that needs a functioning `World`, exactly what the runner's `World.Instance` isolation guard forbids.

**The fix is the pattern MP-2 already shipped one phase earlier.** `MeshDrainPolicy.Drain` takes an
`IMeshDrainHost` that `World` implements on `this` (cached, zero per-frame alloc), which is how **B25**
drives the real drain loop with scripted facts. Give the completion driver the same seam:

```csharp
/// The collaborators MeshCompletionDriver needs from its owner — the IMeshDrainHost sibling.
public interface IMeshCompletionHost
{
    bool TryGetJob(ChunkCoord key, out MeshingJobData job);
    bool TryApplyMesh(ChunkCoord key, MeshDataJobOutput output); // resolve + apply; false = chunk gone
    void ReturnOutput(MeshDataJobOutput output);
    void ReleaseInputs(in MeshingJobData job);
    void CountMerge(ChunkCoord key, bool chunkGone, int targetEpoch);
    void RemoveJob(ChunkCoord key);
}
```

`WorldJobManager` implements it on `this`; the driver takes the host instead of the owner ref. Folding resolve+apply into one `TryApplyMesh` is what makes the fake host possible — it records or throws instead of uploading, with no `Chunk` or `World` in sight.

**What it buys over B27** (which pins ordering, the window break, and rotation — not the driver):

- the **hook → effect mapping**: that the gone-chunk branch still releases (the MR-6 single-release-site invariant, today evidence-only), that a merge fault keeps the previous mesh, that removal is after;
- **the `_curJob` scratch lifecycle** — a fake host counting `ReturnOutput` turns a double-release into a red baseline. That is precisely the code-review finding of 2026-07-25 (clear the released scratch), which had to be accepted on reasoning because no baseline could observe it.

**Cost/risk.** 🟡: one interface, mechanical rewiring of the live merge path (the only real risk —
`Validate All` gates it), a recording fake host, ~2–3 baselines (**B31+**).

**Two alternatives considered and not chosen.** *(a)* Reflection-construct the driver against an uninitialized `WorldJobManager` with private fields poked in — zero production change, precedent exists in the suite, but it binds baselines to **private field names** and fails for reasons unrelated to the behavior it guards. *(b)* Stand up a real `World` + `Chunk` for a genuine `ApplyMeshData` — rejected:
it needs play mode or a heavyweight edit-mode world builder, re-tests what B12–B16/B28–B30 already cover on the renderer side, and its one unique addition (a faulting `Handle.Complete()`) is what B27's fake driver already replays deterministically.

**Why it is a rider, not a phase.** The uncovered surface is narrow — B27 covers sequencing, the renderer baselines cover the apply target, and the MP-1/MP-4 diagnostics cover live routing in-game (2026-07-25 fly-over: 7,969 merge attempts, 0 stale-instance). Do it *if* MP-6's rework touches the driver anyway; do not schedule it on its own.

> ✅ **SHIPPED with MP-6 (2026-07-25).** The precondition held exactly: MP-6's whole behavior change is one
> branch in the merge hook, so this was the cheapest possible moment. Deltas from the sketch above:
> - **Six members, not seven.** `CountMerge` was deliberately left OFF the interface — an interface member
>   cannot be `[Conditional]`, so hoisting the probe would have undone the same-day review fix that keeps its
>   machinery out of release builds. It lives inside the production `TryApplyMesh`. `TryGetJob` became
>   `IsJobComplete` + `CompleteJob` (returning the data) so the fake never needs a real `JobHandle`;
>   `ReleaseInputs`/`ReturnOutput` merged into one `ReleaseJobData` (they are a single MR-6 release site);
>   and MP-6 added `TriggerLoadAnimation`.
> - **The driver moved out of `WorldJobManager`** to `Helpers/MeshCompletionDriver.cs` and had to be **public**
>   — `internal` does not cross into the editor assembly. It keeps `_curJob`, both fault logs, and the scratch
>   clear; `WorldJobManager` implements the host on `this`.
> - **Both predicted payoffs cashed.** The gone-chunk branch's release is now pinned (B31), and the
>   `_curJob` scratch lifecycle — the finding this section was written around, accepted on reasoning because
>   nothing could observe it — reds B33 with an explicit double-return (`[101, 102, 103, 103]`).

---

## 9. Open questions

1. **MP-1 probe results** — how often do the in-flight drop (F1), silent request drops (F8), recycled draw-queue refs (F4), and out-of-range mesh-result discards (2026-07-23 rider) fire in real sessions? Gates MP-3's go/no-go and MP-6's urgency; answers land here as Amended lines.
2. **MP-6 pacing** — ✅ **RESOLVED (2026-07-24):** drop the one-chunk-per-frame stagger and trigger the animation at apply time (user sign-off on the visual change), plus fix the recycled-ref lifecycle hole + clear-on-unload. The stagger is now the P-4 §5.3 budgets-off leg, not the old code — see the 2026-07-24 drift update. **Executed 2026-07-25 by retiring the queue outright** (second sign-off): taken literally, "trigger at apply time" leaves the stage with nothing to do, and deleting it *eliminates* the lifecycle hole instead of patching it.
   `drawApplyBudgetMs` retired with its subject. See the MP-6 Amended note.
3. **Skeleton rename shape (MP-4)** — ✅ **RESOLVED (2026-07-25):** hard rename to `JobCompletionPass` /
   `IJobCompletionDriver<TKey>` (user sign-off), no delegating alias; file + `.cs.meta` moved together, GUID preserved. The P-4 window + rotating-cursor reconcile landed as two optional `RunMergeLoop` parameters with the cursor left in the caller — see the MP-4 Amended note.

---

## Document History

* **v1.8** - MP-6 implemented + in-game confirmed (2026-07-25) — **the MP-1…MP-6 arc is CLOSED, only MP-7 remains**: the **draw-tail retirement**. `World.ChunksToDraw`, `World.Update`'s step 8, `Chunk.CreateMesh`, MP-1's F4 probe and `Settings.drawApplyBudgetMs` are all **deleted**; `Chunk.TriggerLoadAnimation()` is now called by the mesh completion pass immediately after `ApplyMeshData`, so no `Chunk` reference survives a frame — **F4's lifecycle hole is eliminated structurally**, and clear-on-teardown is vacuous. Took the **§8.1
  `IMeshCompletionHost` rider** (its precondition held: the whole change is one branch in the merge hook): `MeshCompletionDriver` moved to `Helpers/` as a public class behind a 6-member host interface implemented on `WorldJobManager`, deliberately **excluding** the `[Conditional]` merge probes. **B31–B33** drive the real driver through the real skeleton with a fake host; each prove-red mutation reds exactly its own baseline, including the double-return (`[101, 102, 103, 103]`) that finally observes the 2026-07-25 scratch-lifecycle review finding.
  `Validate Meshing` 30 → **33**, **`Validate All` 343/343**. Doc-synced `CHUNK_LIFECYCLE_PIPELINE.md`
  §4 + §5.3, `SUB_CHUNK_MESHING_ARCHITECTURE.md` §4.4, this fidelity doc's §4 (tip B30 → B33), `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` §5.3 (the P-4 rider it documents is superseded; the budgets-off draw trickle no longer exists), the meshing-suite skill reference, and one stale call-site line in `DEBUG_METHODS_EXAMPLES.md`.
* **v1.7** - **MP-3 declared FULLY CLOSED (2026-07-25, user decision) — the in-game repro is retired, not owed.** A third attempt (MCP-driven ultra-high-speed edit sequences, on top of the two scripted `EditorApplication.update` probes) again never fired the in-flight arm, exactly as the CORRECTION predicts: F1 is **load-driven**, so no edit-rate recipe can reach it. MP-3 stands on B26's prove-red plus production telemetry (273 / 814,801 retries in a real session). Also cleared the plan's stale status markers now that MP-1…MP-5 are all committed (phase
  table ✅ for MP-1/MP-2/MP-3, "uncommitted"/"smoke pending" headers on MP-2/MP-4/MP-5). **No open items remain in the MP-1…MP-5 arc.**
* **v1.6** - MP-5 implemented (2026-07-25, uncommitted; in-game smoke pending): the GS-5 Phase 0.5 renderer-ownership split (F3) — `SectionRenderer.SetOcclusionCulled(bool)` as the codebase's sole writer of `MeshRenderer.forceRenderingOff`, `Clear()` resetting it on pool recycle, and the two-axis ownership contract XML-documented on the class plus `UpdateMeshNative`/`Clear()`. Decisions: bare write (no cached mirror), setter only (no getter), class + per-member docs. **B28–B30** on the MH-6 renderer fixture (non-interference over both apply paths,
  recycle reset, setter round-trip vs `activeSelf`); prove-red at `UpdateMeshNative`'s entry reds exactly B28 with both legs reporting. `Validate Meshing` 30/30, `Validate All` **340/340**. Doc-synced the culling doc (§5 Phase 0.5 ✅, status line, §7.3, §8, Phase 3 renderer step), `PERFORMANCE_IMPROVEMENTS_REPORT.md` GS-5 prerequisite + recommendation, `SUB_CHUNK_MESHING_ARCHITECTURE.md` §3.2 (ownership table), meshing fidelity tip B27 → B30. **In-game smoke confirmed** (fly-over: 7,969 merge attempts, 0 stale-instance, no visual change). Includes a
  `/code-review high` round: MP-5's "sole writer" prose corrected to "only *setter*" (`Clear()` is reset-only — the culler consequence now stated in culling doc §7.3), plus two MP-4 fixes (probe machinery behind `[Conditional]`; released job scratch cleared in **both** completion drivers) and the new **§8.1** `IMeshCompletionHost` rider — the seam that would let baselines drive the real `MeshCompletionDriver`, scheduled as an optional MP-6 rider because neither review finding was reddable by any suite.
* **v1.5** - MP-4 implemented (2026-07-25, uncommitted; in-game smoke pending): the completion-pass unification — hard rename to `Helpers/JobCompletionPass.cs` / `IJobCompletionDriver<TKey>` (§9 Q3 resolved), the P-4 window + rotating start generalized as two optional `RunMergeLoop` parameters (cursor stays caller-owned), `ProcessMeshJobs` routed through a cached nested `MeshCompletionDriver`. **B27** (skeleton-order replay via a recording fake driver); prove-red reds exactly B27 — *lighting B65 stayed green*, so B27 closes a real release-on-fault gap
  rather than duplicating B65. `Validate Meshing` 27/27, `Validate All` 337/337. The out-of-range discard rider is **closed as provably redundant** (view-distance removal already precedes the unload boundary); it became the D3 **probe-only** stale-instance counter (`MeshingJobData.TargetEpoch`, blittable epoch — no managed field under `Jobs/`). Doc-synced `CHUNK_LIFECYCLE_PIPELINE.md` §4 + §10, lighting fidelity B7 + registry row, meshing fidelity tip B26 → B27.
* **v1.4** - MP-3 implemented (2026-07-24, uncommitted; in-game repro pending): the F1 in-flight lost-update fix as the shared `MeshingScheduleDecision.DequeuesChunk` mapping (Option A — production + B26 share it), `ScheduleMeshing` switch collapsed, MP-1 counter relabeled consumed → retried. **B26** (pure mapping + two-frame drain scenario); prove-red reds exactly B26; `Validate Meshing` 26/26, `Validate All` 336/336. Doc-synced `CHUNK_LIFECYCLE_PIPELINE.md` §5.3 + §9.5 + meshing fidelity §4 (`SUB_CHUNK_MESHING_ARCHITECTURE.md` verified no-op).
* **v1.3** - MP-2 implemented (2026-07-24, uncommitted): `MeshingScheduleDecision` (B24) + `MeshDrainPolicy` drain-body extraction (B25, the "prefer it if cheap" branch — user sign-off), `World : IMeshDrainHost`. B-number drift corrected (B22/B23 were FL sway → MP-2 = B24/B25, MP-3 prove-red = B26); `Validate Meshing` 25/25, `Validate All` 335/335. Doc-synced `CHUNK_LIFECYCLE_PIPELINE.md` §5.3 + meshing fidelity §4.
* **v1.2** - MP-1 implemented (2026-07-24, uncommitted): four `[Conditional]` probes + soak evidence (see §MP-1 Amended).
* **v1.1** - Pre-MP-1 drift update (2026-07-24): folded the P-4 backpressure interactions into MP-2/MP-4/MP-6 scope (drain time budgets, `ProcessMeshJobs` window + rotating cursor, §5.3 draw-tail rewrite), resolved §9 Q2 (drop the stagger, user sign-off) + annotated Q3, added the MP-1 out-of-range-discard rider. F1–F8 re-verified intact against HEAD; line anchors deferred to per-phase re-verification.
* **v1.0** - Initial design (orchestration census + F1–F8 findings + MP-1…MP-7 phased plan at `72ad121`)

---

**Last Updated:** 2026-07-25 (**MP-1…MP-6 all shipped and CLOSED** — MP-6 plus its §8.1 rider landed the same day, in-game confirmed incl. teleport-thrash pool churn; zero open items. MP-3's in-game repro was retired as structurally unreachable, see its Amended note) **Next Review:** when MP-7 starts (compass-name rename — the last phase), or when GS-5 Phase 1 is scheduled (re-check §5 contract — Phase 0.5 is closed by MP-5, and §4.3's single apply site is now also the load-animation trigger point)
