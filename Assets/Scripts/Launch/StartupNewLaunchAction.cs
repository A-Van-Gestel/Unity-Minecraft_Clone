namespace Launch
{
    /// <summary>
    /// <c>-mc-run startup-new</c>: generates the startup-probe world afresh and reports how long the launch takes to reach
    /// a drained pipeline and settled frame time. Leaves the world saved for <c>startup-existing</c> when it quits.
    /// </summary>
    public sealed class StartupNewLaunchAction : ILaunchAction
    {
        /// <inheritdoc />
        public string Name => "startup-new";

        /// <inheritdoc />
        public string Summary => "New-world startup to stable frame time; writes a StartupRun report.";

        /// <inheritdoc />
        public void Start(LaunchArguments arguments) => AutomatedRunLauncher.StartStartupProbe(existingWorld: false);
    }
}
