# Coordinate Spaces & Position Naming Guide

The engine works in **five distinct coordinate spaces**. Mixing them up is the single most recurring bug class
in this codebase — it was survivable while Unity space and voxel space were identical, and became a *silent*
bug class the moment the floating origin shipped (WS-4). This guide names each space, fixes the vocabulary
used for variables/parameters/methods, and lists the conversion seams. **New code must use these names; code
reviews may cite this document.**

Related architecture: `Assets/Scripts/Helpers/WorldOrigin.cs` (the WS-4 reference header),
`Assets/Scripts/Data/ChunkCoord.cs` (the three chunk-scale reference header),
`Documentation/Design/WORLD_SCALING_FLOATING_ORIGIN.md`.

---

## 1. The five spaces

| # | Space                        | Naming                                         | Type                     | What lives here                                                                                                                                                                                 |
|---|------------------------------|------------------------------------------------|--------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1 | **Unity (render) space**     | `unityPos`, `unityCell`, `renderPos`           | `Vector3` / `Vector3Int` | `Transform.position`, camera rays, physics AABBs, mesh vertices, everything the GPU sees. Small numbers near the render origin — float math is safe here.                                       |
| 2 | **Voxel world space**        | `voxelPos` (fractional), `voxelCell` (integer) | `Vector3` / `Vector3Int` | World/WorldData queries, `VoxelMod.GlobalPosition`, `ChunkData`, job inputs, everything persisted. Unbounded both signs (WS-3); differs from Unity space by the integer floating-origin offset. |
| 3 | **Chunk index space**        | `chunkCoord`, `originChunk`                    | `ChunkCoord` (preferred) | Chunk identities: dictionary keys, region math, streaming distance loops. `voxel >> 4`.                                                                                                         |
| 4 | **Chunk-local space**        | `localPos`, `localCell`                        | `Vector3Int`             | A voxel's position *within* its chunk: X/Z in `[0, 16)`, Y absolute. `voxel & 15`.                                                                                                              |
| 5 | **Chunk-relative persisted** | `ChunkRelativePosition`                        | `ChunkRelativePosition`  | The on-disk position format (level.dat v13+): a `ChunkCoord` plus a small local float offset — exact at any distance, unlike an absolute float `Vector3`.                                       |

Two auxiliary integer scales derive from #3: **region coordinates** (`chunk >> 5`) and **region-local slots**
(`chunk & 31`) — see `ChunkMath` and `RegionAddressCodec`.

### The precision rule (WS-4)

> All float math stays in **Unity space** (small numbers). Voxel world space is touched only through integer
> cells or exact multiple-of-`CHUNK_WIDTH` offsets. **Nothing ever adds a large float to a small float.**

Concretely: `transform.position + WorldOrigin.OriginVoxel` is a bug (float add of a potentially huge integer —
drifts past ±2²⁴). The correct form is `WorldOrigin.UnityToVoxelCell(transform.position)` — floor first in
small floats, then an exact integer add.

---

## 2. Conversion API map

