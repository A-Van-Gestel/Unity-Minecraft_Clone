# Pause & Simulation Halt Semantics

**Version:** 1.0  
**Date:** 2026-08-10  
**Status:** **Proposed design — not implemented.**  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> The project has **no pause**. Opening the pause menu blocks input and unlocks the cursor; the
> world keeps generating chunks, flowing fluids, and ticking blocks behind it. Nothing writes
> `Time.timeScale` anywhere in the codebase. This doc scopes what a real pause would have to halt,
> and its central finding is that **`Time.timeScale = 0` is the wrong lever here** — the engine's
> asynchronous job pipeline does not live on the Unity time step, so a timeScale pause would freeze
> the visible world while chunk generation, meshing, and lighting carried on regardless. The
> proposed lever is an explicit `World.IsSimulationPaused` gate applied at the pass level (§4).

**Audited:** 2026-08-10, at commit `2a33e7a7` (branch `feat/world-scaling`), plus the uncommitted
RF-1 Phase 1 working tree. Reviewed: `WorldUIManager` (`IsPauseMenuOpen`, `InUI`, `UpdateUIState`),
`PauseMenuController`, `World.Update`'s pass sequence, `WorldTimeManager.Tick`, `ProcessTickUpdates`,
and a repo-wide search for `Time.timeScale` (**zero writes**, one comment in `TooltipTrigger.cs:67`).
Findings were verified in code, not assumed.

**Relationship to other documents:**

- [`../Architecture/COMMAND_CONSOLE_SYSTEM.md`](../Architecture/COMMAND_CONSOLE_SYSTEM.md) — §2's
  pause-semantics row is the authoritative record of today's behaviour and of RF-1's one exception;
  it must be updated in the same commit as any phase here.
- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  — RF-1 shipped the day/night clock and, with it, the first and only system that stops for the
  pause menu (§2). This doc exists to resolve the inconsistency that created.
- [`../Architecture/CHUNK_LIFECYCLE_PIPELINE.md`](../Architecture/CHUNK_LIFECYCLE_PIPELINE.md) — the
  pass sequence a pause gate has to intercept, and the readiness invariants it must not violate.
- [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) — the
  P-4/P-9 budgets are per-frame; §4.2 covers what a paused frame does to their accounting.

---

## 1. Goals & non-goals

**Goals**

- One coherent answer to "what does pausing mean" that every system follows.
- A pause that is *safe* mid-pipeline: no half-finished chunk transitions, no dropped job results,
  no stalled readiness gates on resume.
- Preserve the existing input/cursor behaviour, which works and is not in question.

**Non-goals (v1)**

- Pausing during multiplayer — no networking exists.
- Pausing audio; the sound engine is unbuilt (see `SOUND_ENGINE_DESIGN.md`).
- Any UI change to the pause menu itself. This is about simulation state, not presentation.
- Deferred to v2 (§6): pausing on focus loss, and a distinct "background/AFK throttle" mode.

---

## 2. Current state

