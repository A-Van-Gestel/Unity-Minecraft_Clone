---
name: game-ui
description: Build, edit, and diagnose in-game (runtime) uGUI + TextMeshPro UI in this engine — menus, HUD, panels, prefabs, layouts, blur backdrops — under its banded in-pipeline UI, RuntimeUIFactory, and data-driven settings contracts. Use when the user asks to "make a menu/screen/panel/HUD", "add a button/slider/list", "fix the layout", or reports UI that is invisible, unclickable, drawn behind something, blank-boxed, or not blurring. For editor windows and inspectors use editor-tool instead.
---

# Game UI (runtime uGUI)

Owns in-game UI work: building and editing screens, panels and prefabs, and diagnosing UI that
renders or behaves wrong. This project's uGUI is **not** stock uGUI: canvases are camera-space and
drawn inside the render pipeline in ordered **bands**, so several textbook defaults (overlay canvases,
`Mask` for scroll views) break it silently. The rules below are the corrections; generic uGUI layout
craft lives in [references/ugui-layout.md](references/ugui-layout.md).

Seams: editor windows, inspectors and `OnGUI` tooling → `editor-tool`. The mechanics of driving the
Editor (eval, hierarchy queries, serialized fields, captures) → `unity-editor`. Renaming or deleting a
serialized field or prefab → `unity-file-ops`. Renderer-feature code under `Assets/Scripts/Rendering/`
→ `.agents/rules/render-graph.md`.

## When to use / when to skip

- **Use** for any request that creates or changes what the player sees in a menu or HUD, and for any
  "the UI is wrong" symptom — start at the diagnosis table below.
- **Skip** for editor tooling (`editor-tool`), world-space rendering that is not UI (shaders, sky,
  fluids), and pure settings-value changes that need no new control.

## Step 0 — Read the ground truth for the task

The Architecture docs in `Documentation/Architecture/` are the source of truth; read the section
that matches the task before writing anything.

| Task | Read |
|---|---|
| Any new panel, menu, or band change; any blur backdrop | `UI_BLUR_BACKDROP_SYSTEM.md` §2.2–2.5 (bands, canvases) and §4 (authoring rules) |
| UI built in code (HUD overlays, console, benchmark HUD) | `RUNTIME_UI_FACTORY.md` |
| A new setting in the Settings menu | `DATA_DRIVEN_SETTINGS_UI.md` — settings are **generated** from `[SettingField]`; never hand-build a control for one |
| Toasts / notifications | `TOAST_NOTIFICATION_SYSTEM.md` (`ToastManager.Show`) |
| Command console UI | `COMMAND_CONSOLE_SYSTEM.md` |

If a task falls outside these, `grep -ril "<ComponentName>" Documentation/Architecture` before
assuming no contract exists.

## Step 1 — Inspect the live state first

Query what exists (`get_scene_hierarchy`, `find_gameobjects`, `get_serialized_fields` — see
`unity-editor`) before changing anything, and search `Assets/Prefabs/UI/` for an existing component
prefab (`Button`, `Dropdown`, `Toggle`, sliders, input fields, headings) before building one.

**Trust runtime values over scene YAML.** A nested canvas stores `m_RenderMode: 2` (WorldSpace) on disk
but reports its root's render mode and camera at runtime — *while active*. An inactive nested canvas
even reports `isRootCanvas == true`. Measure the live object; do not reason from the file.

## Step 2 — Pick the construction path

| The UI is… | Build it with | Notes |
|---|---|---|
| A settings control | `[SettingField]` on the `Settings` / `DevSettings` field | The generator builds and binds it |
| Built in code (no scene edit wanted) | `RuntimeUIFactory` (`Assets/Scripts/UI/Builders/`) | Canvas, panel, TMP text, button, scroll text, blur background — it owns the canvas config and the blur-material contract, **never the palette** |
| Scene- or prefab-authored | The live Editor via `SerializedObject`; prefabs via `PrefabUtility.LoadPrefabContents` → edit → `SaveAsPrefabAsset` | Never hand-edit `.unity` / `.prefab` YAML (`CLAUDE.md`) |

Scripts go in `Assets/Scripts/UI/` (feature subfolder when one exists), prefabs in
`Assets/Prefabs/UI/Components` or `Menus` — not next to each other.

