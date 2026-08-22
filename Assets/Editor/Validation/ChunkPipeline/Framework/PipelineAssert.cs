using System.Collections.Generic;
using System.Text;
using Data;

namespace Editor.Validation.ChunkPipeline.Framework
{
    /// <summary>
    /// Assertions for NS-3's two families: <b>convergence</b> (every chunk eventually reaches lit + meshed —
    /// the anti-deadlock property) and <b>flag pairing</b> (after every step, no flag is left set whose clear
    /// site is unreachable). Both carry non-vacuity floors, because both are trivially satisfiable: a pump
    /// that schedules nothing leaves no flags set, and a scenario whose adversarial order never actually
    /// blocked a chunk converges without exercising anything.
    /// </summary>
    public static class PipelineAssert
    {
        private const int MAX_REPORTED_CHUNKS = 8;

        /// <summary>
        /// Asserts every target chunk meshed within the frame budget, and that the run was non-vacuous:
        /// at least one chunk must have been blocked (parked or mesh-declined) at some point, or the
        /// scenario's adversarial ordering never bit and the convergence result proves nothing.
        /// </summary>
        /// <param name="label">Assertion label for the console line.</param>
        /// <param name="converged">Whether the pump reported convergence within its budget.</param>
        /// <param name="simulator">The pump, for the per-chunk failure diff.</param>
        /// <param name="targets">The chunks required to reach the meshed state.</param>
        /// <param name="totals">The summed per-frame counters.</param>
        /// <param name="requireBlocking">Whether to enforce the blocked-at-least-once floor.</param>
        /// <param name="log">The console sink.</param>
        /// <returns>True when the assertion passes.</returns>
        public static bool Converged(
            string label,
            bool converged,
            ChunkPipelineSimulator simulator,
            IReadOnlyList<ChunkCoord> targets,
            ChunkPipelineSimulator.FrameResult totals,
            bool requireBlocking,
            StringBuilder log)
        {
            if (!converged)
            {
                StringBuilder stuck = new StringBuilder();
                int reported = 0;
                int unmeshed = 0;
                foreach (ChunkCoord target in targets)
                {
                    if (simulator.AllMeshed(new[] { target })) continue;
                    unmeshed++;
                    if (reported >= MAX_REPORTED_CHUNKS) continue;
                    stuck.Append(reported++ == 0 ? "" : ", ").Append(Describe(target));
                }

                if (unmeshed > reported) stuck.Append(", …");
                return Fail(label,
                    $"did not converge in {simulator.Frame} frames — unmeshed: {stuck}. " +
                    $"parked={totals.LightingParked}, meshDeclined={totals.MeshDeclined}, " +
                    $"deferredStrand={totals.UnloadDeferredStrand}", log);
            }

            // Scoped to the observed set on purpose: frontier chunks park unconditionally (their own
            // neighbors were never seeded), so a floor reading the global counters can never fail.
            if (requireBlocking && totals.ObservedParked == 0 && totals.ObservedMeshDeclined == 0)
                return Fail(label,
                    "converged VACUOUSLY — no chunk under test was ever parked or mesh-declined, so the " +
                    "scenario's adversarial ordering never exercised a gate on it. Fix the scenario, not the " +
                    $"engine. (Frontier parks this run: {totals.LightingParked}, which prove nothing.)", log);

            return Pass(label,
                $"converged in {simulator.Frame} frames (observed parked={totals.ObservedParked}, " +
                $"observed meshDeclined={totals.ObservedMeshDeclined})",
                log);
        }

