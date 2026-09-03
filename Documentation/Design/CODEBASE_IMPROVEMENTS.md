# Codebase Improvement Backlog

**Version:** 1.3  
**Date:** 2026-08-24  
**Status:** **Open backlog.** Items are removed (archived) when implemented and verified.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Open **non-performance** improvement items for the VoxelEngine codebase — API modernization and
> dependency hygiene. Each finding lists affected files, a recommendation, and an **Impact Analysis**
> using the same effort/risk/benefit symbols as the performance report. **The one structural item is
> §2.1: `World.cs` remains the project's god object and is still growing** — every other entry here is
> a localized cleanup. Completed items are archived to
> [`../Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md`](../Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md);
> performance findings live in the separate master backlog (see below).

**Last audited:** 2026-07-02 (added §2.1 `World.cs` decomposition; §1 items re-confirmed still open).  
**Facts re-verified:** 2026-07-26, at commit `3f579e44` (branch `feat/world-scaling`) — §1.2's five
`GameObject.Find` call sites all still present; §1.3's `Camera.main` list **corrected** (three more
files than recorded); §2.1's `World.cs` line count **corrected** 3,184 → 4,615; §2.2 confirmed resolved
by CP-4. No new findings were added — this was a fact check of the existing entries, not a fresh audit.

**Relationship to other documents:**

- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — the sibling master
  backlog owning every *performance* finding, with the completed/open split described in its header.
- [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) — deep dive on the
  generation → lighting → meshing pipeline.
- [`CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md`](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) — the CP-*
  arc; it resolved §2.2 and is the executed template §2.1's extractions should follow.
- [`../Guides/CODING_STYLE_GUIDE.md`](../Guides/CODING_STYLE_GUIDE.md) — the conventions these
  modernization items move the codebase toward.

> **Performance items moved (June 2026):** All `[Performance]` and performance-motivated `[Architecture]` findings formerly tracked here (legacy mesh API in `Clouds.cs`, `UnityEngine.Random` in behaviors, O (n) mesh queue, startup lookup/`.ToArray()` allocations, `BlockBehavior` data separation & function pointers) now live in
> **`PERFORMANCE_IMPROVEMENTS_REPORT.md`** — the single master performance backlog with at-a-glance effort/risk/benefit/seed-safety ratings. The string-allocation-on-pool-reset item (§4.1) was implemented and archived.
>
> **See also:** `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` (June 2026) — deep-dive analysis of the chunk generation → lighting → meshing pipeline.

---

## 1. Unity API Modernization

### 1.2 `GameObject.Find` Runtime Lookups  `[Improvement]`

`GameObject.Find(string)` performs an **O (n)** scan of all active objects using string comparison every time it is called.

| Affected Files          | Example Call                                     |
|-------------------------|--------------------------------------------------|
| `UIItemSlot.cs`         | `GameObject.Find("World").GetComponent<World>()` |
| `CreativeInventory.cs`  | `GameObject.Find("World").GetComponent<World>()` |
| `DragAndDropHandler.cs` | `GameObject.Find("CreativeInventory")`           |
| `PlayerInteraction.cs`  | `GameObject.Find("HighlightBlocks")`             |

**Recommendation:** Replace all `GameObject.Find` calls with Inspector-serialized fields (`[SerializeField]`) or use the existing `World.Instance` singleton directly.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — straightforward field wiring or singleton substitution.
> - **Risk:** 🟢 Low — change is isolated per-class, easy to test.
> - **Benefit:** 🟢 High — eliminates a hidden O (n) scan on every `Awake/Start` call and makes dependencies explicit.

---

### 1.3 `Camera.main` Usage  `[Improvement]`

`Camera.main` is cached internally since Unity 2020.1 and is no longer a performance problem. However, relying on it introduces an implicit dependency on `MainCamera` tags.

| Affected Files                                                                                                                                              |
|---------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `World.cs`, `Player.cs`, `PlayerInteraction.cs`, `DebugScreen.cs`, `UI/GraphicsSettingsController.cs`, `Benchmarks/BenchmarkController.cs`, `Jobs/BurstData/BurstVoxelMetadataUtility.cs` |

*(List re-verified 2026-07-26: the last three were missing from the original audit.)*

**Recommendation:** Inject or serialize the camera reference for explicit control.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — add a `[SerializeField]` field and assign in Inspector.
> - **Risk:** 🟢 Low — purely architectural cleanup.
> - **Benefit:** 🟡 Medium — cleaner dependency graph; avoids accidental tag mismatches.

---

## 2. Architecture

### 2.1 `World.cs` God-Object Decomposition  `[Refactor]`

