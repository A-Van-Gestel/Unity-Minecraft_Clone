# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Collision only checks at two height levels (feet and +1)

**Severity:** Bug  
**Files:** `Player.cs` — `Front`, `Back`, `Left`, `Right` properties (lines 335–353)

The horizontal collision properties only check at two Y levels: the player's feet (`position.y`) and one block above (`position.y + 1f`). Since `playerHeight` is configurable (default 1.8f), any height > 2.0f leaves the upper portion unchecked, allowing the player to walk through blocks at head/shoulder level. The `+1f` offset is hardcoded rather than derived from `playerHeight`.

---

## 02. Collision checks don't account for player width in the cross-axis

**Severity:** Bug  
**Files:** `Player.cs` — `Front`, `Back`, `Left`, `Right` properties (lines 335–353)

Each directional collision check only samples a single line along its axis (e.g., `Front` checks `z + playerWidth` but at the exact `x` position). Diagonal movement can clip through block corners because no corner positions are checked. Vertical checks (`CheckDownSpeed`, `CheckUpSpeed`) correctly check all 4 corners.

---

## 03. Mouse input uses `Time.timeScale` — **LOWEST PRIORITY / DO NOT FIX**

**Severity:** Quirk (intentional)  
**Files:** `Player.cs` — `Update` (lines 123, 126)

Camera rotation uses `_mouseHorizontal * Time.timeScale` rather than a pure sensitivity scale. While technically incorrect (frame-rate dependent), the current behavior is consistent across editor (100 fps) and production builds (2000 fps). Fixing it would require re-tuning all sensitivity values and could break the feel. Kept as-is.

---

## 04. Raycast-based block placement can be incorrect on exact voxel edges

**Severity:** Bug (latent)  
**Files:** `PlayerInteraction.cs` — `RaycastForVoxel` (lines 107–145)

The block placement algorithm uses modulus-based proximity checks (`pos.x % 1`). The `% 1` operation on negative floats produces negative results, which can cause incorrect face detection near the world's negative boundaries. Additionally, when X, Y, and Z checks are equally close (corner/edge hit), Y is always chosen as the tiebreaker.

---

## 05. Block placement overlap check only covers 2 voxels of player height

**Severity:** Bug  
**Files:** `PlayerInteraction.cs` — `PlaceCursorBlocks` (lines 168–172)

The placement validity check only checks the player's feet (`y`) and head (`y + 1`). If `playerHeight` is configured taller than 2 blocks, a block could be placed inside the player's body above the second voxel.
