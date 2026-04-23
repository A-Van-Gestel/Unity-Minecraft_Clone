# Known User Interface (UI) related bugs

This document outlines **open** bugs related to the UI, Menus, Inventory, and HUD. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Missing Inventory Update Handling

**Severity:** Implementation  
**Files:** `UIItemSlot.cs`

Adding items natively doesn't auto-update the UI.  
**Impact:** UI state can desync from the actual internal inventory state.

---

## 02. Shortcut Info Panel

**Severity:** Feature
**Files:** UI / HUD

Add a Shortcut info UI panel somewhere in the project (eg: F6 == noclip, F3 == Debug Screen, etc.) As currently, some users do not know that these tools exist.

---

## 03. Block Name Visibility

**Severity:** Feature
**Files:** UI / Inventory

Implement block name visibility in the hotbar & Inventory UI (maybe pop-ups).

---

## 04. Settings Page Overhaul

**Severity:** Feature
**Files:** `SettingsManager.cs` / UI

Settings page overhaul, different tabs, advanced settings, expose all settings available in the `Assets/Scripts/SettingsManager.cs` class.

---

## 05. In-game Pause Screen

**Severity:** Feature
**Files:** UI

In-game pause screen with "continue", "settings", "back to main menu" and "quit" options.

---
