using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// What the player calls a controller's controls, and which of them sense a resting finger, per OpenXR interaction profile
    /// (the path without <c>/interaction_profiles/</c>, as <see cref="ControllerBindings"/> keys them). Used to write bindings
    /// for players ("Left Y: hold 0.5 s", "Right stick click: press, while Left grip held") and to offer Touch only where the
    /// controller can tell a touch from a press. The touch sensors follow the ones <see cref="OpenXR.XrInput"/> binds.
    /// </summary>
    internal static class ControlNames
    {
        /// <summary>The controllers with the Quest's face buttons: X and Y on the left, A and B on the right.</summary>
        private static readonly HashSet<string> XyAb = new HashSet<string>(StringComparer.Ordinal)
        {
            DefaultBindings.QuestProfile, DefaultBindings.Pico4Profile, DefaultBindings.PicoNeo3Profile, DefaultBindings.PicoUltraProfile,
            DefaultBindings.ReverbProfile, DefaultBindings.CosmosProfile, DefaultBindings.Focus3Profile,
        };

        /// <summary>The control's name on this controller, without the hand: "Y", "stick click", "trackpad top".</summary>
        public static string Name(string profile, Hand hand, ControllerControl control)
        {
            switch (control)
            {
                case ControllerControl.Trigger: return profile == DefaultBindings.SimpleProfile ? "select button" : "trigger";
                case ControllerControl.Grip: return "grip";
                case ControllerControl.Thumbstick: return "stick";
                case ControllerControl.ThumbstickClick: return "stick click";
                case ControllerControl.ButtonLower: return FaceButton(profile, hand, upper: false);
                case ControllerControl.ButtonUpper: return FaceButton(profile, hand, upper: true);
                case ControllerControl.Menu: return "Menu";
                case ControllerControl.Trackpad: return "trackpad";
                case ControllerControl.TrackpadUp: return "trackpad top";
                case ControllerControl.TrackpadDown: return "trackpad bottom";
                case ControllerControl.TrackpadLeft: return "trackpad left";
                case ControllerControl.TrackpadRight: return "trackpad right";
                case ControllerControl.TrackpadCentre: return "trackpad middle";
                case ControllerControl.Bumper: return "bumper";
                case ControllerControl.Thumbrest: return "thumbrest";
                case ControllerControl.DpadUp: return "D-pad up";
                case ControllerControl.DpadDown: return "D-pad down";
                case ControllerControl.DpadLeft: return "D-pad left";
                case ControllerControl.DpadRight: return "D-pad right";
                case ControllerControl.View: return "View";
                case ControllerControl.System: return "system button";
                case ControllerControl.TrackpadForce: return "trackpad squeezed";
                default: return control.ToString();
            }
        }

        private static string FaceButton(string profile, Hand hand, bool upper)
        {
            if (profile != null && XyAb.Contains(profile))
                return hand == Hand.Left ? (upper ? "Y" : "X") : (upper ? "B" : "A");
            if (profile == DefaultBindings.IndexProfile || profile == DefaultBindings.SteamFrameProfile)
                return upper ? "B" : "A";
            return upper ? "upper button" : "lower button";
        }

        /// <summary>The control with its hand: "Left Y", "Right stick click".</summary>
        public static string Name(string profile, ControlRef control) =>
            (control.Hand == Hand.Left ? "Left " : "Right ") + Name(profile, control.Hand, control.Control);

        /// <summary>How a binding is used, as the player reads it: "press", "tap", "hold 0.5 s", "touch".</summary>
        public static string Kind(PressKind kind, double holdSeconds)
        {
            switch (kind)
            {
                case PressKind.Press: return "press";
                case PressKind.Tap: return Math.Abs(holdSeconds - BindingEngine.DefaultHoldSeconds) < 1e-6 ? "tap" : "tap (under " + Seconds(holdSeconds) + ")";
                case PressKind.Hold: return "hold " + Seconds(holdSeconds);
                case PressKind.Touch: return "touch";
                default: return kind.ToString();
            }
        }

        public static string Seconds(double seconds) => seconds.ToString("0.0#", CultureInfo.InvariantCulture) + " s";

        /// <summary>A binding as the player reads it: "Left Y: hold 0.5 s", "Right stick click: press, while Left grip held".</summary>
        public static string Describe(string profile, Binding binding)
        {
            var text = new StringBuilder(Name(profile, binding.Control)).Append(": ").Append(Kind(binding.Kind, binding.HoldSeconds));
            if (binding.Modifier.HasValue)
                text.Append(", while ").Append(Name(profile, binding.Modifier.Value)).Append(" held");
            return text.ToString();
        }

        /// <summary>
        /// The controller in <paramref name="hand"/> reports the control (<see cref="HandState.ControlsDown"/> or its touch), as
        /// <see cref="OpenXR.XrInput"/> binds it per profile. A profile not known here is taken as a Quest's.
        /// </summary>
        public static bool Has(string profile, Hand hand, ControllerControl control)
        {
            bool left = hand == Hand.Left;
            if (control == ControllerControl.Trigger)
                return true;
            if (control == ControllerControl.Grip)
                return profile != DefaultBindings.SimpleProfile;
            switch (profile)
            {
                case DefaultBindings.SimpleProfile:
                    return control == ControllerControl.Menu;
                case DefaultBindings.WandProfile:
                    return control == ControllerControl.Menu || control == ControllerControl.Trackpad || control == ControllerControl.TrackpadUp
                        || control == ControllerControl.TrackpadDown || control == ControllerControl.TrackpadLeft
                        || control == ControllerControl.TrackpadRight || control == ControllerControl.TrackpadCentre;
                case DefaultBindings.MotionControllerProfile:
                    return Stick(control) || control == ControllerControl.Menu || control == ControllerControl.Trackpad
                        || control == ControllerControl.TrackpadUp || control == ControllerControl.TrackpadDown;
                case DefaultBindings.IndexProfile:
                    return Stick(control) || control == ControllerControl.ButtonLower || control == ControllerControl.ButtonUpper
                        || control == ControllerControl.Trackpad || left && control == ControllerControl.TrackpadForce;
                case DefaultBindings.SteamFrameProfile:
                    if (Stick(control))
                        return true;
                    return left
                        ? control == ControllerControl.DpadDown || control == ControllerControl.DpadUp || control == ControllerControl.View
                        : control == ControllerControl.ButtonLower || control == ControllerControl.ButtonUpper;
                default:
                    if (Stick(control) || control == ControllerControl.ButtonLower || control == ControllerControl.ButtonUpper)
                        return true;
                    if (control == ControllerControl.Menu)
                        return left || profile == DefaultBindings.PicoNeo3Profile || profile == DefaultBindings.ReverbProfile;
                    if (control == ControllerControl.Thumbrest)
                        return profile == null || profile == DefaultBindings.QuestProfile || profile == DefaultBindings.Focus3Profile || !XyAb.Contains(profile);
                    return control == ControllerControl.Bumper && profile == DefaultBindings.CosmosProfile;
            }
        }

        private static bool Stick(ControllerControl control) => control == ControllerControl.Thumbstick || control == ControllerControl.ThumbstickClick;

        /// <summary>The control can only be touched, never pressed: the thumbrest, and the stick (whose press is its click).</summary>
        public static bool TouchOnly(ControllerControl control) => control == ControllerControl.Thumbrest || control == ControllerControl.Thumbstick;

        /// <summary>The controller senses a finger resting on the control (so Touch can be told from Press there).</summary>
        public static bool HasTouchSensor(string profile, Hand hand, ControllerControl control)
        {
            switch (profile)
            {
                case DefaultBindings.QuestProfile:
                    return control == ControllerControl.Thumbrest || QuestTouch(control);
                case DefaultBindings.Pico4Profile:
                case DefaultBindings.PicoNeo3Profile:
                case DefaultBindings.PicoUltraProfile:
                    return QuestTouch(control);
                case DefaultBindings.IndexProfile:
                    return QuestTouch(control) || control == ControllerControl.Trackpad;
                case DefaultBindings.Focus3Profile:
                    return control == ControllerControl.Trigger || control == ControllerControl.Thumbstick
                        || control == ControllerControl.ThumbstickClick || control == ControllerControl.Thumbrest || control == ControllerControl.Grip;
                case DefaultBindings.MotionControllerProfile:
                    return control == ControllerControl.Trackpad;
                case DefaultBindings.SteamFrameProfile:
                    if (control == ControllerControl.Trigger || control == ControllerControl.Thumbstick || control == ControllerControl.ThumbstickClick)
                        return true;
                    return hand == Hand.Left
                        ? control == ControllerControl.DpadDown || control == ControllerControl.DpadUp
                        : control == ControllerControl.ButtonLower || control == ControllerControl.ButtonUpper;
                case DefaultBindings.WandProfile:
                    return control == ControllerControl.Trackpad;
                default:
                    return false;
            }
        }

        private static bool QuestTouch(ControllerControl control) =>
            control == ControllerControl.Trigger || control == ControllerControl.Thumbstick || control == ControllerControl.ThumbstickClick
            || control == ControllerControl.ButtonLower || control == ControllerControl.ButtonUpper;
    }
}
