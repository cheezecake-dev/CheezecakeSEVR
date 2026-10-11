using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sandbox;
using Sandbox.Engine.Utils;
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

        /// <summary>
        /// The free spectator camera is the view (F8, MySpectatorCameraController moved by the player: UserControlled, or
        /// FreeMouse); the character stays the controlled entity, and the game asks the spectator's own questions in CX_SPECTATOR.
        /// </summary>
        Spectator,
    }

    /// <summary>
    /// What the hands do to the game: the default controls table in docs/VR_PLAN.md, turned into the game's own
    /// controls, for the character on foot and on the jetpack and for the seat of a ship, a rover, a remote control, a
    /// turret or a camera block. The analog parts (walking, flying, steering, the snap turn) are added to the game's
    /// movement, rotation and roll input; the buttons answer the game's questions about its controls. Everything is
    /// added to what the keyboard, mouse and a gamepad give, never instead of it. With a block in hand on foot or on the jetpack the
    /// right grip (with the right stick, A and B) and X build (<see cref="Hands.BuildMode"/>, which also has a paste and the voxel hand), and with the colour
    /// tool the triggers, A, B and the right stick's flick paint (<see cref="Hands.PaintMode"/>); the game's radial menu is opened and steered by
    /// <see cref="ToolbarWheel"/>, which has the hands while it is up. On the jetpack in zero g the left stick click held makes
    /// the right stick pitch and roll (<see cref="UpdateZeroG"/>); the free spectator camera is flown by the same sticks
    /// (<see cref="UpdateSpectator"/>).
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

            /// <summary>A hand weapon reloaded by the hands (<see cref="Hands.Reload"/>): the game's RELOAD, R on the keyboard.</summary>
            Reload,

            /// <summary>B or Menu pressed while a menu has focus: the game's GUI CANCEL (MyControlsGUI.CANCEL), which is not a gameplay control and has no entry in <see cref="Intents"/>.</summary>
            Back,

            /// <summary>The left grip held at the helmet's mouth (<see cref="Hands.VoiceTalk"/>): the game's push to talk, VOICE_CHAT, for as long as it is held.</summary>
            Voice,

            // Build mode (Hands.BuildMode): one frame each per step or tap. MoveCloser and MoveFurther are also the paste's
            // and the voxel hand's distance (VOXEL_CLOSER, VOXEL_FURTHER).
            MoveCloser,
            MoveFurther,
            UseSymmetry,
            SymmetrySwitch,

            /// <summary>Block in hand: the right stick click switches between the large and small block (CUBE_BUILDER_CUBESIZE_MODE).</summary>
            CubeSize,

            /// <summary>Block in hand: the right grip with A steps to the block's next stage, its next variant (NEXT_BLOCK_STAGE, the mouse wheel).</summary>
            NextStage,

            /// <summary>Block in hand: the right grip with B steps to the previous stage (PREV_BLOCK_STAGE).</summary>
            PrevStage,

            // The colour tool in hand (Hands.PaintMode).

            /// <summary>The right trigger paints, as the brush chosen at the pull (<see cref="Hands.PaintMode.BrushControl"/>): RECOLOR, MEDIUM_COLOR_BRUSH, LARGE_COLOR_BRUSH or RECOLOR_WHOLE_GRID.</summary>
            Paint,

            /// <summary>The left trigger takes the colour and skin of the block pointed at (COPY_COLOR).</summary>
            CopyColor,

            /// <summary>A flick of the right stick to the right: the next colour (CYCLE_COLOR_RIGHT), or with the right grip held the next skin (CYCLE_SKIN_RIGHT).</summary>
            CycleNext,

            /// <summary>To the left: the previous colour or skin (CYCLE_COLOR_LEFT, CYCLE_SKIN_LEFT).</summary>
            CyclePrev,

            // Looking through a camera block (CameraViewStick): the previous and next camera, one frame per flick.
            SwitchLeft,
            SwitchRight,

            // A camera block's, a turret's or a searchlight's zoom, held (Zoom).
            ZoomIn,
            ZoomOut,

            /// <summary>The free spectator camera's speed boost (SPECTATOR_SPEED_BOOST, Shift on the keyboard): the left stick click (<see cref="UpdateSpectator"/>).</summary>
            SpectatorBoost,
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
            [MyControlsSpace.MOVE_CLOSER] = Intent.MoveCloser,
            [MyControlsSpace.MOVE_FURTHER] = Intent.MoveFurther,
            [MyControlsSpace.USE_SYMMETRY] = Intent.UseSymmetry,
            [MyControlsSpace.SYMMETRY_SWITCH] = Intent.SymmetrySwitch,
            [MyControlsSpace.RELOAD] = Intent.Reload,
            [MyControlsSpace.VOICE_CHAT] = Intent.Voice,
            [MyControlsSpace.VOXEL_CLOSER] = Intent.MoveCloser,
            [MyControlsSpace.VOXEL_FURTHER] = Intent.MoveFurther,
            [MyControlsSpace.CUBE_BUILDER_CUBESIZE_MODE] = Intent.CubeSize,
            [MyControlsSpace.NEXT_BLOCK_STAGE] = Intent.NextStage,
            [MyControlsSpace.PREV_BLOCK_STAGE] = Intent.PrevStage,
            [MyControlsSpace.RECOLOR] = Intent.Paint,
            [MyControlsSpace.MEDIUM_COLOR_BRUSH] = Intent.Paint,
            [MyControlsSpace.LARGE_COLOR_BRUSH] = Intent.Paint,
            [MyControlsSpace.RECOLOR_WHOLE_GRID] = Intent.Paint,
            [MyControlsSpace.COPY_COLOR] = Intent.CopyColor,
            [MyControlsSpace.CYCLE_COLOR_RIGHT] = Intent.CycleNext,
            [MyControlsSpace.CYCLE_SKIN_RIGHT] = Intent.CycleNext,
            [MyControlsSpace.CYCLE_COLOR_LEFT] = Intent.CyclePrev,
            [MyControlsSpace.CYCLE_SKIN_LEFT] = Intent.CyclePrev,
            [MyControlsSpace.SWITCH_LEFT] = Intent.SwitchLeft,
            [MyControlsSpace.SWITCH_RIGHT] = Intent.SwitchRight,
            [MyControlsSpace.CAMERA_ZOOM_IN] = Intent.ZoomIn,
            [MyControlsSpace.CAMERA_ZOOM_OUT] = Intent.ZoomOut,
            [MyControlsSpace.SPECTATOR_SPEED_BOOST] = Intent.SpectatorBoost,
        };

        // A trigger pulled on a radial menu that is not the toolbar wheel (the actions wheel, which is a menu with the focus
        // and so does not own the hands): it is not a shot, a use or a thrust, however long it stays down after the wheel has
        // closed; it counts again only after it has been let go (SwallowTrigger).
        private static readonly bool[] triggerSwallowed = new bool[2];

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

        // Looking through a camera block the right stick is the camera's (CameraViewStick): the flick to the next camera
        // re-arms once the stick is back near the middle, as the snap turn's does, and up or down is a zoom held with the
        // stick directions' own give. The zoom held this frame, from the stick or a turret's A and B, is logged as it
        // changes (Zoom), under the name of whatever held it.
        private static bool cameraFlickArmed = true;
        private static bool stickZoomIn, stickZoomOut;
        private static bool zoomAsked, zoomInMenu;
        private static int loggedZoom;
        private static string zoomBy;

        // What the hands gave the game's movement and turning this frame, and last frame's: what Motion reports, so it
        // holds still while the render thread reads it.
        private static float moveGiven, turnGiven, moveShown, turnShown;
        private static bool snapGiven;
        private static int snapCount;

        // Zero-g pitch and roll (UpdateZeroG): whether the left stick click holds it, this frame's turn (radians: pitch + nose
        // up, roll + right), how hard the stick is pushed (for the vignette) and whether it was a snap, the flick's arming for
        // Snap, the right stick kept from the game after the click is let go until it is back in the middle, a press refused
        // outside zero g (logged once), and the smooth turn's direction last logged.
        private static bool zeroGHeld, zeroGArmed, zeroGLatch, zeroGRefused, zeroGSnapped;
        private static float zeroGPitch, zeroGRoll, zeroGLevel;
        private static int zeroGPitchLogged, zeroGRollLogged;

        /// <summary>Gravity at or below this (m/s², squared) is zero g: the game's own line, below which it does not stand the body up (MyCharacter.UpdateStandup).</summary>
        private const float ZeroGSquared = 0.1f;

        /// <summary>The jetpack rolls the body by the roll input times this, radians (MyCharacterJetpackComponent.MoveAndRotate).</summary>
        private const float JetpackRollPerUnit = 0.02f;

        // The free spectator camera (UpdateSpectator): A minus B for up and down, and the speed step held on the right stick.
        private static float spectatorUp;
        private static int speedStepDirection;
        private static double speedStepNext;

        /// <summary>
        /// A flick of the right stick up or down steps the spectator's speed by the game's own step (Shift and the mouse wheel:
        /// x1.5); held, it steps again <see cref="SpeedFirstRepeatSeconds"/> after the flick, then every <see cref="SpeedRepeatSeconds"/>.
        /// </summary>
        private const double SpeedFirstRepeatSeconds = 0.5, SpeedRepeatSeconds = 0.25;

        /// <summary>The spectator's speed step and its limits, the game's (MySpectatorCameraController.MoveAndRotate).</summary>
        private const float SpeedStep = 1.5f, SpeedMin = 0.0001f, SpeedMax = 8000f;

        /// <summary>The spectator moves the move input times its speed times 1.6667 m a frame, 60 frames a second (MoveAndRotate_UserControlled); without the boost, 0.35 of that.</summary>
        private const float SpectatorMetresPerSecond = 1.6666667f * 60f, SpectatorUnboosted = 0.35f;

        /// <summary>The spectator camera turns by the rotation input times this times its angular speed, radians (MoveAndRotate_UserControlled).</summary>
        private const float SpectatorTurnPerUnit = 0.0025f;

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

        /// <summary>
        /// Whether the hands may have an answer for this control: something is held or was let go of (<see cref="Live"/>),
        /// or it is one of the terminal's amount modifiers, which are read from the off hand's grip and trigger and are no intent.
        /// </summary>
        public static bool MayAnswer(MyStringId control) =>
            Live || control == MyControlsGUI.SHIFT_LEFT || control == MyControlsGUI.SHIFT_RIGHT;

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

        /// <summary>The hands pitch or roll the local character on the jetpack this frame (<see cref="UpdateZeroG"/>), for <see cref="Tracking.ZeroGPivot"/>.</summary>
        internal static bool ZeroGTurning => zeroGPitch != 0f || zeroGRoll != 0f;

        /// <summary>
        /// The left stick click holds zero-g pitch and roll (<see cref="UpdateZeroG"/>'s test, asked as it stands now, so it holds
        /// before the zero-g pass has run this frame): the colour tool's flick (<see cref="Hands.PaintMode"/>) leaves the right stick
        /// to the roll while it does.
        /// </summary>
        internal static bool ZeroGHeld => ZeroGClick() && CanPitchAndRoll(out _);

        /// <summary>The left stick click is down on the jetpack, outside a camera block's view, with zero-g pitch and roll not Off.</summary>
        private static bool ZeroGClick() =>
            context == VRContext.Jetpack && !cameraView && VRSettings.ZeroGTurn != VRSettings.ZeroGTurnOff
            && VRInput.IsPressed(Hand.Left, VRButtons.StickClick);

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
            zeroGPitch = zeroGRoll = zeroGLevel = 0f;
            zeroGSnapped = false;
            spectatorUp = 0f;
            if (!VRInput.Active || Off)
            {
                Hands.BuildMode.Frame(VRContext.None, true, seconds);
                Hands.PaintMode.Frame(VRContext.None, true);
                context = VRContext.None;
                cameraView = seatSticks = false;
                ControlGrip.Frame(false);
                HandFlight.Frame(false);
                stickUp = stickDown = false;
                flickArmed = true;
                cameraFlickArmed = true;
                stickZoomIn = stickZoomOut = false;
                loggedZoom = 0;
                zeroGHeld = zeroGLatch = zeroGRefused = false;
                zeroGPitchLogged = zeroGRollLogged = 0;
                speedStepDirection = 0;
                holdStart = -1d;
                holdDone = false;
                backB = backMenu = false;
                backScreen = menuNow = null;
                backAnswered = 0;
                menuDown = -1d;
                menuRecentred = menuTapped = false;
                menuBeganIn = menuBackIn = null;
                panelWalk = false;
                triggerSwallowed[0] = triggerSwallowed[1] = false;
                ControlHints.Update(false);
                ToolbarWheel.Reset();
                return;
            }

            context = Resolve(out cameraView, out seatSticks, out string detail);
            LogContext(detail);
            VRPrompts.Frame(context);
            ControlGrip.Frame(context == VRContext.Ship && seatSticks);
            HandFlight.Frame(context == VRContext.Ship && seatSticks);

            leftStick = Dead(VRInput.Get(Hand.Left).Stick);
            rightStick = Dead(VRInput.Get(Hand.Right).Stick);

            ApplyWheel(seconds);
            // With a block in hand (or a paste, or the voxel hand) the right stick is building's while the right grip is held for
            // it: a turn sideways, the distance up and down; with the colour tool a sideways flick of the right stick picks the
            // colour, so it does not turn.
            object menu = MenuScreen();
            Hands.BuildMode.Frame(context, ToolbarWheel.Owns || menu != null, seconds);
            Hands.PaintMode.Frame(context, ToolbarWheel.Owns || menu != null);
            if (Hands.BuildMode.StickBusy || Hands.PaintMode.TakesStick(rightStick))
                rightStick = Vector2.Zero;
            zoomAsked = false;
            zoomInMenu = menu != null;
            if (!ToolbarWheel.Owns)
            {
                bool cameraStick = CameraViewStick(menu != null, out float stickX);
                if (IsSeated(context))
                    UpdateSeated();
                else
                {
                    UpdateZeroG();
                    UpdateWalking();
                    if (context == VRContext.Spectator)
                        UpdateSpectator(seconds);
                    else
                        speedStepDirection = 0;
                }
                // The camera had the stick: the snap turn re-arms only once it is back near the middle, so putting the
                // view back with the stick still out to the side does not turn the body.
                if (cameraStick)
                    flickArmed = Math.Abs(stickX) < FlickRelease;
            }
            if (!zoomAsked)
                Zoom(false, false, zoomBy);
            UpdateUseHold(seconds);
            // Push to talk holds in every context, the toolbar wheel's too (the wheel clears the hands' other controls above).
            Set(Intent.Voice, Hands.VoiceTalk.Frame());

            menuNow = menu;
            MenuButton(seconds, menuNow);
            panelWalk = WalksBesidePanel(menuNow, context, ToolbarWheel.Owns);
            UpdateBack(menuNow);
            ControlHints.Update(menuNow != null && !ToolbarWheel.Owns && !(menuNow is Gui.ActionsWheelScreen),
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
                case VRContext.Spectator: return "spectator camera";
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

            // With a block in hand X is symmetry (Hands.BuildMode) instead of the dampers, and A and B stay jump (the jetpack's
            // climb) and the jetpack, except a press made with the right grip held, which steps the block's stage. A paste and
            // the voxel hand leave A, B and X as they are; the colour tool takes A and B for its brush (Hands.PaintMode).
            bool build = Hands.BuildMode.Active;
            // A hand weapon's left trigger is only its iron sight, which in VR is the gun brought up to the eye (Hands.IronSight).
            if (Hands.IronSight.Frame())
                SwallowTrigger(Hand.Left, "a hand weapon's left trigger (its iron sight)");
            bool paint = Hands.PaintMode.Active;
            bool place = TriggerHeld(Hand.Right), remove = TriggerHeld(Hand.Left);
            jumpButton = !paint && !Hands.BuildMode.TakesA && VRInput.IsPressed(Hand.Right, VRButtons.A);
            // With the colour tool the triggers paint and take a colour, as the game's gamepad has them, not primary and secondary.
            Set(Intent.Primary, place && !paint);
            Set(Intent.Secondary, remove && !paint);
            if (build)
                Hands.BuildMode.Triggers(place, remove, lastSeconds);
            if (paint)
                Hands.PaintMode.Triggers(place, remove);
            Set(Intent.Paint, Hands.PaintMode.Paint);
            Set(Intent.CopyColor, Hands.PaintMode.CopyColor);
            Set(Intent.CycleNext, Hands.PaintMode.CycleNext);
            Set(Intent.CyclePrev, Hands.PaintMode.CyclePrev);
            Set(Intent.Use, VRInput.IsPressed(Hand.Right, VRButtons.Grip) && Hands.GripClaim.Owner(Hand.Right) == null);
            Set(Intent.Jump, jumpButton || stickUp);
            Set(Intent.Crouch, stickDown);
            Set(Intent.Thrusts, !paint && !Hands.BuildMode.TakesB && VRInput.IsPressed(Hand.Right, VRButtons.B));
            Set(Intent.Damping, !build && VRInput.IsPressed(Hand.Left, VRButtons.X));
            Set(Intent.Sprint, VRInput.IsPressed(Hand.Left, VRButtons.StickClick));
            Set(Intent.MoveCloser, Hands.BuildMode.Closer);
            Set(Intent.MoveFurther, Hands.BuildMode.Further);
            Set(Intent.UseSymmetry, Hands.BuildMode.SymmetryTap);
            Set(Intent.SymmetrySwitch, Hands.BuildMode.SymmetryCycle);
            Set(Intent.CubeSize, Hands.BuildMode.CubeSize);
            Set(Intent.NextStage, Hands.BuildMode.NextStage);
            Set(Intent.PrevStage, Hands.BuildMode.PrevStage);
            Set(Intent.Reload, Hands.Reload.Wanted());
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
        /// Zero-g pitch and roll, before the walking controls read the right stick. On the jetpack in zero g, with the left
        /// stick click held, the right stick pitches the body (up and down: the nose up for up, or down with the game's "invert
        /// Y" for the character's pad) and rolls it (sideways, + right), one at a time: only the direction the stick mostly
        /// points in counts, as for the smooth turn. Smooth (the setting's default) turns at the Smooth turn speed, Snap one Snap
        /// turn angle a flick. It is the game's own jetpack turn (MyCharacterJetpackComponent.MoveAndRotate), from the same
        /// rotation and roll inputs a pad's right stick and LB give it (<see cref="AddRotation"/>, <see cref="AddRoll"/>), made
        /// about the eyes (<see cref="Tracking.ZeroGPivot"/>). While the click is held the right stick does nothing else (no yaw,
        /// no climb), and after it is let go the right stick stays off until it has been back near the middle, so a roll does not
        /// run on as a turn or a climb. In gravity, with the setting Off, or in a camera block's view, the click does nothing.
        /// </summary>
        private static void UpdateZeroG()
        {
            int mode = VRSettings.ZeroGTurn;
            bool click = ZeroGClick();
            string why = null;
            bool held = click && CanPitchAndRoll(out why);
            if (click && !held && !zeroGRefused)
                Log.Info($"Zero-g pitch and roll: not here ({why}); the left stick click does nothing on the jetpack");
            zeroGRefused = click && !held;
            if (held != zeroGHeld)
            {
                zeroGHeld = held;
                zeroGPitchLogged = zeroGRollLogged = 0;
                if (held)
                {
                    // A stick already pushed as the click goes down has to come back to the middle before it snaps.
                    zeroGArmed = rightStick.Length() < FlickRelease;
                    Log.Info(mode == VRSettings.ZeroGTurnSnap
                        ? $"Zero-g pitch and roll: left stick click held, the right stick pitches and rolls {VRSettings.SnapTurnDegrees:F0} deg a flick"
                        : $"Zero-g pitch and roll: left stick click held, the right stick pitches and rolls at up to {VRSettings.SmoothTurnSpeedDegrees:F0} deg/s");
                }
                else
                {
                    zeroGLatch = true;
                    Log.Info("Zero-g pitch and roll: let go");
                }
            }
            if (!held)
            {
                if (zeroGLatch && rightStick.Length() < StickRelease)
                    zeroGLatch = false;
                if (zeroGLatch)
                    rightStick = Vector2.Zero;
                return;
            }

            Vector2 stick = rightStick;
            rightStick = Vector2.Zero;
            float up = InvertCharacterY() ? -stick.Y : stick.Y;
            if (mode == VRSettings.ZeroGTurnSnap)
            {
                if (!zeroGArmed)
                {
                    zeroGArmed = stick.Length() < FlickRelease;
                    return;
                }
                if (Math.Max(Math.Abs(stick.X), Math.Abs(stick.Y)) <= FlickPress)
                    return;
                float angle = MathHelper.ToRadians(VRSettings.SnapTurnDegrees);
                zeroGArmed = false;
                zeroGSnapped = true;
                if (Math.Abs(stick.X) > Math.Abs(stick.Y))
                {
                    zeroGRoll = stick.X > 0f ? angle : -angle;
                    Log.Info($"Zero-g snap roll {(zeroGRoll > 0f ? "right" : "left")} {VRSettings.SnapTurnDegrees:F0} deg");
                }
                else
                {
                    zeroGPitch = up > 0f ? angle : -angle;
                    Log.Info($"Zero-g snap pitch {(zeroGPitch > 0f ? "up" : "down")} {VRSettings.SnapTurnDegrees:F0} deg");
                }
                return;
            }
            float speed = VRSettings.SmoothTurnSpeedDegrees;
            zeroGRoll = SmoothTurnRadians(stick, speed, frameSeconds);
            zeroGPitch = SmoothTurnRadians(new Vector2(up, stick.X), speed, frameSeconds);
            zeroGLevel = Math.Abs(stick.X) > Math.Abs(stick.Y) ? Math.Abs(stick.X) : Math.Abs(stick.Y);
            LogZeroGSmooth(ref zeroGPitchLogged, Math.Sign(zeroGPitch), "pitch", "up", "down", speed);
            LogZeroGSmooth(ref zeroGRollLogged, Math.Sign(zeroGRoll), "roll", "right", "left", speed);
        }

        private static void LogZeroGSmooth(ref int logged, int direction, string what, string positive, string negative, float speed)
        {
            if (direction == logged)
                return;
            logged = direction;
            Log.Info(direction == 0 ? $"Zero-g smooth {what} stopped" : $"Zero-g smooth {what} {(direction > 0 ? positive : negative)} at up to {speed:F0} deg/s");
        }

        /// <summary>The local character can be pitched and rolled by its jetpack here; when not, why not, for the log.</summary>
        private static bool CanPitchAndRoll(out string why)
        {
            why = null;
            MyCharacter character = ControlledCharacter();
            if (character == null || !character.JetpackRunning)
            {
                why = "the jetpack is off";
                return false;
            }
            if (character.Definition != null && character.Definition.VerticalPositionFlyingOnly)
            {
                why = "this character flies upright only";
                return false;
            }
            float gravity = character.Gravity.LengthSquared();
            if (gravity > ZeroGSquared)
            {
                why = $"gravity {Math.Sqrt(gravity):0.00} m/s2";
                return false;
            }
            return true;
        }

        /// <summary>The game's "invert Y" for the character on a pad: pushing the right stick up looks, and here pitches, down.</summary>
        private static bool InvertCharacterY()
        {
            try
            {
                return MyInput.Static?.GetJoystickYInversionCharacter() ?? false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The free spectator camera (F8; F6 goes back to the character), after the walking controls: the left stick flies it
        /// where the head looks, level (<see cref="AddMovement"/>), A up and B down along its own up, the right stick turns it by
        /// the Snap turn or Smooth turn setting (<see cref="AddRotation"/>), the left stick click is the game's speed boost
        /// (SPECTATOR_SPEED_BOOST, Shift on the keyboard), and a flick of the right stick up or down steps its speed by the game's
        /// own step, x1.5 (Shift and the mouse wheel), and again while held (<see cref="SpeedFirstRepeatSeconds"/>). The character's
        /// jump, crouch, jetpack, dampers and sprint, which the game skips while the spectator is the view, are not asked for.
        /// </summary>
        private static void UpdateSpectator(double seconds)
        {
            now &= ~((1 << (int)Intent.Jump) | (1 << (int)Intent.Crouch) | (1 << (int)Intent.Thrusts)
                     | (1 << (int)Intent.Damping) | (1 << (int)Intent.Sprint));
            spectatorUp = (VRInput.IsPressed(Hand.Right, VRButtons.A) ? 1f : 0f) - (VRInput.IsPressed(Hand.Right, VRButtons.B) ? 1f : 0f);
            Set(Intent.SpectatorBoost, VRInput.IsPressed(Hand.Left, VRButtons.StickClick));

            int direction = stickUp ? 1 : stickDown ? -1 : 0;
            if (direction == 0 || (direction == speedStepDirection && seconds < speedStepNext))
            {
                if (direction == 0)
                    speedStepDirection = 0;
                return;
            }
            speedStepNext = seconds + (direction == speedStepDirection ? SpeedRepeatSeconds : SpeedFirstRepeatSeconds);
            speedStepDirection = direction;
            MySpectatorCameraController spectator = MySpectatorCameraController.Static;
            if (spectator == null)
                return;
            float speed = direction > 0
                ? Math.Min(spectator.SpeedModeLinear * SpeedStep, SpeedMax)
                : Math.Max(spectator.SpeedModeLinear / SpeedStep, SpeedMin);
            spectator.SpeedModeLinear = speed;
            Log.Info($"Spectator speed {(direction > 0 ? "up" : "down")} to {speed:0.####}: {speed * SpectatorMetresPerSecond * SpectatorUnboosted:0.##} m/s, {speed * SpectatorMetresPerSecond:0.##} m/s with the boost");
        }

        /// <summary>
        /// In a seat the right stick turns at a rate, as a pad's does, and no stick direction is a button or a snap
        /// turn. A ship controller also takes the left stick, the grips (roll) and the buttons; a turret only fires and
        /// zooms (A in, B out).
        /// A seat that does not fly the ship (a passenger seat: the game then uses the stick to turn the view in the
        /// cabin, which the head does here) gets the buttons but no sticks or grips.
        /// </summary>
        private static void UpdateSeated()
        {
            stickUp = stickDown = false;
            // The flick re-arms once the stick has been back in the middle, so leaving a seat with it pushed does not snap-turn.
            flickArmed = Math.Abs(rightStick.X) < FlickRelease;

            // A cockpit screen out of reach takes the dominant trigger as a tap before the game sees it (Gui.TouchToolbar).
            Gui.TouchToolbar.SeatedInput();
            GamepadFeel feel = GamepadFeel.Current();
            float sensitivity = VRSettings.ShipStickSensitivity;
            bool fire = TriggerHeld(Hand.Right);
            if (seatSticks)
                seatRotation = ShipStick.Rotation(rightStick, feel, sensitivity);
            if (context != VRContext.Ship)
            {
                Set(Intent.Primary, fire);
                // A turret, a searchlight, or a turret control block through its camera: A zooms in and B out while
                // held, as a pad's zoom buttons; the zoomed picture is the inset (Rendering.ZoomInset). Not with a menu open,
                // where B is Back (UpdateBack).
                Zoom(!zoomInMenu && VRInput.IsPressed(Hand.Right, VRButtons.A), !zoomInMenu && VRInput.IsPressed(Hand.Right, VRButtons.B), "Turret: A / B");
                return;
            }

            bool secondary = TriggerHeld(Hand.Left);
            bool up = VRInput.IsPressed(Hand.Right, VRButtons.A), down = VRInput.IsPressed(Hand.Right, VRButtons.B);
            if (seatSticks)
            {
                seatMove = ShipStick.Move(leftStick, up, down, feel);
                // Holding the controls, the grips are the hold and the stick hand's sideways tilt is the roll.
                seatRoll = ControlGrip.Gates
                    ? ControlGrip.Roll(feel, sensitivity)
                    : ShipStick.Roll(Squeeze(Hand.Left), Squeeze(Hand.Right), feel, sensitivity);
                HandFlight.Apply(ref seatRotation, ref seatMove, ref seatRoll, up, down, feel);
                ControlGrip.Gate(leftStick, rightStick, ref seatRotation, ref seatMove, ref seatRoll, ref fire, ref secondary, ref up, ref down);
            }
            Set(Intent.Primary, fire);
            Set(Intent.Secondary, secondary);
            Set(Intent.Jump, up);
            Set(Intent.Crouch, down);
            Set(Intent.Damping, VRInput.IsPressed(Hand.Left, VRButtons.X));
            Set(Intent.Lights, VRInput.IsPressed(Hand.Left, VRButtons.StickClick));
            Set(Intent.LandingGear, VRInput.IsPressed(Hand.Right, VRButtons.StickClick));
        }

        /// <summary>
        /// Looking through a camera block, from a seat or on foot, the right stick is the camera's. A sideways flick is the
        /// previous or the next camera, once per flick as a snap turn is: the game's SWITCH_LEFT and SWITCH_RIGHT, which it
        /// reads as new presses while a camera is the view (MyGridCameraSystem.Update:124, the [ and ] keys). Up and down zoom
        /// in and out while held: CAMERA_ZOOM_IN and OUT (:139, a pad's zoom). Only the direction the stick mostly points in
        /// counts, as on foot. The stick is then taken before the seat or the walk see it, so it neither turns the ship nor
        /// the body. A turret control block's camera is not taken: that view is aimed as a turret, and its zoom is on A and B
        /// (<see cref="UpdateSeated"/>). Nor is a stick a menu or the wheel has.
        /// </summary>
        /// <param name="menuOpen">A menu has the focus.</param>
        /// <param name="x">How far the stick is pushed sideways.</param>
        /// <returns>The stick was the camera's this frame.</returns>
        private static bool CameraViewStick(bool menuOpen, out float x)
        {
            x = rightStick.X;
            if (!cameraView || context == VRContext.Turret || menuOpen)
            {
                // Coming into a camera with the stick already out to the side does not switch at once.
                cameraFlickArmed = Math.Abs(x) < FlickRelease;
                stickZoomIn = stickZoomOut = false;
                return false;
            }

            float y = rightStick.Y;
            bool sideways = Math.Abs(x) > Math.Abs(y);
            if (cameraFlickArmed)
            {
                if (sideways && Math.Abs(x) > FlickPress)
                {
                    Set(x > 0f ? Intent.SwitchRight : Intent.SwitchLeft, true);
                    cameraFlickArmed = false;
                    Log.Info(x > 0f
                        ? "Camera view: right stick flicked right, the next camera (SWITCH_RIGHT)"
                        : "Camera view: right stick flicked left, the previous camera (SWITCH_LEFT)");
                }
            }
            else if (Math.Abs(x) < FlickRelease)
                cameraFlickArmed = true;

            stickZoomIn = !sideways && y > (stickZoomIn ? StickRelease : StickPress);
            stickZoomOut = !sideways && -y > (stickZoomOut ? StickRelease : StickPress);
            Zoom(stickZoomIn, stickZoomOut, "Camera view: right stick");
            rightStick = Vector2.Zero;
            return true;
        }

        /// <summary>
        /// The zoom held this frame: CAMERA_ZOOM_IN or CAMERA_ZOOM_OUT, which the game reads as held while a camera block, a
        /// turret or a searchlight is the view (MyGridCameraSystem.Update:139, MyLargeTurretBase.UpdateBeforeSimulationParallel
        /// :1717, MySearchlight:2548), narrowing or widening its field of view a step each update. Logged once each time it
        /// changes, under <paramref name="by"/>, what held it.
        /// </summary>
        private static void Zoom(bool zoomIn, bool zoomOut, string by)
        {
            zoomAsked = true;
            // Both at once ask for nothing (the game would take the zoom in).
            if (zoomIn && zoomOut)
                zoomIn = zoomOut = false;
            Set(Intent.ZoomIn, zoomIn);
            Set(Intent.ZoomOut, zoomOut);
            int zoom = zoomIn ? 1 : zoomOut ? -1 : 0;
            if (zoom == loggedZoom)
                return;
            loggedZoom = zoom;
            if (zoom != 0)
                zoomBy = by;
            Log.Info(zoom == 0
                ? $"{zoomBy ?? by}: zoom let go"
                : $"{by}: zoom {(zoom > 0 ? "in (CAMERA_ZOOM_IN)" : "out (CAMERA_ZOOM_OUT)")} held");
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
            // The spectator's own questions (its speed boost, its movement) are asked in its context while it is the view.
            if (MyControllerHelper.HasContext(gameContext, MyControllerHelper.CX_SPECTATOR))
                return context == VRContext.Spectator ? VRContext.Spectator : VRContext.None;
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
            // So are the amount modifiers, which the terminal asks of the GUI's set too.
            if (control == MyControlsGUI.SHIFT_LEFT || control == MyControlsGUI.SHIFT_RIGHT)
                return WantsAmount(control == MyControlsGUI.SHIFT_LEFT, type);
            if (asked == VRContext.None || !Intents.TryGetValue(control, out Intent intent))
                return false;
            // The gameplay pass run for the wrist panel (WalkBesidePanel) is told about locomotion only.
            if (panelPass && intent != Intent.Jump && intent != Intent.Crouch && intent != Intent.Sprint)
                return false;
            if (intent == Intent.Sprint && asked != VRContext.OnFoot)
                return false;
            // The colour tool's controls that share an intent: the brush chosen at the pull, the colour's or the skin's cycle.
            if (intent == Intent.Paint && control != Hands.PaintMode.BrushControl)
                return false;
            if ((intent == Intent.CycleNext || intent == Intent.CyclePrev)
                && (control == MyControlsSpace.CYCLE_SKIN_RIGHT || control == MyControlsSpace.CYCLE_SKIN_LEFT) != Hands.PaintMode.CyclesSkin)
                return false;
            // The spectator camera is told of its speed boost and the pause button only; the boost is the spectator's alone.
            if (asked == VRContext.Spectator ? intent != Intent.SpectatorBoost && intent != Intent.MainMenu : intent == Intent.SpectatorBoost)
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

        /// <summary>
        /// The terminal's amount modifiers, a pad's two bumpers (MyControlsGUI.SHIFT_LEFT and SHIFT_RIGHT), held on the hand that
        /// does not point the laser: its grip is SHIFT_LEFT, its trigger SHIFT_RIGHT. The inventory's move to the other side (A on
        /// an item: <see cref="Laser"/>, or a double click) then moves 10, 100, or with both 1000 instead of the whole stack, and a
        /// click on a blueprint queues 10, 100 or 1000. Only while the terminal has the focus: the G menu and the radial menus
        /// read the same two controls for other things.
        /// </summary>
        private static bool WantsAmount(bool shiftLeft, MyControlStateType type)
        {
            if (!(menuNow is MyGuiScreenTerminal) || !VRInput.Active)
                return false;
            Hand off = Hands.TwoHand.OffHand;
            VRButtons button = shiftLeft ? VRButtons.Grip : VRButtons.Trigger;
            bool answer;
            switch (type)
            {
                case MyControlStateType.NEW_PRESSED:
                case MyControlStateType.NEW_PRESSED_REPEATING:
                    answer = VRInput.IsNewPressed(off, button);
                    break;
                case MyControlStateType.PRESSED:
                    answer = VRInput.IsPressed(off, button);
                    break;
                case MyControlStateType.NEW_RELEASED:
                    answer = VRInput.IsReleased(off, button);
                    break;
                default:
                    answer = false;
                    break;
            }
            // One line a second at most for each: the terminal asks once for each item it moves or queues.
            long ticks = Stopwatch.GetTimestamp();
            int i = shiftLeft ? 0 : 1;
            if (answer && ticks - amountNoted[i] > Stopwatch.Frequency)
            {
                amountNoted[i] = ticks;
                bool both = VRInput.IsPressed(off, shiftLeft ? VRButtons.Trigger : VRButtons.Grip);
                Log.Info($"Input: the {off} {(shiftLeft ? "grip" : "trigger")} answered the terminal's {(shiftLeft ? "SHIFT_LEFT" : "SHIFT_RIGHT")} ({type}): " +
                              (both ? "x1000 with the other" : shiftLeft ? "x10" : "x100"));
            }
            return answer;
        }

        private static readonly long[] amountNoted = new long[2];

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
        /// indicator the thrusters and the wheels read (forward and back, strafe, steer), A is up and B down. The free spectator
        /// camera is flown as the jetpack is: the left stick turned by the head's yaw, A up and B down.
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
            if (context == VRContext.Spectator)
            {
                AddSpectatorMovement(ref delta);
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
        /// The free spectator camera's movement (x right, y up, z back, along the camera's own axes; the game scales it by its
        /// speed, MoveAndRotate_UserControlled). The eyes are the camera turned by the head as tracked (CameraHeadLink), so the
        /// stick is turned by the head's yaw in the tracking space and goes where the head looks, level with the camera.
        /// </summary>
        private static void AddSpectatorMovement(ref Vector3 delta)
        {
            if (leftStick == Vector2.Zero && spectatorUp == 0f)
                return;
            float yaw = GameHead.Take() ? HeadCentre.Yaw(GameHead.TrackingPose) : 0f;
            float cos = (float)Math.Cos(yaw), sin = (float)Math.Sin(yaw);
            Vector3 add = new Vector3(
                leftStick.X * cos - leftStick.Y * sin,
                spectatorUp,
                -(leftStick.X * sin + leftStick.Y * cos));
            delta = Clamp(delta + add);
            moveGiven = Math.Max(moveGiven, Math.Min(Math.Max(leftStick.Length(), Math.Abs(spectatorUp)), 1f));
        }

        /// <summary>
        /// Adds to the game's rotation input (MyInputExtensions.GetRotation: x pitch, y yaw, + right). In a seat the
        /// right stick adds a rate, as a pad's does. On foot the snap turn: the character turns by
        /// -y * RotationSpeed * 0.02 radians (BodyFollow works from the same figure), so the input for one turn is the
        /// angle over that. The same value comes back for every call in the frame, as the mouse's does. On the jetpack in zero g
        /// the pitch (<see cref="UpdateZeroG"/>) the same way: the jetpack pitches the nose up by -x * RotationSpeed * 0.02. The
        /// spectator camera turns by y * 0.0025 * its angular speed (MoveAndRotate_UserControlled).
        /// </summary>
        public static void AddRotation(ref Vector2 rotation)
        {
            if (IsSeated(context))
            {
                rotation += seatRotation;
                turnGiven = Math.Max(turnGiven, Math.Min(seatRotation.Length() / 20f, 1f)); // the ship controller takes the indicator / 20
                return;
            }
            if (context == VRContext.Spectator)
            {
                if (snap == 0f)
                    return;
                snapGiven = true;
                MySpectatorCameraController spectator = MySpectatorCameraController.Static;
                float turnPerUnit = spectator != null ? SpectatorTurnPerUnit * spectator.SpeedModeAngular : 0f;
                if (turnPerUnit > 0f)
                    rotation.Y += snap / turnPerUnit;
                return;
            }
            if (zeroGPitch != 0f)
            {
                ZeroGShown();
                MyCharacter pitched = ControlledCharacter();
                float pitchPerUnit = pitched != null ? pitched.RotationSpeed * 0.02f : 0f;
                if (pitchPerUnit > 0f)
                    rotation.X -= zeroGPitch / pitchPerUnit;
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

        /// <summary>
        /// Adds the grips to the game's roll input (MyInputExtensions.GetRoll, + right) in a ship controller, and the zero-g roll
        /// (<see cref="UpdateZeroG"/>) on the jetpack, which rolls the body by the input * 0.02 radians.
        /// </summary>
        public static void AddRoll(ref float roll)
        {
            if (context == VRContext.Jetpack && zeroGRoll != 0f)
            {
                ZeroGShown();
                roll += zeroGRoll / JetpackRollPerUnit;
                return;
            }
            if (context != VRContext.Ship)
                return;
            roll += seatRoll;
            turnGiven = Math.Max(turnGiven, Math.Min(Math.Abs(seatRoll) * 0.2f, 1f)); // and rolls at the indicator * 0.2
        }

        /// <summary>The zero-g turn for the comfort vignette: a snap is a snap turn's blink, a smooth turn as strong as the stick is pushed.</summary>
        private static void ZeroGShown()
        {
            if (zeroGSnapped)
                snapGiven = true;
            else
                turnGiven = Math.Max(turnGiven, Math.Min(zeroGLevel, 1f));
        }

        /// <summary>
        /// A trigger pulled on a radial menu (the actions wheel) is that menu's: it does not also fire a tool, use something or
        /// thrust when the wheel closes under a finger still on it. It counts for the game again once it has been let go.
        /// </summary>
        internal static void SwallowTrigger(Hand hand) => SwallowTrigger(hand, $"the {hand.ToString().ToLowerInvariant()} trigger pulled on a radial menu");

        /// <summary>As <see cref="SwallowTrigger(Hand)"/>, for another reason: <paramref name="what"/> names the pull for the log.</summary>
        internal static void SwallowTrigger(Hand hand, string what)
        {
            if (!VRInput.IsPressed(hand, VRButtons.Trigger) || triggerSwallowed[(int)hand])
                return;
            triggerSwallowed[(int)hand] = true;
            Log.Info($"Trigger: {what} is held back from the game until it is let go");
        }

        /// <summary>The game's radial menus ignore the sticks for this long after closing, or until they are let go (MyGuiControlRadialMenuBase.MOVEMENT_SUPPRESSION).</summary>
        private static readonly TimeSpan MovementHold = TimeSpan.FromSeconds(2d);

        /// <summary>
        /// The stick that pointed at a wheel's choice is still pushed as the wheel goes, and the character must not walk (or the
        /// ship thrust) off with it. The game's radial menus ask its gameplay screen to ignore movement until the sticks are back
        /// in the middle or two seconds have gone (MyGuiControlRadialMenuBase.OnClosed); it ends that hold at the first frame with
        /// no movement input. The toolbar wheel keeps the sticks from the game while the grip or a trigger that drove it is down,
        /// which is such frames, so it asks again when the hands go back to the game; the actions wheel, which does not own them,
        /// asks as it closes.
        /// </summary>
        internal static void HoldMovement()
        {
            try
            {
                MyGuiScreenGamePlay gameplay = MyGuiScreenGamePlay.Static;
                if (gameplay != null && MySession.Static != null)
                    gameplay.SuppressMovement = MySession.Static.ElapsedGameTime + MovementHold;
            }
            catch (Exception e)
            {
                Log.Error(e, "Radial menu: the sticks could not be held off the character as it closed");
            }
        }

        /// <summary>The trigger is held and is the game's: not one swallowed by a radial menu (<see cref="SwallowTrigger"/>) and still down.</summary>
        private static bool TriggerHeld(Hand hand)
        {
            bool down = VRInput.IsPressed(hand, VRButtons.Trigger);
            if (!down)
                triggerSwallowed[(int)hand] = false;
            return down && !triggerSwallowed[(int)hand];
        }

        private static void Set(Intent intent, bool held)
        {
            if (held)
                now |= 1 << (int)intent;
        }

        private static Vector3 Clamp(Vector3 v) => new Vector3(
            MathHelper.Clamp(v.X, -1f, 1f), MathHelper.Clamp(v.Y, -1f, 1f), MathHelper.Clamp(v.Z, -1f, 1f));

        /// <summary>How far a grip is squeezed, 0 to 1; a source with only the button says all or nothing. Nothing while the grip holds something (a stick, a door reached from the seat).</summary>
        private static float Squeeze(Hand hand)
        {
            if (HandFlight.Holds(hand) || ControlGrip.Holds(hand) || Hands.GripClaim.Owner(hand) != null)
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
                // The free spectator camera is flown by the hands (UpdateSpectator); the orbiting one (Shift+F8) turns about its
                // target by the mouse, and the hands leave it to the keys.
                MySpectatorCameraController spectator = MySpectatorCameraController.Static;
                VRage.MySpectatorCameraMovementEnum movement = spectator?.SpectatorCameraMovement ?? VRage.MySpectatorCameraMovementEnum.None;
                if (movement == VRage.MySpectatorCameraMovementEnum.UserControlled || movement == VRage.MySpectatorCameraMovementEnum.FreeMouse)
                {
                    detail = movement == VRage.MySpectatorCameraMovementEnum.FreeMouse ? "free mouse" : null;
                    return VRContext.Spectator;
                }
                detail = movement == VRage.MySpectatorCameraMovementEnum.Orbit ? "the orbiting spectator camera is the view" : "the spectator camera is the view";
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
                case VRContext.Spectator: label = detail != null ? $"spectator camera ({detail})" : "spectator camera"; break;
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
