using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Input.Bindings;
using SpaceEngineersVR.Gui;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// The hands on a radial menu. The wheel the hands open is the toolbar's slots (<see cref="SlotWheelScreen"/>, the
    /// game's radial menu screen filled with the toolbar the player has now: the character's on foot and on the jetpack,
    /// the seat's in a seat); the game's own system radial (dampeners, lights, helmet and the like) is one stick click away.
    /// The game's radial screens read the selection from the pad's stick (MyInput.GetJoystickPositionForGameplay),
    /// activate it on GUI SHIFT_RIGHT, close on GUI CANCEL, turn their pages on SWITCH_GUI_LEFT/RIGHT, and close by
    /// themselves the moment the pad is not the last device used. This class opens, steers and closes them from the
    /// controllers, through those same questions.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>On foot and on the jetpack: hold the left grip; push the left stick at a slot; either trigger, or letting
    /// go of the grip, activates it (<c>ActivateItemAtSlot</c>, as its number key does). The middle is chosen when the wheel
    /// opens, whatever the stick was doing and whatever was in the hand, and again whenever the stick is back there: that
    /// is the empty hand (<see cref="IWheelToolbar.EmptyHand"/>, the toolbar's holster slot), so opening the wheel and
    /// pulling a trigger at once leaves the hand empty. Letting go of the grip does the same only once the stick has chosen
    /// something and come back; with the stick never moved it just closes the wheel, because the left grip in empty air is
    /// also how a hand grabs and a missed grab must not holster the tool. The right B closes it without any effect.</item>
    /// <item>Seated: tap Y (let go before the hold that leaves the seat completes); either trigger activates (or empties
    /// the hand, from the middle, which is chosen as the wheel opens), B or another Y tap closes.</item>
    /// <item>The right stick, left or right, turns the toolbar's pages (<c>SwitchToPage</c>, round the ends) and the wheel
    /// shows the page the toolbar is on.</item>
    /// <item>The right stick pushed down, on the slot wheel, turns the external view on or off (the switch under the ring,
    /// <see cref="SlotWheelScreen"/>); the wheel stays up.</item>
    /// <item>Clicking the left stick while the slot wheel is up swaps to the game's system radial, and the hands keep
    /// steering: the same grip or trigger then activates one of its items.</item>
    /// </list>
    /// While the wheel is open it has the sticks and the buttons: <see cref="VRControls"/> hands nothing else to the
    /// game, so the character or ship does not move and the trigger that activates does not fire; it keeps them until the
    /// grip, B or Y that drove it and both triggers are let go (<see cref="DrivingButtonHeld"/>), so the press that closes it
    /// does not reach the game either. The toolbar bar of the HUD
    /// is not drawn on foot (<see cref="Rendering.HudToolbar"/>, the setting "Toolbar on the HUD" brings it back); seated
    /// it stays on screen (the slot screen does not hide the HUD).
    /// </remarks>
    internal static class ToolbarWheel
    {
        /// <summary>Debug, <see cref="Rendering.RenderDebug"/> ToolbarWheel=0: no wheel, the grip and Y do what they did.</summary>
        public const int ModeOff = 0;

        /// <summary>ToolbarWheel=1, the default: the toolbar's slots, with the system radial a stick click away.</summary>
        public const int ModeSlots = 1;

        /// <summary>ToolbarWheel=2: the game's system radial only, without the slot wheel (a fallback if the slot screen misbehaves in the game).</summary>
        public const int ModeSystem = 2;

        /// <summary>ToolbarWheel=3: the game's block palette (TOOLBAR_RADIAL_MENU) instead of the system radial; to look at.</summary>
        public const int ModePalette = 3;

        /// <summary>Which wheel the grip / Y tap opens; one of the Mode constants.</summary>
        public static int Mode { get; set; } = ModeSlots;

        /// <summary>Test seam: stands in for the gameplay screen having the focus, so the wheel can be driven without a game.</summary>
        internal static bool? GameplayFocusOverride { get; set; }

        /// <summary>Test seam: stands in for the radial screen being up.</summary>
        internal static bool? ScreenOverride { get; set; }

        /// <summary>Test seam: the toolbar the player has now (the game's <c>MyToolbarComponent.CurrentToolbar</c>); null when there is none.</summary>
        internal static Func<IWheelToolbar> ToolbarSource { get; set; } = GameToolbar.Current;

        /// <summary>Test seam: puts the slot wheel screen up for a toolbar.</summary>
        internal static Action<IWheelToolbar> OpenSlotScreen { get; set; } = OpenGameSlotScreen;

        /// <summary>Test seam: closes the slot wheel screen.</summary>
        internal static Action CloseSlotScreen { get; set; } = CloseGameSlotScreen;

        /// <summary>Test seam: puts the game's system radial up straight away (the swap from the slots; the open from the grip asks the gameplay screen).</summary>
        internal static Action OpenSystemScreen { get; set; } = OpenGameSystemScreen;

        /// <summary>Test seam: the slot wheel's external view switch is pressed (the right stick pushed down).</summary>
        internal static Action PressViewSwitch { get; set; } = SlotWheelScreen.PressViewSwitch;

        /// <summary>How long the game gets to put its radial menu up after being asked, before the wheel gives up.</summary>
        public const double OpenTimeoutSeconds = 0.5;

        /// <summary>
        /// A thumb eases off the stick as a finger lets go of the grip or squeezes the trigger, so the direction the stick
        /// had this long ago still counts as the choice when the wheel is confirmed.
        /// </summary>
        public const double PickGraceSeconds = 0.1;

        /// <summary>
        /// A stick pushed this far (0 to 1, the dead zone already taken out) is a deliberate choice. The thumb on the stick
        /// is the same thumb that eases off it when the grip is let go or the left trigger is pulled, and on the way back to
        /// the middle its direction wanders, so a stick found short of this when the wheel is confirmed counts as the firm
        /// direction it had a moment ago (<see cref="FirmLookbackSeconds"/>). The seat's right trigger never moves that thumb;
        /// this keeps the grip and the left trigger as exact.
        /// </summary>
        public const float FirmStick = 0.5f;

        /// <summary>How long ago the stick was last <see cref="FirmStick"/> out and still stands as the choice when it has eased off, seconds.</summary>
        public const double FirmLookbackSeconds = 0.25;

        /// <summary>
        /// The stick has to be pushed this far (0 to 1, the dead zone already taken out) before it chooses a slot, and back
        /// under <see cref="SelectRelease"/> before the middle is chosen again. The middle is the empty hand, so a thumb
        /// resting a hair past the dead zone must not pick whichever slot that hair points at; the two limits keep a thumb
        /// at the edge from flickering between the middle and a slot.
        /// </summary>
        public const float SelectPress = 0.2f, SelectRelease = 0.1f;

        /// <summary>
        /// A left stick that had been out this long when the grip (or the Y tap) opened the wheel is the walk, or the thrust,
        /// the thumb was in the middle of, not a choice: letting go of the grip with it still pushed forward must not
        /// activate slot 1. One that came out within this long of the open is a flick that began with the grip, and counts
        /// at once.
        /// </summary>
        public const double HeldStickSeconds = 0.25;

        /// <summary>A stick held at the open chooses nothing until it has been let go, or has turned this many degrees from where it was held.</summary>
        public const double HeldStickTurnDegrees = 30d;

        private const float PagePress = 0.6f, PageRelease = 0.3f;

        private enum Phase
        {
            Idle,

            /// <summary>The wheel has been asked for and its screen is not up yet.</summary>
            Opening,

            /// <summary>The screen is up and the hands steer it.</summary>
            Open,

            /// <summary>The frame the wheel is told to activate or close; a game radial still has to see the stick and the pad.</summary>
            Closing,

            /// <summary>Closed; the wheel keeps the hands until the buttons that drove it are let go.</summary>
            Done,
        }

        private enum Gesture
        {
            Grip,
            Tap,
        }

        /// <summary>Which screen the wheel is driving.</summary>
        private enum Kind
        {
            /// <summary>The toolbar's slots (<see cref="SlotWheelScreen"/>), driven by the wheel itself.</summary>
            Slots,

            /// <summary>The game's system radial, asked of the game's own gameplay screen (or put up by the swap).</summary>
            System,

            /// <summary>The game's block palette.</summary>
            Palette,
        }

        private static Phase phase;
        private static Gesture gesture;
        private static Kind kind;
        private static IWheelToolbar toolbar;
        private static double openedAt, yDownAt = -1d, pickedAt = -1d, stickOutSince = -1d;
        private static Vector2 picked, pick, heldDirection, firm;
        private static double firmAt = -1d;
        private static bool armed = true, choosing;
        private static bool openPulse, confirmPulse, cancelPulse, slotScreenFailed;
        private static int pageHeld, page;
        private static bool viewHeld, viewPressed;

        /// <summary>The cockpit switch the fingertip was on when Y went down (ColbyJack1134's ToolbarGesture.Switch), or -1.</summary>
        private static int yDownSwitch = -1;

        /// <summary>The wheel has the hands: the sticks and buttons are its, and the game is not given them.</summary>
        public static bool Owns => phase != Phase.Idle;

        /// <summary>A game radial menu is being asked about or shown, so it must read the hands as a pad in use.</summary>
        public static bool Capturing => phase == Phase.Opening || phase == Phase.Open || phase == Phase.Closing;

        /// <summary>Which stage the wheel is at, for the log and the checks.</summary>
        internal static string Stage => phase.ToString();

        /// <summary>Which wheel it is driving (Slots, System or Palette), for the log and the checks.</summary>
        internal static string Showing => kind.ToString();

        /// <summary>The right stick is held down on the slot wheel's external view switch: the switch shows the wheel's highlight.</summary>
        internal static bool ViewSwitchHeld => viewPressed && phase == Phase.Open && kind == Kind.Slots;

        /// <summary>The hands are not there (no source, or the game control is off): drop everything.</summary>
        public static void Reset()
        {
            phase = Phase.Idle;
            openPulse = confirmPulse = cancelPulse = false;
            pick = picked = heldDirection = firm = Vector2.Zero;
            armed = true;
            choosing = false;
            yDownAt = pickedAt = stickOutSince = firmAt = -1d;
            pageHeld = page = 0;
            toolbar = null;
        }

        /// <summary>
        /// Game thread, once per frame, after the hands' actions have been worked out: decides what the wheel does this
        /// frame. The sticks are the hands' (dead zone taken out, +Y forward).
        /// </summary>
        public static void Update(VRContext context, double seconds, Vector2 leftStick, Vector2 rightStick)
        {
            openPulse = confirmPulse = cancelPulse = false;
            pick = Vector2.Zero;
            page = 0;

            // How long the stick has been out, wheel or no wheel: what was out before the grip is the walk, not a choice.
            if (leftStick == Vector2.Zero)
                stickOutSince = -1d;
            else if (stickOutSince < 0d)
                stickOutSince = seconds;

            bool seated = VRControls.IsSeated(context);
            bool walking = context == VRContext.OnFoot || context == VRContext.Jetpack;
            bool usable = Mode != ModeOff && VRInput.Active && (seated || walking);

            // The seat's tools wheel (Y tapped, by default: let go before the hold that leaves the seat completes). The cockpit
            // switch under the fingertip is the one as the press began.
            bool tapped = false;
            if (phase == Phase.Idle && seated && GameplayFocused())
            {
                bool down = ControllerBindings.ControlDown(VRAction.SeatToolsWheel);
                if (down && yDownAt < 0d)
                {
                    yDownAt = seconds;
                    yDownSwitch = Cockpits.CockpitButtons.HoveredSwitch;
                }
                tapped = ControllerBindings.Started(VRAction.SeatToolsWheel);
                if (!down)
                    yDownAt = -1d;
            }
            else
                yDownAt = -1d;

            switch (phase)
            {
                case Phase.Idle:
                    if (!usable)
                        break;
                    if (walking && ControllerBindings.Started(VRAction.ToolsWheel) && Hands.GripClaim.Owner(Hand.Left) == null && GameplayFocused())
                        Begin(Gesture.Grip, seconds, leftStick);
                    else if (seated && tapped && yDownSwitch >= 0)
                    {
                        // ColbyJack1134's AssignSwitch: Y tapped with a fingertip on one of his cockpit switches opens the game's
                        // toolbar config screen on that switch, to assign, change or clear what it does. A fingertip that
                        // moved to another switch before the release does nothing, rather than edit the wrong one.
                        if (Cockpits.CockpitButtons.HoveredSwitch == yDownSwitch)
                            Cockpits.CockpitActions.Configure(yDownSwitch);
                    }
                    else if (seated && tapped && WristButton.OpenSeatedAtWrist())
                    {
                        // Tapped while reading the wrist: the wrist panel, as Y opens it on foot.
                    }
                    else if (seated && tapped)
                        Begin(Gesture.Tap, seconds, leftStick);
                    break;

                case Phase.Opening:
                case Phase.Open:
                    Steer(usable && (gesture == Gesture.Grip ? walking : seated), seconds, leftStick, rightStick);
                    break;

                case Phase.Closing:
                case Phase.Done:
                    phase = Phase.Done;
                    if (!DrivingButtonHeld())
                    {
                        // The stick that chose may still be out, and the game's own hold on movement after a radial menu
                        // (set as the screen closed) was ended by the frames of no input the Done phase gave it.
                        phase = Phase.Idle;
                        VRControls.HoldMovement();
                    }
                    break;
            }
        }

        private static void Begin(Gesture how, double seconds, Vector2 leftStick)
        {
            kind = Mode == ModePalette ? Kind.Palette : Mode == ModeSystem ? Kind.System : Kind.Slots;
            toolbar = null;
            if (kind == Kind.Slots)
            {
                // A screen that failed to build once is not tried again this run: the system radial stands in.
                toolbar = slotScreenFailed ? null : ToolbarSource?.Invoke();
                if (toolbar == null && !slotScreenFailed)
                    return;
                if (toolbar == null)
                    kind = Kind.System;
            }
            phase = Phase.Opening;
            gesture = how;
            openedAt = seconds;
            // The middle is what is chosen as the wheel opens: no direction is carried over from before it.
            picked = firm = Vector2.Zero;
            pickedAt = firmAt = -1d;
            choosing = false;
            pageHeld = 0;
            bool held = leftStick != Vector2.Zero && stickOutSince >= 0d && seconds - stickOutSince >= HeldStickSeconds;
            armed = !held;
            heldDirection = held ? leftStick : Vector2.Zero;
            if (held)
                Log.Info("Toolbar wheel: the left stick was already out when the wheel opened; it chooses nothing until it is let go or turned");
            string gestureName = how == Gesture.Grip ? "left grip" : "Y tap";
            if (kind == Kind.Slots)
            {
                try
                {
                    OpenSlotScreen(toolbar);
                    Log.Info($"Toolbar wheel: opening the toolbar slots, page {toolbar.Page + 1} of {toolbar.PageCount} ({gestureName})");
                    return;
                }
                catch (Exception e)
                {
                    slotScreenFailed = true;
                    toolbar = null;
                    kind = Kind.System;
                    Log.Error(e, "Toolbar wheel: the slot wheel could not be put up; using the game's system radial instead");
                }
            }
            openPulse = true;
            Log.Info($"Toolbar wheel: opening the game's {(kind == Kind.Palette ? "block palette" : "system menu")} ({gestureName})");
        }

        private static void Steer(bool usable, double seconds, Vector2 leftStick, Vector2 rightStick)
        {
            bool opening = phase == Phase.Opening;
            if (!usable)
            {
                Finish("the controls changed");
                return;
            }
            bool screen = ScreenUp();
            if (phase == Phase.Opening)
            {
                if (screen)
                    phase = Phase.Open;
                else if (seconds - openedAt > OpenTimeoutSeconds)
                {
                    Finish("the game did not open its radial menu");
                    return;
                }
            }
            else if (!screen)
            {
                Finish("the game closed its radial menu");
                return;
            }

            // The pages: the right stick pushed to a side, held, as a pad's bumper is.
            float x = rightStick.X;
            int was = pageHeld;
            if (pageHeld == 0)
            {
                if (Math.Abs(x) > PagePress && Math.Abs(x) > Math.Abs(rightStick.Y))
                    pageHeld = x > 0f ? 1 : -1;
            }
            else if (Math.Abs(x) < PageRelease || Math.Sign(x) != pageHeld)
                pageHeld = 0;
            if (kind == Kind.Slots)
            {
                // The wheel turns the toolbar's own page and shows it; the screen's carousel is not driven.
                if (was == 0 && pageHeld != 0)
                    TurnPage(pageHeld);
            }
            else
                page = pageHeld;

            // The external view switch under the slot wheel's ring: the right stick pushed down, as it is pushed to a side for
            // the pages. A stick already down as the wheel opens (a seat's look) is not a press until it has come back.
            if (kind == Kind.Slots)
            {
                float y = rightStick.Y;
                if (opening)
                {
                    viewHeld = y < -PageRelease;
                    viewPressed = false;
                }
                else if (!viewHeld && y < -PagePress && -y > Math.Abs(x))
                {
                    viewHeld = viewPressed = true;
                    Log.Info("Toolbar wheel: right stick down, the external view switch");
                    try
                    {
                        PressViewSwitch?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Toolbar wheel: the external view switch failed");
                    }
                }
                else if (viewHeld && y > -PageRelease)
                    viewHeld = viewPressed = false;
            }

            // What ends it (the bindings, by default): either trigger pulled (activates), on foot the grip let go (activates),
            // the right B or a tap of Y (closes). The trigger is the same in the seat and on foot; the thumb stays on the stick
            // while a trigger is pulled.
            string by = ControllerBindings.Started(VRAction.WheelActivate) ? "the wheel's activate" : null;
            bool cancel = ControllerBindings.Started(VRAction.CloseToolsWheel);
            bool gripLetGo = false;
            if (gesture == Gesture.Grip && by == null && !ControllerBindings.IsActive(VRAction.ToolsWheel))
            {
                by = "the tools wheel let go";
                gripLetGo = true;
            }
            bool end = cancel || by != null;

            // A stick held from before the wheel opened chooses nothing until it has been let go or turned.
            if (!armed && (leftStick == Vector2.Zero || TurnedFrom(heldDirection, leftStick) >= HeldStickTurnDegrees))
                armed = true;

            // The middle stays chosen until the stick is pushed SelectPress out, and comes back when it is under SelectRelease.
            float length = leftStick.Length();
            if (choosing ? length < SelectRelease : length >= SelectPress)
                choosing = !choosing;
            Vector2 stick = choosing ? leftStick : Vector2.Zero;

            // The game's stick has +Y down, as a DirectInput axis does.
            if (armed)
            {
                if (stick != Vector2.Zero)
                {
                    picked = new Vector2(stick.X, -stick.Y);
                    pickedAt = seconds;
                    pick = picked;
                    if (length >= FirmStick)
                    {
                        firm = picked;
                        firmAt = seconds;
                    }
                    else if (end && !cancel && EasedFromFirm(seconds))
                        pick = firm;
                }
                else if (end && !cancel && pickedAt >= 0d && seconds - pickedAt <= PickGraceSeconds)
                    pick = EasedFromFirm(seconds) ? firm : picked;
            }

            if (!end)
            {
                if (kind == Kind.Slots && phase == Phase.Open && ControllerBindings.Started(VRAction.OpenActionsWheel))
                    SwapToSystem(seconds);
                return;
            }

            if (kind == Kind.Slots)
            {
                // The middle of the wheel is the empty hand, and it is what is chosen unless the stick is out at a slot: as the
                // wheel opens, and again once the stick is back (the centre of the screen is lit then, SlotWheelScreen.UpdateHighlight).
                // A trigger there empties the hand. Letting go of the grip does too once the stick has chosen something and come
                // back, but not with the stick never moved: the left grip in empty air is also how a hand grabs, and a missed grab
                // must not holster the tool. That, and a cancel (the right B, or Y in a seat), close the wheel and do nothing else.
                bool neverLeft = pickedAt < 0d;
                int slot = !cancel && pick != Vector2.Zero ? SlotWheel.IndexAt(pick) : -1;
                bool emptyHand = slot < 0 && !cancel && !(gripLetGo && neverLeft);
                if (slot >= 0)
                    Activate(slot);
                else if (emptyHand)
                    EmptyHand();
                CloseSlots();
                phase = Phase.Closing;
                Log.Info(slot >= 0 ? $"Toolbar wheel: activated slot {slot + 1} ({by})"
                    : emptyHand ? $"Toolbar wheel: empty hand ({by}{(neverLeft ? ", the stick never left the middle" : string.Empty)})"
                    : gripLetGo && !cancel ? "Toolbar wheel: closed (grip let go, the stick never left the middle)"
                    : "Toolbar wheel: closing");
                return;
            }

            // Activating with nothing chosen is the game's put-the-tool-away; the wheel closes instead.
            confirmPulse = !cancel && pick != Vector2.Zero;
            cancelPulse = !confirmPulse;
            phase = Phase.Closing;
            Log.Info(confirmPulse ? $"Toolbar wheel: activating the chosen item ({by})" : "Toolbar wheel: closing");
        }

        /// <summary>The stick was <see cref="FirmStick"/> out within <see cref="FirmLookbackSeconds"/>: what it points at now is a thumb easing off.</summary>
        private static bool EasedFromFirm(double seconds) => firmAt >= 0d && seconds - firmAt <= FirmLookbackSeconds;

        /// <summary>How far round, in degrees, the stick has turned from one direction to another (0 to 180).</summary>
        internal static double TurnedFrom(Vector2 from, Vector2 to)
        {
            double cross = (double)from.X * to.Y - (double)from.Y * to.X, dot = (double)from.X * to.X + (double)from.Y * to.Y;
            return Math.Abs(Math.Atan2(cross, dot)) * 180d / Math.PI;
        }

        /// <summary>The right stick pushed to a side: the toolbar's next or previous page, round the ends (the game's SwitchToPage does not wrap).</summary>
        private static void TurnPage(int direction)
        {
            try
            {
                int count = toolbar.PageCount;
                if (count < 2)
                    return;
                int target = ((toolbar.Page + direction) % count + count) % count;
                toolbar.SwitchToPage(target);
                Log.Info($"Toolbar wheel: page {target + 1} of {count}");
            }
            catch (Exception e)
            {
                Log.Error(e, "Toolbar wheel: could not change the toolbar's page");
            }
        }

        /// <summary>Activates a slot of the page the toolbar is on, if it is still the toolbar the wheel was opened on.</summary>
        private static void Activate(int slot)
        {
            try
            {
                IWheelToolbar bar = UsableToolbar("activated");
                if (bar != null && slot < bar.SlotCount)
                    bar.ActivateSlot(slot);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Toolbar wheel: slot {slot + 1} could not be activated");
            }
        }

        /// <summary>Empties the hand with the game's own call for it (<see cref="IWheelToolbar.EmptyHand"/>), if it is still the toolbar the wheel was opened on.</summary>
        private static void EmptyHand()
        {
            try
            {
                UsableToolbar("unequipped")?.EmptyHand();
            }
            catch (Exception e)
            {
                Log.Error(e, "Toolbar wheel: the hand could not be emptied");
            }
        }

        /// <summary>
        /// The toolbar the wheel was opened on, when it is still the player's and the game lets the player use it, as the number
        /// keys ask (MyToolbarComponent.HandleInput); otherwise null, with the reason logged and <paramref name="nothing"/> done.
        /// </summary>
        private static IWheelToolbar UsableToolbar(string nothing)
        {
            IWheelToolbar now = ToolbarSource?.Invoke();
            if (toolbar == null || !toolbar.Equals(now))
                Log.Info("Toolbar wheel: the toolbar changed while the wheel was up; nothing " + nothing);
            else if (!toolbar.CanActivate)
                Log.Info("Toolbar wheel: the game does not let the player activate toolbar items now");
            else
                return toolbar;
            return null;
        }

        /// <summary>The slot wheel's screen goes and the game's system radial takes its place; the hands keep steering.</summary>
        private static void SwapToSystem(double seconds)
        {
            CloseSlots();
            kind = Kind.System;
            toolbar = null;
            // The VR actions wheel takes the hands from here; this wheel keeps them from the game until the grip is let go.
            if (Gui.ActionsWheel.Enabled && Gui.ActionsWheel.Open(Hand.Left, gesture == Gesture.Grip))
            {
                Finish("left stick click, handed to the VR actions wheel");
                return;
            }
            Log.Info("Toolbar wheel: left stick click, swapping to the game's system menu");
            phase = Phase.Opening;
            openedAt = seconds;
            pageHeld = 0;
            try
            {
                OpenSystemScreen();
            }
            catch (Exception e)
            {
                Log.Error(e, "Toolbar wheel: the system menu could not be put up");
                Finish("the system menu did not open");
            }
        }

        private static void CloseSlots()
        {
            try
            {
                CloseSlotScreen?.Invoke();
            }
            catch (Exception e)
            {
                Log.Error(e, "Toolbar wheel: the slot wheel could not be closed");
            }
        }

        private static void Finish(string why)
        {
            Log.Info("Toolbar wheel: done, " + why);
            if (kind == Kind.Slots)
                CloseSlots();
            phase = Phase.Done;
        }

        /// <summary>
        /// The controls that opened or closed the wheel, still down. They cannot reach the game (a press belongs to where it
        /// began: ControllerBindings), but the wheel stays done until they are let go, so the game's own hold on movement after
        /// a radial menu sees its frames of no input.
        /// </summary>
        private static bool DrivingButtonHeld() =>
            ControllerBindings.ControlDown(gesture == Gesture.Grip ? VRAction.ToolsWheel : VRAction.SeatToolsWheel)
            || ControllerBindings.ControlDown(VRAction.CloseToolsWheel) || ControllerBindings.ControlDown(VRAction.WheelActivate);

        /// <summary>
        /// The game's answer to a question about a control. The frame a game radial opens from the grip, the gameplay
        /// screen is told its open control is newly pressed (SYSTEM_RADIAL_MENU, or TOOLBAR_RADIAL_MENU), in whichever
        /// control set it is asking about. After that the radial menu's own screen asks about GUI controls: activate
        /// (SHIFT_RIGHT), close (CANCEL) and the pages (SWITCH_GUI_LEFT/RIGHT, held). The slot wheel is not opened through
        /// the game's controls and does its own activating and paging, so nothing is answered for it.
        /// </summary>
        public static bool Answer(MyStringId context, MyStringId control, MyControlStateType type)
        {
            if (openPulse)
                return type == MyControlStateType.NEW_PRESSED
                    && control == (kind == Kind.Palette ? MyControlsSpace.TOOLBAR_RADIAL_MENU : MyControlsSpace.SYSTEM_RADIAL_MENU)
                    && VRControls.ContextOf(context) != VRContext.None;
            if (!Capturing || context != MyControllerHelper.CX_GUI)
                return false;
            switch (type)
            {
                case MyControlStateType.NEW_PRESSED:
                    return control == MyControlsGUI.SHIFT_RIGHT ? confirmPulse : control == MyControlsGUI.CANCEL && cancelPulse;
                case MyControlStateType.PRESSED:
                    return control == MyControlsGUI.SWITCH_GUI_LEFT ? page < 0 : control == MyControlsGUI.SWITCH_GUI_RIGHT && page > 0;
                default:
                    return false;
            }
        }

        /// <summary>The stick the radial menu reads, if the wheel is steering it (x right, y down); false to leave the game's own reading.</summary>
        internal static bool TryStick(out Vector3 value)
        {
            value = new Vector3(pick.X, pick.Y, 0f);
            return Capturing && pick != Vector2.Zero;
        }

        private static bool GameplayFocused() =>
            GameplayFocusOverride ?? MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay;

        /// <summary>The screen the wheel is driving is up: the slot wheel's own, or one of the game's radials that is not it.</summary>
        private static bool ScreenUp()
        {
            if (ScreenOverride.HasValue)
                return ScreenOverride.Value;
            foreach (MyGuiScreenBase screen in MyScreenManager.Screens)
            {
                if (screen is MyGuiControlRadialMenuBase && (screen is SlotWheelScreen) == (kind == Kind.Slots)
                    && screen.State != MyGuiScreenState.CLOSING && screen.State != MyGuiScreenState.CLOSED)
                    return true;
            }
            return false;
        }

        private static void OpenGameSlotScreen(IWheelToolbar bar)
        {
            if (!(bar is GameToolbar game))
                throw new InvalidOperationException("the slot wheel shows the game's toolbar");
            MyGuiSandbox.AddScreen(new SlotWheelScreen(game.Toolbar));
        }

        private static void CloseGameSlotScreen()
        {
            // Closing starts a transition, but collect them first rather than walk a list the screens are changing.
            var open = new List<MyGuiScreenBase>();
            foreach (MyGuiScreenBase screen in MyScreenManager.Screens)
            {
                if (screen is SlotWheelScreen && screen.State != MyGuiScreenState.CLOSING && screen.State != MyGuiScreenState.CLOSED)
                    open.Add(screen);
            }
            foreach (MyGuiScreenBase screen in open)
                screen.CloseScreen();
        }

        /// <summary>The game's own call for the system radial (MyRadialMenuComponent.ShowSystemRadialMenu, what the gameplay screen makes when SYSTEM_RADIAL_MENU is pressed), with the control set of what the player controls.</summary>
        private static void OpenGameSystemScreen()
        {
            // The component is internal to the game, so it is reached by name.
            Type type = AccessTools.TypeByName("Sandbox.Game.Screens.Helpers.MyRadialMenuComponent")
                ?? throw new MissingMemberException("MyRadialMenuComponent");
            MethodInfo show = AccessTools.Method(type, "ShowSystemRadialMenu", new[] { typeof(MyStringId), typeof(Func<bool>) })
                ?? throw new MissingMethodException("MyRadialMenuComponent.ShowSystemRadialMenu");
            // MySession.GetComponent<T> needs the component type at compile time, which is internal; close it over the type found above.
            MethodInfo get = AccessTools.Method(typeof(MySession), nameof(MySession.GetComponent)).MakeGenericMethod(type);
            MyStringId context = MySession.Static.ControlledEntity?.ControlContext ?? MyStringId.NullOrEmpty;
            show.Invoke(get.Invoke(MySession.Static, null), new object[] { context, null });
        }

        /// <summary>
        /// The radial menu closes itself on any frame the pad was not the last device used (MyGuiControlRadialMenuBase.Update),
        /// and takes its choice from the pad's stick. While the wheel runs it is told the pad is in use and given the stick.
        /// Each patch on its own, as <see cref="InputPatches"/> does.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            Hook(harmony, "pad last used", AccessTools.PropertyGetter(typeof(MyVRageInput), nameof(MyVRageInput.IsJoystickLastUsed)), nameof(LastUsed));
            Hook(harmony, "radial stick",
                AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.GetJoystickPositionForGameplay), new[] { typeof(RequestedJoystickAxis) }),
                nameof(Position));
        }

        private static void Hook(Harmony harmony, string what, MethodBase target, string postfix) =>
            PatchRegistry.Hook(harmony, typeof(ToolbarWheel), "Toolbar wheel", "the radial menu will not follow the hands", what, target, postfix: postfix);

        private static void LastUsed(ref bool __result)
        {
            if (Capturing)
                __result = true;
        }

        private static void Position(ref Vector3 __result)
        {
            if (__result == Vector3.Zero && TryStick(out Vector3 value))
                __result = value;
        }
    }
}
