# Known Player related bugs

This document outlines **open** bugs related to the player controller and interaction systems. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Mouse input uses `Time.timeScale` — **LOWEST PRIORITY / DO NOT FIX**

**Severity:** Quirk (intentional)  
**Files:** `Player.cs` — `Update`

Camera rotation uses `_mouseHorizontal * Time.timeScale` rather than a pure sensitivity scale. While technically incorrect (frame-rate dependent), the current behavior is consistent across editor (100 fps) and production builds (2000 fps). Fixing it would require re-tuning all sensitivity values and could break the feel. Kept as-is.

---