        /// <summary>
        /// Asserts a chunk is still stuck holding <c>HasLightChangesToProcess</c> — the §9.6 end state, in
        /// which a stranded chunk can neither schedule lighting (a neighbor is gone, so
        /// <c>AreNeighborsDataReady</c> can never pass), nor mesh, nor be unloaded.
        /// <para>Meshing is deliberately <b>not</b> the signal here: any chunk adjacent to an unloaded
        /// neighbor fails the mesh gate on the missing neighbor alone, so a mesh-based assertion would go
        /// red whether or not stranding was fixed. The flag is what distinguishes the two.</para>
        /// </summary>
        /// <param name="label">Assertion label for the console line.</param>
        /// <param name="chunk">The chunk expected to be stranded.</param>
        /// <param name="log">The console sink.</param>
        /// <returns>True when the chunk is indeed stuck light-pending.</returns>
        public static bool StuckLightPending(string label, ChunkData chunk, StringBuilder log)
        {
            if (chunk == null)
                return Fail(label, "the chunk under test was itself unloaded — the scenario " +
                                   "no longer tests stranding", log);

            return chunk.HasLightChangesToProcess
                ? Pass(label, "still holds HasLightChangesToProcess with no reachable clear site", log)
                : Fail(label, "the chunk cleared its lighting flag — it was never stranded, so this scenario " +
                              "does not reproduce §9.6", log);
        }

        /// <summary>
        /// Asserts no <b>interior</b> chunk ends with a lighting flag set whose clear site is unreachable —
        /// a settled pipeline has no pending lighting work and nothing parked mid-merge. Enforces a
        /// non-vacuity floor: at least one flag-driven schedule must have happened during the run.
        /// <para>The sweep is deliberately scoped to the caller's chunks rather than the whole world. A
        /// frontier chunk legitimately parks with <c>HasLightChangesToProcess</c> set while it waits for
        /// neighbors that do not exist yet (<c>AreNeighborsDataReady</c> can never pass at the edge of the
        /// seeded region), so sweeping every registered chunk would report correct behavior as a defect.</para>
        /// </summary>
        /// <param name="label">Assertion label for the console line.</param>
        /// <param name="fixture">The fixture whose chunks are swept.</param>
        /// <param name="interior">The chunks whose flags must have cleared — never the frontier ring.</param>
        /// <param name="flagsExercised">How many flag-driven schedules the run performed.</param>
        /// <param name="log">The console sink.</param>
        /// <returns>True when the assertion passes.</returns>
        public static bool FlagsPaired(
            string label,
            ChunkPipelineFixture fixture,
            IReadOnlyList<ChunkCoord> interior,
            int flagsExercised,
            StringBuilder log)
        {
            if (flagsExercised == 0)
                return Fail(label,
                    "no lighting flag was ever set during the run — the sweep would pass on an empty world. " +
                    "The scenario is vacuous, not the engine clean.", log);

            StringBuilder offenders = new StringBuilder();
            int count = 0;
            for (int i = 0; i < interior.Count; i++)
            {
                ChunkData chunk = fixture.GetChunk(interior[i].X, interior[i].Z);
                if (chunk == null || !chunk.IsPopulated) continue;
                if (!chunk.NeedsInitialLighting && !chunk.HasLightChangesToProcess && !chunk.IsAwaitingMainThreadProcess)
                    continue;

                if (count < MAX_REPORTED_CHUNKS)
                {
                    offenders.Append(count == 0 ? "" : ", ")
                        .Append($"{Describe(interior[i])}[")
                        .Append(chunk.NeedsInitialLighting ? "NeedsInitialLighting " : "")
                        .Append(chunk.HasLightChangesToProcess ? "HasLightChangesToProcess " : "")
                        .Append(chunk.IsAwaitingMainThreadProcess ? "IsAwaitingMainThreadProcess" : "")
                        .Append(']');
                }

                count++;
            }

            if (count > 0)
                return Fail(label, $"{count} chunk(s) ended with unclearable lighting flags: {offenders}" +
                                   (count > MAX_REPORTED_CHUNKS ? ", …" : ""), log);

            return Pass(label, $"no stranded flags across {interior.Count} interior chunks", log);
        }

        private static string Describe(ChunkCoord coord) => $"({coord.X},{coord.Z})";

        private static bool Pass(string label, string detail, StringBuilder log)
        {
            log.AppendLine($"  [PASS] {label} — {detail}");
            return true;
        }

        private static bool Fail(string label, string detail, StringBuilder log)
        {
            log.AppendLine($"  [FAIL] {label} — {detail}");
            return false;
        }
    }
}
