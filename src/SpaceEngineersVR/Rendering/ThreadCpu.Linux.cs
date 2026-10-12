// The calling thread's CPU time on Linux, for telling a thread that works from one that waits.

#if LINUX

using System.Runtime.InteropServices;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>The CPU time the calling thread has used, which does not count the time it is blocked.</summary>
    internal static class ThreadCpu
    {
        private const int ClockThreadCputimeId = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct TimeSpec
        {
            public long Seconds, Nanoseconds;
        }

        [DllImport("libc.so.6", EntryPoint = "clock_gettime")]
        private static extern int ClockGetTime(int clock, out TimeSpec time);

        /// <summary>The thread's CPU time in nanoseconds, or -1 when the system would not say.</summary>
        public static long Nanoseconds()
        {
            try
            {
                return ClockGetTime(ClockThreadCputimeId, out TimeSpec t) == 0 ? t.Seconds * 1000000000L + t.Nanoseconds : -1;
            }
            catch (System.Exception)
            {
                return -1;
            }
        }
    }
}

#endif
