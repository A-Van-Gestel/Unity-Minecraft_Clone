using System;
using System.IO;
using Serialization;
using UnityEngine;

namespace Editor.Validation.SerializationRoundTrip
{
    /// <summary>
    /// Part 4 of the suite (roadmap <c>NS-1</c>): <see cref="RegionFile"/> sector mechanics. The region layer
    /// is the allocator underneath every save — it hands out 4 KB sector runs, relocates a record that no
    /// longer fits, frees what it vacates, and records all of it in a 1024-entry offset table. A fault here
    /// does not corrupt one chunk: it points a chunk's table entry at sectors another chunk now owns.
    /// <para>The structural assertions parse the offset table straight off disk rather than asking
    /// <see cref="RegionFile"/> where things went — the allocator cannot be its own oracle.</para>
    /// <para><b>Flush discipline (load-bearing for every scenario here).</b> <see cref="RegionFile"/> writes
    /// through a buffered <see cref="FileStream"/> that is only flushed by <see cref="RegionFile.Dispose"/>,
    /// so a second handle opened mid-session reads STALE bytes — an unwritten table entry reads as zero.
    /// Every on-disk inspection below therefore happens after the region file is disposed; a scenario that
    /// inspects the table while the writer is still open passes vacuously (zeros look like "no record" and
    /// "no overlap"). Scenarios that need to observe allocation between writes do so in phases: open, write,
    /// dispose, inspect, reopen — which also exercises the free-map rebuild in <c>InitializeCore</c>.</para>
    /// </summary>
    public static partial class SerializationRoundTripValidationSuite
    {
        /// <summary>Region-file sector size (mirrors <c>RegionFile.SECTOR_SIZE</c>, which is private).</summary>
        private const int REGION_SECTOR_SIZE = 4096;

        /// <summary>Sectors reserved for the location table before any record can be placed.</summary>
        private const int REGION_HEADER_SECTORS = 2;

        /// <summary>Chunks per region side; the offset table is <c>32 × 32</c> entries.</summary>
        private const int REGION_CHUNKS_PER_SIDE = 32;

        /// <summary>Per-record overhead inside a sector run: a 4-byte length plus the 1-byte algorithm code.</summary>
        private const int REGION_RECORD_OVERHEAD = 5;

        // --- Scenarios ---------------------------------------------------------------------------

        /// <summary>
        /// B9. The allocator's baseline contract. Red when: a written record cannot be read back, an unwritten
        /// slot stops reporting "absent", the algorithm code is not preserved per record, or the offset table
        /// entry does not describe a plausible run (inside the file, past the header sectors, sized for the
        /// payload).
        /// </summary>
        /// <returns>True when a single record round-trips and its table entry is well-formed.</returns>
        private static bool RegionFileStoresAndDescribesARecord()
        {
            using Fixture fx = new Fixture();
            string path = RegionPath(fx, "b9");

            byte[] payload = MakePayload(9000, seed: 1);
            bool ok;
            using (RegionFile region = new RegionFile(path))
            {
                ok = Check("an unwritten slot reads as absent", region.LoadChunkData(5, 5).data == null);

                region.SaveChunkData(1, 2, payload, payload.Length, CompressionAlgorithm.LZ4);
                (byte[] read, CompressionAlgorithm algo) = region.LoadChunkData(1, 2);

                ok &= Check("the record reads back byte-identical", PayloadsEqual(payload, read));
                ok &= Check($"the record's algorithm code survives (expected LZ4, got {algo.ToString()})",
                    algo == CompressionAlgorithm.LZ4);
            }

            // Post-dispose: the table is now actually on disk.
            (int start, int count) = ReadTableEntry(path, 1, 2);
            int expectedSectors = SectorsFor(payload.Length);
            ok &= Check($"the table entry is past the header (start {start.ToString()})", start >= REGION_HEADER_SECTORS);
            ok &= Check($"the table entry is sized for the payload (expected {expectedSectors.ToString()} sectors, got {count.ToString()})",
                count == expectedSectors);
            ok &= Check("the allocated run lies inside the file",
                (long)(start + count) * REGION_SECTOR_SIZE <= new FileInfo(path).Length);
            return ok;
        }

