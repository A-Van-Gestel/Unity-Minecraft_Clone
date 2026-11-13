"""Prove a NativeCompressions LZ4 upgrade keeps every existing save readable, outside Unity.

WHY THIS EXISTS
    A green single-version round-trip suite proves only that a library reads what it writes: it stays
    green for a library that writes and reads a non-standard format, which is how 0.6.1 bricked saves.
    This script checks the library against Python's `lz4.frame` (the reference implementation) and
    against real save data instead. LIBRARY_BUGS.md (NativeCompressions entry) records the 1.3.2 run.

CHECKS (`run`)
    A  output format    the new library's output starts with the frame magic 04 22 4D 18 and decodes
                        exactly with lz4.frame (empty, 1 byte, the largest real chunk, a multi-block blob)
    B  existing saves   every LZ4 record in the given worlds decodes byte-identical to lz4.frame
    C  rollback         the old library decodes everything the new one writes, plus Python-written
                        frames with content checksums (only with --old)
    D  corrupt input    the new library throws on every corrupt case, never hangs or returns data;
                        the old library's results are printed for comparison
    B and D read with both the CopyTo and the small-Read pattern; A and C write with both patterns.

SETUP
    1. Build Tools/Lz4Check once per library version (its .csproj header has the dotnet command),
       e.g. into Tools/Python/output/lz4check/new and .../old.
    2. Install lz4 (requirements.txt).

USAGE
    python Tools/Python/lz4_upgrade_check.py run --saves-root <saves dir> --worlds <w1> <w2> ...
        --new Tools/Python/output/lz4check/new [--old Tools/Python/output/lz4check/old]
        [--work Tools/Python/output/lz4_upgrade_check]

    <saves dir> is e.g. %USERPROFILE%/AppData/LocalLow/<company>/<product>/Editor_Temp_Saves.

EXIT CODES
    0  every check passed
    1  a check failed (the summary names it)
    2  bad arguments, or lz4 is not installed
"""

import argparse
import os
import shutil
import struct
import subprocess
import sys
from pathlib import Path

try:
    import lz4.block
    import lz4.frame
except ImportError:  # reported by main(); the module stays importable for its tests
    lz4 = None

FRAME_MAGIC = b"\x04\x22\x4d\x18"
SECTOR_SIZE = 4096
TOTAL_CHUNKS = 1024
LZ4_ALGORITHM = 2  # CompressionAlgorithm.LZ4
MAX_RECORD_BYTES = 16 * 1024 * 1024  # RegionFile's own sanity bound
HANG_SECONDS = 10
MODES = ("copy", "small")


# ── region files ──────────────────────────────────────────────────────────────

def iter_lz4_records(region_path):
    """Yields (slot, payload) for every LZ4 record in one region file."""
    data = region_path.read_bytes()
    if len(data) < SECTOR_SIZE * 2:
        return
    for slot, offset_data in enumerate(struct.unpack_from(f"<{TOTAL_CHUNKS}i", data, 0)):
        if offset_data == 0:
            continue
        pos = ((offset_data >> 8) & 0xFFFFFF) * SECTOR_SIZE
        if pos + 5 > len(data):
            continue
        length = struct.unpack_from("<i", data, pos)[0]
        if not 1 < length <= MAX_RECORD_BYTES or data[pos + 4] != LZ4_ALGORITHM:
            continue
        yield slot, data[pos + 5: pos + 5 + length - 1]


def dump(saves_root, worlds, out):
    """Writes every LZ4 record to out/saves/*.lz4 and its lz4.frame decode to out/saves_oracle/*.bin.

    Returns (records dumped, records lz4.frame itself cannot read).
    """
    saves, oracle = out / "saves", out / "saves_oracle"
    saves.mkdir(parents=True, exist_ok=True)
    oracle.mkdir(parents=True, exist_ok=True)
    dumped = unreadable = 0
    for world in worlds:
        region_dir = Path(saves_root) / world / "Region"
        if not region_dir.is_dir():
            raise FileNotFoundError(f"no Region folder for world '{world}' under {saves_root}")
        for region in sorted(region_dir.glob("r.*.*.bin")):
            for slot, payload in iter_lz4_records(region):
                name = f"{world.replace(' ', '_')}__{region.stem}__{slot}"
                try:
                    raw = lz4.frame.decompress(payload)
                except Exception as e:
                    unreadable += 1
                    print(f"  oracle cannot read {name}: {e}")
                    continue
                (saves / f"{name}.lz4").write_bytes(payload)
                (oracle / f"{name}.bin").write_bytes(raw)
                dumped += 1
    return dumped, unreadable


