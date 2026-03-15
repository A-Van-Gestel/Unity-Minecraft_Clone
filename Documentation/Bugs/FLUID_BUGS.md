# Known Fluid related bugs

This document outlines **open** bugs related to fluid behavior and simulation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Side face rendering between different fluid levels

**Severity:** Visual Artifact / Performance  
**Files:** `MeshGenerationJob.cs` — fluid face cull logic

Side faces between fluid voxels of different fluid levels are always rendered, causing internal faces to be incorrectly visible and hurting performance. This is currently intentional to allow waterfall-like faces to render, but a better solution should be found.

---

## 02. No player effect

**Severity:** Missing Feature  
**Files:** `Player.cs`, `PlayerInteraction.cs`

Fluid voxels do not currently affect the player:

- Player can walk through fluid without slowing down
- No buoyancy / swimming simulation
- No on-screen visual to indicate submersion

---

## 04. No fluid interaction between different fluid types — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (lines 334–346)

Water and lava currently do not interact with each other. In Minecraft, water touching lava creates cobblestone or obsidian. This is intentionally unimplemented for now — the collision logic is silently skipped (water simply won't flow into lava), which is safe.
Implementing proper fluid interaction requires a new interaction table and is deferred as a feature, not a bug fix.

---

## 07. Missing Source Block Regeneration (Infinite Water) — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`

In Minecraft, two or more adjacent water source blocks (level 0) resting on solid ground will cause an empty air block between them to spontaneously regenerate into a new water source block.
This is the core mechanic behind "infinite water sources". This is currently not implemented.

---

## 08. Missing Lava Viscosity Randomization — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`

Currently, lava spreads horizontally at the exact same deterministic rate as water (just scaled by its `TickRate`).
Minecraft lava has a 75% chance to skip horizontal spreading on any given tick, simulating thicker viscosity and resulting in a much slower, more organic flow pattern.

---

## 09. Missing Flow-Blocking Logic for Non-Solid Blocks — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockType.cs`

Currently, fluid spread is gated purely by whether the target block is `Air` (id 0). Non-solid blocks (e.g., torches, ladders, signs) will simply be washed away or ignored.
We need a fluid-interaction tag or explicit list for specific non-solid blocks that should physically block fluid flow identical to a solid block (e.g., doors preventing water from entering a room).

---

## 10. Missing Dynamic Flow Direction Texturing — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Visuals)  
**Files:** `BlockBehavior.Fluids.cs`, `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), Liquid Shader

In Minecraft, the 2D flow vector is calculated based on surrounding fluid height differentials, and this vector rotates or scrolls the water texture UVs so the water visually "moves" in the direction of the flow.
Currently, our `VoxelMeshHelper` passes `Vector2.zero` for all fluid UVs and relies on a global world-space liquid shader, meaning water cannot visually animate in the direction it is physically spreading.

---

## 11. Missing Unique Textures for Falling Fluids (Waterfalls) — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Visuals)  
**Files:** `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), Liquid Shader

Minecraft uses a distinct, fast-scrolling vertical texture specifically for falling fluid blocks (waterfalls).
Currently, our engine passes no "falling" flag to the liquid shader during mesh generation, so all fluid blocks use the exact same visual properties regardless of whether they are resting or falling.

---

## 12. Missing Lava Fire Spreading — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Simulation)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockStationary.java` (Reference)

In Minecraft, both stationary and flowing lava periodically schedule random ticks that can set nearby air blocks on fire if they are adjacent to flammable blocks.
Our fluid engine currently has no random ticking for fluids after they settle, and lava does not interact with surrounding blocks to ignite them.

---

## 13. Missing Block Displacement & Destruction — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (System)  
**Files:** `BlockBehavior.Fluids.cs`

Currently, our fluids only spread into `BlockIDs.Air`. In Minecraft, fluids can flow into certain non-solid blocks (e.g., tall grass, flowers, torches, redstone, rails).
When they do, the fluid displaces the block, destroys it, and drops it as an item entity.

---

## 14. Missing Entity Pushing & Buoyancy — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Physics)  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`, `Entity` base classes

Flowing liquids in Minecraft apply a physical pushing force to any entities (players, mobs, dropped items) caught inside them, moving them in the direction of the flow vector. Additionally, dropped items float upwards to the surface of water (buoyancy).
Our custom `VoxelRigidbody` physics do not currently query fluid flow vectors or apply buoyancy.

---

## 15. Missing Fluid Particles & Audio — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Visuals/Audio)  
**Files:** (New Particle/Audio Systems required)

Minecraft fluids spawn ambient particles and sounds. Water drips through solid ceilings if water is directly above them. Lava emits popping ember particles above its surface.
Both fluids feature ambient background audio (flowing, bubbling) and interaction audio (splashing, hissing when extinguishing fire). Our engine lacks these environmental details.
