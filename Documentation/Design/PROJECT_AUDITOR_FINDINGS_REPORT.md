# Project Auditor Findings Report

**Version:** 1.2  
**Date:** 2026-09-17  
**Status:** **Open backlog.** Items are removed (archived) when implemented and verified.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> The backlog for findings raised by **Project Auditor** (`AU-*`) — Unity's static analyzer, run
> against the whole project rather than one system. The single most important result is a negative
> one: of 17,873 issues, the 11,098 "Code" issues are **almost entirely cold-path** (15 sit inside a
> per-frame Unity message, and every one of those is already gated), so this report is not a
> performance backlog. The real items are build size, two project settings, and the statics-cleanup
> annotations (132 reported hits, 92 annotated members).

**Audited:** 2026-09-17, at commit `39141630` (branch `chore/project-auditor-analysis`).
Findings are from the saved report `_REFERENCES/Minecraft Clone_2026-09-17-19-44-27.projectauditor`
(rules package 2.0.0, Unity 6000.6.1f1, platform StandaloneWindows64, Release code optimization),
parsed in full rather than skimmed in the window. Every item's "What exists today" was **verified
against current code, assets and the live editor** — import settings via `AudioImporter`, project
settings via `PlayerSettings`/`EditorSettings` over Unity MCP, font wiring by resolving `.meta`
GUIDs, and the two flagged `Update` call sites by reading them. Where the auditor's own claim did
not survive that check, it is recorded in §3 as a non-finding rather than silently dropped.

⚠️ **The counts here are a local point-in-time snapshot.** `_REFERENCES/` is git-excluded
(`.git/info/exclude`), so the `.projectauditor` file every number derives from does **not** travel
with the repo, and neither does the post-fix re-run. Treat the figures as evidence of what was true
on 2026-09-17 rather than as reproducible fixtures: a fresh run (`Window/Analysis/Project Auditor`,
or the API — §4) takes ~54 s and regenerates all of them.

**Relationship to other documents:**

- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — the master
  performance backlog (`MR-`/`LI-`/`TG-`/`P-`/`GS-`). Auditor allocation counts are **not** inputs
  to it; see §3.1 for why.
- [`CODEBASE_IMPROVEMENTS.md`](CODEBASE_IMPROVEMENTS.md) — non-performance cleanup backlog; `AU-2`
  is the domain-reload-annotation counterpart of its API-modernization items.
- [`SOUND_ENGINE_DESIGN.md`](SOUND_ENGINE_DESIGN.md) — the shipped audio system whose authored gains
  `AU-5` would have touched (it does not: gains are measured from source files, which import
  settings never modify).
- [`OPEN_WORK_INDEX.md`](OPEN_WORK_INDEX.md) — the per-document map this report is listed in.

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

**This table is the report's ID index — the whole `AU-` space, open and closed.** A completed item
keeps its row (marked ✅) even after its detail section is archived; a declined one is marked ⛔ and
kept so the same finding is not re-proposed from the next auditor run.

| ID                | Finding                                                                                       | Effort | Risk | Benefit | Seed | Save |
|-------------------|-----------------------------------------------------------------------------------------------|:------:|:----:|:-------:|:----:|:----:|
| **AU-1** ✅        | Static Batching is on with zero static objects → 7× `URP0302` SRP-Batcher conflict            |   🟢   |  🟢  |   🟡    |  ✅   |  ✅   |
| **AU-2** ✅        | 132 `UAL0010`/`UAL0013` hits: 45 runtime types lack a statics-cleanup attribute               |   🟡   |  🟡  |   🟢    |  ✅   |  ✅   |
| **AU-3** ✅        | `CLAUDE.md`/`AGENTS.md` state Reload Domain is disabled; it is enabled (`PAS0036`)            |   🟢   |  🟢  |   🟢    |  ✅   |  ✅   |
| **AU-4** ⛔        | Monocraft font atlases are 129 MB of the 341 MB asset payload — declined 2026-09-17           |   🟡   |  🔴  |   🟡    |  ✅   |  ✅   |
| **AU-5** ⛔        | Music imports at Vorbis quality 1.00 → 104 MB — declined 2026-09-17                           |   🟢   |  🟡  |   🟡    |  ✅   |  ✅   |
| **AU-6**          | 4 orphan FiraCode weight variants (~22 MB); `FiraCode.asset` itself is Monocraft's fallback    |   🟢   |  🟡  |   ⚪    |  ✅   |  ✅   |
| **AU-7**          | 27 assets under `Assets/Resources/` — always built, always loaded (`PAA3000`)                 |   🔴   |  🟡  |   🟡    |  ✅   |  ✅   |
| **AU-8**          | `PAS0037`: Direct3D11 precedes Direct3D12 in the Windows graphics API list                     |   🟢   |  🟡  |   🟡    |  ✅   |  ✅   |
| **AU-9**          | `PAS0013`/`PAS0015`: both layer collision matrices have every box ticked                      |   🟢   |  🟡  |   ⚪    |  ✅   |  ✅   |
| **AU-10**         | Settings long tail: mipmap streaming/stripping, texture quality, async upload, prebake meshes |   🟢   |  🟢  |   ⚪    |  ✅   |  ✅   |

