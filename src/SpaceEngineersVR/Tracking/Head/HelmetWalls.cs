// HelmetWalls: the helmet held off walls, blocks and voxels, the eye stopping with it as the real head leans on.
using System;
using System.Collections.Generic;
using Havok;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Hands;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// With the helmet on the eyes (<see cref="Rendering.HelmetView"/>), the helmet is held off what it leans on: lean the
    /// real head into a wall, a console, a voxel slope or a low ceiling and the eye stops where the helmet meets it, and follows the
    /// head again as soon as the head comes back. Along the wall, and in every turn, the eye still follows the head one to one; only
    /// the move into the wall is held. It is the eye the camera, both eyes, the HUD, the aim and the hands are built from
    /// (<see cref="CharacterHead"/>) that is held, and the helmet is posed on that eye (<see cref="HelmetHead"/>), so they all
    /// stop together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The helmet is taken as a ball <see cref="RadiusMm"/> round its middle: the head bone, with the helmet placed on the eye and
    /// turned with it (<see cref="HelmetHead.CentreFromEye"/>: 7.5 cm behind the eye and 2.7 cm above it, the helmet upright). The
    /// shell is 33 x 32 x 29 cm round that point (<see cref="Rendering.HelmetView"/>); the ball is 19 cm, a little more, so that an eye
    /// held off what it faces stays more than 11 cm from it, where the wall fade (<see cref="Rendering.WallFade"/>: its 5 cm near-plane
    /// ball and 6 cm lead) has not started to darken the view. At 17 cm, the shell's own size, a head leaning straight into a wall would
    /// have the view held about a sixth dark.
    /// </para>
    /// <para>
    /// Once a simulation frame, on the first head matrix built with the head's offset in it that can ask physics (the camera, the aim,
    /// the helmet's pose and the hands all take that one frame's hold), the ball is swept (MyPhysics.CastShapeReturnContactBodyDatas,
    /// on the character's collision layer, every hit along the way) from the axis of the character's own collision capsule, at the
    /// height of the helmet's middle, to that middle where the head puts it, and <see cref="Beyond"/> past it. The capsule is a metre
    /// wide (Characters.sbc, MyCharacterProxy): nothing comes nearer its axis than half a metre at the waist and some 30 cm at the eyes'
    /// height, where it rounds off, so the ball starts clear. Its start is never higher than the capsule's top (crouched, the crouch
    /// capsule's) less the ball and <see cref="TopMargin"/>. Solid is what the hands and the wall fade take as solid (<see cref="SurfaceStop.Solid"/>): a grid that is
    /// not a projection, its doors and moving parts with it, and voxels. Not the character itself or anything it holds, not other
    /// characters (their collision is that metre-wide capsule, far larger than the astronaut drawn), not loose items. A hit the ball
    /// already touches where it starts (the body pressed into something: a ladder, a squeeze) holds nothing; the wall fade covers it.
    /// </para>
    /// <para>
    /// Each hit is a plane: the ball's middle where it touched, and the surface's normal there. The helmet's middle is moved out in
    /// front of every plane, by the plane's normal and in a few passes for a corner: sliding the head along a wall the eye moves with
    /// it one to one and is only held off the wall, and pushed into a corner it is held off both walls. The hold is soft: it starts
    /// <see cref="Band"/> before the ball would touch, and less and less of the head's move reaches the eye from there, so the eye
    /// never stops dead and the ball never quite touches. A plane the line from the body only grazes (the line passing a door jamb's
    /// edge) holds less the more it grazes it, so a hit that comes or goes at an edge does not move the eye. A hold that grows is taken
    /// at once; one that shrinks lets the eye back at no more than <see cref="ReleaseSpeed"/>, so a hit that vanishes (a door opening)
    /// lets the eye go over a few frames, not in one.
    /// </para>
    /// <para>
    /// The renderer turns the eyes by how far the head has moved since the game frame (<see cref="CameraHeadLink"/>); while the helmet
    /// is held, that move into the wall is held as the game frame held it (<see cref="LateHold"/>), so pushing on into the wall does not
    /// shake the view at the headset's rate.
    /// </para>
    /// <para>
    /// Only while the helmet is worn: on foot in first person with the helmet view on (<see cref="Rendering.HelmetView.Worn"/>). Not in
    /// a seat, where the helmet does not turn with the head and the seat's own reach and the wall fade hold the eye, nor in third person. Up a ladder, on
    /// the jetpack and in zero g it holds as on foot, from the capsule as the body is turned; crouched, from the crouch capsule. Far past
    /// the hold the view stays at the wall while the real head goes on into it. RenderDebug HelmetWalls=0 turns it off, HelmetWallsMm=n
    /// sets the ball's radius (50 to 250 mm).
    /// </para>
    /// </remarks>
    internal static class HelmetWalls
    {
        /// <summary>Debug: the helmet not held off anything (<see cref="Rendering.RenderDebug"/> HelmetWalls=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The ball's radius by default, millimetres.</summary>
        public const int DefaultRadiusMm = 190;

        /// <summary>Debug: the ball's radius, millimetres (<see cref="Rendering.RenderDebug"/> HelmetWallsMm=n, 50 to 250).</summary>
        public static int RadiusMm { get; set; } = DefaultRadiusMm;

        /// <summary>The hold starts this far, metres, before the ball would touch, and takes the head's move in smoothly from there.</summary>
        private const double Band = 0.03;

        /// <summary>The sweep goes this far past the helmet's middle, metres: what is within <see cref="Band"/> of it head on is found well before it holds.</summary>
        private const double Beyond = 3 * Band;

        /// <summary>A plane the line meets at a cosine below this to its normal holds less, and nothing as the line only grazes it.</summary>
        private const double Graze = 0.2;

        /// <summary>The sweep starts this far, metres, below the capsule's top beyond the ball itself.</summary>
        private const float TopMargin = 0.04f;

        /// <summary>The eye is let back toward where the head puts it at no more than this, metres a second.</summary>
        private const double ReleaseSpeed = 1.0;

        /// <summary>A hit nearer the sweep's start than this, metres, is something the ball already touches there.</summary>
        private const double StartTouch = 0.001;

        /// <summary>The eye held back by more than this, metres, counts as held for the log.</summary>
        private const double HeldLogged = 0.01;

        private const int MaxPlanes = 12, Passes = 4;

        /// <summary>More simulation frames than this since the last measure and the hold starts again.</summary>
        private const long GapFrames = 30;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(HelmetWalls));
        private static readonly List<MyPhysics.HitInfo> hits = new List<MyPhysics.HitInfo>();
        private static readonly Dictionary<float, HkShape> balls = new Dictionary<float, HkShape>();
        private static readonly Vector3D[] planeAt = new Vector3D[MaxPlanes];
        private static readonly Vector3D[] planeNormal = new Vector3D[MaxPlanes];
        private static readonly double[] planeHold = new double[MaxPlanes];

        // The hold (world, added to the eye) for which character and from which simulation frame; whether any is in place.
        private static MyCharacter heldFor;
        private static ulong measuredFrame = ulong.MaxValue;
        private static Vector3D held;
        private static bool active;

        // The first hold, for the log: logged as it starts, and its deepest as it ends.
        private static bool firstStarted, firstEnded;
        private static double firstDeepest;

        // For the renderer (LateHold): the way the eye is held most, in the tracking space, and the share of the head's move along it held.
        private static readonly object gate = new object();
        private static Vector3 lateWay;
        private static float lateShare;

        /// <summary>
        /// Game thread, for every first-person head matrix <see cref="CharacterHead"/> builds with the head's offset in it: what to
        /// add to the translation of <paramref name="eye"/> (the head matrix, world) to hold the helmet off what it leans on. Zero
        /// when the helmet is not worn or nothing is near. Measured once a simulation frame, the same for every head matrix in it;
        /// a frame in which physics cannot be asked keeps the last. <paramref name="basis"/> is the body's (forward, up), which the
        /// head is built on.
        /// </summary>
        public static Vector3D Hold(MyCharacter character, in MatrixD eye, in MatrixD basis)
        {
            if (Off || !guard.On || !Rendering.HelmetView.Worn(character))
            {
                Clear();
                return Vector3D.Zero;
            }
            try
            {
                ulong frame = MySandboxGame.Static.SimulationFrameCounter;
                if (frame != measuredFrame && MyPhysics.CheckThread() && !MyPhysics.InsideSimulation)
                {
                    long frames = heldFor == character && measuredFrame != ulong.MaxValue && frame > measuredFrame ? (long)(frame - measuredFrame) : GapFrames + 1;
                    measuredFrame = frame;
                    heldFor = character;
                    active = true;
                    Measure(character, eye, basis, frames);
                }
                return heldFor == character ? held : Vector3D.Zero;
            }
            catch (Exception e)
            {
                Clear();
                guard.Failed(e, "Helmet walls failed");
                return Vector3D.Zero;
            }
        }

        /// <summary>
        /// Render thread, as the eyes are placed (the head matrix the camera was built from was held): a shift to put into their
        /// placement (pose * this * from-camera, in the tracking space) that holds <paramref name="late"/>, the head's move since the
        /// game frame (the head now less the head in the camera), along the way the helmet is held, by as much as the game frame
        /// held it. False, and no shift, while nothing is held.
        /// </summary>
        internal static bool LateHold(Vector3 late, out Matrix shift)
        {
            Vector3 way;
            float share;
            lock (gate)
            {
                way = lateWay;
                share = lateShare;
            }
            if (!(share > 0f))
            {
                shift = Matrix.Identity;
                return false;
            }
            shift = Matrix.CreateTranslation(-way * (Vector3.Dot(late, way) * share));
            return true;
        }

        /// <summary>Measures this frame's hold for the eye at <paramref name="eye"/>; <paramref name="frames"/> since the last.</summary>
        private static void Measure(MyCharacter character, in MatrixD eye, in MatrixD basis, long frames)
        {
            float radius = RadiusMm / 1000f;
            MatrixD body = character.PositionComp.WorldMatrixRef;
            Vector3D up = body.Up;

            // The helmet's middle where the head puts it, and the sweep's start on the capsule's axis at its height.
            Vector3D centre = eye.Translation + Vector3D.TransformNormal((Vector3D)HelmetHead.CentreFromEye(character), basis);
            MyCharacterDefinition definition = character.Definition;
            double top = (character.IsCrouching ? definition.CharacterCollisionCrouchHeight : definition.CharacterCollisionHeight) - radius - TopMargin;
            double bottom = Math.Min(top, definition.CharacterCollisionWidth * 0.5);
            double height = Math.Max(bottom, Math.Min(top, Vector3D.Dot(centre - body.Translation, up)));
            Vector3D from = body.Translation + up * height;

            Vector3D way = centre - from;
            double length = way.Length();
            int planes = 0;
            object holder = null;
            double most = 0.0;
            Vector3D mostWay = Vector3D.Zero;
            if (length > 1e-4 && !double.IsNaN(length) && !double.IsInfinity(length))
            {
                way /= length;
                double sweep = length + Beyond;
                try
                {
                    hits.Clear();
                    MatrixD at = MatrixD.CreateTranslation(from);
                    MyPhysics.CastShapeReturnContactBodyDatas(from + way * sweep, Ball(radius), ref at, (uint)MyPhysics.CollisionLayers.CharacterCollisionLayer, 0f, hits);
                    foreach (MyPhysics.HitInfo hit in hits)
                    {
                        if (planes == MaxPlanes)
                            break;
                        HkHitInfo info = hit.HkHitInfo;
                        Vector3D normal = info.Normal;
                        double normalLength = normal.Length();
                        if (!(normalLength > 1e-6))
                            continue;
                        normal /= normalLength;
                        // Head on 1, grazing 0; leaving it (a back face), below 0.
                        double facing = -Vector3D.Dot(normal, way);
                        double along = info.HitFraction * sweep;
                        if (!(facing > 0.0) || along < StartTouch)
                            continue;
                        if (SurfaceStop.Solid(info, out _, out object what) == null)
                            continue;
                        Vector3D touched = from + way * along;
                        double free = Vector3D.Dot(centre - touched, normal);
                        double weight = Smooth(facing / Graze);
                        planeAt[planes] = touched;
                        planeNormal[planes] = normal;
                        planeHold[planes] = Math.Min(Band, free + weight * (Soft(free) - free));
                        // How much of the head's move toward it is held there: the renderer holds the same share of the late move.
                        double share = weight * (1.0 - Slope(free));
                        if (share > most)
                        {
                            most = share;
                            mostWay = normal;
                            holder = what;
                        }
                        planes++;
                    }
                }
                finally
                {
                    hits.Clear();
                }
            }

            // Out in front of every plane, by its normal; a few passes settle a corner.
            Vector3D placed = centre;
            for (int pass = 0; pass < Passes; pass++)
            {
                bool moved = false;
                for (int i = 0; i < planes; i++)
                {
                    double ahead = Vector3D.Dot(placed - planeAt[i], planeNormal[i]);
                    if (ahead < planeHold[i])
                    {
                        placed += planeNormal[i] * (planeHold[i] - ahead);
                        moved = true;
                    }
                }
                if (!moved)
                    break;
            }
            Vector3D wanted = placed - centre;

            // Held deeper at once; let back no faster than ReleaseSpeed.
            if (frames <= GapFrames && wanted.LengthSquared() < held.LengthSquared())
            {
                Vector3D back = wanted - held;
                double by = back.Length();
                double step = ReleaseSpeed * frames / 60.0;
                held = by > step ? held + back * (step / by) : wanted;
            }
            else
                held = wanted;

            // For the renderer: the way held most, from the world into the tracking space (the head matrix's turn back to the head,
            // then the tracked head's turn), and the share held along it.
            Vector3 lateIn = Vector3.Zero;
            if (most > 0.0)
            {
                Vector3D inHead = Vector3D.TransformNormal(mostWay, MatrixD.Transpose(eye.GetOrientation()));
                lateIn = Vector3.TransformNormal((Vector3)inHead, GameHead.TrackingPose.GetOrientation());
            }
            lock (gate)
            {
                lateWay = lateIn;
                lateShare = most > 0.0 ? (float)most : 0f;
            }

            Note(radius, holder);
        }

        /// <summary>No hold: the helmet is not worn, the switch is off, or the measure failed.</summary>
        private static void Clear()
        {
            if (!active)
                return;
            active = false;
            heldFor = null;
            measuredFrame = ulong.MaxValue;
            held = Vector3D.Zero;
            lock (gate)
                lateShare = 0f;
        }

        /// <summary>The first hold of the run: a line as the eye is first held back more than <see cref="HeldLogged"/>, and one with its deepest as it lets go.</summary>
        private static void Note(float radius, object holder)
        {
            if (firstEnded)
                return;
            double by = held.Length();
            if (!firstStarted)
            {
                if (by < HeldLogged)
                    return;
                firstStarted = true;
                firstDeepest = by;
                Log.Info($"Helmet walls: the eye is held back {by * 100:F1} cm from where the head puts it, the helmet ({radius * 100f:F0} cm round its middle) " +
                         $"on {(holder != null ? SurfaceStop.Describe(holder) : "something it leans on")}; RenderDebug HelmetWalls=0 lets the head through, HelmetWallsMm=n sets the helmet's size");
                return;
            }
            firstDeepest = Math.Max(firstDeepest, by);
            if (by > 0.001)
                return;
            firstEnded = true;
            Log.Info($"Helmet walls: the first hold let go; the eye was held back {firstDeepest * 100:F1} cm at most");
        }

        /// <summary>Where the ball's middle is held, as its distance in front of a plane, for a head that puts it <paramref name="free"/> in front (negative: behind): the same from <see cref="Band"/> out, nearer in smoothly closer, never at the plane.</summary>
        private static double Soft(double free) => free >= Band ? free : Band * Math.Exp((free - Band) / Band);

        /// <summary>How much of the head's move toward a plane reaches the ball <paramref name="free"/> in front of it, 1 to 0.</summary>
        private static double Slope(double free) => free >= Band ? 1.0 : Math.Exp((free - Band) / Band);

        /// <summary>0 at 0 or less, 1 at 1 or more, smoothly between.</summary>
        private static double Smooth(double t)
        {
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>A sphere shape of <paramref name="radius"/>, made once and kept.</summary>
        private static HkShape Ball(float radius)
        {
            if (!balls.TryGetValue(radius, out HkShape shape))
            {
                shape = new HkSphereShape(radius).Base;
                balls[radius] = shape;
            }
            return shape;
        }
    }
}
