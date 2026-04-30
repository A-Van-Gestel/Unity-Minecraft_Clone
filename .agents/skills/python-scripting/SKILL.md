---
name: python-scripting
description: Use the host's Python 3.14 environment for any task where Python is a better fit than C# — LUT generation, math/algorithm prototyping, data transforms, binary save-file inspection, repo-wide refactor scripts, log analysis, etc. Not limited to a fixed list of use cases.
---

# Python Scripting Protocol

This project assumes **Python 3.14** on the host. Reach for it whenever a problem is tedious, error-prone, or impractical to solve in C# — especially anything involving math validation, data crunching, raw text/code generation, or one-off automation against the repo or save files.

## Preflight (run once per session, before the first script)

The first time this skill is used in a session, verify Python is actually available and recent enough:

```bash
python --version
```

- Expected: `Python 3.14.x` (or newer minor of 3.14). Anything ≥ 3.12 is usable in practice; flag the mismatch to the user but proceed.
- On Windows, `python` may resolve to a Microsoft Store stub that opens an install prompt. If `python --version` hangs or returns nothing, try `py -3 --version` instead and use `py -3` for subsequent invocations.
- If Python is **not installed** (command not found, exit code ≠ 0): stop. Tell the user Python is required for this task and either ask them to install it (3.14 preferred) or propose a C#-only alternative if one is reasonable. Do not attempt to install Python yourself.
- If the version is older than 3.10: avoid `match` statements, PEP 604 union syntax (`int | str`), and other recent features, or ask the user to upgrade.

You only need to do this once per session — once you've confirmed Python is present, subsequent scripts in the same conversation can skip the check.

## When it's a good fit

Not an exhaustive list — use judgment. Examples include:

- **Burst LUT generation** — precomputing frozen arrays (face remaps, rotation matrices, etc.) emitted as C# source. Canonical example: `Tools/Python/generate_rotation_matrices.py` → `Assets/Scripts/Jobs/BurstData/BurstCustomMeshRotationUtility.cs`.
- **Math / algorithm prototyping** — 3D math, chunk boundary logic, voxel raycasting, bitwise packing, fluid CA, lighting BFS sketches. Validate before committing to C#.
- **Data transformation** — parsing CSV/JSON/XML/binary into raw C# code, asset lists, or migration scripts.
- **Save / region file inspection** — decoding our LZ4/GZip region binaries to debug serialization issues without spinning up the editor.
- **Repo-wide automation** — bulk renames, codemods, doc generation, scanning all `.cs` files for a pattern that's awkward in pure regex.
- **Validation & debugging** — quick sanity checks when reasoning about a complex bug (e.g., reproducing a flood-fill bug in 50 lines of Python before touching the Burst job).
- Anything else where Python is genuinely the right tool. If in doubt, ask.

## Where scripts live (HARD RULE)

- **NEVER place `.py` files under `Assets/`.** Anything in `Assets/` triggers Unity's asset import pipeline, generates `.meta` files, and gets indexed by the IDE/Burst toolchain. Python scripts are not Unity assets.
- **Persistent / reusable scripts** → `Tools/Python/<purpose>/script.py` at the repo root (sibling to `Documentation/`). Mirror the architectural area when relevant (e.g., `Tools/Python/Meshing/`, `Tools/Python/Serialization/`).
- **One-off throwaways** → inline in chat as a code block or Artifact. Do not commit ad-hoc scratch scripts to the repo.
- If the destination is unclear, ask the user where it should live before writing the file.

## Dependencies & virtual environments

- **Default to the standard library.** Python 3.14's stdlib (`math`, `json`, `itertools`, `struct`, `pathlib`, `re`, `csv`, `dataclasses`, `collections`, `array`, `binascii`, `gzip`, `lzma`, …) covers the vast majority of what we need. Prefer it.
- **If you genuinely need an external package** (`numpy`, `pillow`, `lz4`, etc.):
    1. Use a project-local virtual environment at `Tools/Python/.venv/` (gitignored — do not commit it).
    2. Record required packages in `Tools/Python/requirements.txt` (or a sibling `requirements.txt` next to the script if it's a self-contained tool).
    3. Ask the user to create/activate the venv and `pip install -r requirements.txt` before running. Do not silently mutate the system Python install.
    4. The script's header should document how to run it (venv path + activation + entry command).
- Never assume external packages are pre-installed. If you import one, you must have either confirmed it's in the stdlib or set up the requirements path above.

## Execution

- If you have terminal access, run the script directly and read the output yourself. Do not ask the user to paste output if you can run it.
- If output is large, write it to a file under `Tools/Python/output/` (gitignored) and read the file rather than dumping into the conversation.
- For scripts that emit C# code, write directly to the target `.cs` path so the user can review the diff in their editor.

## Script structure conventions

Every committed script under `Tools/Python/` should follow these baselines:

- **`if __name__ == "__main__":` guard** on the entry point — even for "single-purpose" scripts. Lets the script be importable later for testing or composition without side-effects.
- **`argparse` for any input** — paths, flags, output destinations. Avoid hardcoded paths inside `main()`; pass them as args with sensible defaults. Throwaway one-shots in chat can skip this.
- **Module docstring at the top** documenting: what the script does, what it reads, what it writes, and the exact run command (including venv activation if applicable). This is the first thing a future reader sees.
- **Determinism is mandatory.** Output must be byte-identical across runs on the same inputs. That means:
    - Seed any RNG explicitly (`random.seed(0)`, `np.random.default_rng(0)`).
    - Iterate dicts/sets in sorted order when their contents flow into output.
    - Pin float formatting (e.g., `f"{x:.10g}"`) — don't rely on `repr()` defaults that vary by platform/version.
    - No timestamps, usernames, absolute paths, or `time.time()` baked into generated output.
      Without determinism, regenerating a LUT produces noisy diffs that hide real changes and break code review.

## C# code generation conventions

If the Python script generates C# code, the script — not a downstream hand-edit pass — must produce output that already matches the project's C# style:

- **AUTO-GENERATED banner** at the top of every generated `.cs` file, matching the convention used by `Assets/Scripts/Data/BlockIDs.cs`:
  ```csharp
  // <auto-generated>
  //     This file is auto-generated by Tools/Python/<script_name>.py.
  //     DO NOT EDIT BY HAND — your changes will be overwritten on the next regeneration.
  //     To regenerate: python Tools/Python/<script_name>.py
  // </auto-generated>
  ```
  This stops humans (and future me) from hand-editing the file and silently losing work on the next regen.
- `s_camelCase` for `static readonly` arrays.
- Allman bracing.
- Trailing commas in array initializers.
- `private const` → `SCREAMING_CASE`, `public const` → `PascalCase`.
- XML docstrings on any generated public surface.

## Constraints

- **Do NOT replace Unity Editor tools.** Anything that touches `.prefab`, `.unity`, `.asset`, or `.meta` files, or that needs to walk Unity's serialization graph, must be a C# Unity Editor script — not Python. Python is for external data crunching, math, raw text/code generation, and inspection of binary blobs we wrote ourselves (region files, save data).
- **Do not silently install packages** into the system Python. Use the venv flow above and surface the requirement to the user.
- **Do not commit `.venv/` or large generated artifacts.** Add them to `.gitignore` if they aren't already covered.
