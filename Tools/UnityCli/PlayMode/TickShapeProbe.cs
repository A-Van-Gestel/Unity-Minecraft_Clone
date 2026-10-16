// Behavior-tick input probe for the Unity CLI: what the active-voxel buckets of a live world hold right now, and what
// the tick's snapshot copies for them.
//
// RUN     unity command run_script --file Tools/UnityCli/PlayMode/TickShapeProbe.cs \
//             --entry UnityCli.TickShapeProbe.Measure --args '[]' --result-only
//         Compiled in memory on each call, never imported by Unity.
// NEEDS   A loaded World scene in Play mode (WorldRig.Launch). Sample right after a teleport onto new terrain to see a
//         generation burst, and again a tick or two later to see what the first tick dropped.
// TRAP    The buckets change every tick (1 s), so two samples a few seconds apart are different ticks; read `tick=`.
//
// AUDIT   --entry UnityCli.TickShapeProbe.AuditSleepers — the registration contract on the live world: no unregistered
//         active-type voxel may be one the tick would still act on (SLEEPERS=0). Refuses while modifications are queued.
//
// Origin: ES-29's probe (an ocean teleport registered 702 k fluid voxels; 3.3 k remained a tick later). The "screenable"
// lines count registered voxels the ES-31 screen (ActiveVoxelScreen, in-chunk reads only) would drop — after the screen
// shipped, mostly edge voxels the population seam wake registered.

