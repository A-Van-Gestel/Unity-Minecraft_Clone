# Documentation Lifecycle & Open-Work Index (DG-*)

**Version:** 1.1  
**Date:** 2026-09-05  
**Status:** **Partially implemented.** **DG-0…DG-3 shipped 2026-09-05** — the inventory corrected
this document's own scope (§2.1), the README and the promotion protocol now state one rule, and
[`OPEN_WORK_INDEX.md`](OPEN_WORK_INDEX.md) is live. **DG-4 (delete the two) is the only phase
open.**  
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

**Amended:** 2026-09-05 — DG-0's inventory ran and refuted three of this document's own claims: the
straggler count (six → twelve), the `_FIXED_BUGS.md` inbound-link count (one → five, across four
docs), and DG-4's premise that each straggler has an Architecture doc "covering its claims" (true for
two, false for ten). §2.1 is new, §3.1's cost bullet and §4's DG-4 row are rewritten, and §3.4
records the *closed, retained* disposition the inventory forced. The §3.1 decision itself — delete
rather than archive — is unchanged.

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
- [`OPEN_WORK_INDEX.md`](OPEN_WORK_INDEX.md) — the index DG-3 built, and the precondition DG-4's
  deletions check against.
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
| `Design/` contents | **Twelve** docs describe closed or shipped work — see §2.1 for the tiering. Only **two** have ever been through the promotion protocol. |
| Where the IDs live | Only **two** Architecture docs carry an `## ID index`: `TOAST_NOTIFICATION_SYSTEM.md` and `UNDERWATER_AND_SUBMERSION_RENDERING.md`. Every other closed ID space is *cited* across several Architecture docs with no index home. |
| Architecture → Design back-references | **Substantive, not courtesy.** `CHUNK_LIFECYCLE_PIPELINE.md:427` reads "see the CP doc §3.3/§7 CP-3" — the Architecture doc defers detail back. Measured across `Architecture/`: 10 links to `WORLD_SCALING_IMPLEMENTATION.md`, 9 to `VOXEL_OCCLUSION_REFACTOR.md`, 6 to `SUN_APPEARANCE_IMPROVEMENTS.md`, 3 to `MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`, 2 each to `OM1_DEVICE_CALIBRATION.md`, `CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md` and `WORLD_SCALING_ANALYSIS.md`. |
| `_FIXED_BUGS.md` inbound | **Five** links across **four** Design docs: `VOXEL_OCCLUSION_REFACTOR` ×2, `UNDERWATER_AND_SUBMERSION_RENDERING` ×2, `LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP`, `CHUNK_PALETTE_MAPPING`. |
| Link validation | `check_doc_refs.py` reads only `@`-prefixed doc references and `check_markdown_breaks.py` only trailing whitespace — **neither can see a broken relative markdown link**, so a deletion leaves both green. Closed 2026-09-05 by `Tools/Python/check_doc_links.py` (785 links resolving; prove-red confirmed against a deleted-doc fixture). |
| Archival precedent | `Archived/WORM_CARVER_FAR_COORDINATE_PRECISION.md` — a shipped design whose architectural content was merged into `Architecture/World Generation/CAVE_GENERATION.md`, moved and tagged `[ARCHIVED]` with a dated reason block. Predates the promotion protocol. |
| Promote-in-place precedent | `Design/TOAST_NOTIFICATION_SYSTEM.md`, 2026-09-02 — ⛔ Superseded status line, kept in `Design/`. Postdates the protocol and follows it. |
| Index precedent | `PERFORMANCE_IMPROVEMENTS_REPORT.md`: `## Legend` (Effort/Risk/Benefit/Seed/Save symbol table), `## Master summary table`, then per-area tables sharing the `ID / Finding / Effort / Risk / Benefit / Seed / Save` columns. Status `Open backlog.`, which `docs-sync` Step 2b **exempts** from the promotion trigger. |
| Memory drift | The auto-memory `reference-documentation-layout` states `Design/` = "**not-yet-implemented** proposals and backlogs", matching README and equally contradicted by the six docs above. |

