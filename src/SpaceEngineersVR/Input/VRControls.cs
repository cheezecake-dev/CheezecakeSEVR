using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Tracking;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>Which of the game's control sets the hands are driving.</summary>
    internal enum VRContext
    {
        None,
        OnFoot,
        Jetpack,

        /// <summary>A ship controller: a cockpit, a remote control, a rover's seat, anything that is a MyShipController.</summary>
        Ship,

        /// <summary>A controlled turret, or another block that is flown like one (turret control block, searchlight).</summary>
        Turret,
    }

    /// <summary>
    /// What the hands do to the game: the default controls table in docs/VR_PLAN.md, turned into the game's own
    /// controls, for the character on foot and on the jetpack and for the seat of a ship, a rover, a remote control, a
    /// turret or a camera block. The analog parts (walking, flying, steering, the snap turn) are added to the game's
    /// movement, rotation and roll input; the buttons answer the game's questions about its controls. Everything is
    /// added to what the keyboard, mouse and a gamepad give, never instead of it. Building gets nothing yet; the game's radial
    /// menu is opened and steered by <see cref="ToolbarWheel"/>, which has the hands while it is up.
    /// </summary>
    internal static class VRControls
    {
        /// <summary>Debug: the hands do nothing to the game (<see cref="Rendering.RenderDebug"/> VRInput=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Test seam: stands in for the controlled entity, so the mapping can be driven without a game.</summary>
        internal static VRContext? ContextOverride { get; set; }

        /// <summary>Test seam: what the game's camera is doing, with <see cref="ContextOverride"/>.</summary>
        internal static bool CameraViewOverride { get; set; }

        /// <summary>Test seam: with <see cref="ContextOverride"/>, the seat is one that does not fly the ship (a passenger seat).</summary>
        internal static bool SeatCannotFlyOverride { get; set; }

        /// <summary>Test seam: stands in for the screen that has focus when it is a menu (null: none; gameplay does not count), so a menu can be had without a game.</summary>
        internal static Func<object> MenuScreenOverride { get; set; }

        /// <summary>Test seam: stands in for the game's gameplay input pass that <see cref="WalkBesidePanel"/> runs (the pass asks the game's questions about the controls), so it can be had without a game.</summary>
        internal static Action GameplayPassOverride { get; set; }

        /// <summary>How long Y has to be held before it leaves the seat (or the camera), so a tap cannot eject you.</summary>
        public const double UseHoldSeconds = 0.5;

        private const float StickPress = 0.6f, StickRelease = 0.3f;
        private const float FlickPress = 0.7f, FlickRelease = 0.3f;

        /// <summary>Longest time one frame's smooth turn is worked out for, seconds, so a stall (a loading screen) is not one huge turn after it.</summary>
        private const double MaxSmoothTurnStep = 0.1;

        /// <summary>What the hands ask of the game, one bit each in the frame's held set.</summary>
        private enum Intent
        {
            Primary,
            Secondary,
            Use,
            Jump,
            Crouch,
            Thrusts,
            Damping,
            Sprint,
            MainMenu,
            Lights,
            LandingGear,

            /// <summary>B or Menu pressed while a menu has focus: the game's GUI CANCEL (MyControlsGUI.CANCEL), which is not a gameplay control and has no entry in <see cref="Intents"/>.</summary>
            Back,
        }

        private static readonly Dictionary<MyStringId, Intent> Intents = new Dictionary<MyStringId, Intent>
        {
            [MyControlsSpace.PRIMARY_TOOL_ACTION] = Intent.Primary,
            [MyControlsSpace.SECONDARY_TOOL_ACTION] = Intent.Secondary,
            [MyControlsSpace.USE] = Intent.Use,
            [MyControlsSpace.JUMP] = Intent.Jump,
            [MyControlsSpace.CROUCH] = Intent.Crouch,
            [MyControlsSpace.THRUSTS] = Intent.Thrusts,
            [MyControlsSpace.DAMPING] = Intent.Damping,
            [MyControlsSpace.SPRINT] = Intent.Sprint,
            [MyControlsSpace.MAIN_MENU] = Intent.MainMenu,
            [MyControlsSpace.HEADLIGHTS] = Intent.Lights,
            [MyControlsSpace.LANDING_GEAR] = Intent.LandingGear,
        };

        // This frame's and the last frame's actions held, as bits. The edges are taken from these rather than from
        // VRInput's buttons because jump is a button or a stick direction, and the stick has no edges of its own.
        private static int now, before;
        private static Vector2 leftStick, rightStick;
        private static bool jumpButton;
        private static bool stickUp, stickDown;
        private static bool flickArmed = true;
        private static float snap;
        private static double lastSeconds;
        private static float frameSeconds;
        private static int smoothDirection;

        // The seated frame: what the sticks, buttons and grips add to the game's movement, rotation and roll.
        private static VRContext context;
        private static bool cameraView, seatSticks;
        private static Vector3 seatMove;
        private static Vector2 seatRotation;
        private static float seatRoll;
        private static string loggedContext;

        // What the hands gave the game's movement and turning this frame, and last frame's: what Motion reports, so it
        // holds still while the render thread reads it.
        private static float moveGiven, turnGiven, moveShown, turnShown;
        private static bool snapGiven;
        private static int snapCount;

        // Y held, from the frame it was pressed in a seat or a camera view; the one frame it gets USE is when the hold completes.
        private static double holdStart = -1d;
        private static bool holdDone;

        // B or Menu pressed in a menu: each is a Back only for a press that began while that menu had focus, and only for
        // as long as the same screen does, so a button held from gameplay, or a press that closed one screen, is not read
        // again by the screen underneath. The kinds of question the game was answered yes to are logged once (backAnswered).
        private static bool backB, backMenu;
        private static object backScreen;

        // The left Menu button (MenuButton): held MenuHoldSeconds it recentres the view, once a press; let go sooner it is a
        // tap, given on the release to where the press began: the game menu from gameplay, Back in the menu that had focus.
        private static double menuDown = -1d;
        private static bool menuRecentred, menuTapped;
        private static object menuBeganIn, menuBackIn;
        private static int backAnswered;
        private static string backAnsweredIn, loggedMenu;
        private static object menuNow;
        private static readonly Func<string> WhereNow = Where;

        // The wrist panel has the focus and the hands walk beside it (WalkBesidePanel); panelPass is true while the game's
        // gameplay pass runs for it, when the game's questions are answered for locomotion only (Wants).
        private static bool panelWalk;
        private static volatile bool panelPass;
        private static int panelErrors;

        /// <summary>Something is held or was let go of this frame, so a control may have an answer.</summary>
        public static bool Live => (now | before) != 0;

        /// <summary>The control set the hands drive this frame.</summary>
        public static VRContext Context => context;

        /// <summary>
        /// Artificial motion for the comfort vignette (<see cref="Rendering.Vignette"/>), readable from any thread: how hard
        /// the hands moved and turned the player last frame, each 0 to 1 (a full stick; a seat's turn as a share of the
        /// gyros' full torque), and how many snap turns they have made in all. Zero while the game is not asking for the
        /// hands' movement (a menu has focus, the wrist panel apart: <see cref="WalkBesidePanel"/>); never real head motion.
        /// </summary>
        internal static (float Move, float Turn, int Snaps) Motion => (moveShown, turnShown, snapCount);

        /// <summary>The context is a seat: a ship controller or a turret.</summary>
        public static bool IsSeated(VRContext c) => c == VRContext.Ship || c == VRContext.Turret;

        /// <summary>
        /// Game thread, once per frame, right after <see cref="VRInput.Update"/>: works out which control set the
        /// hands drive, then this frame's actions, the stick directions that count as buttons, a snap turn if the right
        /// stick was just flicked sideways, and what a seat is given.
        /// </summary>
        public static void Update() => Update(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

        /// <summary>As <see cref="Update()"/>, with the time given in seconds (the hold on Y is timed by it).</summary>
        internal static void Update(double seconds)
        {
            frameSeconds = lastSeconds > 0d ? (float)Math.Min(Math.Max(seconds - lastSeconds, 0d), MaxSmoothTurnStep) : 0f;
            lastSeconds = seconds;
            before = now;
            now = 0;
            moveShown = moveGiven;
            turnShown = turnGiven;
            moveGiven = turnGiven = 0f;
            if (snapGiven)
                snapCount++;
            snapGiven = false;
            leftStick = rightStick = Vector2.Zero;
            jumpButton = false;
            snap = 0f;
            seatMove = Vector3.Zero;
            seatRotation = Vector2.Zero;
            seatRoll = 0f;
            if (!VRInput.Active || Off)
            {
                context = VRContext.None;
                cameraView = seatSticks = false;
                HandFlight.Frame(false);
                stickUp = stickDown = false;
                flickArmed = true;
                holdStart = -1d;
                holdDone = false;
                backB = backMenu = false;
                backScreen = menuNow = null;
                backAnswered = 0;
                menuDown = -1d;
                menuRecentred = menuTapped = false;
                menuBeganIn = menuBackIn = null;
                panelWalk = false;
                ControlHints.Update(false, false);
                ToolbarWheel.Reset();
                return;
            }

            context = Resolve(out cameraView, out seatSticks, out string detail);
            LogContext(detail);
            VRPrompts.Frame(context);
            HandFlight.Frame(context == VRContext.Ship && seatSticks);

            leftStick = Dead(VRInput.Get(Hand.Left).Stick);
            rightStick = Dead(VRInput.Get(Hand.Right).Stick);

            ApplyWheel(seconds);
            if (!ToolbarWheel.Owns)
            {
                if (IsSeated(context))
                    UpdateSeated();
                else
                    UpdateWalking();
            }
            UpdateUseHold(seconds);

            menuNow = MenuScreen();
            MenuButton(seconds, menuNow);
            panelWalk = WalksBesidePanel(menuNow, context, ToolbarWheel.Owns);
            UpdateBack(menuNow);
            ControlHints.Update(IsSeated(context), menuNow != null && !ToolbarWheel.Owns && !(menuNow is Gui.ActionsWheelScreen),
                walking: menuNow == null && !ToolbarWheel.Owns && (context == VRContext.OnFoot || context == VRContext.Jetpack),
                inventory: menuNow is MyGuiScreenTerminal && MyGuiScreenTerminal.GetCurrentScreen() == VRage.Game.ModAPI.MyTerminalPageEnum.Inventory);
            LogBack();
            InputLog.Frame(seconds, WhereNow);
        }

        /// <summary>
        /// The screen that has focus when it is a menu, or null: the gameplay screen has focus while the player plays, and
        /// anything else (the pause menu, options, the terminal, the G menu, the wrist panel, a message box) is a menu.
        /// </summary>
        private static object MenuScreen()
        {
            if (MenuScreenOverride != null)
                return MenuScreenOverride();
            try
            {
                MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
                return focus != null && !(focus is MyGuiScreenGamePlay) ? focus : null;
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not tell whether a menu has focus");
                return null;
            }
        }

        /// <summary>
        /// The screen with focus is the wrist panel (<see cref="QuickActionsScreen"/>, opened with Y or by looking at the
        /// gauntlet) and the hands drive a character on foot or on the jetpack. Every other menu has the hands to itself; this one
        /// is a wrist watch, glanced at while walking.
        /// </summary>
        internal static bool WalksBesidePanel(object menu, VRContext c, bool wheelOwns) =>
            !wheelOwns && menu is QuickActionsScreen && (c == VRContext.OnFoot || c == VRContext.Jetpack);

        /// <summary>The hands walk, turn, jump and fly beside the wrist panel this frame (<see cref="WalkBesidePanel"/> runs).</summary>
        internal static bool WalkingBesidePanel => panelWalk;

        /// <summary>
        /// Game thread, once per frame after <see cref="Update()"/>: while the wrist panel has the focus the game does not
        /// read the controls for walking, because it hands the input to the screen with the focus only, and the gameplay
        /// screen is not that. So it is run here: the gameplay screen's pass (MyGuiScreenGamePlay.HandleUnhandledInput, which
        /// reads the controls and calls MoveAndRotate, Jump, Crouch and Sprint on the character), with the game's questions
        /// answered for the left stick (move), the right stick (turn, and its up and down jump and crouch), A (jump, and the
        /// jetpack's climb) and the left stick click (sprint), and nothing else (<see cref="Wants"/>). The trigger that
        /// clicks the panel, the grips, B and Menu (the panel's Back) and X stay the panel's: a button pressed on the panel
        /// does not also fire a tool or open the pause menu. Keyboard, mouse and a pad are answered as they always are.
        /// </summary>
        public static void WalkBesidePanel()
        {
            if (!panelWalk)
                return;
            try
            {
                Action pass = GameplayPassOverride;
                if (pass == null)
                {
                    MyGuiScreenGamePlay gameplay = MyGuiScreenGamePlay.Static;
                    if (gameplay == null || ControlledCharacter() == null)
                        return;
                    pass = () => gameplay.HandleUnhandledInput(false);
                }
                panelPass = true;
                pass();
            }
            catch (Exception e)
            {
                if (panelErrors++ < 3)
                    Log.Error(e, "Walking beside the wrist panel failed this frame");
            }
            finally
            {
                panelPass = false;
            }
        }

        /// <summary>
        /// B, or a tap of the left Menu button (<see cref="MenuButton"/>: a hold recentres), in a menu is Back: the game's own GUI CANCEL, the question every screen that
        /// closes on Escape also asks (MyGuiScreenBase.HandleInput, a message box's release). The press has to begin in the
        /// menu, and it belongs to the screen that had focus when it began: when another screen takes focus before the
        /// button is let go (this one closed, or opened a confirmation) the rest of the press is dropped, so the release
        /// is not taken by the screen underneath. The toolbar wheel closes itself on B and is left alone.
        /// </summary>
        private static void UpdateBack(object menu)
        {
            int bit = 1 << (int)Intent.Back;
            if (menu == null || ToolbarWheel.Owns)
            {
                before &= ~bit;
                backB = backMenu = false;
                backScreen = null;
                return;
            }
            bool held = backB || backMenu;
            if (held && !ReferenceEquals(menu, backScreen))
            {
                before &= ~bit;
                backB = backMenu = false;
                held = false;
            }
            backB = VRInput.IsPressed(Hand.Right, VRButtons.B) && (backB || VRInput.IsNewPressed(Hand.Right, VRButtons.B));
            // Menu is Back on a tap's release (MenuButton: a hold recentres instead), for the one frame of the release.
            backMenu = MenuHoldOff
                ? VRInput.IsPressed(Hand.Left, VRButtons.Menu) && (backMenu || VRInput.IsNewPressed(Hand.Left, VRButtons.Menu))
                : menuBackIn != null && ReferenceEquals(menuBackIn, menu);
            if (!backB && !backMenu)
                return;
            if (!held)
                backScreen = menu;
            now |= bit;
        }

        /// <summary>How long the left Menu button is held to recentre the view, seconds (<see cref="MenuButton"/>).</summary>
        public const double MenuHoldSeconds = 0.6;

        // The hold's feel on the left hand: nothing for a tap's length, then a light ramp up to the hold, then a firm tick.
        private const double MenuRampFrom = 0.15;
        private const float MenuRampFromAmplitude = 0.08f, MenuRampToAmplitude = 0.35f, MenuRampPulseSeconds = 0.03f;
        private const float MenuTickAmplitude = 0.9f, MenuTickSeconds = 0.05f;

        /// <summary>RenderDebug MenuHoldRecentre=0: the left Menu button is as it was before the hold: the game menu or Back from the press, and no recentre.</summary>
        public static bool MenuHoldOff { get; set; }

        /// <summary>This frame is a tap of the left Menu button's release (<see cref="MenuButton"/>), for screens that close on Menu themselves.</summary>
        internal static bool MenuTapped => menuTapped;

        /// <summary>
        /// The left Menu button: held for <see cref="MenuHoldSeconds"/> it recentres the view on all axes, as Recentre view
        /// does (<see cref="Tracking.GameHead.Recentre"/>), on foot, in a seat and in a menu, once a press, with a light ramp
        /// on the left hand while it builds and a firm tick when it takes; the release after it does nothing. Let go sooner
        /// it is a tap, given on the release, and only to where the press began: the game menu (MAIN_MENU, for that one frame)
        /// from gameplay, Back (<see cref="UpdateBack"/>) in the menu that still has focus. A press during which the focus
        /// changed does nothing, as Back's does. RenderDebug MenuHoldRecentre=0: as before, the game menu while it is held.
        /// </summary>
        private static void MenuButton(double seconds, object menu)
        {
            bool pressed = VRInput.IsPressed(Hand.Left, VRButtons.Menu);
            menuTapped = false;
            menuBackIn = null;
            if (MenuHoldOff)
            {
                Set(Intent.MainMenu, pressed);
                menuDown = -1d;
                return;
            }
            if (pressed)
            {
                if (menuDown < 0d)
                {
                    menuDown = seconds;
                    menuRecentred = false;
                    menuBeganIn = menu;
                }
                double held = seconds - menuDown;
                if (menuRecentred || held < MenuRampFrom)
                    return;
                if (held < MenuHoldSeconds)
                {
                    float share = (float)((held - MenuRampFrom) / (MenuHoldSeconds - MenuRampFrom));
                    Hands.Haptics.Pulse(Hand.Left, MathHelper.Lerp(MenuRampFromAmplitude, MenuRampToAmplitude, share), MenuRampPulseSeconds);
                    return;
                }
                menuRecentred = true;
                Tracking.GameHead.Recentre($"the left Menu button held {held:0.00} s{(menu != null ? " in " + menu.GetType().Name : "")}");
                Hands.Haptics.Pulse(Hand.Left, MenuTickAmplitude, MenuTickSeconds);
                return;
            }
            if (menuDown < 0d)
                return;
            double length = seconds - menuDown;
            menuDown = -1d;
            if (menuRecentred)
                return;
            if (!ReferenceEquals(menu, menuBeganIn))
            {
                Log.Info($"Menu button: tap of {length:0.00} s dropped, the focus changed during it ({menuBeganIn?.GetType().Name ?? "gameplay"} to {menu?.GetType().Name ?? "gameplay"})");
                return;
            }
            menuTapped = true;
            if (menu == null)
                Set(Intent.MainMenu, true);
            else
                menuBackIn = menu;
            Log.Info($"Menu button: tap of {length:0.00} s, {(menu == null ? "the game menu" : "Back in " + menu.GetType().Name)}");
        }

        /// <summary>Where the hands are doing this, for the input log.</summary>
        private static string Where()
        {
            if (menuNow != null)
                return "menu " + menuNow.GetType().Name;
            if (ToolbarWheel.Owns)
                return "toolbar wheel";
            switch (context)
            {
                case VRContext.OnFoot: return "on foot";
                case VRContext.Jetpack: return "jetpack";
                case VRContext.Ship: return "in a seat";
                case VRContext.Turret: return "in a turret";
                default: return "no controlled entity";
            }
        }

        /// <summary>Logs a menu taking focus or losing it, and that the game asked for Back and was told yes, so the log shows how far a press got.</summary>
        private static void LogBack()
        {
            string name = menuNow?.GetType().Name;
            if (name != loggedMenu)
            {
                loggedMenu = name;
                InputLog.Note(name == null ? "Input: no menu has focus" : "Input: menu focus is " + name);
            }
            if (backAnswered == 0)
                return;
            string asked = (backAnswered & (1 << (int)MyControlStateType.NEW_PRESSED)) != 0 ? "new pressed" : "new released";
            backAnswered = 0;
            InputLog.Note($"Input: Back answered the game's CANCEL question ({asked}) while {backAnsweredIn ?? "no menu"} had focus");
        }

        /// <summary>
        /// The toolbar wheel (<see cref="ToolbarWheel"/>) reads the hands first, before the sticks are turned into walking,
        /// turning or flying. While it has them the game's radial menu is the only thing being steered: no stick moves or
        /// turns the character or the ship (and none is worked out, so the right stick that pages the wheel is not logged
        /// as a snap turn), and no button is held for the game, so the trigger that activates an item does not also fire
        /// the tool.
        /// </summary>
        private static void ApplyWheel(double seconds)
        {
            ToolbarWheel.Update(context, seconds, leftStick, rightStick);
            if (!ToolbarWheel.Owns)
                return;
            // The flick that paged the wheel is not a snap turn when the wheel lets go with the stick still over: it re-arms
            // once the stick is back near the middle.
            flickArmed = Math.Abs(rightStick.X) < FlickRelease;
            now = 0;
            leftStick = rightStick = Vector2.Zero;
            jumpButton = stickUp = stickDown = false;
            snap = seatRoll = 0f;
            seatMove = Vector3.Zero;
            seatRotation = Vector2.Zero;
        }

        /// <summary>On foot and on the jetpack: the buttons, the stick directions that count as buttons, the snap turn.</summary>
        private static void UpdateWalking()
        {
            // A stick direction is a button once pushed well past the middle, and stays one until it is nearly back.
            // Only the direction the stick mostly points in counts, so a sideways flick does not also jump.
            bool vertical = Math.Abs(rightStick.Y) >= Math.Abs(rightStick.X);
            stickUp = vertical && rightStick.Y > (stickUp ? StickRelease : StickPress);
            stickDown = vertical && -rightStick.Y > (stickDown ? StickRelease : StickPress);

            // A snap turn once per flick: the stick has to come back near the middle before it can turn again. With Smooth
            // turn on the stick turns the body steadily instead, through the same rotation input, and the flick stays armed
            // for when it is turned off.
            float x = rightStick.X;
            if (VRSettings.SmoothTurn)
            {
                snap = SmoothTurnRadians(rightStick, VRSettings.SmoothTurnSpeedDegrees, frameSeconds);
                flickArmed = Math.Abs(x) < FlickRelease;
                int direction = Math.Sign(snap);
                if (direction != smoothDirection)
                {
                    smoothDirection = direction;
                    Log.Info(direction == 0 ? "Smooth turn stopped" : $"Smooth turn {(direction > 0 ? "right" : "left")} at up to {VRSettings.SmoothTurnSpeedDegrees:F0} deg/s");
                }
            }
            else if (flickArmed)
            {
                if (Math.Abs(x) > FlickPress && Math.Abs(x) > Math.Abs(rightStick.Y))
                {
                    float angle = MathHelper.ToRadians(VRSettings.SnapTurnDegrees);
                    snap = x > 0f ? angle : -angle;
                    flickArmed = false;
                    Log.Info($"Snap turn {(snap > 0f ? "right" : "left")} {VRSettings.SnapTurnDegrees:F0} deg");
                }
            }
            else if (Math.Abs(x) < FlickRelease)
                flickArmed = true;

            jumpButton = VRInput.IsPressed(Hand.Right, VRButtons.A);
            Set(Intent.Primary, VRInput.IsPressed(Hand.Right, VRButtons.Trigger));
            Set(Intent.Secondary, VRInput.IsPressed(Hand.Left, VRButtons.Trigger));
            Set(Intent.Use, VRInput.IsPressed(Hand.Right, VRButtons.Grip) && Hands.GripClaim.Owner(Hand.Right) == null);
            Set(Intent.Jump, jumpButton || stickUp);
            Set(Intent.Crouch, stickDown);
            Set(Intent.Thrusts, VRInput.IsPressed(Hand.Right, VRButtons.B));
            Set(Intent.Damping, VRInput.IsPressed(Hand.Left, VRButtons.X));
            Set(Intent.Sprint, VRInput.IsPressed(Hand.Left, VRButtons.StickClick));
        }

        /// <summary>
        /// Smooth turn: how far the body turns this frame, radians, + right. The stick has had its deadzone taken out, so
        /// the turn is the speed times how far past it the stick is pushed. As for a flick, only a stick mostly pushed
        /// sideways turns: pushed up or down (jump, crouch) it does not.
        /// </summary>
        internal static float SmoothTurnRadians(Vector2 stick, float degreesPerSecond, float seconds)
        {
            if (Math.Abs(stick.X) <= Math.Abs(stick.Y))
                return 0f;
            return MathHelper.ToRadians(degreesPerSecond) * stick.X * seconds;
        }

        /// <summary>
        /// In a seat the right stick turns at a rate, as a pad's does, and no stick direction is a button or a snap
        /// turn. A ship controller also takes the left stick, the grips (roll) and the buttons; a turret only fires.
        /// A seat that does not fly the ship (a passenger seat: the game then uses the stick to turn the view in the
        /// cabin, which the head does here) gets the buttons but no sticks or grips.
        /// </summary>
        private static void UpdateSeated()
        {
            stickUp = stickDown = false;
            // The flick re-arms once the stick has been back in the middle, so leaving a seat with it pushed does not snap-turn.
            flickArmed = Math.Abs(rightStick.X) < FlickRelease;

            GamepadFeel feel = GamepadFeel.Current();
            float sensitivity = VRSettings.ShipStickSensitivity;
            Set(Intent.Primary, VRInput.IsPressed(Hand.Right, VRButtons.Trigger));
            if (seatSticks)
                seatRotation = ShipStick.Rotation(rightStick, feel, sensitivity);
            if (context != VRContext.Ship)
                return;

            bool up = VRInput.IsPressed(Hand.Right, VRButtons.A), down = VRInput.IsPressed(Hand.Right, VRButtons.B);
            if (seatSticks)
            {
                seatMove = ShipStick.Move(leftStick, up, down, feel);
                seatRoll = ShipStick.Roll(Squeeze(Hand.Left), Squeeze(Hand.Right), feel, sensitivity);
                HandFlight.Apply(ref seatRotation, ref seatMove, ref seatRoll, up, down, feel);
            }
            Set(Intent.Secondary, VRInput.IsPressed(Hand.Left, VRButtons.Trigger));
            Set(Intent.Jump, up);
            Set(Intent.Crouch, down);
            Set(Intent.Damping, VRInput.IsPressed(Hand.Left, VRButtons.X));
            Set(Intent.Lights, VRInput.IsPressed(Hand.Left, VRButtons.StickClick));
            Set(Intent.LandingGear, VRInput.IsPressed(Hand.Right, VRButtons.StickClick));
        }

        /// <summary>
        /// Y held for <see cref="UseHoldSeconds"/> in a seat, or looking through a camera block, is the game's USE,
        /// which leaves the seat or the camera. The game acts on USE being let go, so the frame the hold completes
        /// hands it a release: USE was down last frame and is up now. The hold is timed from the press and needs a fresh
        /// press, so Y already down when the seat was entered, or let go and pressed again, starts over.
        /// </summary>
        private static void UpdateUseHold(double seconds)
        {
            bool tracking = !ToolbarWheel.Owns && (IsSeated(context) || cameraView) && VRInput.IsPressed(Hand.Left, VRButtons.Y);
            if (!tracking)
            {
                holdStart = -1d;
                holdDone = false;
                return;
            }
            if (VRInput.IsNewPressed(Hand.Left, VRButtons.Y))
            {
                holdStart = seconds;
                holdDone = false;
            }
            if (holdStart >= 0d && !holdDone && seconds - holdStart >= UseHoldSeconds)
            {
                holdDone = true;
                before |= 1 << (int)Intent.Use;
                now &= ~(1 << (int)Intent.Use);
            }
        }

        /// <summary>Which control set the game is asking about, from the context it was given (a tool's context hangs under its control set).</summary>
        public static VRContext ContextOf(MyStringId gameContext)
        {
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_JETPACK))
                return VRContext.Jetpack;
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_CHARACTER))
                return VRContext.OnFoot;
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_SPACESHIP))
                return IsSeated(context) ? context : VRContext.None;
            return VRContext.None;
        }

        /// <summary>Which control set the local player is in, for the callers that ask without a context.</summary>
        public static VRContext EntityContext() => context;

        /// <summary>
        /// Which control set the controlled entity is in right now, for a prompt the game builds at the moment control
        /// changes hands. <see cref="Context"/> is worked out once a frame, after the game's update: a seat's "Press [F]
        /// to leave" is built as the player sits down, a frame before it, so it named the on-foot control (the right
        /// grip) while the seat's is holding Y. Never throws; falls back to the last frame's answer.
        /// </summary>
        public static VRContext CurrentContext()
        {
            VRContext last = context;
            if (ContextOverride.HasValue)
                return last;
            try
            {
                return Resolve(out _, out _, out _);
            }
            catch (Exception)
            {
                return last;
            }
        }

        /// <summary>The hands' answer to the game asking whether a control is in this state; false for controls they do not drive.</summary>
        public static bool Wants(MyStringId control, MyControlStateType type, VRContext asked)
        {
            // Back is the one answer that is not about a control set: the screens ask it of the GUI's, which has no hands context.
            if (control == MyControlsGUI.CANCEL)
                return WantsBack(type);
            if (asked == VRContext.None || !Intents.TryGetValue(control, out Intent intent))
                return false;
            // The gameplay pass run for the wrist panel (WalkBesidePanel) is told about locomotion only.
            if (panelPass && intent != Intent.Jump && intent != Intent.Crouch && intent != Intent.Sprint)
                return false;
            if (intent == Intent.Sprint && asked != VRContext.OnFoot)
                return false;
            // A seat's buttons are not a character's, and the other way round; only the pause button is everywhere.
            if (intent != Intent.MainMenu && IsSeated(asked) != IsSeated(context))
                return false;
            return InState(1 << (int)intent, type);
        }

        /// <summary>The game asking a menu's cancel question (MyControlsGUI.CANCEL): yes for the edges of a Back press that began in this menu.</summary>
        private static bool WantsBack(MyControlStateType type)
        {
            bool answer = InState(1 << (int)Intent.Back, type);
            if (answer && (type == MyControlStateType.NEW_PRESSED || type == MyControlStateType.NEW_RELEASED))
            {
                backAnswered |= 1 << (int)type;
                backAnsweredIn = menuNow?.GetType().Name;
            }
            return answer;
        }

        /// <summary>Whether the intent's bit is in this state this frame: held, newly held, or newly let go.</summary>
        private static bool InState(int bit, MyControlStateType type)
        {
            bool held = (now & bit) != 0, wasHeld = (before & bit) != 0;
            switch (type)
            {
                case MyControlStateType.NEW_PRESSED:
                case MyControlStateType.NEW_PRESSED_REPEATING:
                    return held && !wasHeld;
                case MyControlStateType.PRESSED:
                    return held;
                case MyControlStateType.NEW_RELEASED:
                    return !held && wasHeld;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Adds the left stick to the game's movement input (MyInputExtensions.GetPositionDelta: x right, y up, z back,
        /// as the keys give it). Walking goes where the head looks: the stick is turned by the head's yaw from the body,
        /// and the body turns to follow while it walks (<see cref="BodyFollow"/>). On the jetpack the right stick and A
        /// add the climb; on foot jump and crouch are buttons only. In a ship controller the left stick is the move
        /// indicator the thrusters and the wheels read (forward and back, strafe, steer), A is up and B down.
        /// </summary>
        public static void AddMovement(ref Vector3 delta)
        {
            if (context == VRContext.Ship)
            {
                if (seatMove != Vector3.Zero)
                {
                    delta = Clamp(delta + seatMove);
                    moveGiven = Math.Max(moveGiven, Math.Min(seatMove.Length(), 1f));
                }
                return;
            }
            if (context == VRContext.OnFoot || context == VRContext.Jetpack)
                moveGiven = Math.Max(moveGiven, Math.Min(Math.Max(leftStick.Length(), context == VRContext.Jetpack
                    ? Math.Abs(MathHelper.Clamp(rightStick.Y + (jumpButton ? 1f : 0f), -1f, 1f)) : 0f), 1f));
            MyCharacter character = ControlledCharacter();
            if (character == null || (leftStick == Vector2.Zero && rightStick == Vector2.Zero && !jumpButton))
                return;
            bool jetpack = character.JetpackRunning;

            float yaw = HeadYaw(character);
            float cos = (float)Math.Cos(yaw), sin = (float)Math.Sin(yaw);
            Vector3 add = new Vector3(
                leftStick.X * cos - leftStick.Y * sin,
                jetpack ? MathHelper.Clamp(rightStick.Y + (jumpButton ? 1f : 0f), -1f, 1f) : 0f,
                -(leftStick.X * sin + leftStick.Y * cos));
            delta = Clamp(delta + add);
        }

        /// <summary>
        /// Adds to the game's rotation input (MyInputExtensions.GetRotation: x pitch, y yaw, + right). In a seat the
        /// right stick adds a rate, as a pad's does. On foot the snap turn: the character turns by
        /// -y * RotationSpeed * 0.02 radians (BodyFollow works from the same figure), so the input for one turn is the
        /// angle over that. The same value comes back for every call in the frame, as the mouse's does.
        /// </summary>
        public static void AddRotation(ref Vector2 rotation)
        {
            if (IsSeated(context))
            {
                rotation += seatRotation;
                turnGiven = Math.Max(turnGiven, Math.Min(seatRotation.Length() / 20f, 1f)); // the ship controller takes the indicator / 20
                return;
            }
            if (snap == 0f)
                return;
            snapGiven = true;
            MyCharacter character = ControlledCharacter();
            if (character == null)
                return;
            float perUnit = character.RotationSpeed * 0.02f;
            if (perUnit > 0f)
                rotation.Y += snap / perUnit;
        }

        /// <summary>Adds the grips to the game's roll input (MyInputExtensions.GetRoll, + right) in a ship controller.</summary>
        public static void AddRoll(ref float roll)
        {
            if (context != VRContext.Ship)
                return;
            roll += seatRoll;
            turnGiven = Math.Max(turnGiven, Math.Min(Math.Abs(seatRoll) * 0.2f, 1f)); // and rolls at the indicator * 0.2
        }

        private static void Set(Intent intent, bool held)
        {
            if (held)
                now |= 1 << (int)intent;
        }

        private static Vector3 Clamp(Vector3 v) => new Vector3(
            MathHelper.Clamp(v.X, -1f, 1f), MathHelper.Clamp(v.Y, -1f, 1f), MathHelper.Clamp(v.Z, -1f, 1f));

        /// <summary>How far a grip is squeezed, 0 to 1; a source with only the button says all or nothing.</summary>
        private static float Squeeze(Hand hand)
        {
            if (HandFlight.Holds(hand))
                return 0f;
            float squeeze = VRInput.Get(hand).Squeeze;
            return squeeze > 0f ? squeeze : VRInput.IsPressed(hand, VRButtons.Grip) ? 1f : 0f;
        }

        /// <summary>The head's yaw from the body this frame, + left; zero when the head is not tracked or the view is not first person.</summary>
        private static float HeadYaw(MyCharacter character)
        {
            if (!character.IsInFirstPersonView && !character.ForceFirstPersonCamera)
                return 0f;
            return GameHead.Take() ? GameHead.Yaw : 0f;
        }

        /// <summary>The local player's character, when it is the one being controlled and the camera is not a free spectator.</summary>
        private static MyCharacter ControlledCharacter()
        {
            MySession session = MySession.Static;
            MyCharacter character = session?.ControlledEntity as MyCharacter;
            if (character == null || character != session.LocalCharacter || character.IsDead || session.IsCameraUserControlledSpectator())
                return null;
            return character;
        }

        /// <summary>
        /// What the local player controls: the character (on foot or flying), a ship controller (cockpit, remote
        /// control, a rover's seat), or a turret and the like, which the game gives the ship controls' context too.
        /// <paramref name="sticks"/> is false for a seat that cannot fly the ship. <paramref name="camera"/> is true while the view is a camera block's; the controlled entity stays the same
        /// then, and the game's USE puts the view back.
        /// </summary>
        private static VRContext Resolve(out bool camera, out bool sticks, out string detail)
        {
            detail = null;
            camera = false;
            sticks = true;
            if (ContextOverride.HasValue)
            {
                camera = CameraViewOverride;
                sticks = !SeatCannotFlyOverride;
                return ContextOverride.Value;
            }
            MySession session = MySession.Static;
            var entity = session?.ControlledEntity;
            if (session == null)
            {
                detail = "no world";
                return VRContext.None;
            }
            if (entity == null)
            {
                detail = "nothing controlled";
                return VRContext.None;
            }
            if (session.IsCameraUserControlledSpectator())
            {
                // The game moves the spectator camera with the keys; the hands have nothing to drive while it is the view.
                detail = "the spectator camera is the view";
                return VRContext.None;
            }
            VRContext result;
            if (entity is MyCharacter character)
            {
                result = character != session.LocalCharacter || character.IsDead ? VRContext.None : character.JetpackRunning ? VRContext.Jetpack : VRContext.OnFoot;
                if (result == VRContext.None)
                    detail = character.IsDead ? "the character is dead" : "not the player's own character";
            }
            else if (entity is MyShipController)
                result = VRContext.Ship;
            else if (MyControllerHelper.HasContext(entity.ControlContext, MyControllerHelper.CX_SPACESHIP))
                result = VRContext.Turret;
            else
            {
                result = VRContext.None;
                detail = "controlling " + entity.GetType().Name;
            }
            if (result == VRContext.Ship || result == VRContext.Turret)
            {
                string name = entity.GetType().Name;
                detail = name.StartsWith("My") ? name.Substring(2) : name;
                sticks = !entity.PrimaryLookaround;
            }
            if (result != VRContext.None)
                camera = session.CameraController is MyCameraBlock { IsActive: true };
            return result;
        }

        /// <summary>Logs the control set once each time it changes.</summary>
        private static void LogContext(string detail)
        {
            string label;
            switch (context)
            {
                case VRContext.OnFoot: label = "on foot"; break;
                case VRContext.Jetpack: label = "jetpack"; break;
                case VRContext.Ship: label = detail != null ? $"ship controller ({detail})" : "ship controller"; break;
                case VRContext.Turret: label = detail != null ? $"turret ({detail})" : "turret"; break;
                default: label = detail != null ? $"none ({detail})" : "none"; break;
            }
            if (cameraView)
                label = "camera over " + label;
            if (label == loggedContext)
                return;
            loggedContext = label;
            Log.Info("VR controls: " + label);
        }

        /// <summary>Takes out the middle of a stick, where a controller rests off zero, and scales the rest back to the full range.</summary>
        private static Vector2 Dead(Vector2 stick)
        {
            float deadzone = VRSettings.StickDeadzone;
            float length = stick.Length();
            if (length <= deadzone)
                return Vector2.Zero;
            return stick * ((Math.Min(length, 1f) - deadzone) / ((1f - deadzone) * length));
        }
    }
}
