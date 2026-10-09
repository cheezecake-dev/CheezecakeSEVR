using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Rendering;

namespace SpaceEngineersVR
{
    /// <summary>
    /// One-time setup. <see cref="PreGameInit(string[])"/> runs before the game's Main, the only point early enough to
    /// influence render device creation (adapter choice, stereo flag). Two hosts call it there:
    /// SpaceEngineersVR.Launcher, and Pulsar through <see cref="global::Preloader.Finish"/>.
    /// </summary>
    public static class Bootstrap
    {
        private static bool initialized;

        internal static Harmony Harmony { get; private set; }

        /// <summary>True when we were set up before the game created its render device.</summary>
        internal static bool StartedBeforeGame { get; private set; }

        /// <summary>
        /// The folder openxr_loader.dll is loaded from: the plugin assembly's own folder. That is Bin64\SpaceEngineersVR
        /// under the launcher, and Pulsar's compiled-plugin Bin folder under Pulsar, where the plugin's asset with
        /// Placement="Bin" is copied (SpaceEngineersVR.xml) or extracted (x64\bin\ in it: <see cref="XrRuntime.FindLoader"/>).
        /// </summary>
        internal static string NativeDir => Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location);

        /// <summary>Called by SpaceEngineersVR.Launcher (by reflection) before it starts the game.</summary>
        public static void PreGameInit(string[] args) => Initialize(args, beforeGame: true, "SpaceEngineersVR.Launcher");

        internal static void PreGameInit(string[] args, string host) => Initialize(args, beforeGame: true, host);

        internal static void Initialize(string[] args, bool beforeGame, string host)
        {
            if (initialized)
                return;
            initialized = true;
            StartedBeforeGame = beforeGame;

            Log.Init();
            Log.Info($"SpaceEngineersVR {typeof(Bootstrap).Assembly.GetName().Version} starting via {host} ({(beforeGame ? "before" : "after")} the game started)");
            Log.Info("Plugin assembly: " + typeof(Bootstrap).Assembly.Location);
            Log.Info("Args: " + string.Join(" ", args ?? Array.Empty<string>()));
            Input.FocusReturn.Start();

            VRSettings.Load(args);
            if (!VRSettings.Enabled)
            {
                Log.Info("VR disabled (-novr)");
                return;
            }

            if (!beforeGame)
                Log.Warn("Loaded after the render device was created; stereo rendering needs SpaceEngineersVR.Launcher.exe or Pulsar's Preloader hook");
            else
                FindHeadset();

            try
            {
                Harmony = new Harmony("SpaceEngineersVR");
                StereoPatches.Apply(Harmony);
                Log.Info("Patched: " + string.Join(", ", Harmony.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name)));
            }
            catch (Exception e)
            {
                // The game must still start; VR is simply off for this run.
                Log.Error(e, "VR setup failed; continuing without VR");
                Harmony?.UnpatchAll(Harmony.Id);
            }
        }

        /// <summary>
        /// Before the game picks its GPU: the headset runtime says which one the session has to be on. Without a
        /// headset (or without OpenXR) the game runs with both eyes side by side in the window.
        /// </summary>
        private static void FindHeadset()
        {
            try
            {
                if (XrRuntime.CreateInstance(NativeDir) && !XrRuntime.TryGetSystem())
                    Log.Info("No headset connected (connect it before starting the game); both eyes go side by side in the window");
            }
            catch (Exception e)
            {
                Log.Error(e, "OpenXR is not usable; both eyes go side by side in the window");
            }
        }
    }
}
