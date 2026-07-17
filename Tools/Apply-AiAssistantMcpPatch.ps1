<#
.SYNOPSIS
Embeds com.unity.ai.assistant (pinned 2.6.0-pre.1) into Packages/ and applies the local MCP
patches: (1) Mono Assembly.Load fallback that fixes the Unity_RunCommand tool on Unity 6000.5+,
(2) lenient deserialization of string-encoded array parameters (Unity_ReadConsole flake),
(3) clear error instead of NullReferenceException when a script uses a blocked namespace,
(4) silence the per-domain-reload "ApiNoLongerSupported" account-refresh console spam,
(5) Backport A (from upstream 2.13.0-pre.1): get_components deep-graph / non-TRS Matrix4x4
    crash guard (DepthLimitedJTokenWriter + Matrix4x4Converter).

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
    },
    @{
        Name        = 'Silence ApiNoLongerSupported console spam'
        File        = 'Modules\Unity.AI.Toolkit.Accounts\Services\Core\AccountApi.cs'
        Marker      = 'without this guard the same message spams the console'
        Anchor      = '                    var errorMessage = result.Result.Error.AiResponseError == AiResultErrorEnum.RateLimitExceeded // typically means wrong url (staging vs prod)'
        Replacement = @'
                    // PATCHED (see Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md):
                    // ApiNoLongerSupported is definitive for this pinned pre-release package (the
                    // endpoint is retired) and is already recorded in PackagesSupported above.
                    // The s_LastLoggedError dedup is static and resets on every domain reload, so
                    // without this guard the same message spams the console after each recompile.
                    if (result.Result.Error.AiResponseError == AiResultErrorEnum.ApiNoLongerSupported)
                        return null;

                    var errorMessage = result.Result.Error.AiResponseError == AiResultErrorEnum.RateLimitExceeded // typically means wrong url (staging vs prod)
'@
    },

    # --- Backport A: get_components deep-graph / non-TRS Matrix4x4 crash (from 2.13.0-pre.1) ------
    @{
        Name        = 'Backport A1: Matrix4x4Converter class'
        File        = 'Modules\Unity.AI.MCP.Runtime\Serialization\UnityTypeConverters.cs'
        Marker      = 'class Matrix4x4Converter'
        Anchor      = @'
            throw new JsonSerializationException($"Unexpected token type '{reader.TokenType}' when deserializing UnityEngine.Object");
        }
    }
'@
        Replacement = @'
            throw new JsonSerializationException($"Unexpected token type '{reader.TokenType}' when deserializing UnityEngine.Object");
        }
    }

    // PATCHED (see Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md):
    // Backported from com.unity.ai.assistant 2.13.0-pre.1 (UUM-144888). Without an explicit
    // Matrix4x4 converter a component exposing a non-TRS Matrix4x4 was serialized field-by-field
    // via reflection; combined with the unbounded write recursion (see GameObjectSerializer) this
    // could overflow the stack. Emit the 16 elements directly instead.
    class Matrix4x4Converter : JsonConverter<Matrix4x4>
    {
        public override void WriteJson(JsonWriter writer, Matrix4x4 value, JsonSerializer serializer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("m00"); writer.WriteValue(value.m00);
            writer.WritePropertyName("m01"); writer.WriteValue(value.m01);
            writer.WritePropertyName("m02"); writer.WriteValue(value.m02);
            writer.WritePropertyName("m03"); writer.WriteValue(value.m03);
            writer.WritePropertyName("m10"); writer.WriteValue(value.m10);
            writer.WritePropertyName("m11"); writer.WriteValue(value.m11);
            writer.WritePropertyName("m12"); writer.WriteValue(value.m12);
            writer.WritePropertyName("m13"); writer.WriteValue(value.m13);
            writer.WritePropertyName("m20"); writer.WriteValue(value.m20);
            writer.WritePropertyName("m21"); writer.WriteValue(value.m21);
            writer.WritePropertyName("m22"); writer.WriteValue(value.m22);
            writer.WritePropertyName("m23"); writer.WriteValue(value.m23);
            writer.WritePropertyName("m30"); writer.WriteValue(value.m30);
            writer.WritePropertyName("m31"); writer.WriteValue(value.m31);
            writer.WritePropertyName("m32"); writer.WriteValue(value.m32);
            writer.WritePropertyName("m33"); writer.WriteValue(value.m33);
            writer.WriteEndObject();
        }

        public override Matrix4x4 ReadJson(JsonReader reader, Type objectType, Matrix4x4 existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.StartArray)
            {
                JArray ja = JArray.Load(reader);
                var m = new Matrix4x4();
                m.m00 = (float)ja[0];  m.m01 = (float)ja[1];  m.m02 = (float)ja[2];  m.m03 = (float)ja[3];
                m.m10 = (float)ja[4];  m.m11 = (float)ja[5];  m.m12 = (float)ja[6];  m.m13 = (float)ja[7];
                m.m20 = (float)ja[8];  m.m21 = (float)ja[9];  m.m22 = (float)ja[10]; m.m23 = (float)ja[11];
                m.m30 = (float)ja[12]; m.m31 = (float)ja[13]; m.m32 = (float)ja[14]; m.m33 = (float)ja[15];
                return m;
            }
            JObject jo = JObject.Load(reader);
            var mat = new Matrix4x4();
            mat.m00 = (float)jo["m00"]; mat.m01 = (float)jo["m01"]; mat.m02 = (float)jo["m02"]; mat.m03 = (float)jo["m03"];
            mat.m10 = (float)jo["m10"]; mat.m11 = (float)jo["m11"]; mat.m12 = (float)jo["m12"]; mat.m13 = (float)jo["m13"];
            mat.m20 = (float)jo["m20"]; mat.m21 = (float)jo["m21"]; mat.m22 = (float)jo["m22"]; mat.m23 = (float)jo["m23"];
            mat.m30 = (float)jo["m30"]; mat.m31 = (float)jo["m31"]; mat.m32 = (float)jo["m32"]; mat.m33 = (float)jo["m33"];
            return mat;
        }
    }
