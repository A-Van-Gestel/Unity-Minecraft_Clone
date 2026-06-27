using Data;
using Data.JobData;
using Data.NativeData;

namespace Editor.WorldTools.Libraries
{
    /// <summary>
    /// Builds <see cref="JobDataManager"/> and <see cref="FluidVertexTemplatesNativeData"/> from a
    /// <see cref="BlockDatabase"/> asset without requiring a <c>World</c> instance.
    /// <para>Thin editor-facing wrapper over the shared runtime <see cref="JobDataManagerFactory"/>,
    /// which owns the single copy of the flatten logic (also used by <c>World.PrepareGlobalJobData</c>
    /// and the OM-1 startup calibrator).</para>
    /// </summary>
    public static class EditorJobDataManagerFactory
    {
        /// <summary>
        /// Creates persistent native job data from the given block database.
        /// The caller is responsible for disposing both returned objects.
        /// </summary>
        /// <param name="blockDatabase">The block database asset to flatten.</param>
        /// <returns>A tuple of the job data manager and fluid vertex template data.</returns>
        public static (JobDataManager jobDataManager, FluidVertexTemplatesNativeData fluidTemplates) Create(
            BlockDatabase blockDatabase)
        {
            GlobalJobData jobData = JobDataManagerFactory.Create(blockDatabase);
            return (jobData.JobDataManager, jobData.FluidVertexTemplates);
        }
    }
}
