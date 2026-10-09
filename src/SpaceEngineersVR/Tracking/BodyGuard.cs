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
    /// <item>Before both, the head is locked to the body: its offset on the floor is held within an ellipse around the head
    /// bone (<see cref="BodyVolume.LockHead"/>), so that stepping back in the room does not take the eye through the
    /// backpack and out of the far side of the body, where the push-out no longer sees a body.</item>
    /// </list>
    /// The body does not walk under a head that has moved sideways: the game's walking is a speed (walk, run, sprint)
    /// with an acceleration ramp, not a position, so a step of 15 cm cannot be asked of it, and its only position move
    /// (MyDeltaNetCommand) is a teleport that is not synced. The head roams outside the body instead.
    /// </summary>
    internal static class BodyGuard
    {
        /// <summary>RenderDebug BodyClearance=0: the eye is not pushed out of the body.</summary>
        public static bool Off { get; set; }

        /// <summary>RenderDebug CrouchWithHead=0: the head's crouch does not crouch the character.</summary>
        public static bool CrouchOff { get; set; }

        /// <summary>RenderDebug HeadLock=0: on foot, the head is not held to the body (<see cref="BodyVolume.LockHead"/>).</summary>
        public static bool LockOff { get; set; }

        /// <summary>RenderDebug SeatClamp=0: seated, the head is not held round the seated character's head (<see cref="CockpitHead"/>, <see cref="BodyVolume.LockSeat"/>).</summary>
        public static bool SeatClampOff { get; set; }

        private const float Step = 1f / 60f; // the simulation's step, seconds

        private static CrouchDriver driver;
        private static MyCharacter known;
        private static float? standHeight; // the head bone's height above the feet, standing idle, metres
        private static ulong driveFrame = ulong.MaxValue;
        private static ulong pushLogFrame, lockLogFrame;

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
        public static Vector3 Offset(MyCharacter character, in Vector3D boneAt, Vector3 tracked)
        {
            if (character.IsDead)
                return tracked;
            MatrixD at = character.PositionComp.WorldMatrixRef;
            float headY = (float)Vector3D.Dot(boneAt - at.Translation, at.Up);
            NoteStanding(character, headY);

            float bodyDrop = standHeight.HasValue ? Math.Max(0f, standHeight.Value - headY) : 0f;
            // A crouch the head made is still made up while the character stands back up after the crouch is switched off
            // (seated play, the option, CrouchOff): the eye stays where the head is as the body rises under it.
            bool crouch = (CrouchEnabled || driver.Ours) && standHeight.HasValue;
            bool clear = !Off && VRSettings.BodyClearance;
            float reach = !LockOff && VRSettings.HeadLock ? VRSettings.HeadReach : 0f;
            Vector3 offset = BodyEye.Offset(tracked, bodyDrop, driver.Ours, crouch, clear, reach);
            ulong frame = MySandboxGame.Static.SimulationFrameCounter;

            if (reach > 0f)
                NoteLock(tracked, reach, frame);

            if (clear)
            {
                // What the push alone did, for the log: the offset with the crouch made up and held, but not pushed.
                Vector3 unpushed = BodyEye.Offset(tracked, bodyDrop, driver.Ours, crouch, false, reach);
                float pushed = Vector3.Distance(unpushed, offset);
                if (pushed > 0.02f && frame - pushLogFrame > 300)
                {
                    pushLogFrame = frame;
                    Log.Info($"Body clearance: eye pushed {pushed * 100f:F0} cm out of the body (head {tracked.X:F2}, {tracked.Y:F2}, {tracked.Z:F2} m from neutral, body crouch {bodyDrop:F2} m{(driver.Ours ? ", the head's" : "")})");
                }
            }
            LastEyeY = offset.Y - bodyDrop;
            LastFrame = frame;
            return offset;
        }

        /// <summary>For the drive's report: the eye's height from the standing character's eye in the last on-foot head (metres), and its frame.</summary>
        internal static float LastEyeY;
        internal static ulong LastFrame;

        /// <summary>The head's crouch crouches the character: on, not switched off, and standing play (seated, leaning down is not a crouch).</summary>
        private static bool CrouchEnabled => !CrouchOff && VRSettings.CrouchWithHead && !VRSettings.SeatedPlay;

        /// <summary>
        /// The log line for a head the lock is holding: how far and which way (the way the head went, past where it is held),
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
            }
            if (character.CurrentMovementState == MyCharacterMovementEnum.Standing)
                standHeight = standHeight.HasValue ? Math.Max(standHeight.Value, headY) : headY;
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
