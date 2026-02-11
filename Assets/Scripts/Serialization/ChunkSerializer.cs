using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Data;
using UnityEngine;

namespace Serialization
{
    /// <summary>
    /// Handles the binary serialization and deserialization of ChunkData.
    /// Uses GZip compression and unsafe memory manipulation for maximum performance.
    /// </summary>
    public static class ChunkSerializer
    {
        private const byte CURRENT_CHUNK_VERSION = 1;

        // FUTURE-PROOFING: Version header for individual sections.
        // Allows upgrading section format (e.g. adding Palettes) without breaking the chunk header.
        private const byte CURRENT_SECTION_VERSION = 1;

        /// <summary>
        /// Serializes a ChunkData object into a compressed byte array buffer.
        /// </summary>
        /// <param name="data">The chunk data to serialize.</param>
        /// <param name="outputBuffer">The reusable buffer to write to.</param>
        /// <returns>The number of bytes written to the buffer (including the 4-byte length header).</returns>
        public static int Serialize(ChunkData data, byte[] outputBuffer)
        {
            // We write to a MemoryStream that wraps our pre-allocated outputBuffer
            using var memoryStream = new MemoryStream(outputBuffer);

            // Note: RegionFile handles the Compression Header now.
            // This stream produces raw Deflate data.
            using (var compressionStream = new DeflateStream(memoryStream, CompressionMode.Compress, true))
            using (var writer = new BinaryWriter(compressionStream))
            {
                // --- Chunk Header ---
                writer.Write(CURRENT_CHUNK_VERSION);
                writer.Write(data.position.x);
                writer.Write(data.position.y); // Z coordinate (Vector2Int.y)

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
                        // Pass version explicitly to the writer
                        WriteSection(writer, data.sections[i]);
                    }
                }

                // Write Lighting Queues ---
                // We access the raw queues from ChunkData. 
                WriteLightQueue(writer, data.SunlightBfsQueue);
                WriteLightQueue(writer, data.BlocklightBfsQueue);
            }

            return (int)memoryStream.Position;
        }

        /// <summary>
        /// Deserializes a compressed byte array into a ChunkData object.
        /// </summary>
        public static ChunkData Deserialize(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return null;

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    using var unmanagedStream = new UnmanagedMemoryStream(ptr, data.Length);
                    using var compressionStream = new DeflateStream(unmanagedStream, CompressionMode.Decompress);
                    using var reader = new BinaryReader(compressionStream);

                    try
                    {
                        byte version = reader.ReadByte();

                        // Safety check: Don't try to load chunks from the future.
                        if (version > CURRENT_CHUNK_VERSION)
                        {
                            Debug.LogError($"Chunk version {version} not supported.");
                            return null;
                        }

                        int x = reader.ReadInt32();
                        int z = reader.ReadInt32();

                        ChunkData chunk = new ChunkData(x, z);

                        // --- Height Map ---
                        chunk.heightMap = reader.ReadBytes(VoxelData.ChunkWidth * VoxelData.ChunkWidth);

                        // --- Sections ---
                        int sectionBitmask = reader.ReadInt32();
                        for (int i = 0; i < chunk.sections.Length; i++)
                        {
                            if ((sectionBitmask & (1 << i)) != 0)
                            {
                                chunk.sections[i] = ReadSection(reader);

                                // --- Read Lighting Queues ---
                                ReadLightQueue(reader, chunk.SunlightBfsQueue);
                                ReadLightQueue(reader, chunk.SunlightBfsQueue);
                        
                                // If we loaded pending lights, flag the chunk for processing
                                if (chunk.SunLightQueueCount > 0 || chunk.BlockLightQueueCount > 0)   
                                {
                                    chunk.HasLightChangesToProcess = true;
                                }
                            }
                        }

                        chunk.IsPopulated = true;
                        return chunk;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Chunk deserialization exception: {ex.Message}");
                        return null;
                    }
                }
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
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(section.voxels.AsSpan());
            writer.Write(bytes);
        }

        private static ChunkSection ReadSection(BinaryReader reader)
        {
            // 1. Read Section Version
            byte version = reader.ReadByte();

            ChunkSection section = new ChunkSection();

            // 2. Read Data based on Version
            if (version == 1)
            {
                section.nonAirCount = reader.ReadUInt16();

                Span<byte> bytes = MemoryMarshal.AsBytes(section.voxels.AsSpan());
                int bytesRead = reader.Read(bytes);

                if (bytesRead != bytes.Length)
                    throw new EndOfStreamException("Incomplete section data");
            }
            else
            {
                // Fallback / Error for unknown versions
                throw new InvalidDataException($"Unknown Section Version: {version}");
            }

            return section;
        }
    }
}
