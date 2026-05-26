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

            var standard = _worldType.biomes
                .OfType<StandardBiomeAttributes>()
                .ToArray();

            _biomeAssets = standard;
            _biomeNames = standard.Select(b => b.biomeName).ToArray();

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

                _lastResult = RunAnalysis(_gridSize, _seed, _originX, _originZ, _singleBiomeMode, biome);
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
            bool singleBiomeMode = false, StandardBiomeAttributes biome = null)
        {
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
                cavesOnRunner.FeatureFlags = GenerationFeatureFlags.Default;

                GenerationFeatureFlags noCavesFlags = GenerationFeatureFlags.Default;
                noCavesFlags.EnableCaves = false;
                cavesOffRunner.Initialize(seed, worldType, db, singleBiomeMode, biome);
                cavesOffRunner.FeatureFlags = noCavesFlags;

                int totalChunks = gridSize * gridSize;
                ChunkAnalysisData[] allData = new ChunkAnalysisData[totalChunks];

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

            return RunAnalysis(gridSize, seed, originX, originZ, true, match);
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

            // Build per-column surface height from the caves-off (solid terrain) map
            int[] surfaceY = new int[w * w];
            long surfaceHeightSum = 0;

            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < w; z++)
                {
                    int sy = 0;
                    for (int y = h - 1; y >= 0; y--)
                    {
                        int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
                        ushort blockId = (ushort)(cavesOffMap[idx] & 0xFFFF);
                        if (blockId != BlockIDs.Air)
                        {
                            sy = y;
                            break;
                        }
                    }

                    surfaceY[x * w + z] = sy;
                    surfaceHeightSum += sy;
                }
            }

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

            // Shape quality: classify each cave block by neighbor count
            int tipBlocks = 0;
            int thinBlocks = 0;
            int openBlocks = 0;

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

                        if (airNeighbors <= 1)
                            tipBlocks++;
                        else if (airNeighbors == 2)
                            thinBlocks++;
                        else if (airNeighbors >= 4)
                            openBlocks++;
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
            int chunksWithNoCaves = 0;
            int totalCaveAir = 0;
            int totalUnderground = 0;
            float maxDensity = 0f;
            float minDensity = float.MaxValue;

            int totalPockets = 0;
            int totalTipBlocks = 0;
            int totalThinBlocks = 0;
            int totalOpenBlocks = 0;
            int globalLargestPocket = 0;

            int[] densityBuckets = new int[5];
            float[] perChunkDensities = new float[totalChunks];
            float[] spatialDensity = new float[gridSize * gridSize];

            for (int i = 0; i < totalChunks; i++)
            {
                ChunkStats s = stats[i];
                totalCaveAir += s.CaveAirBlocks;
                totalUnderground += s.TotalUnderground;
                totalPockets += s.PocketCount;
                totalTipBlocks += s.TipBlocks;
                totalThinBlocks += s.ThinBlocks;
                totalOpenBlocks += s.OpenBlocks;

                if (s.LargestPocket > globalLargestPocket)
                    globalLargestPocket = s.LargestPocket;

                float density = s.TotalUnderground > 0 ? (float)s.CaveAirBlocks / s.TotalUnderground : 0f;
                perChunkDensities[i] = density;

                int cx = i / gridSize;
                int cz = i % gridSize;
                spatialDensity[cx * gridSize + cz] = density;

                if (s.CaveAirBlocks == 0) chunksWithNoCaves++;
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

            float avgDensity = totalUnderground > 0 ? (float)totalCaveAir / totalUnderground : 0f;
            float[] sortedDensities = (float[])perChunkDensities.Clone();
            Array.Sort(sortedDensities);
            float medianDensity = sortedDensities[totalChunks / 2];
            float avgPocketsPerChunk = totalChunks > 0 ? (float)totalPockets / totalChunks : 0f;

            // Shape quality percentages (of total cave air)
            float tipPct = totalCaveAir > 0 ? (float)totalTipBlocks / totalCaveAir : 0f;
            float thinPct = totalCaveAir > 0 ? (float)totalThinBlocks / totalCaveAir : 0f;
            float openPct = totalCaveAir > 0 ? (float)totalOpenBlocks / totalCaveAir : 0f;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== CAVE DENSITY ANALYSIS ===");
            sb.AppendLine($"World Type: {worldType.name} | Biome: {biomeName}");
            sb.AppendLine($"Seed: {seed} | Grid: {gridSize}x{gridSize} ({totalChunks} chunks) | Origin: ({originX}, {originZ})");
            sb.AppendLine();

            // --- OVERVIEW ---
            sb.AppendLine("--- OVERVIEW ---");
            sb.AppendLine($"Total underground voxels: {totalUnderground:N0}");
            sb.AppendLine($"Total cave air blocks:   {totalCaveAir:N0}");
            sb.AppendLine($"Overall cave density:    {avgDensity:P2}");
            sb.AppendLine($"Median chunk density:    {medianDensity:P2}");
            sb.AppendLine($"Min chunk density:       {minDensity:P2}");
            sb.AppendLine($"Max chunk density:       {maxDensity:P2}");
            sb.AppendLine($"Chunks with no caves:    {chunksWithNoCaves}/{totalChunks} ({(float)chunksWithNoCaves / totalChunks:P1})");
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
            sb.AppendLine($"Total pockets:         {totalPockets:N0}");
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

                if (globalLargestPocket > 0)
                {
                    float mergeFactor = (float)networkStats.LargestNetwork / globalLargestPocket;
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
            sb.AppendLine($"Tip blocks (0-1 air neighbors):   {totalTipBlocks,6:N0}  ({tipPct:P1})  [artifact indicator]");
            sb.AppendLine($"Thin blocks (2 air neighbors):    {totalThinBlocks,6:N0}  ({thinPct:P1})  [narrow tunnels]");
            sb.AppendLine($"Open blocks (4+ air neighbors):   {totalOpenBlocks,6:N0}  ({openPct:P1})  [explorable caverns]");

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
                    float pct = totalCaveAir > 0 ? (float)count / totalCaveAir * 100f : 0f;
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
