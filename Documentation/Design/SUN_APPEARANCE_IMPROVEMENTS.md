# Sun Appearance Improvements Design

**Version:** 1.1  
**Date:** 2026-08-15  
**Status:** **Partially implemented** — SN-0 shipped and confirmed in game 2026-08-15; SN-1..SN-3 proposed.  
**Target:** Unity 6.5 (Mono for dev; IL2CPP for production)

> Four changes that turn the sun from a flat disc into a body seen through air, in priority
> order: an **angular aureole** around the disc, **per-channel extinction** so it reddens as it
> sets, an **HDR core** so post-process bloom can finally see it, and URP's screen-space lens
> flare on top. **The pivotal finding: the sun's glow must be produced in the skybox shader as an
> angular term, not by post-process bloom.** Bloom's radius is screen-space and its tuning is
> shared globally with RF-3's lava and lamps, so it cannot express an atmosphere — it can only
> ever be an accent on top of one. The corollary finding is that bloom does not currently see the
> sun *at all*: the disc's output ceiling is exactly 1.0 against an effective linear threshold of
> ≈1.23.

**Audited:** 2026-08-15, at commit `9e4b264f` (branch `feat/world-scaling`).
Findings are from static review of `Assets/Shaders/SkyboxShader.shader` (read in full),
`Assets/Scripts/Data/WorldTypes/TimeOfDaySettings.cs`, `World.cs`'s sky-globals publication, the
authored assets `VoxelEngine-Post-Profile.asset`, `VoxelEngine-URP-Asset.asset` and
`DefaultVolumeProfile.asset`, and URP's own `BloomPostProcessPass.cs:94` and
`Shaders/PostProcessing/Bloom.shader:108-112` for the threshold semantics quoted in §2.
The threshold arithmetic in §2 is computed from those two sources, not assumed. No runtime
state was inspected; nothing here depends on runtime state.

**Amended:** 2026-08-15 — SN-0 shipped. §7 records it, and new §7.1 carries the three findings that
changed this plan: the glow had to become a **blend** because LDR headroom is already spent by the
authored sky; SN-1 is less independent of SN-0 than §7 claimed; and two validation false greens, both
demonstrated by mutation rather than argued.

**Relationship to other documents:**

- [`../Architecture/SKY_AND_CELESTIAL_RENDERING.md`](../Architecture/SKY_AND_CELESTIAL_RENDERING.md)
  — the system this modifies. Its §4 atmosphere model (extinction then additive airlight) is the
  precedent SN-1 extends from the moon to the sun; its §8 authoring traps and §7.1 rendered-pixel
  suite both apply here.
- [`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md`](LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md)
  — parent backlog. This doc takes over the "sun flare" bullet of its RF-2 §6 and **corrects** that
  bullet's cost estimate (§2). RF-3's shipped bloom is the consumer SN-2 feeds.
- [`../Guides/SHADER_CONVENTIONS.md`](../Guides/SHADER_CONVENTIONS.md) — `#pragma target 3.5` floor
  and the interpolator-counting rule. §6 records that no phase here adds a varying.

---

## 1. Goals & non-goals

### Goals

1. **The disc reads as connected to the sky it sits in** — an aureole, so the atmosphere around
   the sun differs from the atmosphere away from it.
2. **The sun reddens as it sets** — wavelength-dependent extinction, the single most recognisable
   "that is a real sun" cue, at the hours players most often look at it.
3. **Bloom sees the sun** — close the gap between what the RF-2 backlog assumed had shipped and
   what actually renders.
4. **Every change stays inside the skybox shader plus authored volume settings** — no voxel
   pipeline contact, no new globals unless named here.

### Non-goals (v1)

- **Tonemapping / HDR colour grading.** RF-3 deliberately left `m_ColorGradingMode: 0` (LDR). §3.2
  keeps that decision and works within it. This is a **permanent scope boundary for this doc**, not
  a deferral: the upgrade changes the appearance of the entire world rather than the sun, so it
  gets its own dedicated design doc rather than a roadmap row here (§3.2 Option B).
