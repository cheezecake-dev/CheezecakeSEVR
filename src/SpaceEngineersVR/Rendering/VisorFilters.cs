// VisorFilters: the visor's glare, glass and sun visor on each eye's picture, once, on the world and not on the inside of the helmet.
using System;
using HarmonyLib;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The visor's filters on each eye's picture, once per eye: the sun's glare scattered in the glass (<see cref="VisorGlare"/>),
    /// then the glass's tint and the sun visor's (<see cref="VisorGlass"/>). They belong on the world seen through the visor, not
    /// on what is on the eye's side of it.
    /// <para>
    /// With the helmet's inside in the scene (<see cref="HelmetInterior"/>), drawn among the renderer's after-processing billboards,
    /// the last of the eye's picture (MyBillboardRenderer.RenderPostPP), the filters go on just before those billboards, in the
    /// eye's pass (a prefix there), so the helmet's inside is not tinted. Otherwise (the helmet's inside off or not ready, or
    /// that pass drawing no billboards, the game's DrawBillboardsPostPP off), they go on after the eye's scene. The only
    /// after-processing billboard of the game's own is a disconnected player's icon, which then is not tinted either, as a
    /// marker on the HUD is not.
    /// </para>
    /// </summary>
    internal static class VisorFilters
    {
        // Render thread: the eye being drawn, and whether its filters are still to go on.
        private static Vector2I eye;
        private static Matrix viewProjection;
        private static bool pending;

        public static void Patch(Harmony harmony)
        {
            Type billboards = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyBillboardRenderer", throwOnError: true);
            harmony.Patch(AccessTools.Method(billboards, "RenderPostPP") ?? throw new MissingMethodException(billboards.Name, "RenderPostPP"),
                prefix: new HarmonyMethod(typeof(VisorFilters), nameof(BeforeAfterProcessing)));
        }

        /// <summary>
        /// Render thread, as an eye's pass begins, its camera set: the eye's picture is <paramref name="size"/> pixels and seen
        /// through <paramref name="eyeViewProjection"/> (the eye's ViewProjectionAt0).
        /// </summary>
        public static void BeginEye(Vector2I size, in Matrix eyeViewProjection)
        {
            eye = size;
            viewProjection = eyeViewProjection;
            pending = true;
        }

        /// <summary>Render thread, after an eye's scene: the filters on <paramref name="eyeTarget"/>, if they did not go on in the scene.</summary>
        public static void EndEye(object eyeTarget)
        {
            if (pending)
                Apply(eyeTarget);
        }

        /// <summary>Prefix of MyBillboardRenderer.RenderPostPP(rc, depthRead, target): in an eye's pass with the helmet's inside in it, the filters first.</summary>
        private static void BeforeAfterProcessing(object target)
        {
            int pass = ScenePass.Current;
            if (pending && (pass == ScenePass.FirstEye || pass == ScenePass.SecondEye) && HelmetInterior.InScene)
                Apply(target);
        }

        private static void Apply(object target)
        {
            pending = false;
            VisorGlare.DrawEye(target, eye, viewProjection);
            VisorGlass.DrawEye(target, eye);
        }
    }
}