*(Added 2026-07-02; line count re-verified 2026-07-26.)* `World.cs` is **4,615 lines** — the largest handwritten file in the project, and **up ~45 % from the 3,184 lines recorded at the original audit**, so this item is getting worse, not better, despite the extractions listed below — and remains the orchestration hub for chunk streaming/activation/unload, the voxel-modification queue (`EnqueueVoxelModification` + application), the tick pump (`ProcessTickUpdates`), lighting scheduling glue, global job-data preparation, debug tooling, and settings plumbing. Recent extractions prove the pattern works and are the template: `WorldJobManager` (job scheduling/completion), `LightWorkScheduler` (MT-2 ready/waiting sets), `MeshBuildQueue`
(MT-1),
`PlacementController` (placement decisions).

| Candidate extraction       | Contents                                                                     |
|----------------------------|------------------------------------------------------------------------------|
| Chunk streaming manager    | view-distance load/activation loops, `UnloadChunks`, spawn handling, borders |
| Voxel-modification pump    | `EnqueueVoxelModification` + queued-mod application/batching                 |
| Debug & visualization glue | debug-screen wiring, chunk-border visualizers, diagnostic toggles            |

**Recommendation:** Move-only refactors (per the `refactor-safely` skill), one extraction per PR, each leaving `World` a thinner facade. Do **not** bundle behavior changes: the chunk-lifecycle invariants (`chunk-lifecycle` skill) make this a high-blast-radius area — the win is testability/maintainability and lower merge friction, not performance.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium per extraction — mechanical but wide (many call sites go through `World.Instance`).
> - **Risk:** 🟡 Medium — pipeline-adjacent state moves; move-only discipline plus the editor
>   validation suites keep it safe.
> - **Benefit:** 🟡 Medium — maintainability and testability; unblocks parallel work on streaming
>   vs tick vs debug code without touching one 3k-line file.

---

### 2.2 Dead legacy load path in `WorldData.LoadChunk`  `[Cleanup]` ✅ RESOLVED

*(Added 2026-07-02, fourth-pass audit.)* `WorldData.LoadChunk` (`WorldData.cs` ~lines 89–114)
carries a commented-out `SaveSystem.LoadChunk` block plus two stacked TODOs ("PHASE 3 TODO-old" /
"TODO-new") from the storage-manager migration — the method's actual behavior today is only
"create a pooled placeholder", while its name and dead code imply disk loading (which really lives in `World.LoadOrGenerateChunk` → `ChunkStorageManager.LoadChunkAsync`).

**Recommendation:** Delete the dead block, resolve the TODOs into one sentence documenting where loading actually happens, and rename to reflect the placeholder-creation behavior (e.g.
`EnsurePlaceholder`) — or fold it into the existing `EnsureChunkExists`, which duplicates the same placeholder logic. Pure cleanup; no behavior change.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — behavior-preserving rename/merge (use the `refactor-safely` skill).
> - **Benefit:** 🟡 Medium — removes a misleading seam in the load path that the SL-2
>   (`PERFORMANCE_IMPROVEMENTS_REPORT.md`) work will otherwise trip over.

> **✅ RESOLVED (2026-07-22) by CP-4:** shipped as `WorldData.GetOrCreatePlaceholder(Vector2Int)` —
> the recommendation's "fold into `EnsureChunkExists`" option taken further: `LoadChunk` deleted
> (dead block + both TODOs), BOTH `EnsureChunkExists` overloads retired (zero/discarded callers),
> and all three placeholder-creation sites consolidated onto the new method. See
> `CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md` §7 CP-4 Amended block.

### 2.3 LP-6 gate-walk probes retained past their question  `[Cleanup]`

*(Added 2026-08-24, on shipping LP-6.)* Four instance counters and their public read properties sit on
`World` — `_gateCallsDataReady` / `_gateCallsReadyAndLit` / `_gateCallsMeshReady` /
`_neighborFactsGathered`, exposed as `GateCallsDataReady`, `GateCallsReadyAndLit`, `GateCallsMeshReady`,
`NeighborFactsGathered` (`World.cs`, declared just above the transient-flags block; incremented at the top
of the three `AreNeighbors*` gates and in `GatherNeighborFacts`). They were added to size LP-6 and then to
prove it landed, and **that question is closed** — the counter diff is recorded in
`LIGHTING_PIPELINE_STATE_REFACTOR.md`'s LP-6 Amended line (−34.7 % scan gate calls).

They are **deliberately kept for now** (user decision, 2026-08-24): they are the only instrument that can
re-verify the laziness in production, and re-deriving them costs a session. The increments are
`#if DEVELOPMENT_BUILD || UNITY_EDITOR`, so a release build carries none of it, and the fields are
*instance* fields — no domain-reload reset obligation (the LP-1 probe convention).

