// VRControls: what is added to the game's movement, rotation and roll input, and trigger swallowing.
using System;
using System.Diagnostics;
using Sandbox;
using Sandbox.Engine.Utils;
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
    internal static partial class VRControls
    {
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
    }
}
