# Directional Per-Face Voxel Occlusion (VO-*)

**Version:** 1.7  
**Date:** 2026-08-08  
**Status:** Proposed design — VO-0…VO-4 implemented and confirmed in game; VO-5…VO-6 pending; VO-7 descoped.  
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production)

> The engine gained partial blocks (`Stone Half Slab`) without the lighting model gaining a notion
> of them, so a half slab is authored `opacity = 15` and behaves as a *full* light blocker: it stops
> sky light entirely and contributes maximum ambient-occlusion darkening from every corner it
> touches. **The single most important decision here is that this plan does NOT introduce a new
> voxel-shape descriptor** — a rotation-aware per-block shape model already exists
> (`BlockCollisionBounds` + `Helpers.BlockCollisionBoundsUtility`), and per-face
> occlusion is derivable from it arithmetically. (v1.0 of this doc called that model "suite-guarded by
> `NS-4`"; VO-1's prove-red disproved it — see **F10**. It is guarded now, by the Occlusion suite this
> arc added.) The work is therefore mostly *plumbing an existing
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
- **(d) Version anchors** (gathered for VO-7, which was later **descoped** — kept because its tripwire
  depends on them). The chunk binary version is `ChunkSerializer.CURRENT_CHUNK_VERSION = 7`
  (`Assets/Scripts/Serialization/ChunkSerializer.cs:31`); the *world* version ladder is separate and its
  highest registered step is `Migration_v12_to_v13_PlayerChunkRelativePosition`, so a relight step would
  be `Migration_v13_to_v14_*` if the VO-7 tripwire ever fires.

**Relationship to other documents:**

- [`LIGHTING_SYSTEM_OVERVIEW.md`](../Architecture/LIGHTING_SYSTEM_OVERVIEW.md) — supplies the BFS
  propagation rules this plan modifies; its "Conditionally Opaque Blocks" section predicted this work
  and was stale (it said no partial blocks exist). **VO-3 rewrote it** to document the implemented
  binary per-face model, the `IsFullyOpaqueCell` source-guard change, and the full-cube equivalence.
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
  — VO-2 closed its gap **B9** (no partial block, no metadata authoring); see that entry for why these scenarios avoid oracle comparison.
- [`AOT_WORLD_MIGRATION_SYSTEM.md`](../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md) — would have hosted
  VO-7's relight; **no longer touched by this arc** (VO-7 descoped 2026-08-08).

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

| Orientation                                | Face   | Touches | Coverage | Result                                          |
|--------------------------------------------|--------|---------|----------|-------------------------------------------------|
| Identity (`meta 0x00` — facing South, roll 0) | −Y     | yes     | 1.0      | Full blocker — slab floor still blocks daylight |
| Identity                                   | +Y     | no      | 0        | Open (the mid-plane face)                       |
| Identity                                   | ±X, ±Z | yes     | 0.5      | Half                                            |
| **Vertical** (`meta 0x03` — facing Bottom, roll 0) | −Z     | no      | 0        | **Open — the motivating case**                  |
| Vertical                                   | +Z     | yes     | 1.0      | Full blocker                                    |
| Vertical                                   | ±Y     | yes     | 0.5      | Half — partial light propagates *downward*      |

The vertical row is the whole reason this plan exists: it is unreachable by any scalar opacity value.

> **Corrected 2026-08-07 (VO-1).** v1.0 of this table labelled the identity row "Upright (`facing=Top`)".
> That label was wrong — under `Facing6Roll2` it is facing **South (0)** that is the identity matrix, and
> `facing=Top` is a different rotation. The coverage *values* were correct and are now **measured, not
> derived**: every row above is asserted by occlusion-suite baselines **B1** (identity) and **B2**
> (vertical), and the remaining 22 orientations by **B4**'s structural invariant.

### 2.4 Serialization boundary

`Serialization/ChunkSerializer.cs` persists `ushort[] LightData` per section, with a compact
classification path and a v8→v9 migration precedent
(`Migration_v8_to_v9_LightDataSerialization.cs`). Therefore:

