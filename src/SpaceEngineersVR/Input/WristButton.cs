using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input.Bindings;
using VRage.Game.Entity.UseObject;
using VRage.Game.Models;
using VRage.Input;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Y on the left controller, on foot and on the jetpack: opens the quick-actions screen (<see cref="QuickActionsScreen"/>)
    /// on the left wrist (<see cref="Rendering.WorldMenu"/>); Y again closes it. In a seat Y already has a job (the toolbar,
    /// and held it leaves the seat: <see cref="VRControls"/>), so it does nothing there. Looking at the astronaut's gauntlet
    /// does not open it: the gauntlet's screen shows the status bars (<see cref="Rendering.WristStatus"/>) and brightens.
    /// </summary>
    /// <remarks>
    /// A button on the screen does not do its action itself: the actions are the game's own controls, which the gameplay
    /// screen reads in its input pass and only while it has the focus. So the button closes the screen and hands the
    /// control to <see cref="Press"/>; once the screen is gone, the gameplay screen's next input pass is told that control
    /// was pressed (MyGuiScreenGamePlay.HandleInput is wrapped, and the game's questions about that one control
    /// - MyVRageInput.IsNewGameControlPressed, MyControllerHelper.IsControl - answer yes during the pass), exactly once.
    /// The game then does what it does for the key: the same checks, sounds, synchronisation and screens.
    /// The Terminal and Inventory tiles open what the hand pointed at when the panel opened (<see cref="OpenedOn"/>): the game
    /// opens what its use ray is on as the key is read, and by then the hand points at the wrist. The Relative tile likewise
    /// matches the speed of what the head looked at when the panel opened (<see cref="OpenedLook"/>): the game picks what the
    /// head's ray is on as the key is read, and by then the head is turned down to the wrist.
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
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(WristButton));
        private static int errors;

        /// <summary>The Terminal and Inventory tiles' target may be this far from where the character was when the panel opened.</summary>
        private const double OpenedOnMetres = 3.0;

        // What the character's use ray was on when the panel opened (OpenedOn), for the Terminal and Inventory tiles; and,
        // for the length of the game's ShowTerminal or ShowInventory, the detector answers with it (AnswerUseObject).
        private static MyCharacter openedBy;
        private static IMyUseObject openedOn;
        private static Vector3D openedAt;
        [ThreadStatic]
        private static bool answering;
        [ThreadStatic]
        private static IMyUseObject answerWith;

        // The Relative tile: the look the panel opened on, handed over with the press (Press); and, for the length of the game's
        // pick (MyPlayerCollection.SetDampeningEntity), the character it is for, the head matrix the character answers with,
        // and the head's own at that moment (for the log).
        private static OpenedLook pendingLook;
        [ThreadStatic]
        private static MyCharacter lookFor;
        [ThreadStatic]
        private static MatrixD lookWith;
        [ThreadStatic]
        private static MatrixD lookNow;

        /// <summary>How far the game's relative-dampening ray reaches (MyPlayerCollection.SetDampeningEntity: forward * 1000).</summary>
        private const double RelativeRayMetres = 1000.0;

        /// <summary>
        /// Where the head looked as the wrist panel opened, on foot or on the jetpack: the head matrix exactly as the game's
        /// relative-dampening pick asks for it, where the character was, and when.
        /// </summary>
        internal sealed class OpenedLook
        {
            internal MyCharacter Character;
            internal MatrixD Head;
            internal Vector3D At;
            internal long Timestamp;
        }

        /// <summary>
        /// The quick-actions screen is hooked into the game's input here. Each hook on its own, like the hand input's: a
        /// button that cannot be hooked must not take the rendering patches down with it.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            PatchRegistry.Apply("input pass", () =>
            {
                // The gameplay screen's input pass (it reads every on-foot control, and the cube builder's, from here).
                harmony.Patch(AccessTools.Method(typeof(MyGuiScreenGamePlay), nameof(MyGuiScreenGamePlay.HandleInput), new[] { typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(WristButton), nameof(BeforeGameplayInput)),
                    finalizer: new HarmonyMethod(typeof(WristButton), nameof(AfterGameplayInput)));
            }, "Wrist panel (input pass) could not be hooked; its buttons will not reach the game");

            PatchRegistry.Apply("new pressed", () =>
            {
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.IsNewGameControlPressed)),
                    postfix: new HarmonyMethod(typeof(WristButton), nameof(NewPressed)));
            }, "Wrist panel (new pressed) could not be hooked; its buttons will not reach the game");

            PatchRegistry.Apply("controls", () =>
            {
                // The helper asks the input above unless a gamepad was used last; this answers in that case too.
                harmony.Patch(AccessTools.Method(typeof(MyControllerHelper), nameof(MyControllerHelper.IsControl)),
                    postfix: new HarmonyMethod(typeof(WristButton), nameof(Control)));
            }, "Wrist panel (controls) could not be hooked; its buttons may not reach the game");

            PatchRegistry.Apply("Terminal and Inventory target", () =>
            {
                // The Terminal and Inventory tiles: the game opens what the detector's use object is when the key is read.
                // Both detector classes answer through UseObject (the closest one overrides it), as for FingerPress.
                Type closest = AccessTools.TypeByName("Sandbox.Game.Entities.Character.Components.MyCharacterClosestDetectorComponent")
                               ?? throw new MissingMemberException("MyCharacterClosestDetectorComponent");
                var getters = new[]
                {
                    AccessTools.PropertyGetter(typeof(MyCharacterDetectorComponent), nameof(MyCharacterDetectorComponent.UseObject))
                        ?? throw new MissingMemberException(nameof(MyCharacterDetectorComponent), "UseObject"),
                    AccessTools.DeclaredProperty(closest, nameof(MyCharacterDetectorComponent.UseObject))?.GetGetMethod(true)
                        ?? throw new MissingMemberException(closest.Name, "UseObject"),
                };
                foreach (var getter in getters)
                    harmony.Patch(getter, postfix: new HarmonyMethod(typeof(WristButton), nameof(AnswerUseObject)));
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.ShowTerminal), Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(WristButton), nameof(BeforeShowTerminal)),
                    finalizer: new HarmonyMethod(typeof(WristButton), nameof(AfterShow)));
                harmony.Patch(AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.ShowInventory), Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(WristButton), nameof(BeforeShowInventory)),
                    finalizer: new HarmonyMethod(typeof(WristButton), nameof(AfterShow)));
            }, "Wrist panel (Terminal and Inventory target) could not be hooked; those tiles open what the hand points at as the panel closes");

            PatchRegistry.Apply("Relative target", () =>
            {
                // The Relative tile: the game's pick (MyPlayerCollection.SetDampeningEntity) asks the controlled entity for its
                // head matrix through the mod interface (IMyControllableEntity.GetHeadMatrix(true, true, false, false)), which
                // a character answers in its explicit implementation. For the length of that one pick, the character answers
                // with the look the panel opened on. The head is hooked first: without it the pick is left alone.
                harmony.Patch(CharacterHeadForInterface(),
                    postfix: new HarmonyMethod(typeof(WristButton), nameof(AnswerHead)));
                harmony.Patch(AccessTools.Method(typeof(Sandbox.Game.Multiplayer.MyPlayerCollection), nameof(Sandbox.Game.Multiplayer.MyPlayerCollection.SetDampeningEntity))
                        ?? throw new MissingMethodException("MyPlayerCollection", "SetDampeningEntity"),
                    prefix: new HarmonyMethod(typeof(WristButton), nameof(BeforeSetDampening)),
                    finalizer: new HarmonyMethod(typeof(WristButton), nameof(AfterSetDampening)));
            }, "Wrist panel (Relative target) could not be hooked; the Relative tile matches what the head looks at as the panel closes");
        }

        /// <summary>MyCharacter's explicit VRage.Game.ModAPI.Interfaces.IMyControllableEntity.GetHeadMatrix, which the game's pick calls.</summary>
        private static MethodInfo CharacterHeadForInterface()
        {
            Type face = typeof(VRage.Game.ModAPI.Interfaces.IMyControllableEntity);
            InterfaceMapping map = typeof(MyCharacter).GetInterfaceMap(face);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == nameof(VRage.Game.ModAPI.Interfaces.IMyControllableEntity.GetHeadMatrix))
                    return map.TargetMethods[i];
            }
            throw new MissingMethodException(nameof(MyCharacter), face.FullName + ".GetHeadMatrix");
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
            if (!guard.On)
                return;
            try
            {
                Frame();
            }
            catch (Exception e)
            {
                guard.Failed(e, "Wrist panel failed this frame");
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
                LastOutcome = "dropped";
            }

            if (screen != null && Gone(screen))
                screen = null;
            if (screen == null && stage == Stage.None)
            {
                openedBy = null;
                openedOn = null;
            }

            if (!Enabled)
            {
                // Switched off while it is up (the setting, or the debug file): take it away.
                if (screen != null)
                    screen.CloseScreen();
                stage = Stage.None;
                return;
            }

            if (ControllerBindings.Started(VRAction.WristPanel, WristHand == Hand.Right))
            {
                Toggle toggle = Decide(true, screen != null, VRControls.Context, VRControls.CameraView, MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay);
                if (toggle == Toggle.Close)
                {
                    Log.Info("Wrist panel: closed (Y)");
                    screen.CloseScreen();
                }
                else if (toggle == Toggle.Open)
                {
                    OpenedOn();
                    screen = new QuickActionsScreen();
                    MyGuiSandbox.AddScreen(screen);
                    Log.Info($"Wrist panel: opened (Y), {screen.TileCount} tiles");
                }
            }
        }

        /// <summary>
        /// Game thread, from the tools wheel as Y is tapped in a seat: while the player reads the gauntlet's screen (turned up and
        /// looked at: <see cref="Rendering.WristStatus.LookedAt"/>) the tap opens the wrist panel on its seated page, as Y opens
        /// it on foot, and Y again closes it; anywhere else the tap is the tools wheel's. True when it opened the panel.
        /// </summary>
        internal static bool OpenSeatedAtWrist()
        {
            if (!Enabled || screen != null || !Rendering.WristStatus.LookedAt)
                return false;
            OpenedOn();
            screen = QuickActionsScreen.OpenSeated("Y at the wrist");
            return true;
        }

        /// <summary>
        /// What a Y press does. It closes the panel if it is up, whatever the player is doing; it opens it only on foot or on
        /// the jetpack (a seat's Y is the toolbar and leaving the seat), not while a camera block is the view (Y held there
        /// leaves the camera, <see cref="VRControls"/>' use hold) and when no menu is open already.
        /// </summary>
        internal static Toggle Decide(bool newPressed, bool panelOpen, VRContext context, bool cameraView, bool gameplayHasFocus)
        {
            if (!newPressed)
                return Toggle.None;
            if (panelOpen)
                return Toggle.Close;
            bool free = context == VRContext.OnFoot || context == VRContext.Jetpack;
            return free && !cameraView && gameplayHasFocus ? Toggle.Open : Toggle.None;
        }

        private static bool Gone(QuickActionsScreen s) => s.State == MyGuiScreenState.CLOSING || s.State == MyGuiScreenState.CLOSED;

        // ---- A button on the screen pressing the game's control ----

        /// <summary>
        /// Game thread, from a button: the screen is closing, and the next gameplay input pass gets this control. For the
        /// Relative tile, <paramref name="look"/> is where the head looked as the panel opened, which the game's pick then uses.
        /// </summary>
        public static void Press(MyStringId control, string label, OpenedLook look = null)
        {
            pending = control;
            pendingLabel = label;
            pendingLook = look;
            stage = Stage.Waiting;
            until = Stopwatch.GetTimestamp() + (long)(PressSeconds * Stopwatch.Frequency);
            Log.Info($"Wrist panel: {label}");
        }

        /// <summary>
        /// Game thread, when the screen the button was on has closed (the wrist panel, or the actions wheel for its entries that
        /// press a game control: <see cref="GameControlEntries"/>). A control waiting for it can now go to the gameplay screen.
        /// </summary>
        public static void ScreenClosed(MyGuiScreenBase closed)
        {
            if (ReferenceEquals(screen, closed))
                screen = null;
            if (stage == Stage.Waiting)
            {
                stage = Stage.Armed;
                served = false;
                until = Stopwatch.GetTimestamp() + (long)(PressSeconds * Stopwatch.Frequency);
            }
        }

        /// <summary>A button's control is on its way to the game (the drive's quickmenu press waits for it).</summary>
        internal static bool Busy => stage != Stage.None;

        /// <summary>What became of the last control handed over: read by the game, not asked about, or dropped (for the drive's report).</summary>
        internal static string LastOutcome { get; set; }

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
            LastOutcome = served ? "read by the game" : "not asked by the game";
            if (served)
                Log.Info($"Wrist panel: '{pendingLabel}' read by the game");
            else
                Log.Warn($"Wrist panel: the game did not ask about '{pendingLabel}' in its input pass (a screen open, or the control not allowed here)");
        }

        private static void BeforeGameplayInput() => Begin();

        // ---- The Terminal and Inventory tiles: what was targeted when the panel opened ----

        /// <summary>Game thread, as the panel opens: what the character's use ray is on (the block the hand points at), and where the character is.</summary>
        private static void OpenedOn()
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            openedBy = character;
            openedOn = character?.GetDetectorComponent()?.UseObject;
            openedAt = character?.PositionComp.GetPosition() ?? Vector3D.Zero;
        }

        private static void BeforeShowTerminal(MyCharacter __instance) => Answer(__instance, MyControlsSpace.TERMINAL, UseActionEnum.OpenTerminal);

        private static void BeforeShowInventory(MyCharacter __instance) => Answer(__instance, MyControlsSpace.INVENTORY, UseActionEnum.OpenInventory);

        /// <summary>
        /// The game's ShowTerminal or ShowInventory, run for the tile's control: for its length the detector answers with what
        /// the use ray was on when the panel opened (nothing, if it was on nothing: then the game opens the character's own),
        /// so the game's own choice (a block's terminal or inventory, or its own) is made from that. Not when the character has
        /// moved off since (<see cref="OpenedOnMetres"/>) or the target has gone; then the game decides from where it is now.
        /// </summary>
        private static void Answer(MyCharacter character, MyStringId control, UseActionEnum action)
        {
            answering = false;
            answerWith = null;
            try
            {
                if (!inPass || stage != Stage.Armed || pending != control || character == null || character != openedBy)
                    return;
                IMyUseObject now = character.GetDetectorComponent()?.UseObject;
                IMyEntity owner = openedOn?.Owner;
                double moved = Vector3D.Distance(character.PositionComp.GetPosition(), openedAt);
                if (openedOn != null && (owner == null || owner.Closed || owner.MarkedForClose))
                {
                    Log.Info($"Wrist panel: {pendingLabel}: what was targeted when the panel opened is gone; the game opens what the hand is on now ({Describe(now)})");
                    return;
                }
                if (moved > OpenedOnMetres)
                {
                    Log.Info($"Wrist panel: {pendingLabel}: moved {moved:F1} m since the panel opened; the game opens what the hand is on now ({Describe(now)})");
                    return;
                }
                answering = true;
                answerWith = openedOn;
                string opens = openedOn == null ? "the character's own (nothing was targeted)"
                    : openedOn.IsActionSupported(action) ? Describe(openedOn) : $"the character's own ({Describe(openedOn)} has no {action})";
                Log.Info($"Wrist panel: {pendingLabel} opens {opens}, targeted when the panel opened; the hand is on {Describe(now)} now");
            }
            catch (Exception e)
            {
                answering = false;
                answerWith = null;
                if (errors++ < 3)
                    Log.Error(e, "Wrist panel: could not give the game what was targeted when the panel opened");
            }
        }

        private static Exception AfterShow(Exception __exception)
        {
            answering = false;
            answerWith = null;
            return __exception;
        }

        private static void AnswerUseObject(ref IMyUseObject __result)
        {
            if (answering)
                __result = answerWith;
        }

        private static string Describe(IMyUseObject use)
        {
            IMyEntity owner = use?.Owner;
            if (owner == null)
                return "nothing";
            if (owner is MyTerminalBlock block)
                return $"'{block.CustomName}' ({block.BlockDefinition.Id.SubtypeName})";
            return string.IsNullOrEmpty(owner.DisplayName) ? owner.GetType().Name : $"'{owner.DisplayName}' ({owner.GetType().Name})";
        }

        // ---- The Relative tile: where the head looked when the panel opened ----

        /// <summary>
        /// Game thread, as the panel is made (Y, or the drive's quickmenu open): where the head looks now, as the game's
        /// relative-dampening pick will ask for it. Null unless the player controls a character (on foot or on the jetpack):
        /// a seat's panel leaves the game's ray as it is.
        /// </summary>
        internal static OpenedLook CaptureLook()
        {
            try
            {
                if (!(MySession.Static?.ControlledEntity is MyCharacter character) || character.PositionComp == null)
                    return null;
                return new OpenedLook
                {
                    Character = character,
                    Head = HeadForPick(character),
                    At = character.PositionComp.GetPosition(),
                    Timestamp = Stopwatch.GetTimestamp(),
                };
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Wrist panel: could not take where the head looked as the panel opened");
                return null;
            }
        }

        /// <summary>The head matrix exactly as MyPlayerCollection.SetDampeningEntity asks for it: GetHeadMatrix(includeY: true) through the mod interface.</summary>
        private static MatrixD HeadForPick(MyCharacter character) =>
            ((VRage.Game.ModAPI.Interfaces.IMyControllableEntity)character).GetHeadMatrix(true, true, false, false);

        /// <summary>
        /// The game's relative-dampening pick, run for the Relative tile's control: for its length the character's head
        /// matrix is the one taken as the panel opened, moved along with the character since (so a drifting player's ray
        /// still starts at the head), so the game's own ray, its own hit and its own choice are made from that look.
        /// Not for any other pick: another control, another entity, or a press without a look (a seat's panel).
        /// </summary>
        private static void BeforeSetDampening(long controlledEntityId)
        {
            lookFor = null;
            try
            {
                if (!inPass || stage != Stage.Armed || pending != MyControlsSpace.DAMPING_RELATIVE)
                    return;
                OpenedLook look = pendingLook;
                if (look == null)
                    return;
                MyCharacter character = look.Character;
                if (character == null || character.Closed || character.MarkedForClose || character.EntityId != controlledEntityId)
                {
                    Log.Info($"Wrist panel: {pendingLabel}: the character the panel opened on is not the one picking; the game picks from where the head looks now");
                    return;
                }
                lookNow = HeadForPick(character);
                MatrixD then = look.Head;
                then.Translation += character.PositionComp.GetPosition() - look.At;
                lookWith = then;
                lookFor = character;
            }
            catch (Exception e)
            {
                lookFor = null;
                if (errors++ < 3)
                    Log.Error(e, "Wrist panel: could not give the game where the head looked when the panel opened");
            }
        }

        private static void AnswerHead(MyCharacter __instance, ref MatrixD __result)
        {
            if (lookFor != null && ReferenceEquals(__instance, lookFor))
                __result = lookWith;
        }

        /// <summary>The pick is over: what the game chose, and what each ray is on, logged once.</summary>
        private static Exception AfterSetDampening(Exception __exception)
        {
            MyCharacter character = lookFor;
            lookFor = null;
            if (character == null)
                return __exception;
            try
            {
                OpenedLook look = pendingLook;
                double seconds = look == null ? 0.0 : (Stopwatch.GetTimestamp() - look.Timestamp) / (double)Stopwatch.Frequency;
                double moved = look == null ? 0.0 : Vector3D.Distance(character.PositionComp.GetPosition(), look.At);
                double turned = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(
                    Vector3D.Dot(Vector3D.Normalize(lookWith.Forward), Vector3D.Normalize(lookNow.Forward)), -1.0, 1.0)));
                Log.Info($"Wrist panel: {pendingLabel}: the game picked {Named(character.RelativeDampeningEntity)}"
                         + $"{(__exception != null ? " (the pick threw)" : string.Empty)}; "
                         + $"ray as the panel opened ({seconds:F1} s ago, moved {moved:F1} m) is on {RayOn(character, lookWith)}; "
                         + $"ray now ({turned:F0} deg away) is on {RayOn(character, lookNow)}");
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Wrist panel: could not report the Relative pick");
            }
            return __exception;
        }

        /// <summary>For the log: what the game's relative-dampening ray from this head is on, by the game's own query (SetDampeningEntity's), and how far.</summary>
        private static string RayOn(MyCharacter character, MatrixD head)
        {
            Vector3D from = head.Translation;
            LineD line = new LineD(from, from + head.Forward * RelativeRayMetres);
            MyIntersectionResultLineTriangleEx? hit = Sandbox.Game.Entities.MyEntities.GetIntersectionWithLine(ref line, character, character.GetTopMostParent(),
                ignoreChildren: false, ignoreFloatingObjects: false);
            if (!hit.HasValue || hit.Value.Entity == null)
                return "nothing within 1 km";
            return $"{Named(hit.Value.Entity.GetTopMostParent())} at {Vector3D.Distance(from, hit.Value.IntersectionPointInWorldSpace):F1} m";
        }

        private static string Named(IMyEntity entity)
        {
            if (entity == null)
                return "none";
            string name = string.IsNullOrWhiteSpace(entity.DisplayName) ? entity.GetType().Name : entity.DisplayName;
            return $"'{name}' (EntityId {entity.EntityId})";
        }

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
