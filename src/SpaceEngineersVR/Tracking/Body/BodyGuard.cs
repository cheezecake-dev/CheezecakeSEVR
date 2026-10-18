using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Keeps the first-person eye out of the character's own body on foot. The tracked head is added to the game's head
    /// bone as an offset and the body does not follow it, so lean, crouch or sit in the room and the eye is in the suit.
    /// Two parts, both on the head offset <see cref="CharacterHead"/> adds:
    /// <list type="bullet">
    /// <item>A real crouch (the head 30 cm or more below where it stands, held for a moment) makes the character crouch,
    /// through the game's own Crouch toggle, and a stand-up stands it. The bone then goes down, and the head offset adds
    /// that drop back (<see cref="CrouchDriver.Made"/>) so the eye is where the player's head is, one to one, going into
    /// the crouch and out of it, and a little above the crouched bone, as it is when standing.</item>
    /// <item>Whatever offset is left is pushed out of the suit, the backpack and the shoulders (<see cref="BodyVolume"/>);
    /// an offset that is clear, which is nearly always, goes in as it is.</item>
    /// <item>Before both, the head is locked to the body: its offset on the floor is kept within an ellipse around the head
    /// bone, so that stepping back in the room does not take the eye through the backpack and out of the far side of the
    /// body, where the push-out no longer sees a body. The edge pushes (<see cref="BodyVolume.PushEdge"/>): one to one
    /// inside it, stopped at it, and the on-foot centre moves with a head that goes past it (<see cref="GameHead.PushOnFoot"/>),
    /// so the view is held off the head only while the head is going outwards, and follows it one to one as soon as it
    /// comes back. RenderDebug HeadPush=0 selects the soft hold instead (<see cref="BodyVolume.LockHead"/>), which does not move
    /// the centre: the further out the head is, the more of its movement is lost, and past about twice the reach all of it.</item>
    /// </list>
    /// The body does not walk under a head that has moved sideways: the game's walking is a speed (walk, run, sprint)
    /// with an acceleration ramp, not a position, so a step of 15 cm cannot be asked of it, and its only position move
    /// (MyDeltaNetCommand) is a teleport that is not synced. The head roams outside the body instead.
    /// </summary>
    internal static partial class BodyGuard
    {
        /// <summary>RenderDebug BodyClearance=0: the eye is not pushed out of the body.</summary>
        public static bool Off { get; set; }

        /// <summary>RenderDebug CrouchWithHead=0: the head's crouch does not crouch the character.</summary>
        public static bool CrouchOff { get; set; }

        /// <summary>
        /// RenderDebug HeadPush=0: on foot, the lock is the soft hold (<see cref="BodyVolume.LockHead"/>) and the on-foot centre
        /// stays where the last recentre put it instead of following the pushed edge (<see cref="BodyVolume.PushEdge"/>).
        /// </summary>
        public static bool PushOff { get; set; }

        /// <summary>RenderDebug EyeHold=0: on foot and upright the eye follows the animated head bone (<see cref="EyeHold"/> off).</summary>
        public static bool HoldOff { get; set; }

        /// <summary>RenderDebug EyeTrace=1: one log line for every simulation frame on foot, with the movement state, the body's drop and the eye's height.</summary>
        public static bool TraceOn { get; set; }

        private const float Step = 1f / 60f; // the simulation's step, seconds

        private static CrouchDriver driver;
        private static MyCharacter known;
        private static float? standHeight; // the head bone's height above the feet, standing idle, metres
        private static ulong driveFrame = ulong.MaxValue;
        private static ulong pushLogFrame, lockLogFrame;
        private static float lastBodyDrop; // the character's own crouch in the last head, metres
        private static float lastEyeExtra; // what the eye hold added to the last head's crouch made up, metres
        private static EyeHold hold;
        private static ulong holdFrame = ulong.MaxValue;
        private static ulong traceFrame = ulong.MaxValue;
        private static float traceLow, traceHigh, traceLast, traceDrop, traceExtra;
        private static MyCharacterMovementEnum traceState;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyCharacter), "MoveAndRotateInternal",
                    new[] { typeof(Vector3), typeof(Vector2), typeof(float), typeof(Vector3) }),
                prefix: new HarmonyMethod(typeof(BodyGuard), nameof(Drive)));
        }

        /// <summary>
        /// The head's offset from the head bone for the local character on foot, in the body's basis (x right, y up, z back):
        /// what the tracker says, with the character's own crouch taken off and out of the body. Game thread.
        /// </summary>
        /// <param name="boneAt">The head bone, in the world, before the offset.</param>
        /// <param name="ahead">How far ahead of the eye the body is drawn (<see cref="BodyShift"/>), metres.</param>
        public static Vector3 Offset(MyCharacter character, in Vector3D boneAt, Vector3 tracked, float ahead = 0f)
        {
            if (character.IsDead)
                return tracked;
            MatrixD at = character.PositionComp.WorldMatrixRef;
            float headY = (float)Vector3D.Dot(boneAt - at.Translation, at.Up);
            NoteStanding(character, headY);

            float bodyDrop = standHeight.HasValue ? Math.Max(0f, standHeight.Value - headY) : 0f;
            lastBodyDrop = bodyDrop;
            // A crouch the head made is still made up while the character stands back up after the crouch is switched off
            // (seated play, the option, CrouchOff): the eye stays where the head is as the body rises under it.
            bool crouch = (CrouchEnabled || driver.Ours) && standHeight.HasValue;
            bool clear = !Off && VRSettings.BodyClearance;
            float reach = HeadReach();
            bool push = reach > 0f && !PushOff;
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            float extra = HoldExtra(character, bodyDrop, crouch, tracked.Y, frame);
            lastEyeExtra = extra;
            Vector3 offset = BodyEye.Offset(tracked, bodyDrop, driver.Ours, crouch, clear, reach, ahead, extra, push);

            // The pushed edge: the head as placed from here on is kept inside it by moving the on-foot centre (GameHead logs
            // how far); this frame's head is already stopped at the edge above. The soft hold logs what it holds.
            if (push)
                GameHead.PushOnFoot(reach);
            else if (reach > 0f)
                NoteLock(tracked, reach, frame);

            if (clear)
            {
                // What the push alone did, for the log: the offset with the crouch made up and held, but not pushed.
                Vector3 unpushed = BodyEye.Offset(tracked, bodyDrop, driver.Ours, crouch, false, reach, ahead, extra, push);
                float pushed = Vector3.Distance(unpushed, offset);
                if (pushed > 0.02f && frame - pushLogFrame > 300)
                {
                    pushLogFrame = frame;
                    Log.Info($"Body clearance: eye pushed {pushed * 100f:F0} cm out of the body (head {tracked.X:F2}, {tracked.Y:F2}, {tracked.Z:F2} m from neutral, body crouch {bodyDrop:F2} m{(driver.Ours ? ", the head's" : "")})");
                }
            }
            LastEyeY = offset.Y - bodyDrop;
            LastFrame = frame;
            if (TraceOn)
                Trace(character, frame, headY + offset.Y, bodyDrop, extra);
            return offset;
        }

        /// <summary>How far the head on foot may go from the character's head before it is held, metres; 0: not held.</summary>
        private static float HeadReach()
        {
            bool unlocked = false;
            TestAidsHeadUnlocked(ref unlocked);
            return !unlocked && VRSettings.HeadLock ? VRSettings.HeadReach : 0f;
        }

        /// <summary>A test aid (renderdebug HeadLock=0) lets the head on foot go any distance from the character's head.</summary>
        static partial void TestAidsHeadUnlocked(ref bool unlocked);

        /// <summary>
        /// How far ahead of the head bone the body can be drawn for this tracked head, up to <paramref name="wanted"/>,
        /// without the eye ending up in it (<see cref="BodyShift"/>): the eye as <see cref="Offset"/> places it before the
        /// push-out (held, the crouch made up), with the last head's crouch, and the room in front of it
        /// (<see cref="BodyVolume.Room"/>). All of it when the clearance is off or the character is not the one seen.
        /// </summary>
        internal static float Room(MyCharacter character, Vector3 tracked, float wanted)
        {
            if (character != known || character.IsDead || Off || !VRSettings.BodyClearance)
                return wanted;
            bool crouch = (CrouchEnabled || driver.Ours) && standHeight.HasValue;
            float reach = HeadReach();
            Vector3 eye = BodyEye.Offset(tracked, lastBodyDrop, driver.Ours, crouch, false, reach, 0f, lastEyeExtra, reach > 0f && !PushOff);
            return BodyVolume.Room(eye, wanted);
        }

        /// <summary>
        /// On foot and upright the head bone's dip is made up whole (<see cref="EyeHold"/>), the eye follows the headset; in
        /// any other state (a crouch, a seat, a ladder, a jump, a fall, flying) the crouch is made up as <see cref="BodyEye"/>
        /// does and the share of the dip eases off over a few frames. What to add to that, metres.
        /// </summary>
        private static float HoldExtra(MyCharacter character, float bodyDrop, bool crouch, float trackedY, ulong frame)
        {
            if (HoldOff)
            {
                hold = default;
                holdFrame = ulong.MaxValue;
                return 0f;
            }
            if (frame != holdFrame)
            {
                // A frame counter that went back (another world) or a long gap is "not seen for a while".
                long frames = frame >= holdFrame ? (long)(frame - holdFrame) : EyeHold.GapFrames + 1;
                holdFrame = frame;
                hold.Step(!driver.Ours && IsUpright(character.CurrentMovementState), bodyDrop, frames);
            }
            float made = crouch ? CrouchDriver.Made(bodyDrop, Math.Max(0f, -trackedY), driver.Ours) : 0f;
            return hold.Extra(bodyDrop, made);
        }

        /// <summary>
        /// The movement states in which the character is up on its feet and the head bone's dip is the animation's:
        /// standing, turning on the spot, walking and running in every direction, sprinting. Not the crouches (the key's
        /// lowers the view as the game does), a seat, a ladder, flying, a jump, a fall or a dead character.
        /// </summary>
        private static bool IsUpright(MyCharacterMovementEnum state)
        {
            switch (state)
            {
                case MyCharacterMovementEnum.Standing:
                case MyCharacterMovementEnum.RotatingLeft:
                case MyCharacterMovementEnum.RotatingRight:
                case MyCharacterMovementEnum.Walking:
                case MyCharacterMovementEnum.BackWalking:
                case MyCharacterMovementEnum.WalkStrafingLeft:
                case MyCharacterMovementEnum.WalkStrafingRight:
                case MyCharacterMovementEnum.WalkingLeftFront:
                case MyCharacterMovementEnum.WalkingLeftBack:
                case MyCharacterMovementEnum.WalkingRightFront:
                case MyCharacterMovementEnum.WalkingRightBack:
                case MyCharacterMovementEnum.Running:
                case MyCharacterMovementEnum.Backrunning:
                case MyCharacterMovementEnum.RunStrafingLeft:
                case MyCharacterMovementEnum.RunStrafingRight:
                case MyCharacterMovementEnum.RunningLeftFront:
                case MyCharacterMovementEnum.RunningLeftBack:
                case MyCharacterMovementEnum.RunningRightFront:
                case MyCharacterMovementEnum.RunningRightBack:
                case MyCharacterMovementEnum.Sprinting:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// RenderDebug EyeTrace=1: a line for every simulation frame that had a head on foot, written when the next frame
        /// starts: the movement state, the body's drop (how far the head bone is below standing), what the eye hold made up,
        /// and the eye's height above the feet at the last head of the frame and its lowest and highest in it, millimetres.
        /// </summary>
        private static void Trace(MyCharacter character, ulong frame, float eyeY, float bodyDrop, float extra)
        {
            if (frame != traceFrame)
            {
                if (traceFrame != ulong.MaxValue)
                    Log.Info($"Eye trace {traceFrame} {traceState} drop {traceDrop * 1000f:F1} extra {traceExtra * 1000f:F1} eye {traceLast * 1000f:F1} low {traceLow * 1000f:F1} high {traceHigh * 1000f:F1} weight {hold.Weight:F2} floor {hold.Floor * 1000f:F1} held {hold.Held * 1000f:F1}");
                traceFrame = frame;
                traceLow = float.MaxValue;
                traceHigh = float.MinValue;
            }
            traceState = character.CurrentMovementState;
            traceDrop = bodyDrop;
            traceExtra = extra;
            traceLast = eyeY;
            traceLow = Math.Min(traceLow, eyeY);
            traceHigh = Math.Max(traceHigh, eyeY);
        }

        /// <summary>For the drive's report: the eye's height from the standing character's eye in the last on-foot head (metres), and its frame.</summary>
        internal static float LastEyeY;
        internal static ulong LastFrame;

        /// <summary>The head's crouch crouches the character: on, not switched off, and standing play (seated, leaning down is not a crouch).</summary>
        private static bool CrouchEnabled => !CrouchOff && VRSettings.CrouchWithHead && !VRSettings.SeatedPlay;

        /// <summary>
        /// The log line for a head the soft hold is holding (RenderDebug HeadPush=0): how far and which way (the way the head went, past where it is held),
        /// at most one in 2 seconds of simulation, and only when it is held by 2 cm or more.
        /// </summary>
        private static void NoteLock(Vector3 tracked, float reach, ulong frame)
        {
            Vector3 locked = BodyVolume.LockHead(tracked, reach);
            float x = tracked.X - locked.X, z = tracked.Z - locked.Z;
            float held = (float)Math.Sqrt(x * x + z * z);
            if (held <= 0.02f || frame - lockLogFrame <= 120)
                return;
            lockLogFrame = frame;
            string back = Math.Abs(z) > 0.01f ? (z > 0f ? "back" : "forward") : null;
            string side = Math.Abs(x) > 0.01f ? (x > 0f ? "right" : "left") : null;
            string way = back != null && side != null ? back + " and " + side : back ?? side;
            Log.Info($"Head lock: head held {held * 100f:F0} cm {way} (head {tracked.X:F2} right, {tracked.Z:F2} back from neutral, held at {locked.X:F2}, {locked.Z:F2}; reach {reach * 100f:F0} cm forward)");
        }

        /// <summary>The head bone's height when the character stands idle is where "standing" is; the highest seen, so a rise after a crouch does not lower it.</summary>
        private static void NoteStanding(MyCharacter character, float headY)
        {
            if (character != known)
            {
                known = character;
                standHeight = null;
                driver = default;
                hold = default;
                holdFrame = ulong.MaxValue;
            }
            if (character.CurrentMovementState == MyCharacterMovementEnum.Standing)
                standHeight = standHeight.HasValue ? Math.Max(standHeight.Value, headY) : headY;
        }

        /// <summary>A head crouch the character may not make now has been noted (until one may be made again).</summary>
        private static bool refusedNoted;

        /// <summary>Once, a head low enough to crouch that may not crouch the character, and why.</summary>
        private static void NoteRefused(MyCharacter character, bool allowed, bool enabled)
        {
            if (allowed || !enabled)
            {
                refusedNoted &= !allowed;
                return;
            }
            if (refusedNoted || !GameHead.Take() || -GameHead.Head.Translation.Y < CrouchDriver.EnterDrop)
                return;
            refusedNoted = true;
            string why = !(character.IsInFirstPersonView || character.ForceFirstPersonCamera) ? "the view is not first person"
                : character.IsSitting ? "seated" : character.IsDead ? "dead" : character.JetpackRunning ? "the jetpack is on"
                : character.IsOnLadder ? "on a ladder" : character.IsFalling ? "falling" : "the head is not tracked";
            Log.Info($"Head crouch: head {-GameHead.Head.Translation.Y:F2} m below standing, but the character does not crouch: {why}");
        }

        /// <summary>
        /// Once per simulation frame, before the character moves: the head's crouch, as the key's. Switched off while the
        /// head's crouch holds the character down, it is stood up as a head back at standing height would stand it.
        /// </summary>
        private static void Drive(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter)
                return;
            bool enabled = CrouchEnabled;
            if (!enabled && !driver.Ours)
            {
                driver = default;
                return;
            }
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;
            if (frame == driveFrame)
                return;
            driveFrame = frame;

            bool allowed = (__instance.IsInFirstPersonView || __instance.ForceFirstPersonCamera)
                           && !__instance.IsSitting && !__instance.IsDead && !__instance.JetpackRunning
                           && !__instance.IsOnLadder && !__instance.IsFalling && GameHead.Take();
            float drop = allowed && enabled ? Math.Max(0f, -GameHead.Head.Translation.Y) : 0f;
            bool crouching = (__instance.MovementFlags & MyCharacterMovementFlags.Crouch) != 0;
            NoteRefused(__instance, allowed, enabled);
            CrouchAction action = driver.Update(Step, drop, crouching, allowed);
            if (action == CrouchAction.None)
                return;
            __instance.Crouch();
            bool now = (__instance.MovementFlags & MyCharacterMovementFlags.Crouch) != 0;
            Log.Info(enabled
                ? $"Head crouch: head {drop:F2} m below standing, asked to {(action == CrouchAction.Crouch ? "crouch" : "stand")}, character now {(now ? "crouching" : "standing")}"
                : $"Head crouch switched off ({(VRSettings.SeatedPlay ? "seated play" : "the option or CrouchOff")}) while it held the character down: asked to stand, character now {(now ? "crouching" : "standing")}");
        }
    }
}
