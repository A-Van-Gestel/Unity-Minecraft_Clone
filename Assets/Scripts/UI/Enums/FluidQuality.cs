namespace UI.Enums
{
    /// <summary>
    /// Controls the visual fidelity of liquid (water and lava) rendering.
    /// Higher tiers enable more expensive shader effects (extra FBM octaves,
    /// dual-phase flow, shore/stream foam, refraction distortion).
    /// </summary>
    public enum FluidQuality
    {
        /// <summary>Minimal liquid rendering. Single flow phase, reduced FBM octaves, no foam or refraction distortion.</summary>
        Low,

        /// <summary>Balanced liquid rendering. Dual flow phase, moderate FBM octaves, shore foam, refraction distortion.</summary>
        Medium,

        /// <summary>Full liquid rendering. Dual flow phase, maximum FBM octaves, shore + stream foam, refraction distortion.</summary>
        High,
    }
}