'@
    },
    @{
        Name        = 'Backport A2a: register Matrix4x4Converter on GameObjectSerializer output'
        File        = 'Modules\Unity.AI.MCP.Editor\Helpers\GameObjectSerializer.cs'
        Marker      = 'new Matrix4x4Converter()'
        Anchor      = '                new UnityEngineObjectConverter() // Handles serialization of references'
        Replacement = @'
                new UnityEngineObjectConverter(), // Handles serialization of references
                new Matrix4x4Converter() // PATCHED (backport 2.13.0-pre.1): non-TRS Matrix4x4 support
'@
    },
    @{
        Name        = 'Backport A2b: DepthLimitedJTokenWriter (bound write recursion)'
        File        = 'Modules\Unity.AI.MCP.Editor\Helpers\GameObjectSerializer.cs'
        Marker      = 'k_MaxSerializeDepth'
        Anchor      = @'
        // Helper to create JToken using the output serializer
        static JToken CreateTokenFromValue(object value, Type type)
'@
        Replacement = @'
        // PATCHED (see Documentation/Guides/UNITY_MCP_RUNCOMMAND_PATCH_GUIDE.md):
        // Backported from com.unity.ai.assistant 2.13.0-pre.1. Newtonsoft's MaxDepth guards only the
        // read path; nothing bounds recursion on the write path, so serializing a Component exposing
        // a deep non-UnityEngine.Object reference graph (e.g. a linked list) overflows the C stack in
        // mono_gc_alloc_obj and crashes the Editor. This writer caps write depth; the existing
        // JsonSerializationException catch below turns the overflow into a skipped field instead.
        const int k_MaxSerializeDepth = 64;

        sealed class DepthLimitedJTokenWriter : JTokenWriter
        {
            readonly int m_MaxDepth;
            public DepthLimitedJTokenWriter(int maxDepth) { m_MaxDepth = maxDepth; }

            public override void WriteStartObject()
            {
                if (Top >= m_MaxDepth)
                    throw new JsonSerializationException($"Maximum depth {m_MaxDepth} exceeded.");
                base.WriteStartObject();
            }

            public override void WriteStartArray()
            {
                if (Top >= m_MaxDepth)
                    throw new JsonSerializationException($"Maximum depth {m_MaxDepth} exceeded.");
                base.WriteStartArray();
            }
        }

        // Helper to create JToken using the output serializer
        static JToken CreateTokenFromValue(object value, Type type)
'@
    },
    @{
        Name        = 'Backport A2c: serialize via the depth-limited writer'
        File        = 'Modules\Unity.AI.MCP.Editor\Helpers\GameObjectSerializer.cs'
        Marker      = 'new DepthLimitedJTokenWriter(k_MaxSerializeDepth)'
        Anchor      = @'
                // Use the pre-configured OUTPUT serializer instance
                return JToken.FromObject(value, _outputSerializer);
'@
        Replacement = @'
                // PATCHED (backport 2.13.0-pre.1): serialize through the depth-limited writer so a
                // deep graph throws (caught below -> field skipped) instead of overflowing the stack.
                using var writer = new DepthLimitedJTokenWriter(k_MaxSerializeDepth);
                _outputSerializer.Serialize(writer, value);
                return writer.Token;
'@
    },
    @{
        Name        = 'Backport A3: register Matrix4x4Converter on ManageGameObject input'
        File        = 'Modules\Unity.AI.MCP.Editor\Tools\ManageGameObject.cs'
        Marker      = 'new Matrix4x4Converter()'
        Anchor      = @'
                new BoundsConverter(),
                new UnityEngineObjectConverter()
            }
'@
        Replacement = @'
                new BoundsConverter(),
                new UnityEngineObjectConverter(),
                new Matrix4x4Converter() // PATCHED (backport 2.13.0-pre.1): non-TRS Matrix4x4 support
            }
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
