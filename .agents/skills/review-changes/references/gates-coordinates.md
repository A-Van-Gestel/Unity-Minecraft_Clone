# Gates — coordinate precision

Loaded when the diff touches shader code, or any computation that turns a world
position into a float. One gate, and it is the one this engine keeps re-learning:
the world is ±2³¹ voxels wide, and float32 does not span it.

---

## Gate 13 — Absolute world coordinate reaches a precision-sensitive float computation

**What fails.** A float32's resolution is proportional to its magnitude — roughly
`distance × 1.2e-7`. A value derived from the player's distance to the **world
centre** therefore arrives with a step size that grows as you travel, and any
computation sensitive to small changes in it degrades smoothly to garbage: a
`sin`/`cos` argument quantizes until an animation steps and then freezes, and a
noise sample position quantizes until neighbouring pixels collapse onto one
lattice cell and the field goes flat.

The floating origin (WS-4) exists precisely so rendered positions stay small. This
gate fires on code that **undoes** it — reconstructing the absolute coordinate
after the engine went to the trouble of removing it.

Shipped instances, both from the same idiom (`worldPos + _WorldOriginOffset`):

- **FL-1 foliage sway** — froze with distance. Fixed `ad0f28c3` / `df496f14`; see
  `Assets/Scripts/Helpers/FoliagePhase.cs` for the shape of the fix.
- **`LiquidCore.hlsl`** — water flattens to a uniform colour. Still open:
  `Documentation/Bugs/FLUID_BUGS.md` #20.

Time is the same axis with a different name: `frequency * _Time.y` grows without
bound over a session and quantizes identically. Treat an unbounded clock in a
periodic argument as the same finding.

**Why it survives review and testing.** At the identity origin the offset is
**zero**, so every such computation is exact at spawn and in every editor preview.
This is the "hidden identity" that §5 of
`Documentation/Architecture/WORLD_SCALING_FLOATING_ORIGIN.md` records WS-4 breaking
— a correct-looking test near spawn is not evidence. Ask for the behaviour at
10⁶ blocks out, not for a screenshot of the origin.

**How to check.** Route from the file list first — any diff under
`Assets/Shaders/` earns this gate outright — then scan content:

```bash
# $RANGE is whatever step 1 resolved: "" (unstaged), --staged, @{u}...HEAD, <base>...HEAD
git diff $RANGE -- '*.hlsl' '*.shader' | grep -nE '^\+.*(_WorldOriginOffset|worldPos|positionWS|_Time)'
git diff $RANGE -- '*.cs'             | grep -nE '^\+.*(OriginVoxel|WorldOrigin|SetGlobalVector|SetGlobalFloat)'
```

For each hit, follow the value to its consumer and answer one question: **does a
value proportional to world distance reach a `sin`/`cos`, a noise sample, or any
other function of a small difference?** If yes, it is a finding regardless of how
correct it looks at spawn.

**The fix depends on whether the consumer is periodic** — do not propose one
without checking, because only half the problem has an exact answer:

- **Periodic** (`sin`, `cos`, anything cyclic): exact. Reduce the large term
  modulo the period on the CPU in `double` and push a small constant. Each wave
  needs its **own** reduction — scaling one already-reduced phase by a multiplier
  does not survive the reduction.
- **Aperiodic** (simplex/Perlin/fbm): no exact reduction exists. The field itself
  has to be made periodic and the offset wrapped, which is a design decision with
  real trade-offs — route to `Documentation/Bugs/FLUID_BUGS.md` #20, which holds
  the open questions, rather than inventing an answer in a review.

**Not a finding.**

- **Integer coordinate math.** `int`/`Vector3Int`/`ChunkCoord`/`ChunkMath` are
  exact to ±2³¹ — that is why the engine routes coordinates through them. Only the
  conversion to float is suspect.
- **`frac(worldPos)` and other multiple-of-16-invariant math.** Documented as
  deliberate in `WORLD_SCALING_FLOATING_ORIGIN.md` §4.6; the origin shift is always
  a whole number of chunks, so these are unaffected.
- **Chunk-local, render-space, or otherwise bounded values.** A vertex position in
  object space, or a Unity-space position after re-anchoring, is small by
  construction. The gate is about magnitude, not about the word "world".
- **A position that only feeds a comparison, a `lerp`, or a distance test.** These
  degrade gracefully; the gate is about functions of *small differences* in a
  *large* value.
