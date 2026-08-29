using System;
using System.Collections.Generic;
using Audio;
using Data;
using Data.Enums;
using Editor.Validation.Framework;
using Physics;
using UnityEngine;

namespace Editor.Validation.SoundEngine
{
    /// <summary>
    /// <see cref="SoundEngineValidationSuite"/> — the pure resolution chain: block ID to material, material to
    /// clip, and the per-event pitch envelope.
    /// <para>
    /// These are the decisions a break, place or step event makes before a single sample is touched, and they
    /// are exactly the ones that fail silently in a running game: a wrong material sounds plausible, a
    /// clip index off the end throws inside a click handler, and an inverted pitch envelope is only ever
    /// heard, never reported.
    /// </para>
    /// </summary>
    public static partial class SoundEngineValidationSuite
    {
        /// <summary>How many distinct events each statistical sweep drives through the pickers.</summary>
        private const int RESOLUTION_SWEEP_EVENTS = 4096;

        static partial void AddResolutionScenarios(List<Scenario> scenarios)
        {
            scenarios.Add(new Scenario("Block ID Resolves To Its Authored Sound Material", RunResolveMaterial));
            scenarios.Add(new Scenario("Unauthored Place Clips Fall Back To Break Clips", RunPlaceFallback));
            scenarios.Add(new Scenario("Clip Pick Is Deterministic And Always In Range", RunClipPick));
            scenarios.Add(new Scenario("Pitch Stays Inside The Group's Envelope", RunPitchEnvelope));
            scenarios.Add(new Scenario("Event Hash Separates Materials And Events", RunEventHash));
            scenarios.Add(new Scenario("Step Samples The Occupied Cell And The Supporting Cell", RunStepCells));
            scenarios.Add(new Scenario("A Sub-Voxel Block Under The Feet Is The Support, Not The Cell Below",
                RunStepSubVoxelSupport));
            scenarios.Add(new Scenario("A Non-Solid Occupant Layers Over The Supporting Block", RunStepOccupantLayering));
        }

        /// <summary>
        /// The lookup every trigger site depends on, including the three ways it can be handed nothing: a null
        /// database, an ID past the end of it, and a hole in the array.
        /// </summary>
        private static bool RunResolveMaterial()
        {
            const string scenario = "Block ID Resolves To Its Authored Sound Material";

            BlockType[] blocks =
            {
                new BlockType { blockName = "Air", soundMaterial = SoundMaterial.None },
                new BlockType { blockName = "Stone", soundMaterial = SoundMaterial.Stone },
                null,
                new BlockType { blockName = "Wood", soundMaterial = SoundMaterial.Wood },
            };

            if (SoundResolution.ResolveMaterial(blocks, 1) != SoundMaterial.Stone)
                return FailSound(scenario, "block 1 did not resolve to Stone.");
            if (SoundResolution.ResolveMaterial(blocks, 3) != SoundMaterial.Wood)
                return FailSound(scenario, "block 3 did not resolve to Wood.");
            if (SoundResolution.ResolveMaterial(blocks, 0) != SoundMaterial.None)
                return FailSound(scenario, "Air did not resolve to None.");
            if (SoundResolution.ResolveMaterial(blocks, 2) != SoundMaterial.None)
                return FailSound(scenario, "a null block entry did not resolve to None.");
            if (SoundResolution.ResolveMaterial(blocks, 99) != SoundMaterial.None)
                return FailSound(scenario, "an out-of-range ID did not resolve to None.");
            if (SoundResolution.ResolveMaterial(null, 1) != SoundMaterial.None)
                return FailSound(scenario, "a null database did not resolve to None.");

            return true;
        }

