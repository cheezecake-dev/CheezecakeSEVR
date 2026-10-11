using System;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Looking at the gauntlet on the astronaut's left forearm: where its display is and when it counts as looked at. The
    /// display shows the status bars (<see cref="Rendering.WristStatus"/>, drawn on the gauntlet's own screen) and brightens
    /// by the show and hide angles below (<see cref="Measure"/> gives the two angles in the tracking space).
    /// The look does not open the wrist panel; Y does (<see cref="WristButton"/>). Nothing here touches the game, so it runs
    /// offline.
    /// </summary>
    /// <remarks>
    /// The gauntlet. SE_astronaut.mwm (and the female model: the same triangle counts) has no bone or dummy for a wrist
    /// device, but the LeftGlove mesh carries a raised plate that the RightGlove has no mirror of: 22 vertices and about
    /// 85 triangles with no counterpart across the body, on the back of the left forearm, a box 0.14 m long, 0.09 m
    /// wide, standing 2.5 cm proud of the glove (its top 0.075 m off the palm bone's line on the back-of-hand side,
    /// 0 to 0.14 m up the forearm from the palm bone). So the display side is the palm bone's +Y (the back of the hand and
    /// forearm), and its middle is <see cref="GauntletFromPalm"/> in the palm bone's frame (X toward the elbow, Y the back
    /// of the hand). The palm bone is where <see cref="Tracking.HandIK"/> puts the arm's palm, so the gauntlet is where
    /// the left controller's palm pose puts it: a wrist watch, seen by turning the forearm up with the back of the wrist
    /// toward the eyes.
    /// <para>
    /// Looked at: once the gaze is within <see cref="ShowGazeDegrees"/> of the gauntlet and the display side is within
    /// <see cref="ShowFaceDegrees"/> of facing the eyes, both for <see cref="ShowSeconds"/> on end. No longer: once the
    /// gaze is past <see cref="HideGazeDegrees"/> or the face turned past <see cref="HideFaceDegrees"/>, for
    /// <see cref="HideSeconds"/> on end. Between the two sets of angles nothing changes (hysteresis).
    /// </para>
    /// </remarks>
    internal static class WristLook
    {
        /// <summary>The middle of the gauntlet's display in the left palm bone's frame, metres: X toward the elbow, Y the back of the hand.</summary>
        public static readonly Vector3 GauntletFromPalm = new Vector3(0.065f, 0.075f, 0.008f);

        /// <summary>Looked at when the gaze is within this many degrees of the gauntlet...</summary>
        public const float ShowGazeDegrees = 20f;

        /// <summary>...and the display faces the eyes within this many degrees...</summary>
        public const float ShowFaceDegrees = 50f;

        /// <summary>...for this long (seconds).</summary>
        public const float ShowSeconds = 0.25f;

        /// <summary>No longer looked at when the gaze is past this many degrees from the gauntlet...</summary>
        public const float HideGazeDegrees = 35f;

        /// <summary>...or the display is turned this many degrees from the eyes...</summary>
        public const float HideFaceDegrees = 70f;

        /// <summary>...for this long (seconds).</summary>
        public const float HideSeconds = 0.4f;

        /// <summary>A gauntlet nearer the eyes than this (metres) has no direction to it worth the name.</summary>
        public const float NearestEye = 0.10f;

        /// <summary>
        /// The two angles of the rule, from the left palm bone's pose and the head's, both in the tracking space (row
        /// vectors, as <see cref="Tracking.HandIK.PalmTarget"/> and the tracked head have them). False when there is no
        /// direction to the gauntlet (a bad pose, or it is at the eyes).
        /// </summary>
        internal static bool Measure(Matrix palm, Matrix head, out float gazeDegrees, out float faceDegrees)
        {
            gazeDegrees = faceDegrees = 180f;
            if (!palm.IsValid() || !head.IsValid())
                return false;
            Vector3 at = Vector3.Transform(GauntletFromPalm, palm);
            Vector3 toGauntlet = at - head.Translation;
            float distance = toGauntlet.Length();
            if (!(distance > NearestEye) || float.IsInfinity(distance))
                return false;
            toGauntlet /= distance;
            Vector3 normal = Vector3.TransformNormal(Vector3.UnitY, palm);
            Vector3 gaze = head.Forward;
            if (!(normal.LengthSquared() > 1e-6f) || !(gaze.LengthSquared() > 1e-6f))
                return false;
            gazeDegrees = AngleDegrees(gaze, toGauntlet);
            faceDegrees = AngleDegrees(normal, -toGauntlet);
            return true;
        }

        private static float AngleDegrees(Vector3 a, Vector3 b)
        {
            float l = (float)Math.Sqrt(a.LengthSquared() * b.LengthSquared());
            return l < 1e-12f ? 180f : MathHelper.ToDegrees((float)Math.Acos(MathHelper.Clamp(Vector3.Dot(a, b) / l, -1f, 1f)));
        }
    }
}
