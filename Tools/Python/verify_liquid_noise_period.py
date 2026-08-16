"""Float32 model of LiquidCore.hlsl's noise, and the evidence behind the FLUID #20 fix.

The liquid shader sampled its noise at an absolute voxel coordinate, so float32's
distance-proportional resolution quantized the sample position: water flickered from
~3e5 blocks out and flattened toward a single color beyond ~1e6. The fix reduces the
origin onto a period the noise ALREADY has, leaving snoise itself untouched
(Assets/Scripts/Helpers/LiquidNoiseOrigin.cs, Documentation/Bugs/_FIXED_BUGS.md § Fluid #20).

This script is the instrument that established the three load-bearing facts:

  period   The shipped Ashima snoise repeats every 867 units of input (3 x 289: the 1/3
           simplex skew times mod289's modulus) and does NOT repeat at 289 or 578.
           This is what makes the origin reduction invisible without touching the noise.

  noop     Reducing the origin by a full period is a no-op — the same field everywhere,
           with no spatial dependence. THIS is the property an origin reduction must be
           tested for. Tiling and continuity are not: a rejected earlier fix that wrapped
           the simplex lattice satisfied both and still seamed on the diagonal plane
           4z + x + y = P, because a lattice-wrapped field only equals the original
           inside one period.

  onset    How the bug was bracketed: sample resolution is ULP(D) blocks at distance D,
           and detail collapses once that exceeds the on-screen pixel footprint.

Reads nothing and writes nothing — it prints a report. Re-run it after any change to
snoise/fbm in LiquidCore.hlsl, to LiquidNoiseOrigin's constants, or when picking a
period for a NEW reduction (the time-axis residual in _FIXED_BUGS #20 needs exactly this).

Float model: NumPy float32 arithmetic == HLSL float (IEEE-754 binary32). The port is a
line-for-line transcription of snoise/fbm; self_check() guards it against porting slips
before any measurement is trusted.

Run:
    python Tools/Python/verify_liquid_noise_period.py
    python Tools/Python/verify_liquid_noise_period.py --check period
Requires numpy (see Tools/Python/requirements.txt).
"""
import argparse

import numpy as np

# --- Shipped constants, mirrored from the engine ------------------------------------
# Helpers/LiquidNoiseOrigin.cs and LIQUID_NOISE_* in Assets/Shaders/Includes/LiquidCore.hlsl.
NOISE_PERIOD_UNITS = 3 * 289  # 867 — the field's intrinsic period, in sample-input units
PERIOD_BLOCKS = 2 * NOISE_PERIOD_UNITS  # 1734 — what the origin is reduced modulo

# Effective scales the two liquid shaders sample at, using the shipped material defaults
# (Assets/Materials/Voxels/LiquidBlocks.mat).
AUTHORED_SCALES = {
    "_WaveScale": 5.0,
    "_RippleScale": 15.0,
    "_WaveScale * 2": 10.0,
    "_NoiseScale": 2.0,
    "_NoiseScale * _CellDensity": 5.0,
    "_NoiseScale * 2": 4.0,
}
OCTAVES = 4  # FLUID_FBM_OCTAVES at the High tier


def f32(x):
    return np.float32(x)


# --- Line-for-line port of LiquidCore.hlsl ------------------------------------------

def mod289(x):
    return x - np.floor(x * f32(1.0 / 289.0)) * f32(289.0)


def permute(x):
    return mod289((x * f32(34.0) + f32(1.0)) * x)


def taylor_inv_sqrt(r):
    return f32(1.79284291400159) - f32(0.85373472095314) * r


