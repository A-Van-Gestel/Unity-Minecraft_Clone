using Jobs.Data;
using Libraries;

namespace Jobs.Generators
{
    /// <summary>
    /// Centralized factory for creating <see cref="FastNoiseLite"/> instances from <see cref="FastNoiseConfig"/>.
    /// Shared between runtime generators and editor preview tools.
    /// </summary>
    public static class FastNoiseFactory
    {
        /// <summary>
        /// Creates and configures a <see cref="FastNoiseLite"/> instance from a configuration struct.
        /// </summary>
        /// <param name="config">The noise configuration.</param>
        /// <param name="baseSeed">The base seed to apply the config's offset to.</param>
        /// <returns>A fully configured noise instance.</returns>
        public static FastNoiseLite CreateNoiseFromConfig(FastNoiseConfig config, int baseSeed)
        {
            FastNoiseLite noise = FastNoiseLite.Create(baseSeed + config.seedOffset);
            noise.SetFrequency(config.frequency);
            noise.SetNoiseType(config.noiseType);
            noise.SetRotationType3D(config.rotationType3D);
            noise.SetFractalType(config.fractalType);
            noise.SetFractalOctaves(config.octaves);
            noise.SetFractalGain(config.gain);
            noise.SetFractalLacunarity(config.lacunarity);
            noise.SetFractalWeightedStrength(config.weightedStrength);
            noise.SetFractalPingPongStrength(config.pingPongStrength);
            noise.SetCellularDistanceFunction(config.cellularDistanceFunction);
            noise.SetCellularReturnType(config.cellularReturnType);
            noise.SetCellularJitter(config.cellularJitter);
            noise.SetNormalizeToZeroOne(config.normalizeToZeroOne);
            noise.SetDomainWarpType(config.domainWarpType);
            noise.SetDomainWarpAmp(config.domainWarpAmp);
            return noise;
        }
    }
}
