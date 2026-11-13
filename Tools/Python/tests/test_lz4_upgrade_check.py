"""lz4_upgrade_check.py: region-record extraction, the byte comparisons and the never-pass-on-nothing report.
The harness half needs a Tools/Lz4Check build and real saves, so it is exercised by hand (see the script's
docstring); the lz4-dependent tests skip when the package is not installed."""
import shutil
import struct
import tempfile
import unittest
from pathlib import Path

from helpers import run_script
import lz4_upgrade_check as check

HAS_LZ4 = check.lz4 is not None


def region_bytes(records):
    """A region file with `records` = {slot: (algorithm, payload)}, one record per sector from sector 2."""
    header = [0] * check.TOTAL_CHUNKS
    body = bytearray(check.SECTOR_SIZE)  # sector 1: unused, keeps the file past the size floor
    for i, (slot, (algorithm, payload)) in enumerate(sorted(records.items())):
        sector = 2 + i
        header[slot] = (sector << 8) | 1
        record = struct.pack("<i", len(payload) + 1) + bytes([algorithm]) + payload
        body += record + bytes(check.SECTOR_SIZE - len(record))
    return struct.pack(f"<{check.TOTAL_CHUNKS}i", *header) + bytes(body)


class RegionTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="lz4-check-test-"))

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def write_region(self, records):
        region_dir = self.root / "World A" / "Region"
        region_dir.mkdir(parents=True)
        path = region_dir / "r.0.0.bin"
        path.write_bytes(region_bytes(records))
        return path

    def test_yields_only_lz4_records_with_their_slots(self):
        path = self.write_region({3: (check.LZ4_ALGORITHM, b"lz4-a"), 7: (1, b"deflate"),
                                  900: (check.LZ4_ALGORITHM, b"lz4-b")})
        self.assertEqual(list(check.iter_lz4_records(path)), [(3, b"lz4-a"), (900, b"lz4-b")])

    def test_skips_a_file_below_the_header_size(self):
        path = self.root / "r.0.0.bin"
        path.write_bytes(b"\x00" * 100)
        self.assertEqual(list(check.iter_lz4_records(path)), [])

    @unittest.skipUnless(HAS_LZ4, "lz4 not installed")
    def test_dump_writes_payload_and_oracle_and_counts_unreadable(self):
        good = check.lz4.frame.compress(b"chunk bytes")
        self.write_region({0: (check.LZ4_ALGORITHM, good), 1: (check.LZ4_ALGORITHM, b"not a frame")})
        out = self.root / "work"
        dumped, unreadable = check.dump(self.root, ["World A"], out)
        self.assertEqual((dumped, unreadable), (1, 1))
        self.assertEqual((out / "saves" / "World_A__r.0.0__0.lz4").read_bytes(), good)
        self.assertEqual((out / "saves_oracle" / "World_A__r.0.0__0.bin").read_bytes(), b"chunk bytes")

    def test_dump_rejects_a_missing_world(self):
        with self.assertRaises(FileNotFoundError):
            check.dump(self.root, ["No Such World"], self.root / "work")


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="lz4-check-test-"))
        self.expected, self.actual = self.root / "expected", self.root / "actual"
        self.expected.mkdir()
        self.actual.mkdir()

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def test_compare_dirs_flags_differing_and_missing_files(self):
        for name, data in (("same", b"1"), ("differs", b"2"), ("missing", b"3")):
            (self.expected / f"{name}.bin").write_bytes(data)
        (self.actual / "same.bin").write_bytes(b"1")
        (self.actual / "differs.bin").write_bytes(b"X")
        ok, failures = check.compare_dirs(self.expected, self.actual)
        self.assertEqual(ok, 1)
        self.assertEqual(sorted(failures), ["differs.bin: differs", "missing.bin: missing"])

    @unittest.skipUnless(HAS_LZ4, "lz4 not installed")
    def test_check_frames_rejects_a_raw_block_and_wrong_content(self):
        (self.expected / "a.bin").write_bytes(b"payload")
        (self.expected / "b.bin").write_bytes(b"payload")
        (self.expected / "c.bin").write_bytes(b"payload")
        (self.actual / "a.lz4").write_bytes(check.lz4.frame.compress(b"payload"))
        (self.actual / "b.lz4").write_bytes(struct.pack("<i", 9) + b"raw block")  # the 0.6.1 writer's shape
        (self.actual / "c.lz4").write_bytes(check.lz4.frame.compress(b"other"))
        ok, failures = check.check_frames(self.actual, self.expected)
        self.assertEqual(ok, 1)
        self.assertTrue(failures[0].startswith("b.lz4: no frame magic"))
        self.assertTrue(failures[1].startswith("c.lz4: lz4.frame decodes different bytes"))


class ReportTests(unittest.TestCase):
    def test_a_check_that_ran_nothing_fails(self):
        report = check.Report()
        report.add("B existing saves", 0, 0, [])
        self.assertTrue(report.failed)

    def test_a_short_count_fails_even_without_failure_lines(self):
        report = check.Report()
        report.add("B existing saves", 9, 10, [])
        self.assertTrue(report.failed)

    def test_a_full_count_passes(self):
        report = check.Report()
        report.add("A output format", 4, 4, [])
        self.assertFalse(report.failed)
        self.assertEqual(report.lines, ["PASS  A output format: 4/4"])


class CliTests(unittest.TestCase):
    def test_missing_arguments_exit_2(self):
        code, _ = run_script("lz4_upgrade_check.py", "run")
        self.assertEqual(code, 2)


if __name__ == "__main__":
    unittest.main()
