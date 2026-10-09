# Settled Decisions — Never Findings

Deliberate designs that look like defects to a fresh reviewer. Each was raised, weighed and kept on
purpose; do not report it under any gate or wording. A change that *alters* one of them is still
reviewable — the entry only says the current shape is intended.

## Code

| Looks like                                                                   | Why it is intended                                                                                                                                                         |
|------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `World.StartWorld`'s 60 s startup-load timeout logs and continues             | Specified that way in the ES-1 packet (`ENGINE_SCALING_PERFORMANCE_ROADMAP.md`), matching `ForceCompleteDataJobsCoroutine`'s "Forcing exit" precedent; prove-red'd.          |
| `PerfSessionExporter` stops the whole session's export on any write failure | PM-6's documented contract ("a write failure stops the session's export and is reported once"), pinned by Performance Monitor B37 — a hitch file included.               |
| Force Reserialize All Assets has no confirmation dialog                      | Removed so the CLAUDE.md pre-commit flow can run it through `unity command menu`.                                                                                          |
| `FluidEmitterScanner` keeps its own one-record `PerfJobTimingPool`           | The scan can outlive the world's pool; commented in both files.                                                                                                            |
| `StorageIoStats.Stamp()` duplicates `PerfStore.Begin()`                      | `Stamp` runs on ThreadPool threads, and `SlotsActive` is the only `PerfStore` member documented worker-safe.                                                               |
| `PerformanceMonitor` samples every frame with the debug HUD closed           | The history ring buffer is what makes a hitch that happened with the HUD off visible when it opens (`DebugScreen.SyncGraphsWithHistory`); ~µs/frame, accepted by design. |
| A serialized-field rename without `[FormerlySerializedAs]`                   | The project carries renames over with the `unity-file-ops` workflow instead — gate 9 checks the carry-over, not the attribute.                                             |

## Project setup

| Looks like                                                                                          | Why it is intended                                                                                                                                                                                           |
|-----------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `com.unity.pipeline` (a preview package) installed                                                  | It is the Unity CLI's Editor bridge — agent tooling.                                                                                                                                                         |
| `com.unity.modules.adaptiveperformance` with no visible use                                         | Android builds ship.                                                                                                                                                                                         |
| `com.unity.modules.imageconversion` with no package depending on it                                 | Used directly by `AtlasPackerWindow` and `BlockIconGenerator` (`EncodeToPNG` / `LoadImage`).                                                                                                                 |
| `com.unity.modules.androidjni`                                                                      | The OM-1 calibration export to public Downloads (`BenchmarkEnvironment.TryWriteToAndroidDownloads`) needs it.                                                                                                   |
| `terrain`, `particlesystem`, `director`, `audio`, `animation` modules                               | Forced transitively by URP / timeline / ugui / uielements; removing them is futile (`packages-lock.json` shows the forcing graph).                                                                          |
| No Git LFS, large binaries committed directly                                                       | LFS was scrubbed on purpose — the record is in `.gitattributes`' own comments. Check its absence with `git config --local --get-regexp '^lfs\.'`: any `git lfs` command re-adds `lfs.repositoryformatversion`. |
| Monocraft font atlases (129 MB) and music at Vorbis quality 1.00 (104 MB)                           | Declined as `AU-4` / `AU-5` in `Documentation/Design/PROJECT_AUDITOR_FINDINGS_REPORT.md`; that report's §3 lists the other auditor counts that are not defects.                                           |
