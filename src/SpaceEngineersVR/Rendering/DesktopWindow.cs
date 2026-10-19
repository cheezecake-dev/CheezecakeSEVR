using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SpaceEngineersVR.OpenXR;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// With the desktop view off (<see cref="VRSettings.DesktopView"/>) the game's window is hidden while a headset shows the
    /// game, and shown again when the setting is turned back on or the headset goes. Hidden, the window takes no keyboard or
    /// mouse, so the game answers the controllers alone. Windows only; on Linux the window stays, black.
    /// </summary>
    internal static class DesktopWindow
    {
#if !LINUX
        private const int Hide = 0, ShowNoActivate = 4;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        /// <summary>The game's window, taken while it is shown (a hidden window is no process's main window).</summary>
        private static IntPtr window;

        private static bool hidden;

        /// <summary>Game thread, once a frame.</summary>
        public static void Update()
        {
            // Only while the headset shows the game: a flat game (a session that never started, a core patch that failed) keeps its window.
            bool hide = !VRSettings.DesktopView && HeadsetWatcher.HasHeadset && StereoPatches.VRPicture;
            if (window == IntPtr.Zero)
            {
                if (!hide)
                    return;
                window = Process.GetCurrentProcess().MainWindowHandle;
                if (window == IntPtr.Zero)
                    return;
            }
            if (hide && IsWindowVisible(window))
            {
                // Again whenever the game shows it (a change of resolution does)
                ShowWindow(window, Hide);
                if (!hidden)
                    Log.Info("Desktop window: hidden while the headset shows the game (Desktop view is off)");
                hidden = true;
            }
            else if (!hide && hidden)
            {
                ShowWindow(window, ShowNoActivate);
                hidden = false;
                Log.Info("Desktop window: shown again");
            }
        }
#else
        public static void Update()
        {
        }
#endif
    }
}
