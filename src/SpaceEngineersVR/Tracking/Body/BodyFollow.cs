using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// On foot, the body turns to follow the head: when the head turns past the neck limit, and fully while walking,
    /// so walking goes where the player looks. The turn goes through the game's own rotation input, so it animates
    /// and syncs like any other turn, and <see cref="GameHead"/> is told how far the body actually turned so the view
    /// does not move with it. Not on the jetpack, in gravity or not: with nothing underfoot to turn it by, the body turns by the
    /// sticks only, and the thrust goes where the head looks as far as it looks round. Nor floating free in zero g with the jetpack off,
    /// standing on nothing: there is no friction to turn the body by, so it keeps its heading while the head looks round
    /// (<see cref="Input.VRControls.FloatsFree"/>). On the magnetic boots or the ground and up a ladder, it follows.
    /// </summary>
    internal static class BodyFollow
    {
        /// <summary>The default of <see cref="NeckLimit"/>, 50 deg.</summary>
        internal static readonly float DefaultNeckLimit = MathHelper.ToRadians(50f);

        /// <summary>On foot and standing, the head turns this far from the body before the body turns after it, radians; RenderDebug NeckLimit=n sets it, degrees, while the game runs.</summary>
        internal static float NeckLimit { get; set; } = DefaultNeckLimit;
        private static readonly float MaxTurnPerFrame = MathHelper.ToRadians(9f); // 540 deg/s at 60 Hz

        /// <summary>The turns are reported at most once a second (<see cref="Note"/>).</summary>
        private static readonly long NoteEvery = Stopwatch.Frequency;

        private static bool turning;
        private static bool zeroGLogged;
        private static bool floatLogged;
        private static float share;
        private static float followed;
        private static Vector3D forwardBefore;

        // The turns not reported yet.
        private static long lastNote;
        private static int heldTurns;
        private static float heldRadians;
        private static bool heldWalking;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyCharacter), "MoveAndRotateInternal",
                    new[] { typeof(Vector3), typeof(Vector2), typeof(float), typeof(Vector3) }),
                prefix: new HarmonyMethod(typeof(BodyFollow), nameof(Request)),
                postfix: new HarmonyMethod(typeof(BodyFollow), nameof(Measure)));
        }

        /// <summary>Adds the follow turn to this frame's rotation input.</summary>
        private static void Request(MyCharacter __instance, Vector3 moveIndicator, ref Vector2 rotationIndicator)
        {
            turning = false;
            if (heldTurns > 0)
                Report();
            if (__instance != MySession.Static?.LocalCharacter || __instance.IsSitting || __instance.IsDead)
                return;
            if (!__instance.IsInFirstPersonView && !__instance.ForceFirstPersonCamera)
                return;
            if (!GameHead.Take())
                return;

            // With a gun in the hand in a multiplayer game the body faces the gun, as every other game fires its shots along the body's facing (SyncedAim).
            bool gun = Hands.SyncedAim.TryGunAim(__instance, out _, out float gunYaw);
            float relative = gun ? gunYaw : GameHead.Yaw;
            float target = moveIndicator != Vector3.Zero || gun ? 0f : MathHelper.Clamp(relative, -NeckLimit, NeckLimit);
            float turn = MathHelper.Clamp(relative - target, -MaxTurnPerFrame, MaxTurnPerFrame);
            if (__instance.JetpackRunning)
            {
                // On the jetpack, in gravity or not, there is nothing underfoot to turn the body by: it turns by the sticks only,
                // and the thrust goes where the head looks, as far as it looks round (VRControls.AddMovement, HeadThrust).
                if (!zeroGLogged && Math.Abs(turn) >= 1e-4f)
                {
                    zeroGLogged = true;
                    Log.Info($"Body follow: not on the jetpack, the head {MathHelper.ToDegrees(relative):F0} deg from the body; the body turns by the sticks only");
                }
                turn = 0f;
            }
            else if (Input.VRControls.FloatsFree(__instance))
            {
                // Floating free in zero g, the jetpack off and standing on nothing, there is no friction to turn the body by.
                if (!floatLogged && Math.Abs(turn) >= 1e-4f)
                {
                    floatLogged = true;
                    Log.Info($"Body follow: not while floating free in zero g with the jetpack off, the head {MathHelper.ToDegrees(relative):F0} deg from the body; the body turns by the sticks only (it follows again on the boots, the ground or a ladder)");
                }
                turn = 0f;
            }
            float perUnit = __instance.RotationSpeed * 0.02f; // the game turns the body by -indicator.Y * this, radians
            if (Math.Abs(turn) < 1e-4f || perUnit <= 0f)
            {
                if (followed != 0f)
                    Note(followed, moveIndicator != Vector3.Zero);
                followed = 0f;
                return;
            }

            float follow = -turn / perUnit;
            float total = rotationIndicator.Y + follow;
            if (Math.Abs(total) < 1e-3f)
                return;
            share = follow / total;
            rotationIndicator.Y = total;
            forwardBefore = BodyForward(__instance);
            turning = true;
        }

        /// <summary>
        /// A stretch of turning has ended. While walking, the head and the body trade a fraction of a degree every other
        /// frame (twenty such turns a second, nearly all 0.0 to 0.3 deg), so the turns add up and one line a second says
        /// their total and how many there were.
        /// </summary>
        private static void Note(float radians, bool walking)
        {
            heldTurns++;
            heldRadians += radians;
            heldWalking |= walking;
            Report();
        }

        private static void Report()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastNote != 0 && now - lastNote < NoteEvery)
                return;
            lastNote = now;
            Log.Info($"Body followed the head by {MathHelper.ToDegrees(heldRadians):F1} deg ({(heldWalking ? "walking" : "neck limit")})"
                + (heldTurns > 1 ? $" ({heldTurns} turns)" : string.Empty));
            heldTurns = 0;
            heldRadians = 0f;
            heldWalking = false;
        }

        /// <summary>How far the body turned, of which the follow's share goes to the anchor.</summary>
        private static void Measure(MyCharacter __instance)
        {
            if (!turning)
                return;
            turning = false;
            Vector3D up = __instance.WorldMatrix.Up;
            Vector3D before = Vector3D.Reject(forwardBefore, up);
            Vector3D after = Vector3D.Reject(BodyForward(__instance), up);
            double turned = Math.Atan2(Vector3D.Dot(Vector3D.Cross(before, after), up), Vector3D.Dot(before, after));
            if (turned != 0.0)
            {
                GameHead.BodyTurned((float)turned * share);
                followed += (float)turned * share;
            }
        }

        /// <summary>
        /// Walking turns the character's physics proxy, which the entity follows after the physics step; the jetpack
        /// turns the entity itself.
        /// </summary>
        private static Vector3D BodyForward(MyCharacter character)
        {
            var proxy = character.Physics?.CharacterProxy;
            return !character.JetpackRunning && proxy != null ? (Vector3D)proxy.Forward : character.WorldMatrix.Forward;
        }
    }
}
