using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Library.Utils;
using VRageMath;
using VRageRender.Animations;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The physical reload (<see cref="VRSettings.PhysicalReload"/>): the reload press drops the magazine, the other hand takes
    /// a fresh one from its pouch with a pull of its trigger, holds it while the trigger is held, and pushes it up into the
    /// magazine well, and that is when the weapon is loaded.
    /// </summary>
    /// <remarks>
    /// <para>The game's reload stays the one that counts. Its start (MyAutomaticRifleGun.Reload) empties the magazine, the
    /// rounds left in it lost (MyGunBase.EmptyMagazine), so that is the magazine dropping; its end (UpdateBeforeSimulation10,
    /// once m_reloadEndTime has passed) takes one magazine from the inventory and fills the weapon (ConsumeMagazine), so that is
    /// the magazine seating. Between the two the end time is kept ahead of the clock, and seating sets it to now, or to the
    /// time the game's own reload would have ended if that is later (<see cref="reloadFloor"/>): by hand the weapon is never
    /// loaded sooner than the reload time, on a host or in single player any more than for another player. Partial
    /// rounds, the inventory count, infinite ammo and the reload refusals are therefore the game's own. Only a server owns the
    /// end time: as a client the drop and the seat are shown, and the weapon is loaded when the server's timer ends.</para>
    /// <para>Which reloads go this way: the reload press of the hands (<see cref="Wanted"/>). A magazine fired empty stays in the
    /// well until that press drops it: the game's own reload of it, which MyCharacter.UpdateShooting starts by itself as the
    /// last round is fired, is held back (<see cref="HoldEmptyMagazine"/>). That reload runs on the server, so as a client the
    /// empty magazine drops when the server starts it. R with rounds left stays the game's timed
    /// reload, as on the desktop.</para>
    /// <para>The well is the model's own: the subpart "magazine" at the dummy "subpart_magazine" (as <see cref="TryZone"/>),
    /// pushed in along the weapon's up. Every hand weapon's model has it; the magazine models are the ammo magazine items' own
    /// models, so the one in the hand is the one that seats.</para>
    /// <para>The pouch: the astronaut's hip on the other hand's side (<see cref="TryHip"/>), within <see cref="HipReach"/>; or, so a
    /// player sitting down, whose lap is in the way of that hip, reaches it too, anywhere <see cref="PouchBelowEyes"/> or more
    /// below the eyes and not at the weapon (its magazine or its front grip). The trigger takes it anywhere there; the grip
    /// only at the hip, as it is also the hand's grab of hulls, ladders, items and the helmet, whose places can be below the
    /// eyes. Either held keeps it in the hand, whose fingers close round it (<see cref="HoldsMagazine"/>). The grip is claimed
    /// only at the hip and while a magazine is held (<see cref="PouchGripName"/>), so a grip elsewhere still grabs or opens
    /// the tools wheel; the trigger does nothing else while a hand weapon is held, so a pull that misses costs nothing.</para>
    /// <para>The magazine in the hand is the well's own model (the subpart that seats), drawn in the drawn fist
    /// (<see cref="HandDrawn"/>); near the well it is drawn lining up with it (<see cref="Guided"/>).</para>
    /// <para>The sound: each weapon has one reload cue (its ReloadSoundName; there is no separate magazine out or in), which the
    /// game plays as its reload starts (MyCharacter.PlayReloadAnimation). For a physical reload it is held back there and played
    /// as the magazine seats, on this machine; other players hear it at the start, as they see the game's animation.</para>
    /// </remarks>
    internal static partial class Reload
    {
        // ---- Tuning: all of the physical reload's places, reaches and timings ----

        /// <summary>The magazine pouch: from the thigh joint this far out to the side, forward and up (metres).</summary>
        private const float HipOut = 0.07f, HipForward = 0.03f, HipUp = 0.0f;

        /// <summary>Without the thigh bones: the hip this high above the feet and this far out from the middle (metres).</summary>
        private const float HipHeightFallback = 0.95f, HipSideFallback = 0.17f;

        /// <summary>A pull this close to the hip's pouch takes a magazine (metres): generous, the hand reaches there unseen.</summary>
        internal const double HipReach = 0.18;

        /// <summary>A pull this far or more below the eyes (metres, along the body's up), away from the weapon, takes one too: belt height, a lap seated.</summary>
        internal const double PouchBelowEyes = 0.45;

        /// <summary>The other hand's trigger, pulled at the pouch: a magazine in the hand while it is held.</summary>
        private static readonly Pull pouchPull = new Pull();

        /// <summary>
        /// The magazine's top this close to the line it slides in along seats it (metres, across the line): generous, the
        /// player cannot see under the weapon, and the guide (<see cref="GuideReach"/>) has lined the drawn magazine up already.
        /// </summary>
        internal const double SeatReach = 0.07;

        /// <summary>The magazine at most this far off the well's axis seats (degrees): a fist held as it comes up under the weapon, not pointed straight up.</summary>
        internal const double SeatCone = 55.0;

        /// <summary>How far below the seated place the line it slides in along starts: this share of the magazine's length.</summary>
        private const double SeatDepth = 1.0;

        /// <summary>
        /// The guide: a magazine in the hand whose top is within this of the well's line (metres), from <see cref="GuideDepth"/>
        /// lengths below its seated place, and within <see cref="GuideCone"/> of its axis, is drawn turned and moved toward the
        /// well, more the nearer it is, up to <see cref="GuideShare"/> of the way: it is seen lining up before it seats.
        /// </summary>
        internal const double GuideReach = 0.16, GuideCone = 80.0, GuideDepth = 2.0;

        private const float GuideShare = 0.5f;

        /// <summary>The magazine entering the guide: a light tick on the other hand.</summary>
        public const float GuideAmplitude = 0.12f, GuideSeconds = 0.015f;

        /// <summary>The other hand's fingers closed round a magazine it holds, as a share of closed round the controller's handle (fingers, thumb).</summary>
        internal const float MagazineCurl = 0.8f, MagazineThumb = 0.85f;

        /// <summary>The name the other hand's grip is claimed under at the hip, and while it holds a magazine (<see cref="GripClaim"/>).</summary>
        public const string PouchGripName = "Reload pouch";

        /// <summary>Where the fist holds the magazine: this share of its length from its bottom.</summary>
        private const float HoldAt = 0.35f;

        /// <summary>The slide into the well, seconds.</summary>
        private const double SlideSeconds = 0.09;

        /// <summary>A dropped magazine: out of the well at this speed (m/s), turning this fast (rad/s), gone after this long, fading over the last part (seconds).</summary>
        private const double DropSpeed = 0.8, DropSpin = 3.0, DropSeconds = 1.0, DropFade = 0.35;

        /// <summary>A magazine taken: a tick on the other hand. None to take: two lighter ones this far apart.</summary>
        public const float TakeAmplitude = 0.35f, TakeSeconds = 0.025f;
        public const float NoneAmplitude = 0.25f, NoneSeconds = 0.02f;
        private const double NoneGap = 0.09;

        /// <summary>Seated: a click on both hands.</summary>
        public const float SeatAmplitude = 0.6f, SeatSeconds = 0.035f;

        /// <summary>A magazine let go of at the pouch goes back: the lightest tick.</summary>
        public const float StowAmplitude = 0.15f, StowSeconds = 0.015f;

        // ---- State (game thread) ----

        private enum Well
        {
            /// <summary>A magazine is in the well, or this is not a physical reload.</summary>
            In,
            /// <summary>The magazine dropped; the game's reload is held until one is seated.</summary>
            Out,
            /// <summary>A magazine sliding in; the game's reload has been let finish.</summary>
            Seating,
        }

        private static Well well;
        private static MyAutomaticRifleGun wellGun;
        private static bool holdingTimer;
        private static long outAt;

        /// <summary>The other hand's trigger holds a pull at the pouch: with a magazine in it (<see cref="inHand"/>), or none to take.</summary>
        private static bool hipHeld, inHand;
        private static MyAutomaticRifleGun handGun;
        private static Vector3D handVelocity, lastHandPos;
        private static long lastHandAt;

        /// <summary>The slide: from the hand's pose to the well's, both in the weapon's own space.</summary>
        private static Matrix slideFrom;
        private static long slideAt;

        /// <summary>The second tick of "none to take", at this time; 0 when none is due.</summary>
        private static long noneTickAt;

        /// <summary>The game's own move of the magazine to the left hand is left out until this time (its reload animation's events).</summary>
        private static long noRealignUntil;

        private static int lastAmmo = -1;

        /// <summary>
        /// The held weapon once it has been seen with rounds in its magazine since it was drawn: only a magazine fired empty in it
        /// stays in its well for the hands to drop (<see cref="NeedsOwnReload"/>). One drawn empty is the game's to load, on its
        /// own timer, as it does on the desktop.
        /// </summary>
        private static MyAutomaticRifleGun loadedGun;

        /// <summary>The held weapon drawn empty with no magazine for it in the inventory: its well is shown empty.</summary>
        private static MyAutomaticRifleGun bareGun;

        /// <summary>
        /// The rounds in the magazine as the game's reload of <see cref="reloadingGun"/> began (MyAutomaticRifleGun.Reload, on a
        /// server): the last shot and the reload it starts come in the same character update, so the frame before still had one.
        /// </summary>
        private static int ammoAtReload = -1;
        private static MyAutomaticRifleGun reloadingGun;
        private static bool seatSound;
        private static int placedFrame = -1;
        private static AccessTools.FieldRef<MyAutomaticRifleGun, MyTimeSpan> reloadEnd;

        /// <summary>
        /// The end time the game's own reload set as it began (MyAutomaticRifleGun.Reload: the clock then plus the weapon's reload
        /// time), read before the hold moves it. A magazine seated earlier than this loads the weapon at this time, not before.
        /// </summary>
        private static MyTimeSpan reloadFloor;

        /// <summary>The held weapon's magazine is out of its well for a physical reload, or a fresh one is sliding in: the other hand is busy with it.</summary>
        internal static bool MagazineOut => well != Well.In;

        private static bool PhysicalOn => VRSettings.PhysicalReload && !Off && hooked && VRInput.Active;

        // ---- Hooks ----

        private static void PatchMagazine(Harmony harmony)
        {
            try
            {
                reloadEnd = AccessTools.FieldRefAccess<MyAutomaticRifleGun, MyTimeSpan>("m_reloadEndTime");
            }
            catch (Exception e)
            {
                Log.Error(e, "Reload: the reload's end time could not be read; a physical reload is shown, and the weapon loads on the game's timer");
            }
            try
            {
                // An explicit interface method: found through the interface map rather than by its compiler-made name.
                InterfaceMapping map = typeof(MyAutomaticRifleGun).GetInterfaceMap(typeof(IMyHandheldGunObject<MyGunBase>));
                int i = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IMyHandheldGunObject<MyGunBase>.PlayReloadSound));
                if (i < 0)
                    throw new MissingMethodException(nameof(MyAutomaticRifleGun), nameof(IMyHandheldGunObject<MyGunBase>.PlayReloadSound));
                harmony.Patch(map.TargetMethods[i], prefix: new HarmonyMethod(typeof(Reload), nameof(BeforeReloadSound)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Reload: the reload sound could not be hooked; it plays as the magazine drops, not as it seats");
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyAutomaticRifleGun), nameof(MyAutomaticRifleGun.Reload)),
                    prefix: new HarmonyMethod(typeof(Reload), nameof(BeforeRifleReload)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Reload: the reload's start could not be hooked; a magazine emptied by firing may reload on the game's timer");
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), "UpdateShooting"),
                    transpiler: new HarmonyMethod(typeof(Reload), nameof(HoldEmptyMagazine)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Reload: the game's reload of a magazine fired empty could not be held back; it drops by itself");
            }
            GripClaim.Register(PouchGripName, GripPriority.ReloadPouch, hand => hand == TwoHand.OffHand && (inHand || TakeMagazine(hand, "grip closed", hipOnly: true)));
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), "RealignMagazineWithHand"),
                    prefix: new HarmonyMethod(typeof(Reload), nameof(BeforeRealign)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Reload: the game's magazine-to-hand move could not be hooked; during a physical reload the well's magazine may follow the left hand");
            }
        }

        /// <summary>MyCharacter.RealignMagazineWithHand, during the reload animation: the magazine is the hands' during a physical reload.</summary>
        private static bool BeforeRealign(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter)
                return true;
            return well == Well.In && System.Diagnostics.Stopwatch.GetTimestamp() >= noRealignUntil;
        }

        /// <summary>
        /// The weapon's reload sound, played by the game as its reload starts: held back when the reload is a physical one, by the
        /// same test as <see cref="ReloadStarted"/> (the hands' press, or an empty magazine), and played at the seat instead.
        /// </summary>
        private static void BeforeRifleReload(MyAutomaticRifleGun __instance)
        {
            if (__instance.GunBase == null || __instance.Owner == null || __instance.Owner != MySession.Static?.LocalCharacter)
                return;
            reloadingGun = __instance;
            ammoAtReload = __instance.GunBase.CurrentAmmo;
        }

        /// <summary>
        /// MyCharacter.UpdateShooting reloads a weapon fired empty by itself (on the server: CanReload, NeedsReload, then Reload and
        /// the reload animation). Its one read of NeedsReload goes through <see cref="NeedsOwnReload"/> instead.
        /// </summary>
        private static IEnumerable<CodeInstruction> HoldEmptyMagazine(IEnumerable<CodeInstruction> code)
        {
            MethodInfo needs = AccessTools.PropertyGetter(typeof(IMyHandheldGunObject<MyDeviceBase>), nameof(IMyHandheldGunObject<MyDeviceBase>.NeedsReload));
            MethodInfo own = AccessTools.Method(typeof(Reload), nameof(NeedsOwnReload));
            List<CodeInstruction> list = new List<CodeInstruction>(code);
            int found = 0;
            foreach (CodeInstruction instruction in list)
            {
                if (!instruction.Calls(needs))
                    continue;
                instruction.opcode = OpCodes.Call;
                instruction.operand = own;
                found++;
            }
            if (found != 1)
                throw new InvalidOperationException($"MyCharacter.UpdateShooting reads NeedsReload {found} times, not once");
            return list;
        }

        /// <summary>The game is to reload the weapon by itself: not one of ours fired empty with reload by hand on, whose magazine stays in until the reload press.</summary>
        private static bool NeedsOwnReload(IMyHandheldGunObject<MyDeviceBase> gun)
        {
            if (!gun.NeedsReload)
            {
                heldEmpty = null;
                return false;
            }
            try
            {
                if (!PhysicalOn || !(gun is MyAutomaticRifleGun rifle) || rifle.Owner == null || rifle.Owner != MySession.Static?.LocalCharacter
                    || !ReferenceEquals(loadedGun, rifle) || rifle.GunBase == null || !rifle.GunBase.HasAmmoMagazines || !TryWell(rifle, out _, out _, out _))
                    return true;
                if (!ReferenceEquals(heldEmpty, rifle))
                {
                    heldEmpty = rifle;
                    Log.Info($"Reload: the {Name(rifle.Owner)}'s magazine is empty; it stays in until the reload press drops it");
                }
                return false;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Reload: holding an empty magazine in failed");
                return true;
            }
        }

        /// <summary>The weapon whose empty magazine <see cref="NeedsOwnReload"/> is holding in, for its one log line.</summary>
        private static MyAutomaticRifleGun heldEmpty;

        /// <summary>The game's reload of <paramref name="gun"/> began with its magazine fired empty: not R with rounds left, nor a weapon drawn empty.</summary>
        private static bool EmptiedBy(MyAutomaticRifleGun gun) =>
            ReferenceEquals(loadedGun, gun) && (ReferenceEquals(reloadingGun, gun) ? ammoAtReload == 0 : lastAmmo == 0 && ReferenceEquals(lastGun, gun));

        private static bool BeforeReloadSound(MyAutomaticRifleGun __instance)
        {
            if (seatSound || !PhysicalOn || __instance.Owner == null || __instance.Owner != MySession.Static?.LocalCharacter)
                return true;
            bool physical = ReferenceEquals(wellGun, __instance)
                            || (asked != null && ReferenceEquals(asked.Gun, __instance))
                            || EmptiedBy(__instance);
            return !(physical && __instance.GunBase != null && __instance.GunBase.HasAmmoMagazines && TryWell(__instance, out _, out _, out _));
        }

        /// <summary>Game thread, from <see cref="HandIK"/> once the held weapon is placed in the arms' frame: the magazines placed in the same frame.</summary>
        internal static void ToolPlaced(MyCharacter character, in MatrixD trackingToModel)
        {
            if (!hooked || !guard.On || (well == Well.In && !inHand && Shapes.Idle))
                return;
            try
            {
                Hand off = TwoHand.OffHand;
                bool hand = HandWorld.TryGetIn(character, off, trackingToModel, out MatrixD grip, out _);
                PlaceShapes(character, hand, grip);
                placedFrame = HandIK.Frame;
            }
            catch (Exception e)
            {
                ResetMagazine("placing the magazine failed");
                guard.Failed(e, "Reload: the magazine could not be placed");
            }
        }

        // ---- The magazine out ----

        /// <summary>The game's reload of the held weapon just started: a physical one drops the magazine.</summary>
        private static void ReloadStarted(MyCharacter character, IMyHandheldGunObject<MyDeviceBase> gun, bool ours)
        {
            if (!PhysicalOn || !(gun is MyAutomaticRifleGun rifle) || rifle.GunBase == null || !rifle.GunBase.HasAmmoMagazines)
                return;
            bool empty = EmptiedBy(rifle);
            int before = ReferenceEquals(reloadingGun, rifle) ? ammoAtReload : lastAmmo;
            reloadingGun = null;
            if (!ours && !empty)
            {
                Log.Info(ReferenceEquals(loadedGun, rifle)
                    ? $"Reload: the {Name(character)}'s reload with {before} rounds left is the game's (R): timed, not by hand"
                    : $"Reload: the {Name(character)} was drawn with its magazine empty; the game loads it, timed, not by hand");
                return;
            }
            if (!TryWell(rifle, out MatrixD rest, out _, out _))
            {
                Log.Info($"Reload: the {Name(character)} has no magazine well in its model; its reload is the game's, timed");
                return;
            }
            well = Well.Out;
            wellGun = rifle;
            outAt = System.Diagnostics.Stopwatch.GetTimestamp();
            holdingTimer = Sandbox.Game.Multiplayer.Sync.IsServer && reloadEnd != null;
            if (holdingTimer)
                reloadFloor = reloadEnd(rifle);
            HoldTimer(rifle);
            Shapes.HideWell(rifle);
            Shapes.Drop(WellModel(rifle), rest, character, -rest.Up * DropSpeed, rest.Right);
            TwoHand.LetGoFor("the magazine came out to reload");
            Log.Info($"Reload: the {Name(character)}'s magazine dropped ({(ours ? "the reload press" : "empty")}, {(before > 0 ? $"{before} rounds in it lost, as the game's reload does" : "empty")}); " +
                     (holdingTimer ? "the reload waits for a magazine pushed into the well" : "a client: the server's timer loads it, the well is shown by hand"));
        }

        // ---- Each frame ----

        /// <summary>Game thread, from <see cref="Step"/> after the reload's start and end are seen.</summary>
        private static void StepMagazine(MyCharacter character, IMyHandheldGunObject<MyDeviceBase> gun, MyEntity weapon)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Hand off = TwoHand.OffHand;
            if (noneTickAt != 0 && now >= noneTickAt)
            {
                noneTickAt = 0;
                Haptics.Pulse(off, NoneAmplitude, NoneSeconds);
            }

            // The weapon put away or switched, or the feature off: what the hands held goes.
            MyAutomaticRifleGun mine = wellGun ?? (inHand ? handGun : null);
            if (mine != null && (!ReferenceEquals(gun, mine) || mine.Closed))
                ResetMagazine(gun == null ? "the weapon was put away" : "the weapon was switched");
            if (well != Well.In && !PhysicalOn)
                ResetMagazine("the physical reload was turned off");

            MyAutomaticRifleGun rifle = gun as MyAutomaticRifleGun;
            // A pull that dropped the magazine (the reload gesture, at the well) is not one at the pouch.
            // The trigger takes one at the pouch, or the grip (claimed there, GripClaim); either held keeps it.
            pouchPull.Update(OffTrigger, () => !magazinePull.Held && (inHand || TakeMagazine(off, "trigger pulled")));
            bool hip = pouchPull.Held || GripClaim.Holds(off, PouchGripName);
            if (hip && !hipHeld)
                HipTaken(character, rifle);
            else if (!hip && hipHeld)
                HipLetGo(character);
            hipHeld = hip;

            if (well == Well.Out && rifle != null)
            {
                if (!rifle.IsReloading)
                {
                    // Ended without a seat: a client's timer, or the server's after the hold let go.
                    Log.Info($"Reload: the {Name(character)}'s reload ended before a magazine was seated ({AmmoState(gun)}); the well shows its magazine");
                    ShowWell(rifle);
                }
                else if (holdingTimer)
                {
                    if (!rifle.GunBase.HasEnoughMagazines())
                    {
                        holdingTimer = false;
                        Log.Info($"Reload: no magazines left for the {Name(character)}; the game's reload ends empty");
                    }
                    else
                        HoldTimer(rifle);
                }
            }

            StepBare(character, rifle);

            // Placed here when the arms did not place it in this frame (hand IK off, or not in first person); see ToolPlaced.
            if (placedFrame != HandIK.Frame && (well != Well.In || inHand || !Shapes.Idle))
            {
                bool hand = HandWorld.TryGetNow(character, off, out MatrixD grip, out _, out _);
                PlaceShapes(character, hand, grip);
            }
            placedFrame = -1;
        }

        /// <summary>
        /// A weapon drawn with its magazine empty and no magazine for it in the inventory comes out with nothing in its well, so
        /// it shows it needs one. Once one is in the inventory the game loads it by itself (MyCharacter.UpdateShooting, timed),
        /// and the well's magazine is shown again as that reload starts. A weapon fired empty keeps its empty magazine in.
        /// </summary>
        private static void StepBare(MyCharacter character, MyAutomaticRifleGun rifle)
        {
            if (rifle != null && rifle.GunBase != null && rifle.GunBase.CurrentAmmo > 0)
                loadedGun = rifle;
            bool bare = rifle != null && !rifle.Closed && hooked && !Off && VRInput.Active && well == Well.In && !inHand
                        && !ReferenceEquals(loadedGun, rifle) && !rifle.IsReloading && rifle.GunBase != null && rifle.GunBase.HasAmmoMagazines
                        && rifle.GunBase.CurrentAmmo == 0 && !rifle.GunBase.HasEnoughMagazines() && TryWell(rifle, out _, out _, out _);
            if (bare)
            {
                if (!ReferenceEquals(bareGun, rifle))
                    Log.Info($"Reload: the {Name(character)} was drawn with its magazine empty and none for it in the inventory; its well is empty");
                bareGun = rifle;
                Shapes.HideWell(rifle);
                return;
            }
            if (bareGun == null)
                return;
            if (well == Well.In)
                Shapes.ShowWell(bareGun);
            bareGun = null;
        }

        /// <summary>The in-hand magazine follows the other hand, seats when it reaches the well, and slides in; the drops fall.</summary>
        private static void PlaceShapes(MyCharacter character, bool handTracked, in MatrixD grip)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            MyAutomaticRifleGun rifle = wellGun ?? character.CurrentWeapon as MyAutomaticRifleGun;
            MyEntity weapon = rifle;
            bool hasWell = rifle != null && !rifle.Closed && TryWell(rifle, out _, out _, out _);

            if (inHand && handTracked)
            {
                MatrixD held = HeldMagazine(grip);
                double dt = (now - lastHandAt) / (double)Second;
                if (lastHandAt != 0 && dt > 0.0 && dt < 0.1)
                    handVelocity = (held.Translation - lastHandPos) / dt;
                lastHandPos = held.Translation;
                lastHandAt = now;
                MatrixD drawn = Guided(rifle, hasWell, held, out bool guiding);
                if (guiding && !guided)
                    Haptics.Pulse(TwoHand.OffHand, GuideAmplitude, GuideSeconds);
                guided = guiding;
                Shapes.Hand(drawn);
                if (well == Well.Out && hasWell && rifle.IsReloading && Seats(rifle, held, out double across, out double angle))
                    Seat(character, rifle, drawn, across, angle);
            }
            else
                guided = false;

            if (well == Well.Seating && hasWell)
            {
                TryWell(rifle, out MatrixD rest, out _, out _);
                double t = (now - slideAt) / (double)Second / SlideSeconds;
                if (t >= 1.0)
                {
                    ShowWell(rifle);
                    Log.Info($"Reload: the {Name(character)}'s magazine is home ({AmmoState(rifle)})");
                }
                else
                {
                    Matrix to = (Matrix)(rest * weapon.PositionComp.WorldMatrixNormalizedInv);
                    Matrix local = Slide(slideFrom, to, (float)(1.0 - (1.0 - t) * (1.0 - t)));
                    Shapes.Hand((MatrixD)local * weapon.WorldMatrix);
                }
            }
            else if (well != Well.In && hasWell)
                Shapes.HideWell(rifle);

            Shapes.StepDrops(now, weapon?.Render.NearFlag ?? false);
        }

        // ---- The hip ----

        /// <summary>
        /// A trigger pull or a grip has just started: it takes a magazine when it is the other hand at its pouch (<see cref="AtPouch"/>,
        /// or with <paramref name="hipOnly"/> at its hip, <see cref="AtHip"/>) while the held weapon's well waits for one.
        /// </summary>
        private static bool TakeMagazine(Hand hand, string what, bool hipOnly = false)
        {
            if (!PhysicalOn || well != Well.Out)
                return false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (!HandTools.IsLocalOnFoot(character) || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay)
                || !(character.CurrentWeapon is MyAutomaticRifleGun rifle) || !ReferenceEquals(rifle, wellGun) || rifle.Closed || rifle.GunBase == null || !rifle.GunBase.HasAmmoMagazines)
                return false;
            string where;
            if (hipOnly ? AtHip(character, hand, out where) : AtPouch(character, rifle, hand, out where))
                return true;
            Note($"Reload: {hand} {what} {where}: not at the pouch");
            return false;
        }

        /// <summary>
        /// The other hand is at its pouch: within <see cref="HipReach"/> of the hip, or <see cref="PouchBelowEyes"/> or more below
        /// the eyes and not at the weapon (its magazine's reach, its front grip). <paramref name="where"/> says where it is, for the log.
        /// </summary>
        private static bool AtPouch(MyCharacter character, MyAutomaticRifleGun rifle, Hand hand, out string where)
        {
            if (AtHip(character, hand, out where))
                return true;
            if (!HandWorld.TryGetNow(character, hand, out MatrixD grip, out _, out _) || !TryBelowEyes(character, hand, out double below))
                return false;
            bool atWeapon = TryZone(rifle, character.HandItemDefinition, out Zone zone) && Distance(zone, grip.Translation) <= zone.Reach
                            || LeftGap(character.HandItemDefinition, rifle, grip.Translation) <= TwoHand.GrabReach;
            where = $"{where}, {below * 100:0} cm below the eyes{(atWeapon ? ", at the weapon" : string.Empty)}";
            return below >= PouchBelowEyes && !atWeapon;
        }

        /// <summary>The other hand is within <see cref="HipReach"/> of its hip. <paramref name="where"/> says how far, for the log.</summary>
        private static bool AtHip(MyCharacter character, Hand hand, out string where)
        {
            where = "with the hand untracked";
            if (!HandWorld.TryGetNow(character, hand, out MatrixD grip, out _, out _) || !TryHip(character, hand, out Vector3D hip))
                return false;
            double toHip = Vector3D.Distance(grip.Translation, hip);
            where = $"{toHip * 100:0} cm from the hip";
            return toHip <= HipReach;
        }

        /// <summary>How far below the eyes the hand is, metres, along the body's up: the tracked head and hand, placed on the body as the arms are.</summary>
        private static bool TryBelowEyes(MyCharacter character, Hand hand, out double below)
        {
            below = 0.0;
            HandState state = VRInput.Get(hand);
            if (!state.Tracked || !HandWorld.TryGetTrackingToBody(character, out MatrixD toModel, out _))
                return false;
            Vector3 head = GameHead.Take() ? GameHead.TrackingPose.Translation : Vector3.Zero;
            below = Vector3D.Transform((Vector3D)head, toModel).Y - Vector3D.Transform((Vector3D)state.Grip.Translation, toModel).Y;
            return true;
        }

        private static void HipTaken(MyCharacter character, MyAutomaticRifleGun rifle)
        {
            Hand off = TwoHand.OffHand;
            if (rifle == null || rifle.GunBase == null)
                return;
            if (!rifle.GunBase.HasEnoughMagazines())
            {
                Haptics.Pulse(off, NoneAmplitude, NoneSeconds);
                noneTickAt = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(NoneGap * Second);
                Log.Info($"Reload: {off} hand at the pouch: no magazines for the {Name(character)} ({AmmoState(rifle)})");
                return;
            }
            string model = HandModel(rifle, out string why);
            if (model == null)
            {
                Log.Info($"Reload: the {Name(character)}'s magazine has no model to hold");
                return;
            }
            inHand = true;
            handGun = rifle;
            lastHandAt = 0;
            handVelocity = Vector3D.Zero;
            Shapes.TakeInHand(model);
            drawnNoted = false;
            guided = false;
            Haptics.Pulse(off, TakeAmplitude, TakeSeconds);
            string seat = SeatPlace(character, rifle);
            Log.Info($"Reload: {off} hand took a {rifle.GunBase.CurrentAmmoMagazineDefinition?.Id.SubtypeName ?? "magazine"} from the pouch ({why}; {AmmoState(rifle)})" +
                     (seat != null ? $"; it seats with the other hand at stage {seat}" : string.Empty));
        }

        /// <summary>The pouch's pull let go: a magazine still in the hand goes back in the pouch, or falls anywhere else.</summary>
        private static void HipLetGo(MyCharacter character)
        {
            if (!inHand)
                return;
            inHand = false;
            Hand off = TwoHand.OffHand;
            bool atPouch = character.CurrentWeapon is MyAutomaticRifleGun rifle && AtPouch(character, rifle, off, out _);
            if (atPouch)
            {
                Shapes.Stow();
                Haptics.Pulse(off, StowAmplitude, StowSeconds);
                Log.Info($"Reload: {off} hand put the magazine back in the pouch");
            }
            else
            {
                Vector3D velocity = (character.Physics != null ? (Vector3D)character.Physics.LinearVelocity : Vector3D.Zero) + handVelocity;
                Shapes.DropFromHand(velocity);
                Log.Info($"Reload: {off} hand let go of the magazine away from the well; it falls (the inventory is untouched)");
            }
        }

        /// <summary>
        /// The pouch on the other hand's hip: the astronaut's thigh joint on that side (SE_RigLThigh or SE_RigRThigh, in its
        /// animated pose), out to the side and a little forward; without the bones, a place measured from the feet.
        /// </summary>
        private static bool TryHip(MyCharacter character, Hand hand, out Vector3D hip)
        {
            hip = Vector3D.Zero;
            MatrixD body = character.WorldMatrix;
            float side = hand == Hand.Left ? -1f : 1f;
            MyCharacterBone thigh = character.AnimationController?.FindBone(hand == Hand.Left ? "SE_RigLThigh" : "SE_RigRThigh", out _);
            if (thigh != null)
            {
                Vector3D joint = Vector3D.Transform((Vector3D)thigh.AbsoluteTransform.Translation, body);
                hip = joint + body.Right * side * HipOut + body.Forward * HipForward + body.Up * HipUp;
                return true;
            }
            hip = body.Translation + body.Up * HipHeightFallback + body.Right * side * HipSideFallback + body.Forward * HipForward;
            return true;
        }

        // ---- The well ----

        /// <summary>The well's magazine at rest in the world (the dummy's place), its model's box, and its model.</summary>
        private static bool TryWell(MyAutomaticRifleGun rifle, out MatrixD rest, out BoundingBox box, out string model)
        {
            rest = MatrixD.Identity;
            box = default;
            model = null;
            if (rifle?.Model?.Dummies == null || !rifle.Model.Dummies.TryGetValue(MyAutomaticRifleGun.NAME_DUMMY_MAGAZINE, out VRageRender.Import.MyModelDummy dummy)
                || rifle.Subparts == null || !rifle.Subparts.TryGetValue(MyAutomaticRifleGun.NAME_SUBPART_MAGAZINE, out MyEntitySubpart part) || part.Model == null)
                return false;
            rest = (MatrixD)Matrix.Normalize(dummy.Matrix) * rifle.WorldMatrix;
            box = part.Model.BoundingBox;
            model = part.Model.AssetName;
            return box.Size.LengthSquared() > 1e-6f;
        }

        private static string WellModel(MyAutomaticRifleGun rifle) => TryWell(rifle, out _, out _, out string model) ? model : null;

        /// <summary>
        /// The magazine drawn in the hand: the well's own (the weapon's magazine subpart's model), so the one in the hand is the
        /// one that seats, in the well's own frame (its up the way it goes in), and the slide into the well never changes model.
        /// The ammo item's model is often the same (the MR-20's is, measured 2026-10-20) but need not be (the launchers' is a
        /// whole rocket); <paramref name="why"/> says how the two compare, for the log.
        /// </summary>
        private static string HandModel(MyAutomaticRifleGun rifle, out string why)
        {
            why = "no well";
            if (!TryWell(rifle, out _, out BoundingBox wellBox, out string wellModel))
                return null;
            string item = rifle.GunBase?.CurrentAmmoMagazineDefinition?.Model;
            BoundingBox? itemBox = string.IsNullOrEmpty(item) ? null : Shapes.ModelBox(item);
            why = $"the well's model {Size(wellBox)}" + (item == null ? string.Empty
                : string.Equals(item, wellModel, StringComparison.OrdinalIgnoreCase) ? ", the item's too"
                : $"; the item's is {item}{(itemBox != null ? " " + Size(itemBox.Value) : string.Empty)}");
            return wellModel;
        }

        private static string Size(in BoundingBox box) => $"{box.Size.X * 100:0.0} x {box.Size.Y * 100:0.0} x {box.Size.Z * 100:0.0} cm";

        /// <summary>
        /// The magazine in the fist: its top (the end that goes into the well, its own up) out of the top of the fist along the
        /// grip's forward, its front (the weapon's forward once seated) away from the palm's heel; held <see cref="HoldAt"/> up its length.
        /// </summary>
        private static MatrixD HeldMagazine(in MatrixD grip)
        {
            BoundingBox box = Shapes.HandBox;
            MatrixD turn = MatrixD.CreateWorld(Vector3D.Zero, grip.Down, grip.Forward);
            Vector3 holdPoint = box.Center + Vector3.Up * (box.Size.Y * (HoldAt - 0.5f));
            turn.Translation = grip.Translation - Vector3D.TransformNormal((Vector3D)holdPoint, turn);
            return turn;
        }

        /// <summary>The magazine in the hand is near enough the well for <see cref="Guided"/> to draw it lining up: for the tick as it starts.</summary>
        private static bool guided;

        /// <summary>The drawn fist's place against the controller's logged once per magazine taken.</summary>
        private static bool drawnNoted;

        /// <summary>
        /// Where the magazine in the hand is drawn: at <paramref name="held"/> (in the fist), or near the well (its top within
        /// <see cref="GuideReach"/> of the well's line and the magazine within <see cref="GuideCone"/> of its axis) moved and turned
        /// part way to the well's line and axis, more the nearer and straighter it is, up to <see cref="GuideShare"/>.
        /// </summary>
        private static MatrixD Guided(MyAutomaticRifleGun rifle, bool hasWell, in MatrixD held, out bool guiding)
        {
            guiding = false;
            if (well != Well.Out || !hasWell || !rifle.IsReloading || !TryWell(rifle, out MatrixD rest, out BoundingBox wellBox, out _))
                return held;
            Vector3D axis = Vector3D.Normalize(rest.Up);
            double angle = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(Vector3D.Normalize(held.Up), axis), -1.0, 1.0)));
            if (angle > GuideCone)
                return held;
            BoundingBox box = Shapes.HandBox;
            Vector3 topLocal = box.Center + Vector3.Up * box.Size.Y * 0.5f;
            Vector3D top = Vector3D.Transform((Vector3D)topLocal, held);
            Vector3D seated = Vector3D.Transform((Vector3D)(wellBox.Center + Vector3.Up * wellBox.Size.Y * 0.5f), rest);
            double along = Vector3D.Dot(top - seated, axis);
            if (along < -GuideDepth * wellBox.Size.Y)
                return held;
            double onLine = Math.Min(along, 0.0);
            double across = Vector3D.Distance(top, seated + axis * onLine);
            if (across > GuideReach)
                return held;
            double share = (1.0 - MathHelper.Clamp((across - SeatReach * 0.5) / (GuideReach - SeatReach * 0.5), 0.0, 1.0))
                           * (1.0 - MathHelper.Clamp((angle - SeatCone) / (GuideCone - SeatCone), 0.0, 1.0));
            if (share <= 0.0)
                return held;
            guiding = true;
            MatrixD target = MatrixD.CreateWorld(Vector3D.Zero, Vector3D.Normalize(rest.Forward), axis);
            target.Translation = seated + axis * onLine - Vector3D.TransformNormal((Vector3D)topLocal, target);
            MatrixD toLocal = rifle.PositionComp.WorldMatrixNormalizedInv;
            return (MatrixD)Slide((Matrix)(held * toLocal), (Matrix)(target * toLocal), (float)share * GuideShare) * rifle.WorldMatrix;
        }

        /// <summary>The other hand holds a magazine (taken from the pouch, or sliding into the well): its fingers close round it (<see cref="HandIK"/>).</summary>
        internal static bool HoldsMagazine(Hand hand) => hand == TwoHand.OffHand && hooked && guard.On && (inHand || well == Well.Seating);

        /// <summary>
        /// Game thread, from <see cref="HandIK"/> once the other arm is solved: the magazine in the hand drawn in the drawn fist,
        /// <paramref name="drawnGrip"/> the controller's grip pose the drawn palm stands for, in the world. The drawn hand is where
        /// the controller is unless the arm is out of reach or the hand is stopped on something.
        /// </summary>
        internal static void HandDrawn(MyCharacter character, in MatrixD drawnGrip)
        {
            if (!inHand || !guard.On)
                return;
            try
            {
                MyAutomaticRifleGun rifle = wellGun ?? character.CurrentWeapon as MyAutomaticRifleGun;
                bool hasWell = rifle != null && !rifle.Closed && TryWell(rifle, out _, out _, out _);
                MatrixD held = HeldMagazine(drawnGrip);
                if (!drawnNoted && lastHandAt != 0)
                {
                    drawnNoted = true;
                    Log.Info($"Reload: the magazine is drawn in the drawn fist, {Vector3D.Distance(held.Translation, lastHandPos) * 100:0.0} cm from the controller's; the fingers closed round it");
                }
                Shapes.Hand(Guided(rifle, hasWell, held, out _));
            }
            catch (Exception e)
            {
                ResetMagazine("placing the magazine in the hand failed");
                guard.Failed(e, "Reload: the magazine could not be placed in the hand");
            }
        }

        /// <summary>The other hand's grip that holds the magazine (<see cref="HeldMagazine"/>) on the well's axis with its top at home: where it seats.</summary>
        private static bool TrySeatGrip(MyAutomaticRifleGun rifle, out MatrixD grip)
        {
            grip = default;
            if (!TryWell(rifle, out MatrixD rest, out BoundingBox wellBox, out _))
                return false;
            BoundingBox box = Shapes.HandBox;
            MatrixD held = MatrixD.CreateWorld(Vector3D.Zero, rest.Forward, rest.Up);
            Vector3D seated = Vector3D.Transform((Vector3D)(wellBox.Center + Vector3.Up * wellBox.Size.Y * 0.5f), rest);
            held.Translation = seated - Vector3D.TransformNormal((Vector3D)(box.Center + Vector3.Up * box.Size.Y * 0.5f), held);
            Vector3 holdPoint = box.Center + Vector3.Up * (box.Size.Y * (HoldAt - 0.5f));
            grip = MatrixD.CreateWorld(held.Translation + Vector3D.TransformNormal((Vector3D)holdPoint, held), held.Up, -held.Forward);
            return true;
        }

        /// <summary>The magazine in the hand is in line with the well and its top is in the column it slides up: from one length below its seated place to that place.</summary>
        private static bool Seats(MyAutomaticRifleGun rifle, in MatrixD held, out double across, out double angle)
        {
            across = angle = double.NaN;
            if (!TryWell(rifle, out MatrixD rest, out BoundingBox wellBox, out _))
                return false;
            Vector3D axis = Vector3D.Normalize(rest.Up);
            angle = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(Vector3D.Normalize(held.Up), axis), -1.0, 1.0)));
            BoundingBox handBox = Shapes.HandBox;
            Vector3D top = Vector3D.Transform((Vector3D)(handBox.Center + Vector3.Up * handBox.Size.Y * 0.5f), held);
            Vector3D seated = Vector3D.Transform((Vector3D)(wellBox.Center + Vector3.Up * wellBox.Size.Y * 0.5f), rest);
            double length = wellBox.Size.Y * SeatDepth;
            double along = MathHelper.Clamp(Vector3D.Dot(top - seated, axis), -length, 0.0);
            across = Vector3D.Distance(top, seated + axis * along);
            if (Trace)
                Note($"Reload trace: magazine top {across * 100:0.0} cm off the well's line, {angle:0} deg off its axis, {Vector3D.Dot(top - seated, axis) * 100:0.0} cm along it from home");
            return across <= SeatReach && angle <= SeatCone;
        }

        private static void Seat(MyCharacter character, MyAutomaticRifleGun rifle, in MatrixD held, double across, double angle)
        {
            Hand off = TwoHand.OffHand;
            if (!rifle.GunBase.HasEnoughMagazines())
            {
                // The inventory emptied while the magazine was in the hand: it was never one to load.
                inHand = false;
                pouchPull.Release();
                GripClaim.Release(TwoHand.OffHand, PouchGripName);
                Shapes.DropFromHand(Vector3D.Zero);
                Haptics.Pulse(off, NoneAmplitude, NoneSeconds);
                noneTickAt = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(NoneGap * Second);
                Log.Info($"Reload: the magazine reached the {Name(character)}'s well with none left in the inventory; nothing loads");
                return;
            }
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            well = Well.Seating;
            inHand = false;
            hipHeld = false;
            pouchPull.Release();
            GripClaim.Release(TwoHand.OffHand, PouchGripName);
            slideFrom = (Matrix)(held * rifle.PositionComp.WorldMatrixNormalizedInv);
            slideAt = now;
            noRealignUntil = now + (long)((rifle.GetReloadDuration() + 0.5) * Second);
            double toGo = 0.0;
            if (holdingTimer && reloadEnd != null)
            {
                // Ends now, unless that is before the game's own reload time: then at that time.
                MyTimeSpan gameNow = MyTimeSpan.FromMilliseconds(MySandboxGame.TotalGamePlayTimeInMilliseconds);
                if (reloadFloor > gameNow)
                {
                    reloadEnd(rifle) = reloadFloor;
                    toGo = (reloadFloor - gameNow).Seconds;
                }
                else
                    reloadEnd(rifle) = MyTimeSpan.FromMilliseconds(MySandboxGame.TotalGamePlayTimeInMilliseconds - 1);
            }
            holdingTimer = false;
            Haptics.Pulse(VRSettings.DominantHand, SeatAmplitude, SeatSeconds);
            Haptics.Pulse(off, SeatAmplitude, SeatSeconds);
            seatSound = true;
            try
            {
                ((IMyHandheldGunObject<MyGunBase>)rifle).PlayReloadSound();
            }
            finally
            {
                seatSound = false;
            }
            Log.Info($"Reload: {off} hand seated a magazine in the {Name(character)} after {(now - outAt) / (double)Second:0.00} s " +
                     $"({across * 100:0.0} cm off the well's line, {angle:0} deg off its axis); " +
                     (!Sandbox.Game.Multiplayer.Sync.IsServer ? "the server's reload ends on its timer"
                      : toGo > 0.0 ? $"the game's reload ends in {toGo:0.00} s, at its own reload time"
                      : "the game's reload ends now"));
        }

        /// <summary>The game's reload end kept one reload time ahead, so it ends that long after the hold stops if nothing seats.</summary>
        private static void HoldTimer(MyAutomaticRifleGun rifle)
        {
            if (holdingTimer && reloadEnd != null)
                reloadEnd(rifle) = MyTimeSpan.FromMilliseconds(MySandboxGame.TotalGamePlayTimeInMilliseconds + rifle.GetReloadDuration() * 1000.0);
        }

        /// <summary>The well's own magazine back at its place and shown; the hand's slide over.</summary>
        private static void ShowWell(MyAutomaticRifleGun rifle)
        {
            well = Well.In;
            holdingTimer = false;
            Shapes.EndHand();
            if (rifle != null && !rifle.Closed)
            {
                rifle.ResetMagazinePosition();
                Shapes.ShowWell(rifle);
            }
            wellGun = null;
        }

        /// <summary>Everything the hands held for a reload let go: the well shown again, a magazine in the hand dropped.</summary>
        private static void ResetMagazine(string why)
        {
            if (well == Well.In && !inHand && wellGun == null)
                return;
            if (why != null)
                Log.Info($"Reload: the physical reload ended: {why}");
            if (inHand)
            {
                inHand = false;
                pouchPull.Release();
                GripClaim.Release(TwoHand.OffHand, PouchGripName);
                Shapes.DropFromHand(Vector3D.Zero);
            }
            MyAutomaticRifleGun rifle = wellGun;
            ShowWell(rifle);
        }

        /// <summary>A pose part way from one to another: the place straight, the turn by the shortest way.</summary>
        private static Matrix Slide(in Matrix from, in Matrix to, float t)
        {
            Quaternion a = Quaternion.CreateFromRotationMatrix(from), b = Quaternion.CreateFromRotationMatrix(to);
            Matrix m = Matrix.CreateFromQuaternion(Quaternion.Slerp(a, b, t));
            m.Translation = Vector3.Lerp(from.Translation, to.Translation, t);
            return m;
        }
    }
}
