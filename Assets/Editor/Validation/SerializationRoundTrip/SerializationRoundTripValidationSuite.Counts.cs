using Data;
using Data.JobData;
using Editor.Validation.Framework;
using Helpers;
using Jobs.BurstData;
using Serialization;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Part 6 of the suite: the per-section block counts a load recomputes on its worker thread. The format
    /// persists only <c>nonAirCount</c>; the opaque, emissive and flowing-fluid counts are derived state that
    /// <c>LoadChunkAsync</c> rebuilds from a flat opacity table when one is passed.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        private const string BLOCK_DATABASE_PATH = "Assets/Resources/Data/BlockDatabase.asset";

        // Test-local palette ids (same rationale as the fixture ids above).
        private const ushort COUNT_ID_AIR = 0;
        private const ushort COUNT_ID_OPAQUE = 1;
        private const ushort COUNT_ID_GLASS = 2;
        private const ushort COUNT_ID_LAMP = 3;
        private const ushort COUNT_ID_WATER = 4;
        private const int COUNT_PALETTE_SIZE = 5;

        private const int COUNT_SECTION_ALL_OPAQUE = 0;
        private const int COUNT_SECTION_MIXED = 1;
        private const int COUNT_SECTION_EMISSIVE = 2;
        private const int COUNT_SECTION_FLUID = 3;
        private const int COUNT_SECTION_NULL = 5;

        // Hand-derived voxel counts per section (see BuildCountsChunk).
        private const int MIXED_OPAQUE_VOXELS = 1000;
        private const int MIXED_GLASS_VOXELS = 500;
        private const int MIXED_LIGHT_ONLY_AIR_VOXELS = 100;
        private const int EMISSIVE_LAMP_VOXELS = 7;
        private const int EMISSIVE_GLASS_VOXELS = 3;
        private const int FLUID_FLOWING_VOXELS = 5;
        private const int FLUID_SOURCE_VOXELS = 2;
        private const byte FLOWING_FLUID_LEVEL = 3;

        /// <summary>
        /// B18. Red when: the load worker's recount differs from a main-thread recount (opaque, emissive or
        /// flowing-fluid miscounted, light-only air counted as non-air, generation not stamped), the recount is
        /// skipped, or a load without a table starts deriving counts the format does not persist.
        /// </summary>
        /// <returns>True when worker-recounted counts match the hand-derived and main-thread values.</returns>
        private static bool WorkerRecountMatchesMainThreadCounts()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(32, 48);
            ChunkPoolBalance balance = ChunkPoolBalance.Capture();

            BlockType[] palette = BuildCountsPalette();
            EmissiveBlockLookup.Initialize(palette);
            FluidBlockLookup.Initialize(palette);
            bool[] opacityTable = JobDataManagerFactory.BuildIsOpaqueById(palette);

            ChunkData source = BuildCountsChunk(pos, opacityTable);
            ChunkData withTable = null;
            ChunkData withoutTable = null;
            bool ok;
            try
            {
                ok = Check("the chunk saves", RunSave(fx.Storage, source) == ChunkSaveResult.Written);

                withTable = System.Threading.Tasks.Task.Run(() => fx.Storage.LoadChunkAsync(pos, opacityTable))
                    .GetAwaiter().GetResult();
                withoutTable = RunLoad(fx.Storage, pos);
                if (withTable == null || withoutTable == null)
                    return Check("both loads return the chunk", false);

                ok &= AssertHandDerivedCounts(source, "source");
                ok &= AssertHandDerivedCounts(withTable, "worker-recounted load");

                for (int i = 0; i < source.sections.Length; i++)
                {
                    ChunkSection expected = source.sections[i];
                    ChunkSection got = withTable.sections[i];
                    if (expected == null)
                    {
                        ok &= Check($"section {i.ToString()}: a never-attached slot stays null on load", got == null);
                        continue;
                    }

                    if (got == null)
                    {
                        ok &= Check($"section {i.ToString()}: the section is loaded", false);
                        continue;
                    }

                    ok &= Check($"section {i.ToString()}: worker counts equal the main-thread counts " +
                                $"(non-air {got.nonAirCount.ToString()}/{expected.nonAirCount.ToString()}, " +
                                $"opaque {got.opaqueCount.ToString()}/{expected.opaqueCount.ToString()}, " +
                                $"emissive {got.emissiveCount.ToString()}/{expected.emissiveCount.ToString()}, " +
                                $"fluid {got.emitterFluidCount.ToString()}/{expected.emitterFluidCount.ToString()}, " +
                                $"generation {got.emitterFluidGeneration.ToString()}/{expected.emitterFluidGeneration.ToString()})",
                        got.nonAirCount == expected.nonAirCount && got.opaqueCount == expected.opaqueCount
                        && got.emissiveCount == expected.emissiveCount
                        && got.emitterFluidCount == expected.emitterFluidCount
                        && got.emitterFluidGeneration == expected.emitterFluidGeneration);

                    ChunkSection bare = withoutTable.sections[i];
                    if (bare == null)
                    {
                        ok &= Check($"section {i.ToString()}: the table-less load carries the section", false);
                        continue;
                    }

                    ok &= Check($"section {i.ToString()}: a table-less load keeps only the persisted non-air count " +
                                $"(non-air {bare.nonAirCount.ToString()}, opaque {bare.opaqueCount.ToString()}, " +
                                $"emissive {bare.emissiveCount.ToString()}, fluid {bare.emitterFluidCount.ToString()}, " +
                                $"generation {bare.emitterFluidGeneration.ToString()})",
                        bare.nonAirCount == expected.nonAirCount && bare.opaqueCount == 0 && bare.emissiveCount == 0
                        && bare.emitterFluidCount == 0 && bare.emitterFluidGeneration == 0);
                }
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(source);
                if (withTable != null) World.Instance.ChunkPool.ReturnChunkData(withTable);
                if (withoutTable != null) World.Instance.ChunkPool.ReturnChunkData(withoutTable);
                RestoreShippedLookups();
            }

            ok &= balance.AssertUnchanged("pools balanced after the counts round trip");
            return ok;
        }

        /// <summary>Builds the five-id palette: air, opaque, non-opaque solid, emissive opaque, water-like fluid.</summary>
        /// <returns>The palette, indexed by the <c>COUNT_ID_*</c> ids.</returns>
        private static BlockType[] BuildCountsPalette()
        {
            BlockType[] palette = new BlockType[COUNT_PALETTE_SIZE];
            for (int id = 0; id < palette.Length; id++)
                palette[id] = new BlockType { blockName = $"Counts {id.ToString()}", opacity = 15 };

            palette[COUNT_ID_AIR].opacity = 0;
            palette[COUNT_ID_GLASS].opacity = 0;
            palette[COUNT_ID_GLASS].isSolid = true;
            palette[COUNT_ID_LAMP].lightEmission = 15;
            palette[COUNT_ID_WATER].opacity = 0;
            palette[COUNT_ID_WATER].fluidType = FluidType.WaterLike;
            return palette;
        }

        /// <summary>
        /// Builds a chunk of hand-countable sections (null slots stay null) and stamps each section's counts
        /// with a main-thread recount, which is the oracle the worker path is compared to.
        /// </summary>
        /// <param name="pos">Chunk position.</param>
        /// <param name="opacityTable">The flat opacity lookup to recount with.</param>
        /// <returns>A pooled chunk the caller must return.</returns>
        private static ChunkData BuildCountsChunk(Vector2Int pos, bool[] opacityTable)
        {
            ChunkData data = World.Instance.ChunkPool.GetChunkData(pos);
            AttachFilledSection(data, COUNT_SECTION_ALL_OPAQUE, COUNT_ID_OPAQUE, ChunkMath.SECTION_VOLUME, 0);

            // Mixed: opaque + non-opaque solids + light-only air (id 0 carrying meta and light, NOT non-air).
            ChunkSection mixed = AttachFilledSection(data, COUNT_SECTION_MIXED, COUNT_ID_OPAQUE, MIXED_OPAQUE_VOXELS, 0);
            int offset = MIXED_OPAQUE_VOXELS;
            FillRun(mixed, offset, MIXED_GLASS_VOXELS, BurstVoxelDataBitMapping.PackVoxelData(COUNT_ID_GLASS, 0));
            offset += MIXED_GLASS_VOXELS;
            FillRun(mixed, offset, MIXED_LIGHT_ONLY_AIR_VOXELS,
                BurstVoxelDataBitMapping.PackVoxelData(COUNT_ID_AIR, FIXTURE_META));
            StampVariedLight(mixed);

            ChunkSection emissive = AttachFilledSection(data, COUNT_SECTION_EMISSIVE, COUNT_ID_LAMP, EMISSIVE_LAMP_VOXELS, 0);
            FillRun(emissive, EMISSIVE_LAMP_VOXELS, EMISSIVE_GLASS_VOXELS,
                BurstVoxelDataBitMapping.PackVoxelData(COUNT_ID_GLASS, 0));

            ChunkSection fluid = AttachFilledSection(data, COUNT_SECTION_FLUID, COUNT_ID_WATER, FLUID_FLOWING_VOXELS,
                FLOWING_FLUID_LEVEL);
            FillRun(fluid, FLUID_FLOWING_VOXELS, FLUID_SOURCE_VOXELS, BurstVoxelDataBitMapping.PackVoxelData(COUNT_ID_WATER, 0));

            foreach (ChunkSection section in data.sections)
                section?.RecalculateCounts(opacityTable);

            return data;
        }

        /// <summary>Attaches a pooled section to a slot and fills its first voxels with one block.</summary>
        /// <param name="data">The chunk being built.</param>
        /// <param name="sectionIndex">The slot to attach to.</param>
        /// <param name="blockId">The test-local id to write.</param>
        /// <param name="count">How many leading voxels to fill.</param>
        /// <param name="fluidLevel">The fluid level nibble (0 for none).</param>
        /// <returns>The attached section.</returns>
        private static ChunkSection AttachFilledSection(ChunkData data, int sectionIndex, ushort blockId, int count, byte fluidLevel)
        {
            ChunkSection section = World.Instance.ChunkPool.GetChunkSection();
            data.sections[sectionIndex] = section;
            byte meta = fluidLevel > 0 ? BurstVoxelDataBitMapping.BuildMetaLegacy(0, fluidLevel, true) : (byte)0;
            FillRun(section, 0, count, BurstVoxelDataBitMapping.PackVoxelData(blockId, meta));
            return section;
        }

        /// <summary>Writes one packed value into a run of a section's voxels.</summary>
        /// <param name="section">The section to write.</param>
        /// <param name="start">The first voxel index.</param>
        /// <param name="count">The run length.</param>
        /// <param name="packed">The packed voxel value.</param>
        private static void FillRun(ChunkSection section, int start, int count, uint packed)
        {
            for (int i = start; i < start + count; i++) section.voxels[i] = packed;
        }

        /// <summary>Asserts every section's counts against the numbers derived by hand from <see cref="BuildCountsChunk"/>.</summary>
        /// <param name="chunk">The chunk to check.</param>
        /// <param name="label">The chunk's label for the failure line.</param>
        /// <returns>True when all four populated sections and the null slot match.</returns>
        private static bool AssertHandDerivedCounts(ChunkData chunk, string label)
        {
            bool ok = true;
            ok &= AssertSectionCounts(chunk, COUNT_SECTION_ALL_OPAQUE, label,
                ChunkMath.SECTION_VOLUME, ChunkMath.SECTION_VOLUME, 0, 0);
            ok &= AssertSectionCounts(chunk, COUNT_SECTION_MIXED, label,
                MIXED_OPAQUE_VOXELS + MIXED_GLASS_VOXELS, MIXED_OPAQUE_VOXELS, 0, 0);
            ok &= AssertSectionCounts(chunk, COUNT_SECTION_EMISSIVE, label,
                EMISSIVE_LAMP_VOXELS + EMISSIVE_GLASS_VOXELS, EMISSIVE_LAMP_VOXELS, EMISSIVE_LAMP_VOXELS, 0);
            ok &= AssertSectionCounts(chunk, COUNT_SECTION_FLUID, label,
                FLUID_FLOWING_VOXELS + FLUID_SOURCE_VOXELS, 0, 0, FLUID_FLOWING_VOXELS);
            ok &= Check($"{label}: the never-attached slot is null", chunk.sections[COUNT_SECTION_NULL] == null);
            return ok;
        }

        /// <summary>Asserts one section's four counts and a current fluid generation.</summary>
        /// <param name="chunk">The chunk to check.</param>
        /// <param name="index">The section slot.</param>
        /// <param name="label">The chunk's label for the failure line.</param>
        /// <param name="nonAir">Expected non-air count.</param>
        /// <param name="opaque">Expected opaque count.</param>
        /// <param name="emissive">Expected emissive count.</param>
        /// <param name="fluid">Expected flowing-fluid count.</param>
        /// <returns>True when all match.</returns>
        private static bool AssertSectionCounts(ChunkData chunk, int index, string label, int nonAir, int opaque,
            int emissive, int fluid)
        {
            ChunkSection s = chunk.sections[index];
            if (s == null) return Check($"{label} section {index.ToString()}: present", false);

            return Check($"{label} section {index.ToString()}: hand-derived counts " +
                         $"(non-air {s.nonAirCount.ToString()}/{nonAir.ToString()}, opaque {s.opaqueCount.ToString()}/{opaque.ToString()}, " +
                         $"emissive {s.emissiveCount.ToString()}/{emissive.ToString()}, fluid {s.emitterFluidCount.ToString()}/{fluid.ToString()}, " +
                         $"generation {s.emitterFluidGeneration.ToString()}/{FluidBlockLookup.Generation.ToString()})",
                s.nonAirCount == nonAir && s.opaqueCount == opaque && s.emissiveCount == emissive
                && s.emitterFluidCount == fluid && s.emitterFluidGeneration == FluidBlockLookup.Generation);
        }

        /// <summary>Re-binds the emissive and fluid lookups to the shipped palette, so the fixture palette cannot leak into a later scenario.</summary>
        private static void RestoreShippedLookups()
        {
            BlockDatabase database = AssetDatabase.LoadAssetAtPath<BlockDatabase>(BLOCK_DATABASE_PATH);
            if (database?.blockTypes == null) return;

            EmissiveBlockLookup.Initialize(database.blockTypes);
            FluidBlockLookup.Initialize(database.blockTypes);
        }
    }
}
