// ReSharper disable CompareOfFloatsByEqualityOperator

using System;
using Helpers;
using UnityEngine;

namespace Physics
{
    public class VoxelRigidbody : MonoBehaviour
    {
        [Header("Physics Settings")]
        [Tooltip("Gravity applied per second when not flying.")]
        public float gravity = -13f;

        [Tooltip("The total height of the physics collider.")]
        [Min(0.1f)]
        public float collisionHeight = 1.8f;

        // A microscopic offset applied to snapped velocities to prevent floating point math from
        // evaluating to exactly equal with the block boundary on subsequent frames.
        private const float COLLISION_EPSILON = 0.001f;
        private const float COLLISION_JITTER_TOLERANCE = 0.001f;

        /// <summary>
        /// How far below the feet a surface may sit and still count as supporting the body.
        /// </summary>
        /// <remarks>
        /// The overlap test in <c>World.CheckPhysicsCollision</c> is strict, so a body resting on a surface —
        /// which the vertical resolve parks <c>COLLISION_EPSILON</c> above it — does NOT overlap that surface.
        /// Probing the un-extended AABB therefore only ever detects an <i>embedded</i> body, and a
        /// correctly-standing one reads as airborne. Must stay above <c>COLLISION_EPSILON</c> (or it cannot span
        /// the stand-off) and far below the thinnest collision volume (0.25) so it never grounds a genuinely
        /// falling body.
        /// <para>
        /// Public because "what is this body standing on" is asked outside the solver too — the footstep audio
        /// resolves which cell carries the feet with this same tolerance, so a slab reads as the support to the
        /// ear exactly when it does to the solver. One definition, or the two answers drift.
        /// </para>
        /// </remarks>
        public const float GroundProbeSkin = COLLISION_EPSILON * 2f;

        [Tooltip("The padding added to the player bounds to avoid snagging flush walls.")]
        [Min(0.1f)]
        public float collisionWidthX = 0.8f;

        [Tooltip("The total depth of the physics collider (Z axis).")]
        [Min(0.1f)]
        public float collisionDepthZ = 0.8f;

        [Tooltip("Internal padding to prevent floating-point edge snagging. Keeps sweeping rays slightly inwards.")]
        [Range(0.0f, 0.1f)]
        public float collisionPadding = 0.001f;

        [Tooltip("Render the physics bounding box in the Scene/Game view.")]
        public bool showBoundingBox = false;

        [Tooltip("Standard Minecraft slab step height in meters.")]
        [Min(0f)]
        public float stepHeight = 0.5f;

        [Tooltip("Step height while swimming, in meters — how big a ledge a body can haul itself onto out of " +
                 "water.\nLarger than the walking step height on purpose: a floating body's feet sit most of a " +
                 "block below the surface, so a bank it is looking straight at is still far out of stepping " +
                 "range. Sized to clear a one-block bank and refuse a two-block wall.")]
        [Min(0f)]
        public float swimStepHeight = 1.45f;

        [Tooltip("Require the jump button to be released before it can jump again, after climbing out of a " +
                 "fluid.\nSwimming up is done by HOLDING jump, so without this the body jumps the moment it " +
                 "lands on the bank and the climb reads as one big leap instead of a hop out of the water. " +
                 "Turn off to let a held jump resume immediately on landing.")]
        public bool requireJumpReleaseAfterFluid = true;

        [Tooltip("How long a step-up's vertical snap takes to catch up visually, in seconds.\n" +
                 "0 disables smoothing entirely — the view jumps the instant the collider does, which is how " +
                 "step-ups behaved before this existed.\nHigher values ease the rise out over longer, which " +
                 "reads as stepping up rather than teleporting. Per body, so a heavy entity can lag more than " +
                 "the player.")]
        [Range(0f, 0.5f)]
        public float stepSmoothing = 0.12f;

        public float CollisionHalfWidthX => collisionWidthX * 0.5f;
        public float CollisionHalfDepthZ => collisionDepthZ * 0.5f;

        // TF-14: extra gap (in voxels) kept between the player collider and the world border,
        // so the body doesn't visually clip through the border wall. Added to the collision half-extent.
        private const float BORDER_MARGIN = 0.5f;

        [Header("Movement Settings")]
        [Tooltip("Jump velocity applied when jumping.")]
        public float jumpForce = 5.7f;

        [Tooltip("The normal horizontal movement speed multiplier.")]
        public float walkSpeed = 3f;

        [Tooltip("The sprinting horizontal movement speed multiplier.")]
        public float sprintSpeed = 6f;

        [Tooltip("The horizontal flying speed multiplier.")]
        public float flyingSpeed = 3f;

        [Tooltip("The vertical flying speed multiplier.")]
        public float flyingAscendSpeed = 5f;

        [Header("Entity States")]
        public bool isFlying = false;

        public bool isNoclipping = false;
        public bool isSprinting = false;

        /// <summary>
        /// True while a teleport arrival hold suspends this body (CMD-2 §3.3): gravity and movement
        /// freeze until the destination chunk is ready. Set/cleared exclusively by
        /// <see cref="World.TeleportPlayer"/> and its hold poll.
        /// </summary>
        [NonSerialized]
        public bool IsTeleportHeld;

