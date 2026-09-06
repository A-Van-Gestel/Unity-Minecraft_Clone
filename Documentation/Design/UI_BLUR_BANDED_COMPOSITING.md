# UI Blur Banded Compositing Design

**Version:** 1.2  
**Date:** 2026-09-06  
**Status:** Proposed design — not implemented.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Moves every UI canvas out of Screen Space - Overlay and into the URP render graph, so the UI blur
> can be **captured more than once per frame**: the screen is re-blurred between UI *bands*, and each
> band's panels sample a `_UIBlurTexture` that already contains the bands beneath them. **The pivotal
> decision is that the fix is a capture-point change, not a shader change** — `Custom/MaskedUIBlur`
> and its five baselines are untouched, because binding the right texture at the right moment is what
> makes a panel blur the panel below it. The cost scales with *occupied* bands, not with panels: the
> common case (HUD only) still runs exactly one blur per frame, which is what it costs today.

**Audited:** 2026-09-06, at commit `76ebf7ee` (branch `main`).
Findings are from static review of `UIBlurRendererFeature.cs`, `UIBlurHistory.cs`,
`CloudPrepassRendererFeature.cs`, `UnderwaterOverlayRendererFeature.cs`, `MaskedUIBlur.shader`,
`UIBlurBlit.shader`, `RuntimeUIFactory.cs`, `ToastManager.cs`, `ToastCard.cs`, `TooltipManager.cs`,
the `Assets/Editor/Validation/UIBlur/` suite and quad renderer, `VoxelEngine-URP-Renderer.asset`,
`ProjectSettings/TagManager.asset`, `EditorBuildSettings.asset`, and the serialized canvases and
cameras of `World.unity` and `MainMenu.unity`. Scene render modes, sorting orders, culling masks and
material references were read out of the serialized scenes, not assumed.

**Amended:** 2026-09-06 — UB-0 ran and returned **GO**. §8 now carries its measured results, and §4.4
gains two corrections the spike forced: the sort criteria and the global-state permission. The spike
itself is preserved on branch `spike/ub0-inpipeline-ui` at commit `e369f061`; it ships nothing.

**Relationship to other documents:**

- [`../Architecture/UI_BLUR_BACKDROP_SYSTEM.md`](../Architecture/UI_BLUR_BACKDROP_SYSTEM.md) — the
  system this extends. Its §8 "Panels cannot blur each other" is what this design closes; its §3
  consumer contract and §4 authoring rules are deliberately preserved unchanged.
- [`../Architecture/RUNTIME_UI_FACTORY.md`](../Architecture/RUNTIME_UI_FACTORY.md) — owns
  `ConfigureCanvas`, which hardcodes the render mode this design changes, and the blur-material
  lifecycle that stays as-is.
- [`../Architecture/TOAST_NOTIFICATION_SYSTEM.md`](../Architecture/TOAST_NOTIFICATION_SYSTEM.md) —
  the consumer whose policy workaround this design deletes (UB-5).
- [`../Architecture/COMMAND_CONSOLE_SYSTEM.md`](../Architecture/COMMAND_CONSOLE_SYSTEM.md) — the
  console panel, whose *text* showing through a toast above it is the motivating fidelity case (§3).
- [`../Architecture/UNDERWATER_AND_SUBMERSION_RENDERING.md`](../Architecture/UNDERWATER_AND_SUBMERSION_RENDERING.md)
  — the overlay feature that must keep recording before the blur; the invariant survives, but it
  gains a second reading (§5).
- [`../Bugs/UI_BUGS.md`](../Bugs/UI_BUGS.md) — **#05** (blur strength scales with resolution) is
  untouched by this design and is *multiplied* by it, since every extra band re-runs the same kernel.
- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  — its "Open / known limitations" item 2, *"Bloom does not appear in the UI blur backdrop"*, is
  **closed** by this design's capture-point move (§5). Its item 5 — the blur target must stay a
  persistent per-camera resource — is preserved, with its rationale updated rather than dropped.

---

## 1. Goals & non-goals

### Goals

1. **A blurred panel composites over the UI beneath it**, including that UI's text and icons, rather
   than punching a hole back to the pre-UI frame.
2. **Scale to arbitrary stacking depth** with one blur *per occupied band*, not per panel — an idle
   HUD-only frame must cost exactly what it costs today.
3. **Leave the consumer contract alone.** `Custom/MaskedUIBlur`, `UIBlur.mat`, the six authored
   tints, and the five `Validate UI Blur Render` baselines stay valid.
4. **Work in every scene**, `World` and `MainMenu` alike, so the main menu can adopt frosted panels
   it does not have today.
5. **Delete the policy workarounds** rather than widening them —
   `ToastManager.IsBlurSuppressed` and the `RuntimeUIFactory.ApplyBlurBackground` flat-fallback
   guidance exist only because of the limitation this design removes.

### Non-goals (v1)

- **Panels within the *same* band blurring each other.** The band is the granularity; two panels in
  one band still cannot stack. Adding a band is cheap and declarative, so this is a tuning matter,
  not a wall. A per-panel capture is a **v2 extension**, see §7's roadmap.
- **Fixing UI_BUGS #05.** The resolution-dependent kernel is orthogonal and stays open. This design
  makes it *more* visible by running the kernel more often, which is noted in §5, not fixed here.
