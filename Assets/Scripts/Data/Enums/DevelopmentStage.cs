using System.Diagnostics.CodeAnalysis;

namespace Data.Enums
{
    /// <summary>
    /// Defines the current stage of development for the version string.
    /// </summary>
    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    public enum DevelopmentStage
    {
        Prototype,
        PreAlpha,
        Alpha,
        Beta,
        ReleaseCandidate,
        Release,
    }
}