---

## 2. Detail sections

### AU-1 — Static Batching is enabled and cannot pay off

**Classification:** Core settings fix.

**What exists today:** `ProjectSettings/ProjectSettings.asset:398` has `m_BuildTargetBatching` →
`Standalone` → `m_StaticBatching: 1`, `m_DynamicBatching: 0`, confirmed live
(`PlayerSettings.GetBatchingForPlatform(StandaloneWindows64)` → `1/0`). Meanwhile **every**
GameObject is non-static: `m_StaticEditorFlags: 0` appears 81 times across `MainMenu.unity` and
`World.unity`, and **202 times across every scene and prefab** — with no other value anywhere.
Chunk meshes are generated at runtime, so nothing is a static-batching candidate by construction.

The auditor reports the consequence 7 times as `URP0302` ("Static Batching conflicts with SRP
Batcher") — once for `VoxelEngine-URP-Asset.asset` and once per quality-level pipeline asset (Very
Low … Ultra).

**Gap / finding:** the setting costs SRP-Batcher compatibility for objects that *could* batch (UI,
skybox, debug renderers) and buys nothing back, because there is no static geometry to pre-batch.

**Proposal:** set static batching off for Standalone through `PlayerSettings`, not by hand-editing
YAML.

⚠️ **The build-profile trap.** `Assets/settings/Build Profiles/Windows - Profiler.asset` carries a
**full frozen copy** of `PlayerSettings` in `m_PlayerSettingsYaml` (971 lines), including
`m_StaticBatching: 1` at its line 425. A project-level change does **not** reach builds made with
that profile. `Windows - Development.asset` and `Windows - Production.asset` have
`m_PlayerSettingsYaml: m_Settings: []` and do inherit. So the fix is two places, and leaving the
Profiler copy stale would make profiler captures measure a different renderer configuration than
dev and production builds.

**✅ Shipped 2026-09-17.** `PlayerSettings.SetBatchingForPlatform(StandaloneWindows64, 0, 0)` plus a
one-line edit of the Profiler profile's frozen copy (`m_StaticBatching: 1` → `0`; verified as a
1-line diff, and the asset still loads as `UnityEditor.Build.Profile.BuildProfile` after a forced
reimport). Before touching it, the frozen copy was diffed against live `PlayerSettings`: it differs
in exactly three values — this batching flag, an inert `openGLRequireES31`, and
`managedCodeVariant: Standalone: 1`, which is the profile's deliberate profiler variant and was left
alone.

**Verification (measured):** `GetBatchingForPlatform` reads back `0/0`, and a full auditor re-run
reports **`URP0302` 0 times, down from 7**.

**Dependencies / cross-links:** `reference_buildprofile_playersettings_override` (the frozen-copy
behavior); `perf-benchmark` if a frame-time claim is ever attached — none is made here, the claim is
"the conflict is gone", not "N ms faster".

---

### AU-2 — 45 runtime types lack a statics-cleanup attribute

**Classification:** Core. Iteration time (`UAL*` rules are filed under the auditor's
`IterationTime` area), plus a convention future sessions copy.

**What exists today:** 132 `DomainReload`-category issues land in project code (88 `UAL0013`,
44 `UAL0010`) across **45 files**, all under `Assets/Scripts/` — none in `Assets/Editor/`. The
message is *"A type with static fields, automatic properties or events must have an
AutoStaticsCleanup or NoAutoStaticsCleanup attribute"*, and `UAL0010` additionally names the
members (e.g. `DeviceCalibration.cs:63` → `s_loadArmFaults, Instance`). Density leaders:
`Benchmarks/PipelineTelemetry.cs` (10), `Physics/PhysicsQueryStats.cs` (8),
`Benchmarks/BenchmarkTourCoverage.cs` (7), `Data/WorldLaunchState.cs` (7),
`DebugVisualizations/ChunkBorderVisualizer.cs` (6), `SectionRenderer.cs` (6).

The attributes exist in this editor and are reachable from project code, both verified rather than
assumed:

- `Unity.Scripting.LifecycleManagement.AutoStaticsCleanupAttribute`,
  `NoAutoStaticsCleanupAttribute` and `AutoStaticsCleanupOnCodeReloadAttribute` all resolve at
  runtime via `Type.GetType(... , "Unity.Scripting")` in the live editor.
- `Assembly-CSharp.csproj:552` already references `Unity.Scripting.dll`, so no asmdef or package
  change is needed.
- Unity's own built-in packages use them in 57 files — 159 `[NoAutoStaticsCleanup]` and 39
  `[AutoStaticsCleanup]` occurrences — and every one of those is **field-level**, with
  `using Unity.Scripting.LifecycleManagement;`. Opt-outs there always carry a one-line reason
  ("Cache for reflection data, no need to reset", "Manually cleaned up via Cleanup()").

This sits **beside**, not instead of, the existing convention: 39 files already run a
`[RuntimeInitializeOnLoadMethod]` reset (`World.DomainReset`, `PipelineTelemetry.DomainReset`, …),
which is what Rider's `UDR0004`/`UDR0005` analyzer requires. Adopting the attributes does not
retire those methods.

**Gap / finding:** with domain reload currently **on** (see `AU-3`) the statics do reset, so today
this is latent rather than broken. It becomes real the moment *Enter Play Mode → Reload Domain* is
disabled for faster iteration — at which point 45 unannotated types decide their own leak behavior.
Annotating now also means the list stops growing: every new type with a mutable static adds rows to
the next auditor run.

**Proposal:** annotate every flagged static, field-level, choosing per member:

- `[AutoStaticsCleanup]` — the default for anything holding **session state**: counters, caches,
  queues, singleton back-references, event lists.
- `[NoAutoStaticsCleanup]` — only with a stated reason, matching Unity's own house style: immutable
  lookup tables, reflection caches, or state deliberately owned by an explicit
  `Dispose`/`Cleanup` path.
- `const`, `readonly` and `[ThreadStatic]` members are out of scope (never mutated across
  sessions).

Two questions must be settled **empirically before the sweep**, because the answers change what is
safe to annotate:

1. **Does `[AutoStaticsCleanup]` reset to `default(T)` or re-run the field initializer?** Unity's
   own comments say "cleared"/"cleaned up", which implies `default(T)`. If so, any annotated field
   with an initializer (`= new Dictionary<…>()`) becomes `null` on the second play session, and the
   type needs its existing `[RuntimeInitializeOnLoadMethod]` reset to rebuild it — an NRE hazard if
   assumed the other way.
2. **Is type-level placement legal?** The rule text says "a *type* … must have", but all 198
   built-in usages are field-level (the single type-level occurrence found is commented out). A
   one-file compile probe settles it; the sweep should not guess.

**✅ Shipped 2026-09-17 — both questions answered first, and the second answer inverted the policy.**

1. **Cleanup is `ResetToDefaultValue`.** `Unity.Scripting.dll` carries `ResetToDefaultValue`,
   `CleanupStrategy`, `ClassAutoCleanup` and `RegisterAutoCleanup` — so `[AutoStaticsCleanup]` zeroes
   the member; it does **not** re-run the field initializer (that lives in `.cctor`, which fast enter
   play mode never re-runs).
2. **Placement is unconstrained.** A compile probe accepted the attribute on a type, a field, an
   auto-property, an event and a `readonly` field.

Measuring reset coverage then settled the direction: **78 of the 88 flagged statics are already
assigned inside their type's `[RuntimeInitializeOnLoadMethod] DomainReset`**, and several of those
resets express intent `ResetToDefaultValue` cannot — `WorldLaunchState` restores authored defaults
(`"New World"`, `IsNewGame = true`), `PipelineTelemetry` restores `MIN_TRACE_CAPACITY`, `readonly`
containers are `.Clear()`ed, and `FluidBlockLookup.s_generation`'s correct reset is an **increment**
(zeroing it would let a section stamped by the previous session pass as freshly counted). Handing
those to automatic cleanup would have been a regression, and the ordering between cleanup and
`RuntimeInitializeOnLoadMethod` is unverified.

So the adopted policy is **`[NoAutoStaticsCleanup]` with a per-member reason** — the attribute
documents an explicit opt-out rather than re-homing verified behavior, exactly as Unity's own
packages use it (159 opt-outs vs 39 opt-ins). **92 members annotated across 45 files.** Reason
tally, re-derived by grep rather than retyped:

| Reason                                     | Count | Notes                                                                                   |
|--------------------------------------------|------:|-----------------------------------------------------------------------------------------|
| `// reset in <the type's reset method>`    |    74 | The method name varies — `DomainReset` (40), `ResetStatics` (12), `ResetSaveProbeCounters` (7), `Reset` (7), `ResetStaticState` (4), `ResetOnPlayModeEnter` (2), `ResetFloatPrecisionTripwire` (1), `ResetDeserializeProbeCounter` (1) |
| `// contents cleared in <method>`          |     7 | `readonly` containers whose CONTENTS are the session state (6 `DomainReset`, 1 `Clear`)  |
| `// immutable table`                       |     8 | `readonly` data built once, never mutated                                               |
| Three specific reasons                     |     3 | the `s_generation` increment; `SectionRenderer.s_materialCombinations` (cache keyed by the `s_materialCacheVersion` its reset zeroes); `BlockBehavior.s_tMods` (per-thread, cleared before every use) |

**The reason must name the type's actual reset method.** The first pass wrote
`// reset in DomainReset` everywhere and was wrong in 15 files whose reset is named something else,
which sends a reader grepping for a method that does not exist in that file. Two members also had
reasons that did not survive scrutiny: `BlockBehavior.s_tMods` is `[ThreadStatic]`, so its
`DomainReset` nulls only the **calling** thread's slot — the reason that actually holds is that
`PerformBlockBehavior` calls `Mods.Clear()` before every use (`BlockBehavior.cs:146`) and the
accessor re-creates the list lazily.

One behavior change came out of it: `SerializationBufferPool` had **no** reset, and its
`ConcurrentBag` of 256 KB buffers is unbounded, so it gained a `DomainReset` that drains the pool —
otherwise the previous session's buffers would be retained once domain reload stops clearing them.

**Verification (measured):** both csproj targets build with 0 errors; Unity's own compile is clean
(no `error CS` in the editor log, no console errors); a full auditor re-run reports
**`UAL0010` 4 and `UAL0013` 8, all in packages and `0` under `Assets/` — down from 132** (total
issues 17,873 → 17,745); Rider `lint_files` on a sample of touched files raises no new `UDR000*` and
no redundant-attribute warning.

⚠️ **The auditor cannot see the whole sweep.** It audits with **Release** code optimization, which
compiles out `#if UNITY_INCLUDE_INSTRUMENTATION` — so its `0 under Assets/` is a release-configuration
result. Four statics live in exactly that branch (`ChunkStorageManager.cs:41-44`, the CP-6/CP-3 fault
-injection seams) and were annotated from a code scan, not because the auditor reported them. Their
gate is the compiler instead: `Assembly-CSharp.csproj` **does** define
`UNITY_INCLUDE_INSTRUMENTATION`, so `dotnet build` compiles that branch and a bad annotation there
fails the build. Anyone re-running the auditor on a dev/instrumented configuration should expect
those four to be already covered.

**Statics cleanup is opt-in per member — so these annotations are documentation, not behavior.**
Measured across four assemblies: Unity's own carry an ILPP-generated aggregate
(`UnityEngine.CoreModule.dll` and `UnityEditor.CoreModule.dll` have
`__AutoStaticsCleanup_..._CodeLoadedScope_Exiting`; `Unity.Collections.dll`, which has 39
`[AutoStaticsCleanup]` opt-ins, has `__AutoStaticsCleanup_UnityEngine_PlayModeScope_Both`), while our
freshly-built `Assembly-CSharp.dll` has **no such method at all** — only the attribute reference,
because the sweep added zero opt-ins. If cleanup were the default, every assembly would carry one.
The practical consequence is that the ~133 unannotated `static readonly` reference/array statics
(`VoxelData.VoxelVerts`, `SectionRenderer.Layout`, …) are **not** at risk of being zeroed, and no
sweep of them is warranted.

⚠️ **Still unproven:** that the cleanup *fires* as intended under fast enter play mode, and that the
generated-method evidence above holds for player/IL2CPP builds (only editor-built assemblies were
inspected). A green auditor proves the attributes are present, not that the mechanism behaves. Since
every annotation is an opt-out whose reset is an already-working reset method, nothing depends on it
today — but if *Enter Play Mode → Reload Domain* is ever disabled (`AU-3`), the honest gate is
entering play twice and logging one annotated counter per type family **plus one unannotated
`readonly` table**, which is what would falsify the opt-in reading.

**Dependencies / cross-links:** `AU-3` (same subsystem, and its doc fix should describe whichever
convention this item lands); `CLAUDE.md` §"Unity Static Fields & Domain Reload".

---

### AU-3 — The domain-reload premise in the agent instructions is false

**Classification:** Core. Rulebook correctness.

