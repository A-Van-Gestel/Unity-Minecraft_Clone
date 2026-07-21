# Command Console System Design

**Version:** 1.12
**Date:** 2026-07-21
**Status:** **Implemented (v1 arc + CMD-4 + CMD-5)** — the v1 arc (CMD-0..3), **CMD-4 relative `~` coordinates** (§8.2), and **CMD-5 tab autocomplete + PowerShell-style inline ghost suggestion** (§8.3) are all shipped and in-game confirmed. Suite **54** baselines; Validate All **287/287** across 11 suites (2026-07-21). Remaining §8 items (selectable/copyable output, chat, entity selectors, `/fill`) stay deliberate v2+ work.
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> An in-game command console (Minecraft-chat-style: `T` opens a left-anchored panel with
> scrollable history and a text input; commands like `/teleport @player X Y Z`). **The pivotal
> decision: a hard three-layer split — a pure-C# `CommandEngine` (parser, registry, selector
> resolution, confirmation flow, history; fully editor-suite-testable and drivable headless), a
> dumb TMP `ConsoleUI` view, and plugin command implementations on a registry** — so the
> minimal v1 that ships with the WS-4c teleport command is the permanent foundation, not a
> throwaway. Commands are **mandatory-`/`-prefixed from day one** (unprefixed input is rejected
> and reserved for future chat). Opening the console blocks player input and unlocks the cursor
> exactly like the inventory (which — verified — does *not* pause simulation; no
> `Time.timeScale` write exists in the project).

**Audited:** 2026-07-16, at commit `a6251fd` (branch `feat/world-scaling`).
Findings are from static review of `WorldUIManager` (UI-state centralization, `InUI` semantics),
`InputManager` + `GameInputActions.inputactions` (action-map split; `T` is unbound — free),
`Player`/`PlayerInteraction`/`TouchControls` (`World.InUI` consumers), `VoxelRigidbody` +
`World.CheckPhysicsCollision` (unloaded chunks collide as empty — the teleport fall-through
hazard), `DebugScreen` (TMP precedent), and the Placement validation suite pattern (stub-`World`
testing precedent). Decision menu closed 2026-07-16 (prefix, pause semantics, arrival policy,
v1 scope).

**Relationship to other documents:**