        /// <summary>
        /// B10. Growth and relocation. A record that outgrows its run must move, and its old sectors must be
        /// released — not stranded. Red when: the rewrite is not relocated (it would overwrite whatever sits
        /// after it), the table still points at the old run, or the vacated sectors are never reused.
        /// </summary>
        /// <returns>True when growth relocates, updates the table, and frees the old run for reuse.</returns>
        private static bool GrowingARecordRelocatesAndFreesItsOldRun()
        {
            using Fixture fx = new Fixture();
            string path = RegionPath(fx, "b10");

            byte[] small = MakePayload(2000, seed: 2);   // 1 sector
            byte[] large = MakePayload(20000, seed: 3);  // 5 sectors
            byte[] filler = MakePayload(2000, seed: 4);  // 1 sector — should land in the freed slot

            using (RegionFile region = new RegionFile(path))
            {
                region.SaveChunkData(0, 0, small, small.Length, CompressionAlgorithm.None);
            }

            (int oldStart, int oldCount) = ReadTableEntry(path, 0, 0);
            bool ok = Check($"the initial record occupies one sector at {oldStart.ToString()}",
                oldStart >= REGION_HEADER_SECTORS && oldCount == SectorsFor(small.Length));

            using (RegionFile region = new RegionFile(path))
            {
                region.SaveChunkData(0, 0, large, large.Length, CompressionAlgorithm.None);
                ok &= Check("the grown record reads back byte-identical",
                    PayloadsEqual(large, region.LoadChunkData(0, 0).data));

                // The vacated run must be free: the next same-sized write belongs there, not at the end.
                region.SaveChunkData(7, 7, filler, filler.Length, CompressionAlgorithm.None);
                ok &= Check("both records read back correctly in-session after the reuse",
                    PayloadsEqual(large, region.LoadChunkData(0, 0).data) &&
                    PayloadsEqual(filler, region.LoadChunkData(7, 7).data));
            }

            (int newStart, int newCount) = ReadTableEntry(path, 0, 0);
            (int fillerStart, _) = ReadTableEntry(path, 7, 7);

            ok &= Check($"a grown record is relocated (was sector {oldStart.ToString()}×{oldCount.ToString()}, now {newStart.ToString()}×{newCount.ToString()})",
                newStart != oldStart && newCount == SectorsFor(large.Length));
            ok &= Check($"the vacated sector is reused by the next write (freed {oldStart.ToString()}, filler landed at {fillerStart.ToString()})",
                fillerStart == oldStart);
            ok &= Check("no two records share a sector after the relocation", NoOverlappingRuns(path));
            return ok;
        }

        /// <summary>
        /// B11. Shrinking. A record that no longer needs its whole run must release the tail, and the file must
        /// not keep growing when free space exists. Red when: shrinking strands the tail sectors (the file
        /// grows on every subsequent write even though space was freed).
        /// </summary>
        /// <returns>True when a shrunk record's tail sectors are reused without extending the file.</returns>
        private static bool ShrinkingARecordReleasesItsTailSectors()
        {
            using Fixture fx = new Fixture();
            string path = RegionPath(fx, "b11");

            byte[] large = MakePayload(20000, seed: 5); // 5 sectors
            byte[] small = MakePayload(2000, seed: 6);  // 1 sector
            byte[] refill = MakePayload(9000, seed: 7); // 3 sectors — must fit in the freed tail

            using (RegionFile region = new RegionFile(path))
            {
                region.SaveChunkData(3, 3, large, large.Length, CompressionAlgorithm.None);
            }

            long lengthAfterLarge = new FileInfo(path).Length;

            bool ok;
            using (RegionFile region = new RegionFile(path))
            {
                region.SaveChunkData(3, 3, small, small.Length, CompressionAlgorithm.None);
                region.SaveChunkData(4, 4, refill, refill.Length, CompressionAlgorithm.None);
                ok = Check("both records read back in-session after the shrink",
                    PayloadsEqual(small, region.LoadChunkData(3, 3).data) &&
                    PayloadsEqual(refill, region.LoadChunkData(4, 4).data));
            }

            (int start, int count) = ReadTableEntry(path, 3, 3);
            (int refillStart, int refillCount) = ReadTableEntry(path, 4, 4);

            ok &= Check($"the shrunk record's run is resized (expected {SectorsFor(small.Length).ToString()} sectors, got {count.ToString()})",
                count == SectorsFor(small.Length));
            ok &= Check($"the refill landed inside the freed tail (shrunk run is {start.ToString()}×{count.ToString()}, refill at {refillStart.ToString()}×{refillCount.ToString()})",
                refillStart >= start + count && refillStart < start + SectorsFor(large.Length));
            ok &= Check($"the freed tail absorbs the next write without extending the file ({lengthAfterLarge.ToString()} → {new FileInfo(path).Length.ToString()} bytes)",
                new FileInfo(path).Length <= lengthAfterLarge);
            ok &= Check("no two records share a sector after the shrink", NoOverlappingRuns(path));

            using (RegionFile reopened = new RegionFile(path))
            {
                ok &= Check("both records survive the shrink and reuse",
                    PayloadsEqual(small, reopened.LoadChunkData(3, 3).data) &&
                    PayloadsEqual(refill, reopened.LoadChunkData(4, 4).data));
            }

            return ok;
        }

