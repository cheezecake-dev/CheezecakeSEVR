using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The held tool or gun taken in both hands. The other hand closes its grip on the spot where the game's own animation
    /// puts the astronaut's left hand (the middle of the hand held as the item definition's LeftHand has the palm, as
    /// <see cref="HeldItem"/> uses its RightHand for the tool hand; both mirrored left-handed, <see cref="ItemSpots"/>), and
    /// from then on the tool points along the line between the two hands: the item's own line from the tool hand's spot to
    /// the other hand's is turned onto the line from the tool hand's grip to the other hand's grip, by the smallest turn
    /// that does it, about the tool hand's grip, so the tool hand keeps its place on the tool and the roll about that line
    /// is the tool hand's. The turn goes into the tool hand's pose itself
    /// (<see cref="HandTools.TryGetHand"/>), so the item's placement, its sensors, the shot direction and the use ray all
    /// follow it through the paths they already take. The other arm settles onto the tool's grip (<see cref="Settle"/>).
    /// </summary>
    /// <remarks>
    /// <para>Grab: a grip pressed with the other hand hovering at the tool's front grip (<see cref="SupportZone"/>, after
    /// ColbyJack1134's: within <see cref="GrabReach"/> of it, and not nearer the magazine), claimed through
    /// <see cref="GripClaim"/> at <see cref="GripPriority.TwoHand"/>, ahead of the helmet, floating items, grab to use
    /// and hulls: the hand is on the tool it holds, the one thing it is surely reaching for. At the magazine the grip reloads
    /// (<see cref="Reload"/>); anywhere else the claim declines and the grip keeps its old meaning (the toolbar wheel on
    /// foot). A light tick on the other hand marks it coming into either zone; the grab is a tick on both hands.</para>
    /// <para>Steady: the turn only depends on where the two grips are (no world up is used, so no flip when the hands line up
    /// with it), it fades out as the other hand comes back toward the tool hand (<see cref="SeparationNone"/>), and it only
    /// counts as far as the item's grip line runs along the tool (<see cref="AimShare"/>): a pistol's support hand cups the
    /// tool hand from the side, a line that would roll the gun rather than aim it, so a pistol is steadied but aims with the
    /// tool hand.</para>
    /// <para>Release: letting go; with <see cref="Stays"/> off, also the other hand more than <see cref="ReleaseOff"/> off the
    /// grip line (the line from the tool hand's grip through the tool's grip as the tool hand alone holds it, measured across
    /// it and along it); either way putting the tool away or switching it, leaving the feet, and the other hand untracked for
    /// longer than <see cref="TrackingGrace"/>. With <see cref="Stays"/> on (the default) the astronaut's hand stays on the
    /// grip however far the real hand goes; past <see cref="ReleaseOff"/> the real hand's pull on the aim eases off, to none at
    /// <see cref="DriftNone"/>, so a drifting hand does not swing the tool. The aim eases back to the tool hand and the arm
    /// back to its controller.</para>
    /// <para>Left-handed the tool is in the left hand and the right hand takes the grip, each on the game's spots mirrored
    /// (<see cref="ItemSpots"/>).</para>
    /// </remarks>
    internal static class TwoHand
    {
        /// <summary>Debug: no two-handed hold (<see cref="Rendering.RenderDebug"/> TwoHand=0); the other grip keeps its usual meaning (the toolbar wheel).</summary>
        public static bool Off { get; set; }

        /// <summary>The other hand coming this near the second grip of a tool held in one hand says where the grip is, once.</summary>
        private const double NearGrip = 0.5;

        /// <summary>Debug: overrides <see cref="VRSettings.TwoHandGripStays"/> (<see cref="Rendering.RenderDebug"/> TwoHandStay=0/1); null follows the setting.</summary>
        public static bool? StaysDebug { get; set; }

        /// <summary>The other hand stays on the tool's grip while its grip button is held, however far it moves; off, it also lets go <see cref="ReleaseOff"/> off the grip line.</summary>
        internal static bool Stays => StaysDebug ?? VRSettings.TwoHandGripStays;

        /// <summary>
        /// Debug: the most the other hand turns round the handle it holds to suit its forearm, each way, degrees
        /// (<see cref="Rendering.RenderDebug"/> TwoHandRoll). 0, the default: the hand is held exactly as the game's LeftHand
        /// spot has it. See <see cref="OnGrip"/>.
        /// </summary>
        public static float RollMost { get; set; } = DefaultRollMost;

        internal const float DefaultRollMost = 0f;

        /// <summary>Debug: the other hand moved along the handle it holds, metres, toward its little finger (<see cref="Rendering.RenderDebug"/> TwoHandSlide, mm).</summary>
        public static float SlideDebug { get; set; }

        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string ClaimName = "TwoHand";

        /// <summary>A grip this close to the tool's grip (metres, from the other controller's grip) takes it (<see cref="GrabTolerance.Spot"/>; a hand already at it keeps <see cref="GrabTolerance.Held"/> more).</summary>
        internal const double GrabReach = GrabTolerance.Spot;

        /// <summary>The other hand this far off the grip line (metres, across it or along it) lets go: past the furthest a grip takes it from, so a hold never drops the frame it is taken.</summary>
        internal const double ReleaseOff = 0.25;

        /// <summary>The other hand untracked this long (seconds) lets go; until then it is taken where it last was on the tool hand.</summary>
        internal const double TrackingGrace = 0.25;

        /// <summary>Seconds for the hand to settle onto the grip and the aim to come to both hands.</summary>
        internal const double GrabSeconds = 0.08;

        /// <summary>Seconds for the aim to ease back to the tool hand, and for the arm to go back to its controller.</summary>
        internal const double ReleaseSeconds = 0.15, HandReturnSeconds = 0.10;

        /// <summary>The grab: a firm tick on both hands.</summary>
        public const float GrabAmplitude = 0.45f, GrabPulseSeconds = 0.03f;

        /// <summary>Let go: the lightest tick on the other hand.</summary>
        public const float LetGoAmplitude = 0.15f, LetGoSeconds = 0.015f;

        /// <summary>A grip this near the tool's grip that is still out of reach is logged (metres).</summary>
        private const double DeclineNoted = 0.30;

        /// <summary>
        /// How much of the tool's grip line has to run along the tool (the share of it along the tool's forward) for the line to
        /// aim it: a rifle 0.95, a hand drill 0.85, a grinder 0.75 (its side handle), a pistol 0.37 (the support hand under and
        /// beside the tool hand). From none at the first to all of it at the second.
        /// </summary>
        private const double AimShareNone = 0.45, AimShareFull = 0.65;

        /// <summary>The turn fades out as the other hand comes back along the line toward the tool hand: none at this share of the grip's distance, all of it at the second.</summary>
        private const double SeparationNone = 0.3, SeparationFull = 0.6;

        /// <summary>
        /// A hand that stays on the grip (<see cref="Stays"/>) while the real one drifts off it: up to <see cref="ReleaseOff"/>
        /// off the grip line it aims the tool as ever, from there its pull eases off, to none this far off (metres), and the tool
        /// points where the tool hand alone points it.
        /// </summary>
        internal const double DriftNone = 0.30;

        /// <summary>Two grips closer than this on an item (metres) are not two places to hold it.</summary>
        private const double ShortestGrip = 0.03;

        private static readonly long Second = Stopwatch.Frequency;

        // ---- State (game thread) ----

        /// <summary>The other hand holds the tool's grip now.</summary>
        private static bool held;

        /// <summary>The hold was taken by the other hand's trigger, not its grip: it lasts while the trigger is held.</summary>
        private static bool byTrigger, triggerWas;

        /// <summary>
        /// Where the other hand took hold, off the tool's grip spot, in the tool's own turn (as the tool hand alone holds it): a
        /// hand that took hold beside the spot steadies the tool from there, so the tool does not swing toward it as it is taken.
        /// </summary>
        private static Vector3D takeOffset;

        /// <summary>The tool held (or, while the aim eases back, last held) in both hands, and its grip spots in its own space.</summary>
        private static MyEntity weapon;
        private static MyHandItemDefinition heldItem;

        /// <summary>The other hand holds this tool's grip now (<see cref="Kickback"/>: held in both hands it adds nothing).</summary>
        internal static bool Holds(MyEntity tool) => held && tool != null && ReferenceEquals(tool, weapon);
        private static Vector3 toolSpot, otherSpot;

        /// <summary>How far the tool's grip line aims it (<see cref="AimShare"/>), for the item held.</summary>
        private static double aimShare;

        /// <summary>0 to 1: the aim from both hands, and the other arm on the tool's grip; linear, eased where read.</summary>
        private static double aimProgress, handProgress;

        /// <summary>The other palm's place on the tool, in the character's model space, for the arm (<see cref="Settle"/>).</summary>
        private static Matrix supportModel;
        private static bool supportValid;

        /// <summary>How far the other hand is turned round the handle (<see cref="OnGrip"/>), radians; and whether it was on the tool last frame.</summary>
        private static float roll;
        private static bool rolled;

        /// <summary>The most the hand turns round the handle in one frame once on it, radians (3 degrees).</summary>
        private const float RollStep = 0.0524f;

        /// <summary>Below this (the squared sine of the forearm's angle to the handle, about 12 degrees) the forearm lies along the handle and gives no way to turn.</summary>
        private const float RollFloor = 0.04f;

        /// <summary>The other hand's grip in the tool hand's aim space, as last tracked, for a moment of lost tracking.</summary>
        private static Vector3D offInMain;
        private static bool offInMainValid;
        private static long offSeen;

        /// <summary>The tool whose second grip the other hand was last near (<see cref="NearGrip"/>); null: none.</summary>
        private static MyHandItemDefinition nearGripOf;
        private static long lastStep, traceAt;

        private static bool hooked;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(TwoHand));
        private static int applyErrors, takeErrors;
        private static readonly LineThrottle notes = new LineThrottle(6);

        /// <summary>The hand that takes the tool's other grip: the one that does not hold it.</summary>
        internal static Hand OffHand => VRSettings.DominantHand == Hand.Right ? Hand.Left : Hand.Right;

        // ---- Hooks ----

        /// <summary>Registers the grip claim and the per-frame update; the update runs before the arms are solved (<see cref="HandIK"/>).</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                GripClaim.Register(ClaimName, GripPriority.TwoHand, Take);
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(TwoHand), nameof(AfterSimulation)) { priority = Priority.High });
                hooked = true;
                Log.Info($"Two-handed tools: hooked (grip claim at priority {(int)GripPriority.TwoHand}, reach {GrabReach * 100:0} cm, release {ReleaseOff * 100:0} cm off the grip line)");
            }
            catch (Exception e)
            {
                hooked = false;
                PatchRegistry.Lost(e, "Two-handed tools could not be hooked; tools stay in one hand and the other grip keeps its old job");
                throw;
            }
        }

        /// <summary>Two-handed holds are in play for this character: on foot, not up a ladder, with the feature on.</summary>
        private static bool Live(MyCharacter character) =>
            hooked && !Off && VRSettings.TwoHandedTools && !HandTools.Off && VRInput.Active && HandTools.IsLocalOnFoot(character)
            && !character.IsOnLadder && !character.Closed && character.PositionComp != null;

        /// <summary>
        /// The tool in hand and its two grip spots, in the item's own space: where each hand closes on it, the middle of the
        /// hand held as the item definition's RightHand and LeftHand have the palms (<see cref="ItemSpots"/>), which is
        /// where a controller's grip goes. The palm bones' own spots are the wrists, 9 cm back: a hand on the front handle was
        /// measured from its wrist spot and was never in reach. False with nothing in hand, for the block builder (a ray, not a
        /// tool), and for an item the game holds in one hand (<see cref="GameHasLeftHand"/>).
        /// </summary>
        private static bool TryItem(MyCharacter character, out MyEntity tool, out MyHandItemDefinition item, out Vector3 toolGrasp, out Vector3 otherGrasp)
        {
            tool = character.CurrentWeapon as MyEntity;
            item = character.HandItemDefinition;
            toolGrasp = otherGrasp = Vector3.Zero;
            if (tool == null || item == null || tool is MyBlockPlacerBase || !GameHasLeftHand(item))
                return false;
            toolGrasp = ItemSpots.ToolGrasp(item);
            otherGrasp = ItemSpots.OtherGrasp(item);
            return (otherGrasp - toolGrasp).Length() >= ShortestGrip;
        }

        /// <summary>
        /// The game itself puts the left hand on this item, in one view or the other (MyCharacter.UpdateHandBones): only then is
        /// its LeftHand spot a grip. The welders set SimulateLeftHand false (HandItems.sbc): the game holds them in one hand,
        /// and their LeftHand data, a palm 22 cm ahead of the tool hand beside the tool and turned back on itself, is never
        /// used; the other hand stays free on them and follows its controller.
        /// </summary>
        internal static bool GameHasLeftHand(MyHandItemDefinition item) =>
            (item.SimulateLeftHand || item.SimulateLeftHandFps) && item.LeftHand.Translation.LengthSquared() > 1e-8f;

        /// <summary>How far an item's grip line aims it: the share of the line from its RightHand to its LeftHand (the palm bones' spots, as the shares above were measured) that runs along its forward (-Z), eased from none to all.</summary>
        internal static double AimShare(Vector3 right, Vector3 left)
        {
            Vector3 line = left - right;
            float length = line.Length();
            return length > 1e-6f ? Smooth(AimShareNone, AimShareFull, -line.Z / length) : 0.0;
        }

        // ---- The measure ----

        /// <summary>The two hands against the tool's grip line, in the world.</summary>
        private struct Measure
        {
            /// <summary>The tool hand's grip: the turn's pivot.</summary>
            public Vector3D Grip;

            /// <summary>The tool's grip where the tool hand alone holds it.</summary>
            public Vector3D Support;

            /// <summary>Unit, from <see cref="Grip"/> to <see cref="Support"/>; <see cref="Base"/> is that distance.</summary>
            public Vector3D Along;
            public double Base;

            /// <summary>The other hand's grip; how far it is along the grip line from the tool hand, and how far off it.</summary>
            public Vector3D Off;
            public double T, Across;

            /// <summary>From the other hand's grip to the tool's.</summary>
            public double Gap;

            /// <summary>The tool as the tool hand alone holds it.</summary>
            public MatrixD One;
        }

        private static bool TryMeasure(in MatrixD grip, in MatrixD aim, Vector3 toolGrasp, Vector3 otherGrasp, in Vector3D off, out Measure m)
        {
            m = default;
            MatrixD one = HeldItem.WeaponWorld(grip, aim, toolGrasp);
            Vector3D support = Vector3D.Transform((Vector3D)otherGrasp, one);
            Vector3D span = support - grip.Translation;
            double length = span.Length();
            if (!(length > 1e-3))
                return false;
            Vector3D off0 = off;
            if (held)
                off0 -= Vector3D.TransformNormal(takeOffset, one);
            m.One = one;
            m.Grip = grip.Translation;
            m.Support = support;
            m.Base = length;
            m.Along = span / length;
            m.Off = off0;
            Vector3D d = off0 - m.Grip;
            m.T = Vector3D.Dot(d, m.Along);
            m.Across = (d - m.T * m.Along).Length();
            m.Gap = Vector3D.Distance(off0, support);
            return true;
        }

        /// <summary>
        /// The turn, about the tool hand's grip, that lays the tool's grip line onto the line between the hands, by
        /// <paramref name="weight"/> of it (and less as the hands come together). <paramref name="lineAngle"/> is the whole angle
        /// between the two lines, radians.
        /// </summary>
        private static bool TryTurn(in Measure m, double weight, out MatrixD pivot, out double lineAngle)
        {
            pivot = MatrixD.Identity;
            lineAngle = 0.0;
            Vector3D d = m.Off - m.Grip;
            double length = d.Length();
            if (!(length > 1e-4))
                return false;
            Vector3D to = d / length;
            Vector3D axis = Vector3D.Cross(m.Along, to);
            double sin = axis.Length();
            lineAngle = Math.Atan2(sin, Vector3D.Dot(m.Along, to));
            // The separation fade is 0 before the other hand is a third of the way out, so the lines are never near opposite.
            double w = weight * Smooth(SeparationNone * m.Base, SeparationFull * m.Base, m.T) * Pull(m);
            if (!(w > 0.0) || !(sin > 1e-9))
                return false;
            pivot = MatrixD.CreateTranslation(-m.Grip) * MatrixD.CreateFromAxisAngle(axis / sin, lineAngle * w) * MatrixD.CreateTranslation(m.Grip);
            return true;
        }

        /// <summary>How much the other hand still aims the tool: all of it on the grip, easing off past <see cref="ReleaseOff"/> to none at <see cref="DriftNone"/>.</summary>
        private static double Pull(in Measure m) => 1.0 - Smooth(ReleaseOff, DriftNone, Math.Max(m.Across, Math.Abs(m.T - m.Base)));

        /// <summary>
        /// The other hand's grip in the world: tracked now, or for a moment where it last was on the tool hand. Placed by
        /// <paramref name="frame"/> when given (the tool hand's own, <see cref="HandWorld.TryGetIn"/>), else by this frame's head.
        /// </summary>
        private static bool TryOffHand(MyCharacter character, in MatrixD mainAim, bool allowGrace, MatrixD? frame, out Vector3D off)
        {
            MatrixD offGrip;
            bool tracked = frame.HasValue
                ? HandWorld.TryGetIn(character, OffHand, frame.Value, out offGrip, out _)
                : HandWorld.TryGetNow(character, OffHand, out offGrip, out _, out _);
            if (tracked)
            {
                off = offGrip.Translation;
                return true;
            }
            off = Vector3D.Zero;
            if (!allowGrace || !offInMainValid || Stopwatch.GetTimestamp() - offSeen > TrackingGrace * Second)
                return false;
            off = Vector3D.Transform(offInMain, mainAim);
            return true;
        }

        // ---- The tool hand's pose ----

        /// <summary>
        /// Turns the tool hand's grip and aim poses (as <see cref="HandTools.TryGetHand"/> gives them, the tool hand's own) by the
        /// two-handed turn, about the tool hand's grip; nothing when the tool is not held in both hands (nor easing back). The
        /// other hand is placed by <paramref name="frame"/> when given, the move the tool hand was placed by.
        /// </summary>
        internal static void Apply(MyCharacter character, ref MatrixD grip, ref MatrixD aim, MatrixD? frame = null)
        {
            if (!(aimProgress > 0.0) || !(aimShare > 0.0) || weapon == null || character == null || !ReferenceEquals(character.CurrentWeapon, weapon))
                return;
            try
            {
                if (!TryOffHand(character, aim, held, frame, out Vector3D off) || !TryMeasure(grip, aim, toolSpot, otherSpot, off, out Measure m))
                    return;
                if (TryTurn(m, Ease.Smooth01(aimProgress) * aimShare, out MatrixD pivot, out _))
                {
                    grip *= pivot;
                    aim *= pivot;
                }
            }
            catch (Exception e)
            {
                if (applyErrors++ < 3)
                    Log.Error(e, "Two-handed tools could not turn the tool hand's pose");
            }
        }

        // ---- The grip claim ----

        /// <summary>A grip has just started: it is ours when it is the other hand and that hand is on the held tool's grip.</summary>
        private static bool Take(Hand hand)
        {
            try
            {
                if (hand != OffHand || !guard.On || Reload.MagazineOut)
                    return false;
                MyCharacter character = MySession.Static?.LocalCharacter;
                if (!Live(character) || ToolbarWheel.Owns || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay)
                    || !TryItem(character, out _, out _, out Vector3 toolGrasp, out Vector3 otherGrasp))
                    return false;
                if (!HandWorld.TryGetNow(character, VRSettings.DominantHand, out MatrixD grip, out MatrixD aim, out _)
                    || !HandWorld.TryGetNow(character, hand, out MatrixD offGrip, out _, out _))
                    return false;
                // The grip where the tool is drawn: turned still, if it is easing back from a moment ago.
                Apply(character, ref grip, ref aim);
                if (!TryMeasure(grip, aim, toolGrasp, otherGrasp, offGrip.Translation, out Measure m))
                    return false;
                SupportZone.Place zone = SupportZone.At(character, character.CurrentWeapon as MyEntity, character.HandItemDefinition, offGrip.Translation);
                if (zone == SupportZone.Place.Grip)
                {
                    Note($"Two-hand: {hand} grip on the {Name(character.HandItemDefinition)}'s front grip, {SupportZone.LastToGrip * 100:0.0} cm from it " +
                         $"(reach {GrabReach * 100:0} cm, {GrabReach * GrabTolerance.Held * 100:0} cm once at it{(SupportZone.Current == SupportZone.Place.Grip ? ", as it was" : string.Empty)})");
                    return true;
                }
                // Near the tool but not on its grip: why the grip did something else instead, for the log.
                if (m.Gap <= DeclineNoted)
                    Note($"Two-hand: {hand} grip {m.Gap * 100:0.0} cm from the {Name(character.HandItemDefinition)}'s grip (reach {GrabReach * 100:0} cm" +
                         $"{(SupportZone.Current == SupportZone.Place.Grip ? $", {GrabReach * GrabTolerance.Held * 100:0} cm from the hover" : string.Empty)}): " +
                         $"{(zone == SupportZone.Place.Magazine ? "at the magazine instead" : "not taken")}");
                return false;
            }
            catch (Exception e)
            {
                if (takeErrors++ < 3)
                    Log.Error(e, "Two-handed tools could not decide on a grip");
                return false;
            }
        }

        // ---- Each frame ----

        /// <summary>Game thread, each simulation frame, after the character's own update (the tool is placed) and before the arms are solved.</summary>
        private static void AfterSimulation(MyCharacter __instance)
        {
            if (__instance != MySession.Static?.LocalCharacter || !guard.On)
                return;
            try
            {
                Step(__instance);
            }
            catch (Exception e)
            {
                held = false;
                aimProgress = handProgress = 0.0;
                supportValid = false;
                guard.Failed(e, "Two-handed tools failed");
            }
        }

        private static void Step(MyCharacter character)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = lastStep == 0 ? 0.0 : Math.Min(0.1, (now - lastStep) / (double)Second);
            lastStep = now;
            Hand off = OffHand;
            // Asking every frame keeps the claim current: it forgets a grip only when asked once it is let go.
            bool holds = GripClaim.Holds(off, ClaimName);
            // The trigger holds the tool as the grip does (a hand's secondary action is given up while it is on the tool).
            bool trigger = VRInput.IsPressed(off, VRButtons.Trigger);
            bool triggerNew = trigger && !triggerWas;
            triggerWas = trigger;
            if (held && byTrigger)
                holds = trigger;

            bool live = Live(character);
            MyEntity tool = null;
            MyHandItemDefinition item = null;
            Vector3 toolGrasp = Vector3.Zero, otherGrasp = Vector3.Zero;
            bool hasGrip = live && TryItem(character, out tool, out item, out toolGrasp, out otherGrasp);
            MatrixD grip = MatrixD.Identity, aim = MatrixD.Identity;
            bool main = hasGrip && HandWorld.TryGetNow(character, VRSettings.DominantHand, out grip, out aim, out _);

            bool offTracked = false, offOk = false;
            Vector3D offPos = Vector3D.Zero;
            if (main)
            {
                offTracked = HandWorld.TryGetNow(character, off, out MatrixD offGrip, out _, out _);
                if (offTracked)
                {
                    offPos = offGrip.Translation;
                    offInMain = Vector3D.Transform(offPos, MatrixD.Invert(aim));
                    offInMainValid = true;
                    offSeen = now;
                    offOk = true;
                }
                else
                {
                    offOk = TryOffHand(character, aim, held, null, out offPos);
                }
            }
            Measure m = default;
            bool measured = offOk && TryMeasure(grip, aim, toolGrasp, otherGrasp, offPos, out m);

            if (held)
            {
                string why = null;
                if (!holds)
                    why = byTrigger ? "the trigger let go" : "the grip let go";
                else if (!live)
                    why = Off || !VRSettings.TwoHandedTools || HandTools.Off ? "switched off" : "no longer on foot";
                else if (!ReferenceEquals(character.CurrentWeapon, weapon))
                    why = character.CurrentWeapon == null ? "the tool was put away" : "the tool was switched";
                else if (!main)
                    why = "the tool hand is not tracked";
                else if (!offOk)
                    why = $"the {off.ToString().ToLowerInvariant()} hand lost tracking";
                else if (!Stays && measured && (m.Across > ReleaseOff || Math.Abs(m.T - m.Base) > ReleaseOff))
                    why = $"pulled {m.Across * 100:0} cm across and {(m.T - m.Base) * 100:+0;-0} cm along the grip line";
                if (why != null)
                    LetGo(off, holds, why);
            }
            else if (holds)
            {
                byTrigger = false;
                if (main && measured)
                    Engage(off, tool, item, toolGrasp, otherGrasp, m);
                else
                    GripClaim.Release(off, ClaimName);
            }
            else if (triggerNew && live && hasGrip && main && measured && !Reload.MagazineOut
                     && GripClaim.Owner(off) == null // a hand already holding a hull, ladder, item or the helmet by its grip is busy
                     && !ToolbarWheel.Owns && MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay
                     && SupportZone.At(character, tool, item, offPos) == SupportZone.Place.Grip)
            {
                byTrigger = true;
                VRControls.SwallowTrigger(off, "the trigger holding the tool in both hands");
                Note($"Two-hand: {off} trigger on the {Name(item)}'s front grip, {SupportZone.LastToGrip * 100:0.0} cm from its spot");
                Engage(off, tool, item, toolGrasp, otherGrasp, m);
            }

            // A tool that went away takes the aim with it: the next one starts in one hand.
            if (!ReferenceEquals(character.CurrentWeapon, weapon))
                aimProgress = 0.0;
            aimProgress = Ease.Approach(aimProgress, held ? 1.0 : 0.0, dt / (held ? GrabSeconds : ReleaseSeconds));
            handProgress = Ease.Approach(handProgress, held ? 1.0 : 0.0, dt / (held ? GrabSeconds : HandReturnSeconds));

            // The palm's place on the tool as placed this frame, in the body's own space (kept as it was while the hand
            // returns from a tool that went away).
            if (TakeSupport(character))
                supportValid = true;
            else if (!(handProgress > 0.0))
            {
                supportValid = false;
            }

            // The zone the other hand hovers in: none while the tool is held in both hands, reloading, or the hand is busy.
            // Two-handed tools off, a gun's magazine is still a zone (Reload).
            MyEntity inHand = character.CurrentWeapon as MyEntity;
            bool onFoot = VRInput.Active && HandTools.IsLocalOnFoot(character) && !character.IsOnLadder && !HandTools.Off;
            Vector3D zoneOff = offPos;
            bool zoneTracked = offTracked;
            if (!offTracked && onFoot && inHand != null && HandWorld.TryGetNow(character, off, out MatrixD zoneGrip, out _, out _))
            {
                zoneOff = zoneGrip.Translation;
                zoneTracked = true;
            }
            bool hovering = onFoot && !held && zoneTracked && !Reload.MagazineOut && inHand != null && !character.CurrentWeapon.IsReloading
                            && !ToolbarWheel.Owns && !VRInput.IsPressed(off, VRButtons.Grip) && MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay;
            SupportZone.Update(character, hovering, inHand, character.HandItemDefinition, zoneOff, off);

            bool near = false;
            if (held)
                Trace(character, m, measured, aim, now);
            else if (!Reload.MagazineOut)
            {
                near = measured && offTracked && main && m.Gap <= NearGrip;
                if (near && !ReferenceEquals(nearGripOf, item))
                    LogNearGrip(character, off, item, m);
            }
            nearGripOf = near ? item : null;
        }

        /// <summary>Once as the other hand comes within <see cref="NearGrip"/> of the held tool's second grip: how far, and where the grip is in the STAGE space (the floor's).</summary>
        private static void LogNearGrip(MyCharacter character, Hand off, MyHandItemDefinition item, in Measure m)
        {
            if (!(OpenXR.XrSession.LocalInStage is Matrix stage) || !HandWorld.TryGetTrackingToBody(character, out MatrixD trackingToModel, out _))
                return;
            Vector3D at = Vector3D.Transform(m.Support, MatrixD.Invert(character.WorldMatrix) * MatrixD.Invert(trackingToModel) * (MatrixD)stage);
            Log.Info($"Two-hand: {off} hand within {NearGrip * 100:0} cm of the {Name(item)}'s grip ({m.Gap * 100:0.0} cm); the grip at stage ({at.X:F3}, {at.Y:F3}, {at.Z:F3}) m");
        }

        /// <summary>The palm's place on the tool (its LeftHand, <see cref="ItemSpots.Other"/>) from where the tool is now, in the body's own space; false while the hand is not on it.</summary>
        private static bool TakeSupport(MyCharacter character)
        {
            if (!(handProgress > 0.0) || weapon == null || heldItem == null || !ReferenceEquals(character.CurrentWeapon, weapon) || weapon.Closed)
                return false;
            supportModel = (Matrix)((MatrixD)ItemSpots.Other(heldItem) * weapon.WorldMatrix * character.PositionComp.WorldMatrixNormalizedInv);
            return true;
        }

        /// <summary>
        /// The held tool was placed again after <see cref="Step"/>, in the frame the arms are solved in
        /// (<see cref="HeldItem.PlaceForArms"/>): the other palm's place on it is taken again, so that hand goes where the tool is drawn.
        /// </summary>
        internal static void ToolPlaced(MyCharacter character)
        {
            if (character == null || !guard.On)
                return;
            try
            {
                if (TakeSupport(character))
                    supportValid = true;
            }
            catch (Exception e)
            {
                guard.Failed(e, "Two-handed tools could not take the grip from the placed tool");
            }
        }

        private static void Engage(Hand off, MyEntity tool, MyHandItemDefinition item, Vector3 toolGrasp, Vector3 otherGrasp, in Measure m)
        {
            takeOffset = Vector3D.TransformNormal(m.Off - m.Support, MatrixD.Transpose(m.One));
            if (!ReferenceEquals(tool, weapon))
                aimProgress = 0.0;
            held = true;
            weapon = tool;
            heldItem = item;
            toolSpot = toolGrasp;
            otherSpot = otherGrasp;
            aimShare = AimShare(ItemSpots.Tool(item).Translation, ItemSpots.Other(item).Translation);
            traceAt = Stopwatch.GetTimestamp();
            Haptics.Pulse(Hand.Left, GrabAmplitude, GrabPulseSeconds);
            Haptics.Pulse(Hand.Right, GrabAmplitude, GrabPulseSeconds);
            Note($"Two-hand: {off} hand took the {Name(item)} by its grip ({m.Gap * 100:0.0} cm from it, hands {Vector3D.Distance(m.Off, m.Grip) * 100:0.0} cm apart, " +
                 $"grip {m.Base * 100:0.0} cm from the tool hand; aims by both hands {aimShare * 100:0}%; " +
                 (byTrigger ? "stays on until the trigger opens)" : Stays ? "stays on until the grip opens)" : $"lets go {ReleaseOff * 100:0} cm off the grip line)"));
        }

        /// <summary>The other hand is needed elsewhere (<see cref="Reload"/>: the magazine came out): it lets go of the tool's grip now.</summary>
        internal static void LetGoFor(string why)
        {
            if (held)
                LetGo(OffHand, GripClaim.Holds(OffHand, ClaimName), why);
        }

        private static void LetGo(Hand off, bool holds, string why)
        {
            held = false;
            byTrigger = false;
            if (holds)
                GripClaim.Release(off, ClaimName);
            Haptics.Pulse(off, LetGoAmplitude, LetGoSeconds);
            Note($"Two-hand: {off} hand let go of the {Name(heldItem)} ({why})");
        }

        /// <summary>Once a second while held: how far the line between the hands turns the tool, and where its sensors point.</summary>
        private static void Trace(MyCharacter character, in Measure m, bool measured, in MatrixD aim, long now)
        {
            if (!measured || now - traceAt < Second)
                return;
            traceAt = now;
            Vector3D sensor = character.WeaponPosition?.LogicalOrientationWorld ?? Vector3D.Zero;
            Vector3D own = Vector3D.Normalize(aim.Forward);
            double turned = sensor.LengthSquared() > 1e-9 ? MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(own, Vector3D.Normalize(sensor)), -1.0, 1.0))) : double.NaN;
            TryTurn(m, 1.0, out _, out double line);
            Note($"Two-hand: {Name(heldItem)} held, the hands' line is {MathHelper.ToDegrees(line):0.0} deg off the tool hand's grip line, " +
                 $"the sensors aim {turned:0.0} deg off the tool hand (share {aimShare * Ease.Smooth01(aimProgress) * Pull(m):0.00}); " +
                 $"sensor forward ({sensor.X:F3}, {sensor.Y:F3}, {sensor.Z:F3}), other hand {m.Across * 100:0.0} cm across and {(m.T - m.Base) * 100:+0.0;-0.0;0.0} cm along" +
                 OffArmText());
        }

        /// <summary>The other arm on the grip as last drawn (<see cref="HandIK.OffOnTool"/>), for the trace.</summary>
        private static string OffArmText()
        {
            HandIK.OffArm arm = HandIK.OffOnTool;
            if (!(arm.OnTool > 0f))
                return "";
            return $"; the {OffHand.ToString().ToLowerInvariant()} arm x{arm.Stretch:F3}, the drawn palm {arm.Spot * 100f:0.0} cm and {arm.SpotTurn:0.0} deg off the game's grip spot " +
                   $"(the wrist {arm.Short * 100f:0.0} cm off its target), the hand pointing {arm.PointOff:0} deg off the grip, " +
                   $"the wrist bent {arm.WristBend:0} deg, the forearm twisted {arm.Twist:+0;-0} deg, the hand turned {RollDegrees:+0;-0} deg round the handle";
        }

        // ---- The arm ----

        /// <summary>
        /// For the arm solver (<see cref="HandIK"/>): moves the other palm's target onto the tool's grip, as far as the hand has
        /// settled there, in the character's model space, and returns that share (0: not on the tool) and the grip's place.
        /// The arm stays on the grip by that share (<see cref="ArmSolver.Settings.Pin"/>, <see cref="ArmSolver.Settings.PinAt"/>):
        /// only the grip is pinned, not the way back to the real hand, so letting go of a grip at the arm's limit eases the arm
        /// from the stretch it held to its normal limit as the hand returns, never past it. Only the arm of the hand that
        /// does not hold the tool (<see cref="OffHand"/>).
        /// </summary>
        internal static float Settle(Hand hand, ref Matrix palm, out Vector3 grip)
        {
            grip = Vector3.Zero;
            if (hand != OffHand)
                return 0f;
            if (!supportValid || !(handProgress > 0.0))
            {
                rolled = false;
                return 0f;
            }
            float w = (float)Ease.Smooth01(handProgress);
            Matrix onGrip = OnGrip(supportModel);
            Quaternion from = Quaternion.CreateFromRotationMatrix(palm), to = Quaternion.CreateFromRotationMatrix(onGrip);
            Matrix settled = Matrix.CreateFromQuaternion(Quaternion.Slerp(from, to, w));
            settled.Translation = Vector3.Lerp(palm.Translation, onGrip.Translation, w);
            palm = settled;
            grip = onGrip.Translation;
            return w;
        }

        /// <summary>The other hand's turn round the handle it holds, degrees (<see cref="OnGrip"/>).</summary>
        internal static float RollDegrees => MathHelper.ToDegrees(roll);

        /// <summary>The game's own spot for the other palm on the held tool (its LeftHand, <see cref="ItemSpots.Other"/>), as placed this frame, model space.</summary>
        internal static bool GripSpot(out Matrix spot)
        {
            spot = supportModel;
            return supportValid && handProgress > 0.0;
        }

        /// <summary>
        /// The palm on the tool's grip: the game's LeftHand spot (<see cref="ItemSpots.Other"/>), in place and in turn, whatever the other controller does
        /// (the controller only takes and lets go of the grip, and aims the tool, <see cref="TryTurn"/>). The spot also says
        /// which way the handle runs: across the palm (the palm bone's Z) through the middle of the closed hand
        /// (<see cref="HandGrip.HandleInPalm"/>). For a look at the wrist (debug only, off by default): with
        /// <see cref="RollMost"/> set the hand turns round that line toward the way its forearm carried it at the last solve
        /// (<see cref="HandIK.OffCarried"/>, where the wrist would be straight), up to <see cref="RollMost"/> each way and
        /// <see cref="RollStep"/> a frame once on the grip, the closed hand staying on the handle; and
        /// <see cref="SlideDebug"/> moves it along the handle.
        /// </summary>
        private static Matrix OnGrip(in Matrix spot)
        {
            Vector3 axis = new Vector3(spot.M31, spot.M32, spot.M33);
            if (axis.LengthSquared() < 1e-8f)
                return spot;
            axis.Normalize();
            float most = MathHelper.ToRadians(Math.Max(0f, RollMost));
            if (!(most > 0f))
            {
                roll = 0f;
            }
            else if (HandIK.OffCarried(out Vector3 carried))
            {
                Vector3 x = spot.Right - axis * Vector3.Dot(spot.Right, axis), c = carried - axis * Vector3.Dot(carried, axis);
                if (c.LengthSquared() > RollFloor && x.LengthSquared() > 1e-6f)
                {
                    float want = (float)Math.Atan2(Vector3.Dot(axis, Vector3.Cross(x, c)), Vector3.Dot(x, c));
                    // Whichever way the matrix turns, take the way that brings the palm's X round onto the forearm's.
                    if (Vector3.Dot(Vector3.TransformNormal(x, Matrix.CreateFromAxisAngle(axis, -want)), c) >
                        Vector3.Dot(Vector3.TransformNormal(x, Matrix.CreateFromAxisAngle(axis, want)), c))
                        want = -want;
                    want = MathHelper.Clamp(want, -most, most);
                    roll = rolled ? roll + MathHelper.Clamp(want - roll, -RollStep, RollStep) : want;
                }
            }
            roll = MathHelper.Clamp(roll, -most, most);
            rolled = true;

            Matrix held = spot;
            if (roll != 0f)
            {
                Vector3 pivot = Vector3.Transform(HandGrip.HandleInPalm(OffHand == Hand.Left), spot);
                held = spot * Matrix.CreateTranslation(-pivot) * Matrix.CreateFromAxisAngle(axis, roll) * Matrix.CreateTranslation(pivot);
            }
            if (SlideDebug != 0f)
                held.Translation += axis * SlideDebug;
            return held;
        }

        // ---- Helpers ----

        private static double Smooth(double from, double to, double x) =>
            to > from ? Ease.Smooth01((x - from) / (to - from)) : x >= to ? 1.0 : 0.0;

        private static string Name(MyHandItemDefinition item) => item?.Id.SubtypeName ?? "tool";

        private static void Note(string line) => notes.Info(line);
    }
}
