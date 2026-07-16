using System;
using System.Collections.Generic;
using Data.WorldTypes;
using Editor.Validation.Framework;
using UnityEngine;
using SpawnPlacement = Spawn.SpawnPlacement;
using SpawnResolution = Spawn.SpawnResolution;
using SpawnSource = Spawn.SpawnSource;

namespace Editor.Validation
{
    /// <summary>
    /// <see cref="SpawnValidationSuite"/> — startup spawn-policy baselines (SP-1).
    /// <para>
    /// These pin the behavior <c>World.StartWorld</c> had before the spawn decision was consolidated, which is
    /// SP-1's whole contract: the refactor is meant to change nothing. Two things here have teeth beyond the
    /// returned values — <b>which position the terrain probe is aimed at</b> (the three sources disagree, and a
    /// resumed save aims it at the spawn point rather than the player), and <b>whether the canonical spawn point is
    /// rewritten</b> (that value lands in level.dat, so a wrong answer silently relocates a player's spawn).
    /// </para>
    /// </summary>
    public static partial class SpawnValidationSuite
    {
        /// <summary>The fresh-world default spawn coordinate, mirroring <c>VoxelData.DefaultSpawnPosition</c>.</summary>
        private const float DEFAULT_SPAWN = 800f;

        /// <summary>
        /// A stand-in terrain probe that records what it was aimed at, so a scenario can assert the aim and not just
        /// the result. Mirrors <c>World.ResolveSpawnHeight</c>'s contract: an already-resolved position is returned
        /// unchanged.
        /// </summary>
        private sealed class RecordingProbe
        {
            private readonly Vector3? _resolvedResult;

            /// <summary>Every position the probe was called with, in call order.</summary>
            public readonly List<Vector3> Calls = new List<Vector3>();

            /// <summary>Creates a probe.</summary>
            /// <param name="resolvedResult">The position to return, or null to echo the input (the "already resolved" case).</param>
            public RecordingProbe(Vector3? resolvedResult) => _resolvedResult = resolvedResult;

            /// <summary>The probe delegate target.</summary>
            /// <param name="position">The position to resolve.</param>
            /// <returns>The configured result, or <paramref name="position"/> unchanged.</returns>
            public Vector3 Probe(Vector3 position)
            {
                Calls.Add(position);
                return _resolvedResult ?? position;
            }
        }

        static partial void AddBaselineScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Spawn Classify Pins All Flag Combinations", RunClassifyMatrix));
            scenarios.Add(new Scenario("Spawn Fresh Initial Is Default XZ At Unresolved Height", RunFreshInitial));
            scenarios.Add(new Scenario("Spawn EditorReplay Initial Is The Persisted Spawn Point", RunReplayInitial));
            scenarios.Add(new Scenario("Spawn LoadedSave Initial Applies The Clip Offset", RunLoadedInitial));
            scenarios.Add(new Scenario("Spawn Fresh Final Probes Itself And Canonicalizes", RunFreshFinal));
            scenarios.Add(new Scenario("Spawn EditorReplay Final Probes Itself And Never Canonicalizes", RunReplayFinal));
            scenarios.Add(new Scenario("Spawn LoadedSave Final Holds The Player And Probes The Spawn Point", RunLoadedFinal));
            scenarios.Add(new Scenario("Spawn LoadedSave Final Skips Canonicalization When Already Resolved", RunLoadedFinalResolved));
            scenarios.Add(new Scenario("Spawn ResolveFinal Rejects A Null Probe", RunNullProbeRejected));
        }

        /// <summary>
        /// Pins the source classification across every flag combination — including the deliberately-preserved hole
        /// where a menu-opened world with no readable level.dat still classifies as <c>LoadedSave</c>.
        /// </summary>
        /// <returns>True when every combination classified as documented.</returns>
        private static bool RunClassifyMatrix()
        {
            // (isNewGame, enablePersistence, hasExistingMetadata) -> expected source.
            (bool NewGame, bool Persist, bool Meta, SpawnSource Expected)[] cases =
            {
                (false, true, true, SpawnSource.LoadedSave),
                // The preserved hole: no metadata, still LoadedSave (player lands on the prefab position, unprobed).
                (false, true, false, SpawnSource.LoadedSave),
                (false, false, true, SpawnSource.Fresh),
                (false, false, false, SpawnSource.Fresh),
                (true, true, true, SpawnSource.EditorReplay),
                (true, true, false, SpawnSource.Fresh),
                (true, false, true, SpawnSource.Fresh),
                (true, false, false, SpawnSource.Fresh),
            };

            bool ok = true;
            foreach ((bool newGame, bool persist, bool meta, SpawnSource expected) in cases)
            {
                SpawnSource actual = SpawnResolution.Classify(newGame, persist, meta);
                ok &= Expect(actual == expected,
                    $"Classify(isNewGame:{newGame}, persistence:{persist}, metadata:{meta}) = {actual}, expected {expected}.");
            }

            return ok;
        }

