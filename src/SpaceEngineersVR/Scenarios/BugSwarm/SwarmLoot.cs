using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace SpaceEngineersVR.Scenarios.BugSwarm
{
    /// <summary>
    /// What a bug leaves when a player kills it: now and then a magazine for the gun that hurt it most, and from stingers
    /// and brutes now and then a better gun with two of its magazines. Brutes always leave something. It is never the basic
    /// pistol or its ammo, which the supplies container has without end in a run: a bug killed with that pistol leaves a
    /// better gun instead.
    /// </summary>
    internal sealed class SwarmLoot
    {
        private static readonly string[] Better = { "FullAutoPistolItem", "ElitePistolItem", "AutomaticRifleItem",
            "PreciseAutomaticRifleItem", "RapidFireAutomaticRifleItem", "UltimateAutomaticRifleItem" };
        private static readonly MyDefinitionId BasicAmmo = new MyDefinitionId(typeof(MyObjectBuilder_AmmoMagazine), "SemiAutoPistolMagazine");

        // per bug, the damage each magazine type did to it
        private readonly Dictionary<long, Dictionary<MyDefinitionId, float>> hurtBy = new Dictionary<long, Dictionary<MyDefinitionId, float>>();
        private readonly HashSet<long> dropped = new HashSet<long>();
        private readonly Random random = new Random();

        internal void Clear()
        {
            hurtBy.Clear();
            dropped.Clear();
        }

        /// <summary>A bug is about to take <paramref name="amount"/> damage from <paramref name="attacker"/>; it drops its loot if that kills it.</summary>
        internal void Hurt(IMyCharacter bug, long attacker, float amount, string kind)
        {
            MyDefinitionId magazine;
            if (dropped.Contains(bug.EntityId) || !Magazine(attacker, out magazine))
                return;
            Dictionary<MyDefinitionId, float> by;
            if (!hurtBy.TryGetValue(bug.EntityId, out by))
                hurtBy[bug.EntityId] = by = new Dictionary<MyDefinitionId, float>();
            float before;
            by.TryGetValue(magazine, out before);
            by[magazine] = before + amount;

            var health = ((MyCharacter)bug).StatComp?.Health;
            if (health == null || amount < health.Value)
                return;
            dropped.Add(bug.EntityId);
            Drop(bug, kind, by);
            hurtBy.Remove(bug.EntityId);
        }

        // the magazine of the hand weapon that did the damage: the gun itself, or the character holding it
        private static bool Magazine(long attacker, out MyDefinitionId magazine)
        {
            magazine = default(MyDefinitionId);
            IMyEntity e;
            if (!MyAPIGateway.Entities.TryGetEntityById(attacker, out e))
                return false;
            var gun = e as IMyHandheldGunObject<MyGunBase> ?? (e as MyCharacter)?.CurrentWeapon as IMyHandheldGunObject<MyGunBase>;
            if (gun?.GunBase == null || !gun.GunBase.HasAmmoMagazines)
                return false;
            magazine = gun.GunBase.CurrentAmmoMagazineId;
            return true;
        }

        private void Drop(IMyCharacter bug, string kind, Dictionary<MyDefinitionId, float> by)
        {
            MyDefinitionId most = default(MyDefinitionId);
            float mostDamage = -1;
            foreach (KeyValuePair<MyDefinitionId, float> hurt in by)
                if (hurt.Value > mostDamage)
                {
                    most = hurt.Key;
                    mostDamage = hurt.Value;
                }
            bool brute = kind == BugSwarmSession.BruteKind, stinger = kind == BugSwarmSession.StingerKind;
            MatrixD at = bug.WorldMatrix;
            bool basic = most == BasicAmmo;
            if (brute || random.NextDouble() < (stinger ? 0.4 : 0.3))
            {
                if (basic)
                    BetterGun(at);
                else
                    Spawn(most, brute ? 2 : 1, at, 0);
            }
            if (!basic && random.NextDouble() < (brute ? 0.25 : stinger ? 0.05 : 0))
                BetterGun(at);
        }

        private void BetterGun(MatrixD at)
        {
            var gun = new MyDefinitionId(typeof(MyObjectBuilder_PhysicalGunObject), Better[random.Next(Better.Length)]);
            Spawn(gun, 1, at, 1);
            MyDefinitionId ammo;
            if (GunMagazine(gun, out ammo))
                Spawn(ammo, 2, at, 2);
        }

        internal static bool GunMagazine(MyDefinitionId gun, out MyDefinitionId magazine)
        {
            magazine = default(MyDefinitionId);
            var item = Sandbox.Definitions.MyDefinitionManager.Static.TryGetPhysicalItemDefinition(gun) as Sandbox.Definitions.MyWeaponItemDefinition;
            if (item == null)
                return false;
            var weapon = Sandbox.Definitions.MyDefinitionManager.Static.GetWeaponDefinition(item.WeaponDefinitionId);
            if (weapon?.AmmoMagazinesId == null || weapon.AmmoMagazinesId.Length == 0)
                return false;
            magazine = weapon.AmmoMagazinesId[0];
            return true;
        }

        // a little above where the bug fell, the drops side by side so they do not spawn inside one another
        private static void Spawn(MyDefinitionId id, int amount, MatrixD at, int slot)
        {
            var content = MyObjectBuilderSerializer.CreateNewObject(id) as MyObjectBuilder_PhysicalObject;
            if (content == null)
                return;
            Vector3D position = at.Translation + at.Up * 0.6 + at.Right * (slot - 1) * 0.4;
            MyFloatingObjects.Spawn(new MyPhysicalInventoryItem((MyFixedPoint)amount, content), position, at.Forward, at.Up);
        }
    }
}