- The **format** does not change — still one `ushort` per voxel.
- The **values** do: worlds saved under the old model carry light computed with boolean occlusion.
- Consequence: this would need a **relight**, not a format migration — but see D4, superseded 2026-08-08.

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
| F10 | **`NS-4` does not guard the collision rotation.** Discovered by VO-1's prove-red: with `math.transpose` applied to the shared rotation core, all **26** Physics Solver baselines stayed green. The plan (v1.0) had asserted `NS-4` was the guard that a collision-bounds refactor is behaviour-preserving; it is not — none of its scenarios distinguish a rotated custom-bounds volume from its inverse. This is a pre-existing coverage gap in `NS-4`, not something VO-1 introduced, and it means *any* future change to the rotation path needs the occlusion baselines (or new `NS-4` scenarios) to be safe. | Recorded here; VO-1's guard chain compensates. A dedicated `NS-4` rotated-bounds scenario is filed as a follow-up in §7. |
| F11 | **The oracle's sky column seeding was over-migrated by VO-3, and nothing could see it.** `LightingOracle`'s downward column walk charged only each cell's *entry* cost through its top face — and a horizontal slab's top face is the open mid-plane, so the column walked straight through the solid half beneath it, leaving the oracle 1 level brighter than the engine under any slab ceiling. The engine was right (its column recalc uses whole-block opacity there). It went unnoticed because B101–B104 are probe-based by design (F7), so **VO-4's B105 is the suite's first oracle comparison containing a partial block at all**. Full-cube controls matched throughout, which is how the fixture was cleared before the spec was touched. | VO-4 (fixed: `ExitBlocked` on the bottom face) |
| F12 | **Sealing a partial-block light shaft never darkens the column beneath it.** Found while authoring B105; reproduces with **no chunk seam anywhere**, so it is not VO-4's subject. `IsLightObstructing` is `Opacity > 0`, so a slab already sits in the heightmap and sealing it never re-runs `RecalculateSunlightForColumn`; `PropagateDarkness` cannot help either, because a flat 15 column has no decrement chain. Controls pin it to partial blocks: a Glass shaft (full cube, opacity 0, equally undimmed column, *not* light-obstructing) and a Water shaft both darken correctly. This makes VO-3's recorded "the field is correct; the heightmap merely stays conservative" true for placement and **false for removal**. | Filed as `LIGHTING_BUGS.md` **Bug 21**; **root fix landed 2026-08-08** (user chose it over the narrower trigger-only option): `LightAttenuation.ObstructsSkyColumn` replaces `IsLightObstructing` at every heightmap site, **plus** a second part the harness caught — `ModifyVoxel`'s recalculation trigger fired only on an opacity change, which sealing a slab by rotation does not produce |
| F9 | ⚠️ **CLOSED-AS-WONTFIX 2026-08-08 (see VO-7).** **Light values are serialized; the model is not versioned.** Nothing on disk records which occlusion model produced a chunk's `LightData`, so without an explicit version bump an upgraded client silently mixes old and new lighting per chunk. (Executor verifies the exact world-version constant — the grep for it returned nothing under `Serialization/`/`Data/`.)                       | ~~VO-7~~ — descoped |

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

> **⚠️ REVERSED 2026-08-07 at the start of VO-2 (v1.3).** v1.0–v1.2 of this section chose the graded
> model below and rejected binary. **That was a correctness error**, caught by working the actual light
> values through before writing VO-2's baselines. The verdicts are now swapped; the arithmetic is in
> Option B. This is not a taste re-litigation — the previously-chosen model fails §1.1 goal 2.

**Option A — binary: a face occludes iff it is *fully* covered (✅ **CHOSEN**).**
`occludes(block, meta, d) = coverage(block, meta, d) >= 1 − ε`. A non-occluding face charges the air
minimum of 1; an occluding face charges the block's `opacity` exactly as today.

- ✅ **Delivers §1.1 goal 2.** The vertical slab's ±Y faces have coverage 0.5, so they do not occlude:
  light passes down through the cell at air cost, and the cell can still qualify for rule 4's
  vertical sky-column shortcut (`LIGHTING_SYSTEM_OVERVIEW.md:57`).
- ✅ **Degenerates to today bit-identically for full cubes** — coverage is 1 on all six faces, so every
  face occludes and the cost is `opacity`, unchanged. This is what makes the "no behaviour change for
  full blocks" prove-red possible.
- ✅ Keeps the upright slab floor blocking daylight (its −Y coverage is 1.0).
- ✅ Matches Starlight's `VoxelShape.faceShapeOccludes()`, which
  `LIGHTING_SYSTEM_OVERVIEW.md` §"Conditionally Opaque Blocks" already names as the model to adopt.
- ⚠️ Light passing *along* a slab plane is all-or-nothing rather than dimmed. Accepted: a single
  scalar per cell cannot represent a partially-open cross-section, and the visual cost of that
  approximation is far smaller than the alternative (below).

