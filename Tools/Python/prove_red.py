"""Prove that an editor validation suite goes red under a source mutation, then restore the file byte-for-byte.

WHAT THIS DOES
    One prove-red cycle against the running Unity Editor, through the Unity CLI (`unity`):
      1. backs the target file up and records its SHA-256;
      2. replaces EXACTLY ONE occurrence of --old with --new (line endings preserved; zero or several matches abort);
      3. recompiles (`unity recompile`) — a mutation that does not compile proves nothing, so that aborts too;
      4. runs the suite's menu item as a detached job, waiting in bounded slices;
      5. prints the suite's [FAIL] lines and its summary from the console entries logged during the run;
      6. ALWAYS restores the backup (also on error or Ctrl+C), verifies the checksum, and recompiles the restored code.
    Exit code 0 means the mutation went RED as it should; 1 means it SURVIVED (the suite stayed green — the gate is
    blind to it); 2 means the cycle could not run.

WHAT IT READS / WRITES
    Rewrites the target file in place for the duration of the run; backups go to Tools/Python/output/prove_red/
    (gitignored). Restoration copies the backup back — never `git checkout --`, which would also discard any
    uncommitted work in that file.

WHY IT EXISTS
    The validation-driven-bugfix and review workflows require each new baseline to be seen failing under the defect
    it guards. Done by hand, the restore step is where it goes wrong: a forgotten revert, a stale assembly, or a
    checkout that loses unrelated edits. PM-8's B31 used this cycle for four mutations.

RUN (from the repo root, with the Editor open on this project)
    python Tools/Python/prove_red.py --file Assets/Scripts/World.cs \\
        --old "lap = PerfTickTotals.Lap(ref _tickTotals.PrepareTicks, lap);" --new "" \\
        --suite "Minecraft Clone/Dev/Validate Performance Monitor"

    Multi-line snippets: pass --old-file / --new-file pointing at text files instead of --old / --new.
    --expect narrows what counts as red to a regex over the [FAIL] lines (e.g. the scenario's ID).
"""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
BACKUP_DIR = REPO_ROOT / "Tools" / "Python" / "output" / "prove_red"

JOB_WAIT_SLICE_SECONDS = 90
STILL_RUNNING_EXIT_CODE = 6
CONSOLE_TAIL = 400
FAIL_PATTERN = re.compile(r"\[FAIL\]")
SUMMARY_PATTERN = re.compile(r"BASELINE TESTS|VALIDATE ALL", re.IGNORECASE)


def unity(*args, timeout=600):
    """Runs a Unity CLI command from the repo root and returns the completed process."""
    executable = shutil.which("unity")
    if executable is None:
        raise RuntimeError("the Unity CLI ('unity') is not on PATH")
    return subprocess.run([executable, *args], cwd=REPO_ROOT, capture_output=True, text=True, encoding="utf-8",
                          errors="replace", timeout=timeout, stdin=subprocess.DEVNULL)


def json_payload(text):
    """Parses the JSON object a CLI command printed, ignoring any text before it."""
    start = text.find("{")
    return json.loads(text[start:]) if start >= 0 else {}


def wait_until_ready(max_polls=300):
    """Polls the Editor until it reports ready (compilation and domain reload finished)."""
    for _ in range(max_polls):
        status = unity("command", "editor_status", "--result-only", timeout=30)
        if '"status": "ready"' in status.stdout:
            return
        time.sleep(1)
    raise RuntimeError("the Editor never reported ready")


def recompile():
    """Recompiles every assembly; returns the compiler's error count."""
    result = unity("recompile", "--format", "json")
    match = re.search(r'"errors":\s*(\d+)', result.stdout)
    wait_until_ready()
    if match is None:
        raise RuntimeError(f"unexpected recompile output:\n{result.stdout}{result.stderr}")
    return int(match.group(1))


def console_entries():
    """Returns the newest console entries as (seq, message) pairs."""
    result = unity("command", "console", "--level", "all", "--tail", str(CONSOLE_TAIL), "--result-only", timeout=60)
    entries = json_payload(result.stdout).get("entries", [])
    return [(entry.get("seq", 0), entry.get("message", "")) for entry in entries]


