using System;
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
    /// does not move with it.
    /// </summary>
    internal static class BodyFollow
    {
        private static readonly float NeckLimit = MathHelper.ToRadians(50f);
        private static readonly float MaxTurnPerFrame = MathHelper.ToRadians(9f); // 540 deg/s at 60 Hz

        private static bool turning;
        private static float share;
        private static float followed;
        private static Vector3D forwardBefore;

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
            if (__instance != MySession.Static?.LocalCharacter || __instance.IsSitting || __instance.IsDead)
                return;
            if (!__instance.IsInFirstPersonView && !__instance.ForceFirstPersonCamera)
                return;
            if (!GameHead.Take())
                return;

            float relative = GameHead.Yaw;
            float target = moveIndicator != Vector3.Zero ? 0f : MathHelper.Clamp(relative, -NeckLimit, NeckLimit);
            float turn = MathHelper.Clamp(relative - target, -MaxTurnPerFrame, MaxTurnPerFrame);
            float perUnit = __instance.RotationSpeed * 0.02f; // the game turns the body by -indicator.Y * this, radians
            if (Math.Abs(turn) < 1e-4f || perUnit <= 0f)
            {
                if (followed != 0f)
                    Log.Info($"Body followed the head by {MathHelper.ToDegrees(followed):F1} deg ({(moveIndicator != Vector3.Zero ? "walking" : "neck limit")})");
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
