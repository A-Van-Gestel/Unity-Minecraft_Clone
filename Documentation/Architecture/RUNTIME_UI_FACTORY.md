# Runtime UI Factory

**Version:** 2.0  
**Date:** 2026-08-15  
**Status:** **Implemented (Stable)** — RUF-1…RUF-3 all shipped and confirmed in game 2026-08-15.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> A shared factory for UI hierarchies this project builds **in code** rather than in a scene or prefab,
> extracted from the benchmark HUD builder and reused by the command console. **The pivotal decision:
> the factory owns the primitives and the blur-material contract, but never the palette** — each
> screen's tints are an independent design choice, and hoisting them into a shared constant would
> silently couple unrelated surfaces.

**Audited:** 2026-08-15, at commit `8c002371` (branch `feat/world-scaling`).
Findings are from static review of `BenchmarkUIBuilder.cs`, `BenchmarkController.cs`,
`FluidStressController.cs`, `ConsoleUI.cs`, and `WorldUIManager.cs`, plus the serialized rects and
canvas scalers in `Assets/Scenes/World.unity`. The console/toolbar overlap in §2 was computed from the
scene's actual anchors and scales, not estimated.

**Amended:** 2026-08-15 — promoted from `Design/` on RUF-3's in-game confirmation. §2 now records the
shipped state, §5 the shipped phases, and §4 the resolution of the console/toolbar overlap.

**Relationship to other documents:**

- [`UI_BLUR_BACKDROP_SYSTEM.md`](UI_BLUR_BACKDROP_SYSTEM.md) — the blur contract this factory wraps;
  its §4 authoring rules are binding on every panel built here.
- [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) — the console whose view is the second
  consumer.
- [`../Bugs/_FIXED_BUGS.md`](../Bugs/_FIXED_BUGS.md) — UI_BUGS **#06**, whose remaining benchmark-HUD
  symptom was closed by §5 phase RUF-2.

---

## 1. Goals & non-goals

### Goals

1. **One implementation of the runtime UI primitives** — canvas, panel, TMP text, button, scrollable
   text area — instead of the current single private copy inside `BenchmarkUIBuilder`.
2. **One place that knows the blur-material contract** — creation, tinting, instance ownership, and the
   flat-color fallback when no blur material is available.
3. **The console gains a blur backdrop** without a scene or prefab edit, preserving the CMD arc's
   standing constraint that the console is built entirely in code.
