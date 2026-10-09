using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRage.Game;
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
    /// The real mouse is never moved (the game's SetMousePosition is dropped while the laser has the cursor), and it wins
    /// whenever it moves, until the hand's hit point has moved more than <see cref="TakeBackPixels"/> from where it was.
    /// A click begins only on the panel, and a button held from a click goes on holding if the ray slips off the edge,
    /// the cursor pinned to the edge, so a drag does not drop. In plain gameplay the cursor is hidden and none of this runs:
    /// the buttons would be a tool being fired.
    /// </remarks>
    internal static class Laser
    {
        /// <summary>Debug: the plain mouse, as before the laser (<see cref="RenderDebug"/> Laser=0).</summary>
        public static bool Off { get; set; }

        /// <summary>How far, in GUI pixels, the hand's hit point has to move for the laser to take the cursor back from the real mouse.</summary>
        private const float TakeBackPixels = 8f;

        /// <summary>A real mouse position that differs by more than this, in window pixels, is the mouse moving.</summary>
        private const float MouseMovedPixels = 0.5f;

        /// <summary>The wheel at full thumbstick, notches a second; a notch is 120, as the platform gives it.</summary>
        private const float ScrollNotchesPerSecond = 8f;

        /// <summary>The same for a screen that takes the wheel smoothly (<see cref="ISmoothWheelScreen"/>): a scroll panel moves a quarter of its height a notch.</summary>
        private const float SmoothNotchesPerSecond = 3f;

        private const int WheelNotch = 120;

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
        private static Vector3 lineFrom, lineTo;

        // Game thread only.
        private static bool haveReal, mouseWins, leftHeld, rightHeld;
        private static Vector2 lastReal, anchor;
        private static float scrollCarry;
        private static long lastFrame, lastNote;
        private static int suppressed, errors;

        /// <summary>
        /// Each hook on its own, like the hand input's: a laser that cannot be hooked must not take the rendering patches
        /// down with it.
        /// </summary>
        public static void Patch(Harmony harmony)
        {
            try
            {
                // After the hands were read for this frame (InputPatches.FrameStart is a postfix on the same method) and the
                // game updated its mouse state: so last, whatever order they were patched in.
                harmony.Patch(AccessTools.Method(typeof(MyVRageInput), nameof(MyVRageInput.Update), new[] { typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(Laser), nameof(AfterInput)) { priority = Priority.Last });
            }
            catch (Exception e)
            {
                Log.Error(e, "Laser pointer (mouse input) could not be hooked; it will not reach the game");
            }

            try
            {
                // The gameplay screen's draw: only when there is a scene, never on the main menu. Billboards added up to the
                // end of the game's frame are drawn in it (MyRenderProxy.AfterUpdate swaps them to the renderer).
                harmony.Patch(AccessTools.Method(typeof(MySession), nameof(MySession.DrawSync)),
                    postfix: new HarmonyMethod(typeof(Laser), nameof(DrawLine)));
            }
            catch (Exception e)
            {
                Log.Error(e, "Laser pointer (visible line) could not be hooked; the cursor still works");
            }
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
        /// The beam as the last frame left it, both ends in the tracking space: from the hand to where the ray meets the panel.
        /// For the main menu, where the game draws no world and so no billboard (<see cref="Rendering.MainMenu"/> draws it in the eyes).
        /// Any thread. False when the laser is not on the panel.
        /// </summary>
        public static bool TryGetLine(out Vector3 from, out Vector3 to)
        {
            lock (gate)
            {
                from = lineFrom;
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
            try
            {
                Frame(__instance, gameFocused);
            }
            catch (Exception e)
            {
                Release();
                if (errors++ < 3)
                    Log.Error(e, "Laser pointer failed this frame");
            }
        }

        /// <summary>Game thread, once per frame, as the game has just updated its mouse state.</summary>
        private static void Frame(MyVRageInput input, bool gameFocused)
        {
            long now = Stopwatch.GetTimestamp();
            float seconds = lastFrame == 0 ? 0f : Math.Min((now - lastFrame) / (float)Stopwatch.Frequency, 0.1f);
            lastFrame = now;

            Hand hand = VRSettings.DominantHand;
            HandState state = VRInput.Get(hand);
            bool cursorShown = MySandboxGame.Static != null && MySandboxGame.Static.IsCursorVisible;

            // A test driving the GUI (Drive's gui commands) has the cursor: it is where the command put it, and the left
            // button is the command's, through the same mouse state.
            if (cursorShown && Drive.TryCursor(out Vector2 driven, out bool driveLeft))
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

            // Where the ray meets the panel.
            LaserRay.Hit hit = default;
            bool ready = false;
            if (!Off && VRInput.Active && state.Tracked && cursorShown
                && HudPanel.TryGetPlacement(out Matrix pose, out float halfWidth, out float halfHeight))
            {
                hit = LaserRay.Cast(state.Aim, pose, halfWidth, halfHeight, Gui);
                ready = hit.Valid;
            }

            // The real mouse, as the platform has it: the game's own read of it goes through the panel (PanelCursor).
            Vector2 real = RealPosition(input);
            bool moved = haveReal && Vector2.Distance(real, lastReal) > MouseMovedPixels;
            haveReal = cursorShown;
            lastReal = real;

            bool mouseWon = mouseWins;
            if (!ready)
                mouseWins = false;
            else if (moved)
            {
                mouseWins = true;
                anchor = hit.Pixel;
            }
            else if (mouseWins && Vector2.Distance(hit.Pixel, anchor) > TakeBackPixels)
                mouseWins = false;
            if (mouseWins != mouseWon)
                Note(mouseWins ? "Laser: the mouse moved, so the mouse has the cursor" : "Laser: the hand moved, so the laser has the cursor");

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
                if (hit.Inside && VRInput.IsNewPressed(hand, VRButtons.Grip))
                    rightHeld = true;
                leftHeld &= trigger;
                rightHeld &= grip;
            }
            if (leftHeld != left)
                Log.Info($"Laser: left button {(leftHeld ? "down" : "up")} at ({hit.Pixel.X:0}, {hit.Pixel.Y:0})");
            if (rightHeld != right)
                Log.Info($"Laser: right button {(rightHeld ? "down" : "up")} at ({hit.Pixel.X:0}, {hit.Pixel.Y:0})");

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
            lock (gate)
            {
                wasHolding = holds;
                holds = nowHolds;
                cursor = hit.Pixel;
                lineOn = nowHolds;
                lineFrom = state.Aim.Translation;
                lineTo = hit.Point;
            }
            if (nowHolds != wasHolding)
                Note(nowHolds ? $"Laser: pointing at the panel, GUI ({hit.Pixel.X:0}, {hit.Pixel.Y:0}), {hit.Distance:0.00} m" : "Laser: off the panel");
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
            leftHeld = rightHeld = mouseWins = false;
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

        /// <summary>
        /// Game thread, in the gameplay screen's draw: a thin line from the hand to where the ray hits the panel, as the
        /// game draws its own lines (billboards), in the world where the hand is (<see cref="HandWorld"/>).
        /// </summary>
        private static void DrawLine()
        {
            try
            {
                Vector3 from, to;
                lock (gate)
                {
                    if (!lineOn)
                        return;
                    from = lineFrom;
                    to = lineTo;
                }
                if (Off || !MyTransparentGeometry.HasCamera || !HandWorld.TryGetTrackingToWorld(out MatrixD toWorld))
                    return;
                Vector3D start = Vector3D.Transform((Vector3D)from, toWorld);
                Vector3D along = Vector3D.Transform((Vector3D)to, toWorld) - start;
                double length = along.Length();
                if (length < 0.02)
                    return;
                MyTransparentGeometry.AddLineBillboard(LineMaterial, LineColor, start, (Vector3)(along / length), (float)length, LineThickness);
            }
            catch (Exception e)
            {
                if (errors++ < 3)
                    Log.Error(e, "Laser pointer could not draw its line");
            }
        }
    }
}
