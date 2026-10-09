using System;
using System.Collections.Generic;
using SpaceEngineersVR.Input;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Which physical interaction holds each hand's grip. A grip that starts with the hand on something a feature can take
    /// (a hull to pull along, a holster, a door, a floating item) belongs to that feature until it is let go; meanwhile the
    /// grip's meaning in empty air (Use on the right hand, the toolbar wheel on the left on foot) is skipped.
    /// </summary>
    /// <remarks>
    /// Features register a <see cref="Taker"/> once. On a new press, the first caller of <see cref="Owner"/> that frame asks
    /// the takers in priority order (lowest first). So it does not matter whether a feature or VRControls looks first, as
    /// long as each taker decides from the hand's pose at call time. A taker must be cheap and must not throw. A throwing
    /// taker is logged once and skipped.
    /// </remarks>
    internal static class GripClaim
    {
        /// <summary>True: this feature takes the grip that just started on <paramref name="hand"/>.</summary>
        public delegate bool Taker(Hand hand);

        private sealed class Entry
        {
            public string Name;
            public int Priority;
            public Taker Take;
            public bool Failed;
        }

        private static readonly List<Entry> takers = new List<Entry>();
        private static readonly string[] owner = new string[2];
        private static readonly bool[] down = new bool[2], fresh = new bool[2];

        /// <summary>Adds a feature (once; a second call with the same name replaces it).</summary>
        public static void Register(string name, int priority, Taker take)
        {
            takers.RemoveAll(e => e.Name == name);
            takers.Add(new Entry { Name = name, Priority = priority, Take = take });
            takers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>The feature holding this hand's grip, or null: the grip means what it always has.</summary>
        public static string Owner(Hand hand)
        {
            int i = (int)hand;
            if (!VRInput.IsPressed(hand, VRButtons.Grip))
            {
                down[i] = false;
                fresh[i] = false;
                owner[i] = null;
                return null;
            }
            // A new press asks again even when nobody looked while the grip was up (VRControls only asks while it is down,
            // the wheel only on a press); asked once per press, however many callers that frame.
            bool isNew = VRInput.IsNewPressed(hand, VRButtons.Grip);
            if (!isNew)
                fresh[i] = false;
            if (!down[i] || (isNew && !fresh[i]))
            {
                down[i] = true;
                fresh[i] = isNew;
                owner[i] = null;
                foreach (Entry e in takers)
                {
                    if (e.Failed)
                        continue;
                    bool took;
                    try
                    {
                        took = e.Take(hand);
                    }
                    catch (Exception ex)
                    {
                        e.Failed = true;
                        Log.Error(ex, $"Grip: {e.Name} failed deciding a grab and is skipped from now on");
                        continue;
                    }
                    if (took)
                    {
                        owner[i] = e.Name;
                        Log.Info($"Grip: {hand} hand taken by {e.Name}");
                        break;
                    }
                }
            }
            return owner[i];
        }

        /// <summary>This feature holds the hand's grip now.</summary>
        public static bool Holds(Hand hand, string name) => Owner(hand) == name;

        /// <summary>Lets go before the button is released (the thing held went away); the grip then means nothing until pressed again.</summary>
        public static void Release(Hand hand, string name)
        {
            int i = (int)hand;
            if (owner[i] == name)
                owner[i] = "released";
        }
    }
}