- **Blurred graphics under a stencil `Mask`.** Still unexercised in the project (`RectMask2D` is
  used, `Mask` is not); the shader declares the state and nothing tests it, exactly as today.
- **World-space / diegetic UI.** Out of scope permanently — this design is about screen-space bands.

---

## 2. Current state (what exists today)

| Area                        | State                                                                                                                                                                            |
|-----------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Capture point               | One Kawase chain at `RenderPassEvent.AfterRenderingTransparents`, into a per-camera `UIBlurHistory` RTHandle, published as `_UIBlurTexture` from an unsafe pass. One capture, ever. |
| Why UI is invisible to it   | Every canvas is Screen Space - Overlay (`World.unity` `m_RenderMode: 0`; `MainMenu.unity` `m_RenderMode: 0`; `RuntimeUIFactory.cs:54` for all four code-built canvases). URP 17.6 *does* draw overlay uGUI inside the render graph — `DrawScreenSpaceUIPass` at `AfterRendering + 2`, i.e. **after** `FinalBlitPass` at `+1` — but it emits **all** overlay UI as one renderer list (`UISubset.UIToolkit_UGUI`, `DrawScreenSpaceUIPass.cs:263`) at a queue offset no user feature can address (`RenderPassEvent` tops out at `AfterRendering`; the `+1`/`+2` offsets are internal constants). So the blur cannot be interleaved between canvases without taking UI off that path. |
| Consumer                    | `MaskedUIBlur.shader` samples `_UIBlurTexture` by screen UV. Output is affine in the sample: `blur * _MultiplyColor + _AdditiveColor`, times vertex color.                        |
| Blurred surfaces            | 5 in `World.unity` sharing `UIBlur.mat` (grep of its GUID: 5 hits) + Benchmark HUD at sortingOrder −10, benchmark results at 200, `ConsoleUI` at 100, and toast cards at 250 — 8 surfaces across 4 code-built canvases. |
| `MainMenu.unity`            | **Zero** blur consumers (0 GUID hits). One canvas, Overlay, sortingOrder 0. Adoption here is new work, not conversion.                                                            |
| Existing workarounds        | `ToastManager.IsBlurSuppressed` polls `WorldUIManager.IsPauseMenuOpen` each frame and flattens cards; `ApplyBlurBackground`'s remarks instruct callers to pass `null` where overlap is possible. Both are policy, and both miss bounded panels — a bottom-left toast over the open console still paints un-dimmed world (observed 2026-09-02). |
| In-repo draw-pass precedent | `CloudPrepassRendererFeature.cs:73-122` already does render-graph `DrawRenderers`: `RendererListParams(cullResults, drawingSettings, filteringSettings)` → `UseRendererList` → `SetRenderAttachment(activeColorTexture, ReadWrite)` → `DrawRendererList`. The band pass is this file with a different sort and filter, and **without** its depth attachment (§4.4). |
| Canvases built in code      | **Four**, not three: `BenchmarkUIBuilder.cs:57` (HUD), `BenchmarkUIBuilder.cs:99` (results), `ConsoleUI.cs:400`, `ToastManager.cs:124`. All four route through `RuntimeUIFactory.CreateCanvas`/`ConfigureCanvas`, which hardcodes the render mode at `RuntimeUIFactory.cs:54`. |
| Colour space                | Project is **Linear** (`ProjectSettings.asset` `m_ActiveColorSpace: 1`); the URP asset has HDR on. `FinalBlitPass` applies the `LinearToSRGBConversion` keyword from `cameraData.requireSrgbConversion` (`FinalBlitPass.cs:200`), so anything drawn into the camera colour is encoded on the way out — which overlay UI, drawn after that blit, currently escapes. |
| Canvas gamma flag           | `m_VertexColorAlwaysGammaSpace` is **0 in `World.unity:4637` but 1 in `MainMenu.unity:3258`** — the two scenes already disagree about how UI vertex colours are treated. Pre-existing and unrelated to this design, but it lands directly in UB-6's path. Both carry `m_AdditionalShaderChannelsFlag: 25` (Tangent + Normal + TexCoord1). |
| Canvas draw path            | URP's `DrawObjectsPass` includes `SortingCriteria.CanvasOrder` in its sort flags (`DrawObjectsPass.cs:205`), and URP's runtime contains **no** special-casing for `ScreenSpaceCamera` canvases — so such canvases already render through the standard cull-results → `DrawRenderers` path this design filters. |
| Tooltip positioning         | `TooltipManager` assigns screen-pixel coordinates straight into a world-space transform — `_tooltipRect.position = finalPos` at `:244`, `:282`, `:316`, with the comment *"Setting position directly works perfectly for Overlay canvases"* at `:243`. That identity holds **only** for Overlay canvases. Tooltips are also instantiated into `_parentCanvas` (`:108`), which in `World.unity` is the single scene canvas. |
| Layer / sorting budget      | `TagManager.asset`: layer 5 is `UI`; layers 3 and 7–31 are free. **Only one sorting layer (`Default`) exists**, so bands must key on GameObject layer, not `SortingLayer`.        |
| Layer discipline            | **None existed before UB-1.** A repo-wide grep for `.layer =` / `SetLayerRecursively` returns *zero* hits, so every runtime-built UI object (`RuntimeUIFactory.cs:38,165,190,220,244,312`, `ToastCard.cs:102,171,196,216`, `ToastManager.cs:327`, `TooltipManager.cs:108`) lands on layer 0 `Default` — **the same layer as world geometry**. Only the 27 scene-authored objects in `World.unity` are on layer 5. Layer-based band filtering therefore has no foundation to build on; UB-1 has to create one. |
| Camera setup                | One camera per scene, `m_CullingMask: 4294967295` in both — UI is already in `cullResults`, so no culling-mask change is needed.                                                  |
| Renderer feature order      | `VoxelEngine-URP-Renderer.asset`: Underwater overlay, then UI blur, then cloud prepass. Underwater-before-blur is a load-bearing invariant, asserted by Underwater **B17**.       |
| Validation reach            | `Validate UI Blur Render` (5 baselines) drives the material **offscreen with a synthetic texture**. It tests the consumer contract in isolation and **cannot observe anything in this design** — it will stay green whether or not banding works. |

