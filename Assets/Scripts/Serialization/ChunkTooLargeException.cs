using System;

namespace Serialization
{
    /// <summary>
    /// Thrown by <see cref="RegionFile.SaveChunkData"/> when a chunk's compressed payload exceeds the
    /// region format's 255-sector (~1 MB) record limit. Deterministic — retrying can never succeed —
    /// so every save path maps it to <see cref="ChunkSaveResult.FailedPermanent"/> (released loudly,
    /// never enters the retry registry) instead of the false <c>Written</c> the pre-CP-3 swallow produced.
    /// </summary>
    public sealed class ChunkTooLargeException : Exception
    {
        /// <summary>Creates the exception with a descriptive message.</summary>
        /// <param name="message">Description including the offending record size.</param>
        public ChunkTooLargeException(string message) : base(message)
        {
        }
    }
}
