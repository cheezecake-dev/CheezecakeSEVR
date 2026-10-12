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
    public static partial class Bootstrap
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

            if (beforeGame)
                FindHeadset();
            else
                Log.Warn("Loaded after the render device was created; VR needs SpaceEngineersVR.Launcher.exe or Pulsar's Preloader hook");

            bool vr = XrRuntime.HasSystem;
            if (!vr)
                VRWithoutHeadset(ref vr);
            if (!vr)
            {
                // Patches change what the player can do (recoil, camera, controls), so none go on unless VR is in use.
                Log.Info("No headset found: nothing is patched and the game runs as usual");
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

        /// <summary>A test build's switch for VR without a headset (src/SpaceEngineersVR.TestAids/); a player's build has none.</summary>
        static partial void VRWithoutHeadset(ref bool on);

#if LINUX
        private const int RuntimeWaitMs = 15000;

        // Who owns the result of the instance lookup: the lookup once the instance exists, or the game once it stopped
        // waiting. Whichever claims it first wins, so VR never starts half way into a run.
        private const int Waiting = 0, Found = 1, Abandoned = 2;
        private static int lookup = Waiting;

        /// <summary>
        /// Runs <paramref name="work"/> on its own thread and rethrows what it throws. After <paramref name="timeoutMs"/>
        /// asks <paramref name="giveUp"/>: true leaves the thread to end on its own and returns false; false means the work
        /// is past the point of no return, so it is waited for.
        /// </summary>
        private static bool RunBounded(Action work, int timeoutMs, Func<bool> giveUp)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { work(); }
                catch (Exception e) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            }) { IsBackground = true, Name = "SEVR OpenXR instance" };
            thread.Start();
            if (!thread.Join(timeoutMs))
            {
                if (giveUp())
                    return false;
                thread.Join();
            }
            failure?.Throw();
            return true;
        }
#endif

        private static void FindHeadsetBlocking()
        {
            if (!XrRuntime.CreateInstance(NativeDir))
                return;
#if LINUX
            if (System.Threading.Interlocked.CompareExchange(ref lookup, Found, Waiting) != Waiting)
                return;
#endif
            if (!XrRuntime.TryGetSystem())
                Log.Info("No headset connected (connect it before starting the game)");
        }

        /// <summary>
        /// Before the game picks its GPU: the headset runtime says which one the session has to be on. Without a
        /// headset (or without OpenXR) nothing is patched and the game runs as usual.
        /// </summary>
        private static void FindHeadset()
        {
            try
            {
#if LINUX
                // WiVRn's xrCreateInstance blocks until a headset connects, so it must not hold the game up for ever.
                if (!RunBounded(FindHeadsetBlocking, RuntimeWaitMs,
                        () => System.Threading.Interlocked.CompareExchange(ref lookup, Abandoned, Waiting) == Waiting))
                {
                    Log.Warn($"No OpenXR answer from the runtime after {RuntimeWaitMs / 1000} s (WiVRn waits for the headset to connect); " +
                             "starting without VR. Connect the headset before starting the game");
                }
#else
                FindHeadsetBlocking();
#endif
            }
            catch (XrException e) when (e.Result == XrResult.ErrorRuntimeUnavailable)
            {
                // The runtime's loader is there but nothing answers it; a stack trace would only bury that.
                Log.Warn("The OpenXR runtime is not running" +
#if LINUX
                         " (start the WiVRn server, connect the headset, then start the game)" +
#endif
                         "; starting without VR");
            }
            catch (Exception e)
            {
                Log.Error(e, "OpenXR is not usable; starting without VR");
            }
        }
    }
}
