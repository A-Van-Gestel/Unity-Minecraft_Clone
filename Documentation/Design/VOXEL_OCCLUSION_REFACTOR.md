# Directional Per-Face Voxel Occlusion (VO-*)

**Version:** 1.1  
**Date:** 2026-08-07  
**Status:** Proposed design — not implemented.  
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

> The engine gained partial blocks (`Stone Half Slab`) without the lighting model gaining a notion
> of them, so a half slab is authored `opacity = 15` and behaves as a *full* light blocker: it stops
> sky light entirely and contributes maximum ambient-occlusion darkening from every corner it
> touches. **The single most important decision here is that this plan does NOT introduce a new
> voxel-shape descriptor** — a rotation-aware per-block shape model already exists and is suite-guarded
> (`BlockCollisionBounds` + `Helpers.BlockCollisionBoundsUtility`, guarded by `NS-4`), and per-face
> occlusion is derivable from it arithmetically. The work is therefore mostly *plumbing an existing
> shape into Burst* and *replacing boolean opacity gates with directional ones*, not designing a shape
> system. Headline defect: `LIGHTING_BUGS.md` Bug 20 and `MESHING_BUGS.md` Bug M01 are two halves of
> one artifact, and Bug M01's repro (`KM01a`) is already red in the meshing suite and becomes this
> arc's acceptance test.

**Audited:** 2026-08-07, at commit `feb26454` (branch `feat/world-scaling`).
Read this session: `Jobs/MeshGenerationJob.cs` (smooth-lighting and custom-mesh paths in full),
`Jobs/BurstData/LightAttenuation.cs`, `Jobs/BurstData/BurstCustomMeshRotationUtility.cs`,
`Jobs/BurstData/BurstVoxelMetadataUtility.cs`, `Jobs/BurstData/BurstVoxelData.cs`
(`BuildCornerOffsetLUT`), `Helpers/VoxelMeshHelper.cs` (custom-mesh + corner-UV paths),
`Helpers/BlockCollisionBoundsUtility.cs`, `Data/JobData.cs`, `Data/BlockType.cs`,
`Data/JobData/JobDataManagerFactory.cs`, `VoxelData.cs`, plus the meshing validation harness
(`MeshingTestWorld`, `TestMeshBlockPalette`) and `Serialization/ChunkSerializer.cs` headers.
Live editor state was queried via Unity MCP to read the real `BlockDatabase.asset` block
properties, and the Bug M01 mechanism was confirmed numerically by an inline harness run
(see §3, F1). Call-site counts were taken by exhaustive grep this session. Anything not verified
in code this session is labeled "executor verifies".

**Amended:** 2026-08-07 — **VO-0 executed.** It needed no production instrumentation after all: three of
its four questions are static data reads and the fourth is a harness question, so it ran entirely through
`Unity_RunCommand` against the real `BlockDatabase.asset` and the lighting harness, leaving zero production
code. Results, which later phases cite:

- **(a) The blast radius is one block type.** Of 38 block types in `BlockDatabase.asset`, exactly **one**
  has `HasCustomBounds` — `Stone Half Slab`. Every behaviour-changing phase in this arc therefore alters
  the appearance of a single block, which materially de-risks VO-3 and VO-5.
- **(b) §2.3's worked table is confirmed exactly.** The slab is authored `mode = MatchVisualMesh`,
  `min = (0.000, 0.000, 0.000)`, `max = (1.000, 0.500, 1.000)` — a clean half-cell. **VO-1's stop-condition
  is satisfied.**
- **(c) A sky-exposed opaque cell DOES store a usable surface stamp.** Superflat Stone (opacity 15) floor
  to `y = 10`, after `RunInitialLighting`: the topmost stone at `y = 10` stores `sky = 15` while buried
  stone at `y ≤ 9` stores 0. **This resolves §8 open question 1** — see the revised VO-6 precondition.
- **(d) Version anchors for VO-7.** The chunk binary version is
  `ChunkSerializer.CURRENT_CHUNK_VERSION = 7` (`Assets/Scripts/Serialization/ChunkSerializer.cs:31`); the
  *world* version ladder is separate and its highest registered step is
  `Migration_v12_to_v13_PlayerChunkRelativePosition`, so VO-7 adds a `Migration_v13_to_v14_*` step.
  (Executor confirms no v13→v14 step has since been added and locates the current-version constant.)

**Relationship to other documents:**

