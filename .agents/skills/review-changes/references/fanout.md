# Shard runner contract — fanning the gates out to subagents

`SKILL.md` step 3a hands each earned shard to its own subagent. This file is the
brief that subagent works from, whoever runs it: the `review-shard-runner` agent
or a generic subagent. Inline runs do not need it. Steps 1, 2, 4 and 5 never
leave the main agent.

## What the main agent passes

Spawn one runner per earned shard (`core` included), all in one message so they
run in parallel. Each brief contains, literally:

- **`RANGE=`** the pinned value from step 1 — `HEAD`, `--staged`, or a range with
  the hashes already resolved (`a1b2c3d4...HEAD`), never a `$(…)` or `@{u}`
  expression.
- **Mode** — intermediate or pre-merge.
- **Repo root** — an absolute path; every git command runs as `git -C <root>`.
- **Changed files** — from `git diff --no-color --name-only $RANGE` *plus* the
  `??` entries of `git status --short`, marked untracked.
- **Shard** — the one `references/gates-*.md` file to run, by path.
- **This file**, by path.
- **Settled decisions** — `.agents/skills/review-changes/references/settled-decisions.md`,
  by path. Deliberate designs listed there are never candidates.

Carried findings are not passed: the runner is stateless, and filtering what an
earlier run already settled is the main agent's job in step 5.

## What the runner does

1. Read the shard in full, this file, and `settled-decisions.md`. Run **every**
   gate in the shard — not the ones that look relevant.
2. Substitute `RANGE` into every command and keep `--no-color`. A bare `git diff`
   on a staged or pre-merge review reads the wrong tree and finds nothing.
3. For an untracked file every line is added: read the whole file.
4. Read the changed regions, not only the hunks, before calling anything
   behavioral. On a committed range, read them at the range's end commit
   (`git -C <root> show <end>:<path>`): the working tree may hold later commits
   and unrelated edits.
5. When a gate points at a source — a `.agents/rules/*.md` file, a sibling skill
   (`docs-sync`, `chunk-lifecycle`, `serialization-migration`), a guide under
   `Documentation/Guides/`, the bug docs under `Documentation/Bugs/` — read it
   before ruling on the gate. The shard is a summary; the source owns the
   carve-outs.
6. A completeness claim ("every caller", "no other site") needs `grep -rn` over
   `Assets/Scripts/` (plus `Assets/Editor/` when callers can live there), with the
   command in the ledger line. `codegraph_explore` orients; its capped result is
   not proof of absence.
7. When a gate needs something the runner cannot do — a profiler session (gate 6's
   owed half), an in-editor world-load round-trip (gate 8), a Rider or Unity
   corroboration, a judgment of intent across the whole change — mark it
   `needs main` instead of guessing.
8. Read-only. Never edit, stage, compile, call `unity`, or run anything that
   writes. Never run `git status`: the brief carries the file list, and parallel
   runners would race on the index refresh.

The runner does **not** refute candidates — step 4 is the main agent's. Two
exceptions go under `Discarded`, with the evidence, so the main agent can overrule
them: a hit that is plainly pre-existing (the delta rule), and a hit on a row of
`settled-decisions.md`.

## What the runner returns — this shape, nothing else

```markdown
Shard: gates-<name>.md · RANGE=<value> · <mode>

### Ledger
- Gate N — ran — `<command>` → <hit count>
- Gate N — not triggered — <why, in one line>
- Gate N — needs main — <what it needs>

### Candidates
- Gate N · `path:line` · "<the line, quoted>" · <mechanism and trigger> · <proposed severity> · now|owed

### Discarded
- Gate N · `path:line` · pre-existing: <evidence, e.g. the line exists at the base>
- Gate N · `path:line` · settled: <the settled-decisions.md row>

### Noticed
- `path:line` · <one line: a likely hit for a gate outside this shard>
```

- **One ledger line per gate in the shard.** The main agent counts them: a gate
  missing from the ledger did not run, whatever the rest of the report says.
- Every candidate quotes a line the runner opened. No source blocks, no prose
  summary, no verdict.
- A hit for a gate outside this shard goes under `Noticed`, never filed under one
  of this shard's gate numbers.
- Empty `Candidates`, `Discarded` and `Noticed` sections print as `none`, so an
  empty list is distinguishable from a truncated report.

## What the main agent does with it

- Check every ledger against the shard's gate list in `SKILL.md`; re-run a
  missing gate inline.
- Take `needs main` gates inline.
- Run step 4 on every candidate — open the cited line yourself — and skim
  `Discarded` for a delta or settled call you disagree with. Route `Noticed`
  lines to the shard that owns the gate, or check them inline if that shard did
  not run.
- Merge by root cause across shards: two runners can report one defect.
