# Unity CLI Extensions Roadmap

**Version:** 1.0  
**Date:** 2026-09-27  
**Status:** Open backlog. Items are removed (archived) when implemented and verified.

> Unbuilt work on the agent ↔ Editor bridge described in
> [`../Architecture/UNITY_CLI_EDITOR_BRIDGE.md`](../Architecture/UNITY_CLI_EDITOR_BRIDGE.md): one
> open verification (`UC-5`) and three unscheduled extensions (`UC-6`…`UC-8`). The `UC-*` ID
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
| **UC-5** | Confirm the Roslyn plugins are stripped from a release build |   🟢   |  🟢  |   🟡    |  ✅  |  ✅  | —      |
| **UC-6** | Headless `Validate All` via `unity run --command` / `unity test` |   🟡   |  🟢  |   🟡    |  ✅  |  ✅  | —      |
| **UC-7** | `--runtime` inspection of a Development player              |   🟡   |  🟡  |   ⚪    |  ✅  |  ✅  | —      |
| **UC-8** | Re-run the Project Auditor report through `audit`           |   🟢   |  🟢  |   ⚪    |  ✅  |  ✅  | —      |

---

## UC-5 — Confirm Roslyn stripping in a release build

**What exists today.** `com.unity.pipeline` bundles five precompiled Roslyn plugin DLLs
(~9.1 MB), enabled for the Win64/Linux64/macOS players and auto-referenced. Release player
scripts do not include `Unity.Pipeline`, the only assembly that uses them, and the IL2CPP linker
removed the previous package's unreferenced assemblies from RC 93. Whether it also removes these
plugins has not been measured.

**Do.** On the next IL2CPP release build, search `<build>_Data/il2cpp_data/Metadata/global-metadata.dat`
and the `…_BackUpThisFolder_ButDontShipItWithYourGame/Managed` folder for `UnityPipeline` and
`CodeAnalysis`. Zero hits closes this item; record the result in the Architecture doc's §7. Any
hit means the plugins ship: restrict their player platforms (a package-level change, so it needs
an embed or an asset postprocessor) and re-measure.

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

* **v1.0** - Initial backlog, split out of `Design/UNITY_MCP_TO_CLI_MIGRATION.md` on its promotion to
  `Architecture/UNITY_CLI_EDITOR_BRIDGE.md`.

---

**Last Updated:** 2026-09-27  
**Next Review:** on the next release build (`UC-5`), or when CI work starts (`UC-6`)
