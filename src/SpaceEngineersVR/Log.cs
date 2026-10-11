using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SpaceEngineersVR
{
    /// <summary>
    /// Plugin log, written beside the game's own logs in %AppData%\SpaceEngineers.
    /// The game's MyLog is not available yet when the launcher bootstraps us, so we keep our own.
    /// <para>
    /// A caller never touches the disk: <see cref="Info"/>, <see cref="Warn"/> and <see cref="Error(string)"/> put the line
    /// (with the time of the call) in a queue under a lock held for a few instructions, and one background thread
    /// (<c>SEVR log</c>, below normal priority) formats, writes and flushes whatever is queued, a batch at a time. It wakes
    /// the moment a line arrives, so with a healthy disk a line is written within a millisecond or so and a batch is usually
    /// one line; batches only grow when the disk is slow, which is when batching matters. (A timer would put up to its
    /// period of lines at risk when the game is killed rather than closed.) The game's threads therefore never wait on the
    /// disk, which matters on a drive that stalls: a controller reset on the NVMe holding %AppData% froze every thread that
    /// wrote a line, one at a time, before this.
    /// </para>
    /// <para>
    /// The queue is bounded (<see cref="Capacity"/>). When the disk is so slow that it fills, the oldest Info lines go
    /// (a quarter of the queue at once, counted) and the caller is still not made to wait; Warn and Error lines are never
    /// dropped. Once the writer is through, it logs how many went and how long the lines waited, which is itself evidence
    /// of a stall; so is "Log: a write to C: took N ms", at most one in ten seconds.
    /// </para>
    /// <para>
    /// Lines still in the queue are flushed, with a 500 ms limit so a dead disk cannot hang the exit, on ProcessExit,
    /// DomainUnload, an unhandled exception and <see cref="Plugin.Dispose"/> (<see cref="Flush"/>). What cannot be saved is
    /// the few milliseconds of lines queued when the process is killed outright (TerminateProcess skips all of those).
    /// Measured in the game: closing the window, and the main menu's Exit to Windows, end the process within a moment without
    /// ProcessExit or <see cref="Plugin.Dispose"/> running, and a Harmony prefix on <c>MySandboxGame.ExitThreadSafe</c> did not
    /// log either, so on that path the lines are safe only because the writer writes each one within about a millisecond.
    /// </para>
    /// </summary>
    internal static class Log
    {
        /// <summary>Lines the queue holds before it drops the oldest Info ones.</summary>
        private const int Capacity = 8192;

        /// <summary>How long an exit waits for the writer.</summary>
        private const int ExitFlushMs = 500;

        /// <summary>A write that takes this long is reported (once in <see cref="SlowReportSeconds"/>).</summary>
        private const long SlowWriteMs = 200;

        private const long SlowReportSeconds = 10;

        private enum Level : byte
        {
            Info,
            Warn,
            Error,
        }

        private struct Entry
        {
            public long Ticks;
            public string Message;
            public string ThreadName;
            public int ThreadId;
            public Level Level;
        }

        // The queue. Every field in this block is guarded by Gate, which is held only for a few instructions and never
        // across I/O. The writer swaps pending with its own emptied array, so a caller never waits for a write.
        private static readonly object Gate = new object();
        private static Entry[] pending = new Entry[Capacity];
        private static int pendingCount;
        private static long enqueued;      // lines handed in so far, dropped ones too
        private static long settled;       // of those, the ones written or dropped (a flush waits for this to reach enqueued)
        private static long droppedSinceReport;
        private static long droppedTotal;
        private static long writtenTotal;

        // The writer thread's own.
        private static StreamWriter writer;
        private static Thread writerThread;
        private static string drive = "the log drive";
        private static bool broken;
        private static long lostSinceReport;
        private static string lostWhy;
        private static bool slowReported;
        private static long lastSlowReport;
        private static long slowWorstMs;
        private static int slowCount;

        private static bool hooked;
        private static volatile bool closing;

        private static readonly char[] TimeBuffer = new char[12];

        public static string FilePath { get; private set; }

        public static void Init() => Init(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers"));

        /// <summary>Logs into <paramref name="dir"/> (the folder is made if it is not there); <see cref="Init()"/> uses the game's.</summary>
        internal static void Init(string dir)
        {
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "SpaceEngineersVR.log");
            KeepPrevious(FilePath);
            // AutoFlush stays off: the writer thread flushes once per batch.
            writer = new StreamWriter(FilePath, append: false);
            string root = Path.GetPathRoot(FilePath);
            if (!string.IsNullOrEmpty(root))
                drive = root.TrimEnd('\\', '/');

            writerThread = new Thread(Run) { IsBackground = true, Name = "SEVR log", Priority = ThreadPriority.BelowNormal };
            writerThread.Start();

            if (!hooked)
            {
                hooked = true;
                // Ours are added before the game's own (the plugin starts first), so the lines are on disk before its crash
                // report runs, which may end the process.
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => Closing("the process is exiting");
                AppDomain.CurrentDomain.DomainUnload += (sender, e) => Closing("the app domain is unloading");
                AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                {
                    Write(Level.Error, "Unhandled exception" + (e.IsTerminating ? " (the process is going down)" : string.Empty) + ": " + e.ExceptionObject);
                    if (e.IsTerminating)
                        Closing("an unhandled exception ends the process");
                };
            }
        }

        /// <summary>
        /// The run before this one stays readable: its log becomes SpaceEngineersVR.prev.log (the older one of those is
        /// replaced), so that starting the game again does not wipe what a headset session wrote. Never throws; a log that
        /// cannot be moved (another game has it open) is left for the writer to deal with, as before.
        /// </summary>
        internal static void KeepPrevious(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                string previous = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".prev" + Path.GetExtension(path));
                // Aside first: a log another process holds open fails here, before the older .prev.log is thrown away.
                string aside = path + ".moving";
                if (File.Exists(aside))
                    File.Delete(aside);
                File.Move(path, aside);
                if (File.Exists(previous))
                    File.Delete(previous);
                File.Move(aside, previous);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
        }

        public static void Info(string message) => Write(Level.Info, message);

        public static void Warn(string message) => Write(Level.Warn, message);

        public static void Error(string message) => Write(Level.Error, message);

        public static void Error(Exception e, string context) => Write(Level.Error, context + ": " + e);

        /// <summary>
        /// Waits until every line logged before this call is on disk (written and flushed) or dropped, or the time is up (a
        /// stalled disk). For the end of the game: the exit hooks call it, so should anything that knows the game is closing.
        /// Never throws.
        /// </summary>
        public static void Flush(int timeoutMs = ExitFlushMs)
        {
            try
            {
                if (writerThread == null || Thread.CurrentThread == writerThread)
                    return;
                long deadline = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
                lock (Gate)
                {
                    long target = enqueued;
                    while (settled < target)
                    {
                        long left = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
                        if (left <= 0)
                            return;
                        Monitor.Wait(Gate, (int)left);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The process is going down (or may be, any moment): the lines so far are flushed, then each line logged is waited for
        /// (up to 100 ms) as it used to be written on the spot, so whatever is logged on the way out is in the file when the
        /// process is killed. Called by the exit hooks in <see cref="Init(string)"/>.
        /// </summary>
        private static void Closing(string why)
        {
            try
            {
                long dropped, written;
                lock (Gate)
                {
                    dropped = droppedTotal;
                    written = writtenTotal;
                }
                Write(Level.Info, $"Log: closing, {why} ({written} lines written so far, {dropped} dropped)");
                Flush(ExitFlushMs);
            }
            finally
            {
                // From here the process is on its way out and the writer may not get another turn: a line logged now is
                // waited for (briefly), as a line used to be written on the spot.
                closing = true;
            }
        }

        private static void Write(Level level, string message)
        {
            Thread t = Thread.CurrentThread;
            long ticks = DateTime.Now.Ticks;
            lock (Gate)
            {
                enqueued++;
                if (pendingCount == pending.Length)
                    MakeRoom();
                pending[pendingCount] = new Entry { Ticks = ticks, Message = message, ThreadName = t.Name, ThreadId = t.ManagedThreadId, Level = level };
                if (++pendingCount == 1)
                    Monitor.PulseAll(Gate);
            }
            if (closing)
                Flush(100);
        }

        /// <summary>
        /// The queue is full, so the disk is far behind: a quarter of it, the oldest Info lines, is dropped in one pass (one
        /// pass in a while, not one per line). Where there are no Info lines to drop the queue grows instead. Called under Gate.
        /// </summary>
        private static void MakeRoom()
        {
            int toDrop = pending.Length / 4, dropped = 0, kept = 0;
            for (int i = 0; i < pendingCount; i++)
            {
                if (dropped < toDrop && pending[i].Level == Level.Info)
                {
                    dropped++;
                    continue;
                }
                pending[kept++] = pending[i];
            }
            Array.Clear(pending, kept, pendingCount - kept);
            pendingCount = kept;
            droppedSinceReport += dropped;
            droppedTotal += dropped;
            if (dropped < pending.Length / 8)
                Array.Resize(ref pending, pending.Length * 2);
        }

        // ---- The writer thread. ----

        private static void Run()
        {
            Entry[] batch = new Entry[Capacity];
            while (true)
            {
                int count;
                long upTo;
                lock (Gate)
                {
                    while (pendingCount == 0)
                        Monitor.Wait(Gate);
                    Entry[] full = pending;
                    count = pendingCount;
                    pending = batch;
                    pendingCount = 0;
                    batch = full;
                    upTo = enqueued;
                }

                long startedAt = Stopwatch.GetTimestamp();
                try
                {
                    WriteBatch(batch, count);
                }
                catch (Exception e)
                {
                    // Never end the writer: the next batch may find the disk back.
                    Lose(count, e.Message);
                }
                long writeMs = (Stopwatch.GetTimestamp() - startedAt) * 1000 / Stopwatch.Frequency;
                long waitedMs = Math.Max(0L, (DateTime.Now.Ticks - batch[0].Ticks) / TimeSpan.TicksPerMillisecond);
                Array.Clear(batch, 0, count);

                long dropped;
                lock (Gate)
                {
                    settled = upTo;
                    writtenTotal += count;
                    dropped = droppedSinceReport;
                    droppedSinceReport = 0;
                    Monitor.PulseAll(Gate);
                }

                try
                {
                    Report(writeMs, waitedMs, count, dropped);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void WriteBatch(Entry[] batch, int count)
        {
            if (broken)
                Reopen();
            for (int i = 0; i < count; i++)
            {
                Entry e = batch[i];
                FormatTime(new DateTime(e.Ticks));
                writer.Write(TimeBuffer, 0, TimeBuffer.Length);
                writer.Write(e.Level == Level.Info ? " [INFO] [" : e.Level == Level.Warn ? " [WARN] [" : " [ERROR] [");
                writer.Write(e.ThreadName ?? e.ThreadId.ToString());
                writer.Write("] ");
                writer.WriteLine(e.Message);
            }
            writer.Flush();
        }

        /// <summary>HH:mm:ss.fff, as <c>{DateTime.Now:HH:mm:ss.fff}</c> wrote it, without a string for each line.</summary>
        private static void FormatTime(DateTime t)
        {
            char[] b = TimeBuffer;
            b[0] = (char)('0' + t.Hour / 10);
            b[1] = (char)('0' + t.Hour % 10);
            b[2] = ':';
            b[3] = (char)('0' + t.Minute / 10);
            b[4] = (char)('0' + t.Minute % 10);
            b[5] = ':';
            b[6] = (char)('0' + t.Second / 10);
            b[7] = (char)('0' + t.Second % 10);
            b[8] = '.';
            b[9] = (char)('0' + t.Millisecond / 100);
            b[10] = (char)('0' + t.Millisecond / 10 % 10);
            b[11] = (char)('0' + t.Millisecond % 10);
        }

        /// <summary>After a failed write the file is opened again for appending (the stream may be unusable); if that fails too, the next batch tries again.</summary>
        private static void Reopen()
        {
            try
            {
                writer.Dispose();
            }
            catch
            {
            }
            writer = new StreamWriter(FilePath, append: true);
            broken = false;
        }

        /// <summary>A batch the disk refused: counted, and said once a write goes through again.</summary>
        private static void Lose(int lines, string why)
        {
            broken = true;
            lostSinceReport += lines;
            lostWhy = why;
        }

        /// <summary>
        /// After a batch: what the stall (if there was one) cost, as lines in the next batch. A slow write is reported once in
        /// ten seconds, the others in the window counted into that report.
        /// </summary>
        private static void Report(long writeMs, long waitedMs, int lines, long dropped)
        {
            if (dropped > 0)
                Write(Level.Warn, $"Log: {dropped} lines dropped while the disk was slow (longest wait {waitedMs} ms)");
            if (lostSinceReport > 0 && !broken)
            {
                Write(Level.Warn, $"Log: {lostSinceReport} lines could not be written to {drive} ({lostWhy})");
                lostSinceReport = 0;
            }

            if (writeMs >= SlowWriteMs)
            {
                slowCount++;
                slowWorstMs = Math.Max(slowWorstMs, writeMs);
            }
            long now = Stopwatch.GetTimestamp();
            if (slowCount > 0 && (!slowReported || now - lastSlowReport >= SlowReportSeconds * Stopwatch.Frequency))
            {
                slowReported = true;
                lastSlowReport = now;
                Write(Level.Warn, slowCount == 1
                    ? $"Log: a write to {drive} took {slowWorstMs} ms ({lines} lines)"
                    : $"Log: a write to {drive} took {slowWorstMs} ms (the worst of {slowCount} slow writes since the last report)");
                slowCount = 0;
                slowWorstMs = 0;
            }
        }
    }
}
