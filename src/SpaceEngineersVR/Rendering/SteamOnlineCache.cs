using System;
using HarmonyLib;
using SpaceEngineersVR.Common;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game asks Steam whether the player is logged on (MySteamService.IsOnline, which calls SteamUser.BLoggedOn) from
    /// its main thread every frame, through the HUD's performance warnings (MyGuiScreenHudSpace.UpdatePerfWarnings). Steam's
    /// client now and then takes 0.3 to 1.5 s to answer, and the whole game waits on it: measured with dotnet-trace on
    /// 2026-10-17, every main-thread stall of 250 ms or more in a minute of standing still had that one stack. In a headset a
    /// held main thread is a world that stops moving. A thread of our own asks Steam every ten seconds and the game reads its
    /// last answer, so the game sees the same value at most ten seconds late and never waits for Steam. Not more often: while
    /// our slow call is in Steam's client, the game's own Steam calls (voice chat, push to talk) wait behind it on the main
    /// thread (run M: two 1 s main-thread ticks ended in the same millisecond as our 672 and 1015 ms answers), and the only
    /// reader is the HUD's offline warning.
    /// Only while VR draws the picture (<see cref="StereoPatches.VRPicture"/>): played flat, the game asks Steam itself as it
    /// always does. The thread starts when VR first draws the picture and asks Steam; when VR stops drawing it
    /// (<see cref="DemandThread"/>) it ends, so a game with no headset has no thread of ours and makes no call to Steam.
    /// </summary>
    internal static class SteamOnlineCache
    {
        private const int RefreshMs = 10000;

        private static volatile int known; // 0 not yet asked, 1 offline, 2 online
        private static Func<bool> ask;
        private static readonly DemandThread Worker = new DemandThread("SEVR Steam online check", RefreshMs,
            () => StereoPatches.VRPicture, Refresh, () => known = 0, Failed);

        [ThreadStatic]
        private static bool asking;

        private static long slowest;

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
            if (Worker.Ensure())
                Log.Info("Steam online check: asked of Steam every 10 s off the game's thread while VR draws the picture; the game reads the last answer (it waited on Steam up to 1.5 s a frame)");
            int value = known;
            if (value == 0)
                return true;
            __result = value == 2;
            return false;
        }

        // On the worker's thread, once a round.
        private static void Refresh()
        {
            asking = true;
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

        private static void Failed(Exception e)
        {
            Log.Info($"Steam online check: asking Steam failed ({e.GetType().Name}: {e.Message}); the game asks it itself again");
            known = 0;
        }
    }
}
