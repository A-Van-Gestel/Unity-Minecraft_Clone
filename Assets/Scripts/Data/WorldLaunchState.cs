using Data.WorldTypes;

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
        /// The world type selected by the user during world creation.
        /// New worlds default to the fast, Burst-compiled Standard path.
        /// </summary>
        public static WorldTypeID SelectedWorldType = WorldTypeID.Standard;
    }
}
