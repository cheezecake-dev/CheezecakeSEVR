// SuggestedBindings: the bindings an outside action asks for, read, put on each controller, and given only where they clash with nothing (no game types; the tests use it).
using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// The bindings another plugin or a mod suggests for an action it registers (<see cref="VRActions.AddOutside"/>). They are
    /// written as the bindings in the settings file are (<see cref="BindingText.TryReadBinding"/>), on the Meta Quest Touch
    /// controller's controls, and put on each controller's own controls the way the plugin's defaults are
    /// (<see cref="DefaultBindings.RoleOf"/>), so one suggestion serves every controller. A suggestion is a default the player
    /// can take away, given only where it clashes with nothing the player has, right- or left-handed (<see cref="ClashesWithNothing"/>):
    /// an outside action never takes a control from one of the plugin's actions or from the player's own choices, and starts
    /// unbound where its suggestion would. A suggestion on a stick's direction is never given (<see cref="UsesDirection"/>).
    /// </summary>
    internal static class SuggestedBindings
    {
        /// <summary>The most bindings one suggestion may hold.</summary>
        public const int MaxPerAction = 4;

        /// <summary>
        /// Reads <paramref name="text"/>: one binding, or several separated by <c>;</c>, each
        /// <c>Hand.Control.Kind[+Hand.Control][@seconds]</c> (<c>Right.ButtonLower.Tap</c>, <c>Left.Trigger.Press+Left.Grip</c>).
        /// Null or blank: none. Press, Tap or Hold only: Touch is not suggested (a resting finger is not a press, and few
        /// controllers sense it). False with <paramref name="why"/> on anything else.
        /// </summary>
        public static bool TryRead(string text, out List<Binding> bindings, out string why)
        {
            bindings = new List<Binding>();
            why = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;
            foreach (string raw in text.Split(';'))
            {
                string part = raw.Trim();
                if (part.Length == 0)
                    continue;
                if (!BindingText.TryReadBinding(part, 0, out Binding b))
                {
                    why = $"\"{part}\" is not Hand.Control.Kind[+Hand.Control][@seconds] (hands Left, Right; kinds Press, Tap, Hold; seconds over 0, up to 10)";
                    return false;
                }
                if (b.Kind == PressKind.Touch)
                {
                    why = $"\"{part}\" is a Touch binding; suggest Press, Tap or Hold";
                    return false;
                }
                foreach (Binding had in bindings)
                {
                    if (had.Control.Equals(b.Control) && had.Kind == b.Kind && Nullable.Equals(had.Modifier, b.Modifier))
                    {
                        why = $"\"{part}\" is given twice";
                        return false;
                    }
                }
                bindings.Add(b);
                if (bindings.Count > MaxPerAction)
                {
                    why = $"more than {MaxPerAction} bindings";
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// <paramref name="quest"/> (bindings on the Meta Quest Touch controller's controls) on <paramref name="profile"/>'s
        /// controls, as its defaults are put there; a binding whose control or held button that controller does not have is left out.
        /// </summary>
        public static List<Binding> ForProfile(string profile, IReadOnlyList<Binding> quest)
        {
            var list = new List<Binding>(quest.Count);
            Func<ControlRef, ControlRef?> role = DefaultBindings.RoleOf(profile);
            string named = profile ?? DefaultBindings.QuestProfile;
            foreach (Binding b in quest)
            {
                ControlRef? control = role(b.Control);
                ControlRef? modifier = b.Modifier.HasValue ? role(b.Modifier.Value) : null;
                if (!control.HasValue || b.Modifier.HasValue && !modifier.HasValue)
                    continue;
                if (!ControlNames.Has(named, control.Value.Hand, control.Value.Control)
                    || modifier.HasValue && (!ControlNames.Has(named, modifier.Value.Hand, modifier.Value.Control) || modifier.Value.Equals(control.Value)))
                    continue;
                list.Add(new Binding(b.Action, control.Value, b.Kind, modifier, b.HoldSeconds));
            }
            return list;
        }

        /// <summary>
        /// <paramref name="b"/> is worked by a stick's direction (the thumbstick pushed any way, or a wand's trackpad as a whole),
        /// as the control or as the one held with it. Both sticks always carry the player's own movement, turning, flight or the
        /// wheels' picking, which are not bindings, so the clash check cannot see them: a suggestion there would fire whenever
        /// the player turns or walks. Such a suggestion starts unbound everywhere; the player may still bind one on the bindings
        /// page. The stick's click (<see cref="ControllerControl.ThumbstickClick"/>) is a button and is not a direction.
        /// </summary>
        public static bool UsesDirection(Binding b) => IsDirection(b.Control.Control) || b.Modifier.HasValue && IsDirection(b.Modifier.Value.Control);

        private static bool IsDirection(ControllerControl control) => control == ControllerControl.Thumbstick || control == ControllerControl.Trackpad;

        /// <summary>
        /// <paramref name="candidate"/> added to <paramref name="list"/> (<paramref name="profile"/>'s bindings) clashes with no
        /// binding there (<see cref="BindingEngine.Conflicts"/>), neither as they work right-handed nor left-handed
        /// (<see cref="BindingMirror.Table"/>), so a suggestion given does not depend on the handedness setting.
        /// </summary>
        public static bool ClashesWithNothing(string profile, IReadOnlyList<Binding> list, Binding candidate)
        {
            var trial = new List<Binding>(list.Count + 1);
            trial.AddRange(list);
            trial.Add(candidate);
            foreach ((Binding a, Binding b, string _) in BindingEngine.Conflicts(trial, VRActions.SameContext, VRActions.HeldMode))
                if (ReferenceEquals(a, candidate) || ReferenceEquals(b, candidate))
                    return false;
            List<Binding> left = BindingMirror.Table(profile, trial);
            Binding moved = left[left.Count - 1];
            foreach ((Binding a, Binding b, string _) in BindingEngine.Conflicts(left, VRActions.SameContextLeftHanded, VRActions.HeldMode))
                if (ReferenceEquals(a, moved) || ReferenceEquals(b, moved))
                    return false;
            return true;
        }
    }
}
