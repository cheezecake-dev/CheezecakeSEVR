using System;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Looking at the gauntlet on the astronaut's left forearm opens the wrist panel (<see cref="WristButton"/>, the same
    /// quick-actions screen Y opens); looking away closes it. This is the rule and the geometry; <see cref="WristButton"/>
    /// feeds it the frame and opens and closes the screen. Nothing here touches the game, so it runs offline.
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
    /// The rule. It shows once the gaze is within <see cref="ShowGazeDegrees"/> of the gauntlet and the display side is
    /// within <see cref="ShowFaceDegrees"/> of facing the eyes, both for <see cref="ShowSeconds"/> on end. It hides once
    /// the gaze is past <see cref="HideGazeDegrees"/> or the face turned past <see cref="HideFaceDegrees"/>, for
    /// <see cref="HideSeconds"/> on end. Between the two sets of angles nothing changes (hysteresis). A panel that closes
    /// any other way (a button on it, Y) stays closed until the gaze has left (the hide angles) once, so a button pressed
    /// while looking at the wrist does not bring the panel straight back.
    /// </para>
    /// <para>
    /// Never a surprise: not on a seat or turret (a seat's controls have the hands: the stick, the throttle, Y), not
    /// while a menu is up, not while the left grip is down (every GripClaim hold, the toolbar wheel's press and a fist are
    /// that), not while the arm is not on the hand (third person, the spectator camera, a failed solve), not with
    /// Left-handed on (the left hand then points the laser and cannot press its own wrist), and not while another
    /// quick-actions panel (Y's) is up, which it leaves alone.
    /// </para>
    /// </remarks>
    internal static class WristLook
    {
        /// <summary>Debug: the gauntlet does not open the panel (<see cref="Rendering.RenderDebug"/> WristLook=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The look opens the panel: not switched off by the debug file or the "Wrist panel on look" setting.</summary>
        public static bool Enabled => !Off && VRSettings.WristLook;

        /// <summary>The middle of the gauntlet's display in the left palm bone's frame, metres: X toward the elbow, Y the back of the hand.</summary>
        public static readonly Vector3 GauntletFromPalm = new Vector3(0.065f, 0.075f, 0.008f);

        /// <summary>Shows when the gaze is within this many degrees of the gauntlet...</summary>
        public const float ShowGazeDegrees = 20f;

        /// <summary>...and the display faces the eyes within this many degrees...</summary>
        public const float ShowFaceDegrees = 50f;

        /// <summary>...for this long (seconds).</summary>
        public const float ShowSeconds = 0.25f;

        /// <summary>Hides when the gaze is past this many degrees from the gauntlet...</summary>
        public const float HideGazeDegrees = 35f;

        /// <summary>...or the display is turned this many degrees from the eyes...</summary>
        public const float HideFaceDegrees = 70f;

        /// <summary>...for this long (seconds).</summary>
        public const float HideSeconds = 0.4f;

        /// <summary>A gauntlet nearer the eyes than this (metres) has no direction to it worth the name.</summary>
        public const float NearestEye = 0.10f;

        /// <summary>A frame is counted as no longer than this (seconds): a hitch (a load) must not stand for a dwell.</summary>
        public const float LongestStep = 0.1f;

        internal enum Change
        {
            None,
            Show,
            Hide,
        }

        /// <summary>What the rule remembers between frames.</summary>
        internal struct State
        {
            /// <summary>The panel is up because of the look.</summary>
            public bool Shown;

            /// <summary>The panel was closed some other way (or by the look) while the gaze was still on it: no show until the gaze has left.</summary>
            public bool Latched;

            /// <summary>Seconds the show conditions (hidden) or the hide conditions (shown) have held without a break.</summary>
            public float Held;

            /// <summary>Why the last hide happened, for the log.</summary>
            public string Why;
        }

        /// <summary>What the rule is told each frame.</summary>
        internal struct Inputs
        {
            /// <summary><see cref="Enabled"/>.</summary>
            public bool Enabled;

            /// <summary>The Left-handed setting: the left hand points the laser, so it cannot press its own wrist.</summary>
            public bool LeftHanded;

            public VRContext Context;

            /// <summary>The gameplay screen has the focus: no menu is up (the look's own panel not counted: it is <see cref="PanelUp"/>).</summary>
            public bool GameplayFocus;

            /// <summary>The panel the look opened is up.</summary>
            public bool PanelUp;

            /// <summary>A panel the look did not open (Y's) is up.</summary>
            public bool OtherPanelUp;

            /// <summary>The left hand is busy: its grip is down.</summary>
            public bool HandBusy;

            /// <summary>The astronaut's left arm is on the left controller this frame (first person; HandIK solved it).</summary>
            public bool ArmFollows;

            /// <summary>The head and the hand are tracked and <see cref="Gaze"/> and <see cref="Face"/> are good.</summary>
            public bool Measured;

            /// <summary>Degrees between where the head looks and the gauntlet.</summary>
            public float Gaze;

            /// <summary>Degrees between the gauntlet's display side and the way to the eyes.</summary>
            public float Face;
        }

        /// <summary>The control sets the panel may be opened in: on foot and on the jetpack, as Y's is.</summary>
        internal static bool Free(VRContext context) => context == VRContext.OnFoot || context == VRContext.Jetpack;

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

        /// <summary>
        /// One frame of the rule: whether the panel is to be shown or hidden now. <paramref name="seconds"/> is the time since
        /// the last frame.
        /// </summary>
        internal static Change Decide(ref State s, Inputs i, float seconds)
        {
            float dt = Math.Min(Math.Max(seconds, 0f), LongestStep);
            bool away = !i.Measured || i.Gaze > HideGazeDegrees || i.Face > HideFaceDegrees;
            bool looking = i.Measured && i.Gaze <= ShowGazeDegrees && i.Face <= ShowFaceDegrees;

            // Y's panel is up (or just closing): it is not ours, and it is left as it is. A look that is still on the wrist
            // must not open the panel again the moment it closes.
            if (i.OtherPanelUp)
            {
                s.Shown = false;
                s.Held = 0f;
                s.Latched = !away;
                return Change.None;
            }

            if (s.Shown)
            {
                // Closed some other way: a button on it, Y, the game.
                if (!i.PanelUp)
                {
                    s.Shown = false;
                    s.Held = 0f;
                    s.Latched = !away;
                    return Change.None;
                }
                // Switched off, or no longer free to open it (sat down, died): at once.
                if (!i.Enabled)
                    return Hide(ref s, away, "switched off");
                if (!Free(i.Context))
                    return Hide(ref s, away, "no longer on foot or the jetpack");
                if (!away)
                {
                    s.Held = 0f;
                    return Change.None;
                }
                s.Held += dt;
                if (s.Held < HideSeconds)
                    return Change.None;
                return Hide(ref s, away, !i.Measured ? "the head or hand is not tracked" : i.Gaze > HideGazeDegrees ? "gaze left the gauntlet" : "gauntlet turned from the eyes");
            }

            if (away)
                s.Latched = false;
            if (s.Latched || !MayShow(i) || !looking)
            {
                s.Held = 0f;
                return Change.None;
            }
            s.Held += dt;
            if (s.Held < ShowSeconds)
                return Change.None;
            s.Shown = true;
            s.Held = 0f;
            s.Why = null;
            return Change.Show;
        }

        /// <summary>Everything but the gaze that has to be true for the look to open the panel.</summary>
        internal static bool MayShow(Inputs i) =>
            i.Enabled && !i.LeftHanded && Free(i.Context) && i.GameplayFocus && !i.PanelUp && !i.OtherPanelUp && !i.HandBusy && i.ArmFollows && i.Measured;

        private static Change Hide(ref State s, bool away, string why)
        {
            s.Shown = false;
            s.Held = 0f;
            s.Latched = !away;
            s.Why = why;
            return Change.Hide;
        }

        private static float AngleDegrees(Vector3 a, Vector3 b)
        {
            float l = (float)Math.Sqrt(a.LengthSquared() * b.LengthSquared());
            return l < 1e-12f ? 180f : MathHelper.ToDegrees((float)Math.Acos(MathHelper.Clamp(Vector3.Dot(a, b) / l, -1f, 1f)));
        }
    }
}
