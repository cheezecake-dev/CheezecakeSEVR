using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.Graphics;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// A screen whose list scrolls with the mouse wheel in proportion to the wheel's travel (a scroll panel), so the laser's
    /// thumbstick gives it the wheel a little every frame, at a steady speed that follows how far the stick is pushed, and
    /// not a notch at a time. Other screens get whole notches (a drop-down or a tree moves a step a notch).
    /// </summary>
    internal interface ISmoothWheelScreen
    {
    }

    /// <summary>
    /// The dominant hand as the game's mouse. While a GUI that wants the mouse is up (the game's cursor is showing) and the
    /// hand's aim ray meets the panel (<see cref="HudPanel"/>), the cursor is where the ray hits, trigger is the left
    /// button, grip the right button and the thumbstick's up and down the wheel. The game reads all of it as it reads a
    /// mouse, because it is put into the mouse's state: the position through <see cref="PanelCursor"/> (the game draws
    /// its cursor where it reads it, so the cursor is the dot), the buttons and wheel into MyVRageInput's actual mouse
    /// state right after it updates it, so that pressed, new pressed, released and double clicks all agree.
    /// </summary>
    /// <remarks>
    /// The real mouse is not moved while the laser has the cursor (the game's SetMousePosition is dropped then). The hands
    /// have the cursor when a screen opens; the mouse has it from when it moves, as in flat play whatever the hands are
    /// doing, going on from where the laser left it, until the trigger or grip is pressed with the ray on the panel.
    /// A click begins only on the panel, and a button held from a click goes on holding if the ray slips off the edge,
    /// the cursor pinned to the edge, so a drag does not drop. In plain gameplay the cursor is hidden and none of this runs:
    /// the buttons would be a tool being fired.
    /// </remarks>
    internal static partial class Laser
    {
        /// <summary>Debug: the plain mouse, as before the laser (<see cref="RenderDebug"/> Laser=0).</summary>
        public static bool Off { get; set; }

        /// <summary>A real mouse position that differs by more than this, in window pixels, is the mouse moving.</summary>
        private const float MouseMovedPixels = 0.5f;

        /// <summary>The wheel at full thumbstick, notches a second; a notch is 120, as the platform gives it.</summary>
        private const float ScrollNotchesPerSecond = 8f;

        /// <summary>The same for a screen that takes the wheel smoothly (<see cref="ISmoothWheelScreen"/>): a scroll panel moves a quarter of its height a notch.</summary>
        private const float SmoothNotchesPerSecond = 3f;

        private const int WheelNotch = 120;

        /// <summary>
        /// A press on a grid (an inventory, the blueprints, an assembler's queue) holds the cursor where the trigger went down
        /// until the hand's aim has turned this far from it (degrees, at the distance the ray hits): the game starts dragging the
        /// item the moment the cursor moves at all with the button held, and a hand always moves a little as its trigger is pulled.
        /// </summary>
        internal const float DragDegrees = 1.0f;

        /// <summary>
        /// A second press on a grid within <see cref="DoubleClickSeconds"/> of the first and this near it (degrees of aim, at
        /// the distance the ray hits) goes where the first went: the game counts a double click only within 0.005 of its
        /// screen units (about 7 pixels of 1080), a quarter of a degree at two metres. Lists and tables are not pressed through
        /// here: their check is the wider one of <see cref="DoubleClickTolerance"/>.
        /// </summary>
        internal const float DoubleClickDegrees = 1.5f;

        /// <summary>The game's double click time (MyGuiControlGrid: 500 ms).</summary>
        private const double DoubleClickSeconds = 0.5;

        internal const float LineThickness = 0.003f;
        internal static readonly Vector4 LineColor = new Vector4(0.35f, 0.85f, 1f, 1f);

        // The game's own line material that ignores depth (MyRenderComponentCubeGrid uses its sibling): the panel can be
        // hung where a wall is, and the line should still show.
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");

        private static readonly Vector2 Gui = new Vector2(GuiLayout.Size.X, GuiLayout.Size.Y);

        private static readonly AccessTools.FieldRef<MyVRageInput, MyMouseState> Actual =
            AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_actualMouseState");

        private static readonly AccessTools.FieldRef<MyVRageInput, MyMouseState> Previous =
            AccessTools.FieldRefAccess<MyVRageInput, MyMouseState>("m_previousMouseState");

        private static readonly AccessTools.FieldRef<MyVRageInput, Vector2> RealPosition =
            AccessTools.FieldRefAccess<MyVRageInput, Vector2>("m_absoluteMousePosition");

        private static readonly object gate = new object();

        // What the laser tells the rest of the game, under the gate: PanelCursor reads the cursor wherever the game does.
        private static bool holds;
        private static Vector2 cursor;
        private static bool lineOn;
        private static Vector3 lineFrom, lineTo, lineTip;

        // Game thread only.
        private static bool haveReal, mouseWins, leftHeld, rightHeld;
        private static Vector2 lastReal;
        private static float scrollCarry;
        private static long lastFrame, lastNote;
        private static readonly FeatureGuard guard = new FeatureGuard(nameof(Laser));
        private static int suppressed;

        // A press on a grid (GridPress): the cursor is held at pressAt while pressHeld; the last press that began a pair, for
        // the double click, at pairAt when pairTime (Stopwatch ticks) is not 0.
        private static bool pressHeld;
        private static Vector2 pressAt, pairAt;
        private static long pairTime;
        private static readonly List<MyGuiControlBase> under = new List<MyGuiControlBase>();
        private static Action<MyGuiControlGrid> doubleClick;
        private static bool doubleClickMissing;

        /// <summary>
        /// Each hook on its own, like the hand input's: a laser that cannot be hooked must not take the rendering patches
        /// down with it.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            PatchRegistry.Apply("mouse input", () =>
            {
                // After the hands were read for this frame (InputPatches.FrameStart is a postfix on the same method) and the
                // game updated its mouse state: so last, whatever order they were patched in.
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.Update), new[] { typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(Laser), nameof(AfterInput)) { priority = Priority.Last });
            }, "Laser pointer (mouse input) could not be hooked; it will not reach the game");

        }

        /// <summary>The laser has the cursor: it is where the hand's ray hits the panel.</summary>
        public static bool TryCursor(out Vector2 onGui)
        {
            lock (gate)
            {
                onGui = cursor;
                return holds;
            }
        }

        /// <summary>
        /// The beam as the last frame left it, both ends in the tracking space, to where the ray meets the panel: from the
        /// drawn hand's fingertip, or from the controller's aim pose where no hand is drawn
        /// (the main menu and the forced view, where <see cref="Rendering.MainMenu"/> draws the beam into the eyes). The
        /// headset's beam over the panel draws it too, and has to meet the world's beam at the hand.
        /// Any thread. False when the laser is not on the panel.
        /// </summary>
        public static bool TryGetLine(out Vector3 from, out Vector3 to)
        {
            bool handDrawn = !Rendering.MainMenu.Wanted && !Rendering.ForcedView.Showing;
            lock (gate)
            {
                from = handDrawn ? lineTip : lineFrom;
                to = lineTo;
                return lineOn && !Off;
            }
        }

        public static bool HoldsCursor
        {
            get
            {
                lock (gate)
                    return holds;
            }
        }

        private static void AfterInput(MyVRageInput __instance, bool gameFocused)
        {
            if (!guard.On)
                return;
            try
            {
                Frame(__instance, gameFocused);
            }
            catch (Exception e)
            {
                Release();
                guard.Failed(e, "Laser pointer failed this frame");
            }
        }

        /// <summary>A test aid (Drive's gui commands) has the cursor: where on the GUI, in GUI pixels, and whether the left button is down.</summary>
        static partial void DrivenCursor(ref bool drives, ref Vector2 onGui, ref bool left);

        /// <summary>A test aid (Drive's mouse commands) is the real mouse: where it is in the window, and whether its left button is down.</summary>
        static partial void DrivenMouse(ref bool drives, ref Vector2 window, ref bool left);

        /// <summary>Game thread, once per frame, as the game has just updated its mouse state.</summary>
        private static void Frame(MyVRageInput input, bool gameFocused)
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = lastFrame == 0 ? 0f : Math.Min((now - lastFrame) / (float)Stopwatch.Frequency, 0.1f);
            lastFrame = now;

            // While the datapad is held, the hand not holding it points.
            Hand hand = Hands.Datapad.LaserHand(VRSettings.DominantHand);
            HandState state = VRInput.Get(hand);
            bool cursorShown = MySandboxGame.Static != null && MySandboxGame.Static.IsCursorVisible;

            // A test driving the GUI (Drive's gui commands) has the cursor: it is where the command put it, and the left
            // button is the command's, through the same mouse state.
            bool drives = false, driveLeft = false;
            Vector2 driven = default;
            if (cursorShown)
                DrivenCursor(ref drives, ref driven, ref driveLeft);
            if (drives)
            {
                leftHeld = rightHeld = mouseWins = false;
                scrollCarry = 0f;
                ref MyMouseState drivenState = ref Actual(input);
                if (driveLeft)
                    drivenState.LeftButton = true;
                if (!gameFocused && VRInput.Active && !Off)
                    drivenState.ScrollWheelValue = Previous(input).ScrollWheelValue;
                lock (gate)
                {
                    holds = true;
                    cursor = driven;
                    lineOn = false;
                }
                return;
            }

            // A fingertip at the held datapad has the cursor instead of the ray (PanelTouch).
            if (cursorShown && !Off && PanelTouch.Frame(Gui, out Vector2 touched, out bool touchLeft, out int touchWheel))
            {
                leftHeld = rightHeld = mouseWins = pressHeld = false;
                scrollCarry = 0f;
                ref MyMouseState touchState = ref Actual(input);
                if (touchLeft)
                    touchState.LeftButton = true;
                if (!gameFocused && VRInput.Active)
                    touchState.ScrollWheelValue = Previous(input).ScrollWheelValue + touchWheel;
                else
                    touchState.ScrollWheelValue += touchWheel;
                lock (gate)
                {
                    holds = true;
                    cursor = touched;
                    lineOn = PanelTouch.TryGetBeam(out lineFrom, out lineTo);
                    lineTip = lineFrom;
                }
                return;
            }
            if (!cursorShown)
                PanelTouch.Reset();

            // Where the ray meets the panel.
            LaserRay.Hit hit = default;
            bool ready = false;
            float pixelsPerDegree = 0f;
            if (!Off && VRInput.Active && state.Tracked && cursorShown
                && HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out float halfHeight))
            {
                hit = LaserRay.Cast(state.Aim, pose, halfWidth, halfHeight, Gui);
                ready = hit.Valid;
                // How far a degree of the hand's aim moves the hit on the panel, in GUI pixels, where it hits now (square on).
                if (halfWidth > 0f)
                    pixelsPerDegree = hit.Distance * (float)Math.Tan(MathHelper.ToRadians(1f)) * Gui.X / (2f * halfWidth);
            }

            // The real mouse, as the platform has it: the game's own read of it goes through the panel (PanelCursor). The
            // game putting it somewhere (SetMousePosition) is not the player moving it.
            bool mouseDriven = false, mouseDrivenLeft = false;
            Vector2 drivenMouse = default;
            if (cursorShown)
                DrivenMouse(ref mouseDriven, ref drivenMouse, ref mouseDrivenLeft);
            if (mouseDriven)
            {
                RealPosition(input) = drivenMouse;
                if (mouseDrivenLeft)
                    Actual(input).LeftButton = true;
            }
            Vector2 real = RealPosition(input);
            bool placedByGame = PanelCursor.TakePlaced(out Vector2 placedAt) && Vector2.Distance(real, placedAt) <= MouseMovedPixels;
            bool moved = haveReal && !placedByGame && Vector2.Distance(real, lastReal) > MouseMovedPixels;
            haveReal = cursorShown;
            lastReal = real;

            // The mouse has the cursor from when it moves until the trigger or grip is pressed on the panel; each screen
            // that opens starts with the hands.
            bool mouseWon = mouseWins;
            // A grip closing on a tool or gun in your own inventory is a pull into the hand (InventoryPull), not the right
            // button; it lasts while the grip is held.
            if (!ready || !cursorShown)
                InventoryPull.Cancel();
            else
            {
                if (!mouseWins && hit.Inside && VRInput.IsNewPressed(hand, VRButtons.Grip) && !Hands.Datapad.OwnsGrip(hand)
                    && HudPanel.TryGetPlacement(out Matrix pullPanel, out _, out _))
                    InventoryPull.TryBegin(hand, GridAt(hit.Pixel), state.Grip.Translation, pullPanel);
                InventoryPull.Step(hand, VRInput.IsPressed(hand, VRButtons.Grip), state.Grip.Translation);
            }
            bool gripIsButton = !Hands.Datapad.OwnsGrip(hand) && !InventoryPull.OwnsGrip(hand);
            bool handPressed = ready && hit.Inside && (VRInput.IsNewPressed(hand, VRButtons.Trigger)
                                                       || (VRInput.IsNewPressed(hand, VRButtons.Grip) && gripIsButton));
            if (!cursorShown)
                mouseWins = false;
            else if (moved)
                mouseWins = true;
            else if (handPressed)
                mouseWins = false;
            if (mouseWins != mouseWon && cursorShown)
                Note(mouseWins ? "Laser: the mouse moved, so the mouse has the cursor" : "Laser: a press on the panel, so the laser has the cursor");

            // Holding the cursor: on the panel, or still carrying a button down from a click that began there.
            bool carrying = leftHeld || rightHeld;
            bool nowHolds = ready && !mouseWins && (hit.Inside || carrying);
            bool trigger = VRInput.IsPressed(hand, VRButtons.Trigger), grip = VRInput.IsPressed(hand, VRButtons.Grip);
            bool left = leftHeld, right = rightHeld;
            if (!nowHolds)
                leftHeld = rightHeld = false;
            else
            {
                if (hit.Inside && VRInput.IsNewPressed(hand, VRButtons.Trigger))
                    leftHeld = true;
                if (hit.Inside && VRInput.IsNewPressed(hand, VRButtons.Grip) && gripIsButton)
                    rightHeld = true;
                leftHeld &= trigger;
                rightHeld &= grip;
            }
            if (leftHeld != left)
                Log.Info($"Laser: left button {(leftHeld ? "down" : "up")} at ({hit.Pixel.X:0}, {hit.Pixel.Y:0})");
            if (rightHeld != right)
                Log.Info($"Laser: right button {(rightHeld ? "down" : "up")} at ({hit.Pixel.X:0}, {hit.Pixel.Y:0})");

            // Where the game is told the cursor is: where the ray hits, unless a press on a grid holds it (GridPress).
            Vector2 at = hit.Pixel;
            if (leftHeld && !left)
                GridPress(hit.Pixel, pixelsPerDegree, now);
            if (pressHeld)
            {
                if (!nowHolds || !(leftHeld || left))
                    pressHeld = false;
                else if (leftHeld && Vector2.Distance(hit.Pixel, pressAt) > DragDegrees * pixelsPerDegree)
                {
                    // A drag. The game drags the item under the cursor on the frame it first moves, so the first step is a
                    // pixel from the press, still on the item pressed; from the next frame the cursor follows the hand.
                    pressHeld = false;
                    at = pressAt + Vector2.Normalize(hit.Pixel - pressAt);
                    Log.Info($"Laser: drag from ({pressAt.X:0}, {pressAt.Y:0}) on a grid, the hand {Vector2.Distance(hit.Pixel, pressAt):0} px off it " +
                             $"(over {DragDegrees * pixelsPerDegree:0} px, {DragDegrees:0.0} deg at {hit.Distance:0.00} m)");
                }
                else
                {
                    // Held, or let go of this frame: the game picks the item where the button goes up.
                    at = pressAt;
                    if (!leftHeld)
                        pressHeld = false;
                }
            }

            // A (X with the left hand pointing) on an item in a grid: the grid's double click. In the inventory it moves the item
            // to the other side, the whole stack, or 10, 100, 1000 with the other hand's grip and trigger (VRControls.WantsAmount).
            if (nowHolds && hit.Inside && !leftHeld && VRInput.IsNewPressed(hand, hand == Hand.Right ? VRButtons.A : VRButtons.X))
                ItemButton(hit.Pixel);

            // The wheel: the thumbstick's up and down. A notch at a time, or, for a screen that scrolls in proportion, a few
            // units of a notch every frame.
            float stick = nowHolds ? Dead(state.Stick.Y) : 0f;
            bool smooth = stick != 0f && FocusTakesSmoothWheel();
            scrollCarry = stick == 0f ? 0f : scrollCarry + stick * (smooth ? SmoothNotchesPerSecond : ScrollNotchesPerSecond) * seconds;
            int wheel;
            if (smooth)
            {
                wheel = (int)(scrollCarry * WheelNotch);
                scrollCarry -= wheel / (float)WheelNotch;
            }
            else
            {
                int notches = (int)scrollCarry;
                wheel = notches * WheelNotch;
                scrollCarry -= notches;
            }

            // Into the mouse state the game has just made: the buttons, which it will see next frame as 'was down', so
            // pressed, new pressed and released agree; and the wheel, which the state keeps as a running total.
            ref MyMouseState actual = ref Actual(input);
            if (leftHeld)
                actual.LeftButton = true;
            if (rightHeld)
                actual.RightButton = true;
            if (!gameFocused && VRInput.Active && !Off)
            {
                // A window without focus gets a cleared state every frame, whose total is zero: the game would read the
                // drop from the last total as a scroll. Keep the total where it was, and add the notches to it.
                actual.ScrollWheelValue = Previous(input).ScrollWheelValue + wheel;
            }
            else
                actual.ScrollWheelValue += wheel;

            bool wasHolding;
            Vector2 laserWasAt;
            lock (gate)
            {
                wasHolding = holds;
                laserWasAt = cursor;
                holds = nowHolds;
                cursor = at;
                lineOn = nowHolds;
                lineFrom = state.Aim.Translation;
                lineTip = Hands.FingerPress.FingertipInTracking(hand, state);
                lineTo = hit.Point;
            }
            if (nowHolds != wasHolding)
                Note(nowHolds ? $"Laser: pointing at the panel, GUI ({hit.Pixel.X:0}, {hit.Pixel.Y:0}), {hit.Distance:0.00} m" : "Laser: off the panel");

            // The mouse took the cursor from the laser: it goes on from where the laser had it, so the cursor does not jump.
            if (mouseWins && !mouseWon && wasHolding)
            {
                input.SetMousePosition((int)laserWasAt.X, (int)laserWasAt.Y);
                if (PanelCursor.TakePlaced(out Vector2 window))
                {
                    RealPosition(input) = window;
                    lastReal = window;
                    Log.Info($"Laser: the mouse goes on from the laser's GUI ({laserWasAt.X:0}, {laserWasAt.Y:0}), window ({window.X:0}, {window.Y:0})");
                }
            }
        }

        /// <summary>
        /// The trigger went down on the panel this frame. On a grid the cursor is held there (<see cref="DragDegrees"/>), and a
        /// second press soon after the first and near it is put where the first was, so the game sees a double click
        /// (<see cref="DoubleClickDegrees"/>). Anywhere else (a slider, a list, a button) the cursor follows the hand as it was.
        /// </summary>
        private static void GridPress(Vector2 pixel, float pixelsPerDegree, long now)
        {
            if (pixelsPerDegree <= 0f || GridAt(pixel) == null)
            {
                pairTime = 0;
                return;
            }
            double since = pairTime == 0 ? double.MaxValue : (now - pairTime) / (double)Stopwatch.Frequency;
            float apart = Vector2.Distance(pixel, pairAt), radius = DoubleClickDegrees * pixelsPerDegree;
            if (since <= DoubleClickSeconds && apart <= radius)
            {
                pressAt = pairAt;
                pairTime = 0;
                Log.Info($"Laser: second press on a grid {apart:0} px from the first after {since * 1000:0} ms: put on the first, a double click " +
                         $"(within {radius:0} px, {DoubleClickDegrees:0.0} deg at this distance)");
            }
            else
            {
                pressAt = pairAt = pixel;
                pairTime = now;
            }
            pressHeld = true;
        }

        /// <summary>The grid drawn on top under this GUI pixel on the screen with focus, or null.</summary>
        private static MyGuiControlGrid GridAt(Vector2 pixel)
        {
            MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
            if (focus == null)
                return null;
            under.Clear();
            try
            {
                focus.GetControlsUnderMouseCursor(MyGuiManager.GetNormalizedMousePosition(pixel, Gui), under, visibleOnly: true);
                for (int i = under.Count - 1; i >= 0; i--)
                    if (under[i] is MyGuiControlGrid grid)
                        return grid;
                return null;
            }
            finally
            {
                under.Clear();
            }
        }

        /// <summary>A on the panel: the double click of the grid item under the cursor, as the game's own double click does it.</summary>
        private static void ItemButton(Vector2 pixel)
        {
            MyGuiControlGrid grid = GridAt(pixel);
            int index = grid?.MouseOverIndex ?? -1;
            MyGuiGridItem item = index >= 0 ? grid.TryGetItemAt(index) : null;
            if (item == null)
            {
                Note($"Laser: A at ({pixel.X:0}, {pixel.Y:0}) is on {(grid == null ? "no grid" : "an empty cell")}; nothing to move");
                return;
            }
            if (doubleClick == null && !doubleClickMissing)
            {
                try
                {
                    doubleClick = AccessTools.MethodDelegate<Action<MyGuiControlGrid>>(AccessTools.Method(typeof(MyGuiControlGrid), "DoubleClickItem"));
                }
                catch (Exception e)
                {
                    doubleClickMissing = true;
                    Log.Error(e, "Laser: the grid's double click could not be found; A does not move items");
                }
            }
            if (doubleClick == null)
                return;
            string what = item.UserData is MyPhysicalInventoryItem stack ? $"{stack.Content?.SubtypeName} x{stack.Amount}" : item.UserData?.GetType().Name ?? "an item";
            Log.Info($"Laser: A on {what} (cell {index} of {grid.Name ?? grid.GetType().Name}): the grid's double click");
            grid.SelectedIndex = index;
            doubleClick(grid);
        }

        /// <summary>The screen with focus scrolls in proportion to the wheel's travel.</summary>
        private static bool FocusTakesSmoothWheel()
        {
            try
            {
                return MyScreenManager.GetScreenWithFocus() is ISmoothWheelScreen;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The laser lets go of everything, after a failure or when it is turned off.</summary>
        private static void Release()
        {
            leftHeld = rightHeld = mouseWins = pressHeld = false;
            pairTime = 0;
            scrollCarry = 0f;
            lock (gate)
                holds = lineOn = false;
        }

        /// <summary>The thumbstick with its middle taken out, scaled to run from 0 to 1 from the edge of it, with its sign.</summary>
        private static float Dead(float value)
        {
            float deadzone = VRSettings.StickDeadzone, size = Math.Abs(value);
            if (size <= deadzone)
                return 0f;
            return Math.Sign(value) * (Math.Min(size, 1f) - deadzone) / (1f - deadzone);
        }

        /// <summary>A log line, but no more than two a second; the ones held back are counted in the next.</summary>
        private static void Note(string text)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastNote != 0 && now - lastNote < Stopwatch.Frequency / 2)
            {
                suppressed++;
                return;
            }
            lastNote = now;
            Log.Info(suppressed > 0 ? $"{text} ({suppressed} similar held back)" : text);
            suppressed = 0;
        }

    }
}
