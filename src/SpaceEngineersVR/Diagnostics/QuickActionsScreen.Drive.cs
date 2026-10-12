// Test hooks for the drive's "quickmenu" command (see Drive).
using System;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    internal sealed partial class QuickActionsScreen
    {
        // ---- The drive's "quickmenu" command (test aid) ----

        private static QuickAction testAction;
        private static string testBefore;

        /// <summary>The panel that is up (not closing), or null.</summary>
        private static QuickActionsScreen Up()
        {
            QuickActionsScreen found = null;
            foreach (MyGuiScreenBase screen in MyScreenManager.Screens)
            {
                if (screen is QuickActionsScreen panel && panel.State != MyGuiScreenState.CLOSING && panel.State != MyGuiScreenState.CLOSED)
                    found = panel;
            }
            return found;
        }

        private static string Reading(QuickAction action) =>
            $"{action.Label}={Words(action, out _) ?? "-"}{(Usable(action) ? string.Empty : " (greyed)")}";

        /// <summary>The panel's buttons as they read now, in order.</summary>
        private string Describe()
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (MyGuiControlBase control in Controls)
            {
                if (control is Tile tile)
                    parts.Add(Reading(tile.Entry));
            }
            return $"{(seatedLayout ? "seated" : page == Page.ExternalView ? "External view page" : "on foot")}, {TileCount} tiles: {string.Join(", ", parts)}";
        }

        /// <summary>The seat and every seated button's state, read from the game whether the panel is up or not.</summary>
        private static string SeatState()
        {
            MyShipController seat = Seat;
            if (seat == null)
                return $"not in a seat (controlling {MySession.Static?.ControlledEntity?.GetType().Name ?? "nothing"})";
            var parts = new System.Collections.Generic.List<string>();
            foreach (QuickAction[] set in new[] { ShipActions, PilotActions, new[] { Camera } })
            {
                foreach (QuickAction action in set)
                    parts.Add(Reading(action));
            }
            return $"{seat.GetType().Name} '{seat.DisplayNameText}' on '{seat.CubeGrid?.DisplayName}', ship control {seat.EnableShipControl}, main cockpit "
                + $"{(seat.CubeGrid?.MainCockpit == null ? "none" : seat.IsMainCockpit ? "this" : "another")}, pilot {(seat.Pilot != null ? "yes" : "no")}; "
                + $"spotlights {Spotlights?.ToString() ?? "-"}, antennas {Antennas?.ToString() ?? "-"}; {string.Join(", ", parts)}";
        }

        /// <summary>
        /// Game thread, once a frame for the drive's "quickmenu" command; true when it has ended. "open": opens the panel as the
        /// actions wheel's entry does in a seat (as Y does on foot) and waits for it to have the focus; "state": the seat and every
        /// seated button's state, read from the game; "press &lt;label&gt;": presses that button as a trigger pull does (its own
        /// click), waits for the game to take the control, and reports its state before and after; "close": closes the panel.
        /// </summary>
        internal static bool Test(string args, int frames, out bool ok, out string detail)
        {
            ok = false;
            detail = null;
            string text = (args ?? string.Empty).Trim();
            int space = text.IndexOf(' ');
            string verb = (space < 0 ? text : text.Substring(0, space)).ToLowerInvariant();
            string rest = space < 0 ? string.Empty : text.Substring(space + 1).Trim();
            QuickActionsScreen up = Up();
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
                        if (Seat != null)
                            OpenSeated();
                        else
                            MyGuiSandbox.AddScreen(new QuickActionsScreen());
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
                    detail = "the panel did not get the focus in 120 frames";
                    return true;

                case "state":
                    ok = true;
                    detail = SeatState();
                    return true;

                case "press":
                    if (frames == 0)
                    {
                        if (up == null)
                        {
                            detail = "the panel is not up";
                            return true;
                        }
                        Tile tile = null;
                        var labels = new System.Collections.Generic.List<string>();
                        foreach (MyGuiControlBase control in up.Controls)
                        {
                            if (!(control is Tile t))
                                continue;
                            labels.Add(t.Entry.Label);
                            if (string.Equals(t.Entry.Label, rest, StringComparison.OrdinalIgnoreCase))
                                tile = t;
                        }
                        if (tile == null)
                        {
                            detail = $"no button '{rest}' ({string.Join(", ", labels)})";
                            return true;
                        }
                        testAction = tile.Entry;
                        testBefore = Reading(tile.Entry);
                        WristButton.LastOutcome = null;
                        tile.PressButton();
                        return false;
                    }
                    if (frames < 12 || (WristButton.Busy && frames < 240))
                        return false;
                    ok = true;
                    detail = $"{testBefore} -> {Reading(testAction)}; {(testAction.Run != null ? "ran" : WristButton.LastOutcome ?? "not pressed")}; panel {(up != null ? "still up" : "closed")}";
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
                    detail = $"quickmenu: unknown '{verb}' (open, state, press <label>, close)";
                    return true;
            }
        }
    }
}
