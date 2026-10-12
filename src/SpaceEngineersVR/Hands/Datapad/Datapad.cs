using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// A datapad stowed on the left forearm, just above the gauntlet's screen: the right hand's grip there pulls it off into
    /// that hand, where it is held up to read and touched with the other hand's fingertip (<see cref="PanelTouch"/>) or pointed
    /// at with its laser (<see cref="Laser"/>). What it shows is <see cref="DatapadScreen"/>: chat, the GPS list and the
    /// terminal. Letting go near the forearm puts it back; letting go elsewhere leaves it standing in the air for
    /// <see cref="FloatSeconds"/> (touched meanwhile, it stays), after which it flies back to the forearm. The free hand's grip
    /// on it takes it into that hand. A chat line that arrives while it is stowed lights the forearm and ticks the controller
    /// (<see cref="Datapad"/>'s indicator).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pad is the GUI panel (<see cref="HudPanel"/>), the same OpenXR layer menus use, so its text is the panel's own
    /// resolution and the laser's beam layer still draws over it: while it is out, <see cref="WorldMenu"/> poses the panel at
    /// the pad (<see cref="TryGetPanel"/>), whatever screen has the focus (the pad's own, the keyboard over it, or the terminal
    /// opened from it). The pad faces the eyes with its top toward the top of the head, held by its outer edge: it does not
    /// depend on how the controller is rolled, and it reads the same wherever it is held.
    /// </para>
    /// <para>
    /// The game has no such thing, so its place is the player's own arm, in the tracking space: <see cref="ForearmFromPalm"/>
    /// back along the forearm of the hand that does not hold the tool (<see cref="StowHand"/>) from the grip pose. The grip there is claimed through <see cref="GripClaim"/> at
    /// <see cref="ClaimPriority"/>: after push to talk at the mouth, before the helmet, floating items and grab to use.
    /// </para>
    /// </remarks>
    internal static partial class Datapad
    {
        /// <summary>Debug: no datapad (<see cref="RenderDebug"/> Datapad=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The setting is on and the debug file has not turned it off.</summary>
        public static bool Enabled => !Off && VRSettings.Datapad;

        /// <summary>The name the grip is claimed under.</summary>
        public const string ClaimName = "Datapad";

        /// <summary>After push to talk (8), before the helmet (10): a grip on the forearm is not a grip on the head.</summary>
        public const int ClaimPriority = 9;

        /// <summary>
        /// The arm it is stowed on: the one that does not hold the tool, so the hand that holds a gun or welder takes it. The
        /// left, with the gauntlet's screen (<see cref="WristButton.WristHand"/>); left-handed, the right.
        /// </summary>
        public static Hand StowHand => VRSettings.DominantHand == Hand.Left ? Hand.Right : Hand.Left;

        // ---- Where it is and how it moves; metres and seconds ----

        /// <summary>The stowed pad's middle, back along the forearm from the grip pose: on the left arm, just above the gauntlet's screen.</summary>
        public const float ForearmFromPalm = 0.17f;

        /// <summary>A grip within this of the stowed pad takes it.</summary>
        public const float ForearmReach = 0.09f;

        /// <summary>Let go within this of the stowed place, it goes back there.</summary>
        public const float StowReach = 0.12f;

        /// <summary>A grip this far round the pad's edges, and this near its plane, takes it.</summary>
        public const float EdgeReach = 0.04f, PlaneReach = 0.06f;

        /// <summary>The holding hand's grip is this far in from the pad's outer edge, and this far below its middle.</summary>
        public const float HoldInset = 0.015f, HoldDrop = 0.02f;

        /// <summary>How wide the stowed pad is drawn as it comes off the arm and goes back.</summary>
        public const float StowWidth = 0.05f;

        public const double PullSeconds = 0.18, ReturnSeconds = 0.25, FloatSeconds = 6.0;

        public const float TickAmplitude = HeadGestures.TickAmplitude, TickSeconds = HeadGestures.TickSeconds;

        internal enum Phase
        {
            Stowed,

            /// <summary>Coming off the forearm into the hand.</summary>
            Pulling,

            Held,

            /// <summary>Let go away from the forearm: standing where it was left.</summary>
            Floating,

            /// <summary>Flying back to the forearm.</summary>
            Returning,
        }

        /// <summary>What the render thread needs to pose the pad, published by the game thread.</summary>
        private struct Shown
        {
            public Phase Phase;
            public Hand Holder;
            public long Since;

            /// <summary>Floating: where it stands; returning: where it started from.</summary>
            public Matrix From;

            public float FromWidth;
        }

        private static readonly object gate = new object();
        private static Shown shown;
        private static readonly Matrix[] grips = new Matrix[2];
        private static readonly bool[] gripsTracked = new bool[2];

        // Game thread.
        private static Phase phase;
        private static Hand holder;
        private static long phaseAt, touchedAt;
        private static DatapadScreen screen;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(Datapad));

        /// <summary>A test aid holds it: held without a grip, until the test aid lets go.</summary>
        private static bool driveHold;

        /// <summary>The phase, for the drive and the log.</summary>
        internal static Phase Now => phase;

        /// <summary>The hand that holds it, while it is held.</summary>
        internal static Hand? Holder => phase == Phase.Pulling || phase == Phase.Held ? holder : (Hand?)null;

        /// <summary>It is out of the stowed place: the screen is up and the panel is the pad.</summary>
        internal static bool Out => phase != Phase.Stowed;

        public static void Patch(Harmony harmony)
        {
            GripClaim.Register(ClaimName, ClaimPriority, Take);
            Log.Info($"Datapad: stowed {ForearmFromPalm * 100:0} cm up the {StowHand.ToString().ToLowerInvariant()} forearm from the grip; " +
                     $"the other grip within {ForearmReach * 100:0} cm takes it (grip claim at priority {ClaimPriority})");
            PatchRegistry.Apply("datapad indicator", () =>
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(Datapad), nameof(DrawIndicator))),
                "Datapad: the forearm's new-message light could not be hooked; the controller still ticks");
        }

        // ---- Game thread ----

        /// <summary>Game thread, once per frame after the hands were read (InputPatches.FrameStart).</summary>
        public static void Update()
        {
            if (!guard.On)
                return;
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                guard.Failed(e, "Datapad failed this frame");
            }
        }

        private static void Frame()
        {
            for (int i = 0; i < 2; i++)
            {
                HandState state = VRInput.Get((Hand)i);
                lock (gate)
                {
                    grips[i] = state.Grip;
                    gripsTracked[i] = VRInput.Active && state.Tracked;
                }
            }

            if (!Enabled || !VRInput.Active && !driveHold)
            {
                if (phase != Phase.Stowed)
                    Stow(Enabled ? "the hands went away" : "switched off");
                UpdateIndicator();
                return;
            }

            // The screen went away without the pad being put back: B, Escape, or the game closing it.
            if (phase != Phase.Stowed && (screen == null || Gone(screen)))
            {
                Stow("its screen was closed");
                UpdateIndicator();
                return;
            }

            // Asked here as well as by VRControls, so a grip on the pad is noticed while its screen has the focus.
            GripClaim.Owner(Hand.Left);
            GripClaim.Owner(Hand.Right);

            double since = Seconds(phaseAt);
            switch (phase)
            {
                case Phase.Pulling:
                case Phase.Held:
                    if (!driveHold && !GripClaim.Holds(holder, ClaimName))
                        Release();
                    else if (phase == Phase.Pulling && since >= PullSeconds)
                        Enter(Phase.Held);
                    break;

                case Phase.Floating:
                    if (Seconds(Math.Max(phaseAt, touchedAt)) >= FloatSeconds)
                    {
                        Log.Info($"Datapad: left standing {FloatSeconds:0} s untouched; back to the forearm");
                        Return();
                    }
                    break;

                case Phase.Returning:
                    if (since >= ReturnSeconds)
                        Stow(null);
                    break;
            }
            UpdateIndicator();
        }

        /// <summary>
        /// A grip has just started. On the stowed pad (the other hand, the game in play): it comes off into that hand. On the
        /// pad standing or flying back (either hand), or held in the other (the free hand): it goes into this one.
        /// </summary>
        private static bool Take(Hand hand)
        {
            if (!Enabled || !VRInput.Active || ToolbarWheel.Owns)
                return false;
            switch (phase)
            {
                case Phase.Stowed:
                    if (hand == StowHand || !gripsTracked[(int)hand] || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay) || !TryForearm(out Vector3 stowAt))
                        return false;
                    float distance = Vector3.Distance(grips[(int)hand].Translation, stowAt);
                    if (distance > ForearmReach)
                        return false;
                    Pull(hand, distance);
                    return true;

                case Phase.Pulling:
                case Phase.Held:
                    if (hand == holder || driveHold || !OnPad(hand))
                        return false;
                    Log.Info($"Datapad: handed from the {Name(holder)} hand to the {Name(hand)}");
                    GripClaim.Release(holder, ClaimName);
                    Haptics.Pulse(holder, TickAmplitude, TickSeconds);
                    holder = hand;
                    Haptics.Pulse(hand, TickAmplitude, TickSeconds);
                    Enter(Phase.Held);
                    return true;

                default:
                    if (!OnPad(hand))
                        return false;
                    Log.Info($"Datapad: taken back into the {Name(hand)} hand ({(phase == Phase.Floating ? "standing" : "on its way back")})");
                    holder = hand;
                    Haptics.Pulse(hand, TickAmplitude, TickSeconds);
                    Enter(Phase.Held);
                    return true;
            }
        }

        /// <summary>Off the forearm into <paramref name="hand"/>: the screen goes up, the pad grows into the hand.</summary>
        private static void Pull(Hand hand, float distance)
        {
            screen = new DatapadScreen(Unread);
            MyGuiSandbox.AddScreen(screen);
            holder = hand;
            ClearUnread();
            Haptics.Pulse(hand, TickAmplitude, TickSeconds);
            Haptics.Pulse(StowHand, TickAmplitude, TickSeconds);
            Enter(Phase.Pulling);
            Log.Info($"Datapad: pulled off the forearm by the {Name(hand)} grip ({distance * 100:0.0} cm from it), {VRSettings.DatapadWidth:0.00} m wide");
        }

        /// <summary>The holding hand let go: back on the forearm if it was near, else it stands where it is.</summary>
        private static void Release()
        {
            driveHold = false;
            Haptics.Pulse(holder, TickAmplitude, TickSeconds);
            if (TryForearm(out Vector3 stowAt) && Vector3.Distance(grips[(int)holder].Translation, stowAt) <= StowReach)
            {
                Log.Info($"Datapad: let go at the forearm ({Vector3.Distance(grips[(int)holder].Translation, stowAt) * 100:0} cm); stowed");
                Return();
                return;
            }
            if (!HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out _))
            {
                Return();
                return;
            }
            lock (gate)
            {
                shown.From = pose;
                shown.FromWidth = 2f * halfWidth;
            }
            touchedAt = 0;
            Enter(Phase.Floating);
            Log.Info($"Datapad: let go by the {Name(holder)} hand away from the forearm; it stands there {FloatSeconds:0} s");
        }

        /// <summary>Flies back to the forearm from where it is shown now; the screen closes as it gets there.</summary>
        private static void Return()
        {
            Matrix from;
            float width;
            if (HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out _))
            {
                from = pose;
                width = 2f * halfWidth;
            }
            else
            {
                Stow(null);
                return;
            }
            lock (gate)
            {
                shown.From = from;
                shown.FromWidth = width;
            }
            Enter(Phase.Returning);
        }

        /// <summary>Back on the forearm at once: the screen closes. <paramref name="why"/> is logged when it is not the usual way.</summary>
        private static void Stow(string why)
        {
            if (phase != Phase.Stowed)
            {
                Haptics.Pulse(StowHand, TickAmplitude, TickSeconds);
                Log.Info(why == null ? "Datapad: back on the forearm" : $"Datapad: back on the forearm ({why})");
            }
            if (Holder.HasValue)
                GripClaim.Release(holder, ClaimName);
            driveHold = false;
            DatapadScreen closing = screen;
            screen = null;
            Enter(Phase.Stowed);
            if (closing != null && !Gone(closing))
                closing.CloseScreen();
        }

        /// <summary>From the pad's screen as it closes: if the pad did not close it, it goes back to the forearm.</summary>
        internal static void ScreenClosed(DatapadScreen closed)
        {
            if (ReferenceEquals(closed, screen))
                Stow("its screen was closed");
        }

        private static void Enter(Phase next)
        {
            phase = next;
            phaseAt = Stopwatch.GetTimestamp();
            lock (gate)
            {
                shown.Phase = next;
                shown.Holder = holder;
                shown.Since = phaseAt;
            }
        }

        /// <summary>A fingertip or the laser used the pad: one standing in the air stays a while longer.</summary>
        internal static void Touched() => touchedAt = Stopwatch.GetTimestamp();

        /// <summary>The hand's grip is near the pad as it was last shown: within its edges and <see cref="EdgeReach"/>, near its plane.</summary>
        private static bool OnPad(Hand hand)
        {
            if (!gripsTracked[(int)hand] || !HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out float halfHeight))
                return false;
            Vector3 p = Vector3.Transform(grips[(int)hand].Translation, Matrix.Invert(pose));
            return Math.Abs(p.Z) <= PlaneReach && Math.Abs(p.X) <= halfWidth + EdgeReach && Math.Abs(p.Y) <= halfHeight + EdgeReach;
        }

        private static bool Gone(MyGuiScreenBase s) => s.State == MyGuiScreenState.CLOSING || s.State == MyGuiScreenState.CLOSED;

        private static double Seconds(long since) => since == 0 ? double.MaxValue : (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;

        private static string Name(Hand hand) => hand.ToString().ToLowerInvariant();

        internal static Hand Other(Hand hand) => hand == Hand.Left ? Hand.Right : Hand.Left;

        // ---- What the other input reads ----

        /// <summary>
        /// The hand that points the laser: the free one while the pad is held, so the hand holding it does not point; else
        /// <paramref name="dominant"/>.
        /// </summary>
        internal static Hand LaserHand(Hand dominant) => Holder.HasValue ? Other(holder) : dominant;

        /// <summary>The pad holds this hand's grip (the hand holds it, or has just taken it): the grip is not the laser's right button.</summary>
        internal static bool OwnsGrip(Hand hand) => phase != Phase.Stowed && GripClaim.Holds(hand, ClaimName);

        /// <summary>
        /// The hands whose fingertips touch the pad (<see cref="PanelTouch"/>): the free hand while it is held, both while it
        /// stands in the air. False while it is stowed, flying, or not the panel's (a screen of its own is not up).
        /// </summary>
        internal static bool TryTouchHands(out Hand first, out Hand? second)
        {
            first = Hand.Right;
            second = null;
            if (!Enabled || screen == null || PanelTouch.InjectedTip == null && !VRInput.Active)
                return false;
            switch (phase)
            {
                case Phase.Pulling:
                case Phase.Held:
                    first = Other(holder);
                    return true;
                case Phase.Floating:
                    first = VRSettings.DominantHand;
                    second = Other(first);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Where the stowed pad's middle is, from the forearm's grip pose (game thread's snapshot).</summary>
        private static bool TryForearm(out Vector3 at)
        {
            at = default;
            if (!gripsTracked[(int)StowHand])
                return false;
            at = ForearmPoint(grips[(int)StowHand]);
            return true;
        }

        /// <summary>The stowed pad's middle: <see cref="ForearmFromPalm"/> back along the forearm (the grip's own +Z) from the grip.</summary>
        internal static Vector3 ForearmPoint(in Matrix grip)
        {
            Vector3 along = grip.Backward;
            float length = along.Length();
            return length > 0.5f ? grip.Translation + along / length * ForearmFromPalm : grip.Translation;
        }
    }
}
