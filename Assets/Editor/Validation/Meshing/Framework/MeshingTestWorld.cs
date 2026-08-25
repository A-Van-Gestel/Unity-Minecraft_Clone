using System;
using Data;
using Data.Enums;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// Selects whether (and how) <see cref="MeshingTestWorld.Run"/> chains the
    /// <see cref="MeshPostProcessJob"/> after the <see cref="MeshGenerationJob"/> (gap MH-5).
    /// </summary>
    public enum PostProcessMode
    {
        /// <summary>Gen-only: assert the chunk-space output, leave <c>InterleavedStream3</c> empty (B1–B9 default).</summary>
        Off,

        /// <summary>Mirror production <see cref="Chunk.ApplyMeshData"/>: <c>genJob.Run()</c> then <c>postJob.Schedule().Complete()</c>.</summary>
        Separate,

        /// <summary>MR-5 shape: <c>postJob.Schedule(genJob.Schedule())</c> — post-process chained on the gen handle off the calling thread.</summary>
        Chained,
    }

    /// <summary>
    /// The four cardinal neighbor chunks a fixture can populate. The names are the job's own
    /// (<see cref="MeshGenerationJob.NeighborN"/> etc.), so a fixture's direction and the slot it lands in
    /// cannot drift apart.
    /// </summary>
    public enum CardinalNeighbor
    {
        /// <summary>The +Z neighbor; this chunk's +Z border reads its <c>z = 0</c> plane.</summary>
        North,

        /// <summary>The +X neighbor; this chunk's +X border reads its <c>x = 0</c> plane.</summary>
        East,

        /// <summary>The -Z neighbor; this chunk's -Z border reads its <c>z = 15</c> plane.</summary>
        South,

        /// <summary>The -X neighbor; this chunk's -X border reads its <c>x = 15</c> plane.</summary>
        West,
    }

    /// <summary>
    /// The four diagonal neighbor chunks a fixture can populate, named for the job's own
    /// <see cref="MeshGenerationJob.NeighborNE"/> etc. Diagonals never affect face culling; they drive
    /// <b>fluid corner geometry</b> (and smooth-lighting AO), so a fixture that exercises them meshes a
    /// fluid on a chunk corner rather than an opaque cube on a border.
    /// </summary>
    public enum DiagonalNeighbor
    {
        /// <summary>The (+X, +Z) neighbor; this chunk's (+X, +Z) corner reads its <c>(0, ·, 0)</c> cell.</summary>
        NorthEast,

        /// <summary>The (+X, -Z) neighbor; this chunk's (+X, -Z) corner reads its <c>(0, ·, 15)</c> cell.</summary>
        SouthEast,

        /// <summary>The (-X, -Z) neighbor; this chunk's (-X, -Z) corner reads its <c>(15, ·, 15)</c> cell.</summary>
        SouthWest,

        /// <summary>The (-X, +Z) neighbor; this chunk's (-X, +Z) corner reads its <c>(15, ·, 0)</c> cell.</summary>
        NorthWest,
    }

    /// <summary>
    /// Single-chunk meshing harness for the validation suite. Owns a synthetic voxel map and the
    /// synthetic <see cref="TestMeshBlockPalette"/>, then runs the <b>real</b>
    /// <see cref="MeshGenerationJob"/> synchronously (<c>job.Run()</c>) and exposes its
    /// <see cref="MeshDataJobOutput"/> for assertion.
    /// <para>
    /// Mirrors the production / benchmark job wiring: light maps and the neighbor input arrays
    /// are left empty <b>by default</b>, exactly as <see cref="Benchmarks.MeshGenerationBenchmark"/> leaves
    /// them, because the standard-cube path under <see cref="SmoothLightingQuality.Off"/> reads neither. The
    /// custom-mesh arrays ARE populated (from <see cref="TestCustomMeshLibrary"/>) so the palette's half-slab
    /// blocks route through the real schema-aware custom-mesh path; no non-custom-mesh block indexes them. The
    /// fluid height templates ARE populated (16 real entries each) so the fluid meshing path — which indexes
    /// them by fluid level — runs exactly as in production. Most tests place blocks in the chunk interior so
    /// face culling only consults in-chunk neighbors and the empty neighbor-chunk maps never influence the
    /// result. <b>Exception:</b> the cross-chunk baselines opt in to populated cardinal neighbors via
    /// <see cref="SetNeighborBlock"/> (or the +X shorthands <see cref="SetNeighborEastBlock"/> /
    /// <see cref="SetNeighborEastBlockViaProductionFill"/>), which <see cref="Run"/> then passes for the
    /// matching <c>NeighborN/E/S/W</c> slot so the job's border-face culling consults it.
    /// </para>
    /// <para>
    /// The eight neighbor <b>light</b> maps are opt-in the same way (MH-13): uncreated stays length-0 —
    /// the job reads such a slot as light 0 — and <see cref="FillNeighborLight"/> creates + fills one
    /// direction's map so a cross-seam smooth-light sample actually reads it. Populating a neighbor's light
    /// is only meaningful once that neighbor's <b>voxel</b> map exists too (see
    /// <see cref="EnsureNeighborChunk"/> for why).
    /// </para>
    /// </summary>
    public sealed class MeshingTestWorld : IDisposable
    {
        private const int SECTION_COUNT = ChunkMath.SECTIONS_PER_CHUNK;
        private const int MAP_SIZE = VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth;

        private NativeArray<uint> _map;
        private NativeArray<ushort> _lightMap;
        private NativeArray<BlockTypeJobData> _blockTypes;
        private MeshDataJobOutput _output;
        private bool _hasOutput;

        // Opt-in cardinal neighbor voxel maps for the cross-chunk border-face-culling baselines
        // (MH-10/MH-11 on +X, MH-12's permutation guard on all four). Each is left uncreated by default so
        // every existing baseline keeps the original empty-neighbor behavior (border faces drawn as "no
        // neighbor"); each is created lazily the first time a test populates that direction.
        private NativeArray<uint> _neighborN, _neighborE, _neighborS, _neighborW;

        // The 4 diagonal maps (MH-12's B38). Diagonals never reach face culling — they feed fluid corner
        // height/flow smoothing and smooth-lighting AO — so they are exercised by a fluid-on-a-corner
        // fixture. Same lazy discipline: uncreated stays length-0, exactly as every pre-B38 baseline saw.
        private NativeArray<uint> _neighborNE, _neighborSE, _neighborSW, _neighborNW;

        // The 8 neighbor LIGHT maps (MH-13's B40) — the light twin of the voxel maps above, and the slots
        // the job's GetLightDataFromLocalPos routes cross-seam smooth-light samples into. Same lazy
        // discipline: uncreated stays length-0, which the job reads as light 0, so every pre-B40 baseline
        // sees exactly the behavior it always did.
        private NativeArray<ushort> _lightN, _lightE, _lightS, _lightW;
        private NativeArray<ushort> _lightNE, _lightSE, _lightSW, _lightNW;

        /// <summary>Creates an all-air chunk (zeroed light map) and the test block palette job data.</summary>
        public MeshingTestWorld()
        {
            EnsureBurstGeometryInitialized();
            _map = new NativeArray<uint>(MAP_SIZE, Allocator.Persistent); // zero == all Air
            _lightMap = new NativeArray<ushort>(MAP_SIZE, Allocator.Persistent); // zero == fully dark
            _blockTypes = TestMeshBlockPalette.CreateJobDataNativeArray(Allocator.Persistent);
        }

        /// <summary>The output of the most recent <see cref="Run"/> call.</summary>
        public MeshDataJobOutput Output => _output;

        /// <summary>Resets every voxel back to Air. Does not touch the light map (use <see cref="FillLight"/>).</summary>
        public void Clear()
        {
            for (int i = 0; i < _map.Length; i++) _map[i] = 0;
        }

        /// <summary>Writes a block (with optional metadata byte) at a chunk-local position.</summary>
        public void SetBlock(int x, int y, int z, ushort id, byte meta = 0)
        {
            int idx = ChunkMath.GetFlattenedIndexInChunk(x, y, z);
            _map[idx] = BurstVoxelDataBitMapping.PackVoxelData(id, meta);
        }

        /// <summary>
        /// MH-10/MH-12: writes a block into one cardinal neighbor chunk at <b>neighbor-local</b>
        /// coordinates, lazily creating that map (all-Air) on first use. Once created, <see cref="Run"/>
        /// passes it for the matching slot, so the meshing job's border-face culling actually consults it:
        /// a face on this chunk's +X border (local x = 15) reads <c>NeighborE[(0, y, z)]</c> via the job's
        /// <c>GetVoxelStateFromLocalPos</c> wrap, and symmetrically for the other three.
        /// This is a <b>direct</b> write — the consumption-gap test (does the job read + cull correctly).
        /// For the fill-faithful variant see <see cref="SetNeighborEastBlockViaProductionFill"/>.
        /// </summary>
        /// <param name="direction">Which cardinal neighbor to write into.</param>
        /// <param name="x">Neighbor-local X (0–15); the +X border reads x = 0, the -X border x = 15.</param>
        /// <param name="y">Neighbor-local Y.</param>
        /// <param name="z">Neighbor-local Z; the +Z border reads z = 0, the -Z border z = 15.</param>
        /// <param name="id">The palette block ID to write into the neighbor.</param>
        /// <param name="meta">Optional metadata byte.</param>
        public void SetNeighborBlock(CardinalNeighbor direction, int x, int y, int z, ushort id, byte meta = 0)
        {
            ref NativeArray<uint> map = ref NeighborMapRef(direction);
            EnsureCreated(ref map);
            map[ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = BurstVoxelDataBitMapping.PackVoxelData(id, meta);
        }

        /// <summary>
        /// MH-10 shorthand for the +X seam B18–B20 standardize on — <see cref="SetNeighborBlock"/> with
        /// <see cref="CardinalNeighbor.East"/>.
        /// </summary>
        /// <param name="x">Neighbor-local X (0–15); the +X border reads x = 0.</param>
        /// <param name="y">Neighbor-local Y.</param>
        /// <param name="z">Neighbor-local Z.</param>
        /// <param name="id">The palette block ID to write into the neighbor.</param>
        /// <param name="meta">Optional metadata byte.</param>
        public void SetNeighborEastBlock(int x, int y, int z, ushort id, byte meta = 0)
        {
            SetNeighborBlock(CardinalNeighbor.East, x, y, z, id, meta);
        }

        /// <summary>
        /// MH-11: builds the +X (<c>NeighborE</c>) neighbor map through the <b>production</b> fill
        /// path (<see cref="ChunkData.FillJobVoxelMap"/>) instead of writing the flat array directly —
        /// the exact code a halo/border-slab substrate (P-1/P-2) rewrites. A throwaway
        /// <see cref="ChunkData"/> gets the block at neighbor-local coords, then its sections are filled
        /// into the neighbor map exactly as <c>WorldData.FillChunkMapForJob</c> does in production. If the
        /// fill ever under-copies or mis-indexes the border plane, the border-culling baseline (B21) reds.
        /// </summary>
        /// <param name="x">Neighbor-local X (0–15); the +X border reads x = 0.</param>
        /// <param name="y">Neighbor-local Y.</param>
        /// <param name="z">Neighbor-local Z.</param>
        /// <param name="id">The palette block ID to write into the neighbor.</param>
        /// <param name="meta">Optional metadata byte.</param>
        public void SetNeighborEastBlockViaProductionFill(int x, int y, int z, ushort id, byte meta = 0)
        {
            EnsureCreated(ref _neighborE);
            ChunkData neighbor = new ChunkData(Vector2Int.zero);
            neighbor.SetVoxel(x, y, z, BurstVoxelDataBitMapping.PackVoxelData(id, meta));
            neighbor.FillJobVoxelMap(_neighborE);
        }

        /// <summary>
        /// MH-12/B38: writes a block into one diagonal neighbor chunk at <b>neighbor-local</b> coordinates,
        /// lazily creating that map (all-Air) on first use. Diagonals do not reach face culling — they reach
        /// <c>GetSmoothedCornerHeight</c> / <c>CalculateSymmetricCornerFlow</c> for fluids (and AO corner
        /// sampling), so the caller is normally placing a fluid to move a corner's height.
        /// </summary>
        /// <param name="direction">Which diagonal neighbor to write into.</param>
        /// <param name="x">Neighbor-local X (0–15).</param>
        /// <param name="y">Neighbor-local Y.</param>
        /// <param name="z">Neighbor-local Z.</param>
        /// <param name="id">The palette block ID to write into the neighbor.</param>
        /// <param name="meta">Optional metadata byte — for a fluid this is its level (0 = source).</param>
        public void SetNeighborBlock(DiagonalNeighbor direction, int x, int y, int z, ushort id, byte meta = 0)
        {
            ref NativeArray<uint> map = ref NeighborMapRef(direction);
            EnsureCreated(ref map);
            map[ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = BurstVoxelDataBitMapping.PackVoxelData(id, meta);
        }

        /// <summary>
        /// MH-13: materializes a neighbor chunk as <b>loaded but empty and dark</b> — an all-Air voxel map
        /// plus a zeroed light map for that direction — without writing anything into either.
        /// <para>
        /// Use it directly to model a neighbor that must stay <b>dark</b> (every B40 control run needs eight
        /// of them); <see cref="FillNeighborLight"/> calls it for you, because a bright light map without it
        /// is unreadable. The job resolves a cross-seam
        /// smooth-light sample in two steps: <c>SampleNeighborLight</c> first calls
        /// <c>GetVoxelStateFromLocalPos</c>, and a <b>missing</b> (length-0) voxel map means "no neighbor
        /// chunk", which short-circuits to full skylight (15) and <b>never reads the light map at all</b>.
        /// A fixture that brightens a light map without materializing the voxel map therefore reads bright
        /// for the wrong reason — a false green. Materializing the neighbor makes the sample resolve to Air,
        /// which is transparent, so the light map is the only thing left that can brighten the corner.
        /// </para>
        /// </summary>
        /// <param name="direction">Which cardinal neighbor to materialize.</param>
        public void EnsureNeighborChunk(CardinalNeighbor direction)
        {
            EnsureCreated(ref NeighborMapRef(direction));
            EnsureCreated(ref NeighborLightRef(direction));
        }

        /// <summary>
        /// MH-13: the diagonal overload of <see cref="EnsureNeighborChunk(CardinalNeighbor)"/> — same
        /// contract, same reason (a corner sample reaches the diagonal chunk and needs its voxel map present
        /// before its light map can be observed).
        /// </summary>
        /// <param name="direction">Which diagonal neighbor to materialize.</param>
        public void EnsureNeighborChunk(DiagonalNeighbor direction)
        {
            EnsureCreated(ref NeighborMapRef(direction));
            EnsureCreated(ref NeighborLightRef(direction));
        }

        /// <summary>
        /// MH-13: fills one cardinal neighbor's <b>light</b> map with a single packed value. <see cref="Run"/>
        /// then passes it for the matching <c>LightN/E/S/W</c> slot, so a border vertex's smooth-light corner
        /// samples read it through the job's <c>GetLightDataFromLocalPos</c> routing.
        /// <para>
        /// Materializes the whole neighbor chunk (<see cref="EnsureNeighborChunk"/>), not just its light map:
        /// a bright light map on a <i>missing</i> chunk is unreadable by construction — see that method — so
        /// there is no state worth expressing in which the two come apart. Call
        /// <see cref="EnsureNeighborChunk"/> directly for a neighbor that should stay <b>dark</b>.
        /// </para>
        /// </summary>
        /// <param name="direction">Which cardinal neighbor's light map to fill.</param>
        /// <param name="packed">Packed <c>ushort</c> light value (sky + blocklight RGB, each 0-15).</param>
        public void FillNeighborLight(CardinalNeighbor direction, ushort packed)
        {
            EnsureNeighborChunk(direction);
            FillLightMap(ref NeighborLightRef(direction), packed);
        }

        /// <summary>
        /// MH-13: the diagonal overload of <see cref="FillNeighborLight(CardinalNeighbor, ushort)"/> — same
        /// contract, including materializing the neighbor chunk. A diagonal light map is reached only by a
        /// corner sample (the corner-offset LUT's diagonal term), so the fixture places its probe on a chunk
        /// corner.
        /// </summary>
        /// <param name="direction">Which diagonal neighbor's light map to fill.</param>
        /// <param name="packed">Packed <c>ushort</c> light value (sky + blocklight RGB, each 0-15).</param>
        public void FillNeighborLight(DiagonalNeighbor direction, ushort packed)
        {
            EnsureNeighborChunk(direction);
            FillLightMap(ref NeighborLightRef(direction), packed);
        }

        /// <summary>
        /// Resolves a cardinal direction to its backing <b>light</b> map field. Named per direction rather
        /// than indexed, for the same reason as <see cref="NeighborMapRef(CardinalNeighbor)"/>: the harness
        /// must not be able to introduce the very permutation its baseline exists to catch.
        /// </summary>
        /// <param name="direction">The cardinal direction to resolve.</param>
        /// <returns>A reference to the backing field, so the caller can lazily create it in place.</returns>
        private ref NativeArray<ushort> NeighborLightRef(CardinalNeighbor direction)
        {
            switch (direction)
            {
                case CardinalNeighbor.North: return ref _lightN;
                case CardinalNeighbor.East: return ref _lightE;
                case CardinalNeighbor.South: return ref _lightS;
                case CardinalNeighbor.West: return ref _lightW;
                default: throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown cardinal neighbor");
            }
        }

        /// <summary>Resolves a diagonal direction to its backing <b>light</b> map field.</summary>
        /// <param name="direction">The diagonal direction to resolve.</param>
        /// <returns>A reference to the backing field, so the caller can lazily create it in place.</returns>
        private ref NativeArray<ushort> NeighborLightRef(DiagonalNeighbor direction)
        {
            switch (direction)
            {
                case DiagonalNeighbor.NorthEast: return ref _lightNE;
                case DiagonalNeighbor.SouthEast: return ref _lightSE;
                case DiagonalNeighbor.SouthWest: return ref _lightSW;
                case DiagonalNeighbor.NorthWest: return ref _lightNW;
                default: throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown diagonal neighbor");
            }
        }

        /// <summary>Lazily creates a neighbor light map, then writes one packed value into every cell.</summary>
        /// <param name="map">The backing light-map field to fill.</param>
        /// <param name="packed">Packed <c>ushort</c> light value to write everywhere.</param>
        private static void FillLightMap(ref NativeArray<ushort> map, ushort packed)
        {
            EnsureCreated(ref map);
            for (int i = 0; i < map.Length; i++) map[i] = packed;
        }

        /// <summary>
        /// Resolves a diagonal direction to its backing map field — named per direction for the same reason
        /// as the cardinal overload.
        /// </summary>
        /// <param name="direction">The diagonal direction to resolve.</param>
        /// <returns>A reference to the backing field, so the caller can lazily create it in place.</returns>
        private ref NativeArray<uint> NeighborMapRef(DiagonalNeighbor direction)
        {
            switch (direction)
            {
                case DiagonalNeighbor.NorthEast: return ref _neighborNE;
                case DiagonalNeighbor.SouthEast: return ref _neighborSE;
                case DiagonalNeighbor.SouthWest: return ref _neighborSW;
                case DiagonalNeighbor.NorthWest: return ref _neighborNW;
                default: throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown diagonal neighbor");
            }
        }

        /// <summary>
        /// Resolves a direction to its backing map field. Named per direction rather than indexed, so the
        /// harness cannot itself introduce the permutation bug MH-12's baseline exists to catch.
        /// </summary>
        /// <param name="direction">The cardinal direction to resolve.</param>
        /// <returns>A reference to the backing field, so the caller can lazily create it in place.</returns>
        private ref NativeArray<uint> NeighborMapRef(CardinalNeighbor direction)
        {
            switch (direction)
            {
                case CardinalNeighbor.North: return ref _neighborN;
                case CardinalNeighbor.East: return ref _neighborE;
                case CardinalNeighbor.South: return ref _neighborS;
                case CardinalNeighbor.West: return ref _neighborW;
                default: throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown cardinal neighbor");
            }
        }

        /// <summary>
        /// Lazily allocates a persistent zeroed neighbor map in place (idempotent). Zero means all-Air for a
        /// voxel map and fully dark for a light map.
        /// </summary>
        /// <typeparam name="T">The map's element type (<c>uint</c> voxels or <c>ushort</c> packed light).</typeparam>
        /// <param name="map">The backing field to create if it is not already.</param>
        private static void EnsureCreated<T>(ref NativeArray<T> map) where T : unmanaged
        {
            if (!map.IsCreated)
                map = new NativeArray<T>(MAP_SIZE, Allocator.Persistent);
        }

        /// <summary>
        /// Fills the entire in-chunk light map with one packed value (MH-3). A spatially uniform field lets
        /// the smooth-light corner oracle be hand-derived without the engine's sampling LUT: every sample a
        /// corner reads is identical, so the averaged result is independent of which neighbors are picked
        /// (see <see cref="MeshOracle.ExpectedUniformCornerLight"/>). Pack values with
        /// <c>LightBitMapping.PackLightData</c>.
        /// </summary>
        /// <param name="packed">Packed <c>ushort</c> light value (sky + blocklight RGB, each 0-15).</param>
        public void FillLight(ushort packed)
        {
            for (int i = 0; i < _lightMap.Length; i++) _lightMap[i] = packed;
        }

        /// <summary>Writes a packed light value at a single chunk-local position.</summary>
        /// <param name="x">Chunk-local X.</param>
        /// <param name="y">Chunk-local Y.</param>
        /// <param name="z">Chunk-local Z.</param>
        /// <param name="packed">Packed <c>ushort</c> light value (sky + blocklight RGB, each 0-15).</param>
        public void SetLight(int x, int y, int z, ushort packed)
        {
            _lightMap[ChunkMath.GetFlattenedIndexInChunk(x, y, z)] = packed;
        }

        /// <summary>
        /// Runs the real <see cref="MeshGenerationJob"/> over the current voxel map and stores the
        /// result in <see cref="Output"/> (disposing any previous output). The returned struct is
        /// owned by this harness — do not dispose it directly.
        /// <para>
        /// When <paramref name="postProcess"/> is not <see cref="PostProcessMode.Off"/>, the real
        /// <see cref="MeshPostProcessJob"/> is chained after the gen job (gap MH-5), rewriting the
        /// output in place to section-space coordinates, relativizing per-section triangle indices, and
        /// populating <c>InterleavedStream3</c> — the post-process stage that is otherwise unguarded.
        /// <see cref="PostProcessMode.Separate"/> mirrors production (<see cref="Chunk.ApplyMeshData"/>):
        /// a synchronous gen run followed by a blocking <c>Schedule().Complete()</c>;
        /// <see cref="PostProcessMode.Chained"/> instead chains the post job on the gen job's handle off
        /// the calling thread (the MR-5 proposal). Both must produce byte-identical output.
        /// </para>
        /// </summary>
        /// <param name="lighting">Smooth-lighting quality; defaults to <see cref="SmoothLightingQuality.Off"/>
        /// so geometry is independent of (absent) light data.</param>
        /// <param name="postProcess">Whether/how to chain <see cref="MeshPostProcessJob"/>; defaults to
        /// <see cref="PostProcessMode.Off"/> so the gen-only chunk-space output is preserved unchanged.</param>
        /// <param name="fullCubeContactShadows">SS-3: opts this run into subdividing faces that only
        /// <b>full cubes</b> reach. Defaults to off, matching the shipped setting, so every baseline
        /// written before SS-3 keeps meshing exactly the geometry it was written against.</param>
        /// <param name="reuseOutput">MH-2: when supplied, the job writes into this caller-owned output
        /// instead of a fresh one, and the harness does NOT take ownership of it (it is neither stored as
        /// <see cref="Output"/> nor disposed by <see cref="Dispose"/>). Used to drive a pooled, reused
        /// buffer through the real meshing path so a stale-data leak can be detected. The buffer must be
        /// empty (length 0) on entry — the job appends and never clears.</param>
        public MeshDataJobOutput Run(SmoothLightingQuality lighting = SmoothLightingQuality.Off,
            PostProcessMode postProcess = PostProcessMode.Off,
            MeshDataJobOutput? reuseOutput = null,
            bool fullCubeContactShadows = false)
        {
            DisposeOutput();

            // Default sections (IsEmpty=false, IsFullySolid=false) force the standard per-voxel
            // iteration path — the path MR-1 lives in — for every section.
            NativeArray<SectionJobData> sectionData =
                new NativeArray<SectionJobData>(SECTION_COUNT, Allocator.TempJob);

            // Empty cardinal/diagonal neighbor maps: interior blocks never read them; border blocks
            // would treat them as "no neighbor" (face drawn), which no scenario relies on.
            NativeArray<uint> emptyMap = new NativeArray<uint>(0, Allocator.TempJob);
            // MH-10/MH-11/MH-12: when a test populated a cardinal neighbor, pass that persistent map for its
            // slot so border-face culling consults it; otherwise behave exactly as before (empty = void).
            NativeArray<uint> mapN = _neighborN.IsCreated ? _neighborN : emptyMap;
            NativeArray<uint> mapE = _neighborE.IsCreated ? _neighborE : emptyMap;
            NativeArray<uint> mapS = _neighborS.IsCreated ? _neighborS : emptyMap;
            NativeArray<uint> mapW = _neighborW.IsCreated ? _neighborW : emptyMap;
            NativeArray<uint> mapNE = _neighborNE.IsCreated ? _neighborNE : emptyMap;
            NativeArray<uint> mapSE = _neighborSE.IsCreated ? _neighborSE : emptyMap;
            NativeArray<uint> mapSW = _neighborSW.IsCreated ? _neighborSW : emptyMap;
            NativeArray<uint> mapNW = _neighborNW.IsCreated ? _neighborNW : emptyMap;
            // Real flattened custom-mesh inputs for the palette's HalfSlab/PartialOpaque blocks, built the
            // same way JobDataManagerFactory flattens VoxelMeshData assets. Blocks that are not custom
            // meshes never index these, so every pre-existing baseline is unaffected by their presence.
            TestCustomMeshLibrary.Build(Allocator.TempJob,
                out NativeArray<CustomMeshData> customMeshes,
                out NativeArray<CustomFaceData> customFaces,
                out NativeArray<CustomVertData> customVerts,
                out NativeArray<int> customTris);
            // Real 16-entry water height template (the palette's only fluid is water). The fluid path
            // indexes this by fluid level, so an empty array would index out of range; it is built from
            // the same shared source of truth the FluidDataGenerator editor tool bakes into the asset.
            NativeArray<float> waterTemplates = BuildFluidTemplateArray(flowLevels: 8, decayStep: 1.0f / 8.0f, Allocator.TempJob);
            // No lava block in the palette, so LavaVertexTemplates is never indexed — the job safety
            // system only needs a constructed (non-default) container, which an empty array satisfies.
            NativeArray<float> lavaTemplates = new NativeArray<float>(0, Allocator.TempJob);

            // Light arrays must be valid (constructed) containers — the job safety system rejects
            // unassigned NativeArrays at schedule/Run time. The in-chunk map is the persistent _lightMap
            // (zeroed by default; populated via FillLight/SetLight for the smooth-light MH-3 tests).
            // An empty neighbor light map suffices for interior blocks, which only read the in-chunk map.
            NativeArray<ushort> emptyLight = new NativeArray<ushort>(0, Allocator.TempJob);
            // MH-13: when a test populated a neighbor's light map, pass it for that slot so cross-seam
            // smooth-light samples read it; otherwise behave exactly as before (empty = light 0).
            NativeArray<ushort> lightN = _lightN.IsCreated ? _lightN : emptyLight;
            NativeArray<ushort> lightE = _lightE.IsCreated ? _lightE : emptyLight;
            NativeArray<ushort> lightS = _lightS.IsCreated ? _lightS : emptyLight;
            NativeArray<ushort> lightW = _lightW.IsCreated ? _lightW : emptyLight;
            NativeArray<ushort> lightNE = _lightNE.IsCreated ? _lightNE : emptyLight;
            NativeArray<ushort> lightSE = _lightSE.IsCreated ? _lightSE : emptyLight;
            NativeArray<ushort> lightSW = _lightSW.IsCreated ? _lightSW : emptyLight;
            NativeArray<ushort> lightNW = _lightNW.IsCreated ? _lightNW : emptyLight;

            // MH-2: write into the caller-owned reuse buffer when provided (the harness will not dispose
            // it); otherwise allocate a fresh harness-owned output.
            MeshDataJobOutput output = reuseOutput ?? new MeshDataJobOutput(Allocator.Persistent);

            MeshGenerationJob job = new MeshGenerationJob
            {
                Map = _map,
                SectionData = sectionData,
                BlockTypes = _blockTypes,
                ClipBounds = MeshClipBounds.Disabled,
                ChunkPosition = Vector3.zero,
                NeighborS = mapS,
                NeighborN = mapN,
                NeighborW = mapW,
                NeighborE = mapE,
                NeighborNE = mapNE,
                NeighborSE = mapSE,
                NeighborSW = mapSW,
                NeighborNW = mapNW,
                CustomMeshes = customMeshes,
                CustomFaces = customFaces,
                CustomVerts = customVerts,
                CustomTris = customTris,
                WaterVertexTemplates = waterTemplates,
                LavaVertexTemplates = lavaTemplates,
                SmoothLighting = lighting,
                FullCubeContactShadows = fullCubeContactShadows,
                Output = output,
                LightMap = _lightMap,
                LightS = lightS,
                LightN = lightN,
                LightW = lightW,
                LightE = lightE,
                LightNE = lightNE,
                LightSE = lightSE,
                LightSW = lightSW,
                LightNW = lightNW,
            };

            // Execute the gen job, optionally chaining the real MeshPostProcessJob (MH-5). The post job
            // rewrites `output` in place (section-space verts, relativized indices, InterleavedStream3),
            // reading the SectionStats the gen job wrote. Both modes block before disposal so the
            // TempJob inputs the gen job reads stay alive until it (and any chained post job) completes.
            switch (postProcess)
            {
                case PostProcessMode.Off:
                    job.Run();
                    break;

                case PostProcessMode.Separate:
                    // Production shape: synchronous gen, then a blocking Schedule().Complete() post pass.
                    job.Run();
                    BuildPostProcessJob(output).Schedule().Complete();
                    break;

                case PostProcessMode.Chained:
                    // MR-5 shape: post chained on the gen handle, both off the calling thread.
                    JobHandle genHandle = job.Schedule();
                    BuildPostProcessJob(output).Schedule(genHandle).Complete();
                    break;
            }

            sectionData.Dispose();
            emptyMap.Dispose();
            customMeshes.Dispose();
            customFaces.Dispose();
            customVerts.Dispose();
            customTris.Dispose();
            waterTemplates.Dispose();
            lavaTemplates.Dispose();
            emptyLight.Dispose();

            // Only take ownership of a harness-allocated output. A caller-supplied reuse buffer (MH-2)
            // stays owned by the caller, so it is never stored as _output nor disposed by this harness.
            if (reuseOutput == null)
            {
                _output = output;
                _hasOutput = true;
            }

            return output;
        }

        /// <summary>
        /// Builds the real <see cref="MeshPostProcessJob"/> over an existing gen output, wired exactly as
        /// <see cref="Chunk.ApplyMeshData"/> does (same field mapping, <c>SectionHeight =
        /// ChunkMath.SECTION_SIZE</c>). The job rewrites <paramref name="output"/> in place.
        /// </summary>
        /// <param name="output">The gen-job output to post-process (mutated in place).</param>
        private static MeshPostProcessJob BuildPostProcessJob(MeshDataJobOutput output)
        {
            return new MeshPostProcessJob
            {
                Vertices = output.Vertices,
                OpaqueTris = output.Triangles,
                TransparentTris = output.TransparentTriangles,
                FluidTris = output.FluidTriangles,
                Stats = output.SectionStats,
                Normals = output.Normals,
                LightData = output.LightData,
                InterleavedStream3 = output.InterleavedStream3,
                SectionHeight = ChunkMath.SECTION_SIZE,
            };
        }

        /// <summary>
        /// Builds a 16-entry fluid vertex-height template via <see cref="FluidMeshData.BuildVertexHeightTemplate"/>
        /// — the same source of truth the <c>FluidDataGenerator</c> editor tool bakes into the asset —
        /// so the fluid meshing path reads exactly the heights it does in production.
        /// </summary>
        /// <param name="flowLevels">Horizontal flow levels (8 for water, 4 for lava).</param>
        /// <param name="decayStep">Height decrease per flow level (1/8 for water, 1/4 for lava).</param>
        /// <param name="allocator">Allocator for the returned array; caller owns disposal.</param>
        private static NativeArray<float> BuildFluidTemplateArray(int flowLevels, float decayStep, Allocator allocator)
        {
            float[] managed = new float[16];
            FluidMeshData.BuildVertexHeightTemplate(managed, flowLevels, decayStep);
            return new NativeArray<float>(managed, allocator);
        }

        /// <summary>Ensures the shared static voxel geometry tables are allocated (no-op in play mode).</summary>
        private static void EnsureBurstGeometryInitialized()
        {
            if (!BurstVoxelData.VoxelVerts.Data.IsCreated)
                BurstVoxelData.Initialize();
        }

        private void DisposeOutput()
        {
            if (_hasOutput)
            {
                _output.Dispose();
                _hasOutput = false;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            DisposeOutput();
            if (_map.IsCreated) _map.Dispose();
            if (_lightMap.IsCreated) _lightMap.Dispose();
            if (_blockTypes.IsCreated) _blockTypes.Dispose();
            if (_neighborN.IsCreated) _neighborN.Dispose();
            if (_neighborE.IsCreated) _neighborE.Dispose();
            if (_neighborS.IsCreated) _neighborS.Dispose();
            if (_neighborW.IsCreated) _neighborW.Dispose();
            if (_neighborNE.IsCreated) _neighborNE.Dispose();
            if (_neighborSE.IsCreated) _neighborSE.Dispose();
            if (_neighborSW.IsCreated) _neighborSW.Dispose();
            if (_neighborNW.IsCreated) _neighborNW.Dispose();
            if (_lightN.IsCreated) _lightN.Dispose();
            if (_lightE.IsCreated) _lightE.Dispose();
            if (_lightS.IsCreated) _lightS.Dispose();
            if (_lightW.IsCreated) _lightW.Dispose();
            if (_lightNE.IsCreated) _lightNE.Dispose();
            if (_lightSE.IsCreated) _lightSE.Dispose();
            if (_lightSW.IsCreated) _lightSW.Dispose();
            if (_lightNW.IsCreated) _lightNW.Dispose();
        }
    }
}
