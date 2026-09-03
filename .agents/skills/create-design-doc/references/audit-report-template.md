# <Area> Feature Improvements Report <!-- or: <Area> <Analysis|Validation> Report -->

<!-- The three field lines below end in TWO TRAILING SPACES (invisible, easily stripped by an
     editor). Without them Markdown joins Version/Date/Status into one run-on line. Same in the
     footer. Rule: two trailing spaces on any line whose NEXT line starts a new **Label:** field
     — never on ordinary wrapped prose, which is meant to rejoin. See the skill's Step 3. -->
**Version:** 1.0  
**Date:** <YYYY-MM-DD>  
**Status:** Open backlog. Items are removed (archived) when implemented and verified.

> The master backlog for **<area>** in the VoxelEngine — <one line on scope>. Sibling report to
> [`<OTHER_REPORT>.md`](<OTHER_REPORT>.md) (`<ID>-*`); <where the combined ranked roadmap lives,
> if reports share one>.

**Audited:** <YYYY-MM-DD>, at commit `<short-hash>` (branch `<branch>`).
Findings are from static review of <the code actually read>. Runtime state was **verified in
code, not assumed** — see each item's "What exists today".
<!-- Gap sweeps and re-ranks add dated lines:
**Amended:** <YYYY-MM-DD> — <e.g. second gap sweep added XX-7..XX-9>. -->

**Relationship to other documents:**

- [`../Architecture/<DOC>.md`](../Architecture/<DOC>.md) — authoritative doc for the audited system.
- [`<OTHER_REPORT>.md`](<OTHER_REPORT>.md) — cross-linked items: `<ID-N>` (do together with ...).

---

## Legend

<!-- Copy verbatim across reports so symbols stay comparable; adjust the Benefit meaning note
     only if this report redefines it (feature reports use player-facing value, perf reports use
     frame-time/GC). -->

| Field       | Values                                                                                                                                         |
|-------------|------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                            |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants or semantics)     |
| **Benefit** | 🟢 Core — high value or unlocks other planned work · 🟡 Situational / polish · ⚪ Minor                                                         |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ Terrain-affecting                                                               |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill) |

---

## Master summary table

**This table is the report's ID index — the whole ID space, open and closed.** A completed item
keeps its row (marked ✅) even after its detail section is archived, and an abandoned one is marked
⛔ rather than deleted. IDs are never recycled and never dropped: commit messages and code comments
cite them, so this table is what keeps those backlinks resolvable. It is also the part that must
survive if the report is ever merged into an Architecture doc.

| ID     | Finding            | Effort | Risk | Benefit | Seed | Save |
|--------|--------------------|:------:|:----:|:-------:|:----:|:----:|
| <ID>-1 | <One-line finding> |   🟡   |  🟢  |   🟢    |  ✅   |  ✅   |

<!-- If multiple reports feed one roadmap, the combined ranked list lives in exactly ONE of
     them; the others link to it. -->

---

## Detail sections

### <ID>-1 — <Finding title>

**Classification:** <Core | Polish | ...>. <Rank in the combined roadmap, if ranked.>

**What exists today:** <verified current state, with `File.cs:line` anchors — never assumed.>

**Gap / finding:** <what is missing or wrong, and why it matters.>

**Proposal:** <the recommended change; sub-numbered §-sections if large. Options with ✅/❌ and
an explicit verdict when a real choice exists.>

**Dependencies / cross-links:** <other items, skills, or docs this interacts with.>

---

## Document History

* **v1.0** - Initial report

---

**Last Updated:** <YYYY-MM-DD>  
**Next Review:** <event trigger, e.g. "next gap sweep" / "after <ID>-1 ships">
