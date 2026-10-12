// Stall watchdog for Windows: suspends the stuck thread briefly to read its managed stack (.NET Framework), or logs without a stack (CoreCLR).

#if !LINUX

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Finds out what code a frozen game thread is waiting in. <see cref="PerfMeter"/> says how long a frame took, never what
    /// it was doing; this reads the stuck thread's managed stack while it is stuck.
    /// <para>
    /// The render thread stamps the time at the start and end of each frame, the main thread at the start and end of each
    /// tick (<see cref="RenderProgress"/>, <see cref="MainProgress"/>: one timestamp read and one store). A background thread
    /// (<c>SEVR stall watchdog</c>) looks at the stamps four times a second. A thread with no stamp for 2 s is stuck: the
    /// watchdog suspends it for the few microseconds it takes to read its stack, resumes it, and only then builds the text and
    /// logs ("Perf stall: render thread stuck 2.3 s, stack: ..."). It does that again at 10 s and 30 s (and every 30 s after),
    /// logging the stack when it differs from the last one logged (at most <see cref="MaxStackLines"/> stack lines per stall
    /// and thread), and a line after each of these says what the process did meanwhile: bytes read and written
    /// (GetProcessIoCounters), the thread's state and CPU time, the video memory (<see cref="VideoMemory"/>). When the thread
    /// moves again, a last line gives the stall's length and the same numbers over it.
    /// </para>
    /// <para>
    /// While a thread is suspended, nothing here logs, locks or waits on anything the game could hold: the stack walk runs on
    /// a throwaway thread (<c>SEVR stall capture</c>) that the watchdog gives 3 s, then resumes the target regardless and says
    /// so, so a walk that hangs cannot freeze the game for good. If the watchdog itself wakes late, every thread of the process
    /// was held (a garbage collection waiting for a thread, or a machine that did not run us), which it logs.
    /// RenderDebug StallStacks=0 turns it off.
    /// </para>
    /// <para>
    /// On CoreCLR (Pulsar's Interim) nothing can read another thread's stack, so the log says the stack was not read and keeps
    /// the rest (the thread's state and CPU time, the process I/O, the video memory). Linux builds <c>StallWatchdog.Linux.cs</c> instead, which reads the same state from /proc.
    /// </para>
    /// </summary>
    internal static class StallWatchdog
    {
        /// <summary>RenderDebug StallStacks=0.</summary>
        public static bool Off;

        private const int PollMs = 250;
        private const double StuckMs = 2000.0;
        private const int FrameLimit = 25;
        private const int CaptureTimeoutMs = 3000;
        private const int MaxStackLines = 6;
        private const double LateMs = 1500.0;

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>When the stack is read: after this many ms stuck, at the first three tries; then every 30 s.</summary>
        private static readonly double[] StackAtMs = { 2000.0, 10000.0, 30000.0 };

        private static readonly Watched render = new Watched("render thread");
        private static readonly Watched main = new Watched("main thread");

        private static int started;
        private static long lastLateReport;
        private static int lastGc0, lastGc1, lastGc2;
        private static IoCounters lastIo;

        // ---- the watched threads' side: a stamp each ----

        /// <summary>The render thread, at the start and the end of a frame (and anywhere else it is known to be running).</summary>
        public static void RenderProgress() => Progress(render);

        /// <summary>The main thread, at the start and the end of a tick.</summary>
        public static void MainProgress() => Progress(main);

        private static void Progress(Watched w)
        {
            Thread current = Thread.CurrentThread;
            if (!ReferenceEquals(w.Thread, current))
            {
                w.Tid = (int)GetCurrentThreadId();
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

            // Written by the thread itself.
            public volatile Thread Thread;
            public int Tid;
            public long Stamp;

            // The watchdog's own.
            public long Seen;               // the stamp last looked at
            public IntPtr Handle;           // for the thread's CPU time
            public int HandleTid;
            public bool Reported;           // a stall of 2 s or more has been logged for the current gap
            public int Stage;               // the next try at reading the stack
            public int StackLines;
            public string LastStack;
            public long BaseTicks;          // the last time the thread was seen moving: when, what the process had read and
            public IoCounters BaseIo;       // written by then, and the thread's CPU time
            public ulong BaseCpu;
            public ulong LastCpu;

            public Watched(string name) => Name = name;
        }

        // ---- the watchdog's thread ----

        private static void Run()
        {
            int failures = 0;
            try
            {
                if (RenderDebug.Peek("StallStacks") == 0)
                    Off = true;
            }
            catch (Exception)
            {
            }
            while (true)
            {
                // Taken just before the sleep, so a hold during it shows in the deltas and the walks do not.
                GetProcessIoCounters(GetCurrentProcess(), out lastIo);
                lastGc0 = GC.CollectionCount(0);
                lastGc1 = GC.CollectionCount(1);
                lastGc2 = GC.CollectionCount(2);
                long before = Stopwatch.GetTimestamp();
                Thread.Sleep(PollMs);
                long now = Stopwatch.GetTimestamp();
                double lateMs = (now - before) * TicksToMs - PollMs;
                if (Off)
                    continue;
                try
                {
                    Poll(now, lateMs);
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

        private static void Poll(long now, double lateMs)
        {
            GetProcessIoCounters(GetCurrentProcess(), out IoCounters io);
            bool late = lateMs >= LateMs;
            if (late && (lastLateReport == 0 || now - lastLateReport >= 5 * Stopwatch.Frequency))
            {
                lastLateReport = now;
                Log.Warn($"Perf stall: the watchdog thread woke {lateMs / 1000.0:0.0} s late, so every thread of the game was held, or the machine did not run it; " +
                         $"process I/O over that time: {Io(lastIo, io)}; GC +{GC.CollectionCount(0) - lastGc0}/{GC.CollectionCount(1) - lastGc1}/{GC.CollectionCount(2) - lastGc2}; {VideoMemory.Describe()}");
            }

            // After being held too, the threads' stamps are as old as the hold, not stuck: look again in a quarter of a second.
            if (late)
                return;
            Check(render, now, io);
            Check(main, now, io);
        }

        private static void Check(Watched w, long now, IoCounters io)
        {
            if (w.Thread == null)
                return;
            long stamp = Volatile.Read(ref w.Stamp);
            if (stamp != w.Seen)
            {
                double gapMs = w.Seen == 0 ? 0.0 : (stamp - w.Seen) * TicksToMs;
                w.Seen = stamp;
                if (gapMs >= StuckMs)
                    Moved(w, gapMs, io);
                w.Reported = false;
                w.Stage = 0;
                w.StackLines = 0;
                w.LastStack = null;
            }

            double ageMs = (now - stamp) * TicksToMs;
            if (ageMs < 300.0)
            {
                // Moving: this is the moment a coming stall is measured from.
                w.BaseTicks = now;
                w.BaseIo = io;
                w.BaseCpu = w.LastCpu = ThreadCpu(w);
                return;
            }
            double due = w.Stage < StackAtMs.Length ? StackAtMs[w.Stage] : 30000.0 * (w.Stage - 1);
            if (ageMs >= due)
            {
                w.Stage++;
                Report(w);
            }
        }

        /// <summary>A thread that was stuck for a while is running again: how long, and what the process did meanwhile.</summary>
        private static void Moved(Watched w, double gapMs, IoCounters io)
        {
            double cpuMs = (ThreadCpu(w) - w.BaseCpu) / 10000.0;
            string what = w.Reported ? "moved again after" : "was held for";
            Log.Info($"Perf stall: {w.Name} {what} {gapMs / 1000.0:0.0} s{(w.Reported ? string.Empty : " (the watchdog did not catch it in the act)")}; " +
                     $"CPU +{cpuMs:0} ms in it, process I/O over it: {Io(w.BaseIo, io)}; {VideoMemory.Describe()}");
        }

        private static void Report(Watched w)
        {
            w.Reported = true;
            string[] frames = Capture(w.Thread, out string failure);

            // The thread is running again (or the walk was given up): from here on, building and logging text is safe.
            long now = Stopwatch.GetTimestamp();
            double ageMs = (now - Volatile.Read(ref w.Stamp)) * TicksToMs;
            GetProcessIoCounters(GetCurrentProcess(), out IoCounters io);
            string stack = frames == null ? null : frames.Length == 0 ? "(no managed frames)" : string.Join(" <- ", frames);
            string note = string.Empty;
            if (stack == null)
            {
                note = ", stack not read: " + failure;
            }
            else if (stack == w.LastStack)
            {
                note = ", the same stack as the last one logged";
            }
            else if (w.StackLines < MaxStackLines)
            {
                w.StackLines++;
                w.LastStack = stack;
                Log.Info($"Perf stall: {w.Name} stuck {ageMs / 1000.0:0.0} s, stack: {stack}");
            }
            else
            {
                note = ", stack not logged (the limit of " + MaxStackLines + " for this stall)";
            }
            double span = Math.Max(0.0, (now - w.BaseTicks) * TicksToMs / 1000.0);
            ulong cpu = ThreadCpu(w);
            Log.Info($"Perf stall: {w.Name} stuck {ageMs / 1000.0:0.0} s{note}; {OsThreadState(w)}, CPU +{(cpu - w.BaseCpu) / 10000.0:0} ms in the stall (+{(cpu - w.LastCpu) / 10000.0:0} since the last line); " +
                     $"process I/O over the last {span:0.0} s: {Io(w.BaseIo, io)}; {VideoMemory.Describe()}");
            w.LastCpu = cpu;
        }

        // ---- reading a stack ----

#if NETCOREAPP
        // StackTrace(Thread) is gone on CoreCLR and Thread.Suspend/Resume throw PlatformNotSupportedException.
        private static string[] Capture(Thread target, out string failure)
        {
            failure = "this runtime (CoreCLR) cannot read another thread's stack";
            return null;
        }
#else
        private sealed class Job
        {
            public Thread Target;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string[] Frames;
            public string Error;
        }

        /// <summary>
        /// The thread's top managed frames, or null (and why) when they could not be read. The target is suspended on a throwaway
        /// thread for the walk, and resumed by it as soon as the walk returns, or by us when it has not within the time limit.
        /// </summary>
        private static string[] Capture(Thread target, out string failure)
        {
            failure = null;
            var job = new Job { Target = target };
            try
            {
                new Thread(Walk) { IsBackground = true, Name = "SEVR stall capture", Priority = ThreadPriority.Highest }.Start(job);
            }
            catch (Exception e)
            {
                failure = "could not start the capture thread: " + e.Message;
                return null;
            }
            if (!job.Done.Wait(CaptureTimeoutMs))
            {
                Resume(target);
                failure = $"the runtime did not suspend or walk the thread within {CaptureTimeoutMs} ms (a collection waiting for a thread, or a lock in the walk); the thread was resumed";
                return null;
            }
            if (job.Error != null)
            {
                failure = job.Error;
                return null;
            }
            return job.Frames;
        }

#pragma warning disable 618 // Suspend and Resume are deprecated, and the only way to read another thread's stack in this process.
        private static void Walk(object state)
        {
            var job = (Job)state;
            StackTrace trace;
            try
            {
                job.Target.Suspend();
                try
                {
                    trace = new StackTrace(job.Target, false);
                }
                finally
                {
                    job.Target.Resume();
                }
            }
            catch (Exception e)
            {
                job.Error = "could not walk the stack: " + e.GetType().Name + ": " + e.Message;
                job.Done.Set();
                return;
            }

            // The target is running again: the names are worked out now.
            try
            {
                int count = Math.Min(trace.FrameCount, FrameLimit);
                var frames = new string[count];
                for (int i = 0; i < count; i++)
                {
                    System.Reflection.MethodBase m = trace.GetFrame(i)?.GetMethod();
                    frames[i] = m == null ? "?" : (m.DeclaringType != null ? m.DeclaringType.FullName + "." : string.Empty) + m.Name;
                }
                job.Frames = frames;
            }
            catch (Exception e)
            {
                job.Error = "could not name the frames: " + e.GetType().Name + ": " + e.Message;
            }
            job.Done.Set();
        }

        private static void Resume(Thread target)
        {
            try
            {
                target.Resume();
            }
            catch (Exception)
            {
            }
        }
#pragma warning restore 618
#endif

        // ---- what the process did ----

        private static string Io(IoCounters from, IoCounters to)
        {
            const double Mb = 1024.0 * 1024.0;
            return string.Format(CultureInfo.InvariantCulture, "read +{0:0.00} MB in {1} ops, write +{2:0.00} MB in {3} ops, other {4} ops",
                (long)(to.ReadTransferCount - from.ReadTransferCount) / Mb, (long)(to.ReadOperationCount - from.ReadOperationCount),
                (long)(to.WriteTransferCount - from.WriteTransferCount) / Mb, (long)(to.WriteOperationCount - from.WriteOperationCount),
                (long)(to.OtherOperationCount - from.OtherOperationCount));
        }

        /// <summary>What Windows says the thread is doing: running, or waiting, and for what kind of event.</summary>
        private static string OsThreadState(Watched w)
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    foreach (ProcessThread thread in process.Threads)
                    {
                        using (thread)
                        {
                            if (thread.Id != w.Tid)
                                continue;
                            System.Diagnostics.ThreadState state = thread.ThreadState;
                            return state == System.Diagnostics.ThreadState.Wait ? $"Windows has it waiting ({thread.WaitReason})" : $"Windows has it {state}";
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return "Windows thread state n/a (" + e.GetType().Name + ")";
            }
            return "Windows thread state n/a (thread not found)";
        }

        /// <summary>The thread's kernel plus user CPU time, in 100 ns units; 0 when it cannot be read.</summary>
        private static ulong ThreadCpu(Watched w)
        {
            try
            {
                if (w.Tid == 0)
                    return 0;
                if (w.HandleTid != w.Tid)
                {
                    if (w.Handle != IntPtr.Zero)
                        CloseHandle(w.Handle);
                    w.Handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)w.Tid);
                    w.HandleTid = w.Tid;
                }
                if (w.Handle == IntPtr.Zero || !GetThreadTimes(w.Handle, out long created, out long exited, out long kernel, out long user))
                    return 0;
                return (ulong)(kernel + user);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private const uint ThreadQueryLimitedInformation = 0x0800;

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
#endif
