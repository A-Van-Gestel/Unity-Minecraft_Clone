# World Scaling Analysis — Height/Depth Increase, Negative Quadrants, Cubic Chunks & Floating Origin

> Architectural analysis of what it takes to scale the world beyond its current hard-coded bounds,
> in three tiers of increasing ambition:
>
> - **Tier A** — Taller bounded world with depth (e.g. Y from −128 to +512, still column chunks).
> - **Tier B** — Unbounded XZ: remove `WorldSizeInChunks`, allow negative-quadrant generation,
    > floating origin for render/physics precision.
> - **Tier C** — Full cubic chunks: unbounded Y as well (infinite height and depth).
>
> Each tier builds on the previous one. For every tier: the hard-coded assumptions that break,
> the gotchas (especially silent negative-coordinate bugs), performance/memory implications, and
> recommended approach. Save-format impact is flagged per item — almost everything here is a
> ⚠️ format change requiring the AOT migration protocol (`serialization-migration` skill).
>
> Status: **Analysis / planning.** §3.2 (floor-div audit) shipped as `WS-1`+`VQ-1` (2026-07-12);
> the Tier B execution roadmap now lives in
> [`WORLD_SCALING_IMPLEMENTATION.md`](WORLD_SCALING_IMPLEMENTATION.md) (global-unbounded, sign-split
> phasing WS-2/WS-3/WS-4). This doc remains the "what breaks per tier" reference.

**Analyzed:** 2026-06-12, at commit `39c92ef` (branch `feat/Modular-World-Generation-&-World-Types`).

Related docs: `Architecture/DATA_STRUCTURES.md`, `Architecture/SUB_CHUNK_MESHING_ARCHITECTURE.md`,
`Architecture/LIGHTING_SYSTEM_OVERVIEW.md`,
`Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`,
`Design/PERFORMANCE_IMPROVEMENTS_REPORT.md` (items `P-2`, `LI-1`, `OM-*` are prerequisites or
strong synergies — see §6), `Design/CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`.

---

## 1. Inventory — current hard-coded assumptions

