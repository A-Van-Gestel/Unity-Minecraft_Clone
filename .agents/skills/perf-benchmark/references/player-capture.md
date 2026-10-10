# Player capture recipe

Companion reference for the `perf-benchmark` skill: running the benchmark in a **built IL2CPP player**,
end to end from the shell, with no clicks. Two flows share the build and cleanup steps:

| Flow | Build | Use for |
|---|---|---|
| **A — Unattended run** | `Windows - Production` (Master, non-development) | Shipping-configuration numbers from the benchmark report |
| **B — Profiler capture** | `Windows - Development` (Master IL2CPP + Development flag) | GC.Alloc call stacks / Profiler data for one benchmark phase |

Profiler API details (the `ProfilerQueries` / `ProfilerCapture` entries) live in the `unity-editor` skill,
`references/profiler.md`; this file only strings them into the player workflow.

## 1. Build

**First record the active build profile** (`AssetDatabase.GetAssetPath(BuildProfile.GetActiveBuildProfile())`
via `unity command eval`): it lives in `Library/`, not git, and a build replaces it — §5 restores it. A null
profile means the classic platform settings were active; record that too.

```bash
OUT="<parent of the repo>/<YYYY-MM-DD> - <purpose> [IL2CPP]/Minecraft Clone.exe"
# Flow A
unity command build --profileName "Windows - Production" --outputPath "$OUT" --confirm true --result-only
# Flow B — the Development flag must be passed explicitly (see below)
unity command build --profileName "Windows - Development" --outputPath "$OUT" --options '["Development"]' --confirm true --result-only
```

- The call returns `queued` and a `buildId`; poll `unity command build_status --result-only` until it reports
  that `buildId` with `"status": "completed"` (an IL2CPP build takes several minutes — poll from a background
  shell). An earlier build's `completed` status lingers, so match the id.
- **`--profileName` activates the profile but builds from `EditorUserBuildSettings`**, ignoring the profile's
  Development flag: without `--options '["Development"]'` Flow B produces a non-development player the
  Profiler cannot attach to. Confirm a development build by its `UnityPlayer.dll` being far larger than a
  production build's, or by the running player listening on a TCP port in the 55000s.
