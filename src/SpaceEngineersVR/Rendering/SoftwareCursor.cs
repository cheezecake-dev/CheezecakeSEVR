using System;
using Sandbox.Engine.Utils;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The cursor dot on the panel. The game draws its own cursor sprite into the GUI only when it is not using the
    /// operating system's cursor (MyDX9Gui.Draw: <c>if (!MyVideoSettingsManager.IsHardwareCursorUsed() || MyFakes.FORCE_SOFTWARE_MOUSE_DRAW)
    /// DrawMouseCursor(...)</c>), and on Windows 10 and 11 it always is. The OS cursor is not in the panel, and the panel is where the laser
    /// (<see cref="Input.Laser"/>) and <see cref="PanelCursor"/> put the cursor, so no dot showed in the VR menus. The game's own switch
    /// for drawing the sprite as well is turned on once the picture is VR's: it draws it at the position it reads (<see cref="PanelCursor"/>).
    /// </summary>
    /// <remarks>
    /// The other way, telling the game it has no hardware cursor (a postfix on IsHardwareCursorUsed), also hides the OS cursor over the window,
    /// but then the game reports MySandboxGame.IsCursorVisible false for every screen, and the laser, the hand ray and the hand flight all read
    /// that as "no menu is up". This way that flag still means what it did, and the OS cursor stays over the mirror window, where it is harmless.
    /// </remarks>
    internal static class SoftwareCursor
    {
        private static bool off, applied;

        /// <summary>The game's own value of the switch, taken before it is first set; null until then.</summary>
        private static bool? gameValue;

        /// <summary>Debug: the game's own choice stands, the OS cursor and no drawn one (<see cref="RenderDebug"/> SoftwareCursor=0).</summary>
        public static bool Off
        {
            get => off;
            set
            {
                off = value;
                if (applied)
                    Set();
            }
        }

        /// <summary>When the picture turns to VR's (<see cref="StereoPatches.VRPicture"/>); until then the game's switch is left alone.</summary>
        public static void Apply()
        {
            try
            {
                gameValue = MyFakes.FORCE_SOFTWARE_MOUSE_DRAW;
            }
            catch (Exception e)
            {
                Log.Error(e, "The software cursor switch could not be read; the VR menus show no cursor dot");
                return;
            }
            Resume();
            StereoPatches.PictureChanged += vr =>
            {
                if (vr)
                    Resume();
                else
                    Restore();
            };
            Log.Info("Software cursor: the game draws its own cursor into the panel (MyFakes.FORCE_SOFTWARE_MOUSE_DRAW); SoftwareCursor=0 turns it off");
        }

        /// <summary>The picture is VR's again after the headset was away (<see cref="StereoPatches.PictureChanged"/>); nothing if <see cref="Apply"/> never ran.</summary>
        public static void Resume()
        {
            if (!gameValue.HasValue)
                return;
            applied = true;
            Set();
        }

        /// <summary>The picture is the game's while the headset is away: the switch goes back to the game's value.</summary>
        public static void Restore()
        {
            if (!applied || !gameValue.HasValue)
                return;
            applied = false;
            try
            {
                MyFakes.FORCE_SOFTWARE_MOUSE_DRAW = gameValue.Value;
            }
            catch (Exception e)
            {
                Log.Error(e, "The software cursor switch could not be put back; the game may draw its cursor over the OS one");
            }
        }

        private static void Set()
        {
            try
            {
                MyFakes.FORCE_SOFTWARE_MOUSE_DRAW = !off;
            }
            catch (Exception e)
            {
                Log.Error(e, "The software cursor switch could not be set; the VR menus show no cursor dot");
            }
        }
    }
}
