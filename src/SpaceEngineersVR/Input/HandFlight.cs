using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.World;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Flying a ship by hand: in a ship seat the grips take hold of a virtual stick (the dominant hand) and throttle
    /// (the other), and the controllers' movement is what the thumbsticks would have been. The stick's tilt and twist
    /// are pitch, roll and yaw; the throttle pushed forward, back, sideways, up or down is the move. Both go through the
    /// pad's own curve (<see cref="ShipStick"/>), so the game sees the same analog input as from a thumbstick.
    /// </summary>
    /// <remarks>
    /// The two grab zones are spheres in the tracking space (where the hands are), put relative to the head as it was
    /// when the player sat down (<see cref="ZoneSide"/>, <see cref="ZoneDown"/>, <see cref="ZoneAhead"/>), turned to face
    /// where the head faced. They are not tied to the cockpit's modelled stick, so the same motion flies in first person,
    /// third person and a camera view. A grip pressed inside a zone grabs it, with the hand's pose at that moment as the
    /// middle; letting go puts the input back to zero at once. A grip pressed anywhere else is the roll it always was.
    /// The deflection is measured in the seat's frame (up, forward and right as the seat has them), not the hand's, so a
    /// stick is tilted forward by tilting the hand forward however the controller sits in the fist.
    /// </remarks>
    internal static class HandFlight
    {
        /// <summary>Where a zone is, from the head at sit-down: metres to the hand's own side, below, and ahead.</summary>
        public const float ZoneSide = 0.25f, ZoneDown = 0.45f, ZoneAhead = 0.30f;

        /// <summary>How near a hand has to be to a zone's middle, metres, for a grip pressed there to grab it.</summary>
        public const float GrabRadius = 0.15f;

        /// <summary>The stick's dead middle, degrees; the throttle's, metres. Both are taken out and the rest scaled to run 0 to 1.</summary>
        public const float StickDeadzoneDegrees = 3f, ThrottleDeadzone = 0.015f;

        /// <summary>The head moving this far, metres, from where the zones were set (stood up, shifted seat, recentred) sets them again, while nothing is held.</summary>
        public const float ResetDistance = 0.6f;

        private const float TickAmplitude = 0.4f, TickSeconds = 0.05f;
        private const float ShaftBelow = 0.12f, ShaftLength = 0.26f, MarkerRadius = 0.03f;
        private const float ShaftThickness = 0.005f, MarkerThickness = 0.003f;
        private static readonly Vector4 StickColor = new Vector4(1f, 0.75f, 0.2f, 1f);
        private static readonly Vector4 ThrottleColor = new Vector4(0.4f, 1f, 0.55f, 1f);
        private const float IdleBrightness = 0.5f;
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");

        /// <summary>Test seam: the tracked head, in place of the game's.</summary>
        internal static Matrix? HeadOverride { get; set; }

        /// <summary>Test seam: whether a GUI has the cursor, in place of the game's.</summary>
        internal static bool? CursorShownOverride { get; set; }

        /// <summary>One thing held: which hand, the hand's turn and place when it took hold.</summary>
        private struct Grab
        {
            public bool Held;
            public Hand Hand;
            public Matrix Rotation;
            public Vector3 Origin;
        }

        /// <summary>What the scene draw needs, in the tracking space; set each frame by <see cref="Frame"/>.</summary>
        private struct Ghost
        {
            public bool On;
            public Vector3 StickZone, ThrottleZone;
            public bool StickHeld, ThrottleHeld;
            public Vector3 StickOrigin, StickHand, StickDirection;
            public Vector3 ThrottleOrigin, ThrottleHand;
        }

        private static readonly object gate = new object();
        private static Ghost ghost;
        private static int errors;

        // Game thread only.
        private static bool running;
        private static Vector3 zonesHead;
        private static float zonesYaw;
        private static Grab stick, throttle;

        // This frame's deflection, in the seat's frame: the stick's tilt forward, tilt right and twist right, radians;
        // the throttle's push right, up and back, metres (z back, as the movement input has it).
        private static Vector3 tilt, push;

        /// <summary>A grip that holds the stick or the throttle; it is not also a roll.</summary>
        public static bool Holds(Hand hand) => (stick.Held && stick.Hand == hand) || (throttle.Held && throttle.Hand == hand);

        public static bool StickHeld => stick.Held;

        public static bool ThrottleHeld => throttle.Held;

        /// <summary>The scene draw: the stick and throttle, as lines. A failure to hook it costs only the picture.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(HandFlight), nameof(DrawGhost)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Hand flying (the ghost stick and throttle) could not be hooked; flying by hand still works");
            }
        }

        /// <summary>
        /// Game thread, once per frame after the hands were read, with whether this is a ship seat that flies the ship:
        /// sets the zones when the seat is entered, grabs and lets go, and measures the deflection.
        /// </summary>
        public static void Frame(bool flying)
        {
            tilt = push = Vector3.Zero;
            if (!flying || !VRSettings.HandFlying || !TryHead(out Matrix head))
            {
                Stop();
                return;
            }

            if (!running || (!stick.Held && !throttle.Held && Vector3.Distance(head.Translation, zonesHead) > ResetDistance))
                SetZones(head);
            running = true;

            bool cursor = CursorShown();
            Hand stickHand = VRSettings.DominantHand;
            Hand throttleHand = stickHand == Hand.Left ? Hand.Right : Hand.Left;
            Vector3 stickZone = Zone(stickHand), throttleZone = Zone(throttleHand);
            Track(ref stick, stickHand, stickZone, cursor, "stick");
            Track(ref throttle, throttleHand, throttleZone, cursor, "throttle");

            var view = new Ghost { On = true, StickZone = stickZone, ThrottleZone = throttleZone, StickHeld = stick.Held, ThrottleHeld = throttle.Held };
            if (stick.Held)
            {
                Matrix now = Turn(VRInput.Get(stick.Hand).Grip);
                Matrix delta = Matrix.Transpose(stick.Rotation) * now;
                tilt = Tilt(delta, zonesYaw);
                view.StickOrigin = stick.Origin;
                view.StickHand = VRInput.Get(stick.Hand).Grip.Translation;
                view.StickDirection = Vector3.TransformNormal(Vector3.Up, delta);
            }
            if (throttle.Held)
            {
                Vector3 hand = VRInput.Get(throttle.Hand).Grip.Translation;
                push = Vector3.TransformNormal(hand - throttle.Origin, Matrix.CreateRotationY(-zonesYaw));
                view.ThrottleOrigin = throttle.Origin;
                view.ThrottleHand = hand;
            }
            lock (gate)
                ghost = view;
        }

        /// <summary>
        /// Puts what is held into the seat's rotation (x pitch, y yaw, + right, as <see cref="ShipStick.Rotation"/> has
        /// it), movement and roll: the stick replaces the right thumbstick and the grips' roll, the throttle the left
        /// thumbstick's move (the up and down buttons still add to it). Nothing held, nothing changed.
        /// </summary>
        public static void Apply(ref Vector2 rotation, ref Vector3 move, ref float roll, bool up, bool down, in GamepadFeel feel)
        {
            if (stick.Held)
            {
                float sensitivity = VRSettings.HandFlyingSensitivity;
                float full = MathHelper.ToRadians(VRSettings.StickMaxAngle), dead = MathHelper.ToRadians(StickDeadzoneDegrees);
                // Tilting forward is the stick pushed up, so the player's own "invert Y" means what it does on the pad.
                float pitch = Pad(Level(tilt.X, dead, full), feel) * ShipStick.RotationScale * sensitivity;
                float yaw = Pad(Level(tilt.Z, dead, full), feel) * ShipStick.RotationScale * sensitivity;
                // Like a real flight stick: tilting forward puts the nose down (the pad's stick-up is nose up).
                rotation = new Vector2(feel.InvertPitch ? -pitch : pitch, yaw);
                roll = Pad(Level(tilt.Y, dead, full), feel) * sensitivity;
            }
            if (throttle.Held)
            {
                float travel = VRSettings.ThrottleTravel;
                move = new Vector3(
                    MathHelper.Clamp(Pad(Level(push.X, ThrottleDeadzone, travel), feel), -1f, 1f),
                    MathHelper.Clamp(Pad(Level(push.Y, ThrottleDeadzone, travel), feel) + (up ? 1f : 0f) - (down ? 1f : 0f), -1f, 1f),
                    MathHelper.Clamp(Pad(Level(push.Z, ThrottleDeadzone, travel), feel), -1f, 1f));
            }
        }

        /// <summary>
        /// The turn from the pose a hand took hold at to its pose now, as tilt forward, tilt right and twist right
        /// (radians; clockwise from above is right), taken in the seat's frame - the tracking space turned by the seat's yaw.
        /// <paramref name="delta"/> is the turn in the tracking space: the hand's first pose undone, then its pose now.
        /// </summary>
        internal static Vector3 Tilt(in Matrix delta, float seatYaw)
        {
            Matrix seat = Matrix.CreateRotationY(seatYaw) * delta * Matrix.CreateRotationY(-seatYaw);
            Quaternion q = Quaternion.CreateFromRotationMatrix(seat);
            if (q.W < 0f)
                q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
            float s = (float)Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            float scale = s < 1e-6f ? 2f : 2f * (float)Math.Atan2(s, q.W) / s;
            Vector3 axisAngle = new Vector3(q.X, q.Y, q.Z) * scale;
            if (float.IsNaN(axisAngle.X) || float.IsNaN(axisAngle.Y) || float.IsNaN(axisAngle.Z))
                return Vector3.Zero;
            // A right-handed turn about +x takes up toward the player, about +y turns left, about +z takes up to the left.
            return new Vector3(-axisAngle.X, -axisAngle.Z, -axisAngle.Y);
        }

        /// <summary>A deflection with its dead middle taken out and the rest scaled so <paramref name="full"/> is 1, with its sign.</summary>
        internal static float Level(float value, float deadzone, float full)
        {
            float size = Math.Abs(value);
            if (!(size > deadzone))
                return 0f;
            return Math.Sign(value) * Math.Min(1f, (size - deadzone) / (full - deadzone));
        }

        /// <summary>The pad's curve on a signed level.</summary>
        private static float Pad(float level, in GamepadFeel feel) =>
            level == 0f ? 0f : Math.Sign(level) * ShipStick.Curve(Math.Abs(level), feel);

        private static bool TryHead(out Matrix head)
        {
            if (HeadOverride.HasValue)
            {
                head = HeadOverride.Value;
                return true;
            }
            if (GameHead.Take())
            {
                head = GameHead.TrackingPose;
                return true;
            }
            // No tracked head and no headset: the desktop emulator's hands, which it places from a head at the origin.
            head = Matrix.Identity;
            return VRInput.Headset?.Active != true && VRInput.Desktop?.Active == true;
        }

        private static bool CursorShown()
        {
            if (CursorShownOverride.HasValue)
                return CursorShownOverride.Value;
            return MySandboxGame.Static != null && MySandboxGame.Static.IsCursorVisible;
        }

        /// <summary>The zones from the head: its place, and the way it faces (yaw only, so the hands rest the same however the head is tilted).</summary>
        private static void SetZones(in Matrix head)
        {
            Vector3 forward = head.Forward;
            zonesHead = head.Translation;
            zonesYaw = (float)Math.Atan2(-forward.X, -forward.Z);
            Log.Info($"Hand flying: grab zones set, head at ({zonesHead.X:0.00}, {zonesHead.Y:0.00}, {zonesHead.Z:0.00}) facing {MathHelper.ToDegrees(zonesYaw):0} deg");
        }

        /// <summary>A hand's zone, in the tracking space: on its own side of the head.</summary>
        private static Vector3 Zone(Hand hand) =>
            zonesHead + Vector3.TransformNormal(new Vector3(hand == Hand.Right ? ZoneSide : -ZoneSide, -ZoneDown, -ZoneAhead), Matrix.CreateRotationY(zonesYaw));

        /// <summary>A hand's grip pose without its place.</summary>
        private static Matrix Turn(Matrix pose)
        {
            pose.Translation = Vector3.Zero;
            return pose;
        }

        /// <summary>Takes hold when the grip goes down inside the zone; lets go the moment the grip comes up, the hand is lost, or a menu takes the hands.</summary>
        private static void Track(ref Grab grab, Hand hand, Vector3 zone, bool cursor, string what)
        {
            HandState state = VRInput.Get(hand);
            if (grab.Held && (grab.Hand != hand || !state.Tracked || cursor || !VRInput.IsPressed(hand, VRButtons.Grip)))
            {
                grab.Held = false;
                Log.Info($"Hand flying: {what} let go");
            }
            if (grab.Held || !state.Tracked || cursor || !VRInput.IsNewPressed(hand, VRButtons.Grip))
                return;
            if (Vector3.Distance(state.Grip.Translation, zone) > GrabRadius)
                return;
            grab = new Grab { Held = true, Hand = hand, Rotation = Turn(state.Grip), Origin = state.Grip.Translation };
            VRInput.Haptic(hand, TickAmplitude, TickSeconds);
            Log.Info($"Hand flying: {what} taken by the {(hand == Hand.Left ? "left" : "right")} hand");
        }

        /// <summary>Nothing flies by hand: let go of anything held and hide the ghost.</summary>
        private static void Stop()
        {
            if (!running && !stick.Held && !throttle.Held)
                return;
            running = false;
            stick.Held = throttle.Held = false;
            lock (gate)
                ghost = default;
        }

        /// <summary>
        /// Game thread, in the gameplay screen's draw: the stick and the throttle as thin lines in the world where the
        /// hands are (<see cref="HandWorld"/>). Idle, a dim cross in each zone; held, a bright one, and the stick's shaft
        /// leaning as the hand leans, the throttle's line from where it was taken to the hand.
        /// </summary>
        private static void DrawGhost()
        {
            try
            {
                Ghost view;
                lock (gate)
                    view = ghost;
                if (!view.On || !MyTransparentGeometry.HasCamera || !HandWorld.TryGetTrackingToWorld(out MatrixD toWorld))
                    return;

                Vector4 stickColor = view.StickHeld ? StickColor : Dim(StickColor);
                Vector4 throttleColor = view.ThrottleHeld ? ThrottleColor : Dim(ThrottleColor);
                Cross(toWorld, view.StickZone, stickColor);
                Cross(toWorld, view.ThrottleZone, throttleColor);
                if (view.StickHeld)
                {
                    Vector3 pivot = view.StickOrigin - new Vector3(0f, ShaftBelow, 0f);
                    Line(toWorld, pivot, pivot + view.StickDirection * ShaftLength, StickColor, ShaftThickness);
                    Cross(toWorld, view.StickHand, StickColor);
                }
                if (view.ThrottleHeld)
                {
                    Line(toWorld, view.ThrottleOrigin, view.ThrottleHand, ThrottleColor, ShaftThickness);
                    Cross(toWorld, view.ThrottleOrigin, ThrottleColor);
                    Cross(toWorld, view.ThrottleHand, ThrottleColor);
                }
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand flying could not draw its stick and throttle");
            }
        }

        private static Vector4 Dim(Vector4 color) => new Vector4(color.X * IdleBrightness, color.Y * IdleBrightness, color.Z * IdleBrightness, color.W);

        private static void Cross(in MatrixD toWorld, Vector3 centre, Vector4 color)
        {
            Line(toWorld, centre - new Vector3(MarkerRadius, 0f, 0f), centre + new Vector3(MarkerRadius, 0f, 0f), color, MarkerThickness);
            Line(toWorld, centre - new Vector3(0f, MarkerRadius, 0f), centre + new Vector3(0f, MarkerRadius, 0f), color, MarkerThickness);
            Line(toWorld, centre - new Vector3(0f, 0f, MarkerRadius), centre + new Vector3(0f, 0f, MarkerRadius), color, MarkerThickness);
        }

        private static void Line(in MatrixD toWorld, Vector3 from, Vector3 to, Vector4 color, float thickness)
        {
            Vector3D start = Vector3D.Transform((Vector3D)from, toWorld);
            Vector3D along = Vector3D.Transform((Vector3D)to, toWorld) - start;
            double length = along.Length();
            if (length < 0.005)
                return;
            MyTransparentGeometry.AddLineBillboard(LineMaterial, color, start, (Vector3)(along / length), (float)length, thickness);
        }
    }
}
