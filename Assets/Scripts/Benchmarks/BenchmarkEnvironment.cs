using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Data;
using Unity.Burst;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if UNITY_EDITOR
using System.Diagnostics;
#endif

namespace Benchmarks
{
    /// <summary>
    /// Captures the runtime context relevant to performance benchmarks (CPU, RAM, OS, Unity build,
    /// scripting backend, Burst settings, and — in Editor — the current git commit hash) and
    /// formats it into a multi-line block ready to be prepended to a benchmark report.
    /// </summary>
    /// <remarks>
    /// <para>Used by <c>MeshGenerationBenchmark</c> and other in-engine benchmarks so the captured
    /// numbers can be cross-referenced with the build they were captured against without manual
    /// transcription. See <c>Documentation/Performance/README.md</c> for the baseline-capture protocol.</para>
    /// <para>All lookups are runtime-safe and do not throw on the happy path. The git hash lookup is
    /// the only operation that touches an external process (Editor only) and is wrapped in a try/catch
    /// with a 2-second timeout so it cannot block a benchmark indefinitely.</para>
    /// </remarks>
    public static class BenchmarkEnvironment
    {
        /// <summary>
        /// Builds a multi-line description of the current system, build, and Burst settings.
        /// </summary>
        /// <returns>A human-readable string with three sections: <c>=== System ===</c>, <c>=== Build ===</c>, <c>=== Burst ===</c>.</returns>
        public static string DescribeSystem()
        {
            StringBuilder sb = new StringBuilder(capacity: 1024);

            sb.AppendLine("=== System ===");
            sb.AppendLine($"CPU:            {SystemInfo.processorType.Trim()}");
            sb.AppendLine($"CPU threads:    {SystemInfo.processorCount}");
            sb.AppendLine($"CPU base MHz:   {SystemInfo.processorFrequency}");
            sb.AppendLine($"RAM:            {SystemInfo.systemMemorySize:N0} MB");
            sb.AppendLine($"OS:             {SystemInfo.operatingSystem}");
            sb.AppendLine($"Graphics API:   {SystemInfo.graphicsDeviceType}");
            sb.AppendLine();

            sb.AppendLine("=== Build ===");
            sb.AppendLine($"Unity:          {Application.unityVersion}");
            sb.AppendLine($"Platform:       {Application.platform}");
            sb.AppendLine($"Mode:           {(Application.isEditor ? "Editor" : "Player")}");
            sb.AppendLine($"Backend:        {ScriptingBackend}");

            // Development vs Release is NOT cosmetic provenance: the P-4 budgets are frame-time-proportional
            // (PipelinePassBudget.ComputeQuota scales by unscaledDeltaTime, ScaleCeilingMs by the FPS-cap
            // interval), so a Development Build's overhead lengthens frames, inflates quotas and shifts the
            // admission regime the capture exists to measure. Two reports that differ only in this line are
            // not comparable, and without it that difference is invisible — the FP-6 defect exactly.
            sb.AppendLine($"Development:    {(Debug.isDebugBuild ? "Yes" : "No")}");

            // The IL2CPP compiler configuration is an axis INDEPENDENT of the Development flag above
            // (a Master build and a Release build are both non-Development) and no runtime managed API
            // exposes it, so it can only come from the stamp.
            BuildStamp stamp = LoadBuildStamp();
            sb.AppendLine($"Configuration:  {DescribeConfiguration(stamp)}");

            // Application.buildGUID is the all-zeros sentinel in Editor mode and meaningful only in Player builds.
            string buildGUID = Application.buildGUID;
            if (!string.IsNullOrEmpty(buildGUID) && buildGUID != "00000000000000000000000000000000")
                sb.AppendLine($"Build GUID:     {buildGUID}");

            sb.AppendLine($"Git commit:     {DescribeGitCommit(stamp)}");
            sb.AppendLine($"Git branch:     {DescribeGitBranch(stamp)}");
            sb.AppendLine();

            sb.AppendLine("=== Burst ===");
            AppendBurstSection(sb, stamp);

            return sb.ToString();
        }

        // ----- Shared Report Utilities -----

        private static readonly Regex s_richTextTagPattern = new Regex(
            @"</?(color|b|i|size|u)(=[^>]*)?>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Strips Unity rich-text tags (<c>&lt;color&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, etc.)
        /// so the string reads cleanly in a plain-text editor.
        /// </summary>
        /// <param name="richText">The rich-text input string.</param>
        /// <returns>The input with all recognized rich-text tags removed.</returns>
        public static string StripRichTextTags(string richText) =>
            s_richTextTagPattern.Replace(richText, string.Empty);

        /// <summary>
        /// Formats a <see cref="TimeSpan"/> as a compact human-readable string.
        /// Examples: <c>"42.3 s"</c>, <c>"7m 12s"</c>, <c>"1h 4m 32s"</c>.
        /// </summary>
        /// <param name="ts">The duration to format.</param>
        /// <returns>A compact duration string.</returns>
        public static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60)
                return $"{ts.TotalSeconds:F1} s";
            if (ts.TotalHours < 1)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        }

