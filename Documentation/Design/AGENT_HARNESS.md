# Agent Harness (AH-*) Design

**Version:** 1.2  
**Date:** 2026-11-20  
**Status:** In progress — `AH-0` (commands, safe session, server-side waits, suite) and `AH-1` (timed agent input, `move_to`, `interact`) are shipped and verified in Play mode 2026-11-20; `AH-2` (Development player serving through `-mc-run agent`) is shipped and verified in an IL2CPP Development build 2026-11-21; `AH-3`/`AH-4` (Master builds, menu/world management) are unscheduled and start only on a concrete need.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production) — agent tooling, Editor today

> The `voxel-engine` command group: typed, one-call-per-action Unity CLI commands an agent uses to
> drive the running game — start a world, wait for it, move and look, break and place blocks, run
> console lines, capture a frame, clean up. **It is a thin command layer over the official
> `com.unity.pipeline` package and existing engine APIs, not a new transport and not console
> commands.** Its load-bearing property is the safety model (§4): every harness world runs in its own
> `RuntimeMode.Agent` with its own save root, so a run can no longer write into the player's own
> worlds, and that is enforced in code rather than by a recipe.

**Audited:** 2026-11-20, at commit `f993eed6` (branch `feat/agent-harness`), on top of the
uncommitted harness working tree. Findings are from reading `Assets/Scripts/AgentHarness/`
(`AgentCommands`, `AgentWorldOps`, `AgentSession`, `AgentInput`, `AgentRoute`),
`Assets/Editor/AgentHarness/AgentPlayCommand.cs`, `RuntimeMode`, the agent save root in
`SaveSystem`, the `AgentInput` contributions in `InputManager`, the `Agent Harness` registration in
`ValidationSuiteRegistry` and `AgentHarnessValidationSuite`. The command surface, safe sessions, reload,
clean-up and `AH-1` movement were run end to end in Editor Play mode the same day through the Unity
CLI; `Validate All` passed 887 baselines across 33 suites (Agent Harness 7/7). The motivation
figures in §1 are estimates from a transcript mining of 2026-10-24 → 2026-11-20 (characters / 4).

**Relationship to other documents:**

- [`../Architecture/UNITY_CLI_EDITOR_BRIDGE.md`](../Architecture/UNITY_CLI_EDITOR_BRIDGE.md) — the
  transport this layer rides on (§2 calling convention, §7 build footprint); it carries the short
  pointer to the harness.
- [`UNITY_CLI_EXTENSIONS_ROADMAP.md`](UNITY_CLI_EXTENSIONS_ROADMAP.md) — `UC-7` (`--runtime`
  inspection of a Development player) is the bridge-side twin of `AH-2`.
- [`../Architecture/COMMAND_CONSOLE_SYSTEM.md`](../Architecture/COMMAND_CONSOLE_SYSTEM.md) — the
  in-game console; the harness reaches it through the `console` passthrough command (§4.4).