| #  | Assumption                                                                                                                                                      | Where                                                                                                                                                     |               Breaks at tier               |
|----|-----------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------|:------------------------------------------:|
| 1  | `ChunkHeight = 128` compile-time const                                                                                                                          | `VoxelData.cs:7`, mirrored in `ChunkMath.CHUNK_HEIGHT`                                                                                                    |                     A                      |
| 2  | World Y range is `[0, ChunkHeight)` — no negative Y anywhere                                                                                                    | `WorldData.IsVoxelInWorld`, lighting jobs, heightmap, physics                                                                                             |                     A                      |
| 3  | `WorldSizeInChunks = 100`; chunk indices `0–99`; voxel coords `0–1599`                                                                                          | `VoxelData.cs:8`, `ChunkCoord` doc contract, `IsVoxelInWorld` XZ test                                                                                     |                     B                      |
| 4  | `WorldCentre = WorldSizeInVoxels / 2` spawn anchor                                                                                                              | `VoxelData.cs:35`                                                                                                                                         |                     B                      |
| 5  | All-positive coordinates → truncating `/` and `%` "just work"                                                                                                   | e.g. `RegionAddressCodec.V2Codec` step 1 (`chunkVoxelPos.x / ChunkWidth` — **already wrong for negatives**, see §3.2); audit every `/ 16`, `% 16`, `% 32` |                     B                      |
| 6  | Chunk identity is 2D: `ChunkCoord(X, Z)`, `Vector2Int` dictionary keys, `Vector2Int` sunlight columns                                                           | `ChunkCoord.cs`, `WorldData.Chunks`, lighting column queues, region addressing                                                                            |                     C                      |
| 7  | A chunk is a full-height column: per-column `heightMap` (16×16 `ushort`), `SectionUniformSkyLevel` per section array, sunlight recalc walks `ChunkHeight−1 → 0` | `ChunkData.cs`                                                                                                                                            |             C (strained by A)              |
| 8  | Lighting job cache key packs Y into `[0, 255]`                                                                                                                  | `NeighborhoodLightingJob.EncodeNeighborKey` (`y * 48 * 48`, comment "Y: [0,255]")                                                                         | A (at height > 256, and at any negative Y) |
| 9  | Lighting/meshing jobs take a 3×3 *column* neighborhood (8 XZ neighbors, no vertical neighbors)                                                                  | `WorldJobManager.ScheduleLightingUpdate` / `ScheduleMeshing`                                                                                              |                     C                      |
| 10 | Job buffer sizes derive from `ChunkWidth² × ChunkHeight` (32,768 voxels) — pooled at fixed counts                                                               | `ChunkJobArrayPool`, `FillChunkMapForJob`                                                                                                                 |               A (memory ×N)                |
| 11 | Region file = 32×32 chunk *columns*, filename `r.{x}.{z}.bin`, no Y                                                                                             | `RegionAddressCodec`, `ChunkStorageManager.GetRegion`                                                                                                     |                     C                      |
| 12 | Absolute float world positions for transforms, camera, player, shader `worldPos`                                                                                | `Chunk.ChunkPosition`, `SectionRenderer` transforms, `LiquidCore.hlsl` noise/shore coords                                                                 |               B (precision)                |
| 13 | Seed mangled to small positive int (`Mathf.Abs(hash) / 10000` — "world generation shit itself" hack)                                                            | `VoxelData.CalculateSeed`                                                                                                                                 |             B (symptom of #14)             |
| 14 | Noise sampled at absolute float coordinates                                                                                                                     | `StandardChunkGenerationJob` → `FastNoiseLite` (float precision)                                                                                          |            B (far from origin)             |

The good news: **sub-chunk sections (16³) already exist** (`ChunkSection`, `SectionRenderer`,
per-section meshing with `MeshSectionStats`, per-section `IsEmpty`/`IsFullySolid` skips). That is
the single hardest prerequisite for both Tier A and Tier C, and it is done.

---

## 2. Tier A — Taller bounded world with depth (e.g. −128 … +512)

The cheapest way to model depth is to **keep internal storage unsigned** and introduce a single
offset constant at the world-API boundary:

```csharp
public const int WorldMinY = -128;            // sea level shifts; bedrock at WorldMinY
public const int ChunkHeight = 640;           // (-128..512) → 640 = 40 sections
// internal storage Y = worldY - WorldMinY    → always [0, ChunkHeight)
```

Everything inside `ChunkData`, sections, jobs, serialization, and lighting keeps operating on
`[0, ChunkHeight)` — no negative-modulo audit of the entire voxel path. Only the conversion layer
(`WorldData.GetVoxelState`, raycasts, player position → voxel, debug UI) applies `WorldMinY`.
This is also what Minecraft itself does internally (its −64 floor is stored as section index
offsets, not signed per-voxel Y).

### 2.1 What must change

| Change                                                                       | Notes                                                                                                                                                                             |                                                          Save impact                                                          |
|------------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:-----------------------------------------------------------------------------------------------------------------------------:|
| `ChunkHeight`/`CHUNK_HEIGHT` consts (two copies — unify first) + `WorldMinY` | Section count becomes `ChunkHeight/16` everywhere it's derived (it already is)                                                                                                    | ⚠️ Format — chunk files store per-section data; version bump + migration that re-anchors old `[0,128)` data at the new offset |
| `EncodeNeighborKey` Y packing                                                | `[0,255]` cap breaks at 640. Repack: `(long)y * 48 * 48` with y up to 640 still fits a `long` trivially — just fix the comment/derivation; or switch to bit-packed `x:6 z:6 y:12` |                                                        ✅ runtime only                                                         |
| `heightMap` (`ushort`)                                                       | Fine to 65k — no change with offset convention                                                                                                                                    |                                            ✅ (values reinterpreted via migration)                                             |
| Sea level / biome terrain anchors                                            | `SeaLevel`, terrain splines, lode/cave Y ranges are authored against `[0,128)` — all `StandardBiomeAttributes` assets need re-authoring or an import-time offset                  |                                                     ✅ (assets, not saves)                                                     |
| Sunlight column recalc                                                       | Walks from `ChunkHeight−1` down — works as-is, but now walks 640 voxels/column; see §2.2                                                                                          |                                                               ✅                                                               |
| `MeshClipBounds` / benchmark/editor paths using 128                          | Mechanical                                                                                                                                                                        |                                                               ✅                                                               |

### 2.2 Performance implications (the real cost of Tier A)

A 640-high column is **5× the voxels** of today's 128. Every per-chunk full-volume cost in
`PERFORMANCE_IMPROVEMENTS_REPORT.md` multiplies by 5:

- **Job snapshot copies** (pipeline doc §1): ~1.7 MB → **~8.5 MB per lighting job**;
  `ChunkJobArrayPool` retention worst case ~96 MB → ~480 MB. **Unacceptable — `P-1`
  (border-slab copies) or `P-2` (persistent native storage) stops being an optimization and
  becomes a prerequisite.** At minimum, jobs must become *section-ranged*: only copy/process the
  Y-range that actually has content (the per-section `IsEmpty` flags and `SectionUniformSkyLevel`
  already identify uniform air/sky regions — exploit them to skip whole sections in fills,
  lighting BFS seeds, and the `ApplyLightingJobResult` merge). The lighting-side version of this is
  now tracked as **`LI-2`** in `PERFORMANCE_IMPROVEMENTS_REPORT.md` (Y-band / section-ranged halo
  gather — copy mechanism proven by the TG-4 fluid Y-band).
- **`ApplyLightingJobResult` merge scan** (§2 of pipeline doc): 32,768 → 163,840 iterations per
  completed job on the main thread. `P-3` (jobified merge + dirty-section mask) likewise graduates
  from "should" to "must".
- **Memory per loaded chunk**: ~5× voxel + light data. The `OM-1`/`OM-2` device budgets must be
  recomputed; view distance on mobile likely drops.
- **Empty-space waste**: most of the added range is air (sky) or stone (deep). Per-section
  **palette/uniform-section storage** (a section that is 100% air or 100% stone stores one value,
  not 4096×4 bytes — see `Design/CHUNK_PALETTE_MAPPING.md`) changes Tier A's memory cost from
  "5×" to "≈1.2–2×". Strongly recommended to land palettes *before or with* Tier A.

### 2.3 Phasing: height first (A1), depth later (A2) — recommended

Tier A splits cleanly into two phases with almost no duplicated work, **provided the `WorldMinY`
plumbing is built in phase A1 even though its value is still 0**:

- **A1 — taller only (e.g. 0…512):** `ChunkHeight = 512`, `WorldMinY = 0`. All the hard Tier A
  work lives here: constant unification, `EncodeNeighborKey`, the save-format migration for the
  new section count, and the §2.2 performance prerequisites (which scale with *total* height —
  0…512 is 4× volume vs 5× for −128…512, same class). The `WorldMinY` boundary layer costs
  nothing at runtime (`y - 0` folds away) and almost nothing to write — but it is what makes A2
  cheap.
- **A2 — add depth (−128…512):** with the plumbing in place, this reduces to (1) a constants
  change, (2) one more migration step re-anchoring stored sections (+8 section indices), and
  (3) the *content* work — deep terrain generation, bedrock floor, cave/lode Y re-authoring —
  which is unavoidable at that point regardless of sequencing.

**Rework traps (avoid by convention during A1):** any new code that hardcodes "Y=0 is the world
floor" (void checks, raycast clamps, heightmap semantics, lighting bottom boundary) instead of
`WorldMinY`; and skipping the boundary-layer plumbing "because it's zero".

**The one cost unique to A2 — retrogen:** chunks saved before A2 contain no data below old Y=0.
Either old chunks keep a solid floor at 0 and only new chunks get full depth (cheap; visible
underground seam at old/new borders), or a retrogen pass generates `[WorldMinY, 0)` for existing
columns on first post-upgrade load (seamless; a real feature — Minecraft 1.18's "blending"
problem). This cost exists in any sequencing; A1-first just means the migration/world-version
machinery is already proven on the simpler case before retrogen lands.

Net overhead of phasing vs. doing Tier A at once: roughly one extra migration step plus one extra
baseline/test cycle (~10–15%) — in exchange, the 4× volume performance reality (the actual risk
of this work) is validated before committing to depth.

### 2.4 Tier A gotchas

- Two independent copies of the height constant exist (`VoxelData.ChunkHeight`,
  `ChunkMath.CHUNK_HEIGHT`). Unify to one source before touching the value — a mismatch compiles
  fine and corrupts indexing silently.
- The startup load coroutine, lighting safety-break iteration counts, and benchmark expectations
  are tuned for 128-high chunks; re-tune the budgets (or better, land time-based budgets `P-4`
  first).
- Worm carver / cave jobs and structure generation clamp against `ChunkHeight` — audit for
  hardcoded 128s and "spawn at Y=..." literals (`BlockIDs`-style constants generation could be
  extended to emit world-dimension constants from one authoritative asset).

---

## 3. Tier B — Unbounded XZ, negative quadrants, floating origin

### 3.1 Removing the world border

- Delete the XZ test from `WorldData.IsVoxelInWorld` (keep the Y test); delete
  `WorldSizeInChunks` / `WorldSizeInVoxels` / `WorldCentre` (spawn becomes a `level.dat` value —
  the persistent `WorldSpawnPoint` / `ChunkRelativePosition` work already covers this).
- `ChunkCoord`'s documented contract ("Range: 0 – (WorldSizeInChunks-1)") becomes "any int" —
  the struct itself already supports it; update the doc header and every loop that iterates
  `0..WorldSizeInChunks`.

### 3.2 The negative-coordinate audit (the gotcha minefield)

C# integer `/` and `%` **truncate toward zero**; chunk math needs **floor division** and
**positive modulo**. Every site that mixes them up works perfectly in the all-positive world and
breaks only at coordinates < 0 — the worst kind of bug.

> ✅ **RESOLVED 2026-07-12 (WS-1).** The worked example below was the flagship case; the WS-1
> implementation audit refined it. **`RegionAddressCodec.V2Codec.ChunkVoxelPosToRegionAddress` step 1**
> used `chunkVoxelPos.x / VoxelData.ChunkWidth` (truncating). Truncation *would* be wrong for a
> mid-chunk voxel like `voxelX = −8` (yields chunk 0; correct is −1), **but that input is
> unreachable**: every encoder caller passes an exact chunk origin (a multiple of 16), and
> truncating division equals floor division for exact multiples *regardless of sign*. Combined with
> step 2's `Mathf.FloorToInt(chunkX / 32f)` and step 3's manual `if (lx<0) lx+=32` correction, the V2
> encoder was already correct for every reachable input, including negative origins — so this was a
> **latent-but-unreachable** inconsistency, not the "already live" corruption it was first billed as.
> WS-1 still routed all three steps through the `ChunkMath` shift/mask helpers (consistency +
> future-proofing against a raw-voxel caller), byte-identical for the reachable range, so **no V3
> version bump was needed** (see the note after the fix pattern).

**Recommended fix pattern — power-of-two shift/mask, which is simultaneously the fastest and the
only always-correct option:**

```csharp
int chunkX  = voxelX >> 4;     // floor division by 16, correct for negatives
int localX  = voxelX & 15;     // positive modulo 16, correct for negatives
int regionX = chunkX >> 5;     // floor division by 32
int regionLocalX = chunkX & 31;
```

This also removes the current float-roundtrip idiom (`Mathf.FloorToInt((float)x / 16)`, used in
`ChunkCoord.FromVoxelOrigin`, `WorldData.GetChunkCoordFor`, etc.) — which has a second latent bug:
**float conversion loses integer precision beyond ±2²⁴ (≈16.7M)**, so even "correct" float-floor
breaks in a truly infinite world. Audit checklist (grep targets): `/ VoxelData.ChunkWidth`,
`/ ChunkMath.CHUNK_WIDTH`, `% CHUNK`, `/ 32f`, `% 32`, `FloorToInt`, plus `Mathf.Abs` on
coordinates. WS-1 centralized all reachable chunk-math sites into the `ChunkMath` shift/mask helpers
(`VoxelToChunk`/`VoxelToLocal`/`ChunkToRegion`/`ChunkToRegionLocal`/`WorldToChunk`); "forbid inline
chunk math" remains a convention. *(✅ **shipped 2026-07-12** as **`WS-1`** — see
`PERFORMANCE_IMPROVEMENTS_REPORT.md`. The `FloorToInt` count in the report's original framing
included ~37 legitimate world→voxel floors that are **not** chunk math and were correctly left
alone.)*