        /// <summary>
        /// Place reuses the break clips when a group authors none of its own, and stops doing so the moment
        /// place clips exist. Step and Hit deliberately do not fall back — a missing footstep is silence, not
        /// a break sound under the player's feet.
        /// </summary>
        private static bool RunPlaceFallback()
        {
            const string scenario = "Unauthored Place Clips Fall Back To Break Clips";

            AudioClip[] breaks = MakeClips(2);
            AudioClip[] places = MakeClips(1);

            BlockSoundGroup breakOnly = new BlockSoundGroup { breakClips = breaks };
            if (!ReferenceEquals(breakOnly.GetClips(BlockSoundEvent.Place), breaks))
                return FailSound(scenario, "a group with no place clips did not fall back to its break clips.");

            BlockSoundGroup both = new BlockSoundGroup { breakClips = breaks, placeClips = places };
            if (!ReferenceEquals(both.GetClips(BlockSoundEvent.Place), places))
                return FailSound(scenario, "an authored place array was overridden by the fallback.");

            BlockSoundGroup empty = new BlockSoundGroup { breakClips = breaks, placeClips = Array.Empty<AudioClip>() };
            if (!ReferenceEquals(empty.GetClips(BlockSoundEvent.Place), breaks))
                return FailSound(scenario, "an empty (not null) place array did not fall back.");

            if (breakOnly.GetClips(BlockSoundEvent.Step) != null)
                return FailSound(scenario, "Step fell back to break clips; it must stay silent.");
            if (breakOnly.GetClips(BlockSoundEvent.Hit) != null)
                return FailSound(scenario, "Hit fell back to break clips; it must stay silent.");

            return true;
        }

        /// <summary>
        /// The picker must never index past a clip array and must report emptiness rather than guessing, and
        /// the same event must always pick the same clip — the property the rest of the suite relies on.
        /// </summary>
        private static bool RunClipPick()
        {
            const string scenario = "Clip Pick Is Deterministic And Always In Range";

            if (SoundResolution.PickClipIndex(0, 12345u) != -1)
                return FailSound(scenario, "an empty clip array did not report -1.");
            if (SoundResolution.PickClipIndex(-3, 12345u) != -1)
                return FailSound(scenario, "a negative count did not report -1.");

            for (int count = 1; count <= 8; count++)
            {
                for (uint salt = 0; salt < RESOLUTION_SWEEP_EVENTS; salt++)
                {
                    uint hash = SoundResolution.EventHash(SoundMaterial.Stone, BlockSoundEvent.Break, salt);
                    int index = SoundResolution.PickClipIndex(count, hash);

                    if (index < 0 || index >= count)
                        return FailSound(scenario, $"count {count} salt {salt}: index {index} is outside [0, {count}).");
                    if (SoundResolution.PickClipIndex(count, hash) != index)
                        return FailSound(scenario, $"count {count} salt {salt}: the same hash picked two different clips.");
                }
            }

            return true;
        }

        /// <summary>
        /// Pitch jitter is what makes repeated block sounds bearable, so it must actually vary — but never
        /// outside the authored envelope, and never invert when a group is authored with min above max.
        /// </summary>
        private static bool RunPitchEnvelope()
        {
            const string scenario = "Pitch Stays Inside The Group's Envelope";

            BlockSoundGroup group = new BlockSoundGroup { pitchMin = 0.9f, pitchMax = 1.1f };
            BlockSoundGroup inverted = new BlockSoundGroup { pitchMin = 1.2f, pitchMax = 0.8f };
            BlockSoundGroup fixedPitch = new BlockSoundGroup { pitchMin = 1f, pitchMax = 1f };

            float lowest = float.MaxValue;
            float highest = float.MinValue;

            for (uint salt = 0; salt < RESOLUTION_SWEEP_EVENTS; salt++)
            {
                uint hash = SoundResolution.EventHash(SoundMaterial.Wood, BlockSoundEvent.Step, salt);

                float pitch = SoundResolution.PickPitch(group, hash);
                if (pitch < group.pitchMin || pitch > group.pitchMax)
                    return FailSound(scenario, $"salt {salt}: pitch {pitch} left [{group.pitchMin}, {group.pitchMax}].");

                lowest = Mathf.Min(lowest, pitch);
                highest = Mathf.Max(highest, pitch);

                float invertedPitch = SoundResolution.PickPitch(inverted, hash);
                if (invertedPitch < 0.8f || invertedPitch > 1.2f)
                    return FailSound(scenario, $"salt {salt}: an inverted envelope produced {invertedPitch}.");

                if (!Mathf.Approximately(SoundResolution.PickPitch(fixedPitch, hash), 1f))
                    return FailSound(scenario, $"salt {salt}: a zero-width envelope did not produce exactly its bound.");
            }

            // Without this the scenario would pass on a picker that returned the midpoint every time — which is
            // precisely the defect (no jitter) the envelope exists to prevent.
            if (highest - lowest < 0.15f)
                return FailSound(scenario, $"pitch spanned only [{lowest}, {highest}] over {RESOLUTION_SWEEP_EVENTS} " +
                                           "events — the jitter is not actually varying.");

            if (!Mathf.Approximately(SoundResolution.PickPitch(null, 0u), 1f))
                return FailSound(scenario, "a null group did not fall back to unity pitch.");

            return true;
        }