def run_suite(menu_path, max_wait_seconds):
    """Runs a suite's menu item as a detached job and returns the console messages it logged."""
    before = max((seq for seq, _ in console_entries()), default=0)
    submitted = unity("command", "menu", "--path", menu_path, "--detach", "--format", "json", timeout=60)
    job_id = re.search(r'"jobId":\s*"([0-9a-f]+)"', submitted.stdout)
    if job_id is None:
        raise RuntimeError(f"the menu item did not start a job:\n{submitted.stdout}{submitted.stderr}")

    waited = 0
    while True:
        result = unity("job", "wait", job_id.group(1), "--timeout", str(JOB_WAIT_SLICE_SECONDS),
                       timeout=JOB_WAIT_SLICE_SECONDS + 30)
        if result.returncode != STILL_RUNNING_EXIT_CODE:
            break
        waited += JOB_WAIT_SLICE_SECONDS
        if waited >= max_wait_seconds:
            raise RuntimeError(f"the suite was still running after {waited} s")

    return [message for seq, message in console_entries() if seq > before]


def mutate(path, old, new):
    """Replaces exactly one occurrence of old with new, keeping the file's line endings."""
    text = path.read_bytes().decode("utf-8")
    if "\r\n" in text:
        old, new = old.replace("\r\n", "\n").replace("\n", "\r\n"), new.replace("\r\n", "\n").replace("\n", "\r\n")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"expected exactly one match of --old in {path}, found {count}")
    path.write_bytes(text.replace(old, new).encode("utf-8"))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_snippet(value, file_value, name):
    """Returns a snippet given inline or by file; exactly one of the two must be set."""
    if (value is None) == (file_value is None):
        raise RuntimeError(f"pass exactly one of --{name} and --{name}-file")
    return value if value is not None else Path(file_value).read_text(encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--file", required=True, help="the source file to mutate, relative to the repo root")
    parser.add_argument("--old", help="the exact text to replace (must occur once)")
    parser.add_argument("--old-file", help="a file holding the text to replace")
    parser.add_argument("--new", help="the replacement text ('' deletes)")
    parser.add_argument("--new-file", help="a file holding the replacement text")
    parser.add_argument("--suite", required=True, help="the suite's menu path")
    parser.add_argument("--expect", default=None, help="regex a [FAIL] line must match to count as red")
    parser.add_argument("--max-wait", type=int, default=600, help="seconds to wait for the suite (default 600)")
    args = parser.parse_args()

    target = (REPO_ROOT / args.file).resolve()
    BACKUP_DIR.mkdir(parents=True, exist_ok=True)
    backup = BACKUP_DIR / (target.name + ".bak")
    original_hash = sha256(target)
    shutil.copyfile(target, backup)

    verdict = 2
    try:
        mutate(target, read_snippet(args.old, args.old_file, "old"), read_snippet(args.new, args.new_file, "new"))
        print(f"mutated {args.file}: {original_hash[:12]} -> {sha256(target)[:12]}")
        errors = recompile()
        if errors:
            raise RuntimeError(f"the mutation does not compile ({errors} errors) — it proves nothing")

        messages = run_suite(args.suite, args.max_wait)
        fails = [m for m in messages if FAIL_PATTERN.search(m) and (args.expect is None or re.search(args.expect, m))]
        for message in messages:
            if FAIL_PATTERN.search(message) or SUMMARY_PATTERN.search(message):
                print("  " + message.splitlines()[0])
        verdict = 0 if fails else 1
        print("RED — the suite caught the mutation" if fails else "SURVIVED — the suite stayed green under the mutation")
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        print(f"error: {error}")
    finally:
        shutil.copyfile(backup, target)
        restored = sha256(target) == original_hash
        print(f"restored {args.file}: checksum {'IDENTICAL' if restored else 'MISMATCH'}")
        if not restored:
            verdict = 2
        elif recompile():
            print("warning: the restored code does not compile")
            verdict = 2
    return verdict


if __name__ == "__main__":
    sys.exit(main())
