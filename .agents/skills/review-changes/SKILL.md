---
name: review-changes
description: Reviews a working diff against this voxel engine's project-specific invariants — architecture constraints, Burst, hot-path GC, serialization, chunk pipeline, coordinate spaces, docs — and ends with a single merge verdict. Safe to run repeatedly mid-work. Use when the user says "review my changes", "review the diff", "pre-merge check", "is this ready to merge", "review before commit", "check my work", or before offering a commit on a non-trivial change.
---

# Review changes

A pass over the project-specific invariants that the compiler and the analyzers
in this repo do not check, built to be run **repeatedly** — after each
meaningful chunk while the work is in progress, and once more before merge.

Its job is to be *runnable*. A review that reports the same 300 hot-path
allocations every time gets ignored after one use, so everything here is scoped
to the diff: several gates flag only what the diff **adds**, and the rest apply
to changed code but never to code the diff left alone.

## Scope — read this before anything else

**What this does not do:**

- It does not replace your tool's own generic code review — in Claude Code, the
  built-in `/code-review`. That covers general correctness; this covers *this
  engine's* invariants. Running both is normal.
- It does not re-verify that the code compiles. The Execution Protocol in
  `AGENTS.md` owns that (`dotnet build`, the DLL-timestamp gate, the stale-DLL
  and phantom-`CS0103` traps), and Rider / `Unity_ValidateScript` own the
  analyzer layer. Reporting a compile error or a ReSharper warning here is noise
  — those tools catch it and say it better.
- It does not review code the diff did not touch. Pre-existing debt is not a
  finding — see the delta rule. This engine has thousands of pre-existing
  `new`/LINQ sites; a review that reports them all is worthless.

**What it does:** the gates in `references/` that the diff actually triggers, a
refute pass over whatever they turned up, then a verdict.

**Where the change lands changes the weighting.** Job code (`Assets/Scripts/Jobs/`)
and the chunk pipeline are the sharp edges — a Burst break, a per-voxel reference
type, or a pool-recycle hazard there fails at scale or in IL2CPP, not on your
machine at edit time. Editor-only tooling and one-shot init code are the soft
edges — a `new List<T>()` in a menu handler is fine. Route effort accordingly:
the same allocation pattern is a High in a meshing loop and a non-finding in
`OnInspectorGUI`.

## Step 1 — establish the diff

Everything is scoped to the change under review, and **getting the scope right is
the single most consequential step** — a review of the wrong scope is worse than
none. On this repo that is not automatic: branches here run long (200+ commits is
normal), so the naive "whole branch vs `main`" diff is often *not* the review
unit. Resolve scope in this order.

**1. The user's stated scope wins — always.** If the user named a range, use it
verbatim; do not second-guess it against the size heuristic below. Translate the
common phrasings:

| User says | Range to review |
|---|---|
| "my working changes" / "before I commit" | `git diff` (+ `git diff --staged`) |
| "the staged changes" | `git diff --staged` |
| "all unpushed commits" / "everything I haven't pushed" | `git diff @{u}...HEAD` |
| "starting from commit `<hash>`" | `git diff <hash>^...HEAD` (includes `<hash>` itself) |
| "the last N commits" | `git diff HEAD~N...HEAD` |
| "this branch" / "the whole PR" | `git diff <base>...HEAD` (see step 3 for `<base>`) |

**2. Always run `git status --short` first**, whatever the scope — it is the only
thing that surfaces untracked files (see below), and those are invisible to every
`git diff` range above.

**3. No stated scope → let size pick the default.** Find the parent branch and
measure the branch against it:

```bash
base=$(git symbolic-ref --quiet --short refs/remotes/origin/HEAD 2>/dev/null | sed 's@^origin/@@'); base=${base:-main}
git rev-list --count "$base"..HEAD     # commits this branch is ahead of its parent (main)
git rev-list --count @{u}..HEAD        # commits not yet pushed (needs an upstream)
```

- If `"$base"..HEAD` is a **normal PR size** (tens of commits) → review the whole
  branch: `git diff "$base"...HEAD`. Pre-merge mode.
