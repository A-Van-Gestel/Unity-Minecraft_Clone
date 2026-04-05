using System;
using Libraries;
using MyBox;
using UnityEngine;

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
        [Separator("Base Settings")]
        [Tooltip("Added to the world seed to differentiate noise layers.")]
        public int seedOffset;

        /// <summary>Base frequency for the noise evaluation.</summary>
        [Tooltip("Base frequency for the noise evaluation.")]
        public float frequency;

        /// <summary>The noise algorithm to use.</summary>
        [Tooltip("The noise algorithm to use.")]
        public FastNoiseLite.NoiseType noiseType;

        /// <summary>3D rotation type. ImproveXZPlanes is recommended for terrain generation.</summary>
        [Tooltip("3D rotation type. ImproveXZPlanes is recommended for terrain generation.")]
        public FastNoiseLite.RotationType3D rotationType3D;

        /// <summary>Fractal type for layered noise. None for single-pass evaluation.</summary>
        [Separator("Fractal Settings")]
        [Tooltip("Fractal type for layered noise. None for single-pass evaluation.")]
        public FastNoiseLite.FractalType fractalType;

        /// <summary>Number of fractal octaves (1-16).</summary>
        [ConditionalField(nameof(fractalType), true, FastNoiseLite.FractalType.None)]
        [Tooltip("Number of fractal octaves (1-16).")]
        public int octaves;

        /// <summary>Fractal gain per octave.</summary>
        [ConditionalField(nameof(fractalType), true, FastNoiseLite.FractalType.None)]
        [Tooltip("Fractal gain per octave.")]
        public float gain;

        /// <summary>Fractal lacunarity (frequency multiplier per octave).</summary>
        [ConditionalField(nameof(fractalType), true, FastNoiseLite.FractalType.None)]
        [Tooltip("Fractal lacunarity (frequency multiplier per octave).")]
        public float lacunarity;

        /// <summary>FBm weighted strength. 0 = standard FBm.</summary>
        [ConditionalField(nameof(fractalType), true, FastNoiseLite.FractalType.None)]
        [Tooltip("FBm weighted strength. 0 = standard FBm.")]
        public float weightedStrength;

        /// <summary>Only meaningful when FractalType == PingPong.</summary>
        [ConditionalField(nameof(fractalType), false, FastNoiseLite.FractalType.PingPong)]
        [Tooltip("Only meaningful when FractalType == PingPong.")]
        public float pingPongStrength;

        /// <summary>Distance function for Cellular noise.</summary>
        [Separator("Cellular Settings")]
        [ConditionalField(nameof(noiseType), false, FastNoiseLite.NoiseType.Cellular)]
        [Tooltip("Distance function for Cellular noise.")]
        public FastNoiseLite.CellularDistanceFunction cellularDistanceFunction;

        /// <summary>Return type for Cellular noise.</summary>
        [ConditionalField(nameof(noiseType), false, FastNoiseLite.NoiseType.Cellular)]
        [Tooltip("Return type for Cellular noise.")]
        public FastNoiseLite.CellularReturnType cellularReturnType;

        /// <summary>Jitter factor for Cellular noise cell points.</summary>
        [ConditionalField(nameof(noiseType), false, FastNoiseLite.NoiseType.Cellular)]
        [Tooltip("Jitter factor for Cellular noise cell points.")]
        public float cellularJitter;

        /// <summary>When true, remaps GetNoise output from [-1, 1] to [0, 1].</summary>
        [Separator("Output Settings")]
        [OverrideLabel("Normalize to [0, 1]")]
        [Tooltip("When true, remaps GetNoise output from [-1, 1] to [0, 1].")]
        public bool normalizeToZeroOne;
    }
}
