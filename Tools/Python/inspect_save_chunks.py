"""Decode region files off disk and identify which historical chunk layout their payloads use.

WHY THIS EXISTS
    Nothing in the engine can read a historical chunk payload any more: `ChunkSerializer.Deserialize`
    hard-rejects any version byte that is not the current one, and the migration steps are the only
    surviving record of the older layouts. When a question is "what is ACTUALLY on disk in that old
    save", reading C# does not answer it — this does, without touching Unity.

    It was written to settle SERIALIZATION_BUGS §10/§11 (August 2026), where it established:
      - real world-v1 saves carry chunk-format v1 payloads, matching what the Migration Chain
        fixture authors — the first evidence for that fixture that is NOT derived from the
        migration steps' own read definitions, which is the limit ChunkFixture.cs documents;
      - world version 1 covers at least TWO incompatible chunk layouts (§11): the earliest saves
        predate both the `needsLight` flag and the trailing light queues, and nothing in the file
        distinguishes them but its length.

FORMATS DECODED
    Region file (Assets/Scripts/Serialization/RegionFile.cs):
        bytes 0..4095   1024 * int32 offsetData; sectorOffset = (offsetData >> 8) & 0xFFFFFF
        record at sectorOffset * 4096:
            int32  length              payload length + 1
            byte   compressionAlgo     0 None, 1 Deflate, 2 LZ4, 3 GZip (SaveDataTypes.cs)
            byte[length - 1]           payload
    Payload byte 0 is the chunk format version of its era.

    Chunk payload, eras 1-2 (Migration_v2_to_v3_RestoreLighting.cs's READ DEFINITION):
        byte version | int32 x | int32 z | [byte needsLight] | heightmap | int32 sectionBitmask
        per set bit: byte sectionVersion | uint16 nonAirCount | uint32[4096] voxels
        [int32 count + count * 13-byte entries] * 2          sun queue, then block queue
    The bracketed fields are the ones early v1 saves lack; the heightmap is 256 bytes at era 1 and
    512 at era 2. The script tries every combination and reports the one that consumes the payload
    EXACTLY — an exact fit over ~131 KB is not something a wrong hypothesis produces by accident.

USAGE
    python Tools/Python/inspect_save_chunks.py <saves-root> <world-name> [<world-name> ...]

    Saves root is the platform persistent-data path, e.g. on Windows
    %USERPROFILE%/AppData/LocalLow/<company>/<product>/Saves        (production builds)
    %USERPROFILE%/AppData/LocalLow/<company>/<product>/Editor_Temp_Saves   (editor / MCP runs)

    LZ4 payloads need the `lz4` package (see requirements.txt); Deflate and None need only the
    stdlib, so an un-provisioned environment still reads the oldest saves.
"""

import struct
import sys
import zlib
from collections import Counter
from pathlib import Path

SECTOR_SIZE = 4096
TOTAL_CHUNKS = 1024
SECTION_BYTES = 1 + 2 + 4096 * 4  # sectionVersion + nonAirCount + uint32[SECTION_VOLUME]
LIGHT_ENTRY_BYTES = 13  # 3 * int32 position + 1 byte level, pre-v7->v8 widening

ALGO_NAMES = {0: "None", 1: "Deflate", 2: "LZ4", 3: "GZip"}


def decompress(payload, algo):
    """Returns the raw payload, or None when the codec is unavailable or the data is unreadable."""
    if algo == 0:
        return payload
    if algo == 1:
        return zlib.decompress(payload, -15)  # raw deflate: no zlib header
    if algo == 2:
        try:
            import lz4.frame
            return lz4.frame.decompress(payload)
        except ImportError:
            return None
        except Exception:
            try:
                import lz4.block
                return lz4.block.decompress(payload, uncompressed_size=4 * 1024 * 1024)
            except Exception:
                return None
    return None


