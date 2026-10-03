namespace Launch
{
    /// <summary>
    /// <c>-mc-run benchmark</c>: the benchmark route (generation, ensure-generated and loading passes), configured
    /// by the <c>benchmark*</c> settings — e.g. <c>-mc-set benchmarkGenerationSpeeds=200</c>.
    /// </summary>
    public sealed class BenchmarkLaunchAction : ILaunchAction
    {
        /// <inheritdoc />
        public string Name => "benchmark";

        /// <inheritdoc />
        public string Summary => "Benchmark route flight; writes a BenchmarkRun report.";

        /// <inheritdoc />
        public void Start(LaunchArguments arguments) => AutomatedRunLauncher.StartBenchmark();
    }
}
