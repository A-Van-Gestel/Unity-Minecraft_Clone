namespace Serialization.Migration
{
    /// <summary>
    /// Base class for any version upgrade script.
    /// Example implementation: "Migration_V1_To_V2_AddFluidPressure"
    /// </summary>
    public abstract class WorldMigration
    {
        /// <summary>
        /// The version this migration moves FROM. (eg: 1 means this script upgrades v1 -> v2)
        /// </summary>
        public abstract int SourceVersion { get; }

        /// <summary>
        /// The version this migration moves TO.
        /// </summary>
        public abstract int TargetVersion { get; }

        /// <summary>
        /// Descriptive name for the UI (eg: "Upgrading Chunk Format...").
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        /// Migrates global metadata (level.dat).
        /// </summary>
        public virtual void MigrateLevelDat(ref string jsonContent)
        {
        }

        /// <summary>
        /// Migrates a specific chunk's raw byte data.
        /// Returns the new byte array.
        /// </summary>
        public virtual byte[] MigrateChunk(byte[] rawOldData)
        {
            return rawOldData; // Default: No change to chunks
        }
    }
}
