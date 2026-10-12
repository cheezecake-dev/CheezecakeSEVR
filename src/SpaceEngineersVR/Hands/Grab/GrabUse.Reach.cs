using System;
using System.Collections.Generic;
using System.Text;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    // Finding what the palm is on: the use-object detector scan and the line-of-reach checks.
    internal static partial class GrabUse
    {
        // ---- Finding what the palm is on ----

        /// <summary>
        /// Every use object of the blocks near the palm, in <see cref="candidates"/>, with its distance to the palm and how
        /// it is grabbed. The character's own and, in a seat, the seat's own block's are left out.
        /// </summary>
        private static void Gather(MyCharacter character, MyEntity seat, Place place, Vector3D palm)
        {
            candidates.Clear();
            nearby.Clear();
            bool seated = place == Place.Seated;
            var sphere = new BoundingSphereD(palm, GatherRadius);
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, nearby);
            foreach (MyEntity entity in nearby)
            {
                if (entity == null || entity.Closed || entity.MarkedForClose || entity.PositionComp == null || entity.Components == null)
                    continue;
                if (ReferenceEquals(entity, character) || (seat != null && ReferenceEquals(entity, seat)))
                    continue;
                if (!entity.Components.TryGet(out MyUseObjectsComponentBase found) || !(found is MyUseObjectsComponent component))
                    continue;
                MatrixD world = entity.PositionComp.WorldMatrixRef;
                var detectors = component.DetectorInteractiveObjects;
                // The game edits this table under its own lock when a block's detectors change.
                lock (detectors)
                {
                    foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in detectors)
                    {
                        IMyUseObject use = pair.Value.UseObject;
                        if (use == null || use.SupportedActions == UseActionEnum.None || ReferenceEquals(use.Owner, character)
                            || (seat != null && ReferenceEquals(use.Owner, seat)))
                            continue;
                        MatrixD box = (MatrixD)pair.Value.Matrix * world;
                        double distance = FingerPress.DistanceToBox(box, palm);
                        if (distance > NearLogDistance)
                            continue;
                        candidates.Add(new Candidate
                        {
                            Use = use,
                            Kind = Classify(use, seated, out string why),
                            Why = why,
                            Distance = distance,
                            Center = Vector3D.Distance(palm, box.Translation),
                            Tag = use.Dummy?.Name ?? pair.Value.DetectorName,
                            BoxCenter = box.Translation,
                        });
                    }
                }
            }
            nearby.Clear();
        }

        private static readonly List<Candidate> inReach = new List<Candidate>();
        private static readonly Comparison<Candidate> nearestFirst =
            (a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Center.CompareTo(b.Center);

        /// <summary>
        /// The use object a hand takes: of those in <see cref="candidates"/> within <see cref="Reach"/> that are taken, the
        /// nearest (inside beats near, then the one whose middle is closest) the arm reaches without going through something
        /// solid (<see cref="LineOfReach"/>). With <paramref name="trace"/>, one log line of what was checked and why.
        /// </summary>
        private static bool BestReachable(Hand hand, MyCharacter character, MyEntity seat, Vector3D palm, in MatrixD trackingToModel,
            in MatrixD modelToWorld, bool trace, out Candidate best)
        {
            best = default;
            inReach.Clear();
            foreach (Candidate c in candidates)
            {
                if (c.Kind != Kind.None && c.Distance <= Reach)
                    inReach.Add(c);
            }
            if (inReach.Count == 0)
                return false;
            inReach.Sort(nearestFirst);

            Vector3D origin = ReachOrigin(hand, character, palm, trackingToModel, modelToWorld, out string from);
            StringBuilder said = trace ? new StringBuilder() : null;
            bool found = false;
            foreach (Candidate c in inReach)
            {
                bool clear = LineOfReach(origin, palm, c.Use, seat, said != null, out string verdict);
                said?.Append(said.Length == 0 ? string.Empty : "; ").Append($"'{c.Tag}' on {Describe(c.Use.Owner)} {verdict}");
                if (clear)
                {
                    best = c;
                    found = true;
                    break;
                }
            }
            inReach.Clear();
            if (said != null && traceLogs++ < MaxTraceLogs)
                Log.Info($"Grab use: {hand} hand's reach from the {from}, {Vector3D.Distance(origin, palm) * 100:0} cm to the palm: {said}");
            return found;
        }

        private static int traceLogs;

        /// <summary>Where the arm reaches from: the hand's shoulder as last solved or animated, or the eyes before the arms are known.</summary>
        private static Vector3D ReachOrigin(Hand hand, MyCharacter character, Vector3D palm, in MatrixD trackingToModel, in MatrixD modelToWorld, out string from)
        {
            if (HandIK.TryShoulder(hand, character, out Vector3D shoulder) && Vector3D.Distance(shoulder, palm) <= MaxShoulderToPalm)
            {
                from = "shoulder";
                return shoulder;
            }
            from = "eyes";
            if (HeadTracker.TryGet(out Matrix tracked))
                return Vector3D.Transform(Vector3D.Transform((Vector3D)tracked.Translation, trackingToModel), modelToWorld);
            return character.GetHeadMatrix(false).Translation;
        }

        private static readonly List<MyPhysics.HitInfo> reachHits = new List<MyPhysics.HitInfo>();
        private static readonly List<HkBodyCollision> startsIn = new List<HkBodyCollision>();
        private static readonly Vector3D[] reachPoint = new Vector3D[1];
        private static HkShape reachBall, startProbe;
        private static bool reachShapesMade;
        private static int reachErrors;

        /// <summary>
        /// Whether nothing solid lies on the line from <paramref name="origin"/> (the shoulder, or the eyes) to the palm other
        /// than the target's own block and the seat. A 1 cm ball is swept along it with
        /// <c>MyPhysics.CastShapeReturnContactBodyDatas</c> on the character collision layer, as <see cref="SurfaceStop"/>
        /// sweeps the drawn hands, and solid is what <see cref="SurfaceStop.Solid"/> says (a grid that is not a projection, or
        /// voxels). Also passed: a back face (the line leaving something) and whatever the line starts inside (a 2 mm probe at
        /// the origin: the seated body's blocks). Physics that cannot be asked from here (inside the simulation, another
        /// thread) or a failed cast count as clear, so this never takes away a grab it could not check.
        /// </summary>
        private static bool LineOfReach(Vector3D origin, Vector3D palm, IMyUseObject use, MyEntity seat, bool note, out string verdict)
        {
            verdict = "clear";
            if (reachErrors >= 3 || !MyPhysics.CheckThread() || MyPhysics.InsideSimulation)
            {
                verdict = "not checked (physics busy)";
                return true;
            }
            Vector3D way = palm - origin;
            double length = way.Length();
            if (length < 2 * ReachBallRadius)
                return true;
            try
            {
                if (!reachShapesMade)
                {
                    reachBall = new HkSphereShape(ReachBallRadius).Base;
                    startProbe = new HkSphereShape(StartProbeRadius).Base;
                    reachShapesMade = true;
                }
                MySlimBlock target = BlockOf(use?.Owner as MyEntity);
                MySlimBlock seatBlock = BlockOf(seat);

                startsIn.Clear();
                Vector3D start = origin;
                Quaternion none = Quaternion.Identity;
                MyPhysics.GetPenetrationsShape(startProbe, ref start, ref none, startsIn, MyPhysics.CollisionLayers.CharacterCollisionLayer);

                reachHits.Clear();
                MatrixD at = MatrixD.CreateTranslation(origin);
                MyPhysics.CastShapeReturnContactBodyDatas(palm, reachBall, ref at, (uint)MyPhysics.CollisionLayers.CharacterCollisionLayer, 0f, reachHits);

                float first = float.MaxValue;
                object blocker = null;
                StringBuilder passed = note ? new StringBuilder() : null;
                int notes = 0;
                foreach (MyPhysics.HitInfo hit in reachHits)
                {
                    HkHitInfo info = hit.HkHitInfo;
                    if (Vector3D.Dot(info.Normal, way) > 0.0)
                        continue; // a back face: the line leaving something
                    MyEntity owner = SurfaceStop.Solid(info, out MySlimBlock block, out object what);
                    if (owner == null)
                        continue;
                    string why = target != null && block == target ? "the target's own block"
                        : seatBlock != null && block == seatBlock ? "the seat"
                        : StartsInside(info, owner, block, origin) ? "starts inside it"
                        : null;
                    if (why != null)
                    {
                        if (passed != null && notes++ < 4)
                            passed.Append(passed.Length == 0 ? string.Empty : ", ")
                                  .Append($"{SurfaceStop.Describe(what)} at {info.HitFraction * length * 100:0} cm ({why})");
                        continue;
                    }
                    if (info.HitFraction < first)
                    {
                        first = info.HitFraction;
                        blocker = what;
                    }
                }
                reachHits.Clear();
                startsIn.Clear();
                string alsoPassed = passed != null && passed.Length > 0 ? $"; passed {passed}" : string.Empty;
                if (blocker == null)
                {
                    verdict = "clear" + alsoPassed;
                    return true;
                }
                verdict = $"blocked by {SurfaceStop.Describe(blocker)} {first * length * 100:0} cm along{alsoPassed}";
                return false;
            }
            catch (Exception e)
            {
                reachHits.Clear();
                startsIn.Clear();
                Log.Error(e, ++reachErrors < 3 ? "Grab use: the line of reach could not be checked" : "Grab use: the line of reach failed again; not checked until the game restarts");
                verdict = "not checked (failed)";
                return true;
            }
        }

        /// <summary>Whether the line began inside what it hit (the probe at its start was in the same body and block, or in the voxels).</summary>
        private static bool StartsInside(HkHitInfo info, MyEntity owner, MySlimBlock block, Vector3D origin)
        {
            if (owner is MyVoxelBase)
            {
                reachPoint[0] = origin;
                MyVoxelBase voxels = info.GetHitEntity() as MyVoxelBase ?? (MyVoxelBase)owner;
                return voxels.IsAnyOfPointInside(reachPoint);
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
        private static MySlimBlock BlockOf(MyEntity entity)
        {
            for (MyEntity e = entity; e != null; e = e.Parent)
            {
                if (e is MyCubeBlock block)
                    return block.SlimBlock;
            }
            return null;
        }

        /// <summary>The palm in the world: the grip pose's position, put in the world as this frame's camera will have the body.</summary>
        private static bool TryPalm(Hand hand, in MatrixD trackingToModel, in MatrixD modelToWorld, out Vector3D palm)
        {
            palm = Vector3D.Zero;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return false;
            palm = Vector3D.Transform(Vector3D.Transform((Vector3D)state.Grip.Translation, trackingToModel), modelToWorld);
            return true;
        }

        /// <summary>A world point in the frame of the use object's owner (a door, a seat), which moves with its ship.</summary>
        private static Vector3D ToOwnerFrame(IMyUseObject use, Vector3D world)
        {
            if (use?.Owner is MyEntity owner && owner.PositionComp != null)
                return Vector3D.Transform(world, owner.PositionComp.WorldMatrixNormalizedInv);
            return world;
        }

        /// <summary>Metres from the palm to the use object's detector box (a held use is checked against the box it took).</summary>
        private static double DistanceToUse(IMyUseObject use, Vector3D palm)
        {
            if (!(use?.Owner is MyEntity owner) || owner.PositionComp == null
                || !owner.Components.TryGet(out MyUseObjectsComponentBase found) || !(found is MyUseObjectsComponent component))
                return double.PositiveInfinity;
            MatrixD world = owner.PositionComp.WorldMatrixRef;
            double best = double.PositiveInfinity;
            // The game edits this table under its own lock when a block's detectors change.
            lock (component.DetectorInteractiveObjects)
            {
                foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in component.DetectorInteractiveObjects)
                {
                    if (!ReferenceEquals(pair.Value.UseObject, use))
                        continue;
                    best = Math.Min(best, FingerPress.DistanceToBox((MatrixD)pair.Value.Matrix * world, palm));
                }
            }
            return best;
        }
    }
}
