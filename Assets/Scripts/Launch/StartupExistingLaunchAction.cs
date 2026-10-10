namespace Launch
{
    /// <summary>
    /// <c>-mc-run startup-existing</c>: loads the startup-probe world a <c>startup-new</c> run saved and reports how long
    /// the launch takes to reach a drained pipeline and settled frame time. Fails the run when no saved world exists.
    /// </summary>
    public sealed class StartupExistingLaunchAction : ILaunchAction
    {
        /// <inheritdoc />
        public string Name => "startup-existing";

        /// <inheritdoc />
        public string Summary => "Saved-world startup to stable frame time; writes a StartupRun report.";

        /// <inheritdoc />
        public void Start(LaunchArguments arguments) => AutomatedRunLauncher.StartStartupProbe(existingWorld: true);
    }
}