        /// <summary>Slots rewritten by the mixed-size storm, and how many rewrite rounds it runs.</summary>
        private const int STORM_SLOT_COUNT = 12, STORM_ROUNDS = 6;

        /// <summary>
        /// B12. The integrity property that matters most: after an adversarial sequence of mixed-size rewrites
        /// across many slots — the shape that exercises relocation, freeing and reuse against each other —
        /// EVERY slot must still return exactly its last written bytes, and no two table entries may claim the
        /// same sector. Red when: an allocation hands out a run that another record still owns (the corruption
        /// this layer's faults actually produce).
        /// </summary>
        /// <returns>True when every slot's final payload survives the storm and all runs are disjoint.</returns>
        private static bool MixedSizeRewriteStormPreservesEveryRecord()
        {
            using Fixture fx = new Fixture();
            string path = RegionPath(fx, "b12");

            System.Random rng = new System.Random(0x9E5104);
            byte[][] expected = new byte[STORM_SLOT_COUNT][];
            bool ok = true;

            using (RegionFile region = new RegionFile(path))
            {
                for (int round = 0; round < STORM_ROUNDS; round++)
                {
                    for (int slot = 0; slot < STORM_SLOT_COUNT; slot++)
                    {
                        // Sizes deliberately straddle sector boundaries in both directions each round.
                        int size = rng.Next(1, 7) * REGION_SECTOR_SIZE - rng.Next(0, 400);
                        byte[] payload = MakePayload(size, rng.Next());
                        expected[slot] = payload;
                        region.SaveChunkData(StormX(slot), StormZ(slot), payload, payload.Length, CompressionAlgorithm.None);
                    }
                }

                for (int slot = 0; slot < STORM_SLOT_COUNT; slot++)
                {
                    (byte[] read, _) = region.LoadChunkData(StormX(slot), StormZ(slot));
                    ok &= Check($"slot {slot.ToString()} still holds its last written payload ({expected[slot].Length.ToString()} bytes)",
                        PayloadsEqual(expected[slot], read));
                }
            }

            // Post-dispose, against the flushed table: structural disjointness and durability of the result.
            ok &= Check("no two records share a sector after the storm", NoOverlappingRuns(path));

            using (RegionFile reopened = new RegionFile(path))
            {
                for (int slot = 0; slot < STORM_SLOT_COUNT; slot++)
                {
                    (byte[] read, _) = reopened.LoadChunkData(StormX(slot), StormZ(slot));
                    ok &= Check($"slot {slot.ToString()} survives the reopen", PayloadsEqual(expected[slot], read));
                }
            }

            return ok;
        }

        /// <summary>Local X of a storm slot.</summary>
        /// <param name="slot">The storm slot index.</param>
        /// <returns>The local chunk X.</returns>
        private static int StormX(int slot) => slot % REGION_CHUNKS_PER_SIDE;

        /// <summary>Local Z of a storm slot.</summary>
        /// <param name="slot">The storm slot index.</param>
        /// <returns>The local chunk Z.</returns>
        private static int StormZ(int slot) => slot / REGION_CHUNKS_PER_SIDE;

