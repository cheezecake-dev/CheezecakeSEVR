using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;
using VRageRender.Import;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Reloading a hand weapon (rifles, pistols, launchers) from the hands: the other hand brought to the weapon's magazine
    /// and its grip squeezed, or the right stick click while the weapon is held. Both only press the game's own RELOAD
    /// control (<see cref="Wanted"/>, through <see cref="VRControls"/>), the question R answers on the keyboard, so the game's
    /// own reload runs as it does for R: MyCharacter.UpdateAfterSimulation asks for RELOAD only while the weapon's CanReload()
    /// is true, then reloads it (or asks the server to) and plays the reload animation and sound. Its timing, its ammo rules
    /// and its refusals (already reloading, the magazine full, no magazines for it in the inventory, the weapon still being
    /// drawn, a screen open) stay the game's; a refused press does nothing, as R does, and the reason is logged.
    /// </summary>
    /// <remarks>
    /// <para>The magazine: the place the game's own model gives it. Every hand weapon (MyAutomaticRifleGun) carries its
    /// magazine as the subpart "magazine", put at the model's dummy "subpart_magazine" (MyAutomaticRifleGun.ResetMagazinePosition);
    /// the zone is that magazine's box at its rest place, grown by <see cref="MagazineReach"/>. The rest place, not the
    /// subpart's place now, because the reload animation carries the magazine to the left hand. A weapon whose model has no
    /// such dummy (a mod's) gets a zone of <see cref="ZoneRadius"/> under it, between the main grip and the game's left-hand
    /// spot.</para>
    /// <para>The grip: claimed through <see cref="GripClaim"/> at <see cref="ClaimPriority"/>, after the two-handed hold (a hand
    /// on the front grip is holding the weapon, not reloading it) and before the helmet, floating items, grab to use and
    /// hulls (a hand under the weapon is reaching for the weapon). A hand within <see cref="TwoHand.GrabReach"/> of the
    /// weapon's left-hand spot is never taken, whether or not two-handed holds are on. Nothing attaches: the claim only
    /// keeps the grip from also opening the tools wheel until it is let go.</para>
    /// <para>The button: B is the jetpack on foot, so the right stick click, which does nothing on foot otherwise (and with a
    /// hand pointing at a container, or on one, it is the Build Planner's production, or its deposit with the right grip held:
    /// <see cref="QuickInventory"/>).</para>
    /// </remarks>
    internal static class Reload
    {
        /// <summary>Debug: no reload from the hands, neither the gesture nor the button (<see cref="Rendering.RenderDebug"/> Reload=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: a log line every frame the other hand is near a held weapon's magazine (<see cref="Rendering.RenderDebug"/> ReloadTrace=1).</summary>
        public static bool Trace { get; set; }

        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string ClaimName = "Reload";

        /// <summary>After the two-handed hold (5): a hand on the front grip holds the weapon. Before the helmet (10): a hand at the magazine is at the weapon, not the head behind it.</summary>
        public const int ClaimPriority = 7;

        /// <summary>The reload button on foot and on the jetpack, while a hand weapon is held: the right stick click.</summary>
        internal const Hand ButtonHand = Hand.Right;
        internal const VRButtons Button = VRButtons.StickClick;

        /// <summary>A grip this close to the magazine's box (metres, from the other controller's grip) reloads: generous, the player cannot see under the weapon well.</summary>
        internal const double MagazineReach = 0.08;

        /// <summary>Without a magazine in the model: a grip this close to the zone's middle (metres) reloads.</summary>
        internal const double ZoneRadius = 0.10;

        /// <summary>The zone's middle without a magazine in the model: this far from the main grip toward the left-hand spot, and this far below the weapon (metres).</summary>
        private const float ZoneAlong = 0.4f, ZoneBelow = 0.06f;

        /// <summary>The reload starting: a firm tick on the gun hand, and on the other hand when it was the gesture.</summary>
        public const float GunHandAmplitude = 0.5f, GunHandSeconds = 0.04f;
        public const float OffHandAmplitude = 0.45f, OffHandSeconds = 0.03f;

        /// <summary>The trace logs the other hand this near the magazine (metres).</summary>
        private const double TraceNear = 0.25;

        /// <summary>A grip this near the magazine that is still out of reach is logged (metres beyond the reach).</summary>
        private const double DeclineNoted = 0.10;

        /// <summary>A press the game takes starts its reload in the same frame on a server; a client waits for the server this long (seconds).</summary>
        private const double StartWaitSeconds = 1.0;

        private static readonly long Second = Stopwatch.Frequency;

        /// <summary>Where the hands reach for the magazine: a box in its own frame in the world (a point for a zone), and how far from it counts.</summary>
        private struct Zone
        {
            public MatrixD World;
            public BoundingBox Box;
            public double Reach;
            public bool FromModel;
        }

        /// <summary>A press of the gesture or the button, waiting for the game's reload to start.</summary>
        private sealed class Ask
        {
            public string Source;
            public bool Gesture;
            public long At;
            public string Before;
            public IMyHandheldGunObject<MyDeviceBase> Gun;
        }

        private static bool hooked;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(Reload));
        private static int takeErrors;
        private static bool down;
        private static Ask asked;
        private static IMyHandheldGunObject<MyDeviceBase> watching, lastGun;
        private static bool lastReloading;
        private static string watchingSource;
        private static long watchingSince;
        private static readonly HashSet<string> described = new HashSet<string>();
        private static AccessTools.FieldRef<MyAutomaticRifleGun, int> equipDelay;
        private static long tracePlacesAt;
        private static readonly LineThrottle notes = new LineThrottle(6);

        // ---- Hooks ----

        /// <summary>Registers the grip claim and the check after the character's update, where the game reads RELOAD and starts its reload.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                try
                {
                    equipDelay = AccessTools.FieldRefAccess<MyAutomaticRifleGun, int>("m_weaponEquipDelay");
                }
                catch (Exception e)
                {
                    Log.Error(e, "Reload: the weapon's draw time could not be read; a refusal while drawing is logged without its reason");
                }
                GripClaim.Register(ClaimName, ClaimPriority, Take);
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(Reload), nameof(AfterSimulation)));
                hooked = true;
                Log.Info($"Reload: hooked (grip claim at priority {ClaimPriority}, {MagazineReach * 100:0} cm round the magazine; button: right stick click)");
            }
            catch (Exception e)
            {
                hooked = false;
                PatchRegistry.Lost(e, "Reload could not be hooked; hand weapons reload with R and on an empty magazine only");
                throw;
            }
        }

        // ---- The press ----

        /// <summary>
        /// Game thread, from <see cref="VRControls"/> each frame on foot and on the jetpack: the hands hold the game's RELOAD
        /// control now. The game reads its first frame as R pressed.
        /// </summary>
        internal static bool Wanted()
        {
            if (Off || !hooked || !guard.On)
            {
                down = false;
                return false;
            }
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                if (!HandTools.IsLocalOnFoot(character) || !TryGun(character, out IMyHandheldGunObject<MyDeviceBase> gun, out _))
                {
                    down = false;
                    return false;
                }
                // The click taken for the Build Planner's production or deposit (QuickInventory) stays out of reload until it is let go.
                bool button = VRInput.IsPressed(ButtonHand, Button) && !QuickInventory.TakesClick;
                bool gesture = GripClaim.Holds(TwoHand.OffHand, ClaimName);
                bool want = button || gesture;
                if (want && !down)
                    Pressed(character, gun, button, gesture);
                down = want;
                return want;
            }
            catch (Exception e)
            {
                down = false;
                guard.Failed(e, "Reload: the hands could not be read");
                return false;
            }
        }

        private static void Pressed(MyCharacter character, IMyHandheldGunObject<MyDeviceBase> gun, bool button, bool gesture)
        {
            string source = gesture && button ? "the gesture and the button" : gesture ? "the gesture" : "the button";
            string before = AmmoState(gun);
            string refused = Refusal(gun);
            if (refused != null)
            {
                asked = null;
                Log.Info($"Reload: {source} pressed RELOAD on the {Name(character)}; the game refuses: {refused} ({before})");
                return;
            }
            asked = new Ask { Source = source, Gesture = gesture, At = Stopwatch.GetTimestamp(), Before = before, Gun = gun };
            Log.Info($"Reload: {source} pressed RELOAD on the {Name(character)} ({before})");
        }

        /// <summary>Why the game will not reload now, read from the same checks as its CanReload(); null when it will.</summary>
        private static string Refusal(IMyHandheldGunObject<MyDeviceBase> gun)
        {
            if (gun.CanReload())
                return null;
            if (gun.IsReloading)
                return "already reloading";
            if (gun is MyAutomaticRifleGun rifle)
            {
                MyGunBase b = rifle.GunBase;
                if (b != null)
                {
                    if (!b.HasAmmoMagazines)
                        return "it takes no magazines";
                    if (b.IsAmmoFull())
                        return "the magazine is full";
                    if (!b.HasEnoughMagazines())
                        return "no magazines for it in the inventory";
                }
                if (equipDelay != null && MySandboxGame.TotalGamePlayTimeInMilliseconds <= equipDelay(rifle))
                    return "it is still being drawn";
            }
            if (!(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay) || MyScreenManager.IsAnyScreenOpening())
                return "a screen has the focus";
            return "its CanReload() says no";
        }

        /// <summary>The rounds in the magazine and the magazines in the inventory, for the log.</summary>
        private static string AmmoState(IMyHandheldGunObject<MyDeviceBase> gun)
        {
            try
            {
                if (!(gun is MyAutomaticRifleGun rifle) || rifle.GunBase == null)
                    return gun.IsReloading ? "reloading" : "not reloading";
                MyGunBase b = rifle.GunBase;
                int capacity = b.WeaponProperties?.AmmoMagazineDefinition?.Capacity ?? -1;
                return $"magazine {b.CurrentAmmo}/{capacity}, {b.GetInventoryAmmoMagazinesCount()} magazines in the inventory{(gun.IsReloading ? ", reloading" : string.Empty)}";
            }
            catch (Exception)
            {
                return "ammo unknown";
            }
        }

        // ---- The grip claim ----

        /// <summary>A grip has just started: it is ours when it is the other hand, at the held weapon's magazine and not on its front grip.</summary>
        private static bool Take(Hand hand)
        {
            if (Off || !hooked || hand != TwoHand.OffHand || takeErrors >= 3)
                return false;
            try
            {
                MyCharacter character = MySession.Static?.LocalCharacter;
                if (!VRInput.Active || !HandTools.IsLocalOnFoot(character) || ToolbarWheel.Owns
                    || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay)
                    || !TryGun(character, out _, out MyEntity weapon)
                    || !TryZone(weapon, character.HandItemDefinition, out Zone zone)
                    || !HandWorld.TryGetNow(character, hand, out MatrixD offGrip, out _, out _))
                    return false;
                Vector3D off = offGrip.Translation;
                double d = Distance(zone, off);
                double gap = LeftGap(character.HandItemDefinition, weapon, off);
                if (gap <= TwoHand.GrabReach)
                {
                    if (d <= zone.Reach)
                        Note($"Reload: {hand} grip on the {Name(character)}'s front grip ({gap * 100:0.0} cm from it, {d * 100:0.0} cm from the magazine): not a reload");
                    return false;
                }
                if (d > zone.Reach)
                {
                    if (d <= zone.Reach + DeclineNoted)
                        Note($"Reload: {hand} grip {d * 100:0.0} cm from the {Name(character)}'s magazine (reach {zone.Reach * 100:0} cm): not taken");
                    return false;
                }
                Note($"Reload: {hand} hand squeezed at the {Name(character)}'s magazine ({d * 100:0.0} cm from it, {Gap(gap)} from the front grip; {(zone.FromModel ? "the model's magazine" : "a zone")})");
                return true;
            }
            catch (Exception e)
            {
                if (takeErrors++ < 3)
                    Log.Error(e, "Reload could not decide on a grip");
                return false;
            }
        }

        // ---- Each frame ----

        /// <summary>Game thread, after the local character's update, in which the game reads RELOAD and starts its reload.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (!hooked || __instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                Step(__instance);
            }
            catch (Exception e)
            {
                asked = null;
                watching = null;
                guard.Failed(e, "Reload failed");
            }
        }

        private static void Step(MyCharacter character)
        {
            long now = Stopwatch.GetTimestamp();
            bool hasGun = TryGun(character, out IMyHandheldGunObject<MyDeviceBase> gun, out MyEntity weapon);
            if (!hasGun)
                gun = null;
            if (!ReferenceEquals(gun, lastGun))
            {
                lastGun = gun;
                lastReloading = gun != null && gun.IsReloading;
                if (gun != null)
                    Describe(character, weapon);
            }

            // The reload starting, however it was asked for (the hands, R, or the game on an empty magazine): a tick on the gun hand.
            bool reloading = gun != null && gun.IsReloading;
            if (reloading && !lastReloading)
            {
                bool ours = asked != null && ReferenceEquals(asked.Gun, gun);
                string source = ours ? asked.Source : "the game (R, or an empty magazine)";
                Haptics.Pulse(VRSettings.DominantHand, GunHandAmplitude, GunHandSeconds);
                if (ours && asked.Gesture)
                    Haptics.Pulse(TwoHand.OffHand, OffHandAmplitude, OffHandSeconds);
                Log.Info($"Reload: the game's reload of the {Name(character)} started, by {source} " +
                         $"({(ours ? asked.Before : "before: unknown")}; now {AmmoState(gun)}; takes {gun.GetReloadDuration():0.00} s)");
                watching = gun;
                watchingSource = source;
                watchingSince = now;
                asked = null;
            }
            lastReloading = reloading;

            if (asked != null && (now - asked.At > StartWaitSeconds * Second || !ReferenceEquals(asked.Gun, gun)))
            {
                Log.Info($"Reload: {asked.Source} pressed RELOAD and no reload started within {StartWaitSeconds:0.0} s ({(gun != null ? AmmoState(gun) : "the weapon was put away")})");
                asked = null;
            }

            if (watching != null)
            {
                if (!ReferenceEquals(watching, gun))
                {
                    Log.Info($"Reload: the reload started by {watchingSource} ended with the weapon put away or switched");
                    watching = null;
                }
                else if (!reloading)
                {
                    Log.Info($"Reload: done after {(now - watchingSince) / (double)Second:0.00} s ({AmmoState(gun)})");
                    watching = null;
                }
            }

            if (Trace && gun != null && !Off)
                TraceFrame(character, weapon);
        }

        /// <summary>Once per weapon kind: where its magazine is taken from, and how it sits against the two grips.</summary>
        private static void Describe(MyCharacter character, MyEntity weapon)
        {
            string name = Name(character);
            if (!described.Add(name))
                return;
            MyHandItemDefinition item = character.HandItemDefinition;
            if (!TryZone(weapon, item, out Zone zone))
            {
                Log.Info($"Reload: the {name} has no magazine or grips to reach for; the button only");
                return;
            }
            Vector3D centre = Vector3D.Transform((Vector3D)zone.Box.Center, zone.World);
            Vector3 size = zone.Box.Size * 100f;
            double toGrip = item != null ? Vector3D.Distance(centre, Vector3D.Transform((Vector3D)HeldItem.GraspInItem(item.RightHand, false), weapon.WorldMatrix)) : double.NaN;
            double toLeft = LeftGap(item, weapon, centre);
            Log.Info(zone.FromModel
                ? $"Reload: the {name}'s magazine is the model's (subpart \"{MyAutomaticRifleGun.NAME_SUBPART_MAGAZINE}\" at dummy \"{MyAutomaticRifleGun.NAME_DUMMY_MAGAZINE}\"), " +
                  $"box {size.X:0.0} x {size.Y:0.0} x {size.Z:0.0} cm, reach {zone.Reach * 100:0} cm; its middle is {toGrip * 100:0.0} cm from the main grip and {Gap(toLeft)} from the left-hand spot"
                : $"Reload: the {name}'s model has no magazine; a zone of {zone.Reach * 100:0} cm under it, {toGrip * 100:0.0} cm from the main grip and {Gap(toLeft)} from the left-hand spot");
        }

        private static void TraceFrame(MyCharacter character, MyEntity weapon)
        {
            Hand hand = TwoHand.OffHand;
            if (!TryZone(weapon, character.HandItemDefinition, out Zone zone))
                return;
            TracePlaces(character, weapon, zone);
            if (!HandWorld.TryGetNow(character, hand, out MatrixD offGrip, out _, out _))
                return;
            Vector3D off = offGrip.Translation;
            double d = Distance(zone, off);
            if (d > TraceNear)
                return;
            double gap = LeftGap(character.HandItemDefinition, weapon, off);
            string where = gap <= TwoHand.GrabReach ? "on the front grip" : d <= zone.Reach ? "in the zone" : "out of reach";
            Log.Info($"Reload trace: {hand} hand {d * 100:0.0} cm from the {Name(character)}'s magazine (reach {zone.Reach * 100:0}), {Gap(gap)} from the front grip: {where}; " +
                     $"grip {(VRInput.IsPressed(hand, VRButtons.Grip) ? "down" : "up")}{(GripClaim.Holds(hand, ClaimName) ? ", held for reload" : string.Empty)}; {AmmoState(character.CurrentWeapon)}");
        }

        /// <summary>
        /// Once a second with the trace on: the magazine's middle and the left-hand spot in the tracking space, for placing a
        /// fake other hand on them. The world is the tracking space turned and moved, so the gun hand's controller carries the
        /// one into the other.
        /// </summary>
        private static void TracePlaces(MyCharacter character, MyEntity weapon, in Zone zone)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - tracePlacesAt < Second)
                return;
            tracePlacesAt = now;
            HandState main = VRInput.Get(VRSettings.DominantHand);
            if (!main.Tracked || !HandWorld.TryGetNow(character, VRSettings.DominantHand, out MatrixD grip, out _, out _))
                return;
            MatrixD toTracking = MatrixD.Invert(grip) * (MatrixD)main.Grip;
            Vector3D magazine = Vector3D.Transform(Vector3D.Transform((Vector3D)zone.Box.Center, zone.World), toTracking);
            MyHandItemDefinition item = character.HandItemDefinition;
            string left = item != null && item.LeftHand.Translation.LengthSquared() > 1e-8f
                ? Format(Vector3D.Transform(Vector3D.Transform((Vector3D)HeldItem.GraspInItem(item.LeftHand, true), weapon.WorldMatrix), toTracking))
                : "none";
            Log.Info($"Reload trace: the {Name(character)}'s magazine middle at tracking {Format(magazine)} m, its left-hand spot at tracking {left} m");
        }

        private static string Format(Vector3D v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

        // ---- The weapon and its magazine ----

        /// <summary>A hand weapon the game can reload is in the character's hand.</summary>
        private static bool TryGun(MyCharacter character, out IMyHandheldGunObject<MyDeviceBase> gun, out MyEntity weapon)
        {
            gun = character?.CurrentWeapon;
            weapon = gun as MyEntity;
            return gun != null && weapon != null && !weapon.Closed && gun.Reloadable;
        }

        /// <summary>
        /// The magazine in the world: the model's magazine subpart's box at its rest place (the dummy the game puts it at), or,
        /// for a model without one, a zone under the weapon between the main grip and the left-hand spot.
        /// </summary>
        private static bool TryZone(MyEntity weapon, MyHandItemDefinition item, out Zone zone)
        {
            zone = default;
            MatrixD world = weapon.WorldMatrix;
            Dictionary<string, MyModelDummy> dummies = weapon.Model?.Dummies;
            if (dummies != null && dummies.TryGetValue(MyAutomaticRifleGun.NAME_DUMMY_MAGAZINE, out MyModelDummy dummy))
            {
                Matrix rest = Matrix.Normalize(dummy.Matrix);
                BoundingBox box = new BoundingBox(Vector3.Zero, Vector3.Zero);
                if (weapon.Subparts != null && weapon.Subparts.TryGetValue(MyAutomaticRifleGun.NAME_SUBPART_MAGAZINE, out MyEntitySubpart part) && part.Model != null)
                    box = part.Model.BoundingBox;
                bool hasBox = box.Size.LengthSquared() > 1e-6f;
                zone.World = (MatrixD)rest * world;
                zone.Box = hasBox ? box : new BoundingBox(Vector3.Zero, Vector3.Zero);
                zone.Reach = hasBox ? MagazineReach : ZoneRadius;
                zone.FromModel = true;
                return true;
            }
            if (item == null)
                return false;
            Vector3 right = HeldItem.GraspInItem(item.RightHand, false);
            Vector3 left = item.LeftHand.Translation.LengthSquared() > 1e-8f ? HeldItem.GraspInItem(item.LeftHand, true) : right + Vector3.Forward * 0.25f;
            Vector3 centre = Vector3.Lerp(right, left, ZoneAlong) + Vector3.Down * ZoneBelow;
            zone.World = MatrixD.CreateTranslation(centre) * world;
            zone.Box = new BoundingBox(Vector3.Zero, Vector3.Zero);
            zone.Reach = ZoneRadius;
            zone.FromModel = false;
            return true;
        }

        /// <summary>From a point in the world to the zone's box (0 inside it), metres.</summary>
        private static double Distance(in Zone zone, Vector3D point)
        {
            Vector3D local = Vector3D.Transform(point, MatrixD.Invert(zone.World));
            Vector3D nearest = new Vector3D(
                MathHelper.Clamp(local.X, zone.Box.Min.X, zone.Box.Max.X),
                MathHelper.Clamp(local.Y, zone.Box.Min.Y, zone.Box.Max.Y),
                MathHelper.Clamp(local.Z, zone.Box.Min.Z, zone.Box.Max.Z));
            return (local - nearest).Length();
        }

        /// <summary>From a point in the world to the weapon's left-hand spot (where the two-handed hold closes the other hand), or infinity for an item without one.</summary>
        private static double LeftGap(MyHandItemDefinition item, MyEntity weapon, Vector3D point)
        {
            if (item == null || !(item.LeftHand.Translation.LengthSquared() > 1e-8f))
                return double.PositiveInfinity;
            return Vector3D.Distance(point, Vector3D.Transform((Vector3D)HeldItem.GraspInItem(item.LeftHand, true), weapon.WorldMatrix));
        }

        // ---- Helpers ----

        private static string Gap(double metres) => double.IsInfinity(metres) ? "no left-hand spot, so far" : $"{metres * 100:0.0} cm";

        private static string Name(MyCharacter character) => character?.HandItemDefinition?.Id.SubtypeName ?? "weapon";

        private static void Note(string line) => notes.Info(line);
    }
}
