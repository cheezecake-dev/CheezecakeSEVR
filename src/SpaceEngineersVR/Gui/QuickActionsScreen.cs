using System;
using System.Text;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The quick-actions screen: the common on-foot actions as big buttons, shown on the wrist panel
    /// (<see cref="Rendering.WorldMenu"/> puts the GUI panel on the left wrist while this screen has the focus). The screen
    /// is the whole 16:9 GUI, 1920x1080 pixels onto about 0.30 m of wrist, so it is made of a few large buttons with large
    /// text, and has a solid background the size of the whole GUI. The HUD is part of the same sprite pass (its screens
    /// draw even without the focus, MyGuiScreenHudBase, and sit lower in the list), so they are drawn first and the
    /// background covers them: the HUD does not shrink onto the wrist beside the buttons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A button does not do its action itself. The actions are the game's own controls, which the gameplay screen reads
    /// (MyGuiScreenGamePlay.HandleUnhandledInput, MyCubeBuilder.HandleGameInput) and only acts on while it has the focus,
    /// so a button closes this screen and presses that control for the game's next gameplay input (<see cref="WristButton.Press"/>).
    /// What the game then does is exactly what the key does: the same checks, sounds, synchronisation and screens.
    /// </para>
    /// <para>
    /// The look is the game's own menus': its screen background (MyGuiConstants.TEXTURE_SCREEN_BACKGROUND), its caption,
    /// its separator lines, and its rectangular button in its four states (MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_*), with
    /// the icons the game's own radial menu gives these actions (Content\Data\RadialMenu.sbc, HUD 2017). At 6400 GUI pixels
    /// to the metre the game's 1-pixel borders would be half a headset pixel, so the button frames are drawn
    /// <see cref="BorderScale"/> times over, and the pointed-at button takes the game's selected look (its light blue fill,
    /// dark text), which reads from arm's length, where the game's mouse-over look is only a shade lighter.
    /// RenderDebug QuickMenuNative=0 gives back the plain panel (<see cref="Plain"/>).
    /// </para>
    /// <para>
    /// Under the actions is the External view tile: it opens a page of its own on the same panel (<see cref="ExternalViewPage"/>)
    /// whose buttons act at once and leave the panel up, so each step is seen on the external view screen as it is pressed;
    /// Back returns to the actions. In a seat, where Y does not open the panel, the actions wheel's External view page opens
    /// it straight onto that page (<see cref="OpenExternalViewPage"/>).
    /// </para>
    /// <para>
    /// In a seat the actions are the seat's instead (<see cref="ShipActions"/>, <see cref="PilotActions"/>): the ship's switches
    /// the game gives a seated player and VR has no button for, each the game's own control as its key presses it, with the
    /// grid's state read as the HUD reads it, greyed where the game would refuse it from this seat (a passenger seat, a cockpit
    /// that is not the main one). Y does not reach the panel there, so the actions wheel has a "Quick actions" entry for it (<see cref="AddTo"/>).
    /// In a cockpit ColbyJack1134's rig owns, the pilot's group also has a Seat tile: his seat panel's keys on a page of their
    /// own (<see cref="Cockpits.SeatPage"/>), which, like the External view page, act at once and leave the panel up.
    /// </para>
    /// </remarks>
    internal sealed partial class QuickActionsScreen : MyGuiScreenBase
    {
        /// <summary>One button: what it says, what it does (the game's control), its icon, and its switch state if it is one.</summary>
        internal sealed class QuickAction
        {
            public QuickAction(string label, MyStringId control, string defaultKey, string tooltip, string icon,
                               Func<bool?> state = null, string iconOff = null, bool whiteIcon = false, float iconScale = 1f, float iconAspect = 1f,
                               Func<bool> canUse = null, Func<string> stateText = null)
            {
                Label = label;
                Control = control;
                DefaultKey = defaultKey;
                Tooltip = tooltip;
                Icon = icon;
                IconOff = iconOff ?? icon;
                WhiteIcon = whiteIcon;
                State = state;
                IconScale = iconScale;
                IconAspect = iconAspect;
                CanUse = canUse;
                StateText = stateText;
            }

            /// <summary>
            /// The panel's own button: it runs <paramref name="run"/> and the panel stays up (no game control is pressed), with
            /// <paramref name="stateText"/> under its name, greyed while <paramref name="canUse"/> says no.
            /// </summary>
            public QuickAction(string label, string tooltip, string icon, Action run, Func<string> stateText = null,
                               Func<bool?> state = null, Func<bool> canUse = null, bool whiteIcon = false, float iconRotation = 0f,
                               bool closesPanel = false, string iconOff = null)
            {
                Label = label;
                Tooltip = tooltip;
                Icon = icon;
                IconOff = iconOff ?? icon;
                Run = run;
                StateText = stateText;
                State = state;
                CanUse = canUse;
                WhiteIcon = whiteIcon;
                IconRotation = iconRotation;
                IconScale = IconAspect = 1f;
                ClosesPanel = closesPanel;
            }

            /// <summary>The panel's own button's action; null for a game control.</summary>
            public Action Run { get; }

            /// <summary>The panel's own button closes the panel once it has run (an action of the game's that has no control, such as the handbrake).</summary>
            public bool ClosesPanel { get; }

            /// <summary>
            /// Words under the name (a value, such as "30 cm", or a switch's own, such as "Some on"); null for none. While they
            /// give null, a switch shows On or Off.
            /// </summary>
            public Func<string> StateText { get; }

            /// <summary>The button can be pressed now; null for always.</summary>
            public Func<bool> CanUse { get; }

            /// <summary>The icon is turned this far, radians clockwise (one chevron serves four ways).</summary>
            public float IconRotation { get; }

            /// <summary>The icon's size against the others' (a picture that fills its square is drawn smaller to match), and its width over its height.</summary>
            public float IconScale { get; }

            public float IconAspect { get; }

            public string Label { get; }

            /// <summary>The game control the button presses.</summary>
            public MyStringId Control { get; }

            /// <summary>The key the game has for it by default (MySandboxGame.cs AddDefaultGameControl), for the log and the tooltip.</summary>
            public string DefaultKey { get; }

            /// <summary>What it does, shown along the panel's foot while the button is pointed at.</summary>
            public string Tooltip { get; }

            /// <summary>The icon, and the one shown while the switch is off (the game's radial menu has such pairs).</summary>
            public string Icon { get; }

            public string IconOff { get; }

            /// <summary>The icon is a white glyph (the G menu's own), drawn in the HUD icons' light blue so the set matches.</summary>
            public bool WhiteIcon { get; }

            /// <summary>For a switch, whether it is on now (null when that cannot be told); null for a button that opens a screen.</summary>
            public Func<bool?> State { get; }
        }

        private const string Hud = GuiTextures.Hud;

        /// <summary>The game has no terminal icon; this is the G menu's two-windows glyph.</summary>
        private const string TerminalIcon = "Textures\\GUI\\Icons\\buttons\\hud.dds";

        private static IMyControllableEntity Controlled => MySession.Static?.ControlledEntity;

        /// <summary>The quick heal's item (MyGuiScreenGamePlay.ConsumeHealthItem, :1341).</summary>
        private static readonly MyDefinitionId MedkitId = new MyDefinitionId(typeof(MyObjectBuilder_ConsumableItem), "Medkit");

        /// <summary>The medkits in the player's own inventory, where the quick heal takes one from.</summary>
        private static int Medkits => (int)(MySession.Static?.LocalCharacter?.GetInventory()?.GetItemAmount(MedkitId) ?? 0);

        private static string MedkitsText() => Medkits == 1 ? "1 medkit" : $"{Medkits} medkits";

        /// <summary>What relative dampening is matching the player's speed to, by its name; null while it is not on.</summary>
        private static string RelativeToOnFoot()
        {
            IMyControllableEntity controlled = Controlled;
            if (controlled == null || !controlled.EnabledDamping || controlled.RelativeDampeningEntity == null)
                return null;
            string name = controlled.RelativeDampeningEntity.DisplayNameText;
            return string.IsNullOrWhiteSpace(name) ? "On" : name;
        }

        /// <summary>
        /// The buttons, left to right and top to bottom. Each is a control that MyGuiScreenGamePlay.HandleUnhandledInput
        /// (or MyCubeBuilder.HandleGameInput) asks the input about by name; the decompiled lines are given with each.
        /// </summary>
        internal static readonly QuickAction[] Actions =
        {
            // MyGuiScreenGamePlay.cs:833 INVENTORY -> controlledEntity.ShowInventory()
            new QuickAction("Inventory", MyControlsSpace.INVENTORY, "I", "Opens your inventory.", Hud + "OpenInventory.png"),

            // MyGuiScreenGamePlay.cs:823 TERMINAL -> controlledEntity.ShowTerminal(). The game has no terminal icon; this is
            // the G menu's two-windows glyph.
            new QuickAction("Terminal", MyControlsSpace.TERMINAL, "K", "Opens the control panel (terminal).",
                TerminalIcon, whiteIcon: true),

            // MyGuiScreenGamePlay.cs:910 BUILD_SCREEN -> the toolbar configuration screen (the G menu)
            new QuickAction("G menu", MyControlsSpace.BUILD_SCREEN, "G", "Opens the G menu: blocks, items and the toolbar.", Hud + "MultiBlockBuilding.png"),

            // MyGuiScreenGamePlay.cs:725 HEADLIGHTS -> controlledEntity.SwitchLights()
            // The game's own pair has the lamp without its rays for Off, which on the wrist read as a bare bar: the lamp with its
            // rays (the HUD's flashlight) both ways, dimmed when off as every switch here is.
            new QuickAction("Lights", MyControlsSpace.HEADLIGHTS, "L", "Switches your lights on or off.", Hud + "LightCenter.png",
                () => Controlled?.EnabledLights, iconScale: 1.15f),

            // MyGuiScreenGamePlay.cs:667 HELMET -> controlledEntity.SwitchHelmet()
            new QuickAction("Helmet", MyControlsSpace.HELMET, "J", "Puts your helmet on or takes it off.", Hud + "PlayerHelmetOn.png",
                () => Controlled?.EnabledHelmet, Hud + "PlayerHelmetOff.png"),

            // MyGuiScreenGamePlay.cs:716 CONSUME_HEALTH -> ConsumeHealthItem (:1339): one medkit from your inventory, the game's
            // quick heal (a pad's; no key by default). The HUD's health cross. Greyed with no medkit; under the name, how many.
            new QuickAction("Heal", MyControlsSpace.CONSUME_HEALTH, "no default key", "Uses a medkit from your inventory to heal.",
                Hud + "HealthIcon.png", whiteIcon: true, iconScale: 0.8f, canUse: () => Medkits > 0, stateText: MedkitsText),

            // MyGuiScreenGamePlay.cs:688 DAMPING -> controlledEntity.SwitchDamping()
            // The game's radial menu and HUD have the warning sign for it, as the seated panel does; the rocket over a speed
            // gauge is the HUD's picture for relative dampening, the next button.
            new QuickAction("Dampeners", MyControlsSpace.DAMPING, "Z", "Switches the inertia dampeners on or off.", Hud + "Dampeners.png",
                () => Controlled?.EnabledDamping),

            // MyGuiScreenGamePlay.cs:679 DAMPING_RELATIVE -> dampeners on, and MyPlayerCollection.SetDampeningEntity: the speed is
            // matched to what the head's ray is on, up to 1 km (MyPlayerCollection.cs:2194). As the button is pressed the head is
            // turned to the wrist, so the game's ray is given the head as it was when the panel opened (WristButton.OpenedLook).
            // Under the name is what it is matched to. As the seated panel's.
            new QuickAction("Relative", MyControlsSpace.DAMPING_RELATIVE, "Ctrl+Z",
                "Relative dampeners: dampeners on, matching your speed to what you were looking at as you opened this panel (up to 1 km).",
                Hud + "DampenersAuto.png", () => Controlled?.EnabledDamping == true && Controlled?.RelativeDampeningEntity != null,
                whiteIcon: true, iconScale: 0.64f, iconAspect: 119f / 105f, stateText: RelativeToOnFoot),

            // MyGuiScreenGamePlay.cs:662 BROADCASTING -> controlledEntity.SwitchBroadcasting()
            new QuickAction("Broadcast", MyControlsSpace.BROADCASTING, "O", "Switches your radio broadcast on or off.", Hud + "ToggleBroadcasting.png",
                () => Controlled?.EnabledBroadcasting),

            // MyCubeBuilder.cs:2905 USE_SYMMETRY -> ToggleSymmetry() (with a block in hand, looking at a grid)
            new QuickAction("Symmetry", MyControlsSpace.USE_SYMMETRY, "N", "Switches symmetry on or off while you are building.", Hud + "ToggleSymmetry.png",
                () => MyCubeBuilder.Static?.UseSymmetry),

            // MyCubeBuilder.cs:2279 COLOR_PICKER -> the colour picker screen (MyGuiScreenColorPicker)
            new QuickAction("Colour", MyControlsSpace.COLOR_PICKER, "P", "Opens the colour picker.", Hud + "ColorPicker.png"),
        };

        /// <summary>The tile under the actions that opens the External view page.</summary>
        private static readonly QuickAction ExternalViewTile = new QuickAction("External view",
            "The external view screen: on or off, its camera round you or your ship, and its size.", ExternalViewPage.PageIcon,
            null, state: () => ExternalView.On, canUse: () => ExternalView.Usable);

        // ---- In a seat ----

        /// <summary>The seat the player is in: a cockpit, a passenger seat, a cryo chamber or a remote control; null on foot and in a turret.</summary>
        private static MyShipController Seat => MySession.Static?.ControlledEntity as MyShipController;

        /// <summary>The seat may work the ship's switches: MyShipController.m_enableShipControl, which a passenger seat has off.</summary>
        private static bool ShipControl => Seat?.EnableShipControl == true;

        /// <summary>The power switch's own rule (MyShipController.SwitchReactorsLocal): not from a cockpit while another is the main one.</summary>
        private static bool PowerAllowed
        {
            get
            {
                MyShipController seat = Seat;
                return seat != null && seat.EnableShipControl && (seat.CubeGrid.MainCockpit == null || seat.IsMainCockpit);
            }
        }

        /// <summary>Park's and the handbrake's own rule (MyShipController.SwitchParkedStatus, SwitchHandbrake).</summary>
        private static bool ParkAllowed
        {
            get
            {
                MyShipController seat = Seat;
                return seat != null && seat.EnableShipControl && (seat.IsMainCockpit || !seat.CubeGrid.HasMainCockpit());
            }
        }

        /// <summary>The ship's spotlights as the L key sees them (MyShipController.SwitchLights); null with no seat.</summary>
        private static MyMultipleEnabledEnum? Spotlights => Seat?.GridReflectorLights?.ReflectorsEnabled;

        /// <summary>The ship's antennas as the O key sees them (MyShipController.SwitchBroadcasting); null with no seat.</summary>
        private static MyMultipleEnabledEnum? Antennas => Seat?.CubeGrid?.GridSystems?.RadioSystem?.AntennasBroadcasterEnabled;

        private static bool? Dampeners => Seat?.EntityThrustComponent?.DampenersEnabled;

        /// <summary>A group of blocks' switch as the HUD reads it (MyStatControlledEntityBroadcasting): some on counts as on.</summary>
        private static bool? AnyOn(MyMultipleEnabledEnum? state)
        {
            switch (state)
            {
                case MyMultipleEnabledEnum.AllEnabled:
                case MyMultipleEnabledEnum.Mixed:
                    return true;
                case MyMultipleEnabledEnum.AllDisabled:
                    return false;
                default:
                    return null;
            }
        }

        /// <summary>The words for a group's switch that On and Off do not say: some on, or no such blocks at all.</summary>
        private static string Some(MyMultipleEnabledEnum? state, string none) =>
            state == MyMultipleEnabledEnum.Mixed ? "Some on" : state == MyMultipleEnabledEnum.NoObjects ? none : null;

        /// <summary>
        /// The ship's switches, as the game gives them to a seated player (MyGuiScreenGamePlay.HandleUnhandledInput, with the
        /// game's keys): each presses the game's control, or for the handbrake, which has none, runs the game's radial menu
        /// action. Greyed where the game's own rule refuses it from this seat; the state is read from the grid, as the HUD reads it.
        /// </summary>
        internal static readonly QuickAction[] ShipActions =
        {
            // MyGuiScreenGamePlay.cs:725 HEADLIGHTS -> MyShipController.SwitchLights: the grid's spotlights all on, or all off.
            new QuickAction("Lights", MyControlsSpace.HEADLIGHTS, "L", "Switches the ship's spotlights on or off.", Hud + "LightCenter.png",
                () => AnyOn(Spotlights), iconScale: 1.15f,
                canUse: () => ShipControl && Spotlights.HasValue && Spotlights != MyMultipleEnabledEnum.NoObjects,
                stateText: () => Some(Spotlights, "No spotlights")),

            // MyGuiScreenGamePlay.cs:745 TOGGLE_REACTORS -> SwitchReactorsLocal: this grid and the grids joined to it by rotors,
            // pistons and hinges (the game's "Local power switch"; Ctrl+Y, the logical group's, is on the actions wheel).
            new QuickAction("Power", MyControlsSpace.TOGGLE_REACTORS, "Y",
                "Local power switch: turns the power of this grid, and of the grids on its rotors, pistons and hinges, on or off.",
                Hud + "GridPowerOnCenter.png", () => Seat?.CubeGrid?.IsPowerSwitchOn, canUse: () => PowerAllowed),

            // MyGuiScreenGamePlay.cs:688 DAMPING -> SwitchDamping. The game's radial menu and HUD have the warning sign for it; the
            // rocket over a speed gauge is the HUD's picture for relative dampening, the next button.
            new QuickAction("Dampeners", MyControlsSpace.DAMPING, "Z", "Switches the inertia dampeners on or off.", Hud + "Dampeners.png",
                () => Dampeners, canUse: () => ShipControl && Seat.EntityThrustComponent != null),

            // MyGuiScreenGamePlay.cs:679 DAMPING_RELATIVE -> dampeners on, and MyPlayerCollection.SetDampeningEntity: the speed is
            // matched to whatever the seat looks straight ahead at, up to 1 km. Under the name is what it is matched to.
            new QuickAction("Relative", MyControlsSpace.DAMPING_RELATIVE, "Ctrl+Z",
                "Relative dampeners: dampeners on, matching your speed to what is straight ahead of the ship (up to 1 km).",
                Hud + "DampenersAuto.png", () => Dampeners == true && Seat.RelativeDampeningEntity != null,
                whiteIcon: true, iconScale: 0.64f, iconAspect: 119f / 105f,
                canUse: () => ShipControl && Seat.EntityThrustComponent != null,
                stateText: RelativeTo),

            // MyGuiScreenGamePlay.cs:772 LANDING_GEAR -> SwitchLandingGears -> SwitchParkedStatus: landing gear, connectors and
            // the wheels' brake together (the game's "Park").
            new QuickAction("Park", MyControlsSpace.LANDING_GEAR, "P", "Park: locks the landing gear and connectors and brakes the wheels, or lets them go.",
                Hud + "ToggleConnectors.png", () => Seat?.CubeGrid?.IsParked, canUse: () => ParkAllowed),

            // No control: the game's radial menu action (MyActionToggleHandbrake) calls the seat's SwitchHandbrake.
            new QuickAction("Handbrake", "Puts the wheels' handbrake on or takes it off.", Hud + "HandbrakeCenter.png",
                () => MySession.Static?.ControlledEntity?.SwitchHandbrake(),
                state: () => Seat?.CubeGrid?.GridSystems?.WheelSystem?.HandBrake, canUse: () => ParkAllowed, closesPanel: true),

            // MyGuiScreenGamePlay.cs:662 BROADCASTING -> MyShipController.SwitchBroadcasting: the grid's antennas.
            new QuickAction("Broadcast", MyControlsSpace.BROADCASTING, "O", "Switches the ship's antennas' broadcasting on or off.",
                Hud + "GridBroadcastingOn.png", () => AnyOn(Antennas), Hud + "GridBroadcastingOff.png",
                canUse: () => ShipControl && Antennas.HasValue && Antennas != MyMultipleEnabledEnum.NoObjects,
                stateText: () => Some(Antennas, "No antenna")),
        };

        /// <summary>What relative dampening is matching the ship's speed to, by its name; null while it is not on.</summary>
        private static string RelativeTo()
        {
            MyShipController seat = Seat;
            if (seat == null || Dampeners != true || seat.RelativeDampeningEntity == null)
                return null;
            string name = seat.RelativeDampeningEntity.DisplayNameText;
            return string.IsNullOrWhiteSpace(name) ? "On" : name;
        }

        /// <summary>The pilot's own, from the seat: the seat passes each to the pilot or opens its own screen, as the keys do.</summary>
        internal static readonly QuickAction[] PilotActions =
        {
            // MyGuiScreenGamePlay.cs:667 HELMET -> the seat's SwitchHelmet -> the pilot's helmet.
            // The screens first, as on foot: what a seated player most often reaches for.
            Actions[0],
            Actions[1],
            // MyGuiScreenGamePlay.cs:910 BUILD_SCREEN: the game opens no G menu from a cryo chamber.
            new QuickAction("G menu", MyControlsSpace.BUILD_SCREEN, "G", "Opens the G menu: blocks, items and the toolbar.", Hud + "MultiBlockBuilding.png",
                canUse: () => Seat != null && !(Seat is MyCryoChamber)),
            new QuickAction("Helmet", MyControlsSpace.HELMET, "J", "Puts your helmet on or takes it off.", Hud + "PlayerHelmetOn.png",
                () => Seat?.Pilot?.OxygenComponent?.HelmetEnabled, Hud + "PlayerHelmetOff.png", canUse: () => Seat?.Pilot != null),
        };

        /// <summary>
        /// MyGuiScreenGamePlay.cs:401 CAMERA_MODE: the seat's view or the view from behind the ship. Shown only while third person
        /// is on in the VR options, the only time the plugin lets the game have the key.
        /// </summary>
        private static readonly QuickAction Camera = new QuickAction("Camera", MyControlsSpace.CAMERA_MODE, "V",
            "Switches between the view from the seat and the view from behind the ship.", Hud + "SwitchCamera.png",
            stateText: () => MySession.Static?.CameraController?.IsInFirstPersonView == false ? "Third person" : "First person");

        /// <summary>The seated page's groups in order, their names, and their buttons as they are to be shown now.</summary>
        private static QuickAction[][] SeatedGroups(out string[] names)
        {
            names = new[] { "Pilot", "Ship" };
            var pilot = new System.Collections.Generic.List<QuickAction>(PilotActions);
            if (VRSettings.ThirdPerson)
                pilot.Add(Camera);
            pilot.Add(ExternalViewTile);
            // ColbyJack1134's seat panel, as a page: only in a seat his rig owns.
            if (Cockpits.SeatPage.Available)
                pilot.Add(Cockpits.SeatPage.Tile);
            return new[] { pilot.ToArray(), ShipActions };
        }

        /// <summary>The seated page's buttons across: two rows for the ship's seven, two for the pilot's.</summary>
        internal const int SeatedColumns = 4;

        private const float GroupHeight = 0.05f, GroupScale = 1.15f;

        /// <summary>
        /// Game thread, as the actions wheel opens: in a seat, where Y is the toolbar wheel and leaving the seat, and so cannot
        /// open the panel, the wheel's top ring gets "Quick actions", which opens it, with the seat's own block picture.
        /// </summary>
        internal static void AddTo(System.Collections.Generic.List<ActionsWheel.Entry> top)
        {
            MyShipController seat = Seat;
            if (seat == null)
                return;
            string icon = null;
            try
            {
                string[] icons = seat.BlockDefinition?.Icons;
                if (icons != null && icons.Length > 0 && !string.IsNullOrEmpty(icons[0]))
                    icon = icons[0];
            }
            catch (Exception)
            {
                // The seat's picture is only a nicety: the panel's own glyph does.
            }
            top.Add(new ActionsWheel.Entry
            {
                Name = "Quick actions",
                Icon = icon ?? TerminalIcon,
                Run = () => OpenSeated(),
                StateText = () => "Lights, power, park",
            });
            // The G menu straight from here too, beside the game's own Inventory entry: one stick click and a choice from the
            // seat's tools wheel.
            if (!(seat is MyCryoChamber))
                top.Add(new ActionsWheel.Entry
                {
                    Name = "G menu",
                    Icon = Hud + "MultiBlockBuilding.png",
                    Run = () => WristButton.Press(MyControlsSpace.BUILD_SCREEN, "G menu"),
                });
        }

        /// <summary>Game thread: opens the panel in a seat (the actions wheel's way in, or Y tapped at the wrist), on its seated page.</summary>
        internal static QuickActionsScreen OpenSeated() => OpenSeated("actions wheel");

        internal static QuickActionsScreen OpenSeated(string how)
        {
            var screen = new QuickActionsScreen();
            MyGuiSandbox.AddScreen(screen);
            MyShipController seat = Seat;
            Log.Info($"Wrist panel: opened in a seat ({how}; {seat?.GetType().Name ?? "none"} '{seat?.DisplayNameText}'), {screen.TileCount} tiles");
            return screen;
        }

        public const int Columns = 3;

        private enum Page
        {
            Actions,
            ExternalView,

            /// <summary>ColbyJack1134's seat panel's keys (<see cref="Cockpits.SeatPage"/>), in a seat his rig owns.</summary>
            Seat,

            /// <summary>What each button does, in each situation (<see cref="AddControlsPage"/>).</summary>
            Controls,

            /// <summary>Other plugins' and mods' tiles (<see cref="AddOnTiles"/>), one of the pages <see cref="addOnPages"/> holds.</summary>
            AddOns,
        }

        /// <summary>Debug: the plain panel as it was, a flat background and text-only buttons (<see cref="Rendering.RenderDebug"/> QuickMenuNative=0).</summary>
        public static bool Plain { get; set; }

        /// <summary>The game's button frames are drawn this many times their size: 3 GUI pixels is about 1.5 headset pixels on the wrist.</summary>
        internal const float BorderScale = 3f;

        private const string BlankTexture = GuiTextures.Blank;
        private static readonly Vector4 PanelColor = new Vector4(0.035f, 0.06f, 0.085f, 1f);

        /// <summary>The HUD icons' own light blue (192, 237, 249), for the white glyphs.</summary>
        private static readonly Vector4 IconBlue = new Color(192, 237, 249).ToVector4();

        /// <summary>The thin line round the game's screen background (screen_background.dds), drawn thick enough to see.</summary>
        private static readonly Vector4 FrameColor = new Color(61, 76, 102).ToVector4();

        private static readonly Vector4 OffColor = new Vector4(0.55f, 0.6f, 0.63f, 1f);
        private static readonly Vector4 FootColor = new Vector4(0.75f, 0.8f, 0.83f, 1f);

        // Measures are the GUI's normalised ones: 1.0 is the screen's height, and the 1920x1080 layout is 4/3 wide.
        private const float MarginX = 0.045f, TitleHeight = 0.15f, FootHeight = 0.105f, Gap = 0.022f, Line = 0.003f;
        private const float CaptionScale = 1.45f, LabelScale = 1.65f, StateScale = 1.3f, FootScale = 1.25f;

        // The plain panel's.
        private const float PlainMarginX = 0.05f, PlainBottomMargin = 0.05f, PlainTitleHeight = 0.16f, PlainGap = 0.03f;
        private const float PlainTitleScale = 1.6f, PlainButtonTextScale = 1.9f;

        /// <summary>The game's rectangular button, drawn for the wrist panel's tiles and the flight settings' buttons (<see cref="Cockpits.FlightSettingsScreen"/>).</summary>
        internal static readonly MyGuiControlButton.StyleDefinition TileStyle = new MyGuiControlButton.StyleDefinition
        {
            NormalTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_BORDER,
            HighlightTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_FOCUS_BORDER,
            // The screen gives its first button focus as it opens; drawn as the game draws focus, that read as a second
            // pointed-at tile beside the one the laser is on. The laser is the only way to choose here, so focus looks plain.
            FocusTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_BORDER,
            ActiveTexture = MyGuiConstants.TEXTURE_RECTANGLE_BUTTON_ACTIVE_BORDER,
            NormalFont = "Blue",
            HighlightFont = "White",
        };

        private static bool preloaded;

        private MyGuiControlLabel foot;
        private bool plain;

        /// <summary>How many tiles the panel shows now (the External view page's readout in the middle not counted).</summary>
        internal int TileCount { get; private set; }
        private Page page;
        private Page? switchTo;
        private string hint = Hint;

        /// <summary>The actions page is laid out for a seat (<see cref="ShipActions"/>, <see cref="PilotActions"/>).</summary>
        private bool seatedLayout;

        /// <summary>Where the head looked as the panel opened, for the Relative tile; null in a seat.</summary>
        private readonly WristButton.OpenedLook look;

        public QuickActionsScreen()
            : this(false)
        {
        }

        /// <param name="externalView">Open on the External view page (from the actions wheel in a seat).</param>
        public QuickActionsScreen(bool externalView)
            : base(new Vector2(0.5f, 0.5f), Plain ? PanelColor : Vector4.One, FullSize(), false,
                   Plain ? BlankTexture : MyGuiConstants.TEXTURE_SCREEN_BACKGROUND.Texture)
        {
            plain = Plain;
            page = externalView && !plain ? Page.ExternalView : Page.Actions;
            // Before anything else: the head is still on what the player looked at as they opened the panel.
            look = WristButton.CaptureLook();
            // No fade in or out: the panel is a wrist watch, and it must be gone the moment a button is pressed, so the
            // gameplay screen has the focus (and presses the control) at once.
            SkipTransition = true;
            CloseButtonEnabled = true;
            if (!preloaded)
            {
                preloaded = true;
                var icons = new System.Collections.Generic.List<string>();
                foreach (QuickAction[] set in new[] { Actions, ShipActions, PilotActions, new[] { Camera } })
                {
                    foreach (QuickAction action in set)
                    {
                        if (!icons.Contains(action.Icon))
                            icons.Add(action.Icon);
                        if (!icons.Contains(action.IconOff))
                            icons.Add(action.IconOff);
                    }
                }
                foreach (QuickAction action in ExternalViewPage.Build(null))
                    if (action != null && !icons.Contains(action.Icon))
                        icons.Add(action.Icon);
                foreach (QuickAction action in Cockpits.SeatPage.Build(null))
                    if (!icons.Contains(action.Icon))
                        icons.Add(action.Icon);
                foreach (QuickAction action in MenuRow)
                    if (!icons.Contains(action.Icon))
                        icons.Add(action.Icon);
                if (!icons.Contains(AddOnsTile.Icon))
                    icons.Add(AddOnsTile.Icon);
                foreach (var situation in Situations)
                    if (!icons.Contains(situation.Icon))
                        icons.Add(situation.Icon);
                if (!icons.Contains(Cockpits.SeatPage.Tile.Icon))
                    icons.Add(Cockpits.SeatPage.Tile.Icon);
                MyRenderProxy.PreloadTextures(icons, TextureType.GUI);
            }
            RecreateControls(constructor: true);
        }

        public override string GetFriendlyName() => "QuickActionsScreen";

        /// <summary>Game thread: opens the panel on its External view page (the actions wheel's way in, in a seat).</summary>
        internal static void OpenExternalViewPage()
        {
            var screen = new QuickActionsScreen(true);
            MyGuiSandbox.AddScreen(screen);
            Log.Info($"Wrist panel: opened on its External view page (actions wheel), {screen.TileCount} tiles");
        }

        /// <summary>The whole GUI layout in the GUI's normalised measures (4/3 wide, 1 high for the 1920x1080 panel).</summary>
        internal static Vector2 FullSize()
        {
            Vector2 size = MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(MyGuiManager.GetFullscreenRectangle().Width, MyGuiManager.GetFullscreenRectangle().Height));
            return size.X > 0.5f && size.Y > 0.5f ? size : new Vector2(4f / 3f, 1f);
        }

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            Vector2 size = Size ?? FullSize();
            foot = null;

            if (plain)
            {
                RecreatePlain(size);
                return;
            }

            bool viewPage = page == Page.ExternalView, seatPage = page == Page.Seat, controlsPage = page == Page.Controls;
            bool addOnsPage = page == Page.AddOns;
            bool actionsPage = !viewPage && !seatPage && !controlsPage && !addOnsPage;
            TileCount = 0;
            // The actions page has the menus row where the others have their caption.
            if (!actionsPage)
                AddCaption(viewPage ? "External View" : seatPage ? "Cockpit" : addOnsPage ? AddOnsTitle : "Controls", null, new Vector2(0f, TitleHeight / 2f - MyGuiConstants.SCREEN_CAPTION_DELTA_Y - 0.005f), CaptionScale);

            float left = -size.X / 2f, top = -size.Y / 2f, inner = size.X - 2f * MarginX;
            var lines = new MyGuiControlSeparatorList();
            // The frame: the game's background has it one pixel wide, which the wrist would lose.
            lines.AddHorizontal(new Vector2(left, top), size.X, Line, FrameColor);
            lines.AddHorizontal(new Vector2(left, -top - Line), size.X, Line, FrameColor);
            lines.AddVertical(new Vector2(left, top), size.Y, Line * 0.75f, FrameColor);
            lines.AddVertical(new Vector2(-left - Line * 0.75f, top), size.Y, Line * 0.75f, FrameColor);
            // Under the caption and over the foot, as the game's screens have them.
            lines.AddHorizontal(new Vector2(left + MarginX, top + TitleHeight - Gap / 2f), inner, Line);
            lines.AddHorizontal(new Vector2(left + MarginX, -top - FootHeight), inner, Line);
            Controls.Add(lines);

            seatedLayout = actionsPage && Seat != null;
            if (actionsPage)
                AddMenuRow(size);
            if (controlsPage)
            {
                hint = ControlsHint;
                AddControlsPage(size, lines);
            }
            else if (seatedLayout)
            {
                hint = Hint;
                AddSeated(size, lines);
            }
            else
            {
                QuickAction[] tiles;
                if (viewPage)
                {
                    tiles = ExternalViewPage.Build(() => switchTo = Page.Actions);
                    hint = PageHint;
                }
                else if (seatPage)
                {
                    tiles = Cockpits.SeatPage.Build(() => switchTo = Page.Actions);
                    hint = SeatHint;
                }
                else if (addOnsPage)
                {
                    tiles = AddOnTiles();
                    hint = AddOnsHint;
                }
                else
                {
                    tiles = new QuickAction[Actions.Length + 1];
                    Actions.CopyTo(tiles, 0);
                    tiles[Actions.Length] = ExternalViewTile;
                    hint = Hint;
                }
                Vector2[] centres = Layout(size, tiles.Length, plain, out Vector2 cell);
                for (int i = 0; i < tiles.Length; i++)
                {
                    if (tiles[i] != null)
                    {
                        Controls.Add(new Tile(tiles[i], centres[i], cell, this));
                        TileCount++;
                    }
                    else
                        Controls.Add(new ViewReadout(centres[i], cell));
                }
            }
            UpdateEnabled();

            foot = new MyGuiControlLabel(new Vector2(0f, -top - FootHeight / 2f), null, hint, FootColor, FootScale, "White",
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true, inner, isAutoScaleEnabled: true);
            Controls.Add(foot);
        }

        /// <summary>
        /// The seated page: the ship's switches and then the pilot's, each group under its name and a line, <see cref="SeatedColumns"/>
        /// across, a last row that is not full centred under the others.
        /// </summary>
        private void AddSeated(Vector2 size, MyGuiControlSeparatorList lines)
        {
            QuickAction[][] groups = SeatedGroups(out string[] names);
            float width = size.X - 2f * MarginX;
            int rows = 0;
            foreach (QuickAction[] group in groups)
                rows += (group.Length + SeatedColumns - 1) / SeatedColumns;
            float y = -size.Y / 2f + TitleHeight + Gap / 2f;
            float bottom = size.Y / 2f - FootHeight - Gap;
            var cell = new Vector2((width - (SeatedColumns - 1) * Gap) / SeatedColumns,
                                   (bottom - y - groups.Length * GroupHeight - (rows - 1) * Gap) / rows);
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0)
                    y += Gap;
                float headingY = y + GroupHeight / 2f;
                Controls.Add(new MyGuiControlLabel(new Vector2(-width / 2f, headingY), null, names[g], FootColor, GroupScale, "White",
                    MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER));
                float named = MyGuiManager.MeasureString("White", names[g], GroupScale).X + Gap;
                lines.AddHorizontal(new Vector2(-width / 2f + named, headingY), width - named, Line);
                y += GroupHeight;

                QuickAction[] tiles = groups[g];
                for (int i = 0; i < tiles.Length; i++)
                {
                    int column = i % SeatedColumns, row = i / SeatedColumns;
                    int inRow = Math.Min(SeatedColumns, tiles.Length - row * SeatedColumns);
                    float shift = (SeatedColumns - inRow) * (cell.X + Gap) / 2f;
                    var centre = new Vector2(-width / 2f + cell.X / 2f + column * (cell.X + Gap) + shift, y + cell.Y / 2f + row * (cell.Y + Gap));
                    Controls.Add(new Tile(tiles[i], centre, cell, this));
                    TileCount++;
                }
                int groupRows = (tiles.Length + SeatedColumns - 1) / SeatedColumns;
                y += groupRows * cell.Y + (groupRows - 1) * Gap;
            }
        }

        private const string Hint = "Point at an action and pull the trigger";
        private const string PageHint = "Point at a control and pull the trigger; grip the screen to move it";
        private const string SeatHint = "Point at a control and pull the trigger; each arrow press moves the seat a step";

        /// <summary>
        /// A page change asked for by a button is made here, outside the controls' own input pass; the actions are laid out
        /// again when the player sits down or gets up with the panel up; and the buttons that cannot be used now are greyed.
        /// </summary>
        public override bool Update(bool hasFocus)
        {
            if (switchTo.HasValue)
            {
                page = switchTo.Value;
                switchTo = null;
                Guard("page", () => RecreateControls(false));
                Log.Info($"Wrist panel: {(page == Page.ExternalView ? "External view page" : page == Page.Seat ? "Cockpit page" : page == Page.Controls ? "Controls page (" + ControlGuide.Title(controlsShown) + ")" : page == Page.AddOns ? AddOnsLogLine() : "back to the actions")}{(seatedLayout ? " (seated)" : string.Empty)}, {TileCount} tiles");
            }
            else if (!plain && page == Page.Controls && controlsStamp != ControlsStampNow())
            {
                // A plugin or mod registered or removed an action (or a binding moved) with the page up: its rows are made again.
                Guard("controls", () => RecreateControls(false));
                Log.Info("Wrist panel: Controls page made again (the registered actions or the bindings changed)");
            }
            else if (!plain && page == Page.Seat && !Cockpits.SeatPage.Available)
            {
                // Out of his cockpit (got up, or his cockpit setting turned off) with the seat page up: back to the actions.
                switchTo = Page.Actions;
            }
            else if (!plain && page == Page.Actions && seatedLayout != (Seat != null))
            {
                Guard("seat", () => RecreateControls(false));
                Log.Info($"Wrist panel: {(seatedLayout ? "seated" : "on-foot")} actions now, {TileCount} tiles");
            }
            UpdateEnabled();
            return base.Update(hasFocus);
        }

        private void UpdateEnabled()
        {
            foreach (MyGuiControlBase control in Controls)
            {
                if (!(control is Tile tile) || tile.Entry.CanUse == null)
                    continue;
                bool can;
                try
                {
                    can = tile.Entry.CanUse();
                }
                catch (Exception)
                {
                    can = false;
                }
                if (tile.Enabled != can)
                    tile.Enabled = can;
            }
        }

        /// <summary>
        /// Where the buttons go on a screen of this size (the controls' positions are from its middle): a grid
        /// <see cref="Columns"/> across between the caption and the foot, the cell size and the middle of each cell, in reading order.
        /// </summary>
        internal static Vector2[] Layout(Vector2 size, int count, bool old, out Vector2 cell)
        {
            float marginX = old ? PlainMarginX : MarginX, gap = old ? PlainGap : Gap;
            float topSpace = old ? PlainTitleHeight : TitleHeight + Gap / 2f;
            float bottomSpace = old ? PlainBottomMargin : FootHeight + Gap;
            int rows = (count + Columns - 1) / Columns;
            float width = size.X - 2f * marginX;
            float top = -size.Y / 2f + topSpace;
            float height = size.Y / 2f - bottomSpace - top;
            cell = new Vector2((width - (Columns - 1) * gap) / Columns, (height - (rows - 1) * gap) / rows);
            var centres = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int column = i % Columns, row = i / Columns;
                // A last row that is not full is centred under the others.
                int inRow = Math.Min(Columns, count - row * Columns);
                float shift = (Columns - inRow) * (cell.X + gap) / 2f;
                centres[i] = new Vector2(-width / 2f + cell.X / 2f + column * (cell.X + gap) + shift, top + cell.Y / 2f + row * (cell.Y + gap));
            }
            return centres;
        }

        private void RecreatePlain(Vector2 size)
        {
            var title = new MyGuiControlLabel(new Vector2(0f, -size.Y / 2f + PlainTitleHeight / 2f), null, "Quick actions", null, PlainTitleScale, "White",
                MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER, isAutoEllipsisEnabled: true, size.X - 2f * PlainMarginX, isAutoScaleEnabled: true);
            Controls.Add(title);

            Vector2[] centres = Layout(size, Actions.Length, plain, out Vector2 cell);
            TileCount = Actions.Length;
            for (int i = 0; i < Actions.Length; i++)
            {
                QuickAction action = Actions[i];
                Controls.Add(new MyGuiControlButton(centres[i], MyGuiControlButtonStyleEnum.Rectangular, cell, null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                    action.Tooltip + " (" + action.DefaultKey + ")", new StringBuilder(action.Label), PlainButtonTextScale, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                    MyGuiControlHighlightType.WHEN_CURSOR_OVER, sender => Guard(action.Label, () => Choose(action)), isAutoscaleEnabled: true, isEllipsisEnabled: true));
            }
        }

        /// <summary>The foot says what the pointed-at button does, and how to use the panel while none is.</summary>
        private void Pointed(Tile tile, bool on)
        {
            if (foot == null)
                return;
            if (on)
                foot.Text = tile.Entry.Tooltip;
            else if (foot.Text == tile.Entry.Tooltip)
                foot.Text = hint;
        }

        /// <summary>
        /// Closes the panel and presses the control for the game's next gameplay input; or, for the panel's own buttons, does
        /// what the button does and leaves the panel up (the External view tile opens its page).
        /// </summary>
        private void Choose(QuickAction action)
        {
            if (!plain)
                Hands.Haptics.Pulse(VRSettings.DominantHand, PressAmplitude, PressSeconds);
            if (action == ExternalViewTile)
            {
                switchTo = Page.ExternalView;
                return;
            }
            if (action == AddOnsTile)
            {
                OpenAddOns();
                return;
            }
            if (action == ControlsTile)
            {
                controlsShown = PlayerSituation();
                switchTo = Page.Controls;
                return;
            }
            if (action == Cockpits.SeatPage.Tile)
            {
                if (Cockpits.SeatPage.Available)
                    switchTo = Page.Seat;
                return;
            }
            if (action.CanUse != null && !action.CanUse())
                return;
            if (action.Run != null)
            {
                action.Run();
                if (action.ClosesPanel)
                {
                    Log.Info($"Wrist panel: {action.Label}");
                    CloseScreen();
                }
                return;
            }
            WristButton.Press(action.Control, action.Label, action.Control == MyControlsSpace.DAMPING_RELATIVE ? look : null);
            CloseScreen();
        }

        private const float HoverAmplitude = 0.18f, HoverSeconds = 0.012f, PressAmplitude = 0.35f, PressSeconds = 0.03f;

        protected override void OnClosed()
        {
            base.OnClosed();
            WristButton.ScreenClosed(this);
        }

        /// <summary>A mistake in one control's handler must not take the game's menu loop down with it.</summary>
        private static void Guard(string what, Action action) => Log.Guard("Quick actions", what, action);

        /// <summary>
        /// What a button says under its name now: its own words if it has some to say, else On or Off for a switch; and the
        /// switch's state (null when it is not a switch, or it cannot be told this frame).
        /// </summary>
        private static string Words(QuickAction action, out bool? on)
        {
            on = null;
            try
            {
                on = action.State?.Invoke();
            }
            catch (Exception)
            {
                // A switch whose state cannot be read this frame is drawn as a plain button.
            }
            string words = null;
            if (action.StateText != null)
            {
                try
                {
                    words = action.StateText();
                }
                catch (Exception)
                {
                    // Words that cannot be worked out this frame are left off.
                }
            }
            if (words == null && on.HasValue)
                words = on.Value ? "On" : "Off";
            return words;
        }

        private static bool Usable(QuickAction action)
        {
            try
            {
                return action.CanUse == null || action.CanUse();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// One action: the game's rectangular button, its frame drawn <see cref="BorderScale"/> times over, the action's icon on
        /// the left, its name, and for a switch whether it is on. The button's own logic picks the state (normal, pointed at,
        /// held down); the icon and text follow it.
        /// </summary>
        private sealed class Tile : MyGuiControlButton
        {
            private readonly QuickActionsScreen screen;

            public Tile(QuickAction action, Vector2 at, Vector2 size, QuickActionsScreen screen)
                : base(at, MyGuiControlButtonStyleEnum.Rectangular, size, null, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                       null, new StringBuilder(), 1f, MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                       MyGuiControlHighlightType.WHEN_CURSOR_OVER, null)
            {
                Entry = action;
                this.screen = screen;
                CustomStyle = TileStyle;
                Size = size;
                Name = "QuickAction" + action.Label.Replace(" ", string.Empty);
                ButtonClicked += sender => Guard(action.Label, () => screen.Choose(action));
            }

            public QuickAction Entry { get; }

            protected override void OnHasHighlightChanged()
            {
                base.OnHasHighlightChanged();
                if (HasHighlight)
                    Hands.Haptics.Pulse(VRSettings.DominantHand, HoverAmplitude, HoverSeconds);
                screen?.Pointed(this, HasHighlight);
            }

            public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
            {
                MyGuiCompositeTexture frame = BackgroundTexture;
                bool pointed = frame == TileStyle.HighlightTexture, held = frame == TileStyle.ActiveTexture;
                Vector2 topLeft = GetPositionAbsoluteTopLeft(), size = Size;
                frame?.Draw(topLeft, size, ApplyColorMaskModifiers(ColorMask, Enabled, backgroundTransitionAlpha), BorderScale);

                string words = Words(Entry, out bool? on);
                bool dim = on == false;

                // The pointed-at button is the game's light blue: its text and icon go dark on it, as the game's do.
                Vector4 ink = pointed ? MyGuiConstants.HIGHLIGHT_TEXT_COLOR : Vector4.One;
                string font = pointed || held ? "White" : "Blue";

                // The icon: a square the height of most of the button, at its left.
                float iconHeight = size.Y * 0.62f;
                Vector2 square = MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(100f, 100f));
                var iconSize = new Vector2(iconHeight * square.X / square.Y, iconHeight);
                float pad = (size.Y - iconHeight) / 2f;
                var iconCentre = new Vector2(topLeft.X + pad * 0.8f + iconSize.X / 2f, topLeft.Y + size.Y / 2f);
                Vector4 iconColor = pointed ? ink : (Entry.WhiteIcon ? IconBlue : Vector4.One);
                if (dim && !held)
                    iconColor.W *= pointed ? 0.55f : 0.4f;
                var drawn = new Vector2(iconSize.Y * Entry.IconScale * Entry.IconAspect * square.X / square.Y, iconSize.Y * Entry.IconScale);
                MyGuiManager.DrawSpriteBatch(dim ? Entry.IconOff : Entry.Icon, iconCentre, drawn,
                    ApplyColorMaskModifiers(iconColor, Enabled, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                    useFullClientArea: false, waitTillLoaded: false, rotation: Entry.IconRotation);

                // The name, and under it On or Off for a switch, or the button's value.
                float textLeft = iconCentre.X + iconSize.X / 2f + pad * 0.5f;
                float textWidth = topLeft.X + size.X - pad * 0.6f - textLeft;
                float labelScale = Fit(font, Entry.Label, LabelScale, textWidth);
                float labelHeight = MyGuiManager.MeasureString(font, Entry.Label, labelScale).Y;
                float y = topLeft.Y + size.Y / 2f;
                if (!string.IsNullOrEmpty(words))
                {
                    float stateHeight = MyGuiManager.MeasureString("White", "Off", StateScale).Y;
                    float labelY = y - stateHeight / 2f * 0.9f;
                    MyGuiManager.DrawString(font, Entry.Label, new Vector2(textLeft, labelY), labelScale,
                        ApplyColorMaskModifiers(ink, Enabled, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                    Vector4 stateColor = pointed ? ink : held ? Vector4.One : on != false ? IconBlue : OffColor;
                    MyGuiManager.DrawString("White", words, new Vector2(textLeft, labelY + labelHeight / 2f + stateHeight * 0.45f), Fit("White", words, StateScale, textWidth),
                        ApplyColorMaskModifiers(stateColor, Enabled, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                }
                else
                {
                    MyGuiManager.DrawString(font, Entry.Label, new Vector2(textLeft, y), labelScale,
                        ApplyColorMaskModifiers(ink, Enabled, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                }
            }

            /// <summary>The largest scale up to <paramref name="scale"/> at which the text fits the width.</summary>
            internal static float Fit(string font, string text, float scale, float width)
            {
                float measured = MyGuiManager.MeasureString(font, text, scale).X;
                return measured > width && measured > 0f ? scale * width / measured : scale;
            }
        }

        /// <summary>
        /// The middle of the External view page's orbit cross: not a button, the camera the arrows move round - the page's
        /// icon, what the screen shows now, and how to move the screen itself.
        /// </summary>
        private sealed class ViewReadout : MyGuiControlBase
        {
            public ViewReadout(Vector2 at, Vector2 size)
                : base(at, size, isActiveControl: false)
            {
                Name = "QuickActionExternalViewShowing";
            }

            public override void Draw(float transitionAlpha, float backgroundTransitionAlpha)
            {
                Vector2 topLeft = GetPositionAbsoluteTopLeft(), size = Size;
                Vector2 square = MyGuiManager.GetNormalizedSizeFromScreenSize(new Vector2(100f, 100f));
                float iconHeight = size.Y * 0.5f;
                var iconSize = new Vector2(iconHeight * square.X / square.Y, iconHeight);
                var centre = new Vector2(topLeft.X + size.X / 2f, topLeft.Y + size.Y * 0.32f);
                MyGuiManager.DrawSpriteBatch(ExternalViewPage.PageIcon, centre, iconSize,
                    ApplyColorMaskModifiers(Vector4.One, true, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,
                    useFullClientArea: false, waitTillLoaded: false);

                string showing = ExternalViewPage.Showing(), hint = ExternalViewPage.Hint();
                float width = size.X * 0.94f;
                MyGuiManager.DrawString("White", showing, new Vector2(centre.X, topLeft.Y + size.Y * 0.69f), Tile.Fit("White", showing, StateScale, width),
                    ApplyColorMaskModifiers(Vector4.One, true, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
                MyGuiManager.DrawString("Blue", hint, new Vector2(centre.X, topLeft.Y + size.Y * 0.88f), Tile.Fit("Blue", hint, StateScale * 0.8f, width),
                    ApplyColorMaskModifiers(FootColor, true, transitionAlpha), MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER);
            }
        }
    }
}
