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

        // The head was lost and the game's own head has taken over; see Lose and Resume.
        private static bool resume, foldPending, resumed;
        private static float foldYaw, foldPitch, gameYaw;
        private static ulong foldFrame;
        private const ulong FoldFrames = 2; // a fold nobody took up within this many frames (no first-person head built) is dropped

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
                        Anchor();
                }
                else if (was)
                    Lose();
                return tracked;
            }
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
        /// The on-foot centre and height are kept for getting up (<see cref="OnFoot"/>). RenderDebug RecentreAll=0: it only
        /// turns, as it did before, and the eye stays where the on-foot centre puts it.
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
                if (!RecentreTurnOnly)
                {
                    seatCentre = raw.Translation;
                    inSeat = true;
                }
                Anchor();
                Log.Info($"Sat down in {seat}: {(inSeat ? "recentred on all axes onto the seat's eye" : "turned only (RecentreAll=0)")}; " +
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

        /// <summary>The head is placed from the sit-down's centre (<see cref="SitDown"/>), for the drive's report.</summary>
        internal static bool SeatCentred
        {
            get
            {
                lock (gate)
                    return inSeat;
            }
        }

        /// <summary>RenderDebug RecentreAll=0: a recentre only turns (the body's forward is where the head faces), as before; the eye stays where it was.</summary>
        public static bool RecentreTurnOnly { get; set; }

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

        /// <summary>Takes the recentre asked for from this frame's head, then places the head. Caller holds the gate.</summary>
        private static void TakeRecentre()
        {
            Vector3 before = head.Translation;
            float turned = yaw;
            recentreAfter = -1;
            anchor = HeadCentre.Yaw(raw);
            // In a seat it is the seat's centre that moves; the on-foot one is kept for getting up.
            if (!RecentreTurnOnly)
            {
                if (inSeat)
                    seatCentre = raw.Translation;
                else
                    centre = raw.Translation;
            }
            Anchor();
            bool fromFloor = !inSeat && !VRSettings.SeatedPlay && originAboveFloor.HasValue && VRSettings.EyeHeight.HasValue;
            string height = RecentreTurnOnly ? "turn only (RecentreAll=0)"
                : inSeat ? "in a seat, onto the seat's eye"
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
            head.Translation = HeadCentre.Place(raw.Translation, inSeat ? seatCentre : centre, anchor, reference) + new Vector3(0f, VRSettings.HeightOffset, 0f);

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