- [`LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) — supplies the BFS
  propagation rules this plan modifies; its "Conditionally Opaque Blocks" section already predicted
  this work and is stale (it says no partial blocks exist). VO-3 corrects it.
- [`SMOOTH_AND_RGB_LIGHTING.md`](../Architecture/SMOOTH_AND_RGB_LIGHTING.md) — §2.5.2 currently
  claims the custom-mesh path "correctly handles sub-block geometry"; VO-6 corrects that claim.
- [`SUB_VOXEL_COLLISION_SYSTEM.md`](../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md) — **owns the shape
  model this plan reuses**. Its single-AABB scope limitation is inherited verbatim (§1 non-goals).
- [`LIGHTING_BUGS.md`](../Bugs/LIGHTING_BUGS.md) Bug 20 / [`MESHING_BUGS.md`](../Bugs/MESHING_BUGS.md)
  Bug M01 — the two defects this arc closes; both were filed test-first this session.
- [`MESHING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/MESHING_VALIDATION_HARNESS_FIDELITY.md)
  — the custom-mesh blind spot it records is now partly closed (see §2.4); MH-3's "AO values are
  un-modelled" note is what VO-5 must extend.
- [`LIGHTING_VALIDATION_HARNESS_FIDELITY.md`](../Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md)
  — the lighting harness gains a partial-block palette entry in VO-2.
- [`AOT_WORLD_MIGRATION_SYSTEM.md`](../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md) — VO-7's relight
  migration runs through it.

---

## 1. Goals and non-goals

### 1.1 Goals

1. A block that does not fill its cell occludes light **per face**, derived from its rotated shape:
   the face on its solid side blocks fully, the opposite face does not block at all, and the
   remaining faces block in proportion to the cross-section they cover.
2. **The motivating case works:** a 1×1 pit capped by a *vertical* slab receives light through the
   slab's open half. Neither of the cheap alternatives can express this — non-directional opacity can
   only block in every direction or none, and both readings are visibly wrong.
3. Ambient occlusion stops treating partial blocks as maximum occluders and instead darkens in
   proportion to actual coverage.
4. Sub-block mesh faces sample their smooth light from the cell their surface actually faces
   (closes Bug M01 / flips `KM01a` green).
5. One shape source of truth shared with physics, placement, and the interaction ray — no second
   descriptor that can drift.

### 1.2 Non-goals (versioned)

| Not doing                                                | Why / where it lives                                                                                                       |
|----------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------|
| Multi-AABB compound shapes (stairs, L-shapes, wedges)    | Inherited limitation of the shape model this plan reuses (`SUB_VOXEL_COLLISION_SYSTEM.md` §3, §7). Owned by **`VQ-4`** — interlock only, never re-proposed here. See §7. |
| Per-face *authored* occlusion overrides                  | Occlusion is derived, not authored. If a block ever needs to lie about its shape, that is a v2 item (§7).                   |
| Directional occlusion for fluids                         | Fluids have their own height/flow model; out of scope. §7.                                                                  |
| Changing the on-disk light **format**                    | Values change, layout does not — see D4 and every phase's serialization tripwire.                                           |
| Re-tuning any block's authored `opacity` value           | Opacity keeps its current meaning (a light cost). Only its *directional application* changes.                               |

---

## 2. Current state

### 2.1 Where occlusion is decided today

| # | Stage                        | Code (anchors — re-verify before editing)                                              | Occlusion model today                    | Suite coverage                          |
|---|------------------------------|-----------------------------------------------------------------------------------------|-------------------------------------------|------------------------------------------|
| 1 | Light attenuation rule       | `Jobs/BurstData/LightAttenuation.cs:29` `Attenuate(sourceLight, opacity)`                | Scalar, direction-free                    | Indirectly, via every lighting baseline |
| 2 | BFS propagation              | `Jobs/NeighborhoodLightingJob.cs` — 16 `IsOpaque` sites                                  | Boolean `Opacity >= 15` gates             | ✅ Lighting suite (tip **B100**)         |
| 3 | Cross-chunk support / veto   | `Helpers/CrossChunkLightModApplier.cs` — 3 `IsOpaque` references                          | Boolean; "fully-opaque neighbors excluded" | ✅ Lighting B48/B49, B56–B59            |
| 4 | Borderless oracle (the spec) | `Assets/Editor/Validation/Lighting/Framework/LightingOracle.cs`                           | Calls the same `Attenuate`                | n/a — it *is* the spec                   |
| 5 | AO corner sampling           | `Jobs/MeshGenerationJob.cs:1040` `SampleNeighborLight`, `:960` `CalculateCornerLights`    | Boolean → substitutes zero                | ⚠️ MH-3 covers **uniform fields only**   |
| 6 | Sub-block face light source  | `Jobs/MeshGenerationJob.cs:559` `GenerateCustomBlockMesh_SchemaAware`                     | Always the block-boundary neighbor        | ❌ → now `KM01a` (red)                   |
| 7 | Section meshing optimization | `Data/ChunkData.cs:880` `opaqueCount`                                                     | Boolean                                   | ✅ Meshing B9 (section tiling)           |

`IsOpaque` totals **43 occurrences across 12 files**; the lighting-critical concentration is stage 2
(16 sites in one file). The rest are meshing, debug UI, and tooltip readers that the executor
classifies per phase.

### 2.2 The shape model that already exists

