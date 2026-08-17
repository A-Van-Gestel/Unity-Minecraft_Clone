# Core gates — always run

These four apply to any diff, whatever it touches, and they are the only gates
that do. The other three shards — `jobs`, `serialization`, `pipeline` — are
loaded per the router in `SKILL.md`.

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
- **bypassing the async BFS flood-fill** queue for sunlight/blocklight
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
- a `[FormerlySerializedAs]` attribute dropped during a rename — the old
  serialized data is now orphaned (gate 9's invariant, deleted)
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

**How to check.** Read the `-` lines directly rather than inferring them from the
new code. **Pass the range you resolved in `SKILL.md` step 1** — a bare `git diff`
sees only unstaged work, so on a pre-merge or staged review it silently reports
nothing and the gate passes for the wrong reason:

```bash
# $RANGE is whatever step 1 resolved: "" (unstaged), --staged, @{u}...HEAD, <base>...HEAD
# --no-color is REQUIRED, not cosmetic: this repo sets color.ui=always, so git emits ANSI escapes
# even when piped — every ^+ / ^- anchor below then matches NOTHING and the gate passes silently.
git diff --no-color -U5 $RANGE | grep -nE '^-.*(FormerlySerializedAs|RuntimeInitializeOnLoadMethod|BurstCompile|\.Release\(|\.Complete\(|await |IsCreated|return;|DomainReset)'
```

A new (untracked) file has no `-` side at all, so this gate is a no-op there —
that is correct, not a gap.

For every hit, ask the two-part question: **what did this line enforce, and where
does the new code enforce it instead?** A plain move (the reset is now folded into
the class's existing `DomainReset`, the `Complete()` is three lines down) is not a
finding — find it and move on. A deletion with no replacement is the finding, and
it should be reported in terms of the invariant, not the line: "the sunlight job
handle is no longer completed before the mesh reads it, so the mesh can build on a
half-lit chunk", not "a `.Complete()` was removed".

Deletions inside a hunk that removes an entire feature or a dead code path are not
findings — the invariant went with the thing it protected.

**Delta-based** by construction.

**Severity.** Inherit the gate the deleted line belonged to — a removed static
reset is gate 11's High, a removed `[FormerlySerializedAs]` is gate 9's Blocker.
For a deletion matching no other gate, High when it can reach a user (lost save
data, a stuck pipeline, a corrupted mesh), Medium otherwise.
