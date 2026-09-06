using Data.Enums;
using UnityEngine;

namespace Audio
{
    /// <summary>
    /// What a body looks like to the footstep layer on one frame — everything
    /// <see cref="FootfallTracker"/> reads, and nothing else.
    /// </summary>
    public struct FootfallSample
    {
        /// <summary>The body's Unity-space position; its feet, not its center.</summary>
        public Vector3 Position;

        /// <summary>Whether the solver reports the body standing on something.</summary>
        public bool Grounded;

        /// <summary>Whether the body is in flight mode.</summary>
        public bool Flying;

        /// <summary>Whether the solver reports any fluid contact.</summary>
        public bool InFluid;

        /// <summary>Whether the body is sprinting.</summary>
        public bool Sprinting;

        /// <summary>The solver's monotonic jump counter.</summary>
        public uint JumpCount;
    }

    /// <summary>
    /// Which one-shots one frame earned. Several can fire together: a jump out of water leaves the
    /// ground and crosses the waterline on the same frame.
    /// </summary>
    public struct FootfallOutcome
    {
        /// <summary>A take-off was sounded.</summary>
        public bool JumpStart;

        /// <summary>The body entered a fluid.</summary>
        public bool Splash;

        /// <summary>Whether <see cref="Footfall"/> holds an event to play through the two-cell resolution.</summary>
        public bool HasFootfall;

        /// <summary>The ground-side one-shot: a stride, a sprint or a landing.</summary>
        public BlockSoundEvent Footfall;

        /// <summary>Whether <see cref="Stroke"/> holds an event to play against the fluid alone.</summary>
        public bool HasStroke;

        /// <summary>The in-fluid one-shot.</summary>
        public BlockSoundEvent Stroke;
    }

    /// <summary>
    /// The footstep layer's state machine: distance accumulation and the ground / fluid / swim edges that
    /// decide which one-shots a frame earns.
    /// <para>
    /// Pure and Unity-free apart from <see cref="Vector3"/>, so every transition can be driven as a frame
    /// sequence by the validation suite. The edges are the fragile half — a latch that stores the wrong
    /// value produces a sound for something that never happened, which no amount of clip authoring reveals.
    /// </para>
    /// </summary>
    public class FootfallTracker
    {
        private Vector3 _lastStepPosition;
        private bool _wasGrounded;
        private bool _wasInFluid;
        private bool _wasSwimming;
        private uint _lastJumpCount;

        /// <summary>
        /// Whether the tracker has seen a live physics state yet.
        /// </summary>
        /// <remarks>
        /// Seeding at construction is too early: the solver reports neither ground nor fluid before its
        /// first solve, so a body already standing — or already floating — would sound a landing or an
        /// entry for something that never happened.
        /// </remarks>
        private bool _seeded;

        /// <summary>Re-seeds on the next <see cref="Advance"/>, discarding every latched edge.</summary>
        public void Reset() => _seeded = false;

        /// <summary>
        /// Advances one frame and returns the one-shots it earned.
        /// </summary>
        /// <param name="sample">This frame's body state.</param>
        /// <param name="strideLength">Horizontal distance between footfalls, in blocks.</param>
        /// <param name="strokeLength">3D distance between swim strokes, in blocks.</param>
        /// <returns>The events to play, in the order the fields are declared.</returns>
        public FootfallOutcome Advance(in FootfallSample sample, float strideLength, float strokeLength)
        {
            FootfallOutcome outcome = default;

            bool swimming = SoundResolution.IsSwimming(sample.Grounded, sample.Flying, sample.InFluid);

            if (!_seeded)
            {
                _seeded = true;
                _wasGrounded = sample.Grounded;
                _wasInFluid = sample.InFluid;
                _wasSwimming = swimming;
                _lastStepPosition = sample.Position;
                _lastJumpCount = sample.JumpCount;
                return outcome;
            }

            // Polled before the ground branches: the take-off leaves the ground in the same fixed step, so
            // the jump would otherwise be indistinguishable from stepping off a ledge.
            if (sample.JumpCount != _lastJumpCount)
            {
                _lastJumpCount = sample.JumpCount;
                outcome.JumpStart = true;
            }

            // The latch follows the raw contact, never the flight-gated one: gating the stored value makes
            // a flier's exit from flight read as an entry into water it was already in.
            bool entered = sample.InFluid && !_wasInFluid;
            _wasInFluid = sample.InFluid;
            if (entered && !sample.Flying)
            {
                // Entry re-bases the accumulator, so the splash is not chased by a stroke fired from
                // distance banked on the way in.
                _lastStepPosition = sample.Position;
                outcome.Splash = true;
            }

            if (swimming)
            {
                // Cleared here too, so sinking onto the bottom still lands rather than resuming mid-stride.
                _wasGrounded = false;

                // Losing footing re-bases for the same reason entry does: a wader carries up to a full
                // stride of banked distance, which is longer than a stroke and would fire one instantly.
                if (!_wasSwimming) _lastStepPosition = sample.Position;
                _wasSwimming = true;

                // 3D, unlike the walking stride: a swimmer climbing a water column covers no horizontal
                // distance, and would stroke only once for the whole climb.
                if ((sample.Position - _lastStepPosition).sqrMagnitude >= strokeLength * strokeLength)
                {
                    _lastStepPosition = sample.Position;
                    outcome.Stroke = SoundResolution.SelectStrideEvent(swimming: true, sample.Sprinting);
                    outcome.HasStroke = true;
                }

                return outcome;
            }

            _wasSwimming = false;

            if (!sample.Grounded)
            {
                // Airborne travel must not bank distance, or a long fall lands and immediately fires a
                // second step from the accumulated horizontal drift.
                _lastStepPosition = sample.Position;
                _wasGrounded = false;
                return outcome;
            }

            if (!_wasGrounded)
            {
                _wasGrounded = true;
                _lastStepPosition = sample.Position;
                outcome.Footfall = BlockSoundEvent.JumpLand;
                outcome.HasFootfall = true;
                return outcome;
            }

            Vector3 delta = sample.Position - _lastStepPosition;
            delta.y = 0f;
            if (delta.sqrMagnitude < strideLength * strideLength) return outcome;

            _lastStepPosition = sample.Position;

            // The stride is deliberately not shortened while sprinting: it is a distance, so a faster body
            // already crosses it more often, which is what a running cadence is.
            outcome.Footfall = SoundResolution.SelectStrideEvent(swimming: false, sample.Sprinting);
            outcome.HasFootfall = true;
            return outcome;
        }
    }
}
