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
    internal static class GuiLayout
    {
        /// <summary>The panel's resolution, and the size the GUI is laid out for.</summary>
        public static readonly Vector2I Size = new Vector2I(1920, 1080);

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MySandboxGame), nameof(MySandboxGame.UpdateScreenSize)),
                prefix: new HarmonyMethod(typeof(GuiLayout), nameof(UseGuiSize)));
        }

        private static void UseGuiSize(ref int width, ref int height)
        {
            width = Size.X;
            height = Size.Y;
        }
    }
}
