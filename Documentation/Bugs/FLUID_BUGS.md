# Known Fluid related bugs

This document outlines **open** bugs related to fluid behavior and simulation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** August 2026

---

## 02. No player effect

**Severity:** Missing Feature  
**Files:** `Player.cs`, `PlayerInteraction.cs`

Fluid voxels do not currently affect the player:

- Player can walk through fluid without slowing down
- No buoyancy / swimming simulation
- No on-screen visual to indicate submersion

---

## 04. No fluid interaction between different fluid types — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.cs` — `HandleFluidFlow` (lines 334–346)

Water and lava currently do not interact with each other. In Minecraft, water touching lava creates cobblestone or obsidian. This is intentionally unimplemented for now — the collision logic is silently skipped (water simply won't flow into lava), which is safe.
Implementing proper fluid interaction requires a new interaction table and is deferred as a feature, not a bug fix.

---

## 09. Missing Flow-Blocking Logic for Non-Solid Blocks — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (not a bug)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockType.cs`

Currently, fluid spread is gated purely by whether the target block is `Air` (id 0). Non-solid blocks (e.g., torches, ladders, signs) will simply be washed away or ignored.
We need a fluid-interaction tag or explicit list for specific non-solid blocks that should physically block fluid flow identical to a solid block (e.g., doors preventing water from entering a room).

---

## 12. Missing Lava Fire Spreading — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Simulation)  
**Files:** `BlockBehavior.Fluids.cs`, `BlockStationary.java` (Reference)

In Minecraft, both stationary and flowing lava periodically schedule random ticks that can set nearby air blocks on fire if they are adjacent to flammable blocks.
Our fluid engine currently has no random ticking for fluids after they settle, and lava does not interact with surrounding blocks to ignite them.

---

## 13. Missing Block Displacement & Destruction — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (System)  
**Files:** `BlockBehavior.Fluids.cs`

Currently, our fluids only spread into `BlockIDs.Air`. In Minecraft, fluids can flow into certain non-solid blocks (e.g., tall grass, flowers, torches, redstone, rails).
When they do, the fluid displaces the block, destroys it, and drops it as an item entity.

---

## 14. Missing Entity Pushing & Buoyancy — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Physics)  
**Files:** `Player.cs`, `Physics/VoxelRigidbody.cs`, `Entity` base classes

Flowing liquids in Minecraft apply a physical pushing force to any entities (players, mobs, dropped items) caught inside them, moving them in the direction of the flow vector. Additionally, dropped items float upwards to the surface of water (buoyancy).
Our custom `VoxelRigidbody` physics do not currently query fluid flow vectors or apply buoyancy.

---

## 15. Missing Fluid Particles & Audio — ⚠️ MISSING FEATURE

**Severity:** Missing Feature (Visuals/Audio)  
**Files:** (New Particle/Audio Systems required)

Minecraft fluids spawn ambient particles and sounds. Water drips through solid ceilings if water is directly above them. Lava emits popping ember particles above its surface.
Both fluids feature ambient background audio (flowing, bubbling) and interaction audio (splashing, hissing when extinguishing fire). Our engine lacks these environmental details.

---

## 16. Suboptimal Fluid Flow Texturing and Vector Math

**Severity:** Improvement (Visuals/Simulation)  
**Files:** `BlockBehavior.Fluids.cs`, `MeshGenerationJob.cs` (`VoxelMeshHelper.cs`), `UberLiquidShader.shader`

While fluid flow vectors are currently calculated and passed to the shader, the visual result and the underlying simulation math are only "functional" at best.
The bilinear interpolation of flow vectors across fluid surfaces can lead to awkward stretching, pinching, or unnatural texture warping in the `UberLiquidShader`.
Future improvements should refine the flow vector derivatives in the meshing job and implement more advanced flowmap rendering techniques (e.g., improved dual-phase crossfading or flowmap texture synthesis) to achieve a highly polished and natural liquid surface.

**Partial improvements (March 2026):** The flow derivative math in `CalculateSymmetricCornerFlow` was significantly improved with a corner-aware accessibility guard that prevents diagonal air behind walls from creating artificial flow gradients,
while preserving natural waterfall edge pull via `GetEffectiveFluidHeight`. The shore push (`CalculateSymmetricCornerShorePush`) received the same guard with a `FluidType == None` check to prevent fluid blocks from being incorrectly promoted to wall status.

---

## 17. Naturally-Generated Fluids Don't Reactivate on Neighbor Break at Far Coordinates

**Severity:** Low (far-lands only; normal-play range unaffected)  
**Status:** Open — logged 2026-07-19 during the PLAYER_BUGS 03 far-coordinate re-test (fresh world, editor/Mono).  
**Files:** suspected `Chunk.cs` (`OnDataPopulated` / active-voxel registration), `World.cs` (`ApplyModifications` neighbor re-activation), `BlockBehavior.cs`

**Description:**

Observed at `/teleport 2147000000 ~ 0` (≈ +2.147×10⁹ voxels, well inside the ±2³¹ edge): breaking a block
adjacent to a **naturally-generated** fluid (ocean/lake water) does not wake the fluid — it never flows into the
opened cell. **Player-placed fluids at the same location flow and behave correctly**, so the tick simulation
itself works there; the failure is specific to waking *generation-time* fluid voxels, pointing at the
active-voxel registration or the neighbor re-activation trigger on the modification path.

**Onset unbracketed:** fluids were not specifically tested at the lower magnitudes of the same session
(+16,800,000 / +2×10⁷), so it is unknown whether this is the ±2²⁴ float class (like `_FIXED_BUGS.md` lighting
#24 / Player #03) or something else. Bracketing the onset is the first diagnostic step.

**Root Cause Suspected (unconfirmed):** a remaining int→float round-trip (or `Vector3Int`→`Vector3` implicit
conversion) on the path that registers/re-activates generated fluid voxels — same class as the seams `ed8cb69`
fixed for mod routing. Note the dev-build `WorldData.AssertWithinFloatPrecision` tripwire did NOT fire during
the session, so the offending path (if float) does not go through the guarded chunk-query APIs.

**Reproduction Steps:**

1. Fresh world, `/teleport 2147000000 ~ 0`; find naturally generated water (ocean/pond).
2. Break a block directly adjacent to (beside/below) a water voxel → water never flows into the gap.
3. Place a water block from the hotbar nearby → it flows normally.
4. To bracket the onset, repeat at +16,777,300 and +2×10⁷.

**Not part of this bug:** the fluid *shader* rendering flat blue at that magnitude — that is now tracked in its
own right as #20 below (it was previously an accepted limitation, not a bug entry).

---

## 20. Fluid surfaces degrade to a near-uniform color with distance from the world centre

**Severity:** Medium (cosmetic, but onset is well inside normal play range — not far-lands-only)  
**Status:** Open — logged 2026-08-16 from a user in-game observation, following the same-root-cause analysis of the
foliage sway bug. **Reclassifies** what `WORLD_SCALING_FLOATING_ORIGIN.md` §4.6/§9 recorded as an accepted
limitation (see "Relationship to the WS-4 decision" below).  
**Files:** `Assets/Shaders/Includes/LiquidCore.hlsl` (`LiquidNoisePos` :118, `snoise` :156, `fbm` :207, water frag
:404–440, lava frag :326–360), `Assets/Scripts/World.cs` (:1717, sets `_WorldOriginOffset`)

**Description:**

Water surfaces progressively lose their surface detail the further the player is from the world centre: the
wave/ripple pattern flattens and the surface trends toward a single uniform color, with foam disappearing. The
degradation is gradual and monotone in distance, not a cliff. Confirmed in-game for water; lava is expected to
share it (same code path, untested). Near the ±2³¹ edge the endpoint was already recorded as "flat blue".

**Root cause (analysis, not yet instrumented):**

`LiquidNoisePos` (:118) reconstructs an *absolute* voxel-space position, `worldPos + _WorldOriginOffset`, and the
frag functions feed it straight into `fbm`/`snoise` (`noisePos * _WaveScale` :406, `* _RippleScale` :407,
`* _WaveScale * 2.0` :425, and the lava equivalents at :328/:348/:360). `_WorldOriginOffset` is `OriginVoxel`
(`World.cs:1717`), so its magnitude is the player's distance from the world centre, and it is passed raw.

The float32 ULP of `noisePos` is therefore about `D * 1.2e-7` **blocks** at distance `D`. Once that exceeds the
finest fbm octave's feature size, neighboring pixels of a water surface quantize onto the same noise lattice
input and `combined_noise` (:414) goes constant. Because that single value is the sole modulator of both the
color `lerp` (:417) and `foamAmt` (:418), the surface flattens to one color and loses foam together — which is
exactly the observed symptom. Order-of-magnitude onset is ~1e5–1e6 blocks depending on the authored scales;
**the onset has not been bracketed in-game and should be measured first.**

**Shared root cause with the foliage sway bug.** The same idiom — reconstructing an absolute coordinate inside a
shader by adding `_WorldOriginOffset` back to a render-space position — breaks FL-1 foliage sway
(`VoxelCommon.hlsl:63`), there as a *temporal* freeze rather than a spatial flattening. The two are one defect
class with two symptoms. The sway half is fixable exactly (`mod 2π` phase reduction) and is being handled
separately; this entry is the half that needs a design pass.

**Relationship to the WS-4 decision (read before working this):**

`WORLD_SCALING_FLOATING_ORIGIN.md` §4.6 deliberately passes the offset raw and §9 lists the resulting degradation
as an accepted cosmetic limitation. Its stated reason is sound and still applies: *"a periodicity `fmod` does not
cleanly exist for simplex across the shader's several scales."* This entry does not claim that reasoning was
wrong — it reopens the item because two things have changed:

1. The onset is at ~1e5 blocks, not the ±2³¹ edge where §9 catalogued it. That is inside reachable play, which
   makes it a bug rather than an edge-of-the-world curiosity.
2. A route the original decision did not consider: make the noise field itself **periodic** with period `P` and
   wrap the offset, rather than trying to `fmod` an aperiodic field. The several-scales objection then becomes a
   solvable constraint (snap each effective scale so `P * scale` is an integer) instead of a blocker.

Reopening a locked WS-4 decision is a deliberate call, so record the outcome in the WS-4 doc either way.

**Proposed direction (not decided — needs its own design pass):**

- Replace the unbounded `_WorldOriginOffset` in shader-land with a **wrapped** offset (`OriginVoxel mod P`),
  bounded and exactly representable, giving uniform precision at any distance.
- Make the noise **tile** with period `P` so the wrap is exactly invisible. A plain wrap without tiling
  re-randomizes the whole pattern in one frame every `P` blocks travelled — rejected on the grounds that a
  visible pop is worse than the degradation it fixes.
- Open decisions, all interdependent, to settle in the design pass:
  - **Noise route.** Periodic simplex (`psrdnoise`) preserves the current water look but is a larger per-pixel
    function and constrains `P` to a multiple of 3 in the skewed lattice; periodic Perlin/value noise tiles
    trivially and is cheaper but *changes the water's appearance* (RF-2's rendered-pixel baselines would flag it,
    correctly).
  - **`P`.** Trades repeat visibility against noise-space precision. `WorldOrigin.ShiftThresholdChunks` (64
    chunks = 1024 blocks) was proposed for conceptual coherence; it works on precision (~1.2e-4 block ULP) but is
    at the small end for repeat distance. ~4096 blocks sits mid-range with margin on both sides. Note the two
    constants optimize different budgets, so couple them by documentation rather than by definition. Fix the
    noise route before fixing `P`.
  - **Scale snapping.** `P * scale` must be an integer at *every* call site or the seam returns at exactly the
    one that was missed — and a missed site only shows up `P` blocks from spawn, which is cheap to get wrong and
    expensive to observe. Snapping in-shader (`scale = round(scale * P) / P`) is imperceptible at these
    granularities.
- Free, if the above lands: fbm octaves inherit the period (doubling an integer stays integer), `flow3D` is a
  bounded translation of a periodic field so it stays periodic, and only XZ needs a period at all —
  `OriginVoxel.y` is pinned to 0 by a ChunkMath validation baseline, so the Y-axis `_Time.y` scroll is untouched.

**Generalization worth doing alongside (prevents a third instance):**

Both known instances came from reaching for an unbounded origin global. Consider collapsing shader access to the
origin behind a single include that exposes only pre-reduced, bounded values (a wrapped position for aperiodic
fields, a `mod 2π` phase for periodic ones) and retiring the `_WorldOriginOffset` name. The stronger guard is a
**far-origin render baseline** — render a fixed scene at origin `(0,0)` and at `(1<<20, 1<<20)` and assert the
frames match. That tests the symptom class rather than a forbidden identifier, so it catches any future feature
that smuggles an absolute coordinate into a shader. RF-2's rendered-pixel suite already provides the machinery,
and such a baseline would have caught both known instances while correctly leaving clouds green.

**Reproduction Steps:**

1. Load any world with visible water (ocean/lake) and note the surface wave/ripple detail and foam at spawn.
2. `/teleport` outward in steps — suggested 1e4, 1e5, 1e6, 1e7 — observing the same kind of water body at each.
3. Surface detail flattens progressively; foam thins then disappears; the surface trends to a uniform color.
4. Bracketing the onset (step 2) is the first diagnostic task — the ~1e5–1e6 estimate is derived, not measured.

**Not part of this bug:** the FL-1 foliage sway freeze (same root idiom, separate fix); the fluid *simulation*
failing to reactivate at far coordinates (#17 above, a CPU-side integer-routing issue); terrain generation noise
precision past ±2²⁴ (the FNL rider, `WORLD_SCALING_IMPLEMENTATION.md` §6); and the striped cloud *field* near
±2³¹ (`WORLD_SCALING_FLOATING_ORIGIN.md` §9 — CPU-side pattern generation, not this shader path; cloud
*drift* is unaffected because `Clouds.cs` wraps it).
