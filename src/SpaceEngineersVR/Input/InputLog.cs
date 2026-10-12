// InputLog: the headset's button presses in the log, rate limited.
using System;
using Sandbox.Game.World;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The headset's buttons in the log: one line for each press and each release, with where it happened (which menu
    /// has focus, or on foot, in a seat), so a session that went wrong can be read back. Only buttons; the sticks and the
    /// analog trigger and grip are not logged. At most <see cref="MaxPerSecond"/> lines a second, whatever the hands do;
    /// the ones over are counted and reported on the first line of the next second. Off with RenderDebug InputLog=0.
    /// </summary>
    internal static class InputLog
    {
        /// <summary>Debug: the buttons are not logged (<see cref="Rendering.RenderDebug"/> InputLog=0).</summary>
        public static bool Off { get; set; }

        public const int MaxPerSecond = 10;

        /// <summary>Test seam: takes the lines instead of the log.</summary>
        internal static Action<string> Sink { get; set; }

        private static readonly VRButtons[] Buttons =
        {
            VRButtons.Trigger, VRButtons.Grip, VRButtons.StickClick, VRButtons.A, VRButtons.B, VRButtons.X, VRButtons.Y, VRButtons.Menu,
        };

        private static readonly Hand[] BothHands = { Hand.Left, Hand.Right };

        private static double windowStart = double.NegativeInfinity;
        private static int inWindow, skipped;

        /// <summary>Test seam: forgets the rate window, as a new session would start it.</summary>
        internal static void Reset()
        {
            windowStart = double.NegativeInfinity;
            inWindow = skipped = 0;
        }

        /// <summary>Game thread, once per frame after <see cref="VRInput.Update"/>: the buttons that changed this frame, with <paramref name="where"/> asked only if there is one.</summary>
        public static void Frame(double seconds, Func<string> where)
        {
            if (Off)
                return;
            Roll(seconds);
            string place = null;
            foreach (Hand hand in BothHands)
            {
                foreach (VRButtons button in Buttons)
                {
                    bool pressed = VRInput.IsNewPressed(hand, button);
                    if (!pressed && !VRInput.IsReleased(hand, button))
                        continue;
                    place = place ?? where();
                    Write(string.Format("Input: {0} {1} {2} ({3})", hand == Hand.Left ? "left" : "right", Name(button), pressed ? "pressed" : "released", place));
                }
            }
        }

        /// <summary>A line about the input that is not a button (a menu taking focus, the game asking for back); counts against the same rate limit.</summary>
        public static void Note(string line)
        {
            if (!Off)
                Write(line);
        }

        /// <summary>A new second starts a new window; the lines the last one dropped are reported first.</summary>
        private static void Roll(double seconds)
        {
            if (seconds - windowStart < 1d && seconds >= windowStart)
                return;
            windowStart = seconds;
            inWindow = 0;
            if (skipped > 0)
            {
                Emit($"Input: {skipped} more button lines skipped (at most {MaxPerSecond} a second)");
                inWindow = 1;
                skipped = 0;
            }
        }

        private static void Write(string line)
        {
            if (inWindow >= MaxPerSecond)
            {
                skipped++;
                return;
            }
            inWindow++;
            Emit(line);
        }

        private static void Emit(string line)
        {
            if (Sink != null)
                Sink(line);
            else
                Log.Info(line);
        }

        private static string Name(VRButtons button)
        {
            switch (button)
            {
                case VRButtons.StickClick: return "stick click";
                case VRButtons.Trigger: return "trigger";
                case VRButtons.Grip: return "grip";
                default: return button.ToString();
            }
        }
    }
}
