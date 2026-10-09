using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Tracking;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The held tool or gun taken in both hands. The other hand closes its grip on the spot where the game's own animation
    /// puts the astronaut's left hand (the item definition's LeftHand, as <see cref="HeldItem"/> uses its RightHand for the
    /// tool hand), and from then on the tool points along the line between the two hands: the item's own line from its
    /// RightHand spot to its LeftHand spot is turned onto the line from the tool hand's grip to the other hand's grip, by
    /// the smallest turn that does it, about the tool hand's grip, so the tool hand keeps its place on the tool and the
    /// roll about that line is the tool hand's. The turn goes into the tool hand's pose itself
    /// (<see cref="HandTools.TryGetHand"/>), so the item's placement, its sensors, the shot direction and the use ray all
    /// follow it through the paths they already take. The other arm settles onto the tool's grip (<see cref="Settle"/>).
    /// </summary>
    /// <remarks>
    /// <para>Grab: a grip pressed with the other hand within <see cref="GrabReach"/> of the tool's grip, claimed through
    /// <see cref="GripClaim"/> at <see cref="ClaimPriority"/>, ahead of the holsters and the helmet, floating items, grab to use
    /// and hulls: the hand is on the tool it holds, the one thing it is surely reaching for. Anywhere else the claim
    /// declines and the grip keeps its old meaning (the toolbar wheel on foot). A light tick on the other hand marks it coming
    /// into reach; the grab is a tick on both hands.</para>
    /// <para>Steady: the turn only depends on where the two grips are (no world up is used, so no flip when the hands line up
    /// with it), it fades out as the other hand comes back toward the tool hand (<see cref="SeparationNone"/>), and it only
    /// counts as far as the item's grip line runs along the tool (<see cref="AimShare"/>): a pistol's support hand cups the
    /// tool hand from the side, a line that would roll the gun rather than aim it, so a pistol is steadied but aims with the
    /// tool hand.</para>
    /// <para>Release: letting go, or the other hand more than <see cref="ReleaseOff"/> off the grip line (the line from the tool
    /// hand's grip through the tool's grip as the tool hand alone holds it, measured across it and along it); also putting
    /// the tool away or switching it, leaving the feet, and the other hand untracked for longer than <see cref="TrackingGrace"/>.
    /// The aim eases back to the tool hand and the arm back to its controller.</para>
    /// <para>The settle is for the right-handed rig only: the game's character holds items in its right hand, so with
    /// Left-handed on the aim and the claim work for the right controller but the astronaut's arms stay as they were.</para>
    /// </remarks>
    internal static class TwoHand
    {
        /// <summary>Debug: no two-handed hold (<see cref="Rendering.RenderDebug"/> TwoHand=0); the other grip opens the wheel as before.</summary>
        public static bool Off { get; set; }

        /// <summary>The name the grip is claimed under (<see cref="GripClaim"/>).</summary>
        public const string ClaimName = "TwoHand";

        /// <summary>Asked before the holsters (10): a hand on the tool's own grip is reaching for the tool, not the helmet behind it.</summary>
        public const int ClaimPriority = 5;

        /// <summary>A grip this close to the tool's grip (metres, from the other controller's grip) takes it.</summary>
        internal const double GrabReach = 0.10;

        /// <summary>The other hand this far off the grip line (metres, across it or along it) lets go.</summary>
        internal const double ReleaseOff = 0.15;

        /// <summary>The other hand untracked this long (seconds) lets go; until then it is taken where it last was on the tool hand.</summary>
        internal const double TrackingGrace = 0.25;

        /// <summary>Seconds for the hand to settle onto the grip and the aim to come to both hands.</summary>
        internal const double GrabSeconds = 0.08;

        /// <summary>Seconds for the aim to ease back to the tool hand, and for the arm to go back to its controller.</summary>
        internal const double ReleaseSeconds = 0.15, HandReturnSeconds = 0.10;

        /// <summary>The grab: a firm tick on both hands.</summary>
        public const float GrabAmplitude = 0.45f, GrabPulseSeconds = 0.03f;

        /// <summary>The other hand comes into reach of the grip: light, like a holster's.</summary>
        public const float HintAmplitude = 0.2f, HintSeconds = 0.02f;

        /// <summary>Let go: the lightest tick on the other hand.</summary>
        public const float LetGoAmplitude = 0.15f, LetGoSeconds = 0.015f;

        /// <summary>Once in reach, the hint holds this much further out, so the edge does not tick over and over.</summary>
        private const double HintSlack = 1.3;

        private const double HintEverySeconds = 0.25;

        /// <summary>
        /// How much of the tool's grip line has to run along the tool (the share of it along the tool's forward) for the line to
        /// aim it: a rifle 0.95, a hand drill and a welder 0.85, a grinder 0.75 (its side handle), a pistol 0.37 (the support
        /// hand under and beside the tool hand). From none at the first to all of it at the second.
        /// </summary>
        private const double AimShareNone = 0.45, AimShareFull = 0.65;

        /// <summary>The turn fades out as the other hand comes back along the line toward the tool hand: none at this share of the grip's distance, all of it at the second.</summary>
        private const double SeparationNone = 0.3, SeparationFull = 0.6;

        /// <summary>Two grips closer than this on an item (metres) are not two places to hold it.</summary>
        private const double ShortestGrip = 0.03;

        private static readonly long Second = Stopwatch.Frequency;

        // ---- State (game thread) ----

        /// <summary>The other hand holds the tool's grip now.</summary>
        private static bool held;

        /// <summary>The tool held (or, while the aim eases back, last held) in both hands, and its grip spots in its own space.</summary>
        private static MyEntity weapon;
        private static MyHandItemDefinition heldItem;
        private static Vector3 rightSpot, leftSpot;

        /// <summary>How far the tool's grip line aims it (<see cref="AimShare"/>), for the item held.</summary>
        private static double aimShare;

        /// <summary>0 to 1: the aim from both hands, and the other arm on the tool's grip; linear, eased where read.</summary>
        private static double aimProgress, handProgress;

        /// <summary>The other palm's place on the tool, in the character's model space, for the arm (<see cref="Settle"/>).</summary>
        private static Matrix supportModel;
        private static bool supportValid;

        /// <summary>The other hand's grip in the tool hand's aim space, as last tracked, for a moment of lost tracking.</summary>
        private static Vector3D offInMain;
        private static bool offInMainValid;
        private static long offSeen;

        private static bool hinted;
        private static long hintAt, lastStep, traceAt;

        private static bool hooked;
        private static int errors, applyErrors, takeErrors;
        private static long noteWindow;
        private static int notes, skipped;

        /// <summary>The hand that takes the tool's other grip: the one that does not hold it.</summary>
        internal static Hand OffHand => VRSettings.DominantHand == Hand.Right ? Hand.Left : Hand.Right;

        // ---- Hooks ----

        /// <summary>Registers the grip claim and the per-frame update; the update runs before the arms are solved (<see cref="HandIK"/>).</summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                GripClaim.Register(ClaimName, ClaimPriority, Take);
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdateAfterSimulation)),
                    postfix: new HarmonyMethod(typeof(TwoHand), nameof(AfterSimulation)) { priority = Priority.High });
                hooked = true;
                Log.Info($"Two-handed tools: hooked (grip claim at priority {ClaimPriority}, reach {GrabReach * 100:0} cm, release {ReleaseOff * 100:0} cm off the grip line)");
            }
            catch (Exception e)
            {
                hooked = false;
                Log.Error(e, "Two-handed tools could not be hooked; tools stay in one hand and the other grip keeps its old job");
            }
        }

        /// <summary>Two-handed holds are in play for this character: on foot, not up a ladder, with the feature on.</summary>
        private static bool Live(MyCharacter character) =>
            hooked && !Off && VRSettings.TwoHandedTools && !HandTools.Off && VRInput.Active && HandTools.IsLocalOnFoot(character)
            && !character.IsOnLadder && !character.Closed && character.PositionComp != null;

        /// <summary>
        /// The tool in hand and its two grip spots (the item definition's RightHand and LeftHand translations, in the item's
        /// own space). False with nothing in hand, for the block builder (a ray, not a tool), and for an item with no
        /// LeftHand spot.
        /// </summary>
        private static bool TryItem(MyCharacter character, out MyEntity tool, out MyHandItemDefinition item, out Vector3 right, out Vector3 left)
        {
            tool = character.CurrentWeapon as MyEntity;
            item = character.HandItemDefinition;
            right = left = Vector3.Zero;
            if (tool == null || item == null || tool is MyBlockPlacerBase)
                return false;
            right = item.RightHand.Translation;
            left = item.LeftHand.Translation;
            return left.LengthSquared() > 1e-8f && (left - right).Length() >= ShortestGrip;
        }

        /// <summary>How far an item's grip line aims it: the share of the line from its RightHand to its LeftHand that runs along its forward (-Z), eased from none to all.</summary>
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
        }

        private static bool TryMeasure(in MatrixD grip, in MatrixD aim, Vector3 right, Vector3 left, in Vector3D off, out Measure m)
        {
            m = default;
            MatrixD one = HeldItem.WeaponWorld(grip, aim, right);
            Vector3D support = Vector3D.Transform((Vector3D)left, one);
            Vector3D span = support - grip.Translation;
            double length = span.Length();
            if (!(length > 1e-3))
                return false;
            m.Grip = grip.Translation;
            m.Support = support;
            m.Base = length;
            m.Along = span / length;
            m.Off = off;
            Vector3D d = off - m.Grip;
            m.T = Vector3D.Dot(d, m.Along);
            m.Across = (d - m.T * m.Along).Length();
            m.Gap = Vector3D.Distance(off, support);
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
            double w = weight * Smooth(SeparationNone * m.Base, SeparationFull * m.Base, m.T);
            if (!(w > 0.0) || !(sin > 1e-9))
                return false;
            pivot = MatrixD.CreateTranslation(-m.Grip) * MatrixD.CreateFromAxisAngle(axis / sin, lineAngle * w) * MatrixD.CreateTranslation(m.Grip);
            return true;
        }

        /// <summary>The other hand's grip in the world: tracked now, or for a moment where it last was on the tool hand.</summary>
        private static bool TryOffHand(MyCharacter character, in MatrixD mainAim, bool allowGrace, out Vector3D off)
        {
            if (HandWorld.TryGetNow(character, OffHand, out MatrixD offGrip, out _, out _))
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
        /// two-handed turn, about the tool hand's grip; nothing when the tool is not held in both hands (nor easing back).
        /// </summary>
        internal static void Apply(MyCharacter character, ref MatrixD grip, ref MatrixD aim)
        {
            if (!(aimProgress > 0.0) || !(aimShare > 0.0) || weapon == null || character == null || !ReferenceEquals(character.CurrentWeapon, weapon))
                return;
            try
            {
                if (!TryOffHand(character, aim, held, out Vector3D off) || !TryMeasure(grip, aim, rightSpot, leftSpot, off, out Measure m))
                    return;
                if (TryTurn(m, Smooth01(aimProgress) * aimShare, out MatrixD pivot, out _))
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
                if (hand != OffHand || errors >= 3)
                    return false;
                MyCharacter character = MySession.Static?.LocalCharacter;
                if (!Live(character) || ToolbarWheel.Owns || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay)
                    || !TryItem(character, out _, out _, out Vector3 right, out Vector3 left))
                    return false;
                if (!HandWorld.TryGetNow(character, VRSettings.DominantHand, out MatrixD grip, out MatrixD aim, out _)
                    || !HandWorld.TryGetNow(character, hand, out MatrixD offGrip, out _, out _))
                    return false;
                // The grip where the tool is drawn: turned still, if it is easing back from a moment ago.
                Apply(character, ref grip, ref aim);
                return TryMeasure(grip, aim, right, left, offGrip.Translation, out Measure m) && m.Gap <= GrabReach;
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
            if (errors >= 3 || __instance != MySession.Static?.LocalCharacter)
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
                if (errors++ < 3)
                    Log.Error(e, "Two-handed tools failed" + (errors >= 3 ? "; they stay off for this run" : string.Empty));
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

            bool live = Live(character);
            MyEntity tool = null;
            MyHandItemDefinition item = null;
            Vector3 right = Vector3.Zero, left = Vector3.Zero;
            bool hasGrip = live && TryItem(character, out tool, out item, out right, out left);
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
                    offOk = TryOffHand(character, aim, held, out offPos);
                }
            }
            Measure m = default;
            bool measured = offOk && TryMeasure(grip, aim, right, left, offPos, out m);

            if (held)
            {
                string why = null;
                if (!holds)
                    why = "the grip let go";
                else if (!live)
                    why = Off || !VRSettings.TwoHandedTools || HandTools.Off ? "switched off" : "no longer on foot";
                else if (!ReferenceEquals(character.CurrentWeapon, weapon))
                    why = character.CurrentWeapon == null ? "the tool was put away" : "the tool was switched";
                else if (!main)
                    why = "the tool hand is not tracked";
                else if (!offOk)
                    why = $"the {off.ToString().ToLowerInvariant()} hand lost tracking";
                else if (measured && (m.Across > ReleaseOff || Math.Abs(m.T - m.Base) > ReleaseOff))
                    why = $"pulled {m.Across * 100:0} cm across and {(m.T - m.Base) * 100:+0;-0} cm along the grip line";
                if (why != null)
                    LetGo(off, holds, why);
            }
            else if (holds)
            {
                if (main && measured)
                    Engage(off, tool, item, right, left, m);
                else
                    GripClaim.Release(off, ClaimName);
            }

            // A tool that went away takes the aim with it: the next one starts in one hand.
            if (!ReferenceEquals(character.CurrentWeapon, weapon))
                aimProgress = 0.0;
            aimProgress = Approach(aimProgress, held ? 1.0 : 0.0, dt / (held ? GrabSeconds : ReleaseSeconds));
            handProgress = Approach(handProgress, held ? 1.0 : 0.0, dt / (held ? GrabSeconds : HandReturnSeconds));

            // The palm's place on the tool as placed this frame, in the body's own space (kept as it was while the hand
            // returns from a tool that went away).
            if (handProgress > 0.0 && weapon != null && heldItem != null && ReferenceEquals(character.CurrentWeapon, weapon) && !weapon.Closed)
            {
                supportModel = (Matrix)((MatrixD)heldItem.LeftHand * weapon.WorldMatrix * character.PositionComp.WorldMatrixNormalizedInv);
                supportValid = true;
            }
            else if (!(handProgress > 0.0))
            {
                supportValid = false;
            }

            if (held)
                Trace(character, m, measured, aim, now);
            else
                Hint(off, item, m, measured && offTracked && main, now);
        }

        private static void Engage(Hand off, MyEntity tool, MyHandItemDefinition item, Vector3 right, Vector3 left, in Measure m)
        {
            if (!ReferenceEquals(tool, weapon))
                aimProgress = 0.0;
            held = true;
            weapon = tool;
            heldItem = item;
            rightSpot = right;
            leftSpot = left;
            aimShare = AimShare(right, left);
            hinted = true;
            traceAt = Stopwatch.GetTimestamp();
            Haptics.Pulse(Hand.Left, GrabAmplitude, GrabPulseSeconds);
            Haptics.Pulse(Hand.Right, GrabAmplitude, GrabPulseSeconds);
            Note($"Two-hand: {off} hand took the {Name(item)} by its grip ({m.Gap * 100:0.0} cm from it, hands {Vector3D.Distance(m.Off, m.Grip) * 100:0.0} cm apart, " +
                 $"grip {m.Base * 100:0.0} cm from the tool hand; aims by both hands {aimShare * 100:0}%)");
        }

        private static void LetGo(Hand off, bool holds, string why)
        {
            held = false;
            if (holds)
                GripClaim.Release(off, ClaimName);
            Haptics.Pulse(off, LetGoAmplitude, LetGoSeconds);
            Note($"Two-hand: {off} hand let go of the {Name(heldItem)} ({why})");
        }

        /// <summary>A light tick as the other hand comes within reach of the held tool's grip, while it is not gripping anything.</summary>
        private static void Hint(Hand off, MyHandItemDefinition item, in Measure m, bool measured, long now)
        {
            if (!measured)
            {
                hinted = false;
                return;
            }
            // A grip held (the wheel, an item, a grip that was let go of the tool) leaves the hint as it is.
            if (VRInput.IsPressed(off, VRButtons.Grip) || !(MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay))
                return;
            bool near = m.Gap <= GrabReach * (hinted ? HintSlack : 1.0);
            if (near && !hinted && now - hintAt > HintEverySeconds * Second)
            {
                hintAt = now;
                Haptics.Pulse(off, HintAmplitude, HintSeconds);
                Note($"Two-hand: {off} hand in reach of the {Name(item)}'s grip ({m.Gap * 100:0.0} cm)");
            }
            hinted = near;
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
                 $"the sensors aim {turned:0.0} deg off the tool hand (share {aimShare * Smooth01(aimProgress):0.00}); " +
                 $"sensor forward ({sensor.X:F3}, {sensor.Y:F3}, {sensor.Z:F3}), other hand {m.Across * 100:0.0} cm across and {(m.T - m.Base) * 100:+0.0;-0.0} cm along");
        }

        // ---- The arm ----

        /// <summary>
        /// For the arm solver (<see cref="HandIK"/>): moves the other palm's target onto the tool's grip, as far as the hand has
        /// settled there, in the character's model space. Only the left arm of a right-handed player (the rig holds items in
        /// its right hand).
        /// </summary>
        internal static bool Settle(Hand hand, ref Matrix palm)
        {
            if (!supportValid || !(handProgress > 0.0) || hand != Hand.Left || VRSettings.DominantHand != Hand.Right)
                return false;
            float w = (float)Smooth01(handProgress);
            Quaternion from = Quaternion.CreateFromRotationMatrix(palm), to = Quaternion.CreateFromRotationMatrix(supportModel);
            Matrix settled = Matrix.CreateFromQuaternion(Quaternion.Slerp(from, to, w));
            settled.Translation = Vector3.Lerp(palm.Translation, supportModel.Translation, w);
            palm = settled;
            return true;
        }

        // ---- Helpers ----

        private static double Approach(double value, double target, double step) =>
            value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

        private static double Smooth01(double x)
        {
            x = MathHelper.Clamp(x, 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }

        private static double Smooth(double from, double to, double x) =>
            to > from ? Smooth01((x - from) / (to - from)) : x >= to ? 1.0 : 0.0;

        private static string Name(MyHandItemDefinition item) => item?.Id.SubtypeName ?? "tool";

        private static void Note(string line)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - noteWindow > Second)
            {
                noteWindow = now;
                notes = 0;
            }
            if (notes >= 6)
            {
                skipped++;
                return;
            }
            notes++;
            Log.Info(skipped > 0 ? $"{line} ({skipped} lines skipped before this)" : line);
            skipped = 0;
        }
    }
}
