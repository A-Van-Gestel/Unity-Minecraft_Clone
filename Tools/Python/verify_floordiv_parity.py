"""Exhaustive parity proof for the structure cell-election floor-div fix.

Verifies that the float idiom `(int)math.floor((float)x / spacing)` at
StandardChunkGenerationJob:573-574 is bit-identical to exact integer floor
division over the ENTIRE in-band range |x| <= 2^24 for every spacing the
authoring range allows (StructurePoolEntry.spacing is [Range(1, 64)]).

This is the evidence behind swapping the expression for ChunkMath.FloorDiv
without a world-version gate: identical in-band => existing worlds generate
byte-identical structures everywhere generation is non-degenerate.

Also searches OUT-of-band (|x| > 2^24) for the first divergence per spacing,
which feeds the validation suite's "teeth" cases (proof the parity sweep is
not vacuously green).

Float model: NumPy float32 arithmetic == C#'s float (IEEE-754 binary32,
round-to-nearest-even) for conversion and division. `//` on NumPy int64 is
floor division, the exact oracle.
"""
import numpy as np

IN_BAND = 1 << 24  # 16_777_216
SPACING_MAX = 64
CHUNK = 1 << 22  # elements per vectorized chunk


def float_idiom(x_int64: np.ndarray, spacing: int) -> np.ndarray:
    """The current C# expression: (int)math.floor((float)x / spacing)."""
    return np.floor(x_int64.astype(np.float32) / np.float32(spacing)).astype(np.int64)


def check_band(lo: int, hi: int, spacing: int):
    """Return list of (x, float_result, exact_result) mismatches in [lo, hi)."""
    mismatches = []
    for start in range(lo, hi, CHUNK):
        x = np.arange(start, min(start + CHUNK, hi), dtype=np.int64)
        got = float_idiom(x, spacing)
        expected = x // spacing
        bad = np.nonzero(got != expected)[0]
        for i in bad[:3]:  # cap per-chunk reporting
            mismatches.append((int(x[i]), int(got[i]), int(expected[i])))
        if mismatches:
            break
    return mismatches


def first_divergence_above(start: int, spacing: int, limit: int):
    """Scan upward from `start` for the first float-vs-exact divergence."""
    pos = start
    while pos < limit:
        hi = min(pos + CHUNK, limit)
        x = np.arange(pos, hi, dtype=np.int64)
        got = float_idiom(x, spacing)
        expected = x // spacing
        bad = np.nonzero(got != expected)[0]
        if len(bad) > 0:
            i = bad[0]
            return int(x[i]), int(got[i]), int(expected[i])
        pos = hi
    return None


def main():
    print(f"In-band exhaustive sweep: |x| <= 2^24 ({2 * IN_BAND + 1:,} values) "
          f"x spacings 1..{SPACING_MAX}")
    total_mismatches = 0
    for s in range(1, SPACING_MAX + 1):
        bad = check_band(-IN_BAND, IN_BAND + 1, s)
        if bad:
            total_mismatches += len(bad)
            for x, got, exp in bad:
                print(f"  IN-BAND MISMATCH spacing={s}: x={x} float={got} exact={exp}")
    if total_mismatches == 0:
        print("  PASS: float idiom == exact floor division for ALL in-band inputs.")
    else:
        print(f"  FAIL: {total_mismatches} in-band mismatches — the swap is NOT behavior-preserving.")

    print("\nOut-of-band first divergences (teeth-case candidates):")
    for s in (3, 5, 7, 10, 33, 63):
        hit = first_divergence_above(IN_BAND + 1, s, IN_BAND * 4)
        if hit is None:
            print(f"  spacing={s}: none found below 2^26")
        else:
            x, got, exp = hit
            print(f"  spacing={s}: x={x} float={got} exact={exp}")
            # Verify the mirrored negative case explicitly for suite symmetry.
            xn = np.array([-x], dtype=np.int64)
            gn = float_idiom(xn, s)[0]
            en = xn[0] // s
            tag = "diverges" if gn != en else "agrees"
            print(f"      negative mirror x={-x}: float={int(gn)} exact={int(en)} ({tag})")


if __name__ == "__main__":
    main()
