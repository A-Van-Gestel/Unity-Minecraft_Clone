# Known Block Behavior related bugs

This document outlines **open** bugs related to block behaviors (grass spreading, fluid simulation, etc.). Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Numbering note:** `§01` and `§04` are **retired, not free** — both are archived in
> [`_FIXED_BUGS.md`](./_FIXED_BUGS.md). `§01` belonged to the fluid horizontal-flow condition, whose
> surviving half is `FLUID_BUGS.md` #04; `§04` to Custom Mesh Collision Support, whose surviving half
> is `VQ-4` (compound multi-AABB shapes). New entries continue from `§05`.

## 02. Block Behavior Separation

**Severity:** Future Architecture  
**Files:** `BlockBehavior.cs` — `Behave` / `Active`; `Chunk.cs` — the behavior-family tick (`TG-1` TODO at the call site)

`Behave` and `Active` are evaluated as separate passes over the same voxel, so each one repeats the other's
chunk lookups. Combining them would halve those lookups — the `TG-1` TODO sits at the `Chunk.cs` call site.

**Scope:** grass only, and only on the main thread. Fluids run in the Burst `Jobs/FluidTickJob.cs`, which
evaluates both halves over a native packed snapshot, and `Chunk.cs` already ticks one behavior-family bucket
at a time — so the "split active collections by block type" half of this entry is covered.

**Impact:** duplicated lookups on the grass tick. Re-measure before scheduling: `TG-4`'s profile gate
concluded grass stays managed, so this is a lookup-count saving rather than a bottleneck fix.

---

## 03. Additional Light Sources

**Severity:** Feature  
**Files:** Block Data

Lava is the only survival-facing light source. Decorative emitters — glowstone, torches, jack-o'-lantern —
are still missing, and need art plus placement rules rather than lighting work: the emission path itself is
exercised by the debug lamps.

**Already available:** `BlockIDs.DebugLamp01`–`DebugLamp11` cover the light levels and
`DebugLamp12Green` / `DebugLamp13Blue` / `DebugLamp14Red` / `DebugLamp15White` the RGB channels. They are
the fixtures the lighting work is verified against.

---
