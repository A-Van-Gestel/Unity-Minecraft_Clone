# Unity MCP `Unity_RunCommand` Patch Guide (embedded `com.unity.ai.assistant`)

The `com.unity.ai.assistant` package (pinned to **2.6.0-pre.1**) is **embedded** in this project at
`Packages/com.unity.ai.assistant/` with a one-block local patch, because its `Unity_RunCommand`
MCP tool is broken on Unity 6000.5+ Mono editors. The embedded folder is **gitignored** (~6,000
files / 48 MB of external package source) — after a fresh clone it does not exist and must be
recreated with the script below.

## TL;DR — (re)apply the patch

```powershell
# From the repo root. Idempotent: safe to run any time.
Tools\Apply-AiAssistantMcpPatch.ps1
```

If it reports that no package source is available: make sure `Packages/com.unity.ai.assistant`
is absent, open the project in Unity once (the registry package resolves into
`Library/PackageCache`), then re-run the script. Afterwards focus Unity (or Assets > Refresh) and
verify with a trivial `Unity_RunCommand` call.

## Symptom

Every `Unity_RunCommand` MCP call fails with:

```
UNEXPECTED_ERROR: Execution failed:
No logs available
```

Compilation/validation succeeds; only execution dies. Other MCP tools keep working.

## Root cause (diagnosed 2026-07-06)

`Editor/Assistant/Utils/AssemblyUtils.cs` → `LoadFromBytes` switches at
`#if UNITY_6000_5_OR_NEWER` from `Assembly.Load(byte[])` to Unity 6.5's new
`UnityEngine.Assemblies.CurrentAssemblies.LoadFromBytes`. On a **Mono** editor that API resolves
by assembly *name* and returns the already-loaded assembly on a collision — and the package names
its dynamically compiled run-command assembly `Unity.AI.Assistant.Bridge.Editor`
(`RunCommandUtils.k_DynamicAssemblyName`), identical to the package's own loaded assembly. The
freshly emitted image (containing the user's `CommandScript`) is therefore never loaded,
`GetType("...CommandScript")` returns null, and the tool reports "failed to start" with the emit
diagnostics swallowed.

The bug appeared with the Unity 6.5 upgrade (`63473df`) and hardened across editor patches
(worked on 6000.5.0f1 as late as 2026-06-21, deterministic failure on 6000.5.2f1). It is a
package↔editor incompatibility — **not** related to this project's builtin-package trimming, and
**not** relay drift (`~/.unity/relay` matches the bundled 1.0.12-build.91).

## The patch

One guarded fallback in `LoadFromBytes` (the script applies it with an anchored replace):

```csharp
#if UNITY_6000_5_OR_NEWER
            if (Type.GetType("Mono.Runtime") != null)
                return Assembly.Load(assemblyBytes);          // Mono: name-safe byte[] load
            return CurrentAssemblies.LoadFromBytes(assemblyBytes); // CoreCLR: needs collectible ALCs
#else
            return Assembly.Load(assemblyBytes);
#endif
```

## Embed details / constraints

- The package must stay pinned to **2.6.0-pre.1** (external constraint). The embedded copy is the
  same version; `Packages/manifest.json` is unchanged and `packages-lock.json` flips the source to
  `embedded` automatically.
- `RelayApp~/` (520 MB of relay binaries) is **not** copied — only its `relay.json`, so the
  package's `ServerInstaller` version check reports "up to date" against the relay already
  installed at `~/.unity/relay/`. If `~/.unity/relay` is ever wiped, restore the binaries from a
  registry copy of the package (delete the embed, let Unity re-resolve, reinstall, re-run the
  script).
- MCP `Unity_RunCommand` calls that trip the package's unsafe-code classifier (e.g.
  `AssetDatabase.DeleteAsset`) fail with *"User interactions are not supported for MCP tool
  calls"* — by design (the approval UI only exists in the Assistant window). Use a menu item or
  the shell for destructive operations.

## When to remove all of this

When the pin is lifted and the package can be upgraded: delete `Packages/com.unity.ai.assistant/`,
`Tools/Apply-AiAssistantMcpPatch.ps1`, the `.gitignore` entry, and this guide, then verify
`Unity_RunCommand` against the new registry version (the upstream package may fix the load path or
rename the dynamic assembly).
