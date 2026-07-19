# Known User Interface (UI) related bugs

This document outlines **open** bugs related to the UI, Menus, Inventory, and HUD. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** May 2026

---

## 01. Missing Inventory Update Handling

**Severity:** Implementation  
**Files:** `UIItemSlot.cs`

Adding items natively doesn't auto-update the UI.  
**Impact:** UI state can desync from the actual internal inventory state.

---

## 02. Settings UI: Sub-Page / Pop-Up Navigation Support

**Severity:** Feature  
**Files:** `SettingsUIGenerator.cs`, `SettingActionAttribute.cs`

The `[SettingAction]` attribute system supports simple action buttons (e.g., "Clear All Benchmarks"). A future extension should support **sub-page navigation**: a button that opens a pop-up or child panel with additional settings, and triggers a callback on close to refresh the parent settings page.

**Proposed approach:** Add an optional `NavigatesTo` property to `SettingActionAttribute` (or a dedicated `[SettingSubPage]` attribute). The generator would handle panel open/close lifecycle and auto-call `RebindValues()` on the parent when the sub-page dismisses.

---

## 03. Pre-Benchmark Setup Screen

**Severity:** Feature  
**Files:** `MainMenuController.cs`, `BenchmarkController.cs`, `Settings.cs`

Replace the one-click "Run Benchmark" button with a dedicated setup screen that exposes all benchmark configuration before starting. Should include: region size, generation/loading speed editors, seed field, world type selector, and an estimated duration label.

Additionally, consider integrating currently runtime-only benchmark scripts (e.g., `MeshGenerationBenchmark`) into this UI as selectable benchmark modes, so all benchmarking is accessible from a single entry point rather than requiring Inspector configuration.

The Benchmark tab in the Settings UI could serve as this setup screen with a "Start Benchmark" `[SettingAction]` button, avoiding the need for a separate panel.

---

## 04. Console Input Field Disappears After Changing Render Distance

**Severity:** Bug (intermittent)
**Status:** Open / dormant — logged 2026-07-19 (user-observed during the far-lands re-test sessions); repro attempts
2026-07-19 with instrumentation live all failed — possibly a one-off; `[UIBUG04]` instrumentation stays in as a
latent tripwire for the next natural occurrence
**Files:** suspected `WorldUIManager.cs` (console open/close + `World.InUI` state machine), `ConsoleUI.cs`, `PauseMenuController.cs` (`OnSettingsClosed`), `SettingsUIGenerator.cs`

**Description:**

Sometimes, after changing the render distance in the in-game settings menu, the console's command input
field no longer appears; only a world save & reload brings it back. Suspected to involve the settings-menu →
world trigger path, but the mechanism is unconfirmed ("sometimes" — the trigger condition is not yet pinned
down, e.g. whether the console must have been open before/while entering the settings menu).

**Symptom refined (user recollection, 2026-07-19):** the console DID open — the translucent backdrop and
history panel rendered — but the interactable input text field did not, leaving the console unusable. So in
the observed instance the toggle path, action maps, and `InUI` were all working (the panel opened); the
failure was field-level *inside* an open panel. This demotes candidate class 1 (open/close state desync)
for that instance and promotes candidate class 2 / a field-rendering failure (input field GameObject
inactive, rect collapsed, or TMP failing to render/activate it).

**Root Cause Suspected (unconfirmed — initial static survey only):**

- A direct mechanism was NOT found on the render-distance apply path: `PauseMenuController.OnSettingsClosed`
  replaces `World.Instance.settings` wholesale and calls `World.OnSettingsChanged()`, which only re-runs
  `CheckViewDistance()` and `clouds.Reinitialize()` — nothing there touches the console hierarchy.
- Candidate class 1 — **open/close state desync:** `ConsoleUI` is runtime-code-built with its own canvas;
  `Open()`/`Close()` are driven by `WorldUIManager`, and `IsOpen` is derived from `_panel.activeSelf`. If the
  manager's UI state (or `World.InUI` / the Gameplay-vs-UI action-map swap) desyncs from the panel's active
  state — e.g. via the pause-menu transitions around the settings screen — the toggle key may stop reaching
  `Open()`.
