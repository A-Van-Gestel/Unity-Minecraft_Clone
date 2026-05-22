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
    }
}