        /// <summary>
        /// B13. Table durability across a close/reopen — the case that decides whether a saved world is still
        /// readable next session. Red when: the offset table is not flushed, or reopening mis-parses it (the
        /// reopen path rebuilds its free-sector map from the table, so a mis-parse also corrupts later writes).
        /// </summary>
        /// <returns>True when every record survives a close/reopen, including a write made after reopening.</returns>
        private static bool OffsetTableSurvivesCloseAndReopen()
        {
            using Fixture fx = new Fixture();
            string path = RegionPath(fx, "b13");

            byte[] a = MakePayload(5000, seed: 11);
            byte[] b = MakePayload(12000, seed: 12);
            byte[] c = MakePayload(3000, seed: 13);

            using (RegionFile region = new RegionFile(path))
            {
                region.SaveChunkData(0, 0, a, a.Length, CompressionAlgorithm.Deflate);
                region.SaveChunkData(31, 31, b, b.Length, CompressionAlgorithm.LZ4);
            }

            bool ok;
            using (RegionFile reopened = new RegionFile(path))
            {
                (byte[] readA, CompressionAlgorithm algoA) = reopened.LoadChunkData(0, 0);
                (byte[] readB, CompressionAlgorithm algoB) = reopened.LoadChunkData(31, 31);
                ok = Check("record A survives the reopen with its algorithm", PayloadsEqual(a, readA) && algoA == CompressionAlgorithm.Deflate);
                ok &= Check("record B survives the reopen with its algorithm", PayloadsEqual(b, readB) && algoB == CompressionAlgorithm.LZ4);

                // A write after reopening must not land on sectors the rebuilt free map should know are taken.
                reopened.SaveChunkData(10, 10, c, c.Length, CompressionAlgorithm.None);
                ok &= Check("a post-reopen write leaves the existing records intact",
                    PayloadsEqual(a, reopened.LoadChunkData(0, 0).data) &&
                    PayloadsEqual(b, reopened.LoadChunkData(31, 31).data) &&
                    PayloadsEqual(c, reopened.LoadChunkData(10, 10).data));
            }

            ok &= Check("no two records share a sector after the post-reopen write", NoOverlappingRuns(path));
            return ok;
        }

        /// <summary>
        /// B14. The deterministic-failure contract. A record needing more than 255 sectors cannot be addressed
        /// by the 1-byte size field, so it must fail as a TYPED throw the save paths map to
        /// <c>FailedPermanent</c>. Red when: it degrades to a silent truncation, a generic exception (which the
        /// save paths would treat as retryable and loop on forever), or a false success.
        /// </summary>
        /// <returns>True when an oversized record throws <see cref="ChunkTooLargeException"/> and changes nothing.</returns>
        private static bool OversizedRecordThrowsTypedAndChangesNothing()
        {
            using Fixture fx = new Fixture();
            string path = RegionPath(fx, "b14");

            byte[] neighbour = MakePayload(4000, seed: 21);
            byte[] oversized = new byte[256 * REGION_SECTOR_SIZE]; // needs 257 sectors including the record header

            using (RegionFile region = new RegionFile(path))
            {
                region.SaveChunkData(2, 2, neighbour, neighbour.Length, CompressionAlgorithm.None);
            }

            long lengthBefore = new FileInfo(path).Length;
            bool ok;

            using (RegionFile region = new RegionFile(path))
            {
                bool threwTyped = false;
                try
                {
                    region.SaveChunkData(2, 3, oversized, oversized.Length, CompressionAlgorithm.None);
                }
                catch (ChunkTooLargeException)
                {
                    threwTyped = true;
                }
                catch (Exception e)
                {
                    return Check($"an oversized record throws ChunkTooLargeException (threw {e.GetType().Name})", false);
                }

                ok = Check("an oversized record throws ChunkTooLargeException", threwTyped);
                ok &= Check("the neighbouring record is untouched in-session",
                    PayloadsEqual(neighbour, region.LoadChunkData(2, 2).data));
            }

            ok &= Check("the rejected record claims no table entry", ReadTableEntry(path, 2, 3).start == 0);
            ok &= Check($"the rejected write does not extend the file (expected {lengthBefore.ToString()}, got {new FileInfo(path).Length.ToString()} bytes)",
                new FileInfo(path).Length == lengthBefore);
            return ok;
        }

        // --- Helpers -----------------------------------------------------------------------------

        /// <summary>Builds a region-file path inside the fixture's volatile world folder (deleted on Dispose).</summary>
        /// <param name="fx">The suite fixture.</param>
        /// <param name="name">A scenario-unique file name stem.</param>
        /// <returns>An absolute path in a created directory.</returns>
        private static string RegionPath(Fixture fx, string name)
        {
            string folder = Path.Combine(SaveSystem.GetSavePath(fx.WorldName, useVolatilePath: true), "RegionMechanics");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, $"r.{name}.bin");
        }

        /// <summary>Deterministic pseudo-random payload of the requested length.</summary>
        /// <param name="length">Payload length in bytes.</param>
        /// <param name="seed">Seed, so a failure is reproducible.</param>
        /// <returns>The payload.</returns>
        private static byte[] MakePayload(int length, int seed)
        {
            byte[] data = new byte[length];
            new System.Random(seed).NextBytes(data);
            return data;
        }