        /// <summary>
        /// The hash is the only variation source behind both pickers, so identical inputs must agree and
        /// different materials or events must not collapse onto one another.
        /// </summary>
        private static bool RunEventHash()
        {
            const string scenario = "Event Hash Separates Materials And Events";

            uint first = SoundResolution.EventHash(SoundMaterial.Stone, BlockSoundEvent.Break, 7u);
            if (SoundResolution.EventHash(SoundMaterial.Stone, BlockSoundEvent.Break, 7u) != first)
                return FailSound(scenario, "the same event hashed to two different values.");

            int materialCollisions = 0;
            int eventCollisions = 0;

            for (uint salt = 0; salt < RESOLUTION_SWEEP_EVENTS; salt++)
            {
                if (SoundResolution.EventHash(SoundMaterial.Stone, BlockSoundEvent.Break, salt)
                    == SoundResolution.EventHash(SoundMaterial.Wood, BlockSoundEvent.Break, salt))
                    materialCollisions++;

                if (SoundResolution.EventHash(SoundMaterial.Stone, BlockSoundEvent.Break, salt)
                    == SoundResolution.EventHash(SoundMaterial.Stone, BlockSoundEvent.Step, salt))
                    eventCollisions++;
            }

            if (materialCollisions > 0)
                return FailSound(scenario, $"{materialCollisions} of {RESOLUTION_SWEEP_EVENTS} salts hashed two " +
                                           "materials identically.");
            if (eventCollisions > 0)
                return FailSound(scenario, $"{eventCollisions} of {RESOLUTION_SWEEP_EVENTS} salts hashed two " +
                                           "events identically.");

            return true;
        }

