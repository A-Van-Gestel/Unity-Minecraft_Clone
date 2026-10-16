"""Prove that an editor validation suite goes red under a source mutation, then restore the file byte-for-byte.

WHAT THIS DOES
    Prove-red cycles against the running Unity Editor, through the Unity CLI (`unity`):
      1. recompiles once, so every run below tests the code on disk (a tree that does not compile stops here);
      2. runs each distinct suite once unmutated — a baseline that is already red proves nothing, so its entries
         abort (--no-baseline skips this when the suite was just seen green on this exact tree);
      3. backs the target file up and records its SHA-256;
      4. replaces EXACTLY ONE occurrence of --old with --new (line endings preserved; zero or several matches abort);
      5. recompiles — a mutation that does not compile proves nothing, so that aborts too;
      6. runs the suite's menu item as a detached job, waiting in bounded slices, and reads this run's console
         entries: its [FAIL] lines and its summary line. No summary line, `NO SCENARIOS RAN` or a STALE-CODE WARNING
         means the run proves nothing;
      7. ALWAYS restores the backup (also on error or Ctrl+C), verifies the checksum, deletes the backup and
         recompiles the restored code.
    `git status --short` is compared before and after the whole run; a difference prints TREE CHANGED.

TIME BUDGET
    --budget (default 540 s) caps the whole run below the agent tool's 10-minute limit. An entry whose expected
    recompiles and suite runs no longer fit is not started, and the summary marks it NOT RUN. A suite still running
    at --max-wait keeps running inside the Editor (killing the CLI wait does not stop it), so it stops the batch and
    leaves the restored file uncompiled. A kill of THIS script runs no cleanup: the mutation stays in place, the
    backup it printed is the way back, and every later run refuses to start until that backup is gone.

WHAT IT READS / WRITES
    Rewrites the target file in place for the duration of a cycle; backups go to Tools/Python/output/prove_red/
    (gitignored), one per cycle, deleted once the restore checks out. Restoration copies the backup back — never
    `git checkout --`, which would also discard any uncommitted work in that file.

WHY IT EXISTS
    The validation-driven-bugfix and review workflows require each new baseline to be seen failing under the defect
    it guards. Done by hand, the restore step is where it goes wrong: a forgotten revert, a stale assembly, or a
    checkout that loses unrelated edits.

RUN (from the repo root, with the Editor open on this project and not in Play mode)
    python -u Tools/Python/prove_red.py --file Assets/Scripts/World.cs \\
        --old "lap = PerfTickTotals.Lap(ref _tickTotals.PrepareTicks, lap);" --new "" \\
        --suite "Minecraft Clone/Dev/Validate Performance Monitor"

    Multi-line snippets: pass --old-file / --new-file pointing at text files instead of --old / --new.
    --expect narrows what counts as red to a regex over the [FAIL] lines (e.g. the scenario's ID).

    python -u Tools/Python/prove_red.py --batch mutations.json
    A JSON list of {"file", "old", "new", "suite", "expect"?, "label"?}; each entry is its own cycle, each distinct
    suite gets one baseline, and a summary table ends the run. A restore mismatch, restored code that does not
    compile, a suite that outlives --max-wait or the time budget stops the batch: nothing may run against a tree
    in an unknown state.

EXIT CODES (a batch exits with its worst entry)
    0  RED: the mutation made a baseline fail, as it should
    1  SURVIVED: every baseline stayed green, or no [FAIL] line matched --expect
    2  the cycle could not run: missing file, invalid --expect, baseline red, no unique match, a mutation that
       leaves the file unchanged or does not compile, no verdict in the console, a timeout, no time budget left,
       restore mismatch, a backup left by an earlier run, any unexpected error
"""

import argparse
import datetime
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
import traceback
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
BACKUP_DIR = REPO_ROOT / "Tools" / "Python" / "output" / "prove_red"

JOB_WAIT_SLICE_SECONDS = 90
STILL_RUNNING_EXIT_CODE = 6
CONSOLE_TAIL = 2000  # the Editor console keeps its last 2000 entries
FAIL_PATTERN = re.compile(r"\[FAIL\]")
SUMMARY_PATTERN = re.compile(r"BASELINE TESTS (PASSED|FAILED)|VALIDATE ALL:")
NO_VERDICT_PATTERN = re.compile(r"NO SCENARIOS RAN|STALE-CODE WARNING")
NOT_RUN = 3  # the time budget ran out before the entry; exits as 2


class BatchStopped(Exception):
    """The tree or the Editor is in an unknown state; nothing else may run in this batch."""


class SuiteTimedOut(BatchStopped):
    """The suite job outlived --max-wait and is still running inside the Editor."""


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