---

## 3. Decision: where the second capture comes from

`_UIBlurTexture` contains no UI, so a panel replaces what is beneath it. Every candidate fix is an
answer to "how does a panel learn what is underneath it", and they differ enormously in fidelity and
in blast radius.

### Option A — Analytic affine chain (rejected)

Register each blurred panel's screen rect and its `(multiply, additive, alpha)`, upload them as a
global array, and have the shader reconstruct its own backdrop per pixel by composing the affine
transforms of the layers below it. No capture change, no render-order change.

- ✅ Genuinely cheap: one blur, one `SetGlobalVectorArray` per frame, a bounded rect loop per pixel.
  Scales to *n* panels by construction and is testable by the existing offscreen harness.
- ✅ Lowest blast radius of every option — contained entirely within UI code and one shader.
- ❌ **It can only reproduce a lower panel's flat tint, never its content.** The console's text
  drawn through a toast above it stays invisible, which is the exact case that makes the result read
  as wrong. A frosted surface that omits what is behind it is not frosted glass; it is a tinted
  rectangle that happens to sit above another one.
- ❌ Axis-aligned rects only; rounded corners, rotation and mask intersections all approximate.
- ❌ Requires per-panel data on a shared-material system (the five scene panels share `UIBlur.mat`,
  toast cards share one material per variant), forcing either a vertex channel or per-panel material
  instances — new surface on top of the material-lifetime hazard the system already documents.

### Option B — In-pipeline banded compositing ✅ **CHOSEN**

Move every canvas into the render graph and draw the UI in ordered *bands*, re-blurring the camera
color between bands and rebinding `_UIBlurTexture` before each one.

This wins on the thing Option A cannot buy at any price: the capture is a real capture, so whatever
band 0 drew — panels, text, icons, sprites, a scrolling console backlog — is genuinely in the
texture band 1 samples. It is also the only option that generalizes without further design work:
adding a band is a declaration, and a new scene adopts the system by declaring bands rather than by
inheriting a per-panel bookkeeping contract.

It is the largest change of the three, and the honest reason it is affordable is
`CloudPrepassRendererFeature`: the project already draws a filtered renderer list into the camera
color from a render-graph pass, so the novel part is the *interleaving*, not the drawing.

**Unity independently arrived at the same mechanism.** URP 17.6's backdrop-filter support
(`DrawScreenSpaceUIPass.RenderOverlayUIToolkitAndUGUIComposite`) seeds an offscreen buffer with the
scene, renders UI into it, and lets elements sample it *as it fills* — which is this design's band
loop with the bands collapsed to individual elements. It is unusable here because it is gated on
`UIElementsRuntimeUtility.AnyOverlayPanelHasBackdropFilter()` and only UI Toolkit elements can
sample the buffer (§9), but it is strong corroboration that the capture-point approach is the one
the problem wants.

Two structural properties make it cheaper than it first looks:

- **No shader change and no per-panel data.** A panel samples `_UIBlurTexture` exactly as it does
  today; correctness comes from *when* it is drawn. The consumer contract and its baselines survive
  untouched.
- **Cost tracks occupied bands.** A band with nothing to draw is skipped along with the blur that
  would have preceded it, so an idle HUD frame runs one blur — today's cost.

### Option C — Overlay camera stack (rejected)

One URP overlay camera per band, each with its own renderer and blur feature.

- ✅ Reaches the same fidelity as Option B, and the band boundary is a camera, which is a concept
  the team already reasons about.
- ❌ **Pays a full URP camera loop per band** — setup, culling, and render-graph compilation for
  what is a single filtered draw in Option B. Bands are cheap in B and expensive here, which
  inverts the incentive the design depends on.
- ❌ Every band camera inherits the renderer's feature list, so the underwater overlay and cloud
  prepass need per-camera suppression that Option B gets for free.

### Option D — Generalized flat-fallback policy (rejected)

Keep the current architecture; centralize overlap detection so any covered blurred panel drops to a
flat color automatically.

- ✅ Cheapest possible change, and it removes the per-consumer policy duplication.
- ❌ **It removes frost instead of stacking it.** The result is correct-looking and less pretty in
  exactly the situations the feature exists for. `UI_BLUR_BACKDROP_SYSTEM.md` §8 already names this
  as the wrong fix.

---

## 4. Architecture

### 4.1 The band loop

