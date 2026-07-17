# Unity MCP `Unity_RunCommand` Patch Guide (embedded `com.unity.ai.assistant`)

The `com.unity.ai.assistant` package (pinned to **2.6.0-pre.1**) is **embedded** in this project at
`Packages/com.unity.ai.assistant/` with a small set of local patches, primarily because its
`Unity_RunCommand` MCP tool is broken on Unity 6000.5+ Mono editors. The embedded folder is **gitignored** (~6,000
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

## The patches

The script applies each with an anchored replace; every patched site carries a `// PATCHED` comment
pointing back to this guide.

1. **Mono `Assembly.Load` fallback** (`Editor/Assistant/Utils/AssemblyUtils.cs`, `LoadFromBytes`)
   — the fix for the root cause above:

   ```csharp
   #if UNITY_6000_5_OR_NEWER
               if (Type.GetType("Mono.Runtime") != null)
                   return Assembly.Load(assemblyBytes);          // Mono: name-safe byte[] load
               return CurrentAssemblies.LoadFromBytes(assemblyBytes); // CoreCLR: needs collectible ALCs
   #else
               return Assembly.Load(assemblyBytes);
   #endif
   ```

2. **Lenient string-encoded array parameters**
   (`Modules/Unity.AI.MCP.Editor/ToolRegistry/IToolHandler.cs`, `DeserializeParameter`) — some MCP
   clients emit array arguments as JSON-encoded strings (e.g. `Types: "[\"error\"]"`), which the
   strict deserializer rejected with `Error converting value ... to type 'ConsoleLogType[]'`
   (the classic flaky `Unity_ReadConsole` failure). When the target property is a collection and
   the string starts with `[`, it is re-parsed as JSON; non-collection string parameters (e.g. a
   `FilterText` of `"[EmitProbe]"`) are never touched.

3. **Clear error for blocked-namespace scripts** (`Editor/Assistant/RunCommand/RunCommandUtils.cs`)
   — scripts using an unauthorized namespace (`System.Net`, `System.Diagnostics`,
   `System.Runtime.InteropServices`, `System.Reflection` — see
   `RunCommandCodeAnalyzer.k_UnauthorizedNamespaces`) are built with a null `Compilation`, which
   made `Unity_RunCommand` fail with an opaque `Object reference not set to an instance of an
   object`. `Execute`/`ExecuteReadonly` now throw *"Script was blocked before execution: it uses an
   unauthorized namespace ..."* instead.

4. **Null-safe `AgentRunCommand.Unsafe`** (`Editor/Assistant/RunCommand/AgentRunCommand.cs`) — the
   `Unsafe` property dereferenced the metadata that blocked scripts never get (`m_Metadata`),
   NRE-ing before patch 3's guard could fire; it now null-guards exactly like the adjacent
   `HasWriteOperations` property already did.

5. **Silence `ApiNoLongerSupported` console spam**
   (`Modules/Unity.AI.Toolkit.Accounts/Services/Core/AccountApi.cs`) — the account
   settings/points-balance refresh hits the retired `generators.ai.unity.com` endpoint, which
   permanently answers `ApiNoLongerSupported` for this pinned pre-release SDK. The package already
   treats that error as definitive (`PackagesSupported = false`, no retry), but still logged
   *"Error after 1 attempt(s): ApiNoLongerSupported ..."* — and its `s_LastLoggedError` dedup is a
   static that resets on every domain reload, so the message reappeared after each recompile.
   Definitive `ApiNoLongerSupported` results now return without console logging; all other account
   errors still log as before.

6. **Backport A — `get_components` deep-graph / non-TRS Matrix4x4 crash guard**
   (`Modules/Unity.AI.MCP.Runtime/Serialization/UnityTypeConverters.cs`,
   `Modules/Unity.AI.MCP.Editor/Helpers/GameObjectSerializer.cs`,
   `Modules/Unity.AI.MCP.Editor/Tools/ManageGameObject.cs`) — backported from upstream
   **2.13.0-pre.1** (UUM-144888). `Unity_ManageGameObject` `get_components` serializes public
   members via reflection; a member exposing a deep non-`UnityEngine.Object` reference graph
   overflowed the C stack inside `mono_gc_alloc_obj` and **crashed the Editor** (Newtonsoft's
   `MaxDepth` guards only the read path). A `DepthLimitedJTokenWriter` now caps write depth at 64 —
   the existing `JsonSerializationException` catch turns the overflow into a skipped field — and a
   `Matrix4x4Converter` (registered on both the output and input serializers) emits the 16 elements
   directly instead of reflecting a non-TRS matrix. Minimal port: `CreateTokenFromValue` keeps its
   signature (no `SerializationResult`/sentinel machinery). Verified in-editor: a probe component
   with a 300-deep property returned without crashing (`"Maximum depth 64 exceeded"` warning, field
   skipped) and a `Matrix4x4` property serialized to `m00..m33`.

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
- `Unity_RunCommand` scripts can never use `System.Net`, `System.Diagnostics`,
  `System.Runtime.InteropServices`, or `System.Reflection` (hard blocklist in
  `RunCommandCodeAnalyzer`). Since patches 3/4 the failure is at least a clear "Script was
  blocked" error. For reflection-heavy editor work, use a temp `[MenuItem]` script driven via
  `Unity_ManageMenuItem` instead.

## When to remove all of this

When the pin is lifted and the package can be upgraded: delete `Packages/com.unity.ai.assistant/`,
`Tools/Apply-AiAssistantMcpPatch.ps1`, the `.gitignore` entry, and this guide, then verify
`Unity_RunCommand` against the new registry version (the upstream package may fix the load path or
rename the dynamic assembly).
