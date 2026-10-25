using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// The bindings a controller starts with, per OpenXR interaction profile (the path without <c>/interaction_profiles/</c>,
    /// as <see cref="ControllerBindings"/> keys them). They are the controls as they have always been: one table for the Quest's
    /// controller (<see cref="Quest"/>), and for each other controller the same roles put on the controls it has
    /// (<see cref="For"/>), as <see cref="OpenXR.XrInput"/>'s layouts have always put them. The controls named are the ones
    /// <see cref="HandState.ControlsDown"/> reports. Right-handed: the left-handed setting swaps the hands where it always has.
    /// </summary>
    internal static class DefaultBindings
    {
        public const string QuestProfile = "oculus/touch_controller";
        public const string Pico4Profile = "bytedance/pico4_controller";
        public const string PicoNeo3Profile = "bytedance/pico_neo3_controller";
        public const string PicoUltraProfile = "bytedance/pico_ultra_controller_bd";
        public const string ReverbProfile = "hp/mixed_reality_controller";
        public const string CosmosProfile = "htc/vive_cosmos_controller";
        public const string Focus3Profile = "htc/vive_focus3_controller";
        public const string IndexProfile = "valve/index_controller";
        public const string SteamFrameProfile = "valve/frame_controller_valve";
        public const string WandProfile = "htc/vive_controller";
        public const string MotionControllerProfile = "microsoft/motion_controller";
        public const string SimpleProfile = "khr/simple_controller";

        /// <summary>Every profile the plugin offers the runtime, in the order XrInput suggests them.</summary>
        public static readonly string[] Profiles =
        {
            QuestProfile, Pico4Profile, PicoNeo3Profile, PicoUltraProfile, ReverbProfile, CosmosProfile, Focus3Profile,
            IndexProfile, SteamFrameProfile, WandProfile, MotionControllerProfile, SimpleProfile,
        };

        /// <summary>The left Menu: let go within this long is a tap (the game menu, or back), held this long recentres.</summary>
        public const double MenuHoldSeconds = 0.6;

        /// <summary>Y in a seat: let go within this long is the tools wheel, held this long leaves the seat.</summary>
        public const double UseHoldSeconds = 0.5;

        /// <summary>X: let go within this long is the dampeners, held this long aims a ping.</summary>
        public const double PingHoldSeconds = 0.35;

        /// <summary>X while building: let go within this long is symmetry on or off, held this long the next plane.</summary>
        public const double SymmetryHoldSeconds = 0.5;

        /// <summary>A Vive wand's right Menu: let go within this long is the dampeners (and B's one-off uses), held this long the game menu.</summary>
        public const double WandMenuTapSeconds = 0.5;

        /// <summary>A Vive wand's left Menu on foot and on the jetpack: let go within this long is the wrist panel, held this long the jetpack on or off.</summary>
        public const double WandJetpackHoldSeconds = 0.35;

        private const Hand L = Hand.Left, R = Hand.Right;
        private const ControllerControl Trigger = ControllerControl.Trigger, Grip = ControllerControl.Grip,
            Click = ControllerControl.ThumbstickClick, Lower = ControllerControl.ButtonLower, Upper = ControllerControl.ButtonUpper,
            Menu = ControllerControl.Menu;

        /// <summary>The Quest's controller: X and Y the left's lower and upper buttons, A and B the right's, Menu on the left.</summary>
        public static List<Binding> Quest() => new List<Binding>
        {
            // On foot and jetpack.
            B(VRAction.Jump, R, Lower, PressKind.Press),
            B(VRAction.JetpackUp, R, Lower, PressKind.Press),
            B(VRAction.ToggleJetpack, R, Upper, PressKind.Press),
            B(VRAction.Dampeners, L, Lower, PressKind.Tap, PingHoldSeconds),
            B(VRAction.Ping, L, Lower, PressKind.Hold, PingHoldSeconds),
            B(VRAction.MarkGps, R, Trigger, PressKind.Press, modifier: Ref(L, Lower)),
            B(VRAction.MarkGps, L, Trigger, PressKind.Press, modifier: Ref(L, Lower)),
            B(VRAction.Sprint, L, Click, PressKind.Press),
            B(VRAction.ZeroGTurn, L, Click, PressKind.Press),
            B(VRAction.Use, R, Grip, PressKind.Press),
            B(VRAction.PrimaryAction, R, Trigger, PressKind.Press),
            B(VRAction.SecondaryAction, L, Trigger, PressKind.Press),
            B(VRAction.Reload, R, Click, PressKind.Press),
            B(VRAction.ToolsWheel, L, Grip, PressKind.Press),
            B(VRAction.WristPanel, L, Upper, PressKind.Press),

            // At a container.
            B(VRAction.QuickTake, L, Click, PressKind.Press),
            B(VRAction.QuickProduction, R, Click, PressKind.Press),
            B(VRAction.QuickDepositAll, R, Click, PressKind.Press, modifier: Ref(R, Grip)),

            // Building.
            B(VRAction.PlaceBlock, R, Trigger, PressKind.Press),
            B(VRAction.RemoveBlock, L, Trigger, PressKind.Press),
            B(VRAction.BuildModifier, L, Click, PressKind.Press),
            B(VRAction.CubeSize, R, Click, PressKind.Press),
            B(VRAction.NextStage, R, Lower, PressKind.Press, modifier: Ref(L, Click)),
            B(VRAction.PreviousStage, R, Upper, PressKind.Press, modifier: Ref(L, Click)),
            B(VRAction.Symmetry, L, Lower, PressKind.Tap, SymmetryHoldSeconds),
            B(VRAction.SymmetryPlane, L, Lower, PressKind.Hold, SymmetryHoldSeconds),

            // Colour tool.
            B(VRAction.Paint, R, Trigger, PressKind.Press),
            B(VRAction.CopyColour, L, Trigger, PressKind.Press),
            B(VRAction.MediumBrush, R, Lower, PressKind.Press),
            B(VRAction.LargeBrush, R, Upper, PressKind.Press),

            // Wheels and the ping aimed from one.
            B(VRAction.WheelActivate, L, Trigger, PressKind.Press),
            B(VRAction.WheelActivate, R, Trigger, PressKind.Press),
            B(VRAction.CloseToolsWheel, R, Upper, PressKind.Press),
            B(VRAction.CloseToolsWheel, L, Upper, PressKind.Tap, UseHoldSeconds),
            B(VRAction.OpenActionsWheel, L, Click, PressKind.Press),
            B(VRAction.CloseActionsWheel, R, Upper, PressKind.Press),
            B(VRAction.CloseActionsWheel, L, Upper, PressKind.Press),
            B(VRAction.CloseActionsWheel, L, Menu, PressKind.Tap, MenuHoldSeconds),
            B(VRAction.PingCommit, R, Trigger, PressKind.Press),
            B(VRAction.PingCancel, R, Upper, PressKind.Press),

            // Seats.
            B(VRAction.LeaveSeat, L, Upper, PressKind.Hold, UseHoldSeconds),
            B(VRAction.SeatToolsWheel, L, Upper, PressKind.Tap, UseHoldSeconds),
            B(VRAction.ThrustUp, R, Lower, PressKind.Press),
            B(VRAction.ThrustDown, R, Upper, PressKind.Press),
            B(VRAction.ShipFire, R, Trigger, PressKind.Press),
            B(VRAction.ShipSecondary, L, Trigger, PressKind.Press),
            B(VRAction.ShipLights, L, Click, PressKind.Press),
            B(VRAction.LandingGear, R, Click, PressKind.Press),

            // Turrets.
            B(VRAction.TurretFire, R, Trigger, PressKind.Press),
            B(VRAction.TurretZoomIn, R, Lower, PressKind.Press),
            B(VRAction.TurretZoomOut, R, Upper, PressKind.Press),

            // Spectator camera.
            B(VRAction.SpectatorUp, R, Lower, PressKind.Press),
            B(VRAction.SpectatorDown, R, Upper, PressKind.Press),
            B(VRAction.SpectatorBoost, L, Click, PressKind.Press),

            // Menus.
            B(VRAction.MenuClick, R, Trigger, PressKind.Press),
            B(VRAction.MenuRightClick, R, Grip, PressKind.Press),
            B(VRAction.MenuMoveItem, R, Lower, PressKind.Press),
            B(VRAction.MenuBack, R, Upper, PressKind.Press),
            B(VRAction.MenuBack, L, Menu, PressKind.Tap, MenuHoldSeconds),
            B(VRAction.AmountTimes10, L, Grip, PressKind.Press),
            B(VRAction.AmountTimes100, L, Trigger, PressKind.Press),

            // Anywhere.
            B(VRAction.GameMenu, L, Menu, PressKind.Tap, MenuHoldSeconds),
            B(VRAction.Recentre, L, Menu, PressKind.Hold, MenuHoldSeconds),
        };

        /// <summary>The bindings <paramref name="profile"/> starts with; a profile not known here gets the Quest's.</summary>
        public static List<Binding> For(string profile)
        {
            switch (profile)
            {
                case WandProfile:
                    return Wand();
                default:
                    // PICO Neo3, Reverb G2, WMR and the simple controller: the right Menu does all the left Menu does.
                    return Compose(RoleOf(profile), rightMenuToo: profile == PicoNeo3Profile || profile == ReverbProfile
                        || profile == MotionControllerProfile || profile == SimpleProfile);
            }
        }

        /// <summary>
        /// Where <paramref name="profile"/> has each Quest control: the control it plays that part with, or null where it has
        /// none. Every default is the Quest's table put through this (a wand's also adds its trackpad's edges).
        /// </summary>
        public static Func<ControlRef, ControlRef?> RoleOf(string profile)
        {
            switch (profile)
            {
                case IndexProfile:
                    // No Menu button the game can take: the left trackpad pressed firmly is Menu.
                    return c => c.Hand == L && c.Control == Menu ? Ref(L, ControllerControl.TrackpadForce) : c;
                case SteamFrameProfile:
                    // The left D-pad's down and up are X and Y, its View button is Menu.
                    return SteamFrameRole;
                case MotionControllerProfile:
                    // The trackpad clicked is the face buttons, upper half over lower; a Menu button on each controller.
                    return MotionControllerRole;
                case SimpleProfile:
                    // One select button (the trigger) and a Menu button on each hand.
                    return c => c.Control == Trigger || c.Control == Menu ? c : (ControlRef?)null;
                case WandProfile:
                    return WandRole;
                default:
                    return QuestRole;
            }
        }

        private static ControlRef? QuestRole(ControlRef c) => c;

        private static ControlRef? SteamFrameRole(ControlRef c)
        {
            if (c.Hand != L)
                return c;
            switch (c.Control)
            {
                case Lower: return Ref(L, ControllerControl.DpadDown);
                case Upper: return Ref(L, ControllerControl.DpadUp);
                case Menu: return Ref(L, ControllerControl.View);
                default: return c;
            }
        }

        private static ControlRef? MotionControllerRole(ControlRef c)
        {
            switch (c.Control)
            {
                case Lower: return Ref(c.Hand, ControllerControl.TrackpadDown);
                case Upper: return Ref(c.Hand, ControllerControl.TrackpadUp);
                default: return c;
            }
        }

        /// <summary>
        /// The Quest's table with each control put where <paramref name="role"/> says (null: the controller has none, and the
        /// binding is left out). <paramref name="rightMenuToo"/>: the right Menu does all the left Menu does.
        /// </summary>
        private static List<Binding> Compose(Func<ControlRef, ControlRef?> role, bool rightMenuToo)
        {
            var list = new List<Binding>();
            foreach (Binding b in Quest())
            {
                ControlRef? control = role(b.Control);
                ControlRef? modifier = b.Modifier.HasValue ? role(b.Modifier.Value) : null;
                if (!control.HasValue || b.Modifier.HasValue && !modifier.HasValue)
                    continue;
                list.Add(new Binding(b.Action, control.Value, b.Kind, modifier, b.HoldSeconds));
                if (rightMenuToo && b.Control.Hand == L && b.Control.Control == Menu)
                    list.Add(new Binding(b.Action, Ref(R, Menu), b.Kind, modifier, b.HoldSeconds));
            }
            return list;
        }

        /// <summary>
        /// The Vive wands (<see cref="OpenXR.WandLayout"/>): the left trackpad clicked is the left stick click and the left Menu
        /// is Y; the right trackpad clicked in the middle is the right stick click (and in a menu moves an item, A's job there),
        /// with the left trackpad held the next block stage; at its top and bottom edges A and B's held uses: jump and crouch,
        /// the jetpack's up and down, a seat's thrust, a turret's zoom, the spectator's climb and descent, the medium and large
        /// brush. X's jobs go to what is left:
        /// <list type="bullet">
        /// <item>The right Menu tapped is the dampeners (X tapped) and B's other one-off uses, held the game menu.</item>
        /// <item>The right Menu held with the right trigger aims a ping, let go of the trigger it pings; the left trigger pulled
        /// as well marks a GPS instead (X held, and the trigger with it).</item>
        /// <item>The left Menu tapped is the wrist panel, held the jetpack on or off (B); in a seat it stays Y.</item>
        /// <item>Building, with the left trackpad held (working the block), the left trigger tapped is symmetry on or off, held the
        /// next symmetry plane (X tapped and held).</item>
        /// </list>
        /// Recentring is the runtime's.
        /// </summary>
        private static List<Binding> Wand()
        {
            ControlRef up = Ref(R, ControllerControl.TrackpadUp), down = Ref(R, ControllerControl.TrackpadDown),
                leftPad = Ref(L, ControllerControl.Trackpad), rightMenu = Ref(R, Menu);
            var pad = new List<Binding>
            {
                new Binding((int)VRAction.Jump, up, PressKind.Press),
                new Binding((int)VRAction.JetpackUp, up, PressKind.Press),
                new Binding((int)VRAction.ThrustUp, up, PressKind.Press),
                new Binding((int)VRAction.TurretZoomIn, up, PressKind.Press),
                new Binding((int)VRAction.SpectatorUp, up, PressKind.Press),
                new Binding((int)VRAction.MediumBrush, up, PressKind.Press),
                new Binding((int)VRAction.Crouch, down, PressKind.Press),
                new Binding((int)VRAction.JetpackDown, down, PressKind.Press),
                new Binding((int)VRAction.ThrustDown, down, PressKind.Press),
                new Binding((int)VRAction.TurretZoomOut, down, PressKind.Press),
                new Binding((int)VRAction.SpectatorDown, down, PressKind.Press),
                new Binding((int)VRAction.LargeBrush, down, PressKind.Press),
                new Binding((int)VRAction.MenuMoveItem, Ref(R, ControllerControl.TrackpadCentre), PressKind.Press),
                // With the left trackpad held the pad's edges move and turn the block (BuildMode), so the next stage is its middle.
                new Binding((int)VRAction.NextStage, Ref(R, ControllerControl.TrackpadCentre), PressKind.Press, leftPad),
            };
            // X's jobs. The dampeners are the one tap free on foot, on the jetpack and in a seat alike, so B's jetpack moves to the
            // left Menu held.
            var x = new List<Binding>
            {
                new Binding((int)VRAction.Dampeners, rightMenu, PressKind.Tap, null, WandMenuTapSeconds),
                new Binding((int)VRAction.Ping, Ref(R, Trigger), PressKind.Press, rightMenu),
                new Binding((int)VRAction.MarkGps, Ref(L, Trigger), PressKind.Press, rightMenu),
                new Binding((int)VRAction.ToggleJetpack, Ref(L, Menu), PressKind.Hold, null, WandJetpackHoldSeconds),
                new Binding((int)VRAction.Symmetry, Ref(L, Trigger), PressKind.Tap, leftPad, SymmetryHoldSeconds),
                new Binding((int)VRAction.SymmetryPlane, Ref(L, Trigger), PressKind.Hold, leftPad, SymmetryHoldSeconds),
            };
            var list = new List<Binding>();
            foreach (Binding b in Quest())
            {
                // B's held uses are the pad's bottom edge, not a tap of the right Menu that lasts one frame; the jetpack is the left Menu's.
                if (b.Control.Hand == R && b.Control.Control == Upper && (pad.Exists(p => p.Action == b.Action) || x.Exists(p => p.Action == b.Action)))
                    continue;
                ControlRef? modifier = b.Modifier.HasValue ? WandRole(b.Modifier.Value) : null;
                if (b.Modifier.HasValue && !modifier.HasValue)
                    continue;
                if (b.Control.Hand == L && b.Control.Control == Menu)
                {
                    // The game menu (and back, and closing the actions wheel) is the right Menu held; recentring is the runtime's.
                    if (b.Kind == PressKind.Tap)
                        list.Add(new Binding(b.Action, rightMenu, PressKind.Hold, modifier, WandMenuTapSeconds));
                    continue;
                }
                ControlRef? control = WandRole(b.Control);
                if (!control.HasValue)
                    continue;
                if (b.Control.Hand == R && b.Control.Control == Upper)
                    list.Add(new Binding(b.Action, control.Value, PressKind.Tap, modifier, WandMenuTapSeconds));
                else if (b.Action == (int)VRAction.WristPanel)
                    // Tapped, as the left Menu held is the jetpack.
                    list.Add(new Binding(b.Action, control.Value, PressKind.Tap, modifier, WandJetpackHoldSeconds));
                else
                    list.Add(new Binding(b.Action, control.Value, b.Kind, modifier, b.HoldSeconds));
            }
            list.AddRange(pad);
            list.AddRange(x);
            return list;
        }

        // A wand: the left trackpad clicked is the left stick click, the left Menu Y, the right trackpad's middle the right stick
        // click and the right Menu B; no X or A (Wand gives X's jobs their own), and the left Menu's uses go to the right Menu.
        private static ControlRef? WandRole(ControlRef c)
        {
            if (c.Hand == L)
            {
                switch (c.Control)
                {
                    case Click: return Ref(L, ControllerControl.Trackpad);
                    case Upper: return Ref(L, Menu);
                    case Lower:
                    case Menu: return null;
                    default: return c;
                }
            }
            switch (c.Control)
            {
                case Click: return Ref(R, ControllerControl.TrackpadCentre);
                case Lower: return null;
                case Upper: return Ref(R, Menu);
                default: return c;
            }
        }

        private static ControlRef Ref(Hand hand, ControllerControl control) => new ControlRef(hand, control);

        private static Binding B(VRAction action, Hand hand, ControllerControl control, PressKind kind, double hold = BindingEngine.DefaultHoldSeconds, ControlRef? modifier = null) =>
            new Binding((int)action, Ref(hand, control), kind, modifier, hold);
    }
}