A band is an ordered group of UI *subtrees* drawn together, identified by a GameObject layer (§4.2
explains why a subtree and not a canvas). One feature owns the whole walk — **including band 0's
capture** — at `RenderPassEvent.AfterRendering`, so every band samples a screen that has been through
the post stack (§5). The pass walks bands in ascending order, re-blurring between them:

```
RenderPassEvent.AfterRendering
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        the post-processed world — bloom included
├─ draw band 0                                 HUD · toolbar · creative inventory
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        now contains the toolbar, its icons and counts
├─ draw band 1                                 pause · settings · help menus
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        now contains the open menu's labels
├─ draw band 2                                 console · benchmark results
│
├─ blur(cameraColor) ──▶ _UIBlurTexture        now contains the console's backlog text
└─ draw band 3                                 toasts · tooltips
```

The blur before band *k* is skipped when band *k* has nothing to draw, and the blur after the last
occupied band is never recorded at all. An idle frame therefore records one blur and one draw.

### 4.2 Band assignment

**A band is a subtree, not a canvas.** This is forced by the scene: `World.unity` holds exactly one
`Canvas` (line 4574), under which `Toolbar`, `CreativeInventory` and `PauseMenuContainer` are
*siblings* beneath `SafeArea`. A per-canvas band could not separate the toolbar from the pause menu
— the likeliest overlap in the game — so the full-screen menus would still punch a hole over the
toolbar and goal 1 would be unmet in its most common case. `FilteringSettings.layerMask` filters per
renderer, so a subtree of a canvas can be its own band without splitting the canvas.

Bands replace `sortingOrder` as the cross-band ordering authority; the mapping preserves today's
observed paint order rather than redesigning it:

| Band | Layer      | Occupants                                                      | Declared on                              | Today's order |
|:----:|------------|-------------------------------------------------------------------|------------------------------------------|---------------|
| 0    | `UIBand0`  | Benchmark HUD, `Toolbar`, `CreativeInventory`                     | HUD canvas root; the two scene subtrees  | −10, 0        |
| 1    | `UIBand1`  | `PauseMenu`, `SettingsMenu`, `HelpMenu`                           | `PauseMenuContainer`                     | 0             |
| 2    | `UIBand2`  | `ConsoleUI` panel, benchmark results overlay                      | their canvas roots                       | 100, 200      |
| 3    | `UIBand3`  | Toast cards, tooltips                                             | toast canvas root; a new tooltip root    | 250           |

Two consequences the per-canvas reading hid:

- **Bands 0 and 1 are subtrees of the same canvas.** Splitting one canvas across two draw passes
  costs its batching across the split, which is why the split is placed where a real occlusion
  exists rather than per panel.
- **Tooltips must be reparented.** `TooltipManager` instantiates into `_parentCanvas`
  (`TooltipManager.cs:108`), which is band 0's canvas, so a band-3 tooltip would live under a
  band-0 root. It needs its own band-3 root (UB-3), or every enable would stomp its layer back.

