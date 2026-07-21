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

## 04. Console Input Field Destroyed During Heavy Chunk Churn (far-lands teleport / render-distance change)

**Severity:** Bug (intermittent) — **mitigated** (self-heal + `LateUpdate` guard shipped); root cause not yet pinned
**Status:** **Mitigated; tripwire live for root-cause capture** — the confirmed failure mode is understood and
the console now self-heals, so the bug is no longer user-visible. The exact destroyer is still unidentified
(not statically findable — no project code destroys the field); the `[UIBUG04]` instrumentation + an
input-field death sentinel stay in to capture the destroyer's frame on the next natural occurrence.
**Files:** `ConsoleUI.cs` (fix + sentinel); investigation touched `WorldUIManager.cs`, `PauseMenuController.cs`,
`SettingsUIGenerator.cs`, `World.cs` (teleport / `ShiftOrigin`), `CommandEngine.cs` — all exonerated as direct destroyers.

**Description:**

Intermittently, after a **far-lands `/teleport`** (user-confirmed trigger, 2026-07-21) or a **render-distance
change** (original report), the console opens with its translucent backdrop and scrollable history rendered but
**no input field**, leaving it unusable; before the fix, only a world save & reload restored it.

**Confirmed failure mode (from a natural `[UIBUG04]` repro, 2026-07-21):**

The `[UIBUG04] Open()` / `FocusInputNextFrame ran.` snapshots print `inputGoActive=False` **and**
`inputText='<null>'`, while `panelActiveSelf=True` / `panelInHierarchy=True`. Both fields come from the same
`_inputField != null` ternary in `ConsoleUI.DiagUIBug04State()`; `inputText='<null>'` is only the *false* branch,
so **`_inputField` is Unity-null** — the input field's GameObject was **destroyed** out from under the live view,
while the panel, canvas, and `ConsoleUI` component all survived. This is neither the original candidate class 1
(open/close state desync — impossible: `IsConsoleOpen` is derived live from `_panel.activeSelf`, no stored
manager belief) nor the focus-loss variant of class 2: the object is *gone*, not deactivated or unfocused. In
the captured logs no `ConsoleUI OnDisable` or panel-`activeSelf` watchdog warning fired in the gap, so the panel
was never deactivated — only the "Input" subtree was silently destroyed and never rebuilt (subsequent opens
re-showed an emptied panel).

**Trigger — heavy chunk churn (both reports unified):** the destruction correlates with a *full chunk-set
re-stream*, which both reported triggers force — a render-distance change resets `_playerLastChunkCoord` to
force one, and a far-lands teleport forces one via `ShiftOrigin` + a `PlayerChunkCoord` jump plus a multi-second
arrival hold. **Leading unproven theory (user, 2026-07-21):** closing the console with `Esc` *while the teleport
arrival hold is still running* — so `TeleportHoldEnded` later posts a line to a now-closed console. `LateUpdate`
had no `IsOpen` guard, so that posted line drove `Canvas.ForceUpdateCanvases()` + a scroll write on the
*inactive* panel subtree, mid-churn, around an origin rebase — a plausible provocation for an engine/TMP-internal
teardown of the field. Not reliably reproducible on demand (deliberate teleport / render-distance / close-mid-hold
attempts across 2026-07-19..21 all failed to reproduce), consistent with a timing race.

**Root cause — NOT statically findable:** every `Destroy`/`DestroyImmediate` in `Assets/Scripts` was swept;
the only objects destroyed during a teleport are chunk / section / cloud / border geometry — never anything under
`WorldUIManager`. `ShiftOrigin` only repositions. The confirmation flow (`CommandEngine`) is pure C#. So the actual
destroyer is engine/TMP-internal (a canvas/InputField teardown) *provoked* by the churn, not a call in our code.

**Mitigation shipped (2026-07-21):**

