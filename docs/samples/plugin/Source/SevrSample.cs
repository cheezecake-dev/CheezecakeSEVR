using System.Globalization;
using System.IO;
using System.Text;
using SevrIntegration;
using VRage.Utils;
using VRageMath;

namespace SevrSamplePlugin
{
    /// <summary>
    /// Everything this sample does with Cheezecake's SE VR's API (docs/MOD_API.md), through SevrClient.cs: no reference to the
    /// VR plugin. Without the VR plugin, or with one older than version 2, SevrClient logs that once and this does nothing more.
    /// Every line it writes to the game's log starts "SevrSample: plugin:".
    /// </summary>
    internal static class SevrSample
    {
        /// <summary>The full path of the plugin's own icon, as Pulsar delivered it (Plugin.LoadAssets); null without it.</summary>
        internal static string IconPath;

        private const string Beacon = "beacon";

        // Actions on the tools wheel, enough to give the wrist panel's Controls page more rows than it can show at full size once
        // they are bound. Each suggestion is given only where it clashes with nothing, so most start unbound: the player binds
        // them on the bindings page.
        private static readonly string[][] Fillers =
        {
            new[] { "x01", "Sample 01", "Right.ThumbstickClick.Press" },
            new[] { "x02", "Sample 02", "Right.ThumbstickClick.Hold@1" },
            new[] { "x03", "Sample 03", "Right.Grip.Press" },
            new[] { "x04", "Sample 04", "Right.Grip.Hold@1" },
            new[] { "x05", "Sample 05", "Right.ButtonLower.Press+Right.Grip" },
            new[] { "x06", "Sample 06", "Right.ButtonLower.Press+Right.Trigger" },
            new[] { "x07", "Sample 07", "Right.ButtonLower.Press+Left.Trigger" },
            new[] { "x08", "Sample 08", "Right.ButtonUpper.Press+Right.Grip" },
            new[] { "x09", "Sample 09", "Right.ButtonUpper.Press+Right.Trigger" },
            new[] { "x10", "Sample 10", "Right.ButtonUpper.Press+Left.Trigger" },
            new[] { "x11", "Sample 11", "Right.ThumbstickClick.Press+Right.Grip" },
            new[] { "x12", "Sample 12", "Right.ThumbstickClick.Press+Right.Trigger" },
            new[] { "x13", "Sample 13", "Right.ThumbstickClick.Press+Left.Trigger" },
            new[] { "x14", "Sample 14", "Right.Grip.Press+Left.Trigger" },
            new[] { "x15", "Sample 15", "Right.Trigger.Press+Right.Grip" },
            new[] { "x16", "Sample 16", "Right.Trigger.Press+Left.Trigger" },
            new[] { "x17", "Sample 17", "Right.ButtonUpper.Hold@1" },
            new[] { "x18", "Sample 18", "Right.Menu.Press" },
            new[] { "x19", "Sample 19", "Left.ButtonLower.Tap" },
            new[] { "x20", "Sample 20", "Left.ButtonUpper.Tap" },
        };

        private static bool registered;
        private static int heldFrames, wheelUses, tileUses;

        /// <summary>Finds the API and registers everything. Called once, from the plugin's Init.</summary>
        public static void Connect()
        {
            if (!SevrClient.Connect("SevrSamplePlugin", null))
                return;
            Log("found the API, version " + SevrClient.Version + ", IsActive " + SevrClient.IsActive);
            Register();
        }

        /// <summary>
        /// The actions, menu items and event handlers. Registering again replaces them in place and keeps the player's bindings.
        /// </summary>
        private static void Register()
        {
            bool named = SevrClient.SetOwnerName("SEVR sample plugin");

            // On the tools wheel (the left grip held): the right A. A Press is on while the button is down.
            bool beacon = SevrClient.RegisterAction(Beacon, "Sample beacon", SevrContext.ToolsWheel, "Right.ButtonLower.Press", OnBeacon);

            int fillersRegistered = 0;
            foreach (string[] filler in Fillers)
            {
                string id = filler[0];
                if (SevrClient.RegisterAction(id, filler[1], SevrContext.ToolsWheel, filler[2],
                        on => Log("action " + id + " " + (on ? "on" : "off")), () => true))
                    fillersRegistered++;
            }

            // The plugin's own icon, which Pulsar delivered as an asset (Plugin.LoadAssets); without it, one of the game's.
            string icon = IconPath != null && File.Exists(IconPath) ? IconPath : Path.Combine("Textures", "GUI", "Icons", "Cubes", "Battery.dds");
            Log("icon: " + icon);
            bool wheelItem = SevrClient.RegisterMenuItem(SevrMenu.ActionsWheel, "beacon", "Sample beacon", icon, () =>
            {
                wheelUses++;
                Log("actions wheel item used (" + wheelUses + " times); Pulse left " + SevrClient.Pulse(SevrHand.Left, 0.8, 0.2));
            }, state: () => "Used " + wheelUses + " times");

            // Stays open: the wrist panel stays up after the tile is used. Using it re-registers everything, as a plugin that
            // reloads its settings would: Unregister, then the same registrations again.
            bool wristTile = SevrClient.RegisterMenuItem(SevrMenu.WristPanel, "reload", "Sample reload", icon, () =>
            {
                tileUses++;
                Log("wrist panel tile used (" + tileUses + " times): Unregister " + SevrClient.Unregister() + ", registering again");
                Register();
            }, state: () => "Re-registers the sample", stayOpen: true);

            registered = beacon;
            Log("registered: owner name " + named + ", beacon " + beacon + ", fillers " + fillersRegistered + " of " + Fillers.Length
                + ", wheel item " + wheelItem + ", wrist tile " + wristTile);

            // Events are raised for changes only: what holds now is logged with the registration. Unregister takes handlers
            // away too, so they are subscribed with everything else.
            Log("subscribed: ActiveChanged " + SevrClient.Subscribe(SevrEvent.ActiveChanged, OnActiveChanged)
                + ", ContextChanged " + SevrClient.Subscribe(SevrEvent.ContextChanged, OnContextChanged)
                + ", BindingsChanged " + SevrClient.Subscribe(SevrEvent.BindingsChanged, OnBindingsChanged)
                + ", DominantHandChanged " + SevrClient.Subscribe(SevrEvent.DominantHandChanged, () => Log("DominantHandChanged: now " + (SevrClient.DominantHand == SevrHand.Right ? "right" : "left") + "-handed"))
                + ", HudChanged " + SevrClient.Subscribe(SevrEvent.HudChanged, () => Log("HudChanged: HudVisible " + SevrClient.HudVisible)));
            LogLabels();
            LogState();
        }

