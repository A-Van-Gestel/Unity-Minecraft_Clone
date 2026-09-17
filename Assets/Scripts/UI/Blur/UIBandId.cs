namespace UI.Blur
{
    /// <summary>
    /// The ordered UI compositing bands. Each band is drawn as a group, and the screen is re-blurred
    /// between bands so a panel can frost the UI beneath it.
    /// </summary>
    /// <remarks>
    /// The order is the paint order — a higher band draws over, and can frost, every lower one. A band
    /// is a subtree rather than a canvas, so surfaces sharing one canvas can still composite separately.
    /// Values are explicit because they index the band walk.
    /// </remarks>
    public enum UIBandId
    {
        /// <summary>The always-on play surface, beneath every menu and overlay.</summary>
        Hud = 0,

        /// <summary>Full-screen menus, which cover the play surface.</summary>
        Menus = 1,

        /// <summary>Bounded surfaces that sit above a full-screen menu.</summary>
        Modals = 2,

        /// <summary>Transient feedback, drawn above everything else.</summary>
        Notifications = 3,
    }
}
