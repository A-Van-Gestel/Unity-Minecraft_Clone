# Known Lighting related bugs

This document outlines known bugs related to current lighting implementation.


## 01. Ghost lighting that seems to be possible to occur at the other end of a chunk border.

**example:**
- Block sunlight at the chunk border between chunk A and B -> These parts behave the same as before the fix, but blocking the vertical tunnel to the surface one by one, gradually (mostly) fully darkens the cave
- A bright spot of around light level 5 and darkening remains on a chunk border C and A, probably because Chunk C is not properly updated, or stopped to early during the darkens propagation pass.


## 02. Light leakage on chunk corners

**example:**
- Dig a vertical tunnel in chunk A, right at the chunk corner next to chunk B and chunk C.
- Dig into chunk C (Sky needs to be accessible)
- Block sky access in both chunk C and then chunk A.
- FAILURE: The vertical tunnel is still fully lit, even though **no** Skylight (sunlight) is accessible.
