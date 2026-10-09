using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// Adds a "VR" button to the game's Options screen, which opens <see cref="VROptionsScreen"/>. That screen is
    /// SpaceEngineers.Game.GUI.MyGuiScreenOptionsSpace, an internal class. The main menu's Options button and the in-game
    /// Escape menu's both open it (MyGuiScreenMainMenu.OnClickOptions), so one patch covers both. The screen has no room
    /// for another button, so it is made one button row taller before its controls are laid out, and the VR button goes
    /// in above Credits, which moves down one place.
    /// </summary>
    internal static class VROptionsEntry
    {
        public const string ScreenTypeName = "SpaceEngineers.Game.GUI.MyGuiScreenOptionsSpace";
        public const string RecreateName = "RecreateControls";
        public const string CreditsName = "Credits";
        public const string ButtonName = "VR";

        /// <summary>Screens already made taller; RecreateControls can run more than once for one screen.</summary>
        private static readonly ConditionalWeakTable<MyGuiScreenBase, object> Roomy = new ConditionalWeakTable<MyGuiScreenBase, object>();

        /// <summary>Hooks the Options screen. Never throws: without this button the rest of VR is unaffected.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                Type screen = typeof(SpaceEngineersGame).Assembly.GetType(ScreenTypeName);
                MethodInfo recreate = screen == null ? null : AccessTools.DeclaredMethod(screen, RecreateName, new[] { typeof(bool) });
                if (recreate == null)
                {
                    Log.Warn($"The Options screen ({ScreenTypeName}.{RecreateName}) was not found; there is no VR button (the game changed?)");
                    return;
                }
                harmony.Patch(recreate,
                    prefix: new HarmonyMethod(typeof(VROptionsEntry), nameof(MakeRoom)),
                    postfix: new HarmonyMethod(typeof(VROptionsEntry), nameof(AddButton)));
                Log.Info("Options screen patched: VR button");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not add the VR button to the Options screen; continuing without it");
            }
        }

        private static void MakeRoom(MyGuiScreenBase __instance)
        {
            try
            {
                Vector2? size = __instance.Size;
                if (!size.HasValue || Roomy.TryGetValue(__instance, out _))
                    return;
                Roomy.Add(__instance, null);
                __instance.Size = new Vector2(size.Value.X, size.Value.Y + MyGuiConstants.MENU_BUTTONS_POSITION_DELTA.Y);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not make room for the VR button on the Options screen");
            }
        }

        private static void AddButton(MyGuiScreenBase __instance)
        {
            try
            {
                MyGuiControlBase credits = __instance.Controls.GetControlByName(CreditsName);
                if (credits == null)
                {
                    Log.Warn("The Options screen has no Credits button to put the VR button above; there is no VR button (the game changed?)");
                    return;
                }
                Vector2 at = credits.Position;
                credits.Position = at + MyGuiConstants.MENU_BUTTONS_POSITION_DELTA;
                var button = new MyGuiControlButton(at, MyGuiControlButtonStyleEnum.Default, null, null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                    "Settings for playing in VR: display, height, controls", new StringBuilder("VR"), 0.8f, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                    MyGuiControlHighlightType.WHEN_CURSOR_OVER, OpenVROptions);
                button.Name = ButtonName;
                __instance.Controls.Add(button);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not add the VR button to the Options screen");
            }
        }

        private static void OpenVROptions(MyGuiControlButton sender)
        {
            try
            {
                MyGuiSandbox.AddScreen(new VROptionsScreen());
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not open the VR options");
            }
        }
    }
}