        /// <summary>Fresh worlds start at the default XZ with the unresolved-height sentinel.</summary>
        /// <returns>True when the initial position matches.</returns>
        private static bool RunFreshInitial()
        {
            Vector3 initial = SpawnResolution.ResolveInitial(
                SpawnSource.Fresh, new Vector3(1f, 2f, 3f), new ChunkRelativePosition(new Vector3(9f, 9f, 9f)), DEFAULT_SPAWN);

            bool ok = Expect(Mathf.Approximately(initial.x, DEFAULT_SPAWN) && Mathf.Approximately(initial.z, DEFAULT_SPAWN),
                $"Fresh initial XZ = ({initial.x}, {initial.z}), expected ({DEFAULT_SPAWN}, {DEFAULT_SPAWN}).");
            ok &= Expect(!ChunkRelativePosition.IsHeightResolved(initial.y),
                $"Fresh initial Y = {initial.y}, expected the unresolved sentinel.");
            return ok;
        }

        /// <summary>An editor replay starts at the world's persisted spawn point, ignoring the saved player position.</summary>
        /// <returns>True when the initial position is the spawn point.</returns>
        private static bool RunReplayInitial()
        {
            Vector3 spawnAbsolute = new Vector3(1234.5f, 70f, -987.25f);
            ChunkRelativePosition spawnPoint = new ChunkRelativePosition(spawnAbsolute);

            Vector3 initial = SpawnResolution.ResolveInitial(
                SpawnSource.EditorReplay, new Vector3(1f, 2f, 3f), spawnPoint, DEFAULT_SPAWN);

            return Expect(initial == spawnPoint.ToAbsoluteWorldPosition(),
                $"EditorReplay initial = {initial}, expected the spawn point {spawnPoint.ToAbsoluteWorldPosition()}.");
        }

        /// <summary>A resumed save starts at the persisted player position, lifted by the anti-clip offset.</summary>
        /// <returns>True when the offset was applied on Y only.</returns>
        private static bool RunLoadedInitial()
        {
            Vector3 saved = new Vector3(1234.5f, 70.25f, -987.5f);

            Vector3 initial = SpawnResolution.ResolveInitial(
                SpawnSource.LoadedSave, saved, new ChunkRelativePosition(new Vector3(9f, 9f, 9f)), DEFAULT_SPAWN);

            return Expect(initial == saved + new Vector3(0f, SpawnResolution.SavedPositionClipOffsetY, 0f),
                $"LoadedSave initial = {initial}, expected {saved} + {SpawnResolution.SavedPositionClipOffsetY} on Y.");
        }

        /// <summary>A fresh world probes its own position and adopts the resolved surface as the canonical spawn.</summary>
        /// <returns>True when the probe aim, placement, and canonical spawn all match.</returns>
        private static bool RunFreshFinal()
        {
            Vector3 initial = new Vector3(DEFAULT_SPAWN, ChunkRelativePosition.UNRESOLVED_HEIGHT, DEFAULT_SPAWN);
            Vector3 surface = new Vector3(800.5f, 65.1f, 800.5f);
            RecordingProbe probe = new RecordingProbe(surface);

            SpawnPlacement placement = SpawnResolution.ResolveFinal(
                SpawnSource.Fresh, initial, default, probe.Probe);

            bool ok = Expect(probe.Calls.Count == 1 && probe.Calls[0] == initial,
                $"Fresh probed {probe.Calls.Count} time(s) at [{string.Join(", ", probe.Calls)}], expected once at {initial}.");
            ok &= Expect(placement.PlayerVoxelPosition == surface,
                $"Fresh placed the player at {placement.PlayerVoxelPosition}, expected the probed surface {surface}.");
            ok &= Expect(placement.ShouldCanonicalizeSpawn, "Fresh must canonicalize its resolved surface as the spawn point.");
            ok &= Expect(placement.CanonicalSpawn.ToAbsoluteWorldPosition() == surface,
                $"Fresh canonical spawn = {placement.CanonicalSpawn.ToAbsoluteWorldPosition()}, expected {surface}.");
            return ok;
        }

