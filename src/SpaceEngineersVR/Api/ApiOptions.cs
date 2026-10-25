// ApiOptions: reads the options a SevrApi caller gives as names in one string ("Cursor, AtHand") (no game types; tests/ApiTests runs it).
using System;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// Options given to a SevrApi member as their public names, comma-separated, any case, spaces ignored: one string keeps a
    /// member's signature the same as options are added, and names read better in a caller's code than a row of bools.
    /// </summary>
    internal static class ApiOptions
    {
        /// <summary>
        /// Parses <paramref name="text"/> into bits: the name at index i of <paramref name="names"/> sets bit i. Empty or null is
        /// none. False, with <paramref name="why"/> for the caller's log, when a name is not one of them.
        /// </summary>
        public static bool TryParse(string text, string[] names, out int bits, out string why)
        {
            bits = 0;
            why = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;
            foreach (string part in text.Split(','))
            {
                string name = part.Trim();
                if (name.Length == 0)
                    continue;
                int index = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    string shown = name.Length > 32 ? name.Substring(0, 32) + ".." : name;
                    why = $"\"{shown}\" is not an option (known: {string.Join(", ", names)})";
                    bits = 0;
                    return false;
                }
                bits |= 1 << index;
            }
            return true;
        }
    }
}