The Benchmark HUD's negative sorting order is load-bearing today only because an opaque blurred
panel punched a hole over the paused screen (UI_BUGS #06). Under banding that pressure disappears,
but the order is preserved anyway — this design does not relitigate it.

### 4.3 Components

```
┌──────────────────────────┐    declares band      ┌────────────────────────┐
│ UIBlurBand (per subtree) │ ────────────────────▶ │ UIBandRegistry         │
│ · owns its band's layer  │                       │ · occupied-band bitmask│
│ · registers on enable    │                       │ · static, domain-reset │
└──────────────────────────┘                       └───────────┬────────────┘
                                                               │ reads
                                                   ┌───────────▼────────────────────┐
                                                   │ UIBandCompositeRendererFeature │
                                                   │ · per band: blur, then draw    │
                                                   │ · AfterRendering               │
                                                   └───────────┬────────────────────┘
                                                               │ uses
                                                   ┌───────────▼────────────┐
                                                   │ UIBlurChain (extracted)│
                                                   │ · the Kawase ping-pong │
                                                   │   lifted verbatim from │
                                                   │   UIBlurRendererFeature│
                                                   └────────────────────────┘
```

`UIBlurBand` is the explicit registration point: a subtree declares its band, and the component owns
that band's layer so nothing depends on hand-authored layer assignment surviving a prefab edit.
`UIBandRegistry` holds one mutable static (the occupied-band mask) and therefore needs a
`[RuntimeInitializeOnLoadMethod]` reset per `CLAUDE.md`'s domain-reload rule.

**A one-shot recursive layer set on enable is not sufficient**, and this is the sharpest edge in the
design. Every occupant of bands 2 and 3 is created *after* its band root enables — `ToastCard.cs:102`,
`:171`, `:196`, `:216`, `ToastManager.cs:327`, `TooltipManager.cs:108` — and Unity's `new GameObject`
defaults to layer 0 with no inheritance on reparent. A late child would therefore be filtered out of
every band draw *and* fall back inside URP's own transparent mask, drawing at the wrong time. The
exact scenario this design exists for — a toast over the console — is the one such a contract would
miss. The layer must therefore be applied **at creation**, which the project has nowhere to do it
today (§2, "Layer discipline": zero `.layer =` assignments repo-wide). UB-1 owns building that:

- `RuntimeUIFactory`'s creation helpers take the layer from the nearest `UIBlurBand` ancestor.
- `UIBlurBand` re-applies its layer to its subtree on enable, as a repair pass rather than the
  primary mechanism.
- A UB-1 baseline asserts **no `Graphic` under a band root sits on a foreign layer**, which is the
  assertion that turns this from a convention into a contract.

`UIBlurChain` is a pure extraction of the existing Kawase ping-pong so the producer and the band
pass cannot drift apart; the extraction must not change the kernel, the offset progression, or the
`UIBlurHistory` target selection.

### 4.4 The band draw pass

Modeled directly on `CloudPrepassRendererFeature.CloudPrepass`:

- `FilteringSettings` — `RenderQueueRange.transparent`, `layerMask` = the band's layer.
- `DrawingSettings` — shader tags `SRPDefaultUnlit` and `UniversalForward` (UGUI, TMP and
  `MaskedUIBlur` all declare passes with no `LightMode`, so they resolve under `SRPDefaultUnlit`),
  and **`SortingCriteria.CommonTransparent`**, which is also what `CloudPrepass` uses.
  **Not `CanvasOrder`.** UB-0 swept the options against a pause menu with title text: `CanvasOrder`
  (0x20) and `RenderQueue | CanvasOrder` (0x22) both render the panel *over* its own child text,
  while `CommonTransparent` (0x17), URP's opaque combo (0x33) and even `None` all order it
  correctly. The natural renderer-list order is already right; `CanvasOrder` without `SortingLayer`
  is what breaks it. The failure is silent — the panel still draws, so only the missing text
  reveals it.
- `SetRenderAttachment(activeColorTexture, 0, AccessFlags.ReadWrite)` — read-write, not write, for
  the same reason the cloud pass documents: UI alpha-blends against what is already there.
- **`builder.AllowGlobalStateModification(true)` is mandatory**, and must be set at record time. A
  raster pass otherwise rejects the `unity_GUIZTestMode` write below with
  `InvalidOperationException: Modifying global state from this command buffer is not allowed` — the
  same rule that forces `UIBlurRendererFeature`'s `SetGlobalTexture` into an unsafe pass. Measured
  in UB-0, where it aborted the whole render graph rather than degrading quietly.
- **No depth attachment, and `unity_GUIZTestMode` = `CompareFunction.Always`.** These two go
  together and the precedent gets it the other way round: `CloudPrepass` also calls
  `SetRenderAttachmentDepth(activeDepthTexture, ReadWrite)`, because clouds *are* world geometry.
  UI is not — it must never depth-test against terrain, or a panel disappears behind a hill. Every
  UGUI shader, `MaskedUIBlur` included, declares `ZTest [unity_GUIZTestMode]`, and that global is
  normally written by the UI system on the Overlay path this design leaves behind, so it is stale
  here and must be set explicitly. **Copying the cloud pass faithfully is a bug**; this bullet is
  the deviation.

### 4.5 What the canvases change to

`RenderMode.ScreenSpaceCamera` with `worldCamera` set. The edit is a **one-line change inside
`RuntimeUIFactory.ConfigureCanvas` (`RuntimeUIFactory.cs:54`)**, which all four code-built canvases
route through (§2), plus the serialized canvases of `World.unity` and `MainMenu.unity`. It is not an
edit at the call sites.

URP's own passes must then exclude the band layers via **all three** masks on
`UniversalRendererData` — `m_PrepassLayerMask`, `opaqueLayerMask` and `transparentLayerMask`, each
currently `4294967295` in `VoxelEngine-URP-Renderer.asset:51,185,198`. The prepass mask is the one
that is easy to miss and expensive to get wrong: band layers left inside it put UI geometry into the
depth prepass, writing depth on the camera plane in front of the world, which corrupts every depth
consumer — the underwater feature and the fluid surface both read camera depth. The two public
`LayerMask` properties in URP 17.6.0 make the exclusion assertable from a validation suite in the
Underwater B17 style, and UB-1's assertion must cover the prepass mask or it passes while the
misconfiguration ships.

---

## 5. Prerequisites & integration points

- ✅ **UB-0 has run and returned GO** (§8). Row 1 — the only gating row — confirmed that canvas
  geometry draws from a URP raster pass at `AfterRendering`, and row 3 retired the colour-space
  NO-GO branch by measurement. The spike is preserved on `spike/ub0-inpipeline-ui` (`e369f061`)
  as a working reference for UB-2; it ships nothing.
- ⚠️ **`TooltipManager` is a guaranteed rewrite, not a verification item.** It assigns screen-pixel
  coordinates directly into a world-space transform at `:244`, `:282` and `:316`, and says so in a
  comment at `:243`: *"Setting position directly works perfectly for Overlay canvases."* That
  identity holds only because an Overlay canvas' world space **is** screen pixels. Under
  `ScreenSpaceCamera` the canvas plane sits at `planeDistance` in camera world units, so a tooltip
  at mouse (960, 540) is placed 960 world units right and 540 up of the origin — off-screen on the
  first hover. It needs `RectTransformUtility.ScreenPointToLocalPointInRectangle` and an
  `anchoredPosition`, which is UB-3's scope, not a check.
- **`UIBlurRendererFeature` is absorbed into `UIBandCompositeRendererFeature`.** Every capture,
  band 0's included, moves to `RenderPassEvent.AfterRendering`, so a separate producer at
  `AfterRenderingTransparents` would only publish a `_UIBlurTexture` that the band walk immediately
  overwrites. `UIBlurChain` is lifted out of it and owned by the composite feature; the old feature
  and its entry in `VoxelEngine-URP-Renderer.asset` go away.
- **This closes an accepted limitation elsewhere.** `LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`
  records *"Bloom does not appear in the UI blur backdrop"* as open-and-accepted, noting that
  *"changing it means moving the injection point past post-processing"* — which is precisely this
  move. Frosted panels will show bloom for the first time. That is the intended outcome, not a
  side effect, and it is why UB-4's tint retune is real work rather than a verification pass: all
  eight blurred surfaces change appearance at once and the six authored tints were calibrated
  against a pre-bloom capture.
- ⚠️ **Underwater B17 breaks and must be rewritten in UB-2.** It scans the renderer list for
  `feature is UIBlurRendererFeature` and asserts `overlayIndex < blurIndex`
  (`UnderwaterRenderValidationSuite.Overlay.cs:1184,1193,1197`). With the feature absorbed, both the
  membership check and the ordering check fail. The invariant they protect — the blur samples an
  already-tinted screen — does not disappear; it becomes **structural**, since `AfterRendering` is
  unconditionally later than the overlay's `AfterRenderingTransparents` regardless of list order.
  B17's replacement therefore asserts the composite feature's presence and its pass event, not a
  list index, and that is a strictly stronger guarantee than the one it retires.
- **`UIBlurHistory` stays, with its rationale updated.** The same report's item 5 records that the
  blur target must be a persistent per-camera resource, never a render-graph texture — bought with
  a real bug in which bloom's prefilter reclaimed the pooled target. The original reason (Overlay
  canvases sample *after* the graph) stops applying once band draws are graph passes, but the
  target is kept anyway: relaxing a hard-won invariant is not this design's business, and per-camera
  keying still prevents a Game and a Scene view from reallocating each other's target.