        /// <summary>
        /// Strips rich-text tags from a report string and writes the plain-text version to a timestamped
        /// file. On desktop/editor this is <c>Application.persistentDataPath/Benchmarks/</c>; on Android it
        /// is the public <c>Downloads/&lt;product&gt;/</c> collection (via MediaStore) instead, because
        /// <c>persistentDataPath</c> is app-private there — invisible to file managers and unopenable via
        /// <c>file://</c>. Android falls back to <c>persistentDataPath</c> if MediaStore fails.
        /// </summary>
        /// <param name="richTextReport">The full report with rich-text tags.</param>
        /// <param name="filenamePrefix">Prefix for the output file (e.g., "BenchmarkRun" or "MeshGenerationBenchmark").</param>
        /// <returns>The written location (full path, or public Downloads relative path on Android) on
        /// success, or <c>null</c> on failure.</returns>
        public static string WriteReportToDisk(string richTextReport, string filenamePrefix)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string fileName = $"{filenamePrefix}_{timestamp}.log";
            string plainText = StripRichTextTags(richTextReport);

            // Android: target public Downloads so the file is retrievable from the Files / Downloads app.
            if (Application.platform == RuntimePlatform.Android)
            {
                string downloadsLocation = TryWriteToAndroidDownloads(fileName, plainText);
                if (!string.IsNullOrEmpty(downloadsLocation))
                {
                    Debug.Log($"<color=cyan>[Benchmark] Report written to:</color> {downloadsLocation}");
                    return downloadsLocation;
                }
                // MediaStore failed — fall through to persistentDataPath (still adb-pullable).
            }

            try
            {
                string folder = Path.Combine(Application.persistentDataPath, "Benchmarks");
                Directory.CreateDirectory(folder);
                string fullPath = Path.Combine(folder, fileName);

                File.WriteAllText(fullPath, plainText);

                Debug.Log($"<color=cyan>[Benchmark] Report written to:</color> {fullPath}");
                return fullPath;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Benchmark] Failed to write report to disk: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes <paramref name="content"/> into the device's public <c>Downloads</c> collection via
        /// MediaStore (Android 10+ / API 29+, no storage permission required), so the file is reachable
        /// from the Files / Downloads app — unlike <c>persistentDataPath</c>, which is app-private. The JNI
        /// body compiles only under the Android target (<c>#if UNITY_ANDROID</c>); elsewhere it is a no-op
        /// returning <c>null</c> so the caller falls back to <c>persistentDataPath</c>.
        /// </summary>
        /// <param name="fileName">Display name for the created file.</param>
        /// <param name="content">UTF-8 text to write.</param>
        /// <returns>The public relative path on success, or <c>null</c> on any failure.</returns>
        private static string TryWriteToAndroidDownloads(string fileName, string content)
        {
#if UNITY_ANDROID
            try
            {
                // Group reports under Downloads/<product> so they are easy to find and clean up.
                string relativeDir = "Download/" + Application.productName;

                using AndroidJavaClass player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using AndroidJavaObject resolver = activity.Call<AndroidJavaObject>("getContentResolver");

                // Column-name string literals are the stable MediaStore.MediaColumns values, used directly
                // to avoid extra JNI static lookups: DISPLAY_NAME / MIME_TYPE / RELATIVE_PATH.
                using AndroidJavaObject values = new AndroidJavaObject("android.content.ContentValues");
                values.Call("put", "_display_name", fileName);
                values.Call("put", "mime_type", "text/plain");
                values.Call("put", "relative_path", relativeDir);

                using AndroidJavaClass downloads = new AndroidJavaClass("android.provider.MediaStore$Downloads");
                using AndroidJavaObject collection = downloads.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");

                using AndroidJavaObject itemUri = resolver.Call<AndroidJavaObject>("insert", collection, values);
                if (itemUri == null) return null;

                using AndroidJavaObject stream = resolver.Call<AndroidJavaObject>("openOutputStream", itemUri);
                if (stream == null) return null;

                byte[] bytes = Encoding.UTF8.GetBytes(content);
                stream.Call("write", bytes);
                stream.Call("flush");
                stream.Call("close");

                return relativeDir + "/" + fileName;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Benchmark] MediaStore write to Downloads failed ({e.Message}); " +
                                 "falling back to persistentDataPath.");
                return null;
            }
