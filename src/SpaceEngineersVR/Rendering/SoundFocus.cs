using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.OpenXR;
using VRage;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Keeps the game's sound on while the player is in the headset. With EnableMuteWhenNotInFocus (a config
    /// setting, on in many installs) <c>MySandboxGame.UpdateSound</c> sets the music, game, HUD and voice volumes to 0 whenever the
    /// desktop window is not the foreground window, and restores them from the config when it is again. In VR the mirror window is
    /// often not the foreground window (the player is typing somewhere else, or Windows refused the game its focus at start-up),
    /// so the game went silent in the headset.
    /// </summary>
    /// <remarks>
    /// UpdateSound reads <c>form.IsActive</c> once (the game window, <see cref="IVRageWindow"/>); that one call is rewritten to
    /// <see cref="WindowActive"/>, which says "active" while the headset session runs. Everything else is the game's own code, so a
    /// game already muted gets its volumes back from its own restore branch (hasFocus is false, the window now reads active), and
    /// flat play, or the mirror before the session starts, behaves as before. The window's IsActive itself is not changed: input,
    /// the cursor and the focus hand-back read it too and mean the real window by it.
    /// </remarks>
    internal static class SoundFocus
    {
        /// <summary>Debug: the game's own mute stands even in VR (<see cref="RenderDebug"/> FocusMute=1).</summary>
        public static bool GameMute;

        private static bool logged;

        public static void Patch(Harmony harmony)
        {
            // Sound is not worth the rest of VR: a game update that moves UpdateSound only costs this hook.
            try
            {
                MethodInfo updateSound = AccessTools.Method(typeof(MySandboxGame), "UpdateSound")
                                         ?? throw new MissingMemberException(nameof(MySandboxGame), "UpdateSound");
                harmony.Patch(updateSound, transpiler: new HarmonyMethod(typeof(SoundFocus), nameof(TranspileUpdateSound)));
                Log.Info("Sound: the game's mute when its window is in the background is lifted while the headset is in use (FocusMute=1 restores it)");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not hook the game's sound mute; the game may go silent while its window is in the background");
            }
        }

        private static IEnumerable<CodeInstruction> TranspileUpdateSound(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            return HandTools.Redirect(instructions, IsWindowActive, AccessTools.Method(typeof(SoundFocus), nameof(WindowActive)), 1, ref count);
        }

        private static bool IsWindowActive(MethodBase method) =>
            method.DeclaringType == typeof(IVRageWindow) && method.Name == "get_" + nameof(IVRageWindow.IsActive);

        /// <summary>What UpdateSound sees as the game window's IsActive: true while the headset session runs.</summary>
        public static bool WindowActive(IVRageWindow window)
        {
            bool active = window.IsActive;
            if (active || GameMute || !XrSession.Running)
                return active;

            if (!logged)
            {
                logged = true;
                Log.Info("Sound: kept on while the game window is in the background (headset in use)");
            }
            return true;
        }
    }
}
