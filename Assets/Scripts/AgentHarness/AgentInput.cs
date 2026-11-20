using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentHarness
{
    /// <summary>
    /// Agent-driven gameplay input, added to the real input in <c>InputManager</c> like the touch controls are. A hold
    /// lasts a span of game time (frame rate varies too much for frame counts) and expires on its own, so a dropped command never leaves a key stuck; a scene
    /// unload releases everything. The player ignores it while a menu is open or an automated mode runs, as it does
    /// real input.
    /// </summary>
    public static class AgentInput
    {
        private const int NO_FRAME = -1;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static Vector2 s_move;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static bool s_jump, s_crouch;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_holdStartFrame = NO_FRAME;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static float s_holdUntilTime;

        [NoAutoStaticsCleanup] // reset in DomainReset
        private static int s_attackFrame = NO_FRAME, s_useFrame = NO_FRAME;

        /// <summary>Movement intent (x = strafe, y = forward), zero outside a hold.</summary>
        public static Vector2 MoveInput => IsHolding ? s_move : Vector2.zero;

        /// <summary>True on the first frame of a hold that jumps.</summary>
        public static bool JumpPressed => s_jump && Time.frameCount == s_holdStartFrame;

        /// <summary>True while a hold that jumps runs.</summary>
        public static bool JumpHeld => s_jump && IsHolding;

        /// <summary>Crouch (descend while flying) value, 0 or 1.</summary>
        public static float CrouchValue => s_crouch && IsHolding ? 1f : 0f;

        /// <summary>True on the one frame an agent break (attack) was requested for.</summary>
        public static bool AttackPressed => Time.frameCount == s_attackFrame;

        /// <summary>True on the one frame an agent place (use) was requested for.</summary>
        public static bool UsePressed => Time.frameCount == s_useFrame;

        /// <summary>True while a hold runs.</summary>
        public static bool IsHolding => s_holdStartFrame != NO_FRAME && Time.frameCount >= s_holdStartFrame &&
                                        Time.time < s_holdUntilTime;

        /// <summary>The <see cref="Time.time"/> the current or last hold ends at.</summary>
        public static float HoldEndsAt => s_holdUntilTime;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            Release();
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        /// <summary>Holds movement and buttons from the next frame for <paramref name="seconds"/> of game time.</summary>
        /// <param name="move">Movement intent (x = strafe, y = forward), clamped to length 1.</param>
        /// <param name="jump">Hold jump (ascend while flying).</param>
        /// <param name="crouch">Hold crouch (descend while flying).</param>
        /// <param name="seconds">How long to hold; the hold always covers at least its first frame.</param>
        public static void Hold(Vector2 move, bool jump, bool crouch, float seconds)
        {
            s_move = Vector2.ClampMagnitude(move, 1f);
            s_jump = jump;
            s_crouch = crouch;
            s_holdStartFrame = Time.frameCount + 1;
            s_holdUntilTime = Time.time + Mathf.Max(seconds, Time.maximumDeltaTime);
        }

        /// <summary>Requests one break (attack) press on the next frame.</summary>
        /// <returns>The frame the press lands on.</returns>
        public static int PressAttack() => s_attackFrame = Time.frameCount + 1;

        /// <summary>Requests one place (use) press on the next frame.</summary>
        /// <returns>The frame the press lands on.</returns>
        public static int PressUse() => s_useFrame = Time.frameCount + 1;

        /// <summary>Ends every hold and pending press at once.</summary>
        public static void Release()
        {
            s_move = Vector2.zero;
            s_jump = s_crouch = false;
            s_holdStartFrame = NO_FRAME;
            s_holdUntilTime = 0f;
            s_attackFrame = s_useFrame = NO_FRAME;
        }

        private static void OnSceneUnloaded(Scene scene) => Release();
    }
}
