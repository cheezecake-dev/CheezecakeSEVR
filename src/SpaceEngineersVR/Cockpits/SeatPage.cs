// Adapted from ColbyJack1134's SpaceEngineersVR (Player/SeatPanel.cs), with his permission.
// Phase 7 of docs/COCKPIT_PORT.md. His seat panel's keys, moved onto our quick menu (QuickActionsScreen) as a page of its own,
// shown only in a seat his rig owns: his seat arrows, his seat reset, his stick padlock and stick reset, and his Flight
// Settings key. The panel itself, a world surface by the seat, is not drawn any more: it lagged the cockpit in motion and stood
// out of the cabin. The page is the cockpit's: its Flight settings and Assign switches come first, where a player looks for a
// cockpit's settings, then the seat and sticks. Not carried from it: his Power, Dampeners, Park, Lights and Broadcast keys, which the quick menu's own
// seated page and the game's controls already give. His arrows moved the seat at 12 cm/s while held; here each press is one
// step of StepMetres, seen at once with the panel still up.
using System;
using SpaceEngineersVR.Gui;
using VRageMath;

namespace SpaceEngineersVR.Cockpits
{
    internal static class SeatPage
    {
        private const string Hud = GuiTextures.Hud;

        /// <summary>The game's white chevron (it points right), turned for each way the seat goes.</summary>
        private const string Chevron = "Textures\\GUI\\Icons\\buttons\\ArrowSingle.dds";

        internal const string PageIcon = Hud + "CockpitBuildMode.png";

        /// <summary>The game's own button panel icon, for the switches' assignment.</summary>
        private const string SwitchesIcon = @"Textures\GUI\Icons\Cubes\ButtonPanel.dds";

        /// <summary>Metres per press: fine enough to fit the eye to the canopy, and his whole travel in about twenty presses.</summary>
        public const float StepMetres = 0.02f;

        /// <summary>The page has a seat to act on: his rig owns the seat the player is in (SeatFit's own rule).</summary>
        internal static bool Available => SeatFit.Eligible(SeatFit.Seat);

        /// <summary>The seat has his sticks (not a wheel or handlebars) for the padlock and the stick reset.</summary>
        private static bool Sticks => Available && !CockpitControls.IsSteering;

        /// <summary>The tile on the seated quick actions that opens this page.</summary>
        internal static readonly QuickActionsScreen.QuickAction Tile = new QuickActionsScreen.QuickAction("Cockpit",
            "This cockpit's settings: how its sticks and levers fly, what its switches do, where you sit and where its sticks are.", PageIcon, null,
            stateText: () => SeatFit.AtDefault ? null : "Moved", canUse: () => Available);