        /// <summary>Plugin registrations last the run; this is only tidiness at the game's end.</summary>
        public static void Disconnect()
        {
            if (SevrClient.Connected)
                Log("Unregister at the game's end: " + SevrClient.Unregister());
            registered = false;
        }

        /// <summary>Polling, once a frame: the same frames the callback sees.</summary>
        public static void Update()
        {
            if (!registered)
                return;
            if (SevrClient.ActionStarted(Beacon))
            {
                heldFrames = 0;
                Log("polled: beacon started this frame (ActionHeld " + SevrClient.ActionHeld(Beacon) + ")");
            }
            if (SevrClient.ActionHeld(Beacon))
                heldFrames++;
            if (SevrClient.ActionEnded(Beacon))
                Log("polled: beacon ended this frame, held " + heldFrames + " frames");
        }

        private static void OnBeacon(bool on)
        {
            if (on)
                Log("beacon callback: on; Pulse right " + SevrClient.Pulse(SevrHand.Right, 0.6, 0.15));
            else
                Log("beacon callback: off");
        }

        private static void OnActiveChanged()
        {
            Log("ActiveChanged: IsActive " + SevrClient.IsActive + ", HudVisible " + SevrClient.HudVisible);
            LogState();
        }

        private static void OnContextChanged()
        {
            Log("ContextChanged: '" + SevrClient.Context.ToString().Replace(" ", "") + "'");
            LogState();
        }

        private static void OnBindingsChanged()
        {
            Log("BindingsChanged");
            LogLabels();
        }

        private static void LogLabels()
        {
            var text = new StringBuilder("labels: beacon '").Append(SevrClient.ActionLabel(Beacon)).Append("'");
            int bound = 0;
            foreach (string[] filler in Fillers)
            {
                string label = SevrClient.ActionLabel(filler[0]);
                if (label.Length > 0)
                    bound++;
                text.Append(", ").Append(filler[0]).Append(" '").Append(label).Append("'");
            }
            text.Append("; fillers bound ").Append(bound).Append(" of ").Append(Fillers.Length);
            Log(text.ToString());
        }

        private static void LogState()
        {
            Log("state: dominant " + (SevrClient.DominantHand == SevrHand.Right ? "right" : "left")
                + ", context '" + SevrClient.Context.ToString().Replace(" ", "") + "'"
                + ", profiles '" + SevrClient.ControllerProfile(SevrHand.Left) + "' / '" + SevrClient.ControllerProfile(SevrHand.Right) + "'"
                + ", tracked " + SevrClient.HandTracked(SevrHand.Left) + "/" + SevrClient.HandTracked(SevrHand.Right)
                + ", right grip at " + Format(SevrClient.HandGripPose(SevrHand.Right).Translation)
                + ", right aim forward " + Format(SevrClient.HandAimPose(SevrHand.Right).Forward)
                + ", head at " + Format(SevrClient.HeadPose.Translation)
                + ", right trigger " + Format(SevrClient.HandTrigger(SevrHand.Right)) + ", left squeeze " + Format(SevrClient.HandSqueeze(SevrHand.Left))
                + ", left stick " + Format(SevrClient.HandStick(SevrHand.Left).X) + "," + Format(SevrClient.HandStick(SevrHand.Left).Y));
        }

        private static string Format(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string Format(Vector3D value)
        {
            return "(" + Format(value.X) + ", " + Format(value.Y) + ", " + Format(value.Z) + ")";
        }

        private static void Log(string text)
        {
            MyLog.Default.WriteLine("SevrSample: plugin: " + text);
        }
    }
}
