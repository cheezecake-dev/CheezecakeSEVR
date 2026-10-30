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

        /// <summary>
        /// Left-handed, the action is used with its moved bindings (<paramref name="leftHanded"/>) with the player at
        /// <paramref name="where"/>, not with its own (<paramref name="plain"/>). The one rule for which hand an action is on:
        /// play (ControllerBindings) and the bindings page's clash check (<see cref="Conflicts"/>) both ask it, of the bindings
        /// as they are, so a rebinding or a mode switched off moves the answer with it.
        /// <list type="bullet">
        /// <item>An action that does not follow the hand (<see cref="VRActionInfo.FollowsHand"/>) never moves.</item>
        /// <item>One that does moves, except where a held mode that can be used at <paramref name="where"/> is bound on the
        /// control (with the same held button) one of its moved bindings would take: holding that control is the mode, so the
        /// action keeps its own control there (reload on the jetpack, while zero-g pitch and roll is on the left stick click).</item>
        /// <item>One that moves only to make way (<see cref="VRActionInfo.MakesWay"/>) moves while another hand-following action
        /// that can be used now moves onto one of its controls, in a place the two share; the same wherever it applies, so it is
        /// on one hand in all its places (sprint, on the other stick click while a gun that reloads takes the left one).</item>
        /// </list>
        /// </summary>
        /// <param name="plain">The bindings in use.</param>
        /// <param name="leftHanded"><see cref="Table"/> of <paramref name="plain"/>, in the same order.</param>
        /// <param name="usable">The action can be used now (neither the game nor a setting refuses it): asked of a held mode where
        /// it applies, and of the actions a make-way action yields to.</param>
        public static bool Moves(int action, BindingContext where, IReadOnlyList<Binding> plain, IReadOnlyList<Binding> leftHanded, Func<int, bool> usable)
        {
            if (plain == null || leftHanded == null || usable == null)
                throw new ArgumentNullException(plain == null ? nameof(plain) : leftHanded == null ? nameof(leftHanded) : nameof(usable));
            if (plain.Count != leftHanded.Count)
                throw new ArgumentException("The left-handed table is not the bindings' (BindingMirror.Table, in the same order)", nameof(leftHanded));
            VRActionInfo info = VRActions.Info(action);
            if (info == null || !info.FollowsHand)
                return false;
            return info.MakesWay ? MakesWayNow(info, plain, leftHanded, usable) : !HeldModeTakes(action, where, leftHanded, usable);
        }

        /// <summary>A held mode usable at <paramref name="where"/> is bound where one of <paramref name="action"/>'s moved bindings would be.</summary>
        private static bool HeldModeTakes(int action, BindingContext where, IReadOnlyList<Binding> leftHanded, Func<int, bool> usable)
        {
            foreach (Binding held in leftHanded)
            {
                if (held.Action == action || !VRActions.HeldMode(held.Action) || (VRActions.ContextsOf(held.Action) & where) == 0 || !usable(held.Action))
                    continue;
                foreach (Binding moved in leftHanded)
                    if (moved.Action == action && SamePlace(moved, held))
                        return true;
            }
            return false;
        }

        /// <summary>A usable hand-following action that is not itself making way moves onto one of <paramref name="info"/>'s own controls where they both apply.</summary>
        private static bool MakesWayNow(VRActionInfo info, IReadOnlyList<Binding> plain, IReadOnlyList<Binding> leftHanded, Func<int, bool> usable)
        {
            for (int i = 0; i < leftHanded.Count; i++)
            {
                Binding moved = leftHanded[i];
                VRActionInfo by = VRActions.Info(moved.Action);
                if (by == null || by.Index == info.Index || !by.FollowsHand || by.MakesWay || SamePlace(moved, plain[i]))
                    continue;
                BindingContext shared = by.Contexts & info.Contexts;
                if (shared == BindingContext.None || !usable(by.Index) || !Lands(info.Index, moved, plain))
                    continue;
                foreach (BindingContext place in Places(shared))
                    if (!HeldModeTakes(by.Index, place, leftHanded, usable))
                        return true;
            }
            return false;
        }

        /// <summary><paramref name="moved"/> is on one of <paramref name="action"/>'s own controls (with the same held button).</summary>
        private static bool Lands(int action, Binding moved, IReadOnlyList<Binding> plain)
        {
            foreach (Binding own in plain)
                if (own.Action == action && SamePlace(own, moved))
                    return true;
            return false;
        }

        private static bool SamePlace(Binding a, Binding b) => a.Control.Equals(b.Control) && Nullable.Equals(a.Modifier, b.Modifier);

        /// <summary>Each place in <paramref name="contexts"/> on its own.</summary>
        private static IEnumerable<BindingContext> Places(BindingContext contexts)
        {
            for (int bit = 0; bit < 32; bit++)
            {
                var place = (BindingContext)(1 << bit);
                if ((contexts & place) != 0)
                    yield return place;
            }
        }

        /// <summary>The places where the action is used with its moved bindings (<see cref="Moves"/> at each place it applies).</summary>
        public static BindingContext MovesIn(int action, IReadOnlyList<Binding> plain, IReadOnlyList<Binding> leftHanded, Func<int, bool> usable)
        {
            BindingContext moves = BindingContext.None;
            foreach (BindingContext place in Places(VRActions.ContextsOf(action)))
                if (Moves(action, place, plain, leftHanded, usable))
                    moves |= place;
            return moves;
        }

        /// <summary>
        /// The bindings in <paramref name="plain"/> that cannot both work as they do for a left-handed player
        /// (<see cref="BindingEngine.Conflicts"/>): a hand-following action's moved binding counts in the places it moves to
        /// (<see cref="MovesIn"/>), its own binding in the places it stays, and an action that cannot be used
        /// (<paramref name="usable"/>) counts nowhere. Each clash names the bindings of <paramref name="plain"/> it comes from.
        /// </summary>
        public static List<(Binding A, Binding B, string Why)> Conflicts(string profile, IReadOnlyList<Binding> plain, Func<int, bool> usable)
        {
            if (plain == null || usable == null)
                throw new ArgumentNullException(plain == null ? nameof(plain) : nameof(usable));
            List<Binding> moved = Table(profile, plain);
            var played = new List<Binding>(plain.Count);
            var from = new Dictionary<Binding, Binding>();
            var places = new Dictionary<Binding, BindingContext>();
            void Add(Binding b, Binding source, BindingContext where)
            {
                played.Add(b);
                from[b] = source;
                places[b] = where;
            }
            var movesIn = new Dictionary<int, BindingContext>();
            for (int i = 0; i < plain.Count; i++)
            {
                Binding own = plain[i];
                VRActionInfo info = VRActions.Info(own.Action);
                if (info == null || !usable(own.Action))
                    continue;
                if (!info.FollowsHand)
                {
                    Add(own, own, info.Contexts);
                    continue;
                }
                if (!movesIn.TryGetValue(own.Action, out BindingContext moves))
                    movesIn[own.Action] = moves = MovesIn(own.Action, plain, moved, usable);
                if (moves != BindingContext.None)
                    Add(moved[i], own, moves);
                if ((info.Contexts & ~moves) != BindingContext.None)
                    Add(own, own, info.Contexts & ~moves);
            }
            var found = new List<(Binding A, Binding B, string Why)>();
            foreach ((Binding a, Binding b, string why) in BindingEngine.ConflictsBetween(played, (x, y) => VRActions.SameContext(places[x], places[y]), VRActions.HeldMode))
                found.Add((from[a], from[b], why));
            return found;
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
