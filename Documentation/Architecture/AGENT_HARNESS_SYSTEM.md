# Agent Harness System

**Version:** 1.0  
**Date:** 2026-11-21  
**Status:** **Implemented (Stable)** — `AH-0`…`AH-2` shipped: the command surface and safe sessions, timed
agent input, and serving from a Development player.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production) — agent tooling, Editor and Development players

> The `voxel-engine` command group: typed, one-call-per-action Unity CLI commands an agent uses to drive
> the running game — start a world, wait for it, move and look, break and place blocks, run console
> lines, capture a frame, clean up. It is a thin command layer over the `com.unity.pipeline` package and
> existing engine APIs, not a new transport and not console commands. **Its load-bearing property is the
> safety model (§4): every harness world runs in `RuntimeMode.Agent`, whose saves resolve to an isolated
> root, so a run cannot write into a player's own worlds — enforced in code, not by a recipe.**

**Audited:** 2026-11-21, at commit `df782e86` (branch `feat/agent-harness`).
Every claim below was re-verified against current code this session, from the code rather than from the
Design doc: `Assets/Scripts/AgentHarness/` (`AgentCommands.cs`, `AgentSession.cs`, `AgentWorldOps.cs`,
`AgentRoute.cs`, `AgentInput.cs`, `AgentServer.cs`, `AgentDescriptor.cs`),
`Assets/Editor/AgentHarness/AgentPlayCommand.cs`,
`Assets/Editor/Validation/AgentHarness/AgentHarnessValidationSuite.cs`,
`Assets/Editor/Validation/Framework/ValidationSuiteRegistry.cs`, `Assets/Scripts/Launch/AgentLaunchAction.cs`,
`LaunchActionInstaller.cs`, `LaunchSession.cs`, `LaunchSettingsOverrides.cs`, `Assets/Scripts/SaveSystem.cs`,
`Assets/Scripts/Data/Enums/RuntimeMode.cs`, `WorldLaunchState.cs`, `InputManager.cs`, `Player.cs`,
`PlayerInteraction.cs`, `PauseMenuController.cs`, the persistence paths of `World.cs`, `SettingsManager.cs`,
`ModificationManager.cs` and `LightingStateManager.cs`, `Assets/link.xml`, the `ENABLE_RUNTIME_PIPELINE*`
defines in `ProjectSettings/ProjectSettings.asset`,
`ProjectSettings/Packages/com.unity.pipeline/RuntimePipelineConfig.json`, `Packages/manifest.json`,
`.gitignore` and `Tools/Python/run_player.py`. Nothing was run in the Editor or a player for this audit.

**Relationship to other documents:**

- [`UNITY_CLI_EDITOR_BRIDGE.md`](UNITY_CLI_EDITOR_BRIDGE.md) — the transport this layer rides on (§2
  custom commands, §7 build footprint).
- [`../Design/AGENT_HARNESS_ROADMAP.md`](../Design/AGENT_HARNESS_ROADMAP.md) — the harness's unbuilt
  work: `AH-3` (IL2CPP Master), `AH-4` (menus and world management), pathfinding and the open build-size
  question.
- [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) — the in-game console the `console` command
  reaches (§2).