        /// <summary>
        /// The sub-voxel support rule: standing on a half slab, the slab fills the player's own cell and the
        /// cell below holds whatever it was placed on. Sounding the cell below names a block the player never
        /// touched — walking a stone slab laid over dirt used to play dirt.
        /// </summary>
        /// <remarks>
        /// The tolerance cases are the fragile part and the reason they are pinned: a resting body is parked
        /// <c>COLLISION_EPSILON</c> (0.001) <i>above</i> its surface by the vertical resolve, so an exact
        /// equality test would never fire in game, and a symmetric window twice the probe skin would start
        /// claiming support from blocks the player is genuinely falling past.
        /// </remarks>
        private static bool RunStepSubVoxelSupport()
        {
            const string scenario = "A Sub-Voxel Block Under The Feet Is The Support, Not The Cell Below";

            const ushort air = 0;
            const ushort dirt = 1;
            const ushort stoneSlab = 2;
            const ushort stone = 3;
            const ushort water = 4;
            const ushort glassPane = 5;

            BlockType[] blocks =
            {
                new BlockType { blockName = "Air", isSolid = false, soundMaterial = SoundMaterial.None },
                new BlockType { blockName = "Dirt", isSolid = true, soundMaterial = SoundMaterial.Dirt },
                new BlockType
                {
                    blockName = "Stone Half Slab", isSolid = true, soundMaterial = SoundMaterial.Stone,
                    collisionBounds = new BlockCollisionBounds
                    {
                        mode = CollisionBoundsMode.CustomAABB,
                        min = new Vector3(0f, 0f, 0f),
                        max = new Vector3(1f, 0.5f, 1f),
                    },
                },
                new BlockType { blockName = "Stone", isSolid = true, soundMaterial = SoundMaterial.Stone },
                new BlockType { blockName = "Water", isSolid = false, soundMaterial = SoundMaterial.Liquid },
                new BlockType
                {
                    blockName = "Glass Pane", isSolid = false, soundMaterial = SoundMaterial.Glass,
                    collisionBounds = new BlockCollisionBounds
                    {
                        mode = CollisionBoundsMode.CustomAABB,
                        min = new Vector3(0f, 0f, 0f),
                        max = new Vector3(1f, 0.5f, 1f),
                    },
                },
            };

            // The regression itself: a stone slab over dirt must sound like stone, not dirt.
            SoundResolution.ResolveStep(blocks, stoneSlab, 0, dirt, 64, 64.5f + PHYSICS_REST_OFFSET,
                out SoundMaterial slabSupport, out SoundMaterial slabOccupant);
            if (slabSupport != SoundMaterial.Stone)
                return FailSound(scenario, $"a stone slab over dirt sounded {slabSupport}, expected Stone.");
            if (slabOccupant != SoundMaterial.None)
                return FailSound(scenario, $"the slab layered {slabOccupant} over itself; expected no second layer.");

            // The ordinary case must be untouched: feet on a full block top, occupant cell empty.
            SoundResolution.ResolveStep(blocks, air, 0, stone, 64, 64f + PHYSICS_REST_OFFSET,
                out SoundMaterial flatSupport, out SoundMaterial flatOccupant);
            if (flatSupport != SoundMaterial.Stone || flatOccupant != SoundMaterial.None)
                return FailSound(scenario,
                    $"standing on a full block resolved ({flatSupport}, {flatOccupant}), expected (Stone, None).");

            // Layering must survive the new path: wading is still a splash over the riverbed.
            SoundResolution.ResolveStep(blocks, water, 0, dirt, 64, 64f + PHYSICS_REST_OFFSET,
                out SoundMaterial wadeSupport, out SoundMaterial wadeOccupant);
            if (wadeSupport != SoundMaterial.Dirt || wadeOccupant != SoundMaterial.Liquid)
                return FailSound(scenario,
                    $"wading resolved ({wadeSupport}, {wadeOccupant}), expected (Dirt, Liquid).");

            // A non-solid block cannot carry the feet however its bounds are authored — the player falls through
            // it, so the block below is still the support and the pane layers over it.
            SoundResolution.ResolveStep(blocks, glassPane, 0, dirt, 64, 64.5f + PHYSICS_REST_OFFSET,
                out SoundMaterial paneSupport, out SoundMaterial paneOccupant);
            if (paneSupport != SoundMaterial.Dirt || paneOccupant != SoundMaterial.Glass)
                return FailSound(scenario,
                    $"a non-solid sub-voxel block resolved ({paneSupport}, {paneOccupant}), expected (Dirt, Glass).");

            // --- Tolerance band -------------------------------------------------------------------
            // gap = feetY - slabTop. In game this is +COLLISION_EPSILON; the band must accept that and
            // reject a body genuinely above the surface.
            (float Gap, bool ShouldCarry, string Case)[] gapCases =
            {
                (0f, true, "flush contact"),
                (VoxelRigidbody.GroundProbeSkin * 0.5f, true, "the in-game resting offset"),
                (VoxelRigidbody.GroundProbeSkin, true, "the far edge of the probe skin"),
                (VoxelRigidbody.GroundProbeSkin * 2f, false, "clearly above the surface"),
                (0.1f, false, "falling past the slab"),
                (-VoxelRigidbody.GroundProbeSkin * 2f, false, "embedded below the surface"),
            };

            foreach ((float gap, bool shouldCarry, string label) in gapCases)
            {
                bool carries = SoundResolution.OccupantCarriesFeet(blocks, stoneSlab, 0, 64, 64.5f + gap);
                if (carries != shouldCarry)
                    return FailSound(scenario,
                        $"{label} (gap {gap:R}): carries={carries}, expected {shouldCarry}.");
            }

            // Guards inherited from the material path must survive the bounds lookup, which dereferences
            // collisionBounds unconditionally.
            if (SoundResolution.OccupantCarriesFeet(null, stoneSlab, 0, 64, 64.5f))
                return FailSound(scenario, "a null database claimed to carry the feet.");
            if (SoundResolution.OccupantCarriesFeet(blocks, 99, 0, 64, 64.5f))
                return FailSound(scenario, "an out-of-range block ID claimed to carry the feet.");

            return true;
        }