### 2.1 The inventory (DG-0, 2026-09-05)

Twelve `Design/` docs describe closed or shipped work. They are **not one population**, and the
difference decides what may happen to each. Status lines were read in full; the promotion column is
"does an Architecture doc carry this design's ID index", not "are the IDs mentioned somewhere".

| Tier | Docs | Disposition |
|---|---|---|
| **1 — Promoted** | `UNDERWATER_AND_SUBMERSION_RENDERING` ⛔, `TOAST_NOTIFICATION_SYSTEM` ⛔ | **Deletable** at DG-4, after an index row and inbound-link repair. Both went through the protocol; both have an Architecture doc with an `## ID index`. |
| **2 — Closed arc, never promoted** | `CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR` (CP-1…CP-7, arc closed 2026-07-23), `MESHING_PIPELINE_ORCHESTRATION_REFACTOR` (MP-1…MP-7, self-described "historical record, not a plan"), `CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT` (P9-*), `FLIGHT_PROFILE_CAPTURE` (FP-0…FP-4), `WORLD_SCALING_IMPLEMENTATION` (WS track "fully closed", OQ-1…7 resolved), `SUN_APPEARANCE_IMPROVEMENTS` (SN-0/1/4 shipped; SN-2+SN-3 reverted, superseded by SN-4) | **Retained** — §3.4. Their content was never merged into an Architecture doc; it is *cited* from several, which defer back for detail. Deleting them removes what the Architecture tree points at. |
| **3 — Open items remain** | `VOXEL_OCCLUSION_REFACTOR` (VO-9 not started), `SILHOUETTE_CONTACT_SHADOWS` (SS-4 not started), `WORLD_SCALING_ANALYSIS` (Tiers A and C still unbuilt) | **Unchanged, and already compliant.** README's second Convention — partially-implemented docs stay — already covers these. They were never in this document's scope; v1.0 mis-tiered them by reading a truncated status line. |
| **4 — No Architecture target** | `OM1_DEVICE_CALIBRATION` (`OM1` returns zero hits under `Architecture/`) | **Retained.** Nothing to promote into; §4's original rule already said a doc without a target cannot be deleted. |

**The headline: promotion is rarer than the tree suggests.** Ten of the twelve were closed by
shipping the work and updating whatever Architecture docs it touched — which is a reasonable thing
to have done, and is *not* a promotion. So the deletion rule §3.1 settles applies to two documents
today, and to future promotions; it is not a licence to clear `Design/`.

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
- ❌ Requires editing `Documentation/Bugs/_FIXED_BUGS.md` to repoint inbound links, crossing a skill
  boundary `docs-sync` normally forbids — **five links across four Design docs** (§2), two of them
  the `UW-*` doc's. DG-2 carves that exception explicitly rather than leaving it to judgment.
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

**One global, feature-independent index ✅ CHOSEN** — [`OPEN_WORK_INDEX.md`](OPEN_WORK_INDEX.md),
status `Open backlog.`, shipped at DG-3 on 2026-09-05.

**Its rows are per-DOCUMENT, not per-item** — amended 2026-09-05 when DG-3 measured what a per-item
index would actually contain. Two findings forced it. Roughly **144 item rows already exist** across
seven master tables in the backlog reports, so mirroring them here would be precisely the second
source of truth the next paragraph forbids, drifting from the originals on their next edit. And the
orphan case that motivated per-item rows is **empty**: all 66 forward-referenced IDs under
`Architecture/` resolve to a document that already owns them. The retrieval problem was never
homeless items — it is that a reader must know *which* document to open, which a per-document map
answers completely.

So each row is an area, its owning document, its ID space and a one-line state. The index also
carries the **closed, retained** docs of §3.4, so a reader who finds a finished design in `Design/`
can tell it is deliberate rather than an oversight.

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

### 3.4 What happens to a closed arc that was never promoted

