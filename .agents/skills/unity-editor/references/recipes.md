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

How to read a suite's output is owned by run-validation-suite.

</recipe>

<recipe name="query-live-state">

**"Query a singleton or ScriptableObject"**

```bash
unity command eval "var w = UnityEngine.Object.FindFirstObjectByType<World>(); return w == null ? \"no World\" : \"chunks=\" + w.worldData.Chunks.Count;" --result-only

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

<recipe name="serialized-field">

**"Read a `[SerializeField]` value from a scene object"**

1. `unity command get_scene_hierarchy --result-only` → the object's `instanceId`.
2. `unity command get_serialized_fields --target <instanceId> --component <Type> --result-only`
   (private serialized fields included; enums come back as names).

</recipe>

</recipes>

<gotchas>

1. **Save paths are confined to `Assets/`.** `Temp/x.png` becomes `Assets/Temp/x.png` and is
   imported with a `.meta`. Only `Assets/AgentCaptures~/` (gitignored, never imported) is safe.
2. **Project types need their namespace in `eval`** — `Data.BlockIDs.Stone`.
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
