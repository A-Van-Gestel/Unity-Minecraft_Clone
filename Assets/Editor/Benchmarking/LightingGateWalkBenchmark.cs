using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Data;
using Editor.Validation.ChunkPipeline.Framework;
using Jobs.Data;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor.Benchmarking
{
    /// <summary>
    /// Editor micro A/B for the neighbor-readiness gates (<c>World.AreNeighborsDataReady</c> /
    /// <c>AreNeighborsReadyAndLit</c>) — the per-chunk cost the lighting ready-set scan pays twice today and
    /// that LP-6 proposes to pay at most once.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a micro A/B and not a frame capture.</b> LP-6's packet retired the millisecond route: three
    /// consecutive runs of identical code reported Lighting Scheduling of 89/33/34 ms, a spread that swamps the
    /// effect in question. This measures the <i>per-call</i> cost instead; multiplied by the call counts the
    /// <c>World.GateCalls*</c> probes collect over a fixed benchmark route, it yields the ms/frame at stake
    /// without fighting frame noise.</para>
    /// <para><b>Real gates, not a model.</b> <see cref="ChunkPipelineFixture"/> stands up a real <c>World</c>
    /// whose <c>worldData</c> and job dictionaries the production gates read directly, so this times the
    /// shipping code path — dictionary probes, <c>IsChunkInWorld</c>, short-circuits and all.</para>
    /// <para><b>Screening only.</b> Editor Mono is slower than IL2CPP and its ratios are not guaranteed to
    /// carry over. What transfers is the <i>shape</i>: which gate costs more, and how much of it is the
    /// <c>LightingJobs</c> probe that two of the three gates never read — measured against a dictionary
    /// filled to production's in-flight cap, since an empty one skips hashing entirely.</para>
    /// </remarks>
    internal static class LightingGateWalkBenchmark
    {
        private const int WARMUP = 200;
        private const int RUNS = 20000;

        /// <summary>Live lighting jobs the isolated probe is timed against — production's in-flight cap, so the
        /// probe hashes into a dictionary the size the real gate walks.</summary>
        private const int IN_FLIGHT_LIGHTING_JOBS = 64;

        /// <summary>Chunk-map radii to sweep — a gate's cost is dominated by dictionary probes, whose cost
        /// tracks map size. 8/16/24 span roughly 289, 1089 and 2401 resident chunks.</summary>
        private static readonly int[] s_mapRadii = { 8, 16, 24 };

        /// <summary>One scenario's timing, in nanoseconds per gate call.</summary>
        private struct Result
        {
            public string Label;
            public double MeanNs;
            public double MinNs;
            public long FactsPerCall;
            public bool Verdict;
        }

        [MenuItem("Minecraft Clone/Benchmarks/Lighting Gate Walk (LP-6)")]
        private static void Run()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                sb.Append("Lighting gate-walk micro A/B (LP-6) — EDITOR MONO, SCREENING ONLY\n");
                sb.Append(RUNS).Append(" runs + ").Append(WARMUP).Append(" warm-ups, per gate call.\n");
                sb.Append("FactsPerCall = neighbors actually examined (8 = full walk, fewer = short-circuit).\n");

                foreach (int radius in s_mapRadii) AppendRadius(sb, radius);

                AppendProbeIsolation(sb);
            }
            catch (Exception e)
            {
                sb.Append("EXCEPTION: ").Append(e.GetType().Name).Append(": ").Append(e.Message)
                    .Append('\n').Append(e.StackTrace);
            }

            string outPath = Path.Combine(Application.temporaryCachePath, "lighting_gate_walk_bench.txt");
            File.WriteAllText(outPath, sb.ToString());
            Debug.Log($"[LightingGateWalkBenchmark] Report written to {outPath}\n\n{sb}");
        }

        /// <summary>Times both gates over one chunk-map size, in the all-ready and short-circuit shapes.</summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="radius">Chunk radius of the resident map.</param>
        private static void AppendRadius(StringBuilder sb, int radius)
        {
            int chunkCount = (2 * radius + 1) * (2 * radius + 1);
            sb.Append("\n── map radius ").Append(radius).Append(" (").Append(chunkCount)
                .Append(" resident chunks) ──\n");

            // All-ready: every gate walks all 8 neighbors and returns true. This is the steady-state interior
            // case and the one the scan pays most often, so it is the figure LP-6's arithmetic uses.
            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                fixture.AddSquare(radius);
                ChunkCoord center = new ChunkCoord(0, 0);

                Append(sb, Measure(fixture, "DataReady   (all 8 ready)", center, readyAndLit: false));
                Append(sb, Measure(fixture, "ReadyAndLit (all 8 ready)", center, readyAndLit: true));
            }

            // Short-circuit: the FIRST neighbor examined blocks, so the loop exits after one gather. The true
            // per-call cost in a streaming world sits between this and the all-ready figure.
            using (ChunkPipelineFixture fixture = new ChunkPipelineFixture())
            {
                fixture.AddSquare(radius);
                ChunkCoord center = new ChunkCoord(0, 0);

                // AllNeighborOffsets' first entry is the neighbor the gate loop reaches first, so unpopulating
                // it blocks DataReady on the first examined neighbor. Only DataReady is measured here; the
                // matching ReadyAndLit short-circuit is not, so read the figure below as that gate's alone.
                // AddSquare populates the whole square, so the first offset always resolves — the null check
                // is defensive. If it ever fired, this would disable a LATER neighbor and the label would lie.
                foreach (Vector3Int offset in VoxelData.AllNeighborOffsets)
                {
                    ChunkData first = fixture.GetChunk(offset.x, offset.z);
                    if (first == null) continue;

                    first.IsPopulated = false;
                    break;
                }

                Append(sb, Measure(fixture, "DataReady   (first neighbor blocks)", center, readyAndLit: false));
            }
        }

        /// <summary>Times one gate on one fixture, reporting mean/min nanoseconds and the neighbors examined.</summary>
        /// <param name="fixture">The stub world holding the seeded chunk map.</param>
        /// <param name="label">Scenario label for the report.</param>
        /// <param name="coord">The chunk whose neighbors are gated.</param>
        /// <param name="readyAndLit">True to time the strict gate; false for the data-ready gate.</param>
        /// <returns>The scenario's result.</returns>
        private static Result Measure(ChunkPipelineFixture fixture, string label, ChunkCoord coord, bool readyAndLit)
        {
            World world = fixture.World;
            bool verdict = false;

            for (int i = 0; i < WARMUP; i++)
                verdict = readyAndLit ? world.AreNeighborsReadyAndLit(coord) : world.AreNeighborsDataReady(coord);

            long factsBefore = world.NeighborFactsGathered;
            long callsBefore = readyAndLit ? world.GateCallsReadyAndLit : world.GateCallsDataReady;

            double minNs = double.MaxValue;
            Stopwatch watch = new Stopwatch();

            // Timed in blocks so one Stopwatch read is amortized over many calls; the block minimum is the
            // noise-resistant figure, the overall mean the representative one.
            const int BLOCK = 500;
            long totalTicks = 0;
            const int blocks = RUNS / BLOCK;
            const long executedCalls = (long)blocks * BLOCK; // NOT RUNS — a non-multiple would divide by calls never made.
            for (int block = 0; block < blocks; block++)
            {
                watch.Restart();
                for (int i = 0; i < BLOCK; i++)
                    verdict = readyAndLit ? world.AreNeighborsReadyAndLit(coord) : world.AreNeighborsDataReady(coord);
                watch.Stop();

                totalTicks += watch.ElapsedTicks;
                double blockNs = watch.ElapsedTicks * (1e9 / Stopwatch.Frequency) / BLOCK;
                if (blockNs < minNs) minNs = blockNs;
            }

            long calls = (readyAndLit ? world.GateCallsReadyAndLit : world.GateCallsDataReady) - callsBefore;
            long facts = world.NeighborFactsGathered - factsBefore;

            return new Result
            {
                Label = label,
                MeanNs = totalTicks * (1e9 / Stopwatch.Frequency) / executedCalls,
                MinNs = minNs,
                FactsPerCall = calls > 0 ? facts / calls : 0,
                Verdict = verdict,
            };
        }

        /// <summary>
        /// Times the lone <c>LightingJobs.ContainsKey</c> probe in isolation — the fact
        /// <c>GatherNeighborFacts</c> assembles for every gate but that only <c>ReadyAndLit</c> reads. Its
        /// cost × 8 neighbors × the DataReady/MeshReady call counts is the saving of the deferred
        /// lazy-fact-gathering follow-up, sized here so that item is filed with a number attached.
        /// </summary>
        /// <param name="sb">The report builder.</param>
        private static void AppendProbeIsolation(StringBuilder sb)
        {
            sb.Append("\n── isolated LightingJobs.ContainsKey probe (the LP-2 fact-gathering question) ──\n");

            foreach (int radius in s_mapRadii)
            {
                using ChunkPipelineFixture fixture = new ChunkPipelineFixture();
                fixture.AddSquare(radius);

                Dictionary<ChunkCoord, LightingJobData> dict = fixture.Jobs.LightingJobs;

                // The fixture seeds this dictionary EMPTY, and an empty Dictionary<,> has no buckets — so
                // ContainsKey would return on a null check without ever hashing, timing a fast path
                // production never takes. Fill it to the in-flight cap the real probe walks against.
                // Scoped to this fixture deliberately: populating LightingJobs on the fixtures used by
                // Measure would make AreNeighborsReadyAndLit see jobs in flight and change its short-circuit
                // behavior, corrupting the gate-walk numbers this benchmark exists to produce.
                for (int i = 0; i < IN_FLIGHT_LIGHTING_JOBS; i++)
                    dict[new ChunkCoord(-1000 - i, -1000)] = default;

                ChunkCoord probe = new ChunkCoord(1, 0);
                bool hit = false;

                for (int i = 0; i < WARMUP; i++) hit = dict.ContainsKey(probe);

                Stopwatch watch = Stopwatch.StartNew();
                for (int i = 0; i < RUNS; i++) hit = dict.ContainsKey(probe);
                watch.Stop();

                double perProbeNs = watch.ElapsedTicks * (1e9 / Stopwatch.Frequency) / RUNS;
                sb.Append("  radius ").Append(radius).Append(": ")
                    .Append(perProbeNs.ToString("F1")).Append(" ns/probe → ")
                    .Append((perProbeNs * 8).ToString("F1")).Append(" ns per wasted 8-neighbor walk")
                    .Append(hit ? " (hit)" : " (miss)").Append('\n');
            }
        }

        /// <summary>Appends one scenario's row to the report.</summary>
        /// <param name="sb">The report builder.</param>
        /// <param name="result">The scenario result to render.</param>
        private static void Append(StringBuilder sb, Result result)
        {
            sb.Append("  ").Append(result.Label.PadRight(36))
                .Append(" mean ").Append(result.MeanNs.ToString("F1").PadLeft(8)).Append(" ns")
                .Append("   min ").Append(result.MinNs.ToString("F1").PadLeft(8)).Append(" ns")
                .Append("   facts/call ").Append(result.FactsPerCall)
                .Append("   → ").Append(result.Verdict)
                .Append('\n');
        }
    }
}
