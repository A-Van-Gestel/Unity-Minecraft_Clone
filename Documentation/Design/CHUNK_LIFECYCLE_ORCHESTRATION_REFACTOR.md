# Chunk Lifecycle Orchestration Refactor (CP-*)

**Version:** 1.0
**Date:** 2026-07-06
**Status:** Proposed design — not implemented.
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

> Clean-up / refactor plan for the **outer chunk lifecycle** — placeholder creation and the
> view-distance spiral, the async load-or-generate arm, generation completion + structure-mod
> application, activation/visual linking, unload/save, and the pool recycle path — completing the
> trilogy with [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md) (LP-*,
> the lighting inner loop) and
> [`MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md)
> (MP-*, the meshing inner loop). The two most important findings: **the async load arm has no
> failure contract — an I/O exception faults a discarded fire-and-forget `Awaitable`, leaves
> `IsLoading` stuck `true` forever, and the permanently-unpopulated placeholder then parks every
> neighbor's lighting (a silent stall class) — and a chunk's `ModifiedChunks` membership is
> removed when its unload save is *fired*, not when it succeeds, so a failed save silently loses
> the session's edits.** Structurally, the pivotal decision is to make this layer testable via
> **pure-decision extraction + targeted mini-suites** (an unload-policy truth table, the NS-5
> coordinate-math equivalence suite, an NS-1 deserialize-robustness seed) rather than building the
> full NS-3 lifecycle harness now. The plan also **executes WS-1** (shift/mask coordinate math —
> Tier B's prerequisite, zero-risk today) and keeps the palette-mapping and world-scaling seams
> clean per the two future designs. Performance items stay owned by the existing P-*/OM-*/SL-*/
> SU-* backlog — this plan prepares their seams, it does not re-propose them.

**Audited:** 2026-07-06, at commit `0a12036` (branch `feat/async-lighting-validation-suite`).
Findings are from static review of `World.cs` (`CheckViewDistance` L2507–2650, `LoadOrGenerateChunk`
L763–941, `UnloadChunks` L2330–2466, `ApplyModifications` L2065+, startup coroutine phases
L959–1123, `CollectInitialChunks` L736–755), `WorldJobManager.cs` (`ProcessGenerationJobs`
L657–870), `ChunkPoolManager.cs` (full), `Data/WorldData.cs` (`RequestChunk`/`LoadChunk`/
`EnsureChunkExists`/`GetChunkCoordFor` L68–165), `Serialization/ChunkStorageManager.cs`
(`LoadChunkAsync` L50–83, `SaveChunk(Async)` L90–170), `Serialization/ChunkSerializer.cs`
(`Deserialize` L74–104, `ReadChunkInternal`), `Data/ChunkData.cs` (`Reset` L242–288), and
`Chunk.cs` (`Reset`/`Release`). The LP/MP docs' surveys of the same files are presumed current
(same day). Line numbers are anchors for the executor, not contracts — re-verify before editing.

**Relationship to other documents:**

- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) —
  the authoritative pipeline reference; §2 (data-axis flags), §5.1 (generation path), §9.5/§9.6
  (orphaning/stranding risks) are restructured or made observable here; every phase doc-syncs it.
- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — the Tier A/B/C future this plan keeps
  open: CP-2 **executes WS-1** (§3.2's shift/mask fix, called out there as the only zero-risk
  early-shippable slice); CP-7 executes the §5-checklist constants unification; §5 here lists the
  scaling seams every phase must not close (identity types, per-column data, `IsChunkInWorld`).
- [`CHUNK_PALETTE_MAPPING.md`](CHUNK_PALETTE_MAPPING.md) — the future palette boundary: hydration
  (`ChunkSerializer.ReadChunkInternal` → `PopulateFromSave`) and dehydration
  (`WriteChunkInternal`) are each single-site today, and `ModManager`'s pending mods are the third
  seam that doc names. §5 pins "keep these single-site" as a constraint; no CP phase touches the
  palette itself.
- [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) — the
  performance deep-dive for this same layer (its §3 backpressure = P-4, §4.4 stable-bit = P-5).
  This plan is the *clarity/testability* complement: CP-1's deferral counters give §3.3's "pinned
  trail" its missing instrumentation, and CP-5's decision extraction is the seam P-4's rec 3
  ("unload light-pending chunks via persistence") will land on.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — P-1..P-6, OM-1..3,
  SL-1..4, SU-1..2, WS-1 keep their IDs. CP-2 executes WS-1; everything else is interlock only
  (named per phase).
- [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md) /
  [`MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md) —
  the sibling plans for the inner loops. Boundary: LP owns the lighting work flags/gates/scan;
  MP owns mesh request→apply; CP owns identity, creation/load, population, unload/save, pools.
  Coordination points are named in §7 (LP-4's data-axis note, MP-6's draw-queue clear).
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — CP-2 builds the
  **NS-5** equivalence suite alongside WS-1; CP-3 seeds **NS-1** (deserialize robustness); CP-1's
  probes + CP-5's decision are **NS-3** groundwork (the full lifecycle harness stays future).
