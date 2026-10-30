using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Input.Bindings;
using VRage.Game.Entity.UseObject;
using VRage.Game.Models;
using VRage.ModAPI;
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
    /// A button on the screen is one of the game's controls. The game reads the keys only while its gameplay screen has the
    /// focus, and the panel has it, so a press runs the game's own code that reads that control's key, there and then, once,
    /// with that control the only one pressed (<see cref="Press"/>, <see cref="GameControlCall"/>): the game does what the
    /// key does, with the same checks, sounds and synchronisation.
    /// The Terminal and Inventory tiles open what the hand pointed at when the panel opened (<see cref="OpenedOn"/>): the game
    /// opens what its use ray is on, and at the press the hand points at the wrist. The Relative tile likewise matches the
    /// speed of what the head looked at when the panel opened (<see cref="OpenedLook"/>): the game picks what the head's ray is
    /// on, and at the press the head is turned down to the wrist.
    /// </remarks>
    internal static partial class WristButton
    {
        /// <summary>Debug: no wrist button and no wrist panel (<see cref="Rendering.RenderDebug"/> WristPanel=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The button and the panel are on: not switched off by the debug file or the setting.</summary>
        public static bool Enabled => !Off && VRSettings.WristPanel;

        /// <summary>The hand that wears the panel; the button is on its controller.</summary>
        public const Hand WristHand = Hand.Left;

        /// <summary>What <see cref="Decide"/> says Y does.</summary>
        internal enum Toggle
        {
            None,
            Open,
            Close,
        }

        private static readonly object gate = new object();
        private static Matrix published = Matrix.Identity;
        private static bool publishedTracked;

        // Game thread.
        private static QuickActionsScreen screen;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(WristButton));
        private static readonly FeatureGuard lookGuard = new FeatureGuard(nameof(WristButton) + " look");

        // What the character's use ray was on when the panel opened (OpenedOn), and where on it the ray hit, in its owner's own
        // frame (so a moving grid carries it), for the Terminal and Inventory tiles, which open it with the detector answering
        // with it (DetectorAnswer) while it is still within the game's reach for it.
        private static MyCharacter openedBy;
        private static IMyUseObject openedOn;
        private static Vector3D openedHit;

        // The Relative tile: for the length of its call (Relative), the character it is for and the head matrix the character
        // answers with. Without the head hook (headHooked) the game's pick takes the head as it is at the press.
        private static bool headHooked;
        [ThreadStatic]
        private static MyCharacter lookFor;
        [ThreadStatic]
        private static MatrixD lookWith;

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
        /// The game's input states a tile's press quiets (<see cref="GameControlCall.Reach"/>), and the head the Relative tile's
        /// pick asks for. Each on its own: a part that cannot reach the game must not take the panel down with it.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            PatchRegistry.Apply("game controls", GameControlCall.Reach,
                "Wrist panel (game controls) could not reach the game's input; its tiles for the game's controls do nothing");

            PatchRegistry.Apply("Relative target", () =>
            {
                // The game's pick (MyPlayerCollection.SetDampeningEntity) asks the controlled entity for its head matrix through
                // the mod interface (IMyControllableEntity.GetHeadMatrix(true, true, false, false)), which a character answers in
                // its explicit implementation. For the length of the Relative tile's call, the character answers with the look
                // the panel opened on.
                harmony.Patch(CharacterHeadForInterface(),
                    postfix: new HarmonyMethod(typeof(WristButton), nameof(AnswerHead)));
                headHooked = true;
            }, "Wrist panel (Relative target) could not be hooked; the Relative tile matches what the head looks at as it is pressed");
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

            if (screen != null && Gone(screen))
                screen = null;
            if (screen == null)
            {
                openedBy = null;
                openedOn = null;
            }

            if (!Enabled)
            {
                // Switched off while it is up (the setting, or the debug file): take it away.
                if (screen != null)
                    screen.CloseScreen();
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

        /// <summary>Game thread, when the panel has closed: it is no longer the one up.</summary>
        public static void ScreenClosed(MyGuiScreenBase closed)
        {
            if (ReferenceEquals(screen, closed))
                screen = null;
        }

        /// <summary>What the last press of a game control did: read by the game, or why not (for the drive's report).</summary>
        internal static string LastOutcome { get; set; }

        // ---- The Terminal and Inventory tiles: what was targeted when the panel opened ----

        /// <summary>
        /// Game thread, as the panel opens: what the character's use ray is on (the block the hand points at), and where the ray
        /// hit it, in its owner's frame.
        /// </summary>
        private static void OpenedOn()
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            MyCharacterDetectorComponent detector = character?.GetDetectorComponent();
            openedBy = character;
            openedOn = detector?.UseObject;
            IMyEntity owner = openedOn?.Owner;
            openedHit = owner != null ? Vector3D.Transform(detector.HitPosition, owner.PositionComp.WorldMatrixNormalizedInv) : Vector3D.Zero;
        }

        /// <summary>
        /// The gameplay screen's pass with the Terminal or Inventory control pressed (<paramref name="press"/>): for its length
        /// the detector answers with what the use ray was on when the panel opened (nothing, if it was on nothing: then the game
        /// opens the character's own), so the game's own choice (a block's terminal or inventory, or its own) is made from that.
        /// Not when that target has gone or is out of the game's reach for it now (<see cref="InReach"/>), nor from a seat or
        /// without the panel; then the game decides from what the hand is on now. The press's outcome, and which it was.
        /// </summary>
        private static string OpenTargeted(Sandbox.Game.Entities.IMyControllableEntity controlled, UseActionEnum action, Func<string> press)
        {
            if (!(controlled is MyCharacter character) || character != openedBy)
                return press() + "; the game opens what the controls are on now";
            if (!DetectorAnswer.Hooked)
                return press() + "; the detector's answer could not be hooked, so the game opens what the hand is on now";
            MyCharacterDetectorComponent detector = character.GetDetectorComponent();
            IMyUseObject now = detector?.UseObject;
            IMyEntity owner = openedOn?.Owner;
            if (openedOn != null && (owner == null || owner.Closed || owner.MarkedForClose))
                return press() + $"; what was targeted when the panel opened is gone, so the game opens what the hand is on now ({Describe(now)})";
            if (openedOn != null && !InReach(detector, openedOn, owner, out double metres))
                return press() + $"; what was targeted when the panel opened is {metres:F1} m away now, past the game's reach for it " +
                       $"({openedOn.InteractiveDistance:F1} m), so the game opens what the hand is on now ({Describe(now)})";
            string outcome = null;
            DetectorAnswer.With(openedOn, () => outcome = press());
            string opened = openedOn == null ? "the character's own (nothing was targeted)"
                : openedOn.IsActionSupported(action) ? Describe(openedOn) : $"the character's own ({Describe(openedOn)} has no {action})";
            return $"{outcome}; {opened}, targeted when the panel opened; the hand is on {Describe(now)} now";
        }

        /// <summary>
        /// The game's reach for a use object: its own interactive distance from where the character's detector looks from, to
        /// where the ray hit it (MyCharacterShapecastDetectorComponent.cs:257). Here the hit taken as the panel opened, carried
        /// with its owner since.
        /// </summary>
        private static bool InReach(MyCharacterDetectorComponent detector, IMyUseObject use, IMyEntity owner, out double metres)
        {
            metres = double.PositiveInfinity;
            if (detector == null)
                return false;
            Vector3D hit = Vector3D.Transform(openedHit, owner.PositionComp.WorldMatrixRef);
            metres = Vector3D.Distance(detector.StartPosition, hit);
            return metres < use.InteractiveDistance;
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
            if (!lookGuard.On)
                return null;
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
                lookGuard.Failed(e, "Wrist panel: could not take where the head looked as the panel opened");
                return null;
            }
        }

        /// <summary>The head matrix exactly as MyPlayerCollection.SetDampeningEntity asks for it: GetHeadMatrix(includeY: true) through the mod interface.</summary>
        private static MatrixD HeadForPick(MyCharacter character) =>
            ((VRage.Game.ModAPI.Interfaces.IMyControllableEntity)character).GetHeadMatrix(true, true, false, false);

        /// <summary>
        /// The gameplay screen's pass with the Relative control pressed (<paramref name="press"/>), whose handler has the game
        /// pick what to match: for its length the character's head matrix is the one taken as the panel opened, moved along
        /// with the character since (so a drifting player's ray still starts at the head), so the game's own ray, its own hit
        /// and its own choice are made from that look. Where this game runs the pick (single player, or the host) it runs inside
        /// the call; a client's goes to the server, which picks from the head the server has. Without a look (a seat's panel,
        /// the actions wheel) or the head hook, the game picks as it is. The press's outcome, and what was picked.
        /// </summary>
        private static string Relative(Sandbox.Game.Entities.IMyControllableEntity controlled, OpenedLook look, Func<string> press)
        {
            MyCharacter character = look?.Character;
            if (character == null || !headHooked || !ReferenceEquals(controlled, character) || character.Closed || character.MarkedForClose)
            {
                string outcome = press();
                return outcome + (character == null ? "; the game picks from where the controls look now"
                    : !headHooked ? "; the head could not be hooked, so the game picks from where the head looks now"
                    : "; the character the panel opened on is not the one picking, so the game picks from where the head looks now");
            }
            MatrixD now = HeadForPick(character);
            MatrixD then = look.Head;
            then.Translation += character.PositionComp.GetPosition() - look.At;
            lookWith = then;
            lookFor = character;
            string read;
            try
            {
                read = press();
            }
            finally
            {
                lookFor = null;
            }
            double seconds = (Stopwatch.GetTimestamp() - look.Timestamp) / (double)Stopwatch.Frequency;
            double moved = Vector3D.Distance(character.PositionComp.GetPosition(), look.At);
            double turned = MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(
                Vector3D.Dot(Vector3D.Normalize(then.Forward), Vector3D.Normalize(now.Forward)), -1.0, 1.0)));
            string picked = Sandbox.Game.Multiplayer.Sync.IsServer
                ? $"the game picked {Named(character.RelativeDampeningEntity)}"
                : "the server picks, from the head it has";
            return $"{read}; {picked}; ray as the panel opened ({seconds:F1} s ago, moved {moved:F1} m) is on {RayOn(character, then)}; "
                   + $"ray now ({turned:F0} deg away) is on {RayOn(character, now)}";
        }

        private static void AnswerHead(MyCharacter __instance, ref MatrixD __result)
        {
            if (lookFor != null && ReferenceEquals(__instance, lookFor))
                __result = lookWith;
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
    }
}
