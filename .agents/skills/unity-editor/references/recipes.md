<objective>
End-to-end recipes for common Editor tasks in this project, plus the verified gotchas. Load this
when you need a complete pattern rather than a single command.
</objective>

<recipes>

<recipe name="check-project-compiles">

**"Does the project compile in Unity?"**

```bash
unity recompile --format json    # exit 0 clean · 6 compile errors · 7 no Editor
```

The result lists each error with `file`, `line`, `code`, `message`. It compiles every assembly the
running Editor has, including a `.cs` file created seconds ago that no `.csproj` lists yet, so
there is no stale-DLL or new-file false green. When no Editor is running, fall back to
`dotnet build` as `CLAUDE.md` describes.

**Then wait for the reload before using the new code.** `recompile` returns when compilation
ends; the domain reload that loads the result follows (~10 s here), and calls sent during it fail
with exit `12` (`EDITOR_NOT_RUNNING`):

```bash
unity recompile --format json && \
unity status --until-ready --timeout 120
```

Verified: a constant changed three times read back its new value each time through this gate.

**In Play mode the idle state is `"playing"`, never `"ready"`** — after `editor.playmode start`, wait for
`'"status": "playing"'` (it follows the play-entry domain reload). A loop that requires `"ready"` while
playing spins until its timeout.

</recipe>

<recipe name="run-long-menu-item">

**"Run Validate All / a long generator / anything over 30 s"**

```bash
unity command console clear
unity command editor invoke_menu --path "Minecraft Clone/Dev/Validate All" --detach --format json   # → jobId

# Then, as SEPARATE shell calls, repeat until the result arrives:
unity job wait <jobId> --timeout 90
#   exit 6 + "The job keeps running; reattach ..."  -> run it again
#   exit 0 + result                                  -> done

unity command console get --tail 400 --result-only    # read the summary
```

Keep each wait under the shell's own timeout (agent shells commonly default to 120 s). A single
unbounded `unity job wait` blocks for the whole job, ~3.5 minutes for `Validate All`. The exit `6`
of a bounded wait is not a failure when the message says the job keeps running; any other exit
`6` is a real one, and `unity job status <jobId>` shows the job's `state` if in doubt.
`unity job wait` takes no `--result-only` (`error: unknown option`, and no wait happens); leave it off.

How to read a suite's output is owned by run-validation-suite.

</recipe>

<recipe name="query-live-state">

**"Query a singleton or ScriptableObject"**

```bash
unity command script eval "var w = UnityEngine.Object.FindAnyObjectByType<World>(); return w == null ? \"no World\" : \"chunks=\" + w.worldData.Chunks.Count;" --result-only

unity command script eval "var g = UnityEditor.AssetDatabase.FindAssets(\"t:BlockDatabase\"); return UnityEditor.AssetDatabase.GUIDToAssetPath(g[0]);" --result-only
```

For anything longer than a few lines, write a `.cs` file under the scratchpad (or `Tools/UnityCli/`
if it is worth keeping) with a `public static` entry point and run it with `script run`.

</recipe>

<recipe name="undoable-edit">

**"Change the scene so the user can undo it"**

Prefer the `gameobjects` setters (one Undo step each). From `script eval`, open a named group first,
because `script eval` does not start one:

```bash
unity command script eval "UnityEditor.Undo.IncrementCurrentGroup(); UnityEditor.Undo.SetCurrentGroupName(\"Agent: add probe\"); var go = new UnityEngine.GameObject(\"Probe\"); UnityEditor.Undo.RegisterCreatedObjectUndo(go, \"Agent: add probe\"); return go.GetEntityId().ToString();" --result-only
```

</recipe>

<recipe name="capture-view">

**"See what the Scene/Game view shows"**

```bash
unity command capture scene_view --output-path "Assets/AgentCaptures~/view.png" --width 960 --height 540 --result-only
# then open the PNG with the Read tool; delete the folder when done
```

`capture game_view --source camera --camera "<name>"` renders one camera. Keep captures small:
the image costs context in proportion to its pixel count. `max-resolution` applies only to inline
images, never to a saved file.

</recipe>

<recipe name="playmode-world">

**"Load a world in Play mode, drive the player, edit blocks, and capture a frame"**

