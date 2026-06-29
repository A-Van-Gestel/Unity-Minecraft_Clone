using System;
using System.IO;
using System.IO.Compression;
using NativeCompressions;
using UnityEngine;

namespace Serialization
{
    /// <summary>
    /// A wrapper factory for creating compression streams.
    /// Abstracts the underlying implementation and handles native library availability checks.
    /// </summary>
    public static class CompressionFactory
    {
        // The standard LZ4 Frame format magic number (little-endian on disk: 04 22 4D 18).
        // Checked before decompression because NativeCompressions' LZ4Stream spins forever on
        // non-frame input instead of throwing (see Documentation/Bugs/SERIALIZATION_BUGS.md #03);
        // the check turns corrupt payloads into an InvalidDataException, which the chunk
        // deserializer handles via its "corrupt chunk -> warn -> regenerate" path.
        private const uint LZ4_FRAME_MAGIC = 0x184D2204;

        private static bool? s_lz4Available;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            s_lz4Available = null;
        }

        /// <summary>
        /// Checks if the NativeCompressions DLL is loaded and functional.
        /// </summary>
        private static bool IsLZ4Available()
        {
            if (s_lz4Available.HasValue) return s_lz4Available.Value;

            try
            {
                // Test instantiation. This will throw DllNotFoundException if the native plugin is missing.
                using MemoryStream testStream = new MemoryStream();
                using LZ4Stream lz4 = new LZ4Stream(testStream, CompressionMode.Compress, true);
                s_lz4Available = true;
                return true;
            }
            catch (Exception ex) // Catches DllNotFoundException and TypeInitializationException
            {
                Debug.LogError($"[CompressionFactory] LZ4 native library not found or failed to initialize. Falling back to GZip. Error: {ex.Message}");
                s_lz4Available = false;
                return false;
            }
        }

        /// <summary>
        /// Creates a stream that WRITES compressed data to the underlying output stream.
        /// </summary>
        public static Stream CreateOutputStream(Stream outputStream, CompressionAlgorithm algorithm, bool leaveOpen = true)
        {
            switch (algorithm)
            {
                case CompressionAlgorithm.None:
                    return outputStream;

                case CompressionAlgorithm.GZip:
                    return new DeflateStream(outputStream, CompressionMode.Compress, leaveOpen);

                case CompressionAlgorithm.LZ4:
                    if (IsLZ4Available())
                    {
                        return new LZ4Stream(outputStream, CompressionMode.Compress, leaveOpen);
                    }

                    // Fallback ensures the game can still save even if the plugin breaks
                    Debug.LogWarning("[CompressionFactory] LZ4 requested but not available. Fallback to GZip.");
                    return new DeflateStream(outputStream, CompressionMode.Compress, leaveOpen);

                default:
                    throw new ArgumentException($"Unsupported compression algorithm: {algorithm.ToString()}");
            }
        }

        /// <summary>
        /// Creates a stream that READS decompressed data from the underlying input stream.
        /// </summary>
        public static Stream CreateInputStream(Stream inputStream, CompressionAlgorithm algorithm, bool leaveOpen = false)
        {
            switch (algorithm)
            {
                case CompressionAlgorithm.None:
                    return inputStream;

                case CompressionAlgorithm.GZip:
                    return new DeflateStream(inputStream, CompressionMode.Decompress, leaveOpen);

                case CompressionAlgorithm.LZ4:
                    if (IsLZ4Available())
                    {
                        ValidateLz4FrameMagic(inputStream);
                        return new LZ4Stream(inputStream, CompressionMode.Decompress, leaveOpen);
                    }

                    // If we are trying to READ an LZ4 file but the DLL is missing, we cannot fallback.
                    throw new InvalidOperationException("Cannot decompress LZ4 chunk: Native library is missing.");

                default:
                    throw new ArgumentException($"Unsupported decompression algorithm: {algorithm.ToString()}");
            }
        }

        /// <summary>
        /// Verifies that a seekable stream positioned at an LZ4 payload starts with the LZ4 Frame
        /// magic number, restoring the stream position afterwards. Keeps non-frame data away from
        /// the native frame decompressor, which spins forever instead of throwing
        /// (see <c>Documentation/Bugs/SERIALIZATION_BUGS.md</c> #03).
        /// </summary>
        /// <param name="inputStream">The stream containing the compressed payload.</param>
        /// <exception cref="InvalidDataException">The payload does not start with the LZ4 frame magic.</exception>
        private static void ValidateLz4FrameMagic(Stream inputStream)
        {
            // Non-seekable streams can't be peeked without consuming data; all current callers
            // (UnmanagedMemoryStream / MemoryStream over region payloads) are seekable.
            if (!inputStream.CanSeek) return;

            long startPosition = inputStream.Position;

            Span<byte> magicBytes = stackalloc byte[4];
            int read = inputStream.Read(magicBytes);
            inputStream.Position = startPosition;

            if (read < 4)
            {
                throw new InvalidDataException(
                    $"LZ4 payload truncated: only {read} bytes available, cannot contain a frame header.");
            }

            uint magic = (uint)(magicBytes[0] | (magicBytes[1] << 8) | (magicBytes[2] << 16) | (magicBytes[3] << 24));
            if (magic != LZ4_FRAME_MAGIC)
            {
                throw new InvalidDataException(
                    $"LZ4 payload does not start with the frame magic (got 0x{magic:X8}, expected 0x{LZ4_FRAME_MAGIC:X8}). " +
                    "Payload is corrupt or was written in an incompatible format.");
            }
        }
    }
}
