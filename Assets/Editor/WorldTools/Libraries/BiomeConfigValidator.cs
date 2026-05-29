using System.Collections.Generic;
using Data.WorldTypes;
using Jobs.Data;
using Libraries;
using UnityEngine;

namespace Editor.WorldTools.Libraries
{
    /// <summary>
    /// Severity levels for biome configuration validation warnings.
    /// </summary>
    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>
    /// A single validation result produced by <see cref="BiomeConfigValidator"/>.
    /// </summary>
    public struct BiomeValidationResult
    {
        public ValidationSeverity Severity;
        public string Message;
        public int SubTabIndex;
    }

    /// <summary>
    /// Static validation suite for <see cref="StandardBiomeAttributes"/> configurations.
    /// Detects noise parameter combinations that produce visual artifacts
    /// (steep cliffs, domain warp folds, cave edge cases, etc.) and returns
    /// human-readable warnings for display in the Biome Editor.
    /// </summary>
    public static class BiomeConfigValidator
    {
        private const int SUB_TAB_TERRAIN = 0;
        private const int SUB_TAB_SURFACE = 1;
        private const int SUB_TAB_BLENDING = 2;
        private const int SUB_TAB_CAVES = 3;

        /// <summary>
        /// Runs all validators against the given biome and returns any warnings.
        /// </summary>
        /// <param name="biome">The biome configuration to validate.</param>
        /// <param name="seaLevel">The world's sea level.</param>
        /// <returns>List of validation results, empty if no issues detected.</returns>
        public static List<BiomeValidationResult> Validate(StandardBiomeAttributes biome, int seaLevel)
        {
            List<BiomeValidationResult> results = new List<BiomeValidationResult>();
            if (biome == null) return results;

            ValidateDensityCliffs(biome, results);
            ValidateDomainWarpFolds(biome, results);
            ValidateStrataCoverage(biome, results);
            ValidateSeaLevelOscillation(biome, seaLevel, results);
            ValidateBlendRadius(biome, results);
            ValidateCaveThresholds(biome, results);
            ValidateCaveHeightBounds(biome, results);
            ValidateWormRadiusBounds(biome, results);
            ValidateWormSquashFactor(biome, results);

            return results;
        }

