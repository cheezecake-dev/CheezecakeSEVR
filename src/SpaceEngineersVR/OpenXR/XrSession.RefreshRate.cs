using System;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The headset's display refresh rate (XR_FB_display_refresh_rate): the rates the runtime offers, and the one the
    /// "Refresh rate" setting asks for. Dynamic resolution and the performance meter take the frame period from each
    /// frame's timing, so they follow a change without help.
    /// </summary>
    internal static unsafe partial class XrSession
    {
        private static Xr.EnumerateDisplayRefreshRatesFB enumerateRates;
        private static Xr.GetDisplayRefreshRateFB getRate;
        private static Xr.RequestDisplayRefreshRateFB requestRate;
        private static bool refreshLoaded, refreshListed;

        /// <summary>The session and setting value last requested, so a request is made once per session and per change.</summary>
        private static ulong requestedSession;
        private static string requestedName;

        /// <summary>
        /// Set when the session becomes visible again: a headset that reconnects to a running session (WiVRn) comes back at
        /// its own rate, sometimes a moment after it shows, so the first change away from the setting's rate is answered once.
        /// </summary>
        private static bool reassertRate;

        /// <summary>The rates the runtime offers, Hz, ascending; empty before the session runs or without the extension.</summary>
        public static float[] OfferedRefreshRates { get; private set; } = new float[0];

        /// <summary>The rate the display runs at, Hz; 0 when unknown.</summary>
        public static float RefreshRate { get; private set; }

        /// <summary>
        /// Asks the runtime for the rate the setting names; "Runtime default" asks for nothing. Called once a frame from
        /// <see cref="PollEvents"/>: it makes the request when the session first runs and again when the setting changes.
        /// </summary>
        private static void ApplyRefreshRateSetting()
        {
            string name = VRSettings.RefreshRateName;
            if (!Running || !XrRuntime.HasRefreshRate || (requestedSession == session && requestedName == name))
                return;
            requestedSession = session;
            requestedName = name;
            try
            {
                if (!LoadRefreshRate())
                    return;
                float asked = VRSettings.RefreshRateHz;
                if (asked > 0f)
                    RequestRefreshRate(asked);
                else
                    ReadRefreshRate();
            }
            catch (Exception e)
            {
                Log.Warn("OpenXR refresh rate: " + e.Message);
            }
        }

        private static bool LoadRefreshRate()
        {
            if (!refreshLoaded)
            {
                refreshLoaded = true;
                enumerateRates = Xr.GetFunction<Xr.EnumerateDisplayRefreshRatesFB>(XrRuntime.Instance, "xrEnumerateDisplayRefreshRatesFB");
                getRate = Xr.GetFunction<Xr.GetDisplayRefreshRateFB>(XrRuntime.Instance, "xrGetDisplayRefreshRateFB");
                requestRate = Xr.GetFunction<Xr.RequestDisplayRefreshRateFB>(XrRuntime.Instance, "xrRequestDisplayRefreshRateFB");
            }
            if (!refreshListed)
            {
                refreshListed = true;
                Xr.Check(enumerateRates(session, 0, out uint count, null), "xrEnumerateDisplayRefreshRatesFB", XrRuntime.Instance);
                var rates = new float[count];
                if (count > 0)
                {
                    fixed (float* p = rates)
                        Xr.Check(enumerateRates(session, count, out count, p), "xrEnumerateDisplayRefreshRatesFB", XrRuntime.Instance);
                    Array.Sort(rates);
                }
                OfferedRefreshRates = rates;
                VRSettings.OfferRefreshRates(rates);
                Log.Info("OpenXR display refresh rates offered: " + (rates.Length == 0 ? "none" : string.Join(", ", rates) + " Hz"));
            }
            return OfferedRefreshRates.Length > 0;
        }

        private static void ReadRefreshRate()
        {
            Xr.Check(getRate(session, out float rate), "xrGetDisplayRefreshRateFB", XrRuntime.Instance);
            RefreshRate = rate;
            Log.Info($"OpenXR display refresh rate: {rate} Hz (the runtime's default)");
        }

        /// <summary>Requests the offered rate nearest <paramref name="hz"/>; the runtime confirms with an event.</summary>
        public static void RequestRefreshRate(float hz)
        {
            float nearest = OfferedRefreshRates[0];
            foreach (float offered in OfferedRefreshRates)
                if (Math.Abs(offered - hz) < Math.Abs(nearest - hz))
                    nearest = offered;
            if (nearest != hz)
                Log.Warn($"OpenXR: the runtime does not offer {hz} Hz; asking for {nearest} Hz instead");
            int result = requestRate(session, nearest);
            if (result < 0)
                Log.Warn($"OpenXR: requesting {nearest} Hz failed: {Xr.ResultName(XrRuntime.Instance, result)}");
            else
                Log.Info($"OpenXR: asked for a display refresh rate of {nearest} Hz");
        }

        private static void OnRefreshRateChanged(XrEventDataDisplayRefreshRateChangedFB* changed)
        {
            RefreshRate = changed->ToDisplayRefreshRate;
            Log.Info($"OpenXR display refresh rate changed: {changed->FromDisplayRefreshRate} -> {changed->ToDisplayRefreshRate} Hz");
            float asked = VRSettings.RefreshRateHz;
            if (reassertRate && asked > 0f && Math.Abs(RefreshRate - asked) > 0.5f)
            {
                reassertRate = false;
                requestedName = null;
            }
        }

        /// <summary>The headset shows the session again, after a reconnect or taking it off: ask for the setting's rate again.</summary>
        private static void OnSessionVisibleAgain()
        {
            if (VRSettings.RefreshRateHz <= 0f)
                return;
            requestedName = null;
            reassertRate = true;
        }

        /// <summary>The test aid's answer (the drive command "refreshrate"): what the headset offers, runs at, and the setting asks.</summary>
        public static string RefreshRateReport() =>
            (XrRuntime.HasRefreshRate ? "offered " + (OfferedRefreshRates.Length == 0 ? "none yet" : string.Join(", ", OfferedRefreshRates) + " Hz") : "the runtime has no refresh-rate control") +
            $"; running at {(RefreshRate > 0f ? RefreshRate + " Hz" : "an unknown rate")}; the setting asks for {(VRSettings.RefreshRateHz > 0f ? VRSettings.RefreshRateHz + " Hz" : "the runtime default")}";

        /// <summary>
        /// A new session offers its own rates and is asked again. Its functions are looked up again too: after a lost runtime
        /// the next session is on a new instance, and the old instance's function pointers crash the game when called.
        /// </summary>
        private static void ResetRefreshRate()
        {
            refreshLoaded = false;
            enumerateRates = null;
            getRate = null;
            requestRate = null;
            refreshListed = false;
            requestedSession = 0;
            requestedName = null;
            reassertRate = false;
            RefreshRate = 0f;
            OfferedRefreshRates = new float[0];
        }
    }
}
