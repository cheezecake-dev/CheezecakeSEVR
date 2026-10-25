// MotionExtrapolation: the held tool's flames, sparks and dust moved on with the tool.

using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Weapons;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Render.Particles;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <remarks>
    /// The held tool's particle effects (the welder's flame and sparks, the grinder's sparks, the drill's dust and sparks) are
    /// GPU emitters placed in the world, not under the tool's render object: the tools create them with no render parent
    /// (MyEngineerToolBase.StartEffect, MyWelder.UpdateFlameEffect, MyDrillBase: the parent is the tool's cell parent, none for
    /// a hand tool), and the game moves each with the tool by its world matrix (WorldPositionChanged). So while the tool and the
    /// hand were moved on by <see cref="Begin"/>'s shift, the emitter kept the game frame's place: walking or flying, the flame
    /// stood off the tip by the carrier's velocity times how far the frame's age is from the mean, back and forth 12 times a
    /// second (at 6 m/s up to 4 cm), and trailed behind it while emitting. Each emitter is given the same shift at the one moment
    /// it is read for the frame (MyGPUEmitter.Update, as MyGPUEmitters.Gather collects them in the first eye's pass, after
    /// MyRenderParticlesManager.Draw has filled it from the effect's matrix) and put back straight after, so the particles start
    /// at the drawn tip and its motion between frames (the emitter's PositionDelta) is the drawn tool's. RenderDebug
    /// ToolEffectsRide=0 leaves them where the game frame put them, for comparison.
    /// </remarks>
    internal static partial class MotionExtrapolation
    {
        /// <summary>RenderDebug ToolEffectsRide=0: the held tool's particle effects are left where the game frame put them, as before.</summary>
        public static bool EffectsOff { get; set; }

        private static readonly AccessTools.FieldRef<MyEngineerToolBase, MyParticleEffect> toolEffect =
            AccessTools.FieldRefAccess<MyEngineerToolBase, MyParticleEffect>("m_toolEffect");
        private static readonly AccessTools.FieldRef<MyEngineerToolBase, MyParticleEffect> toolSecondaryEffect =
            AccessTools.FieldRefAccess<MyEngineerToolBase, MyParticleEffect>("m_toolSecondaryEffect");
        private static readonly AccessTools.FieldRef<MyWelder, MyParticleEffect> welderFlame =
            AccessTools.FieldRefAccess<MyWelder, MyParticleEffect>("m_flameEffect");
        private static readonly AccessTools.FieldRef<MyHandDrill, MyDrillBase> drillBase =
            AccessTools.FieldRefAccess<MyHandDrill, MyDrillBase>("m_drillBase");

        // ---- game thread ----

        /// <summary>The render ids of the held tool's particle effects that are running, into the sample.</summary>
        private static void AddEffects(Sample s, MyEntity tool)
        {
            if (EffectsOff)
                return;
            if (tool is MyEngineerToolBase engineer)
            {
                s.AddEffect(toolEffect(engineer));
                s.AddEffect(toolSecondaryEffect(engineer));
                if (engineer is MyWelder welder)
                    s.AddEffect(welderFlame(welder));
            }
            else if (tool is MyHandDrill drill && drillBase(drill) is MyDrillBase drilling)
            {
                s.AddEffect(drilling.DustParticles);
                s.AddEffect(drilling.SparkEffect);
            }
        }

        // ---- render thread ----

        private static FieldInfo particlesManager, generations, emitterField;
        private static MethodInfo getEffect;
        private static AccessTools.FieldRef<object, MyGPUEmitterData> emitterData;
        private static bool effectsHooked, effectsFailed;

        // The emitters moved on this frame, and by how much: set in Begin, cleared in End.
        private static readonly object[] riding = new object[16];
        private static int ridingCount;
        private static Vector3D ridingShift;

        // For the log line: frames with effects moved, the most emitters at once.
        private static int effectFrames, effectEmitters;
        private static bool effectsLogged;

        /// <summary>At start-up: the hook on the emitter's per-frame read, and the renderer's members the emitters are found by.</summary>
        public static void PatchEffects(Harmony harmony)
        {
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type managers = render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
            Type manager = render11.GetType("VRage.Render11.Particles.MyRenderParticlesManager", throwOnError: true);
            Type effect = render11.GetType("VRage.Render11.Particles.MyRenderParticleEffect", throwOnError: true);
            Type generation = render11.GetType("VRage.Render11.Particles.MyParticleGPUGeneration", throwOnError: true);
            Type emitter = render11.GetType("VRageRender.MyGPUEmitter", throwOnError: true);
            particlesManager = AccessTools.Field(managers, "ParticleEffectsManager") ?? throw new MissingFieldException(managers.Name, "ParticleEffectsManager");
            getEffect = AccessTools.Method(manager, "Get", new[] { typeof(uint) }) ?? throw new MissingMethodException(manager.Name, "Get(uint)");
            generations = AccessTools.Field(effect, "m_generations") ?? throw new MissingFieldException(effect.Name, "m_generations");
            emitterField = AccessTools.Field(generation, "m_emitter") ?? throw new MissingFieldException(generation.Name, "m_emitter");
            emitterData = AccessTools.FieldRefAccess<MyGPUEmitterData>(emitter, "EmitterData");
            MethodInfo update = AccessTools.Method(emitter, "Update") ?? throw new MissingMethodException(emitter.Name, "Update");
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(MotionExtrapolation), nameof(EmitterRead)),
                postfix: new HarmonyMethod(typeof(MotionExtrapolation), nameof(EmitterReadDone)));
            effectsHooked = true;
            Log.Info("Motion extrapolation: the held tool's flames, sparks and dust are moved on with it (RenderDebug ToolEffectsRide=0 leaves them)");
        }

        /// <summary>Render thread, in <see cref="Begin"/>: the sample's effects' emitters, to be moved on by <paramref name="shift"/> as they are read.</summary>
        private static void RideEffects(Sample s, in Vector3D shift)
        {
            ridingCount = 0;
            if (!effectsHooked || effectsFailed || EffectsOff || s.EffectCount == 0)
                return;
            try
            {
                object manager = particlesManager.GetValue(null);
                if (manager == null)
                    return;
                for (int i = 0; i < s.EffectCount; i++)
                {
                    object effect = getEffect.Invoke(manager, new object[] { s.Effects[i] });
                    if (effect == null || !(generations.GetValue(effect) is IList list))
                        continue;
                    foreach (object generation in list)
                    {
                        object emitter = generation != null ? emitterField.GetValue(generation) : null;
                        if (emitter != null && ridingCount < riding.Length && emitterData(emitter).ParentID == uint.MaxValue)
                            riding[ridingCount++] = emitter;
                    }
                }
                ridingShift = shift;
                if (ridingCount > 0)
                {
                    effectFrames++;
                    effectEmitters = Math.Max(effectEmitters, ridingCount);
                    if (!effectsLogged)
                    {
                        effectsLogged = true;
                        Log.Info($"Motion extrapolation: the held tool's particle effects ride with it ({s.EffectCount} effect(s), {ridingCount} emitter(s))");
                    }
                }
            }
            catch (Exception e)
            {
                effectsFailed = true;
                ridingCount = 0;
                Log.Error(e, "Motion extrapolation: finding the held tool's particle emitters failed; its flames and sparks keep the game frame's place for the rest of the run");
            }
        }

        /// <summary>Render thread, in <see cref="End"/>: nothing rides any more.</summary>
        private static void EndEffects() => ridingCount = 0;

        /// <summary>Prefix of MyGPUEmitter.Update: an emitter of the held tool is read moved on with it.</summary>
        private static void EmitterRead(object __instance, out bool __state)
        {
            __state = false;
            if (ridingCount == 0)
                return;
            for (int i = 0; i < ridingCount; i++)
            {
                if (!ReferenceEquals(riding[i], __instance))
                    continue;
                emitterData(__instance).WorldPosition += ridingShift;
                __state = true;
                return;
            }
        }

        /// <summary>Postfix of MyGPUEmitter.Update: the emitter goes back where the game frame put it.</summary>
        private static void EmitterReadDone(object __instance, bool __state)
        {
            if (__state)
                emitterData(__instance).WorldPosition -= ridingShift;
        }

        private static string EffectStats()
        {
            string text = effectFrames == 0 ? "" : $"; the tool's effects moved on in {effectFrames} frames, up to {effectEmitters} emitters";
            effectFrames = effectEmitters = 0;
            return text;
        }
    }
}
