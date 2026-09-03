namespace Spawn
{
    /// <summary>
    /// Where a starting player position comes from. The three cases behave differently in both startup phases
    /// (which position is placed, what the terrain probe is aimed at, and whether the canonical spawn point is
    /// rewritten), so the source is resolved once by <see cref="SpawnResolution.Classify"/> and carried through.
    /// </summary>
    public enum SpawnSource
    {
        /// <summary>A brand-new world: no persisted position exists, so the spawn is the default XZ probed to the surface.</summary>
        Fresh,

        /// <summary>
        /// Hitting Play directly in the World scene on top of a world that already has a save. The persisted spawn
        /// point is honored, but the session is not authoritative over it.
        /// </summary>
        EditorReplay,

        /// <summary>A world opened from an existing save: the player resumes at their persisted position.</summary>
        LoadedSave,
    }
}
