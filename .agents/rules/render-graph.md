---
name: render-graph
description: URP Render Graph rules for ScriptableRendererFeatures and their passes. Enforced when editing files under Assets/Scripts/Rendering/.
trigger: glob
glob: Assets/Scripts/Rendering/**/*.cs
paths:
  - "Assets/Scripts/Rendering/**/*.cs"
---

# URP Render Graph Rules

Renderer features here record through the Unity 6 Render Graph API (URP 17). The graph records
every pass into **one command buffer** and executes it later, and it only knows about the
dependencies you declare. Most defects below compile, render something plausible, and are wrong.

## Recording

- **The render function is `static`** and reads only its `PassData` and context — never instance
  fields. Assign **every** `PassData` field on every `RecordRenderGraph` call: the objects are
  pooled, so a field set only sometimes carries a stale handle from an earlier frame.
- **Never mutate a shared `Material` inside a render function.** Materials are references in the
  command buffer, so `material.SetFloat(...)` takes effect immediately while the draws wait in the
  one command buffer that runs later, and every queued draw sees the *last* value (URP's
  `BloomPostProcessPass` keeps a material per mip for this reason). Use one material per variant,
  or per-draw values the command buffer captures.
- **Descriptors:** read the graph's own with `renderGraph.GetTextureDesc(handle)` (a `TextureDesc`,
  not a `RenderTextureDescriptor`) and change only the fields that must differ.
  `cameraData.cameraTargetDescriptor` is acceptable when the result also sizes a persistent
  `RTHandle`, which needs a `RenderTextureDescriptor`.

## Declaring access

- **Read with `UseTexture`, write with `SetRenderAttachment`**, and declare `AccessFlags.ReadWrite`
  on an attachment the shader **blends** against. A write-only declaration lets the graph treat the
  frame already there as expendable.
- **Dependencies the graph cannot see** — an imported texture shared between passes, a draw a later
  pass samples through the attachment — need `builder.AllowPassCulling(false)` **and** a fixed
  record order. Reordering such passes breaks them silently.
- **`ConfigureInput(ScriptableRenderPassInput.Depth)`** on any pass that samples scene depth. This
  renderer copies depth **AfterTransparents**, so the depth texture contains the fluid surface.
  Do **not** declare `Color` just to sample `activeColorTexture` — that requests an extra opaque
  copy; set `requiresIntermediateTexture` instead.

## Global state

- A raster pass may write globals only after `builder.AllowGlobalStateModification(true)`.
- Publish a graph texture as a global with `builder.SetGlobalTextureAfterPass(handle, id)` where the
  consumers are graph passes. An unsafe pass calling `cmd.SetGlobalTexture` is the fallback for a
  persistent imported target; say why in a comment.
- Add no global without a named consumer. A global keeps its resource alive and couples passes
  invisibly.

## Feature lifecycle

- `Create()` runs again on every domain reload and inspector edit with no matching `Dispose`: keep it
  **idempotent** and release old resources before building new ones.
- Pair `CoreUtils.CreateEngineMaterial` with `CoreUtils.Destroy`, and `RTHandle` allocation with
  `Release()`, in a `ReleaseResources` method called from both `Create` and `Dispose`.
- Render scale and MSAA are changed at runtime by the graphics settings. Verify a depth- or
  resolution-sensitive effect at a non-default render scale.

## Reference

- Architecture: `@Documentation/Architecture/UI_BLUR_BACKDROP_SYSTEM.md`,
  `@Documentation/Architecture/UNDERWATER_AND_SUBMERSION_RENDERING.md`
- Review gates: `.agents/skills/review-changes/references/gates-rendering.md`
- Adapted from Unity's `validate-urp-render-graph-renderer-feature` skill
  (`Unity-Technologies/unity-agent-plugin` @ `566368b`, Unity Companion License).