| Area | Behaviour today |
|-----------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Time.timeScale` | **Never written.** Repo-wide search finds only a comment. Unity's time step runs at full speed always. |
| Input / cursor | `WorldUIManager.InUI = IsCreativeInventoryOpen \|\| IsPauseMenuOpen` gates player input and cursor lock. This is the entire extent of "pause" today. |
| Chunk generation | Runs. `World.Update` drives `ProcessGenerationJobs` → `DrainGenerationRequests` every frame regardless of UI state. |
| Meshing / lighting | Runs. Same pass sequence, same budgets. |
| Fluids / block ticks | Run. `ProcessTickUpdates` accumulates `Time.deltaTime` against `VoxelData.TickLength`. |
| Day/night clock | **Halts** while `IsPauseMenuOpen` — `World.AdvanceWorldTime`, added by RF-1 (2026-08-10). The only system that stops, and the reason this doc exists. |
| Physics | Runs. `VoxelRigidbody` integrates in `FixedUpdate`. |
| Cloud drift / sway | Run. Both are shader-time driven (`FoliageSway`, `CloudShader`), so they ignore any C#-side gate by construction. |

**The inconsistency to resolve.** RF-1 was asked for "a full time freeze when paused" and delivered
exactly that, but the request was made under the reasonable assumption that pausing already stopped
the world. It does not. Time-of-day is now the sole exception, which is arguably worse than either
consistent answer: a player who opens the menu at dusk returns to the same dusk, with a fluid column
that has finished draining and terrain that has finished generating.

---

## 3. Why `Time.timeScale = 0` is the wrong lever

The obvious implementation is one line in `WorldUIManager.IsPauseMenuOpen`. It is rejected:

### Option A — `Time.timeScale = 0` (rejected)

- ✅ One line; halts `Update`-driven `deltaTime` accumulation and all `FixedUpdate` physics for free.
- ❌ **Does not stop the job pipeline.** Chunk generation, meshing, and lighting are scheduled as
  Burst jobs and drained by passes that run on *frame* cadence, not on scaled time. `Update` still
  fires every frame at `timeScale = 0`; only `Time.deltaTime` reads zero. So the world would keep
  streaming and remeshing behind a "paused" menu — the exact confusion this doc is resolving, just
  relocated.
- ❌ **Silently changes the P-4/P-9 budget accounting.** The per-frame ms ceilings and the
  FPS-cap-proportional scaling read wall-clock time; a `timeScale` of 0 leaves them intact while
  every `deltaTime`-derived input to them goes to zero, which is an untested regime.
- ❌ Breaks any UI that animates on scaled time, and makes `TooltipTrigger`'s existing
  `unscaledDeltaTime` workaround load-bearing rather than incidental.

### Option B — explicit `World.IsSimulationPaused` gate ✅ **CHOSEN**

- ✅ Halts exactly what we choose to halt, at the pass level, where the pipeline invariants are
  already understood and documented.
- ✅ Leaves wall-clock budgets, UI animation, and the editor profiler untouched.
- ✅ Testable headlessly — a bool on `World` is drivable from a validation scenario, where
  `Time.timeScale` is not.
- ❌ More call sites than one line, and every *future* pass must remember the gate. §4.1 mitigates
  this by gating at the sequence level rather than inside each pass.

### Option C — halt nothing, revert RF-1's clock gate (rejected, but the cheap fallback)

- ✅ Zero work; restores consistency immediately by removing the exception.
- ❌ Loses a behaviour the project owner explicitly asked for, and leaves "pause" meaning nothing
  at all.
- *Kept on the table as the fallback if PA-1 is never scheduled: consistency by subtraction beats a
  single arbitrary exception.*

---

## 4. Proposed design

### 4.1 The gate

`World` gains `public bool IsSimulationPaused { get; private set; }`, set from `WorldUIManager`'s
pause-menu transition (not from `InUI` — the inventory and console deliberately keep running, §5).

`World.Update` splits into an **always-run** prologue and a **pausable** simulation body:

- **Always runs:** floating-origin re-anchor, `AssertPlayerNearOrigin`, chunk-border visualization,
  `SetGlobalLightValue` (the globals must stay published or the paused frame renders stale), and
  the teleport-hold release check.
- **Pausable:** `WorldTimeManager.Tick`, the generation/meshing/lighting pass sequence,
  `ProcessTickUpdates`, and the fluid passes.

Gating the *sequence* rather than each pass means a new pass added later is paused by default —
the safe direction to fail.

### 4.2 Draining, not abandoning

A pause must not strand in-flight work. Jobs already scheduled are **drained to completion** on the
pausing frame rather than left half-processed:

- Scheduled jobs whose results are pending stay enrolled; the completion passes run one more time.
- No new work is *admitted* (`DrainGenerationRequests` is gated), so the in-flight set shrinks to
  empty and stays there.
- Budget accounting: a paused frame reports no pass participation. The §7.1 telemetry participation
  denominator must exclude paused frames, or a long pause skews every stop-reason verdict.

### 4.3 What resume must guarantee

- No chunk sits in a state that requires a pass that was skipped — satisfied by §4.2's drain.
- `WorldTimeManager` resumes without a jump: `Tick` is simply not called while paused, and its
  sub-tick residue is untouched, so no time accumulates and none is lost.
- The physics arrival-hold flag (`VoxelRigidbody.isTeleportHeld`) is orthogonal and must not be
  cleared by a pause.

---

## 5. What deliberately does not pause

| System | Rationale |
|---------------------------|--------------------------------------------------------------------------------------------------------------------------|
| Console (`/`) open | The console is a debugging surface; freezing the world while typing would make it useless for observing live behaviour. |
| Creative inventory open | Long-standing behaviour, and the inventory is used mid-flight. Changing it is out of scope. |
| Cloud drift, foliage sway | Shader-time driven; a C# gate cannot reach them without a new "paused time" global. Cosmetic, and motion reads as alive. |
| Editor play-mode pause | Unity's own pause already stops `Update` entirely; no engine involvement needed. |

---

## 6. Phased implementation plan

| Phase    | Scope                                                                                                              | Effort | Depends on |
|----------|--------------------------------------------------------------------------------------------------------------------|:------:|------------|
| **PA-0** | `World.IsSimulationPaused` + the `Update` prologue/body split, with only `WorldTimeManager.Tick` gated (behaviour-identical to RF-1's shipped gate, but through the new seam). Validation: a scenario driving the flag directly. |   🟢   | —          |
| **PA-1** | Gate the tick/fluid passes. Validation: fluid column does not advance across a paused span; resumes identically.     |   🟡   | PA-0       |
| **PA-2** | Gate the generation/meshing/lighting sequence incl. §4.2's drain contract. Validation: no chunk left mid-transition; `Validate All` green; the `chunk-lifecycle` invariants re-checked.                                          |   🔴   | PA-1       |
| **PA-3** | Telemetry: exclude paused frames from the participation denominator (§4.2).                                          |   🟢   | PA-2       |

**Validation baselines are built alongside each phase.** PA-0/PA-1 extend the World Clock and
Behavior suites; PA-2 belongs to the Pipeline Backpressure and Meshing suites, whose fixtures
already drive `World.Update`-equivalent pass sequences headlessly.

---

## 7. Constraint compliance

| Constraint                | How this design satisfies it                                                                                   |
|---------------------------|------------------------------------------------------------------------------------------------------------------|
| Packed-`uint` voxel data  | Untouched — no voxel, light, or section data is read or written by a pause gate.                               |
| Burst job compatibility   | The gate is main-thread managed state; no job struct changes. Jobs are drained, never cancelled mid-flight.     |
| No hot-path GC            | One bool read per pass sequence per frame. No allocation.                                                       |
| Pooling                   | Unaffected; the pool-prune linger window is wall-clock based and keeps running (correct — a pause is not demand). |
| Serialization             | **No on-disk change.** Pause is transient session state and is deliberately not persisted.                      |

---

## 8. Extension roadmap

| Version | Item                                                                                                       |
|---------|--------------------------------------------------------------------------------------------------------------|
| v2      | Pause on application focus loss (`OnApplicationFocus`), gated by a setting — desirable on laptops, but needs care not to pause during an alt-tabbed benchmark capture. |
| v2      | A distinct **background throttle** (reduced tick rate rather than full halt) for AFK, reusing P-4's FPS-cap-proportional ceiling scaling rather than the pause gate.    |
| v3      | Pausing audio and ambience once `SOUND_ENGINE_DESIGN.md` ships.                                            |

---

## Document History

* **v1.0** - Initial design. Filed out of RF-1 (day/night cycle), which introduced the project's first
  and only pause-aware system and thereby surfaced the inconsistency this doc scopes. Central decision:
  `Time.timeScale` is rejected as the lever (§3) because the job pipeline is frame-driven, not
  scaled-time-driven, so it would freeze the visible world while streaming continued underneath.

---

**Last Updated:** 2026-08-10  
**Next Review:** when PA-0 is scheduled — re-verify §2's current-state table against the code first,
particularly whether any system beyond the day/night clock has since become pause-aware.
