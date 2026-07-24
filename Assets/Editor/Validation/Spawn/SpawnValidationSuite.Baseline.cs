using System;
using System.Collections.Generic;
using Data;
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
            scenarios.Add(new Scenario("Spawn LoadedSave Initial Resumes A Far Save Exactly", RunLoadedInitialFarOut));
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
            ChunkRelativePosition initial = SpawnResolution.ResolveInitial(
                SpawnSource.Fresh, new ChunkRelativePosition(new Vector3(1f, 2f, 3f)),
                new ChunkRelativePosition(new Vector3(9f, 9f, 9f)), DEFAULT_SPAWN);

            Vector3 absolute = initial.ToAbsoluteWorldPosition();
            bool ok = Expect(Mathf.Approximately(absolute.x, DEFAULT_SPAWN) && Mathf.Approximately(absolute.z, DEFAULT_SPAWN),
                $"Fresh initial XZ = ({absolute.x}, {absolute.z}), expected ({DEFAULT_SPAWN}, {DEFAULT_SPAWN}).");
            ok &= Expect(!ChunkRelativePosition.IsHeightResolved(absolute.y),
                $"Fresh initial Y = {absolute.y}, expected the unresolved sentinel.");
            return ok;
        }

        /// <summary>An editor replay starts at the world's persisted spawn point, ignoring the saved player position.</summary>
        /// <returns>True when the initial position is the spawn point.</returns>
        private static bool RunReplayInitial()
        {
            Vector3 spawnAbsolute = new Vector3(1234.5f, 70f, -987.25f);
            ChunkRelativePosition spawnPoint = new ChunkRelativePosition(spawnAbsolute);

            ChunkRelativePosition initial = SpawnResolution.ResolveInitial(
                SpawnSource.EditorReplay, new ChunkRelativePosition(new Vector3(1f, 2f, 3f)), spawnPoint, DEFAULT_SPAWN);

            // Exact struct equality, not an absolute round-trip: the spawn point must arrive untouched.
            return Expect(initial == spawnPoint,
                $"EditorReplay initial = {initial}, expected the spawn point {spawnPoint}.");
        }

        /// <summary>A resumed save starts at the persisted player position, lifted by the anti-clip offset.</summary>
        /// <returns>True when the offset was applied on Y only.</returns>
        private static bool RunLoadedInitial()
        {
            Vector3 savedAbsolute = new Vector3(1234.5f, 70.25f, -987.5f);
            ChunkRelativePosition saved = new ChunkRelativePosition(savedAbsolute);

            ChunkRelativePosition initial = SpawnResolution.ResolveInitial(
                SpawnSource.LoadedSave, saved, new ChunkRelativePosition(new Vector3(9f, 9f, 9f)), DEFAULT_SPAWN);

            ChunkRelativePosition expected = new ChunkRelativePosition(
                savedAbsolute + new Vector3(0f, SpawnResolution.SavedPositionClipOffsetY, 0f));

            return Expect(initial == expected,
                $"LoadedSave initial = {initial}, expected {saved} + {SpawnResolution.SavedPositionClipOffsetY} on Y.");
        }

        /// <summary>
        /// WS-4c's reason for existing: a save from the far reaches of the world must resume at the <b>exact</b>
        /// position it was written at. The chunk-relative form carries it losslessly; the absolute
        /// <c>Vector3</c> this replaced could not represent it at all past ±2²⁴.
        /// </summary>
        /// <returns>True when the far position survives the resolve untouched.</returns>
        private static bool RunLoadedInitialFarOut()
        {
            // A position no absolute float could hold: 2^30 voxels out, with a sub-voxel offset that would be
            // rounded away entirely (the ULP at 2^30 is 64).
            ChunkRelativePosition saved = new ChunkRelativePosition(
                new ChunkCoord(1 << 26, -(1 << 26)), new Vector3(5.25f, 70.5f, 9.75f));

            ChunkRelativePosition initial = SpawnResolution.ResolveInitial(
                SpawnSource.LoadedSave, saved, default, DEFAULT_SPAWN);

            bool ok = Expect(initial.Chunk.Equals(saved.Chunk),
                $"Far LoadedSave landed in chunk {initial.Chunk.X},{initial.Chunk.Z}, expected {saved.Chunk.X},{saved.Chunk.Z}.");

            // The sub-voxel XZ offset must be BIT-exact: it is passed straight through, so any drift here is the
            // precision loss this format exists to prevent, not rounding.
            ok &= Expect(initial.localPosition.x == saved.localPosition.x &&
                         initial.localPosition.z == saved.localPosition.z,
                $"Far LoadedSave local XZ = ({initial.localPosition.x}, {initial.localPosition.z}), " +
                $"expected ({saved.localPosition.x}, {saved.localPosition.z}) exactly.");

            // Y is a computed sum, so it is compared approximately — exact equality on a float addition is not a
            // property of the code under test, it is a property of the JIT's intermediate precision.
            ok &= Expect(Mathf.Approximately(
                    initial.localPosition.y, saved.localPosition.y + SpawnResolution.SavedPositionClipOffsetY),
                $"Far LoadedSave Y = {initial.localPosition.y}, expected {saved.localPosition.y} + the anti-clip offset.");
            return ok;
        }

        /// <summary>A fresh world probes its own position and adopts the resolved surface as the canonical spawn.</summary>
        /// <returns>True when the probe aim, placement, and canonical spawn all match.</returns>
        private static bool RunFreshFinal()
        {
            ChunkRelativePosition initial = new ChunkRelativePosition(
                new Vector3(DEFAULT_SPAWN, ChunkRelativePosition.UNRESOLVED_HEIGHT, DEFAULT_SPAWN));
            Vector3 surface = new Vector3(800.5f, 65.1f, 800.5f);
            RecordingProbe probe = new RecordingProbe(surface);

            SpawnPlacement placement = SpawnResolution.ResolveFinal(
                SpawnSource.Fresh, initial, default, probe.Probe);

            bool ok = Expect(probe.Calls.Count == 1 && probe.Calls[0] == initial.ToAbsoluteWorldPosition(),
                $"Fresh probed {probe.Calls.Count} time(s) at [{string.Join(", ", probe.Calls)}], expected once at {initial}.");
            ChunkRelativePosition placedPlayer = placement.PlayerVoxelPosition;
            ChunkRelativePosition canonicalSpawn = placement.CanonicalSpawn;
            ok &= Expect(placedPlayer.ToAbsoluteWorldPosition() == surface,
                $"Fresh placed the player at {placement.PlayerVoxelPosition}, expected the probed surface {surface}.");
            ok &= Expect(placement.ShouldCanonicalizeSpawn, "Fresh must canonicalize its resolved surface as the spawn point.");
            ok &= Expect(canonicalSpawn.ToAbsoluteWorldPosition() == surface,
                $"Fresh canonical spawn = {canonicalSpawn.ToAbsoluteWorldPosition()}, expected {surface}.");
            return ok;
        }

        /// <summary>A replay probes its own position but must never rewrite the save's spawn point.</summary>
        /// <returns>True when the placement resolved and no canonicalization was requested.</returns>
        private static bool RunReplayFinal()
        {
            ChunkRelativePosition initial = new ChunkRelativePosition(
                new Vector3(320f, ChunkRelativePosition.UNRESOLVED_HEIGHT, 320f));
            Vector3 surface = new Vector3(320.5f, 71.1f, 320.5f);
            RecordingProbe probe = new RecordingProbe(surface);

            SpawnPlacement placement = SpawnResolution.ResolveFinal(
                SpawnSource.EditorReplay, initial, new ChunkRelativePosition(new Vector3(320f, 71f, 320f)), probe.Probe);

            bool ok = Expect(probe.Calls.Count == 1 && probe.Calls[0] == initial.ToAbsoluteWorldPosition(),
                $"EditorReplay probed {probe.Calls.Count} time(s) at [{string.Join(", ", probe.Calls)}], expected once at {initial}.");
            ChunkRelativePosition placedPlayer = placement.PlayerVoxelPosition;
            ok &= Expect(placedPlayer.ToAbsoluteWorldPosition() == surface,
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
            ChunkRelativePosition initial = new ChunkRelativePosition(new Vector3(1234.5f, 70.35f, -987.5f));
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
            ChunkRelativePosition canonicalSpawn = placement.CanonicalSpawn;
            ok &= Expect(canonicalSpawn.ToAbsoluteWorldPosition() == resolvedSpawn,
                $"LoadedSave canonical spawn = {canonicalSpawn.ToAbsoluteWorldPosition()}, expected {resolvedSpawn}.");
            return ok;
        }

        /// <summary>An already-resolved spawn point must not be rewritten — the probe echoes it and nothing changes.</summary>
        /// <returns>True when no canonicalization was requested.</returns>
        private static bool RunLoadedFinalResolved()
        {
            ChunkRelativePosition initial = new ChunkRelativePosition(new Vector3(10f, 70f, 10f));
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
                SpawnResolution.ResolveFinal(SpawnSource.Fresh, default, default, null);
                return Expect(false, "ResolveFinal accepted a null probe; expected ArgumentNullException.");
            }
            catch (ArgumentNullException)
            {
                return true;
            }
        }
    }
}
