"""Convert selected clip families from a source sound pack into engine-ready OGG files.

Sound packs ship as high-bit-depth WAV, which is the wrong thing to commit: the sources are large
(this project has no Git LFS by deliberate choice) and the engine plays short one-shots where Vorbis
is transparent. This converts only the families asked for, caps the variant count, and renames to the
``<family>_NNN.ogg`` convention the runtime's random-variant picker and the Sound Editor both key on.

Requires ffmpeg on PATH.

Example
-------
    python Tools/Python/convert_audio_pack.py \
        --source "K:/.../Footsteps_Essentials_NOX_SOUND" \
        --out "Assets/Audio/Blocks/nox_footsteps" \
        --families Footsteps_Leaves_Walk,Footsteps_Wood_Walk \
        --max-variants 8
"""

import argparse
import pathlib
import re
import shutil
import subprocess
import sys
from collections import defaultdict

VARIANT_SUFFIX = re.compile(r"_\d+$")


def family_of(stem: str) -> str:
    """Return the family a clip belongs to: its name minus a trailing ``_NN`` variant suffix."""
    return VARIANT_SUFFIX.sub("", stem)


def collect(source: pathlib.Path) -> dict:
    """Group every WAV under *source* by family, each sorted so variant order is stable."""
    families = defaultdict(list)
    for wav in source.rglob("*.wav"):
        families[family_of(wav.stem)].append(wav)
    for paths in families.values():
        paths.sort(key=lambda p: p.name)
    return families


def convert(src: pathlib.Path, dst: pathlib.Path, quality: int) -> bool:
    """Encode one WAV to OGG Vorbis, forcing mono. Returns True on success."""
    result = subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-i", str(src),
         "-ac", "1", "-c:a", "libvorbis", "-q:a", str(quality), str(dst)],
        capture_output=True, text=True,
    )
    if result.returncode != 0:
        print(f"  FAILED {src.name}: {result.stderr.strip()[:200]}")
        return False
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", required=True, help="Pack root to scan recursively for .wav files.")
    parser.add_argument("--out", required=True, help="Destination folder for the converted .ogg files.")
    parser.add_argument("--families", help="Comma-separated family names to convert. Omit to list what is available.")
    parser.add_argument("--max-variants", type=int, default=8, help="Cap on variants kept per family (default 8).")
    parser.add_argument("--quality", type=int, default=4, help="libvorbis -q:a value (default 4).")
    args = parser.parse_args()

    if shutil.which("ffmpeg") is None:
        print("ffmpeg is not on PATH.")
        return 1

    source = pathlib.Path(args.source)
    if not source.is_dir():
        print(f"Source is not a directory: {source}")
        return 1

    families = collect(source)

    # No selection means the caller is still deciding: print the menu instead of guessing for them.
    if not args.families:
        print(f"{len(families)} families under {source}:\n")
        for name in sorted(families):
            print(f"  {name:<46} {len(families[name]):>3} variants")
        return 0

    wanted = [f.strip() for f in args.families.split(",") if f.strip()]
    missing = [f for f in wanted if f not in families]
    if missing:
        print("Unknown families: " + ", ".join(missing))
        return 1

    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    total_in = total_out = 0
    for name in wanted:
        picked = families[name][: args.max_variants]
        written = 0
        for index, wav in enumerate(picked):
            target = out / f"{name}_{index:03d}.ogg"
            if convert(wav, target, args.quality):
                written += 1
                total_out += target.stat().st_size
                total_in += wav.stat().st_size
        print(f"  {name:<46} {written:>3} clips")

    if total_in:
        print(f"\n{len(wanted)} families -> {out}")
        print(f"  {total_in / 1e6:.1f} MB WAV -> {total_out / 1e6:.2f} MB OGG "
              f"({total_in / max(total_out, 1):.0f}x smaller)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