        /// <summary>
        /// Returns only the results that belong to the given sub-tab index.
        /// </summary>
        public static List<BiomeValidationResult> FilterBySubTab(List<BiomeValidationResult> results, int subTabIndex)
        {
            List<BiomeValidationResult> filtered = new List<BiomeValidationResult>();
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].SubTabIndex == subTabIndex)
                    filtered.Add(results[i]);
            }

            return filtered;
        }

        #region Validators

        /// <summary>
        /// Detects when 3D density amplitude × noise gradient produces terrain slopes
        /// steep enough to expose subsurface stone (>2 blocks/column).
        /// </summary>
        private static void ValidateDensityCliffs(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (!biome.enable3DDensity || biome.densityAmplitude <= 0f) return;

            FastNoiseConfig cfg = biome.densityNoiseConfig;
            if (cfg.frequency <= 0f) return;

            // For FBm gradient: each octave doubles frequency (lacunarity=2) and halves amplitude (gain=0.5).
            // Gradient scales with frequency × amplitude, so lacunarity × gain = 1.0 per octave —
            // every octave contributes EQUALLY to the gradient. The sum is simply numOctaves.
            int effectiveOctaves = (cfg.fractalType != FastNoiseLite.FractalType.None && cfg.octaves > 1) ? cfg.octaves : 1;

            // Max gradient per block ≈ frequency × 2π × numOctaves.
            // Multiply by 1.4 to account for 3D isosurface amplification (the terrain surface is a 2D
            // slice through a 3D density field, so effective slopes exceed the raw 1D noise gradient).
            const float ISOSURFACE_FACTOR = 1.4f;
            float maxGradientPerBlock = cfg.frequency * 2f * Mathf.PI * effectiveOctaves * ISOSURFACE_FACTOR;
            float maxSlopeBlocks = maxGradientPerBlock * biome.densityAmplitude;

            if (maxSlopeBlocks > 2.5f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Warning,
                    SubTabIndex = SUB_TAB_TERRAIN,
                    Message = $"Steep terrain likely: density amplitude ({biome.densityAmplitude:F0}) × noise gradient " +
                              $"can produce ~{maxSlopeBlocks:F1} blocks/column slopes, exposing subsurface stone. " +
                              "Reduce amplitude, lower frequency, or drop octaves for gentler terrain.",
                });
            }
            else if (maxSlopeBlocks > 1.5f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Info,
                    SubTabIndex = SUB_TAB_TERRAIN,
                    Message = $"Moderate terrain slopes: density can produce ~{maxSlopeBlocks:F1} blocks/column variation. " +
                              "This may occasionally expose stone on steep faces.",
                });
            }
        }

        /// <summary>
        /// Detects when domain warp amplitude is high enough relative to noise wavelength
        /// to create coordinate space folds (sharp terrain discontinuities).
        /// A fold occurs when the Jacobian determinant of the warped coordinate map approaches zero.
        /// </summary>
        private static void ValidateDomainWarpFolds(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (!biome.enable3DDensity || !biome.enableDensityWarp) return;

            FastNoiseConfig warpCfg = biome.densityWarpConfig;
            if (warpCfg.frequency <= 0f) return;

            // Fold risk: warpAmp × warpFreq × 2π > 1.0 means the Jacobian can go negative
            float foldFactor = warpCfg.domainWarpAmp * warpCfg.frequency * 2f * Mathf.PI;

            if (foldFactor > 1.5f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Warning,
                    SubTabIndex = SUB_TAB_TERRAIN,
                    Message = $"Domain warp fold risk: warp amplitude ({warpCfg.domainWarpAmp:F0}) × frequency ({warpCfg.frequency:F3}) " +
                              $"produces fold factor {foldFactor:F2} (>1.5). This creates sharp terrain creases. " +
                              "Reduce domainWarpAmp or frequency.",
                });
            }
            else if (foldFactor > 0.8f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Info,
                    SubTabIndex = SUB_TAB_TERRAIN,
                    Message = $"Domain warp is moderately aggressive (fold factor {foldFactor:F2}). " +
                              "This produces organic terrain but may create occasional sharp features.",
                });
            }
        }

        /// <summary>
        /// Checks if the total strata (terrain layer) depth is less than the density amplitude,
        /// which guarantees stone will be exposed on slopes steeper than the strata coverage.
        /// </summary>
        private static void ValidateStrataCoverage(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (!biome.enable3DDensity || biome.densityAmplitude <= 0f) return;
            if (biome.terrainLayers == null || biome.terrainLayers.Length == 0)
            {
                if (biome.densityAmplitude > 2f)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Warning,
                        SubTabIndex = SUB_TAB_SURFACE,
                        Message = "No terrain layers defined but 3D density is active. " +
                                  "All subsurface blocks will be stone — slopes will have no grass/dirt cover.",
                    });
                }

                return;
            }

            int totalStrataDepth = 0;
            foreach (StandardTerrainLayer layer in biome.terrainLayers)
                totalStrataDepth += layer.depth;

            if (totalStrataDepth < biome.densityAmplitude * 0.5f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Warning,
                    SubTabIndex = SUB_TAB_SURFACE,
                    Message = $"Strata depth ({totalStrataDepth} blocks) is less than half the density amplitude ({biome.densityAmplitude:F0}). " +
                              "Steep 3D density slopes will expose raw stone. Add deeper terrain layers or reduce density amplitude.",
                });
            }
            else if (totalStrataDepth < biome.densityAmplitude)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Info,
                    SubTabIndex = SUB_TAB_SURFACE,
                    Message = $"Strata depth ({totalStrataDepth}) is less than density amplitude ({biome.densityAmplitude:F0}). " +
                              "The steepest slopes may expose some stone.",
                });
            }
        }

        /// <summary>
        /// Detects when baseTerrainHeight ± densityAmplitude straddles the sea level,
        /// causing chaotic land/water boundaries within the biome.
        /// </summary>
        private static void ValidateSeaLevelOscillation(StandardBiomeAttributes biome, int seaLevel, List<BiomeValidationResult> results)
        {
            if (!biome.enable3DDensity || biome.densityAmplitude <= 0f) return;

            float low = biome.baseTerrainHeight - biome.densityAmplitude;
            float high = biome.baseTerrainHeight + biome.densityAmplitude;

            if (seaLevel > low && seaLevel < high)
            {
                float distFromCenter = Mathf.Abs(biome.baseTerrainHeight - seaLevel);
                float ratio = distFromCenter / biome.densityAmplitude;

                if (ratio < 0.3f)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Warning,
                        SubTabIndex = SUB_TAB_TERRAIN,
                        Message = $"Base height ({biome.baseTerrainHeight:F0}) is very close to sea level ({seaLevel}), " +
                                  $"with density amplitude ±{biome.densityAmplitude:F0}. " +
                                  "This creates chaotic land/water boundaries. Raise base height or reduce amplitude.",
                    });
                }
                else if (ratio < 0.6f)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Info,
                        SubTabIndex = SUB_TAB_TERRAIN,
                        Message = $"Sea level ({seaLevel}) falls within the density band ({low:F0}–{high:F0}). " +
                                  "Some areas will be underwater. This may be intentional for coastal terrain.",
                    });
                }
            }
        }

        /// <summary>
        /// Checks for blend radius values that may produce artifacts:
        /// too small (sharp biome transitions) or unusually large (excessive blending overlap).
        /// </summary>
        private static void ValidateBlendRadius(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (biome.blendRadius < 0.05f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Warning,
                    SubTabIndex = SUB_TAB_BLENDING,
                    Message = $"Blend radius ({biome.blendRadius:F2}) is very small. " +
                              "Biome transitions will be abrupt with visible height steps at boundaries.",
                });
            }

            if (biome.blendWeight < 0.1f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Info,
                    SubTabIndex = SUB_TAB_BLENDING,
                    Message = $"Blend weight ({biome.blendWeight:F2}) is very low. " +
                              "This biome's height will have minimal influence on neighbors, " +
                              "and its borderFade will decay quickly (density turns off near boundaries).",
                });
            }
        }

        /// <summary>
        /// Checks for cave threshold values at extremes that produce degenerate behavior.
        /// </summary>
        private static void ValidateCaveThresholds(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (biome.caveLayers == null) return;

            for (int i = 0; i < biome.caveLayers.Length; i++)
            {
                StandardCaveLayer cave = biome.caveLayers[i];
                if (cave.mode == CaveMode.WormCarver) continue;

                string name = string.IsNullOrEmpty(cave.layerName) ? $"Cave Layer {i}" : cave.layerName;

                if (cave.threshold < 0.05f)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Warning,
                        SubTabIndex = SUB_TAB_CAVES,
                        Message = $"\"{name}\": threshold ({cave.threshold:F2}) is near zero — " +
                                  "almost all terrain in the height range will be carved into air.",
                    });
                }
                else if (cave.threshold > 0.95f)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Info,
                        SubTabIndex = SUB_TAB_CAVES,
                        Message = $"\"{name}\": threshold ({cave.threshold:F2}) is near 1.0 — " +
                                  "this cave layer will produce very few or no caves.",
                    });
                }
            }
        }

        /// <summary>
        /// Checks for cave layers where minHeight >= maxHeight (no valid range).
        /// </summary>
        private static void ValidateCaveHeightBounds(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (biome.caveLayers == null) return;

            for (int i = 0; i < biome.caveLayers.Length; i++)
            {
                StandardCaveLayer cave = biome.caveLayers[i];
                string name = string.IsNullOrEmpty(cave.layerName) ? $"Cave Layer {i}" : cave.layerName;

                if (cave.minHeight >= cave.maxHeight)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Error,
                        SubTabIndex = SUB_TAB_CAVES,
                        Message = $"\"{name}\": minHeight ({cave.minHeight}) >= maxHeight ({cave.maxHeight}) — " +
                                  "this cave layer will never generate.",
                    });
                }

                int range = cave.maxHeight - cave.minHeight;
                if (cave.depthFadeMargin > 0 && cave.depthFadeMargin * 2 >= range)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Warning,
                        SubTabIndex = SUB_TAB_CAVES,
                        Message = $"\"{name}\": depthFadeMargin ({cave.depthFadeMargin}) covers the entire height range ({range} blocks). " +
                                  "The cave will be fully faded everywhere — effectively disabled.",
                    });
                }
            }
        }

        /// <summary>
        /// Checks for Worm Carver layers where radiusMin exceeds radiusMax (inverted wave semantics).
        /// </summary>
        private static void ValidateWormRadiusBounds(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (biome.caveLayers == null) return;

            for (int i = 0; i < biome.caveLayers.Length; i++)
            {
                StandardCaveLayer cave = biome.caveLayers[i];
                if (cave.mode != CaveMode.WormCarver) continue;

                string name = string.IsNullOrEmpty(cave.layerName) ? $"Cave Layer {i}" : cave.layerName;

                if (cave.wormRadiusMin > cave.wormRadiusMax)
                {
                    results.Add(new BiomeValidationResult
                    {
                        Severity = ValidationSeverity.Warning,
                        SubTabIndex = SUB_TAB_CAVES,
                        Message = $"\"{name}\": wormRadiusMin ({cave.wormRadiusMin:F1}) > wormRadiusMax ({cave.wormRadiusMax:F1}) — " +
                                  "the radius wave is inverted (pinch points will be widest).",
                    });
                }
            }
        }

        /// <summary>
        /// Checks for Worm Carver layers with extreme effective squash values.
        /// Accounts for <see cref="WormSquashAxis"/> to validate the post-inversion value the job will use.
        /// </summary>
        private static void ValidateWormSquashFactor(StandardBiomeAttributes biome, List<BiomeValidationResult> results)
        {
            if (biome.caveLayers == null) return;

            for (int i = 0; i < biome.caveLayers.Length; i++)
            {
                StandardCaveLayer cave = biome.caveLayers[i];
                if (cave.mode != CaveMode.WormCarver) continue;

                string name = string.IsNullOrEmpty(cave.layerName) ? $"Cave Layer {i}" : cave.layerName;
                float effective = WormSquashAxisHelper.ToEffectiveSquash(cave.wormSquashAxis, cave.wormSquashFactor);

                ValidateEffectiveSquash(effective, cave.wormSquashAxis, cave.wormSquashFactor, name, SUB_TAB_CAVES, results);
            }
        }

        /// <summary>
        /// Runs all validators against the given trunk worm configuration and returns any warnings.
        /// Call from the World Type editor to validate world-level trunk worm settings.
        /// </summary>
        /// <param name="config">The trunk worm configuration to validate. Null is treated as disabled (no warnings).</param>
        /// <returns>List of validation results, empty if no issues detected.</returns>
        public static List<BiomeValidationResult> ValidateTrunkWormConfig(TrunkWormConfig config)
        {
            List<BiomeValidationResult> results = new List<BiomeValidationResult>();
            if (config == null || !config.enabled) return results;

            if (config.radiusMin > config.radiusMax)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Warning,
                    SubTabIndex = SUB_TAB_CAVES,
                    Message = $"Trunk Worm: radiusMin ({config.radiusMin:F1}) > radiusMax ({config.radiusMax:F1}) — " +
                              "the radius wave is inverted (pinch points will be widest).",
                });
            }

            float effective = WormSquashAxisHelper.ToEffectiveSquash(config.squashAxis, config.squashFactor);
            ValidateEffectiveSquash(effective, config.squashAxis, config.squashFactor, "Trunk Worm", SUB_TAB_CAVES, results);

            return results;
        }

        /// <summary>
        /// Shared validation for effective squash values, used by both per-biome and trunk worm validators.
        /// </summary>
        private static void ValidateEffectiveSquash(float effective, WormSquashAxis axis, float rawValue, string name,
            int subTabIndex, List<BiomeValidationResult> results)
        {
            if (effective < 0.5f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Warning,
                    SubTabIndex = subTabIndex,
                    Message = $"\"{name}\": squash ({rawValue:F2}, axis={axis}) produces very flat, wide tunnels " +
                              "(effective vertical squash {effective:F2}) — may cut through thin terrain layers or cave floors.",
                });
            }
            else if (effective > 2f)
            {
                results.Add(new BiomeValidationResult
                {
                    Severity = ValidationSeverity.Warning,
                    SubTabIndex = subTabIndex,
                    Message = $"\"{name}\": squash ({rawValue:F2}, axis={axis}) produces very tall, narrow fissures " +
                              "(effective vertical squash {effective:F2}) — may punch through terrain ceilings.",
                });
            }
        }

        #endregion
    }
}
