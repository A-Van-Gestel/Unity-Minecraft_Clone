using System.Collections.Generic;
using System.Text;
using Editor.Validation.Lighting.Framework;
using UnityEditor;
using UnityEngine;
using Random = System.Random;

namespace Editor.Dev
{
    /// <summary>
    /// P9-2 investigation probe (design doc <c>CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md</c> §6 Option B1):
    /// measures how many post-generation edge-check rounds recompute an <b>unchanged</b> light field.
    /// <para>
    /// P9-1 sized the lighting multiplier at 6.28 quota units per delivered chunk but proved nothing
    /// redundant. The suspect mechanism is that <c>WorldJobManager.MergeCompletedLightingJob</c> re-arms the
    /// cascade (self <c>NeedsEdgeCheck</c> + <c>TriggerNeighborEdgeChecks</c>) on <i>stability</i>, never on
    /// <i>effect</i> — and a pass that changed nothing is stable by definition. This probe drives the real
    /// harness rounds one at a time, diffing every chunk's light field across each round, and reports the
    /// no-op fraction plus the saving the proposed outcome-conditional rule would have realized.
    /// </para>
    /// <para><b>Read the unit carefully.</b> One tallied "chunk-round" is a <i>flagging wave</i>: every chunk
    /// is flagged and the grid is then run to quiescence, so a single round folds in an unbounded number of
    /// production lighting passes. Production instead flags only the cardinal neighbors of chunks that
    /// actually stabilized, bounded by <c>RemainingEdgeCheckRounds</c> — which this probe ignores entirely.
    /// A no-op round therefore means "this whole wave changed nothing for this chunk", which is a sound
    /// statement about redundancy but is <b>not</b> the same unit as the per-schedule multiplier (6.28) that
    /// P9-1 measured. Do not quote the two as if they were.</para>
    /// <para><b>Not a validation suite</b> — it reports numbers, it does not pass or fail, and it is not
    /// registered with <c>Validate All</c>.</para>
    /// <para><b>KEEP (decided 2026-08-02).</b> An earlier version of this docstring said "delete once P9-2
    /// has its verdict". P9-2 has its verdict — it shipped — and this probe is the reproducible evidence
    /// behind design doc §3.3b, which is the only place the redundancy claim is demonstrated rather than
    /// asserted. It is also the cheapest way to re-test the premise if the cascade is ever changed again:
    /// run it, and a healthy engine should still report a high no-op fraction. Delete it only alongside
    /// §3.3b itself.</para>
    /// </summary>
    public static class EdgeCheckRedundancyProbe
    {
        /// <summary>Chunks per axis in each probe world — 5 matches the Bug-05 canopy fuzz's grid.</summary>
        private const int GRID = 5;

        /// <summary>Y of the stone floor's top surface in every fixture.</summary>
        private const int FLOOR_TOP = 10;

        /// <summary>Seeds swept for the randomized dense-canopy family.</summary>
        private const int CANOPY_SEEDS = 12;

        /// <summary>Edge-check rounds driven per world. Production runs 2 (<c>RemainingEdgeCheckRounds</c>);
        /// the extra rounds are observational — they show whether anything is still converging past the budget.</summary>
        private const int PROBE_ROUNDS = 4;

        /// <summary>Production's real edge-check round budget, for labeling which rounds are in-budget.</summary>
        private const int PRODUCTION_ROUNDS = 2;

        [MenuItem("Minecraft Clone/Dev/P9-2 Edge-Check Redundancy Probe")]
        public static void Run()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine("--- P9-2 edge-check redundancy probe ---");
            report.AppendLine($"grid {GRID}x{GRID} chunks, {PROBE_ROUNDS} edge rounds driven (production budget = {PRODUCTION_ROUNDS})");
            report.AppendLine();

            RoundTotals[] flat = ProbeWorld(BuildSuperflatWorld(), report, "superflat floor", settleFirst: true);
            if (flat == null) report.AppendLine("[superflat floor] FAILED TO CONVERGE — no rows produced.\n");

            SweepCanopy(report, $"dense canopy ({CANOPY_SEEDS.ToString()} seeds, aggregated) — SETTLED first", settleFirst: true);

