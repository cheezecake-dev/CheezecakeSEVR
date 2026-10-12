using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>What the GPU is doing between two of <see cref="PerfMeter"/>'s timestamps.</summary>
    internal enum GpuPass : byte
    {
        /// <summary>Outside the scene passes: the full view and vignette blended into the eyes, the copies to the headset and the mirror window.</summary>
        Compose,

        /// <summary>The GUI panel, the debug drawing and anything else after the eyes.</summary>
        Gui,

        /// <summary>A pass's start: frame constants, offscreen textures, the GBuffer and shadow map clears.</summary>
        Setup,

        /// <summary>Depth passes: the sun's shadow cascades and the spot lights' shadow maps.</summary>
        Shadows,

        /// <summary>The GBuffer: every opaque mesh, voxels, foliage, decals, and the depth copies after them.</summary>
        GBuffer,

        /// <summary>The environment probe's forward pass into a cube face.</summary>
        Probe,

        Occlusion,

        /// <summary>The GBuffer resolve: the probe's post-processing, the sun shadow mask, SSAO/HBAO and the lights.</summary>
        Lighting,

        /// <summary>Transparent geometry, billboards, particles, atmosphere and clouds.</summary>
        Transparent,

        /// <summary>The luminance reduction for auto exposure.</summary>
        Exposure,

        Bloom,

        /// <summary>Tone mapping, highlight, the LDR billboards, FXAA, chromatic aberration, the post-PP billboards and the copy out.</summary>
        Post,

        Count,
    }

    /// <summary>
    /// Puts <see cref="PerfMeter"/>'s timestamps at the boundaries of the renderer's work inside a scene pass. The renderer
    /// records most of a pass on worker threads into deferred contexts, then plays them on the immediate context in a fixed order
    /// (MyRenderScheduler.Done): the old and the new geometry renderers' passes (each a GBuffer, depth or forward pass), the
    /// occlusion queries, the GBuffer resolve and lighting, the transparent pass; then DrawGameScene post-processes on the
    /// immediate context. A timestamp before each of these says, on the GPU's clock, when it got there.
    /// The hooks stay in, but issue nothing unless RenderDebug GpuPasses=1 (<see cref="PerfMeter.PassMarks"/>, off by default).
    /// </summary>
    internal static class GpuPasses
    {
        private static readonly Assembly Render11 = typeof(MyDX11Render).Assembly;

        /// <summary>The pass class a command list was recorded by, to the work it is; -1 marks nothing.</summary>
        private static readonly Dictionary<Type, int> byPassType = new Dictionary<Type, int>();

        public static void Patch(Harmony harmony)
        {
            int hooked = 0, missing = 0;
            void Hook(string typeName, string method, string prefix, string postfix = null)
            {
                Type type = Render11.GetType(typeName);
                bool any = false;
                if (type != null)
                {
                    foreach (MethodInfo m in AccessTools.GetDeclaredMethods(type))
                    {
                        if (m.Name != method || m.IsAbstract || m.IsGenericMethodDefinition)
                            continue;
                        harmony.Patch(m,
                            prefix: prefix == null ? null : new HarmonyMethod(typeof(GpuPasses), prefix),
                            postfix: postfix == null ? null : new HarmonyMethod(typeof(GpuPasses), postfix));
                        any = true;
                    }
                }
                if (any)
                    hooked++;
                else
                {
                    missing++;
                    Log.Warn($"GPU passes: {typeName}.{method} not found; its time goes to the work before it");
                }
            }

            try
            {
                Hook("VRageRender.MyRender11", "DrawGameScene", nameof(SceneStart), nameof(SceneEnd));
                Hook("VRageRender.MyGeometryRendererOld", "DoneFrame", nameof(Geometry));
                Hook("VRageRender.MyRenderingPass", "ExecuteCommandList", nameof(OldPass));
                Hook("VRage.Render11.GeometryStage2.Rendering.MyGeometryRenderer", "DoneFrame", nameof(Geometry));
                Hook("VRage.Render11.GeometryStage2.RenderPass.MyRenderPass", "PostprocessWork", nameof(NewPass));
                Hook("VRageRender.MyHighlight", "CopyDepthStencil", nameof(Geometry));
                Hook("VRage.Render11.Culling.Occlusion.MyOcclusionTask", "Consume", nameof(Occlusion));
                Hook("VRage.Render11.GBufferResolve.MyGBufferResolver", "ConsumeWork", nameof(Lighting));
                Hook("VRageRender.MyTransparentRendering", "ConsumeWork", nameof(Transparent));
                Hook("VRageRender.MyEyeAdaptation", "Run", nameof(Exposure));
                Hook("VRageRender.MyEyeAdaptation", "ConstantExposure", nameof(Exposure));
                Hook("VRageRender.MyModernBloom", "Run", nameof(Bloom));
                Hook("VRageRender.MyToneMapping", "Run", nameof(Post));
                Log.Info($"GPU passes: {hooked} timestamp points hooked" + (missing > 0 ? $", {missing} missing" : ""));
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "GPU passes: hooking failed; the Perf lines give the frame's GPU time without the breakdown");
                throw;
            }
        }

        private static void SceneStart() => PerfMeter.Mark(GpuPass.Setup);

        private static void SceneEnd() => PerfMeter.Mark(GpuPass.Compose, ScenePass.None);

        private static void Geometry() => PerfMeter.Mark(GpuPass.GBuffer);

        private static void Occlusion() => PerfMeter.Mark(GpuPass.Occlusion);

        private static void Lighting() => PerfMeter.Mark(GpuPass.Lighting);

        private static void Transparent() => PerfMeter.Mark(GpuPass.Transparent);

        private static void Exposure() => PerfMeter.Mark(GpuPass.Exposure);

        private static void Bloom() => PerfMeter.Mark(GpuPass.Bloom);

        private static void Post() => PerfMeter.Mark(GpuPass.Post);

        private static void OldPass(object __instance) => MarkPass(__instance);

        private static void NewPass(object __instance) => MarkPass(__instance);

        private static void MarkPass(object pass)
        {
            if (!PerfMeter.Marking || pass == null)
                return;
            Type type = pass.GetType();
            if (!byPassType.TryGetValue(type, out int work))
            {
                work = Classify(type.Name);
                byPassType[type] = work;
            }
            if (work >= 0)
                PerfMeter.Mark((GpuPass)work);
        }

        private static int Classify(string name)
        {
            switch (name)
            {
                case "MyDepthPass":
                case "MyDepthRenderPass":
                    return (int)GpuPass.Shadows;
                case "MyForwardPass":
                case "MyForwardRenderPass":
                    return (int)GpuPass.Probe;
                case "MyGBufferPass":
                case "MyGBufferRenderPass":
                case "MyFoliageRenderingPass":
                case "MyFoliageGeneratingPass":
                    return (int)GpuPass.GBuffer;
                case "MyTransparentModelPass":
                case "MyTransparentRenderPass":
                case "MyTransparentForDecalsRenderPass":
                    return (int)GpuPass.Transparent;
                case "MyHighlightPass":
                    return (int)GpuPass.Post;
                default:
                    Log.Info($"GPU passes: pass class {name} not classified; its time goes to the work before it");
                    return -1;
            }
        }
    }
}
