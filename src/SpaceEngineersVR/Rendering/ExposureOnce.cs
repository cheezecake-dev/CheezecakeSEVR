using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SharpDX.Mathematics.Interop;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Auto exposure (MyEyeAdaptation) adapts once a frame, to what both eyes see, and both eyes are tone mapped with the
    /// same exposure. The renderer runs the adaptation in every DrawGameScene: it builds a luminance histogram of the
    /// picture, then moves the exposure a step towards the one the histogram asks for, by the whole frame's time, from the
    /// exposure the last run left (the pair m_autoExposure, swapped after each step). With a pass per eye it stepped twice a
    /// frame, so the view adapted twice as fast as on a flat screen, and the eyes were tone mapped one step apart, each
    /// towards what only it could see: with the sun or a lamp in view of one eye only, the eyes differed in brightness.
    /// Now the first eye's run builds the histogram and its step is undone; the second eye's run adds its own picture to the
    /// same histogram and takes the frame's one step; and both eyes are tone mapped with the exposure the frame began with,
    /// a frame behind, as the next frame starts from the new one.
    /// </summary>
    /// <remarks>
    /// The histogram is normalised by its total when the step reads it, so two eyes' worth of pixels weigh as one picture.
    /// The external view's pass has an exposure pair of its own (<see cref="ExternalView"/>) and adapts as the renderer does.
    /// RenderDebug ExposureEachPass=1 gives every pass its own step again.
    /// </remarks>
    internal static class ExposureOnce
    {
        /// <summary>Every pass adapts the exposure, as the renderer does (RenderDebug ExposureEachPass=1).</summary>
        public static volatile bool EachPass;

        /// <summary>What the current run of MyEyeAdaptation.Run does: its own step, as the renderer does, or its eye's part of the frame's one step.</summary>
        private enum Role { Renderer, FirstEye, SecondEye }

        private static FieldInfo autoExposure;
        private static MethodInfo clearUav;
        private static readonly object[] clearArgs = new object[2];
        private static Role role;
        private static bool patched, histogramKept, adding, held, logged;

        public static void Patch(Harmony harmony)
        {
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type adaptation = render11.GetType("VRageRender.MyEyeAdaptation", throwOnError: true);
            Type context = render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);
            Type uav = render11.GetType("VRage.Render11.Resources.IUavBindable", throwOnError: true);
            autoExposure = AccessTools.Field(adaptation, "m_autoExposure") ?? throw new MissingFieldException(adaptation.Name, "m_autoExposure");
            clearUav = AccessTools.Method(context, "ClearUav", new[] { uav, typeof(RawInt4) }) ?? throw new MissingMethodException(context.Name, "ClearUav");
            MethodInfo method = AccessTools.Method(adaptation, "Run") ?? throw new MissingMethodException(adaptation.Name, "Run");
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(ExposureOnce), nameof(BeforeRun)),
                transpiler: new HarmonyMethod(typeof(ExposureOnce), nameof(KeepHistogram)),
                finalizer: new HarmonyMethod(typeof(ExposureOnce), nameof(AfterRun)));
            if (!patched)
                throw new MissingMethodException("Auto exposure: the histogram clear has changed shape; each pass adapts the exposure");
            Log.Info("Auto exposure: hooked; it adapts once a frame to both eyes unless RenderDebug ExposureEachPass=1");
        }

        /// <summary>Render thread, before the first eye's pass.</summary>
        public static void BeginFrame()
        {
            histogramKept = false;
            EndEye();
        }

        /// <summary>Render thread, after each eye's pass: the second eye's tone mapping is done, and the frame's new exposure goes in.</summary>
        public static void EndEye()
        {
            if (!held)
                return;
            held = false;
            SwapPair();
        }

        /// <summary>MyEyeAdaptation.Run's one ClearUav, of the histogram, goes through <see cref="ClearHistogram"/>.</summary>
        private static IEnumerable<CodeInstruction> KeepHistogram(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(typeof(ExposureOnce), nameof(ClearHistogram));
            var code = new List<CodeInstruction>(instructions);
            int found = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.Calls(clearUav))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    found++;
                }
            }
            patched = found == 1;
            return code;
        }

        /// <summary>MyRenderContext.ClearUav(histogram, values), except in the second eye, which adds to the first eye's histogram.</summary>
        private static void ClearHistogram(object renderContext, object histogram, RawInt4 values)
        {
            if (adding)
                return;
            clearArgs[0] = histogram;
            clearArgs[1] = values;
            clearUav.Invoke(renderContext, clearArgs);
        }

        /// <summary>MyEyeAdaptation.Run, render thread.</summary>
        private static void BeforeRun()
        {
            int pass = ScenePass.Current;
            role = Role.Renderer;
            if (EachPass)
                return;
            if (pass == ScenePass.FirstEye)
            {
                role = Role.FirstEye;
            }
            else if (pass == ScenePass.SecondEye && histogramKept)
            {
                role = Role.SecondEye;
                adding = true;
            }
        }

        private static Exception AfterRun(Exception __exception)
        {
            adding = false;
            if (__exception == null && role == Role.FirstEye)
            {
                // Undo the step: the first eye is tone mapped with the exposure the frame began with.
                SwapPair();
                histogramKept = true;
            }
            else if (__exception == null && role == Role.SecondEye)
            {
                // The frame's step is taken; the second eye is tone mapped with the exposure the frame began with too.
                SwapPair();
                held = true;
                if (!logged)
                {
                    logged = true;
                    Log.Info("Auto exposure: adapted once to both eyes' histogram (first time)");
                }
            }
            role = Role.Renderer;
            return __exception;
        }

        /// <summary>Swaps the current and the last exposure (m_autoExposure[0] and [1]), as Run does after its step.</summary>
        private static void SwapPair()
        {
            var pair = (Array)autoExposure.GetValue(null);
            object current = pair.GetValue(0);
            pair.SetValue(pair.GetValue(1), 0);
            pair.SetValue(current, 1);
        }
    }
}
