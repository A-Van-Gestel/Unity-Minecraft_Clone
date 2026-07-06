<#
.SYNOPSIS
Embeds com.unity.ai.assistant (pinned 2.6.0-pre.1) into Packages/ and applies the Mono
Assembly.Load fallback patch that fixes the Unity_RunCommand MCP tool on Unity 6000.5+.

.DESCRIPTION
The embedded package is intentionally NOT committed to git (see .gitignore). Run this script
after a fresh clone, or whenever Packages/com.unity.ai.assistant is missing or unpatched.

Modes (automatic):
  1. Packages/com.unity.ai.assistant exists          -> (re)apply the patch only. Idempotent.
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
$targetFile = Join-Path $EmbedDir 'Editor\Assistant\Utils\AssemblyUtils.cs'

$expectedVersion = '2.6.0-pre.1'
$patchedMarker   = 'Type.GetType("Mono.Runtime")'
$anchor          = '            return CurrentAssemblies.LoadFromBytes(assemblyBytes);'
$replacement     = @'
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
    Write-Host 'A newer package may have fixed the bug upstream or moved the patch anchor - verify manually.'
}

# --- Apply the patch (idempotent) -------------------------------------------------------------
$content = Get-Content $targetFile -Raw
if ($content.Contains($patchedMarker)) {
    Write-Host 'Already patched - nothing to do.' -ForegroundColor Green
    exit 0
}
if (-not $content.Contains($anchor)) {
    throw "Patch anchor not found in $targetFile - the package source changed; patch manually (see guide)."
}

$content = $content.Replace($anchor, $replacement)
Set-Content -Path $targetFile -Value $content -NoNewline
Write-Host "Patched $targetFile" -ForegroundColor Green
Write-Host 'Done. Focus the Unity Editor (or run Assets > Refresh) so it re-resolves and recompiles,'
Write-Host 'then verify with a trivial Unity_RunCommand call.'
