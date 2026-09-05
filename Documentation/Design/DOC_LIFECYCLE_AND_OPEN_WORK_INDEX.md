# Documentation Lifecycle & Open-Work Index (DG-*)

**Version:** 1.0  
**Date:** 2026-09-05  
**Status:** **Proposed design — not implemented.** DG-0…DG-4 are all open. Nothing in this document
has been executed; `Design/` still holds six promoted-or-implemented docs and both skills still
state the rule DG-2 replaces.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production) — documentation-only, no code

> What happens to a Design doc after its Architecture doc exists. Today two written rules disagree:
> `Documentation/README.md` says a fully-implemented proposal **leaves** `Design/`, while
> `docs-sync`'s promotion protocol says the Design doc is **not deleted** and stays there frozen.
> Six docs currently sit in the state README forbids. **The decision this document settles: README
> is right, and goes further — a promoted design doc is *deleted*, not archived, because its
> load-bearing content has by then been merged into the Architecture doc and git history keeps the
> rest.** The cost that buys is retrieval: deferred and paused items currently discoverable by
> browsing `Design/` would vanish into Architecture limitation sections. **DG-3 is the
> counterweight** — one global, feature-independent index with a master table pointing at every
> open item and its owning doc, on the `PERFORMANCE_IMPROVEMENTS_REPORT.md` model.

**Audited:** 2026-09-05, at commit `cb1508ed` (branch `feat/fluid-physics`).
Findings are from reading `Documentation/README.md` (the Directory guide table and the Conventions
list), `.claude/skills/docs-sync/SKILL.md` (Step 2b/2c) and
`.claude/skills/docs-sync/references/promotion-protocol.md` (Step 4), the status header of every
file in `Documentation/Design/`, `Documentation/Design/PERFORMANCE_IMPROVEMENTS_REPORT.md`'s Legend
and Master summary table as the structural precedent, and
`Documentation/Archived/WORM_CARVER_FAR_COORDINATE_PRECISION.md` as the archival precedent. Inbound
link counts were measured by grep across `Documentation/`, `CLAUDE.md`, `AGENTS.md` and `.agents/`.
`.agents/skills/` and `.claude/skills/` were confirmed to be the **same files** (identical inode),
so a skill edit lands in both twins at once.

**Relationship to other documents:**

- [`../README.md`](../README.md) — the Conventions list DG-1 amends. Currently the only place the
  "implemented docs leave `Design/`" rule is written, and it has **zero** inbound `@`-references,
  which is why it lost to the skill in practice.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — the structural model
  for DG-3: Legend + Master summary table + per-area tables keyed `ID / Finding / Effort / Risk /
  Benefit / Seed / Save`. Also the precedent that a backlog-status doc is exempt from the promotion
  trigger, so a global index never becomes due for promotion itself.
- [`../Architecture/UNDERWATER_AND_SUBMERSION_RENDERING.md`](../Architecture/UNDERWATER_AND_SUBMERSION_RENDERING.md)
  and [`UNDERWATER_AND_SUBMERSION_RENDERING.md`](UNDERWATER_AND_SUBMERSION_RENDERING.md) — the
  promotion that surfaced the conflict, and DG-4's first migration target.
- [`ANIMATED_LIQUID_SURFACE.md`](ANIMATED_LIQUID_SURFACE.md) — `UW-5`. The worked example of the
  retrieval problem DG-3 solves: a paused item split out of a promoted design, discoverable today
  only because two docs happen to link it.
- [`TOAST_NOTIFICATION_SYSTEM.md`](TOAST_NOTIFICATION_SYSTEM.md) — the other ⛔ Superseded design,
  and the precedent that established promote-in-place. DG-4 migrates it too.

---

## 1. Goals & non-goals

### Goals

1. **One written rule for what happens to a promoted Design doc**, stated in both the README and the
   skill that enforces it, so the two cannot drift apart again.
2. **A `Design/` folder whose contents match its description** — proposals and open backlogs, and
   nothing that describes shipped code.
3. **No loss of retrievability for open work.** Deleting promoted designs must not make paused or
   deferred items harder to find than they are today. This is the goal that makes DG-3 mandatory
   rather than optional.
