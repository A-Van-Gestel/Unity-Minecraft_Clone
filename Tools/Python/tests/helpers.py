"""Shared fixtures for the helper-script tests: throwaway git repos and script runners.

Every test builds its own repository under the system temp directory, so the suite never touches this
checkout's git state. The repos pin the settings the scripts are sensitive to (LF line endings, a known
branch name, an identity for commits) and fake an upstream with `update-ref`, so "pushed" vs "unpushed"
is deterministic without a remote.
"""
import os
import shutil
import subprocess
import sys
import tempfile

TOOLS_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)  # lets tests import a script's functions directly


def script_path(name):
    return os.path.join(TOOLS_DIR, name)


def run_script(name, *args, cwd=None):
    """Runs a helper script with this interpreter; returns (exit code, stdout + stderr as text)."""
    result = subprocess.run([sys.executable, script_path(name), *args], cwd=cwd, capture_output=True)
    output = (result.stdout + result.stderr).decode("utf-8", "replace")
    return result.returncode, output


class TempRepo:
    """A disposable git repository. Use as a context manager, or call `close()`."""

    def __init__(self):
        self.root = tempfile.mkdtemp(prefix="tools-test-")
        self.git("init", "-q", "-b", "main")
        for key, value in (("user.name", "Test"), ("user.email", "test@example.invalid"),
                           ("core.autocrlf", "false"), ("core.longpaths", "true"),
                           ("commit.gpgsign", "false")):
            self.git("config", key, value)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        # Remove directory links first: never let a tree delete follow one out of the sandbox.
        for directory, names, _ in os.walk(self.root, topdown=True):
            for name in list(names):
                path = os.path.join(directory, name)
                if os.path.islink(path) or (hasattr(os.path, "isjunction") and os.path.isjunction(path)):
                    os.rmdir(path) if os.name == "nt" else os.unlink(path)
                    names.remove(name)
        shutil.rmtree(self.root, ignore_errors=True)

    def path(self, relative):
        return os.path.join(self.root, *relative.split("/"))

    def git(self, *args, check=True):
        result = subprocess.run(["git", *args], cwd=self.root, capture_output=True)
        if check and result.returncode != 0:
            raise AssertionError(f"git {' '.join(args)} failed: {result.stderr.decode('utf-8', 'replace')}")
        return result.stdout.decode("utf-8", "replace")

    def write(self, relative, text):
        full = self.path(relative)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "wb") as handle:
            handle.write(text.encode("utf-8"))

    def read(self, relative):
        with open(self.path(relative), "rb") as handle:
            return handle.read().decode("utf-8")

    def commit(self, message):
        self.git("add", "-A")
        self.git("commit", "-q", "-m", message)
        return self.git("rev-parse", "HEAD").strip()

    def mark_pushed(self):
        """Pretends everything up to HEAD is pushed (the scripts fall back to origin/<branch>)."""
        self.git("update-ref", "refs/remotes/origin/main", "HEAD")

    def run(self, name, *args, cwd=None):
        return run_script(name, *args, cwd=cwd or self.root)


def unity_meta(guid):
    """A script `.meta` in Unity's format — two of these differ only in their guid line."""
    return ("fileFormatVersion: 2\n"
            f"guid: {guid}\n"
            "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n"
            "  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n"
            "  assetBundleVariant: \n")


def numbered_lines(count, replace=None):
    """`count` lines `1`..`count`, with optional {line number: new text} replacements."""
    replace = replace or {}
    return "".join(f"{replace.get(n, n)}\n" for n in range(1, count + 1))
