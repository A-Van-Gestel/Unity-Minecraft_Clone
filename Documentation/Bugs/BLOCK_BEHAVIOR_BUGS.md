# Known Block Behavior related bugs

This document outlines known bugs and major improvements related to block behaviors (grass spreading, fluid simulation, etc.).


## 01. `BlockBehavior.s_mods` is a shared static list (thread safety / reentrancy hazard)

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `s_mods` (line 13), `Behave` (line 159)

The `Behave` method uses a single shared static `List<VoxelMod>` (`s_mods`) that is cleared and reused on every call. The returned reference is the same `s_mods` list, meaning:

1. If the caller stores the returned reference instead of consuming it immediately, the data will be overwritten on the next `Behave()` call.
2. The code comment at line 243–245 acknowledges this: *"Callers must consume the result immediately and must not store the reference"*. However, in `Chunk.TickUpdate()` (line 205), the result is passed directly to `World.Instance.EnqueueVoxelModifications(mods)`, which iterates and enqueues each mod. This works correctly *today* because the next `Behave()` call doesn't happen until the next loop iteration — but it's fragile and any refactor that changes call ordering could introduce data corruption.


## 02. Grass block ID is hardcoded

**Severity:** Improvement  
**Files:** `BlockBehavior.cs` — `Active` (line 70), `Behave` (line 178), `IsConvertibleDirt` (line 263)

Block IDs are hardcoded: grass = `2`, dirt = `3`, air = `0`. This creates a tight coupling between the behavior code and the order of entries in the `BlockDatabase` asset. If a new block is inserted before these entries or their order changes, the grass behavior silently breaks without any compiler error or runtime warning.

A proper solution would be to reference blocks by name or by a dedicated enum/tag, rather than raw integer IDs.


## 03. Fluid horizontal flow condition is slightly wrong

**Severity:** Bug  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (line 334)

The condition for allowing horizontal fluid flow checks:
```csharp
if (neighborState.HasValue && (!neighborState.Value.Properties.isSolid || neighborState.Value.Properties.fluidType != FluidType.None))
```

This condition evaluates to `true` if the neighbor is **either** non-solid **or** a fluid block. However, this means a solid fluid block (if such a thing existed in the data) would pass the check even if it shouldn't be flowable. More importantly, the logic allows flowing *into* a non-solid block that is a *different* fluid type (e.g., water flowing into lava), because the inner check on line 339 only validates same-type interaction. The outer guard should be more restrictive.


## 04. Fluid `FluidLevel` is set redundantly in `HandleFluidFlow`

**Severity:** Code Quality  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (lines 344, 346)

The `FluidLevel` is set twice on the same `VoxelMod`:
```csharp
VoxelMod mod = new VoxelMod(globalNeighborPos, blockId: currentId)
{
    FluidLevel = newLevel,     // Line 344
};
mod.FluidLevel = newLevel;     // Line 346 (redundant)
```

This is harmless but indicates copy-paste from a refactor — the initializer expression on line 344 already sets the value.


## 05. Fluid downward flow always places a source block, creating infinite water

**Severity:** Bug (by design, but worth noting)  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (lines 300–321)

> [!WARNING]
> **SAVE COMPATIBILITY:** Existing saved worlds contain waterfalls made of source blocks (`FluidLevel = 0`). These would **remain as source blocks** after this fix (saved data is unchanged), meaning old waterfalls keep their infinite behavior. However, any **new** fluid flows would behave differently (creating non-source "flowing" blocks instead). This creates an inconsistency between old and new waterfalls in the same world.

When a fluid flows downward, it always places a **source block** (`FluidLevel = 0`) below. This means a single source block at height Y creates a full column of source blocks all the way down to the first solid block, making every block in the waterfall a new infinite source. This diverges from Minecraft's behavior where falling water creates "flowing" blocks that disappear when the source is removed.

This is noted in `FLUID_BUGS.md` as a related issue, but the root cause is specifically the `FluidLevel = 0` default on newly created fluid mods during downward flow.

