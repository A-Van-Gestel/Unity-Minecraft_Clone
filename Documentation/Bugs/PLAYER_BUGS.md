# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Collision Issues in Tight Spaces

**Severity:** Bug
**Files:** Player Controller

Player collision can get stuck / flaky in tight spaces (eg: single block wide tunnels or when flying trough caves).

---

## 02. Movement Speed Reset on Fly Mode Toggle

**Severity:** Bug
**Files:** Player Controller

When increasing the player movement speed, the horizontal speed is still increased when the player is falling after turning fly mode off. The movement should be "reset" back to the standard player movement speed. The actual movement speed override itself should be saved in the game-state however for when the player turns fly mode back on.

---

## 03. World-gen replacement tags leak into player placement

**Severity:** Bug
**Files:** `PlayerInteraction.cs`, `PlacementResolver.cs`, `World.CheckForVoxel`, `BlockDatabase.asset`
**Validation:** `Minecraft Clone/Dev/Validate Placement` (`PlacementValidationSuite`) — known-bug repros `PA-DATA-1`, `PA-KB-CoalOre`, `PA-KB-DirectionalBlock`, `PA-KB-OakLog`.

Some blocks cannot be placed **on top of** other blocks. The held block's `BlockType.canReplaceTags` field drives two unrelated things in the player path:

1. **Raycast skip** — `PlayerInteraction.PlaceCursorBlocks` derives `skipTags` from the held block's `canReplaceTags` (via `PlacementResolver.GetRaycastSkipTags`), and `World.CheckForVoxel` makes the ray pass *through* any block whose `tags` overlap. If `canReplaceTags` contains a **structural** tag (e.g. `ROCK`, `LEAVES`), the ray tunnels through that surface and the player cannot aim at it.
2. **Replace vs. place-on-top** — `PlacementResolver.ResolvesToReplace` → `BlockTagUtility.CanReplace` then *replaces* the targeted block instead of landing adjacent.

The values in `BlockDatabase.asset` are tuned for **world generation** (the same `canReplaceTags` field gates `World.ApplyModifications`), not player placement:

- **Coal Ore** `canReplaceTags = ROCK` — for ore-in-stone generation; in the player's hand it tunnels through / replaces stone.
- **Directional Block** `canReplaceTags = ~UNBREAKABLE` (almost everything) — tunnels through nearly every solid.
- **Oak Log** `canReplaceTags = … | LEAVES` — for tree generation; tunnels through leaves.

The sane player-placement mask is the soft/transient set only: `REPLACEABLE | PLANT | LIQUID`. Fix direction (separate change): retune the offending blocks' tags, or split the player-placement skip/replace mask from the world-gen `canReplaceTags`. The validation suite encodes the target so the fix is verifiable.

---
