using System;
using Libraries;

namespace Jobs.Data
{
    /// <summary>
    /// A fully serializable, blittable configuration struct for a <see cref="FastNoiseLite"/> instance.
    /// Factory construction of the actual FastNoiseLite object from this config
    /// must happen on the Main Thread inside StandardChunkGenerator.Initialize().
    /// </summary>
    [Serializable]
    public struct FastNoiseConfig
    {
        /// <summary>Added to the world seed to differentiate noise layers.</summary>
        public int SeedOffset;

        /// <summary>Base frequency for the noise evaluation.</summary>
        public float Frequency;

        /// <summary>The noise algorithm to use.</summary>
        public FastNoiseLite.NoiseType NoiseType;

        /// <summary>3D rotation type. ImproveXZPlanes is recommended for terrain generation.</summary>
        public FastNoiseLite.RotationType3D RotationType3D;

        // Fractal parameters

        /// <summary>Fractal type for layered noise. None for single-pass evaluation.</summary>
        public FastNoiseLite.FractalType FractalType;

        /// <summary>Number of fractal octaves (1-16).</summary>
        public int Octaves;

        /// <summary>Fractal gain per octave.</summary>
        public float Gain;

        /// <summary>Fractal lacunarity (frequency multiplier per octave).</summary>
        public float Lacunarity;

        /// <summary>FBm weighted strength. 0 = standard FBm.</summary>
        public float WeightedStrength;

        /// <summary>Only meaningful when FractalType == PingPong.</summary>
        public float PingPongStrength;

        // Cellular parameters — only meaningful when NoiseType == Cellular

        /// <summary>Distance function for Cellular noise.</summary>
        public FastNoiseLite.CellularDistanceFunction CellularDistanceFunction;

        /// <summary>Return type for Cellular noise.</summary>
        public FastNoiseLite.CellularReturnType CellularReturnType;

        /// <summary>Jitter factor for Cellular noise cell points.</summary>
        public float CellularJitter;
    }
}