- [`../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
  — the save roots; `Agent_Saves/` is the newest of them.
- [`../Guides/PROJECT_STRUCTURE.md`](../Guides/PROJECT_STRUCTURE.md) — folder placement of the
  three `AgentHarness/` folders.

---

## 1. Goals & non-goals

**Why it exists.** Runtime driving is a small slice of agent work: 134 runtime-driving Bash calls in 14
sessions over four weeks, 2.9 % of those sessions' Bash calls, about 40k tokens of tool I/O (estimate).
Token savings alone would never repay a large build. The real costs were elsewhere:

1. **Data safety.** On 2026-11-17 an agent Play run wrote blocks into the player's own world. The guard
   was a recipe (back up and byte-patch `Assets/settings.json`, check a log line, restore); 8 runtime
   calls touched `settings.json`.
2. **Polling.** 84 of the 134 runtime calls (63 %) carried hand-rolled `sleep`/`until` loops.
3. **Re-invented probes.** Per-session scratch rigs guessed members, reflected into privates and mixed
   coordinate spaces.
4. **No control of a player build** beyond the four fixed `-mc-run` actions.

### Goals

1. **Typed, discoverable, one-call-per-action commands** with C# default flag values, `--help`, structured
   results and honest effect annotations (`ReadOnly` / `Idempotent` / `Additive` / `Destructive`).
2. **Safe by default, enforced in code**: a harness world can never be a persisted user world (§4).
3. **Server-side waits** replacing shell polling loops (`wait`, plus the waits built into `session_end`,
   `capture`, `input_hold`, `move_to`, `interact`).
4. **Real input paths**: movement and interaction go through `InputManager`, `Player` and
   `PlayerInteraction`, so the harness exercises what a player exercises.
5. **One facade** (`AgentWorldOps`, `AgentSession`) that the commands, the suite and later phases share.

### Non-goals (v1)

- **Player builds** (Development, IL2CPP Master) — planned as `AH-2`/`AH-3`, see §7.
- **Menu and world management beyond agent worlds** (create/load/delete of any world, settings screens,
  pause menu) — planned as `AH-4`, see §7.
- **Pathfinding.** `move_to` walks a straight line and refuses a blocked one with hints (§4.5).
- **Perf verdicts.** A harness-enabled build is for functional checks and low-precision perf probes; A/B
  verdicts stay with the dedicated benchmark (`perf-benchmark` skill), because the server thread and the
  per-frame dispatcher change the measured player.
- **Unattended runs.** Entering Play mode and building a player remain the user's call; the harness makes
  runs cheaper, not autonomous.

---

## 2. Current state (what the harness builds on)

| Area            | State                                                                                                                                                                                                                                                                                                                               |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Transport       | `com.unity.pipeline` 1.0.0 serves custom `[CliGroupCommand]` static methods: typed `[CliArg]` flags with C# defaults, `Task<ExitCode>` for commands that run across frames, a per-request timeout. The Editor serves them today (UNITY_CLI_EDITOR_BRIDGE §2).                                                                       |
| Player serving  | The package can serve players when the Runtime Pipeline toggles define `ENABLE_RUNTIME_PIPELINE_DEV` (development builds) or `ENABLE_RUNTIME_PIPELINE` (release builds, marked experimental by the package). This project defines `ENABLE_RUNTIME_PIPELINE_DEV` with `autoStart` off since `AH-2` (§7; UNITY_CLI_EDITOR_BRIDGE §7). |
| Command console | `Assets/Scripts/Commands/` exists only in the World scene (it cannot drive menus) and answers in prose lines. It carries no hidden/permission flag. It stays reachable through the `console` command.                                                                                                                               |
| Launch layer    | `Assets/Scripts/Launch/`: `-mc-run`/`-mc-set`/`-mc-mute`/`-mc-quit`. `LaunchSession.ApplySessionOverrides` is the in-memory, never-saved settings override the harness reuses; `dev.*` fields apply only in debug builds.                                                                                                           |
| Input seam      | `InputManager` already adds a `TouchControls` contribution to every gameplay value; the harness adds `AgentInput` to the same sums.                                                                                                                                                                                                 |
| Old rig         | `Tools/UnityCli/PlayMode/WorldRig.cs` (`script run`, every argument required). Replaced by this harness and deleted with `AH-2` (2026-11-21).                                                                                                                                                                                       |

---

## 3. Decision: how to give an agent typed control of the running game

The pivotal choice is the backbone the commands sit on.

### Option A — console commands with a hidden/internal flag (rejected)

- ✅ One `IConsoleCommand` per operation, the console already ships.
- ❌ **The console exists only in the World scene**, so it cannot start a world or drive a menu.
- ❌ Its output is prose, which is what the harness removes; confirmations are a second round trip.
- ❌ "Hidden" is cosmetic: the console already ships ungated in every build.

### Option B — an own RPC layer (HTTP, pipe or file queue) (rejected)

- ✅ Fully ours; works wherever we compile it.
- ❌ We would own the parser, the schema, the listener and the security model, when the official package
  already provides discovery, typed flags, cross-frame commands, loopback binding and a bearer token.
  A file queue stays only as the `AH-3` fallback if the release server fails its spike.

### Option C — grow `WorldRig` and recipes (rejected)

- ✅ Smallest change.
- ❌ Keeps every pain in §1: `script run` compiles per call, takes JSON-array arguments with no defaults,
  and the rig's launch path skipped state the menu's create path sets (`BorderRadius`,
  `SelectedWorldType`, the world-scene request stamp).

### Option D — typed CLI commands over an `AgentWorldOps` facade ✅ **CHOSEN**

Typed flags, defaults, `--help`, structured results and server-side waits come from the package; the
facade keeps the engine calls in one place, reusable by commands, suites and a later transport. No
console feature is lost: the `console` command runs any console line through the live `ConsoleUI.Engine`.

**Command group name: `voxel-engine`.** It names what is driven; a hyphen is allowed because the package
enforces only unique group + name. Command names are snake_case (`session_start`, `set_block`).

---

## 4. Architecture

```
 agent shell ── unity command voxel-engine <name> --flag value
                      │  (com.unity.pipeline, served by the Editor)
                      ▼
 AgentCommands  (Assets/Scripts/AgentHarness/AgentCommands.cs)      thin [CliGroupCommand] wrappers
   │   AgentPlayCommand (Assets/Editor/AgentHarness/)               editor-only `play`
   ├──▶ AgentSession   — session lifecycle, agent save root, persistence/mute overrides
   ├──▶ AgentWorldOps  — status, pose, block ops, console, capture, route check (voxel space only)
   │        └──▶ AgentRoute — pure straight-line route dry run
   └──▶ AgentInput     — timed holds + one-frame presses ──▶ InputManager sums ──▶ Player / PlayerInteraction
                                                                 │
                                       World · WorldLaunchState · SaveSystem · LaunchSession · ConsoleUI
```

Every command is a thin wrapper: it checks the safety gate where one applies, calls the facade, writes a
structured result and returns an `ExitCode`. A refusal logs its reason on the error stream and returns
`Failed` (`NotFound` for a missing target, `InvalidUsage` for a bad flag combination, `TimedOut` for a
wait that ran out). Coordinates are voxel space throughout; `AgentWorldOps` converts to Unity space only
at the boundary through `WorldOrigin`, and re-converts every call because the floating origin can shift
while the player walks.

### 4.1 Session lifecycle (`AgentSession`)

1. **Start** (`session_start`): only from the `MainMenu` scene in Play mode, with a valid world name. It
   applies the persistence mode, optionally mutes, sets the launch state (`CurrentMode = Agent`,
   `WorldName`, `Seed`, `IsNewGame`, `SelectedWorldType = Standard`, `BorderRadius = 0`), stamps the world
   scene request and loads `Scenes/World`. A new world replaces an earlier folder of the same name under
   the agent root; `--load true` loads the saved agent world instead.
2. **Run**: `IsSessionWorld` is true while a session is active and the launch mode is `Agent`. The
   world-changing commands consult it (§4.3).
3. **End** (`session_end`, or any return to the main menu): `session_end` finds the
   `PauseMenuController`, calls `SaveAndQuitToMainMenu` and waits for the menu scene. On every `MainMenu`
   scene load while a session is active, a `sceneLoaded` hook restores the keep-in-memory setting and the
   master volume and returns the launch mode to `Default`.
4. **Editor safe play** (`play`, editor-only): stores a one-shot `SessionState` request and enters Play. A
   `BeforeSceneLoad` hook (`AgentSafePlayHook`, a separate class so `AgentSession` keeps one reset hook)
   consumes it: if Play opened the **World** scene, that world becomes the memory-mode session world
   `agent-play` before `World.Start` reads the persistence setting. Play from the main menu starts
   normally and the agent follows with `session_start`.

### 4.2 Session modes

| Mode          | Persistence                                                                                                | Use                                              |
|---------------|------------------------------------------------------------------------------------------------------------|--------------------------------------------------|
| **Memory**    | `dev.keepChunksInMemory` forced on for the session; nothing but `level.dat` reaches disk (see below).      | Default; fast functional runs. Development only. |
| **Throwaway** | Saved under `<persistentDataPath>/Agent_Saves/<name>/`, reloadable with `--load true` until `clean_world`. | Disk-I/O work, save/reload, perf probes.         |

Memory mode refuses outside a debug build, because the `Dev` settings section is read only there.
A memory-mode world never loads, unloads or saves chunks, so disk-I/O counters, save paths and reloads read
0 in it; a run that must touch disk uses Throwaway.

### 4.3 Safety model

Every layer is enforced in code; none depends on an agent following a recipe.

| Layer                  | Rule                                                                                                                                                                                                  | Where                                                            |
|------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------------------------------------------------------------|
| Isolated save root     | `RuntimeMode.Agent` resolves every save path to `<persistentDataPath>/Agent_Saves/`, whatever the volatile flag. The user's `Saves/` and `Editor_Temp_Saves/` are never opened writable by a session. | `SaveSystem.GetSavePath`, `GetAgentSaveRoot`, `AgentSavesFolder` |
| Settings not edited    | Memory mode overrides `dev.keepChunksInMemory` in memory through `LaunchSession.ApplySessionOverrides`; `settings.json` is never edited, and the original value is restored when the main menu loads. | `AgentSession.TryApplyPersistence`, `OnSceneLoaded`              |
| Mutation gate          | `set_block`, `console` and `interact` refuse unless the world is a session world or `--allow-persisted true` is passed. With no loaded world they refuse even with the flag.                          | `AgentWorldOps.CheckMutationAllowed`                             |
| Name confinement       | A world name must be one plain folder directly under the agent root: no separators, no `.`/`..`, no invalid file-name characters. `clean_world` can therefore only delete a child of the agent root.  | `AgentSession.IsValidWorldName`, `TryCleanWorlds`                |
| Loaded world protected | `clean_world` refuses to delete the loaded session world.                                                                                                                                             | `AgentSession.TryCleanWorlds`                                    |
| Quiet by default       | A session mutes audio through `LaunchSession.MuteForSession`; `--muted false` opts out; the volume is restored when the session ends.                                                                 | `AgentSession.Mute`, `OnSceneLoaded`                             |
| Hold expiry            | An input hold lasts a span of game time and expires on its own; a scene unload, `input_release` and the domain reset release everything.                                                              | `AgentInput`                                                     |

Save & Quit writes `level.dat` even with persistence off, so a memory-mode session still leaves a small
folder behind; that is the reason session worlds live in the isolated root and `clean_world` exists. The
gate does not cover player-state commands (`pose`, `teleport`, `select_slot`, `input_hold`, `move_to`,
`look_at`), which change nothing that is saved beyond the session world's own player.

### 4.4 Commands

Run as `unity command voxel-engine <name> [--flag value]`; `--help` lists the flags. Under Git Bash export
`MSYS_NO_PATHCONV=1` (or omit the leading `/` of a console line), which the `console` command accepts. The
class declares 19 commands (`AgentCommands.CommandCount`); the editor-only `play` is the 20th.

| Command         | Effect      | What it does                                                                                       | Key flags (defaults)                                                                                                                               |
|-----------------|-------------|----------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------|
| `play`          | Additive    | Editor only. Enters Play mode safely (§4.1.4).                                                     | `--muted` (true)                                                                                                                                   |
| `status`        | ReadOnly    | Scene, runtime mode, session, world, effective persistence, save folder, player, hotbar, `hints`.  | —                                                                                                                                                  |
| `wait`          | ReadOnly    | Waits across frames, then reports status; `TimedOut` with the last status.                         | `--until` Frames \| WorldReady \| Menu (WorldReady), `--frames` (1), `--timeout` (60 s)                                                            |
| `session_start` | Destructive | From the main menu, launches an agent world (§4.1).                                                | `--mode` Memory \| Throwaway, `--name` (agent-world), `--seed` (0), `--load`, `--muted` (true)                                                     |
| `session_end`   | Additive    | Save and quit to the main menu, then waits for it.                                                 | `--timeout` (60 s)                                                                                                                                 |
| `world_list`    | ReadOnly    | Lists saved agent worlds (agent root only): name, seed, version, last played.                      | —                                                                                                                                                  |
| `clean_world`   | Destructive | Deletes agent worlds; exactly one of `--name` or `--all`.                                          | `--name`, `--all`                                                                                                                                  |
| `pose`          | Idempotent  | Places the camera eye at a voxel position with a yaw and pitch.                                    | `--x --y --z` (required), `--yaw` (0, faces +Z), `--pitch` (0, positive looks down)                                                                |
| `look_at`       | Idempotent  | Turns the player and camera toward a voxel-space point.                                            | `--x --y --z` (required)                                                                                                                           |
| `teleport`      | Idempotent  | Teleports through the world's arrival hold; follow with `wait --until WorldReady`.                 | `--x --z` (required), `--y` (-1 = land on the surface)                                                                                             |
| `get_block`     | ReadOnly    | Reads the block name, id and meta at a cell; `NotFound` when unloaded.                             | `--x --y --z` (required)                                                                                                                           |
| `set_block`     | Destructive | Force-places a block by name (`Air` breaks), the `/setblock` path. Gated.                          | `--x --y --z --block` (required), `--allow-persisted`                                                                                              |
| `select_slot`   | Idempotent  | Selects a hotbar slot (0-based).                                                                   | `--index` (required)                                                                                                                               |
| `console`       | Destructive | Runs a console line on the live console and returns its lines, failure and pending flags. Gated.   | `--line` (required, leading `/` optional), `--allow-persisted`                                                                                     |
| `capture`       | Additive    | Renders the main camera to a PNG one frame after the call, so it shows the latest state.           | `--output-path` (`Assets/AgentCaptures~/capture_<frame>.png`; a player: `<persistentDataPath>/AgentCaptures/`), `--width` (1280), `--height` (720) |
| `input_hold`    | Additive    | Holds movement and buttons for game-time seconds through `InputManager`, then reports.             | `--seconds` (0.5), `--forward`, `--strafe` (-1..1), `--jump`, `--crouch`, `--sprint`                                                               |
| `input_release` | Idempotent  | Releases every agent-held input at once.                                                           | —                                                                                                                                                  |
| `move_to`       | Additive    | Walks or flies to a voxel column with real movement, after a route dry run (§4.5).                 | `--x --z` (required), `--tolerance` (0.5), `--sprint`, `--timeout` (60 s), `--force`                                                               |
| `interact`      | Destructive | Looks at a cell and breaks it or places the held block through `PlayerInteraction`. Gated.         | `--action` Break \| Place, `--x --y --z` (required), `--allow-persisted`                                                                           |
| `quit`          | Destructive | Player only: logs `Run finished … exit code 0` and exits the player just after replying (§7 AH-2). | —                                                                                                                                                  |

`status` carries a `hints` array (what blocks the agent and the next command: on the main menu, still
loading, teleport hold, UI open, not a session world, player inside a block), so an agent rarely needs a
second call to find out why something refused. `capture` waits a frame before rendering because
`World.Update` pushes the lighting uniforms; a render in the calling frame shows the previous state.

### 4.5 Input and the route check

**Input seam.** `AgentInput` is a static input source added to the `InputManager` sums beside
`TouchControls`: `MoveInput`, `JumpPressed`/`JumpHeld`/`JumpValue`, `CrouchValue`, `AttackPressed` and
`UsePressed`. Sprint is set on the player directly. Holds are measured in **game time**, not frames (the
Editor ran about 300 fps, so frame counts vary too much): a hold starts on the next frame, lasts at least
one frame, and expires on its own. `AttackPressed`/`UsePressed` are one-frame presses. The player ignores
agent input while a menu is open or an automated mode runs, as it does real input. A synthetic Input
System device was rejected (§9) because it depends on window focus and is imprecise.

**`move_to`.** Before moving it dry-runs the straight horizontal line (`AgentRoute.Check`, sampled every
half block, pure and unit-tested) and refuses with `hints` unless `--force true`. The line is computed
relative to the player's feet cell (integer cell plus a small float offset), so it stays exact at any
distance from spawn; `pose` and `look_at` likewise split a voxel position into an exact integer cell and its
fraction before converting through `WorldOrigin`.

| Block              | Meaning                                                                                                                                                |
|--------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------|
| `StartInsideBlock` | The player's feet or head cell is solid. Never overridden by `--force`: an embedded jump can drop the player through the floor (`PLAYER_BUGS.md` §05). |
| `Wall`             | A step needs more than the one-block climb a hop clears; when flying, any solid cell on the line.                                                      |
| `Unloaded`         | The line crosses a chunk that is not loaded.                                                                                                           |
| `Fluid`            | Walking, the line enters or lands in water or lava.                                                                                                    |

Noclip skips the check. The walk yaws the player toward the target each iteration, holds forward with a
short lease, hops when no progress is made for 0.75 s, and ends on arrival (`tolerance`), timeout
(`TimedOut`) or cancellation, always releasing the input. There is **no pathfinding**: past terrain the
agent flies (`console --line "fly on"`, then `input_hold --jump true`) or uses `noclip on`; flying
`move_to` is the reliable mover.

### 4.6 Verification

- **Suite `Minecraft Clone/Dev/Validate Agent Harness`** (`Assets/Editor/Validation/AgentHarness/`,
  registered in `Validate All`; `ExpectedSuiteCount` 32 → 33). All ten scenarios are baselines:

| ID    | Pins                                                                                                                          |
|-------|-------------------------------------------------------------------------------------------------------------------------------|
| AH-01 | A walking route reports its first obstruction and the responsible cell.                                                       |
| AH-02 | A flying route keeps its altitude, so only a solid cell on it blocks.                                                         |
| AH-03 | `YawTowards`: 0 faces +Z, 90 faces +X.                                                                                        |
| AH-04 | A world name cannot address anything but a child of the agent root; `clean_world ..` deletes nothing.                         |
| AH-05 | Agent mode saves under the agent root whatever the volatile flag; listing a missing root is empty.                            |
| AH-06 | A world-changing command is refused without a loaded world, even with `--allow-persisted`.                                    |
| AH-07 | Every declared command is registered in the `voxel-engine` group (count floor).                                               |
| AH-08 | `clean_world` by name deletes exactly that agent world (a sibling survives) and refuses an unknown name.                      |
| AH-09 | The `Unity.Pipeline` members `AgentDescriptor` reflects into still exist (a package upgrade reds here, not in a player).      |
| AH-10 | `quit` refuses only in the Editor, and a player's default capture folder is under `persistentDataPath`, not its build folder. |

  The suite header records the mutations that red each baseline. The load-bearing one is AH-04. The suite
  is headless and covers the pure logic and the gates; the live game is verified by the Play smoke.
- **`Validate All`**: the combined summary block is the verdict (887 baselines across 33 suites at
  introduction, Agent Harness 7/7).
- **Play smoke** (the user's call to enter Play): the `playmode-world` recipe in the `unity-editor` skill
  is the runnable script — `play`, `session_start`, `wait --until WorldReady`, `status` (check
  `runtimeMode` Agent and the persistence flag), world edits, `capture`, `session_end`,
  `clean_world`, `editor.playmode stop`; afterwards the save-root listing is unchanged apart from the
  session folder the clean-up removes.

### 4.7 Limitations and gotchas

- **Editor and Development players only.** A Development player serves the commands under `-mc-run agent` (§7 AH-2);
  Production and Master players do not (`AH-3`). In the Editor, `editor status` never reports
  `ready` during Play: wait with `voxel-engine wait`. After `play` the domain reloads, so the first `wait`
  may need a retry.
- **A refused command's text is on stderr.** With `--result-only` it prints `Unknown error`; read stderr
  (or drop the flag) for the reason and the `hints`.
- **Agent worlds are not migrated.** Loading a saved agent world whose save version differs from
  `SaveSystem.CURRENT_VERSION` is refused; clean it and create a new one.
- **The console exists only in the World scene**, so `console` refuses elsewhere. The gate applies to
  every console line, including read-only ones.
- **Captures include the HUD** and are written under `Assets/AgentCaptures~/` by default (never imported).
- **Pass Windows paths** to `--output-path`; under Git Bash export `MSYS_NO_PATHCONV=1`.
- **No pathfinding** (§4.5). **No perf verdicts** from a harness-enabled build (§1).

---

## 5. Prerequisites & integration points

- **`RuntimeMode.Agent`** is a new enum value that is interactive like `Default` (not an automated mode)
  and has its own save root. Any code that switches on `RuntimeMode` for a save path goes through
  `SaveSystem.GetSavePath`, which already routes it.
- **`LaunchSession.ApplySessionOverrides`** is the single in-memory settings override; the harness adds
  no second path to settings.
- **`InputManager`** gained the `AgentInput` terms (§4.5). They are zero outside a hold.
- **Statics and domain reload.** `AgentSession` and `AgentInput` annotate their statics
  `[NoAutoStaticsCleanup]`, reset them in one `[RuntimeInitializeOnLoadMethod]` per class, and unhook
  their scene callbacks before re-hooking.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                   |
|-------------------------------------------------|------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Block reads and writes go through `World.TryGetVoxel` and `PlaceBlockCommand`; no per-voxel type is added. |
| Burst jobs 100 % Burst-compatible               | No file under `Jobs/` is touched; the harness is main-thread managed code.                                 |
| No GC / LINQ in hot paths                       | Commands run per request, not per frame; `AgentInput` reads are allocation-free statics.                   |
| Pooling conventions                             | Not applicable: no per-frame collections; status and hint arrays are per-call.                             |
| No BinaryFormatter/JSON for terrain             | No new on-disk format: the agent root reuses the existing region/`level.dat` format.                       |
| `BlockIDs` constants, no raw IDs                | Blocks are addressed by name, resolved through `CommandArgUtility.TryResolveBlockId`.                      |
| Coordinate spaces never mixed                   | Voxel space in every parameter; conversion only in `AgentWorldOps` through `WorldOrigin`.                  |
| Domain-reload statics rules                     | `[NoAutoStaticsCleanup]` + one `[RuntimeInitializeOnLoadMethod]` per class (§5).                           |

---

## 7. Phased implementation plan

| Phase                                     | Scope                                                                                                                            | Effort | Depends on | Status        |
|-------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------|:------:|------------|---------------|
| **AH-0 — Command surface + safe session** | Facade, `voxel-engine` commands, `RuntimeMode.Agent` + agent save root, session lifecycle, safe `play`, server-side waits, suite |   🟡   | —          | ✅ 2026-11-20 |
| **AH-1 — Smooth player control**          | `AgentInput` through the `InputManager` seam, `input_hold`/`input_release`, `move_to` with route dry run, `interact` break/place |   🟡   | AH-0       | ✅ 2026-11-20 |
| **AH-2 — Development player**             | Serve the commands from a Development player; runtime start switch; a launch action that stays on the main menu                  |   🟡   | AH-0       | ✅ 2026-11-21 |
| **AH-3 — IL2CPP Master**                  | Spike the release server (stripping, `[Preserve]`); file-queue fallback transport                                                |   🔴   | AH-2       | —             |
| **AH-4 — Menus and world management**     | Shared menu service behind the buttons and the commands; `menu`/`world` commands beyond agent worlds                             |   🟡   | AH-0       | —             |

`AH-0` and `AH-1` deliver standalone value in the Editor, where the pain is today; `AH-2` extends them to a
Development player. `AH-3` and `AH-4` start only when a concrete Master or menu need appears; each is its own session.

**Validation is built alongside, not after**: `AH-0` registered the `Validate Agent Harness` suite
(AH-01…AH-08: route logic, yaw, name confinement, save routing, mutation gate, command count floor, named
delete); later
phases add baselines for what is pure (a launch-action count, a menu service's state rules). The live
game, the player build and anything focus- or frame-timing-dependent stay verified by smoke runs.

### AH-2 — Development player (decided direction)

Enable the server **at runtime by a setting or launch flag** rather than at build time: the package is
compiled in with `autoStart=false`, and the game calls `RuntimePipelineBootstrap.Instance.StartServer()`
when the setting or flag asks for it (flag name decided in AH-2). A dedicated build profile carrying the
define is the accepted fallback. The player then answers `unity command --runtime-path <build dir>
voxel-engine …`. A `-mc-run`-style launch action keeps the player on the main menu, muted, waiting for
commands. Building the player is the user's call (`player-runner`), and transient pipeline assets must not
remain in `git status` afterwards. This phase also settles `UC-7`.

**As built (2026-11-21, verified in an IL2CPP Development build):**

- **Switch:** `-mc-run agent` (`AgentLaunchAction`, installer count 4 → 5, Launch Arguments B15) mutes, stays on the
  main menu and calls `AgentServer.TryStart`. No `Settings` field: nothing persisted can open the server. Without a
  compiled-in server the action fails the run (exit 1 with `-mc-quit`); in the Editor it does nothing.
- **Toggle scope:** the global **Enable in Development & Profiled Builds** toggle (`ENABLE_RUNTIME_PIPELINE_DEV` for
  every installed build target) with `autoStart` off in the tracked `ProjectSettings/Packages/com.unity.pipeline/RuntimePipelineConfig.json`.
  `AgentServer`'s `#if` mirrors the runtime asmdef constraint, checked by compiling player scripts for a Development
  (runtime present, start branch) and a Release (runtime absent, refusal branch) Windows player.
- **Descriptor:** the package restricts `.unity-pipeline-runtime-port` with `whoami`/`icacls`, which cannot start in
  an IL2CPP player, so it never writes the file. `AgentDescriptor` takes over the package's own descriptor (identity,
  endpoints, token, 15 s heartbeat) by reflection and writes it without the current-user restriction; the package logs
  its error once. AH-09 pins the reflected members.
