using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.Utils;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// The "Camera shake" setting: how much of the game's own camera movement reaches the view when the head did not make it
    /// (Off: none, Low: a quarter, Full: all). A head in a headset that is moved by something other than the head is what
    /// makes people sick, so each place the game moves the camera is scaled where it does it, and the hands, the HUD and the
    /// eyes, which are all built from that camera, stay together with it.
    /// </summary>
    /// <remarks>
    /// What the game moves the camera by (decompiled; the game has no option for any of it):
    /// <list type="bullet">
    /// <item><b>MyCameraShake</b>, one per camera (<c>MyCamera.CameraShake</c>), fed by <c>AddShake</c> from: the hand drill
    /// (<c>MyDrillBase.PerformCameraShake</c>, every frame it drills something), the ship drill, the hand grinder (every frame
    /// its effect is on), the rifle (each shot), damage to the character (<c>MyCharacter.DoDamage</c>, up to the camera's full
    /// 15), explosions (<c>MyExplosion</c>, by distance) and the cockpit when the ship speeds up along its way
    /// (<c>MyCockpit.AddShake</c>). <c>MyCamera.Update</c> adds its <c>UpdateShake</c> position to the view, a random jolt every
    /// frame; the direction it also returns is never used. Scaled here: <see cref="AfterShake"/>.</item>
    /// <item><b>MyCameraSpring</b> (<c>MyCamera.CameraSpring</c>): on in every seat and in third person, it lags the camera
    /// behind the ship's velocity changes, and explosions kick it. <c>MyCamera.Update</c> adds it the same way. Scaled:
    /// <see cref="AfterSpring"/>.</item>
    /// <item><b>The head bob</b> in <c>MyCharacter.GetHeadMatrixInternal</c>: while walking starts or stops, the first-person
    /// head matrix is raised by <c>0.03 * offset * (sin(10 t) + 3)</c> metres, offset ramping 0 to 3 and back (a bump of up to
    /// about 13 cm over half a second). It moves the head bone the plugin hangs the eye on. Scaled by replacing the 0.03:
    /// <see cref="Bob"/>.</item>
    /// <item>The rifle's FOV spring (<c>AddFovSpring</c>): the headset's field of view is the headset's, so it does not reach
    /// the view. Not touched. The animation's own head motion (the walking and landing of the head bone) is not touched either.</item>
    /// </list>
    /// The tools' and the ship's physical shake (<c>EnableToolShake</c>, <c>ApplyShakeForce</c>) pushes the grid, not the camera,
    /// and is left to the game. RenderDebug CameraShake=0/1/2 takes the place of the setting.
    /// </remarks>
    internal static class CameraShake
    {
        /// <summary>The share of the game's camera movement that reaches the view now.</summary>
        public static float Scale => VRSettings.CameraShakeScale;

        private static readonly string[] LevelNames = { "Off", "Low", "Full" };

        // This frame's offsets as the game made them, before the setting: camera-local metres. Game thread (Draw).
        private static Vector3 shakeRaw, springRaw;
        private static Vector3D cameraBefore;

        // What the last second moved the view by, for the log: the measure of what the setting did.
        private static readonly object gate = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static long windowStart;
        private static int frames, active;
        private static double appliedMax, appliedSum, rawMax, rawSum, shakeMax, springMax;
        private static double eyeLow = double.MaxValue, eyeHigh = double.MinValue;
        private static int eyeSamples;

        public static void Patch(Harmony harmony)
        {
            Hook("shake", () => harmony.Patch(AccessTools.Method(typeof(MyCameraShake), nameof(MyCameraShake.UpdateShake)),
                postfix: new HarmonyMethod(typeof(CameraShake), nameof(AfterShake))));
            Hook("spring", () => harmony.Patch(AccessTools.Method(typeof(MyCameraSpring), nameof(MyCameraSpring.Update)),
                postfix: new HarmonyMethod(typeof(CameraShake), nameof(AfterSpring))));
            Hook("measure", () => harmony.Patch(AccessTools.Method(typeof(MyCamera), nameof(MyCamera.Update)),
                prefix: new HarmonyMethod(typeof(CameraShake), nameof(BeforeCamera)),
                postfix: new HarmonyMethod(typeof(CameraShake), nameof(AfterCamera))));
            Hook("head bob", () => harmony.Patch(AccessTools.Method(typeof(MyCharacter), "GetHeadMatrixInternal"),
                transpiler: new HarmonyMethod(typeof(CameraShake), nameof(BobTranspiler))));
        }

        private static void Hook(string what, Action patch)
        {
            try
            {
                patch();
            }
            catch (Exception e)
            {
                // The game's own movement stays; nothing else depends on this.
                Log.Error(e, $"Camera shake: the {what} could not be hooked; the game's camera movement stays as it is");
            }
        }

        /// <summary>The camera shake's position for this frame (camera-local metres), scaled. The direction is never used by the camera.</summary>
        private static void AfterShake(ref Vector3 outPos, ref Vector3 outDir)
        {
            shakeRaw = outPos;
            float scale = Scale;
            outPos *= scale;
            outDir *= scale;
        }

        /// <summary>The camera spring's offset for this frame (camera-local metres), scaled.</summary>
        private static void AfterSpring(ref Vector3 newCameraLocalOffset)
        {
            springRaw = newCameraLocalOffset;
            newCameraLocalOffset *= Scale;
        }

        private static void BeforeCamera(MyCamera __instance)
        {
            shakeRaw = springRaw = Vector3.Zero;
            cameraBefore = __instance.WorldMatrix.Translation;
        }

        /// <summary>
        /// The camera has been moved by the shake and the spring: how far from where the head's view put it (the same camera,
        /// before and after), and how far it would have been with the setting on Full. Logged once a second while there was any.
        /// </summary>
        private static void AfterCamera(MyCamera __instance)
        {
            double applied = Vector3D.Distance(cameraBefore, __instance.WorldMatrix.Translation);
            double raw = (shakeRaw + springRaw).Length();
            lock (gate)
            {
                frames++;
                if (raw > 0.0 || applied > 0.0)
                    active++;
                appliedSum += applied;
                rawSum += raw;
                appliedMax = Math.Max(appliedMax, applied);
                rawMax = Math.Max(rawMax, raw);
                shakeMax = Math.Max(shakeMax, shakeRaw.Length());
                springMax = Math.Max(springMax, springRaw.Length());
                long now = clock.ElapsedMilliseconds;
                if (now - windowStart >= 1000)
                    Report(now);
            }
        }

        /// <summary>The eye's height above the character's feet in a head matrix built for the view (first person, on foot), for the log.</summary>
        public static void NoteEye(MyCharacter character, in Vector3D eye)
        {
            MatrixD at = character.PositionComp.WorldMatrixRef;
            double height = Vector3D.Dot(eye - at.Translation, at.Up);
            lock (gate)
            {
                eyeLow = Math.Min(eyeLow, height);
                eyeHigh = Math.Max(eyeHigh, height);
                eyeSamples++;
            }
        }

        /// <summary>Caller holds the gate.</summary>
        private static void Report(long now)
        {
            double seconds = (now - windowStart) / 1000.0;
            bool moved = rawMax >= 0.0005 || appliedMax >= 0.0005;
            bool bobbed = eyeSamples > 0 && eyeHigh - eyeLow >= 0.002;
            if (windowStart != 0 && (moved || bobbed))
            {
                string level = LevelNames[Math.Max(0, Math.Min(LevelNames.Length - 1, VRSettings.CameraShakeLevel))];
                string text = $"Camera shake {level}: ";
                if (moved)
                    text += $"in {seconds:F1} s the view moved off the head's by up to {appliedMax * 1000.0:F1} mm (mean {appliedSum / Math.Max(1, frames) * 1000.0:F2} mm over {frames} frames, {active} with a push); " +
                            $"as the game makes it (Full) up to {rawMax * 1000.0:F1} mm (mean {rawSum / Math.Max(1, frames) * 1000.0:F2}); shake up to {shakeMax * 1000.0:F1}, spring up to {springMax * 1000.0:F1} mm. ";
                if (bobbed)
                    text += $"Eye height above the feet ranged {(eyeHigh - eyeLow) * 1000.0:F0} mm ({eyeLow:F3} to {eyeHigh:F3} m, {eyeSamples} head matrices).";
                Log.Info(text.TrimEnd());
            }
            windowStart = now;
            frames = active = eyeSamples = 0;
            appliedMax = appliedSum = rawMax = rawSum = shakeMax = springMax = 0.0;
            eyeLow = double.MaxValue;
            eyeHigh = double.MinValue;
        }

        /// <summary>The share of the game's head bob for this character's head matrix: the setting for the local player's, all of it for any other.</summary>
        public static float Bob(MyCharacter character)
        {
            return character == MySession.Static?.LocalCharacter ? Scale : 1f;
        }

        /// <summary>
        /// MyCharacter.GetHeadMatrixInternal sets <c>num3 = 0.03f</c>, the bob's height per unit of offset, and nothing else uses
        /// that constant: it becomes <c>0.03f * Bob(this)</c>.
        /// </summary>
        private static IEnumerable<CodeInstruction> BobTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var changed = new List<CodeInstruction>();
            int found = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                changed.Add(instruction);
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value && Math.Abs(value - 0.03f) < 1e-6f)
                {
                    found++;
                    changed.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    changed.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CameraShake), nameof(Bob))));
                    changed.Add(new CodeInstruction(OpCodes.Mul));
                }
            }
            if (found != 1)
                Log.Warn($"Camera shake: found {found} head bob constants in GetHeadMatrixInternal, expected 1; the walking head bob is " + (found == 0 ? "not scaled" : "scaled at each"));
            return changed;
        }
    }
}