## Step 3 — Place it in a band

Every canvas is `ScreenSpaceCamera` with a `worldCamera`, and UI draws in four ordered bands
(`UIBandId`: Hud, Menus, Modals, Notifications). A panel frosts the bands beneath it, never panels in
its own band.

- **Joining an existing band:** parent under that band's root. Band identity is inherited, but the
  `UI` GameObject layer is not: attach code-created children with `RuntimeUIFactory.Attach` (or set
  the layer to `UI`) — see Step 4.
- **A new band root:** a nested `Canvas` + `UIBlurBand` on the subtree root. `overrideSorting` is set
  before `sortingLayerID` (the component does this — do not set them by hand in the other order).
- **Interactive band root ⇒ its own `GraphicRaycaster`.** A nested canvas takes its subtree out of the
  parent's raycaster; without one the UI draws and silently ignores clicks. A non-interactive root
  (tooltips) must *not* get one, or it eats clicks meant for the UI beneath.
- **A band root that starts inactive** does not get `overrideSorting` from an editor-time apply.
  Activate it, apply, restore its state.
- **Self-sorting controls escape their band.** `TMP_Dropdown` sends its popup to the root canvas's
  band; the shared `Dropdown.prefab` carries `UIBandDropdownSorting` for this. Any new control that
  sets `overrideSorting` itself needs the same treatment.
- **Never create a `ScreenSpaceOverlay` canvas.** It draws outside the render graph and leaves the band
  walk while still looking right on screen. Nulling `worldCamera` flips a canvas to overlay by itself.

## Step 4 — Project build rules

- **`RectMask2D`, never `Mask`.** The band pass binds no stencil, so a stencil `Mask` clips nothing and a
  scroll list paints across the screen. The project has zero `Mask` components; keep it that way.
- **Fully qualify uGUI types:** `UnityEngine.UI.Image`, `UnityEngine.UI.Button`. Inside this project's
  `UI.*` namespaces a bare `UI.Button` resolves to *our* namespace.
- **Audit authored materials over `Image`, never `Graphic`.** `TMP_SubMeshUI` overrides the `material`
  getter to *create* an instance, which throws `MissingReferenceException` when its source material was
  destroyed (routine for generated submeshes) and allocates instances on the way. For a census by canvas,
  walk transforms: `Graphic.canvas` is the nearest active ancestor canvas and uGUI's ancestor walks stop at
  `overrideSorting`, so `GraphicRegistry.GetGraphicsForCanvas` misses nested sorting canvases.
- **Create UI objects as `new GameObject(name, typeof(RectTransform))`.** `AddComponent<RectTransform>()`
  on a plain `Transform` returns null. Likewise add a `[RequireComponent]` target first and the
  requiring component last, or the explicit add returns null.
- **UI layer on creation:** `RuntimeUIFactory.Attach` copies the parent's layer through the subtree; a
  child created with plain `SetParent` stays on layer 0 and falls into URP's own draw.
- **Blur backdrop panels** use `Custom/MaskedUIBlur` through `ApplyBlurBackground` /
  `CreateBlurMaterialInstance`: alpha at or near 1 (lower alpha leaks *sharp* screen, not transparency),
  `Image.color` RGB white, tint on the material only (material colors are gamma-authored, vertex colors
  are not).
- **Text:** TMP only. The default font (Monocraft) is a **static** atlas with no fallback: a codepoint
  it lacks renders as a blank box. Check `TMP_FontAsset.HasCharacter((char)c, true)` for any symbol
  outside plain ASCII, then look at it rendered.
- **No backlog/design IDs** (`RF-2`, `UB-3`, …) in any player-visible or tooltip string.
- **Keep local Z at 0** below the root canvas. A camera-space canvas seen through a perspective camera
  scales anything off its plane; an overlay canvas hid this.
- **Screen pixels are not canvas coordinates.** Map mouse/screen positions with
  `RectTransformUtility.ScreenPointToLocalPointInRectangle` and the **root** canvas's camera (pass the
  same camera to `TMP_TextUtilities` link hit tests).
