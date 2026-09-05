# Open Work Index

**Version:** 1.2  
**Date:** 2026-09-05  
**Status:** **Open backlog.** A pointer map, not a tracker — rows are added and removed as docs
gain or lose open work, never as individual items move.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production) — documentation only

> One page answering "where is the work that isn't done?". Every open item in this repo is owned by
> a document; the retrieval problem was never that items are homeless, it is that a reader has to
> already know **which** document to open. **This index is deliberately per-document, not per-item.**
> Roughly 144 item rows already exist across seven master tables in the backlog reports, and
> mirroring them here would create a second source of truth that drifts from the first on its next
> edit. So this maps areas to owning docs and stops; the item, its status and its reasoning stay
> where they are.

**Audited:** 2026-09-05, at commit `6e4afd3a` (branch `feat/fluid-physics`).
Every row was read off the owning document's own `**Status:**` header this session, not carried from
memory or from another index — the failure this whole `DG-*` arc was filed to fix was exactly a
recycled count. The claim that no open item is orphaned was measured: every forward-referenced ID
under `Documentation/Architecture/` (66 distinct IDs across `VX-`, `CL-`, `FL-`, `TF-`, `RF-`, `P-`,
`MR-`, `LI-`, `NS-`, `VO-`, `SS-`, `LP-`, `UW-`) resolves to a document listed below.

**Relationship to other documents:**

- [`DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md`](DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md) — `DG-*`, the design
  that specifies this index (§3.2) and the deletion rule it is a precondition of (§3.1).
- [`../README.md`](../README.md) — the folder conventions this index sits under.
- [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) — the structural model,
  and by far the largest single backlog listed here.

---

## 1. What this is, and what it is not

**It is** a map from an area of the engine to the document that owns its unfinished work.

**It is not** a status tracker, a priority ranking, or a place to record why something is deferred.
Those live in the owning doc, which is the artifact that gets updated when work moves — a row here
that repeated them would go stale the first time it did.

**Adding a row:** when a document starts owning open work. **Removing a row:** when it stops.
Nothing else in this file changes as individual items ship.

---

## 2. Where the open work lives