- If it is **huge** (hundreds of commits — a long-lived branch) → the
  branch-vs-parent diff is almost certainly not what the user wants reviewed.
  Default to the **unpushed** commits (`git diff @{u}...HEAD`); if there is no
  upstream, or that range is also large, **stop and ask for the base commit or
  range** before reading a thousand-file diff.

Note the **three-dot** form: `git diff <base>...HEAD` shows what HEAD *added*
since it diverged from `<base>`, which is the PR view — not every change that
landed on `<base>` in the meantime. On a long-lived branch that distinction is
the difference between a focused review and an unreadable one.

**`git diff` cannot see an untracked file.** A brand-new `.cs` file that has
never been `git add`ed produces no diff output at all, so a review scoped to
`git diff` alone silently skips it. This is not an edge case — it is the *normal*
state of new work: a new job, a new manager, a new test start life untracked.
(It is also the file the Execution Protocol warns compiles to a **false green**
until Unity imports it — so it is doubly unreviewed.)

So always run `git status --short` and fold the `??` entries into the review. For
a new file **every line is added**, which means the delta rule softens nothing:
read the whole file and run every gate its path triggers. A new file under
`Assets/Scripts/Jobs/` gets the full Burst + hot-path + pool pass.

The scope you resolved also picks the **mode**, because a review run mid-work and
a review run before merge want different output:

| What the user is doing | Scope | Mode | Verdict |
|---|---|---|---|
| Still writing the change | `git diff` (+ `--staged`) | **intermediate** | `CONTINUE` / `FIX FIRST` |
| About to commit | `git diff --staged` | **intermediate** | `CONTINUE` / `FIX FIRST` |
| Reviewing committed work for a PR | the range resolved above — the user's, or `@{u}...HEAD` / `<base>...HEAD` | **pre-merge** | `MERGE` / `HOLD` |

The line is whether the work is still in the working tree (intermediate) or
already committed and aimed at a PR (pre-merge) — not which git command produced
the diff.

**This skill is meant to be run repeatedly.** The intended rhythm is an
intermediate run after each meaningful chunk, so defects surface while the code
is still soft, and a single pre-merge run at the end. That only works if repeated
runs stay quiet — hence the now/owed split in step 3 and the carry-forward rule
in step 5.

State which scope and mode you used — a review of the wrong scope is worse than
none.

Get the file list first, then read the changed regions. Do not review from the
diff hunks alone for anything behavioral: a hunk shows what changed, not what the
surrounding method now does. Use `codegraph_explore` on the changed symbols to
see the call flow and blast radius, and `codegraph impact <sym>` (CLI, via Bash)
before trusting that a struct/interface change is local.

## Step 2 — the delta rule

**Only flag what this diff introduces.**

This is not politeness, it is the difference between a usable gate and a
discarded one. Concrete example: the hot-path GC gate greps for `new`,
`.ToList()`, and LINQ. Run that across `Assets/Scripts/` and it returns
**thousands of pre-existing hits**. A gate that reports them all is instantly
worthless.

So for every allocation-style or boundary-style gate:

1. Check whether the *diff* adds a violating line.
2. If yes → candidate.
3. If the violation was already there and the diff merely moved or reformatted
   the line → not a finding. Mention it at Low only if the diff makes it
   materially worse (e.g. moved a one-shot `new` into a per-frame loop).

Establish a baseline when you need one, rather than trusting a remembered number:

```bash
# does this hot area already allocate, independent of the diff?
grep -rnE '\.(Any|Where|Select|ToList|ToArray|Count)\(' Assets/Scripts/Jobs
```

The delta rule is why the deletion gate (gate 4) exists as its own thing: the one
class of regression the `+` side cannot show you is a guard the `-` side removed.

## Step 3 — load the shards the diff earns, then run the gates

The nineteen gates are split across six reference files. **Read
`references/gates-core.md` always**, plus each shard the changed-file list
triggers. Loading a shard the diff cannot trip is wasted context; skipping one it
does trip is a missed gate, so route from the actual file list, not from a guess
about what the change was "about".

