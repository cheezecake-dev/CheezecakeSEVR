// Adapted from ColbyJack1134's SpaceEngineersVR (Player/Shared/WeaponPose.cs: WeaponProfile's guns, their kind, muzzle,
// sight and magazine, measured on the game's meshes), with his permission. What the model carries is now read from it
// (GunModel); his sights stay, and his muzzle where the game's differs (no game state; tests/GameModelTests runs it).
using System;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// What the plugin knows of a hand weapon, in the weapon model's space (the space of the weapon entity's world matrix):
    /// its kind, where the muzzle is, where the magazine's base is, and where the sight's aim point is (peep, notch, reflex dot
    /// or reticle) and which way the sight line runs. The kind, the muzzle and the magazine come from the gun's own definition
    /// and model (<see cref="GunModel"/>), so a mod's gun has them as the game's do; a model carries no sight, so the game's
    /// guns have ColbyJack1134's measured ones (<see cref="Vanilla"/>) and a mod's gun the one its mod registers through
    /// SevrApi (<c>RegisterGunSight</c>, <see cref="Api.ApiGuns"/>), or none, and then it is never aimed down the sights
    /// (<see cref="IronSight"/>).
    /// </summary>
    internal sealed partial class GunProfile
    {
        internal readonly MyItemWeaponType Kind;
        internal readonly Vector3 Muzzle;

        /// <summary>The magazine's base; null for a model without a magazine subpart (<see cref="Reload"/> then reaches for a zone under the gun).</summary>
        internal readonly Vector3? Magazine;

        /// <summary>The sight's aim point; the sight line runs through it along <see cref="SightDirection"/>. Null: no sight known.</summary>
        internal readonly Vector3? Sight;

        /// <summary>The sight line's direction, a unit vector: along the bore unless the sight says otherwise (the flare gun's post sits above its rear block).</summary>
        internal readonly Vector3 SightDirection;

        internal bool IsPistol => Kind == MyItemWeaponType.Pistol;

        private GunProfile(MyItemWeaponType kind, Vector3 muzzle, Vector3? magazine, Vector3? sight, Vector3 sightDirection)
        {
            Kind = kind;
            Muzzle = muzzle;
            Magazine = magazine;
            Sight = sight;
            SightDirection = sightDirection;
        }

        /// <summary>
        /// A gun's profile from what its definition and model give (<paramref name="kind"/>, <paramref name="muzzle"/>,
        /// <paramref name="magazine"/>, from <see cref="GunModel"/>), the game's own gun it is (<paramref name="vanilla"/>,
        /// <see cref="Vanilla.Find"/>, null for any other), and the sight a mod registered for its item
        /// (<paramref name="registered"/>, null for none). The game's gun keeps its measured sight and, where it has one, its
        /// measured muzzle, so nothing a mod registers changes it.
        /// </summary>
        internal static GunProfile Make(MyItemWeaponType kind, Vector3 muzzle, Vector3? magazine, Vanilla vanilla, Api.ApiGuns.Sight? registered)
        {
            if (vanilla != null)
                return new GunProfile(kind, vanilla.Muzzle ?? muzzle, magazine, vanilla.Sight, vanilla.SightDirection);
            if (registered.HasValue)
                return new GunProfile(kind, muzzle, magazine, registered.Value.Point, registered.Value.Direction);
            return new GunProfile(kind, muzzle, magazine, null, Vector3.Forward);
        }

        /// <summary>
        /// ColbyJack1134's measurements of the game's own guns that their models do not carry: each sight, and the Elite rocket
        /// launcher's muzzle (its model's muzzle dummy, "Dummy_Muzzle", is not one the game's gun base reads, so the game fires
        /// it from the hand item's MuzzlePosition, 9 cm lower and 2 cm further forward; the sight test keeps his).
        /// </summary>
        internal sealed class Vanilla
        {
            internal readonly string Item, Model;
            internal readonly Vector3 Sight;
            internal readonly Vector3 SightDirection = Vector3.Forward;

            /// <summary>Null: the muzzle the game uses (<see cref="GunModel.Muzzle"/>).</summary>
            internal readonly Vector3? Muzzle;

            private Vanilla(string item, string model, Vector3 sight, Vector3? sightDirection = null, Vector3? muzzle = null)
            {
                Item = item;
                Model = "Models/Weapons/" + model + ".mwm";
                Sight = sight;
                if (sightDirection.HasValue)
                    SightDirection = Vector3.Normalize(sightDirection.Value);
                Muzzle = muzzle;
            }

            internal static readonly Vanilla[] All =
            {
                new Vanilla("AutomaticRifleItem", "AutomaticRifle", new Vector3(0, .1121f, -.0107f)),
                new Vanilla("PreciseAutomaticRifleItem", "PreciseAutomaticRifle", new Vector3(0, .11f, -.0109f)),
                new Vanilla("RapidFireAutomaticRifleItem", "RapidFireAutomaticRifle", new Vector3(0, .1121f, -.0107f)),
                new Vanilla("UltimateAutomaticRifleItem", "UltimateAutomaticRifle", new Vector3(0, .11f, -.0109f)),
                new Vanilla("SemiAutoPistolItem", "Pistol_Warfare", new Vector3(0, .0881f, .083f)),
                new Vanilla("FullAutoPistolItem", "Pistol_FullAuto_Warfare", new Vector3(0, .0881f, .083f)),
                new Vanilla("ElitePistolItem", "Pistol_Elite_Warfare", new Vector3(0, .0981f, .04f)),
                new Vanilla("FlareGunItem", "Pistol_FlareGun", new Vector3(0, .0931f, .0553f), new Vector3(0, .0041f, -.1386f)),
                new Vanilla("BasicHandHeldLauncherItem", "RocketLauncher_Regular", new Vector3(-.1635f, .1681f, -.068f)),
                new Vanilla("AdvancedHandHeldLauncherItem", "RocketLauncher_Precision", new Vector3(-.163f, .1682f, -.1217f),
                    muzzle: new Vector3(-0.00000004f, 0.16770662f, -0.41881937f)),
            };

            /// <summary>The game's gun whose item is <paramref name="item"/> and whose model is <paramref name="model"/>, or null (a mod's gun, or a mod's model on the game's item).</summary>
            internal static Vanilla Find(string item, string model)
            {
                if (item == null || model == null)
                    return null;
                string path = model.Replace('\\', '/');
                foreach (Vanilla v in All)
                    if (item == v.Item && path.EndsWith(v.Model, StringComparison.OrdinalIgnoreCase))
                        return v;
                return null;
            }
        }
    }
}
