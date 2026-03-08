using System.IO;

namespace Serialization.Migration
{
    /// <summary>
    /// Represents a complete, atomic transition from one World Version to the next.
    /// Each concrete subclass defines exactly what changes between two versions.
    /// Naming convention: Migration_v{Source}_to_v{Target}_{ShortDescription}
    /// </summary>
    public abstract class WorldMigrationStep
    {
        /// <summary>The world version this step upgrades from.</summary>
        public abstract int SourceWorldVersion { get; }

        /// <summary>The world version this step upgrades to.</summary>
        public abstract int TargetWorldVersion { get; }

        /// <summary>
        /// Human-readable description shown in the migration progress UI.
        /// Example: "Upgrading chunk lighting format..."
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        /// A short, user-facing description of what this migration fixes or changes.
        /// Displayed in the UI before the user chooses to load/migrate the world.
        /// </summary>
        public abstract string ChangeSummary { get; }

        // ── Chunk Format Migration ────────────────────────────────────────────

        /// <summary>
        /// Declares the chunk format version this step writes as output.
        /// Return null if this world version bump does not alter the chunk binary layout.
        /// </summary>
        /// <remarks>
        /// ⚠️ If you return a non-null value here, you MUST override <see cref="MigrateChunk"/>
        /// and write the new version byte as the FIRST byte of the returned array.
        /// Failure to do so will cause an <see cref="InvalidDataException"/> at runtime,
        /// which is intentional fail-fast behavior during development.
        /// </remarks>
        public virtual byte? TargetChunkFormatVersion => null;

        /// <summary>
        /// Migrates the full JSON content of level.dat.
        /// After all steps run, the manager stamps the final version number — do not set it here.
        /// </summary>
        public virtual string MigrateLevelDat(string oldJson) => oldJson;

        /// <summary>
        /// Migrates the raw bytes of pending_mods.bin.
        /// </summary>
        public virtual byte[] MigratePendingMods(byte[] rawOldData) => rawOldData;

        /// <summary>
        /// Migrates the raw bytes of lighting_pending.bin.
        /// </summary>
        public virtual byte[] MigratePendingLighting(byte[] rawOldData) => rawOldData;

        /// <summary>
        /// Migrates a single UNCOMPRESSED chunk payload.
        /// The manager handles decompression before calling this and recompression after.
        /// Only called when TargetChunkFormatVersion is non-null AND the chunk's current
        /// version byte is less than TargetChunkFormatVersion.
        /// </summary>
        public virtual byte[] MigrateChunk(byte[] uncompressedChunkData)
        {
            return uncompressedChunkData;
        }

        // ── Region Layout Migration ───────────────────────────────────────────

        /// <summary>
        /// Returns true if this migration step requires a full restructure of the
        /// region file layout — i.e. chunks need to move to different region files
        /// or different slots within a region file.
        ///
        /// <para>
        /// When true, <see cref="PerformRegionLayoutMigration"/> is called instead of
        /// the standard per-file loop. The step is responsible for reading all old
        /// region files from <c>oldRegionPath</c> and writing the correctly-addressed
        /// chunks into <c>newRegionPath</c>.
        /// </para>
        ///
        /// <para>
        /// Example use case: fixing a coordinate scale bug where chunks were stored
        /// using voxel-space positions instead of chunk-index positions.
        /// </para>
        /// </summary>
        public virtual bool RequiresRegionLayoutMigration => false;

        /// <summary>
        /// Called once for the entire region folder when <see cref="RequiresRegionLayoutMigration"/>
        /// is true. Responsible for reading every chunk from <paramref name="oldRegionPath"/>
        /// and writing each one — with its corrected region address — into <paramref name="newRegionPath"/>.
        ///
        /// <para>
        /// The manager will atomically swap the two directories after this method returns,
        /// so the step must write ALL chunks before returning.
        /// </para>
        ///
        /// <para>
        /// Compression: the step should recompress all chunk payloads to
        /// <paramref name="targetCompression"/> to normalise the save on disk.
        /// </para>
        /// </summary>
        /// <param name="oldRegionPath">Path to the existing (pre-migration) Region folder.</param>
        /// <param name="newRegionPath">Path to the freshly-created temporary Region folder to write into.</param>
        /// <param name="targetCompression">The player's currently configured compression algorithm.</param>
        /// <returns>The number of chunks successfully processed.</returns>
        public virtual int PerformRegionLayoutMigration(
            string oldRegionPath,
            string newRegionPath,
            CompressionAlgorithm targetCompression)
        {
            return 0;
        }
    }
}