            // POSITIVE CONTROL. The families above settle the whole grid to quiescence before any edge
            // round, which is the one condition under which an edge check is trivially a no-op — so a
            // 100 % no-op reading there proves nothing until the same instrument is shown to REPORT a
            // change when one exists. This family runs a single initial wave instead (every chunk lit
            // from unlit neighbor snapshots, nothing reconciled yet) and must show non-zero changes.
            SweepCanopy(report, $"dense canopy ({CANOPY_SEEDS.ToString()} seeds, aggregated) — UNSETTLED (control)", settleFirst: false);

            report.AppendLine();
            report.AppendLine("Reading: a 'chunk-round' is one FLAGGING WAVE run to quiescence, not one lighting");
            report.AppendLine("schedule — see the class docstring before comparing these to the 6.28 multiplier.");
            report.AppendLine("'no-op' = the wave wrote zero light changes into that chunk, i.e. it recomputed an");
            report.AppendLine("unchanged result. 'would skip' = chunk-rounds the proposed outcome-conditional rule");
            report.AppendLine("(propagate only when the previous pass changed light) would not have scheduled.");

            Debug.Log(report.ToString());
        }

        /// <summary>Sweeps the seeded canopy family and appends its aggregated table.</summary>
        /// <param name="report">Report to append to.</param>
        /// <param name="label">Family label.</param>
        /// <param name="settleFirst">Whether to converge the grid fully before the edge rounds.</param>
        private static void SweepCanopy(StringBuilder report, string label, bool settleFirst)
        {
            RoundTotals[] totals = new RoundTotals[PROBE_ROUNDS];
            for (int seed = 0; seed < CANOPY_SEEDS; seed++)
            {
                RoundTotals[] one = ProbeWorld(BuildCanopyWorld(seed), report: null, label: null, settleFirst);
                if (one == null)
                {
                    report.AppendLine($"  {label}: seed {seed.ToString()} FAILED TO CONVERGE — excluded");
                    continue;
                }

                for (int r = 0; r < PROBE_ROUNDS; r++)
                    totals[r].Add(in one[r]);
            }

            AppendFamily(report, label, totals);
        }

        /// <summary>Per-round tallies, summed across the worlds of one fixture family.</summary>
        private struct RoundTotals
        {
            /// <summary>Chunk-rounds actually driven (production flags every chunk each round).</summary>
            public int ChunkRounds;

            /// <summary>Chunk-rounds whose light field differed afterwards.</summary>
            public int Changed;

            /// <summary>Voxels whose packed light value differed afterwards.</summary>
            public long VoxelsChanged;

            /// <summary>Chunk-rounds the outcome-conditional rule would not have scheduled.</summary>
            public int WouldSkip;

            /// <summary>Chunk-rounds skipped by the rule that DID change — a correctness loss, must stay 0.</summary>
            public int SkippedButChanged;

            public void Add(in RoundTotals other)
            {
                ChunkRounds += other.ChunkRounds;
                Changed += other.Changed;
                VoxelsChanged += other.VoxelsChanged;
                WouldSkip += other.WouldSkip;
                SkippedButChanged += other.SkippedButChanged;
            }
        }