| Shard | Gates | Load when the diff touches |
|---|---|---|
| `references/gates-core.md` | 1, 2, 3, 4 | **always** |
| `references/gates-jobs.md` | 5, 6, 7 | `Assets/Scripts/Jobs/`, an `Update`/`LateUpdate`/`FixedUpdate`, a meshing / chunk-loop body, or a job-dispatch wrapper |
| `references/gates-serialization.md` | 8, 9 | `Assets/Scripts/Serialization/`, `ChunkData.cs`, `ChunkStorageManager.cs`, or a `[SerializeField]` / public field on a `MonoBehaviour` / `ScriptableObject` |
| `references/gates-pipeline.md` | 10, 11, 12 | `World.cs`, `WorldJobManager.cs`, `ChunkPoolManager.cs`, a pooled type (`Chunk.cs`, `Data/ChunkData.cs`, `Data/ChunkSection.cs`, `VisualizerChunkData.cs`), lighting / fluid / meshing / chunk-management code, or a newly added mutable `static` |
| `references/gates-coordinates.md` | 13, 14 | `Assets/Shaders/` (any `.shader` / `.hlsl`), `WorldOrigin.cs`, `ChunkMath.cs`, `ChunkCoord.cs`, noise sampling, or any code that converts a world/voxel position to `float` or moves one between coordinate spaces |
| `references/gates-docs.md` | 15, 16, 17, 18, 19 | `Documentation/`, `.agents/skills/`, `CLAUDE.md`, or `AGENTS.md` — i.e. the diff **edits docs** (gate 3 in `core` covers the opposite case, code changed with no doc edit) |

Most diffs load core plus one or two shards. A diff that touches everything loads
everything — that is correct, not a failure of the router.

Two triggers are **content-based, not path-based**, and neither can be settled
from the file list alone. Scan the diff before deciding to skip either — no hits
means the shard is genuinely not earned; not checking means you do not know.

- **Serialization**, because any `MonoBehaviour` or `ScriptableObject` in the diff
  might carry a `[SerializeField]` or public field:
  `git diff --no-color $RANGE | grep -nE '^[-+].*(\[SerializeField\]|public\s+\w+\s+\w+\s*;)'`
- **Coordinates**, because a world position can be turned into a float, or moved
  between spaces, anywhere — not only under `Assets/Shaders/`:
  `git diff --no-color $RANGE | grep -nE '^\+.*(OriginVoxel|_LiquidNoiseOrigin|worldPos|positionWS|_Time\.y|FloorToInt|%\s*16)'`

**The shards summarize `.agents/rules/*.md`; the rules are the source of truth.**
Those rule files are glob-attached by other harnesses while you *edit* a matching
file — nothing auto-loads them during a review here, so a gate that names one is
telling you to open it. Gates 5, 8 and 10 each carry conventions and carve-outs
the summary deliberately does not repeat, and reviewing from the summary alone is
how a `MarshalAs`-less `bool` or an unreset pooled field ships.

Do not review from memory of the gate titles. The baselines and the exceptions
are the part that matters, and the exceptions are where false positives come from.

