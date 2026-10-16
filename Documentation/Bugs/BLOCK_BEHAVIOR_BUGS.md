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

## 05. Grass under a log never turns to dirt

**Severity:** Implementation (behavior rule never applies in the live world)  
**Files:** `BlockBehavior.cs` / `Jobs/GrassTickJob.cs` — the grass "solid on top → Dirt" rule;
`World.ApplyModifications` — the Default-rule placement gate; `Data/PlacementRules.cs` —
`BlockTagUtility.CanReplaceForPlacement`; the structure expansion in `WorldJobManager.ProcessGenerationJobs` (stage 2)

**Found:** 2026-10-11, by `TickShapeProbe.AuditSleepers` during ES-31 (present on the base build `586f27e8`, not
introduced by ES-31). On seed 4242 at the ES-29 ocean teleport, 492 grass voxels sit under oak logs and leaves and stay
grass.

**What happens:** the tick evaluates the covered grass correctly — `Behave` emits a `Dirt` mod onto the grass itself and
`Active` drops it — but the mod is a Live, `ReplacementRule.Default` mod, and `ApplyModifications` refuses it:
`CanReplaceForPlacement(Dirt, Grass)` is false, so Dirt may not replace Grass. The voxel then leaves its bucket and never
ticks again. Re-registering it changes nothing (verified: one woken voxel ticked, emitted the mod, was refused, dropped).
The harness never sees this: `BehaviorTestWorld` applies mods with `SetVoxel`, so `BH-B7` (grass → dirt under a solid
block) passes while the live world never converts.

**Wanted behavior:** grass under a **log** turns to dirt; grass under **leaves** may stay grass.

**Proposed fix:**
- **Preferred — at generation / structure placement.** When a structure places a trunk block on grass, convert that grass
  to dirt in the same expansion, so the case never reaches the tick. Covers every tree the generator places.
- **Otherwise — at behavior-tick time.** Let the grass tick's own conversion bypass the placement mask (its own cell, a
  rule-owned change, e.g. a forced rule on that mod), and narrow the rule to the blocks that should smother grass — a log
  but not leaves — instead of "any solid block". That narrowing also changes `ActiveVoxelScreen`'s grass rule (keep the
  two in step) and `BH-B7`.
- Either way, add a live-apply guard: the harness's `SetVoxel` apply path is why `BH-B7` could not catch this
  (`BEHAVIOR_VALIDATION_HARNESS_FIDELITY.md` BH-7, "apply-path fidelity").

---
