# DOTS / ECS Adoption Analysis

**Version:** 1.0  
**Date:** 2026-10-02  
**Status:** Draft — decision record. Terrain verdicts are settled; `EC-6` (dynamic entities) is deferred
behind the §6 triggers. Not scheduled.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Should the engine adopt Unity's Entities stack (Entities, Entities Graphics, Unity Physics) to solve
> load time, traversal spikes and scaling? **No for terrain — the engine stays "DOTS without
> Entities"** (Burst + Collections + Mathematics + jobs + BatchRendererGroup, which it already is and
> which community consensus recommends for voxel terrain). The deciding reasons are specific, not a
> general dislike of ECS: Entities Graphics requires URP Forward+ and DOTS-instancing shaders yet gives
> no batching win for unique per-section meshes; ECS job safety is per component *type*, so it cannot
> express per-section concurrency; streaming would be a constant stream of main-thread structural
> changes; and installing the package re-adds three modules the project deliberately pruned.
> **The one good fit — large crowds of dynamic entities (dropped items, projectiles, mobs) — is
> deferred (`EC-6`) until a design needs hundreds of them at once and the native chunk store
> (`ES-18a`) has shipped.**

**Audited:** 2026-10-02, at commit `376778fe` (branch `main`).
Repo facts verified in `Packages/manifest.json`, `Packages/packages-lock.json`, the core-package
`package.json` files bundled with the installed 6000.6.4f1 editor
(`Editor/Data/Resources/PackageManager/BuiltInPackages/`), the Entities Graphics source and bundled docs
there (`EntitiesGraphicsSystem.cs`, `requirements-and-compatibility.md`, `entities-graphics-performance.md`,
`known-issues.md`, `concepts-structural-changes.md`, `scheduling-jobs-dependencies.md`), the URP renderer
asset, and a grep of `Assets/` for any ECS usage. Unity's ECS status posts and community threads were
read on the web (URLs inline). **The `unity-api` MCP does not index the Entities package** — Entities
signatures below come from the bundled package source, not the MCP.

**Relationship to other documents:**

- [`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`](ENGINE_SCALING_PERFORMANCE_ROADMAP.md) — its §3 Option C
  (ECS / Entities Graphics) defers to this document for the full reasoning; `ES-18` (native chunk store)
  and `ES-20` (GPU-driven renderer) are the non-ECS answers this analysis endorses.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — `TG-4` delivered the
  "ECS/DOTS pattern" for block behavior *without* Entities, the precedent this analysis generalizes.
- [`../Archived/PERSISTENT_CHUNK_STORAGE_P2.md`](../Archived/PERSISTENT_CHUNK_STORAGE_P2.md) — the §5
  pin/defer/double-buffer rules ECS would *not* provide for chunk storage (§4 EC-1).
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the
  neighbor-predicate invariants ECS system ordering cannot express (§4 EC-2).

---

## 1. Question and scope

The roadmap rejected ECS in one line ("still one mesh per section + a whole engine-model migration").
This document replaces that line with a per-subsystem evaluation against Unity 6.6's actual Entities
stack, records what installing it would cost, and names the events that would reopen the decision.

Out of scope: rewriting this engine around ECS for reasons other than performance (authoring workflow,
modding), and multiplayer — which is listed as a reopen trigger (§6), not evaluated.

---

## 2. Current state

| Item                                                   | State                                                                                                                                                                                       | Source                              |
|--------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-------------------------------------|
| Entities / Entities Graphics / Unity Physics / Netcode | **Not installed.** All four ship inside the 6000.6.4f1 editor as **core packages, version 6.6.0** — installing is a manifest line, not a download                                           | manifest + bundled `package.json`   |
| Burst / Collections / Mathematics                      | 2.0.0 / 6.6.0 (core) / 1.4.0 — already the backbone of every job                                                                                                                            | manifest, lock file                 |
| ECS code in `Assets/`                                  | **None** (`Unity.Entities`, `IComponentData`, `ISystem`, `SystemBase`, `EntityManager`: zero hits); no `GetInstanceID` use either, so the InstanceID → `EntityId` deprecation costs nothing | grep                                |
| Jobs in use                                            | 12 job structs (9 `IJob`, 2 `IJobFor`, 1 `IJobParallelFor`), 74 native hash-map/queue/stream references                                                                                     | grep                                |
| Renderer                                               | URP 17.6 renderer `m_RenderingMode: 0` = **Forward**, not Forward+                                                                                                                          | `VoxelEngine-URP-Renderer.asset:68` |
| Block shaders                                          | Forward pass only; no `DOTS_INSTANCING_ON` variants                                                                                                                                         | the three block `.shader` files     |
| Physics                                                | Custom `VoxelRigidbody` against the voxel grid; no PhysX colliders                                                                                                                          | `Physics/VoxelRigidbody.cs`         |
| Orchestration                                          | Managed `World` (5 877 lines) + `WorldJobManager` (2 239)                                                                                                                                   | —                                   |
| Validation                                             | ~36 editor `Validate …` suites driving pure helpers and job structs directly                                                                                                                | `Assets/Editor`                     |
| Package policy                                         | `assetbundle`, `unityanalytics`, `unitywebrequest` deliberately removed (lean package set)                                                                                                  | memory + manifest                   |

