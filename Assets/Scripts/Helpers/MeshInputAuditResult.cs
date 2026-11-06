namespace Helpers
{
    /// <summary>
    /// What one mesh-input audit saw: how many visible meshes it compared against live inputs, why the others were
    /// skipped, and how many no longer match. Diagnostics only.
    /// </summary>
    public struct MeshInputAuditResult
    {
        /// <summary>Recorded meshes compared against their live inputs.</summary>
        public int Audited;

        /// <summary>Of <see cref="Audited"/>, those whose live inputs differ from the snapshot their mesh was built from.</summary>
        public int Mismatched;

        /// <summary>Records whose chunk was unloaded or recycled since its mesh was scheduled.</summary>
        public int SkippedGone;

        /// <summary>Records whose chunk has no active visual chunk.</summary>
        public int SkippedInactive;

        /// <summary>Records whose chunk is queued for or in the middle of a re-mesh, which will refresh its inputs.</summary>
        public int SkippedPending;

        /// <summary>Records whose chunk lacks a mesh-ready neighbor, so its seam inputs cannot be read live.</summary>
        public int SkippedNeighbors;

        /// <summary>A one-line summary.</summary>
        /// <returns>The counts, labeled.</returns>
        public override string ToString() =>
            $"audited {Audited}, mismatched {Mismatched}; skipped: gone {SkippedGone}, inactive {SkippedInactive}, " +
            $"re-mesh pending {SkippedPending}, neighbors not ready {SkippedNeighbors}";
    }
}