# ── inputs ────────────────────────────────────────────────────────────────────

def generate_inputs(out):
    """Writes the A inputs to out/gen; the real chunk is the largest oracle payload."""
    gen = out / "gen"
    gen.mkdir(parents=True, exist_ok=True)
    (gen / "empty.bin").write_bytes(b"")
    (gen / "one.bin").write_bytes(b"\x07")
    noise = os.urandom(1024 * 1024)
    (gen / "big.bin").write_bytes((noise + b"\x00" * (3 * 1024 * 1024) + noise[:500_000]) * 2)  # > 4 MB blocks
    largest = max((out / "saves_oracle").glob("*.bin"), key=lambda p: p.stat().st_size, default=None)
    if largest is not None:
        shutil.copyfile(largest, gen / "chunk.bin")
    return gen


def generate_corrupt_inputs(out, sample):
    """Writes the D inputs to out/bad: each passes or bypasses the frame-magic check differently."""
    bad = out / "bad"
    bad.mkdir(parents=True, exist_ok=True)
    good = lz4.frame.compress(sample, content_checksum=True)
    flipped = bytearray(good)
    flipped[len(good) // 2] ^= 0xFF
    block = lz4.block.compress(sample[:1900], store_size=False)
    cases = {
        "truncated": good[: len(good) // 2],
        "flipped_body": bytes(flipped),
        "garbage_after_magic": FRAME_MAGIC + os.urandom(2000),
        "raw_block_0_6_1": struct.pack("<i", len(block)) + block,  # the 0.6.1 writer's format
        "header_only": good[:7],
    }
    for name, blob in cases.items():
        (bad / f"{name}.lz4").write_bytes(blob)
    return bad


def python_frames(src, dst):
    """Compresses every src/*.bin with lz4.frame (content checksum on) into dst/*.lz4."""
    dst.mkdir(parents=True, exist_ok=True)
    for f in src.glob("*.bin"):
        (dst / f"{f.stem}.lz4").write_bytes(lz4.frame.compress(f.read_bytes(), content_checksum=True))


# ── comparisons ───────────────────────────────────────────────────────────────

def check_frames(lz4_dir, oracle_dir):
    """Counts *.lz4 files that start with the frame magic and lz4.frame-decode to the oracle *.bin."""
    ok, failures = 0, []
    for f in sorted(lz4_dir.glob("*.lz4")):
        blob = f.read_bytes()
        expected = (oracle_dir / f"{f.stem}.bin").read_bytes()
        if blob[:4] != FRAME_MAGIC:
            failures.append(f"{f.name}: no frame magic ({blob[:8].hex(' ')})")
            continue
        try:
            if lz4.frame.decompress(blob) != expected:
                failures.append(f"{f.name}: lz4.frame decodes different bytes")
                continue
        except Exception as e:
            failures.append(f"{f.name}: lz4.frame failed: {e}")
            continue
        ok += 1
    return ok, failures


def compare_dirs(expected_dir, actual_dir):
    """Counts expected *.bin files whose same-named file in actual_dir is byte-identical."""
    ok, failures = 0, []
    for f in sorted(expected_dir.glob("*.bin")):
        other = actual_dir / f.name
        if other.exists() and other.read_bytes() == f.read_bytes():
            ok += 1
        else:
            failures.append(f"{f.name}: {'differs' if other.exists() else 'missing'}")
    return ok, failures


# ── harness ───────────────────────────────────────────────────────────────────

def harness_command(build_dir):
    """The command prefix that runs a Tools/Lz4Check build."""
    exe = Path(build_dir) / "lz4check.exe"
    return [str(exe)] if exe.exists() else ["dotnet", str(Path(build_dir) / "lz4check.dll")]


def run_harness(build_dir, *args, timeout=None):
    """Runs the harness; returns (exit code, stdout). A timeout returns (None, '')."""
    try:
        done = subprocess.run(harness_command(build_dir) + [str(a) for a in args],
                              capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return None, ""
    return done.returncode, done.stdout.decode("utf-8", "replace")


# ── driver ────────────────────────────────────────────────────────────────────

class Report:
    """Collects one PASS/FAIL line per check."""

    def __init__(self):
        self.lines, self.failed = [], False

    def add(self, label, ok, total, failures):
        passed = not failures and ok == total and total > 0  # total 0 = ran nothing, never a pass
        self.failed |= not passed
        self.lines.append(f"{'PASS' if passed else 'FAIL'}  {label}: {ok}/{total}")
        for line in failures[:5]:
            self.lines.append(f"        {line}")
        if len(failures) > 5:
            self.lines.append(f"        ... {len(failures) - 5} more")


def run(args):
    work = Path(args.work)
    if work.exists():
        shutil.rmtree(work)
    report = Report()

    for label, build in (("new", args.new), ("old", args.old)):
        if build:
            print(f"{label}: {run_harness(build, 'info')[1].strip()}")

    dumped, unreadable = dump(args.saves_root, args.worlds, work)
    print(f"dumped {dumped} LZ4 records ({unreadable} unreadable by lz4.frame itself)")
    gen = generate_inputs(work)
    oracle = work / "saves_oracle"

    for mode in MODES:  # A
        run_harness(args.new, "compress-dir", gen, work / f"A_{mode}", mode)
        ok, failures = check_frames(work / f"A_{mode}", gen)
        report.add(f"A output format ({mode} writes)", ok, len(list(gen.glob("*.bin"))), failures)

    for mode in MODES:  # B
        run_harness(args.new, "decompress-dir", work / "saves", work / f"B_{mode}", mode)
        ok, failures = compare_dirs(oracle, work / f"B_{mode}")
        report.add(f"B existing saves ({mode} reads)", ok, dumped, failures)

    if args.old:  # C
        run_harness(args.new, "compress-dir", oracle, work / "C_new", "small")
        ok, failures = check_frames(work / "C_new", oracle)
        report.add("C new writes valid frames", ok, dumped, failures)
        run_harness(args.old, "decompress-dir", work / "C_new", work / "C_old", "copy")
        ok, failures = compare_dirs(oracle, work / "C_old")
        report.add("C old reads new frames", ok, dumped, failures)
        python_frames(gen, work / "C_py")
        for label, build in (("new", args.new), ("old", args.old)):
            run_harness(build, "decompress-dir", work / "C_py", work / f"C_py_{label}", "small")
            ok, failures = compare_dirs(gen, work / f"C_py_{label}")
            report.add(f"C {label} reads Python frames", ok, len(list(gen.glob("*.bin"))), failures)

    chunk = gen / "chunk.bin"  # D
    bad = generate_corrupt_inputs(work, chunk.read_bytes() if chunk.exists() else os.urandom(4096))
    ok, failures = 0, []
    for case in sorted(bad.glob("*.lz4")):
        results = {}
        for label, build in (("new", args.new), ("old", args.old)):
            if build:
                _, out = run_harness(build, "fail", case, HANG_SECONDS, timeout=HANG_SECONDS + 20)
                results[label] = out.strip() or "HANG (killed)"
        print(f"  D {case.stem:<22} " + " || ".join(f"{k}: {v}" for k, v in results.items()))
        if results["new"].startswith("read=THROW") and "dispose=ok" in results["new"]:
            ok += 1
        else:
            failures.append(f"{case.name}: new library did not throw cleanly: {results['new']}")
    report.add("D new throws on corrupt input", ok, len(list(bad.glob("*.lz4"))), failures)

    print("\n" + "\n".join(report.lines))
    return 1 if report.failed else 0


def main(argv=None):
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    p_run = sub.add_parser("run", help="run checks A-D")
    p_run.add_argument("--saves-root", required=True)
    p_run.add_argument("--worlds", nargs="+", required=True)
    p_run.add_argument("--new", required=True, help="Tools/Lz4Check build of the candidate version")
    p_run.add_argument("--old", help="Tools/Lz4Check build of the installed version (enables C)")
    p_run.add_argument("--work", default="Tools/Python/output/lz4_upgrade_check")
    args = parser.parse_args(argv)
    if lz4 is None:
        print("lz4 is not installed: pip install -r Tools/Python/requirements.txt")
        return 2
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
