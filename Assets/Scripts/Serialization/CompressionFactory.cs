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
        private static bool? _lz4Available;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            _lz4Available = null;
        }

        /// <summary>
        /// Checks if the NativeCompressions DLL is loaded and functional.
        /// </summary>
        private static bool IsLZ4Available()
        {
            if (_lz4Available.HasValue) return _lz4Available.Value;

            try
            {
                // Test instantiation. This will throw DllNotFoundException if the native plugin is missing.
                using MemoryStream testStream = new MemoryStream();
                using LZ4Stream lz4 = new LZ4Stream(testStream, CompressionMode.Compress, true);
                _lz4Available = true;
                return true;
            }
            catch (Exception ex) // Catches DllNotFoundException and TypeInitializationException
            {
                Debug.LogError($"[CompressionFactory] LZ4 native library not found or failed to initialize. Falling back to GZip. Error: {ex.Message}");
                _lz4Available = false;
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
                    throw new ArgumentException($"Unsupported compression algorithm: {algorithm}");
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
                        return new LZ4Stream(inputStream, CompressionMode.Decompress, leaveOpen);
                    }

                    // If we are trying to READ an LZ4 file but the DLL is missing, we cannot fallback.
                    throw new InvalidOperationException("Cannot decompress LZ4 chunk: Native library is missing.");

                default:
                    throw new ArgumentException($"Unsupported decompression algorithm: {algorithm}");
            }
        }
    }
}
