# Command Console System Design

**Version:** 1.4
**Date:** 2026-07-18
**Status:** In progress — CMD-0 (engine core + validation suite) and CMD-1 (console UI) implemented 2026-07-18; CMD-2 pending; CMD-3 (command pack) plan closed 2026-07-18, executes after CMD-2 (§8.1).
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
- **Tab autocomplete & selectable/copyable output** — deferred to **v2** (decided 2026-07-16);
  the registry already exposes what autocomplete needs.
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
  data + mesh ready (streaming does the loading; the hold piggybacks the same readiness the
  initial-load gate uses), then releases. Also ships the **2-arg form** `/teleport X Z`, which
  resolves the surface height on arrival (`ChunkData.GetHighestVoxel` — chunk-local; there is no
  world-level overload) — the natural form when far terrain height is unknown.

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
  (`@<word>`), number (signed int/float), or word. **`~`-prefixed tokens are reserved** —
  tokenized but rejected with "relative coordinates are not supported yet" (v2 seat, §8).
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

### 4.3 `TeleportCommand`

`/teleport [@target] X Y Z` and `/teleport [@target] X Z` (surface-resolved Y). Coordinates are
**absolute voxel world space** — post-WS-4 the execution is a thin wrapper: re-anchor
`WorldOrigin` to the destination chunk, place the transform via `WorldOrigin.VoxelToUnity`,
apply the §3.3 arrival hold, and let streaming load the surroundings. Validation tiers:

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

| Phase                     | Scope                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      | Effort | Depends on     |
|---------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|----------------|
| **CMD-0 — engine core** ✅ | Tokenizer, registry, selector resolver (`@player`), confirmation flow, history buffers, `CommandContext`; `HelpCommand`; the validation suite. **Implemented 2026-07-18**: `Assets/Scripts/Commands/` (namespace `Commands`, pure C# — zero Unity usings) + `Validate Command Console` suite (20 baselines, registry suite #10)                                                                                                                                                                                                                                                                                                                                                                                                                                                            |   🟡   | —              |
| **CMD-1 — console UI** ✅  | Panel + ScrollRect + input field, `IsConsoleOpen` state, `ToggleConsole` action, Esc chain, ↑/↓ recall, T-leak guard. **Implemented 2026-07-18**: runtime-code-built `UI.ConsoleUI` (own overlay canvas, no scene edits) + pure `UI.ConsoleTextFormatter` (suite-pinned severity colors + noparse guard, B21/B22); **the Gameplay action map is disabled while the console is open** (typing can't trigger hotbar/toggles), so Esc/↑/↓ arrive via new UI-map actions (`Cancel`/`HistoryUp`/`HistoryDown`) rather than the gameplay Escape chain. Raw-keyboard bypasses closed: benchmark trigger keys route through the gameplay-gated `InputManager.DebugKeyPressed`, and tripwire baseline B23 fails the suite on any `Keyboard.current` read outside `InputManager` (suite 23, B21–B23) |   🟡   | CMD-0          |
| **CMD-2 — `/teleport`**   | §4.3 command incl. warning/confirm matrix, arrival hold, 2-arg surface form; defines the `CommandContext` world facade (§4.1)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |   🟡   | CMD-1, WS-4a/b |
| **CMD-3 — command pack**  | The §8.1 pack — plan closed 2026-07-18: 10 commands incl. `/spawn` (`/fill` stays out); facade extension + shared `ConsoleCommandInstaller.RegisterAll` seam, then Waves A (read-only) → B (setters + `/spawn`) → C (🟡 commands), suite grown per wave (§8.1)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |   🟡   | CMD-2          |

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
| **v2**  | The **CMD-3 command pack** (§8.1); tab autocomplete (registry-driven); selectable/copyable output; relative `~` coordinates.                                                  |
| **v3+** | Entity selectors (`@entity-<id>`, filters) with the entity system; chat on the unprefixed namespace; permissions on `CommandContext` — each gets a design pass when concrete. |

### 8.1 CMD-3 — command pack (plan closed 2026-07-18; executes after CMD-2)

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

#### 8.1.1 CMD-3 execution plan (closed 2026-07-18)

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

---

## Document History

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

**Last Updated:** 2026-07-18
**Next Review:** at the WS-4c kickoff (CMD-2 is its payload).
