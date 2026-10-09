using System;
using System.Diagnostics;
using HarmonyLib;
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
    /// <summary>
    /// Y on the left controller, on foot and on the jetpack: opens the quick-actions screen (<see cref="QuickActionsScreen"/>)
    /// on the left wrist (<see cref="Rendering.WorldMenu"/>); Y again closes it. In a seat Y already has a job (the toolbar,
    /// and held it leaves the seat: <see cref="VRControls"/>), so it does nothing there. Looking at the astronaut's gauntlet
    /// opens the same panel too, and looking away closes it (<see cref="WristLook"/>, <see cref="LookFrame"/>).
    /// </summary>
    /// <remarks>
    /// A button on the screen does not do its action itself: the actions are the game's own controls, which the gameplay
    /// screen reads in its input pass and only while it has the focus. So the button closes the screen and hands the
    /// control to <see cref="Press"/>; once the screen is gone, the gameplay screen's next input pass is told that control
    /// was pressed (MyGuiScreenGamePlay.HandleInput is wrapped, and the game's questions about that one control
    /// - MyVRageInput.IsNewGameControlPressed, MyControllerHelper.IsControl - answer yes during the pass), exactly once.
    /// The game then does what it does for the key: the same checks, sounds, synchronisation and screens.
    /// </remarks>
    internal static class WristButton
    {
        /// <summary>Debug: no wrist button and no wrist panel (<see cref="Rendering.RenderDebug"/> WristPanel=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The button and the panel are on: not switched off by the debug file or the setting.</summary>
        public static bool Enabled => !Off && VRSettings.WristPanel;

        /// <summary>The hand that wears the panel; the button is on its controller.</summary>
        public const Hand WristHand = Hand.Left;

        /// <summary>A control handed over for the game stays good this long (seconds): a paused or unfocused game does not read it.</summary>
        public const double PressSeconds = 2.0;

        /// <summary>What <see cref="Decide"/> says Y does.</summary>
        internal enum Toggle
        {
            None,
            Open,
            Close,
        }

        private enum Stage
        {
            None,

            /// <summary>A button was pressed; its screen is closing.</summary>
            Waiting,

            /// <summary>The screen is gone; the gameplay screen's next input pass gets the control.</summary>
            Armed,
        }

        private static readonly object gate = new object();
        private static Matrix published = Matrix.Identity;
        private static bool publishedTracked;

        // Game thread (the input pass flag is read by the questions about controls, wherever they come from).
        private static QuickActionsScreen screen;
        private static Stage stage;
        private static MyStringId pending;
        private static string pendingLabel;
        private static long until;
        private static volatile bool inPass;
        private static bool served;
        private static int errors;

        /// <summary>
        /// The quick-actions screen is hooked into the game's input here. Each hook on its own, like the hand input's: a
        /// button that cannot be hooked must not take the rendering patches down with it.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                // The gameplay screen's input pass (it reads every on-foot control, and the cube builder's, from here).
                harmony.Patch(AccessTools.Method(typeof(MyGuiScreenGamePlay), nameof(MyGuiScreenGamePlay.HandleInput), new[] { typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(WristButton), nameof(BeforeGameplayInput)),
                    finalizer: new HarmonyMethod(typeof(WristButton), nameof(AfterGameplayInput)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Wrist panel (input pass) could not be hooked; its buttons will not reach the game");
            }

            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsNewGameControlPressed)),
                    postfix: new HarmonyMethod(typeof(WristButton), nameof(NewPressed)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Wrist panel (new pressed) could not be hooked; its buttons will not reach the game");
            }

            try
            {
                // The helper asks the input above unless a gamepad was used last; this answers in that case too.
                harmony.Patch(AccessTools.Method(typeof(MyControllerHelper), nameof(MyControllerHelper.IsControl)),
                    postfix: new HarmonyMethod(typeof(WristButton), nameof(Control)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Wrist panel (controls) could not be hooked; its buttons may not reach the game");
            }
        }

        /// <summary>
        /// Render or game thread: the wrist hand's grip pose in the tracking space. With a headset it is the freshest the
        /// headset published (the panel is placed as the frame is composed, and must not trail the hand by a game frame);
        /// otherwise, and with the desktop hands, it is the frame's snapshot. False when the hand is not tracked.
        /// </summary>
        public static bool TryGetWristGrip(out Matrix grip)
        {
            IVRInputSource headset = VRInput.Headset;
            if (headset != null && headset.Active)
            {
                headset.ReadNow(out HandState left, out HandState right);
                HandState state = WristHand == Hand.Left ? left : right;
                grip = state.Grip;
                return state.Tracked;
            }
            lock (gate)
            {
                grip = published;
                return publishedTracked;
            }
        }

        /// <summary>Game thread, once per frame after the hands were read (InputPatches.FrameStart).</summary>
        public static void Update()
        {
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Wrist panel failed this frame");
            }
        }

        private static void Frame()
        {
            HandState hand = VRInput.Get(WristHand);
            lock (gate)
            {
                published = hand.Grip;
                publishedTracked = VRInput.Active && hand.Tracked;
            }

            if (stage != Stage.None && Stopwatch.GetTimestamp() > until)
            {
                Log.Warn($"Wrist panel: '{pendingLabel}' was not taken by the game in {PressSeconds} s; dropped");
                stage = Stage.None;
            }

            if (screen != null && Gone(screen))
                screen = null;

            if (!Enabled)
            {
                // Switched off while it is up (the setting, or the debug file): take it away.
                if (screen != null)
                    screen.CloseScreen();
                stage = Stage.None;
                look = default;
                openedByLook = false;
                return;
            }

            if (VRInput.IsNewPressed(WristHand, VRButtons.Y))
            {
                Toggle toggle = Decide(true, screen != null, VRControls.Context, MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay);
                if (toggle == Toggle.Close)
                {
                    Log.Info("Wrist panel: closed (Y)");
                    screen.CloseScreen();
                    openedByLook = false;
                }
                else if (toggle == Toggle.Open)
                {
                    screen = new QuickActionsScreen();
                    MyGuiSandbox.AddScreen(screen);
                    openedByLook = false;
                    Log.Info($"Wrist panel: opened (Y), {QuickActionsScreen.Actions.Length} actions");
                }
            }
            LookFrame();
        }

        // ---- Looking at the gauntlet (WristLook) ----

        private static WristLook.State look;
        private static bool openedByLook;
        private static long lookAt;

        /// <summary>The Left-handed setting stops the look (the left hand points the laser): said once.</summary>
        private static bool leftHandedNoted;

        /// <summary>
        /// Game thread, once per frame after Y: the gauntlet on the astronaut's left forearm opens the panel when it is
        /// looked at and closes it when it is not (<see cref="WristLook"/>). A panel Y opened is never touched.
        /// </summary>
        private static void LookFrame()
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = lookAt == 0 ? 0f : (float)((now - lookAt) / (double)Stopwatch.Frequency);
            lookAt = now;

            // The panel the look opened went away by another road (a button on it, Y, the game): the rule hears of it below.
            if (openedByLook && screen == null)
                openedByLook = false;
            bool enabled = WristLook.Enabled;
            if (!enabled && !openedByLook)
            {
                look = default;
                return;
            }

            HandState hand = VRInput.Get(WristHand);
            bool measured = false;
            float gaze = 180f, face = 180f;
            // No character (the main menu, a loading world): no gauntlet to look at, and no head to ask.
            if (VRInput.Active && hand.Tracked && MySession.Static?.LocalCharacter != null && GameHead.Take())
                measured = WristLook.Measure(HandIK.PalmTarget(WristHand, hand), GameHead.TrackingPose, out gaze, out face);

            var inputs = new WristLook.Inputs
            {
                Enabled = enabled,
                LeftHanded = VRSettings.DominantHand == WristHand,
                Context = VRControls.Context,
                GameplayFocus = MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay,
                PanelUp = screen != null && openedByLook,
                OtherPanelUp = screen != null && !openedByLook,
                // The grip is how every hold of the hand starts (GripClaim's owners, the toolbar wheel, a fist).
                HandBusy = VRInput.IsPressed(WristHand, VRButtons.Grip),
                ArmFollows = HandIK.ArmFollows(WristHand),
                Measured = measured,
                Gaze = gaze,
                Face = face,
            };
            if (inputs.LeftHanded && enabled && !leftHandedNoted && measured && gaze <= WristLook.ShowGazeDegrees)
            {
                leftHandedNoted = true;
                Log.Info("Wrist panel: not opened by looking at the gauntlet with Left-handed on (the left hand points the laser and cannot press its own wrist); Y still opens it");
            }

            WristLook.Change change = WristLook.Decide(ref look, inputs, seconds);
            if (change == WristLook.Change.Show)
            {
                screen = new QuickActionsScreen();
                MyGuiSandbox.AddScreen(screen);
                openedByLook = true;
                LookNote($"Wrist panel: opened by looking at the gauntlet (gaze {gaze:F0} deg off it, its face {face:F0} deg from the eyes), {QuickActionsScreen.Actions.Length} actions");
            }
            else if (change == WristLook.Change.Hide)
            {
                if (screen != null)
                    screen.CloseScreen();
                openedByLook = false;
                LookNote($"Wrist panel: closed, {look.Why} (gaze {gaze:F0} deg, face {face:F0} deg)");
            }
        }

        private static long lookWindowStart;
        private static int lookLines, lookSkipped;

        /// <summary>Look lines in the log: five in ten seconds at most, and the lines left out are counted in the next.</summary>
        private static void LookNote(string text)
        {
            long ms = Environment.TickCount;
            if (lookLines == 0 || ms - lookWindowStart >= 10000 || ms < lookWindowStart)
            {
                lookWindowStart = ms;
                lookLines = 0;
            }
            if (lookLines >= 5)
            {
                lookSkipped++;
                return;
            }
            lookLines++;
            Log.Info(lookSkipped > 0 ? $"{text} (+{lookSkipped} look lines not logged)" : text);
            lookSkipped = 0;
        }

        /// <summary>
        /// What a Y press does. It closes the panel if it is up, whatever the player is doing; it opens it only on foot or on
        /// the jetpack (a seat's Y is the toolbar and leaving the seat) and when no menu is open already.
        /// </summary>
        internal static Toggle Decide(bool newPressed, bool panelOpen, VRContext context, bool gameplayHasFocus)
        {
            if (!newPressed)
                return Toggle.None;
            if (panelOpen)
                return Toggle.Close;
            bool free = context == VRContext.OnFoot || context == VRContext.Jetpack;
            return free && gameplayHasFocus ? Toggle.Open : Toggle.None;
        }

        private static bool Gone(QuickActionsScreen s) => s.State == MyGuiScreenState.CLOSING || s.State == MyGuiScreenState.CLOSED;

        // ---- A button on the screen pressing the game's control ----

        /// <summary>Game thread, from a button: the screen is closing, and the next gameplay input pass gets this control.</summary>
        public static void Press(MyStringId control, string label)
        {
            pending = control;
            pendingLabel = label;
            stage = Stage.Waiting;
            until = Stopwatch.GetTimestamp() + (long)(PressSeconds * Stopwatch.Frequency);
            Log.Info($"Wrist panel: {label}");
        }

        /// <summary>Game thread, when the screen has closed. A control waiting for it can now go to the gameplay screen.</summary>
        public static void ScreenClosed(QuickActionsScreen closed)
        {
            if (screen == closed)
                screen = null;
            if (stage == Stage.Waiting)
            {
                stage = Stage.Armed;
                served = false;
                until = Stopwatch.GetTimestamp() + (long)(PressSeconds * Stopwatch.Frequency);
            }
        }

        /// <summary>The game is asking about this control during the input pass the handed-over control is for.</summary>
        internal static bool Wants(MyStringId control) => inPass && stage == Stage.Armed && control == pending;

        internal static void Begin()
        {
            inPass = true;
            served = false;
        }

        /// <summary>The input pass is over: a handed-over control is delivered (or not) just this once.</summary>
        internal static void End()
        {
            inPass = false;
            if (stage != Stage.Armed)
                return;
            stage = Stage.None;
            if (served)
                Log.Info($"Wrist panel: '{pendingLabel}' read by the game");
            else
                Log.Warn($"Wrist panel: the game did not ask about '{pendingLabel}' in its input pass (a screen open, or the control not allowed here)");
        }

        private static void BeforeGameplayInput() => Begin();

        // A finalizer, so that an exception out of the pass cannot leave the flag set; the exception goes on unchanged.
        private static Exception AfterGameplayInput(Exception __exception)
        {
            End();
            return __exception;
        }

        private static void NewPressed(MyVRageInput __instance, MyStringId controlId, ref bool __result)
        {
            if (!__result && Wants(controlId) && !__instance.IsControlBlocked(controlId))
            {
                served = true;
                __result = true;
            }
        }

        private static void Control(MyStringId controlId, MyControlStateType type, bool joystickOnly, ref bool __result)
        {
            if (!__result && !joystickOnly && type == MyControlStateType.NEW_PRESSED && Wants(controlId) && !MyInput.Static.IsControlBlocked(controlId))
            {
                served = true;
                __result = true;
            }
        }
    }
}
