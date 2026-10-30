using SpaceEngineersVR.Input;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Which interaction holds each hand's grip, in the world and on a menu. A grip that starts with the hand on something a
    /// feature can take (a hull to pull along, the helmet, a door, a floating item) belongs to that feature until it is let go;
    /// meanwhile the grip's meaning in empty air (Use on the right hand, the toolbar wheel on the left on foot) is skipped. On
    /// a panel, a grip a menu feature claims (an inventory pull) is not the right mouse button and not the game's Use.
    /// </summary>
    /// <remarks>
    /// The game's controllers' grips, read through <see cref="VRInput"/>; the rules are <see cref="GripTable"/>'s, the order
    /// <see cref="GripPriority"/>'s. Features register a <see cref="GripTable.Taker"/> once.
    /// </remarks>
    internal static class GripClaim
    {
        private static readonly GripTable table = new GripTable(
            hand => VRInput.IsPressed(hand, VRButtons.Grip),
            hand => VRInput.IsNewPressed(hand, VRButtons.Grip));

        /// <inheritdoc cref="GripTable.Register"/>
        public static void Register(string name, GripPriority priority, GripTable.Taker take) => table.Register(name, priority, take);

        /// <inheritdoc cref="GripTable.Owner"/>
        public static string Owner(Hand hand) => table.Owner(hand);

        /// <inheritdoc cref="GripTable.Holds"/>
        public static bool Holds(Hand hand, string name) => table.Holds(hand, name);

        /// <inheritdoc cref="GripTable.ClaimInMenu"/>
        public static bool ClaimInMenu(Hand hand, string name) => table.ClaimInMenu(hand, name);

        /// <inheritdoc cref="GripTable.MenuOwner"/>
        public static string MenuOwner(Hand hand) => table.MenuOwner(hand);

        /// <inheritdoc cref="GripTable.Release"/>
        public static void Release(Hand hand, string name) => table.Release(hand, name);

        /// <inheritdoc cref="GripTable.HoldUse"/>
        public static void HoldUse(Hand hand) => table.HoldUse(hand);

        /// <inheritdoc cref="GripTable.UseWaits"/>
        public static bool UseWaits(Hand hand) => table.UseWaits(hand);

        /// <inheritdoc cref="GripTable.DropUse"/>
        public static void DropUse(Hand hand) => table.DropUse(hand);

        /// <inheritdoc cref="GripTable.UseDropped"/>
        public static bool UseDropped(Hand hand) => table.UseDropped(hand);
    }
}