**What exists today:** `CLAUDE.md` (and its hand-synced twin `AGENTS.md`) states: *"With Enter Play
Mode → Reload Domain disabled (this project's setup), statics are **not** re-initialized between
play sessions, so a stale value leaks into the next run."* The live editor reports
`EditorSettings.enterPlayModeOptionsEnabled = False` and `enterPlayModeOptions = None`, i.e. domain
**and** scene reload both run on play — statics *are* re-initialized. The auditor reports the same
thing from the other side as `PAS0036` ("Domain Reload is enabled when entering Play Mode").

A second, smaller inconsistency: `ProjectSettings/EditorSettings.asset:29` has
`m_EnterPlayModeOptionsEnabled: 1` while the live editor reports `False`. The file and the running
editor disagree; the live value is what plays.

**Gap / finding:** the *rules* the section prescribes (reset every mutable static on play-mode
entry; one `[RuntimeInitializeOnLoadMethod]` per class) are still good practice and are what
`UDR0004`/`UDR0005` enforce — but their stated **justification** is wrong, which makes the section
impossible to reason about when deciding whether `AU-2`'s annotations matter.

**Proposal:** correct the premise in both twins, keep the rules, and state the actual reason they
hold (they are what makes disabling domain reload *possible*, and what the analyzers require).
Mention `UAL0010`/`UAL0013` next to the existing `UDR0004`/`UDR0005` reference once `AU-2` decides
the convention. Both twins are edited and staged together.

**✅ Shipped 2026-09-17.** Both twins now state the measured setting and why the rules still hold,
and the section gained the `AU-2` convention (attribute names, the `Unity.Scripting.LifecycleManagement`
using, the `[NoAutoStaticsCleanup]`-with-a-reason default and when `[AutoStaticsCleanup]` is right),
pointing here for the worked example. `PAS0036` still fires — deliberately: the setting was not
changed, only the documentation that misdescribed it.

The `EditorSettings.asset` vs live-editor disagreement is **left as found** and not reconciled: the
file says `m_EnterPlayModeOptionsEnabled: 1`, the editor reports `False`. Worth an owner glance,
since whichever wins decides whether `AU-2`'s annotations are load-bearing.

**Dependencies / cross-links:** `AU-2`; `reference_claude_agents_twin_files`; `docs-sync` is not
involved (neither twin is under `Documentation/`).

---

### AU-4 ⛔ — Monocraft font atlases (129 MB) — declined 2026-09-17

**What exists today:** `Assets/Fonts/Monocraft/` ships 8 font assets of ~34 MB each; in the measured
build they are **129.2 MB across 32 files** — the largest single asset group, ahead of the 104 MB of
music and the 64 MB block atlas. Each has `m_AtlasWidth/Height: 4096`, `m_AtlasPopulationMode: 0`
(static), `m_IsMultiAtlasTexturesEnabled: 0`, 1,475 glyphs, at sampling point size **121**.

The obvious "delete the unused variants" move is **wrong**, and this is the part worth keeping:
6 of the 8 have zero scene/prefab references, but `Monocraft.asset`'s `m_FontWeightTable`
(line 36924 ff.) points at every one of them, and the code uses `<b>`/`<i>` rich-text tags. They are
the weight faces, not orphans. `Monocraft.asset` itself is the TMP default font
(`TMP Settings.asset:27`) with 29 referrers.

**Verdict — ⛔ declined (owner decision, 2026-09-17).** Atlas regeneration on this project is
finicky: the fonts turn boxy or soft at different resolutions and font sizes, and the current
settings render correctly in the common cases. The ~96 MB was real but the risk is to text
legibility everywhere, and regeneration would additionally add ~280 MB of fresh blobs to git history
(these assets are tracked in-repo; Git LFS is deliberately scrubbed). Do not re-propose atlas
regeneration or weight-variant deletion from a future auditor run.

---

### AU-5 ⛔ — Music Vorbis quality 1.00 (104 MB) — declined 2026-09-17

**What exists today:** 17 tracks under `Assets/Audio/Music/pizzadoggy_cozy_tunes/` total
**103.8 MB** in the measured build. Verified live via `AudioImporter`: `loadType = Streaming`,
`compressionFormat = Vorbis`, `quality = 1.00`, `preloadAudioData = 0`, `loadInBackground = 1`, no
platform override (so `defaultSampleSettings` is the lever). Example: *Floating Dream* is 247.6 s,
stereo, 44.1 kHz, 11.25 MB.

Re-importing at a lower quality would **not** disturb the sound engine's authored gains:
`AudioLoudnessAnalyzer.Measure` runs ffmpeg against the **source** `.ogg` file, and import settings
never modify sources. That was the plausible-looking coupling, and it does not exist.

**Verdict — ⛔ declined (owner decision, 2026-09-17).** Music stays at highest quality; the ~40–50 MB
is not worth trading audible fidelity for. Note for any future revisit: the knob is one float on
`defaultSampleSettings`, fully reversible, and the honest gate is a fresh player build — the
auditor's size data comes from the build report, not from the project.

---

### AU-6 — Four orphan FiraCode weight variants (~22 MB)

**What exists today:** `Assets/Fonts/FireCode/` contributes **22.4 MB across 20 build files**.
`FiraCode.asset` is **live** — it is the single entry in `Monocraft.asset`'s
`m_FallbackFontAssetTable` (guid `ed930d1a…`), i.e. the fallback for codepoints Monocraft's static
atlas lacks. The other four (`FiraCode-Bold`, `-Light`, `-Medium`, `-SemiBold`) have **zero**
referrers anywhere in `Assets/` once the fonts folder itself is excluded, and no name-based
reference in code.

**Gap / finding:** ~18 MB of build payload for four faces nothing points at. Unlike `AU-4` this
needs no regeneration — it is a deletion.

**Proposal:** owner decision, not a mechanical cleanup: content deletion is curated by the project
owner, and a font that is unreferenced today may still be intended for a future UI. If accepted,
delete through the `unity-file-ops` skill (asset + `.meta` together), one commit, then confirm the
console shows no missing-script/GUID errors and `Monocraft.asset`'s fallback entry still resolves.

**Dependencies / cross-links:** `AU-4` (same subsystem, opposite verdict — that one is regeneration,
this one is deletion); `feedback_missing_content_ask_first`.

---

### AU-7 — 27 assets under `Assets/Resources/`

**What exists today:** `PAA3000` flags 27 assets in `Resources` folders; 20 of them appear in the
build file list. They include the databases the engine loads by path — `Data/BlockDatabase.asset`,
`Data/AmbienceDatabase.asset`, `Data/MusicMetadataLibrary.asset`, `CreditsDatabase.asset`, the
`BlockTagPresets/*` set and `FluidData/*` — plus TextMesh Pro's own `Resources` payload, which is
not ours to move.

**Gap / finding:** everything under `Resources` is included in the build unconditionally and
contributes to startup load time, whether or not a given world needs it. The cost here is modest
(these are small ScriptableObjects), so this is a structural note, not a size win.

**Proposal:** no action for now. If it is ever taken up, the change is to reach these assets through
direct serialized references or Addressables instead of `Resources.Load`, which touches block-data
and audio-database loading — a refactor with its own design, not an auditor follow-up. TMP's
`Resources` content stays regardless.

---

### AU-8 — Direct3D11 precedes Direct3D12 on Windows

**What exists today:** `PAS0037`. Live: `PlayerSettings.GetGraphicsAPIs(StandaloneWindows64)` =
`Direct3D11, Direct3D12, Vulkan` with `GetUseDefaultGraphicsAPIs = False`, so the order is
deliberate, not inherited. The auditor's advice in rules 2.0.0 is order-based (it was
presence-based before), and it wants D3D12 first.