**Recommendation:** delete them once one of these is true — (a) the follow-up `LightSchedule` phase lands
and re-measures the pass anyway, (b) LP-8's production-scheduler rig can witness gate counts directly, or
(c) a year passes with nobody reading them. LP-3 is the precedent: it deleted LP-1's probes once they had
answered their question. Note the two properties consumed by
`Editor/Benchmarking/LightingGateWalkBenchmark.cs` (`NeighborFactsGathered`, `GateCalls*`) — that benchmark
must be retired or reworked in the same pass, or it will not compile.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — delete 4 fields, 4 properties, 4 increment sites, and adjust one editor benchmark.
> - **Risk:** 🟢 Low — dev/editor-only, read by nothing in production.
> - **Benefit:** 🟢 Low — but it is 8 members off the §2.1 god object, and removes instrumentation whose
>   presence implies an open question that is in fact answered.

---

## Document History

*Entries before v1.0 are reconstructed from git history — this document predates the project's
Document History convention, so they record what the commits changed, not contemporaneous notes.*

* **v1.3** - Added §2.3 (2026-08-24): LP-6's four gate-walk counters on `World`, kept by explicit decision
  as the re-verification instrument for that phase but filed for deletion with named retirement triggers.
  Filed *on shipping* rather than discovered later, so the retention is deliberate on the record and at the
  declaration site. Feeds §2.1 — it is 8 more members on the god object. **No existing entry was
  re-audited**; §§1.2/1.3/2.1 still carry their 2026-07-26 verification date.
* **v1.2** - §1.4 implemented and **archived** to
  [`../Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md`](../Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md)
  (2026-08-14): all ten project-owned shaders now declare `#pragma target 3.5`. Verified by reimport
  (10/10 supported, zero shader messages) and `Validate All` 477/477. Opened and closed the same day —
  the rule survives in [`../Guides/SHADER_CONVENTIONS.md`](../Guides/SHADER_CONVENTIONS.md) §1.
* **v1.1** - Added §1.4 (shader `#pragma target` sweep, 2026-08-14): the ten project-owned shaders still
  below the 3.5 floor, to be raised in a standalone commit. Prompted by RF-3's liquid emissive read
  pushing `LiquidV2F` to 11 interpolators against a declared `target 3.0`. The **rule** itself (why 3.5,
  why not 4.5/5.0, the Unity 6.6 item) was authored in the new
  [`../Guides/SHADER_CONVENTIONS.md`](../Guides/SHADER_CONVENTIONS.md) rather than here, so it outlives
  this entry's archival. **No existing entry was re-audited** — §§1.2/1.3/2.1 facts still carry their
  2026-07-26 verification date.
* **v1.0** - Mandatory header added (2026-07-26): `Version`/`Date`/`Status`/`Target`, summary
  blockquote, and the relationship list; the existing archive and performance-backlog pointers were
  folded into it. **Two stale facts corrected in the body** while re-verifying the entries: §2.1's
  `World.cs` line count **3,184 → 4,615** (the item has grown ~45 % since it was written, despite the
  extractions it lists), and §1.3's `Camera.main` file list gained three files the original audit
  missed (`UI/GraphicsSettingsController.cs`, `Benchmarks/BenchmarkController.cs`,
  `Jobs/BurstData/BurstVoxelMetadataUtility.cs`). §1.2's five `GameObject.Find` sites re-confirmed
  present. **This was a fact check of existing entries, not a fresh audit** — no new findings added.
  First versioned edition.
* *(2026-07-22, `830ecf13`)* - §2.2 (dead legacy load path in `WorldData.LoadChunk`) marked
  ✅ RESOLVED by CP-4.
* *(2026-07-02, `7f75338a`)* - Fourth-pass audit added §2.1 (`World.cs` god-object decomposition) and
  §2.2; §1 items re-confirmed still open.
* *(2026-06-12, `c4d58cb9`)* - **Scope split:** every `[Performance]` and performance-motivated
  `[Architecture]` finding moved out to the new `PERFORMANCE_IMPROVEMENTS_REPORT.md`, leaving this
  document as the *non-performance* backlog. §4.1 (string allocation on pool reset) was implemented and
  archived in the same pass.
* *(2026-04-16, `cb787249`)* - Moved into the restructured `Documentation/` tree; completed items
  split out to `Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md`.

---

**Last Updated:** 2026-07-26 (header added; §2.1 line count and §1.3 file list corrected)  
**Next Review:** when §2.1 is scheduled — re-measure `World.cs` first, since the figure has drifted
badly twice now. A genuine re-audit for *new* non-performance findings is also overdue; the last one
was 2026-07-02.
