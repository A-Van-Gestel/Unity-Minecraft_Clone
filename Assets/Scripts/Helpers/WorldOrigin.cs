using Data;
using Data.WorldTypes;
using UnityEngine;

namespace Helpers
{
    // ============================================================
    // FLOATING ORIGIN — REFERENCE (WS-4)
    // ============================================================
    //
    // Unity render space and voxel world space are two DIFFERENT spaces related by an integer offset.
    // Before WS-4 they were identical; that identity is gone, and mixing them up is a silent bug that
    // only surfaces once the origin leaves (0, 0).
    //
    //   unityPos  = voxelPos - OriginVoxel          (offset is an exact multiple of CHUNK_WIDTH)
    //   voxelCell = FloorToInt(unityPos) + OriginVoxel   (integer add - exact to the +/-2^31 edge)
    //
    // ── THE PRECISION RULE ──────────────────────────────────────
    //   All float math stays in UNITY space (small numbers near the render origin). Voxel world space is
    //   touched only through integer cells or exact multiple-of-CHUNK_WIDTH offsets. Nothing ever adds a
    //   large float to a small float - that is the jitter this type exists to eliminate.
    //
    // ── WHICH SPACE AM I IN? ────────────────────────────────────
    //   Transform.position, camera rays, physics AABBs, mesh vertices  -> UNITY space
    //   World/WorldData queries, VoxelMod.GlobalPosition, ChunkData,
    //   everything persisted, everything under Assets/Scripts/Jobs/    -> VOXEL space
    //
    // ── SCOPE ───────────────────────────────────────────────────
    //   XZ only. Y NEVER shifts, so OriginVoxel.y is always 0 and every helper passes Y through untouched.
    // ============================================================

    /// <summary>
    /// The floating-origin anchor: the chunk whose corner currently maps to the Unity-space origin, plus the
    /// conversion helpers every presentation-layer boundary calls to cross between Unity and voxel space.
    /// <para>
    /// Main-thread / presentation-layer only. Nothing under <c>Assets/Scripts/Jobs/</c> may reference this type:
    /// jobs live in voxel space exclusively and never see the origin. The voxel pipeline (generation, lighting,
    /// meshing), chunk streaming, and every on-disk format are origin-independent by construction.
    /// </para>
    /// </summary>
    public static class WorldOrigin
    {
        /// <summary>
        /// Chebyshev chunk distance from the current anchor at which the world re-anchors on the player.
        /// </summary>
        public const int ShiftThresholdChunks = 64;

        /// <summary>The chunk whose minimum corner currently sits at the Unity-space origin.</summary>
        public static ChunkCoord OriginChunk { get; private set; }

        /// <summary>
        /// The voxel-space coordinate of the Unity-space origin (<see cref="OriginChunk"/>'s corner), cached so the
        /// per-call-site conversions are a bare integer add. Y is always 0 - the origin never shifts vertically.
        /// </summary>
        public static Vector3Int OriginVoxel { get; private set; }

        /// <summary>True while the origin is the identity (0, 0), where Unity and voxel space coincide.</summary>
        public static bool IsIdentity => OriginVoxel.x == 0 && OriginVoxel.z == 0;

        /// <summary>
        /// Re-anchors to the identity before each play session. Required because these are statics: with
        /// "Enter Play Mode without Domain Reload" they would otherwise carry the previous session's origin into a
        /// fresh world, silently offsetting every conversion from the first frame.
        /// </summary>
        // Assigns both statics inline rather than delegating to ResetToIdentity: the domain-reload analyzer only
        // recognizes assignments made directly in the attributed method.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEnter()
        {
            OriginChunk = new ChunkCoord(0, 0);
            OriginVoxel = Vector3Int.zero;
        }

        /// <summary>
        /// Re-anchors the origin. Must be called before any transform is written or chunk created for the new
        /// anchor - callers that place objects read <see cref="OriginVoxel"/> and would otherwise bake in the
        /// stale offset. WS-4a only ever calls this with the identity.
        /// </summary>
        /// <param name="originChunk">The chunk whose corner becomes the new Unity-space origin.</param>
        public static void SetOrigin(ChunkCoord originChunk)
        {
            OriginChunk = originChunk;
            OriginVoxel = new Vector3Int(
                originChunk.X * ChunkMath.CHUNK_WIDTH, 0, originChunk.Z * ChunkMath.CHUNK_WIDTH);
        }

        /// <summary>
        /// Resets the anchor to the identity (0, 0), restoring the pre-WS-4 space identity. Used by world teardown
        /// and by validation fixtures that must not leak an origin into the next suite.
        /// </summary>
        public static void ResetToIdentity() => SetOrigin(new ChunkCoord(0, 0));

        /// <summary>
        /// Whether the world should re-anchor onto <paramref name="playerChunk"/> — true once it is further than
        /// <see cref="ShiftThresholdChunks"/> chunks (Chebyshev) from the current anchor. The policy behind
        /// <c>World.Update</c>'s shift trigger; kept here so the threshold and the test that reads it stay together.
        /// </summary>
        /// <param name="playerChunk">The chunk the player currently occupies (voxel-chunk space).</param>
        /// <returns>True if the world is due a re-anchor this frame.</returns>
        public static bool ShouldReanchor(ChunkCoord playerChunk)
        {
            int dx = Mathf.Abs(playerChunk.X - OriginChunk.X);
            int dz = Mathf.Abs(playerChunk.Z - OriginChunk.Z);
            return Mathf.Max(dx, dz) > ShiftThresholdChunks;
        }

