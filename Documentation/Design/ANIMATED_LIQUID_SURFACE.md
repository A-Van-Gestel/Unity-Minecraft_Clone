# Animated Liquid Surface (UW-5)

**Version:** 1.0  
**Date:** 2026-09-05  
**Status:** **Draft — ⏸️ paused on cost/benefit at current priority (2026-09-05).** Not scheduled.
Before implementation starts, re-verify §4's three findings against current code — all three name
files (`LiquidCore.hlsl`'s `LiquidVert`, the MR-2 vertex layout, `FluidSurfaceResolver`) that other
work may have moved.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> The waterline's remaining half. `UW-4` shipped a **geometrically exact** screen split — the locus
> where each ray's submerged length reaches zero — but the boundary is one pixel wide and does not
> move. Softening it means making the *drawn surface* undulate, not decorating the overlay.
> **The decision this document carries forward, paid for by a build that was reverted: no
> screen-space band can put a wavy edge on a straight one.** The liquid mesh's top face is
> flat-topped quads, so a sine band drawn against that edge must cross it — the gap it leaves *is*
> the wave amplitude, and centring the band only halves the error in each direction. The wobble
> belongs to the geometry.

**Audited:** 2026-09-05, at commit `cb1508ed` (branch `feat/fluid-physics`).
The findings in §4 were established while building and playing the reverted screen-space band on
2026-09-05, and the file/line anchors were re-checked against current code this session:
`Shaders/Includes/LiquidCore.hlsl` (`LiquidVert`, `LiquidV2F`),
`Helpers/FluidSurfaceResolver.cs`, `Helpers/EyeSubmersion.cs`, `World.GatherEyeSubmersion` /
`TryResolveEyeCell`, `Helpers/VoxelMeshHelper.cs`'s fluid meshing path, `Audio/SoundManager.cs`,
and `Assets/Editor/Validation/UnderwaterRender/`. **No implementation exists** — nothing from the
reverted build survives in code.

**Relationship to other documents:**

- [`../Architecture/UNDERWATER_AND_SUBMERSION_RENDERING.md`](../Architecture/UNDERWATER_AND_SUBMERSION_RENDERING.md)
  — the shipped system this extends. Owns the eye query, the overlay pass and the hard-edge
  limitation this would close (its §7). It carries `UW-5`'s row in its ID index, pointing here.
- The `UW-*` design doc was deleted at its promotion (`DG-*`). Its §3.6 held the dated account of
  the reverted band, reproduced in §2 below; the original is at
  `git show 5cbc13f3:Documentation/Design/UNDERWATER_AND_SUBMERSION_RENDERING.md`.
- [`../Architecture/FLUID_SHORELINE_RENDERING.md`](../Architecture/FLUID_SHORELINE_RENDERING.md) —
  owns the liquid shader's vertex-channel contract, which §4.2 finds has no room left.
- [`../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md`](../Architecture/SUB_VOXEL_COLLISION_SYSTEM.md) —
  §7 owns `FluidContact`. Physics must **not** see the wave (§4.3).
- [`SOUND_ENGINE_DESIGN.md`](SOUND_ENGINE_DESIGN.md) — the ambience low-pass filter a wobbling
  `EyeDepth` would chatter (§4.3).

---

## 1. Why this is paused, not closed

Animating the liquid mesh is UW-4-sized work — a vertex-stage displacement that keeps the lid
welded to the walls, an eye query that follows the wave, and an answer for what an oscillating
`EyeDepth` does to the ambience gate — bought against a boundary that is **already geometrically
correct** and merely hard-edged.

At current priority that trade does not pay. It is a cost/benefit call at a moment in time, not a
verdict that the effect is unwanted or unreachable. Nothing else waits on it: `UW-6` shipped with
`UW-5` paused, which is what "polish on top of a working effect" was supposed to buy.

---

## 2. What is already done, and must not be rebuilt

`UW-4` shipped the **screen split**. Each pixel solves where its own ray meets the surface plane
and fogs only the segment below it, so the waterline emerges as the locus where the submerged
length reaches zero rather than being drawn. It is exact and tracks pitch *and* roll. This document
does **not** re-open it.

What remains is only the softening: a meniscus band, and a wave.

⛔ **A screen-space meniscus band was built in full, played, and reverted on 2026-09-05. Do not
rebuild it.** It authored `meniscusWidth` / `meniscusWobble` on `BlockType`, published them through
`_SubmersionParams.z` and a new `_SubmersionWaveParams`, and drew the band on the surface plane's
**horizon** — the locus where the view ray runs level, `dot(rayDirection, worldUp) = 0`, which is
an infinite plane's exact image at any eye depth — displaced by a world-anchored sine and gated by
the fog's horizontal bound and by eye depth. Four baselines, all proven red by mutation, all green.
It still failed in play, for two reasons:

1. **It did not line up with the water.** The band sat a wide margin above the drawn surface. The
   horizon is where the plane's image goes *asymptotically*; the surface the player sees is the
   **corner-smoothed mesh** a fraction of a block over their eye. At shallow depth those are
   nowhere near each other.
2. **Even perfectly aligned, it cannot close.** The drawn surface edge is a **straight** line — the
   mesh is flat-topped quads. A sine band drawn against it must cross it: where the wave crests the
   band paints water over sky, where it dips a strip of bare surface shows through. The gap is the
   wave amplitude, by construction.

**Why four green, mutation-proven baselines caught none of it:** every one asserted the band
against the *plane the shader was handed*, never against the surface the mesher **drew**. That is
the gap §5 exists to close.

The two reserved global slots the band used are back to zero and still reserved:
`_SubmersionParams.z` (meniscus half-width) and `_SubmersionParams.w` (v2 distortion).

---

## 3. The route that remains

**Local vertex displacement on the liquid mesh**, with the overlay's band — if it survives at all —
reading the same wave, so the two agree by construction rather than by tuning.

For the waterline to undulate, the *surface* has to undulate. That reframes the whole phase: it is
mesh and shader work in `UberLiquidShader`/`LiquidCore.hlsl` plus a matching change in
`FluidSurfaceResolver`, not a change to `UnderwaterOverlay.shader`.

---

## 4. Three findings this design owes an answer to

Established while building the reverted band, and recorded so a resumed pass starts from them
rather than rediscovering them. All three are the reason the phase is UW-4-sized rather than small.

### 4.1 The lid and the walls have to stay welded

Top-face vertices are identifiable in `LiquidVert` (`Shaders/Includes/LiquidCore.hlsl`) by their
`+Y` normal. The **side** faces' top edges carry horizontal normals and share no marker with the
lid they meet — so displacing only the lid **tears it off the walls**.

Displacing *every* vertex by an XZ-keyed wave welds them, but then it moves the whole water column,
sliding the body against terrain at every shoreline. Neither shape works as stated; the design owes
a third.

### 4.2 There is nowhere to put a "distance from the surface" vertex channel

The obvious weld fix is a per-vertex weight that goes to 1 at the lid and falls off down the side
faces. MR-2's 32-byte vertex layout has **all four color channels, both UVs and `lightData`
spoken for** — see [`../Architecture/FLUID_SHORELINE_RENDERING.md`](../Architecture/FLUID_SHORELINE_RENDERING.md)
for the contract, and `SectionRenderer`'s `Layout` for the authoritative packing.

So the weight has to be **derived** in the vertex stage, not carried. Growing the vertex is a
separate, project-wide decision with its own cost (MR-2 shrank it 60 B → 32 B deliberately) and is
not this phase's to make unilaterally.

### 4.3 A wobbling surface reaches the audio

The shipped system requires the eye query to follow the **drawn** surface — that is the whole point
of `FluidSurfaceResolver` being shared with the mesher. So `SurfaceY` would have to include the
displacement, and then `EyeDepth` **oscillates through zero** for a player floating at the surface.

`EyeSubmersion.IsSubmerged` is the same boolean the ambience low-pass switches on, pinned
bit-exactly by baseline `B16` — so the muffling would chatter with the waves.

Two ways out, both with costs, neither a detail:

- **Hysteresis on the gate.** Keeps one boolean, but the tint and the muffling then lag each other
  by the hysteresis band at exactly the moment a player is looking for the effect.
- **Let audio keep the unwobbled height.** Cheap, but it reopens the visual/audio divergence the
  shared query was built to close.

A third constraint sits beside them: `World.GatherFluidContact`'s height must stay **unaffected**.
Physics reads the logical per-cell template on purpose, and a wave reaching it would oscillate
buoyancy.

---

## 5. What a resumed phase must validate

The reverted band's baselines were green and useless. The gate that would have caught it asserts
against the **mesher's real output**, which is the `B4` pattern the suite already uses:

- **The displaced vertex height read off real `VoxelMeshHelper.GenerateFluidMeshData` output
  matches whatever `FluidSurfaceResolver` reports at the same XZ *and time*** — the `B4` pattern
  extended with time as an input. This is the one that makes the drawn surface and the eye query
  unable to drift.
- **The lid and the side faces' top edges agree at a shared corner** — the tearing case of §4.1,
  asserted on emitted vertices, not on the wave function.
- **The wave is a pure function of world position and time**, so chunk borders cannot seam. (This
  also rules out keying the wobble to screen position, which was weighed and rejected: the wave
  would stay glued to the view and slide sideways whenever the player turns their head.)
- **`World.GatherFluidContact`'s height is unaffected** — §4.3's split must survive, or buoyancy
  oscillates.

None of that is measurable from a fullscreen readback, so the harness is `FluidSurfaceFixture`'s,
not `OverlayFragmentRenderer`'s. New baselines continue the suite's numbering from `B26`.

---

## 6. Constraint compliance

| Project constraint | How this design complies |
|---|---|
| Voxels are packed `uint`s, no per-voxel objects | The wave is a pure function of world position and time. Nothing is stored per voxel. |
| Burst jobs 100 % Burst-compatible | `FluidSurfaceResolver` is already static math over value types with no managed references; a time input keeps it so. |
| No GC / LINQ in hot paths | Vertex-stage work in the shader; no per-frame allocation on the C# side. |
| No `BinaryFormatter`/JSON for terrain | **Zero on-disk change.** Any authored wave parameters sit on `BlockType`, a ScriptableObject — not in the chunk or `level.dat` schema. |
| `BlockIDs` constants, no raw IDs | No block is named; the fluid is whatever occupies the cell. |
| WS-4 coordinate spaces | The wave is keyed on world position; `SurfaceY` stays in Unity space, as `EyeSubmersion` already documents. |
| `#pragma target 4.5` shader floor | Vertex displacement adds no varying if §4.2's weight is derived rather than carried; if it is carried, `LiquidV2F` has 4 of 15 interpolators free. |

---

## Document History

* **v1.0** - Split out of `Design/UNDERWATER_AND_SUBMERSION_RENDERING.md` §3.6 at that document's
  promotion to `Architecture/` (2026-09-05). An Architecture doc describes current code
  and cannot hold a plan for work not done, and the source design is frozen on promotion — so
  `UW-5`'s resume material, including the three banked findings, lives here where it can be edited
  when the phase resumes. `UW-5` keeps its ID; nothing is renumbered.

---

**Last Updated:** 2026-09-05  
**Next Review:** when UW-5 is picked back up — re-verify §4's file anchors first
