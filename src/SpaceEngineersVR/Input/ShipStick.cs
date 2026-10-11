using System;
using VRage.Input;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// How the game's gamepad scales a thumbstick, read from the player's own joystick options so the hands feel like
    /// the pad they would otherwise use. A pad axis gives sensitivity * level ^ exponent, where level is the push past
    /// the deadzone, 0 to 1 (MyVRageInput.GetJoystickAxisStateForGameplay: 2 and 2 out of the box).
    /// </summary>
    internal struct GamepadFeel
    {
        public const float DefaultSensitivity = 2f;
        public const float DefaultExponent = 2f;

        public float Sensitivity, Exponent;

        /// <summary>The player's "invert Y" for vehicles: pushing the stick up pitches the nose down.</summary>
        public bool InvertPitch;

        public static GamepadFeel Default => new GamepadFeel { Sensitivity = DefaultSensitivity, Exponent = DefaultExponent };

        /// <summary>The game's current joystick options; the defaults when the input layer is not up (or holds nonsense).</summary>
        public static GamepadFeel Current()
        {
            GamepadFeel feel = Default;
            IMyInput input = MyInput.Static;
            if (input == null)
                return feel;
            float sensitivity = input.GetJoystickSensitivity(), exponent = input.GetJoystickExponent();
            if (sensitivity > 0.01f && sensitivity < 100f)
                feel.Sensitivity = sensitivity;
            if (exponent >= 0.1f && exponent < 20f)
                feel.Exponent = exponent;
            feel.InvertPitch = input.GetJoystickYInversionVehicle();
            return feel;
        }
    }

    /// <summary>
    /// The numbers a seated stick, grip or button gives the game, as a gamepad at the same push would. Pure
    /// arithmetic on the inputs, so it can be checked without the game.
    /// </summary>
    internal static class ShipStick
    {
        /// <summary>The factor MyInputExtensions.GetRotation puts on the sum of its inputs before the game sees them.</summary>
        public const float RotationScale = 9f;

        /// <summary>How far a grip has to be squeezed before it rolls the ship (its analog value, 0 to 1).</summary>
        public const float GripDeadzone = 0.1f;

        /// <summary>The pad's curve: the push past the deadzone (0 to 1), made into what the game is given.</summary>
        public static float Curve(float level, GamepadFeel feel) =>
            feel.Sensitivity * (float)Math.Pow(MathHelper.Clamp(level, 0f, 1f), feel.Exponent);

        /// <summary>
        /// A stick that already has its deadzone taken out (length 0 to 1 is the level) put through the curve, the
        /// direction kept, so a diagonal is as strong as a straight push and a small push is gentle.
        /// </summary>
        public static Vector2 Shape(Vector2 stick, GamepadFeel feel)
        {
            float length = stick.Length();
            return length <= 0f ? Vector2.Zero : stick * (Curve(Math.Min(length, 1f), feel) / length);
        }

        /// <summary>
        /// What the pad's right stick adds to MyInputExtensions.GetRotation: x pitch, y yaw, + right, nose up is negative.
        /// A full push to the right is 18 with the default options; the game turns that into 0.9 of the gyros' torque
        /// (MyShipController.MoveAndRotate: indicator / 20, clamped to 1).
        /// </summary>
        public static Vector2 Rotation(Vector2 rightStick, GamepadFeel feel, float sensitivity)
        {
            Vector2 turn = Shape(rightStick, feel) * (RotationScale * sensitivity);
            return new Vector2(feel.InvertPitch ? turn.Y : -turn.Y, turn.X);
        }

        /// <summary>
        /// What the pad's left stick and the up and down buttons add to MyInputExtensions.GetPositionDelta: x right,
        /// y up, z back, as the keys give it, each at most 1.
        /// </summary>
        public static Vector3 Move(Vector2 leftStick, bool up, bool down, GamepadFeel feel)
        {
            Vector2 push = Shape(leftStick, feel);
            return new Vector3(
                MathHelper.Clamp(push.X, -1f, 1f),
                (up ? 1f : 0f) - (down ? 1f : 0f),
                MathHelper.Clamp(-push.Y, -1f, 1f));
        }

        /// <summary>
        /// What the grips add to MyInputExtensions.GetRoll, + right: a pad rolls with the same curve on its stick, a full
        /// squeeze being 2 with the default options (which the gyros take as 0.4 of their torque).
        /// </summary>
        public static float Roll(float leftGrip, float rightGrip, GamepadFeel feel, float sensitivity) =>
            (Curve(GripLevel(rightGrip), feel) - Curve(GripLevel(leftGrip), feel)) * sensitivity;

        /// <summary>Tilting or twisting the held stick (<see cref="ControlGrip"/>): no push up to this, a full push from this, degrees.</summary>
        public const float TiltDeadzoneDegrees = 10f, TiltFullDegrees = 35f;

        /// <summary>
        /// What tilting the held stick sideways adds to MyInputExtensions.GetRoll, + right: the tilt past its deadzone, scaled
        /// so <see cref="TiltFullDegrees"/> is a full push, through the pad's curve, as the grips' roll is.
        /// </summary>
        public static float TiltRoll(float tiltRadians, GamepadFeel feel, float sensitivity) =>
            TiltPush(tiltRadians, feel) * sensitivity;

        /// <summary>
        /// What tilting the held stick forward and twisting it add to MyInputExtensions.GetRotation, as the pad's right stick
        /// does (<see cref="Rotation"/>: x pitch, y yaw, + right, nose up is negative): a forward tilt pitches the nose down, as
        /// a flight stick's does, whatever the player's "invert Y" says, and a twist to the right yaws right. Each is the tilt
        /// past its deadzone through the pad's curve, so a full tilt is a full push of the stick.
        /// </summary>
        public static Vector2 TiltTurn(float tiltForwardRadians, float twistRightRadians, GamepadFeel feel, float sensitivity) =>
            new Vector2(TiltPush(tiltForwardRadians, feel), TiltPush(twistRightRadians, feel)) * (RotationScale * sensitivity);

        /// <summary>
        /// A turn of the right stick's kind (x pitch, y yaw) kept to what the stick gives at a full push, with the direction
        /// kept: the thumbstick and the held stick's tilt together never turn the ship harder than a pad pushed all the way.
        /// </summary>
        public static Vector2 LimitTurn(Vector2 turn, GamepadFeel feel, float sensitivity) =>
            Vector2.ClampToSphere(turn, Curve(1f, feel) * RotationScale * sensitivity);

        /// <summary>A tilt or twist past its deadzone put through the pad's curve, with its sign: 0 inside the deadzone, a full push from <see cref="TiltFullDegrees"/>.</summary>
        private static float TiltPush(float tiltRadians, GamepadFeel feel)
        {
            float size = MathHelper.ToDegrees(Math.Abs(tiltRadians));
            if (!(size > TiltDeadzoneDegrees))
                return 0f;
            float level = Math.Min(1f, (size - TiltDeadzoneDegrees) / (TiltFullDegrees - TiltDeadzoneDegrees));
            return Math.Sign(tiltRadians) * Curve(level, feel);
        }

        /// <summary>The squeeze past its deadzone, 0 to 1.</summary>
        private static float GripLevel(float squeeze) =>
            squeeze <= GripDeadzone ? 0f : (Math.Min(squeeze, 1f) - GripDeadzone) / (1f - GripDeadzone);
    }
}
