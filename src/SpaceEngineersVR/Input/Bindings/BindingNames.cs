// BindingNames: an action's control named from a binding table and the controller it is for (no game types; the tests use it).
using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// Names what to press for an action from a controller's binding table as that controller's profile
    /// calls its controls (<see cref="ControlNames"/>). A control only one hand has a name for ("Y", "B" on a Quest) needs no
    /// hand said; the rest ("Left grip") do. <see cref="ActionNames"/> is this for the controller in hand.
    /// </summary>
    internal readonly struct BindingNames
    {
        private readonly IReadOnlyList<Binding> bindings, mirroredBindings;
        private readonly string profile;
        private readonly Func<Hand, ControllerControl, bool> onHand;

        /// <param name="onHand">Whether the controller in that hand has the control (the Quest's right hand has no Menu); null: both have every one.</param>
        /// <param name="mirrored">The same bindings as they work left-handed (<see cref="BindingMirror.Table"/>); null: made from <paramref name="bindings"/>.</param>
        public BindingNames(IReadOnlyList<Binding> bindings, string profile, Func<Hand, ControllerControl, bool> onHand = null, IReadOnlyList<Binding> mirrored = null)
        {
            this.bindings = bindings;
            mirroredBindings = mirrored ?? BindingMirror.Table(profile, bindings);
            this.profile = profile;
            this.onHand = onHand;
        }

        /// <summary>The first binding of <paramref name="action"/> in use (on <paramref name="hand"/> when given; as it works left-handed when <paramref name="mirrored"/>).</summary>
        public Binding Find(VRAction action, bool mirrored = false, Hand? hand = null)
        {
            Binding first = null;
            foreach (Binding b in mirrored ? mirroredBindings : bindings)
            {
                if (b.Action != (int)action)
                    continue;
                if (hand == null || b.Control.Hand == hand.Value)
                    return b;
                if (first == null)
                    first = b;
            }
            return hand == null ? first : null;
        }

        /// <summary>
        /// What else <paramref name="main"/>'s control does by <paramref name="other"/>'s binding on it, for a label's note:
        /// "hold: recentre" held there, "+ Left stick click: next stage" with a modifier, "+ Right trigger: ping" held as the
        /// modifier of another control; null when <paramref name="other"/> is not on that control, so a rebinding moves or
        /// drops the note.
        /// </summary>
        public string Also(Binding main, VRAction other, string what, bool mirrored = false)
        {
            if (main == null || what == null)
                return null;
            foreach (Binding b in mirrored ? mirroredBindings : bindings)
            {
                if (b.Action != (int)other || ReferenceEquals(b, main))
                    continue;
                if (b.Control.Index != main.Control.Index)
                {
                    // Main's control held and another pressed with it (a Vive wand's right Menu and its trigger: a ping).
                    string with = b.Modifier.HasValue && b.Modifier.Value.Index == main.Control.Index && !main.Modifier.HasValue
                        ? Qualified(b.Control.Hand, b.Control.Control) : null;
                    if (with != null)
                        return "+ " + with + ": " + what;
                    continue;
                }
                if (b.Modifier.HasValue && !main.Modifier.HasValue)
                {
                    string modifier = Qualified(b.Modifier.Value.Hand, b.Modifier.Value.Control);
                    if (modifier != null)
                        return "+ " + modifier + ": " + what;
                }
                else if (!b.Modifier.HasValue && b.Kind == PressKind.Hold && main.Kind != PressKind.Hold)
                    return "hold: " + what;
            }
            return null;
        }

        /// <summary>The control's name on the controller in use, capitalised, without the hand: "Y", "Stick click"; null where the controller has none.</summary>
        public string Control(Hand hand, ControllerControl control) => Upper(Raw(hand, control));

        // The name as ControlNames gives it ("grip", "Menu", "Y"), or null where the controller in that hand has no such control.
        private string Raw(Hand hand, ControllerControl control)
        {
            if (!Has(profile, control))
                return null;
            if (control == ControllerControl.Thumbstick && profile == DefaultBindings.WandProfile)
                control = ControllerControl.Trackpad;
            if (onHand != null && !onHand(hand, control))
                return null;
            return ControlNames.Name(profile, hand, control);
        }

        /// <summary>The control with its hand where the other hand has one of that name: "Left grip", "Y".</summary>
        public string Qualified(Hand hand, ControllerControl control)
        {
            string name = Raw(hand, control);
            if (name == null)
                return null;
            Hand other = hand == Hand.Left ? Hand.Right : Hand.Left;
            return Raw(other, control) != name ? Upper(name) : (hand == Hand.Left ? "Left " : "Right ") + name;
        }

        /// <summary>What to press for the action, with its hand where needed: "Y", "Right stick click", "Left stick click + A"; null with no binding.</summary>
        public string Name(VRAction action, bool mirrored = false)
        {
            Binding b = Find(action, mirrored);
            if (b == null)
                return null;
            string name = Qualified(b.Control.Hand, b.Control.Control);
            if (name != null && b.Modifier.HasValue)
            {
                string modifier = Qualified(b.Modifier.Value.Hand, b.Modifier.Value.Control);
                name = modifier == null ? null : modifier + " + " + name;
            }
            return name;
        }

        /// <summary>As <see cref="Name"/>, said as it is used: "Hold Y", "Tap Y", "B".</summary>
        public string Describe(VRAction action, bool mirrored = false)
        {
            Binding b = Find(action, mirrored);
            string name = Name(action, mirrored);
            if (name == null)
                return null;
            switch (b.Kind)
            {
                case PressKind.Hold: return "Hold " + name;
                case PressKind.Tap: return "Tap " + name;
                case PressKind.Touch: return "Touch " + name;
                default: return name;
            }
        }

        /// <summary>The controller has the control: a simple controller only its select button and Menu, a Vive wand no stick or face buttons.</summary>
        private static bool Has(string profile, ControllerControl control)
        {
            switch (profile)
            {
                case DefaultBindings.SimpleProfile:
                    return control == ControllerControl.Trigger || control == ControllerControl.Menu;
                case DefaultBindings.WandProfile:
                    return control != ControllerControl.ThumbstickClick && control != ControllerControl.ButtonLower && control != ControllerControl.ButtonUpper;
                default:
                    return true;
            }
        }

        private static string Upper(string s) => string.IsNullOrEmpty(s) || char.IsUpper(s[0]) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
