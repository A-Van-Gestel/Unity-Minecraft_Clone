using System;
using System.IO;
using Benchmarks;
using Data;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Editor.ProjectUtilities
{
    /// <summary>
    /// Bakes build-time provenance into the <see cref="BuildStamp"/> asset so player builds can
    /// report the git commit, IL2CPP compiler configuration, and Burst AOT settings they were
    /// actually compiled with.
    /// <para>Each of these is known only at build time. Querying them from a running player yields
    /// either nothing (git) or a misleading constant (<c>BurstCompiler.Options</c> is editor-only
    /// and reports safety checks as enabled in every player), which silently misattributes
    /// benchmark captures.</para>
    /// </summary>
    public class BuildStampBaker : IPreprocessBuildWithReport
    {
        /// <summary>Project-relative path to the stamp asset written by this baker.</summary>
        public const string AssetPath = "Assets/Resources/Data/BuildStamp.asset";

        private const string MENU_PATH = "Minecraft Clone/Bake Build Stamp";

        // Runs after GameVersionManager (order 0) so build-time mutations stay in a predictable order.
        public int callbackOrder => 10;

        /// <summary>
        /// Called automatically by Unity right before a build starts. Bakes the stamp for the
        /// platform being built.
        /// </summary>
        /// <param name="report">The build report describing the pending build.</param>
        public void OnPreprocessBuild(BuildReport report)
        {
            Bake(report.summary.platform, logResult: true);
        }

        /// <summary>
        /// Bakes the stamp manually for the active build target. Provided as a fallback for cases
        /// where mutating an asset from the build hook is undesirable — run this, then build.
        /// </summary>
        [MenuItem(MENU_PATH)]
        public static void BakeForActiveTarget()
        {
            Bake(EditorUserBuildSettings.activeBuildTarget, logResult: true);
        }

        /// <summary>
        /// Writes current provenance into the stamp asset.
        /// </summary>
        /// <param name="target">Build target whose player settings should be recorded.</param>
        /// <param name="logResult">Whether to log the baked values to the console.</param>
        private static void Bake(BuildTarget target, bool logResult)
        {
            BuildStamp stamp = AssetDatabase.LoadAssetAtPath<BuildStamp>(AssetPath);
            if (!stamp)
            {
                stamp = CreateStampAsset();
                if (!stamp) return;
            }

            NamedBuildTarget namedTarget = ResolveNamedBuildTarget(target);
            BenchmarkEnvironment.TryQueryGit(out string commit, out string branch, out bool dirty);
            BurstAotFlags burst = ReadBurstAotSettings(target);

            // Edit through SerializedObject so the private [SerializeField] backing fields stay
            // private (the asset is read-only to all runtime code) and the write is undo/dirty-tracked.
            SerializedObject so = new SerializedObject(stamp);
            so.FindProperty("_gitCommit").stringValue = dirty ? commit + "-dirty" : commit;
            so.FindProperty("_gitBranch").stringValue = branch;
            so.FindProperty("_gitDirty").boolValue = dirty;
            so.FindProperty("_il2CppConfiguration").stringValue =
                PlayerSettings.GetScriptingBackend(namedTarget) == ScriptingImplementation.IL2CPP
                    ? PlayerSettings.GetIl2CppCompilerConfiguration(namedTarget).ToString()
                    : "n/a (Mono)";
            so.FindProperty("_scriptingBackend").stringValue =
                PlayerSettings.GetScriptingBackend(namedTarget).ToString();
            so.FindProperty("_burstSafetyChecks").boolValue = burst.SafetyChecks;
            so.FindProperty("_burstOptimizations").boolValue = burst.Optimizations;
            so.FindProperty("_bakedAtUtc").stringValue = DateTime.UtcNow.ToString("o");
            so.FindProperty("_isBaked").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(stamp);
            AssetDatabase.SaveAssetIfDirty(stamp);

            if (!logResult) return;

            Debug.Log($"[BuildStampBaker] Baked stamp for {target}: " +
                      $"commit={so.FindProperty("_gitCommit").stringValue}, branch={branch}, " +
                      $"config={so.FindProperty("_il2CppConfiguration").stringValue}, " +
                      $"burstSafetyChecks={burst.SafetyChecks}, burstOptimizations={burst.Optimizations}");
        }

        /// <summary>
        /// Creates the stamp asset and its parent folder if missing.
        /// </summary>
        /// <returns>The created asset, or <c>null</c> if creation failed (logged).</returns>
        private static BuildStamp CreateStampAsset()
        {
            try
            {
                string directory = Path.GetDirectoryName(AssetPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                BuildStamp created = ScriptableObject.CreateInstance<BuildStamp>();
                AssetDatabase.CreateAsset(created, AssetPath);
                Debug.Log($"[BuildStampBaker] Created missing stamp asset at '{AssetPath}'.");
                return created;
            }
            catch (Exception e)
            {
                // Never fail a build over provenance: an unstamped build reports "(unstamped build)",
                // which is honest, whereas aborting a 15-minute Master build over this is not a trade
                // worth making.
                Debug.LogError($"[BuildStampBaker] Could not create '{AssetPath}': {e.Message}. " +
                               "The build will report unstamped provenance.");
                return null;
            }
        }

        /// <summary>
        /// Maps a <see cref="BuildTarget"/> to the <see cref="NamedBuildTarget"/> its player settings
        /// are stored under.
        /// </summary>
        private static NamedBuildTarget ResolveNamedBuildTarget(BuildTarget target)
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);
            return NamedBuildTarget.FromBuildTargetGroup(group);
        }

        /// <summary>Burst AOT flags relevant to benchmark provenance.</summary>
        private struct BurstAotFlags
        {
            public bool SafetyChecks;
            public bool Optimizations;
        }

        /// <summary>
        /// Reads the Burst AOT settings JSON for the target being built.
        /// <para>Burst exposes these only through internal editor types, so the on-disk settings file
        /// is the supported read path. A missing file means Burst is using its defaults — safety
        /// checks off, optimizations on — which is what an unconfigured project ships with.</para>
        /// </summary>
        /// <param name="target">The build target whose Burst settings should be read.</param>
        /// <returns>The safety-check and optimization flags Burst will AOT-compile with.</returns>
        private static BurstAotFlags ReadBurstAotSettings(BuildTarget target)
        {
            // Burst's defaults, per BurstPlatformAotSettings.InitialiseDefaults.
            BurstAotFlags flags = new BurstAotFlags { SafetyChecks = false, Optimizations = true };

            // Burst treats 32- and 64-bit Windows as one settings file (BurstPlatformAotSettings.ResolveTarget).
            BuildTarget resolved = target == BuildTarget.StandaloneWindows64
                ? BuildTarget.StandaloneWindows
                : target;

            string path = $"ProjectSettings/BurstAotSettings_{resolved}.json";
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[BuildStampBaker] No Burst AOT settings at '{path}'. " +
                                 "Reporting Burst defaults (safety checks off, optimizations on).");
                return flags;
            }

            if (TryParseBurstAotSettings(File.ReadAllText(path), out bool safety, out bool optimizations))
            {
                flags.SafetyChecks = safety;
                flags.Optimizations = optimizations;
            }
            else
            {
                Debug.LogWarning($"[BuildStampBaker] Could not parse '{path}'. Reporting Burst defaults.");
            }

            return flags;
        }

        /// <summary>
        /// Parses the two Burst AOT flags this baker reports out of Burst's settings JSON.
        /// </summary>
        /// <remarks>
        /// Public and text-driven so the parse can be exercised against mutated input. That matters
        /// here because Burst's defaults are <c>SafetyChecks=false, Optimizations=true</c> — identical
        /// to what a silently failed parse would produce — so a correct-looking result proves nothing
        /// unless the parser is fed values that differ from the defaults.
        /// <para>Burst nests its payload under a <c>"MonoBehaviour"</c> key.
        /// <see cref="JsonUtility"/> does not deserialize a nested object into a nested
        /// <c>[Serializable]</c> field here, so the payload braces are sliced out and the flat
        /// remainder is parsed instead.</para>
        /// </remarks>
        /// <param name="json">Raw contents of a <c>BurstAotSettings_*.json</c> file.</param>
        /// <param name="safetyChecks">Parsed <c>EnableSafetyChecks</c> value.</param>
        /// <param name="optimizations">Parsed <c>EnableOptimisations</c> value.</param>
        /// <returns><c>true</c> when both flags were read from the payload.</returns>
        public static bool TryParseBurstAotSettings(string json, out bool safetyChecks, out bool optimizations)
        {
            safetyChecks = false;
            optimizations = true;

            if (string.IsNullOrWhiteSpace(json)) return false;

            // Require the wrapper key: without it, slicing would hand JsonUtility the outer object,
            // whose fields do not match the payload — yielding defaults while reporting success.
            int keyIndex = json.IndexOf("\"MonoBehaviour\"", StringComparison.Ordinal);
            if (keyIndex < 0) return false;

            int open = json.IndexOf('{', keyIndex);
            if (open < 0) return false;

            // Match braces rather than assuming the payload is the last object in the file.
            int depth = 0;
            int close = -1;
            for (int i = open; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}' && --depth == 0)
                {
                    close = i;
                    break;
                }
            }

            if (close < 0) return false;

            try
            {
                BurstSettingsPayload payload =
                    JsonUtility.FromJson<BurstSettingsPayload>(json.Substring(open, close - open + 1));
                if (payload == null) return false;

                safetyChecks = payload.EnableSafetyChecks;
                optimizations = payload.EnableOptimisations;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Field names are the JSON keys Burst writes and must match them verbatim — including the
        // British "Optimisations" spelling — so the Unity serialized-field naming rule does not apply.
        // ReSharper disable InconsistentNaming
        [Serializable]
        private class BurstSettingsPayload
        {
            public bool EnableSafetyChecks;
            public bool EnableOptimisations;
        }
        // ReSharper restore InconsistentNaming
    }
}