#else
            // Non-Android targets never reach the Android branch in WriteReportToDisk; stub for compilation.
            _ = fileName;
            _ = content;
            return null;
#endif
        }

        /// <summary>
        /// Opens the location where reports are stored for the user to browse. On desktop/editor this opens
        /// <paramref name="desktopFolderPath"/> in the OS file browser; on Android it opens the system
        /// <b>Downloads</b> view (where <see cref="WriteReportToDisk"/> places reports), since a
        /// <c>file://</c> URL into app-private storage is a no-op there.
        /// </summary>
        /// <param name="desktopFolderPath">The report folder to open on desktop/editor (ignored on Android).</param>
        public static void OpenReportsLocation(string desktopFolderPath)
        {
            // Android: a file:// URL is blocked, and reports live in MediaStore Downloads (no real path),
            // so open the system Downloads UI instead. Fall through to file:// if the intent fails.
            if (Application.platform == RuntimePlatform.Android && TryOpenAndroidDownloads())
                return;

            if (!string.IsNullOrEmpty(desktopFolderPath))
                Application.OpenURL("file:///" + desktopFolderPath.Replace('\\', '/'));
        }

        /// <summary>
        /// Launches the Android system Downloads view (<c>DownloadManager.ACTION_VIEW_DOWNLOADS</c>). The JNI
        /// body compiles only under the Android target; elsewhere it is a no-op returning <c>false</c>.
        /// </summary>
        /// <returns><c>true</c> if the Downloads view was launched; <c>false</c> on any failure.</returns>
        private static bool TryOpenAndroidDownloads()
        {
#if UNITY_ANDROID
            try
            {
                const int FLAG_ACTIVITY_NEW_TASK = 0x10000000;

                using AndroidJavaClass player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");

                // DownloadManager.ACTION_VIEW_DOWNLOADS == "android.intent.action.VIEW_DOWNLOADS".
                using AndroidJavaObject intent = new AndroidJavaObject("android.content.Intent", "android.intent.action.VIEW_DOWNLOADS");
                intent.Call<AndroidJavaObject>("addFlags", FLAG_ACTIVITY_NEW_TASK);
                activity.Call("startActivity", intent);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Benchmark] Could not open the Android Downloads view ({e.Message}).");
                return false;
            }
#else
            return false;
#endif
        }

        // ----- Helpers -----

        /// <summary>
        /// Returns "IL2CPP" or "Mono" based on the active scripting backend at compile time.
        /// </summary>
        private static string ScriptingBackend =>
#if ENABLE_IL2CPP
            "IL2CPP";
#else
            "Mono";