- **A physically-based Rayleigh/Mie atmosphere** (precomputed transmittance/scattering LUTs). This
  is a genuine rejection for this doc, not a deferral — see §3.3 Option C. It would *replace* the
  authored zenith/horizon gradients, which the Sky Editor exists to author and which carry
  user-locked decisions.
- **God rays / volumetric light shafts.** Screen-space shafts need a depth pass and a separate
  cost/benefit case; they belong with the `VX-*` volumetric backlog, not here.
- **A sprite-chain lens flare.** Superseded — see §3.4.
- **Seasonal declination, blood-moon tint, per-biome sky colour.** Untouched RF-2 remainder; this
  doc changes nothing they depend on.

---

## 2. Current state (what exists today)

| Area | State |
|------|-------|
| **Sun disc colour** | `SkyboxShader.shader:143-145` — `SUN_CORE_COLOR` `(1.0, 0.97, 0.86)`, `SUN_LIMB_COLOR` `(1.0, 0.86, 0.66)`, `SUN_LIMB_DARKENING` 0.18. Composited at `:412-417`. Peak output is **exactly 1.0** and cannot exceed it: the limb term only ever scales down. |
| **Sky gradient** | `:254` — `heightFactor` is a function of `abs(viewDir.y)` **alone**. It has zero dependence on `_SunDirection`. The air 5° from the sun is rendered identically to the air 175° from it. |
| **Sunset horizon** | Consequence of the row above: the horizon colour is elevation-keyed, so at sunset the **entire** horizon ring warms, including the quadrant behind the player. |
| **Sun atmosphere model** | `:416` — `lerp(sunColor, _VoxelFogColor, hazeAmount)`. This is the blend-toward-fog model the polish arc **refuted** for the moon and replaced with extinction-then-airlight (`:372`, `:392`, Architecture §4). The sun was never migrated. |
| **Disc size** | `TimeOfDaySettings.cs:122` — `_sunAngularRadius = 1.5°`, ≈5.6× the real sun. Authored and deliberate (the tooltip says so); readability at voxel scale. Not changed by this doc. |
| **Bloom** | `VoxelEngine-Post-Profile.asset` — `threshold 1.1`, `intensity 0.25`, `scatter 0.6`, `highQualityFiltering 0`, `clamp` at its 65472 default (effectively off). One global instance, shared with RF-3's lava and lamps. |
| **HDR pipeline** | `VoxelEngine-URP-Asset.asset` — `m_SupportsHDR: 1`, `m_HDRColorBufferPrecision: 0` (R11G11B10), `m_MSAA: 2`, `m_ColorGradingMode: 0` (**LDR**). No tonemapper is active in either volume profile. |
| **Screen-space lens flare** | Present in `DefaultVolumeProfile.asset` at `intensity: 0`. Drives off the bloom mip pyramid (`bloomMip: 1`), so it inherits the threshold problem below. |

### 2.1 Bloom cannot meaningfully see the sun — the arithmetic

URP converts the authored threshold out of gamma space and derives a hardcoded soft knee
(`BloomPostProcessPass.cs:94-95`):

```csharp
float threshold     = Mathf.GammaToLinearSpace(bloom.threshold.value);  // 1.1  -> ~1.23
float thresholdKnee = threshold * 0.5f;                                 //      -> ~0.62
```

The prefilter then computes (`Bloom.shader:110-112`):

```hlsl
half softness   = clamp(brightness - Threshold + ThresholdKnee, 0.0, 2.0 * ThresholdKnee);
softness        = (softness * softness) / (4.0 * ThresholdKnee + 1e-4);
half multiplier = max(brightness - Threshold, softness) / max(brightness, 1e-4);
```

At the sun's ceiling `brightness = 1.0`, with `Threshold ≈ 1.23` and `ThresholdKnee ≈ 0.62`, the
`brightness - Threshold` term is negative and only the soft knee contributes: `multiplier ≈ 0.06`.
Roughly **6 %** of the sun's brightness enters the bloom pyramid, then scales by `intensity 0.25`
— under 1.5 % additive. Not literally zero, but visually nothing, and **it cannot be tuned upward
from the shader side**, because the disc is already at its own ceiling of 1.0.

