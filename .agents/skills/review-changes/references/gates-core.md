# Core gates — always run

These five (1–4 and 24) apply to any diff, whatever it touches, and they are the
only gates that do. The other six shards — `jobs`, `serialization`, `pipeline`,
`coordinates`, `rendering`, `docs` — are loaded per the router in `SKILL.md`.

Each gate carries **what fails**, **how to check**, **severity**, and whether it
is **delta-based** (flag only what the diff adds) or absolute (any occurrence in
changed code is a finding).

Every severity below is a *ceiling*, not a fixed value. Downgrade when the blast
radius is genuinely small — a hot-path pattern in editor-only code, an
architectural smell in a throwaway prototype — and say why.

---

## Gate 1 — Data-oriented architecture constraint violated

**What fails.** The diff breaks one of the five Core Architecture Constraints in
`AGENTS.md`. These are hard rejections, not style preferences:

- a **reference type stored per voxel** — voxels are bit-packed into a single
  `uint`; a class/struct-with-references per voxel defeats the entire data model
  and allocates per loaded chunk
- `BinaryFormatter`, `JSON`, or `XmlSerializer` used for **terrain data** — the
  region system is custom binary with LZ4/GZip
- **monolithic-column meshing** replacing sub-chunk (16³ section) meshing
- **bypassing the async BFS flood-fill** queue for skylight/blocklight
  propagation

**How to check.** These follow from *what the diff adds*, not from a grep of the
whole tree. Ask of each new type and each new serialization/meshing/lighting
path: does it introduce one of the four shapes above? A new `class`/`struct` with
managed fields that is *indexed per voxel* is the reference-type violation; the
same type held once per chunk-section as metadata is not (see
`PER_BLOCK_METADATA_SCHEMAS.md` for the sanctioned sparse-metadata pattern).
`codegraph impact <NewType>` (CLI, via Bash) tells you how per-voxel its storage
really is.

**Delta-based** — a violation the diff introduces or extends. A pre-existing one
the diff merely moves is not a finding (delta rule).

**Severity.** Blocker. This is the one gate where the correct output is often
"reject and propose the data-oriented alternative", not "patch line NN".

---

## Gate 2 — Coding-standards regression on new code

**What fails.** New code departs from `CODING_STYLE_GUIDE.md` in a way the
analyzers do not catch:

- a **magic number** inline where a named constant belongs
- a `public` field where `[SerializeField] private` is the rule
- a **new public method or class with no XML docstring** (`<summary>`, `<param>`,
  `<returns>`)
- **wrong const casing** — `public const` is `PascalCase`, `private const` is
  `SCREAMING_CASE`
- a raw block ID literal instead of a `BlockIDs.*` constant

**How to check.** Read the added lines. This gate is **new code only** — do not
retrofit docstrings onto untouched members the diff happened to sit near, and do
not flag a magic number that was already there. `AGENTS.md` also forbids
*deleting* existing docstrings/comments/`#region` tags unless their code is
deleted — that half is caught by gate 4, not here.

**Delta-based.**

**Severity.** Low, ceiling Medium. A missing docstring on a new public API is
Low; a raw ID literal that will read the wrong block, or a `public` field that
exposes mutable engine state through the Inspector, is Medium.

---

## Gate 3 — Documented behavior changed with no doc edit in the same commit

**What fails.** The diff makes a sentence in `Documentation/Architecture/`,
`Design/`, or `Guides/` false and ships alone. This is the mechanism by which a
doc tree drifts out of trust.

**How to check.** Route to `docs-sync` — it owns the code-area → owning-doc map
and the impact classification. The review-side question is only: does the diff
include the doc edit, or an explicit statement that there is no doc impact?
Per `AGENTS.md`, refactors, behavior-preserving bug fixes, and test-only changes
are exempt — a change that alters *documented behavior* or ships a feature
drafted in a Design doc is not.

**Open bug entries go stale the same way.** An entry in `Documentation/Bugs/`
names the code it describes (`**Files:**` line, method names in its prose); a
diff that changes that code can leave the entry describing a guard, a call site
or a mechanism that is gone. These docs are outside the Doc Sync rule's three
folders, so check them here. List the open entries naming a changed file, and
re-read each:

```bash
git diff --no-color --name-only $RANGE -- '*.cs' '*.hlsl' '*.shader' '*.py' | xargs -n1 basename | \
  sort -u | grep -lFf - Documentation/Bugs/*.md | grep -v '_FIXED_BUGS.md'
```

A sentence the diff made false is the finding: Low when it is a side remark,
Medium when it is the entry's repro, "why it doesn't fire" or proposed fix. A
diff that *fixes* the entry is not this gate — route to `archive-fixed-bug`
after the user confirms. Gate 12 owns the opposite question, whether the diff
revives or collides with the bug itself.

**Absolute.**

**Severity.** Medium normally. High when the false statement is in a Core
Architecture Constraint reference (e.g. `DATA_STRUCTURES.md`,
`SUB_CHUNK_MESHING_ARCHITECTURE.md`) or in a Guide other work is built on
(`COORDINATE_SPACES_GUIDE.md`, `BURST_COMPILER_GUIDE.md`).

---

## Gate 4 — Deleted guard or invariant not re-established

**What fails.** Every other gate reads the `+` side of the diff. This one reads
the `-` side, and it is the only one that does. A refactor, a "simplification", or
a merge resolution quietly drops a line that was holding something up, and nothing
in the new code takes over the job. Nothing fails at compile time — the guard's
absence is only visible on the path it existed to protect.

The recurring shapes in this engine:

