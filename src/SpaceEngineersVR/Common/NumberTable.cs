// NumberTable: a few numbers kept per key in one line of settings text, as the hidden settings keep them.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// The text of a hidden setting that keeps a few numbers for each of several keys (a controller profile, a kind of
    /// cockpit): "key:n,n,n;key:n,n,n", the numbers in the invariant culture. Used by <see cref="Tracking.GripFit"/> and the
    /// datapad's seat spots (<see cref="Hands.Datapad"/>).
    /// </summary>
    internal static class NumberTable
    {
        /// <summary>
        /// The entries of <paramref name="text"/> that have exactly <paramref name="count"/> finite numbers, by key (case
        /// aside); every other entry, as written, goes into <paramref name="skipped"/> when it is given, for the log.
        /// </summary>
        public static Dictionary<string, float[]> Parse(string text, int count, List<string> skipped = null)
        {
            var table = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in (text ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // The last colon: the numbers have none, a key might.
                int colon = entry.LastIndexOf(':');
                string[] numbers = colon > 0 ? entry.Substring(colon + 1).Split(',') : null;
                var values = new float[count];
                bool good = numbers != null && numbers.Length == count;
                for (int i = 0; good && i < count; i++)
                    good = float.TryParse(numbers[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                           && !float.IsNaN(values[i]) && !float.IsInfinity(values[i]);
                string key = good ? entry.Substring(0, colon).Trim() : null;
                if (string.IsNullOrEmpty(key))
                {
                    skipped?.Add(entry.Trim());
                    continue;
                }
                table[key] = values;
            }
            return table;
        }

        /// <summary>The text of <paramref name="table"/>, its keys in order (case aside), each number as <paramref name="format"/> writes it.</summary>
        public static string Format(IEnumerable<KeyValuePair<string, float[]>> table, string format = "0.###")
        {
            var text = new StringBuilder();
            foreach (KeyValuePair<string, float[]> entry in table.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (text.Length > 0)
                    text.Append(';');
                text.Append(entry.Key).Append(':')
                    .Append(string.Join(",", entry.Value.Select(value => value.ToString(format, CultureInfo.InvariantCulture))));
            }
            return text.ToString();
        }
    }
}
