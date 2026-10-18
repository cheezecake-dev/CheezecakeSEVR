// SeatG: what the seated astronaut feels of the ship's acceleration, for the push into the seat, the grey-out, the hands and the breathing.
using System;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Common
{
    /// <summary>
    /// The acceleration the local character feels in a seat (a cockpit, a seat, a cryo chamber: MyCockpit and what derives from
    /// it, the character's parent, as the game's own MyCharacterSoundComponent.UpdateBreath finds it), from the game's physics, and
    /// what it does to the astronaut.
    /// <list type="bullet">
    /// <item><b>The ship's pull</b> (<see cref="Pull"/>): the seat's own acceleration, the change each simulation step of the grid's
    /// velocity at the seat (MyPhysicsComponentBase.GetVelocityAtPoint), as the game measures a body's acceleration
    /// (MyPhysicsComponentBase.UpdateAccelerations: the change of velocity times 60), so a seat away from the middle of a turning
    /// ship feels the turn too. It leaves gravity out, as the game's own cockpit shake does (MyCockpit.UpdateBeforeSimulation, from
    /// the grid's LinearAcceleration): it is how the body moves from where it sits at rest, so it moves the eye (the push) and is
    /// felt in the hands. A ship hovering does not pull; one falling pulls up out of the seat.</item>
    /// <item><b>The load on the body</b> (<see cref="Load"/>): the pull less the planet's gravity at the seat
    /// (MyGravityProviderSystem.CalculateNaturalGravityInPoint, the gravity a ship falls in), what the body is pressed with: 1 g
    /// sitting on a planet, nothing falling, 4 g pulling up at 3 g over a planet. It greys the view out and makes the astronaut
    /// breathe hard.</item>
    /// <item>One step's change is capped at <see cref="StepCap"/> and the rest smoothed over <see cref="FeltSeconds"/>, so a
    /// collision's one-step spike is a bump, not G, and only acceleration held for a moment counts.</item>
    /// <item>How hard is hard, from the game's own scale: its cockpit starts to shake as a ship speeds up at 1 m/s² and shakes fully
    /// from 10 m/s² (MyCockpit.MIN_SHAKE_ACC, MAX_SHAKE_ACC). The push starts where the shake starts; the hands feel the pull from
    /// where the shake is full, about 1 g; the breathing and the grey-out start at a load of 2 g, which a light ship on its
    /// thrusters or its dampeners reaches and a heavy one does not, and the grey-out is full at 5 g, where a person without a G-suit
    /// greys out.</item>
    /// </list>
    /// What it does is read from here: <see cref="Push"/> by the seated head (<see cref="Tracking.CockpitHead"/>), <see cref="GreyOut"/>
    /// by the vignette (<see cref="Rendering.Vignette"/>), the pull by the hands (<see cref="Hands.Haptics.GForceFrame"/>), and
    /// <see cref="BreathingHard"/> by the helmet's breathing (<see cref="Rendering.HelmetBreath"/>). Each has its own setting. Only
    /// this machine feels it; nothing in the game changes, and the view is only ever moved a few centimetres or darkened.
    /// </summary>
    internal static class SeatG
    {
        /// <summary>One g, m/s², for the log.</summary>
        public const float G = 9.81f;

        // ---- Tuning: m/s² and seconds ----

        /// <summary>One simulation step's acceleration is taken at most this: a collision's spike is not G (about 10 g).</summary>
        public const float StepCap = 100f;

        /// <summary>The felt acceleration follows the seat's with this time constant: acceleration held for a moment, not a jolt.</summary>
        public const float FeltSeconds = 0.3f;

        /// <summary>The push into the seat starts at this pull (where the game's cockpit starts to shake) and is full at this one, and how far.</summary>
        public const float PushFrom = 1f, PushFull = 30f, PushMetres = 0.04f;

        /// <summary>The push follows the pull with this time constant on top: two smoothings, so it eases in and out with no jerk.</summary>
        public const float PushSeconds = 0.25f;

        /// <summary>The press in the hands starts at this pull (where the game's cockpit shake is full, about 1 g) and is full at this one.</summary>
        public const float RumbleFrom = 10f, RumbleFull = 50f;

        /// <summary>The grey-out starts at this load (2 g) and is full at this one (5 g).</summary>
        public const float GreyFrom = 20f, GreyFull = 50f;

        /// <summary>The grey-out closes in over about this long under a load held, and clears over about this long after.</summary>
        public const float GreyInSeconds = 3f, GreyOutSeconds = 2f;

        /// <summary>The grey-out at its darkest, as the vignette's strength: the edges dark, the middle still clear.</summary>
        public const float GreyMost = 0.7f;

        /// <summary>The astronaut breathes hard from this load (2 g), and for this long after it eases, before the game's own recovery.</summary>
        public const float BreathFrom = 20f, BreathAfterSeconds = 3f;

        /// <summary>The marks the log notes as they are crossed: the pull at 2 m/s² (felt) and the press's start, the load at the grey-out's start and full.</summary>
        private static readonly float[] Marks = { 2f, RumbleFrom, GreyFrom, GreyFull };

        /// <summary>How many of <see cref="Marks"/> are the pull's; the rest are the load's.</summary>
        private const int PullMarks = 2;

        /// <summary>A mark passed is let go below this share of it, so the log does not flicker.</summary>
        private const float MarkDown = 0.8f;

        // ---- Pure, so it can be checked without a game ----

        /// <summary>0 below <paramref name="from"/>, 1 from <paramref name="full"/>, eased between (smoothstep).</summary>
        public static float Ramp(float value, float from, float full)
        {
            float t = MathHelper.Clamp((value - from) / (full - from), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Moves <paramref name="value"/> toward <paramref name="target"/> over <paramref name="seconds"/>, with time constant <paramref name="tau"/>.</summary>
        public static float Follow(float value, float target, float seconds, float tau) => value + (target - value) * (1f - (float)Math.Exp(-seconds / tau));

        public static Vector3 Follow(Vector3 value, Vector3 target, float seconds, float tau) => value + (target - value) * (1f - (float)Math.Exp(-seconds / tau));

        /// <summary>The push for a pull (seat basis): against it, by how hard it is, up to <see cref="PushMetres"/>.</summary>
        public static Vector3 PushFor(Vector3 pull)
        {
            float length = pull.Length();
            return length < 1e-3f ? Vector3.Zero : -pull / length * (PushMetres * Ramp(length, PushFrom, PushFull));
        }

        // ---- What the seat feels now. Game thread, but for GreyOut and BreathingHard (any thread) ----

        /// <summary>The ship's pull on the seat, m/s², in the seat block's basis (x right, y up, z back); zero out of a seat.</summary>
        public static Vector3 Pull { get; private set; }

        /// <summary>The load on the body, m/s²: the pull less the planet's gravity; zero out of a seat.</summary>
        public static float Load { get; private set; }

        /// <summary>The seated eye pushed against the pull, metres in the seat block's basis (x right, y up, z back), by the setting.</summary>
        public static Vector3 Push { get; private set; }

        /// <summary>How far the view's edges have greyed in, as the vignette's strength (0 none to <see cref="GreyMost"/>), by the setting. Any thread.</summary>
        public static float GreyOut => greyOut;

        /// <summary>The astronaut breathes hard now: from a load of <see cref="BreathFrom"/>, and a while after, by the setting. Any thread.</summary>
        public static bool BreathingHard => breathingHard;

        private static volatile float greyOut;
        private static volatile bool breathingHard;
        private static Vector3 load;
        private static float strain, breathHold;
        private static MyCockpit seat;
        private static Vector3 lastVelocity;
        private static ulong lastFrame;
        private static readonly bool[] passed = new bool[4];
        private static bool logBreath;

        /// <summary>Game thread, every frame the game draws (<see cref="Rendering.HelmetState"/>): the local character's seat, if it sits in one.</summary>
        public static void Frame(MyCharacter character)
        {
            ulong frame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            MyCockpit inSeat = character != null && !character.IsDead ? character.Parent as MyCockpit : null;
            if (frame == lastFrame && ReferenceEquals(inSeat, seat))
                return; // the game has not stepped (or is paused): nothing new to feel, and the press in the hands lets go
            float seconds = Math.Min(frame - lastFrame, 6UL) * MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;
            lastFrame = frame;

            Vector3 accel = Vector3.Zero, gravity = Vector3.Zero, velocity = Vector3.Zero;
            var physics = inSeat?.CubeGrid?.Physics;
            if (inSeat?.PositionComp != null)
            {
                MatrixD world = inSeat.PositionComp.WorldMatrixRef;
                gravity = InSeat(MyGravityProviderSystem.CalculateNaturalGravityInPoint(world.Translation), world);
                if (physics != null && !physics.IsStatic)
                {
                    velocity = physics.GetVelocityAtPoint(world.Translation);
                    if (ReferenceEquals(inSeat, seat) && seconds > 0f)
                    {
                        Vector3 change = (velocity - lastVelocity) / seconds;
                        float length = change.Length();
                        if (length > StepCap)
                            change *= StepCap / length;
                        accel = InSeat(change, world);
                    }
                }
            }
            if (!ReferenceEquals(inSeat, seat))
            {
                // A new seat, or none: the speed it was sat down at is not felt, and sitting still is where the load starts.
                seat = inSeat;
                Pull = Vector3.Zero;
                load = -gravity;
            }
            lastVelocity = velocity;
            if (seconds <= 0f)
                return;

            Pull = Follow(Pull, accel, seconds, FeltSeconds);
            load = Follow(load, accel - gravity, seconds, FeltSeconds);
            Load = inSeat != null ? load.Length() : 0f;

            Push = Follow(Push, inSeat != null && VRSettings.SeatGPush ? PushFor(Pull) : Vector3.Zero, seconds, PushSeconds);

            float greyTarget = inSeat != null ? Ramp(Load, GreyFrom, GreyFull) : 0f;
            strain = Follow(strain, greyTarget, seconds, (greyTarget > strain ? GreyInSeconds : GreyOutSeconds) / 3f);
            greyOut = VRSettings.SeatGGreyOut && inSeat != null && FirstPerson(inSeat) ? strain * GreyMost : 0f;

            breathHold = inSeat != null && Load >= BreathFrom ? BreathAfterSeconds : Math.Max(0f, breathHold - seconds);
            breathingHard = VRSettings.SeatGBreathing && breathHold > 0f;

            if (inSeat != null)
                Hands.Haptics.GForceFrame(Pull.Length());
            Note(inSeat);
        }

        /// <summary>A world vector in the seat block's basis (x right, y up, z back).</summary>
        private static Vector3 InSeat(Vector3 v, in MatrixD world) =>
            new Vector3((float)Vector3D.Dot(v, world.Right), (float)Vector3D.Dot(v, world.Up), (float)Vector3D.Dot(v, world.Backward));

        /// <summary>The seat's view is the pilot's own eyes (first person, the seat's camera), so its edges are the astronaut's.</summary>
        private static bool FirstPerson(MyCockpit inSeat) =>
            (inSeat.IsInFirstPersonView || inSeat.ForceFirstPersonCamera) && ReferenceEquals(MySession.Static?.CameraController, inSeat);

        /// <summary>One line each time the pull or the load crosses one of <see cref="Marks"/>, and as the hard breathing starts and stops; never per frame.</summary>
        private static void Note(MyCockpit inSeat)
        {
            float pull = Pull.Length();
            bool changed = false, up = false;
            for (int i = 0; i < Marks.Length; i++)
            {
                float value = i < PullMarks ? pull : Load;
                bool now = passed[i] ? value >= Marks[i] * MarkDown : value >= Marks[i];
                if (now != passed[i])
                {
                    passed[i] = now;
                    changed = true;
                    up |= now;
                }
            }
            if (changed)
            {
                string where = inSeat?.BlockDefinition?.Id.SubtypeName ?? "no seat";
                Log.Info($"Seat G: {(up ? "rising" : "easing")} in {where}: the ship pulls {pull:0.0} m/s² ({Pull.X:0.0} right, {Pull.Y:0.0} up, {Pull.Z:0.0} back), " +
                         $"{Load / G:0.0} g on the body; push {Push.Length() * 100f:0.0} cm{(VRSettings.SeatGPush ? "" : " (off)")}, " +
                         $"press in the hands {(pull >= RumbleFrom ? "on" : "off")}{(VRSettings.HapticSeatG ? "" : " (off)")}, " +
                         $"grey-out {strain * GreyMost:0.00}{(VRSettings.SeatGGreyOut ? "" : " (off)")}");
            }
            if (breathingHard != logBreath)
            {
                logBreath = breathingHard;
                Log.Info(breathingHard
                    ? $"Seat G: the astronaut breathes hard ({Load / G:0.0} g on the body), while it lasts and {BreathAfterSeconds:0} s after"
                    : "Seat G: the hard breathing eases; the game's own breathing recovers from here");
            }
        }
    }
}
