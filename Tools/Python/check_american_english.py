"""Flag British spellings in ADDED lines (or whole files) — the repo writes American English.

WHAT THIS GUARDS
    CODING_STYLE_GUIDE.md §1: identifiers, comments, docstrings, tooltips, logs, player-facing
    strings and docs are all American English, because the .NET and Unity APIs underneath are
    (`Color`, `Initialize`, `bounds.center`). The only exemption is a framework name that is itself
    British: `MonoBehaviour` and its relatives (`StateMachineBehaviour`, `UnityEngine.Behaviour`).

WHAT IT CHECKS
    By default only lines ADDED relative to HEAD (staged + unstaged) plus every line of untracked
    files, so pre-existing spellings elsewhere never block a commit. Whitespace-only changes are
    ignored, so re-aligning a table does not resurface the spellings already in it. `--base/--head` checks a commit
    range instead; `--all PATH ...` scans whole files. Matching is case-insensitive and also finds
    a word inside an identifier at a camel-case boundary (`centreX`, `ConfirmationCancelledNotice`).
    A line containing `american-english: ignore` is skipped (for quoted external text).

    The word list is explicit rather than a `-ise` rule: `exercise`, `promise`, `analysis`,
    `emphasis`, `central` and friends are spelled the same in both dialects.

READS   git diffs and text files. WRITES nothing.

RUN
    python Tools/Python/check_american_english.py                         # working tree vs HEAD
    python Tools/Python/check_american_english.py Assets/Scripts/Foo.cs  # only these paths (a batch)
    python Tools/Python/check_american_english.py --base HEAD~3 --head HEAD
    python Tools/Python/check_american_english.py --all Assets/Scripts Documentation

EXIT CODES
    0  no British spelling found
    1  at least one found (each printed as path:line: word -> suggestion)
    2  git error or missing path
"""
import argparse
import os
import re
import subprocess
import sys

TEXT_EXTENSIONS = {".cs", ".md", ".py", ".hlsl", ".shader", ".cginc", ".compute", ".json", ".txt",
                   ".uss", ".uxml", ".js", ".mjs"}
SUPPRESS_MARKER = "american-english: ignore"
SELF_NAME = os.path.basename(__file__)
EXEMPT_PREFIXES = ("Mono", "StateMachine", "Network", "Playable", "UnityEngine.")

# British stem -> American stem; each stem is matched with the suffix group that follows it.
OUR_WORDS = ["colour", "honour", "favour", "labour", "neighbour", "behaviour", "flavour", "armour",
             "rumour", "vapour"]
IZE_STEMS = ["initialis", "serialis", "deserialis", "optimis", "normalis", "finalis", "organis",
             "recognis", "realis", "utilis", "minimis", "maximis", "prioritis", "synchronis",
             "visualis", "categoris", "customis", "summaris", "materialis", "authoris", "emphasis",
             "generalis", "specialis", "standardis", "parameteris", "tokenis", "sanitis", "randomis",
             "memoris", "capitalis", "localis", "stabilis", "centralis", "rasteris", "quantis",
             "virtualis", "vectoris", "linearis", "discretis", "amortis", "apologis", "criticis"]
DOUBLED_L = ["cancell", "modell", "labell", "levell", "travell", "signall", "fuell", "totall",
             "tunnell", "channell"]
EXACT = {
    "centre": "center", "centres": "centers", "centred": "centered", "centring": "centering",
    "metre": "meter", "metres": "meters", "litre": "liter", "fibre": "fiber", "calibre": "caliber",
    "theatre": "theater", "defence": "defense", "offence": "offense", "licence": "license",
    "pretence": "pretense", "judgement": "judgment", "judgements": "judgments",
    "acknowledgement": "acknowledgment", "grey": "gray", "greys": "grays", "greyed": "grayed",
    "greyscale": "grayscale", "whilst": "while", "artefact": "artifact", "artefacts": "artifacts",
    "manoeuvre": "maneuver", "focussed": "focused", "focussing": "focusing", "learnt": "learned",
    "fulfil": "fulfill", "fulfilment": "fulfillment", "analyse": "analyze", "analysed": "analyzed",
    "analysing": "analyzing", "analyser": "analyzer", "paralyse": "paralyze",
    "programme": "program",
}


def suffixes(*options):
    """Regex group trying the LONGEST suffix first: alternation takes the first option that fits,
    so `e` before `ed` would match `initialise` inside `initialised` and then fail the word-end test."""
    return "(" + "|".join(sorted(options, key=len, reverse=True)) + ")"


def build_patterns():
    """(regex, British stem length, American stem); the suffix after the stem carries over."""
    patterns = []
    for word in OUR_WORDS:
        regex = re.compile(word + suffixes("s", "ed", "ing", "al", "ally", "able", "ite", "ites", "less")
                           + "?", re.I)
        patterns.append((regex, len(word), word.replace("our", "or")))
    for stem in IZE_STEMS:
        regex = re.compile(stem + suffixes("e", "ed", "es", "er", "ers", "ing", "ation", "ations", "able"),
                           re.I)
        patterns.append((regex, len(stem), stem[:-1] + "z"))
    for stem in DOUBLED_L:
        patterns.append((re.compile(stem + suffixes("ed", "ing", "er", "ers"), re.I), len(stem), stem[:-1]))
    for word, american in EXACT.items():
        patterns.append((re.compile(word, re.I), len(word), american))
    return patterns


PATTERNS = build_patterns()


