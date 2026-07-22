namespace Serialization
{
    /// <summary>
    /// Outcome of a chunk save attempt, surfaced to callers so a failure can be reacted to
    /// instead of silently swallowed (CP-6 durability contract).
    /// </summary>
    public enum ChunkSaveResult : byte
    {
        /// <summary>The chunk payload was serialized and written to its region file.</summary>
        Written,

        /// <summary>The save was aborted by cancellation (application quit) before the write
        /// landed. The snapshot is staged into the failed-save retry registry so the quit-time
        /// <see cref="ChunkStorageManager.FlushFailedSavesSync"/> can write it synchronously —
        /// without this, a save canceled after its chunk left <c>ModifiedChunks</c> would lose
        /// the edits silently.</summary>
        Canceled,

        /// <summary>The save threw; nothing reached disk but a later attempt may succeed. The
        /// edits survive only in the serialization snapshot, which the failed-save retry registry
        /// takes ownership of (see <see cref="ChunkStorageManager"/>).</summary>
        Failed,

        /// <summary>The save failed deterministically (serialization produced no payload) —
        /// retrying can never succeed, so the snapshot is released and the loss logged loudly
        /// instead of entering an infinite retry loop.</summary>
        FailedPermanent,
    }
}