        /// <summary>
        /// The height the vertical resolve parks a resting body above its surface. Mirrors
        /// <c>VoxelRigidbody.COLLISION_EPSILON</c>, which is private — kept as a local literal so this suite
        /// pins the in-game geometry rather than following a constant that could change underneath it.
        /// </summary>
        private const float PHYSICS_REST_OFFSET = 0.001f;

        /// <summary>
        /// The sampling geometry itself: which two cells a step reads for a given feet height. Below y = 0 a
        /// truncating cast would round toward zero and sample the cell above, which is why floor is pinned here
        /// rather than left to the caller.
        /// </summary>
        private static bool RunStepCells()
        {
            const string scenario = "Step Samples The Occupied Cell And The Supporting Cell";

            // feetY, expected occupant, expected support.
            (float FeetY, int Occupant, int Support)[] cases =
            {
                (64f, 64, 63), // Resting exactly on a block top.
                (64.5f, 64, 63), // Mid-cell, as after a step-up onto a slab.
                (64.999f, 64, 63), // Just below the next cell boundary.
                (0f, 0, -1), // The origin plane.
                (-0.5f, -1, -2), // Below y = 0: truncation would answer (0, -1).
                (-64f, -64, -65), // Deep negative, well away from the sign boundary.
            };

            foreach ((float feetY, int expectedOccupant, int expectedSupport) in cases)
            {
                SoundResolution.StepCells(feetY, out int occupant, out int support);

                if (occupant != expectedOccupant)
                    return FailSound(scenario, $"feet at y={feetY} occupied cell {occupant}, expected {expectedOccupant}.");
                if (support != expectedSupport)
                    return FailSound(scenario, $"feet at y={feetY} supporting cell {support}, expected {expectedSupport}.");
                if (support != occupant - 1)
                    return FailSound(scenario, $"feet at y={feetY} gave non-adjacent cells {occupant} and {support}.");
            }

            return true;
        }

