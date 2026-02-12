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

        // Compression Types (Minecraft Parity)
        private const byte COMPRESSION_GZIP = 1; // We treat our DeflateStream as Type 1
        private const byte COMPRESSION_ZLIB = 2;
        private const byte COMPRESSION_NONE = 3;
        private const byte COMPRESSION_LZ4 = 4;

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

        public byte[] LoadChunkData(int localX, int localZ)
        {
            // CRITICAL FIX: Exclusive lock. 
            // FileStream.Seek changes the position for the whole instance.
            // Multiple readers cannot share the stream simultaneously.
            lock (_fileLock)
            {
                try
                {
                    int index = localX + localZ * CHUNKS_PER_SIDE;
                    int offsetData = _offsets[index];
                    if (offsetData == 0) return null;

                    int sectorOffset = (offsetData >> 8) & 0xFFFFFF;
                    long filePosition = (long)sectorOffset * SECTOR_SIZE;

                    if (filePosition >= _fileStream.Length) return null;

                    _fileStream.Seek(filePosition, SeekOrigin.Begin);

                    // 1. Read Length (4 bytes)
                    byte[] lengthBytes = new byte[4];
                    int headerBytesRead = _fileStream.Read(lengthBytes, 0, 4);
                    if (headerBytesRead < 4) return null;

                    // Convert Big Endian (if standard) or Little Endian. 
                    // BinaryWriter matches BitConverter.ToInt32 on the same system.
                    int length = BitConverter.ToInt32(lengthBytes, 0);

                    if (length <= 1 || length > 16 * 1024 * 1024)
                    {
                        Debug.LogWarning($"RegionFile corrupt: Invalid length {length} at {localX},{localZ}");
                        return null;
                    }

                    // 2. Read Compression Type (1 byte)
                    int compressionType = _fileStream.ReadByte();

                    if (compressionType != COMPRESSION_GZIP)
                    {
                        Debug.LogError($"RegionFile: Unsupported compression type {compressionType} at {localX},{localZ}");
                        return null;
                    }

                    // 3. Read Payload
                    int payloadLength = length - 1;
                    byte[] data = new byte[payloadLength];
                    int payloadBytesRead = _fileStream.Read(data, 0, payloadLength);

                    if (payloadBytesRead < payloadLength) return null;

                    return data;
                }
                catch (Exception e)
                {
                    Debug.LogError($"RegionFile IO Error: {e.Message}");
                    return null;
                }
            }
        }

        public void SaveChunkData(int localX, int localZ, byte[] data, int payloadLength)
        {
            lock (_fileLock)
            {
                try
                {
                    int index = localX + localZ * CHUNKS_PER_SIDE;

                    // Total stored size = Payload + 1 byte (CompressionType)
                    int totalLength = payloadLength + 1;

                    // Sectors needed = (Length Header 4B + Content) / 4096
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

                    // 5. Write Data
                    _fileStream.Write(BitConverter.GetBytes(totalLength), 0, 4);
                    _fileStream.WriteByte(COMPRESSION_GZIP);
                    _fileStream.Write(data, 0, payloadLength);

                    // 6. Padding (Optional but good for cleanliness)
                    // If the data doesn't fill the sector(s), we should probably zero it out or just leave it.
                    // Strictly speaking, not required as the Length header prevents reading garbage,
                    // but writing zeros is safer if we ever use a tool that expects clean sectors.
                    // We calculate actual written bytes: 4 + 1 + payloadLength
                    // Remainder to fill sectors:

                    long writtenBytes = 4 + 1 + payloadLength;
                    long sectorBytes = sectorsNeeded * SECTOR_SIZE;
                    long padding = sectorBytes - writtenBytes;
                    if (padding > 0)
                    {
                        // Allocate a small zero array or reuse one
                        byte[] pad = new byte[padding];
                        _fileStream.Write(pad, 0, pad.Length);
                    }


                    // 7. Update Header
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

        // Add a method to perform raw byte replacement without full deserialization overhead
        public void RawWriteChunk(int localX, int localZ, byte[] data)
        {
            SaveChunkData(localX, localZ, data, data.Length);
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

        public void Dispose()
        {
            lock (_fileLock)
            {
                _fileStream?.Dispose();
            }
        }
    }
}
