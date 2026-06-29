namespace Data.Enums
{
    /// <summary>
    /// Defines the operational mode of the game engine at runtime.
    /// </summary>
    public enum RuntimeMode
    {
        /// <summary>
        /// Standard gameplay mode with persistence and full player control.
        /// </summary>
        Default,

        /// <summary>
        /// Automated profiling mode with isolated saves and programmatic player movement.
        /// </summary>
        Benchmark,

        /// <summary>
        /// Automated full-world fluid stress pass: isolated saves and programmatic control like
        /// <see cref="Benchmark"/>, but instead of a movement sweep it seeds a deterministic ocean flood in a
        /// real loaded world and captures the per-frame Tick / Apply / Mesh / Lighting attribution (the TG-4 §5
        /// gate). Driven by <c>FluidStressController</c>.
        /// </summary>
        FluidStress,
    }
}
