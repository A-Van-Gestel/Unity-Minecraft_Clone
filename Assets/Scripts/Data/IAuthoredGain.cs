using UnityEngine;

namespace Data
{
    /// <summary>
    /// The clip-and-trim pair every authored sound entry carries, so tooling can read a mixed set of them
    /// without knowing which kind of entry each one is.
    /// </summary>
    /// <remarks>
    /// An interface rather than a shared base type, because two of the three implementers are <c>struct</c>s
    /// and must stay that way: a class base could initialize <c>volume</c> to 1, which is exactly the unset
    /// sentinel SOUND_ENGINE_DESIGN.md §17 removed (an omitted trim means <b>silent</b>).
    /// <para>
    /// <b>Consume it as a generic constraint</b> (<c>where T : IAuthoredGain</c>), never as an
    /// interface-typed parameter or collection: two of the three implementers are structs, and the latter
    /// boxes every one (`GENERAL_OPTIMIZATION_GUIDE.md` §4). A constraint dispatches without allocating —
    /// the same shape <see cref="Helpers.INeighborGates"/> and <c>IBlockObstruction</c> are used in.
    /// </para>
    /// <para>
    /// Members are implemented implicitly, unlike those two: <c>EffectiveVolume</c> was already public API
    /// on the tracks before this interface existed, and explicit implementation would have forced every
    /// direct reader through a cast — which is the box this rule exists to avoid.
    /// </para>
    /// </remarks>
    public interface IAuthoredGain
    {
        /// <summary>The clip this entry plays, or null when the entry is silent by omission.</summary>
        AudioClip Clip { get; }

        /// <summary>The authored content trim, in <c>[0, 1]</c>. Zero is silent.</summary>
        /// <remarks>
        /// What an <i>unset</i> trim means is per-implementer: the track structs default to silent, while
        /// <see cref="EmitterSoundEntry"/> is a class whose field initializer sets full level.
        /// </remarks>
        float EffectiveVolume { get; }
    }
}
