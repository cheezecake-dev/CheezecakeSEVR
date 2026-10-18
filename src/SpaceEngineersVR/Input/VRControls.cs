using System;
using System.Diagnostics;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Tracking;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// What the hands do to the game: the control table in docs/VR_PLAN.md, turned into the game's own controls for the
    /// character (on foot and on the jetpack) and for the seat of a ship, rover, remote control, turret or camera block.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Everything is added to what the keyboard, mouse and a gamepad give, never instead of it. The analog parts
    /// (walking, flying, steering, the snap turn) go into the game's movement, rotation and roll input
    /// (VRControls.Output.cs); the buttons answer the game's questions about its controls (VRControls.Context.cs).</item>
    /// <item>With a block in hand the left stick click and the right hand's stick, A and B build
    /// (<see cref="Hands.BuildMode"/>, which also has the paste and the voxel hand; the click is then not sprint or
    /// zero-g pitch and roll). With the colour tool the triggers, A, B and the right stick's flick paint
    /// (<see cref="Hands.PaintMode"/>).</item>
    /// <item>The game's radial menu is opened and steered by <see cref="ToolbarWheel"/>, which has the hands while it is up.</item>
    /// <item>On the jetpack in zero g, the left stick click held makes the right stick pitch and roll (<see cref="UpdateZeroG"/>);
    /// the free spectator camera is flown by the same sticks (<see cref="UpdateSpectator"/>).</item>
    /// </list>
    /// The class is split by job across partial files: Intent (the action set), Menu, Locomotion, Context, Output; the
    /// per-frame state they share is declared here.
    /// </remarks>
    internal static partial class VRControls
    {
        /// <summary>Debug: the hands do nothing to the game (<see cref="Rendering.RenderDebug"/> VRInput=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How long Y has to be held before it leaves the seat (or the camera), so a tap cannot eject you.</summary>
        public const double UseHoldSeconds = 0.5;

        private const float StickPress = 0.6f, StickRelease = 0.3f;
        private const float FlickPress = 0.7f, FlickRelease = 0.3f;

        /// <summary>Longest time one frame's smooth turn is worked out for, seconds, so a stall (a loading screen) is not one huge turn after it.</summary>
        private const double MaxSmoothTurnStep = 0.1;

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
        private static volatile bool seatDriven;
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
        internal const float ZeroGSquared = 0.1f;

        /// <summary>The default of <see cref="MaxLookYaw"/>, 70 deg; RenderDebug MaxLookYaw=n sets it, degrees, while the game runs.</summary>
        internal static readonly float DefaultMaxLookYaw = MathHelper.ToRadians(70f);

        /// <summary>The furthest the head looks round from the body, radians: past it the thrust, the character's head and the helmet stop turning, and the eyes turn on inside the helmet. The game has no yaw limit of its own outside ladders; a neck turns about this far.</summary>
        internal static float MaxLookYaw { get; set; } = DefaultMaxLookYaw;

        /// <summary>The furthest the head looks up or down from the body, radians: the game's own limit on the character's head (MyCharacter.MAX_HEAD_LOCAL_X_ANGLE, 89 deg).</summary>
        internal static readonly float MaxLookPitch = MathHelper.ToRadians(89f);

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

        /// <summary>A camera block is the view this frame (looking through it from on foot or a seat): Y held there leaves it.</summary>
        public static bool CameraView => cameraView;

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
        /// A seat's controls are what the hands drive this frame: the context is a seat, and no menu or toolbar wheel has the hands
        /// (a menu's Back is B, so the Vive wand's pad must not turn into B there). Written on the game thread, read on the render
        /// thread by <see cref="OpenXR.XrInput"/> for the right wand's pad (<see cref="OpenXR.WandLayout"/>), so it is volatile.
        /// </summary>
        internal static bool SeatDriven => seatDriven;

        /// <summary>The hands pitch or roll the local character on the jetpack this frame (<see cref="UpdateZeroG"/>), for <see cref="Tracking.ZeroGPivot"/>.</summary>
        internal static bool ZeroGTurning => zeroGPitch != 0f || zeroGRoll != 0f;

        /// <summary>
        /// The left stick click holds zero-g pitch and roll (<see cref="UpdateZeroG"/>'s test, asked as it stands now, so it holds
        /// before the zero-g pass has run this frame): the colour tool's flick (<see cref="Hands.PaintMode"/>) leaves the right stick
        /// to the roll while it does.
        /// </summary>
        internal static bool ZeroGHeld => ZeroGClick() && CanPitchAndRoll(out _);

        /// <summary>The left stick click is down on the jetpack, outside a camera block's view, with zero-g pitch and roll not Off, not held to build (<see cref="Hands.BuildMode.Modifier"/>) and not taken for a withdraw (<see cref="Hands.QuickInventory.TakesLeftClick"/>).</summary>
        private static bool ZeroGClick() =>
            context == VRContext.Jetpack && !cameraView && VRSettings.ZeroGTurn != VRSettings.ZeroGTurnOff
            && VRInput.IsPressed(Hand.Left, VRButtons.StickClick) && !Hands.BuildMode.Modifier && !Hands.QuickInventory.TakesLeftClick;

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
                Hands.QuickInventory.Frame(VRContext.None, true);
                Hands.Ping.Frame(VRContext.None, true, seconds);
                context = VRContext.None;
                seatDriven = false;
                cameraView = seatSticks = false;
                Cockpits.CockpitControls.Stop();
                Cockpits.SeatFit.Reset();
                Cockpits.CockpitButtons.Reset();
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
            // ColbyJack1134's cockpit sticks, throttles, wheels and handlebars, where his rig owns the seat.
            Cockpits.CockpitControls.Update(MenuScreen() != null);
            // His seat fit, in the seat his rig owns (moved from the quick menu's Seat page, Cockpits.SeatPage).
            Cockpits.SeatFit.Update();
            // His dashboard switches and covers, touched by the fingertip and pressed with the trigger or grip.
            Cockpits.CockpitTouch.BeginFrame();
            Cockpits.CockpitButtons.Update();

            leftStick = Dead(VRInput.Get(Hand.Left).Stick);
            rightStick = Dead(VRInput.Get(Hand.Right).Stick);

            ApplyWheel(seconds);
            // With a block in hand (or a paste, or the voxel hand) the right stick is building's while the left stick click is held for
            // it: a turn sideways, the distance up and down; with the colour tool a sideways flick of the right stick picks the
            // colour, so it does not turn.
            object menu = MenuScreen();
            seatDriven = IsSeated(context) && menu == null && !ToolbarWheel.Owns;
            Hands.BuildMode.Frame(context, ToolbarWheel.Owns || menu != null, seconds);
            Hands.PaintMode.Frame(context, ToolbarWheel.Owns || menu != null);
            // Pointing at a container, or the right hand on one: the stick clicks are the game's quick take, production and dump
            // there, and the right grip's Use waits for the grip to be let go.
            Hands.QuickInventory.Frame(context, ToolbarWheel.Owns || menu != null);
            // Held, X aims a ping and the pointing hand's trigger marks it; tapped, it is the dampeners on its release.
            Hands.Ping.Frame(context, ToolbarWheel.Owns || menu != null || cameraView, seconds);
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

            menuNow = menu;
            MenuButton(seconds, menuNow);
            panelWalk = WalksBesidePanel(menuNow, context, ToolbarWheel.Owns);
            UpdateBack(menuNow);
            ControlHints.Update(menuNow != null && !ToolbarWheel.Owns && !(menuNow is Gui.ActionsWheelScreen),
                walking: menuNow == null && !ToolbarWheel.Owns && (context == VRContext.OnFoot || context == VRContext.Jetpack),
                inventory: menuNow is MyGuiScreenTerminal && MyGuiScreenTerminal.GetCurrentScreen() == VRage.Game.ModAPI.MyTerminalPageEnum.Inventory,
                container: Hands.QuickInventory.Ready);
            ControlHints.Cutscene(menuNow == null && Rendering.ForcedView.CutsceneSkippable());
            LogBack();
            InputLog.Frame(seconds, WhereNow);
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

        /// <summary>How far a grip is squeezed, 0 to 1; a source with only the button says all or nothing. Nothing while the grip holds something (a cockpit stick, a door reached from the seat).</summary>
        private static float Squeeze(Hand hand)
        {
            if (Hands.GripClaim.Owner(hand) != null)
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
