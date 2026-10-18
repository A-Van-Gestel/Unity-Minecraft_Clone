---
name: afk-heartbeat
description: Keeps an unattended session alive while the user is away — a recurring CronCreate heartbeat that re-invokes the agent every 20 minutes so the prompt cache stays warm and finished background agents get picked up, plus the rules for background tasks and the final report. Use when the user says "I'm AFK", "I'll be away for an hour", "work autonomously", "keep the cache warm", "reset the heartbeat" or "set up the heartbeat".
---

# AFK heartbeat

Owns the mechanics of an unattended stretch: the heartbeat cron, how long a background task may run, and the
teardown. What the agent works on meanwhile is the task's own business (a bug, a benchmark harvest); this skill
only keeps the session moving.

A session cron is the only mechanism: `CronCreate` jobs live in the session and die with it, so no script or
settings file can pre-create one — re-run Step 1 in every session that needs it.

## Step 1 — Create the heartbeat

Load the schema (`ToolSearch` → `select:CronCreate,CronDelete`), then:

- `cron`: `4,24,44 * * * *` — every 20 minutes, off the :00/:30 marks.
- `recurring`: `true`.
- `prompt`: `Heartbeat: if a background agent is still running, reply with one line and keep waiting; otherwise
  continue the <task> work.` — name the task, so a heartbeat that fires after a context summary still knows
  what to resume.

Record the returned job id: Step 3 needs it. One heartbeat per session — when the user asks to "reset" it,
`CronDelete` an old one still listed in `CronList` before creating the new one.

## Step 2 — While the user is away

- **No background task longer than ~25 minutes.** A heartbeat only re-invokes an idle agent; split long batches
  (player runs, suite sweeps) so each finishes inside one heartbeat interval.
- **A heartbeat with an agent still running gets one line** ("X still running; waiting") and nothing else — no
  polling of the agent's output file.
- **Default every judgment call to your own recommendation** and list each one in the final report instead of
  stopping to ask; the user is not there to answer.
- Commits follow the user's authorization for the session, never more (CLAUDE.md, Execution Protocol).

## Step 3 — Teardown

`CronDelete` the heartbeat **before** writing the final report — a heartbeat left running keeps re-invoking a
session whose work is done. The final report covers what landed (commits), what ran, every judgment call made
alone, and the exact next step.