**Gap / finding:** D3D12 can reduce CPU driver overhead on a draw-call-heavy renderer, which this
is. It can equally regress: different driver paths, different shader compilation behavior, and this
project's whole perf history is measured on D3D11.

**Proposal:** do **not** flip it as a settings tidy-up. It is a measured change: capture a baseline,
reorder, re-capture on the same scene and the same fps-matched run per the `perf-benchmark` protocol,
and keep it only on a frame-level win. Until then the current order is the known-good one.

**Dependencies / cross-links:** `perf-benchmark`; `reference_benchmark_noise_floor` (±1–5%, so a
small delta proves nothing).

---

### AU-9 — Both layer collision matrices have every box ticked

**What exists today:** `PAS0013` (Physics) and `PAS0015` (Physics 2D). Every layer pair can collide.

**Gap / finding:** broad matrices cost broadphase work. But this engine's voxel collision is custom,
the 2D matrix is irrelevant to a 3D game (nothing uses 2D physics), and the NS-4 physics suite's 44
baselines encode current collision behavior — narrowing the matrix is a behavior change that suite
would (correctly) notice.

**Proposal:** low priority. The 2D matrix can be narrowed safely because nothing reads it; the 3D
one should only change alongside a physics-layer audit, with `Validate Physics Solver` green before
and after.

---

### AU-10 — Settings long tail

**What exists today**, all verified live or in `ProjectSettings`:

| Rule                | Finding                                             | Assessment                                                                                                  |
|---------------------|-----------------------------------------------------|-------------------------------------------------------------------------------------------------------------|
| `PAS1007` ×5        | Mipmap streaming off on 5 quality levels            | Largely moot: the block atlas has `enableMipMap: 0`, so there are no mips to stream                         |
| `PAS0019`           | Texture Quality not Full Res                        | Deliberate quality-level scaling; check only if texture quality looks wrong at Ultra                        |
| `PAS0027`           | Mipmap Stripping disabled (`mipStripping = False`)  | Would shrink builds only for mipped textures; see above                                                     |
| `PAS0020`/`PAS0021` | Async Upload Time Slice / Buffer Size at defaults   | Worth a look only if streaming hitches appear; both are frame-time knobs, so measured, not tidied           |
| `PAS0007`           | Prebake Collision Meshes disabled                   | Applies to imported meshes; this project builds collision at runtime                                        |
| `PAS0016`           | Fixed Timestep at default                           | Physics cadence is a gameplay decision, not an auditor one                                                  |
| `PAS1004`           | IL2CPP Compiler Configuration = `Master`            | Deliberate: slowest build, fastest runtime, which is what RC captures want                                  |
| `PAS0029`           | Splash Screen enabled                               | Not disableable on this license tier                                                                        |

**Proposal:** no action as a group. Rows exist so a future auditor run can be diffed against a
recorded assessment instead of re-triaged from scratch.

---

## 3. Non-findings — verified, do not re-litigate

The auditor's headline counts are dominated by issues that are **not** defects here. Each was
checked, not waved away.

### 3.1 The 11,098 "Code" issues are cold-path

Split by assembly: **2,590** in `Assembly-CSharp` (runtime), **8,508** in
`Assembly-CSharp-Editor`, 144 domain-reload rules (→ `AU-2`). The runtime top rules are 738 boxing
allocations, 706 object allocations, 354 `String.Concat`, 279 array allocations.

Mapped to their enclosing methods, only **15** runtime hits sit inside a per-frame Unity message,
and all of them are already benign:

- **11** in `DebugVisualizations/TerrainGenDebugOverlay.cs::OnGUI` — a debug overlay that
  early-returns on `!_isActive` (plus one `PAR0022` "OnGUI is discouraged" for the method itself).
- **2** in `World.cs::Update` at line 2696 — a `Debug.Log` behind `failSafePromoted > 0 &&
  settings.enableDiagnosticLogs`, so the interpolated string is built only when diagnostics are on
  *and* the fail-safe fired.
- **2** in `UI/WorldUIManager.cs::Update` at line 192 — a `LogWarning` inside the UI_BUGS #04
  recovery branch, i.e. only when a tracked UI was destroyed underneath the manager.

Two structural limits make the remaining ~2,575 unusable as a perf signal: the auditor reports **no
call context** (an allocation in a cold init method and one in a hot inner loop look identical), and
it cannot see inside **Burst-compiled jobs**, which is where this engine's hot path actually lives.
Use the profiler and `perf-benchmark`; do not mine these counts.

The editor-assembly 8,508 are irrelevant by definition — editor code does not ship, and allocation
in a tool window costs nothing a player sees.

### 3.2 The 38 read/write-enabled textures are editor-only

`PAA0002` flags 38 textures; all 38 are under `Assets/Editor/AtlasPacker/SourceTextures/`. The
build file list contains **0** entries under `Assets/Editor` — the atlas packer's inputs never ship,
and read/write is what the packer needs. Non-issue.

### 3.3 Audio threshold rules mis-fire on this content

