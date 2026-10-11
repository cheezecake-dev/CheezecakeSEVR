using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using SpaceEngineersVR.OpenXR;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Dynamic render resolution. While the GPU takes longer than the headset's frame (less headroom), the eyes are drawn
    /// smaller, down to <see cref="Floor"/> of the runtime's size each way, and back up when there is time. The runtime
    /// scales the drawn part of each eye image up (the projection layer's image rectangle,
    /// <see cref="XrSession.SetEyeRect"/>), so a smaller frame is softer, not smaller in view.
    /// </summary>
    /// <remarks>
    /// The renderer's screen resources (GBuffer, lighting, HBAO) are made at the render size by the game's own
    /// MyRender11.CreateScreenResources, the path a resolution change or a custom-size screenshot takes, before a frame
    /// is drawn (<see cref="Apply"/>). That remake costs a few milliseconds, so the scale moves in steps of
    /// <see cref="Step"/>, at most two down at once, one up at a time.
    /// <para>
    /// Each change is a visible blur or sharpening, so the scale holds unless the GPU is plainly out of time or plainly
    /// has room. Down when two seconds' GPU mean is over <see cref="DownHeadroom"/> of the frame period, or half a
    /// second's is over the whole period (frames are being missed); up only when ten seconds' mean, scaled by the pixels
    /// one step up adds, is under <see cref="UpHeadroom"/> of the period (a few seconds of a cheap view are not enough),
    /// and not within <see cref="UpHoldAfterDown"/> of a step down or <see cref="UpHoldAfterUp"/> of a step up. The gap
    /// between the two thresholds and the holds are the hysteresis: the 22:12 headset session's
    /// controller (down over 86% on half a second, up under 90% on two) changed the scale about every 8 s between 70%
    /// and 85% while the GPU's 10 s means stayed about 11 ms at all three, so each change followed the GPU time's
    /// second-to-second swings, not the load. Only while the GPU is the bottleneck: when its time is no more than the
    /// render thread's, smaller frames would not help. RenderDebug DynamicResolution=0/1 overrides the setting,
    /// RenderScale=n holds n percent.
    /// </para>
    /// </remarks>
    internal static class DynamicResolution
    {
        public const float Floor = 0.7f, Step = 0.05f;

        /// <summary>Fractions of the frame period: down over <see cref="DownHeadroom"/> (2 s) or <see cref="PanicHeadroom"/> (0.5 s), up under <see cref="UpHeadroom"/> one step up (10 s).</summary>
        private const double DownHeadroom = 0.92, PanicHeadroom = 1.0, UpHeadroom = 0.75;

        private const double SettleSeconds = 1.0, PanicSeconds = 0.5, DownSeconds = 2.0, UpSeconds = 10.0;

        /// <summary>No step up for this long after a step down (back to the scale just left), or after a step up, s.</summary>
        private const double UpHoldAfterDown = 60.0, UpHoldAfterUp = 20.0;

        /// <summary>RenderDebug DynamicResolution=0/1: overrides the setting.</summary>
        public static bool? Debug;

        /// <summary>RenderDebug RenderScale=n: the scale held at n percent, with or without a headset.</summary>
        public static float? FixedScale;

        public static bool On => Debug ?? VRSettings.DynamicResolution;

        private static MethodInfo createScreenResources;
        private static bool failed;

        /// <summary>The scale the screen resources are at now, and the one the controller wants (render thread).</summary>
        private static float scale = 1f, wanted = 1f;

        /// <summary>The scale the eyes are drawn at (<see cref="EyeRenderer.RenderResolution"/>); the new one already during <see cref="Apply"/>.</summary>
        public static float Scale => scale;

        /// <summary>True while <see cref="Apply"/> remakes the screen resources.</summary>
        public static bool Resizing { get; private set; }

        /// <summary>GPU and render-thread time summed over one decision window, from its first sample.</summary>
        private struct Window
        {
            public double Gpu, Cpu;
            public int Count;
            public long Since;

            public void Add(double gpuMs, double cpuMs, long now)
            {
                if (Count == 0)
                {
                    Since = now;
                    Gpu = Cpu = 0;
                }
                Gpu += gpuMs;
                Cpu += cpuMs;
                Count++;
            }

            public double GpuMean => Gpu / Count;
            public double CpuMean => Cpu / Count;

            /// <summary>The GPU is the bottleneck: smaller frames would help.</summary>
            public bool GpuBound => GpuMean > CpuMean * 1.15 + 0.5;
        }

        // The samples since each window's last decision.
        private static Window panic, down, up;
        private static long changedAt;
        private static bool changedDown;
        private static string reason = "";

        private static void ResetWindows() => panic.Count = down.Count = up.Count = 0;

        // For the Perf line: this window's changes.
        private static int windowChanges;
        private static double windowWorst;
        private static float windowLow = 1f, windowHigh = 1f;

        private static double Seconds(long from, long to) => (to - from) / (double)Stopwatch.Frequency;

        /// <summary>Eye images of <paramref name="full"/> size drawn at <see cref="Scale"/>: multiples of 8, the full size at 100%.</summary>
        public static Vector2I Size(Vector2I full)
        {
            float s = scale;
            if (s >= 0.999f)
                return full;
            int w = Math.Max(8, (int)Math.Round(full.X * s / 8.0) * 8);
            int h = Math.Max(8, (int)Math.Round(full.Y * s / 8.0) * 8);
            return new Vector2I(Math.Min(w, full.X), Math.Min(h, full.Y));
        }

        /// <summary>The scale wanted now: the held one, the controller's with a headset, otherwise 100%.</summary>
        private static float Wanted()
        {
            if (FixedScale.HasValue)
                return FixedScale.Value;
            return On && XrSession.Exists ? wanted : 1f;
        }

        /// <summary>
        /// Render thread, before a frame's eyes are drawn: remakes the screen resources at the wanted scale when it
        /// changed. Each change is logged with the time the remake took.
        /// </summary>
        public static void Apply()
        {
            if (failed)
                return;
            float want = Wanted();
            if (Math.Abs(want - scale) < 0.001f)
                return;
            float from = scale;
            Vector2I fromSize = EyeRenderer.RenderResolution;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (createScreenResources == null)
                    createScreenResources = AccessTools.Method(typeof(MyDX11Render).Assembly.GetType("VRageRender.MyRender11", throwOnError: true), "CreateScreenResources")
                                            ?? throw new MissingMethodException("MyRender11", "CreateScreenResources");
                scale = want;
                Resizing = true;
                createScreenResources.Invoke(null, null);
            }
            catch (Exception e)
            {
                failed = true;
                scale = from;
                Log.Error(e, "Dynamic resolution: could not remake the screen resources; the eyes stay at their size for the rest of the run");
                return;
            }
            finally
            {
                Resizing = false;
            }
            long now = Stopwatch.GetTimestamp();
            double ms = Seconds(t0, now) * 1000.0;
            changedAt = now;
            changedDown = scale < from;
            ResetWindows();
            windowChanges++;
            windowWorst = Math.Max(windowWorst, ms);
            windowLow = Math.Min(windowLow, scale);
            windowHigh = Math.Max(windowHigh, scale);

            // Every change is logged: there is at most one a SettleSeconds + DownSeconds.
            Vector2I size = EyeRenderer.RenderResolution;
            string why = FixedScale.HasValue ? "RenderScale" : !On || !XrSession.Exists ? "dynamic resolution off" : reason;
            Log.Info(string.Format(CultureInfo.InvariantCulture,
                "Dynamic resolution: {0:0}% -> {1:0}% of the eye size, {2}x{3} -> {4}x{5} ({6}); screen resources remade in {7:0.0} ms",
                from * 100f, scale * 100f, fromSize.X, fromSize.Y, size.X, size.Y, why, ms));
        }

        /// <summary>
        /// A frame's GPU time (read back two or three frames late), the render thread's time in it and the headset's
        /// period. Moves <see cref="wanted"/>; <see cref="Apply"/> carries it out before the next frame.
        /// </summary>
        public static void Sample(double gpuMs, double cpuMs, double periodMs, bool headsetFrame)
        {
            // The death view's frames draw no eyes, only the small external view pass: their cheapness says nothing about
            // what the eyes can afford when the player is back.
            if (!headsetFrame || !XrSession.Exists || FixedScale.HasValue || !On || failed || periodMs <= 0 || DeathView.Drawing)
            {
                ResetWindows();
                return;
            }
            long now = Stopwatch.GetTimestamp();
            if (changedAt != 0 && Seconds(changedAt, now) < SettleSeconds)
                return; // frames drawn before the change, and the change's own hitch
            panic.Add(gpuMs, cpuMs, now);
            down.Add(gpuMs, cpuMs, now);
            up.Add(gpuMs, cpuMs, now);

            // Down: half a second over the whole period (frames are being missed), or two seconds over DownHeadroom of it.
            if (Seconds(panic.Since, now) >= PanicSeconds)
            {
                if (Down(ref panic, periodMs * PanicHeadroom, now))
                    return;
                panic.Count = 0;
            }
            if (Seconds(down.Since, now) >= DownSeconds)
            {
                if (Down(ref down, periodMs * DownHeadroom, now))
                    return;
                down.Count = 0;
            }

            // Up: ten seconds with room for a step under UpHeadroom of the period, and not within the hold after a change.
            double elapsed = Seconds(up.Since, now);
            if (elapsed < UpSeconds)
                return;
            bool held = changedAt != 0 && Seconds(changedAt, now) < (changedDown ? UpHoldAfterDown : UpHoldAfterUp);
            if (!held && wanted < 1f - 0.001f)
            {
                float next = Math.Min(1f, wanted + Step);
                double ratio = next / wanted;
                double gpu = up.GpuMean, predicted = gpu * ratio * ratio, target = periodMs * UpHeadroom;
                if (predicted < target)
                {
                    reason = string.Format(CultureInfo.InvariantCulture, "GPU {0:0.0} ms over {1:0.0} s, {2:0.0} expected one step up, against {3:0.0}",
                        gpu, elapsed, predicted, target);
                    wanted = next;
                }
            }
            up.Count = 0;
        }

        /// <summary>Steps <see cref="wanted"/> down when <paramref name="w"/>'s GPU mean is over <paramref name="target"/> and the GPU is the bottleneck.</summary>
        private static bool Down(ref Window w, double target, long now)
        {
            double gpu = w.GpuMean;
            if (gpu <= target || !w.GpuBound || wanted <= Floor + 0.001f)
                return false;
            // The pixel work goes with the square of the scale; the rest (shadows, setup) does not, so this undershoots and the next decision finishes.
            double need = wanted * Math.Sqrt(target / gpu);
            float next = (float)(Math.Floor(need / Step + 1e-3) * Step);
            next = Math.Max(Floor, Math.Max(wanted - 2f * Step, Math.Min(next, wanted - Step)));
            reason = string.Format(CultureInfo.InvariantCulture, "GPU {0:0.0} ms over {1:0.0} s against {2:0.0}, render thread {3:0.0}",
                gpu, Seconds(w.Since, now), target, w.CpuMean);
            wanted = next;
            ResetWindows();
            return true;
        }

        /// <summary>For the Perf line: "render scale 85% 1752x1928 (dynamic, 2 changes 80-90%, slowest remake 14.2 ms)". Starts the next window.</summary>
        public static string Describe()
        {
            Vector2I size = EyeRenderer.RenderResolution;
            string mode = FixedScale.HasValue ? "held" : !On ? "dynamic off" : !XrSession.Exists ? "no headset" : failed ? "failed" : "dynamic";
            string text = string.Format(CultureInfo.InvariantCulture, "render scale {0:0}% {1}x{2} ({3}", scale * 100f, size.X, size.Y, mode);
            if (windowChanges > 0)
                text += string.Format(CultureInfo.InvariantCulture, ", {0} change(s) {1:0}-{2:0}%, slowest remake {3:0.0} ms",
                    windowChanges, windowLow * 100f, windowHigh * 100f, windowWorst);
            windowChanges = 0;
            windowWorst = 0;
            windowLow = windowHigh = scale;
            return text + ")";
        }
    }
}
