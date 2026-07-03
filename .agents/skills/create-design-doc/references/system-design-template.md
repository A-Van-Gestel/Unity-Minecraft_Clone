# <System Name> Design

**Version:** 1.0
**Date:** <YYYY-MM-DD>
**Status:** Proposed design — not implemented. <!-- or: Draft — far-horizon (<gate>). Not scheduled. -->
**Target:** Unity 6.4 (Mono for dev; IL2CPP for production) <!-- optional; keep for engine-touching designs -->

> One-paragraph blockquote summary: what the system is, and the single most important
> architectural decision it settles (bold the decision). A reader should be able to stop after
> this paragraph and know what was decided.
>
> For **Draft** docs add a second blockquote paragraph: what must be re-verified before
> implementation starts, and any prerequisite work that ships first.

**Audited:** <YYYY-MM-DD>, at commit `<short-hash>` (branch `<branch>`).
Findings are from static review of <the classes/files/systems actually read>. Runtime state was
verified in code (and via Unity MCP where noted), not assumed.
<!-- Later substantive changes add dated lines here:
**Amended:** <YYYY-MM-DD> — <what changed and why>. -->

**Relationship to other documents:**

- [`../Architecture/<DOC>.md`](../Architecture/<DOC>.md) — constraint or system this builds on.
- [`<SIBLING_DESIGN>.md`](<SIBLING_DESIGN>.md) — parent/sibling design and how they divide scope.

---

## 1. Goals & non-goals

### Goals

1. **<Goal>** — one line each; number them so later sections can reference "goal 2".

### Non-goals (v1)

- <Deferred item> — planned as a **v2 extension**, see the §<N> extension roadmap. (Version
  deferred wishes; don't phrase them as permanent rejections unless they are.)
- <Genuinely rejected item> — why it stays out.

---

## 2. Current state (what exists today)

| Area   | State                                                                    |
|--------|--------------------------------------------------------------------------|
| <Area> | <Finding from actual code reading; anchor with `File.cs:line` if useful> |

---

## 3. Decision: <the pivotal choice>

<One line naming why this is the pivotal decision. Repeat this section per major decision.>

### Option A — <name> (rejected)

- ✅ <Genuine strength — steelman the losers.>
- ❌ **<Deal-breaker in bold.>** <Explanation.>

### Option B — <name> ✅ **CHOSEN** <!-- in Drafts: ✅ **preferred direction** -->

<Why it wins, in prose. Reference precedents in this codebase where they exist.>

### Option C — <name> (rejected)

- ...

---

## 4. Data model / Architecture

<The design itself: data structures first, then runtime. Use code blocks for key types with XML
docstrings matching the coding style guide. Use an ASCII diagram when components interact:>

```
┌─────────────┐      ┌─────────────┐
│  Component  │ ───▶ │  Component  │
└─────────────┘      └─────────────┘
```

<Call out threading/ownership explicitly for anything touching jobs, pools, or chunk lifecycle.>

---

## 5. Prerequisites & integration points

<Work that must exist first (flag the genuinely blocking one with ⚠️), and reserved seats for
future features that will plug in without restructuring.>

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies |
|-------------------------------------------------|--------------------------|
| Voxels are packed `uint`s, no per-voxel objects | <...>                    |
| Burst jobs 100 % Burst-compatible               | <...>                    |
| No GC / LINQ in hot paths                       | <...>                    |
| Pooling conventions                             | <...>                    |
| No BinaryFormatter/JSON for terrain             | <...>                    |
| BlockIDs constants, no raw IDs                  | <...>                    |

---

## 7. Phased implementation plan

| Phase                   | Scope                        | Effort | Depends on |
|-------------------------|------------------------------|:------:|------------|
| **<P>0 — <Foundation>** | <Data/foundation work first> |   🟢   | —          |
| **<P>1 — <...>**        | <...>                        |   🟢   | <P>0       |

<State which minimal phase set delivers standalone value. For core systems:>

**Validation is built alongside, not after**: each phase adds its baselines to a
`Validate <System>` editor suite as it lands — <name the deterministic scope each phase pins>.
The <non-deterministic/audible/visual> layer stays verified in-game.

### Extension roadmap (post-<P>N, in intended order)

| Version | Extension                                                 |
|---------|-----------------------------------------------------------|
| **v2**  | <...>                                                     |
| **v3+** | <...> — gets its own design doc when it becomes concrete. |

---

## 8. Open questions <!-- Only questions still open AT COMMIT TIME. Same-session answers get

folded into the body instead — never ship "resolved same day" Q&A scaffolding. Drafts may
instead title this "Verification checklist (MUST re-verify before implementation)". -->

1. **<Question>** — <what would resolve it and where the answer will land>.

---

## Document History

* **v1.0** - Initial design

---

**Last Updated:** <YYYY-MM-DD>
**Next Review:** <event trigger, e.g. "when <P>0 starts" / "on promotion to Architecture">