Region filenames `r.{x}.{z}.bin` handle negative integers fine; the region *header/slot* math is
covered by the shift/mask fix. WS-1 deliberately made **no** V3 version bump — the fix is
byte-identical for every reachable input (all callers pass exact chunk origins), so there is no
buggy on-disk build to detect. The defensive V3 bump is **deferred to Tier B**: introduce it when
negative coordinates first become reachable (riding the border-removal change), so it protects real
data instead of being a no-op version stamp today. ⚠️ Format-adjacent when it lands.

### 3.3 Floating origin (the precision problem)

Float32 has ~7 significant digits: at |pos| ≈ 100k, positions quantize to ~1 cm (visible vertex
jitter, z-fighting, swimming shadows); at ~1M it's ~10 cm (broken). Camera-relative jitter starts
being visible around **16k–65k** units. The engine's internal data model is already mostly immune
— this is a *presentation-layer* problem:

**Already safe (keep it that way):**

- Voxel data, chunk identity, jobs: integer `ChunkCoord` + local indices — exact at any distance.
- Mesh vertices: chunk-local (0–16 per axis after `MeshPostProcessJob`) — exact.
- `ChunkRelativePosition`: the right idea — int macro + float micro. Extend usage from spawn
  point to **the player/camera and any future entities** (the `−` operator already returns exact
  deltas; that's the primitive everything else needs).
- Physics: `VoxelRigidbody` is custom (no PhysX world-space dependency) — make it operate on
  `ChunkRelativePosition` natively and it is origin-independent.

**Needs work — recommended design: periodic origin shift.**

Maintain a `WorldOriginChunk` (a `ChunkCoord`); Unity world position of anything =
`(chunk - WorldOriginChunk) * 16 + local`. When the player strays more than N chunks (e.g. 64 =
1024 units) from the origin, re-anchor:

1. Set `WorldOriginChunk = PlayerChunkCoord` and translate every active chunk GameObject, the
   player, clouds, and particles by the (exact, integer-multiple-of-16) delta in one frame.
   Chunk positions are already assigned in exactly one place (`Chunk.Reset` /
   `ChunkLoadAnimation`) — route them through the origin mapping and the shift is one loop.
2. **Shift by integer multiples of the chunk size only.** This keeps every voxel boundary, every
   `frac()` in shaders, and every texture-tiling computation bit-exact across shifts.
3. **Shaders:** `LiquidCore.hlsl` uses `worldPos` for noise coordinates, `frac(worldPos)` shore
   distances, and flow routing. `frac()` itself survives integer shifts, but the *noise field*
   would visibly teleport. Add a `float3 _WorldOriginOffset` global (set alongside
   `GlobalLightLevel` in `World.cs`) and sample noise at `worldPos + _WorldOriginOffset` — the
   liquid pattern then stays continuous across shifts. Same for any future world-space shader
   effect. (Note: this reintroduces large values into the noise *input* — see §3.4; acceptable for
   cosmetic liquid noise, where a `fmod` by the noise period keeps the input small.)
4. Things that must NOT shift: skybox, directional light, UI. Things that must: clouds plane,
   chunk borders visualizer, debug visualizations, any cached `Vector3` world positions
   (`Chunk.ChunkPosition`, `ChunkLoadAnimation` targets — audit for cached absolute positions;
   prefer deriving from `ChunkCoord` on demand so there is nothing to patch).

A 64-chunk re-anchor radius keeps all rendered geometry within ±(loadDistance+64)·16 ≈ a few
thousand units of origin — float precision ~0.1–0.5 mm. Comfortable.

### 3.4 Generation determinism far from origin

- **Noise input precision:** `FastNoiseLite` is compiled with `float` coordinates here. Noise
  inputs are `worldX * frequency` — at low frequencies the product stays small, but at
  |worldX| ≈ 10⁷ the *input* float quantizes above the noise feature size and terrain develops
  visible stair/streak artifacts. FNL supports double-precision coordinates via its `FNLfloat`
  switch — measure the Burst cost (doubles vectorize at half width) or, cheaper, **pass chunk-local
  coordinates plus a per-chunk double-precision base offset** into the job and let the job add them
  in double once per column, sampling noise at `(double)(baseX + localX) * freq` computed in double
  then narrowed. Decide before Tier B ships; this silently caps the "usable" world radius.
- **Seed handling:** `VoxelData.CalculateSeed`'s `Mathf.Abs(hash) / 10000` hack (self-described as
  working around generation breakage) should be root-caused as part of this work — most likely a
  negative-seed or seed-magnitude issue inside a generator. An infinite world makes seed hygiene
  matter more, not less. (Seed *output* must remain identical for existing worlds — this is the
  one place where a "fix" is ⚠️ seed-breaking by definition. Gate it on world version.)
