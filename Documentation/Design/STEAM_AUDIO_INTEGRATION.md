# Steam Audio Integration (Physical Acoustics)

> **Draft** exploration of integrating Valve's **Steam Audio** SDK as the v3+ acoustics extension
> of the sound engine: HRTF binaural spatialization, physically-based occlusion & transmission,
> and real-time reflections/reverb derived from the voxel world itself. The central architectural
> idea is to **skip acoustic mesh export entirely** and feed Steam Audio's simulation through its
> **custom ray tracer callback API**, answering acoustic rays directly against the packed-`uint`
> voxel data — the same "the voxel array *is* the geometry" philosophy the engine already uses
> for placement raycasts and voxel physics.
>
> Status: **Draft — far-horizon (sound engine v3+).** Not scheduled. Written to capture the
> design direction and the verification checklist (§8) while the reasoning is fresh; SDK
> specifics MUST be re-verified against the then-current Steam Audio release before any
> implementation starts. Prerequisite: the base sound engine (S0–S3) and ideally the v2 voxel
> occlusion extension are shipped first.

**Relationship to other documents:**

- [`SOUND_ENGINE_DESIGN.md`](SOUND_ENGINE_DESIGN.md) — the parent design. This doc is the "v3+"
  row of its §8 extension roadmap. Everything here layers *under* that design's four layers: the
  pooled `AudioSource`s, the emitter budget, and the `SoundMaterial` data model are all reused,
  not replaced.
- [`../REFERENCES_AND_CREDITS.md`](../REFERENCES_AND_CREDITS.md) — native-dependency precedent:
  **NativeCompressions** (Cysharp) already ships native LZ4 bindings via NuGet, so a native
  plugin + P/Invoke bindings is an established pattern in this project, not a first.
- [`../Guides/BURST_COMPILER_GUIDE.md`](../Guides/BURST_COMPILER_GUIDE.md) — the §4.2 callback
  strategy relies on `BurstCompiler.CompileFunctionPointer` interop rules.
- [`../Architecture/DATA_STRUCTURES.md`](../Architecture/DATA_STRUCTURES.md) — the packed-`uint`
  voxel model the custom ray tracer reads.

---

## 1. What Steam Audio is (and why it fits)

**Steam Audio** (Valve) is a spatial-audio SDK: C API (`phonon`), official Unity plugin,
**Apache-2.0 licensed and fully open source** (github.com/ValveSoftware/steam-audio) — free for
commercial and non-commercial use, no royalties, no SDK gatekeeping. It provides, roughly in
ascending cost:

| Feature                      | What it does                                                                           | Voxel-engine value                                                                  |
|------------------------------|----------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| **HRTF binaural rendering**  | Per-source spatializer replacing Unity's panner; true "above/behind you" on headphones | High — free immersion win, needs *no* geometry at all                               |
| **Occlusion & transmission** | Ray-tests source→listener against scene geometry; muffles + filters through materials  | High — replaces the v2 hand-rolled low-pass with physically-parameterized filtering |
| **Reflections & reverb**     | Real-time ray-traced early reflections + late reverb from actual geometry              | Medium-high — caves that *sound* like caves, without hand-authored reverb zones     |
| **Pathing**                  | Sound finds its way around corners via baked probe graphs                              | Low — see §6: baking does not fit an infinite procedural world                      |

The audio-thread DSP (binaural convolution, filtering) runs inside Unity's audio pipeline via the
native spatializer plugin interface; the *simulation* (ray tracing, reflection estimation) runs
on Steam Audio's own worker thread(s), decoupled from both the frame and the audio callback.

---

## 2. Why this is *not* FMOD/Wwise through the back door

The parent design's non-goal ("no third-party audio middleware") stands. Steam Audio is not a
middleware replacing Unity's audio engine — it plugs *into* it as a spatializer/mixer effect, so:

- All of `SOUND_ENGINE_DESIGN.md` survives: `SoundManager`, pooled `AudioSource`s, the mixer
  groups, the emitter scan, the `BlockSoundDatabase`. Enabling Steam Audio is (to a first
  approximation) checking "Spatialize" on the pooled sources and selecting the Steam Audio
  spatializer in project settings.
- It is removable: disable the spatializer and the engine falls back to Unity panning + the v2
  voxel occlusion. The integration must preserve this fallback permanently (platform issues,
  perf tiers, debugging).

---

## 3. The geometry problem — three options

Steam Audio needs to answer one question: *what do acoustic rays hit?* For a static-scene game
the answer is "the level mesh". For this engine the world is chunked, remeshed on edit,
pooled/recycled, and infinite. Three ways to feed it:

### Option A — export section render meshes as acoustic geometry (rejected)

Register each section's render mesh (or a simplified copy) as Steam Audio static/dynamic
geometry; re-commit on every remesh.

- ✅ What the official Unity plugin expects out of the box.
- ❌ **Churn:** every block edit already triggers a remesh; mirroring that into acoustic-scene
  re-commits doubles the mesh pipeline's output work for a consumer that needs far less detail.