        public bool IsGrounded { get; private set; }
        public Vector3 Velocity { get; private set; }
        public float MoveSpeed { get; private set; }

        /// <summary>
        /// How many jumps this body has taken. Increments on the fixed step that applies the jump impulse.
        /// </summary>
        /// <remarks>
        /// A counter rather than a "jumped this step" flag so a reader polling from <c>Update</c> cannot miss
        /// one: two fixed steps can run between renders, which would clear a flag before anyone saw it.
        /// Readers keep the last value they observed and compare. The audio layer uses this to tell a jump
        /// from walking off a ledge, which look identical from outside the solver.
        /// </remarks>
        public uint JumpCount { get; private set; }

        /// <summary>
        /// The fluid this body is in as of the current tick, or <c>default</c> (<c>FluidType.None</c>) when it
        /// is in air. Refreshed once per <c>FixedUpdate</c>, before any force reads it.
        /// </summary>
        /// <remarks>
        /// Public because "is this body in water, and how deep" is asked outside the solver too — the same
        /// reason <see cref="GroundProbeSkin"/> is. Readers get the solver's own answer instead of running a
        /// second, differently-quantized probe of their own.
        /// </remarks>
        public FluidContact FluidContact { get; private set; }

        /// <summary>
        /// How far <b>below</b> its collider a step-up's view should still be drawn, in meters, decaying to
        /// zero over <see cref="stepSmoothing"/>. Always 0 when smoothing is disabled.
        /// </summary>
        /// <remarks>
        /// <b>Presentation only — nothing in the solve reads this.</b> The collider still snaps, because a
        /// body that climbed gradually would spend those frames inside the geometry it is climbing. Only the
        /// view lags: subtract this from the drawn position and it trails the step, then catches up.
        /// </remarks>
        public float StepSmoothingOffset { get; private set; }

        /// <summary>
        /// The step height in force right now — <see cref="swimStepHeight"/> while in fluid, otherwise
        /// <see cref="stepHeight"/>.
        /// </summary>
        /// <remarks>
        /// Only a <i>floating</i> body gets the swim allowance — one standing on the bottom of shallow water
        /// is walking, and steps like a walker. The allowance is bigger because the float equilibrium parks
        /// the feet most of a block under the surface, putting a one-block bank out of walking-step reach.
        /// That gap is measured from the surface, so it does not grow with depth: one value clears a
        /// one-block bank and still refuses a two-block wall.
        /// </remarks>
        public float EffectiveStepHeight => FluidContact.InFluid && !IsGrounded ? swimStepHeight : stepHeight;

        private float _verticalMomentum;
        private Vector3 _movementIntent;
        private float _verticalFlyingIntent;
        private bool _jumpRequest;
        private float _swimVerticalIntent;
        private float _lastMoveSpeed;

        /// <summary>Vertical distance the step-up pre-pass snapped this tick, awaiting the smoothing handoff.</summary>
        private float _pendingStepRise;

        /// <summary>Whether a jump is being refused until the button is released — see <see cref="requireJumpReleaseAfterFluid"/>.</summary>
        private bool _jumpBlockedUntilRelease;

        /// <summary>The jump button's current state, as last reported by the input layer.</summary>
        private bool _jumpHeld;

        /// <summary>Whether the body was in fluid on the previous tick, for edge detection on the exit.</summary>
        private bool _wasInFluid;

        /// <summary>
        /// Below this the smoothing offset is snapped to zero rather than decayed further — an exponential
        /// decay never actually reaches zero, and a permanently non-zero offset would keep the view a
        /// fraction of a millimeter low forever.
        /// </summary>
        private const float STEP_SMOOTHING_EPSILON = 0.001f;

        /// <summary>
        /// How fast a swim stroke ramps toward its target speed, in m/s². High enough to feel responsive,
        /// finite so the stroke reads as swimming rather than as a jump out of the water.
        /// </summary>
        private const float SWIM_ACCELERATION = 12f;

        /// <summary>
        /// How fast a falling column pulls a body toward its downward current speed, in m/s². Deliberately
        /// below <see cref="SWIM_ACCELERATION"/>: the difference is the rate a swimmer climbs a waterfall.
        /// </summary>
        private const float FALL_CURRENT_ACCELERATION = 8f;

        /// <summary>
        /// The fraction of its still-water stroke speed a body is guaranteed to climb at while swimming up
        /// through a falling column. Below 1 so a waterfall still visibly resists; above 0 so it can always
        /// be escaped.
        /// </summary>
        private const float WATERFALL_CLIMB_FLOOR = 0.35f;

        /// <summary>
        /// Submersion at which a fluid's horizontal speed penalty reaches full strength.
        /// </summary>
        /// <remarks>
        /// The penalty ramps to its authored value over this much of the body and then stays there, rather
        /// than scaling linearly across the whole collider. Linear scaling meant a body floating at the
        /// surface — where submersion is small by construction — kept nearly all of its walking speed and
        /// slid across the top of the water. Knee-deep is where wading already costs most of your speed.
        /// </remarks>
        private const float FULL_HORIZONTAL_DRAG_SUBMERSION = 0.25f;