- **Structure/decorator RNG:** any `random(chunkX * K1 + chunkZ * K2)`-style hashing must use
  mixers that behave for negative inputs (e.g. SplitMix-style avalanche on `(x, z)` packed into a
  `long`), not multiplicative hacks that collide across quadrants (classic "mirrored structures at
  ±coordinates" bug).

---

## 4. Tier C — Full cubic chunks (infinite height and depth)

The most invasive tier. The section infrastructure makes meshing nearly free to adapt — the
**lighting model and the pipeline's dependency gates are the actual project**.

### 4.1 Identity & storage

- `ChunkCoord` → `(X, Y, Z)`; `WorldData.Chunks` keyed by 3D coord (`Vector3Int`/custom struct).
  Introduce a **`ChunkColumn` container** (dictionary of columns → sparse list of loaded cubes +
  per-column shared state). Column-level data that must survive cube granularity: heightmap,
  `SectionUniformSkyLevel` successor, biome/climate cache, region slot grouping.
- Region format: either `r.{x}.{z}.bin` containing 32×32 *columns* each holding a sparse set of
  cubes (closer to today's layout; recommended), or 3D regions `r.{x}.{y}.{z}.bin` (simpler math,
  many more files). Either way: ⚠️ major format version + migration (existing column chunks split
  into 8 cubes — the AOT migration system's frozen-DTO pattern handles this well).
- `ChunkData` (the column) splits into per-cube data + column metadata. The pool-reset-safety
  rules apply to both new types.

### 4.2 The skylight problem (the classic cubic-chunks killer)

Today sunlight is resolved by walking each column top-down from `ChunkHeight−1` — possible only
because the whole column is one load unit. With cubes, **the cube above you may not be loaded**,
so "is the sky visible from here?" is no longer locally answerable. Known solutions, in increasing
fidelity:

1. **Column heightmap authority (recommended).** The `ChunkColumn` owns a persistent heightmap
   (saved per column, updated on block edits). A cube's skylight seeds derive from comparing its Y
   range against the column heightmap — no need for the cubes above to be loaded. This is
   the Cubic Chunks mod's approach and fits the existing heightmap + `SectionUniformSkyLevel`
   machinery naturally (`SectionUniformSkyLevel` generalizes to "cube is entirely above heightmap
   → uniform full sky").
2. Defer skylight for cubes whose above-column isn't resolved (queue, like today's
   `lighting_pending`) — already half-built via `LightingStateManager`.
3. Vertical light propagation across cube borders reuses the existing cross-chunk mod queue —
   it gains a Y direction but no new concepts.

### 4.3 Pipeline & scheduling consequences

- **Neighbor gates go 3D.** `AreNeighborsDataReady` / `AreNeighborsReadyAndLit` move from 8
  XZ neighbors to up to 26 (or 6+edges depending on what lighting/meshing actually read). The
  deadlock surface that produced three historical pipeline deadlocks (see `chunk-lifecycle` skill)
  grows accordingly — the gates, flag pairing, and unload pinning rules all need re-derivation,
  and the §3.3 pinning problem (pipeline doc) becomes more acute: a vertical stack of pinned cubes
  can hold a whole column hostage. **Land `P-4` backpressure and the `OM-*` hardening first;
  cubic chunks multiply every queue's fan-out.**
- **Jobs:** the 9-map lighting/meshing neighborhood becomes 27-map — at which point per-map
  branch dispatch is untenable and the **padded-volume approach (`LI-1`) becomes the only sane
  layout**; design `P-2`'s persistent storage with cubic chunks in mind (3D-keyed, halo-padded
  cubes) so it doesn't have to be rebuilt.
- **Meshing:** already per-section — a cube *is* a section. `MeshGenerationJob` loses its
  section loop and processes one cube with 6 neighbor shells. The shell/`IsFullySolid`
  optimizations carry over unchanged. This is the easy part.
- **Vertical streaming heuristics:** view-distance becomes an ellipsoid (players care more about
  XZ than Y); cave-diving and surface play want different Y budgets. Without this, cubic chunks
  *increase* loaded-chunk counts and memory rather than decreasing them.

### 4.4 Is Tier C worth it?

Honest assessment: Tier A + palettes (§2.2) delivers "tall world with depth" at ~1.2–2× memory
with maybe 15% of Tier C's engineering cost and none of its deadlock risk. Tier C pays off only
if gameplay genuinely needs *unbounded* verticality or per-cube streaming (sky islands at Y=10⁶,
mega-caverns). Recommendation: ship A and B; spec C as its own design doc only when a concrete
gameplay need exists — but make the two architecture-shaping choices now (P-2 persistent storage
3D-keyed + halo-padded; region format with per-column cube sets) so C stays *possible* without a
third storage rewrite.

---

## 5. Cross-cutting gotcha checklist

- [x] **Floor div / positive mod audit** (§3.2) — ✅ **WS-1, 2026-07-12**: shift/mask helpers in
  `ChunkMath`; all reachable chunk-math sites migrated (incl. `RegionAddressCodec` V2 — fixed in
  place, no V3 bump, see §3.2). V3 codec bump deferred to Tier B (no reachable buggy output today).
- [ ] **Unify duplicate constants** (`VoxelData.ChunkHeight` vs `ChunkMath.CHUNK_HEIGHT`) before
  changing either.
- [ ] **`EncodeNeighborKey` Y range** (today `[0,255]`) and any other bit-packed position encodings
  (grep for `<<` near coordinate names, `* 48`, packing comments).
- [ ] **`ushort`/`byte` Y fields**: heightmap values, `LightQueueNode`-style structs, serialized Y
  bytes in the chunk format — every one is a silent truncation at >255 or <0.
- [ ] **`Mathf.Abs` on coordinates or seeds** — almost always a quadrant-mirroring bug in disguise.
- [ ] **Cached absolute `Vector3` positions** (`Chunk.ChunkPosition`, animation targets, debug
  visualizers) — must be derived or patched on origin shift.
- [ ] **Shaders consuming `worldPos`** (`LiquidCore.hlsl` noise/shore/flow) — need
  `_WorldOriginOffset` continuity across origin shifts; keep noise inputs small via period `fmod`.
- [ ] **Save format**: every tier bumps versions — chunk layout (height/offset), region codec
  (negative addressing), level.dat (spawn/world-type params). Never reinterpret old bytes in
  place; always a migration step with frozen DTOs.
- [ ] **Determinism gates**: Tier A re-anchoring and Tier B noise-precision changes alter generated
  terrain by definition → these are **world-version-gated generator changes**, not silent updates.
  Old worlds must keep generating new chunks with the old generator parameters (the modular
  world-type system is the natural home for "generator profile vX").

---

## 6. Sequencing & relationship to the performance backlog

```
Palettes (CHUNK_PALETTE_MAPPING)  ─┐
P-4 backpressure + OM-1/OM-2/OM-3 ─┼─►  Tier A (height/depth)  ─►  Tier B (infinite XZ + floating origin)  ─►  [Tier C if ever]
P-2 persistent storage (3D-ready) ─┘         ▲                                                                    ▲
LI-1 padded lighting volume ────────────────┘  (5× volume makes copy costs prohibitive)                          │
                                                                                            27-neighbor jobs need LI-1/P-2 shapes
```

- **Palettes** turn Tier A's 5× memory into ~1.5× — do them first or together.
- **P-1/P-2 + P-3** are prerequisites for Tier A's job-copy and merge costs (§2.2).
- **OM-1/OM-2** budgets must be parameterized by chunk height anyway — do the parameterization once.
- **LI-1 / P-2** should be designed with 3D keys and halo padding so Tier C never forces a rewrite.
- Tier B's floor-div/shift-mask cleanup (§3.2) is also a micro-optimization win on its own
  (removes float roundtrips from every chunk lookup) — it was the only part of this document with
  **zero** save/seed risk, so it shipped early and independently as **`WS-1`** (✅ **2026-07-12**,
  `PERFORMANCE_IMPROVEMENTS_REPORT.md`). Rather than waiting for CP-2's NS-5 suite, WS-1 landed its
  own equivalence guard in the existing "Chunk Math" validation suite (negative-domain sweeps +
  boundary/teeth cases). The §2.4 constants unification (`VoxelData.ChunkHeight` vs
  `ChunkMath.CHUNK_HEIGHT`) remains open — phase **CP-7** of
  [CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) (2026-07-06).
