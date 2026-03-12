# Known Serialization & Storage related bugs

This document outlines **open** bugs related to saving, loading, Region files, and Mod Manager. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

---

## 01. Region File Thread Safety adds massive overhead

**Severity:** Performance / Concurrency  
**Files:** `RegionFile.cs`

The `_fileLock` works correctly to prevent save data corruption but adds massive overhead.

**Status:** Needs careful architectural changes to split read and write concurrency. See `Documentation/Technical/REGION_FILE_CONCURRENCY.md` for a full breakdown of requirements before addressing this.

---

## 02. Mod Manager depends on Block Database Initialization

**Severity:** Architecture  
**Files:** `ModificationManager.cs`

`RestoreChunkModifications` relies on `World.Instance.blockDatabase` being loaded before data is restored.  
**Impact:** Tight coupling and order-of-initialization dependency.

---