`SUB_VOXEL_COLLISION_SYSTEM.md` is `Status: Implemented` and ships exactly the descriptor this plan
needs, already rotation-aware:

- `BlockCollisionBounds { CollisionBoundsMode mode; Vector3 min, max; bool HasCustomBounds }` in
  block-local `[0,1]³`, authored in the Block Editor.
- `Helpers/BlockCollisionBoundsUtility.GetBounds(blockType, meta, blockOrigin)` — resolves the
  authored bounds through `BurstCustomMeshRotationUtility.GetRotationMatrix` (the *same* matrix the
  mesher rotates vertices with) and returns the enclosing AABB. Full-block types take a fast path
  that skips rotation entirely.
- Guarded by **`NS-4`** (`Minecraft Clone/Dev/Validate Physics Solver`, 26 baselines) and shared by
  the physics solver, placement occupancy, and the `VQ-3` interaction ray.

**Gap (the actual work):** the utility is **managed** — it takes a `BlockType` class and returns
`UnityEngine.Bounds`, and `BlockTypeJobData` carries **no bounds fields at all** (verified by grep:
zero matches for `CollisionBounds`/`BoundsMin` in `Data/JobData.cs`). The rotation math inside is
already `float3x3` / `Unity.Mathematics`, so the body is Burst-ready; only the signature is not.

### 2.3 Per-face occlusion is arithmetic on that AABB

For a block-local axis-aligned box `[min, max]` and a face direction `d`, with `ε` a small epsilon:

- **Touches** the face plane iff `max[axis(d)] >= 1-ε` (positive `d`) or `min[axis(d)] <= ε` (negative `d`).
- **Coverage** = product of the box's extents on the two axes perpendicular to `d`, clamped to `[0,1]`.
- **Occlusion fraction** = `touches ? coverage : 0`.

Worked against the real slab (authored `min=(0,0,0)`, `max=(1,0.5,1)` — executor verifies the exact
authored values in `BlockDatabase.asset`; the *shape* is confirmed by the mesh geometry):

| Orientation                        | Face      | Touches | Coverage | Result                                        |
|-------------------------------------|-----------|---------|----------|-----------------------------------------------|
| Upright (`facing=Top`)              | −Y        | yes     | 1.0      | Full blocker — slab floor still blocks daylight |
| Upright                             | +Y        | no      | 0        | Open                                          |
| Upright                             | ±X, ±Z    | yes     | 0.5      | Half                                          |
| **Vertical** (`facing=Bottom` roll 0) | −Z      | no      | 0        | **Open — the motivating case**                |
| Vertical                            | +Z        | yes     | 1.0      | Full blocker                                  |
| Vertical                            | ±Y        | yes     | 0.5      | Half — partial light propagates *downward*    |

The vertical row is the whole reason this plan exists: it is unreachable by any scalar opacity value.

### 2.4 Serialization boundary

`Serialization/ChunkSerializer.cs` persists `ushort[] LightData` per section, with a compact
classification path and a v8→v9 migration precedent
(`Migration_v8_to_v9_LightDataSerialization.cs`). Therefore:

- The **format** does not change — still one `ushort` per voxel.
- The **values** do: worlds saved under the old model carry light computed with boolean occlusion.
- Consequence: this needs a **relight**, not a format migration. See D4 / VO-7.

### 2.5 Harness state (this session's groundwork, already landed)

Not part of the phases below — recording it so a cold executor does not redo it:

- `Assets/Editor/Validation/Meshing/Framework/TestCustomMeshLibrary.cs` — flattened synthetic
  custom-mesh fixtures (a parametric box mesh; half slab at `topY = 0.5`), mirroring
  `JobDataManagerFactory`'s flattening. `MeshingTestWorld` now passes **real** custom-mesh arrays.
- `TestMeshBlockPalette` gained `HalfSlab` (opacity 15, mirrors production) and `PartialOpaque`
  (opacity 7) at IDs 7/8; `Count` is 9.
- `MeshingValidationSuite.KnownBugs.cs` — the suite's first `K` scenario, `KM01a`, red for the
  documented reason with a passing positive control. Meshing suite is **41 scenarios, 40 baselines
  green**.

---

## 3. Findings