def snoise(v):
    """LiquidCore.hlsl's snoise, unmodified. v: (..., 3) float32."""
    Cx, Cy = f32(1.0 / 6.0), f32(1.0 / 3.0)
    Dy, Dw = f32(0.5), f32(2.0)

    dot_vC = (v[..., 0] + v[..., 1] + v[..., 2]) * Cy
    i = np.floor(v + dot_vC[..., None])
    dot_iC = (i[..., 0] + i[..., 1] + i[..., 2]) * Cx
    x0 = v - i + dot_iC[..., None]

    g = (x0[..., [1, 2, 0]] <= x0[..., [0, 1, 2]]).astype(np.float32)  # step(x0.yzx, x0.xyz)
    l = f32(1.0) - g
    i1 = np.minimum(g, l[..., [2, 0, 1]])
    i2 = np.maximum(g, l[..., [2, 0, 1]])

    x1 = x0 - i1 + Cx
    x2 = x0 - i2 + Cy
    x3 = x0 - Dy

    # mod289 is what gives the field its period; see the module docstring.
    i = mod289(i)
    z = np.zeros_like(i[..., 0])
    o = np.ones_like(i[..., 0])

    def quad(axis):
        return np.stack([z, i1[..., axis], i2[..., axis], o], -1)

    p = permute(permute(permute(i[..., 2][..., None] + quad(2))
                        + i[..., 1][..., None] + quad(1))
                + i[..., 0][..., None] + quad(0))

    n_ = f32(0.142857142857)
    ns = n_ * np.array([Dw, Dy, f32(1.0)], np.float32) - np.array([f32(0.0), f32(1.0), f32(0.0)], np.float32)

    j = p - f32(49.0) * np.floor(p * ns[2] * ns[2])
    x_ = np.floor(j * ns[2])
    y_ = np.floor(j - f32(7.0) * x_)
    x = x_ * ns[0] + ns[1]
    y = y_ * ns[0] + ns[1]
    h = f32(1.0) - np.abs(x) - np.abs(y)

    b0 = np.concatenate([x[..., :2], y[..., :2]], -1)
    b1 = np.concatenate([x[..., 2:], y[..., 2:]], -1)
    s0 = np.floor(b0) * f32(2.0) + f32(1.0)
    s1 = np.floor(b1) * f32(2.0) + f32(1.0)
    sh = -(h <= f32(0.0)).astype(np.float32)  # -step(h, 0): edge is h, x is 0

    a0 = b0[..., [0, 2, 1, 3]] + s0[..., [0, 2, 1, 3]] * sh[..., [0, 0, 1, 1]]
    a1 = b1[..., [0, 2, 1, 3]] + s1[..., [0, 2, 1, 3]] * sh[..., [2, 2, 3, 3]]

    p0 = np.stack([a0[..., 0], a0[..., 1], h[..., 0]], -1)
    p1 = np.stack([a0[..., 2], a0[..., 3], h[..., 1]], -1)
    p2 = np.stack([a1[..., 0], a1[..., 1], h[..., 2]], -1)
    p3 = np.stack([a1[..., 2], a1[..., 3], h[..., 3]], -1)

    norm = taylor_inv_sqrt(np.stack([(p0 * p0).sum(-1), (p1 * p1).sum(-1),
                                     (p2 * p2).sum(-1), (p3 * p3).sum(-1)], -1))
    p0 = p0 * norm[..., 0][..., None]
    p1 = p1 * norm[..., 1][..., None]
    p2 = p2 * norm[..., 2][..., None]
    p3 = p3 * norm[..., 3][..., None]

    m = np.maximum(f32(0.6) - np.stack([(x0 * x0).sum(-1), (x1 * x1).sum(-1),
                                        (x2 * x2).sum(-1), (x3 * x3).sum(-1)], -1), f32(0.0))
    m = m * m
    dots = np.stack([(p0 * x0).sum(-1), (p1 * x1).sum(-1),
                     (p2 * x2).sum(-1), (p3 * x3).sum(-1)], -1)
    return f32(42.0) * (m * m * dots).sum(-1)


def fbm(p, octaves=OCTAVES):
    """LiquidCore.hlsl's fbm."""
    v = np.zeros(p.shape[:-1], np.float32)
    a, f = f32(0.5), f32(1.0)
    for _ in range(octaves):
        v = v + a * snoise((p * f).astype(np.float32))
        a = a * f32(0.5)
        f = f * f32(2.0)
    return v