This corrects a documented assumption. `LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md:366`
records the sun flare as "cheapest viable = let RF-3's bloom catch the HDR sun disc (**free** once
both ship)". Both shipped; it is not free, because no part of the sun path was ever made HDR. That
row needs the amendment in §5.

---

## 3. Decisions

### 3.1 Where the sun's glow comes from

The pivotal choice, because it determines whether the other phases are polish or foundation.

#### Option A — post-process bloom only (rejected)

- ✅ Zero shader work; one asset value plus the HDR core of SN-2.
- ✅ Automatically covers lens flare, since URP's SSLF reads the same pyramid.
- ❌ **Bloom's radius is screen-space, not angular.** It scales with resolution and render scale,
  not with the atmosphere. The same sun would carry a visibly different halo at 1080p and at 1440p,
  and GS-4's render-scale slider (30–200 %) would change it again — a sky whose atmosphere depends
  on a graphics setting.
- ❌ **The bloom instance is global and already tuned for RF-3.** `intensity 0.25` / `scatter 0.6`
  were chosen for lava and lamps. There is no per-source bloom, so every knob that makes the sun's
  halo bigger also changes every emissive block in the world.
- ❌ It blooms *the rendered pixels*, so it cannot know about elevation, haze, or sun angle — the
  three things that actually govern an aureole.

#### Option B — in-shader angular aureole, with bloom as an accent ✅ **CHOSEN**

The glow is a function of `dot(viewDir, _SunDirection)` evaluated in the skybox fragment shader,
so it is measured in **degrees of arc** and is invariant to resolution, render scale and MSAA. It
can be modulated by the same `hazeAmount` the discs already use, which is what makes the halo
swell and redden near the horizon the way a real aureole does. `_SunDirection` is already a
published global (`World.cs:287`), so this adds no CPU work and no new varyings.

Bloom then becomes what it is good at: a small screen-space accent on an already-correct disc,
lifting the core past the point where the eye reads "too bright to look at". The two are
complementary rather than alternatives — which is why SN-0 ships first and stands alone, and SN-2
is tuned against it rather than in place of it.

This also fixes the sunset-horizon-ring defect in §2 for free: an azimuthal term is exactly what
the elevation-only gradient is missing.

#### Option C — both, with the aureole *replacing* the disc's own limb detail (rejected)

- ✅ Simplest composite; one radial falloff from centre to sky.
- ❌ **Discards the polish arc's shipped work.** Limb darkening and the warm limb colour landed
  2026-08-12 and were confirmed in game. An aureole is the air *outside* the disc; it should be
  added around the existing disc, not merged into it.

### 3.2 HDR headroom versus tonemapping

`m_ColorGradingMode: 0` with no active tonemapper means everything above 1.0 **hard-clips to
white** in the final image. So the HDR value chosen for the sun does not merely brighten it — past
1.0 it also flattens it.

#### Option A — radial HDR ramp, LDR shape preserved at the limb ✅ **CHOSEN**

The disc keeps its existing ≤1.0 colour across most of its face and ramps into HDR only over a
small central core, so the limb retains `SUN_LIMB_COLOR` and its darkening while the core carries
enough energy to cross the ≈1.23 threshold with margin. Bloom sees the core; the eye still sees a
shaped disc rather than a white circle.

Self-contained, reversible, and consistent with RF-3's locked decision to stay in LDR grading. It
also keeps the change auditable: the disc's appearance below the core radius is bit-identical to
today, so any visual regression is localised.

#### Option B — enable Neutral tonemapping (`m_ColorGradingMode` → HDR) (rejected — own design doc)

- ✅ The physically right answer. Highlight rolloff would let the core go white while the limb
  keeps its colour *naturally*, with no radial-ramp hack, and would improve every bright surface in
  the world including RF-3's lava.
- ✅ Removes the clipping constraint entirely, so future sky work is not designed around it.
- ❌ **It changes the appearance of the entire world**, not the sun. Every authored sky gradient,
  every block texture and RF-3's emissive tuning were all authored against an untonemapped image.
  Shipping it as a side effect of a sun change would silently invalidate that authoring.
