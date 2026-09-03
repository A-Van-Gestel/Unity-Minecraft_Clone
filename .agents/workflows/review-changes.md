---
name: review-changes
description: Run a project-aware review of the working diff against this voxel engine's invariants, then produce a numbered Blockers→Low report with a single verdict. Thin entry point — the gates, modes, delta rule, refute pass, and report format live in the review-changes skill.
---

# Review Changes Workflow

Entry point for `/review-changes`. The authoritative procedure — scope/mode
selection, the delta rule, the twelve gates, the refute pass, and the numbered
report — lives in the **`review-changes` skill** at
`.agents/skills/review-changes/SKILL.md`. This workflow does not restate the
gates, because a second copy drifts.

## Steps

1. **Load the skill.** Read `.agents/skills/review-changes/SKILL.md` and follow
   it end to end. It routes you to the `references/gates-*.md` shards the diff
   actually earns (core is always loaded; `jobs` / `serialization` / `pipeline`
   load per the changed-file list).

2. **Resolve the scope, then run it in the mode the scope implies.** The skill's
   step 1 is the authority: the user's stated scope wins (e.g. "review all
   unpushed commits from `<hash>`"); otherwise the default is chosen by branch
   size against the parent (`main`), because branches here run long and the naive
   whole-branch diff is often not the review unit.
   - Mid-work (`git diff` / `--staged`) → **intermediate** mode, verdict
     `CONTINUE` / `FIX FIRST`.
   - Committed work aimed at a PR (the resolved range — the user's, or
     `@{u}...HEAD` / `<base>...HEAD`) → **pre-merge** mode, verdict
     `MERGE` / `HOLD`.

   Always run `git status --short` first — an untracked new `.cs` file produces
   no `git diff` and would otherwise be skipped entirely.

3. **Produce the numbered report** exactly as the skill's step 5 defines it:
   findings carry stable `#N` tokens, one root cause per number, a ten-finding
   cap, uncertainty marked in place, empty sections omitted, and one verdict from
   the mode's vocabulary. On repeat runs, carry settled findings forward in the
   one-line `Carried:` summary rather than re-litigating them.

4. **Do not commit.** The review produces findings only. Never stage, commit, or
   push as part of it — that decision stays with the user. (This overlaps the
   skill's scope note; it is repeated here because it is the one thing a workflow
   invocation must not get wrong.)

This layers on top of your tool's generic review (`/code-review`) and the
compile/analyzer layer (the Execution Protocol's `dotnet build` + DLL-timestamp
gate, Rider, `Unity_ValidateScript`) — it does not replace either. Reporting a
`CS####` error or a ReSharper style hit here is noise.
