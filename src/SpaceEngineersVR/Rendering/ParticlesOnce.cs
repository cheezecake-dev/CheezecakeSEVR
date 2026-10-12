using System;
using System.Reflection;
using HarmonyLib;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// GPU particles (MyGPUParticleRenderer: weather, dust, sparks, smoke, thruster and explosion effects) step once a frame,
    /// in the first eye's pass. The renderer gathers its emitters (MyGPUParticleRenderer.Update, where each effect also adds
    /// its bursts) and emits and simulates them (Run: Emit, Simulate) in every DrawGameScene, each time by the whole frame's
    /// time. With a pass per eye, and the external view's, particles moved two or three times as fast as on a flat screen,
    /// bursts came out two or three times over, and the second eye drew them a step on from the first, so a moving particle
    /// sat in a different place for each eye.
    /// Now the second eye's pass and the external view's skip the gather, the emit and the simulation, and draw the first
    /// eye's particles from their own camera: the particle buffer, the alive list, the emitter buffer and the indirect draw
    /// arguments still hold what the first eye's step left.
    /// </summary>
    /// <remarks>
    /// Run swaps its two alive lists before it emits. In a skipped pass they are swapped once beforehand, so Run's swap puts
    /// the first eye's back and it draws the list the first eye's simulation wrote. RenderDebug ParticlesEachPass=1 gives every
    /// pass its own step again, as the renderer does.
    /// </remarks>
    internal static class ParticlesOnce
    {
        /// <summary>Every pass steps the particles, as the renderer does (RenderDebug ParticlesEachPass=1).</summary>
        public static volatile bool EachPass;

        private static FieldInfo alive1, alive2, aliveIndex, emitterCount;
        private static volatile bool firstStepped;
        private static bool skipping, logged;

        public static void Patch(Harmony harmony)
        {
            Type renderer = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyGPUParticleRenderer", throwOnError: true);
            alive1 = ReflectionUtil.Field(renderer, "m_aliveIndexBuffer1");
            alive2 = ReflectionUtil.Field(renderer, "m_aliveIndexBuffer2");
            aliveIndex = ReflectionUtil.Field(renderer, "m_aliveIndexBufferIndex");
            emitterCount = ReflectionUtil.Field(renderer, "m_emitterCount");
            harmony.Patch(Method(renderer, "Update"), prefix: new HarmonyMethod(typeof(ParticlesOnce), nameof(BeforeUpdate)));
            harmony.Patch(Method(renderer, "Run"), prefix: new HarmonyMethod(typeof(ParticlesOnce), nameof(BeforeRun)),
                finalizer: new HarmonyMethod(typeof(ParticlesOnce), nameof(AfterRun)));
            harmony.Patch(Method(renderer, "Emit"), prefix: new HarmonyMethod(typeof(ParticlesOnce), nameof(NotSkipping)));
            harmony.Patch(Method(renderer, "Simulate"), prefix: new HarmonyMethod(typeof(ParticlesOnce), nameof(NotSkipping)));
            Log.Info("GPU particles: hooked; they step once a frame, in the first eye, unless RenderDebug ParticlesEachPass=1");
        }

        /// <summary>Render thread, before the first eye's pass.</summary>
        public static void BeginFrame() => firstStepped = false;

        /// <summary>True in a pass that draws the first eye's particles instead of stepping them again.</summary>
        private static bool Skip
        {
            get
            {
                int pass = ScenePass.Current;
                return !EachPass && firstStepped && (pass == ScenePass.SecondEye || pass == ScenePass.Chase);
            }
        }

        /// <summary>MyGPUParticleRenderer.Update, a worker job of the pass: false keeps the first eye's emitters and counts.</summary>
        private static bool BeforeUpdate(ref int __result)
        {
            if (!Skip)
                return true;
            __result = (int)emitterCount.GetValue(null);
            return false;
        }

        /// <summary>MyGPUParticleRenderer.Run, render thread.</summary>
        private static void BeforeRun()
        {
            if (ScenePass.Current == ScenePass.FirstEye)
            {
                firstStepped = true;
                return;
            }
            if (!Skip)
                return;
            object list = alive1.GetValue(null);
            alive1.SetValue(null, alive2.GetValue(null));
            alive2.SetValue(null, list);
            aliveIndex.SetValue(null, 1 - (int)aliveIndex.GetValue(null));
            skipping = true;
            if (!logged)
            {
                logged = true;
                Log.Info($"GPU particles: pass {ScenePass.Current} drew the first eye's step (first time)");
            }
        }

        private static Exception AfterRun(Exception __exception)
        {
            skipping = false;
            return __exception;
        }

        /// <summary>MyGPUParticleRenderer.Emit and Simulate: false in a skipped pass.</summary>
        private static bool NotSkipping() => !skipping;

        private static MethodInfo Method(Type type, string name) =>
            AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);
    }
}
