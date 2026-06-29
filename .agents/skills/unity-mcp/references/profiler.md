<objective>
Reference for all 10 Unity MCP profiler tools. These tools query GC allocations, frame timing, and cross-thread analysis from Unity's Profiler. They only work when profiling data is loaded (play mode with profiler recording active).
</objective>

<prerequisite>
**Before calling any profiler tool**, verify profiling data exists:

```json
Unity_ManageEditor: {"Action": "GetState"}
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

1. **Confirm data exists:** `Unity_ManageEditor` -> `GetState` (must show `IsPlaying: true` or paused with profiler data loaded)
2. **Start broad:** `GetOverallGcAllocations` or `GetFrameRangeTopTimeSummary` over a frame range
3. **Identify hot frames:** Look for spikes in the summary
4. **Drill into hot frame:** `GetFrameGcAllocations` or `GetFrameTopTimeSamples` on the specific frame
5. **Drill into sample:** `GetSampleTimeSummary` on the specific sample
6. **Cross-thread analysis:** `GetRelatedSamples` to find correlated work on job threads

</drill_down_workflow>

<runcommand_profiler_fallback>

**IMPORTANT: The MCP profiler tools may throw `TargetInvocationException` errors.** When they do, fall back to `Unity_RunCommand` using `ProfilerDriver` + `HierarchyFrameDataView` APIs directly. This approach is proven reliable and gives full control over the query.

**Required usings:**

```csharp
using UnityEditorInternal;
using UnityEditor.Profiling;
```

**Get frame range:**

```csharp
int firstFrame = ProfilerDriver.firstFrameIndex;
int lastFrame = ProfilerDriver.lastFrameIndex;
bool recording = ProfilerDriver.enabled;
```

**Read GC allocations per frame (overview):**

```csharp
// MergeSamplesWithTheSameName for summaries, ViewModes.Default for per-call detail
using (var fd = ProfilerDriver.GetHierarchyFrameDataView(frameIndex, 0,
    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
    HierarchyFrameDataView.columnGcMemory, false))
{
    if (!fd.valid) continue;
    int rootId = fd.GetRootItemID();
    var children = new List<int>();
    fd.GetItemChildren(rootId, children);
    foreach (int childId in children)
    {
        float gc = fd.GetItemColumnDataAsFloat(childId, HierarchyFrameDataView.columnGcMemory);
        string name = fd.GetItemName(childId);
    }
}
```

**Recursive GC tree dump (identify exact allocators):**

Use `ViewModes.Default` (not merged) and recurse via `GetItemChildren` to walk the full hierarchy. Each leaf `GC.Alloc` node shows the allocation size. Its parent is the code site that allocated.

**API pitfalls to avoid:**

- `result.Log("{0:N0}", value)` — format specifiers like `{0:N0}` do NOT work in `result.Log()`. Use string concatenation instead: `result.Log("Total: " + value + " B")`.
- `HierarchyFrameDataView.GetItemCallsCount()` — does NOT exist. Do not attempt to call it.
- `RawFrameDataView.GetSampleMetadataAsLong(sampleIndex, 4)` — metadata index 4 is NOT GC bytes. Throws `IndexOutOfRangeException`. Use `HierarchyFrameDataView` with `columnGcMemory` instead.
- **Output size:** Deep recursive dumps of high-allocation frames can exceed the MCP output limit. Cap recursion depth, or sample specific frames rather than dumping all.
- **Frame 0 is startup.** Skip it when analyzing steady-state idle GC. Start from frame 1 or later.

**Recommended workflow:**

1. Confirm profiler state: frame range + `ProfilerDriver.enabled`
2. Sample ~20 evenly spaced frames for a GC overview (identify which frames spike)
3. Full-scan all frames to count frequency and total GC per source category
4. Deep-dump (ViewModes.Default, depth 12+) on the worst frames to trace exact allocators
5. Cross-reference profiler sample names with codebase (e.g. `World.Tick() [Coroutine: MoveNext]` → `World.cs`)

</runcommand_profiler_fallback>