- Do not build from `Windows - Profiler` for GC or timing work: it includes deep-profiling support. GC.Alloc
  call stacks need only a development build. (The exception is `SKILL.md` Step 2's render-pass captures, which
  need that profile's `Checked` managed code variant.)

## 2. Settings for the run

Do not edit `settings.json` in the build. Pass what the run needs on the command line — `-mc-set` overrides
any `Settings` field for the session and never reaches the file (syntax and exit codes: `SKILL.md`
"Unattended runs"). A shorter run for a single phase:

```
-mc-set benchmarkGenerationSpeeds=200 -mc-set benchmarkLoadingSpeeds=50
```

The player otherwise inherits its settings file — `persistentDataPath/settings.json` when one exists, else
`<build>_Data/settings.json`, created with calibrated defaults on first launch.

## 3a. Flow A — unattended run

```powershell
$p = Start-Process -FilePath $exe -ArgumentList @('-force-d3d11','-mc-run','benchmark',
     '-mc-set','benchmarkGenerationSpeeds=200','-mc-set','benchmarkLoadingSpeeds=50','-mc-mute','-mc-quit') -PassThru
if ($p.WaitForExit(900000)) { "exit $($p.ExitCode)" } else { Stop-Process -Id $p.Id -Force; "killed" }
```

- **Always `-mc-mute`, always a timeout.** A player occasionally freezes after quitting (pre-existing, see
  `SKILL.md`); kill it on expiry.
- **`Start-Process` takes no `-LiteralPath`** (confirmed in Windows PowerShell 5.1), so do not swap it in for the
  `[IL2CPP]` folder. If `-FilePath` misbehaves, launch with `[System.Diagnostics.Process]::Start($psi)` from a
  `ProcessStartInfo` whose `FileName` is the path — it takes the path literally.
- Exit code `0` = report written. `Player.log` (in `persistentDataPath`) carries
  `[Launch] Run finished: report <path>, exit code <n>`, which names the report — or take the newest
  `BenchmarkRun_*.log` in `persistentDataPath/Benchmarks/`.
- **Per-frame and hitch data:** the report's "Frame Health (every frame)" tables give each phase's exact wall
  p50/p99/worst, CPU p50/p99, GC p99, collections, hitch frames and GPU p99 over **every** frame of the phase. A
  benchmark runs at the Frame tier at least; `-mc-set perfMonitorTier=Systems` adds per-slot times to the hitch
  records, and `=Capture` also streams every frame (every slot and counter) to
  `persistentDataPath/PerfLogs/PerfSession_*.csv` plus one `…_hitch<NNN>_frame<F>.csv` per hitch record (`python
  Tools/Python/check_perf_session_files.py [<folder>] [--columns N]` checks their structure). Just before
  the `Run finished` line, `[Launch] Performance monitor at run end:` prints the `/perf stats` + `/perf hitches`
  readouts over the run's last 2 048 frames — the only place each hitch's slot ranking appears without Capture. The
  next launch overwrites Player.log, so copy it after each run; `python Tools/Python/tabulate_tick_hitches.py <logs…>`
  turns a Systems run's `Tick`-led hitch records into one table of the tick's parts and counts.
- **Per-phase slots, counters, jobs and I/O:** `python Tools/Python/summarize_perf_session.py --report <BenchmarkRun_*.log>
  --session <PerfSession_*>` cuts a Capture run's session file by the frame range each Frame Health row prints and
  cross-checks every phase against that row (exit 1 on a mismatch).
- **Time to stable:** `-mc-run startup-new`, then `startup-existing`, with the same arguments; each writes a
  `StartupRun_*.log` and exits 0 only when every mark was reached. The benchmark's own launch is interrupted by its
  flight, so it never reports the stable mark.

## 3b. Flow B — Profiler capture of one phase

0. **No other player running**: `Get-Process 'Minecraft Clone' -ErrorAction SilentlyContinue` must return
   nothing, because `ConnectToPlayer` attaches to the first player it discovers. If one is running, stop and ask —
   it may be the user's own session; never close a player you did not start.
1. **Launch without waiting**: the same `Start-Process … -PassThru` as Flow A, keeping `$p`, but no
   `WaitForExit` yet. World load and the pipeline settle take several seconds, which is the window for steps 2–3.
2. **Connect the Editor Profiler** (Editor in Edit mode): `ProfilerCapture.ConnectToPlayer ["WindowsPlayer"]`,
   repeated every second or two until it reports `connected` — the player takes a few seconds to be discovered.
3. **Arm**: `ProfilerCapture.ArmAutoStop` with the phase marker — `Benchmark.Generation.<speed>mps`, e.g.
   `["Benchmark.Generation.200mps", 60, "ProfilerCaptures/<name>.data"]`. It clears frames, turns on GC.Alloc
   call stacks and records; its Editor hook saves 60 frames after the phase ends.
4. **Poll from a background shell** until `SAVED`:
   `ProfilerCapture.Poll ["", 0, ""]` every 5 s. Check the `SAVED` line carries no
   `WARNING: the window's start was overwritten`.
5. **Wait for the player**: it finishes the run, writes its report and exits (`-mc-quit`) — wait on `$p` with
   a timeout and kill it on expiry, exactly as Flow A does.

## 4. Read the result

1. `ProfilerQueries.Load` the saved `.data`, then
   `GcCallstacks [-1, -1, <top>, <depth>, "*", "Benchmark.Generation.<speed>mps", "<tsv path>"]`.
2. From the run's `BenchmarkRun_*.log`, phase section `Generation Pass / <speed> m/s`: `Frames sampled`,
   `Chunks populated: N generated`, and `Avg GC/frame` from the GC Allocations table.
3. **Checks before reading the ranking:**
   - the `GcCallstacks` window frame count **equals** `Frames sampled` — otherwise the capture missed part of
     the phase;
   - `no callstack` is ~0 % and GC.Alloc + "outside GC.Alloc" equals the thread totals;
   - coverage = GC.Alloc total ÷ (`Avg GC/frame` × `Frames sampled`) — about 1 or above is healthy (the heap
     delta records 0 on collection frames, so exact GC.Alloc can exceed it); well below 1 means allocation the
     Profiler cannot see.
4. Per generated chunk = bytes ÷ `N`. Compare like with like: the heap-delta figure (`Avg GC/frame` ×
   frames ÷ `N`) against earlier heap-delta figures; exact GC.Alloc bytes are a different measure.
5. `ProfilerQueries.Clear` when done.

## 5. Cleanup

Run this right after the last run, before docs or reviews.

- A CLI build leaves its target profile active — restore the one recorded in §1:
  `BuildProfile.SetActiveBuildProfile(AssetDatabase.LoadAssetAtPath<BuildProfile>("Assets/Settings/Build Profiles/<name>.asset"))`
  through `unity command eval` — or `BuildProfile.SetActiveBuildProfile(null)` when §1 recorded a null profile,
  which makes the platform profile active again.
- A build rewrites `Assets/Resources/Data/BuildStamp.asset` and may add `m_RuntimeSettings` entries to
  `Assets/UniversalRenderPipelineGlobalSettings.asset`: `git restore` both unless the build is a release.
- It also bakes today's date into `PlayerSettings.bundleVersion` (`ProjectSettings/ProjectSettings.asset`, via
  `GameVersionManager`). Keep that one: it lands alone, as `Updated: Version to "<YYYY-MM-DD> - PreAlpha"`.