def tree_status():
    """`git status --short` of the repo, compared before and after the run."""
    return subprocess.run(["git", "status", "--short"], cwd=REPO_ROOT, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", stdin=subprocess.DEVNULL).stdout


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
            raise SuiteTimedOut(f"the suite was still running after {waited} s (job {job_id.group(1)}); it keeps "
                                "running in the Editor — wait for it, then run `unity recompile`")

    return [message for seq, message in console_entries() if seq > before]


def suite_failures(messages, label):
    """Prints a run's verdict lines and returns its [FAIL] lines; raises when the run gave no verdict."""
    for message in messages:
        if FAIL_PATTERN.search(message) or SUMMARY_PATTERN.search(message) or NO_VERDICT_PATTERN.search(message):
            print("  " + message.splitlines()[0])
    blocker = next((m for m in messages if NO_VERDICT_PATTERN.search(m)), None)
    if blocker is not None:
        raise RuntimeError(f"{label}: no verdict — {NO_VERDICT_PATTERN.search(blocker).group(0)}")
    if not any(SUMMARY_PATTERN.search(m) for m in messages):
        raise RuntimeError(f"{label}: no suite summary line in the console — no verdict")
    return [m.splitlines()[0] for m in messages if FAIL_PATTERN.search(m)]


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


class Budget:
    """The run's total time; an entry starts only when its expected recompiles and suite runs still fit."""

    def __init__(self, seconds, max_wait):
        self.deadline = time.monotonic() + seconds
        self.max_wait = max_wait
        self.longest_compile = 0.0
        self.longest_suite = 0.0

    def left(self):
        return self.deadline - time.monotonic()

    def suite_wait(self):
        return max(1.0, min(self.max_wait, self.left()))

    def needed(self, baseline):
        """Seconds an entry is expected to take: two recompiles and its run, plus its baseline when not cached.

        Padded by half plus 5 s: a run cut off by the budget proves nothing."""
        if baseline is not None and baseline[0]:
            return 0  # a cached failed baseline aborts the entry without running anything
        suite = baseline[1] if baseline is not None else self.longest_suite
        runs = 1 if baseline is not None else 2
        return 1.5 * (2 * self.longest_compile + runs * suite) + 5


def timed_compile(budget):
    started = time.monotonic()
    try:
        return recompile()
    finally:
        budget.longest_compile = max(budget.longest_compile, time.monotonic() - started)


def timed_suite(suite, budget):
    started = time.monotonic()
    try:
        return run_suite(suite, budget.suite_wait())
    finally:
        budget.longest_suite = max(budget.longest_suite, time.monotonic() - started)


def load_batch(path):
    """Reads a batch file: a JSON list of {file, old, new, suite, expect?, label?} entries."""
    entries = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(entries, list) or not entries:
        raise RuntimeError("the batch file must hold a non-empty JSON list")
    for index, entry in enumerate(entries, 1):
        if not isinstance(entry, dict) or not all(isinstance(entry.get(key), str)
                                                  for key in ("file", "old", "new", "suite")):
            raise RuntimeError(f"batch entry {index}: needs file, old, new and suite as strings")
        if any(not isinstance(entry.get(key), (str, type(None))) for key in ("expect", "label")):
            raise RuntimeError(f"batch entry {index}: expect and label must be strings")
    return entries


def validate_entry(entry):
    """Rejects an entry that cannot run, before its baseline costs a suite run."""
    if not (REPO_ROOT / entry["file"]).is_file():
        raise RuntimeError(f"no such file: {entry['file']}")
    if entry.get("expect") is not None:
        try:
            re.compile(entry["expect"])
        except re.error as error:
            raise RuntimeError(f"invalid --expect regex: {error}")


def check_baseline(suite, budget, cache):
    """Runs each distinct suite once unmutated; raises when it is not cleanly green.

    The cache maps a suite to (error or None, seconds the baseline took)."""
    if suite not in cache:
        print(f"baseline: {suite}")
        started = time.monotonic()
        try:
            if suite_failures(timed_suite(suite, budget), "baseline"):
                raise RuntimeError("the baseline is already red — a red mutated run would prove nothing")
            cache[suite] = (None, time.monotonic() - started)
        except RuntimeError as error:
            cache[suite] = (str(error), time.monotonic() - started)
    if cache[suite][0]:
        raise RuntimeError(cache[suite][0])


def cycle(entry, budget):
    """One mutate → recompile → run → restore cycle; returns 0 red, 1 survived, 2 proves nothing."""
    file, expect = entry["file"], entry.get("expect")
    target = (REPO_ROOT / file).resolve()
    BACKUP_DIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%dT%H%M%S%f")
    backup = BACKUP_DIR / f"{file.replace('/', '__').replace(chr(92), '__')}.{stamp}.bak"
    original_hash = sha256(target)
    shutil.copyfile(target, backup)
    print(f"backup: {backup}")

    verdict, suite_running = 2, False
    try:
        mutate(target, entry["old"], entry["new"])
        mutated_hash = sha256(target)
        if mutated_hash == original_hash:
            raise RuntimeError("the mutation leaves the file unchanged")
        print(f"mutated {file}: {original_hash[:12]} -> {mutated_hash[:12]}")
        errors = timed_compile(budget)
        if errors:
            raise RuntimeError(f"the mutation does not compile ({errors} errors) — it proves nothing")

        fails = suite_failures(timed_suite(entry["suite"], budget), "mutated run")
        red = [line for line in fails if expect is None or re.search(expect, line)]
        verdict = 0 if red else 1
        if red:
            print("RED — the suite caught the mutation")
        elif fails:
            print(f"SURVIVED — {len(fails)} [FAIL] line(s) but none matched --expect")
        else:
            print("SURVIVED — the suite stayed green under the mutation")
    except SuiteTimedOut:
        suite_running = True
        raise
    except (RuntimeError, OSError, ValueError, subprocess.TimeoutExpired) as error:
        print(f"error: {error}")
    finally:
        shutil.copyfile(backup, target)
        restored = sha256(target) == original_hash
        print(f"restored {file}: checksum {'IDENTICAL' if restored else 'MISMATCH'}")
        if not restored:
            raise BatchStopped(f"{file} did not restore byte-identical; backup kept at {backup}")
        backup.unlink()
        if not suite_running and timed_compile(budget):
            raise BatchStopped("the restored code does not compile")
    return verdict


def run_entries(entries, budget, baseline=True):
    """Runs every entry; stops the batch on an unknown tree or Editor state. Returns the worst verdict."""
    names = {0: "RED", 1: "SURVIVED", 2: "NO PROOF", NOT_RUN: "NOT RUN"}
    leftovers = sorted(BACKUP_DIR.glob("*.bak")) if BACKUP_DIR.is_dir() else []
    if leftovers:
        print("STOPPED: PREVIOUS RUN WAS KILLED (or another prove_red run is live) — its mutation may still be in "
              "place. Compare each backup with its file (path = name before the timestamp, `__` = `/`), "
              "restore it if needed, then delete it:")
        for backup in leftovers:
            print(f"  {backup}")
        return 2

    status_before = tree_status()
    baselines, results = {}, []
    try:
        if timed_compile(budget):
            print("STOPPED: the unmutated tree does not compile")
            return 2
        for index, entry in enumerate(entries, 1):
            label = entry.get("label") or entry["file"]
            if len(entries) > 1:
                print(f"\n### [{index}/{len(entries)}] {label}")
            needed = budget.needed(baselines.get(entry["suite"]) if baseline else (None, budget.longest_suite))
            if needed > budget.left():
                print(f"STOPPED: time budget — {budget.left():.1f}s left, this entry needs about {needed:.1f}s;"
                      " run the rest as a new batch")
                results += [(rest.get("label") or rest["file"], NOT_RUN) for rest in entries[index - 1:]]
                break
            try:
                validate_entry(entry)
                if baseline:
                    check_baseline(entry["suite"], budget, baselines)
                verdict = cycle(entry, budget)
            except RuntimeError as error:
                print(f"error: {error}")
                verdict = 2
            except BatchStopped as error:
                print(f"STOPPED: {error}")
                results.append((label, 2))
                break
            results.append((label, verdict))
    finally:
        status_after = tree_status()
        if status_after != status_before:
            print("TREE CHANGED — `git status --short` before:\n" + (status_before or "  (clean)\n")
                  + "after:\n" + (status_after or "  (clean)\n"))

    if len(entries) > 1:
        print("\n=== prove-red summary ===")
        for label, verdict in results:
            print(f"  {names[verdict]:9} {label}")
        if len(results) < len(entries):
            print(f"  ({len(entries) - len(results)} entries not run: the batch stopped)")
    return min(2, max((verdict for _, verdict in results), default=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--batch", help="a JSON list of {file, old, new, suite, expect?, label?} mutations")
    parser.add_argument("--file", help="the source file to mutate, relative to the repo root")
    parser.add_argument("--old", help="the exact text to replace (must occur once)")
    parser.add_argument("--old-file", help="a file holding the text to replace")
    parser.add_argument("--new", help="the replacement text ('' deletes)")
    parser.add_argument("--new-file", help="a file holding the replacement text")
    parser.add_argument("--suite", help="the suite's menu path")
    parser.add_argument("--expect", default=None, help="regex a [FAIL] line must match to count as red")
    parser.add_argument("--no-baseline", action="store_true",
                        help="skip the unmutated run (only when the suite was just seen green on this tree)")
    parser.add_argument("--max-wait", type=int, default=600, help="seconds to wait for one suite run (default 600)")
    parser.add_argument("--budget", type=int, default=540, help="seconds for the whole run (default 540)")
    args = parser.parse_args()

    try:
        if args.budget <= 0:
            raise RuntimeError("--budget must be positive")
        if args.batch:
            if args.file or args.suite:
                raise RuntimeError("pass either --batch or --file/--suite, not both")
            entries = load_batch(args.batch)
        else:
            if not (args.file and args.suite):
                raise RuntimeError("--file and --suite are required without --batch")
            entries = [{"file": args.file, "old": read_snippet(args.old, args.old_file, "old"),
                        "new": read_snippet(args.new, args.new_file, "new"), "suite": args.suite,
                        "expect": args.expect}]
    except (RuntimeError, OSError, ValueError) as error:
        print(f"error: {error}")
        return 2
    try:
        return run_entries(entries, Budget(args.budget, args.max_wait), baseline=not args.no_baseline)
    except Exception:  # an uncaught error would exit 1, which reads as SURVIVED
        traceback.print_exc(file=sys.stdout)
        return 2


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
    sys.exit(main())
