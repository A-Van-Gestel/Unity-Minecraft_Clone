# Third-Party Library Ideas Report

**Version:** 1.0
**Date:** 2026-08-05
**Status:** Open backlog. Items are removed (archived) when implemented and verified.

> Techniques harvested from an evaluation of the Cysharp library suite (ZString, ZLinq,
> NativeMemoryArray, UniTask, ZLogger, R3, Kokuban). **All seven libraries were rejected as
> dependencies** (§2 records why, so they are not re-proposed); this report exists because
> several of their *ideas* are worth applying with BCL types we already have. The headline
> finding: none of these libraries reach inside Burst jobs, where this engine's performance
> lives — they all target the managed layer, which is already disciplined. The surviving items
> are therefore small and localized, not architectural.

**Audited:** 2026-08-05, at commit `1a5fc107` (branch `feat/world-scaling`).
Findings are from static review of `Assets/Scripts/Helpers/UI/StringBuilderFormat.cs` (full),
`DebugScreen.cs`, `Assets/Scripts/Serialization/` (`RegionFile.cs`, `ChunkStorageManager.cs`,
`ChunkSerializer.cs`, `SerializationBufferPool.cs`), `Packages/manifest.json`, and repo-wide
sweeps for `System.Linq` imports, `event Action` declarations, and `Debug.Log*` call sites.
Allocation claims below are **verified in code, not assumed** — each item's "What exists today"
names the line. No profiler capture was taken; items whose payoff depends on measured allocation
volume say so explicitly and carry a measurement gate.

**Relationship to other documents:**

- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — the master
  performance backlog and the **owner of the ID space for runtime perf work**. `TP-1` is *not* a
  new finding: it is already filed there as `SL-1`, and this report contributes only a technique
  note. `TP-4` is adjacent to `SL-1`'s "`Task.Run` closure" observation but is not covered by it.
- [`../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md`](../Architecture/INFINITE_WORLD_STORAGE_AND_SERIALIZATION_ARCHITECTURE.md)
  — authoritative doc for the region/chunk IO path that `TP-1` touches.
- [`REGION_FILE_CONCURRENCY.md`](REGION_FILE_CONCURRENCY.md) — `TP-1` edits inside
  `RegionFile`'s `_fileLock`; its locking contract constrains the buffer's lifetime.
- [`../Guides/GENERAL_OPTIMIZATION_GUIDE.md`](../Guides/GENERAL_OPTIMIZATION_GUIDE.md) — the
  pooling and no-hot-path-GC rules every item here must satisfy.
- [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) — `TP-8`
  concerns the headless CI runner's output formatting.

---

## Legend

| Field       | Values                                                                                                                                         |
|-------------|------------------------------------------------------------------------------------------------------------------------------------------------|
| **Effort**  | 🟢 Low (hours, localized) · 🟡 Medium (days, several files) · 🔴 High (architectural, cross-system)                                            |
| **Risk**    | 🟢 Low (isolated, easy to verify) · 🟡 Medium (touches shared state or visual output) · 🔴 High (touches pipeline invariants or semantics)     |
| **Benefit** | 🟢 Core — high value or unlocks other planned work · 🟡 Situational / polish · ⚪ Minor                                                         |
| **Seed**    | ✅ Safe — cannot change generated terrain for a given seed · ⚠️ Terrain-affecting                                                               |
| **Save**    | ✅ Safe — no on-disk format change · ⚠️ Format — requires a save-format version bump + AOT migration step (see `serialization-migration` skill) |

---

## 1. Master summary table

| ID   | Finding                                                              | Source library    | Effort | Risk | Benefit | Seed | Save |
|------|----------------------------------------------------------------------|-------------------|:------:|:----:|:-------:|:----:|:----:|
| TP-1 | ↪ **Filed elsewhere as `SL-1`** — technique note only, not a new item | NativeMemoryArray |   —    |  —   |    —    |  ✅   |  ✅   |
| TP-2 | HUD numeric formatting is an approximation of `"F{n}"`, not exact     | ZString           |   🟢   |  🟢  |   ⚪    |  ✅   |  ✅   |
| TP-3 | Interface-typed `foreach` boxes its enumerator (unaudited)            | ZLinq             |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| TP-4 | `async Task` state-machine/`Task` allocation on the chunk IO path     | UniTask           |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| TP-5 | No declarative frame-rate limiter / change-detector for UI rebuilds   | R3                |   🟡   |  🟢  |   🟡    |  ✅   |  ✅   |
| TP-6 | No subscription-lifetime tracking for long-lived event subscribers    | R3                |   🟡   |  🟢  |   ⚪    |  ✅   |  ✅   |
| TP-7 | Log call sites pay formatting cost before the level check             | ZLogger           |   🟡   |  🟡  |   ⚪    |  ✅   |  ✅   |
| TP-8 | Headless CI stdout has no ANSI colorization                           | Kokuban           |   🟢   |  🟢  |   ⚪    |  ✅   |  ✅   |