using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Data;
using Editor.Validation.Behavior.Framework;
using Helpers;
using Jobs.BurstData;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityCli
{
    /// <summary>Entry points for measuring the behavior tick's input on a live world through <c>run_script</c>.</summary>
    public static class TickShapeProbe
    {
        // The tick's read reach below and above its active voxels (FluidBurstTicker / GrassBurstTicker).
        private const int FLUID_REACH = 1;
        private const int GRASS_REACH_DOWN = 1;
        private const int GRASS_REACH_UP = 2;
        private const int BYTES_PER_KB = 1024;
        private const int SECTION_KB = ChunkMath.SECTION_VOLUME * sizeof(uint) / BYTES_PER_KB;
        private const int MAP_KB = ChunkMath.CHUNK_VOLUME * sizeof(uint) / BYTES_PER_KB;
        private const int W = ChunkMath.CHUNK_WIDTH;
        private const int MAX_SECTIONS_HISTOGRAM = ChunkMath.SECTIONS_PER_CHUNK;
        private const int MAX_LISTED_SLEEPERS = 20;

        private static readonly Vector2Int[] s_neighborOffsets =
        {
            new Vector2Int(-W, 0), new Vector2Int(W, 0), new Vector2Int(0, -W), new Vector2Int(0, W),
            new Vector2Int(-W, -W), new Vector2Int(-W, W), new Vector2Int(W, -W), new Vector2Int(W, W),
        };

        private static readonly Vector3Int[] s_cardinals =
        {
            new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1), new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0),
        };

        /// <summary>
        /// Measures the ticking chunks' active buckets: fluid and grass voxel counts, the fluid bands' section depth, what
        /// the snapshot would copy as full maps, as bands and as bands copied once per chunk (the shipped arena), the
        /// share of active voxels in chunk-edge columns, and how many the registration screen would drop.
        /// </summary>
        /// <returns>A multi-line report.</returns>
        public static string Measure()
        {
            World world = Object.FindAnyObjectByType<World>();
            if (world == null || !Application.isPlaying) return "no World in Play mode";

            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            IEnumerable active = (IEnumerable)typeof(World).GetField("_activeChunks", flags).GetValue(world);
            IDictionary chunkMap = (IDictionary)typeof(World).GetField("_chunkMap", flags).GetValue(world);
            BlockType[] blockTypes = world.BlockTypes;

            int activeChunks = 0, fluidChunks = 0, grassChunks = 0;
            long fluidVoxels = 0, grassVoxels = 0, fluidEdge = 0, grassEdge = 0, fluidScreenable = 0, grassScreenable = 0;
            long fullMaps = 0, bandedFluid = 0, bandedGrass = 0;
            int[] bandHistogram = new int[MAX_SECTIONS_HISTOGRAM + 1];
            HashSet<long> uniqueFluid = new HashSet<long>();
            HashSet<long> uniqueAll = new HashSet<long>();

            foreach (object coord in active)
            {
                if (!chunkMap.Contains(coord)) continue;
                ChunkData cd = ((Chunk)chunkMap[coord]).ChunkData;
                if (cd == null) continue;
                activeChunks++;
                ChunkDataScreenSource screen = new ChunkDataScreenSource(cd, blockTypes);

                NativeHashSet<int> fluids = Bucket(cd, "ActiveFluidsBucket");
                if (fluids.IsCreated && fluids.Count > 0)
                {
                    fluidChunks++;
                    YRange(fluids, out int min, out int max);
                    int first = Mathf.Max(0, min - FLUID_REACH) / ChunkMath.SECTION_SIZE;
                    int last = Mathf.Min(ChunkMath.CHUNK_HEIGHT - 1, max + FLUID_REACH) / ChunkMath.SECTION_SIZE;
                    bandHistogram[last - first + 1]++;
                    int maps = 1 + PopulatedNeighbors(world, cd, s_neighborOffsets.Length);
                    fullMaps += maps;
                    bandedFluid += (long)maps * (last - first + 1);
                    Request(uniqueFluid, world, cd, first, last, s_neighborOffsets.Length);
                    Request(uniqueAll, world, cd, first, last, s_neighborOffsets.Length);

                    foreach (int index in fluids)
                    {
                        fluidVoxels++;
                        Vector3Int p = Local(index);
                        if (IsEdge(p)) fluidEdge++;
                        if (!ActiveVoxelScreen.MustRegister(ref screen, p.x, p.y, p.z, cd.GetVoxel(p.x, p.y, p.z))) fluidScreenable++;
                    }
                }

                NativeHashSet<int> grass = Bucket(cd, "ActiveGrassBucket");
                if (grass.IsCreated && grass.Count > 0)
                {
                    grassChunks++;
                    YRange(grass, out int min, out int max);
                    int first = Mathf.Max(0, min - GRASS_REACH_DOWN) / ChunkMath.SECTION_SIZE;
                    int last = Mathf.Min(ChunkMath.CHUNK_HEIGHT - 1, max + GRASS_REACH_UP) / ChunkMath.SECTION_SIZE;
                    bandedGrass += (long)(1 + PopulatedNeighbors(world, cd, s_cardinals.Length)) * (last - first + 1);
                    Request(uniqueAll, world, cd, first, last, s_cardinals.Length);

                    foreach (int index in grass)
                    {
                        grassVoxels++;
                        Vector3Int p = Local(index);
                        if (IsEdge(p)) grassEdge++;
                        if (!ActiveVoxelScreen.MustRegister(ref screen, p.x, p.y, p.z, cd.GetVoxel(p.x, p.y, p.z))) grassScreenable++;
                    }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"tick={world.TickCounter} activeChunks={activeChunks}");
            sb.AppendLine($"FLUID chunks={fluidChunks} voxels={fluidVoxels} edge={fluidEdge} screenable={fluidScreenable}");
            sb.Append("  band sections histogram (1..8):");
            for (int i = 1; i <= MAX_SECTIONS_HISTOGRAM; i++) sb.Append(' ').Append(bandHistogram[i]);
            sb.AppendLine();
            sb.AppendLine($"  copies: full maps {fullMaps * MAP_KB / BYTES_PER_KB} MB | banded {bandedFluid * SECTION_KB / BYTES_PER_KB} MB | " +
                          $"banded once per chunk {uniqueFluid.Count * SECTION_KB / BYTES_PER_KB} MB (upper bound: empty sections are not copied)");
            sb.AppendLine($"GRASS chunks={grassChunks} voxels={grassVoxels} edge={grassEdge} screenable={grassScreenable} " +
                          $"banded {bandedGrass * SECTION_KB / BYTES_PER_KB} MB");
            sb.AppendLine($"ALL banded once per chunk (fluid + grass, the shipped arena) {uniqueAll.Count * SECTION_KB / BYTES_PER_KB} MB");
            return sb.ToString();
        }

        /// <summary>
        /// Audits the registration contract on the live world: every active-type voxel that no bucket holds must be one
        /// the tick would drop unchanged — the managed <c>BlockBehavior.Active</c> is false and <c>Behave</c> emits nothing.
        /// A voxel failing that is a sleeper nothing will wake. Chunks still awaiting their full registration
        /// (<c>NeedsActiveVoxelRescan</c>) are skipped: they are unregistered by design until a visual attaches.
        /// </summary>
        /// <returns>A report: the audited counts, and up to <see cref="MAX_LISTED_SLEEPERS"/> offending voxels.</returns>
        public static string AuditSleepers()
        {
            World world = Object.FindAnyObjectByType<World>();
            if (world == null || !Application.isPlaying) return "no World in Play mode";

            // A queued modification will wake its six neighbors when applied, so a sleeper beside one is not yet a
            // fault. Refuse rather than report those as false failures.
            ICollection queued = (ICollection)typeof(World).GetField("_modifications", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(world);
            if (queued.Count > 0) return $"REFUSED: {queued.Count} queued modifications — retry";

            bool[] isActiveById = world.IsActiveById;
            int chunks = 0, skippedUnregistered = 0;
            long unregistered = 0, sleepers = 0, rejectedOnly = 0;
            StringBuilder listed = new StringBuilder();

            foreach (ChunkData cd in world.worldData.ChunkValues)
            {
                if (cd == null || !cd.IsPopulated) continue;
                if (cd.NeedsActiveVoxelRescan)
                {
                    skippedUnregistered++;
                    continue;
                }

                chunks++;
                for (int s = 0; s < cd.sections.Length; s++)
                {
                    ChunkSection section = cd.sections[s];
                    if (section == null || section.IsEmpty) continue;

                    for (int i = 0; i < section.voxels.Length; i++)
                    {
                        ushort id = BurstVoxelDataBitMapping.GetId(section.voxels[i]);
                        if (id >= isActiveById.Length || !isActiveById[id]) continue;

                        Vector3Int p = new Vector3Int(i % ChunkMath.SECTION_SIZE,
                            s * ChunkMath.SECTION_SIZE + i / ChunkMath.SECTION_SIZE % ChunkMath.SECTION_SIZE,
                            i / (ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE));
                        if (cd.IsVoxelActive(p)) continue;

                        unregistered++;
                        bool active = BlockBehavior.Active(cd, p);
                        List<VoxelMod> mods = BlockBehavior.Behave(cd, p);
                        int emitted = mods?.Count ?? 0;
                        int accepted = emitted == 0 ? 0 : CountAccepted(world, mods);
                        if (!active && accepted == 0)
                        {
                            if (emitted > 0) rejectedOnly++;
                            continue;
                        }

                        sleepers++;
                        if (sleepers <= MAX_LISTED_SLEEPERS)
                            listed.AppendLine($"  chunk {cd.Position} local {p} id {id} active={active} mods={emitted} accepted={accepted}");
                    }
                }
            }

            return $"tick={world.TickCounter} auditedChunks={chunks} skippedAwaitingRegistration={skippedUnregistered} " +
                   $"unregisteredActiveType={unregistered} rejectedOnly={rejectedOnly} SLEEPERS={sleepers}\n{listed}";
        }

        // Mirrors ApplyModifications' placement gate for the tick's mods (Live source, Default rule): a mod it would
        // refuse changes nothing, so the voxel that emits only such mods is inert even though Behave returns them.
        // rejectedOnly counts those — e.g. grass under a log, whose Dirt mod the placement mask refuses.
        private static int CountAccepted(World world, List<VoxelMod> mods)
        {
            int accepted = 0;
            foreach (VoxelMod mod in mods)
            {
                Vector3Int g = mod.GlobalPosition;
                if (!world.worldData.TryGetVoxel(g.x, g.y, g.z, out VoxelState existing))
                {
                    accepted++; // an unpopulated target is deferred to the pending store, not refused
                    continue;
                }

                BlockType existingProps = world.BlockTypes[existing.ID];
                bool ok = mod.ID == BlockIDs.Air
                    ? (existingProps.tags & BlockTags.UNBREAKABLE) == 0
                    : mod.Rule switch
                    {
                        ReplacementRule.ForcePlace => (existingProps.tags & BlockTags.UNBREAKABLE) == 0,
                        ReplacementRule.OnlyReplaceAir => existing.ID == BlockIDs.Air,
                        _ => BlockTagUtility.CanReplaceForPlacement(world.BlockTypes[mod.ID], existingProps),
                    };
                if (ok) accepted++;
            }

            return accepted;
        }

        private static NativeHashSet<int> Bucket(ChunkData cd, string property) =>
            (NativeHashSet<int>)typeof(ChunkData).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cd);

        private static Vector3Int Local(int index)
        {
            ChunkMath.GetLocalPositionFromFlattenedIndex(index, out int x, out int y, out int z);
            return new Vector3Int(x, y, z);
        }

        private static void YRange(NativeHashSet<int> bucket, out int min, out int max)
        {
            min = int.MaxValue;
            max = int.MinValue;
            foreach (int index in bucket)
            {
                int y = Local(index).y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
            }
        }

        private static bool IsEdge(Vector3Int p) =>
            p.x == 0 || p.z == 0 || p.x == ChunkMath.CHUNK_WIDTH - 1 || p.z == ChunkMath.CHUNK_WIDTH - 1;

        private static int PopulatedNeighbors(World world, ChunkData cd, int count)
        {
            int n = 0;
            for (int i = 0; i < count; i++)
                if (world.worldData.TryGetChunk(cd.Position + s_neighborOffsets[i], out ChunkData nb) && nb != null && nb.IsPopulated)
                    n++;
            return n;
        }

        // Neighbor slots 0–3 are the cardinals, so a count of 4 requests the grass shape and 8 the fluid shape.
        private static void Request(HashSet<long> unique, World world, ChunkData cd, int first, int last, int neighborCount)
        {
            Add(unique, cd.Position, first, last);
            for (int i = 0; i < neighborCount; i++)
                if (world.worldData.TryGetChunk(cd.Position + s_neighborOffsets[i], out ChunkData nb) && nb != null && nb.IsPopulated)
                    Add(unique, nb.Position, first, last);
        }

        private static void Add(HashSet<long> unique, Vector2Int origin, int first, int last)
        {
            for (int s = first; s <= last; s++)
                unique.Add(((long)ChunkMath.VoxelToChunk(origin.x) << 36) ^ ((long)(uint)ChunkMath.VoxelToChunk(origin.y) << 4) ^ s);
        }
    }
}
