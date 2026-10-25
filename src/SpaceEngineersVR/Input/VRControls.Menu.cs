// VRControls: menus - which screen has focus, back and menu buttons, walking beside a panel.
using System;
using System.Diagnostics;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input.Bindings;
using VRage.Input;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    internal static partial class VRControls
    {
        /// <summary>A test aid implements this to stand in for the screen that has focus; <paramref name="active"/> is set when it does.</summary>
        static partial void MenuScreenSeam(ref bool active, ref object screen);

        /// <summary>A test aid implements this to stand in for the game's gameplay input pass.</summary>
        static partial void GameplayPassSeam(ref Action pass);

        /// <summary>
        /// The screen that has focus when it is a menu, or null: the gameplay screen has focus while the player plays, and
        /// anything else (the pause menu, options, the terminal, the G menu, the wrist panel, a message box) is a menu.
        /// </summary>
        internal static object FocusedMenu() => MenuScreen();

        private static object MenuScreen()
        {
            bool seamed = false;
            object seam = null;
            MenuScreenSeam(ref seamed, ref seam);
            if (seamed)
                return seam;
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
        /// gauntlet), or a screen another plugin registered to walk beside (<see cref="PanelScreens.WalksBeside"/>), and the hands
        /// drive a character on foot or on the jetpack. Every other menu has the hands to itself; this one is a wrist watch,
        /// glanced at while walking.
        /// </summary>
        internal static bool WalksBesidePanel(object menu, VRContext c, bool wheelOwns) =>
            !wheelOwns && (menu is QuickActionsScreen || PanelScreens.WalksBeside(menu)) && (c == VRContext.OnFoot || c == VRContext.Jetpack);

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
                Action pass = null;
                GameplayPassSeam(ref pass);
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
        /// Back (B, or a tap of the left Menu button, whose hold recentres, by default) in a menu: the game's own GUI CANCEL, the
        /// question every screen that closes on Escape also asks (MyGuiScreenBase.HandleInput, a message box's release). The press
        /// has to begin in a menu (ControllerBindings), and it belongs to the screen that had focus when Back came on: when
        /// another screen takes focus before the button is let go (this one closed, or opened a confirmation) the rest of the
        /// press is dropped, so the release is not taken by the screen underneath. The toolbar wheel closes itself and is left alone.
        /// </summary>
        private static void UpdateBack(object menu)
        {
            int bit = 1 << (int)Intent.Back;
            // A cutscene on the forced view's screen: B is its skip. The game asks the same GUI CANCEL while it plays, beside
            // Enter and Space (MyGuiScreenGamePlay.HandleInput, with DisableInput set), and no gameplay control is read then.
            if (menu == null && Rendering.ForcedView.InCutscene)
                menu = CutsceneSkip;
            if (menu == null || ToolbarWheel.Owns)
            {
                before &= ~bit;
                backHeld = false;
                backScreen = null;
                return;
            }
            bool held = backHeld;
            if (held && !ReferenceEquals(menu, backScreen))
            {
                before &= ~bit;
                backHeld = false;
                held = false;
            }
            // A cutscene asks the same question with no menu: B (the jetpack's, or a seat's thrust down) skips it, as it always has.
            bool cutscene = menu == CutsceneSkip;
            bool on = cutscene ? On(VRAction.ToggleJetpack) || On(VRAction.ThrustDown) : On(VRAction.MenuBack);
            bool began = cutscene
                ? ControllerBindings.Started(VRAction.ToggleJetpack) || ControllerBindings.Started(VRAction.ThrustDown)
                : ControllerBindings.Started(VRAction.MenuBack);
            backHeld = on && (backHeld || began);
            if (!backHeld)
                return;
            if (!held)
                backScreen = menu;
            now |= bit;
        }

        /// <summary>Stands for the cutscene as the screen a Back press began in (<see cref="UpdateBack"/>).</summary>
        private static readonly object CutsceneSkip = new object();

        // The recentre hold's feel on the hand that holds it: nothing for a tap's length, then a light ramp up to the hold, then a firm tick.
        private const double MenuRampFrom = 0.25;
        private const float MenuRampFromAmplitude = 0.08f, MenuRampToAmplitude = 0.35f, MenuRampPulseSeconds = 0.03f;
        private const float MenuTickAmplitude = 0.9f, MenuTickSeconds = 0.05f;

        // The recentre's hold is building (for the ramp), and the hand that holds it.
        private static bool menuRecentred;

        /// <summary>
        /// The game menu and Recentre view (both on the left Menu button by default: a tap the game menu, or Back in a menu,
        /// held its hold time a recentre). Recentre turns the view on all axes, as Recentre view does
        /// (<see cref="Tracking.GameHead.Recentre"/>), on foot, in a seat and in a menu, once a press, with a light ramp on the
        /// hand while it builds and a firm tick when it takes. The game menu is MAIN_MENU, for the one frame it comes on, from
        /// gameplay only; a press that began in a menu or the other way round does nothing where it ends (ControllerBindings).
        /// </summary>
        private static void MenuButton(object menu)
        {
            Hand hand = ControllerBindings.HandOf(VRAction.Recentre);
            if (ControllerBindings.Started(VRAction.Recentre))
            {
                menuRecentred = true;
                Tracking.GameHead.Recentre($"Recentre view held{(menu != null ? " in " + menu.GetType().Name : "")}");
                Hands.Haptics.Pulse(hand, MenuTickAmplitude, MenuTickSeconds);
            }
            else
            {
                double progress = ControllerBindings.HoldProgress(VRAction.Recentre);
                if (progress < 0d || progress >= 1d)
                    menuRecentred = false;
                else if (!menuRecentred)
                {
                    double from = MenuRampFrom;
                    if (progress >= from)
                        Hands.Haptics.Pulse(hand, MathHelper.Lerp(MenuRampFromAmplitude, MenuRampToAmplitude, (float)((progress - from) / (1d - from))), MenuRampPulseSeconds);
                }
            }
            if (menu == null && ControllerBindings.Started(VRAction.GameMenu))
            {
                Set(Intent.MainMenu, true);
                Log.Info("Game menu button: the game menu");
            }
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
    }
}
