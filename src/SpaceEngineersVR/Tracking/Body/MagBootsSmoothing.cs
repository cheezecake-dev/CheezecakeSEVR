using System;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.OpenXR;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// On magnetic boots the first-person view rides a smoothed copy of the body's up, so the surface underfoot does not rock
    /// it in 2.3 degree steps. The hands, the held tool, the lasers and the aim are placed by the same head matrix and go with
    /// it. The body's yaw, the player's own turning, is not smoothed.
    /// </summary>
    /// <remarks>
    /// What the game does (decompiled): with no gravity, the jetpack off and the feet down (<c>MyCharacter.IsMagneticBootsEnabled</c>),
    /// <c>MyCharacter.UpdatePhysicalMovement</c> turns the body every simulation step toward the normal of what it stands on
    /// (<c>UpdateStandup</c>: at most 0.04 rad, 2.3 deg, a step, which is 137 deg/s, about the body's middle, through the physics
    /// proxy's <c>SetForwardAndUp</c>). On voxels that normal is the physics proxy's support normal, the normal of the triangle
    /// of the voxel mesh the capsule rests on, which changes at every triangle the feet cross, so the body rocks after it in
    /// 2.3 deg steps. On a grid the game casts four rays round the feet and takes their normal when they agree
    /// (<c>MyMagneticBootsOnGridSmoothing</c>), else the proxy's, which at a block's edge or a seam is the slanted normal of the
    /// edge. The first-person head is built on the body's forward and up (<see cref="CharacterHead"/>), so each of those turns
    /// tilts the view and swings the eye about the middle with it. Where the middle is (the character controller's) is not part
    /// of the turn.
    ///
    /// Here, after each step on magnetic boots, a smoothed up follows the body's up through a critically damped spring
    /// (<see cref="SmoothSeconds"/>), and between steps it holds still relative to what the feet are on (a grid's own turn, a
    /// ship rolling, is carried at once, not smoothed; on voxels the world holds still). Every first-person head matrix
    /// (<see cref="CharacterHead"/>) is then turned by the shortest turn from the body's up to the smoothed up, about the body's
    /// middle: the eye goes where the smoothed body would have it, and a turn about the up, the player's yaw, passes through
    /// unchanged. Off magnetic boots (the jetpack, a jump, gravity, a ladder) whatever is left of the difference is carried with
    /// the body instead and eased out by the same spring, so the player's own pitch and roll on the jetpack are not smoothed.
    /// Once the difference is under 0.006 deg and still, the view is on the body's up exactly. The body as drawn keeps the
    /// game's up: looking down, the legs still rock. RenderDebug MagBootsSmoothing=0 gives the view the body's up again (the
    /// log goes on measuring both); MagBootsSmoothingMs=n sets the smoothing time.
    /// </remarks>
    internal static class MagBootsSmoothing
    {
        /// <summary>RenderDebug MagBootsSmoothing=0: the view has the body's up, as the game turns it.</summary>
        public static bool Off { get; set; }

        /// <summary>
        /// The spring's smoothing time, seconds (2 / its natural frequency). A steady turn is followed this far behind, so
        /// stepping over a 90 deg edge, which the game turns in 0.65 s, eases over about a second and is at most some 27 deg
        /// behind the body's; rocking at 3 Hz is cut to about a quarter, at 6 Hz to a fifteenth, and the game's flicker from
        /// step to step (30 Hz) to under a three-hundredth. Critically damped rather than a plain exponential: the smoothed up's
        /// speed has no steps in it where the game's turn starts and stops.
        /// </summary>
        public const double DefaultSmoothSeconds = 0.2;

        /// <summary>The smoothing time in use, seconds (RenderDebug MagBootsSmoothingMs=n sets it).</summary>
        public static double SmoothSeconds { get; set; } = DefaultSmoothSeconds;

        /// <summary>The game's simulation step, seconds.</summary>
        private const double StepSeconds = MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;

        /// <summary>Further than this behind the body's up the smoothed up is pulled in (radians, 60 deg).</summary>
        private const double MaxOffset = Math.PI / 3.0;

        /// <summary>Under this apart (radians, 0.006 deg) and turning slower than <see cref="SettleSpin"/>, the smoothed up is the body's.</summary>
        private const double SettleAngle = 1e-4;

        /// <summary>Radians a second.</summary>
        private const double SettleSpin = 1e-3;

        /// <summary>More simulation steps than this since the last one and the smoothed up starts again on the body's.</summary>
        private const ulong GapSteps = 3;

        private const double StatsSeconds = 10.0;

        private static MyCharacter body;    // the character the state is for (null: none yet, or it stopped)
        private static ulong lastStep;      // the simulation frame of the last step
        private static bool onBoots;        // the last step was on magnetic boots: the smoothed up holds still relative to the feet's support
        private static bool held;           // the smoothed up is not the body's (else the view has the body's up)
        private static Vector3D smoothUp;   // the smoothed up at the last step, world
        private static Vector3D localUp;    // the same in the body's own frame (off the boots it rides the body)
        private static Vector3D spin;       // the smoothed up's angular velocity, world, radians a second
        private static Vector3D bodyUpWas;  // the body's up at the last step, world
        private static MatrixD bodyWas;     // the body's orientation at the last step
        private static MyCubeGrid gridWas;  // the grid the feet were on at the last step (null: voxels or nothing, and the world holds still)
        private static MatrixD gridWasTurn; // its orientation then
        private static Vector3D middleWas;  // the body's middle then, in that grid's frame (the world's without one)
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(MagBootsSmoothing));

        // The 10 s log (Stats): steps on boots, and the turn and lift seen in them.
        private static long windowStart;
        private static int steps, gridSteps, voxelSteps, liftSteps;
        private static double bodySum, bodyPeak, smoothSum, smoothPeak, offPeak, liftSum, liftPeak;

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), "UpdatePhysicalMovement"),
                    postfix: new HarmonyMethod(typeof(MagBootsSmoothing), nameof(Stepped)));
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "Magnetic boots smoothing: not hooked; on magnetic boots the view tilts with the body as the game turns it");
                throw;
            }
        }

        /// <summary>
        /// Turns a first-person head matrix of <paramref name="character"/> from the body's up to the smoothed up, about the
        /// body's middle, while the headset is in use. Game thread; <see cref="CharacterHead"/> calls it for every head
        /// matrix it builds.
        /// </summary>
        public static void Apply(MyCharacter character, ref MatrixD head)
        {
            if (Off || !held || character != body || !XrSession.Focused)
                return;
            MatrixD world = character.PositionComp.WorldMatrixRef;
            Vector3D up = world.Up;
            Vector3D target = onBoots ? smoothUp : Vector3D.TransformNormal(localUp, world);
            Vector3D axis = Vector3D.Cross(up, target);
            double sin = axis.Length();
            if (sin < 1e-12)
                return;
            MatrixD turn = MatrixD.CreateFromAxisAngle(axis / sin, Math.Atan2(sin, Vector3D.Dot(up, target)));
            Vector3D middle = Middle(character, world);
            Vector3D eye = head.Translation;
            head *= turn;
            head.Translation = middle + Vector3D.TransformNormal(eye - middle, turn);
        }

        /// <summary>The game has turned and placed the body for this step.</summary>
        private static void Stepped(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                Step(__instance);
            }
            catch (Exception e)
            {
                body = null;
                held = false;
                guard.Failed(e, "Magnetic boots smoothing: a step failed; the view has the body's up until the next one");
            }
        }

        private static void Step(MyCharacter character)
        {
            MyPhysicsBody physics = character.Physics;
            if (character.IsDead || character.IsSitting || physics == null || !physics.Enabled)
            {
                body = null;
                held = false;
                return;
            }
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            if (character == body && frame == lastStep)
                return; // a second call in one step: its turn is smoothed from the next step on
            MatrixD world = character.PositionComp.WorldMatrixRef;
            MatrixD turn = world.GetOrientation();
            Vector3D up = world.Up;
            bool boots = character.IsMagneticBootsEnabled;
            var sound = character.SoundComp;
            MyCubeGrid grid = boots ? sound?.StandingOnGrid : null;
            bool voxels = boots && grid == null && sound?.StandingOnVoxel != null;
            MatrixD gridWorld = grid != null ? grid.PositionComp.WorldMatrixRef : MatrixD.Identity;
            MatrixD gridTurn = gridWorld.GetOrientation();
            Vector3D middle = Middle(character, world);
            Vector3D middleHere = grid != null ? Vector3D.Transform(middle, grid.PositionComp.WorldMatrixNormalizedInv) : middle;

            if (character != body || frame < lastStep || frame - lastStep > GapSteps)
            {
                // A new body, or steps missed (a seat, a pause, a load): start again on the body's up.
                smoothUp = up;
                spin = Vector3D.Zero;
                held = false;
            }
            else
            {
                double seconds = StepSeconds * (frame - lastStep);
                Vector3D bodyFrom = bodyUpWas;
                if (boots)
                {
                    // Held still relative to the feet's support since the last step: a grid's own turn is carried, not smoothed.
                    if (grid != null && grid == gridWas)
                    {
                        smoothUp = Carry(smoothUp, gridWasTurn, gridTurn);
                        spin = Carry(spin, gridWasTurn, gridTurn);
                        bodyFrom = Carry(bodyFrom, gridWasTurn, gridTurn);
                    }
                }
                else
                {
                    // Off the boots what is left rides the body, so the player's own turns are not smoothed.
                    smoothUp = Carry(smoothUp, bodyWas, turn);
                    spin = Carry(spin, bodyWas, turn);
                }
                Vector3D smoothFrom = smoothUp;
                if (boots || held)
                    Follow(up, seconds);
                else
                {
                    smoothUp = up;
                    spin = Vector3D.Zero;
                }
                if (boots)
                {
                    steps++;
                    if (grid != null)
                        gridSteps++;
                    else if (voxels)
                        voxelSteps++;
                    double bodyTurn = MathHelper.ToDegrees(Angle(bodyFrom, up)) / seconds;
                    double smoothTurn = MathHelper.ToDegrees(Angle(smoothFrom, smoothUp)) / seconds;
                    bodySum += bodyTurn;
                    bodyPeak = Math.Max(bodyPeak, bodyTurn);
                    smoothSum += smoothTurn;
                    smoothPeak = Math.Max(smoothPeak, smoothTurn);
                    offPeak = Math.Max(offPeak, MathHelper.ToDegrees(Angle(up, smoothUp)));
                    if (onBoots && grid == gridWas)
                    {
                        // The middle's move along the up relative to the support, which is not smoothed here.
                        Vector3D moved = middleHere - middleWas;
                        if (grid != null)
                            moved = Vector3D.TransformNormal(moved, gridWorld);
                        double lift = Math.Abs(Vector3D.Dot(moved, up)) / seconds * 100.0;
                        liftSteps++;
                        liftSum += lift;
                        liftPeak = Math.Max(liftPeak, lift);
                    }
                }
            }

            body = character;
            lastStep = frame;
            onBoots = boots;
            localUp = Vector3D.TransformNormal(smoothUp, MatrixD.Transpose(turn));
            bodyUpWas = up;
            bodyWas = turn;
            gridWas = grid;
            gridWasTurn = gridTurn;
            middleWas = middleHere;
            Stats();
        }

        /// <summary>One step of the critically damped spring that pulls the smoothed up toward the body's.</summary>
        private static void Follow(in Vector3D up, double seconds)
        {
            if (Vector3D.Dot(up, smoothUp) <= 0.0)
            {
                // A quarter turn or more apart is no surface's doing (a teleport): start again on the body's up.
                smoothUp = up;
                spin = Vector3D.Zero;
                held = false;
                return;
            }
            // The smoothed up as a turn from the body's (axis times angle, square to the up), and its speed in the same terms.
            Vector3D offset = Between(up, smoothUp);
            spin -= Vector3D.Dot(spin, up) * up;

            // A critically damped spring (Game Programming Gems 4, 1.10), stepped exactly enough for any step length.
            double omega = 2.0 / Math.Max(0.001, SmoothSeconds);
            double w = omega * seconds;
            double decay = 1.0 / (1.0 + w + 0.48 * w * w + 0.235 * w * w * w);
            Vector3D push = (spin + omega * offset) * seconds;
            spin = (spin - omega * push) * decay;
            offset = (offset + push) * decay;

            double angle = offset.Length();
            if (angle > MaxOffset)
            {
                offset *= MaxOffset / angle;
                angle = MaxOffset;
            }
            held = angle >= SettleAngle || spin.Length() >= SettleSpin;
            if (!held)
            {
                smoothUp = up;
                spin = Vector3D.Zero;
                return;
            }
            smoothUp = angle < 1e-12 ? up : Vector3D.TransformNormal(up, MatrixD.CreateFromAxisAngle(offset / angle, angle));
        }

        /// <summary>The turn from <paramref name="from"/> to <paramref name="to"/> (unit vectors less than a half turn apart) as its axis times its angle.</summary>
        private static Vector3D Between(in Vector3D from, in Vector3D to)
        {
            Vector3D axis = Vector3D.Cross(from, to);
            double sin = axis.Length();
            return sin < 1e-12 ? Vector3D.Zero : axis * (Math.Atan2(sin, Vector3D.Dot(from, to)) / sin);
        }

        /// <summary>The angle between two unit vectors, radians.</summary>
        private static double Angle(in Vector3D a, in Vector3D b) => Math.Atan2(Vector3D.Cross(a, b).Length(), Vector3D.Dot(a, b));

        /// <summary>A direction that held still in a frame that turned from <paramref name="was"/> to <paramref name="now"/> (orientations).</summary>
        private static Vector3D Carry(in Vector3D v, in MatrixD was, in MatrixD now) =>
            Vector3D.TransformNormal(Vector3D.TransformNormal(v, MatrixD.Transpose(was)), now);

        /// <summary>The body's middle, which the game turns it about (its physics centre), in the world.</summary>
        private static Vector3D Middle(MyCharacter character, in MatrixD world) =>
            world.Translation + Vector3D.TransformNormal((Vector3D)(character.Physics?.Center ?? Vector3.Zero), world);

        /// <summary>
        /// Once every 10 s with steps on magnetic boots: how fast the body's up turned relative to what the feet are on and how
        /// fast the smoothed up did (mean and peak), how far apart they got, and how fast the body's middle moved along its up
        /// (which is the controller's, and not smoothed).
        /// </summary>
        private static void Stats()
        {
            long now = Stopwatch.GetTimestamp();
            if (windowStart == 0)
            {
                windowStart = now;
                return;
            }
            double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
            if (seconds < StatsSeconds)
                return;
            if (steps > 0)
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "Magnetic boots {0:0.0} s: {1} steps on boots ({2} on a grid, {3} on voxels); the body's up turned {4:0.0} deg/s mean, {5:0.0} peak; " +
                    "the smoothed up {6:0.0} mean, {7:0.0} peak, up to {8:0.0} deg off the body's; the body's middle moved along its up {9:0.0} cm/s mean, {10:0.0} peak; {11}",
                    seconds, steps, gridSteps, voxelSteps, bodySum / steps, bodyPeak, smoothSum / steps, smoothPeak, offPeak,
                    liftSteps > 0 ? liftSum / liftSteps : 0.0, liftPeak,
                    Off ? "smoothing off, the view has the body's up"
                        : string.Format(CultureInfo.InvariantCulture, "smoothing on, {0:0} ms", SmoothSeconds * 1000.0)));
            windowStart = now;
            steps = gridSteps = voxelSteps = liftSteps = 0;
            bodySum = bodyPeak = smoothSum = smoothPeak = offPeak = liftSum = liftPeak = 0.0;
        }
    }
}
