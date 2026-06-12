# Reference Implementation: the Lighting Validation Suite

Everything lives under `Assets/Editor/Validation/Lighting/`. Menu item: **`Minecraft Clone/Dev/Validate Lighting Engine`**.

## File map

| File                                     | Role                                                                                                                                                  |
|------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------|
| `LightingValidationSuite.cs`             | Runner: `Scenario` struct (`Name`, `Func<bool> Run`, `KnownBugId`), partial-method registration, try/catch per scenario, categorized summary          |
| `LightingValidationSuite.Baseline.cs`    | `B1`–`B12` regression scenarios (must stay green)                                                                                                     |
| `LightingValidationSuite.KnownBugs.cs`   | `K`-scenarios reproducing open bugs from `LIGHTING_BUGS.md` (expected red)                                                                            |
| `Framework/LightingTestWorld.cs`         | Harness core: N×N grid of chunk buffers, runs the real `NeighborhoodLightingJob`, applies cross-chunk mods via the shared `CrossChunkLightModApplier` |
| `Framework/LightingTestWorld.Builder.cs` | Authoring + queries (two write paths, see below)                                                                                                      |
| `Framework/LightingOracle.cs`            | Borderless global flood-fill — the spec                                                                                                               |
| `Framework/LightingAssert.cs`            | `MatchesOracle`, `FieldsEqual`, `Converged`, `NoBlocklightInVolume`, `IsTrue` — all with bounded diffs                                                |
| `Framework/TestBlockPalette.cs`          | Synthetic fixtures: Air(0), Stone, Glass, Leaves, DimGlass(op.5), LampWhite/Red/Green/Blue (opaque emissive 15), Torch (transparent emissive 14)      |

Namespace: suite = `Editor.Validation.Lighting`, framework = `Editor.Validation.Lighting.Framework`.

## The shared production logic (Phase-0 extraction)

`Assets/Scripts/Helpers/CrossChunkLightModApplier.cs` — pure static decision logic for applying a cross-chunk `LightModification` (stale-snapshot guards, wake-node old-value semantics). Called by BOTH `WorldJobManager.ProcessLightingJobs` (production) and `LightingTestWorld.CompleteLightingJob` (harness). **Fixes to mod-apply rules go here, never into the harness.**

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
- **Race via flight API:** `B7` and `K08a` (Begin → edit + neighbor job → Complete → assert).
- **Tripwire baseline:** `B7` — encodes behavior that only worked *because of* the seeding force-clear, planted before the Bug 07 fix to catch a naive fix (dropping the force-clear entirely); it stayed green through the fix (June 2026). Pattern: docstring states which fix it guards.
- **Isolated invariant (contaminated field):** `B9` — while Bug 07 was open it ran a `NoBlocklightInVolume` floor scan instead of a full oracle compare, with a dated docstring note; the full compare was restored once Bug 07 was fixed (June 2026). Pattern for any scenario whose full-field assertion is contaminated by a *different* open bug.
- **Won't-reproduce → baseline:** `B8` (authored as the Bug 05 repro, converges correctly; bug entry notes repro is still TODO).
- **Promoted scenarios:** `B9` (was `K09`, Bug 09) and `B10`–`B12` (were `K07a`–`K07c`, Bug 07); see `_FIXED_BUGS.md` Lighting entries 10–11 for the full promotion records.

## Lighting-specific gotchas

- **`0xFFFF` light values are legitimate** (sky 15 + RGB 15,15,15 — white lamp on a sunlit surface). Never reintroduce `ushort.MaxValue` sentinel checks on light reads; bounds-check via `GetPackedData`'s `uint.MaxValue` instead. (Fixed engine-wide June 2026.)
- **Opaque sources propagate only their own emission** — never received surface light. Rule exists in BOTH `PropagateLightRGB` and the oracle's `SolveBlocklight`; keep them in sync.
- **Out-of-grid neighbors must be zero-length arrays, not `default`** — Unity's job scheduler rejects unconstructed containers; the job treats `Length == 0` as void space.
- **`LightQueueNode` is serialized in chunk save data** (`ChunkData.cs` ~384) — changing it is a save-format change requiring AOT migration. `LightModification` is job-output only and safe to extend (e.g. the `IsRemoval` flag).
- **Wake-node convention (post-Bug-07):** cross-chunk applies write the new value first and report `old = 0` for channels that didn't lose light; the job force-clears a channel only on the block-change signature `cur == old > 0`. Don't "simplify" either side without the other.
- Expected attenuation quick check: emission 15 lamp → adjacent air 14, then −1 per air step; `AttenuateLight = max(0, source − max(1, opacity))`; opaque neighbors receive `source − 1` surface light but never propagate.
