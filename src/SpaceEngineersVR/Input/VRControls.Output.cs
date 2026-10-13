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
        /// <summary>RenderDebug ZeroGHeadThrust=0: on the jetpack in zero g the left stick and the climb are turned by the head's yaw only, level with the body, as in gravity.</summary>
        public static bool HeadThrustOff { get; set; }

        // The first frame the zero-g thrust went where the head looks is logged, once a run (HeadThrust).
        private static bool headThrustLogged;

        /// <summary>
        /// Adds the left stick to the game's movement input (MyInputExtensions.GetPositionDelta: x right, y up, z back,
        /// as the keys give it). Walking goes where the head looks: the stick is turned by the head's yaw from the body,
        /// and the body turns to follow while it walks (<see cref="BodyFollow"/>). On the jetpack the right stick and A
        /// add the climb; on foot jump and crouch are buttons only. On the jetpack in zero g the stick and the climb are
        /// turned by the head's pitch as well, so the thrust goes where the head looks (<see cref="HeadThrust"/>). In a ship
        /// controller the left stick is the move indicator the thrusters and the wheels read (forward and back, strafe, steer),
        /// A is up and B down. The free spectator camera is flown as the jetpack is in gravity: the left stick turned by the
        /// head's yaw, A up and B down.
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
            float climb = jetpack ? MathHelper.Clamp(rightStick.Y + (jumpButton ? 1f : 0f), -1f, 1f) : 0f;
            if (jetpack && HeadThrust(character, climb, out Vector3 looked))
            {
                delta = Clamp(delta + looked);
                return;
            }

            // On the jetpack the body does not turn after the head (BodyFollow), so the head can look further round than the
            // thrust goes: it stops at the look limit, as in zero g.
            float yaw = jetpack ? LimitedHeadYaw(character) : HeadYaw(character);
            float cos = (float)Math.Cos(yaw), sin = (float)Math.Sin(yaw);
            Vector3 add = new Vector3(
                leftStick.X * cos - leftStick.Y * sin,
                climb,
                -(leftStick.X * sin + leftStick.Y * cos));
            delta = Clamp(delta + add);
        }

        /// <summary>The head's yaw from the body as <see cref="HeadYaw"/> gives it, limited to <see cref="MaxLookYaw"/> (<see cref="GameHead.LimitedLook"/>).</summary>
        private static float LimitedHeadYaw(MyCharacter character)
        {
            if ((!character.IsInFirstPersonView && !character.ForceFirstPersonCamera) || !GameHead.Take())
                return 0f;
            GameHead.LimitedLook(out float yaw, out _);
            return yaw;
        }

        /// <summary>
        /// On the jetpack in zero g the thrust goes where the head looks, as it does in the game, where the mouse pitches the whole
        /// body and W goes where the view looks: the left stick and the climb are turned from the head's frame into the body's by
        /// the head's yaw and pitch from the body, and by none of its roll. The look is the one limited as a neck is
        /// (<see cref="GameHead.LimitedLook"/>): round no further than <see cref="MaxLookYaw"/> (70 deg) from the body, and up or
        /// down no further than <see cref="MaxLookPitch"/> (89 deg). Forward is the limited look's forward, the strafe its right
        /// levelled, the climb its up with the roll taken out. The limited look never passes straight up or down, and a head bent
        /// back past vertical is read as looking straight up facing the body's way, so the strafe and the climb need no flipping
        /// to stay on the head's right and up. False, and the stick is turned by the yaw alone as in gravity, where the zero-g
        /// pitch and roll would be refused (gravity, a character that flies upright only: <see cref="CanPitchAndRoll"/>), with the
        /// head not tracked or the view not first person, and with RenderDebug ZeroGHeadThrust=0.
        /// </summary>
        /// <param name="climb">The climb, from the right stick and A, -1 to 1.</param>
        /// <param name="add">The movement to add, in the body's frame (x right, y up, z back).</param>
        private static bool HeadThrust(MyCharacter character, float climb, out Vector3 add)
        {
            add = Vector3.Zero;
            if (!FliesWhereHeadLooks(character) || !GameHead.Take())
                return false;
            GameHead.LimitedLook(out float yaw, out float pitch);
            add = Vector3.TransformNormal(new Vector3(leftStick.X, climb, -leftStick.Y),
                Matrix.CreateRotationX(pitch) * Matrix.CreateRotationY(yaw));
            if (!headThrustLogged)
            {
                headThrustLogged = true;
                Log.Info($"Zero-g thrust: the jetpack goes where the head looks, pitched {MathHelper.ToDegrees(Math.Abs(pitch)):F0} deg {(pitch >= 0f ? "up" : "down")} " +
                         $"and turned {MathHelper.ToDegrees(Math.Abs(yaw)):F0} deg {(yaw >= 0f ? "left" : "right")} from the body " +
                         $"(the look is limited to {MathHelper.ToDegrees(MaxLookYaw):F0} deg round and {MathHelper.ToDegrees(MaxLookPitch):F0} deg up/down; " +
                         "the stick along the head's forward and its levelled right, the climb along its up; the body turns by the sticks only, not after the head; " +
                         "RenderDebug ZeroGHeadThrust=0 flies level with the body)");
            }
            return true;
        }

        /// <summary>
        /// On the jetpack in zero g, in first person, with RenderDebug ZeroGHeadThrust not 0: the thrust goes where the head looks
        /// (<see cref="HeadThrust"/>), so the body does not turn to follow the head (<see cref="Tracking.BodyFollow"/>) and turns by the
        /// sticks only. Looked past straight up, the head's yaw from the body comes out turned half round, and a body following it
        /// would spin round with it.
        /// </summary>
        internal static bool FliesWhereHeadLooks(MyCharacter character) =>
            !HeadThrustOff && (character.IsInFirstPersonView || character.ForceFirstPersonCamera) && CanPitchAndRoll(out _);

        /// <summary>
        /// Floating free: in zero g (the gravity <see cref="CanPitchAndRoll"/> allows, <see cref="ZeroGSquared"/>), with the jetpack
        /// off, standing on nothing and not on a ladder. There is no friction to turn the body by then, so the body does not turn
        /// to follow the head (<see cref="Tracking.BodyFollow"/>) and turns by the sticks only. What the character stands on is the
        /// game's own physics answer, <c>CharacterProxy.Supported</c>: the feet are on the ground, a hull or an asteroid, which is
        /// also what the magnetic boots hold to (MyCharacter.UpdateBootsStateAndEmmisivity reads it for their state, and the
        /// boots are only ever on while the character is not falling), so it is true on the boots as on the ground. It is read
        /// from the physics every step (MyCharacter.UpdateMovement), rather than from IsFalling, which is not set during a jump
        /// (MyCharacter.OnCharacterStateChanged starts a fall only when no jump is running) and so would call a character that
        /// has jumped off in zero g grounded. A character with no physics proxy is not taken to be floating.
        /// </summary>
        internal static bool FloatsFree(MyCharacter character)
        {
            if (character == null || character.IsDead || character.IsSitting || character.JetpackRunning || character.IsOnLadder
                || character.Gravity.LengthSquared() > ZeroGSquared)
                return false;
            var proxy = character.Physics?.CharacterProxy;
            return proxy != null && !proxy.Supported;
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
