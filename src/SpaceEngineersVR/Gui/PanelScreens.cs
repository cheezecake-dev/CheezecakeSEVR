// PanelScreens: other plugins' GUI screens registered through SevrApi (RegisterPanelScreen), shown on the VR panel as they ask.
using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Api;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// What the panel does for a screen another plugin registered (<see cref="ApiScreens"/>, <see cref="PanelScreenOptions"/>),
    /// followed from the screen with the focus once a frame (<see cref="Track"/>, from <see cref="WorldMenu"/>):
    /// <list type="bullet">
    /// <item>Cursor: while it has the focus in VR, the game's cursor is shown on it (the game's own
    /// MyGuiScreenBase.DrawMouseCursor, set on and put back as it was when the focus leaves or VR goes), so the laser points
    /// and clicks as on every other screen.</item>
    /// <item>AtHand: as it takes the focus the panel is fixed at the off hand (<see cref="Panel"/>), as the plugin's wheels
    /// are, and goes back to the menus' place as it loses it.</item>
    /// <item>WalkBeside: the sticks keep walking while it has the focus on foot (<see cref="WalksBeside"/>, read by
    /// <see cref="Input.VRControls"/>).</item>
    /// </list>
    /// Every other screen that takes the focus is on the panel ahead, as the game's are, with nothing registered.
    /// </summary>
    internal static class PanelScreens
    {
        /// <summary>Where the panel stands for an AtHand screen while it has the focus.</summary>
        internal static readonly WheelAtHand Panel = new WheelAtHand();

        private static readonly FeatureGuard guard = new FeatureGuard("Registered panel screens");

        /// <summary>The game's protected MyGuiScreenBase.DrawMouseCursor setter; null if this game build has none.</summary>
        private static readonly Action<MyGuiScreenBase, bool> setCursor = CursorSetter();

        private static MyGuiScreenBase atHand, cursorOn;
        private static bool cursorMissingLogged;

        /// <summary>The options registered for <paramref name="screen"/>'s type; none for the game's, the plugin's and unregistered ones.</summary>
        internal static PanelScreenOptions Of(object screen) =>
            screen == null || screen is MyGuiScreenGamePlay ? PanelScreenOptions.None : ApiRuntime.Screens.Of(screen.GetType().FullName);

        /// <summary>The screen was registered to let the player walk beside it (<see cref="PanelScreenOptions.WalkBeside"/>).</summary>
        internal static bool WalksBeside(object screen)
        {
            try
            {
                return (Of(screen) & PanelScreenOptions.WalkBeside) != 0;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Registered panel screens: could not tell whether a screen walks beside");
                return false;
            }
        }

        /// <summary>
        /// Game thread, once a frame as the game draws its screens: follows the screen with the focus, placing the panel at the
        /// hand and showing the cursor as its registration asks, and undoing both when it loses the focus or VR goes.
        /// </summary>
        internal static void Track(MyGuiScreenBase focus)
        {
            if (!guard.On)
            {
                Release();
                return;
            }
            try
            {
                bool vr = StereoPatches.VRPicture;
                PanelScreenOptions options = vr ? Of(focus) : PanelScreenOptions.None;
                FollowHand(focus, (options & PanelScreenOptions.AtHand) != 0);
                FollowCursor(focus, (options & PanelScreenOptions.Cursor) != 0);
            }
            catch (Exception e)
            {
                Release();
                guard.Failed(e, "Registered panel screens: could not follow the screen with the focus; they are shown as any screen is meanwhile");
            }
        }

        private static void FollowHand(MyGuiScreenBase focus, bool wanted)
        {
            if (ReferenceEquals(atHand, wanted ? focus : null))
                return;
            if (atHand != null)
            {
                Panel.Close();
                atHand = null;
            }
            if (!wanted)
                return;
            Hand hand = Hands.TwoHand.OffHand;
            Panel.Open(hand, out float fromHead);
            atHand = focus;
            Log.Info($"Registered panel screen {focus.GetType().FullName}: at the {hand.ToString().ToLowerInvariant()} hand, {fromHead:0.00} m from the head");
        }

        private static void FollowCursor(MyGuiScreenBase focus, bool wanted)
        {
            if (cursorOn != null && (!wanted || !ReferenceEquals(cursorOn, focus)))
            {
                // It was hidden before it was shown here, so hidden is how it goes back.
                setCursor?.Invoke(cursorOn, false);
                cursorOn = null;
            }
            if (!wanted || focus.GetDrawMouseCursor() && cursorOn == null)
                return;
            if (setCursor == null)
            {
                if (!cursorMissingLogged)
                    Log.Warn("Registered panel screens: this game build has no MyGuiScreenBase.DrawMouseCursor; a Cursor screen shows no cursor");
                cursorMissingLogged = true;
                return;
            }
            // Shown again each frame it is hidden while it keeps the focus: the screen's own code may hide it as it updates.
            if (!focus.GetDrawMouseCursor())
                setCursor(focus, true);
            if (cursorOn == null)
                Log.Info($"Registered panel screen {focus.GetType().FullName}: the cursor is shown on it in VR");
            cursorOn = focus;
        }

        /// <summary>Undoes what is in place: the panel back from the hand, the cursor put back.</summary>
        private static void Release()
        {
            try
            {
                if (atHand != null)
                    Panel.Close();
                if (cursorOn != null)
                    setCursor?.Invoke(cursorOn, false);
            }
            catch (Exception e)
            {
                Log.Error(e, "Registered panel screens: could not put a screen back as it was");
            }
            atHand = cursorOn = null;
        }

        private static Action<MyGuiScreenBase, bool> CursorSetter()
        {
            try
            {
                MethodInfo setter = AccessTools.PropertySetter(typeof(MyGuiScreenBase), "DrawMouseCursor");
                return setter == null ? null : (Action<MyGuiScreenBase, bool>)Delegate.CreateDelegate(typeof(Action<MyGuiScreenBase, bool>), setter);
            }
            catch (Exception e)
            {
                Log.Error(e, "Registered panel screens: MyGuiScreenBase.DrawMouseCursor could not be bound");
                return null;
            }
        }
    }
}