        /// <summary>
        /// The layering rule that makes wading and flora audible: a non-solid occupant sounds <i>in addition
        /// to</i> the block supporting the player, a solid one adds nothing, and an occupant matching the
        /// support is dropped rather than doubled.
        /// </summary>
        private static bool RunStepOccupantLayering()
        {
            const string scenario = "A Non-Solid Occupant Layers Over The Supporting Block";

            // Mirrors the shipped palette: the only non-solid sounding blocks are the two fluids and cross-mesh flora.
            const ushort air = 0;
            const ushort stone = 1;
            const ushort sand = 2;
            const ushort water = 3;
            const ushort grassBlades = 4;
            const ushort slab = 5;
            const ushort mutePlant = 6;
            const ushort stoneDust = 7;

            BlockType[] blocks =
            {
                new BlockType { blockName = "Air", isSolid = false, soundMaterial = SoundMaterial.None },
                new BlockType { blockName = "Stone", isSolid = true, soundMaterial = SoundMaterial.Stone },
                new BlockType { blockName = "Sand", isSolid = true, soundMaterial = SoundMaterial.Sand },
                new BlockType { blockName = "Water", isSolid = false, soundMaterial = SoundMaterial.Liquid },
                new BlockType { blockName = "Grass Blades", isSolid = false, soundMaterial = SoundMaterial.Plant },
                new BlockType { blockName = "Half Slab", isSolid = true, soundMaterial = SoundMaterial.Stone },
                new BlockType { blockName = "Mute Plant", isSolid = false, soundMaterial = SoundMaterial.None },
                new BlockType { blockName = "Stone Dust", isSolid = false, soundMaterial = SoundMaterial.Stone },
            };

            // occupant, support, expected support layer, expected occupant layer, what the case represents.
            (ushort Occupant, ushort Support, SoundMaterial Support2, SoundMaterial Occupant2, string Case)[] cases =
            {
                (water, sand, SoundMaterial.Sand, SoundMaterial.Liquid, "wading: a splash over the riverbed"),
                (grassBlades, stone, SoundMaterial.Stone, SoundMaterial.Plant, "flora rustling over the ground"),
                (air, stone, SoundMaterial.Stone, SoundMaterial.None, "the ordinary case: air adds no layer"),
                (slab, stone, SoundMaterial.Stone, SoundMaterial.None, "a solid occupant adds no layer"),
                (mutePlant, sand, SoundMaterial.Sand, SoundMaterial.None, "a silent occupant adds no layer"),
                (stoneDust, stone, SoundMaterial.Stone, SoundMaterial.None, "an occupant matching the support is not doubled"),
                (air, air, SoundMaterial.None, SoundMaterial.None, "nothing under the feet at all"),
            };

            foreach ((ushort occupant, ushort support, SoundMaterial expectedSupport, SoundMaterial expectedOccupant,
                         string label) in cases)
            {
                SoundResolution.ResolveStepMaterials(blocks, occupant, support,
                    out SoundMaterial actualSupport, out SoundMaterial actualOccupant);

                if (actualSupport != expectedSupport)
                    return FailSound(scenario, $"{label}: support layer was {actualSupport}, expected {expectedSupport}.");
                if (actualOccupant != expectedOccupant)
                    return FailSound(scenario, $"{label}: occupant layer was {actualOccupant}, expected {expectedOccupant}.");
            }

            // The supporting block must keep sounding even when an occupant layers over it — the whole point
            // of layering over the earlier winner-takes-all rule.
            SoundResolution.ResolveStepMaterials(blocks, water, sand, out SoundMaterial wadeSupport, out _);
            if (wadeSupport == SoundMaterial.None)
                return FailSound(scenario, "wading silenced the riverbed instead of layering over it.");

            // The out-of-range and null guards ResolveMaterial already carries must survive the occupant path.
            SoundResolution.ResolveStepMaterials(blocks, 99, stone, out SoundMaterial oorSupport, out SoundMaterial oorOccupant);
            if (oorSupport != SoundMaterial.Stone || oorOccupant != SoundMaterial.None)
                return FailSound(scenario, "an out-of-range occupant did not leave the support alone.");

            SoundResolution.ResolveStepMaterials(null, water, stone, out SoundMaterial nullSupport, out SoundMaterial nullOccupant);
            if (nullSupport != SoundMaterial.None || nullOccupant != SoundMaterial.None)
                return FailSound(scenario, "a null database did not resolve to None on both layers.");

            return true;
        }

        /// <summary>
        /// Builds a throwaway clip array. The clips are procedural and never played — only their identity and
        /// count matter to the resolution chain.
        /// </summary>
        /// <param name="count">How many clips to create.</param>
        /// <returns>An array of distinct, non-null clips.</returns>
        private static AudioClip[] MakeClips(int count)
        {
            AudioClip[] clips = new AudioClip[count];
            for (int i = 0; i < count; i++) clips[i] = AudioClip.Create($"ValidationClip{i}", 16, 1, 8000, false);
            return clips;
        }

        /// <summary>Logs a scenario failure in the suite's standard form and returns false.</summary>
        /// <param name="scenario">The failing scenario's name.</param>
        /// <param name="detail">What was expected and what happened instead.</param>
        /// <returns>Always false, so callers can <c>return FailSound(...)</c>.</returns>
        private static bool FailSound(string scenario, string detail)
        {
            Debug.LogError($"[FAIL] {scenario} — {detail}");
            return false;
        }
    }
}
