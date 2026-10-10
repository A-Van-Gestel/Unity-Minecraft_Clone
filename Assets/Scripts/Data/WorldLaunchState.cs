using Data.Enums;
using Data.WorldTypes;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// A static container to pass configuration from the Main Menu to the Game Scene.
    /// </summary>
    public static class WorldLaunchState
    {
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static string WorldName = "New World";
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static int Seed = 0;
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static bool IsNewGame = true;

        /// <summary>
        /// The current operational mode of the game.
        /// </summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static RuntimeMode CurrentMode = RuntimeMode.Default;

        /// <summary>
        /// True when the game is running under any automated harness (<see cref="RuntimeMode.Benchmark"/> or
        /// <see cref="RuntimeMode.FluidStress"/>) rather than interactive play. Used to suppress manual player
        /// movement, block interaction, the toolbar, and on-screen touch controls so the harness drives the
        /// session without interference. Prefer this over scattering per-mode equality checks.
        /// </summary>
        public static bool IsAutomatedMode => CurrentMode is RuntimeMode.Benchmark or RuntimeMode.FluidStress;

        /// <summary>
        /// The world type selected by the user during world creation.
        /// New worlds default to the fast, Burst-compiled Standard path.
        /// </summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static WorldTypeID SelectedWorldType = WorldTypeID.Standard;

        /// <summary>
        /// Gameplay border half-extent (in voxels) chosen for a new world, or <c>0</c> for no border (the default).
        /// Persisted into level.dat on the world's first save (TF-14).
        /// </summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static int BorderRadius = 0;

        /// <summary>The value of <see cref="WorldSceneRequestedAt"/> when no scene load request is pending.</summary>
        public const double NoSceneRequest = -1.0;

        /// <summary>
        /// <see cref="Time.realtimeSinceStartupAsDouble"/> when the World scene load was requested, or
        /// <see cref="NoSceneRequest"/>: the earliest instant a launch's elapsed time can be measured from, scene load
        /// included.
        /// </summary>
        [NoAutoStaticsCleanup] // reset in DomainReset
        public static double WorldSceneRequestedAt = NoSceneRequest;

        /// <summary>Stamps the World scene load request; call right before loading the scene.</summary>
        public static void MarkWorldSceneRequested() => WorldSceneRequestedAt = Time.realtimeSinceStartupAsDouble;

        /// <summary>
        /// Returns the pending scene load request and clears it, so a later World started without one (Play pressed in
        /// the World scene) cannot inherit a stale stamp.
        /// </summary>
        /// <returns>The request time, or <see cref="NoSceneRequest"/>.</returns>
        public static double TakeWorldSceneRequest()
        {
            double requestedAt = WorldSceneRequestedAt;
            WorldSceneRequestedAt = NoSceneRequest;
            return requestedAt;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            WorldName = "New World";
            Seed = 0;
            IsNewGame = true;
            CurrentMode = RuntimeMode.Default;
            SelectedWorldType = WorldTypeID.Standard;
            BorderRadius = 0;
            WorldSceneRequestedAt = NoSceneRequest;
        }
    }
}