- [`../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
  — the storage architecture CP-3/CP-6 harden at the orchestration boundary (region internals
  untouched; `REGION_FILE_CONCURRENCY.md`/SL-4 owns the locking design).

---

## 1. Goals & non-goals

### Goals

1. **Give the load and save arms a failure contract** — no fault may strand `IsLoading`, leak a
   pooled shell, or silently drop a chunk's modified status (§2.4 F1/F7).
2. **Make the lifecycle's policies pure and testable** — unload deferral (F6) and placeholder
   creation (F2) become single-site, decision-extracted, truth-table-baselined code, the LP/MP
   pattern applied to the outer loop.
3. **Execute WS-1** — centralized shift/mask coordinate math with an equivalence suite (NS-5),
   removing the float-roundtrip/truncation minefield before Tier B can ever trip it (§2.4 F3).
4. **Keep the scaling and palette seams open** — single-site hydration/dehydration, per-column
   data isolated, constants unified, no new hardcoded 16/128 (§5).
5. **Preserve behavior at every phase boundary except the named fixes (CP-3, CP-6)** — both fixes
   are failure-path-only changes with prove-red evidence and in-game confirmation.
6. *(SECONDARY)* Make the P-4/OM-3 cost surfaces observable (deferral counters, pool churn, save
   concurrency) so the performance items land on measured ground.

### Non-goals (v1)

- **Backpressure, budgets, caps, panic gates** — owned by **P-4** / **SU-2** (perf report). CP-1
  provides their instrumentation; CP-5 provides P-4-rec-3's seam. No scheduling-policy change here.
- **The "lighting stable" save bit** (**P-5**), **save-task capping** (**OM-3**), **load/save
  alloc reduction** (**SL-1/SL-3**), **region locking** (**SL-4**) — owned by the perf backlog.
  CP-6 fixes only the *correctness* hole in the save-fire path, not its throughput.
- **Tier A/B/C themselves** — heights, `WorldMinY`, unbounded XZ, floating origin, cubic chunks
  stay in `WORLD_SCALING_ANALYSIS.md`. CP ships the two slices that doc marks as safe-early
  (WS-1, constants unification) and nothing else.
- **Palette mapping implementation** — its own doc; CP only protects the seams.
- **The full NS-3 chunk-lifecycle harness** — deliberately deferred (see §3.1); this plan ships
  its groundwork instead.
- **Startup-coroutine restructuring** — the gen/light/mesh force-complete phases stay; LP-5
  unifies the lighting arm decision, SU-1/SU-2 own the perf shape. A deeper "startup = Update with
  elevated budgets" unification is a v2 idea only (§8).
- **`MeshBuildQueue`, lighting scheduler, job internals** — owned by MP-*/LP-*/existing systems.

---

## 2. Current state — the outer lifecycle surface

### 2.1 Stage map

| # | Stage                     | Code                                                                                                                                                                                                                                                                                                                   | Coverage today                                         |
|---|---------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------|
| 1 | **Placeholder + spiral**  | `CheckViewDistance` (W:2507): on chunk-boundary crossing, spiral over `LoadDistance` square; missing → pooled placeholder into `worldData.Chunks`; `!IsPopulated && !IsLoading && !GenerationJobs` → `IsLoading = true` + fire-and-forget `LoadOrGenerateChunk`                                                        | ❌ none                                                 |
| 2 | **Async load arm**        | `LoadOrGenerateChunk` (W:763): `await StorageManager.LoadChunkAsync` (background `Task.Run` → region read → `ChunkSerializer.Deserialize` into a **pooled shell**) → mid-await unload guard → `PopulateFromSave` (hydration copy) → shell returned → pending mods + lighting restore → edge-check/initial-lighting arm | ❌ none                                                 |
| 3 | **Generate arm**          | not on disk / persistence off → `JobManager.ScheduleGeneration` (W:940)                                                                                                                                                                                                                                                | terrain content via worldgen tooling; orchestration ❌  |
| 4 | **Generation completion** | `ProcessGenerationJobs` (WJM:657): HF-2 two-stage fault isolation; `Populate`; structure expansion under `maxStructureModsPerFrame` budget (un-released budget-retry `continue`s); pending-mod + pending-lighting recovery; `NeedsInitialLighting = true`                                                              | ❌ orchestration none                                   |
| 5 | **Mod application**       | `ApplyModifications` (W:2065): drains `_modifications`; unpopulated target → `ModManager.AddPendingMod` (persisted); placement rules; `ModifyVoxel`                                                                                                                                                                    | placement *rules* ✅ (placement suite); routing ❌       |
| 6 | **Activation / visual**   | `CheckViewDistance` view-set diff: pool `Chunk` get/return, `_chunkMap`, borders, re-request mesh when populated                                                                                                                                                                                                       | ❌ none                                                 |
| 7 | **Unload**                | `UnloadChunks` (W:2330): distance test → job-pin → light-flag-pin → 8-neighbor strand-pin → persist orphaned sunlight columns → fire-and-forget save if modified → mesh-queue remove → pool visual + data, remove from `Chunks`                                                                                        | ❌ none                                                 |
| 8 | **Pool recycle**          | `ChunkPoolManager` (5 pools; data/section pools concurrent for background deserialization); `ChunkData.Reset` / `Chunk.Reset`/`Release`                                                                                                                                                                                | `Reset` transient-state ✅ (lighting B33/B34); sizing ❌ |

### 2.2 Identity & coordinate math (the WS-1 surface)

Three key spaces coexist: `ChunkCoord` (chunk grid — `_chunkMap`, all three job dictionaries,
`ModManager`), `Vector2Int` voxel-origin (`worldData.Chunks`, `LightWorkScheduler`,
`SunlightRecalculationQueue`, serialization), and float world positions (transforms, player).
Conversions are scattered and idiom-mixed: `Mathf.FloorToInt(worldPos.x / 16f) * 16`
(`WorldData.GetChunkCoordFor` W:145–150 — float roundtrip, breaks beyond ±2²⁴),
`ChunkCoord.FromVoxelOrigin`/`ToVoxelOrigin`, and the **already-wrong-for-negatives** truncating
division in `RegionAddressCodec.V2Codec` step 1 (scaling doc §3.2). All-positive coordinates make
every variant agree today — which is exactly why no test can currently red a drift.

### 2.3 What no suite can currently red

- A load-arm fault (stuck `IsLoading`, leaked shell) — §2.4 F1.
- A wrong unload deferral (either direction: premature unload = the §9.6 stranding deadlock
  history; over-deferral = the P-4 pinned-trail memory climb).
- A silently-lost save (F7) or a placeholder-creation drift across its three sites (F2).
- Any coordinate-math change (no equivalence suite; NS-5 is the roadmap's named answer).
- Pool sizing/churn behavior (F4).

### 2.4 Findings

| #   | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |       Addressed by       |
|-----|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------------------------:|
| F1  | **The async load arm has no failure contract.** `Deserialize` catches → `null` → "(re-)generated" (good), but exceptions *outside* it — `GetRegion`/`RegionFile.LoadChunkData` I/O faults — propagate out of the `Task.Run`, fault the awaited call, and `_ = LoadOrGenerateChunk(...)` (W:2551) discards the `Awaitable`. Nothing clears `IsLoading` on any fault, and the `!IsLoading` gate (W:2547) then blocks every retry: a permanently-unpopulated placeholder sits in range, and every neighbor's lighting parks on `AreNeighborsDataReady` forever (rescued by nothing — the fail-safe re-promotes but the gate keeps failing). Sub-finding: a mid-parse throw inside `ReadChunkInternal` leaks the pooled shell (+ any sections already attached) — `Deserialize`'s catch returns null without returning the shell (bounded, managed-only, but it bypasses the pool contract). The mid-await unload guard (W:781–790) is correct — keep and pin it. |           CP-3           |
| F2  | **Placeholder creation is triplicated and one site wears a misleading name.** `CheckViewDistance` (W:2542), `WorldData.LoadChunk` (W:113), and `WorldData.EnsureChunkExists` (W:132) all do `Chunks.Add(pool.GetChunkData(pos))`. `WorldData.LoadChunk` contains only a commented-out legacy save-system block + two contradictory TODOs — it loads nothing. `RequestChunk(pos, allowChunkDataCreation: true)` can therefore silently *resurrect* a placeholder for an unloaded chunk (benign today only because job-pinning prevents the race — convention, not structure).                                                                                                                                                                                                                                                                                                                                                                                  |           CP-4           |
| F3  | **Coordinate math is idiom-mixed and negative-hostile** (§2.2) — the WS-1 surface, with the float-precision hazard and the live (currently-unreachable) `RegionAddressCodec` truncation bug. No equivalence coverage exists.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |           CP-2           |
| F4  | **Pool sizing comment and code disagree.** `ChunkPoolManager.Update` (CPM:103–116): the comment derives "Area = (Dist·2+1)²" but the code computes `chunksNeeded = dist·2+1` — the *width*. Spare-pool targets are therefore ~25× smaller than the comment intends (e.g. ~31 vs ~781 at view distance 12). Plausibly the width was the deliberate "one edge strafe" spare size — but then the comment lies; if area was intended, every teleport/view-distance change churns hundreds of GameObject destroy/creates. Needs churn measurement, then either the comment or the formula fixed.                                                                                                                                                                                                                                                                                                                                                                   |       CP-1 → CP-7        |
| F5  | **Save-on-unload drops the modified flag before the save succeeds.** `UnloadChunks` (W:2422–2433) fires `SaveChunkAsync` and immediately `ModifiedChunks.Remove(data)`. `SaveChunkAsync` catches internally (CSM:164 — so the `ContinueWith` fault log is nearly unreachable) and simply logs; the chunk is then unloaded, unmodified-flagged, and its session edits exist nowhere. A failed save must re-mark (or the remove must move into a success continuation). Distinct from OM-3 (task flood = throughput), this is a durability hole.                                                                                                                                                                                                                                                                                                                                                                                                                |           CP-6           |
| F6  | **Unload policy is a monolithic inline block.** Distance → job-pin → light-pin → 8-neighbor strand-scan → persist → save → pool teardown, all inline in `UnloadChunks`. The deferral rules are the pipeline's deadlock-vs-memory-climb balance point (pipeline doc §9.6 fixed a deadlock here; perf analysis §3.3 blames the same rules for the pinned-trail climb) — and they are unobservable and untestable. P-4's rec 3 will edit exactly these rules.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |        CP-1, CP-5        |
| F7  | **Startup coroutine is a second, hand-rolled pipeline** (gen loop / lighting sweeps / mesh force-complete with own budgets + safety breaks). LP-5 unifies its lighting arm decision; SU-1/SU-2 own its perf. The structural duplication itself is accepted for v1 (§8 v2 idea).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |         §8 (v2)          |
| F8  | **Duplicate world-dimension constants**: `VoxelData.ChunkHeight` vs `ChunkMath.CHUNK_HEIGHT` (+ width twins) — the scaling doc's §5 checklist names unification as a prerequisite for ever touching the value; a mismatch compiles fine and corrupts indexing silently.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |           CP-7           |
| F9  | **Hydration double-copy is the palette seam.** Disk → pooled shell (`ReadChunkInternal`, background thread) → `PopulateFromSave` copy into the live placeholder → shell returned. This is where palette hydration (LocalID→GlobalID remap) will run; SL-1 may later want the shell eliminated. Not a defect — a seam to keep single-site and to document (constraint, §5).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |     §5 (constraint)      |
| F10 | **Generation fault path can double-release a job** (`WorldJobManager.cs:~849`, in the HF-2-style stage-2 fault isolation added on `feat/async-lighting-validation-suite`): the catch releases only when the happy path has not (`released` flag), but if `ReleaseGenerationJobData` *itself* throws mid-release — after `_activeVoxelListPool.Return`, during `Dispose` — `released` is still false and the catch calls it again, returning the same `ActiveVoxels` list to the pool twice. Two future generation jobs then share one live list (silent cross-job active-voxel corruption). Error-path-only (PLAUSIBLE, 2026-07-10 branch review); fix is a one-liner — make the release idempotent (set a flag between the pool `Return` and `Dispose`, or `null`/default the pooled ref before disposing).                                                                                                                                                  | — (standalone hardening) |

---

## 3. Decisions

### 3.1 Decision: how to make the outer lifecycle testable

#### Option A — build the NS-3 chunk-lifecycle harness now (rejected for v1)

- ✅ The roadmap's endgame: adversarial event orders, convergence + flag-pairing assertions.
- ❌ **Premature and 🔴-sized.** The coverage roadmap itself sequences NS-3 late ("the hardest
  harness on this list") and its embryo (`LightingFrameSimulator`) plus the LP/MP extractions are
  still landing. Stubbing `World`-level orchestration before the decisions are extracted would
  test a shape the LP/MP/CP refactors are about to change.

#### Option B — pure-decision extraction + probes + targeted mini-suites ✅ **CHOSEN**

The same pattern that worked five times (A2/B2/HF-4/LP/MP): extract the *policies* (unload
deferral, placeholder creation, coordinate math) into pure code with truth-table baselines, make
the *failure paths* observable with `[Conditional]` probes, and seed the two cheap named suites —
**NS-5** (coordinate equivalence — the roadmap's "best value-per-effort") and **NS-1**
(deserialize robustness). When NS-3 is eventually built, it drives these extracted decisions
instead of re-deriving them.

#### Option C — leave it to in-game verification (rejected)

- ❌ This layer's failure modes are *silent by construction* (stuck `IsLoading`, lost saves,
  pinned trails) — in-game play is precisely what doesn't surface them.

### 3.2 Decision: WS-1 timing — execute now ✅

`WORLD_SCALING_ANALYSIS.md` §6 already answers this: the shift/mask cleanup "can ship early and
independently, and is the only part of this document with zero save/seed risk when done
correctly" — plus it is a micro-perf win (drops float roundtrips from every chunk lookup). In the
all-positive world, `x >> 4` / `x & 15` are provably identical to every current idiom, so CP-2 is
behavior-preserving *and* can carry an equivalence suite that also pins the negative domain the
future Tier B needs. Waiting buys nothing; every month adds new call sites to audit.
(Rejected alternative — defer to Tier B: the audit grows, and Tier B then starts on an unproven
minefield.)

### 3.3 Decision: load-arm failure policy (F1)

#### Option A — quarantine faulted coords (retry budget + blacklist) (rejected)

- ❌ New state and policy for a case whose *transient* form (file lock, AV scan) is best served by
  simple retry and whose *permanent* form (corrupt file) is already handled by
  `Deserialize → null → regenerate`. Over-engineering.

#### Option B — clear-and-retry, aligned with the existing corrupt-file intent ✅ **CHOSEN**

Wrap the `LoadOrGenerateChunk` body: any fault → one `Debug.LogError` (errors are the regression
signal) → `IsLoading = false` → return. The placeholder stays; the next `CheckViewDistance`
boundary crossing retries naturally (the same convergence shape as every other retry in this
engine). A *persistently* faulting file shows up as a repeating error log (loud), and the
`Deserialize` null-path already provides the regenerate escape hatch. Plus: return the pooled
shell on the `ReadChunkInternal` partial-parse path so the pool contract holds.

---

## 4. Target architecture (the extraction shapes)

### 4.1 `ChunkUnloadDecision` (CP-5)

```csharp
/// <summary>Pure per-chunk unload decision, mirroring World.UnloadChunks' deferral rules so the
/// policy is truth-table-testable and its deferral reasons observable (the outer-lifecycle
/// sibling of LightingScanDecision / MeshingScheduleDecision).</summary>
public static class ChunkUnloadDecision
{
    public enum Result : byte
    {
        Unload,            // out of range, unpinned — proceed to persist/save/pool teardown
        KeepInRange,       // within unload distance
        DeferJobRunning,   // a generation/mesh/lighting job still owns buffers for this chunk
        DeferLightPending, // IsAwaitingMainThreadProcess / HasLightChangesToProcess (LP-3 may shrink this)
        DeferWouldStrand,  // a populated neighbor still needs this chunk's data (pipeline §9.6)
    }

    public static Result Evaluate(in ChunkUnloadFacts facts); // plain bools + distance, no refs
}
```

`UnloadChunks` becomes: gather facts → `Evaluate` → switch (the persist/save/teardown body
unchanged). CP-1's counters tally the deferral reasons per pass — the §3.3-perf-analysis "pinned
trail" becomes a number on the debug screen instead of a hypothesis. P-4's rec 3 later lands as a
new arm in this one function, baselined.

### 4.2 `ChunkMath` shift/mask + NS-5 equivalence suite (CP-2)

Per the scaling doc §3.2: `VoxelToChunk(int) => x >> 4`, `VoxelToLocal(int) => x & 15`,
`ChunkToRegion(int) => x >> 5`, etc., as the **only** sanctioned chunk math; every
`FloorToInt(x / 16f)`, `/ ChunkWidth`, `% 32` call site migrates (grep checklist in the scaling
doc §5). The NS-5 suite (`Minecraft Clone/Dev/Validate Chunk Math`, new, own numbering B1+)
asserts: (a) equivalence with the old idioms across the positive domain, (b) hand-derived
correctness on the negative domain and at ±2²⁴±k (where the float idiom breaks — these cases pin
the *future* Tier B contract), (c) the region-address round-trip. `RegionAddressCodec` V2 step 1
migrates to the helpers — **behavior-identical for every reachable (all-positive) coordinate
today**; whether to also bump the codec version defensively (scaling doc suggests V3) is decided
with the `serialization-migration` skill at execution — if bumped, that step follows the full AOT
protocol; if not, the doc records why (no byte changes for reachable inputs).

### 4.3 Load/save failure contracts (CP-3, CP-6)

- Load: §3.3 Option B (fault → log → `IsLoading = false` → natural retry) + shell-return on
  partial parse + NS-1 robustness baselines (truncated payload, garbage bytes, wrong version →
  `null`, no leak, no throw across the `Task` boundary).
- Save: `ModifiedChunks.Remove` only after `SaveChunkAsync` reports success; on failure the chunk
  is re-marked (it is already unloaded — re-marking means the *data* object returned to the pool
  must NOT be re-marked; instead the failure path re-queues a save by coord from the still-live
  snapshot, or simpler: perform the remove in a success continuation and on failure log + re-add
  the coord to a retry list the next `UnloadChunks` pass drains — executor picks the minimal shape
  that survives the pooled-data lifetime, and documents it). The internal catch in `SaveChunkAsync`
  must surface failure to the caller (return `bool`/status) instead of swallowing.

### 4.4 What deliberately does NOT change

The spiral/activation flow, `LoadDistance`/view-distance semantics, the generation budget-retry
`continue` shape (HF-2's audit verdict), `ModManager` pending-mod routing, `LightingStateManager`
persistence, the startup coroutine's structure (v1), and every P-*/OM-*/SL-*/SU-* scheduling/
throughput behavior.

---

## 5. Scaling & palette readiness (the standing constraints)

| Future requirement                                     | Constraint on CP-* (and successors)                                                                                                                                                                                                                                                                |
|--------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Tier B** negative coords (scaling §3.2)              | CP-2's helpers are the only sanctioned chunk math from then on; new inline `/16`/`%16`/`FloorToInt` is a review reject.                                                                                                                                                                            |
| **Tier A** height change (scaling §2)                  | CP-7 unifies the height/width constants to one source first (the §5-checklist prerequisite); no CP phase hardcodes 128/16/8-sections.                                                                                                                                                              |
| **Tier C** cubic chunks (scaling §4)                   | Keep per-*column* state (`heightMap`, `SectionUniformSkyLevel`) identifiable and isolated on `ChunkData`; decisions extracted here take facts, not chunk refs, so a 3D key swap re-plumbs callers, not policies.                                                                                   |
| **Palette mapping** (palette doc §2/§4)                | Hydration (`ReadChunkInternal`→`PopulateFromSave`) and dehydration (`WriteChunkInternal`) stay single-site; `ModManager` pending-mod storage is the third seam (raw `ushort` IDs today — palette doc owns its migration). CP-3 must not fork the hydration path while adding the failure contract. |
| **Palette + Tier A memory** (uniform/palette sections) | `ChunkSection` pool + `SectionUniformSkyLevel` compaction are the natural palette-section substrate — CP-4/CP-7 leave section allocation single-site (`GetNewSection`/pool).                                                                                                                       |
| **Save format**                                        | Only CP-2's optional codec-version bump touches anything format-adjacent, under the AOT protocol; every other phase is runtime-only. Tripwire: any phase wanting a format change stops and re-scopes.                                                                                              |

---

## 6. Constraint compliance checklist

| Project constraint                              | How this plan complies                                                                                                                                               |
|-------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — lifecycle orchestration only.                                                                                                                            |
| Burst jobs 100 % Burst-compatible               | No job code touched; CP-2's helpers are plain static int math, Burst-safe by construction if jobs adopt them later.                                                  |
| No GC / LINQ in hot paths                       | Decisions are static pure functions over value-type facts; probes are `[Conditional]`; no new per-frame allocations.                                                 |
| Pooling conventions                             | CP-3 restores the shell-return contract; CP-4 keeps placeholder creation on the pool; CP-7 fixes the pool-target derivation; `Reset` invariants (B33/B34) untouched. |
| No BinaryFormatter/JSON for terrain             | Serializer internals untouched; CP-3 hardens the *boundary*; CP-2's optional codec bump follows the AOT migration protocol.                                          |
| BlockIDs constants, no raw IDs                  | N/A (the palette doc owns ID semantics).                                                                                                                             |

---

## 7. Phased implementation plan

**Universal regression gate for every phase**: `Minecraft Clone/Dev/Validate Lighting Engine`
(62 baselines, both modes), `Validate Meshing` (B1–B21), `Validate Mesh Build Queue` (9) — this
layer's edits sit under all three pipelines — plus `Validate Placement` when `ApplyModifications`
is touched (CP-4 does not touch its rule logic, but run it anyway: cheap);
`dotnet build "Assembly-CSharp.csproj"` AND `dotnet build "Assembly-CSharp-Editor.csproj"` clean.
New suites created here (NS-5 chunk math, NS-1 robustness seed) number independently from B1.
Workflow gotchas apply (new-file Unity import before `dotnet build`; menu suites can run stale
code — confirm flips after `RequestScriptCompilation` with a fresh `Unity_RunCommand` wave).
Behavior-changing phases (CP-3, CP-6) need in-game/fault-injection confirmation before their
baselines are trusted.

| Phase                                               | Scope (files)                                                                                                           | Effort | Depends on                         |
|-----------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------|:------:|------------------------------------|
| **CP-1 — Lifecycle observability probes**           | `World.cs`, `ChunkPoolManager.cs`, `ChunkStorageManager.cs` (editor-only diagnostics + debug-screen counters)           |   🟢   | —                                  |
| **CP-2 — WS-1: shift/mask chunk math + NS-5 suite** | `Helpers/ChunkMath.cs`, call-site migration (`WorldData`, `ChunkCoord`, `RegionAddressCodec`, …), new NS-5 suite        |   🟡   | —                                  |
| **CP-3 — Load-arm failure contract + NS-1 seed**    | `World.LoadOrGenerateChunk`, `ChunkStorageManager.LoadChunkAsync`, `ChunkSerializer.Deserialize`, new robustness checks |   🟡   | CP-1 (evidence)                    |
| **CP-4 — Placeholder consolidation**                | `Data/WorldData.cs` (`LoadChunk` retirement, `GetOrCreatePlaceholder`), `World.CheckViewDistance`                       |   🟢   | —                                  |
| **CP-5 — `ChunkUnloadDecision` extraction**         | new `Helpers/ChunkUnloadDecision.cs`; `World.UnloadChunks`; truth-table baselines                                       |   🟡   | CP-1 (counters); LP-3 coordination |
| **CP-6 — Save-on-unload durability fix**            | `World.UnloadChunks` save block; `ChunkStorageManager.SaveChunkAsync` (surface failure)                                 |   🟡   | CP-1 (evidence)                    |
| **CP-7 — Pool sizing + constants unification**      | `ChunkPoolManager.Update`; `VoxelData`/`ChunkMath` constants                                                            |   🟢   | CP-1 (churn data)                  |

**Minimal standalone-value set:** CP-1 + CP-3 (kills the silent-stall class) or CP-2 alone
(WS-1 + NS-5, fully independent). **Validation is built alongside, not after** — CP-2/3/5 each
ship their suites/baselines in the same commit as the code.

### CP-1 — Lifecycle observability probes (🟢, no behavior change)

- **Scope** (editor/dev `[Conditional]` dual-gate + a few always-on debug-screen counters where
  the overlay already exists):
    1. Load arm: count faults escaping `LoadChunkAsync` (wrap-and-rethrow with a counter at the
       await site — pre-CP-3 this documents today's loss), `Deserialize → null` occurrences, and
       placeholders older than N seconds still `IsLoading && !IsPopulated` (the F1 stuck-state
       detector — checked in the ~1 s lighting fail-safe scan's loop, which already walks
       `Chunks.Values`).
    2. Unload: per-reason deferral tallies (job / light-flags / strand — F6's observability; the
       §3.3-perf pinned-trail metric) + unload-per-pass counts, on the debug screen.
    3. Save: fired vs succeeded vs failed `SaveChunkAsync` counts (F5 evidence).
    4. Pool: destroys-per-minute per pool (F4 churn evidence) via `DynamicPool` prune counts.
- **Acceptance:** universal gate + a soak (streaming sprint, teleport, view-distance change,
  save-heavy exit) with results recorded here (Amended line). CP-3/6/7 read this evidence.
- **Doc-sync:** none (no behavior). **Serialization:** none.

> **Amended (2026-07-21, branch `feat/world-scaling`): implemented; regression-green; in-game soak pending.**
> Probes shipped, no behavior change. Files (the §7 3-file scope was under-listed — real edit set):
> `Helpers/DynamicPool.cs` + `Helpers/ConcurrentDynamicPool.cs` (cumulative `TotalDestroyed`, `Interlocked`
> on the concurrent pool) surfaced per-pool via `ChunkPoolManager.Destroyed*`; `ChunkStorageManager.cs`
> (`SavesFired`/`SavesCompleted`/`SavesFailed` — fired at method entry so both fire sites are captured,
> failed in the existing swallow-catch); `ChunkSerializer.cs` (`DeserializeFailures` in the parse catch);
> `World.cs` (load-arm fault wrapper `LoadOrGenerateChunk` → `LoadOrGenerateChunkInner`; unload per-reason
> deferral + per-pass tallies; stuck-`IsLoading` detector on the fail-safe scan); `DebugScreen.cs`
> (middle-left "CHUNK LIFECYCLE (CP-1)" panel, Full mode).
>
> **Decisions taken** (session decision-menu): (1) *always-on* display counters (unload/save/pool/deserialize),
> only the load-arm fault counter + stuck detector dev-gated via dual `[Conditional]`; (2) the fail-safe scan
> was *restructured to always walk* `ChunkValues` — the stuck detector rides the existing lighting scan when
> lighting is on and a dedicated dev-only walk when it is off, so it works in all configs without a second
> walk; (3) `SavesFailed` increments *inside the existing catch* (real failure count, pure probe).
>
> **Drift corrections found vs this doc's pre-P-4 §2.1/F1 audit** (fold in when §2 is next re-anchored):
> (A) `CheckViewDistance` no longer fires `LoadOrGenerateChunk` — it enqueues; `DrainGenerationRequests`
> (P-4 §3.1) is the sole runtime fire site and sets `IsLoading` at admission. The "await site W:2551 /
> gate W:2547" anchors are stale. (B) `IsLoading` now *has* a non-`Reset` clear site
> (`WorldJobManager` §3.2 discard); "nothing clears `IsLoading`" holds only for the *fault* path now.
> (C) There are **two** `SaveChunkAsync` fire sites (`UnloadChunks` + `SaveModifiedChunks`); F5 names only
> the first. (D) The load-arm wrapper is the exact seam CP-3 converts (its `catch` → log → clear
> `IsLoading` → return).
>
> **Verification done:** `dotnet build Assembly-CSharp.csproj` clean (0 errors); Unity recompiled (fresh DLL);
> universal gate green against fresh DLL — **Lighting 88/88, Meshing 23/23, Mesh Build Queue 9/9**, no errors /
> `[FAIL]` / isolation violations. The fail-safe-scan restructure is the risk edit; Lighting 88/88 (incl.
> convergence) is its regression proof.
>
> **Amended (2026-07-21): in-game soak result — Q1 answered, and it confirms the §3.3 pinned-trail is real.**
> Fly-around soak then stationary ≥10 s (the pinned set did **not** drain while stationary). HUD after settling:
> `Unloaded last pass 17 · Deferred job 0 / light 308 / strand 395 · Saves 1795/1795/0 · Deserialize 0 ·
> Load-arm faults 0 · Stuck loading 0 · Pool destroys chunk 0 / data 145 / sect 346`. A read-only live probe
> (`World.Instance` walk) classified the pinned set: **totalLoaded 1096, beyondUnload 743 (all populated),
> lightPending 327, initialLighting 231, needsEdge 137, awaitingMainThread 0; of the 343 light-/initial-pinned
> chunks, 343 have a missing neighbor and 0 have all neighbors present; strandCandidates 739.** Verdict:
> **~68 % of loaded chunks sit beyond unload distance and cannot be reclaimed** — trailing-edge chunks whose
> outer neighbors were never generated fail the neighbor-data gate, so their lighting never completes, so
> `UnloadChunks` light-pins them permanently and strand-pins their inward neighbors. `awaitingMainThread=0` +
> `allNeighborsPresent=0` rule out a genuine stall — this is exactly the **F6 / §3.3 pinned-trail** and the
> target of **P-4 rec 3 / CP-5** (unload light-pending out-of-range chunks by persisting their pending lighting).
> Clean signals: **Saves 0 failed (F5 clean), Deserialize 0, Load-arm faults 0, Stuck loading 0** — no silent
> stall or durability loss in normal play (F1/F5 injection tests still pending). Pool churn: **chunk pool
> destroys 0 while data/section churn (145/346)** — the **F4** width-vs-area asymmetry, CP-7 evidence.
> **This soak strongly prioritizes P-4 rec 3 / CP-5** (🔴, deadlock history — own plan + `chunk-lifecycle`).
>
> **Amended (2026-07-21): F1/F5 fault-injection confirmed the probes fire on real faults.** Via temporary one-shot
> inject hooks in `ChunkStorageManager` (reverted after — not committed): **F5** — armed `InjectSaveFaultOnce`
> and called `SaveChunkAsync` on a loaded chunk → `SavesFired 0→1, SavesCompleted 0 (unchanged), SavesFailed
> 0→1`, plus the swallow-catch logged `[SaveChunkAsync] Failed … CP-1 TEMP injected save fault` — the F5
> durability hole is now observable. **F1** — armed `InjectLoadFaultOnce`, flew one chunk in → `LoadArmFaults
> 0→1` (fault propagated through the load arm to the wrapper catch) **and** `StuckLoading 0→1` (the faulted
> placeholder stayed `IsLoading && !IsPopulated` across two ~1s scans). Confirms the stuck placeholder does NOT
> self-recover pre-CP-3 (the `!IsLoading` re-enqueue gate keeps skipping it) — the exact F1 stall CP-3 closes.

### CP-2 — WS-1 execution: shift/mask chunk math + NS-5 equivalence suite (🟡)

- **Scope:** §4.2. Helpers in `ChunkMath` (`public const`/aggressive-inline static int ops);
  migrate the grep checklist from `WORLD_SCALING_ANALYSIS.md` §3.2/§5 (`FloorToInt(... / 16f)`,
  `/ VoxelData.ChunkWidth`, `% 32`, `ToVoxelOrigin`/`FromVoxelOrigin` internals,
  `WorldData.GetChunkCoordFor`/`GetLocalVoxelPositionInChunk`, `RegionAddressCodec.V2Codec`
  step 1). New editor suite `Minecraft Clone/Dev/Validate Chunk Math` (NS-5 seed): old-idiom
  equivalence over the positive domain, hand-derived negative-domain + big-coordinate cases,
  region round-trips. Y stays untouched (no `WorldMinY` — that is Tier A's).
- **Prove-red:** flip one helper (`>> 4` → `/ 16`) → the negative-domain baselines red; restore.
- **Acceptance:** universal gate + NS-5 green + an in-game session on an **existing saved world**
  (region addressing must resolve identical files — any migration prompt or missing chunk is a
  stop-the-line failure) + new-world generation smoke.
- **Serialization note:** the codec-math migration is byte-identical for all reachable inputs;
  the defensive V3 version bump is an execution-time decision under the `serialization-migration`
  skill (record the verdict here either way).
- **Doc-sync:** `WORLD_SCALING_ANALYSIS.md` §3.2/§6 (WS-1 → executed, pointer here);
  `PERFORMANCE_IMPROVEMENTS_REPORT.md` WS-1 row status; `CHUNK_LIFECYCLE_PIPELINE.md` untouched.

### CP-3 — Load-arm failure contract + NS-1 robustness seed (🟡, failure-path behavior change)

- **Scope:** §3.3 Option B — try/catch the `LoadOrGenerateChunk` body (fault → one
  `Debug.LogError` → `IsLoading = false` → return, placeholder retryable); audit
  `RegionFile.LoadChunkData`/`GetRegion` fault modes (contract: throw ⇒ caught above, or catch-
  internally-and-return-null — pick one and document); return the pooled shell on
  `ReadChunkInternal` partial-parse failure (the `Deserialize` catch gains the return — thread-safe,
  the data pool is concurrent). NS-1 seed: editor checks feeding `Deserialize` truncated /
  garbage / wrong-version payloads → `null`, no throw, and (via pool counters) no shell leak.
- **Prove-red:** fault-injection — temporarily make `LoadChunkAsync` throw for one coord (editor
  hook): pre-fix, the placeholder sticks (`IsLoading` forever, CP-1's stuck-detector fires, and
  neighbor lighting visibly parks); post-fix, one error logs and the chunk loads on the next
  boundary crossing. This is the in-game confirmation too (the HF-2 injection precedent).
- **Acceptance:** universal gate + the injection scenario + a normal load soak (zero new errors).
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §5.1 (load path gains its failure contract) + §2
  (`IsLoading` row: now cleared on fault); `INFINITE_WORLD_STORAGE_...md` boundary note.
  **Serialization:** none (read-path behavior on *invalid* data only).

### CP-4 — Placeholder consolidation + `WorldData.LoadChunk` retirement (🟢)

- **Scope:** one `WorldData.GetOrCreatePlaceholder(Vector2Int)` used by all three sites (F2);
  delete `LoadChunk`'s commented legacy block + stale TODOs and fold its callers
  (`RequestChunk(create:true)`) onto the new method; document (XML) the resurrect semantics of
  `RequestChunk(create:true)` — or, if the executor confirms `ProcessGenerationJobs` is its only
  `true` caller and job-pinning makes resurrection unreachable, add an editor-only assert that the
  chunk already exists (making the convention structural). Zero intended behavior change.
- **Acceptance:** universal gate + streaming/load smoke. **Doc-sync:** pipeline doc §5.1 flowchart
  (placeholder step names the single site). **Serialization:** none.

### CP-5 — `ChunkUnloadDecision` extraction (🟡)

- **Scope:** §4.1 — pure decision + `UnloadChunks` routes through it (behavior-identical arms,
  including the exact pin set and the 8-neighbor strand rule); truth-table baselines (new
  lifecycle-suite partial or NS-5's file — executor picks the home and states it) covering every
  arm incl. the §9.6 stranding cases; CP-1's counters keyed by the enum.
- **Coordination:** LP-3 (lighting doc) removes `IsAwaitingMainThreadProcess` — its gate term here
  shrinks `DeferLightPending`; land in either order, the truth table updates with it. P-4 rec 3
  later adds its persist-and-unload arm *here*, baselined.
- **Prove-red:** invert the strand-check term → truth-table baselines red (and only those).
- **Acceptance:** universal gate + an unload-heavy in-game soak (sprint + return; no stranded
  chunks, deferral counters sane, memory flat-ish).
- **Doc-sync:** pipeline doc §9.6 (points at the decision + its baselines as the now-testable
  guard). **Serialization:** none.

### CP-6 — Save-on-unload durability (🟡, failure-path behavior change)

- **Scope:** §4.3 — `SaveChunkAsync` surfaces success/failure; `ModifiedChunks.Remove` moves to
  the success path; failure re-queues (coord-keyed retry drained by a later `UnloadChunks` pass or
  next session via the existing modified tracking — executor picks the minimal correct shape given
  the pooled-data lifetime, which is the subtle part: the `ChunkData` is recycled immediately, so
  the retry must not hold the reference). OM-3 (task flood) explicitly untouched.
- **Prove-red / verification:** fault-injection (make one save throw): pre-fix the edit is
  silently gone on reload; post-fix it survives (retry or re-mark). In-game: edit → walk away
  (unload) → return → edits present; plus the injection run. CP-1's save counters confirm.
- **Doc-sync:** pipeline doc unload step; `INFINITE_WORLD_STORAGE_...md` save-boundary note.
  **Serialization:** none (no format change — failure-path bookkeeping only).

### CP-7 — Pool sizing + constants unification (🟢)

- **Scope:** (a) resolve F4 with CP-1's churn data: either the formula becomes the commented area
  (pool covers the active set + buffer) or the width is confirmed as the intended spare-size and
  the comment is rewritten to say so — decide on measurement, not taste; (b) unify
  `VoxelData.ChunkHeight`/`ChunkMath.CHUNK_HEIGHT` (+ width twins) to a single declaration site
  (the other becomes an alias or is removed via `refactor-safely`) — the scaling doc §5
  prerequisite, mechanical, zero behavior.
- **Acceptance:** universal gate + a teleport/view-distance-change session with CP-1's churn
  counters before/after (if the formula changed, churn must drop; memory ceiling must not grow
  past the buffer intent). **Doc-sync:** scaling doc §5 checklist tick (constants); pipeline doc
  untouched. **Serialization:** none.

---

## 8. Extension roadmap (post-CP-7, in intended order)

| Version | Extension                                                                                                                                                                                                                    |
|---------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | **P-4 backpressure lands on CP-5's seam** (in-flight caps, out-of-range discard, time budgets, panic gate — owned by the perf report; CP-1's counters are its before/after evidence).                                        |
| **v2**  | **Startup unification** (F7): startup = the normal Update pipeline with elevated/time-based budgets, retiring the hand-rolled coroutine phases — only after LP-5 + P-4, with SU-1/SU-2's loading-screen goals as the driver. |
| **v2**  | **NS-3 chunk-lifecycle harness** (coverage roadmap): drives `ChunkUnloadDecision`, the LP-4 transition API, and MP-2's scheduling decision through adversarial event orders — the extractions here are its prerequisites.    |
| **v3+** | **Tier A/B/C execution** per `WORLD_SCALING_ANALYSIS.md` (CP-2/CP-7 are its named early slices; palettes per `CHUNK_PALETTE_MAPPING.md` land on the §5 seams).                                                               |

---

## 9. Open questions

1. **CP-1 probe results** — stuck-`IsLoading` occurrences, save-failure rate, deferral-reason
   distribution, pool churn. Gates CP-3/6/7 scoping; answers land as Amended lines.
2. **CP-2 codec version bump** — defensive V3 bump vs. documented no-bump (byte-identical for all
   reachable inputs). Decide with the `serialization-migration` skill at execution.
3. **F4 intent** — was the width-sized spare pool deliberate? Measurement (CP-1) plus a look at
   `DynamicPool.UpdatePruning`'s exact semantics answers it; CP-7 records the verdict.
4. **CP-6 retry shape** — coord-keyed retry list vs. success-continuation-only, constrained by the
   immediate pool recycle of the saved `ChunkData`. Executor decides and documents.

---

## Document History

* **v1.0** - Initial design (outer-lifecycle census + F1–F9 findings + CP-1…CP-7 phased plan at `0a12036`)
* **v1.1** - Added F10 (generation fault-path double-release; surfaced by the 2026-07-10 branch code review of `feat/async-lighting-validation-suite`)

---

**Last Updated:** 2026-07-10
**Next Review:** when CP-1 starts (re-verify §2 line anchors against HEAD), or when Tier A/B or palette work is scheduled (re-check §5 constraints)