        #region Voxel -> Unity (placing visuals)

        /// <summary>
        /// Converts an absolute voxel cell to the Unity-space position of its minimum corner.
        /// </summary>
        /// <param name="voxelPos">The absolute voxel coordinate.</param>
        /// <returns>The corresponding Unity-space position.</returns>
        public static Vector3 VoxelToUnity(Vector3Int voxelPos)
        {
            return new Vector3(
                voxelPos.x - OriginVoxel.x,
                voxelPos.y,
                voxelPos.z - OriginVoxel.z);
        }

        /// <summary>
        /// Converts a voxel-space XZ origin (as returned by <see cref="ChunkCoord.ToVoxelOrigin"/>) to a Unity-space
        /// position at Y = 0 - the chunk-placement form, matching the shape of <see cref="ChunkCoord.ToWorldPosition"/>.
        /// </summary>
        /// <param name="chunkVoxelPos">The absolute 2D voxel coordinates of a chunk's minimum corner.</param>
        /// <returns>The corresponding Unity-space position, with Y = 0.</returns>
        public static Vector3 VoxelToUnity(Vector2Int chunkVoxelPos)
        {
            return new Vector3(
                chunkVoxelPos.x - OriginVoxel.x,
                0f,
                chunkVoxelPos.y - OriginVoxel.z);
        }

        /// <summary>
        /// Converts a fractional voxel-space position to Unity space. For values that are genuinely fractional in
        /// voxel space (spawn points, save-restored positions); prefer the integer overloads wherever a cell will do.
        /// </summary>
        /// <param name="voxelPos">The fractional voxel-space position.</param>
        /// <returns>The corresponding Unity-space position.</returns>
        public static Vector3 VoxelToUnity(Vector3 voxelPos)
        {
            return new Vector3(
                voxelPos.x - OriginVoxel.x,
                voxelPos.y,
                voxelPos.z - OriginVoxel.z);
        }

        /// <summary>
        /// Converts a persisted chunk-relative position to Unity space — the exact inverse of
        /// <see cref="UnityToRelative"/>, and the <b>only</b> lossless way to restore a saved position.
        /// <para>
        /// Unlike the <see cref="Vector3"/> overload, no large absolute coordinate is ever formed: the chunk distance
        /// resolves in <c>int</c> and only the small local offset is float math, so this is exact to the ±2³¹ edge.
        /// Routing a saved position through <c>ToAbsoluteWorldPosition()</c> instead would round it away past ±2²⁴ —
        /// the precision this format exists to keep.
        /// </para>
        /// </summary>
        /// <param name="voxelPos">The chunk-relative voxel-space position (as persisted).</param>
        /// <returns>The corresponding Unity-space position.</returns>
        public static Vector3 VoxelToUnity(ChunkRelativePosition voxelPos)
        {
            return new Vector3(
                (voxelPos.Chunk.X - OriginChunk.X) * ChunkMath.CHUNK_WIDTH + voxelPos.localPosition.x,
                voxelPos.localPosition.y,
                (voxelPos.Chunk.Z - OriginChunk.Z) * ChunkMath.CHUNK_WIDTH + voxelPos.localPosition.z);
        }

        #endregion

        #region Unity -> Voxel (queries from transforms)

        /// <summary>
        /// Converts a Unity-space position to the absolute voxel cell containing it: floor first (in small Unity
        /// floats, at full precision), then add the integer origin - so the result is exact at any world distance.
        /// </summary>
        /// <param name="unityPos">The Unity-space position (a transform, camera ray point, or AABB corner).</param>
        /// <returns>The absolute voxel cell containing that position.</returns>
        public static Vector3Int UnityToVoxelCell(Vector3 unityPos)
        {
            return new Vector3Int(
                Mathf.FloorToInt(unityPos.x) + OriginVoxel.x,
                Mathf.FloorToInt(unityPos.y),
                Mathf.FloorToInt(unityPos.z) + OriginVoxel.z);
        }

        /// <summary>
        /// Converts a Unity-space position to the voxel-space chunk containing it.
        /// </summary>
        /// <param name="unityPos">The Unity-space position (typically the player transform).</param>
        /// <returns>The chunk coordinate containing that position in voxel space.</returns>
        public static ChunkCoord UnityToChunk(Vector3 unityPos)
        {
            return new ChunkCoord(
                ChunkMath.VoxelToChunk(Mathf.FloorToInt(unityPos.x) + OriginVoxel.x),
                ChunkMath.VoxelToChunk(Mathf.FloorToInt(unityPos.z) + OriginVoxel.z));
        }

        /// <summary>
        /// Converts a Unity-space position to the persistence format, resolving the chunk from the integer origin and
        /// keeping the sub-chunk offset as a small local float - so no large-float round-trip is ever taken.
        /// </summary>
        /// <param name="unityPos">The Unity-space position to persist.</param>
        /// <returns>The equivalent chunk-relative (voxel-space) position.</returns>
        public static ChunkRelativePosition UnityToRelative(Vector3 unityPos)
        {
            return new ChunkRelativePosition(OriginChunk, unityPos);
        }

        #endregion
    }
}