| #  | Finding (verified this session unless noted)                                                                                                                                                                                                                                                                                                                                              | Addressed by |
|----|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------|
| F1 | **Sub-block faces sample the wrong cell.** `GenerateCustomBlockMesh_SchemaAware` builds one corner quad from `CalculateCornerLights(worldFace, pos, …)`, which reads `pos + rotatedOffset` and the ring around it — correct only for boundary faces. Confirmed numerically: a slab's mid-plane face at `z = 8.50` kept light 136 when its own cell went 8→15, and moved to 166 when the cell a block and a half away did. `MESHING_BUGS.md` Bug M01. | VO-6         |
| F2 | **Partial blocks are full light blockers.** `Stone Half Slab` is authored `opacity = 15` and `IsOpaque => opacity >= 15`, so sky light stops dead at a slab and every AO corner touching one gets maximum darkening. `LIGHTING_BUGS.md` Bug 20.                                                                                                                                              | VO-3, VO-5   |
| F3 | **The architecture doc's premise is stale.** `LIGHTING_SYSTEM_OVERVIEW.md` §"Conditionally Opaque Blocks" states "We have no block types with directional transparency… if stairs, slabs, or other partial blocks are added in the future". A partial block has since shipped; the doc still reads as if none exists.                                                                        | VO-3 doc-sync |
| F4 | **A documented capability claim is false.** `SMOOTH_AND_RGB_LIGHTING.md` §2.5.2 claims the bilinear path "correctly handles sub-block geometry (half slabs, fences, stairs)". The bilinear blend *is* correct — `GetCornerUV` was verified corner-by-corner against `BuildCornerOffsetLUT` for all six faces — but the quad it blends is sampled from the wrong cell (F1).                    | VO-6 doc-sync |
| F5 | **The shape model exists but stops at the Burst boundary.** `BlockCollisionBoundsUtility` is managed (`BlockType` in, `Bounds` out) and `BlockTypeJobData` carries no bounds fields, so no job can ask "what shape is this block?" — the reason the lighting job has only a boolean to work with.                                                                                              | VO-1         |
| F6 | **Second-descriptor hazard.** A naive reading of F5 invites a fresh `VoxelShape` type for lighting, which would be the second rotation-aware shape descriptor in the codebase and would drift from the collision one. Explicitly rejected in D1.                                                                                                                                             | D1           |
| F7 | **The oracle shares the model under test.** `LightingOracle` calls the same `LightAttenuation.Attenuate` as the engine, so a directional change lands in both simultaneously and the suite cannot arbitrate correctness by itself. Baselines must be authored to pin *behaviour* (light reaches / does not reach a probe) rather than re-deriving the formula.                                | VO-2         |
| F8 | **AO occlusion is boolean, not fractional.** `SampleCorner` skips the diagonal term only when `sideAOpaque && sideBOpaque`, and `SampleNeighborLight` substitutes hard zero. There is no representation for "half occluding", so even a correct shape model has nowhere to put a coverage fraction.                                                                                            | VO-5         |
| F9 | **Light values are serialized; the model is not versioned.** Nothing on disk records which occlusion model produced a chunk's `LightData`, so without an explicit version bump an upgraded client silently mixes old and new lighting per chunk. (Executor verifies the exact world-version constant — the grep for it returned nothing under `Serialization/`/`Data/`.)                       | VO-7         |

---

## 4. Decisions

### D1 — Shape source of truth

**Option A — new `VoxelShape` / `hasDirectionalOpacity` descriptor (rejected).**
This is what `LIGHTING_SYSTEM_OVERVIEW.md` sketches and what Starlight does.

- ✅ Independent of the collision system; could express occlusion that differs from collision.
- ❌ Second rotation-aware shape descriptor in one codebase — the exact "second-sibling twin"
  anti-pattern. Two sources of truth for "what shape is this block" *will* drift.
- ❌ Needs its own authoring UI, its own rotation path, its own tests. Large.
- ❌ Discards `NS-4`'s 26 baselines, which already prove the rotation path correct.

**Option B — derive occlusion from the existing `BlockCollisionBounds` (✅ **CHOSEN**).**

- ✅ One descriptor, one authoring surface, one rotation path — already shipped and suite-guarded.
- ✅ Occlusion becomes pure arithmetic on a rotated AABB (§2.3), no new data model.
- ✅ Physics, placement, ray, lighting, and AO agree on block shape *by construction*.
- ⚠️ Couples lighting to the collision model: a block whose collision volume intentionally differs
  from its visual volume would light "wrong". Accepted — no such block exists today, and the v2 escape
  hatch (authored override) is in §7.
- ⚠️ Inherits the single-AABB limitation. Accepted and stated as a non-goal (§1.2).

### D2 — Occlusion representation

**Option A — binary (occludes / does not) (rejected).** Simplest and cheapest, but throws away the
coverage fraction, so a slab's side faces would have to round to either "full blocker" (today's bug,
just directional) or "free" (light leaks along the slab plane). It also cannot express the ±Y half
case in §2.3's vertical row, which is precisely the requested behaviour.

**Option B — graded coverage folded into the existing opacity cost (✅ **CHOSEN**).**
`cost(block, meta, d) = max(1, round(opacity × occlusionFraction(block, meta, d)))`, with a fully
un-occluding face costing the air minimum of 1.

- ✅ Reuses `Attenuate`'s existing shape (`max(0, source − cost)`) — the formula's *structure* is
  unchanged, only the cost's derivation.
- ✅ Degenerates exactly to today's behaviour for full cubes (coverage 1 on every face → `opacity`),
  which is what makes a "no behaviour change for full blocks" prove-red possible.
