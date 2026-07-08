namespace Editor.Validation.Framework
{
    /// <summary>
    /// Selects the vocabulary <see cref="ValidationSuiteRunner"/> uses when a known-bug scenario changes
    /// state, so the "may be fixed → archive" vs "may be implemented → promote" wording lives in one place
    /// instead of drifting across per-suite copies (the VS-1 finding).
    /// <list type="bullet">
    /// <item><see cref="Bug"/> — the scenario reproduces a documented <i>bug</i>; when it starts passing the
    /// fix is a candidate for in-game confirmation and archival via the archive-fixed-bug workflow. This is
    /// the default (zero) value.</item>
    /// <item><see cref="Unimplemented"/> — the scenario pins a not-yet-<i>implemented</i> behavior; when it
    /// starts passing the feature is a candidate for promotion to a baseline (used by the MeshBuildQueue and
    /// LightWorkScheduler suites).</item>
    /// </list>
    /// </summary>
    public enum KnownBugChannel
    {
        /// <summary>Reproduces a documented bug; a pass means "may be fixed — verify in-game, then archive".</summary>
        Bug = 0,

        /// <summary>Pins an unimplemented behavior; a pass means "may be implemented — verify, then promote to a baseline".</summary>
        Unimplemented,
    }
}
