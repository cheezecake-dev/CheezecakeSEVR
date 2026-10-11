using System;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The drive's "wheel" command (a test aid, as "quickmenu" is the wrist panel's): the actions wheel opened, read and used
    /// without a hand pointing into it.
    /// <list type="bullet">
    /// <item>"wheel open [left|right]": opens the wheel at that hand (the right by default) as the actions button does, with
    /// the trigger to choose, and waits for it to have the focus; the detail is the ring.</item>
    /// <item>"wheel state": the ring up now, each entry with its state line, "&gt;" for a page and "(greyed)" where it cannot be used.</item>
    /// <item>"wheel pick &lt;name&gt;": chooses the entry of that name on the ring up now as the trigger does. A page shows its
    /// ring; an entry that presses a game control waits for the game to read it (<see cref="WristButton"/>) and says whether it did.</item>
    /// <item>"wheel close": closes it.</item>
    /// </list>
    /// </summary>
    internal static class ActionsWheelDrive
    {
        /// <summary>The wheel that is up (not closing), or null.</summary>
        private static ActionsWheelScreen Up()
        {
            ActionsWheelScreen found = null;
            foreach (MyGuiScreenBase screen in MyScreenManager.Screens)
            {
                if (screen is ActionsWheelScreen wheel && wheel.State != MyGuiScreenState.CLOSING && wheel.State != MyGuiScreenState.CLOSED)
                    found = wheel;
            }
            return found;
        }

        private static string picked;

        /// <summary>Game thread, once a frame for the drive's "wheel" command; true when it has ended (<paramref name="ok"/> and <paramref name="detail"/> say how).</summary>
        internal static bool Step(string args, int frames, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            string text = (args ?? string.Empty).Trim();
            int space = text.IndexOf(' ');
            string verb = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
            string rest = space < 0 ? string.Empty : text.Substring(space + 1).Trim().Trim('"');
            ActionsWheelScreen up = Up();
            switch (verb)
            {
                case "open":
                    if (frames == 0)
                    {
                        if (up != null)
                        {
                            ok = true;
                            detail = "already up; " + up.Describe();
                            return true;
                        }
                        Hand hand = string.Equals(rest, "left", StringComparison.OrdinalIgnoreCase) ? Hand.Left : Hand.Right;
                        if (!ActionsWheel.Open(hand, gripHeld: false))
                        {
                            detail = "the wheel did not open (see the log)";
                            return true;
                        }
                        return false;
                    }
                    if (up != null && MyScreenManager.GetScreenWithFocus() == up)
                    {
                        ok = true;
                        detail = up.Describe();
                        return true;
                    }
                    if (frames < 120)
                        return false;
                    detail = "the wheel did not get the focus in 120 frames";
                    return true;

                case "state":
                    ok = up != null;
                    detail = up != null ? up.Describe() : "the wheel is not up";
                    return true;

                case "pick":
                    if (frames == 0)
                    {
                        if (up == null)
                        {
                            detail = "the wheel is not up";
                            return true;
                        }
                        WristButton.LastOutcome = null;
                        if (!up.DrivePick(rest, out detail))
                            return true;
                        picked = detail;
                        return false;
                    }
                    // A control's press waits for the wheel to close and the gameplay screen's next pass (2 s at most).
                    if (frames < 12 || (WristButton.Busy && frames < 240))
                        return false;
                    ok = true;
                    detail = $"{picked}: {(WristButton.LastOutcome != null ? "control " + WristButton.LastOutcome : "no control pressed")}; "
                        + (up != null ? "wheel up, " + up.Describe() : "wheel closed");
                    return true;

                case "close":
                    if (frames == 0 && up != null)
                    {
                        up.CloseScreen();
                        return false;
                    }
                    if (up != null && frames < 60)
                        return false;
                    ok = up == null;
                    detail = ok ? "closed" : "still up after 60 frames";
                    return true;

                default:
                    detail = $"wheel: unknown '{verb}' (open [left|right], state, pick <name>, close)";
                    return true;
            }
        }
    }
}
