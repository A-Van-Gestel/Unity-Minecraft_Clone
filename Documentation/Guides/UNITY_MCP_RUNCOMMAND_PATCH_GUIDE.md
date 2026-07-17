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

7. **Backport B — keep MCP responsive while the Editor is unfocused**
   (`Modules/Unity.AI.MCP.Editor/Bridge.cs`) — backported from upstream **2.13.0-pre.2** (MCP half
   only; the chat/relay-account connection-recovery half is deliberately omitted). The command
   queue drained off `EditorApplication.update`, which Unity **throttles when the Editor is
   unfocused**, so tool calls stalled. A focus-independent `MainThreadCommandPump` (a background
   `System.Threading.Timer`, inlined as a nested class in `Bridge.cs`) now ticks at 100 ms; when
   work is pending it marshals the drain back to the main thread via the captured
   `SynchronizationContext` and calls `EditorApplication.QueuePlayerLoopUpdate()` to wake the
   throttled loop. `ProcessCommands` is reentrancy-guarded, so dual-driving it (pump + the retained
   `update` hook) is safe; `isRunning` is now `volatile`. The relay binary is unchanged — this only
   fixes the Editor-side drain throttle.

8. **Backport C — keep the chat-Assistant runtime out of player builds**
   (`Runtime/Unity.AI.Assistant.Runtime.asmdef`) — backported from upstream **2.13.0-pre.1**. The
   assembly had empty `defineConstraints` with `includePlatforms: []`, so it compiled into IL2CPP
   player builds even though it is editor-only in practice. Adding the constraint
   `"UNITY_EDITOR || UNITY_AI_ASSISTANT_RUNTIME"` drops it from shipped builds; all 8 assemblies that
   reference it are `*.Editor` asmdefs, so nothing in a player build breaks, and in-editor
   compilation is unchanged (the `UNITY_EDITOR` arm). `Unity.AI.MCP.Runtime` and `Unity.AI.Tracing`
   also build for all platforms but were left unconstrained upstream, so they are left as-is. The
   player-build exclusion is only observable in an actual IL2CPP build; verified here only that the
   editor still compiles clean.

9. **MCP-1 (local improvement, not an upstream backport) — omit `localFixedCode` from RunCommand
   success responses** (`Modules/Unity.AI.MCP.Editor/Tools/RunCommand.cs`). Every successful
   `Unity_RunCommand` echoed the full namespace-wrapped rewrite of the script back in the response —
   pure token waste for the calling agent, on every call. It is now dropped from the success
   response and kept only on the `COMPILATION_FAILED` response, where it aids debugging. Verified:
   success responses no longer carry `localFixedCode`; a deliberate compile error still returns it.

10. **MCP-2 (local improvement, not an upstream backport) — a Warning must not fail the whole
    RunCommand** (`Modules/Unity.AI.Assistant.Tools/Scripting/RunCommandTool.cs`). `ExecuteCommand`
    treated any `LogType.Warning` in the execution logs as a failure (alongside Error/Exception) and
    threw, so the MCP layer surfaced `UNEXPECTED_ERROR: Command was executed partially...` even though
    the command ran fully — a constant false-failure for agents, since Unity code legitimately logs
    warnings (deprecations, validation notes). The predicate now checks only `Error`/`Exception`,
    matching the read-only sibling `RunReadOnlyCommandTool`; warnings still surface in `ExecutionLogs`.
    **Verify (repro doubles as the regression check):** a command calling `result.LogWarning(...)`
    returns success with the warning in the logs; a command calling `result.LogError(...)` still fails.

## Embed details / constraints

- The package must stay pinned to **2.6.0-pre.1** (external constraint). The embedded copy is the
  same version; `Packages/manifest.json` is unchanged and `packages-lock.json` flips the source to
  `embedded` automatically.
- `RelayApp~/`: the embed keeps `relay.json` **and** the Windows binary `relay_win.exe` (~124 MB);
  the ~400 MB of mac/linux binaries are skipped (Windows dev only). `relay_win.exe` must be present
  because `ServerInstaller.InstallOrUpdateRelay` copies the bundled relay from the package to
  `~/.unity/relay/` whenever the bundled version is newer than the installed one — i.e. on first use
  and after any package/relay upgrade (the smart part: an upgrade would auto-propagate the new relay
  if the pin were ever lifted). With only `relay.json`, the version check still reads fine but the
  copy then fails (`CopyToTargetDir` → "original file does not exist") and the relay never
  (re)installs — so if `~/.unity/relay` is wiped, the embedded `relay_win.exe` reinstalls it on the
  next editor load.
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