**Ranking guidance:** `TP-4` is the only item here with a plausible measurable payoff, and it is
gated on a profiler capture (§3). `TP-1`'s finding is already owned by `SL-1` in the performance
backlog — schedule it there, not here. `TP-2` is a correctness cleanup worth doing whenever
`StringBuilderFormat` is next touched. `TP-3`, `TP-5`–`TP-8` are opportunistic — do not schedule
them on their own.

---

## 2. Evaluated libraries — verdicts (do not re-propose)

Recorded so future sessions do not re-run this evaluation. NuGetForUnity is already installed
(`Packages/manifest.json`), so *installation* is mechanically cheap for any of these — the
rejections below are on merit, not on install friction, except where noted.

| Library               | Verdict                     | Reason                                                                                                                                                                    |
|-----------------------|-----------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **ZString**           | ❌ Rejected — already solved | Its Unity headline (zero-alloc TMP text) is already achieved by `StringBuilderFormat` + `TMP.SetText(StringBuilder)` (`DebugScreen.cs:316-350`). Its struct-builder/`ArrayPool` win is nil against long-lived `StringBuilder` fields. Residual idea → `TP-2`. |
| **ZLinq**             | ❌ Rejected — rule is cheaper | Only 8 files import `System.Linq`, mostly editor/migration code where perf is irrelevant. The CLAUDE.md "no LINQ in hot paths" rule already covers the runtime. Residual idea → `TP-3`.                                                                        |
| **NativeMemoryArray** | ❌ Rejected — wrong layer     | Explicitly managed-side only, so it cannot cross into `Assets/Scripts/Jobs/`; Unity.Collections 6.5 already provides Burst-native containers. The 2 GB `Array.MaxLength` ceiling it exists to break is not one we approach. Residual idea → `TP-1`.            |
| **UniTask**           | ❌ Rejected — no fit          | Our async surface is genuine background-thread IO (`Task.Run`), where `UniTask.RunOnThreadPool` is just `Task.Run`. Its core value (replacing coroutines, awaiting `AsyncOperation`) does not apply — heavy work runs on Jobs. Unity 6.5 ships `Awaitable` for the rest. Residual idea → `TP-4`. |
| **ZLogger**           | ❌ Rejected — breaks tooling  | Routes logs away from `UnityEngine.Debug.Log`. The entire diagnostic workflow depends on that sink: the `voxel-debugging` instrument-then-read loop, `Unity_ReadConsole`, and tailing `<project>/Logs/Editor.log`. Redirecting 317+ call sites would silently break all of it. Residual idea → `TP-7`. |
| **R3**                | ❌ Rejected *for now*         | Only 5 classes hold events (`SettingsManager`, `CommandEngine`, `PerformanceMonitor`, `World`, `ChunkData`) — not a subscription-leak problem worth an Rx runtime. **Reconsider if** the settings/HUD binding layer grows past ~5 more bindings. Residual ideas → `TP-5`, `TP-6`. |
| **Kokuban**           | ❌ Rejected — already solved  | Unity's console renders rich-text tags, not ANSI escapes, so it does nothing in-editor; the validation runner already colorizes its summary. Only `-batchmode` stdout is a real niche. Residual idea → `TP-8`.                                                |

---

## 3. Detail sections

### TP-1 — Pooled read buffer on the region load path → **already filed as `SL-1`**

**Classification:** Not a new finding. **Do not schedule from this report.**

**What exists today:** The **save** path is already pooled — `ChunkStorageManager.cs:230,292,687`
rent from `SerializationBufferPool` (`ConcurrentBag<byte[]>`) and serialize into the rented buffer
via `ChunkSerializer.Serialize(data, byte[] outputBuffer, …)` (`ChunkSerializer.cs:61`). The
**load** path is not: `RegionFile.LoadChunkData` allocates `new byte[4]` for the length header
(`RegionFile.cs:137`) and a fresh `new byte[payloadLength]` for the payload (`RegionFile.cs:167`).

