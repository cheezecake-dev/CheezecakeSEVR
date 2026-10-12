using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.Entity.UseObject;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    // Grab diagnostics: the rate-limited log (Say), what the hand is near (LogNear, Survey) and descriptions of game objects.
    internal static partial class GrabUse
    {
        // ---- The log ----

        private static void Say(string message)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - logWindow > Stopwatch.Frequency)
            {
                logWindow = now;
                logsInWindow = 0;
            }
            if (logs >= MaxLogs || logsInWindow >= MaxLogsPerSecond)
                return;
            logs++;
            logsInWindow++;
            Log.Info(message);
        }

        /// <summary>What the hand is close to, taken or not, with where its middle is in tracking space (what a fake hand is placed with).</summary>
        private static void LogNear(Hand hand, Vector3D palm, long now, in MatrixD trackingToModel, in MatrixD modelToWorld)
        {
            int i = (int)hand;
            if (nearLogs >= MaxNearLogs || candidates.Count == 0 || now - nearLoggedAt[i] < NearLogEvery)
                return;
            Candidate nearest = candidates[0];
            foreach (Candidate c in candidates)
            {
                if (c.Distance < nearest.Distance || (c.Distance == nearest.Distance && c.Center < nearest.Center))
                    nearest = c;
            }
            nearLoggedAt[i] = now;
            nearLogs++;
            Vector3D centerInTracking = Vector3D.Transform(nearest.BoxCenter, MatrixD.Invert(trackingToModel * modelToWorld));
            Log.Info($"Grab use: {hand} palm {nearest.Distance * 100:0.0} cm from '{nearest.Tag}' {nearest.Use.GetType().Name} on {Describe(nearest.Use.Owner)} " +
                     $"({(nearest.Kind == Kind.None ? $"not taken: {nearest.Why ?? "?"}" : nearest.Kind.ToString())}; box middle at tracking {centerInTracking.X:0.00},{centerInTracking.Y:0.00},{centerInTracking.Z:0.00})");
        }

        /// <summary>
        /// For the drive's "uses" command (testing): every use object within <paramref name="radius"/> metres of the local
        /// character's head, nearest first, with how it would be grabbed from where the player is and where its box's middle
        /// is in the tracking space (what a fake hand is placed with: x right, y up, z back), and the head's place there.
        /// </summary>
        internal static string Survey(double radius, int max)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            Place place = Where(character, out MyEntity seat);
            if (character?.PositionComp == null)
                return "no character";
            if (!HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out var why))
                return $"no hands' frame ({why})";
            MatrixD trackingToWorld = trackingToModel * MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);
            MatrixD worldToTracking = MatrixD.Invert(trackingToWorld);
            Vector3D head = HeadTracker.TryGet(out Matrix tracked) ? (Vector3D)tracked.Translation : Vector3D.Zero;
            Vector3D center = Vector3D.Transform(head, trackingToWorld);
            var found = new List<(double distance, string text)>();
            var all = new List<MyEntity>();
            var sphere = new BoundingSphereD(center, radius);
            MyGamePruningStructure.GetAllEntitiesInSphere(ref sphere, all);
            foreach (MyEntity entity in all)
            {
                if (entity?.PositionComp == null || entity.Components == null || entity.Closed
                    || !entity.Components.TryGet(out MyUseObjectsComponentBase component) || !(component is MyUseObjectsComponent uses))
                    continue;
                MatrixD world = entity.PositionComp.WorldMatrixRef;
                // The game edits this table under its own lock when a block's detectors change.
                lock (uses.DetectorInteractiveObjects)
                {
                    foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> pair in uses.DetectorInteractiveObjects)
                    {
                        IMyUseObject use = pair.Value.UseObject;
                        if (use == null || ReferenceEquals(use.Owner, character))
                            continue;
                        MatrixD box = (MatrixD)pair.Value.Matrix * world;
                        double distance = FingerPress.DistanceToBox(box, center);
                        if (distance > radius)
                            continue;
                        bool own = seat != null && ReferenceEquals(use.Owner, seat);
                        string reason = null;
                        Kind kind = own ? Kind.None : Classify(use, place == Place.Seated, out reason);
                        Vector3D at = Vector3D.Transform(box.Translation, worldToTracking);
                        found.Add((distance, $"{distance:0.00} m '{use.Dummy?.Name ?? pair.Value.DetectorName}' {use.GetType().Name} on {Describe(use.Owner)} " +
                                             $"[{(own ? "the seat's own" : kind == Kind.None ? "not: " + reason : kind.ToString())}] at {at.X:0.00},{at.Y:0.00},{at.Z:0.00}"));
                    }
                }
            }
            found.Sort((a, b) => a.distance.CompareTo(b.distance));
            string list = string.Join(" | ", found.Take(max).Select(f => f.text));
            return $"{place}{(seat != null ? " in " + Describe(seat) : string.Empty)}; head at tracking {head.X:0.00},{head.Y:0.00},{head.Z:0.00}; " +
                   $"{found.Count} use objects within {radius:0.#} m: {list}";
        }

        internal static string Describe(IMyEntity owner)
        {
            if (owner == null)
                return "nothing";
            return owner is MyCubeBlock block && block.BlockDefinition != null
                ? $"{owner.GetType().Name} {block.BlockDefinition.Id.SubtypeName}"
                : owner.GetType().Name;
        }
    }
}
