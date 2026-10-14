// VRControls: per-context stick and button mapping - walking, zero-g, spectator, seats, camera view, zoom, use-hold.
using System;
using System.Diagnostics;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Tracking;
using VRage.Input;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    internal static partial class VRControls
    {
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
            // climb) and the jetpack, except a press made with the left stick click held, which steps the block's stage. A paste and
            // the voxel hand leave A, B and X as they are; the colour tool takes A and B for its brush (Hands.PaintMode). The quick
            // take, production and dump at a container are on the stick clicks, not A and B (Hands.QuickInventory); sprint, the
            // zero-g roll and reload (Hands.Reload) leave a click while a press of it is taken there.
            bool build = Hands.BuildMode.Active;
            // The triggers follow the hand holding the gun or tool: left-handed, the left one is primary (Hands.ItemSpots).
            bool swap = Hands.ItemSpots.TriggersSwapped;
            Hand fire = swap ? Hand.Left : Hand.Right, second = swap ? Hand.Right : Hand.Left;
            // A hand weapon's secondary trigger is only its iron sight, which in VR is the gun brought up to the eye (Hands.IronSight).
            if (Hands.IronSight.Frame())
                SwallowTrigger(second, "a hand weapon's secondary trigger (its iron sight)");
            bool paint = Hands.PaintMode.Active;
            bool place = TriggerHeld(fire), remove = TriggerHeld(second);
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
            // At a container the grip's Use waits for the grip to be let go, so the right stick click can deposit with it held
            // (Hands.QuickInventory.HoldsBack; the ray Use is done there on release, a grab's in Hands.GrabUse).
            Set(Intent.Use, VRInput.IsPressed(Hand.Right, VRButtons.Grip) && Hands.GripClaim.Owner(Hand.Right) == null && !Hands.QuickInventory.HoldsBack);
            Set(Intent.Jump, jumpButton || stickUp);
            Set(Intent.Crouch, stickDown);
            Set(Intent.Thrusts, !paint && !Hands.BuildMode.TakesB && !Hands.Ping.TakesB && VRInput.IsPressed(Hand.Right, VRButtons.B));
            Set(Intent.Damping, !build && DampingFromX());
            // The left stick click held with a block in hand is building's (Hands.BuildMode.Modifier), not sprint; pressed at a
            // container it is the quick withdraw (Hands.QuickInventory.TakesLeftClick) until it is let go.
            // Left-handed with a hand weapon that reloads, the left click is reload and sprint is the right click (Hands.Reload.SprintHand).
            Hand sprintHand = Hands.Reload.SprintHand;
            bool sprintTaken = sprintHand == Hand.Left ? Hands.BuildMode.Modifier || Hands.QuickInventory.TakesLeftClick : Hands.QuickInventory.TakesClick;
            Set(Intent.Sprint, VRInput.IsPressed(sprintHand, VRButtons.StickClick) && !sprintTaken);
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
        /// The dampeners from X: while X is the ping's (<see cref="Hands.Ping.TakesX"/>) a tap, given once as it is let go, so a
        /// hold can aim instead; otherwise X held, as the game reads a key.
        /// </summary>
        private static bool DampingFromX() =>
            Hands.Ping.TakesX ? Hands.Ping.DampingTap : VRInput.IsPressed(Hand.Left, VRButtons.X);

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
            // In a seat ColbyJack1134's rig owns, the thumbsticks fly or drive on their own only with Thumbstick controls in
            // cockpits on (VRSettings.ColbyThumbsticks); his ApplyFlight below still has them for his held sticks, wheel and bars.
            bool thumbsticks = !Cockpits.CockpitControls.Owns || VRSettings.ColbyThumbsticks;
            Vector2 flyLeft = thumbsticks ? leftStick : Vector2.Zero, flyRight = thumbsticks ? rightStick : Vector2.Zero;
            if (seatSticks)
                seatRotation = ShipStick.Rotation(flyRight, feel, sensitivity);
            if (context != VRContext.Ship)
            {
                Set(Intent.Primary, fire);
                // A turret, a searchlight, or a turret control block through its camera: A zooms in and B out while
                // held, as a pad's zoom buttons; the zoomed view is on the forced view's screen (Rendering.ForcedView).
                // Not with a menu open, where B is Back (UpdateBack).
                Zoom(!zoomInMenu && VRInput.IsPressed(Hand.Right, VRButtons.A), !zoomInMenu && VRInput.IsPressed(Hand.Right, VRButtons.B), "Turret: A / B");
                return;
            }

            bool secondary = TriggerHeld(Hand.Left);
            bool up = VRInput.IsPressed(Hand.Right, VRButtons.A), down = !Hands.Ping.TakesB && VRInput.IsPressed(Hand.Right, VRButtons.B);
            if (seatSticks)
            {
                seatMove = ShipStick.Move(flyLeft, up, down, feel);
                seatRoll = ShipStick.Roll(Squeeze(Hand.Left), Squeeze(Hand.Right), feel, sensitivity);
                // ColbyJack1134's cockpit: a held stick or throttle overrides the turn, roll and move (his ApplyFlight).
                Cockpits.CockpitControls.ApplyFlight(Cockpits.CockpitControls.RotationSpeed, leftStick, rightStick, ref seatMove, ref seatRotation, ref seatRoll);
            }
            Set(Intent.Primary, fire);
            Set(Intent.Secondary, secondary);
            Set(Intent.Jump, up);
            Set(Intent.Crouch, down);
            Set(Intent.Damping, DampingFromX());
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
    }
}
