namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>The kind of screen that has the focus, as far as the place the player is in goes (<see cref="ScreenPlace"/>).</summary>
    internal enum FocusedScreen
    {
        /// <summary>The gameplay screen: no menu has the focus.</summary>
        None,

        /// <summary>The actions wheel.</summary>
        ActionsWheel,

        /// <summary>The wrist panel.</summary>
        WristPanel,

        /// <summary>One of the game's radial menu screens: the tools wheel's slot wheel, the system radial or the block palette.</summary>
        Radial,

        /// <summary>Any other screen: the game menu, the terminal, the G menu, a message box.</summary>
        Menu,
    }

    /// <summary>
    /// The place the player is in for the bindings (<see cref="BindingContext"/>) as the screen with the focus makes it. Kept
    /// apart from the game so the choice is checked without it (tests/BindingTests).
    /// </summary>
    internal static class ScreenPlace
    {
        /// <summary>
        /// The place the focused screen puts the player in, or <see cref="BindingContext.None"/> when it is the gameplay's to say.
        /// </summary>
        /// <param name="screen">The screen with the focus.</param>
        /// <param name="toolsWheelOwns">The tools wheel has the hands (<c>ToolbarWheel.Owns</c>): it asked for a radial screen, or has one up.</param>
        /// <remarks>
        /// A radial screen the tools wheel is driving is the tools wheel, not a menu. The grip that opened it goes on holding it
        /// open (<see cref="VRAction.ToolsWheel"/> is on in the tools wheel's place only), so a screen that put the player in
        /// the menus as it took the focus would end that hold the frame it came up, and the wheel would close as it opened.
        /// A radial screen the wheel is not driving (the game's own gamepad radial, with the VR wheels off) is a menu.
        /// </remarks>
        public static BindingContext Of(FocusedScreen screen, bool toolsWheelOwns)
        {
            switch (screen)
            {
                case FocusedScreen.ActionsWheel:
                    return BindingContext.ActionsWheel;
                case FocusedScreen.WristPanel:
                    return BindingContext.WristPanel;
                case FocusedScreen.Radial:
                    return toolsWheelOwns ? BindingContext.ToolsWheel : BindingContext.Menus;
                case FocusedScreen.Menu:
                    return BindingContext.Menus;
                default:
                    return toolsWheelOwns ? BindingContext.ToolsWheel : BindingContext.None;
            }
        }

        /// <summary>
        /// The labels beside the hands show a menu's controls for the focused screen (<c>ControlGuide.Situation.Menu</c>): any
        /// screen the bindings put in a menu's place or the actions wheel's, by the same rule as <see cref="Of"/>. The tools
        /// wheel's own radial screen is the tools wheel, whose controls are its own; the wrist panel is walked beside, so the
        /// hands keep the place's controls; with no screen, it is the gameplay's.
        /// </summary>
        public static bool LabelsShowMenu(FocusedScreen screen, bool toolsWheelOwns)
        {
            BindingContext place = Of(screen, toolsWheelOwns);
            return place == BindingContext.Menus || place == BindingContext.ActionsWheel;
        }
    }
}
