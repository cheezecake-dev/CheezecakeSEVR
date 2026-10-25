using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Engine.Utils;
using VRage;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Keeps the game on the monitor it was set to. The game saves its monitor as a place in its list of monitors
    /// (SpaceEngineers.cfg VideoAdapter, an index into MyPlatformRender's adapter list, one entry per graphics card and
    /// display), and that list's order can change between runs, as Windows lists them. The game then opens on another monitor
    /// with the first one's resolution: on the wrong screen, or minimized when that monitor has no such mode in fullscreen.
    /// </summary>
    /// <remarks>
    /// Each time the game writes its video settings to the config (MyVideoSettingsManager.WriteCurrentSettingsToConfig) the
    /// name of the monitor it saved is kept (<see cref="VRSettings.Monitor"/>), and before the game reads the config at start
    /// (MyVideoSettingsManager.Initialize) the config's VideoAdapter is pointed back at the entry with that name. Before a name
    /// is kept, the first start with the plugin keeps the one the game opens on. Off with <see cref="VRSettings.KeepMonitor"/>.
    /// The game's list is read the way the game reads it (MyVRage.Platform.Render.GetRenderAdapterList), before the render
    /// thread starts.
    /// </remarks>
    internal static class MonitorChoice
    {
        public static void Hook(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.Initialize)),
                prefix: new HarmonyMethod(typeof(MonitorChoice), nameof(PointAtKeptMonitor)));
            harmony.Patch(AccessTools.Method(typeof(MyVideoSettingsManager), nameof(MyVideoSettingsManager.WriteCurrentSettingsToConfig)),
                postfix: new HarmonyMethod(typeof(MonitorChoice), nameof(KeepMonitor)));
        }

        private static void PointAtKeptMonitor()
        {
            if (!VRSettings.KeepMonitor)
                return;
            try
            {
                MyConfig config = MySandboxGame.Config;
                MyAdapterInfo[] list = MyVRage.Platform.Render.GetRenderAdapterList();
                if (config == null || list == null || list.Length == 0)
                    return;
                int saved = config.VideoAdapter;
                string kept = VRSettings.Monitor;
                if (string.IsNullOrEmpty(kept))
                {
                    Remember(list, saved);
                    return;
                }
                string savedName = saved >= 0 && saved < list.Length ? list[saved].Name : "not in the list";
                if (savedName == kept)
                    return;
                int wanted = Find(list, kept);
                if (wanted < 0)
                {
                    Log.Info($"Monitor: {kept} is not connected; the game opens on its saved monitor {saved} ({savedName})");
                    return;
                }
                config.VideoAdapter = wanted;
                Log.Info($"Monitor: the game's list of monitors changed order; the game opens on {wanted} ({kept}) again, not {saved} ({savedName})");
            }
            catch (Exception e)
            {
                Log.Error(e, "Monitor: could not check which monitor the game opens on; it opens on the one it saved");
            }
        }

        private static void KeepMonitor()
        {
            if (!VRSettings.KeepMonitor)
                return;
            try
            {
                MyConfig config = MySandboxGame.Config;
                if (config != null)
                    Remember(MyVRage.Platform.Render.GetRenderAdapterList(), config.VideoAdapter);
            }
            catch (Exception e)
            {
                Log.Error(e, "Monitor: could not keep the game's monitor");
            }
        }

        private static void Remember(MyAdapterInfo[] list, int index)
        {
            if (list == null || index < 0 || index >= list.Length || string.IsNullOrEmpty(list[index].Name) || list[index].Name == VRSettings.Monitor)
                return;
            VRSettings.Monitor = list[index].Name;
            VRSettings.Save();
            Log.Info($"Monitor: the game's monitor is {list[index].Name} (its list's entry {index})");
        }

        /// <summary>The first entry with this name that can run the game; -1 when the monitor is not connected.</summary>
        private static int Find(MyAdapterInfo[] list, string name)
        {
            int found = -1;
            for (int i = list.Length - 1; i >= 0; i--)
                if (list[i].Name == name && (found < 0 || list[i].IsDx11Supported || !list[found].IsDx11Supported))
                    found = i;
            return found;
        }
    }
}
