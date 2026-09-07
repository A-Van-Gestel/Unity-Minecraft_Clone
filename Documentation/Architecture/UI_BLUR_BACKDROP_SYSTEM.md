# UI Blur Backdrop System

**Version:** 2.2  
**Date:** 2026-09-07  
**Status:** **Implemented (Stable)** — `UB-0`…`UB-6` and `UB-8` shipped and confirmed in game in both
scenes (`UB-4` ⏸️ paused, no retune due). `UB-8`'s confirmation was the Render Graph Viewer's pass
list on 2026-09-07: bands appear and disappear from the recorded frame as surfaces open and close, in
`World` and `MainMenu` alike. `UB-7`'s promotion half is this document; its play-mode regression guard
is `NS-12` in
[`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md).  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Frosted-glass backdrops for UI panels. UI is drawn **inside the URP render graph**, in ordered
> *bands*, and the screen is re-blurred between them — so a panel samples a `_UIBlurTexture` that
> already contains every band beneath it. **The pivotal property: correctness comes from *when* a
> panel is drawn, not from what its shader does.** `Custom/MaskedUIBlur` samples one global by screen
> UV and knows nothing about bands. Cost tracks the bands that currently *draw something*, not panels
> and not declared bands: an idle HUD-only frame records exactly one blur in both scenes.

**Audited:** 2026-09-07, at commit `0c2d3f7a` (branch `feat/ui-blur-rework`).
Every claim below was re-verified against current code this session, from the code rather than from
the Design doc it supersedes: `UIBandCompositeRendererFeature.cs`, `UIBlurChain.cs`,
`UIBlurHistory.cs`, `UIBlurBand.cs`, `UIBandLayers.cs`, `UIBandId.cs`, `UIBandRegistry.cs`,
`UIBandDropdownSorting.cs`, `RuntimeUIFactory.cs`, `ToastManager.cs`, `ToastStyle.cs`,
`TooltipManager.cs`, `DragAndDropHandler.cs`, `CreditsMenuController.cs`,
`GraphicsSettingsController.cs`, `TouchControls.cs`, `WorldUIManager.cs`, `BenchmarkUIBuilder.cs`,
`ConsoleUI.cs`, `MaskedUIBlur.shader`, `UIBlurBlit.shader`, `UIBlurRenderValidationSuite.cs`,
`UIBlurQuadRenderer.cs`, `UIBandLayerValidationSuite.cs`,
`UnderwaterRenderValidationSuite.Overlay.cs`, `ValidationSuiteRegistry.cs`,
`VoxelEngine-URP-Renderer.asset`, `VoxelEngine-Post-Profile.asset`, `UIBlur.mat`, `UIBlurClear.mat`,
`ProjectSettings/TagManager.asset`, and the serialized canvases, bands and blurred graphics of
`World.unity` and `MainMenu.unity`. Shaders are not indexed by CodeGraph and were read directly.
Scene render modes, sorting layers, material references and renderer masks were parsed out of the
serialized assets, not assumed.

**Relationship to other documents:**

- [`../Guides/SHADER_CONVENTIONS.md`](../Guides/SHADER_CONVENTIONS.md) — the `#pragma target 4.5`
  floor and interpolator-counting rule both shaders follow.
- [`RUNTIME_UI_FACTORY.md`](RUNTIME_UI_FACTORY.md) — the shared UI builder that owns
  `ConfigureCanvas` (§2.5) and the material-instance lifecycle described in §5.
- [`../Bugs/UI_BUGS.md`](../Bugs/UI_BUGS.md) — **#05** (blur strength scales with resolution) is open
  against the blur chain and is *amplified* here, since every occupied band re-runs the kernel.
  **#07** (dropdown popup template too short) was filed while fixing the mask defect in §8.
- [`DATA_DRIVEN_SETTINGS_UI.md`](DATA_DRIVEN_SETTINGS_UI.md) — the settings menu, one of the blurred
  panels, and the owner of the render-scale setting §8 records a consequence of.
- [`TOAST_NOTIFICATION_SYSTEM.md`](TOAST_NOTIFICATION_SYSTEM.md) — a consumer that used to work
  around the old stacking limit by policy; it now has its own band and frosts unconditionally.
- [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) — the console panel, whose *text* showing
  through a toast raised above it is the assertion this system exists to make true.
- [`UNDERWATER_AND_SUBMERSION_RENDERING.md`](UNDERWATER_AND_SUBMERSION_RENDERING.md) — records before
  the blur, so a frosted panel samples an already-tinted screen. Pinned by its `B17` (§7).
- [`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md)
  — **`NS-12`**, the play-mode scenario host this system's defining assertion needs before it can be
  pinned by a baseline.

---

## ID index

| ID | Scope | Where it now lives |
|----|-------|--------------------|
| **UB-0** | Feasibility spike: one canvas in-pipeline, one band pass, masks cleared | ⛔ Shipped nothing. Preserved on branch `spike/ub0-inpipeline-ui` (`e369f061`); its two surviving corrections are §2.4's sort criteria and global-state permission |
| **UB-1** | `UIBandId` / `UIBandLayers`, the band sorting layers, layer assignment at creation, `UIBlurBand`'s repair pass | §2.2, §2.3, §7 (`L1`–`L4`) |
| **UB-2** | `UIBlurChain` extracted, `UIBlurRendererFeature` absorbed, `UIBandRegistry`, the composite feature, the renderer masks, Underwater `B17` rewritten | §2.1, §2.4, §2.5, §7 |
| **UB-3** | `World.unity` conversion: render mode, three band roots, the screen-to-canvas rewrites, off-plane Z sweep, dropdown re-banding | §2.2, §2.5, §6 |
| **UB-4** | Tint retune against the post-processed capture | ⏸️ Paused 2026-09-07 — measured as not due (§5); the limitation it was to close was closed by `UB-3` |
| **UB-5** | Toast flat-fallback policy deleted; cards frost unconditionally | §6, and `TOAST_NOTIFICATION_SYSTEM.md` |
| **UB-6** | `MainMenu.unity` adoption: bands, `UIBlurClear.mat`, stencil `Mask` → `RectMask2D`, canvas camera for link hit-testing | §2.2, §5, §6, §8 |
| **UB-7** | Validation & promotion | **Split.** The promotion is this document. The play-mode regression guard is `NS-12` in [`../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md`](../Design/VALIDATION_SUITE_COVERAGE_ROADMAP.md) |
| **UB-8** | Occupancy reads a band's *content* rather than its root being enabled, so a declared-but-empty band leaves the walk | §2.1, §6, §7 (`L17`–`L19`), §8 |

---

## 1. Components

| File                                                          | Role                                                                                          |
|---------------------------------------------------------------|-----------------------------------------------------------------------------------------------|
| `Assets/Scripts/Rendering/UIBandCompositeRendererFeature.cs`   | The producer. Walks the occupied bands, re-blurring before each one draws.                     |
| `Assets/Scripts/Rendering/UIBlurChain.cs`                      | The Kawase chain and the `_UIBlurTexture` publish, recorded once per band.                     |
| `Assets/Scripts/Rendering/UIBlurHistory.cs`                    | Per-camera persistent blur target, so the result is not a pooled render-graph texture.          |
| `Assets/Scripts/UI/Blur/UIBandId.cs`                           | The four ordered bands. The enum value *is* the paint order and the walk index.                 |
| `Assets/Scripts/UI/Blur/UIBandLayers.cs`                       | Resolves a band's sorting layer and the single `UI` GameObject layer; off-plane Z detection; the subtree content test occupancy reads. |
| `Assets/Scripts/UI/Blur/UIBandRegistry.cs`                     | Which bands currently have content, and the walk order derived from that.                       |
| `Assets/Scripts/UI/Blur/UIBlurBand.cs`                         | Declares a subtree as a band. One component routes the whole subtree.                           |
| `Assets/Scripts/UI/Blur/UIBandDropdownSorting.cs`              | Keeps a `TMP_Dropdown`'s self-sorting popup inside its own band.                                |
| `Assets/Shaders/UIBlurBlit.shader`                             | `Hidden/UI/KawaseBlur` — the kernel the chain's iterations run.                                 |
| `Assets/Shaders/MaskedUIBlur.shader`                           | Consumer. A UI shader that samples `_UIBlurTexture` by screen UV.                               |
| `Assets/Materials/UI/UIBlur.mat`                               | The shared tinted material (`_MultiplyColor` `0.415`).                                          |
| `Assets/Materials/UI/UIBlurClear.mat`                          | The neutral variant (`_MultiplyColor` white) — blurs without darkening. Unreferenced today (§5). |
| `Assets/Editor/Validation/UIBlur/`                             | The rendered-pixel consumer suite and its quad renderer.                                        |
| `Assets/Editor/Validation/UIBands/`                            | The band-routing suite.                                                                         |

---

## 2. Producer: the band walk

### 2.1 The loop

`UIBandCompositeRendererFeature` enqueues **one** pass, at
**`RenderPassEvent.AfterRenderingPostProcessing`** (`UIBandCompositeRendererFeature.cs:30`), for
`CameraType.Game` only (`:99`). That event is load-bearing twice over: every band samples a screen
that has been through the post stack, and the active target is still the camera color rather than the
backbuffer — which is the only place the walk can both blur the target and draw into it. The pass sets
`requiresIntermediateTexture` (`:103`) so a camera rendering straight to the backbuffer cannot strand
it.

The pass walks the occupied bands in ascending order, re-blurring before each (`:166-185`):

```
RenderPassEvent.AfterRenderingPostProcessing
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        the post-processed world — bloom included
├─ draw band 0  (Hud)                          HUD · toolbar · creative inventory
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        now contains the toolbar, its icons and counts
├─ draw band 1  (Menus)                        pause · settings · help menus
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        now contains the open menu's labels
├─ draw band 2  (Modals)                       console · benchmark results · world-select modals
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        now contains the console's backlog text
└─ draw band 3  (Notifications)                toasts · tooltips
```

A band with nothing to draw is skipped along with the blur that would have preceded it, so cost is
one blur plus one draw per **occupied** band.

**Occupancy is the subtree's content, not the band root's enabled state.** A `UIBlurBand` registers in
`OnEnable`, but a band root outlives its content by design: `PauseMenuContainer`, `TooltipRoot`, and the
runtime `Console` and `Toasts` canvases are all enabled for the whole scene while the panels *below*
them toggle. Keying occupancy on registration therefore reported every declared band on every frame —
an idle `World` frame walked all four bands and an idle `MainMenu` two, paying a full Kawase chain and
a renderer-list draw for three of them that rendered nothing. `UIBandRegistry.OccupiedMask` asks each
registered band for `UIBlurBand.HasVisibleContent`, which is `UIBandLayers.HasVisibleGraphic`: an
allocation-free descent that prunes at the first inactive GameObject and returns at the first active,
enabled `Graphic`. An empty band costs the inactive-child checks; an occupied one costs the depth to
its first graphic, never the size of its subtree. It is recomputed per read rather than memoized —
there is one read per camera per frame, so a cache would buy nothing and would hand a stale mask to
consecutive edit-mode reads inside one editor frame.

**A transform walk, not a `GraphicRegistry` query.** uGUI registers a `Graphic` against its *nearest
active* ancestor canvas, so anything under a nested `overrideSorting` canvas — a `TMP_Dropdown` popup,
most of all — lands in a different bucket than the band root's. A per-canvas query would need a cached
canvas set, and a stale one would take that band out of the walk and make its UI vanish with nothing on
screen to explain it. `L18` is the baseline that pins this.

A subtree can also draw through something that is not a uGUI `Graphic`, which this test cannot see;
`UIBlurBand`'s `_alwaysOccupied` opt-out keeps such a band in the walk unconditionally. No band uses it
today.

**The base band (`Hud`) always walks** (`:157`) —
`_UIBlurTexture` must publish every frame whatever the registry reports, because edit-mode registration
does not survive a domain reload and gating the base capture on occupancy would silently cost every
panel its blur in the editor while looking correct in play mode.

A band whose sorting layer is missing from the project is skipped and reported once (`:172-203`): its
filter would otherwise collapse to a range matching nothing, drawing an empty band that still paid for
its blur, with nothing on screen to suggest a project-settings problem.

### 2.2 Band identity: a sorting layer on a nested canvas

**A band is a subtree, not a canvas.** `World.unity` holds one root `Canvas`, under which `Toolbar`,
`CreativeInventory` and `PauseMenuContainer` are siblings — a per-canvas band could not separate the
toolbar from the pause menu, which is the likeliest overlap in the game.

Band identity is carried by the **sorting layer** of one `Canvas` per band root; the **GameObject
layer** answers a different question, "is this UI at all", and one layer (`UI`, index 5) covers every
band (`UIBandLayers.cs:10-19`). The split is what makes declaring a band free: a nested `Canvas` with
`overrideSorting` routes its whole subtree from one component, writing **no property on any child** and
therefore no prefab overrides.

| Band | Enum | Sorting layer | `uniqueID` |
|:----:|------|---------------|-----------|
| 0 | `UIBandId.Hud` | `Default` | 0 |
| 1 | `UIBandId.Menus` | `UIBandMenus` | 1343291393 |
| 2 | `UIBandId.Modals` | `UIBandModals` | 1343291394 |
| 3 | `UIBandId.Notifications` | `UIBandNotifications` | 1907843371 |

Declared in `ProjectSettings/TagManager.asset` in that order, which is what `SortingLayer.value` ranks
by and therefore what makes `L1`'s "ascends with band order" assertion meaningful.

**Five routing constraints govern `UIBlurBand.Apply`, each found by it breaking something:**

- `overrideSorting` applies only to a **nested** canvas; Unity forces it back off on a root canvas,
  which already owns its sorting natively (`UIBlurBand.cs:74`).
- It must be set **before** `sortingLayerID` (`:74-76`) — a canvas still inheriting its parent's
  sorting ignores the assignment, leaving the id at 0 with no error.
- **A nested band root needs its own `GraphicRaycaster`.** uGUI registers a `Graphic` against its
  nearest canvas, and a `GraphicRaycaster` serves only the canvas on its own object, so the canvas that
  buys the banding takes the subtree out of the parent raycaster's reach. The UI still draws, which is
  what makes the loss of input silent. `UIBlurBand` warns in-editor when a nested band root holds a
  `Selectable` and carries no raycaster (`:122-130`). A band root with no interactive content should
  *not* get one — `TooltipRoot` has none deliberately, so tooltips cannot intercept clicks meant for the
  UI beneath them.
- **A band root that starts inactive never receives `overrideSorting` from an editor-time apply.** An
  inactive nested canvas reports `isRootCanvas == true`, so `Apply` takes the root-canvas branch and
  skips the opt-out, leaving `sortingLayerID` set on a canvas that is still inheriting. At runtime
  `OnEnable` gets it right on its own (`:30-34`), so the defect is confined to what a scene file
  records; the authoring fix is to activate the object, apply, and restore its state.
- **A canvas that loses its camera does not stay camera-space — Unity flips it to overlay.** Assigning
  a null `worldCamera` silently sets `renderMode` to `ScreenSpaceOverlay`, so "camera-space with no
  camera" does not exist as a detectable state. What *is* detectable is an **overlay root canvas under
  a band**, which is always wrong — an overlay canvas draws outside the render graph, so the subtree
  leaves the band walk while still looking correct on screen. `RebindCameraIfLost` restores both the
  render mode and the camera (`:94-110`).

**A control that sorts itself can escape its band.** `TMP_Dropdown` resolves its popup's sorting layer
from the first ancestor canvas with `isRootCanvas`, ignoring `overrideSorting`, so a dropdown inside a
banded subtree sends its popup to the *root* canvas's band — behind the panel that opened it. uGUI's own
`Dropdown` tests `isRootCanvas || overrideSorting` and is unaffected. `UIBandDropdownSorting` re-stamps
the band's sorting layer onto any self-sorting canvas in the dropdown when the popup is parented
(`OnTransformChildrenChanged`), which happens before the blocker is built so both follow. It rides on
the shared `Dropdown.prefab`, so every dropdown instantiated from it inherits the fix.

The general shape, worth checking for any future control: **anything that sets `overrideSorting` itself
will pick a sorting layer, and it will not pick the band's.**

### 2.3 Layer discipline: the `UI` layer is assigned, never inherited

The GameObject layer is what the renderer's own draw masks exclude (§2.5), and Unity's `new GameObject`
starts on layer 0 with **no inheritance on reparent**. A one-shot recursive sweep on enable is therefore
not sufficient: every occupant of the Modals and Notifications bands is created *after* its band root
enabled, and a late child would fall back inside URP's own transparent mask and draw at the wrong time.

The layer is applied **at creation** instead. `RuntimeUIFactory.Attach` parents an object and copies the
parent's layer across the whole subtree (`RuntimeUIFactory.cs:46-50`), so every factory helper lands its
output on the band. `UIBlurBand.Apply` re-applies the `UI` layer to its subtree on enable
(`UIBlurBand.cs:67`) as a repair pass rather than as the primary mechanism. `L2`–`L4` are what make that
a contract rather than a convention.

Band identity is exempt from all of this: it lives on the band root's canvas, so a late child inherits
it by being in the subtree, with nothing to assign and nothing to repair.

### 2.4 The band draw pass

One raster pass per band (`UIBandCompositeRendererFeature.cs:206-253`), modeled on
`CloudPrepassRendererFeature`:

- **`FilteringSettings`** — `RenderQueueRange.transparent`, plus two filters answering two questions:
  `layerMask` = the single `UI` GameObject layer ("this is UI"), and `sortingLayerRange` collapsed to
  the band's own sorting value ("this is that band") (`:221-226`).
- **`DrawingSettings`** — shader tags `SRPDefaultUnlit` and `UniversalForward` (UGUI, TMP and
  `MaskedUIBlur` all declare passes with no `LightMode`, so they resolve under `SRPDefaultUnlit`), and
  **`SortingCriteria.CommonTransparent`** (`:215-216`). **Not `CanvasOrder`** — `CanvasOrder` without
  `SortingLayer` renders a panel *over* its own child text, and the failure is silent because the panel
  still draws.
- **`SetRenderAttachment(activeColorTexture, 0, ReadWrite)`** (`:236`) — read-write, because UI
  alpha-blends against what is already in the attachment.
- **`AllowPassCulling(false)`** (`:240`) — the next band's blur samples the color attachment, which is
  not a dependency the graph can see.
- **`AllowGlobalStateModification(true)`** (`:244`), mandatory and set at record time. Without it the
  raster pass rejects the `unity_GUIZTestMode` write below outright, aborting the render graph.
- **No depth attachment, and `unity_GUIZTestMode` = `CompareFunction.Always`** (`:250`). These go
  together, and the precedent gets it the other way round: `CloudPrepass` binds depth because clouds
  *are* world geometry. UI is not — it must never depth-test against terrain, or a panel disappears
  behind a hill. Every UGUI shader declares `ZTest [unity_GUIZTestMode]`, and that global is written by
  the UI system only on the Overlay path this system left behind, so it is stale here and is set
  explicitly. **Copying the cloud pass faithfully is a bug**; this is the deviation.

The blur itself is `UIBlurChain.Record`, called once per band with a per-band pass label so bands stay
separable in a frame capture. It downsamples the camera color by `downsample` (2), runs `iterations`
(4) Kawase steps ping-ponging between two render-graph temporaries on a gentle offset progression
`[0.5, 0.5, 1.5, 1.5, …]`, and writes the final iteration into a **persistent per-camera target** from
`UIBlurHistory` — never a pooled render-graph texture, which the graph would return at its last-used
pass where a later pass can be handed the same memory. Cameras exposing no history manager fall back to
a shared `_UIBlurTextureFallback` handle (`UIBlurChain.cs:126-140`). The target is imported per call, so
the graph sees no edge between one band's blur and the next band's draw: **record order plus
`AllowPassCulling(false)` are what hold the interleaving**, and reordering or culling them breaks it
silently.

The result is published with `SetGlobalTexture("_UIBlurTexture", …)` from an **unsafe** pass
(`:111-120`), because setting global state is not permitted inside a raster pass; culling is disabled so
the global is always rebound.

### 2.5 What the canvases must be

`RenderMode.ScreenSpaceCamera` with a `worldCamera`, at `planeDistance` 100. For code-built UI this is
`RuntimeUIFactory.ConfigureCanvas` (`RuntimeUIFactory.cs:79-107`), which all four code-built canvases
route through; it warns when no `Camera.main` exists, since the canvas would draw correctly and be
silently outside every band. The component order there is load-bearing: `UIBlurBand` carries
`[RequireComponent(typeof(Canvas))]`, so adding it before the explicit `AddComponent<Canvas>()` makes
Unity supply the canvas and the explicit add return **null**. `L8` goes through the factory precisely
because a test that exercises the component directly cannot see that fault.

URP's own passes must exclude the `UI` layer from **all three** masks on `UniversalRendererData` —
`m_PrepassLayerMask`, `m_OpaqueLayerMask` and `m_TransparentLayerMask`, each `0xFFFFFFDF` in
`VoxelEngine-URP-Renderer.asset:51-60` (every bit but layer 5). The prepass mask is the one that is easy
to miss and expensive to get wrong: UI left inside it writes depth on the camera plane in front of the
world, corrupting every depth consumer — the underwater feature and the fluid surface both read camera
depth. `L5` asserts all three, because an assertion covering only opaque and transparent would pass
while that misconfiguration shipped.

---

## 3. Consumer: the `Custom/MaskedUIBlur` contract

Unchanged by the move into the render graph — this is the property that let the whole arc ship without
touching the shader or its five baselines. The fragment program computes, in linear space:

```
rgb = (blur.rgb * _MultiplyColor.rgb + _AdditiveColor.rgb) * vertexColor.rgb
a   =  mainTex.a * vertexColor.a          (then clip rect, then alpha clip)
```

blended with `Blend SrcAlpha OneMinusSrcAlpha`. Material tints apply first, then the UI vertex color
scales the whole panel — so fading a panel out fades its additive term too rather than leaving a glow.

It implements the standard UI material surface: `_Stencil*` + `_ColorMask`, `UNITY_UI_CLIP_RECT` +
`_ClipRect` (so `RectMask2D` works), and `UNITY_UI_ALPHACLIP`. The clip test is a locally-declared
`Get2DClipping` rather than an include of the Built-in pipeline's `UnityUI.cginc`. **The `_Stencil*`
properties are declared but inert under banding** — see §8.

`_ClipRect` is compared against the **untransformed vertex position**, which is the canvas-space
coordinate the UI feeds in — it is deliberately not transformed to world space.

Interpolators: 3, or 4 with clipping enabled. `#pragma target 4.5`, the project floor.

---

## 4. Authoring rules (the non-obvious half)

These are consequences of §2's capture points and of Unity's color handling. Every one was learned by
measurement, and getting any of them wrong produces a plausible-looking panel that is wrong.

### 4.1 Alpha is not "transparency" — it is how much sharp screen leaks in

The panel's own content is the *blurred* screen, so `1 − alpha` is the fraction of the **un-blurred**
screen that shows through. An alpha of 0.72 against a 0.415 tint puts the sharp image ahead of the
blurred one by roughly **2.7 : 1** (the tint reaches the shader as linear `0.1437`, so `0.28` sharp
against `0.72 × 0.1437`), and the panel stops reading as frosted glass at all. Sharpness bleed is a
function of alpha **alone** — no tint value can compensate, because the tint scales the blurred layer
and the sharp layer equally in relative terms.

**Rule:** a panel that should look frosted wants alpha at or near 1. Lower it only for a deliberate
sharp-through effect, and accept the frost loss as the price.

### 4.2 A panel frosts the bands beneath it, never its own band

The blur before band *k* contains everything bands `0…k-1` drew — panels, text, icons, a scrolling
console backlog — so an opaque blurred panel **composites over** the UI beneath it rather than replacing
it. That is what a band declaration buys.

**Two panels in the *same* band still cannot stack**: the lower one is not in the capture the upper one
samples, and no alpha value changes that. The band is the granularity, and adding a band is a
declaration on one component — so this is a tuning matter, not a wall. `RuntimeUIFactory`'s
`ApplyBlurBackground` states the same rule at the call site.

### 4.3 Material colors are gamma-authored; vertex colors are not

Unity converts material `Color` properties from gamma to linear on upload, so the material's authored
`_MultiplyColor` of `0.415` reaches the shader as **`0.1437`**. UI vertex colors (`Image.color`) are
**not** converted on this path. Two colors that read the same in the inspector therefore do different
things depending on which knob they are set through. White and black are fixed points of the conversion,
which is why only tinted values expose the difference — and why the validation suite converts its
material-tint expectations (`AsShaderSees`) but not its vertex-color ones.

### 4.4 Alpha comes from `_MainTex`, so a sprite-less Image is opaque

Output alpha is `mainTex.a * vertexColor.a`. An `Image` with no sprite binds Unity's white texture, so
`mainTex.a` is 1 and all opacity control comes from `Image.color.a`.

### 4.5 RGB must be white unless a tint is intended

Since vertex color multiplies the result, an `Image.color` with black RGB renders a **solid black
panel**. `ApplyBlurBackground` forces `Color.white` for exactly this reason; tint belongs on the
material.

---

## 5. Material instances and tinting

`_MultiplyColor` / `_AdditiveColor` live on the **material**, so two panels needing different tints need
different material instances (or different material assets). Panels needing only different *opacity* can
share one material and vary `Image.color.a`.

Two shared assets exist:

- **`UIBlur.mat`** — `_MultiplyColor` `0.415`, the darkening frost. **Every frosted surface in both
  scenes uses it.**
- **`UIBlurClear.mat`** — `_MultiplyColor` white, so the backdrop **blurs without darkening**. It needs
  no shader or feature change; the neutral path has always existed. **Currently referenced by nothing**,
  and kept as the ready-made option for a surface that must not darken.

**The rule: frost darkens, everywhere.** Legibility is what the tint buys — text over a blurred
backdrop reads better against the darkened one — and a single frost value across every screen is what
makes the interface look like one interface. A panel carrying its own scrims therefore darkens twice
by design rather than by oversight.

**Every darkening layer past the frost must be near-opaque, or it drains color.** Layers stack
multiplicatively, so a half-transparent one shows the already-darkened result through itself and a
colored backdrop arrives pulled toward gray. The settings menu's tooltip is the reference: at
`α 0.949` it is effectively opaque, so nothing beneath it reaches the eye and it reads clean. Both of
`MainMenu`'s `SelectPanel` layers now match it — the `Scroll View` scrim and the world tiles sit at
`α 0.95`.

Measured on a 4K frame, the ladder that produced is worth keeping, because it shows where a screen's
brightness actually comes from: the dirt backdrop renders at roughly `0.52` sRGB, the frost takes it
to `0.204`, and each near-opaque layer past that lands near its own color. A half-transparent layer
sits *between* those steps — `α 0.392` put the gaps between rows at `0.157` brown against rows at
`0.114`, which reads as regular banding down the list. **The frost is never the layer to change**;
it was doing its job at every point in this.

**A tile's own color cannot be black, because `Selectable` tints multiply.** `ColorTint` resolves to
`graphic.color × stateColor`, so a black background renders hover (`× 0.9`) and pressed (`× 0.78`)
identical to normal and the row stops responding to the cursor. The tiles keep `rgb 0.10`. The same
trap applies to any darkened surface that is also a `Selectable`.

**No tint retune was due when the capture point moved past post-processing** (`UB-4`, ⏸️). The profile's
only post effect is Bloom at `intensity 0.25` / `threshold 1.1` (`VoxelEngine-Post-Profile.asset`), so
sub-white pixels contribute nothing and the authored tints still read correctly — confirmed in game in
both scenes on 2026-09-07, with `MainMenu`'s colors checked against an older production build.

Runtime-built UI creates its instance with `Shader.Find("Custom/MaskedUIBlur")` — safe in player builds
because the shader is listed in **Always Included Shaders** (`ProjectSettings/GraphicsSettings.asset`).
Any code that does this **owns the instance and must destroy it**.

`RuntimeUIFactory.CreateBlurMaterialInstance` / `ApplyBlurBackground` are the shared entry points for
code-built screens; creation and application are separate calls so a caller whose build method re-runs
(`ConsoleUI.BuildPanel`, re-entered by the UI_BUGS #04 self-heal) can still allocate exactly once.
`ApplyBlurBackground` also carries the fallback path that renders a flat color when no blur material is
available — used live by `FluidStressController`, which passes none.

**Where lifetime attaches.** Destroying a panel's GameObject does **not** reclaim the material assigned
to its `Image.material` — measured in play mode, the instances outlive the hierarchy. Each code-built
screen therefore hands its instance to the component that lives on the panel's canvas root
(`BenchmarkHUD`, `BenchmarkResultsScreen`), which destroys it in `OnDestroy`. `ToastManager` owns one
instance per `ToastVariant` — per variant, not per card, because cards are pooled and lazily built, so a
per-card instance would leak one material per card the session ever needed — and destroys them in
`OnDestroy`. `ConsoleUI` is the deliberate exception: it owns its instance directly because the material
must survive `BuildPanel` being re-entered by the UI_BUGS #04 self-heal.

---

## 6. Current usage

### `World.unity`

One root `Canvas` in Screen Space - Camera at `sortingOrder` 0, plus two nested band roots. Five
sprite-less, opaque-white `Image`s share `UIBlur.mat`:

| Panel                                       | Band    | Declared on          | Notes                                         |
|---------------------------------------------|---------|----------------------|-----------------------------------------------|
| `Toolbar`                                   | `Hud`   | root `Canvas`        | the console overlaps its left edge            |
| `CreativeInventory`                         | `Hud`   | root `Canvas`        |                                               |
| `PauseMenuContainer/PauseMenu`              | `Menus` | `PauseMenuContainer` | full-screen                                   |
| `PauseMenuContainer/SettingsMenu`           | `Menus` | `PauseMenuContainer` | full-screen; a `SettingsMenu.prefab` instance |
| `PauseMenuContainer/HelpMenu`               | `Menus` | `PauseMenuContainer` | full-screen                                   |

`TooltipRoot` is a third band root (`Notifications`) carrying no blurred graphic and, deliberately, no
`GraphicRaycaster`.

Four canvases are built in code, each with its own material instance (§5):

| Canvas                    | Band            | `sortingOrder` | Tint    | Notes                                                    |
|---------------------------|-----------------|---------------:|---------|----------------------------------------------------------|
| Benchmark HUD             | `Hud`           | **-10**        | `0.7`   | below the scene canvas                                    |
| `ConsoleUI` panel         | `Modals`        | 100            | `0.415` | matches the scene panels; covers the toolbar's left edge  |
| Benchmark results overlay | `Modals`        | 200            | `0.15`  | terminal modal                                            |
| `ToastCard` backdrops     | `Notifications` | 250            | derived | one instance per `ToastVariant`: `0.415` neutral lerped 35 % toward the variant's accent |

Cross-band ordering is the band's, not `sortingOrder`'s — the bands draw in separate passes, in walk
order, and `sortingOrder` now only orders within a band.

**All four bands are declared here and all four band roots stay enabled**: the root `Canvas`,
`PauseMenuContainer` (whose three menus are all inactive by default), `TooltipRoot`, and the
`Console` and `Toasts` canvases `WorldUIManager.Awake` builds. Before `UB-8` that was an idle
occupancy mask of `15` — four blurs and four draws for one band's worth of visible UI. It is now `1`
on an idle frame, rising only as surfaces actually open (`3` with the pause menu, `5` with the
console, `9` with a toast live).

**The full-screen menus and the creative inventory are still mutually exclusive**, and that gate survives
for its own reason rather than the compositing one: `WorldUIManager.HandleEscape` dismisses the inventory
on the first Escape and only opens the pause menu on a second press, and the inventory toggle is gated on
`!IsPauseMenuOpen`. Under banding the inventory would be *frosted over* rather than punched through —
but still clickable, which is the reason the gates remain.

### `MainMenu.unity`

Adopted in `UB-6`. One root `Canvas` in Screen Space - Camera (`Hud`), three submenu band roots on
`Menus` (`Credits&LicencesMenu`, the `SettingsMenu.prefab` instance, the `WorldSelectMenu.prefab`
instance), three modals inside `WorldSelectMenu` on `Modals`, and a `TooltipRoot` on `Notifications`.
Seven blurred graphics, all on `UIBlur.mat`.

**`MainMenu`'s screens never overlap** — `MainMenuController` deactivates the main panel whenever a
submenu opens — so banding there buys something different from panel-over-panel: the camera renders only
a skybox, and without a band boundary between the full-screen `Background` image and the panels, a
frosted panel would sample the skybox and punch a hole through the menu backdrop. The band puts
`Background` into the capture the panels read.

Every submenu and modal root here already starts inactive, so only `TooltipRoot` — enabled for the
whole scene with its tooltip instantiated on demand — leaked a band before `UB-8`. The idle mask was
`9`, two blurs; it is now `1`.

### Consequences elsewhere

- **Screen-pixel coordinates are no longer canvas coordinates.** An Overlay canvas' world space *is*
  screen pixels; a camera-space canvas' plane sits at `planeDistance` in camera world units. Three sites
  assumed the identity and were rewritten: `TooltipManager` and `DragAndDropHandler` now map through the
  canvas rect with `RectTransformUtility.ScreenPointToLocalPointInRectangle`, and
  `CreditsMenuController` passes the canvas camera to `TMP_TextUtilities.FindIntersectingLink`. All three
  resolve the **root** canvas, since render mode and camera live there and a band root is nested. This
  defect class is found by searching for the *pattern*, not by listing UI files.
- **Stray local Z became visible.** An overlay canvas ignores local Z; a screen-space canvas rendered
  through a **perspective** camera projects anything off its plane at a different scale. The offending
  values were zeroed in both scenes, and `UIBandLayers.TryFindDepthOffset` plus `UIBlurBand`'s in-editor
  warning now detect new ones. The root canvas is exempt — its Z *is* the plane distance Unity places it
  at.
- **`TouchControls` opts itself out, on every platform it runs on.** It builds its own canvas
  (`TouchControls.cs:365-378`) instead of going through `RuntimeUIFactory`, and sets
  `RenderMode.ScreenSpaceOverlay` directly, so it takes neither the render mode nor a band. An overlay
  canvas is drawn by URP *after* every band, so on mobile the touch controls sit above all banded UI and
  no panel can frost them. Mobile-only, hence recorded rather than fixed; routing it through the factory
  is the fix whenever mobile is next exercised.

---

## 7. Validation

Two registered suites (`ValidationSuiteRegistry.cs:94-95`), both in `Validate All`.

**`Minecraft Clone/Dev/Validate UI Band Layers` — 19 baselines.** Synthetic hierarchies, no graphics
device, so they stay meaningful before any scene declares a band.

| ID | Asserts |
|----|---------|
| `L1` | Every band resolves to its own declared sorting layer, distinct, ascending with band order |
| `L2` | A band layer reaches the whole subtree, at any depth |
| `L3` | An object created *after* its band root enabled still lands on the band layer |
| `L4` | Attaching a pre-built hierarchy carries the layer to its children |
| `L5` | All three renderer masks — **prepass included** — exclude the UI layer |
| `L6` | Occupancy drives the walk order **and the capture count** |
| `L7` | A nested band root routes its subtree through one canvas, writing nothing per object |
| `L8` | The factory builds exactly one fully configured, banded canvas |
| `L9` | A root band canvas routes without `overrideSorting` |
| `L10` | The factory's canvas is visible to the band walk |
| `L11` | A nested band canvas takes its subtree out of the parent's raycaster |
| `L12` | A dropdown popup is re-banded onto its own band |
| `L13` | The shared dropdown prefab carries the band sorting fixer |
| `L14` | UI sitting off the canvas plane is detected, and the canvas root is exempt |
| `L15` | A dropdown nested inside another prefab inherits the fixer |
| `L16` | A band restores a root canvas that fell back to overlay |
| `L17` | A band is occupied by its content, not by its root being enabled |
| `L18` | A graphic built after the band root enabled, deep and under its own sorting canvas, still occupies |
| `L19` | A disabled graphic vacates a band, and `_alwaysOccupied` holds it in the walk regardless |

Five of these are load-bearing in a way their one-line summary hides. `L5` covers the prepass mask
because omitting it is how the assertion passes while the misconfiguration ships. `L6` asserts the
*count* as well as the order, because a band silently dropping out of the walk leaves the remaining order
correct. `L8` goes **through the factory** rather than exercising `UIBlurBand` directly, because the
`[RequireComponent]` construction-order fault is invisible to a component-level test. `L17` reads its
*occupied* half through `UIBandRegistry` rather than the component, so a band that failed to register
reds there instead of satisfying the two vacancy assertions and going green while nothing can draw.
`L18` is the one that pins the worst outcome available here — a band that leaves the walk still looks
authored correctly and simply stops appearing — which is why it asserts the late, deep,
`overrideSorting`-nested case rather than a graphic sitting on the band root.

**`Minecraft Clone/Dev/Validate UI Blur Render` — 5 baselines.** Drives the material offscreen with a
synthetic `_UIBlurTexture`, so it tests the **consumer contract in isolation**: `B1` round-trip, `B2`
`_MultiplyColor`, `B3` `_AdditiveColor`, `B4` vertex color on rgb and alpha, `B5` clip rect. Two harness
properties are load-bearing: `B1` doubles as the did-anything-render check (a silent no-draw returns the
backdrop everywhere, which would let `B5` pass vacuously), and the draw uses a `CommandBuffer`, never
`SetPass` + `DrawMeshNow` — the immediate-mode path inherits ambient GL state and silently drew nothing
inside `Validate All`. The renderer also snapshots and restores the shader globals it overwrites.

**Underwater `B17`** pins the cross-feature invariant: the composite is present, its blur shader is
assigned, and it records **after** the underwater overlay so a frosted panel samples an already-tinted
screen. It compares the two features' **live pass events**, read off the features rather than restated,
so it holds for any renderer-list order and cannot degenerate into comparing two literals.

**Reading occupancy from a live frame.** The **Render Graph Viewer** (`Window/Analysis/Render Graph
Viewer`) names every recorded pass, so a band that left the walk is directly observable: an idle
`World` frame lists exactly `UI Band Hud Iter 0`…`Iter 3`, `Set Global` and `Draw`, and gains a
matching six-pass group per band as surfaces open. `UIBandRegistry.OccupiedMask` is the same fact one
step earlier, readable from a play-mode query. Both were used to confirm `UB-8`.

**What no suite reaches.** The system's defining assertion — a toast raised over the open console shows
the console's *text* blurred in its backdrop — is only observable in a running game. It was **confirmed
in game on 2026-09-07** and is therefore discharged, but **nothing pins it against regression**: every
suite here runs in edit mode, `Scenario` is a synchronous `Func<bool>`, and no suite enters play mode.
The harness that would close this is **`NS-12`**, which needs an async scenario host in
`ValidationSuiteRunner` — a change all 30 suites depend on, which is why it is its own item and not a UI
baseline. Every defect this arc's conversion phases produced was of the same shape: invisible to a suite,
found by playing.

---

## 8. Known limitations

- **A stencil `Mask` cannot clip anything drawn in a band.** The band pass binds **no depth-stencil
  attachment** (§2.4), and `Mask` works by writing a stencil reference its children test against —
  neither can reach a buffer that is not bound. Nothing errors: the mask simply passes everything and a
  scroll list paints across the whole screen. **A banded scene must use `RectMask2D`, never `Mask`**,
  which clips in the shader through `_ClipRect` and which `MaskedUIBlur` already supports. All six
  stencil `Mask` users have been converted and the project now has **zero**; the shader's `_Stencil*`
  properties remain declared but inert. The worst case arrived through the shared `Dropdown.prefab` —
  the resolution dropdown spilled 24 of its 27 entries down the screen unclipped in both scenes — which
  makes this a live hazard for any prefab imported from outside the project, not a latent one.
- **UI_BUGS #05 — blur strength scales with screen resolution, and banding multiplies it.** The Kawase
  taps are specified in texels, so the blur radius as a fraction of the screen is inversely proportional
  to resolution. Running the kernel once per occupied band does not introduce the error but does make the
  inconsistency between bands grow with band count. Open; its acceptance test needs a matched capture at
  two resolutions.
- **Render scale now scales the UI, and this cannot be fixed while banding exists.** UI draws into the
  same intermediate target URP's final blit rescales, so at the graphics setting's 30 % floor the
  interface and its text render at 30 % and are upscaled with the world; on the Overlay path UI was
  composited *after* that blit and was immune. The only way back is to draw UI after the upscale — the
  backbuffer — where a band cannot be captured by the next band's blur, so the interleaving is impossible
  there by construction. **Accepted, not open**, and confirmed in game at the 30 % floor in both scenes
  (2026-09-07): everything works, the UI reads softer, nothing regresses.
  <br>Two effects stack there with different causes, worth separating before anyone tunes either: soft
  text and edges are this item (UI rasterized at 30 % and upscaled), while the *blur's* softness shifting
  is `UI_BUGS #05` — render scale is a second lever onto the texel-specified kernel, beside window
  resolution. Fixing #05 changes the second and leaves the first.
- **Occupancy is deliberately conservative in three cases.** A band still counts as occupied when its
  canvas is `enabled == false`, when a `CanvasGroup` has faded it to `alpha 0`, and when its content is
  entirely off-screen or fully clipped by a `RectMask2D`. Each costs one wasted blur; the opposite bias
  would cost a panel its band, which is invisible on screen. Occupancy also only sees uGUI `Graphic`s,
  while the band draw filter matches *any* renderer on the `UI` layer in the band's sorting layer — a
  band drawing through something else must set `UIBlurBand._alwaysOccupied`.
- **Panels within one band cannot stack** (§4.2). Expressible as a one-panel band today; worth
  formalizing as a per-panel mode if it becomes common.
- **The UI blur no longer runs in the Scene view.** The absorbed producer ran there deliberately; the
  band pass is `CameraType.Game` only, because in-pipeline UI would otherwise put full-screen menus over
  the editor viewport. Harmless — with UI off that camera, nothing samples the result.

---

## 9. Rejected alternatives

The standing "do not re-litigate" list.

| Alternative | Why rejected | Date |
|---|---|---|
| **Analytic affine chain of panel rects** — register each panel's rect and `(multiply, additive, alpha)` as a global array and have the shader reconstruct its own backdrop per pixel | Reproduces a lower panel's flat tint but **never its content** — the console's text stays invisible through a toast above it, which is the exact case that makes the artifact obvious. Also axis-aligned rects only, and it needs per-panel data on a shared-material system. | 2026-09-06 |
| **Overlay camera stack**, one URP overlay camera per band | Pays a full URP camera loop — setup, culling, graph compilation — per band, for what is a single filtered draw. Bands are cheap in the chosen design and expensive here, which inverts the cost model the whole thing depends on. Also needs per-camera suppression of the underwater and cloud features. | 2026-09-06 |
| **Generalized flat-fallback overlap policy** — centralize overlap detection so any covered panel drops to a flat color | Removes frost instead of stacking it: correct-looking and less pretty in exactly the situations the feature exists for. | 2026-09-06 |
| **Re-blurring the previous frame's composited back buffer** | Self-referential — a panel's backdrop would contain the panel itself from the previous frame, producing a recursive smear. No latency budget makes this correct. | 2026-09-06 |
| **UI Toolkit's native backdrop-filter** | URP 17.6 ships the same mechanism, but it is gated on `AnyOverlayPanelHasBackdropFilter()` and only UIElements can sample the composite buffer — uGUI has no backdrop-filter API. Using it means porting the blurred surfaces to UIElements beside a mature uGUI stack, with only coarse ordering between a UIToolkit panel and the uGUI canvases. Worth revisiting if uGUI ever gains the API. | 2026-09-06 |
| **Banding by GameObject layer** (the original mechanism, built and then replaced) | Costs a per-object `m_Layer` write on every band member, which on a prefab instance becomes a prefab override *per object* — and most of both scenes' UI is prefab instances. It also needs a repair pass on every enable to survive late-created children. A sorting layer routes the same subtree from one nested canvas with zero overrides; three `ProjectSettings` entries are the cheaper half of that trade. The `UI` layer survives for the different job in §2.3. | 2026-09-06 |
| **Drawing UI at `RenderPassEvent.AfterRendering`** | By then URP has switched the active target to the backbuffer, which has no sampleable texture. Structural rather than a crash fix: a band drawn into the backbuffer cannot be captured by the next band's blur, so interleaving is impossible there regardless. The trap is that `activeColorTexture.IsValid()` still returns **true** — only `isActiveTargetBackBuffer` reveals it. | 2026-09-06 |
| **Gating the base band's blur on occupancy** | Edit-mode registration does not survive domain reloads, so this silently costs every panel its blur in the editor while looking correct in play mode (§2.1). | 2026-09-06 |
| **Relaxing `UIBlurHistory` to a render-graph texture** | The original reason — Overlay canvases sample after the graph — no longer applies now that band draws are graph passes, but the target stays persistent anyway: it was bought with a real bug in which bloom's prefilter reclaimed the pooled memory, and per-camera keying still stops a Game and a Scene view reallocating each other's target every frame. | 2026-09-06 |

---

## Document History

* **v2.2** - Frost tinting settled the other way: **every frosted surface in both scenes now uses the
  darkening `UIBlur.mat`**, for text legibility and one consistent look across screens.
  `MainMenu`'s `SelectPanel` and `CreatePanel` moved off `UIBlurClear.mat`, which is now referenced by
  nothing and kept as the ready-made neutral option. §6's `MainMenu` count follows. That surfaced the
  color drain on `SelectPanel`, the one screen stacking layers past the frost; it was closed by raising
  both to the opacity of the settings tooltip, which occupies the same position and reads cleanly
  because it is effectively opaque — the world tiles from `α 0.502` and the `Scroll View` scrim from
  `α 0.392`, both to **`α 0.95`**. §5 now carries the rules that fell out of it, with the measured
  brightness ladder: every layer past the frost is opaque or it drains, and a `Selectable`'s background
  cannot be black because `ColorTint` multiplies.
* **v2.1** - `UB-8`: occupancy reads a band's **content** instead of its root being enabled, which is
  what the v2.0 headline already claimed. Both scenes kept every declared band root enabled for the
  scene's lifetime, so the idle mask was `15` in `World` (four blurs) and `9` in `MainMenu` (two) where
  the document said one — measured, then corrected here rather than restated. §2.1 gains the content
  test and why it is a transform walk rather than a `GraphicRegistry` query, §6 records the per-scene
  idle masks, §7 goes 16 → 19 baselines with `L17`–`L19`, and §8 records the three conservative cases
  and the `_alwaysOccupied` opt-out.
* **v2.0** - **Promoted from `Design/UI_BLUR_BANDED_COMPOSITING.md` (v1.12)** on `UB-6`'s in-game
  confirmation, per the `docs-sync` promotion protocol; the design was deleted in the same commit and is
  retrievable at `git show 0c2d3f7a:Documentation/Design/UI_BLUR_BANDED_COMPOSITING.md`. Every claim was
  re-verified against code at `0c2d3f7a` rather than carried from the design's prose. Three of the
  design's claims had been reversed by later phases and enter here only in their final form: banding keys
  on a **sorting layer** (§2.2), which the design's own §9 had rejected; a stencil `Mask` is not merely
  untested under banding but **cannot clip there at all** (§8), retracting the design's "`Mask` is
  unused" non-goal; and the routing constraints are **five**, not three. The v1.2 supersession note is
  retired — §2 now describes the producer that exists. §4.2 and §8 no longer say panels cannot blur each
  other; what remains is a per-band limit, not a global one.
* **v1.2** - Supersession note added after `UB-3` replaced the producer with
  `UIBandCompositeRendererFeature`; §1's table and §2's opening corrected. Not a re-audit.
* **v1.1** - `RuntimeUIFactory` took ownership of blur material instances (`RUF-1`…`RUF-3`): §5 records
  the shared entry points, §6 adds the code-built panels and the HUD's negative sorting order.
* **v1.0** - Initial architecture doc, written after the UI_BUGS #06 fix (`36b74204`) completed the
  consumer's UI contract and added the rendered-pixel suite.

---

**Last Updated:** 2026-09-07  
**Next Review:** when `NS-12` lands the play-mode guard, or when UI_BUGS #05 is fixed
