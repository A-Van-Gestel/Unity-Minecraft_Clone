using Data;
using Data.JobData;
using Data.NativeData;
using Helpers;
using Jobs;
using Unity.Collections;
using Unity.Jobs;

namespace Benchmarks
{
    /// <summary>
    /// Shared, <c>World</c>-free core for scheduling a single production meshing job in isolation —
    /// the one source of truth for the <see cref="MeshGenerationJob"/> field wiring, so the meshing
    /// benchmark (<see cref="MeshGenerationBenchmark"/>) and the OM-1 startup calibrator schedule the
    /// exact same job and cannot drift apart.
    /// <para>Job data is injected (not pulled from <c>World.Instance</c>): the benchmark passes the live
    /// world's <see cref="JobDataManager"/>; the calibrator passes a temporary one built by
    /// <see cref="JobDataManagerFactory"/> at the Main Menu where no <c>World</c> exists.</para>
    /// </summary>
    public static class IsolatedJobProbe
    {
        /// <summary>
        /// Schedules a single <see cref="MeshGenerationJob"/> over the given voxel maps and injected
        /// job data, returning the chained handle and the output container.
        /// <para>Temporary per-schedule containers (section data and the cardinals-only empty array) are
        /// created here and chained onto the returned handle for auto-disposal on completion. The caller
        /// owns the returned <see cref="MeshDataJobOutput"/> and must dispose it after
        /// <see cref="JobHandle.Complete"/>.</para>
        /// </summary>
        /// <param name="input">The center map plus the 8 neighbor maps and the diagonal-inclusion flag.</param>
        /// <param name="jobData">Injected block-type and custom-mesh native job data.</param>
        /// <param name="fluidTemplates">Injected water/lava vertex templates.</param>
        /// <returns>The scheduled job handle and its output container.</returns>
        public static (JobHandle handle, MeshDataJobOutput output) ScheduleMesh(
            in MeshProbeInput input,
            JobDataManager jobData,
            FluidVertexTemplatesNativeData fluidTemplates)
        {
            MeshDataJobOutput meshOutput = new MeshDataJobOutput(Allocator.Persistent);

            // SectionData is required by MeshGenerationJob.Execute (one entry per 16-block section).
            // Left at default (IsEmpty=false, IsFullySolid=false) so every section runs the per-voxel
            // standard path — the representative hot path the throughput probe should measure.
            const int sectionCount = ChunkMath.SECTIONS_PER_CHUNK;
            NativeArray<SectionJobData> sectionData = new NativeArray<SectionJobData>(sectionCount, Allocator.TempJob);

            // In cardinals-only mode the diagonal neighbor fields take a temporary empty array.
            NativeArray<uint> emptyArray = input.IncludeDiagonals
                ? default
                : new NativeArray<uint>(0, Allocator.TempJob);

            MeshGenerationJob job = new MeshGenerationJob
            {
                Map = input.Center,
                SectionData = sectionData,
                BlockTypes = jobData.BlockTypesJobData,
                NeighborBack = input.Back,
                NeighborFront = input.Front,
                NeighborLeft = input.Left,
                NeighborRight = input.Right,
                NeighborFrontRight = input.IncludeDiagonals ? input.FrontRight : emptyArray,
                NeighborBackRight = input.IncludeDiagonals ? input.BackRight : emptyArray,
                NeighborBackLeft = input.IncludeDiagonals ? input.BackLeft : emptyArray,
                NeighborFrontLeft = input.IncludeDiagonals ? input.FrontLeft : emptyArray,
                // Light maps are optional to the job (GetLight returns 0 for an uncreated map); the
                // benchmark leaves them default (it runs only in player builds where job-safety is off),
                // while the calibrator supplies created zero maps so it also passes editor job-safety.
                LightMap = input.LightCenter,
                LightBack = input.LightBack,
                LightFront = input.LightFront,
                LightLeft = input.LightLeft,
                LightRight = input.LightRight,
                LightFrontRight = input.LightFrontRight,
                LightBackRight = input.LightBackRight,
                LightBackLeft = input.LightBackLeft,
                LightFrontLeft = input.LightFrontLeft,
                CustomMeshes = jobData.CustomMeshesJobData,
                CustomFaces = jobData.CustomFacesJobData,
                CustomVerts = jobData.CustomVertsJobData,
                CustomTris = jobData.CustomTrisJobData,
                WaterVertexTemplates = fluidTemplates.WaterVertexTemplates,
                LavaVertexTemplates = fluidTemplates.LavaVertexTemplates,
                ClipBounds = MeshClipBounds.Disabled,
                Output = meshOutput,
            };

            // Chain disposal of the temporary native containers to the handle so they auto-free when the
            // job completes (avoids JobTempAlloc 4-frame leak warnings).
            JobHandle handle = job.Schedule();
            handle = sectionData.Dispose(handle);
            if (emptyArray.IsCreated)
            {
                handle = emptyArray.Dispose(handle);
            }

            return (handle, meshOutput);
        }
    }

    /// <summary>
    /// Inputs for a single <see cref="IsolatedJobProbe.ScheduleMesh"/> call: the 9 voxel maps (center +
    /// 8 neighbors), the optional 9 light maps, and the diagonal-inclusion flag. The caller owns the
    /// referenced native arrays; this struct only borrows them for the duration of the schedule.
    /// <para>Light maps are optional — leave them <c>default</c> to mesh with zero light (valid only in
    /// player builds where job-safety is off). Set created maps to pass editor job-safety.</para>
    /// </summary>
    public struct MeshProbeInput
    {
        /// <summary>The chunk being meshed.</summary>
        public NativeArray<uint> Center;

        /// <summary>Cardinal neighbor voxel maps.</summary>
        public NativeArray<uint> Back, Front, Left, Right;

        /// <summary>Diagonal neighbor voxel maps (ignored when <see cref="IncludeDiagonals"/> is false).</summary>
        public NativeArray<uint> FrontRight, BackRight, BackLeft, FrontLeft;

        /// <summary>Center light map (optional; <c>default</c> meshes with zero light).</summary>
        public NativeArray<ushort> LightCenter;

        /// <summary>Cardinal neighbor light maps (optional).</summary>
        public NativeArray<ushort> LightBack, LightFront, LightLeft, LightRight;

        /// <summary>Diagonal neighbor light maps (optional).</summary>
        public NativeArray<ushort> LightFrontRight, LightBackRight, LightBackLeft, LightFrontLeft;

        /// <summary>When false, the diagonal voxel fields receive an empty array (cardinals-only meshing).</summary>
        public bool IncludeDiagonals;
    }
}
