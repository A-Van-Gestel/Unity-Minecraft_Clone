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

        // How far below the feet the grounded check looks. The overlap test in World.CheckPhysicsCollision is strict,
        // so a body resting on a surface — which the vertical resolve parks COLLISION_EPSILON above it — does NOT
        // overlap that surface. Probing the un-extended AABB therefore only ever detects an *embedded* body, and a
        // correctly-standing one reads as airborne. Must stay above COLLISION_EPSILON (or it cannot span the
        // stand-off) and far below the thinnest collision volume (0.25) so it never grounds a genuinely falling body.
        private const float GROUND_PROBE_SKIN = COLLISION_EPSILON * 2f;

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

        private float _verticalMomentum;
        private Vector3 _movementIntent;
        private float _verticalFlyingIntent;
        private bool _jumpRequest;
        private float _lastMoveSpeed;

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
        public void RequestJump()
        {
            if (IsGrounded && !isFlying)
            {
                _jumpRequest = true;
            }
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

            if (_jumpRequest && !isFlying)
            {
                _verticalMomentum = jumpForce;
                IsGrounded = false;
                _jumpRequest = false;
            }

            transform.Translate(Velocity, Space.World);

            ClampToWorldBorder();
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
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (clampedX != pos.x || clampedZ != pos.z)
                transform.position = new Vector3(clampedX, pos.y, clampedZ);
        }

        private void CalculateVelocity()
        {
            // VERTICAL VELOCITY & GRAVITY
            if (!isFlying)
            {
                // Only start accelerating downwards when falling off a block.
                if (IsGrounded && _verticalMomentum < 0)
                    _verticalMomentum = 0f;

                // Affect vertical momentum with gravity.
                if (_verticalMomentum > gravity)
                    _verticalMomentum += Time.fixedDeltaTime * gravity;
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

            Velocity = _movementIntent * (Time.fixedDeltaTime * MoveSpeed);

            // Apply vertical momentum (falling / jumping)
            Velocity += Vector3.up * (_verticalMomentum * Time.fixedDeltaTime);

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

                    for (int i = 0; i < substeps; i++)
                    {
                        // Use the corrected subMove from the previous step as a baseline,
                        // but re-evaluate against current world position.
                        Vector3 currentSubMove = subMove;
                        ResolveMovement(ref currentSubMove);
                        transform.position += currentSubMove; // Move temporarily to test next substeps accurately
                        totalDisplacement += currentSubMove;

                        // Carry over velocity blocks (if an axis stopped, it stays stopped)
                        if (currentSubMove.x == 0) subMove.x = 0;
                        if (currentSubMove.y == 0) subMove.y = 0;
                        if (currentSubMove.z == 0) subMove.z = 0;
                    }

                    // Revert the temporary position changes because `VoxelRigidbody`
                    // expects `transform.Translate(Velocity)` to be called externally later.
                    transform.position -= totalDisplacement;
                    Velocity = totalDisplacement;
                }
                else
                {
                    Vector3 tempVelocity = Velocity;
                    ResolveMovement(ref tempVelocity);
                    Velocity = tempVelocity;
                }
            }
        }

        private void ResolveMovement(ref Vector3 movement)
        {
            Vector3 pos = transform.position;
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

            // If blocked and grounded, attempt step-up with ORIGINAL movement
            if (horizontalBlocked && IsGrounded && !isFlying)
            {
                Bounds liftedAABB = horizontalFutureAABB;
                liftedAABB.center += Vector3.up * stepHeight;

                bool clearsAtStep = true;
                if (movement.x != 0f)
                    clearsAtStep &= !Probe(liftedAABB, axis: 0, xSign, out _);
                if (movement.z != 0f)
                    clearsAtStep &= !Probe(liftedAABB, axis: 2, zSign, out _);

                if (clearsAtStep)
                {
                    // Sweep DOWNWARD to find highest support surface
                    Bounds sweepAABB = liftedAABB;
                    sweepAABB.Expand(new Vector3(0, stepHeight, 0));
                    sweepAABB.center -= new Vector3(0, stepHeight * 0.5f, 0);

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
                        movement.y = stepHeight;
                    }

                    // SUCCESS: horizontal velocity is preserved as-is (no correction applied).
                    horizontalBlocked = false;
                    horizontalFutureAABB.center += Vector3.up * movement.y;
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
                // Explicitly check ground when vertical movement is 0, probing GROUND_PROBE_SKIN below the feet so a
                // body already resting on a surface registers — flush contact is not overlap, so an un-extended probe
                // would only ever find ground under a body embedded in it.
                Bounds groundProbeAABB = verticalFutureAABB;
                groundProbeAABB.SetMinMax(
                    new Vector3(verticalFutureAABB.min.x, verticalFutureAABB.min.y - GROUND_PROBE_SKIN,
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
                new Vector3(envelope.min.x, envelope.min.y - GROUND_PROBE_SKIN, envelope.min.z),
                new Vector3(envelope.max.x, envelope.max.y + stepHeight + collisionPadding + COLLISION_EPSILON,
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
#if DEVELOPMENT_BUILD || UNITY_EDITOR
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
