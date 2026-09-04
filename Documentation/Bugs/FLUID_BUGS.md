# Known Fluid related bugs

This document outlines **open** bugs related to fluid behavior and simulation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026

---

## 02. No player effect — ⚠️ PARTIALLY IMPLEMENTED

**Severity:** Missing Feature (visual only)  
**Files (shipped physics half):** `Player.cs`, `Physics/VoxelRigidbody.cs`, `World.GatherFluidContact`  
**Files (open visual half):** `Shaders/UnderwaterOverlay.shader` — `UW-5` adds the waterline split to the
overlay fragment that now carries the tint and fog

The physics half shipped 2026-09-03 (with **#14**, in-game confirmed) — fluid now slows the player,
floats them, carries them on its current, and lets them swim up, down and out onto a bank. Tuned per
fluid in `BlockDatabase.asset`; guarded by `Minecraft Clone/Dev/Validate Physics Solver` `B27`-`B44`.

Designed in [`../Design/UNDERWATER_AND_SUBMERSION_RENDERING.md`](../Design/UNDERWATER_AND_SUBMERSION_RENDERING.md)
(`UW-0`…`UW-6`), which scopes three defects under the remaining bullet: the liquid pass never rendered
from *inside* a fluid body (`Cull Back`), there is no screen-space medium, and there is no waterline
when the eye sits at the surface.

**`UW-1` shipped and confirmed in game 2026-09-04:**

- `Cull Off` on `UberLiquidShader`'s `LiquidForward` pass, so a fluid body is visible from inside it.
  The back-face shell is drawn where culling used to hide it. Confirmed correct at distance under the
  atmospheric fog as well, which the baselines cannot see — they zero the fog range to isolate culling.

**Landed and confirmed in game 2026-09-04 — `UW-0` through `UW-4`, the overlay after eight passes:**

- `submersionColor` / `submersionDensity` on `BlockType`, authored for water and lava; all seven fluid
  coefficients now have `BlockEditor` sliders. The editor copied blocks with hand-written initializer
  lists that had fallen behind the data class in **two** places — `DuplicateSelectedBlock`, and the load
  path, where it was silent data loss: the copy held defaults for all seven and save wrote it back over
  the asset. Both now use `Editor/BlockEditor/Helpers/BlockTypeCloner`, which copies reflectively, and
  water's values were re-authored afterward.
- `World.GatherEyeSubmersion` over `Helpers/FluidSurfaceResolver` — the sub-cell, surface-height-aware
  submersion query, with the mesher's corner smoothing moved into that shared resolver.
- Audio adopted it: `SoundManager` reads the shared query, so a head just under a partly-filled surface
  now muffles where the old per-cell test read dry. `AmbienceResolution.IsSubmerged` is gone.
- **The screen-space medium** (`UW-4`): `Shaders/UnderwaterOverlay.shader` tints the screen in the
  fluid's authored color and fogs it by Beer–Lambert over the part of each pixel's view ray that lies
  below the surface, composited in one alpha-blended pass over the camera color. Driven by
  `Rendering/SubmersionOverlay` and `World.PublishSubmersionGlobals`, drawn by `Rendering/UnderwaterOverlayRendererFeature`, which sits at
  index 0 of `VoxelEngine-URP-Renderer.asset`'s `m_RendererFeatures` — ahead of `UIBlurRendererFeature`,
  so blurred HUD panels show the tinted world rather than an untinted one.

Guarded by `Minecraft Clone/Dev/Validate Underwater Render` (`B1`-`B24`). `B2` and `B3` were confirmed
red before the `Cull Off` line and green after; `B12` and `B17` were each confirmed able to fail by
mutation — dropping the shader's ray-length scale reds the off-center samples while the screen center
stays green, and moving the feature below the UI blur reds the ordering assertion while the feature is
still present.

**Fixed across eight in-game passes, 2026-09-04 — nine items:**

- **The fade re-ran once per voxel cell while sinking.** `World.GatherEyeSubmersion` resolved the
  drawn surface from the eye's *own* cell, whose corners are forced flat when fluid sits above it — so
  `SurfaceY` reported that cell's ceiling and `EyeDepth` reset at every boundary. It now walks up to
  the top of the fluid body (`World.TopOfFluidBody`). `B8` could not catch this because it asserted
  only the depth's sign; `B18` pins the value. ✅ confirmed fixed in game.
- **Water read too cyan** — retuned to `(0.08, 0.24, 0.50)`. ✅ confirmed in game.
- **The fog was too strong** — density `0.14` → `0.05`, moving the half-obscured distance from 5 to
  ~14 blocks.
- **A partly submerged view could switch the medium off entirely.** Fog is now charged per pixel over
  each ray's own submerged length, so the screen splits at the waterline instead of the whole effect
  gating on the eye. Pinned by `B19`, which was proven red by charging every ray its full length.
- **That per-pixel fog then shipped with an inverted vertical sign** — the sky fogged and the water
  clear, showing as a plane across the view within roughly ±20–30° of level. `Blit.hlsl` already flips
  its texcoord on platforms whose textures start at the top, so the shader's `UNITY_UV_STARTS_AT_TOP`
  compensation was a second flip. Pinned by `B20`, which measures the orientation against a marker drawn
  in clip space rather than reasoning about the platform convention. ✅ confirmed fixed in game.
- **And it fogged from *above* the surface too**, painting the medium over a dry cave while the player
  stood in a shallow pool. The surface is a plane but the fluid is a body: from outside, the plane runs
  to the horizon while the pool is a few blocks wide. The overlay is now gated on `IsSubmerged`, which is
  exact — a ray that reaches water terminates at the water, so there is nothing to see through from
  above. Pinned by `B21`.
- **The same plane-versus-body gap from *inside*, at a shoreline.** With the eye just under the surface
  at the body's edge, a ray leaving the water sideways crossed zero water and was still charged to the
  terrain beyond — the boundary face nearest the eye is inside the near clip plane, so it never reaches
  the depth buffer. `EyeSubmersion` now carries the body's ±X/±Z extent and the fragment clamps against
  that box (`B22`). Diagnosed by probing the live frame; the numbers are in the design doc's §3.2.
- **That box then read unstable while swimming**, worst crossing a cell boundary vertically, because its
  extents are re-measured from whichever cell the eye occupies and all four step together. Eased over a
  short time constant in the publish path, snapping on entry so the fog cannot sweep in from the last
  body swum through (`B23`). The easing buys no accuracy and costs a slight lag — `VX-3`/`VX-5` remains
  the exact fix.
- **And most of that instability was not cell quantization at all.** Each horizontal extent is a single
  1-D probe along a world axis, so any block standing in the water truncated that side of the box — one
  voxel measured cutting a side from 23 cells to 6.47. The scan now reports the fluid body's **reach**
  rather than stopping at the first gap, which is correct because a solid block inside the body is an
  occluder the depth buffer already bounds (`B24`).

**Still open — the waterline's polish:**

- The screen now splits geometrically at the surface, but the boundary is a hard one-pixel edge: no
  meniscus band and no wobble. `UW-5` owns both, and its prerequisite is met — `UW-4` is confirmed.
- The medium is bounded by a **box**, not by the fluid body itself, so it stays a proxy. Accepted as
  good enough at the eighth pass rather than tuned further: `VX-3` on `VX-5` is the exact replacement
  and deletes both `_SubmersionBounds` and `World.MeasureHorizontalExtent`.

**Adjacent limitation, not part of this entry:** clouds are not visible through a water surface. The
liquid shader reads what is behind it from `_CameraOpaqueTexture`, which URP fills before any
transparent geometry draws, and `CloudShader` is `Queue="Transparent"`. Cause, consequences and the two
possible fixes are recorded in the design doc's §8; the work belongs to the cloud backlog.

Lava is authored a first pass thicker than water, but a proper lava feel pass has not been done —
`UW-6` owns it.

---

## 04. No fluid interaction between different fluid types — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs` — `HandleFluidSpread`; `Jobs/FluidTickJob.cs` — `HandleFluidSpread` (both paths carry the same neighbor gate)

Water and lava currently do not interact with each other. In Minecraft, water touching lava creates cobblestone or obsidian. This is intentionally unimplemented for now — the collision logic is silently skipped (water simply won't flow into lava), which is safe.
Implementing proper fluid interaction requires a new interaction table and is deferred as a feature, not a bug fix.

---

## 09. Flow-blocking for non-solid blocks is decided by the placement `REPLACEABLE` tag, not a fluid-specific one — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs` — `HandleFluidSpread`; `Jobs/FluidTickJob.cs` — `HandleFluidSpread`; `Data/PlacementRules.cs` (`BlockTags.REPLACEABLE`)

Both spread paths gate each neighbor on `neighborIsAir || neighborIsReplaceable || neighborIsSameFluidAndWorse`, where `neighborIsReplaceable` is `!isSolid && (tags & BlockTags.REPLACEABLE) != 0`. A non-solid block **without** the tag therefore blocks flow exactly like a solid one — flow-blocking is the default for any untagged block.

The gap is the *distinction*, not the blocking. `BlockTags.REPLACEABLE` is the **placement** tag (`1 << 13`, "tall grass etc. can be replaced by placing a block"), and fluids reuse it verbatim as their wash-away set. One bit answers two unrelated questions, so a block cannot be "replaceable when the player places into it, but watertight against fluid" — or the reverse. Doors are the motivating case: one should stop water while remaining a normal solid.

**Consequence to watch:** anything given `REPLACEABLE` for placement reasons silently becomes fluid-washable in the same edit. A fluid-specific tag (or an explicit interaction table) would decouple the two.

---

## 12. Missing Lava Fire Spreading — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Simulation)  
**Files:** `Jobs/FluidTickJob.cs` (the Burst tick since `TG-4`), `BlockBehavior.Fluids.cs` (managed fallback), `BlockStationary.java` (Reference)

In Minecraft, both stationary and flowing lava periodically schedule random ticks that can set nearby air blocks on fire if they are adjacent to flammable blocks.
Our fluid engine currently has no random ticking for fluids after they settle, and lava does not interact with surrounding blocks to ignite them.

---

## 13. Displaced blocks are destroyed but never dropped — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (System)  
**Files:** `BlockBehavior.Fluids.cs`, `Jobs/FluidTickJob.cs`; a drop/item-entity system (does not exist)

Displacement and destruction work: the spread gate admits any non-solid neighbor carrying `BlockTags.REPLACEABLE`, so water flowing into tall grass overwrites it with a fluid voxel.

The **drop** is missing. In Minecraft the displaced block is destroyed *and dropped as an item entity*; here it is overwritten and gone. This is not a fluid defect — the engine has no item-entity system at all (no drop, spawn, or pickup path exists in `Assets/Scripts`), so there is nothing for the fluid to hand the block to.

This entry is therefore blocked on a system that does not exist yet, not on fluid work. Once dropped entities land, the fluid side is a one-line hook at the displacement site, and **#14** (entity pushing and buoyancy) becomes reachable at the same moment — dropped items are its first non-player subject.

**See also #09:** which blocks are washable is decided by the *placement* `REPLACEABLE` tag rather than a fluid-specific one.

---

## 14. Missing Entity Pushing & Buoyancy — ⚠️ PARTIALLY IMPLEMENTED

**Severity:** Missing Feature (Physics) — blocked on the item-entity system for the remainder  
**Files:** `Physics/VoxelRigidbody.cs`, `Physics/FluidContact.cs`, `Physics/FluidContactResolver.cs`, `World.GatherFluidContact`, `Jobs/BurstData/BurstFluidFlowUtility.cs`

Flowing liquids in Minecraft apply a physical pushing force to any entities (players, mobs, dropped items) caught inside them, moving them in the direction of the flow vector. Additionally, dropped items float upwards to the surface of water (buoyancy).

**Shipped 2026-09-03, in-game confirmed:** `VoxelRigidbody` resolves a `FluidContact` once per
`FixedUpdate` and applies buoyancy, vertical and horizontal drag, and the flow push. The flow vector
is not a second implementation — `VoxelMeshHelper`'s corner-flow core moved to
`Jobs.BurstData.BurstFluidFlowUtility` and both meshing and physics call it, so the current a body
feels is the one the shader draws (negated: the meshing vector is a UV scroll offset pointing
upstream). A falling column pushes **down** rather than forwarding that outward vector. Guarded by
`B27`-`B44`; the design decisions are recorded in `SUB_VOXEL_COLLISION_SYSTEM.md` §7 + revision history.

**Still open:** the *entity* half. There are no mobs and no dropped items — the engine has no
item-entity system at all, so `VoxelRigidbody`'s only consumer is the player. Dropped-item buoyancy
stays blocked on the same system as **#13**, and this entry must not be archived until it exists.

---

## 15. Missing Fluid Particles & Audio — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Visuals/Audio)  
**Files:** (New Particle/Audio Systems required)

Minecraft fluids spawn ambient particles and sounds. Water drips through solid ceilings if water is directly above them. Lava emits popping ember particles above its surface.
Both fluids feature ambient background audio (flowing, bubbling) and interaction audio (splashing, hissing when extinguishing fire). Our engine lacks these environmental details.

---

## 16. Suboptimal Fluid Flow Texturing and Vector Math

**Severity:** Improvement (Visuals/Simulation)  
**Files:** `BlockBehavior.Fluids.cs`, `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`

While fluid flow vectors are currently calculated and passed to the shader, the visual result and the underlying simulation math are only "functional" at best.
The bilinear interpolation of flow vectors across fluid surfaces can lead to awkward stretching, pinching, or unnatural texture warping in the `UberLiquidShader`.
Future improvements should refine the flow vector derivatives in the meshing job and implement more advanced flowmap rendering techniques (e.g., improved dual-phase crossfading or flowmap texture synthesis) to achieve a highly polished and natural liquid surface.

**Partial improvements (March 2026):** The flow derivative math in `CalculateSymmetricCornerFlow` was significantly improved with a corner-aware accessibility guard that prevents diagonal air behind walls from creating artificial flow gradients,
while preserving natural waterfall edge pull via `GetEffectiveFluidHeight`. The shore push (`CalculateSymmetricCornerShorePush`) received the same guard with a `FluidType == None` check to prevent fluid blocks from being incorrectly promoted to wall status.