        /// <summary>
        /// PH-1: this body's gathered voxel neighborhood, refilled once per resolve and read by every sweep.
        /// Per-instance rather than shared, so entities do not clobber each other's gather.
        /// </summary>
        private readonly PhysicsCellBuffer _cellBuffer = new PhysicsCellBuffer();

        private World _world;

        private void Start()
        {
            _world = World.Instance;
        }

        /// <summary>
        /// Applies horizontal movement intent. The vector should be the normalized forward/right inputs.
        /// </summary>
        public void SetMovementIntent(Vector3 inputDirection)
        {
            // Normalize to prevent diagonal acceleration
            if (inputDirection.magnitude > 1.0f)
                inputDirection.Normalize();

            _movementIntent = inputDirection;
        }

        /// <summary>
        /// Applies vertical flight intent (usually -1 to 1 based on jump/crouch keys).
        /// </summary>
        public void SetVerticalFlyingIntent(float verticalInput)
        {
            _verticalFlyingIntent = verticalInput;
        }

        /// <summary>
        /// Indicates the entity wishes to jump this frame.
        /// </summary>
        /// <remarks>
        /// Gated on <see cref="IsGrounded"/>, and on not still holding the button that carried the body out
        /// of a fluid — see <see cref="requireJumpReleaseAfterFluid"/>. A refused request is simply not
        /// latched, so <c>PLAYER_BUGS</c> §04's distinction between "refused" and "ineffective" still reads
        /// off the latch.
        /// </remarks>
        public void RequestJump()
        {
            if (_jumpBlockedUntilRelease) return;

            if (IsGrounded && !isFlying)
            {
                _jumpRequest = true;
            }
        }

        /// <summary>
        /// Reports the jump button's state for this frame. Releasing it clears a post-fluid jump block.
        /// </summary>
        /// <param name="held">Whether the jump button is currently down.</param>
        /// <remarks>
        /// Separate from <see cref="RequestJump"/> because the solver needs the <i>falling</i> edge, which a
        /// request-only API never delivers: the input layer stops calling <c>RequestJump</c> when the button
        /// comes up, which is indistinguishable from it simply not being a jump frame.
        /// </remarks>
        public void SetJumpHeld(bool held)
        {
            _jumpHeld = held;
            if (!held) _jumpBlockedUntilRelease = false;
        }

        /// <summary>
        /// Applies vertical swim intent for this tick — the in-fluid counterpart of
        /// <see cref="SetVerticalFlyingIntent"/>, and of <see cref="RequestJump"/> for a body that is
        /// swimming rather than standing.
        /// </summary>
        /// <param name="verticalInput">-1 (swim down) to 1 (swim up); 0 is no stroke.</param>
        /// <remarks>
        /// A second entry point rather than a relaxed gate inside <see cref="RequestJump"/>, which stays a
        /// pure gate on <see cref="IsGrounded"/> — widening it would change what a refused jump means.
        /// <para>
        /// <b>Stored, not consumed</b>, like <see cref="_movementIntent"/>, and gated on being in fluid
        /// where it is <i>used</i> rather than here. Clearing it per fixed step would drop strokes whenever
        /// two steps fall between two renders, weakening swimming as the frame rate drops. A caller owes a
        /// zero when it stops driving the body.
        /// </para>
        /// </remarks>
        public void SetSwimVerticalIntent(float verticalInput)
        {
            _swimVerticalIntent = Mathf.Clamp(verticalInput, -1f, 1f);
        }

        /// <summary>
        /// Increments the flying speed.
        /// </summary>
        public void IncrementFlyingSpeed(float amount)
        {
            flyingSpeed += amount;
            if (flyingSpeed <= 0) flyingSpeed = 1f;
        }

        private void FixedUpdate()
        {
            // Wait for world to finish initial load and meshing to prevent falling through terrain,
            // and freeze while a teleport arrival hold waits for its destination chunk (CMD-2 §3.3).
            if (!_world.IsWorldLoaded || IsTeleportHeld) return;

            CalculateVelocity();

            ApplyPendingJump();

            transform.Translate(Velocity, Space.World);

            ClampToWorldBorder();

            CollectStepSmoothing();
        }

        /// <summary>
        /// Converts a latched jump request into upward momentum, after this tick's velocity resolve — so the
        /// impulse is carried by the next tick's movement.
        /// </summary>
        private void ApplyPendingJump()
        {
            if (!_jumpRequest || isFlying) return;

            _verticalMomentum = jumpForce;
            IsGrounded = false;
            _jumpRequest = false;
            JumpCount++;
        }

        /// <summary>
        /// Folds this tick's step-up snap into <see cref="StepSmoothingOffset"/>, so the view has something
        /// to catch up from. Clears the pending rise either way, so a body with smoothing switched off does
        /// not accumulate one.
        /// </summary>
        private void CollectStepSmoothing()
        {
            if (stepSmoothing > 0f && _pendingStepRise > 0f)
            {
                // Capped at the step height: the pre-pass can only lift a body by that much in one go, and a
                // larger reading means several substeps each stepped up — which is a legitimate climb the
                // view should not lag a whole staircase behind.
                StepSmoothingOffset = Mathf.Min(StepSmoothingOffset + _pendingStepRise,
                    Mathf.Max(stepHeight, swimStepHeight));
            }

            _pendingStepRise = 0f;
        }

