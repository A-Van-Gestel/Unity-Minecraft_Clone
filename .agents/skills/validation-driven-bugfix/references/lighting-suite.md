# Reference Implementation: the Lighting Validation Suite

Everything lives under `Assets/Editor/Validation/Lighting/`. Menu item: **`Minecraft Clone/Dev/Validate Lighting Engine`**.

## File map

| File                                                  | Role                                                                                                                                                                 |
|-------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `LightingValidationSuite.cs`                          | Runner: `Scenario` struct (`Name`, `Func<bool> Run`, `KnownBugId`), partial-method registration, try/catch per scenario, categorized summary                         |
| `LightingValidationSuite.Baseline.cs`                 | Core baseline regression scenarios + the `AddBaselineScenarios` registration hub; dispatches to per-group baseline partials (see *Baseline file organization* below) |
| `LightingValidationSuite.OracleProbes.cs`             | Oracle-independent probe baselines (`B35`–`B39`, `B45`)                                                                                                              |
| `LightingValidationSuite.Bug05Canopy.cs`              | Dense-canopy generation fuzz baseline (`B42`) + its standalone menu item                                                                                             |
| `LightingValidationSuite.Bug09Fuzz.cs`                | Cross-chunk geometry fuzz baseline (`B40`)                                                                                                                           |
| `Baselines/LightingValidationSuite.Baseline.Bug12.cs` | Bug-12 family baselines (`B50`–`B53`), self-registered via `AddBug12BaselineScenarios` — the first of the `Baselines/` group split                                   |
| `LightingValidationSuite.KnownBugs.cs`                | `K`-scenarios reproducing open bugs from `LIGHTING_BUGS.md` (expected red); currently none open (Bug 12 promoted to `B53`)                                           |
| `Framework/LightingTestWorld.cs`                      | Harness core: N×N grid of chunk buffers, runs the real `NeighborhoodLightingJob`, applies cross-chunk mods via the shared `CrossChunkLightModApplier`                |
| `Framework/LightingTestWorld.Builder.cs`              | Authoring + queries (two write paths, see below)                                                                                                                     |
| `Framework/LightingOracle.cs`                         | Borderless global flood-fill — the spec                                                                                                                              |
| `Framework/LightingAssert.cs`                         | `MatchesOracle`, `FieldsEqual`, `Converged`, `NoBlocklightInVolume`, `IsTrue` — all with bounded diffs                                                               |
| `Framework/TestBlockPalette.cs`                       | Synthetic fixtures: Air(0), Stone, Glass, Leaves, DimGlass(op.5), LampWhite/Red/Green/Blue (opaque emissive 15), Torch (transparent emissive 14)                     |

Namespace: suite = `Editor.Validation.Lighting`, framework = `Editor.Validation.Lighting.Framework`.

## Baseline file organization (partials)

`LightingValidationSuite` is a single `partial class` spread across all the files above. The runner (`LightingValidationSuite.cs`) drives registration through two partial-method hooks — `AddBaselineScenarios` and `AddKnownBugScenarios` — each implemented in a partial file. As the baseline set grows, baseline **groups** are factored into their own self-contained partial files (under `Baselines/` for new groups) rather than letting `Baseline.cs` grow without bound (it had crossed 1000+ lines).

Each group file owns **its scenarios, its private constants/builders, AND its own registration**, wired with a per-group partial-method hook:

- `Baseline.cs` declares the hook and calls it from `AddBaselineScenarios`:
  ```csharp
  // in AddBaselineScenarios(scenarios):
  AddBug12BaselineScenarios(scenarios);
  // elsewhere in Baseline.cs (defining declaration, no body):
  static partial void AddBug12BaselineScenarios(List<Scenario> scenarios);
  ```
