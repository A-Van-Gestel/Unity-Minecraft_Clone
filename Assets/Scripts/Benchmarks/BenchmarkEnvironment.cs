using System;
using System.Text;
using Unity.Burst;
using UnityEngine;
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

            // Application.buildGUID is the all-zeros sentinel in Editor mode and meaningful only in Player builds.
            string buildGUID = Application.buildGUID;
            if (!string.IsNullOrEmpty(buildGUID) && buildGUID != "00000000000000000000000000000000")
                sb.AppendLine($"Build GUID:     {buildGUID}");

            sb.AppendLine($"Git commit:     {GetGitCommitHash()}");
            sb.AppendLine();

            sb.AppendLine("=== Burst ===");
            BurstCompilerOptions options = BurstCompiler.Options;
            sb.AppendLine($"Compilation:    {OnOff(options.EnableBurstCompilation)}");
            sb.AppendLine($"Safety checks:  {OnOff(options.EnableBurstSafetyChecks)}");
            sb.AppendLine($"Synchronous:    {OnOff(options.EnableBurstCompileSynchronously)}");
            sb.AppendLine();

            return sb.ToString();
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

        /// <summary>
        /// Shells out to <c>git rev-parse --short HEAD</c> to obtain the current commit hash.
        /// Editor-only — player builds have no shell access and return a sentinel string.
        /// </summary>
        /// <returns>The 7-character short commit hash, or a sentinel describing why it could not be obtained.</returns>
        private static string GetGitCommitHash()
        {
#if UNITY_EDITOR
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Run from Assets/ — git walks up to the repo root automatically.
                    WorkingDirectory = Application.dataPath,
                };

                using Process proc = Process.Start(psi);
                if (proc == null) return "(git unavailable)";

                if (!proc.WaitForExit(milliseconds: 2000))
                {
                    proc.Kill();
                    return "(git timeout)";
                }

                if (proc.ExitCode != 0) return "(not a git repo)";

                string output = proc.StandardOutput.ReadToEnd().Trim();
                return string.IsNullOrEmpty(output) ? "(empty)" : output;
            }
            catch (Exception)
            {
                // Most commonly: `git` not on PATH. Fall through to sentinel.
                return "(git unavailable)";
            }
#else
            return "(player build — record manually)";
#endif
        }
    }
}
