// GunModel: what a hand weapon's own model says about it, its muzzle and its magazine's base (no game state; tests/GameModelTests runs it on the game's models).
using System;
using System.Collections.Generic;
using VRageMath;
using VRageRender.Import;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Reads a hand weapon's muzzle and magazine from its model, in the model's space (the space of the weapon entity's world
    /// matrix), by the game's own rules, so a mod's gun gets them as the game's guns do.
    /// </summary>
    internal static class GunModel
    {
        /// <summary>The muzzle dummies' names the game's gun base looks for (MyGunBase.LoadDummies: a dummy whose name contains one).</summary>
        internal const string ProjectileDummy = "muzzle_projectile", MissileDummy = "muzzle_missile";

        /// <summary>
        /// The muzzle as the game's gun base has it (MyGunBase.LoadDummies, MyAutomaticRifleGun.Init): the first of the model's
        /// dummies, in the model's order, whose name in lower case contains <see cref="ProjectileDummy"/>,
        /// <see cref="MissileDummy"/> or the weapon item's missile dummy name (<paramref name="namedDummy"/>, null for none;
        /// the game's check reads that one name for every kind of muzzle); the dummy's place without its scale. A model with
        /// none fires from the hand item's <paramref name="handItemMuzzle"/>, and so is that muzzle.
        /// </summary>
        internal static Vector3 Muzzle(IEnumerable<KeyValuePair<string, MyModelDummy>> dummies, string namedDummy, Vector3 handItemMuzzle)
        {
            if (dummies != null)
                foreach (KeyValuePair<string, MyModelDummy> dummy in dummies)
                {
                    if (dummy.Key == null || dummy.Value == null)
                        continue;
                    string name = dummy.Key.ToLower(); // the current culture's, as the game's check
                    if (name.Contains(ProjectileDummy) || name.Contains(MissileDummy) || !string.IsNullOrEmpty(namedDummy) && name.Contains(namedDummy))
                        return Matrix.Normalize(dummy.Value.Matrix).Translation;
                }
            return handItemMuzzle;
        }

        /// <summary>
        /// The magazine's base: the middle of the bottom of the magazine subpart's box, at its rest place (the magazine dummy,
        /// <paramref name="rest"/>, without its scale, as MyAutomaticRifleGun.ResetMagazinePosition puts the subpart). Null
        /// when the box is empty (the subpart has no model).
        /// </summary>
        internal static Vector3? MagazineBase(Matrix rest, BoundingBox box)
        {
            if (!(box.Size.LengthSquared() > 1e-12f) || !IsFinite(box.Min) || !IsFinite(box.Max))
                return null;
            Vector3 centre = box.Center;
            return Vector3.Transform(new Vector3(centre.X, box.Min.Y, centre.Z), Matrix.Normalize(rest));
        }

        private static bool IsFinite(Vector3 v) => !float.IsNaN(v.X) && !float.IsInfinity(v.X) && !float.IsNaN(v.Y) && !float.IsInfinity(v.Y)
                                                   && !float.IsNaN(v.Z) && !float.IsInfinity(v.Z);
    }
}
