using Data;
using Data.Enums;
using Helpers;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Jobs
{
    /// <summary>
    /// Single-threaded Burst pass that finds the flowing fluid around the listener and accumulates it into
    /// the world-anchored emitter bin grid (SOUND_ENGINE_DESIGN.md §5.2).
    /// </summary>
    /// <remarks>
    /// <para>Reads a main-thread snapshot of the chunk sections worth scanning — those whose
    /// <see cref="ChunkSection.emitterFluidCount"/> is positive — and never touches live chunk data or the
    /// fluid simulation. Produce-on-worker / consume-on-main: the caller completes the handle and reads
    /// <see cref="Bins"/>.</para>
    /// <para><b>Parity invariant:</b> the sounding test below and the managed
    /// <see cref="FluidBlockLookup.IsEmitterFluid"/> that maintains the section counts are two
    /// implementations of one decision — including its water/lava asymmetry. If they disagree, a section
    /// holding a sounding fluid can report zero and never be snapshotted at all — silence, with nothing to
    /// see. Change both, or neither.</para>
    /// </remarks>
    [BurstCompile]
    public struct FluidEmitterScanJob : IJob
    {
        /// <summary>Squared audible radius, compared against the squared listener distance.</summary>
        private const int RADIUS_SQ = FluidEmitterScanGeometry.RadiusXZ * FluidEmitterScanGeometry.RadiusXZ;

        /// <summary>Snapshotted section voxels, <see cref="ChunkMath.SECTION_VOLUME"/> entries per section.</summary>
        [ReadOnly]
        public NativeArray<uint> Sections;

        /// <summary>Voxel-space low corner of each snapshotted section, parallel to <see cref="Sections"/>.</summary>
        [ReadOnly]
        public NativeArray<int3> SectionOrigins;

        /// <summary>How many entries of <see cref="SectionOrigins"/> this run should read.</summary>
        public int SectionCount;

        /// <summary>Global block-type lookup; <see cref="BlockTypeJobData.FluidType"/> drives the kind.</summary>
        [ReadOnly]
        public NativeArray<BlockTypeJobData> BlockTypes;

        /// <summary>The listener's voxel cell — the center of the spherical cull.</summary>
        public int3 ListenerVoxel;

        /// <summary>The bin grid's world-snapped origin, from <see cref="FluidEmitterScanGeometry.BinOrigin"/>.</summary>
        public int3 BinOrigin;

        /// <summary>
        /// The accumulation grid, <see cref="FluidEmitterScanGeometry.BinCount"/> long. Cleared by the job
        /// itself, so the caller never pays for the memset on the main thread. Not <c>[WriteOnly]</c>: the
        /// accumulate is a read-modify-write of the bin it lands in.
        /// </summary>
        public NativeArray<FluidEmitterBin> Bins;

        /// <inheritdoc />
        public void Execute()
        {
            for (int i = 0; i < Bins.Length; i++) Bins[i] = default;

            for (int s = 0; s < SectionCount; s++)
            {
                int3 origin = SectionOrigins[s];
                int baseIndex = s * ChunkMath.SECTION_VOLUME;

                // Section layout is x + localY * 16 + z * 256 (ChunkData.SetVoxel's index math), so z is the
                // outermost axis here — walked in storage order to keep the read linear.
                for (int z = 0; z < ChunkMath.SECTION_SIZE; z++)
                for (int y = 0; y < ChunkMath.SECTION_SIZE; y++)
                for (int x = 0; x < ChunkMath.SECTION_SIZE; x++)
                {
                    uint packed = Sections[baseIndex + x + y * ChunkMath.SECTION_SIZE +
                                           z * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE];

                    if ((packed & BurstVoxelDataBitMapping.ID_MASK) == 0) continue;

                    ushort id = BurstVoxelDataBitMapping.GetId(packed);
                    if (id >= BlockTypes.Length) continue;

                    FluidType fluid = BlockTypes[id].FluidType;
                    if (fluid == FluidType.None) continue;

                    // Water only sounds when it moves — a still lake is the ambience bed's job. Lava sounds in
                    // every state: there is no lava bed, and it is a hazard worth hearing before seeing.
                    byte level = BurstVoxelDataBitMapping.GetFluidLevel(packed);
                    if (fluid == FluidType.WaterLike && level == 0) continue;

                    int3 voxel = origin + new int3(x, y, z);

                    // Spherical cull inside the box: the box corners are 1.7x the radius away, and an
                    // emitter that far off would fade in before the listener could plausibly hear it.
                    int3 delta = voxel - ListenerVoxel;
                    if (math.lengthsq(delta) > RADIUS_SQ) continue;

                    int kind = (int)(fluid == FluidType.WaterLike
                        ? BurstVoxelDataBitMapping.IsFluidFalling(level)
                            ? FluidEmitterKind.WaterFall
                            : FluidEmitterKind.WaterFlow
                        : BurstVoxelDataBitMapping.IsFluidFalling(level)
                            ? FluidEmitterKind.LavaFall
                            : FluidEmitterKind.LavaFlow);

                    int3 bin = (voxel - BinOrigin) >> FluidEmitterScanGeometry.BinShift;
                    int binIndex = FluidEmitterScanGeometry.BinIndex(bin, kind);
                    if (binIndex < 0) continue;

                    // NativeArray of a struct has no by-ref element access outside unsafe code, so the
                    // accumulate is a read-modify-write. Single-threaded, so it needs no atomics.
                    FluidEmitterBin cell = Bins[binIndex];
                    cell.Weight++;
                    cell.SumPos += voxel;
                    Bins[binIndex] = cell;
                }
            }
        }
    }
}