- [`INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
  — the save roots; `Agent_Saves/` is one of them.
- [`../Guides/PROJECT_STRUCTURE.md`](../Guides/PROJECT_STRUCTURE.md) — folder placement of the three
  `AgentHarness/` folders.

---

## ID index

| ID                          | Scope                                                                                                                | Where it now lives                                 |
|-----------------------------|----------------------------------------------------------------------------------------------------------------------|----------------------------------------------------|
| **AH-0**                    | Facade, `voxel-engine` commands, `RuntimeMode.Agent` + agent save root, session lifecycle, safe `play`, waits, suite | §1–§4, §8 — ✅ 2026-11-20                          |
| **AH-1**                    | `AgentInput` through the `InputManager` seam, `input_hold`/`input_release`, `move_to` with route dry run, `interact` | §5 — ✅ 2026-11-20                                 |
| **AH-2**                    | Serving from a Development player: `-mc-run agent`, `AgentDescriptor`, stripping roots, `quit`                       | §6 — ✅ 2026-11-21                                 |
| **AH-3**                    | IL2CPP Master: release-server spike, file-queue fallback                                                             | Not built — `../Design/AGENT_HARNESS_ROADMAP.md`   |
| **AH-4**                    | Shared menu service; `menu` / `world` commands beyond agent worlds                                                   | Not built — `../Design/AGENT_HARNESS_ROADMAP.md`   |
| **AH-01**…**AH-13**         | `Validate Agent Harness` scenarios                                                                                   | §8.1                                               |
| **§8 Q1 (A3)**              | Build-profile defines vs the package assembly constraints                                                            | Open — `../Design/AGENT_HARNESS_ROADMAP.md` (AH-3) |
| **§8 Q2 (A4)**              | The release server under IL2CPP Master                                                                               | Open — `../Design/AGENT_HARNESS_ROADMAP.md` (AH-3) |
| **§8 Q3**                   | Runtime start switch                                                                                                 | Answered: `-mc-run agent`, §6.1                    |
| **§8 Q4**                   | Build size of the `Unity.Pipeline` stripping root                                                                    | Open — `../Design/AGENT_HARNESS_ROADMAP.md`        |
| **v2** (extension roadmap)  | Pathfinding for `move_to`                                                                                            | Not built — `../Design/AGENT_HARNESS_ROADMAP.md`   |
| **v3+** (extension roadmap) | Delete `Tools/UnityCli/PlayMode/WorldRig.cs`                                                                         | ✅ 2026-11-21 — deleted; the harness replaces it   |

The `§8 Qn` and `vN` labels are the design's own section-local names; they are carried here because
commit messages cite them (`§8 Q4`).

---

## 1. Shape

```
 agent shell ── unity command voxel-engine <name> --flag value
                      │  (com.unity.pipeline: the Editor, or a Development player started with -mc-run agent)
                      ▼
 AgentCommands  (Assets/Scripts/AgentHarness/AgentCommands.cs)      thin [CliGroupCommand] wrappers
   │   AgentPlayCommand (Assets/Editor/AgentHarness/)               editor-only `play`
   ├──▶ AgentSession   — session lifecycle, agent save root, persistence/mute overrides
   ├──▶ AgentWorldOps  — status, pose, block ops, console, capture, route check (voxel space only)
   │        └──▶ AgentRoute — pure straight-line route dry run
   ├──▶ AgentInput     — timed holds + one-frame presses ──▶ InputManager sums ──▶ Player / PlayerInteraction
   └──▶ AgentServer    — player only: starts the runtime server; AgentDescriptor publishes its descriptor
                                                                 │
                                       World · WorldLaunchState · SaveSystem · LaunchSession · ConsoleUI
```

Every command is a thin wrapper: it checks the mutation gate where one applies, calls the facade, writes a
structured result and returns an `ExitCode`. A refusal writes its reason with `cmd.Log.Error` (not into
the result) and returns `Failed`, or `NotFound` (`get_block` on an unloaded cell, `session_end` with no
pause menu), `InvalidUsage` (`clean_world` without exactly one of `--name`/`--all`) or `TimedOut` (`wait`,
`move_to`). Waits stop one second before the request's `--timeout` (`TIMEOUT_MARGIN_SECONDS`), so the
command answers before the request expires.

Coordinates are voxel space in every parameter. `AgentWorldOps` converts to Unity space only at the
boundary, through `WorldOrigin`, and re-converts on every call because the floating origin can shift while
the player walks. `pose` and `look_at` split a voxel position into its integer cell (converted exactly) and
the fraction inside it, so they stay exact at any distance from spawn.

The group is `voxel-engine` (`AgentCommands.Group`); command names are snake_case. `AgentCommands`
declares 19 commands (`AgentCommands.CommandCount`); the editor-only `play` in `AgentPlayCommand` is the
20th.

---

## 2. Commands

Run as `unity command voxel-engine <name> [--flag value]`. The `Effect` column is the command's
`CommandEffect` annotation.

| Command         | Effect      | What it does                                                                                                                   | Key flags (defaults)                                                                                                                                   |
|-----------------|-------------|--------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------|
| `play`          | Additive    | Editor only. Enters Play mode safely (§3.3).                                                                                   | `--muted` (true)                                                                                                                                       |
| `status`        | ReadOnly    | Scene, runtime mode, session, world, seed, effective persistence, save folder, chunks, player pose and flags, hotbar, `hints`. | —                                                                                                                                                      |
| `wait`          | ReadOnly    | Waits across frames, then reports status; `TimedOut` with the last status.                                                     | `--until` Frames \| WorldReady \| Menu (WorldReady), `--frames` (1), `--timeout` (60 s)                                                                |
| `session_start` | Destructive | From the main menu, launches an agent world (§3.1).                                                                            | `--mode` Memory \| Throwaway (Memory), `--name` (agent-world), `--seed` (0), `--load` (false), `--muted` (true)                                        |
| `session_end`   | Additive    | Save and quit to the main menu, then waits for it.                                                                             | `--timeout` (60 s)                                                                                                                                     |
| `world_list`    | ReadOnly    | Lists saved agent worlds (agent root only), newest first: name, seed, version, last played.                                    | —                                                                                                                                                      |
| `clean_world`   | Destructive | Deletes agent worlds; exactly one of `--name` or `--all`.                                                                      | `--name`, `--all`                                                                                                                                      |
| `pose`          | Idempotent  | Places the camera eye at a voxel position with a yaw and pitch.                                                                | `--x --y --z` (required), `--yaw` (0, faces +Z), `--pitch` (0, positive looks down)                                                                    |
| `look_at`       | Idempotent  | Turns the player and camera toward a voxel-space point.                                                                        | `--x --y --z` (required)                                                                                                                               |
| `teleport`      | Idempotent  | Teleports through the world's arrival hold (`World.TeleportPlayer`); follow with `wait --until WorldReady`.                    | `--x --z` (required), `--y` (-1 = land on the surface)                                                                                                 |
| `get_block`     | ReadOnly    | Reads the block name, id and meta at a cell; `NotFound` when outside the world or unloaded.                                    | `--x --y --z` (required)                                                                                                                               |
| `set_block`     | Destructive | Force-places a block by name (`Air` breaks) through `World.PlaceBlockCommand`, the `/setblock` path. Gated.                    | `--x --y --z --block` (required), `--allow-persisted`                                                                                                  |
| `select_slot`   | Idempotent  | Selects a hotbar slot (0-based).                                                                                               | `--index` (required)                                                                                                                                   |
| `console`       | Destructive | Runs a line on the live `ConsoleUI.Engine`; returns its lines and the failed and pending-confirmation flags. Gated.            | `--line` (required, leading `/` optional), `--allow-persisted`                                                                                         |
| `capture`       | Additive    | Renders the main camera to a PNG one frame after the call, so it shows the latest state.                                       | `--output-path` (Editor `Assets/AgentCaptures~/capture_<frame>.png`; player `<persistentDataPath>/AgentCaptures/`), `--width` (1280), `--height` (720) |
| `input_hold`    | Additive    | Holds movement and buttons for game-time seconds through `InputManager`, then reports.                                         | `--seconds` (0.5), `--forward`, `--strafe` (-1..1), `--jump`, `--crouch`, `--sprint`                                                                   |
| `input_release` | Idempotent  | Releases every agent-held input and sprint at once.                                                                            | —                                                                                                                                                      |
| `move_to`       | Additive    | Walks or flies to a voxel column with real movement, after a route dry run (§5.2).                                             | `--x --z` (required), `--tolerance` (0.5), `--sprint`, `--timeout` (60 s), `--force`                                                                   |
| `interact`      | Destructive | Looks at a cell's center and breaks it or places the held block through `PlayerInteraction`. Gated.                            | `--action` Break \| Place, `--x --y --z` (required), `--allow-persisted`                                                                               |
| `quit`          | Destructive | Player only: ends the run with exit code 0 just after replying (§6.4).                                                         | —                                                                                                                                                      |

**`hints`.** `status` carries a `hints` array naming what blocks the agent and the next command: on the
main menu, the world still loading, a teleport arrival hold, a menu or the console open, not a session
world, the player inside a solid block. Every command that answers with a status carries it too, so an
agent rarely needs a second call to find out why something refused.

**`capture`** waits one frame before rendering because `World.Update` pushes the lighting uniforms; a
render in the calling frame shows the previous state. It renders `Camera.main` into a temporary
`RenderTexture` and restores the camera's target afterwards.

**`console`** prefixes a missing leading `/`, because Git Bash rewrites a leading `/` as a path. The
console exists only in the World scene, so the command refuses elsewhere; the mutation gate applies to
every line, read-only ones included.

---

## 3. Sessions

### 3.1 Lifecycle (`AgentSession`)

1. **Start** (`session_start`) — only in Play mode, from the `MainMenu` scene, with a valid world name
   (§4) and arguments that pass `AgentSession.ValidateStart`. `--load true` needs a saved agent world of
   that name at `SaveSystem.CURRENT_VERSION`. A new world replaces an earlier folder of the same name under
   the agent root; it is deleted after the arguments pass and before any state changes, so a refused start
   keeps it and a failed delete is a clean refusal. The start then applies the persistence mode (§3.2),
   optionally mutes, sets the launch state (`CurrentMode = Agent`, `WorldName`, `Seed` — the saved seed when
   loading — `IsNewGame`, `SelectedWorldType = Standard`, `BorderRadius = 0`), stamps the world-scene
   request (`WorldLaunchState.MarkWorldSceneRequested`) and loads `Scenes/World`.
2. **Run** — `AgentSession.IsSessionWorld` is true while a session is active and the launch mode is
   `Agent`. The world-changing commands consult it (§4).
3. **End** — `session_end` finds the `PauseMenuController`, calls `SaveAndQuitToMainMenu` and waits for the
   menu scene. On every `MainMenu` scene load while a session is active, a `sceneLoaded` hook restores the
   keep-in-memory setting and the master volume, returns the launch mode to `Default` and clears the
   session, so any return to the main menu ends it.

### 3.2 Session modes

| Mode          | Persistence                                                                                                                                                                                          | Use                                               |
|---------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------------|
| **Memory**    | `dev.keepChunksInMemory` forced on for the session, so `Settings.EnablePersistence` is false: chunks never load from, unload to or save to disk.                                                     | Default; fast functional runs. Debug builds only. |
| **Throwaway** | Persistence on (a debug build with keep-in-memory set has it forced off for the session); saved under `<persistentDataPath>/Agent_Saves/<name>/`, reloadable with `--load true` until `clean_world`. | Disk-I/O work, save/reload, perf probes.          |

Memory mode is refused outside a debug build (`Debug.isDebugBuild`), because `EnablePersistence` reads the
`Dev` section only there and `dev.*` overrides are rejected outside it. It is also refused with `--load`:
with persistence off `World.Start` reads no saved data, so a load would regenerate the world from its seed.
A memory-mode world's disk-I/O counters, save paths and reloads therefore read 0; a run that must touch
disk uses Throwaway.

Save & Quit still runs `SaveSystem.SaveWorld` with persistence off, which writes `level.dat`,
`pending_mods.bin` and `pending_lighting.bin` to the world's save folder — under the agent root, which is
why a memory-mode session still leaves a small folder behind and `clean_world` exists.

### 3.3 Editor safe play (`play`)

`play` refuses while Play mode is on or entering and while scripts have compile errors (Unity would refuse
Play). Otherwise it stores a one-shot `SessionState` request and enters Play. A `BeforeSceneLoad` hook
(`AgentSafePlayHook`, its own class so `AgentSession` keeps one reset hook) consumes the request: if Play
opened the **World** scene, that world becomes the memory-mode session world `agent-play` before the
scene's `Awake`, so `World.Start` reads the session. Play from the main menu starts normally and the agent
follows with `session_start`. An `EditorApplication.update` watch drops the request when Play is not
entered, so a later manual Play stays normal.

Play-mode entry reloads the domain (`ProjectSettings/EditorSettings.asset`, `m_EnterPlayModeOptions: 0`),
so a command sent straight after `play` can land in the reload; retry it, then `wait`.

---

## 4. Safety model

Every layer is enforced in code; none depends on an agent following a recipe.

| Layer                  | Rule                                                                                                                                                                                                                                                            | Where                                                            |
|------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------|
| Isolated save root     | `RuntimeMode.Agent` resolves every save path to `<persistentDataPath>/Agent_Saves/`, whatever the volatile flag. A session never resolves a path under `Saves/` or `Editor_Temp_Saves/`.                                                                        | `SaveSystem.GetSavePath`, `GetAgentSaveRoot`, `AgentSavesFolder` |
| Settings not edited    | Persistence is overridden in memory through `LaunchSession.ApplySessionOverrides`, which never writes to disk; the original value is restored when the main menu loads.                                                                                         | `AgentSession.TryApplyPersistence`, `OnSceneLoaded`              |
| Mutation gate          | `set_block`, `console` and `interact` refuse unless the world is a session world or `--allow-persisted true` is passed. With no loaded world they refuse even with the flag.                                                                                    | `AgentWorldOps.CheckMutationAllowed`                             |
| Name confinement       | A world name must be one plain folder directly under the agent root: no separators, no name made only of dots, no invalid file-name characters, and its full path's parent must be the root. `clean_world` can therefore only delete a child of the agent root. | `AgentSession.IsValidWorldName`, `TryCleanWorlds`                |
| Loaded world protected | While a session is active (from `session_start`, through the scene load), `clean_world` refuses `--all` and the session world's name, matched case-insensitively as Windows folders are.                                                                        | `AgentSession.IsCleanBlocked`, `TryCleanWorlds`                  |
| Quiet by default       | A session mutes audio through `LaunchSession.MuteForSession`; `--muted false` opts out; the volume is restored when the session ends.                                                                                                                           | `AgentSession.Mute`, `OnSceneLoaded`                             |
| Hold expiry            | An input hold lasts a span of game time and expires on its own; a scene unload, `input_release` and the domain reset release everything.                                                                                                                        | `AgentInput`                                                     |

The gate does not cover player-state commands (`pose`, `look_at`, `teleport`, `select_slot`,
`input_hold`, `move_to`), which change nothing saved beyond the session world's own player.

`RuntimeMode.Agent` is interactive like `Default`: it is not one of `WorldLaunchState.IsAutomatedMode`'s
modes, so the player, block interaction and the toolbar run as in normal play.

---

## 5. Input and the route check

### 5.1 Input seam (`AgentInput`)

`AgentInput` is a static input source combined into the `InputManager` values beside `TouchControls`:
added to `MoveInput`, OR-ed into `JumpPressed`/`JumpHeld`/`AttackPressed`/`UsePressed`, and max-ed into
`JumpValue`/`CrouchValue`. Sprint is set on the player directly (`Player.IsSprinting`) and cleared when the
hold ends.

Holds are measured in **game time**, not frames, because frame rate varies too much for frame counts. A
hold starts on the next frame, lasts at least `Time.maximumDeltaTime` so it covers its first frame, clamps
movement to length 1, and expires on its own. `AttackPressed`/`UsePressed` are one-frame presses on the
next frame. The player ignores agent input as it ignores real input: `Player.Update` skips input while
`World.InUI` or an automated mode runs, and `PlayerInteraction` returns early on the same conditions.

`interact` turns toward the cell's center, waits a frame (the interaction probe reads the camera in its
own `Update`), presses, waits until the press frame has passed and reports the block now at the cell.

### 5.2 `move_to` and the route check (`AgentRoute`)

Before moving, `move_to` dry-runs the straight horizontal line (`AgentRoute.Check`, sampled every half
block, pure and covered by AH-01/AH-02/AH-13) and refuses with `hints` unless `--force true`. Walking may
climb one block per step and drop up to 64; flying keeps its altitude. The line is computed relative to the
player's feet cell (integer cell plus a small float offset), so it stays exact at any distance from spawn.
A sample step that changes both x and z also checks the side cell the line crossed first; through an exact
corner it blocks only when both side cells block passage, since one corner is grazed and slid past.

| Block              | Meaning                                                                                                                                                          |
|--------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `StartInsideBlock` | The player's feet or head cell is solid. Never overridden by `--force`: a jump from inside a block can drop the player through the floor (`PLAYER_BUGS.md` §05). |
| `Wall`             | Walking, a step needs more than the one-block climb a hop clears; flying, any solid cell on the line.                                                            |
| `Unloaded`         | The line crosses a cell whose chunk is not loaded.                                                                                                               |
| `Fluid`            | Walking, the line enters or lands in water or lava.                                                                                                              |

Noclip skips the check. The walk yaws the player toward the target every iteration, holds forward with a
0.25 s lease, hops when it makes less than 0.05 blocks of progress in 0.75 s, and ends on arrival
(`--tolerance`), timeout (`TimedOut`) or cancellation, always releasing the input. There is **no
pathfinding**: past terrain the agent flies (`console --line "fly on"`, then `input_hold --jump true`) or
uses `noclip on`, as the `Wall` hints say.

---

## 6. Player builds

### 6.1 Serving and the start switch

The project defines `ENABLE_RUNTIME_PIPELINE_DEV` for the Standalone, Android and Server targets (the
package's **Enable in Development & Profiled Builds** toggle) with `autoStart` false in the tracked
`ProjectSettings/Packages/com.unity.pipeline/RuntimePipelineConfig.json`; the release define
`ENABLE_RUNTIME_PIPELINE` is not set. `AgentServer`'s `#if` mirrors the runtime assembly's define constraint
(`!UNITY_EDITOR && ENABLE_RUNTIME_PIPELINE_DEV && (UNITY_INCLUDE_INSTRUMENTATION || ENABLE_PROFILER)`), so
every reference to the runtime types stays inside it.

The switch is the launch action **`-mc-run agent`** (`AgentLaunchAction`, one of
`LaunchActionInstaller.InstalledActionCount` = 5). It mutes the session, stays on the main menu and calls
`AgentServer.TryStart`, which bootstraps the runtime driver, starts its server and publishes the descriptor
(§6.2). There is no `Settings` field: nothing persisted can open the server. In a build without the runtime
the action logs the refusal and fails the run (`LaunchSession.TryQuitAfterRun` with `ExitRunFailed`, which
exits only under `-mc-quit`); in the Editor, which serves the commands itself, it starts nothing. The player
then answers `unity command --runtime-path <build dir> voxel-engine …`.

### 6.2 The descriptor takeover (`AgentDescriptor`)

The package restricts its `.unity-pipeline-runtime-port` descriptor to the current user through
`whoami`/`icacls`, which cannot be started in an IL2CPP player, so the package never writes the file and no
client can connect. `AgentDescriptor.TryEnsurePublished` reflects into the package's live descriptor
(`PipelineDescriptorPublisher.s_File`, a `LiveDescriptorFile`), takes its path, identity, endpoint registry
and token getter, disposes the package's failing writer and writes the same descriptor itself without the
restriction — to a temporary file moved into place, so a client never reads a half-written one.

- A descriptor already on disk is kept only when its `pid` is this process; one left by a killed or older
  player is replaced.
- A 15 s heartbeat (the package's `DefaultHeartbeatInterval`) rewrites it, and stops if another process's
  descriptor has taken the path.
- On `Application.quitting` it waits up to 1 s for a heartbeat write in flight, then deletes the file if it
  is still its own.
- AH-09 pins every reflected member, so a package upgrade that renames one reds in the Editor suite rather
  than in a player.

### 6.3 Stripping roots (`Assets/link.xml`)

The runtime server finds the `[CliGroupCommand]` methods, binds their flags (enums included) and
serializes their results through reflection only, which Medium managed stripping cannot see. `link.xml`
therefore roots the `AgentHarness` namespace and anonymous types (`<>f__AnonymousType*`) in
`Assembly-CSharp`, and `Unity.Pipeline` whole with `ignoreIfMissing="1"` — the package serializes its own
models, its runtime descriptor included, by reflection, and every build without the runtime has no such
assembly. The size cost of that whole-assembly root is open (`§8 Q4`, in the roadmap).

### 6.4 Ending a run

`voxel-engine quit` refuses in the Editor (stop Play mode instead). In a player it replies, then after
500 ms calls `LaunchSession.EndRun`, which logs `Run finished: report (none), exit code 0` and exits the
process. `Tools/Python/run_player.py --no-report` counts a finished run with exit code 0 and no report as
success.

A build leaves an empty `Assets/settings/Pipeline/` folder and its `.meta`, both gitignored.

---

## 7. Integration points and constraint compliance

- **`RuntimeMode.Agent`** has its own save root; any code that resolves a save path through
  `SaveSystem.GetSavePath` routes it.
- **`LaunchSession.ApplySessionOverrides`** is the single in-memory settings override; the harness adds no
  second path to settings.
- **`InputManager`** carries the `AgentInput` terms (§5.1); they are zero outside a hold.
- **Statics and domain reload.** `AgentSession`, `AgentInput` and `AgentDescriptor` annotate their statics
  `[NoAutoStaticsCleanup]`, reset them in one `[RuntimeInitializeOnLoadMethod]` per class, and unhook their
  callbacks before re-hooking.

| Project constraint                              | How the harness complies                                                                                |
|-------------------------------------------------|---------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Block reads and writes go through `World.TryGetVoxel` and `World.PlaceBlockCommand`; no per-voxel type. |
| Burst jobs 100 % Burst-compatible               | The harness is main-thread managed code; nothing under `Jobs/` belongs to it.                           |
| No GC / LINQ in hot paths                       | Commands run per request, not per frame; `AgentInput` reads are allocation-free statics.                |
| Pooling conventions                             | Not applicable: status and hint arrays are per call, never per frame.                                   |
| No BinaryFormatter/JSON for terrain             | No new on-disk format: the agent root holds the existing region/`level.dat` format.                     |
| `BlockIDs` constants, no raw IDs                | Blocks are addressed by name, resolved through `CommandArgUtility.TryResolveBlockId`.                   |
| Coordinate spaces never mixed                   | Voxel space in every parameter; conversion only in `AgentWorldOps` through `WorldOrigin`.               |
| Domain-reload statics rules                     | `[NoAutoStaticsCleanup]` + one `[RuntimeInitializeOnLoadMethod]` per class.                             |

---

## 8. Verification

### 8.1 `Validate Agent Harness`

Menu `Minecraft Clone/Dev/Validate Agent Harness` (`Assets/Editor/Validation/AgentHarness/`), registered in
`ValidationSuiteRegistry` as `Agent Harness` and so part of `Validate All`. All thirteen scenarios are
baselines; the suite needs no scene, player or world, and covers the pure logic and the gates.

| ID    | Pins                                                                                                                          |
|-------|-------------------------------------------------------------------------------------------------------------------------------|
| AH-01 | A walking route reports its first obstruction and the responsible cell.                                                       |
| AH-02 | A flying route keeps its altitude, so only a solid cell on it blocks.                                                         |
| AH-03 | `YawTowards`: 0 faces +Z, 90 faces +X.                                                                                        |
| AH-04 | A world name cannot address anything but a child of the agent root; `clean_world ..` deletes nothing.                         |
| AH-05 | Agent mode saves under the agent root whatever the volatile flag; listing a missing root is empty.                            |
| AH-06 | A world-changing command is refused without a loaded world, even with `--allow-persisted`.                                    |
| AH-07 | The `[CliGroupCommand]` methods on `AgentCommands` equal `CommandCount`, all in the `voxel-engine` group.                     |
| AH-08 | `clean_world` by name deletes exactly that agent world (a sibling survives) and refuses an unknown name.                      |
| AH-09 | The `Unity.Pipeline` members `AgentDescriptor` reflects into still exist.                                                     |
| AH-10 | `quit` refuses only in the Editor, and a player's default capture folder is under `persistentDataPath`, not its build folder. |
| AH-11 | `clean_world` refuses the active session's world in any case, and `--all`, while a session is active.                         |
| AH-12 | `session_start` refuses Memory with `--load`, and Memory outside a debug build.                                               |
| AH-13 | A diagonal route checks the side cell it crosses (not the other), and an exact corner blocks only between two walls.          |

The suite header records the mutation that reds each baseline. The load-bearing one is AH-04: a name that
escapes the agent root would let a delete reach a player's saves. Mutating `TryCleanWorlds`' own name check
is deliberately never proven, because it would delete the save root's parent.

### 8.2 Smoke runs

The live game, the player build and anything focus- or frame-timing-dependent are verified by smoke runs,
not by the suite: the `playmode-world` recipe (Editor Play mode) and the `player-agent` recipe (a
Development player) in the `unity-editor` skill's `references/recipes.md`. Entering Play mode and building
a player stay explicit decisions per run; the harness makes runs cheaper, not autonomous.

---

## 9. Limitations

- **Editor and Development players only.** Production and Master players carry no runtime server, and
  `-mc-run agent` fails the run there (§6.1). A Master build is `AH-3`.
- **No pathfinding** (§5.2). Flying `move_to` and noclip are the movers past terrain.
- **No perf verdicts from a harness-enabled build.** It is for functional checks and low-precision probes;
  A/B verdicts stay with the dedicated benchmark (`perf-benchmark` skill).
- **Agent worlds are not migrated.** Loading a saved agent world whose save version differs from
  `SaveSystem.CURRENT_VERSION` is refused; clean it and create a new one. Sharing the menu's migration path
  is part of `AH-4`.
- **The console exists only in the World scene**, so `console` cannot drive menus, and the mutation gate
  covers read-only console lines too.
- **Menus and world management beyond agent worlds** (any world's create/load/delete, settings screens,
  the pause menu) have no commands; that is `AH-4`.
- **A Memory session writes metadata on Save & Quit** (§3.2), always under the agent root.
- **Two `com.unity.pipeline` player defects are worked around, not fixed**: the descriptor that cannot be
  written in an IL2CPP player (§6.2) and the descriptor getters Medium stripping removes (§6.3).
- **Every Development and Profiler build carries the package runtime**, even with the server off.

---

## 10. Rejected alternatives

| Alternative                                                   | Why rejected                                                                                                                                                                                                                            | Date       |
|---------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------|
| Console commands with an internal/hidden flag as the backbone | The console exists only in the World scene (cannot start a world or drive menus), answers in prose that needs a second round trip, and "hidden" is cosmetic in a build that ships the console ungated.                                  | 2026-11-20 |
| Own HTTP, pipe or file-queue server                           | The official package already provides discovery, typed flags, cross-frame commands, loopback binding and a token; owning a parser, schema, listener and security model buys nothing. A file queue survives only as the `AH-3` fallback. | 2026-11-20 |
| Grow `WorldRig` and recipes only                              | Keeps compile-per-call `script run` evals, JSON-array arguments with no defaults, and a launch path that skipped state the menu's create path sets (`BorderRadius`, `SelectedWorldType`, the world-scene request stamp).                | 2026-11-20 |
| Synthetic Input System device for gameplay                    | Depends on window focus and is imprecise; the `InputManager` seam is deterministic per frame. `runtime simulate_key`/`simulate_pointer` stays for UI tests.                                                                             | 2026-11-20 |
| Unique world names under `Saves/` or `Editor_Temp_Saves/`     | Unsafe in a player, whose `Saves/` are real worlds; an isolated root is safe everywhere.                                                                                                                                                | 2026-11-20 |
| Perf captures with the harness compiled in                    | The server thread and per-frame dispatcher change the measured player; A/B verdicts stay with the dedicated benchmark.                                                                                                                  | 2026-11-20 |
| A `Settings` field to open the player's server                | Anything persisted could then open the server; a launch action (`-mc-run agent`) opens it only for the run that asks.                                                                                                                   | 2026-11-21 |
| Counting harness holds in frames                              | Frame rate varies too much (the Editor ran about 300 fps), so a frame count is not a stable duration; holds use game time.                                                                                                              | 2026-11-20 |

---

## Document History

* **v1.0** - Promoted from `Design/AGENT_HARNESS.md` (v1.3) after `AH-2`, the last scheduled phase.
  Every claim re-verified against code at `df782e86`; the phase structure is merged into current-state
  sections, and the unbuilt `AH-3`/`AH-4`, pathfinding and the open `§8` questions moved to
  `Design/AGENT_HARNESS_ROADMAP.md`. Two design claims were corrected from code: a Memory session's Save &
  Quit also writes `pending_mods.bin` and `pending_lighting.bin`, not only `level.dat`, and AH-07 checks
  the command count for equality, not as a floor. The design was then deleted per `DG-*`;
  `git show df782e86:Documentation/Design/AGENT_HARNESS.md` returns it.

---

**Last Updated:** 2026-11-21  
**Next Review:** when `AH-3` or `AH-4` starts, or when `com.unity.pipeline` is upgraded (AH-09 and §6.2)
