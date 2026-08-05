# Git Hygiene: `.gitattributes` Expansion, EOL Normalization, and Git LFS Scrub

**Version:** 1.0  
**Date:** 2026-08-05  
**Status:** **TEMPORARY RUNBOOK — delete this file when Phase D completes.** Not a design doc; it
describes a one-time repo-maintenance operation with no lasting architecture to document.

> Three independent pieces of git hygiene, ordered by risk. **Phase A** scrubs Git LFS, which is
> installed but has never tracked a single file. **Phase B** expands `.gitattributes` with diff
> drivers and a binary safety net. **Phase C** pins line endings to LF, which — contrary to the
> usual line-ending horror story — has **zero commit churn here**, because the index already
> stores LF for every file. **Phase D** deletes this runbook. A full project backup is taken
> first at the user's request; the measured blast radius (§2) says that is prudence, not
> necessity.

**Audited:** 2026-08-05, at commit `73a05ecc` (branch `feat/world-scaling`).
Every claim below was verified this session by direct command, not inferred: LFS history was
searched with `git log --all -S` and `git grep` in `HEAD`; the EOL census was computed by reading
all 1,787 tracked files; index-vs-worktree encoding was compared with `git show :<path>`; the
merge-driver fallback and both diff drivers were proven in throwaway sandbox repos.

**Relationship to other documents:**

- [`../../.agents/skills/unity-file-ops/SKILL.md`](../../.agents/skills/unity-file-ops/SKILL.md)
  — owns Unity `.meta`/scene/prefab file operations; Phase B's merge driver and Phase C's
  `eol=lf` both touch the file types that skill governs.
- [`../../.gitattributes`](../../.gitattributes) — the file Phases B and C extend. Its existing
  `*.md whitespace=-blank-at-eol` rule (added `63f61277`) must survive both edits.
- [`.../create-design-doc` Step 3](../../.agents/skills/create-design-doc/SKILL.md) — the
  Markdown hard-line-break convention that `.gitattributes` protects; Phase C must not regress it.

---

## 1. Verified findings

| # | Finding                                                                                        | How it was verified                                              |
|---|------------------------------------------------------------------------------------------------|------------------------------------------------------------------|
| 1 | **Git LFS has never tracked anything.** `git lfs ls-files` = 0.                                | Direct command                                                   |
| 2 | **No LFS pointer was ever committed**, on any branch, at any point in history.                  | `git log --all -S 'version https://git-lfs.github.com'` → empty; `git grep` in `HEAD` → empty |
| 3 | LFS residue is **local only**: 4 hooks, 2 `lfs.*` keys in `.git/config`, `.git/lfs/` (60 KB, 12 files — all lock caches, **zero objects**). | `ls`, `du`, `git config --local --list`                          |
| 4 | `filter.lfs.*` (4 keys) live in the **global** `~/.gitconfig` — machine-wide, not repo-scoped.  | `git config --global --list`                                     |
| 5 | **The index already stores LF for every file.** A `.cs` that is CRLF in the worktree is LF in the index. | `git show :Assets/Editor/AtlasPacker/AtlasPackerWindow.cs`       |
| 6 | Worktree EOL is **mixed**: 1,438 LF / 254 CRLF / 94 binary. CRLF concentrated in `.cs` (136), `.meta` (50), `.md` (45). | Byte scan of every tracked file                                  |
| 7 | Asset serialization is **Force Text** (`m_SerializationMode: 2`), so all Unity assets are YAML. | `ProjectSettings/EditorSettings.asset`                           |
| 8 | An **undefined** merge driver falls back safely to git's default text merge with normal conflict markers — no error, no corruption. | Sandbox repo, deliberate conflict                                |
| 9 | `diff=csharp` and `diff=markdown` are built in and change hunk headers to the enclosing method / heading. | Sandbox repos                                                    |
| 10 | Only one shell script exists (`Tools/Apply-AiAssistantMcpPatch.ps1`); **no `.bat`/`.cmd`** that would require CRLF. | `git ls-files`                                                   |

### Why Phase C is low-risk (finding 5, restated)

`core.autocrlf = true` has been normalizing CRLF → LF **on every commit**, so the repository's
blobs are already LF throughout. Adding `eol=lf` therefore changes only what git writes at
**checkout** time. Consequences:

- `git add --renormalize .` is expected to stage **nothing** — there is no blob to change.
- The 254 CRLF worktree files converge to LF as they are next checked out or rewritten.
- `git status` stays clean throughout; there is no mass-modification commit.

This is the opposite of the usual "normalize line endings" operation, which rewrites every blob.
If `--renormalize` *does* stage files, that contradicts finding 5 — **stop and re-audit** rather
than committing the result.

---

## 2. Phase 0 — Backup (user-required gate)

Take a **full project backup before Phase A** — user's explicit instruction. Recommended shape:

1. Close Unity (so no asset import is mid-flight and `Library/` is quiescent).
2. Copy the entire project directory, or archive it:
   `Minecraft Clone` → `Minecraft Clone.backup-2026-08-05`.
3. Confirm the copy contains `.git/` (many archive tools skip dot-directories by default —
   verify explicitly, since `.git/` is the only thing that makes the backup a real fallback).

**Gate:** backup exists, contains `.git/`, and the user has confirmed it before any phase runs.

> Blast-radius note for calibration: Phases A–C touch `.git/config`, `.git/hooks/`, `.git/lfs/`
> and one tracked file (`.gitattributes`). No phase rewrites history, and no phase modifies a
> tracked file's content. The backup is cheap insurance, not a sign of a dangerous operation.

---

## 3. Phase A — Scrub Git LFS

Because no pointer was ever committed (findings 1–2), this is **local cleanup only**. No
`git lfs migrate`, no history rewrite, no coordination with any remote.

1. `git lfs uninstall --local` — removes this repo's LFS hooks and local filter config.
2. Verify the 4 hooks are gone: `post-checkout`, `post-commit`, `post-merge`, `pre-push` should
   no longer reference `lfs` (the `pre-push` hook is the one that currently aborts a push when
   `git-lfs` is missing from `PATH`).
3. Remove the two leftover keys if `uninstall` did not:
   `git config --local --unset lfs.repositoryformatversion` and
   `git config --local --remove-section 'lfs.https://github.com/A-Van-Gestel/Unity-Minecraft_Clone.git/info/lfs'`
   (quote the section name — it contains slashes and colons).
4. Delete `.git/lfs/` (60 KB of stale lock caches; contains no objects, so nothing is lost).

**Decision required — global config (finding 4):** `filter.lfs.*` is in `~/.gitconfig` and affects
**every repository on this machine**. Removing it (`git lfs uninstall` without `--local`, or
`git config --global --remove-section filter.lfs`) is only safe if **no other repo on this machine
uses LFS**. Ask before touching it. Leaving it costs nothing here — with no `.gitattributes`
`filter=lfs` entries, the filter never engages.

**Gate:** `git lfs ls-files` still empty · no `lfs` string in any `.git/hooks/*` · `git status`
clean · a `git push --dry-run` (or a real push) succeeds without the git-lfs pre-push check.

---

## 4. Phase B — Expand `.gitattributes` (Tier 1 + Tier 2)

Append to the existing `.gitattributes`, preserving the `*.md whitespace=-blank-at-eol` rule.

```gitattributes
# --- Diff hunk headers (built-in drivers; verified 2026-08-05) -------------------
*.cs    diff=csharp
*.md    diff=markdown

# --- Binary safety net ----------------------------------------------------------
# Git guesses binary from NUL bytes in the first 8 KB; a file that looks textual can be
# misdetected and EOL-mangled. `binary` == `-diff -merge -text`.
*.png *.jpg *.jpeg *.gif *.tga *.psd *.tif *.tiff *.exr *.hdr   binary
*.fbx *.blend *.obj *.dae *.3ds                                 binary
*.wav *.mp3 *.ogg *.aiff *.aif                                  binary
*.ttf *.ttc *.otf                                               binary
*.dll *.so *.dylib *.a *.bundle *.pdb *.mdb *.unitypackage      binary
*.pdf *.cubemap                                                 binary

# --- Unity YAML smart merge (Tier 2, optional) ----------------------------------
# Safe to declare before configuring the driver: an UNDEFINED merge driver falls back to
# git's default text merge with normal conflict markers (verified in a sandbox repo).
*.unity *.prefab *.asset *.mat *.anim *.controller *.physicMaterial   merge=unityyamlmerge
```

