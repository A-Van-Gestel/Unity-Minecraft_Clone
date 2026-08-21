using System.Collections.Generic;
using Editor.Validation.Framework;
using Serialization;
using Serialization.Migration;
using UnityEditor;
using UnityEngine;

namespace Editor.Validation.MigrationChain
{
    /// <summary>
    /// Validation suite for the AOT world-migration chain (<c>AOT_WORLD_MIGRATION_SYSTEM.md</c>; roadmap
    /// item NS-7): the <c>level.dat</c> step chain folded by <see cref="LevelDatCodec"/>, and
    /// <see cref="MigrationManager"/> driven end-to-end over a real on-disk world.
    /// <para>
    /// Two failure classes are guarded. <b>Field loss:</b> the steps that re-serialize the whole document
    /// from a frozen DTO (v12→v13's re-type, v14→v15's removal) drop anything their DTO omits, from every
    /// migrated save, silently — and <see cref="LevelDatCodec.ReadNormalized"/> fails <i>open</i> to a raw
    /// parse, so a broken chain reds nothing in production. <b>Silent no-op:</b> a chunk-format step that
    /// runs but does not transform must be caught by the manager's version-byte fail-fast guard, not written
    /// through as if migrated.
    /// </para>
    /// <para>
    /// Coverage is deliberately partial: this suite covers the <c>level.dat</c> chain (v1→v15) and the
    /// manager's orchestration, backup, corruption and rollback paths. The five chunk-payload rewrites
    /// (v2→v3, v5→v6, v7→v8, v8→v9, v9→v10), the two <c>pending_mods</c> steps and the v1→v2 region-layout
    /// restructure need a historical chunk-format fixture writer per era and are tracked as roadmap NS-7b.
    /// </para>
    /// <para>All scenarios are <b>baselines</b> (must stay green). <c>B25</c> was authored as the <c>K10</c>
    /// repro of <c>_FIXED_BUGS.md</c> Serialization 07 and promoted once the fix was confirmed in-game on a real v1
    /// world. No production code is under test through a new seam — the manager needed none.</para>
    /// <para>
    /// <b>Prove-red is recorded, not assumed.</b> These baselines were authored against shipped code, so four
    /// engine mutations were applied (in two pairs with disjoint predicted red-sets) to observe them fail:
    /// </para>
    /// <list type="bullet">
    /// <item>v12→v13's <c>ToChunkRelative</c> switched from floor-then-shift to a truncating divide →
    /// <b>B2, B6</b> (both assert the negative-coordinate re-type; B3's fixture is positive, so it correctly
    /// stayed green — truncation and flooring agree there).</item>
    /// <item>The manager's version-byte fail-fast guard neutered → <b>B8, B10, B11</b>. B9 stayed green,
    /// confirming it is the null/empty guard that catches an empty return, not this one.</item>
    /// <item><c>worldName</c> removed from the v15 frozen DTO → <b>B2, B3, B4</b> — the field-loss class.</item>
    /// <item>The post-chain <c>level.dat</c> version stamp in <c>MigrateGlobalFiles</c> deleted → only
    /// <b>B11</b>.</item>
    /// </list>
    /// <para>
    /// <b>NS-7b's chunk-payload scenarios (B14–B25) added five more mutations</b>, applied in two batches
    /// with disjoint predicted red-sets; every batch reddened exactly its prediction:
    /// </para>
    /// <list type="bullet">
    /// <item>v8→v9's <c>SUNLIGHT_SHIFT</c> 16→17 → <b>B17</b>.</item>
    /// <item>v2→v3's v1 heightmap widening zeroed → <b>B19</b>.</item>
    /// <item>the frozen v4 fluid-id snapshot emptied → <b>B22</b>.</item>
    /// <item>Facade re-routed from SCHEMA_NONE to KEEP_LEGACY → <b>B16, B20, B23</b> (all three arms that
    /// depend on it, chunk and pending-mods alike).</item>
    /// <item>v9→v10's <c>LEGACY_LIGHT_MASK</c> zeroed → <b>B18</b> only; B16 stayed green, confirming the two
    /// guard different things.</item>
    /// </list>
    /// <para>
    /// That last result is a <b>coverage finding worth keeping</b>: B6/B7/B12 do NOT detect a missing manager
    /// stamp, because every shipped step also sets the version itself inside <c>MigrateLevelDat</c> — which
    /// <c>AOT_WORLD_MIGRATION_SYSTEM.md</c> §6 forbids and every step does anyway. So the manager's stamp is
    /// redundant for any complete chain, and only B11 observes it, incidentally, via the synthetic step (whose
    /// pass-through <c>MigrateLevelDat</c> does not self-stamp). Do not "fix" the steps to satisfy §6 — that
    /// contradiction is a tracked open item, and rewriting a shipped step's output is forbidden.
    /// </para>
    /// </summary>
    public static partial class MigrationChainValidationSuite
    {
        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Migration Chain")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the migration-chain scenarios, returning the categorized result (the headless/CI
        /// entry point). Stays on <see cref="KnownBugChannel.Bug"/>: this suite's known-bug slot has hosted a
        /// documented serialization bug before (archived as <c>_FIXED_BUGS.md</c> Serialization 07, promoted to
        /// <c>B25</c>), so a future repro
        /// added here must route the reader to in-game confirmation and the archive-fixed-bug workflow, not to
        /// "promote an implemented feature to a baseline".
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1: chain non-vacuity — a v1 document reaches a shape only the full chain produces, and a current document passes through untouched", ChainActuallyRuns),
                new Scenario("B2: v12→current — the player-position re-type floors-then-shifts a negative position, and the fields no later step owns survive", V12PlayerPositionRetype),
                new Scenario("B3: v1→current chained — every injected historical default lands and every v1 field survives fourteen steps", V1ChainedToCurrent),
                new Scenario("B4: v3→current — the world-type and dimension steps OVERWRITE, pinning old worlds to their historical geometry", V3OverwritesTypeAndDimensions),
                new Scenario("B5: migration path structure — every source version resolves a gapless, correctly-ordered chain to current", MigrationPathIsGapless),
                new Scenario("B6: MigrationManager end-to-end — a real v12 world is stamped and content-migrated, its untouched chunk payloads survive, and the backup holds the original", EndToEndV12World),
                new Scenario("B7: a world with no generated chunks still completes and is still stamped", NoRegionFolderCompletes),
                new Scenario("B8: fail-fast — a step that silently no-ops is counted corrupted, never written through as migrated", SilentNoOpStepIsCaught),
                new Scenario("B9: fail-fast — a step returning empty data is caught before the next read", EmptyStepOutputIsCaught),
                new Scenario("B10: fail-fast — a step that forgets its version bump is caught", WrongVersionByteIsCaught),
                new Scenario("B11: answering the corruption prompt with rollback aborts, and the caller's rollback fully restores the world", AbortRestoresTheWorld),
                new Scenario("B12: rollback after a SUCCESSFUL migration restores the original level.dat and every chunk", RollbackAfterSuccessRestoresOriginal),
                new Scenario("B13: the dev corruption injector reaches the migration loop, and every chunk stays accounted for", DevCorruptionSeamIsWired),
                new Scenario("B14: the authored chunk-format v1/v2 fixture matches the layout the steps read, and a truncated one is rejected", ChunkFixtureIntegrity),
                new Scenario("B15: every chunk-format step writes its declared version byte, and the chain terminates at the current format", ChunkChainPerStepVersions),
                new Scenario("B16: the migrated payload is readable by the production deserializer with every seeded probe intact", ChunkChainOutputIsReadable),
                new Scenario("B17: legacy per-voxel light bits are lifted into the per-section LightData array", LegacyLightIsLiftedIntoLightData),
                new Scenario("B18: v9→v10 clears the now-reserved legacy light bits without disturbing the id", LegacyLightBitsAreStripped),
                new Scenario("B19: the v1 byte-per-column heightmap widens to ushorts with its values intact", V1HeightmapWidening),
                new Scenario("B20: ConvertLegacyMeta's shipped schema table is pinned arm by arm", ConvertLegacyMetaTableIsPinned),
                new Scenario("B21: the REAL manager applies the chunk-format chain to a v2 world and the chunk still loads", RealManagerAppliesChunkFormatChain),
                new Scenario("B22: pending_mods v4→v5 collapses orientation+fluid into one meta byte, keyed off the frozen v4 fluid ids", PendingModsV4ToV5),
                new Scenario("B23: pending_mods v5→v6 re-encodes meta per schema and agrees with the per-voxel converter", PendingModsV5ToV6),
                new Scenario("B24: the v1→v2 repack recovers each chunk index from its broken address and rewrites it at the correct one", RegionRepackMovesChunksToCorrectAddresses),
                new Scenario("B25: a migrated v1 world's chunks are readable rather than regenerated from seed", V1WorldChunksSurviveMigration),
            };
            return ValidationSuiteRunner.Execute("Migration Chain", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        // --- Expected values (frozen, mirroring what each step injects) -----------------------------

        /// <summary>The wind every pre-v14 world was rendered with — what the v13→v14 step injects.</summary>
        private const float HISTORICAL_WIND_X = -0.6f;

        /// <summary>Day tick the v14→v15 step seeds; a stored light level carries no recoverable time.</summary>
        private const long MIGRATED_NOON_TICKS = 6000L;

        /// <summary>Legacy world center (800) / chunk width (16) — the spawn chunk the v10→v11 step injects.</summary>
        private const int LEGACY_SPAWN_CHUNK = 50;

        /// <summary>The v11-era unresolved-height sentinel the injected spawn carries until load resolves it.</summary>
        private const float UNRESOLVED_SPAWN_HEIGHT = -1_000_000f;

        /// <summary>Chunk height the v6→v7 step pins onto every older world.</summary>
        private const int LEGACY_CHUNK_HEIGHT = 128;

        /// <summary>Chunk width the v6→v7 step pins onto every older world.</summary>
        private const int LEGACY_CHUNK_WIDTH = 16;

        /// <summary>World size in chunks the v6→v7 step pins onto every older world.</summary>
        private const int LEGACY_WORLD_SIZE = 100;

        // --- Fixtures ------------------------------------------------------------------------------

        /// <summary>
        /// A minimal v1 <c>level.dat</c>: only the fields that existed before v4 added the world type,
        /// v7 the dimensions, v11 the spawn point and v12 the border. Every present field carries a
        /// distinct non-default value, so a step that drops one is caught rather than passing on a default.
        /// </summary>
        private const string V1_LEVEL_DAT = @"{
  ""version"": 1, ""worldName"": ""V1Probe"", ""seed"": 987,
  ""creationDate"": 1111, ""lastPlayed"": 2222,
  ""worldState"": { ""timeOfDay"": 0.5 },
  ""player"": {
    ""position"": { ""x"": 33.5, ""y"": 71.0, ""z"": 8.25 },
    ""rotation"": { ""x"": 0.0, ""y"": 45.0, ""z"": 0.0 },
    ""capabilities"": { ""isFlying"": true, ""isNoclipping"": true },
    ""inventory"": [ { ""slotIndex"": 2, ""itemID"": 9, ""amount"": 13 } ],
    ""cursorItem"": { ""itemID"": 4, ""amount"": 2, ""originSlotIndex"": 5 }
  }
}";