- **Stripping roots** (`Assets/link.xml`): the `AgentHarness` namespace and anonymous types (commands, flags and
  results are reached by reflection), and `Unity.Pipeline` whole with `ignoreIfMissing` — Medium stripping otherwise
  drops the getters its descriptor serializes, leaving no `pid`. That root grows the Development build's output by
  about half (1.78 → 2.68 GB); a narrower root is §8 question 4.
- **Ending a run:** `voxel-engine quit` (player only) logs `Run finished … exit code 0` through `LaunchSession.EndRun`
  and exits after replying; `run_player.py --no-report` counts that as success.
- **Build folders:** a build leaves an empty `Assets/settings/Pipeline/` and its `.meta`, both gitignored.
- **Verified:** ON — status, Memory session, `wait`, `set_block`/`get_block`, `capture`, `session_end`, `world_list`,
  `clean_world`, `quit`, run exit 0; the descriptor's `lastHeartbeat` advances every 15 s and the file is gone after
  `quit`. OFF (`-mc-run startup-new -mc-quit`, same build) — no descriptor, `--runtime-path` refused (exit 6), no
  `Start HTTP server` line, run exit 0. Master (`Windows - Production`) — no `Unity.Pipeline` in the build, and
  `-mc-run agent` logs the refusal and ends the run with exit 1, writing no descriptor.
