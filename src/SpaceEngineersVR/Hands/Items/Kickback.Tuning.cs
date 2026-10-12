using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Weapons;
using VRage.Game.ModAPI;

namespace SpaceEngineersVR.Hands
{
    // Kickback tuning: the strength, limit and timing constants, and the game figures it reads.
    internal static partial class Kickback
    {
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
    }
}