| # | Gate | Shard |
|---|---|---|
| 1 | Data-oriented architecture constraint violated (per-voxel reference type, `BinaryFormatter`/JSON/XML for terrain, monolithic-column meshing, bypassing the async BFS lighting queue) | core |
| 2 | Coding-standards regression on new code (magic number, `public` field vs `[SerializeField] private`, missing XML docstring on new public API, wrong const casing) | core |
| 3 | Documented behavior changed with no doc edit in the same commit | core |
| 4 | Deleted guard or invariant not re-established | core |
| 5 | Burst incompatibility in job code | jobs |
| 6 | Hot-path GC allocation | jobs |
| 7 | Pool not used where a pool exists | jobs |
| 8 | On-disk serialization layout changed with no AOT migration | serialization |
| 9 | `[SerializeField]` / prefab-referenced field renamed or deleted without `[FormerlySerializedAs]` | serialization |
| 10 | Chunk-pipeline invariant broken (flag pairing, gate ordering, pool recycle safety) | pipeline |
| 11 | Mutable `static` added without a per-play reset, or a second `[RuntimeInitializeOnLoadMethod]` | pipeline |
| 12 | Change collides with a known bug in `Documentation/Bugs/` | pipeline |
| 13 | Absolute world coordinate (or an unbounded clock) reaches a `sin`/`cos`/noise argument — correct at spawn, degrades with distance | coordinates |
| 14 | Coordinate spaces mixed, or a position named for no space — correct until the origin re-anchors | coordinates |
| 15 | A doc rewrite silently dropped content (index entry, section, table row) | docs |
| 16 | A completed (`✅`/`⛔`) phase was patched to track code drift rather than corrected | docs |
| 17 | A promotion or Architecture rewrite reuses an `Audited:` line it did not re-earn | docs |
| 18 | An issued ID was deleted from an index table instead of marked superseded | docs |
| 19 | `Last Updated:` / `Audited:` restamped for a targeted one-section edit | docs |

### On an intermediate run: what may wait, and what may not

The unit of an intermediate review is **the next commit**, not the merge. So a
gate defers only when finishing it needs something outside this machine or this
moment — a play-mode round-trip, or a batched job nobody runs per commit.
Everything cheap and local fires now, because the working tree has to be coherent
at commit time.

Exactly two gates have an owed half:

| Gate | Owed half — not a finding mid-work | Now half — always a finding |
|---|---|---|
| 6 hot-path GC | the *measured* allocation. Real GC bytes only show up in a play-mode profiling session the user runs (the Profiler tools return nothing without one) | the **pattern** — a `new`/LINQ/lambda-capture the diff adds to a per-frame or per-chunk path. Flag it now; you do not need the profiler to know it allocates |
| 8 AOT migration | authoring the migration step and the in-editor world-load round-trip that proves an old save still loads | the **layout break itself** — a changed field order / width / added field in a serialized terrain struct. That is a Blocker the moment it lands, whether or not the migration is written yet |

Everything else fires on every run. Three that look deferrable and are not:

- **Gate 5 (Burst).** A managed reference or a `string` interpolation in a
  `[BurstCompile]` job does not "compile later" — it silently falls out of Burst
  or fails AOT in the IL2CPP player. It must never reach a commit that way.
- **Gate 9 (serialized-field rename).** A rename without `[FormerlySerializedAs]`
  is silent data loss the instant the scene/prefab re-serializes. There is no
  later — the reference is already gone.
- **Gate 11 (domain-reload static).** A mutable `static` with no per-play reset
  leaks a stale value into the next play session immediately (this project runs
  with *Reload Domain* off). A field initializer is not a fix.

On an intermediate run the owed halves go under `Still owed before merge` in the
report — a checklist, not findings. On a pre-merge run there is no owed half:
everything is a finding.

## Step 4 — refute each candidate before it becomes a finding

The gates produce *candidates*. A candidate is not a finding until it survives a
pass whose explicit goal is to kill it. Do this for every candidate, including
the ones you are sure about — certainty is exactly the state in which a grep hit
gets promoted without anyone opening the file.

For each candidate, re-derive it from the source as if you were arguing the
other side, and land on one of:

- **Confirmed** — you can name the path, state, or config that triggers it and
  the concrete consequence, and you can quote the line. It ships.
- **Uncertain** — the mechanism is real but the trigger depends on runtime state
  you cannot pin down from source. It ships, **labelled as uncertain**, with the
  one thing that would settle it. Do not drop a candidate merely because it is
  frame-timing-, domain-reload-, or save-migration-dependent: a static that only
  leaks on the second play session, an allocation that only bites at render
  distance, a layout change that only breaks a pre-existing save — these are this
  engine's normal failure modes, not speculation.
