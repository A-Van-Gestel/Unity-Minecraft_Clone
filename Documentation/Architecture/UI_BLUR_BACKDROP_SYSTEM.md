# UI Blur Backdrop System

**Version:** 1.1  
**Date:** 2026-08-15  
**Status:** **Implemented (Stable)** — the producer (`UIBlurRendererFeature`) and the consumer shader
(`Custom/MaskedUIBlur`) both ship. The consumer's UI contract was completed in `36b74204` (UI_BUGS #06)
and is guarded by the `Validate UI Blur Render` suite (**5** baselines on rendered pixels) — see §7.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> Frosted-glass backdrops for UI panels: one Kawase blur of the screen per frame, published as the
> global `_UIBlurTexture`, sampled by any UI `Image` whose material is `Custom/MaskedUIBlur`. **The
> pivotal property, and the source of every surprise in this system: the blur is captured *before any
> overlay canvas draws*, so it contains no UI at all.** A panel therefore does not "see through" to what
> is behind it — it *replaces* those pixels with a blurred copy of the world. Everything in §4 follows
> from that one fact.

**Audited:** 2026-08-15, at commit `8c002371` (branch `feat/world-scaling`).
Findings are from static review of `UIBlurRendererFeature.cs`, `UIBlurHistory.cs`, `MaskedUIBlur.shader`,
`UIBlurBlit.shader`, `BenchmarkUIBuilder.cs`, and the five blurred `Image` components in
`Assets/Scenes/World.unity`. The scene values, draw order, and shader arithmetic were verified by
reading the serialized scene and by rendered-pixel measurement through the validation suite, not assumed.

**Relationship to other documents:**

- [`../Guides/SHADER_CONVENTIONS.md`](../Guides/SHADER_CONVENTIONS.md) — the `#pragma target 4.5` floor
  and interpolator-counting rule this shader follows.
- [`RUNTIME_UI_FACTORY.md`](RUNTIME_UI_FACTORY.md) — the shared UI builder that owns the
  material-instance lifecycle described in §5 for code-built screens.
- [`../Bugs/UI_BUGS.md`](../Bugs/UI_BUGS.md) — **#05** (blur strength scales with resolution) is open
  against the producer; **#06** was the consumer's missing UI contract, fixed here.
- [`DATA_DRIVEN_SETTINGS_UI.md`](DATA_DRIVEN_SETTINGS_UI.md) — the settings menu, one of the five
  blurred panels.
- [`TOAST_NOTIFICATION_SYSTEM.md`](TOAST_NOTIFICATION_SYSTEM.md) — a consumer that works around §8's
  stacking limit by policy, dropping its cards to a flat backdrop while a full-screen panel is up.

---

## 1. Components

| File                                                  | Role                                                                             |
|-------------------------------------------------------|----------------------------------------------------------------------------------|
| `Assets/Scripts/Rendering/UIBlurRendererFeature.cs`    | Producer. Kawase-blurs the camera color, publishes `_UIBlurTexture`.             |
| `Assets/Scripts/Rendering/UIBlurHistory.cs`            | Per-camera persistent blur target, so the result survives past the render graph. |
| `Assets/Shaders/UIBlurBlit.shader`                     | The Kawase blur kernel used by the producer's iterations.                        |
| `Assets/Shaders/MaskedUIBlur.shader`                   | Consumer. A UI shader that samples `_UIBlurTexture` by screen UV.                |
| `Assets/Materials/UI/UIBlur.mat`                       | The shared material asset; used by all five scene panels.                        |
| `Assets/Editor/Validation/UIBlur/`                     | The rendered-pixel validation suite and its quad renderer.                       |

---

## 2. Producer: how the blur is made

`UIBlurRendererFeature` enqueues one pass at **`RenderPassEvent.AfterRenderingTransparents`**. It
downsamples the camera color by `downsample` (default 2), runs `iterations` (default 4) Kawase blur
steps ping-ponging between two render-graph temporaries, and writes the final iteration into a
**persistent per-camera target** obtained from `UIBlurHistory` — not a pooled render-graph texture,
which bloom would otherwise reclaim before the UI sampled it. Cameras that expose no history manager
fall back to a shared `_UIBlurTextureFallback` handle.

The result is published with `SetGlobalTexture("_UIBlurTexture", …)` from an unsafe pass, because
setting global state is not permitted inside a raster pass. Pass culling is disabled so the global is
always rebound.

The pass runs only for `CameraType.Game` and `CameraType.SceneView`.

---

## 3. Consumer: the `Custom/MaskedUIBlur` contract

