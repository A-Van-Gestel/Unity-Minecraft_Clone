using System;
using System.Collections.Generic;
using System.IO;
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
        private const byte CURRENT_CHUNK_VERSION = 3;

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
            using MemoryStream memoryStream = new MemoryStream(outputBuffer);

            // Create wrapper. leaveOpen=true allows us to check position after flush.
            using (Stream compressionStream = CompressionFactory.CreateOutputStream(memoryStream, algorithm, leaveOpen: true))
            {
                // Use UTF8 and leaveOpen=true for the writer too.
                using (BinaryWriter writer = new BinaryWriter(compressionStream, Encoding.UTF8, true))
                {
                    WriteChunkInternal(writer, data);
                }
            }

            return (int)memoryStream.Position;
        }

        /// <summary>
        /// Deserializes a byte array into a ChunkData object based on the specified compression algorithm.
        /// </summary>
        /// <param name="data">The compressed raw byte span from the region file.</param>
        /// <param name="algorithm">The compression algorithm used on the data.</param>
        /// <param name="debugCoord">The expected coordinate of the chunk, used for sanity checks.</param>
        /// <returns>A populated <see cref="ChunkData"/> instance, or null if deserialization fails.</returns>
        public static ChunkData Deserialize(ReadOnlySpan<byte> data, CompressionAlgorithm algorithm, Vector2Int debugCoord)
        {
            if (data.Length == 0) return null;

            unsafe
            {
                fixed (byte* ptr = data)
                {
                    using UnmanagedMemoryStream unmanagedStream = new UnmanagedMemoryStream(ptr, data.Length);
                    Stream decompressionStream = null; // Helper to dispose if created

                    try
                    {
                        // Explicit leaveOpen=true because we manage the unmanagedStream lifecycle here
                        decompressionStream = CompressionFactory.CreateInputStream(unmanagedStream, algorithm, leaveOpen: true);

                        // Explicitly tell BinaryReader NOT to close the stream (leaveOpen: true).
                        // This makes the intent clear: "The finally block owns the stream disposal."
                        using BinaryReader reader = new BinaryReader(decompressionStream, Encoding.UTF8, leaveOpen: true);

                        return ReadChunkInternal(reader, debugCoord, data.Length);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Deserialize] Chunk {debugCoord} deserialization failed ({algorithm}). Payload: {data.Length} bytes. Error: {ex.GetType().Name} - {ex.Message}");
                        return null;
                    }
                    finally
                    {
                        // Clean up the wrapper.
                        // If it's 'None', it equals unmanagedStream (which is disposed by 'using' above), so we check equality.
                        if (decompressionStream != null && decompressionStream != unmanagedStream)
                        {
                            decompressionStream.Dispose();
                        }
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
            // Heightmap is fixed size (16*16 = 256 entries) -> 512 bytes
            ReadOnlySpan<byte> hmBytes = MemoryMarshal.AsBytes(data.heightMap.AsSpan());
            writer.Write(hmBytes);

            // --- Section Bitmask ---
            int sectionBitmask = 0;
            // Pre-allocate a safe local array for the background thread
            ChunkSection[] safeSections = new ChunkSection[data.sections.Length];

            for (int i = 0; i < data.sections.Length; i++)
            {
                // Snapshot the reference. If the main thread nullifies data.sections immediately after this line,
                // our local 'sec' variable still safely points  to the object in memory, preventing NREs.
                ChunkSection sec = data.sections[i];

                if (sec != null)
                {
                    sectionBitmask |= 1 << i;
                    safeSections[i] = sec;
                }
            }

            writer.Write(sectionBitmask);

            // --- Write Sections ---
            for (int i = 0; i < safeSections.Length; i++)
            {
                // Check bitmask to skip empty sections
                if ((sectionBitmask & (1 << i)) != 0)
                {
                    // Pass our safe reference to the writer
                    WriteSection(writer, safeSections[i]);
                }
            }

            // --- Write Lighting Queues ---
            // Lock queues during serialization to prevent concurrent modification by the Main Thread
            lock (data.SunlightBfsQueue) WriteLightQueue(writer, data.SunlightBfsQueue);
            lock (data.BlocklightBfsQueue) WriteLightQueue(writer, data.BlocklightBfsQueue);
        }

        // --- Internal Read Logic ---
        private static ChunkData ReadChunkInternal(BinaryReader reader, Vector2Int coord, int totalLen)
        {
            try
            {
                // --- Chunk Header ---
                // Safety check: The AOT Migration Manager handles historical versions offline.
                // The live game serializer strictly expects the current fully up-to-date version.
                byte version = reader.ReadByte();
                if (version != CURRENT_CHUNK_VERSION)
                    throw new InvalidDataException($"Unsupported Version: {version}. Expected: {CURRENT_CHUNK_VERSION}. World is either corrupt or bypassed AOT migration.");

                int x = reader.ReadInt32();
                int z = reader.ReadInt32();

                // Sanity check coordinates
                if (x != coord.x || z != coord.y)
                    Debug.LogWarning($"[ReadChunkInternal] Chunk coord mismatch at {coord}. Read: {x},{z}");

                ChunkData chunk = World.Instance.ChunkPool.GetChunkData(new Vector2Int(x, z)); // Get from POOL

                // --- State Flags ---
                chunk.NeedsInitialLighting = reader.ReadBoolean();

                // --- Height Map ---
                byte[] hmBytes = reader.ReadBytes(VoxelData.ChunkWidth * VoxelData.ChunkWidth * sizeof(ushort));
                if (hmBytes.Length != VoxelData.ChunkWidth * VoxelData.ChunkWidth * sizeof(ushort))
                    throw new EndOfStreamException("Heightmap truncated");
                Buffer.BlockCopy(hmBytes, 0, chunk.heightMap, 0, hmBytes.Length);

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
                Debug.LogError($"[ReadChunkInternal] Deserialize Crash at stream pos {curr}. Expected Payload Size: {totalLen}");
                throw;
            }
        }

        // --- Helpers for Lighting ---

        private static void WriteLightQueue(BinaryWriter writer, Queue<LightQueueNode> queue)
        {
            // Write Count
            writer.Write(queue.Count);

            // Write Items
            foreach (LightQueueNode node in queue)
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
            if (count < 0 || count > 100_000)
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
                    OldLightLevel = level,
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

            // Fast unsafe write
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(section.voxels.AsSpan());
            writer.Write(bytes);
        }

        private static ChunkSection ReadSection(BinaryReader reader)
        {
            // Read Section Version
            byte version = reader.ReadByte();
            if (version == 1)
            {
                ChunkSection section = World.Instance.ChunkPool.GetChunkSection(); // Get from POOL
                section.nonAirCount = reader.ReadUInt16();

                // Robust read for large data blocks
                Span<byte> bytes = MemoryMarshal.AsBytes(section.voxels.AsSpan());
                int totalBytesToRead = bytes.Length;
                int bytesReadTotal = 0;

                while (bytesReadTotal < totalBytesToRead)
                {
                    int bytesRead = reader.Read(bytes[bytesReadTotal..]);
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