        /// <summary>The page in reading order, three across.</summary>
        internal static QuickActionsScreen.QuickAction[] Build(Action back)
        {
            return new[]
            {
                new QuickActionsScreen.QuickAction("Back", "Back to the quick actions.", GuiTextures.Back, back),
                new QuickActionsScreen.QuickAction("Flight settings",
                    "How this cockpit's sticks, wheel and levers fly it: sensitivity, curves, deadzones, which throttle levers spring back to zero, and how your hands hold them.",
                    Hud + "Speedometer.png", FlightSettings.Open, canUse: () => Available && !FlightSettings.IsOpen, closesPanel: true),
                new QuickActionsScreen.QuickAction("Assign switches",
                    "What this cockpit's switches, buttons and handles do: the toolbar screen opens on them. You can also touch one and tap Y.",
                    SwitchesIcon, AssignSwitches, canUse: () => Available && CockpitActions.Toolbar != null && !CockpitActions.Editing, closesPanel: true),

                new QuickActionsScreen.QuickAction("Move sticks",
                    "Unlocks the sticks: grip one and move it to where your hand rests, then come back here to lock them where they are.",
                    Hud + "BlockRotate.png", ToggleSticks, stateText: () => CockpitControls.Adjusting ? "Unlocked" : "Locked",
                    state: () => CockpitControls.Adjusting, canUse: () => Sticks, closesPanel: true),
                Arrow("Seat forward", "Moves your view forward in the seat.", Vector3.Forward, () => Along(-SeatFit.Offset.Z, -Default.Z, "forward", "back"), -MathHelper.PiOver2),
                Arrow("Seat up", "Moves your view up in the seat.", Vector3.Up, () => Along(SeatFit.Offset.Y, Default.Y, "up", "down"), 0f, Hud + "ValueIncrease.png"),

                Arrow("Seat left", "Moves your view left in the seat.", Vector3.Left, () => Along(-SeatFit.Offset.X, -Default.X, "left", "right"), MathHelper.Pi),
                new QuickActionsScreen.QuickAction("Reset seat", "Puts your view back where this cockpit has it.", Hud + "ReloadGame.png",
                    () => { SeatFit.ResetFit(); Log.Info("ColbyCockpit: quick menu reset the seat fit"); },
                    stateText: () => SeatFit.AtDefault ? "Cockpit's own" : null, canUse: () => Available && !SeatFit.AtDefault),
                Arrow("Seat right", "Moves your view right in the seat.", Vector3.Right, () => Along(SeatFit.Offset.X, Default.X, "right", "left"), 0f),

                new QuickActionsScreen.QuickAction("Reset sticks", "Puts the sticks back where this cockpit has them.", Hud + "ReloadGame.png",
                    ResetSticks, canUse: () => Sticks, closesPanel: true),
                Arrow("Seat back", "Moves your view back in the seat.", Vector3.Backward, () => Along(SeatFit.Offset.Z, Default.Z, "back", "forward"), MathHelper.PiOver2),
                Arrow("Seat down", "Moves your view down in the seat.", Vector3.Down, () => Along(-SeatFit.Offset.Y, -Default.Y, "down", "up"), 0f, Hud + "ValueDecrease.png"),
            };
        }

        private static Vector3 Default => SeatFit.Seat == null ? Vector3.Zero : SeatFit.Default(SeatFit.Seat.BlockDefinition.Id.SubtypeName);

        private static QuickActionsScreen.QuickAction Arrow(string label, string tooltip, Vector3 direction, Func<string> where, float rotation, string icon = null) =>
            new QuickActionsScreen.QuickAction(label, tooltip, icon ?? Chevron, () => Step(label, direction), stateText: where,
                canUse: () => Available && SeatFit.CanNudge(direction, StepMetres), whiteIcon: icon == null, iconRotation: icon == null ? rotation : 0f);

        private static void Step(string label, Vector3 direction)
        {
            if (!SeatFit.Nudge(direction, StepMetres))
                return;
            Vector3 o = SeatFit.Offset;
            Log.Info($"ColbyCockpit: quick menu {label.ToLowerInvariant()}, seat fit now ({o.X:F2}, {o.Y:F2}, {o.Z:F2}) m");
        }

        /// <summary>
        /// How far the view is from the cockpit's own along one axis, in centimetres, by the way this arrow goes or the other.
        /// <paramref name="value"/> and <paramref name="home"/> are signed so that more is this arrow's way.
        /// </summary>
        private static string Along(float value, float home, string way, string other)
        {
            int cm = (int)Math.Round((value - home) * 100f);
            return cm == 0 ? "Centred" : cm > 0 ? $"{cm} cm {way}" : $"{-cm} cm {other}";
        }

        /// <summary>
        /// The padlock and the stick reset wait for the panel to close: the sticks take no change while a menu is up
        /// (CockpitControls.CanAdjust), and unlocked they are moved by gripping them, which needs the hands back.
        /// </summary>
        private static void ToggleSticks()
        {
            CockpitControls.Request = CockpitControls.Adjusting ? CockpitControls.PlacementRequest.Lock : CockpitControls.PlacementRequest.Unlock;
            Log.Info($"ColbyCockpit: quick menu asked to {(CockpitControls.Request == CockpitControls.PlacementRequest.Lock ? "lock" : "unlock")} the sticks");
        }

        /// <summary>The game's toolbar screen on this cockpit's switches, from the first (CockpitActions.Configure), once the panel is down.</summary>
        private static void AssignSwitches()
        {
            Log.Info("ColbyCockpit: quick menu opens the switch assignment");
            CockpitActions.Configure(0);
        }

        private static void ResetSticks()
        {
            CockpitControls.Request = CockpitControls.PlacementRequest.Reset;
            Log.Info("ColbyCockpit: quick menu asked to put the sticks back");
        }
    }
}