- a **per-play `static` reset** removed — the field now leaks a stale value into
  the next play session (gate 11's invariant, deleted rather than missing)
- a serialized field renamed with its old value not restored in the asset
  YAML — the old serialized data is now orphaned (gate 9's invariant)
- a **pool `Release`/recycle** call removed, or an `await`/`Complete()` on a job
  handle dropped, leaving work in flight or a buffer never returned (gate 10)
- a `[BurstCompile]` attribute removed from a job, silently dropping it to managed
  execution (gate 5, arrived at by deletion)
- a bounds / neighbor-ready / `IsCreated` check on a `NativeArray` collapsed into
  the happy path
- an XML docstring, inline comment, or `#region` deleted while its code stays —
  `AGENTS.md` forbids this
- a test deleted or renamed rather than updated — the case it covered is now
  uncovered
- a `lock` removed — say which thread now owns the state it guarded; a lock
  that only ever guarded single-owner data is a plain removal, not a finding

**How to check.** Read the `-` lines directly rather than inferring them from the
new code. **Pass the range you resolved in `SKILL.md` step 1** — a bare `git diff`
sees only unstaged work, so on a pre-merge or staged review it silently reports
nothing and the gate passes for the wrong reason:

```bash
# $RANGE is whatever step 1 pinned: HEAD (working changes), --staged, <hash>...HEAD, <base>...HEAD
# --no-color is REQUIRED, not cosmetic: under a color.ui / color.diff = always setting git emits ANSI
# escapes even when piped — every ^+ / ^- anchor below then matches NOTHING and the gate passes silently.
git diff --no-color -U5 $RANGE | grep -nE '^-.*(FormerlySerializedAs|RuntimeInitializeOnLoadMethod|BurstCompile|\.Release\(|\.Complete\(|await |IsCreated|return;|DomainReset|\block ?\()'
```

A new (untracked) file has no `-` side at all, so this gate is a no-op there —
that is correct, not a gap.

For every hit, ask the two-part question: **what did this line enforce, and where
does the new code enforce it instead?** A plain move (the reset is now folded into
the class's existing `DomainReset`, the `Complete()` is three lines down) is not a
finding — find it and move on. A deletion with no replacement is the finding, and
it should be reported in terms of the invariant, not the line: "the skylight job
handle is no longer completed before the mesh reads it, so the mesh can build on a
half-lit chunk", not "a `.Complete()` was removed".

Deletions inside a hunk that removes an entire feature or a dead code path are not
findings — the invariant went with the thing it protected.

**Delta-based** by construction.

**Severity.** Inherit the gate the deleted line belonged to — a removed static
reset is gate 11's High, a dropped serialized value is gate 9's Blocker.
For a deletion matching no other gate, High when it can reach a user (lost save
data, a stuck pipeline, a corrupted mesh), Medium otherwise.

---

## Gate 24 — A comment that no longer describes the code

**What fails.** `CODING_STYLE_GUIDE.md` §3: comments describe the code as it
stands now, explain *why*, and an inline comment runs three lines at most. No
compiler, analyzer or checker script reads a comment's meaning, so this is the
one part of a diff that reaches the next reader entirely unchecked. It applies
to C#, HLSL and the Python under `Tools/Python/` alike.

Three shapes, in descending cost:

- **It describes the past.** "used to", "previously", "the old path", or the
  story of a bug that is already fixed. One refactor later a reader cannot tell
  which half still applies. History belongs in `Documentation/Bugs/_FIXED_BUGS.md`,
  the owning design doc and `git log`.
- **It describes something that does not exist yet.** The same failure pointed
  forward: a comment asserting a guarantee, a caller, a save-format field or a
  validation scenario that the diff does not contain and nothing else provides.
  It reads as authoritative and cannot be checked against anything.
- **A mechanical rename rewrote prose it did not understand.** A rename leaves
  paragraphs describing a mechanism that is gone. A renamed symbol in the diff is
  the signal to re-read every comment that mentioned it.

**How to check.** Four mechanical signals, none of them a verdict:

```bash
SRC=(-- '*.cs' '*.hlsl' '*.shader' '*.py')
# 1. comment lines the diff ADDS, filtered to history markers
git diff --no-color $RANGE "${SRC[@]}" | grep -E '^\+' | grep -vE '^\+\+\+' | \
  grep -iE '^\+[[:space:]]*(///|//|#).*(used to|previously|no longer|originally|any ?more|the old |before the fix|was a (bug|defect)|this fixes|the first implementation)'

# 2. a diff that claims to be comments-only: prove it. Empty output = the claim
#    holds; any line means it also changes code.
git diff -U0 --no-color $RANGE "${SRC[@]}" | grep -E '^[+-]' | grep -vE '^(\+\+\+|---)' | \
  grep -vE '^[+-][[:space:]]*(///|//|#|"""|$)'

# 3. added inline comments longer than three lines (/// docstrings and # region
#    directives excluded; one hit per over-long run)
git diff --no-color $RANGE "${SRC[@]}" | awk '
  /^\+[[:space:]]*\/\/[^\/]|^\+[[:space:]]*\/\/$|^\+[[:space:]]*# / { n++; if (n == 4) print FNR": "$0; next }
  { n = 0 }'
```

The fourth signal has no one-liner and is worth the minute: **did a comment block
get bigger?** A "trim" that relocates prose onto a member instead of deleting it
grows the thing it claimed to shrink. Compare block lengths against `$RANGE`
rather than trusting the diffstat, which nets growth against deletion elsewhere
in the file.

**Signal 1 is a prompt to read, not a finding.** Its false positives are
ordinary English, and this repo has hundreds of them: "the buffer must no longer
be referenced", "the columns originally queued". Open every hit. A finding
reported from the grep alone is the kind that gets this gate switched off.

**Not a finding:**

- A measured number that justifies a live constant — "p99 queue depth", "−59 MB
  managed heap", "25 % of a burst's fluid voxels". That is *current* information
  however historical it sounds, and it is the reason the constant is not a guess.
- A Unity, Burst, IL2CPP or platform constraint invisible from the call site. When
  a comment reads like a war story, ask whether the constraint is still live
  before calling it history; deleting a live one re-opens the defect it prevents.
- Docstring bulk on an on-disk or wire-format struct, where the docstring *is*
  the contract readers check the layout against.

**Delta-based.** Only comments the diff adds or rewrites. Pre-existing prose is
not a finding, and a whole-file comment audit is its own task, not a review.

**Severity.** Medium for a comment that describes the past or a contract that
does not exist — it sends the next reader re-deriving something the code already
handles. High when it explains a guard, because a reader who believes a stale
reason deletes the guard. Low for bulk alone — an inline comment past three lines
with nothing false in it; say whether the code wants an extracted method instead,
as the style guide asks.
