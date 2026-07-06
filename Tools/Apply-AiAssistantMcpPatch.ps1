<#
.SYNOPSIS
Embeds com.unity.ai.assistant (pinned 2.6.0-pre.1) into Packages/ and applies the local MCP
patches: (1) Mono Assembly.Load fallback that fixes the Unity_RunCommand tool on Unity 6000.5+,
(2) lenient deserialization of string-encoded array parameters (Unity_ReadConsole flake),
(3) clear error instead of NullReferenceException when a script uses a blocked namespace.

.DESCRIPTION
The embedded package is intentionally NOT committed to git (see .gitignore). Run this script
after a fresh clone, or whenever Packages/com.unity.ai.assistant is missing or unpatched.

Modes (automatic):
  1. Packages/com.unity.ai.assistant exists          -> (re)apply the patches only. Idempotent.
  2. Missing, but Library/PackageCache copy exists   -> embed from cache (excludes the 520 MB
     RelayApp~ binaries; keeps relay.json so the relay version check short-circuits), then patch.
  3. Neither exists                                  -> prints recovery steps and exits 1.

Full background: Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md

.PARAMETER EmbedDir
Override the embed target directory (used for testing the patch logic against a copy).
#>
param(
    [string]$EmbedDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $EmbedDir) { $EmbedDir = Join-Path $repoRoot 'Packages\com.unity.ai.assistant' }

$expectedVersion = '2.6.0-pre.1'

