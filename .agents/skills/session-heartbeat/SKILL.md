---
name: session-heartbeat
description: Keeps a session alive while it waits or works unattended. A recurring CronCreate heartbeat re-invokes the agent every 20-40 minutes, so the prompt cache stays warm and finished background work (Validate All, player runs, benchmarks, background agents) gets picked up. Also covers the rules for background tasks and the final report. Use when the user says "I'm AFK", "I'll be away", "work autonomously", "keep the cache warm", "set up / reset the heartbeat", and before any background job estimated at over ~15 minutes.
---

# Session heartbeat

Owns the mechanics of waiting: the heartbeat cron, how long a background task may run, and the teardown. What the
session works on meanwhile is the task's own business. This skill only keeps the session moving.

A session cron is the only mechanism. `CronCreate` jobs live in the session and die with it, so no script or
settings file can create one ahead of time. Run Step 1 again in every session that needs it.

A user-level skill of the same name may exist on some machines; it takes precedence there. Keep the cron and the
rules below in step with it.

## When to set one

- The user leaves the session unattended for a while ("AFK", "back in an hour", "work autonomously").
- **Before a background job you estimate at over ~15 minutes.** Estimate from the job's last known duration:
  `Validate All` (≈ 3.5 min) needs none on its own; player builds and `-mc-run` sessions, benchmark sweeps and
  long implementer or player-runner agents usually do.
- Several back-to-back jobs whose total passes ~15 minutes count too.

## Step 1: create the heartbeat

Load the schema (`ToolSearch` → `select:CronCreate,CronDelete,CronList`), then:

- `cron`: `9,49 * * * *` (gaps of 40 and 20 minutes, off the :00/:30 marks). The prompt cache stays warm for
  about an hour, and every wake-up re-reads the whole context, so fewer beats are cheaper. A cron cannot express
  an even 45-minute period, and the scheduler adds a fixed per-job offset, so stay well under 60 minutes.
- `recurring`: `true`.
- `prompt`: `Heartbeat: if a background job is still running, reply with one line and keep waiting; otherwise
  continue the <task> work.` Name the task, so a heartbeat that fires after a context summary still knows what to
  resume.

Record the returned job id, because Step 3 needs it. Keep one heartbeat per session. When the user asks to "reset"
it, `CronDelete` any old one still listed in `CronList` before creating the new one.

## Step 2: while it runs

- **A heartbeat that finds a job still running gets one line** ("X still running; waiting") and nothing else. Do
  not poll the job's output file.
- **Split batches you control** (player runs, suite sweeps) so each piece finishes inside about one heartbeat
  interval (20-40 minutes). A single job that cannot be split may run longer: the heartbeat keeps the session warm.
- **When the user is away, default every judgment call to your own recommendation** and list each one in the final
  report instead of stopping to ask.
- Commits follow the user's authorization for the session, never more (`CLAUDE.md`, Execution Protocol).

## Step 3: teardown

While the user is away, the heartbeat **outlives the work**: write the final report and leave it running, so the
cache stays warm until the user returns. A cold cache invites auto-compaction, which loses the detail the user's
end-of-session recap ("is anything in this session not yet written down?") needs. `CronDelete` it only when the
user says they are back. When the user was present all along and only a long job needed covering, delete it as
soon as that job is over. The final report covers what landed, what ran, every judgment call made alone, and the
exact next step.