- ❌ **Memory:** a second persistent copy of world geometry, at render granularity, for a system
  that would be happy with 1-block resolution.
- ❌ The engine's physics deliberately does *not* use mesh colliders (custom `VoxelRigidbody`
  voxel physics) — building collider-grade meshes only for audio would be architecturally
  backwards.

### Option B — coarse proxy geometry (fallback)

Per-section occupancy boxes (e.g. one box per solid 4³ cell, or greedy-merged slabs) committed as
acoustic geometry, rebuilt lazily per section.

- ✅ Much cheaper than A; bounded update cost; still works with the official plugin's scene API.
- ❌ Still a second geometry representation to build, cache, and invalidate — and the quality
  ceiling (occlusion through 4³ approximations) is mediocre in tunnels, exactly where acoustics
  matter most.
- Kept as the **fallback** if Option C's callback path proves unavailable or too slow.

### Option C — custom ray tracer callbacks against voxel data ✅ **preferred direction**

Steam Audio's C API supports a **custom scene type**: instead of handing it geometry, the host
registers closest-hit / any-hit ray callbacks and Steam Audio calls them during simulation. The
callbacks answer rays with a **voxel DDA traversal** over the packed-`uint` chunk data — the same
class of traversal the engine already uses for placement rays and (planned, v2) occlusion rays.

- ✅ **Zero acoustic geometry.** No export, no re-commit on edit — an edited voxel is "in the
  acoustic scene" the moment it's in the chunk array. The world's *one* source of truth stays its
  only representation.
- ✅ 1-block acoustic resolution everywhere, better than any proxy.
- ✅ Per-hit material comes straight from the block ID → `SoundMaterial` → acoustic-material
  table (§5) — no per-triangle material plumbing.
- ⚠️ **Threading is the hard part** (§4.1): callbacks arrive on Steam Audio's simulation
  thread(s) and must read chunk data safely against the main thread's edits and the chunk pool's
  recycling.
- ⚠️ **Callback performance matters** (§4.2): simulation shoots thousands of rays per update;
  a managed-C# callback per ray would drown in transition overhead — the callbacks should be
  **Burst-compiled function pointers**.
- ⚠️ Requires the **C API via P/Invoke** (NativeCompressions precedent); the official Unity
  plugin may not expose custom scenes — §8 item 1 verifies this.

---

## 4. Architecture sketch (Option C)

### 4.1 Thread-safe voxel reads from the simulation thread

The callbacks read voxel data owned by the main thread while chunks load, unload, and recycle
through the pool. Candidate strategies, in preference order:

1. **Acoustic snapshot volume** — maintain a listener-centered snapshot (e.g. 5×5 chunk columns
   of solidity + `SoundMaterial` bytes, ~1 byte/voxel) that the main thread refreshes
   incrementally (dirty sections only) and hands to the simulation double-buffered. This is the
   same shape as the LI-1 padded-volume / TG-4 halo-gather patterns already proven in this
   project, and it shrinks the read surface to a compact, immutable-per-simulation-pass buffer.
   Acoustics doesn't need the live world — a snapshot a few hundred ms stale is inaudible.
2. Direct reads with lifetime pinning (read locks / epoch guards on chunk recycling) —
   avoids the copy but couples audio to the chunk pipeline's invariants (see the
   `chunk-lifecycle` deadlock history). Only if the snapshot's memory cost proves unacceptable.

Strategy 1 is strongly preferred: it makes the callback path allocation-free, lock-free, and
completely decoupled from chunk lifecycle correctness.

### 4.2 Burst-compiled callbacks

Implement the closest-hit/any-hit callbacks as static, `[BurstCompile]`d functions turned into
native function pointers via `BurstCompiler.CompileFunctionPointer`, closing over nothing —
all state (snapshot pointer, dimensions, material table) travels through the callback's
`userData` pointer. Native (Steam Audio) → native (Burst) calls skip the managed transition
entirely, making per-ray cost pure DDA arithmetic. This is the same interop discipline the Burst
guide already documents for job internals.

### 4.3 Simulation scope & sources

- **Simulation radius:** only sources within ~24–32 blocks participate in occlusion; only the
  listener's surroundings feed reflections. The parent design's emitter budget (~4–8 loops +
  ~16–32 one-shots) keeps the source count trivially low for Steam Audio.
- **Update cadence:** occlusion per-source at ~10 Hz; reflections for the listener at ~1–2 Hz
  with interpolation — both well within the SDK's intended usage.
- **Reverb:** listener-centric real-time reverb from the same ray budget; no baked reverb
  (§6).

### 4.4 Distribution & bindings

- Native libraries (`phonon`) per platform under `Assets/Plugins/` — same shape as the
  NativeCompressions native payload. Windows x64 first; other platforms feature-gated.
- A thin P/Invoke binding layer (only the entry points actually used: context, scene-with-
  callbacks, simulator, sources, direct/reflection effects). Check for maintained C# bindings
  first (§8 item 2); hand-rolling the ~dozen needed signatures is acceptable otherwise.