`PAA4000` ("long clip not set to streaming") flags 16 clips including
`Footsteps_Water_Run_001.ogg`, which is **1.2 s** long; `PAA4001` ("short clip is set to
streaming") flags 17 music tracks that are **minutes** long and correctly streaming. Both rules are
byte-threshold based (`StreamingClipThresholdBytes = 218294`,
`LongDecompressedClipThresholdBytes = 204800`), so mono 48 kHz footsteps cross the "long" line on
decompressed size while streamed music trips the other rule. `Assets/Audio/Blocks` totals **2.0 MB**
across 832 build files, so the memory at stake is negligible either way. Ignore this family unless
the sound engine's own listening passes raise something.

### 3.4 Package advisories that contradict deliberate pins

`PAP0002`/`PAP0001` flag `com.unity.ai.assistant` 2.6.0-pre.1 as preview and updatable to
2.9.0-pre.2 — that pin is load-bearing (the patched build is what keeps `Unity_RunCommand`
working, and later versions gate the MCP bridge behind entitlements). `PAP0003` suggests
*downgrading* `com.unity.ide.rider` (3.1.0 → 3.0.40) and `com.unity.project-auditor-rules`
(2.0.0 → 1.0.3) to Unity's "recommended" versions, both of which are older than what is installed
on purpose. Ignore all four.

### 3.5 The 64 MB block atlas is deliberate

`Assets/Textures/packed_texture_atlas.png` is 4096², uncompressed (default-platform
`textureCompression: 0`), `enableMipMap: 0`, `filterMode: 0` (point), `isReadable: 0` → exactly
64 MB. That is what crisp pixel-art voxel faces require; block-compression artifacts on a
tightly-packed atlas would bleed between tiles. Not a finding.

---

## 4. Re-running the analysis

- **Live**: `Window/Analysis/Project Auditor` exists in this editor (6.6 bundles the module as
  `UnityEditor.ProjectAuditorModule`). The window's grid is not queryable over MCP, so the saved
  report file is the practical interface for analysis.
- **The saved file** is two header lines (`PROJECT_AUDITOR_REPORT`, format version `1.3`) followed by
  one JSON document: `m_Issues` (flat list, each with `descriptorId`, `category`, `severity`,
  `location.path`/`line`, `properties`) and `m_DescriptorLibrary.m_SerializedDescriptors` (300
  rule definitions). Parsing it with Python beats reading the window; enclosing-method attribution
  has to be reconstructed locally, and the reconstruction used for §3.1 agreed with the auditor's own
  method names on 124 of 188 cross-checkable hits (the rest resolve to `null` on async/iterator
  bodies), so per-method counts are **indicative, not exact** — the per-frame set above was then
  confirmed by reading each site.
- ⚠️ **Build-size data is as old as the last build.** Every MB figure in this report comes from the
  build report of the **2026-09-01 RC 92 [DEV]** build (`Total Size 1.13 GB`, 10 min 18 s,
  StandaloneWindows64, Succeeded), not from the project as it stands. Asset-size claims only refresh
  after a new player build.

---

## 5. Rejected alternatives

| Option                                                              | Verdict                                                                                                                  | Date       |
|---------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------|------------|
| Regenerate the Monocraft atlases smaller (all 8, or the 7 weights)  | ⛔ Rejected — atlas regeneration renders boxy/soft at other resolutions and sizes; ~280 MB of new git blobs (`AU-4`)      | 2026-09-17 |
| Delete the "unreferenced" Monocraft weight variants                 | ⛔ Rejected — they are not unreferenced; `Monocraft.asset`'s `m_FontWeightTable` points at all 6 and the code uses `<b>`  | 2026-09-17 |
| Lower music Vorbis import quality to ~0.6                           | ⛔ Rejected — audible fidelity outranks ~40–50 MB (`AU-5`)                                                                | 2026-09-17 |
| Treat the auditor's allocation counts as a performance backlog       | ⛔ Rejected — no call context, blind to Burst jobs, 15/2,590 per-frame and all gated (§3.1)                               | 2026-09-17 |
| Fix `PAA0002` read/write textures                                    | ⛔ Rejected — editor-only assets, 0 in the build (§3.2)                                                                   | 2026-09-17 |
| Flip Direct3D12 ahead of Direct3D11 as a settings tidy-up            | ⛔ Rejected as an unmeasured change; kept as the measured item `AU-8`                                                     | 2026-09-17 |

---

## Document History

* **v1.2** - Post-review corrections (2026-09-17). The `AU-2` reason tally was wrong (said 9 immutable,
  summing to 89) and is now a grep-derived table; `// reset in DomainReset` was **inaccurate in 15 files**
  whose reset method is named otherwise, and every comment now names the real method; four
  `#if UNITY_INCLUDE_INSTRUMENTATION` statics the Release-configuration auditor cannot see were annotated,
  and the "0 under `Assets/`" claim is scoped accordingly. The readonly-statics hazard raised in review is
  **refuted by measurement**: cleanup is opt-in per member, so the ~133 unannotated `readonly` statics need
  no sweep. Header now states the counts are a local snapshot, since `_REFERENCES/` is git-excluded.

* **v1.1** - `AU-1`, `AU-2` and `AU-3` shipped the same day the report was filed (2026-09-17), and
  `AU-2`'s policy **inverted** during implementation: measuring reset coverage showed 78 of 88 statics
  already reset explicitly, several in ways `ResetToDefaultValue` cannot express, so the sweep adopted
  `[NoAutoStaticsCleanup]`-with-a-reason instead of handing reset duty to automatic cleanup.
* **v1.0** - Initial report

---

**Last Updated:** 2026-09-17  
**Next Review:** on the next Project Auditor run, or if *Enter Play Mode → Reload Domain* is disabled
(which makes `AU-2`'s annotations load-bearing and demands the fast-enter-playmode gate)
