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
        /// Resolves the block under the player's feet and plays its step sound.
        /// </summary>
        /// <param name="emphasis">Volume multiplier — above 1 for the landing step.</param>
        private void PlayStep(float emphasis)
        {
            Vector3 unityPos = transform.position;

            // The cell below the feet: floor() rather than round(), and one below the standing surface.
            Vector3Int unityCell = new Vector3Int(
                Mathf.FloorToInt(unityPos.x),
                Mathf.FloorToInt(unityPos.y) - 1,
                Mathf.FloorToInt(unityPos.z));

            Vector3Int voxelCell = unityCell + WorldOrigin.OriginVoxel;
            if (!_world.TryGetVoxel(voxelCell.x, voxelCell.y, voxelCell.z, out VoxelState state)) return;

            SoundMaterial material = SoundResolution.ResolveMaterial(_world.BlockTypes, state.ID);
            if (material == SoundMaterial.None) return;

            // Played at the foot position, not the block center: the listener is on the camera, and a step
            // should read as being underneath the player rather than a block away.
            Vector3 feetPos = new Vector3(unityPos.x, unityCell.y + 1f, unityPos.z);
            SoundManager.Instance.PlayBlockSound(material, BlockSoundEvent.Step, feetPos, emphasis);
        }
    }
}
