using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Serialization
{
    public class RegionFile : IDisposable
    {
        // 4KB sectors
        private const int SECTOR_SIZE = 4096;
        private const int CHUNKS_PER_SIDE = 32;
        private const int TOTAL_CHUNKS = CHUNKS_PER_SIDE * CHUNKS_PER_SIDE; // 1024

        private readonly string _filePath;
        private FileStream _fileStream;

        // Location Table: 1024 entries.
        // Each entry: 3 bytes offset (in sectors), 1 byte size (in sectors).
        private readonly int[] _offsets = new int[TOTAL_CHUNKS];

        // TODO: This fileLock works correctly to prevent save data corruption, but adds massive overhead and possible loading / saving slowdowns, I believe this could be reworked into a more performant, but still thread save system. (eg: section specific lock or something like that?)
        // Thread Safety: FileStream position is not thread-safe, so we need exclusive locking for BOTH reads and writes.
        private readonly object _fileLock = new object();

        // Fragmentation Management: Simple boolean map of used sectors
        private readonly List<bool> _sectorUsage = new List<bool>();

        public RegionFile(string path)
        {
            _filePath = path;
            Initialize();
        }

        private void Initialize()
        {
            // Lock during initialization to be safe, though usually called once.
            lock (_fileLock)
            {
                bool exists = File.Exists(_filePath);
                _fileStream = new FileStream(_filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

                if (!exists || _fileStream.Length < SECTOR_SIZE * 2)
                {
                    _fileStream.SetLength(SECTOR_SIZE * 2);
                    _sectorUsage.Add(true); // Header 1
                    _sectorUsage.Add(true); // Header 2
                }
                else
                {
                    int totalSectors = (int)(_fileStream.Length / SECTOR_SIZE);
                    for (int i = 0; i < totalSectors; i++) _sectorUsage.Add(false);
                    _sectorUsage[0] = true;
                    _sectorUsage[1] = true;

                    // Read Header
                    _fileStream.Seek(0, SeekOrigin.Begin);
                    using var reader = new BinaryReader(_fileStream, Encoding.Default, true);

                    // Read Offsets
                    for (int i = 0; i < TOTAL_CHUNKS; i++)
                    {
                        int offset = reader.ReadInt32();
                        _offsets[i] = offset;

                        // Mark used sectors
                        if (offset != 0)
                        {
                            int sectorStart = (offset >> 8) & 0xFFFFFF;
                            int sectorCount = offset & 0xFF;

                            // Robustness: ensure we don't crash on corrupted offsets table
                            while (_sectorUsage.Count <= sectorStart + sectorCount) _sectorUsage.Add(false);

                            for (int k = 0; k < sectorCount; k++)
                                if (sectorStart + k < _sectorUsage.Count)
                                    _sectorUsage[sectorStart + k] = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Reads and extracts compressed chunk data from the region file.
        /// </summary>
        /// <param name="localX">The chunk's local X coordinate index within the region (0-31).</param>
        /// <param name="localZ">The chunk's local Z coordinate index within the region (0-31).</param>
        /// <returns>A tuple containing the raw compressed byte payload and the specific <see cref="CompressionAlgorithm"/> used.</returns>
        /// <example>Chunk Index (33, 33) maps to Region (1, 1) and Local (1, 1). Passed params: <c>localX = 1</c>, <c>localZ = 1</c>.</example>
        public (byte[] data, CompressionAlgorithm algorithm) LoadChunkData(int localX, int localZ)
        {
            // CRITICAL: Exclusive lock.
            // FileStream.Seek changes the position for the whole instance.
            // Multiple readers cannot share the stream simultaneously.
            lock (_fileLock)
            {
                try
                {
                    int index = localX + localZ * CHUNKS_PER_SIDE;
                    int offsetData = _offsets[index];
                    if (offsetData == 0) return (null, CompressionAlgorithm.GZip);

                    int sectorOffset = (offsetData >> 8) & 0xFFFFFF;
                    long filePosition = (long)sectorOffset * SECTOR_SIZE;

                    if (filePosition >= _fileStream.Length)
                    {
                        // File truncated or corrupt header
                        return (null, CompressionAlgorithm.GZip);
                    }

                    _fileStream.Seek(filePosition, SeekOrigin.Begin);

                    // 1. Read Length (4 bytes)
                    byte[] lengthBytes = new byte[4];
                    int headerBytesRead = _fileStream.Read(lengthBytes, 0, 4);
                    if (headerBytesRead < 4) return (null, CompressionAlgorithm.GZip);

                    // Convert Big Endian (if standard) or Little Endian.
                    // BinaryWriter matches BitConverter.ToInt32 on the same system.
                    int length = BitConverter.ToInt32(lengthBytes, 0);

                    // Sanity check length (Max 16MB)
                    if (length <= 1 || length > 16 * 1024 * 1024)
                    {
                        Debug.LogWarning($"RegionFile corrupt: Invalid length {length} at {localX},{localZ}");
                        return (null, CompressionAlgorithm.GZip);
                    }

                    // 2. Read Compression Type (1 byte)
                    int compressionByte = _fileStream.ReadByte();

                    // Map byte to Enum
                    CompressionAlgorithm algo = (CompressionAlgorithm)compressionByte;

                    // Basic validation of supported types
                    if (!Enum.IsDefined(typeof(CompressionAlgorithm), algo))
                    {
                        Debug.LogError($"RegionFile: Unsupported compression type {compressionByte} at {localX},{localZ}");
                        return (null, CompressionAlgorithm.GZip);
                    }

                    // 3. Read Payload
                    int payloadLength = length - 1;
                    byte[] data = new byte[payloadLength];
                    int payloadBytesRead = _fileStream.Read(data, 0, payloadLength);

                    if (payloadBytesRead < payloadLength) return (null, CompressionAlgorithm.GZip);

                    return (data, algo);
                }
                catch (Exception e)
                {
                    Debug.LogError($"RegionFile IO Error: {e.Message}");
                    return (null, CompressionAlgorithm.GZip);
                }
            }
        }

        /// <summary>
        /// Writes compressed chunk data into the region file. Manages internal sector fragmentation
        /// to allocate contiguous free blocks when chunk sizes increase.
        /// </summary>
        /// <param name="localX">The chunk's local X coordinate index within the region (0-31).</param>
        /// <param name="localZ">The chunk's local Z coordinate index within the region (0-31).</param>
        /// <param name="data">The compressed binary payload.</param>
        /// <param name="payloadLength">The exact length of the payload array to write.</param>
        /// <param name="algorithm">The compression algorithm code to record in the region header.</param>
        public void SaveChunkData(int localX, int localZ, byte[] data, int payloadLength, CompressionAlgorithm algorithm)
        {
            lock (_fileLock)
            {
                try
                {
                    int index = localX + localZ * CHUNKS_PER_SIDE;
                    int totalLength = payloadLength + 1; // +1 for compression byte
                    int sectorsNeeded = (totalLength + 4 + SECTOR_SIZE - 1) / SECTOR_SIZE;

                    if (sectorsNeeded > 255)
                    {
                        Debug.LogError($"Chunk {localX}, {localZ} is too big to save ({totalLength} bytes)");
                        return;
                    }

                    int oldOffsetData = _offsets[index];
                    int oldSectorStart = (oldOffsetData >> 8) & 0xFFFFFF;
                    int oldSectorCount = oldOffsetData & 0xFF;

                    int writeSectorStart;

                    // 1. Can we overwrite?
                    if (oldSectorStart != 0 && oldSectorCount == sectorsNeeded)
                    {
                        writeSectorStart = oldSectorStart;
                    }
                    else
                    {
                        // 2. Free old
                        if (oldSectorStart != 0)
                        {
                            for (int k = 0; k < oldSectorCount; k++)
                                if (oldSectorStart + k < _sectorUsage.Count)
                                    _sectorUsage[oldSectorStart + k] = false;
                        }

                        // 3. Find first contiguous sequence of free sectors
                        writeSectorStart = FindFreeSectors(sectorsNeeded);
                    }

                    // 4. Ensure size
                    long requiredLength = (long)(writeSectorStart + sectorsNeeded) * SECTOR_SIZE;
                    if (_fileStream.Length < requiredLength)
                        _fileStream.SetLength(requiredLength);

                    _fileStream.Seek((long)writeSectorStart * SECTOR_SIZE, SeekOrigin.Begin);

                    // 5. Write Header + Compression Byte + Data
                    _fileStream.Write(BitConverter.GetBytes(totalLength), 0, 4);
                    _fileStream.WriteByte((byte)algorithm);
                    _fileStream.Write(data, 0, payloadLength);

                    // 6. Padding (for clean sectors)
                    long writtenBytes = 4 + 1 + payloadLength;
                    long sectorBytes = sectorsNeeded * SECTOR_SIZE;
                    long padding = sectorBytes - writtenBytes;
                    if (padding > 0)
                    {
                        // Allocate a small zero array
                        byte[] pad = new byte[padding];
                        _fileStream.Write(pad, 0, pad.Length);
                    }

                    // 7. Update Offsets
                    int newOffsetData = (writeSectorStart << 8) | (sectorsNeeded & 0xFF);
                    _offsets[index] = newOffsetData;

                    _fileStream.Seek(index * 4, SeekOrigin.Begin);
                    _fileStream.Write(BitConverter.GetBytes(newOffsetData), 0, 4);
                }
                catch (Exception e)
                {
                    Debug.LogError($"RegionFile Write Error: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Iterates the location table and yields the local coordinates of every chunk that actually exists inside this region.
        /// </summary>
        /// <returns>An enumerable of <see cref="Vector2Int"/> local chunk coordinates (0-31 bounds).</returns>
        public IEnumerable<Vector2Int> GetAllChunkCoords()
        {
            // Reading this array is thread-safe as it's a fixed size int array,
            // but the values might update during iteration.
            // Since this is usually used for migration (offline), it's acceptable.
            for (int i = 0; i < TOTAL_CHUNKS; i++)
            {
                if (_offsets[i] != 0)
                {
                    int localX = i % CHUNKS_PER_SIDE;
                    int localZ = i / CHUNKS_PER_SIDE;
                    yield return new Vector2Int(localX, localZ);
                }
            }
        }

        /// <summary>
        /// Safely extracts the local coordinates and compression type for all chunks in this region.
        /// </summary>
        public List<(Vector2Int localCoord, CompressionAlgorithm algorithm)> GetAllChunkMetadata()
        {
            var result = new List<(Vector2Int, CompressionAlgorithm)>();

            // We must lock the file stream while reading to prevent threading collisions
            lock (_fileLock)
            {
                for (int i = 0; i < TOTAL_CHUNKS; i++)
                {
                    int offsetData = _offsets[i];
                    if (offsetData != 0)
                    {
                        int localX = i % CHUNKS_PER_SIDE;
                        int localZ = i / CHUNKS_PER_SIDE;

                        int sectorOffset = (offsetData >> 8) & 0xFFFFFF;

                        // Sector start * 4096 + 4 bytes for the payload length header = The 1-byte compression flag
                        long compBytePos = (long)sectorOffset * SECTOR_SIZE + 4;
                        CompressionAlgorithm algo = CompressionAlgorithm.None;

                        if (compBytePos < _fileStream.Length)
                        {
                            _fileStream.Seek(compBytePos, SeekOrigin.Begin);
                            byte compByte = (byte)_fileStream.ReadByte();

                            if (Enum.IsDefined(typeof(CompressionAlgorithm), (CompressionAlgorithm)compByte))
                            {
                                algo = (CompressionAlgorithm)compByte;
                            }
                        }

                        result.Add((new Vector2Int(localX, localZ), algo));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Performs a raw byte replacement without full deserialization overhead.
        /// Used primarily in offline migration tools.
        /// </summary>
        /// <param name="localX">The chunk's local X coordinate index within the region (0-31).</param>
        /// <param name="localZ">The chunk's local Z coordinate index within the region (0-31).</param>
        /// <param name="data">The compressed binary payload.</param>
        /// <param name="algorithm">The compression algorithm flag, defaulting to GZip for legacy formats.</param>
        public void RawWriteChunk(int localX, int localZ, byte[] data, CompressionAlgorithm algorithm = CompressionAlgorithm.GZip)
        {
            // For migration/raw writes, we assume GZip if not specified, or we could overload this.
            // Keeping GZip as default for legacy compatibility in migrations.
            SaveChunkData(localX, localZ, data, data.Length, algorithm);
        }

        private int FindFreeSectors(int count)
        {
            int consecutive = 0;
            for (int i = 2; i < _sectorUsage.Count; i++) // Skip headers
            {
                if (!_sectorUsage[i])
                {
                    consecutive++;
                    if (consecutive == count)
                    {
                        int start = i - count + 1;
                        for (int k = 0; k < count; k++) _sectorUsage[start + k] = true;
                        return start;
                    }
                }
                else
                {
                    consecutive = 0;
                }
            }

            // Append to end
            int appendStart = _sectorUsage.Count;
            for (int k = 0; k < count; k++) _sectorUsage.Add(true);
            return appendStart;
        }

        /// <summary>
        /// Flushes any buffered stream data directly to the physical disk and releases file handles.
        /// Preventing corruption on abrupt process exits.
        /// </summary>
        public void Dispose()
        {
            lock (_fileLock)
            {
                if (_fileStream != null)
                {
                    try
                    {
                        // Force all buffered data to disk BEFORE closing
                        _fileStream.Flush(flushToDisk: true); // Force write to disk
                        _fileStream.Dispose(); // Close handle
                        _fileStream = null;

                        Debug.Log($"[RegionFile] Flushed and closed: {Path.GetFileName(_filePath)}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[RegionFile] Error disposing {_filePath}: {ex.Message}");
                    }
                }
            }
        }
    }
}
