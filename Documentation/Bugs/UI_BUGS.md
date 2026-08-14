# Known User Interface (UI) related bugs

This document outlines **open** bugs related to the UI, Menus, Inventory, and HUD. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** May 2026

---

## 01. Missing Inventory Update Handling

**Severity:** Implementation  
**Files:** `UIItemSlot.cs`

Adding items natively doesn't auto-update the UI.  
**Impact:** UI state can desync from the actual internal inventory state.

---

## 02. Settings UI: Sub-Page / Pop-Up Navigation Support

**Severity:** Feature  
**Files:** `SettingsUIGenerator.cs`, `SettingActionAttribute.cs`

The `[SettingAction]` attribute system supports simple action buttons (e.g., "Clear All Benchmarks"). A future extension should support **sub-page navigation**: a button that opens a pop-up or child panel with additional settings, and triggers a callback on close to refresh the parent settings page.

**Proposed approach:** Add an optional `NavigatesTo` property to `SettingActionAttribute` (or a dedicated `[SettingSubPage]` attribute). The generator would handle panel open/close lifecycle and auto-call `RebindValues()` on the parent when the sub-page dismisses.

---

## 03. Pre-Benchmark Setup Screen

**Severity:** Feature  
**Files:** `MainMenuController.cs`, `BenchmarkController.cs`, `Settings.cs`

Replace the one-click "Run Benchmark" button with a dedicated setup screen that exposes all benchmark configuration before starting. Should include: region size, generation/loading speed editors, seed field, world type selector, and an estimated duration label.

Additionally, consider integrating currently runtime-only benchmark scripts (e.g., `MeshGenerationBenchmark`) into this UI as selectable benchmark modes, so all benchmarking is accessible from a single entry point rather than requiring Inspector configuration.

The Benchmark tab in the Settings UI could serve as this setup screen with a "Start Benchmark" `[SettingAction]` button, avoiding the need for a separate panel.

---

## 04. Console Input Field Destroyed During Heavy Chunk Churn (far-lands teleport / render-distance change)

**Severity:** Bug (intermittent) — **mitigated** (self-heal + `LateUpdate` guard shipped); root cause not yet pinned  
**Status:** **Mitigated; tripwire live for root-cause capture** — the confirmed failure mode is understood and
the console now self-heals, so the bug is no longer user-visible. The exact destroyer is still unidentified
(not statically findable — no project code destroys the field); the `[UIBUG04]` instrumentation + an
input-field death sentinel stay in to capture the destroyer's frame on the next natural occurrence.  
**Files:** `ConsoleUI.cs` (fix + sentinel); investigation touched `WorldUIManager.cs`, `PauseMenuController.cs`,
`SettingsUIGenerator.cs`, `World.cs` (teleport / `ShiftOrigin`), `CommandEngine.cs` — all exonerated as direct destroyers.

**Description:**

Intermittently, after a **far-lands `/teleport`** (user-confirmed trigger, 2026-07-21) or a **render-distance
change** (original report), the console opens with its translucent backdrop and scrollable history rendered but
**no input field**, leaving it unusable; before the fix, only a world save & reload restored it.

**Confirmed failure mode (from a natural `[UIBUG04]` repro, 2026-07-21):**

The `[UIBUG04] Open()` / `FocusInputNextFrame ran.` snapshots print `inputGoActive=False` **and**
`inputText='<null>'`, while `panelActiveSelf=True` / `panelInHierarchy=True`. Both fields come from the same
`_inputField != null` ternary in `ConsoleUI.DiagUIBug04State()`; `inputText='<null>'` is only the *false* branch,
so **`_inputField` is Unity-null** — the input field's GameObject was **destroyed** out from under the live view,
while the panel, canvas, and `ConsoleUI` component all survived. This is neither the original candidate class 1
(open/close state desync — impossible: `IsConsoleOpen` is derived live from `_panel.activeSelf`, no stored
manager belief) nor the focus-loss variant of class 2: the object is *gone*, not deactivated or unfocused. In
the captured logs no `ConsoleUI OnDisable` or panel-`activeSelf` watchdog warning fired in the gap, so the panel
was never deactivated — only the "Input" subtree was silently destroyed and never rebuilt (subsequent opens
re-showed an emptied panel).