Convert **at the boundary**, once, and name the result for its new space. Never "fix up" a value deep inside a
callee — that is how a helper ends up serving two spaces (the bug class `WorldOrigin`'s header exists to prevent).

| From → To                        | API                                                                  | Notes                                                                |
|----------------------------------|----------------------------------------------------------------------|----------------------------------------------------------------------|
| Unity → voxel cell               | `WorldOrigin.UnityToVoxelCell(Vector3)`                              | Floor-then-integer-add; exact to ±2³¹.                               |
| Unity → chunk index              | `WorldOrigin.UnityToChunk(Vector3)`                                  | The player-transform → `PlayerChunkCoord` path.                      |
| Unity → persisted                | `WorldOrigin.UnityToRelative(Vector3)`                               | The save path; no large float is ever formed.                        |
| Voxel → Unity                    | `WorldOrigin.VoxelToUnity(Vector3Int / Vector3 / Vector2Int)`        | Placing visuals from voxel data.                                     |
| Persisted → Unity                | `WorldOrigin.VoxelToUnity(ChunkRelativePosition)`                    | The **only** lossless restore of a saved position.                   |
| Voxel (fractional) → chunk index | `ChunkCoord.FromVoxelPosition(Vector3)`                              | Voxel space **only** — never a transform (use `UnityToChunk`).       |
| Voxel origin → chunk index       | `ChunkCoord.FromVoxelOrigin(Vector2Int / Vector3Int)`                | Integer form.                                                        |
| Chunk index → voxel origin       | `ChunkCoord.ToVoxelOrigin()`                                         |                                                                      |
| Voxel int → chunk / local        | `ChunkMath.VoxelToChunk(int)` / `ChunkMath.VoxelToLocal(int)`        | Shift/mask — negative-correct, Burst-safe.                           |
| Voxel float → chunk              | `ChunkMath.WorldToChunk(float)`                                      | Floor-then-shift (see rename backlog: voxel space despite the name). |
| Chunk → region / slot            | `ChunkMath.ChunkToRegion(int)` / `ChunkMath.ChunkToRegionLocal(int)` |                                                                      |

**Never hand-roll** floor-division/modulo chunk math (`Mathf.FloorToInt(pos / 16)`, `% 16` + negative fixup) —
the `ChunkMath` helpers are the only always-correct forms for negative coordinates and are asserted by the
Chunk Math validation suite.

## 3. Rules

1. **Name the space, not the container.** `Vector3` says nothing; `unityPos` vs `voxelPos` says everything.
   A parameter that accepts voxel space is named `voxelPos`/`voxelCell`; docstrings bold the space when the
   signature can't (`<b>voxel-space</b>`).
2. **One space per value.** A variable never holds a Unity-space value on one line and a voxel-space value
   later. Convert into a new, correctly-named variable.
3. **Jobs are voxel-space only.** Nothing under `Assets/Scripts/Jobs/` may reference `WorldOrigin` — the
   pipeline (generation, lighting, meshing, storage) is origin-independent by construction.
4. **Everything persisted is voxel space** (chunk-relative where fractional). A Unity-space value on disk is
   a corruption bug the moment the origin re-anchors.
5. **Pin the origin per operation.** A multi-step operation against one coordinate frame (a ray march, a
   probe + click pair) reads `WorldOrigin.OriginVoxel` once and threads it through — never re-reads the
   global mid-operation (see `PlacementController`'s class remarks for the rationale).
6. **Y never shifts.** The floating origin is XZ-only; Y is identical in Unity and voxel space and needs no
   conversion — but still floors in float space (`UnityToVoxelCell` handles all three axes correctly).

## 4. Rename backlog (legacy misnomers)

These predate the WS-4 vocabulary. They are **documented here, not mass-renamed**, to keep diffs reviewable;
execute individually via the `refactor-safely` skill when touching the surrounding code, and strike through
completed rows.

| Current name                                       | Actual space                                                                      | Target name                                          |
|----------------------------------------------------|-----------------------------------------------------------------------------------|------------------------------------------------------|
| `ChunkCoord.ToWorldPosition()`                     | voxel origin as `Vector3`                                                         | `ToVoxelPositionV3()` (or fold into `ToVoxelOrigin`) |
| `ChunkMath.WorldToChunk(float)`                    | fractional **voxel** coord                                                        | `VoxelToChunk(float)` overload                       |
| `WorldData.GetChunkCoordFor(Vector3)`              | voxel pos → voxel *origin* (misleading: returns `Vector2Int`, not a `ChunkCoord`) | `GetChunkVoxelOriginFor`                             |
| `WorldData.IsVoxelInWorld(Vector3 worldPos)` param | voxel space                                                                       | `voxelPos`                                           |
| `Chunk.GetVoxelPositionInChunkFromGlobalVector3`   | voxel → chunk-local                                                               | `VoxelToLocal(Vector3)`                              |
| `World.GetChunkFromVector3(Vector3 pos)`           | voxel space                                                                       | `GetChunkFromVoxelPosition`                          |
| `CheckForVoxel(Vector3 worldPos)` param            | voxel space                                                                       | `voxelPos`                                           |
| `ChunkRelativePosition.ToAbsoluteWorldPosition()`  | absolute **voxel** `Vector3` (lossy past ±2²⁴ — avoid on precision paths)         | `ToAbsoluteVoxelPosition()`                          |

Historical references inside **frozen** migration steps (`Migration_v12_to_v13_*`'s remarks) keep their
era-accurate names by design — annotate, never rewrite.

---

## Document History

| Version | Date       | Changes                                                                                                                                                      |
|---------|------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1.0     | 2026-07-17 | Initial guide: the five spaces, WS-4 precision rule, conversion API map, naming rules, rename backlog. Extracted from the WS-4 review fix pack (finding #5). |
