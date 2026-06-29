---
name: archive-fixed-bug
description: Moves a documented bug entry from its category file to _FIXED_BUGS.md after the user confirms the fix works. Use when the user says "that worked", "bug is fixed", "confirmed fixed", or similar after a debugging session — never pre-emptively before user confirmation.
---

# Archive Fixed Bug Protocol

When a bug documented in `@Documentation/Bugs/` has been fixed AND the user has confirmed the fix works in-game, move the entry to the fixed-bugs archive. This keeps the active bug files focused on open issues and preserves a durable record of what has been resolved.

## When to use this skill

- User says "that worked", "bug is fixed", "confirmed", "ship it", or similar after a diagnostic + fix round.
- User explicitly asks to archive a bug entry.
- After merging a bug-fix branch where the corresponding bug entry still lives in an active file.

**Do NOT use this skill pre-emptively.** An unconfirmed fix is not a fixed bug. Wait for user confirmation.

## How to use it

### Step 1 — Locate the entry

The bug is documented somewhere under `@Documentation/Bugs/`. Category files include:

- `BLOCK_BEHAVIOR_BUGS.md`
- `CHUNK_MANAGEMENT_BUGS.md`
- `FLUID_BUGS.md`
- `JOB_SYSTEM_BUGS.md`
- `LIGHTING_BUGS.md`
- `PLAYER_BUGS.md`
- `SERIALIZATION_BUGS.md`
- `UI_BUGS.md`
- `WORLD_GENERATION_BUGS.md`

Read the relevant file and find the entry. If you are uncertain which file contains it, ask the user rather than guessing.

### Step 2 — Move, do not duplicate

1. Copy the entry verbatim (including any sub-bullets, reproduction steps, and linked PRs).
2. Delete the entry from the source category file.
3. Append the copied entry to `@Documentation/Bugs/_FIXED_BUGS.md` under the matching category header. If the header does not yet exist in `_FIXED_BUGS.md`, create it in alphabetical order with the other categories.

### Step 3 — Update the `Fixed:` field

- Update (or add) a `**Fixed:** {Month} {Year}` line on the entry, using the current absolute date. Example: `**Fixed:** April 2026`.
- Preserve any existing `**Reported:**` / `**Status:**` fields — just augment with `Fixed:`.

### Step 4 — Verify

- Confirm the source category file no longer contains the entry (no duplicate).
- Confirm `_FIXED_BUGS.md` now contains the entry under the correct category header with the `Fixed:` date.
- Do NOT commit automatically — leave the staged changes for the user to review and commit.

## Format example

Moving an entry like:

```markdown
### Chunk meshing deadlock on neighbor edge check

**Reported:** March 2026
**Status:** Intermittent — reproduces under load with view distance 16+.

- Symptoms: chunks at render edge never mesh, neighbors all Populated.
- Suspected: NeedsEdgeCheck never clearing when ScheduleLightingUpdate skipped.
```

Becomes in `_FIXED_BUGS.md`:

```markdown
### Chunk meshing deadlock on neighbor edge check

**Reported:** March 2026
**Fixed:** April 2026
**Status:** Resolved

- Symptoms: chunks at render edge never mesh, neighbors all Populated.
- Root cause: NeedsEdgeCheck never clearing when ScheduleLightingUpdate skipped.
```
