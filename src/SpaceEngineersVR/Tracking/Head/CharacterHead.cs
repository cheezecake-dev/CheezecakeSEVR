using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.Common;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Puts the tracked head into the local character's head matrix (MyCharacter.GetHeadMatrixInternal). Aiming, the
    /// use and interaction rays, block placement and the first-person camera are all built from it, so they follow
    /// where the player looks. The body keeps its own facing and the head turns on top of it; the tracking space's
    /// forward is the body's forward. Seated, the seat's head matrix takes over (<see cref="CockpitHead"/>).
    /// </summary>
    internal static class CharacterHead
    {
        public static void Patch(Harmony harmony)
        {
            // GetHeadMatrixInternal builds the matrix that the aim, the use ray, block placement and the first-person camera all
            // start from. The postfix replaces its result with the tracked head's pose; the prefix first lets the game's own head
            // angles take over where the head was lost, so the view does not jump. Without this patch the view follows the mouse.
            harmony.Patch(AccessTools.Method(typeof(MyCharacter), "GetHeadMatrixInternal"),
                prefix: new HarmonyMethod(typeof(CharacterHead), nameof(HandOver)),
                postfix: new HarmonyMethod(typeof(CharacterHead), nameof(TrackHead)));
            GameHead.PatchView(harmony, AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.GetViewMatrix)));
        }

        /// <summary>The head matrix being built is the local character's first-person head (the camera's, the aim's).</summary>
        private static bool Ours(MyCharacter character, int headBone)
        {
            return headBone != -1 && headBone == CharacterFields.HeadBoneIndex(character) && IsLocalFirstPerson(character);
        }

        /// <summary>The character is the local player's, on foot, seen from its own eyes: its head matrix is the tracked head's.</summary>
        public static bool IsLocalFirstPerson(MyCharacter character)
        {
            return character == MySession.Static?.LocalCharacter
                   && !character.IsSitting && (character.IsInFirstPersonView || character.ForceFirstPersonCamera);
        }

        /// <summary>
        /// A lost head leaves the game's own head angles, which the matrix is about to be built from, where they were:
        /// they take over at the angles the head looked at (<see cref="GameHead.BeforeGameHead"/>), so the view stays.
        /// </summary>
        private static void HandOver(MyCharacter __instance, int headBone)
        {
            if (!Ours(__instance, headBone))
                return;
            float x = __instance.HeadLocalXAngle, y = __instance.HeadLocalYAngle;
            if (GameHead.BeforeGameHead(ref x, ref y))
            {
                __instance.HeadLocalXAngle = x;
                __instance.HeadLocalYAngle = y;
            }
        }

        private static void TrackHead(MyCharacter __instance, int headBone, bool includeY, bool includeX, ref MatrixD __result)
        {
            if (!Ours(__instance, headBone))
                return;
            // The body is drawn ahead of the head bone (BodyShift), and the head bone with it: the eye stays where it was.
            float ahead = BodyShift.Ahead(__instance, headBone);
            if (ahead != 0f)
                __result.Translation += Vector3D.TransformNormal(new Vector3D(0.0, 0.0, ahead), __instance.PositionComp.WorldMatrixRef);
            if (!GameHead.Take())
            {
                GameHead.GameHeadShown(__instance.HeadLocalYAngle);
                MagBootsSmoothing.Apply(__instance, ref __result);
                return;
            }
            // Got up from a seat: the head is placed from the on-foot centre again (the seat's was the sit-down's).
            GameHead.OnFoot();

            // Synced to other players and used by the head animation, so others see the head tilt. On a client the game's recoil
            // on the aim goes on top: the server fires this player's shots along the synced pitch.
            __instance.HeadLocalXAngle = MathHelper.ToDegrees(GameHead.Pitch);
            Hands.Kickback.OnSyncedHead(__instance);

            // The game's own head matrix is pitch * yaw (from the mouse) on the body's basis, at the head bone.
            MatrixD basis = MatrixD.CreateFromDir(__instance.WorldMatrix.Forward, __instance.WorldMatrix.Up);
            MatrixD result = (MatrixD)GameHead.Rotation(includeY, includeX) * basis;
            result.Translation = __result.Translation;
            if (includeX && includeY && !Rendering.WallFade.EyeHeld)
            {
                // The head offset from the bone, kept out of the character's own body (BodyGuard: a real crouch is the
                // character's, and what is left is pushed out of the suit and the backpack), the body where it is drawn
                // this frame (BodyShift.Reckon), also when this is built before the bones are moved. Not at all once the
                // wall fade has failed for good: the eye is then held at the head bone, where no wall can be reached through.
                Vector3 offset = BodyGuard.Offset(__instance, __result.Translation, GameHead.Head.Translation, BodyShift.Reckon(__instance, ahead));
                result.Translation += Vector3D.TransformNormal((Vector3D)offset, basis);
            }
            __result = result;
            if (includeX && includeY)
                CameraShake.NoteEye(__instance, result.Translation);
            // On magnetic boots the head rides the smoothed up, not the body's (the hands, tools and lasers are placed by it too).
            MagBootsSmoothing.Apply(__instance, ref __result);
            GameHead.UsedForView();
        }
    }
}
