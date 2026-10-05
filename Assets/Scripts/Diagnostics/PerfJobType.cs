namespace Diagnostics
{
    /// <summary>The worker job types the performance monitor times; values index per-type arrays.</summary>
    public enum PerfJobType : byte
    {
        /// <summary>Terrain generation, every job in its chain.</summary>
        Generation,

        /// <summary>The neighborhood lighting job.</summary>
        Lighting,

        /// <summary>The mesh job and its post-process.</summary>
        Meshing,

        /// <summary>One chunk's fluid simulation tick.</summary>
        Fluids,

        /// <summary>The scan for flowing fluid around the listener that places water and lava sounds.</summary>
        FluidSoundScan,

        /// <summary>Number of job types; not a job type.</summary>
        Count,
    }
}
