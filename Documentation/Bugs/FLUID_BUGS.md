# Known fluid related bugs

This document outlines known bugs related to fluid behavior and simulation.

## 1. Cross chunk fluid simulation

Fluid simulation (or block behavior in general) is currently not fully cross chunk-aware, meaning that fluids can only flow one block into a neighbouring chunk B, it then stops behaving or updating. This can lead to unexpected behavior and visual artifacts where the fluid just suddenly stops.

Looking at the debug screen, the new "*should be active*" fluid voxel is not added to the active voxels list of the neighbouring chunk B.

NOTE: I am standing in chunk B and breaking the solid voxel in chunk B so that the fluid from chunk A flows into chunk B. So both chunks are fully loaded.

## 2. Side face rendering

Side faces between fluid voxels of different fluid levels are currently always rendered, this can lead to visual artefacts where these internal faces are incorrectly visible to the player and is bad for performance.
NOTE: This is currently done to allow "waterfall" like faces to render properly, but a better solution should be found.

## 3. No player effect

Fluid voxels do not currently affect the player, meaning that the player can walk through fluid voxels without any interaction.  
It should slow the player down when walking into fluid voxels. And affect the buoyancy of the player when swimming.  
A visual on-screen effect should also be applied to indicate that the player is submerged in a fluid.


## 4. Downward flow creates infinite source blocks

**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (lines 300–321)

> [!WARNING]
> **SAVE COMPATIBILITY:** Existing saved waterfalls are composed of source blocks (`FluidLevel = 0`) and would remain unchanged. However, **new** fluid flows after the fix would create non-source "flowing" blocks instead, leading to inconsistent waterfall behavior between old and new terrain in the same world.

When a fluid flows downward into air, it places a **source block** (`FluidLevel = 0` by default) at the position below. This means every block in a waterfall column becomes a full infinite source block, so removing the original source at the top does not stop the waterfall — each block below sustains itself. In Minecraft, downward-flowing water creates "flowing" blocks that dry up when the source is removed.

**Root cause:** The `VoxelMod` created for downward flow does not explicitly set a `FluidLevel`, so it defaults to `0` (source). It should be set to a non-source flowing level (or a dedicated "falling fluid" state).



## 5. Fluid can flow into blocks of a different fluid type

**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (lines 334–346)

The outer guard condition for horizontal flow allows interaction if the neighbor is non-solid OR is any fluid type. The inner check (line 339) validates that the neighbor is the same fluid type before spreading, but only if the neighbor *is* a fluid. This means water can flow into a position adjacent to lava without any interaction logic (like creating cobblestone/obsidian), because the flow simply doesn't happen — the horizontal spread is silently skipped rather than triggering a reaction.
