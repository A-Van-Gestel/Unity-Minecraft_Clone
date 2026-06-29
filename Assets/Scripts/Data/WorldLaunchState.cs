using Data.Enums;
using Data.WorldTypes;
using UnityEngine;

namespace Data
{
    /// <summary>
    /// A static container to pass configuration from the Main Menu to the Game Scene.
    /// </summary>
    public static class WorldLaunchState
    {
        public static string WorldName = "New World";
        public static int Seed = 0;
        public static bool IsNewGame = true;

        /// <summary>
        /// The current operational mode of the game.
        /// </summary>
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
        public static WorldTypeID SelectedWorldType = WorldTypeID.Standard;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void DomainReset()
        {
            WorldName = "New World";
            Seed = 0;
            IsNewGame = true;
            CurrentMode = RuntimeMode.Default;
            SelectedWorldType = WorldTypeID.Standard;
        }
    }
}
