using System;
using System.Collections.Generic;
using Audio;
using Data;
using Data.Enums;
using Editor.Validation.Framework;
using Helpers;
using Jobs;
using Jobs.BurstData;
using Jobs.Data;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.SoundEngine
{
    /// <summary>
    /// <see cref="SoundEngineValidationSuite"/> — the S3 fluid emitters: the per-section sounding-fluid
    /// count that decides what the scan even looks at, the Burst scan's binning, and the pure ranking,
    /// slot-assignment and gain decisions behind the looping sources.
    /// </summary>
    /// <remarks>
    /// <para>Silent like the rest of the suite: what the scan <i>finds</i> and which source carries it are
    /// assertable without an <c>AudioListener</c>, and that is the half where the defects live. Whether the
    /// resulting loop sits convincingly in the mix stays an in-game judgment.</para>
    /// <para>These scenarios bind <see cref="FluidBlockLookup"/> to a fixture palette, which is a
    /// process-wide static. Each restores the shipped <c>BlockDatabase</c> palette before returning, so a
    /// <c>Validate All</c> run cannot inherit the fixture — the same discipline the volume scenarios apply
    /// to <see cref="AudioVolumes"/>.</para>
    /// </remarks>
    public static partial class SoundEngineValidationSuite
    {
        /// <summary>Fluid level used for "flowing but not falling" fixtures.</summary>
        private const byte FIXTURE_FLOW_LEVEL = 3;

        /// <summary>Cluster size the gain scenarios treat as fully loud.</summary>
        private const int FIXTURE_SATURATION_WEIGHT = 64;

        /// <summary>Tolerance for centroid comparisons, in voxels.</summary>
        private const float CENTROID_TOLERANCE = 0.001f;

        static partial void AddEmitterScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Only Sections Holding A Sounding Fluid Report A Positive Count",
                RunEmitterCountEditSequence));
            scenarios.Add(new Scenario("Managed And Job-Side Sounding Tests Agree Across The Palette",
                RunSoundingTestParity));
            scenarios.Add(new Scenario("The Bin Grid Stays Anchored To The World As The Listener Moves",
                RunBinGridAnchoring));
            scenarios.Add(new Scenario("A Still Water Body Produces No Emitter Candidates", RunStillWaterScan));
            scenarios.Add(new Scenario("A Still Lava Pool Still Sounds", RunStillLavaScan));
            scenarios.Add(new Scenario("A Flowing Stream Bins At Its Own Centroid", RunStreamScan));
            scenarios.Add(new Scenario("A Voxel Beyond The Scan Radius Is Never Binned", RunRadiusCull));
            scenarios.Add(new Scenario("A Falling Column Merges Into One Emitter", RunFallingColumnMerge));
            scenarios.Add(new Scenario("Water And Lava Never Share An Emitter", RunKindSeparation));
            scenarios.Add(new Scenario("An Emitter Reclaims Its Own Still-Audible Source", RunEmitterSlotReclaim));
            scenarios.Add(new Scenario("A New Emitter Takes A Silent Source Before The Quietest Audible One",
                RunEmitterSlotPreference));
            scenarios.Add(new Scenario("Cluster Gain Rises With Weight And Saturates", RunEmitterGainCurve));
            scenarios.Add(new Scenario("Emitter Rolloff Actually Reaches Silence", RunRolloffCurve));
            scenarios.Add(new Scenario("Rolloff Shape Is The Same At Every Audible Radius", RunRolloffRadiusIndependence));
            scenarios.Add(new Scenario("Teleport Test Fires Only Past Its Threshold", RunTeleportTest));
            scenarios.Add(new Scenario("Source Volume Applies One Fade Curve And A Linear Cluster Gain",
                RunSourceVolume));
            scenarios.Add(new Scenario("A World Re-Anchor Offsets Emitters By The Origin Delta", RunOriginShift));
            scenarios.Add(new Scenario("Bin Grid Has One Slot Per Emitter Kind", RunKindCountMatchesEnum));
        }

        /// <summary>
        /// The differential guard on <see cref="ChunkSection.emitterFluidCount"/>: the incrementally
        /// maintained count must equal a full recount after every edit, and a still body must count zero.
        /// </summary>
        /// <remarks>
        /// This is the scenario that stands between S3 and a silent failure. The count is what decides
        /// whether a section is snapshotted at all, so an under-count does not produce a wrong sound — it
        /// produces no sound, which is indistinguishable from "no water nearby" unless something checks.
        /// </remarks>
        private static bool RunEmitterCountEditSequence()
        {
            const string scenario = "Only Sections Holding A Sounding Fluid Report A Positive Count";

            BlockType[] palette = BindFixturePalette();
            try
            {
                ChunkData chunkData = new ChunkData(new Vector2Int(0, 0));

                // A still body: sources only, so nothing here is an emitter.
                for (int y = 0; y < ChunkMath.SECTION_SIZE; y++)
                    chunkData.SetVoxel(0, y, 0, Pack(BlockIDs.Water, 0));

                if (!CheckCountsAgree(scenario, chunkData, "after filling a still column", out string detail))
                    return FailSound(scenario, detail);
                if (chunkData.sections[0].emitterFluidCount != 0)
                    return FailSound(scenario, "a column of source blocks reported flow — a still lake would " +
                                               $"be scanned every tick (count {chunkData.sections[0].emitterFluidCount}).");

                // One flowing voxel, then a falling one: both are emitters, and neither changes the block id.
                chunkData.SetVoxel(1, 0, 0, Pack(BlockIDs.Water, FIXTURE_FLOW_LEVEL));
                chunkData.SetVoxel(2, 0, 0, Pack(BlockIDs.Water, BurstVoxelDataBitMapping.MakeFluidFalling(1)));
                if (!CheckCountsAgree(scenario, chunkData, "after adding flow", out detail))
                    return FailSound(scenario, detail);
                if (chunkData.sections[0].emitterFluidCount != 2)
                    return FailSound(scenario, $"expected 2 flowing voxels, got {chunkData.sections[0].emitterFluidCount}.");

                // Settling a flowing voxel back to a source must decrement even though the id is unchanged.
                chunkData.SetVoxel(1, 0, 0, Pack(BlockIDs.Water, 0));
                if (!CheckCountsAgree(scenario, chunkData, "after a flowing voxel settled to a source", out detail))
                    return FailSound(scenario, detail);

                // Lava in a second section, to prove the count is per-section and not per-chunk.
                chunkData.SetVoxel(3, ChunkMath.SECTION_SIZE, 3, Pack(BlockIDs.Lava, FIXTURE_FLOW_LEVEL));
                if (!CheckCountsAgree(scenario, chunkData, "after adding lava in section 1", out detail))
                    return FailSound(scenario, detail);
                if (chunkData.sections[1].emitterFluidCount != 1)
                    return FailSound(scenario, "section 1's lava flow was not counted in its own section.");

                // A lava SOURCE counts too, where a water source does not: lava sounds in every state.
                chunkData.SetVoxel(4, ChunkMath.SECTION_SIZE, 3, Pack(BlockIDs.Lava, 0));
                if (!CheckCountsAgree(scenario, chunkData, "after adding a lava source", out detail))
                    return FailSound(scenario, detail);
                if (chunkData.sections[1].emitterFluidCount != 2)
                    return FailSound(scenario, "a still lava source was not counted — lava must sound at any " +
                                               $"level (count {chunkData.sections[1].emitterFluidCount}).");

                chunkData.SetVoxel(4, ChunkMath.SECTION_SIZE, 3, Pack(BlockIDs.Air, 0));

                // Breaking every emitter must return the counts to zero, not merely to "smaller".
                chunkData.SetVoxel(2, 0, 0, Pack(BlockIDs.Air, 0));
                chunkData.SetVoxel(3, ChunkMath.SECTION_SIZE, 3, Pack(BlockIDs.Air, 0));
                if (!CheckCountsAgree(scenario, chunkData, "after breaking every flowing voxel", out detail))
                    return FailSound(scenario, detail);
                if (chunkData.sections[0].emitterFluidCount != 0 || chunkData.sections[1].emitterFluidCount != 0)
                    return FailSound(scenario, "a section still reported flow after every flowing voxel was removed.");

                // A pooled section must not carry its count into its next life.
                ChunkSection recycled = chunkData.sections[0];
                recycled.Reset();
                if (recycled.emitterFluidCount != 0)
                    return FailSound(scenario, "ChunkSection.Reset left a stale flowing count on a pooled section.");

                return true;
            }
            finally
            {
                RestoreShippedPalette(palette);
            }
        }

        /// <summary>
        /// Pins the parity invariant between the managed count predicate and the job's own test. They are
        /// two implementations of one decision, and a disagreement is silent: a section whose count says
        /// "nothing here" is never snapshotted, so the job never gets the chance to disagree out loud.
        /// </summary>
        /// <remarks>
        /// Sweeps the whole fixture palette against the <b>real job</b>, one run per (id, level) pair, rather
        /// than against a restatement of its rule. A restatement can only catch an edit to the managed side —
        /// edit the job's predicate and a restating scenario stays green, which is precisely the divergence
        /// this exists to find.
        /// </remarks>
        private static bool RunSoundingTestParity()
        {
            const string scenario = "Managed And Job-Side Sounding Tests Agree Across The Palette";

            BlockType[] palette = BindFixturePalette();
            NativeArray<BlockTypeJobData> jobPalette = BuildJobPalette(palette);
            NativeArray<uint> voxels = new NativeArray<uint>(ChunkMath.SECTION_VOLUME, Allocator.TempJob);
            NativeArray<int3> origins = new NativeArray<int3>(1, Allocator.TempJob);
            NativeArray<FluidEmitterBin> bins =
                new NativeArray<FluidEmitterBin>(FluidEmitterScanGeometry.BinCount, Allocator.TempJob);

            try
            {
                int3 listener = int3.zero;
                int3 binOrigin = FluidEmitterScanGeometry.BinOrigin(listener);
                origins[0] = int3.zero;

                for (ushort id = 0; id < palette.Length; id++)
                for (byte level = 0; level <= BurstVoxelDataBitMapping.META_VAL_FLUID_MASK; level++)
                {
                    uint packed = Pack(id, level);

                    // One voxel at the section's corner, which is also the listener's cell, so nothing but
                    // the predicate can decide whether it lands in a bin.
                    for (int i = 0; i < voxels.Length; i++) voxels[i] = 0u;
                    voxels[0] = packed;

                    new FluidEmitterScanJob
                    {
                        Sections = voxels,
                        SectionOrigins = origins,
                        SectionCount = 1,
                        BlockTypes = jobPalette,
                        ListenerVoxel = listener,
                        BinOrigin = binOrigin,
                        Bins = bins,
                    }.Run();

                    int binned = 0;
                    foreach (FluidEmitterBin bin in bins) binned += bin.Weight;

                    bool managed = FluidBlockLookup.IsEmitterFluid(packed);
                    if (managed == (binned > 0)) continue;

                    return FailSound(scenario, $"id {id} at level {level}: the section count says {managed} " +
                                               $"while the scan job binned {binned} voxel(s). A section whose " +
                                               "count says 'nothing here' is never snapshotted, so this " +
                                               "disagreement is silence with no other symptom.");
                }

                return true;
            }
            finally
            {
                voxels.Dispose();
                origins.Dispose();
                bins.Dispose();
                jobPalette.Dispose();
                RestoreShippedPalette(palette);
            }
        }

        /// <summary>
        /// The property the whole clustering choice rests on: the bin grid is anchored to world coordinates,
        /// not to the listener, so a given voxel keeps falling in the same world cell as the player walks.
        /// A listener-relative grid would re-cut its boundaries every scan and jump the centroids across them.
        /// </summary>
        private static bool RunBinGridAnchoring()
        {
            const string scenario = "The Bin Grid Stays Anchored To The World As The Listener Moves";

            int3 voxel = new int3(100, 70, -37);
            int3 expectedCell = new int3(voxel.x >> FluidEmitterScanGeometry.BinShift,
                voxel.y >> FluidEmitterScanGeometry.BinShift,
                voxel.z >> FluidEmitterScanGeometry.BinShift);

            // Both quadrants: the negative side is where a truncating divide would silently mis-align.
            for (int step = -20; step <= 20; step++)
            {
                int3 listener = new int3(100 + step, 70, -37 - step);
                int3 origin = FluidEmitterScanGeometry.BinOrigin(listener);

                if ((origin.x & (FluidEmitterScanGeometry.BinSize - 1)) != 0 ||
                    (origin.y & (FluidEmitterScanGeometry.BinSize - 1)) != 0 ||
                    (origin.z & (FluidEmitterScanGeometry.BinSize - 1)) != 0)
                    return FailSound(scenario, $"listener {listener} produced an unsnapped bin origin {origin}.");

                int3 bin = (voxel - origin) >> FluidEmitterScanGeometry.BinShift;
                int3 worldCell = (origin >> FluidEmitterScanGeometry.BinShift) + bin;

                if (!worldCell.Equals(expectedCell))
                    return FailSound(scenario, $"listener {listener} placed voxel {voxel} in world cell " +
                                               $"{worldCell}, not {expectedCell} — the grid moved with the listener.");
            }

            return true;
        }

        /// <summary>Still water is the ambience bed's job, and must contribute no emitter at all.</summary>
        private static bool RunStillWaterScan()
        {
            const string scenario = "A Still Water Body Produces No Emitter Candidates";

            uint[] section = new uint[ChunkMath.SECTION_VOLUME];
            for (int i = 0; i < section.Length; i++) section[i] = Pack(BlockIDs.Water, 0);

            return RunScan(scenario, section, int3.zero, new int3(8, 8, 8), (bins, binOrigin) =>
            {
                FluidEmitterCandidate[] candidates = new FluidEmitterCandidate[8];
                int count = FluidEmitterResolution.Collect(bins, binOrigin, candidates, candidates.Length);

                return count == 0
                    ? null
                    : $"a still body produced {count} candidate(s); the first weighs {candidates[0].Weight}.";
            });
        }

        /// <summary>
        /// Lava is the asymmetry: unlike water it sounds at every level, so a pool of pure source blocks
        /// must still produce an emitter.
        /// </summary>
        /// <remarks>
        /// The mirror image of <see cref="RunStillWaterScan"/>, and the pair is the point — one scenario
        /// alone could pass with the rule applied to both fluids.
        /// </remarks>
        private static bool RunStillLavaScan()
        {
            const string scenario = "A Still Lava Pool Still Sounds";

            uint[] section = new uint[ChunkMath.SECTION_VOLUME];
            const int poolY = 3;
            const int poolSide = 4;
            for (int x = 0; x < poolSide; x++)
            for (int z = 0; z < poolSide; z++)
                section[SectionIndex(x, poolY, z)] = Pack(BlockIDs.Lava, 0);

            return RunScan(scenario, section, int3.zero, new int3(2, poolY, 2), (bins, binOrigin) =>
            {
                FluidEmitterCandidate[] candidates = new FluidEmitterCandidate[8];
                int count = FluidEmitterResolution.Collect(bins, binOrigin, candidates, candidates.Length);

                if (count == 0)
                    return "a still lava pool produced no emitter; lava must sound at any level.";
                if (candidates[0].Kind != FluidEmitterKind.LavaFlow)
                    return $"still lava resolved as {candidates[0].Kind}; a level-0 pool is not falling, so " +
                           "it belongs to the horizontal lava loop.";
                if (candidates[0].Weight != poolSide * poolSide)
                    return $"expected weight {poolSide * poolSide}, got {candidates[0].Weight}.";

                return null;
            });
        }

        /// <summary>A horizontal run of flowing water becomes one emitter placed at the run's mean position.</summary>
        private static bool RunStreamScan()
        {
            const string scenario = "A Flowing Stream Bins At Its Own Centroid";

            uint[] section = new uint[ChunkMath.SECTION_VOLUME];
            const int streamZ = 4;
            const int streamY = 5;
            const int streamLength = 6;
            for (int x = 0; x < streamLength; x++)
                section[SectionIndex(x, streamY, streamZ)] = Pack(BlockIDs.Water, FIXTURE_FLOW_LEVEL);

            const float expectedX = (streamLength - 1) / 2f;

            return RunScan(scenario, section, int3.zero, new int3(4, 5, 4), (bins, binOrigin) =>
            {
                FluidEmitterCandidate[] candidates = new FluidEmitterCandidate[8];
                int count = FluidEmitterResolution.Collect(bins, binOrigin, candidates, candidates.Length);

                if (count != 1) return $"expected one candidate, got {count}.";
                if (candidates[0].Kind != FluidEmitterKind.WaterFlow)
                    return $"a horizontal stream resolved as {candidates[0].Kind}, not WaterFlow.";
                if (candidates[0].Weight != streamLength)
                    return $"expected weight {streamLength}, got {candidates[0].Weight}.";

                float3 centroid = candidates[0].Centroid;
                if (math.abs(centroid.x - expectedX) > CENTROID_TOLERANCE ||
                    math.abs(centroid.y - streamY) > CENTROID_TOLERANCE ||
                    math.abs(centroid.z - streamZ) > CENTROID_TOLERANCE)
                    return $"centroid {centroid} is not the stream's mean ({expectedX}, {streamY}, {streamZ}).";

                return null;
            });
        }

        /// <summary>
        /// The spherical cull: flow inside the search box but outside the audible radius contributes
        /// nothing, so a box corner cannot sound like something the listener is standing next to.
        /// </summary>
        private static bool RunRadiusCull()
        {
            const string scenario = "A Voxel Beyond The Scan Radius Is Never Binned";

            uint[] section = new uint[ChunkMath.SECTION_VOLUME];
            section[SectionIndex(0, 0, 0)] = Pack(BlockIDs.Water, FIXTURE_FLOW_LEVEL);

            // The section sits at the far corner of the box: inside it on every axis, outside the sphere.
            const int corner = FluidEmitterScanGeometry.RadiusXZ;
            int3 origin = new int3(corner, corner, corner);

            return RunScan(scenario, section, origin, int3.zero, (bins, binOrigin) =>
            {
                FluidEmitterCandidate[] candidates = new FluidEmitterCandidate[4];
                int count = FluidEmitterResolution.Collect(bins, binOrigin, candidates, candidates.Length);

                return count == 0 ? null : "flow at the box corner was binned despite lying outside the radius.";
            });
        }

        /// <summary>
        /// The rule that makes a waterfall one sound: bins of one kind stacked in a column merge, so a fall
        /// tall enough to span several bins does not play as three copies of itself beating together.
        /// </summary>
        private static bool RunFallingColumnMerge()
        {
            const string scenario = "A Falling Column Merges Into One Emitter";

            byte falling = BurstVoxelDataBitMapping.MakeFluidFalling(1);
            uint[] section = new uint[ChunkMath.SECTION_VOLUME];
            for (int y = 0; y < ChunkMath.SECTION_SIZE; y++) section[SectionIndex(2, y, 2)] = Pack(BlockIDs.Water, falling);

            // A full 16-tall section spans two 8-block bins, so an unmerged grid would report two emitters.
            const float expectedY = (ChunkMath.SECTION_SIZE - 1) / 2f;

            return RunScan(scenario, section, int3.zero, new int3(2, 8, 2), (bins, binOrigin) =>
            {
                FluidEmitterCandidate[] candidates = new FluidEmitterCandidate[8];
                int count = FluidEmitterResolution.Collect(bins, binOrigin, candidates, candidates.Length);

                if (count != 1) return $"a single column produced {count} emitters; vertical merging did not run.";
                if (candidates[0].Kind != FluidEmitterKind.WaterFall)
                    return $"a falling column resolved as {candidates[0].Kind}, not WaterFall.";
                if (candidates[0].Weight != ChunkMath.SECTION_SIZE)
                    return $"expected weight {ChunkMath.SECTION_SIZE}, got {candidates[0].Weight}.";
                if (math.abs(candidates[0].Centroid.y - expectedY) > CENTROID_TOLERANCE)
                    return $"the merged centroid sits at y={candidates[0].Centroid.y}, not the column's mean {expectedY}.";

                return null;
            });
        }

        /// <summary>
        /// Water and lava in the same bin must stay two emitters. Sharing one would play a single loop for
        /// both, and the loop that lost is the one the player is standing next to half the time.
        /// </summary>
        private static bool RunKindSeparation()
        {
            const string scenario = "Water And Lava Never Share An Emitter";

            uint[] section = new uint[ChunkMath.SECTION_VOLUME];
            section[SectionIndex(1, 1, 1)] = Pack(BlockIDs.Water, FIXTURE_FLOW_LEVEL);
            section[SectionIndex(2, 1, 1)] = Pack(BlockIDs.Lava, FIXTURE_FLOW_LEVEL);

            return RunScan(scenario, section, int3.zero, new int3(1, 1, 1), (bins, binOrigin) =>
            {
                FluidEmitterCandidate[] candidates = new FluidEmitterCandidate[8];
                int count = FluidEmitterResolution.Collect(bins, binOrigin, candidates, candidates.Length);

                if (count != 2) return $"expected one emitter per fluid, got {count}.";
                if (candidates[0].Kind == candidates[1].Kind)
                    return $"both emitters resolved as {candidates[0].Kind}.";
                if (!candidates[0].Cell.Equals(candidates[1].Cell))
                    return "the two fluids were binned into different cells; they share one.";

                return null;
            });
        }

        /// <summary>
        /// A cluster that is still there keeps the source already emitting for it, so a stream the player
        /// walks past does not restart from silence every scan.
        /// </summary>
        private static bool RunEmitterSlotReclaim()
        {
            const string scenario = "An Emitter Reclaims Its Own Still-Audible Source";

            int3 cell = new int3(3, 4, 5);
            int3[] cells = { new int3(9, 9, 9), cell, new int3(1, 1, 1) };
            FluidEmitterKind[] kinds =
                { FluidEmitterKind.LavaFlow, FluidEmitterKind.WaterFlow, FluidEmitterKind.WaterFall };
            // Slot 1 is deliberately NOT the quietest: if it were, the reclaim below would pass on the
            // fallback rule alone and the scenario would assert nothing (VO-* Bug M03 — a positive control
            // must not be satisfiable by the behavior under test).
            float[] fades = { 0.9f, 0.5f, 0.2f };

            FluidEmitterCandidate[] candidates =
            {
                new FluidEmitterCandidate { Kind = FluidEmitterKind.WaterFlow, Cell = cell, Weight = 10 },
            };
            int[] slots = new int[1];

            FluidEmitterResolution.AssignSlots(cells, kinds, fades, candidates, 1, slots);

            if (slots[0] != 1)
                return FailSound(scenario, $"the returning emitter took slot {slots[0]} instead of reclaiming " +
                                           "slot 1, which is still audible with its own cell and kind.");

            // Same cell, different kind: not the same emitter, so the reclaim must not fire and the
            // quietest source (slot 2) takes it instead.
            candidates[0].Kind = FluidEmitterKind.LavaFall;
            FluidEmitterResolution.AssignSlots(cells, kinds, fades, candidates, 1, slots);

            if (slots[0] != 2)
                return FailSound(scenario, $"a different kind in the same cell took slot {slots[0]}; it must " +
                                           "not reclaim the water source, leaving the quietest (slot 2).");

            return true;
        }

        /// <summary>
        /// Preference order for a new emitter: a silent source before any audible one, and the quietest
        /// audible one only when none is free — the least-heard interruption.
        /// </summary>
        private static bool RunEmitterSlotPreference()
        {
            const string scenario = "A New Emitter Takes A Silent Source Before The Quietest Audible One";

            int3[] cells = { new int3(1, 1, 1), new int3(2, 2, 2), new int3(3, 3, 3) };
            FluidEmitterKind[] kinds =
                { FluidEmitterKind.WaterFlow, FluidEmitterKind.WaterFlow, FluidEmitterKind.WaterFlow };

            FluidEmitterCandidate[] candidates =
            {
                new FluidEmitterCandidate { Kind = FluidEmitterKind.LavaFlow, Cell = new int3(7, 7, 7), Weight = 5 },
            };
            int[] slots = new int[1];

            float[] withSilent = { 0.6f, 0f, 0.2f };
            FluidEmitterResolution.AssignSlots(cells, kinds, withSilent, candidates, 1, slots);
            if (slots[0] != 1)
                return FailSound(scenario, $"a new emitter took slot {slots[0]} while slot 1 was silent.");

            float[] allAudible = { 0.6f, 0.9f, 0.2f };
            FluidEmitterResolution.AssignSlots(cells, kinds, allAudible, candidates, 1, slots);
            if (slots[0] != 2)
                return FailSound(scenario, $"with every source audible, the emitter took slot {slots[0]} " +
                                           "instead of the quietest (slot 2).");

            // Two candidates must never land on one source, or one of them is inaudible for no reason.
            FluidEmitterCandidate[] pair =
            {
                new FluidEmitterCandidate { Kind = FluidEmitterKind.LavaFlow, Cell = new int3(7, 7, 7), Weight = 5 },
                new FluidEmitterCandidate { Kind = FluidEmitterKind.LavaFall, Cell = new int3(8, 8, 8), Weight = 4 },
            };
            int[] pairSlots = new int[2];
            FluidEmitterResolution.AssignSlots(cells, kinds, allAudible, pair, 2, pairSlots);

            if (pairSlots[0] == pairSlots[1])
                return FailSound(scenario, $"both emitters were assigned source {pairSlots[0]}.");

            return true;
        }

        /// <summary>
        /// The loudness curve: a trickle is already clearly audible, a river saturates rather than growing
        /// without bound, and the curve never runs backwards.
        /// </summary>
        private static bool RunEmitterGainCurve()
        {
            const string scenario = "Cluster Gain Rises With Weight And Saturates";

            if (FluidEmitterResolution.GainFromWeight(0, FIXTURE_SATURATION_WEIGHT) != 0f)
                return FailSound(scenario, "an empty cluster produced a non-zero gain.");

            float previous = 0f;
            for (int weight = 1; weight <= FIXTURE_SATURATION_WEIGHT * 2; weight++)
            {
                float gain = FluidEmitterResolution.GainFromWeight(weight, FIXTURE_SATURATION_WEIGHT);

                if (gain < previous)
                    return FailSound(scenario, $"gain fell from {previous} to {gain} at weight {weight}.");
                if (gain > 1f)
                    return FailSound(scenario, $"gain reached {gain} at weight {weight}; it must saturate at 1.");

                previous = gain;
            }

            if (!Mathf.Approximately(FluidEmitterResolution.GainFromWeight(FIXTURE_SATURATION_WEIGHT,
                    FIXTURE_SATURATION_WEIGHT), 1f))
                return FailSound(scenario, "a cluster at the saturation weight did not reach full gain.");

            // Square-rooted, so a single voxel must already be well clear of silence rather than
            // proportionally inaudible.
            float single = FluidEmitterResolution.GainFromWeight(1, FIXTURE_SATURATION_WEIGHT);
            if (single <= 1f / FIXTURE_SATURATION_WEIGHT)
                return FailSound(scenario, $"a single flowing voxel reads at {single} — the curve is linear, " +
                                           "not the perceptual shaping the beds use.");

            return true;
        }

        /// <summary>
        /// Runs the scan job over one fixture section and hands the resulting bins to an assertion.
        /// </summary>
        /// <param name="scenario">The scenario name, for failure reporting.</param>
        /// <param name="section">The section's packed voxels.</param>
        /// <param name="sectionOrigin">Its voxel-space low corner.</param>
        /// <param name="listenerVoxel">Where the listener stands.</param>
        /// <param name="assert">Returns null on success, or the failure detail.</param>
        /// <returns>True when the assertion passed.</returns>
        private static bool RunScan(string scenario, uint[] section, int3 sectionOrigin, int3 listenerVoxel,
            System.Func<NativeArray<FluidEmitterBin>, int3, string> assert)
        {
            BlockType[] palette = BindFixturePalette();
            NativeArray<uint> voxels = new NativeArray<uint>(section, Allocator.TempJob);
            NativeArray<int3> origins = new NativeArray<int3>(1, Allocator.TempJob);
            NativeArray<FluidEmitterBin> bins =
                new NativeArray<FluidEmitterBin>(FluidEmitterScanGeometry.BinCount, Allocator.TempJob);
            NativeArray<BlockTypeJobData> jobPalette = BuildJobPalette(palette);

            try
            {
                origins[0] = sectionOrigin;
                int3 binOrigin = FluidEmitterScanGeometry.BinOrigin(listenerVoxel);

                new FluidEmitterScanJob
                {
                    Sections = voxels,
                    SectionOrigins = origins,
                    SectionCount = 1,
                    BlockTypes = jobPalette,
                    ListenerVoxel = listenerVoxel,
                    BinOrigin = binOrigin,
                    Bins = bins,
                }.Run();

                string detail = assert(bins, binOrigin);
                return detail == null || FailSound(scenario, detail);
            }
            finally
            {
                voxels.Dispose();
                origins.Dispose();
                bins.Dispose();
                jobPalette.Dispose();
                RestoreShippedPalette(palette);
            }
        }

        /// <summary>
        /// Compares every section's incrementally maintained counts against a full recount.
        /// </summary>
        /// <param name="scenario">The scenario name, unused except to keep call sites readable.</param>
        /// <param name="chunkData">The chunk to audit.</param>
        /// <param name="stage">What the sequence had just done, for the failure message.</param>
        /// <param name="detail">Receives the failure detail when they disagree.</param>
        /// <returns>True when every section agrees.</returns>
        private static bool CheckCountsAgree(string scenario, ChunkData chunkData, string stage, out string detail)
        {
            detail = null;

            for (int s = 0; s < chunkData.sections.Length; s++)
            {
                ChunkSection section = chunkData.sections[s];
                if (section == null) continue;

                int incremental = section.emitterFluidCount;
                section.RecalculateNonAirCount();
                int recounted = section.emitterFluidCount;

                if (incremental == recounted) continue;

                detail = $"section {s} {stage}: SetVoxel maintained {incremental} flowing voxels, a full " +
                         $"recount finds {recounted}. The scan skips any section reporting zero, so an " +
                         "under-count is silence with no other symptom.";
                return false;
            }

            return true;
        }

        /// <summary>Packs a fixture voxel from a block id and a raw fluid level.</summary>
        /// <param name="id">The block id.</param>
        /// <param name="fluidLevel">The 4-bit fluid level, falling flag included.</param>
        /// <returns>The packed voxel uint.</returns>
        private static uint Pack(ushort id, byte fluidLevel) =>
            BurstVoxelDataBitMapping.PackVoxelData(id,
                BurstVoxelDataBitMapping.BuildMetaLegacy(0, fluidLevel, true));

        /// <summary>Flattens a section-local coordinate the way <c>ChunkData.SetVoxel</c> does.</summary>
        /// <param name="x">Section-local X.</param>
        /// <param name="y">Section-local Y.</param>
        /// <param name="z">Section-local Z.</param>
        /// <returns>The index into a section's voxel array.</returns>
        private static int SectionIndex(int x, int y, int z) =>
            x + y * ChunkMath.SECTION_SIZE + z * ChunkMath.SECTION_SIZE * ChunkMath.SECTION_SIZE;

        /// <summary>
        /// Builds a minimal palette carrying air, stone and the two fluids at their real block ids, and
        /// binds <see cref="FluidBlockLookup"/> to it.
        /// </summary>
        /// <returns>The fixture palette, to be passed back to <see cref="RestoreShippedPalette"/>.</returns>
        private static BlockType[] BindFixturePalette()
        {
            BlockType[] palette = new BlockType[Mathf.Max(BlockIDs.Water, BlockIDs.Lava) + 1];
            for (int id = 0; id < palette.Length; id++) palette[id] = new BlockType { blockName = $"Fixture {id}" };

            palette[BlockIDs.Air].blockName = "Air";
            palette[BlockIDs.Stone].blockName = "Stone";
            palette[BlockIDs.Water].fluidType = FluidType.WaterLike;
            palette[BlockIDs.Lava].fluidType = FluidType.LavaLike;

            FluidBlockLookup.Initialize(palette);
            return palette;
        }

        /// <summary>Converts a managed fixture palette into the job-side palette the scan job reads.</summary>
        /// <param name="palette">The fixture palette.</param>
        /// <returns>A job palette the caller must dispose.</returns>
        private static NativeArray<BlockTypeJobData> BuildJobPalette(BlockType[] palette)
        {
            BlockTypeJobData[] jobData = new BlockTypeJobData[palette.Length];
            for (int id = 0; id < palette.Length; id++) jobData[id] = new BlockTypeJobData(palette[id]);

            return new NativeArray<BlockTypeJobData>(jobData, Allocator.TempJob);
        }

        /// <summary>
        /// Re-binds <see cref="FluidBlockLookup"/> to the shipped palette, so a fixture cannot leak into a
        /// later suite in a <c>Validate All</c> run.
        /// </summary>
        /// <param name="fixturePalette">The palette that was bound; only used to detect a no-op.</param>
        private static void RestoreShippedPalette(BlockType[] fixturePalette)
        {
            if (fixturePalette == null) return;

            BlockDatabase database = AssetDatabase.LoadAssetAtPath<BlockDatabase>(BLOCK_DATABASE_PATH);
            if (database?.blockTypes != null) FluidBlockLookup.Initialize(database.blockTypes);
        }

        /// <summary>
        /// The rolloff curve must land on zero at its outer distance and never climb on the way there.
        /// </summary>
        /// <remarks>
        /// Unity's built-in logarithmic mode does not do this: <c>maxDistance</c> is where it <i>stops
        /// attenuating</i>, leaving a constant <c>minDistance / maxDistance</c> floor at every distance
        /// beyond — which is how a waterfall stayed audible from across the world. This scenario is the
        /// guard on that: the value at the far end must be silence, not a floor.
        /// </remarks>
        private static bool RunRolloffCurve()
        {
            const string scenario = "Emitter Rolloff Actually Reaches Silence";

            const float minDistance = 6f;
            const float maxDistance = 24f;
            AnimationCurve curve = FluidEmitterResolution.BuildRolloffCurve(minDistance, maxDistance, 8);

            if (curve.length < 3)
                return FailSound(scenario, $"the curve has {curve.length} keys — it was not built.");

            if (!Mathf.Approximately(curve.Evaluate(0f), 1f))
                return FailSound(scenario, $"gain at the listener is {curve.Evaluate(0f)}, not 1.");

            const float plateau = minDistance / maxDistance;
            if (!Mathf.Approximately(curve.Evaluate(plateau), 1f))
                return FailSound(scenario, $"gain at minDistance is {curve.Evaluate(plateau)}, not 1 — the " +
                                           "full-volume plateau is missing.");

            float atMax = curve.Evaluate(1f);
            if (Mathf.Abs(atMax) > 0.0001f)
                return FailSound(scenario, $"gain at maxDistance is {atMax}, not silence. Unity's logarithmic " +
                                           $"mode would leave {minDistance / maxDistance} here, audible at any distance.");

            // Beyond the last key Unity clamps to it, so silence past maxDistance follows from the key above —
            // asserted anyway, because that clamping is the behavior the whole fix leans on.
            if (Mathf.Abs(curve.Evaluate(4f)) > 0.0001f)
                return FailSound(scenario, "the curve does not stay silent past maxDistance.");

            const int samples = 400;
            float previous = float.MaxValue;
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples;
                float gain = curve.Evaluate(t);

                if (gain < -0.0001f || gain > 1.0001f)
                    return FailSound(scenario, $"gain {gain} at normalized distance {t} is outside [0, 1].");

                // Tolerance, not equality: the smoothed tangents may wobble a hair between sample points, but
                // a real rise would be an emitter getting LOUDER as the player backs away from it.
                if (gain > previous + 0.005f)
                    return FailSound(scenario, $"gain rose from {previous} to {gain} at normalized distance " +
                                               $"{t} — the emitter gets louder with distance there.");

                previous = gain;
            }

            return true;
        }

        /// <summary>
        /// The teleport test: travel must not trip it, a jump must, and it must behave the same in the
        /// negative quadrants.
        /// </summary>
        /// <remarks>
        /// Fed voxel cells rather than Unity positions on purpose — the engine re-anchors its render origin
        /// as the player travels, which shifts every Unity coordinate at once. A Unity-space test would read
        /// that re-anchor as a teleport and cut the emitters for no reason.
        /// </remarks>
        private static bool RunTeleportTest()
        {
            const string scenario = "Teleport Test Fires Only Past Its Threshold";
            const int threshold = FluidEmitterScanGeometry.RadiusXZ;

            int3 origin = new int3(0, 64, 0);

            if (FluidEmitterResolution.IsTeleport(origin, origin, threshold))
                return FailSound(scenario, "standing still registered as a teleport.");

            if (FluidEmitterResolution.IsTeleport(origin, origin + new int3(threshold, 0, 0), threshold))
                return FailSound(scenario, "a move of exactly the threshold registered as a teleport; the " +
                                           "test must fire strictly beyond it.");

            if (!FluidEmitterResolution.IsTeleport(origin, origin + new int3(threshold + 1, 0, 0), threshold))
                return FailSound(scenario, "a move one voxel past the threshold did not register.");

            // Diagonal: the test is a real distance, not a per-axis one, so a move under the threshold on
            // every axis can still exceed it overall.
            int3 diagonal = new int3(threshold - 4, threshold - 4, threshold - 4);
            if (!FluidEmitterResolution.IsTeleport(origin, origin + diagonal, threshold))
                return FailSound(scenario, "a diagonal jump past the threshold did not register — the test " +
                                           "is measuring one axis rather than distance.");

            // Negative quadrant: same magnitudes, opposite direction.
            int3 far = new int3(-100000, 20, -100000);
            if (!FluidEmitterResolution.IsTeleport(far, origin, threshold))
                return FailSound(scenario, "a jump out of the negative quadrant did not register.");
            if (FluidEmitterResolution.IsTeleport(far, far + new int3(-1, 0, 1), threshold))
                return FailSound(scenario, "a one-voxel step in the negative quadrant registered as a teleport.");

            return true;
        }

        /// <summary>
        /// Every emitter kind shares one rolloff curve while authoring its own audible radius, and that is
        /// only sound because the curve's shape over <i>normalized</i> distance does not depend on the
        /// radius — the full-volume plateau is a fixed fraction of it.
        /// </summary>
        /// <remarks>
        /// Pinned because the coupling is invisible: derive <c>minDistance</c> any other way and the shared
        /// curve silently stops matching every kind but the default one. Lava authoring 10 blocks against a
        /// 24-block default is the case that would break.
        /// </remarks>
        private static bool RunRolloffRadiusIndependence()
        {
            const string scenario = "Rolloff Shape Is The Same At Every Audible Radius";
            const float headroom = 4f;

            float[] radii = { 10f, 24f, 64f };
            AnimationCurve reference = FluidEmitterResolution.BuildRolloffCurve(radii[0] / headroom, radii[0], 8);

            foreach (float radius in radii)
            {
                AnimationCurve curve = FluidEmitterResolution.BuildRolloffCurve(radius / headroom, radius, 8);

                for (int i = 0; i <= 100; i++)
                {
                    float t = i / 100f;
                    float expected = reference.Evaluate(t);
                    float actual = curve.Evaluate(t);

                    if (Mathf.Abs(expected - actual) > 0.0005f)
                        return FailSound(scenario, $"at radius {radius}, normalized distance {t} gives " +
                                                   $"{actual} where radius {radii[0]} gives {expected} — the " +
                                                   "shape depends on the radius, so one shared curve is wrong.");
                }

                if (!Mathf.Approximately(curve.Evaluate(1f), 0f))
                    return FailSound(scenario, $"radius {radius} does not reach silence at its far end.");
            }

            return true;
        }

        /// <summary>
        /// The composed source volume: exactly one square root, on the fade, with cluster gain entering
        /// linearly.
        /// </summary>
        /// <remarks>
        /// The scenario that would have caught the original defect. Cluster gain was folded into the fade
        /// target, so <c>GainFromFade</c> rooted it a second time — a net fourth root that flattened cluster
        /// size almost out of existence. <see cref="RunEmitterGainCurve"/> tests <c>GainFromWeight</c> in
        /// isolation and structurally cannot see the compounding; only the composed product can.
        /// </remarks>
        private static bool RunSourceVolume()
        {
            const string scenario = "Source Volume Applies One Fade Curve And A Linear Cluster Gain";

            // Full fade, full trim: the volume must be the cluster gain itself, un-rooted a second time.
            for (int i = 0; i <= 10; i++)
            {
                float clusterGain = i / 10f;
                float volume = FluidEmitterResolution.SourceVolume(1f, clusterGain, 1f, 1f);

                if (Mathf.Abs(volume - clusterGain) > 0.0001f)
                    return FailSound(scenario, $"at full fade a cluster gain of {clusterGain} produced " +
                                               $"{volume}; the cluster term must enter linearly, not rooted " +
                                               "a second time by the fade curve.");
            }

            // The fade contributes exactly one equal-power root.
            float half = FluidEmitterResolution.SourceVolume(0.25f, 1f, 1f, 1f);
            if (Mathf.Abs(half - 0.5f) > 0.0001f)
                return FailSound(scenario, $"fade 0.25 gave {half}, not the equal-power 0.5.");

            // Trim and category gain are plain multipliers.
            float trimmed = FluidEmitterResolution.SourceVolume(1f, 0.5f, 0.5f, 0.5f);
            if (Mathf.Abs(trimmed - 0.125f) > 0.0001f)
                return FailSound(scenario, $"trim and category gain did not multiply linearly (got {trimmed}).");

            if (FluidEmitterResolution.SourceVolume(0f, 1f, 1f, 1f) != 0f)
                return FailSound(scenario, "a fully faded-out source was not silent.");

            return true;
        }

        /// <summary>
        /// A world re-anchor must move an already-placed emitter by exactly the origin delta, so it stays
        /// over its water.
        /// </summary>
        /// <remarks>
        /// The emitters are the one placed thing <c>World.ShiftOrigin</c> does not know about, and their
        /// voxel positions are immune to the shift by design — so nothing else in the system would notice
        /// the transforms going stale. Sign errors are the likely failure, hence both directions.
        /// </remarks>
        private static bool RunOriginShift()
        {
            const string scenario = "A World Re-Anchor Offsets Emitters By The Origin Delta";

            Vector3Int before = new Vector3Int(0, 0, 0);
            Vector3Int after = new Vector3Int(1024, 0, -2048);
            Vector3 emitterVoxel = new Vector3(1030.5f, 64f, -2040.25f);

            // What the source transform holds before the shift, and what it must hold after.
            Vector3 staleUnity = new Vector3(emitterVoxel.x - before.x, emitterVoxel.y, emitterVoxel.z - before.z);
            Vector3 expected = new Vector3(emitterVoxel.x - after.x, emitterVoxel.y, emitterVoxel.z - after.z);

            Vector3 corrected = staleUnity + FluidEmitterResolution.OriginShiftDelta(before, after);
            if (Vector3.Distance(corrected, expected) > 0.001f)
                return FailSound(scenario, $"after the shift the emitter sits at {corrected}, not {expected} " +
                                           "— the delta has the wrong sign or axis.");

            // And back again, so the correction is symmetric rather than accidentally right in one direction.
            Vector3 restored = corrected + FluidEmitterResolution.OriginShiftDelta(after, before);
            if (Vector3.Distance(restored, staleUnity) > 0.001f)
                return FailSound(scenario, $"shifting back gave {restored}, not the original {staleUnity}.");

            if (Mathf.Abs(FluidEmitterResolution.OriginShiftDelta(before, after).y) > 0.0001f)
                return FailSound(scenario, "the delta moved Y; the origin only ever re-anchors on XZ.");

            return true;
        }

        /// <summary>
        /// The bin grid's kind stride is a Burst-safe constant duplicated from
        /// <see cref="FluidEmitterKind"/>. This is the guard on that duplication.
        /// </summary>
        /// <remarks>
        /// <see cref="FluidEmitterKind"/>'s own docstring invites appending values. Appending one without
        /// bumping <c>KindCount</c> would make the job index one past the end of the bin array on the far
        /// cell — a silent out-of-bounds write in a Burst job with safety checks off.
        /// </remarks>
        private static bool RunKindCountMatchesEnum()
        {
            const string scenario = "Bin Grid Has One Slot Per Emitter Kind";

            int enumCount = Enum.GetValues(typeof(FluidEmitterKind)).Length;
            if (FluidEmitterScanGeometry.KindCount != enumCount)
                return FailSound(scenario, $"FluidEmitterKind has {enumCount} values but " +
                                           $"FluidEmitterScanGeometry.KindCount is " +
                                           $"{FluidEmitterScanGeometry.KindCount}. The bin grid is sized and " +
                                           "indexed from the constant, so they must move together.");

            // The far cell with the last valid kind must be the last slot, and one past it must be refused.
            int3 farBin = new int3(FluidEmitterScanGeometry.BinsXZ - 1, FluidEmitterScanGeometry.BinsY - 1,
                FluidEmitterScanGeometry.BinsXZ - 1);

            int last = FluidEmitterScanGeometry.BinIndex(farBin, enumCount - 1);
            if (last != FluidEmitterScanGeometry.BinCount - 1)
                return FailSound(scenario, $"the far bin's last kind indexes {last}, not " +
                                           $"{FluidEmitterScanGeometry.BinCount - 1}.");

            if (FluidEmitterScanGeometry.BinIndex(farBin, enumCount) >= 0)
                return FailSound(scenario, "an out-of-range kind was not refused — it would write past the " +
                                           "end of the bin array.");

            return true;
        }
    }
}
