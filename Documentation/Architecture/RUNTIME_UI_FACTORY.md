# Runtime UI Factory Design

**Version:** 1.0  
**Date:** 2026-08-15  
**Status:** Proposed design — not implemented.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> A shared factory for UI hierarchies this project builds **in code** rather than in a scene or prefab,
> extracted from the benchmark HUD builder and reused by the command console. **The pivotal decision:
> the factory owns the primitives and the blur-material contract, but never the palette** — the
> benchmark's tints and the console's tints are independent design choices, and hoisting them into a
> shared constant would silently couple two unrelated surfaces.

**Audited:** 2026-08-15, at commit `8c002371` (branch `feat/world-scaling`).
Findings are from static review of `BenchmarkUIBuilder.cs`, `BenchmarkController.cs`,
`FluidStressController.cs`, `ConsoleUI.cs`, and `WorldUIManager.cs`, plus the serialized rects and
canvas scalers in `Assets/Scenes/World.unity`. The console/toolbar overlap in §2 was computed from the
scene's actual anchors and scales, not estimated.

**Relationship to other documents:**

- [`../Architecture/UI_BLUR_BACKDROP_SYSTEM.md`](../Architecture/UI_BLUR_BACKDROP_SYSTEM.md) — the blur
  contract this factory wraps; its §4 authoring rules are binding on every panel built here.
- [`../Architecture/COMMAND_CONSOLE_SYSTEM.md`](../Architecture/COMMAND_CONSOLE_SYSTEM.md) — the console
  whose view is the second consumer.
- [`../Bugs/UI_BUGS.md`](../Bugs/UI_BUGS.md) — **#06**, whose remaining benchmark-HUD symptom is closed
  by §5 phase RUF-2.

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

## 2. Current state (what exists today)

| Area                          | State                                                                                                                          |
|-------------------------------|--------------------------------------------------------------------------------------------------------------------------------|
| `BenchmarkUIBuilder`          | The only code-built UI library. Mixes three layers: primitives, blur wiring, and benchmark-specific composition + palette.      |
| `BenchmarkUIBuilder.CreateCanvas` | Always creates a **new** GameObject. Unusable by `ConsoleUI`, which adds canvas components to its own object.               |
| Benchmark HUD canvas          | `sortingOrder = 100`; the entire scene UI is one canvas at `sortingOrder = 0`, so the HUD paints over the pause menu.           |
| `FluidStressController.cs:147`| Calls `CreateResultsScreen` with **no** blur material — the null-fallback path is live in production, not dead defensive code.  |
| `ConsoleUI`                   | Builds its own canvas, panel, scroll view, input field and ghost overlay inline. Panel backdrop is a flat `(0,0,0,0.55)` Image. |
| Console panel rect            | x 12–692, y 12–452 at a fixed 1920x1080 reference.                                                                             |
| `Toolbar` rect                | 218x26 at scale 3, bottom-centre, y 5 → spans x ≈ 633–1287, y ≈ 15–93 in reference pixels.                                      |

**The console/toolbar overlap is real: ~59 reference px.** `UIScaleController` rescales only the scene
canvas, not the console's own, so a Large UI scale widens it. A blurred console panel drawn over the
blurred toolbar would punch a hole back to the un-blurred world (blur doc §4.2) — which is why the
console's blur adoption is gated behind that overlap being understood, not merely behind the shader fix.

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

## 5. Phased implementation plan

Validation baselines are not added per phase here: the blur contract is already pinned by
`Validate UI Blur Render`, and this work is construction plumbing rather than new rendering behaviour.
Each phase's gate is a build plus the aggregate suite staying green, and RUF-2/RUF-3 additionally need
in-game confirmation because no suite covers a built hierarchy.

| Phase     | Scope                                                                                                                                                        | Effort | Depends on |
|-----------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|--------|------------|
| **RUF-1** | Create `Assets/Scripts/UI/Builders/RuntimeUIFactory.cs` (namespace `UI.Builders`) with the primitives lifted verbatim, **plus a `CreateCanvas` overload taking an existing GameObject**. Add the blur helpers: `CreateBlurMaterialInstance(multiply, additive)` and `ApplyBlurBackground(Image, multiply, fallbackColor)` returning false when it applied the fallback. | 🟢     | —          |
| **RUF-2** | Rewire `BenchmarkUIBuilder` to delegate, keeping its two public entry points and its palette. Drop the HUD canvas's `sortingOrder` below the scene canvas so the pause menu covers it. Preserve `FluidStressController`'s null-material path.                                    | 🟢     | RUF-1      |
| **RUF-3** | Wire `ConsoleUI`'s panel backdrop through `ApplyBlurBackground`. Material instance allocated **once** and destroyed in `OnDestroy`.                            | 🟡     | RUF-1      |

### RUF-2 note — the HUD's opacity

`BenchmarkUIBuilder` sets `panelImage.color = Color.white` at **alpha 1** on the blur path, while its own
fallback constants record the intended translucency (`s_hudBackgroundColor` a=0.7,
`s_resultsOverlayColor` a=0.85). Whether to bring the blur path in line with those alphas is a look
question, not a mechanical one: lowering alpha trades frost for reveal exactly as the blur doc's §4.1
describes. The sorting-order change alone closes the reported symptom, so alpha may stay at 1.

### RUF-3 note — re-entrancy

`ConsoleUI.BuildPanel` is re-entrant: the UI_BUGS #04 self-heal path (`RebuildMissingChildren`) calls it
again when the panel is destroyed out from under the view. A material instance created inside
`BuildPanel` therefore leaks one material per heal. Allocate it in `Awake` (or lazily, once) and destroy
it in `OnDestroy`.

The console's own tint and alpha should follow the blur doc's §4: the console panel has nothing drawn
above it, so it has no reason to run below alpha 1.

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

---

**Last Updated:** 2026-08-15  
**Next Review:** when RUF-1 starts, or on promotion to Architecture after RUF-3 is confirmed in game