#endif

        private static string OnOff(bool flag) => flag ? "Enabled" : "Disabled";

        // ----- Build Provenance -----

        #region Build provenance

        /// <summary>
        /// Loads the baked <see cref="BuildStamp"/>, or <c>null</c> when the asset is absent.
        /// </summary>
        private static BuildStamp LoadBuildStamp() => Resources.Load<BuildStamp>(BuildStamp.ResourcePath);

        /// <summary>
        /// Describes the IL2CPP compiler configuration. In the Editor there is no such configuration
        /// to report; in a player it is only knowable from the stamp.
        /// </summary>
        private static string DescribeConfiguration(BuildStamp stamp)
        {
            if (Application.isEditor) return "n/a (Editor)";
            return stamp && stamp.IsBaked ? stamp.Il2CppConfiguration : BuildStamp.UnknownValue;
        }

        /// <summary>
        /// Describes the source commit: queried live in the Editor, read from the stamp in a player.
        /// </summary>
        private static string DescribeGitCommit(BuildStamp stamp)
        {
            if (Application.isEditor) return GetGitCommitHash();
            return stamp && stamp.IsBaked ? stamp.GitCommit : BuildStamp.UnknownValue;
        }

        /// <summary>
        /// Describes the source branch: queried live in the Editor, read from the stamp in a player.
        /// </summary>
        /// <param name="stamp">The baked stamp; unused in the Editor, which queries git directly.</param>
        // ReSharper disable once UnusedParameter.Local — used only in the non-Editor branch below.
        private static string DescribeGitBranch(BuildStamp stamp)
        {
#if UNITY_EDITOR
            // `branch` carries its own sentinel when the query fails, so the result is reportable either way.
            TryQueryGit(out _, out string branch, out _);
            return branch;
#else
            return stamp && stamp.IsBaked ? stamp.GitBranch : BuildStamp.UnknownValue;
#endif
        }

        /// <summary>
        /// Appends the Burst section, sourcing safety-check and optimization state correctly per context.
        /// </summary>
        /// <remarks>
        /// <c>BurstCompilerOptions.EnableBurstSafetyChecks</c> is documented editor-only ("Does not have
        /// an impact on player mode") and is hardcoded <c>true</c> by the options constructor, so reading
        /// it in a player reports <c>Enabled</c> for every build regardless of the AOT settings that
        /// actually governed compilation. Players must use the stamp; the Editor value is authoritative
        /// only in the Editor.
        /// </remarks>
        private static void AppendBurstSection(StringBuilder sb, BuildStamp stamp)
        {
            BurstCompilerOptions options = BurstCompiler.Options;
            sb.AppendLine($"Compilation:    {OnOff(options.EnableBurstCompilation)}");

            if (Application.isEditor)
            {
                sb.AppendLine($"Safety checks:  {OnOff(options.EnableBurstSafetyChecks)}");
                sb.AppendLine($"Synchronous:    {OnOff(options.EnableBurstCompileSynchronously)}");
            }
            else if (stamp && stamp.IsBaked)
            {
                sb.AppendLine($"Safety checks:  {OnOff(stamp.BurstSafetyChecks)} (AOT)");
                sb.AppendLine($"Optimizations:  {OnOff(stamp.BurstOptimizations)} (AOT)");
            }
            else
            {
                sb.AppendLine($"Safety checks:  {BuildStamp.UnknownValue}");
                sb.AppendLine($"Optimizations:  {BuildStamp.UnknownValue}");
            }

            sb.AppendLine();
        }

        #endregion

        /// <summary>
        /// Shells out to <c>git rev-parse --short HEAD</c> to obtain the current commit hash,
        /// annotated with <c>-dirty</c> when the working tree has uncommitted changes.
        /// Editor-only — player builds have no shell access and read the baked
        /// <see cref="BuildStamp"/> instead.
        /// </summary>
        /// <returns>The short commit hash, or a sentinel describing why it could not be obtained.</returns>
        private static string GetGitCommitHash()
        {
#if UNITY_EDITOR
            return TryQueryGit(out string commit, out _, out bool dirty)
                ? (dirty ? commit + "-dirty" : commit)
                : commit; // On failure `commit` already carries the explanatory sentinel.
#else
            return BuildStamp.UnknownValue;
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// Queries the repository for the current commit, branch, and working-tree cleanliness.
        /// <para>Lives here rather than in the editor assembly so the benchmark harness and the
        /// build-time <c>BuildStampBaker</c> read git through one implementation and cannot report
        /// provenance in two different formats.</para>
        /// </summary>
        /// <param name="commit">Short commit hash on success; an explanatory sentinel on failure.</param>
        /// <param name="branch">Current branch name, or <c>HEAD</c> when detached.</param>
        /// <param name="dirty"><c>true</c> when the working tree has uncommitted changes.</param>
        /// <returns><c>true</c> when the commit hash was obtained.</returns>
        public static bool TryQueryGit(out string commit, out string branch, out bool dirty)
        {
            branch = "(unknown)";
            dirty = false;

            if (!TryRunGit("rev-parse --short HEAD", out commit)) return false;

            if (TryRunGit("rev-parse --abbrev-ref HEAD", out string branchOutput))
                branch = branchOutput;

            // A non-empty porcelain listing means tracked changes, untracked files, or both. Any of
            // those makes the bare hash an over-claim, so the distinction is not worth splitting.
            dirty = TryRunGit("status --porcelain", out string statusOutput) &&
                    !string.IsNullOrWhiteSpace(statusOutput);

            return true;
        }

        /// <summary>
        /// Runs a git subcommand and captures its trimmed stdout.
        /// </summary>
        /// <param name="arguments">Arguments passed to the <c>git</c> executable.</param>
        /// <param name="output">Trimmed stdout on success; an explanatory sentinel on failure.</param>
        /// <returns><c>true</c> when git exited 0 with non-empty output.</returns>
        private static bool TryRunGit(string arguments, out string output)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git", arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Run from Assets/ — git walks up to the repo root automatically.
                    WorkingDirectory = Application.dataPath,
                };

                using Process proc = Process.Start(psi);
                if (proc == null)
                {
                    output = "(git unavailable)";
                    return false;
                }

                // Read before waiting: a full stdout pipe buffer deadlocks a process that is still
                // writing, which `status --porcelain` can hit on a large dirty tree.
                string stdout = proc.StandardOutput.ReadToEnd();

                if (!proc.WaitForExit(milliseconds: 2000))
                {
                    proc.Kill();
                    output = "(git timeout)";
                    return false;
                }

                if (proc.ExitCode != 0)
                {
                    output = "(not a git repo)";
                    return false;
                }

                output = stdout.Trim();
                if (string.IsNullOrEmpty(output))
                {
                    output = "(empty)";
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                // Most commonly: `git` not on PATH. Fall through to sentinel.
                output = "(git unavailable)";
                return false;
            }
        }
#endif
    }
}
