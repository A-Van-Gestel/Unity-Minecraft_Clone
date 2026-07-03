---
name: create-skill
description: Author a new agent skill under .agents/skills/ following the Agent Skills specification and this project's conventions — frontmatter rules, trigger-rich descriptions, progressive disclosure, and never-stale references/ files. Use when the user asks to create, write, or scaffold a new skill, turn a workflow or protocol into a skill, or asks "should this be a skill?".
---

# Create Skill

Authoring protocol for new skills in `.agents/skills/`. Skills are auto-discovered from that
directory; a correctly-formed skill appears in the agent's available-skills list with no other
registration step. The format follows the **Agent Skills specification** — the condensed spec
lives in [references/agent-skills-spec.md](references/agent-skills-spec.md); read it when unsure
about a constraint instead of guessing.

## Step 1 — Decide it should be a skill at all

A skill is the right container for **episodic, on-demand procedure**: a workflow the agent needs
in full only when a matching task appears. It is the wrong container for:

- **Always-relevant constraints** → `CLAUDE.md` (loaded every session; keep it short, link out).
- **Single facts / user preferences** → auto-memory.
- **System knowledge** → `Documentation/` (skills may *point* there, not duplicate it).

Also check the existing skills list first: extending a sibling skill (or splitting one) may beat
adding a near-duplicate. Every new skill permanently costs ~100 tokens of description in every
session, so the description must earn its seat.

## Step 2 — Name and scaffold

```
.agents/skills/<skill-name>/
├── SKILL.md          # required — UPPERCASE filename (one legacy lowercase skill.md exists; do not copy that)
├── references/       # optional — docs loaded on demand
├── scripts/          # optional — runnable helpers
└── assets/           # optional — templates, static resources
```

- `name`: lowercase letters/numbers/hyphens, 1–64 chars, no leading/trailing/double hyphens,
  **must equal the directory name**. Verb-first names read best (`create-…`, `archive-…`,
  `refactor-…`) for workflows; noun names (`chunk-lifecycle`, `unity-mcp`) for reference cards.
- Save `SKILL.md` as **UTF-8 without BOM**. A BOM before the opening `---` breaks frontmatter
  parsing and the skill's description renders as garbage in the skills list (this has actually
  happened in this repo).

## Step 3 — Write the frontmatter

This project uses only the two required fields:

```yaml
---
name: <skill-name>
description: <what it does + when to use it, ≤1024 chars>
---
```

(`license`, `compatibility`, `metadata`, `allowed-tools` exist in the spec — see the reference —
but are unused here; add them only with a concrete reason.)

**The description is the skill's only always-loaded surface** — the agent decides whether to
activate the skill from the description alone. Write it as:

1. One sentence: what the skill does (specific nouns, not "helps with X").
2. "Use when …": concrete trigger situations *and* literal user phrasings in quotes
   (e.g. `or when the user says "that worked", "bug is fixed"`).
3. If an adjacent skill could be confused with it, add an explicit routing line
   (e.g. `For updating EXISTING docs use the docs-sync skill instead.`).

## Step 4 — Write the body

No format restrictions, but the house style that has worked here:

- **Title + one-paragraph mission** stating what the skill owns and (if relevant) which sibling
  skill owns the neighboring concern — seams stated in both skills, in both directions.
- **Numbered `## Step N` sections** for workflows; tables for reference cards.
- **"When to use / when to skip"** near the top if activation is nuanced.
- **Constraints section** at the end for hard rules (the "do not"s).
- Reference project ground truth by path (`Documentation/…`, `CLAUDE.md` rules) instead of
  restating it; restated facts go stale silently.

**Budgets (progressive disclosure):** keep `SKILL.md` under ~500 lines / ~5k tokens. Anything
bulky, stable, or only-sometimes-needed goes in `references/` as its own focused file, linked
with a relative path from the skill root, one level deep. The agent loads reference files only
when needed — this is the cheap place for templates, specs, and lookup tables.

**Never-stale rule:** do not hardcode links to living artifacts (specific design docs, code
line numbers, current backlog items) in `SKILL.md` — they get promoted, moved, and archived.
Either describe how to *find* the artifact (grep/glob/graph query) or put a stable
template/snapshot in `references/`. Naming stable *directories* and *conventions* is fine.

**Scripts:** anything executable goes in `scripts/`, self-contained or with dependencies stated
at the top; per the repo's Python protocol, substantial persistent tooling belongs in
`Tools/Python/` with the skill pointing at it.

## Step 5 — Validate and integrate

1. **Self-check against the spec** (frontmatter constraints, naming, budgets) using
   [references/agent-skills-spec.md](references/agent-skills-spec.md). The upstream
   `skills-ref validate` CLI exists but is not installed here — the manual checklist in the
   reference covers what it checks.
2. **Read the file back once** and confirm: no BOM, frontmatter opens at byte 0, `name` matches
   the directory, description under 1024 chars.
3. **Cross-reference seams**: if the new skill borders an existing one (shared trigger surface),
   edit the sibling's body (not description, unless it misroutes) to name the split.
4. **Update `CLAUDE.md` only if** the skill must be discoverable from a rule that already lives
   there (e.g. it gates a workflow like serialization changes). Most skills need no CLAUDE.md
   mention — the description is the discovery mechanism.
5. Offer a commit message in the project's single-line `Verb: description` style; never
   auto-commit.

## Constraints

- **One skill, one concern.** If the body needs an "and also, separately…" section, split it.
- **Do not duplicate a sibling skill's rules** — link to the skill by name and let it own them.
- **Do not write speculative skills** for workflows that haven't happened yet at least once;
  skills encode *proven* procedure (a design doc is the right home for unproven plans).
- **Preserve the description's trigger phrases** when editing an existing skill — they are
  load-bearing for activation, not prose to be tidied.