- **Cost:** every Development and Profiler build carries the runtime, its per-frame driver and the signed
  method-reload receiver even with the server off.

### AH-3 — IL2CPP Master (decided direction)

Spike the experimental release server first, **gated behind the same setting or flag as AH-2**: a
dedicated `Windows - Agent (Master)` build profile, `[Preserve]` on the command classes plus a `link.xml`
entry (Standalone managed stripping is Medium), build, `--runtime-path` smoke. If it fails, the fallback
transport is a file queue (`-mc-agent <inbox>`: one request file to one response file, a main-thread poll,
`JsonUtility` DTOs) behind a define present only in that profile, reusing `AgentWorldOps`. Master safety:
keep-in-memory is impossible in a Master build (the `Dev` settings are not read), so session worlds there
always use the isolated root. `script eval` is unavailable in Master per the package docs, which is why
typed commands matter most there. A harness-enabled Master build is not a perf-capture build.

### AH-4 — Menus and world management

Extract the menu actions into a shared service used by both the buttons and the commands
(`WorldSelectMenu` create/load with AOT migration/delete, `PauseMenuController` resume and save-and-quit,
settings open/close), then expose `menu …` and `world list|create|load|delete` commands, with delete limited
to session/agent roots unless `--allow-persisted`. Real button clicks stay `runtime simulate_pointer`.
Agent-world loading would then share the menu's migration path, closing the v1 limitation in §4.7.