- **Refuted** — drop it silently. Only three things refute a candidate: the code
  does not say that (quote the line that proves it), the diff already handles it
  elsewhere (cite the guard — a `[FormerlySerializedAs]`, an existing
  `DomainReset`, a pool `Get`/`Release` pair), or it is pre-existing and the diff
  did not make it worse (the delta rule).

Tools serve this step; they do not replace it. `Unity_ValidateScript` on a
changed `.cs` file corroborates a hot-path allocation candidate; Rider
`lint_files` corroborates a UDR domain-reload candidate or a dead/unused member.
Use them to *confirm or refute* — but if the Editor/IDE is not running, the
candidate does not vanish, it goes to the report as uncertain and the tool goes
on the `Not verified` line.

"I could not be bothered to check" is not refutation — that outcome belongs on
the `Not verified` line in the report.

## Step 5 — report

```markdown
Reviewed: <which diff, how many files> — <intermediate|pre-merge>, shards: core, <others>

### Blockers
- **#1** `Assets/Scripts/.../File.cs:NN` — <what is wrong> — <why it fails / what breaks>

### High
- **#2** `Assets/Scripts/.../Other.cs:NN` — …

### Medium
- **#3** …

### Low
- **#4** …

### Still owed before merge      (intermediate runs only, unnumbered)
- profiler pass to confirm the alloc at `Mesher.cs:120`
- AOT migration step for the new `ChunkData` field

Carried: #5 fixed · #6 rejected · #8 deferred      (repeat runs only)

**Verdict: FIX FIRST** — <the one sentence that says what must change>
```

Rules for the report:

- **One verdict, always — from the mode's vocabulary.** Pre-merge: `MERGE` or
  `HOLD`; any Blocker ⇒ `HOLD`. Intermediate: `CONTINUE` or `FIX FIRST`, where
  `FIX FIRST` means *this will get more expensive to fix the longer you build on
  it* — a Burst-incompatible job signature other jobs will call, an architectural
  violation the next files will copy, a serialized-field rename before prefabs
  re-bind, a struct layout the migration will have to chase. A Blocker that is
  self-contained (one magic number, one missing docstring) is still `CONTINUE`
  with the Blocker listed: it is one keystroke whenever you get to it, and
  stopping the user for it is what makes a repeated gate annoying. High findings
  are a judgment call — say which way you went and why.
- **Number every finding, and keep the number stable.** Findings carry a literal
  `#N` so they can be answered by reference. Use the token, **not** a markdown
  ordered list — a `1.` list restarts at each `###` heading and would put two
  `#1`s in one report. Numbering runs across the whole report in printed order.

  **A carried finding keeps its number for the whole session**; new findings take
  the next unused one. That makes later runs non-contiguous (`#1, #4, #7`), and
  `#1` may stop being the most severe — accepted, because severity is still
  readable from the section heading and referential stability is the entire point.
  Without it, "fix #3" means something different after every run. Numbers reset
  when the session does. `Still owed` items stay unnumbered: they are a checklist.
- **Do not re-litigate a finding.** On a repeat run, re-report a finding only if
  the code at that location changed since it was raised. Otherwise it goes in the
  one-line `Carried:` summary with its number and disposition. A finding the user
  rejected stays rejected — do not re-raise it in different words, and do not
  re-raise it through a different gate. Carry-forward is session-scoped: a new
  session starts clean.
- **Answer a numbered reply directly.** The numbers exist so the review can be
  driven by reference, so treat `fix #1, drop #3` as the instruction it plainly is:

  | Reply | What happens | Next run shows |
  |---|---|---|
  | `fix #1` | apply the fix | `#1 fixed` |
  | `drop #3` / `reject #3` | closed, and it stays closed | `#3 rejected` |
  | `defer #2` | stays open, no fix now | `#2 deferred`, re-reported only if that code changes |

  If a reply is ambiguous — a number never issued, one from a previous session
  (numbers reset), or a range like `fix 1-3` spanning severities — restate which
  findings you are acting on before touching anything.
- **Cap the report at ten findings.** If more survive step 4, keep the ten most
  severe and close with one line: `+N further Low findings omitted`. The cap never
  cuts a Blocker — if ten Blockers survive, the diff is the problem, not the cap.