- ✅ Gives the slab's side faces `15 × 0.5 → 8`, a visible but non-black attenuation.
- ⚠️ Rounding must be pinned once and shared by engine and oracle, or they diverge (F7).

### D3 — Composing the two faces of a traversal

Light crossing A→B exits A through A's `+d` face and enters B through B's `−d` face. Today only the
destination's opacity is charged ("charged the destination's opacity on entry",
`LightAttenuation.cs:20-23`).

✅ **CHOSEN:** `cost = max(exitCost(A, +d), entryCost(B, −d))`, i.e. the more occluding of the two
faces governs. Rejected alternative — summing the two — double-charges two adjacent slabs and makes
a slab corridor far darker than either slab alone; rejected alternative — keeping destination-only —
lets light escape *out* of a sealed slab box through the slab's own solid face.

### D4 — Migration strategy

✅ **CHOSEN:** bump the world version and **relight** affected chunks on load; the on-disk *format*
is untouched. Rejected: a value-rewriting migration (a migration cannot recompute a BFS without the
neighbourhood, so it would have to relight anyway) and doing nothing (F9 — silently mixed models).

### D5 — AO occlusion weighting

✅ **CHOSEN:** replace `SampleNeighborLight`'s boolean `isOpaque` out-parameter with a coverage
fraction, and weight both the substituted-darkness term and the diagonal-skip test by it. The
diagonal term is skipped only when both side coverages are ≥ 1 (preserving today's behaviour for
full cubes exactly). Executor confirms the exact blend before writing baselines — this is the one
decision whose *visual* outcome needs user sign-off (VO-5).

---

## 5. Phased implementation plan

### Universal regression gate (applies to every phase)

- `dotnet build "Assembly-CSharp.csproj"` **and** `dotnet build "Assembly-CSharp-Editor.csproj"`
  (or one Rider `build_solution_start`, which covers both).
- Suites: **Validate Lighting Engine**, **Validate Meshing**, **Validate Physics Solver**
  (`NS-4` — every phase touching the bounds path), and **Validate All** before closing a phase.
- The stale-editor-code gotchas apply in full: a new `.cs` file needs `AssetDatabase.Refresh()`
  before `dotnet build` reports truthfully, and the reliable readiness gate is the **DLL timestamp**,
  not `IsCompiling`. When a menu-suite result contradicts the analysis, re-run the scenario inline via
  `Unity_RunCommand` — that never gives a false green on a stale build.
- **Serialization tripwire (every phase):** zero on-disk *format* change. Only VO-7 may touch the
  world version, and if any other phase finds it wants a format change — stop, invoke
  `serialization-migration`, and treat it as a scope change.

| Phase    | Scope                                                        | Effort | Depends on   |
|----------|--------------------------------------------------------------|--------|--------------|
| **VO-0** | Probe: evidence for the model's assumptions                  | 🟢     | —            |
| **VO-1** | Burst-safe bounds mirror + shared occlusion utility          | 🟢     | VO-0         |
| **VO-2** | Harness + oracle support for partial blocks (suite-only)     | 🟢     | VO-1         |
| **VO-3** | Directional occlusion in the BFS                             | 🔴     | VO-2         |
| **VO-4** | Directional cross-chunk support / veto                       | 🔴     | VO-3         |
| **VO-5** | Fractional AO occlusion                                      | 🟡     | VO-1         |
| **VO-6** | Sub-block face light sampling (closes Bug M01)               | 🟡     | VO-1 (VO-3 for the general case — see packet) |
| **VO-7** | World-version bump + relight migration                       | 🟡     | VO-3, VO-4   |

**Minimal standalone-value set:** VO-0 → VO-1 → VO-5 → VO-6 delivers the *visual* fix (AO stops
max-darkening, sub-block faces sample correctly) without touching the BFS — **confirmed viable by
VO-0(c)**, which cleared VO-6's dependency on VO-3. It leaves the motivating case (light through a
vertical slab's open half) unfixed — that needs VO-3 — and its sub-block face light is exact only
for sky-exposed slabs (VO-6 packet).

---

### VO-0 — Probe (🟢, no behavior change) · ✅ **EXECUTED 2026-08-07**

- **Scope (as executed):** no production instrumentation was needed — (a), (b) and (d) are static
  data reads and (c) is a harness question, so all four ran through `Unity_RunCommand` against the
  real `BlockDatabase.asset` and `LightingTestWorld`. Recorded: (a) how many block types have
  `HasCustomBounds` true (the population this plan affects); (b) the authored
  `collisionBounds.min/max` of `Stone Half Slab`, confirming §2.3's worked table; (c) whether a
  sky-exposed opaque cell stores a usable surface stamp (`LIGHTING_SYSTEM_OVERVIEW.md:247` says
  opaque voxels "receive surface light but never propagate it onward" — VO-6's fallback depends on
  this); (d) the version anchors for VO-7 (F9).
