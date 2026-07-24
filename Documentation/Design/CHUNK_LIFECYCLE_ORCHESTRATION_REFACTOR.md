# Chunk Lifecycle Orchestration Refactor (CP-*)

**Version:** 1.6 **Date:** 2026-07-06 **Status:** ✅ Executed — CP-1…CP-7 all shipped (arc closed 2026-07-23). **Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

> Clean-up / refactor plan for the **outer chunk lifecycle** — placeholder creation and the
> view-distance spiral, the async load-or-generate arm, generation completion + structure-mod
> application, activation/visual linking, unload/save, and the pool recycle path — completing the
> trilogy with [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md) (LP- *,
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
> clean per the two future designs. Performance items stay owned by the existing P- */OM-*/SL- */
> SU-* backlog — this plan prepares their seams, it does not re-propose them.

**Audited:** 2026-07-06, at commit `0a12036` (branch `feat/async-lighting-validation-suite`). Findings are from static review of `World.cs` (`CheckViewDistance` L2507–2650, `LoadOrGenerateChunk`
L763–941, `UnloadChunks` L2330–2466, `ApplyModifications` L2065+, startup coroutine phases L959–1123, `CollectInitialChunks` L736–755), `WorldJobManager.cs` (`ProcessGenerationJobs`
L657–870), `ChunkPoolManager.cs` (full), `Data/WorldData.cs` (`RequestChunk`/`LoadChunk`/
`EnsureChunkExists`/`GetChunkCoordFor` L68–165), `Serialization/ChunkStorageManager.cs`
(`LoadChunkAsync` L50–83, `SaveChunk(Async)` L90–170), `Serialization/ChunkSerializer.cs`
(`Deserialize` L74–104, `ReadChunkInternal`), `Data/ChunkData.cs` (`Reset` L242–288), and
`Chunk.cs` (`Reset`/`Release`). The LP/MP docs' surveys of the same files are presumed current (same day). Line numbers are anchors for the executor, not contracts — re-verify before editing.

**Relationship to other documents:**

- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the authoritative pipeline reference; §2 (data-axis flags), §5.1 (generation path), §9.5/§9.6 (orphaning/stranding risks) are restructured or made observable here; every phase doc-syncs it.
- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — the Tier A/B/C future this plan keeps open: CP-2 **executes WS-1** (§3.2's shift/mask fix, called out there as the only zero-risk early-shippable slice); CP-7 executes the §5-checklist constants unification; §5 here lists the scaling seams every phase must not close (identity types, per-column data, `IsChunkInWorld`).
- [`CHUNK_PALETTE_MAPPING.md`](CHUNK_PALETTE_MAPPING.md) — the future palette boundary: hydration (`ChunkSerializer.ReadChunkInternal` → `PopulateFromSave`) and dehydration (`WriteChunkInternal`) are each single-site today, and `ModManager`'s pending mods are the third seam that doc names. §5 pins "keep these single-site" as a constraint; no CP phase touches the palette itself.
- [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) — the performance deep-dive for this same layer (its §3 backpressure = P-4, §4.4 stable-bit = P-5). This plan is the *clarity/testability* complement: CP-1's deferral counters give §3.3's "pinned trail" its missing instrumentation, and CP-5's decision extraction is the seam P-4's rec 3 ("unload light-pending chunks via persistence") will land on.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — P-1..P-6, OM-1..3, SL-1..4, SU-1..2, WS-1 keep their IDs. CP-2 executes WS-1; everything else is interlock only (named per phase).
- [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md) /
  [`MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md) — the sibling plans for the inner loops. Boundary: LP owns the lighting work flags/gates/scan; MP owns mesh request→apply; CP owns identity, creation/load, population, unload/save, pools. Coordination points are named in §7 (LP-4's data-axis note, MP-6's draw-queue clear).
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — CP-2 builds the **NS-5** equivalence suite alongside WS-1; CP-3 seeds **NS-1** (deserialize robustness); CP-1's probes + CP-5's decision are **NS-3** groundwork (the full lifecycle harness stays future).
- [`../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
  — the storage architecture CP-3/CP-6 harden at the orchestration boundary (region internals untouched; `REGION_FILE_CONCURRENCY.md`/SL-4 owns the locking design).

---

## 1. Goals & non-goals

### Goals

1. **Give the load and save arms a failure contract** — no fault may strand `IsLoading`, leak a pooled shell, or silently drop a chunk's modified status (§2.4 F1/F7).
2. **Make the lifecycle's policies pure and testable** — unload deferral (F6) and placeholder creation (F2) become single-site, decision-extracted, truth-table-baselined code, the LP/MP pattern applied to the outer loop.
3. **Execute WS-1** — centralized shift/mask coordinate math with an equivalence suite (NS-5), removing the float-roundtrip/truncation minefield before Tier B can ever trip it (§2.4 F3).
4. **Keep the scaling and palette seams open** — single-site hydration/dehydration, per-column data isolated, constants unified, no new hardcoded 16/128 (§5).
5. **Preserve behavior at every phase boundary except the named fixes (CP-3, CP-6)** — both fixes are failure-path-only changes with prove-red evidence and in-game confirmation.
6. *(SECONDARY)* Make the P-4/OM-3 cost surfaces observable (deferral counters, pool churn, save concurrency) so the performance items land on measured ground.

### Non-goals (v1)

- **Backpressure, budgets, caps, panic gates** — owned by **P-4** / **SU-2** (perf report). CP-1 provides their instrumentation; CP-5 provides P-4-rec-3's seam. No scheduling-policy change here.
- **The "lighting stable" save bit** (**P-5**), **save-task capping** (**OM-3**), **load/save alloc reduction** (**SL-1/SL-3**), **region locking** (**SL-4**) — owned by the perf backlog. CP-6 fixes only the *correctness* hole in the save-fire path, not its throughput.
- **Tier A/B/C themselves** — heights, `WorldMinY`, unbounded XZ, floating origin, cubic chunks stay in `WORLD_SCALING_ANALYSIS.md`. CP ships the two slices that doc marks as safe-early (WS-1, constants unification) and nothing else.
- **Palette mapping implementation** — its own doc; CP only protects the seams.
- **The full NS-3 chunk-lifecycle harness** — deliberately deferred (see §3.1); this plan ships its groundwork instead.
- **Startup-coroutine restructuring** — the gen/light/mesh force-complete phases stay; LP-5 unifies the lighting arm decision, SU-1/SU-2 own the perf shape. A deeper "startup = Update with elevated budgets" unification is a v2 idea only (§8).
- **`MeshBuildQueue`, lighting scheduler, job internals** — owned by MP- */LP-*/existing systems.

---

## 2. Current state — the outer lifecycle surface

### 2.1 Stage map

| # | Stage                     | Code                                                                                                                                                                                                                                                                                                                   | Coverage today                                           |
|---|---------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------|
| 1 | **Placeholder + spiral**  | `CheckViewDistance` (W:2507): on chunk-boundary crossing, spiral over `LoadDistance` square; missing → pooled placeholder into `worldData.Chunks`; `!IsPopulated && !IsLoading && !GenerationJobs` → `IsLoading = true` + fire-and-forget `LoadOrGenerateChunk`                                                        | ❌ none                                                  |
| 2 | **Async load arm**        | `LoadOrGenerateChunk` (W:763): `await StorageManager.LoadChunkAsync` (background `Task.Run` → region read → `ChunkSerializer.Deserialize` into a **pooled shell**) → mid-await unload guard → `PopulateFromSave` (hydration copy) → shell returned → pending mods + lighting restore → edge-check/initial-lighting arm | ❌ none                                                  |
| 3 | **Generate arm**          | not on disk / persistence off → `JobManager.ScheduleGeneration` (W:940)                                                                                                                                                                                                                                                | terrain content via worldgen tooling; orchestration ❌   |
| 4 | **Generation completion** | `ProcessGenerationJobs` (WJM:657): HF-2 two-stage fault isolation; `Populate`; structure expansion under `maxStructureModsPerFrame` budget (un-released budget-retry `continue`s); pending-mod + pending-lighting recovery; `NeedsInitialLighting = true`                                                              | ❌ orchestration none                                    |
| 5 | **Mod application**       | `ApplyModifications` (W:2065): drains `_modifications`; unpopulated target → `ModManager.AddPendingMod` (persisted); placement rules; `ModifyVoxel`                                                                                                                                                                    | placement *rules* ✅ (placement suite); routing ❌       |
| 6 | **Activation / visual**   | `CheckViewDistance` view-set diff: pool `Chunk` get/return, `_chunkMap`, borders, re-request mesh when populated                                                                                                                                                                                                       | ❌ none                                                  |
| 7 | **Unload**                | `UnloadChunks` (W:2330): distance test → job-pin → light-flag-pin → 8-neighbor strand-pin → persist orphaned sunlight columns → fire-and-forget save if modified → mesh-queue remove → pool visual + data, remove from `Chunks`                                                                                        | ❌ none                                                  |
| 8 | **Pool recycle**          | `ChunkPoolManager` (5 pools; data/section pools concurrent for background deserialization); `ChunkData.Reset` / `Chunk.Reset`/`Release`                                                                                                                                                                                | `Reset` transient-state ✅ (lighting B33/B34); sizing ❌ |

### 2.2 Identity & coordinate math (the WS-1 surface)

Three key spaces coexist: `ChunkCoord` (chunk grid — `_chunkMap`, all three job dictionaries,
`ModManager`), `Vector2Int` voxel-origin (`worldData.Chunks`, `LightWorkScheduler`,
`SunlightRecalculationQueue`, serialization), and float world positions (transforms, player). Conversions are scattered and idiom-mixed: `Mathf.FloorToInt(worldPos.x / 16f) * 16`
(`WorldData.GetChunkCoordFor` W:145–150 — float roundtrip, breaks beyond ±2²⁴),
`ChunkCoord.FromVoxelOrigin`/`ToVoxelOrigin`, and the **already-wrong-for-negatives** truncating division in `RegionAddressCodec.V2Codec` step 1 (scaling doc §3.2). All-positive coordinates make every variant agree today — which is exactly why no test can currently red a drift.

### 2.3 What no suite can currently red

- A load-arm fault (stuck `IsLoading`, leaked shell) — §2.4 F1.
- A wrong unload deferral (either direction: premature unload = the §9.6 stranding deadlock history; over-deferral = the P-4 pinned-trail memory climb).
- A silently-lost save (F7) or a placeholder-creation drift across its three sites (F2).
- Any coordinate-math change (no equivalence suite; NS-5 is the roadmap's named answer).
- Pool sizing/churn behavior (F4).

### 2.4 Findings

| #   | Finding                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |       Addressed by       |
|-----|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------------------------:|
| F1  | **The async load arm has no failure contract.** `Deserialize` catches → `null` → "(re-)generated" (good), but exceptions *outside* it — `GetRegion`/`RegionFile.LoadChunkData` I/O faults — propagate out of the `Task.Run`, fault the awaited call, and `_ = LoadOrGenerateChunk(...)` (W:2551) discards the `Awaitable`. Nothing clears `IsLoading` on any fault, and the `!IsLoading` gate (W:2547) then blocks every retry: a permanently-unpopulated placeholder sits in range, and every neighbor's lighting parks on `AreNeighborsDataReady` forever (rescued by nothing — the fail-safe re-promotes but the gate keeps failing). Sub-finding: a mid-parse throw inside `ReadChunkInternal` leaks the pooled shell (+ any sections already attached) — `Deserialize`'s catch returns null without returning the shell (bounded, managed-only, but it bypasses the pool contract). The mid-await unload guard (W:781–790) is correct — keep and pin it. **✅ FIXED (2026-07-22, §7 CP-3 Amended block).** Audit drift: `RegionFile.LoadChunkData` actually caught ALL exceptions internally → null → "not on disk" → **regenerate over saved data** (worse than the stall); the only fault escaping the `Task.Run` was `GetRegion`'s `Lazy<RegionFile>` ctor, whose default mode caches the exception for the session. Both closed by CP-3. |           CP-3           |
| F2  | **Placeholder creation is triplicated and one site wears a misleading name.** `CheckViewDistance` (W:2542), `WorldData.LoadChunk` (W:113), and `WorldData.EnsureChunkExists` (W:132) all do `Chunks.Add(pool.GetChunkData(pos))`. `WorldData.LoadChunk` contains only a commented-out legacy save-system block + two contradictory TODOs — it loads nothing. `RequestChunk(pos, allowChunkDataCreation: true)` can therefore silently *resurrect* a placeholder for an unloaded chunk (benign today only because job-pinning prevents the race — convention, not structure). **✅ FIXED (2026-07-22, §7 CP-4 Amended block).** Audit drift: `ProcessGenerationJobs` was NOT the only `create:true` caller — three exist (see the Amended block); the third (`GetHighestVoxel`) legitimately relies on creation, so the resurrect semantics are documented on `RequestChunk` and the two unreachable sites are editor-asserted instead.                                                                                                                                                                                                                                                                                                                                                                                                            |           CP-4           |
| F3  | **Coordinate math is idiom-mixed and negative-hostile** (§2.2) — the WS-1 surface, with the float-precision hazard and the live (currently-unreachable) `RegionAddressCodec` truncation bug. No equivalence coverage exists. **✅ FIXED** — bulk out of band by WS-1 (2026-07-12: shift/mask helpers + call-site migration + equivalence sweeps in the "Chunk Math" suite); residue (NS-5 region-codec pins, post-WS-1 call-site audit, `IsChunkAligned` alignment helper) closed 2026-07-22 (§7 CP-2 Amended block).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |           CP-2           |
| F4  | **Pool sizing comment and code disagree.** `ChunkPoolManager.Update` (CPM:103–116): the comment derives "Area = (Dist·2+1)²" but the code computes `chunksNeeded = dist·2+1` — the *width*. Spare-pool targets are therefore ~25× smaller than the comment intends (e.g. ~31 vs ~781 at view distance 12). Plausibly the width was the deliberate "one edge strafe" spare size — but then the comment lies; if area was intended, every teleport/view-distance change churns hundreds of GameObject destroy/creates. Needs churn measurement, then either the comment or the formula fixed. **✅ FIXED (2026-07-23, §7 CP-7 Amended block).** Measurement showed BOTH halves were wrong: the comment described an abandoned area implementation (deliberately reverted for idle retention at vd 5), and the width formula used the view radius for pools serving the unload boundary with no answer for reload transients. Replaced by per-pool row soft caps + service-area hard caps + a 90 s no-demand linger window.                                                                                                                                                                                                                                                                                                                          |       CP-1 → CP-7        |
| F5  | **Save-on-unload drops the modified flag before the save succeeds.** `UnloadChunks` (W:2422–2433) fires `SaveChunkAsync` and immediately `ModifiedChunks.Remove(data)`. `SaveChunkAsync` catches internally (CSM:164 — so the `ContinueWith` fault log is nearly unreachable) and simply logs; the chunk is then unloaded, unmodified-flagged, and its session edits exist nowhere. A failed save must re-mark (or the remove must move into a success continuation). Distinct from OM-3 (task flood = throughput), this is a durability hole.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |           CP-6           |
| F6  | **Unload policy is a monolithic inline block.** Distance → job-pin → light-pin → 8-neighbor strand-scan → persist → save → pool teardown, all inline in `UnloadChunks`. The deferral rules are the pipeline's deadlock-vs-memory-climb balance point (pipeline doc §9.6 fixed a deadlock here; perf analysis §3.3 blames the same rules for the pinned-trail climb) — and they are unobservable and untestable. P-4's rec 3 will edit exactly these rules.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |        CP-1, CP-5        |
| F7  | **Startup coroutine is a second, hand-rolled pipeline** (gen loop / lighting sweeps / mesh force-complete with own budgets + safety breaks). LP-5 unifies its lighting arm decision; SU-1/SU-2 own its perf. The structural duplication itself is accepted for v1 (§8 v2 idea).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |         §8 (v2)          |
| F8  | **Duplicate world-dimension constants**: `VoxelData.ChunkHeight` vs `ChunkMath.CHUNK_HEIGHT` (+ width twins) — the scaling doc's §5 checklist names unification as a prerequisite for ever touching the value; a mismatch compiles fine and corrupts indexing silently. **✅ FIXED (2026-07-23, §7 CP-7 Amended block):** `ChunkMath.CHUNK_WIDTH`/`CHUNK_HEIGHT` are now const aliases of `VoxelData.ChunkWidth`/`ChunkHeight` (single declaration site; const-to-const compiles to literals, Burst-safe — the "self-contained for Burst" duplication claim was already moot, jobs reference `VoxelData.*` directly). Frozen migration/V1 literals deliberately untouched.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |           CP-7           |
| F9  | **Hydration double-copy is the palette seam.** Disk → pooled shell (`ReadChunkInternal`, background thread) → `PopulateFromSave` copy into the live placeholder → shell returned. This is where palette hydration (LocalID→GlobalID remap) will run; SL-1 may later want the shell eliminated. Not a defect — a seam to keep single-site and to document (constraint, §5).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |     §5 (constraint)      |
| F10 | **Generation fault path can double-release a job** (`WorldJobManager.cs:~849`, in the HF-2-style stage-2 fault isolation added on `feat/async-lighting-validation-suite`): the catch releases only when the happy path has not (`released` flag), but if `ReleaseGenerationJobData` *itself* throws mid-release — after `_activeVoxelListPool.Return`, during `Dispose` — `released` is still false and the catch calls it again, returning the same `ActiveVoxels` list to the pool twice. Two future generation jobs then share one live list (silent cross-job active-voxel corruption). Error-path-only (PLAUSIBLE, 2026-07-10 branch review); fix is a one-liner — make the release idempotent (set a flag between the pool `Return` and `Dispose`, or `null`/default the pooled ref before disposing).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      | — (standalone hardening) |

---

## 3. Decisions

### 3.1 Decision: how to make the outer lifecycle testable

#### Option A — build the NS-3 chunk-lifecycle harness now (rejected for v1)

- ✅ The roadmap's endgame: adversarial event orders, convergence + flag-pairing assertions.
- ❌ **Premature and 🔴-sized.** The coverage roadmap itself sequences NS-3 late ("the hardest harness on this list") and its embryo (`LightingFrameSimulator`) plus the LP/MP extractions are still landing. Stubbing `World`-level orchestration before the decisions are extracted would test a shape the LP/MP/CP refactors are about to change.

#### Option B — pure-decision extraction + probes + targeted mini-suites ✅ **CHOSEN**

The same pattern that worked five times (A2/B2/HF-4/LP/MP): extract the *policies* (unload deferral, placeholder creation, coordinate math) into pure code with truth-table baselines, make the *failure paths* observable with `[Conditional]` probes, and seed the two cheap named suites — **NS-5** (coordinate equivalence — the roadmap's "best value-per-effort") and **NS-1**
(deserialize robustness). When NS-3 is eventually built, it drives these extracted decisions instead of re-deriving them.

#### Option C — leave it to in-game verification (rejected)

- ❌ This layer's failure modes are *silent by construction* (stuck `IsLoading`, lost saves, pinned trails) — in-game play is precisely what doesn't surface them.

### 3.2 Decision: WS-1 timing — execute now ✅

`WORLD_SCALING_ANALYSIS.md` §6 already answers this: the shift/mask cleanup "can ship early and independently, and is the only part of this document with zero save/seed risk when done correctly" — plus it is a micro-perf win (drops float roundtrips from every chunk lookup). In the all-positive world, `x >> 4` / `x & 15` are provably identical to every current idiom, so CP-2 is behavior-preserving *and* can carry an equivalence suite that also pins the negative domain the future Tier B needs. Waiting buys nothing; every month adds new call sites to audit.
(Rejected alternative — defer to Tier B: the audit grows, and Tier B then starts on an unproven minefield.)

### 3.3 Decision: load-arm failure policy (F1)

#### Option A — quarantine faulted coords (retry budget + blacklist) (rejected)

- ❌ New state and policy for a case whose *transient* form (file lock, AV scan) is best served by simple retry and whose *permanent* form (corrupt file) is already handled by
  `Deserialize → null → regenerate`. Over-engineering.

#### Option B — clear-and-retry, aligned with the existing corrupt-file intent ✅ **CHOSEN**

Wrap the `LoadOrGenerateChunk` body: any fault → one `Debug.LogError` (errors are the regression signal) → `IsLoading = false` → return. The placeholder stays; the next `CheckViewDistance`
boundary crossing retries naturally (the same convergence shape as every other retry in this engine). A *persistently* faulting file shows up as a repeating error log (loud), and the
`Deserialize` null-path already provides the regenerate escape hatch. Plus: return the pooled shell on the `ReadChunkInternal` partial-parse path so the pool contract holds.

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

`UnloadChunks` becomes: gather facts → `Evaluate` → switch (the persist/save/teardown body unchanged). CP-1's counters tally the deferral reasons per pass — the §3.3-perf-analysis "pinned trail" becomes a number on the debug screen instead of a hypothesis. P-4's rec 3 later lands as a new arm in this one function, baselined.

### 4.2 `ChunkMath` shift/mask + NS-5 equivalence suite (CP-2)

Per the scaling doc §3.2: `VoxelToChunk(int) => x >> 4`, `VoxelToLocal(int) => x & 15`,
`ChunkToRegion(int) => x >> 5`, etc., as the **only** sanctioned chunk math; every
`FloorToInt(x / 16f)`, `/ ChunkWidth`, `% 32` call site migrates (grep checklist in the scaling doc §5). The NS-5 suite (`Minecraft Clone/Dev/Validate Chunk Math`, new, own numbering B1+)
asserts: (a) equivalence with the old idioms across the positive domain, (b) hand-derived correctness on the negative domain and at ±2²⁴±k (where the float idiom breaks — these cases pin the *future* Tier B contract), (c) the region-address round-trip. `RegionAddressCodec` V2 step 1 migrates to the helpers — **behavior-identical for every reachable (all-positive) coordinate today**; whether to also bump the codec version defensively (scaling doc suggests V3) is decided with the `serialization-migration` skill at execution — if bumped, that step follows
the full AOT protocol; if not, the doc records why (no byte changes for reachable inputs).

### 4.3 Load/save failure contracts (CP-3, CP-6)

- Load: §3.3 Option B (fault → log → `IsLoading = false` → natural retry) + shell-return on partial parse + NS-1 robustness baselines (truncated payload, garbage bytes, wrong version →
  `null`, no leak, no throw across the `Task` boundary).
- Save: `ModifiedChunks.Remove` only after `SaveChunkAsync` reports success; on failure the chunk is re-marked (it is already unloaded — re-marking means the *data* object returned to the pool must NOT be re-marked; instead the failure path re-queues a save by coord from the still-live snapshot, or simpler: perform the remove in a success continuation and on failure log + re-add the coord to a retry list the next `UnloadChunks` pass drains — executor picks the minimal shape that survives the pooled-data lifetime, and documents it). The internal catch in
  `SaveChunkAsync`
  must surface failure to the caller (return `bool`/status) instead of swallowing.

### 4.4 What deliberately does NOT change

The spiral/activation flow, `LoadDistance`/view-distance semantics, the generation budget-retry
`continue` shape (HF-2's audit verdict), `ModManager` pending-mod routing, `LightingStateManager`
persistence, the startup coroutine's structure (v1), and every P- */OM-*/SL- */SU-* scheduling/ throughput behavior.

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
(62 baselines, both modes), `Validate Meshing` (B1–B21), `Validate Mesh Build Queue` (9) — this layer's edits sit under all three pipelines — plus `Validate Placement` when `ApplyModifications`
is touched (CP-4 does not touch its rule logic, but run it anyway: cheap);
`dotnet build "Assembly-CSharp.csproj"` AND `dotnet build "Assembly-CSharp-Editor.csproj"` clean. New suites created here (NS-5 chunk math, NS-1 robustness seed) number independently from B1. Workflow gotchas apply (new-file Unity import before `dotnet build`; menu suites can run stale code — confirm flips after `RequestScriptCompilation` with a fresh `Unity_RunCommand` wave). Behavior-changing phases (CP-3, CP-6) need in-game/fault-injection confirmation before their baselines are trusted.

| Phase                                               | Scope (files)                                                                                                           | Effort | Depends on                         |
|-----------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------|:------:|------------------------------------|
| **CP-1 — Lifecycle observability probes**           | `World.cs`, `ChunkPoolManager.cs`, `ChunkStorageManager.cs` (editor-only diagnostics + debug-screen counters)           |   🟢   | —                                  |
| **CP-2 — WS-1: shift/mask chunk math + NS-5 suite** | `Helpers/ChunkMath.cs`, call-site migration (`WorldData`, `ChunkCoord`, `RegionAddressCodec`, …), new NS-5 suite        |   🟡   | —                                  |
| **CP-3 — Load-arm failure contract + NS-1 seed**    | `World.LoadOrGenerateChunk`, `ChunkStorageManager.LoadChunkAsync`, `ChunkSerializer.Deserialize`, new robustness checks |   🟡   | CP-1 (evidence)                    |
| **CP-4 — Placeholder consolidation**                | `Data/WorldData.cs` (`LoadChunk` retirement, `GetOrCreatePlaceholder`), `World.CheckViewDistance`                       |   🟢   | —                                  |
| **CP-5 — `ChunkUnloadDecision` extraction**         | new `Helpers/ChunkUnloadDecision.cs`; `World.UnloadChunks`; truth-table baselines                                       |   🟡   | CP-1 (counters); LP-3 coordination |
| **CP-6 — Save-on-unload durability fix**            | `World.UnloadChunks` save block; `ChunkStorageManager.SaveChunkAsync` (surface failure)                                 |   🟡   | CP-1 (evidence)                    |
| **CP-7 — Pool sizing + constants unification**      | `ChunkPoolManager.Update`; `VoxelData`/`ChunkMath` constants                                                            |   🟢   | CP-1 (churn data)                  |

**Minimal standalone-value set:** CP-1 + CP-3 (kills the silent-stall class) or CP-2 alone (WS-1 + NS-5, fully independent). **Validation is built alongside, not after** — CP-2/3/5 each ship their suites/baselines in the same commit as the code.

### CP-1 — Lifecycle observability probes (🟢, no behavior change)

- **Scope** (editor/dev `[Conditional]` dual-gate + a few always-on debug-screen counters where the overlay already exists):
    1. Load arm: count faults escaping `LoadChunkAsync` (wrap-and-rethrow with a counter at the await site — pre-CP-3 this documents today's loss), `Deserialize → null` occurrences, and placeholders older than N seconds still `IsLoading && !IsPopulated` (the F1 stuck-state detector — checked in the ~1 s lighting fail-safe scan's loop, which already walks
       `Chunks.Values`).
    2. Unload: per-reason deferral tallies (job / light-flags / strand — F6's observability; the §3.3-perf pinned-trail metric) + unload-per-pass counts, on the debug screen.
    3. Save: fired vs succeeded vs failed `SaveChunkAsync` counts (F5 evidence).
    4. Pool: destroys-per-minute per pool (F4 churn evidence) via `DynamicPool` prune counts.
- **Acceptance:** universal gate + a soak (streaming sprint, teleport, view-distance change, save-heavy exit) with results recorded here (Amended line). CP-3/6/7 read this evidence.
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

- **Scope:** §4.2. Helpers in `ChunkMath` (`public const`/aggressive-inline static int ops); migrate the grep checklist from `WORLD_SCALING_ANALYSIS.md` §3.2/§5 (`FloorToInt(... / 16f)`,
  `/ VoxelData.ChunkWidth`, `% 32`, `ToVoxelOrigin`/`FromVoxelOrigin` internals,
  `WorldData.GetChunkCoordFor`/`GetLocalVoxelPositionInChunk`, `RegionAddressCodec.V2Codec`
  step 1). New editor suite `Minecraft Clone/Dev/Validate Chunk Math` (NS-5 seed): old-idiom equivalence over the positive domain, hand-derived negative-domain + big-coordinate cases, region round-trips. Y stays untouched (no `WorldMinY` — that is Tier A's).
- **Prove-red:** flip one helper (`>> 4` → `/ 16`) → the negative-domain baselines red; restore.
- **Acceptance:** universal gate + NS-5 green + an in-game session on an **existing saved world**
  (region addressing must resolve identical files — any migration prompt or missing chunk is a stop-the-line failure) + new-world generation smoke.
- **Serialization note:** the codec-math migration is byte-identical for all reachable inputs; the defensive V3 version bump is an execution-time decision under the `serialization-migration`
  skill (record the verdict here either way).
- **Doc-sync:** `WORLD_SCALING_ANALYSIS.md` §3.2/§6 (WS-1 → executed, pointer here);
  `PERFORMANCE_IMPROVEMENTS_REPORT.md` WS-1 row status; `CHUNK_LIFECYCLE_PIPELINE.md` untouched.

> **Drift note (2026-07-22, pre-execution):** most of this packet was executed OUT OF BAND —
> **WS-1 shipped independently 2026-07-12** (`WORLD_SCALING_ANALYSIS.md` §3.2 ✅ / §6): the
> `Helpers/ChunkMath` shift/mask helpers exist and are live, `RegionAddressCodec.V2Codec` routes
> through them (step-1 truncation bug gone), the equivalence guard landed in the existing
> **"Chunk Math" suite** (38 baselines at 2026-07-22 — negative-domain sweeps + boundary cases;
> this packet's "new suite" framing is stale), and the **no-V3-bump** decision was taken and
> recorded there (§8 v2 item resolved). Per `VALIDATION_SUITE_COVERAGE_ROADMAP.md` NS-5, the
> outstanding residue is: **the V2/V3 region-codec pins**, a residual call-site audit at HEAD
> (post-2026-07-12 code, e.g. CP-4's alignment assert), and this packet's Amended close-out.

> **Amended (2026-07-22, branch `feat/world-scaling`): CP-2 CLOSED as a residue execution** — WS-1
> itself shipped out of band 2026-07-12 (drift note above); this session executed only the residue.
> Shipped as: **(1) NS-5 V1/V2 region-codec pins** — new partial
> `ChunkMathValidationSuite.RegionCodec.cs` (+6 scenarios, "Chunk Math" 38→44; registry untouched at
> 14 suites): V2 encoder *expected-address* table on both signs + a mixed-sign pair (hand-derived,
> cross-checked against an independent double-precision floor oracle), ±2³¹-adjacent aligned-origin
> pins (`int.MinValue` / `0x7FFFFFF0` ± one chunk; decoder inverse and ×16 reconstruction exact at
> the edge), a two-way inverse property (encoder∘decoder dense chunk sweep AND decoder∘encoder over
> every slot of four regions — the direction the pre-existing WS-2/WS-3 round-trip pins never
> exercised), slot-range + truncation teeth, the V1 decoder legacy pin + V1≠V2 divergence teeth
> (guards the v1→v2 region-repack's reads), and the V1 encoder guard + `ForVersion` dispatch pins
> (v<1 rejected; v2 ≡ current v13; the `allowLegacyEncoder: true` arm deliberately unexercised — it
> `LogError`s by design). **(2) Residual call-site audit at HEAD** (diff `1cb1e5b8..HEAD`, both
> assemblies, scaling-doc §5 patterns): **zero** new truncating/float chunk math on signed
> coordinates. Hit dispositions: CP-4's `GetOrCreatePlaceholder` alignment assert + the WorldOrigin
> suite's alignment oracle (sign-safe `==0`/`!=0` remainder tests) **migrated** to a new sanctioned
> helper `ChunkMath.IsChunkAligned` (with an `IsChunkAligned ⇔ local==0` equivalence check added to
> the VoxelToLocal sweep); justify-in-place for `Migration_v10_to_v11`'s `LEGACY_WORLD_CENTRE`
> consts (frozen migration, positive compile-time), `WorldInfoUtility`'s border ceil-div
> (magnitude, not a coordinate), the worm-spawn `NextFloat(0, ChunkWidth)` offsets, and
> benchmark/legacy flat-index decodes (non-negative indices).
>
> **Decisions taken** (session decision-menu): (1) pins' home = **new partial in the existing
> "Chunk Math" suite** (namespace `Editor.Validation` per the type-shadowing lesson; no registry
> change); (2) audit disposition = **migrate the alignment checks** through `IsChunkAligned`
> (user call — convention purity over justify-in-place). The **no-V3-bump verdict stands as
> recorded** (§8 v2 item 2, resolved 2026-07-12) — reaffirmed, not re-decided; the pins are
> read-only and no on-disk change was made.
>
> **Drift corrections vs this packet:** beyond the drift note — the WS-2/WS-3 sessions had already
> landed two V2 **round-trip identity** pins in the suite, so the true residue was narrower than
> "the V2/V3 pins": *expected-value* pins (round-trip identity is provably blind to a matched
> encoder/decoder bug pair), the ±2³¹ domain, and the V1 legacy contract. "V3 correctness
> assertions" (roadmap phrasing) are moot — no V3 codec exists. The report's "21 scenarios" suite
> count was stale (38 pre-session; corrected there).
>
> **Prove-red done:** one combined flip session — V2Codec steps 2+3 → matched truncating `/`+`%`,
> `V1_CHUNK_WIDTH` 16→8, `IsChunkAligned` mask → wrong constant: **exactly 7/44 red** (the 5 new
> codec pins + the two `IsChunkAligned` adopter scenarios), while the two pre-existing round-trip
> identity pins stayed **green** under the matched truncating pair — demonstrating live the
> blindness the expected-value pins close. Flips restored (`RegionAddressCodec.cs` byte-identical
> to HEAD), rerun green.
>
> **Verification:** both csproj clean (new partial confirmed present in the regenerated editor
> csproj — false-green gate); fresh-DLL timestamp gates before every in-editor run; **Validate All
> 322/322 across all 14 suites** (Chunk Math 44/44). **In-game CONFIRMED (2026-07-22, user-run):**
> existing world (already spanning negative chunk coords) loaded with no migration prompt; existing
> chunks streamed from disk and new chunks generated during free flight — zero errors (console
> verified clean post-session). Region addressing resolved identical files throughout.
>
> **Amended (2026-07-22): post-close code-review pass (same uncommitted change).** Five review
> findings fixed: (1) an **asymmetric** decoder expected-value pin added to the inverse-property
> scenario (all prior decoder inputs were axis-symmetric — a decoder-only X/Z swap would have
> passed the new pin layer and been caught only by the older sampled round-trip pins); (2) the V1
> encoder-guard pin is now **message-bound** ("not permitted" token) so an unrelated
> `InvalidOperationException` cannot satisfy it after the guard is deleted; (3) the
> `IsChunkAligned ⇔ local==0` equivalence extracted from the VoxelToLocal sweep into its own
> scenario `IsChunkAligned == (VoxelToLocal == 0) (sweep)` — a red now names the actual defect
> (**Chunk Math 44→45**, prove-red re-run: mask flip reds exactly the new scenario + the
> WorldOrigin adopter); (4)+ (5) coverage-roadmap drift fixed (stale "no suite exists yet" header;
> "pins remain outstanding" bullet marked superseded). Validate All **323/323**.

### CP-3 — Load-arm failure contract + NS-1 robustness seed (🟡, failure-path behavior change)

- **Scope:** §3.3 Option B — try/catch the `LoadOrGenerateChunk` body (fault → one
  `Debug.LogError` → `IsLoading = false` → return, placeholder retryable); audit
  `RegionFile.LoadChunkData`/`GetRegion` fault modes (contract: throw ⇒ caught above, or catch- internally-and-return-null — pick one and document); return the pooled shell on
  `ReadChunkInternal` partial-parse failure (the `Deserialize` catch gains the return — thread-safe, the data pool is concurrent). NS-1 seed: editor checks feeding `Deserialize` truncated / garbage / wrong-version payloads → `null`, no throw, and (via pool counters) no shell leak.
- **Prove-red:** fault-injection — temporarily make `LoadChunkAsync` throw for one coord (editor hook): pre-fix, the placeholder sticks (`IsLoading` forever, CP-1's stuck-detector fires, and neighbor lighting visibly parks); post-fix, one error logs and the chunk loads on the next boundary crossing. This is the in-game confirmation too (the HF-2 injection precedent).
- **Acceptance:** universal gate + the injection scenario + a normal load soak (zero new errors).
- **Doc-sync:** `CHUNK_LIFECYCLE_PIPELINE.md` §5.1 (load path gains its failure contract) + §2 (`IsLoading` row: now cleared on fault); `INFINITE_WORLD_STORAGE_...md` boundary note. **Serialization:** none (read-path behavior on *invalid* data only).

> **Amended (2026-07-22, branch `feat/world-scaling`): CP-3 SHIPPED; regression-green + suite prove-red + in-game injection/soak confirmed.**
> Shipped as: `World.LoadOrGenerateChunk` catch converted to the §3.3 Option B contract (count → one
> `Debug.LogError` → **identity-guarded** `IsLoading = false` → return; the guard captures the admitted
> placeholder ref and clears only if `TryGetChunk` still returns that instance — a late fault can never
> clear the flag on a successor load; OCE teardown arm keeps its rethrow); `ChunkSerializer.ReadChunkInternal`
> returns the pooled shell + attached sections in its catch (concurrent pools — background-safe);
> `RegionFile.LoadChunkData` generic catch now **rethrows** (corrupt-shape branches keep returning null);
> `ChunkStorageManager.GetRegion` evicts a faulted `Lazy` (value-conditional `ICollection.Remove`) before
> rethrowing; permanent dev-only `InjectLoadFaults(int)` seam beside the CP-6 save seams; `ChunkPoolManager`
> gains `ActiveData`/`ActiveSections`. New suite `Minecraft Clone/Dev/Validate Deserialization Robustness`
> (NS-1 seed, B1–B7; registry `ExpectedSuiteCount` 13→14).
>
> **Decisions taken** (session decision-menu): (1) NS-1 home = **new suite** (roadmap identity; parts 1–5
> grow there); (2) injection seam = **permanent dev-only** (CP-6 precedent); (3) `LoadChunkData` contract =
> **split null/throw** — null strictly means "not on disk / corrupt-shape → regenerate by design", unexpected
> faults throw → retry (a transient I/O fault must never regenerate over saved data) + the `Lazy` eviction;
> (4) **rider**: `RegionFile.SaveChunkData` generic catch rethrows — a failed disk write previously reported
> `Written` to CP-6's registry (silent loss its suite could not see; the CP-6 seam injects before this call).
> The deterministic "chunk too big" early return stays a logged non-throw (accepted limitation).
>
> **Drift corrections vs this doc's F1 text:** `LoadChunkData` I/O faults did NOT propagate — its catch-all
> returned null, so a transient read fault took the regenerate-and-overwrite path; the only escaping fault
> was region OPEN (`Lazy` ctor), which the default `Lazy` mode then cached for the session (permanent
> per-region failure — would have defeated Option B's retry). **Latent bug found by the suite:**
> `ChunkSerializer.Serialize` with `CompressionAlgorithm.None` threw `ObjectDisposedException` (the factory
> returns the MemoryStream itself and the `using` closed it before the Position read) — a world saved with
> compression None could not save at all; fixed with `Deserialize`'s identity-guard dispose idiom.
>
> **Prove-red done:** (a) suite-first — pre-fix run reds exactly B2 (shell leak, data 0→1) and B5 (shell +
> attached sections, data 0→1 / sections 0→2), 5/7 green; post-fix 7/7. (b) In-game pre-fix injection —
> `InjectLoadFaults(1)` + fly: `LoadArmFaults 0→1`, `StuckLoading 0→1` held across scans, placeholder
> `(384,-2464)` permanently `IsLoading && !IsPopulated`, no self-recovery (re-confirms CP-1's F1 evidence
> live). **Post-fix in-game:** same injection → exactly one contract error log, `StuckLoading 0`, the faulted
> chunk re-enqueued on the next boundary crossing and loaded (`IsPopulated=true`). Soak over existing
> terrain: zero non-injected errors, `Deserialize 0`, `Saves 305/305/0`, retry-pending 0.
>
> **Verification:** both csproj clean; fresh DLLs; **Validate All 315/315 across all 14 suites** (Lighting
> 88/88, Meshing 23/23, Mesh Build Queue 9/9, Save Durability 12/12 — the save rider preserved CP-6 green).
>
> **Amended (2026-07-22): post-ship code-review hardening (same uncommitted change).** Review findings
> fixed: (1) **too-large chunk writes** are now the typed `ChunkTooLargeException` → `FailedPermanent` on
> all three save paths (the rider's accepted "too big stays a logged false-`Written`" limitation is
> CLOSED; `InjectTooLargeSaves` seam + Save Durability **B13**, prove-red exact — mutating the seam to a
> plain `IOException` reds only B13); (2) **`RegionFile` partial-init leak** — `Initialize` disposes its
> `FileStream` before rethrowing, so a transient open fault can't leak the exclusive handle and turn every
> eviction-retry re-open into a sharing violation; (3) **pool-ABA window** in the fault path's identity
> guard closed via `ChunkData.LifecycleEpoch` (bumped every `Reset`; the wrapper clears `IsLoading` only
> on same-instance + same-epoch); (4) **migration transient reads** — `MigrationManager` wraps
> `LoadChunkData` in a 3-attempt bounded retry so an AV-scan hiccup doesn't drop a healthy chunk as
> "corrupted"; (5) suites share one `Framework/StorageValidationFixture` (single seam-disarm list).
> Dropped by decision: startup-fault retry hardening (verified: the first `Update()`'s sentinel-driven
> `CheckViewDistance` already re-enqueues a faulted startup chunk on frame 1 — only the bounded, loud
> safety-break wait remains). Suite counts: Save Durability B1–B13, Validate All 316 baselines.
>
> **Amended (2026-07-22): second review round (same uncommitted change).** (1) The **mid-await unload
> guard** in `LoadOrGenerateChunkInner` gains the same `LifecycleEpoch` term as the wrapper's fault path
> (a pool-ABA recycle across the disk-read await now takes the return-shell arm instead of hydrating a
> successor lifecycle); pipeline doc §5.1 updated to instance+epoch. (2) **v1→v2 repack per-chunk fault
> isolation ADDED — supersedes round 1's "dropped" decision** (user call, prompted by the too-large throw
> extending the abort surface to the write site): the repack loop now skips a faulting chunk like
> `MigrationManager`'s corrupted-chunk path. Error-handling only — the byte transform is untouched, which
> is the `serialization-safety` rule's actual subject. (3) The four dev-only injection seams share one
> `TryConsumeInjection` decrement-and-clamp core (the CP-6 `CompareExchange` race fix now lives in one
> place). (4) New source files staged (`ChunkTooLargeException.cs`, `StorageValidationFixture.cs` + metas).

### CP-4 — Placeholder consolidation + `WorldData.LoadChunk` retirement (🟢)

- **Scope:** one `WorldData.GetOrCreatePlaceholder(Vector2Int)` used by all three sites (F2); delete `LoadChunk`'s commented legacy block + stale TODOs and fold its callers (`RequestChunk(create:true)`) onto the new method; document (XML) the resurrect semantics of
  `RequestChunk(create:true)` — or, if the executor confirms `ProcessGenerationJobs` is its only
  `true` caller and job-pinning makes resurrection unreachable, add an editor-only assert that the chunk already exists (making the convention structural). Zero intended behavior change.
- **Acceptance:** universal gate + streaming/load smoke. **Doc-sync:** pipeline doc §5.1 flowchart (placeholder step names the single site). **Serialization:** none.

> **Amended (2026-07-22, branch `feat/world-scaling`): CP-4 SHIPPED; zero behavior change.**
> Shipped as: `WorldData.GetOrCreatePlaceholder(Vector2Int)` (TryGet → else pool `GetChunkData` +
> `SetChunk`; creation stays on the pool Get path so `Reset` runs and `LifecycleEpoch` bumps; sets no
> `IsLoading`, enqueues nothing — admission owns both, P-4 §3.1) now used by all three F2 sites:
> `CheckViewDistance`'s spiral, `RequestChunk(create:true)`'s create arm, and the startup placeholder
> loop. `WorldData.LoadChunk` deleted (commented legacy save block + both contradictory TODOs with it).
>
> **Decisions taken** (session decision-menu): (1) `create:true` = **XML doc + two site asserts** — the
> packet's "only caller" precondition is FALSE (three callers: `ProcessGenerationJobs`, job-pinned;
> `Chunk` visual re-link, guaranteed by the same-call spiral — `ChunkPool.Get` has exactly one call
> site; `GetHighestVoxel`, which genuinely relies on creation for the spawn probe), so the resurrect
> semantics are documented on `RequestChunk` and the two unreachable sites carry `#if UNITY_EDITOR`
> `Debug.Assert`s making the conventions structural. (2) **Absorb + retire**: `GetOrCreatePlaceholder`
> absorbed `EnsureChunkOriginExists`, and BOTH `EnsureChunkExists` overloads were deleted — the
> `Vector3` overload had zero callers, the `Vector3Int` overload one caller (startup) that discarded
> the bool and round-tripped an exact chunk origin through `Vector3Int(x, 0, y)` (y-bounds check
> vacuous, origin→origin conversion identity) — startup now calls `GetOrCreatePlaceholder` directly.
>
> **Rider observation (pre-existing, unchanged):** `GetHighestVoxel`'s `create:true` makes a freshly
> created *empty* placeholder satisfy its `chunkData != null` branch, shadowing the accurate
> "expensive world generation" fallback for uncreated chunks. Out of CP-4's behavior-preserving scope.
>
> **Verification:** both csproj clean; fresh DLLs; **Validate All 316/316 across all 14 suites**.
> In-game smoke confirmed (2026-07-22): fly-through generating new chunks, then backtrack reloading
> existing ones — zero errors/warnings, neither site assert fired, no visual anomalies.
>
> **Post-ship code-review hardening (same day, same uncommitted change):** (1) editor-only
> **alignment assert** in `GetOrCreatePlaceholder` — the retired `EnsureChunkExists` overloads
> normalized arbitrary positions via `GetChunkCoordFor`, the new sole creation entry does not, so a
> misaligned key would register an unfindable phantom chunk; docstring states the origin-aligned
> precondition. (2) `RequestChunk` restructured to one dictionary lookup per outcome (create arm
> delegates directly). (3) Doc closures: `CODEBASE_IMPROVEMENTS.md` §2.2 marked ✅ RESOLVED by CP-4;
> storage doc §10.4 narrative gains a "(since retired — now `GetOrCreatePlaceholder`)" note.
> (4) Second smoke on an **existing** save (startup disk-hit arm — the first smoke's new-world run
> only exercised the generate arm at startup).

- **Scope:** §4.1 — pure decision + `UnloadChunks` routes through it (behavior-identical arms, including the exact pin set and the 8-neighbor strand rule); truth-table baselines (new lifecycle-suite partial or NS-5's file — executor picks the home and states it) covering every arm incl. the §9.6 stranding cases; CP-1's counters keyed by the enum.
- **Coordination:** LP-3 (lighting doc) removes `IsAwaitingMainThreadProcess` — its gate term here shrinks `DeferLightPending`; land in either order, the truth table updates with it. P-4 rec 3 later adds its persist-and-unload arm *here*, baselined.
- **Prove-red:** invert the strand-check term → truth-table baselines red (and only those).
- **Acceptance:** universal gate + an unload-heavy in-game soak (sprint + return; no stranded chunks, deferral counters sane, memory flat-ish).
- **Doc-sync:** pipeline doc §9.6 (points at the decision + its baselines as the now-testable guard). **Serialization:** none.

> **Amended (2026-07-21, branch `feat/world-scaling`): CP-5 + P-4 rec 3 SHIPPED together; regression-green + in-game confirmed.**
> Shipped as two bisectable commits (uncommitted at time of writing): (1) CP-5 behavior-identical extraction —
> `Helpers/ChunkUnloadDecision.cs` (`Evaluate(in ChunkUnloadFacts)`, precedence job→light→strand→unload) +
> `UnloadChunks` routed through it, CP-1 tallies keyed off the enum; new suite
> `Minecraft Clone/Dev/Validate Chunk Unload Decision` (registry `ExpectedSuiteCount` 11→12). (2) P-4 rec 3 —
> the strand fact narrowed to **in-range** neighbors (`!IsBeyondUnloadDistance`), new `UnloadPersistLightPending`
> arm (force `NeedsInitialLighting=true` → persist columns → unload), precedence reordered to
> job→in-range-strand→persist-light→unload, `UnloadedLightPersisted` HUD counter.
>
> **Decisions taken** (session decision-menu): (1) *in-range-only* strand defer (out-of-range strand neighbors no
> longer block — the trail-drain lever); (2) persist via *force `NeedsInitialLighting`* (correct-by-construction
> re-light on reload, captured by the synchronous `SaveChunkAsync` snapshot — no new persistence machinery, no
> migration); (3) truth-table home = a *new* suite (matches ChunkMath/LightWorkScheduler precedent). `DeferLightPending`
> is retained in the enum but no longer returned for out-of-range chunks (its per-pass count now reads ~0 — the
> drained signal), superseding this doc's "LP-3 may shrink `DeferLightPending`" note (LP-3 will now shrink the
> *fact term* `ProcessingLight`, not the arm).
>
> **Prove-red done** (manual, both commits): (a) invert the strand term in `Evaluate` → in commit 1 reds *only*
> the §9.6 witnesses B4/B5 (+ their B8 rows); (b) remove the `UnloadPersistLightPending` arm → reds *only* the
> persist witnesses B3/B9 (+ B8 `1010`). Suite 9/9 after revert.
>
> **Verification:** `dotnet build` both csproj clean; universal gate green against fresh DLLs — **Chunk Unload
> Decision 9/9, Lighting 88/88, Meshing 23/23, Mesh Build Queue 9/9** (Mesh Build Queue B7 zero-alloc inconclusive
> on editor Mono, as always).
>
> **In-game soak re-measure (mirrors the CP-1 §7 soak entry).** Fly-out then stationary. Read-only `World.Instance`
> classification walk, before (CP-1) → after: **totalLoaded 1096 → 363; beyond-unload *unreclaimable* 743 → ~0–2;
> `Deferred — light` 308 → 0; `Deferred — strand` 395 → 0–2.** The pinned trail (§3.3 / F6) is drained. The only
> residual is a **bounded boundary shell**: an out-of-range chunk (Chebyshev dist 11, boundary=10) strand-deferred
> by a single *buffer-band* neighbor (dist 10, kept in-range) that is stuck `NeedsInitialLighting` because *its*
> outer neighbors are gone — correct §9.6 behavior, perimeter-bounded (not the old area trail), and self-resolving
> the instant the player moves it past the boundary (confirmed: strand returns to 0). No visual artifacts; no
> stranded/black chunks; `[LIGHTING RESCUE]` logs confirm the persist path fires. **Durability:** edit a block →
> fly past unload (unloaded via the new arm) → return → the edit and its lighting are correct (force-relight +
> normal modified-chunk save both hold). CP-5 is DONE; P-4 rec 3 §3 is Implemented (see perf-analysis doc).

### CP-6 — Save-on-unload durability (🟡, failure-path behavior change)

- **Scope:** §4.3 — `SaveChunkAsync` surfaces success/failure; `ModifiedChunks.Remove` moves to the success path; failure re-queues (coord-keyed retry drained by a later `UnloadChunks` pass or next session via the existing modified tracking — executor picks the minimal correct shape given the pooled-data lifetime, which is the subtle part: the `ChunkData` is recycled immediately, so the retry must not hold the reference). OM-3 (task flood) explicitly untouched.
- **Prove-red / verification:** fault-injection (make one save throw): pre-fix the edit is silently gone on reload; post-fix it survives (retry or re-mark). In-game: edit → walk away (unload) → return → edits present; plus the injection run. CP-1's save counters confirm.
- **Doc-sync:** pipeline doc unload step; `INFINITE_WORLD_STORAGE_...md` save-boundary note. **Serialization:** none (no format change — failure-path bookkeeping only).

> **Amended (2026-07-22, branch `feat/world-scaling`): CP-6 SHIPPED; regression-green + suite prove-red done; in-game injection/durability run pending.**
> Shipped as: `Serialization/ChunkSaveResult.cs` (new — `Written`/`Canceled`/`Failed`);
> `ChunkStorageManager.SaveChunkAsync` → `Task<ChunkSaveResult>` (generic catch surfaces `Failed`; the
> previously-silent `length<=0` async skip now logs + counts as `Failed`; `Canceled` never retries) + a
> coord-keyed **failed-save retry registry** owned by the storage manager (thread-safe staging →
> main-thread map; newer failure supersedes older, superseded snapshot back to pool): per-frame
> `DrainFailedSaveRetries` from `World.Update` (one due entry/frame, backoff 1→30 s, entries never dropped
> mid-session), `LoadChunkAsync` reload guard (sync-flush the coord before reading disk — closes the
> stale-reload race), `FlushFailedSavesSync` in the synchronous `SaveAllModifiedChunks` arm (quit /
> force-unload, before `Dispose`); dev-only `InjectSaveFaults` seam; HUD `retry-pending` readout beside the
> CP-1 save counters. New suite `Minecraft Clone/Dev/Validate Save Durability` (B1–B8; registry
> `ExpectedSuiteCount` 12→13).
>
> **Decisions taken** (session decision-menu): (1) retry shape = **retained-snapshot registry** — §9 Q4
> answered: a coord-only retry list cannot work, the recycled `ChunkData` leaves the edits alive *only* in
> the serialization snapshot, so the snapshot's ownership transfers to the registry; consequently
> `ModifiedChunks.Remove` **stays at fire time** (§4.3's "remove in a success continuation" is unsound for
> the unload site — a recycled ref must never linger in the `HashSet<ChunkData>`); (2) reload guard = yes;
> (3) second fire site: the registry mechanism covers `SaveModifiedChunks`' async arm automatically, sync
> arm gains only the final flush; (4) validation = permanent seam + suite. **Drift corrections:** §4.3's
> "retry list drained by a later `UnloadChunks` pass" was unworkable — `UnloadChunks` runs only on
> chunk-boundary crossings, so the drain is per-frame in `Update`.
>
> **Prove-red done (mechanical):** routing the `Failed` arm's snapshot back to the pool (pre-fix shape)
> reds exactly B2–B7 ("chunk missing on disk" = the silently-lost edit); B1/B8 stay green. Reverted.
> **Verification:** both csproj clean; Save Durability 8/8 green against fresh DLLs; **Validate All
> 304/304 across all 13 suites** (Lighting 88/88, Meshing 23/23, Mesh Build Queue 9/9 — B7 zero-alloc
> inconclusive on editor Mono, as always). **In-game F5 injection + durability run CONFIRMED
> (2026-07-22):** armed `InjectSaveFaults(1)` + programmatic Stone edit in a nearby chunk → fly-out
> unloaded the area; the fault hit an unload save (`SavesFailed 0→1`), and the registry recovered it via
> the `World.Update` drain (`[SaveRetry] Recovered … after 1 attempt(s)`); the edited chunk unloaded,
> saved normally, and on return the edit was present. Counters reconciled across the whole soak:
> **Fired 648 / Completed 648 / Failed 1 / retry-pending 0** — every fired save reached disk despite the
> failure (pre-CP-6, the faulted chunk's data would not exist on disk). Normal save/exit smoke green (clean quit, no console errors).
>
> **Amended (2026-07-22): post-ship code-review hardening (same uncommitted change).** Review findings
> fixed: (1) **quit-canceled saves stage their snapshot** into the registry (closes the
> manual-save-then-quit hole — `SaveAllModifiedChunks(false)` Clears membership up-front, the shutdown
> token cancels the in-flight writes, and pre-fix nothing recovered them; the quit flush now writes staged
> canceled snapshots synchronously; residual: a continuation that hasn't staged before the flush runs is
> still lost — the 100 ms cancellation-propagation sleep covers the practical window); (2) **zero-length
> serialization → `FailedPermanent`** (deterministic — released loudly, never enters the retry loop; new
> `InjectZeroLengthSerializes` seam + B9); (3) **flush retains retryable entries** (force-unload no longer
> drops recoverable edits; moot at real quit); (4) **single shared write core `WriteToRegion`** hosting
> the fault seam (sync/async/retry paths can no longer drift; sync quit saves now injectable too);
> (5) injection-counter clamp race fixed (`CompareExchange`). Dropped by decision: in-flight-save reload
> window (comment + limitation only), main-thread retry hitch (accepted), startup drain dead window
> (unreachable). Suite now **B1–B9**; B8 rewritten to the canceled-staging contract; prove-red re-run —
> mutating the disposition (un-stage Canceled + stage FailedPermanent) reds exactly B8/B9.
>
> **Amended (2026-07-22): second review round (post-commit `ca171b7`).** Two quit-flush ordering bugs
> found and fixed: (1) the `ModifiedChunks.Count == 0` early return in `SaveAllModifiedChunks` skipped
> `FlushFailedSavesSync` entirely — a quit with no currently-modified chunk silently lost every pending
> registry entry (the F5 hole re-opened through the guard clause); (2) the flush ran AFTER the per-chunk
> sync saves, so a stale registry snapshot could overwrite the newer just-synced bytes for the same
> coord. Both fixed by one restructure: flush FIRST, outside the early return (snapshots ≤ live data in
> freshness ⇒ newest bytes always land last). Plus: (3) **supersede rule** — every successful write
> (sync + async) stages a supersede op into a single ordered staging queue (`StagingOp`), dropping any
> OLDER pending entry for its coord when drained (a stale snapshot must never regress newer data; FIFO
> protects a newer failure) — new **B10** with two-phase FIFO witness; (4) `Dispose` makes a final write
> attempt per remaining registry entry (world-switch swap can't silently discard retained edits;
> `World.Instance`-dead OnDestroy fallback drops loudly) — new **B11** (manager-swap round-trip);
> (5) `ListPool` for the save-list snapshot. Suite **B1–B11**; prove-red — disabling the supersede
> enqueue reds exactly B10 (its failure message IS the guarded regression: stale v1 replayed over v2).
>
> **Amended (2026-07-22): third review round (same uncommitted change).** (1) Supersede decisions moved
> from staging-queue order to a **monotonic data-freshness sequence stamped at capture time** (snapshot
> creation / sync serialize) — closes the overlapping-same-coord-saves inversion where an OLDER save
> failing slower than a NEWER save's completion could resurrect stale data (narrow, not directly
> baselinable without private access; B10 witnesses the seq path); (2) **sync `SaveChunk` failures now
> snapshot-and-stage** (force-unload live path recoverable via drain; quit gets the Dispose attempt) —
> new **B12**, prove-red exact (disable staging → only B12 red); (3) Dispose flush is bounded multi-pass
> (post-flush stragglers get a real write attempt); (4) `ListPool` + try/finally on both pooled lists.
> Suite **B1–B12**.

### CP-7 — Pool sizing + constants unification (🟢)

- **Scope:** (a) resolve F4 with CP-1's churn data: either the formula becomes the commented area (pool covers the active set + buffer) or the width is confirmed as the intended spare-size and the comment is rewritten to say so — decide on measurement, not taste; (b) unify
  `VoxelData.ChunkHeight`/`ChunkMath.CHUNK_HEIGHT` (+ width twins) to a single declaration site (the other becomes an alias or is removed via `refactor-safely`) — the scaling doc §5 prerequisite, mechanical, zero behavior.
- **Acceptance:** universal gate + a teleport/view-distance-change session with CP-1's churn counters before/after (if the formula changed, churn must drop; memory ceiling must not grow past the buffer intent). **Doc-sync:** scaling doc §5 checklist tick (constants); pipeline doc untouched. **Serialization:** none.

> **Amended (2026-07-23, branch `feat/world-scaling`): CP-7 SHIPPED — ARC COMPLETE; regression-green + measured BEFORE/AFTER sessions (Unity-MCP-driven).**
> Shipped as two code changes: **(1) F8 unification** — `ChunkMath.CHUNK_WIDTH`/`CHUNK_HEIGHT` become
> const aliases of `VoxelData.ChunkWidth`/`ChunkHeight` (header comment rewritten to the alias truth;
> a const-to-const reference compiles to an IL literal, so Burst compatibility is unchanged — the old
> "self-contained for Burst" claim was already moot: jobs reference `VoxelData.*` directly, e.g.
> MeshGenerationJob 37×). Frozen migration literals (`Migration_v10/v12` steps, `V1_CHUNK_WIDTH`)
> deliberately untouched (frozen-DTO rule). **(2) F4 pool sizing** — `ChunkPoolManager.Update` derives
> per-pool idle caps: row-budget **soft caps** (`SPARE_ROWS_VISUAL=2` × view row for chunk/border/
> visualizer — those pools recycle within the same `CheckViewDistance` pass; `SPARE_ROWS_DATA=4` ×
> unload-boundary row for data — async generation lag; sections = data × derived `SECTIONS_PER_CHUNK`,
> killing the hardcoded `* 8`), service-area **hard caps** (always enforced — idle can never exceed
> what the current active set could need), and a `LINGER_SECONDS=90` **no-demand window**: a surplus
> above soft cap is destroyed only after 90 s in which no `Get` consumed idle (demand detected in
> `EffectiveCap` as an idle-count drop exceeding the destroy delta); `POOL_BUFFER_PERCENTAGE` retired.
>
> **Decisions taken** (session decision-menu): (1) F4 = **row caps + linger** — the packet's
> formula-vs-comment binary was a false choice: the user confirmed the width formula was a DELIBERATE
> revert of an earlier area implementation (1000+ idle retained at vd 5), so the comment was the stale
> abandoned intent, and measurement showed width was mis-radiused (view row for pools serving the
> unload boundary); (2) unification direction = **VoxelData holds the literals**, ChunkMath aliases
> (historical origin, majority of references); (3) the initially-approved *generation-busy prune gate*
> was **replaced mid-execution by the linger window**: the measured worst destroy-recreate (vd 10→5→10
> flip: 306 GameObjects + 200 data) happened at `genJobs==0` where a busy-gate is blind, and the
> expiry test showed a surplus-began timer unprotects fresh returns joining an old surplus →
> demand-reset semantics.
>
> **Measurement (world "Test CP-4" v13, vd 10; caps then chunk 27 / data 54 / sect 216).**
> BEFORE: far teleport **~675 data destroyed** then re-allocated (grind to cap at 20/s over ~35 s,
> repeatable); terrain-density delta **~1050 sections (~25 MB)**; vd 10→5→10 flip **306 chunk
> GameObjects + 200 data** destroyed and re-created within 40 s (sections escaped only because their
> 100 s grind outlasted the flip — 1596 reused); paced flight (2-chunk hops) **0**; single 6-chunk
> fast burst **+61** (the CP-1 soak's flight churn, reproduced). AFTER: teleports, flight, and burst
> all **0 destroys** — the ~729-object data buffer recycles across teleports (idle dips to 0 at each
> teleport's placeholder wave, refills from the trailing returns); vd flip-up reused 288 data + 1519
> sections with **zero fresh data allocs**; vd-shrink reclaim enforced immediately by the smaller
> hard cap (bounded at area (vd)); linger expiry proven — 729 surplus → soft cap **124** (= 4 × 31)
> after ~110 s stationary, +605 destroys, reclaim intact; post-expiry teleport re-protected (destroys
> flat). Zero console errors in all sessions.
>
> **Drift corrections vs this doc:** F4's `CPM:103–116` anchor → `Update` at CPM:113+ pre-change; the
> "~31 vs ~781 at view distance 12" example was accurate; the CP-1 soak churn numbers predate CP-5's
> trail-drain — post-CP-5 steady flight barely churns, the dominant churn is teleport/flip transients.
> **New pipeline observation (out of CP-7 scope; recorded for P-4/OM-*):** teleport reuse of
> `ChunkData` is structurally defeated by intra-frame ordering — `CheckViewDistance` creates the new
> area's placeholders BEFORE `UnloadChunks` returns the old area in the same crossing pass, so the
> data pool doubles transiently (~729 fresh allocs) on the FIRST far teleport regardless of cap
> policy; the retained buffer converts this into steady-state recycling from the second teleport on.
>
> **Verification:** both csproj clean; fresh-DLL timestamp gates before every in-editor run (the
> unification's editor-assembly skip is expected — identical const values leave the API hash and the
> inlined literals unchanged); **Validate All 323/323 across all 14 suites** after the unification,
> after the pool-sizing change, and after the linger refinement. No new suite (pool sizing has no
> harness — the measured sessions above are the packet's acceptance evidence).
>
> **Amended (2026-07-23): post-close code-review pass (same uncommitted change).** Five review
> findings, all fixed: (1)+ (2)+ (3) the demand *inference* (per-frame idle-count delta minus destroy
> delta) was replaced by an **exact demand signal** — both pools now export cumulative `TotalGets`
> (plain increment on the main-thread `DynamicPool`; inside the existing lock on
> `ConcurrentDynamicPool`, `Interlocked.Read` getter) and `EffectiveCap` reduces to
> *time-since-last-demand* (`TotalGets` advanced → stamp `LastDemandTime`; cap = expired ? soft :
> hard). This closes the balanced-flow blind spot (same-frame Gets+Returns netting zero hid demand —
> a >90 s sustained flight could prune a hot pool), kills the double locked `PooledCount` read, and
> deletes `SurplusSince`/`PrevPooled`/`PrevDestroyed`; the disabled border/visualizer arms now
> **always run `EffectiveCap`** for bookkeeping and only override the final cap to 0, so state can
> no longer go stale across a disable window. (4) `ChunkMath.SECTIONS_PER_CHUNK` is now the single
> public derivation (CHUNK_VOLUME re-derived from it); the 6 pre-existing local
> `sectionCount = VoxelData.ChunkHeight / ChunkMath.SECTION_SIZE` consts (`ChunkData`, `Chunk`,
> `JobData`, `IsolatedJobProbe`, `EditorChunkPipelineRunner`, `MeshingTestWorld`) alias it, and
> `ChunkPoolManager`'s private copy is deleted — one grep target for Tier A. (5) the two 4-line
> inline comment blocks trimmed to ≤3 lines per the style rule. Re-verified: both csproj clean,
> **Validate All 323/323**, in-game session — teleport protected (0 destroys, 714 idle retained),
> 120 s no-demand expiry reclaims to soft cap 124 exactly (+590), post-expiry teleport re-protected
> (destroys flat — the exact scenario the surplus-began timer failed), zero console errors.
>
> **Amended (2026-07-23): second review round (same uncommitted change).** Five findings, three
> fixed, two dropped by decision: (1) **`Helpers/PoolPruneDecision` extraction** — the linger policy
> is now the pure public `Evaluate(soft, hard, totalGets, lingerSeconds, now, ref State)` (time a
> parameter, no clock mocking), `ChunkPoolManager` delegates passing `Time.time`; new suite
> **`Minecraft Clone/Dev/Validate Pool Prune Decision`** (B1–B5 truth table: window-active hard,
> demand restamps, strict-boundary expiry → soft, post-expiry demand re-protects, fresh-state
> expiry; registry `ExpectedSuiteCount` 14→15) — prove-red exact: dropping the `PrevGets` update
> reds only B3/B4. (2) **Section/Height Coupling Guard** added beside the pow2 guard (Chunk Math
> 45→46): `CHUNK_HEIGHT % SECTION_SIZE == 0` + `SECTIONS_PER_CHUNK` reconstruction +
> `CHUNK_VOLUME` pin — prove-red exact: a simulated Tier A `CHUNK_HEIGHT = 120` reds only the new
> guard (1 of 46). (3) `unloadRow` now derives from **`Settings.LoadDistance`** (the single source)
> instead of re-composing `viewDistance + DATA_LOAD_BUFFER` inline. Dropped as deliberate, recorded
> in the linger comment: any demand retains the whole surplus (bounded by the hard cap — "demand =
> keep"), and the window runs on **scaled game time**, freezing coherently with the pools' prune
> timers while paused. **Validate All 329/329 across all 15 suites.**

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

1. **CP-1 probe results** — stuck-`IsLoading` occurrences, save-failure rate, deferral-reason distribution, pool churn. Gates CP-3/6/7 scoping; answers land as Amended lines.
2. **CP-2 codec version bump** — defensive V3 bump vs. documented no-bump (byte-identical for all reachable inputs). Decide with the `serialization-migration` skill at execution.
3. **F4 intent** — was the width-sized spare pool deliberate? Measurement (CP-1) plus a look at
   `DynamicPool.UpdatePruning`'s exact semantics answers it; CP-7 records the verdict. ✅ **ANSWERED (2026-07-23, CP-7 Amended block):** yes and no — the width formula WAS deliberate (a revert of an earlier area implementation that retained 1000+ idle objects at vd 5), but the comment was that abandoned implementation's derivation left behind, and the width sizing itself was mis-radiused (view row for pools serving the unload boundary). `UpdatePruning` caps the IDLE count only (1 destroy / 0.05 s while over cap), so neither width nor area expressed the
   real need — a transient-burst buffer. Verdict: measurement-derived per-pool row soft caps + area hard caps + 90 s no-demand linger window.
4. **CP-6 retry shape** — coord-keyed retry list vs. success-continuation-only, constrained by the immediate pool recycle of the saved `ChunkData`. ✅ **ANSWERED (2026-07-22, CP-6 Amended block):**
   retained-snapshot registry — the recycled `ChunkData` leaves the edits alive only in the serialization snapshot, so a coord-only list has nothing to save and a success-continuation cannot re-mark a recycled ref; ownership of the snapshot transfers to the storage-manager registry instead.

---

## Document History

* **v1.0** - Initial design (outer-lifecycle census + F1–F9 findings + CP-1…CP-7 phased plan at `0a12036`)
* **v1.1** - Added F10 (generation fault-path double-release; surfaced by the 2026-07-10 branch code review of `feat/async-lighting-validation-suite`)
* **v1.2** - CP-5 (`ChunkUnloadDecision` extraction) + P-4 rec 3 (persist-and-unload the pinned trail) SHIPPED on `feat/world-scaling`; regression-green, prove-red + in-game soak/durability confirmed (Amended block in §7 CP-5)
* **v1.3** - CP-6 (save-on-unload durability, F5) SHIPPED on `feat/world-scaling`: `ChunkSaveResult` contract + failed-save retry registry + reload guard + quit flush + `Validate Save Durability` suite (B1–B8); §9 Q4 answered (Amended block in §7 CP-6)
* **v1.4** - CP-3 (load-arm failure contract, F1) SHIPPED on `feat/world-scaling`: §3.3 Option B on the CP-1 wrapper seam + pooled-shell return + split null/throw storage contract (`Lazy` eviction, `SaveChunkData` rider) + `Validate Deserialization Robustness` suite (NS-1 seed, B1–B7); F1 drift corrected (Amended block in §7 CP-3)
* **v1.5** - CP-2 CLOSED as residue execution (WS-1 shipped out of band 2026-07-12): NS-5 V1/V2 region-codec pins (`.RegionCodec.cs` partial, Chunk Math 38→44) + call-site audit at HEAD (zero migration-required hits; alignment checks → new `ChunkMath.IsChunkAligned`) + no-V3-bump reaffirmed; F3 closed (Amended block in §7 CP-2)
* **v1.6** - CP-7 SHIPPED — **ARC COMPLETE**: F8 constants unified (ChunkMath aliases VoxelData) + F4 pool sizing replaced by measured row soft caps / area hard caps / 90 s no-demand linger window (`EffectiveCap`); §9 Q3 answered; BEFORE/AFTER Unity-MCP measurement sessions recorded; new pipeline observation logged (teleport placeholder-before-unload ordering defeats first-teleport pool reuse — P-4/OM-* candidate); post-close review pass — exact `TotalGets` demand signal on both pools (closes the balanced-flow blind spot + disabled-pool stale state) +
  public `ChunkMath.SECTIONS_PER_CHUNK` adopted at all 7 derivation sites; second round — `Helpers/PoolPruneDecision` extraction + new `Validate Pool Prune Decision` suite (B1–B5, registry 15) + Section/Height Coupling Guard (Chunk Math 46) + `unloadRow` from `Settings.LoadDistance`; Validate All 329/329 across 15 suites (Amended blocks in §7 CP-7)

---

**Last Updated:** 2026-07-23 **Next Review:** when Tier A/B or palette work is scheduled (re-check §5 constraints), or when P-4/OM-* picks up the teleport placeholder-before-unload ordering observation (§7 CP-7 Amended block)
