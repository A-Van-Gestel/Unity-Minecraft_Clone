# Known Editor Tool related bugs

This document outlines **open** bugs and improvement items for the custom editor tooling under
`Assets/Editor/` — the Block Editor, Sound Editor, Credits Editor, World Gen Preview, and the `Dev` menu
utilities. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026
>
> **Scope note:** runtime UI (inventory, HUD, settings menu) belongs in [`UI_BUGS.md`](./UI_BUGS.md).
> This file is for the authoring tools themselves — where the failure costs *authored data* rather than
> a frame.

---

## 01. Block Editor clones `BlockType` field-by-field, so new fields are silently dropped

**Severity:** Implementation (data loss)  
**Files:** `Assets/Editor/BlockEditor/BlockEditorWindow.cs` — `LoadBlockData`;
`Assets/Editor/BlockEditor/BlockEditorWindow.BlockEditor.cs` — `DuplicateSelectedBlock`

The Block Editor does not edit `BlockDatabase.asset` directly. `LoadBlockData` builds a **hand-written,
field-by-field copy** of every `BlockType` into `_blockTypesCopy` (so the window can offer Save/Revert),
and `SaveBlockData` writes that copy back over the asset. `DuplicateSelectedBlock` repeats the same
literal field list a second time.

Any field added to `BlockType` without also being added to **both** lists is therefore:

1. invisible in the window (it reads as the type's default), and
2. **erased from the asset on the next Save** — including the Save offered by Unity's unsaved-changes
   prompt when the window closes.

**Proven by:** the `soundMaterial` channel (2026-08-28). It was added to `BlockType` and given a Block
Editor dropdown, but not to either copy list. Every block showed `None`, and one Save zeroed all 37
authored values in `BlockDatabase.asset`. The values were restored from git and both lists corrected —
but the *pattern* is unchanged, so the next field added repeats it exactly.

**Impact:** silent, destructive loss of authored block data. It does not fail loudly, does not fail at
edit time, and the loss is only visible by diffing the asset. The `Validate Sound Engine` census
(`Every Placeable Block Has An Authored Sound Material`) catches this particular field on the next
`Validate All`, but only *after* the damage has reached disk.

**Proposed fix:** replace both literal lists with a single reflection copy over
`typeof(BlockType).GetFields()`. The semantics are identical to today's code — value types by value,
asset references shallow — but no future field can be omitted, and the replacement is shorter than either
list it removes. A validation baseline asserting round-trip completeness is only worth writing if the
hand-written form is kept.

---

## 02. Block Editor list shows no per-block property badges

**Severity:** Improvement  
**Files:** `Assets/Editor/BlockEditor/BlockEditorWindow.BlockEditor.cs` — the block list column

The block list shows names only. Every authored property — sound material, render shape, tags, collision
bounds, light emission, fluid type — is visible only after selecting a block, one at a time. With 38
blocks and growing, answering "is this channel authored correctly across the palette?" means 38 clicks,
and a block that is wrong or unset looks exactly like one that is right.

This is what let `§01` go unnoticed: every block reading `None` was indistinguishable from every block
being correct, because nothing in the list surfaced the value.

**Impact:** authoring errors and regressions across the palette are invisible at a glance; auditing is
per-block manual work.

**Proposed fix — deferred to its own session,** because doing it per-channel is worse than doing it once:
a badge/column system for the list (compact per-block indicators for the channels that matter), plus
filtering by those channels in the vein of the existing tag filter. Scope it as a Block Editor list
overhaul covering every channel worth surfacing, not just sound material.

---