The `voxel-engine` command group (`Assets/Scripts/AgentHarness/`, editor-only `play` in
`Assets/Editor/AgentHarness/`) does it in typed one-call-per-action commands; `unity command voxel-engine <name>
--help` lists the flags. Every session world runs in `RuntimeMode.Agent`, which saves only under
`<persistentDataPath>/Agent_Saves/`, so the user's worlds are never opened writable, and it starts muted.

```bash
export MSYS_NO_PATHCONV=1      # Git Bash otherwise rewrites "/give ..." and "/k/..." arguments as paths
V="unity command voxel-engine"

$V play                        # the user's call — say so first. World scene open: it becomes the memory-mode
                               # session world 'agent-play'. MainMenu open: Play starts normally, then:
$V session_start --mode Memory --name probe --seed 4242       # or --mode Throwaway (saved, reloadable)
$V wait --until WorldReady --timeout 120                       # server-side wait: no shell polling loop
$V status                      # runtimeMode Agent, persistence, saveFolder, player, hotbar, hints[]
$V console --line "fly on"; $V console --line "time set noon"; $V console --line "time freeze"  # leading / optional
$V set_block --x 0 --y 74 --z 6 --block stone                  # Air breaks; names, never raw IDs
$V get_block --x 0 --y 74 --z 6
$V pose --x 0.5 --y 76.6 --z -2.5 --yaw 0 --pitch 8            # eye in VOXEL space
$V capture --width 480 --height 270                            # renders a frame after the call
$V move_to --x 20 --z 0 --sprint true                          # real movement; route dry-run first
$V input_hold --forward 1 --jump true --seconds 0.5            # timed holds; nothing stays pressed
$V interact --action Break --x 1 --y 70 --z 0                  # real PlayerInteraction break / place
$V session_end                                                 # save and quit to MainMenu; restores settings
$V world_list; $V clean_world --name probe                     # agent root only; --all clears it
unity command editor.playmode stop                             # when done
```

Verified 2026-11-20 end to end (safe play, memory and throwaway sessions, reload, clean, P1 movement).

- **Wait with `voxel-engine wait`, not `editor status`.** In Play, `editor status` never reports `ready`, so a
  `wait-for ... "ready"` loop runs into its timeout. After `play` the domain reloads: retry the first `wait` until it
  answers.
- **World-changing commands refuse a non-session world** (`set_block`, `console`, `interact`): pass
  `--allow-persisted` only when the user asked for an edit to that world.
- **`move_to` does no pathfinding.** It dry-runs the straight line (start inside a block, a wall a hop cannot clear,
  water or lava, unloaded chunks) and refuses with `hints` unless `--force true`; fly (`fly on` + `input_hold --jump
  true`) or `noclip on` past terrain. Flying `move_to` is the reliable mover. A start inside a block is never
  forceable: a jump from inside a block can drop the player through the floor (`PLAYER_BUGS.md` §05).
- **Memory mode still writes `level.dat` on `session_end`** (Save & Quit saves whatever persistence says), which is
  why session worlds live in `Agent_Saves`; `clean_world` removes them.
- **A failing command's text is on stderr.** With `--result-only` a refusal prints `Unknown error`; drop it (or read
  stderr) to see the reason and the `hints`.
- **Pass Windows paths** to `capture --output-path` (`cygpath -w`); the default writes `Assets/AgentCaptures~/`.
- **`PerfStore.SumCounter` holds only the last 2048 frames** — ≈ 7 s at Editor frame rates. Read counters within a
  few seconds of the activity, or the window has rolled past it and every sum reads 0.
- **A keep-in-memory world never loads, unloads or saves chunks**, so disk I/O counters, save paths and reloads read
  0 there: a run that must touch disk uses `--mode Throwaway`.
- **A capture shows the previous state for one frame, and new blocks later still.** `World.Update()` pushes the
  global shader uniforms, so `capture` renders a frame after the call; check `GlobalLightLevel` (e.g. via `status`
  after `time set`) before trusting a lighting frame. Blocks from `set_block` or `interact` remesh asynchronously:
  `wait --until Frames --frames 10` before capturing them.
