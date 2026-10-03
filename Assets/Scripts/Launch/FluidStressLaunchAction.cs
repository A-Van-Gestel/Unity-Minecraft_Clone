namespace Launch
{
    /// <summary><c>-mc-run fluidstress</c>: the full-world fluid stress pass.</summary>
    public sealed class FluidStressLaunchAction : ILaunchAction
    {
        /// <inheritdoc />
        public string Name => "fluidstress";

        /// <inheritdoc />
        public string Summary => "Full-world ocean flood; writes a fluid stress report.";

        /// <inheritdoc />
        public void Start(LaunchArguments arguments) => AutomatedRunLauncher.StartFluidStress();
    }
}
