# Known fluid related bugs

This document outlines known bugs related to fluid behavior and simulation.

## 1. Cross chunk fluid simulation

Fluid simulation (or block behavior in general) is currently not fully cross chunk-aware, meaning that fluids can only flow one block into a neighbouring chunk, it then stops behaving or updating. This can lead to unexpected behavior and visual artifacts.

## 2. Corner smoothing (Horizontal behavior)

Fluid meshes are currently affected bo voxels that are horizontal neighbors of the fluid voxel. Even if the horizontal neighbor is not directly touching this fluid voxel (eg: blocked by a solid block). This leads to a visual artifact where the fluid mesh is incorrectly smoothed
at the corner of the fluid voxel (eg: Higher or lower than it should depending on the fluid level of the horizontal neighbor).

Example diagram:

|    | 01              | 02              | 03             |
|----|-----------------|-----------------|----------------|
| 01 | `Solid`         | `Water Source`  | `Water Source` |
| 02 | `Flowing Water` | `Solid`         | `Water Source` |
| 03 | `Flowing Water` | `Solid`         | `Water Source` |
| 04 | `Flowing Water` | `Solid`         | `Water Source` |
| 05 | `Flowing Water` | `Solid`         | `Water Source` |
| 06 | `Flowing Water` | `Flowing Water` | `Water Source` |

The `Flowing Water` in 01 x 02 would be affected by the `Water Source` in 02 x 01.

## 3. Side face rendering
Side faces between fluid voxels of different fluid levels are currently always rendered, this can lead to visual artefacts where these internal faces are incorrectly visible to the player and is bad for performance.
NOTE: This is currently done to allow "waterfall" like faces to render properly, but a better solution should be found.

## 4. No player effect
Fluid voxels do not currently affect the player, meaning that the player can walk through fluid voxels without any interaction.  
It should slow the player down when walking into fluid voxels. And affect the buoyancy of the player when swimming.  
A visual on-screen effect should also be applied to indicate that the player is submerged in a fluid.
