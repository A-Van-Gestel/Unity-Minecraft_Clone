# Chunk-pipeline, domain-reload & known-bug gates

Load when the diff touches `World.cs`, `WorldJobManager.cs`, `ChunkPoolManager.cs`,
a pooled type (`Chunk.cs`, `Data/ChunkData.cs`, `Data/ChunkSection.cs`,
`VisualizerChunkData.cs`), lighting / fluid / meshing / chunk-management code, or
adds a mutable `static`.

The chunk generation → lighting → meshing pipeline has a recurring deadlock and
stale-state history; these gates guard the invariants that history is made of.
Route to `chunk-lifecycle` (gate 10) and `validation-driven-bugfix` /
`run-validation-suite` (gate 12).

**Source of truth: `.agents/rules/chunk-pipeline.md` and
`.agents/rules/pool-reset-safety.md`.** Gate 10 summarizes both; the rules carry
the named flags, the two distinct readiness gates, and the pooled-type reset table
plus its verification checklist. Read them when gate 10 fires — `pool-reset-safety.md`
in particular, whenever a field is added to a pooled type.

Each gate carries **what fails**, **how to check**, **severity**, and its
delta/absolute nature.

---

## Gate 10 — Chunk-pipeline invariant broken

**What fails.** A change in `World.cs`, `WorldJobManager.cs`, `ChunkPoolManager.cs`,
or `ChunkData.cs` breaks one of the pipeline's ordering/ownership invariants:

- **flag pairing** — a state flag set without its clear (or cleared without its
  set), so a chunk is left mid-transition and never advances
- **gate ordering** — meshing or lighting scheduled before its precondition
  (`AreNeighborsReadyAndLit` and friends) holds, so work runs on a half-built
  neighbourhood
- **conflated readiness gates** — `AreNeighborsDataReady` (neighbor terrain exists,
  for initial lighting) swapped for `AreNeighborsReadyAndLit` (neighbors fully lit
  and stable, for meshing), or vice versa. They are different gates
- **off-main-thread flag mutation** — state flags are mutated only on the main
  thread in `World.Update()`; a job reads a snapshot. A job that writes a flag is
  a race
- **immediacy assumption** — the pipeline throttles jobs per frame, so code that
  assumes a scheduled job ran by the next line fails under load
- **pool recycle safety**, two distinct failures: a chunk/buffer returned to the
  pool while a job still holds its `NativeArray` (aliasing), **and** a transient
  field added to a pooled type with no matching line in `Reset()`/`Release()` —
  the recycled object inherits stale state. The second is the one with a bug
  history (`RemainingEdgeCheckRounds`); note the carve-outs in
  `pool-reset-safety.md`: monotonic counters *increment* rather than reset,
  counters with a non-zero default reset to that default, and the `ChunkData`
  lighting flags must be reset **through the property**, not the backing field

**How to check.** Read the changed region with the surrounding scheduling code in
view — a hunk alone will not show you the ordering. `codegraph callers <method>`
(CLI, via Bash) tells you which pipeline stage reaches it. Cross-check the
`chunk-pipeline` rule for the specific flag/gate names; consult `chunk-lifecycle`
for the full pipeline contract. The failure to describe is the *race or deadlock*,
not the line: "the chunk is recycled before the mesh job completes, so a stale
`NativeArray` is meshed and the pool double-issues it".

**Absolute** within changed pipeline code — these are correctness invariants, not
additions to count.

**Severity.** Blocker for a recycle-aliasing or deadlock path (data race, frozen
chunks). High for a flag-pairing slip that self-heals on a later tick. If you
cannot prove the race from source, mark it uncertain and name the validation
suite that would settle it (gate 12 / `run-validation-suite`).

---

## Gate 11 — Mutable `static` added without a per-play reset

**What fails.** This project runs with *Enter Play Mode → Reload Domain*
**disabled**, so `static` fields are **not** re-initialized between play sessions.
The diff adds a mutable `static` (counter, cache, singleton back-reference, event
list) and either:

- gives it **no per-play reset** — a field initializer (`= 0`) is not enough, it
  only runs on domain reload, so a stale value from the last session leaks into
  the next run; or
- adds a **second `[RuntimeInitializeOnLoadMethod]`** to a class that already has
  one — Rider's **UDR0005** — instead of folding the reset into the existing one
  (e.g. `World.DomainReset`).

**How to check.** For each added `static`, ask: is it mutated across a play
session, and is it zeroed by code that runs each play start? `const`, `readonly`,
and `[ThreadStatic]` are exempt (never mutated across sessions). Rider
`lint_files` reports UDR0004/UDR0005 directly — corroborate with it when the IDE
is running; if not, flag from the source (uncertain) and put Rider on `Not
verified`. A static the diff *removed a reset from* is gate 4.

**Delta-based.**

**Severity.** High — a leaked static is a heisenbug that only appears on the
second play session and is miserable to trace. Blocker if the leaked state is a
singleton back-reference or an event list that double-subscribes.

---

## Gate 12 — Change collides with a known bug

**What fails.** The diff touches lighting, fluids, meshing, or chunk management
and either reintroduces something recorded as fixed in
`Documentation/Bugs/_FIXED_BUGS.md`, or collides with an open entry in the
matching `Documentation/Bugs/*.md` (e.g. `LIGHTING_BUGS.md`, `FLUID_BUGS.md`,
`MESHING_BUGS.md`, `CHUNK_MANAGEMENT_BUGS.md`, `JOB_SYSTEM_BUGS.md`).

**How to check.** Identify the subsystem from the changed paths, then scan that
system's bug doc and `_FIXED_BUGS.md` for the symptom the change could revive.
This is a *cross-check*, not a full read — you are looking for a named collision,
not auditing the whole doc. If the diff **fixes** an open bug, that is not a
finding; note it and route to `archive-fixed-bug` (after the user confirms). If
the subsystem has a validation suite, `validation-driven-bugfix` /
`run-validation-suite` is how you prove the change did not revive the entry.

**Absolute** (any collision the changed subsystem's docs describe).

**Severity.** High — a known-fixed bug walking back in is a regression the team
already paid to fix once. Medium if the collision is with an *open* bug and the
change merely fails to fix it rather than making it worse.
