using Helpers;
using Jobs.Helpers;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Editor.Validation.CloudRender
{
    public static partial class CloudRenderValidationSuite
    {
        /// <summary>Burst may fuse multiply-adds the main thread rounds separately; positions differ by far less.</summary>
        private const float GEOMETRY_TOLERANCE = 1e-5f;

        private const int TILE_SIZE = Clouds.CloudTileSize;
        private const int PATTERN_WIDTH = 512;

        // Any offset proves job/main-thread equivalence; this is the inspector default.
        private const float DEPTH_OFFSET = 0.0035f;

        private static readonly uint[] s_seeds = { 0u, 12345u };

        /// <summary>The two default layers' noise inputs (a dense low layer and a sparse salted upper one).</summary>
        private static readonly CloudPatternSettings[] s_layerInputs =
        {
            new CloudPatternSettings { Width = PATTERN_WIDTH, BasePeriodCells = 32, Octaves = 4, Persistence = 0.6f, Coverage = 0.23f },
            new CloudPatternSettings { Width = PATTERN_WIDTH, BasePeriodCells = 16, Octaves = 4, Persistence = 0.6f, Coverage = 0.12f, Seed = 1u },
        };

        private static bool RunB7PrebuiltPatternMatches()
        {
            bool ok = true;
            foreach (uint seed in s_seeds)
            {
                foreach (CloudPatternSettings layer in s_layerInputs)
                {
                    CloudPatternSettings settings = WithSeed(layer, seed);
                    NativeArray<bool> expected = BuildReferencePattern(settings, out int expectedSet);
                    CloudLayerPrebuild prebuild = CloudLayerPrebuild.Schedule(settings, false, TILE_SIZE, DEPTH_OFFSET);
                    NativeArray<bool> actual = prebuild.TakePattern();
                    try
                    {
                        int mismatches = 0;
                        for (int i = 0; i < expected.Length; i++)
                        {
                            if (expected[i] != actual[i]) mismatches++;
                        }

                        ok &= Check($"Seed {settings.Seed}, period {settings.BasePeriodCells}: {expectedSet} cloud cells, prebuild {prebuild.SetCount}, {mismatches} cells differ",
                            expectedSet > 0 && expectedSet < expected.Length && prebuild.SetCount == expectedSet
                            && actual.Length == expected.Length && mismatches == 0);
                    }
                    finally
                    {
                        actual.Dispose();
                        expected.Dispose();
                        prebuild.Dispose();
                    }
                }
            }

            return ok;
        }

        private static bool RunB8PrebuiltMeshesMatch()
        {
            bool ok = true;
            foreach (uint seed in s_seeds)
            {
                foreach (CloudPatternSettings layer in s_layerInputs)
                {
                    CloudPatternSettings settings = WithSeed(layer, seed);
                    NativeArray<bool> pattern = BuildReferencePattern(settings, out _);
                    try
                    {
                        ok &= CheckTiles(settings, pattern, false);
                        ok &= CheckTiles(settings, pattern, true);
                    }
                    finally
                    {
                        pattern.Dispose();
                    }
                }
            }

            return ok;
        }

        /// <summary>Compares every prebuilt tile of one layer and style against the main-thread build.</summary>
        private static bool CheckTiles(CloudPatternSettings settings, NativeArray<bool> pattern, bool fancy)
        {
            CloudLayerPrebuild prebuild = CloudLayerPrebuild.Schedule(settings, fancy, TILE_SIZE, DEPTH_OFFSET);
            NativeList<float3> vertices = new NativeList<float3>(Allocator.Temp);
            NativeList<float3> normals = new NativeList<float3>(Allocator.Temp);
            NativeList<int> triangles = new NativeList<int>(Allocator.Temp);
            NativeArray<float3> cornerNormals = new NativeArray<float3>(
                CloudTileMesher.FancyCornerScratchLength(TILE_SIZE), Allocator.Temp);
            NativeList<int3> faces = new NativeList<int3>(Allocator.Temp);
            int nonEmpty = 0, mismatchedTiles = 0;
            float worstError = 0f;
            try
            {
                for (int i = 0; i < prebuild.TileCount; i++)
                {
                    prebuild.GetTile(i, out int originX, out int originZ,
                        out NativeArray<float3> actualVertices, out NativeArray<float3> actualNormals, out NativeArray<int> actualTriangles);
                    if (fancy)
                    {
                        CloudTileMesher.BuildFancyTile(pattern, settings.Width, originX, originZ, TILE_SIZE, DEPTH_OFFSET,
                            cornerNormals, faces, vertices, normals, triangles);
                    }
                    else
                    {
                        CloudTileMesher.BuildFastTile(pattern, settings.Width, originX, originZ, TILE_SIZE, DEPTH_OFFSET,
                            vertices, normals, triangles);
                    }

                    if (vertices.Length > 0) nonEmpty++;
                    if (!SameGeometry(vertices.AsArray(), normals.AsArray(), triangles.AsArray(),
                            actualVertices, actualNormals, actualTriangles, ref worstError))
                    {
                        mismatchedTiles++;
                    }
                }

                string style = fancy ? "Fancy" : "Fast";
                int expectedTiles = (settings.Width / TILE_SIZE) * (settings.Width / TILE_SIZE);
                return Check($"Seed {settings.Seed}, period {settings.BasePeriodCells}, {style}: {prebuild.TileCount} tiles, " +
                             $"{nonEmpty} with geometry, {mismatchedTiles} differ, worst error {worstError:E1}",
                    prebuild.TileCount == expectedTiles && nonEmpty > 0 && mismatchedTiles == 0);
            }
            finally
            {
                vertices.Dispose();
                normals.Dispose();
                triangles.Dispose();
                cornerNormals.Dispose();
                faces.Dispose();
                prebuild.Dispose();
            }
        }

        /// <summary>Counts and indices exact, positions and normals within <see cref="GEOMETRY_TOLERANCE"/>.</summary>
        private static bool SameGeometry(
            NativeArray<float3> expectedVertices, NativeArray<float3> expectedNormals, NativeArray<int> expectedTriangles,
            NativeArray<float3> actualVertices, NativeArray<float3> actualNormals, NativeArray<int> actualTriangles,
            ref float worstError)
        {
            if (expectedVertices.Length != actualVertices.Length || expectedNormals.Length != actualNormals.Length
                || expectedTriangles.Length != actualTriangles.Length)
            {
                return false;
            }

            for (int i = 0; i < expectedTriangles.Length; i++)
            {
                if (expectedTriangles[i] != actualTriangles[i]) return false;
            }

            bool within = true;
            for (int i = 0; i < expectedVertices.Length; i++)
            {
                float error = math.max(math.cmax(math.abs(expectedVertices[i] - actualVertices[i])),
                    math.cmax(math.abs(expectedNormals[i] - actualNormals[i])));
                worstError = math.max(worstError, error);
                if (error > GEOMETRY_TOLERANCE) within = false;
            }

            return within;
        }

        /// <summary>The pattern built the main-thread way: the noise job, then the threshold and fill inline.</summary>
        private static NativeArray<bool> BuildReferencePattern(CloudPatternSettings settings, out int set)
        {
            int cells = settings.Width * settings.Width;
            NativeArray<float> density = new NativeArray<float>(cells, Allocator.TempJob);
            NativeArray<bool> pattern = new NativeArray<bool>(cells, Allocator.TempJob);
            try
            {
                settings.ToNoiseJob(density).Schedule(cells, settings.Width).Complete();
                float threshold = CloudTileMesher.ComputeCoverageThreshold(density, settings.Coverage);
                set = CloudTileMesher.FillPattern(density, threshold, pattern);
            }
            finally
            {
                density.Dispose();
            }

            return pattern;
        }

        private static CloudPatternSettings WithSeed(CloudPatternSettings layer, uint worldSeed)
        {
            layer.Seed ^= worldSeed;
            return layer;
        }

        private const ulong FNV_OFFSET = 14695981039346656037UL;
        private const ulong FNV_PRIME = 1099511628211UL;
        private const int BYTES_PER_LONG = 8;
        private const int BITS_PER_BYTE = 8;
        private const long EMPTY_TILE_MARK = -1;

        /// <summary>
        /// Golden FNV-1a digests of the shipped cloud geometry, per seed × layer × style: the pattern bits x-major, then
        /// per tile (x-major) the counts, indices and position/normal bits. An intended change to the cloud geometry, or a compiler change to the noise's float
        /// rounding, re-records them.
        /// </summary>
        private static readonly (uint seed, int layer, bool fancy, ulong pattern, ulong meshes)[] s_goldenDigests =
        {
            (0u, 0, false, 0xc8543ebccc795a24UL, 0xa28a56f18a59ce98UL),
            (0u, 0, true, 0xc8543ebccc795a24UL, 0xe9a417bce109acdcUL),
            (0u, 1, false, 0x3c74eca650080064UL, 0x93316b4584083805UL),
            (0u, 1, true, 0x3c74eca650080064UL, 0xa0b429a62b2a7274UL),
            (12345u, 0, false, 0xdcaa3924e37f4405UL, 0x5c0d55c0968a4ed5UL),
            (12345u, 0, true, 0xdcaa3924e37f4405UL, 0x027e04f870dcec68UL),
            (12345u, 1, false, 0x8531f9cfe2d236a5UL, 0x391ae782e9740bcaUL),
            (12345u, 1, true, 0x8531f9cfe2d236a5UL, 0x19627906dc3c14b7UL),
        };

        private static bool RunB9MesherMatchesGolden()
        {
            bool ok = true;
            foreach ((uint seed, int layer, bool fancy, ulong goldenPattern, ulong goldenMeshes) in s_goldenDigests)
            {
                CloudPatternSettings settings = WithSeed(s_layerInputs[layer], seed);
                NativeArray<bool> pattern = BuildReferencePattern(settings, out _);
                try
                {
                    ulong patternDigest = DigestPattern(pattern, settings.Width);
                    ulong meshDigest = DigestTiles(pattern, settings.Width, fancy);
                    ok &= Check($"Seed {seed}, layer {layer}, {(fancy ? "Fancy" : "Fast")}: pattern {patternDigest:x16}, meshes {meshDigest:x16}",
                        patternDigest == goldenPattern && meshDigest == goldenMeshes);
                }
                finally
                {
                    pattern.Dispose();
                }
            }

            return ok;
        }

        private static ulong DigestPattern(NativeArray<bool> pattern, int width)
        {
            ulong hash = FNV_OFFSET;
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < width; z++)
                    hash = Fnv(hash, pattern[z * width + x] ? 1 : 0);
            }

            return hash;
        }

        private static ulong DigestTiles(NativeArray<bool> pattern, int width, bool fancy)
        {
            NativeList<float3> vertices = new NativeList<float3>(Allocator.Temp);
            NativeList<float3> normals = new NativeList<float3>(Allocator.Temp);
            NativeList<int> triangles = new NativeList<int>(Allocator.Temp);
            NativeArray<float3> cornerNormals = new NativeArray<float3>(
                CloudTileMesher.FancyCornerScratchLength(TILE_SIZE), Allocator.Temp);
            NativeList<int3> faces = new NativeList<int3>(Allocator.Temp);
            ulong hash = FNV_OFFSET;
            try
            {
                for (int tileX = 0; tileX < width / TILE_SIZE; tileX++)
                {
                    for (int tileZ = 0; tileZ < width / TILE_SIZE; tileZ++)
                    {
                        if (fancy)
                        {
                            CloudTileMesher.BuildFancyTile(pattern, width, tileX * TILE_SIZE, tileZ * TILE_SIZE, TILE_SIZE,
                                DEPTH_OFFSET, cornerNormals, faces, vertices, normals, triangles);
                        }
                        else
                        {
                            CloudTileMesher.BuildFastTile(pattern, width, tileX * TILE_SIZE, tileZ * TILE_SIZE, TILE_SIZE,
                                DEPTH_OFFSET, vertices, normals, triangles);
                        }

                        hash = Fnv(hash, (long)DigestTile(vertices.AsArray(), normals.AsArray(), triangles.AsArray()));
                    }
                }
            }
            finally
            {
                vertices.Dispose();
                normals.Dispose();
                triangles.Dispose();
                cornerNormals.Dispose();
                faces.Dispose();
            }

            return hash;
        }

        /// <summary>One tile's digest; an empty tile (cached as no mesh) hashes a fixed mark.</summary>
        private static ulong DigestTile(NativeArray<float3> vertices, NativeArray<float3> normals, NativeArray<int> triangles)
        {
            ulong hash = FNV_OFFSET;
            if (vertices.Length == 0) return Fnv(hash, EMPTY_TILE_MARK);

            hash = Fnv(hash, vertices.Length);
            hash = Fnv(hash, triangles.Length);
            for (int i = 0; i < triangles.Length; i++)
                hash = Fnv(hash, triangles[i]);

            for (int i = 0; i < vertices.Length; i++)
            {
                hash = Fnv(hash, math.asint(vertices[i].x));
                hash = Fnv(hash, math.asint(vertices[i].y));
                hash = Fnv(hash, math.asint(vertices[i].z));
                hash = Fnv(hash, math.asint(normals[i].x));
                hash = Fnv(hash, math.asint(normals[i].y));
                hash = Fnv(hash, math.asint(normals[i].z));
            }

            return hash;
        }

        /// <summary>Folds a value's eight little-endian bytes into an FNV-1a hash.</summary>
        private static ulong Fnv(ulong hash, long value)
        {
            for (int i = 0; i < BYTES_PER_LONG; i++)
            {
                hash ^= (byte)(value >> (i * BITS_PER_BYTE));
                hash *= FNV_PRIME;
            }

            return hash;
        }
    }
}