- **Canvas scaler:** Scale With Screen Size, 1920×1080, match 0.5 (`RuntimeUIFactory.ReferenceResolution`);
  `UIScaleController` rewrites the reference resolution from the UI-scale setting at runtime.
- **A new `[SerializeField]` default freezes into the scene** on the next scene save. Changing the C#
  initializer afterwards changes nothing; set the scene/prefab value too.

## Step 5 — Verify

1. **Read writes back from disk.** An editor write that returned `true` is not evidence: re-read with a
   fresh `SerializedObject`, `grep` the saved scene/prefab for the value, and compare the file's
   `guid:` count before and after a save. A property set on a **prefab instance** needs
   `PrefabUtility.RecordPrefabInstancePropertyModifications` or the save drops it.
2. **`git diff` every scene/prefab you saved.** A save is never guaranteed to be a no-op. Note
   `git status` for that file **before** editing — the user may have uncommitted scene work in it.
   Revert only the unwanted hunks (reverse-apply them with `git apply -R`, checked first with
   `--check`), never the whole file, which would also discard the intended edit and theirs. Report
   churn you cannot separate cleanly rather than guessing.
3. **Look at it:** `capture_game_view` (`unity-editor`). Band UI is game-camera-only, so the Scene view
   never shows it. Play mode needs the user's go-ahead.
4. **Suites** (via `run-validation-suite`) when the change touches bands, canvases, raycasters or the
   blur: `Minecraft Clone/Dev/Validate UI Band Layers`, and `Validate UI Blur Render` for backdrop work.
5. **Final look is the user's.** Stop with the change in place; they tune by hand afterwards.

## Diagnosis

Read the element's live state (Step 1) before guessing. Most symptoms have one of these causes:

| Symptom | Check, in order |
|---|---|
| Invisible | Zero size or outside parent bounds; alpha 0; wrong GameObject layer (not `UI`); canvas became overlay (null camera) or has no `Camera.main`; parent inactive |
| Draws, but clicks do nothing | Nested band root without its own `GraphicRaycaster`; no/duplicate `EventSystem`; a non-interactive graphic above it with Raycast Target on; `CanvasGroup.blocksRaycasts` / `interactable` off |
| Drawn behind something it should cover | Wrong band (check the band root's sorting layer at runtime); a self-sorting control escaped its band; same-band panels expected to stack |
| Scroll list paints outside its area | A stencil `Mask` — replace with `RectMask2D` |
| Panel solid black / not frosted / too sharp | `Image.color` RGB not white; alpha well below 1; material is not a `MaskedUIBlur` instance |
| Text shows blank boxes | Codepoint missing from the static Monocraft atlas |
| Element slightly too big or small | Non-zero local Z on it or a parent (`UIBandLayers.TryFindDepthOffset` finds it) |
| Tooltip / drag / link hit offset from the mouse | Screen pixels used as canvas coordinates — map through the root canvas camera |
| Layout jitters or ignores sizes | Layout Group / `ContentSizeFitter` conflict — [references/ugui-layout.md](references/ugui-layout.md) |
| A value reverts after an edit or on reload | Prefab-instance override not recorded; serialized default frozen in the scene; write never reached disk |
| Visible in Game view, missing in Scene view | By design — bands render for game cameras only |

For an **audit** across a hierarchy (which panels use which material), iterate
`GetComponentsInChildren<UnityEngine.UI.Image>(true)`, never `Graphic`: the TMP submesh `material`
getter instantiates materials and throws on destroyed sources. A per-canvas census
(`GraphicRegistry`) also misses nested sorting canvases; walk transforms.

A bug that survives this table goes to `voxel-debugging` (instrument first) and, once confirmed, into
`Documentation/Bugs/UI_BUGS.md`.

## Constraints

- **Generate only what is asked.** "A menu screen" is layout; scripts and wiring only when logic is
  requested.
- **Fix in place; never destroy and rebuild a hierarchy** to fix it — serialized references and prefab
  overrides go with it. One change, verify, then the next; revert a fix that did not work.
- **Honor exact values** the user gives (sizes, colors, spacing); do not "improve" untouched properties.
- **No palette constants in `RuntimeUIFactory`** — each screen's tint is its own decision.
- **Never add a `Mask`, an overlay canvas, or a `Graphic`-typed material sweep.**
