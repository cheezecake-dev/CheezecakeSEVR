// Stall watchdog for Linux: reads the stuck thread's state from /proc, since CoreCLR cannot read another thread's stack.

#if LINUX

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Finds out when a game thread is frozen and what the process did meanwhile. The Windows one also reads the stuck thread's
    /// stack, which CoreCLR does not allow, so this keeps the rest and reads it from /proc instead of kernel32: the render and
    /// main threads stamp the time at the start and end of each frame or tick (<see cref="RenderProgress"/>,
    /// <see cref="MainProgress"/>); a background thread looks four times a second, and a thread with no stamp for 2 s is logged
    /// ("Perf stall: render thread stuck 2.3 s"), again at 10 s and 30 s (then every 30 s), with the thread's state and kernel
    /// wait (/proc/self/task/tid/stat, wchan), its CPU time, the bytes the process read and wrote (/proc/self/io), the garbage
    /// collections and the video memory. A last line gives the stall's length when the thread moves again.
    /// Off unless RenderDebug has StallStacks=1: it is a diagnostic.
    /// </summary>
    internal static class StallWatchdog
    {
        /// <summary>True unless RenderDebug has StallStacks=1.</summary>
        public static bool Off = true;

        private const int PollMs = 250;
        private const double StuckMs = 2000.0;
        private static readonly double[] ReportAtMs = { 2000.0, 10000.0, 30000.0 };
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private static readonly Watched render = new Watched("render thread");
        private static readonly Watched main = new Watched("main thread");
        private static int started;

        [DllImport("libc.so.6", EntryPoint = "gettid")]
        private static extern int GetThreadId();

        /// <summary>The render thread, at the start and the end of a frame.</summary>
        public static void RenderProgress() => Progress(render);

        /// <summary>The main thread, at the start and the end of a tick.</summary>
        public static void MainProgress() => Progress(main);

        private static void Progress(Watched w)
        {
            Thread current = Thread.CurrentThread;
            if (!ReferenceEquals(w.Thread, current))
            {
                try
                {
                    w.Tid = GetThreadId();
                }
                catch (Exception)
                {
                    w.Tid = 0;
                }
                w.Thread = current;
                if (Interlocked.Exchange(ref started, 1) == 0)
                    StartWatchdog();
            }
            Volatile.Write(ref w.Stamp, Stopwatch.GetTimestamp());
        }

        private static void StartWatchdog()
        {
            try
            {
                new Thread(Run) { IsBackground = true, Name = "SEVR stall watchdog", Priority = ThreadPriority.AboveNormal }.Start();
            }
            catch (Exception e)
            {
                Off = true;
                Log.Error(e, "Stall watchdog could not start");
            }
        }

        private sealed class Watched
        {
            public readonly string Name;
            public volatile Thread Thread;
            public int Tid;
            public long Stamp;
            // The watchdog's own.
            public long Seen;
            public int Stage;           // the next entry of ReportAtMs (then every 30 s)
            public long BaseTicks;      // when the thread was last seen moving, with what the process had read and written then
            public Io BaseIo;
            public double BaseCpuMs;
            public bool Reported;

            public Watched(string name) => Name = name;
        }

        private struct Io
        {
            public long Read, Written;
        }

        private static void Run()
        {
            try
            {
                if (RenderDebug.Peek("StallStacks") is int on && on != 0)
                    Off = false;
            }
            catch (Exception)
            {
            }
            int failures = 0;
            while (true)
            {
                Thread.Sleep(PollMs);
                if (Off)
                    continue;
                try
                {
                    long now = Stopwatch.GetTimestamp();
                    Check(render, now);
                    Check(main, now);
                }
                catch (Exception e)
                {
                    if (++failures <= 3)
                        Log.Error(e, "Stall watchdog");
                    if (failures >= 10)
                    {
                        Off = true;
                        Log.Warn("Stall watchdog: stopped after repeated errors");
                    }
                }
            }
        }

        private static void Check(Watched w, long now)
        {
            long stamp = Volatile.Read(ref w.Stamp);
            if (stamp == 0 || w.Tid == 0)
                return;
            if (stamp != w.Seen)
            {
                if (w.Reported)
                {
                    double length = (now - w.BaseTicks) * TicksToMs;
                    Log.Info($"Perf stall: {w.Name} moving again after {length / 1000.0:0.0} s; over it the process {IoText(w.BaseIo, ReadIo())}, thread cpu +{CpuMs(w.Tid) - w.BaseCpuMs:0} ms");
                }
                w.Seen = stamp;
                w.BaseTicks = now;
                w.BaseIo = ReadIo();
                w.BaseCpuMs = CpuMs(w.Tid);
                w.Stage = 0;
                w.Reported = false;
                return;
            }
            double stuckMs = (now - w.BaseTicks) * TicksToMs;
            if (stuckMs < StuckMs)
                return;
            double due = w.Stage < ReportAtMs.Length ? ReportAtMs[w.Stage] : ReportAtMs[ReportAtMs.Length - 1] + 30000.0 * (w.Stage - ReportAtMs.Length + 1);
            if (stuckMs < due)
                return;
            w.Stage++;
            w.Reported = true;
            Log.Warn($"Perf stall: {w.Name} stuck {stuckMs / 1000.0:0.0} s (no stack: CoreCLR cannot read another thread's); {ThreadText(w.Tid)}, cpu +{CpuMs(w.Tid) - w.BaseCpuMs:0} ms; " +
                     $"the process {IoText(w.BaseIo, ReadIo())}; GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}; {VideoMemory.Describe()}");
        }

        /// <summary>The thread's state letter (R running, S sleeping, D uninterruptible disk or driver wait) and the kernel function it waits in.</summary>
        private static string ThreadText(int tid)
        {
            string state = "?";
            string wait = null;
            try
            {
                string stat = System.IO.File.ReadAllText($"/proc/self/task/{tid}/stat");
                int close = stat.LastIndexOf(')');
                if (close > 0 && close + 2 < stat.Length)
                    state = stat[close + 2].ToString();
            }
            catch (Exception)
            {
            }
            try
            {
                wait = System.IO.File.ReadAllText($"/proc/self/task/{tid}/wchan").Trim();
            }
            catch (Exception)
            {
            }
            return $"state {state}" + (string.IsNullOrEmpty(wait) || wait == "0" ? "" : $" in {wait}");
        }

        /// <summary>The thread's CPU time (user plus system) in ms; /proc counts in 10 ms ticks (USER_HZ is 100).</summary>
        private static double CpuMs(int tid)
        {
            try
            {
                string stat = System.IO.File.ReadAllText($"/proc/self/task/{tid}/stat");
                string[] f = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                // After "pid (comm)": state is f[0], utime f[11], stime f[12].
                return (long.Parse(f[11], CultureInfo.InvariantCulture) + long.Parse(f[12], CultureInfo.InvariantCulture)) * 10.0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static Io ReadIo()
        {
            var io = new Io();
            try
            {
                foreach (string line in System.IO.File.ReadLines("/proc/self/io"))
                {
                    if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                        io.Read = long.Parse(line.Substring(11).Trim(), CultureInfo.InvariantCulture);
                    else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                        io.Written = long.Parse(line.Substring(12).Trim(), CultureInfo.InvariantCulture);
                }
            }
            catch (Exception)
            {
            }
            return io;
        }

        private static string IoText(Io from, Io to) =>
            $"read {(to.Read - from.Read) / 1048576.0:0.0} MB from storage and wrote {(to.Written - from.Written) / 1048576.0:0.0} MB";
    }
}
#endif
