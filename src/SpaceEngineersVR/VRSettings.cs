using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SpaceEngineersVR
{
    /// <summary>
    /// One setting. Declared once, in <see cref="VRSettings"/>; the settings file and the options screen
    /// (Gui/VROptionsScreen.cs) are both generated from the list, so a new setting is one declaration there.
    /// A setting has two values. The effective one is what the rest of the plugin reads. The pending one is what is
    /// shown, edited and saved. For a setting that applies live they are always the same. A setting that applies on
    /// restart (<see cref="Restart"/>) keeps its effective value as it was at start-up, so changing it in the menu
    /// cannot disturb the run in progress (the mirror window's size, for one, is compared against all the time).
    /// </summary>
    internal abstract class Setting
    {
        protected Setting(string key, string label, string section, string tooltip, bool restart, bool hidden)
        {
            Key = key;
            Label = label;
            Section = section;
            Tooltip = tooltip ?? string.Empty;
            Restart = restart;
            Hidden = hidden;
        }

        /// <summary>The name in the settings file.</summary>
        public string Key { get; }

        /// <summary>The name in the options screen.</summary>
        public string Label { get; }

        /// <summary>The options screen groups settings under this caption, in the order the sections first appear.</summary>
        public string Section { get; }

        public string Tooltip { get; }

        /// <summary>The change only takes effect the next time the game starts.</summary>
        public bool Restart { get; }

        /// <summary>Kept in the file but not shown in the options screen (the height calibration sets it).</summary>
        public bool Hidden { get; }

        /// <summary>Sets the pending value (and the effective one, unless <see cref="Restart"/>) to the default.</summary>
        public abstract void ResetToDefault();

        /// <summary>The pending value, to hand back to <see cref="Restore"/>.</summary>
        public abstract object Snapshot();

        public abstract void Restore(object snapshot);

        /// <summary>Both values back to the default. Used when the settings are loaded.</summary>
        internal abstract void Clear();

        /// <summary>The pending value as it is written to the file; empty for none.</summary>
        internal abstract string Serialize();

        /// <summary>Reads a value from the file into both values. False, with the reason, when the text is not a valid value.</summary>
        internal abstract bool TryLoad(string text, out string problem);

        /// <summary>One line for the comment above the setting in the file.</summary>
        internal abstract string Describe();
    }

    /// <summary>A number, in a range, moved in steps. Stored as a float; <see cref="Integer"/> ones are whole.</summary>
    internal sealed class NumberSetting : Setting
    {
        private float effective, pending;
        private bool effectiveSet, pendingSet;

        /// <param name="optional">May be unset (null): <see cref="DefaultValue"/> is then null too.</param>
        /// <param name="displayScale">What the options screen multiplies the value by to show it (metres as millimetres).</param>
        public NumberSetting(string key, string label, string section, float? defaultValue, float min, float max, float step,
            string unit = "", int decimals = 2, float displayScale = 1f, string tooltip = null,
            bool restart = false, bool hidden = false, bool optional = false, bool integer = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            DefaultValue = defaultValue;
            Min = min;
            Max = max;
            Step = step;
            Unit = unit ?? string.Empty;
            Decimals = decimals;
            DisplayScale = displayScale;
            Optional = optional;
            Integer = integer;
            Clear();
        }

        public float? DefaultValue { get; }

        public float Min { get; }

        public float Max { get; }

        public float Step { get; }

        /// <summary>Shown after the value in the options screen ("m", "deg", "px").</summary>
        public string Unit { get; }

        public int Decimals { get; }

        public float DisplayScale { get; }

        public bool Optional { get; }

        public bool Integer { get; }

        /// <summary>What the plugin uses. For a setting that is <see cref="Optional"/> and unset: the default, or the minimum.</summary>
        public float Value => effectiveSet ? effective : DefaultValue ?? Min;

        /// <summary>What the plugin uses; null when unset.</summary>
        public float? Effective => effectiveSet ? effective : (float?)null;

        /// <summary>What the options screen shows and the file holds; null when unset.</summary>
        public float? Pending => pendingSet ? pending : (float?)null;

        /// <summary>
        /// The new value, from the options screen or from code: not range-checked (the screen's slider keeps within
        /// the range, and code knows what it measured). Null unsets an optional setting; a number that is not a
        /// number is ignored.
        /// </summary>
        public void Set(float? value)
        {
            if (!Accept(ref value))
                return;
            Assign(ref pending, ref pendingSet, value);
            if (!Restart)
                Assign(ref effective, ref effectiveSet, value);
        }

        /// <summary>Sets both values, for the command line.</summary>
        public void Force(float? value)
        {
            if (!Accept(ref value))
                return;
            Assign(ref pending, ref pendingSet, value);
            Assign(ref effective, ref effectiveSet, value);
        }

        private bool Accept(ref float? value)
        {
            if (!value.HasValue)
                return Optional;
            if (float.IsNaN(value.Value) || float.IsInfinity(value.Value))
                return false;
            if (Integer)
                value = (float)Math.Round(value.Value);
            return true;
        }

        private static void Assign(ref float target, ref bool targetSet, float? value)
        {
            // The value first, then whether there is one: another thread that sees "set" sees a value that goes with it.
            if (value.HasValue)
                target = value.Value;
            targetSet = value.HasValue;
        }

        public override void ResetToDefault() => Set(DefaultValue);

        public override object Snapshot() => Pending;

        public override void Restore(object snapshot) => Set((float?)snapshot);

        internal override void Clear() => Force(DefaultValue);

        internal override string Serialize()
        {
            if (!pendingSet)
                return string.Empty;
            return Integer
                ? ((long)Math.Round(pending)).ToString(CultureInfo.InvariantCulture)
                : pending.ToString("R", CultureInfo.InvariantCulture);
        }

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (Optional)
                {
                    Force(null);
                    return true;
                }
                problem = "no value";
                return false;
            }
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || float.IsNaN(value) || float.IsInfinity(value))
            {
                problem = $"'{text}' is not a number";
                return false;
            }
            if (value < Min || value > Max)
            {
                problem = $"{Format(value)} is outside {Format(Min)} to {Format(Max)}";
                return false;
            }
            if (Integer && value != Math.Round(value))
            {
                problem = $"'{text}' is not a whole number";
                return false;
            }
            Force(value);
            return true;
        }

        internal override string Describe()
        {
            string unit = Unit.Length > 0 ? " (" + Unit + ")" : string.Empty;
            string restart = Restart ? ", takes effect on restart" : string.Empty;
            string def = DefaultValue.HasValue ? Format(DefaultValue.Value) : "none";
            return $"{Label}{unit}: {Format(Min)} to {Format(Max)}, default {def}{restart}";
        }

        private static string Format(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>An on/off switch.</summary>
    internal sealed class FlagSetting : Setting
    {
        private volatile bool effective, pending;

        public FlagSetting(string key, string label, string section, bool defaultValue, string tooltip = null, bool restart = false, bool hidden = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            DefaultValue = defaultValue;
            Clear();
        }

        public bool DefaultValue { get; }

        /// <summary>What the plugin uses.</summary>
        public bool Value => effective;

        /// <summary>What the options screen shows and the file holds.</summary>
        public bool Pending => pending;

        public void Set(bool value)
        {
            pending = value;
            if (!Restart)
                effective = value;
        }

        /// <summary>Sets both values, for the command line.</summary>
        public void Force(bool value)
        {
            pending = value;
            effective = value;
        }

        public override void ResetToDefault() => Set(DefaultValue);

        public override object Snapshot() => pending;

        public override void Restore(object snapshot) => Set((bool)snapshot);

        internal override void Clear() => Force(DefaultValue);

        internal override string Serialize() => pending ? "true" : "false";

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                case "on":
                    Force(true);
                    return true;
                case "false":
                case "0":
                case "no":
                case "off":
                    Force(false);
                    return true;
                default:
                    problem = $"'{text}' is not true or false";
                    return false;
            }
        }

        internal override string Describe() =>
            $"{Label}: true or false, default {(DefaultValue ? "true" : "false")}{(Restart ? ", takes effect on restart" : string.Empty)}";
    }

    /// <summary>One of a few named choices, stored in the file by its name.</summary>
    internal sealed class ChoiceSetting : Setting
    {
        private readonly string[] choices;
        private volatile int effective, pending;

        public ChoiceSetting(string key, string label, string section, string[] choices, int defaultValue, string tooltip = null,
            bool restart = false, bool hidden = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            this.choices = (string[])choices.Clone();
            DefaultValue = defaultValue;
            Clear();
        }

        /// <summary>The choices' names, in the order the options screen lists them; a value is an index into it.</summary>
        public string[] Choices => (string[])choices.Clone();

        public int DefaultValue { get; }

        /// <summary>What the plugin uses.</summary>
        public int Value => effective;

        /// <summary>What the options screen shows and the file holds.</summary>
        public int Pending => pending;

        /// <summary>The new choice; one that is not in the list is ignored.</summary>
        public void Set(int value)
        {
            if (value < 0 || value >= choices.Length)
                return;
            pending = value;
            if (!Restart)
                effective = value;
        }

        private void Force(int value)
        {
            pending = value;
            effective = value;
        }

        public override void ResetToDefault() => Set(DefaultValue);

        public override object Snapshot() => pending;

        public override void Restore(object snapshot) => Set((int)snapshot);

        internal override void Clear() => Force(DefaultValue);

        internal override string Serialize() => choices[pending];

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            string name = (text ?? string.Empty).Trim();
            for (int i = 0; i < choices.Length; i++)
            {
                if (string.Equals(choices[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    Force(i);
                    return true;
                }
            }
            problem = $"'{text}' is not one of {string.Join(", ", choices)}";
            return false;
        }

        internal override string Describe() =>
            $"{Label}: {string.Join(" or ", choices)}, default {choices[DefaultValue]}{(Restart ? ", takes effect on restart" : string.Empty)}";
    }

    /// <summary>
    /// A line of text. Written to the file with the characters that would break a line or start a comment escaped
    /// (<c>%</c>, <c>#</c>, line breaks), so any name survives the round trip.
    /// </summary>
    internal sealed class TextSetting : Setting
    {
        private volatile string effective = string.Empty, pending = string.Empty;

        public TextSetting(string key, string label, string section, string tooltip = null, bool restart = false, bool hidden = false)
            : base(key, label, section, tooltip, restart, hidden)
        {
            Clear();
        }

        /// <summary>What the plugin uses; never null.</summary>
        public string Value => effective;

        /// <summary>What the options screen shows and the file holds; never null.</summary>
        public string Pending => pending;

        public void Set(string value)
        {
            pending = value ?? string.Empty;
            if (!Restart)
                effective = pending;
        }

        public override void ResetToDefault() => Set(string.Empty);

        public override object Snapshot() => pending;

        public override void Restore(object snapshot) => Set((string)snapshot);

        internal override void Clear()
        {
            pending = string.Empty;
            effective = string.Empty;
        }

        internal override string Serialize() => Escape(pending);

        internal override bool TryLoad(string text, out string problem)
        {
            problem = null;
            string value = Unescape(text ?? string.Empty);
            pending = value;
            effective = value;
            return true;
        }

        internal override string Describe() => $"{Label}: text, default none{(Restart ? ", takes effect on restart" : string.Empty)}";

        internal static string Escape(string text) =>
            text.Replace("%", "%25").Replace("#", "%23").Replace("\r", "%0D").Replace("\n", "%0A");

        internal static string Unescape(string text) =>
            text.Replace("%0A", "\n").Replace("%0D", "\r").Replace("%23", "#").Replace("%25", "%");
    }

    /// <summary>
    /// The plugin's settings: declared below, read from the settings file at start-up (then the command line, which wins
    /// for that run) and written back by <see cref="Save"/>. The options screen edits them live.
    /// </summary>
    internal static class VRSettings
    {
        private static readonly object Gate = new object();
        private static readonly List<Setting> all = new List<Setting>();
        private static readonly Dictionary<string, Setting> byKey = new Dictionary<string, Setting>(StringComparer.OrdinalIgnoreCase);

        // ---- The settings. One declaration each; the options screen follows this list, in this order, and sections
        // ---- appear in the order they are first used here.

        private static readonly NumberSetting hudDistance = Add(new NumberSetting("hudDistance", "HUD distance", "Display",
            1.5f, 0.5f, 5f, 0.05f, unit: "m", decimals: 2,
            tooltip: "How far ahead of your eyes the HUD floats during play (-hudDistance). You see the change once the menu is closed."));

        private static readonly NumberSetting hudWidth = Add(new NumberSetting("hudWidth", "HUD width", "Display",
            55f, 20f, 120f, 1f, unit: "deg", decimals: 0,
            tooltip: "How wide the HUD looks, in degrees of your view (-hudWidth). You see the change once the menu is closed."));

        private static readonly FlagSetting showToolbarOnFoot = Add(new FlagSetting("showToolbarOnFoot", "Show toolbar on foot", "Display", false,
            tooltip: "On foot and on the jetpack, the HUD's bottom toolbar (the hotbar, its page numbers, the Customize Toolbar and Color lines, and the plate behind them) is left out: the tools wheel and the wrist panel do its job, and a short label names the tool you pick. The health, energy, gravity and oxygen readouts stay. In a seat, a camera view, the G menu and the terminal it is shown as the game shows it. On: the game's toolbar on foot too."));

        private static readonly NumberSetting comfortVignette = Add(new NumberSetting("comfortVignette", "Comfort vignette", "Display",
            0f, 0f, 1f, 0.05f, decimals: 2,
            tooltip: "Darkens the edges of your view while the thumbsticks move or turn you, which eases motion sickness. 0 turns it off. Moving your head never triggers it."));
        private static readonly FlagSetting levelChaseCamera = Add(new FlagSetting("levelChaseCamera", "Level chase camera", "Display", true,
            tooltip: "Third person in a ship: the camera follows the ship but keeps the horizon level, and only turns with its heading, so rolling and pitching the ship does not turn the world around you. Off: the game's own camera, locked to the ship. Needs \"Allow third person\"."));

        private static readonly FlagSetting thirdPerson = Add(new FlagSetting("thirdPerson", "Allow third person", "Display", false,
            tooltip: "Lets the camera-mode key switch your own view to third person (with the level-horizon chase camera). A world, a saved game or a respawn always starts in first person in VR; this only lets you switch yourself. Turning it off while in third person puts you back in first person. The external view screen works either way."));

        private static readonly NumberSetting menuDistance = Add(new NumberSetting("menuDistance", "Menu distance", "Menus",
            2f, 0.5f, 6f, 0.05f, unit: "m", decimals: 2,
            tooltip: "How far ahead of your head a menu is put (-menuDistance)."));

        private static readonly NumberSetting menuWidth = Add(new NumberSetting("menuWidth", "Menu width", "Menus",
            60f, 20f, 120f, 1f, unit: "deg", decimals: 0,
            tooltip: "How wide a menu looks, in degrees of your view (-menuWidth)."));

        private static readonly NumberSetting mainMenuWidth = Add(new NumberSetting("mainMenuWidth", "Main menu width", "Menus",
            85f, 40f, 110f, 5f, unit: "deg", decimals: 0,
            tooltip: "How wide the main menu looks, in degrees of your view. Menus in a world use Menu width."));

        private static readonly FlagSetting vrKeyboard = Add(new FlagSetting("vrKeyboard", "On-screen keyboard", "Menus", true,
            tooltip: "Click a text box with the laser and a keyboard opens on the menu to type into it. Off: only a real keyboard types."));
        private static readonly FlagSetting externalView = Add(new FlagSetting("externalView", "External view", "Display", false,
            tooltip: "A small screen on the dash showing your ship (or you, on foot) from behind and above, or the view from one of the ship's cameras, while you stay in the cockpit. On the desktop it is in the lower left of the picture. Costs a third scene pass. The actions wheel's External view entry turns it on and off and chooses what it shows."));

        private static readonly NumberSetting externalViewSize = Add(new NumberSetting("externalViewSize", "External view size", "Display",
            0.3f, 0.15f, 0.6f, 0.01f, unit: "m", decimals: 2,
            tooltip: "How wide the external view screen is in the headset, in metres. You see the change as the next frame is drawn."));

        private static readonly FlagSetting externalViewHalfRate = Add(new FlagSetting("externalViewHalfRate", "External view at half rate", "Display", true,
            tooltip: "Draw the external view every other frame. Saves most of its cost; the screen looks slightly less smooth."));

        private static readonly ChoiceSetting externalViewSource = Add(new ChoiceSetting("externalViewSource", "External view source", "Display",
            new[] { "Third person", "Camera" }, 0, hidden: true,
            tooltip: "What the external view shows: your ship from behind and above (Third person), or the view from one of its camera blocks (Camera). Chosen on the actions wheel."));

        private static readonly TextSetting externalViewCamera = Add(new TextSetting("externalViewCamera", "External view camera", "Display", hidden: true,
            tooltip: "The camera block the external view shows when its source is Camera: its entity id, a bar, and its name. Chosen on the actions wheel."));

        private static readonly ChoiceSetting playPosition = Add(new ChoiceSetting("playPosition", "Play position", "Height",
            new[] { "Standing", "Seated" }, 0,
            tooltip: "Standing: your real height drives your view (calibrate it below), and crouching for real crouches your character. Seated: your head's height when you switch, and wherever your head is when you recentre, becomes your character's eyes, standing tall on foot or at the seat's eyes in a seat, and leaning down never crouches."));

        private static readonly NumberSetting eyeHeight = Add(new NumberSetting("eyeHeight", "Eye height", "Height",
            null, 0.3f, 3f, 0.01f, unit: "m", decimals: 2, hidden: true, optional: true,
            tooltip: "Your eye height above the floor, set by height calibration."));

        private static readonly NumberSetting heightOffset = Add(new NumberSetting("heightOffset", "Height offset", "Height",
            0f, -0.5f, 0.5f, 0.01f, unit: "m", decimals: 2,
            tooltip: "Raises (+) or lowers (-) your view on top of the calibration."));

        private static readonly FlagSetting bodyClearance = Add(new FlagSetting("bodyClearance", "Keep eyes out of the body", "Height", true,
            tooltip: "On foot, lean or step so your head would be inside your character's chest, shoulders or backpack and the view stops at the surface instead. Off: the view goes where your head goes, through the body."));

        private static readonly FlagSetting crouchWithHead = Add(new FlagSetting("crouchWithHead", "Crouch when you crouch", "Height", true,
            tooltip: "On foot, in Standing play, crouch for real (head 30 cm or more below standing for a moment) and your character crouches, standing up when you do. Off, and always in Seated play: only the crouch key crouches."));

        private static readonly NumberSetting seatReach = Add(new NumberSetting("seatReach", "Seat head reach", "Height",
            0.25f, 0.1f, 1f, 0.05f, unit: "cm", decimals: 0, displayScale: 100f,
            tooltip: "In a seat, how far forward, up or down your head can move from your character's head before the view slows to a stop, in centimetres. Sideways it is four fifths of this. Backwards it is only as far as the body allows, so you cannot lean back into your own head and body."));

        private static readonly FlagSetting headLock = Add(new FlagSetting("headLock", "Lock head to body", "Height", true,
            tooltip: "On foot, your head can only move so far from your character's head. Step back, forward or sideways in the room and the view stops at the reach below instead of going out through the back of the body. Off: the view goes where your head goes."));

        private static readonly NumberSetting headReach = Add(new NumberSetting("headReach", "Head reach on foot", "Height",
            0.25f, 0.1f, 1f, 0.05f, unit: "cm", decimals: 0, displayScale: 100f,
            tooltip: "With the head locked to the body, how far forward your head can move from your character's head, in centimetres. Sideways it is four fifths of this. Backwards it is only as far as the body allows."));

        private static readonly NumberSetting snapTurn = Add(new NumberSetting("snapTurn", "Snap turn", "Controls",
            30f, 15f, 90f, 15f, unit: "deg", decimals: 0,
            tooltip: "How far one flick of the right stick turns you."));

        private static readonly FlagSetting smoothTurn = Add(new FlagSetting("smoothTurn", "Smooth turn", "Controls", false,
            tooltip: "Turn steadily while the right stick is held sideways, instead of in snap-turn steps. Some people find it less comfortable."));

        private static readonly NumberSetting smoothTurnSpeed = Add(new NumberSetting("smoothTurnSpeed", "Smooth turn speed", "Controls",
            120f, 30f, 360f, 15f, unit: "deg/s", decimals: 0,
            tooltip: "How fast the body turns with the right stick pushed all the way over, when Smooth turn is on. Pushed half way, half as fast."));

        private static readonly NumberSetting stickDeadzone = Add(new NumberSetting("stickDeadzone", "Stick deadzone", "Controls",
            0.15f, 0.05f, 0.4f, 0.01f, decimals: 2,
            tooltip: "How far a thumbstick has to be pushed before it counts. Raise it if you drift when the sticks are at rest."));

        private static readonly NumberSetting shipStickSensitivity = Add(new NumberSetting("shipStickSensitivity", "Ship stick sensitivity", "Controls",
            1f, 0.25f, 3f, 0.05f, decimals: 2,
            tooltip: "How fast the right stick and the grips turn a ship, a turret or a camera. 1 matches a gamepad pushed all the way."));

        private static readonly FlagSetting handFlying = Add(new FlagSetting("handFlying", "Hand flying", "Controls", false,
            tooltip: "In a ship seat, press a grip with your hand by the stick (dominant hand) or throttle (other hand) to take hold of it, and fly by moving the controller. Off: thumbsticks and grips only."));

        private static readonly NumberSetting stickMaxAngle = Add(new NumberSetting("stickMaxAngle", "Stick full deflection", "Controls",
            25f, 10f, 45f, 1f, unit: "deg", decimals: 0,
            tooltip: "How far you tilt or twist the held stick for full pitch, roll or yaw. Smaller is twitchier."));

        private static readonly NumberSetting throttleTravel = Add(new NumberSetting("throttleTravel", "Throttle travel", "Controls",
            0.10f, 0.05f, 0.25f, 0.01f, unit: "cm", decimals: 0, displayScale: 100f,
            tooltip: "How far you push the held throttle for full thrust in that direction. Smaller is twitchier."));

        private static readonly NumberSetting handFlyingSensitivity = Add(new NumberSetting("handFlyingSensitivity", "Hand flying sensitivity", "Controls",
            1f, 0.25f, 3f, 0.05f, decimals: 2,
            tooltip: "How fast the held stick turns a ship. 1 matches a gamepad pushed all the way."));

        private static readonly FlagSetting leftHanded = Add(new FlagSetting("leftHanded", "Left-handed", "Controls", false,
            tooltip: "Point the menu laser with your left hand: its trigger clicks, its grip right-clicks, its thumbstick scrolls. Off: the right hand."));

        private static readonly NumberSetting hapticStrength = Add(new NumberSetting("hapticStrength", "Haptic strength", "Controls",
            1f, 0f, 1f, 0.05f, unit: "%", decimals: 0, displayScale: 100f,
            tooltip: "How hard the controllers buzz when you fire, weld, grind or drill, place a block or get hit. 0 turns it off."));
        private static readonly FlagSetting grabToUse = Add(new FlagSetting("grabToUse", "Grab to use", "Controls", true,
            tooltip: "On foot, closing the grip with your hand on a seat, cryo pod, door, medical room, survival kit, cargo container or terminal block uses it, as the Use key does. A door has to be pulled or pushed a hand's width while held. With nothing in reach the right grip is still Use along your hand's ray. Not while that hand holds a tool, a menu is up or you are on a ladder or seated."));

        private static readonly FlagSetting wristPanel = Add(new FlagSetting("wristPanel", "Wrist panel", "Controls", true,
            tooltip: "On foot and on the jetpack, Y on the left controller opens the quick-actions panel on your left wrist (inventory, terminal, G menu, lights, helmet, dampeners, broadcast, symmetry, colour picker); Y again closes it. Off: Y does nothing there."));

        private static readonly FlagSetting wristLook = Add(new FlagSetting("wristLook", "Wrist panel on look", "Controls", true,
            tooltip: "On foot and on the jetpack, looking at the gauntlet on your astronaut's left forearm opens the wrist panel, and looking away closes it: turn your left forearm up with the back of the wrist toward your eyes and look at it for a moment. Y still opens it, and a panel opened with Y stays until Y or a button closes it. Not while the left grip is down, in a seat, or with a menu open. Needs Wrist panel on; off with Left-handed on, since the left hand then points the laser and cannot press its own wrist."));

        private static readonly FlagSetting fingerPress = Add(new FlagSetting("fingerPress", "Press with fingertip", "Controls", true,
            tooltip: "On foot, touching a push-button with the tip of a controller's index finger presses it, as the Use key does: the buttons of a button panel, and the next, previous and pause buttons of a jukebox. Doors, seats, terminals and cargo are not touched. Not while that hand holds a tool, a menu is up or you are on a ladder or seated. Off: only the Use key and the hand's ray."));

        private static readonly FlagSetting holsters = Add(new FlagSetting("holsters", "Holsters", "Controls", true,
            tooltip: "On foot, draw tools from your body: grip with the tool hand at your hip for the welder, at the other hip for the grinder, over your shoulder for the rifle, over the other shoulder for the hand drill (the best one you carry). Let go of the grip back at the holster to put it away. Grip the helmet with either hand to open or close it; tap the side of your head with a fingertip for the suit lamp. A grip anywhere else keeps its old job (Use, the toolbar wheel). Off: the toolbar, the wheel and the keys only."));
        private static readonly FlagSetting grabItems = Add(new FlagSetting("grabItems", "Grab items", "Controls", true,
            tooltip: "On foot, close a hand's grip on a floating item (ore, ice, components, a dropped tool) to take it out of the air and hold it; open the hand and it flies on at the hand's speed. Let go of it at your chest and it goes into your inventory. Heavy stacks come along slowly. Off: a grip near an item means what it does in empty air."));
        private static readonly FlagSetting grabHulls = Add(new FlagSetting("grabHulls", "Grab hulls", "Controls", true,
            tooltip: "With no gravity, press a grip with your hand on a ship's or station's hull to take hold of it, and pull or push to move along it, as in Lone Echo. Let go and you keep going at the speed your hand moved you (up to 6 m/s); the jetpack's dampeners stop you only if they are on. Off: a grip on a hull is Use or the toolbar wheel, as in empty air."));

        private static readonly FlagSetting grabLadders = Add(new FlagSetting("grabLadders", "Grab ladders", "Controls", true,
            tooltip: "Press a grip with your hand on a ladder to get on it, then pull your hand down to climb a step up, or push it up to climb down. Off: ladders by the Use key and the move stick only."));

        private static readonly FlagSetting twoHandedTools = Add(new FlagSetting("twoHandedTools", "Two-handed tools", "Controls", true,
            tooltip: "On foot, close your other hand's grip on the front of the tool or gun you hold, where the astronaut's other hand goes, to hold it with both hands: it then points along the line from your tool hand to your other hand. A light tick tells you the hand is close enough. Let go, or pull the hand away, and it goes back to one hand. A pistol is steadied but still aims with the tool hand. A grip anywhere else keeps its old job (the toolbar wheel). Off: tools in one hand only."));

        private static readonly FlagSetting controlHints = Add(new FlagSetting("controlHints", "Control hints", "Controls", true,
            tooltip: "Short reminders on the HUD: \"Hold Y to leave the seat\" when you sit down (the first three times each session), and \"B: back, right stick: scroll\" the first time a menu opens, where the tools wheel and the wrist panel are the first time you walk, and how to move an item to the other inventory the first time the inventory opens. Off: none. B and the left Menu button close a menu either way."));

        private static readonly FlagSetting vrButtonNames = Add(new FlagSetting("vrButtonNames", "VR button names", "Controls", true,
            tooltip: "The game's prompts name the controller buttons instead of the keys: \"Press [Right grip] to open\", \"[A] to jump\", \"[Y > Inventory]\" for what the wrist panel opens. A control the controllers have no way to press keeps its key. Off: the keyboard's names."));

        private static readonly FlagSetting vrRadialMenu = Add(new FlagSetting("vrRadialMenu", "VR radial menus", "Controls", true,
            tooltip: "The tools wheel and the game's actions wheel say which controller buttons work them, and the game's actions wheel leaves out what has no use in VR: the keyboard help screen, the spectator camera, and the camera switch while third person is off. Off: the game's wheels as they are on a gamepad."));

        private static readonly FlagSetting vrActionsWheel = Add(new FlagSetting("vrActionsWheel", "VR actions wheel", "Controls", true,
            tooltip: "Clicking the left stick on the tools wheel opens the game's actions (inventory, dampeners, lights, helmet, HUD, broadcasting, and the Menu, View and Blueprint pages) as a wheel at your left hand: point at an entry with the hand or the stick, let go of the grip or pull the trigger to use it, B to close. Off: the game's own gamepad radial screen."));

        private static readonly FlagSetting enabled = Add(new FlagSetting("enabled", "VR enabled", "Advanced", true, restart: true, hidden: true,
            // Not in the menu: turned off there, VR would have no menu to turn it back on. enabled=false in the file, or -novr.
            tooltip: "Start the game with VR."));

        private static readonly NumberSetting ipd = Add(new NumberSetting("ipd", "Fallback IPD", "Advanced",
            0.064f, 0.04f, 0.09f, 0.001f, unit: "mm", decimals: 0, displayScale: 1000f,
            tooltip: "Distance between the eyes, used when no headset gives the real one (-ipd)."));

        private static readonly NumberSetting mirrorWidth = Add(new NumberSetting("mirrorWidth", "Mirror width", "Advanced",
            1920f, 640f, 3840f, 40f, unit: "px", decimals: 0, restart: true, integer: true,
            tooltip: "Width of the game window on your monitor (-vrmirror 1920x1080). Your own resolution is left alone."));

        private static readonly NumberSetting mirrorHeight = Add(new NumberSetting("mirrorHeight", "Mirror height", "Advanced",
            1080f, 360f, 2160f, 40f, unit: "px", decimals: 0, restart: true, integer: true,
            tooltip: "Height of the game window on your monitor."));

        // ---- What the rest of the plugin reads. ----

        /// <summary>-novr: load the plugin but leave the game untouched.</summary>
        public static bool Enabled => enabled.Value;

        /// <summary>Interpupillary distance used when no headset supplies eye poses (-ipd 0.064), metres.</summary>
        public static float FallbackIpd => ipd.Value;

        /// <summary>Size of the desktop mirror window (-vrmirror 1920x1080).</summary>
        public static int MirrorWidth => (int)mirrorWidth.Value;

        public static int MirrorHeight => (int)mirrorHeight.Value;

        /// <summary>The game's toolbar stays on the HUD on foot too; off (the default) it is left out there (<see cref="Rendering.HudToolbar"/>).</summary>
        public static bool ShowToolbarOnFoot => showToolbarOnFoot.Value;

        /// <summary>How far ahead of the eyes the GUI and HUD panel floats, metres (-hudDistance 1.5).</summary>
        public static float HudDistance => hudDistance.Value;

        /// <summary>How wide the GUI and HUD panel looks, degrees (-hudWidth 55); its height follows the GUI's aspect.</summary>
        public static float HudWidthDegrees => hudWidth.Value;

        /// <summary>How strongly the comfort vignette darkens the edges of the view while the sticks move or turn you, 0 (off) to 1 (<see cref="Rendering.Vignette"/>).</summary>
        public static float ComfortVignette => comfortVignette.Value;
        /// <summary>The external view screen is on. The actions wheel turns it on and off, and so does the options screen.</summary>
        public static bool ExternalViewOn
        {
            get => externalView.Value;
            set => externalView.Set(value);
        }

        /// <summary>The external view shows one of the ship's camera blocks (<see cref="ExternalViewCameraId"/>) instead of the chase camera.</summary>
        public static bool ExternalViewIsCamera
        {
            get => externalViewSource.Value == 1;
            set => externalViewSource.Set(value ? 1 : 0);
        }

        /// <summary>The camera block the external view shows when it is on a camera: its entity id; 0 for none.</summary>
        public static long ExternalViewCameraId
        {
            get
            {
                SplitExternalViewCamera(out long id, out _);
                return id;
            }
        }

        /// <summary>That camera's name, as it was when it was chosen.</summary>
        public static string ExternalViewCameraName
        {
            get
            {
                SplitExternalViewCamera(out _, out string name);
                return name;
            }
        }

        /// <summary>Remembers the camera block the external view shows: its entity id, and its name (for the label, and to find it again if the id is gone).</summary>
        public static void SetExternalViewCamera(long entityId, string name) =>
            externalViewCamera.Set(entityId.ToString(CultureInfo.InvariantCulture) + "|" + (name ?? string.Empty));

        private static void SplitExternalViewCamera(out long id, out string name)
        {
            id = 0L;
            name = string.Empty;
            string value = externalViewCamera.Value;
            int bar = value.IndexOf('|');
            if (bar <= 0 || !long.TryParse(value.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                id = 0L;
                return;
            }
            name = value.Substring(bar + 1);
        }

        /// <summary>How wide the external view screen is in the headset, metres; its height follows the picture's 16:9.</summary>
        public static float ExternalViewSize => externalViewSize.Value;

        /// <summary>The external view is drawn every other frame.</summary>
        public static bool ExternalViewHalfRate => externalViewHalfRate.Value;
        /// <summary>Third person in a ship: the camera keeps the horizon level (<see cref="Tracking.ChaseCamera"/>) instead of rolling with the ship.</summary>
        public static bool LevelChaseCamera => levelChaseCamera.Value;

        /// <summary>Your own view may be third person (<see cref="Tracking.ThirdPersonGate"/>); off, it stays first person on foot and in every seat.</summary>
        public static bool ThirdPerson => thirdPerson.Value;

        /// <summary>How far ahead of the head a menu's panel is put, where it stays, metres (-menuDistance 2).</summary>
        public static float MenuDistance => menuDistance.Value;

        /// <summary>How wide a menu's panel looks from the head, degrees (-menuWidth 60); its height follows the GUI's aspect.</summary>
        public static float MenuWidthDegrees => menuWidth.Value;

        /// <summary>How wide the panel looks while no world is loaded (the main menu and the screens off it), degrees; the world's menus use <see cref="MenuWidthDegrees"/>.</summary>
        public static float MainMenuWidthDegrees => mainMenuWidth.Value;

        /// <summary>Clicking a text box (or the game asking for a virtual keyboard) opens the on-screen keyboard while the hands are active (<see cref="Gui.VRKeyboardHook"/>).</summary>
        public static bool VRKeyboard => vrKeyboard.Value;

        /// <summary>
        /// Seated play (the play position): the eye's height is taken at the recentre and the calibration is not used, and a
        /// real crouch does not crouch the character (<see cref="Tracking.HeadCentre"/>, <see cref="Tracking.BodyGuard"/>).
        /// False: standing play. RenderDebug SeatedPlay=0/1 overrides the setting (<see cref="SeatedPlayOverride"/>).
        /// </summary>
        public static bool SeatedPlay => SeatedPlayOverride ?? playPosition.Value == 1;

        /// <summary>RenderDebug SeatedPlay=1 (seated) or 0 (standing) in place of the setting; null: the setting.</summary>
        public static bool? SeatedPlayOverride { get; set; }

        /// <summary>
        /// The player's eye height above the floor when standing, metres, from height calibration
        /// (<see cref="Tracking.HeightCalibration"/>); in standing play the character's eyes are put there. Null: not
        /// calibrated, and the view's height is the headset's height relative to where it was at the last recentre (or where
        /// tracking started).
        /// </summary>
        public static float? EyeHeight
        {
            get => eyeHeight.Effective;
            set => eyeHeight.Set(value);
        }

        /// <summary>Raises (+) or lowers (-) the view, metres, on top of the calibration.</summary>
        public static float HeightOffset
        {
            get => heightOffset.Value;
            set => heightOffset.Set(value);
        }

        /// <summary>On foot, the eye is kept out of the character's own body (<see cref="Tracking.BodyGuard"/>).</summary>
        public static bool BodyClearance => bodyClearance.Value;

        /// <summary>On foot, a real crouch of the head crouches the character (<see cref="Tracking.BodyGuard"/>).</summary>
        public static bool CrouchWithHead => crouchWithHead.Value;

        /// <summary>
        /// In a seat, how far forward, up and down the head can go from the seated character's head, metres; sideways is four
        /// fifths of it, back is the body's (<see cref="Tracking.BodyVolume.LockSeat"/>, <see cref="Tracking.CockpitHead"/>).
        /// </summary>
        public static float SeatReach => seatReach.Value;

        /// <summary>On foot, the head is held within reach of the character's head (<see cref="Tracking.BodyGuard"/>).</summary>
        public static bool HeadLock => headLock.Value;

        /// <summary>On foot, how far forward the head can go from the character's head, metres; sideways is four fifths of it, back is the body's (<see cref="Tracking.BodyVolume.LockHead"/>).</summary>
        public static float HeadReach => headReach.Value;

        /// <summary>How far one flick of the right stick turns the character, degrees.</summary>
        public static float SnapTurnDegrees => snapTurn.Value;

        /// <summary>The right stick turns the body steadily (<see cref="SmoothTurnSpeedDegrees"/>) instead of in snap-turn steps.</summary>
        public static bool SmoothTurn => smoothTurn.Value;

        /// <summary>Degrees a second the body turns with the right stick pushed all the way over, when <see cref="SmoothTurn"/> is on.</summary>
        public static float SmoothTurnSpeedDegrees => smoothTurnSpeed.Value;

        /// <summary>How hard the controllers buzz, 0 (off) to 1 (the strengths in <see cref="Hands.Haptics"/> as they are).</summary>
        public static float HapticStrength => hapticStrength.Value;

        /// <summary>How far a thumbstick has to be pushed before it counts, 0 to 1.</summary>
        public static float StickDeadzone => stickDeadzone.Value;

        /// <summary>How hard the right stick and the grips turn a ship, a turret or a camera, against a gamepad at full push (1).</summary>
        public static float ShipStickSensitivity => shipStickSensitivity.Value;

        /// <summary>Grab the stick and throttle in a ship seat and fly by moving the controllers (<see cref="Input.HandFlight"/>).</summary>
        public static bool HandFlying => handFlying.Value;

        /// <summary>How far the held stick is tilted or twisted for full pitch, roll or yaw, degrees.</summary>
        public static float StickMaxAngle => stickMaxAngle.Value;

        /// <summary>How far the held throttle is pushed for full thrust, metres.</summary>
        public static float ThrottleTravel => throttleTravel.Value;

        /// <summary>How hard the held stick turns a ship against a gamepad at full push (1).</summary>
        public static float HandFlyingSensitivity => handFlyingSensitivity.Value;

        /// <summary>The hand that points the menu laser (<see cref="Input.Laser"/>). Right unless "Left-handed" is on.</summary>
        public static Input.Hand DominantHand => leftHanded.Value ? Input.Hand.Left : Input.Hand.Right;

        /// <summary>Y opens the quick-actions panel on the wrist (<see cref="Input.WristButton"/>).</summary>
        public static bool WristPanel => wristPanel.Value;

        /// <summary>Looking at the astronaut's gauntlet opens the wrist panel (<see cref="Input.WristLook"/>).</summary>
        public static bool WristLook => wristLook.Value;

        /// <summary>Closing the grip on a seat, door, medical room, cargo container or terminal block uses it (<see cref="Hands.GrabUse"/>).</summary>
        public static bool GrabToUse => grabToUse.Value;

        /// <summary>Touching a use object with a fingertip presses it (<see cref="Hands.FingerPress"/>).</summary>
        public static bool FingerPress => fingerPress.Value;

        /// <summary>Tools from the body, the helmet and the lamp by hand (<see cref="Hands.Holsters"/>).</summary>
        public static bool Holsters => holsters.Value;
        /// <summary>A grip on a floating item holds it, opening the hand throws it, letting go at the chest stows it (<see cref="Hands.ItemGrab"/>).</summary>
        public static bool GrabItems => grabItems.Value;
        /// <summary>With no gravity, a grip on a hull takes hold of it and the body follows the hand (<see cref="Hands.HullGrab"/>).</summary>
        public static bool GrabHulls => grabHulls.Value;

        /// <summary>A grip on a ladder gets on it and climbs it by the hand's pull (<see cref="Hands.HullGrab"/>).</summary>
        public static bool GrabLadders => grabLadders.Value;

        /// <summary>The other hand's grip on the held tool's front grip holds it in both hands, aimed along the line between them (<see cref="Hands.TwoHand"/>).</summary>
        public static bool TwoHandedTools => twoHandedTools.Value;

        /// <summary>The HUD reminders for leaving a seat and for backing out of a menu (<see cref="Input.ControlHints"/>).</summary>
        public static bool ControlHints => controlHints.Value;

        /// <summary>Prompts name the controller buttons (<see cref="Input.VRPrompts"/>).</summary>
        public static bool VRButtonNames => vrButtonNames.Value;

        /// <summary>The radial menus are labelled for the controllers and leave out what VR has no use for (<see cref="Gui.VRRadialMenus"/>).</summary>
        public static bool VRRadialMenu => vrRadialMenu.Value;

        /// <summary>The left stick click on the tools wheel opens the actions wheel at the hand, not the game's radial screen (<see cref="Gui.ActionsWheel"/>).</summary>
        public static bool VRActionsWheel => vrActionsWheel.Value;

        // ---- The list, for the options screen. ----

        /// <summary>Every setting, in the order they were declared.</summary>
        public static IReadOnlyList<Setting> All => all;

        /// <summary>How many times <see cref="Save"/> has written the file. The options screen uses it to see whether anything saved while it was open.</summary>
        public static int SaveCount { get; private set; }

        /// <summary>The settings file: %APPDATA%\SpaceEngineers\SpaceEngineersVR.cfg.</summary>
        public static string FilePath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineers", "SpaceEngineersVR.cfg");

        private static T Add<T>(T setting) where T : Setting
        {
            if (byKey.ContainsKey(setting.Key))
            {
                // Programming error. Keep the plugin running: the first one declared wins.
                Log.Error($"Setting '{setting.Key}' is declared twice; the second is ignored");
                return setting;
            }
            all.Add(setting);
            byKey.Add(setting.Key, setting);
            return setting;
        }

        /// <summary>Writes every setting to <see cref="FilePath"/>: to a temporary file first, then swapped in, so a crash cannot leave half a file. Never throws.</summary>
        public static void Save()
        {
            lock (Gate)
            {
                string path = FilePath;
                string temp = path + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(temp, Render(), new UTF8Encoding(false));
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                    SaveCount++;
                    Log.Info("Settings saved to " + path);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not save the settings to " + path);
                    try
                    {
                        if (File.Exists(temp))
                            File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // The failure is already logged; a stray temporary file is harmless.
                    }
                }
            }
        }

        private static string Render()
        {
            var text = new StringBuilder();
            text.AppendLine("# Space Engineers VR settings. One key=value per line; lines starting with # are comments.");
            text.AppendLine("# Options > VR in the game rewrites this file. Edit it only while the game is closed.");
            text.AppendLine("# Command-line arguments (for example -hudDistance 2) override these for that run.");
            string section = null;
            foreach (Setting setting in all)
            {
                if (setting.Section != section)
                {
                    section = setting.Section;
                    text.AppendLine();
                    text.AppendLine("# " + section);
                }
                text.AppendLine("# " + setting.Describe());
                text.AppendLine(setting.Key + "=" + setting.Serialize());
            }
            return text.ToString();
        }

        /// <summary>Everything to its default, then the settings file, then the command line.</summary>
        public static void Load(string[] args)
        {
            lock (Gate)
            {
                foreach (Setting setting in all)
                    setting.Clear();
                LoadFile();
                if (args != null)
                    LoadArguments(args);
            }
        }

        private static void LoadFile()
        {
            string path = FilePath;
            string[] lines;
            try
            {
                if (!File.Exists(path))
                {
                    Log.Info("No settings file at " + path + "; using the defaults");
                    return;
                }
                lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not read the settings file " + path + "; using the defaults");
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                string where = $"Settings file line {i + 1}";
                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    Log.Warn($"{where}: expected key=value, got '{line}'; skipped");
                    continue;
                }
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1);
                int comment = value.IndexOf('#');
                if (comment >= 0)
                    value = value.Substring(0, comment);
                value = value.Trim();

                if (!byKey.TryGetValue(key, out Setting setting))
                {
                    Log.Warn($"{where}: unknown setting '{key}'; skipped");
                    continue;
                }
                if (!seen.Add(setting.Key))
                    Log.Warn($"{where}: '{setting.Key}' is set again; this one wins if it is valid");
                if (!setting.TryLoad(value, out string problem))
                    Log.Warn($"{where}: {setting.Key}: {problem}; keeping '{setting.Serialize()}'");
            }
            Log.Info("Settings read from " + path);
        }

        private static void LoadArguments(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("-novr", StringComparison.OrdinalIgnoreCase))
                    enabled.Force(false);
                else if (arg.Equals("-vrmirror", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string[] size = args[i + 1].Split('x', 'X');
                    if (size.Length == 2 && int.TryParse(size[0], out int w) && int.TryParse(size[1], out int h) && w > 0 && h > 0)
                    {
                        mirrorWidth.Force(w);
                        mirrorHeight.Force(h);
                    }
                }
                else if (arg.Equals("-ipd", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ipdValue))
                    ipd.Force(ipdValue);
                else if (arg.Equals("-hudDistance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float distance) && distance > 0f)
                    hudDistance.Force(distance);
                else if (arg.Equals("-hudWidth", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float width) && width > 0f && width < 170f)
                    hudWidth.Force(width);
                else if (arg.Equals("-menuDistance", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float menuDistanceValue) && menuDistanceValue > 0f)
                    menuDistance.Force(menuDistanceValue);
                else if (arg.Equals("-menuWidth", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                         && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float menuWidthValue) && menuWidthValue > 0f && menuWidthValue < 170f)
                    menuWidth.Force(menuWidthValue);
            }
        }
    }
}