**A third disposition: "closed, retained" ✅ CHOSEN.** The rule has two dispositions in v1.0 —
delete, or leave alone — and the inventory found six docs that fit neither. Their arcs are finished,
so "partially implemented, stays" does not describe them; their content was never merged into an
Architecture doc, so deleting them destroys what the Architecture tree defers to (§2).

They **stay in `Design/`**, and DG-2 gives them a status-line convention that says why: *the arc is
closed, and the Architecture tree cites this document rather than superseding it.* That is honest
about the state, and it stops a future reader — or a future agent applying §3.1 — from mistaking a
closed arc for a completed promotion and deleting it.

**Deletion is reserved for documents that actually went through the promotion protocol.** The test
is mechanical and worth stating because it is the one a reader will get wrong: *does an Architecture
doc carry this design's `## ID index`?* Two do. Citing the IDs is not the same thing — an ID index
is the promotion's own artifact, and its absence means the merge never happened.

Rejected: **promote all six properly, then delete.** Each is a full promotion — the `UW-*` one took
a session for a document written that same day — so six is a multi-session arc bought for tidiness
in a folder that would still hold them meanwhile. If any is promoted later on its own merits, §3.1
applies to it then.

---

## 4. Phased implementation plan

| Phase | Scope | Effort | Depends on | Status |
|---|---|:--:|---|---|
| **DG-0 — Inventory** ✅ 2026-09-05 | Enumerate every `Design/` doc whose status is implemented, superseded or closed, and every open item they own. Record which have an Architecture doc already and which do not — a doc with no promotion target cannot be deleted, only left. | 🟢 | — | — |
| **DG-1 — README** ✅ 2026-09-05 | Rewrite the three Conventions lines and the `Design/` row of the Directory guide to state the deletion rule, name the `git show <sha>:<path>` retrieval, and point at the DG-3 index. Correct the `reference-documentation-layout` memory in the same pass. | 🟢 | DG-0 | — |
| **DG-2 — Protocol** ✅ 2026-09-05 | Rewrite `promotion-protocol.md` Step 4 and `docs-sync` SKILL.md Step 2b/2c to match: delete rather than retain, state the `## ID index` test that separates a promotion from a closed arc (§3.4), add the *closed, retained* disposition, carve the explicit `Documentation/Bugs/` exception for repointing inbound links, and require the DG-3 index row before deletion. **Lands in the same commit as DG-1** (§3.3). `.agents/` and `.claude/` are the same inode — one edit, both twins. | 🟢 | DG-1 | — |
| **DG-3 — The index** ✅ 2026-09-05 | [`OPEN_WORK_INDEX.md`](OPEN_WORK_INDEX.md), seeded from DG-0's inventory. Shipped **per-document** rather than per-item (§3.2, amended) — 24 area rows plus the six closed-retained docs. **Prerequisite for DG-4**, not a follow-up: deleting a design before its open items have a home is the regression §3.1 accepts only because this exists. | 🟡 | DG-0 | ✅ |
| **DG-4 — Delete the two** | Apply §3.1 to **tier 1** only (§2.1): `UNDERWATER_AND_SUBMERSION_RENDERING` first, then `TOAST_NOTIFICATION_SYSTEM`. One commit each: add the index row, repoint inbound links (incl. `_FIXED_BUGS.md`), **remove the Architecture doc's "the design this was promoted from" relationship bullet** — a deliberate link, so a zero-hits sweep would read it as breakage rather than as intent — then delete. Tiers 2–4 are untouched; they get DG-2's *closed, retained* status line instead. | 🟢 | DG-2, DG-3 | — |

*Status: `—` not started · `In progress` · `✅ YYYY-MM-DD` complete · `⏸️ YYYY-MM-DD` deliberately
not implemented · `⛔ Superseded YYYY-MM-DD — <by what>`.*

