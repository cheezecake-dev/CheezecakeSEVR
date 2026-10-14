// Datapad, in a seat: let go there it stays, fixed to the seat, and each kind of seat remembers where.
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Datapad
    {
        /// <summary>A remembered spot further than this from the pilot (metres) is not one.</summary>
        private const float SpotReach = 2f;

        // Game thread: the seat the pad stands in or comes out to (null: none; on foot it stands in the tracking space), and
        // where, in its pilot's space.
        private static MyCockpit standSeat;
        private static Matrix standInPilot;

        /// <summary>The pad stands in a seat, or is coming out to its spot there.</summary>
        private static bool InSeat => standSeat != null && (phase == Phase.Standing || phase == Phase.Coming && toStand);

        /// <summary>The local character sits in the seat it controls: that seat, and the character.</summary>
        private static bool TrySeat(out MyCockpit seat, out MyCharacter pilot)
        {
            pilot = MySession.Static?.LocalCharacter;
            seat = MySession.Static?.ControlledEntity?.Entity as MyCockpit;
            return seat != null && !seat.Closed && pilot != null && ReferenceEquals(seat.Pilot, pilot);
        }

        /// <summary>The kind of seat its spot is remembered by.</summary>
        private static string Kind(MyCockpit seat) => seat.BlockDefinition?.Id.SubtypeName ?? string.Empty;

        /// <summary>
        /// Let go at <paramref name="pose"/> (the tracking space): in a seat it is fixed to the seat from now on, and the seat's
        /// kind keeps the spot. False on foot, or when the pilot's space cannot be had.
        /// </summary>
        private static bool AnchorInSeat(in Matrix pose)
        {
            standSeat = null;
            if (!TrySeat(out MyCockpit seat, out MyCharacter pilot) || !HandWorld.TryGetTrackingToBody(pilot, out MatrixD toPilot, out _))
                return false;
            standSeat = seat;
            standInPilot = (Matrix)((MatrixD)pose * toPilot);
            Remember(Kind(seat), standInPilot);
            return true;
        }

        /// <summary>
        /// The spot the pad was last let go in this kind of seat, in the tracking space now, while the player sits in one: the
        /// pad is fixed there. False on foot, or with no spot kept for the seat.
        /// </summary>
        private static bool TrySeatSpot(out Matrix pose)
        {
            pose = default;
            standSeat = null;
            if (!TrySeat(out MyCockpit seat, out MyCharacter pilot) || !Spots().TryGetValue(Kind(seat), out float[] numbers)
                || !PoseNumbers.TryPose(numbers, SpotReach, out Matrix inPilot) || !HandWorld.TryGetTrackingToBody(pilot, out MatrixD toPilot, out _))
                return false;
            standSeat = seat;
            standInPilot = inPilot;
            pose = (Matrix)((MatrixD)inPilot * MatrixD.Invert(toPilot));
            return true;
        }

        /// <summary>
        /// Game thread, each frame the pad stands in a seat: where it is in the tracking space now (when the pilot's space can
        /// be had; else where it was). False when the player is no longer in that seat.
        /// </summary>
        private static bool FollowSeat()
        {
            if (!TrySeat(out MyCockpit seat, out MyCharacter pilot) || !ReferenceEquals(seat, standSeat))
                return false;
            if (HandWorld.TryGetTrackingToBody(pilot, out MatrixD toPilot, out _))
            {
                var pose = (Matrix)((MatrixD)standInPilot * MatrixD.Invert(toPilot));
                lock (gate)
                    shown.From = pose;
            }
            return true;
        }

        /// <summary>Put away from the hand in a seat: its kind forgets the spot, and the pad comes out into the hand there again.</summary>
        private static void ForgetSeatSpot()
        {
            if (TrySeat(out MyCockpit seat, out _))
                Remember(Kind(seat), null);
        }

        /// <summary>The spot of a kind of seat kept (<paramref name="inPilot"/>) or taken out (null), in the settings file.</summary>
        private static void Remember(string kind, Matrix? inPilot)
        {
            Dictionary<string, float[]> spots = Spots();
            if (inPilot.HasValue)
                spots[kind] = PoseNumbers.Of(inPilot.Value);
            else if (!spots.Remove(kind))
                return;
            VRSettings.DatapadSeatSpots = NumberTable.Format(spots, "0.####");
            VRSettings.Save();
            Log.Info(inPilot.HasValue
                ? $"Datapad: its spot in the {kind} kept; it comes out there in that kind of seat"
                : $"Datapad: put away from the hand in the {kind}; it comes out into the hand there again");
        }

        private static Dictionary<string, float[]> Spots() => NumberTable.Parse(VRSettings.DatapadSeatSpots, PoseNumbers.Count);
    }
}
