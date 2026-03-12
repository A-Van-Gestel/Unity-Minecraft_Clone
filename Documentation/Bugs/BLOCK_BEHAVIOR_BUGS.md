# Known Block Behavior related bugs

This document outlines **open** bugs related to block behaviors (grass spreading, fluid simulation, etc.). Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

## 01. Fluid horizontal flow condition is slightly wrong

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (line 334)

The condition for allowing horizontal fluid flow evaluates to `true` if the neighbor is either non-solid or any fluid block. This means a solid fluid block (if one existed) would incorrectly pass, and water can be adjacent to lava without triggering interaction logic — the spread
is silently skipped rather than triggering a reaction (see also `FLUID_BUGS.md #04`).

---

## 02. Block Behavior Separation

**Severity:** Future Architecture  
**Files:** `BlockBehavior.cs`

Need to combine `Behave` and `Active` logic, or split active collections by block type.  
**Impact:** Performance bottleneck on main thread.

---