**Option B — graded coverage folded into the opacity cost (rejected).**
`cost = max(1, round(opacity × coverage))`.

- ❌ **Fatal: it does not deliver the motivating case.** Worked through for a 2-deep shaft capped by a
  vertical slab, sky 15 above: entering the slab cell costs `max(1, round(15 × 0.5)) = 8` → cell = 7;
  exiting downward costs 8 again → **0**. The pit stays dark. Because the cell is no longer 15, the
  vertical sky-column rule cannot rescue it either.
- ❌ The flaw is conceptual, not a tuning problem: one scalar per cell cannot represent "half the
  cross-section is open", so *any* positive cost compounds across the two face crossings a traversal
  makes. Lowering the coefficient only moves the depth at which light dies.
- ⚠️ Would also have needed a rounding rule pinned identically in engine and oracle, or they diverge (F7).

**Coverage is still graded where grading is the right question.** `GetFaceCoverage` keeps returning the
fraction; the BFS thresholds it (this decision), while **VO-5's ambient occlusion consumes it directly**
(D5). Light transport asks "can photons get past"; AO asks "how much of this corner is visually
blocked". VO-1's utility serves both unchanged.

### D3 — Composing the two faces of a traversal

Light crossing A→B exits A through A's `+d` face and enters B through B's `−d` face. Today only the
destination's opacity is charged ("charged the destination's opacity on entry",
`LightAttenuation.cs:20-23`).

