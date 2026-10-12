using System;
using System.Diagnostics;
using System.Threading;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The session's frame loop while the picture is the game's (<see cref="Rendering.StereoPatches.PlayingFlat"/>): xrWaitFrame,
    /// xrBeginFrame and xrEndFrame with no layers, on a thread of their own.
    /// </summary>
    /// <remarks>
    /// The OpenXR specification asks for the loop in the states the session goes through while the headset is away and
    /// comes back: in SYNCHRONIZED "the application should continue its frame loop by calling xrWaitFrame, xrBeginFrame and
    /// xrEndFrame, but should avoid heavy GPU work", and it "can save resources here by skipping rendering and not
    /// submitting any composition layers until xrWaitFrame returns an XrFrameState with shouldRender set to true". A
    /// runtime throttles xrWaitFrame for a session that is not shown (WiVRn with the headset asleep: about 5 frames a
    /// second), so the loop cannot run on the render thread without holding the game to that rate.
    /// <para>
    /// The loop keeps to the wall clock: it calls xrWaitFrame again only once real time has caught up with how far the
    /// predicted display time moved (<see cref="IdleFramesAhead"/>). XrTime counts nanoseconds ("the monotonically-increasing
    /// count of nanoseconds that have elapsed since a runtime-chosen epoch"), so the two can be compared without converting
    /// clocks. A runtime whose xrWaitFrame stops blocking while the headset sleeps would otherwise see thousands of frames a
    /// second from a loop with nothing else to do; WiVRn moves its prediction one display period on for each, so after
    /// twenty seconds asleep the session's display times are an hour ahead, and once VR returns the first frame with layers
    /// is held until that hour has passed, blocking the next xrEndFrame.
    /// </para>
    /// <para>
    /// Only this thread makes the three calls while it runs. The render thread stops and joins it (<see cref="StopIdleFrames"/>)
    /// before it makes a frame call of its own, ends the session or destroys it.
    /// </para>
    /// </remarks>
    internal static unsafe partial class XrSession
    {
        private static Thread idleThread;
        private static volatile bool idleStop;
        private static volatile Exception idleFault;
        private static readonly ManualResetEventSlim idleWake = new ManualResetEventSlim(false);

        /// <summary>The longest the loop rests in one go, so a prediction far ahead is worked off in steps it can re-check.</summary>
        private const int IdleRestMostMs = 100;

        /// <summary>Render thread: starts the loop if the session runs and nothing else is in a frame. Nothing if it is going already.</summary>
        public static void StartIdleFrames()
        {
            if (idleThread != null || !Running || InFrame || session == 0)
                return;
            idleStop = false;
            idleWake.Reset();
            ulong handle = session;
            Rendering.DxvkInterop queue = dxvk;
            idleThread = new Thread(() => IdleFrames(handle, queue)) { IsBackground = true, Name = "SEVR headset idle frames", Priority = ThreadPriority.BelowNormal };
            idleThread.Start();
        }

        /// <summary>Render thread: stops the loop and waits for its frame to end. Nothing if it is not going.</summary>
        public static void StopIdleFrames()
        {
            Thread thread = idleThread;
            if (thread == null)
                return;
            idleStop = true;
            idleWake.Set();
            long start = Stopwatch.GetTimestamp();
            thread.Join();
            idleThread = null;
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (ms >= 50.0)
                Log.Info($"The headset's frame loop without layers took {ms:0} ms to stop (its xrWaitFrame was throttled by the runtime)");
        }

        /// <summary>Render thread: the failure that ended the loop, once; null when there was none.</summary>
        public static Exception TakeIdleFault()
        {
            Exception fault = idleFault;
            idleFault = null;
            return fault;
        }

        private static void IdleFrames(ulong handle, Rendering.DxvkInterop queue)
        {
            long leastOffset = long.MaxValue;
            try
            {
                while (!idleStop)
                {
                    var state = new XrFrameState { Type = XrStructureType.FrameState };
                    var waitInfo = new XrBaseInfo(XrStructureType.FrameWaitInfo);
                    Xr.Check(Xr.xrWaitFrame(handle, ref waitInfo, ref state), "xrWaitFrame", XrRuntime.Instance);
                    var beginInfo = new XrBaseInfo(XrStructureType.FrameBeginInfo);
                    var endInfo = new XrFrameEndInfo
                    {
                        Type = XrStructureType.FrameEndInfo,
                        DisplayTime = state.PredictedDisplayTime,
                        EnvironmentBlendMode = XrConst.EnvironmentBlendOpaque,
                    };
                    queue?.LockQueue();
                    try
                    {
                        Xr.Check(Xr.xrBeginFrame(handle, ref beginInfo), "xrBeginFrame", XrRuntime.Instance);
                        Xr.Check(Xr.xrEndFrame(handle, ref endInfo), "xrEndFrame", XrRuntime.Instance);
                    }
                    finally
                    {
                        queue?.ReleaseQueue();
                    }
                    long aheadNs = IdleFramesAhead(state.PredictedDisplayTime, NowNs(), ref leastOffset);
                    if (aheadNs >= 1_000_000)
                        idleWake.Wait((int)Math.Min(aheadNs / 1_000_000, IdleRestMostMs));
                }
            }
            catch (Exception e)
            {
                idleFault = e;
            }
        }

        /// <summary>
        /// How far, in nanoseconds, the predicted display time has moved on beyond the wall clock since the loop started: its
        /// offset from <paramref name="nowNs"/> less the smallest offset seen so far (<paramref name="leastOffset"/>, updated).
        /// Zero while the runtime's xrWaitFrame paces the loop itself.
        /// </summary>
        private static long IdleFramesAhead(long predictedDisplayTime, long nowNs, ref long leastOffset)
        {
            long offset = predictedDisplayTime - nowNs;
            if (offset < leastOffset)
                leastOffset = offset;
            return offset - leastOffset;
        }

        private static long NowNs()
        {
            long ticks = Stopwatch.GetTimestamp();
            return ticks / Stopwatch.Frequency * 1_000_000_000L + ticks % Stopwatch.Frequency * 1_000_000_000L / Stopwatch.Frequency;
        }
    }
}
