using System;
using System.Globalization;
using System.IO;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Debug aid: a head pose without a headset, to drive and check the headset paths on the desktop. Controlled by
    /// %APPDATA%\SpaceEngineers\SpaceEngineersVR.fakehead, re-read about once a second while the game runs, e.g.
    /// "yaw=40 pitch=-10 roll=0 shift=0.3" (degrees; shift moves the eyes' field of view off centre, in tangent units,
    /// + to the right). No file, no fake head.
    /// </summary>
    /// <remarks>
    /// height=1.65 (metres) gives the fake head a floor: its height above it. The tracking space's origin is then
    /// <see cref="OriginHeight"/> above the floor, as a headset's LOCAL space is, and the head sits at that height
    /// minus the origin's, in the origin's up. Without it the floor is not known and the head stays at the origin's height.
    /// x=0.3 z=0.3 (metres, the tracking space's own axes: + is right, + is BACK, as the head steps in the room) moves the head
    /// off the origin, to test how the body holds it (<see cref="Tracking.BodyGuard"/>: the head lock and the keep-out).
    /// </remarks>
    internal static class FakeHead
    {
        private static readonly string ControlFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "SpaceEngineersVR.fakehead");

        /// <summary>How high the fake tracking space's origin is above the floor, metres, when the fake head has a floor.</summary>
        public const float OriginHeight = 1.70f;

        private static float yaw, pitch, roll, shift, x, z;
        private static float? height;
        private static string lastText;

        public static bool Active { get; private set; }

        /// <summary>The fake origin's height above the floor, metres; null (floor unknown) without a height= in the control file.</summary>
        public static float? OriginAboveFloor => Active && height.HasValue ? OriginHeight : (float?)null;

        /// <summary>Head pose in the tracking space: roll, then pitch, then yaw, as a head turns, at the height set.</summary>
        public static Matrix Head
        {
            get
            {
                Matrix head = Matrix.CreateRotationZ(MathHelper.ToRadians(roll)) * Matrix.CreateRotationX(MathHelper.ToRadians(pitch))
                              * Matrix.CreateRotationY(MathHelper.ToRadians(yaw));
                if (height.HasValue)
                    head.M42 = height.Value - OriginHeight;
                head.M41 = x;
                head.M43 = z;
                return head;
            }
        }

        /// <param name="frame">Frame counter; the control file is read about once a second.</param>
        public static void Update(int frame)
        {
            if (frame % 60 == 1)
                Read();
        }

        /// <summary>The eyes' field of view from the game camera's projection, shifted; null to keep the game camera's.</summary>
        public static EnvMatrices.FovTangents? Fov(in Matrix projection)
        {
            if (!Active || shift == 0f)
                return null;
            float h = 1f / projection.M11, v = 1f / projection.M22;
            return new EnvMatrices.FovTangents { Left = -h + shift, Right = h + shift, Up = v, Down = -v };
        }

        private static void Read()
        {
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
            Apply(text);
        }

        /// <summary>Takes the control file's text (null: no file) as the fake head.</summary>
        internal static void Apply(string text)
        {
            if (text == lastText)
                return;
            lastText = text;

            Active = text != null;
            yaw = pitch = roll = shift = x = z = 0f;
            height = null;
            foreach (string part in (text ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=');
                if (pair.Length != 2 || !float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                    continue;
                switch (pair[0].ToLowerInvariant())
                {
                    case "yaw": yaw = value; break;
                    case "pitch": pitch = value; break;
                    case "roll": roll = value; break;
                    case "shift": shift = value; break;
                    case "height": height = value; break;
                    case "x": x = value; break;
                    case "z": z = value; break;
                }
            }
            Log.Info(Active ? $"Fake head: yaw {yaw}, pitch {pitch}, roll {roll}, field of view shifted {shift}, {x} m right, {z} m back, " +
                              (height.HasValue ? $"{height.Value} m above the floor" : "no floor") : "Fake head off");
        }
    }
}
