using System;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// No iron sight in VR: the sight is aimed by bringing the gun up to the eye. A hand weapon's secondary action (the trigger
    /// of the hand that does not hold it, <see cref="SightHand"/>; SECONDARY_TOOL_ACTION) is only its iron sight: MyAutomaticRifleGun.Shoot(SecondaryAction) calls
    /// MyCharacter.Zoom (MyAutomaticRifleGun.cs:577), which puts the character in ZoomMode IronSight, zooms the camera in, hides
    /// the crosshair, slows walking and stops sprinting (MyCharacter.EnableIronsight, MyCharacter.cs:7871). So while the
    /// player's character holds one (rifles, pistols and the rocket launcher are all MyAutomaticRifleGun), that trigger is
    /// held back from the game until it is let go (<see cref="Input.VRControls"/>).
    /// </summary>
    /// <remarks>
    /// The eyes are drawn with the headset's own field of view, so the game's camera zoom never reached them; what the sight
    /// did reach is the rest (the slower walk, no sprint, the hidden crosshair). Should the character be in the iron sight
    /// anyway (a key, the drive, a mod), it is taken out of it as the game does when the weapon is put away
    /// (MyCharacter.cs:8777): EnableIronsight off, the view kept as it was, and the camera's zoom reset.
    /// </remarks>
    internal static class IronSight
    {
        private static bool loggedHolding, leaving;
        private static int errors;

        /// <summary>The trigger that fires a hand weapon: the hand that holds it (the dominant one).</summary>
        public static Hand FireHand => VRSettings.DominantHand;

        /// <summary>The trigger that would be a hand weapon's iron sight: the other hand's.</summary>
        public static Hand SightHand => FireHand == Hand.Left ? Hand.Right : Hand.Left;

        /// <summary>
        /// Game thread, once a frame on foot and on the jetpack, before the triggers are read: true while the player's character
        /// holds a hand weapon, whose <see cref="SightHand"/> trigger is then held back from the game. Takes the character out of an iron sight it is in.
        /// </summary>
        public static bool Frame()
        {
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                bool holding = HandTools.IsLocalOnFoot(character) && character.CurrentWeapon is MyAutomaticRifleGun;
                if (holding != loggedHolding)
                {
                    loggedHolding = holding;
                    if (holding)
                        Log.Info($"Iron sight: {Describe(character)} in hand; the {FireHand.ToString().ToLowerInvariant()} trigger fires, the {SightHand.ToString().ToLowerInvariant()} one (its iron sight) is held back from the game");
                }
                // Once per stay in the sight: the game's way out goes through its event, which may land a frame later.
                bool inSight = HandTools.IsLocalOnFoot(character) && character.ZoomMode == MyZoomModeEnum.IronSight;
                if (inSight && !leaving)
                    Leave(character);
                leaving = inSight;
                return holding;
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Iron sight: could not be checked this frame");
                return false;
            }
        }

        /// <summary>The game's own way out of the iron sight, as it does when the weapon is put away (MyCharacter.cs:8777).</summary>
        private static void Leave(MyCharacter character)
        {
            bool firstPerson = character.IsInFirstPersonView;
            character.EnableIronsight(false, true, MySession.Static.CameraController == character);
            character.IsInFirstPersonView = firstPerson;
            MySector.MainCamera?.Zoom.ResetZoom();
            Log.Info($"Iron sight: the character was in the iron sight with {Describe(character)}; taken out of it (aim by bringing the gun to the eye)");
        }

        private static string Describe(MyCharacter character)
        {
            var weapon = character.CurrentWeapon as MyEntity;
            string name = weapon?.DisplayNameText;
            return string.IsNullOrEmpty(name) ? "a hand weapon" : $"'{name}'";
        }
    }
}
