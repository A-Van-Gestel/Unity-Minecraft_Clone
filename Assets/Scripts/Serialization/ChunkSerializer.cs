using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Data;
using Helpers;
using UnityEngine;

namespace Serialization
{
    /// <summary>
    /// Handles the binary serialization and deserialization of ChunkData.
    /// Supports multiple compression algorithms via the <see cref="CompressionAlgorithm"/> enum.
    /// </summary>
    public static class ChunkSerializer
    {
        private const byte CURRENT_CHUNK_VERSION = 1;

        // FUTURE-PROOFING: Version header for individual sections.
        // Allows upgrading section format (e.g. adding Palettes) without breaking the chunk header.
        private const byte CURRENT_SECTION_VERSION = 1;

        /// <summary>
        /// Serializes a ChunkData object into a byte array buffer using the specified compression algorithm.
        /// </summary>
        /// <param name="data">The chunk data to serialize.</param>
        /// <param name="outputBuffer">The reusable buffer to write to.</param>
        /// <param name="algorithm">The compression algorithm to use.</param>
        /// <returns>The number of bytes written to the buffer (including the 4-byte length header).</returns>
        public static int Serialize(ChunkData data, byte[] outputBuffer, CompressionAlgorithm algorithm)
        {
            // Write to the pre-allocated buffer
            using var memoryStream = new MemoryStream(outputBuffer);

            switch (algorithm)
            {
                case CompressionAlgorithm.GZip:
                    // DeflateStream (GZip style)
                    using (var compressionStream = new DeflateStream(memoryStream, CompressionMode.Compress, true))
                    using (var writer = new BinaryWriter(compressionStream))
                    {
                        WriteChunkInternal(writer, data);
                        writer.Flush();
                    }

                    break;

                case CompressionAlgorithm.None:
                    // Raw writes
                    using (var writer = new BinaryWriter(memoryStream, Encoding.UTF8, true))
                    {
                        WriteChunkInternal(writer, data);
                        writer.Flush();
                    }

                    break;

                // TODO: Future implementation for LZ4:
                // case CompressionAlgorithm.LZ4: ...

                default:
                    Debug.LogError($"Unsupported compression algorithm for serialization: {algorithm}");
                    return 0;
            }

            return (int)memoryStream.Position;
        }

