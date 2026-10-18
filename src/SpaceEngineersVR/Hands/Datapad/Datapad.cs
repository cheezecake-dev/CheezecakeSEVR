using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// A datapad, taken out from the wrist panel's Datapad tile (<see cref="QuickActionsScreen"/>, on foot and in a seat): the
    /// panel turns into the pad in the hand it was on (<see cref="OutHand"/>), where it stays without the grip held, and is
    /// touched with the other hand's fingertip (<see cref="PanelTouch"/>) or pointed at with its laser (<see cref="Laser"/>).
    /// What it shows is <see cref="DatapadScreen"/>: chat, the GPS list, the ship (a hologram of it standing on the pad,
    /// <see cref="ShipModel"/>) and the terminal. The other hand's grip on it takes it into that hand; a hand that grips it
    /// and lets go leaves it standing in the air for <see cref="FloatSeconds"/> (touched meanwhile, it stays), after which it
    /// is put away; in a seat it stays where it was let go, fixed to the seat. Y, B or its screen closing put it away too: it
    /// goes back into the wrist it came from. A chat line that arrives while it is away ticks that hand's controller and
    /// marks the tile (<see cref="Unread"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pad is the game's own datapad (<see cref="DatapadModel"/>), its screen showing the GUI panel (<see cref="HudPanel"/>)
    /// that the pad's screens are drawn into; until the model is read, or without it, the pad is that panel itself, the same
    /// OpenXR layer menus use. Either way, while it is out <see cref="WorldMenu"/> poses the panel at the pad
    /// (<see cref="TryGetPanel"/>), whatever screen has the focus (the pad's own, the keyboard over it, or the terminal opened
    /// from it). The pad faces the eyes with its top toward the top of the head, held by its outer edge: it does not depend on
    /// how the controller is rolled, and it reads the same wherever it is held.
    /// </para>
    /// <para>
    /// In a seat the pad let go stays where it was let go, for as long as the player sits there: it is kept in the pilot's
    /// own space, which the seat carries, and placed in the tracking space from it each frame by the move the arms are solved
    /// by (<see cref="Tracking.HandWorld.TryGetTrackingToBody"/>), so it rides with the ship and stays put in the cockpit when the view
    /// is recentred. Each kind of seat (its block's subtype) remembers the spot in the settings file
    /// (<see cref="VRSettings.DatapadSeatSpots"/>): taken out again in that kind of seat, the pad comes out to it. Put away
    /// from the hand there instead, it comes out into the hand again. Leaving the seat sends a pad standing in it back into
    /// the wrist.
    /// </para>
    /// <para>
    /// Nothing is kept on the arm: the left forearm carries the gauntlet's status screen (<see cref="WristStatus"/>), which a
    /// pad there would cover. The grip on the pad is claimed through <see cref="GripClaim"/> at <see cref="ClaimPriority"/>,
    /// and only while it is out, so a grip anywhere else keeps its own job.
    /// </para>
    /// </remarks>
    internal static partial class Datapad
    {
        /// <summary>
        /// The datapad is shelved until it is reworked (it could not be used in the world as the gauntlet's status screen can,
        /// and the menus drew over the hands): nothing of it is patched, the wrist panel has no Datapad tile, and it does
        /// nothing each frame. False turns it all back on.
        /// </summary>
        internal static readonly bool Shelved = true;

        /// <summary>Debug: no datapad (<see cref="RenderDebug"/> Datapad=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The name the grip is claimed under.</summary>
        public const string ClaimName = "Datapad";

        /// <summary>Before the helmet (10): a grip on the pad held up to the face is the pad's, not the helmet's.</summary>
        public const int ClaimPriority = 9;

        /// <summary>The hand it comes out into: the one the wrist panel is on.</summary>
        public static Hand OutHand => WristButton.WristHand;

        // ---- Where it is and how it moves; metres and seconds ----

        /// <summary>A grip this far round the pad's edges, and this near its plane, takes it.</summary>
        public const float EdgeReach = 0.04f, PlaneReach = 0.06f;

        /// <summary>The holding hand's grip is this far in from the pad's outer edge, and this far below its middle.</summary>
        public const float HoldInset = 0.015f, HoldDrop = 0.02f;

        /// <summary>How wide it is drawn as it goes back into the wrist.</summary>
        public const float AwayWidth = 0.05f;

        public const double ComeSeconds = 0.18, GoSeconds = 0.25, FloatSeconds = 6.0;

        public const float TickAmplitude = HeadGestures.TickAmplitude, TickSeconds = HeadGestures.TickSeconds;

        internal enum Phase
        {
            /// <summary>Put away: not shown.</summary>
            Away,

            /// <summary>Coming out of the wrist panel into the hand.</summary>
            Coming,

            Held,

            /// <summary>Let go: standing where it was left.</summary>
            Standing,

            /// <summary>Going back into the wrist.</summary>
            Going,
        }

        /// <summary>What the render thread needs to pose the pad, published by the game thread.</summary>
        private struct Shown
        {
            public Phase Phase;
            public Hand Holder;
            public long Since;

            /// <summary>Standing, or coming out to its spot in a seat: where it stands; going: where it started from.</summary>
            public Matrix From;

            public float FromWidth;

            /// <summary>Coming out to its spot in a seat (<see cref="From"/>), not into the hand.</summary>
            public bool ToStand;
        }

        private static readonly object gate = new object();
        private static Shown shown;
        private static readonly Matrix[] grips = new Matrix[2];
        private static readonly bool[] gripsTracked = new bool[2];

        // Game thread.
        private static Phase phase;
        private static Hand holder;

        /// <summary>The holder's grip holds it (taken by a grip); false while it rests in the hand it came out into.</summary>
        private static bool gripped;

        /// <summary>Coming out to its spot in a seat (<see cref="TrySeatSpot"/>) rather than into the hand.</summary>
        private static bool toStand;

        private static long phaseAt, touchedAt;
        private static DatapadScreen screen;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(Datapad));

        /// <summary>A test aid took it out: it stays out without the hands, and no grip takes it, until the test aid lets go.</summary>
        private static bool driven;

        /// <summary>The phase, for the drive and the log.</summary>
        internal static Phase Now => phase;

        /// <summary>The hand that holds it, while it is held.</summary>
        internal static Hand? Holder => phase == Phase.Coming && !toStand || phase == Phase.Held ? holder : (Hand?)null;

        /// <summary>It is out: the screen is up and the panel is the pad.</summary>
        internal static bool Out => phase != Phase.Away;

        /// <summary>The wrist panel's Datapad tile can take it out: not switched off, and the hands are the headset's.</summary>
        internal static bool CanOpen => !Shelved && !Off && guard.On && VRInput.Active;

        public static void Patch(Harmony harmony)
        {
            GripClaim.Register(ClaimName, ClaimPriority, Take);
            Log.Info($"Datapad: taken out from the wrist panel's Datapad tile into the {Name(OutHand)} hand; while it is out a grip on it takes it " +
                     $"(grip claim at priority {ClaimPriority})");
        }

        // ---- Game thread ----

        /// <summary>Game thread, once per frame after the hands were read (InputPatches.FrameStart).</summary>
        public static void Update()
        {
            if (Shelved || !guard.On)
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

        /// <summary>
        /// Game thread, from the wrist panel's Datapad tile as the panel closes: the screen goes up and the pad comes out where
        /// the panel was, into the wrist panel's hand (<see cref="OutHand"/>).
        /// </summary>
        internal static void Open() => Open(OutHand, false);

        /// <summary>The pad out into <paramref name="hand"/>; by a test aid (<paramref name="byDrive"/>) without the hands. False when it cannot come out.</summary>
        private static bool Open(Hand hand, bool byDrive)
        {
            if (Shelved || !(byDrive ? !Off && guard.On : CanOpen) || phase != Phase.Away)
                return false;
            bool toChat = Unread;
            screen = new DatapadScreen(toChat);
            MyGuiSandbox.AddScreen(screen);
            holder = hand;
            gripped = false;
            driven = byDrive;
            ClearUnread();
            // In a kind of seat it was last let go in, it comes out to that spot.
            Matrix spot = default;
            toStand = !byDrive && TrySeatSpot(out spot);
            if (toStand)
            {
                lock (gate)
                {
                    shown.From = spot;
                    shown.FromWidth = VRSettings.DatapadWidth;
                }
            }
            Haptics.Pulse(hand, TickAmplitude, TickSeconds);
            Enter(Phase.Coming);
            string to = toStand ? $"to its spot in the {Kind(standSeat)}" : $"into the {Name(hand)} hand";
            Log.Info($"Datapad: taken out from the wrist panel {to}{(byDrive ? " (drive)" : string.Empty)}, {VRSettings.DatapadWidth:0.00} m wide" +
                     (toChat ? ", on its chat page (a line came in while it was away)" : string.Empty));
            return true;
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

            if (phase != Phase.Away && (Off || !VRInput.Active && !driven))
                PutAway(Off ? "switched off" : "the hands went away");
            // The screen went away without the pad being put away: B, Escape, or the game closing it.
            else if (phase != Phase.Away && (screen == null || Gone(screen)))
                PutAway("its screen was closed");
            if (phase != Phase.Away)
                Step();
            UpdateIndicator();
        }

        /// <summary>Game thread, while it is out: grips, Y, and the timed steps.</summary>
        private static void Step()
        {
            // Asked here as well as by VRControls, so a grip on the pad is noticed while its screen has the focus.
            GripClaim.Owner(Hand.Left);
            GripClaim.Owner(Hand.Right);

            // Y on the wrist panel's hand, which opens and closes the panel, puts the pad back as well.
            if (phase != Phase.Going && VRInput.IsNewPressed(WristButton.WristHand, VRButtons.Y))
            {
                Log.Info("Datapad: Y; going back into the wrist");
                Return();
                return;
            }

            // Standing in a seat: it stays with the seat, and goes back into the wrist when the player gets up.
            if (InSeat && !FollowSeat())
            {
                Log.Info("Datapad: the seat it stands in was left; going back into the wrist");
                Return();
                return;
            }

            double since = Seconds(phaseAt);
            switch (phase)
            {
                case Phase.Coming:
                case Phase.Held:
                    if (gripped && !GripClaim.Holds(holder, ClaimName))
                        Release();
                    else if (phase == Phase.Coming && since >= ComeSeconds)
                        Enter(toStand ? Phase.Standing : Phase.Held);
                    break;

                case Phase.Standing:
                    if (standSeat == null && Seconds(Math.Max(phaseAt, touchedAt)) >= FloatSeconds)
                    {
                        Log.Info($"Datapad: left standing {FloatSeconds:0} s untouched; going back into the wrist");
                        Return();
                    }
                    break;

                case Phase.Going:
                    if (since >= GoSeconds)
                        PutAway(null);
                    break;
            }
        }

        /// <summary>
        /// A grip has just started on the pad. Held in one hand, the other hand's grip takes it, and the hand it rests in closes
        /// on it (so that letting go leaves it standing); standing or going back, either hand's grip takes it.
        /// </summary>
        private static bool Take(Hand hand)
        {
            if (Off || !VRInput.Active || ToolbarWheel.Owns || phase == Phase.Away || driven || !OnPad(hand))
                return false;
            // The ship hologram on the pad is the free hand's to turn; the hand holding the pad still grips the pad.
            if (!(Holder.HasValue && hand == holder) && ShipModel.OnPadAt(hand))
                return false;
            if (Holder.HasValue && hand == holder)
            {
                if (gripped)
                    return false;
                gripped = true;
                Haptics.Pulse(hand, TickAmplitude, TickSeconds);
                Log.Info($"Datapad: gripped by the {Name(hand)} hand it rests in; let go and it stands there");
                return true;
            }
            if (Holder.HasValue)
            {
                Log.Info($"Datapad: handed from the {Name(holder)} hand to the {Name(hand)}");
                if (gripped)
                    GripClaim.Release(holder, ClaimName);
                Haptics.Pulse(holder, TickAmplitude, TickSeconds);
            }
            else
                Log.Info($"Datapad: taken back into the {Name(hand)} hand ({(phase == Phase.Standing ? "standing" : phase == Phase.Coming ? "on its way to its spot" : "on its way back")})");
            holder = hand;
            gripped = true;
            toStand = false;
            standSeat = null;
            Haptics.Pulse(hand, TickAmplitude, TickSeconds);
            Enter(Phase.Held);
            return true;
        }

        /// <summary>The holding hand let go: it stands where it is.</summary>
        private static void Release()
        {
            driven = false;
            gripped = false;
            Haptics.Pulse(holder, TickAmplitude, TickSeconds);
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
            bool inSeat = AnchorInSeat(pose);
            Enter(Phase.Standing);
            Log.Info(inSeat
                ? $"Datapad: let go by the {Name(holder)} hand in the {Kind(standSeat)}; it stays there, fixed to the seat"
                : $"Datapad: let go by the {Name(holder)} hand; it stands there {FloatSeconds:0} s");
        }

        /// <summary>Goes back into the wrist from where it is shown now; the screen closes as it gets there.</summary>
        private static void Return()
        {
            if (!HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out _))
            {
                PutAway(null);
                return;
            }
            LetGo();
            lock (gate)
            {
                shown.From = pose;
                shown.FromWidth = 2f * halfWidth;
            }
            Enter(Phase.Going);
        }

        /// <summary>Put away at once: the screen closes. <paramref name="why"/> is logged when it is not the usual way.</summary>
        private static void PutAway(string why)
        {
            if (phase != Phase.Away)
            {
                Haptics.Pulse(OutHand, TickAmplitude, TickSeconds);
                Log.Info(why == null ? "Datapad: put away" : $"Datapad: put away ({why})");
            }
            LetGo();
            DatapadScreen closing = screen;
            screen = null;
            Enter(Phase.Away);
            if (closing != null && !Gone(closing))
                closing.CloseScreen();
        }

        /// <summary>
        /// On the way away: the hand's grip and the seat let go of it. Put away from the hand in a seat, that kind of seat
        /// forgets its spot (<see cref="ForgetSeatSpot"/>).
        /// </summary>
        private static void LetGo()
        {
            if (Holder.HasValue && !driven)
                ForgetSeatSpot();
            if (Holder.HasValue && gripped)
                GripClaim.Release(holder, ClaimName);
            gripped = false;
            driven = false;
            toStand = false;
            standSeat = null;
        }

        /// <summary>From the pad's screen as it closes: if the pad did not close it, the pad is put away.</summary>
        internal static void ScreenClosed(DatapadScreen closed)
        {
            if (ReferenceEquals(closed, screen))
                PutAway("its screen was closed");
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
                shown.ToStand = toStand;
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
        internal static bool OwnsGrip(Hand hand) => phase != Phase.Away && GripClaim.Holds(hand, ClaimName);

        /// <summary>
        /// The hands whose fingertips touch the pad (<see cref="PanelTouch"/>): the free hand while it is held, both while it
        /// stands in the air. False while it is away or going, or not the panel's (a screen of its own is not up).
        /// </summary>
        internal static bool TryTouchHands(out Hand first, out Hand? second)
        {
            first = Hand.Right;
            second = null;
            if (Off || screen == null || PanelTouch.InjectedTip == null && !VRInput.Active)
                return false;
            switch (phase)
            {
                case Phase.Coming when !toStand:
                case Phase.Held:
                    first = Other(holder);
                    return true;
                case Phase.Coming:
                case Phase.Standing:
                    first = VRSettings.DominantHand;
                    second = Other(first);
                    return true;
                default:
                    return false;
            }
        }
    }
}