- **The band pass runs for `CameraType.Game` only**, exactly as `UnderwaterOverlayRendererFeature`
  already does. In-pipeline UI would otherwise render in the Scene view, which Overlay UI does not
  do today, putting full-screen menus over the editor viewport and adding a second camera path for
  band bugs to hide in. Note this also ends the blur running in the Scene view, which the absorbed
  feature did deliberately — harmless, because with UI off that camera nothing samples the result.
- **UI_BUGS #05 is amplified.** Running the kernel once per occupied band multiplies the
  resolution-dependent radius error rather than introducing it. If #05 is fixed first the two
  changes are independent; if not, the visible inconsistency between bands grows with band count.
- **Always Included Shaders unchanged.** `Custom/MaskedUIBlur` stays listed; no new shader ships.
- **Reserved seat: per-panel capture.** Nothing in the band design forbids a future band that holds
  exactly one panel, which is how a v2 per-panel mode would be expressed without restructuring.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                                                             |
|-------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Not applicable — no voxel data is touched.                                                                                                                            |
| Burst jobs 100 % Burst-compatible               | Not applicable — nothing under `Assets/Scripts/Jobs/` changes.                                                                                                        |
| No GC / LINQ in hot paths                       | `RecordRenderGraph` runs per camera per frame: pass data classes are allocated by the render graph as today, the occupied-band mask is an `int`, and band iteration is a bit loop over a fixed count. No per-frame collection allocation, no LINQ. |
| Pooling conventions                             | Blur targets keep coming from `UIBlurHistory` (per camera) and render-graph temporaries for the ping-pong, exactly as the producer does today. Band count does not multiply persistent targets — the chain reuses one pair. |
| No BinaryFormatter/JSON for terrain             | Not applicable — nothing reaches disk.                                                                                                                                |
| BlockIDs constants, no raw IDs                  | Not applicable — no block references.                                                                                                                                 |
| Mutable statics reset on play-mode entry        | `UIBandRegistry`'s occupied-band mask is the one mutable static; it gets its own `[RuntimeInitializeOnLoadMethod]` (the class has none today, so no UDR0005 conflict). |
| Shader `#pragma target 4.5` floor               | No shader is modified. `UIBlurBlit` and `MaskedUIBlur` keep their existing pragmas and interpolator counts.                                                            |
| No magic numbers                                | Band count, band layer names and the band-to-layer mapping are named constants on `UIBandRegistry`; `private const` in `SCREAMING_CASE`, `public const` in `PascalCase`. |
| Coordinate spaces named for their space         | The only space crossing left in the shader is screen UV, unchanged. `TooltipManager`'s screen-pixels-into-world-position assignment is a real space violation that Overlay canvases happened to make harmless; UB-3 converts it to `ScreenPointToLocalPointInRectangle` + `anchoredPosition`, which names both spaces correctly. |
| Mutable statics reset on play-mode entry        | Covered above; `UIBlurBand` itself holds no statics, so no UDR0005 conflict is introduced by the per-subtree component.                                              |
| Layers are assigned, not assumed                | New discipline this design creates (§2 shows none exists): band layers are applied at GameObject creation and asserted by a UB-1 baseline, rather than relying on scene authoring or reparent inheritance, which Unity does not provide. |

---

## 7. Phased implementation plan

