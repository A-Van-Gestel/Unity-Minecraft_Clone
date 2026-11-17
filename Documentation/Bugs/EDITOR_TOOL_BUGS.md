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

## 02. Block Editor list shows no per-block property badges

**Severity:** Improvement  
**Files:** `Assets/Editor/BlockEditor/BlockEditorWindow.BlockEditor.cs` — the block list column

The block list shows names only. Every authored property — sound material, render shape, tags, collision
bounds, light emission, fluid type — is visible only after selecting a block, one at a time. With 38
blocks and growing, answering "is this channel authored correctly across the palette?" means 38 clicks,
and a block that is wrong or unset looks exactly like one that is right.

This is what let the field-by-field copy bug (`_FIXED_BUGS.md` Editor Tooling #03) go unnoticed: every block reading `None` was indistinguishable from every block
being correct, because nothing in the list surfaced the value.

**Impact:** authoring errors and regressions across the palette are invisible at a glance; auditing is
per-block manual work.

**Proposed fix — deferred to its own session,** because doing it per-channel is worse than doing it once:
a badge/column system for the list (compact per-block indicators for the channels that matter), plus
filtering by those channels in the vein of the existing tag filter. Scope it as a Block Editor list
overhaul covering every channel worth surfacing, not just sound material.

---
