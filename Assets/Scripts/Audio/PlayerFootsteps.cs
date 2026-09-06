using Data;
using Data.Enums;
using Helpers;
using Physics;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// Plays a footstep one-shot for the block under the player every fixed distance traveled on the
    /// ground, dedicated one-shots for sprinting, jumping off and landing, and — once nothing is holding the
    /// body up — swim strokes and an entry splash for the surrounding fluid (SOUND_ENGINE_DESIGN.md §5.1).
    /// </summary>
    /// <remarks>
    /// Read-only with respect to physics: it polls <see cref="VoxelRigidbody.IsGrounded"/>,
    /// <see cref="VoxelRigidbody.FluidContact"/> and the transform rather than having the solver raise
    /// events, so the physics hot path and its validation suite stay untouched by an audio feature.
    /// </remarks>
    [RequireComponent(typeof(VoxelRigidbody))]
    public class PlayerFootsteps : MonoBehaviour
    {
        [Tooltip("Horizontal distance in blocks between footsteps while walking.")]
        [Range(0.5f, 4f)]
        [SerializeField]
        private float _strideLength = 1.5f;

        [Tooltip("Distance in blocks between swim strokes. Measured in 3D, unlike the walking stride, so " +
                 "climbing a waterfall still strokes.")]
        [Range(0.5f, 4f)]
        [SerializeField]
        private float _strokeLength = 1.2f;

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
        /// Whether the body was touching a fluid last frame, so entering one can be heard exactly once.
        /// </summary>
        /// <remarks>
        /// Seeded alongside <see cref="_wasGrounded"/> rather than in <c>Awake</c>: a player who spawns
        /// already in water would otherwise splash for an entry that never happened.
        /// </remarks>
        private bool _wasInFluid;

        /// <summary>
        /// The <see cref="VoxelRigidbody.JumpCount"/> already sounded, so a jump is heard exactly once.
        /// </summary>
        /// <remarks>
        /// The counter is what separates a jump from walking off a ledge: both leave the ground, and only
        /// the solver knows which one happened.
        /// </remarks>
        private uint _lastJumpCount;

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
            _lastJumpCount = _body.JumpCount;
        }

        private void Update()
        {
            if (_body == null) return;

            _world ??= World.Instance;
            if (_world == null || SoundManager.Instance == null) return;

            bool grounded = _body.IsGrounded;
            bool flying = _body.isFlying;
            bool inFluid = _body.FluidContact.InFluid;

            if (!_groundedSeeded)
            {
                _groundedSeeded = true;
                _wasGrounded = grounded;
                _wasInFluid = inFluid;
                _lastStepPosition = transform.position;
                _lastJumpCount = _body.JumpCount;
                return;
            }

            // Polled before the grounded branches: the take-off leaves the ground in the same fixed step,
            // so the jump would otherwise be indistinguishable from stepping off a ledge.
            TryPlayJumpStart();

            // A flown body is not entering anything, even though the solver still reports its contact.
            TryPlaySplash(inFluid && !flying);

            if (SoundResolution.IsSwimming(grounded, flying, inFluid))
            {
                // Cleared here too, so sinking onto the bottom still lands rather than resuming mid-stride.
                _wasGrounded = false;

                // 3D, unlike the walking stride: a swimmer climbing a water column covers no horizontal
                // distance, and would stroke only once for the whole climb.
                if ((transform.position - _lastStepPosition).sqrMagnitude < _strokeLength * _strokeLength)
                    return;

                _lastStepPosition = transform.position;
                PlayStroke(SoundResolution.SelectStrideEvent(swimming: true, _body.isSprinting));
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
                PlayFootfall(BlockSoundEvent.JumpLand);
                return;
            }

            Vector3 delta = transform.position - _lastStepPosition;
            delta.y = 0f;
            if (delta.sqrMagnitude < _strideLength * _strideLength) return;

            _lastStepPosition = transform.position;

            // The stride is deliberately not shortened while sprinting: it is a distance, so a faster body
            // already crosses it more often, which is what a running cadence is.
            PlayFootfall(SoundResolution.SelectStrideEvent(swimming: false, _body.isSprinting));
        }

        /// <summary>
        /// Sounds a splash on the frame the body first touches a fluid.
        /// </summary>
        /// <param name="inFluid">Whether the body counts as touching a fluid this frame.</param>
        /// <remarks>
        /// Entry only. Leaving a fluid is deliberately silent: a body climbing out is already sounding the
        /// footfall of whatever it climbed onto, and a second one-shot over it reads as a double hit.
        /// </remarks>
        private void TryPlaySplash(bool inFluid)
        {
            if (inFluid == _wasInFluid) return;

            _wasInFluid = inFluid;
            if (!inFluid) return;

            // Entry re-bases the stroke accumulator, so the splash is not chased by a stroke fired from
            // distance banked on the way in.
            _lastStepPosition = transform.position;
            PlayStroke(BlockSoundEvent.Splash);
        }

        /// <summary>
        /// Plays one in-fluid one-shot: the fluid the solver put the body in, and nothing else.
        /// </summary>
        /// <param name="evt">Which in-fluid one-shot this is — a stroke or an entry splash.</param>
        /// <remarks>
        /// Deliberately <i>not</i> the two-cell footfall <see cref="PlayFootfall"/> resolves: a swimmer a
        /// block above a seabed is touching nothing, so layering the cell below would sound sand under them.
        /// The fluid comes from <see cref="FluidContact.BlockId"/>, so the ear names the same waterline the
        /// solver is pushing the body with.
        /// </remarks>
        private void PlayStroke(BlockSoundEvent evt)
        {
            SoundMaterial material = SoundResolution.ResolveMaterial(_world.BlockTypes, _body.FluidContact.BlockId);

            // Body center, not the feet: a stroke is the whole body moving through the fluid, and a one-shot
            // at the feet reads as coming from below a listener whose ears are at eye height.
            Vector3 strokePos = transform.position + new Vector3(0f, _body.collisionHeight * 0.5f, 0f);
            SoundManager.Instance.PlayBlockSound(material, evt, strokePos);
        }

        /// <summary>
        /// Sounds a take-off when the solver reports a jump this frame has not been played yet.
        /// </summary>
        private void TryPlayJumpStart()
        {
            uint jumps = _body.JumpCount;
            if (jumps == _lastJumpCount) return;

            _lastJumpCount = jumps;
            PlayFootfall(BlockSoundEvent.JumpStart);
        }

        /// <summary>
        /// Plays one footfall: the supporting block always, plus a layered one-shot for a non-solid block
        /// occupying the player's own cell.
        /// </summary>
        /// <param name="evt">Which footfall this is — a stride step, a sprint step, a take-off or a landing.
        /// Unauthored gait and jump clips fall back to the material's plain step clips.</param>
        private void PlayFootfall(BlockSoundEvent evt)
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
            SoundManager.Instance.PlayBlockSound(supportMaterial, evt, feetPos);
            SoundManager.Instance.PlayBlockSound(occupantMaterial, evt, feetPos, _occupantLayerVolume);
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