4. **No broken inbound links**, including from `Documentation/Bugs/`, which `docs-sync` is otherwise
   forbidden to edit.

### Non-goals

- **Changing what a promotion itself does.** The merge protocol — verify against code, fresh
  `Audited:` line, carry the ID index, distil rejected alternatives — is working and is not
  reopened here. DG-2 changes only the *disposal* step.
- **Rewriting `Archived/`.** Existing archived docs stay where they are. DG-2 sets the rule going
  forward and DG-4 applies it to the six current stragglers; it does not re-litigate anything
  already archived.
- **A per-item status tracker.** DG-3 is an index — a pointer table — not a project-management
  surface. Item status stays in the owning doc.

---

## 2. Current state

| Area | State |
|---|---|
| `README.md` Directory guide | `Design/` = "Proposals, specs, and open backlogs for features **not yet implemented**", with the rule "Nothing here is authoritative for current code." |
| `README.md` Conventions | Three lines govern: implemented docs "belong in `Architecture/`, not `Design/`. When a proposal is fully implemented, **move it**"; partially-implemented docs stay; "obsolete or fully superseded move to `Archived/` with a header note". |
| `promotion-protocol.md` Step 4 | The opposite: superseded *phase detail* moves to `Archived/`, but "The Design doc is **not deleted**. It keeps its phases and their dated statuses as the record of intent, gains a status line pointing at the Architecture doc that superseded it, and stops receiving edits." |
| Which rule wins today | The skill's. `README.md` has **zero** inbound `@`-references from `CLAUDE.md`, `AGENTS.md`, `.agents/` or `Documentation/`, so no agent is routed to it; the skill is loaded on every docs-sync invocation. |
| `Design/` contents | **Six** docs describe shipped or superseded work: `UNDERWATER_AND_SUBMERSION_RENDERING` ⛔, `TOAST_NOTIFICATION_SYSTEM` ⛔, `FLIGHT_PROFILE_CAPTURE` ✅ Implemented, `SUN_APPEARANCE_IMPROVEMENTS` Implemented, `CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT` (core question closed), `WORLD_SCALING_ANALYSIS` (superseded for execution). |
| Archival precedent | `Archived/WORM_CARVER_FAR_COORDINATE_PRECISION.md` — a shipped design whose architectural content was merged into `Architecture/World Generation/CAVE_GENERATION.md`, moved and tagged `[ARCHIVED]` with a dated reason block. Predates the promotion protocol. |
| Promote-in-place precedent | `Design/TOAST_NOTIFICATION_SYSTEM.md`, 2026-09-02 — ⛔ Superseded status line, kept in `Design/`. Postdates the protocol and follows it. |
| Index precedent | `PERFORMANCE_IMPROVEMENTS_REPORT.md`: `## Legend` (Effort/Risk/Benefit/Seed/Save symbol table), `## Master summary table`, then per-area tables sharing the `ID / Finding / Effort / Risk / Benefit / Seed / Save` columns. Status `Open backlog.`, which `docs-sync` Step 2b **exempts** from the promotion trigger. |
| Memory drift | The auto-memory `reference-documentation-layout` states `Design/` = "**not-yet-implemented** proposals and backlogs", matching README and equally contradicted by the six docs above. |

---

## 3. Decisions

### 3.1 What happens to a Design doc at promotion

#### Option A — Delete it ✅ **CHOSEN**

By the time promotion completes, every load-bearing part of the design has been *merged*, not
copied: the current-state description into the Architecture body, the standing "do not
re-litigate" list into its Rejected alternatives (including options refuted by measurement), the
consequences into its Limitations, and the whole ID space into its ID index. Work that was planned
but not done is split into its own live Design doc, because an Architecture doc cannot hold it.

What deletion actually costs is the **dated `Document History` narrative** — the arc of how the
design changed under play. That is real, and it is also **still in git**: `git show
<sha>:Documentation/Design/<NAME>.md` retrieves the file forever. Archived-versus-deleted therefore
differs only in *browsability*, and browsability of a document that no longer describes the code is
the hazard, not the benefit — it is a stale file that an agent or a person can find and read.

