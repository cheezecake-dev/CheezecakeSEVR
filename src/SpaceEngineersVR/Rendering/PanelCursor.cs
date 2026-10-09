using HarmonyLib;
using SpaceEngineersVR.Input;
using VRage.Input;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The mouse on the panel. The game reads the cursor in window pixels and lays its GUI out in GUI pixels
    /// (<see cref="GuiLayout"/>), but the GUI shows as a panel somewhere in each eye (<see cref="HudPanel"/>), so a
    /// click on a drawn button went to wherever that button is laid out. The cursor is read through the panel instead -
    /// from where the panel shows in the eye under the cursor, back to the GUI - and placed through it the other way.
    /// The game draws its cursor where it reads it, so the drawn cursor lands under the real one. While the laser pointer
    /// (<see cref="Laser"/>) has the cursor, it reads the GUI pixel the hand points at instead, and the game's moves of
    /// the real cursor are dropped.
    /// </summary>
    /// <remarks>
    /// Everything the game sees of the mouse is in GUI pixels: the mouse area it is told (GetMouseAreaSize) is the GUI's
    /// size, so it scales nothing, and the position is mapped into it here. Only the platform's own mouse is in window pixels.
    /// </remarks>
    internal static class PanelCursor
    {
        /// <summary>Debug: the cursor is the window stretched over the GUI, not read through the panel (<see cref="RenderDebug"/> PanelCursor=0).</summary>
        public static bool Off { get; set; }

        private static readonly AccessTools.FieldRef<MyVRageInput, float> PositionScale =
            AccessTools.FieldRefAccess<MyVRageInput, float>("m_mousePositionScale");

        private static readonly object gate = new object();
        private static readonly MyViewport[] areas = new MyViewport[2], panels = new MyViewport[2];
        private static readonly Vector2 gui = new Vector2(GuiLayout.Size.X, GuiLayout.Size.Y);
        private static int lastEye;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.GetMouseAreaSize)),
                postfix: new HarmonyMethod(typeof(PanelCursor), nameof(AreaSize)));
            harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.GetRawMousePosition)),
                postfix: new HarmonyMethod(typeof(PanelCursor), nameof(RawPosition)));
            harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.GetMousePosition)),
                postfix: new HarmonyMethod(typeof(PanelCursor), nameof(Position)));
            harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.SetMousePosition)),
                prefix: new HarmonyMethod(typeof(PanelCursor), nameof(SetPosition)));
        }

        /// <summary>
        /// Render thread, each frame the panel is shown: the parts of the window it can show in (an eye's part, or all
        /// of it), and the rectangle it showed at in each, zero size where it did not show.
        /// </summary>
        public static void Shown(MyViewport[] areaWindows, MyViewport[] panelWindows)
        {
            lock (gate)
            {
                for (int i = 0; i < 2; i++)
                {
                    areas[i] = areaWindows[i];
                    panels[i] = panelWindows[i];
                }
            }
        }

        /// <summary>
        /// Render thread: where the left eye shows in the window as of the last frame the panel was shown - its half in
        /// the desktop layout, the whole window with a headset or on a screen with no eyes (menus, loading). Zero size
        /// before the first such frame. The drive's shot saves this part, so its pixels are the ones <see cref="WindowToGui"/> reads.
        /// </summary>
        internal static MyViewport LeftArea()
        {
            lock (gate)
                return areas[0];
        }

        /// <summary>A point in the window to where it falls on the GUI, through the panel in the part of the window it is in.</summary>
        private static Vector2 ToGui(Vector2 window)
        {
            lock (gate)
            {
                int eye = Off ? -1 : AreaAt(window.X);
                if (eye < 0)
                    return Stretched(window);
                lastEye = eye;
                MyViewport panel = panels[eye];
                return new Vector2(
                    MathHelper.Clamp((window.X - panel.OffsetX) / panel.Width, 0f, 1f) * (gui.X - 1f),
                    MathHelper.Clamp((window.Y - panel.OffsetY) / panel.Height, 0f, 1f) * (gui.Y - 1f));
            }
        }

        /// <summary>A point in the window (a pixel of the mirror's back buffer) to the GUI pixel the real mouse there would point at.</summary>
        internal static Vector2 WindowToGui(Vector2 window) => ToGui(window);

        /// <summary>A point on the GUI to where it shows in the window, in the part the cursor was last in.</summary>
        private static Vector2 ToWindow(Vector2 onGui)
        {
            lock (gate)
            {
                if (Off || panels[lastEye].Width <= 0f)
                    return Unstretched(onGui);
                MyViewport panel = panels[lastEye];
                return new Vector2(panel.OffsetX + onGui.X / gui.X * panel.Width, panel.OffsetY + onGui.Y / gui.Y * panel.Height);
            }
        }

        /// <summary>The window as if it were the GUI, with no panel to read through.</summary>
        private static Vector2 Stretched(Vector2 window)
        {
            Vector2I size = EyeRenderer.WindowSize;
            return size.X > 0 && size.Y > 0 ? new Vector2(window.X / size.X * gui.X, window.Y / size.Y * gui.Y) : window;
        }

        private static Vector2 Unstretched(Vector2 onGui)
        {
            Vector2I size = EyeRenderer.WindowSize;
            return size.X > 0 && size.Y > 0 ? new Vector2(onGui.X / gui.X * size.X, onGui.Y / gui.Y * size.Y) : onGui;
        }

        private static int AreaAt(float x)
        {
            int any = -1;
            for (int i = 0; i < 2; i++)
            {
                if (areas[i].Width <= 0f || panels[i].Width <= 0f)
                    continue;
                if (x >= areas[i].OffsetX && x < areas[i].OffsetX + areas[i].Width)
                    return i;
                if (any < 0)
                    any = i;
            }
            return any;
        }

        // The game scales the cursor by (GUI size / mouse area) in MyGuiManager.GetNormalizedMousePosition; this makes it 1.
        private static void AreaSize(ref Vector2 __result) => __result = gui;

        private static void RawPosition(ref Vector2 __result) => __result = Laser.TryCursor(out Vector2 pointed) ? pointed : ToGui(__result);

        // As the game works it out (MyVRageInput.GetMousePosition), from the cursor on the GUI.
        private static void Position(MyVRageInput __instance, ref Vector2 __result)
        {
            float scale = PositionScale(__instance);
            __result = (__instance.GetRawMousePosition() - __instance.GetMouseAreaSize() / 2f * (1f - scale)) / scale;
        }

        // While the laser has the cursor the game placing it is dropped: SetMousePosition moves the real Windows cursor.
        // So it is while a test drives the game without focus (Drive): the cursor belongs to someone else then.
        private static bool SetPosition(ref int x, ref int y)
        {
            if (Laser.HoldsCursor || Drive.KeepsRealMouse)
                return false;
            Vector2 window = ToWindow(new Vector2(x, y));
            x = (int)window.X;
            y = (int)window.Y;
            return true;
        }
    }
}