`SL-1` in [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) already
documents this and more (the 512 B heightmap `ReadBytes`, the per-load `Enum.IsDefined` reflection,
the save-side `pad` and `BitConverter.GetBytes` arrays, the wrapper-object churn), and its
recommendation — a length-aware rent from `SerializationBufferPool`, sliced for free because
`ChunkSerializer.Deserialize` **already takes `ReadOnlySpan<byte>`** (`ChunkSerializer.cs:92`) —
is the correct shape. This report's only contribution is confirming that the NativeMemoryArray
`IBufferWriter<byte>` / pooled-buffer model independently arrives at the same design, and that it
needs no third-party package to implement.

> **Two corrections of record**, both made while auditing this item — recorded so the wrong
> versions do not resurface:
> 1. An earlier session claim that the *save* path allocated a per-chunk `byte[]` was **wrong** —
>    it pools. The unpooled allocation is on **load**.
> 2. A draft of this item proposed changing `Deserialize` to take `(byte[] buffer, int length)`.
>    Unnecessary — it already takes `ReadOnlySpan<byte>`, so an oversized pooled buffer slices to
>    the exact payload length with no signature change and no tail-byte hazard.

**Dependencies / cross-links:** Schedule under `SL-1`. `REGION_FILE_CONCURRENCY.md` — a rented
buffer must not outlive `RegionFile`'s `_fileLock` critical section in a way that lets two readers
share one buffer. `MigrationManager.cs:353,428` calls `LoadChunkDataWithRetry` and would need
migrating in the same change. No on-disk layout change: in-memory buffer lifetime only, so **no
version bump and no AOT migration step** (`Save: ✅`).

---

### TP-2 — Exact numeric formatting for HUD text

**Classification:** Polish (correctness cleanup).

**What exists today:** `StringBuilderFormat.AppendFixed` (`StringBuilderFormat.cs:31`) formats via
`RoundParts` (`:173`), which scales by a power of ten, adds `0.5`, and truncates —
half-away-from-zero. Its own docstring (`:25`) admits this matches `"F{n}"` only *"closely enough
for debug display"*. `MeasureFixedWidth` (`:195`) re-derives the width from the same helper, so
padding stays consistent with whatever `AppendFixed` emits.

**Gap / finding:** Two divergences from `"F{n}"`. (a) .NET's `"F"` uses banker's-style
round-half-to-even in some paths, so values sitting exactly on a half-digit can differ by one in
the last place. (b) `(long)(value * scale + 0.5)` overflows silently for large magnitudes and is
subject to binary floating-point representation error before the rounding even happens. Neither
matters for an FPS counter; both would matter if these helpers are ever reused for a value a
player or a test asserts on.

**Proposal:** Replace `RoundParts`'s hand-rolled arithmetic with `TryFormat` into a stack-allocated
`Span<char>`, then append the span — the technique ZString uses internally, available in the BCL
without a dependency. Keep the public `AppendFixed`/`AppendFixedPadded`/`AppendIntPadded` surface
unchanged so no call site moves.

- ⚠️ **Verify availability first.** The project targets *.NET Framework* API Compatibility
  (CLAUDE.md); confirm `double.TryFormat(Span<char>, out int, ReadOnlySpan<char>, IFormatProvider)`
  resolves under that profile in Unity 6.5 before committing to this approach. If it does not,
  the fallback is to keep the current arithmetic and simply document the rounding mode honestly in
  the docstring — the cheaper half of the fix, and worth doing regardless.
- `MeasureFixedWidth` collapses into "format once, measure the written span, pad, append" — which
  also removes the current double-formatting (the value is decomposed twice per padded append).

**Dependencies / cross-links:** Consumers are `DebugScreen.cs` and `Benchmarks/BenchmarkHUD.cs`.
Do this opportunistically the next time `StringBuilderFormat` is edited; it does not justify its
own change.

---

### TP-3 — Audit interface-typed enumeration for boxed enumerators

**Classification:** Polish (conditional — audit first).

**What exists today:** Not audited. The CLAUDE.md rule bans LINQ in hot paths and the repo honours
it (8 `System.Linq` imports total, mostly editor). What the rule does *not* cover is plain
`foreach` over a field or parameter **declared** as `IEnumerable<T>`, `ICollection<T>`, or
`IReadOnlyList<T>`.

**Gap / finding:** `List<T>` and `Dictionary<K,V>` expose struct enumerators, so `foreach` over a
concretely-typed variable allocates nothing. The moment the same collection is reached through an
interface, C# binds to `IEnumerable<T>.GetEnumerator()`, which boxes the struct enumerator — one
allocation per loop, invisible at the call site and invisible to the no-LINQ rule. This is the one
real gap ZLinq's `AsValueEnumerable()` closes that our existing conventions do not.

