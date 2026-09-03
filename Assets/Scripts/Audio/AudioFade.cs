using UnityEngine;

namespace Audio
{
    /// <summary>
    /// Fade integration shared by every layer that ramps a gain over time — the ambience beds, the fluid
    /// emitters, the music scheduler and the underwater filter.
    /// </summary>
    /// <remarks>
    /// Its own type rather than a member of one layer's resolution class: three of the four consumers would
    /// otherwise be reaching across into a class named for the fourth, and the one that did not reach across
    /// hand-rolled the same step instead.
    /// </remarks>
    public static class AudioFade
    {
        /// <summary>
        /// Advances one fade toward its target at the authored rate.
        /// </summary>
        /// <param name="currentFade">The fade position, [0, 1].</param>
        /// <param name="targetFade">Where it is heading — 1 for the selected source, 0 for every other.</param>
        /// <param name="deltaTime">Seconds since the previous tick.</param>
        /// <param name="fadeSeconds">Seconds a full 0↔1 traversal takes. Zero or fewer snaps.</param>
        /// <returns>The new fade position, clamped to [0, 1].</returns>
        /// <remarks>
        /// Each source owns its own fade rather than two sharing one crossfade timer. A paired timer has no
        /// answer for a change arriving mid-fade: whichever source the pair reassigns is cut at whatever gain
        /// it happened to hold. Independent fades make that case ordinary — a bed the player returns to is
        /// still playing, so its target simply flips back to 1, and it rises from where it was.
        /// </remarks>
        public static float Advance(float currentFade, float targetFade, float deltaTime, float fadeSeconds)
        {
            float target = Mathf.Clamp01(targetFade);
            float step = fadeSeconds <= 0f ? 1f : Mathf.Max(0f, deltaTime) / fadeSeconds;
            return Mathf.MoveTowards(Mathf.Clamp01(currentFade), target, step);
        }
    }
}
