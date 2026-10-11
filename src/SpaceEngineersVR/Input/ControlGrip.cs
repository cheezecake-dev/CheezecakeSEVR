using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Hands on the controls to fly: in a pilot seat the ship flies only while the player holds its controls, at the
    /// places the game's own pilot animation puts the astronaut's hands in that seat. The dominant hand holds the stick
    /// (the right thumbstick's turn and pitch, and the hand's own: pitch by tilting it forward or back like a flight stick,
    /// yaw by twisting it, roll by tilting it sideways; the triggers' fire), the other the throttle (the
    /// left thumbstick's move, A and B up and down). The panel buttons (dampeners, lights, landing gear, Y, Menu) work
    /// without holding anything. Letting go of a control takes its inputs away at once.
    /// </summary>
    /// <remarks>
    /// <para>The points: each frame, before the arms are put on the controllers (<see cref="HandIK"/>), the seated
    /// character's palm bones stand where that seat's animation (the cockpit's CharacterAnimation: cockpit1_small,
    /// FighterCockpitPosture, RoverCockpitPose, ...) holds them, so every seat has its own, a rover's included. They are
    /// read there, in the body's model space (x right, y up, z back), with the tracking-to-body transform the arms use.</para>
    /// <para>The zones are close to the controls: a grip takes its control only with the palm within
    /// <see cref="TakeRadius"/> of where the game's pose holds it, and never while the fingertip or the palm is within
    /// <see cref="PressRange"/> of something the fingertip presses (a screen's glass, a push-button: <see cref="NearPress"/>),
    /// so a hand closed to point at the cab's screens and buttons is left free to press them. A grip that closes in the
    /// zone, or a closed grip that comes into it, holds the control for as long as the button stays down, wherever the
    /// hand goes: a controller out of the headset's view loses its position tracking and its reported position drifts, so
    /// the hold never depends on the palm's place (only its turn is read for the stick's pitch, yaw and roll while the
    /// position is not tracked: <see cref="HandState.PositionTracked"/>). A grip that starts on something else (Grab to use, the external
    /// view screen) is that feature's (<see cref="GripClaim"/>, which asks this one last).</para>
    /// <para>While held the astronaut's hand is drawn on the control as the animation has it, eased in and out
    /// (<see cref="DrawShare"/>); not held, the arm follows the controller. A hand coming into its zone gets a light
    /// tick and a faint ring on the control; taking hold, a firmer pulse. Pushing a stick with nothing held shows
    /// "Take hold of the stick and throttle to fly", once per sit-down (under the Control hints setting).</para>
    /// <para>With Hand flying on, the same points are its grab zones, and its motion mapping does the flying.
    /// No grab point is made up where the game gives none: a seat whose definition names no pilot pose, a pose that does
    /// not have both hands on something (a hand down by the side or resting on the leg, <see cref="OnControl"/>, decided
    /// once the pose has blended in), and any moment the seated hands cannot be read (a camera block's view) hold nothing
    /// back. The standing stations are pilot seats like the rest. Not here at all: a remote control or a turret
    /// controller (no seat), and a passenger seat (no controls).</para>
    /// </remarks>
    internal static class ControlGrip
    {
        /// <summary>The name the grip claim is held under.</summary>
        public const string ClaimName = "ControlGrip";

        /// <summary>Last of the grip's takers: the helmet (10), the view screen (15), items (20), Grab to use (30) and hulls (40) first.</summary>
        public const int ClaimPriority = 50;

        /// <summary>
        /// A grip takes its control with the palm this close to where the game's pose holds it, metres (RenderDebug
        /// ControlGripTake=cm). From a drop pod run (SmallBlockCockpit, 2026-10-10, the 30 cm zones of before): every
        /// grip that went on to touch the cab's screen closed 21 to 35 cm from the pose's palm, and every grip closed 13 cm
        /// or nearer was a hand going to its control and touched nothing. The pose's palms are 49-56 cm below, 20-22 cm
        /// ahead of and 29-41 cm to the side of the eye; the cab's screens 71-75 cm from the eyes.
        /// </summary>
        public const float DefaultTakeRadius = 0.14f;

        /// <summary>
        /// A fingertip or palm this close to something the fingertip presses (in front of a screen's glass and over it, or a
        /// push-button's box), metres, or through the glass, is a hand at that: a grip there does not take a control.
        /// </summary>
        public const double PressRange = 0.05;

        /// <summary>RenderDebug ControlGripTake=cm in place of the default (null: the default).</summary>
        public static float? TakeDebug { get; set; }

        /// <summary>The take radius in use, metres.</summary>
        public static float TakeRadius => TakeDebug ?? DefaultTakeRadius;

        /// <summary>
        /// A hand of the game's pose is on a control only if its palm is at least this far ahead of the hips (not down by
        /// the side) and this far from its thigh's bone (not resting on the leg), metres; both hands, or the seat is not
        /// held back at all.
        /// </summary>
        public const float AheadMin = 0.10f, LegClear = 0.12f;

        /// <summary>How long the drawn hand takes to go onto the control and back to the controller, seconds.</summary>
        public const float OnSeconds = 0.25f, OffSeconds = 0.2f;

        public const float TickAmplitude = 0.2f, TickSeconds = 0.02f;
        public const float TakeAmplitude = 0.45f, TakeSeconds = 0.03f;
        public const float LetGoAmplitude = 0.15f, LetGoSeconds = 0.015f;

        /// <summary>The hint shown the first time a stick is pushed with nothing held, after each sit-down.</summary>
        public const string HintText = "Take hold of the stick and throttle to fly";

        /// <summary>The points read from the animation are used for this many simulation frames; older ones (the camera is a camera block's) are not, and any grip holds.</summary>
        private const ulong StaleFrames = 30;

        /// <summary>Whether the pose's hands are on controls is decided, and the zones logged, this many captures after a sit-down, once the seat's animation has blended in; until then nothing is held back.</summary>
        private const int DecideAfterCaptures = 30;

        private const double TickEverySeconds = 0.5;
        private const float RingRadius = 0.025f, RingThickness = 0.0025f;
        private const int RingSegments = 16;
        private static readonly Vector4 StickColor = new Vector4(1f, 0.75f, 0.2f, 1f) * 0.55f;
        private static readonly Vector4 ThrottleColor = new Vector4(0.4f, 1f, 0.55f, 1f) * 0.55f;
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");

        /// <summary>RenderDebug ControlGrip=0/1 in place of the setting; null: the setting.</summary>
        public static bool? Debug { get; set; }

        /// <summary>RenderDebug ControlGripLog=1: a line each time what the hands ask of the seat, or what reaches the game, changes (at most 5 a second).</summary>
        public static bool LogInputs { get; set; }

        /// <summary>The controls fly only while held (the setting, or RenderDebug ControlGrip).</summary>
        public static bool Enabled => Debug ?? VRSettings.HandsOnControls;

        /// <summary>A hand's zone in the seated body's model space (x right, y up, z back): round the pose's palm on its control.</summary>
        internal struct Zone
        {
            public Vector3 Game;

            /// <summary>Which way is right in model space (+1: +x), for the log.</summary>
            public float RightSign;

            /// <summary>Metres from a palm at <paramref name="p"/> (model space) to the pose's palm.</summary>
            public float Distance(Vector3 p) => Vector3.Distance(p, Game);
        }

        /// <summary>One control: whether held, since when, and the hand's turn when it took hold (for pitch, yaw and roll).</summary>
        private struct Control
        {
            public bool Held, InZone;

            /// <summary>Held with the hand's position not tracked (a controller out of the headset's view): only its turn is read.</summary>
            public bool PositionLost;
            public Matrix Rotation;
            public float SeatYaw;
            public double TickedAt;
            public float Share;
        }

        // Game thread only.
        private static bool active, hooked;
        private static MyCockpit seat;
        private static Control stick, throttle;

        /// <summary>The held stick's turn from how it was taken (<see cref="HandFlight.Tilt"/>): tilt forward, tilt right and twist right, radians; zero while it is not held.</summary>
        private static Vector3 tilt;
        private static bool hintShown;
        private static MyCockpit noPoseLogged;
        private static int errors, drawErrors;

        // What the animation says this frame (Capture), in the body's model space.
        private static MyCharacter boneFor;
        private static int pelvisBone = -1, headBone = -1;
        private static readonly int[] thighBone = { -1, -1 }, calfBone = { -1, -1 };
        private static readonly Zone[] zones = new Zone[2];
        private static readonly Matrix[] gamePalm = new Matrix[2];
        private static bool captured;
        private static ulong capturedFrame;
        private static MatrixD trackingToModel, modelToTracking;
        private static MyCharacter capturedFor;
        private static int capturesSinceSit;

        // This sit-down's pose: decided once it has blended in; whether both hands are on controls, and why each is or is not.
        private static bool decided, onControls;
        private static readonly string[] handWhy = new string[2];

        // The log (LogInputs).
        private static string lastReport;
        private static double lastReportAt;

        /// <summary>
        /// A pilot seat is entered whose pose has both hands on controls, and the hands either fly only when holding
        /// (<see cref="Enabled"/>) or fly by hand: the controls can be held.
        /// </summary>
        public static bool Active => active && onControls;

        /// <summary>
        /// The ship's controls work only while held, this frame: the setting is on, the seat's pose has both hands on
        /// controls, and they are known now. While they are not (no frame read yet, a camera block's view, the arm solver
        /// off) nothing is held back, as no grab point is made up in their place.
        /// </summary>
        public static bool Gates => active && onControls && Enabled && Fresh();

        public static bool StickHeld => Active && stick.Held;

        public static bool ThrottleHeld => Active && throttle.Held;

        /// <summary>The stick is the dominant hand's, the throttle the other's.</summary>
        public static Hand StickHand => VRSettings.DominantHand;

        /// <summary>This hand holds its control (and its grip is not a roll).</summary>
        public static bool Holds(Hand hand) => Active && (hand == StickHand ? stick.Held : throttle.Held);

        private static ref Control For(Hand hand) => ref (hand == StickHand ? ref stick : ref throttle);

        /// <summary>The grip's taker and the ring's draw. A failure to hook the draw costs only the ring.</summary>
        public static void Patch(Harmony harmony)
        {
            GripClaim.Register(ClaimName, ClaimPriority, Take);
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(ControlGrip), nameof(DrawRings)));
                hooked = true;
            }
            catch (Exception e)
            {
                Log.Error(e, "Hands on the controls: the ring on a control could not be hooked; holding the controls still works");
            }
        }

        /// <summary>
        /// Game thread, once per frame after the hands were read and before the seat's input is worked out, with whether
        /// the hands drive a ship seat that flies the ship: which controls are held, the stick hand's tilt, the zone ticks.
        /// </summary>
        public static void Frame(bool shipSeat)
        {
            try
            {
                MyCockpit cockpit = shipSeat ? PilotSeat() : null;
                string pose = cockpit != null ? Pose(cockpit) : null;
                if (cockpit != null && pose == null && cockpit != noPoseLogged)
                {
                    noPoseLogged = cockpit;
                    Log.Info($"Hands on the controls: the {cockpit.BlockDefinition?.Id.SubtypeName} gives the pilot no pose of its own, so no hand is on a control: its controls work without holding");
                }
                if (cockpit == null || pose == null || !(Enabled || VRSettings.HandFlying))
                {
                    Stop();
                    return;
                }
                if (!active || cockpit != seat)
                {
                    Stop();
                    active = true;
                    seat = cockpit;
                    hintShown = false;
                    capturesSinceSit = 0;
                    decided = onControls = false;
                    Log.Info($"Hands on the controls: seated in the {cockpit.BlockDefinition?.Id.SubtypeName} '{cockpit.CustomName}' (pose {pose}), " +
                             $"stick in the {Name(StickHand)} hand; whether its hands are on controls is read once the pose has blended in");
                }
                Update(Hand.Left);
                Update(Hand.Right);
                tilt = Vector3.Zero;
                if (stick.Held)
                {
                    Matrix delta = Matrix.Transpose(stick.Rotation) * Turn(VRInput.Get(StickHand).Grip);
                    tilt = HandFlight.Tilt(delta, stick.SeatYaw);
                }
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hands on the controls failed this frame");
            }
        }

        /// <summary>Roll from tilting the held stick sideways from how it was taken (+ right), as the pad's stick would give it.</summary>
        public static float Roll(in GamepadFeel feel, float sensitivity) => StickHeld ? ShipStick.TiltRoll(tilt.Y, feel, sensitivity) : 0f;

        /// <summary>
        /// Pitch and yaw from tilting and twisting the held stick from how it was taken, as the pad's right stick would give
        /// them (x pitch, y yaw, + right, <see cref="ShipStick.Rotation"/>): tilted forward the nose goes down and pulled back
        /// it goes up, as a flight stick's does, and twisted to the right it yaws right (unless <see cref="VRSettings.YawFromTwist"/> is off).
        /// Nothing while the stick is not held.
        /// </summary>
        public static Vector2 PitchAndYaw(in GamepadFeel feel, float sensitivity) =>
            StickHeld ? ShipStick.TiltTurn(tilt.X, VRSettings.YawFromTwist ? tilt.Z : 0f, feel, sensitivity) : Vector2.Zero;

        /// <summary>
        /// Game thread, in the seat's input: what the hands ask of a pilot seat, with what an unheld control drives taken
        /// out when the controls fly only while held. The throttle: the move (left stick) and up and down (A, B). The
        /// stick: turn and pitch (right stick, and the hand's tilt and twist, added by the caller), roll, and fire (the
        /// triggers). Also the "take hold" hint, and the log.
        /// </summary>
        public static void Gate(Vector2 leftStick, Vector2 rightStick, ref Vector2 rotation, ref Vector3 move, ref float roll,
                                ref bool fire, ref bool secondary, ref bool up, ref bool down)
        {
            if (!active)
                return;
            Vector2 askedRotation = rotation;
            Vector3 askedMove = move;
            float askedRoll = roll;
            bool askedFire = fire, askedSecondary = secondary, askedUp = up, askedDown = down;
            if (Gates)
            {
                if (!throttle.Held)
                {
                    move = Vector3.Zero;
                    up = down = false;
                }
                if (!stick.Held)
                {
                    rotation = Vector2.Zero;
                    roll = 0f;
                    fire = secondary = false;
                }
                if (!hintShown && !stick.Held && !throttle.Held && (leftStick != Vector2.Zero || rightStick != Vector2.Zero) && ControlHints.ShowNow(HintText))
                    hintShown = true;
            }
            if (LogInputs)
                Report($"move {V(askedMove)} turn {V(askedRotation)} roll {askedRoll:0.00}{Buttons(askedFire, askedSecondary, askedUp, askedDown)}",
                       $"move {V(move)} turn {V(rotation)} roll {roll:0.00}{Buttons(fire, secondary, up, down)}");
        }

        /// <summary>
        /// Game thread, each simulation frame, from <see cref="HandIK"/> after the frame's animation and before the arms
        /// are solved: the seated palms as the animation holds them, and the body's middle, chin, hips and laps, in model
        /// space. Also eases each drawn hand onto or off its control.
        /// </summary>
        internal static void Capture(MyCharacter character, MyCharacterBone[] bones, ArmSolver.Chain left, ArmSolver.Chain right, in MatrixD toModel)
        {
            if (!active || left == null || right == null || character != seat?.Pilot)
            {
                stick.Share = throttle.Share = 0f;
                return;
            }
            try
            {
                Ease(ref For(Hand.Left), Hand.Left);
                Ease(ref For(Hand.Right), Hand.Right);
                if (boneFor != character)
                {
                    boneFor = character;
                    pelvisBone = Find(character, "SE_RigPelvis");
                    headBone = Find(character, "SE_RigHead");
                    thighBone[0] = Find(character, "SE_RigLThigh");
                    thighBone[1] = Find(character, "SE_RigRThigh");
                    calfBone[0] = Find(character, "SE_RigLCalf");
                    calfBone[1] = Find(character, "SE_RigRCalf");
                }
                if (!Has(bones, pelvisBone) || !Has(bones, headBone) || !Has(bones, thighBone[0]) || !Has(bones, thighBone[1]) || !Has(bones, calfBone[0]) || !Has(bones, calfBone[1]))
                    return;
                gamePalm[0] = bones[left.Palm].AbsoluteTransform;
                gamePalm[1] = bones[right.Palm].AbsoluteTransform;
                Vector3 hipL = bones[thighBone[0]].AbsoluteTransform.Translation, hipR = bones[thighBone[1]].AbsoluteTransform.Translation;
                float rightSign = hipR.X >= hipL.X ? 1f : -1f;
                float chinY = bones[headBone].AbsoluteTransform.Translation.Y, hipZ = (hipL.Z + hipR.Z) * 0.5f;
                bool both = true;
                for (int i = 0; i < 2; i++)
                {
                    Vector3 hip = i == 0 ? hipL : hipR, knee = bones[calfBone[i]].AbsoluteTransform.Translation;
                    Vector3 palm = gamePalm[i].Translation;
                    // Standing (the thigh more down than forward), there is no lap for a pose's hand to rest on.
                    Vector3 thigh = knee - hip;
                    bool standing = -thigh.Y > new Vector2(thigh.X, thigh.Z).Length();
                    zones[i] = new Zone { Game = palm, RightSign = rightSign };
                    if (!decided)
                        both &= OnControl(i, palm, hip, knee, chinY, hipZ, standing);
                }
                trackingToModel = toModel;
                modelToTracking = MatrixD.Invert(toModel);
                captured = true;
                capturedFor = character;
                capturedFrame = MySandboxGame.Static.SimulationFrameCounter;
                if (!decided && ++capturesSinceSit >= DecideAfterCaptures)
                {
                    decided = true;
                    onControls = both;
                    LogZones();
                }
            }
            catch (Exception e)
            {
                captured = false;
                if (errors++ < 3)
                    Log.Error(e, "Hands on the controls could not read the seated hands");
            }
        }

        /// <summary>
        /// Whether the game's pose has this hand on a control: ahead of the hips, off the leg, under the chin. A hand down by
        /// the side or resting in the lap holds nothing, and then neither does the seat (no point is made up for it).
        /// </summary>
        private static bool OnControl(int i, Vector3 palm, Vector3 hip, Vector3 knee, float chinY, float hipZ, bool standing)
        {
            float ahead = hipZ - palm.Z;
            Vector3 thigh = knee - hip;
            float along = MathHelper.Clamp(Vector3.Dot(palm - hip, thigh) / Math.Max(thigh.LengthSquared(), 1e-6f), 0f, 1f);
            float leg = Vector3.Distance(palm, hip + thigh * along);
            bool on = ahead >= AheadMin && leg >= LegClear && palm.Y <= chinY;
            handWhy[i] = $"{ahead * 100f:F0} cm ahead of the hips, {leg * 100f:F0} cm off the thigh, {(chinY - palm.Y) * 100f:F0} cm under the chin" +
                         $"{(standing ? " (standing)" : string.Empty)}: {(on ? "on a control" : ahead < AheadMin ? "down by the side, not on a control" : leg < LegClear ? "resting on the leg, not on a control" : "above the chin, not on a control")}";
            return on;
        }

        /// <summary>How far the astronaut's hand is drawn on its control rather than on the controller, 0 to 1, eased; for <see cref="HandIK"/>.</summary>
        internal static float DrawShare(Hand hand)
        {
            if (!Active)
                return 0f;
            float s = For(hand).Share;
            return s * s * (3f - 2f * s);
        }

        /// <summary>A hand's control in the tracking space (where the controllers are), for hand flying's ghost; false while it is not known.</summary>
        internal static bool TryPoint(Hand hand, out Vector3 point)
        {
            point = default;
            if (!Active || !Fresh())
                return false;
            point = (Vector3)Vector3D.Transform((Vector3D)zones[(int)hand].Game, modelToTracking);
            return true;
        }

        // ---- the grip ----

        /// <summary>The taker (<see cref="GripClaim"/>): a grip that starts in its zone, away from anything to press, holds the control.</summary>
        private static bool Take(Hand hand)
        {
            if (!Active)
                return false;
            bool inZone = InZone(hand, out string why);
            Log.Info($"Hands on the controls: {Name(hand)} grip pressed, {why}: {(inZone ? "takes the " + What(hand) : "not a hold")}");
            return inZone;
        }

        /// <summary>One hand: hold, let go, or the tick for coming into the zone.</summary>
        private static void Update(Hand hand)
        {
            ref Control c = ref For(hand);
            string owner = GripClaim.Owner(hand);
            bool down = VRInput.IsPressed(hand, VRButtons.Grip);
            // The grip is this one's (claimed as it closed in the zone) or nobody's (closed elsewhere, nothing took it).
            bool ours = down && (owner == ClaimName || owner == null);
            bool known = Reach(hand, out float distance, out string where);
            bool inZone = false;
            string why = where;
            if (known && distance <= TakeRadius)
            {
                inZone = !NearPress(hand, out string press);
                if (!inZone)
                    why += $", {press}";
            }
            bool held;
            if (!ours)
                held = false;
            else if (c.Held)
                held = true; // held until the grip opens, wherever the hand goes
            else
                held = inZone; // claimed as it closed in the zone, or closed elsewhere and brought into it
            if (held && !c.Held)
            {
                if (owner == null)
                    Log.Info($"Hands on the controls: {Name(hand)} hand came into its zone with the grip closed, {why}: takes the {What(hand)}");
                c.Held = true;
                c.PositionLost = false;
                c.InZone = false;
                c.Rotation = Turn(VRInput.Get(hand).Grip);
                c.SeatYaw = SeatYaw();
                Haptics.Pulse(hand, TakeAmplitude, TakeSeconds);
                Log.Info($"Hands on the controls: {What(hand)} held by the {Name(hand)} hand");
            }
            else if (!held && c.Held)
            {
                c.Held = false;
                c.PositionLost = false;
                Haptics.Pulse(hand, LetGoAmplitude, LetGoSeconds);
                Log.Info($"Hands on the controls: {What(hand)} let go{(down ? string.Empty : ": the grip opened")}");
            }
            if (c.Held)
            {
                // A controller out of the headset's view keeps reporting a position that drifts: nothing here depends on it
                // while held, so say so, and only its turn is read (the stick's pitch, yaw and roll; hand flying's throttle keeps its last push).
                bool lost = !VRInput.Get(hand).PositionTracked;
                if (lost != c.PositionLost)
                {
                    c.PositionLost = lost;
                    Log.Info(lost
                        ? $"Hands on the controls: the {Name(hand)} controller's position is not tracked (out of the headset's view?), {why}; the {What(hand)} stays held while the grip stays closed, read by the controller's turn alone"
                        : $"Hands on the controls: the {Name(hand)} controller's position is tracked again, {why}; the {What(hand)} is still held");
                }
            }
            if (c.Held || down)
            {
                // Still followed, so letting go in the zone is the let-go pulse alone, not a tick after it.
                c.InZone = inZone;
                return;
            }
            double now = MySession.Static?.ElapsedGameTime.TotalSeconds ?? 0d;
            if (inZone && !c.InZone && now - c.TickedAt >= TickEverySeconds)
            {
                c.TickedAt = now;
                Haptics.Pulse(hand, TickAmplitude, TickSeconds);
            }
            c.InZone = inZone;
        }

        /// <summary>
        /// The hand's palm, where the arm would put it, is within the take radius of its control and the hand is not at
        /// something to press (<see cref="NearPress"/>); false while the zones are not known (<see cref="Gates"/> holds
        /// nothing back then).
        /// </summary>
        private static bool InZone(Hand hand, out string why)
        {
            if (!Reach(hand, out float distance, out why) || distance > TakeRadius)
                return false;
            if (NearPress(hand, out string press))
            {
                why += $", {press}";
                return false;
            }
            return true;
        }

        /// <summary>
        /// How far the hand's palm, where the arm would put it, is from the pose's palm on its control, metres, and that in
        /// words; false while the zones are not known.
        /// </summary>
        private static bool Reach(Hand hand, out float distance, out string why)
        {
            distance = float.MaxValue;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
            {
                why = "the hand is not tracked";
                return false;
            }
            if (!onControls || !Fresh())
            {
                why = onControls ? "the game's hands in this seat are not known now" : "the game's pose has no hand on a control here";
                return false;
            }
            Vector3 palm = (Vector3)Vector3D.Transform((Vector3D)HandIK.PalmTarget(hand, state).Translation, trackingToModel);
            distance = zones[(int)hand].Distance(palm);
            why = $"{distance * 100f:F0} cm from the game's hand (takes within {TakeRadius * 100f:F0} cm)";
            return true;
        }

        /// <summary>
        /// The hand is at something its fingertip presses: the fingertip or the palm within <see cref="PressRange"/> of a
        /// screen's glass (<see cref="ScreenGlass"/>: screen touch and the touch toolbar's screen) or through it, or the
        /// fingertip within it of a push-button's box (<see cref="FingerPress"/>). Placed as the fingertip touches are
        /// (<see cref="HandWorld.TryGetNow"/>). <paramref name="what"/> names it.
        /// </summary>
        private static bool NearPress(Hand hand, out string what)
        {
            what = null;
            MyCharacter pilot = seat?.Pilot;
            if (pilot == null || !HandWorld.TryGetNow(pilot, hand, out MatrixD grip, out _, out _))
                return false;
            Vector3D tip = FingerPress.Fingertip(hand, grip), palm = grip.Translation;
            ScreenGlass.Screen at = ScreenGlass.At(hand, tip, palm, PressRange, out float distance);
            if (at != null)
            {
                what = $"{(distance >= 0f ? $"{distance * 100f:F0} cm from" : $"{-distance * 100f:F0} cm through")} {at.Key}: free to press it, not a hold";
                return true;
            }
            if (VRSettings.FingerPress && FingerPress.NearButton(pilot, tip, PressRange, out string button))
            {
                what = $"the fingertip at {button}: free to press it, not a hold";
                return true;
            }
            return false;
        }

        private static void Ease(ref Control c, Hand hand)
        {
            float step = c.Held ? 1f / (OnSeconds * 60f) : -1f / (OffSeconds * 60f);
            c.Share = MathHelper.Clamp(c.Share + step, 0f, 1f);
        }

        private static bool Fresh() =>
            captured && capturedFor == seat?.Pilot && MySandboxGame.Static != null && MySandboxGame.Static.SimulationFrameCounter - capturedFrame <= StaleFrames;

        /// <summary>The seat's yaw in the tracking space (from the body, else the head), the frame the stick's tilt is measured in.</summary>
        private static float SeatYaw()
        {
            Vector3 forward;
            if (Fresh())
                forward = (Vector3)Vector3D.TransformNormal(Vector3D.Forward, modelToTracking);
            else if (GameHead.Take())
                forward = GameHead.TrackingPose.Forward;
            else
                return 0f;
            return (float)Math.Atan2(-forward.X, -forward.Z);
        }

        /// <summary>
        /// The pilot pose the game seats a character with in this cockpit (MyCockpit.GetSitAnimation's sources), or null when
        /// it names none. Every stock seat that flies a ship names one; its hands are where the grab points are read.
        /// </summary>
        private static string Pose(MyCockpit cockpit)
        {
            var d = cockpit.BlockDefinition;
            if (d == null)
                return null;
            // CharacterAnimations' type lives in an assembly the plugin does not reference: read its lists by name.
            object lists = SitAnimations?.GetValue(d);
            if (lists != null)
            {
                foreach (string list in new[] { "MaleAnimations", "FemaleAnimations", "DefaultAnimations" })
                {
                    if (AccessTools.Field(lists.GetType(), list)?.GetValue(lists) is System.Collections.IList names && names.Count > 0 && names[0] is string pose && pose.Length > 0)
                        return pose;
                }
            }
            foreach (string name in new[] { d.CharacterAnimationMale, d.CharacterAnimationFemale, d.CharacterAnimation })
            {
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            return null;
        }

        private static readonly System.Reflection.FieldInfo SitAnimations = AccessTools.Field(typeof(Sandbox.Definitions.MyShipControllerDefinition), "CharacterAnimations");
        private static readonly System.Reflection.MethodInfo MoveIndicator = AccessTools.PropertyGetter(typeof(MyShipController), "MoveIndicator");
        private static readonly System.Reflection.MethodInfo RotationIndicator = AccessTools.PropertyGetter(typeof(MyShipController), "RotationIndicator");
        private static readonly System.Reflection.MethodInfo RollIndicator = AccessTools.PropertyGetter(typeof(MyShipController), "RollIndicator");

        /// <summary>The cockpit the local character sits in and flies from, or null.</summary>
        private static MyCockpit PilotSeat()
        {
            MySession session = MySession.Static;
            if (session == null || session.IsCameraUserControlledSpectator())
                return null;
            return session.ControlledEntity is MyCockpit cockpit && cockpit.Pilot != null && cockpit.Pilot == session.LocalCharacter ? cockpit : null;
        }

        /// <summary>Out of the pilot seat: nothing held, nothing drawn.</summary>
        private static void Stop()
        {
            if (!active)
                return;
            if (stick.Held || throttle.Held)
                Log.Info("Hands on the controls: left the seat, the controls let go");
            active = false;
            seat = null;
            stick = default;
            throttle = default;
            captured = false;
            decided = onControls = false;
            lastReport = null;
        }

        // ---- the log ----

        private static void LogZones()
        {
            Vector3D eye = GameHead.Take() ? Vector3D.Transform((Vector3D)GameHead.TrackingPose.Translation, trackingToModel) : Vector3D.Zero;
            string Side(int i)
            {
                Zone z = zones[i];
                Vector3 t = (Vector3)Vector3D.Transform((Vector3D)z.Game, modelToTracking);
                Vector3 fromEye = z.Game - (Vector3)eye;
                Hand hand = i == 0 ? Hand.Left : Hand.Right;
                return $"{What(hand)} ({Name(hand)}): the game's hand at tracking {V(t)}, {Math.Abs(fromEye.X) * 100f:F0} cm {(fromEye.X * z.RightSign >= 0f ? "right" : "left")}, " +
                       $"{-fromEye.Y * 100f:F0} cm below and {-fromEye.Z * 100f:F0} cm ahead of the eye";
            }
            Log.Info($"Hands on the controls: the {seat?.BlockDefinition?.Id.SubtypeName}'s pose: right hand {handWhy[1]}; left hand {handWhy[0]}: " +
                     (onControls ? $"its controls {(Enabled ? "fly only while held" : "are hand flying's grab points")}" : "no grab points, its controls work without holding"));
            Log.Info($"Hands on the controls: zones in the {seat?.BlockDefinition?.Id.SubtypeName}: {Side(1)}; {Side(0)}; a grip takes a control within " +
                     $"{TakeRadius * 100f:F0} cm of the game's hand and not within {PressRange * 100f:F0} cm of a screen or button, holds it while the grip stays closed; " +
                     $"seat yaw {MathHelper.ToDegrees(SeatYaw()):F0} deg in the tracking space");
        }

        private static void Report(string asked, string given)
        {
            double now = MySession.Static?.ElapsedGameTime.TotalSeconds ?? 0d;
            string key = $"stick {(stick.Held ? "held" : "free")} (tilt forward {MathHelper.ToDegrees(tilt.X):0}, right {MathHelper.ToDegrees(tilt.Y):0}, twist right {MathHelper.ToDegrees(tilt.Z):0}), throttle {(throttle.Held ? "held" : "free")}; hands ask {asked}; to the game {given}";
            // The cockpit's own indicators, as its last MoveAndRotate left them (a frame behind): a change there is a line too.
            string cockpit = string.Empty;
            if (seat != null && MoveIndicator != null && RotationIndicator != null && RollIndicator != null)
                cockpit = $"; the cockpit had move {V((Vector3)MoveIndicator.Invoke(seat, null))} rotation {V((Vector2)RotationIndicator.Invoke(seat, null))} roll {(float)RollIndicator.Invoke(seat, null):0.00}";
            if (key + cockpit == lastReport || now - lastReportAt < 0.2)
                return;
            lastReport = key + cockpit;
            lastReportAt = now;
            Log.Info("Hands on the controls: " + key + cockpit);
        }

        private static string Buttons(bool fire, bool secondary, bool up, bool down) =>
            (fire ? " fire" : string.Empty) + (secondary ? " secondary" : string.Empty) + (up ? " up" : string.Empty) + (down ? " down" : string.Empty);

        private static string V(Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";

        private static string V(Vector2 v) => $"({v.X:0.00}, {v.Y:0.00})";

        private static string Name(Hand hand) => hand == Hand.Left ? "left" : "right";

        private static string What(Hand hand) => hand == StickHand ? "stick" : "throttle";

        private static Matrix Turn(Matrix pose)
        {
            pose.Translation = Vector3.Zero;
            return pose;
        }

        private static int Find(MyCharacter character, string name)
        {
            character.AnimationController.FindBone(name, out int index);
            return index;
        }

        private static bool Has(MyCharacterBone[] bones, int index) => index >= 0 && index < bones.Length && bones[index] != null;

        // ---- the ring ----

        /// <summary>Game thread, in the gameplay screen's draw: a faint ring on each control whose hand is in its zone and not holding it.</summary>
        private static void DrawRings()
        {
            try
            {
                if (!active || !hooked || !Fresh() || !MyTransparentGeometry.HasCamera || MySector.MainCamera == null || capturedFor?.PositionComp == null)
                    return;
                MatrixD world = capturedFor.PositionComp.WorldMatrixRef;
                Vector3D eye = MySector.MainCamera.Position;
                for (int i = 0; i < 2; i++)
                {
                    Hand hand = i == 0 ? Hand.Left : Hand.Right;
                    Control c = For(hand);
                    if (!c.InZone || c.Held)
                        continue;
                    Ring(Vector3D.Transform((Vector3D)zones[i].Game, world), eye, hand == StickHand ? StickColor : ThrottleColor);
                }
            }
            catch (Exception e)
            {
                if (drawErrors++ < 3)
                    Log.Error(e, "Hands on the controls could not draw the ring on a control");
            }
        }

        /// <summary>A small circle facing the eye.</summary>
        private static void Ring(Vector3D centre, Vector3D eye, Vector4 color)
        {
            Vector3D toEye = eye - centre;
            if (toEye.LengthSquared() < 1e-6)
                return;
            toEye.Normalize();
            Vector3D a = Vector3D.CalculatePerpendicularVector(toEye);
            Vector3D b = Vector3D.Cross(toEye, a);
            Vector3D last = centre + a * RingRadius;
            for (int k = 1; k <= RingSegments; k++)
            {
                double angle = MathHelper.TwoPi * k / RingSegments;
                Vector3D next = centre + (a * Math.Cos(angle) + b * Math.Sin(angle)) * RingRadius;
                Vector3D along = next - last;
                double length = along.Length();
                MyTransparentGeometry.AddLineBillboard(LineMaterial, color, last, (Vector3)(along / length), (float)length, RingThickness);
                last = next;
            }
        }
    }
}