**Trigger — heavy chunk churn (both reports unified):** the destruction correlates with a *full chunk-set
re-stream*, which both reported triggers force — a render-distance change resets `_playerLastChunkCoord` to
force one, and a far-lands teleport forces one via `ShiftOrigin` + a `PlayerChunkCoord` jump plus a multi-second
arrival hold. **Leading unproven theory (user, 2026-07-21):** closing the console with `Esc` *while the teleport
arrival hold is still running* — so `TeleportHoldEnded` later posts a line to a now-closed console. `LateUpdate`
had no `IsOpen` guard, so that posted line drove `Canvas.ForceUpdateCanvases()` + a scroll write on the
*inactive* panel subtree, mid-churn, around an origin rebase — a plausible provocation for an engine/TMP-internal
teardown of the field. Not reliably reproducible on demand (deliberate teleport / render-distance / close-mid-hold
attempts across 2026-07-19..21 all failed to reproduce), consistent with a timing race.

**Root cause — NOT statically findable:** every `Destroy`/`DestroyImmediate` in `Assets/Scripts` was swept;
the only objects destroyed during a teleport are chunk / section / cloud / border geometry — never anything under
`WorldUIManager`. `ShiftOrigin` only repositions. The confirmation flow (`CommandEngine`) is pure C#. So the actual
destroyer is engine/TMP-internal (a canvas/InputField teardown) *provoked* by the churn, not a call in our code.

**Mitigation shipped (2026-07-21):**

