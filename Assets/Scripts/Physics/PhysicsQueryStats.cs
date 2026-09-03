using UnityEngine;

namespace Physics
{
    /// <summary>
    /// Dev-only counters for the collision solver's voxel query volume — <c>PH-1</c>'s measurement instrument.
    /// <para>
    /// It exists because <c>PH-1</c> is <b>behavior-neutral by design</b>: a green suite says nothing about
    /// whether the refactor achieved anything, and a gather whose envelope is too small still resolves correctly
    /// (every sweep just falls back). <see cref="Fallbacks"/> is therefore the only signal that separates "gathered
    /// once" from "silently re-scanning per sweep as before", and it is what the step-up baselines assert against.
    /// </para>
    /// <para>
    /// Every increment goes through a <c>[Conditional]</c> method, so release builds compile the call sites away
    /// entirely rather than paying for counters no one reads.
    /// </para>
    /// </summary>
    public static class PhysicsQueryStats
    {
        /// <summary>Gather passes run (one per resolve, so one per substep).</summary>
        public static int Gathers;

        /// <summary>Cells visited by gather passes — the grid positions actually looked up.</summary>
        public static int CellsScannedByGather;

        /// <summary>Cells visited by direct <c>World.CheckPhysicsCollision</c> scans (the pre-PH-1 cost shape).</summary>
        public static int CellsScannedDirectly;

        /// <summary>Sweeps issued by the solver, however they were answered.</summary>
        public static int SweepQueries;

        /// <summary>Sweeps the gathered buffer could not answer, which fell back to a direct scan.</summary>
        public static int Fallbacks;

        /// <summary>
        /// The <b>counterfactual</b>: cells the solver would have read if every sweep still ran its own scan, summed
        /// from each sweep's own cell range. Measured in the same run as
        /// <see cref="CellsScannedByGather"/> so a before/after comparison needs no second build and carries no
        /// A/B drift — this is the number <c>PH-1</c> is judged on.
        /// </summary>
        public static int CellsScannedIfUngathered;

        /// <summary>Physics ticks that ran collision (one per <c>CalculateVelocity</c>), for per-tick averages.</summary>
        public static int Ticks;

        /// <summary>
        /// Zeroes every counter. Also the play-mode entry reset: with domain reload disabled these statics would
        /// otherwise carry the previous session's totals into the next one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            Gathers = 0;
            CellsScannedByGather = 0;
            CellsScannedDirectly = 0;
            SweepQueries = 0;
            Fallbacks = 0;
            CellsScannedIfUngathered = 0;
            Ticks = 0;
        }

        /// <summary>Records one physics tick that ran collision resolution.</summary>
        [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
        public static void CountTick() => Ticks++;

        /// <summary>Records what one sweep's own scan range would have cost before the gather existed.</summary>
        /// <param name="cellsInSweepRange">Cells in this sweep's own floor-range.</param>
        [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
        public static void CountCounterfactual(int cellsInSweepRange)
        {
            CellsScannedIfUngathered += cellsInSweepRange;
        }

        /// <summary>Records one gather pass.</summary>
        /// <param name="cellsScanned">Grid positions the pass looked up.</param>
        [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
        public static void CountGather(int cellsScanned)
        {
            Gathers++;
            CellsScannedByGather += cellsScanned;
        }

        /// <summary>Records one direct scan.</summary>
        /// <param name="cellsScanned">Grid positions the scan looked up.</param>
        [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
        public static void CountDirectScan(int cellsScanned)
        {
            CellsScannedDirectly += cellsScanned;
        }

        /// <summary>Records one solver sweep.</summary>
        /// <param name="fellBack">True when the gathered buffer could not answer it.</param>
        [System.Diagnostics.Conditional("UNITY_INCLUDE_INSTRUMENTATION")]
        public static void CountSweep(bool fellBack)
        {
            SweepQueries++;
            if (fellBack) Fallbacks++;
        }
    }
}