- **Pose in voxel space, always.** The floating origin is not stable across a reload, so a Unity-space position
  saved from an earlier session can land inside terrain; `pose` and `look_at` convert through `WorldOrigin`.
- **A reload test edits a block first.** Edited chunks are saved on unload and loaded on return; untouched generated
  chunks may simply regenerate, which proves nothing about the save path.
- **Captures include the HUD** (the hotbar draws into the camera).
- **No worldgen path places lava** (only the block, fluid and credits assets name it), so a lava scene
  needs a fixture. Recorded 2026-08, not re-run since: an open-topped stone box of `BlockIDs.Lava` sources
  is stable, and `/noclip` holds an eye under its surface (lava's buoyancy floats the player out).
- **Keep the rig as a script** (fixed seed + placements + pose + `time freeze`), not as a saved world: a later
  session re-runs it and gets a comparable frame.

</recipe>

<recipe name="player-agent">

**"Drive a built Development player with the same `voxel-engine` commands"**

A Development player carries the Unity CLI runtime server (the Pipeline's dev toggle is on, `autoStart` off) and
starts it only for `-mc-run agent`, which also mutes and stays on the main menu. Building the player is the user's
call (`player-runner`); launching and driving it go through `run_player.py --no-report`.

```bash
export MSYS_NO_PATHCONV=1
B="<build dir>"                       # Windows path, e.g. from `cd "$B" && pwd -W`
python Tools/Python/run_player.py --exe "$B/Minecraft Clone.exe" --tag agent --timeout 10 --no-report \
  -- -mc-run agent                    # run in the background; it returns when the player quits
V="unity command --runtime-path $B voxel-engine"   # quote "$B" when it contains spaces
$V status                             # retry until the descriptor exists (a few seconds after launch)
$V session_start --mode Memory --name probe --seed 4242
$V wait --until WorldReady --timeout 120
$V set_block --x 0 --y 90 --z 0 --block stone
$V session_end; $V clean_world --name probe
$V quit                               # player only: logs 'Run finished … exit code 0' and exits; run_player exits 0
```

Verified 2026-11-21 against an IL2CPP Development build (status, session, block edit, capture, end, clean, quit), and
the refusal in a Master build (`-mc-run agent` → exit 1, no descriptor).

- **The descriptor is the game's, not the package's.** The package restricts `.unity-pipeline-runtime-port` through
  `whoami`/`icacls`, which an IL2CPP player cannot start, so it logs one `Descriptor … was not written` error and
  `AgentDescriptor` writes the same file without that restriction (`[Agent] Wrote the runtime descriptor …`).
- **`Unity.Pipeline` is preserved whole** (`Assets/link.xml`): Medium stripping otherwise drops the getters its
  descriptor is serialized from, and the file comes out without `pid`, which the CLI rejects as no instance.
- **Without `-mc-run agent` nothing listens**: no descriptor, no `Start HTTP server` line, and `--runtime-path` fails
  with exit 6. The method-reload receiver (signed pushes only) is present in every Development build regardless.
- **Agent worlds and captures live under the player's `persistentDataPath`** (`Agent_Saves/`, `AgentCaptures/`),
  which the Editor shares, so `world_list` in either sees the other's agent worlds.
- A harness-enabled build is for functional checks and low-precision probes, never a perf A/B leg.

</recipe>

<recipe name="serialized-field">

**"Read a `[SerializeField]` value from a scene object"**

1. `unity command scene get_hierarchy --result-only` → the object's `instanceId`.
2. `unity command component get_fields --target <instanceId> --component <Type> --result-only`
   (private serialized fields included; enums come back as names).

</recipe>

<recipe name="project-auditor">
The `Window/Analysis/Project Auditor` grid is not queryable from the CLI. Either parse a saved report or
re-run the audit from code.

- **Saved report** (`*.projectauditor`): two header lines (`PROJECT_AUDITOR_REPORT`, format version), then one
  JSON document — `m_Issues` (each with `descriptorId.m_AsString`, `category.m_String`, `severity`,
  `location.path`/`.line`, `properties[]`, where `[0]` is the assembly for Code issues),
  `m_DescriptorLibrary.m_SerializedDescriptors` (the rule definitions), `sessionInfo`, `moduleMetadata`.
  Reports run to megabytes: parse with Python, never read one whole.
- **Re-run** (a full audit takes ~54 s, so run it as a detached job):
  `var report = new Unity.ProjectAuditor.Editor.ProjectAuditor().Audit(new AnalysisParams(true) { Platform = BuildTarget.StandaloneWindows64 }, null);`
  then `report.FindByDescriptorId("UAL0013")` / `GetAllIssues()` / `Save(path)`.
- **It audits with `CodeOptimization.Release`,** so `#if UNITY_INCLUDE_INSTRUMENTATION` code is compiled out and
  its statics and allocations never appear — a "0 issues" claim is release-only. Build-size figures come from the
  last player build's report, and the audit has no call context and cannot see Burst jobs, so allocation counts
  are not a performance signal.
</recipe>

</recipes>

<gotchas>

1. **Relative save paths resolve from the project root.** Always pass the full
   `Assets/AgentCaptures~/<name>.png` (gitignored, never imported): `Temp/x.png` lands in the project's
   `Temp/`, and any other folder under `Assets/` is imported with a `.meta`.
2. **Project types need their namespace in `script eval`** — `Data.BlockIDs.Stone`. Global-namespace types
   (`World`, `Player`, `SaveSystem`) bind as-is: `script eval` injects no namespace, so the `global::` prefixes
   in old MCP-bridge snippets are unnecessary.
3. **Default timeout is 30 s.** `--timeout` raises it; long work uses `--detach`.
4. **A call during a domain reload fails fast** — retry it.
5. **A synchronous `wait_for` holds the whole command queue** — use `--async true`.
6. **`script eval` formats numbers with the Editor's locale** (`58033,8`). Key maps by scaled integers
   or format with `CultureInfo.InvariantCulture`.
7. **A parameter named `format` collides with the global `--format` flag** — omit it.
8. **Always pass the project path** (`UNITY_PROJECT_PATH` / `--project-path`); auto-detection
   costs ~1 s per call, and `unity shell` does not avoid it.
9. **`script run` ignores C# default parameter values** — pass every argument.
10. **The main thread can be busy** (a long menu item, a compile): calls queue behind it and time
    out at `--timeout`. `editor status` shows `compiling` / `domainReloadInProgress`.
11. **Obsolete APIs fail `script eval` compilation**, not just warn — e.g. `Object.GetInstanceID()` in
    Unity 6.6; use `GetEntityId()`. Check with the `unity-api` tools when unsure.
12. **Never call `Undo.PerformUndo()` blind.** If the edit before it failed, it reverts whatever
    group is on top, which may be the user's work. Check `Undo.GetCurrentGroupName()` equals the
    group you opened first.
13. **A modal dialog blocks every command** — each call times out at `--timeout`; nothing wedges.
    If the Editor stops answering, list its visible window titles (PowerShell `EnumWindows` for the
    Unity PID) to spot the dialog. Stop any shell still in `unity job wait` first, so follow-up
    work doesn't queue behind it. Only dismiss a dialog your own command raised; anything else
    is the user's call. `PlayerBuildInterface.CompilePlayerScripts` raises one when its output
    folder's parent does not exist, so create it first.
14. **Exit `7` with an Editor visibly open may be Safe Mode.** An Editor that *starts* with C#
    compile errors boots into Safe Mode, where no package loads, `com.unity.pipeline` included, so
    every `unity command` fails as if no Editor were running. Confirm with
    `unity pipeline list --format json` (`data.summary.instancesInSafeMode` > 0). Take the errors from
    Unity's own `error CS…` lines in `Logs/Editor.log`, not `dotnet build`: it cannot see a `.cs` file
    the `.csproj` does not list yet and reports green for it. Fix them, then have the user leave Safe
    Mode or restart the Editor.
15. **Play mode holds compiles and reloads.** `Editor/ProjectUtilities/PlayModeReloadGuard` locks assembly
    reloads from entering Play mode until exiting it, because the engine's runtime state does not survive a
    Play-mode reload. With a source change pending, `unity recompile` prints nothing and blocks until Play mode
    stops, then completes about 2 s later, and `editor status` reports `compiling` the whole time. Stop Play
    mode first (with the user's consent), or edit only after leaving it; with no change pending it returns
    `up_to_date` at once.

</gotchas>