        /// <summary>
        /// Eases <see cref="StepSmoothingOffset"/> back to zero on the render clock, so the smoothing runs at
        /// display rate rather than in the 50 Hz physics steps it is hiding.
        /// </summary>
        private void Update()
        {
            if (StepSmoothingOffset <= 0f) return;

            if (stepSmoothing <= 0f)
            {
                // Switched off mid-flight: drop the outstanding offset rather than freezing the view low.
                StepSmoothingOffset = 0f;
                return;
            }

            // Exponential ease-out, so stepSmoothing reads as a time constant and the catch-up decelerates
            // into place instead of arriving at full speed and stopping dead.
            StepSmoothingOffset *= Mathf.Exp(-Time.deltaTime / stepSmoothing);
            if (StepSmoothingOffset < STEP_SMOOTHING_EPSILON) StepSmoothingOffset = 0f;
        }

        /// <summary>
        /// Hard-clamps the player's horizontal position inside the per-world gameplay border —
        /// a square AABB centered on the world origin. No-op when the border is disabled
        /// (<see cref="World.BorderRadius"/> is 0). Player-only: the voxel pipeline (generation,
        /// lighting, meshing, storage) is deliberately border-blind, so terrain still exists past
        /// the fence; only the player is stopped.
        /// </summary>
        private void ClampToWorldBorder()
        {
            int radius = _world.BorderRadius;
            if (radius <= 0) return;

            // The border is a voxel-space AABB centered on the WORLD origin while the transform is Unity space, so
            // the limits shift by the origin instead of staying symmetric about the render origin. The border edge
            // and origin resolve in integer math FIRST (both can be huge; near the border they cancel to a small
            // number), and only then does the small fractional collider inset apply in float — subtracting two large
            // floats instead would round the bound off the true border line past ±2²⁴.
            Vector3Int ov = WorldOrigin.OriginVoxel;
            float minX = (-(long)radius - ov.x) + CollisionHalfWidthX + BORDER_MARGIN;
            float maxX = ((long)radius - ov.x) - CollisionHalfWidthX - BORDER_MARGIN;
            float minZ = (-(long)radius - ov.z) + CollisionHalfDepthZ + BORDER_MARGIN;
            float maxZ = ((long)radius - ov.z) - CollisionHalfDepthZ - BORDER_MARGIN;

            // Guard tiny radii from inverting the bounds: pin the player to the border's center line instead.
            if (maxX < minX) minX = maxX = (minX + maxX) * 0.5f;
            if (maxZ < minZ) minZ = maxZ = (minZ + maxZ) * 0.5f;

            Vector3 pos = transform.position;
            float clampedX = Mathf.Clamp(pos.x, minX, maxX);
            float clampedZ = Mathf.Clamp(pos.z, minZ, maxZ);

            // Exact comparison is intended: Mathf.Clamp returns the value itself when it is in range, so this asks
            // "did the clamp change anything" to skip a redundant transform write. A tolerance here would swallow
            // small-but-real clamps right at the border line.
            if (clampedX != pos.x || clampedZ != pos.z)
                transform.position = new Vector3(clampedX, pos.y, clampedZ);
        }

