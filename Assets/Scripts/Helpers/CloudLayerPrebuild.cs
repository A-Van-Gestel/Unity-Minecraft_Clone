using System;
using Jobs;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Helpers
{
    /// <summary>The inputs that fully determine one procedural cloud pattern.</summary>
    public struct CloudPatternSettings : IEquatable<CloudPatternSettings>
    {
        /// <summary>Pattern edge in cells.</summary>
        public int Width;

        /// <summary>Noise lattice cells across the pattern at the first octave.</summary>
        public int BasePeriodCells;

        /// <summary>FBM octaves.</summary>
        public int Octaves;

        /// <summary>FBM amplitude falloff per octave.</summary>
        public float Persistence;

        /// <summary>World seed XOR the layer's salt.</summary>
        public uint Seed;

        /// <summary>Fraction of cells that end up as cloud.</summary>
        public float Coverage;

        /// <summary>The noise job for these settings, writing into the given field.</summary>
        /// <param name="density">Receives the density field; <see cref="Width"/> squared elements.</param>
        /// <returns>The unscheduled job.</returns>
        public CloudPatternJob ToNoiseJob(NativeArray<float> density) => new CloudPatternJob
        {
            PatternWidth = Width,
            BasePeriodCells = BasePeriodCells,
            Octaves = Octaves,
            Persistence = Persistence,
            Seed = Seed,
            Density = density,
        };

        /// <inheritdoc />
        public bool Equals(CloudPatternSettings other) =>
            Width == other.Width && BasePeriodCells == other.BasePeriodCells && Octaves == other.Octaves
            && Persistence.Equals(other.Persistence) && Seed == other.Seed && Coverage.Equals(other.Coverage);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is CloudPatternSettings other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Width, BasePeriodCells, Octaves, Persistence, Seed, Coverage);
    }

    /// <summary>
    /// One procedural cloud layer's pattern and every tile mesh, built by Burst jobs on the workers ahead of the
    /// frame that uses them. Owns all buffers (persistent: they outlive the four-frame TempJob limit) until
    /// <see cref="Dispose"/>; the pattern can be taken over instead. Main thread only.
    /// </summary>
    public sealed class CloudLayerPrebuild : IDisposable
    {
        /// <summary>The pattern inputs this prebuild was scheduled with.</summary>
        public readonly CloudPatternSettings Settings;

        /// <summary>Whether the tiles are Fancy hulls (else Fast quads).</summary>
        public readonly bool Fancy;

        /// <summary>Tile edge in cells.</summary>
        public readonly int TileSize;

        /// <summary>The hull's outward push the tiles were built with.</summary>
        public readonly float DepthOffset;

        private NativeArray<float> _density;
        private NativeArray<bool> _pattern;
        private NativeArray<int> _setCount;
        private readonly NativeList<float3>[] _vertices;
        private readonly NativeList<float3>[] _normals;
        private readonly NativeList<int>[] _triangles;
        private JobHandle _handle;
        private bool _completed;

        private CloudLayerPrebuild(in CloudPatternSettings settings, bool fancy, int tileSize, float depthOffset)
        {
            Settings = settings;
            Fancy = fancy;
            TileSize = tileSize;
            DepthOffset = depthOffset;
            int tiles = TilesPerSide * TilesPerSide;
            _vertices = new NativeList<float3>[tiles];
            _normals = new NativeList<float3>[tiles];
            _triangles = new NativeList<int>[tiles];
        }

        /// <summary>Tiles along one pattern edge.</summary>
        public int TilesPerSide => Settings.Width / TileSize;

        /// <summary>Tiles in the pattern.</summary>
        public int TileCount => _vertices.Length;

        /// <summary>Cloud cells in the pattern; valid after <see cref="Complete"/>.</summary>
        public int SetCount => _setCount[0];

        /// <summary>Schedules the noise, threshold and one mesh job per tile, and starts the workers on them.</summary>
        /// <param name="settings">The pattern inputs.</param>
        /// <param name="fancy">True for Fancy hulls, false for Fast quads.</param>
        /// <param name="tileSize">Tile edge in cells; must divide <see cref="CloudPatternSettings.Width"/>.</param>
        /// <param name="depthOffset">The hull's outward push, in blocks.</param>
        /// <returns>The running prebuild.</returns>
        /// <exception cref="ArgumentException">The tile size does not divide the pattern width.</exception>
        public static CloudLayerPrebuild Schedule(in CloudPatternSettings settings, bool fancy, int tileSize, float depthOffset)
        {
            if (tileSize <= 0 || settings.Width % tileSize != 0)
                throw new ArgumentException($"Cloud tile size {tileSize} must divide the pattern width {settings.Width}.");

            CloudLayerPrebuild prebuild = new CloudLayerPrebuild(settings, fancy, tileSize, depthOffset);
            prebuild.ScheduleJobs();
            return prebuild;
        }

        /// <summary>Waits for every job; safe to call more than once.</summary>
        public void Complete()
        {
            if (_completed) return;
            _handle.Complete();
            _completed = true;
        }

        /// <summary>Hands the pattern to the caller, who then owns (and disposes) it.</summary>
        /// <returns>The flat cloud pattern, index <c>z * width + x</c>.</returns>
        public NativeArray<bool> TakePattern()
        {
            Complete();
            NativeArray<bool> pattern = _pattern;
            _pattern = default;
            return pattern;
        }

        /// <summary>One tile's origin and mesh data; the arrays stay owned by this prebuild.</summary>
        /// <param name="index">Tile index, X-major within a row of <see cref="TilesPerSide"/>.</param>
        /// <param name="originX">Pattern-space X of the tile's first cell.</param>
        /// <param name="originZ">Pattern-space Z of the tile's first cell.</param>
        /// <param name="vertices">Tile-local vertices.</param>
        /// <param name="normals">One normal per vertex.</param>
        /// <param name="triangles">Triangle indices.</param>
        public void GetTile(int index, out int originX, out int originZ,
            out NativeArray<float3> vertices, out NativeArray<float3> normals, out NativeArray<int> triangles)
        {
            Complete();
            originX = index % TilesPerSide * TileSize;
            originZ = index / TilesPerSide * TileSize;
            vertices = _vertices[index].AsArray();
            normals = _normals[index].AsArray();
            triangles = _triangles[index].AsArray();
        }

        /// <summary>Completes any running job, then frees every buffer still owned.</summary>
        public void Dispose()
        {
            _handle.Complete();
            _completed = true;
            if (_density.IsCreated) _density.Dispose();
            if (_pattern.IsCreated) _pattern.Dispose();
            if (_setCount.IsCreated) _setCount.Dispose();
            for (int i = 0; i < _vertices.Length; i++)
            {
                if (_vertices[i].IsCreated) _vertices[i].Dispose();
                if (_normals[i].IsCreated) _normals[i].Dispose();
                if (_triangles[i].IsCreated) _triangles[i].Dispose();
            }
        }

        private void ScheduleJobs()
        {
            int cells = Settings.Width * Settings.Width;
            _density = new NativeArray<float>(cells, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _pattern = new NativeArray<bool>(cells, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _setCount = new NativeArray<int>(1, Allocator.Persistent);

            JobHandle noise = Settings.ToNoiseJob(_density).Schedule(cells, Settings.Width);
            JobHandle threshold = new CloudThresholdJob
            {
                Density = _density,
                Coverage = Settings.Coverage,
                Pattern = _pattern,
                SetCount = _setCount,
            }.Schedule(noise);

            // The density is dead once thresholded; free it on the workers instead of holding it through the load.
            JobHandle densityFreed = _density.Dispose(threshold);
            _density = default;

            NativeArray<JobHandle> tileHandles = new NativeArray<JobHandle>(TileCount, Allocator.Temp);
            for (int i = 0; i < TileCount; i++)
            {
                _vertices[i] = new NativeList<float3>(Allocator.Persistent);
                _normals[i] = new NativeList<float3>(Allocator.Persistent);
                _triangles[i] = new NativeList<int>(Allocator.Persistent);
                tileHandles[i] = new CloudTileMeshJob
                {
                    Pattern = _pattern,
                    PatternWidth = Settings.Width,
                    TileOriginX = i % TilesPerSide * TileSize,
                    TileOriginZ = i / TilesPerSide * TileSize,
                    TileSize = TileSize,
                    DepthOffset = DepthOffset,
                    Fancy = Fancy,
                    Vertices = _vertices[i],
                    Normals = _normals[i],
                    Triangles = _triangles[i],
                }.Schedule(threshold);
            }

            _handle = JobHandle.CombineDependencies(JobHandle.CombineDependencies(tileHandles), densityFreed);
            tileHandles.Dispose();
            JobHandle.ScheduleBatchedJobs();
        }
    }
}