        /// <summary>Sectors a payload of this length occupies, including the per-record header overhead.</summary>
        /// <param name="payloadLength">The payload length in bytes.</param>
        /// <returns>The sector count.</returns>
        private static int SectorsFor(int payloadLength) =>
            (payloadLength + 1 + REGION_RECORD_OVERHEAD - 1 + REGION_SECTOR_SIZE - 1) / REGION_SECTOR_SIZE;

        /// <summary>
        /// Reads one offset-table entry straight off disk: 3 bytes of sector start, 1 byte of sector count,
        /// packed as a little-endian int at <c>index × 4</c>. Deliberately independent of
        /// <see cref="RegionFile"/>'s own bookkeeping — the allocator cannot be its own oracle.
        /// <para>Only meaningful once the writing <see cref="RegionFile"/> has been disposed; see the flush
        /// discipline note on the class.</para>
        /// </summary>
        /// <param name="path">The region file path.</param>
        /// <param name="localX">Local chunk X (0–31).</param>
        /// <param name="localZ">Local chunk Z (0–31).</param>
        /// <returns>The entry's sector start and count; <c>(0, 0)</c> means "no record".</returns>
        private static (int start, int count) ReadTableEntry(string path, int localX, int localZ) =>
            DecodeTableEntry(ReadOffsetTable(path), localX + localZ * REGION_CHUNKS_PER_SIDE);

        /// <summary>
        /// Reads the whole location table in one pass. Deliberately one read rather than a seek per entry:
        /// the table is 1024 entries, and a handle-per-entry sweep costs thousands of file opens.
        /// </summary>
        /// <param name="path">The region file path.</param>
        /// <returns>The raw table bytes (zero-filled if the file is shorter than the table).</returns>
        private static byte[] ReadOffsetTable(string path)
        {
            byte[] table = new byte[REGION_CHUNKS_PER_SIDE * REGION_CHUNKS_PER_SIDE * 4];
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = 0;
            while (read < table.Length)
            {
                int n = fs.Read(table, read, table.Length - read);
                if (n == 0) break;
                read += n;
            }

            return table;
        }

        /// <summary>Decodes one packed table entry: 3 bytes of sector start, 1 byte of sector count.</summary>
        /// <param name="table">The raw table bytes.</param>
        /// <param name="index">The entry index (<c>localX + localZ × 32</c>).</param>
        /// <returns>The entry's sector start and count; <c>(0, 0)</c> means "no record".</returns>
        private static (int start, int count) DecodeTableEntry(byte[] table, int index)
        {
            int packed = BitConverter.ToInt32(table, index * 4);
            return ((packed >> 8) & 0xFFFFFF, packed & 0xFF);
        }

        /// <summary>
        /// Walks every offset-table entry and asserts no two allocated runs overlap — the invariant whose
        /// violation means one chunk's record has been handed sectors another chunk still points at. Also
        /// asserts the table is non-empty, so it cannot pass vacuously against an unflushed (all-zero) table.
        /// </summary>
        /// <param name="path">The region file path.</param>
        /// <returns>True when at least one run exists and all runs are disjoint.</returns>
        private static bool NoOverlappingRuns(string path)
        {
            int totalEntries = REGION_CHUNKS_PER_SIDE * REGION_CHUNKS_PER_SIDE;
            int sectors = (int)(new FileInfo(path).Length / REGION_SECTOR_SIZE) + 1;
            int[] owner = new int[sectors];
            for (int i = 0; i < sectors; i++) owner[i] = -1;

            byte[] table = ReadOffsetTable(path);
            int runs = 0;
            for (int index = 0; index < totalEntries; index++)
            {
                (int start, int count) = DecodeTableEntry(table, index);
                if (start == 0) continue;

                runs++;
                for (int s = start; s < start + count && s < sectors; s++)
                {
                    if (owner[s] != -1)
                    {
                        Debug.LogError($"    sector {s.ToString()} claimed by both entry {owner[s].ToString()} and entry {index.ToString()}");
                        return false;
                    }

                    owner[s] = index;
                }
            }

            if (runs == 0)
            {
                Debug.LogError("    the offset table is empty — the disjointness check would pass vacuously (unflushed writer?)");
                return false;
            }

            return true;
        }

        /// <summary>Compares a written payload against what the region layer returned (null-safe).</summary>
        /// <param name="expected">The payload as written.</param>
        /// <param name="actual">The payload as read back, or null.</param>
        /// <returns>True when the bytes match exactly.</returns>
        private static bool PayloadsEqual(byte[] expected, byte[] actual)
        {
            if (actual == null || expected.Length != actual.Length) return false;
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i])
                    return false;
            return true;
        }
    }
}
