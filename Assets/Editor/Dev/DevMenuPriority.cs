namespace Editor.Dev
{
    /// <summary>
    /// Priorities that group the <c>Minecraft Clone/Dev</c> menu into sections. Unity draws a separator
    /// wherever two adjacent items differ by 11 or more, so the gaps between these values are what produce
    /// the visible sectioning; items sharing a value stay together, sorted by name.
    /// </summary>
    /// <remarks>
    /// Only ordering changes — every menu path stays exactly as it was, which matters because the suite
    /// paths are cited throughout <c>Documentation/</c> and the agent skills. Add a new Dev menu item with
    /// whichever value matches its section rather than a bare number, so the sections cannot drift apart.
    /// </remarks>
    public static class DevMenuPriority
    {
        /// <summary>The aggregate "Validate All" entry, alone at the top of the menu.</summary>
        public const int Aggregate = 10;

        /// <summary>The standard validation suites — the ones that run in seconds and gate a change.</summary>
        public const int Validation = 100;

        /// <summary>Deep fuzz sweeps and nightly variants: minutes to run, not part of a normal pass.</summary>
        public const int DeepValidation = 200;

        /// <summary>Analyzers, probes and diagnostic dumps that report on the engine rather than assert.</summary>
        public const int Diagnostics = 300;

        /// <summary>Tools that write to project assets.</summary>
        public const int AssetTools = 400;
    }
}
