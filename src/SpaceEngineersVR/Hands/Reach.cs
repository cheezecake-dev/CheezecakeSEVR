using System;
using System.Collections.Generic;
using System.Text;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// How far a hand acts on the world from: every path where a hand uses, presses, grabs or holds on to something takes
    /// its point through <see cref="Limit"/>, so a tracked hand pushed through a wall, or held out farther than an arm
    /// reaches, cannot act on what is behind it. The use and build rays and the held tool's sensors start there
    /// (<see cref="HandTools.ReachRay"/>), and the fingertip (<see cref="FingerPress"/>, <see cref="ScreenTouch"/>), the
    /// palm (<see cref="GrabUse"/>, <see cref="ItemGrab"/>) and the hull grab's hold (<see cref="HullGrab"/>) are limited
    /// the same way, and a held gun's muzzle is kept on the near side of a wall (<see cref="Unblocked"/>). What is only
    /// drawn (the hand, the laser) is not.
    /// </summary>
    internal static class Reach
    {
        /// <summary>
        /// How far from the head bone a hand acts, metres: an arm's length. The game has no reach of its own for a hand (its
        /// use line runs 5 m from the head, MyConstants.DEFAULT_INTERACTIVE_DISTANCE, and still does from where this leaves the hand).
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
        private static HkShape ball, startProbe;
        private static bool shapesMade;
        private static int errors;

        /// <summary>
        /// The point a hand at <paramref name="hand"/> acts from: within <see cref="ArmLength"/> of the character's head
        /// bone, and, when the line from the head bone to it runs into something solid (<see cref="Sweep"/>), no more than
        /// <see cref="SurfaceGive"/> past where it does. Game thread. Unchanged when there is no head bone to measure from.
        /// </summary>
        public static Vector3D Limit(MyCharacter character, Vector3D hand) => Stop(character, hand, ArmLength, SurfaceGive);

        /// <summary>
        /// <paramref name="point"/> brought back to the near side of the first solid thing on the line from the character's
        /// head bone to it, with no arm's length: for what a held item does from where the item is (a gun's muzzle), which
        /// may be farther out than a hand reaches but not on the far side of a wall.
        /// </summary>
        public static Vector3D Unblocked(MyCharacter character, Vector3D point) => Stop(character, point, double.MaxValue, 0.0);

        private static Vector3D Stop(MyCharacter character, Vector3D point, double maxLength, double give)
        {
            if (!TryHead(character, out Vector3D head))
                return point;
            Vector3D way = point - head;
            double length = way.Length();
            if (!(length > 1e-4))
                return point;
            way /= length;
            length = Math.Min(length, maxLength);
            if (Sweep(head, head + way * length, null, BlockOf(Seat(character)), null, out float first, out object blocker) && blocker != null)
                length = Math.Min(length, first * length + give);
            return head + way * length;
        }

        /// <summary>The head bone in the world, or the head matrix where the skeleton is not there.</summary>
        private static bool TryHead(MyCharacter character, out Vector3D head)
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

        /// <summary>The seat the character sits in, or null.</summary>
        private static MyEntity Seat(MyCharacter character) =>
            MySession.Static?.ControlledEntity is MyShipController seat && seat.Pilot == character ? seat : null;

        /// <summary>
        /// Sweeps a 1 cm ball from <paramref name="origin"/> to <paramref name="to"/> (<c>MyPhysics.CastShapeReturnContactBodyDatas</c>
        /// on the character collision layer, as <see cref="SurfaceStop"/> sweeps the drawn hands) and gives the first solid
        /// thing it meets (<see cref="SurfaceStop.Solid"/>: a grid that is not a projection, or voxels), as a share of the
        /// way, in <paramref name="first"/>; <paramref name="blocker"/> is null when nothing is in the way. Passed: back
        /// faces (the line leaving something), <paramref name="target"/> and <paramref name="seat"/>, and whatever the line
        /// starts inside (a 2 mm probe at the origin). <paramref name="passed"/>, if given, gets what was passed and why.
        /// False when physics cannot be asked from here (inside the simulation, another thread) or the sweep failed: the
        /// line was not checked, and callers take it as clear.
        /// </summary>
        internal static bool Sweep(Vector3D origin, Vector3D to, MySlimBlock target, MySlimBlock seat, StringBuilder passed,
            out float first, out object blocker)
        {
            first = 1f;
            blocker = null;
            if (errors >= 3 || !MyPhysics.CheckThread() || MyPhysics.InsideSimulation)
                return false;
            Vector3D way = to - origin;
            double length = way.Length();
            if (length < 2 * BallRadius)
                return true;
            try
            {
                if (!shapesMade)
                {
                    ball = new HkSphereShape(BallRadius).Base;
                    startProbe = new HkSphereShape(StartProbeRadius).Base;
                    shapesMade = true;
                }
                startsIn.Clear();
                Vector3D start = origin;
                Quaternion none = Quaternion.Identity;
                MyPhysics.GetPenetrationsShape(startProbe, ref start, ref none, startsIn, MyPhysics.CollisionLayers.CharacterCollisionLayer);

                hits.Clear();
                MatrixD at = MatrixD.CreateTranslation(origin);
                MyPhysics.CastShapeReturnContactBodyDatas(to, ball, ref at, (uint)MyPhysics.CollisionLayers.CharacterCollisionLayer, 0f, hits);

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
                        : seat != null && block == seat ? "the seat"
                        : StartsInside(info, owner, block, origin) ? "starts inside it"
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
                return true;
            }
            catch (Exception e)
            {
                Log.Error(e, ++errors < 3 ? "Reach: the line from the body to the hand could not be checked" : "Reach: the line from the body to the hand failed again; not checked until the game restarts");
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

        /// <summary>Whether the line began inside what it hit (the probe at its start was in the same body and block, or in the voxels).</summary>
        private static bool StartsInside(HkHitInfo info, MyEntity owner, MySlimBlock block, Vector3D origin)
        {
            if (owner is MyVoxelBase)
            {
                onePoint[0] = origin;
                MyVoxelBase voxels = info.GetHitEntity() as MyVoxelBase ?? (MyVoxelBase)owner;
                return voxels.IsAnyOfPointInside(onePoint);
            }
            foreach (HkBodyCollision c in startsIn)
            {
                if (c.Body != info.Body)
                    continue;
                if (block == null || (owner is MyCubeGrid grid && SurfaceStop.BlockOf(grid, c.ShapeKey) == block))
                    return true;
            }
            return false;
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