        private void CalculateVelocity()
        {
            // Resolved once per tick, before anything reads it. Fluid forces integrate alongside gravity for
            // the same reason gravity does: ResolveMovement runs once per SUBSTEP, so applying them there
            // would scale every force by the substep count of whatever speed the body happens to be moving.
            ResolveFluidContact();

            // VERTICAL VELOCITY & GRAVITY
            if (!isFlying)
            {
                // Only start accelerating downwards when falling off a block.
                if (IsGrounded && _verticalMomentum < 0)
                    _verticalMomentum = 0f;

                // Affect vertical momentum with gravity.
                if (_verticalMomentum > gravity)
                    _verticalMomentum += Time.fixedDeltaTime * gravity;

                ApplyFluidVerticalForces();
            }
            else
            {
                if (_verticalFlyingIntent != 0)
                    _verticalMomentum += Time.fixedDeltaTime * _verticalFlyingIntent * flyingAscendSpeed;
                else
                    _verticalMomentum = 0;
            }

            // FORWARD & HORIZONTAL VELOCITY
            MoveSpeed = walkSpeed;
            if (isSprinting)
                MoveSpeed = sprintSpeed;

            // Only change moveSpeed multiplier when on the ground or when flying
            if (IsGrounded && !isFlying)
                _lastMoveSpeed = MoveSpeed;
            else if (isFlying)
            {
                _lastMoveSpeed = flyingSpeed;
                MoveSpeed = _lastMoveSpeed;
            }
            else
                MoveSpeed = _lastMoveSpeed;

            // Wading slows the body in proportion to submersion, so ankle-deep water barely bites.
            // Must stay AFTER the _lastMoveSpeed write: that field carries the ground speed into the air,
            // so scaling before it would let one waded step follow the body until it lands again.
            if (FluidContact.InFluid && !isFlying)
            {
                float drag = Mathf.Clamp01(FluidContact.SubmergedFraction / FULL_HORIZONTAL_DRAG_SUBMERSION);
                MoveSpeed *= Mathf.Lerp(1f, FluidContact.SubmergedSpeedMultiplier, drag);
            }

            Velocity = _movementIntent * (Time.fixedDeltaTime * MoveSpeed);

            // Apply vertical momentum (falling / jumping)
            Velocity += Vector3.up * (_verticalMomentum * Time.fixedDeltaTime);

            // The current carries the body regardless of its own input — a body standing still in a river
            // still moves downstream.
            if (FluidContact.InFluid && !isFlying)
                Velocity += FluidContact.FlowDirection * (FluidContact.PushStrength * Time.fixedDeltaTime);

            // COLLISION (Sub-voxel AABB physics solver)
            if (!isNoclipping)
            {
                PhysicsQueryStats.CountTick();
                const float MIN_COLLISION_THICKNESS = 0.25f; // Quarter-slab
                const float maxStep = MIN_COLLISION_THICKNESS * 0.5f; // 0.125m

                // Velocity here is actually the intended displacement for this frame
                float displacementMag = Velocity.magnitude;
                if (displacementMag > maxStep)
                {
                    int substeps = Mathf.CeilToInt(displacementMag / maxStep);
                    Vector3 totalDisplacement = Vector3.zero;
                    Vector3 remainingDisplacement = Velocity;
                    Vector3 subMove = remainingDisplacement / substeps;

                    // PH-2: the running position is a local, not the transform. Each substep must resolve against
                    // where the previous one left the body, but staging that on the transform would leave it holding
                    // a not-yet-final position mid-tick — and a throw inside the loop would leave it there for good,
                    // since the revert that used to undo the staging could never run.
                    Vector3 runningPos = transform.position;

                    for (int i = 0; i < substeps; i++)
                    {
                        // Use the corrected subMove from the previous step as a baseline,
                        // but re-evaluate against current world position.
                        Vector3 currentSubMove = subMove;
                        ResolveMovement(ref currentSubMove, runningPos);
                        runningPos += currentSubMove;
                        totalDisplacement += currentSubMove;

                        // Carry over velocity blocks (if an axis stopped, it stays stopped)
                        if (currentSubMove.x == 0) subMove.x = 0;
                        if (currentSubMove.y == 0) subMove.y = 0;
                        if (currentSubMove.z == 0) subMove.z = 0;
                    }

                    Velocity = totalDisplacement;
                }
                else
                {
                    Vector3 tempVelocity = Velocity;
                    ResolveMovement(ref tempVelocity, transform.position);
                    Velocity = tempVelocity;
                }
            }
        }

        /// <summary>
        /// Refreshes <see cref="FluidContact"/> from the body's current AABB. Clears it while noclipping, so
        /// a ghosting body is not shoved around by water it is deliberately passing through.
        /// </summary>
        private void ResolveFluidContact()
        {
            if (isNoclipping)
            {
                FluidContact = default;
                return;
            }

            Vector3 pos = transform.position;
            Bounds bodyAABB = new Bounds();
            bodyAABB.SetMinMax(
                new Vector3(pos.x - CollisionHalfWidthX, pos.y, pos.z - CollisionHalfDepthZ),
                new Vector3(pos.x + CollisionHalfWidthX, pos.y + collisionHeight, pos.z + CollisionHalfDepthZ));

            _world.GatherFluidContact(bodyAABB, out FluidContact contact);
            FluidContact = contact;

            // Climbing out IS holding jump, so without this the body jumps on the frame it lands and the
            // climb and the jump fuse into one launch. Edge-triggered, so re-entering the water re-arms it.
            if (_wasInFluid && !FluidContact.InFluid && requireJumpReleaseAfterFluid && _jumpHeld)
                _jumpBlockedUntilRelease = true;

            _wasInFluid = FluidContact.InFluid;
        }