**Proposal:** An audit, not a change. Sweep the per-frame and per-chunk managed paths for
interface-typed iteration; where found, change the *declaration* to the concrete type (free, no
dependency) rather than adopting a library. Only if a genuinely polymorphic collection must stay
interface-typed on a hot path does the question of a helper arise — and then a hand-rolled
`struct` enumerator wrapper is smaller than a package.

**Verification / gate:** The check is a profiler GC-allocation query on the pipeline update path
(`Unity_Profiler_GetFrameGcAllocati_*`), not a code read — a boxed enumerator is invisible in
source. If the capture shows no such allocations, close this item.

---

### TP-4 — Async allocation on the chunk IO path

**Classification:** Core (GC pressure), conditional on measurement.

**What exists today:** `ChunkStorageManager.LoadChunkAsync` (`:176`) and `SaveChunkAsync` (`:284`)
are `async Task<T>` methods wrapping `Task.Run` for background IO. Each invocation allocates a
`Task<T>` plus a compiler-generated state machine.

**Gap / finding:** In **Debug** compilation the C# compiler emits the async state machine as a
*class*, so every chunk load and save allocates it on the heap; in **Release** it is a struct and
the allocation disappears unless the method actually suspends. Unity exposes this per-project as
*Player Settings → Code Optimization*, and the setting's current value for this project has **not
been verified**. The `Task<T>` object itself allocates in both modes.

**Proposal:** Two steps, cheapest first.

1. **Verify Code Optimization is set to Release** for development builds and editor play. This is
   the free half of what UniTask would buy and requires no code change. Do this first — it may
   close the item outright.
2. Only if a capture still shows material `Task`/`AsyncStateMachineBox` allocation: consider
   returning `ValueTask<T>` from these two methods. Both have a meaningful synchronous-completion
   path (`LoadChunkAsync` returns `null` early for not-on-disk), which is exactly the case
   `ValueTask` optimizes. ❌ **Rejected alternative:** adopting `UniTask` for its pooled task type
   — it would pull a whole runtime in for two methods on a background thread, and its PlayerLoop
   integration (the actual reason to use it) is unused here.

**Verification / gate:** Ship nothing on this item without a before/after allocation capture during
chunk streaming, per the `perf-benchmark` protocol. Note that these allocations are on a
ThreadPool thread, not the render thread — they cost collection frequency, not frame spikes, so
the bar for "material" is higher than for a per-frame allocation.

**Dependencies / cross-links:** `SL-1` already names "the `Task.Run` closure" among the load path's
allocations — if both are actioned, do them in one pass over `ChunkStorageManager`. `ValueTask` has
stricter consumption rules than `Task` (single await, no double-consumption) — audit both call
chains before converting. CP-6's pending-retry logic and CP-3's fault-vs-null load contract both
depend on these methods' faulted-task semantics, which `ValueTask` preserves but which must be
re-tested.

---

### TP-5 — Declarative frame-rate limiting and change detection for UI rebuilds

**Classification:** Polish.

**What exists today:** HUD and overlay text is rebuilt from `Update`-driven code with hand-rolled
gating where gating exists at all (`DebugScreen.cs` rebuilds six `StringBuilder`s per refresh).
`SettingsManager` exposes plain `event Action` for change notification.

**Gap / finding:** Two recurring shapes have no shared primitive: "rebuild this panel at most
every N frames" and "notify me when this property changes, given it has no change event". Each
site re-solves them with a frame counter and a cached previous value, or skips the optimization.

**Proposal:** Two small helpers, ~15 lines each, modelled on R3's `DebounceFrame` /
`ThrottleLastFrame` and `Observable.EveryValueChanged` — **without** adopting R3.

- A `FrameThrottle` struct: `if (!_throttle.Ready(frameCount)) return;` at the top of a rebuild.
- A `ChangeDetector<T>` struct: holds the last value and an `IEqualityComparer<T>`, returns true
  on change. This is what `EveryValueChanged` does, minus the observable plumbing.

Only build these if a **third** site needs them; two hand-rolled counters do not justify an
abstraction. Recorded here so the shape is recognized when the third arrives.

**Dependencies / cross-links:** If the UI layer grows enough that these helpers start composing
(throttle *then* change-detect *then* bind), that is the signal to revisit R3 wholesale (§2).

---

### TP-6 — Subscription-lifetime tracking for long-lived subscribers

**Classification:** Polish (diagnostics).

**What exists today:** Five classes expose events (`SettingsManager`, `CommandEngine`,
`PerformanceMonitor`, `World`, `ChunkData`). Unsubscription is manual — the standard `-=` in
`OnDisable`/`OnDestroy` — and nothing verifies it happened.

