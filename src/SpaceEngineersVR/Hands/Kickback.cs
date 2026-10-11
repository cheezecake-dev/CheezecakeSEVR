using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// A gun or tool made for two hands, used with one, kicks wildly: the held item and its aim (the bullets, the grind and
    /// drill point) are thrown about while the trigger is down, and settle back to the hand when it is let go. Held in both
    /// hands (<see cref="TwoHand"/>) only <see cref="TwoHands"/> of it is left: none as built, so the item does what the game
    /// does with it. The view is never moved; only the tool hand's pose as <see cref="HandTools.TryGetHand"/> gives it, so
    /// the item's placement, the arm that follows it, the shot direction and the tool sensors all take the kick and the
    /// camera does not.
    /// </summary>
    /// <remarks>
    /// <para>Made for two hands is the game's own data: the item definition puts the astronaut's left hand on it
    /// (SimulateLeftHand, and a LeftHand spot) on a grip of its own along the tool (<see cref="TwoHand.AimShare"/> above none):
    /// the rifles, the rocket launchers, the grinders and the hand drills. A pistol's support hand cups the tool hand (share
    /// 0.37, none): in one hand a pistol kicks, a flick per shot that is back before the next, so it stays usable; cupped by
    /// the other hand, nothing (<see cref="PistolsKick"/>). The welders have SimulateLeftHand false: those, and the block
    /// placer, never kick from this.</para>
    /// <para>Guns kick per shot from the gun's own recoil (its weapon definition's RecoilGroundVertical and Horizontal, times
    /// the game's multiplier for standing, walking, running, crouching or aiming; the world's EnableRecoil off turns it off),
    /// scaled up: the muzzle climbs with every shot before the last has settled, is thrown sideways, snaps, and shoves back.
    /// The game puts that same recoil on the character instead: on foot it turns the head up and to the side
    /// (MyAutomaticRifleGun.RecoilValueChangedCallback, AnimateRecoil, AnimateRecoilHorizontal), and on the jetpack or falling
    /// in zero g it spins the whole body. In a headset that turns the view, so for the local character it is kept off the
    /// head and the body (<see cref="BeforeGameRecoil"/>, <see cref="AfterGunUpdate"/>) and lands at the wrist here
    /// instead, with the game's own sideways draw for the shot.</para>
    /// <para>The grinder and the drill fight the hand whenever they run, biting into something or spinning in the air (a
    /// little less there, <see cref="AirShare"/>): a yank as the motor spins up, a climb and a pull to one side that build
    /// while the trigger stays down, and hard jerks. All of it is in units of the tool's own camera shake (its maximum, and
    /// each jerk drawn the way the game draws the shake), so the drill, which the game shakes harder, kicks harder. The
    /// grinder pulls to its side; the drill twists the hand about its bit, against its spin.</para>
    /// <para>Both are springs: a fast one for the snap of each kick, a slow one for where the kicks have pushed the item, which
    /// brings it back in about 0.4 s once the trigger is let go. The other hand taking the grip eases it to the two-handed
    /// share over 0.3 s. The drawn arm follows the item, so how far it is thrown is held inside limits (<see cref="MaxUp"/>,
    /// <see cref="MaxSide"/>, <see cref="MaxTwist"/>, <see cref="MaxBack"/>), approached softly and never reached.</para>
    /// <para>The "Tool and weapon kickback" setting scales it (Off, Light: half, Full); RenderDebug Kickback=0 turns it off,
    /// KickScale, KickAir and KickTwoHand tune it live (<see cref="Scale"/>, <see cref="Air"/>, <see cref="TwoHands"/>), and
    /// KickbackTrace=1 logs every frame. One line when a use starts and one when it has settled: how far the kick went, how far
    /// the aim point moved on what it pointed at (and the hand's own aim beside it), and the most the view moved in a frame.</para>
    /// </remarks>
    internal static class Kickback
    {
        /// <summary>Debug: no kickback (<see cref="Rendering.RenderDebug"/> Kickback=0), whatever the setting says.</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: a log line every simulation frame while an item is in use or settling (<see cref="Rendering.RenderDebug"/> KickbackTrace=1).</summary>
        public static bool Trace { get; set; }

        /// <summary>Live tuning: a multiplier on all of the kick, on top of the setting (<see cref="Rendering.RenderDebug"/> KickScale=x; 1 as built).</summary>
        public static double Scale { get; set; } = 1.0;

        /// <summary>Live tuning: the share of a grinder's or drill's kick left spinning in the air (<see cref="Rendering.RenderDebug"/> KickAir=x; <see cref="AirShare"/> as built).</summary>
        public static double Air { get; set; } = AirShare;

        /// <summary>Live tuning: the share of the one-handed kick left with both hands on the item (<see cref="Rendering.RenderDebug"/> KickTwoHand=x; <see cref="TwoHandShare"/> as built).</summary>
        public static double TwoHands { get; set; } = TwoHandShare;

        // ---- How hard, one-handed at Full (Light is half). Each one is one edit to tune; KickScale multiplies them all live. ----

        /// <summary>
        /// Guns: each shot climbs the muzzle by the gun's own ground recoil (degrees: the rifle 3, the precise rifle 6, times
        /// the game's 0.7 standing) times this. The game's figure is for a character it draws with both hands on the gun; one
        /// hand is far worse.
        /// </summary>
        internal const double GunClimbScale = 4.0;

        /// <summary>The climb eases toward this (degrees, one-handed at Full) however long the trigger is held: each shot adds its share of what is left, so a few seconds of full auto end far off target and the gun never turns over.</summary>
        internal const double GunMaxClimb = 45.0;

        /// <summary>Each shot also throws the muzzle sideways by the gun's RecoilGroundHorizontal times the game's own random -1..1 times this; the throws add up while the trigger stays down, toward <see cref="GunMaxSide"/> either way.</summary>
        internal const double GunSideScale = 6.0, GunMaxSide = 25.0;

        /// <summary>The snap of each shot on top of the climb, degrees per degree of the gun's own recoil: the muzzle jumps and springs back to the climb.</summary>
        internal const double GunJoltScale = 4.0;

        /// <summary>Each shot shoves the gun back toward you (metres): with no shoulder or second hand behind it, the arm takes the recoil.</summary>
        internal const double GunBack = 0.09;

        /// <summary>A gun whose definition gives no recoil (the hand-held rocket launchers): this up and this sideways, degrees.</summary>
        internal const double GunFallbackVertical = 4.0, GunFallbackHorizontal = 1.5;

        /// <summary>
        /// A pistol in one hand: each shot flips the muzzle up by the pistol's own ground recoil (degrees: the semi-auto 2, the
        /// full-auto and the flare gun 3, the elite 4, times the game's standing multiplier, 0.9 for the pistols and 1 for the
        /// flare gun) times this: about 8 degrees for the semi-auto, 12 for the full-auto, 13 for the flare gun and 16 for the
        /// elite standing, a tenth more walking. Sideways by its horizontal recoil times the game's own -1..1 draw for the shot
        /// times the same. Not a climb that holds: each flip comes back to the aim on its own (<see cref="PistolOmega"/>), and
        /// a shot fired before the last has come back lands on top of it, so spamming the trigger walks the muzzle up and
        /// timing the shots to the return keeps it on target.
        /// </summary>
        internal const double PistolClimbScale = 4.4;

        /// <summary>
        /// The pistol's flip, critically damped (rad/s): it peaks 1/16 s after the shot, is back within a tenth of it 0.31 s
        /// after and within a twentieth 0.36 s after. The semi-auto and elite fire at most every 0.13 s (450 a minute): the
        /// semi-auto fired at that pace sends each shot with the muzzle still 5 to 7 degrees up, timed to the return (0.3 s)
        /// under one; the elite twice that.
        /// </summary>
        internal const double PistolOmega = 16.0;

        /// <summary>Each pistol shot shoves the gun back into the wrist this far (metres), and it comes back with the flip.</summary>
        internal const double PistolBack = 0.04;

        /// <summary>A pistol's flip turns about the wrist, this far behind the controller's grip along the aim (metres), so the hand lifts a little and the muzzle a lot. The other items turn about the grip.</summary>
        internal const double PistolWristBehindGrip = 0.07;

        /// <summary>The grinder and the drill jerk about this many times a second while biting (<see cref="AirShare"/> of that in the air).</summary>
        internal const double JerksPerSecond = 10.0;

        /// <summary>
        /// Each jerk is drawn as the game draws its camera shake for the tool, an exponential with the tool's mean intensity
        /// capped at its maximum shake (grinder 1.0 and 1.5, MyAngleGrinder; drill 0.85 and 2, MyDrillBase), and kicks the tool
        /// this many degrees per unit of shake: a grinder's biggest jerk is 12 degrees and its average 7.6, a drill's 16 and
        /// 9.4. Full size from the moment the trigger goes down; the climb, the pull and the twist are what build.
        /// </summary>
        internal const double JerkDegreesPerShake = 8.0;

        /// <summary>The grinder and the drill climb as the trigger stays down: degrees per unit of the tool's maximum shake, once built (a grinder 24 up, a drill 32).</summary>
        internal const double ClimbDegreesPerShake = 16.0;

        /// <summary>
        /// The pull to the side from the spin's torque, as a share of the climb: the grinder's disc throws it hard to its side
        /// (18 degrees once built); the drill's spin is about its own bit, so it twists instead and pulls aside a little (8).
        /// </summary>
        internal const double GrinderTorqueShare = 0.75, DrillTorqueShare = 0.25;

        /// <summary>
        /// Which side the torque pulls to, +1 right, -1 left. The game has no torque direction for either; these are picks, to
        /// flip if they feel wrong.
        /// </summary>
        internal const double GrinderTorqueSide = 1.0, DrillTorqueSide = -1.0;

        /// <summary>
        /// The drill twists the hand about its bit, against the spin: degrees per unit of its maximum shake once built (30), and
        /// each of its jerks wrenches it on by this share of the jerk (a big one 13 more). -1 rolls the top of the tool to the
        /// left: a bit turning clockwise seen from behind, as a drill turns forward. The game spins the bit at -25 rad/s about
        /// its own Z (MyHandDrill.UpdateAfterSimulation); which way that is seen from behind depends on the model, so the side
        /// is a pick, to flip if it feels wrong.
        /// </summary>
        internal const double DrillTwistDegreesPerShake = 15.0, DrillJerkTwistShare = 0.8, DrillTwistSide = -1.0;

        /// <summary>
        /// The motor spinning up yanks the tool as the trigger goes down (after at least <see cref="SpinUpRestSeconds"/> off):
        /// degrees per unit of maximum shake, the grinder to its torque's side (15) and the drill about its bit (20), and half of
        /// that up for both. Biting or not: it is the motor, not the work.
        /// </summary>
        internal const double SpinUpDegreesPerShake = 10.0, SpinUpRestSeconds = 0.3;

        /// <summary>Spinning in the air a tool kicks at this share of biting: its climb, pull and twist, the size of its jerks and how often they come. The spin-up yank is the same either way.</summary>
        internal const double AirShare = 0.8;

        /// <summary>The climb, pull and twist build while the trigger stays down: from this share as the use starts to all of it after <see cref="BuildSeconds"/>.</summary>
        internal const double HeatStart = 0.3, BuildSeconds = 1.5;

        /// <summary>Jerks are spread this far either side of straight up (degrees), centred this far toward the torque's side.</summary>
        internal const double JerkSpread = 70.0, JerkTilt = 30.0;

        /// <summary>With both hands on the item this share of the one-handed kick is left: a third, so a braced gun still kicks but much less.</summary>
        internal const double TwoHandShare = 0.33;

        // ---- How far the item can be thrown: the drawn arm follows it, so the wrist and the arm have to stay believable ----

        /// <summary>The most the item is turned up (or down) and to the side about the hand, degrees; the two together stay inside the ellipse these make.</summary>
        internal const double MaxUp = 55.0, MaxSide = 35.0;

        /// <summary>The most the item twists the hand about its aim, degrees (a forearm turns about 80 each way).</summary>
        internal const double MaxTwist = 45.0;

        /// <summary>The most the item is shoved back toward you, metres.</summary>
        internal const double MaxBack = 0.12;

        /// <summary>Up to this share of a limit the throw is as the springs have it; past it, it is squeezed so the limit is approached and never reached.</summary>
        internal const double LimitKnee = 0.6;

        // ---- How it moves ----

        /// <summary>The snap: a kick peaks 1/30 s after it lands and is gone in about 0.16 s (critically damped, rad/s).</summary>
        internal const double JoltOmega = 30.0;

        /// <summary>Where the kicks pushed the item: followed at this rate while in use, and 95% of the way back to the hand 4.74 / 12 = 0.4 s after the trigger is let go.</summary>
        internal const double SettleOmega = 12.0;

        /// <summary>The other hand taking the grip eases the kick to the two-handed share (and letting go eases it back) over this long, seconds.</summary>
        internal const double TwoHandEaseSeconds = 0.3;

        // ---- Haptics, on the tool hand, per kick, one-handed ----

        /// <summary>A shot: the strongest the controller has, longer than the shot's own buzz (0.9, 0.06 s), which it replaces; a rifle's next shot takes over before it ends.</summary>
        public const float ShotKickAmplitude = 1f, ShotKickSeconds = 0.15f;

        /// <summary>A pistol's shot: as strong, as short as its flick.</summary>
        public const float PistolKickSeconds = 0.1f;

        /// <summary>A grinder's or drill's jerk: from the first for the smallest to the second for the biggest. The tool's light contact buzz waits for it (<see cref="Rumbling"/>).</summary>
        public const float JerkMinAmplitude = 0.65f, JerkMaxAmplitude = 1f, JerkSeconds = 0.09f;

        /// <summary>The motor's yank as it spins up.</summary>
        public const float SpinUpAmplitude = 1f, SpinUpSeconds = 0.18f;

        // ---- The game's own figures, read from it, with what the decompiled game had as the fallback ----

        private const double GrinderMeanFallback = 1.0, DrillMeanFallback = 0.85;

        private static readonly AccessTools.FieldRef<MyEngineerToolBase, MyShootActionEnum?> EffectAction = Field<MyEngineerToolBase, MyShootActionEnum?>("EffectAction");
        private static readonly AccessTools.FieldRef<MyHandDrill, bool> InDrillingRange = Field<MyHandDrill, bool>("m_objectInDrillingRange");
        private static readonly AccessTools.FieldRef<MyHandDrill, MyDrillBase> DrillBase = Field<MyHandDrill, MyDrillBase>("m_drillBase");
        private static readonly AccessTools.FieldRef<MyDrillBase, float> DrillMean = Field<MyDrillBase, float>("m_drillCameraMeanShakeIntensity");
        private static readonly AccessTools.FieldRef<MyAngleGrinder, double> GrinderMean = Field<MyAngleGrinder, double>("m_grinderCameraMeanShakeIntensity");

        private delegate void RecoilMultipliersCall(MyAutomaticRifleGun gun, MyCharacter owner, out float vertical, out float horizontal);

        private static readonly RecoilMultipliersCall RecoilMultipliers = Multipliers();

        private const double Step = VRage.Game.MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;

        private static readonly Random random = new Random();

        private enum Kind
        {
            None,
            Gun,
            Pistol,
            Grinder,
            Drill,
        }

        // ---- State (game thread) ----

        private static bool hooked;
        private static int errors, applyErrors, shotErrors;

        /// <summary>The item the state is for; a new one starts still.</summary>
        private static MyEntity current;
        private static MyHandItemDefinition currentItem;
        private static Kind currentKind;
        private static readonly Dictionary<MyHandItemDefinition, Hold> holds = new Dictionary<MyHandItemDefinition, Hold>();

        /// <summary>1 with the item in one hand, 0 with the other hand on its grip; eased (<see cref="TwoHandEaseSeconds"/>).</summary>
        private static double oneHand = 1.0;

        /// <summary>The share of the one-handed kick for the hands on the item: 1 in one hand, <see cref="TwoHands"/> in both, eased between.</summary>
        private static double hands = 1.0;

        /// <summary>How much of the kick reaches the item this frame: the setting's share, times <see cref="Scale"/> and <see cref="hands"/>, in use.</summary>
        private static double drive;

        private static double useTime, heat;
        private static bool inUse, biting, burst;

        /// <summary>Seconds since the item was last in use: a grinder or drill yanks as it spins up only after a rest (<see cref="SpinUpRestSeconds"/>).</summary>
        private static double restTime = SpinUpRestSeconds;

        /// <summary>A gun's climb and throw in this burst, degrees for a drive of 1: the slow spring's target is these times the drive, so Light, a live scale and the other hand taking the grip all scale what has built up.</summary>
        private static double burstUp, burstSide;

        /// <summary>The slow spring: where the kicks pushed the item (degrees up, degrees right, degrees of twist) and where it is headed.</summary>
        private static double slowUp, slowSide, slowTwist, slowUpV, slowSideV, slowTwistV, targetUp, targetSide, targetTwist;

        /// <summary>The fast spring: the snap of each kick (degrees up, degrees right, degrees of twist, metres back).</summary>
        private static double fastUp, fastSide, fastTwist, fastBack, fastUpV, fastSideV, fastTwistV, fastBackV;

        /// <summary>What <see cref="Apply"/> turns the pose by: radians up, radians right, radians of twist (the top to the right), metres back. Written once a frame.</summary>
        private static double outUp, outSide, outTwist, outBack;

        /// <summary>When the kick's last pulse ends (<see cref="Stopwatch"/> ticks), for <see cref="Rumbling"/>.</summary>
        private static long rumbleUntil;

        /// <summary>The game's own sideways draw (-1..1) for the shot being fired, kept from its recoil (<see cref="BeforeGameRecoil"/>) for the wrist; null when it has none.</summary>
        private static float? gameSide;

        /// <summary>The game's recoil kept off the local character since the last log line: shots, and the most its head angles were turned back in a frame (degrees).</summary>
        private static int recoilKept;
        private static double headUndone;

        // ---- One use, for the log ----

        private static bool episode, episodeGun;
        private static string episodeItem;
        private static long episodeStart, settleSince;
        private static int framesOne, framesTwo, shots, jerks, spinUps, misses, ownMisses, useFrames, biteFrames, limitedFrames, trackedFrames;
        private static double peakUp, peakSide, peakTwist, peakBack, viewTurn, viewMove;
        private static readonly List<Vector3D> hits = new List<Vector3D>(), ownHits = new List<Vector3D>();
        private static MatrixD lastView;
        private static bool lastViewValid;

        private const int MaxHits = 900;

        /// <summary>How far ahead of the hand the aim point is looked for (metres); a hit further off counts as a miss.</summary>
        private const double MeasureReach = 6.0, MeasureStart = 0.25;

        // ---- Hooks ----

        /// <summary>The per-frame update (after the character's own) and each shot of a hand gun.</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(Kickback), nameof(AfterSimulation)));
                // After the shot's own buzz (Haptics, normal priority), so the kick's pulse is the one that plays.
                harmony.Patch(AccessTools.Method(typeof(MyAutomaticRifleGun), nameof(MyAutomaticRifleGun.Shoot),
                        new[] { typeof(MyShootActionEnum), typeof(Vector3), typeof(Vector3D?), typeof(string) }),
                    postfix: new HarmonyMethod(typeof(Kickback), nameof(AfterShoot)) { priority = Priority.Low });
                hooked = true;
                Log.Info($"Kickback: hooked (a two-handed item in one hand kicks; guns {GunClimbScale}x their recoil up to {GunMaxClimb} deg, pistols a {PistolClimbScale}x flip " +
                         $"peaking {1000.0 / PistolOmega:0} ms after the shot; grinder and drill jerks {JerkDegreesPerShake} deg and climb {ClimbDegreesPerShake} deg per unit of the game's shake, " +
                         $"{AirShare} of it in the air, the drill twisting {DrillTwistDegreesPerShake} deg; two hands {TwoHandShare}; at most {MaxUp} deg up, {MaxSide} aside, " +
                         $"{MaxTwist} twist, {MaxBack * 100} cm back; settles in about {4.74 / SettleOmega:0.00} s; recoil multipliers {(RecoilMultipliers != null ? "from the game" : "not found, 1")})");
            }
            catch (Exception e)
            {
                hooked = false;
                Log.Error(e, "Kickback could not be hooked; tools and guns in one hand do not kick");
            }

            // The game's own recoil, kept off the local character's head and body whatever the kickback setting: the shot's
            // recoil as it arrives (on the server at once, on a client when the server's value does), and the head angles the
            // gun's update animates (a save and restore around it, so nothing it does to the head is missed).
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyAutomaticRifleGun), "RecoilValueChangedCallback"),
                    prefix: new HarmonyMethod(typeof(Kickback), nameof(BeforeGameRecoil)));
                harmony.Patch(AccessTools.Method(typeof(MyAutomaticRifleGun), nameof(MyAutomaticRifleGun.UpdateAfterSimulation)),
                    prefix: new HarmonyMethod(typeof(Kickback), nameof(BeforeGunUpdate)),
                    postfix: new HarmonyMethod(typeof(Kickback), nameof(AfterGunUpdate)));
                Log.Info("Kickback: the game's own gun recoil is kept off the local character (no head turn on foot, no body spin on the jetpack); it kicks at the wrist instead");
            }
            catch (Exception e)
            {
                Log.Error(e, "Kickback: the game's own gun recoil could not be kept off the local character; each shot turns the head (or spins the body on the jetpack), and the view with it");
            }
        }

        /// <summary>The share of the one-handed kick the setting asks for: 0 (Off, or RenderDebug Kickback=0), a half (Light), all (Full).</summary>
        private static double Share => Off ? 0.0 : VRSettings.KickbackShare;

        // ---- The tool hand's pose ----

        /// <summary>
        /// Turns the tool hand's grip and aim poses (as <see cref="HandTools.TryGetHand"/> gives them, after
        /// <see cref="TwoHand.Apply"/>) by this frame's kick: twisted about the aim, up and to the side about the tool hand's
        /// grip (a pistol's wrist), then back along the aim. Nothing while there is no kick.
        /// </summary>
        internal static void Apply(MyCharacter character, ref MatrixD grip, ref MatrixD aim)
        {
            double up = outUp, side = outSide, twist = outTwist, back = outBack;
            if ((up == 0.0 && side == 0.0 && twist == 0.0 && back == 0.0) || character == null || current == null || !ReferenceEquals(character.CurrentWeapon, current))
                return;
            try
            {
                Turn(ref grip, ref aim, up, side, twist, back, PivotBehindGrip);
            }
            catch (Exception e)
            {
                if (applyErrors++ < 3)
                    Log.Error(e, "Kickback could not turn the tool hand's pose");
            }
        }

        /// <summary>How far behind the grip, along the aim, the item turns: a pistol about the wrist, the rest about the grip.</summary>
        private static double PivotBehindGrip => currentKind == Kind.Pistol ? PistolWristBehindGrip : 0.0;

        private static void Turn(ref MatrixD grip, ref MatrixD aim, double up, double side, double twist, double back, double pivotBehind)
        {
            Vector3D forward = aim.Forward;
            double length = forward.Length();
            if (!(length > 1e-6))
                return;
            forward /= length;
            Vector3D upAxis = Vector3D.Reject(aim.Up, forward);
            if (!(upAxis.LengthSquared() > 1e-12))
                return;
            upAxis.Normalize();
            Vector3D right = Vector3D.Cross(forward, upAxis);
            // The twist about the aim (a turn about forward takes the top to the right), then up about the aim's right (the
            // muzzle rises), then to the side about its up (a turn about up is to the left).
            MatrixD turn = MatrixD.CreateFromAxisAngle(forward, twist) * MatrixD.CreateFromAxisAngle(right, up) * MatrixD.CreateFromAxisAngle(upAxis, -side);
            Vector3D pivot = grip.Translation - forward * pivotBehind;
            MatrixD m = MatrixD.CreateTranslation(-pivot) * turn * MatrixD.CreateTranslation(pivot - forward * back);
            grip *= m;
            aim *= m;
        }

        // ---- Each shot ----

        /// <summary>
        /// A shot of the gun in hand. It comes before this frame's <see cref="Update"/> (the character fires before its own
        /// UpdateAfterSimulation ends), and a semi-automatic fires only on the first frame of a press, when <see cref="drive"/>
        /// is still the last frame's nothing: so the shot's share is worked out here, from the same parts, and the log's
        /// episode opens here if it is not open yet.
        /// </summary>
        private static void AfterShoot(MyAutomaticRifleGun __instance, MyShootActionEnum action)
        {
            try
            {
                if (action != MyShootActionEnum.PrimaryAction || current == null || !IsGun(currentKind) || !ReferenceEquals(__instance, current)
                    || !Haptics.IsLocal(__instance.Owner) || MySession.Static?.Settings?.EnableRecoil == false)
                    return;
                double shotDrive = Share * Math.Max(0.0, Scale) * hands;
                if (!(shotDrive > 0.0))
                    return;
                if (!episode)
                    Begin(currentItem, Stopwatch.GetTimestamp());
                Shot(__instance, shotDrive);
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not kick a shot");
            }
            finally
            {
                gameSide = null;
            }
        }

        /// <summary>One shot's kick, <paramref name="shotDrive"/> of the one-handed kick at Full.</summary>
        private static void Shot(MyAutomaticRifleGun gun, double shotDrive)
        {
            MyWeaponDefinition definition = gun.GunBase?.WeaponDefinition;
            double vertical = definition?.RecoilGroundVertical ?? 0.0, horizontal = definition?.RecoilGroundHorizontal ?? 0.0;
            if (!(vertical > 0.0) && !(horizontal > 0.0))
            {
                vertical = GunFallbackVertical;
                horizontal = GunFallbackHorizontal;
            }
            else if (RecoilMultipliers != null && gun.Owner != null)
            {
                RecoilMultipliers(gun, gun.Owner, out float v, out float h);
                vertical *= v;
                horizontal *= h;
            }
            // Sideways: the game's own draw for this shot when its recoil came with it (on the server, at once), else a draw like it.
            double draw = gameSide.HasValue ? MathHelper.Clamp(gameSide.Value, -1.0, 1.0) : random.NextDouble() * 2.0 - 1.0;
            if (currentKind == Kind.Pistol)
            {
                // A flip per shot that comes back on its own: a shot before the last has come back adds to what is left of it.
                double scale = PistolClimbScale * shotDrive;
                Kick(ref fastUpV, vertical * scale, PistolOmega);
                Kick(ref fastSideV, horizontal * draw * scale, PistolOmega);
                Kick(ref fastBackV, PistolBack * shotDrive, PistolOmega);
                shots++;
                if (hands >= 0.5)
                    Rumble(ShotKickAmplitude, PistolKickSeconds);
                return;
            }
            // A burst starts from wherever the last one left the gun.
            if (!burst)
            {
                burst = true;
                burstUp = MathHelper.Clamp(slowUp / shotDrive, -GunMaxClimb, GunMaxClimb);
                burstSide = MathHelper.Clamp(slowSide / shotDrive, -GunMaxSide, GunMaxSide);
            }
            burstUp += vertical * GunClimbScale * Math.Max(0.0, 1.0 - burstUp / GunMaxClimb);
            double throw_ = horizontal * draw * GunSideScale;
            burstSide += throw_ * Math.Max(0.0, 1.0 - Math.Sign(throw_) * burstSide / GunMaxSide);
            Kick(ref fastUpV, vertical * GunJoltScale * shotDrive, JoltOmega);
            Kick(ref fastBackV, GunBack * shotDrive, JoltOmega);
            shots++;
            if (hands >= 0.5)
                Rumble(ShotKickAmplitude, ShotKickSeconds);
        }

        /// <summary>The kick's own pulse on the tool hand, and how long the tool's contact buzz waits for it (<see cref="Rumbling"/>).</summary>
        private static void Rumble(float amplitude, float seconds)
        {
            Haptics.Pulse(Haptics.ToolHand, amplitude, seconds);
            rumbleUntil = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        }

        /// <summary>
        /// A kick's pulse is still playing. The grinder's and drill's light contact buzz is asked for every frame, and the runtime
        /// replaces a vibration in progress with the next one, so it would cut each jerk to a frame; it waits for it instead
        /// (<see cref="Haptics.ToolContact"/>).
        /// </summary>
        internal static bool Rumbling => Stopwatch.GetTimestamp() < rumbleUntil;

        /// <summary>A kick that peaks at <paramref name="peak"/> on the fast spring (of rate <paramref name="omega"/>), as a push on its speed.</summary>
        private static void Kick(ref double velocity, double peak, double omega) => velocity += peak * omega * Math.E;

        /// <summary>The fast spring's rate for the item in hand: a pistol's flick is slower than a rifle's snap.</summary>
        private static double FastOmega => currentKind == Kind.Pistol ? PistolOmega : JoltOmega;

        // ---- The game's own recoil, kept off the local character ----

        /// <summary>
        /// A shot's recoil reaching a hand gun (MyAutomaticRifleGun.RecoilValueChangedCallback, on the character's synced
        /// recoil value). On the jetpack, or falling in zero g, it sets the body's angular velocity (by RecoilJetpackVertical
        /// and Horizontal, degrees a second): the whole character turns. Otherwise it sets the head's next pitch
        /// (RecoilGroundVertical times the movement multiplier, animated over the next frames by AnimateRecoil) and turns
        /// the head sideways at once (RecoilGroundHorizontal times its draw, AddHorizontalRecoil). For the local character
        /// none of it runs: the head and the body are the player's in a headset, and the recoil is the wrist's
        /// (<see cref="Shot"/>, with the game's sideways draw). Without it the gun is never recoiling, so the jetpack's own
        /// damping (MyCharacter, IsRecoiling) stops any spin as usual.
        /// </summary>
        private static bool BeforeGameRecoil(MyAutomaticRifleGun __instance)
        {
            try
            {
                MyCharacter owner = __instance.Owner;
                if (!Haptics.IsLocal(owner))
                    return true;
                if (Sandbox.Game.Multiplayer.Sync.IsServer)
                {
                    // On the server it arrives inside the shot, before AfterShoot: that shot's own draw.
                    owner.GetRecoilData(out _, out float side);
                    gameSide = side;
                }
                recoilKept++;
                return false;
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not keep the game's recoil off the character");
                return true;
            }
        }

        /// <summary>The local character's head angles around a gun's update (Harmony's state, as ExitUpright's).</summary>
        public struct HeadAngles
        {
            public MyCharacter Owner;
            public float X, Y;
        }

        /// <summary>The local character's head angles before the gun's update, which animates the game's recoil into them.</summary>
        private static void BeforeGunUpdate(MyAutomaticRifleGun __instance, out HeadAngles __state)
        {
            __state = default;
            try
            {
                MyCharacter owner = __instance.Owner;
                if (Haptics.IsLocal(owner))
                    __state = new HeadAngles { Owner = owner, X = owner.HeadLocalXAngle, Y = owner.HeadLocalYAngle };
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not keep the game's recoil off the character");
            }
        }

        /// <summary>
        /// After the gun's update: whatever its recoil animation did to the local character's head angles (AnimateRecoil,
        /// AnimateRecoilHorizontal; with no recoil started there is normally nothing, but a recoil the game had going, or its
        /// return to a stale angle, would turn the game's head) is put back.
        /// </summary>
        private static void AfterGunUpdate(HeadAngles __state)
        {
            MyCharacter owner = __state.Owner;
            if (owner == null)
                return;
            try
            {
                float x = owner.HeadLocalXAngle, y = owner.HeadLocalYAngle;
                if (x == __state.X && y == __state.Y)
                    return;
                owner.HeadLocalXAngle = __state.X;
                owner.HeadLocalYAngle = __state.Y;
                headUndone = Math.Max(headUndone, Math.Max(Math.Abs(x - __state.X), Math.Abs(y - __state.Y)));
            }
            catch (Exception e)
            {
                if (shotErrors++ < 3)
                    Log.Error(e, "Kickback could not keep the game's recoil off the character");
            }
        }

        // ---- Each frame ----

        private static void AfterSimulation(MyCharacter __instance)
        {
            if (errors >= 3 || __instance != MySession.Static?.LocalCharacter)
                return;
            try
            {
                Update(__instance);
            }
            catch (Exception e)
            {
                Still();
                current = null;
                if (errors++ < 3)
                    Log.Error(e, "Kickback failed" + (errors >= 3 ? "; it stays off for this run" : string.Empty));
            }
        }

        private static void Update(MyCharacter character)
        {
            double share = Share;
            MyEntity tool = character.CurrentWeapon as MyEntity;
            MyHandItemDefinition item = character.HandItemDefinition;
            bool live = hooked && share > 0.0 && !HandTools.Off && VRInput.Active && HandTools.IsLocalOnFoot(character)
                        && !character.IsOnLadder && !character.Closed;
            Kind kind = live ? KindOf(tool, item) : Kind.None;

            // A new item (or none, or the feature off) starts still.
            if (!ReferenceEquals(tool, current) || kind != currentKind)
            {
                End("switched");
                Still();
                recoilKept = 0;
                headUndone = 0.0;
                current = kind != Kind.None ? tool : null;
                currentItem = item;
                currentKind = kind;
                oneHand = 1.0;
                hands = 1.0;
                restTime = SpinUpRestSeconds;
            }
            if (kind == Kind.None)
                return;

            bool twoHands = TwoHand.Holds(tool);
            oneHand = Approach(oneHand, twoHands ? 0.0 : 1.0, Step / TwoHandEaseSeconds);
            double twoShare = MathHelper.Clamp(TwoHands, 0.0, 1.0);
            hands = twoShare + (1.0 - twoShare) * Smooth01(oneHand);
            bool wasInUse = inUse;
            inUse = InUse(tool, kind, out biting);
            // The log's episode opens before this frame's kicks are counted, so a spin-up on the first frame of use is in it (a
            // gun's first shot, earlier in the frame, has opened it already: AfterShoot).
            if (inUse && !wasInUse && !episode)
                Begin(item, Stopwatch.GetTimestamp());
            drive = inUse ? share * Math.Max(0.0, Scale) * hands : 0.0;
            useTime = drive > 0.0 ? useTime + Step : 0.0;
            heat = drive > 0.0 ? HeatStart + (1.0 - HeatStart) * Smooth01(useTime / BuildSeconds) : 0.0;

            if (IsGun(kind))
            {
                // The climb holds while the trigger is down (between shots, through a reload) and goes back once it is let go,
                // or once the other hand has taken all of it.
                if (!(drive > 0.0))
                {
                    burst = false;
                    burstUp = burstSide = 0.0;
                }
                targetUp = burstUp * drive;
                targetSide = burstSide * drive;
                targetTwist = 0.0;
            }
            else
            {
                Tool(tool, kind, inUse && !wasInUse);
            }
            restTime = inUse ? 0.0 : restTime + Step;

            Spring(ref slowUp, ref slowUpV, targetUp, SettleOmega);
            Spring(ref slowSide, ref slowSideV, targetSide, SettleOmega);
            Spring(ref slowTwist, ref slowTwistV, targetTwist, SettleOmega);
            Spring(ref fastUp, ref fastUpV, 0.0, FastOmega);
            Spring(ref fastSide, ref fastSideV, 0.0, FastOmega);
            Spring(ref fastTwist, ref fastTwistV, 0.0, FastOmega);
            Spring(ref fastBack, ref fastBackV, 0.0, FastOmega);

            double up = slowUp + fastUp, side = slowSide + fastSide, twist = slowTwist + fastTwist, back = fastBack;
            if (Math.Abs(up) < 1e-4 && Math.Abs(side) < 1e-4 && Math.Abs(twist) < 1e-4 && Math.Abs(back) < 1e-5 && !inUse)
            {
                Still();
                up = side = twist = back = 0.0;
            }
            bool limited = Limit(ref up, ref side, ref twist, ref back);
            outUp = MathHelper.ToRadians(up);
            outSide = MathHelper.ToRadians(side);
            outTwist = MathHelper.ToRadians(twist);
            outBack = back;

            Measure(character, tool, item, twoHands, wasInUse, up, side, twist, back, limited);
        }

        /// <summary>The grinder's and the drill's climb, pull, twist, spin-up yank and jerks for this frame.</summary>
        private static void Tool(MyEntity tool, Kind kind, bool started)
        {
            double maxShake, mean, torque, torqueShare, twistPerShake, jerkTwist;
            if (kind == Kind.Grinder)
            {
                maxShake = MyAngleGrinder.GRINDER_MAX_SHAKE;
                mean = GrinderMean != null ? GrinderMean((MyAngleGrinder)tool) : GrinderMeanFallback;
                torque = GrinderTorqueSide;
                torqueShare = GrinderTorqueShare;
                twistPerShake = jerkTwist = 0.0;
            }
            else
            {
                maxShake = MyDrillBase.DRILL_MAX_SHAKE;
                MyDrillBase drillBase = DrillBase != null ? DrillBase((MyHandDrill)tool) : null;
                mean = drillBase != null && DrillMean != null ? DrillMean(drillBase) : DrillMeanFallback;
                torque = DrillTorqueSide;
                torqueShare = DrillTorqueShare;
                twistPerShake = DrillTwistDegreesPerShake * DrillTwistSide;
                jerkTwist = DrillJerkTwistShare * DrillTwistSide;
            }
            if (!(drive > 0.0))
            {
                targetUp = targetSide = targetTwist = 0.0;
                return;
            }
            double bite = biting ? 1.0 : MathHelper.Clamp(Air, 0.0, 1.5);
            double strength = drive * bite;
            targetUp = ClimbDegreesPerShake * maxShake * strength * heat;
            targetSide = targetUp * torqueShare * torque;
            targetTwist = twistPerShake * maxShake * strength * heat;

            // The motor spinning up yanks the tool, biting or not: the grinder to its torque's side, the drill about its bit.
            if (started && restTime >= SpinUpRestSeconds)
            {
                double yank = SpinUpDegreesPerShake * maxShake * drive;
                Kick(ref fastUpV, yank * 0.5, JoltOmega);
                if (kind == Kind.Drill)
                    Kick(ref fastTwistV, yank * DrillTwistSide, JoltOmega);
                else
                    Kick(ref fastSideV, yank * torque, JoltOmega);
                spinUps++;
                if (hands >= 0.5)
                    Rumble(SpinUpAmplitude, SpinUpSeconds);
            }

            if (random.NextDouble() >= JerksPerSecond * bite * Step)
                return;
            // The game's draw: an exponential with the tool's mean intensity, capped at its maximum shake.
            double size = Math.Min(-Math.Log(1.0 - random.NextDouble()) * mean, 1.0);
            double peak = size * maxShake * JerkDegreesPerShake * strength;
            double angle = MathHelper.ToRadians(JerkTilt * torque + (random.NextDouble() * 2.0 - 1.0) * JerkSpread);
            Kick(ref fastUpV, peak * Math.Cos(angle), JoltOmega);
            Kick(ref fastSideV, peak * Math.Sin(angle), JoltOmega);
            if (jerkTwist != 0.0)
                Kick(ref fastTwistV, peak * jerkTwist, JoltOmega);
            jerks++;
            if (hands >= 0.5)
                Rumble(JerkMinAmplitude + (JerkMaxAmplitude - JerkMinAmplitude) * (float)size, JerkSeconds);
        }

        /// <summary>
        /// Holds the throw inside what the drawn arm can follow (<see cref="MaxUp"/> and <see cref="MaxSide"/> together,
        /// <see cref="MaxTwist"/>, <see cref="MaxBack"/>): up to <see cref="LimitKnee"/> of a limit as it is, past it squeezed so
        /// the limit is approached and never reached. True when it took more than 5% off any of it (for the log).
        /// </summary>
        private static bool Limit(ref double up, ref double side, ref double twist, ref double back)
        {
            bool limited = false;
            double u = up / MaxUp, s = side / MaxSide;
            double r = Math.Sqrt(u * u + s * s);
            if (r > LimitKnee)
            {
                double k = Soft(r) / r;
                up *= k;
                side *= k;
                limited = k < 0.95;
            }
            limited |= Squeeze(ref twist, MaxTwist);
            limited |= Squeeze(ref back, MaxBack);
            return limited;
        }

        private static bool Squeeze(ref double value, double most)
        {
            double r = Math.Abs(value) / most;
            if (!(r > LimitKnee))
                return false;
            double k = Soft(r) / r;
            value *= k;
            return k < 0.95;
        }

        /// <summary>A share of a limit past <see cref="LimitKnee"/>, squeezed: the same at the knee and as steep, toward 1 and never past it.</summary>
        private static double Soft(double r) => LimitKnee + (1.0 - LimitKnee) * Math.Tanh((r - LimitKnee) / (1.0 - LimitKnee));

        /// <summary>The item is in use: a gun's trigger down, a grinder or drill on; and whether a tool bites into something (the game's own tests, as <see cref="Haptics"/> uses them).</summary>
        private static bool InUse(MyEntity tool, Kind kind, out bool bite)
        {
            bite = false;
            switch (kind)
            {
                case Kind.Gun:
                case Kind.Pistol:
                    bite = true;
                    return ((MyAutomaticRifleGun)tool).IsShooting;
                case Kind.Grinder:
                    var grinder = (MyAngleGrinder)tool;
                    bite = grinder.IsShooting && !grinder.IsHeatingUp && EffectAction != null && EffectAction(grinder) == MyShootActionEnum.PrimaryAction;
                    return grinder.IsShooting;
                case Kind.Drill:
                    var drill = (MyHandDrill)tool;
                    bite = drill.IsShooting && InDrillingRange != null && InDrillingRange(drill) && drill.IsDrillingAnObject;
                    return drill.IsShooting;
                default:
                    return false;
            }
        }

        /// <summary>What kind of kick the item has (see <see cref="HoldOf"/> and the remarks).</summary>
        private static Kind KindOf(MyEntity tool, MyHandItemDefinition item)
        {
            if (tool == null || item == null || tool is MyBlockPlacerBase)
                return Kind.None;
            Hold hold = HoldOf(item);
            if (hold == Hold.OneHand)
                return Kind.None;
            if (tool is MyAutomaticRifleGun)
                return hold == Hold.Along ? Kind.Gun : PistolsKick ? Kind.Pistol : Kind.None;
            if (hold != Hold.Along)
                return Kind.None;
            if (tool is MyAngleGrinder)
                return Kind.Grinder;
            if (tool is MyHandDrill)
                return Kind.Drill;
            return Kind.None;
        }

        /// <summary>
        /// The game puts its left hand on the pistols too (the four, the flare gun included), cupping the tool hand from the side
        /// rather than on a grip of their own along the gun. True: in one hand a pistol flips up with each shot (the Pistol
        /// figures above); held with the other hand cupping it, the two-handed share (none as built, <see cref="TwoHands"/>). False: pistols never kick.
        /// </summary>
        internal static readonly bool PistolsKick = true;

        /// <summary>How the game holds an item: in one hand, with the other on a grip of its own along it, or with the other cupping the tool hand.</summary>
        private enum Hold
        {
            OneHand,
            Along,
            Cupped,
        }

        /// <summary>
        /// The one test for which items kick, from the game's data: it puts the left hand on the item (SimulateLeftHand, and a
        /// LeftHand spot) on a grip along it (the test <see cref="TwoHand"/> aims by: the rifles, launchers, grinders, drills),
        /// or cupping the tool hand (share none: the pistols). Welders, the block placer and the like have SimulateLeftHand false.
        /// </summary>
        private static Hold HoldOf(MyHandItemDefinition item)
        {
            if (holds.TryGetValue(item, out Hold hold))
                return hold;
            hold = !item.SimulateLeftHand || !(item.LeftHand.Translation.LengthSquared() > 1e-8f) ? Hold.OneHand
                : TwoHand.AimShare(item.RightHand.Translation, item.LeftHand.Translation) > 0.0 ? Hold.Along
                : Hold.Cupped;
            holds[item] = hold;
            Log.Info($"Kickback: the {Name(item)} is " + (hold == Hold.Along ? "made for two hands; in one it kicks"
                : hold == Hold.Cupped ? "held with the other hand cupping the tool hand; " + (PistolsKick ? "a gun of these flips up with each shot in one hand" : "it never kicks")
                : "a one-hand item (the game leaves the other hand off it); it never kicks"));
            return hold;
        }

        // ---- The springs ----

        /// <summary>One frame of a critically damped spring toward <paramref name="target"/> (exact for a target that holds over the frame).</summary>
        private static void Spring(ref double x, ref double v, double target, double omega)
        {
            double y = x - target, e = Math.Exp(-omega * Step), k = v + omega * y;
            x = target + (y + k * Step) * e;
            v = (v - omega * k * Step) * e;
        }

        private static void Still()
        {
            slowUp = slowSide = slowTwist = slowUpV = slowSideV = slowTwistV = targetUp = targetSide = targetTwist = 0.0;
            fastUp = fastSide = fastTwist = fastBack = fastUpV = fastSideV = fastTwistV = fastBackV = 0.0;
            outUp = outSide = outTwist = outBack = 0.0;
            useTime = heat = drive = burstUp = burstSide = 0.0;
            burst = inUse = biting = false;
        }

        // ---- The log: one line as a use starts, one as it has settled ----

        private static void Measure(MyCharacter character, MyEntity tool, MyHandItemDefinition item, bool twoHands, bool wasInUse,
            double up, double side, double twist, double back, bool limited)
        {
            long now = Stopwatch.GetTimestamp();
            if (inUse && !wasInUse)
            {
                string held = !twoHands ? "one-handed" : hands > 0.0 ? $"two-handed ({hands:0.##} of the kick)" : "two-handed (nothing added)";
                Log.Info($"Kickback: the {Name(item)} in use, {held}" +
                         (IsGun(currentKind) ? string.Empty : biting ? ", biting" : ", in the air") + $", kickback {Level()}");
            }
            if (!episode)
                return;

            if (twoHands) framesTwo++; else framesOne++;
            if (GameHead.Tracked) trackedFrames++;
            if (inUse) { useFrames++; if (biting) biteFrames++; }
            if (limited) limitedFrames++;
            peakUp = Math.Max(peakUp, Math.Abs(up));
            peakSide = Math.Max(peakSide, Math.Abs(side));
            peakTwist = Math.Max(peakTwist, Math.Abs(twist));
            peakBack = Math.Max(peakBack, Math.Abs(back));

            // The view: how far it turned and moved since the last frame.
            MatrixD view = MySector.MainCamera?.WorldMatrix ?? MatrixD.Identity;
            double frameTurn = 0.0, frameMove = 0.0;
            if (lastViewValid)
            {
                frameTurn = Math.Max(Angle(view.Forward, lastView.Forward), Angle(view.Up, lastView.Up));
                frameMove = Vector3D.Distance(view.Translation, lastView.Translation);
                viewTurn = Math.Max(viewTurn, frameTurn);
                viewMove = Math.Max(viewMove, frameMove);
            }
            lastView = view;
            lastViewValid = true;

            // Where the aim points on what is in front of it, kicked and as the hand alone (and the other hand) would have it.
            if (HandWorld.TryGetNow(character, VRSettings.DominantHand, out MatrixD grip, out MatrixD aim, out _))
            {
                TwoHand.Apply(character, ref grip, ref aim);
                Hit(character, tool, aim, ownHits, ref ownMisses);
                Turn(ref grip, ref aim, MathHelper.ToRadians(up), MathHelper.ToRadians(side), MathHelper.ToRadians(twist), back, PivotBehindGrip);
                Hit(character, tool, aim, hits, ref misses);
            }

            if (Trace)
                Log.Info($"Kickback trace: {Name(item)} use={(inUse ? 1 : 0)} bite={(biting ? 1 : 0)} hands={(twoHands ? 2 : 1)} share={hands:0.00} drive={drive:0.00} heat={heat:0.00} " +
                         $"up={up:0.00} side={side:0.00} twist={twist:0.00} back={back * 100:0.0}cm{(limited ? " limited" : string.Empty)} " +
                         $"target {targetUp:0.0}/{targetSide:0.0}/{targetTwist:0.0} view {frameTurn:0.000}deg {frameMove * 1000:0.0}mm");

            if (inUse)
            {
                settleSince = 0;
                return;
            }
            if (settleSince == 0)
                settleSince = now;
            bool settled = Math.Abs(up) < 0.05 && Math.Abs(side) < 0.05 && Math.Abs(back) < 0.001;
            if (settled || now - settleSince > 2 * Stopwatch.Frequency)
                End(settled ? "settled" : "still kicked 2 s after the trigger");
        }

        private static void Begin(MyHandItemDefinition item, long now)
        {
            episode = true;
            episodeItem = Name(item);
            episodeGun = IsGun(currentKind);
            episodeStart = now;
            settleSince = 0;
            framesOne = framesTwo = shots = jerks = spinUps = misses = ownMisses = useFrames = biteFrames = limitedFrames = trackedFrames = 0;
            peakUp = peakSide = peakTwist = peakBack = viewTurn = viewMove = 0.0;
            hits.Clear();
            ownHits.Clear();
            lastViewValid = false;
        }

        private static void End(string why)
        {
            if (!episode)
                return;
            episode = false;
            double seconds = (Stopwatch.GetTimestamp() - episodeStart) / (double)Stopwatch.Frequency;
            string held = framesTwo == 0 ? "one-handed" : framesOne == 0 ? "two-handed" : $"one-handed {framesOne} frames, two-handed {framesTwo}";
            Log.Info($"Kickback: the {episodeItem} {why} after {seconds:0.00} s, {held}, kickback {Level()}: peak {peakUp:0.0} deg up, {peakSide:0.0} deg to the side, " +
                     $"{peakTwist:0.0} deg of twist, {peakBack * 100:0.0} cm back, squeezed at the limits {limitedFrames} frames; {shots} shots, {jerks} jerks, {spinUps} spin-ups, " +
                     $"biting {biteFrames} of {useFrames} frames in use; the aim point moved over {Span(hits) * 100:0.0} cm on what it pointed at " +
                     $"({hits.Count} hits, {misses} misses; the hand's own aim {Span(ownHits) * 100:0.0} cm, {ownHits.Count} hits); " +
                     (episodeGun ? $"the game's own recoil kept off the character {recoilKept} times, its head angles put back by up to {headUndone:0.00} deg; " : string.Empty) +
                     $"the head tracked {trackedFrames} of {framesOne + framesTwo} frames; the view turned at most {viewTurn:0.000} deg and moved {viewMove * 1000:0.0} mm in a frame");
            recoilKept = 0;
            headUndone = 0.0;
        }

        private static void Hit(MyCharacter character, MyEntity tool, in MatrixD aim, List<Vector3D> list, ref int missed)
        {
            if (!HandTools.Ray(aim, out Vector3D origin, out Vector3D forward))
                return;
            MyPhysics.HitInfo? hit = MyPhysics.CastRay(origin + forward * MeasureStart, origin + forward * MeasureReach);
            IMyEntity entity = hit.HasValue ? ((IHitInfo)hit.Value).HitEntity : null;
            if (!hit.HasValue || ReferenceEquals(entity, character) || ReferenceEquals(entity, tool))
            {
                missed++;
                return;
            }
            if (list.Count < MaxHits)
                list.Add(hit.Value.Position);
        }

        /// <summary>The longest distance between two of the points (metres).</summary>
        private static double Span(List<Vector3D> points)
        {
            double longest = 0.0;
            for (int i = 0; i < points.Count; i++)
                for (int j = i + 1; j < points.Count; j++)
                    longest = Math.Max(longest, Vector3D.DistanceSquared(points[i], points[j]));
            return Math.Sqrt(longest);
        }

        // ---- Helpers ----

        private static string Level()
        {
            if (Off)
                return "off (RenderDebug)";
            string level = new[] { "Off", "Light", "Full" }[Math.Max(0, Math.Min(2, VRSettings.KickbackLevel))];
            if (Scale != 1.0)
                level += $", KickScale {Scale:0.##}";
            if (Air != AirShare)
                level += $", KickAir {Air:0.##}";
            if (TwoHands != TwoHandShare)
                level += $", KickTwoHand {TwoHands:0.##}";
            return level;
        }

        private static double Angle(Vector3D a, Vector3D b)
        {
            double la = a.Length(), lb = b.Length();
            if (!(la > 1e-9) || !(lb > 1e-9))
                return 0.0;
            return MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(a, b) / (la * lb), -1.0, 1.0)));
        }

        private static bool IsGun(Kind kind) => kind == Kind.Gun || kind == Kind.Pistol;

        private static double Approach(double value, double target, double step) =>
            value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

        private static double Smooth01(double x)
        {
            x = MathHelper.Clamp(x, 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }

        private static string Name(MyHandItemDefinition item) => item?.Id.SubtypeName ?? "tool";

        private static AccessTools.FieldRef<TOwner, TValue> Field<TOwner, TValue>(string name)
        {
            try
            {
                return AccessTools.FieldRefAccess<TOwner, TValue>(name);
            }
            catch (Exception e)
            {
                Log.Warn($"Kickback: {typeof(TOwner).Name}.{name} not found ({e.GetType().Name}); its fallback is used");
                return null;
            }
        }

        private static RecoilMultipliersCall Multipliers()
        {
            try
            {
                var method = AccessTools.Method(typeof(MyAutomaticRifleGun), "GetRecoilMultipliers", new[] { typeof(MyCharacter), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });
                return method == null ? null : (RecoilMultipliersCall)Delegate.CreateDelegate(typeof(RecoilMultipliersCall), method);
            }
            catch (Exception e)
            {
                Log.Warn($"Kickback: the game's recoil multipliers not found ({e.GetType().Name}); 1 is used");
                return null;
            }
        }
    }
}
