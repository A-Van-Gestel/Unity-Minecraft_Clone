using UnityEngine;

namespace UI.Enums
{
    /// <summary>
    /// Defines the available window/fullscreen display modes.
    /// Use <see cref="ToFullScreenMode"/> to convert to <see cref="FullScreenMode"/>.
    /// </summary>
    public enum WindowMode
    {
        /// <summary>Standard movable window.</summary>
        Windowed = 0,

        /// <summary>Borderless fullscreen window covering the entire display.</summary>
        [InspectorName("Borderless Windowed")]
        BorderlessWindowed = 1,

        /// <summary>Exclusive fullscreen mode with sole display access.</summary>
        Fullscreen = 2,
    }

    /// <summary>
    /// Extension methods for <see cref="WindowMode"/>.
    /// </summary>
    public static class WindowModeExtensions
    {
        /// <summary>
        /// Converts a <see cref="WindowMode"/> to the corresponding Unity <see cref="FullScreenMode"/>.
        /// </summary>
        public static FullScreenMode ToFullScreenMode(this WindowMode mode)
        {
            return mode switch
            {
                WindowMode.Fullscreen => FullScreenMode.ExclusiveFullScreen,
                WindowMode.BorderlessWindowed => FullScreenMode.FullScreenWindow,
                _ => FullScreenMode.Windowed,
            };
        }
    }
}