def is_word_segment(line, match):
    """True when the match is a whole word or a whole camel-case/ALL-CAPS identifier segment."""
    text, start, end = match.group(0), match.start(), match.end()
    if start > 0 and line[start - 1].isalpha():
        if not (line[start - 1].islower() and text[0].isupper()):
            return False
    if end < len(line):
        following = line[end]
        if following.islower() or (text.isupper() and following.isupper()):
            return False
    return True


def is_exempt(line, match):
    """Unity's own `Behaviour` types keep their British name: the bare `Behaviour` class, and the
    `MonoBehaviour` / `StateMachineBehaviour` / `NetworkBehaviour` / `PlayableBehaviour` family."""
    head = line[:match.start()]
    if not match.group(0).lower().startswith("behaviour"):
        return False
    return is_bare_behaviour_type(line, match) or head.endswith(EXEMPT_PREFIXES)


def is_bare_behaviour_type(line, match):
    """`Behaviour` as Unity's class: in a type position (`: Behaviour`, `<Behaviour>`, `is Behaviour b`,
    `typeof(Behaviour)`, `Behaviour.enabled`) — never a capitalized word in prose or a heading."""
    if match.group(0) != "Behaviour":
        return False
    head, tail = line[:match.start()], line[match.end():]
    if head and (head[-1].isalnum() or head[-1] == "_"):
        return False
    before = head.rstrip()
    return (before.endswith((":", "<", "(", ",", " is", " as", " new"))
            or re.match(r"[>.)\[]|\s+\w+\s*[=;),]", tail) is not None)


def scan_line(line):
    if SUPPRESS_MARKER in line:
        return []
    hits = []
    for pattern, stem_length, american in PATTERNS:
        for match in pattern.finditer(line):
            if is_word_segment(line, match) and not is_exempt(line, match):
                british = match.group(0)
                hits.append((british, american + british[stem_length:].lower()))
    return hits


def run_git(*args):
    result = subprocess.run(["git", "-c", "color.ui=never", "-c", "core.quotePath=false", "-c", "diff.relative=false",
                             *args],
                            capture_output=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr.decode("utf-8", "replace").strip())
    return result.stdout.decode("utf-8", "replace")


def added_lines(diff_text):
    """Yields (path, line_number, text) for every '+' line of a -U0 diff. `+++` is read as a file
    header only between `diff --git` and the first `@@`, so an added line that itself starts with
    `++ ` is content, not a new header."""
    path, number, in_header = None, 0, False
    for line in diff_text.split("\n"):
        if line.startswith("diff --git "):
            path, in_header = None, True
        elif in_header and line.startswith("+++ "):
            # git ends the header with a TAB when the path contains a space.
            path = line[6:].rstrip("\t") if line.startswith("+++ b/") else None
        elif line.startswith("@@"):
            in_header = False
            number = int(re.search(r"\+(\d+)", line).group(1))
        elif not in_header and line.startswith("+") and path:
            yield path, number, line[1:].rstrip("\r")
            number += 1


def is_text_path(path):
    # This file and its test list the British spellings it hunts for, so neither is scanned.
    if os.path.basename(path) in (SELF_NAME, "test_" + SELF_NAME):
        return False
    return os.path.splitext(path)[1].lower() in TEXT_EXTENSIONS


def file_lines(path):
    with open(path, "rb") as handle:
        for number, line in enumerate(handle.read().decode("utf-8", "replace").split("\n"), 1):
            yield path, number, line.rstrip("\r")


def collect_all(paths):
    for root in paths:
        if os.path.isfile(root):
            yield root
            continue
        for directory, _, names in os.walk(root):
            for name in names:
                yield os.path.join(directory, name)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Flag British spellings.")
    parser.add_argument("--base", default="HEAD")
    parser.add_argument("--head", default=None, help="default: working tree (+ untracked files)")
    parser.add_argument("--all", nargs="+", metavar="PATH", help="scan whole files instead of a diff")
    parser.add_argument("paths", nargs="*", help="diff mode: limit to these files/directories (a batch)")
    args = parser.parse_args()
    pathspec = ["--", *args.paths] if args.paths else []

    sources = []
    try:
        if args.all:
            missing = [p for p in args.all if not os.path.exists(p)]
            if missing:
                print(f"error: path not found: {', '.join(missing)}", file=sys.stderr)
                return 2
            for path in collect_all(args.all):
                if is_text_path(path):
                    sources.append(file_lines(path))
        else:
            # -w: a line whose only change is whitespace (e.g. a re-aligned table row) is not new text.
            # Pinned output format: color.diff / noprefix / mnemonicPrefix settings would otherwise hide
            # every `+++ b/` header and make the scan pass vacuously.
            diff_args = ["diff", "-U0", "-w", "--no-ext-diff", "--no-color", "--src-prefix=a/", "--dst-prefix=b/",
                         args.base] + ([args.head] if args.head else [])
            sources.append(added_lines(run_git(*diff_args, *pathspec)))
            if not args.head:
                untracked = run_git("ls-files", "--others", "--exclude-standard", "-z", *pathspec).split("\0")
                sources.extend(file_lines(p) for p in untracked if p and is_text_path(p))
    except RuntimeError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2

    found = 0
    for source in sources:
        for path, number, text in source:
            if not is_text_path(path):
                continue
            for word, american in scan_line(text):
                found += 1
                print(f"{path}:{number}: {word} -> {american}")
    if found:
        print(f"{found} British spelling(s). Fix them, or mark quoted external text with "
              f"'{SUPPRESS_MARKER}'.")
        return 1
    print("OK: no British spelling in the checked lines.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
