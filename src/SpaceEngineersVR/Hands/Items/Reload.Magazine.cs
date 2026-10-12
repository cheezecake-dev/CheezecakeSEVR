using System;
using System.Reflection;
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
    /// a fresh one from its hip and pushes it up into the magazine well, and that is when the weapon is loaded.
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
    /// <para>Which reloads go this way: the reload press of the hands (<see cref="Wanted"/>), and the game's own reload of an
    /// empty magazine (MyCharacter starts it by itself as the last round is fired). R with rounds left stays the game's timed
    /// reload, as on the desktop.</para>
    /// <para>The well is the model's own: the subpart "magazine" at the dummy "subpart_magazine" (as <see cref="TryZone"/>),
    /// pushed in along the weapon's up. Every hand weapon's model has it; the magazine models are the ammo magazine items' own
    /// models, so the one in the hand is the one that seats. The hip is the astronaut's thigh joint on the other hand's side.</para>
    /// <para>The sound: each weapon has one reload cue (its ReloadSoundName; there is no separate magazine out or in), which the
    /// game plays as its reload starts (MyCharacter.PlayReloadAnimation). For a physical reload it is held back there and played
    /// as the magazine seats, on this machine; other players hear it at the start, as they see the game's animation.</para>
    /// </remarks>
    internal static partial class Reload
    {
        /// <summary>The name the hip's grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string MagazineClaimName = "Magazine";

        /// <summary>After the reload gesture (7), before the helmet (10): a hand at the hip with a weapon held is reaching for a magazine.</summary>
        public const int MagazineClaimPriority = 8;

        // ---- Tuning: all of the physical reload's places, reaches and timings ----

        /// <summary>The magazine pouch: from the thigh joint this far out to the side, forward and up (metres).</summary>
        private const float HipOut = 0.07f, HipForward = 0.03f, HipUp = 0.0f;

        /// <summary>Without the thigh bones: the hip this high above the feet and this far out from the middle (metres).</summary>
        private const float HipHeightFallback = 0.95f, HipSideFallback = 0.17f;

        /// <summary>A grip this close to the pouch takes a magazine (metres): generous, the hand reaches there unseen.</summary>
        internal const double HipReach = 0.18;

        /// <summary>The magazine's top this close to the line it slides in along seats it (metres, across the line).</summary>
        internal const double SeatReach = 0.05;

        /// <summary>The magazine at most this far off the well's axis seats (degrees).</summary>
        internal const double SeatCone = 35.0;

        /// <summary>How far below the seated place the line it slides in along starts: this share of the magazine's length.</summary>
        private const double SeatDepth = 1.0;

        /// <summary>Where the fist holds the magazine: this share of its length from its bottom.</summary>
        private const float HoldAt = 0.35f;

        /// <summary>The slide into the well, seconds.</summary>
        private const double SlideSeconds = 0.09;

        /// <summary>A dropped magazine: out of the well at this speed (m/s), turning this fast (rad/s), gone after this long, fading over the last part (seconds).</summary>
        private const double DropSpeed = 0.8, DropSpin = 3.0, DropSeconds = 1.0, DropFade = 0.35;

        /// <summary>A hand magazine as long as this many times the well's own is not drawn in the hand (the rocket launchers' item is a whole rocket); the well's model is.</summary>
        private const float HandModelLongest = 1.5f;

        /// <summary>A magazine taken: a tick on the other hand. None to take: two lighter ones this far apart.</summary>
        public const float TakeAmplitude = 0.35f, TakeSeconds = 0.025f;
        public const float NoneAmplitude = 0.25f, NoneSeconds = 0.02f;
        private const double NoneGap = 0.09;

        /// <summary>Seated: a click on both hands.</summary>
        public const float SeatAmplitude = 0.6f, SeatSeconds = 0.035f;

        /// <summary>A magazine let go of at the hip goes back: the lightest tick.</summary>
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

        /// <summary>The other hand's grip holds the hip's claim: with a magazine in it (<see cref="inHand"/>), or none to take.</summary>
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
            GripClaim.Register(MagazineClaimName, MagazineClaimPriority, TakeMagazine);
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

        /// <summary>The game's reload of <paramref name="gun"/> began with its magazine empty: fired out, not R with rounds left.</summary>
        private static bool EmptiedBy(MyAutomaticRifleGun gun) =>
            ReferenceEquals(reloadingGun, gun) ? ammoAtReload == 0 : lastAmmo == 0 && ReferenceEquals(lastGun, gun);

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
                Log.Info($"Reload: the {Name(character)}'s reload with {before} rounds left is the game's (R): timed, not by hand");
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
            bool hip = GripClaim.Holds(off, MagazineClaimName);
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

            // Placed here when the arms did not place it in this frame (hand IK off, or not in first person); see ToolPlaced.
            if (placedFrame != HandIK.Frame && (well != Well.In || inHand || !Shapes.Idle))
            {
                bool hand = HandWorld.TryGetNow(character, off, out MatrixD grip, out _, out _);
                PlaceShapes(character, hand, grip);
            }
            placedFrame = -1;
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
                Shapes.Hand(held);
                if (well == Well.Out && hasWell && rifle.IsReloading && Seats(rifle, held, out double across, out double angle))
                    Seat(character, rifle, held, across, angle);
            }

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
        /// A grip has just started: ours when it is the other hand at its hip while the held weapon's well waits for a magazine.
        /// At any other time the grip there keeps its old meaning (the toolbar wheel).
        /// </summary>
        private static bool TakeMagazine(Hand hand)
        {
            if (!PhysicalOn || hand != TwoHand.OffHand || well != Well.Out)
                return false;
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (!HandTools.IsLocalOnFoot(character) || ToolbarWheel.Owns || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay)
                || !(character.CurrentWeapon is MyAutomaticRifleGun rifle) || !ReferenceEquals(rifle, wellGun) || rifle.Closed || rifle.GunBase == null || !rifle.GunBase.HasAmmoMagazines
                || !HandWorld.TryGetNow(character, hand, out MatrixD grip, out _, out _) || !TryHip(character, hand, out Vector3D hip))
                return false;
            double d = Vector3D.Distance(grip.Translation, hip);
            if (d > HipReach)
            {
                if (d <= HipReach + DeclineNoted)
                    Note($"Reload: {hand} grip {d * 100:0.0} cm from the hip (reach {HipReach * 100:0} cm): not a magazine");
                return false;
            }
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
                Log.Info($"Reload: {off} hand at the hip: no magazines for the {Name(character)} ({AmmoState(rifle)})");
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
            Haptics.Pulse(off, TakeAmplitude, TakeSeconds);
            Log.Info($"Reload: {off} hand took a {rifle.GunBase.CurrentAmmoMagazineDefinition?.Id.SubtypeName ?? "magazine"} from the hip ({why}; {AmmoState(rifle)})");
        }

        /// <summary>The hip's grip let go: a magazine still in the hand goes back at the hip, or falls anywhere else.</summary>
        private static void HipLetGo(MyCharacter character)
        {
            if (!inHand)
                return;
            inHand = false;
            Hand off = TwoHand.OffHand;
            bool atHip = HandWorld.TryGetNow(character, off, out MatrixD grip, out _, out _) && TryHip(character, off, out Vector3D hip)
                         && Vector3D.Distance(grip.Translation, hip) <= HipReach;
            if (atHip)
            {
                Shapes.Stow();
                Haptics.Pulse(off, StowAmplitude, StowSeconds);
                Log.Info($"Reload: {off} hand put the magazine back at the hip");
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
        /// The magazine drawn in the hand: the ammo magazine item's own model (the weapons' wells use the same models), unless
        /// that is much bigger than the well's magazine (a rocket for the launchers), then the well's.
        /// </summary>
        private static string HandModel(MyAutomaticRifleGun rifle, out string why)
        {
            string wellModel = TryWell(rifle, out _, out BoundingBox wellBox, out string m) ? m : null;
            string item = rifle.GunBase?.CurrentAmmoMagazineDefinition?.Model;
            why = "the well's model";
            if (string.IsNullOrEmpty(item))
                return wellModel;
            BoundingBox? itemBox = Shapes.ModelBox(item);
            if (itemBox == null)
                return wellModel;
            if (wellModel != null && Longest(itemBox.Value) > Longest(wellBox) * HandModelLongest)
            {
                why = $"the item's model is {Longest(itemBox.Value) * 100:0} cm long, the well's {Longest(wellBox) * 100:0} cm: the well's drawn";
                return wellModel;
            }
            why = "the item's model";
            return item;
        }

        private static float Longest(in BoundingBox box) => Math.Max(box.Size.X, Math.Max(box.Size.Y, box.Size.Z));

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
                GripClaim.Release(off, MagazineClaimName);
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
            GripClaim.Release(off, MagazineClaimName);
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
                GripClaim.Release(TwoHand.OffHand, MagazineClaimName);
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