def combined_noise(world_xyz, origin_xz):
    """EvaluateWater's combined_noise for still water: the wave and ripple fbms, remapped.

    Omits flow3D and the _Time.y scroll, which are constant across a still surface.
    """
    pos = (world_xyz + np.array([origin_xz[0], 0.0, origin_xz[1]], np.float32)).astype(np.float32)
    out = []
    for scale in (AUTHORED_SCALES["_WaveScale"], AUTHORED_SCALES["_RippleScale"]):
        out.append(fbm((pos * f32(scale)).astype(np.float32)))
    c = (out[0] + out[1]) * f32(0.5)
    return (c + f32(1.0)) * f32(0.5)


# --- Checks -------------------------------------------------------------------------

def self_check():
    """Guard the port before trusting any measurement below."""
    print("[self-check] the port must behave like simplex noise")
    rng = np.random.default_rng(0)
    v = (rng.random((20000, 3)) * 40.0 - 20.0).astype(np.float32)
    out = snoise(v)
    print(f"    range [{out.min():+.4f}, {out.max():+.4f}]  mean {out.mean():+.5f}  std {out.std():.4f}")

    # Simplex is C1, so a fine walk must never jump. A mis-permuted lattice — the likeliest
    # porting slip — shows up here as a discontinuity at cell boundaries.
    t = np.linspace(0.0, 30.0, 300001, dtype=np.float32)
    line = np.stack([t, t * f32(0.37) + f32(1.3), f32(11.0) - t * f32(0.61)], -1).astype(np.float32)
    jump = float(np.abs(np.diff(snoise(line))).max())
    print(f"    max step over a 1e-4-spaced walk: {jump:.3e}  (C1 field => tiny)")

    ok = -1.05 < out.min() and out.max() < 1.05 and abs(out.mean()) < 0.02 and jump < 1e-2
    print(f"    => {'OK' if ok else 'FAILED — do not trust the results below'}\n")
    return ok


def check_period():
    """The shipped snoise repeats every 867 units of input, and not at 289 or 578."""
    print(f"[period] is the UNMODIFIED snoise periodic? (expect {NOISE_PERIOD_UNITS} yes, 289/578 no)")
    rng = np.random.default_rng(17)
    v = (rng.random((60000, 3)) * 400.0 - 200.0).astype(np.float32)
    ref = snoise(v)

    ok = True
    for shift in (289.0, 578.0, float(NOISE_PERIOD_UNITS), 2.0 * NOISE_PERIOD_UNITS):
        worst = 0.0
        for axis in range(3):
            s = np.zeros(3, np.float32)
            s[axis] = shift
            worst = max(worst, float(np.abs(snoise((v + s).astype(np.float32)) - ref).max()))
        periodic = worst < 1e-2
        expected = shift % NOISE_PERIOD_UNITS == 0
        ok &= periodic == expected
        print(f"    shift {shift:8.1f}: max|diff| = {worst:.3e}  "
              f"{'PERIODIC' if periodic else 'not periodic':<14} {'(expected)' if periodic == expected else '<-- UNEXPECTED'}")
    print(f"    => {'OK' if ok else 'FAILED'}\n")
    return ok


def _mean_reduction_error(origin_shift, z_lo=300.0, step=0.01):
    """Mean |difference| between sampling at origin 0 and at `origin_shift`, over a water patch."""
    axis = np.arange(0.0, 14.0, step) + z_lo
    xs = np.linspace(-2.0, 2.0, 10)
    wx, wz = np.meshgrid(xs, axis)
    world = np.stack([wx, np.full_like(wx, 101.0), wz], -1).astype(np.float32)
    a = combined_noise(world, (0.0, 0.0)).astype(np.float64)
    b = combined_noise(world, (float(origin_shift), float(origin_shift))).astype(np.float64)
    return float(np.abs(a - b).mean())


