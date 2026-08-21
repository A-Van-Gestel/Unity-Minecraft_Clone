using System;
using System.Security.Cryptography;
using System.Text;
using Data;
using Editor.Validation.Framework;
using Serialization;
using UnityEngine;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Part 2 of the suite (roadmap <c>NS-1</c>): the golden-byte format guard. The reference fixture chunk's
    /// serialized bytes are pinned by hash against the chunk-format version they were captured under, so any
    /// layout change that ships WITHOUT a version bump turns this red — the failure mode the
    /// <c>serialization-safety</c> rules exist to prevent, since an unbumped layout change is read by the live
    /// serializer as if it were the old layout and silently corrupts every chunk it touches.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        /// <summary>
        /// The chunk-format version the golden hash below was captured under. This is read from the PAYLOAD
        /// (its first byte), not from <c>ChunkSerializer.CURRENT_CHUNK_VERSION</c> — that constant is private,
        /// and pinning the on-disk byte is the stronger assertion anyway: it is what a save actually carries.
        /// <para><b>When the format legitimately changes:</b> bump this, blank
        /// <see cref="GOLDEN_REFERENCE_PAYLOAD_HASH"/> to re-enter capture mode, re-run, paste the new hash —
        /// and make sure a migration step ships in the same commit.</para>
        /// </summary>
        private const byte GOLDEN_CHUNK_FORMAT_VERSION = 7;

        /// <summary>
        /// SHA-256 (hex, uppercase) of the reference fixture chunk serialized with
        /// <see cref="CompressionAlgorithm.None"/> at chunk position (0,0). Empty string = capture mode.
        /// </summary>
        private const string GOLDEN_REFERENCE_PAYLOAD_HASH =
            "231E9FF451778DE56A44654B9716736940906D6CFF4866DB4846DACBF74CADC0";

        /// <summary>Fixed position for the golden fixture — the position is IN the payload, so it must not vary.</summary>
        private static readonly Vector2Int s_goldenChunkPos = new Vector2Int(0, 0);

        /// <summary>Expected byte length of the golden payload; pinned alongside the hash as a readable diff.</summary>
        private const int GOLDEN_REFERENCE_PAYLOAD_LENGTH = 49856;

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>
        /// B6. Red when: the serialized layout changes in any way — field order, widths, section encoding,
        /// heightmap size, queue record shape — without <c>CURRENT_CHUNK_VERSION</c> being bumped with it.
        /// A version bump alone does not silence this: the pinned version byte is asserted too, so the
        /// re-capture is a deliberate, reviewable edit rather than a silent re-baseline.
        /// </summary>
        /// <returns>True when the payload matches the frozen hash, length and version byte.</returns>
        private static bool GoldenPayloadBytesAreFrozen()
        {
            using Fixture fx = new Fixture();

            ChunkData source = BuildReferenceChunk(s_goldenChunkPos);
            byte[] payload;
            byte[] second;
            try
            {
                payload = SerializeUncompressed(source);
                second = SerializeUncompressed(source);
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(source);
            }

            // Determinism first: in capture mode GoldenMaster returns true, so without this the scenario
            // would be a free green on the very run that freezes the hash.
            bool ok = AssertPayloadsIdentical(payload, second, "serializing the same chunk twice");

            ok &= Check($"the on-disk chunk-format version byte is v{GOLDEN_CHUNK_FORMAT_VERSION.ToString()} (got v{payload[0].ToString()})",
                payload[0] == GOLDEN_CHUNK_FORMAT_VERSION);

            ok &= Check($"payload length is unchanged (expected {GOLDEN_REFERENCE_PAYLOAD_LENGTH.ToString()}, got {payload.Length.ToString()})",
                payload.Length == GOLDEN_REFERENCE_PAYLOAD_LENGTH);

            ok &= GoldenMaster.AssertOrCapture(
                $"B6 golden chunk payload (v{payload[0].ToString()}, {payload.Length.ToString()} bytes)",
                HashPayload(payload),
                GOLDEN_REFERENCE_PAYLOAD_HASH);

            return ok;
        }

        // --- Helpers -----------------------------------------------------------------------------

        /// <summary>
        /// SHA-256 of a payload, hex-encoded — the golden pin. A hash rather than an embedded byte blob: the
        /// payload is ~49 KB, and a mismatch is diagnosed from the length/version assertions above plus the
        /// round-trip scenarios, not by eyeballing bytes.
        /// </summary>
        /// <param name="payload">The bytes to hash.</param>
        /// <returns>Uppercase hex SHA-256.</returns>
        private static string HashPayload(byte[] payload)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(payload);
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }
}
