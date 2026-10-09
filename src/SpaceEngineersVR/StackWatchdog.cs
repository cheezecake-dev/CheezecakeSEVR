using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SpaceEngineersVR
{
    /// <summary>
    /// Debug aid: SEVR_STACKDUMP=&lt;seconds&gt; logs the managed stacks of the watched threads that long after start,
    /// for finding out where the game is stuck when it hangs.
    /// </summary>
    internal static class StackWatchdog
    {
        private static readonly List<Thread> watched = new List<Thread>();

        public static void Watch(Thread thread)
        {
            lock (watched)
            {
                if (!watched.Contains(thread))
                    watched.Add(thread);
            }
        }

        public static void StartFromEnvironment()
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("SEVR_STACKDUMP"), out int seconds) || seconds <= 0)
                return;

            Watch(Thread.CurrentThread);
            var dumper = new Thread(() =>
            {
                Thread.Sleep(seconds * 1000);
                Thread[] threads;
                lock (watched)
                    threads = watched.ToArray();
                foreach (Thread thread in threads)
                    Log.Info($"Stack of thread '{thread.Name}' ({thread.ManagedThreadId}, {thread.ThreadState}):\n{Capture(thread)}");
            }) { IsBackground = true, Name = "SEVR stack watchdog" };
            dumper.Start();
            Log.Info($"Stack watchdog armed: dump in {seconds}s");
        }

#pragma warning disable 618 // Suspend/Resume are deprecated, but the only way to read another thread's stack in-process.
        private static string Capture(Thread thread)
        {
            try
            {
                thread.Suspend();
                try
                {
                    return new StackTrace(thread, false).ToString();
                }
                finally
                {
                    thread.Resume();
                }
            }
            catch (Exception e)
            {
                return $"(could not capture: {e.GetType().Name}: {e.Message})";
            }
        }
#pragma warning restore 618
    }
}
