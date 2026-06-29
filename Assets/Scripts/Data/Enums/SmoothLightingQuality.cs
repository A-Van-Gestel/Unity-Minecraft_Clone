namespace Data.Enums
{
    /// <summary>
    /// Controls the quality level of per-vertex smooth lighting.
    /// Byte-backed for Burst compatibility and blittable job struct usage.
    /// </summary>
    public enum SmoothLightingQuality : byte
    {
        /// <summary>Flat per-block lighting. Classic blocky look, lowest cost.</summary>
        Off = 0,

        /// <summary>Corner-averaged AO with horizontal gradients. Standard smooth lighting.</summary>
        Standard = 1,

        /// <summary>Standard plus vertical gradients on cross meshes (flora). Samples two Y levels.</summary>
        High = 2,
    }
}