**Validation.** There is no suite for documentation. The gates are **three** repo checkers:
`python Tools/Python/check_doc_refs.py` (whose found-count must stay plausible, not merely
unresolved-zero), `python Tools/Python/check_markdown_breaks.py`, and
`python Tools/Python/check_doc_links.py` — added 2026-09-05 because the first two are **blind to a
broken relative markdown link**, which is precisely how a deletion breaks the tree (§2). It resolves
785 links today and was proven red against a deleted-doc fixture. Run all three before and after
each DG-4 deletion; a grep sweep for the filename is the corroborating check, not the gate. DG-3's
index gets no automated check; its failure mode is omission, which only DG-0's inventory can
bound.

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
| A per-**item** index row for every open item | The literal v1.0 spec, refuted by measurement at DG-3: ~144 rows already exist across seven master tables, so this is a second source of truth that drifts on their next edit — and it buys nothing, because no open item is orphaned. §3.2 | 2026-09-05 |
| Rely on Architecture docs' limitation sections for open items | They record deferrals well but are reachable only by already knowing which system to read. §3.2 | 2026-09-05 |
| Promote the six closed-but-unpromoted arcs, then delete them | Each is a full promotion — the `UW-*` one took a session for a doc written that same day — so six is a multi-session arc bought for tidiness, in a folder that holds them meanwhile either way. §3.4 | 2026-09-05 |
| Delete the closed-but-unpromoted arcs anyway, repointing links at `Architecture/` | Fastest route to a clean `Design/`, and it discards content the Architecture docs explicitly defer to — `CHUNK_LIFECYCLE_PIPELINE.md:427` sends the reader to "the CP doc §3.3/§7 CP-3". §3.4 | 2026-09-05 |
| Treat "the Architecture docs cite this design's IDs" as proof of promotion | It is the intuitive test and it is wrong: ten of twelve closed designs are cited without ever having been merged. The `## ID index` — the promotion's own artifact — is the test that separates them. §3.4 | 2026-09-05 |
| Fix `README.md` alone and leave the protocol as-is | Recreates the split in the other direction: README is referenced by nothing, the skill is loaded every invocation, so the skill would keep winning. §3.3 | 2026-09-05 |
| Delete and leave `_FIXED_BUGS.md`'s inbound link broken | Respects `docs-sync`'s no-editing-`Bugs/` constraint, but a knowingly broken link in the bug archive is worse than the boundary crossing. DG-2 carves the exception instead. §3.1 | 2026-09-05 |

---

## Document History

* **v1.1** - **DG-0's inventory ran, and corrected this document.** Three of v1.0's own claims were
  wrong: the straggler count (six → **twelve**, because v1.0 counted them with `grep -l Superseded`,
  which only finds docs containing that word), the `_FIXED_BUGS.md` inbound-link count (one →
  **five**, across four docs), and DG-4's premise. That last one is the substantive correction:
  **only two Architecture docs carry an `## ID index`**, so only two designs were ever actually
  promoted. The other ten were closed by shipping the work and updating whatever Architecture docs
  it touched — which leaves those docs *citing* the design and deferring detail back to it
  (`CHUNK_LIFECYCLE_PIPELINE.md:427`), so deleting them destroys content. New §2.1 tiers all twelve;
  new §3.4 adds the **closed, retained** disposition and the `## ID index` test that separates a
  promotion from a closed arc; DG-4 shrinks from "migrate the six" to "delete the two"; §6 gains
  three rejected alternatives. Three docs v1.0 listed turned out to have **open items**
  (`VO-9`, `SS-4`, world-scaling Tiers A and C) and were never in scope at all. The §3.1 decision —
  delete rather than archive — is unchanged. `Tools/Python/check_doc_links.py` was added in the same
  session to close the false-green the validation block now names.
* **v1.0** - Initial design. Filed out of the `UW-*` promotion (2026-09-05), which hit the
  README-versus-protocol conflict and could not resolve it in scope: the fix is repo-wide, needs a
  new index document, and rewrites two skill files.

---

**Last Updated:** 2026-09-05  
**Next Review:** when DG-3 starts — DG-0 is done (§2.1), DG-1 and DG-2 land together
