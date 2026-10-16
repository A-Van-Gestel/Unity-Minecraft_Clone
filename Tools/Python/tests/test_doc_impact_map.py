"""doc_impact_map.py: the docs-sync Step 1 table and no-doc list, read from a fixture skill in a throwaway repo."""
import unittest

from helpers import TempRepo

SKILL = """# Fixture

### Step 1 — Identify

| Code area | Primary doc |
|---|---|
| Lifecycle — `Assets/Scripts/World.cs` | `Architecture/LIFECYCLE.md` |
| Jobs — `Assets/Scripts/Jobs/` and `Assets/Scripts/Helpers/Light*.cs`, bare `ChunkData` | `Architecture/JOBS.md` (+ `some-skill` skill) |
| Naming, bracing | `Guides/STYLE.md` |

**Where there is deliberately no owning doc.** Prose before the list,
with a `Assets/Scripts/NotAList/` token that is not a bullet.

- Glue — `Assets/Scripts/Helpers/`,
  `Assets/Scripts/Config/` on a continuation line.
- Agent tooling — `.agents/`, `CLAUDE.md`.

When a change lands only here, `Assets/Scripts/AfterList/` is prose again.

- A later list in Step 1 — `Assets/Scripts/LaterList/` — is not part of the no-doc list.

### Step 2 — Classify

| Code area | Primary doc |
|---|---|
| Ignored — `Assets/Scripts/Step2Only.cs` | `Architecture/STEP2.md` |
"""

TRACKED = ["Assets/Scripts/World.cs", "Assets/Scripts/Jobs/MeshJob.cs", "Assets/Scripts/Helpers/LightHelper.cs",
           "Assets/Scripts/Helpers/Util.cs", "Assets/Scripts/Config/Cfg.cs", ".agents/x.md", "CLAUDE.md",
           "Documentation/Architecture/LIFECYCLE.md", "Documentation/Architecture/JOBS.md",
           "Documentation/Guides/STYLE.md", "Assets/Scripts/Audio/Sound.cs"]


class DocImpactMapTests(unittest.TestCase):
    def setUp(self):
        self.repo = TempRepo()
        self.repo.write("skill.md", SKILL)
        for path in TRACKED:
            self.repo.write(path, "base\n")
        self.repo.commit("base")

    def tearDown(self):
        self.repo.close()

    def run_map(self, *args, skill=None):
        self.repo.write("skill.md", skill or SKILL)
        return self.repo.run("doc_impact_map.py", "--skill", self.repo.path("skill.md"), *args)

    def section(self, out, title):
        body = out.split(f"## {title}", 1)[1]
        return body.split("\n## ", 1)[0]

    def test_prefix_glob_and_exact_tokens_match_their_rows(self):
        code, out = self.run_map("--paths", "Assets/Scripts/World.cs", "Assets/Scripts/Jobs/MeshJob.cs")
        self.assertEqual(code, 0, out)
        candidates = self.section(out, "Candidate docs")
        self.assertIn("Architecture/LIFECYCLE.md", candidates)
        self.assertIn("Architecture/JOBS.md", candidates)
        self.assertNotIn("some-skill", candidates)

    def test_glob_token_wins_over_the_no_doc_prefix(self):
        code, out = self.run_map("--paths", "Assets/Scripts/Helpers/LightHelper.cs", "Assets/Scripts/Helpers/Util.cs")
        self.assertEqual(code, 0, out)
        self.assertIn("LightHelper.cs", self.section(out, "Candidate docs"))
        no_doc = self.section(out, "Deliberately no doc")
        self.assertIn("Assets/Scripts/Helpers/ (1 paths)", no_doc)

    def test_glob_star_does_not_cross_into_subfolders(self):
        code, out = self.run_map("--paths", "Assets/Scripts/Helpers/Lighting/Foo.cs")
        self.assertEqual(code, 0, out)
        self.assertNotIn("Foo.cs", self.section(out, "Candidate docs"))

    def test_no_doc_bullets_include_continuations_and_bare_file_names_only(self):
        code, out = self.run_map("--paths", "Assets/Scripts/Config/Cfg.cs", "CLAUDE.md", ".agents/x.md")
        self.assertEqual(code, 0, out)
        no_doc = self.section(out, "Deliberately no doc")
        for pattern in ("Assets/Scripts/Config/", "CLAUDE.md", ".agents/"):
            self.assertIn(pattern, no_doc)
        self.assertIn("(parsed 3 map rows, 4 no-doc patterns)", out)

    def test_unmapped_paths_are_listed_and_meta_files_ignored(self):
        code, out = self.run_map("--paths", "Assets/Scripts/Audio/Sound.cs", "Assets/Scripts/Audio/Sound.cs.meta")
        self.assertEqual(code, 0, out)
        unmapped = self.section(out, "Unmapped")
        self.assertIn("Assets/Scripts/Audio/ (1): Sound.cs", unmapped)
        self.assertNotIn(".meta", out)

    def test_changed_doc_is_marked_on_its_candidate_row(self):
        self.repo.write("Assets/Scripts/World.cs", "changed\n")
        self.repo.write("Documentation/Architecture/LIFECYCLE.md", "changed\n")
        code, out = self.run_map()
        self.assertEqual(code, 0, out)
        self.assertIn("Architecture/LIFECYCLE.md [CHANGED]", self.section(out, "Candidate docs"))
        self.assertIn("Documentation/Architecture/LIFECYCLE.md", self.section(out, "Docs changed"))

    def test_range_reads_the_committed_diff(self):
        base = self.repo.git("rev-parse", "HEAD").strip()
        self.repo.write("Assets/Scripts/Jobs/MeshJob.cs", "changed\n")
        self.repo.commit("change a job")
        code, out = self.run_map("--range", f"{base}..HEAD")
        self.assertEqual(code, 0, out)
        self.assertIn("Architecture/JOBS.md", self.section(out, "Candidate docs"))

    def test_subject_rows_are_listed_for_judging(self):
        code, out = self.run_map("--paths", "Assets/Scripts/World.cs")
        self.assertIn("Naming, bracing -> Guides/STYLE.md", self.section(out, "Rows to judge by subject"))

    def test_tables_outside_step1_are_ignored(self):
        code, out = self.run_map("--paths", "Assets/Scripts/Step2Only.cs")
        self.assertNotIn("STEP2", out)

    def test_map_without_the_sentinel_row_is_unparsed(self):
        skill = SKILL.replace("`Assets/Scripts/World.cs`", "World.cs")
        code, out = self.run_map("--paths", "Assets/Scripts/World.cs", skill=skill)
        self.assertEqual(code, 2, out)
        self.assertIn("MAP UNPARSED", out)

    def test_stale_token_and_missing_doc_exit_3(self):
        skill = SKILL.replace("`Assets/Scripts/Config/`", "`Assets/Scripts/Gone/`").replace(
            "`Guides/STYLE.md`", "`Guides/GONE.md`")
        code, out = self.run_map("--paths", "Assets/Scripts/World.cs", skill=skill)
        self.assertEqual(code, 3, out)
        stale = self.section(out, "Stale map entries")
        self.assertIn("`Assets/Scripts/Gone/` matches no tracked file", stale)
        self.assertIn("doc `Guides/GONE.md` does not exist", stale)

    def test_clean_map_has_no_stale_entries(self):
        code, out = self.run_map("--paths", "Assets/Scripts/World.cs")
        self.assertEqual(code, 0, out)
        self.assertIn("none", self.section(out, "Stale map entries"))


if __name__ == "__main__":
    unittest.main()