- [`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) — WS-4c ships this
  system's v1 with `/teleport` as the first command; teleport execution is a thin wrapper over
  its `WorldOrigin` helpers. This doc owns the console; that doc owns the origin machinery.
- [`WORLD_SCALING_IMPLEMENTATION.md`](WORLD_SCALING_IMPLEMENTATION.md) — grandparent roadmap
  (WS-4 phase table).
- [`../Guides/CODING_STYLE_GUIDE.md`](../Guides/CODING_STYLE_GUIDE.md) — naming/docstring rules
  for the new public engine surface.

---

## 1. Goals & non-goals

### Goals

1. **A reusable command backbone** — parser, registry, selectors, confirmation, history — that
   later commands, entities, and (if ever) chat/multiplayer extend without restructuring.
2. **Ship the WS-4c `/teleport`** on that backbone: flexible destination, out-of-world /
   out-of-border / far-terrain warnings with yes/no confirmation.
3. **Deterministic testability** — the engine is pure C# with no Unity-UI dependency, covered
   by its own `Validate Command Console` editor suite from v1.
4. **Inventory-parity UX** — `T` opens, input is captured, cursor unlocks, world keeps
   simulating; `Esc`/`Enter` behave as expected.

### Non-goals (v1)

- **Chat messages** — unprefixed input is *rejected with a hint* in v1; the mandatory-`/`
  decision (§3.1) reserves the unprefixed namespace so chat can land later without breaking
  command habits. **v2+**, see §8.
- **Entity selectors beyond `@player`** — the selector token shape and resolver interface ship
  in v1; `@entity-<id>` resolution lands with the entity system (**v2+**, §8).
- **Tab autocomplete** shipped in **CMD-5** (§8.3, 2026-07-19), including a PowerShell-style inline
  ghost suggestion; **selectable/copyable output** stays deferred to a later v2 item.
- **Permissions / cheats gating** — the execution context carries a source (§4.1) so
  permissions can attach later; v1 is a dev tool, always allowed.
- **True simulation pause** — rejected for the console (decided 2026-07-16): the job pipeline
  is `Update`-driven, so a real pause is new invariant surface over the chunk pipeline for
  modest benefit. A future explicit `/pause` feature may revisit it.

---

## 2. Current state (what exists today)

| Area              | State                                                                                                                                                                                                                          |
|-------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| UI state          | `WorldUIManager` centralizes UI state: `InUI = IsCreativeInventoryOpen \|\| IsPauseMenuOpen` (:149), cursor lock/visibility follow it. `Player`/`PlayerInteraction`/`TouchControls` gate on `World.InUI`.                      |
| "Pause" semantics | `InUI` blocks input only — **no `Time.timeScale` write exists in the project**; fluids, streaming, and day/night continue while the inventory is open. Console adopts the same semantics.                                      |
| Input             | `InputManager` wraps an `InputActionAsset` with Gameplay/UI maps. `T` is unbound in both maps (verified in `GameInputActions.inputactions`). Escape handling is a priority chain in `WorldUIManager.HandleEscape`.             |
| UI toolkit        | TMP + UGUI (`ScrollRect`) used throughout (`DebugScreen`, menus). No console/command code exists anywhere.                                                                                                                     |
| Teleport hazard   | `World.CheckPhysicsCollision` treats unloaded chunks as empty (`TryGetVoxel` miss → no hit), and `VoxelRigidbody`'s `IsWorldLoaded` gate covers only the initial load — a raw far teleport drops the player through the world. |
| Suite precedent   | The Placement suite drives `PlacementController` against a real stub `World` ("exercise the real subsystem"); the engine follows the same pattern.                                                                             |

---

## 3. Decisions

### 3.1 Command prefix — mandatory `/` ✅ **CHOSEN**

- **Option A — bare commands, `/` optional (rejected):** ✅ least typing in a console where
  everything is a command. ❌ **If chat ever lands, bare `teleport` must flip from command to
  chat message** — a habit-breaking semantic change on every input.
- **Option B — `\` prefix (rejected):** as sketched in the original request. ✅ visually
  distinct. ❌ **Awkward on many keyboard layouts and against genre convention** for no gain.
- **Option C — mandatory `/` from day one ✅ CHOSEN** (decided 2026-07-16): unprefixed input is
  rejected with a hint (`Commands start with '/' — try /help`). The unprefixed namespace is
  reserved for future chat; nothing ever changes meaning later.

### 3.2 Console-open semantics — inventory parity ✅ **CHOSEN**

Opening the console sets a new `WorldUIManager.IsConsoleOpen` state; `InUI` ORs it in. Input is
blocked, cursor unlocks, **simulation keeps running** — identical to the inventory (decided
2026-07-16; true pause rejected, see §1 non-goals).

### 3.3 Teleport arrival — hold until ground ready ✅ **CHOSEN**

- **Raw teleport (rejected):** ❌ falls through ungenerated terrain (§2 hazard) — "fell to
  Y = −1M" sessions.
- **Force `IsFlying` (rejected):** ❌ mutates player capability state as a side effect.
- **Hold until ready ✅ CHOSEN** (decided 2026-07-16): the command places the player at the
  destination and suspends gravity/movement until the destination column's chunk reports
  data + mesh ready (streaming does the loading), then releases. Also ships the **2-arg form**
  `/teleport X Z`, which resolves the surface height on arrival-release
  (`ChunkData.GetHighestVoxel` — chunk-local; there is no world-level overload) — the natural
  form when far terrain height is unknown.

Hold execution decisions (closed 2026-07-18):

- **Release condition = data + mesh** (confirmed): physics only needs the destination chunk's
  *data* — collision is voxel-based (`TryGetVoxel`; unloaded collides as empty), not
  mesh-collider-based — so the mesh wait is a UX choice (never drop the player into an
  invisible-but-collidable world), not a safety one. Note the startup "initial-load gate" is a
  one-shot coroutine, not a reusable predicate: the hold runs its **own** readiness poll
  (chunk populated + mesh applied), read-only over the pipeline — no new chunk flags, the
  pipeline stays teleport-blind.
- **Timeout fail-safe** (~10 s, named const): a destination chunk that never becomes ready
  (the CP-* audit documents async-load failures leaving chunks stuck) would otherwise suspend
  the player forever. On timeout the hold releases with a console warning line — the player
  might fall if data genuinely never loaded, but can `/teleport` back; no permanent soft-lock.
- **World-owned hold**: `World` owns chunk-readiness knowledge and the `Update` loop, so the
  hold state lives there (polled in `Update`); `VoxelRigidbody` only gains a held flag checked
  in `FixedUpdate` beside the existing `IsWorldLoaded` gate. (The CP refactor may extract it
  later.)

### 3.4 v1 quality-of-life scope ✅ **CHOSEN**

↑/↓ command-history recall and a `/help` command (registry-driven usage listing) ship in v1;
tab autocomplete and selectable output defer to v2 (decided 2026-07-16).

---

## 4. Architecture

```
┌──────────────────────────┐      ┌─────────────────────────────────────────────┐
│  ConsoleUI (Mono, TMP)   │ ───▶ │  CommandEngine (pure C#, no Unity-UI deps)  │
│  left panel, ScrollRect  │      │  Tokenizer → Registry → Selectors → Execute │
│  TMP_InputField, ↑/↓     │ ◀─── │  History buffer · PendingConfirmation       │
└─────────────┬────────────┘      └───────────────┬─────────────────────────────┘
              │ opens via                         │ IConsoleCommand (registry)
   WorldUIManager.IsConsoleOpen        ┌──────────┴──────────┐
   (third UI state; Esc closes)        │ TeleportCommand      │ ← ships in WS-4c
                                       │ HelpCommand          │
                                       └─────────────────────┘
```

### 4.1 `CommandEngine` (pure C#)

```csharp
/// <summary>Outcome of one submitted line: output lines, or a pending confirmation.</summary>
public readonly struct CommandResult { /* lines (Info/Warn/Error), PendingConfirmation? */ }

/// <summary>A registered console command. Implementations are stateless policy objects.</summary>
public interface IConsoleCommand
{
    string Name { get; }             // "teleport"
    string[] Aliases { get; }        // e.g. "tp"
    string Usage { get; }            // "/teleport [@target] X Y Z | /teleport [@target] X Z"
    CommandResult Execute(CommandContext ctx, CommandArgs args);
}
```

- **Tokenizer:** splits on whitespace with quoted-string support; classifies tokens as selector
  (`@<word>`), number (signed int/float), or word. **`~`-prefixed tokens** are tokenized as
  `CommandTokenType.Relative` and resolved at the coordinate-parse layer (CMD-4, §8.2 —
  implemented): coord-consuming commands add the token's offset to the player's coordinate on that
  axis; non-coord commands reject a `~` via their own argument checks.
- **`CommandRegistry`:** name/alias → `IConsoleCommand`; unknown command → error + `/help`
  hint. `HelpCommand` iterates the registry (self-documenting).
- **`TargetSelectorResolver`:** resolves selector tokens to targets. v1 resolves `@player`
  (the local player; also the default when the selector is omitted); anything else → "unknown
  target". `@entity-<id>` plugs into this resolver without touching the parser.
- **`CommandContext`:** the execution environment — the source (v1: local player; the seat for
  permissions/multiplayer), plus the world facade the command acts through. Commands never
  reach for scene singletons directly, which is what makes the suite's stub-`World` testing
  work. **Facade shape decided 2026-07-18:** concrete nullable `World` + `Player` references on
  the context (Placement-suite precedent — no interface), optional/defaulting to null so the
  parameterless construction path and every headless suite call site stay source-compatible;
  world-touching commands with a null world fail gracefully (`No world is loaded.`). CMD-2
  defines the facade with `/teleport`; CMD-3 extends it.
- **Confirmation flow (generic):** any command may return `PendingConfirmation` (prompt +
  continuation). The engine holds at most one; the next submitted line resolves it — `yes`/`y`
  executes the continuation, `no`/`n` cancels, anything else cancels with a notice and is then
  processed normally. Not teleport-specific.
- **History:** ring buffers for output lines and submitted commands (↑/↓ recall) live in the
  engine, so the UI is a stateless view and headless callers (suites, benchmark controllers,
  future scripting) get identical behavior via `engine.Execute(string)`.

### 4.2 `ConsoleUI` (MonoBehaviour)

Left-anchored translucent panel: `ScrollRect` history (autoscroll to newest, free scrollback)
over a `TMP_InputField`. Opened by a new `ToggleConsole` gameplay action bound to `T`
(verified unbound); closed by `Esc` (first in `WorldUIManager.HandleEscape`'s priority chain)
or after submit-on-empty. The input field is focused on open; **the opening `T` press must not
leak a "t" into the field** (activate the field on the frame after open, or clear on focus —
known Unity Input System + TMP interaction). `Enter` submits; ↑/↓ recall history; severity
colors via TMP rich text.

Threading/ownership: everything main-thread. The engine is plain managed code — no jobs, no
native containers, no pooled-type fields (pool-reset-safety not applicable).

**Resilience (UI_BUGS #04).** The whole hierarchy is runtime-built and owned by this view, so a
heavy chunk-churn / floating-origin rebase (e.g. a far-lands `/teleport`) can — through an
as-yet-unidentified engine/TMP-internal teardown — destroy a built object (observed: the input
field) out from under the live view. `RebuildMissingChildren()` therefore **self-heals at
whatever level died**: the whole `ConsolePanel` (rebuilt under the surviving canvas via
`BuildPanel()`), or an individual build-unit (history view, input field, or the ghost overlay
alone). It runs from `Open()` (before showing — `Open()` returns `bool` and `WorldUIManager`
skips the action-map swap if it fails, so a failed heal can't soft-lock input) **and** from
`Update()` while open (a child destroyed mid-session is rebuilt and refocused). Relatedly,
`LateUpdate` **no-ops while closed** — a line posted to a closed console (e.g. the teleport
arrival-hold outcome after an `Esc`-close mid-hold) must not drive a canvas rebuild on the
inactive panel subtree; `Open()` re-marks the dirty/scroll flags, so no posted line is lost.
Root cause is not yet pinned (no project code destroys the field); see
`Documentation/Bugs/UI_BUGS.md` #04.

### 4.3 `TeleportCommand`

`/teleport [@target] X Y Z` and `/teleport [@target] X Z` (surface-resolved Y); alias `tp`.
Coordinates are **absolute voxel world space**, **integer literals only in v1** (decided
2026-07-18: exact tier math via `CommandToken.Integer`; decimals rejected with a usage hint —
allowing them later breaks nothing, whereas float range checks get imprecise exactly at the
> 2²⁴ thresholds the tiers guard). Post-WS-4 the execution is a thin wrapper — a public
`World.TeleportPlayer(dest, resolveSurfaceY)`: re-anchor via the existing (private)
`ShiftOrigin` on the destination chunk, place the transform via `WorldOrigin.VoxelToUnity`
(the transient large-float translate is immediately overwritten, so no precision loss), begin
> the §3.3 arrival hold, and let streaming load the surroundings (`PlayerChunkCoord` changes →
`CheckViewDistance` fires on the next `Update` by itself). Validation tiers:

| Input condition                                      | Behavior                                                   |
|------------------------------------------------------|------------------------------------------------------------|
| Unparseable / wrong arity / unknown selector         | Error + usage string; nothing executes                     |
| Y outside `[0, ChunkHeight)`                         | ⚠️ Warn + confirm (proceed clamps? No — proceeds verbatim) |
| Outside the TF-14 border fence (when enabled)        | ⚠️ Warn + confirm (fence is a player clamp — see note)     |
| Beyond the noise-degradation radius (~±16.7M voxels) | ⚠️ Warn + confirm ("terrain artifacts expected")           |
| Beyond ±2³¹⁻ᵋ voxels (chunk-origin `×16` wrap)       | Error — permanently outside the addressable world          |

Note (border fence): `VoxelRigidbody.ClampToWorldBorder` re-clamps every `FixedUpdate`, so a
confirmed out-of-fence teleport lands and is immediately clamped back to the fence edge — the
warning text says so rather than pretending the destination sticks.

Tier evaluation order (decided 2026-07-18): hard errors first (arity / unknown selector /
wrap), then **all** applicable warnings are collected and presented in a **single**
`PendingConfirmation` listing every warning line (the engine holds at most one pending
confirmation, so stacking them one-per-warning would drop all but the last).

Suite note: the teleport matrix (§7) runs against a stub world (`ValidationReflection` recipe

+ a dummy player GameObject). **`WorldOrigin` is shared static state** — the fixture must
  snapshot and restore it on `Dispose` (ChunkMath-suite precedent), or a teleport baseline leaks
  a shifted origin into every subsequent suite.

---

## 5. Prerequisites & integration points

- ⚠️ **WS-4a/WS-4b** ([`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md))
  are the only blocking prerequisites for *`TeleportCommand`* — without the origin machinery a
  far teleport jitters exactly like far travel does today. The console engine + UI have no
  prerequisite and could land any time.