        /// <summary>A replay probes its own position but must never rewrite the save's spawn point.</summary>
        /// <returns>True when the placement resolved and no canonicalization was requested.</returns>
        private static bool RunReplayFinal()
        {
            Vector3 initial = new Vector3(320f, ChunkRelativePosition.UNRESOLVED_HEIGHT, 320f);
            Vector3 surface = new Vector3(320.5f, 71.1f, 320.5f);
            RecordingProbe probe = new RecordingProbe(surface);

            SpawnPlacement placement = SpawnResolution.ResolveFinal(
                SpawnSource.EditorReplay, initial, new ChunkRelativePosition(new Vector3(320f, 71f, 320f)), probe.Probe);

            bool ok = Expect(probe.Calls.Count == 1 && probe.Calls[0] == initial,
                $"EditorReplay probed {probe.Calls.Count} time(s) at [{string.Join(", ", probe.Calls)}], expected once at {initial}.");
            ok &= Expect(placement.PlayerVoxelPosition == surface,
                $"EditorReplay placed the player at {placement.PlayerVoxelPosition}, expected {surface}.");
            ok &= Expect(!placement.ShouldCanonicalizeSpawn,
                "EditorReplay must not rewrite the persisted spawn point.");
            return ok;
        }

        /// <summary>
        /// A resumed save holds the player exactly where they logged out and aims the probe at the <i>spawn point</i>
        /// instead — the case a single "resolve the player's height" model would get wrong.
        /// </summary>
        /// <returns>True when the player is untouched and the spawn point was lazily canonicalized.</returns>
        private static bool RunLoadedFinal()
        {
            Vector3 initial = new Vector3(1234.5f, 70.35f, -987.5f);
            ChunkRelativePosition spawnPoint =
                new ChunkRelativePosition(new Vector3(48f, ChunkRelativePosition.UNRESOLVED_HEIGHT, 64f));
            Vector3 resolvedSpawn = new Vector3(48.5f, 66.1f, 64.5f);
            RecordingProbe probe = new RecordingProbe(resolvedSpawn);

            SpawnPlacement placement = SpawnResolution.ResolveFinal(
                SpawnSource.LoadedSave, initial, spawnPoint, probe.Probe);

            bool ok = Expect(placement.PlayerVoxelPosition == initial,
                $"LoadedSave moved the player to {placement.PlayerVoxelPosition}, expected the resumed position {initial}.");
            ok &= Expect(probe.Calls.Count == 1 && probe.Calls[0] == spawnPoint.ToAbsoluteWorldPosition(),
                $"LoadedSave probed [{string.Join(", ", probe.Calls)}], expected exactly the spawn point " +
                $"{spawnPoint.ToAbsoluteWorldPosition()} — never the player position.");
            ok &= Expect(placement.ShouldCanonicalizeSpawn,
                "LoadedSave must canonicalize a spawn point the probe resolved.");
            ok &= Expect(placement.CanonicalSpawn.ToAbsoluteWorldPosition() == resolvedSpawn,
                $"LoadedSave canonical spawn = {placement.CanonicalSpawn.ToAbsoluteWorldPosition()}, expected {resolvedSpawn}.");
            return ok;
        }

        /// <summary>An already-resolved spawn point must not be rewritten — the probe echoes it and nothing changes.</summary>
        /// <returns>True when no canonicalization was requested.</returns>
        private static bool RunLoadedFinalResolved()
        {
            Vector3 initial = new Vector3(10f, 70f, 10f);
            ChunkRelativePosition spawnPoint = new ChunkRelativePosition(new Vector3(48.5f, 66.1f, 64.5f));
            RecordingProbe probe = new RecordingProbe(null);

            SpawnPlacement placement = SpawnResolution.ResolveFinal(
                SpawnSource.LoadedSave, initial, spawnPoint, probe.Probe);

            bool ok = Expect(placement.PlayerVoxelPosition == initial,
                $"LoadedSave moved the player to {placement.PlayerVoxelPosition}, expected {initial}.");
            ok &= Expect(!placement.ShouldCanonicalizeSpawn,
                "An already-resolved spawn point must not be rewritten to level.dat.");
            return ok;
        }

        /// <summary>A null probe is a programming error, not a silent no-resolve.</summary>
        /// <returns>True when the call threw.</returns>
        private static bool RunNullProbeRejected()
        {
            try
            {
                SpawnResolution.ResolveFinal(SpawnSource.Fresh, Vector3.zero, default, null);
                return Expect(false, "ResolveFinal accepted a null probe; expected ArgumentNullException.");
            }
            catch (ArgumentNullException)
            {
                return true;
            }
        }
    }
}
