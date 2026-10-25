using System;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// In a multiplayer game, the aim the game syncs follows the gun in the hand, so the shots go where the hand points in
    /// every player's game. Every machine fires a hand gun along its own head matrix, not the direction the shooter sent
    /// (MyCharacter.ShootInternal calls UpdateShootDirection with shootStraight, which takes GetHeadMatrix(includeY: false)'s
    /// forward): that is the synced head pitch on the body's facing. So while the local character holds a gun on foot, the
    /// head pitch written into the synced head angles is the gun's (<see cref="Tracking.CharacterHead"/>) and the body turns
    /// to face where the gun points (<see cref="Tracking.BodyFollow"/>). The view stays the tracked head's. Other players see
    /// the game's own animation point the gun where the shots go.
    /// <list type="bullet">
    /// <item>Joining, the host fires this player's shots, and their damage, along the synced aim.</item>
    /// <item>Hosting, this game's own shots already leave along the hand (<see cref="HeldItem.HeadMatrix"/>) and only the
    /// host deals damage (MyProjectile.DoDamage is server-only), so the synced aim changes no hit and no damage: it is what
    /// the other players' games draw the host's shots from, the tracers and the gun's animation.</item>
    /// </list>
    /// Single player has nobody to show it to, and nothing changes.
    /// </summary>
    /// <remarks>
    /// Not on the jetpack: there the game fires along the body's own pitch as well (includeX is off), and the body turns by
    /// the sticks only, so every game still fires along the body's forward.
    /// </remarks>
    internal static class SyncedAim
    {
        private static Vector3D handForward;
        private static int handFrame = -10;
        private static bool logged, jetpackLogged;

        /// <summary>The gun's aim as <see cref="HeldItem"/> placed it this frame (the game's recoil included).</summary>
        internal static void NoteHand(MyCharacter character, in Vector3D forward)
        {
            if (!(character.CurrentWeapon is MyAutomaticRifleGun))
                return;
            handForward = forward;
            handFrame = MySession.Static.GameplayFrameCounter;
        }

        /// <summary>
        /// The gun's aim from the body, as the other players' games fire this player's shots: pitch above the body's horizon,
        /// degrees, and yaw from the body's forward, radians, + left (as <see cref="Tracking.GameHead.Yaw"/>). False in single
        /// player, on the jetpack, and without a gun placed in the hand in the last two frames.
        /// </summary>
        internal static bool TryGunAim(MyCharacter character, out float pitchDegrees, out float yaw)
        {
            pitchDegrees = yaw = 0f;
            if (!Sync.MultiplayerActive || !HandTools.IsLocalOnFoot(character) || character.IsSitting
                || !(character.CurrentWeapon is MyAutomaticRifleGun))
                return false;
            if (MySession.Static.GameplayFrameCounter - handFrame > 2)
                return false;
            if (character.JetpackRunning)
            {
                if (!jetpackLogged)
                {
                    jetpackLogged = true;
                    Log.Info("Synced aim: not on the jetpack; the game fires along the body's forward there");
                }
                return false;
            }
            MatrixD body = character.WorldMatrix;
            Vector3D up = body.Up;
            Vector3D level = Vector3D.Reject(handForward, up);
            double across = level.Length();
            if (across < 1e-4)
                return false;
            pitchDegrees = MathHelper.ToDegrees((float)Math.Atan2(Vector3D.Dot(handForward, up), across));
            level /= across;
            yaw = (float)Math.Atan2(Vector3D.Dot(Vector3D.Cross(body.Forward, level), up), Vector3D.Dot(body.Forward, level));
            if (!logged)
            {
                logged = true;
                Log.Info($"Synced aim ({(Sync.IsServer ? "hosting" : "joining")}): the synced head pitch and the body's facing follow the gun (pitch {pitchDegrees:F1} deg, yaw {MathHelper.ToDegrees(yaw):F1} deg from the body)");
            }
            return true;
        }
    }
}
