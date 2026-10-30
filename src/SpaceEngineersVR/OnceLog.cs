using System;
using System.Collections.Generic;

namespace SpaceEngineersVR
{
    /// <summary>
    /// For a read made every frame that has a safe answer when it throws (a greyed entry, an empty line, the last value): the
    /// first exception of each site is logged in full and the rest are not, so a broken entry shows in the log without a line a
    /// frame. A site is a fixed name written at the catch; the set is bounded (<see cref="MaxSites"/>), so a name built from data
    /// cannot grow it. What the read answers instead stays the caller's. A feature that stays off after repeated failures is
    /// <see cref="FeatureGuard"/>'s, not this.
    /// </summary>
    internal static class OnceLog
    {
        /// <summary>Sites remembered; any further ones are not logged.</summary>
        internal const int MaxSites = 256;

        private static readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>From a catch: logs <paramref name="e"/> under <paramref name="context"/> the first time <paramref name="site"/> fails; false when it was logged before.</summary>
        /// <param name="detail">Which one failed (an entry's name, say), appended to the first line only; passed as it is, so a repeat costs no string.</param>
        public static bool Error(string site, Exception e, string context, string detail = null)
        {
            lock (seen)
            {
                if (seen.Count >= MaxSites || !seen.Add(site))
                    return false;
            }
            Log.Error(e, detail == null ? context : $"{context} ({detail})");
            return true;
        }
    }
}
