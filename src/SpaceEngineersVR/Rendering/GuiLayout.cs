using EmptyKeys.UserInterface;
using HarmonyLib;
using Sandbox;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The GUI is laid out at a fixed 16:9 size, whatever the size of the mirror window. It is a panel
    /// (<see cref="HudPanel"/>) of that size, no longer the window, and a window that is not 16:9 would lay the menus
    /// out for a shape the panel does not have. The game takes its GUI size from one place, MySandboxGame.ScreenSize
    /// (it hands it to MyGuiManager.UpdateScreenSize and to the GUI screens that size themselves from it), so it is
    /// replaced there; the camera keeps the window's viewport (MySandboxGame.ScreenViewport) and its aspect.
    /// </summary>
    /// <remarks>
    /// Until the picture is VR's (<see cref="StereoPatches.VRPicture"/>) the GUI keeps the window's size.
    /// <para>
    /// The MVVM screens (store, ATM, contracts) are the exception: EmptyKeys lays them out, and hit-tests the mouse, in the
    /// size its renderer reports (VRage.UserInterface's MyRenderer.GetViewport, the render's MainViewport, which is the
    /// window), while they are drawn into the GUI. That size is replaced as well.
    /// </para>
    /// </remarks>
    internal static class GuiLayout
    {
        /// <summary>The panel's resolution, and the size the GUI is laid out for.</summary>
        public static readonly Vector2I Size = new Vector2I(1920, 1080);

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MySandboxGame), nameof(MySandboxGame.UpdateScreenSize)),
                prefix: new HarmonyMethod(typeof(GuiLayout), nameof(UseGuiSize)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("VRage.UserInterface.Renderers.MyRenderer"), "GetViewport"),
                postfix: new HarmonyMethod(typeof(GuiLayout), nameof(UseGuiViewport)));
        }

        private static void UseGuiViewport(ref Rect __result)
        {
            if (StereoPatches.VRPicture)
                __result = new Rect(0f, 0f, Size.X, Size.Y);
        }

        private static void UseGuiSize(ref int width, ref int height)
        {
            if (!StereoPatches.VRPicture)
                return;
            width = Size.X;
            height = Size.Y;
        }
    }
}
