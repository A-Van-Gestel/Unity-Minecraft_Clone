using System;
using System.Collections.Generic;
using Audio;
using Data;
using Data.Enums;
using Editor.Validation.Framework;
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