- ✅ `Design/` means one thing again, so its own description stops being a lie.
- ✅ Removes the stale-read surface entirely rather than labelling it.
- ✅ The record of intent survives in history, retrievable by a documented command (DG-1 writes it
  into the README so nobody has to know the trick).
- ❌ Requires editing `Documentation/Bugs/_FIXED_BUGS.md` to repoint an inbound link, crossing a
  skill boundary `docs-sync` normally forbids. DG-2 carves that exception explicitly rather than
  leaving it to judgment.
- ❌ Retrieval of *open* items regresses unless DG-3 lands. Treated as a hard dependency, not a
  risk: DG-3 is a prerequisite of DG-4, not a follow-up.

#### Option B — Move to `Archived/` with a header note (rejected)

The `WORM_CARVER` precedent, and what README's third Convention line says today.

- ✅ Keeps the dated history browsable without git.
- ❌ Keeps a stale file in a folder people do read. `Archived/` is described as "read-only
  reference… a record of past decisions", which is exactly the framing that invites someone to
  treat a superseded design as decision context.
- ❌ Still needs every inbound link repointed, so it saves none of the link work deletion costs.
- ❌ Grows a folder monotonically with documents whose every useful sentence exists in two places.

#### Option C — Keep it in `Design/`, frozen (rejected — the status quo)

