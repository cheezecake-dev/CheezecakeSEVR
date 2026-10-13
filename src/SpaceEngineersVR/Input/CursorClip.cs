using System;
using System.Reflection;
#if !LINUX
using System.Runtime.InteropServices;
#endif
using HarmonyLib;
using SpaceEngineersVR.Rendering;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The Mouse can leave the window setting: while VR runs the game does not confine the desktop mouse to its window. The game's
    /// window clips the cursor to itself on every render frame (MyGameWindow.UpdateClip: while it is active and the cursor is
    /// hidden or captured), which in VR, where play is by the controllers, only keeps the mouse off the other monitors. While the
    /// picture is VR's (<see cref="StereoPatches.VRPicture"/>) and the setting is on, that call frees the cursor instead
    /// and the game's own clip is skipped; otherwise it runs as the game has it, so turning the setting off puts the clip back on the
    /// next frame. Only the clip is released: no input is sent and the window's focus is not touched.
    /// </summary>
    /// <remarks>Windows only: the clip is the game's WinForms window's, released through user32. On Linux nothing here is patched.</remarks>
    internal static class CursorClip
    {
#if !LINUX
        // Adapted from ColbyJack1134's SpaceEngineersVR (Patches/CursorClipPatch.cs), with his permission.
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClipCursor(IntPtr rect);
#endif

        public static void Patch(Harmony harmony)
        {
#if LINUX
            Log.Info("Cursor clip: the Linux window does not clip the mouse through user32; nothing is patched");
#else
            // The window class is internal to the game's platform assembly, so it is found by name.
            Type window = AccessTools.TypeByName("VRage.Platform.Windows.Forms.MyGameWindow");
            MethodBase update = window == null ? null : AccessTools.DeclaredMethod(window, "UpdateClip");
            PatchRegistry.Hook(harmony, typeof(CursorClip), "Cursor clip", "the game keeps the mouse inside its window in VR",
                "MyGameWindow.UpdateClip", update, prefix: nameof(BeforeUpdateClip));
#endif
        }

#if !LINUX
        /// <summary>MyGameWindow.UpdateClip, every render frame and when the window's mode or mouse capture changes: false skips the game's clip.</summary>
        private static bool BeforeUpdateClip()
        {
            if (!StereoPatches.VRPicture || !VRSettings.FreeMouse)
                return true;
            ClipCursor(IntPtr.Zero);
            return false;
        }
#endif
    }
}
