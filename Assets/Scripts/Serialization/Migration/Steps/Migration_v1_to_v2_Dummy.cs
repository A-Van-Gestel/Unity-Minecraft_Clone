using System.Threading;

namespace Serialization.Migration.Steps
{
    /// <summary>
    /// A safe dummy migration used to verify the UI, threading, and backup systems.
    /// It upgrades the world to v2 but does not alter the underlying binary chunk format.
    /// </summary>
    public class MigrationV1ToV2Dummy : WorldMigrationStep
    {
        public override int SourceWorldVersion => 1;
        public override int TargetWorldVersion => 2;

        public override string Description => "Testing Migration UI & Repacking Regions...";
        public override string ChangeSummary => "Dummy migration for testing UI and backup functionality.";

        // Leaving this null tells the manager: "The chunk binary layout hasn't changed in v2."
        // The manager will skip calling MigrateChunk, but will STILL iterate all regions 
        // to re-compress them and update the UI progress bar.
        public override byte? TargetChunkFormatVersion => null;

        public override string MigrateLevelDat(string oldJson)
        {
            // Artificial delay (1 second) so you can clearly see the 
            // "Migrating World Metadata..." phase on your UI.
            Thread.Sleep(1000);
            // throw new Exception("Dummy migration failed!"); // Throw fake exception to test error dialog
            return oldJson;
        }

        public override byte[] MigrateChunk(byte[] uncompressedChunkData)
        {
            // This won't be called because TargetChunkFormatVersion is null,
            // but if it were, we'd just return the data unchanged.
            return uncompressedChunkData;
        }
    }
}
