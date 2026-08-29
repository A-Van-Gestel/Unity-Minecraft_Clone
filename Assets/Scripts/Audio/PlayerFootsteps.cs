using Data;
using Data.Enums;
using Helpers;
using Physics;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// Plays a footstep one-shot for the block under the player every fixed distance traveled on the
    /// ground, plus an immediate louder step on landing (SOUND_ENGINE_DESIGN.md §5.1).
    /// </summary>
    /// <remarks>
    /// Read-only with respect to physics: it polls <see cref="VoxelRigidbody.IsGrounded"/> and the
    /// transform rather than having the solver raise events, so the physics hot path and its validation
    /// suite stay untouched by an audio feature.
    /// </remarks>
    [RequireComponent(typeof(VoxelRigidbody))]
    public class PlayerFootsteps : MonoBehaviour
    {
        [Tooltip("Horizontal distance in blocks between footsteps while walking.")]
        [Range(0.5f, 4f)]
        [SerializeField]
        private float _strideLength = 1.5f;

        [Tooltip("Volume of the layered step from a non-solid block occupying the player's own cell " +
                 "(water, flora), relative to the step from the block underneath.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _occupantLayerVolume = 0.9f;

        private VoxelRigidbody _body;
        private World _world;
        private Vector3 _lastStepPosition;
        private bool _wasGrounded;

        /// <summary>
        /// Whether <see cref="_wasGrounded"/> has been seeded from a live physics state yet.
        /// </summary>
        /// <remarks>
        /// Seeding in <c>Awake</c> is too early: <see cref="VoxelRigidbody.IsGrounded"/> is false before the
        /// first solve, so a player already standing on the ground when the world finishes loading would take
        /// the landing branch and thud once for a fall that never happened.
        /// </remarks>
        private bool _groundedSeeded;

        private void Awake()
        {
            _body = GetComponent<VoxelRigidbody>();
            _lastStepPosition = transform.position;
        }

        private void Update()
        {
            if (_body == null) return;

            _world ??= World.Instance;
            if (_world == null || SoundManager.Instance == null) return;

            bool grounded = _body.IsGrounded;

            if (!_groundedSeeded)
            {
                _groundedSeeded = true;
                _wasGrounded = grounded;
                _lastStepPosition = transform.position;
                return;
            }

            if (!grounded)
            {
                // Airborne travel must not bank distance, or a long fall lands and immediately fires a
                // second step from the accumulated horizontal drift.
                _lastStepPosition = transform.position;
                _wasGrounded = false;
                return;
            }

            if (!_wasGrounded)
            {
                _wasGrounded = true;
                _lastStepPosition = transform.position;
                PlayStep();
                return;
            }

            Vector3 delta = transform.position - _lastStepPosition;
            delta.y = 0f;
            if (delta.sqrMagnitude < _strideLength * _strideLength) return;

            _lastStepPosition = transform.position;
            PlayStep();
        }

        /// <summary>
        /// Plays a step: the supporting block always, plus a layered one-shot for a non-solid block
        /// occupying the player's own cell.
        /// </summary>
        private void PlayStep()
        {
            Vector3 unityPos = transform.position;
            SoundResolution.StepCells(unityPos.y, out int occupantUnityY, out int supportUnityY);

            int unityX = Mathf.FloorToInt(unityPos.x);
            int unityZ = Mathf.FloorToInt(unityPos.z);
            Vector3Int origin = WorldOrigin.OriginVoxel;
            int voxelX = unityX + origin.x;
            int voxelZ = unityZ + origin.z;

            // A cell outside the loaded world resolves to Air rather than aborting the step: an unloaded
            // occupant must not silence a perfectly known supporting block below it.
            TryGetBlock(voxelX, occupantUnityY + origin.y, voxelZ, out ushort occupantId, out byte occupantMeta);
            TryGetBlock(voxelX, supportUnityY + origin.y, voxelZ, out ushort supportId, out _);

            // Unity-space Y throughout: the collision bounds this resolves against are authored and evaluated
            // in render space, and the feet position below is too.
            SoundResolution.ResolveStep(_world.BlockTypes, occupantId, occupantMeta, supportId,
                occupantUnityY, unityPos.y,
                out SoundMaterial supportMaterial, out SoundMaterial occupantMaterial);

            // Played at the foot position, not the block center: the listener is on the camera, and a step
            // should read as being underneath the player rather than a block away.
            Vector3 feetPos = new Vector3(unityPos.x, occupantUnityY, unityPos.z);

            // Both calls are unconditional: a None material is already silent, and each takes its own voice
            // and event salt, so the two layers get independent clips and pitch rather than flanging.
            SoundManager.Instance.PlayBlockSound(supportMaterial, BlockSoundEvent.Step, feetPos);
            SoundManager.Instance.PlayBlockSound(occupantMaterial, BlockSoundEvent.Step, feetPos,
                _occupantLayerVolume);
        }

        /// <summary>
        /// Reads one voxel's block ID and metadata, treating an unloaded or out-of-world cell as air.
        /// </summary>
        /// <remarks>
        /// The metadata is what lets a rotated sub-voxel shape resolve its real collision volume, so it is read
        /// alongside the ID rather than defaulted.
        /// </remarks>
        /// <param name="voxelX">Voxel-world X.</param>
        /// <param name="voxelY">Voxel-world Y.</param>
        /// <param name="voxelZ">Voxel-world Z.</param>
        /// <param name="blockId">The block ID, or <see cref="BlockIDs.Air"/> when the cell cannot be read.</param>
        /// <param name="meta">The voxel's raw metadata byte, or 0 when the cell cannot be read.</param>
        private void TryGetBlock(int voxelX, int voxelY, int voxelZ, out ushort blockId, out byte meta)
        {
            if (_world.TryGetVoxel(voxelX, voxelY, voxelZ, out VoxelState state))
            {
                blockId = state.ID;
                meta = state.Meta;
                return;
            }

            blockId = BlockIDs.Air;
            meta = 0;
        }
    }
}
