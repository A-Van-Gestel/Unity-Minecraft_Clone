using Helpers;

namespace Editor.Validation.PipelineBackpressure
{
    /// <summary>
    /// The visit start of a budgeted completion pass (<see cref="PipelinePassBudget.NextScanStart"/>): pure, no world.
    /// <para><b>Prove-red (by temporary mutation):</b> dropping the <c>!hasBudget</c> clause reds B35 (an unbudgeted call
    /// moves the cursor); a cursor that never advances, or one that advances without the modulo, reds B35.</para>
    /// </summary>
    public static partial class PipelineBackpressureValidationSuite
    {
        private const int SCAN_START_COUNT = 5;
        private const int SCAN_START_CALLS_UNBUDGETED = 4;
        private const int SCAN_START_STALE_CURSOR = 9;
        private const int SCAN_START_SHRUNKEN_COUNT = 3;

        /// <summary>
        /// B35. Unbudgeted: start 0 and the cursor untouched. Budgeted: one slot per call, wrapping, every index a start
        /// exactly once per lap, a degenerate count safe, and a cursor left over from a larger candidate set clamped in.
        /// </summary>
        private static bool RunB35ScanStart()
        {
            int cursor = 2;
            bool unbudgetedStable = true;
            for (int i = 0; i < SCAN_START_CALLS_UNBUDGETED; i++)
                unbudgetedStable &= PipelinePassBudget.NextScanStart(ref cursor, SCAN_START_COUNT, false) == 0 && cursor == 2;
            bool ok = Check("without a budget the start is 0 and the cursor stays where it was, call after call", unbudgetedStable);

            cursor = 0;
            int[] expected = { 1, 2, 3, 4, 0, 1 };
            string seen = "";
            bool sequenceMatches = true;
            foreach (int want in expected)
            {
                int got = PipelinePassBudget.NextScanStart(ref cursor, SCAN_START_COUNT, true);
                seen += got + " ";
                sequenceMatches &= got == want && cursor == want;
            }

            ok &= Check($"with a budget the start advances one slot per call and wraps: 1,2,3,4,0,1 (got {seen.TrimEnd()})",
                sequenceMatches);

            cursor = 0;
            int[] starts = new int[SCAN_START_COUNT];
            for (int i = 0; i < SCAN_START_COUNT; i++)
                starts[PipelinePassBudget.NextScanStart(ref cursor, SCAN_START_COUNT, true)]++;
            bool everyOnce = true;
            foreach (int visits in starts) everyOnce &= visits == 1;
            ok &= Check($"over {SCAN_START_COUNT} consecutive budgeted calls every index is a start exactly once", everyOnce);

            int zeroCursor = 3;
            bool zeroSafe = true;
            foreach (bool hasBudget in new[] { true, false })
                zeroSafe &= PipelinePassBudget.NextScanStart(ref zeroCursor, 0, hasBudget) == 0;
            ok &= Check("a count of 0 returns 0 with and without a budget (no divide by zero)", zeroSafe);

            cursor = SCAN_START_STALE_CURSOR;
            int shrunk = PipelinePassBudget.NextScanStart(ref cursor, SCAN_START_SHRUNKEN_COUNT, true);
            ok &= Check($"a cursor larger than a shrunken count still yields an index inside [0, {SCAN_START_SHRUNKEN_COUNT}) (got {shrunk}, cursor {cursor})",
                shrunk >= 0 && shrunk < SCAN_START_SHRUNKEN_COUNT && cursor == shrunk);

            return ok;
        }
    }
}