✅ **CHOSEN (restated for D2's binary model, 2026-08-07):** a traversal is blocked by whichever of the
two faces occludes, and charges that block's opacity:

```
occA = occludes(A, +d) ? opacity(A) : 0        // A's exit face
occB = occludes(B, −d) ? opacity(B) : 0        // B's entry face
cost = max(1, max(occA, occB))
```

Rejected — summing the two — double-charges two adjacent slabs and makes a slab corridor far darker
than either slab alone. Rejected — keeping destination-only — lets light escape *out* of a sealed
slab box through the slab's own solid face.

> **Full-cube equivalence needs care (executor must verify).** For full cubes every face occludes, so
> this reduces to `max(1, max(opacity(A), opacity(B)))`, whereas today's rule is destination-only:
> `max(1, opacity(B))`. These differ when the **source** is a non-opaque attenuating block — water
> (opacity 2) propagating into air would go from cost 1 to cost 2, which would move existing lighting
> baselines. Note `PropagateLight` early-returns for *opaque* sources, so only the semi-transparent
> range is affected. **VO-3 must either restrict the exit term to blocks with custom bounds, or prove
> by baseline that no semi-transparent full block regresses.** Resolve this before touching the BFS —
> it is the likeliest source of an accidental behaviour change in the whole arc.

### D4 — Migration strategy

~~✅ **CHOSEN:** bump the world version and **relight** affected chunks on load~~ — **SUPERSEDED
2026-08-08: do nothing.** The "doing nothing" option was rejected here on the strength of F9's
mixed-model risk; that risk turned out not to exist for this project (no released worlds, and stale
light self-heals on block update). The full reasoning and its expiry condition are in the VO-7 packet.
The on-disk *format* was never going to change either way, which is why this is a scope removal and not
a redesign.

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
- **Serialization tripwire (every phase):** zero on-disk *format* change **and no version bump** (VO-7
  descoped). If any phase finds it wants either — stop, invoke `serialization-migration`, and treat it
  as a scope change.

| Phase    | Scope                                                        | Effort | Depends on   |
|----------|--------------------------------------------------------------|--------|--------------|
| ~~**VO-0**~~ | ✅ Probe: evidence for the model's assumptions            | 🟢     | —            |
| ~~**VO-1**~~ | ✅ Burst-safe bounds mirror + shared occlusion utility    | 🟢     | VO-0         |
| ~~**VO-2**~~ | ✅ Harness support for partial blocks (suite-only)        | 🟢     | VO-1         |
| ~~**VO-3**~~ | ✅ Directional occlusion in the BFS (awaiting in-game)    | 🔴     | VO-2         |
| ~~**VO-4**~~ | ✅ Directional cross-chunk support / veto                     | 🔴     | VO-3         |
| **VO-5** | Fractional AO occlusion                                      | 🟡     | VO-1         |
| **VO-6** | Sub-block face light sampling (closes Bug M01)               | 🟡     | VO-1 (VO-3 for the general case — see packet) |
| ~~**VO-7**~~ | ❌ World-version bump + relight — **DESCOPED**, see packet | —      | —            |

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

### VO-1 — Burst-safe bounds mirror + shared occlusion utility (🟢, no behavior change) · ✅ **EXECUTED 2026-08-07**

- **Precondition:** ✅ VO-0(a)/(b) recorded; the slab's authored bounds are a clean half-cell.
- **Scope (as executed):** the bounds mirror landed on `BlockTypeJobData` (`HasCustomBounds`,
  `BoundsMin`, `BoundsMax`) populated **in its own constructor**, not in `JobDataManagerFactory` as
  the plan guessed — the constructor already receives the `BlockType`, so no factory change was
  needed. New `Jobs/BurstData/BurstOcclusionUtility` implements §2.3's touches/coverage arithmetic
  (`RotateLocalBounds`, `GetFaceCoverage`, `GetBlockFaceCoverage`); the managed
  `BlockCollisionBoundsUtility.GetRotatedBounds` now delegates its 8-corner rotation to that core and
  only re-spaces the result. New suite `Assets/Editor/Validation/Occlusion/` (menu
  `Minecraft Clone/Dev/Validate Occlusion`, 5 baselines), registered in `ValidationSuiteRegistry`
  (`ExpectedSuiteCount` 17 → 18). Nothing consumes the coverage function yet.
- **Scope (original):** add bounds fields to `BlockTypeJobData` (mirroring `BlockCollisionBounds`);
  add a new Burst-safe
  `Jobs/BurstData/BurstOcclusionUtility` implementing §2.3's touches/coverage arithmetic over a
  rotated AABB, sharing `BurstCustomMeshRotationUtility.GetRotationMatrix`. Re-express the managed
  `BlockCollisionBoundsUtility.GetRotatedBounds` in terms of the new shared core so there is exactly
  one rotation-to-AABB implementation (heuristic: consolidate, do not mint a twin). **Does NOT**
  change any caller's behaviour — nothing consumes the new occlusion function yet.
- **Ordering:** before VO-2/VO-3/VO-5.
- **Prove-red (executed — the plan's prediction was WRONG, see F10):** the sabotage was
  `math.transpose` on the rotation inside `RotateLocalBounds`. Result:

  | Guard                              | Under sabotage | Note                                                                                         |
  |------------------------------------|----------------|-----------------------------------------------------------------------------------------------|
  | `NS-4` Physics Solver (26)         | **all green**  | Does **not** discriminate the rotation core — the plan's assumed guard does not exist (F10).   |
  | Occlusion `B5` (managed == core)   | green          | Both sides share the core, so an *agreement* test cannot see a core bug. Guards divergence only. |
  | Occlusion `B4` (structural)        | green          | one-full/one-empty/opposite is transpose-invariant.                                            |
  | Occlusion `B1` (identity)          | green          | Identity is its own transpose.                                                                 |
  | **Occlusion `B2` (vertical)**      | **RED**        | The only guard that caught it: faces 0/1 swapped (transpose = inverse rotation), exactly the expected signature. |

  Restored clean: 5/5 occlusion baselines green.  
  **The evidence that VO-1 is behaviour-preserving is therefore a chain, not `NS-4`:** B1/B2 pin the
  core's absolute output, and B5 pins managed == core. Do not weaken either half — dropping B2 for
  "B4 covers all 24 orientations" would silently remove the only real guard.
- **Acceptance:** ✅ universal gate — **Validate All: 416 baselines across 18 suites PASSED**. No
  in-game step (no behaviour change).
- **Testability gain:** "what does this block occlude in direction d" becomes a pure, unit-testable
  function callable from Burst — the precondition for every later phase.
- **Doc-sync:** `SUB_VOXEL_COLLISION_SYSTEM.md` §3.2 gains a note that the rotation core is now
  shared with lighting; `DATA_STRUCTURES.md` if `BlockTypeJobData`'s layout is documented there.
- **Serialization:** none — `BlockTypeJobData` is built at load from the database, never persisted.

### VO-2 — Harness support for partial blocks (🟢, suite-only) · ✅ **EXECUTED 2026-08-07**

- **Scope (as executed):** `TestBlockPalette` gained `HalfSlab` (id 11, `Count` 11 → 12) — opacity 15
  like production, `BlockCollisionBounds.BottomHalfSlab`, `Facing6Roll2`. `SetBlock`/`PlaceBlock`
  gained an optional `meta` parameter (default 0, so every pre-VO-2 call site is untouched); without
  it the harness could not express a slab's *orientation*, which is the entire variable under test.
  Scenarios in `LightingValidationSuite.PartialBlocks.cs`.
- **Taxonomy correction made during execution.** These were first written as four baselines
  (B101–B104), which turned `Validate All` red — a *baseline* that fails is by definition a regression.
  The scenario asserting behaviour the engine does not yet have belongs in the **known-bug** channel:
  it is now **`K20a`** (tagged Bug 20), expected-red, suite stays green, and it flips to a cyan "fix
  candidate" when VO-3 lands. The three that pass today stayed baselines **B101–B103**. Any future
  phase adding "assert the target behaviour" scenarios must make the same split.
- **Deviation from the original scope — `LightingOracle` was deliberately NOT changed.** The plan said
  to teach it the directional cost here; doing so would put the oracle a phase ahead of the engine and
  red every oracle comparison in any scenario containing a slab, isolating nothing. And per **F7** the
  oracle shares `LightAttenuation` with the engine, so it can never arbitrate this model anyway. The
  spec is therefore written down in the **baselines** (behaviour assertions), and the oracle changes in
  **VO-3** alongside the engine.
- **Ordering:** before VO-3. ✅
- **Prove-red (executed):** the red/green split is exactly as designed —

  | Scenario                       | Below the cap | Verdict                                  |
  |--------------------------------|---------------|------------------------------------------|
  | B103 open shaft (control)      | sky 15        | GREEN — fixture proven non-vacuous       |
  | B102 full cube cap (control)   | sky 0         | GREEN                                    |
  | B101 floor slab `0x00`         | sky 0         | GREEN — tripwire, must stay green        |
  | **K20a vertical slab `0x03`**  | **sky 0**     | **RED (expected) — Bug 20, green in VO-3** |

  The cap cell itself reads sky 15 in every case, re-confirming VO-0(c)'s surface stamp.
  Note B101 is green *today for the wrong reason* (the current model blocks in every direction); its
  value is entirely as a VO-3 tripwire against "fix Bug 20 by making slabs transparent".
- **Acceptance:** ✅ universal gate — **Validate All: 419 baselines across 18 suites PASSED**, with
  K20a reported as reproducing Bug 20 (expected).
- **Testability gain:** the lighting suite can express partial blocks and their orientation at all.
- **Doc-sync:** `LIGHTING_VALIDATION_HARNESS_FIDELITY.md` — new palette entry + `meta` API note.
- **Serialization:** none.

### VO-3 — Directional occlusion in the BFS (🔴, behavior change — the F2 fix) · ✅ **CODE COMPLETE 2026-08-07 — AWAITING IN-GAME CONFIRMATION**

**How D3's full-cube-equivalence risk was resolved.** The packet warned that a two-face `max()` cost would
regress semi-transparent full blocks (water into air: cost 1 → 2). Resolved by the first of the two options
it offered — **every new predicate short-circuits on `HasCustomBounds`**, so a full cube's path is
arithmetically identical to the pre-VO-3 rule and the destination-only charge is preserved. The
equivalence is structural, not measured-and-hoped: three predicates in `LightAttenuation`, each returning
the old value when `!HasCustomBounds`.

- `FaceBlocksLight(block, meta, face)` — opaque **and** coverage ≥ 1. Replaces the `IsOpaque` test at the
  traversal sites.
- `EntryOpacity(block, meta, face)` — authored opacity on a covered face, **0 (air)** on an uncovered one.
- `ExitBlocked(block, meta, face)` — partial blocks only; a full opaque cube is already stopped by the
  source guard, so this must not fire for one or the guard would apply twice.

Plus `BlockTypeJobData.IsFullyOpaqueCell` (`IsOpaque && !HasCustomBounds`) for the propagation-**source**
guards, which ask "does this cell hold only surface light" — a partial block does not, and must
re-propagate.

**Sites migrated (sunlight + RGB propagation).** `PropagateLight` and `PropagateLightRGB`: source guard,
per-direction exit test, and the neighbour opaque/attenuate branch. `LightingOracle` received the identical
change plus a metadata channel (`LightingTestWorld.GetBlockMeta`) — the oracle previously cached only block
ids and so could not evaluate an orientation-dependent spec.

**Deliberately NOT migrated here — deferred to VO-4:** the cross-chunk edge-check seeding, the removal
initiators, and the `CrossChunkLightModApplier` support/veto sites. They are the Bug 11/13/14/15 machinery
and belong with VO-4's soak.

**Results:** `K20a` flips to a cyan fix candidate (below a vertical slab: sky **0 → 14**); **Validate All:
all 419 baselines across 18 suites PASSED**. Prove-red: forcing `FaceBlocksLight` to `false` for partial
blocks (i.e. "slabs are transparent") reds **B101 and only B101**, with its authored diagnostic — restored
clean. The opposite sabotage needs no run: coverage-1-everywhere *is* the pre-VO-3 engine, measured in VO-2
as K20a red / B101–B103 green.

**Amended 2026-08-07 — first in-game report found a third site, and a weak assertion.** The user reported
the horizontal slab correct (straight to 0 — B101 holding in game) but the column under a *vertical* slab
decaying `15/14/13/…/0` instead of staying 15. Two whole-block tests had been left unconverted:

- `isVerticalSunlight` (the unattenuated downward sky-column rule) gated on
  `BlockTypeJobData.IsFullyTransparentToLight` — a whole-block `Opacity == 0` test that a slab fails, so
  the column resumed attenuating below it. **Fixed** with `LightAttenuation.IsTransparentThroughFace`.
- `IsLightObstructing` (`Opacity > 0`) still puts the **heightmap** at a vertical slab, so
  `RecalculateSunlightForColumn` PASS 1 stops force-lighting there. **Deliberately NOT changed** — with
  the rule above fixed, the BFS carries the undimmed column down anyway, so the field is correct; the
  heightmap merely stays conservative (fast path off, slow path right). Changing `IsLightObstructing`
  would touch `ChunkData` heightmap maintenance, generation, and the LI-2 band derivation, for no
  correctness gain. Executor: revisit only if profiling shows the lost fast path matters.

**The stricter definition matters.** `IsTransparentThroughFace` is "entry cost is **zero**", not "the face
does not occlude" — otherwise water (opacity 2, occludes nothing) would have started extending the
unattenuated column, a silent regression. Verified: water still dims `14/13/12/…`.

**Assertion strengthened.** `K20a` originally asserted only `sky > 0` below the slab. That was too weak —
it passed while the column decayed, which is why only in-game play caught it. It is now a **column-for-
column differential against an uncapped shaft** over a 6-deep shaft, which pins the degree without
restating the cost formula. Recorded in the scenario docstring as "do not weaken back".

✅ **CONFIRMED IN GAME 2026-08-07** — "it's now indeed 15 all the way down", with the horizontal slab still
blocking. Repro `K20a` promoted to permanent baseline **B104**.

**Design question raised and settled at confirmation.** Should a vertical slab dim the column at all, given
it physically blocks half the cross-section? **Decision: no — keep the undimmed column.** A voxel sky value
is *intensity at a point*, not *flux through the cell*: a point in the slab's open half has an unobstructed
line to the sky, exactly like a point in a doorway or beside a pillar. The decisive case is a **deep open
shaft** — a vertical slab at the top of a 20-block shaft would leave the bottom pitch black under any
per-step decay, because the slab's obstruction is *local* while a decay is *cumulative*. That is the same
flaw that sank the graded cost model in D2. The "half obstructed" intuition is real but belongs to **ambient
occlusion** (VO-5), which is local by nature and already has the 0.5 coverage available.

Two alternatives were considered and rejected:

- **A per-block authored flag** to opt blocks into decaying. Cost is negligible (`BlockTypeJobData` is
  already fetched per neighbour), so this was rejected on *coherence*, not performance: it is the
  "lie about your shape" override already deferred as a §7 v2 item, it has no principled default, and the
  shape already answers the question. If it is ever wanted, note the distinction needs **no new field** —
  `columnContinues = EntryOpacity == 0 && coverage >= 1` derives it from the shape alone, a one-`&&` change.
- **A one-time "light cut"** (lose N levels at the slab, then propagate undimmed). Rejected as genuinely
  requiring new per-column state: sky-light *removal* is authoritative through `RecalculateSunlightForColumn`,
  whose PASS 1 writes a literal 15 above the heightmap, and `PropagateDarkness` unwinds light by following
  exact `neighbor == old − cost` decrement chains. A column sitting flat below 15 has neither, and sky light
  has no spare bits for the extra state.

**Pre-existing limitation noted (not introduced here, not fixed here):** the same "obstruction is local but
decay is permanent" shape applies to **semi-transparent full blocks**, most visibly leaves. Leaves are
`opacity 1`, so `max(1, opacity)` makes them cost exactly what air costs *per step* — but they are
`IsLightObstructing`, so they break the unattenuated column, and **nothing can re-enter it**. Light below a
canopy therefore decays to 0 with distance even through clear air. Rarely visible in natural generation.
Fixing it needs the same new per-column state the light-cut idea needs, so it is recorded here rather than
scheduled.

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
- **Serialization:** format unchanged; **values change**. VO-7 was to own that consequence but was
  **descoped 2026-08-08** — there are no released worlds and stale light self-heals on any block update.
  See the VO-7 packet for the conditional tripwire.

### VO-4 — Directional cross-chunk support / veto (🔴, behavior change) · ✅ **EXECUTED + CONFIRMED IN GAME 2026-08-08**

✅ **CONFIRMED IN GAME 2026-08-08** — no flicker at a slab seam under a break/replace soak, which is the
check that matters here: the failure mode was a period-2 oscillation, not a wrong value. Repro `K20b`
promoted to permanent baseline **B106**. Committed as `9443d08c`.

**The live-lock, named precisely.** VO-3 taught `PropagateLight` to deliver through a partial block's open
half but left the veto's support scan whole-block, and a half slab is authored `opacity = 15`. So the BFS
feeds a seam voxel through a slab while the veto computes **zero** support for it: the Bug 12 initiator
fires, the removal applies, the BFS re-lights, and the pair cycles — the Bug 13 period-2 shape, reachable
through a slab. The governing invariant restored here is **support(neighbour → target, face) must equal
what `PropagateLight` would write**; five hand-written mirrors of that rule had drifted from the original.

**Site classification (done before any edit — the packet's own warning).** Ten surviving `IsOpaque` sites:

| Site | Question | Verdict |
|------|----------|---------|
| Bug 12 sky initiator; `PullBackDimmerCrossSeamStamp` neighbour + centre; Bug 18 RGB initiator | valid propagation participant? | → `IsFullyOpaqueCell` |
| `CheckEdgeVoxel` / `CheckEdgeVoxelRGB`, neighbour and centre arms | can light cross this face? | → `IsFullyOpaqueCell` + `ExitBlocked` / `FaceBlocksLight` / `EntryOpacity` |
| Column-recalc shadow caster (`:1144`) | does this cast a horizontal shadow? | **left whole-block, deliberately** |

The shadow-caster site is asymmetric: over-triggering only enqueues a redundant removal + re-propagate
(correct field, wasted work), while under-triggering leaves stale light that edge checks can never remove
(§3.7 reason 2). Conservative is the safe side there. Sites 1–4 have no safe side — too generous vetoes a
legitimate removal (stable over-bright), too stingy live-locks — which is why the mirror is now structural.

**`IsVerticallySkyLit` was the third, unlisted site.** It gated on whole-block `IsFullyTransparentToLight`,
so a voxel held at an undimmed 15 beneath a vertical slab did **not** count as sky-lit and the Bug 12
initiator would fire on it — while the veto's support model tops out at 14 and cannot defend a 15. It now
uses `IsTransparentThroughFace` on the same two faces `PropagateLight`'s vertical rule tests.

**API change.** `Func<ushort,bool> isBlockFullyOpaque` + `Func<ushort,(byte,byte,byte)> blockEmission`
collapsed into one `Func<ushort, BlockTypeJobData> getBlockData`, and the scalar `targetOpacity` became
`CrossChunkLightModApplier.TargetEntryCost` — `Flat(opacity)` (whole-block, bit-identical to before, and
what B49 varies) or `ForBlock(block, meta)` (directional). Making the distinction a *type* is what keeps a
future edit from silently reintroducing a whole-block answer.

**Results:** `K20b` flips to a fix candidate; **Validate All: all 421 baselines across 18 suites PASSED**,
including B48/B49 and B56–B59. Values verified inline against live assemblies (open-side slab source 11,
solid-side 0, opaque cube 0, open-face entry 11, covered-face entry 0, B49's flat differential 7/11).

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

### VO-7 — World-version bump + relight migration · ❌ **DESCOPED 2026-08-08 (user decision) — DO NOT IMPLEMENT**

**Why it was dropped.** F9's premise was that an upgraded client would silently mix old and new lighting
per chunk. That premise does not hold for this project's actual situation, which the owner confirmed:

- The engine has **no released worlds**. The only saves carrying pre-`VO-3` light are the developer's own
  local test worlds, so there is no population to migrate.
- Stale light is **self-healing in practice** — any block update in a chunk re-runs its lighting, and the
  owner confirmed affected chunks already re-lit correctly that way. The residue is limited to chunks that
  are never touched again.
- The remaining fix is a manual one-liner on a single local world, which is cheaper than a migration step
  plus its round-trip test, its doc-sync, and its permanent presence in the version ladder.

Building it anyway would add a migration step that no user will ever execute, and version ladders are
append-only — the cost is permanent.

**What replaces it:** nothing. `CURRENT_CHUNK_VERSION` stays 7 and the world ladder stays at v13.

> ⚠️ **Tripwire — the decision is conditional, not absolute.** It rests entirely on "no released worlds".
> If this engine ever ships, or a world that matters is saved before the arc completes, the reasoning
> expires and the relight becomes necessary again. The anchors are preserved in VO-0(d) above:
> `ChunkSerializer.CURRENT_CHUNK_VERSION = 7`, world ladder tops at
> `Migration_v12_to_v13_PlayerChunkRelativePosition`, so the step would be `Migration_v13_to_v14_*`.
> Re-open this phase — do not invent a new id — and route through `serialization-migration`.

---

## 6. Constraint compliance

| Constraint (CLAUDE.md)                        | How this design satisfies it                                                                                                                             |
|-----------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are bit-packed `uint`, no per-voxel objects | Nothing is added per voxel. Occlusion is derived from *block-type* data + the existing metadata byte already in the packed `uint`.                      |
| `Assets/Scripts/Jobs/` is 100% Burst-compatible | VO-1's occlusion utility is a static struct-free function over `float3`/`float3x3` using only `Unity.Mathematics`; the bounds mirror is blittable floats. |
| Sub-chunk (section) meshing                   | Untouched — no phase changes section partitioning.                                                                                                          |
| Async BFS flood-fill lighting                 | VO-3 changes the *cost function*, not the queue/flag/scheduling contracts. `chunk-lifecycle` invariants explicitly preserved (VO-3 ordering note).           |
| Region-based binary serialization             | Format unchanged in every phase; no version bump (VO-7 descoped). No `BinaryFormatter`/JSON anywhere.                                                        |
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
| —       | **Close the `NS-4` rotated-bounds gap (F10)**                        | Add a Physics Solver scenario that actually discriminates a rotated custom-bounds volume (e.g. land a body on a vertical slab and assert the rest height differs from the identity orientation). Owned by `SUB_VOXEL_COLLISION_SYSTEM.md` / `NS-4`, not by a VO phase — but every VO phase touching the rotation core is unguarded there until it exists. |

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
* **v1.7** - VO-4 code complete: the support/veto mirrors made directional via `TargetEntryCost` + `NeighborCanDeliver`, `IsVerticallySkyLit` found as a third unlisted site, shadow-caster site deliberately left whole-block; repro `K20b` flips green and baseline **B105** added; 421 baselines green. Two new findings — **F11** (the oracle's column seeding was over-migrated by VO-3; B105 is the suite's first partial-block oracle comparison) and **F12** (sealed partial-block shafts never darken — filed as Bug 21, NOT a VO-4 defect). AWAITING IN-GAME CONFIRMATION
* **v1.6** - VO-3 confirmed in game (repro `K20a` promoted to permanent baseline **B104**); the sky-column rule found still whole-block in play and fixed via `IsTransparentThroughFace`; undimmed-column question settled with rationale; **VO-7 DESCOPED** (no released worlds, stale light self-heals on block update) with a conditional tripwire; F9 closed as wontfix and D4 superseded
* **v1.5** - VO-3 code complete: directional occlusion in the sky + RGB propagation paths and the oracle; D3's full-cube-equivalence risk resolved by short-circuiting every predicate on `HasCustomBounds`; K20a fixed (sky 0 → 14), 419 baselines green, B101 prove-red confirmed. Cross-chunk sites deferred to VO-4. AWAITING IN-GAME CONFIRMATION
* **v1.4** - VO-2 executed: `TestBlockPalette.HalfSlab` + `meta` on `SetBlock`/`PlaceBlock`, baselines B101–B103 green and repro **K20a** red as designed (lighting harness gap **B9** closed); oracle deliberately left to VO-3
* **v1.3** - **D2 REVERSED** at the start of VO-2: binary per-face occlusion (Starlight's `faceShapeOccludes`) replaces the graded opacity cost, which was proven by worked arithmetic to leave the motivating pit dark; D3 restated for it, with a full-cube-equivalence warning VO-3 must resolve
* **v1.2** - VO-1 executed: Burst bounds mirror + shared `BurstOcclusionUtility` core + new Occlusion suite (5 baselines); §2.3's identity-row label corrected; new finding **F10** — `NS-4` does not guard the collision rotation, so VO-1's prove-red rests on occlusion `B2` alone

---

**Last Updated:** 2026-08-08  
**Next Review:** when VO-4 starts
