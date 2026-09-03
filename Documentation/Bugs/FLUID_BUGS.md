# Known Fluid related bugs

This document outlines **open** bugs related to fluid behavior and simulation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026

---

## 02. No player effect — ⚠️ PARTIALLY IMPLEMENTED

**Severity:** Missing Feature (visual only)  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`, `World.GatherFluidContact`

The physics half shipped 2026-09-03 (with **#14**, in-game confirmed) — fluid now slows the player,
floats them, carries them on its current, and lets them swim up, down and out onto a bank. Tuned per
fluid in `BlockDatabase.asset`; guarded by `Minecraft Clone/Dev/Validate Physics Solver` `B27`-`B44`.

**Still open — the third bullet only:**

- No on-screen visual to indicate submersion (overlay/tint while the eye line is under the surface).

Designed in [`../Design/UNDERWATER_AND_SUBMERSION_RENDERING.md`](../Design/UNDERWATER_AND_SUBMERSION_RENDERING.md)
(`UW-0`…`UW-6`), which scopes three defects under that one sentence: the liquid pass never renders
from *inside* a fluid body (`Cull Back`), there is no screen-space medium, and there is no waterline
when the eye sits at the surface. Not implemented.

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

