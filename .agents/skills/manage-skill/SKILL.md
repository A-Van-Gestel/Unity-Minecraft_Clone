---
name: manage-skill
description: Create, edit, or audit an agent skill under .agents/skills/ following the Agent Skills specification and this project's conventions — frontmatter rules, trigger-rich descriptions, the description-token budget, progressive disclosure, bidirectional seams, and never-stale references/ files. Use when the user asks to create, write, or scaffold a new skill, edit or audit an existing SKILL.md, turn a workflow or protocol into a skill, or asks "should this be a skill?".
---

# Manage a skill

Authoring and maintenance protocol for skills in `.agents/skills/` — creating a new one, and
editing or auditing an existing one. Skills are auto-discovered from that directory; a
correctly-formed skill appears in the agent's available-skills list with no other registration
step. The format follows the **Agent Skills specification** — the condensed spec lives in
[references/agent-skills-spec.md](references/agent-skills-spec.md); read it when unsure about a
constraint instead of guessing.

Creating a new skill is Steps 1–5 below; **Editing an existing skill** is a separate short
section after them.

## Step 1 — Decide it should be a skill at all

A skill is the right container for **episodic, on-demand procedure**: a workflow the agent needs
in full only when a matching task appears. It is the wrong container for:

- **Always-relevant constraints** → `CLAUDE.md` (loaded every session; keep it short, link out).
- **Single facts / user preferences** → auto-memory.
- **System knowledge** → `Documentation/` (skills may *point* there, not duplicate it).

Also check the existing skills list first: extending a sibling skill (or splitting one) may beat
adding a near-duplicate. Every new skill's description is loaded into **every** session forever,
whether or not it fires, so measure the standing cost before adding to it:

```bash
# total always-loaded description cost across all skills, in BYTES (÷4 ≈ tokens)
for f in .agents/skills/*/SKILL.md; do grep -m1 '^description: ' "$f"; done | wc -c
```

The per-file `-m1` matters: a plain `grep -h` over all files also counts every scaffold or example
`description:` line sitting in a skill *body* (this file has one), inflating the total.

Re-measure rather than trusting a remembered number. A near-duplicate costs every future session
real context *and* dilutes activation — two similar descriptions compete for the same trigger.
**Prefer extending or splitting a sibling over adding a near-duplicate.** If the new capability
is one section long, it is a section in an existing skill.

## Step 2 — Name and scaffold

```
.agents/skills/<skill-name>/
├── SKILL.md          # required — UPPERCASE filename
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
add them only with a concrete reason. Precedent: `unity-mcp` uses `metadata` to pin the package
version it was authored against, so version drift is detectable.)

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
Same rule for counts and versions: if a number will drift, say how to re-measure it rather than
baking in today's value.

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
3. **Cross-reference seams, both directions.** If the new skill borders an existing one (shared
   trigger surface), name the split in **both** bodies — add a routing line to the new skill, then
   edit the sibling's body to name the new one. A one-way seam is how two skills end up both
   half-owning a concern, and the older skill is the one an agent is more likely to already be
   inside. If a user could plausibly invoke the wrong one, put the routing line in the
   **description** too (`For X use <other-skill> instead`) — the only surface available before
   either body loads.
4. **Update `CLAUDE.md` only if** the skill must be discoverable from a rule that already lives
   there (e.g. it gates a workflow like serialization changes). Most skills need no CLAUDE.md
   mention — the description is the discovery mechanism.
5. Offer a commit message in the project's single-line `Verb: description` style; never
   auto-commit.

## Editing an existing skill

Most changes to a skill are edits, not new skills. The discipline differs from authoring:

- **If the edit adds capability, extend the description too.** The description is the only
  always-loaded surface (Step 3) — a skill that grows a new section nobody can trigger has gained
  nothing. Add the new trigger phrasing to the `description`.
- **If the edit changes what the skill owns, re-check the seam** (Step 5.3) in the *other*
  direction — a sibling's routing line may now name the wrong owner.
- **Preserve the description's trigger phrases.** They are load-bearing for activation; never tidy
  them out to make the line read more cleanly. A tidier description that stops matching is a skill
  that never runs.
- **Read the file back** after editing: no BOM, frontmatter still opens at byte 0, `name` still
  equals the directory, description still under 1024 chars, and every fenced code block is still
  valid (see Gotchas).

## Gotchas (from real incidents)

- **IDE auto-reflow corrupts SKILL.md code blocks.** An editor reformat has mangled fenced code
  examples into one-token-per-line garbage. After any IDE-side save of a `SKILL.md`, re-check that
  its code blocks are still valid.
- **Don't write generic knowledge.** Ask of every line: *would the agent get this wrong without
  it?* If no, cut it. A skill is the corrections and conventions specific to this repo, not a
  restatement of what a capable model already knows.

## Constraints

- **One skill, one concern.** If the body needs an "and also, separately…" section, split it.
- **Do not duplicate a sibling skill's rules** — link to the skill by name and let it own them.
- **Do not write speculative skills** for workflows that haven't happened yet at least once;
  skills encode *proven* procedure (a design doc is the right home for unproven plans).
- **Preserve the description's trigger phrases** when editing an existing skill — they are
  load-bearing for activation, not prose to be tidied.