The fragment program computes, in linear space:

```
rgb = (blur.rgb * _MultiplyColor.rgb + _AdditiveColor.rgb) * vertexColor.rgb
a   =  mainTex.a * vertexColor.a          (then clip rect, then alpha clip)
```

blended with `Blend SrcAlpha OneMinusSrcAlpha`. Material tints apply first, then the UI vertex color
scales the whole panel — so fading a panel out fades its additive term too rather than leaving a glow.

It implements the standard UI material surface: `_Stencil*` + `_ColorMask` (so `Mask` works),
`UNITY_UI_CLIP_RECT` + `_ClipRect` (so `RectMask2D` works), and `UNITY_UI_ALPHACLIP`. The clip test is a
locally-declared `Get2DClipping` rather than an include of the Built-in pipeline's `UnityUI.cginc`.

`_ClipRect` is compared against the **untransformed vertex position**, which is the canvas-space
coordinate the UI feeds in — it is deliberately not transformed to world space.

Interpolators: 3, or 4 with clipping enabled. `#pragma target 4.5`, the project floor.

---

## 4. Authoring rules (the non-obvious half)

These are consequences of §2's capture point and of Unity's color handling. Every one of them was
learned by measurement, and getting any of them wrong produces a plausible-looking panel that is wrong.

### 4.1 Alpha is not "transparency" — it is how much sharp screen leaks in

`_UIBlurTexture` holds no UI, and its content is the *blurred* world. So `1 − alpha` is the fraction of
the **un-blurred** screen that shows through. An alpha of 0.72 against a 0.415 tint puts the sharp
image ahead of the blurred one by roughly **2.7 : 1**, and the panel stops reading as frosted glass at
all. Sharpness bleed is a function of alpha **alone** — no tint value can compensate, because the tint
scales the blurred layer and the sharp layer equally in relative terms.

**Rule:** a panel that should look frosted wants alpha at or near 1. Lower it only to reveal UI drawn
beneath it, and accept the frost loss as the price.

### 4.2 An opaque panel hides everything beneath it

Because the blur has no UI in it, an opaque blurred panel is a hole back to the pre-UI frame: it does
not composite over the UI underneath, it replaces it. This is correct frosted-glass behaviour, but it
means **two blurred panels cannot stack meaningfully**, and a full-screen blurred backdrop hides every
UI element below it in the same canvas.

The engine resolves this by *policy*, not by alpha — see §6.

### 4.3 Material colors are gamma-authored; vertex colors are not

Unity converts material `Color` properties from gamma to linear on upload, so the material's authored
`_MultiplyColor` of `0.415` reaches the shader as **`0.1437`**. UI vertex colors (`Image.color`) are
**not** converted on this path. Two colors that read the same in the inspector therefore do different
things depending on which knob they are set through. White and black are fixed points of the
conversion, which is why only tinted values expose the difference.

### 4.4 Alpha comes from `_MainTex`, so a sprite-less Image is opaque

Output alpha is `mainTex.a * vertexColor.a`. An `Image` with no sprite binds Unity's white texture, so
`mainTex.a` is 1 and all opacity control comes from `Image.color.a`. All five scene panels are
sprite-less.

### 4.5 RGB must be white unless a tint is intended

Since vertex color multiplies the result, an `Image.color` with black RGB renders a **solid black
panel**. This is the trap that made the #06 fix a shader-plus-scene change rather than a shader change:
the panels had been authored `(0, 0, 0, 0.72)` back when the shader ignored vertex color entirely.

---

## 5. Material instances and tinting

`_MultiplyColor` / `_AdditiveColor` live on the **material**, so two panels needing different tints need
different material instances (or different material assets). Panels needing only different *opacity*
can share one material and vary `Image.color.a`.

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
(`BenchmarkHUD`, `BenchmarkResultsScreen`), which destroys it in `OnDestroy`. `ConsoleUI` is the
deliberate exception: it owns its instance directly because the material must survive `BuildPanel`
being re-entered by the UI_BUGS #04 self-heal.

---

## 6. Current usage

The five scene panels live in `World.unity` on a single `Canvas` (`sortingOrder` 0), so paint order is
hierarchy order. All five share `UIBlur.mat` (tint `0.415`) and are opaque white.

| Panel                                       | Sibling order under `SafeArea` | Notes                                    |
|---------------------------------------------|--------------------------------|------------------------------------------|
| `Toolbar`                                   | 1                              | the console overlaps its left edge       |
| `CreativeInventory`                         | 2                              | nothing draws above it                   |
| `PauseMenuContainer/PauseMenu`              | 5                              | full-screen                              |
| `PauseMenuContainer/SettingsMenu`           | 5                              | full-screen; an in-scene added component |
| `PauseMenuContainer/HelpMenu`               | 5                              | full-screen                              |

