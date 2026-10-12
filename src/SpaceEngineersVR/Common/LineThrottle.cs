using System.Diagnostics;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// Writes log lines for a feature that can fire every frame, at most a fixed number per second. Lines over the limit are
    /// dropped and counted, and the count is appended to the next line that gets through, so a gap in the log is visible.
    /// Not thread-safe: one instance per feature, used from the game thread only.
    /// </summary>
    internal sealed class LineThrottle
    {
        private readonly int perSecond;
        private long window;
        private int inWindow, skipped;

        public LineThrottle(int perSecond)
        {
            this.perSecond = perSecond;
        }

        public void Info(string line)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - window > Stopwatch.Frequency)
            {
                window = now;
                inWindow = 0;
            }
            if (inWindow >= perSecond)
            {
                skipped++;
                return;
            }
            inWindow++;
            Log.Info(skipped > 0 ? $"{line} ({skipped} lines skipped before this)" : line);
            skipped = 0;
        }
    }
}