        /// <summary>
        /// Applies buoyancy, vertical drag and the swim stroke to <see cref="_verticalMomentum"/>, after
        /// gravity has been integrated for this tick.
        /// </summary>
        /// <remarks>
        /// Buoyancy cancels a <i>fraction of gravity</i> rather than adding an upward force, so an authored
        /// 1 is exactly neutral at full submersion and no tuning produces runaway lift. Scaling it by
        /// <see cref="FluidContact.SubmergedFraction"/> settles a body at the surface: the support falls
        /// away as it rises.
        /// <para>
        /// Drag decays exponentially rather than subtracting linearly, so it cannot overshoot past zero and
        /// reverse the body — which would read as a bounce at the surface.
        /// </para>
        /// </remarks>
        private void ApplyFluidVerticalForces()
        {
            if (!FluidContact.InFluid) return;

            float submersion = FluidContact.SubmergedFraction;

            // Cancel the authored fraction of this tick's gravity pull.
            _verticalMomentum -= Time.fixedDeltaTime * gravity * FluidContact.Buoyancy * submersion;

            // Exponential decay toward zero; never overshoots however large the coefficient is authored.
            float drag = FluidContact.VerticalDrag * submersion;
            if (drag > 0f)
                _verticalMomentum *= Mathf.Exp(-drag * Time.fixedDeltaTime);

            // A falling column drags the body toward its current speed. A momentum target rather than
            // displacement, so the stroke below acts on the same axis and can gain on it — the gap between
            // the two accelerations is the rate a swimmer climbs a waterfall.
            if (FluidContact.IsFalling)
            {
                _verticalMomentum = Mathf.MoveTowards(_verticalMomentum,
                    -FluidContact.PushStrength * submersion, FALL_CURRENT_ACCELERATION * Time.fixedDeltaTime);
            }

            if (_swimVerticalIntent != 0f)
            {
                // Scaled by submersion so the stroke fades as the body rises, settling it at the waterline
                // rather than carrying it clear of the pool.
                float target = _swimVerticalIntent * FluidContact.SwimAscendSpeed * submersion;

                // Accelerated toward rather than snapped to, so the stroke composes with the buoyancy and
                // drag above. The authority is scaled by submersion too: a body mostly out of the water gets
                // little purchase and sinks back, which is what holds the float line under the surface.
                _verticalMomentum = Mathf.MoveTowards(_verticalMomentum, target,
                    SWIM_ACCELERATION * submersion * Time.fixedDeltaTime);

                // A waterfall slows a climb; it does not forbid one. A gameplay floor rather than physics —
                // an honest force balance nets downward here, and a waterfall that cannot be climbed reads
                // as a trap. Tuning-independent, so no authored value can close the exit.
                if (FluidContact.IsFalling && _swimVerticalIntent > 0f)
                    _verticalMomentum = Mathf.Max(_verticalMomentum, target * WATERFALL_CLIMB_FLOOR);
            }
        }