---

## 3. Unity ECS in 6.6 — what is true today

- **Core packages since 6.4.** Entities, Collections, Mathematics and Entities Graphics version with the
  editor (6.4.0 → 6.5.0 → 6.6.0); Unity Physics and Netcode are present as 6.6.0 built-ins.
  ([changelog](https://docs.unity3d.com/Packages/com.unity.entities@6.4/changelog/CHANGELOG.html),
  [status Dec 2025](https://discussions.unity.com/t/ecs-development-status-december-2025/1699284),
  [status for 6.6](https://discussions.unity.com/t/ecs-development-status-for-unity-6-6/1724480))
- **"ECS for All" is in progress, not delivered.** The 64-bit `EntityId` replaced InstanceID (deprecated
  in 6.4); managed `IComponentData` and managed shared components are deprecated in 6.6 in favor of
  `UnityObjectRef<T>`. **GameObjects-as-entities and unified transforms are not shipped in 6.6.**
- **Entities Graphics** — "For URP, only Forward+ rendering path is supported" (bundled docs; the code
  only *warns* on Forward, `EntitiesGraphicsSystem.cs:900–920`). Built on BRG + DOTS instancing; a draw
  command renders instances sharing one Mesh and Material; Unity's own performance doc says it "might be
  slower … than rendering GameObject with SRP batcher" when there is little to batch.
  `DOTS_INSTANCING_ON` variants are never stripped; Android is Vulkan-only; single World only.
  `RegisterMesh(Mesh)` is managed and main-thread (`EntitiesGraphicsSystem.cs:2754/2785`).
- **Baking / SubScenes** convert editor-authored content; they do nothing for a seed-driven,
  region-saved procedural world.
- **Structural changes** (create/destroy entities, add/remove components, set shared components) are
  main-thread sync points; enableable components are the workaround.
- **Dependencies** are tracked per system and per component type — Unity documents the resulting
  over-waiting as a known issue — and **not at all through `NativeArray`s**.
- **Dynamic buffers** beyond the 128 B default internal capacity live in heap arrays outside the 16 KB
  archetype chunk; one 16³ `uint` section is 16 KB by itself.
- **Physics:** Unity Physics is collider-based; Havok is no longer bundled with Pro (support ended at
  6.3 LTS; [thread](https://discussions.unity.com/t/unity-ends-havok-physics-support-no-longer-included-with-unity-pro/1694786)).
- **Voxel precedent:** no shipped commercial voxel game documented on Entities was found — only
  prototypes ([dots-blockworld](https://github.com/sarkahn/dots-blockworld),
  [Voxelman](https://github.com/keijiro/Voxelman), a 2020
  [16³-chunk prototype](https://discussions.unity.com/t/a-voxel-game-prototype-in-unity-ecs/782075/42)
  reporting problems "with lots of chunks"). Forum consensus: an entity per voxel is a disaster, an
  entity per chunk is workable but barely fits; use per-chunk mesh data and BRG for terrain
  ([2025 thread](https://discussions.unity.com/t/looking-for-dots-advice-for-making-a-voxel-based-world/1618800)).

---

## 4. Per-subsystem verdicts

| ID       | Subsystem                                                     | ECS benefit                                                        | Cost / risk                                                               | Verdict                                           |
|----------|---------------------------------------------------------------|--------------------------------------------------------------------|---------------------------------------------------------------------------|---------------------------------------------------|
| **EC-1** | Chunk data storage                                            | Native memory, queries, inspector                                  | Rewrite storage + every reader; per-type safety; structural churn         | **REJECT** → `ES-18`                              |
| **EC-2** | Pipeline orchestration                                        | Automatic dependencies, ordered systems                            | ~8.1 k lines; re-prove lifecycle invariants; LP-8 + suites invalidated    | **REJECT** — borrow one pattern                   |
| **EC-3** | Section rendering (Entities Graphics)                         | Unity-maintained BRG path                                          | Forward+ switch, DOTS-instancing variants, still one `Mesh` per section   | **REJECT** → `ES-20`, BRG-direct first            |
| **EC-4** | Fluid / behavior tick                                         | None — TG-4 already Burst + parallel                               | Rewrite for nothing                                                       | **REJECT**                                        |
| **EC-5** | Lighting BFS                                                  | None                                                               | Cross-entity writes need ECBs or disabled safety; re-prove seam baselines | **REJECT** → `ES-18b`                             |
| **EC-6** | Future dynamic entities (items, projectiles, mobs, particles) | **Real** — thousands of identical instances, textbook `IJobEntity` | Package install, hybrid boundary                                          | **DEFER** until §6 triggers, then **PARTIAL**     |
| **EC-7** | Physics (Unity Physics)                                       | Nothing for terrain                                                | Per-section collider generation on every remesh                           | **REJECT** for terrain; **DEFER** for rigid props |

**EC-1 — Chunk storage.** A section would be an entity holding `[InternalBufferCapacity(0)]`
`DynamicBuffer`s of voxels and light — the same heap-array memory model as ES-18a, with less control.
Because safety is per component *type*, any job writing one section's voxel buffer conflicts with every
job reading any section's; per-section concurrent lighting/meshing then needs `BufferLookup` with
`[NativeDisableParallelForRestriction]`, i.e. hand-writing ES-18b's read pins, deferred edits and
double-buffered publish anyway. Streaming loads/unloads become structural changes at vd 32, and the
roadmap's sparse all-air sections, uniform compaction (ES-19) and palettes become archetype changes or
variable-size buffers.

**EC-2 — Orchestration.** The pipeline's deadlock history lives in **cross-chunk neighbor predicates**
(`AreNeighborsReadyAndLit`), flag set/clear pairing, pool-reset clearing and main-thread-only flag
writes. ECS dependencies are per type and per system and over-wait by design; they cannot say "chunk X
may advance once its eight neighbors reach state S". Pattern worth borrowing without the package: an
explicit per-chunk state bitset (what enableable components are) scanned by a Burst job over native
state — exactly ES-18b's planned Burst-side scheduling.

**EC-3 — Rendering.** Hard costs: Forward → Forward+ under three custom renderer features
(`CloudPrepassRendererFeature`, `UnderwaterOverlayRendererFeature`, `UIBandCompositeRendererFeature`) and
their depth-copy ordering; DOTS-instancing variants of all three block shaders, never stripped;
Vulkan-only Android while the project lists GLES3. And no win: every section mesh is unique, so ≈ one
draw command per section (~33.8 k at vd 32), each remesh still a managed `Mesh` + main-thread
`RegisterMesh`, and Unity documents it can be slower than SRP-batched GameObjects. ES-20's path —
BRG-direct with `BatchDrawCommandProceduralIndirect` and vertex pulling — uses the same backend without
Entities, without Forward+ (to be confirmed in ES-20 G0), and with indirect draws Entities Graphics does
not offer.

**EC-4 — Tick.** TG-4 already runs fluids as a Burst, parallel, Y-band halo job, byte-identical to the
managed oracle. Active voxels as entities would break core constraint 1 (voxels are packed `uint`s).

**EC-5 — Lighting.** The BFS runs over a padded volume with a 2-cell halo gathered in-job, cross-chunk
modifications queued, and a main-thread merge; it never propagates into neighbor chunks and keeps the
permanent 200 k node cap. ECS adds nothing here and would force every seam baseline (B54/B55 and the
11-baseline halo set) to be re-proven. ES-18b's "merge becomes a publish" is the right model.

**EC-6 — Dynamic entities (the one fit).** Dropped items, XP orbs, arrows and break particles share a
handful of meshes and materials — the ideal Entities Graphics batching case — and their simulation
(gravity, voxel collision, merging, despawn) is a textbook `IJobEntity`. Preconditions: (1) a design that
needs hundreds to thousands simultaneously (a few dozen mobs are fine as GameObjects with
`VoxelRigidbody`); (2) ES-18a shipped, so Burst systems can query voxels without managed snapshots;
(3) a rendering choice — Entities Graphics inherits EC-3's Forward+ cost, while BRG-direct or
`Graphics.RenderMeshInstanced` from a native list with plain jobs needs no Entities at all, and should be
tried first.

**EC-7 — Physics.** Unity Physics would need a mesh/compound collider per section rebuilt on every
remesh, doubling the meshing pipeline; the custom grid sweep is O(cells touched) and already handles
sub-voxel shapes (VQ-3) and fluid contacts. Future mobs should use a Burst port of the existing sweep
against ES-18. Only non-voxel rigid props (debris, ragdolls) would justify it.

---

## 5. Decision: overall stance

### Option A — Full ECS migration (rejected)

- ✅ One Unity-maintained architecture; Netcode for Entities becomes available.
- ❌ **Every per-subsystem verdict in §4 except EC-6 is REJECT**, so the migration would pay its whole
  cost (≈8 k lines of orchestration, every suite's harness, the Forward+ switch) to gain one deferred
  feature.

### Option B — Hybrid: an ECS World for dynamic entities only (deferred — this is EC-6)

- ✅ ECS where it fits; chunk data stays in the custom native store.
- ❌ ECS does not track `NativeArray` dependencies, so the store's write `JobHandle` must be merged into
  each system's `Dependency` by hand — a new silent-race class (guard it like P-2 §5's pin rules); new
  World-creating suite infrastructure; the package-cost list in §5.1.

### Option C — "DOTS without Entities" ✅ **CHOSEN** (standing policy)

The engine already is this: Burst, Collections 6.6, Mathematics, `IJob`/`IJobFor`/`IJobParallelFor`,
native hash maps/queues/streams. The roadmap's next steps extend it with no package change — ES-18's
`int3 → slot` index in a `NativeParallelHashMap`, cross-chunk light changes as a `NativeStream`,
section-ranged batched jobs, and ES-20's BRG procedural-indirect draws. Editor tooling, validation
suites and the static/domain-reload rules carry on unchanged.

### 5.1 What installing Entities would cost (for whoever reopens EC-6)

- **Packages:** `com.unity.entities` 6.6.0 depends on `assetbundle`, `unityanalytics` and
  `unitywebrequest` — the three modules the lean-package decision removed — plus `nuget.mono-cecil`,
  `scriptablebuildpipeline`, `test-framework.performance` and `modules.hierarchy`.
- **Build:** unstrippable DOTS-instancing variants if Entities Graphics is added; a larger player.
- **Compile/editor:** source generators on every referencing assembly (put ECS code in its own asmdef);
  `TypeManager` initialization at play start; the `unity-api` MCP cannot verify Entities signatures, so
  the bundled package source must stand in for the "don't hallucinate signatures" rule.
- **Statics:** ECS system state lives in the World (cleaner for UDR0004/0005), but any bootstrap code
  still follows the one-`[RuntimeInitializeOnLoadMethod]`-per-class and UAL0010/0013 annotation rules.
- **API churn:** managed components deprecated in 6.6, `Entities.ForEach`/`IAspect` in 1.4 — adopting
  early means paying migrations.

---

## 6. Reopen triggers

1. **A dynamic-entity design needing hundreds+ simultaneous instances** → do EC-6 as Option B, after
   ES-18a, trying BRG-direct rendering before Entities Graphics.
2. **ECS for All ships GameObjects-as-entities + unified transforms** (watch the 6.7/6.8 status posts) —
   the hybrid boundary cost largely disappears.
3. **Multiplayer becomes a goal** — Netcode for Entities is core as of 6.6.0 and would be the strongest
   single argument for broader adoption.
4. **The renderer moves to Forward+ for its own reasons** — removes EC-3's first cost (the batching
   argument still holds).
5. **Unity deprecates standalone BRG** or moves `MeshRenderer` onto an entity backend.

---

## 7. Verification checklist

1. Whether BRG-direct (ES-20's middle path) works on the **plain Forward** URP path — the docs only state
   the Forward+ requirement for Entities Graphics. Check in ES-20 G0 before relying on it.
2. Exact availability of `IJobParallelForBatch` / `IJobParallelForDefer` in Collections 6.6 (not indexed by
   the MCP).
3. The Entities feature claims above against the 6.6.0 package source at the time EC-6 reopens — the
   bundled docs still title themselves "Entities 1.4" in places and the changelog stops at 6.4.

---

## 8. Rejected alternatives

| Alternative                                      | Why rejected                                                                                          | Date       |
|--------------------------------------------------|-------------------------------------------------------------------------------------------------------|------------|
| Full ECS migration                               | Pays ≈8 k lines + every harness + Forward+ for one deferred benefit (§5 Option A)                     | 2026-10-02 |
| Sections as entities with `DynamicBuffer` voxels | Per-type safety blocks per-section concurrency; streaming = structural changes (EC-1)                 | 2026-10-02 |
| Entities Graphics for terrain                    | Forward+ + DOTS-instancing tax, no batching win for unique meshes, no indirect/procedural path (EC-3) | 2026-10-02 |
| One entity per voxel / per active voxel          | Violates the packed-`uint` constraint; documented community failure                                   | 2026-10-02 |
| Unity Physics for terrain collision              | Per-section colliders rebuilt on every remesh; custom sweep already fits (EC-7)                       | 2026-10-02 |

---

## Document History

* **v1.0** - Initial analysis: repo facts, Unity 6.6 ECS state, `EC-1`…`EC-7` per-subsystem verdicts,
  "DOTS without Entities" chosen as standing policy, `EC-6` deferred with reopen triggers.

---

**Last Updated:** 2026-10-02  
**Next Review:** when any §6 trigger fires, or when ES-20 G0 answers §7 item 1
