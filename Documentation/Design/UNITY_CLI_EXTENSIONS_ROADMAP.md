# Unity CLI Extensions Roadmap

**Version:** 1.1  
**Date:** 2026-09-27  
**Status:** Open backlog. Items are removed (archived) when implemented and verified.

> Unbuilt work on the agent ↔ Editor bridge described in
> [`../Architecture/UNITY_CLI_EDITOR_BRIDGE.md`](../Architecture/UNITY_CLI_EDITOR_BRIDGE.md): one
> verification (`UC-5`, closed 2026-09-27) and three unscheduled extensions (`UC-6`…`UC-8`). The `UC-*` ID
> space continues from the migration that built the bridge; `UC-0`…`UC-4` are closed and indexed
> in the Architecture doc.

**Audited:** 2026-09-27, at commit `58bbdecc` (branch `mcp-to-cli-migration`).
Each item's "What exists today" was checked against the live CLI (`unity --help`,
`unity command --help`, the Editor's command list) and the repo, not carried over from the
migration design.

**Relationship to other documents:**

- [`../Architecture/UNITY_CLI_EDITOR_BRIDGE.md`](../Architecture/UNITY_CLI_EDITOR_BRIDGE.md) — the
  implemented bridge; its §7 is the claim `UC-5` settles.
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — owns the
  validation suites `UC-6` would run headlessly.
- [`PROJECT_AUDITOR_FINDINGS_REPORT.md`](PROJECT_AUDITOR_FINDINGS_REPORT.md) — the report `UC-8`
  would re-run.

---

## Legend

| Field       | Values                                                                                                                                         |
|-------------|------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                            |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants or semantics)     |
| **Benefit** | 🟢 Core — high value or unlocks other planned work · 🟡 Situational / polish · ⚪ Minor                                                         |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ Terrain-affecting                                                               |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill) |

---

## Master summary

| ID       | Item                                                        | Effort | Risk | Benefit | Seed | Save | Status |
|----------|-------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|--------|
| **UC-5** | Confirm the Roslyn plugins are stripped from a release build |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  | ✅ 2026-09-27 |
| **UC-6** | Headless `Validate All` via `unity run --command` / `unity test` |   🟡   |  🟢  |   🟡    |  ✅  |  ✅  | —      |
| **UC-7** | `--runtime` inspection of a Development player              |   🟡   |  🟡  |   ⚪    |  ✅  |  ✅  | —      |
| **UC-8** | Re-run the Project Auditor report through `audit`           |   🟢   |  🟢  |   ⚪    |  ✅  |  ✅  | —      |

---

## UC-5 — Confirm Roslyn stripping in a release build ✅ 2026-09-27

**Closed.** The RC 94 IL2CPP release build (the first with the bridge) ships no `UnityPipeline.*`
or `Unity.Pipeline*` assembly. Its post-strip `Managed/` set is the same 69 assemblies as RC 93,
and the build is ~85 KB smaller. The Architecture doc's §7 holds the measurement. The check planned
here, zero hits for `CodeAnalysis`, turned out too broad: that string also matches the compiler's
own attribute namespace, present identically in both builds. Match the plugin names exactly
(`UnityPipeline.`) when re-checking after a package bump.

## UC-6 — Headless `Validate All` in CI

**What exists today.** The batch path is `Unity -batchmode … -executeMethod
Editor.Validation.Framework.ValidationSuiteCI.RunHeadless` (run-validation-suite skill). The CLI
offers `unity run --command` (a registered Editor command, headless) and `unity test` (Edit/Play
Mode tests with an NUnit report and CI-oriented exit codes). The suites are menu items, not NUnit
tests.

**Do.** Decide between registering `RunHeadless` as a Pipeline command for `unity run --command`,
and exposing the suites to `unity test`. Keep the existing NUnit3 XML contract.

## UC-7 — `--runtime` player inspection

**What exists today.** `unity command --runtime <player exec name>` targets a running Unity
Player instead of the Editor. It needs a Development player with the Pipeline runtime enabled;
`enableInBuilds` is `false` in this project.

**Do.** Only if a player-only bug needs live inspection: enable the runtime server for a
Development build and document the security posture. Never for Release.

## UC-8 — Project Auditor through the CLI

**What exists today.** The Editor exposes `audit` / `audit_status` (Project Auditor scan, async).
The current report was produced from a saved `.projectauditor` file.

**Do.** Re-run the audit through the CLI on the next audit pass, and compare its CSV against the
saved-report route before switching.

---

## Document History

* **v1.1** - `UC-5` closed: RC 94 release build ships no Pipeline/Roslyn assembly (Architecture §7).
* **v1.0** - Initial backlog, split out of `Design/UNITY_MCP_TO_CLI_MIGRATION.md` on its promotion to
  `Architecture/UNITY_CLI_EDITOR_BRIDGE.md`.

---

**Last Updated:** 2026-09-27  
**Next Review:** when CI work starts (`UC-6`)
