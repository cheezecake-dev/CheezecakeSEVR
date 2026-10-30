using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// The tracked head as the game thread sees it: one snapshot per simulation frame, so every head matrix built in
    /// that frame agrees, and the record of which camera it went into (for <see cref="CameraHeadLink"/>).
    /// </summary>
    /// <remarks>
    /// The head is given relative to the body: the tracking space turned by the anchor, the angle between the
    /// tracking space's forward and the body's. When the body turns to follow the head (<see cref="BodyFollow"/>), the
    /// anchor turns with it, so the view stays where it is. Any other turn of the body (the mouse, the stick, a ship
    /// turning under the player) turns the view with it.
    ///
    /// When the head is lost the game's own head takes over, which has no anchor (and no roll): the view would snap by the
    /// head's yaw relative to the body. So the last head is handed to the game (<see cref="BeforeGameHead"/>: its head
    /// angles become where the head looked, and the view stays), and when the head is back the anchor is set so the view
    /// picks up from wherever the game's head looks by then.
    /// </remarks>
    internal static class GameHead
    {
        private static readonly object gate = new object();
        private static ulong snapshotFrame = ulong.MaxValue;
        private static bool tracked;
        private static Matrix raw, head;
        private static float? originAboveFloor;
        private static float heightReference;
        private static long sampledAt;
        private static float yaw, pitch, roll;
        private static float anchor;

        // The recentre (HeadCentre): where the head was at the last one, in the tracking space, and one asked for and not
        // yet taken (Stopwatch ticks of the ask: it takes the first head sampled after it; -1 none).
        private static Vector3 centre;
        private static long recentreAfter = -1;
        private static string recentreBy;
        private static bool? playSeated; // the play position the head's height last followed (null: no head placed yet)

        // Sat in a seat (SitDown, OnFoot): the head is placed from where it was at the sit-down, or at a recentre since, on all
        // three axes, whatever the play position; the on-foot centre above is kept for getting up.
        private static bool inSeat;
        private static Vector3 seatCentre;

        // On foot, the head lock's pushed edge (PushOnFoot): the reach it was last used with and the snapshot frame it was
        // used in, and what the pushes have moved the on-foot centre by since the last log line (body basis, metres).
        private static float pushReach;
        private static ulong pushFrame = ulong.MaxValue;
        private const ulong PushFrames = 2; // the centre is pushed while an on-foot head used the edge within this many frames
        private static Vector3 pushedWay;
        private static float pushedSinceLog;
        private static ulong pushLogFrame = ulong.MaxValue;
        private const ulong PushLogFrames = 120; // at most one push line in this many frames (2 s)

        // The head was lost and the game's own head has taken over; see Lose and Resume.
        private static bool resume, foldPending, resumed;
        private static float foldYaw, foldPitch, gameYaw;
        private static ulong foldFrame;
        private const ulong FoldFrames = 2; // a fold nobody took up within this many frames (no first-person head built) is dropped

        // The local character the anchor was last set for (NewBody): a new one, a world loaded or a respawn, faces the head.
        private static object anchoredFor;

        [ThreadStatic] private static int viewDepth;
        [ThreadStatic] private static bool cameraTracked;
        private static Matrix? cameraHead;

        /// <summary>
        /// Head pose in the tracking space, this frame, turned by the anchor, its position from the last recentre and the
        /// play position (<see cref="HeadCentre"/>): where the game puts the character's eyes, relative to the body.
        /// </summary>
        public static Matrix Head => head;

        /// <summary>Head pose in the tracking space as tracked, without the anchor, this frame (as the eyes are).</summary>
        public static Matrix TrackingPose => raw;

        /// <summary>When the render thread sampled this frame's head (Stopwatch ticks), and with it the controllers.</summary>
        public static long SampledAt => sampledAt;

        /// <summary>Head pitch this frame, radians, + up.</summary>
        public static float Pitch => pitch;

        /// <summary>Head yaw relative to the body this frame, radians, + left.</summary>
        public static float Yaw => yaw;

        /// <summary>
        /// The head's look from the body this frame, limited as a neck is (<see cref="Input.VRControls.MaxLookYaw"/>,
        /// <see cref="Input.VRControls.MaxLookPitch"/>): what the zero-g thrust, the character's head and the helmet turn by.
        /// Bent back past straight up (or forward past straight down), <see cref="Yaw"/> comes out turned half round and
        /// <see cref="Pitch"/> short of the bend, the roll half round with them; that is undone first, so the look stops at
        /// straight up (down) facing the body's way instead of flipping round behind it. A head rolled over while looking
        /// about level (the pitch under 45 deg) is taken as rolled, and keeps its yaw.
        /// </summary>
        public static void LimitedLook(out float limitedYaw, out float limitedPitch)
        {
            Look(out float y, out float p);
            limitedYaw = MathHelper.Clamp(y, -Input.VRControls.MaxLookYaw, Input.VRControls.MaxLookYaw);
            limitedPitch = MathHelper.Clamp(p, -Input.VRControls.MaxLookPitch, Input.VRControls.MaxLookPitch);
        }

        /// <summary>
        /// The head's look from the body as <see cref="LimitedLook"/> takes it before the limits: bent back past straight up
        /// (or forward past straight down), turned back round. With <see cref="LimitedLook"/>, how far the head has turned
        /// on inside the helmet past the neck's limits (<see cref="Rendering.HelmetInterior"/>).
        /// </summary>
        public static void Look(out float lookYaw, out float lookPitch)
        {
            lookYaw = yaw;
            lookPitch = pitch;
            if (head.Up.Y < 0f && Math.Abs(lookPitch) > MathHelper.PiOver4)
            {
                lookPitch = Math.Sign(lookPitch) * (MathHelper.Pi - Math.Abs(lookPitch));
                lookYaw = MathHelper.WrapAngle(lookYaw + MathHelper.Pi);
            }
        }

        /// <summary>How high the tracking space's origin is above the floor in this frame's snapshot, metres; null when the headset gives no floor.</summary>
        public static float? OriginAboveFloor => originAboveFloor;

        /// <summary>
        /// The height in the tracking space at which the head is at the character's eye this frame (<see cref="HeadCentre.HeightReference"/>;
        /// in a seat, the sit-down's), for <see cref="Rendering.ComfortGrid"/> to find the floor by the eye height when the headset gives none.
        /// </summary>
        public static float HeightReference => heightReference;

        /// <summary>A head was tracked in the last snapshot taken (this frame's once a head matrix has been built in it), for the logs.</summary>
        public static bool Tracked
        {
            get
            {
                lock (gate)
                    return tracked;
            }
        }

        /// <summary>Takes this frame's snapshot if it has not been taken yet. False when nothing is tracked.</summary>
        public static bool Take()
        {
            HeightCalibration.Update(); // the calibration's countdown runs on the game's frames, with or without a screen open
            lock (gate)
            {
                ulong frame = MySandboxGame.Static.SimulationFrameCounter;
                if (frame == snapshotFrame)
                    return tracked;
                snapshotFrame = frame;

                bool was = tracked;
                tracked = HeadTracker.TryGet(out raw, out originAboveFloor, out sampledAt);
                if (tracked)
                {
                    if (!was && resume)
                        Resume();
                    if (recentreAfter >= 0 && sampledAt > recentreAfter)
                        TakeRecentre();
                    else
                    {
                        NewBody();
                        Anchor();
                    }
                }
                else if (was)
                    Lose();
                return tracked;
            }
        }

        /// <summary>
        /// A new local character on foot (a world loaded, a respawn): its body faces where the head looks, as the game shows a
        /// flat player the character's own facing. The neck's turn from the last character (<see cref="BodyFollow"/> leaves
        /// the head up to its limit off the body) is not carried into another body or another world. Seated, the sit-down
        /// sets the anchor (<see cref="SitDown"/>). Caller holds the gate, with a head tracked.
        /// </summary>
        private static void NewBody()
        {
            var character = Sandbox.Game.World.MySession.Static?.LocalCharacter;
            if (character == null || ReferenceEquals(character, anchoredFor))
                return;
            anchoredFor = character;
            if (character.IsSitting || character.IsDead)
                return;
            float was = MathHelper.WrapAngle(HeadCentre.Yaw(raw) - anchor);
            anchor = HeadCentre.Yaw(raw);
            Log.Info($"A new body on foot ({character.DisplayName}): it faces where the head looks; the head was {MathHelper.ToDegrees(was):F0} deg off the last body's forward");
        }

        /// <summary>The body turned by this much (radians, + left) to follow the head; the view stays put.</summary>
        public static void BodyTurned(float angle)
        {
            lock (gate)
            {
                anchor = MathHelper.WrapAngle(anchor + angle);
                if (tracked)
                    Anchor();
            }
        }

        /// <summary>
        /// Sitting down (<see cref="CockpitHead"/>): a recentre on all axes, at once, as Recentre view's: where the head is and
        /// faces now becomes the seat's eye and forward, whatever the play position and however the player stands or sits in
        /// the room, so the eye starts at the pilot's eye and not pressed against the top or the bottom of the seat's hold.
        /// The on-foot centre and height are kept for getting up (<see cref="OnFoot"/>).
        /// </summary>
        /// <param name="seat">The seat, for the log.</param>
        public static void SitDown(string seat)
        {
            lock (gate)
            {
                if (!tracked)
                    return;
                Vector3 before = head.Translation;
                float turned = yaw;
                anchor = HeadCentre.Yaw(raw);
                seatCentre = raw.Translation;
                inSeat = true;
                Anchor();
                Log.Info($"Sat down in {seat}: recentred on all axes onto the seat's eye; " +
                         $"turned {MathHelper.ToDegrees(turned):F0} deg; eye was ({before.X:F2}, {before.Y:F2}, {before.Z:F2}), " +
                         $"now ({head.Translation.X:F2}, {head.Translation.Y:F2}, {head.Translation.Z:F2}) m from the seat's eye (x right, y up, z back)");
            }
        }

        /// <summary>
        /// The head is used on foot (<see cref="CharacterHead"/>): after a seat, the head is placed from the on-foot centre and
        /// height again (the floor's with a calibration in standing play, else the last on-foot recentre's), in one step, as
        /// the view moves from the seat to the character; the yaw stays.
        /// </summary>
        public static void OnFoot()
        {
            lock (gate)
            {
                if (!inSeat)
                    return;
                inSeat = false;
                Vector3 before = head.Translation;
                if (tracked)
                    Anchor();
                bool fromFloor = !VRSettings.SeatedPlay && originAboveFloor.HasValue && VRSettings.EyeHeight.HasValue;
                Log.Info($"Got up: the eye is placed from the on-foot centre again, {(fromFloor ? "its height from the floor" : "its height from the last on-foot recentre")}; " +
                         $"eye was ({before.X:F2}, {before.Y:F2}, {before.Z:F2}) m from the seat's eye, now ({head.Translation.X:F2}, {head.Translation.Y:F2}, {head.Translation.Z:F2}) from the character's eye (the yaw unchanged)");
            }
        }

        /// <summary>
        /// On foot, the head lock is the pushed edge with this reach (<see cref="BodyGuard"/>, <see cref="BodyVolume.PushEdge"/>).
        /// From the next head placed, a head past the edge moves the on-foot centre with it, by as far as it is past, so the
        /// head is placed on the edge, where that frame's view was already stopped: the view does not move, and as soon as
        /// the head comes back the view follows it one to one. Without this the centre stays where the last recentre put it
        /// (before any, the tracking space's origin), and a head far from it (a seated player leaning back after getting up
        /// from a seat) is held off the view by all of that, with none of its movement reaching the view. Game thread, each
        /// on-foot first-person head that used the edge.
        /// </summary>
        public static void PushOnFoot(float reach)
        {
            lock (gate)
            {
                pushReach = reach;
                pushFrame = snapshotFrame;
            }
        }

        /// <summary>The head is placed from the sit-down's centre (<see cref="SitDown"/>), for the drive's report.</summary>
        internal static bool SeatCentred
        {
            get
            {
                lock (gate)
                    return inSeat;
            }
        }

        /// <summary>A recentre has been asked for and the head it is to be taken from has not come yet.</summary>
        public static bool RecentrePending
        {
            get
            {
                lock (gate)
                    return recentreAfter >= 0;
            }
        }

        /// <summary>
        /// Recentre on all axes: where the head is and faces at the first head sampled after this call becomes the
        /// character's eye and forward (<see cref="HeadCentre"/>): x, y and z, and the yaw. Not the pitch or the roll, which
        /// would tilt the horizon. Seated play takes the height from it too; standing play with a calibrated height keeps
        /// the height from the floor; in a seat it is onto the seat's eye, and getting up goes back to the on-foot one
        /// (<see cref="SitDown"/>). Any thread: the Options button, the drive, or the runtime's own recentre, which has
        /// moved the tracking space (the head sampled after the call is in the new space, so it is taken from that).
        /// </summary>
        /// <param name="by">Who asked, for the log.</param>
        public static void Recentre(string by)
        {
            lock (gate)
            {
                recentreAfter = Stopwatch.GetTimestamp();
                recentreBy = by;
            }
        }

        /// <summary>How many recentres have been taken: ColbyJack1134's cockpit lets go of its sticks when this changes.</summary>
        internal static int Recentres => System.Threading.Volatile.Read(ref recentres);

        private static int recentres;

        /// <summary>Takes the recentre asked for from this frame's head, then places the head. Caller holds the gate.</summary>
        private static void TakeRecentre()
        {
            recentres++;
            Vector3 before = head.Translation;
            float turned = yaw;
            recentreAfter = -1;
            anchor = HeadCentre.Yaw(raw);
            // In a seat it is the seat's centre that moves; the on-foot one is kept for getting up.
            if (inSeat)
                seatCentre = raw.Translation;
            else
                centre = raw.Translation;
            Anchor();
            bool fromFloor = !inSeat && !VRSettings.SeatedPlay && originAboveFloor.HasValue && VRSettings.EyeHeight.HasValue;
            string height = inSeat ? "in a seat, onto the seat's eye"
                : fromFloor ? $"height from the floor (calibrated eye height {VRSettings.EyeHeight.Value:F2} m)"
                : "height from the recentre";
            Log.Info($"Recentred ({recentreBy}): play position {(VRSettings.SeatedPlay ? "seated" : "standing")}, {height}; " +
                     $"turned {MathHelper.ToDegrees(turned):F0} deg; eye was ({before.X:F2}, {before.Y:F2}, {before.Z:F2}), " +
                     $"now ({head.Translation.X:F2}, {head.Translation.Y:F2}, {head.Translation.Z:F2}) m from the {(inSeat ? "seat's" : "character's")} eye (x right, y up, z back)");
        }

        /// <summary>
        /// The head was lost this frame. Its last yaw and pitch relative to the body are kept, to be handed to the game's
        /// head (<see cref="BeforeGameHead"/>); the game's head, which is what the view is now, is taken to look there.
        /// </summary>
        private static void Lose()
        {
            resume = true;
            resumed = false;
            foldPending = true;
            foldYaw = yaw;
            foldPitch = pitch;
            foldFrame = snapshotFrame;
            gameYaw = yaw;
        }

        /// <summary>
        /// The head is back. The anchor restarts so that its yaw relative to the body is where the game's head looked when
        /// the view was last shown, whatever the body did and wherever the head now points; the view carries on from there.
        /// </summary>
        private static void Resume()
        {
            Decompose(raw, out float rawYaw, out _, out _);
            anchor = MathHelper.WrapAngle(rawYaw - gameYaw);
            resume = false;
            foldPending = false;
            resumed = true;
        }

        /// <summary>
        /// Before the game builds a head matrix that this head would replace (the local player's, first person). Takes the
        /// snapshot, then adjusts the game's own head angles (<c>HeadLocalXAngle</c>, <c>HeadLocalYAngle</c>, degrees):
        /// when the head has just been lost they are set to where it looked, so the game's head matrix, the view from now
        /// on, looks there too; when it is back they are cleared, as nothing is driving them. True when they changed and
        /// are to be written back.
        /// </summary>
        public static bool BeforeGameHead(ref float headX, ref float headY)
        {
            bool head = Take();
            lock (gate)
            {
                if (head)
                {
                    if (!resumed)
                        return false;
                    resumed = false;
                    headX = headY = 0f;
                    return true;
                }
                if (!foldPending)
                    return false;
                foldPending = false;
                if (snapshotFrame - foldFrame > FoldFrames)
                    return false;
                headX = MathHelper.ToDegrees(foldPitch);
                headY = MathHelper.ToDegrees(foldYaw);
                return true;
            }
        }

        /// <summary>
        /// After the game built a head matrix with no head tracked: its head angle (<c>HeadLocalYAngle</c>, degrees) is
        /// where the view looks, relative to the body, for <see cref="Resume"/> to pick up.
        /// </summary>
        public static void GameHeadShown(float headY)
        {
            lock (gate)
            {
                if (!tracked)
                    gameYaw = MathHelper.WrapAngle(MathHelper.ToRadians(headY));
            }
        }

        private static void Anchor()
        {
            Vector3 was = head.Translation;
            head = raw * Matrix.CreateRotationY(-anchor);
            Decompose(head, out yaw, out pitch, out roll);

            // The play position changed (Options, RenderDebug SeatedPlay): its height is taken at once, in one step, and only
            // the height: x, z and the yaw stay where the last recentre put them. To seated, the head's height here becomes
            // the character's eye; to standing, the height is the floor's (calibrated) or the last recentre's again.
            bool seated = VRSettings.SeatedPlay;
            bool switched = playSeated.HasValue && playSeated.Value != seated;
            playSeated = seated;
            if (switched && seated)
                centre.Y = raw.Translation.Y;

            // The one place the head's position is set: from the last recentre's centre, the calibration and the play
            // position (HeadCentre), with the height offset on top. The camera head (cameraHead, below) stays the tracking
            // pose, so this is in the camera the game builds and is not repeated by the renderer's eye placement.
            // In a seat, from the sit-down's centre on all three axes, whatever the play position.
            float reference = inSeat ? seatCentre.Y : HeadCentre.HeightReference(seated, centre.Y, originAboveFloor, VRSettings.EyeHeight);
            heightReference = reference;
            head.Translation = HeadCentre.Place(raw.Translation, inSeat ? seatCentre : centre, anchor, reference) + new Vector3(0f, VRSettings.HeightOffset, 0f);

            // On foot with the lock's pushed edge in use: a head past the edge moves the on-foot centre with it (PushOnFoot).
            if (!inSeat && pushReach > 0f && snapshotFrame >= pushFrame && snapshotFrame - pushFrame <= PushFrames)
                Push(reference);

            if (switched && inSeat)
                Log.Info($"Play position now {(seated ? "seated" : "standing")}: in a seat the eye stays at the sit-down's; on foot it follows the new play position");
            else if (switched)
            {
                bool fromFloor = !seated && originAboveFloor.HasValue && VRSettings.EyeHeight.HasValue;
                string height = seated ? "the eye's height is the head's height here"
                    : fromFloor ? $"the eye's height is from the floor (calibrated eye height {VRSettings.EyeHeight.Value:F2} m)"
                    : "the eye's height is the last recentre's";
                Log.Info($"Play position now {(seated ? "seated" : "standing")}: {height}; the eye was {was.Y:F2} m from the character's eye height, " +
                         $"now {head.Translation.Y:F2} (x, z and the yaw unchanged)");
            }
        }

        /// <summary>
        /// Moves the on-foot centre by as far as the placed head is past the lock's edge (<see cref="BodyVolume.PushEdge"/>), on
        /// the floor's plane, and places the head again from it: it is then on the edge. Logs what it moved, at most once in
        /// 2 s. Caller holds the gate.
        /// </summary>
        private static void Push(float reference)
        {
            Vector3 at = head.Translation;
            Vector3 edge = BodyVolume.PushEdge(at, pushReach);
            var past = new Vector3(at.X - edge.X, 0f, at.Z - edge.Z);
            float by = past.Length();
            if (by < 0.0001f)
                return;
            // Place turns the offset from the centre by -anchor into the body's basis; the move goes back by +anchor.
            Vector3 move = Vector3.Transform(past, Matrix.CreateRotationY(anchor));
            centre.X += move.X;
            centre.Z += move.Z;
            head.Translation = HeadCentre.Place(raw.Translation, centre, anchor, reference) + new Vector3(0f, VRSettings.HeightOffset, 0f);

            pushedWay += past;
            pushedSinceLog += by;
            bool first = pushLogFrame == ulong.MaxValue || snapshotFrame < pushLogFrame;
            if (pushedSinceLog < 0.02f || (!first && snapshotFrame - pushLogFrame < PushLogFrames))
                return;
            string back = Math.Abs(pushedWay.Z) > 0.01f ? (pushedWay.Z > 0f ? "back" : "forward") : null;
            string side = Math.Abs(pushedWay.X) > 0.01f ? (pushedWay.X > 0f ? "right" : "left") : null;
            string way = back != null && side != null ? back + " and " + side : back ?? side ?? "to and fro";
            string over = first ? "" : $", in {(snapshotFrame - pushLogFrame) / 60f:F0} s";
            Log.Info($"Head lock: the on-foot centre moved {pushedSinceLog * 100f:F0} cm with the head past the edge ({way}{over}), " +
                     $"so the view stays on the head; the head is now {head.Translation.X:F2} right, {head.Translation.Z:F2} back of the character's eye " +
                     $"(reach {pushReach * 100f:F0} cm forward, {BodyVolume.LockBack * 100f:F0} back)");
            pushLogFrame = snapshotFrame;
            pushedSinceLog = 0f;
            pushedWay = Vector3.Zero;
        }

        /// <summary>
        /// The head's rotation in the game's own order, pitch * yaw (row vectors: pitch applied first), with roll before
        /// both. includeX/includeY drop the pitch and roll or the yaw, as the game's head matrices do.
        /// </summary>
        public static Matrix Rotation(bool includeY, bool includeX)
        {
            return (includeX ? Matrix.CreateRotationZ(roll) * Matrix.CreateRotationX(pitch) : Matrix.Identity)
                   * (includeY ? Matrix.CreateRotationY(yaw) : Matrix.Identity);
        }

        /// <summary>The head matrix being built went into the camera, if a camera is being built.</summary>
        public static void UsedForView()
        {
            if (viewDepth > 0)
                cameraTracked = true;
        }

        /// <summary>Marks a GetViewMatrix, so a tracked head matrix built inside it is known to be in the camera.</summary>
        public static void PatchView(Harmony harmony, MethodBase getViewMatrix)
        {
            harmony.Patch(getViewMatrix,
                prefix: new HarmonyMethod(typeof(GameHead), nameof(EnterView)),
                postfix: new HarmonyMethod(typeof(GameHead), nameof(LeaveView)));
        }

        /// <summary>The head pose the last camera was built from (null if it was not tracked); read once, as it is sent.</summary>
        public static Matrix? TakeCameraHead()
        {
            Matrix? taken = cameraHead;
            cameraHead = null;
            return taken;
        }

        private static void EnterView()
        {
            if (viewDepth++ == 0)
                cameraTracked = false;
        }

        private static void LeaveView()
        {
            if (--viewDepth == 0)
                // The tracking-space pose: the renderer's eye poses are in the same space, and the anchor cancels out
                // of eye * inverse(head in camera).
                cameraHead = cameraTracked ? raw : (Matrix?)null;
        }

        /// <summary>Head rotation = roll * pitch * yaw (row vectors: roll applied first).</summary>
        private static void Decompose(in Matrix head, out float yaw, out float pitch, out float roll)
        {
            Vector3 forward = head.Forward;
            yaw = (float)Math.Atan2(-forward.X, -forward.Z);
            pitch = (float)Math.Asin(MathHelper.Clamp(forward.Y, -1f, 1f));
            Matrix rotation = head;
            rotation.Translation = Vector3.Zero;
            Matrix rollOnly = rotation * Matrix.Transpose(Matrix.CreateRotationX(pitch) * Matrix.CreateRotationY(yaw));
            roll = (float)Math.Atan2(rollOnly.M12, rollOnly.M11);
        }
    }
}