- **`WorldUIManager`** gains `IsConsoleOpen` (mirrors the inventory pattern; `InUI` ORs it in;
  Escape chain extended).
- **`InputManager` / `GameInputActions`** gain the `ToggleConsole` action (`T`, Gameplay map).
- **Reserved seats:** entity selectors (resolver), relative `~` coords (tokenizer), permissions
  (context source), autocomplete (registry metadata), chat (unprefixed namespace), multiplayer
  (source-agnostic engine), headless scripting (`engine.Execute`).

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                         |
|-------------------------------------------------|------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Untouched — no voxel data involvement.                                                                           |
| Burst jobs 100 % Burst-compatible               | No job code; the engine is main-thread managed code and never referenced from `Assets/Scripts/Jobs/`.            |
| No GC / LINQ in hot paths                       | Console code runs only on open/submit (not per-frame gameplay); history uses preallocated ring buffers; no LINQ. |
| Pooling conventions                             | No pooled types touched; UI elements are persistent, not per-frame instantiated.                                 |
| No BinaryFormatter/JSON for terrain             | Nothing persisted by this system (command history is session-only in v1).                                        |
| BlockIDs constants, no raw IDs                  | Not applicable in v1 (no block-referencing commands yet; a future `/setblock` must use `BlockIDs`).              |
| No magic numbers                                | History capacity, panel sizing, etc. as named constants per the style guide.                                     |

---

## 7. Phased implementation plan

Work items carry the **`CMD-`** prefix (verified unused in `Documentation/`).

