using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Data;
using Data.WorldTypes;
using Editor.DataGeneration;
using Editor.WorldTools.Libraries;
using Helpers;
using Jobs.Data;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace Editor.Dev
{
    /// <summary>Controls how trunk worms are handled during analysis.</summary>
    public enum TrunkWormMode
    {
        /// <summary>Include trunk worms in the analysis (default behavior).</summary>
        Include,

        /// <summary>Exclude trunk worms — only per-biome local cave layers are evaluated.</summary>
        Exclude,

        /// <summary>Trunk worms only — disable all per-biome noise-based layers (Cheese, Noodle, Spaghetti).</summary>
        TrunkOnly,
    }

    /// <summary>
    /// Editor window that generates a grid of chunks and reports cave density, pocket distribution,
    /// and shape quality statistics. Provides both a UI for manual use and a static API for
    /// programmatic invocation via Unity_RunCommand.
    /// </summary>
    public class CaveDensityAnalyzer : EditorWindow
    {
        [SerializeField]
        private int _gridSize = 8;

        [SerializeField]
        private int _seed = 42;

        [SerializeField]
        private int _originX;

        [SerializeField]
        private int _originZ;

        [SerializeField]
        private bool _singleBiomeMode;

        [SerializeField]
        private int _selectedBiomeIndex;

        [SerializeField]
        private int _seedCount = 1;

        [SerializeField]
        private TrunkWormMode _trunkWormMode = TrunkWormMode.Include;

        private string[] _biomeNames;
        private StandardBiomeAttributes[] _biomeAssets;
        private WorldTypeDefinition _worldType;
        private string _lastResult;
        private Vector2 _scrollPos;
        private GUIStyle _monoStyle;

        [MenuItem("Minecraft Clone/Dev/Cave Density Analyzer")]
        private static void ShowWindow()
        {
            CaveDensityAnalyzer window = GetWindow<CaveDensityAnalyzer>("Cave Density Analyzer");
            window.minSize = new Vector2(460, 380);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshBiomeList();
        }

        private void RefreshBiomeList()
        {
            _worldType = FindWorldType();
            if (_worldType?.biomes == null)
            {
                _biomeNames = Array.Empty<string>();
                _biomeAssets = Array.Empty<StandardBiomeAttributes>();
                return;
            }

            // Collect biomes from the WorldType array
            HashSet<StandardBiomeAttributes> seen = new HashSet<StandardBiomeAttributes>();
            List<StandardBiomeAttributes> allBiomes = new List<StandardBiomeAttributes>();
            List<string> allNames = new List<string>();

            foreach (BiomeBase b in _worldType.biomes)
            {
                if (b is StandardBiomeAttributes sba && seen.Add(sba))
                {
                    allBiomes.Add(sba);
                    allNames.Add(sba.biomeName);
                }
            }

            // Discover standalone biome assets not in the WorldType array (L1)
            string[] guids = AssetDatabase.FindAssets("t:StandardBiomeAttributes");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                StandardBiomeAttributes sba = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(path);
                if (sba != null && seen.Add(sba))
                {
                    allBiomes.Add(sba);
                    allNames.Add($"{sba.biomeName} (standalone)");
                }
            }

            _biomeAssets = allBiomes.ToArray();
            _biomeNames = allNames.ToArray();

            if (_selectedBiomeIndex >= _biomeNames.Length)
                _selectedBiomeIndex = 0;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Cave Density Analyzer", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            _gridSize = EditorGUILayout.IntSlider("Grid Size", _gridSize, 2, 32);
            _seed = EditorGUILayout.IntField("Seed", _seed);
            _seedCount = EditorGUILayout.IntSlider("Seed Count", _seedCount, 1, 5);
            _trunkWormMode = (TrunkWormMode)EditorGUILayout.EnumPopup("Trunk Worms", _trunkWormMode);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Origin (chunk coordinates)", EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            _originX = EditorGUILayout.IntField("X", _originX);
            _originZ = EditorGUILayout.IntField("Z", _originZ);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            _singleBiomeMode = EditorGUILayout.Toggle("Single Biome Mode", _singleBiomeMode);

            if (_singleBiomeMode && _biomeNames.Length > 0)
            {
                _selectedBiomeIndex = EditorGUILayout.Popup("Biome", _selectedBiomeIndex, _biomeNames);
            }

            EditorGUILayout.Space(8);

            if (GUILayout.Button("Run Analysis", GUILayout.Height(28)))
            {
                StandardBiomeAttributes biome = _singleBiomeMode && _selectedBiomeIndex < _biomeAssets.Length
                    ? _biomeAssets[_selectedBiomeIndex]
                    : null;

                _lastResult = _seedCount > 1
                    ? RunMultiSeedAnalysis(_gridSize, _seed, _seedCount, _originX, _originZ, _singleBiomeMode, biome, _trunkWormMode)
                    : RunAnalysis(_gridSize, _seed, _originX, _originZ, _singleBiomeMode, biome, _trunkWormMode);
            }

            if (!string.IsNullOrEmpty(_lastResult))
            {
                EditorGUILayout.Space(4);
                _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
                _monoStyle ??= new GUIStyle(EditorStyles.label)
                {
                    font = Font.CreateDynamicFontFromOSFont("Consolas", 11),
                    fontSize = 11,
                    wordWrap = true,
                    richText = false,
                };

                EditorGUILayout.TextArea(_lastResult, _monoStyle);
                EditorGUILayout.EndScrollView();
            }
        }

        // ── Static API for programmatic invocation ──────────────────────────

        /// <summary>
        /// Generates a grid of chunks and returns cave density, pocket, and shape quality statistics.
        /// Also logs the result to the Unity console.
        /// </summary>
        /// <param name="gridSize">Width/depth of the chunk grid to generate.</param>
        /// <param name="seed">World seed for generation.</param>
        /// <param name="originX">Chunk X origin offset.</param>
        /// <param name="originZ">Chunk Z origin offset.</param>
        /// <param name="singleBiomeMode">If true, forces generation to use only the specified biome.</param>
        /// <param name="biome">The biome to use when <paramref name="singleBiomeMode"/> is true.</param>
        /// <returns>Formatted analysis results string.</returns>
        public static string RunAnalysis(int gridSize, int seed, int originX = 0, int originZ = 0,
            bool singleBiomeMode = false, StandardBiomeAttributes biome = null,
            TrunkWormMode trunkMode = TrunkWormMode.Include)
        {
            return RunAnalysisInternal(gridSize, seed, originX, originZ, singleBiomeMode, biome, trunkMode, out _);
        }

        private static string RunAnalysisInternal(int gridSize, int seed, int originX, int originZ,
            bool singleBiomeMode, StandardBiomeAttributes biome, TrunkWormMode trunkMode,
            out SeedMetrics metrics)
        {
            metrics = default;
            WorldTypeDefinition worldType = FindWorldType();
            if (worldType == null)
            {
                const string err = "[CaveDensityAnalyzer] No WorldTypeDefinition with StandardBiomeAttributes found.";
                Debug.LogError(err);
                return err;
            }

            BlockDatabase db = EditorBlockDatabaseCache.Database;
            if (db == null)
            {
                const string err = "[CaveDensityAnalyzer] No BlockDatabase found.";
                Debug.LogError(err);
                return err;
            }

            EditorChunkPipelineRunner cavesOnRunner = new EditorChunkPipelineRunner();
            EditorChunkPipelineRunner cavesOffRunner = new EditorChunkPipelineRunner();
            try
            {
                cavesOnRunner.Initialize(seed, worldType, db, singleBiomeMode, biome);

                GenerationFeatureFlags onFlags = GenerationFeatureFlags.Default;
                if (trunkMode == TrunkWormMode.TrunkOnly)
                {
                    onFlags.EnableCheese = false;
                    onFlags.EnableNoodle = false;
                    onFlags.EnableSpaghetti = false;
                    onFlags.EnableLocalWormCarver = false;
                }

                cavesOnRunner.FeatureFlags = onFlags;
                cavesOnRunner.TrunkWormOverride = trunkMode == TrunkWormMode.Exclude ? false : null;
                cavesOnRunner.EnableTelemetry = true;

                GenerationFeatureFlags noCavesFlags = GenerationFeatureFlags.Default;
                noCavesFlags.EnableCaves = false;
                cavesOffRunner.Initialize(seed, worldType, db, singleBiomeMode, biome);
                cavesOffRunner.FeatureFlags = noCavesFlags;

                int totalChunks = gridSize * gridSize;
                ChunkAnalysisData[] allData = new ChunkAnalysisData[totalChunks];
                List<WormTelemetryEntry> allTelemetry = new List<WormTelemetryEntry>();

                int chunkIdx = 0;
                for (int cx = 0; cx < gridSize; cx++)
                {
                    for (int cz = 0; cz < gridSize; cz++)
                    {
                        EditorUtility.DisplayProgressBar("Cave Density Analyzer",
                            $"Generating chunk ({originX + cx}, {originZ + cz})...",
                            (float)chunkIdx / totalChunks);

                        ChunkCoord coord = new ChunkCoord(originX + cx, originZ + cz);

                        GenerationJobData cavesOn = cavesOnRunner.ScheduleGeneration(coord);
                        GenerationJobData cavesOff = cavesOffRunner.ScheduleGeneration(coord);
                        cavesOn.Handle.Complete();
                        cavesOff.Handle.Complete();

                        allData[chunkIdx] = AnalyzeChunk(cavesOn.Map, cavesOff.Map);

                        // Collect worm telemetry (L5)
                        if (cavesOn.WormTelemetry.IsCreated)
                        {
                            foreach (WormTelemetryEntry entry in cavesOn.WormTelemetry)
                                allTelemetry.Add(entry);
                        }

                        cavesOn.Dispose();
                        cavesOff.Dispose();
                        chunkIdx++;
                    }
                }

                EditorUtility.DisplayProgressBar("Cave Density Analyzer", "Merging cross-chunk networks...", 1f);

                ChunkStats[] allStats = new ChunkStats[totalChunks];
                for (int i = 0; i < totalChunks; i++)
                    allStats[i] = allData[i].Stats;

                GlobalNetworkStats networkStats = MergeAcrossChunks(allData, gridSize);

                EditorUtility.ClearProgressBar();

                string biomeName = singleBiomeMode && biome != null ? biome.biomeName : "All (multi-biome)";
                string result = FormatResults(allStats, gridSize, seed, originX, originZ, biomeName, worldType, networkStats);
                metrics = ComputeMetrics(allStats, networkStats);

                // Per-layer breakdown (L4): run additional passes with specific modes disabled
                string layerBreakdown = RunLayerBreakdown(seed, worldType, db, singleBiomeMode, biome,
                    gridSize, originX, originZ, totalChunks, cavesOffRunner, trunkMode);
                if (!string.IsNullOrEmpty(layerBreakdown))
                    result += layerBreakdown;

                // Worm diagnostics (L5)
                if (allTelemetry.Count > 0)
                    result += FormatWormTelemetry(allTelemetry);

                Debug.Log(result);
                return result;
            }
            finally
            {
                cavesOnRunner.Dispose();
                cavesOffRunner.Dispose();
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Convenience overload accepting a biome name string for programmatic use.
        /// </summary>
        public static string RunAnalysis(int gridSize, int seed, int originX, int originZ,
            string biomeName)
        {
            if (string.IsNullOrEmpty(biomeName))
                return RunAnalysis(gridSize, seed, originX, originZ);

            WorldTypeDefinition worldType = FindWorldType();
            if (worldType?.biomes == null)
            {
                const string err = "[CaveDensityAnalyzer] No WorldTypeDefinition found.";
                Debug.LogError(err);
                return err;
            }

            StandardBiomeAttributes match = null;
            foreach (BiomeBase b in worldType.biomes)
            {
                if (b is StandardBiomeAttributes sba && sba.biomeName.Equals(biomeName, StringComparison.OrdinalIgnoreCase))
                {
                    match = sba;
                    break;
                }
            }

            if (match == null)
            {
                string err = $"[CaveDensityAnalyzer] Biome '{biomeName}' not found. Available: " +
                             string.Join(", ", worldType.biomes.OfType<StandardBiomeAttributes>().Select(b => b.biomeName));
                Debug.LogError(err);
                return err;
            }

            return RunAnalysis(gridSize, seed, originX, originZ, true, match, TrunkWormMode.Include);
        }

        /// <summary>
        /// Runs the analysis across multiple seeds and returns a summary with mean +/- stddev
        /// for key metrics, followed by individual seed results.
        /// </summary>
        /// <param name="gridSize">Width/depth of the chunk grid to generate.</param>
        /// <param name="baseSeed">Starting seed. Seeds used: [baseSeed, baseSeed+1, ..., baseSeed+seedCount-1].</param>
        /// <param name="seedCount">Number of seeds to average over (2-5).</param>
        /// <param name="originX">Chunk X origin offset.</param>
        /// <param name="originZ">Chunk Z origin offset.</param>
        /// <param name="singleBiomeMode">If true, forces generation to use only the specified biome.</param>
        /// <param name="biome">The biome to use when <paramref name="singleBiomeMode"/> is true.</param>
        /// <returns>Formatted multi-seed analysis results string.</returns>
        public static string RunMultiSeedAnalysis(int gridSize, int baseSeed, int seedCount,
            int originX = 0, int originZ = 0,
            bool singleBiomeMode = false, StandardBiomeAttributes biome = null,
            TrunkWormMode trunkMode = TrunkWormMode.Include)
        {
            seedCount = Math.Clamp(seedCount, 2, 5);

            SeedMetrics[] allMetrics = new SeedMetrics[seedCount];
            StringBuilder perSeedResults = new StringBuilder();

            for (int si = 0; si < seedCount; si++)
            {
                int seed = baseSeed + si;
                string result = RunAnalysisInternal(gridSize, seed, originX, originZ,
                    singleBiomeMode, biome, trunkMode, out SeedMetrics seedMetrics);
                perSeedResults.AppendLine($"──── Seed {seed} ────");
                perSeedResults.AppendLine(result);
                allMetrics[si] = seedMetrics;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== MULTI-SEED SUMMARY ===");
            string biomeName = singleBiomeMode && biome != null ? biome.biomeName : "All (multi-biome)";
            sb.AppendLine($"Biome: {biomeName} | Seeds: {baseSeed} to {baseSeed + seedCount - 1} ({seedCount} seeds)");
            sb.AppendLine($"Grid: {gridSize}x{gridSize} | Origin: ({originX}, {originZ})");
            sb.AppendLine();

            sb.AppendLine($"{"Metric",-28} {"Mean",10} {"StdDev",10} {"Min",10} {"Max",10}");
            sb.AppendLine(new string('-', 70));

            AppendMetricRow(sb, "Density %", allMetrics, m => m.Density * 100f);
            AppendMetricRow(sb, "Empty chunks %", allMetrics, m => m.EmptyChunkPct * 100f);
            AppendMetricRow(sb, "Connectivity %", allMetrics, m => m.Connectivity * 100f);
            AppendMetricRow(sb, "Max chunks spanned", allMetrics, m => m.MaxChunksSpanned);
            AppendMetricRow(sb, "Open blocks %", allMetrics, m => m.OpenBlockPct * 100f);
            AppendMetricRow(sb, "Dead-end %", allMetrics, m => m.DeadEndPct * 100f);
            AppendMetricRow(sb, "Junction %", allMetrics, m => m.JunctionPct * 100f);
            AppendMetricRow(sb, "Horizontal %", allMetrics, m => m.HorizontalPct * 100f);
            AppendMetricRow(sb, "Vertical %", allMetrics, m => m.VerticalPct * 100f);

            sb.AppendLine();
            sb.AppendLine(perSeedResults.ToString());

            string final = sb.ToString();
            Debug.Log(final);
            return final;
        }

        private struct SeedMetrics
        {
            public float Density;
            public float EmptyChunkPct;
            public float Connectivity;
            public float MaxChunksSpanned;
            public float OpenBlockPct;
            public float DeadEndPct;
            public float JunctionPct;
            public float HorizontalPct;
            public float VerticalPct;
        }

        /// <summary>
        /// Aggregates raw totals from per-chunk stats. Shared between <see cref="ComputeMetrics"/> and <see cref="FormatResults"/>.
        /// </summary>
        private struct AggregatedTotals
        {
            public int TotalCaveAir;
            public int TotalUnderground;
            public int EmptyChunks;
            public int TotalPockets;
            public int TotalTipBlocks;
            public int TotalThinBlocks;
            public int TotalOpenBlocks;
            public int TotalJunctionBlocks;
            public int TotalHorizontalBlocks;
            public int TotalVerticalBlocks;
            public int TotalMixedBlocks;
            public int GlobalLargestPocket;
        }

        /// <summary>
        /// Single aggregation pass over <see cref="ChunkStats"/> array.
        /// </summary>
        private static AggregatedTotals AggregateTotals(ChunkStats[] stats)
        {
            AggregatedTotals t = default;
            foreach (ChunkStats stat in stats)
            {
                t.TotalCaveAir += stat.CaveAirBlocks;
                t.TotalUnderground += stat.TotalUnderground;
                t.TotalPockets += stat.PocketCount;
                t.TotalTipBlocks += stat.TipBlocks;
                t.TotalThinBlocks += stat.ThinBlocks;
                t.TotalOpenBlocks += stat.OpenBlocks;
                t.TotalJunctionBlocks += stat.JunctionBlocks;
                t.TotalHorizontalBlocks += stat.HorizontalBlocks;
                t.TotalVerticalBlocks += stat.VerticalBlocks;
                t.TotalMixedBlocks += stat.MixedBlocks;
                if (stat.CaveAirBlocks == 0) t.EmptyChunks++;
                if (stat.LargestPocket > t.GlobalLargestPocket) t.GlobalLargestPocket = stat.LargestPocket;
            }

            return t;
        }

        /// <summary>
        /// Computes key metrics directly from analysis data without string parsing.
        /// </summary>
        private static SeedMetrics ComputeMetrics(ChunkStats[] stats, GlobalNetworkStats networkStats)
        {
            AggregatedTotals t = AggregateTotals(stats);

            return new SeedMetrics
            {
                Density = t.TotalUnderground > 0 ? (float)t.TotalCaveAir / t.TotalUnderground : 0f,
                EmptyChunkPct = stats.Length > 0 ? (float)t.EmptyChunks / stats.Length : 0f,
                Connectivity = networkStats.GlobalConnectivityRatio,
                MaxChunksSpanned = networkStats.MaxChunksSpanned,
                OpenBlockPct = t.TotalCaveAir > 0 ? (float)t.TotalOpenBlocks / t.TotalCaveAir : 0f,
                DeadEndPct = t.TotalCaveAir > 0 ? (float)t.TotalTipBlocks / t.TotalCaveAir : 0f,
                JunctionPct = t.TotalCaveAir > 0 ? (float)t.TotalJunctionBlocks / t.TotalCaveAir : 0f,
                HorizontalPct = t.TotalCaveAir > 0 ? (float)t.TotalHorizontalBlocks / t.TotalCaveAir : 0f,
                VerticalPct = t.TotalCaveAir > 0 ? (float)t.TotalVerticalBlocks / t.TotalCaveAir : 0f,
            };
        }

        private static void AppendMetricRow(StringBuilder sb, string name, SeedMetrics[] metrics,
            Func<SeedMetrics, float> selector)
        {
            float[] values = new float[metrics.Length];
            for (int i = 0; i < metrics.Length; i++)
                values[i] = selector(metrics[i]);

            float sum = 0f;
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (float value in values)
            {
                sum += value;
                if (value < min) min = value;
                if (value > max) max = value;
            }

            float mean = sum / values.Length;

            float varianceSum = 0f;
            foreach (float value in values)
            {
                float diff = value - mean;
                varianceSum += diff * diff;
            }

            float stddev = (float)Math.Sqrt(varianceSum / values.Length);

            sb.AppendLine($"{name,-28} {mean,10:F2} {stddev,10:F2} {min,10:F2} {max,10:F2}");
        }

        // ── Worm telemetry formatting (L5) ──────────────────────────────────

        /// <summary>
        /// Formats aggregated worm telemetry into a diagnostic report section.
        /// </summary>
        private static string FormatWormTelemetry(List<WormTelemetryEntry> entries)
        {
            int localCount = 0, trunkCount = 0;
            long localStepsSum = 0, trunkStepsSum = 0;
            int localBranches = 0, trunkBranches = 0;
            int noiseSeekAttempts = 0, noiseSeekSuccesses = 0;
            int maskSeekAttempts = 0, maskSeekSuccesses = 0;
            int termNatural = 0, termBlocked = 0, termFade = 0;

            foreach (WormTelemetryEntry e in entries)
            {
                if (e.IsTrunk)
                {
                    trunkCount++;
                    trunkStepsSum += e.ActualSteps;
                    trunkBranches += e.BranchesSpawned;
                }
                else
                {
                    localCount++;
                    localStepsSum += e.ActualSteps;
                    localBranches += e.BranchesSpawned;
                }

                noiseSeekAttempts += e.NoiseSeekAttempts;
                noiseSeekSuccesses += e.NoiseSeekSuccesses;
                maskSeekAttempts += e.MaskSeekAttempts;
                maskSeekSuccesses += e.MaskSeekSuccesses;

                switch (e.TerminationReason)
                {
                    case WormTelemetryEntry.TERMINATION_NATURAL: termNatural++; break;
                    case WormTelemetryEntry.TERMINATION_TRAVERSAL_BLOCKED: termBlocked++; break;
                    case WormTelemetryEntry.TERMINATION_FADE_COMPLETE: termFade++; break;
                }
            }

            float localAvgSteps = localCount > 0 ? (float)localStepsSum / localCount : 0f;
            float trunkAvgSteps = trunkCount > 0 ? (float)trunkStepsSum / trunkCount : 0f;
            float localAvgBranches = localCount > 0 ? (float)localBranches / localCount : 0f;
            float trunkAvgBranches = trunkCount > 0 ? (float)trunkBranches / trunkCount : 0f;
            float noiseSeekPct = noiseSeekAttempts > 0 ? (float)noiseSeekSuccesses / noiseSeekAttempts : 0f;
            float maskSeekPct = maskSeekAttempts > 0 ? (float)maskSeekSuccesses / maskSeekAttempts : 0f;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- WORM DIAGNOSTICS ---");
            sb.AppendLine($"Worms spawned:       local={localCount}  trunk={trunkCount}");
            sb.AppendLine($"Avg actual length:   local={localAvgSteps:F1}  trunk={trunkAvgSteps:F1}");
            sb.AppendLine($"Avg branches/worm:   local={localAvgBranches:F1}  trunk={trunkAvgBranches:F1}");
            sb.AppendLine($"Noise seek:          attempts={noiseSeekAttempts}  successes={noiseSeekSuccesses} ({noiseSeekPct:P1})");
            sb.AppendLine($"Mask seek:           attempts={maskSeekAttempts}  successes={maskSeekSuccesses} ({maskSeekPct:P1})");
            sb.AppendLine($"Termination:         natural={termNatural}  traversal-blocked={termBlocked}  fade={termFade}");
            sb.AppendLine();
            return sb.ToString();
        }

        // ── Per-layer breakdown (L4) ────────────────────────────────────────

        /// <summary>
        /// Runs generation passes with individual cave layer types disabled to compute
        /// how much cave air each layer type (WormCarver, Cheese, Noodle/Spaghetti) contributes.
        /// </summary>
        private static string RunLayerBreakdown(int seed, WorldTypeDefinition worldType, BlockDatabase db,
            bool singleBiomeMode, StandardBiomeAttributes biome,
            int gridSize, int originX, int originZ, int totalChunks,
            EditorChunkPipelineRunner cavesOffRunner, TrunkWormMode trunkMode)
        {
            bool? trunkOverride = trunkMode == TrunkWormMode.Exclude ? false : null;
            bool includeLocalWorms = trunkMode != TrunkWormMode.TrunkOnly;

            // Build per-layer passes that respect the trunk mode, capturing indices at insertion time
            List<(string label, GenerationFeatureFlags flags)> passes = new List<(string, GenerationFeatureFlags)>();
            int wormPassIndex = passes.Count;
            passes.Add(("Worm Carver", MakeSingleModeFlags(worm: true, localWorm: includeLocalWorms)));
            int cheesePassIndex = -1;
            if (trunkMode != TrunkWormMode.TrunkOnly)
            {
                cheesePassIndex = passes.Count;
                passes.Add(("Cheese", MakeSingleModeFlags(cheese: true)));
                passes.Add(("Noodle/Spaghetti", MakeSingleModeFlags(noodle: true, spaghetti: true)));
            }

            int[] perLayerAir = new int[passes.Count];
            int[] perLayerChunks = new int[passes.Count];

            // Cache caves-off maps and their surface maps to avoid redundant recomputation
            Dictionary<ChunkCoord, NativeArray<uint>> cavesOffCache = new Dictionary<ChunkCoord, NativeArray<uint>>();
            Dictionary<ChunkCoord, int[]> surfaceMapCache = new Dictionary<ChunkCoord, int[]>();
            Dictionary<ChunkCoord, NativeArray<uint>> wormMaps = new Dictionary<ChunkCoord, NativeArray<uint>>();
            Dictionary<ChunkCoord, NativeArray<uint>> cheeseMaps = new Dictionary<ChunkCoord, NativeArray<uint>>();

            // Single runner reused across layer passes (only FeatureFlags changes)
            EditorChunkPipelineRunner runner = new EditorChunkPipelineRunner();
            try
            {
                runner.Initialize(seed, worldType, db, singleBiomeMode, biome);

                for (int pi = 0; pi < passes.Count; pi++)
                {
                    runner.FeatureFlags = passes[pi].flags;
                    runner.TrunkWormOverride = trunkOverride;

                    int chunkIdx = 0;
                    for (int cx = 0; cx < gridSize; cx++)
                    {
                        for (int cz = 0; cz < gridSize; cz++)
                        {
                            EditorUtility.DisplayProgressBar("Cave Density Analyzer",
                                $"Layer breakdown: {passes[pi].label} ({cx * gridSize + cz + 1}/{totalChunks})...",
                                (float)chunkIdx / totalChunks);

                            ChunkCoord coord = new ChunkCoord(originX + cx, originZ + cz);
                            GenerationJobData layerOn = runner.ScheduleGeneration(coord);

                            // Use cached caves-off map and surface map, or generate and cache
                            if (!cavesOffCache.TryGetValue(coord, out NativeArray<uint> cavesOffMap))
                            {
                                GenerationJobData cavesOff = cavesOffRunner.ScheduleGeneration(coord);
                                cavesOff.Handle.Complete();
                                cavesOffMap = new NativeArray<uint>(cavesOff.Map, Allocator.Persistent);
                                cavesOffCache[coord] = cavesOffMap;
                                surfaceMapCache[coord] = BuildSurfaceMap(cavesOffMap);
                                cavesOff.Dispose();
                            }

                            layerOn.Handle.Complete();

                            int air = CountCaveAir(layerOn.Map, cavesOffMap, surfaceMapCache[coord]);
                            perLayerAir[pi] += air;
                            if (air > 0) perLayerChunks[pi]++;

                            // Retain worm/cheese maps for L6 reuse
                            if (pi == wormPassIndex)
                                wormMaps[coord] = new NativeArray<uint>(layerOn.Map, Allocator.Persistent);
                            else if (pi == cheesePassIndex)
                                cheeseMaps[coord] = new NativeArray<uint>(layerOn.Map, Allocator.Persistent);

                            layerOn.Dispose();
                            chunkIdx++;
                        }
                    }
                }

                // Cheese-worm connectivity (L6): reuse retained worm/cheese maps from L4
                int totalCheesePockets = 0;
                int connectedCheesePockets = 0;

                bool hasWormAndCheese = perLayerAir[wormPassIndex] > 0
                                        && cheesePassIndex >= 0 && perLayerAir[cheesePassIndex] > 0;
                if (hasWormAndCheese)
                {
                    for (int cx = 0; cx < gridSize; cx++)
                    {
                        for (int cz = 0; cz < gridSize; cz++)
                        {
                            int ci = cx * gridSize + cz;
                            EditorUtility.DisplayProgressBar("Cave Density Analyzer",
                                $"Cheese-worm connectivity ({ci + 1}/{totalChunks})...",
                                (float)ci / totalChunks);

                            ChunkCoord coord = new ChunkCoord(originX + cx, originZ + cz);

                            AnalyzeCheeseWormConnectivity(
                                cheeseMaps[coord], wormMaps[coord],
                                cavesOffCache[coord], surfaceMapCache[coord],
                                out int pockets, out int connected);
                            totalCheesePockets += pockets;
                            connectedCheesePockets += connected;
                        }
                    }
                }

                int totalLayerAir = 0;
                foreach (int airCount in perLayerAir)
                    totalLayerAir += airCount;

                if (totalLayerAir == 0) return null;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("--- LAYER BREAKDOWN ---");
                for (int i = 0; i < passes.Count; i++)
                {
                    float pct = (float)perLayerAir[i] / totalLayerAir;
                    sb.AppendLine($"  {passes[i].label,-18} {perLayerAir[i],8:N0} blocks ({pct,6:P1}) — {perLayerChunks[i]} chunks affected");
                }

                sb.AppendLine();

                // Cheese-worm connectivity output (L6)
                if (hasWormAndCheese && totalCheesePockets > 0)
                {
                    int isolated = totalCheesePockets - connectedCheesePockets;
                    float connPct = (float)connectedCheesePockets / totalCheesePockets;
                    float isoPct = (float)isolated / totalCheesePockets;

                    sb.AppendLine("--- CHEESE-WORM CONNECTIVITY ---");
                    sb.AppendLine($"Total cheese pockets:           {totalCheesePockets}");
                    sb.AppendLine($"Connected to worm tunnels:      {connectedCheesePockets} ({connPct:P1})");
                    sb.AppendLine($"Isolated (cheese-only):         {isolated} ({isoPct:P1})");
                    sb.AppendLine();
                }

                return sb.ToString();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                runner.Dispose();
                foreach (NativeArray<uint> map in cavesOffCache.Values)
                    if (map.IsCreated)
                        map.Dispose();
                foreach (NativeArray<uint> map in wormMaps.Values)
                    if (map.IsCreated)
                        map.Dispose();
                foreach (NativeArray<uint> map in cheeseMaps.Values)
                    if (map.IsCreated)
                        map.Dispose();
            }
        }

        /// <summary>
        /// Flood fills cheese-only cave air and checks if each pocket overlaps with worm-only cave air.
        /// </summary>
        private static void AnalyzeCheeseWormConnectivity(
            NativeArray<uint> cheeseOnMap, NativeArray<uint> wormOnMap,
            NativeArray<uint> cavesOffMap, int[] surfaceY,
            out int totalPockets, out int connectedPockets)
        {
            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;

            bool[] isCheeseAir = new bool[w * h * w];
            bool[] isWormAir = new bool[w * h * w];

            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int sy = surfaceY[x * w + z];
                    for (int y = 1; y < sy; y++)
                    {
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        ushort offBlock = (ushort)(cavesOffMap[idx] & 0xFFFF);
                        if (offBlock == BlockIDs.Air) continue;

                        if ((ushort)(cheeseOnMap[idx] & 0xFFFF) == BlockIDs.Air)
                            isCheeseAir[idx] = true;
                        if ((ushort)(wormOnMap[idx] & 0xFFFF) == BlockIDs.Air)
                            isWormAir[idx] = true;
                    }
                }
            }

            // Flood fill cheese pockets, check worm overlap per pocket
            bool[] visited = new bool[w * h * w];
            totalPockets = 0;
            connectedPockets = 0;

            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int sy = surfaceY[x * w + z];
                    for (int y = 1; y < sy; y++)
                    {
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        if (!isCheeseAir[idx] || visited[idx]) continue;

                        totalPockets++;
                        bool hasWormOverlap = false;

                        Queue<(int, int, int)> queue = new Queue<(int, int, int)>();
                        visited[idx] = true;
                        queue.Enqueue((x, y, z));

                        while (queue.Count > 0)
                        {
                            (int fx, int fy, int fz) = queue.Dequeue();
                            int fi = ChunkMath.GetFlattenedIndexInChunk(fx, fy, fz);
                            if (isWormAir[fi]) hasWormOverlap = true;

                            TryAdd(fx - 1, fy, fz);
                            TryAdd(fx + 1, fy, fz);
                            TryAdd(fx, fy - 1, fz);
                            TryAdd(fx, fy + 1, fz);
                            TryAdd(fx, fy, fz - 1);
                            TryAdd(fx, fy, fz + 1);
                        }

                        if (hasWormOverlap) connectedPockets++;

                        void TryAdd(int nx, int ny, int nz)
                        {
                            if (nx < 0 || nx >= w || nz < 0 || nz >= w || ny < 1 || ny >= h) return;
                            int ni = ChunkMath.GetFlattenedIndexInChunk(nx, ny, nz);
                            if (!isCheeseAir[ni] || visited[ni]) return;
                            visited[ni] = true;
                            queue.Enqueue((nx, ny, nz));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Creates feature flags with caves enabled but only specific cave modes active.
        /// </summary>
        private static GenerationFeatureFlags MakeSingleModeFlags(
            bool worm = false, bool cheese = false, bool noodle = false, bool spaghetti = false,
            bool localWorm = true)
        {
            GenerationFeatureFlags flags = GenerationFeatureFlags.Default;
            flags.EnableWormCarver = worm;
            flags.EnableCheese = cheese;
            flags.EnableNoodle = noodle;
            flags.EnableSpaghetti = spaghetti;
            flags.EnableLocalWormCarver = localWorm;
            return flags;
        }

        /// <summary>
        /// Builds per-column surface height from a caves-off (solid terrain) map.
        /// Shared by AnalyzeChunk, CountCaveAir, and AnalyzeCheeseWormConnectivity.
        /// </summary>
        private static int[] BuildSurfaceMap(NativeArray<uint> cavesOffMap)
        {
            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;

            int[] surfaceY = new int[w * w];
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    for (int y = h - 1; y >= 0; y--)
                    {
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        if ((ushort)(cavesOffMap[idx] & 0xFFFF) != BlockIDs.Air)
                        {
                            surfaceY[x * w + z] = y;
                            break;
                        }
                    }
                }
            }

            return surfaceY;
        }

        /// <summary>
        /// Counts cave air blocks by comparing caves-on vs caves-off maps (fast path without full analysis).
        /// When <paramref name="precomputedSurfaceY"/> is provided, skips the surface map computation.
        /// </summary>
        private static int CountCaveAir(NativeArray<uint> cavesOnMap, NativeArray<uint> cavesOffMap,
            int[] precomputedSurfaceY = null)
        {
            const int w = VoxelData.ChunkWidth;
            int[] surfaceY = precomputedSurfaceY ?? BuildSurfaceMap(cavesOffMap);

            int count = 0;
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int sy = surfaceY[x * w + z];
                    for (int y = 1; y < sy; y++)
                    {
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        ushort onBlock = (ushort)(cavesOnMap[idx] & 0xFFFF);
                        ushort offBlock = (ushort)(cavesOffMap[idx] & 0xFFFF);
                        if (onBlock == BlockIDs.Air && offBlock != BlockIDs.Air)
                            count++;
                    }
                }
            }

            return count;
        }

        // ── Data structures ─────────────────────────────────────────────────

        private struct ChunkStats
        {
            public int TotalUnderground;
            public int CaveAirBlocks;
            public int AvgSurfaceHeight;

            // Pocket analysis
            public int PocketCount;
            public int LargestPocket;
            public int SmallestPocket;
            public int MedianPocket;

            // Shape quality
            public int ThinBlocks;
            public int TipBlocks;
            public int OpenBlocks;

            // Network topology (L7) — DeadEnd == TipBlocks, Corridor == ThinBlocks (same thresholds)
            public int JunctionBlocks;

            // Tunnel direction (L8)
            public int HorizontalBlocks;
            public int VerticalBlocks;
            public int MixedBlocks;

            // Y-level distribution
            public int[] CaveAirPerY;
        }

        private struct PocketInfo
        {
            public int Size;
            public bool TouchesBoundary;
            public int MinY;
            public int MaxY;
        }

        private struct ChunkAnalysisData
        {
            public ChunkStats Stats;
            public PocketInfo[] Pockets;

            // Boundary pocket IDs (local 1-based; 0 = no cave air)
            // Indexed by y * ChunkWidth + lateral coordinate
            public int[] XNegFace;
            public int[] XPosFace;
            public int[] ZNegFace;
            public int[] ZPosFace;
        }

        private struct GlobalNetworkStats
        {
            public int NetworkCount;
            public int LargestNetwork;
            public int SmallestNetwork;
            public int MedianNetwork;
            public int MaxChunksSpanned;
            public float AvgChunksSpanned;
            public float GlobalConnectivityRatio;
            public int TotalCaveAir;

            // Network vertical extent
            public int MinNetworkYSpan;
            public int MedianNetworkYSpan;
            public int MaxNetworkYSpan;
            public float AvgNetworkYSpan;
            public int LargestNetworkMinY;
            public int LargestNetworkMaxY;

            // Network isolation (nearest-neighbor centroid distance in chunk units)
            public int IsolationNetworkCount;
            public float MinIsolationDist;
            public float MedianIsolationDist;
            public float AvgIsolationDist;

            // Network topology (L7) and tunnel direction (L8) are computed in FormatResults()
            // from per-chunk ChunkStats accumulators — no struct fields needed here.
        }

        private class UnionFind
        {
            private readonly int[] _parent;
            private readonly int[] _rank;

            public UnionFind(int size)
            {
                _parent = new int[size];
                _rank = new int[size];
                for (int i = 0; i < size; i++)
                    _parent[i] = i;
            }

            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }

                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);
                if (ra == rb) return;

                if (_rank[ra] < _rank[rb])
                    _parent[ra] = rb;
                else if (_rank[ra] > _rank[rb])
                    _parent[rb] = ra;
                else
                {
                    _parent[rb] = ra;
                    _rank[ra]++;
                }
            }
        }

        // ── Analysis logic ──────────────────────────────────────────────────

        /// <summary>
        /// Compares caves-on vs caves-off maps to identify true cave blocks,
        /// then runs flood fill for pocket analysis, shape quality scoring, and boundary extraction.
        /// </summary>
        private static ChunkAnalysisData AnalyzeChunk(NativeArray<uint> cavesOnMap, NativeArray<uint> cavesOffMap)
        {
            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;

            int[] surfaceY = BuildSurfaceMap(cavesOffMap);
            long surfaceHeightSum = 0;
            foreach (int yCount in surfaceY)
                surfaceHeightSum += yCount;

            // Identify cave blocks: solid in caves-off, air in caves-on, below surface
            bool[] isCaveAir = new bool[w * h * w];
            int caveAirCount = 0;
            int totalUnderground = 0;
            int[] caveAirPerY = new int[h];

            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int sy = surfaceY[x * w + z];
                    for (int y = 1; y < sy; y++)
                    {
                        totalUnderground++;
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        ushort onBlock = (ushort)(cavesOnMap[idx] & 0xFFFF);
                        ushort offBlock = (ushort)(cavesOffMap[idx] & 0xFFFF);

                        if (onBlock == BlockIDs.Air && offBlock != BlockIDs.Air)
                        {
                            isCaveAir[idx] = true;
                            caveAirCount++;
                            caveAirPerY[y]++;
                        }
                    }
                }
            }

            // Flood fill to find connected pockets
            int[] pocketId = new int[w * h * w];
            List<PocketInfo> pockets = new List<PocketInfo>();
            int nextPocketId = 1;

            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int sy = surfaceY[x * w + z];
                    for (int y = 1; y < sy; y++)
                    {
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        if (!isCaveAir[idx] || pocketId[idx] != 0) continue;

                        PocketInfo info = FloodFill(isCaveAir, pocketId, nextPocketId, x, y, z, surfaceY);
                        pockets.Add(info);
                        nextPocketId++;
                    }
                }
            }

            // Shape quality, topology, and tunnel direction: classify each cave block
            int tipBlocks = 0;
            int thinBlocks = 0;
            int openBlocks = 0;
            int junctionBlocks = 0;
            int horizontalBlocks = 0;
            int verticalBlocks = 0;
            int mixedBlocks = 0;

            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int sy = surfaceY[x * w + z];
                    for (int y = 1; y < sy; y++)
                    {
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        if (!isCaveAir[idx]) continue;

                        int airNeighbors = CountAirNeighbors(isCaveAir, x, y, z);

                        // Shape quality
                        if (airNeighbors <= 1)
                            tipBlocks++;
                        else if (airNeighbors == 2)
                            thinBlocks++;
                        else if (airNeighbors >= 4)
                            openBlocks++;

                        // Topology (L7) — only junctionBlocks is new; dead-end/corridor reuse tip/thin
                        if (airNeighbors >= 3)
                            junctionBlocks++;

                        // Tunnel direction (L8) — normalize by axis count (4 horizontal vs 2 vertical)
                        int hCount = CountHorizontalAirNeighbors(isCaveAir, x, y, z);
                        int vCount = CountVerticalAirNeighbors(isCaveAir, x, y, z);
                        // Compare hCount/4 vs vCount/2 → multiply through to avoid float: hCount*2 vs vCount*4
                        int hNorm = hCount * 2;
                        int vNorm = vCount * 4;
                        if (hNorm > vNorm)
                            horizontalBlocks++;
                        else if (vNorm > hNorm)
                            verticalBlocks++;
                        else
                            mixedBlocks++;
                    }
                }
            }

            // Pocket statistics
            int pocketCount = pockets.Count;
            List<int> pocketSizes = new List<int>(pocketCount);

            for (int i = 0; i < pocketCount; i++)
                pocketSizes.Add(pockets[i].Size);

            pocketSizes.Sort();

            // Extract boundary face pocket IDs for cross-chunk merging
            const int faceSize = h * w;
            int[] xNegFace = new int[faceSize];
            int[] xPosFace = new int[faceSize];
            int[] zNegFace = new int[faceSize];
            int[] zPosFace = new int[faceSize];

            for (int y = 0; y < h; y++)
            {
                for (int z = 0; z < w; z++)
                {
                    int fi = y * w + z;
                    xNegFace[fi] = pocketId[ChunkMath.GetFlattenedIndexInChunk(0, y, z)];
                    xPosFace[fi] = pocketId[ChunkMath.GetFlattenedIndexInChunk(w - 1, y, z)];
                }

                for (int x = 0; x < w; x++)
                {
                    int fi = y * w + x;
                    zNegFace[fi] = pocketId[ChunkMath.GetFlattenedIndexInChunk(x, y, 0)];
                    zPosFace[fi] = pocketId[ChunkMath.GetFlattenedIndexInChunk(x, y, w - 1)];
                }
            }

            return new ChunkAnalysisData
            {
                Stats = new ChunkStats
                {
                    TotalUnderground = totalUnderground,
                    CaveAirBlocks = caveAirCount,
                    AvgSurfaceHeight = (int)(surfaceHeightSum / (w * w)),
                    PocketCount = pocketCount,
                    LargestPocket = pocketCount > 0 ? pocketSizes[pocketCount - 1] : 0,
                    SmallestPocket = pocketCount > 0 ? pocketSizes[0] : 0,
                    MedianPocket = pocketCount > 0 ? pocketSizes[pocketCount / 2] : 0,
                    ThinBlocks = thinBlocks,
                    TipBlocks = tipBlocks,
                    OpenBlocks = openBlocks,
                    JunctionBlocks = junctionBlocks,
                    HorizontalBlocks = horizontalBlocks,
                    VerticalBlocks = verticalBlocks,
                    MixedBlocks = mixedBlocks,
                    CaveAirPerY = caveAirPerY,
                },
                Pockets = pockets.ToArray(),
                XNegFace = xNegFace,
                XPosFace = xPosFace,
                ZNegFace = zNegFace,
                ZPosFace = zPosFace,
            };
        }

        /// <summary>
        /// BFS flood fill from a starting cave air block. Returns pocket size, boundary contact, and Y-range.
        /// </summary>
        private static PocketInfo FloodFill(bool[] isCaveAir, int[] pocketId, int id,
            int startX, int startY, int startZ, int[] surfaceY)
        {
            const int w = VoxelData.ChunkWidth;

            Queue<(int x, int y, int z)> queue = new Queue<(int, int, int)>();
            int startIdx = ChunkMath.GetFlattenedIndexInChunk(startX, startY, startZ);
            pocketId[startIdx] = id;
            queue.Enqueue((startX, startY, startZ));
            int size = 0;
            bool touchesBoundary = false;
            int minY = startY;
            int maxY = startY;

            while (queue.Count > 0)
            {
                (int x, int y, int z) = queue.Dequeue();
                size++;

                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                if (x == 0 || x == w - 1 || z == 0 || z == w - 1)
                    touchesBoundary = true;

                // 6-connected neighbors
                TryEnqueue(x - 1, y, z);
                TryEnqueue(x + 1, y, z);
                TryEnqueue(x, y - 1, z);
                TryEnqueue(x, y + 1, z);
                TryEnqueue(x, y, z - 1);
                TryEnqueue(x, y, z + 1);
            }

            return new PocketInfo
            {
                Size = size,
                TouchesBoundary = touchesBoundary,
                MinY = minY,
                MaxY = maxY,
            };

            void TryEnqueue(int nx, int ny, int nz)
            {
                if (nx < 0 || nx >= w || nz < 0 || nz >= w || ny < 1) return;
                int sy = surfaceY[nx * w + nz];
                if (ny >= sy) return;

                int nIdx = ChunkMath.GetFlattenedIndexInChunk(nx, ny, nz);
                if (!isCaveAir[nIdx] || pocketId[nIdx] != 0) return;

                pocketId[nIdx] = id;
                queue.Enqueue((nx, ny, nz));
            }
        }

        /// <summary>
        /// Counts face-adjacent air neighbors (6-connected) for a cave block.
        /// </summary>
        private static int CountAirNeighbors(bool[] isCaveAir, int x, int y, int z)
        {
            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;
            int count = 0;

            if (x > 0 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x - 1, y, z)]) count++;
            if (x < w - 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x + 1, y, z)]) count++;
            if (y > 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y - 1, z)]) count++;
            if (y < h - 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y + 1, z)]) count++;
            if (z > 0 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y, z - 1)]) count++;
            if (z < w - 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y, z + 1)]) count++;

            return count;
        }

        /// <summary>
        /// Counts horizontal (X/Z axis) air neighbors for tunnel direction classification.
        /// </summary>
        private static int CountHorizontalAirNeighbors(bool[] isCaveAir, int x, int y, int z)
        {
            const int w = VoxelData.ChunkWidth;
            int count = 0;

            if (x > 0 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x - 1, y, z)]) count++;
            if (x < w - 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x + 1, y, z)]) count++;
            if (z > 0 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y, z - 1)]) count++;
            if (z < w - 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y, z + 1)]) count++;

            return count;
        }

        /// <summary>
        /// Counts vertical (Y axis) air neighbors for tunnel direction classification.
        /// </summary>
        private static int CountVerticalAirNeighbors(bool[] isCaveAir, int x, int y, int z)
        {
            const int h = VoxelData.ChunkHeight;
            int count = 0;

            if (y > 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y - 1, z)]) count++;
            if (y < h - 1 && isCaveAir[ChunkMath.GetFlattenedIndexInChunk(x, y + 1, z)]) count++;

            return count;
        }

        // ── Cross-chunk merging ─────────────────────────────────────────────

        /// <summary>
        /// Merges pockets across chunk boundaries using union-find to compute global network statistics.
        /// </summary>
        private static GlobalNetworkStats MergeAcrossChunks(ChunkAnalysisData[] data, int gridSize)
        {
            const int w = VoxelData.ChunkWidth;
            const int h = VoxelData.ChunkHeight;
            const int faceSize = h * w;
            int totalChunks = data.Length;

            // Assign global pocket ID offsets (0-based)
            int[] offsets = new int[totalChunks];
            int totalPockets = 0;
            for (int i = 0; i < totalChunks; i++)
            {
                offsets[i] = totalPockets;
                totalPockets += data[i].Pockets.Length;
            }

            if (totalPockets == 0)
            {
                return new GlobalNetworkStats
                {
                    NetworkCount = 0,
                    LargestNetwork = 0,
                    SmallestNetwork = 0,
                    MedianNetwork = 0,
                    MaxChunksSpanned = 0,
                    AvgChunksSpanned = 0f,
                    GlobalConnectivityRatio = 0f,
                    TotalCaveAir = 0,
                };
            }

            UnionFind uf = new UnionFind(totalPockets);

            // Merge adjacent chunk boundaries
            for (int cx = 0; cx < gridSize; cx++)
            {
                for (int cz = 0; cz < gridSize; cz++)
                {
                    int chunkIdx = cx * gridSize + cz;

                    // +X neighbor
                    if (cx + 1 < gridSize)
                    {
                        int neighborIdx = (cx + 1) * gridSize + cz;
                        MergeFaces(uf, data[chunkIdx].XPosFace, offsets[chunkIdx],
                            data[neighborIdx].XNegFace, offsets[neighborIdx], faceSize);
                    }

                    // +Z neighbor
                    if (cz + 1 < gridSize)
                    {
                        int neighborIdx = cx * gridSize + (cz + 1);
                        MergeFaces(uf, data[chunkIdx].ZPosFace, offsets[chunkIdx],
                            data[neighborIdx].ZNegFace, offsets[neighborIdx], faceSize);
                    }
                }
            }

            // Aggregate per-network stats
            Dictionary<int, int> networkSizes = new Dictionary<int, int>();
            Dictionary<int, HashSet<int>> networkChunks = new Dictionary<int, HashSet<int>>();
            Dictionary<int, int> networkMinY = new Dictionary<int, int>();
            Dictionary<int, int> networkMaxY = new Dictionary<int, int>();
            int totalCaveAir = 0;

            for (int ci = 0; ci < totalChunks; ci++)
            {
                PocketInfo[] pockets = data[ci].Pockets;
                for (int pi = 0; pi < pockets.Length; pi++)
                {
                    int globalId = offsets[ci] + pi;
                    int root = uf.Find(globalId);

                    networkSizes.TryAdd(root, 0);
                    networkChunks.TryAdd(root, new HashSet<int>());
                    networkMinY.TryAdd(root, int.MaxValue);
                    networkMaxY.TryAdd(root, 0);

                    networkSizes[root] += pockets[pi].Size;
                    networkChunks[root].Add(ci);
                    if (pockets[pi].MinY < networkMinY[root]) networkMinY[root] = pockets[pi].MinY;
                    if (pockets[pi].MaxY > networkMaxY[root]) networkMaxY[root] = pockets[pi].MaxY;
                    totalCaveAir += pockets[pi].Size;
                }
            }

            int networkCount = networkSizes.Count;
            List<int> sizes = new List<int>(networkSizes.Values);
            sizes.Sort();

            int largestNetwork = sizes[^1];
            int smallestNetwork = sizes[0];
            int medianNetwork = sizes[sizes.Count / 2];

            int maxChunksSpanned = 0;
            float chunksSpannedSum = 0f;
            foreach (HashSet<int> chunks in networkChunks.Values)
            {
                int count = chunks.Count;
                chunksSpannedSum += count;
                if (count > maxChunksSpanned)
                    maxChunksSpanned = count;
            }

            // Network Y-spans
            List<int> rootList = new List<int>(networkSizes.Keys);
            List<int> ySpans = new List<int>(networkCount);
            int largestRoot = -1;
            int largestSize = 0;

            foreach (int root in rootList)
            {
                ySpans.Add(networkMaxY[root] - networkMinY[root] + 1);
                if (networkSizes[root] > largestSize)
                {
                    largestSize = networkSizes[root];
                    largestRoot = root;
                }
            }

            ySpans.Sort();

            // Network isolation (nearest-neighbor centroid distance in chunk units)
            List<(float cx, float cz)> centroids = new List<(float, float)>(networkCount);
            foreach (int root in rootList)
            {
                float sumCx = 0f, sumCz = 0f;
                HashSet<int> chunks = networkChunks[root];
                foreach (int ci in chunks)
                {
                    sumCx += (float)ci / gridSize;
                    sumCz += ci % gridSize;
                }

                centroids.Add((sumCx / chunks.Count, sumCz / chunks.Count));
            }

            List<float> nearestDistances = new List<float>();
            for (int i = 0; i < centroids.Count; i++)
            {
                float nearest = float.MaxValue;
                for (int j = 0; j < centroids.Count; j++)
                {
                    if (i == j) continue;
                    float dx = centroids[i].cx - centroids[j].cx;
                    float dz = centroids[i].cz - centroids[j].cz;
                    float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (dist < nearest) nearest = dist;
                }

                if (nearest < float.MaxValue)
                    nearestDistances.Add(nearest);
            }

            nearestDistances.Sort();

            float minIso = 0f, medIso = 0f, avgIso = 0f;
            if (nearestDistances.Count > 0)
            {
                minIso = nearestDistances[0];
                medIso = nearestDistances[nearestDistances.Count / 2];
                float sum = 0f;
                foreach (float t in nearestDistances)
                    sum += t;

                avgIso = sum / nearestDistances.Count;
            }

            return new GlobalNetworkStats
            {
                NetworkCount = networkCount,
                LargestNetwork = largestNetwork,
                SmallestNetwork = smallestNetwork,
                MedianNetwork = medianNetwork,
                MaxChunksSpanned = maxChunksSpanned,
                AvgChunksSpanned = networkCount > 0 ? chunksSpannedSum / networkCount : 0f,
                GlobalConnectivityRatio = totalCaveAir > 0 ? (float)largestNetwork / totalCaveAir : 0f,
                TotalCaveAir = totalCaveAir,
                MinNetworkYSpan = ySpans[0],
                MedianNetworkYSpan = ySpans[ySpans.Count / 2],
                MaxNetworkYSpan = ySpans[^1],
                AvgNetworkYSpan = ySpans.Count > 0 ? (float)ySpans.Sum() / ySpans.Count : 0f,
                LargestNetworkMinY = largestRoot >= 0 ? networkMinY[largestRoot] : 0,
                LargestNetworkMaxY = largestRoot >= 0 ? networkMaxY[largestRoot] : 0,
                IsolationNetworkCount = nearestDistances.Count,
                MinIsolationDist = minIso,
                MedianIsolationDist = medIso,
                AvgIsolationDist = avgIso,
            };
        }

        /// <summary>
        /// Merges matching cave air voxels between two adjacent boundary faces via union-find.
        /// Local pocket IDs (1-based) are converted to global IDs using offsets.
        /// </summary>
        private static void MergeFaces(UnionFind uf, int[] faceA, int offsetA,
            int[] faceB, int offsetB, int faceSize)
        {
            for (int i = 0; i < faceSize; i++)
            {
                int localA = faceA[i];
                int localB = faceB[i];
                if (localA > 0 && localB > 0)
                    uf.Union(offsetA + localA - 1, offsetB + localB - 1);
            }
        }

        // ── Formatting ──────────────────────────────────────────────────────

        private static string FormatResults(ChunkStats[] stats, int gridSize, int seed,
            int originX, int originZ, string biomeName, WorldTypeDefinition worldType,
            GlobalNetworkStats networkStats)
        {
            int totalChunks = stats.Length;
            AggregatedTotals agg = AggregateTotals(stats);

            // Per-chunk density distribution (not covered by AggregateTotals)
            float maxDensity = 0f;
            float minDensity = float.MaxValue;
            int[] densityBuckets = new int[5];
            float[] perChunkDensities = new float[totalChunks];
            float[] spatialDensity = new float[gridSize * gridSize];

            for (int i = 0; i < totalChunks; i++)
            {
                float density = stats[i].TotalUnderground > 0
                    ? (float)stats[i].CaveAirBlocks / stats[i].TotalUnderground
                    : 0f;
                perChunkDensities[i] = density;

                int cx = i / gridSize;
                int cz = i % gridSize;
                spatialDensity[cx * gridSize + cz] = density;

                if (density > maxDensity) maxDensity = density;
                if (density < minDensity) minDensity = density;

                int bucket = density switch
                {
                    0f => 0,
                    < 0.02f => 1,
                    < 0.05f => 2,
                    < 0.10f => 3,
                    _ => 4,
                };
                densityBuckets[bucket]++;
            }

            float avgDensity = agg.TotalUnderground > 0 ? (float)agg.TotalCaveAir / agg.TotalUnderground : 0f;
            float[] sortedDensities = (float[])perChunkDensities.Clone();
            Array.Sort(sortedDensities);
            float medianDensity = sortedDensities[totalChunks / 2];
            float avgPocketsPerChunk = totalChunks > 0 ? (float)agg.TotalPockets / totalChunks : 0f;

            // Shape quality percentages (of total cave air)
            float tipPct = agg.TotalCaveAir > 0 ? (float)agg.TotalTipBlocks / agg.TotalCaveAir : 0f;
            float thinPct = agg.TotalCaveAir > 0 ? (float)agg.TotalThinBlocks / agg.TotalCaveAir : 0f;
            float openPct = agg.TotalCaveAir > 0 ? (float)agg.TotalOpenBlocks / agg.TotalCaveAir : 0f;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== CAVE DENSITY ANALYSIS ===");
            sb.AppendLine($"World Type: {worldType.name} | Biome: {biomeName}");
            sb.AppendLine($"Seed: {seed} | Grid: {gridSize}x{gridSize} ({totalChunks} chunks) | Origin: ({originX}, {originZ})");
            sb.AppendLine();

            // --- OVERVIEW ---
            sb.AppendLine("--- OVERVIEW ---");
            sb.AppendLine($"Total underground voxels: {agg.TotalUnderground:N0}");
            sb.AppendLine($"Total cave air blocks:   {agg.TotalCaveAir:N0}");
            sb.AppendLine($"Overall cave density:    {avgDensity:P2}");
            sb.AppendLine($"Median chunk density:    {medianDensity:P2}");
            sb.AppendLine($"Min chunk density:       {minDensity:P2}");
            sb.AppendLine($"Max chunk density:       {maxDensity:P2}");
            sb.AppendLine($"Chunks with no caves:    {agg.EmptyChunks}/{totalChunks} ({(float)agg.EmptyChunks / totalChunks:P1})");
            sb.AppendLine();

            // --- DENSITY DISTRIBUTION ---
            sb.AppendLine("--- DENSITY DISTRIBUTION ---");
            sb.AppendLine($"  0% caves:    {densityBuckets[0],3} ({(float)densityBuckets[0] / totalChunks:P1})");
            sb.AppendLine($"  <2% caves:   {densityBuckets[1],3} ({(float)densityBuckets[1] / totalChunks:P1})");
            sb.AppendLine($"  2-5% caves:  {densityBuckets[2],3} ({(float)densityBuckets[2] / totalChunks:P1})");
            sb.AppendLine($"  5-10% caves: {densityBuckets[3],3} ({(float)densityBuckets[3] / totalChunks:P1})");
            sb.AppendLine($"  >10% caves:  {densityBuckets[4],3} ({(float)densityBuckets[4] / totalChunks:P1})");
            sb.AppendLine();

            // --- POCKET ANALYSIS ---
            int globalSmallestPocket = int.MaxValue;
            int medianPocketSum = 0;
            int chunksWithPockets = 0;
            int avgSurfaceHeightSum = 0;

            for (int i = 0; i < totalChunks; i++)
            {
                avgSurfaceHeightSum += stats[i].AvgSurfaceHeight;
                if (stats[i].PocketCount <= 0) continue;
                chunksWithPockets++;
                medianPocketSum += stats[i].MedianPocket;
                if (stats[i].SmallestPocket < globalSmallestPocket)
                    globalSmallestPocket = stats[i].SmallestPocket;
            }

            int avgSurfaceHeight = totalChunks > 0 ? avgSurfaceHeightSum / totalChunks : 0;
            int avgMedianPocket = chunksWithPockets > 0 ? medianPocketSum / chunksWithPockets : 0;
            if (globalSmallestPocket == int.MaxValue) globalSmallestPocket = 0;

            sb.AppendLine("--- POCKET ANALYSIS ---");
            sb.AppendLine($"Avg surface height:    {avgSurfaceHeight}");
            sb.AppendLine($"Total pockets:         {agg.TotalPockets:N0}");
            sb.AppendLine($"Avg pockets per chunk: {avgPocketsPerChunk:F1}");
            sb.AppendLine($"Smallest pocket:       {globalSmallestPocket:N0} blocks");
            sb.AppendLine($"Avg median pocket:     {avgMedianPocket:N0} blocks");

            // Per-chunk pocket size distribution
            int chunksLargePocket = 0;
            int chunksSmallOnly = 0;
            for (int i = 0; i < totalChunks; i++)
            {
                if (stats[i].LargestPocket >= 100)
                    chunksLargePocket++;
                else if (stats[i].PocketCount > 0 && stats[i].LargestPocket < 20)
                    chunksSmallOnly++;
            }

            sb.AppendLine($"Chunks with pocket >= 100: {chunksLargePocket}/{totalChunks}");
            sb.AppendLine($"Chunks with only small (<20) pockets: {chunksSmallOnly}/{totalChunks}");
            sb.AppendLine();

            // --- CROSS-CHUNK NETWORKS ---
            if (networkStats.NetworkCount > 0)
            {
                sb.AppendLine("--- CROSS-CHUNK NETWORKS ---");
                sb.AppendLine($"Global networks:         {networkStats.NetworkCount:N0}  [pockets merged across chunk boundaries]");
                sb.AppendLine($"Largest network:         {networkStats.LargestNetwork:N0} blocks");
                sb.AppendLine($"Smallest network:        {networkStats.SmallestNetwork:N0} blocks");
                sb.AppendLine($"Median network:          {networkStats.MedianNetwork:N0} blocks");
                sb.AppendLine($"Global connectivity:     {networkStats.GlobalConnectivityRatio:P1}  [largest network / total cave air]");
                sb.AppendLine($"Max chunks spanned:      {networkStats.MaxChunksSpanned}");
                sb.AppendLine($"Avg chunks spanned:      {networkStats.AvgChunksSpanned:F1}");

                if (agg.GlobalLargestPocket > 0)
                {
                    float mergeFactor = (float)networkStats.LargestNetwork / agg.GlobalLargestPocket;
                    sb.AppendLine($"Merge amplification:     {mergeFactor:F1}x  [global largest vs per-chunk largest]");
                }

                sb.AppendLine();

                // Network Y-range
                int largestYSpan = networkStats.LargestNetworkMaxY - networkStats.LargestNetworkMinY + 1;
                sb.AppendLine("--- NETWORK Y-RANGE ---");
                sb.AppendLine($"Largest network Y-range: y={networkStats.LargestNetworkMinY} to y={networkStats.LargestNetworkMaxY} (span {largestYSpan})");
                sb.AppendLine($"Min network Y-span:      {networkStats.MinNetworkYSpan} blocks");
                sb.AppendLine($"Median network Y-span:   {networkStats.MedianNetworkYSpan} blocks");
                sb.AppendLine($"Avg network Y-span:      {networkStats.AvgNetworkYSpan:F1} blocks");
                sb.AppendLine($"Max network Y-span:      {networkStats.MaxNetworkYSpan} blocks");

                string yRangeAssessment;
                if (networkStats.MedianNetworkYSpan >= 40)
                    yRangeAssessment = "DEEP — networks span large vertical ranges, multi-level cave systems";
                else if (networkStats.MedianNetworkYSpan >= 15)
                    yRangeAssessment = "MODERATE — networks have meaningful vertical extent";
                else if (networkStats.MedianNetworkYSpan >= 5)
                    yRangeAssessment = "SHALLOW — networks are mostly horizontal layers";
                else
                    yRangeAssessment = "FLAT — networks are confined to thin Y-bands";

                sb.AppendLine($"Assessment: {yRangeAssessment}");
                sb.AppendLine();

                // Network isolation
                if (networkStats.IsolationNetworkCount >= 2)
                {
                    sb.AppendLine("--- NETWORK ISOLATION ---");
                    sb.AppendLine($"Min nearest-neighbor dist:    {networkStats.MinIsolationDist:F1} chunks");
                    sb.AppendLine($"Median nearest-neighbor dist: {networkStats.MedianIsolationDist:F1} chunks");
                    sb.AppendLine($"Avg nearest-neighbor dist:    {networkStats.AvgIsolationDist:F1} chunks");

                    string isolationAssessment;
                    if (networkStats.MedianIsolationDist >= 4f)
                        isolationAssessment = "WELL-SEPARATED — clear solid rock between cave systems";
                    else if (networkStats.MedianIsolationDist >= 2f)
                        isolationAssessment = "MODERATE — some breathing room between networks";
                    else if (networkStats.MedianIsolationDist >= 1f)
                        isolationAssessment = "CLOSE — networks are near each other, may feel continuous";
                    else
                        isolationAssessment = "CLUSTERED — networks are packed tightly, minimal isolation";

                    sb.AppendLine($"Assessment: {isolationAssessment}");
                    sb.AppendLine();
                }
            }

            // --- SHAPE QUALITY ---
            sb.AppendLine("--- SHAPE QUALITY ---");
            sb.AppendLine($"Tip blocks (0-1 air neighbors):   {agg.TotalTipBlocks,6:N0}  ({tipPct:P1})  [artifact indicator]");
            sb.AppendLine($"Thin blocks (2 air neighbors):    {agg.TotalThinBlocks,6:N0}  ({thinPct:P1})  [narrow tunnels]");
            sb.AppendLine($"Open blocks (4+ air neighbors):   {agg.TotalOpenBlocks,6:N0}  ({openPct:P1})  [explorable caverns]");

            string quality;
            if (tipPct > 0.3f)
                quality = "POOR — heavy artifacting, many isolated tips";
            else if (tipPct > 0.15f)
                quality = "FAIR — noticeable thin spikes, consider smoothing";
            else if (openPct > 0.3f)
                quality = "GOOD — mostly open, explorable spaces";
            else
                quality = "OK — mixed tunnel/cavern profile";

            sb.AppendLine($"Assessment: {quality}");
            sb.AppendLine();

            // --- NETWORK TOPOLOGY ---
            if (agg.TotalCaveAir > 0)
            {
                // Dead-end == tip blocks (0-1 neighbors), corridor == thin blocks (2 neighbors)
                float deadEndPct = tipPct;
                float corridorPct = thinPct;
                float junctionPct = (float)agg.TotalJunctionBlocks / agg.TotalCaveAir;

                sb.AppendLine("--- NETWORK TOPOLOGY ---");
                sb.AppendLine($"Dead-end blocks (0-1 neighbors):  {agg.TotalTipBlocks,6:N0}  ({deadEndPct:P1})  [passage termini]");
                sb.AppendLine($"Corridor blocks (2 neighbors):    {agg.TotalThinBlocks,6:N0}  ({corridorPct:P1})  [linear passages]");
                sb.AppendLine($"Junction blocks (3+ neighbors):   {agg.TotalJunctionBlocks,6:N0}  ({junctionPct:P1})  [decision points]");

                string topoAssessment;
                if (junctionPct > 0.4f)
                    topoAssessment = "OPEN — many decision points, cavern-dominant";
                else if (junctionPct > 0.15f)
                    topoAssessment = "BRANCHING — good mix of tunnels and intersections";
                else if (corridorPct > 0.4f)
                    topoAssessment = "LINEAR — mostly corridors with few branches";
                else if (deadEndPct > 0.3f)
                    topoAssessment = "FRAGMENTED — many dead-ends, exploration feels choppy";
                else
                    topoAssessment = "MIXED — balanced topology";

                sb.AppendLine($"Assessment: {topoAssessment}");
                sb.AppendLine();
            }

            // --- TUNNEL DIRECTION ---
            if (agg.TotalCaveAir > 0)
            {
                float hPct = (float)agg.TotalHorizontalBlocks / agg.TotalCaveAir;
                float vPct = (float)agg.TotalVerticalBlocks / agg.TotalCaveAir;
                float mPct = (float)agg.TotalMixedBlocks / agg.TotalCaveAir;

                sb.AppendLine("--- TUNNEL DIRECTION ---");
                sb.AppendLine($"Horizontal blocks (H > V neighbors): {agg.TotalHorizontalBlocks,6:N0}  ({hPct:P1})");
                sb.AppendLine($"Vertical blocks (V > H neighbors):   {agg.TotalVerticalBlocks,6:N0}  ({vPct:P1})");
                sb.AppendLine($"Mixed blocks (H == V neighbors):     {agg.TotalMixedBlocks,6:N0}  ({mPct:P1})");

                float hvRatio = agg.TotalVerticalBlocks > 0 ? (float)agg.TotalHorizontalBlocks / agg.TotalVerticalBlocks : float.PositiveInfinity;

                string dirAssessment;
                if (hvRatio > 5f)
                    dirAssessment = $"STRONGLY HORIZONTAL — H:V ratio {hvRatio:F1}:1, flat cave network";
                else if (hvRatio > 2f)
                    dirAssessment = $"MOSTLY HORIZONTAL — H:V ratio {hvRatio:F1}:1, occasional vertical sections";
                else if (hvRatio > 0.5f)
                    dirAssessment = $"BALANCED — H:V ratio {hvRatio:F1}:1, multi-level cave systems";
                else if (hvRatio > 0.2f)
                    dirAssessment = $"MOSTLY VERTICAL — H:V ratio {hvRatio:F1}:1, shaft-dominant";
                else
                    dirAssessment = $"STRONGLY VERTICAL — H:V ratio {hvRatio:F1}:1, vertical fissures";

                sb.AppendLine($"Assessment: {dirAssessment}");
                sb.AppendLine();
            }

            // --- Y-LEVEL HISTOGRAM ---
            const int chunkHeight = VoxelData.ChunkHeight;
            int[] globalCaveAirPerY = new int[chunkHeight];
            for (int i = 0; i < totalChunks; i++)
            {
                int[] perY = stats[i].CaveAirPerY;
                if (perY == null) continue;
                for (int y = 0; y < chunkHeight; y++)
                    globalCaveAirPerY[y] += perY[y];
            }

            int peakYCount = 0;
            for (int y = 0; y < chunkHeight; y++)
            {
                if (globalCaveAirPerY[y] > peakYCount)
                    peakYCount = globalCaveAirPerY[y];
            }

            if (peakYCount > 0)
            {
                const int BAR_WIDTH = 40;
                sb.AppendLine("--- Y-LEVEL HISTOGRAM ---");
                sb.AppendLine($"(peak = {peakYCount:N0} blocks at scale {BAR_WIDTH} chars)");

                for (int y = chunkHeight - 1; y >= 1; y--)
                {
                    int count = globalCaveAirPerY[y];
                    if (count == 0 && y > avgSurfaceHeight + 2) continue;

                    int barLen = (int)((float)count / peakYCount * BAR_WIDTH);
                    float pct = agg.TotalCaveAir > 0 ? (float)count / agg.TotalCaveAir * 100f : 0f;
                    sb.Append($"y={y,3} | ");
                    sb.Append(new string('#', barLen).PadRight(BAR_WIDTH));
                    sb.AppendLine($" {count,6:N0} ({pct,5:F1}%)");
                }

                sb.AppendLine();
            }

            // --- PER-CHUNK HEATMAP ---
            if (gridSize <= 16)
            {
                sb.AppendLine("--- PER-CHUNK HEATMAP (density %) ---");
                for (int cz = gridSize - 1; cz >= 0; cz--)
                {
                    sb.Append($"z={originZ + cz,3} | ");
                    for (int cx = 0; cx < gridSize; cx++)
                        sb.Append($"{spatialDensity[cx * gridSize + cz] * 100f,5:F1}% ");
                    sb.AppendLine();
                }

                sb.Append("       ");
                for (int cx = 0; cx < gridSize; cx++)
                    sb.Append($" x={originX + cx,2}  ");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static WorldTypeDefinition FindWorldType()
        {
            string[] guids = AssetDatabase.FindAssets("t:WorldTypeDefinition");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                WorldTypeDefinition wt = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(path);
                if (wt?.biomes == null) continue;

                foreach (BiomeBase b in wt.biomes)
                {
                    if (b is StandardBiomeAttributes)
                        return wt;
                }
            }

            return null;
        }
    }
}
