using Unity.Scripting.LifecycleManagement;
using UnityEditor;

namespace Editor.ProjectUtilities
{
    /// <summary>
    /// Holds script reloads while the Editor is in Play mode: code changed during play compiles and loads only when Play
    /// mode stops, whatever the <i>Script Changes While Playing</i> preference says. The engine's runtime state
    /// does not survive a Play-mode reload — native collections and other unserialized state are lost, so a reload leaks
    /// them and leaves the world's systems throwing every frame.
    /// <para>
    /// The lock is taken once Play mode has been entered, after the domain reload that entering it performs, and released
    /// once Edit mode has been entered — after the play scene's teardown has freed its native state — when Unity runs the
    /// compile and reload it held. Meanwhile <see cref="EditorApplication.isCompiling"/> stays true.
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModeReloadGuard
    {
        // Editor-only and true only during Play mode, when no domain reload can run, so it never outlives one.
#pragma warning disable UDR0001
        [NoAutoStaticsCleanup] // cleared in Unlock
        private static bool s_isLocked;
#pragma warning restore UDR0001

        static PlayModeReloadGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>Whether the guard is holding script reloads.</summary>
        public static bool IsHoldingReloads => s_isLocked;

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    Lock();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    Unlock();
                    break;
            }
        }

        // The Editor counts reload locks, so each one taken here is released exactly once.
        private static void Lock()
        {
            if (s_isLocked) return;

            EditorApplication.LockReloadAssemblies();
            s_isLocked = true;
        }

        private static void Unlock()
        {
            if (!s_isLocked) return;

            EditorApplication.UnlockReloadAssemblies();
            s_isLocked = false;
        }
    }
}
