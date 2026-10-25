// Adapted from ColbyJack1134's SpaceEngineersVR (Player/WeaponHandling.cs: the support hand's hover; Player/Shared/WeaponPose.cs:
// WeaponProfile.Grab, ReloadAt and HoverHysteresis; Player/CockpitFeedback.cs: Hover), with his permission.
using System.Diagnostics;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Common;
using SpaceEngineersVR.Input;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// Where the other hand (the one not holding the tool or gun) is on the held item: at its front grip, at its magazine, or
    /// at neither. The two are told apart before anything commits: the hand "hovers" in a zone, a light tick on that hand
    /// says it has come into one, and only then does a grip there take the two-handed hold (<see cref="TwoHand"/>), or a grip
    /// or trigger at the magazine reload (<see cref="Reload"/>).
    /// </summary>
    /// <remarks>
    /// <para>Which zone: the magazine wins when the hand is nearer it than the front grip, and always on a pistol, whose support
    /// hand cups the grip from the side right by its magazine (a pistol's magazine zone is only below its base, so a hand
    /// cupping the grip is not in it). Otherwise the front grip, within <see cref="TwoHand.GrabReach"/> of the item's
    /// left-hand spot.</para>
    /// <para>The zone the hand is already in counts <see cref="Hysteresis"/> times as far, so a hand resting on a boundary
    /// does not flicker between zones.</para>
    /// <para>No zone while the item is already held in both hands, while the game is reloading it or the magazine is out
    /// (<see cref="Reload.MagazineOut"/>), and with a screen or the tools wheel open.</para>
    /// </remarks>
    internal static class SupportZone
    {
        internal enum Place { None, Grip, Magazine }

        /// <summary>The zone already hovered keeps a larger reach (<see cref="GrabTolerance.Held"/>), for the tick and for the grip that follows it.</summary>
        internal const float Hysteresis = (float)GrabTolerance.Held;

        /// <summary>Coming into a zone: the lightest tick on the other hand, at most this often.</summary>
        public const float HoverAmplitude = 0.12f, HoverSeconds = 0.012f;
        private const double HoverEverySeconds = 0.1;

        private static Place current;
        private static MyEntity currentOf;
        private static long hoverAt;
        private static readonly LineThrottle notes = new LineThrottle(6);

        /// <summary>The zone the other hand hovers in now, on <see cref="CurrentOf"/>; set each frame by <see cref="Update"/>.</summary>
        internal static Place Current => current;

        /// <summary>From the other hand to the front grip at the last <see cref="At"/>, metres (infinite: no front grip), for the log.</summary>
        internal static double LastToGrip { get; private set; } = double.PositiveInfinity;

        /// <summary>The item <see cref="Current"/> is a zone of.</summary>
        internal static MyEntity CurrentOf => currentOf;

        /// <summary>
        /// The zone <paramref name="off"/> (the other hand's grip, in the world) is in on the held <paramref name="weapon"/>,
        /// keeping the zone it was already in by <see cref="Hysteresis"/>.
        /// </summary>
        internal static Place At(MyCharacter character, MyEntity weapon, MyHandItemDefinition item, Vector3D off)
        {
            Place was = ReferenceEquals(currentOf, weapon) ? current : Place.None;
            GunProfile profile = GunProfile.Of(character);
            bool pistol = profile != null && profile.Kind == GunKind.Pistol;
            bool magazine = Reload.MagazineAt(character, weapon, profile, off, was == Place.Magazine ? Hysteresis : 1f, out double toMagazine);
            double toGrip = item != null && VRSettings.TwoHandedTools && !TwoHand.Off && TwoHand.GameHasLeftHand(item)
                ? Vector3D.Distance(off, Vector3D.Transform((Vector3D)ItemSpots.OtherGrasp(item), weapon.WorldMatrix))
                : double.PositiveInfinity;
            LastToGrip = toGrip;
            if (magazine && (pistol || toMagazine < toGrip))
                return Place.Magazine;
            if (toGrip < TwoHand.GrabReach * (was == Place.Grip ? Hysteresis : 1f)
                || (!double.IsInfinity(toGrip) && OnBody(weapon, item, off, was == Place.Grip)))
                return Place.Grip;
            return magazine ? Place.Magazine : Place.None;
        }

        /// <summary>A hand on the held item's own model, this far round its box, metres (more once there, <see cref="Hysteresis"/>).</summary>
        internal const double BodyReach = 0.03;

        /// <summary>The model counts from this far ahead of where the tool hand holds it, metres: a hand by the tool hand is not on the front.</summary>
        internal const double BodyAhead = 0.08;

        /// <summary>
        /// The other hand is on the front of the held item's own model (its box), ahead of the tool hand: a hand put on a drill's
        /// body or a grinder's head is on the tool, wherever the game's LeftHand spot sits on it (the drill's is a side
        /// handle 23 cm off its axis), and the hold then settles the hand onto that spot as the game has it.
        /// </summary>
        private static bool OnBody(MyEntity weapon, MyHandItemDefinition item, Vector3D off, bool already)
        {
            if (weapon?.PositionComp == null || item == null)
                return false;
            BoundingBox box = weapon.PositionComp.LocalAABB;
            if (!(box.Size.LengthSquared() > 1e-6))
                return false;
            Vector3D local = Vector3D.Transform(off, weapon.PositionComp.WorldMatrixNormalizedInv);
            double grow = BodyReach * (already ? Hysteresis : 1f);
            box.Inflate((float)grow);
            // Forward is -Z in the item's space.
            return box.Contains((Vector3)local) == ContainmentType.Contains && local.Z < ItemSpots.ToolGrasp(item).Z - BodyAhead;
        }

        /// <summary>
        /// Game thread, once a frame from <see cref="TwoHand"/>: the zone the other hand hovers in now (none when
        /// <paramref name="open"/> is false), and a tick on that hand as it comes into one.
        /// </summary>
        internal static void Update(MyCharacter character, bool open, MyEntity weapon, MyHandItemDefinition item, Vector3D off, Hand hand)
        {
            Place now = open && weapon != null ? At(character, weapon, item, off) : Place.None;
            if (now != current && now != Place.None)
            {
                long time = Stopwatch.GetTimestamp();
                if (time - hoverAt >= HoverEverySeconds * Stopwatch.Frequency)
                {
                    hoverAt = time;
                    Haptics.Pulse(hand, HoverAmplitude, HoverSeconds);
                }
                notes.Info($"Two-hand: {hand} hand at the {item?.Id.SubtypeName ?? "item"}'s {(now == Place.Grip ? "front grip" : "magazine")}");
            }
            current = now;
            currentOf = now == Place.None ? null : weapon;
        }
    }
}
