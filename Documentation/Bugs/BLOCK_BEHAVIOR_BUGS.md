# Known Block Behavior related bugs

This document outlines **open** bugs related to block behaviors (grass spreading, fluid simulation, etc.). Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. `BlockBehavior.s_mods` is a shared static list (thread safety / reentrancy hazard)

**Severity:** Bug (latent)  
**Files:** `BlockBehavior.cs` — `s_mods` (line 13), `Behave` (line 159)

The `Behave` method uses a single shared static `List<VoxelMod>` (`s_mods`) that is cleared and reused on every call. The returned reference is the same `s_mods` list, meaning:

1. If the caller stores the returned reference instead of consuming it immediately, the data will be overwritten on the next `Behave()` call.
2. The comment at lines 243–245 acknowledges this: *"Callers must consume the result immediately and must not store the reference."* `Chunk.TickUpdate()` currently does this correctly (passes directly to `EnqueueVoxelModifications`), but this is fragile — any refactor changing call ordering could introduce silent data corruption.

---

## 02. Grass / dirt block IDs are hardcoded

**Severity:** Improvement  
**Files:** `BlockBehavior.cs` — `Active` (line 70), `Behave` (line 178), `IsConvertibleDirt` (line 263)

Block IDs are hardcoded: grass = `2`, dirt = `3`, air = `0`. If a new block is inserted before these entries or the `BlockDatabase` order changes, grass behavior silently breaks without any compiler error or runtime warning.

A proper solution would reference blocks by name or a dedicated enum/tag.

---

## 03. Fluid horizontal flow condition is slightly wrong

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (line 334)

The condition for allowing horizontal fluid flow evaluates to `true` if the neighbor is either non-solid or any fluid block. This means a solid fluid block (if one existed) would incorrectly pass, and water can be adjacent to lava without triggering interaction logic — the spread is silently skipped rather than triggering a reaction (see also `FLUID_BUGS.md #04`).

---

## 04. Fluid downward flow always places a source block, creating infinite water

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (lines 300–321)

> [!WARNING]
> **SAVE COMPATIBILITY:** Existing waterfalls in saved worlds are made of source blocks and would remain unchanged. New fluid flows after a fix would create non-source "flowing" blocks instead, producing an inconsistency between old and new waterfalls in the same world.

When a fluid flows downward, it places a **source block** (`FluidLevel = 0`) below, making every block in a waterfall column an independent infinite source. Removing the top source does not stop the fall. In Minecraft, falling water creates disposable "flowing" blocks.

**Root cause:** The `VoxelMod` for downward flow never sets `FluidLevel`, so it defaults to `0` (source).
