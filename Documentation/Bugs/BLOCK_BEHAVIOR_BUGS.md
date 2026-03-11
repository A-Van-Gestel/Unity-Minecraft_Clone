# Known Block Behavior related bugs

This document outlines **open** bugs related to block behaviors (grass spreading, fluid simulation, etc.). Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

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

The condition for allowing horizontal fluid flow evaluates to `true` if the neighbor is either non-solid or any fluid block. This means a solid fluid block (if one existed) would incorrectly pass, and water can be adjacent to lava without triggering interaction logic — the spread
is silently skipped rather than triggering a reaction (see also `FLUID_BUGS.md #04`).

---