- **Self-heal** (`ConsoleUI.RebuildMissingChildren`, permanent): reconstructs whatever level died, with a
  name-based remnant cleanup so a partial survivor can't duplicate —
    - the whole **`ConsolePanel`** (rebuilt via the extracted `BuildPanel()` under the surviving canvas — only the
      `Console` GameObject/canvas being destroyed is unrecoverable, but then this component can't run anyway);
    - the **history view** (`_scrollRect`+`_historyText`) and **input field** (`_inputField`, full rebuild) as
      individual build-units; the **ghost overlay** alone (`_ghostText`) when only it died, preserving the live
      field and its typed text (this granular path also means the self-heal never deliberately destroys a live
      field, so it can't trip the death sentinel — review finding).

  Called from **`Open()`** (heals before showing; `Open()` returns `bool`, and `WorldUIManager` skips the
  Gameplay-map swap if it returns false, so a failed heal can't soft-lock input) **and from `Update()`** while
  open (heals a child destroyed mid-session and refocuses; also covers `LateUpdate`'s history-deref). A permanent
  `LogWarning` fires on each rebuild so recurrences stay visible even after the temporary instrumentation is
  removed. In-game confirmed 2026-07-21: deleting the input field, the history view, or the whole `ConsolePanel`
  at runtime all recover on the next open (panel deletion previously left the console permanently invisible).
- **`LateUpdate` guard** (`if (!IsOpen) return;`, permanent): a line posted to a closed console no longer drives a
  canvas rebuild on the inactive panel subtree (defuses the leading theory). No line is lost — `Open()` re-marks
  `_historyDirty`/`_autoscrollPending`, so a reopen rebuilds and autoscrolls.
- **`WorldUIManager` stale-`InUI` recovery** (`Update`, permanent): the panel self-heal only fires from `Open()`,
  but if the `ConsolePanel` is destroyed *while the console is open*, `IsConsoleOpen` (derived from
  `_panel.activeSelf`) flips to false while `InUI` stays latched true and the Gameplay map stays disabled — `T`
  lives on that disabled map, so nothing can re-trigger `Open()` (a soft-lock; in-game confirmed 2026-07-21 that
  the panel self-heal alone did NOT recover this case). `Update` now detects the desync (`InUI` latched true while
  none of console/inventory/pause is actually open), re-enables all maps, and re-runs `UpdateUIState()`; the next
  `T` then reopens and rebuilds the panel. UI-agnostic — it never misfires in normal operation because the state
  setters call `UpdateUIState()` synchronously, so `InUI` is only ever stale after an external destruction.

**Diagnostic tripwire (lightweight, permanent while root cause is unresolved):**

The heavy investigative `[UIBUG04]` scaffolding from the 2026-07-19/21 passes — the raw-T probe +
`InputManager` map-state accessors, the `WorldUIManager` failure-moment capture / `IsConsoleOpen` setter logs /
`DiagUIBug04Snapshot`, the `ConsoleUI` Open/Close logs / panel-`activeSelf` watchdog / `OnEnable`/`OnDisable` /
`FocusInputNextFrame` log / `DiagUIBug04State`, and the `PauseMenuController` settings brackets — was **removed
2026-07-21** now that the failure mode is understood and the console self-heals. What stays, as a permanent
recurrence tripwire (no removable tag — plain warnings referencing "UI_BUGS #04"):

- `ConsoleUI.InputFieldDeathSentinel` — a component on the "Input" GameObject whose `OnDestroy` logs the **exact
  frame** the field dies mid-play (`gameObject.scene.isLoaded`-gated to suppress teardown noise).
- The `RebuildMissingChildren` self-heal `LogWarning`s (panel / history / input / ghost rebuilt) and the
  `WorldUIManager` stale-`InUI` recovery warning — each fires on a recurrence.

**On the next natural occurrence:** the sentinel's "Console input field destroyed externally mid-play" line pins
the death frame — cross-reference it against a `ShiftOrigin` / arrival-hold / hold-end post in the same or
adjacent frames to finally name the destroyer. (If deeper capture is again needed, re-add scaffolding then.)

---
