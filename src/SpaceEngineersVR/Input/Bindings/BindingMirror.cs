// BindingMirror: the bindings as a left-handed player has them (no game types; the tests use it).
using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// Left-handed play: the actions that follow a hand (<see cref="VRActionInfo.FollowsHand"/>: the tool hand's triggers and
    /// stick click, the laser hand's item move, the off hand's amount buttons) move to the other hand's matching control, and
    /// every other binding stays on the control it is on, so a button held as a modifier (X to mark a GPS, the left stick click
    /// to turn a block) is still that button. The matching control is the one playing the same part on the other hand as the
    /// controller's defaults put it (<see cref="DefaultBindings.RoleOf"/>): a Steam Frame's right A is its left D-pad down, and
    /// a control with no partner the other hand reports (an Index's left trackpad pressed firmly) stays where it is.
    /// </summary>
    internal static class BindingMirror
    {
        /// <summary>The bindings as they work left-handed: <paramref name="list"/> with the hand-following actions moved over.</summary>
        public static List<Binding> Table(string profile, IReadOnlyList<Binding> list)
        {
            Func<ControlRef, ControlRef?> role = DefaultBindings.RoleOf(profile);
            var mirrored = new List<Binding>(list.Count);
            foreach (Binding b in list)
                mirrored.Add(VRActions.FollowsHand(b.Action) ? Mirror(profile, b, role) : b);
            return mirrored;
        }

        /// <summary>The binding with its control and held button moved to their partners on the other hand.</summary>
        public static Binding Mirror(string profile, Binding b) => Mirror(profile, b, DefaultBindings.RoleOf(profile));

        private static Binding Mirror(string profile, Binding b, Func<ControlRef, ControlRef?> role) =>
            new Binding(b.Action, Counterpart(profile, b.Control, role), b.Kind,
                b.Modifier.HasValue ? Counterpart(profile, b.Modifier.Value, role) : (ControlRef?)null, b.HoldSeconds);

        /// <summary>The control playing <paramref name="c"/>'s part on the other hand; <paramref name="c"/> itself where none does.</summary>
        public static ControlRef Counterpart(string profile, ControlRef c) => Counterpart(profile, c, DefaultBindings.RoleOf(profile));

        private static ControlRef Counterpart(string profile, ControlRef c, Func<ControlRef, ControlRef?> role)
        {
            ControlRef? other = Partner(profile, c, role);
            // Only a pair both ways: two controls on one hand never claim the same partner.
            if (other.HasValue && Partner(profile, other.Value, role) is ControlRef back && back.Equals(c))
                return other.Value;
            return c;
        }

        private static ControlRef? Partner(string profile, ControlRef c, Func<ControlRef, ControlRef?> role)
        {
            Hand otherHand = c.Hand == Hand.Left ? Hand.Right : Hand.Left;
            // The Quest control whose part c plays (a Steam Frame's left D-pad down plays X), then that part on the other hand.
            ControlRef? quest = QuestPart(c, role);
            if (quest.HasValue && role(new ControlRef(otherHand, quest.Value.Control)) is ControlRef played
                && played.Hand == otherHand && ControlNames.Has(profile, otherHand, played.Control))
                return played;
            // Else the same control on the other hand, where that hand has one.
            if (ControlNames.Has(profile, otherHand, c.Control))
                return new ControlRef(otherHand, c.Control);
            return null;
        }

        private static ControlRef? QuestPart(ControlRef c, Func<ControlRef, ControlRef?> role)
        {
            ControlRef? itself = null;
            for (int i = 0; i < ControlRef.ControlCount; i++)
            {
                var q = new ControlRef(c.Hand, (ControllerControl)i);
                if (!(role(q) is ControlRef played) || !played.Equals(c))
                    continue;
                if (!q.Equals(c))
                    return q;
                itself = q;
            }
            return itself;
        }
    }
}