- **Merge by root cause, not by call site.** One fix, one entry, **one number**.
  When the same defect appears at several places, report it once at the clearest
  site and list the rest inline: `[also at: file:NN, file:NN]`. Three jobs missing
  the same `[BurstCompile]`-breaking pattern from one shared helper are one finding.
- **Mark uncertainty in place.** A candidate that came out uncertain in step 4
  keeps its severity but ends with `— unconfirmed: <what would settle it>`
  (usually a profiler session, a play-mode load test, or a Rider/Unity sweep).
- **Omit empty sections.** Do not print "### Low\n- none". A short report is a
  good outcome.
- **Every finding cites a line you actually read.** Not a hunk header, not a
  grep hit you did not open. Because a new `.cs` file can read as a false green in
  `dotnet build`, a finding inside one must be defended from the *source*, not a
  build result.
- **State what breaks, concretely.** "Violates the architecture" is not a finding;
  "this adds a `List<BlockEntity>` per voxel, so a loaded chunk allocates 4096
  objects and defeats the bit-packed `uint` layout" is.
- **Never pad.** Inventing a Medium to look thorough trains the reader to skim.
  If the diff is clean, say so and stop — a one-line `CONTINUE` is the most common
  correct output of an intermediate run.
- **Distinguish "did not verify" from "is fine."** If a gate needs something you
  cannot run — a profiler session, an in-editor world load, a Rider sweep because
  the IDE is closed — list it under a final `Not verified` line rather than
  passing it silently.
- **Name the shards you loaded.** The header line is the review's coverage claim.
  A reader who knows only `core` ran knows the Burst gates did not.

## Routing

| Situation | Go here |
|---|---|
| Gate 3 fired, or you are unsure which doc owns the behavior | `docs-sync` |
| Gate 16, 17 or 18 fired — a frozen phase was patched, evidence was not re-earned, or an ID was dropped | `docs-sync` (owns the freeze rule, promotion protocol, and ID index) |
| Gate 5 or 6 fired — Burst break or hot-path alloc, and you want the pooled/`Unity.Mathematics` rewrite | `burst-optimization` |
| Gate 8 fired — on-disk layout changed | `serialization-migration` |
| Gate 9 fired — a `[SerializeField]`/prefab-referenced rename or a `.meta` concern | `refactor-safely` and `unity-file-ops` |
| Gate 10 fired — a chunk-pipeline invariant | `chunk-lifecycle` |
| Gate 12 fired — the diff touches a system with a validation suite, or you need to prove a pipeline regression | `validation-driven-bugfix` / `run-validation-suite` |
| The diff *fixes* a bug documented in `Documentation/Bugs/` | `archive-fixed-bug` — move it to `_FIXED_BUGS.md` after the user confirms |
| The review shows the *approach* is wrong, not the code | `create-implementation-plan` — that is a re-plan, not a patch; say so instead of listing symptoms |

## Anti-patterns

The rules above say what to do; these are the ways this review goes wrong that
none of them catch.

- **Severity inflation.** If everything is a Blocker, nothing is. Reserve it for
  "this fails at scale or in IL2CPP, or corrupts a save, or violates a Core
  Architecture Constraint the team wrote down deliberately".
- **Reporting the compiler's or the analyzer's job.** `CS####` errors, ReSharper
  style hits, format deviations — the Execution Protocol and Rider/`Unity_ValidateScript`
  own those. This skill's output is the invariants they cannot see.
- **Blaming pre-existing allocations.** The engine is full of `new` and LINQ the
  diff did not add. Flagging them is the fastest way to get this review ignored —
  run the delta rule.
- **A verdict with no consequence.** `HOLD` must name the specific change that
  would flip it to `MERGE`, and `FIX FIRST` must name what gets more expensive if
  you keep building.
- **Skipping the refute pass on the obvious ones.** The candidate you are most
  certain about is the one most likely to reach the report unopened — and the
  second run that repeats the first one's list is the run that gets this skill
  turned off.
