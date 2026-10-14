// Haptics, movement: the jetpack's thrust, the magnetic boots locking on, and a hard landing, felt in both hands.
using System;
using System.Diagnostics;
using Sandbox.Game.Entities.Character;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Haptics
    {
        // ---- Movement: felt in both hands, from what the character's body does (its velocity, its boots, its footing) ----

        /// <summary>The jetpack's rumble at full thrust, and the thrust (m/s², the jetpack's own over the body's mass) that is full.</summary>
        public const float JetpackAmplitude = 0.35f, JetpackFullThrust = 12f;

        /// <summary>Thrust under this (m/s²) is not felt: the drift of a body at rest.</summary>
        public const float JetpackLeast = 0.6f;

        /// <summary>How long each frame's rumble lasts, seconds: a little over a frame, so it runs on while it is asked for again.</summary>
        public const float RumbleSeconds = 0.05f;

        /// <summary>How fast the felt thrust follows the body's, per second: smooths the steps of the physics.</summary>
        public const float ThrustFollow = 12f;

        /// <summary>The boots locking on (a clunk) and letting go (a light click).</summary>
        public const float BootsLockAmplitude = 0.75f, BootsLockSeconds = 0.05f, BootsReleaseAmplitude = 0.3f, BootsReleaseSeconds = 0.03f;

        /// <summary>A landing at this speed along gravity (m/s) or more is felt, harder up to <see cref="LandingHardest"/>.</summary>
        public const float LandingLeast = 3f, LandingHardest = 10f;

        public const float LandingSoftAmplitude = 0.35f, LandingHardAmplitude = 1f, LandingSeconds = 0.08f;

        // The local character's body last frame.
        private static MyCharacter movedFor;
        private static Vector3 lastVelocity;
        private static float feltThrust;
        private static bool lastBoots, lastSupported;
        private static long movedAt;

        /// <summary>
        /// One frame of the movement felt in the hands, for the local character (from the character update the heartbeat uses):
        /// the jetpack's rumble while it runs, by the thrust its thrusters give (the jetpack's own FinalThrust over the body's
        /// mass, smoothed), so it rumbles under the dampeners' braking too, and not at all in a fall with the thrusters idle; a clunk as the magnetic boots lock on and a click as they
        /// let go; a jolt as the feet come down hard in gravity. Off with "Movement" (<see cref="VRSettings.HapticMovement"/>);
        /// nothing seated, dead or for a new character's first frame.
        /// </summary>
        private static void MovementFrame(MyCharacter character)
        {
            long now = Stopwatch.GetTimestamp();
            var physics = character.Physics;
            bool fresh = !ReferenceEquals(character, movedFor) || physics == null;
            float seconds = (float)((now - movedAt) / (double)Stopwatch.Frequency);
            Vector3 velocity = physics?.LinearVelocity ?? Vector3.Zero;
            bool boots = character.IsMagneticBootsEnabled;
            bool supported = physics?.CharacterProxy?.Supported ?? false;
            bool feel = !fresh && VRSettings.HapticMovement && !character.IsDead && !character.IsSitting && seconds > 0f && seconds < 0.25f;

            if (feel)
            {
                // The jetpack: the thrust its thrusters give, as the game has it. The body's own change of velocity was read
                // before, and a fall at the game's speed limit (no change, gravity still pulling) felt like full thrust.
                float mass = physics.Mass;
                float target = character.JetpackRunning && character.JetpackComp != null && mass > 0f
                    ? character.JetpackComp.FinalThrust.Length() / mass
                    : 0f;
                feltThrust += (target - feltThrust) * Math.Min(1f, ThrustFollow * seconds);
                if (character.JetpackRunning && feltThrust > JetpackLeast)
                {
                    float amplitude = JetpackAmplitude * Math.Min(1f, feltThrust / JetpackFullThrust);
                    Pulse(Hand.Left, amplitude, RumbleSeconds);
                    Pulse(Hand.Right, amplitude, RumbleSeconds);
                }

                // The boots.
                if (boots != lastBoots)
                {
                    float amplitude = boots ? BootsLockAmplitude : BootsReleaseAmplitude, length = boots ? BootsLockSeconds : BootsReleaseSeconds;
                    Pulse(Hand.Left, amplitude, length);
                    Pulse(Hand.Right, amplitude, length);
                }

                // A landing in gravity: the speed it came down at, along gravity, as the feet found footing.
                Vector3 gravity = character.Gravity;
                if (supported && !lastSupported && !boots && gravity.LengthSquared() > 0.25f)
                {
                    float speed = Vector3.Dot(lastVelocity, Vector3.Normalize(gravity));
                    if (speed >= LandingLeast)
                    {
                        float amplitude = MathHelper.Lerp(LandingSoftAmplitude, LandingHardAmplitude,
                            MathHelper.Clamp((speed - LandingLeast) / (LandingHardest - LandingLeast), 0f, 1f));
                        Pulse(Hand.Left, amplitude, LandingSeconds);
                        Pulse(Hand.Right, amplitude, LandingSeconds);
                    }
                }
            }
            else
            {
                feltThrust = 0f;
            }

            movedFor = character;
            movedAt = now;
            lastVelocity = velocity;
            lastBoots = boots;
            lastSupported = supported;
        }
    }
}
