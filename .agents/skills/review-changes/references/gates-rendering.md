# Gates — URP Render Graph

Loaded when the diff touches `Assets/Scripts/Rendering/`, or any code that records Render Graph
passes (see the content trigger in `SKILL.md`). Four gates for the one way this code fails that
the compiler, the analyzers and a glance at the Game view all miss: the graph executes what you
**declared**, not what you meant, and it runs every pass later from one command buffer.

**Source of truth: `.agents/rules/render-graph.md`.** The gates below summarize it and add the
review mechanics. Measured URP facts this project depends on (depth copied AfterTransparents,
runtime render scale and MSAA) are there too. Read it when any gate fires.

Each gate carries **what fails**, **how to check**, **severity**, and whether it is
**delta-based** or absolute. Severities are ceilings.

---

## Gate 20 — Resource access declared wrong

**What fails.** The pass reads or writes something the graph was not told about, or was told
wrongly, so the graph culls, reorders or discards around it:

- an attachment the shader **blends** against, declared `AccessFlags.Write` instead of `ReadWrite`
  — the graph may drop the frame already in it
- a sampled texture with no `UseTexture`, or a read/write role swapped
- a dependency the graph cannot see (an imported texture shared across passes, a later pass
  sampling this pass's attachment) without `AllowPassCulling(false)` and a fixed record order
- scene depth sampled without `ConfigureInput(ScriptableRenderPassInput.Depth)`, or `Color`
  declared just to sample `activeColorTexture` (that forces an extra opaque copy)
- a fresh descriptor built by hand where `renderGraph.GetTextureDesc(handle)` would carry format
  and MSAA over. Advisory only (Low) unless a format or MSAA mismatch is visible

**How to check.** For each added or changed `AddRasterRenderPass` / `AddUnsafePass` /
`AddComputePass`, list what the shader reads and writes. Then match each item to a builder call.
The blend state lives in the `.shader` (`Blend` line), not in the C#: open it. A declaration
the diff **removes** is the easiest to miss, so scan the `-` side too:

```bash
git diff --no-color $RANGE | grep -nE '^-.*(ConfigureInput|AllowPassCulling|AccessFlags\.ReadWrite|UseTexture|requiresIntermediateTexture)'
```

**Delta-based.** **Severity.** High: these render correctly until a graph compile decides
differently (another feature enabled, a URP setting changed, a camera type added).

---

## Gate 21 — Render function reads stale or shared state

**What fails.** The render function executes after recording, from one command buffer, with
pooled `PassData`:

- a render function that is not `static`, or that captures instance state
- a `PassData` field that is not assigned on **every** `RecordRenderGraph` call, so it can carry a
  handle from an earlier frame
- **a shared `Material` mutated inside the render function** (`material.SetFloat/SetTexture/...`).
  The command buffer holds a reference to the material, so every queued draw renders with the value
  set **last**. URP's own `BloomPostProcessPass` keeps one material per mip for this reason

**How to check.**

```bash
git diff --no-color $RANGE -- '*.cs' | grep -nE '^\+.*(SetRenderFunc|\.Set(Float|Int|Vector|Color|Texture|Matrix)\(|PassData)'
```

For each render function, confirm it is `static` and that every `PassData` field it reads is
assigned in the same record call. For each `Material.Set*` inside a render function, ask whether
the same material serves more than one draw in the frame.

**Absolute** for the shared-material case, because it is wrong the first time two draws share the
material. **Severity.** High. The result looks plausible (it blurs, it tints), so nobody catches it
from a screenshot.

---

## Gate 22 — Global state published around the graph

**What fails.**

- a `cmd.SetGlobal*` in a raster pass without `builder.AllowGlobalStateModification(true)`
- a graph texture published with `cmd.SetGlobalTexture` from an unsafe pass when
  `builder.SetGlobalTextureAfterPass` would express it (consumers are graph passes). The unsafe-pass
  form is acceptable for a persistent imported target, **with a comment saying so**
- a new global with no named consumer: it extends the resource's lifetime and couples passes
  invisibly

**How to check.** `grep -nE 'SetGlobal(Texture|Float|Vector|Buffer)|SetGlobalTextureAfterPass|AllowGlobalStateModification'`
on the changed files, then name the consumer (shader property) for each global the diff adds.

**Delta-based.** **Severity.** Medium, ceiling High when a missing `AllowGlobalStateModification`
makes the write silently not happen.

---

## Gate 23 — Feature lifecycle leak

**What fails.** `ScriptableRendererFeature.Create()` runs again on every domain reload and inspector
edit **with no matching `Dispose`**:

- `Create()` that allocates without first releasing, or that is not idempotent
- `CoreUtils.CreateEngineMaterial` without a `CoreUtils.Destroy`, or an `RTHandle` without
  `Release()`, on both the `Create` and `Dispose` paths
- per-frame work in `AddRenderPasses` that belongs in `Create` (allocations, material creation)

**How to check.** Read `Create`, `Dispose` and the feature's release method together. Every
resource `Create` builds must have a release that both paths reach.

**Absolute** in changed feature code. **Severity.** Medium: an editor-session leak that grows with
every inspector tweak. High if it also allocates per frame.

---

## Documented exceptions (not findings)

Calibrated 2026-09-27 against the four files under `Assets/Scripts/Rendering/` that record passes.
Each of these would trip a gate read naively, and each is deliberate. Do not report them unless a
diff changes the reason.

| File | Pattern | Why it is correct |
|---|---|---|
| `UnderwaterOverlayRendererFeature.cs` | Blit with **no source texture** bound | The effect is a `SrcAlpha` blend over the attachment (declared `ReadWrite`); depth comes from the URP depth global, declared via `ConfigureInput(Depth)` |
| `UIBandCompositeRendererFeature.cs` | Samples `activeColorTexture` with no `ConfigureInput(Color)` | Sets `requiresIntermediateTexture`, which is the right request; `Color` would add an opaque copy |
| `UIBandCompositeRendererFeature.cs` | `cmd.SetGlobalFloat` inside a raster pass | Allowed by `AllowGlobalStateModification(true)` in the same builder |
| `UIBlurChain.cs` | `cameraTargetDescriptor` instead of a graph descriptor | The descriptor also sizes the persistent `UIBlurHistory` `RTHandle`, which takes a `RenderTextureDescriptor` |
| `UIBlurChain.cs` | `cmd.SetGlobalTexture` from an unsafe pass | Publishes a persistent **imported** target to UI shaders drawn through renderer lists; commented at `PublishGlobal` |
| `CloudPrepassRendererFeature.cs` | Color **and** depth attachments `ReadWrite` | Clouds alpha-blend and `ZWrite On` against the frame already drawn |

Not gates: replacing a hand-written raster pass with `AddBlitPass` / `AddCopyPass` is a
simplification to suggest in passing, never a finding.
