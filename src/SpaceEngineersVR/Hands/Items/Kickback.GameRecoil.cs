using System;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    // The game's own gun recoil on the aim: where the game would turn the head on foot, the aim that fires the shots turns instead.
    internal static partial class Kickback
    {
        // ---- The game's recoil on the aim ----

        /// <summary>The gun whose recoil turns the aim now; the state below is its own. None once the aim is back.</summary>
        private static MyAutomaticRifleGun aimGun;

        /// <summary>
        /// The game's recoil on the aim, kept as MyAutomaticRifleGun keeps it for the head (degrees), but measured from where
        /// the aim would be without it, so the angle it returns to is always 0: how far up the aim is turned
        /// (m_currentVertAngle) and where the climb is headed (m_nextVertAngle), the climb's and the return's timers
        /// (m_recoilTimer, m_backRecoilTimer), the turn aside as the game's head yaw, + left (m_horizontalAngle), and where its
        /// return started (m_horizontalAngleOriginal).
        /// </summary>
        private static float aimUp, aimNext, aimRise = 1f, aimReturn = 1f, aimYaw, aimYawFrom;

        /// <summary>The game time of the last shot whose recoil went to the aim (milliseconds, m_lastRecoil).</summary>
        private static int aimShotAt;

        /// <summary>The aim is turned by a recoil that has not finished coming back (m_isRecoiling).</summary>
        private static bool aimRecoiling;

        /// <summary>The gun and the aim (the hand's or the head's) last logged, and how many such lines so far.</summary>
        private static MyAutomaticRifleGun aimLoggedGun;
        private static bool aimLoggedHand;
        private static int aimLines;

        /// <summary>
        /// A shot's recoil that applies where the game would turn the head (MyAutomaticRifleGun.RecoilValueChangedCallback,
        /// off the jetpack's spin). In VR the tracked head overwrites the head angles, so the game's turn would reach neither
        /// the view nor the shots; the aim that fires the shots takes it instead (<see cref="Apply"/>, <see cref="TurnHeadAim"/>)
        /// and the head angles are left alone. As the game does it: once the gun's reset time has passed since its last shot a
        /// new climb starts from where the aim is; the climb's target goes up by RecoilGroundVertical times the shot's vertical
        /// value and the stance's multiplier; the aim turns aside at once by RecoilGroundHorizontal times the shot's draw and the
        /// multiplier, held within MAX_HORIZONTAL_RECOIL_DEVIATION all told. False when the gun's figures cannot be read: the
        /// game then has the recoil.
        /// </summary>
        private static bool RecoilOnAim(MyAutomaticRifleGun gun, MyCharacter owner, float vertical, float side)
        {
            MyWeaponDefinition definition = gun.GunBase?.WeaponDefinition;
            if (definition == null || gun.GunBase.WeaponProperties == null)
                return false;
            if (!ReferenceEquals(gun, aimGun))
            {
                AimStill();
                aimGun = gun;
            }
            int now = MySandboxGame.TotalGamePlayTimeInMilliseconds;
            if (now - aimShotAt > gun.GunBase.WeaponProperties.RecoilResetTimeMilliseconds)
            {
                aimNext = aimUp;
                aimReturn = 0f;
            }
            float v = 1f, h = 1f;
            if (RecoilMultipliers != null)
                RecoilMultipliers(gun, owner, out v, out h);
            aimRise = 0f;
            aimNext += definition.RecoilGroundVertical * vertical * v;
            aimRecoiling = true;
            AimAside(definition.RecoilGroundHorizontal * side * h);
            aimShotAt = now;
            NoteAim(gun, owner, definition, v, h);
            return true;
        }

        /// <summary>The turn aside a shot adds (degrees, + left), held within the game's most (MyAutomaticRifleGun.AddHorizontalRecoil).</summary>
        private static void AimAside(float add)
        {
            aimYawFrom = 0f;
            float sum = aimYaw + add;
            if (Math.Abs(sum) < MyAutomaticRifleGun.MAX_HORIZONTAL_RECOIL_DEVIATION)
            {
                aimYaw += add;
                return;
            }
            float rest = MyAutomaticRifleGun.MAX_HORIZONTAL_RECOIL_DEVIATION - Math.Abs(aimYaw);
            aimYaw += sum < 0f ? -rest : rest;
        }

        /// <summary>
        /// The aim's recoil for this frame, in the gun's own update as the game animates the head (AnimateRecoil,
        /// AnimateRecoilHorizontal). Within the reset time of the last shot the aim climbs toward its target, a share more of
        /// the way each frame (RECOIL_SPEED over the shots a second); past it, it is held, then goes back to 0 over about ten
        /// frames (RECOIL_RETURN_SPEED a frame), the turn aside with it.
        /// </summary>
        private static void AnimateAimRecoil(MyAutomaticRifleGun gun)
        {
            var properties = gun.GunBase?.WeaponProperties;
            if (properties == null)
                return;
            int since = MySandboxGame.TotalGamePlayTimeInMilliseconds - aimShotAt;
            float reset = properties.RecoilResetTimeMilliseconds;
            if (since > reset)
            {
                if (aimReturn >= 1f || Math.Abs(aimUp) < 0.001f)
                {
                    if (aimRecoiling)
                    {
                        aimReturn = 1f;
                        aimUp = 0f;
                        aimRecoiling = false;
                    }
                }
                else
                {
                    aimUp = MathHelper.Lerp(aimUp, 0f, aimReturn);
                    aimReturn += MyAutomaticRifleGun.RECOIL_RETURN_SPEED;
                }
            }
            else if (aimRise < 1f)
            {
                float rate = properties.CurrentWeaponRateOfFire / 60f;
                aimRise += rate > 0f ? MyAutomaticRifleGun.RECOIL_SPEED / rate : 1f;
                aimUp = MathHelper.Lerp(aimUp, aimNext, aimRise);
            }
            if (aimYaw != 0f && !(since < reset))
            {
                if (aimYawFrom == 0f)
                    aimYawFrom = aimYaw;
                if (aimReturn >= 1f)
                {
                    aimYaw = 0f;
                    aimYawFrom = 0f;
                }
                else
                {
                    aimYaw = MathHelper.Lerp(aimYawFrom, 0f, aimReturn);
                }
            }
            if (!aimRecoiling && aimYaw == 0f)
                AimStill();
        }

        private static void AimStill()
        {
            aimGun = null;
            aimUp = aimNext = aimYaw = aimYawFrom = 0f;
            aimRise = aimReturn = 1f;
            aimShotAt = 0;
            aimRecoiling = false;
        }

        /// <summary>The game's recoil on the aim now, radians up and to the right, while the gun it is for is in <paramref name="character"/>'s hand.</summary>
        private static bool GameRecoilOnAim(MyCharacter character, out double up, out double right)
        {
            up = right = 0.0;
            if (aimGun == null || (aimUp == 0f && aimYaw == 0f) || character == null || !ReferenceEquals(character.CurrentWeapon, aimGun))
                return false;
            up = MathHelper.ToRadians((double)aimUp);
            right = -MathHelper.ToRadians((double)aimYaw);
            return true;
        }

        /// <summary>
        /// The head matrix a gun's shot leaves along when the hand is not tracked (<see cref="HeldItem.HeadMatrix"/>), turned by
        /// the game's recoil on the aim as the hand's aim is (<see cref="Apply"/>): up about the head's right and aside about its
        /// up, about the eye. Only that matrix turns; the view is built separately and stays where the head looks.
        /// </summary>
        internal static void TurnHeadAim(MyCharacter character, ref MatrixD head)
        {
            if (!HandTools.IsLocalOnFoot(character) || !GameRecoilOnAim(character, out double up, out double right))
                return;
            try
            {
                MatrixD eye = head;
                Turn(ref eye, ref head, up, right, 0.0, 0.0, 0.0);
            }
            catch (Exception e)
            {
                if (applyErrors++ < 3)
                    Log.Error(e, "Kickback could not turn the head's aim by the game's recoil");
            }
        }

        /// <summary>The head yaw (degrees, + left) last added to the synced head angles (<see cref="OnSyncedHead"/>), and the yaw it left them at.</summary>
        private static float syncedYaw, syncedYawLeft;

        /// <summary>
        /// On a client, the game's recoil on the aim added to the local character's head angles, just after the tracked head
        /// has written its pitch into them (<see cref="Tracking.CharacterHead"/>). They are synced to the server, which fires
        /// this player's shots along the head's pitch it rebuilds from them (MyCharacter.UpdateShootDirection, shooting
        /// straight, the yaw left out), so the shots it fires climb as the aim does. The yaw is added on top of whatever else
        /// set it, and taken off again as the recoil goes back; when something else has set the yaw since, that is kept. The
        /// view is built from the tracked head (GameHead.Rotation), not from these angles, and stays where the head looks.
        /// </summary>
        internal static void OnSyncedHead(MyCharacter character)
        {
            if (Sandbox.Game.Multiplayer.Sync.IsServer || character == null)
                return;
            bool active = aimGun != null && ReferenceEquals(character.CurrentWeapon, aimGun);
            float up = active ? aimUp : 0f, yaw = active ? aimYaw : 0f;
            if (up != 0f)
                character.HeadLocalXAngle += up;
            if (yaw == 0f && syncedYaw == 0f)
                return;
            float now = character.HeadLocalYAngle;
            float before = now == syncedYawLeft ? now - syncedYaw : now;
            character.HeadLocalYAngle = before + yaw;
            syncedYaw = yaw;
            syncedYawLeft = character.HeadLocalYAngle;
        }

        /// <summary>One log line for each gun whose recoil goes to the aim, and again when that aim changes (the hand's, the head's), up to <see cref="MaxRecoilLines"/>.</summary>
        private static void NoteAim(MyAutomaticRifleGun gun, MyCharacter owner, MyWeaponDefinition definition, float v, float h)
        {
            bool hand = !HandTools.Off && HandWorld.TryGetNow(owner, VRSettings.DominantHand, out _, out _, out _);
            if (ReferenceEquals(gun, aimLoggedGun) && hand == aimLoggedHand)
                return;
            aimLoggedGun = gun;
            aimLoggedHand = hand;
            if (++aimLines > MaxRecoilLines)
                return;
            double back = (Math.Ceiling(1.0 / MyAutomaticRifleGun.RECOIL_RETURN_SPEED) + 1.0) * Step * 1000.0;
            string aim = hand ? "the hand's aim (the gun turns with it)" : "the head's aim the shots leave along (the hand is not tracked)";
            Log.Info($"Kickback: the game's recoil for the {Name(owner.HandItemDefinition)} turns {aim} instead of the head: " +
                     $"{definition.RecoilGroundVertical * v:0.0} deg up and up to {definition.RecoilGroundHorizontal * h:0.0} deg aside a shot at this stance, " +
                     $"held {gun.GunBase.WeaponProperties.RecoilResetTimeMilliseconds:0} ms after the last shot and back in about {back:0} ms, as the game's own; the view is not turned");
        }
    }
}