### Extension roadmap (post-AH-4, in intended order)

| Version | Extension                                                                                    |
|---------|----------------------------------------------------------------------------------------------|
| **v2**  | Pathfinding for `move_to` — only if flying and noclip prove insufficient in real agent work. |
| **v3+** | ✅ 2026-11-21 — `Tools/UnityCli/PlayMode/WorldRig.cs` deleted with `AH-2`.                   |

---

## 8. Open questions

1. **A3 — build-profile defines vs the package assembly constraints.** The package says its toggle
   writes the `ENABLE_RUNTIME_PIPELINE*` defines project-wide for every installed build target; whether a
   per-profile define satisfies the runtime asmdef `defineConstraints` is untested. **AH-2 answer
   (2026-11-21):** moot for Development builds, which use the global toggle; still untested, and only relevant
   if `AH-3` wants a profile-scoped release define.
2. **A4 — the release server under IL2CPP Master.** Whether the experimental release Runtime Pipeline
   works with Medium stripping once the command classes carry `[Preserve]` is untested. Resolved by the
   `AH-3` spike; the file queue is the fallback.
3. **Runtime start switch.** **Answered in AH-2 (2026-11-21):** the launch action `-mc-run agent`, no `Settings`
   field and no separate flag; `AH-3` reuses it.