| Phase                                         | Scope                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | Effort | Depends on     |
|-----------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|----------------|
| **CMD-0 — engine core** ✅                     | Tokenizer, registry, selector resolver (`@player`), confirmation flow, history buffers, `CommandContext`; `HelpCommand`; the validation suite. **Implemented 2026-07-18**: `Assets/Scripts/Commands/` (namespace `Commands`, pure C# — zero Unity usings) + `Validate Command Console` suite (20 baselines, registry suite #10)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |   🟡   | —              |
| **CMD-1 — console UI** ✅                      | Panel + ScrollRect + input field, `IsConsoleOpen` state, `ToggleConsole` action, Esc chain, ↑/↓ recall, T-leak guard. **Implemented 2026-07-18**: runtime-code-built `UI.ConsoleUI` (own overlay canvas, no scene edits) + pure `UI.ConsoleTextFormatter` (suite-pinned severity colors + noparse guard, B21/B22); **the Gameplay action map is disabled while the console is open** (typing can't trigger hotbar/toggles), so Esc/↑/↓ arrive via new UI-map actions (`Cancel`/`HistoryUp`/`HistoryDown`) rather than the gameplay Escape chain. Raw-keyboard bypasses closed: benchmark trigger keys route through the gameplay-gated `InputManager.DebugKeyPressed`, and tripwire baseline B23 fails the suite on any `Keyboard.current` read outside `InputManager` (suite 23, B21–B23)                                              |   🟡   | CMD-0          |
| **CMD-2 — `/teleport`** ✅                     | §4.3 command incl. warning/confirm matrix, arrival hold, 2-arg surface form; defines the `CommandContext` world facade (§4.1). **Implemented 2026-07-18, in-game confirmed**: `TeleportCommand` (alias `tp`, zero Unity usings) + `World.TeleportPlayer` (reuses the WS-4b `ShiftOrigin`) + World-owned hold (release = destination `ChunkData.IsPopulated` && `Chunk.HasMeshApplied` [new pool-reset-safe flag], 10 s timeout fail-safe, `TeleportHoldEnded` → console "Arrived."/timeout warning via `CommandEngine.PostLine`) + `VoxelRigidbody.isTeleportHeld` FixedUpdate gate; suite 23→**31** (B24–B31 teleport matrix vs `CommandTeleportTestWorld`, WorldOrigin snapshot/restore, prove-red on B29); far-lands verification surfaced lighting **Bug 19** (pre-existing, logged in `LIGHTING_BUGS.md`)                          |   🟡   | CMD-1, WS-4a/b |
| **CMD-3 — command pack** ✅                    | The §8.1 pack (`/fill` stays out). **Implemented 2026-07-18, in-game confirmed**: 13 commands via `ConsoleCommandInstaller.RegisterAll` (shared production/suite registration list, count-floor B32) — Wave A `/seed` `/where` `/origin [force]` (new public `World.ForceOriginReanchor`), Wave B `/time set` `/set-world-border` (shrink-strand confirm) `/setspawn` `/spawn` (CMD-2 hold reuse) `/fly` `/noclip` `/speed` (keybind coupling replicated), Wave C `/give` (name→ID case-insensitive; `ItemStack.ID` is a **byte** → >255 guard) `/setblock` (new `World.PlaceBlockCommand` owns ForcePlace + `Vector3Int`, keeping `Commands` UnityEngine-free; placed-vs-queued report) `/chunk info`. Shared `CommandArgUtility`; suite 31→**43** (B32–B43, prove-red ×2: B39 coupling, B41 case-insensitivity); Validate All 266/266 |   🟡   | CMD-2 ✅        |
| **CMD-4 — relative `~` coords** ✅             | Relative coordinates (`~`, `~N`, `~-N`) on the coord-consuming commands (`/teleport`, `/setblock`). **Implemented 2026-07-19, in-game confirmed** (§8.2): integer offsets only (v1 coord policy), player base via new `World.TryGetPlayerVoxelCell` (keeps `Commands` UnityEngine-free), global `~`-rejection gate removed. Suite 43→**47** (B9 flipped reserved→dispatches, B44–B47), prove-red exactly 5; Validate All 270/270.                                                                                                                                                                                                                                                                                                                                                                                                       |   🟢   | CMD-2 ✅        |
| **CMD-5 — tab autocomplete + inline ghost** ✅ | Registry-driven Tab completion **+ PowerShell-style inline gray ghost suggestion**. **Implemented + in-game confirmed 2026-07-19** (§8.3): command names + argument values via a new opt-in `IArgumentCompleter` (D1), multi-match → common prefix + candidate list (D2), primary names only (D3); Tab routes through a UI-map action (B23 held). Inline ghost via pure `CommandEngine.Suggest` (single-candidate only) + a gray TMP overlay; **Tab / RightArrow / End** accept. Suite 47→**52** (B48–B52), prove-red B48; Validate All 275/275.                                                                                                                                                                                                                                                                                        |   🟢   | CMD-0 ✅        |

CMD-0+1 deliver standalone value (a working console with `/help`); CMD-2 is the WS-4c payload —
the two roadmaps meet there: **WS-4c = CMD-2 + the v12→v13 player-position migration** from
[`WORLD_SCALING_FLOATING_ORIGIN.md`](WORLD_SCALING_FLOATING_ORIGIN.md) §7.

**Validation is built alongside, not after** — the `Validate Command Console` suite grows with
each phase:

- **CMD-0:** tokenizer matrix (quotes, selectors, signed numbers, `~` rejection, garbage);
  registry dispatch + unknown command + alias resolution; confirmation state machine
  (yes / no / unrelated-input-cancels); mandatory-`/` rejection hint; history ring behavior.
- **CMD-1:** UI stays in-game verified (panel/focus/scroll are not deterministic-suite
  material); the engine↔UI seam is covered by driving `engine.Execute` headless.
- **CMD-2:** teleport argument/warning matrix against a stub `World` (Placement-suite
  precedent): valid / arity / Y-warn / fence-warn / far-warn / wrap-error, and confirm-then-
  execute vs confirm-then-cancel. The arrival hold is verified in-game (WS-4b's far-travel
  gate doubles as its test).

## 8. Extension roadmap (post-CMD-2, in intended order)

| Version | Extension                                                                                                                                                                     |
|---------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | The **CMD-3 command pack** (§8.1) ✅; **CMD-4 relative `~` coordinates** (§8.2) ✅; **CMD-5 tab autocomplete + inline ghost** (§8.3) ✅; selectable/copyable output (unplanned). |
| **v3+** | Entity selectors (`@entity-<id>`, filters) with the entity system; chat on the unprefixed namespace; permissions on `CommandContext` — each gets a design pass when concrete. |

### 8.1 CMD-3 — command pack ✅ (implemented + in-game confirmed 2026-07-18)

Ordered by value ÷ cost; every 🟢 entry is a thin getter/setter over a system that already
exists. Effort assumes CMD-0..2 have landed. **Plan decisions closed 2026-07-18** (decision
menu; don't re-litigate): scope = all rows below except `/fill` (10 commands — `/spawn` is in,
since CMD-2 lands first and its arrival hold will exist); ordering = after CMD-2, per this
doc's phasing; facade = the §4.1 concrete nullable `World`+`Player` shape, CMD-3 extends what
CMD-2 defines; `/time` ships `set` only; `/setblock` uses `ReplacementRule.ForcePlace`.
Execution plan in §8.1.1.

| Command                              | Backing system (exists today)                                  | Effort | Note                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
|--------------------------------------|----------------------------------------------------------------|:------:|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `/where`                             | transform + `WorldOrigin` + region codec                       |   🟢   | Prints voxel pos, chunk, region file, origin chunk. The best WS-4 debugging aid after `/teleport`; pairs with the far-coordinates soak (WS-4 doc §8.2.4)                                                                                                                                                                                                                                                                                                  |
| `/origin` (dev)                      | `WorldOrigin`                                                  |   🟢   | Show the origin / force a shift — makes the WS-4b in-game gate scriptable instead of "fly 1024 units"                                                                                                                                                                                                                                                                                                                                                     |
| `/set-world-border <radius>` / `off` | TF-14 `BorderRadius` + `World.SetBorderRadius` + level.dat v12 |   🟢   | Everything exists; the command is a setter + save. Warn+confirm when shrinking would strand the player outside (the fence re-clamps them inward)                                                                                                                                                                                                                                                                                                          |
| `/spawn`, `/setspawn`                | `WorldSpawnPoint` (CRP) + `SetSpawnPoint`                      |   🟢   | `/spawn` = teleport-to-CRP (reuses CMD-2's arrival hold); `/setspawn` writes the existing field                                                                                                                                                                                                                                                                                                                                                           |
| `/time set <0..1>`                   | `World.globalLightLevel` (persisted as `timeOfDay`)            |   🟢   | Also enables deterministic lighting screenshots/repros. `add` deferred until RF-1 ships a real clock — today the field is a static light level, so time arithmetic has no meaning                                                                                                                                                                                                                                                                         |
| `/seed`                              | `VoxelData.Seed`                                               |   🟢   | Trivial; handy once the seed-hygiene work (Bug 04) starts                                                                                                                                                                                                                                                                                                                                                                                                 |
| `/fly`, `/noclip`, `/speed <n>`      | `VoxelRigidbody` flags                                         |   🟢   | Already keybound (F1/F6); command form adds discoverability + an exact speed value                                                                                                                                                                                                                                                                                                                                                                        |
| `/give <block> [n]`                  | `BlockIDs` + toolbar/inventory                                 |   🟡   | Needs name→ID lookup (BlockDatabase names); MUST resolve via `BlockIDs`, never raw IDs                                                                                                                                                                                                                                                                                                                                                                    |
| `/setblock X Y Z <block>`            | `VoxelMod` + `World.AddModification`                           |   🟡   | Thin wrapper over the existing mod path; useful for validation repros. Uses `ReplacementRule.ForcePlace` (decided 2026-07-18 — the `Default` placement-tag gate would silently reject overwriting e.g. stone, defeating the repro purpose; UNBREAKABLE still refuses). Unloaded targets are SAFE: `ApplyModifications` routes them to `ModManager.AddPendingMod` (persistent, auto-applied on chunk load) — the command reports "queued", no guard needed |
| `/chunk info`                        | `ChunkData` state flags                                        |   🟡   | Dumps the current chunk's pipeline state (lighting flags, mesh state, active voxels) — queryable anywhere; pairs with `chunk-lifecycle` debugging                                                                                                                                                                                                                                                                                                         |
| `/fill`                              | mass `VoxelMod`                                                |   🔴   | **Deliberately deferred** — unbounded mod volumes stress the apply path's per-frame budgets; needs its own design pass, don't sneak it in                                                                                                                                                                                                                                                                                                                 |

#### 8.1.1 CMD-3 execution plan (closed 2026-07-18) — **executed 2026-07-18**

All five steps below ran as planned in the CMD-2 follow-on session (suite gates green at every
wave, prove-red on B39/B41, Validate All 266/266); step 5's in-game half is pending. Two
mechanical deltas from execution: the pack is **13** commands (the table's row count; the
plan's "10" was loose row counting), and `/give` gained a >255-ID guard (`ItemStack.ID` is a
byte). Original plan, for the record:

Runs after CMD-2; each step ends in a verification gate and its own compilable commit.

1. **Facade + registration seam** — extend the §4.1 facade CMD-2 defined (add `Player` if
   CMD-2 didn't) and add a static `ConsoleCommandInstaller.RegisterAll(registry, context)` that
   `WorldUIManager` and the suite share, so headless runs register the identical pack.
   *Gate:* both builds + existing suite green (seam is non-breaking).
2. **Wave A — read-only:** `/seed`, `/where` (voxel pos / chunk / region via
   `RegionAddressCodec.ForVersion(...).ChunkVoxelPosToRegionAddress` / origin chunk), `/origin`
   show + `force` (needs a small public `World` wrapper — the re-anchor path is private).
   *Gate:* parse/arity/output baselines + the **`/help` count-floor baseline** (asserts the
   registered-command count — the false-green gate against silently dropped registrations).
3. **Wave B — setters + `/spawn`:** `/time set <0..1>`, `/set-world-border <r>|off`
   (shrink-strands-player warn + confirm via the §4.1 `PendingConfirmation` flow), `/setspawn`,
   `/spawn` (teleport-to-`WorldSpawnPoint` reusing CMD-2's execution + arrival hold), `/fly`,
   `/noclip`, `/speed <n>`. The fly↔noclip coupling lives at `Player.cs`'s toggle sites, NOT in
   the properties — the commands must replicate it (noclip⇒flying on; flying-off⇒noclip off).
   *Gate:* validation-tier + border-confirmation-matrix baselines; state-effect asserts against
   a slim stub world (the `PlacementTestWorld`/`ValidationReflection` recipe — setter asserts
   need no chunks/pool); capability commands verified in-game.
4. **Wave C — 🟡:** `/give` (name→ID via `BlockType.blockName`, case-insensitive), `/setblock`
   (ForcePlace; see table note), `/chunk info` (dump: `IsPopulated`, `IsLoading`,
   `NeedsInitialLighting`, `HasLightChangesToProcess`, `NeedsEdgeCheck`,
   `IsAwaitingMainThreadProcess`, active-voxel count + mesh-side state pinned from `Chunk` at
   implementation time). *Gate:* parse/lookup-failure baselines headless + a pending-mod-route
   baseline for `/setblock` on an unloaded chunk; effects verified in-game.
5. **Doc-sync:** flip the §8.1 rows to implemented; final `Validate All`.

Commands with a null world facade return the graceful `No world is loaded.` error
(baseline-able). Out of scope stays: `/fill` (own design pass), autocomplete/selectable output
(separate v2 items), permissions.

### 8.2 CMD-4 — relative `~` coordinates ✅ (implemented + in-game confirmed 2026-07-19)

Minecraft-style relative coordinates on the two coord-consuming commands. `~` = the player's
current voxel coordinate on that axis; `~N`/`~-N`/`~+N` = that coordinate plus a signed offset;
e.g. `/teleport ~ ~10 ~` rises 10 voxels in place. The tokenizer already classified `~`-prefixed
tokens as `CommandTokenType.Relative` (CMD-0); the only blocker was the pre-dispatch rejection gate.

**Shipped 2026-07-19** exactly as planned below (two commits: A1+A2, then A3+A4). Suite 43→**47**:
B9 flipped from "reserved-`~` rejected" to "the command runs and receives the `~` token"; B44–B47
cover 3-arg resolve + signed offsets, the 2-arg surface form + `~`-offset overflow, `~`-with-no-player
graceful error, and `/setblock` relative. Prove-red verified — re-inserting the gate reddened exactly
those 5 baselines. Validate All 270/270 across 10 suites. The `RelativeCoordsError` const was retired
(not repurposed); the no-player message lives on `CommandArgUtility.RelativeNeedsPlayerError`. Original
plan, for the record:

**Decisions (closed 2026-07-18):**

- **Integer offsets only** — matches the v1 absolute-coord policy (§4.3); `~1.5` is rejected
  exactly like a decimal absolute coordinate. Allowing decimals later breaks nothing.
- **Scope = `/teleport` and `/setblock`** — the only commands that consume explicit coordinates.
  `/setspawn`/`/spawn` use the current position with no coordinate args, so `~` does not apply.
- **Player base via a `World` facade helper**, not `WorldOrigin` inside the command — new
  `World.TryGetPlayerVoxelCell(out int x, out int y, out int z)` wraps
  `WorldOrigin.UnityToVoxelCell(player.transform.position)`, keeping the `Commands` namespace
  free of `UnityEngine` usings (the `PlaceBlockCommand`/`Vector3Int` precedent). Returns false
  when no player is loaded → graceful error.

**Execution plan (each step ends in its verification gate; each is a compilable commit):**

1. **A1 — relative-aware coord parse** (`CommandArgUtility.cs`): a `TryParseCoord` overload taking
   `(token, axis, bool relativeAllowed, int relativeBase, out value, out error)`. A `Relative`
   token strips `~`, parses the remainder as an integer offset (empty = 0), computes
   `relativeBase + offset` in `long`, and bounds-checks the **resolved** value against
   `AddressableLimitVoxels`. The existing absolute path is unchanged; the old signature delegates
   with `relativeAllowed: false`. *Gate:* `dotnet build Assembly-CSharp.csproj` green.
2. **A2 — player-base facade** (`World.cs`): `TryGetPlayerVoxelCell`. *Gate:* build green.
3. **A3 — lift the gate + wire the commands** (`CommandEngine.cs`, `TeleportCommand.cs`,
   `SetBlockCommand.cs`): delete the Relative-rejection loop in `Process`; repurpose
   `RelativeCoordsError` as the "relative coordinate needs a loaded player" message (or retire
   it). Both commands: when any coord arg is `Relative`, fetch the player base once via A2 (error
   if unavailable) and pass per-axis bases into the A1 parser. **Reorder `TeleportCommand` so the
   world/player fetch precedes coord parsing** (today coords parse before the `ctx.World == null`
   check). The addressable-limit and Y-range checks already run on the parsed value, so they cover
   the resolved coordinate for free. *Gate:* build green.
4. **A4 — suite** (`CommandConsoleValidationSuite.*.cs`): **B9 flips** — it currently asserts the
   global `~`-rejection (`Baseline.cs:170`); repurpose it prove-red→green (a `~` reaching the coord
   parser resolves against a stub player base; a `~` with no player yields the graceful error).
   **B4 tokenizer classification stays valid — do not touch it.** Add teleport/setblock baselines:
   `~ ~ ~` = no-op to the player cell; `~10`/`~-10` offsets; relative pushed past the addressable
   limit → error; relative without a player → error. Reuse the teleport fixture's `WorldOrigin`
   snapshot/restore (the setblock fixture must do the same). *Gate:* `Validate Command Console`
   green, prove-red verified on the flipped B9.

**Out of scope:** `^` caret/local (look-relative) coordinates — a distinct Minecraft feature, not
requested; relative coords on non-coord commands.

### 8.3 CMD-5 — tab autocomplete + inline ghost ✅ (implemented + in-game confirmed 2026-07-19)

Registry-driven Tab completion in the console input field, plus a PowerShell-style inline gray
"ghost" suggestion. The registry already exposes everything the name-completion path needs
(`CommandRegistry.Commands` → `IConsoleCommand.Name`).

**Shipped 2026-07-19** across three commits (completion core + suite → Tab UI wiring → inline ghost).
The pure core (`CommandEngine.Complete`) and the ghost derivation (`CommandEngine.Suggest`) are
suite-pinned; the UI (ghost overlay, key wiring) is in-game confirmed per §7. Execution deltas from
the plan below, for the record:

- **Inline ghost suggestion added mid-implementation** (user request 2026-07-19), *reversing* the
  original "candidates list into history instead — no ghost-text widget" out-of-scope call. Delivered
  as a pure `CommandEngine.Suggest(input)` (returns the gray suffix only for a single unambiguous
  candidate; ambiguous/empty/fully-typed → empty, so the multi-match Tab behavior is untouched) driving
  a non-interactive TMP overlay that renders `<transparent>typed</transparent><gray>suffix</gray>` so
  the ghost aligns to the caret. A long-line horizontal-scroll misalignment guard was considered and
  **deliberately deferred** until it actually overflows (short console commands never hit it).
- **Accept keys = Tab + RightArrow + End** (decision 2026-07-19): the inline ghost accepts on
  RightArrow/End (caret-at-end guarded) as well as Tab, via a new UI-map `ConsoleAcceptSuggestion`
  action (RightArrow + End bindings) — B23-clean, no direct device reads.
- **`IArgumentCompleter` signature** = `string[] CompleteArgument(int argIndex, string partial, CommandContext ctx)`
  (the simple index+partial+ctx shape); **completion operates at end-of-input only** (no caret param).
- Suite 47→**52**: B48 (name single/empty/no-match/full-pack), B49 (common prefix), B50 (block-name
  args + coord no-op), B51 (`off` + no-completer + chat no-ops), B52 (`Suggest` ghost). Prove-red
  verified on B48 (dropping the trailing space reddened exactly it). Validate All 275/275 across 10 suites.

Original plan, for the record:

**Decisions (closed 2026-07-18, decision menu — don't re-litigate):**

- **D1 — scope = command names *and* argument values.** Beyond completing the `/command` token,
  complete argument values for commands that opt in: block names for `/give` and `/setblock`,
  `on`/`off` for `/set-world-border`. Delivered via a **new opt-in `IArgumentCompleter` interface**
  — *not* added to `IConsoleCommand`, so the other 11 commands are untouched.
- **D2 — multi-match = longest common prefix + candidate list.** On ≥2 matches, complete as far as
  the shared prefix allows and list all candidates in history (bash/Minecraft behavior). Stateless
  — no cycle cursor to track across presses.
- **D3 — completion targets primary names only.** Aliases (`tp`) still resolve when typed in full
  but are not offered as candidates (canonical, less noisy).

**Execution plan (each step ends in its verification gate; each is a compilable commit):**

1. **B1 — pure completion core** (`CommandEngine.cs` + a new `CommandCompletion` type in
   `Commands`): `Complete(string input)` returns `{ string CompletedText, string[] Candidates }`.
   Only the **first token** (command name, after `/`) completes against primary names (D3); caret
   mid-argument delegates to the resolved command's `IArgumentCompleter` (D1); multi-match returns
   the common prefix + candidates (D2). The method is **pure — no `PostLine` side effects** — so
   the suite asserts on the returned struct. It takes `ctx` (already on the engine) so block-name
   completion can read `ctx.World.BlockTypes`. *Gate:* build green.
2. **B1b — `IArgumentCompleter` + providers**: the opt-in interface plus implementations on
   `/give`, `/setblock` (block-name scan), and `/set-world-border` (`on`/`off`). *Gate:* build green.
3. **B2 — UI-map Tab action** (`GameInputActions.inputactions` + `InputManager.cs`): a
   `ConsoleAutocomplete` action (Tab) on the **UI map** (same map as the existing
   `Cancel`/`HistoryUp`/`HistoryDown` console actions), an `_autocompleteAction` field, and a
   `ConsoleAutocompletePressed` property. **Required, not optional** — the B23 tripwire fails the
   suite on any `Keyboard.current` read outside `InputManager`, so Tab cannot be read any other
   way. *Gate:* build green; Unity import; suite still green (B23 not tripped).
4. **B3 — UI wiring** (`ConsoleUI.cs`): in `Update()` beside the ↑/↓ handling, on
   `ConsoleAutocompletePressed` call `_engine.Complete(_inputField.text)`, set the field text +
   caret to end, and on multi-match `PostLine` the candidate list. **Guard Tab against EventSystem
   focus navigation / TMP tab-char insertion** (the documented T-leak class). *Gate:* build green;
   **in-game verification** (Tab feel is not deterministic-suite material — matches how the CMD-1
   UI was verified).
5. **B4 — suite**: name-completion baselines (prefix→single completes with trailing space;
   prefix→multiple returns common prefix + full candidate set; no-match unchanged; empty/`/`
   returns all names; case-insensitive; mid-argument no-op) **and** argument-completion baselines
   (block names via the CMD-3 stub `BlockDatabase` fixture; `on`/`off`). Plus a count-floor-style
   check that completion sees the full registered pack. *Gate:* `Validate Command Console` green;
   `Validate All` green.
6. **B5 — doc-sync**: flip §8.2/§8.3 to implemented, update §4.1's tokenizer note, bump version.

**Commit sequence** (spans both features; each compiles, each preserves verdicts): (1) A1+A2 →
(2) A3+A4 → (3) B1+B1b+B4 → (4) B2+B3 → (5) B5. CMD-4 and CMD-5 are independent — either may ship
first.

**Out of scope:** a multi-candidate suggestion *dropdown* widget (the single-candidate inline ghost
shipped; multiple candidates still list into history); the long-line ghost-alignment overflow guard
(deferred until it overflows in practice); selectable/copyable output (separate v2 item); argument
completion for commands that don't opt into `IArgumentCompleter`.

**Assumptions — all verified on ship (2026-07-19):** (1) Tab/RightArrow/End captured via UI-map
InputActions without the EventSystem stealing them or TMP inserting a tab char — confirmed in-game.
(2) The CMD-3 stub `CommandTeleportTestWorld` exposes `BlockTypes` (Air + Stone) for headless
argument-completion tests — confirmed (reused by B50/B52).

---

## Document History

* **v1.12** - **ConsoleUI resilience hardening (UI_BUGS #04), in-game confirmed 2026-07-21.** A natural repro
  confirmed the failure mode: a built object is *destroyed* out from under the live view (e.g. `_inputField`
  Unity-null while the panel survives) during heavy chunk churn — a far-lands `/teleport` (user-confirmed) or a
  render-distance change, both of which force a full chunk-set re-stream; leading unproven theory is an
  `Esc`-close *during* the teleport arrival hold. No project code destroys it (every `Destroy` in `Assets/Scripts`
  swept), so the destroyer is engine/TMP-internal. Permanent fixes: `RebuildMissingChildren` self-heals at
  whatever level died — the whole `ConsolePanel` (via extracted `BuildPanel()` under the surviving canvas), or an
  individual history-view / input-field / ghost-overlay build-unit — called from `Open()` (which now returns
  `bool`; the `WorldUIManager` caller skips the action-map swap on failure) **and** from `Update()` while open;
  `LateUpdate` no-ops while closed; and `WorldUIManager.Update` recovers a stale-`InUI` soft-lock (needed when the
  `ConsolePanel` is destroyed *while open*, which the self-heal alone couldn't reach). Deleting the input field,
  history view, or whole panel at runtime all recover on the next open. A permanent lightweight tripwire remains
  (`InputFieldDeathSentinel` + the self-heal warnings) while the root cause is unresolved; the heavy `[UIBUG04]`
  investigative scaffolding was removed the same day. §4.2 gained a Resilience note. Root cause tracked in
  `UI_BUGS.md` #04.
* **v1.11** - **CMD-5 tab autocomplete + PowerShell-style inline ghost SHIPPED + in-game confirmed
  2026-07-19.** Three commits: (1) pure completion core — `CommandEngine.Complete` + a new opt-in
  `IArgumentCompleter` (`string[] CompleteArgument(int argIndex, string partial, CommandContext ctx)`)
  on `/give`/`/setblock`/`/set-world-border`, `CommandArgUtility.MatchBlockNames`, suite 47→51
  (B48–B51, prove-red B48); (2) Tab UI wiring — `ConsoleAutocomplete` UI-map action + `ConsoleUI`
  (B23 held); (3) inline ghost — pure `CommandEngine.Suggest` (single-candidate suffix only) + a gray
  non-interactive TMP overlay (`<transparent>typed</transparent><gray>suffix</gray>` for caret
  alignment), accepted by **Tab / RightArrow / End** (new `ConsoleAcceptSuggestion` UI-map action,
  caret-at-end guarded), suite →**52** (B52). The ghost **reverses** the original out-of-scope
  "no ghost-text widget" call (user request); the long-line alignment guard is deferred until it
  overflows. Decisions closed 2026-07-19: simple completer signature, end-of-input-only caret,
  Tab+RightArrow/End accept. Multi-match behavior unchanged (common prefix + candidate list). Validate
  All 275/275 across 10 suites. §7 CMD-5 row + §8 v2 row + §8.3 + §1 non-goal all flipped to ✅ — the
  whole planned CMD arc (CMD-0..5) is now shipped.
* **v1.10** - **CMD-4 relative `~` coordinates SHIPPED + in-game confirmed 2026-07-19** (both
  `/teleport` and `/setblock` verified by hand in relative mode). Implemented exactly per the §8.2
  plan across two commits (A1+A2 groundwork: `CommandArgUtility.TryParseCoord` relative overload +
  `World.TryGetPlayerVoxelCell`; A3+A4: removed the global `~`-reject gate in `CommandEngine.Process`,
  wired both commands with the player-base fetch before parse, suite 43→47). B9 flipped
  reserved→dispatches; B44–B47 added (resolve/offsets, 2-arg surface + overflow, no-player graceful,
  `/setblock` relative); prove-red reddened exactly those 5; Validate All 270/270. `RelativeCoordsError`
  retired in favor of `CommandArgUtility.RelativeNeedsPlayerError`. §7 CMD-4 row + §8.2 + §4.1 tokenizer
  note + §8 v2 row all flipped to ✅. CMD-5 (§8.3) remains the last planned-but-unimplemented v2 item.
* **v1.9** - Two v2 items promoted from backlog rows to **closed execution plans** (decision menus
  closed 2026-07-18; both **NOT yet implemented** — the doc now carries enough for a cold session to
  start warm). **CMD-4 relative `~` coordinates** (§8.2): integer offsets only, scope =
  `/teleport`+`/setblock`, player base via new `World.TryGetPlayerVoxelCell` (keeps `Commands`
  UnityEngine-free), global `~`-rejection gate removed, B9 flips prove-red→green (B4 classification
  untouched). **CMD-5 tab autocomplete** (§8.3): D1 scope = command names + argument values via a new
  opt-in `IArgumentCompleter` (other 11 commands untouched); D2 multi-match = common prefix +
  candidate list; D3 primary names only; Tab **must** route through a UI-map action (B23 tripwire).
  §7 gained CMD-4/CMD-5 `Planned` rows; §8 v2 row + §4.1 tokenizer note repointed to the plans.
* **v1.8** - CMD-3 in-game CONFIRMED (all 13 commands verified by hand) — §7/§8.1 flipped to
  full ✅; the entire v1 console arc (CMD-0..3) is now shipped. Notable emergent integration
  observed during verification: `/setblock <far> stone` followed by `/teleport <far X Z>`
  places the queued block first (pending-mods replay on chunk load precedes the arrival hold's
  readiness release) and the 2-arg surface resolution then lands the player ON the new block —
  pending mods, hold ordering, and `GetHighestVoxel` composing with zero coordination code.
* **v1.7** - CMD-3 code-complete + suite-verified (same-day follow-on session): all 13 pack
  commands shipped via the shared `ConsoleCommandInstaller.RegisterAll` seam (count-floor B32
  false-green guard, `InstalledCommandCount` 14 incl. `/teleport`); new public `World` surface:
  `ForceOriginReanchor` (/origin force) + `PlaceBlockCommand` (/setblock's ForcePlace +
  `Vector3Int` ownership — the `Commands` namespace stays free of UnityEngine usings);
  `SetGlobalLightValue` null-camera-guarded so /time is headless-testable. Suite 31→43
  (B32–B43; fixture gained a stub `BlockDatabase` + installer registration;
  `ValidationReflection.GetInstanceField` added), prove-red ×2 (B39 fly↔noclip coupling, B41
  case-insensitive name lookup), Validate All 266/266 across 10 suites. Execution deltas
  recorded in §8.1.1 (13 commands, /give byte-ID guard). §7/§8.1 flip to full ✅ follows the
  pending in-game pass.
* **v1.6** - CMD-2 shipped + in-game confirmed (near / 2-arg surface / far≥2²⁴ confirm / fence
  re-clamp / verbatim-Y all verified by hand): §7 row flipped ✅ with the shipped surface
  (facade `AttachWorld` wired in `WorldUIManager.Start` — `Awake` is too early for
  `World.Instance`; `CommandEngine.PostLine` added for system-originated lines; hold outcome
  reported as "Arrived."/timeout warning). Suite 23→31 (B24–B31 matrix, prove-red-verified via
  fence-tier sabotage → exactly B29 red); Validate All 254/254 across 10 suites (isolation
  guard proves the fixture's WorldOrigin snapshot/restore is leak-tight). Far-lands
  verification surfaced pre-existing lighting **Bug 19** (negative heightmap index in
  `RecalculateSunlightForColumn` beyond ±2²⁴ — logged in `LIGHTING_BUGS.md`, out of scope
  here). WS-4c tooling row closes in `WORLD_SCALING_FLOATING_ORIGIN.md` alongside.
* **v1.5** - CMD-2 plan closed (decision menu 2026-07-18): §3.3 gained the hold execution
  decisions — release = data + mesh confirmed (with the physics-only-needs-data nuance: the
  hold runs its own read-only readiness poll, the startup gate is not a reusable predicate),
  ~10 s timeout fail-safe with console warning (CP-F1-class stall guard), World-owned hold
  (rigidbody gets only a held flag). §4.3: integer-only coordinates in v1, `tp` alias, the
  `World.TeleportPlayer` execution wrapper spelled out, single-combined-confirmation tier
  order, and the suite's `WorldOrigin` snapshot/restore requirement. Scope note: CMD-2 is the
  command only — the WS-4c persistence half (v13) shipped 2026-07-17 separately.
* **v1.4** - CMD-3 plan closed (decision menu 2026-07-18): scope = the §8.1 pack minus `/fill`
  (10 commands — `/spawn` rejoins since CMD-2 executes first, so its arrival hold exists);
  sequencing = after CMD-2; `CommandContext` facade shape decided (§4.1: concrete nullable
  `World`+`Player`, Placement precedent, null → graceful error); `/time` = `set` only until
  RF-1; `/setblock` = `ForcePlace` + safe pending-mod routing for unloaded targets (verified in
  `ApplyModifications`). Added §8.1.1 execution plan (facade seam → Waves A/B/C, per-wave suite
  gates, `/help` count-floor false-green guard); CMD-3 row added to §7. Drift fix: §3.3
  surface-height API is `ChunkData.GetHighestVoxel` (chunk-local), not `World.GetHighestVoxel`.
* **v1.3** - CMD-1 shipped: `UI.ConsoleUI` builds its whole panel (overlay canvas, ScrollRect
  history, TMP input field) in code at runtime, spawned by `WorldUIManager` — zero scene/prefab
  edits (TouchControls precedent). Input: `ToggleConsole` (T, Gameplay) + `Cancel`/`HistoryUp`/
  `HistoryDown` (UI map); opening the console **disables the Gameplay map** so typing cannot
  fire hotbar/toggle actions — the console's Esc therefore rides the UI map's `Cancel`, not the
  `HandleEscape` gameplay chain (same user-visible behavior as §4.2's "first in the chain").
  T-leak guarded by next-frame field activation + clear; empty submit closes; severity colors
  via `ConsoleTextFormatter` (suite baselines B21/B22 — mapping + markup-injection guard).
  In-game verification caught 4 raw-keyboard bypasses (benchmark trigger keys fired while
  typing — `Keyboard.current` ignores action-map state): fixed by routing them through the new
  gameplay-gated `InputManager.DebugKeyPressed(Key)`, guarded by tripwire B23 (prove-red
  verified: red on the 4 bypasses, green after; any future direct `Keyboard.current` read
  outside `InputManager` reds the suite). Suite 23. UI feel verified in-game manually per §7.
* **v1.2** - CMD-0 shipped: `Commands` runtime namespace (13 files, engine instance-based, no
  statics; culture-invariant tokenizer; confirmation check ordered before the `/`-prefix rule;
  duplicate registry keys throw; `@player` resolves to a semantic `CommandTarget`, not a scene
  object) + `Validate Command Console` suite (20 baselines incl. prove-red-verified prefix
  guards) registered as the 10th aggregate suite. Decisions baked in 2026-07-18: minimal
  `CommandContext` (world facade deferred to CMD-2), case-insensitive command names, submitted
  lines echoed as Info. §7 status flipped for CMD-0.
* **v1.1** - Added §8.1: the CMD-3 candidate command pack (11 commands ranked by value ÷ cost,
  each mapped to its existing backing system; `/fill` explicitly deferred pending an apply-path
  design pass). §8 heading level fixed (was nested under §7).
* **v1.0** - Initial design — three-layer engine/UI/command split; decision menu closed
  2026-07-16 (mandatory `/`, inventory-parity input blocking, hold-until-ready arrival +
  2-arg surface form, v1 = history recall + `/help`); `CMD-0..2` phasing with CMD-2 as the
  WS-4c teleport payload.

---

**Last Updated:** 2026-07-21
**Next Review:** the whole planned CMD arc (CMD-0..5) has shipped. The remaining §8 items (selectable/copyable output, chat on the unprefixed namespace, entity selectors, `/fill`, and the deferred long-line ghost-alignment guard) each still need their own design pass before scheduling.