| Phase                              | Scope                                                                                                                                                                                                                                    | Effort | Depends on   | Status |
|------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|--------------|--------|
| **UB-0 — Feasibility spike**       | Throwaway branch. One canvas → Screen Space - Camera, one band pass at `AfterRendering`, all three layer masks cleared. Measured against `main`; results in §8. Gated on row 1. Shipped nothing. | 🟡     | —            | ✅ 2026-09-06 (GO) |
| **UB-1 — Layer discipline**        | The foundation §2 says does not exist: `UIBandId`/`UIBandLayers`, the four band layers in the Tag Manager, layer assignment **at creation** via `RuntimeUIFactory.Attach` (15 call sites), `UIBlurBand`'s enable-time repair pass, and the no-foreign-layer baselines. | 🟡     | UB-0         | ✅ 2026-09-06 |
| **UB-2 — Band infrastructure**     | `UIBlurChain` lifted out of `UIBlurRendererFeature`, which is then **absorbed** (§5); `UIBandRegistry`; `UIBandCompositeRendererFeature` at `AfterRendering`, Game camera only; all three renderer-asset masks; **rewrite Underwater B17**, which breaks by construction. | 🔴     | UB-1         | —      |
| **UB-3 — Canvas conversion**       | Render mode at `RuntimeUIFactory.cs:54`; `UIBlurBand` on the four band roots in `World.unity` + the four code-built canvases; **`TooltipManager` repositioning rewrite** (§5) and its new band-3 root.                                     | 🔴     | UB-2         | —      |
| **UB-4 — Look reconciliation**     | Re-tune the six authored tints against the new post-processed capture — all eight blurred surfaces now show bloom (§5). In-game A/B against pre-UB-3 captures; closes the lighting report's accepted limitation 2. | 🟡     | UB-3         | —      |
| **UB-5 — Workaround removal**      | Delete `ToastManager._wasBlurSuppressed`/`Update`/`IsBlurSuppressed`/`ApplyBackdropForUIState` and the suppression branch in `BackdropMaterialFor`; correct the now-false XML remarks in `RuntimeUIFactory`, `ToastManager`, `ToastCard`.  | 🟢     | UB-4         | —      |
| **UB-6 — MainMenu adoption**       | Convert `MainMenu.unity`'s canvas, declare its bands, and add the frosted panels it does not have today.                                                                                                                                   | 🟢     | UB-4         | —      |
| **UB-7 — Validation & promotion**  | Play-mode capture harness + the baseline that actually pins the fix; `docs-sync` promotion of this doc into `UI_BLUR_BACKDROP_SYSTEM.md`.                                                                                                  | 🟡     | UB-5, UB-6   | —      |

UB-0 through UB-5 is the minimal set that delivers standalone value: it closes §8's limitation for
the `World` scene and removes the policy debt. UB-6 is additive adoption; UB-7 is what makes the
result defensible over time.

**UB-5 must not land before UB-4.** Deleting the toast suppression policy while band 0's capture
point is still unsettled removes the fallback that currently covers the toast-over-menu case, so a
regression there would surface as a visual defect with no safety net. For the same reason UB-6
depends on UB-4 rather than UB-3: adopting new blurred panels in `MainMenu` before the tints are
settled means tuning them twice.

**Validation is built alongside, not after.** The existing `Validate UI Blur Render` suite tests the
consumer contract offscreen and **cannot see any of this** — it will stay green whether banding
works or not, which makes it a false-green risk rather than a gate. Each phase therefore adds its
own baselines:

- **UB-1** — the no-foreign-layer baseline: walk each band root's subtree and assert every `Graphic`
  under it carries that band's layer, including objects created after the root enabled. This is the
  assertion that makes §4.3's creation-time contract real rather than aspirational, and it is the
  one that catches the toast-card and tooltip cases directly.
- **UB-2** — wiring assertions in the Underwater **B17** style, deterministic and cheap: the
  composite feature is present, ordered after the underwater overlay, the band layers are distinct
  and non-overlapping, and **all three** of `m_PrepassLayerMask` / `opaqueLayerMask` /
  `transparentLayerMask` have every band layer cleared. Omitting the prepass mask here is how the
  assertion passes while the misconfiguration ships (§4.5). Plus a registry baseline pinning band
  occupancy and iteration order for a synthetic set of roots, asserting the *count* of recorded
  blurs as well as the order, so a band silently dropping from the walk cannot pass.
- **UB-4** — `Unity_Camera_Capture` A/B baselines of the toolbar and pause menu against pre-UB-3
  captures, which is the only way the deliberate look change (§5) is measurable rather than argued.
  Capture with bloom both on and off: the on/off delta inside a panel backdrop is the direct evidence
  that the capture point actually moved past the post stack.
- **UB-7** — the play-mode capture baseline that actually pins the fix: a toast raised over the open
  console must show the console's **text** blurred in its backdrop. That single assertion is the
  design's reason for existing, and no offscreen harness can make it.

### Extension roadmap (post-UB-7, in intended order)

| Version | Extension                                                                                                                      |
|---------|----------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Per-panel bands for stacking *within* a band — expressible today as a one-panel band, worth formalizing if it becomes common.   |
| **v2**  | Per-band blur parameters (radius/iterations), so a toast can frost more softly than a full-screen menu.                        |
| **v3+** | Fixing UI_BUGS #05 in `UIBlurBlit` so the kernel is resolution-independent across every band — gets its own entry when started. |

