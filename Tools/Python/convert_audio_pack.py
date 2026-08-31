"""Convert selected clip families from a source sound pack into engine-ready OGG files.

Sound packs ship as high-bit-depth WAV, which is the wrong thing to commit: the sources are large
(this project has no Git LFS by deliberate choice) and the engine plays short one-shots where Vorbis
is transparent. This converts only the families asked for, caps the variant count, and renames to the
``<family>_NNN.ogg`` convention the runtime's random-variant picker and the Sound Editor both key on.

A ``.curated`` sidecar in the output folder lists variants that were deliberately auditioned out;
they are skipped rather than re-created, because a re-import is otherwise the one thing that silently
undoes a by-ear decision. See ``load_curated``.

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

CURATED_FILENAME = ".curated"

#: ``<family>: <index>, <lo>-<hi>, ...`` with ``#`` comments. Leading dot keeps Unity from importing it.
CURATED_ENTRY = re.compile(r"^(?P<family>[^:#]+):(?P<indices>[^#]*)")


def family_of(stem: str) -> str:
    """Return the family a clip belongs to: its name minus a trailing ``_NN`` variant suffix."""
    return VARIANT_SUFFIX.sub("", stem)


def load_curated(out: pathlib.Path) -> dict:
    """Read ``out/.curated`` into ``{family: {index, ...}}``. Returns empty when the file is absent.

    Indices name the variant number in the output filename, which is the position in the sorted source
    list — so skipping one leaves every other clip's number untouched. That is the whole point: the
    surviving files keep their names, and therefore their Unity ``.meta`` GUIDs and every reference to
    them. Renumbering instead would break the database on the next import.

    A malformed line raises rather than being ignored. A curation file nobody can parse is worse than
    none at all: it looks like the decision is recorded when it is not.
    """
    path = out / CURATED_FILENAME
    if not path.is_file():
        return {}

    curated = {}
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue

        match = CURATED_ENTRY.match(line)
        if not match:
            raise ValueError(f"{path}:{number}: expected '<family>: <indices>', got {raw.strip()!r}")

        family = match.group("family").strip()
        indices = set()
        for token in match.group("indices").split(","):
            token = token.strip()
            if not token:
                continue
            try:
                if "-" in token:
                    low, high = (int(part, 10) for part in token.split("-", 1))
                    if high < low:
                        raise ValueError
                    indices.update(range(low, high + 1))
                else:
                    indices.add(int(token, 10))
            except ValueError:
                raise ValueError(f"{path}:{number}: bad index {token!r} (want '7' or '18-29')") from None

        if indices:
            curated.setdefault(family, set()).update(indices)

    return curated


def collect(source: pathlib.Path) -> dict:
    """Group every WAV under *source* by family, each sorted so variant order is stable."""
    families = defaultdict(list)
    for wav in source.rglob("*.wav"):
        families[family_of(wav.stem)].append(wav)
    for paths in families.values():
        paths.sort(key=lambda p: p.name)
    return families


def convert(src: pathlib.Path, dst: pathlib.Path, quality: int, channels: int) -> bool:
    """Encode one WAV to OGG Vorbis at *channels* channels. Returns True on success.

    Mono is right for the 3D one-shot voices, where a stereo clip does not spatialize. Ambience beds
    play from 2D sources and must stay stereo, which is what ``--stereo`` is for.
    """
    result = subprocess.run(
        ["ffmpeg", "-y", "-loglevel", "error", "-i", str(src),
         "-ac", str(channels), "-c:a", "libvorbis", "-q:a", str(quality), str(dst)],
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
    parser.add_argument("--stereo", action="store_true",
                        help="Keep two channels. For 2D ambience beds; one-shots must stay mono.")
    parser.add_argument("--ignore-curated", action="store_true",
                        help=f"Re-create variants listed in the output folder's {CURATED_FILENAME}. "
                             "For a fresh audition pass only - the default is to honour the file.")
    parser.add_argument("--flat", action="store_true",
                        help="Name outputs after the source stem instead of appending a _NNN variant index. "
                             "For families that are a single clip, such as ambience loops.")
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

    try:
        curated = {} if args.ignore_curated else load_curated(out)
    except ValueError as error:
        print(error)
        return 1

    if args.ignore_curated and (out / CURATED_FILENAME).is_file():
        print(f"  !! --ignore-curated: re-creating variants {CURATED_FILENAME} excludes\n")

    total_in = total_out = 0
    total_skipped = 0
    resurrected = []
    for name in wanted:
        picked = families[name][: args.max_variants]
        excluded = curated.get(name, set())
        written = skipped = 0
        for index, wav in enumerate(picked):
            target = out / (f"{wav.stem}.ogg" if args.flat else f"{name}_{index:03d}.ogg")

            # Skipped AFTER enumerate, never filtered before it: the indices of the surviving clips
            # must not shift, or every .meta GUID and database reference to them is invalidated.
            if index in excluded:
                skipped += 1
                if target.exists():
                    resurrected.append(target)
                continue

            if convert(wav, target, args.quality, 2 if args.stereo else 1):
                written += 1
                total_out += target.stat().st_size
                total_in += wav.stat().st_size

        total_skipped += skipped
        note = f"   ({skipped} curated out)" if skipped else ""
        print(f"  {name:<46} {written:>3} clips{note}")

    if total_in:
        print(f"\n{len(wanted)} families -> {out}")
        print(f"  {total_in / 1e6:.1f} MB WAV -> {total_out / 1e6:.2f} MB OGG "
              f"({total_in / max(total_out, 1):.0f}x smaller)")
    if total_skipped:
        print(f"  {total_skipped} variants skipped per {CURATED_FILENAME}")

    # A curated variant already on disk predates the sidecar. Reported rather than deleted: removing a
    # Unity asset means removing its .meta too, which is the caller's call, not a converter's.
    if resurrected:
        print(f"\n  !! {len(resurrected)} curated variant(s) still present on disk. Delete each with "
              "its .meta sibling:")
        for path in resurrected:
            print(f"       {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