- ❌ RF-3 explicitly declined it. Reversing a locked decision belongs in its own change with its own
  in-game pass, not folded into this one.

**Decided: Option A, and the tonemapping move gets its own dedicated design doc** rather than a
roadmap row here. The reasoning that makes it a separate doc rather than a later phase is its blast
radius — its scope has to include a re-tune pass over the four authored sky gradients and RF-3's
emissive values, both authored against an untonemapped image, plus an in-game pass over ordinary
terrain. That is a larger surface than everything in this document combined, and none of it is
about the sun. SN-2 is therefore designed to be **correct under LDR on its own terms**, not as a
stopgap waiting on a tonemapper: the radial ramp is the shipped answer, not scaffolding.

#### Option C — lower the global bloom threshold instead (rejected)

- ✅ One value; no shader change at all.
- ❌ **It is the same global instance RF-3 tuned.** Dropping the threshold to catch a 1.0 sun would
  also catch every surface in the world near 1.0 — the washed-out failure `b981ec44` already
  produced once by a different route.

### 3.3 The sun's atmosphere model

#### Option A — keep `lerp(sunColor, _VoxelFogColor, hazeAmount)` (rejected)

- ✅ Shipped, understood, cheap.
- ❌ **It is the model the polish arc already refuted**, still in place on the sun only. The moon
  moved to extinction-then-airlight because blending toward fog colour and then adding airlight
  pays for the same air twice (Architecture §4).
- ❌ **It structurally cannot produce a red sunset.** Lerping toward a single fog colour
  *desaturates* the disc toward the sky. Real extinction is wavelength-dependent — blue leaves the
  sight line first, which is *why* a low sun goes orange and then deep red. A scalar lerp has no
  channel to express that in.

#### Option B — per-channel extinction plus additive airlight ✅ **CHOSEN**

`exp(-opticalDepth * beta)` with a per-channel `beta` (blue extinguished hardest), applied to the
disc's own light, followed by the additive airlight term — the identical structure the moon
already uses, so the two bodies stop disagreeing about the same air. `opticalDepth` derives from
the existing `hazeAmount`, so no new global and no new authored field.

The wavelength ratio is a **tuning constant, not a physical derivation**: this design deliberately
does not import Rayleigh coefficients, because the sky it must agree with is authored (Option C),
not simulated. The constant is chosen by eye against `SkyPreviewRenderer` and confirmed in game.

#### Option C — a full precomputed Rayleigh/Mie atmosphere (rejected)

- ✅ Correct by construction: aureole, sunset reddening, and horizon gradient would all fall out of
  one model instead of three tuned terms.
- ❌ **It would replace the authored gradients.** `TimeOfDaySettings`' four gradients, the Sky
  Editor built to author them, and the dawn-crossing fix of `f6526e1f` all assume the sky's colour
  is authored. A simulated atmosphere makes those controls meaningless.
- ❌ It collides with user-locked decisions — notably that fog must reach full opacity to conceal
  the chunk boundary, which a physical model has no reason to honour.
- ❌ Cost and scope: LUT generation, a new authored asset, and a re-verification of every one of
  the 22 baselines across `Validate Sky` and `Validate Sky Render`.

### 3.4 Lens flare

**URP's screen-space lens flare ✅ CHOSEN**, superseding the parent report's rejection.

`LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md:366-368` rejects "a classic sprite-chain flare"
on the grounds that it needs *occlusion queries against voxel depth* for a stylistic mismatch. That
reasoning is sound and is not being reversed — but it does not apply to the screen-space flare
already sitting in `DefaultVolumeProfile.asset`. SSLF reads the rendered frame's bright pixels, so
**occlusion is inherent**: a block in front of the sun means there are no bright pixels to flare
from, with no query, no sprite chain and no per-object bookkeeping.

It is gated entirely on SN-2, since it samples the bloom pyramid.

---

## 4. Architecture

All four phases are fragment-shader changes in one file plus authored volume values. No new shader
globals, no C# changes, no new varyings.

