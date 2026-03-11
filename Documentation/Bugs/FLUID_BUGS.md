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

Water and lava currently do not interact with each other. In Minecraft, water touching lava creates cobblestone or obsidian. This is intentionally unimplemented for now — the collision logic is silently skipped (water simply won't flow into lava), which is safe. Implementing proper fluid interaction requires a new interaction table and is deferred as a feature, not a bug fix.

---

## 05. 7x7 Horizontal Spreading Cube in Mid-Air

**Severity:** Gameplay / Physics bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` Step 4

When a source block is placed on top of an elevated surface (like a tree), the fluid flows outwards and sometimes incorrectly spawns horizontal spreading blocks in mid-air (forming a floating 7x7 water grid) instead of accurately checking if those spread locations have ground support below them. The `isSupportedBelow` check during Step 4 needs further refinement to distinguish between valid adjacent support vs floating.
