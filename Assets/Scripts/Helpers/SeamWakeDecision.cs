using Data;
using Jobs.BurstData;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// Decides which voxels to re-register as active when a chunk becomes populated, so behavior that quiesced
    /// against a not-yet-loaded neighbor resumes.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> A read of an unpopulated chunk resolves to <i>void</i>, and a void read
    /// satisfies no spread test — so a fluid whose only flow-receptive direction was a not-yet-loaded neighbor
    /// evaluates inactive and leaves <c>ChunkData.ActiveFluidsBucket</c> on its first tick. Population registers
    /// only the <b>newly populated chunk's own</b> voxels, and the sole cross-chunk wake
    /// (<c>World.ApplyModifications</c> step 4) needs an applied mod 6-adjacent to the sleeping cell — so nothing
    /// re-woke it. See <c>_FIXED_BUGS.md</c> Fluid §19.</para>
    /// <para><b>Why one cell deep is enough.</b> Whether a fluid is active depends only on its ±1 neighbors
    /// (<c>FluidTickJob.IsFluidActive</c>: the below read, the 4 horizontal reads, and
    /// <c>CalculateExpectedFluidLevel</c>'s above/±1 reads). The ≤4-cell flow pathfinder reaches further, but it
    /// only chooses a <i>direction</i> among already-valid ones — it never decides activity. So the wake set is the
    /// 1-deep slab facing the seam, and only on the 4 <b>cardinal</b> sides: a diagonal chunk is never an immediate
    /// neighbor of any cell.</para>
    /// <para><b>Why re-registering indiscriminately is safe.</b> A woken voxel that is still stable simply
    /// evaluates inactive again on the next tick and drops back out of the bucket — the wake cannot leak or
    /// accumulate. This is deliberately family-agnostic (it routes through
    /// <see cref="ChunkData.AddActiveVoxel(Vector3Int, ushort)"/>), so grass — which reads neighbors through the
    /// same <c>GetState</c> path and has the identical gap — is covered by the same pass.</para>
    /// </remarks>
    public static class SeamWakeDecision
    {
        /// <summary>Number of cardinal directions a seam wake considers (diagonals cannot hold an immediate neighbor).</summary>
        public const int CardinalCount = 4;

        // Cardinal neighbor offsets in voxel space, indexed by direction: 0=W(−X), 1=E(+X), 2=S(−Z), 3=N(+Z).
        private static readonly Vector2Int[] s_cardinalOffsets =
        {
            new Vector2Int(-ChunkMath.CHUNK_WIDTH, 0),
            new Vector2Int(ChunkMath.CHUNK_WIDTH, 0),
            new Vector2Int(0, -ChunkMath.CHUNK_WIDTH),
            new Vector2Int(0, ChunkMath.CHUNK_WIDTH),
        };

        /// <summary>Returns the voxel-space origin offset of the cardinal neighbor in the given direction.</summary>
        /// <param name="direction">Direction index: 0=W(−X), 1=E(+X), 2=S(−Z), 3=N(+Z).</param>
        /// <returns>The offset to add to the center chunk's voxel origin.</returns>
        public static Vector2Int NeighborVoxelOffset(int direction) => s_cardinalOffsets[direction];

        /// <summary>Returns the opposite cardinal direction (W↔E, S↔N).</summary>
        /// <param name="direction">Direction index: 0=W(−X), 1=E(+X), 2=S(−Z), 3=N(+Z).</param>
        /// <returns>The direction pointing back.</returns>
        public static int Opposite(int direction) => direction ^ 1;

        /// <summary>
        /// Describes the neighbor-local slab that faces the seam for a given direction — the only cells whose
        /// activity the center chunk's population can change.
        /// </summary>
        /// <param name="direction">Direction index: 0=W(−X), 1=E(+X), 2=S(−Z), 3=N(+Z).</param>
        /// <param name="slabIsOnX">True when the slab is a fixed-X plane; false when it is a fixed-Z plane.</param>
        /// <param name="slabLocal">The neighbor-local X (or Z) of the slab: the face pointing back at the center.</param>
        public static void SeamSlab(int direction, out bool slabIsOnX, out int slabLocal)
        {
            slabIsOnX = direction is 0 or 1;

            // The neighbor's facing slab is its far side relative to the offset: the −X neighbor faces the center
            // with its x=15 plane, the +X neighbor with its x=0 plane.
            slabLocal = direction is 0 or 2 ? ChunkMath.CHUNK_WIDTH - 1 : 0;
        }

        /// <summary>
        /// Re-registers the active-behavior voxels in <paramref name="neighbor"/>'s seam-facing slab that the cells
        /// across the seam in <paramref name="populated"/> could actually affect. Called both ways when a chunk
        /// populates: on a neighbor's slab (work that quiesced against the placeholder) and on the populated chunk's
        /// own slab (cells the registration screen left to this pass).
        /// </summary>
        /// <param name="neighbor">The chunk whose seam slab is woken. Must not be null.</param>
        /// <param name="populated">The chunk across the seam, whose facing slab gates the wake. Must not be null.</param>
        /// <param name="direction">Direction of <paramref name="neighbor"/> from <paramref name="populated"/> (0=W, 1=E, 2=S, 3=N).</param>
        /// <param name="isActiveById">Flat "has ticking behavior" table (<c>World.IsActiveById</c>).</param>
        /// <param name="isSolidById">Flat solidity table (<c>World.IsSolidById</c>).</param>
        /// <param name="blockTypes">The block palette (<c>World.BlockTypes</c>) for the fluid-source test.</param>
        /// <returns>The number of voxels re-registered — 0 when nothing across the seam can receive behavior.</returns>
        /// <remarks>
        /// The two slabs are walked as <b>pairs</b>: a neighbor voxel is only woken when the cell directly across
        /// the seam could change its evaluation. A level-0 fluid source is tested with the registration screen's own
        /// rule (<see cref="ActiveVoxelScreen.CanSourceFlowInto"/>): it acts across a seam only by spreading into that
        /// cell, so a source facing a wall or a source of its own fluid stays asleep — on an ocean seam, every cell.
        /// Every other voxel uses the receive test: a solid cell is a wall to every fluid predicate — exactly what
        /// void already was — so it cannot. The one solid exception is <see cref="BlockIDs.Dirt"/>, which grass
        /// converts (<c>BlockBehavior.IsConvertibleDirt</c>); it is named explicitly because it is the only solid
        /// block any behavior family targets today. <b>Add to this test if a family gains a solid target</b>, or
        /// that family will silently stop waking across seams.
        /// </remarks>
        public static int WakeSeamSlab(ChunkData neighbor, ChunkData populated, int direction,
            bool[] isActiveById, bool[] isSolidById, BlockType[] blockTypes)
        {
            SeamSlab(direction, out bool slabIsOnX, out int slabLocal);

            // The populated chunk faces the neighbor with its opposite plane: neighbor x=15 ↔ populated x=0.
            int populatedSlabLocal = ChunkMath.CHUNK_WIDTH - 1 - slabLocal;

            int woken = 0;

            // Walk section by section so an all-air span costs one null check instead of 256 voxel reads —
            // the common case for the tall empty column above terrain.
            for (int s = 0; s < neighbor.sections.Length; s++)
            {
                ChunkSection section = neighbor.sections[s];
                if (section == null || section.IsEmpty) continue;

                // A null section on the populated side is all air, which receives everything — no per-cell read
                // needed for it, and no skipping possible.
                ChunkSection populatedSection = populated.sections[s];
                int startY = s * ChunkMath.SECTION_SIZE;

                for (int sectionY = 0; sectionY < ChunkMath.SECTION_SIZE; sectionY++)
                for (int across = 0; across < ChunkMath.SECTION_SIZE; across++)
                {
                    int x = slabIsOnX ? slabLocal : across;
                    int z = slabIsOnX ? across : slabLocal;

                    int index = x + sectionY * ChunkMath.SECTION_SIZE +
                                z * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE;

                    uint packed = section.voxels[index];
                    ushort id = BurstVoxelDataBitMapping.GetId(packed);
                    if (id >= isActiveById.Length || !isActiveById[id]) continue;

                    if (populatedSection != null)
                    {
                        if (IsFluidSource(packed, id, blockTypes))
                        {
                            if (!SourceCanFlowAcross(populatedSection, populatedSlabLocal, slabIsOnX, sectionY, across, id, blockTypes))
                                continue;
                        }
                        // Same-Y sample first: it is the common admit and costs one raw array index. Only when it
                        // rejects do we pay for the y+1 sample (grass's up-diagonal target), so the extra read lands
                        // exclusively on cells we were about to skip.
                        else if (!CanReceiveBehavior(populatedSection, populatedSlabLocal, slabIsOnX, sectionY, across, isSolidById) &&
                                 !CanReceiveGrassAbove(populated, populatedSlabLocal, slabIsOnX, startY + sectionY, across, isSolidById))
                        {
                            continue;
                        }
                    }

                    neighbor.AddActiveVoxel(new Vector3Int(x, startY + sectionY, z), id);
                    woken++;
                }
            }

            return woken;
        }

        private static bool IsFluidSource(uint packed, ushort id, BlockType[] blockTypes) =>
            id < blockTypes.Length && blockTypes[id].fluidType != FluidType.None &&
            BurstVoxelDataBitMapping.GetFluidLevel(packed) == 0;

        /// <summary>Whether a level-0 source could spread into (or stay active against) the cell across the seam.</summary>
        private static bool SourceCanFlowAcross(ChunkSection populatedSection, int populatedSlabLocal, bool slabIsOnX,
            int sectionY, int across, ushort sourceId, BlockType[] blockTypes)
        {
            int px = slabIsOnX ? populatedSlabLocal : across;
            int pz = slabIsOnX ? across : populatedSlabLocal;

            uint pPacked = populatedSection.voxels[px + sectionY * ChunkMath.SECTION_SIZE +
                                                   pz * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE];
            ushort pId = BurstVoxelDataBitMapping.GetId(pPacked);
            if (pId >= blockTypes.Length) return true; // unknown id — wake rather than silently skip

            BlockType target = blockTypes[pId];
            return ActiveVoxelScreen.CanSourceFlowInto(sourceId, pId, BurstVoxelDataBitMapping.GetFluidLevel(pPacked),
                target.isSolid, (target.tags & BlockTags.REPLACEABLE) != 0);
        }

        /// <summary>
        /// Returns true when the cell across the seam could change an adjacent voxel's behavior evaluation — i.e.
        /// it is not an inert wall. See <see cref="WakeSeamSlab"/>'s remarks for why Dirt is the one solid
        /// exception.
        /// </summary>
        private static bool CanReceiveBehavior(ChunkSection populatedSection, int populatedSlabLocal,
            bool slabIsOnX, int sectionY, int across, bool[] isSolidById)
        {
            int px = slabIsOnX ? populatedSlabLocal : across;
            int pz = slabIsOnX ? across : populatedSlabLocal;

            int pIndex = px + sectionY * ChunkMath.SECTION_SIZE +
                         pz * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE;

            ushort pId = BurstVoxelDataBitMapping.GetId(populatedSection.voxels[pIndex]);
            if (pId >= isSolidById.Length) return true; // unknown id — wake rather than silently skip
            return !isSolidById[pId] || pId == BlockIDs.Dirt;
        }

        /// <summary>
        /// Second gate sample, one cell <b>above</b> the across-seam cell: grass's up-diagonal spread target
        /// (<c>s_grassSpreadVectors</c>' "Above Adjacent" entries → <c>IsConvertibleDirt(pos + dir + up)</c>).
        /// That target is <see cref="BlockIDs.Dirt"/>, which is solid, so the same-Y sample alone would skip a
        /// grass voxel whose only opening is diagonally up across the seam.
        /// </summary>
        /// <remarks>
        /// Only y and y+1 are sampled. The third grass path (<c>IsDirtNextToAir</c>) needs <i>air</i> at the
        /// same Y with dirt at y−1, and that air already admits at the same-Y sample — so y−1 needs no sample of
        /// its own. <c>s_grassSpreadVectors</c> has no down-diagonal entry.
        /// <para>Reads through <see cref="ChunkData.GetVoxel"/> rather than a raw section index because y+1 can
        /// cross into the next section (or a null one); it runs only on the reject path, so the cost is bounded
        /// to cells that were about to be skipped.</para>
        /// </remarks>
        private static bool CanReceiveGrassAbove(ChunkData populated, int populatedSlabLocal, bool slabIsOnX,
            int globalY, int across, bool[] isSolidById)
        {
            int aboveY = globalY + 1;
            if (aboveY >= ChunkMath.CHUNK_HEIGHT) return false; // no cell above the world's top row

            int px = slabIsOnX ? populatedSlabLocal : across;
            int pz = slabIsOnX ? across : populatedSlabLocal;

            ushort pId = BurstVoxelDataBitMapping.GetId(populated.GetVoxel(px, aboveY, pz));
            if (pId >= isSolidById.Length) return true; // unknown id — wake rather than silently skip
            return !isSolidById[pId] || pId == BlockIDs.Dirt;
        }
    }
}
