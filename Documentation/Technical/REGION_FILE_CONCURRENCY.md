# Architectural Analysis: Region File Thread Safety & Concurrency

## Background
Currently, `RegionFile.cs` handles all read and write operations inside a global `lock (_fileLock)` block. Since operations on `FileStream` (like `Seek()`, `Read()`, `Write()`) are stateful, executing them concurrently without a lock would lead to race conditions where the internal file position cursor is moved unexpectedly by one thread while another thread is reading/writing, resulting in save data corruption. While safe, this single lock causes a severe bottleneck for I/O operations, especially as chunk saving and loading scale up.

## Feasibility of a Multi-Lock System
A multi-lock or sector-specific locking system is highly feasible but requires careful architectural adjustments to separate stateful operations (like file pointer positioning) from thread execution contexts.

### The Core Challenge
The root of the concurrency issue isn't just about overwriting data; it's about the `FileStream`'s internal pointer (`Position`). Two threads writing to completely different sectors cannot share the same `FileStream` instance safely without external synchronization if they both rely on calling `Seek()`. 

## Proposed Implementation Strategies

### 1. FileStream Pooling / Thread-Local Streams (Recommended for Reads)
Instead of a single `FileStream` protected by a lock, we can maintain a pool of `FileStream` instances (or create them per-thread) all pointing to the same file with `FileShare.ReadWrite`.
- **Reads (`LoadChunkData`)**: Can occur completely concurrently. A thread grabs an available `FileStream` from the pool, `Seek`s to the required position, reads the data, and returns the stream to the pool. Since each stream has its own independent `Position` cursor, no thread blocking occurs for concurrent chunk loads.
- **Why this is safe:** Read operations don't alter the file size or the data. Independent stream objects manage their own file pointers.

### 2. Sector-Specific / Chunk-Specific Locking (For Writes)
If we want to allow concurrent writes to different parts of the region file, we need granular locking.
- We can maintain an array of `lock` objects, for example, a lock per chunk index (1024 locks), or a `ConcurrentDictionary<int, object>` for active write locks.
- **The Catch:** Since writing often requires modifying the global `_offsets` table (Location Table) and the `_sectorUsage` map (Fragmentation Management), the threads *must still synchronize* when updating these global metadata structures.

### 3. The Hybrid Approach: Concurrent Reads + Queued/Serialized Writes (Best Balance)
Given that writes involve metadata updates (Sector map, offsets table) and potentially expanding the file's length, fully concurrent writes are highly complex and prone to edge-case corruption (e.g., two chunks requesting free sectors simultaneously).
**The most robust solution:**
- **Reads:** Fully concurrent using a pool of `FileStream` objects (or `FileOptions.Asynchronous` with `RandomAccess` in newer .NET versions).
- **Writes:** Maintained on a single background I/O thread, or kept under a single lock `lock (_writeLock)`. Write operations in voxel games are usually queued and processed asynchronously anyway. By decoupling Reads from the Write lock, loading chunks (which directly blocks gameplay/meshing) will never be stalled by saving chunks.

### 4. Utilizing .NET `RandomAccess`
We are using Unity 6 with the modern .NET future set, so it is entirely possible that we have access to `System.IO.RandomAccess`. This API allows thread-safe, concurrent reads and writes to a file handle using entirely stateless offset-based operations without moving a shared `FileStream.Position` cursor.
- `RandomAccess.Read(SafeFileHandle, Span<byte>, long fileOffset)`
- `RandomAccess.Write(SafeFileHandle, ReadOnlySpan<byte>, long fileOffset)`
If `RandomAccess` is available, this eliminates the need for multiple `FileStream` instances entirely. You would still need locks for modifying the `_sectorUsage` and `_offsets` arrays during writes, but the actual disk I/O could be fully concurrent.

## Critical Requirements to Prevent Data Corruption
If moving to a multi-lock/concurrent system, the following MUST be guaranteed:
1. **Metadata Synchronization:** Finding free sectors (`_sectorUsage`) and updating the location table (`_offsets`) MUST remain under an exclusive lock so two chunks aren't written to the same sectors.
2. **File Resizing:** `_fileStream.SetLength()` must be thread-safe. Other threads shouldn't attempt to read/write while the file is being resized.
3. **Atomic Writes:** If a crash occurs mid-write, the region file shouldn't be corrupted. The offset table should ideally be updated *after* the chunk data is successfully written to disk.

## Conclusion & Next Steps
The most performant and safest immediate step is to **split the Read and Write locks**, employing `FileStream` pooling for concurrent reads, while keeping writes serialized. This tackles the biggest performance bottleneck (loading stalls) while avoiding the immense complexity and corruption risks of fully concurrent writes.
