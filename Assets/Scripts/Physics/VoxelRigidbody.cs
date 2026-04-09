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

        [Tooltip("The total width of the physics collider (X axis).")]
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

        public float CollisionHalfWidthX => collisionWidthX * 0.5f;
        public float CollisionHalfDepthZ => collisionDepthZ * 0.5f;

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

        public bool IsGrounded { get; private set; }
        public Vector3 Velocity { get; private set; }
        public float MoveSpeed { get; private set; }

        private float _verticalMomentum;
        private Vector3 _movementIntent;
        private float _verticalFlyingIntent;
        private bool _jumpRequest;
        private float _lastMoveSpeed;

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
            // Wait for world to finish initial load and meshing to prevent falling through terrain
            if (!_world.IsWorldLoaded) return;

            CalculateVelocity();

            if (_jumpRequest && !isFlying)
            {
                _verticalMomentum = jumpForce;
                IsGrounded = false;
                _jumpRequest = false;
            }

            transform.Translate(Velocity, Space.World);
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

            // COLLISION (Continuous predict-check snapping)
            if (!isNoclipping)
            {
                float extX = CollisionHalfWidthX;
                float extZ = CollisionHalfDepthZ;

                // Resolve Z Axis
                if (Velocity.z > 0 && CheckHorizontalCollision(0, extZ + Velocity.z))
                    Velocity = new Vector3(Velocity.x, Velocity.y, Mathf.Floor(transform.position.z + extZ + Velocity.z) - (transform.position.z + extZ) - COLLISION_EPSILON);
                else if (Velocity.z < 0 && CheckHorizontalCollision(0, -extZ + Velocity.z))
                    Velocity = new Vector3(Velocity.x, Velocity.y, Mathf.Floor(transform.position.z - extZ + Velocity.z) + 1f - (transform.position.z - extZ) + COLLISION_EPSILON);

                // Resolve X Axis
                if (Velocity.x > 0 && CheckHorizontalCollision(extX + Velocity.x, 0))
                    Velocity = new Vector3(Mathf.Floor(transform.position.x + extX + Velocity.x) - (transform.position.x + extX) - COLLISION_EPSILON, Velocity.y, Velocity.z);
                else if (Velocity.x < 0 && CheckHorizontalCollision(-extX + Velocity.x, 0))
                    Velocity = new Vector3(Mathf.Floor(transform.position.x - extX + Velocity.x) + 1f - (transform.position.x - extX) + COLLISION_EPSILON, Velocity.y, Velocity.z);

                // Resolve Y Axis
                if (Velocity.y < 0)
                    Velocity = new Vector3(Velocity.x, CheckDownSpeed(Velocity.y), Velocity.z);
                else if (Velocity.y > 0)
                    Velocity = new Vector3(Velocity.x, CheckUpSpeed(Velocity.y), Velocity.z);
                else
                    CheckDownSpeed(0); // Maintain IsGrounded explicitly when falling velocity is 0
            }
        }

        #region Collision Checks

        private float CheckDownSpeed(float downSpeed)
        {
            Vector3 pos = transform.position;
            float y = pos.y + downSpeed;

            // Skin width to ensure vertical checks don't clip into adjacent side walls
            float wx = CollisionHalfWidthX - collisionPadding;
            float wz = CollisionHalfDepthZ - collisionPadding;

            // Check 4 corners of the bottom face
            if (_world.CheckForCollision(new Vector3(pos.x - wx, y, pos.z - wz)) ||
                _world.CheckForCollision(new Vector3(pos.x + wx, y, pos.z - wz)) ||
                _world.CheckForCollision(new Vector3(pos.x + wx, y, pos.z + wz)) ||
                _world.CheckForCollision(new Vector3(pos.x - wx, y, pos.z + wz)))
            {
                IsGrounded = true;
                // Snap exactly to the top surface of the voxel. Not using epsilon because gravity pushes exactly onto it.
                return Mathf.Floor(y) + 1f - pos.y;
            }

            IsGrounded = false;
            return downSpeed;
        }

        private float CheckUpSpeed(float upSpeed)
        {
            Vector3 pos = transform.position;
            float y = pos.y + collisionHeight + upSpeed;

            // Skin width to ensure vertical checks don't clip into adjacent side walls
            float wx = CollisionHalfWidthX - collisionPadding;
            float wz = CollisionHalfDepthZ - collisionPadding;

            // Check 4 corners of the top face
            if (_world.CheckForCollision(new Vector3(pos.x - wx, y, pos.z - wz)) ||
                _world.CheckForCollision(new Vector3(pos.x + wx, y, pos.z - wz)) ||
                _world.CheckForCollision(new Vector3(pos.x + wx, y, pos.z + wz)) ||
                _world.CheckForCollision(new Vector3(pos.x - wx, y, pos.z + wz)))
            {
                _verticalMomentum = 0; // set to 0 so the entity falls when its head hits a block while jumping
                return Mathf.Floor(y) - (pos.y + collisionHeight) - COLLISION_EPSILON;
            }

            return upSpeed;
        }

        /// <summary>
        /// Sweeps an Axis-Aligned face dynamically up to the entity's height to check for collisions.
        /// Incorporates a skin width padding algorithm to allow sliding parallel to blocks without snagging.
        /// </summary>
        private bool CheckHorizontalCollision(float dx, float dz)
        {
            Vector3 center = transform.position;
            Vector3 offset = new Vector3(dx, 0, dz);

            // Inset dimensions based on padding
            float insetX = CollisionHalfWidthX - collisionPadding;
            float insetZ = CollisionHalfDepthZ - collisionPadding;

            // Perpendicular vector for the sweeping face
            Vector3 perp;
            if (Mathf.Abs(dz) > 0) // Moving along Z, sweep an X face
                perp = new Vector3(insetX, 0, 0);
            else // Moving along X, sweep a Z face
                perp = new Vector3(0, 0, insetZ);

            Vector3 corner1 = offset - perp;
            Vector3 corner2 = offset + perp;

            // Iterate bottom to top at 1 block intervals
            // Use a y-offset skin width so horizontal checks don't catch the floor
            float startY = collisionPadding;
            const float step = 1.0f;
            for (float y = startY; y < collisionHeight; y += step)
            {
                if (_world.CheckForCollision(center + corner1 + new Vector3(0, y, 0))) return true;
                if (_world.CheckForCollision(center + corner2 + new Vector3(0, y, 0))) return true;
            }

            // Explicit top check (also inset horizontally/vertically)
            float topY = collisionHeight - collisionPadding;
            if (topY > startY)
            {
                if (_world.CheckForCollision(center + corner1 + new Vector3(0, topY, 0))) return true;
                if (_world.CheckForCollision(center + corner2 + new Vector3(0, topY, 0))) return true;
            }

            return false;
        }

        #endregion

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
