using Data;
using Serialization;
using UnityEngine;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Part 3 of the suite (roadmap <c>NS-1</c>): the compression matrix. Every shipped
    /// <see cref="CompressionAlgorithm"/> arm must round-trip a chunk identically, and a chunk written under
    /// one algorithm must still load when the world's <c>saveCompression</c> setting says another — the
    /// algorithm is a property of the stored record, not of the current settings, and a world whose setting
    /// changed between sessions would otherwise be unreadable.
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        /// <summary>The algorithm arms this suite covers. <c>GZip</c> is reserved (commented out in the enum),
        /// so these three are the whole shipped surface — if a fourth arm lands, it belongs here.</summary>
        private static readonly CompressionAlgorithm[] s_compressionArms =
        {
            CompressionAlgorithm.None,
            CompressionAlgorithm.Deflate,
            CompressionAlgorithm.LZ4,
        };

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>
        /// B7. Red when: any compression arm fails to round-trip a chunk, or an arm silently degrades to a
        /// passthrough. The non-vacuity half matters as much as the identity half — a codec that quietly
        /// stopped compressing would round-trip perfectly while every save on disk grew ~4x.
        /// </summary>
        /// <returns>True when all three arms round-trip identically and both codecs actually compress.</returns>
        private static bool EveryCompressionArmRoundTrips()
        {
            using Fixture fx = new Fixture();
            Vector2Int pos = new Vector2Int(64, 64);
            PoolBalance balance = PoolBalance.Capture();

            ChunkData source = BuildReferenceChunk(pos);
            bool ok = true;
            int uncompressedLength = 0;
            try
            {
                foreach (CompressionAlgorithm algorithm in s_compressionArms)
                {
                    byte[] payload = Serialize(source, algorithm);
                    if (algorithm == CompressionAlgorithm.None) uncompressedLength = payload.Length;

                    ChunkData loaded = ChunkSerializer.Deserialize(payload, algorithm, pos);
                    if (loaded == null)
                    {
                        ok &= Check($"{algorithm.ToString()} payload deserializes", false);
                        continue;
                    }

                    try
                    {
                        ok &= AssertChunksEquivalent(source, loaded, $"{algorithm.ToString()} round trip");
                    }
                    finally
                    {
                        World.Instance.ChunkPool.ReturnChunkData(loaded);
                    }

                    if (algorithm == CompressionAlgorithm.None) continue;

                    // Non-vacuity: the codec must actually have engaged. The fixture chunk carries two full
                    // LightData sections and a large voxel run, so every real codec compresses it heavily.
                    ok &= Check($"{algorithm.ToString()} actually compresses ({payload.Length.ToString()} bytes vs {uncompressedLength.ToString()} uncompressed)",
                        payload.Length < uncompressedLength);
                }
            }
            finally
            {
                World.Instance.ChunkPool.ReturnChunkData(source);
            }

            ok &= balance.AssertUnchanged("pools balanced after the compression matrix");
            return ok;
        }

        /// <summary>
        /// B8. The cross-load contract, through the full storage stack: three chunks are each saved under a
        /// DIFFERENT <c>saveCompression</c> setting, then all three are read back while the setting names yet
        /// another arm. Red when: the load path starts trusting the current setting instead of the algorithm
        /// byte stored with the record — which would make every world unreadable the moment a player (or a
        /// settings default) changes compression, with the chunks silently regenerating over saved edits.
        /// </summary>
        /// <returns>True when every chunk loads identically regardless of the active setting.</returns>
        private static bool ChunksLoadRegardlessOfTheActiveCompressionSetting()
        {
            using Fixture fx = new Fixture();
            CompressionAlgorithm[] writeArms = s_compressionArms;
            Vector2Int[] positions = new Vector2Int[writeArms.Length];
            bool ok = true;

            // Write each chunk under its own algorithm.
            for (int i = 0; i < writeArms.Length; i++)
            {
                positions[i] = new Vector2Int(i * 16, 96);
                World.Instance.settings.saveCompression = writeArms[i];

                ChunkData source = BuildReferenceChunk(positions[i]);
                try
                {
                    ok &= Check($"chunk written with {writeArms[i].ToString()} reports Written",
                        RunSave(fx.Storage, source) == ChunkSaveResult.Written);
                }
                finally
                {
                    World.Instance.ChunkPool.ReturnChunkData(source);
                }
            }

            // Read them all back under a setting that matches at most one of them.
            foreach (CompressionAlgorithm activeSetting in s_compressionArms)
            {
                World.Instance.settings.saveCompression = activeSetting;

                for (int i = 0; i < writeArms.Length; i++)
                {
                    ChunkData expected = BuildReferenceChunk(positions[i]);
                    ChunkData loaded = RunLoad(fx.Storage, positions[i]);
                    try
                    {
                        if (loaded == null)
                        {
                            ok &= Check($"chunk written with {writeArms[i].ToString()} loads while the setting says {activeSetting.ToString()}", false);
                            continue;
                        }

                        ok &= AssertChunksEquivalent(expected, loaded,
                            $"written {writeArms[i].ToString()} / setting {activeSetting.ToString()}");
                    }
                    finally
                    {
                        World.Instance.ChunkPool.ReturnChunkData(expected);
                        if (loaded != null) World.Instance.ChunkPool.ReturnChunkData(loaded);
                    }
                }
            }

            return ok;
        }
    }
}
