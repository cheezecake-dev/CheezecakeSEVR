// Adapted from ColbyJack1134's SpaceEngineersVR (Player/Shared/WeaponPose.cs: WeaponProfile's guns, their kind, muzzle,
// sight and magazine, measured on the game's meshes), with his permission.
using System;
using Sandbox.Game.Entities.Character;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal enum GunKind { Rifle, Pistol, Launcher }

    /// <summary>
    /// ColbyJack1134's measurements of the game's own hand weapons, in the weapon model's space (the space of the weapon
    /// entity's world matrix): where the muzzle is, where the sight's aim point is (peep, notch, reflex dot or reticle) and
    /// which way the sight line runs, and where the magazine's base is. Only the game's weapons are measured; a mod's weapon
    /// has no profile (<see cref="Of"/> is null), so it is never aimed down the sights (<see cref="IronSight"/>) and its
    /// magazine is reached for by the model's own box (<see cref="Reload"/>).
    /// </summary>
    internal sealed class GunProfile
    {
        internal readonly string Item, Model;
        internal readonly GunKind Kind;
        internal readonly Vector3 Muzzle, Magazine;

        /// <summary>The sight's aim point; the sight line runs through it along <see cref="SightDirection"/>.</summary>
        internal readonly Vector3? Sight;

        /// <summary>The sight line's direction where it is not parallel to the bore (the flare gun's post sits above its rear block).</summary>
        internal readonly Vector3 SightDirection = Vector3.Forward;

        private GunProfile(string item, string model, GunKind kind, Vector3 muzzle, Vector3 magazine, Vector3? sight, Vector3? sightDirection = null)
        {
            Item = item;
            Model = "Models/Weapons/" + model + ".mwm";
            Kind = kind;
            Muzzle = muzzle;
            Magazine = magazine;
            Sight = sight;
            if (sightDirection.HasValue)
                SightDirection = Vector3.Normalize(sightDirection.Value);
        }

        internal static readonly GunProfile[] All =
        {
            new GunProfile("AutomaticRifleItem", "AutomaticRifle", GunKind.Rifle, new Vector3(0.00000013f, 0.04812809f, -0.31885853f), new Vector3(0f, -0.10668955f, -0.07233407f), new Vector3(0, .1121f, -.0107f)),
            new GunProfile("PreciseAutomaticRifleItem", "PreciseAutomaticRifle", GunKind.Rifle, new Vector3(0.00000013f, 0.04812809f, -0.31885853f), new Vector3(0f, -0.09042368f, -0.06554391f), new Vector3(0, .11f, -.0109f)),
            new GunProfile("RapidFireAutomaticRifleItem", "RapidFireAutomaticRifle", GunKind.Rifle, new Vector3(0.00000013f, 0.04812809f, -0.31885853f), new Vector3(0f, -0.19320688f, -0.07912423f), new Vector3(0, .1121f, -.0107f)),
            new GunProfile("UltimateAutomaticRifleItem", "UltimateAutomaticRifle", GunKind.Rifle, new Vector3(0.00000013f, 0.04812809f, -0.31885853f), new Vector3(0f, -0.12747202f, -0.07080819f), new Vector3(0, .11f, -.0109f)),
            new GunProfile("SemiAutoPistolItem", "Pistol_Warfare", GunKind.Pistol, new Vector3(0f, 0.067419f, -0.17239414f), new Vector3(0f, -0.09267507f, 0.06955369f), new Vector3(0, .0881f, .083f)),
            new GunProfile("FullAutoPistolItem", "Pistol_FullAuto_Warfare", GunKind.Pistol, new Vector3(0f, 0.06741901f, -0.17239273f), new Vector3(0f, -0.16958985f, 0.08965235f), new Vector3(0, .0881f, .083f)),
            new GunProfile("ElitePistolItem", "Pistol_Elite_Warfare", GunKind.Pistol, new Vector3(0f, 0.06741901f, -0.17239273f), new Vector3(0f, -0.09287956f, 0.0760093f), new Vector3(0, .0981f, .04f)),
            new GunProfile("FlareGunItem", "Pistol_FlareGun", GunKind.Pistol, new Vector3(0f, 0.06341899f, -0.10469195f), new Vector3(0f, -0.09649658f, 0.08415222f), new Vector3(0, .0931f, .0553f), new Vector3(0, .0041f, -.1386f)),
            new GunProfile("BasicHandHeldLauncherItem", "RocketLauncher_Regular", GunKind.Launcher, new Vector3(0.00000014f, 0.16771293f, -0.41278997f), new Vector3(-0.00001526f, -0.03022767f, -0.20481059f), new Vector3(-.1635f, .1681f, -.068f)),
            new GunProfile("AdvancedHandHeldLauncherItem", "RocketLauncher_Precision", GunKind.Launcher, new Vector3(-0.00000004f, 0.16770662f, -0.41881937f), new Vector3(-0.00001526f, -0.0294178f, -0.20481059f), new Vector3(-.163f, .1682f, -.1217f)),
        };

        internal static GunProfile Find(string item, string model)
        {
            if (item == null || model == null)
                return null;
            string path = model.Replace('\\', '/');
            foreach (GunProfile p in All)
                if (item == p.Item && path.EndsWith(p.Model, StringComparison.OrdinalIgnoreCase))
                    return p;
            return null;
        }

        private static MyEntity lastWeapon;
        private static GunProfile lastProfile;

        /// <summary>The profile of the weapon in the character's hand, or null (nothing in hand, a tool, a mod's weapon). Game thread.</summary>
        internal static GunProfile Of(MyCharacter character)
        {
            MyEntity weapon = character?.CurrentWeapon as MyEntity;
            if (weapon == null)
                return null;
            if (!ReferenceEquals(weapon, lastWeapon))
            {
                lastWeapon = weapon;
                lastProfile = Find(character.CurrentWeapon.PhysicalObject?.SubtypeName, weapon.Model?.AssetName);
            }
            return lastProfile;
        }
    }
}
