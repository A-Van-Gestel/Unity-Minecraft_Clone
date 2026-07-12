# NUnit3 XML results — anatomy & mapping

The headless path (`ValidationSuiteCI.RunHeadless`, or `-executeMethod … RunHeadless`) writes an
NUnit3 `test-run` document via `Framework/NUnitXmlWriter` (default path
`TestResults/validation-results.xml`, gitignored). The in-editor menu/agent runs do **not** write it
— they only log to the console. The writer sits behind `IValidationResultWriter`, so a different
format (e.g. JUnit) could be emitted later without changing callers.

## Shape

```
test-run                     ← whole aggregate run
└── test-suite (TestFixture) ← one per validation suite, in registry order
    └── test-case            ← one per scenario
        ├── properties/property name="known-bug"   (known-bug scenarios only)
        ├── failure/message + failure/stack-trace   (result="Failed" only)
        └── reason/message                           (result="Inconclusive" only)
```

## Key attributes (same names on `test-run` and each `test-suite`)

| Attribute | Meaning |
|-----------|---------|
| `result` | `"Failed"` if any baseline failed at that level, else `"Passed"`. |
| `testcasecount` / `total` | Number of scenarios. |
| `passed` | baseline passes **+** known-bug scenarios that now pass (fix candidates). |
| `failed` | baseline failures (the regression signal). |
| `inconclusive` | known-bug scenarios still reproducing their bug (expected). |
| `duration` | seconds (culture-invariant); `start-time`/`end-time` on `test-run` only. |

Roll-ups are computed at both levels so the file is internally consistent; the
`Validation Framework` self-test round-trips this in-memory every `Validate All` run to keep it from
drifting.

## Scenario → test-case mapping

| Scenario outcome | `test-case result` | Extra |
|------------------|--------------------|-------|
| Baseline passed | `Passed` | — |
| Baseline failed / suite threw / isolation-failed | `Failed` | `<failure><message>` (+ `<stack-trace>` when an exception was captured) |
| Known-bug still reproducing | `Inconclusive` | `<reason><message>` + `properties/property name="known-bug"` |
| Known-bug now passing | `Passed` | `label="FixCandidate"` + the `known-bug` property |

## Exit code (batch only)

`RunHeadless` exits **0 only when** `Success && !AnySuiteRanNothing && !RanNothing` — i.e. every
baseline passed **and** no suite registered zero scenarios **and** at least one suite ran. Any
baseline failure, a suite that ran nothing, an unknown `-validationSuites` name, or a crash → exit
**1**. Inconclusive (known-bug) results never affect the exit code.

## Sample (trimmed)

```xml
<?xml version="1.0" encoding="utf-8"?>
<test-run id="0" testcasecount="5" result="Failed" total="5" passed="3" failed="1"
          inconclusive="1" skipped="0" warnings="0"
          start-time="…" end-time="…" duration="0.015">
  <test-suite type="TestFixture" id="1" name="Alpha" fullname="Alpha" testcasecount="5"
              result="Failed" total="5" passed="3" failed="1" inconclusive="1" duration="0.015">
    <test-case id="1-1" name="A pass1" result="Passed" duration="0.001" asserts="0" />
    <test-case id="1-3" name="A fail" result="Failed" duration="0.003" asserts="0">
      <failure><message><![CDATA[boom]]></message><stack-trace><![CDATA[…]]></stack-trace></failure>
    </test-case>
    <test-case id="1-4" name="A repro" result="Inconclusive" duration="0.004" asserts="0">
      <properties><property name="known-bug" value="Bug 99" /></properties>
      <reason><message><![CDATA[Reproduces Bug 99 (expected until fixed/implemented).]]></message></reason>
    </test-case>
    <test-case id="1-5" name="A fixcand" result="Passed" label="FixCandidate" duration="0.005" asserts="0">
      <properties><property name="known-bug" value="Bug 42" /></properties>
    </test-case>
  </test-suite>
</test-run>
```

## Reading it

- `test-run/@result` + `@failed` is the one-glance verdict; `@failed > 0` is a regression.
- `@inconclusive > 0` is expected (open known bugs) — never a failure.
- To find *what* broke: the `test-case[@result='Failed']/failure/message` nodes (mirror the
  console `[FAIL]` + `Failed baselines` recap).
- A `test-case[@label='FixCandidate']` is a documented bug that may now be fixed → confirm in-game,
  then use the `validation-driven-bugfix` skill.
- NUnit3 is currently round-trip-checked in-memory only, not yet against a live CI parser (deferred
  with CI itself); validate against the target consumer once a real pipeline exists.
```
