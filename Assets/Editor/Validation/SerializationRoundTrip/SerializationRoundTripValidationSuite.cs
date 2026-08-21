using System.Collections.Generic;
using Data;
using Editor.Validation.Framework;
using Serialization;
using UnityEditor;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Validation suite for the chunk save-format contract (roadmap <c>NS-1</c> parts 1–5): a chunk that goes
    /// through <see cref="ChunkSerializer"/> and the region layer must come back semantically identical, must
    /// be encoded with the section flags the v7 layout defines, and must keep its bytes frozen for a given
    /// chunk-format version. It guards the engine's worst failure class — silent save corruption — from the
    /// happy-path side.
    /// <para><b>Charter split.</b> <c>Validate Deserialization Robustness</c> owns the FAILURE paths at the load
    /// boundary (truncated / garbage / wrong-version payloads → null, no throw, no pooled leak) and
    /// <c>Validate Save Durability</c> owns the retry/staging contract. This suite owns format FIDELITY:
    /// round-trip identity, section-flag classification, golden bytes, the compression matrix, and (part 4–5)
    /// <c>RegionFile</c> sector mechanics and the pending stores.</para>
    /// <para><b>Fixture independence.</b> The fixture palette uses test-local voxel ids rather than
    /// <c>BlockIDs</c> constants — this suite pins serialized bytes, so it must not move when
    /// <c>BlockDatabase.asset</c> is re-authored. See the palette block in the <c>.Fixture.cs</c> partial.</para>
    /// <para><b>Prove-red:</b> recorded per part as each lands; a baseline nobody has observed failing proves
    /// nothing. Part 1: making <c>WriteSection</c> always emit flag 0x01 must red the flag-classification
    /// scenarios while leaving the accessor-level round-trip green (which is exactly why both exist).</para>
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        /// <summary>Runs every scenario and prints a categorized summary via the shared runner.</summary>
        [MenuItem("Minecraft Clone/Dev/Validate Serialization Round-Trip")]
        public static void RunAll() => Execute();

        /// <summary>
        /// Builds and runs the round-trip scenarios, returning the categorized result (the headless/CI entry
        /// point).
        /// </summary>
        /// <param name="logToConsole">When false, runs silently and only returns the result (for headless/CI use).</param>
        /// <param name="showProgress">When false, suppresses this suite's own progress bar (the aggregate runner drives one).</param>
        /// <returns>The categorized, timed result of the run.</returns>
        public static ValidationRunResult Execute(bool logToConsole = true, bool showProgress = true)
        {
            List<Scenario> scenarios = new List<Scenario>
            {
                new Scenario("B1: fixture integrity — the reference chunk exercises all four section flags, and data-less sections are excluded", FixtureCoversEverySectionFlag),
            };
            return ValidationSuiteRunner.Execute("Serialization Round-Trip", scenarios, KnownBugChannel.Bug, logToConsole, showProgress);
        }

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>
        /// B1. The vacuous-pass guard every later scenario leans on: if the fixture chunk stopped producing
        /// one of the four section-flag classes, the round-trip, golden-byte and compression scenarios would
        /// all still pass while silently covering less. Red when: the builder no longer emits every flag
        /// class, or a data-less section starts being written into the bitmask (save bloat).
        /// </summary>
        /// <returns>True when the reference payload's flag map matches the expected map exactly.</returns>
        private static bool FixtureCoversEverySectionFlag()
        {
            using Fixture fx = new Fixture();
            PoolBalance balance = PoolBalance.Capture();

            ChunkData data = BuildReferenceChunk(new UnityEngine.Vector2Int(0, 0));
            int sectionCount = data.sections.Length;
            byte[] payload;
            try
            {
                payload = SerializeUncompressed(data);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(data);
            }

            bool ok = Check($"payload is non-degenerate ({payload.Length.ToString()} bytes)",
                payload.Length > PAYLOAD_HEADER_BYTES + PAYLOAD_HEIGHTMAP_BYTES);

            byte[] flags = ParseSectionFlags(payload, sectionCount);
            ok &= Check($"section flag map matches the fixture contract (expected {FormatFlags(s_expectedFixtureFlags)}, got {FormatFlags(flags)})",
                FlagMapsEqual(s_expectedFixtureFlags, flags));

            // Stated separately from the map compare so a shrunk fixture reads as "lost a flag class"
            // rather than as an opaque map mismatch.
            ok &= Check("all four section flag classes are present in the payload",
                HasFlag(flags, 0x00) && HasFlag(flags, 0x01) && HasFlag(flags, 0x02) && HasFlag(flags, 0x03));

            ok &= balance.AssertUnchanged("pools balanced after building and returning the fixture chunk");
            return ok;
        }

        // --- Scenario helpers --------------------------------------------------------------------

        /// <summary>Compares two flag maps slot by slot (length mismatch counts as a mismatch).</summary>
        /// <param name="expected">The expected flag map.</param>
        /// <param name="actual">The parsed flag map.</param>
        /// <returns>True when every slot matches.</returns>
        private static bool FlagMapsEqual(IReadOnlyList<byte> expected, IReadOnlyList<byte> actual)
        {
            if (expected.Count != actual.Count) return false;
            for (int i = 0; i < expected.Count; i++)
                if (expected[i] != actual[i])
                    return false;
            return true;
        }

        /// <summary>True when the flag map contains at least one section written with <paramref name="flag"/>.</summary>
        /// <param name="flags">The parsed flag map.</param>
        /// <param name="flag">The section flag to look for.</param>
        /// <returns>True when present.</returns>
        private static bool HasFlag(IReadOnlyList<byte> flags, byte flag)
        {
            foreach (byte f in flags)
                if (f == flag)
                    return true;
            return false;
        }
    }
}
