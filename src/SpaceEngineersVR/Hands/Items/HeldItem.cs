using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// world matrix is put so the spot the hand closes on (the middle of the hand held as the item definition's RightHand
    /// has the palm, mirrored for the left hand: <see cref="ItemSpots.ToolGrasp"/>) is the controller's grip and the weapon's forward is the hand's aim; the arm IK follows, as it takes its hand target from the weapon's
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
    internal static partial class HeldItem
    {
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetLogicalPosition = Setter(nameof(MyCharacterWeaponPositionComponent.LogicalPositionWorld));
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetLogicalOrientation = Setter(nameof(MyCharacterWeaponPositionComponent.LogicalOrientationWorld));
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetCrosshairPoint = Setter(nameof(MyCharacterWeaponPositionComponent.LogicalCrosshairPoint));
        private static readonly Action<MyCharacterWeaponPositionComponent, Vector3D> SetGraphicalPosition = Setter(nameof(MyCharacterWeaponPositionComponent.GraphicalPositionWorld));

        /// <summary>The game's own distance for the crosshair point (MyCharacterWeaponPositionComponent.UpdateLogicalWeaponPosition).</summary>
        private const double CrosshairDistance = 2000.0;

        private static int errors;

        /// <summary>Shots logged since the item in hand was taken: the first three are.</summary>
        private static int shootCalls;

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

        /// <summary>
        /// Replaces <c>GetHeadMatrix(...)</c> in UpdateShootDirection, whose forward is the straight shot of a gun: the hand's
        /// aim, or with the hand not tracked the head's, turned by the game's recoil as the hand's aim is (<see cref="Kickback.TurnHeadAim"/>).
        /// </summary>
        public static MatrixD HeadMatrix(MyCharacter character, bool includeY, bool includeX, bool forceHeadAnim, bool forceHeadBone, bool preferLocalOverSync)
        {
            if (HandTools.IsLocalOnFoot(character) && character.CurrentWeapon != null && HandTools.TryGetHand(out _, out MatrixD aim)
                && HandTools.ReachRay(character, aim, out Vector3D origin, out Vector3D direction))
                return HandTools.HeadLike(origin, direction, aim.Up);
            MatrixD head = character.GetHeadMatrix(includeY, includeX, forceHeadAnim, forceHeadBone, preferLocalOverSync);
            Kickback.TurnHeadAim(character, ref head);
            return head;
        }

        private static bool HoldsItem(IMyGunObject<MyDeviceBase> weapon)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            return HandTools.IsLocalOnFoot(character) && ReferenceEquals(character.CurrentWeapon, weapon);
        }

        /// <summary>
        /// Debug (<see cref="Rendering.RenderDebug"/> ToolGrasp=0): place the item by the game's palm bone spot (its RightHand
        /// translation, at the wrist) instead of the middle of the hand. The wrist is about 9 cm from the palm's middle, so
        /// the tool then sits that far out of the hand, toward the fingers.
        /// </summary>
        public static bool WristGrasp { get; set; }

        /// <summary>
        /// Where a hand closes on an item, in the item's own space: the middle of the closed hand
        /// (<see cref="Tracking.HandGrip.HandleInPalm"/>) through the palm bone's spot the item definition gives that hand
        /// (<paramref name="palmInItem"/>, its RightHand or LeftHand). That is where the controller's grip goes. The palm
        /// bone's own spot is the wrist, 9 cm from it; the item placed by the wrist sat out of the hand toward the fingers.
        /// </summary>
        internal static Vector3 GraspInItem(in Matrix palmInItem, bool left) =>
            WristGrasp ? palmInItem.Translation : Vector3.Transform(Tracking.HandGrip.HandleInPalm(left), palmInItem);

        /// <summary>
        /// The weapon's world matrix for a hand: forward and up from the hand's aim, and placed so the point where the
        /// hand holds the item (<paramref name="gripInItem"/>, <see cref="ItemSpots.ToolGrasp"/>, in the item's
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
                Place(__instance, null);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not place the held item");
            }
        }

        /// <summary>
        /// Game thread, as the arms are solved (<see cref="Tracking.HandIK"/>): the held item placed again, by the move the arms
        /// are solved by (<paramref name="trackingToModel"/>), so the hand on its grip and the controller are in one frame.
        /// False when it was not placed (no item in hand, hand tools off, the hand not tracked).
        /// </summary>
        internal static bool PlaceForArms(MyCharacter character, in MatrixD trackingToModel)
        {
            if (errors >= 3 || character?.WeaponPosition == null)
                return false;
            try
            {
                return Place(character.WeaponPosition, trackingToModel);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Hand tools could not place the held item for the arms");
                return false;
            }
        }

        /// <summary>
        /// The held item on the controller's grip: placed by <paramref name="frame"/> when given (<see cref="Tracking.HandWorld.TryGetIn"/>),
        /// else by this frame's head (<see cref="HandTools.TryGetHand(out MatrixD, out MatrixD)"/>).
        /// </summary>
        private static bool Place(MyCharacterWeaponPositionComponent __instance, MatrixD? frame)
        {
            {
                bool forArms = frame.HasValue;
                if (HandTools.Off)
                    return HeldTrace.On && HeldTrace.Refused(__instance.Character, forArms, "hand tools are off");
                MyCharacter character = __instance.Character;
                if (!HandTools.IsLocalOnFoot(character) || character.IsOnLadder)
                    return HeldTrace.On && HeldTrace.Refused(character, forArms, character?.IsOnLadder == true ? "on a ladder" : "not the local character on foot");
                MyHandItemDefinition item = character.HandItemDefinition;
                if (item == null || !(character.CurrentWeapon is MyEntity weapon))
                    return HeldTrace.On && HeldTrace.Refused(character, forArms, "no hand item");
                // A gun in the hand needs its muzzle checked against walls (MuzzleShift). Once those checks have given up, the
                // game holds the gun its own way, so a barrel can never be pushed through a wall to fire from behind it.
                if (Reach.GivenUp && weapon is IMyHandheldGunObject<MyGunBase>)
                    return HeldTrace.On && HeldTrace.Refused(character, forArms, "the line checks gave up: the game holds the gun");
                MatrixD grip, aim;
                bool got = frame.HasValue ? HandTools.TryGetHand(frame.Value, out grip, out aim) : HandTools.TryGetHand(out grip, out aim);
                if (!got)
                    return HeldTrace.On && HeldTrace.Refused(character, forArms, "no hand (not tracked, or the view refused it)");
                if (!HandTools.ReachRay(character, aim, out Vector3D origin, out Vector3D forward))
                    return HeldTrace.On && HeldTrace.Refused(character, forArms, "no ray from the aim");

                // What the sensors read first, then the weapon: moving the weapon makes the tools refresh their sensor too.
                SetLogicalPosition(__instance, origin);
                SetLogicalOrientation(__instance, forward);
                SetCrosshairPoint(__instance, origin + forward * CrosshairDistance);
                MatrixD world = WeaponWorld(grip, aim, ItemSpots.ToolGrasp(item));
                if (weapon is IMyHandheldGunObject<MyGunBase> gun && gun.GunBase != null)
                    world.Translation += MuzzleShift(character, gun.GunBase, world);
                NotePlacement(item, world, grip);
                weapon.WorldMatrix = world;
                SetGraphicalPosition(__instance, world.Translation);

                if (weapon is MyEngineerToolBase tool)
                    tool.UpdateSensorPosition();
                else if (weapon is MyHandDrill drill)
                    drill.WorldPositionChanged(null);
                if (HeldTrace.On)
                    HeldTrace.Placed(character, forArms, item, weapon, grip, aim, world);
                return true;
            }
        }

        /// <summary>
        /// How far to move a gun placed at <paramref name="world"/> so its muzzle is on the near side of anything solid
        /// between the head and it (<see cref="Reach.Unblocked"/>): the shot starts at the muzzle, and a barrel pushed into
        /// a wall would fire from behind it. Zero when the way is clear; a way that could not be checked puts the muzzle at the
        /// head bone (<see cref="Reach.Unblocked"/>). Once the line checks have given up (<see cref="Reach.GivenUp"/>) a gun is
        /// not placed in the hand at all (<see cref="Place"/>), so this is never asked then.
        /// </summary>
        private static Vector3D MuzzleShift(MyCharacter character, MyGunBase gun, in MatrixD world)
        {
            Vector3D muzzle = Vector3D.Transform(gun.GetMuzzleLocalPosition(), world);
            return Reach.Unblocked(character, muzzle) - muzzle;
        }

        private static MyHandItemDefinition noted;
        private static bool notedWrist, notedMirrored;
        private static long notedAt;

        /// <summary>
        /// Once per item taken in hand: where it sits on the controller's grip, for the log. Called each frame it is held, so
        /// a gap of half a second means it was put away and taken out again, which counts as taken in hand anew.
        /// </summary>
        private static void NotePlacement(MyHandItemDefinition item, in MatrixD world, in MatrixD grip)
        {
            long now = Stopwatch.GetTimestamp(), last = notedAt;
            notedAt = now;
            if (ReferenceEquals(item, noted) && notedWrist == WristGrasp && notedMirrored == ItemSpots.Mirrored
                && now - last < Stopwatch.Frequency / 2)
                return;
            noted = item;
            shootCalls = 0;
            notedWrist = WristGrasp;
            notedMirrored = ItemSpots.Mirrored;
            Vector3D wrist = Vector3D.Transform((Vector3D)ItemSpots.Tool(item).Translation, world);
            // The cube placer (the build and colour tools) is a type with no subtype.
            string name = string.IsNullOrEmpty(item.Id.SubtypeName) ? item.Id.TypeId.ToString().Replace("MyObjectBuilder_", "") : item.Id.SubtypeName;
            Log.Info($"Hand tools: the {name} is held in the {ItemSpots.ToolHand.ToString().ToLowerInvariant()} hand " +
                     $"{(WristGrasp ? "by the game's wrist spot (ToolGrasp=0)" : "by the middle of the hand")}{(ItemSpots.Mirrored ? ", the game's spots mirrored" : "")}; " +
                     $"the astronaut's wrist is {Vector3D.Distance(wrist, grip.Translation) * 100:0.0} cm from the controller's grip");
        }

        private static Action<MyCharacterWeaponPositionComponent, Vector3D> Setter(string property) =>
            (Action<MyCharacterWeaponPositionComponent, Vector3D>)Delegate.CreateDelegate(typeof(Action<MyCharacterWeaponPositionComponent, Vector3D>),
                AccessTools.PropertySetter(typeof(MyCharacterWeaponPositionComponent), property)
                ?? throw new MissingMemberException(nameof(MyCharacterWeaponPositionComponent), property));
    }
}