- **Ordering:** first. Every later phase cites its results.
- **Prove-red:** none (read-only probe) — lean on the regression gate.
- **Acceptance:** ✅ results recorded as the dated `**Amended:**` line above. No build or suite run
  was required, since nothing was changed.
- **Testability gain:** turns four assumptions in this doc into recorded evidence, and resolves §8
  open question 1.
- **Doc-sync:** the `**Amended:**` line only. ✅ done.
- **Serialization:** none.

### VO-1 — Burst-safe bounds mirror + shared occlusion utility (🟢, no behavior change)

- **Precondition:** VO-0(a)/(b) recorded. If the slab's authored bounds are *not* a clean half-cell,
  STOP and re-derive §2.3's table before proceeding.
- **Scope:** add bounds fields to `BlockTypeJobData` (mirroring `BlockCollisionBounds`, populated in
  `JobDataManagerFactory.Create` alongside `customMeshIndex`); add a new Burst-safe
  `Jobs/BurstData/BurstOcclusionUtility` implementing §2.3's touches/coverage arithmetic over a
  rotated AABB, sharing `BurstCustomMeshRotationUtility.GetRotationMatrix`. Re-express the managed
  `BlockCollisionBoundsUtility.GetRotatedBounds` in terms of the new shared core so there is exactly
  one rotation-to-AABB implementation (heuristic: consolidate, do not mint a twin). **Does NOT**
  change any caller's behaviour — nothing consumes the new occlusion function yet.
- **Ordering:** before VO-2/VO-3/VO-5.
- **Prove-red:** `NS-4` is the guard that the collision refactor is behaviour-preserving — sabotage
  the shared rotation core (e.g. transpose the matrix) and confirm `NS-4` baselines go red and
  **only** those, then restore. New unit baselines in the meshing or a new occlusion suite: assert
  §2.3's six worked rows for the slab across all 24 `Facing6Roll2` orientations, plus a full-cube
  control asserting coverage 1 on all six faces (so nothing passes vacuously).
- **Acceptance:** universal gate. No in-game step (no behaviour change).
- **Testability gain:** "what does this block occlude in direction d" becomes a pure, unit-testable
  function callable from Burst — the precondition for every later phase.
- **Doc-sync:** `SUB_VOXEL_COLLISION_SYSTEM.md` §3.2 gains a note that the rotation core is now
  shared with lighting; `DATA_STRUCTURES.md` if `BlockTypeJobData`'s layout is documented there.
- **Serialization:** none — `BlockTypeJobData` is built at load from the database, never persisted.

### VO-2 — Harness + oracle support for partial blocks (🟢, suite-only)

- **Scope:** add a partial-block entry to the lighting harness palette (`TestBlockPalette`) and teach
  `LightingOracle` the directional cost. **Author behaviour-pinning baselines, not formula
  restatements** (F7): probe-reaches / probe-does-not-reach assertions around a slab in each
  orientation, including the §1.2 motivating pit. Claim numbers from the lighting suite tip
  (**B100** → B101+).
- **Ordering:** before VO-3 — the baselines must exist and be *red* against the old engine for the
  directional cases before the engine changes.
- **Prove-red:** inherent — the new baselines are written against the target behaviour and start red
  for the directional scenarios while staying green for full-cube ones.
- **Acceptance:** universal gate; harness-green is the whole verification (suite-only phase).
- **Testability gain:** the lighting suite can express partial blocks at all, which it cannot today.
- **Doc-sync:** `LIGHTING_VALIDATION_HARNESS_FIDELITY.md` — new palette entry + coverage note.
- **Serialization:** none.

### VO-3 — Directional occlusion in the BFS (🔴, behavior change — the F2 fix)

- **Scope:** extend `LightAttenuation.Attenuate` to a directional form per D2/D3 and migrate the
  16 `IsOpaque` sites in `NeighborhoodLightingJob.cs`. Each site must be classified first — some are
  "is this a valid propagation *source*" (which stays a whole-block question) and some are "can light
  cross this face" (which becomes directional). **Getting that classification wrong is the main risk
  in this arc.**
- **Ordering:** after VO-2. Coordinate with `chunk-lifecycle` invariants — invoke that skill before
  editing; the BFS's queue/flag contracts are unchanged by this phase and must stay so.
- **Prove-red:** VO-2's directional baselines flip green; full-cube baselines B1–B100 stay green
  throughout (that is the "no behaviour change for full blocks" claim from D2). Then sabotage:
  force `occlusionFraction` to 1 everywhere → only the new directional baselines red; force it to 0
  → sealed-box baselines red. Both restore clean.
- **Acceptance:** universal gate **+ in-game confirmation** — the §1.1 motivating pit lights through
  the vertical slab's open half, a slab floor still darkens the room below, and a sealed slab box
  stays dark. User sign-off required (visible lighting change).