        /// <summary>
        /// Deserializes a byte array into a ChunkData object based on the specified compression algorithm.
        /// </summary>
        public static ChunkData Deserialize(ReadOnlySpan<byte> data, CompressionAlgorithm algorithm, Vector2Int debugCoord)
        {
            if (data.Length == 0) return null;

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    using var unmanagedStream = new UnmanagedMemoryStream(ptr, data.Length);

                    // Set up the reader stream based on compression type
                    Stream readStream = unmanagedStream;
                    Stream decompressionStream = null; // Helper to dispose if created

                    try
                    {
                        switch (algorithm)
                        {
                            case CompressionAlgorithm.GZip:
                                decompressionStream = new DeflateStream(unmanagedStream, CompressionMode.Decompress);
                                readStream = decompressionStream;
                                break;

                            case CompressionAlgorithm.None:
                                // readStream is already unmanagedStream
                                break;

                            default:
                                Debug.LogError($"Unsupported compression algorithm for deserialization: {algorithm}");
                                return null;
                        }

                        using var reader = new BinaryReader(readStream);
                        return ReadChunkInternal(reader, debugCoord, data.Length);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Chunk {debugCoord} deserialization failed ({algorithm}). Payload: {data.Length} bytes. Error: {ex.GetType().Name} - {ex.Message}");
                        return null;
                    }
                    finally
                    {
                        decompressionStream?.Dispose();
                    }
                }
            }
        }

        // --- Internal Write Logic ---
        private static void WriteChunkInternal(BinaryWriter writer, ChunkData data)
        {
            // --- Chunk Header ---
            writer.Write(CURRENT_CHUNK_VERSION);
            writer.Write(data.position.x);
            writer.Write(data.position.y); // Z coordinate (Vector2Int.y)
            
            // --- State Flags ---
            writer.Write(data.NeedsInitialLighting);

            // --- Height Map ---
            // Heightmap is fixed size (16*16 = 256 bytes)
            writer.Write(data.heightMap);

            // --- Section Bitmask ---
            int sectionBitmask = 0;
            for (int i = 0; i < data.sections.Length; i++)
            {
                if (data.sections[i] != null && !data.sections[i].IsEmpty)
                {
                    sectionBitmask |= (1 << i);
                }
            }

            writer.Write(sectionBitmask);

            // --- Write Sections ---
            for (int i = 0; i < data.sections.Length; i++)
            {
                // Check bitmask to skip empty sections
                if ((sectionBitmask & (1 << i)) != 0)
                {
                    WriteSection(writer, data.sections[i]);
                }
            }

            // --- Write Lighting Queues ---
            // We access the raw queues from ChunkData. 
            WriteLightQueue(writer, data.SunlightBfsQueue);
            WriteLightQueue(writer, data.BlocklightBfsQueue);
        }

        // --- Internal Read Logic ---
        private static ChunkData ReadChunkInternal(BinaryReader reader, Vector2Int coord, int totalLen)
        {
            // Diagnostics for debugging stream position
            long startPos = reader.BaseStream.CanSeek ? reader.BaseStream.Position : 0;

            try 
            {
                // --- Chunk Header ---
                // Safety check: Don't try to load chunks from the future.
                byte version = reader.ReadByte();
                if (version > CURRENT_CHUNK_VERSION)
                    throw new InvalidDataException($"Unsupported Version: {version}");

                int x = reader.ReadInt32();
                int z = reader.ReadInt32();
                
                // Sanity check coordinates
                if (x != coord.x || z != coord.y)
                    Debug.LogWarning($"Chunk coord mismatch at {coord}. Read: {x},{z}");

                ChunkData chunk = new ChunkData(x, z);
                
                // --- State Flags ---
                chunk.NeedsInitialLighting = reader.ReadBoolean();

                // --- Height Map (256 bytes) ---
                chunk.heightMap = reader.ReadBytes(VoxelData.ChunkWidth * VoxelData.ChunkWidth);
                if (chunk.heightMap.Length != VoxelData.ChunkWidth * VoxelData.ChunkWidth)
                    throw new EndOfStreamException("Heightmap truncated");

                // --- Sections ---
                int sectionBitmask = reader.ReadInt32();
                for (int i = 0; i < chunk.sections.Length; i++)
                {
                    if ((sectionBitmask & (1 << i)) != 0)
                        chunk.sections[i] = ReadSection(reader);
                }

                // --- Lighting ---
                ReadLightQueue(reader, chunk.SunlightBfsQueue);
                ReadLightQueue(reader, chunk.BlocklightBfsQueue);

                // If we loaded pending lights, flag the chunk for processing
                if (chunk.SunLightQueueCount > 0 || chunk.BlockLightQueueCount > 0)
                {
                    chunk.HasLightChangesToProcess = true;
                }

                chunk.IsPopulated = true;
                return chunk;
            }
            catch (Exception)
            {
                long curr = reader.BaseStream.CanSeek ? reader.BaseStream.Position : -1;
                Debug.LogError($"Deserialize Crash at stream pos {curr}. Expected Payload Size: {totalLen}");
                throw;
            }
        }

        // --- Helpers for Lighting ---

        private static void WriteLightQueue(BinaryWriter writer, Queue<LightQueueNode> queue)
        {
            // Write Count
            writer.Write(queue.Count);

            // Write Items
            foreach (var node in queue)
            {
                writer.Write(node.Position.x);
                writer.Write(node.Position.y);
                writer.Write(node.Position.z);
                writer.Write(node.OldLightLevel);
            }
        }

        private static void ReadLightQueue(BinaryReader reader, Queue<LightQueueNode> queue)
        {
            int count = reader.ReadInt32();
            // Sanity check to prevent OOM on corrupt data
            if (count < 0 || count > 100000) 
                throw new InvalidDataException($"Invalid LightQueue count: {count}");

            for (int i = 0; i < count; i++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int z = reader.ReadInt32();
                byte level = reader.ReadByte();

                queue.Enqueue(new LightQueueNode
                {
                    Position = new Vector3Int(x, y, z),
                    OldLightLevel = level
                });
            }
        }

        private static void WriteSection(BinaryWriter writer, ChunkSection section)
        {
            // 1. Write Section Version
            writer.Write(CURRENT_SECTION_VERSION);

            // 2. Write Data (Version 1 Format)
            writer.Write((ushort)section.nonAirCount);

            // Validate size
            if (section.voxels.Length != ChunkMath.SECTION_VOLUME)
            {
                throw new InvalidDataException($"Section voxel array corrupted. Size: {section.voxels.Length}");
            }

            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(section.voxels.AsSpan());
            writer.Write(bytes);
        }

        private static ChunkSection ReadSection(BinaryReader reader)
        {
            // Read Section Version
            byte version = reader.ReadByte();
            if (version == 1)
            {
                ChunkSection section = new ChunkSection();
                section.nonAirCount = reader.ReadUInt16();

                // Robust read for large data blocks
                Span<byte> bytes = MemoryMarshal.AsBytes(section.voxels.AsSpan());
                int totalBytesToRead = bytes.Length;
                int bytesReadTotal = 0;

                while (bytesReadTotal < totalBytesToRead)
                {
                    int bytesRead = reader.Read(bytes.Slice(bytesReadTotal));
                    if (bytesRead == 0)
                        throw new EndOfStreamException($"Section data truncated. Read {bytesReadTotal} of {totalBytesToRead} bytes.");

                    bytesReadTotal += bytesRead;
                }

                return section;
            }

            // Fallback / Error for unknown versions
            throw new InvalidDataException($"Unknown Section Version: {version}");
        }
    }
}