- **Self-heal** (`ConsoleUI.RebuildMissingChildren`, permanent): reconstructs whatever level died, with a
  name-based remnant cleanup so a partial survivor can't duplicate —
    - the whole **`ConsolePanel`** (rebuilt via the extracted `BuildPanel()` under the surviving canvas — only the
      `Console` GameObject/canvas being destroyed is unrecoverable, but then this component can't run anyway);
    - the **history view** (`_scrollRect`+`_historyText`) and **input field** (`_inputField`, full rebuild) as
      individual build-units; the **ghost overlay** alone (`_ghostText`) when only it died, preserving the live
      field and its typed text (this granular path also means the self-heal never deliberately destroys a live
      field, so it can't trip the death sentinel — review finding).

  Called from **`Open()`** (heals before showing; `Open()` returns `bool`, and `WorldUIManager` skips the
  Gameplay-map swap if it returns false, so a failed heal can't soft-lock input) **and from `Update()`** while
  open (heals a child destroyed mid-session and refocuses; also covers `LateUpdate`'s history-deref). A permanent
  `LogWarning` fires on each rebuild so recurrences stay visible even after the temporary instrumentation is
  removed. In-game confirmed 2026-07-21: deleting the input field, the history view, or the whole `ConsolePanel`
  at runtime all recover on the next open (panel deletion previously left the console permanently invisible).
- **`LateUpdate` guard** (`if (!IsOpen) return;`, permanent): a line posted to a closed console no longer drives a
  canvas rebuild on the inactive panel subtree (defuses the leading theory). No line is lost — `Open()` re-marks
  `_historyDirty`/`_autoscrollPending`, so a reopen rebuilds and autoscrolls.
- **`WorldUIManager` stale-`InUI` recovery** (`Update`, permanent): the panel self-heal only fires from `Open()`,
  but if the `ConsolePanel` is destroyed *while the console is open*, `IsConsoleOpen` (derived from
  `_panel.activeSelf`) flips to false while `InUI` stays latched true and the Gameplay map stays disabled — `T`
  lives on that disabled map, so nothing can re-trigger `Open()` (a soft-lock; in-game confirmed 2026-07-21 that
  the panel self-heal alone did NOT recover this case). `Update` now detects the desync (`InUI` latched true while
  none of console/inventory/pause is actually open), re-enables all maps, and re-runs `UpdateUIState()`; the next
  `T` then reopens and rebuilds the panel. UI-agnostic — it never misfires in normal operation because the state
  setters call `UpdateUIState()` synchronously, so `InUI` is only ever stale after an external destruction.

**Diagnostic tripwire (lightweight, permanent while root cause is unresolved):**

The heavy investigative `[UIBUG04]` scaffolding from the 2026-07-19/21 passes — the raw-T probe +
`InputManager` map-state accessors, the `WorldUIManager` failure-moment capture / `IsConsoleOpen` setter logs /
`DiagUIBug04Snapshot`, the `ConsoleUI` Open/Close logs / panel-`activeSelf` watchdog / `OnEnable`/`OnDisable` /
`FocusInputNextFrame` log / `DiagUIBug04State`, and the `PauseMenuController` settings brackets — was **removed
2026-07-21** now that the failure mode is understood and the console self-heals. What stays, as a permanent
recurrence tripwire (no removable tag — plain warnings referencing "UI_BUGS #04"):

- `ConsoleUI.InputFieldDeathSentinel` — a component on the "Input" GameObject whose `OnDestroy` logs the **exact
  frame** the field dies mid-play (`gameObject.scene.isLoaded`-gated to suppress teardown noise).
- The `RebuildMissingChildren` self-heal `LogWarning`s (panel / history / input / ghost rebuilt) and the
  `WorldUIManager` stale-`InUI` recovery warning — each fires on a recurrence.

**On the next natural occurrence:** the sentinel's "Console input field destroyed externally mid-play" line pins
the death frame — cross-reference it against a `ShiftOrigin` / arrival-hold / hold-end post in the same or
adjacent frames to finally name the destroyer. (If deeper capture is again needed, re-add scaffolding then.)

---

## 05. UI Blur Strength Scales With Screen Resolution

**Severity:** Bug (cosmetic) — **open, unfixed**  
**Status:** **Reported, not started.** Reproduced 2026-08-14; deferred by the user as non-blocking. Wants a
full implementation plan before any code change, because both candidate fixes alter the blur's *look* and
need a visual sign-off.  
**Files:** `Assets/Shaders/UIBlurBlit.shader` (kernel), `Assets/Scripts/Rendering/UIBlurRendererFeature.cs`
(offset progression + blur target size)

**Description:**

The UI blur backdrop (pause menu, console, any `Custom/MaskedUIBlur` panel) gets **blurrier at low
resolutions and sharper at high ones**. The kernel is specified in *texels* rather than in screen-normalized
units, so its radius as a fraction of the screen is inversely proportional to render resolution.

**Repro:** open the pause menu (large blur canvas), then change the resolution — in the editor, resizing the
Game view is enough. The backdrop's blur strength visibly shifts as the resolution changes.

**Mechanism (read from code, not inferred):**

`UIBlurBlit.shader:31` reads `_BlitTexture_TexelSize.xy` (`1/width`, `1/height` of the source) and lines
37-40 place the four Kawase taps at `(±offset ± 0.5) * texelSize`. The UV-space radius is therefore
`offset / width`. `UIBlurRendererFeature.cs` derives the blur target from `cameraTargetDescriptor` divided by
`downsample`, so the `downsample` setting shifts the constant but not the proportionality.

With `downsample: 2` and 4 iterations (max offset ~2 texels):

| Resolution | Blur target width | Max tap radius (screen width) |
|---|---|---|
| 2560x1440 | 1280 | ~0.16% |
| 1280x720  | 640  | ~0.31% |

**Not a regression of `fa9ac4bc`** (the pooled-render-graph-texture fix). That commit changed *which* texture
the final blur iteration writes into; the per-camera history target takes the identical descriptor the pooled
temps did, so texel size is untouched. The tap offsets predate this session's work. The defect went unnoticed
because resolution never changes mid-session in normal play.

**Candidate fixes (not yet decided — this is the plan's job):**

1. **Scale the offsets by resolution** — multiply the `0.5f + step` progression by
   `blurTargetWidth / referenceWidth`. Smallest diff. Risk: at high resolutions the taps grow large in texels,
   which is the regime the existing comment in `RecordRenderGraph` warns produces blocky artifacts, so it may
   need an iteration-count bump to hold quality at 4K.
2. **Fix the blur target's pixel dimensions** — size it to a constant (~960px wide) instead of dividing by
   `downsample`. Taps stay small in texels, the UV radius is constant for free, and blur cost stops scaling
   with resolution. Costs: it redefines what the `downsample` inspector setting means, and softness at the
   current resolution shifts, so it needs a before/after capture.

Whichever is chosen, the acceptance test is a **matched capture at two resolutions** showing equal blur
softness as a fraction of the screen — a single-resolution capture cannot observe this defect at all.

---

## 06. UI Blur Panels Are Opaque and Cannot Stack

**Severity:** Bug — **open, unfixed**  
**Status:** **Reported 2026-08-14, fix planned and approved.** Found while scoping the command console's
opt-in to the blur backdrop. Fix decided: give `MaskedUIBlur` standard UI vertex-color semantics (full RGBA
multiply) plus UI clipping support, re-author the five scene panel colors in the same commit, and pin the
behaviour with a new rendered-pixel validation suite.  
**Files:** `Assets/Shaders/MaskedUIBlur.shader` (root cause), `Assets/Scenes/World.unity` (five authored
colors), `Assets/Scripts/Benchmarks/BenchmarkUIBuilder.cs` (same defect, built in code)

**Description:**

Every `Custom/MaskedUIBlur` panel renders **fully opaque**, regardless of the alpha it was authored with.
Two visible consequences:

1. **A blurred panel hides whatever UI is beneath it.** With the pause menu open, the creative inventory is
   invisible — not closed, *painted over* by the full-screen pause backdrop.
2. **Blurred panels cannot stack.** An opaque blur panel is a hole back to the pre-UI frame: it replaces
   those pixels with world blur instead of compositing over what is already there. The benchmark HUD
   therefore appears to float *above* the pause menu, showing un-dimmed world where the surrounding screen
   is dimmed.

**Repro:** open the creative inventory, then open the pause menu — the inventory vanishes. Or run a
benchmark and pause mid-run — the HUD strip stays visibly lighter than the dimmed screen around it.

**Mechanism (read from code, not inferred):**

`MaskedUIBlur.shader:37-41` declares `appdata_t` with only `POSITION` and `TEXCOORD0` — there is **no
`COLOR` semantic**, so the UI vertex color is structurally unreachable by the shader. Output alpha comes
solely from `_MainTex.a` (`:85`), and all five scene panels have `m_Sprite: {fileID: 0}`, so `_MainTex` is
the default white texture and alpha is **always 1**.

The five panels are nonetheless authored `m_Color = (0, 0, 0, 0.72)` — the classic "dim the screen 72 %"
overlay, silently promoted to 100 % by the shader. The RGB is black, a leftover from before the Built-in-RP
GrabPass version was replaced; it is equally ignored today.

The stacking half has a second contributing cause: `UIBlurRendererFeature.cs:78` captures `_UIBlurTexture`
at `RenderPassEvent.AfterRenderingTransparents`, **before any ScreenSpaceOverlay canvas draws**. Every blur
panel in the frame samples the same UI-free snapshot, so an opaque one cannot show anything drawn between it
and the world.

The inventory is genuinely still active while hidden: `WorldUIManager.UpdateUIState()` (`:245-255`) touches
only `InUI` and the cursor, and never deactivates `creativeInventoryWindow`.

**Evidence — draw order and coverage (`World.unity`, single Canvas at `sortingOrder` 0, so paint order is
hierarchy order):**

| Panel | Sibling index under `SafeArea` | Rect |
|---|---|---|
| `Toolbar` | 1 | 218x26 at scale 3, bottom-centre |
| `CreativeInventory` | 2 | 216x168 at scale 3, centred |
| `PauseMenuContainer` -> `PauseMenu` | **5** | anchors (0,0)-(1,1), sizeDelta 0 — **full screen** |

The fifth blurred `Image` is an in-scene added component on the `SettingsMenu` prefab instance
(`m_PrefabInstance: {fileID: 0}` on the component), so it is a scene-local edit, not a prefab change.

**Impact beyond the two reported symptoms:** the shader also declares no `_Stencil*` properties and no
`UNITY_UI_CLIP_RECT` / `_ClipRect`, so a blurred graphic inside a `Mask` or `RectMask2D` does not clip at
all. This blocks the command console from adopting the backdrop: its panel (`ConsoleUI.cs:26-28`, x 12-692)
overlaps the toolbar (x ~633-1287) by roughly 59 reference px at default UI scale, and would punch the same
hole through it. `UIScaleController` rescales only the scene canvas, so a Large UI scale widens the overlap.

The benchmark HUD reaches the same end state by a different route — `BenchmarkUIBuilder.cs:53` sets
`color = Color.white` at **alpha 1**, while its own non-blur fallback constants (`s_hudBackgroundColor`
a=0.7, `s_resultsOverlayColor` a=0.85) record the intended translucency.

**Not the cause:** the two separate `Material` instances the benchmark creates. A single shared material
behaves identically — the defect is in the shader's vertex-color contract, not in instancing.

**The system these panels belong to is documented in**
[`../Architecture/UI_BLUR_BACKDROP_SYSTEM.md`](../Architecture/UI_BLUR_BACKDROP_SYSTEM.md) — its §4
carries the authoring rules (alpha is sharp-bleed, not transparency; material colors are gamma-converted
but vertex colors are not; RGB must be white) that this entry's fix established.

**Distinct from `#05`,** which concerns the blur *producer* (`UIBlurBlit.shader` + the renderer feature) and
its resolution dependence. This entry is about the *consumer* shader's compositing. They are being fixed
separately, on the user's decision, because `#05`'s acceptance test needs a matched two-resolution capture.

---