- **Testability gain:** occlusion becomes a property the suite can vary per face.
- **Doc-sync:** `LIGHTING_SYSTEM_OVERVIEW.md` — rewrite "Conditionally Opaque Blocks" from "not
  applicable" to the implemented model (F3); update the §1.3 propagation rules and the §3.4
  data-model gotcha note about opaque neighbours.
- **Serialization:** format unchanged; **values change** — VO-7 owns the consequence. Do not ship
  VO-3 to users without VO-7.

### VO-4 — Directional cross-chunk support / veto (🔴, behavior change)

- **Scope:** `CrossChunkLightModApplier`'s `InChunkSunlightSupport` / `CrossChunkSunlightSupport` /
  `PullBackClaimStillSupported` currently exclude "fully-opaque neighbours" as non-propagating; that
  test becomes directional. This machinery has a documented live-lock history (Bugs 11/13/14) — read
  `LIGHTING_SYSTEM_OVERVIEW.md` §3.4/§3.7 in full before editing.
- **Ordering:** immediately after VO-3; VO-3 is not shippable without it (a directional BFS with a
  boolean veto can oscillate).
- **Prove-red:** baselines B48/B49 and B56–B59 are the existing guards and must stay green. Add a
  partial-block seam scenario and prove it red before the fix.
- **Acceptance:** universal gate + in-game confirmation at a chunk seam with slabs on both sides,
  plus a soak to rule out oscillation (the Bug 13 failure mode was a period-2 live-lock, not a wrong
  value — a single frame's screenshot cannot see it).
- **Testability gain:** seam behaviour for partial blocks becomes assertable.
- **Doc-sync:** `LIGHTING_SYSTEM_OVERVIEW.md` §3.4.
- **Serialization:** none beyond VO-3's.

### VO-5 — Fractional AO occlusion (🟡, behavior change — the F8 fix)

- **Precondition:** D5's blend confirmed with the user.
- **Scope:** `MeshGenerationJob.SampleNeighborLight` returns a coverage fraction instead of a bool;
  `SampleCorner` weights the darkness term and the diagonal-skip test by it;
  `CalculateCornerLights`'s `directOpaque` branch likewise. Full cubes must produce **bit-identical**
  output.
- **Ordering:** independent of VO-3/VO-4 (needs only VO-1) — can land earlier for partial visual value.
- **Prove-red:** meshing B11 (uniform smooth-light values) and every standard-cube baseline stay
  green — that is the bit-identical claim. New baselines: a partial block adjacent to a probe face
  darkens it *less* than a full cube does and *more* than air does (a strict ordering assertion, not
  a predicted constant — avoids the A4 trap MH-3 warns about). Positive control: full-cube-vs-air
  must already differ, or the ordering is vacuous.
- **Acceptance:** universal gate + in-game visual confirmation. **User sign-off** (visual change).
- **Testability gain:** extends MH-3 past its documented "uniform fields only" limit toward the
  distinct-per-corner case.
- **Doc-sync:** `SMOOTH_AND_RGB_LIGHTING.md` AO section; `MESHING_VALIDATION_HARNESS_FIDELITY.md`
  MH-3 entry.
- **Serialization:** none (mesh output is not persisted).

### VO-6 — Sub-block face light sampling (🟡, behavior change — the F1 fix, closes Bug M01)

- **Precondition:** ✅ **satisfied by VO-0(c)** — a sky-exposed opaque cell stores a usable surface
  stamp (measured: sky 15 on the topmost opaque block, 0 when buried), so sampling the own cell does
  **not** render slabs black and the trap recorded in `MESHING_BUGS.md` Bug M01's "Fix ordering" is
  cleared. VO-6 may therefore land **before** VO-3, which is what makes the minimal standalone-value
  set viable. Know the limit of that: the stamp is the light the *sky column* delivered to the
  surface, so VO-6-before-VO-3 is exact for sky-exposed slabs (the reported screenshot) and an
  approximation for slabs lit indirectly or by blocklight. VO-3 upgrades it to a properly propagated
  value; do not describe VO-6 alone as closing the general case.
- **Scope:** in `GenerateCustomBlockMesh_SchemaAware`, derive the light-sampling cell from the face's
  actual position rather than the block boundary —
  `sampleCell = floor(pos + rotatedFaceCentroid + 0.5 · rotatedNormal)` degenerates to today's
  `pos + rotatedOffset` for boundary faces and yields `pos` for mid-plane faces. Apply to the legacy
  custom-mesh path (`GenerateCustomBlockMesh_Legacy`) too, or state explicitly why not.
- **Ordering:** after VO-3.
- **Prove-red:** **`KM01a` flips green** — it is already red on record, so this phase gets its
  prove-red for free. Every boundary-face baseline stays green (that is the "degenerates correctly"
  claim). Promote `KM01a` → a baseline only after in-game confirmation, per
  `validation-driven-bugfix`.
- **Acceptance:** universal gate + in-game confirmation on the reported four-slab pit screenshot.
- **Testability gain:** closes the meshing suite's custom-mesh light blind spot.
- **Doc-sync:** `SMOOTH_AND_RGB_LIGHTING.md` §2.5.2 — correct the false "correctly handles sub-block
  geometry" claim (F4). Archive Bug M01 via `archive-fixed-bug` after confirmation.
- **Serialization:** none.

### VO-7 — World-version bump + relight migration (🟡, behavior change)

- **Precondition:** VO-3 + VO-4 landed.
- **Scope:** bump the world version and flag chunks saved under the old model for relight on load,
  through `AOT_WORLD_MIGRATION_SYSTEM`. Invoke `serialization-migration` before writing any of it.
- **Ordering:** last. Ships with VO-3/VO-4 as one user-visible release.
- **Prove-red:** a world saved pre-bump loads and relights to the same field a freshly-generated
  world produces (round-trip equality). Prove the tripwire works by loading a pre-bump save *without*
  the relight flag and confirming the field differs.
- **Acceptance:** universal gate + in-game load of a genuinely old save + a save/quit/reload cycle.
- **Testability gain:** the "which lighting model produced this chunk" question becomes answerable.
- **Doc-sync:** `AOT_WORLD_MIGRATION_SYSTEM.md` migration table;
  `INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md` version history.
- **Serialization:** **this is the phase that owns it.** Format unchanged; version bumped; relight
  path added.

---

## 6. Constraint compliance

| Constraint (CLAUDE.md)                        | How this design satisfies it                                                                                                                             |
|-----------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are bit-packed `uint`, no per-voxel objects | Nothing is added per voxel. Occlusion is derived from *block-type* data + the existing metadata byte already in the packed `uint`.                      |
| `Assets/Scripts/Jobs/` is 100% Burst-compatible | VO-1's occlusion utility is a static struct-free function over `float3`/`float3x3` using only `Unity.Mathematics`; the bounds mirror is blittable floats. |
| Sub-chunk (section) meshing                   | Untouched — no phase changes section partitioning.                                                                                                          |
| Async BFS flood-fill lighting                 | VO-3 changes the *cost function*, not the queue/flag/scheduling contracts. `chunk-lifecycle` invariants explicitly preserved (VO-3 ordering note).           |
| Region-based binary serialization             | Format unchanged in every phase; VO-7 bumps a version and relights. No `BinaryFormatter`/JSON anywhere.                                                      |
| No LINQ / GC allocations in hot paths         | The occlusion function is allocation-free arithmetic; the rotation matrix comes from the existing precomputed LUTs. `GetRotatedBounds`'s existing inline-8-corner style (no arrays) is preserved. |
| `BlockIDs` constants, never raw IDs           | No production code references block IDs; test palettes keep their documented test-local-index exemption.                                                     |

---

## 7. Extension roadmap

| Version | Item                                                                 | Notes                                                                                       |
|---------|----------------------------------------------------------------------|---------------------------------------------------------------------------------------------|
| v2      | Compound (multi-AABB) occlusion for stairs / L-shapes / wedges       | **Owned by `VQ-4`** — this plan interlocks only. VO-1's utility should take a bounds *list* shape internally so VQ-4 does not have to re-cut the seam. |
| v2      | Authored per-face occlusion overrides                                | Escape hatch for a block whose visual and collision volumes intentionally differ (D1's accepted risk). |
| v2      | Directional occlusion for fluids                                     | Fluid surfaces have their own height model; would need its own coverage derivation.          |
| v3      | `FLAG_HAS_SIDED_TRANSPARENT_BLOCKS`-style queue flag                 | Starlight's optimization — only pay the directional check when a partial block is in range. Measure first (`perf-benchmark`); do not pre-optimize. |

---

## 8. Open questions

1. ~~**Does an opaque partial block's cell store a usable surface stamp today?**~~ — ✅ **RESOLVED
   2026-08-07 by VO-0(c): yes.** Measured sky 15 on a sky-exposed opaque surface, 0 when buried.
   VO-6 is unblocked from VO-3, with the sky-exposed-only caveat recorded in its packet.
2. **What is the correct rounding in D2's cost formula?** `round` is written here as a placeholder;
   the executor pins one rule and shares it between `LightAttenuation` and `LightingOracle` in the
   same commit (F7 is the failure mode if they diverge).
3. **Should `opaqueCount` (`ChunkData.cs:880`, the section meshing optimization) count partial
   blocks?** It currently uses `IsOpaque`. Counting them risks a section being treated as fully
   solid when it is not; not counting them is safe but may cost meshing throughput. Executor decides
   with a `perf-benchmark` measurement if it turns out to matter.

---

## Document History

* **v1.0** - Initial design
* **v1.1** - VO-0 executed (no production code needed): blast radius is one block type, §2.3's bounds table confirmed, surface stamp confirmed (resolves open question 1 and unblocks VO-6 from VO-3), VO-7 version anchors pinned

---

**Last Updated:** 2026-08-07  
**Next Review:** when VO-1 starts
