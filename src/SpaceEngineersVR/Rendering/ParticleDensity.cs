using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The Particle density setting: how many particles the game's continuous GPU effects (dust, smoke, sparks, thruster flames,
    /// weather) make, as a share of what the game's own particle quality gives. The game keeps that quality as a multiplier
    /// (MyGPUEmitters.ParticleCountMultiplier: 0.25 on low, 1 on the rest, set again whenever the quality changes) and reads it
    /// in one place, where each emitter works out how many particles to emit this frame (MyGPUEmitter.Update). That read is
    /// scaled by the setting, so the setting stands whatever the game does to the multiplier, and the game's quality still
    /// applies under it. Bursts an effect asks for outright (a fixed count a frame) are not part of the multiplier and are not scaled.
    /// </summary>
    /// <remarks>
    /// The emitters are gathered once a frame, in the first eye's pass (<see cref="ParticlesOnce"/> skips the gather in the other
    /// passes), so each particle is made once whatever the number of eyes. At 100%, or while the picture is the game's
    /// (<see cref="StereoPatches.VRPicture"/>), the game's own multiplier comes back unchanged.
    /// </remarks>
    internal static class ParticleDensity
    {
        private static MethodInfo multiplier;
        private static bool patched;

        public static void Patch(Harmony harmony)
        {
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type emitters = render11.GetType("VRageRender.MyGPUEmitters", throwOnError: true);
            Type emitter = render11.GetType("VRageRender.MyGPUEmitter", throwOnError: true);
            MethodInfo update = AccessTools.Method(emitter, "Update") ?? throw new MissingMethodException(emitter.Name, "Update");
            multiplier = AccessTools.PropertyGetter(emitters, "ParticleCountMultiplier") ?? throw new MissingMethodException(emitters.Name, "ParticleCountMultiplier");
            harmony.Patch(update, transpiler: new HarmonyMethod(typeof(ParticleDensity), nameof(Scaled)));
            if (!patched)
                throw new MissingMethodException("GPU particle density: the emitter's use of the particle multiplier has changed shape; the setting does nothing");
            Log.Info("GPU particle density: hooked; the Particle density setting scales the emitters' multiplier");
        }

        /// <summary>The one read of the game's particle multiplier goes through <see cref="Scale"/>.</summary>
        // Adapted from ColbyJack1134's SpaceEngineersVR (Patches/ParticleDensityPatch.cs), with his permission.
        private static IEnumerable<CodeInstruction> Scaled(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo scale = AccessTools.Method(typeof(ParticleDensity), nameof(Scale));
            int found = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (instruction.Calls(multiplier))
                {
                    found++;
                    yield return new CodeInstruction(OpCodes.Call, scale);
                }
            }
            patched = found == 1;
        }

        /// <summary>The game's multiplier times the setting, while the picture is VR's.</summary>
        private static float Scale(float native) => StereoPatches.VRPicture ? native * VRSettings.ParticleDensity : native;
    }
}
