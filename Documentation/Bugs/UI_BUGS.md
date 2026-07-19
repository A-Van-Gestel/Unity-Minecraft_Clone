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
**Status:** Open — logged 2026-07-19 (user-observed during the far-lands re-test sessions; repro not yet deterministic)
**Files:** suspected `WorldUIManager.cs` (console open/close + `World.InUI` state machine), `ConsoleUI.cs`, `PauseMenuController.cs` (`OnSettingsClosed`), `SettingsUIGenerator.cs`

**Description:**

Sometimes, after changing the render distance in the in-game settings menu, the console's command input
field no longer appears; only a world save & reload brings it back. Suspected to involve the settings-menu →
world trigger path, but the mechanism is unconfirmed ("sometimes" — the trigger condition is not yet pinned
down, e.g. whether the console must have been open before/while entering the settings menu).

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

---
