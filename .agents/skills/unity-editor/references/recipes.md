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
with a network error:

```bash
unity recompile --format json && \
until unity command editor_status --result-only | grep -q '"status": "ready"'; do sleep 1; done
```

Verified: a constant changed three times read back its new value each time through this gate.

**In Play mode the idle state is `"playing"`, never `"ready"`** — after `editor_play`, wait for
`'"status": "playing"'` (it follows the play-entry domain reload). A loop that requires `"ready"` while
playing spins until its timeout.

</recipe>

<recipe name="run-long-menu-item">

**"Run Validate All / a long generator / anything over 30 s"**

```bash
unity command clear_console
unity command menu --path "Minecraft Clone/Dev/Validate All" --detach --format json   # → jobId

# Then, as SEPARATE shell calls, repeat until the result arrives:
unity job wait <jobId> --timeout 90
#   exit 6 + "The job keeps running; reattach ..."  -> run it again
#   exit 0 + result                                  -> done

unity command console --tail 400 --result-only    # read the summary
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
unity command eval "var w = UnityEngine.Object.FindAnyObjectByType<World>(); return w == null ? \"no World\" : \"chunks=\" + w.worldData.Chunks.Count;" --result-only

unity command eval "var g = UnityEditor.AssetDatabase.FindAssets(\"t:BlockDatabase\"); return UnityEditor.AssetDatabase.GUIDToAssetPath(g[0]);" --result-only
```

For anything longer than a few lines, write a `.cs` file under the scratchpad (or `Tools/UnityCli/`
if it is worth keeping) with a `public static` entry point and run it with `run_script`.

</recipe>

<recipe name="undoable-edit">

**"Change the scene so the user can undo it"**

Prefer the `gameobjects` setters (one Undo step each). From `eval`, open a named group first,
because `eval` does not start one:

```bash
unity command eval "UnityEditor.Undo.IncrementCurrentGroup(); UnityEditor.Undo.SetCurrentGroupName(\"Agent: add probe\"); var go = new UnityEngine.GameObject(\"Probe\"); UnityEditor.Undo.RegisterCreatedObjectUndo(go, \"Agent: add probe\"); return go.GetEntityId().ToString();" --result-only
```

</recipe>

<recipe name="capture-view">

**"See what the Scene/Game view shows"**

```bash
unity command capture_scene_view --save_path "Assets/AgentCaptures~/view.png" --width 960 --height 540 --result-only
# then open the PNG with the Read tool; delete the folder when done
```

`capture_game_view --source camera --camera "<name>"` renders one camera. Keep captures small:
the image costs context in proportion to its pixel count. `max_resolution` applies only to inline
images, never to a saved file.

</recipe>

<recipe name="playmode-world">

**"Load a world in Play mode, set it up, and capture a frame"**

The entry points live in `Tools/UnityCli/PlayMode/WorldRig.cs` (`run_script`, never imported):

```bash
RIG() { unity command run_script --file Tools/UnityCli/PlayMode/WorldRig.cs \
          --entry "UnityCli.WorldRig.$1" --args "$2" --result-only; }

RIG Saves '[5]'                           # edit mode OK; current-version saves only
unity command editor_play                 # the user's call — say so first; wait for "playing"
RIG Launch '["Probe", 4242, true]'        # MainMenu only; true = fresh world at the current version
RIG Status '[]'                           # poll: chunks > 0, then cameraCell off (0, -999999, 0)
RIG Command '["/fly"]'                    # without it gravity drops a posed player within ~2 s
RIG Command '["/time set noon"]'; RIG Command '["/time freeze"]'
unity command eval "var w = UnityEngine.Object.FindAnyObjectByType<World>(); return w.PlaceBlockCommand(0, 74, 6, Data.BlockIDs.Stone).ToString();" --result-only
RIG Pose '[0.5, 76.6, -2.5, 0, 8]'        # eye in VOXEL space, yaw, pitch (+ looks down)
RIG Capture '["<scratchpad>/frame.png", 480, 270]'   # a LATER call than any state change
unity command editor_stop                 # when done
```

Verified 2026-10-09 end to end on a fresh seed-4242 world (noon and midnight frames of a placed wall).

- **State change and capture never share a call.** `World.Update()` pushes the global shader uniforms, so in
  the call that ran `/time set midnight` `GlobalLightLevel` still read `1`; the next call read `0.267`.
  `Status` and `Capture` both report it — check it before trusting a frame. Freshly placed blocks remesh
  asynchronously for the same reason.
- **Console commands need the live `ConsoleUI.Engine`.** `new CommandEngine()` has no commands registered and
  answers `Unknown command`, which reads like a missing feature.
- **Pose in voxel space, always.** The floating origin is not stable across a reload, so a literal Unity
  position from an earlier session can land inside terrain; `Pose` converts through `WorldOrigin` and returns
  the round-tripped cell to check.
- **`Launch` skips the menu's AOT migration**, so it only loads current-version saves (`Saves` filters to
  them); the save root comes from the `enableVolatileSaveData` setting, not from `WorldLaunchState`.
- **Captures include the HUD** (the hotbar draws into the camera).
- **No worldgen path places lava** (only the block, fluid and credits assets name it), so a lava scene
  needs a fixture. Recorded 2026-08, not re-run since: an open-topped stone box of `BlockIDs.Lava` sources
  is stable, and `/noclip` holds an eye under its surface (lava's buoyancy floats the player out).
- **Keep the rig as a script** (fixed seed + `IsNewGame` + placements + pose + `/time freeze`), not as a saved
  world: a later session re-runs it and gets a comparable frame.

</recipe>

<recipe name="serialized-field">

**"Read a `[SerializeField]` value from a scene object"**

1. `unity command get_scene_hierarchy --result-only` → the object's `instanceId`.
2. `unity command get_serialized_fields --target <instanceId> --component <Type> --result-only`
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

1. **Save paths are confined to `Assets/`.** `Temp/x.png` becomes `Assets/Temp/x.png` and is
   imported with a `.meta`. Only `Assets/AgentCaptures~/` (gitignored, never imported) is safe.
2. **Project types need their namespace in `eval`** — `Data.BlockIDs.Stone`. Global-namespace types
   (`World`, `Player`, `SaveSystem`) bind as-is: `eval` injects no namespace, so the `global::` prefixes
   in old MCP-bridge snippets are unnecessary.
3. **Default timeout is 30 s.** `--timeout` raises it; long work uses `--detach`.
4. **A call during a domain reload fails fast** — retry it.
5. **A synchronous `wait_for` holds the whole command queue** — use `--async true`.
6. **`eval` formats numbers with the Editor's locale** (`58033,8`). Key maps by scaled integers
   or format with `CultureInfo.InvariantCulture`.
7. **A parameter named `format` collides with the global `--format` flag** — omit it.
8. **Always pass the project path** (`UNITY_PROJECT_PATH` / `--project-path`); auto-detection
   costs ~1 s per call, and `unity shell` does not avoid it.
9. **`run_script` ignores C# default parameter values** — pass every argument.
10. **The main thread can be busy** (a long menu item, a compile): calls queue behind it and time
    out at `--timeout`. `editor_status` shows `compiling` / `domainReloadInProgress`.
11. **Obsolete APIs fail `eval` compilation**, not just warn — e.g. `Object.GetInstanceID()` in
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
    stops, then completes about 2 s later, and `editor_status` reports `compiling` the whole time. Stop Play
    mode first (with the user's consent), or edit only after leaving it; with no change pending it returns
    `up_to_date` at once.

</gotchas>