| Area | Owning document | ID space | Open work |
|---|---|---|---|
| **Performance (master backlog)** | [`PERFORMANCE_IMPROVEMENTS_REPORT.md`](PERFORMANCE_IMPROVEMENTS_REPORT.md) | `MR-` `LI-` `TG-` `P-` `GS-` … | 31 items open, 30 complete, 1 deferred ⏸️. Completed detail is archived; rows stay in the master table |
| **World generation features** | [`WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md`](WORLDGEN_FEATURE_IMPROVEMENTS_REPORT.md) | `TF-` | Open backlog; the combined ranked TF/RF roadmap is at the end of that doc |
| **Lighting & rendering features** | [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md) | `RF-` | Open backlog. Performance counterparts (`LI-`, `GS-`) live in the performance report |
| **Volumetric & ray-traced effects** | [`VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md`](VOLUMETRIC_AND_RAYTRACED_EFFECTS_REPORT.md) | `VX-` | Open backlog. `VX-3` on `VX-5` is the named replacement for the submersion box |
| **Clouds** | [`CLOUD_RENDERING_IMPROVEMENTS_REPORT.md`](CLOUD_RENDERING_IMPROVEMENTS_REPORT.md) | `CL-` | Open backlog |
| **Foliage liveliness** | [`FOLIAGE_LIVELINESS_IMPROVEMENTS_REPORT.md`](FOLIAGE_LIVELINESS_IMPROVEMENTS_REPORT.md) | `FL-` | Open backlog |
| **Codebase (non-performance)** | [`CODEBASE_IMPROVEMENTS.md`](CODEBASE_IMPROVEMENTS.md) | — | Open backlog: API modernization and cleanup items |
| **Third-party libraries** | [`THIRD_PARTY_LIBRARY_IDEAS_REPORT.md`](THIRD_PARTY_LIBRARY_IDEAS_REPORT.md) | — | Open backlog of *techniques*; all seven evaluated libraries were rejected |
| **Validation coverage** | [`VALIDATION_SUITE_COVERAGE_ROADMAP.md`](VALIDATION_SUITE_COVERAGE_ROADMAP.md) | `NS-` | Living backlog; `NS-4`, `NS-5`, `NS-7`, `NS-7b` complete |
| **Lighting async bugs & harness** | [`LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md`](LIGHTING_ASYNC_BUG_VALIDATION_ROADMAP.md) | `AS-` `HF-` | In progress; `AS-1` closed, `HF-1`…`HF-3` done |
| **Lighting pipeline state** | [`LIGHTING_PIPELINE_STATE_REFACTOR.md`](LIGHTING_PIPELINE_STATE_REFACTOR.md) | `LP-` | `LP-1`…`LP-7` shipped; **`LP-8` remains** |
| **Chunk pipeline performance** | [`CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md`](CHUNK_PIPELINE_PERFORMANCE_ANALYSIS.md) | §-numbered | §1.1 and the §3 backpressure family shipped; **§2 and §4 remain open** |
| **Visibility culling** | [`VISIBILITY_CULLING_ARCHITECTURE.md`](VISIBILITY_CULLING_ARCHITECTURE.md) | Phases 0–3 | Phases 0 and 0.5 complete; **the culler itself (Phases 1–3) is unbuilt** |
| **Voxel occlusion / AO** | [`VOXEL_OCCLUSION_REFACTOR.md`](VOXEL_OCCLUSION_REFACTOR.md) | `VO-` | `VO-0`…`VO-6`, `VO-8`, `VO-9a`, `VO-9b` done; **`VO-9` not started**, premise revised |
| **Contact shadows** | [`SILHOUETTE_CONTACT_SHADOWS.md`](SILHOUETTE_CONTACT_SHADOWS.md) | `SS-` | `SS-0`…`SS-3a` shipped (`SS-3` default-OFF by owner decision); **`SS-4` not started** |
| **Sound engine** | [`SOUND_ENGINE_DESIGN.md`](SOUND_ENGINE_DESIGN.md) | `S`-numbered | `S0`–`S3`, `S5`–`S8`, `S10`, `S11` shipped; `S8` and `S10` await their listening pass |
| **Spatial audio (far horizon)** | [`STEAM_AUDIO_INTEGRATION.md`](STEAM_AUDIO_INTEGRATION.md) | — | Draft, unscheduled; SDK specifics need re-verification before any work starts |
| **Waterline / liquid surface** | [`ANIMATED_LIQUID_SURFACE.md`](ANIMATED_LIQUID_SURFACE.md) | `UW-5` | ⏸️ Paused on cost/benefit. Mesh-displacement route open; the screen-space band is refuted |
| **Pause & simulation halt** | [`PAUSE_AND_SIMULATION_HALT.md`](PAUSE_AND_SIMULATION_HALT.md) | `PA-` | Proposed, not implemented |
| **Region file concurrency** | [`REGION_FILE_CONCURRENCY.md`](REGION_FILE_CONCURRENCY.md) | — | Proposed, not implemented; `RegionFile` still serializes all I/O behind one lock |
| **Chunk palette mapping** | [`CHUNK_PALETTE_MAPPING.md`](CHUNK_PALETTE_MAPPING.md) | — | Draft, unscheduled; nothing built. Re-verify its prerequisites before starting |
| **World scaling (remaining tiers)** | [`WORLD_SCALING_ANALYSIS.md`](WORLD_SCALING_ANALYSIS.md) | Tiers A–C | Tier B shipped; **Tiers A and C still unbuilt**. Also the standing "what breaks per tier" reference |
| **Device calibration** | [`OM1_DEVICE_CALIBRATION.md`](OM1_DEVICE_CALIBRATION.md) | `OM-` | Implemented and player-build verified; **pending its final calibration pass** |
| **Doc lifecycle & drift detection** | [`DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md`](DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md) | `DG-` | `DG-0`…`DG-4` shipped; **`DG-5` open** — the status-drift checker and its `docs-sync` step. Also the standing rule this index's §3 cites |

