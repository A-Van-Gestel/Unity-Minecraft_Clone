# Codebase Improvement Backlog

> Open **non-performance** improvement items for the VoxelEngine codebase (API modernization, dependency hygiene). Each finding includes affected files, a recommendation, and a brief **Impact Analysis**.
> Completed items have been archived to `Documentation/Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md`.

**Last audited:** 2026-07-02 (added §2.1 `World.cs` decomposition; §1 items re-confirmed still open)

> **Performance items moved (June 2026):** All `[Performance]` and performance-motivated `[Architecture]` findings formerly tracked here (legacy mesh API in `Clouds.cs`, `UnityEngine.Random` in behaviors, O(n) mesh queue, startup lookup/`.ToArray()` allocations, `BlockBehavior` data separation & function pointers) now live in
> **`PERFORMANCE_IMPROVEMENTS_REPORT.md`** — the single master performance backlog with at-a-glance effort/risk/benefit/seed-safety ratings. The string-allocation-on-pool-reset item (§4.1) was implemented and archived.
>
> **See also:** `CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md` (June 2026) — deep-dive analysis of the chunk generation → lighting → meshing pipeline.

---

## 1. Unity API Modernization

### 1.2 `GameObject.Find` Runtime Lookups  `[Improvement]`

`GameObject.Find(string)` performs an **O(n)** scan of all active objects using string comparison every time it is called.

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
> - **Benefit:** 🟢 High — eliminates a hidden O(n) scan on every `Awake/Start` call and makes dependencies explicit.

---

### 1.3 `Camera.main` Usage  `[Improvement]`

`Camera.main` is cached internally since Unity 2020.1 and is no longer a performance problem. However, relying on it introduces an implicit dependency on `MainCamera` tags.

| Affected Files                                                    |
|-------------------------------------------------------------------|
| `World.cs`, `Player.cs`, `PlayerInteraction.cs`, `DebugScreen.cs` |

**Recommendation:** Inject or serialize the camera reference for explicit control.

> **Impact Analysis:**
> - **Effort:** 🟢 Low — add a `[SerializeField]` field and assign in Inspector.
> - **Risk:** 🟢 Low — purely architectural cleanup.
> - **Benefit:** 🟡 Medium — cleaner dependency graph; avoids accidental tag mismatches.

---

## 2. Architecture

### 2.1 `World.cs` God-Object Decomposition  `[Refactor]`

*(Added 2026-07-02.)* `World.cs` is **3,184 lines** — the largest handwritten file in the project —
and remains the orchestration hub for chunk streaming/activation/unload, the voxel-modification
queue (`EnqueueVoxelModification` + application), the tick pump (`ProcessTickUpdates`), lighting
scheduling glue, global job-data preparation, debug tooling, and settings plumbing. Recent
extractions prove the pattern works and are the template: `WorldJobManager` (job
scheduling/completion), `LightWorkScheduler` (MT-2 ready/waiting sets), `MeshBuildQueue` (MT-1),
`PlacementController` (placement decisions).

| Candidate extraction       | Contents                                                                     |
|----------------------------|------------------------------------------------------------------------------|
| Chunk streaming manager    | view-distance load/activation loops, `UnloadChunks`, spawn handling, borders |
| Voxel-modification pump    | `EnqueueVoxelModification` + queued-mod application/batching                 |
| Debug & visualization glue | debug-screen wiring, chunk-border visualizers, diagnostic toggles            |

**Recommendation:** Move-only refactors (per the `refactor-safely` skill), one extraction per PR,
each leaving `World` a thinner facade. Do **not** bundle behavior changes: the chunk-lifecycle
invariants (`chunk-lifecycle` skill) make this a high-blast-radius area — the win is
testability/maintainability and lower merge friction, not performance.

> **Impact Analysis:**
> - **Effort:** 🟡 Medium per extraction — mechanical but wide (many call sites go through `World.Instance`).
> - **Risk:** 🟡 Medium — pipeline-adjacent state moves; move-only discipline plus the editor
    > validation suites keep it safe.
> - **Benefit:** 🟡 Medium — maintainability and testability; unblocks parallel work on streaming
    > vs tick vs debug code without touching one 3k-line file.

---

### 2.2 Dead legacy load path in `WorldData.LoadChunk`  `[Cleanup]`

*(Added 2026-07-02, fourth-pass audit.)* `WorldData.LoadChunk` (`WorldData.cs` ~lines 89–114)
carries a commented-out `SaveSystem.LoadChunk` block plus two stacked TODOs ("PHASE 3 TODO-old" /
"TODO-new") from the storage-manager migration — the method's actual behavior today is only
"create a pooled placeholder", while its name and dead code imply disk loading (which really lives
in `World.LoadOrGenerateChunk` → `ChunkStorageManager.LoadChunkAsync`).

**Recommendation:** Delete the dead block, resolve the TODOs into one sentence documenting where
loading actually happens, and rename to reflect the placeholder-creation behavior (e.g.
`EnsurePlaceholder`) — or fold it into the existing `EnsureChunkExists`, which duplicates the same
placeholder logic. Pure cleanup; no behavior change.

> **Impact Analysis:**
> - **Effort:** 🟢 Low.
> - **Risk:** 🟢 Low — behavior-preserving rename/merge (use the `refactor-safely` skill).
> - **Benefit:** 🟡 Medium — removes a misleading seam in the load path that the SL-2
    > (`PERFORMANCE_IMPROVEMENTS_REPORT.md`) work will otherwise trip over.