- Candidate class 2 — **focus/selection loss:** `ConsoleUI.FocusInputNextFrame` / `ActivateInputField` guard the
  documented T-leak class; a settings-menu interaction stealing EventSystem selection could leave the field
  visually absent/unfocusable while the panel itself is open.

**Diagnostic first steps (next session):**

1. Reproduce with the scene hierarchy visible: when the field "disappears", check whether the ConsoleUI panel
   GameObject is inactive (state desync) vs active-but-empty/unfocused (focus loss), and whether the whole
   panel or only the input field is missing.
2. Instrument `WorldUIManager`'s console toggle path + `ConsoleUI.Open()/Close()` with logs; compare the
   manager's believed state against `_panel.activeSelf` after a render-distance change.
3. Bracket the trigger: does it require the console to have been opened at least once before the settings
   visit? Does changing a *different* setting (or opening/closing settings without changes) also trigger it?

**Diagnostic instrumentation LIVE (2026-07-19 — `[UIBUG04]` log prefix; awaiting an in-game repro):**

- Structural finding from the instrumentation pass: `WorldUIManager.IsConsoleOpen` has **no stored manager
  belief** — it is derived live from `_panel.activeSelf`. A literal manager-vs-panel desync (candidate 1 as
  originally phrased) is therefore impossible; the real desync surface is between the panel state and the
  things written only at transition time in the `IsConsoleOpen` setter: the `InUI` snapshot and the
  Gameplay/UI action-map swap. `T` (`ToggleConsole`) lives on the Gameplay map AND is guarded by `!InUI`,
  so either a stuck-disabled Gameplay map or a stuck-true `InUI` silently kills the toggle. Only
  `WorldUIManager`'s console setter (and `InputManager.OnEnable/OnDisable`) ever touches the maps — verified
  by grep.
- Instrumented (all sites tagged `UI_BUGS #04 diagnostic`, remove together after root-cause confirmation):
  `InputManager.DiagnosticRawKeyPressed/…MapEnabled` (raw map-independent T probe — inside `InputManager`
  so the B23 tripwire stays green); `WorldUIManager.Update` failure-moment capture (raw T while console
  closed → full snapshot: `InUI` components, map states, panel/field/focus state, EventSystem selection,
  frame); `WorldUIManager.IsConsoleOpen` setter transition + early-return logs; `ConsoleUI` Open/Close logs,
  an external panel-`activeSelf`-change watchdog in `Update`, `OnEnable`/`OnDisable` logs (catches ancestor
  deactivation the watchdog can't see), and a `FocusInputNextFrame` completion log;
  `PauseMenuController.EnterSettings`/`OnSettingsClosed` settings-visit bracket snapshots.
- Gate state at instrumentation commit: both `dotnet build`s green; Validate All 279/279 (Command Console
  54/54, B23 held).
- On repro, the discriminating log is the `[UIBUG04] Raw T while console closed:` line —
  `ToggleConsolePressed=False` + `gameplayMap=False` ⇒ stuck action maps; `ToggleConsolePressed=True` +
  `InUI=True` ⇒ stale `InUI`; toggle fires but panel/field state odd ⇒ candidate 2 (focus/field loss).
- **Given the refined symptom (panel opens, field missing), the primary lines to capture on the next natural
  occurrence are instead** the `[UIBUG04] Open():` and `[UIBUG04] FocusInputNextFrame ran.` snapshots —
  `inputGoActive=False` ⇒ the field's GameObject was deactivated (find what did it); `inputGoActive=True` +
  `inputFocused=False` ⇒ activation/focus failure; `inputGoActive=True` + `inputFocused=True` but nothing
  visible ⇒ pure rendering failure (rect/TMP mesh) — grab the ConsolePanel hierarchy + `Input` RectTransform
  values in the inspector before reloading.

---