---

## 3. Closed arcs retained in `Design/`

These own **no** open work, and are not deletable either. Their arcs finished without ever going
through the promotion protocol, so the Architecture tree *cites* them — in several cases deferring
detail back to them — rather than superseding them. They are listed here so a reader who finds a
finished design in `Design/` knows it is deliberate, and so nobody applies the deletion rule to one.
The test that separates these from a promotion is whether an Architecture doc carries the design's
`## ID index`; none of these has one. See
[`DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md`](DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md) §2.1 and §3.4.

| Document | ID space | Closed |
|---|---|---|
| [`CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md`](CHUNK_LIFECYCLE_ORCHESTRATION_REFACTOR.md) | `CP-1`…`CP-7` | 2026-07-23 |
| [`MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md`](MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md) | `MP-1`…`MP-7` | 2026-07-26 |
| [`CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md`](CHUNK_PIPELINE_SCHEDULE_QUOTA_THROUGHPUT.md) | `P9-*` | Core question closed |
| [`FLIGHT_PROFILE_CAPTURE.md`](FLIGHT_PROFILE_CAPTURE.md) | `FP-0`…`FP-4` | All shipped |
| [`WORLD_SCALING_IMPLEMENTATION.md`](WORLD_SCALING_IMPLEMENTATION.md) | `WS-*`, `OQ-1`…`OQ-7` | Track fully closed |
| [`SUN_APPEARANCE_IMPROVEMENTS.md`](SUN_APPEARANCE_IMPROVEMENTS.md) | `SN-0`…`SN-4` | 2026-08-15 |

---

## 4. Deferrals recorded inside Architecture docs

An Architecture doc's limitations section is where a shipped system records what it deliberately
does not do. Those are **consequences, not backlog items**, and they stay there — but where one
names a future ID, that ID is owned by a document in §2 and is reachable from it. Measured
2026-09-05: every forward-referenced ID under `Architecture/` resolves to a §2 document, so there is
no orphaned work for this index to adopt.

If that ever stops being true — a limitation that names no owner — the fix is to give it an owning
doc, not to grow an item row here.

---

## Document History

* **v1.2** - `DG-5` filed, so the `DG-*` row returns to §2 (2026-09-05) — the removal rule exercised
  in the other direction, four hours after §3 gained the row. The §3 caveat written about that row
  went with it rather than being left to dangle onto the entry above. That this file needed three
  versions in one day is itself the argument for `DG-5`: nothing but a person noticed either move.
* **v1.1** - `DG-4` shipped, so the `DG-*` row left §2 for §3 (2026-09-05). Per §1 a row is *removed*
  when its document stops owning open work, not edited to say "done" — otherwise §2 slowly becomes
  the completed-work list it exists not to be. Noted as the first exercise of that rule, and the
  first time this index went stale: it did so within hours, for its own arc, with nothing to catch
  it. Nothing triggers an index review when a document's last item closes; the owning doc's author
  has to remember.
* **v1.0** - Initial index (`DG-3`). Built per-document rather than per-item: ~144 item rows already
  exist across seven master tables, and mirroring them would create the second source of truth
  `DOC_LIFECYCLE_AND_OPEN_WORK_INDEX.md` §3.2 forbids. The orphan set that motivated a per-item
  design turned out to be empty — every forward-referenced ID in `Architecture/` already has an
  owning doc — so §3.2 was amended to match what the measurement supported.

---

**Last Updated:** 2026-09-05  
**Next Review:** when a document starts or stops owning open work