```
World.PublishSkyGlobals ──▶ _SunDirection, _SunAngularRadius, _VoxelFogRange (all existing)
                                     │
                                     ▼
                        SkyboxShader.frag
                          ├─ base gradient  (unchanged)
                          ├─ SN-0 aureole   ── added into `color` BEFORE the discs
                          ├─ stars / moon   (unchanged)
                          └─ sun disc
                               ├─ SN-1 per-channel extinction + airlight (replaces :416 lerp)
                               └─ SN-2 radial HDR core
                                     │
                                     ▼  (HDR scene colour, R11G11B10)
                        URP post: Bloom ──▶ SN-3 ScreenSpaceLensFlare
```

**Ordering inside the fragment shader matters and is a correctness constraint, not a preference.**
The aureole is added to `color` *before* the moon block, so that `skyAirlight` — captured at `:267`
as the pre-star sky — includes it. If the aureole is added after, a moon near the sun would be lit
by an airlight that disagrees with the sky it is drawn against, which is the same class of
double-model bug the polish arc spent three in-game rounds on. The aureole must stay outside the
star accumulation for the reason `:263-267` already documents: anything the discs are seen
*through* must exclude the stars, or stars shine out of the moon.

**The star field is unaffected** but must be re-checked in game: the aureole raises sky brightness
near the sun, and `starFade` is keyed on sun elevation rather than on local sky brightness. This is
a look question, not a defect — at the hours stars are visible the sun is below the horizon and the
aureole is at its weakest — but it is the one interaction between SN-0 and existing content.

---

## 5. Prerequisites & integration points

- **No blocking prerequisites.** SN-0 and SN-1 depend on nothing that has not shipped.
- ⚠️ **SN-2 must set a bloom `clamp` value.** It is currently at the 65472 default, i.e. off. An HDR
  core plus `m_MSAA: 2` and a feathered disc rim is the standard recipe for edge fireflies; the
  clamp is the guard and is part of SN-2's scope, not a follow-up.
- **Doc amendments owed in the same commit** (per `docs-sync`):
    - `LIGHTING_RENDERING_FEATURE_IMPROVEMENTS_REPORT.md` — the RF-2 §6 "sun flare" bullet's *"free
      once both ship"* claim is false as built (§2.1); the still-open table row should point here.
    - `SKY_AND_CELESTIAL_RENDERING.md` — §4 currently describes extinction-then-airlight as the
      moon's model; SN-1 makes it the shared model. Promotion of this doc is `docs-sync`'s job.
- **Reserved seats.** SN-0's aureole term is the natural host for a future godray/volumetric shaft
  (`VX-*`) to key off, and for the blood-moon `SkyEvent` tint to scale, if either lands.

---

## 6. Constraint compliance checklist

| Project constraint | How this design complies |
|--------------------|--------------------------|
| Voxels are packed `uint`s, no per-voxel objects | No contact. Nothing here reads or writes voxel data. |
| Burst jobs 100 % Burst-compatible | No contact. No job code is touched. |
| No GC / LINQ in hot paths | No contact. No C# runs per frame that does not already run; no new globals to publish. |
| Pooling conventions | No contact. No allocations of any kind. |
| No BinaryFormatter/JSON for terrain | No contact. Nothing reaches disk; no save-format change, so no AOT migration step. |
| `BlockIDs` constants, no raw IDs | No contact. No block references. |
| No magic numbers | Every new term gets a named `static const` beside the existing `SUN_*` block, `SCREAMING_CASE` per the private-const rule, each with a why-comment in the file's established voice. |
| `#pragma target 3.5` floor (`SHADER_CONVENTIONS.md`) | All new work is `dot`/`pow`/`exp`/`lerp` in the fragment stage. **No phase adds a varying**, so the interpolator count is unchanged. |
| Mutable statics reset on play-mode entry | No contact. No new statics; shader `static const` is compile-time. |

---

## 7. Phased implementation plan