**Gap / finding:** A subscriber that fails to unsubscribe keeps its target alive and keeps
receiving callbacks after logical teardown. With domain reload disabled (CLAUDE.md), a leaked
subscription on a **static** event survives into the next play session — the same class of stale
state the static-reset rule exists to prevent, but for delegates rather than fields. Nothing
currently surfaces this.

**Proposal:** The transferable idea from R3's `ObservableTracker` window is *observability*, not
Rx: an editor-only registry that records `(subscriber type, event name, subscribe stack trace)` on
subscribe and removes on unsubscribe, with a small `EditorWindow` listing what is still live. Wrap
registration in `#if UNITY_EDITOR` and behind an explicit enable toggle — stack-trace capture is
expensive, exactly as R3 documents.

- Cheaper first move: add the five events' unsubscription to the domain-reset paths that already
  exist (`World.DomainReset`) and assert emptiness at play-mode exit. That catches the leak class
  without building a window.

**Dependencies / cross-links:** `editor-tool` skill for the window's lifecycle/cleanup rules if it
is ever built. Overlaps the CLAUDE.md static-field/domain-reload rules.

---

### TP-7 — Defer log-message formatting until after the level check

**Classification:** Polish.

**What exists today:** 317+ `Debug.Log*` call sites across 56 files under `Assets/Scripts`. Many
pass interpolated strings; the codebase already uses `.ToString()` on value types at some sites to
dodge boxing (e.g. `RegionFile.cs:148,161`) and guards diagnostic logs behind
`#if DEVELOPMENT_BUILD || UNITY_EDITOR` plus a settings flag (`ChunkStorageManager.cs:182-184`).

**Gap / finding:** An interpolated argument is fully formatted **before** `Debug.Log` is called,
so a log statement that is filtered or disabled still pays string construction. The existing
`#if` + flag pattern handles this correctly but is applied per-site and by hand.

**Proposal:** The idea worth keeping from ZLogger is its `[ZLoggerMessage]` source generator: log
call sites become generated methods that check the level *before* formatting. The
dependency-free equivalents, cheapest first:

1. `[System.Diagnostics.Conditional("UNITY_EDITOR")]` on diagnostic-only log wrappers — the
   compiler removes the call **and its argument evaluation** at every call site. This is the
   highest-value, lowest-cost piece and needs no generator.
2. If per-category runtime levels are ever wanted, a small source generator emitting
   `LogChunkLoaded(coord, ms)`-style methods.

❌ **Rejected:** adopting ZLogger itself — it redirects the sink and breaks the MCP debugging loop
(§2). Any work here must keep `UnityEngine.Debug.Log` as the terminal destination.

**Dependencies / cross-links:** `voxel-debugging` skill owns the Burst-safe logging rules; a
wrapper layer must not break Burst-context logging constraints.

---

### TP-8 — ANSI colorization for headless CI output

**Classification:** Minor.

**What exists today:** The validation runner emits a colorized console summary in-editor, and
`ValidationSuiteCI` supports headless/batch runs with an NUnit3 XML writer
(`Assets/Editor/Validation/Framework/`).

**Gap / finding:** In-editor colorization uses Unity rich-text tags, which Unity's console renders
but a terminal does not — they appear as literal `<color=…>` noise in `-batchmode` stdout and in CI
logs. Conversely ANSI escapes render in a terminal but appear as garbage in Unity's console. The
two destinations need different encodings and currently share one.

**Proposal:** Detect the destination once (`Application.isBatchMode`) and swap the encoding: rich
text in-editor, ANSI SGR codes in batch. Kokuban's genuinely useful ideas here are *terminal
capability detection* (do not emit escapes when the output is redirected to a file, and honour
`NO_COLOR`) and *graceful downsampling to no-color*. Both are a handful of lines against
`Console.IsOutputRedirected` and an env-var check — not worth a .NET CLI dependency in a Unity
project.

**Dependencies / cross-links:** `run-validation-suite` skill documents how the summary is read;
update it if the batch output format changes. `VALIDATION_SUITE_COVERAGE_ROADMAP.md`.

---

## Document History

* **v1.0** - Initial report — harvested from the 2026-08-05 Cysharp library evaluation; all seven
  libraries rejected as dependencies (§2), seven transferable ideas filed as `TP-2`…`TP-8`. `TP-1`
  was found to duplicate `SL-1` during the audit and is kept only as a cross-reference plus two
  corrections of record.

---

**Last Updated:** 2026-08-05
**Next Review:** When a profiler capture is next taken on the chunk streaming path (gates `TP-1`
and `TP-4`), or when the settings/HUD binding layer grows enough to revisit R3 (§2).
