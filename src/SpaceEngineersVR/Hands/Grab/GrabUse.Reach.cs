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
                if (c.Kind != Kind.None && c.Distance <= UseRadius)
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

        /// <summary>
        /// Whether nothing solid lies on the line from <paramref name="origin"/> (the shoulder, or the eyes) to the palm other
        /// than the target's own block and the seat (<see cref="Reach.Sweep"/>). A line that could not be checked (physics
        /// busy, or a failed cast) counts as clear, so this never takes away a grab it could not check.
        /// </summary>
        private static bool LineOfReach(Vector3D origin, Vector3D palm, IMyUseObject use, MyEntity seat, bool note, out string verdict)
        {
            StringBuilder passed = note ? new StringBuilder() : null;
            if (!Reach.Sweep(origin, palm, Reach.BlockOf(use?.Owner as MyEntity), Reach.BlockOf(seat), passed, out float first, out object blocker))
            {
                verdict = "not checked (physics busy or failed)";
                return true;
            }
            string alsoPassed = passed != null && passed.Length > 0 ? $"; passed {passed}" : string.Empty;
            if (blocker == null)
            {
                verdict = "clear" + alsoPassed;
                return true;
            }
            verdict = $"blocked by {SurfaceStop.Describe(blocker)} {first * Vector3D.Distance(origin, palm) * 100:0} cm along{alsoPassed}";
            return false;
        }

        /// <summary>
        /// The palm in the world: the grip pose's position, put in the world as this frame's camera will have the body, and
        /// kept within the arm's reach (<see cref="Reach.Limit"/>).
        /// </summary>
        private static bool TryPalm(Hand hand, MyCharacter character, in MatrixD trackingToModel, in MatrixD modelToWorld, out Vector3D palm)
        {
            palm = Vector3D.Zero;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked)
                return false;
            palm = Reach.Limit(character, Vector3D.Transform(Vector3D.Transform((Vector3D)state.Grip.Translation, trackingToModel), modelToWorld));
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
