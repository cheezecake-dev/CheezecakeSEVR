using System;
using System.Collections.Generic;
using System.Text;
using Havok;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// How far a hand acts on the world from: every path where a hand uses, presses, grabs or holds on to something takes
    /// its point through <see cref="Limit"/>, so a tracked hand pushed through a wall, or held out farther than an arm
    /// reaches, cannot act on what is behind it. The use, build and ping rays and the held tool's sensors start there
    /// (<see cref="HandTools.ReachRay"/>), and the fingertip (<see cref="FingerPress"/>, <see cref="ScreenTouch"/>), the
    /// palm (<see cref="GrabUse"/>, <see cref="ItemGrab"/>) and the hull grab's hold (<see cref="HullGrab"/>) are limited
    /// the same way, and a held gun's muzzle is kept on the near side of a wall (<see cref="Unblocked"/>). What is only
    /// drawn (the hand, the laser) is not.
    /// </summary>
    internal static class Reach
    {
        /// <summary>
        /// How far from the head bone a hand acts, metres: an arm's length. The game has no reach of its own for a hand (its
        /// use line runs MyConstants.DEFAULT_INTERACTIVE_DISTANCE from the head; from the hand it ends no farther from the head, <see cref="HandUse"/>).
        /// </summary>
        public const double ArmLength = 0.8;

        /// <summary>How far past the surface the line from the head first meets the point may go, metres: enough to press a button flush with a panel, too little to pass through anything.</summary>
        public const double SurfaceGive = 0.02;

        /// <summary>The line is swept with a ball this wide (SurfaceStop's fingertip ball), metres.</summary>
        private const float BallRadius = 0.01f;

        /// <summary>A probe this wide at the start of the line finds what it starts inside (a seat round the head), metres.</summary>
        private const float StartProbeRadius = 0.002f;

        private static readonly List<MyPhysics.HitInfo> hits = new List<MyPhysics.HitInfo>();
        private static readonly List<HkBodyCollision> startsIn = new List<HkBodyCollision>();
        private static readonly Vector3D[] onePoint = new Vector3D[1];
        private static readonly Dictionary<float, HkShape> balls = new Dictionary<float, HkShape>();

        /// <summary>The hands' own failures (<see cref="Sweep(Vector3D, Vector3D, MySlimBlock, MySlimBlock, StringBuilder, out float, out object)"/>).</summary>
        private static readonly Failures hands = new Failures(
            "Reach: a line from the body could not be checked; the hands reach nothing past the head in that frame",
            "Reach: a line from the body failed again; until the next world loads the hands reach nothing past the head, and use is the game's own from the head");

        /// <summary>
        /// The game method a world load goes through (MySession.LoadWorld): each caller's failure count starts again there
        /// (<see cref="Failures.Reset"/>), so a few stray errors in one world do not decide the next.
        /// </summary>
        internal static System.Reflection.MethodBase WorldLoad() =>
            AccessTools.DeclaredMethod(typeof(MySession), "LoadWorld", new[] { typeof(MyObjectBuilder_Checkpoint), typeof(MyObjectBuilder_Sector) });

        /// <summary>The hands' failure count starts again with each world (<see cref="Failures.Reset"/>), so it is a world's failures that give up, not a run's.</summary>
        public static void Patch(Harmony harmony) =>
            PatchRegistry.Hook(harmony, typeof(Reach), "Reach", "the hands' failure count carries over from one world to the next", "world load",
                WorldLoad(), prefix: nameof(NewWorld));

        private static void NewWorld()
        {
            if (hands.Reset())
                Log.Info("Reach: a world is loading; the hands' lines are checked again");
        }

        /// <summary>
        /// Any thread: the hands' sweeps have failed three times and no longer run (until the next world loads). From then on <see cref="Limit"/> and
        /// <see cref="Unblocked"/> stay at the head, so a hand presses and grabs nothing, and use is what the game itself
        /// allows from the head (<see cref="HandUse"/> leaves its detectors on the head's own line).
        /// </summary>
        public static bool GivenUp => hands.GivenUp;

        /// <summary>
        /// How often a caller's sweeps have failed, and the log lines for it: at three, its sweeps stop until the next world
        /// loads (<see cref="Reset"/>). Each caller keeps its own, so the hands failing does not switch another caller's
        /// sweeps off, nor the reverse.
        /// </summary>
        internal sealed class Failures
        {
            private const int Limit = 3;
            private readonly string failed, gaveUp;
            private volatile int count;

            /// <param name="failed">The log line for a failure that is not the last.</param>
            /// <param name="gaveUp">The log line for the third, which says what happens from then on.</param>
            public Failures(string failed, string gaveUp)
            {
                this.failed = failed;
                this.gaveUp = gaveUp;
            }

            /// <summary>Any thread: three failures have been counted, and this caller's sweeps no longer run.</summary>
            public bool GivenUp => count >= Limit;

            /// <summary>Any thread: forgets the failures counted, so the caller's sweeps run again (a new world). True when there were any.</summary>
            public bool Reset()
            {
                bool had = count > 0;
                count = 0;
                return had;
            }

            /// <summary>Counts one failure and logs it; the third is logged once, as the last.</summary>
            public void Add(Exception e)
            {
                if (GivenUp)
                    return;
                Log.Error(e, ++count < Limit ? failed : gaveUp);
            }
        }

        /// <summary>
        /// The point a hand at <paramref name="hand"/> acts from: within <see cref="ArmLength"/> of the character's head
        /// bone, and, when the line from the head bone to it runs into something solid (<see cref="Sweep(Vector3D, Vector3D, MySlimBlock, MySlimBlock, StringBuilder, out float, out object)"/>; in a closed
        /// cockpit, also out of its cells), no more than <see cref="SurfaceGive"/> past where it does. Game thread. A line
        /// that cannot be checked (physics busy, or a failed cast, <see cref="Sweep(Vector3D, Vector3D, MySlimBlock, MySlimBlock, StringBuilder, out float, out object)"/>)
        /// is taken as blocked at its start: the head bone, so in that frame the hand reaches nothing beyond it. Unchanged
        /// only when there is no character to measure from.
        /// </summary>
        public static Vector3D Limit(MyCharacter character, Vector3D hand) => Stop(character, hand, ArmLength, SurfaceGive, null);

        /// <summary>
        /// <see cref="Limit(MyCharacter, Vector3D)"/>, with the line passing through <paramref name="target"/>: for reaching
        /// something set into that block's own face (a button panel's button), which every other surface still stops.
        /// </summary>
        public static Vector3D Limit(MyCharacter character, Vector3D hand, MySlimBlock target) => Stop(character, hand, ArmLength, SurfaceGive, target);

        /// <summary>
        /// <paramref name="point"/> brought back to the near side of the first solid thing on the line from the character's
        /// head bone to it, with no arm's length: for what a held item does from where the item is (a gun's muzzle), which
        /// may be farther out than a hand reaches but not on the far side of a wall. A line that cannot be checked puts it
        /// at the head bone, as <see cref="Limit"/> does.
        /// </summary>
        public static Vector3D Unblocked(MyCharacter character, Vector3D point) => Stop(character, point, double.MaxValue, 0.0, null);

        private static Vector3D Stop(MyCharacter character, Vector3D point, double maxLength, double give, MySlimBlock target)
        {
            if (!TryHead(character, out Vector3D head))
                return point;
            Vector3D way = point - head;
            double length = way.Length();
            if (!(length > 1e-4))
                return point;
            way /= length;
            length = Math.Min(length, maxLength);
            bool checkedLine;
            float first = 1f;
            object blocker = null;
            try
            {
                checkedLine = Sweep(head, head + way * length, target, ClosedSeat(character), null, out first, out blocker);
            }
            catch (Exception e)
            {
                hands.Add(e);
                checkedLine = false;
            }
            // An unchecked line is not a clear one: the hand reaches nothing past the line's start.
            if (!checkedLine)
                return head;
            if (blocker != null)
                length = Math.Min(length, first * length + give);
            return head + way * length;
        }

        /// <summary>The head bone in the world, or the head matrix where the skeleton is not there.</summary>
        internal static bool TryHead(MyCharacter character, out Vector3D head)
        {
            head = Vector3D.Zero;
            if (character?.PositionComp == null)
                return false;
            var bones = character.AnimationController?.CharacterBones;
            int index = CharacterFields.HeadBoneIndex(character);
            if (bones != null && index >= 0 && index < bones.Length && bones[index] != null)
            {
                head = Vector3D.Transform((Vector3D)bones[index].AbsoluteTransform.Translation, character.PositionComp.WorldMatrixRef);
                return true;
            }
            head = character.GetHeadMatrix(false, true, false, true).Translation;
            return true;
        }

        /// <summary>
        /// The block of the cockpit the character sits in when the game counts it as closed (<c>MyCockpitDefinition.IsPressurized</c>,
        /// which is the game's own "inside a closed cockpit", MySectorWeatherComponent's m_insideClosedCockpit); null in an
        /// open seat or on foot. A line from inside it does not leave its cells (<see cref="Sweep(Vector3D, Vector3D, MySlimBlock, MySlimBlock, StringBuilder, out float, out object)"/>).
        /// </summary>
        internal static MySlimBlock ClosedSeat(MyCharacter character) =>
            character?.Parent is MyCockpit seat && seat.BlockDefinition != null && seat.BlockDefinition.IsPressurized ? seat.SlimBlock : null;

        /// <summary>
        /// Sweeps a 1 cm ball from <paramref name="origin"/> to <paramref name="to"/> (<c>MyPhysics.CastShapeReturnContactBodyDatas</c>
        /// on the character collision layer, as <see cref="SurfaceStop"/> sweeps the drawn hands) and gives the first solid
        /// thing it meets (<see cref="SurfaceStop.Solid"/>: a grid that is not a projection, or voxels), as a share of the
        /// way, in <paramref name="first"/>; <paramref name="blocker"/> is null when nothing is in the way. Passed: back
        /// faces (the line leaving something), <paramref name="target"/>, and the collision shape the line starts inside (a
        /// 2 mm probe at the origin; only that shape, so a seat's back the head rests in is passed and its canopy is not).
        /// With <paramref name="closedSeat"/> (<see cref="ClosedSeat"/>), leaving that block's cells also stops the line, so
        /// its canopy and shell stop it however coarse their collision is. <paramref name="passed"/>, if given, gets what was
        /// passed and why. False when physics cannot be asked from here (inside the simulation, another thread) or the sweep
        /// failed (the hands' own count stops them at three): the line was not checked, and callers take it as blocked.
        /// </summary>
        internal static bool Sweep(Vector3D origin, Vector3D to, MySlimBlock target, MySlimBlock closedSeat, StringBuilder passed,
            out float first, out object blocker) =>
            Sweep(origin, to, BallRadius, StartProbeRadius, target, closedSeat, passed, hands, out first, out blocker);

        /// <summary>
        /// <see cref="Sweep(Vector3D, Vector3D, MySlimBlock, MySlimBlock, StringBuilder, out float, out object)"/> with a ball
        /// of <paramref name="radius"/>, and a probe of <paramref name="probeRadius"/> for what it starts inside. Its failures
        /// are counted in <paramref name="failures"/>, the caller's own: three stop this caller's sweeps (false from then on),
        /// and no one else's.
        /// </summary>
        internal static bool Sweep(Vector3D origin, Vector3D to, float radius, float probeRadius, MySlimBlock target, MySlimBlock closedSeat,
            StringBuilder passed, Failures failures, out float first, out object blocker)
        {
            first = 1f;
            blocker = null;
            if (failures.GivenUp || !MyPhysics.CheckThread() || MyPhysics.InsideSimulation)
                return false;
            Vector3D way = to - origin;
            double length = way.Length();
            if (length < 2 * BallRadius)
                return true;
            try
            {
                startsIn.Clear();
                Vector3D start = origin;
                Quaternion none = Quaternion.Identity;
                MyPhysics.GetPenetrationsShape(Ball(probeRadius), ref start, ref none, startsIn, MyPhysics.CollisionLayers.CharacterCollisionLayer);

                hits.Clear();
                MatrixD at = MatrixD.CreateTranslation(origin);
                MyPhysics.CastShapeReturnContactBodyDatas(to, Ball(radius), ref at, (uint)MyPhysics.CollisionLayers.CharacterCollisionLayer, 0f, hits);

                int notes = 0;
                foreach (MyPhysics.HitInfo hit in hits)
                {
                    HkHitInfo info = hit.HkHitInfo;
                    if (Vector3D.Dot(info.Normal, way) > 0.0)
                        continue;
                    MyEntity owner = SurfaceStop.Solid(info, out MySlimBlock block, out object what);
                    if (owner == null)
                        continue;
                    string why = target != null && block == target ? "the target's own block"
                        : StartsInside(info, owner, origin) ? "starts inside it"
                        : null;
                    if (why != null)
                    {
                        if (passed != null && notes++ < 4)
                            passed.Append(passed.Length == 0 ? string.Empty : ", ")
                                  .Append($"{SurfaceStop.Describe(what)} at {info.HitFraction * length * 100:0} cm ({why})");
                        continue;
                    }
                    if (blocker == null || info.HitFraction < first)
                    {
                        first = info.HitFraction;
                        blocker = what;
                    }
                }
                if (closedSeat != null && LeavesCells(closedSeat, origin, to, out float leaves) && (blocker == null || leaves < first))
                {
                    first = leaves;
                    blocker = (object)closedSeat.FatBlock ?? closedSeat;
                    if (passed != null)
                        passed.Append(passed.Length == 0 ? string.Empty : ", ").Append($"stopped where it leaves {SurfaceStop.Describe(blocker)}'s cells, {leaves * length * 100:0} cm along");
                }
                return true;
            }
            catch (Exception e)
            {
                failures.Add(e);
                first = 1f;
                blocker = null;
                return false;
            }
            finally
            {
                hits.Clear();
                startsIn.Clear();
            }
        }

        /// <summary>
        /// Whether the line began inside what it hit: the probe at its start was in the same body and the same collision shape
        /// of it (a grid's shape key is one convex piece of one block), or in the voxels.
        /// </summary>
        private static bool StartsInside(HkHitInfo info, MyEntity owner, Vector3D origin)
        {
            if (owner is MyVoxelBase)
            {
                onePoint[0] = origin;
                MyVoxelBase voxels = info.GetHitEntity() as MyVoxelBase ?? (MyVoxelBase)owner;
                return voxels.IsAnyOfPointInside(onePoint);
            }
            uint key = info.ShapeKeyCount > 0 ? info.GetShapeKey(0) : uint.MaxValue;
            foreach (HkBodyCollision c in startsIn)
            {
                if (c.Body == info.Body && c.ShapeKey == key)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Where the line from <paramref name="origin"/> to <paramref name="to"/> leaves <paramref name="block"/>'s cells, as a
        /// share of the way; false when it starts outside them or ends inside them.
        /// </summary>
        private static bool LeavesCells(MySlimBlock block, Vector3D origin, Vector3D to, out float leaves)
        {
            leaves = 1f;
            MyCubeGrid grid = block.CubeGrid;
            if (grid?.PositionComp == null)
                return false;
            double size = grid.GridSize;
            var cells = new BoundingBoxD(((Vector3D)block.Min - 0.5) * size, ((Vector3D)block.Max + 0.5) * size);
            MatrixD toGrid = grid.PositionComp.WorldMatrixNormalizedInv;
            Vector3D from = Vector3D.Transform(origin, toGrid);
            Vector3D way = Vector3D.Transform(to, toGrid) - from;
            if (cells.Contains(from) != ContainmentType.Contains)
                return false;
            double t = 1.0;
            for (int axis = 0; axis < 3; axis++)
            {
                double d = way.GetDim(axis);
                if (d > 1e-9)
                    t = Math.Min(t, (cells.Max.GetDim(axis) - from.GetDim(axis)) / d);
                else if (d < -1e-9)
                    t = Math.Min(t, (cells.Min.GetDim(axis) - from.GetDim(axis)) / d);
            }
            if (!(t < 1.0))
                return false;
            leaves = (float)Math.Max(t, 0.0);
            return true;
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

        /// <summary>The block an entity is, or is a part of (a door's leaf, a seat); null for anything else.</summary>
        internal static MySlimBlock BlockOf(MyEntity entity)
        {
            for (MyEntity e = entity; e != null; e = e.Parent)
            {
                if (e is MyCubeBlock block)
                    return block.SlimBlock;
            }
            return null;
        }
    }
}
