using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace UI.Enums
{
    /// <summary>
    /// Multisample anti-aliasing levels offered in the Graphics settings.
    /// Use <see cref="MsaaLevelExtensions.ToMsaaQuality"/> to convert to URP's <see cref="MsaaQuality"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately 0-based and contiguous rather than mirroring <see cref="MsaaQuality"/>'s sample counts
    /// (1/2/4/8): the settings UI binds enum dropdowns by <i>index</i>, storing the selected option's
    /// position back into the field, so a gapped enum would desync the stored value from the shown option.
    /// </remarks>
    public enum MsaaLevel
    {
        /// <summary>No multisampling. The MSAA resolve is skipped entirely.</summary>
        Off = 0,

        /// <summary>2 samples per pixel.</summary>
        [InspectorName("2x")]
        X2 = 1,

        /// <summary>4 samples per pixel.</summary>
        [InspectorName("4x")]
        X4 = 2,

        /// <summary>8 samples per pixel.</summary>
        [InspectorName("8x")]
        X8 = 3,
    }

    /// <summary>
    /// Extension methods for <see cref="MsaaLevel"/>.
    /// </summary>
    public static class MsaaLevelExtensions
    {
        /// <summary>
        /// Converts a <see cref="MsaaLevel"/> to the corresponding URP <see cref="MsaaQuality"/>.
        /// </summary>
        /// <param name="level">The selected anti-aliasing level.</param>
        /// <returns>The matching URP sample-count enum.</returns>
        public static MsaaQuality ToMsaaQuality(this MsaaLevel level)
        {
            return level switch
            {
                MsaaLevel.X2 => MsaaQuality._2x,
                MsaaLevel.X4 => MsaaQuality._4x,
                MsaaLevel.X8 => MsaaQuality._8x,
                _ => MsaaQuality.Disabled,
            };
        }
    }
}
