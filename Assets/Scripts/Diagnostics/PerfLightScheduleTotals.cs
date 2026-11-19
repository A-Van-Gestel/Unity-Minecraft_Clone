using Helpers;

namespace Diagnostics
{
    /// <summary>
    /// Running totals of the gameplay lighting jobs scheduled, by arm: the scan's initial, edge and regular arms, and the
    /// initial lighting a chunk loaded from disk schedules directly. The owner counts only while the monitor's slots
    /// record and samples them into the per-frame <c>LightSched*</c> <see cref="PerfCounter"/>s. Main thread only.
    /// </summary>
    public struct PerfLightScheduleTotals
    {
        /// <summary>Jobs the scan scheduled on its initial-lighting arm.</summary>
        public long Initial;

        /// <summary>Jobs the scan scheduled on its edge-check arm.</summary>
        public long Edge;

        /// <summary>Jobs the scan scheduled on its regular light-changes arm.</summary>
        public long Regular;

        /// <summary>Initial-lighting jobs a chunk loaded from disk scheduled outside the scan.</summary>
        public long LoadInitial;

        /// <summary>Counts one job the scan scheduled on <paramref name="action"/>'s arm; a non-scheduling action counts nothing.</summary>
        /// <param name="action">The scan arm that scheduled the job.</param>
        public void RecordScan(LightingScanDecision.ScanAction action)
        {
            switch (action)
            {
                case LightingScanDecision.ScanAction.ScheduleInitial:
                    Initial++;
                    break;
                case LightingScanDecision.ScanAction.ScheduleEdge:
                    Edge++;
                    break;
                case LightingScanDecision.ScanAction.ScheduleRegular:
                    Regular++;
                    break;
            }
        }
    }
}
