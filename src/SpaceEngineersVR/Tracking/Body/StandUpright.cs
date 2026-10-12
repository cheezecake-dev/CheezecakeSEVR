using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Rendering;
using VRage.Game;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Coming into gravity tilted, or turning the jetpack off in it, the body stands upright in one step under a cut's
    /// fade (<see cref="CameraCut"/>), so the game does not roll and pitch the horizon round the player over the next second.
    /// </summary>
    /// <remarks>
    /// What the game does (decompiled): with the jetpack off, <c>MyCharacter.UpdatePhysicalMovement</c> turns the body toward
    /// gravity's up every frame through <c>UpdateStandup</c>, at most 0.04 rad (2.3 deg) a step, about the body's middle, and
    /// hands the result to the physics proxy (<c>SetForwardAndUp</c>); the first-person view is built on the body
    /// (<see cref="CharacterHead"/>), so a body upside down rolls the view through 180 deg over 1.3 s. Getting up from a seat
    /// is already upright on its own frame (<see cref="ExitUpright"/>); this is every other way in.
    ///
    /// Here, after the game's step, for the local character standing toward gravity alone (RotationToSupport None, the
    /// player's astronaut): when more than <see cref="MinDegrees"/> are left, the rest of the turn is made at once, the same
    /// shortest turn the game's steps add up to, so the heading is kept. Less is left to the game: a few frames of it are not
    /// felt. Magnetic boots stand the body on the surface through the same method and are not touched
    /// (<see cref="MagBootsSmoothing"/>). RenderDebug StandUpright=0 leaves it all to the game.
    /// </remarks>
    internal static class StandUpright
    {
        /// <summary>RenderDebug StandUpright=0: the game turns the body upright a step a frame, the view with it.</summary>
        public static bool Off { get; set; }

        /// <summary>A turn left bigger than this, degrees, is made at once; the game's steps finish a smaller one in a few frames.</summary>
        public const float MinDegrees = 10f;

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(StandUpright));
        private static readonly AccessTools.FieldRef<MyCharacter, Vector3> characterGravity = AccessTools.FieldRefAccess<MyCharacter, Vector3>("m_gravity");

        /// <summary>Times the body was stood upright at once this run, for the drive's report.</summary>
        public static int Count { get; private set; }

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyCharacter), "UpdateStandup"),
                postfix: new HarmonyMethod(typeof(StandUpright), nameof(After)));
        }

        private static void After(MyCharacter __instance, ref Vector3 gravity, ref Vector3 chUp, ref Vector3 chForward)
        {
            if (Off || !guard.On || !XrSession.Focused)
                return;
            try
            {
                if (!ReferenceEquals(__instance, MySession.Static?.LocalCharacter)
                    || __instance.Definition.RotationToSupport != MyEnumCharacterRotationToSupport.None
                    || gravity != characterGravity(__instance) // the boots' surface, not gravity
                    || gravity.LengthSquared() < 1e-4f || chUp.LengthSquared() < 1e-8f)
                    return;
                Vector3 up = -Vector3.Normalize(gravity);
                Vector3 bodyUp = Vector3.Normalize(chUp);
                float angle = (float)Math.Acos(MathHelper.Clamp(bodyUp.Dot(up), -1f, 1f));
                if (angle < MathHelper.ToRadians(MinDegrees))
                    return;
                Vector3 axis = bodyUp.Cross(up);
                if (axis.LengthSquared() < 1e-8f)
                    axis = chForward; // upside down: no shortest turn, so roll over the way the body faces
                if (axis.LengthSquared() < 1e-8f)
                    return;
                Matrix turn = Matrix.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
                chUp = Vector3.TransformNormal(chUp, turn);
                chForward = Vector3.TransformNormal(chForward, turn);
                Count++;
                CameraCut.Mark($"the body stood upright in gravity, {MathHelper.ToDegrees(angle):0} deg at once");
            }
            catch (Exception e)
            {
                guard.Failed(e, "Stand upright: turning the body upright at once failed; the game turns it a step a frame");
            }
        }
    }
}