**Never** mark `.unity`/`.prefab`/`.asset`/`.meta` as `binary` or `-text`. Under Force Text
(finding 7) they are YAML; marking them binary destroys diffs and makes scene conflicts
unresolvable. That is the actual `.asset` corruption risk — not their mere presence in this file.

**Gate:** `git diff -U1 -- <any .cs>` shows a method name in the `@@` header · `git status` clean
apart from `.gitattributes` · `python Tools/Python/check_markdown_breaks.py` still exits 0.

---

## 5. Phase C — Pin line endings to LF

Append:

```gitattributes
# --- Line endings ---------------------------------------------------------------
# The index already stores LF throughout (core.autocrlf=true normalized on every commit),
# so this changes only what git writes at CHECKOUT. It makes the worktree consistent across
# clones regardless of each machine's core.autocrlf, and matches both .editorconfig
# (end_of_line = lf) and what Unity itself writes for YAML/.meta.
* text=auto eol=lf

# Windows batch files are the one format that genuinely needs CRLF. None exist today; this
# is a guard for future additions.
*.bat *.cmd text eol=crlf
```

Then, in order:

1. `git add --renormalize .`
2. `git status` — **expected: nothing staged** (finding 5). If files *are* staged, stop: the
   audit's central assumption is wrong. Re-verify with `git show :<path>` before proceeding.
3. Commit `.gitattributes` only.
4. Optional worktree convergence — force the 254 CRLF files to be rewritten as LF now instead of
   lazily. Only with a clean tree and the backup in hand:
   `git rm --cached -r -q . && git reset --hard`
5. Re-run the EOL census; CRLF count should drop toward 0 (binaries excluded).

**Unity-specific check (do not skip):** after step 4, open the Unity Editor and confirm it does
not mass-reimport or mark assets dirty. `.meta` and YAML assets are LF as Unity writes them, so
this should be a no-op — but it is the one interaction this plan cannot prove from the shell.

**Gate:** `git status` clean · Unity opens without a reimport storm or console errors ·
`python Tools/Python/check_markdown_breaks.py` exits 0 · `git diff --check HEAD~1 HEAD` quiet ·
spot-check that `Documentation/` files retain their two-space hard line breaks.

---

## 6. Phase D — Delete this runbook

Delete `Documentation/Design/GIT_HYGIENE_EOL_AND_LFS_SCRUB.md` and commit. Nothing here belongs
in the permanent documentation set: Phase A removes a mistake, Phases B–C are one-time repo
config whose rationale lives in `.gitattributes`' own comments.

If any decision from §3 (global LFS config) or §4 (Tier 2 merge driver) was made differently than
recorded here, fold the outcome into `.gitattributes` comments **before** deleting.

---

## 7. Rollback

| Phase | Rollback                                                                                          |
|-------|---------------------------------------------------------------------------------------------------|
| A     | `git lfs install --local` restores hooks and filter config. Nothing was committed, so nothing to revert. |
| B / C | `git revert` the `.gitattributes` commit, or restore the file from the previous commit.           |
| C-4   | Worktree-only; `git reset --hard` from the backup or re-checkout. No blob changed, so history is intact. |
| Any   | Full restore from the Phase 0 backup.                                                             |

---

## 8. Out of scope

- **History rewriting of any kind.** Findings 1–2 make it unnecessary; nothing about this plan
  justifies `git lfs migrate` or `filter-branch`.
- **`.editorconfig` changes** — it is already correct and already agrees with Phase C's `eol=lf`.
- **`linguist-*` attributes** — cosmetic GitHub language-stat tuning, no functional benefit.
- **Configuring the UnityYAMLMerge driver itself.** Phase B only *declares* it; wiring
  `merge.unityyamlmerge.driver` to Unity's `UnityYAMLMerge.exe` is a separate, per-machine step.

---

## Document History

* **v1.0** - Initial runbook — Phase A (LFS scrub) written after confirming no pointer was ever
  committed; Phase C written after confirming the index is already LF, which reduces it from a
  mass-blob rewrite to a checkout-time behaviour change.

---

**Last Updated:** 2026-08-05  
**Next Review:** none — delete at Phase D.