        /// <summary>
        /// Resolves one displacement against the voxel world — the step-up pre-pass, the per-axis horizontal
        /// resolve in Z → X order, and the vertical resolve that sets <see cref="IsGrounded"/>.
        /// </summary>
        /// <param name="movement">The intended displacement, corrected in place.</param>
        /// <param name="pos">The feet-center position to resolve from. Passed in rather than read from the
        /// transform so the substep chain can advance it in a local (<c>PH-2</c>); callers resolving a single
        /// displacement pass <c>transform.position</c>.</param>
        private void ResolveMovement(ref Vector3 movement, Vector3 pos)
        {
            float extX = CollisionHalfWidthX - collisionPadding; // Keeping slight inset to avoid snagging flush walls
            float extZ = CollisionHalfDepthZ - collisionPadding;
            float h = collisionHeight;

            // Build entity AABB
            Bounds currentAABB = new Bounds();
            currentAABB.SetMinMax(
                new Vector3(pos.x - extX, pos.y, pos.z - extZ),
                new Vector3(pos.x + extX, pos.y + h, pos.z + extZ)
            );

            GatherCells(currentAABB, movement);

            // Predict horizontal future AABB (NO Y movement, slightly shrunk on Y to avoid floor/ceiling snags)
            Bounds horizontalFutureAABB = currentAABB;
            horizontalFutureAABB.SetMinMax(
                new Vector3(currentAABB.min.x, currentAABB.min.y + collisionPadding, currentAABB.min.z),
                new Vector3(currentAABB.max.x, currentAABB.max.y - collisionPadding, currentAABB.max.z)
            );
            horizontalFutureAABB.center += new Vector3(movement.x, 0, movement.z);

            // 1. Step-Up Pre-pass
            bool groundedByStep = false;
            bool zBlocked = false;
            bool xBlocked = false;
            int zSign = 0, xSign = 0;

            if (movement.z != 0f)
            {
                zSign = movement.z > 0 ? 1 : -1;
                zBlocked = Probe(horizontalFutureAABB, axis: 2, zSign, out _);
            }

            if (movement.x != 0f)
            {
                xSign = movement.x > 0 ? 1 : -1;
                xBlocked = Probe(horizontalFutureAABB, axis: 0, xSign, out _);
            }

            bool horizontalBlocked = zBlocked || xBlocked;

            float step = EffectiveStepHeight;

            // If blocked and supported, attempt step-up with ORIGINAL movement. Buoyancy counts as support,
            // since a swimmer is never grounded. Climbing out also requires ASKING to rise — twice a walking
            // step, so it is the player's call rather than a consequence of touching the bank.
            bool climbingOutOfFluid = FluidContact.InFluid && _swimVerticalIntent > 0f;
            if (horizontalBlocked && (IsGrounded || climbingOutOfFluid) && !isFlying)
            {
                Bounds liftedAABB = horizontalFutureAABB;
                liftedAABB.center += Vector3.up * step;

                bool clearsAtStep = true;
                if (movement.x != 0f)
                    clearsAtStep &= !Probe(liftedAABB, axis: 0, xSign, out _);
                if (movement.z != 0f)
                    clearsAtStep &= !Probe(liftedAABB, axis: 2, zSign, out _);

                if (clearsAtStep)
                {
                    // Sweep DOWNWARD to find highest support surface
                    Bounds sweepAABB = liftedAABB;
                    sweepAABB.Expand(new Vector3(0, step, 0));
                    sweepAABB.center -= new Vector3(0, step * 0.5f, 0);

                    if (Probe(sweepAABB, axis: 1, -1, out var groundContact))
                    {
                        // Found support
                        float newY = groundContact.ContactFace;
                        movement.y = newY - pos.y; // Instant vertical snap
                        movement.y += COLLISION_EPSILON; // Stop slightly short
                        groundedByStep = true;
                    }
                    else
                    {
                        // No support found, step onto air
                        movement.y = step;
                    }

                    // SUCCESS: horizontal velocity is preserved as-is (no correction applied).
                    horizontalBlocked = false;
                    horizontalFutureAABB.center += Vector3.up * movement.y;

                    // Hand the snap to the smoothing offset. Accumulated rather than assigned because a
                    // single tick can substep, and each substep may step up in turn — the view owes the
                    // total, not the last leg.
                    if (movement.y > 0f) _pendingStepRise += movement.y;

                    // Armed here rather than on the fluid-exit edge, which is a tick too late by
                    // construction: the step-up grounds the body within this tick, so the input layer can
                    // latch a jump on the render frame before that edge is ever detected.
                    if (climbingOutOfFluid && requireJumpReleaseAfterFluid && _jumpHeld)
                    {
                        _jumpBlockedUntilRelease = true;
                        _jumpRequest = false;
                    }
                }
            }

            // 2. Resolve Horizontal (if step-up failed or not attempted)
            if (horizontalBlocked)
            {
                // Reset horizontal AABB to current to sweep axes independently.
                // This prevents cross-axis interference (e.g. hitting an X wall generating a Z push).
                Bounds sweepAABB = currentAABB;
                sweepAABB.SetMinMax(
                    new Vector3(currentAABB.min.x, currentAABB.min.y + collisionPadding, currentAABB.min.z),
                    new Vector3(currentAABB.max.x, currentAABB.max.y - collisionPadding, currentAABB.max.z)
                );

                if (movement.z != 0f)
                {
                    sweepAABB.center += new Vector3(0, 0, movement.z);
                    Probe(sweepAABB, axis: 2, zSign, out var zContact);
                    if (zContact.Hit)
                    {
                        float epsilon = Mathf.Sign(zContact.Correction) * COLLISION_EPSILON;
                        if (Mathf.Abs(zContact.Correction) < COLLISION_JITTER_TOLERANCE) epsilon = 0; // Prevent jitter if already at edge

                        movement.z += zContact.Correction + epsilon;
                        if (Mathf.Abs(movement.z) < 0.0001f) movement.z = 0;
                        sweepAABB.center += new Vector3(0, 0, zContact.Correction + epsilon);
                    }
                }

                if (movement.x != 0f)
                {
                    sweepAABB.center += new Vector3(movement.x, 0, 0);
                    Probe(sweepAABB, axis: 0, xSign, out var xContact);
                    if (xContact.Hit)
                    {
                        float epsilon = Mathf.Sign(xContact.Correction) * COLLISION_EPSILON;
                        if (Mathf.Abs(xContact.Correction) < COLLISION_JITTER_TOLERANCE) epsilon = 0;

                        movement.x += xContact.Correction + epsilon;
                        if (Mathf.Abs(movement.x) < 0.0001f) movement.x = 0;
                        sweepAABB.center += new Vector3(xContact.Correction + epsilon, 0, 0);
                    }
                }
            }

            // 3. Resolve Vertical (Y)
            // Use the FULL AABB (not shrunk vertically) and apply ALL resolved movement
            Bounds verticalFutureAABB = currentAABB;
            verticalFutureAABB.center += movement;
            IsGrounded = groundedByStep;

            if (movement.y != 0f)
            {
                int ySign = movement.y > 0 ? 1 : -1;
                Probe(verticalFutureAABB, axis: 1, ySign, out var yContact);

                if (yContact.Hit)
                {
                    float epsilon = Mathf.Sign(yContact.Correction) * COLLISION_EPSILON;
                    if (Mathf.Abs(yContact.Correction) < COLLISION_JITTER_TOLERANCE) epsilon = 0;

                    movement.y += yContact.Correction + epsilon;
                    if (Mathf.Abs(movement.y) < 0.0001f) movement.y = 0;

                    if (ySign < 0)
                    {
                        IsGrounded = true;
                    }
                    else if (ySign > 0)
                    {
                        _verticalMomentum = 0; // Hit ceiling, kill upward momentum
                    }
                }
            }
            else
            {
                // Explicitly check ground when vertical movement is 0, probing GroundProbeSkin below the feet so a
                // body already resting on a surface registers — flush contact is not overlap, so an un-extended probe
                // would only ever find ground under a body embedded in it.
                Bounds groundProbeAABB = verticalFutureAABB;
                groundProbeAABB.SetMinMax(
                    new Vector3(verticalFutureAABB.min.x, verticalFutureAABB.min.y - GroundProbeSkin,
                        verticalFutureAABB.min.z),
                    verticalFutureAABB.max);

                if (Probe(groundProbeAABB, axis: 1, -1, out _))
                    IsGrounded = true;
            }
        }

