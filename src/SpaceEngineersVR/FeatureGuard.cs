using System;
using System.Diagnostics;

namespace SpaceEngineersVR
{
    /// <summary>
    /// Keeps an optional feature's per-frame hook from costing frames or filling the log once it keeps throwing (a game
    /// update that changed what it reads, say). Three exceptions within ten seconds switch the feature off; it comes back
    /// after 30 s, then after 2 min, and the third time it trips it stays off for the session. The first exception is
    /// logged in full and each switch in one line; the rest go unlogged. Core rendering does not use it.
    /// </summary>
    /// <remarks>
    /// The hook returns at once while <see cref="On"/> is false, and its catch hands the exception to <see cref="Failed"/>.
    /// <see cref="On"/> is a field read while the feature runs, so it costs nothing in a hook called many times a frame.
    /// </remarks>
    internal sealed class FeatureGuard
    {
        private const int Burst = 3;
        private const double BurstSeconds = 10.0;
        private static readonly double[] OffSeconds = { 30.0, 120.0 };

        private readonly string name;
        private readonly object gate = new object();
        private long burstStart;
        private int inBurst, trips, total;

        /// <summary>Switched off until <see cref="backAt"/>, a Stopwatch timestamp (long.MaxValue: for good).</summary>
        private volatile bool off;
        private long backAt;

        public FeatureGuard(string name)
        {
            this.name = name;
        }

        /// <summary>The feature may run this frame.</summary>
        public bool On => !off || Back();

        /// <summary>From the hook's catch: <paramref name="context"/> heads the first exception's log line. True when this one switched it off.</summary>
        public bool Failed(Exception e, string context)
        {
            lock (gate)
            {
                if (total++ == 0)
                    Log.Error(e, context);
                if (off)
                    return false;
                long now = Stopwatch.GetTimestamp();
                if (inBurst == 0 || Seconds(burstStart, now) > BurstSeconds)
                {
                    burstStart = now;
                    inBurst = 0;
                }
                if (++inBurst < Burst)
                    return false;

                inBurst = 0;
                string seen = $"{Burst} exceptions in {Seconds(burstStart, now):0.0} s, the last {e.GetType().Name}: {e.Message}";
                if (trips < OffSeconds.Length)
                {
                    backAt = now + (long)(OffSeconds[trips] * Stopwatch.Frequency);
                    Log.Warn($"{name}: {seen}; switched off, back in {OffSeconds[trips]:0} s");
                }
                else
                {
                    backAt = long.MaxValue;
                    Log.Warn($"{name}: {seen}; switched off for the rest of the session");
                }
                trips++;
                off = true;
                return true;
            }
        }

        private bool Back()
        {
            if (Stopwatch.GetTimestamp() < backAt)
                return false;
            lock (gate)
            {
                if (!off)
                    return true;
                if (Stopwatch.GetTimestamp() < backAt)
                    return false;
                off = false;
            }
            Log.Info($"{name}: switched on again ({total} exceptions so far)");
            return true;
        }

        private static double Seconds(long from, long to) => (double)(to - from) / Stopwatch.Frequency;
    }
}
