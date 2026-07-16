# World Scaling — WS-4 Floating Origin Design

**Version:** 1.5
**Date:** 2026-07-16
**Status:** Partially implemented — **WS-4a shipped** (origin plumbing, pinned at the identity);
WS-4b (the shift) and WS-4c (persistence + tooling) proposed.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The far-travel precision phase of the world-scaling track. Unity render space and voxel world
> space — identical today — become two spaces related by a periodically re-anchored
> **`WorldOriginChunk`** offset. **The pivotal decision: all float math stays in Unity space
> (small numbers near the render origin); voxel world space is touched only through integer
> cells or exact multiple-of-16 offsets, via explicit conversion helpers at every
> presentation-layer boundary.** Physics stays a pure Unity-space transform solver (only its
> voxel *lookups* offset — superseding the parent analysis' "make `VoxelRigidbody` operate on
> `ChunkRelativePosition` natively" suggestion); `ChunkRelativePosition` is the *persistence*
> format, not the runtime math substrate. Rendering jitter was observed in-game at ~10 000
> voxels from origin (earlier than the analysis doc's 16k–65k estimate), which this design
> eliminates by keeping all rendered/simulated coordinates within ~1–2k units of the Unity
> origin at any world position up to the permanent ±2³¹ voxel edge.

**Audited:** 2026-07-16, at commit `a6251fd` (branch `feat/world-scaling`).
Findings are from static review of the full presentation/query boundary: `ChunkRelativePosition`,
`Chunk.Reset`/`PlayChunkLoadAnimation`, `ChunkLoadAnimation`, `SectionRenderer` parenting,
`VoxelRigidbody` (+ `World.CheckPhysicsCollision:3373`, `ClampToWorldBorder`),
`PlacementController.MarchRay`/`Probe`, `PlayerInteraction` (mods, highlights, player-AABB veto),
`World.cs` spawn/load/Update/streaming/visualizer paths, `Clouds`, `BorderWallRenderer`,
`DebugScreen`, `TerrainGenDebugOverlay`, `ChunkPoolManager.GetBorder`, `VisualizerChunkData`,
`Player.GetSaveData`/`LoadSaveData`, `SaveDataTypes`, `SaveSystem.CURRENT_VERSION` (= 12),
`ChunkMath.WorldToChunk`, `LiquidCore.hlsl`, and shader/tween greps (no DOTween, no particle
systems, no other runtime `worldPos` consumers except `BorderWallShader.shader` — see §8).

**Relationship to other documents:**

- [`WORLD_SCALING_IMPLEMENTATION.md`](WORLD_SCALING_IMPLEMENTATION.md) — parent roadmap; this doc
  executes its §6 (WS-4). WS-2/WS-3 shipped 2026-07-13; the noise-precision rider stays deferred
  (§1 non-goals).
- [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) — grandparent analysis; §3.3 is the
  design seed. Two of its suggestions are superseded here (§3, §4.2) — drift noted inline.
- [`../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md`](../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md)
  — the v12→v13 level.dat migration in WS-4c follows this protocol.
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) —
  untouched by this design: streaming/gates are chunk-coord-relative and origin-independent (§2).
- [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) — the WS-4c "dev teleport command" is
  `CMD-2` of the command console system (its own design, 2026-07-16); WS-4c = CMD-2 + the
  v12→v13 player-position migration.

---

## 1. Goals & non-goals

### Goals

1. **Eliminate far-travel float jitter** — rendered geometry, physics, and interaction stay
   precise at any distance from the world origin (observed jitter onset today: ~10k voxels).
2. **Zero save-format ambiguity** — everything persisted (mods, spawn, player position) is in
   voxel world space; Unity space never leaks to disk.
3. **Origin-independence by construction** — the voxel pipeline (generation → lighting →
   meshing), chunk streaming, and all job code never see the origin; only the presentation
   boundary converts.
4. **Seamless shifts** — a re-anchor is visually and physically undetectable (no pop, no
   velocity change, no lost interaction state).

### Non-goals (v1)

- **Generation noise precision (the FNL "Far Lands" rider).** Explicitly excluded from this
  phase (decided 2026-07-16). Terrain generation still degrades at ~±2²⁴ ≈ 16.7M voxels; WS-4
  makes *travel* stable, not *generation*. The rider (double-precision per-chunk noise base
  offsets, ⚠️ seed-breaking, world-version-gated) keeps its
  [`WORLD_SCALING_IMPLEMENTATION.md`](WORLD_SCALING_IMPLEMENTATION.md) §6 spec and ships as its
  own follow-up — the WS-4c teleport tool (§7) is its test harness when it does.
- **Entities.** None exist. §4.5 states the rule future entities must follow so they plug in
  without restructuring.
- **Tier A (height/depth) and Tier C (cubic chunks)** — separate tracks, unaffected: the origin
  offset is XZ-only and Y never shifts.
- **Multiplayer** — out of scope for the engine generally.

---

## 2. Current state (what exists today)

| Area                    | State                                                                                                                                                                                                                                                                                                                                                             |
|-------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| The hidden identity     | Unity render space **==** voxel world space, everywhere. Every boundary in §5's tables silently relies on it; WS-4 is precisely the act of breaking this identity and paying for it at each site.                                                                                                                                                                 |
| `ChunkRelativePosition` | Exists and is sound (int XZ chunk macro + float local, Y unclamped absolute; exact `−` operator; normalizing constructor). Used **only** by `WorldSaveData.spawnPosition`, the v10→v11 migration, and the Chunk Math suite. The runtime never touches it.                                                                                                         |
| Player / camera         | `transform.position` is the absolute voxel position; camera is a child. Save: `PlayerSaveData.position` is an absolute `Vector3` in level.dat (v12), restored verbatim on load. Since **SP-1** the restore runs through the single `Spawn.SpawnResolution` chokepoint in `World.StartWorld` STEP 1 — `Player.LoadSaveData` no longer writes the transform at all. |
| Physics                 | Fully custom (`VoxelRigidbody`), no PhysX world-space dependency. Operates on `transform.position`; `World.CheckPhysicsCollision:3373` floors the Unity-space AABB straight into `TryGetVoxel(int)` lookups. `ClampToWorldBorder` clamps against ±`BorderRadius`.                                                                                                 |
| Interaction             | `PlacementController.MarchRay` marches camera-space floats, floors to `hitCell`; cells feed `VoxelMod.GlobalPosition` (**persisted** to `pending_mods.bin`) and the highlight/place transforms.                                                                                                                                                                   |
| Chunk visuals           | `Chunk.Reset` assigns `ChunkPosition = Coord.ToWorldPosition()` (`Chunk.cs:89`); `SectionRenderer`s are children (chunk-local, shift-safe). `ChunkLoadAnimation` caches an absolute `_targetPos`.                                                                                                                                                                 |
| Streaming               | `PlayerChunkCoord` is assigned via the private `GetChunkCoordFromVector3` helper (`World.cs:681/:1586/:2587`), not `ChunkCoord.FromWorldPosition` directly; every loop after that is chunk-coord-relative (`Mathf.Abs(coord − playerCoord)`), hence origin-independent already.                                                                                   |
| Shaders                 | `LiquidCore.hlsl` samples noise at `worldPos * scale` (jumps on shift without an offset) and `frac(worldPos)` shore math (invariant under multiple-of-16 shifts). `BorderWallShader` also consumes `worldPos` (§8). No other runtime world-space shaders.                                                                                                         |
| Jitter observation      | In-game (2026-07-15, post-WS-3): vertex jitter visible at ~10 000 voxels from origin — earlier than the 16k–65k estimate in `WORLD_SCALING_ANALYSIS.md` §3.3.                                                                                                                                                                                                     |

---

## 3. Decision: where the Unity↔voxel conversions live

The failure mode WS-4 must prevent is a *space-confused call site* — a query fed Unity space
where voxel space is expected (or vice versa). Today the identity hides such bugs; after the
first shift they surface as silent off-by-origin errors, possibly only visual.

### Option A — convert inside the existing float-taking APIs (rejected)

- ✅ Call sites unchanged.
- ❌ **The same APIs serve both spaces today.** `GetVoxelState(Vector3)`/`CheckForVoxel` are
  called with camera-derived Unity floats (raycast, debug screen) *and* voxel-space floats
  (`VoxelMod.GlobalPosition` routing in `WorldJobManager:1298/:1442/:1511`). Redefining their
  input space breaks the other caller class.

### Option B — parallel Unity-space overloads on `World` (rejected)

- ✅ One-line call sites.
- ❌ **Doubles the query API surface and makes the wrong-twin mistake easy** — a future caller
  picking `CheckForVoxel` instead of `CheckForVoxelUnity` compiles fine and is exactly the
  silent bug class this design exists to kill.

### Option C — explicit conversion helpers at every boundary call site ✅ **CHOSEN**

A single static surface (`WorldOrigin`, §4.1). `World`/`WorldData` query APIs stay pure voxel
space; every presentation-layer call site performs a visible, greppable conversion. The §5
boundary tables map 1:1 onto code, so the audit *is* the diff review. Decided 2026-07-16.

**Supersedes** `WORLD_SCALING_ANALYSIS.md` §3.3's implied "route positions through the origin
mapping" phrasing only in mechanism — the shift design itself is unchanged.

---

## 4. Architecture

### 4.1 The `WorldOrigin` state and identity

```csharp
/// <summary>
/// The floating-origin anchor: the chunk whose corner currently maps to the Unity-space
/// origin. XZ only — Y never shifts. Main-thread, presentation-layer only: nothing under
/// Assets/Scripts/Jobs/ may reference this type (jobs live in voxel space exclusively).
/// </summary>
public static class WorldOrigin
{
    /// <summary>Chebyshev chunk distance from the origin at which the world re-anchors.</summary>
    public const int ShiftThresholdChunks = 64;                       // 1024 units

    public static ChunkCoord OriginChunk { get; }                     // authoritative anchor
    public static Vector3Int OriginVoxel { get; }                     // OriginChunk * 16, cached

    public static Vector3    VoxelToUnity(Vector3Int voxelPos);       // exact int subtract
    public static Vector3    VoxelToUnity(Vector3 voxelPos);          // for spawn floats
    public static Vector3Int UnityToVoxelCell(Vector3 unityPos);      // FloorToInt + int add
    public static ChunkCoord UnityToChunk(Vector3 unityPos);          // for PlayerChunkCoord
    public static ChunkRelativePosition UnityToRelative(Vector3 unityPos); // for persistence
}
```

The identity, and the precision rule that makes it safe at any distance:

```
unityPos  = voxelPos − OriginVoxel                    (offset is an exact multiple of 16)
voxelCell = FloorToInt(unityPos) + OriginVoxel        (integer add — exact at ±2³¹)
```

**All float math stays in Unity space.** The raycast marches camera-space floats and only the
resulting *cell* is offset; physics AABBs stay in Unity space and only the voxel *lookup* is
offset. Nothing ever adds a large float to a small float. At runtime, the pairing "small Unity
transform + integer origin" *is* the chunk-relative representation —
`ChunkRelativePosition` proper is reserved for persistence (§4.4).

Not persisted: on load, `OriginChunk` is derived from the loaded player position *before* any
transform is written or chunk created; a fresh world starts at `(0, 0)` (identity — near-spawn
behavior is bit-identical to today, which is also the regression baseline for WS-4a).

### 4.2 Physics: Unity-space solver, offset lookups

`VoxelRigidbody` is untouched — it remains a pure Unity-space transform solver (velocity,
momentum, AABB sweeps are all deltas or near-origin floats). Exactly two sites change:

- `World.CheckPhysicsCollision:3373` — the integer scan offsets its lookup
  (`TryGetVoxel(x + ox, y, z + oz)`) while block bounds and corrections stay in Unity space, so
  `ContactFace`/`Correction` remain consistent with the entity AABB.
- `VoxelRigidbody.ClampToWorldBorder:151` — the TF-14 border is a voxel-space AABB centered on
  the *world* origin: clamp against `±(radius − margins) − OriginVoxel`.

**Supersedes** `WORLD_SCALING_ANALYSIS.md` §3.3's "make `VoxelRigidbody` operate on
`ChunkRelativePosition` natively": reading the solver shows it needs no such rewrite — its float
math is already origin-relative-by-nature once the two lookup sites convert. (Doc drift to sync
into the parent analysis.)

### 4.3 The shift operation

Runs at the top of `World.Update`, before `PlayerChunkCoord` is consumed, when
`Chebyshev(PlayerChunkCoord − OriginChunk) > ShiftThresholdChunks`. One frame, main thread:

1. `delta = PlayerChunkCoord − OriginChunk`; `OriginChunk = PlayerChunkCoord`;
   `unityDelta = delta × 16` (exact float — magnitude ≤ ~1k units + view distance).
2. Translate by `−unityDelta`:
    - every active chunk GameObject — **re-derive** `ChunkPosition` from `Coord` via
      `WorldOrigin.VoxelToUnity` and reassign (never patch cached vectors by subtraction);
    - in-flight `ChunkLoadAnimation`s — shift `_targetPos` *and* the current transform (the
      Lerp then continues with identical relative motion — verified by reading);
    - the player transform (camera is a child; velocity/momentum are deltas — untouched);
    - chunk-border visualizer objects, `VisualizerChunkData` objects, the `Clouds` root;
    - cached positions: `_lastVisualizerPlayerPos` (`World.cs:2784`), debug-screen caches.
3. Refresh the `_WorldOriginOffset` shader global (§4.6).
4. `DEVELOPMENT_BUILD`/editor assertion: player Unity-space XZ magnitude ≤
   `(ShiftThresholdChunks + margin) × 16` — turns a missed shift or drifted site into a loud
   failure instead of far-out jitter (§6 false-green guard).

Rebuilt-per-frame consumers need nothing: highlight/place blocks (`PlaceCursorBlocks`),
`BorderWallRenderer` (`LateUpdate` full rebuild), cloud tile re-anchoring (`UpdateClouds`).

**Must NOT shift:** skybox, directional light, UI, camera-local state. Verified absent: PhysX
bodies, particle systems, tweens (no DOTween usage repo-wide).

**Timing safety:** the shift runs in `Update`; the physics solver runs in `FixedUpdate` — no
mid-solve teleport is possible. Streaming is unaffected: `PlayerChunkCoord` and every distance
loop are voxel-chunk-space values that do not change when the origin moves.

### 4.4 Persistence: voxel space on disk, `ChunkRelativePosition` for the player

Everything persisted is voxel world space. Two changes:

- **WS-4b (must land with the shift):** `Player.GetSaveData` writes
  `transform.position + OriginVoxel` (voxel-space absolute `Vector3`); the startup path sets the origin
  from the saved position first, then places the transform near the Unity origin. Without this, the
  first post-shift save corrupts the player position.
  <br>**Where this goes (corrected by the WS-4a audit, re-homed by SP-1):** *not* at `World.StartWorld`'s
  anchor call — that runs before `SaveSystem.LoadWorldGameState`, and the anchor must be derived from the
  starting player position, which only exists once the save is parsed. **SP-1 gave that position exactly one
  home**, so the derivation belongs at the `Spawn.SpawnResolution` chokepoint in `StartWorld` **STEP 1**:
  `ResolveInitial(...)` → `AnchorOrigin(chunkOf(spawnPosition))` → write
  `transform = VoxelToUnity(spawnPosition)`, in that order — one site covering all three
  `SpawnSource`s, not three. (Pre-SP-1 this said "inside the load path / `Player.LoadSaveData`"; that method
  no longer writes the transform.) `World.AnchorOrigin` is the single permitted entry point (it pairs
  `WorldOrigin.SetOrigin` with the `_WorldOriginOffset` shader push so a re-anchor cannot land without the
  shader following); WS-4a leaves it pinned to the fresh-world identity.
  <br>**STEP 4 is not an anchor site:** it runs after chunk creation, so re-anchoring there would need the
  §4.3 translate loop. It stays a plain `VoxelToUnity` conversion.
- **WS-4c:** `PlayerSaveData.position` migrates `Vector3` → `ChunkRelativePosition`
  (**v12→v13**, AOT frozen-DTO protocol, `MigrateLevelDat` only — chunk/region formats
  untouched), removing the ±2²⁴ precision cap on the saved value. Runtime construction uses
  `new ChunkRelativePosition(OriginChunk, transform.position)` — the normalizing constructor
  resolves the small local offset exactly, with no large-float round-trip. Rotation,
  capabilities, and inventory are unchanged.

`VoxelMod.GlobalPosition` (persisted in `pending_mods.bin`) is already voxel space; WS-4a makes
the interaction call sites (`PlayerInteraction.cs:75/:93`) convert their Unity-space cells at
mod creation so that stays true.

### 4.5 Rule for future entities

An entity is a Unity transform simulated near the origin, plus the shared `WorldOrigin` anchor;
it registers with the shift translate loop (or sits under a shifted parent), and persists its
position as `ChunkRelativePosition`. No entity stores an absolute-voxel float position at
runtime.

### 4.6 Shaders

`World.cs` sets a `float3 _WorldOriginOffset` global (= `OriginVoxel`, precedent:
`s_shaderGlobalLightLevel` at `World.cs:634/:1417`). `LiquidCore.hlsl` samples its noise fields
at `worldPos + _WorldOriginOffset` so the liquid pattern stays continuous across shifts;
`frac(worldPos)` shore/wall math is left on raw `worldPos` (invariant under multiple-of-16
shifts). The offset is passed raw — far from origin the noise *input* precision degrades exactly
as today's absolute `worldPos` does (cosmetic, liquid-only; a periodicity `fmod` does not
cleanly exist for simplex across the shader's several scales — accepted limitation, §9).
`BorderWallShader`'s `worldPos` usage is a WS-4a audit item (§8).

### 4.7 Rule: the origin is read fresh, never stored

`WorldOrigin.OriginVoxel` is **frame state** — it changes whenever the world re-anchors. Two rules follow,
both learned from a WS-4a code-review finding (a captured origin that would have gone stale on the first
shift, guarded only by a docstring):

- **Never cache the origin in construction-time state.** Anything holding it across frames must be refreshed
  by someone, and "someone must remember" is precisely the silent-bug class this design exists to remove.
  Read it fresh at the point of use (presentation layer only), or take it as a parameter.
- **Reusable decision units take it as a per-call parameter**, not from the global and not as a field — the
  pattern `PlacementController` follows. This buys two properties at once:
    1. **Snapshot atomicity.** A ray march makes one voxel query per step (hundreds per call); all of them must
       resolve against the same origin, or a march torn across a re-anchor silently targets a mix of two
       coordinate frames. A parameter pins the frame for the call's duration *by construction*.
    2. **Suite isolation.** The unit stays free of the global, so validation can drive it at any origin with no
       global state to set or restore — which is what makes the origin plumbing falsifiable at all (§6).

  Where a result and a later action must agree (e.g. a probe's cells feeding a `VoxelMod` on click), carry the
  origin **alongside** the result rather than re-reading the global, so both use one frame's anchor.

**Ceiling of this rule (WS-4b checklist item):** no suite can prove a *caller* reads the global fresh — a
MonoBehaviour that caches `WorldOrigin.OriginVoxel` in `Start()` resurrects the bug one layer up, invisibly at
origin (0,0). That is inherent to keeping the static presentation-only; the backstop is the §7 in-game gate
(targeting/placement land on the correct voxels after multiple shifts), which must not be traded away.

---

## 5. The boundary inventory (the migration list)

Every crossing between the two spaces, from code reading at `a6251fd`. This is the WS-4a
execution checklist; each row becomes a visible `WorldOrigin.*` call.

### 5.1 Voxel → Unity (placing visuals)

| Site                                                                        | Change                                                                                                                                                                                                                                                                                                                                       |
|-----------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Chunk.Reset` / `PlayChunkLoadAnimation` (`Chunk.cs:89/:104/:640`)          | `ChunkPosition = VoxelToUnity(Coord.ToVoxelOrigin())` — the single chokepoint for chunk GameObjects (SectionRenderers are children — shift-safe)                                                                                                                                                                                             |
| `ChunkLoadAnimation.ResetToUnderground` targets                             | Receives the Unity-space position from `Chunk.Reset`; shift loop re-bases in-flight animations (§4.3)                                                                                                                                                                                                                                        |
| Chunk-border visualizer (`World.cs:2725`, `ChunkPoolManager.GetBorder:229`) | Placement position converts                                                                                                                                                                                                                                                                                                                  |
| `VisualizerChunkData` (:43/:67)                                             | Placement position converts                                                                                                                                                                                                                                                                                                                  |
| Collision-bounds debug draw (`World.cs:2947`)                               | `VoxelToUnity(Coord.ToVoxelOrigin()) + local` before `Debug.DrawLine`                                                                                                                                                                                                                                                                        |
| `BorderWallRenderer.RebuildMesh`                                            | Wall planes at `±ext − OriginVoxel`; keep `uv.x` bands voxel-space for continuity across shifts                                                                                                                                                                                                                                              |
| Spawn transform writes (`World.StartWorld` STEP 1 + STEP 4)                 | Since **SP-1** there are exactly two, both fed by `Spawn.SpawnResolution` (`ResolveInitial` / `ResolveFinal`) and both converting one voxel-space value: `_playerTransform.position = VoxelToUnity(spawnPosition)`. The unit is pure voxel space throughout; `SetSpawnPoint(placement.CanonicalSpawn)` builds from voxel space               |
| `Clouds` (`Awake` anchor `:46`, `CloudTileCoordFromFloat:352`)              | Pattern lookup adds `OriginVoxel` so the cloud pattern doesn't teleport on shift; tile re-anchoring is player-relative and needs nothing else. Do the pattern wrap in **integer** space (pattern repeats every `_cloudTexWidth`) — the float-`frac` idiom re-introduces large-float precision loss far out; integer modulo is exact for free |
| Shader global                                                               | `_WorldOriginOffset` (§4.6)                                                                                                                                                                                                                                                                                                                  |

### 5.2 Unity → Voxel (queries from transforms)

| Site                                                                                    | Change                                                                                                                                  |
|-----------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------|
| `PlayerChunkCoord` (`World.cs:671/:1567/:2566`)                                         | `WorldOrigin.UnityToChunk(transform.position)`. Everything downstream is origin-independent — **the chunk pipeline needs zero changes** |
| `World.CheckPhysicsCollision:3373`                                                      | Offset the integer voxel lookup only (§4.2)                                                                                             |
| `VoxelRigidbody.ClampToWorldBorder:151`                                                 | Clamp against border AABB minus `OriginVoxel` (§4.2)                                                                                    |
| Raycast/placement (`PlacementController.MarchRay/Probe/CanPlaceAt`)                     | March in Unity space; convert resulting integer cells (`hitCell`, `placeCell`) — **fragility hotspot**, see §7 gates                    |
| `VoxelMod` creation (`PlayerInteraction.cs:75/:93`)                                     | Convert highlight-cell → voxel cell at mod creation (`GlobalPosition` is persisted)                                                     |
| Player-AABB placement veto (`PlayerInteraction.PlaceCellOverlapsPlayer`)                | Compare in one space consistently (keep Unity space: cell converts back, or veto runs on Unity cells before conversion)                 |
| `Player.GetSaveData` + the SP-1 `SpawnResolution` chokepoint (`StartWorld` STEP 1)      | §4.4 — origin anchored from the resolved starting position first, transform placed second                                               |
| `DebugScreen` (:264/:274/:343)                                                          | Display voxel-space coordinates (transform + origin); `GetVoxelState`/`GetChunkFromVector3` queries convert                             |
| `TerrainGenDebugOverlay` (:97–:150)                                                     | `gx/gz` generation-sampling coordinates add `OriginVoxel`                                                                               |
| Benchmark controllers (`BenchmarkController:253/:332/:410`, `FluidStressController:98`) | Waypoints are voxel-space values driven into the transform — convert at apply (they run near origin; correctness hygiene)               |
| Minimap / `WorldSelectMenu:543`, `WorldInfoUtility`                                     | **No change** — read saved (voxel-space) data / chunk scans, never live transforms (verified in the WS-2 OQ-4 audit)                    |

**Call-site audit sweep (WS-4a):** classify every caller of `GetVoxelState(Vector3)`,
`CheckForVoxel`, `GetChunkCoordFor`, `ChunkCoord.FromWorldPosition`, `GetChunkFromVector3`,
`GetVoxelPositionInChunkFromGlobalVector3`, `ChunkMath.WorldToChunk(float)`, and
`World.GetChunkCoordFromVector3` (the private helper the `PlayerChunkCoord` sites actually call) as
Unity-space (convert) or voxel-space (leave), and record the classification in the PR description.
Plus a repo grep for cached absolute `Vector3` fields and `transform.position =` writes (§8).

**Outcome (2026-07-16):** every public float-taking query API above stays **pure voxel space** and none
of them were re-spaced; each Unity-space caller converts at its own site. `World.CheckForVoxel` gained
an integer-cell overload (`CheckForVoxel(int,int,int,…)`) on the VQ-1 fast path, which the ray march
uses so its per-step query converts a cell instead of round-tripping a float.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                                     |
|-------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — coordinate/presentation work only.                                                                                               |
| Burst jobs 100 % Burst-compatible               | Jobs never see the origin: `WorldOrigin` is main-thread/presentation-only and banned under `Assets/Scripts/Jobs/`.                           |
| No GC / LINQ in hot paths                       | Conversions are integer adds/subtracts; the shift loop iterates existing collections, allocation-free.                                       |
| Pooling conventions                             | `Chunk`/`ChunkLoadAnimation` stay pooled; positions are re-derived in `Reset` (pool-reset-safety: no new transient field without its reset). |
| No BinaryFormatter/JSON for terrain             | Chunk/region formats untouched. WS-4c is level.dat-only, via the AOT frozen-DTO migration protocol (v12→v13).                                |
| BlockIDs constants, no raw IDs                  | Not applicable — no block logic touched.                                                                                                     |
| No magic numbers                                | `ShiftThresholdChunks` const (`PascalCase` public const per style guide); no inline 16s — `ChunkMath` constants.                             |

**False-green guard (the design's biggest risk):** WS-4a runs at origin `(0,0)`, where a
*missed* boundary site is invisible — the identity hides it, and it only surfaces after the
first shift, possibly only as a subtle visual offset. Three gates: (1) the §5 audit table maps
1:1 to greppable `WorldOrigin.*` calls; (2) the Chunk Math suite gains non-zero-origin
conversion baselines (§7), so origin math is exercised without play mode; (3) the runtime
bounded-position assertion (§4.3 step 4) makes drift loud in dev builds.

---

## 7. Phased implementation plan

| Phase                                                                             | Scope                                                                                                                                                                                                                                                                                                | Effort | Depends on      |
|-----------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|-----------------|
| **WS-4a — origin plumbing, no shift** ✅ **SHIPPED 2026-07-16, in-game confirmed** | `WorldOrigin` type + helpers; thread every §5 boundary site; call-site audit sweep; suite baselines. Origin pinned `(0,0)` → **zero behavior change**, game bit-identical. Also landed the `_WorldOriginOffset` global + `LiquidCore` sampling (identity-safe at offset 0, so it moved out of WS-4b) |   🟡   | WS-3 ✅          |
| **WS-4b — the shift**                                                             | §4.3 trigger + translate loop; `GetSaveData`/load voxel-space fix (§4.4 — **the one boundary WS-4a deliberately left**), now a single `AnchorOrigin` at the SP-1 chokepoint rather than three spawn sites; bounded-position assertion                                                                |   🔴   | WS-4a ✅, SP-1 ✅ |
| **WS-4c — persistence + tooling**                                                 | `PlayerSaveData.position` → `ChunkRelativePosition`, v12→v13 AOT migration; `/teleport` command — `CMD-2` of [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) (console phases CMD-0/1 may land earlier, independently)                                                                       |   🟡   | WS-4b, CMD-1    |

WS-4a+b deliver the standalone value (stable far travel); WS-4c extends save precision past
±2²⁴ and ships the far-coordinate test harness. Bisectable: each phase compiles and keeps all
suites green on its own.

**Validation is built alongside, not after** (the WS-1/WS-2/WS-3 pattern, Chunk Math suite):

- **WS-4a baselines** ✅ **shipped (Chunk Math 26→32, Placement 13→15; Validate All 197→205):**
  *Chunk Math* — `VoxelToUnity`/`UnityToVoxelCell` round-trips at non-zero origins (±small, ±10k, ±2³⁰
  — inside the `ToVoxelOrigin` ×16 wrap guard), chunk-alignment of `OriginVoxel`, Y-never-shifts,
  `UnityToChunk` parity, `UnityToRelative` ↔ transform+origin round-trip, identity at `(0,0)`.
  *Placement* — the **call-site** guard: the real `PlacementController` replayed at four origins
  (identity, ±10k, negative quadrant, ±2³⁰ edge) must produce byte-identical outcomes, and its probe
  must return Unity-space cells far out.
  <br>**Why the second suite is not optional:** with the origin pinned at `(0,0)`, sabotaging the
  controller's conversion entirely left **all 13 pre-existing placement baselines green** — and
  sabotaging `UnityToVoxelCell` left the "identity" Chunk Math scenario green. Origin-`(0,0)` coverage
  is structurally blind to a missed conversion; only the non-zero-origin scenarios have teeth.
- **WS-4b:** shift-delta re-anchor equivalence (`voxelCell` invariant across a simulated
  re-anchor for the same physical point). The shift itself is runtime: in-game gate below.
- **WS-4c:** v12→v13 migration fixture (frozen v12 level.dat → migrated CRP equals the old
  absolute position exactly at near coords; far-coordinate fixture beyond 2²⁴ documents the
  recovered precision), riding the `serialization-migration` skill.

**In-game gate (WS-4b, ends on user confirmation):** fly past the 64-chunk threshold repeatedly
— no visual pop at the shift frame (stationary + walking + falling + swimming in fluid); block
targeting, placement/break, physics step-up, and fluid edits land on the correct voxels after
multiple shifts; save + reload round-trips position and pending mods; a far session (~50k–100k
via WS-4c teleport, or a long flight before WS-4c) shows **no jitter at ≥10k** where it is
visible today; liquid noise pattern is continuous across a shift.

### Extension roadmap (post-WS-4c, in intended order)

| Version | Extension                                                                                                                                                                                                                                       |
|---------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Generation noise-precision rider (`WORLD_SCALING_IMPLEMENTATION.md` §6) — double base offsets, ⚠️ seed-breaking, world-version-gated. Uses the WS-4c teleport as its harness. Extends the usable radius from ~16.7M to the permanent ±2³¹ edge. |
| **v3+** | Entity system adoption of §4.5's rule — gets its own design doc when entities become concrete.                                                                                                                                                  |

---

## 8. Verification checklist & research points

### 8.1 Gating — resolved during WS-4a (2026-07-16)

All four closed by the WS-4a audit sweep; kept here as the record of what was checked.

1. **`BorderWallShader.shader` `worldPos` usage** — ✅ **no change needed.** `worldPos` feeds only
   `distance(i.worldPos, _WorldSpaceCameraPos)` (`:79`); both operands shift together, so the fade is
   shift-invariant. The band texture samples `TRANSFORM_TEX(i.uv, _MainTex)`, not `worldPos` — the
   world-anchored bands live in `BorderWallRenderer`'s `uv.x`, which WS-4a keeps in voxel space.
2. **Cached absolute `Vector3` sweep** — ✅ complete. Beyond §5: `ChunkLoadAnimation._targetPos` and
   `Chunk.ChunkPosition` are now Unity space end-to-end (re-derived in `Chunk.Reset`); benchmark
   waypoints convert at apply; UI/tooltip/toolbar `.position` writes are screen space. The **one**
   remaining unconverted boundary is `Player.GetSaveData` (`Player.cs:235`), deliberately deferred to
   WS-4b (§4.4) because it and the restore side must change together or saves corrupt. Since **SP-1**
   the restore side is no longer in `Player.LoadSaveData` (which stopped writing the transform) but at
   the `SpawnResolution` chokepoint — one site instead of two, which is what WS-4b converts.
3. **Validation-suite world stubs** — ✅ confirmed. Suites never touch the `WorldOrigin` global: the
   Chunk Math origin scenarios restore the identity in a `finally`, and `PlacementController` takes the
   origin as a **per-probe parameter** (see §4.7), so `PlacementTestWorld` drives far origins with no
   global to leak.
4. **`Chunk.GetVoxelPositionInChunkFromGlobalVector3` callers** — ✅ classified **voxel-space; method
   does NOT convert.** Both callers pass voxel-space values (`World.cs:2271` a `VoxelMod`-derived
   neighbor; `DebugScreen.cs:590` a converted target cell), so its callers own the conversion.

**Sweep finding (the reason this step is not a formality):** two space-confused sites existed that the
§5 tables never named — `Clouds.CloudTilePosFromVector3` was called with *both* pattern-space and
world-space values (§5.1's "pattern lookup adds `OriginVoxel`", applied literally inside that method,
would have corrupted every tile key at creation), and `ChunkPoolManager.GetBorder` re-derived a
`ChunkCoord` from the position it was handed. A third was *introduced and caught here*: routing the
three `PlayerChunkCoord` sites through the shared `GetChunkCoordFromVector3` helper silently
re-spaced its fourth caller (`World.cs:2148`, a voxel-space `VoxelMod.GlobalPosition`) — the exact
Option-A failure §3 rejects. The player sites now convert individually at their call site.

### 8.2 Non-gating — far-out scalability research points (added 2026-07-16)

Open questions surfaced by this design's audit that do **not** block WS-4a/b/c. Each names its
resolution path; findings land back here (or in `PERFORMANCE_IMPROVEMENTS_REPORT.md` if they
graduate to work items).

1. **`ChunkCoord`/`Vector2Int` dictionary hash quality at large/negative coordinates.**
   `WorldData.Chunks` and `World._chunkMap` are hot lookup dictionaries keyed by chunk
   identity. Hash functions that distribute fine over a 0–99 world can cluster at coordinates
   like ±500 000 or across mixed-sign quadrants, silently degrading every chunk lookup.
   *Resolve:* micro-benchmark dictionary fill/lookup with realistic far-out and mixed-quadrant
   key sets vs the near-origin baseline (`perf-benchmark` protocol). If degraded, a
   SplitMix-style mixer in `ChunkCoord.GetHashCode` is a small, save-safe fix — **measure
   first**, per the optimization guide.
2. **Minimap / `WorldInfoUtility` span math at extreme chunk spread.** Dynamic downsampling is
   verified (WS-2 OQ-4), but a save with visited chunks at −10M *and* +10M drives the span
   computation into billions of voxels. *Resolve:* one synthetic far-spread save (two distant
   region files) through the world-select minimap; check for overflow/degenerate texture dims.
3. **Region-file fan-out.** Long-range travel creates one region file per 512×512 voxels — a
   cross-map flight leaves thousands of files in one directory. *Resolve:* confirm
   `ChunkStorageManager.GetRegion` and the save/load paths never enumerate the region
   directory (open-by-computed-name only), and note the OS directory-size practicalities in
   `INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md` if relevant.
4. **Far-coordinates soak scenario (standing, post-WS-4c).** Once `/teleport` (CMD-2) exists:
   teleport to ±100k / ±10M / ±1×10⁹ and run the checklist — streaming, block edits, fluid
   sim, lighting, save/reload, minimap. Candidate for a semi-automated benchmark-mode pass on
   the existing `BenchmarkController` flight infrastructure. Doubles as the harness for the v2
   noise rider (§7 extension roadmap).
5. **Negative-quadrant generation parity remains in-game-only** (WS-3's recorded limitation —
   no generation suite exists to extend). If a generation validation suite is ever built, that
   parity scenario plus the §8.2.4 soak coordinates should be its first citizens.

---

## 9. Limitations (stated as consequences)

- **Terrain generation still degrades at ~±16.7M voxels** — WS-4 deliberately does not touch
  the samplers (§1 non-goals). Travel is stable there; the terrain itself develops FNL
  float-precision artifacts until the v2 rider ships.
- **Liquid noise input precision degrades cosmetically far out** under the raw
  `_WorldOriginOffset` — same class as today's absolute `worldPos`, liquid-only, accepted.
- **Until WS-4c lands, the saved player position is precise only to ±2²⁴** (voxel-space
  `Vector3`), coincidentally the same cap as the un-shipped noise rider.
- **The teleport command is a first-class console command** (`CMD-2` of
  [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md)), not throwaway dev tooling — but v1
  ships no permissions gating, so it is effectively a cheats-on capability until that seat fills.
- **WS-4a is invisible and ships effectively-dead code.** The origin never leaves `(0,0)` in
  production until WS-4b, so WS-4a has no user-visible effect and no in-game evidence is obtainable —
  a play session proves only "nothing regressed". Its positive signal is entirely the non-zero-origin
  suites, and their reach is one call path (placement) plus the helper math. A boundary site that is
  both un-swept and unexercised by those suites stays latent until the first shift.
  <br>**In-game passes (2026-07-16, both green):** two runs near spawn — one before the code review,
  one after it — covering world generation around (0,0), player collision, block placement/breakage,
  and fluid simulation. That is the *no-regression* check, and it exercised the highest-risk changes
  (the per-probe placement refactor, the startup-ordering move, the physics offset lookup, the
  `LiquidCore` sampling change). Not covered by those runs, and low-risk-but-unverified until WS-4b
  makes them observable: `Clouds` (its root-anchor write moved `Awake` → `Initialize`),
  `BorderWallRenderer`'s per-axis conversion (only renders when a world has `BorderRadius > 0`), a
  save → reload round-trip, and the `DebugScreen` coordinate readout.
- **Two coordinate sites still ROUND where the engine otherwise floors** — left untouched by WS-4a
  deliberately, so the phase stayed bit-identical. MyBox's `Vector3.ToVector3Int()` extension is
  `RoundToInt`, not `FloorToInt`; only floor names the cell *containing* a position, and every WS-4
  conversion (`WorldOrigin.UnityToVoxelCell`, `MarchRay`, `CheckPhysicsCollision`) floors. The survivors are
  `DebugScreen.cs:271` (`_groundVoxelPos`, a readout) and `World.ResolveSpawnHeight` (`World.cs:3248`, which
  rounds the spawn XZ before the height probe). Both are pre-existing and only diverge for fractional inputs.
  **SP-1 passed over the second one deliberately** (it moved the call, not the rounding), and WS-4b touches the
  same path (§4.4) and must pass over it again: **do not silently "fix" it** inside the shift work — changing it
  is a behavior change that deserves its own decision, not a rider on a phase whose contract is "no behavior
  change except the shift".
- **The saved player position is still Unity-space-shaped.** `Player.GetSaveData` writes
  `transform.position` verbatim (`Player.cs:235`), which is correct only while the origin is the
  identity. This is WS-4b's **first** obligation (§4.4): the first post-shift save corrupts the player
  position if `GetSaveData` and the chokepoint's origin ordering do not land with the shift. SP-1 reduced
  the restore side from two competing writes to one, but changed neither side's space.
- **Parent-doc drift to sync (docs-sync, with WS-4a):** `WORLD_SCALING_ANALYSIS.md` §3.3 —
  (a) the `VoxelRigidbody`-on-`ChunkRelativePosition` suggestion is superseded by §4.2 here;
  (b) record the observed ~10k jitter onset alongside the 16k–65k estimate; (c) "chunk positions
  are assigned in exactly one place" undercounts (borders, visualizers, benchmarks — §5.1).

---

## Document History

* **v1.5** - **SP-1 landed as a pre-WS-4b pass** (2026-07-16): `World.StartWorld`'s three spawn/player-position
  paths (fresh / editor-replay / loaded-save) and its post-chunk height resolve were consolidated into the pure
  `Spawn.SpawnResolution` decision unit (`Classify` + `ResolveInitial` + `ResolveFinal`), covered by a new
  **Validate Spawn** suite (9 baselines; Validate All 205→214). Zero behavior change at the pinned identity,
  in-game confirmed on all three sources. Doc impact here: §4.4's WS-4b origin derivation **re-homed** from
  "inside the load path / `Player.LoadSaveData`" to the STEP 1 chokepoint — `Player.LoadSaveData` no longer
  writes the transform, and STEP 4 is named as a non-anchor site (it runs after chunk creation). §2, §5.1's
  spawn row, §5.2's save/startup row, §7's WS-4b row (now one `AnchorOrigin`, not three spawn sites; depends on
  SP-1), §8.1 item 2, and §9 updated to match; `ResolveSpawnHeight` line ref corrected `:3268`→`:3248`, with the
  round-vs-floor non-change restated (SP-1 moved the call, not the rounding).
* **v1.4** - WS-4a code review (2026-07-16). New **§4.7 "the origin is read fresh, never stored"** — the
  origin is frame state, so reusable decision units take it as a per-call parameter (snapshot atomicity across
  a multi-step march + suite isolation), never as constructor state; states the rule's ceiling (no suite can
  prove a *caller* reads the global fresh → the §7 in-game gate is the backstop). §4.4 corrected: the WS-4b
  origin derivation belongs **inside the load path**, not at the startup anchor, which runs before the save is
  parsed; `World.AnchorOrigin` named as the single entry point pairing `SetOrigin` with the shader push. §8.1
  item 3 updated (per-probe parameter, not injected ctor state). §9 additionally records the two surviving
  `ToVector3Int` (round-not-floor) coordinate sites as a deliberate WS-4a non-change, flagged so the WS-4b
  spawn/load work does not "fix" them as a rider on a no-behavior-change phase.
* **v1.3** - **WS-4a SHIPPED** (2026-07-16). §7 phase table + validation section record the shipped
  baselines (Chunk Math 26→32, Placement 13→15, Validate All 197→205, both prove-red); §8.1 flipped
  from "MUST re-verify" to resolved, with `BorderWallShader` closed as **no change needed** (its
  `worldPos` is camera-relative, hence shift-invariant) and the sweep's three space-confused sites
  recorded; `_WorldOriginOffset` + `LiquidCore` moved WS-4b→WS-4a (identity-safe at offset 0);
  §2/§5.2 drift corrected (`PlayerChunkCoord` goes through `GetChunkCoordFromVector3`, not
  `ChunkCoord.FromWorldPosition`); §9 gained the "WS-4a is invisible" and "saved player position is
  still Unity-space-shaped" limitations.
* **v1.2** - §8 split into gating checklist (8.1) + non-gating far-out scalability research
  points (8.2: chunk-key hash quality benchmark, minimap far-spread span test, region-file
  fan-out check, standing far-coordinates soak scenario, generation-suite parity note); clouds
  row in §5.1 now specifies integer-space pattern wrap (exact at any distance).
* **v1.1** - Teleport tooling re-homed: the WS-4c command is now `CMD-2` of the new
  [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) (three-layer console design,
  2026-07-16) instead of an ad-hoc dev command; WS-4c row, relationships, and §9 updated.
* **v1.0** - Initial design — periodic re-anchor at 64 chunks; explicit `WorldOrigin` helpers at
  call sites (decision menu closed 2026-07-16: helpers > overloads, threshold 64, player save →
  `ChunkRelativePosition` v13, dev teleport in scope); physics stays Unity-space (supersedes the
  parent analysis' CRP-native suggestion); full §5 boundary inventory from the `a6251fd` audit;
  noise rider explicitly excluded to the v2 extension.

---

**Last Updated:** 2026-07-16
**Next Review:** when WS-4b starts (its first obligation is the §4.4 `GetSaveData`/load fix — the one
boundary WS-4a left, now a single `AnchorOrigin` at SP-1's `SpawnResolution` chokepoint) or when the noise
rider is scheduled.
