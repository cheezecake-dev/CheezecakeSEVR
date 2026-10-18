using System;
using System.Threading;
using HarmonyLib;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game asks Steam whether the player is logged on (MySteamService.IsOnline, which calls SteamUser.BLoggedOn) from
    /// its main thread every frame, through the HUD's performance warnings (MyGuiScreenHudSpace.UpdatePerfWarnings). Steam's
    /// client now and then takes 0.3 to 1.5 s to answer, and the whole game waits on it: measured with dotnet-trace on
    /// 2026-10-17, every main-thread stall of 250 ms or more in a minute of standing still had that one stack. In a headset a
    /// held main thread is a world that stops moving. A thread of our own asks Steam once a second and the game reads its
    /// last answer, so the game sees the same value at most a second late and never waits for Steam.
    /// Only while VR draws the picture (<see cref="StereoPatches.VRPicture"/>): played flat, the game asks Steam itself as it
    /// always does, and the thread is not started until VR first draws the picture, nor asks Steam while it does not.
    /// </summary>
    internal static class SteamOnlineCache
    {
        private const int RefreshMs = 1000;

        private static volatile int known; // 0 not yet asked, 1 offline, 2 online
        private static Func<bool> ask;

        [ThreadStatic]
        private static bool asking;

        private static int started; // 1 once the thread is running

        public static void Patch(Harmony harmony)
        {
            Type service = AccessTools.TypeByName("VRage.Steam.MySteamService");
            if (service == null)
            {
                Log.Info("Steam online check: no Steam service in this game (another platform); nothing to do");
                return;
            }
            var getter = AccessTools.PropertyGetter(service, "IsOnline") ?? throw new MissingMethodException("MySteamService.IsOnline");
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(SteamOnlineCache), nameof(Cached)));

            // The original, on our thread: the prefix lets this thread's own calls through to Steam.
            var serviceOf = AccessTools.PropertyGetter(AccessTools.TypeByName("Sandbox.Engine.Networking.MyGameService"), "Service")
                ?? throw new MissingMethodException("MyGameService.Service");
            ask = () =>
            {
                object instance = serviceOf.Invoke(null, null);
                if (instance == null || !service.IsInstanceOfType(instance))
                    throw new InvalidOperationException("the game's service is not Steam's");
                return (bool)getter.Invoke(instance, null);
            };
        }

        private static bool Cached(ref bool __result)
        {
            if (asking || !StereoPatches.VRPicture)
                return true;
            if (started == 0 && Interlocked.Exchange(ref started, 1) == 0)
            {
                new Thread(Refresh) { IsBackground = true, Name = "SEVR Steam online check" }.Start();
                Log.Info("Steam online check: asked of Steam once a second off the game's thread while VR draws the picture; the game reads the last answer (it waited on Steam up to 1.5 s a frame)");
            }
            int value = known;
            if (value == 0)
                return true;
            __result = value == 2;
            return false;
        }

        private static void Refresh()
        {
            asking = true;
            long slowest = 0;
            while (true)
            {
                if (!StereoPatches.VRPicture)
                {
                    known = 0;
                    Thread.Sleep(RefreshMs);
                    continue;
                }
                try
                {
                    long start = Environment.TickCount;
                    bool online = ask();
                    long took = Environment.TickCount - start;
                    int value = online ? 2 : 1;
                    if (value != known)
                        Log.Info($"Steam online check: Steam says the player is {(online ? "online" : "offline")}");
                    known = value;
                    if (took >= 250 && took > slowest)
                    {
                        slowest = took;
                        Log.Info($"Steam online check: Steam took {took} ms to answer, off the game's thread (the slowest yet)");
                    }
                }
                catch (Exception e)
                {
                    Log.Info($"Steam online check: asking Steam failed ({e.GetType().Name}: {e.Message}); the game asks it itself again");
                    known = 0;
                    return;
                }
                Thread.Sleep(RefreshMs);
            }
        }
    }
}
