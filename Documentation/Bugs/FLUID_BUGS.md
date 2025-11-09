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
