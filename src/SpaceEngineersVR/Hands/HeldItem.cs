using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The tool or gun the character holds, in the tracked dominant hand, pointing where the hand points. The weapon's
    /// world matrix is put so the spot the game holds it by (the item definition's RightHand) is the controller's grip
    /// and the weapon's forward is the hand's aim; the arm IK follows, as it takes its hand target from the weapon's
    /// matrix. The weapon position component's logical position and orientation (what the tool sensors read) are the
    /// hand's, and the sensors are refreshed, so a welder, grinder or drill works on what the hand points at.
    /// </summary>
    /// <remarks>
    /// The direction that is synced to other players is the one the game sends when it starts a shot
    /// (MyCharacter.UpdateShootDirection): it computes it from the head, and this swaps the head's answer for the hand's
    /// inside that method, so the game's own sync, timing and cooldown are untouched. Other players see a tool where
    /// their own animation puts it, shooting the way the hand points; where a tool's sensor starts is not synced (the
    /// server uses the character's head position for it with the synced direction).
    /// The hand is placed with <see cref="Tracking.HandWorld"/>, which in the update phase of a frame uses the previous
    /// frame's camera: while the character itself moves fast the item trails the hand by one frame of that movement.
    /// </remarks>
    internal static class HeldItem
    {
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetLogicalPosition = Setter(nameof(MyCharacterWeaponPositionComponent.LogicalPositionWorld));
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetLogicalOrientation = Setter(nameof(MyCharacterWeaponPositionComponent.LogicalOrientationWorld));
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetCrosshairPoint = Setter(nameof(MyCharacterWeaponPositionComponent.LogicalCrosshairPoint));
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetGraphicalPosition = Setter(nameof(MyCharacterWeaponPositionComponent.GraphicalPositionWorld));

        /// <summary>The game's own distance for the crosshair point (MyCharacterWeaponPositionComponent.UpdateLogicalWeaponPosition).</summary>
        private const double CrosshairDistance = 2000.0;

        private static int errors, shootCalls;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyCharacterWeaponPositionComponent), nameof(MyCharacterWeaponPositionComponent.Update), new[] { typeof(bool) }),
                postfix: new HarmonyMethod(typeof(HeldItem), nameof(AfterUpdate)));

            harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateShootDirection)),
                transpiler: new HarmonyMethod(typeof(HeldItem), nameof(TranspileShootDirection)));
            Log.Info("Hand tools: tools and guns in the hand hooked (shoot direction calls rewritten: " + shootRewrites + ")");
        }

        private static int shootRewrites;

        /// <summary>
        /// The game's shoot direction is "from the weapon to the head's aimed point", or for guns the head's forward. Both
        /// calls give the hand's direction instead while the hand holds the item.
        /// </summary>
        private static IEnumerable<CodeInstruction> TranspileShootDirection(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            IEnumerable<CodeInstruction> result = HandTools.Redirect(instructions,
                method => method.Name == nameof(IMyGunObject<MyDeviceBase>.DirectionToTarget) && method.DeclaringType != null
                          && method.DeclaringType.IsGenericType && method.DeclaringType.GetGenericTypeDefinition() == typeof(IMyGunObject<>),
                AccessTools.Method(typeof(HeldItem), nameof(DirectionToTarget)), 1, ref count);
            result = HandTools.Redirect(result, HandTools.IsCharacterHeadMatrix, AccessTools.Method(typeof(HeldItem), nameof(HeadMatrix)), 1, ref count);
            shootRewrites = count;
            return result;
        }

        /// <summary>Replaces <c>weapon.DirectionToTarget(aimedPoint)</c> in UpdateShootDirection.</summary>
        public static Vector3 DirectionToTarget(IMyGunObject<MyDeviceBase> weapon, Vector3D target)
        {
            if (HoldsItem(weapon) && HandTools.TryGetRay(out _, out Vector3D direction))
            {
                if (shootCalls++ < 3)
                    Log.Info($"Hand tools: shot direction from the hand ({direction.X:F3}, {direction.Y:F3}, {direction.Z:F3})");
                return (Vector3)direction;
            }
            return weapon.DirectionToTarget(target);
        }

        /// <summary>Replaces <c>GetHeadMatrix(...)</c> in UpdateShootDirection, whose forward is the straight shot of a gun.</summary>
        public static MatrixD HeadMatrix(MyCharacter character, bool includeY, bool includeX, bool forceHeadAnim, bool forceHeadBone, bool preferLocalOverSync)
        {
            if (HandTools.IsLocalOnFoot(character) && character.CurrentWeapon != null && HandTools.TryGetHand(out _, out MatrixD aim)
                && HandTools.Ray(aim, out Vector3D origin, out Vector3D direction))
                return HandTools.HeadLike(origin, direction, aim.Up);
            return character.GetHeadMatrix(includeY, includeX, forceHeadAnim, forceHeadBone, preferLocalOverSync);
        }

        private static bool HoldsItem(IMyGunObject<MyDeviceBase> weapon)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            return HandTools.IsLocalOnFoot(character) && ReferenceEquals(character.CurrentWeapon, weapon);
        }

        /// <summary>
        /// The weapon's world matrix for a hand: forward and up from the hand's aim, and placed so the point where the
        /// game holds the item (<paramref name="gripInItem"/>, the item definition's RightHand translation, in the item's
        /// own space) is at the controller's grip.
        /// </summary>
        internal static MatrixD WeaponWorld(in MatrixD grip, in MatrixD aim, Vector3 gripInItem)
        {
            Vector3D forward = Vector3D.Normalize(aim.Forward);
            Vector3D up = Vector3D.Normalize(Vector3D.Reject(aim.Up, forward));
            MatrixD world = MatrixD.CreateWorld(Vector3D.Zero, forward, up);
            world.Translation = grip.Translation - Vector3D.TransformNormal((Vector3D)gripInItem, world);
            return world;
        }

        private static void AfterUpdate(MyCharacterWeaponPositionComponent __instance)
        {
            try
            {
                if (HandTools.Off)
                    return;
                MyCharacter character = __instance.Character;
                if (!HandTools.IsLocalOnFoot(character) || character.IsOnLadder)
                    return;
                MyHandItemDefinition item = character.HandItemDefinition;
                if (item == null || !(character.CurrentWeapon is MyEntity weapon))
                    return;
                if (!HandTools.TryGetHand(out MatrixD grip, out MatrixD aim) || !HandTools.Ray(aim, out Vector3D origin, out Vector3D forward))
                    return;

                // What the sensors read first, then the weapon: moving the weapon makes the tools refresh their sensor too.
                SetLogicalPosition(__instance, origin);
                SetLogicalOrientation(__instance, forward);
                SetCrosshairPoint(__instance, origin + forward * CrosshairDistance);
                MatrixD world = WeaponWorld(grip, aim, item.RightHand.Translation);
                weapon.WorldMatrix = world;
                SetGraphicalPosition(__instance, world.Translation);

                if (weapon is MyEngineerToolBase tool)
                    tool.UpdateSensorPosition();
                else if (weapon is MyHandDrill drill)
                    drill.WorldPositionChanged(null);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not place the held item");
            }
        }

        private static Action<MyCharacterWeaponPositionComponent, Vector3D> Setter(string property) =>
            (Action<MyCharacterWeaponPositionComponent, Vector3D>)Delegate.CreateDelegate(typeof(Action<MyCharacterWeaponPositionComponent, Vector3D>),
                AccessTools.PropertySetter(typeof(MyCharacterWeaponPositionComponent), property)
                ?? throw new MissingMemberException(nameof(MyCharacterWeaponPositionComponent), property));
    }
}
