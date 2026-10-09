using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Debug aid: hands without controllers, to drive and check every control path on the desktop. Controlled by
    /// %APPDATA%\SpaceEngineers\SpaceEngineersVR.fakehands, re-read about four times a second while the game runs; no
    /// file, or a blank one, no hands. One setting per word (spaces or lines), hand first, values separated by commas:
    /// "left.stick=0,1 right.stick=1,0 right.trigger=1 left.squeeze=1 right.buttons=A,Grip" (stick x,y; trigger and
    /// squeeze 0..1; buttons are <see cref="VRButtons"/> names). A trigger or squeeze past half also presses its
    /// button. "right.aim=yaw,pitch[,x,y,z[,roll]]" and "left.grip=..." place a hand (degrees, + left and + up, metres in the
    /// tracking space the head is in; the roll, degrees, twists the pose about its own Y axis, the forearm's, before the pitch and yaw); the grip pose follows the aim pose unless set. Without a position a hand rests
    /// 0.2 m to its side, 0.3 m below and 0.35 m ahead of the head, turned with the head's yaw. Settings left out are
    /// neutral. The word "rest" alone turns the hands on with everything neutral.
    /// </summary>
    internal sealed class DesktopHands : IVRInputSource
    {
        private static readonly string ControlFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "SpaceEngineersVR.fakehands");

        private const int PollEveryFrames = 15;
        private const int HapticLogsPerSecond = 4;
        private const float PressedAt = 0.5f;
        private static readonly Vector3 Rest = new Vector3(0.2f, -0.3f, -0.35f);
        private static readonly char[] Separators = { ' ', '\t', '\r', '\n' };

        /// <summary>Where a hand is placed: degrees, and a position in the tracking space (null: the rest position).</summary>
        private struct Placement
        {
            public float Yaw, Pitch, Roll;
            public Vector3? Position;
        }

        private struct Setting
        {
            public Vector2 Stick;
            public float Trigger, Squeeze;
            public VRButtons Buttons;
            public Placement? Aim, Grip;
        }

        private readonly Setting[] hands = new Setting[2];
        private readonly long[] loggedAt = new long[2];
        private readonly float[] loggedAmplitude = new float[2], loggedSeconds = new float[2];
        private readonly int[] heldBack = new int[2];
        private string lastText;
        private int frame;

        public bool Active { get; private set; }

        /// <summary>Game thread, every frame before <see cref="VRInput.Update"/>: picks up changes to the control file.</summary>
        public void Poll()
        {
            if (frame++ % PollEveryFrames != 0)
                return;
            string text = null;
            try
            {
                if (File.Exists(ControlFile))
                    text = File.ReadAllText(ControlFile).Trim();
            }
            catch (IOException)
            {
                return;
            }
            if (text == lastText)
                return;
            lastText = text;
            Parse(text);
        }

        public void Read(out HandState left, out HandState right)
        {
            Matrix head = HeadTracker.TryGet(out Matrix tracked) ? tracked : Matrix.Identity;
            left = State(Hand.Left, head);
            right = State(Hand.Right, head);
        }

        public void ReadNow(out HandState left, out HandState right) => Read(out left, out right);

        /// <summary>
        /// Hands without controllers cannot buzz, so the pulse is logged, which is how a test sees it: "Haptic: Right 0.90 0.060s".
        /// A hand logs at most four a second, unless the pulse differs from the last one logged (a tool's buzz asks every frame;
        /// a shot, a click or a hit right after it still shows); the ones held back are counted in the next line of the same pulse.
        /// </summary>
        public void Haptic(Hand hand, float amplitude, float seconds)
        {
            if (!Active)
                return;
            int i = (int)hand;
            long now = Stopwatch.GetTimestamp();
            bool same = amplitude == loggedAmplitude[i] && seconds == loggedSeconds[i] && loggedAt[i] != 0;
            if (same && now - loggedAt[i] < Stopwatch.Frequency / HapticLogsPerSecond)
            {
                heldBack[i]++;
                return;
            }
            if (!same)
                heldBack[i] = 0;
            loggedAt[i] = now;
            loggedAmplitude[i] = amplitude;
            loggedSeconds[i] = seconds;
            Log.Info($"Haptic: {hand} {amplitude:0.00} {seconds:0.000}s" + (heldBack[i] > 0 ? $" ({heldBack[i]} similar held back)" : string.Empty));
            heldBack[i] = 0;
        }

        private HandState State(Hand hand, in Matrix head)
        {
            Setting setting = hands[(int)hand];
            Vector3 forward = head.Forward;
            float headYaw = (float)Math.Atan2(-forward.X, -forward.Z);
            Vector3 side = new Vector3(hand == Hand.Left ? -Rest.X : Rest.X, Rest.Y, Rest.Z);
            Vector3 rest = head.Translation + Vector3.TransformNormal(side, Matrix.CreateRotationY(headYaw));

            Matrix aim = Place(setting.Aim, rest);
            VRButtons buttons = setting.Buttons;
            if (setting.Trigger >= PressedAt)
                buttons |= VRButtons.Trigger;
            if (setting.Squeeze >= PressedAt)
                buttons |= VRButtons.Grip;
            return new HandState
            {
                Tracked = true,
                Aim = aim,
                Grip = setting.Grip.HasValue ? Place(setting.Grip, rest) : aim,
                Trigger = setting.Trigger,
                Squeeze = setting.Squeeze,
                Stick = setting.Stick,
                Buttons = buttons,
            };
        }

        /// <summary>Pitch first, then yaw, as a head turns (<see cref="Rendering.FakeHead"/>).</summary>
        private static Matrix Place(Placement? placement, Vector3 rest)
        {
            Placement p = placement ?? default;
            // The roll first, about the pose's own Y axis (a grip pose's Y runs back toward the wrist: the forearm's twist).
            Matrix pose = Matrix.CreateRotationY(MathHelper.ToRadians(p.Roll)) * Matrix.CreateRotationX(MathHelper.ToRadians(p.Pitch))
                          * Matrix.CreateRotationY(MathHelper.ToRadians(p.Yaw));
            pose.Translation = p.Position ?? rest;
            return pose;
        }

        private void Parse(string text)
        {
            hands[0] = hands[1] = default;
            var applied = new List<string>();
            foreach (string word in (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Apply(word))
                    applied.Add(word);
                else if (!word.Equals("rest", StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"Fake hands: ignored '{word}'");
            }
            Active = !string.IsNullOrEmpty(text);
            Log.Info(!Active ? "Fake hands off" : applied.Count > 0 ? "Fake hands: " + string.Join(" ", applied) : "Fake hands at rest");
        }

        /// <summary>One "hand.setting=values" word; false if it is not one.</summary>
        private bool Apply(string word)
        {
            int dot = word.IndexOf('.'), equals = word.IndexOf('=');
            if (dot < 1 || equals < dot + 2)
                return false;
            Hand hand;
            switch (word.Substring(0, dot).ToLowerInvariant())
            {
                case "left": hand = Hand.Left; break;
                case "right": hand = Hand.Right; break;
                default: return false;
            }
            string[] values = word.Substring(equals + 1).Split(',');
            ref Setting setting = ref hands[(int)hand];
            float[] numbers;
            switch (word.Substring(dot + 1, equals - dot - 1).ToLowerInvariant())
            {
                case "stick":
                    if (!Numbers(values, 2, 2, out numbers))
                        return false;
                    setting.Stick = new Vector2(MathHelper.Clamp(numbers[0], -1f, 1f), MathHelper.Clamp(numbers[1], -1f, 1f));
                    return true;
                case "trigger":
                    if (!Numbers(values, 1, 1, out numbers))
                        return false;
                    setting.Trigger = MathHelper.Clamp(numbers[0], 0f, 1f);
                    return true;
                case "squeeze":
                    if (!Numbers(values, 1, 1, out numbers))
                        return false;
                    setting.Squeeze = MathHelper.Clamp(numbers[0], 0f, 1f);
                    return true;
                case "buttons":
                    VRButtons buttons = VRButtons.None;
                    foreach (string name in values)
                    {
                        if (!Enum.TryParse(name, ignoreCase: true, out VRButtons button))
                            return false;
                        buttons |= button;
                    }
                    setting.Buttons = buttons;
                    return true;
                case "aim":
                    return Pose(values, out setting.Aim);
                case "grip":
                    return Pose(values, out setting.Grip);
                default:
                    return false;
            }
        }

        private static bool Pose(string[] values, out Placement? placement)
        {
            placement = null;
            if ((values.Length != 2 && values.Length != 5 && values.Length != 6) || !Numbers(values, values.Length, values.Length, out float[] n))
                return false;
            placement = new Placement
            {
                Yaw = n[0], Pitch = n[1], Roll = values.Length == 6 ? n[5] : 0f,
                Position = values.Length >= 5 ? new Vector3(n[2], n[3], n[4]) : (Vector3?)null,
            };
            return true;
        }

        private static bool Numbers(string[] values, int min, int max, out float[] numbers)
        {
            numbers = new float[values.Length];
            if (values.Length < min || values.Length > max)
                return false;
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                    return false;
            }
            return true;
        }
    }
}