Three more are built in code on their own canvases, each with its own material instance (§5):

| Panel                          | Canvas `sortingOrder` | Tint    | Notes                                                    |
|--------------------------------|-----------------------|---------|----------------------------------------------------------|
| Benchmark HUD                  | **-10**               | `0.7`   | below the scene canvas, so full-screen menus cover it     |
| Benchmark results overlay      | 200                   | `0.15`  | terminal modal, deliberately above everything             |
| `ConsoleUI` panel              | 100                   | `0.415` | matches the scene panels; covers the toolbar's left edge  |
| `ToastCard` backdrops          | 250                   | `0.606`–`0.620` | one material instance per `ToastVariant`; drops to a flat colour while a full-screen menu is up (§8) |

The HUD's negative order is load-bearing rather than cosmetic: at a positive order its opaque panel
punched a hole back to the un-blurred world over the paused screen (UI_BUGS #06).

Because the menus are opaque and full-screen (§4.2), the engine keeps them from ever covering live UI:
`WorldUIManager.HandleEscape` dismisses the creative inventory on the first Escape and only opens the
pause menu on a second press, and the inventory toggle is gated on `!IsPauseMenuOpen`. Those two gates
together make "pause menu open while the inventory is open" unreachable, which is what makes the
opaque backdrop safe.

---

## 7. Validation

`Minecraft Clone/Dev/Validate UI Blur Render` — 5 baselines, registered in the aggregate
(`Validate All`). The suite drives the material directly with a synthetic `_UIBlurTexture`, so it tests
the **consumer contract in isolation**; the producer's own open defect (#05) cannot red it.

| ID | Asserts                                                                            |
|----|-------------------------------------------------------------------------------------|
| B1 | A blur texel survives the round trip unchanged, and the backdrop beside it is untouched |
| B2 | `_MultiplyColor` scales the sampled blur                                            |
| B3 | `_AdditiveColor` offsets it                                                         |
| B4 | The UI vertex color tints the panel and fades it over what is behind                |
| B5 | A clip rect excludes the pixels outside it                                          |

Two properties of the harness are load-bearing:

- **B1 doubles as the did-anything-render check.** A silent no-draw returns the backdrop everywhere,
  which would let B5's "clipped away" assertion pass vacuously. B1 fails loudly instead.
- **The draw uses a `CommandBuffer`, never `SetPass` + `DrawMeshNow`.** The immediate-mode path
  inherits ambient GL state: it worked when the suite ran alone and silently drew nothing when it ran
  after the camera-based suites in `Validate All`. The quad renderer also snapshots and restores the
  shader globals it overwrites.

---

## 8. Known limitations

- **UI_BUGS #05 — blur strength scales with screen resolution.** The Kawase taps are specified in
  texels, so the blur radius as a fraction of the screen is inversely proportional to resolution. Open
  against the producer; its acceptance test needs a matched capture at two resolutions.
- **Panels cannot blur each other.** Fixing this needs a second capture point after the overlay
  canvases draw, which no design currently proposes.

  Consumers work around it by *policy* rather than by compositing, and the workarounds are only as
  good as the overlap they anticipate. `ToastManager` drops its cards to a flat backdrop whenever a
  full-screen blurred panel is up (`WorldUIManager.IsPauseMenuOpen`), which covers the default
  top-right anchor. It does **not** cover a card anchored to a corner a *bounded* blurred panel
  occupies — a bottom-left toast raised while the console panel is open still paints un-dimmed world
  over it, observed 2026-09-02. Widening the policy per anchor would mean every consumer re-deriving
  an overlap test against every other blurred rect; the real fix is the second capture point.
- **No blurred graphic may sit inside a `Mask` without the stencil state Unity supplies.** The shader
  now declares the properties, but nothing exercises this path in the project today.

---

## Document History

* **v1.0** - Initial architecture doc, written after the UI_BUGS #06 fix (`36b74204`) completed the
  consumer's UI contract and added the rendered-pixel suite.
* **v1.1** - `RuntimeUIFactory` took ownership of blur material instances (RUF-1…RUF-3): §5 records the
  shared entry points, §6 adds the three code-built panels and the HUD's negative sorting order.

---

**Last Updated:** 2026-08-15  
**Next Review:** when UI_BUGS #05 is fixed
