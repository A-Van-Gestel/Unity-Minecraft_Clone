# Known Fluid related bugs

This document outlines **open** bugs related to fluid behavior and simulation. Resolved bugs are archived in [`_FIXED_BUGS.md`](./_FIXED_BUGS.md).

> **Last reviewed:** March 2026

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

**Not part of this bug:** the fluid *shader* rendering flat blue (flow vectors collapsing) at that magnitude is
the accepted cosmetic liquid-noise precision limitation (`WORLD_SCALING_FLOATING_ORIGIN.md` §9).
