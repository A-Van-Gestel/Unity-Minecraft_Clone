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
