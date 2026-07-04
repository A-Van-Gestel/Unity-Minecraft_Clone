# Lighting System Overview

This document provides a complete technical reference for the voxel lighting engine. The system is asynchronous, multi-threaded (via Unity's C# Job System and Burst), and handles two distinct light channels: **Sky light** (monochrome scalar, tinted by `SkyLightColor` in the shader) and **Blocklight** (per-channel RGB).
The design is heavily inspired by the **Starlight** lighting engine (now **Moonrise**), a high-performance replacement for Minecraft's vanilla lighting. Where our implementation diverges from Starlight, this document explains why.

> **Reference implementation:** `_REFERENCES/Moonrise/` contains the full Moonrise source. Key files are in `.../patches/starlight/light/`.

---

## 1. Core Concepts

### 1.1 Data Representation

Light is stored in a separate `ushort[] LightData` array per section (one `ushort` per voxel, 4096 entries per 16×16×16 section). The `uint` voxel data no longer carries light bits — bits 16-23 are reserved/zeroed (see `DATA_STRUCTURES.md`).

| Field        | Bits (ushort) | Range | Purpose                              |
|--------------|---------------|-------|--------------------------------------|
| Sky light    | 0-3           | 0-15  | Light from the sky (monochrome)      |
| Blocklight R | 4-7           | 0-15  | Red channel of block-emitted light   |
| Blocklight G | 8-11          | 0-15  | Green channel of block-emitted light |
| Blocklight B | 12-15         | 0-15  | Blue channel of block-emitted light  |

All light access goes through `LightBitMapping` helpers (`GetSkyLight`, `SetSkyLight`, `GetBlocklightR/G/B`, `PackLightData`, etc.).

**Final light value:** For rendering, the shader applies separate shade curves to sky light (modulated by day/night cycle and tinted by `SkyLightColor`) and blocklight RGB, then takes per-channel `max()`. See `SMOOTH_AND_RGB_LIGHTING.md` §3.6 for the full shader pipeline.

### 1.2 Block Light Properties

Each `BlockType` defines how it interacts with the lighting system:

| Property        | Type        | Meaning                                                                                                |
|-----------------|-------------|--------------------------------------------------------------------------------------------------------|
| `opacity`       | byte (0-15) | How much light is reduced when passing *through* this block. 0 = fully transparent, 15 = fully opaque. |
| `lightEmission` | byte (0-15) | How much light this block emits. 0 = none, 15 = brightest (e.g., lava).                                |

**Derived properties** (computed from opacity):

- `IsOpaque`: `opacity >= 15` — blocks all light completely.
- `IsFullyTransparentToLight`: `opacity == 0` — light passes through unchanged (air, glass).
- `IsLightObstructing`: `opacity > 0` — has some attenuation (used for heightmap).

### 1.3 The Algorithm: Dual-Phase BFS Flood-Fill

The core of the system is a Breadth-First Search (BFS) flood-fill. When a voxel's light changes, the update propagates through neighbors in two sequential phases:

**Phase 1 — Darkness Removal (`PropagateDarkness`):**

1. Dequeue a removal node (position + old light level).
2. For each of 6 neighbors: if the neighbor has light > 0 and light < the removal node's old level, set the neighbor's light to 0 and enqueue it for further removal.
3. If the neighbor's light >= the removal node's old level, it has light from a *different* source. Enqueue it for re-spreading in Phase 2.

**Phase 2 — Light Spreading (`PropagateLight`):**

1. Dequeue a position, read its current light level.
2. For each of 6 neighbors: calculate `targetLevel = sourceLight - max(1, neighborOpacity)`.
3. If `targetLevel > neighborCurrentLight`, update the neighbor and enqueue it for further spreading.
4. **Special rule:** Sky light at level 15 traveling straight *down* through fully transparent blocks does not attenuate (remains 15). This creates the fast vertical sky light columns.
5. **Opaque surface lighting:** Opaque blocks receive light on their surface (`sourceLight - 1`) but are never enqueued for further propagation. Light stops at opaque surfaces.

**Ordering is critical:** All darkness removal completes before any light spreading begins. This ensures the "clean slate" from removal is fully established before new light fills in, preventing ghost light artifacts.

**BFS boundary rule (critical):** The BFS must **never enqueue positions outside the center chunk** for further propagation. Both `PropagateDarkness` and `PropagateLight` use an `IsInCenterChunk(neighborPos)` guard before enqueueing.
Neighbor voxels still have their light **written** via `SetLight` (which routes to `CrossChunkLightMods`), but the BFS does not continue through them. Without this guard, the BFS can exit the center chunk,
travel through a neighbor's data (which may be all-zeros for unloaded chunks, appearing as a void of air), and re-enter the center chunk underground — creating vertical walls of light leaking through solid terrain at chunk borders.
The neighbor's own lighting job handles propagation within its borders after the cross-chunk modifications are applied by the main thread.

---

## 2. Sky Light vs. Blocklight

### 2.1 Blocklight

Blocklight is fully RGB — each light-emitting block defines an emission color `(R, G, B)` with independent 4-bit channels (0-15 each). The BFS propagates three channels independently, using per-channel `max()` at each destination voxel for correct additive color mixing. Each channel attenuates by `max(1, opacity)` per step.

### 2.2 Sky Light

More complex, with special optimizations. Sky light remains a monochrome scalar (0-15) in the BFS; time-of-day color (blue moonlight, warm dawn, red blood moon) is applied as a shader uniform (`SkyLightColor`) at render time.

**Source:** The sky above the chunk (conceptually Y = ChunkHeight), with a starting value of 15.

**Heightmap:** Each chunk maintains a `heightMap[256]` (16x16 columns) storing the Y-level of the highest light-obstructing block per column. This enables fast sky light initialization.

**Column Recalculation (`RecalculateSunlightForColumn`):**

1. **Above the heightmap:** All voxels above the highest opaque block are set to sky light 15.
2. **Horizontal shadow casting:** If the highest block is opaque, check its 4 horizontal neighbors for partial sky light (1-14). If found, set them to 0 and enqueue for darkness removal to clear stale scattered light.
3. **Below the heightmap:** Propagate downward from the highest block with opacity-based attenuation.

After column recalculation, the standard BFS phases handle horizontal spreading.

---

## 3. The Asynchronous Update Loop

### 3.1 Block Modification → Light Queues

When a player places or breaks a block (`ChunkData.ModifyVoxel`):

1. The old sky light and blocklight RGB values are captured from `section.LightData[]` via `LightBitMapping`.
2. The heightmap is updated if the block is light-obstructing.
3. The modified voxel and its 6 neighbors are added to `_sunlightBfsQueue` and `_blocklightBfsQueue`.
4. The chunk is flagged: `HasLightChangesToProcess = true`.

### 3.2 Job Scheduling (`WorldJobManager.ScheduleLightingUpdate`)

On the main thread each frame, `World.Update()` iterates the **ready set** of the lighting dirty-set scheduler (`LightWorkScheduler`, MT-2) — chunks with pending work whose readiness gates can plausibly pass — rather than scanning all loaded chunks or the full dirty set:

1. **Drain staging queue:** Background threads (e.g., deserialization) enqueue positions into a `ConcurrentQueue<Vector2Int>`. The main thread drains this into the ready set at the start of each frame.
2. **Iterate ready set:** For each chunk in the set, check flags (`NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`).
3. Check that all 8 neighbors have finished terrain generation (`AreNeighborsDataReady`).
4. Create snapshot copies of the center chunk map (writable) and all 8 neighbor maps (read-only).
5. Transfer the managed light queues to `NativeQueue`s for the job.
6. Check `SkylightRecalculationQueue` for pending column recalculations (from unloaded neighbor recovery).
7. Schedule the `NeighborhoodLightingJob`.
8. **Self-clean / park:** Remove the chunk from the scheduler when all flags are clear; if flags remain but a readiness gate blocked scheduling, the chunk is parked in a **waiting set** the scan does not visit. Parked chunks are promoted back on the events that can flip their gate (neighbor generation/load completed, lighting job completed, own flag re-set).

A time-based fail-safe full scan (every ~1 second) re-populates the ready set from `worldData.Chunks.Values` and re-promotes the entire waiting set, so any missed registration or promotion degrades to ≤1 s of latency instead of a stall. See [CHUNK_LIFECYCLE_PIPELINE.md](CHUNK_LIFECYCLE_PIPELINE.md) Section 4 for the full pseudocode.

### 3.3 The Job (`NeighborhoodLightingJob`)

**Inputs:** The center + 8 neighbor voxel snapshot maps and the center + 8 neighbor light snapshot maps (all `[ReadOnly]` gather sources), the writable halo-padded voxel/light scratch volumes (`PaddedVoxels`/`PaddedLight`, length `ChunkMath.PADDED_LIGHTING_VOLUME` = 20×128×20), heightmap, light queues, block type data. (LI-1 replaced the old 9-separate-maps-with-a-write-through-hashmap design; the BFS now reads/writes a single padded volume via a branch-free flat index. The center's [0,16) range lives at padded [2,18), with a 2-voxel halo —
`ChunkMath.LIGHTING_HALO = MAX_LIGHTING_BFS_REACH` — carrying the widest cross-seam read.)

**Execution order:**

1. **Worker-thread gather** *(P-2 Phase 1)* — assemble the 9 voxel + 9 light snapshot maps into the halo-padded `PaddedVoxels`/`PaddedLight` volumes (`ChunkMath.GatherPadded*`). This runs first, on the worker thread inside `Execute()` — **not** on the main thread before scheduling — so the main thread pays only the snapshot fill. A missing neighbor is sentinel-filled (`uint`/`ushort.MaxValue`). All subsequent steps read/write the padded volume.
2. **Edge check** *(optional)* — If `PerformEdgeCheck` is set, validate light at all 4 horizontal chunk borders against neighbor data. Border voxels with less light than their neighbor could supply are enqueued for re-spreading. See Section 3.6 for details.
3. **Seed** — Process column recalculation queue, sky light BFS queue, blocklight BFS queue.
4. **Sky light darkness removal** → **Sky light spreading**.
5. **Blocklight darkness removal** → **Blocklight spreading** (per-channel RGB).

**Cross-chunk writes:** The job never mutates the snapshot maps. Instead:

- Neighbor light modifications are added to `CrossChunkLightMods` (a `NativeList<LightModification>`).
- The **halo cells of the padded light volume** are the in-job cross-chunk read-back store: a write into a halo cell updates `PaddedLight` in place, so subsequent reads within the same job execution see the modified value. This replaces the old `NativeHashMap<long, ulong>` write-through cache (deleted in LI-1 — it existed only because the previous design's neighbor arrays were `[ReadOnly]`). It is critical for darkness removal: if we set a neighbor voxel's light to 0, the re-spreading phase must see that 0, not the stale snapshot value.

**Output:** The BFS-updated `PaddedLight` volume (its center [2,18) region is extracted back into the center light map on completion, via `ChunkMath.ExtractCenterLight`), the cross-chunk modifications list, and the `IsStable` flag. The voxel data is never modified by the job.

### 3.4 Result Processing (`WorldJobManager.ProcessLightingJobs`)

Back on the main thread:

1. **Merge light data** into live chunk data via `ApplyLightingJobResult` — the `ushort[] LightData` array is copied back from the job's NativeArray. Block changes made to the `uint` voxel array during job execution are preserved (TOCTOU safety).
2. **Verify pull-back claims** (`VerifyPullBackClaims`, after the merge and the deferred-mod drain): the job records every darkness-wave seam pull-back (§3.7) as a `PullBackClaim` — "I re-lit center voxel X to level L because the neighbor snapshot showed Y" — and the main thread re-checks each claim against the neighbor's **live** data. A superseded claim (the voxel no longer holds L) is skipped; a claim the live neighbor still supports (`CrossChunkLightModApplier.PullBackClaimStillSupported`, the exact `CheckEdgeVoxel` write condition) is kept, so fresh
   snapshots verify for free; an unverifiable claim (neighbor absent/unloaded) is kept conservatively; a **stale** claim (the neighbor darkened after the snapshot) is routed through the sunlight-removal veto below with the claimed neighbor's chunk as the excluded emitter — clearing sourceless ghost light and waking the chunk for the corrective darkness wave (Bug 14 fix; diagnostics: `LastStalePullBacksCleared`). Claims are recorded for center voxels only — the column-recalc shadow-caster path can seed darkness nodes in the halo, and those
   pull-backs surface as ordinary cross-chunk uplift mods instead (guarded by baseline B60).
3. **Apply cross-chunk modifications** to loaded neighbor chunks via the shared `LightingJobProcessor` / `CrossChunkLightModApplier` decision logic (the same code the editor lighting validation suite exercises, so production and harness cannot drift). Routing is decided first (`LightingJobProcessor.RouteCrossChunkMod`): out-of-world mods are dropped, mods for unloaded neighbors are persisted (step 3), mods for a neighbor whose own job is in flight are deferred (see In-flight defer below), and the rest are applied directly. Each applied mod is evaluated
   per-voxel against the neighbor's *current* light (`CrossChunkLightModApplier.Compute`):
    - **Sky light — only-increase guard:** A non-zero sky light mod *lower* than the neighbor's current sky value is skipped. Cross-chunk mods are computed against a stale schedule-time snapshot, so they may only *raise* sky light; the neighbor's own column recalculation is authoritative for decreases. (A mod equal to the current value is also a no-op.)
    - **Sky light — independent-support removal veto (Bugs 11 + 13):** A sky light *removal* (level 0) is skipped when an independent source still supports the current value. Independent support is the max of (a) neighbors *inside the receiving chunk* (`InChunkSunlightSupport`, the Bug 11 veto — stops two adjacent chunks that removed each other's shared seam column against stale snapshots from oscillating forever) and (b) **live** cross-chunk neighbors in chunks *other than the emitter* (`CrossChunkSunlightSupport`, the Bug 13 extension — a border voxel
      legitimately fed across a *different* seam, e.g. the perimeter gradient under a multi-chunk opaque slab, must not be cleared by the Bug 12 initiator; excluding the emitting chunk preserves the initiator's collapse of genuine sourceless seam loops). Support is attenuated by the **target** voxel's own opacity via the shared `LightAttenuation.Attenuate`, and fully-opaque neighbors (which cannot propagate sky light) are excluded. See §3.7 and baselines B48/B49 + B56–B59.
    - **Blocklight — per-channel placement vs. removal:** Placement mods only ever *raise* channels (a zero channel from a stale snapshot never lowers the live value); removal mods let a genuine zero channel through while non-zero channels still MAX-merge. Wake-up nodes report old=0 for channels that did not lose light, so the neighbor's next pass re-spreads an uplift instead of misreading it as a removal (Bug 07).
    - When a mod applies: write the new packed light value and enqueue a BFS wake-up node in the neighbor's light queue (which also sets `HasLightChangesToProcess`).
    - **In-flight defer:** If the target neighbor has its own lighting job in flight (its inputs were snapshotted before this mod existed), the mod is NOT applied — that job's full-LightMap merge would overwrite it and the surviving wake-up node would become a no-op, losing the mod permanently. Instead it is deferred (`_deferredCrossChunkMods`, each entry carrying its **emitter's chunk origin** so the removal veto's emitter exclusion survives the defer/drain path) and drained immediately after the target's own merge.
4. **Handle unloaded neighbors:** If a target neighbor isn't loaded, persist the mod for recovery when the chunk eventually loads, per channel:
    - **Sunlight mods** degrade to affected column coordinates in `LightingStateManager` (`pending_lighting.bin`) — the column recalculation is authoritative for the sky channel.
    - **Blocklight mods** are persisted in full (local position + RGB + removal flag, `pending_blocklight.bin`) and replayed through `CrossChunkLightModApplier` when the chunk loads from disk — a column recalc cannot restore RGB data, so without this, removals (broken lamps) would leave permanent ghost light in the saved neighbor.
5. **Stability check:**
    - If `IsStable`: request mesh rebuild for this chunk and neighbors, and — while `RemainingEdgeCheckRounds > 0` — re-arm the iterative edge-check rounds on this chunk and its cardinal neighbors (§3.6). Stability is first passed through `LightingJobProcessor.IsEffectivelyStable`, which treats a chunk as stable when its only outstanding mods target out-of-world positions.
    - If not stable: set `HasLightChangesToProcess = true` for another pass next frame.

### 3.5 Readiness Gates

Mesh generation is gated by the **relaxed** `AreNeighborsMeshReady`, which requires all 8 neighbors only to:

- Have finished terrain generation.
- Have populated voxel data (`IsPopulated`).
- Have `NeedsInitialLighting = false` (at least one lighting pass complete).

It deliberately does **not** require neighbors to have no running lighting jobs, `HasLightChangesToProcess = false`, or `IsAwaitingMainThreadProcess = false` (the stricter `AreNeighborsReadyAndLit` contract still used for edge-check scheduling). The relaxed gate was introduced to break the wave-front meshing deadlock — see [CHUNK_LIFECYCLE_PIPELINE.md](CHUNK_LIFECYCLE_PIPELINE.md) §3.3 and §9.3. Any stale border lighting is corrected by the automatic re-mesh that fires when the neighbor's lighting job later stabilizes.

> **Note:** `ScheduleMeshing` still checks `HasLightChangesToProcess` / `NeedsInitialLighting` on the **center** chunk before meshing it. And `NeedsEdgeCheck` is intentionally NOT checked by either gate or by `ScheduleMeshing`. Edge checks are corrections that improve quality but do not block the meshing pipeline — a chunk with `NeedsEdgeCheck = true` can be meshed before the edge check runs.

This prevents meshes from being built before their own and their neighbors' first lighting pass completes.

For the complete pipeline flow including all gates, flags, and interactions, see [CHUNK_LIFECYCLE_PIPELINE.md](CHUNK_LIFECYCLE_PIPELINE.md).

### 3.6 Edge Consistency Checking (Starlight-Inspired)

After a chunk's initial lighting stabilizes, its border voxels may have incorrect light due to neighbor load order, dropped cross-chunk modifications, or save/load inconsistencies. The edge checking system detects and corrects these issues.

**Lifecycle (iterative):** Edge checks now run as a small fixed number of *rounds* rather than once, because two adjacent chunks that both stabilize against each other's stale snapshot need more than one reconciliation pass.

1. Each `ChunkData` starts with `RemainingEdgeCheckRounds = 2` (a `[NonSerialized]` counter, reset by `ChunkData.Reset()`). When a lighting job reports `IsStable` (`ProcessLightingJobs`) and rounds remain, the chunk decrements the counter and re-arms its own `NeedsEdgeCheck` + `HasLightChangesToProcess`, then propagates `NeedsEdgeCheck` to its 4 cardinal neighbors via `TriggerNeighborEdgeChecks` (only neighbors that are populated and past initial lighting). Round 1 fixes the immediate frontier; round 2 reconciles the remainder after neighbors have run
   their own edge checks. Chunks loaded from disk with stable lighting also start with `NeedsEdgeCheck = true`.
2. In the main update loop, `NeedsEdgeCheck` is checked after initial lighting but before regular updates. It requires `AreNeighborsReadyAndLit` to fire on the primary path, with a fallback under the weaker `AreNeighborsDataReady` gate when `HasLightChangesToProcess` is also set (see [CHUNK_LIFECYCLE_PIPELINE.md](CHUNK_LIFECYCLE_PIPELINE.md) §7).
3. `WorldJobManager.ScheduleLightingUpdate` reads `chunkData.NeedsEdgeCheck` into the job's `PerformEdgeCheck` flag and clears it.
4. The job's edge check runs as "Pass -1" before the normal BFS seeding.

**Algorithm (`CheckEdges`):**

- Iterates all voxels on the 4 horizontal chunk borders (South z=0, North z=15, West x=0, East x=15).
- For each border voxel, reads the cross-chunk neighbor's light level.
- Calculates `expectedFromNeighbor = max(0, neighborLight - 1 - centerOpacity)`.
- If `expectedFromNeighbor > centerLight`, the center voxel is missing light. Enqueues it in the placement queue for the BFS to correct.

**Design constraint:** The edge check only **adds** missing light (placement queue). It does not remove stale light (no removal queue entries). Removal during edge checks risks propagating false darkness inward when neighbor data is stale or incomplete.

### 3.7 Why Cross-Chunk Light *Removal* Is Structurally Hard

Cross-chunk light **placement** (spreading brighter values inward) is robust: the edge check (§3.6) re-adds any missing light, and a stale snapshot only ever *under*-reports brightness, which a later pass corrects upward. Cross-chunk **removal** (clearing light whose source disappeared) is the engine's recurring problem area — most open/fixed lighting bugs live here (`Documentation/Bugs/`: Bugs 05, 08, 09, 11, 12, 13, 14) — for three compounding reasons:

1. **Jobs read stale schedule-time snapshots.** A job's neighbor maps are copied when it is scheduled (§3.3), so a removal computed against that snapshot can disagree with the neighbor's now-current light. This is the source of the cross-seam removal/re-placement oscillation (Bug 11, fixed) and of the in-flight defer logic (§3.4, Bug 08).
2. **Edge checks only ADD, never remove** (§3.6, §4.2). Over-bright stale light at a border is *never* corrected by the edge-check pass — only too-dark light is. The "inverse artifact" (light that refuses to darken) therefore had **no automatic correction path** and persisted until a full relight. Two initiators have since been supplied for its known mechanisms: the Bug 12 cross-seam removal emit (reason 3 below) and the Bug 14 pull-back claim verification (§3.4 step 2 — the dominant planter of border over-bright, a darkness wave's own stale-snapshot
   re-light, is now re-verified against live data at merge time). Over-bright from any *other* origin still has no corrector.
3. **Removal needs an initiator.** `PropagateDarkness` (§1.3, Phase 1) clears a voxel by tracing the neighbors *it* lit (`neighbor == old − cost`). A light "loop" with no real source — e.g. two seam voxels on opposite sides of a chunk boundary that mutually support each other after the genuine source is removed — had no node to start removal from, so it survived as a **stable-but-wrong** over-bright field (Bug 12, fixed June 2026). The fix supplies the missing initiator across the seam: when a darkness wave meets a cross-chunk neighbor at *exactly* the
   removed level (the 2-cycle signature) and that neighbor is neither fully opaque nor directly sky-exposed, `PropagateDarkness` now emits a cross-chunk sunlight removal mod for it. The neighbor's chunk then re-evaluates through the removal veto: a genuinely independent source keeps the value, the stale loop clears. Guarded by lighting-suite baseline B53 (promoted from repro K12a) + over-correction tripwire B50; only the *symmetric* mutually-equal seam stalls (asymmetric and multi-hop cross-seam loops converge
   regardless, since a level gradient always has a strictly-lower side the existing removal branch handles — completeness baselines B51/B52). **Bug 13 (fixed July 2026)** was this initiator's blind spot at scale: under a multi-chunk opaque slab, the perimeter-fed gradient's seam voxels look exactly like the 2-cycle signature to each other, but their true support crosses a *different* seam — in-chunk support alone could not veto the removal, so initiator → removal →
   pull-back re-light → counter-initiator live-locked the region with a period-2 cycle. The veto's support model was extended to credit **live third-party cross-chunk neighbors** (excluding the emitter — see §3.4), which vetoes the perimeter-fed voxel while leaving B53's genuinely sourceless loops collapsible (baselines B56–B59). **Bug 14 (fixed July 2026)** was the same repro's terminating exit: the darkness wave's own seam **pull-back** (the Bug 07 defect-2
   re-light of a just-darkened border voxel from the neighbor snapshot) trusted a stale snapshot and planted sourceless ghost light nothing revisited; it is now recorded per-write and re-verified against live data at merge time (§3.4 step 2, baselines B60/B61).

**Data-model gotcha when sampling neighbor light for a removal decision:** the stored sky value of an **opaque** voxel is *not* a valid propagation source. `PropagateLight` early-returns for opaque sources (§1.3 rule 5 — opaque blocks receive surface light but never propagate it onward), yet a sky-exposed opaque surface still *stores* a high value (a roof block holds sky 15). Any heuristic that reads neighbor light to estimate "is this voxel still independently supported?" must therefore **skip opaque neighbors** and charge the destination's
`max(1, opacity)` on entry. The cross-chunk sunlight removal veto (`CrossChunkLightModApplier.InChunkSunlightSupport`, added for Bug 11; extended with the live third-party scan `CrossChunkSunlightSupport` for Bug 13) and the pull-back claim check (`PullBackClaimStillSupported`, Bug 14) all had to learn both lessons — see their implementations and baselines B48/B49.

---

## 4. Cross-Reference: Our System vs. Starlight (Moonrise)

This section documents how our lighting engine compares to the Starlight reference implementation. For each Starlight technique, we note whether it's implemented, applicable, or unnecessary given our architecture.

### 4.1 Techniques We Implement Correctly

#### Dual-Phase BFS (Removal → Spreading)

**Starlight:** `performLightDecrease()` runs before `performLightIncrease()`.
**Our system:** Same. All darkness removal completes before light spreading begins, for both channels.
**Status:** Implemented correctly.

#### Vertical Sky Light No-Attenuation Rule

**Starlight:** Sky light at level 15 traveling downward through fully transparent blocks stays at 15.
**Our system:** Same rule in `PropagateLight` (`NeighborhoodLightingJob.cs`).
**Status:** Implemented correctly.

#### Heightmap-Driven Column Optimization

**Starlight:** Uses `heightMapBlockChange[]` to track the lowest Y that needs updating per column.
**Our system:** Uses `heightMap[]` in `RecalculateSunlightForColumn` to skip air above the highest opaque block.
**Status:** Implemented correctly.

#### TOCTOU-Safe Light Merge

**Starlight:** Uses SWMR (Single-Writer Multi-Reader) nibble arrays to separate updating and visible light data.
**Our system:** `ApplyLightingJobResult` merges only light bits, preserving block changes made during job execution.
**Status:** Implemented correctly, different mechanism but same safety guarantee.

#### Opacity-Based Light Attenuation

**Starlight:** `targetLevel = propagatedLevel - max(1, opacity)`.
**Our system:** `targetLevel = sourceLight - max(1, neighborOpacity)`.
Every attenuation site now funnels through the single shared `LightAttenuation.Attenuate` helper (`max(0, s - max(1, opacity))`): the BFS (`PropagateLight`), column recalculation (`RecalculateSunlightForColumn`), the edge check (`CheckEdgeVoxel`), the cross-chunk sunlight removal veto (`InChunkSunlightSupport`), and the validation oracle. Sharing one definition guarantees the formula cannot drift between paths (semi-transparent blocks such as water attenuate identically everywhere).
**Status:** Implemented correctly.

#### Edge Checking on Chunk Load

**Starlight:** Has a dedicated `checkChunkEdges()` method that runs on chunk load. It iterates every block on the 4 horizontal chunk borders and validates that each block's light level is consistent with its neighbors.
**Our system:** Implemented as a `PerformEdgeCheck` flag on `NeighborhoodLightingJob` with a `NeedsEdgeCheck` lifecycle flag on `ChunkData`. Runs for a fixed number of iterative rounds (`RemainingEdgeCheckRounds`, default 2) after each lighting stabilization — re-armed on the chunk and its cardinal neighbors — and once for chunks loaded from disk. See Section 3.6 for details.
**Difference from Starlight:** Our edge check only adds missing light (placement queue), never removes stale light. Starlight's `checkChunkEdges` does both. This is a deliberate constraint — removal during edge checks risks false darkness when neighbor data is incomplete.
**Status:** Implemented (placement-only variant).

#### BFS Chunk Boundary Confinement

**Starlight:** Uses a bounded 5x5 chunk cache. Propagation naturally stops when the cache boundary is reached.
**Our system:** The BFS reads from the 3x3 neighbor grid but is explicitly confined to the center chunk via `IsInCenterChunk()` guards on all queue enqueue operations. Neighbor voxels have their light *written* (via `CrossChunkLightMods`) but are never enqueued for further BFS
propagation.
**Why this is critical:** Without this guard, the BFS exits the center chunk, travels through neighbor data (which may be all-zeros for unloaded chunks — appearing as a void of air), and re-enters the center chunk underground.
This creates vertical walls of light leaking through solid terrain at chunk borders facing unloaded chunks.
**Status:** Implemented correctly.

### 4.2 Missing Techniques (Applicable Improvements)

#### Direction Exclusion in BFS Queue ("No-Backtrack" Logic)

**Starlight:** Each queue entry is a 64-bit `long` encoding position, light level, and a **6-bit direction bitmask**. When block A propagates to neighbor B (e.g., East), the queue entry for B uses `everythingButTheOppositeDirection` to exclude West.
This prevents B from re-checking the block that just lit it.

```
Queue entry layout (64-bit long):
  Bits 0-27:  Encoded position (x | z << 6 | y << 12)
  Bits 28-31: Propagated light level (0-15)
  Bits 32-37: Direction bitset (which of 6 directions to check)
  Bits 38-61: Unused
  Bits 62-64: Flags (FLAG_WRITE_LEVEL, FLAG_RECHECK_LEVEL, FLAG_HAS_SIDED_TRANSPARENT_BLOCKS)
```

The direction bitset indexes into a precomputed lookup table `OLD_CHECK_DIRECTIONS[64]`, where each entry is an array of only the active directions for that bitmask. This avoids iterating all 6 directions every time.

**Our system:** Checks all 6 directions for every queue entry. The level comparison (`targetLevel > neighborLight`) prevents infinite loops, so this is a performance issue rather than a correctness bug.

**Impact:** ~17% unnecessary neighbor checks. Each BFS node visits 6 neighbors instead of 5 (the source direction is always redundant).

**Recommendation:** **Medium priority.** Could be implemented by changing the queue entry to include a `fromDirection` byte and skipping the opposite direction in the propagation loop.
Alternatively, pack direction + level + position into a single `ulong` matching Starlight's layout.

#### `FLAG_RECHECK_LEVEL` for Re-Queued Sources

**Starlight:** During darkness removal, when a neighbor's light is higher than the removal target (indicating an alternative light source), Starlight re-queues it in the *increase* queue with `FLAG_RECHECK_LEVEL` set. During the increase phase, this flag triggers a verification:

```java
if (this.getLightLevel(posX, posY, posZ) != propagatedLightLevel) {
    continue;  // Level changed since we queued this — skip
}
```

This prevents re-propagating light from a position whose level was subsequently modified by another darkness wave.

**Our system:** No equivalent flag. Re-queued positions are placed directly in the placement queue without verification.

**Mitigating factor:** Our system processes all darkness removal before all light spreading (strictly sequential). This means by the time the placement queue is processed, all darkness waves have completed.
The current level read at the start of `PropagateLight` reflects the final state. **This largely eliminates the need for `FLAG_RECHECK_LEVEL` in our architecture.**

**Recommendation:** **Low priority.** Our sequential processing order makes this unnecessary. Starlight needs it because its architecture can interleave decrease and increase operations more aggressively.
If we ever move to a combined queue (interleaved processing), this flag would become necessary.

### 4.3 Starlight Techniques Not Applicable to Our System

#### SWMR Nibble Arrays

**Starlight:** Uses `SWMRNibbleArray` (Single-Writer Multi-Reader) — a custom data structure with separate `storageUpdating` (writer thread) and `storageVisible` (reader threads, volatile). Writers modify `storageUpdating`; `updateVisible()` syncs changes to `storageVisible`.
This allows zero-copy concurrent reads during light propagation.

**Our system:** Uses snapshot copies of chunk data (`GetChunkMapForJob` allocates a `NativeArray<uint>` copy). The job writes to its own copy, and `ApplyLightingJobResult` merges the light bits back.

**Why not applicable:** Unity's Job System enforces read-only access on shared NativeArrays via `[ReadOnly]` attributes. We cannot have a job write to a neighbor chunk's NativeArray while another job reads it.
The snapshot + merge approach is the idiomatic Unity solution and works correctly within Unity's safety system.

#### Null Section Initialization / Extrusion

**Starlight:** Starlight's nibble arrays are separate from block data. A null nibble means "no light data." When a null (empty) section borders a non-empty section,`initNibble()` either sets it to full light (above all blocks) or
"extrudes" the bottom layer of the section above (copies light downward). This ensures empty sections have correct light for neighbor lookups. `checkNullSection()` handles this with a cache (`nullPropagationCheckCache`) to avoid redundant initialization.

**Our system:** Light is stored in a separate `ushort[] LightData` array per section. An "empty" voxel (air, ID=0) still has light data that gets set normally by the BFS. There is no concept of a "null nibble" because every allocated section has a full `LightData` array.

**Why not applicable:** Our per-section `LightData` array means light storage always exists for every voxel in an allocated section. The BFS writes light values into air voxels the same way as any other. Null sections (unallocated) are handled at a higher level — they do not participate in the BFS.

#### Conditionally Opaque Blocks / VoxelShape Face Occlusion

**Starlight:** Supports blocks that are transparent in some directions but opaque in others (e.g., stairs, slabs, glass panes). Uses `VoxelShape.faceShapeOccludes()` for per-face transparency checks.
Queue entries carry `FLAG_HAS_SIDED_TRANSPARENT_BLOCKS` to enable the expensive check only when needed.

**Our system:** Uses a single `opacity` value per block type. Blocks are either uniformly opaque or uniformly transparent — no per-face variation.

**Why not applicable (currently):** We have no block types with directional transparency. If stairs, slabs, or other partial blocks are added in the future, this optimization would become relevant.
At that point, adding a `hasDirectionalOpacity` flag to `BlockTypeJobData` and implementing per-face checks would be necessary.

#### Deferred Light Writes / `FLAG_WRITE_LEVEL`

**Starlight:** Uses `FLAG_WRITE_LEVEL` to defer the actual light write until the queue entry is processed. This allows batch writes and avoids redundant writes when a voxel is updated multiple times in the same pass.

**Our system:** `SetLight` writes immediately when called. Since our BFS processes each position at most once per phase (the level comparison prevents re-processing), redundant writes are already rare.

**Why not applicable:** With our sequential dual-phase approach, each voxel is written at most once during darkness removal (set to 0) and at most once during spreading (set to final level). The overhead of deferred writing would not provide meaningful benefit.

---

## 5. Performance Optimization Opportunities

These are optimizations from Starlight that *are* applicable to our system, ranked by potential impact.

### 5.1 Branchless Neighbor Lookup (Unified Map Buffer)

**Priority: High** | **Complexity: High**

**Current:** `GetPackedData` in `NeighborhoodLightingJob` uses a chain of `if/else if` branches to determine which of 9 NativeArrays to read from, for *every single neighbor check*.

**Starlight approach:** Uses a single contiguous cache array indexed by `(x >> 4) + 5 * (z >> 4) + 25 * (y >> 4)`. No branches — just math.

**Proposed:** Allocate a single `NativeArray<uint>` of size `48 * 128 * 48` (3x1x3 chunks = ~1.2 MB). Copy the 9 chunk maps into the correct offsets at job start. Replace all `GetPackedData` branch logic with:

```csharp
int index = (localPos.x + 16) + (localPos.z + 16) * 48 + localPos.y * (48 * 48);
uint data = unifiedMap[index];
```

### 5.2 Direction Bitmask in Queue Entries

**Priority: Medium** | **Complexity: Low-Medium**

See Section 4.2 above. Pack position + level + direction bitmask into a `ulong` queue entry. Skip the reverse direction when propagating. Use `math.tzcnt` (count trailing zeros) for branchless bit iteration.

### 5.3 Virtual Sky Light (Heightmap-Based Read Optimization)

**Priority: Medium** | **Complexity: Medium**

**Current:** `RecalculateSunlightForColumn` writes sky light=15 to every air voxel above the heightmap via the `ushort LightData[]` array. This generates thousands of memory writes per column.

**Starlight approach:** Treats sky-exposed air as "extruded" — the level 15 is implicit from the heightmap, never explicitly stored.

**Proposed:** Add a heightmap check to `GetSkyLight`:

```csharp
if (y > heightMap[x + 16 * z]) return 15;  // Virtual: no memory read needed
return LightBitMapping.GetSkyLight(lightData[index]);  // Physical: read from LightData
```

This eliminates the write loop and reduces memory bandwidth.

### 5.4 Section-Level Skipping in BFS

**Priority: Medium** | **Complexity: Medium**

**Current:** The BFS propagates through every Y level, even empty sections above the terrain.

**Proposed:** Use the existing `SectionJobData.IsEmpty` flags to skip entire 16-block vertical ranges in the BFS. If a section is empty and the section above is also empty, sunlight is implicitly 15 (virtual skylight) and no propagation is needed.

### ~~5.5 Align BFS Attenuation with Starlight Formula~~ (Fixed)

All three attenuation sites (`PropagateLight`, `RecalculateSunlightForColumn`, `CheckEdgeVoxel`) now use the Starlight-aligned `max(1, opacity)` formula. The previous `1 + opacity` formula over-attenuated semi-transparent blocks (e.g., water) by 1 level compared to column recalculation, causing a 1-level shadow line at chunk borders underwater. See Section 4.2 "Opacity-Based Light Attenuation" for the current comparison.

### 5.6 Column Aggregation for Burst Updates

**Priority: Low** | **Complexity: Medium**

When multiple blocks change in the same vertical column (e.g., explosions, falling sand), deduplicate them into a single column recalculation. Use a `NativeArray<int>` (size 256) to track the lowest modified Y per column, and seed the BFS only from that Y level.

---

## 6. Lighting-Disabled Mode (`enableLighting = false`)

The `enableLighting` setting (`SettingsManager.enableLighting`) is an `[InitializationField]` — it can only be changed from the main menu and requires a world reload to take effect. When disabled, the entire lighting engine is bypassed and every block renders at full brightness (sky light = 15). This section documents where and how the disabled path diverges from the normal pipeline.

### 6.1 Design Intent

The setting exists to fully disable the lighting engine for debugging, performance testing, or aesthetic preference. The goal is simple: **every block, including underground caves, appears at full light level.** No BFS jobs run, no edge checks fire, and no cross-chunk light modifications are processed.

### 6.2 Pipeline Bypass Points

The disabled-lighting path is implemented via guards at specific pipeline entry points, not via a single top-level switch. Each guard prevents work from being enqueued that no job will ever consume:

| Location                                | Guard                                                                        | Purpose                                                                                         |
|-----------------------------------------|------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------|
| `ChunkData.AddToSunLightQueue`          | `enableLighting` check                                                       | Prevents BFS queue entries + `HasLightChangesToProcess` flag                                    |
| `ChunkData.AddToBlockLightQueue`        | `enableLighting` check                                                       | Same, for blocklight channel                                                                    |
| `ChunkData.ModifyVoxel`                 | `lightingEnabled` local                                                      | Sets initial sky light = 15 (not 0); gates `QueueSkylightRecalculation`                         |
| `WorldJobManager.ProcessGenerationJobs` | Stage 2: gates `LightingStateManager` recovery                               | Prevents orphaned recalculation queue entries and stale `HasLightChangesToProcess`              |
| `WorldJobManager.ProcessGenerationJobs` | Stage 3: sky light fill (else branch)                                        | Fills `LightData` with sky=15 on all non-null sections via `LightingHelper.FillUniformSkyLight` |
| `WorldJobManager.ScheduleMeshing`       | `enableLighting` gate on `HasLightChangesToProcess` / `NeedsInitialLighting` | Bypasses lighting-readiness check — meshing proceeds without waiting for lighting               |
| `World.AreNeighborsMeshReady`           | `enableLighting` gate on `NeedsInitialLighting`                              | Same bypass for neighbor readiness                                                              |
| `World.ForceCompleteDataJobsCoroutine`  | Wraps entire Phase 2 lighting loop                                           | Skips all BFS jobs during initial world load; clears stale flags from disk-loaded chunks        |
| `World.Update` lighting scheduler       | `enableLighting` wraps entire block                                          | Skips dirty-set drain, watchdog scan, and job scheduling                                        |
| `GetChunkMapForJob` / `GetMapForJob`    | Post-copy sky light stamp when `enableLighting = false`                      | Stamps sky light=15 on every entry in the light map snapshot via `LightingHelper`               |

### 6.3 Sky Light Fill (Generation Path)

When a chunk completes terrain generation with lighting disabled, `ProcessGenerationJobs` fills `LightData` instead of setting `NeedsInitialLighting`:

```
for each section in chunkData.sections:
    if section is null → skip (avoids allocating sections for air-only volumes)
    LightingHelper.FillUniformSkyLight(section.LightData, skyLevel: 15)
```

The null-section skip is critical: without it, allocating a `ChunkSection` just to fill its `LightData` wastes ~24 KB per section for air-only volumes that meshing never reads (`IsEmpty = true`).

**Job snapshot sky light stamp:** The sky light fill only covers sections that are non-null at the time it runs. Sections allocated *after* the fill — by structure placement via `ApplyModifications` — have their `LightData` initialized to all-zeros by the section pool. Rather than patching individual cases, both `GetChunkMapForJob` and `GetMapForJob` apply a full-array sky light stamp to the light map snapshot when lighting is disabled, using `LightingHelper.StampFullBrightSunlight`. This catches all sources of stale sky light=0 without allocating
physical `ChunkSection` objects.

### 6.4 Block Modification Path

`ModifyVoxel` writes sky light = 15 to the section's `LightData[]` (instead of the normal 0) so every placed block starts at full brightness. The `QueueSkylightRecalculation` call is skipped entirely — without a running BFS engine, queued columns would set `HasLightChangesToProcess = true` with no job to clear it.

The heightmap is still maintained unconditionally (it is cheap and has no downstream consumers when lighting is disabled).

### 6.5 Initial World Load (Coroutine Path)

`ForceCompleteDataJobsCoroutine` Phase 2 is wrapped in `if (settings.enableLighting)`. The `else` branch clears stale lighting flags (`NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`) on all chunks in the initial load area. These flags may have been serialized as `true` from a previous session where lighting was enabled.

### 6.6 Key Invariants

1. **No path may set `HasLightChangesToProcess = true` when lighting is disabled** without a corresponding clear before meshing. The `ScheduleMeshing` gate bypass is a safety net, not a substitute for preventing the flag from being set.
2. **Null sections must remain null.** The sky light fill must skip them to avoid a memory explosion above terrain.
3. **`enableLighting` is `[InitializationField]`** — it cannot be toggled at runtime. All disabled-path logic assumes a consistent value for the entire world session. `ChunkData.Reset()` clears all transient flags on pool recycling, so a world reload with a different setting starts clean.

---

## 7. Key File Reference

| File                                         | Role                                                                                                                                       |
|----------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|
| `Jobs/NeighborhoodLightingJob.cs`            | Core BFS flood-fill job (sky light + RGB blocklight propagation)                                                                           |
| `WorldJobManager.cs`                         | Schedules lighting jobs, processes results, applies cross-chunk modifications                                                              |
| `Data/ChunkData.cs`                          | Heightmap management, light queues, `ModifyVoxel` triggering, `NeedsEdgeCheck` flag                                                        |
| `Data/ChunkSection.cs`                       | Section-level voxel + `LightData` storage, `IsEmpty`/`IsFullySolid` flags                                                                  |
| `Jobs/BurstData/LightBitMapping.cs`          | Bit-packing/unpacking for light values in `ushort LightData[]`                                                                             |
| `Jobs/BurstData/BurstVoxelDataBitMapping.cs` | Bit-packing/unpacking for block ID and metadata in `uint`                                                                                  |
| `Helpers/LightingHelper.cs`                  | Shared lighting utilities (`FillUniformSkyLight`, `StampFullBrightSunlight`)                                                               |
| `Helpers/CrossChunkLightModApplier.cs`       | Pure per-voxel cross-chunk mod decision (stale-snapshot guards, Bug 11 sky veto, wake-up node semantics); shared with the validation suite |
| `Helpers/LightingJobProcessor.cs`            | Cross-chunk mod routing (drop / persist / defer / apply) + effective-stability override                                                    |
| `Jobs/BurstData/LightAttenuation.cs`         | The single shared attenuation formula `max(0, s - max(1, opacity))`                                                                        |
| `Helpers/ChunkMath.cs`                       | Coordinate → flat index conversion                                                                                                         |
| `Serialization/LightingStateManager.cs`      | Persists pending sky light recalculations for unloaded chunks                                                                              |
| `Jobs/StandardChunkGenerationJob.cs`         | Initial heightmap computation during world generation                                                                                      |

**Starlight reference files** (Java, in `_REFERENCES/Moonrise/.../starlight/light/`):

| File                        | Role                                                             |
|-----------------------------|------------------------------------------------------------------|
| `StarLightEngine.java`      | Base class: BFS propagation, queue management, direction bitsets |
| `SkyStarLightEngine.java`   | Sunlight: column propagation, null section handling, extrusion   |
| `BlockStarLightEngine.java` | Blocklight: source detection, emission propagation               |
| `StarLightInterface.java`   | Public API: task queueing, scheduling, edge checking entry point |
| `SWMRNibbleArray.java`      | Thread-safe light data storage (Single-Writer Multi-Reader)      |
