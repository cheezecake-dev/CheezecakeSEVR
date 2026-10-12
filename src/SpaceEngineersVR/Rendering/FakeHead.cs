using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// A head pose without a headset, to drive and check the headset paths on the desktop. Only the test aids set it
    /// (src/SpaceEngineersVR.TestAids/FakeHead.TestAids.cs, from the fakehead control file); without them it is never active.
    /// </summary>
    internal static partial class FakeHead
    {
        /// <summary>How high the fake tracking space's origin is above the floor, metres, when the fake head has a floor.</summary>
        public const float OriginHeight = 1.70f;

        public static bool Active { get; private set; }

        /// <summary>
        /// The fake head is the game's head even with a headset live, in place of the headset's own (the eyes are still the
        /// headset's). Without it a live headset's head wins.
        /// </summary>
        public static bool Overrides { get; private set; }

        /// <summary>The fake origin's height above the floor, metres; null when the fake head has no floor.</summary>
        public static float? OriginAboveFloor { get; private set; }

        /// <summary>Head pose in the tracking space.</summary>
        public static Matrix Head { get; private set; } = Matrix.Identity;

        /// <summary>How far the eyes' field of view is moved off centre, in tangent units, + to the right.</summary>
        private static float Shift { get; set; }

        /// <summary>Picks up the test aids' fake head, if they are compiled in.</summary>
        /// <param name="frame">Frame counter; the control file is read about once a second.</param>
        public static void Update(int frame) => Poll(frame);

        static partial void Poll(int frame);

        /// <summary>The eyes' field of view from the game camera's projection, shifted; null to keep the game camera's.</summary>
        public static EnvMatrices.FovTangents? Fov(in Matrix projection)
        {
            if (!Active || Shift == 0f)
                return null;
            float h = 1f / projection.M11, v = 1f / projection.M22;
            return new EnvMatrices.FovTangents { Left = -h + Shift, Right = h + Shift, Up = v, Down = -v };
        }
    }
}