4. **Build size of the `Unity.Pipeline` stripping root.** `<assembly fullname="Unity.Pipeline" preserve="all"
   ignoreIfMissing="1" />` in `Assets/link.xml` grew the Windows IL2CPP Development build from 1 777 985 592 to
   2 679 007 473 bytes (build report `totalSizeBytes` of the 2026-11-21 builds just before and just after the
   entry was added; no other change between them). Production
   and Master builds are unaffected: they contain no `Unity.Pipeline`.
   - **Why it grew** — unverified: keeping every member keeps code the linker would otherwise drop, possibly
     including paths into the bundled Roslyn assemblies (`UnityPipeline.Microsoft.CodeAnalysis*`) that `Unity.Pipeline`
     references. Compare the `ManagedStripped/` assembly sizes of two builds before choosing a fix.
   - **What a narrower root must keep:** every type the server serializes through reflection. Known so far: the
     runtime descriptor model `Unity.Pipeline.Models.RuntimeInstanceDescriptor`, whose stripped getters left the
     descriptor without `pid`. The rest of the set (request and response models) is unknown, so try a namespace
     root (`Unity.Pipeline.Models`) first.
   - **Done when:** a Development build with the narrower root is measurably smaller and still passes the
     `player-agent` recipe's ON run (descriptor with `pid`, `status`, a Memory session, `set_block`, `capture`,
     `quit` with exit 0). Building the player is the user's call (`player-runner`).

