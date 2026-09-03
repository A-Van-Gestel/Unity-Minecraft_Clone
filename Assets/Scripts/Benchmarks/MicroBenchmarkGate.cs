using Data;

namespace Benchmarks
{
    /// <summary>
    /// Single source of truth for whether the in-world micro-benchmark harnesses
    /// (<see cref="ChunkGenerationBenchmark"/>, <see cref="MeshGenerationBenchmark"/>,
    /// <see cref="LightingJobBenchmark"/>) are allowed to arm themselves.
    /// <para>
    /// These harnesses ship inside the World scene, so without a gate a release build would leave their
    /// trigger keys live for any player. Centralizing the rule here keeps the three from drifting apart —
    /// in particular the automated-capture exclusion, which is easy to add to one harness and forget in
    /// the others.
    /// </para>
    /// </summary>
    public static class MicroBenchmarkGate
    {
        /// <summary>
        /// Whether the micro-benchmark harnesses may arm. Read once per harness in <c>Start()</c>, so a
        /// settings change lands on the next world load rather than mid-session.
        /// </summary>
        /// <returns>
        /// <c>true</c> only in interactive play with <see cref="Settings.enableInWorldMicroBenchmarks"/> on.
        /// Always <c>false</c> under an automated harness: the benchmark and fluid-stress captures drive
        /// this same scene, and a micro-benchmark firing mid-run would corrupt that capture's numbers.
        /// </returns>
        public static bool IsArmed()
        {
            return !WorldLaunchState.IsAutomatedMode
                   && SettingsManager.LoadSettings().enableInWorldMicroBenchmarks;
        }
    }
}
