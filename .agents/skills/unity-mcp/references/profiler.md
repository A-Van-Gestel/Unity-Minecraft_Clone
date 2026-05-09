<objective>
Reference for all 10 Unity MCP profiler tools. These tools query GC allocations, frame timing, and cross-thread analysis from Unity's Profiler. They only work when profiling data is loaded (play mode with profiler recording active).
</objective>

<prerequisite>
**Before calling any profiler tool**, verify profiling data exists:

```json
Unity_ManageEditor: { "Action": "GetState" }
```

Check the response for active profiling data. If the profiler has no data, these tools return empty/error results.
</prerequisite>

<gc_allocation_tools>

**GC Allocation Analysis — find what's generating garbage**

| Tool                                 | Parameters                                 | Use                                       |
|--------------------------------------|--------------------------------------------|-------------------------------------------|
| `GetOverallGcAllocations`            | *(none)*                                   | Summary of GC across all captured frames  |
| `GetFrameGcAllocations`              | `frameIndex`                               | Top GC allocators in a specific frame     |
| `GetFrameRangeGcAllocations`         | `startFrameIndex`, `lastFrameIndex`        | GC summary over a frame range             |
| `GetSampleGcAllocations`             | `frameIndex`, `threadName`, `sampleId`     | GC for a specific profiler sample (by ID) |
| `GetSampleGcAllocationsByMarkerPath` | `frameIndex`, `threadName`, `markerIdPath` | GC for a sample (by marker path)          |

</gc_allocation_tools>

<timing_tools>

**Time / Performance Analysis — find what's slow**

| Tool                               | Parameters                                             | Use                                           |
|------------------------------------|--------------------------------------------------------|-----------------------------------------------|
| `GetFrameTopTimeSamples`           | `frameIndex`, `targetFrameTime`                        | Top time consumers in a frame                 |
| `GetFrameRangeTopTimeSummary`      | `startFrameIndex`, `lastFrameIndex`, `targetFrameTime` | Time summary over frame range                 |
| `GetFrameSelfTimeSamples`          | `frameIndex`                                           | Self-time (excludes children) per frame       |
| `GetSampleTimeSummary`             | `frameIndex`, `threadName`, `sampleId`                 | Drill into a specific sample's timing (by ID) |
| `GetSampleTimeSummaryByMarkerPath` | `frameIndex`, `threadName`, `markerIdPath`             | Timing by marker path                         |

**`targetFrameTime` parameter:** Pass the target frame budget in milliseconds. For 60 FPS pass `16.67`, for 30 FPS pass `33.33`. The tools use this to flag samples that exceed the budget.

</timing_tools>

<analysis_tools>

**Deep Analysis — drill down and correlate**

| Tool                    | Parameters                                                  | Use                            |
|-------------------------|-------------------------------------------------------------|--------------------------------|
| `GetBottomUpSampleTime` | `frameIndex`, `threadName`, `bottomUpId`                    | Bottom-up analysis of a sample |
| `GetRelatedSamples`     | `frameIndex`, `threadName`, `sampleId`, `relatedThreadName` | Cross-thread correlation       |

</analysis_tools>

<drill_down_workflow>

**Step-by-step profiler drill-down:**

1. **Confirm data exists:** `Unity_ManageEditor` -> `GetState`
2. **Start broad:** `GetOverallGcAllocations` or `GetFrameRangeTopTimeSummary` over a frame range
3. **Identify hot frames:** Look for spikes in the summary
4. **Drill into hot frame:** `GetFrameGcAllocations` or `GetFrameTopTimeSamples` on the specific frame
5. **Drill into sample:** `GetSampleTimeSummary` on the specific sample
6. **Cross-thread analysis:** `GetRelatedSamples` to find correlated work on job threads

</drill_down_workflow>