        /// <summary>
        /// A v3 <c>level.dat</c> carrying a NON-legacy world type and deliberately wrong dimensions, so the
        /// v3→v4 and v6→v7 steps' overwrite semantics are distinguishable from "the field happened to
        /// already hold the expected value".
        /// </summary>
        private const string V3_LEVEL_DAT = @"{
  ""version"": 3, ""worldName"": ""V3Probe"", ""seed"": 555,
  ""chunkHeight"": 999, ""chunkWidth"": 99, ""worldSizeInChunks"": 9,
  ""worldType"": 2,
  ""creationDate"": 3333, ""lastPlayed"": 4444,
  ""worldState"": { ""timeOfDay"": 0.1 },
  ""player"": {
    ""position"": { ""x"": 5.0, ""y"": 64.0, ""z"": 5.0 },
    ""rotation"": { ""x"": 0.0, ""y"": 0.0, ""z"": 0.0 },
    ""capabilities"": { ""isFlying"": false, ""isNoclipping"": false },
    ""inventory"": [],
    ""cursorItem"": null
  }
}";

        /// <summary>
        /// A fully-populated v12 <c>level.dat</c> — the last era with an absolute <c>Vector3</c> player
        /// position. The position is deliberately NEGATIVE on both horizontal axes: the v12→v13 re-type
        /// floors-then-shifts, and a truncating divide would land it in the wrong chunk (the WS-1 rule).
        /// </summary>
        private const string V12_LEVEL_DAT = @"{
  ""version"": 12, ""worldName"": ""V12Probe"", ""seed"": 246,
  ""chunkHeight"": 128, ""chunkWidth"": 16, ""worldSizeInChunks"": 100, ""worldType"": 1,
  ""spawnPosition"": { ""_chunkX"": 4, ""_chunkZ"": -6, ""localPosition"": { ""x"": 2.5, ""y"": 65.0, ""z"": 3.5 } },
  ""borderRadius"": 768, ""creationDate"": 5555, ""lastPlayed"": 6666,
  ""worldState"": { ""timeOfDay"": 0.9 },
  ""player"": {
    ""position"": { ""x"": -17.25, ""y"": 70.0, ""z"": -1.5 },
    ""rotation"": { ""x"": 10.0, ""y"": 20.0, ""z"": 30.0 },
    ""capabilities"": { ""isFlying"": true, ""isNoclipping"": false },
    ""inventory"": [ { ""slotIndex"": 1, ""itemID"": 6, ""amount"": 17 } ],
    ""cursorItem"": null
  }
}";

        /// <summary>Expected chunk X after re-typing -17.25: floor(-17.25) = -18, -18 >> 4 = -2.</summary>
        private const int V12_EXPECTED_CHUNK_X = -2;

        /// <summary>Expected chunk Z after re-typing -1.5: floor(-1.5) = -2, -2 >> 4 = -1.</summary>
        private const int V12_EXPECTED_CHUNK_Z = -1;

        /// <summary>Expected local X after re-typing -17.25: -17.25 - (-2 * 16) = 14.75.</summary>
        private const float V12_EXPECTED_LOCAL_X = 14.75f;

        /// <summary>Expected local Z after re-typing -1.5: -1.5 - (-1 * 16) = 14.5.</summary>
        private const float V12_EXPECTED_LOCAL_Z = 14.5f;

        // --- Helpers -------------------------------------------------------------------------------

        /// <summary>Logs a single assertion as PASS/FAIL and returns its result for AND-chaining.</summary>
        private static bool Check(string label, bool condition)
        {
            if (condition) Debug.Log($"  [PASS] {label}");
            else Debug.LogError($"  [FAIL] {label}");
            return condition;
        }

        // --- Scenarios: the level.dat chain --------------------------------------------------------

        /// <summary>B1. Red when: the step chain does not run at all (an unregistered step, or the codec's
        /// fail-open catch swallowing a broken chain and returning a raw parse). Two-sided, so neither half
        /// can pass vacuously: the old document must GAIN what only the chain can add, and a current-version
        /// document must pass through with its own values, not the migration's injected defaults.</summary>
        private static bool ChainActuallyRuns()
        {
            WorldSaveData migrated = LevelDatCodec.ReadNormalized(V1_LEVEL_DAT);

            // Each of these sections is introduced by a different step; a raw parse of the v1 text produces
            // the live type's defaults, which differ from every one of them.
            bool ok = Check("a v1 document gains the v11 spawn point",
                migrated.spawnPosition.Chunk.X == LEGACY_SPAWN_CHUNK && migrated.spawnPosition.Chunk.Z == LEGACY_SPAWN_CHUNK);
            ok &= Check($"a v1 document gains the v14 wind, got {migrated.worldState?.environment?.windX}",
                migrated.worldState?.environment != null &&
                Mathf.Approximately(migrated.worldState.environment.windX, HISTORICAL_WIND_X));
            ok &= Check($"a v1 document gains the v15 clock at noon, got tick {migrated.worldState?.time?.ticks}",
                migrated.worldState?.time != null && migrated.worldState.time.ticks == MIGRATED_NOON_TICKS);
            ok &= Check("a v1 document gains the v7 legacy dimensions",
                migrated.chunkHeight == LEGACY_CHUNK_HEIGHT && migrated.worldSizeInChunks == LEGACY_WORLD_SIZE);

            // The other side: a current-version document must NOT be run through the chain.
            WorldSaveData current = LevelDatCodec.ReadNormalized(CurrentVersionLevelDat());
            ok &= Check($"a current document keeps its own clock, got tick {current.worldState?.time?.ticks}",
                current.worldState?.time != null && current.worldState.time.ticks == 12345L);
            ok &= Check($"a current document keeps its own wind, got {current.worldState?.environment?.windX}",
                current.worldState?.environment != null &&
                Mathf.Approximately(current.worldState.environment.windX, 3.5f));
            ok &= Check("a current document keeps its own border radius", current.borderRadius == 4096);
            ok &= Check($"a current document reports the current version, got v{current.version}",
                current.version == SaveSystem.CURRENT_VERSION);
            return ok;
        }

        /// <summary>
        /// Serializes a current-version document with values no migration step would ever inject, so B1's
        /// pass-through half cannot be satisfied by the chain running anyway.
        /// </summary>
        /// <returns>The current-shape <c>level.dat</c> JSON.</returns>
        private static string CurrentVersionLevelDat()
        {
            WorldSaveData data = new WorldSaveData
            {
                version = SaveSystem.CURRENT_VERSION,
                worldName = "CurrentProbe",
                seed = 777,
                borderRadius = 4096,
                worldState = new WorldStateData
                {
                    environment = new EnvironmentData { windX = 3.5f, windZ = -4.5f },
                    time = new WorldTimeData { ticks = 12345L, frozen = true },
                },
            };
            return JsonUtility.ToJson(data, true);
        }

        /// <summary>B5. Red when: a step is unregistered or registered out of order, so
        /// <c>BuildMigrationPath</c> either throws or returns a chain that skips a version. Guards the
        /// registration discipline the manager enforces only at runtime, for every source version at once.</summary>
        private static bool MigrationPathIsGapless()
        {
            MigrationManager manager = new MigrationManager();
            bool ok = true;

            for (int source = 1; source < SaveSystem.CURRENT_VERSION; source++)
            {
                List<WorldMigrationStep> path = manager.GetRequiredMigrations(source);
                bool contiguous = path.Count == SaveSystem.CURRENT_VERSION - source;
                int expected = source;

                for (int i = 0; i < path.Count && contiguous; i++)
                {
                    contiguous = path[i].SourceWorldVersion == expected && path[i].TargetWorldVersion == expected + 1 &&
                                 !string.IsNullOrEmpty(path[i].Description) &&
                                 !string.IsNullOrEmpty(path[i].ChangeSummary);
                    expected = path[i].TargetWorldVersion;
                }

                ok &= Check($"v{source.ToString()} resolves a gapless {(SaveSystem.CURRENT_VERSION - source).ToString()}-step chain to v{SaveSystem.CURRENT_VERSION.ToString()} (got {path.Count.ToString()})",
                    contiguous);
            }

            // The current version needs nothing, and a future version is the manager's throw, not a path.
            ok &= Check("the current version resolves an empty chain",
                manager.GetRequiredMigrations(SaveSystem.CURRENT_VERSION).Count == 0);
            return ok;
        }
    }
}