        /// <summary>
        /// Runs one world's initial lighting, then drives <see cref="PROBE_ROUNDS"/> edge-check rounds,
        /// diffing every chunk's light field around each one.
        /// </summary>
        /// <param name="world">The authored, unlit world; disposed here.</param>
        /// <param name="report">Report to append this world's own table to, or null to only return totals.</param>
        /// <param name="label">Family label for the appended table.</param>
        /// <param name="settleFirst">True to converge the grid to quiescence before the edge rounds;
        /// false to run a SINGLE initial wave, leaving the field unreconciled (the positive control).</param>
        /// <returns>Per-round totals, or null when a stage failed to converge.</returns>
        private static RoundTotals[] ProbeWorld(
            LightingTestWorld world, StringBuilder report, string label, bool settleFirst)
        {
            using (world)
            {
                List<Vector2Int> coords = new List<Vector2Int>(world.AllChunkCoords());

                foreach (Vector2Int coord in coords)
                    world.QueueFullSunlightRecalc(coord);

                // maxRounds:1 performs exactly one wave and then reports -1 because work remains — that
                // "failure" IS the control's setup, so it is deliberately not treated as one.
                if (settleFirst)
                {
                    if (world.RunWaveToConvergence() < 0) return null;
                }
                else
                {
                    world.RunWaveToConvergence(1);
                }

                RoundTotals[] totals = new RoundTotals[PROBE_ROUNDS];

                // Whether each chunk's PREVIOUS round changed its light. The initial wave always changes
                // everything, so round 1 is scheduled for every chunk under either rule.
                Dictionary<Vector2Int, bool> changedLastRound = new Dictionary<Vector2Int, bool>();
                foreach (Vector2Int coord in coords)
                    changedLastRound[coord] = true;

                Dictionary<Vector2Int, ushort[]> before = new Dictionary<Vector2Int, ushort[]>();

                for (int round = 0; round < PROBE_ROUNDS; round++)
                {
                    foreach (Vector2Int coord in coords)
                        before[coord] = CaptureLight(world, coord);

                    foreach (Vector2Int coord in coords)
                    {
                        world.GetChunkData(coord).FlagEdgeCheck();
                        world.FlagLightWork(coord);
                    }

                    if (world.RunWaveToConvergence() < 0) return null;

                    Dictionary<Vector2Int, bool> changedThisRound = new Dictionary<Vector2Int, bool>();
                    foreach (Vector2Int coord in coords)
                    {
                        int diff = CountLightDiff(world, coord, before[coord]);
                        bool changed = diff > 0;
                        changedThisRound[coord] = changed;

                        // The proposed rule schedules a chunk's next edge check only when the previous pass
                        // changed ITS light or a cardinal neighbor's — the two paths that can move a border.
                        bool wouldRun = changedLastRound[coord] || AnyCardinalChanged(coords, changedLastRound, coord);

                        totals[round].ChunkRounds++;
                        if (changed) totals[round].Changed++;
                        totals[round].VoxelsChanged += diff;
                        if (!wouldRun)
                        {
                            totals[round].WouldSkip++;
                            if (changed) totals[round].SkippedButChanged++;
                        }
                    }

                    changedLastRound = changedThisRound;
                }

                if (report != null) AppendFamily(report, label, totals);
                return totals;
            }
        }

        /// <summary>Whether any of a chunk's 4 cardinal neighbors inside the grid changed last round.</summary>
        private static bool AnyCardinalChanged(
            List<Vector2Int> coords, Dictionary<Vector2Int, bool> changedLastRound, Vector2Int coord)
        {
            // The grid's edge chunks have fewer in-grid neighbors than production's interior chunks do,
            // which biases WouldSkip UP (fewer trigger sources). Noted in the probe's write-up.
            return (changedLastRound.TryGetValue(coord + Vector2Int.up, out bool n) && n)
                   || (changedLastRound.TryGetValue(coord + Vector2Int.down, out bool s) && s)
                   || (changedLastRound.TryGetValue(coord + Vector2Int.right, out bool e) && e)
                   || (changedLastRound.TryGetValue(coord + Vector2Int.left, out bool w) && w);
        }

        /// <summary>Copies one chunk's full packed light field into a flat buffer for later diffing.</summary>
        private static ushort[] CaptureLight(LightingTestWorld world, Vector2Int chunkCoord)
        {
            Vector2Int origin = world.GetChunkData(chunkCoord).Position;
            ushort[] buffer = new ushort[VoxelData.ChunkWidth * VoxelData.ChunkHeight * VoxelData.ChunkWidth];

            int i = 0;
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
                buffer[i++] = world.GetLightData(new Vector3Int(origin.x + x, y, origin.y + z));

            return buffer;
        }

        /// <summary>Counts voxels whose packed light value differs from the captured snapshot.</summary>
        private static int CountLightDiff(LightingTestWorld world, Vector2Int chunkCoord, ushort[] before)
        {
            Vector2Int origin = world.GetChunkData(chunkCoord).Position;
            int diff = 0;

            int i = 0;
            for (int x = 0; x < VoxelData.ChunkWidth; x++)
            for (int y = 0; y < VoxelData.ChunkHeight; y++)
            for (int z = 0; z < VoxelData.ChunkWidth; z++)
            {
                if (world.GetLightData(new Vector3Int(origin.x + x, y, origin.y + z)) != before[i]) diff++;
                i++;
            }

            return diff;
        }

