# World Scaling — WS-4 Floating Origin Design

**Version:** 1.9
**Date:** 2026-07-19
**Status:** **Implemented** — every WS-4 phase is shipped and in-game confirmed: WS-4a (origin plumbing),
WS-4b (the shift), WS-4c persistence (`ChunkRelativePosition` player position, level.dat v13), and WS-4c
tooling (`/teleport` = CMD-2, 2026-07-18). The only WS-4-adjacent work left is the deferred v2 noise rider
(terrain degrades past ±2²⁴; lighting Bug 19 — the far-lands lighting crash logged there — was fixed
independently 2026-07-19 via integer column routing, in-game confirmed and archived as `_FIXED_BUGS.md` #24).
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

| Area                    | State                                                                                                                                                                                                                                                                                                                                                                                                                          |
|-------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| The hidden identity     | **Broken as of WS-4b** — this table describes the pre-WS-4 world it was written against, kept because it is what §5's tables were derived from. Unity render space **==** voxel world space, everywhere; every boundary in §5 silently relied on it. WS-4 is precisely the act of breaking that identity and paying for it at each site — and §9 records the three sites where the payment was missed and the identity hid it. |
| `ChunkRelativePosition` | Exists and is sound (int XZ chunk macro + float local, Y unclamped absolute; exact `−` operator; normalizing constructor). Used **only** by `WorldSaveData.spawnPosition`, the v10→v11 migration, and the Chunk Math suite. The runtime never touches it.                                                                                                                                                                      |
| Player / camera         | `transform.position` is the absolute voxel position; camera is a child. Save: `PlayerSaveData.position` is an absolute `Vector3` in level.dat (v12), restored verbatim on load. Since **SP-1** the restore runs through the single `Spawn.SpawnResolution` chokepoint in `World.StartWorld` STEP 1 — `Player.LoadSaveData` no longer writes the transform at all.                                                              |
| Physics                 | Fully custom (`VoxelRigidbody`), no PhysX world-space dependency. Operates on `transform.position`; `World.CheckPhysicsCollision:3373` floors the Unity-space AABB straight into `TryGetVoxel(int)` lookups. `ClampToWorldBorder` clamps against ±`BorderRadius`.                                                                                                                                                              |
| Interaction             | `PlacementController.MarchRay` marches camera-space floats, floors to `hitCell`; cells feed `VoxelMod.GlobalPosition` (**persisted** to `pending_mods.bin`) and the highlight/place transforms.                                                                                                                                                                                                                                |
| Chunk visuals           | `Chunk.Reset` assigns `ChunkPosition = Coord.ToWorldPosition()` (`Chunk.cs:89`); `SectionRenderer`s are children (chunk-local, shift-safe). `ChunkLoadAnimation` caches an absolute `_targetPos`.                                                                                                                                                                                                                              |
| Streaming               | `PlayerChunkCoord` is assigned via the private `GetChunkCoordFromVector3` helper (`World.cs:681/:1586/:2587`), not `ChunkCoord.FromWorldPosition` directly; every loop after that is chunk-coord-relative (`Mathf.Abs(coord − playerCoord)`), hence origin-independent already.                                                                                                                                                |
| Shaders                 | `LiquidCore.hlsl` samples noise at `worldPos * scale` (jumps on shift without an offset) and `frac(worldPos)` shore math (invariant under multiple-of-16 shifts). `BorderWallShader` also consumes `worldPos` (§8). No other runtime world-space shaders.                                                                                                                                                                      |
| Jitter observation      | In-game (2026-07-15, post-WS-3): vertex jitter visible at ~10 000 voxels from origin — earlier than the 16k–65k estimate in `WORLD_SCALING_ANALYSIS.md` §3.3.                                                                                                                                                                                                                                                                  |

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

Not persisted: it is derived at startup from the starting player position — *before* any transform is written or
chunk created — at the one place that position exists, SP-1's `SpawnResolution` chokepoint (§4.4).

**Every source anchors, including a fresh world** (decided 2026-07-16, shipped in WS-4b). A fresh world's spawn is
`(800, _, 800)`, so it anchors at chunk `(50, 50)` rather than the identity: the fresh-world case is *not* special.
(WS-4a pinned the identity everywhere, which is what made it a zero-behavior-change phase; that pin is gone.) The
reason to anchor a hard-coded spawn at all is that it will not stay hard-coded — a future spawn-scan system that
searches for a clean surface spawn may land arbitrarily far out, and a path that only anchors "when the save says
so" would silently place that world at a far origin. Anchoring unconditionally means no caller has to remember.

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

Runs in `World.Update` immediately after `PlayerChunkCoord` is assigned and before it is consumed, when
`WorldOrigin.ShouldReanchor(PlayerChunkCoord)` — `Chebyshev(PlayerChunkCoord − OriginChunk) >
ShiftThresholdChunks`. One frame, main thread, allocation-free (`World.ShiftOrigin`):

1. `delta = newOriginChunk − OriginChunk`; `unityDelta = delta × 16` (exact float — a whole number of chunks);
   then `AnchorOrigin(newOriginChunk)` **first**, since everything below reads the new origin.
2. Move every Unity-space object, in two kinds:
    - **Re-derived** from voxel space (never patched, so repeated shifts cannot accumulate drift): every live
      `Chunk` in `_chunkMap` (`Chunk.Reanchor` → `UnityPosition` from `Coord`), the `_chunkBorders` objects (from
      their `ChunkCoord` keys), `VoxelVisualizer.Reanchor`, and the `Clouds` root.
    - **Patched by `−unityDelta`** — transient Unity-space state with no voxel-space source to re-derive from: the
      player transform (re-deriving would *quantize* its sub-voxel position; the camera is a child, and
      velocity/momentum are deltas) and `_lastVisualizerPlayerPos`.
    - In-flight `ChunkLoadAnimation`s take their new target via `Reanchor(newTarget)`, which shifts the transform by
      the *target's* delta so the rise continues with identical relative motion. It deliberately does **not** branch
      on `enabled`: a chunk that has been `Reset` but not yet started is disabled while parked underground, and
      snapping it would undo the anti-flash pre-position.
3. The `_WorldOriginOffset` shader global follows automatically — `AnchorOrigin` pairs it with `SetOrigin` (§4.6),
   so a re-anchor cannot land without it.
4. `AssertPlayerNearOrigin` (`UNITY_EDITOR`/`DEVELOPMENT_BUILD`, `[Conditional]`, latched): player Unity-space XZ ≤
   `(ShiftThresholdChunks + 4) × 16` = 1088 units — turns a missed shift or a drifted site into one loud error
   instead of far-out jitter (§6 false-green guard). It runs *after* the trigger, where a working shift leaves the
   player ≤ 64 chunks (1040 units) out, so it cannot false-positive.

Rebuilt-per-frame consumers need nothing: highlight/place blocks (`PlaceCursorBlocks`) and `BorderWallRenderer`
(`LateUpdate` full rebuild). Two corrections from executing this (v1.6): **the debug screen holds no cross-frame
Unity-space cache** — it recomputes from the transform each interval, so it needs nothing here — and the **`Clouds`
root is cosmetic**, because `UpdateClouds` places each tile player-relative in Unity space. The clouds still get an
explicit `Reanchor()`: `UpdateClouds` is otherwise only driven by `CheckViewDistance`, i.e. on a chunk crossing, and
while a shift frame *is* a chunk crossing today, that is a coincidence and not a contract.

**Must NOT shift:** skybox, directional light, UI, camera-local state. Verified absent: PhysX
bodies, particle systems, tweens (no DOTween usage repo-wide).

**Timing safety:** the shift runs in `Update`; the physics solver runs in `FixedUpdate` — no
mid-solve teleport is possible. Streaming is unaffected: `PlayerChunkCoord` and every distance
loop are voxel-chunk-space values that do not change when the origin moves.

### 4.4 Persistence: voxel space on disk, `ChunkRelativePosition` for the player

Everything persisted is voxel world space. Two changes:

- **WS-4b ✅ shipped:** `Player.GetSaveData` writes `transform.position + OriginVoxel` (voxel-space absolute
  `Vector3`); the startup path sets the origin from the resolved starting position first, then places the transform
  near the Unity origin. Without this pairing, the first post-shift save would corrupt the player position.
  <br>**Where it went (corrected by the WS-4a audit, re-homed by SP-1):** *not* at `World.StartWorld`'s pre-load
  anchor — that runs before `SaveSystem.LoadWorldGameState`, and the anchor must come from the starting player
  position, which only exists once the save is parsed. **SP-1 gave that position exactly one home**, so the
  derivation lives at the `Spawn.SpawnResolution` chokepoint in `StartWorld` **STEP 1**: `ResolveInitial(...)` →
  `AnchorOrigin(ChunkCoord.FromVoxelPosition(spawnPosition))` → `transform = VoxelToUnity(spawnPosition)`, in that
  order — one site covering all three `SpawnSource`s, not three. `World.AnchorOrigin` is the single permitted entry
  point (it pairs `WorldOrigin.SetOrigin` with the `_WorldOriginOffset` shader push so a re-anchor cannot land
  without the shader following). Y is ignored — the origin is XZ-only — so the unresolved-height sentinel passes
  through harmlessly.
  <br>**No version bump:** the field is unchanged (`Vector3`), only the space of the value written into it. Every
  v12 file ever written was saved while WS-4a pinned the origin at `(0, 0)`, so its stored value is *already*
  numerically voxel-space and reads back identically — the change is forward-looking only, and
  `SaveSystem.CURRENT_VERSION` stays **12**. (Confirmed against the `serialization-migration` protocol; the v12→v13
  `ChunkRelativePosition` retype is WS-4c's, and that one *is* a migration.)
  <br>**STEP 4 is not an anchor site:** it runs after chunk creation, so re-anchoring there would need the
  §4.3 translate loop. It stays a plain `VoxelToUnity` conversion.
- **WS-4c ✅ shipped (persistence half):** `PlayerSaveData.position` migrated `Vector3` →
  `ChunkRelativePosition` (**v12→v13**, AOT frozen-DTO protocol, `MigrateLevelDat` only — chunk/region formats
  untouched), removing the ±2²⁴ precision cap on the saved value. `Player.GetSaveData` writes
  `WorldOrigin.UnityToRelative(transform.position)` — the normalizing constructor resolves the small local offset
  exactly, with no large-float round-trip. Rotation, capabilities, and inventory are unchanged.
  <br>**The format is only half the win — the load path has to carry it.** `ChunkRelativePosition` is threaded
  through `SpawnResolution`/`SpawnPlacement` end-to-end, and the new **`WorldOrigin.VoxelToUnity(ChunkRelativePosition)`**
  (the exact inverse of `UnityToRelative`: the chunk distance resolves in `int`, only the local offset is float) is
  what converts at the transform write. STEP 1's anchor becomes simply `AnchorOrigin(spawnPosition.Chunk)` — the
  saved chunk *is* the anchor, no coordinate math at all.
  <br>⚠️ **`ToAbsoluteWorldPosition()` anywhere between the save file and the transform silently undoes all of it.**
  Proven, not asserted: sabotaging the resume to route through it collapses a 2³⁰-voxel save's sub-voxel offset
  from `(5.25, 9.75)` to `(0, 0)` — the Spawn suite's far-resume baseline fails on exactly that.

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

| Phase                                                                             | Scope                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    | Effort | Depends on      |
|-----------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|-----------------|
| **WS-4a — origin plumbing, no shift** ✅ **SHIPPED 2026-07-16, in-game confirmed** | `WorldOrigin` type + helpers; thread every §5 boundary site; call-site audit sweep; suite baselines. Origin pinned `(0,0)` → **zero behavior change**, game bit-identical. Also landed the `_WorldOriginOffset` global + `LiquidCore` sampling (identity-safe at offset 0, so it moved out of WS-4b)                                                                                                                                                                                                     |   🟡   | WS-3 ✅          |
| **WS-4b — the shift** ✅ **SHIPPED 2026-07-17, in-game confirmed**                 | §4.3 trigger + translate loop; `GetSaveData`/load voxel-space fix (§4.4 — **the one boundary WS-4a deliberately left**), a single `AnchorOrigin` at the SP-1 chokepoint rather than three spawn sites; bounded-position assertion. Also closed three WS-4a boundary misses the identity had hidden (§9)                                                                                                                                                                                                  |   🔴   | WS-4a ✅, SP-1 ✅ |
| **WS-4c — persistence** ✅ **SHIPPED 2026-07-17, in-game confirmed**               | `PlayerSaveData.position` → `ChunkRelativePosition`, v12→v13 AOT migration, CRP threaded through the load path (+ the frozen-DTO fix the retype forced on four shipped steps — §9)                                                                                                                                                                                                                                                                                                                       |   🟡   | WS-4b ✅         |
| **WS-4c — tooling** ✅ **SHIPPED 2026-07-18, in-game confirmed**                   | `/teleport` — `CMD-2` of [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) (that doc owns the shipped surface, v1.6). Execution is the thin wrapper this doc predicted: `World.TeleportPlayer` = `ShiftOrigin(destChunk)` + `VoxelToUnity` placement + an arrival hold (data + mesh, 10 s fail-safe). Far verification (±2×10⁷) confirmed degraded-but-stable terrain and surfaced lighting **Bug 19** (`LIGHTING_BUGS.md`) — a far-coords global→local seam, independent of the origin machinery |   🟡   | CMD-1 ✅         |

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
- **WS-4b baselines** ✅ **shipped (Chunk Math 32→35; Validate All 214→217, all three prove-red):**
  *save round-trip anchors near origin* — resuming a far save must land the transform inside the anchor chunk and
  re-save to the identical voxel position (pins §4.4's ordering; the identity-origin case **passes under sabotage**,
  the far ones do not); *re-anchor equivalence* — §7's named baseline: re-derivation and delta-patching agree
  exactly, the point keeps its `voxelCell`, and a patched fractional position keeps its sub-voxel offset;
  *`ShouldReanchor` policy* — fires past the threshold, never within, and is satisfied by its own re-anchor.
  <br>Its shift deltas are **threshold-sized on purpose**: an arbitrary origin jump genuinely breaks the cell
  invariant (the object would then sit 2³⁰ units from the render origin — the very state this design prevents). The
  real trigger fires one chunk past the threshold, and within that bound the arithmetic is float-exact even at the
  ±2³¹ edge, because the offset subtraction happens in `int`.
  <br>**Reach (stated, not implied):** these pin the origin *math*. No editor suite can construct a `Chunk` or drive
  a MonoBehaviour, so the *call sites* — `GetSaveData` actually adding `OriginVoxel`, the translate loop actually
  covering every object — are guarded by review and the in-game gate, not by a baseline. Same class as §4.7's ceiling.
- **WS-4c baselines** ✅ **shipped (Spawn 9→10; Validate All 217→218, prove-red):** *far save resumes exactly* —
  a 2³⁰-voxel save with a sub-voxel offset must arrive with its chunk and its local XZ **bit-identical**. Y is
  compared approximately on purpose: it is a computed sum, and exact equality on a float addition tests the JIT's
  intermediate precision, not the code. The prove-red is the "disk-only" design that was rejected in planning —
  it fails this and nothing else, since the other nine baselines are near-origin and structurally blind to it.
  <br>The migration itself was verified against **real saves rather than a synthetic fixture** — there were ~200 on
  disk spanning v1–v12, which is better evidence than any fixture: the far v1 world `Test 100_000 world`
  (x=809527) through the whole v1→v13 chain, decomposing exactly to chunk 50595 + local 7.5625.

**In-game gate (WS-4b)** ✅ **PASSED 2026-07-17.** The gate was: fly past the 64-chunk threshold repeatedly — no
visual pop at the shift frame; block targeting, placement/break, physics, and fluid edits land on the correct voxels
after multiple shifts; save + reload round-trips; **no jitter at ≥10k** where it is visible today; liquid noise
continuous across a shift.

**Result:** a **pre-existing WS-3 save already ~10k from spawn** was loaded (so the load path was exercised against a
file written before any of this existed), then flown to **~20k through multiple shifts**. No jitter at any point,
nothing rendered or behaved incorrectly. Targeting/breaking/placing correct at ~20k; liquids render and simulate
correctly; clouds correct with no jitter; save → reload returned to the same chunk. The world border was verified
both unshifted (160-voxel radius) and shifted (2560-voxel radius). The fresh-world path (`Fresh` → spawn `(800,800)`
→ anchor `(50, 50)`, §4.1's amendment) was confirmed separately. **One bug found and fixed** — the debug screen's
`WORLD → XYZ` readout (§9). This is the check no suite can perform (§4.7's ceiling), and it earned its place: it
found a defect in a surface v1.3's §9 had explicitly parked as "unverified rather than working".

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
2. **Cached absolute `Vector3` sweep** — ✅ complete, and **re-run with "shift eyes" for WS-4b** (2026-07-17): the
   first sweep asked *which values need converting*; the second asked *which survive a frame and must therefore be
   re-anchored*. Exactly four hold cross-frame position state: `Chunk.UnityPosition` and
   `ChunkLoadAnimation._targetPos` (re-anchored by the §4.3 loop), `World._lastVisualizerPlayerPos` (patched), and
   `PlayerInteraction._lastProbeOrigin` (a same-frame origin snapshot — correct as-is; it is §4.7's
   carry-alongside pattern). **`VoxelRigidbody` is confirmed delta-only** (`Velocity`, `_verticalMomentum`,
   `_movementIntent`), which is what §4.2's "physics needs no rewrite" claim rests on. Benchmark waypoints convert
   at apply; UI/tooltip/toolbar `.position` writes are screen space. `Player.GetSaveData` — the one boundary WS-4a
   deliberately left — was closed by WS-4b (§4.4).
3. **Validation-suite world stubs** — ✅ confirmed. Suites never touch the `WorldOrigin` global: the
   Chunk Math origin scenarios restore the identity in a `finally`, and `PlacementController` takes the
   origin as a **per-probe parameter** (see §4.7), so `PlacementTestWorld` drives far origins with no
   global to leak.
4. **`Chunk.GetVoxelPositionInChunkFromGlobalVector3` callers** — ✅ classified **voxel-space; method does NOT
   convert.** Both callers pass voxel-space values (`World.ApplyModifications`, a `VoxelMod`-derived neighbor;
   `DebugScreen`, a converted target cell), so its callers own the conversion.
   <br>⚠️ **This classification was right about the callers and wrong about the method** — which read the
   Unity-space `ChunkPosition` and so did not honor it. WS-4b fixed the method (§9); the statement above is true of
   the code only as of 2026-07-17. The lesson is in the audit shape: classifying a method's *callers* says nothing
   about whether its *body* keeps the contract.

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
  float-precision artifacts until the v2 rider ships. The *lighting* crash at those magnitudes
  (Bug 19, archived as `_FIXED_BUGS.md` #24) was fixed independently 2026-07-19 — integer column
  routing end-to-end (`SunlightColumnRouting` + `Vector3Int` overloads on the `WorldData`/`ChunkCoord`
  query APIs), so lighting is exact to the ±2³¹ edge even where terrain is degraded.
- **The absolute ±2³¹ voxel edge overflows by construction — documented-only (decided 2026-07-19).**
  At chunk ±2²⁷ the neighbor/halo ±1 arithmetic (`LocalToGlobal`, cross-chunk mod resolution)
  wraps `int`, producing merge faults with `int.MinValue` chunk/local values. No integer-math fix
  exists short of a hard world border inside the edge; it is unreachable in normal play (only
  `/teleport` to the edge hits it), and HF-2 per-job fault isolation contains the damage — the
  world stays playable. Revisit only if a gameplay-facing world bound ever ships.
  <br>**Known symptom inventory at the edge** (all one class, none scheduled for fixing):
  (a) lighting cross-chunk merge faults with `int.MinValue` chunk/local values (contained by HF-2);
  (b) `StandardChunkGenerationJob` structure cell election — `cell × spacing ± padding`
  (`StandardChunkGenerationJob.cs:596`) wraps, inverting the bounds pair so
  `Random.NextInt(min, max)` throws `ArgumentException: min must be less than or equal to max`
  inside the Burst job (observed 2026-07-19; the WS-3 `FloorDiv` rider is exact — it is the
  subsequent multiply that wraps); (c) **quadrant inversion**: int space is a ring, so coordinates
  crossing ±2³¹ arrive in the sign-mirrored quadrant — teleporting "past" the edge is wraparound,
  not clamping. All are Burst-job exceptions or coordinate aliasing at a location where terrain is
  already maximally degraded (the v2 noise rider caps usable radius at ±2²⁴ until it ships, and at
  ±2³¹ permanently by design).
- **Liquid noise input precision degrades cosmetically far out** under the raw
  `_WorldOriginOffset` — same class as today's absolute `worldPos`, liquid-only, accepted.
- ~~**Until WS-4c lands, the saved player position is precise only to ±2²⁴**~~ — **closed 2026-07-17**: it is
  a `ChunkRelativePosition` on disk (v13) and stays chunk-relative all the way to the transform, so it is exact to
  the ±2³¹ edge. The **spawn point** and the terrain height probe still resolve through an absolute `Vector3`
  (`ResolveSpawnHeight` queries the world in absolute voxel space), so a *spawn point* placed past ±2²⁴ still
  rounds — a smaller, separate concern than the player's position, and untouched here.
- **The v13 retype exposed that four shipped level.dat migrations were only accidentally safe.** v3→v4, v6→v7,
  v10→v11 and v11→v12 all round-tripped the whole document through the **live** `WorldSaveData`
  (`FromJson` → mutate one field → `ToJson`). That works for an *additive* change — JsonUtility defaults an absent
  field — and every level.dat change up to v12 was additive. A **re-type** is the case it cannot survive: a v1–v12
  document's `"position":{"x":..,"y":..,"z":..}` has none of the members the new type looks for, so the field is
  silently defaulted and written away. With ~200 saves on disk spanning v1–v12, this would have blanked the player
  position in every one of them, with no error — the backup being the only recourse.
  <br>This is exactly the coupling
  [`AOT_WORLD_MIGRATION_SYSTEM.md`](../Architecture/AOT_WORLD_MIGRATION_SYSTEM.md) §1.2 forbids ("a complete
  rewrite of those classes in the future cannot break old migrations"); those steps simply never adopted frozen
  DTOs, and nothing forced the issue until now. Fixed first, as its own commit, by `LegacyLevelDat` — one frozen
  v1–v12 shape all four now read. **The generalization is in the DTO's header:** a step migrating vN→vN+1 only ever
  sees vN-shaped JSON, so a frozen DTO can never drop a field that did not exist yet — which is why it is safe to
  freeze one shape for the whole era, and why the next re-type must write its own rather than extend it.
- **The teleport command is a first-class console command** (`CMD-2` of
  [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md)), not throwaway dev tooling — but v1
  ships no permissions gating, so it is effectively a cheats-on capability until that seat fills.
- **WS-4a's identity hid three real boundary misses; WS-4b found them all** (2026-07-17). This is the
  false-green §6 predicted, and it is worth stating as a measured outcome rather than a risk: at origin `(0,0)` all
  three were *indistinguishable from correct*, so 214 baselines and two in-game passes stayed green over them. All
  three had one shape — **a Unity-space value read as voxel space**:
    1. **`Chunk.GetVoxelPositionInChunkFromGlobalVector3`** subtracted the (now Unity-space) `ChunkPosition` from a
       voxel-space position. Live in `World.ApplyModifications` — *every* block edit that re-activates a neighbor —
       so it would have pushed out-of-range locals into `AddActiveVoxel` on the first shift. v1.4's §8.1 item 4
       audited this method's **callers** and never read its body. Fixed by delegating to `ChunkMath.VoxelToLocal`,
       which needs no chunk origin at all, so the site cannot be re-confused (and it converged onto the idiom
       `WorldData.TryGetVoxel` already used — it had been a divergent duplicate of a baselined conversion).
    2. **`MeshGenerationJob.ChunkPosition`** was fed `Chunk.ChunkPosition` — a job holding the origin, violating §6.
       Dormant only because its sole consumer (`ClipBounds`) is `Disabled` at every call site. Now fed
       `chunkCoord.ToWorldPosition()`.
    3. **`DebugScreen`'s `WORLD → XYZ`** printed the raw transform (`PopulateTopLeftBuilder`), while the *same
       class's* `UpdateInfrequentData` and `TARGET VOXEL` readouts converted correctly. §5.2's row named the site;
       WS-4a converted part of it. Found in-game at ~20k, where XYZ disagreed with TARGET VOXEL.
       <br>**The prevention, not just the fix:** `Chunk.ChunkPosition` was renamed **`Chunk.UnityPosition`** — a field
       named `ChunkPosition` holding Unity space, in a codebase where "world/chunk position" means voxel space, is
       exactly the wrong-twin trap §3 rejected. `ChunkCoord.FromWorldPosition`'s docstring (which advertised
       `Transform.position` while every caller passed voxel space) now says so, and `FromVoxelPosition` was added to name
       the space at the anchor site.
       <br>**Historical (WS-4a, 2026-07-16):** the origin never left `(0,0)` until WS-4b, so WS-4a had no user-visible
       effect and no in-game evidence was obtainable — its two near-spawn passes proved only "nothing regressed". The
       surfaces those passes could not cover (`Clouds`, `BorderWallRenderer`, save → reload, the `DebugScreen` readout)
       were parked as *unverified rather than working*; WS-4b's gate has now covered all four, and the one that was
       actually broken was the `DebugScreen` readout.
- **Two coordinate sites still ROUND where the engine otherwise floors** — left untouched by WS-4a
  deliberately, so the phase stayed bit-identical. MyBox's `Vector3.ToVector3Int()` extension is
  `RoundToInt`, not `FloorToInt`; only floor names the cell *containing* a position, and every WS-4
  conversion (`WorldOrigin.UnityToVoxelCell`, `MarchRay`, `CheckPhysicsCollision`) floors. The survivors are
  `DebugScreen.cs` (`_groundVoxelPos`, a readout) and `World.ResolveSpawnHeight` (which rounds the spawn XZ before
  the height probe). Both are pre-existing and only diverge for fractional inputs. **SP-1 passed over the second one
  deliberately** (it moved the call, not the rounding) and **WS-4b passed over both again** — it touched the spawn
  path (§4.4) and the debug readout (above) without touching their rounding. Both survive on purpose: changing them
  is a behavior change that deserves its own decision, not a rider on a phase whose contract is narrower. Still
  open for whoever wants it.
- ~~**The saved player position is still Unity-space-shaped.**~~ **Closed by WS-4b** (§4.4): `Player.GetSaveData`
  writes `transform.position + OriginVoxel`, and the SP-1 chokepoint anchors from the saved position before placing
  the transform. Old v12 saves are unaffected — every one was written at `OriginVoxel == 0`. The *precision* limit
  above (±2²⁴) stands until WS-4c.
- **Parent-doc drift to sync (docs-sync, with WS-4a):** `WORLD_SCALING_ANALYSIS.md` §3.3 —
  (a) the `VoxelRigidbody`-on-`ChunkRelativePosition` suggestion is superseded by §4.2 here;
  (b) record the observed ~10k jitter onset alongside the 16k–65k estimate; (c) "chunk positions
  are assigned in exactly one place" undercounts (borders, visualizers, benchmarks — §5.1).

---

## Document History

* **v1.9** - **Bug 19 fixed** (2026-07-19): the far-lands sunlight column-recalc crash §7's far
  verification surfaced is closed — root cause was `WorldData.QueueSunlightRecalculation`'s
  int→float round-trip mis-chunking columns past ±2²⁴ (plus implicit `Vector3Int`→`Vector3`
  conversions at 11 query call sites). Fixed by the shared integer `SunlightColumnRouting` seam +
  `Vector3Int` overloads (auto-capturing every integer caller) + a latched dev-build ±2²⁴ tripwire
  on the float paths; guarded by lighting B95/B96 on a far-anchored harness (prove-red, then
  Validate All 279/279). §9 additionally records the **±2³¹ edge overflow class as documented-only**
  (decision 2026-07-19) and the header/limitations updated to reflect lighting being exact to ±2³¹.
  Same-day addendum: §9 gained the edge symptom inventory — the generation-side `Random.NextInt`
  min>max fault (structure cell election multiply wrap, `StandardChunkGenerationJob.cs:596`) and
  quadrant inversion (int-ring wraparound), both observed at the edge in-game and classified non-issues.
* **v1.8** - **WS-4c's tooling half SHIPPED** (2026-07-18): `/teleport` landed as CMD-2 of
  `COMMAND_CONSOLE_SYSTEM.md` (v1.6 there records the shipped surface + suite B24–B31). The §7
  WS-4 phase table is now fully ✅. Far-teleport verification (±2×10⁷ voxels) confirmed the
  documented terrain degradation past ±2²⁴ and surfaced lighting **Bug 19** (negative
  chunk-local heightmap index in `RecalculateSunlightForColumn` — logged in
  `LIGHTING_BUGS.md`; a global→local column-math seam, not an origin-machinery defect).
* **v1.7** - **WS-4c's persistence half SHIPPED** (2026-07-17), in-game confirmed across multiple migrated saves
  and the fresh-world flow: `PlayerSaveData.position` is a `ChunkRelativePosition` (level.dat **v12→v13**,
  `MigrateLevelDat` only, frozen DTOs on both sides), threaded through `SpawnResolution`/`SpawnPlacement` to the
  transform via the new `WorldOrigin.VoxelToUnity(ChunkRelativePosition)` — so the saved position is exact to the
  ±2³¹ edge instead of ±2²⁴, and STEP 1's anchor is just `spawnPosition.Chunk`. Spawn 9→10, Validate All 217→218,
  prove-red (the rejected "disk-only" design collapses a 2³⁰ sub-voxel offset to `(0,0)`). **The phase's real
  finding is in §9:** the retype exposed that four shipped level.dat migrations round-tripped the live
  `WorldSaveData` and were only ever *accidentally* safe (every prior change was additive) — they would have
  silently blanked the player position in the ~200 v1–v12 saves on disk. Fixed first, as its own commit, via the
  frozen `LegacyLevelDat`. §7 splits WS-4c into a shipped persistence row and a deferred `/teleport` (CMD-2) row at
  the user's request; the ±2²⁴ saved-position limitation is retired, with the spawn point's own absolute-`Vector3`
  probe named as the smaller remaining case.
* **v1.6** - **WS-4b SHIPPED** (2026-07-17), in-game confirmed: the shift trigger + translate loop
  (`World.ShiftOrigin`, `WorldOrigin.ShouldReanchor`, `Chunk`/`ChunkLoadAnimation`/`VoxelVisualizer`/`Clouds`
  `Reanchor`), §4.4's `GetSaveData`/anchor pairing at the SP-1 chokepoint (**no version bump — level.dat stays
  v12**), and the latched bounded-position assertion. Chunk Math 32→35, Validate All 214→217, all three baselines
  prove-red. **§4.1 amended: fresh worlds anchor on their spawn → `(50, 50)`; the identity is no longer the
  fresh-world state.** §9 rewritten around the phase's main finding — the WS-4a identity had hidden **three**
  boundary misses (`Chunk.GetVoxelPositionInChunkFromGlobalVector3`, live in every block edit;
  `MeshGenerationJob.ChunkPosition`, a job holding the origin; `DebugScreen`'s `WORLD → XYZ`, found in-game), all
  the same shape, all invisible at origin `(0,0)`; `Chunk.ChunkPosition` renamed `UnityPosition` and
  `ChunkCoord.FromVoxelPosition` added so the space is in the name. §8.1 item 4 corrected (it audited that method's
  callers, not its body) and item 2's sweep re-run with shift eyes; §4.3 corrected on two counts (the debug screen
  holds no cross-frame cache; the `Clouds` root is cosmetic); §2's identity row marked broken; the saved-position
  limitation retired; `ToVector3Int` round-vs-floor still deliberately untouched.
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

**Last Updated:** 2026-07-19
**Next Review:** when `/teleport` (CMD-2) is scheduled — it needs CMD-1 first — or when the v2 noise rider is,
whose harness it was always meant to be. WS-4's own work is otherwise complete.
