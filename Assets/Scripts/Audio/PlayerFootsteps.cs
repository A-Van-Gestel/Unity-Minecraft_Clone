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

        [Tooltip("Volume multiplier for the step played on landing, relative to a walking step.")]
        [Range(1f, 3f)]
        [SerializeField]
        private float _landingEmphasis = 1.4f;

        [Tooltip("Volume of the layered step from a non-solid block occupying the player's own cell " +
                 "(water, flora), relative to the step from the block underneath.")]
        [Range(0f, 1f)]
        [SerializeField]
        private float _occupantLayerVolume = 0.9f;

        private VoxelRigidbody _body;
        private World _world;
        private Vector3 _lastStepPosition;
        private bool _wasGrounded;

        private void Awake()
        {
            _body = GetComponent<VoxelRigidbody>();
            _lastStepPosition = transform.position;
            _wasGrounded = _body != null && _body.IsGrounded;
        }

        private void Update()
        {
            if (_body == null) return;

            _world ??= World.Instance;
            if (_world == null || SoundManager.Instance == null) return;

            bool grounded = _body.IsGrounded;

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
                PlayStep(_landingEmphasis);
                return;
            }

            Vector3 delta = transform.position - _lastStepPosition;
            delta.y = 0f;
            if (delta.sqrMagnitude < _strideLength * _strideLength) return;

            _lastStepPosition = transform.position;
            PlayStep(1f);
        }

        /// <summary>
        /// Plays a step: the supporting block always, plus a layered one-shot for a non-solid block
        /// occupying the player's own cell.
        /// </summary>
        /// <param name="emphasis">Volume multiplier — above 1 for the landing step.</param>
        private void PlayStep(float emphasis)
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
            ushort occupantId = TryGetBlockId(voxelX, occupantUnityY + origin.y, voxelZ);
            ushort supportId = TryGetBlockId(voxelX, supportUnityY + origin.y, voxelZ);

            SoundResolution.ResolveStepMaterials(_world.BlockTypes, occupantId, supportId,
                out SoundMaterial supportMaterial, out SoundMaterial occupantMaterial);

            // Played at the foot position, not the block center: the listener is on the camera, and a step
            // should read as being underneath the player rather than a block away.
            Vector3 feetPos = new Vector3(unityPos.x, occupantUnityY, unityPos.z);

            // Both calls are unconditional: a None material is already silent, and each takes its own voice
            // and event salt, so the two layers get independent clips and pitch rather than flanging.
            SoundManager.Instance.PlayBlockSound(supportMaterial, BlockSoundEvent.Step, feetPos, emphasis);
            SoundManager.Instance.PlayBlockSound(occupantMaterial, BlockSoundEvent.Step, feetPos,
                emphasis * _occupantLayerVolume);
        }

        /// <summary>
        /// Reads one voxel's block ID, treating an unloaded or out-of-world cell as air.
        /// </summary>
        /// <param name="voxelX">Voxel-world X.</param>
        /// <param name="voxelY">Voxel-world Y.</param>
        /// <param name="voxelZ">Voxel-world Z.</param>
        /// <returns>The block ID, or <see cref="BlockIDs.Air"/> when the cell cannot be read.</returns>
        private ushort TryGetBlockId(int voxelX, int voxelY, int voxelZ)
        {
            return _world.TryGetVoxel(voxelX, voxelY, voxelZ, out VoxelState state) ? state.ID : BlockIDs.Air;
        }
    }
}
