using JetBrains.Annotations;

namespace UI.Enums
{
    /// <summary>
    /// Defines the available vertical synchronization modes.
    /// Values map directly to <see cref="UnityEngine.QualitySettings.vSyncCount"/>.
    /// </summary>
    public enum VSyncMode
    {
        /// <summary>VSync disabled. Frame rate is governed by <see cref="Settings.maxFps"/>.</summary>
        Off = 0,

        /// <summary>Synchronizes rendering to every VBlank. Eliminates tearing but FPS snaps to refresh-rate divisors when the GPU can't keep up.</summary>
        On = 1,

        /// <summary>Synchronizes rendering to every second VBlank, capping at half the display refresh rate.</summary>
        [UsedImplicitly]
        HalfRefreshRate = 2,
    }
}
