using System;
using System.Threading;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// Looks for the headset for the whole run, on its own thread: makes the OpenXR instance, then asks for the headset
    /// every <see cref="RetryMs"/> until it is there. The render thread starts a session once it is
    /// (<see cref="Rendering.EyeRenderer"/>), and after a loss hands the headset, or the whole instance, back here
    /// (<see cref="Forget"/>) to be looked for again.
    /// </summary>
    /// <remarks>
    /// Only this thread creates and destroys the instance and looks for the system; only the render thread creates and
    /// destroys sessions, and it never waits for this one. That keeps WiVRn's xrCreateInstance, which blocks until a
    /// headset connects, and a runtime that is slow to answer while the headset comes and goes, off the render thread.
    /// </remarks>
    internal static class HeadsetWatcher
    {
        private const int RetryMs = 2000;

        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static readonly ManualResetEventSlim answered = new ManualResetEventSlim(false);

        /// <summary>Set by the render thread when the instance has to go; this thread destroys it and makes a new one.</summary>
        private static volatile bool instanceLost;

        /// <summary>The headset is there and nothing is waiting to be torn down: a session can be started on it.</summary>
        public static bool HasHeadset => XrRuntime.HasSystem && !instanceLost;

        /// <summary>OpenXR cannot be used on this machine (no loader, no graphics extension we can use): no headset will be found this run.</summary>
        public static bool Unusable => unusable;

        private static volatile bool unusable;

        /// <summary>
        /// Starts looking, and waits up to <paramref name="timeoutMs"/> for the first answer: the headset found or not, or
        /// OpenXR unusable (each logged). False when there was no answer in that time; the lookup goes on regardless.
        /// </summary>
        public static bool Start(string pluginDir, int timeoutMs)
        {
            new Thread(() => Run(pluginDir)) { IsBackground = true, Name = "SEVR headset watcher", Priority = ThreadPriority.BelowNormal }.Start();
            return answered.Wait(timeoutMs);
        }

        /// <summary>
        /// Render thread, once its session is destroyed: the headset is looked for again; with <paramref name="instance"/>
        /// (the instance was lost, or the runtime failed) a new instance is made first.
        /// </summary>
        public static void Forget(bool instance)
        {
            if (instance)
                instanceLost = true; // before the system goes, so HasHeadset never sees the old instance without it
            XrRuntime.ForgetSystem();
            wake.Set();
        }

        private static void Run(string pluginDir)
        {
            bool absentLogged = false, failureLogged = false;
            while (!PatchRegistry.VROff)
            {
                try
                {
                    if (instanceLost)
                    {
                        XrRuntime.DestroyInstance();
                        instanceLost = false;
                    }
                    if (XrRuntime.Instance == 0 && !XrRuntime.CreateInstance(pluginDir))
                    {
                        unusable = true; // logged: no loader, or no graphics extension we can use
                        return;
                    }
                    if (XrRuntime.TryGetSystem())
                    {
                        absentLogged = false;
                    }
                    else if (!absentLogged)
                    {
                        absentLogged = true;
                        Log.Info("No headset connected; VR starts when one connects");
                    }
                    failureLogged = false;
                }
                catch (XrException e) when (e.Result == XrResult.ErrorRuntimeUnavailable || e.Result == XrResult.ErrorRuntimeFailure
                                            || e.Result == XrResult.ErrorInstanceLost)
                {
                    if (e.Result != XrResult.ErrorRuntimeUnavailable)
                        XrRuntime.DestroyInstance();
                    if (!failureLogged)
                    {
                        failureLogged = true;
                        // The runtime's loader is there but nothing answers it; a stack trace would only bury that.
                        Log.Warn(e.Result == XrResult.ErrorRuntimeUnavailable
                            ? "The OpenXR runtime is not running" +
#if LINUX
                              " (start the WiVRn server)" +
#endif
                              "; VR starts when it is running and the headset connects"
                            : $"The OpenXR runtime failed ({e.Message}); trying again every {RetryMs / 1000} s");
                    }
                }
                catch (Exception e)
                {
                    Log.Error(e, "OpenXR is not usable; the game runs as usual for this run");
                    unusable = true;
                    return;
                }
                finally
                {
                    answered.Set();
                }
                wake.WaitOne(XrRuntime.HasSystem ? Timeout.Infinite : RetryMs);
            }
        }
    }
}
