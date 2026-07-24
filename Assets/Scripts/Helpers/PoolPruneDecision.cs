using System.Runtime.CompilerServices;

namespace Helpers
{
    /// <summary>
    /// Pure idle-cap decision for the pool-pruning linger window (CP-7/F4). While demand has been
    /// observed within the linger window, a pool is granted its hard (service-area) cap so returned
    /// bursts (teleport, view-distance flip, fast flight) get re-used instead of
    /// destroyed-and-reallocated; after a full window with no demand, the surplus is genuine
    /// shrinkage and drains to the soft (row) cap. Demand is the pool's cumulative Get counter
    /// advancing between evaluations — an exact signal, so an actively recycling pool is never
    /// reclaimed mid-use even when same-frame Gets and Returns balance out. Time is a parameter,
    /// making the function pure — the "Pool Prune Decision" suite truth-table-tests it (the
    /// <see cref="ChunkUnloadDecision"/> pattern).
    /// </summary>
    public static class PoolPruneDecision
    {
        /// <summary>Per-pool bookkeeping: the last sampled Get counter and the last observed demand time.</summary>
        public struct State
        {
            /// <summary>The pool's cumulative Get count sampled at the previous evaluation.</summary>
            public long PrevGets;

            /// <summary>Time of the most recent observed demand, in the caller's clock.</summary>
            public float LastDemandTime;
        }

        /// <summary>Resolves the idle cap a pool should prune against for this evaluation.</summary>
        /// <param name="softCap">Row-budget cap that applies after a demand-free window.</param>
        /// <param name="hardCap">Service-area cap that always applies.</param>
        /// <param name="totalGets">The pool's cumulative Get count.</param>
        /// <param name="lingerSeconds">How long a surplus may sit without demand before reclaim.</param>
        /// <param name="now">Current time in the caller's clock (same units as <paramref name="lingerSeconds"/>).</param>
        /// <param name="state">Per-pool bookkeeping, updated in place.</param>
        /// <returns>The cap to hand to the pool's <c>UpdatePruning</c>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Evaluate(int softCap, int hardCap, long totalGets, float lingerSeconds, float now, ref State state)
        {
            if (totalGets != state.PrevGets)
            {
                state.PrevGets = totalGets;
                state.LastDemandTime = now;
            }

            return now - state.LastDemandTime > lingerSeconds ? softCap : hardCap;
        }
    }
}
