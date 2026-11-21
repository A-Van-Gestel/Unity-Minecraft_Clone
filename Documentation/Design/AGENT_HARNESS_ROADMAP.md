# Agent Harness Roadmap

**Version:** 1.0  
**Date:** 2026-11-21  
**Status:** Open backlog. Items are removed (archived) when implemented and verified.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production) — agent tooling

> Unbuilt work on the `voxel-engine` agent harness described in
> [`../Architecture/AGENT_HARNESS_SYSTEM.md`](../Architecture/AGENT_HARNESS_SYSTEM.md): an IL2CPP Master
> build (`AH-3`), menus and world management (`AH-4`), pathfinding, and the open build-size question
> (`§8 Q4`). **Each starts only on a concrete need, as its own session.** The `AH-*` ID space continues from
> the design that built the harness; `AH-0`…`AH-2` are closed and indexed in the Architecture doc.

**Audited:** 2026-11-21, at commit `df782e86` (branch `feat/agent-harness`).
Split out of `Design/AGENT_HARNESS.md` v1.3 at its promotion. Each "What exists today" line was checked
against the code this session (`AgentServer.cs`, `AgentSession.cs`, `Assets/link.xml`,
`ProjectSettings/ProjectSettings.asset`, `WorldSelectMenu.cs`, `PauseMenuController.cs`); the plans
themselves are the design's decided directions, carried over as intent.

**Relationship to other documents:**

- [`../Architecture/AGENT_HARNESS_SYSTEM.md`](../Architecture/AGENT_HARNESS_SYSTEM.md) — the implemented
  harness; its §9 lists the limitations these items would lift.
- [`UNITY_CLI_EXTENSIONS_ROADMAP.md`](UNITY_CLI_EXTENSIONS_ROADMAP.md) — the bridge-side backlog; `UC-7`
  closed with `AH-2`.

---

## Master summary

| ID        | Item                                                               | Effort | Depends on | Status |
|-----------|--------------------------------------------------------------------|:------:|------------|--------|
| **AH-3**  | IL2CPP Master: release-server spike, file-queue fallback           |   🔴   | AH-2       | —      |
| **AH-4**  | Shared menu service; `menu` / `world` commands beyond agent worlds |   🟡   | AH-0       | —      |
| **v2**    | Pathfinding for `move_to`                                          |   🟡   | AH-1       | —      |
| **§8 Q4** | Narrower `Unity.Pipeline` stripping root                           |   🟢   | AH-2       | —      |

*Status: `—` not started · `In progress` · `✅ YYYY-MM-DD` complete · `⏸️ YYYY-MM-DD` deliberately not
implemented · `⛔ Superseded YYYY-MM-DD — <by what>`.*

---

## 1. AH-3 — IL2CPP Master

**What exists today.** Only `ENABLE_RUNTIME_PIPELINE_DEV` is defined, so a Production or Master player has
no runtime server, and `-mc-run agent` logs the refusal and fails the run (`AgentServer.TryStart`'s `#else`
branch).

**Decided direction.** Spike the experimental release server first, gated behind the same `-mc-run agent`
switch as `AH-2`: a dedicated `Windows - Agent (Master)` build profile, `[Preserve]` on the command classes
plus a `link.xml` entry (Standalone managed stripping is Medium), build, `--runtime-path` smoke. If it
fails, the fallback transport is a file queue (`-mc-agent <inbox>`: one request file to one response file,
a main-thread poll, `JsonUtility` DTOs) behind a define present only in that profile, reusing
`AgentWorldOps`. Master safety: keep-in-memory is impossible there (the `Dev` settings are not read), so
session worlds always use Throwaway under the isolated root. `script eval` is unavailable in Master per the
package docs, which is why typed commands matter most there. A harness-enabled Master build is not a
perf-capture build.

**Open questions it settles:**

1. **§8 Q1 (A3) — build-profile defines vs the package assembly constraints.** The package says its
   toggle writes the `ENABLE_RUNTIME_PIPELINE*` defines project-wide for every installed build target;
   whether a per-profile define satisfies the runtime asmdef `defineConstraints` is untested. Moot for
   Development builds, which use the global toggle; relevant only if `AH-3` wants a profile-scoped release
   define.
2. **§8 Q2 (A4) — the release server under IL2CPP Master.** Whether the experimental release Runtime
   Pipeline works with Medium stripping once the command classes carry `[Preserve]` is untested. The spike
   answers it; the file queue is the fallback.

---

## 2. AH-4 — Menus and world management

**What exists today.** Commands reach only agent worlds: `session_start` launches from the main menu,
`session_end` calls `PauseMenuController.SaveAndQuitToMainMenu`, and `world_list`/`clean_world` see only
the agent root. Loading a saved agent world of another save version is refused rather than migrated.

**Decided direction.** Extract the menu actions into a shared service used by both the buttons and the
commands (`WorldSelectMenu` create/load with AOT migration/delete, `PauseMenuController` resume and
save-and-quit, settings open/close), then expose `menu …` and `world list|create|load|delete` commands,
with delete limited to session/agent roots unless `--allow-persisted`. Real button clicks stay
`runtime simulate_pointer`. Agent-world loading would then share the menu's migration path, closing the
not-migrated limitation.

---

## 3. v2 — Pathfinding for `move_to`

**What exists today.** `move_to` walks a straight line after a route dry run and refuses a blocked one
with hints; past terrain the agent flies or noclips.

**Decided direction.** Build pathfinding only if flying and noclip prove insufficient in real agent work.

---

## 4. §8 Q4 — Build size of the `Unity.Pipeline` stripping root

**What exists today.** `<assembly fullname="Unity.Pipeline" preserve="all" ignoreIfMissing="1" />` in
`Assets/link.xml`. Measured 2026-11-21: it grew the Windows IL2CPP Development build from 1 777 985 592 to
2 679 007 473 bytes (build report `totalSizeBytes` of the builds just before and just after the entry was
added; no other change between them). Production and Master builds are unaffected: they contain no
`Unity.Pipeline`.

- **Why it grew** — unverified: keeping every member keeps code the linker would otherwise drop, possibly
  including paths into the bundled Roslyn assemblies (`UnityPipeline.Microsoft.CodeAnalysis*`) that
  `Unity.Pipeline` references. Compare the `ManagedStripped/` assembly sizes of two builds before choosing
  a fix.
- **What a narrower root must keep:** every type the server serializes through reflection. Known so far:
  the runtime descriptor model `Unity.Pipeline.Models.RuntimeInstanceDescriptor`, whose stripped getters
  left the descriptor without `pid`. The rest of the set (request and response models) is unknown, so try a
  namespace root (`Unity.Pipeline.Models`) first.
- **Done when:** a Development build with the narrower root is measurably smaller and still passes the
  `player-agent` recipe's ON run (descriptor with `pid`, `status`, a Memory session, `set_block`,
  `capture`, `quit` with exit 0). Building the player is an explicit per-run decision (`player-runner`).

---

## Document History

* **v1.0** - Split out of `Design/AGENT_HARNESS.md` (v1.3) at its promotion to
  `Architecture/AGENT_HARNESS_SYSTEM.md`: `AH-3`, `AH-4`, the v2 pathfinding extension and the open `§8`
  questions (Q1, Q2, Q4).

---

**Last Updated:** 2026-11-21  
**Next Review:** when `AH-3` or `AH-4` starts, or on a `com.unity.pipeline` upgrade (re-measure §4)
