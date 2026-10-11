using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using HarmonyLib;
using Sandbox;
using SharpDX.Direct3D11;
using SpaceEngineersVR.OpenXR;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// A frame-time meter, to tell a headset session that is bound by the CPU (the render thread or the main thread) from one
    /// bound by the GPU, and to catch hitches. It measures, per frame cycle (the start of one render-thread frame to the start
    /// of the next):
    /// <list type="bullet">
    /// <item>the render thread's own work in the frame (<see cref="EyeRenderer"/>'s DrawScene to the end of the headset frame, minus the wait below);</item>
    /// <item>the time in xrWaitFrame (idle headroom, when the runtime holds the frame back);</item>
    /// <item>the time outside our frame (everything else the render thread does, and any wait for the main thread);</item>
    /// <item>the GPU time between two D3D11 timestamp queries round the eyes, the external view and the panel, read back two or
    /// three frames later without waiting (so there is never a stall). It is the span on the GPU's clock, so it includes any time the
    /// GPU sat idle waiting for the CPU to hand over the rest of the frame: it is the GPU's busy time only when the GPU is the
    /// bottleneck (GPU about the cycle, and the render thread's in-frame time well under it);</item>
    /// <item>the main thread's tick (Game.RunSingleFrame) and its Update() (MySandboxGame.Update), by a prefix and a postfix;</item>
    /// <item>the frames the runtime asked for (from the progression of the predicted display time over the display period) against
    /// the frames submitted, and the garbage collections;</item>
    /// <item>the GPU time by pass: more timestamps inside the span above, at the boundaries of the renderer's work (<see cref="GpuPasses"/>
    /// says where; <see cref="Mark(GpuPass)"/>), summed per scene pass (the eye drawn first, the eye drawn second, the external view,
    /// and outside them) and per kind of work (shadows, GBuffer, lighting...). Consecutive marks of the same kind are merged.
    /// Off by default, RenderDebug GpuPasses=1 turns it on (<see cref="PassMarks"/>).</item>
    /// </list>
    /// One line every 10 s (and at the end of a session, or when its state changes), a second line with the worst cycle's
    /// breakdown when there was a hitch, and one line at once (at most one in 5 s) for a cycle over 100 ms. Each of these lines
    /// ends with the video memory in use against its budget (<see cref="VideoMemory"/>). Nothing is allocated per frame and
    /// nothing waits. RenderDebug PerfMeter=0 turns it off. A frame or tick that does not end is not seen here until it does:
    /// <see cref="StallWatchdog"/> (fed by the same hooks) reads the stuck thread's stack while it is stuck.
    /// </summary>
    internal static class PerfMeter
    {
        /// <summary>RenderDebug PerfMeter=0.</summary>
        public static bool Off;

        /// <summary>
        /// RenderDebug GpuPasses=1: the per-pass timestamps (<see cref="Mark(GpuPass, int)"/>), for the Perf line's GPU time by pass.
        /// Off by default: with them the game ended in NVIDIA's driver (c0000005 in nvwgf2umx.dll at 0x60c2a3, on the driver's own
        /// worker thread) 2 times in about 19 min of world time; without them (the build before they came, and PerfMeter=0) 0 in
        /// 104 min. Off, the meter issues only the frame's start, end and disjoint queries, which dynamic resolution reads; the
        /// pass queries are not even made until this is turned on.
        /// </summary>
        public static bool PassMarks;

        private const double WindowSeconds = 10.0;
        private const double BigHitchMs = 100.0;
        private const double BigHitchEverySeconds = 5.0;
        private const double WindowOnlyPeriodMs = 1000.0 / 60.0;
        private const int Ring = 8;
        private const int MaxMarks = 96;
        private const int Scenes = 4; // first eye, second eye, external view, outside them

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly long WindowTicks = (long)(WindowSeconds * Stopwatch.Frequency);
        private static readonly long BigHitchGap = (long)(BigHitchEverySeconds * Stopwatch.Frequency);

        private static bool Active => !Off && !broken;

        private static bool broken;

        // ---- main thread (its own hooks write; the render thread reads when it reports) ----

        private static readonly object mainLock = new object();
        private static readonly Hist mainTickLive = new Hist(), mainSimLive = new Hist();
        private static readonly Hist mainTick = new Hist(), mainSim = new Hist();
        private static long mainTickMax, mainSimMax;
        private static long tickStart, simStart;

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Sandbox.Engine.Platform.Game), "RunSingleFrame"),
                    prefix: new HarmonyMethod(typeof(PerfMeter), nameof(TickPrefix)),
                    postfix: new HarmonyMethod(typeof(PerfMeter), nameof(TickPostfix)));
                harmony.Patch(AccessTools.DeclaredMethod(typeof(MySandboxGame), "Update", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(PerfMeter), nameof(UpdatePrefix)),
                    postfix: new HarmonyMethod(typeof(PerfMeter), nameof(UpdatePostfix)));
                Log.Info("Frame-time meter hooked (main thread tick and Update, render thread frame); PerfMeter=0 turns it off");
            }
            catch (Exception e)
            {
                Log.Error(e, "Frame-time meter: could not hook the main thread; it will report the render thread only");
            }
        }

        private static void TickPrefix()
        {
            StallWatchdog.MainProgress();
            tickStart = Stopwatch.GetTimestamp();
        }

        private static void TickPostfix()
        {
            StallWatchdog.MainProgress();
            if (Off)
                return;
            long dt = Stopwatch.GetTimestamp() - tickStart;
            lock (mainLock)
            {
                mainTickLive.Add(dt * TicksToMs);
                if (dt > mainTickMax)
                    mainTickMax = dt;
            }
        }

        private static void UpdatePrefix() => simStart = Stopwatch.GetTimestamp();

        private static void UpdatePostfix()
        {
            if (Off)
                return;
            long dt = Stopwatch.GetTimestamp() - simStart;
            lock (mainLock)
            {
                mainSimLive.Add(dt * TicksToMs);
                if (dt > mainSimMax)
                    mainSimMax = dt;
            }
        }

        // ---- render thread ----

        /// <summary>One frame cycle. Its interval, main thread maximum and garbage collections are only known at the next frame's start.</summary>
        private struct Rec
        {
            public int State;           // 0 free, 1 open, 2 closed (the frame ended)
            public int Gpu;             // 0 no queries issued, 1 begun, 2 ended (a result to read)
            public int IntervalState;   // 0 not known yet, 1 known, 2 never (the cycle was cut off)
            public bool Skipped, Submitted;
            public long Seq;
            public long Start, End, Interval, Wait, Slots;
            public double PeriodMs, GpuMs;
            public long MainTickMax, MainSimMax;
            public int Gc0, Gc1, Gc2;
            public int Slot;
            public Query Disjoint, StartQuery, EndQuery;
        }

        // The pass marks of each ring slot's frame: a timestamp query, the work that starts there and the scene pass it is in.
        private static readonly Query[][] markQueries = new Query[Ring][];
        private static readonly byte[][] markWork = new byte[Ring][];
        private static readonly sbyte[][] markScene = new sbyte[Ring][];
        private static readonly int[] markCount = new int[Ring];
        private static readonly ulong[] markTimes = new ulong[MaxMarks];
        private static int renderThread = -1, markOverflow;
        private static readonly double[] passSum = new double[Scenes * (int)GpuPass.Count];
        private static int passFrames;

        private static readonly Rec[] ring = new Rec[Ring];
        private static int head, tail, pending, cur = -1, prev = -1;
        private static bool open, attached, gpuFailed;
        private static long seq;
        private static DeviceContext ctx;
        private static string gpuNote;
        private static int gpuDisjoint;

        private static long prevDisplayTime;
        private static double lastPeriodMs = WindowOnlyPeriodMs;
        private static int lastGc0, lastGc1, lastGc2;
        private static long meterTicks;

        // ---- the window being summed ----

        private static int windowMode = -1;
        private static long windowStart;
        private static int gcWindow0, gcWindow1, gcWindow2;
        private static int frames, hitches, headsetFrames;
        private static long asked, submitted;
        private static readonly Hist cycleH = new Hist(), renderH = new Hist(), waitH = new Hist(), outsideH = new Hist(), gpuH = new Hist();
        private static Rec worst;
        private static double worstInterval;
        private static long lastBigHitchLog;

        /// <summary>Called at the top of DrawScene, and by the GUI step when there was no scene (menus, loading); before the wait for the headset's frame.</summary>
        public static void FrameBegin()
        {
            StallWatchdog.RenderProgress();
            if (!Active)
                return;
            renderThread = Thread.CurrentThread.ManagedThreadId;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                Begin(t0);
            }
            catch (Exception e)
            {
                Break(e);
            }
            meterTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>For the GUI step: begins a frame when DrawScene did not (no scene).</summary>
        public static void FrameBeginIfClosed()
        {
            if (!open)
                FrameBegin();
        }

        private static void Begin(long t)
        {
            if (!attached)
                Attach();
            if (open)
                AbandonOpen(); // a frame whose GUI step never came
            int mode = ModeCode();
            if (mode != windowMode)
            {
                Flush("mode change", final: true);
                windowMode = mode;
                prev = -1;
                prevDisplayTime = 0;
            }
            if (windowStart == 0)
            {
                windowStart = t;
                gcWindow0 = GC.CollectionCount(0);
                gcWindow1 = GC.CollectionCount(1);
                gcWindow2 = GC.CollectionCount(2);
            }

            // The previous frame's cycle ends where this one starts.
            if (prev >= 0)
            {
                ref Rec p = ref ring[prev];
                if (p.Seq == seq && p.IntervalState == 0)
                {
                    p.Interval = t - p.Start;
                    p.IntervalState = 1;
                    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                    p.Gc0 = g0 - lastGc0;
                    p.Gc1 = g1 - lastGc1;
                    p.Gc2 = g2 - lastGc2;
                    lastGc0 = g0;
                    lastGc1 = g1;
                    lastGc2 = g2;
                    lock (mainLock)
                    {
                        p.MainTickMax = mainTickMax;
                        p.MainSimMax = mainSimMax;
                        mainTickMax = mainSimMax = 0;
                    }
                }
            }
            else
            {
                lastGc0 = GC.CollectionCount(0);
                lastGc1 = GC.CollectionCount(1);
                lastGc2 = GC.CollectionCount(2);
                lock (mainLock)
                    mainTickMax = mainSimMax = 0;
            }

            if (pending == Ring)
                FinalizeOldest(); // the oldest result never came: give up on its GPU time
            int i = head;
            head = (head + 1) % Ring;
            pending++;
            ref Rec r = ref ring[i];
            Query disjoint = r.Disjoint, startQuery = r.StartQuery, endQuery = r.EndQuery;
            r = default;
            r.Disjoint = disjoint;
            r.StartQuery = startQuery;
            r.EndQuery = endQuery;
            r.State = 1;
            r.Slot = i;
            markCount[i] = 0;
            r.Seq = ++seq;
            r.Start = t;
            r.PeriodMs = lastPeriodMs;
            r.GpuMs = -1;
            cur = prev = i;
            open = true;
        }

        /// <summary>XrSession.BeginFrame: time spent in xrWaitFrame, and the display time and period the runtime gave.</summary>
        public static void Waited(long waitStart, long displayTime, long periodNs)
        {
            if (!Active || !open)
                return;
            long t0 = Stopwatch.GetTimestamp();
            ref Rec r = ref ring[cur];
            r.Wait += t0 - waitStart;
            if (periodNs > 0)
            {
                lastPeriodMs = periodNs / 1e6;
                r.PeriodMs = lastPeriodMs;
                r.Slots = prevDisplayTime == 0 ? 1 : Math.Max(1L, (long)Math.Round((double)(displayTime - prevDisplayTime) / periodNs));
                prevDisplayTime = displayTime;
            }
            meterTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>XrSession.EndFrame, after xrEndFrame returned: the frame went to the runtime.</summary>
        public static void Submitted()
        {
            if (Active && open)
                ring[cur].Submitted = true;
        }

        /// <summary>After the wait for the headset's frame: the timestamp the GPU time is measured from.</summary>
        public static void GpuStart()
        {
            if (!Active || !open || gpuFailed || ctx == null)
                return;
            long t0 = Stopwatch.GetTimestamp();
            ref Rec r = ref ring[cur];
            if (r.Gpu == 0)
            {
                try
                {
                    ctx.Begin(r.Disjoint);
                    ctx.End(r.StartQuery);
                    r.Gpu = 1;
                }
                catch (Exception e)
                {
                    GpuBroke(e);
                }
            }
            meterTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>A timestamp can be put in this frame's GPU span now: the pass marks are on (<see cref="PassMarks"/>), the frame is open, its start query was issued, and this is the render thread.</summary>
        public static bool Marking => PassMarks && Active && open && !gpuFailed && ctx != null && cur >= 0 && ring[cur].Gpu == 1 &&
                                      Thread.CurrentThread.ManagedThreadId == renderThread;

        /// <summary>The GPU starts <paramref name="work"/> here, in the scene pass the render thread is in (<see cref="ScenePass.Current"/>).</summary>
        public static void Mark(GpuPass work) => Mark(work, ScenePass.Current);

        /// <summary>The GPU starts <paramref name="work"/> here, in <paramref name="scene"/> (a <see cref="ScenePass"/> value).</summary>
        public static void Mark(GpuPass work, int scene)
        {
            if (!Marking)
                return;
            long t0 = Stopwatch.GetTimestamp();
            int slot = cur, n = markCount[slot];
            if (n > 0 && markWork[slot][n - 1] == (byte)work && markScene[slot][n - 1] == scene)
                return;
            if (n >= MaxMarks)
            {
                markOverflow++;
                return;
            }
            try
            {
                Query[] queries = markQueries[slot] ?? (markQueries[slot] = MakeMarkQueries());
                ctx.End(queries[n]);
                markWork[slot][n] = (byte)work;
                markScene[slot][n] = (sbyte)scene;
                markCount[slot] = n + 1;
            }
            catch (Exception e)
            {
                GpuBroke(e);
            }
            meterTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>Before the headset frame is handed to the runtime: the timestamp the GPU time is measured to.</summary>
        public static void GpuEnd()
        {
            if (!Active || !open || cur < 0)
                return;
            long t0 = Stopwatch.GetTimestamp();
            EndGpu(ref ring[cur]);
            meterTicks += Stopwatch.GetTimestamp() - t0;
        }

        private static void EndGpu(ref Rec r)
        {
            if (r.Gpu != 1)
                return;
            try
            {
                ctx.End(r.EndQuery);
                ctx.End(r.Disjoint);
                r.Gpu = 2;
            }
            catch (Exception e)
            {
                r.Gpu = 0;
                GpuBroke(e);
            }
        }

        /// <summary>After the headset frame went to the runtime.</summary>
        public static void FrameEnd()
        {
            StallWatchdog.RenderProgress();
            if (!Active || !open)
                return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                ref Rec r = ref ring[cur];
                EndGpu(ref r);
                r.End = t0;
                r.State = 2;
                open = false;
                Poll();
                if (windowStart != 0 && t0 - windowStart >= WindowTicks)
                    Flush("10 s", final: false);
            }
            catch (Exception e)
            {
                Break(e);
            }
            meterTicks += Stopwatch.GetTimestamp() - t0;
        }

        /// <summary>The headset session changed state, or went away: the window so far is reported (a session that ended gets its last line this way).</summary>
        public static void OnSessionChange(string why)
        {
            if (!Active)
                return;
            try
            {
                Flush(why, final: true);
            }
            catch (Exception e)
            {
                Break(e);
            }
        }

        private static int ModeCode() => XrSession.Running ? 16 + (int)XrSession.State : XrSession.Exists ? 32 + (int)XrSession.State : 0;

        private static readonly string[] labels = new string[64];

        private static string Label(int code)
        {
            if (code < 0 || code >= labels.Length)
                return "?";
            if (labels[code] == null)
                labels[code] = code >= 32 ? $"no headset frames, session {(XrSessionState)(code - 32)}; window only"
                    : code >= 16 ? ((XrSessionState)(code - 16)).ToString()
                    : "no headset; window only";
            return labels[code];
        }

        private static void Attach()
        {
            attached = true;
            try
            {
                Device device = EyeRenderer.D3DDevice;
                VideoMemory.Init(device);
                ctx = device.ImmediateContext;
                for (int i = 0; i < Ring; i++)
                {
                    ring[i].Disjoint = new Query(device, new QueryDescription { Type = QueryType.TimestampDisjoint });
                    ring[i].StartQuery = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
                    ring[i].EndQuery = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
                    markWork[i] = new byte[MaxMarks];
                    markScene[i] = new sbyte[MaxMarks];
                }
            }
            catch (Exception e)
            {
                GpuBroke(e);
            }
        }

        /// <summary>A ring slot's pass timestamps, made the first time <see cref="PassMarks"/> marks in it (render thread).</summary>
        private static Query[] MakeMarkQueries()
        {
            Device device = EyeRenderer.D3DDevice;
            var queries = new Query[MaxMarks];
            for (int k = 0; k < MaxMarks; k++)
                queries[k] = new Query(device, new QueryDescription { Type = QueryType.Timestamp });
            return queries;
        }

        private static void GpuBroke(Exception e)
        {
            if (gpuFailed)
                return;
            gpuFailed = true;
            gpuNote = e.GetType().Name + ": " + e.Message;
            Log.Error(e, "Frame-time meter: GPU timestamp queries failed; the GPU time is left out, the CPU times go on");
        }

        private static void Break(Exception e)
        {
            broken = true;
            Log.Error(e, "Frame-time meter stopped");
        }

        /// <summary>An open frame that was never finished: its queries are closed, its numbers dropped.</summary>
        private static void AbandonOpen()
        {
            ref Rec r = ref ring[cur];
            EndGpu(ref r);
            r.Gpu = 0;
            r.Skipped = true;
            r.State = 2;
            open = false;
        }

        // ---- finishing a frame's record ----

        /// <summary>Finishes the oldest records whose numbers are all in: closed, interval known, GPU result read (or given up on, at 8 frames).</summary>
        private static void Poll()
        {
            while (pending > 0)
            {
                ref Rec r = ref ring[tail];
                if (r.State != 2)
                    break;
                if (!r.Skipped)
                {
                    if (r.IntervalState == 0)
                        break;
                    if (r.Gpu == 2 && ReadGpu(ref r) == 0)
                        break;
                }
                Finish(ref r);
                tail = (tail + 1) % Ring;
                pending--;
            }
        }

        private static void FinalizeOldest()
        {
            ref Rec r = ref ring[tail];
            if (r.Gpu == 2)
                ReadGpu(ref r);
            Finish(ref r);
            tail = (tail + 1) % Ring;
            pending--;
        }

        /// <summary>Finishes every closed record there is, with what GPU results are in.</summary>
        private static void Drain()
        {
            while (pending > 0)
            {
                ref Rec r = ref ring[tail];
                if (r.State != 2)
                    break;
                if (r.IntervalState == 0)
                    r.IntervalState = 2;
                if (!r.Skipped && r.Gpu == 2)
                    ReadGpu(ref r);
                Finish(ref r);
                tail = (tail + 1) % Ring;
                pending--;
            }
        }

        /// <summary>1 read, 0 not there yet, -1 no usable result (disjoint, or the queries failed).</summary>
        private static int ReadGpu(ref Rec r)
        {
            if (gpuFailed)
            {
                r.Gpu = 0;
                return -1;
            }
            try
            {
                if (!ctx.GetData(r.Disjoint, AsynchronousFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint))
                    return 0;
                if (disjoint.Disjoint || disjoint.Frequency == 0)
                {
                    gpuDisjoint++;
                    r.Gpu = 0;
                    return -1;
                }
                if (!ctx.GetData(r.StartQuery, AsynchronousFlags.DoNotFlush, out ulong a) || !ctx.GetData(r.EndQuery, AsynchronousFlags.DoNotFlush, out ulong b))
                    return 0;
                int marks = markCount[r.Slot];
                for (int k = 0; k < marks; k++)
                    if (!ctx.GetData(markQueries[r.Slot][k], AsynchronousFlags.DoNotFlush, out markTimes[k]))
                        return 0;
                r.GpuMs = b >= a ? (b - a) * 1000.0 / disjoint.Frequency : -1;
                if (r.GpuMs >= 0 && marks > 0)
                    SumPasses(r.Slot, marks, a, b, 1000.0 / disjoint.Frequency);
                r.Gpu = 0;
                return 1;
            }
            catch (Exception e)
            {
                GpuBroke(e);
                r.Gpu = 0;
                return -1;
            }
        }

        /// <summary>Adds a frame's spans between marks to the window's sums by scene pass and work. Before the first mark, the work is <see cref="GpuPass.Compose"/>.</summary>
        private static void SumPasses(int slot, int marks, ulong start, ulong end, double msPerTick)
        {
            ulong from = start;
            int work = (int)GpuPass.Compose, scene = Scenes - 1;
            for (int k = 0; k <= marks; k++)
            {
                ulong to = k < marks ? markTimes[k] : end;
                if (to > from)
                {
                    passSum[scene * (int)GpuPass.Count + work] += (to - from) * msPerTick;
                    from = to;
                }
                if (k < marks)
                {
                    work = markWork[slot][k];
                    int s = markScene[slot][k];
                    scene = s >= 0 && s < Scenes - 1 ? s : Scenes - 1;
                }
            }
            passFrames++;
        }

        private static void Finish(ref Rec r)
        {
            if (r.Skipped)
                return;
            double total = (r.End - r.Start) * TicksToMs;
            double wait = r.Wait * TicksToMs;
            frames++;
            renderH.Add(Math.Max(0.0, total - wait));
            if (r.Slots > 0)
            {
                headsetFrames++;
                asked += r.Slots;
                if (r.Submitted)
                    submitted++;
                waitH.Add(wait);
            }
            if (r.GpuMs >= 0)
            {
                gpuH.Add(r.GpuMs);
                DynamicResolution.Sample(r.GpuMs, Math.Max(0.0, total - wait), r.PeriodMs, r.Slots > 0);
            }
            if (r.IntervalState != 1)
                return;

            double interval = r.Interval * TicksToMs;
            cycleH.Add(interval);
            outsideH.Add(Math.Max(0.0, interval - total));
            if (interval > 2.0 * r.PeriodMs)
            {
                hitches++;
                if (interval > worstInterval)
                {
                    worstInterval = interval;
                    worst = r;
                }
            }
            if (interval >= BigHitchMs)
            {
                long now = Stopwatch.GetTimestamp();
                if (lastBigHitchLog == 0 || now - lastBigHitchLog >= BigHitchGap)
                {
                    lastBigHitchLog = now;
                    Log.Info("Perf hitch: " + Describe(ref r, 0) + ", " + VideoMemory.Describe() + " (now)");
                }
            }
        }

        private static string Describe(ref Rec r, long windowStartTicks)
        {
            double total = (r.End - r.Start) * TicksToMs;
            double wait = r.Wait * TicksToMs;
            double interval = r.Interval * TicksToMs;
            var sb = new StringBuilder(256);
            Append(sb, $"frame cycle {interval:0.0} ms ({interval / Math.Max(r.PeriodMs, 0.1):0.0}x the {r.PeriodMs:0.0} ms period)");
            if (windowStartTicks != 0)
                Append(sb, $", {Math.Max(0L, r.Start - windowStartTicks) * TicksToMs / 1000.0:0.0} s into the window");
            Append(sb, $": render thread in-frame {Math.Max(0.0, total - wait):0.0}, xrWaitFrame {wait:0.0}, outside our frame {Math.Max(0.0, interval - total):0.0}");
            if (r.GpuMs >= 0)
                Append(sb, $", GPU {r.GpuMs:0.0}");
            else
                sb.Append(", GPU n/a");
            Append(sb, $", slowest main thread tick {r.MainTickMax * TicksToMs:0.0} (Update() {r.MainSimMax * TicksToMs:0.0}), GC +{r.Gc0}/{r.Gc1}/{r.Gc2}");
            if (!r.Submitted && r.Slots > 0)
                sb.Append(", not submitted");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, FormattableString text) => sb.Append(text.ToString(CultureInfo.InvariantCulture));

        private static readonly string[] sceneNames = { "eye 1", "eye 2", "external view", "outside the eyes" };
        private static readonly string[] workNames = { "compose", "gui", "setup", "shadows", "gbuffer", "probe", "occlusion", "lighting", "transparent", "exposure", "bloom", "post" };

        /// <summary>
        /// "; GPU by pass (ms per frame, N frames): eye 1 9.10 (setup 0.20, shadows 2.00, ...), eye 2 ..., external view 1.00 (...),
        /// outside the eyes 0.40 (compose 0.30, gui 0.10)". The eyes list every kind of scene work, the others only what they had.
        /// </summary>
        private static void AppendPasses(StringBuilder sb)
        {
            if (passFrames == 0)
                return;
            int count = (int)GpuPass.Count;
            Append(sb, $"; GPU by pass (ms per frame, {passFrames} frames");
            if (markOverflow > 0)
                Append(sb, $", {markOverflow} marks over {MaxMarks} dropped");
            sb.Append("):");
            bool firstScene = true;
            for (int scene = 0; scene < Scenes; scene++)
            {
                double total = 0.0;
                for (int w = 0; w < count; w++)
                    total += passSum[scene * count + w];
                if (scene >= 2 && total < 0.005 * passFrames)
                    continue;
                sb.Append(firstScene ? " " : ", ");
                firstScene = false;
                Append(sb, $"{sceneNames[scene]} {total / passFrames:0.00} (");
                bool firstWork = true;
                for (int w = 0; w < count; w++)
                {
                    double ms = passSum[scene * count + w] / passFrames;
                    bool eye = scene < 2 && w >= (int)GpuPass.Setup;
                    if (!eye && ms < 0.005)
                        continue;
                    if (!firstWork)
                        sb.Append(", ");
                    firstWork = false;
                    Append(sb, $"{workNames[w]} {ms:0.00}");
                }
                sb.Append(')');
            }
        }

        // ---- the report ----

        /// <summary>Reports the window and starts the next. <paramref name="final"/>: finish every record there is first (session ended, state or mode changed).</summary>
        private static void Flush(string why, bool final)
        {
            long now = Stopwatch.GetTimestamp();
            if (final)
                Drain();
            double seconds = windowStart == 0 ? 0.0 : (now - windowStart) / (double)Stopwatch.Frequency;
            lock (mainLock)
            {
                mainTick.TakeFrom(mainTickLive);
                mainSim.TakeFrom(mainSimLive);
            }

            if (frames > 0 && seconds >= 0.5)
            {
                var sb = new StringBuilder(640);
                Append(sb, $"Perf {seconds:0.0} s [{Label(windowMode)}]");
                if (final)
                    Append(sb, $" ({why})");
                sb.Append(": ");
                if (headsetFrames > 0)
                    Append(sb, $"{asked} frames asked, {submitted} submitted ({100.0 * submitted / Math.Max(asked, 1):0}%)");
                else
                    Append(sb, $"{frames} frames drawn ({frames / seconds:0}/s)");
                Append(sb, $"; frame cycle {cycleH.Mean:0.0} ms avg / {cycleH.P95:0.0} p95 / {cycleH.Max:0.0} max (display period {lastPeriodMs:0.0}), {hitches} over 2x");
                Append(sb, $"; render thread in-frame {renderH.Mean:0.0} / {renderH.P95:0.0} / {renderH.Max:0.0}");
                if (waitH.N > 0)
                    Append(sb, $"; xrWaitFrame wait {waitH.Mean:0.0} avg / {waitH.Max:0.0} max");
                Append(sb, $"; outside our frame {outsideH.Mean:0.0} / {outsideH.P95:0.0} / {outsideH.Max:0.0}");
                if (gpuH.N > 0)
                    Append(sb, $"; GPU {gpuH.Mean:0.0} / {gpuH.P95:0.0} / {gpuH.Max:0.0} ({gpuH.N} of {frames} frames{(gpuDisjoint > 0 ? $", {gpuDisjoint} disjoint" : "")})");
                else
                    Append(sb, $"; GPU n/a ({(gpuFailed ? gpuNote : gpuDisjoint > 0 ? gpuDisjoint + " disjoint" : "no results read")})");
                if (mainTick.N > 0)
                    Append(sb, $"; main thread tick {mainTick.Mean:0.0} / {mainTick.P95:0.0} / {mainTick.Max:0.0} ({mainTick.N / seconds:0}/s), Update() {mainSim.Mean:0.0} / {mainSim.P95:0.0} / {mainSim.Max:0.0}");
                else
                    sb.Append("; main thread: no ticks in the window");
                Append(sb, $"; GC {GC.CollectionCount(0) - gcWindow0}/{GC.CollectionCount(1) - gcWindow1}/{GC.CollectionCount(2) - gcWindow2}");
                AppendPasses(sb);
                sb.Append("; ").Append(DynamicResolution.Describe());
                sb.Append("; ").Append(VideoMemory.Describe());
                sb.Append(Hands.SurfaceStop.PerfText());
                sb.Append(WristStatus.PerfText());
                Append(sb, $"; meter {meterTicks * TicksToMs / frames:0.000} ms/frame");
                Log.Info(sb.ToString());
                if (hitches > 0)
                    Log.Info($"Perf worst of {hitches} cycles over 2x: " + Describe(ref worst, windowStart));
            }

            frames = hitches = headsetFrames = 0;
            asked = submitted = 0;
            cycleH.Clear();
            renderH.Clear();
            waitH.Clear();
            outsideH.Clear();
            gpuH.Clear();
            mainTick.Clear();
            mainSim.Clear();
            worst = default;
            worstInterval = 0;
            gpuDisjoint = 0;
            meterTicks = 0;
            Array.Clear(passSum, 0, passSum.Length);
            passFrames = 0;
            markOverflow = 0;
            windowStart = 0;
        }

        /// <summary>Counts in 0.25 ms steps up to 100 ms (one more bucket beyond), for an average, a 95th percentile and a maximum without keeping samples.</summary>
        private sealed class Hist
        {
            private const int Buckets = 400;
            private const double Step = 0.25;
            private readonly int[] counts = new int[Buckets + 1];

            public int N { get; private set; }

            public double Sum { get; private set; }

            public double Max { get; private set; }

            public double Mean => N == 0 ? 0.0 : Sum / N;

            public void Add(double ms)
            {
                if (ms < 0.0)
                    ms = 0.0;
                int bucket = (int)(ms / Step);
                counts[bucket > Buckets ? Buckets : bucket]++;
                N++;
                Sum += ms;
                if (ms > Max)
                    Max = ms;
            }

            public double P95
            {
                get
                {
                    if (N == 0)
                        return 0.0;
                    int target = (int)Math.Ceiling(N * 0.95), seen = 0;
                    for (int b = 0; b <= Buckets; b++)
                    {
                        seen += counts[b];
                        if (seen >= target)
                            return b == Buckets ? Max : Math.Min((b + 1) * Step, Max);
                    }
                    return Max;
                }
            }

            public void Clear()
            {
                Array.Clear(counts, 0, counts.Length);
                N = 0;
                Sum = 0.0;
                Max = 0.0;
            }

            /// <summary>Moves <paramref name="source"/>'s samples here, leaving it empty.</summary>
            public void TakeFrom(Hist source)
            {
                Array.Copy(source.counts, counts, counts.Length);
                N = source.N;
                Sum = source.Sum;
                Max = source.Max;
                source.Clear();
            }
        }
    }
}