- ✅ Zero work; matches the protocol as written and the Toast precedent.
- ❌ `Design/` accumulates docs that describe shipped code, so its stated contract ("Nothing here is
  authoritative for current code") becomes something a reader has to *know* rather than something
  the folder enforces. Six docs deep, that is already where it is.

### 3.2 Where open items become findable

**One global, feature-independent index ✅ CHOSEN** — `Documentation/Design/OPEN_WORK_INDEX.md`
(name to confirm at DG-3), status `Open backlog.`, on the `PERFORMANCE_IMPROVEMENTS_REPORT.md`
model: a Legend, a master summary table, and per-area tables beneath it. Each row is an ID, a
one-line description, and **a pointer to the doc that owns it** — a live Design doc, or an
Architecture doc's limitation section where the deferral is recorded as a consequence.

The index is a **pointer table, not a second source of truth.** It carries no rationale and no
status narrative; those stay in the owning doc, which is the thing that gets updated when the work
moves. A row that duplicated its owner's reasoning would be the same drift this document exists to
close, one level up.

Rejected: **per-area indices** (one per subsystem). They would be cheaper to write and would rot
independently — the whole retrieval problem is that a reader does not know which area to look in.

Rejected: **relying on the Architecture docs' limitation sections alone.** They already record
deferrals well (the `UW-*` promotion put `VX-3`/`VX-5`, the extent-scan edge case and the paused
waterline in §7), but they are reachable only by knowing which system to read. That is precisely
the regression Option A in §3.1 introduces.

### 3.3 Which document states the rule

**Both, with the README as the human-facing statement and the skill as the enforced one.** The
conflict happened because the rule lived in a file nothing references while the *opposite* rule
lived in a file loaded on every invocation. DG-1 and DG-2 are therefore a matched pair and must
land in the same commit; landing either alone recreates the split in the other direction.

---

## 4. Phased implementation plan

| Phase | Scope | Effort | Depends on | Status |
|---|---|:--:|---|---|
| **DG-0 — Inventory** | Enumerate every `Design/` doc whose status is implemented, superseded or closed, and every open item they own. Record which have an Architecture doc already and which do not — a doc with no promotion target cannot be deleted, only left. | 🟢 | — | — |
| **DG-1 — README** | Rewrite the three Conventions lines and the `Design/` row of the Directory guide to state the deletion rule, name the `git show <sha>:<path>` retrieval, and point at the DG-3 index. Correct the `reference-documentation-layout` memory in the same pass. | 🟢 | DG-0 | — |
| **DG-2 — Protocol** | Rewrite `promotion-protocol.md` Step 4 and `docs-sync` SKILL.md Step 2b/2c to match: delete rather than retain, carve the explicit `Documentation/Bugs/` exception for repointing inbound links, and require the DG-3 index row before deletion. `.agents/` and `.claude/` are the same inode — one edit, both twins. | 🟢 | DG-1 | — |
| **DG-3 — The index** | Author `OPEN_WORK_INDEX.md` per §3.2, seeded from DG-0's inventory. **Prerequisite for DG-4**, not a follow-up: deleting a design before its open items have a home is the regression §3.1 accepts only because this exists. | 🟡 | DG-0 | — |
| **DG-4 — Migrate the six** | Apply the rule to the six docs in §2, one commit each: verify the Architecture doc covers the design's claims, repoint inbound links (incl. `_FIXED_BUGS.md`), add index rows, delete. `UNDERWATER_AND_SUBMERSION_RENDERING` first — it is the freshest merge and its promotion is verified. A doc with no Architecture counterpart (`WORLD_SCALING_ANALYSIS`) is assessed individually and may stay. | 🟡 | DG-2, DG-3 | — |

*Status: `—` not started · `In progress` · `✅ YYYY-MM-DD` complete · `⏸️ YYYY-MM-DD` deliberately
not implemented · `⛔ Superseded YYYY-MM-DD — <by what>`.*

**Validation.** There is no suite for documentation. The gates are the two repo checkers —
`python Tools/Python/check_doc_refs.py` (whose found-count must stay plausible, not merely
unresolved-zero) and `python Tools/Python/check_markdown_breaks.py` — plus, per deletion, a grep
sweep for the deleted filename across `CLAUDE.md`, `AGENTS.md`, `Documentation/` and `.agents/`
returning zero hits. DG-3's index gets no automated check; its failure mode is omission, which
only DG-0's inventory can bound.

---

## 5. Constraint compliance

| Project constraint | How this design complies |
|---|---|
| Voxels / Burst / hot-path GC / pooling / serialization | Not applicable — documentation only, no code changes in any phase. |
| No on-disk change | **Zero.** Nothing here reaches the save format or any asset. |
| `CLAUDE.md`/`AGENTS.md` twin sync | DG-1 touches neither; if a later phase does, both are edited and staged together. |
| Commit style | One phase per commit, `Docs:` or `Skill:` prefixed; DG-4 is one commit per migrated doc so each deletion is independently revertable. |

---

## 6. Rejected alternatives

| Alternative | Why rejected | Date |
|---|---|---|
| Keep promoted designs in `Design/`, frozen (status quo) | `Design/`'s stated contract becomes something a reader must know rather than something the folder enforces; six docs deep, that is already the case. §3.1 | 2026-09-05 |
| Move promoted designs to `Archived/` | Keeps a stale file in a folder framed as decision context, saves none of the inbound-link work, and grows monotonically with documents whose every useful sentence exists twice. §3.1 | 2026-09-05 |
| Delete without a global index | Cheapest, and it regresses exactly what `Design/` was accidentally providing: a browsable list of work not done. §3.2 | 2026-09-05 |
| Per-area open-work indices instead of one global | Cheaper to write and they rot independently, but the retrieval problem is that a reader does not know which area to look in. §3.2 | 2026-09-05 |
| Rely on Architecture docs' limitation sections for open items | They record deferrals well but are reachable only by already knowing which system to read. §3.2 | 2026-09-05 |
| Fix `README.md` alone and leave the protocol as-is | Recreates the split in the other direction: README is referenced by nothing, the skill is loaded every invocation, so the skill would keep winning. §3.3 | 2026-09-05 |
| Delete and leave `_FIXED_BUGS.md`'s inbound link broken | Respects `docs-sync`'s no-editing-`Bugs/` constraint, but a knowingly broken link in the bug archive is worse than the boundary crossing. DG-2 carves the exception instead. §3.1 | 2026-09-05 |

---

## Document History

* **v1.0** - Initial design. Filed out of the `UW-*` promotion (2026-09-05), which hit the
  README-versus-protocol conflict and could not resolve it in scope: the fix is repo-wide, needs a
  new index document, and rewrites two skill files.

---

**Last Updated:** 2026-09-05  
**Next Review:** when DG-0 starts