        /// <summary>
        /// PH-1: resolves the voxel neighborhood this resolve's sweeps will read, <b>once</b>, instead of letting
        /// each of the nine sweeps rescan it.
        /// </summary>
        /// <param name="currentAABB">The body's AABB before this resolve's movement.</param>
        /// <param name="movement">The intended displacement being resolved.</param>
        private void GatherCells(Bounds currentAABB, Vector3 movement)
        {
            Bounds destination = currentAABB;
            destination.center += movement;

            Bounds envelope = currentAABB;
            envelope.Encapsulate(destination);

            // The body's own box is not enough: the step-up pre-pass reads LIFTED boxes and the ground probe reads
            // BELOW the feet. Upward, stepHeight bounds it — the lifted box must be clear for the step to proceed,
            // so the support its downward sweep then finds sits at or below that lift, and the post-step-up box
            // cannot rise further. The stand-offs are added because the solver parks bodies an epsilon off contact.
            envelope.SetMinMax(
                new Vector3(envelope.min.x, envelope.min.y - GroundProbeSkin, envelope.min.z),
                new Vector3(envelope.max.x, envelope.max.y + EffectiveStepHeight + collisionPadding + COLLISION_EPSILON,
                    envelope.max.z));

            _world.GatherPhysicsCells(envelope, _cellBuffer);
        }

        /// <summary>
        /// Issues one collision sweep, answered from <see cref="_cellBuffer"/> when it covers the sweep and by a
        /// direct world scan when it does not.
        /// <para>
        /// <b>The fallback is a correctness device, not an optimization.</b> A horizontal correction can shift the
        /// cumulative sweep box outside the gathered envelope — most sharply for a body resolving from inside
        /// geometry (<c>PLAYER_BUGS</c> §05, corrections of a block or more). Falling back there is what makes the
        /// gathered path's result identical to the direct scan's for every input, rather than only for the inputs
        /// the envelope happens to bound.
        /// </para>
        /// </summary>
        /// <param name="bounds">The sweep's entity AABB.</param>
        /// <param name="axis">The movement axis to resolve (0=X, 1=Y, 2=Z).</param>
        /// <param name="directionSign">+1 for positive movement, -1 for negative.</param>
        /// <param name="contact">The resolved contact, when the sweep hits.</param>
        /// <returns>True if the AABB overlaps solid collision geometry on that axis.</returns>
        private bool Probe(Bounds bounds, int axis, int directionSign, out CollisionContact contact)
        {
            if (_cellBuffer.TryQuery(bounds, axis, directionSign, out contact, out bool hitAnything))
            {
                PhysicsQueryStats.CountSweep(false);
                return hitAnything;
            }

            PhysicsQueryStats.CountSweep(true);
            return _world.CheckPhysicsCollision(bounds, axis, directionSign, out contact);
        }


        #region Debug Visualizer

        private void OnDrawGizmos()
        {
            if (showBoundingBox)
                DrawBoundingBox(Color.yellow, 0f);
        }

        // In development builds, we use LateUpdate to draw the debug lines continuously if toggled on
#if UNITY_INCLUDE_INSTRUMENTATION
        private void LateUpdate()
        {
            if (showBoundingBox)
                DrawBoundingBox(Color.red, Time.deltaTime);
        }
#endif

        private void DrawBoundingBox(Color color, float duration)
        {
            Vector3 center = transform.position;
            float extX = CollisionHalfWidthX;
            float extZ = CollisionHalfDepthZ;
            float h = collisionHeight;

            // Define the 8 corners of the full AABB
            Vector3 bfl = center + new Vector3(-extX, 0, extZ);
            Vector3 bfr = center + new Vector3(extX, 0, extZ);
            Vector3 bbl = center + new Vector3(-extX, 0, -extZ);
            Vector3 bbr = center + new Vector3(extX, 0, -extZ);

            Vector3 tfl = bfl + new Vector3(0, h, 0);
            Vector3 tfr = bfr + new Vector3(0, h, 0);
            Vector3 tbl = bbl + new Vector3(0, h, 0);
            Vector3 tbr = bbr + new Vector3(0, h, 0);

            // Draw Bottom Face
            Debug.DrawLine(bfl, bfr, color, duration);
            Debug.DrawLine(bfr, bbr, color, duration);
            Debug.DrawLine(bbr, bbl, color, duration);
            Debug.DrawLine(bbl, bfl, color, duration);

            // Draw Top Face
            Debug.DrawLine(tfl, tfr, color, duration);
            Debug.DrawLine(tfr, tbr, color, duration);
            Debug.DrawLine(tbr, tbl, color, duration);
            Debug.DrawLine(tbl, tfl, color, duration);

            // Draw Vertical Pillars
            Debug.DrawLine(bfl, tfl, color, duration);
            Debug.DrawLine(bfr, tfr, color, duration);
            Debug.DrawLine(bbl, tbl, color, duration);
            Debug.DrawLine(bbr, tbr, color, duration);
        }

        #endregion
    }
}