# Each patch: relative file, a marker proving it is already applied, a unique anchor line in the
# pristine source, and the replacement block (which must contain the anchor's behavior).
$patches = @(
    @{
        Name        = 'RunCommand Mono Assembly.Load fallback'
        File        = 'Editor\Assistant\Utils\AssemblyUtils.cs'
        Marker      = 'Type.GetType("Mono.Runtime")'
        Anchor      = '            return CurrentAssemblies.LoadFromBytes(assemblyBytes);'
        Replacement = @'
            // PATCHED (see Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md):
            // On the Mono editor, CurrentAssemblies.LoadFromBytes resolves by assembly name and
            // returns the already-loaded assembly when the name collides - the dynamic run-command
            // assembly shares its name with this package's Bridge.Editor assembly, so the emitted
            // image (and its CommandScript type) is never actually loaded. Assembly.Load is safe
            // on Mono; keep CurrentAssemblies for CoreCLR, which needs it for collectible contexts.
            if (Type.GetType("Mono.Runtime") != null)
                return Assembly.Load(assemblyBytes);
            return CurrentAssemblies.LoadFromBytes(assemblyBytes);
'@
    },
    @{
        Name        = 'Lenient string-encoded array parameters'
        File        = 'Modules\Unity.AI.MCP.Editor\ToolRegistry\IToolHandler.cs'
        Marker      = 'When the target property is a collection, re-parse the string'
        Anchor      = '            return parameters.ToObject(parameterType, JsonSerializer.Create(ToolHandlerSettings.DefaultSettings));'
        Replacement = @'
            // PATCHED (see Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md):
            // Some MCP clients emit array parameters as JSON-encoded strings (e.g. "[\"error\"]").
            // When the target property is a collection, re-parse the string so deserialization
            // succeeds instead of failing with "Error converting value".
            foreach (var property in new List<JProperty>(parameters.Properties()))
            {
                if (property.Value.Type != JTokenType.String)
                    continue;

                Type targetType = null;
                foreach (var candidate in parameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        targetType = candidate.PropertyType;
                        break;
                    }
                }

                bool isCollection = targetType != null && targetType != typeof(string) &&
                    (targetType.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(targetType));
                if (!isCollection)
                    continue;

                var text = ((string)property.Value).Trim();
                if (!text.StartsWith("["))
                    continue;

                try { property.Value = JToken.Parse(text); }
                catch (JsonReaderException) { /* not actually JSON - keep the original string */ }
            }

            return parameters.ToObject(parameterType, JsonSerializer.Create(ToolHandlerSettings.DefaultSettings));
'@
    },
    @{
        Name        = 'Clear error for blocked-namespace scripts'
        File        = 'Editor\Assistant\RunCommand\RunCommandUtils.cs'
        Marker      = 'ThrowIfBlocked'
        Anchor      = @'
        internal static ExecutionResult Execute(AgentRunCommand command, string title = "")
        {
'@
        Replacement = @'
        // PATCHED (see Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md):
        // Scripts that use an unauthorized namespace leave Compilation null (BuildRunCommand skips
        // Initialize), which surfaced as an opaque NullReferenceException here. Fail with a clear,
        // actionable message instead.
        static void ThrowIfBlocked(AgentRunCommand command)
        {
            if (command.Compilation == null)
                throw new InvalidOperationException(
                    "Script was blocked before execution: it uses an unauthorized namespace " +
                    "(System.Net, System.Diagnostics, System.Runtime.InteropServices or System.Reflection).");
        }

        internal static ExecutionResult Execute(AgentRunCommand command, string title = "")
        {
            ThrowIfBlocked(command);
'@
    },
    @{
        Name        = 'Clear error for blocked-namespace scripts (readonly path)'
        File        = 'Editor\Assistant\RunCommand\RunCommandUtils.cs'
        Marker      = 'internal static ReadonlyExecutionResult ExecuteReadonly(AgentRunCommand command, string title = "")
        {
            ThrowIfBlocked(command);'
        Anchor      = @'
        internal static ReadonlyExecutionResult ExecuteReadonly(AgentRunCommand command, string title = "")
        {
'@
        Replacement = @'
        internal static ReadonlyExecutionResult ExecuteReadonly(AgentRunCommand command, string title = "")
        {
            ThrowIfBlocked(command);
'@
    },
    @{
        Name        = 'Null-safe Unsafe for blocked scripts'
        File        = 'Editor\Assistant\RunCommand\AgentRunCommand.cs'
        Marker      = 'm_Metadata?.IsUnsafe'
        Anchor      = '        public bool Unsafe => m_Metadata.IsUnsafe;'
        Replacement = @'
        // PATCHED (see Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md):
        // m_Metadata is null for blocked (unauthorized-namespace) scripts, where Initialize is
        // never called; null-guard like HasWriteOperations below so callers reach the clear
        // blocked-script error instead of an opaque NullReferenceException.
        public bool Unsafe => m_Metadata?.IsUnsafe ?? false;
'@
    }
)

# --- Mode 2: embed from the project package cache if not yet embedded -----------------------
if (-not (Test-Path $EmbedDir)) {
    $cacheDir = Get-ChildItem (Join-Path $repoRoot 'Library\PackageCache') -Directory -Filter 'com.unity.ai.assistant@*' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $cacheDir) {
        Write-Host 'ERROR: Neither the embedded package nor a Library/PackageCache copy exists.' -ForegroundColor Red
        Write-Host 'Recovery: make sure Packages/com.unity.ai.assistant is absent, open the project'
        Write-Host 'in Unity once (the registry package resolves into Library/PackageCache), close'
        Write-Host 'or focus away from Unity, then re-run this script.'
        exit 1
    }

    Write-Host "Embedding from $($cacheDir.FullName) (excluding RelayApp~)..."
    robocopy $cacheDir.FullName $EmbedDir /E /XD RelayApp~ /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "robocopy failed with exit code $LASTEXITCODE" }

    # Keep only relay.json: ServerInstaller then reports "relay up to date" instead of warning
    # every session, and never tries to copy binaries from here (relay lives in ~/.unity/relay).
    New-Item -ItemType Directory -Force (Join-Path $EmbedDir 'RelayApp~') | Out-Null
    Copy-Item (Join-Path $cacheDir.FullName 'RelayApp~\relay.json') (Join-Path $EmbedDir 'RelayApp~\relay.json')
    Write-Host 'Embed complete.'
}

# --- Version sanity check --------------------------------------------------------------------
$packageJson = Get-Content (Join-Path $EmbedDir 'package.json') -Raw | ConvertFrom-Json
if ($packageJson.version -ne $expectedVersion) {
    Write-Host "WARNING: embedded package is $($packageJson.version), expected $expectedVersion." -ForegroundColor Yellow
    Write-Host 'A newer package may have fixed the bugs upstream or moved the patch anchors - verify manually.'
}

# --- Apply the patches (idempotent) ------------------------------------------------------------
$applied = 0
foreach ($patch in $patches) {
    $targetFile = Join-Path $EmbedDir $patch.File

    # Match in LF space: the package sources are CRLF while this script's here-strings carry
    # whatever line endings the script file has. Restore CRLF on write.
    $content     = (Get-Content $targetFile -Raw) -replace "`r`n", "`n"
    $marker      = $patch.Marker      -replace "`r`n", "`n"
    $anchor      = $patch.Anchor      -replace "`r`n", "`n"
    $replacement = $patch.Replacement -replace "`r`n", "`n"

    if ($content.Contains($marker)) {
        Write-Host "[$($patch.Name)] already patched." -ForegroundColor Green
        continue
    }
    if (-not $content.Contains($anchor)) {
        throw "[$($patch.Name)] anchor not found in $targetFile - the package source changed; patch manually (see guide)."
    }

    $patchedContent = $content.Replace($anchor, $replacement) -replace "`n", "`r`n"
    Set-Content -Path $targetFile -Value $patchedContent -NoNewline
    Write-Host "[$($patch.Name)] patched $($patch.File)" -ForegroundColor Green
    $applied++
}

if ($applied -gt 0) {
    Write-Host 'Done. Focus the Unity Editor (or run Assets > Refresh) so it re-resolves and recompiles,'
    Write-Host 'then verify with a trivial Unity_RunCommand call.'
}