4. **The benchmark HUD stops floating over the pause menu** (the second half of UI_BUGS #06).

### Non-goals (v1)

- **A general UI framework.** This is a factory for the handful of screens built in code; scene-authored
  UI stays scene-authored.
- **Restyling the benchmark HUD or the console.** Both keep their current look; only the construction
  path changes.
- **Making blurred panels stack.** Impossible without a second blur capture point — see the blur doc's
  §8.

---

## 2. Structure

| Area                             | State                                                                                                                              |
|----------------------------------|--------------------------------------------------------------------------------------------------------------------------------------|
| `RuntimeUIFactory`               | `Assets/Scripts/UI/Builders/`, namespace `UI.Builders`. Static; holds the primitives, the reference resolution, and the blur helpers. |
| `RuntimeUIFactory.CreateCanvas`  | Creates a **new** canvas GameObject. `ConfigureCanvas` is the overload that adds the components to an object the caller already owns. |
| `BenchmarkUIBuilder`             | Composition + palette only; delegates construction. Keeps both public entry points unchanged.                                       |
| Benchmark HUD canvas             | `sortingOrder = -10` — **below** the scene UI canvas (0), so a full-screen scene panel covers it (see §5 RUF-2).                     |
| Benchmark results canvas         | `sortingOrder = 200`; a terminal modal, deliberately above everything.                                                               |
| `FluidStressController.cs:147`   | Calls `CreateResultsScreen` with **no** blur material — the null-fallback path is live in production, not dead defensive code.       |
| `ConsoleUI`                      | Hosts its canvas on its own GameObject via `ConfigureCanvas` at `sortingOrder = 100`; panel backdrop goes through `ApplyBlurBackground`. |
| `TouchControls.cs:371`           | Sets `sortingOrder = 90` directly on its own canvas — above the scene UI, below the console.                                        |
| `ToastManager` canvas            | `sortingOrder = 250` via `ConfigureCanvas` — above the benchmark results modal (200), so toasts draw over every screen including the pause menu. Safe only because every card sets `blocksRaycasts = false`. Adds `UIScaleController`, so cards honour the UI Scale setting.                                        |
| …its blur material               | **One** instance owned by the manager, shared by every pooled card and destroyed in `OnDestroy` — a per-card instance would leak one material per card the session ever built. Cards go through `ApplyBlurBackground`.                                                                                              |
| …its state-dependent backdrop    | The manager polls `WorldUIManager.InUI` and swaps every live card between the blur material and the flat fallback on the transition. A blurred panel *replaces* the UI beneath it rather than compositing over it (blur doc §4.2), so a frosted card at 250 over a dimmed pause screen would paint un-dimmed world. Cards stay visible in both states; only the material changes. |
| Console panel rect               | x 12–692, y 12–452 at a fixed 1920x1080 reference.                                                                                  |
| `Toolbar` rect                   | 218x26 at scale 3, bottom-centre, y 5 → spans x ≈ 633–1287, y ≈ 15–93 in reference pixels.                                           |

**The console/toolbar overlap is real: ~59 reference px** at the default UI scale — see §5's RUF-3
note for how it is resolved. `UIScaleController` scales whichever canvas it is attached to, and
`ConsoleUI` never adds it, so the console's own canvas is left unscaled and the overlap varies with
UI scale rather than being fixed (it disappears at Small). `ToastManager` does add the component,
which is what makes the toast canvas the code-built surface that follows the setting.

Canvas scaler match differs by screen and is deliberate: the benchmark canvases use `0.5` (balanced),
the console uses `0` (width-matched) because it is anchored to the bottom-left corner and height
scaling would drift it away from that corner.

---

## 3. Decision: where the palette lives

The extraction's one genuine judgment call, because it decides whether two screens stay independent.

### Option A — shared palette constants in the factory (rejected)

- ✅ One place to restyle everything; fewer magic colors at call sites.
- ❌ **Couples unrelated surfaces.** The benchmark HUD's `0.7` multiply tint and the scene's `0.415`
  were chosen independently for different jobs. A shared constant means a future benchmark restyle
  silently changes the console, with nothing in either file to hint at it.

### Option B — palettes stay at call sites ✅ **CHOSEN**

The factory takes colors as parameters and holds none. `BenchmarkUIBuilder` keeps
`s_hudBackgroundColor`, `s_resultsOverlayColor`, `s_buttonNormalColor` and friends; `ConsoleUI` keeps
its own. This matches how the two screens already differ and keeps the factory's surface honest: it
knows *how* to build a blurred panel, not *what color* any particular panel should be.

---

## 4. Decision: how per-panel tint is expressed

### Option A — encode tint in `Image.color.rgb` (rejected for now)

- ✅ One shared material asset could serve every tint; no instance management at all.
- ❌ **Unverified color-space behaviour.** Material colors are gamma-converted on upload; UI vertex
  colors on the measured path were not (blur doc §4.3). Whether uGUI's `CanvasRenderer` converts
  `Graphic.color` in a linear project was never measured here — the suite's B4 used a manual mesh, not a
  canvas. Choosing this without measuring would put an unverified assumption under every panel.

### Option B — one material instance per distinct tint ✅ **CHOSEN**

Keep `Image.color` as white-plus-alpha (alpha behaves identically in both color spaces) and vary tint
through the material. The factory hands back the instance and the caller destroys it. This is the
existing `BenchmarkController` pattern and uses only paths that have been measured.

If a future session wants Option A, the prerequisite is a suite scenario that renders through a real
`CanvasRenderer` and measures whether `Graphic.color` is gamma-converted.

---

## 5. Implementation phases (all shipped)

Validation baselines were not added per phase: the blur contract is already pinned by
`Validate UI Blur Render`, and this work is construction plumbing rather than new rendering behaviour.
Each phase's gate was a build plus the aggregate suite staying green (482/482 across 22 suites), and
RUF-2/RUF-3 additionally needed in-game confirmation because no suite covers a built hierarchy.

| Phase     | Scope                                                                                                                                                        | Effort | Depends on |
|-----------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|--------|------------|
| **RUF-1** | Created `Assets/Scripts/UI/Builders/RuntimeUIFactory.cs` (namespace `UI.Builders`) with the primitives lifted verbatim, plus `ConfigureCanvas` for an existing GameObject. Blur helpers: `CreateBlurMaterialInstance` and `ApplyBlurBackground`, the latter returning false when it applied the fallback. | 🟢     | —          |
| **RUF-2** | Rewired `BenchmarkUIBuilder` to delegate, keeping its two public entry points and its palette. Dropped the HUD canvas's `sortingOrder` to -10 so the pause menu covers it. Preserved `FluidStressController`'s null-material path.                                    | 🟢     | RUF-1      |
| **RUF-3** | Wired `ConsoleUI`'s panel backdrop through `ApplyBlurBackground`. Material instance allocated **once** and destroyed in `OnDestroy`.                            | 🟡     | RUF-1      |

### RUF-1 note — why `ApplyBlurBackground` does not allocate

The helper takes an already-created material instance rather than creating one. An
internally-allocating helper cannot serve a caller whose build method re-runs, which is exactly
RUF-3's situation — so creation and application are separate calls.

### RUF-2 note — the HUD's opacity

`BenchmarkUIBuilder` sets `panelImage.color = Color.white` at **alpha 1** on the blur path, while its own
fallback constants record the intended translucency (`s_hudBackgroundColor` a=0.7,
`s_resultsOverlayColor` a=0.85). Alpha stayed at 1: lowering it trades frost for reveal exactly as the
blur doc's §4.1 describes, and the sorting-order change alone closes the reported symptom.

### RUF-3 note — re-entrancy, and the toolbar overlap

`ConsoleUI.BuildPanel` is re-entrant: the UI_BUGS #04 self-heal path (`RebuildMissingChildren`) calls it
again when the panel is destroyed out from under the view. A material instance created inside
`BuildPanel` would therefore leak one material per heal, so it is allocated in `Awake` and destroyed in
`OnDestroy`.

The console panel runs at alpha 1 with the same `0.415` tint as the scene panels, per the blur doc's
§4. Being opaque, it covers the toolbar's leftmost slot region where the two overlap — **accepted
deliberately** (user decision, 2026-08-15) over raising the panel, hiding the toolbar, or leaving the
console flat. The hotbar is inert while the console holds input focus (the Gameplay action map is
disabled), the overlap shrinks to nothing at Small UI scale, and every alternative would have meant a
layout or policy change the arc's non-goals rule out.

---

## 6. Constraint compliance

| Constraint                        | How this design satisfies it                                                                     |
|-----------------------------------|--------------------------------------------------------------------------------------------------|
| No hot-path GC                    | All allocation happens once at UI construction; nothing here runs per frame.                     |
| Pooling                           | Not applicable — these objects live for the screen's lifetime.                                   |
| `[SerializeField] private`        | The factory is static and exposes no inspector surface; no serialized state is introduced.       |
| Directory placement               | `Assets/Scripts/UI/Builders/` per `PROJECT_STRUCTURE.md`'s `Scripts/UI/` rule.                    |
| No scene/prefab edits for console | Preserved — the console still builds everything in code and finds its shader via `Shader.Find`.  |
| Serialization                     | Zero on-disk change. Nothing here reaches a save file.                                           |

---

## 7. Extension roadmap

| Version | Item                                                                                                       |
|---------|-------------------------------------------------------------------------------------------------------------|
| v2      | Vertex-color tinting (§4 Option A) once the `CanvasRenderer` color-space question is measured.               |
| v2      | Fold `TouchControls`' runtime construction into the factory if its primitives turn out to overlap.           |
| v3      | A blurred-graphic-inside-`Mask` path, once anything in the project needs one (blur doc §8).                   |

---

## Document History

* **v1.0** - Initial design, split out of the UI_BUGS #06 fix arc so the factory and console wiring
  survive as a plan rather than as session context.
* **v2.0** - RUF-1…RUF-3 shipped and confirmed in game; promoted from `Design/` to `Architecture/`.
  §2 rewritten to the shipped structure, §5 to the shipped phases, and the console/toolbar overlap
  decision recorded in RUF-3's note.

---

**Last Updated:** 2026-08-15  
**Next Review:** when a third screen adopts the factory, or when §7's vertex-color tinting is measured