---

## 9. Rejected alternatives

| Alternative                                                   | Why rejected                                                                                                                                                    | Date       |
|---------------------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------|------------|
| Console commands with an internal/hidden flag as the backbone | The console exists only in the World scene (cannot drive menus), answers in prose, and "hidden" is cosmetic in a build that ships the console ungated.          | 2026-11-20 |
| Own HTTP, pipe or file-queue server                           | The official package already provides discovery, typed flags, cross-frame commands, loopback binding and a token; a file queue stays only as the AH-3 fallback. | 2026-11-20 |
| Grow `WorldRig` and recipes only                              | Keeps compile-per-call evals, no defaults and a launch path that skips state the menu sets.                                                                     | 2026-11-20 |
| Synthetic Input System device for gameplay                    | Depends on window focus and is imprecise; the `InputManager` seam is deterministic per frame. `runtime simulate_key`/`simulate_pointer` stays for UI tests.     | 2026-11-20 |
| Unique world names under `Saves/` or `Editor_Temp_Saves/`     | Unsafe in a player, whose `Saves/` are real worlds; an isolated root is safe everywhere.                                                                        | 2026-11-20 |
| Perf captures with the harness compiled in                    | The server thread and per-frame dispatcher change the measured player; A/B verdicts stay with the dedicated benchmark.                                          | 2026-11-20 |

---

## Document History

* **v1.2** - §8 question 4: the build size of the `Unity.Pipeline` stripping root (2026-11-21).
* **v1.1** - `AH-2` shipped: `-mc-run agent`, global dev toggle with `autoStart` off, `AgentDescriptor`, stripping
  roots, `quit`, AH-09/AH-10; §8 A3 and the start switch answered; `WorldRig.cs` deleted (2026-11-21).
* **v1.0** - Initial design with `AH-0` and `AH-1` shipped and verified in Play mode (2026-11-20).

---

**Last Updated:** 2026-11-20  
**Next Review:** when `AH-3` starts (a concrete Master need), or on promotion to Architecture after the last phase
