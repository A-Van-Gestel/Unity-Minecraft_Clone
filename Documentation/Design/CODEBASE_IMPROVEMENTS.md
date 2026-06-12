# Codebase Improvement Backlog

> Open **non-performance** improvement items for the VoxelEngine codebase (API modernization, dependency hygiene). Each finding includes affected files, a recommendation, and a brief **Impact Analysis**.
> Completed items have been archived to `Documentation/Archived/CODEBASE_IMPROVEMENTS_COMPLETED.md`.

**Last audited:** June 2026

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