---

## 8. UB-0 results

UB-0 ran on 2026-09-06 (branch `spike/ub0-inpipeline-ui`, commit `e369f061`): one canvas converted to
Screen Space - Camera, one band pass at `AfterRendering`, all three layer masks cleared. Measurements
are rendered-pixel readbacks from the Game camera, not visual inspection.

**Verdict: GO.** Row 1 was the only gating row. Nothing in rows 2-5 argues against proceeding, and
row 3 removed the design's largest stated risk.

| # | What was measured                          | Result                                                                                                                                                                     |
|---|--------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| 1 | Does band UI draw at all?                  | ✅ **GO.** The lit region is exactly the toolbar's rect (218 of 640 columns; the toolbar spans ~218). The value is *predicted* by `UI_BLUR_BACKDROP_SYSTEM.md` §3: a `0.0682` background blurred and scaled by `_MultiplyColor` (linear `0.1437`) gives `0.0098`; measured `0.0100`. The consumer contract survives the move untouched. |
| 2 | Does TMP render from the band pass?        | ✅ Yes — 732 glyph pixels at `0.9995`. The Built-in-CG-shader concern was unfounded. What looked like a TMP failure was §4.4's sort criteria hiding text behind its own panel; see the correction there. |
| 3 | How far do UI colours shift?               | ✅ **No shift.** Glyph `0.9995` against an authored `1.000`, and the panel matches its formula to 2%. The linear→sRGB encode this design was expected to introduce does not materialise at this injection point — which retires the NO-GO branch that stood here. |
| 4 | Do clicks still land?                      | ✅ One hit at the toolbar centre (`ItemSlot (4)`), zero at a control point away from UI. `GraphicRaycaster.eventCamera` resolves to `Main Camera` on the converted canvas, as its source predicted. |
| 5 | What does the extra pass cost?             | ⚠️ **Not measurable from a one-band spike**, and reported as such rather than dressed up. Single samples 9.35 ms (on) vs 9.23 ms (off) sit inside the ±1-5% noise floor and were taken at different stages of world load. Structurally the band draw *replaces* work URP's transparent pass already did, so its marginal cost is ~0; the design's real added cost is the per-band blur chain that UB-2 introduces. UB-2 owns that number. |

Two further results worth keeping:

- **The layer-mask exclusion works, and URP does not double-draw.** A same-pixel A/B with the band
  pass toggled off and on moved the toolbar pixel by `0.8426`, so the band pass is the only thing
  drawing UI once the three masks exclude its layer.
- **A full world load and play session produced zero console errors** with UI rendering in-pipeline.

Both §4.4 corrections came out of this run: the sort criteria, and the global-state permission.
Neither was visible from static reading, which is what the spike existed to catch.

---

## 9. Rejected alternatives

| Alternative                                     | Why rejected                                                                                                                                                                                                              | Date       |
|-------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------|
| Analytic affine chain of panel rects (Option A) | Reproduces a lower panel's flat tint but never its content — the console's text stays invisible through a toast above it, which is the case that makes the artifact obvious. Also needs per-panel data on shared materials. | 2026-09-06 |
| Overlay camera stack, one camera per band (Option C) | Pays a full URP camera loop per band, inverting the cost model this design depends on, and needs per-camera suppression of the underwater and cloud features that Option B gets for free.                             | 2026-09-06 |
| Generalized flat-fallback overlap policy (Option D) | Removes frost instead of stacking it. Already named as the wrong fix in `UI_BLUR_BACKDROP_SYSTEM.md` §8.                                                                                                               | 2026-09-06 |
| Banding by `SortingLayer` rather than GameObject layer | The project defines exactly one sorting layer (`Default`), so this needs a `ProjectSettings` change to buy what a free GameObject layer gives directly, and `FilteringSettings.layerMask` is the better-trodden filter. | 2026-09-06 |
| Re-blurring the previous frame's composited back buffer | Self-referential: a panel's backdrop would contain the panel itself from the previous frame, producing a recursive smear. No latency budget makes this correct.                                                       | 2026-09-06 |
| Adopting UI Toolkit's native backdrop-filter for blurred surfaces | URP 17.6 already ships the mechanism (§3), but it is gated on `AnyOverlayPanelHasBackdropFilter()` and only UIElements can sample the composite buffer — uGUI has no backdrop-filter API (zero hits across `com.unity.ugui@2.6.0`). Using it means porting the blurred surfaces to UIElements beside a mature uGUI stack, with only coarse ordering between a UIToolkit panel and the uGUI canvases. Worth revisiting if uGUI ever gains the API. | 2026-09-06 |

---

## Document History

* **v1.2** - UB-1 shipped and was confirmed in game: band layers declared, layer assignment moved to
  creation across 15 sites, `UI Band Layers` suite registered (aggregate 29 -> 30 suites).
* **v1.1** - UB-0 ran and returned GO: §8 replaced with its measured results, §4.4 corrected on the
  sort criteria (`CommonTransparent`, not `CanvasOrder`) and on the mandatory
  `AllowGlobalStateModification(true)`, UB-0 marked complete in §5 and §7.
* **v1.0** - Initial design

---

**Last Updated:** 2026-09-06  
**Next Review:** when UB-1 (layer discipline) starts
