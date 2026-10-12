using System;
using System.Diagnostics;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The index fingertip on the GUI panel (<see cref="HudPanel"/>) as the game's mouse, while the panel is something held
    /// in the hand (<see cref="Datapad"/>): the tip near the panel moves the cursor; touching it and lifting off is a click;
    /// touching and sliding is a swipe, which turns the mouse wheel under the place the touch began, so a list or a text
    /// follows the finger; touching and holding still presses the button and keeps it down, the cursor then following the
    /// tip, for a drag or a slider. It goes into the mouse state the same way as the laser's (<see cref="Laser"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A click is sent when the finger lifts, not as it lands: the finger does not know yet whether it will swipe, and a
    /// press already sent cannot be taken back. The game sees the button go down on one frame and up on the next, at the
    /// place the touch began.
    /// </para>
    /// <para>
    /// The panel is a layer the headset draws over the scene, so the drawn hand is behind it. Where the tip is shows as the
    /// game's cursor and as a short beam from the tip down to the surface (the laser's beam layer, which is over the panel),
    /// shrinking to nothing as the tip touches.
    /// </para>
    /// </remarks>
    internal static class PanelTouch
    {
        /// <summary>The tip this near the surface, or past it: touching. Metres.</summary>
        internal const float ContactDepth = 0.004f;

        /// <summary>The tip within this in front of the panel: it has the cursor. Metres.</summary>
        internal const float HoverDepth = 0.03f;

        /// <summary>Lifted further than this off the surface: the touch is up. As a screen's glass (<see cref="ScreenTouch.LiftOff"/>).</summary>
        internal const float LiftOff = ScreenTouch.LiftOff;

        /// <summary>Slid further than this past the panel's edge: the touch is up.</summary>
        internal const float SlideOff = ScreenTouch.SlideOff;

        /// <summary>A tip further behind the surface than this did not come from in front: not a touch. Metres.</summary>
        internal const float PassThrough = 0.04f;

        /// <summary>A touch that slides this far along the surface is a swipe, not a click. Metres.</summary>
        internal const float SwipeMetres = 0.012f;

        /// <summary>A touch held still this long presses the button and keeps it down. Seconds.</summary>
        internal const double HoldSeconds = 0.45;

        /// <summary>
        /// Wheel units per GUI pixel of swipe, at the 1080-pixel height. A scrolled text moves by 0.0005 of the screen's height a
        /// wheel unit (MyGuiControlMultilineText.HandleInput), so this is 1:1: the text stays under the finger.
        /// </summary>
        private const float WheelPerPixel = 1f / (1080f * 0.0005f);

        private const float LandAmplitude = 0.2f, LandSeconds = 0.012f, ClickAmplitude = 0.35f, ClickSeconds = 0.03f;

        private enum Phase
        {
            Idle,

            /// <summary>Touching; not yet a click, a swipe or a hold.</summary>
            Down,

            Swipe,

            /// <summary>Held still: the button is down, and the cursor follows the tip.</summary>
            Hold,

            /// <summary>The button went down at the touch's place last frame; it goes up this frame.</summary>
            Click,
        }

        private static Phase phase;
        private static Hand touching;
        private static Vector2 downPixel, lastPixel, downLocal;
        private static long downAt;
        private static float wheelCarry;
        private static long lastNote;

        // The beam from the tip to the surface (the laser's line, see Laser.TryGetLine).
        private static bool beam;
        private static Vector3 beamFrom, beamTo;

        /// <summary>
        /// Test aid (the drive's datapad poke): the tip of this hand, in the tracking space, instead of the controller's.
        /// Null: the controllers. Game thread.
        /// </summary>
        internal static Vector3? InjectedTip { get; set; }

        internal static Hand InjectedHand { get; set; }

        /// <summary>A touch is going on (down, swiping, held, or the click being sent).</summary>
        internal static bool Busy => phase != Phase.Idle;

        /// <summary>A fingertip tapped in the last moment: the press it made was already felt (the tap's own tick).</summary>
        internal static bool JustTapped => tapAt != 0 && Seconds(tapAt, Stopwatch.GetTimestamp()) < 0.2;

        private static long tapAt;

        /// <summary>The touch's last few words, for the drive.</summary>
        internal static string LastEvent { get; private set; } = "none";

        /// <summary>The beam from the tip to the surface this frame, when the tip has the cursor.</summary>
        internal static bool TryGetBeam(out Vector3 from, out Vector3 to)
        {
            from = beamFrom;
            to = beamTo;
            return beam;
        }

        /// <summary>
        /// Game thread, from the laser's frame (after the game read its mouse): whether a fingertip has the cursor this frame,
        /// and if so where it is (GUI pixels), whether the left button is down, and how far the wheel turns.
        /// </summary>
        internal static bool Frame(Vector2 gui, out Vector2 cursor, out bool left, out int wheel)
        {
            cursor = default;
            left = false;
            wheel = 0;
            beam = false;
            if (!HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out float halfHeight) || !Datapad.TryTouchHands(out Hand first, out Hand? second))
            {
                Reset();
                return false;
            }

            if (phase == Phase.Click)
            {
                // The second frame of a click: the button goes up where it went down.
                cursor = downPixel;
                phase = Phase.Idle;
                return true;
            }

            // The hand that is touching, or else whichever tip is nearest in front, over the panel.
            Hand hand = first;
            Spot spot = default;
            if (phase != Phase.Idle)
            {
                hand = touching;
                if (!TrySpot(hand, pose, halfWidth, halfHeight, gui, out spot))
                {
                    Lift("the hand was lost");
                    return false;
                }
            }
            else
            {
                bool found = TrySpot(first, pose, halfWidth, halfHeight, gui, out spot) && spot.Over(HoverDepth);
                if (second.HasValue && TrySpot(second.Value, pose, halfWidth, halfHeight, gui, out Spot other) && other.Over(HoverDepth)
                    && (!found || other.Depth < spot.Depth))
                {
                    hand = second.Value;
                    spot = other;
                    found = true;
                }
                if (!found)
                    return false;
            }

            long now = Stopwatch.GetTimestamp();
            switch (phase)
            {
                case Phase.Idle:
                    cursor = spot.Pixel;
                    if (spot.Depth <= ContactDepth && spot.Depth >= -PassThrough && spot.Outside <= 0f && spot.Before > ContactDepth)
                    {
                        phase = Phase.Down;
                        touching = hand;
                        downPixel = lastPixel = spot.Pixel;
                        downLocal = spot.Local;
                        downAt = now;
                        Haptics.Pulse(hand, LandAmplitude, LandSeconds);
                    }
                    break;

                case Phase.Down:
                    cursor = downPixel;
                    if (spot.Depth > LiftOff || spot.Outside > SlideOff)
                    {
                        // Lifted (or slid off) without moving: a click where it landed.
                        phase = Phase.Click;
                        left = true;
                        tapAt = now;
                        Haptics.Pulse(hand, ClickAmplitude, ClickSeconds);
                        Note($"Touch: {hand} tap at GUI ({downPixel.X:0}, {downPixel.Y:0}) after {Seconds(downAt, now) * 1000:0} ms");
                    }
                    else if (Vector2.Distance(spot.Local, downLocal) > SwipeMetres)
                    {
                        phase = Phase.Swipe;
                        wheelCarry = 0f;
                        Note($"Touch: {hand} swipe from GUI ({downPixel.X:0}, {downPixel.Y:0})");
                        wheel = Swipe(spot.Pixel, gui);
                    }
                    else if (Seconds(downAt, now) >= HoldSeconds)
                    {
                        phase = Phase.Hold;
                        left = true;
                        Haptics.Pulse(hand, ClickAmplitude, ClickSeconds);
                        Note($"Touch: {hand} hold at GUI ({downPixel.X:0}, {downPixel.Y:0})");
                    }
                    break;

                case Phase.Swipe:
                    // The wheel turns over the place the swipe began, so the list under it moves, wherever the finger goes.
                    cursor = downPixel;
                    if (spot.Depth > LiftOff || spot.Outside > SlideOff)
                        Lift("swipe ended");
                    else
                        wheel = Swipe(spot.Pixel, gui);
                    break;

                case Phase.Hold:
                    cursor = spot.Pixel;
                    if (spot.Depth > LiftOff || spot.Outside > SlideOff)
                        Lift("hold let go");
                    else
                        left = true;
                    break;
            }
            Datapad.Touched();

            // The beam from the tip to the surface, while the tip is in front of it.
            if (spot.Depth > ContactDepth)
            {
                beam = true;
                beamFrom = spot.Tip;
                beamTo = Vector3.Transform(new Vector3(spot.Local.X, spot.Local.Y, 0f), pose);
            }
            return true;
        }

        /// <summary>Each hand's tip depth last frame; NaN when it was not seen, which is not "in front".</summary>
        private static readonly float[] lastDepth = { float.NaN, float.NaN };

        /// <summary>A hand's tip against the panel this frame.</summary>
        private struct Spot
        {
            public Vector3 Tip;
            public Vector2 Local, Pixel;
            public float Depth, Outside;

            /// <summary>The depth the frame before (NaN if the tip was not seen then).</summary>
            public float Before;

            public bool Over(float depth) => Depth <= depth && Depth >= -PassThrough && Outside <= 0f;
        }

        private static bool TrySpot(Hand hand, in Matrix pose, float halfWidth, float halfHeight, Vector2 gui, out Spot spot)
        {
            spot = default;
            Vector3 tip;
            if (InjectedTip.HasValue && hand == InjectedHand)
                tip = InjectedTip.Value;
            else
            {
                HandState state = VRInput.Get(hand);
                if (!state.Tracked)
                    return false;
                tip = FingerPress.FingertipInTracking(hand, state);
            }
            Locate(tip, pose, halfWidth, halfHeight, gui, out spot.Local, out spot.Depth, out spot.Pixel, out spot.Outside);
            spot.Tip = tip;
            // A touch has to come from in front: the depth is kept for the next frame.
            spot.Before = lastDepth[(int)hand];
            lastDepth[(int)hand] = spot.Depth;
            return true;
        }

        /// <summary>
        /// Where a point is against the panel: in the panel's own plane (metres from its middle, x right, y up), how far in front
        /// of it (negative behind), the GUI pixel it is over (clamped to the panel), and how far past the nearest edge it is
        /// (0 over the panel). The panel as <see cref="LaserRay.Cast"/> takes it.
        /// </summary>
        internal static void Locate(Vector3 point, in Matrix panel, float halfWidth, float halfHeight, Vector2 gui,
            out Vector2 local, out float depth, out Vector2 pixel, out float outside)
        {
            Vector3 p = Vector3.Transform(point, Matrix.Invert(panel));
            local = new Vector2(p.X, p.Y);
            depth = p.Z;
            outside = Math.Max(0f, Math.Max(Math.Abs(p.X) - halfWidth, Math.Abs(p.Y) - halfHeight));
            float u = halfWidth > 0f ? (p.X + halfWidth) / (2f * halfWidth) : 0.5f;
            float v = halfHeight > 0f ? (halfHeight - p.Y) / (2f * halfHeight) : 0.5f;
            pixel = new Vector2(MathHelper.Clamp(u, 0f, 1f) * (gui.X - 1f), MathHelper.Clamp(v, 0f, 1f) * (gui.Y - 1f));
        }

        /// <summary>The wheel for the swipe's move since the last frame: the finger going up scrolls the content up with it.</summary>
        private static int Swipe(Vector2 pixel, Vector2 gui)
        {
            float dy = (pixel.Y - lastPixel.Y) * 1080f / Math.Max(gui.Y, 1f);
            lastPixel = pixel;
            wheelCarry += dy * WheelPerPixel;
            int whole = (int)wheelCarry;
            wheelCarry -= whole;
            return whole;
        }

        private static void Lift(string why)
        {
            Note($"Touch: {touching} {why}");
            phase = Phase.Idle;
            wheelCarry = 0f;
        }

        /// <summary>Nothing to touch (the panel is not held): a touch going on ends without a click.</summary>
        internal static void Reset()
        {
            if (phase != Phase.Idle && phase != Phase.Click)
                Lift("the panel went away");
            phase = Phase.Idle;
            beam = false;
            lastDepth[0] = lastDepth[1] = float.NaN;
        }

        private static double Seconds(long from, long to) => (to - from) / (double)Stopwatch.Frequency;

        /// <summary>A log line, but no more than five a second.</summary>
        private static void Note(string text)
        {
            LastEvent = text;
            long now = Stopwatch.GetTimestamp();
            if (lastNote != 0 && now - lastNote < Stopwatch.Frequency / 5)
                return;
            lastNote = now;
            Log.Info(text);
        }
    }
}
