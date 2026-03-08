using UnityEngine;

namespace Physics
{
    public class VoxelRigidbody : MonoBehaviour
    {
        [Header("Physics Settings")]
        [Tooltip("Gravity applied per second when not flying.")]
        public float gravity = -13f;

        [Tooltip("The radius of the entity (width / 2).")]
        public float entityWidth = 0.4f;

        [Tooltip("The total height of the entity.")]
        public float entityHeight = 1.8f;

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

        private float _verticalMomentum = 0f;
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
                float w = entityWidth;

                // Resolve Z Axis
                if (Velocity.z > 0 && CheckHorizontalCollision(0, w + Velocity.z))
                    Velocity = new Vector3(Velocity.x, Velocity.y, Mathf.Floor(transform.position.z + w + Velocity.z) - (transform.position.z + w) - 0.001f);
                else if (Velocity.z < 0 && CheckHorizontalCollision(0, -w + Velocity.z))
                    Velocity = new Vector3(Velocity.x, Velocity.y, Mathf.Floor(transform.position.z - w + Velocity.z) + 1f - (transform.position.z - w) + 0.001f);

                // Resolve X Axis
                if (Velocity.x > 0 && CheckHorizontalCollision(w + Velocity.x, 0))
                    Velocity = new Vector3(Mathf.Floor(transform.position.x + w + Velocity.x) - (transform.position.x + w) - 0.001f, Velocity.y, Velocity.z);
                else if (Velocity.x < 0 && CheckHorizontalCollision(-w + Velocity.x, 0))
                    Velocity = new Vector3(Mathf.Floor(transform.position.x - w + Velocity.x) + 1f - (transform.position.x - w) + 0.001f, Velocity.y, Velocity.z);

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
            float w = entityWidth - 0.05f;

            // Check 4 corners of the bottom face
            if (_world.CheckForCollision(new Vector3(pos.x - w, y, pos.z - w)) ||
                _world.CheckForCollision(new Vector3(pos.x + w, y, pos.z - w)) ||
                _world.CheckForCollision(new Vector3(pos.x + w, y, pos.z + w)) ||
                _world.CheckForCollision(new Vector3(pos.x - w, y, pos.z + w)))
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
            float y = pos.y + entityHeight + upSpeed;

            // Skin width to ensure vertical checks don't clip into adjacent side walls
            float w = entityWidth - 0.05f;

            // Check 4 corners of the top face
            if (_world.CheckForCollision(new Vector3(pos.x - w, y, pos.z - w)) ||
                _world.CheckForCollision(new Vector3(pos.x + w, y, pos.z - w)) ||
                _world.CheckForCollision(new Vector3(pos.x + w, y, pos.z + w)) ||
                _world.CheckForCollision(new Vector3(pos.x - w, y, pos.z + w)))
            {
                _verticalMomentum = 0; // set to 0 so the entity falls when its head hits a block while jumping
                return Mathf.Floor(y) - (pos.y + entityHeight) - 0.001f;
            }

            return upSpeed;
        }

        /// <summary>
        /// Sweeps an Axis-Aligned bounding box face dynamically up to the entity's height to check for collisions.
        /// Incorporates a skin width algorithm to prevent sticking and allows diagonal sliding.
        /// </summary>
        private bool CheckHorizontalCollision(float dx, float dz)
        {
            Vector3 center = transform.position;
            Vector3 offset = new Vector3(dx, 0, dz);

            // Skin width for horizontal sweeping to allow sliding off walls
            float skinWidth = 0.05f;
            float insetWidth = entityWidth - skinWidth;

            // Perpendicular vector to find the two corners of the AABB face
            Vector3 perp = new Vector3(Mathf.Abs(dz) > 0 ? insetWidth : 0, 0, Mathf.Abs(dx) > 0 ? insetWidth : 0);

            Vector3 corner1 = offset - perp;
            Vector3 corner2 = offset + perp;

            // Iterate bottom to top at 1 block intervals
            // Use a y-offset skin width so horizontal checks don't catch the floor
            float startY = 0.05f;
            float step = 1.0f;
            for (float y = startY; y < entityHeight; y += step)
            {
                if (_world.CheckForCollision(center + corner1 + new Vector3(0, y, 0))) return true;
                if (_world.CheckForCollision(center + corner2 + new Vector3(0, y, 0))) return true;
            }

            // Explicit top check (also inset horizontally/vertically)
            float topY = entityHeight - skinWidth;
            if (topY > startY)
            {
                if (_world.CheckForCollision(center + corner1 + new Vector3(0, topY, 0))) return true;
                if (_world.CheckForCollision(center + corner2 + new Vector3(0, topY, 0))) return true;
            }

            return false;
        }

        #endregion

        /// <summary>
        /// Provides access to directly modify vertical momentum (used for loading state).
        /// </summary>
        public void ApplyDirectVerticalMomentum(float momentum)
        {
            _verticalMomentum = momentum;
        }
    }
}
