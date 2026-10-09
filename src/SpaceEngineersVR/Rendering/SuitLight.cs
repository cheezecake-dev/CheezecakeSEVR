using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Components;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Lights;
using SpaceEngineersVR.Tracking;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Puts the suit's lamp where the player's eyes are. The game lights the world from one spotlight on the character
    /// (<c>MyRenderComponentCharacter.UpdateLightPosition</c>): the head bone, plus the definition's LightOffset of 15 cm up
    /// (Characters.sbc, 0 / 0.15 / 0), turned by the head's pitch and roll (no yaw) about the bone. That is right for a flat
    /// screen, where the camera is the bone. In the headset the eye is the bone plus wherever the player's head is (the
    /// body's own crouch, a lean, a different height), and the head's turn is the player's, not the body's: the lamp
    /// sat 15 cm or more above the eyes, behind them when the player leans in, and pointed along the body, not where the head looks.
    /// </summary>
    /// <remarks>
    /// One light, not two: the spotlight's texture (Textures\Lights\dual_reflector_2) is already a pair of lobes, one for each
    /// lamp, so a second source would draw four, and would double the spotlight's shadows. The light is placed from the same head
    /// matrix the camera is built from (<see cref="CharacterHead"/>, one snapshot per simulation frame), so the lamp and the
    /// view agree within the frame. Anything else - third person, a seat, no head tracked - keeps the game's own placement.
    /// RenderDebug SuitLightEye=0 gives the game's placement back; SuitLightEye=2 also logs once a second whether the lamp was
    /// placed in the frame the camera was sent from.
    /// </remarks>
    internal static class SuitLight
    {
        /// <summary>The lamp's place from the eye, in the head's own axes, metres: a few cm above it, a little ahead of the face.</summary>
        private const float Up = 0.04f, Ahead = 0.06f;

        /// <summary>RenderDebug SuitLightEye=0: the game's own placement.</summary>
        public static bool Off { get; set; }

        /// <summary>RenderDebug SuitLightEye=2: log how the lamp's frame compares with the camera's, and charge an empty suit so the lamp has power.</summary>
        public static bool Diag { get; set; }

        private static bool logged;
        private static MyCharacter charged;
        private static long lightFrame = -1;
        private static int updates, cameras, behind, ahead;
        private static long diagAt;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyRenderComponentCharacter), nameof(MyRenderComponentCharacter.UpdateLightPosition)),
                prefix: new HarmonyMethod(typeof(SuitLight), nameof(Place)));
            harmony.Patch(AccessTools.Method(typeof(MyRenderProxy), nameof(MyRenderProxy.SetCameraViewMatrix)),
                postfix: new HarmonyMethod(typeof(SuitLight), nameof(CameraSent)));
        }

        /// <summary>The game's placement for everything but the local character's own first-person head.</summary>
        private static bool Place(MyRenderComponentCharacter __instance, MyLight ___m_light)
        {
            if (___m_light == null)
                return true;
            if (Off)
            {
                logged = false;
                return true;
            }
            MyCharacter character = __instance.Container?.Entity as MyCharacter;
            if (character == null || !CharacterHead.IsLocalFirstPerson(character) || !GameHead.Take())
                return true;

            // The camera's own head matrix: the tracked head's rotation, at the bone plus the head's offset from it.
            MatrixD eye = character.GetHeadMatrix(true, true, false, true, true);
            if (!eye.IsValid() || eye == MatrixD.Zero)
                return true;

            Vector3D at = eye.Translation + eye.Up * Up + eye.Forward * Ahead;
            ___m_light.ReflectorDirection = (Vector3)eye.Forward;
            ___m_light.ReflectorUp = (Vector3)eye.Up;
            ___m_light.Position = at;
            ___m_light.UpdateLight();

            if (!logged)
            {
                logged = true;
                LogMove(character, eye, at);
            }
            if (Diag)
            {
                if (charged != character)
                    Charge(character);
                Interlocked.Exchange(ref lightFrame, (long)MySandboxGame.Static.SimulationFrameCounter);
                Interlocked.Increment(ref updates);
            }
            return false;
        }

        /// <summary>Test aid (SuitLightEye=2): a world whose suit is empty has no lamp, so the battery gets a charge, once per character.</summary>
        private static void Charge(MyCharacter character)
        {
            charged = character;
            var source = character.SuitBattery?.ResourceSource;
            if (source == null || source.RemainingCapacityByType(MyResourceDistributorComponent.ElectricityId) > 1E-05f)
                return;
            source.SetRemainingCapacityByType(MyResourceDistributorComponent.ElectricityId, 1E-04f);
            Log.Info("Suit light: the suit's battery was empty; charged it for the test (SuitLightEye=2)");
        }

        /// <summary>Once: where the game would have put the lamp, and where it is now, in cm from the eye (right, up, forward).</summary>
        private static void LogMove(MyCharacter character, in MatrixD eye, in Vector3D now)
        {
            try
            {
                MatrixD game = character.GetHeadMatrix(false, true, false, true);
                Vector3D was = Vector3D.Transform((Vector3)character.Definition.LightOffset, game);
                double turned = Math.Acos(MathHelper.Clamp(Vector3D.Dot(game.Forward, eye.Forward), -1.0, 1.0)) * 180.0 / Math.PI;
                Log.Info($"Suit light: moved from {FromEye(eye, was)} to {FromEye(eye, now)} (cm right, up, forward of the eye); "
                         + $"aimed where the head looks, {turned:F0} degrees from where the game aims it");
            }
            catch (Exception e)
            {
                Log.Warn($"Suit light: could not log the move ({e.Message})");
            }
        }

        private static string FromEye(in MatrixD eye, in Vector3D point)
        {
            Vector3D d = point - eye.Translation;
            return $"{Vector3D.Dot(d, eye.Right) * 100:F1}, {Vector3D.Dot(d, eye.Up) * 100:F1}, {Vector3D.Dot(d, eye.Forward) * 100:F1}";
        }

        /// <summary>Game thread, as a camera goes to the renderer: was the lamp last placed in this simulation frame?</summary>
        private static void CameraSent()
        {
            if (!Diag)
                return;
            long frame = (long)MySandboxGame.Static.SimulationFrameCounter;
            long lamp = Interlocked.Read(ref lightFrame);
            Interlocked.Increment(ref cameras);
            if (lamp < frame)
                Interlocked.Increment(ref behind);
            else if (lamp > frame)
                Interlocked.Increment(ref ahead);
            long now = Stopwatch.GetTimestamp();
            if (diagAt == 0)
                diagAt = now;
            if (now - diagAt < Stopwatch.Frequency)
                return;
            diagAt = now;
            Log.Info($"Suit light diag: {updates} lamp updates, {cameras} cameras sent, lamp behind the camera's frame on {behind}, ahead on {ahead} (frame {frame})");
            updates = cameras = behind = ahead = 0;
        }
    }
}
