// Aiming down the sights by the sight line (Sighted, and the iron sight re-asserted) is adapted from ColbyJack1134's
// SpaceEngineersVR (Player/WeaponHandling.cs: Sighted, UpdateAim), with his permission.
using System;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The iron sight in VR is the gun brought up to the eye. A hand weapon's secondary action (the trigger of the hand that
    /// does not hold it, <see cref="SightHand"/>; SECONDARY_TOOL_ACTION) is only its iron sight: MyAutomaticRifleGun.Shoot(SecondaryAction)
    /// calls MyCharacter.Zoom (MyAutomaticRifleGun.cs:577), which puts the character in ZoomMode IronSight and zooms the
    /// camera. So while the player's character holds one (rifles, pistols and the rocket launchers are all
    /// MyAutomaticRifleGun), that trigger is held back from the game (<see cref="Input.VRControls"/>), and instead, after
    /// ColbyJack1134's, the character is in the iron sight while the player looks down the gun's sights
    /// (<see cref="VRSettings.ColbyAimDownSights"/>): the eye on the line through the sight's aim point along the sight
    /// line, behind the muzzle (<see cref="Sighted(Vector3D, Vector3D, Vector3D, Vector3D, bool)"/>). What the game's iron
    /// sight does then is what a flat player aiming gets: the tighter spread (MyGunBase: DeviateShotAngleAiming in ZoomMode
    /// IronSight), the slower walk and no sprint.
    /// </summary>
    /// <remarks>
    /// <para>The iron sight is entered without the camera (EnableIronsight with changeCamera false: the zoom, the crosshair and
    /// the camera controller are left alone), as the eyes are drawn with the headset's own field of view anyway. The game
    /// drops the iron sight by itself as a reload starts (MyAutomaticRifleGun.Reload), so it is put back while the sights
    /// are still lined up, once the reload is over. A small tick on the gun hand marks starting and stopping to aim.</para>
    /// <para>A gun is aimed down its sights when its sight is known (<see cref="GunProfile.Sight"/>): the game's own guns have
    /// ColbyJack1134's measured ones, and a mod's gun the one its mod registers (SevrApi <c>RegisterGunSight</c>); a gun with
    /// none is never aimed down its sights. A character in the iron sight any other way (a key, the drive, a mod) is taken out of it as the game does
    /// when the weapon is put away (MyCharacter.cs:8777): EnableIronsight off, the view kept as it was, and the camera's zoom
    /// reset.</para>
    /// </remarks>
    internal static class IronSight
    {
        /// <summary>Behind the muzzle, the eye at least this far and at most this far along the sight line (metres): ColbyJack1134's.</summary>
        private const double EyeNearest = 0.12, EyeFarthest = 1.1;

        /// <summary>The eye this close to the sight line (metres) starts aiming; once aiming, this wider tolerance keeps it, so the sight picture does not flicker.</summary>
        private const double OnLine = 0.09, StaysOnLine = 0.14;

        /// <summary>Starting to aim: a small tick on the gun hand; stopping: a lighter one (ColbyJack1134's).</summary>
        public const float AimAmplitude = 0.45f, AimSeconds = 0.03f, StopAmplitude = 0.25f, StopSeconds = 0.02f;

        private static bool loggedHolding, leaving, aiming;

        /// <summary>The iron sight is the sight line's: leaving it goes without the camera, as it came.</summary>
        private static bool ours;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(IronSight));

        /// <summary>The player is aiming down the held gun's sights now.</summary>
        public static bool Aiming => aiming;

        /// <summary>The trigger that fires a hand weapon: the hand that holds it (the dominant one).</summary>
        public static Hand FireHand => VRSettings.DominantHand;

        /// <summary>The trigger that would be a hand weapon's iron sight: the other hand's.</summary>
        public static Hand SightHand => FireHand == Hand.Left ? Hand.Right : Hand.Left;

        /// <summary>
        /// Game thread, once a frame on foot and on the jetpack, before the triggers are read: true while the player's character
        /// holds a hand weapon, whose <see cref="SightHand"/> trigger is then held back from the game. Puts the character in the
        /// iron sight while the player looks down the gun's sights, and takes it out of one it is in any other way.
        /// </summary>
        public static bool Frame()
        {
            // Switched off, no hand weapon is held and no sight is looked down; it picks up from the game's state when it is back.
            if (!guard.On)
            {
                aiming = false;
                return false;
            }
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                bool onFoot = HandTools.IsLocalOnFoot(character);
                bool holding = onFoot && character.CurrentWeapon is MyAutomaticRifleGun;
                if (holding != loggedHolding)
                {
                    loggedHolding = holding;
                    if (holding)
                        Log.Info($"Iron sight: {Describe(character)} in hand; the {FireHand.ToString().ToLowerInvariant()} trigger fires, the {SightHand.ToString().ToLowerInvariant()} one (its iron sight) is held back from the game" +
                                 (!VRSettings.ColbyAimDownSights ? string.Empty
                                  : GunProfile.Of(character)?.Sight != null ? "; aimed down its sights by looking along them" : "; its sight is not known (a mod gives it with RegisterGunSight), so it is not aimed down it"));
                }

                bool sighted = holding && character.Definition?.CanIronsight == true && VRSettings.ColbyAimDownSights && !HandTools.Off && VRInput.Active && Sighted(character, aiming);
                if (sighted != aiming)
                {
                    aiming = sighted;
                    if (sighted)
                        Haptics.Pulse(FireHand, AimAmplitude, AimSeconds);
                    else if (holding)
                        Haptics.Pulse(FireHand, StopAmplitude, StopSeconds);
                    Log.Info(sighted ? $"Iron sight: looking down the sights of {Describe(character)}: aiming (the game's iron sight)" : "Iron sight: no longer aiming down the sights");
                }

                bool inSight = onFoot && character.ZoomMode == MyZoomModeEnum.IronSight;
                if (aiming)
                {
                    // The game drops the iron sight as a reload starts: put back once the reload is over.
                    if (!inSight && !character.CurrentWeapon.IsReloading)
                        character.EnableIronsight(true, true, false);
                    ours = true;
                    leaving = false;
                    return holding;
                }
                if (inSight && !leaving)
                {
                    if (ours)
                        character.EnableIronsight(false, true, false);
                    else
                        Leave(character);
                }
                ours = false;
                // Once per stay in the sight: the game's way out goes through its event, which may land a frame later.
                leaving = inSight;
                return holding;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Iron sight: could not be checked this frame");
                return false;
            }
        }

        /// <summary>
        /// ColbyJack1134's sight test: the eye is behind the muzzle (between <see cref="EyeNearest"/> and <see cref="EyeFarthest"/>
        /// along the sight line) and within <see cref="OnLine"/> of the line through the sight's aim point, or
        /// <see cref="StaysOnLine"/> while already aiming.
        /// </summary>
        internal static bool Sighted(Vector3D eye, Vector3D muzzle, Vector3D sight, Vector3D direction, bool was)
        {
            double along = Vector3D.Dot(eye - muzzle, direction);
            if (along > -EyeNearest || along < -EyeFarthest)
                return false;
            Vector3D offset = eye - sight;
            return (offset - direction * Vector3D.Dot(offset, direction)).Length() < (was ? StaysOnLine : OnLine);
        }

        /// <summary>The held gun's sight lined up with the eye: the gun as drawn, the eye as tracked this frame.</summary>
        private static bool Sighted(MyCharacter character, bool was)
        {
            GunProfile profile = GunProfile.Of(character);
            MyEntity weapon = character.CurrentWeapon as MyEntity;
            if (profile?.Sight == null || weapon == null || !GameHead.Take() || !HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _))
                return false;
            MatrixD toWorld = trackingToModel * MatrixD.Invert(character.PositionComp.WorldMatrixNormalizedInv);
            Vector3D eye = Vector3D.Transform((Vector3D)GameHead.TrackingPose.Translation, toWorld);
            MatrixD pose = weapon.WorldMatrix;
            Vector3D direction = Vector3D.Normalize(Vector3D.TransformNormal((Vector3D)profile.SightDirection, pose));
            return Sighted(eye, Vector3D.Transform((Vector3D)profile.Muzzle, pose), Vector3D.Transform((Vector3D)profile.Sight.Value, pose), direction, was);
        }

        /// <summary>The game's own way out of the iron sight, as it does when the weapon is put away (MyCharacter.cs:8777).</summary>
        private static void Leave(MyCharacter character)
        {
            bool firstPerson = character.IsInFirstPersonView;
            character.EnableIronsight(false, true, MySession.Static.CameraController == character);
            character.IsInFirstPersonView = firstPerson;
            MySector.MainCamera?.Zoom.ResetZoom();
            Log.Info($"Iron sight: the character was in the iron sight with {Describe(character)}, not by looking down its sights; taken out of it");
        }

        private static string Describe(MyCharacter character)
        {
            var weapon = character.CurrentWeapon as MyEntity;
            string name = weapon?.DisplayNameText;
            return string.IsNullOrEmpty(name) ? "a hand weapon" : $"'{name}'";
        }
    }
}
