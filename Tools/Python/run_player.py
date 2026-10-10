"""Run one automated session in a built player and report how it ended: exit code, report path, a saved Player.log.

WHAT THIS DOES
    Launches a built player (`Minecraft Clone.exe`) with the given `-mc-*` launch arguments, always adding
    `-force-d3d11`, `-mc-mute` and `-mc-quit` unless they are already there. Waits up to --timeout minutes and kills
    the process on expiry. Then copies the run's Player.log next to the other runs as `<tag>_Player.log`, finds the
    last `[Launch] Run finished: report <path>, exit code <n>` line, prints one result line and appends it to
    `runs.txt`. On a failed run it also prints the log's last --tail lines.

WHAT IT READS / WRITES
    Reads `ProjectSettings/ProjectSettings.asset` (company and product name, which locate Player.log under
    %USERPROFILE%/AppData/LocalLow) and the player's Player.log. Writes `<out>/<tag>_Player.log` and appends to
    `<out>/runs.txt`. Never touches the build folder or the player's settings.json.

WHY IT EXISTS
    Each launch overwrites Player.log, a player can hang after quitting, and the report path is only in the log, so
    every unattended run needs the same launch, timeout, copy and parse steps; this script is the one place they
    live, so no run skips the timeout or reads another launch's log.

RUN
    python Tools/Python/run_player.py --exe "<build>/Minecraft Clone.exe" --tag <name> [--timeout 15]
        [--out <folder>] [--tail 30] -- -mc-run startup-new -mc-set perfMonitorTier=Frame

    Everything after `--` goes to the player. --out defaults to Tools/Python/output/player_runs (gitignored).

EXIT CODES
    0  the log records a finished run with exit code 0 and a report — also when the player then had to be killed,
       since a player occasionally freezes after Application.Quit with its report already complete
    1  anything else: the run failed or wrote no report, the player was killed before reporting, or Player.log was
       not written by this launch
"""

import argparse
import re
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
PROJECT_SETTINGS = REPO_ROOT / "ProjectSettings" / "ProjectSettings.asset"
DEFAULT_OUT = REPO_ROOT / "Tools" / "Python" / "output" / "player_runs"
DEFAULT_TIMEOUT_MINUTES = 15.0
DEFAULT_TAIL_LINES = 30
SECONDS_PER_MINUTE = 60.0

# Flags every unattended run carries; each is added only when the caller did not pass it.
REQUIRED_FLAGS = ["-force-d3d11", "-mc-mute", "-mc-quit"]
GRAPHICS_API_PREFIX = "-force-"

RUN_FINISHED = re.compile(r"\[Launch\] Run finished: report (?P<report>.+?), exit code (?P<code>-?\d+)")
COMPANY = re.compile(r"^\s*companyName:\s*(?P<value>.+?)\s*$", re.MULTILINE)
PRODUCT = re.compile(r"^\s*productName:\s*(?P<value>.+?)\s*$", re.MULTILINE)


def player_arguments(game_args):
    """The player's arguments: the caller's, plus each required flag the caller did not already pass."""
    args = list(game_args)
    for flag in REQUIRED_FLAGS:
        # Any -force-<api> is a graphics-API choice; the caller's wins over the D3D11 default.
        if flag.startswith(GRAPHICS_API_PREFIX) and any(a.startswith(GRAPHICS_API_PREFIX) for a in args):
            continue
        if flag not in args:
            args.append(flag)
    return args


def player_log_path(project_settings_text, home):
    """Player.log for the project's company and product names, under the given home folder."""
    company = COMPANY.search(project_settings_text)
    product = PRODUCT.search(project_settings_text)
    if company is None or product is None:
        raise ValueError("companyName / productName not found in ProjectSettings.asset")
    return home / "AppData" / "LocalLow" / company.group("value") / product.group("value") / "Player.log"


def parse_run_finished(log_text):
    """The last 'Run finished' line as (report path or None, exit code), or None when the log has none."""
    matches = list(RUN_FINISHED.finditer(log_text))
    if not matches:
        return None
    last = matches[-1]
    report = last.group("report").strip()
    return (None if report == "(none)" else report), int(last.group("code"))


def log_signature(log_path):
    """The log's (modification time in ns, size), or None when it does not exist."""
    if not log_path.is_file():
        return None
    stat = log_path.stat()
    return stat.st_mtime_ns, stat.st_size


def run_process(command, timeout_seconds):
    """Runs the command; returns (process exit code or None, killed on timeout)."""
    # The player echoes its whole log to the console; Player.log keeps all of it, so the caller sees only the result.
    process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        return process.wait(timeout=timeout_seconds), False
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()
        return None, True


def tail(text, lines):
    """The last `lines` lines of the text."""
    return "\n".join(text.splitlines()[-lines:])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--exe", required=True, type=Path, help="the built player executable")
    parser.add_argument("--tag", required=True, help="name for this run's saved log and result line")
    parser.add_argument("--timeout", type=float, default=DEFAULT_TIMEOUT_MINUTES, help="minutes before the kill")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT, help="folder for saved logs and runs.txt")
    parser.add_argument("--tail", type=int, default=DEFAULT_TAIL_LINES, help="log lines printed on a failed run")
    parser.add_argument("game_args", nargs=argparse.REMAINDER, help="'--' then the player's arguments")
    args = parser.parse_args(argv)

    game_args = args.game_args[1:] if args.game_args[:1] == ["--"] else args.game_args
    if not args.exe.is_file():
        print(f"error: no player at {args.exe}", file=sys.stderr)
        return 1

    log_path = player_log_path(PROJECT_SETTINGS.read_text(encoding="utf-8"), Path.home())
    command = [str(args.exe)] + player_arguments(game_args)
    before = log_signature(log_path)
    started = time.time()
    exit_code, killed = run_process(command, args.timeout * SECONDS_PER_MINUTE)
    elapsed = int(time.time() - started)

    # A launch that died before opening its log leaves the previous run's Player.log behind, unchanged.
    after = log_signature(log_path)
    fresh = after is not None and after != before
    log_text = log_path.read_text(encoding="utf-8", errors="replace") if fresh else ""
    args.out.mkdir(parents=True, exist_ok=True)
    saved_log = args.out / f"{args.tag}_Player.log"
    if fresh:
        saved_log.write_text(log_text, encoding="utf-8")

    finished = parse_run_finished(log_text)
    succeeded = finished is not None and finished[1] == 0 and finished[0] is not None
    process_state = "killed (timeout)" if killed else f"exit {exit_code}"
    if finished is None:
        outcome = "no 'Run finished' line" if fresh else "Player.log not written by this launch"
    else:
        outcome = f"run exit {finished[1]}, report {finished[0] or '(none)'}"

    line = (f"{datetime.now():%Y-%m-%d %H:%M:%S} | {args.tag} | {process_state} | {elapsed}s | {outcome} | "
            f"log {saved_log if fresh else '(none)'} | args {' '.join(command[1:])}")
    print(line)
    with (args.out / "runs.txt").open("a", encoding="utf-8") as runs:
        runs.write(line + "\n")

    if not succeeded and log_text:
        print(f"--- last {args.tail} lines of Player.log ---")
        print(tail(log_text, args.tail))
    return 0 if succeeded else 1


if __name__ == "__main__":
    sys.exit(main())