- The group file provides the implementing body plus the scenario methods + constants:
  ```csharp
  // Baselines/LightingValidationSuite.Baseline.Bug12.cs
  public static partial class LightingValidationSuite
  {
      static partial void AddBug12BaselineScenarios(List<Scenario> scenarios)
      {
          scenarios.Add(new Scenario("B53: …", Baseline_CrossSeamSunlightLoopClearsOnSourceRemoval));
          // … B50/B51/B52 …
      }
      private const int SEAM_LOOP_SLAB_MIN_Y = 58; // SCREAMING_CASE per CLAUDE.md
      private static bool Baseline_CrossSeamSunlightLoopClearsOnSourceRemoval() { … }
  }
  ```

Why this shape:

- A `partial void` with no implementing body is **elided** by the compiler, so the call site is safe even before/without the group file — groups are independently addable/removable.
- All files are the same class, so a group's `private` / `private const` members are visible suite-wide; co-locating them in the group file keeps the geometry constants next to the scenarios that use them.
- The runner and `KnownBugs.cs` are untouched when adding a baseline group — only `Baseline.cs` gains one declaration + one call.

**To add a baseline group:**

1. Create `Baselines/LightingValidationSuite.Baseline.<Topic>.cs` with `namespace Editor.Validation.Lighting` + `public static partial class LightingValidationSuite`.
2. Implement `static partial void Add<Topic>BaselineScenarios(List<Scenario> scenarios)` (the `scenarios.Add(...)` calls) + the scenario methods + any `private const` geometry (SCREAMING_CASE).
3. In `Baseline.cs`, add the defining declaration `static partial void Add<Topic>BaselineScenarios(List<Scenario> scenarios);` and call it from `AddBaselineScenarios`.
4. A brand-new `.cs` file is not in the `.csproj` until Unity re-imports — validate via Unity recompile, not `dotnet build` (see CLAUDE.md's new-file gotcha).

> The Bug-12 family (`B50` over-correction tripwire, `B51`/`B52` completeness, `B53` promoted repro) was the first group migrated this way (June 2026, `Baselines/LightingValidationSuite.Baseline.Bug12.cs`). The pre-existing topic partials (`OracleProbes`, `Bug05Canopy`, `Bug09Fuzz`) keep their scenario *bodies* in their own file but still register centrally in `AddBaselineScenarios`; they can be migrated to the hook pattern incrementally as the bulk of `Baseline.cs` is split.

## The shared production logic (Phase-0 extraction)

`Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` — pure static decision logic for applying a cross-chunk `LightModification` (stale-snapshot guards, wake-node old-value semantics). Called by BOTH `WorldJobManager.ProcessLightingJobs` (production) and `LightingTestWorld.CompleteLightingJob` (harness). **Fixes to mod-apply rules go here, never into the harness.**

`Assets/Scripts/Helpers/LightingJobProcessor.cs` — pure static routing decision for a job's emitted cross-chunk mods (drop / persist / defer / apply) plus the stability override. Also called by both production and harness. **Fixes to the defer-vs-apply ordering rule go here, never duplicated into the harness.**

> ⚠️ **Before authoring a repro, know the harness's blind spots.** A green suite does NOT prove an un-modelled area is correct. Closed since this note was written: the per-section merge runs real `ChunkData` code (A1), chunk unload / persist / replay is modelled (B1, baselines B30–B32), and pool recycle through the real `ChunkData.Reset()` with a reset-completeness guard is modelled (B4, baselines B33–B34). Still un-modelled: `neighborsDataReady == false` (B2) and true async races (B3, structural).
> Read [LIGHTING_VALIDATION_HARNESS_FIDELITY.md](../../../../Documentation/Architecture/Testing%20Framework/LIGHTING_VALIDATION_HARNESS_FIDELITY.md) before concluding a bug "can't be reproduced".

## Harness API cheat sheet

```csharp
using LightingTestWorld world = new LightingTestWorld(3);   // 3×3 grid; 5 for diagonal scenarios
// Generation-style authoring (raw write, NO light queueing — mirrors world gen / Bug 06 path):
world.SetBlock(pos, id); world.FillBox(min, max, id); world.FillSuperflatFloor(y, id);
world.RecalculateHeightmaps();                              // once, after SetBlock authoring
// Player-edit authoring (full ModifyVoxel mirror: old-value capture, heightmap, wake-ups, column recalc):
world.PlaceBlock(pos, id); world.BreakBlock(pos);
// Execution:
world.RunInitialLighting();              // all-column recalc + 2 edge-check rounds (sequential)
world.RunInitialLightingParallel();      // same but wave-parallel (stale snapshots — gen-time realism)
world.RunToConvergence(maxRounds);       // returns rounds, or -1 = non-convergence (flicker assert)
world.RunWaveToConvergence(maxRounds);   // Begin-all → Complete-all per round
// Race injection (Bug 08-style):
var flight = world.BeginLightingJob(coord);   // snapshot inputs, drain queues
/* ... mutate live state: edits, other jobs ... */
world.CompleteLightingJob(flight);            // run + stale merge + mod application
// Queries: GetBlockId, GetLightData, GetSkyLight, GetBlocklightRGB, SnapshotLightField
```

## Worked examples to copy from

- **Plain oracle scenario:** `B1` / `B5` (place → converge → `MatchesOracle`).
- **Ghost-light / returns-to-baseline:** `B4` (`SnapshotLightField` → place → break → `FieldsEqual`).
- **Race via flight API:** `B7` and `B13` (Begin → edit + neighbor job → Complete → assert).
- **Tripwire baseline:** `B7` — encodes behavior that only worked *because of* the seeding force-clear, planted before the Bug 07 fix to catch a naive fix (dropping the force-clear entirely); it stayed green through the fix (June 2026). Pattern: docstring states which fix it guards.
- **Isolated invariant (contaminated field):** `B9` — while Bug 07 was open it ran a `NoBlocklightInVolume` floor scan instead of a full oracle compare, with a dated docstring note; the full compare was restored once Bug 07 was fixed (June 2026). Pattern for any scenario whose full-field assertion is contaminated by a *different* open bug.
- **Won't-reproduce → baseline:** `B8` (authored as the Bug 05 repro, converges correctly; bug entry notes repro is still TODO).
- **Promoted scenarios:** `B9` (was `K09`, Bug 09), `B10`–`B12` (were `K07a`–`K07c`, Bug 07), `B13` (was `K08a`, Bug 08), and `B53` (was `K12a`, Bug 12; lives in the `Baselines/` group file, `_FIXED_BUGS.md` Lighting #16); see `_FIXED_BUGS.md` Lighting entries 10–12 for the full promotion records. Bug 08's archive entry is also the template for recording **partial confirmation scope** (path 2 in-game + suite; path 1 code-inspection only); Bug 12's is the template for **oracle-only confirmation** (never observed in-game — confirmation rests on the
  borderless oracle).

## Lighting-specific gotchas

- **`0xFFFF` light values are legitimate** (sky 15 + RGB 15,15,15 — white lamp on a sunlit surface). Never reintroduce `ushort.MaxValue` sentinel checks on light reads; bounds-check via `GetPackedData`'s `uint.MaxValue` instead. (Fixed engine-wide June 2026.)
- **Opaque sources propagate only their own emission** — never received surface light. Rule exists in BOTH `PropagateLightRGB` and the oracle's `SolveBlocklight`; keep them in sync.
- **Out-of-grid neighbors must be zero-length arrays, not `default`** — Unity's job scheduler rejects unconstructed containers; the job treats `Length == 0` as void space.
- **`LightQueueNode` is serialized in chunk save data** (`ChunkData.cs` ~384) — changing it is a save-format change requiring AOT migration. `LightModification` is job-output only and safe to extend (e.g. the `IsRemoval` flag).
- **Wake-node convention (post-Bug-07):** cross-chunk applies write the new value first and report `old = 0` for channels that didn't lose light; the job force-clears a channel only on the block-change signature `cur == old > 0`. Don't "simplify" either side without the other.
- Expected attenuation quick check: emission 15 lamp → adjacent air 14, then −1 per air step; `AttenuateLight = max(0, source − max(1, opacity))`; opaque neighbors receive `source − 1` surface light but never propagate.