| Phase | Scope | Effort | Depends on |
|-------|-------|:------:|------------|
| **SN-0 — Aureole** ✅ **SHIPPED** | Angular forward-scatter glow around `_SunDirection`, applied to `color` before the discs (§4 ordering) **and to the sun disc by the same operator** (§7.1). Modulated by `hazeAmount` so it swells near the horizon. Guarded by **B8**, plus a repaired **B4**. | 🟢 | — |
| **SN-1 — Per-channel extinction** | Replace `:416`'s `lerp`-to-fog with `exp(-opticalDepth * beta)` extinction plus additive airlight, matching the moon's model. Delivers the sunset reddening. | 🟢 | — (independent of SN-0 — but see §7.1: SN-0 had to defend against `:416` to ship at all) |
| **SN-2 — HDR core + bloom coupling** | Radial HDR ramp over the disc's central core (§3.2 Option A), sized to clear the ≈1.23 linear threshold with margin. Set bloom `clamp`. Re-verify RF-3's lava/lamp look is untouched. | 🟡 | SN-0, SN-1 (tune against the finished disc, not a moving one) |
| **SN-3 — Screen-space lens flare** | Raise `ScreenSpaceLensFlare.intensity` off 0 and tune. Confirm occlusion behaves when a block crosses the sun. | 🟢 | SN-2 |

**SN-0 alone delivers standalone value** and is the recommended first commit: it is the change that
most directly answers "it looks like a yellow circle", it is independent of the HDR decision in
§3.2, and it touches no authored asset.

**Validation is built alongside, not after.** Each phase adds baselines to `Validate Sky Render`
(Architecture §7.1), which is the only suite that observes rendered pixels:

- **SN-0** — a *differential*: sky luminance sampled at a fixed small angle from the sun versus the
  same angular offset on the opposite side of the sky. This pins the aureole's existence without
  pinning its strength, so re-tuning the glow cannot false-red it. Chosen over an absolute sample
  for exactly the reason B7 was (Architecture §7.1, v1.6).
- **SN-1** — the disc's **red:blue ratio** at high versus low elevation. Reddening is a change in
  channel *ratio*; asserting absolute channel values would pin the tuning constant instead of the
  behaviour.
- **SN-2** — that the disc's peak exceeds the linear bloom threshold, and that the limb retains a
  colour distinct from the core (the guard against Option A collapsing into a white circle).
- **SN-3** — no baseline. Screen-space flare is a post-process the sky suite does not render
  through; in-game only.

