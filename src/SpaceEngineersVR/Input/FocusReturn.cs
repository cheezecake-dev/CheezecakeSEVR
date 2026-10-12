// FocusReturn: the shared surface of a launch-time aid that keeps a test launch from taking the foreground.
namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Keeps a game started for a test from holding the foreground. With the environment variable <see cref="Variable"/> set
    /// (a test launcher sets it to the window that had the foreground at launch), a background thread watches the
    /// foreground window from before the game's Main runs. Whenever a window of this process takes it (the game shows and
    /// focuses its window at start, MySandboxGame.InitQuickLaunch), it is handed straight back to the window that had it
    /// before. No keyboard or mouse input is sent. Without the variable nothing here runs.
    /// </summary>
    /// <remarks>
    /// Windows only (FocusReturn.Windows.cs): the game is the foreground process at that moment, so Windows lets it give the
    /// foreground away. A take with a mouse button or Alt down is someone at the PC clicking or Alt-Tabbing into the game;
    /// from then on the game keeps the foreground it is given (logged once). On Linux the variable is ignored
    /// (FocusReturn.Linux.cs), as there is no user32 to call.
    /// </remarks>
    internal static partial class FocusReturn
    {
        public const string Variable = "SEVR_FOCUS_BACK";
    }
}