def iter_records(region_path):
    """Yields (algo, raw_payload) for every populated chunk slot in one region file."""
    data = region_path.read_bytes()
    if len(data) < SECTOR_SIZE * 2:
        return

    offsets = struct.unpack_from("<%di" % TOTAL_CHUNKS, data, 0)
    for offset_data in offsets:
        if offset_data == 0:
            continue

        pos = ((offset_data >> 8) & 0xFFFFFF) * SECTOR_SIZE
        if pos + 5 > len(data):
            continue

        length = struct.unpack_from("<i", data, pos)[0]
        if not 1 < length <= 16 * 1024 * 1024:  # RegionFile's own sanity bound
            continue

        algo = data[pos + 4]
        yield algo, decompress(data[pos + 5: pos + 5 + length - 1], algo)


def parse_era_payload(buf, has_needs_light, heightmap_bytes, has_queues):
    """Parses under one layout hypothesis. Returns a dict, or None if the layout does not fit."""
    p = 0
    try:
        version = buf[p]; p += 1
        x, z = struct.unpack_from("<ii", buf, p); p += 8
        needs_light = None
        if has_needs_light:
            needs_light = buf[p]; p += 1
        p += heightmap_bytes
        bitmask = struct.unpack_from("<i", buf, p)[0]; p += 4

        sections = []
        for i in range(32):
            if not (bitmask >> i) & 1:
                continue
            if p + SECTION_BYTES > len(buf):
                return None
            sections.append((i, buf[p], struct.unpack_from("<H", buf, p + 1)[0]))
            p += SECTION_BYTES

        queues = []
        if has_queues:
            for _ in range(2):
                if p + 4 > len(buf):
                    return None
                count = struct.unpack_from("<i", buf, p)[0]; p += 4
                if count < 0 or p + count * LIGHT_ENTRY_BYTES > len(buf):
                    return None
                queues.append(count)
                p += count * LIGHT_ENTRY_BYTES
    except (IndexError, struct.error):
        return None

    if p != len(buf):
        return None

    return dict(version=version, x=x, z=z, needs_light=needs_light, bitmask=bitmask,
                sections=sections, queues=queues)


def identify_layout(buf):
    """Returns (description, parsed) for the single hypothesis that consumes buf exactly."""
    for has_needs_light in (True, False):
        for heightmap_bytes in (256, 512):
            for has_queues in (True, False):
                parsed = parse_era_payload(buf, has_needs_light, heightmap_bytes, has_queues)
                if parsed is not None:
                    return (f"needsLight={has_needs_light} heightmap={heightmap_bytes} "
                            f"queues={has_queues}"), parsed
    return None, None


def inspect_world(saves_root, world_name, max_regions=8, max_chunks_per_region=40):
    """Prints a compression/version census plus a structural read of the first decodable chunk."""
    region_dir = Path(saves_root) / world_name / "Region"
    if not region_dir.is_dir():
        print(f"[{world_name}] no Region folder")
        return

    files = sorted(region_dir.glob("r.*.*.bin"))
    algos, versions = Counter(), Counter()
    sample = None

    for region_path in files[:max_regions]:
        seen = 0
        for algo, raw in iter_records(region_path):
            algos[ALGO_NAMES.get(algo, algo)] += 1
            versions[raw[0] if raw else "undecodable"] += 1
            if raw and sample is None:
                sample = raw
            seen += 1
            if seen >= max_chunks_per_region:
                break

    total_mb = sum(f.stat().st_size for f in files) / (1024 * 1024)
    print(f"[{world_name}] {len(files)} region files, {total_mb:.2f} MB "
          f"(sampled {sum(algos.values())} chunks from {min(len(files), max_regions)})")
    print(f"    compression        : {dict(algos)}")
    print(f"    chunk format bytes : {dict(versions)}")

    if sample is None:
        print("    layout             : no decodable payload (LZ4 without the lz4 package?)")
        return

    description, parsed = identify_layout(sample)
    if parsed is None:
        print(f"    layout             : {len(sample)} bytes, no era-1/2 hypothesis fits "
              "(current-format payload, or an unknown layout)")
        return

    print(f"    layout             : {len(sample)} bytes -> {description}")
    print(f"      stored position  : x={parsed['x']} z={parsed['z']} (voxel-space origin)")
    print(f"      sections         : {len(parsed['sections'])} (bitmask 0x{parsed['bitmask']:x})"
          f"  queues: {parsed['queues'] or 'absent'}")


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 1
    for world_name in argv[2:]:
        inspect_world(argv[1], world_name)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