- Delete the throwaway build folder (ask first if the user may want to play it). The `[IL2CPP]` suffix is a
  wildcard to PowerShell path cmdlets — use `-LiteralPath` there, or bash.

## 6. Reading a built player

- **Compile-time gating (`#if`, `[Conditional]`) can only be verified in a player.** The Editor defines
  `DEBUG`, `UNITY_ENABLE_CHECKS` and `UNITY_INCLUDE_INSTRUMENTATION` under every managed code variant, so a
  suite is green before and after a gating change by construction. Build two players that differ only in the
  gate and byte-search `<Product>_Data/il2cpp_data/Metadata/global-metadata.dat` for a literal inside the gated
  block (`s.encode("utf-8") in blob or s.encode("utf-16-le") in blob`), always with an **ungated control
  literal** present in both — without it an all-absent result is indistinguishable from a broken search.
  - Pick a literal in a method that really runs in a player: editor/test-only methods are stripped from both
    builds, so their absence is inconclusive.
  - `[Conditional]` removes call sites, not the method: a `[Conditional]` method on a MonoBehaviour can ship
    its body and strings in a Release player. If diagnostic text must not ship, use `#if`.
  - Confirm the build is the configuration you think: `<Data>/boot.config` carries a `player-connection-*`
    block only in a Development build, and `profiler-enable=1` in a profiler build.
- `<Data>/globalgamemanagers` holds the baked `bundleVersion`; `<Data>/app.info` holds company | product.
- **A build profile's Player Settings override is a full copy, not a diff.** Diff its
  `m_PlayerSettingsYaml` blob against live settings before trusting it (the copy does not follow later project
  changes; normalize the extra leading space after each `'|`). Never hand-write a partial fragment — every
  omitted field falls back to Unity defaults (`DefaultCompany`, version `1.0`). Flipping one existing line in
  place is safe; verify it as a 1-line `git diff`, then `ImportAsset(ForceUpdate)` and confirm the asset still
  loads as a `BuildProfile`. With the profile active, `PlayerSettings.Set…` calls write into its override.