        /// <summary>Appends one fixture family's per-round table.</summary>
        private static void AppendFamily(StringBuilder report, string label, RoundTotals[] totals)
        {
            report.AppendLine($"[{label}]");
            report.AppendLine("  round | chunk-rounds | changed | no-op | no-op % | voxels changed | would skip | SKIPPED-BUT-CHANGED");
            for (int r = 0; r < totals.Length; r++)
            {
                RoundTotals t = totals[r];
                if (t.ChunkRounds == 0) continue;

                int noOp = t.ChunkRounds - t.Changed;
                string budget = r < PRODUCTION_ROUNDS ? " " : "*";
                report.AppendLine(
                    $"  {(r + 1).ToString()}{budget}    | {t.ChunkRounds.ToString(),12} | {t.Changed.ToString(),7} | {noOp.ToString(),5} | " +
                    $"{(100f * noOp / t.ChunkRounds).ToString("F1"),6}% | {t.VoxelsChanged.ToString(),14} | {t.WouldSkip.ToString(),10} | {t.SkippedButChanged.ToString(),19}");
            }

            report.AppendLine("  (* = beyond production's 2-round budget; observational only)");
            report.AppendLine();
        }

        /// <summary>A bare superflat world — the common, trivially-converged case.</summary>
        private static LightingTestWorld BuildSuperflatWorld()
        {
            LightingTestWorld world = new LightingTestWorld(GRID);
            world.FillSuperflatFloor(FLOOR_TOP, TestBlockPalette.Stone);
            world.RecalculateHeightmaps();
            return world;
        }

        /// <summary>
        /// A seeded dense-canopy world — the adversarial case, geometrically equivalent to the Bug-05
        /// canopy fuzz (canopy over the whole grid, a few sky wells, opaque under-canopy dividers with
        /// doorways) so under-canopy light must thread across several chunk borders to converge.
        /// </summary>
        /// <param name="seed">The iteration seed; the geometry is a pure function of it.</param>
        /// <returns>The authored, unlit world.</returns>
        private static LightingTestWorld BuildCanopyWorld(int seed)
        {
            Random rng = new Random(unchecked(seed * 0x27D4EB2D + 1));
            const int worldWidth = GRID * VoxelData.ChunkWidth;
            const int worldMax = worldWidth - 1;
            const int gapBottomY = FLOOR_TOP + 1;

            int gapHeight = 1 + rng.Next(4);
            int canopyBaseY = gapBottomY + gapHeight;
            int canopyTopY = canopyBaseY + rng.Next(3);

            LightingTestWorld world = new LightingTestWorld(GRID);
            world.FillSuperflatFloor(FLOOR_TOP, TestBlockPalette.Stone);
            world.FillBox(new Vector3Int(0, canopyBaseY, 0), new Vector3Int(worldMax, canopyTopY, worldMax),
                TestBlockPalette.Leaves);

            int wallCount = rng.Next(9);
            for (int i = 0; i < wallCount; i++)
            {
                bool isXWall = rng.Next(2) == 0;
                int fixedCoord = 1 + rng.Next(worldWidth - 2);
                int doorway = rng.Next(worldWidth);

                for (int along = 0; along < worldWidth; along++)
                {
                    if (along == doorway) continue;
                    for (int y = gapBottomY; y < canopyBaseY; y++)
                    {
                        world.SetBlock(isXWall
                            ? new Vector3Int(fixedCoord, y, along)
                            : new Vector3Int(along, y, fixedCoord), TestBlockPalette.Stone);
                    }
                }
            }

            // Sky wells last so a well always wins over a divider sharing its column.
            int wellCount = 1 + rng.Next(3);
            for (int i = 0; i < wellCount; i++)
            {
                int wx = rng.Next(worldWidth);
                int wz = rng.Next(worldWidth);
                for (int y = gapBottomY; y < VoxelData.ChunkHeight; y++)
                    world.SetBlock(new Vector3Int(wx, y, wz), TestBlockPalette.Air);
            }

            world.RecalculateHeightmaps();
            return world;
        }
    }
}