- The official Unity plugin is still used **if** it can coexist with a custom C-API scene
  (§8 item 1) — it provides the spatializer plugin registration and mixer effects for free. If
  it can't, the fallback is the plugin with Option B proxy geometry, or spatializer-only usage
  (HRTF without geometry) which needs no scene at all.

---

## 5. Acoustic materials from `SoundMaterial`

The parent design's per-block `SoundMaterial` enum doubles as the acoustic material key — one
static table, no new per-block authoring:

| `SoundMaterial`              | Acoustic character (absorption / scattering / transmission)                                                          |
|------------------------------|----------------------------------------------------------------------------------------------------------------------|
| Stone / Metal                | Low absorption, low scattering — bright, echoey caves                                                                |
| Dirt / Grass / Sand / Gravel | High absorption — dead outdoor ground                                                                                |
| Wood                         | Medium absorption, some transmission — audible through huts                                                          |
| Leaves / Plant               | High scattering, high transmission — foliage barely occludes                                                         |
| Glass                        | Low absorption, moderate transmission                                                                                |
| Wool / Snow                  | Very high absorption — natural sound dampeners                                                                       |
| Liquid                       | Special-cased: the underwater mixer snapshot already handles the submerged state; water surfaces get high absorption |

Coefficients start from Steam Audio's stock material presets and get tuned by ear; the table
lives next to `BlockSoundDatabase` as data.

---

## 6. What an infinite procedural world rules out

- **No baking of any kind.** Baked pathing probes, baked reflections, and baked reverb all
  assume a finite, known level. Everything here is real-time; **pathing is therefore out of
  scope** (its audible win over occlusion+reflections is smallest anyway).
- **Simulation is listener-local.** The acoustic world is the snapshot radius; distant sounds
  are already distance-culled by the parent design before Steam Audio ever sees them.
- **Perf is tier-gated.** Reflections are the expensive knob and must sit behind the quality
  tier / settings surface (`Acoustics: Off / HRTF only / +Occlusion / +Reflections`), with
  "Off" falling back to v2 voxel occlusion. HRTF-only is cheap enough to consider default-on.

---

## 7. Phasing (SA-*)

| Phase                           | Scope                                                                                                                                      | Needs geometry? | Exit criterion                                                                                                 |
|---------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------|:---------------:|----------------------------------------------------------------------------------------------------------------|
| **SA-0 — Spike**                | Import SDK, register spatializer, HRTF on the pooled one-shot sources. No scene, no P/Invoke beyond setup.                                 |       No        | Audible binaural improvement; no audio-thread spikes in profiler                                               |
| **SA-1 — Custom scene**         | C-API P/Invoke layer, snapshot volume (§4.1), Burst DDA callbacks (§4.2), per-source **occlusion + transmission** replacing v2's low-pass. |    Callbacks    | A/B vs. v2 occlusion: better quality at acceptable cost; validation baselines for the DDA callback (§8 item 6) |
| **SA-2 — Reflections & reverb** | Listener-centric real-time reflections + reverb, quality-tier-gated.                                                                       |    Callbacks    | Caves/interiors audibly distinct; frame + audio thread within budget on mid-tier                               |
| **SA-3 — Polish**               | Material-table tuning, settings UI tiers, per-platform native payloads, credits entry (Apache-2.0, `CreditCategory.Audio`).                |        —        | Shippable default configuration chosen                                                                         |

Each phase is independently shippable; SA-0 alone is a worthwhile end state (HRTF with v2 voxel
occlusion on top costs one plugin and zero architecture).

---

## 8. Verification checklist (MUST re-verify before implementation)

This doc was written from SDK knowledge that will be stale by v3. Before SA-0:

1. **Custom ray tracer exposure** — confirm the current SDK still supports custom scene
   callbacks (`IPLSceneType` custom + closest/any-hit callbacks) and whether the official Unity
   plugin can host a custom scene, or whether the C API must own the simulator with the plugin
   used only as spatializer. This decides Option C vs. Option B.
2. **C# bindings state** — check for maintained bindings/NuGet (NativeCompressions-style)
   covering the needed entry points before hand-rolling P/Invoke.
3. **Callback threading contract** — which thread(s) invoke the callbacks, re-entrancy, and
   whether callbacks may be native function pointers (required for the Burst plan).
4. **License & credits** — confirm Apache-2.0 still covers the release used; add the
   `REFERENCES_AND_CREDITS.md` + `CreditsDatabase` entries at SA-0, not SA-3, so the dependency
   is credited from its first commit.
5. **Platform payloads** — required native binaries per target; Mono vs IL2CPP marshalling
   differences for the function-pointer interop.
6. **Validation strategy** — the DDA hit callback is deterministic and pure: baseline it in the
   `Validate Sound Engine` suite (fixture snapshot → known rays → expected hits/materials),
   independent of the Steam Audio runtime. Simulation output itself is verified by ear, as with
   the parent design's audible layer.