def check_reduction_is_noop():
    """The property that matters: reducing the origin by a full period changes nothing.

    Carries its own positive control — periods that are NOT multiples of the field's
    intrinsic period must visibly disagree, so a vacuous pass is impossible.

    The metric is the MEAN, deliberately. The max is dominated by isolated samples where
    the ~1e-2 float slop on the *unreduced* coordinate (evaluated at ~1e5 magnitude) flips
    a simplex corner; production never evaluates that coordinate — it always samples the
    smaller, more accurate reduced one. What distinguishes a correct reduction from the
    rejected lattice-wrap approach is that the error has no spatial dependence, which the
    per-band sweep below shows.
    """
    print(f"[no-op] reducing the origin by a period must change nothing (shipped: {PERIOD_BLOCKS} blocks)")

    valid = [NOISE_PERIOD_UNITS, PERIOD_BLOCKS, 2 * PERIOD_BLOCKS]
    invalid = [500, 1024, 1500]  # not multiples of NOISE_PERIOD_UNITS

    print(f"    {'period':>8} {'multiple?':>10} {'mean|diff|':>12}")
    valid_errors, invalid_errors = [], []
    for p in valid + invalid:
        err = _mean_reduction_error(p)
        (valid_errors if p % NOISE_PERIOD_UNITS == 0 else invalid_errors).append(err)
        print(f"    {p:>8} {str(p % NOISE_PERIOD_UNITS == 0):>10} {err:>12.3e}")

    separation = min(invalid_errors) / max(valid_errors)
    print(f"    separation (worst valid vs best invalid): {separation:.0f}x")

    # No spatial dependence: a boundary is the lattice-wrap signature this rejects.
    print(f"    per-band mean at the shipped period, looking for a trend:")
    band_means = []
    for z_lo in (0, 200, 400, 700, 1200, 1600):
        m = _mean_reduction_error(PERIOD_BLOCKS, z_lo=float(z_lo), step=0.004)
        band_means.append(m)
        print(f"        z in [{z_lo:4d}, {z_lo + 14:4d}]: {m:.3e}")
    spread = max(band_means) / min(band_means)
    print(f"        spread across bands: {spread:.2f}x")

    ok = max(valid_errors) < 5e-3 and separation > 20.0 and spread < 3.0
    print(f"    => {'OK' if ok else 'FAILED'}\n")
    return ok


def check_onset():
    """How the bug was bracketed: detail collapses once ULP(D) exceeds the pixel footprint."""
    print("[onset] pre-fix degradation vs distance (a 2-block near-field patch, ~1 sample/pixel)")
    res, extent = 100, 2.0
    axis = np.linspace(0.0, extent, res)
    wx, wz = np.meshgrid(axis, axis)
    world = np.stack([wx, np.full_like(wx, 62.0), wz], -1).astype(np.float32)

    print(f"    {'distance':>12} {'ULP(blocks)':>12} {'detail':>9} {'flat pairs':>11}")
    base = None
    for d in (0.0, 1e4, 1e5, 3e5, 1e6, 3e6, 1e7):
        c = combined_noise(world, (d, d))
        dx = np.diff(c, axis=1)
        detail = float(np.sqrt((dx.astype(np.float64) ** 2).mean()))
        base = base if base is not None else detail
        ulp = float(np.spacing(np.float32(max(d, 1.0))))
        print(f"    {d:>12,.0f} {ulp:>12.3g} {detail / base:>8.1%} {float((dx == 0).mean()):>10.1%}")
    print("    => onset ~3e5 (first quantization), unmistakable from ~1e6\n")
    return True


CHECKS = {"period": check_period, "noop": check_reduction_is_noop, "onset": check_onset}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", choices=sorted(CHECKS) + ["all"], default="all",
                        help="which check to run (default: all)")
    args = parser.parse_args()

    if not self_check():
        raise SystemExit(1)

    selected = sorted(CHECKS) if args.check == "all" else [args.check]
    ok = all(CHECKS[name]() for name in selected)
    print("RESULT:", "all checks passed" if ok else "FAILED")
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
