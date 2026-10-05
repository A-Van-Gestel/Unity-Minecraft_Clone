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
- **Per-frame and hitch data:** just before that line, `[Launch] Performance monitor at run end:` prints the
  `/perf stats` + `/perf hitches` readouts over the run's last 2 048 frames. Pick the depth with
  `-mc-set perfMonitorTier=Frame` (adds GPU/render-thread/present-wait time and hitch records) or `=Systems` (adds
  per-slot times and each hitch's top three slots); the default `Basic` gives wall/CPU and GC only. Until the
  benchmark report itself reads the store, this block is the only place a player exposes it. The next launch
  overwrites Player.log, so copy it after each run; `python Tools/Python/tabulate_tick_hitches.py <logs…>` turns a
  Systems run's `Tick`-led hitch records into one table of the tick's parts and counts.

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

- A CLI build leaves its target profile active — restore the previous one:
  `BuildProfile.SetActiveBuildProfile(AssetDatabase.LoadAssetAtPath<BuildProfile>("Assets/Settings/Build Profiles/<name>.asset"))`
  through `unity command eval`.
- A build rewrites `Assets/Resources/Data/BuildStamp.asset` and may add `m_RuntimeSettings` entries to
  `Assets/UniversalRenderPipelineGlobalSettings.asset`: `git restore` both unless the build is a release.
- It also bakes today's date into `PlayerSettings.bundleVersion` (`ProjectSettings/ProjectSettings.asset`, via
  `GameVersionManager`). Keep that one: it lands alone, as `Updated: Version to "<YYYY-MM-DD> - PreAlpha"`.
- Delete the throwaway build folder. The `[IL2CPP]` suffix is a wildcard to PowerShell path cmdlets — use
  `-LiteralPath` there, or bash.
