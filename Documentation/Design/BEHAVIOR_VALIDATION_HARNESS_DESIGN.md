# Behavior (Tick) Validation Harness — Draft Implementation Design

**Status:** 🟢 **Waves 0–2 complete — the entire fluid + grass behavior surface is baselined & green (2026-06-21).**
The harness exists and runs: menu item **`Minecraft Clone/Dev/Validate Behavior`**, sources under
`Assets/Editor/Validation/Behavior/` (`Framework/{TestBehaviorBlockPalette,BehaviorTestWorld,BehaviorSnapshot}.cs`,
`BehaviorValidationSuite{,.Baseline}.cs`). 8 baselines green: **Smoke** (rig), **BH-B1**
(water spread), **BH-B4** (decay + termination), **BH-B2** (1-block cliff → gravity + waterfall reset),
**BH-B3** (infinite-source regeneration), **BH-B5** (lava viscosity staggering — TG-3 seeded-RNG gate #1),
**BH-B6** (grass spread to convertible dirt — TG-3 seeded-RNG gate #2: reservoir sampling + 2% roll; in-game
confirmed), and **BH-B7** (grass→dirt under a solid block — deterministic). BH-7 (apply-path fidelity) closed as
accepted defensive parity. Remaining: **(a)** promote this doc to `Documentation/Architecture/Testing Framework/`
(now due — Waves 1–2 have landed); **(b)** **BH-D1**, the old-vs-new differential — the load-bearing TG-4/TG-5
parity test, built in that PR since it needs both code paths to exist.
**Created:** 2026-06-20
**Author intent:** the parity guard that lets the **TG-4** (per-behavior native collections) and **TG-5**
(Burst function-pointer dispatch) optimizations in
[PERFORMANCE_IMPROVEMENTS_REPORT.md](PERFORMANCE_IMPROVEMENTS_REPORT.md) claim *behavior-preserving* — the
same role the Meshing suite plays for `MR-*` and the Lighting suite plays for the lighting engine.
**Proposed scope:** `Assets/Editor/Validation/Behavior/` — a new `BehaviorValidationSuite` +
`BehaviorTestWorld` + `BehaviorOracle`/`BehaviorAssert` + `TestBehaviorBlockPalette` harness, menu item
**`Minecraft Clone/Dev/Validate Behavior`**.
**Siblings (same document shape & conventions):**
[MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md),
[LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md).

---

## 1. Why this document exists

TG-4 and TG-5 re-architect the **block-behavior tick pump** — the central `switch` in `BlockBehavior` and
the single active-voxel collection that fluids and grass flow through. Both are flagged 🔴/🔴 (TG-4) and
🟡/🟡 (TG-5) on **risk**, and both carry the explicit acceptance note *"fluid parity testing required."*
Today there is **no automated guard whatsoever** on behavior output: CodeGraph reports `HandleFluidSpread`,
`BlockBehavior.Behave`, `BlockBehavior.Active`, and `ChunkData.GetState`/`VoxelFromV3Int` all as
**"⚠️ no covering tests found."** Attempting TG-4 against an untested tick pump is how you ship a subtle
fluid-flow regression that only surfaces as a player bug report weeks later.

This document specifies the harness to build **before** either optimization is attempted. It is written at
the same point in the lifecycle as the meshing fidelity doc was: the open optimizations — not the suite —
define what to build.

### The hard part is different from meshing/lighting

The meshing suite has an **independent geometric oracle** (`MeshOracle.ExpectedStandardCubeFace` re-derives
vertex positions from first principles). The lighting suite has an independent BFS oracle. **Fluid flow has
no such tractable independent oracle** — the Minecraft Beta 1.3.2 flow rules (decay, gravity, infinite-source
regeneration, optimal-flow pathfinding, viscosity staggering) *are* the specification. Re-deriving them in a
test would just be a second copy of the production code (the A4 "shared-assumption trap" the lighting doc
warns about, in its most acute form).

**Therefore, the harness is not oracle-based. It is a three-legged guard:**

| Leg                                            | What it proves                                                                                                                                               | What it does **not** prove                                                                                                                               |
|------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Golden-master (characterization)** baselines | A refactor (TG-4/TG-5) preserves the *exact* emitted `VoxelMod` stream + active/inactive decisions, tick-for-tick.                                           | That the *current* behavior is correct — it freezes whatever the code does today, bugs included.                                                         |
| **Behavioral invariants**                      | Structural truths that must hold for *any* correct fluid/grass engine (determinism, termination, conservation-style sanity, the `Active`/`Behave` contract). | Fine-grained flow-shape correctness.                                                                                                                     |
| **Differential A/B** (the actual parity test)  | The new TG-4/TG-5 dispatch produces a **byte-identical** `VoxelMod` stream to the old `switch` path over the same scenario, tick-for-tick.                   | Nothing about absolute correctness — only equivalence. This is the load-bearing TG-4/TG-5 guard, analogous to MR-5's chained-vs-separate equality (B10). |

The golden-master leg is the one most people forget is legitimate: because TG-4/TG-5 are **explicitly
behavior-preserving refactors**, "the output didn't change" *is* the correctness criterion, exactly as
`MeshAssert.OutputsEqual` is for the output-preserving `MR-*` items.

### Enabling precondition (already met)

Golden-master snapshots are only reproducible if the tick is **deterministic**. **TG-3 (done, 2026-06-20)**
made it so: `UnityEngine.Random` (globally locked, sequence-dependent on unrelated callers) was replaced
with a **local `Unity.Mathematics.Random`** seeded per-voxel **and** per-tick via `World.TickCounter`. A
fixed `(seed map, starting TickCounter, tick count)` now yields a fixed `VoxelMod` stream. **This harness was
not buildable before TG-3** — the snapshots would have been non-reproducible noise. Note the TG-3 caveat: the
RNG *sequence* differs from the pre-TG-3 implementation, so any golden master must be captured **after** the
TG-3 commit (it is; this doc post-dates it).

---

## 2. What the harness must exercise (the trusted core to build)

The harness must run the **real** tick path, not a reimplementation. Concretely it drives, per tick:

```
for each active voxel pos in the test chunk:
    mods   = BlockBehavior.Behave(chunkData, pos)   // the real method — emits List<VoxelMod>
    active = BlockBehavior.Active(chunkData, pos)    // the real keep/drop classification
    capture (pos, active, mods snapshot)
apply captured mods to chunkData   (mirroring World.ModifyVoxel replay order — see §3 risk R3)
advance TickCounter
```

This mirrors `Chunk.TickUpdate` (`Chunk.cs:237`) + `World.ProcessTickUpdates` (`World.cs:1294`) without a
live `World` MonoBehaviour. The captured per-tick record (the ordered set of `VoxelMod`s + the active-voxel
classification) is the **snapshot** that golden-master and differential legs compare.

### The seams that must be stubbed (the real work)

Unlike the meshing job (a pure function over a `NativeArray<uint>`), the behavior tick is **managed
main-thread code that reaches through `World.Instance` singletons** and a managed `ChunkData`. The harness
must stand up a minimal `World.Instance` exactly as the **MH-6 reflection-stub** precedent did
(`SectionRendererTestFixture` reflects the private `World.Instance` setter onto an `AddComponent`'d `World`
so no `Awake`/`OnEnable` runs). The seams, enumerated from the current code:

| #  | Seam (current code)                                                                                                                                       | Used by                                                                                                                                                                                    | Stub strategy                                                                                                                                                                                                                  |
|----|-----------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| S1 | `VoxelState.Properties` → `World.Instance.BlockTypes[id]` (managed `BlockType[]`)                                                                         | every `Behave`/`Active` call (`props.fluidType`, `props.isSolid`, `props.flowLevels`, `props.spreadChance`, `props.waterfallsMaxSpread`, `props.infiniteSourceRegeneration`, `props.tags`) | `TestBehaviorBlockPalette` builds a synthetic `BlockType[]` (Air, Dirt, Grass, WaterSource, optionally Lava) and assigns it to `World.Instance.BlockTypes`.                                                                    |
| S2 | `World.Instance.TickCounter`                                                                                                                              | grass-spread seed (`BlockBehavior.cs:151`), lava viscosity seed (`BlockBehavior.Fluids.cs:160`)                                                                                            | settable on the stub `World`; the harness advances it each tick.                                                                                                                                                               |
| S3 | `World.Instance.settings.enableWaterDiagnosticLogs` (`IsWaterDebugEnabled`)                                                                               | fluid debug logging                                                                                                                                                                        | stub `settings` with the flag **off** (keeps the suite quiet & fast).                                                                                                                                                          |
| S4 | `ChunkData.GetState(localPos)` → `World.Instance.worldData.GetVoxelState(globalPos)` **when the position bleeds outside the chunk** (`ChunkData.cs:1080`) | any border-adjacent fluid/grass voxel                                                                                                                                                      | **Two-tier**: Tier-1 scenarios place voxels in the chunk **interior** (like `MeshingTestWorld`) so this is never hit; Tier-2 (cross-chunk, see §3 MH-equivalent gap) needs a stub `worldData` returning a controlled neighbor. |
| S5 | `Behave` returns the `ThreadStatic` reusable `Mods` list — *"callers must consume immediately, not store"* (`BlockBehavior.cs:220`)                       | snapshot capture                                                                                                                                                                           | the harness must **deep-copy** each returned list into the snapshot the same tick, never retain the reference.                                                                                                                 |

> **This seam table is also the TG-4/TG-5 design spec in miniature.** Every row is a managed-singleton
> dependency the optimization must convert to blittable native data (a `BlockType` blob indexed by id, a
> passed-in tick counter, an output `NativeList<VoxelMod>`, a native neighbor view). Building the harness
> first *documents the exact coupling surface* the refactor has to sever.

### What counts as the snapshot (the comparison unit)

A per-scenario snapshot is the ordered list, across N ticks, of:

- the set of active-voxel positions evaluated this tick (and the `Active` result for each), and
- the `VoxelMod`s emitted (`GlobalPosition`, `ID`, `Meta`, `ImmediateUpdate`, `Rule`) — `VoxelMod` already
  has value `Equals`/`GetHashCode` (`VoxelMod.cs:65`), so equality is free.

Comparison is **order-sensitive within a tick** unless we deliberately canonicalize (see risk R3). Snapshots
serialize to a compact text form checked into the suite as the golden master (the lighting suite's
baseline-as-code pattern).

---

## 3. Blind spots, risks & open design questions

Gap/risk IDs are `BH-#`. Each states what is at stake and the proposed resolution. These are the decisions to
lock before writing code.

### BH-1 — `ChunkData` standup without a live World · **AUDITED 2026-06-20 — feasible, low-risk** · gates the whole harness

- **Issue:** `BlockBehavior` operates on a managed `ChunkData`, which owns `sections[]` (managed
  `ChunkSection.voxels` arrays) and a `Position`. The harness needs to construct one, write voxels into it
  (`SetBlock`-style), and read it back via the real `GetState`/`VoxelFromV3Int` — **without** the chunk
  lifecycle (generation/lighting/meshing pipeline) ever running.
- **Audit verdict — construction is a solved problem (precedent exists):**
    - `new ChunkData(pos)` runs `InitializeSections()` (`ChunkData.cs:163–187`), which only `new[]`s the
      managed `sections`/`SectionUniformSkyLevel` arrays — **no `World` access in the ctor.** The **Lighting
      suite already does exactly this**: `LightingTestWorld.cs:192` and `LightingAssert.cs:290–291` all
      `new ChunkData(...)` directly in edit mode.
    - Lazy section allocation on write is World-null-safe: `GetNewSection()` (`ChunkData.cs:245–253`) has an
      explicit `if (World.Instance != null) … else return new ChunkSection();` *"Fallback … for test
      scenarios"*. `SetVoxel` (`ChunkData.cs:590–605`) and the other write paths route through it.
    - So `BehaviorTestWorld` can `new ChunkData((0,0))`, write voxels via `SetVoxel` (or a thin
      `SetBlock(x,y,z,id,meta)` wrapper over `BurstVoxelDataBitMapping.PackVoxelData`), and read them back
      through the **real** `GetState`/`VoxelFromV3Int` — **no production change, no World stub** for the
      read/populate surface.
- **The one remaining unknown moved to the *apply* step, not construction** — see the refined BH-3 below:
  the production *mod-replay* path `ChunkData.ModifyVoxel` **hard-returns on `World.Instance is null`**
  (`ChunkData.cs:425`) and couples to more subsystems, so how the harness applies emitted mods is the real
  design fork. Construction itself is settled.
- **Correction (found during implementation):** the audit's "construction is World-null-safe via the
  `new ChunkSection()` fallback" is only half-true. That fallback in `GetNewSection` (`ChunkData.cs:245`)
  fires **only when `World.Instance == null`** — but the behavior harness *must* set `World.Instance` (for
  `VoxelState.Properties`), so section allocation instead takes the `World.Instance.ChunkPool.GetChunkSection()`
  branch and **NREs on the null `ChunkPool`** (built in `World.Awake`, which is bypassed). Fix: the harness
  injects a real `ChunkPoolManager(_worldGo.transform)` into `World.ChunkPool` by reflection (private setter);
  its ctor only wires lazy pools — no GameObjects, no `World` access — so it is edit-mode-safe. This is now
  part of `BehaviorTestWorld` and is the reason `BlockType[]` standup and section allocation both work.
- **Effort:** 🟢 low (was 🟡 — downgraded by the audit; mirror `LightingTestWorld`'s ctor usage + inject `ChunkPool`).

### BH-2 — `World.Instance` reflection stub + synthetic `BlockType` palette · **AUDITED 2026-06-20 — verified, low-risk**

- **Issue:** seams S1–S3. No production change is acceptable (mirror MH-6's "zero production change").
- **Audit verdict — the full recipe is confirmed against current code:**
    - `VoxelState.Properties` (`VoxelState.cs:274`) is exactly `=> World.Instance.BlockTypes[ID];` — so a
      stub `World.Instance` with a populated palette is mandatory and sufficient for the read surface.
    - **World stub:** reuse `SectionRendererTestFixture`'s proven recipe verbatim — `AddComponent<World>()`
      (plain `MonoBehaviour`, so **no `Awake`/`OnEnable` in edit mode**), then drive the **private static**
      `World.Instance` setter by reflection (`GetProperty(...).GetSetMethod(nonPublic:true)`), and restore the
      previous instance on `Dispose`.
    - **Palette indirection (important):** `World.BlockTypes` is a **read-only** property —
      `public BlockType[] BlockTypes => blockDatabase.blockTypes;` (`World.cs:75`). So you cannot assign it;
      instead set the **public field** `world.blockDatabase` to a stub `BlockDatabase`
      (`ScriptableObject.CreateInstance<BlockDatabase>()`) whose public `blockTypes` array is the palette —
      exactly as the MH-6 fixture stubs `opaqueMaterial`/etc. on a stub `BlockDatabase`.
    - **`BlockType` is a plain `[Serializable] class`** (`BlockType.cs:12`) with **every** field the behavior
      code reads exposed and object-initializer-settable: `isSolid`, `fluidType` (`FluidType.None|WaterLike|LavaLike`),
      `fluidLevel`, `flowLevels`, `waterfallsMaxSpread` (default `true`), `infiniteSourceRegeneration`,
      `spreadChance` (default `1.0`), `opacity` (+ derived `IsLightObstructing` = `opacity>0`), `tags`/`canReplaceTags`
      (`BlockTags`), and `isActive`. No asset wiring needed.
    - **`settings`:** `World.settings` is a public field of type `Settings`, a plain `[Serializable] class`
      (`SettingsManager.cs:59`); `new Settings()` and set `enableWaterDiagnosticLogs = false` (keeps the suite
      quiet) and `enableLighting = false` (only matters if BH-3 Option A is ever used).
    - **`TickCounter`:** `World.TickCounter` (`World.cs:116`) is **read-only** over a private `_tickCounter`
      (`:109`). The harness advances the tick by reflecting and writing `_tickCounter` each pass (same
      reflection pattern as the `Instance` setter).
- **One palette design point that differs from `TestMeshBlockPalette`:** the meshing palette uses
  *test-local* indices (0–4) because the mesh job consumes the array directly. The behavior code instead
  **hardcodes real IDs** (`id == BlockIDs.Grass`, `BlockIDs.Dirt`, `BlockIDs.Air`). So `TestBehaviorBlockPalette`
  must build a **`BlockType[]` indexed by real `BlockIDs`** (sized `max(usedId)+1`, air-like defaults in the
  gaps, real entries at `BlockIDs.Air/Dirt/Grass` + a water id). This keeps the `BlockIDs`-constant branches
  in `Behave`/`Active` on their production paths.
- **Effort:** 🟢 low (was 🟡 — every piece is a confirmed, already-used pattern; no production change, no
  unknowns remain).

### BH-3 — Mod application: which path, and in what order · **must-solve** · refined by the BH-1 audit

`Behave` only *emits* mods; it does not apply them. In production `Chunk.TickUpdate` → `World.EnqueueVoxelModifications`
→ the global `_modifications` queue → `ChunkData.ModifyVoxel` applies them. The harness must replay mods between
ticks, and there are **two decisions**: *which apply path*, and *in what order*.

**Decision 1 — apply path (the BH-1 audit surfaced a real fork).** `ChunkData.ModifyVoxel` (`ChunkData.cs:422–515`)
is the faithful path but couples far beyond the behavior surface:

| It touches                                                                 | In edit mode (interior scenario, lighting off)                                                  | Stub burden                                                 |
|----------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------|-------------------------------------------------------------|
| `if (World.Instance is null) return;` (`:425`)                             | **hard-blocks** — World stub mandatory                                                          | requires BH-2                                               |
| `World.Instance.BlockTypes[id]`, `settings.enableLighting`                 | fine with the stub palette + `enableLighting=false`                                             | S1, S3(+`enableLighting`)                                   |
| `worldData.QueueSunlightRecalculation` (`:495`)                            | skipped (gated on `lightingEnabled`)                                                            | none                                                        |
| `World.NotifyChunkModified` (`:501`)                                       | no-op — empty `_chunkMap` ⇒ `chunk==null`, interior ⇒ no border rebuilds (`World.cs:1663–1685`) | none                                                        |
| `worldData.ModifiedChunks.Add(this)` (`:514`)                              | **NRE unless `worldData` stubbed**                                                              | requires `worldData` stub                                   |
| `Chunk.AddActiveVoxel/RemoveActiveVoxel` via the `Chunk` link (`:506–512`) | skipped if `Chunk==null` — but then the active set never grows as fluid spreads                 | needs a `Chunk` link OR harness-side active-set maintenance |

- **Option A — faithful replay via `ModifyVoxel`:** maximal fidelity, but drags in `worldData.ModifiedChunks`,
  the `World`/`Chunk` links, and lighting/meshing notify machinery that is **irrelevant to behavior parity** and
  adds NRE surface. Over-coupled for what TG-4/TG-5 actually change.
- **Option B — minimal apply via `SetVoxel` + harness-side active-set maintenance (RECOMMENDED):** apply each mod
  with `SetVoxel` (World-null-safe per the BH-1 audit) and have the driver replicate the **active-set contract**
  that `ModifyVoxel` + `Chunk.TickUpdate` jointly enforce: on apply, add the touched voxel to the active set iff
  `newProps.isActive`; in the tick loop, drop any voxel whose `Active(...)` returns `false` (exactly
  `Chunk.TickUpdate:251`). The harness already calls `Active` per voxel for its snapshot, so this is nearly free —
  and it keeps the harness scoped to the **behavior surface** (`Behave`/`Active`/`VoxelMod`/active-set), which is
  precisely TG-4/TG-5's surface. Lighting/meshing/notify are out of scope (§6).
- **Recommendation: build Option B.** Keep Option A as a documented higher-fidelity escalation only if a future
  bug is suspected to live in the `ModifyVoxel` coupling itself (not a behavior-parity concern).
- **As-built (2026-06-20, hardened post-review):** Option B was extended so `ApplyMod` faithfully mirrors the
  **state-affecting** half of `World.ApplyModifications` + `ModifyVoxel` — the `oldPacked == newPacked` no-op
  early-out, the `BlockTagUtility.CanReplace` / `ReplacementRule` placement gate (rejected mods dropped), and
  the `REQUIRES_SUPPORT` break cascade (re-enqueued into a FIFO drain) — while still using `SetVoxel` to skip
  the lighting/meshing/notify side effects (genuinely out of scope). A code review found that the original
  `SetVoxel`-only version *bypassed* the gate + cascade + no-op guard (a silent fidelity hole); these are now
  replicated. The BH-B1 golden master is byte-identical before and after (water→air is applied by both paths),
  confirming the hardening changed fidelity, not behavior. Palette tags stay `NONE` (so `CanReplace` stays
  permissive and fluid self-replacement works); per-scenario tag fidelity can be added as scenarios need it.

**Decision 2 — apply order (the subtle correctness question).** Ordering across voxels within a tick matters when
two cells write the same neighbor. The driver must replicate production's order: iterate active voxels as
`Chunk.TickUpdate` does — the `_activeVoxels` **`HashSet<Vector3Int>` enumeration order**.

- **Open question — RESOLVED (empirical probe, 2026-06-20):** is that enumeration order *deterministic*
  run-to-run? **Yes, and non-randomized.** A standalone probe (`scratchpad/hashprobe`, reproducing
  `Vector3Int.GetHashCode` verbatim) established: (T1) a fixed add-history yields a fixed order, and for
  *pure-add* sequences enumeration order **equals insertion order**; (T2) order tracks insertion history;
  (T3/T3b) any fixed add/**remove** history is reproducible run-to-run; (T4) `Vector3Int` hashing is pure
  arithmetic with **no per-process seed** (`.NET` does not randomize value-type-keyed sets — unlike
  `string`), so the order is stable **across editor sessions and machines**, not merely within a run;
  (T5) initial **capacity does not change** enumeration order (so a future pre-size of `_activeVoxels` is
  order-safe). **Therefore, current production tick behavior is deterministic** as long as the add/remove
  history is — which it is, given TG-3's seeded RNG + the linear `OnDataPopulated` scan order. The earlier
  "iteration order might be non-deterministic" concern is **closed**; BH-6 remains as a cheap continuous guard.
    - **Mono confirmation — DONE (2026-06-20):** the same probe was re-run in-editor on **Mono 6.13.0** (via a
      throwaway `[MenuItem]`, since `Unity_RunCommand`'s C#-exec backend was returning `ApiNoLongerSupported`).
      Results corroborate CoreCLR **exactly**: T1's enumeration string is byte-identical across the two runtimes,
      the `Vector3Int` hashes match (`805306401 / 0 / -268433409`, non-randomized on Mono too), and a slot-reuse
      case (remove two, then add two new → the new entries occupy the *freed* mid-array slots) was deterministic
      and reproducible. Both the editor's Mono and IL2CPP builds use the same CoreFX-derived `HashSet<T>`, so the
      result holds for shipped builds. **No further confirmation needed before freezing golden masters.**
- **Consequence for the differential (BH-D1):** TG-4 **will** reorder iteration (splitting actives by behavior
  type changes traversal). So the differential must compare streams **order-sensitively where two mods target the
  same voxel** (a genuine behavior difference) but **canonicalized (position-sorted) for independent mods** (a
  benign reordering). This split must be decided and encoded **before** any golden master is captured — a golden
  master frozen against an incidental order would reject a correct TG-4.
- **Effort:** 🔴 high — Decision 2 is the subtlest correctness question in the whole harness.

### BH-4 — Cross-chunk (border) fluid flow · **OPEN** · Tier-2

- **Issue:** seam S4. Fluids spreading at a chunk border call into `World.Instance.worldData.GetVoxelState`.
  Interior-only scenarios (Tier-1) dodge this, but real fluid bugs cluster at chunk seams.
- **Proposed:** Tier-2 fixture with a stub `worldData` (or a 2-chunk `BehaviorTestWorld`) returning controlled
  neighbor voxels. Defer to a later wave — Tier-1 already guards the bulk of TG-4/TG-5's dispatch change.
- **Effort:** 🟡 medium. **Like the meshing suite's cross-chunk culling, this is explicitly deferred, not denied.**

### BH-5 — `Active`/`Behave` contract invariant · **RETRACTED (2026-06-20) — not a valid invariant**

- **Originally proposed:** "a voxel for which `Active(...)` returns `false` must emit no world-changing
  `VoxelMod`, else the tick-pump drop would lose work" — plus universal termination.
- **Why it's wrong (confirmed in code):** `Chunk.TickUpdate` (`Chunk.cs:247-260`) **enqueues the mods before**
  the `Active` drop-check, so a dropped voxel's mods are still applied — **no work is lost**, and "inactive ⇒
  no mods" is not a safety requirement. It holds incidentally in today's fluid/grass code (BH-B1 shows dropped
  voxels emitting nothing), but a future behavior could legitimately emit on its final tick, so asserting it
  would bake in a non-invariant. Termination is also **not universal** — a sustained source (BH-B1) never
  terminates; only a cut-off flow (BH-B4) does. The single universal invariant is determinism (**BH-6**).
- **Disposition:** BH-B1 ships with **BH-6 (determinism) + non-vacuity** instead of this contract; assert
  termination only in scenarios that expect it (e.g. BH-B4). The §4 BH-B8 row (was "contract over all
  fixtures") collapses into the per-scenario BH-6 checks.

### BH-6 — Determinism invariant · **build first**

- **Invariant:** running the same scenario twice (fresh `ChunkData`, same starting `TickCounter`, same tick
  count) yields byte-identical snapshots. This is the precondition that makes golden masters meaningful and
  directly exercises the TG-3 seeding.
- **Effort:** 🟢 trivial (run twice, `OutputsEqual`-style compare).

### BH-7 — apply-path fidelity · **RESOLVED — accepted defensive parity (2026-06-20)**

- **Context (from the 2026-06-20 code review):** `BehaviorTestWorld.ApplyMod` faithfully mirrors the
  state-affecting half of `World.ApplyModifications` + `ChunkData.ModifyVoxel` — the `oldPacked == newPacked`
  no-op early-out, the `BlockTagUtility.CanReplace` / `ReplacementRule` placement gate (rejected mods dropped),
  and the `REQUIRES_SUPPORT` break-cascade (FIFO re-enqueue). The original `SetVoxel`-only version silently
  bypassed all three; that hole is closed.
- **Reachability check (done):** enumerated **all 6** behavior-emitted mod sites (grass→dirt, dirt→grass in
  `BlockBehavior.cs`; decay-to-air, level-update, gravity, horizontal-spread in `BlockBehavior.Fluids.cs`).
  Result: **none triggers the gate or the cascade.** No behavior emits a *solid → non-solid* mod (grass↔dirt
  are solid→solid; every fluid mod replaces a non-solid/Air/same-fluid cell), so the `REQUIRES_SUPPORT`
  cascade is **unreachable through `Behave`**. And every emitted mod is a placement the live engine performs,
  so `CanReplace` **never rejects** one. The no-op guard *is* reachable (BH-B1's T2 double-writes a cell) but
  its effect is idempotent on state and the active set, so it never changes a snapshot.
- **Disposition:** the gate + cascade are kept as **defensive parity** — they cost nothing, mirror production
  exactly, and keep the apply path correct if **TG-4/TG-5 (or a new behavior) changes mod emission**. They are
  **intentionally unguarded** because no behavior scenario can exercise them. Marked with a dated
  `DEFENSIVE PARITY (BH-7)` comment at both code sites so they are not "simplified" away or mistaken for tested.
- **If reachability ever changes** (a behavior that removes a solid support, or emits a `CanReplace`-rejected
  mod): close with a **direct `ApplyMod` unit test** driven by crafted mods (the harness would need to expose
  the apply path for testing) — *not* a `Behave`-driven scenario. BH-B9/BH-B10 in §4 are gated on that.
- **Effort:** ✅ closed (reachability analysis only; no scenario built — none is constructible today).

---

## 4. Baseline scenarios (proposed BH-series)

Test-first, one commit per scenario, all baselines green after each (the `validation-driven-bugfix`
lifecycle). Each golden-master scenario pairs with a **positive control** so it can't pass vacuously (the
B8/B9 pattern — e.g. prove the snapshot is non-empty and that a deliberately altered palette changes it).

| ID            | Scenario                                                                                                                                                                                                                                                           | Leg                                                                  | Guards                                                                                        |
|---------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| **BH-B1** ✅   | Single water source on a flat floor, **3 ticks** → symmetric level-1→3 spread (28 mods)                                                                                                                                                                            | golden-master + determinism + non-vacuity                            | core horizontal spread — **shipped 2026-06-20**                                               |
| **BH-B2** ✅   | Boxed source flows east over a 1-block cliff → falls one block → waterfall reset on landing (3-tick golden, 5 mods; in-game confirmed before freeze)                                                                                                               | golden-master + determinism                                          | gravity + `MakeFalling` + optimal-flow-toward-drop + waterfall reset — **shipped 2026-06-21** |
| **BH-B3** ✅   | Two sources one gap apart in a walled channel → gap fills then regenerates to a source; all stabilize (3-tick golden, 3 mods, terminates)                                                                                                                          | golden-master + determinism + termination                            | `infiniteSourceRegeneration` path — **shipped 2026-06-21**                                    |
| **BH-B4** ✅   | Unsupported (sourceless) water cell → drains to air in 1 tick; active set empties (the decay mod replaces water→air, so the apply-step active-removal terminates it)                                                                                               | golden-master + determinism + termination                            | decay-to-air + termination + `ImmediateUpdate` — **shipped 2026-06-20**                       |
| **BH-B5** ✅   | Lava (low `spreadChance`) in a walled 1-D channel, **11 ticks** → viscosity staggering (source skip,skip,spread; x8 skip×4,spread) → full channel (level 3) → quiesces (3 mods, terminates; in-game confirmed before freeze)                                       | golden-master + determinism + progression + staggering + termination | TG-3 per-tick reseed (staggering *progresses*, not freezes) — **shipped 2026-06-21**          |
| **BH-B6** ✅   | Grass flanked by two convertible-dirt cells, **6 ticks** → idles 4 ticks (fails the 2% roll) then spreads to the reservoir-chosen right cell (x=9 over x=7, so candidate-scan order is frozen); position picked so the seed fires early (1 mod; in-game confirmed) | golden-master + determinism + non-vacuity + chosen-candidate         | grass reservoir-sampling + spread roll (seeded RNG) — **shipped 2026-06-21**                  |
| **BH-B7** ✅   | Grass capped by a solid block, **2 ticks** → emits one Dirt mod onto itself, drops from the active set, terminates. Deterministic — the solid-on-top branch returns before any RNG use; frozen after a code-trace match (1 mod)                                    | golden-master + determinism + termination + became-dirt              | grass→dirt branch (no RNG gate) — **shipped 2026-06-21**                                      |
| ~~**BH-B8**~~ | ~~contract over all fixtures~~ — **retired** (BH-5 retracted; determinism is per-scenario via BH-6)                                                                                                                                                                | —                                                                    | —                                                                                             |
| **BH-B9**     | **GATED** (BH-7 unreachable via `Behave`) — `REQUIRES_SUPPORT` on a draining support → cascade; revisit via direct `ApplyMod` test                                                                                                                                 | golden-master + positive control                                     | apply-path support cascade (**BH-7**)                                                         |
| **BH-B10**    | **GATED** (BH-7 unreachable via `Behave`) — mod a `CanReplace`-rejected block; revisit via direct `ApplyMod` test                                                                                                                                                  | golden-master + positive control                                     | apply-path `CanReplace` gate (**BH-7**); needs per-fixture tags                               |
| **BH-D1**     | **Differential:** every BH-B# scenario run through old `switch` vs new TG-4/TG-5 dispatch → identical snapshots                                                                                                                                                    | A/B                                                                  | **the load-bearing TG-4/TG-5 parity guard**                                                   |

BH-D1 is added **in the TG-4/TG-5 PR itself** (it needs both code paths to exist), exactly as MR-5's
chained-vs-separate equality baseline (B10) was the guard for MR-5.

---

## 5. Phased build plan (waves)

Mirrors the meshing/lighting wave structure: each wave leaves the suite green and unblocks the next. Build
**test-first**, one commit per scenario, all baselines green after each (the `validation-driven-bugfix`
lifecycle), driven via the menu item + `Unity_ReadConsole` (see §8 — `Unity_RunCommand` is unavailable).

### Wave 0 — Harness infrastructure · ✅ DONE (2026-06-20)

World reflection-stub (`ValidationReflection`) + `TestBehaviorBlockPalette` + `BehaviorTestWorld` (Option-B
apply, hardened post-review to mirror the `CanReplace` gate + support cascade + no-op guard) + `BehaviorSnapshot`

+ `GoldenMaster` helper + the `Validate Behavior` runner. **Smoke** + **BH-B1** green. The `HashSet`
  iteration-order determinism question was settled empirically (CoreCLR + Mono).

### Wave 1 — Fluid golden masters (Tier-1, interior only) · ✅ DONE (2026-06-21)

Freeze current fluid behavior so TG-4/TG-5 can be proven behavior-preserving. **Confirm each scenario in-game
before freezing its golden** — a golden master of buggy behavior is worse than none (`validation-driven-bugfix`).
Recommended order (most-bug-prone / highest-invariant-value first):

1. ✅ **BH-B4 — unsupported water → decay to air** (DONE 2026-06-20): established the per-scenario
   **termination** assertion (`ActiveVoxelCount == 0`) + the decay-to-air path. Captured-and-frozen; the
   behavior is textbook (sourceless flow drains immediately) and was cross-checked against a code trace.
2. ✅ **BH-B2 — water over a 1-block cliff** (DONE 2026-06-21): gravity + `MakeFalling` + optimal-flow-toward-drop
    + waterfall reset. Boxed source → 3-tick golden; **confirmed in-game before freeze** (the falling-fluid
      dynamics warranted it).
3. ✅ **BH-B3 — two sources over solid** (DONE 2026-06-21): `infiniteSourceRegeneration` — gap regenerates to a
   source; deterministic, so frozen after a code-trace match (terminates too).
4. ✅ **BH-B5 — lava (low `spreadChance`)** (DONE 2026-06-21, completes Wave 1): the only scenario exercising
   the TG-3 per-tick reseed. A single source in a walled 1-D channel; the seeded gate makes the *same* position
   skip then spread across ticks (source skip,skip,spread; the level-2 cell skip×4 then spread), advancing the
   front cell-by-cell to level 3 before quiescing. Asserts determinism (the core TG-3 reproducibility guard),
   non-vacuity, **progression** (front reaches x+2 — a position-only seed would freeze it), **staggering** (the
   active source skips ≥1 tick — unlike water at 1.0), and **termination**. **Confirmed in-game before freeze**
   (the 25% staggering eyeballed). This validates the determinism precondition the whole harness rests on.

### Wave 2 — Grass golden masters · ✅ DONE (2026-06-21)

Lower-traffic, but TG-4 splits grass into its own collection, so it needs the same guard.

1. ✅ **BH-B6 — grass spreads to convertible dirt** (DONE 2026-06-21): the second seeded-RNG path. Grass flanked
   by two convertible-dirt cells; the seeded reservoir-sampling + 2% spread roll make it idle four ticks then
   spread to one chosen candidate. The grass column was picked (via a throwaway seed probe, since spread
   chance is only 0.02) so the per-tick reseed fires a spread at T5 — keeping the golden tight while still
   showing the idle→spread staggering. Two candidates make the reservoir **choice** observable (x=9 over x=7),
   so a candidate-scan-order change in TG-4/TG-5 breaks the golden. Asserts determinism, non-vacuity,
   exactly-one-candidate-converted, golden. **In-game confirmed before freeze.**
2. ✅ **BH-B7 — grass turns to dirt under a solid block** (DONE 2026-06-21, completes Wave 2): the grass→dirt
   branch fires *before* any RNG use (it returns on the solid-on-top check), so it is **deterministic** — frozen
   after a code-trace match, no seed probe. A grass cell capped by stone emits one Dirt mod onto itself, drops
   from the active set, and terminates. Asserts determinism, non-vacuity, became-dirt, termination, golden. This
   completes the behavior surface (all fluid + grass paths baselined). **Remaining: promote the doc (below); BH-D1
   lands in the TG-4/TG-5 PR.**

### Interleaved — close the BH-7 apply-path-fidelity gap · ✅ DONE (2026-06-20)

Reachability check complete: enumerated all 6 behavior-emitted mod sites — **neither the `CanReplace` gate nor
the `REQUIRES_SUPPORT` cascade is reachable through `Behave`** (no behavior emits a solid→non-solid mod; every
emission is a placement the engine performs, so `CanReplace` never rejects one). BH-7 is closed as **accepted
defensive parity** (§3): the code is kept faithful + dated-commented, BH-B9/BH-B10 are gated on reachability
ever-changing (then via a direct `ApplyMod` test, not a `Behave`-driven scenario).

### Wave 3 — Differential mode · built **with** the TG-4/TG-5 PR (not before)

**BH-D1.** Stand up the old `switch` path behind a flag (or a captured pre-refactor golden) and assert
byte-identical snapshots per the BH-3 ordering policy. **This is the wave that actually unblocks the
optimization** — it needs both code paths to exist, so it lands in the TG-4/TG-5 PR itself.

### Deferred (not a blocker for TG-4/TG-5 dispatch)

**BH-4** (cross-chunk Tier-2 fixture). The empty-`worldData` stub already prevents border NREs (reads return
"void"); a real two-chunk fixture is only needed when a scenario must model cross-chunk flow. Build alongside
whatever first needs border-fluid coverage.

### Promotion

Once Wave 1 (+ Wave 2) lands, this doc is no longer "proposed" — **move it to
`Documentation/Architecture/Testing Framework/`** as a sibling fidelity doc (per the status header) and update
the cross-references.

---

## 6. Out of scope (by design)

- **True concurrency / Burst scheduling races.** Synchronous, single-threaded driver only — same
  **WONTFIX (structural)** stance as the meshing/lighting suites. TG-4's *parallelism* (running per-behavior
  jobs across cores) is verified for **output equivalence** (BH-D1), not for thread-safety of the scheduling
  itself; that is a separate concern (Burst safety system + stress testing), not this harness.
- **Absolute fluid-flow correctness.** Out of reach without an independent oracle (see §1). The harness guards
  *equivalence* and *invariants*, not *spec-correctness* of the current rules.
- **Performance measurement.** That is the `ActiveVoxelScanBenchmark` / a future behavior benchmark's job; this
  suite is a correctness guard only.

---

## 7. Cross-references

- Optimizations this harness gates: [PERFORMANCE_IMPROVEMENTS_REPORT.md](PERFORMANCE_IMPROVEMENTS_REPORT.md)
  §Tick & Gameplay (TG-4, TG-5), and the TG-3 entry (the determinism precondition).
- Sibling harness docs & status-tag / wave conventions:
  [MESHING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
  (esp. MH-6 reflection-stub seam, B10 differential pattern, B8/B9 positive-control rule),
  [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
  (esp. A1 logic-move-not-fake pattern, A4 shared-assumption trap).
- Production tick path being modeled: `Chunk.TickUpdate` (`Chunk.cs:237`),
  `World.ProcessTickUpdates` (`World.cs:1294`), `BlockBehavior.Active`/`Behave` (`BlockBehavior.cs`),
  `BlockBehavior.Fluids.cs` (`HandleFluidFlow`/`HandleFluidSpread`).
- Test-first lifecycle, taxonomy, positive-control rule, in-game-confirm-before-baseline:
  `.agents/skills/validation-driven-bugfix/SKILL.md`.
- Editor-validation cold-start checklist (RequestScriptCompilation + stale-code trap):
  the meshing fidelity doc §6 cold-start checklist applies verbatim.

---

## 8. Operational notes (running the suite)

- **Run it:** menu item **`Minecraft Clone/Dev/Validate Behavior`**. Green when the console logs
  `ALL N BEHAVIOR BASELINE TESTS PASSED`.
- **`Unity_RunCommand` is unavailable in this environment** — its C#-exec backend (`com.unity.ai.assistant`)
  returns `ApiNoLongerSupported`, and a restart does not fix it. Drive the suite (and any ad-hoc in-editor
  check) via **`Unity_ManageMenuItem` + `Unity_ReadConsole`** instead, which work. For a one-off probe, add a
  throwaway `[MenuItem]` that logs via `Debug.Log`, then delete it.
- **New-file / edit cycle:** after editing, `Unity_ManageMenuItem Execute "Assets/Refresh"` → poll
  `Unity_ManageEditor GetState` until `IsCompiling == false` → check `Unity_ReadConsole` (Type=Error) for
  compile errors → run the suite. `Clear` the console before a run and use `FilterText` + `IncludeStacktrace=false`
  so the result fits the tool's output cap.
- **Capturing a golden master:** leave the scenario's golden constant null/empty; `GoldenMaster.AssertOrCapture`
  logs the snapshot between `<<<GOLDEN-BEGIN>>>`/`<<<GOLDEN-END>>>`. Paste it into the constant, re-run to
  confirm `golden master matched`. **Confirm the behavior in-game first** (per Wave 1).
