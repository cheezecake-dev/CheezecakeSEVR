using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Puts the tracked head into a seat's head matrix (MyCockpit.GetHeadMatrix; cryo chambers and every other seat
    /// derive from it), the first-person camera while the local player sits in it. The seat's forward is the tracking
    /// space's forward.
    /// </summary>
    internal static class CockpitHead
    {
        private static readonly AccessTools.FieldRef<MyCockpit, MatrixD> CameraDummy = AccessTools.FieldRefAccess<MyCockpit, MatrixD>("m_cameraDummy");

        private static MyCockpit seat;
        private static ulong seatFrame;
        private static ulong holdLogFrame;
        private static ulong noteSeatFrame = ulong.MaxValue; // the frame the sit-down's "Seat clamp:" line is due (none: MaxValue)
        private const ulong NoteSeatAfter = 60; // a second of simulation: the pilot is in the seat and the sitting animation done

        /// <summary>
        /// The last seated head, for the drive's report (seat basis, x right, y up, z back): the tracked head from the seat's
        /// eye, where the eye was held, and the seat's eye from the pilot's head; <see cref="LastFrame"/> is its frame.
        /// </summary>
        internal static Vector3 LastOffset, LastHeld, LastFromHead;
        internal static ulong LastFrame;

        /// <summary>The seat's eye further than this from the pilot's head (metres) is not a pilot sitting in it: held round the seat's eye instead.</summary>
        private const float PilotFar = 0.5f;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyCockpit), nameof(MyCockpit.GetHeadMatrix)),
                prefix: new HarmonyMethod(typeof(CockpitHead), nameof(HandOver)),
                postfix: new HarmonyMethod(typeof(CockpitHead), nameof(TrackHead)));
            GameHead.PatchView(harmony, AccessTools.Method(typeof(MyCockpit), nameof(MyCockpit.GetViewMatrix)));
        }

        /// <summary>The head matrix being built is the first-person head of the seat the local player sits in.</summary>
        private static bool Ours(MyCockpit seat)
        {
            MyCharacter pilot = seat.Pilot;
            return pilot != null && pilot == MySession.Static?.LocalCharacter && seat.PositionComp != null
                   && (seat.IsInFirstPersonView || seat.ForceFirstPersonCamera);
        }

        /// <summary>
        /// A lost head leaves the seat's own head angles, which the matrix is about to be built from, where they were:
        /// they take over at the angles the head looked at (<see cref="GameHead.BeforeGameHead"/>), so the view stays.
        /// </summary>
        private static void HandOver(MyCockpit __instance)
        {
            if (!Ours(__instance))
                return;
            float x = __instance.HeadLocalXAngle, y = __instance.HeadLocalYAngle;
            if (GameHead.BeforeGameHead(ref x, ref y))
            {
                __instance.HeadLocalXAngle = x;
                __instance.HeadLocalYAngle = y;
            }
        }

        /// <summary>
        /// True when this is the first head matrix in this seat since the player sat down (or looked away from it for a
        /// frame or more), when the seat is to face where the player looks. A seat whose head is lost is still sat in:
        /// frames without the head count as being in the seat, so the head coming back does not turn the view to the seat.
        /// </summary>
        internal static bool Seated(MyCockpit inSeat, ulong frame, bool tracked)
        {
            bool sitDown = inSeat != seat || frame > seatFrame + 1;
            if (tracked)
            {
                seat = inSeat;
                seatFrame = frame;
            }
            else if (inSeat == seat)
                seatFrame = frame;
            return sitDown;
        }

        private static void TrackHead(MyCockpit __instance, bool includeY, bool includeX, ref MatrixD __result)
        {
            if (!Ours(__instance))
                return;
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            if (!GameHead.Take())
            {
                Seated(__instance, frame, false);
                GameHead.GameHeadShown(__instance.HeadLocalYAngle);
                return;
            }
            // Sitting down: a recentre onto the seat's eye, facing where the player looks (GameHead.SitDown).
            if (Seated(__instance, frame, true))
            {
                GameHead.SitDown(__instance.BlockDefinition?.Id.SubtypeName ?? __instance.GetType().Name);
                noteSeatFrame = frame + NoteSeatAfter;
            }

            // The game's own seat head matrix is pitch * yaw (from the mouse) * the camera dummy * the seat, at the
            // seat's head position.
            MatrixD basis = CameraDummy(__instance) * __instance.PositionComp.WorldMatrixRef;
            MatrixD result = (MatrixD)GameHead.Rotation(includeY, includeX) * basis;
            result.Translation = __result.Translation;
            if (includeX && includeY)
            {
                // Seated, the head is held round the seated character's head: lean, stand or walk off in the room and the eye
                // would be back through the character's own head and body, out through the cockpit's glass or the floor.
                Vector3 offset = GameHead.Head.Translation;
                Vector3 fromHead = FromHead(__instance, basis, __result.Translation);
                LastOffset = offset;
                LastFromHead = fromHead;
                if (!BodyGuard.SeatClampOff && VRSettings.SeatReach > 0f)
                    offset = Hold(__instance, fromHead, offset, frame);
                LastHeld = offset;
                LastFrame = frame;
                result.Translation += Vector3D.TransformNormal((Vector3D)offset, basis);
            }
            __result = result;
            GameHead.UsedForView();
        }

        /// <summary>
        /// The head's offset from the seat's eye, held round the seated character's head (<see cref="BodyVolume.LockSeat"/>):
        /// forward and to the side as far as the reach, back only as far as the head, so the eye cannot go back into the
        /// character's own head and body. The seat's eye is the pilot's head, or a head dummy in the cockpit's model
        /// (MyCockpit.GetHeadMatrix), so the hold is centred on the pilot's head itself; how far apart the two are is logged
        /// once a sit-down.
        /// </summary>
        /// <param name="fromHead">The seat's eye from the pilot's head (<see cref="FromHead"/>).</param>
        /// <param name="offset">The tracked head from the character's eye, in the seat's basis (x right, y up, z back).</param>
        private static Vector3 Hold(MyCockpit inSeat, Vector3 fromHead, Vector3 offset, ulong frame)
        {
            float reach = VRSettings.SeatReach;
            Vector3 held = BodyVolume.LockSeat(fromHead + offset, reach) - fromHead;

            // Logged a second after the sit-down: on the sit-down's own frame the pilot is not in the seat yet.
            if (noteSeatFrame != ulong.MaxValue && frame >= noteSeatFrame)
            {
                noteSeatFrame = ulong.MaxValue;
                string from = fromHead == Vector3.Zero
                    ? $"the pilot's head is not within {PilotFar * 100f:F0} cm of the seat's eye; the head is held round the seat's eye"
                    : $"the seat's eye is ({fromHead.X:F3}, {fromHead.Y:F3}, {fromHead.Z:F3}) m from the pilot's head (x right, y up, z back); the head is held round the pilot's head";
                Log.Info($"Seat clamp: in {inSeat.BlockDefinition?.Id.SubtypeName ?? inSeat.GetType().Name} {from}: " +
                         $"{reach * 100f:F0} cm forward, {reach * BodyVolume.LockSideShare * 100f:F0} to the side, " +
                         $"{BodyVolume.LockBack * 100f:F0} back, {reach * 100f:F0} up and down");
            }
            NoteHold(offset, held, frame);
            return held;
        }

        /// <summary>
        /// The seat's eye from the pilot's head, in the seat's basis (x right, y up, z back); zero when there is no pilot or
        /// the two are further apart than <see cref="PilotFar"/> (then the hold is round the seat's eye).
        /// </summary>
        /// <param name="eye">The seat's eye, world (the game's own head matrix's position).</param>
        private static Vector3 FromHead(MyCockpit inSeat, in MatrixD basis, in Vector3D eye)
        {
            MyCharacter pilot = inSeat.Pilot;
            if (pilot == null)
                return Vector3.Zero;
            Vector3D d = eye - pilot.GetHeadMatrix(false, true, forceHeadAnim: true, forceHeadBone: true, preferLocalOverSync: true).Translation;
            var local = new Vector3((float)Vector3D.Dot(d, basis.Right), (float)Vector3D.Dot(d, basis.Up), (float)Vector3D.Dot(d, basis.Backward));
            return !float.IsNaN(local.X + local.Y + local.Z) && local.Length() < PilotFar ? local : Vector3.Zero;
        }

        /// <summary>The log line for a head the seat holds by 2 cm or more: how far and which way, at most one in 2 seconds of simulation.</summary>
        private static void NoteHold(Vector3 offset, Vector3 held, ulong frame)
        {
            Vector3 by = offset - held;
            float length = by.Length();
            if (length <= 0.02f || frame - holdLogFrame <= 120)
                return;
            holdLogFrame = frame;
            string way = Join(Join(Way(by.Z, "back", "forward"), Way(by.X, "right", "left")), Way(by.Y, "up", "down"));
            Log.Info($"Seat clamp: head held {length * 100f:F0} cm {way} (head {offset.X:F2} right, {offset.Y:F2} up, {offset.Z:F2} back " +
                     $"from the seat's eye, held at {held.X:F2}, {held.Y:F2}, {held.Z:F2})");
        }

        private static string Way(float by, string plus, string minus) => Math.Abs(by) > 0.01f ? (by > 0f ? plus : minus) : null;

        private static string Join(string a, string b) => a != null && b != null ? a + " and " + b : a ?? b;
    }
}
