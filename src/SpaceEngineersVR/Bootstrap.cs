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

            UserFiles.CopyFromOldPlace(VRSettings.FileName);
            VRSettings.Load(args);
            if (!VRSettings.Enabled)
            {
                Log.Info("VR disabled (-novr)");
                return;
            }

            if (!beforeGame)
            {
                // Nothing looks for the headset then, so none will be found: patch nothing.
                Log.Warn("Loaded after the render device was created; VR needs SpaceEngineersVR.Launcher.exe or Pulsar's Preloader hook. Nothing is patched");
                return;
            }
            FindHeadset();
            if (HeadsetWatcher.Unusable)
            {
                Log.Info("OpenXR is not usable on this machine, so no headset can be found; nothing is patched and the game runs as usual");
                return;
            }

            try
            {
                Harmony = new Harmony("SpaceEngineersVR");
                if (PatchRegistry.Apply(nameof(StereoPatches), () => StereoPatches.Apply(Harmony)))
                    Log.Info("Patched: " + string.Join(", ", Harmony.GetPatchedMethods().Select(m => m.DeclaringType?.Name + "." + m.Name)));
                else
                    // A core feature: VR is off for this run, and the main menu says why.
                    PatchRegistry.Finish(null);
            }
            catch (Exception e)
            {
                // The game must still start; VR is simply off for this run.
                Log.Error(e, "VR setup failed; continuing without VR");
                Harmony?.UnpatchAll(Harmony.Id);
            }
        }

        /// <summary>
        /// Before the game picks its GPU: the headset runtime says which one the session has to be on, so its first answer is
        /// waited for (<see cref="HeadsetWatcher"/>). Without a headset the game starts with its own picture, and VR starts
        /// when the headset connects; the picture turns to VR's and what changes play is patched only then
        /// (<see cref="StereoPatches.PatchForHeadset"/>). Without a usable OpenXR nothing is patched.
        /// </summary>
        private static void FindHeadset()
        {
#if LINUX
            // WiVRn's xrCreateInstance blocks until a headset connects, so it must not hold the game up for ever.
            const int waitMs = 15000;
#else
            const int waitMs = System.Threading.Timeout.Infinite;
#endif
            if (!HeadsetWatcher.Start(NativeDir, waitMs))
                Log.Warn($"No OpenXR answer from the runtime after {waitMs / 1000} s (WiVRn waits for the headset to connect); " +
                         "starting with the game's own picture, and VR starts when the headset connects");
        }
    }
}
