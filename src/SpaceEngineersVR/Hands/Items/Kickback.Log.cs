using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    // Kickback measurement: the per-use log lines (distance kicked, aim moved, view moved).
    internal static partial class Kickback
    {
        // ---- The log: one line as a use starts, one as it has settled ----

        private static void Measure(MyCharacter character, MyEntity tool, MyHandItemDefinition item, bool twoHands, bool wasInUse,
            double up, double side, double twist, double back, bool limited)
        {
            long now = Stopwatch.GetTimestamp();
            if (inUse && !wasInUse)
            {
                string held = !twoHands ? "one-handed" : hands > 0.0 ? $"two-handed ({hands:0.##} of the kick)" : "two-handed (nothing added)";
                Log.Info($"Kickback: the {Name(item)} in use, {held}" +
                         (IsGun(currentKind) ? string.Empty : biting ? ", biting" : ", in the air") + $", kickback {Level()}");
            }
            if (!episode)
                return;

            if (twoHands) framesTwo++; else framesOne++;
            if (GameHead.Tracked) trackedFrames++;
            if (inUse) { useFrames++; if (biting) biteFrames++; }
            if (limited) limitedFrames++;
            peakUp = Math.Max(peakUp, Math.Abs(up));
            peakSide = Math.Max(peakSide, Math.Abs(side));
            peakTwist = Math.Max(peakTwist, Math.Abs(twist));
            peakBack = Math.Max(peakBack, Math.Abs(back));

            // The view: how far it turned and moved since the last frame.
            MatrixD view = MySector.MainCamera?.WorldMatrix ?? MatrixD.Identity;
            double frameTurn = 0.0, frameMove = 0.0;
            if (lastViewValid)
            {
                frameTurn = Math.Max(Angle(view.Forward, lastView.Forward), Angle(view.Up, lastView.Up));
                frameMove = Vector3D.Distance(view.Translation, lastView.Translation);
                viewTurn = Math.Max(viewTurn, frameTurn);
                viewMove = Math.Max(viewMove, frameMove);
            }
            lastView = view;
            lastViewValid = true;

            // Where the aim points on what is in front of it, kicked and as the hand alone (and the other hand) would have it.
            if (HandWorld.TryGetNow(character, VRSettings.DominantHand, out MatrixD grip, out MatrixD aim, out _))
            {
                TwoHand.Apply(character, ref grip, ref aim);
                Hit(character, tool, aim, ownHits, ref ownMisses);
                Turn(ref grip, ref aim, MathHelper.ToRadians(up), MathHelper.ToRadians(side), MathHelper.ToRadians(twist), back, PivotBehindGrip);
                Hit(character, tool, aim, hits, ref misses);
            }

            if (Trace)
                Log.Info($"Kickback trace: {Name(item)} use={(inUse ? 1 : 0)} bite={(biting ? 1 : 0)} hands={(twoHands ? 2 : 1)} share={hands:0.00} drive={drive:0.00} heat={heat:0.00} " +
                         $"up={up:0.00} side={side:0.00} twist={twist:0.00} back={back * 100:0.0}cm{(limited ? " limited" : string.Empty)} " +
                         $"target {targetUp:0.0}/{targetSide:0.0}/{targetTwist:0.0} view {frameTurn:0.000}deg {frameMove * 1000:0.0}mm");

            if (inUse)
            {
                settleSince = 0;
                return;
            }
            if (settleSince == 0)
                settleSince = now;
            bool settled = Math.Abs(up) < 0.05 && Math.Abs(side) < 0.05 && Math.Abs(back) < 0.001;
            if (settled || now - settleSince > 2 * Stopwatch.Frequency)
                End(settled ? "settled" : "still kicked 2 s after the trigger");
        }

        private static void Begin(MyHandItemDefinition item, long now)
        {
            episode = true;
            episodeItem = Name(item);
            episodeGun = IsGun(currentKind);
            episodeStart = now;
            settleSince = 0;
            framesOne = framesTwo = shots = jerks = spinUps = misses = ownMisses = useFrames = biteFrames = limitedFrames = trackedFrames = 0;
            peakUp = peakSide = peakTwist = peakBack = viewTurn = viewMove = 0.0;
            hits.Clear();
            ownHits.Clear();
            lastViewValid = false;
        }

        private static void End(string why)
        {
            if (!episode)
                return;
            episode = false;
            double seconds = (Stopwatch.GetTimestamp() - episodeStart) / (double)Stopwatch.Frequency;
            string held = framesTwo == 0 ? "one-handed" : framesOne == 0 ? "two-handed" : $"one-handed {framesOne} frames, two-handed {framesTwo}";
            Log.Info($"Kickback: the {episodeItem} {why} after {seconds:0.00} s, {held}, kickback {Level()}: peak {peakUp:0.0} deg up, {peakSide:0.0} deg to the side, " +
                     $"{peakTwist:0.0} deg of twist, {peakBack * 100:0.0} cm back, squeezed at the limits {limitedFrames} frames; {shots} shots, {jerks} jerks, {spinUps} spin-ups, " +
                     $"biting {biteFrames} of {useFrames} frames in use; the aim point moved over {Span(hits) * 100:0.0} cm on what it pointed at " +
                     $"({hits.Count} hits, {misses} misses; the hand's own aim {Span(ownHits) * 100:0.0} cm, {ownHits.Count} hits); " +
                     (episodeGun ? $"the game's own recoil kept off the character {recoilKept} times, its head angles put back by up to {headUndone:0.00} deg; " : string.Empty) +
                     $"the head tracked {trackedFrames} of {framesOne + framesTwo} frames; the view turned at most {viewTurn:0.000} deg and moved {viewMove * 1000:0.0} mm in a frame");
            recoilKept = 0;
            headUndone = 0.0;
        }

        private static void Hit(MyCharacter character, MyEntity tool, in MatrixD aim, List<Vector3D> list, ref int missed)
        {
            if (!HandTools.Ray(aim, out Vector3D origin, out Vector3D forward))
                return;
            MyPhysics.HitInfo? hit = MyPhysics.CastRay(origin + forward * MeasureStart, origin + forward * MeasureReach);
            IMyEntity entity = hit.HasValue ? ((IHitInfo)hit.Value).HitEntity : null;
            if (!hit.HasValue || ReferenceEquals(entity, character) || ReferenceEquals(entity, tool))
            {
                missed++;
                return;
            }
            if (list.Count < MaxHits)
                list.Add(hit.Value.Position);
        }

        /// <summary>The longest distance between two of the points (metres).</summary>
        private static double Span(List<Vector3D> points)
        {
            double longest = 0.0;
            for (int i = 0; i < points.Count; i++)
                for (int j = i + 1; j < points.Count; j++)
                    longest = Math.Max(longest, Vector3D.DistanceSquared(points[i], points[j]));
            return Math.Sqrt(longest);
        }
    }
}