Two standing lessons from this system apply and are restated because they have both bitten here
before. **Run the prove-red mutations rather than predicting them** — three of the six original
`Validate Sky Render` baselines passed the exact mutation they were written to catch. And **a
passing prove-red does not validate the expected value** when the contract belongs to a consumer
the suite never runs (RF-3's B61). SN-2's consumer is URP's bloom pass, which no suite executes, so
**SN-2 cannot be called sound without eyes on real output** regardless of baseline colour.

### 7.1 What SN-0 changed about this plan

SN-0 shipped 2026-08-15 and confirmed in game. Three things it found are worth more than the phase itself.

**The glow is a BLEND, not an addition — and LDR is why.** The design assumed an additive term. It
cannot be: the authored sky beside the sun already sits at **0.78–0.88**, so an additive glow pushed the
sky past 1.0 and clipped it flat, and once the disc was lifted to match, *all three channels clipped
across the whole disc* — a flat white circle with the polish arc's limb darkening destroyed. The shipped
form blends sky and disc toward a bright warm tint by the same factor, which cannot exceed 1.0 by
construction. Measured across a full authored day: no hole (disc rim above adjacent sky by +0.046 to
+0.240), limb gradient intact (centre above rim by 0.026–0.119), **max channel anywhere 0.9932**.

This is the strongest evidence yet for §3.2's rejected Option B. The LDR headroom next to the sun is
*already spent by the authored sky*, so every future sky effect faces the same squeeze. That argument
belongs in the tonemapping doc when it is written; it is not a reason to reopen §3.2 here.

**SN-1 is not as independent as the table claimed.** `:416`'s `lerp`-to-fog paints the disc ~92 % fog
colour near the horizon. Give the sky a glow the disc does not get, and the sky beside the sun outruns
the sun — measured at sunrise, disc 0.557 against 0.784 immediately outside. SN-0 had to apply its blend
to the disc as well purely to defend against a term SN-1 will delete. When SN-1 lands, re-check whether
that defence is still doing work or has become redundant.

**`Validate Sky Render` B4 was a false green, demonstrated not argued.** B4 ("the sun disc is brighter
than the sky around it") **passed the regression above**, reporting `centre 0.9682 outshines sky 0.4803`
while the sun visibly rendered as a hole in a screenshot. Three independent fixture faults, any one of
which was sufficient: it ran on `SkyPreviewState.Uniform`, which zeroes `FogRange` and so made the disc's
haze term a **no-op**; its sun sat at mid elevation, where haze is weak; and it sampled the sky at a
**frame corner**, where the aureole has fallen off, rather than beside the disc. B4 now runs both
fixtures — the original mid-sun/no-fog one keeps its original 1.5× margin **unchanged**, and a second
low-sun/fog-on pass samples just outside the rim. Re-running the regression against the repaired B4
reds it: `a low sun's centre (0.5054) still outshines the sky just outside its rim (0.7496)`.

**And B8's own night assertion was a false green on its first draft.** Written with both probes at a
fixed elevation, it passed the twilight-fade removal it exists to catch: with the sun 80° down, both
probes sit more than 90° away, where `saturate(dot(view, sun))` is zero regardless of the fade, so the
term under test never entered the expression. Rewritten to place probes by **true angular rotation** from
the sun axis, the same mutation reds it (0.4893 vs 0.3318). Note this is the *third* time in one session
that a probe placed by elevation or azimuth could not reach a defect near the poles — an azimuth offset
shrinks by cos(elevation), which at −80° put a nominal 3° probe **inside** the 1.5° disc and reported the
disc as sky. Angular offsets are now the rule in this suite; `AngularOffset` exists for it.

### Extension roadmap (post-SN-3, in intended order)

| Version | Extension |
|---------|-----------|
| **v2** | Aureole colour authored on `TimeOfDaySettings` and exposed in the Sky Editor, rather than a code constant — matches how every other sky colour is authored. |
| **v3+** | Volumetric light shafts through the aureole — gets its own design doc, and belongs with the `VX-*` backlog rather than here. |

**Neutral tonemapping is deliberately absent from this table.** It is not a sun extension; §3.2
Option B records why it gets its own design doc, and §1 lists it as a scope boundary. Nothing in
SN-0..SN-3 is written to be revisited when it lands — if it ever does, SN-2's radial ramp becomes
redundant rather than wrong, and can be simplified away in that doc's re-tune pass.

---

## 8. Open questions

One remains, and it is deliberately **not** resolvable on paper.

1. **Does the aureole need to fade with `_StarBrightness` or the RF-1 light curve at all?** The
   design keys it purely on sun geometry and haze, which is the physically motivated choice and
   the one SN-0 implements. Whether a very dim sky should *also* dim the aureole is a taste
   question about a rendered image, so it is **answered by SN-0's in-game pass, not before it** —
   settling it from the shader source would be guessing at a look. If the answer turns out to be
   yes, it is a one-term multiply inside SN-0 and changes no other phase.

   The `starFade` interaction noted in §4 is the concrete thing to look at during that pass: stars
   key on sun elevation rather than local sky brightness, so the sun-adjacent sky is where a
   disagreement would show up first.

---

## Document History

* **v1.1** - **SN-0 shipped** (2026-08-15), confirmed in game. New §7.1. The additive glow the design
  assumed was replaced by a **blend** — the authored sky beside the sun already occupies 0.78-0.88 of
  the LDR range, so adding clipped both the sky and the disc flat and destroyed the limb darkening.
  §7.1 also records that B4 of `Validate Sky Render` **passed a regression visible in a screenshot**
  (uniform fixture with fog off, mid-elevation sun, corner sample) and that B8's first night assertion
  passed its own mutation for a fourth reason — probes placed by elevation cannot reach a sun 80 degrees
  below the horizon. Both repaired and re-reddened by mutation.
* **v1.0** - Initial design

---

**Last Updated:** 2026-08-15  
**Next Review:** when SN-1 starts — §7.1 leaves it a specific question about SN-0's disc defence
