// GunProfile.Held: the profile of the gun in the character's hand, read from its definition and model as it is drawn.
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using SpaceEngineersVR.Api;
using VRage.Game.Entity;
using VRage.Game.Models;
using VRageMath;
using VRageRender.Import;

namespace SpaceEngineersVR.Hands
{
    internal sealed partial class GunProfile
    {
        private static MyEntity lastWeapon;
        private static MyModel lastMagazineModel;
        private static int lastSights = -1;
        private static GunProfile lastProfile;

        /// <summary>
        /// The profile of the gun in the character's hand, or null (nothing in hand, a tool). Made again when another gun is
        /// drawn, when its magazine subpart's model arrives, or when a mod registers or takes away a sight. Game thread.
        /// </summary>
        internal static GunProfile Of(MyCharacter character)
        {
            MyAutomaticRifleGun gun = character?.CurrentWeapon as MyAutomaticRifleGun;
            if (gun == null || gun.Closed)
                return null;
            MyModel magazineModel = MagazinePart(gun)?.Model;
            int sights = ApiRuntime.Guns.Version;
            if (!ReferenceEquals(gun, lastWeapon) || !ReferenceEquals(magazineModel, lastMagazineModel) || sights != lastSights)
            {
                lastWeapon = gun;
                lastMagazineModel = magazineModel;
                lastSights = sights;
                lastProfile = Read(character, gun, magazineModel);
            }
            return lastProfile;
        }

        private static GunProfile Read(MyCharacter character, MyAutomaticRifleGun gun, MyModel magazineModel)
        {
            MyHandItemDefinition hand = character.HandItemDefinition;
            string item = gun.PhysicalObject?.SubtypeName;
            Dictionary<string, MyModelDummy> dummies = gun.Model?.Dummies;

            // The game's gun base reads the weapon item's dummy names (MyAutomaticRifleGun.Init); its check uses the missile one.
            string named = null;
            if (gun.PhysicalItemDefinition is MyWeaponItemDefinition weaponItem && weaponItem.DummyNames != null)
                weaponItem.DummyNames.TryGetValue(MyGunBase.DUMMY_KEY_MISSILE, out named);
            Vector3 muzzle = GunModel.Muzzle(dummies, named, hand?.MuzzlePosition ?? Vector3.Zero);

            Vector3? magazine = null;
            if (magazineModel != null && dummies != null && dummies.TryGetValue(MyAutomaticRifleGun.NAME_DUMMY_MAGAZINE, out MyModelDummy rest) && rest != null)
                magazine = GunModel.MagazineBase(rest.Matrix, magazineModel.BoundingBox);

            return Make(hand?.WeaponType ?? VRage.Game.MyItemWeaponType.None, muzzle, magazine, Vanilla.Find(item, gun.Model?.AssetName),
                        ApiRuntime.Guns.Of(item));
        }

        private static MyEntitySubpart MagazinePart(MyEntity gun) =>
            gun.Subparts != null && gun.Subparts.TryGetValue(MyAutomaticRifleGun.NAME_SUBPART_MAGAZINE, out MyEntitySubpart part) ? part : null;
    }
}
