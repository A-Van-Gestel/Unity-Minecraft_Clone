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
4. **Renumber it.** Archive numbers are unique per section and independent of the source file's numbering — the source file retires a number when its entry leaves, so carrying the number across collides with whatever was archived under it earlier. Take the next free number in the section and put the source id in the heading: `### ~~26. Title~~ (LIGHTING_BUGS.md Bug 20)`. See the numbering note at the top of `_FIXED_BUGS.md`.

### Step 2b — Retarget citations of the old id (**the step most often missed**)

Open bugs get cited by id from code comments, other docs, and validation suites — `(BLOCK_BEHAVIOR #05)`, `LIGHTING_BUGS.md Bug 21`. Once the entry moves, every one of those points at a file that no longer contains it.

1. Grep for the id **and** the source file name across `Assets/` and `Documentation/`:
   `grep -rn "LIGHTING_BUGS.md" Assets/ --include=*.cs` and the same for the bare id string.
2. Retarget each hit to `_FIXED_BUGS.md` plus the **new** section number, and shift tense — a comment saying a live bug "is" something should now say what the code does and cite the archive for why.
3. Leave **frozen historical records alone**: a Design doc's dated findings table or revision history is era-accurate by design, not a current-state claim. Only current-state text and code comments get retargeted.
4. Keeping the source id in the heading (Step 2.4) is what lets citations you miss still resolve by search — it is a safety net, not a substitute for this step.

> Real cost of skipping it: archiving one lighting bug stranded seven pointers across `JobData.cs`, `ChunkData.cs`, `LightAttenuation.cs`, two validation suites and a test palette; a second bug's pointers had been stranded by an earlier session and went unnoticed until an unrelated audit.
4. **Renumber it.** Archive numbers are unique per section and independent of the source file's numbering — the source file retires a number when its entry leaves, so carrying the number across collides with whatever was archived under it earlier. Take the next free number in the section and put the source id in the heading: `### ~~26. Title~~ (LIGHTING_BUGS.md Bug 20)`. See the numbering note at the top of `_FIXED_BUGS.md`.

### Step 2b — Retarget citations of the old id (**the step most often missed**)

Open bugs get cited by id from code comments, other docs, and validation suites — `(BLOCK_BEHAVIOR #05)`, `LIGHTING_BUGS.md Bug 21`. Once the entry moves, every one of those points at a file that no longer contains it.

1. Grep for the id **and** the source file name across `Assets/` and `Documentation/`:
   `grep -rn "LIGHTING_BUGS.md" Assets/ --include=*.cs` and the same for the bare id string.
2. Retarget each hit to `_FIXED_BUGS.md` plus the **new** section number, and shift tense — a comment saying a live bug "is" something should now say what the code does and cite the archive for why.
3. Leave **frozen historical records alone**: a Design doc's dated findings table or revision history is era-accurate by design, not a current-state claim. Only current-state text and code comments get retargeted.
4. Keeping the source id in the heading (Step 2.4) is what lets citations you miss still resolve by search — it is a safety net, not a substitute for this step.

> Real cost of skipping it: archiving one lighting bug stranded seven pointers across `JobData.cs`, `ChunkData.cs`, `LightAttenuation.cs`, two validation suites and a test palette; a second bug's pointers had been stranded by an earlier session and went unnoticed until an unrelated audit.

### Step 3 — Update the `Fixed:` field

- Update (or add) a `**Fixed:** {Month} {Year}` line on the entry, using the current absolute date. Example: `**Fixed:** April 2026`.
- Preserve any existing `**Reported:**` / `**Status:**` fields — just augment with `Fixed:`.

### Step 4 — Verify

- Confirm the source category file no longer contains the entry (no duplicate).
- Confirm the new number does not collide with an existing one **in that section** of `_FIXED_BUGS.md`.
- Confirm no `Assets/` or `Documentation/` reference still sends a reader to the source file for this entry (Step 2b).
- Confirm the new number does not collide with an existing one **in that section** of `_FIXED_BUGS.md`.
- Confirm no `Assets/` or `Documentation/` reference still sends a reader to the source file for this entry (Step 2b).
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

> **Preserve the hard line breaks.** `**Reported:**` / `**Fixed:**` / `**Status:**` each end in
> **two trailing spaces** (invisible above); the last one in the stack does not. Drop them and
> the renderer joins the whole stack into one run-on line. When you insert `**Fixed:**`, add the
> two spaces to it *and* to the `**Reported:**` line above it.
