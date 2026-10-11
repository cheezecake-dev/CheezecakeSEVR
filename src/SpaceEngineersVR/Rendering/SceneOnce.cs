using System;
using HarmonyLib;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>Which scene pass (MyRender11.DrawGameScene) the render thread is in: the eye drawn first, the eye drawn second, or the external view's.</summary>
    internal static class ScenePass
    {
        public const int None = -1, FirstEye = 0, SecondEye = 1, Chase = 2;

        /// <summary>Set by the render thread around each pass; read by the renderer's worker jobs while the pass runs.</summary>
        public static volatile int Current = None;
    }

    /// <summary>
    /// Work the renderer does in every DrawGameScene that does not depend on where the eye is, so the eye drawn second (and
    /// the external view's pass) can take the first eye's instead of doing it again:
    /// <list type="bullet">
    /// <item>Spot light shadow maps (MyShadows.PrepareSpotlights): up to four, each drawn from its light, placed relative to
    /// CameraPosition (the head, the same for both eyes). Skipped in the second eye, the first eye's maps and their
    /// assignment to lights are still in place (the depth clear only clears the passes queued this time), and the lighting
    /// finds them by light (MyShadows.GetShadowMap). Both eyes then shadow the same lights, from the same maps.</item>
    /// <item>The environment probe (MyEnvironmentProbe.UpdateCullQuery and FinalizeEnvProbes): a step machine that draws one
    /// cube face, or post-processes, mips or prefilters one, and blends the result in, per call. Called in every pass it
    /// stepped two or three times a frame, adding a forward pass of the scene into a cube face each time, and blended the
    /// probe between the eyes, so they lit with slightly different probes. Now it steps once a frame, in the first eye, as on a
    /// flat screen.</item>
    /// </list>
    /// Both off until checked in game (a 2026-10-10 run froze 2 s after they and SharedShadows were turned on together, with the
    /// signature of the known driver freeze: GPU idle, card nearly full); RenderDebug SharedSpotShadows=1 and ProbeOnce=1 turn them on.
    /// </summary>
    internal static class SceneOnce
    {
        /// <summary>Every pass draws its own spot light shadow maps, as the renderer does; RenderDebug SharedSpotShadows=1 shares the first eye's.</summary>
        public static bool SpotShadowsEachEye = true;

        /// <summary>The environment probe steps in every pass, as the renderer does; RenderDebug ProbeOnce=1 steps it once a frame.</summary>
        public static bool ProbeEachPass = true;

        private static bool spotsPrepared;
        private static bool loggedSpots, loggedProbe;

        public static void Patch(Harmony harmony)
        {
            try
            {
                Type shadows = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyShadows", throwOnError: true);
                harmony.Patch(AccessTools.Method(shadows, "PrepareSpotlights") ?? throw new MissingMethodException("MyShadows", "PrepareSpotlights"),
                    prefix: new HarmonyMethod(typeof(SceneOnce), nameof(BeforeSpotlights)));
                Log.Info("Spot light shadows: hooked; each pass draws its own unless RenderDebug SharedSpotShadows=1");
            }
            catch (Exception e)
            {
                Log.Error(e, "Spot light shadows: could not hook MyShadows.PrepareSpotlights; every pass draws its own");
            }
            try
            {
                Type probe = typeof(MyDX11Render).Assembly.GetType("VRage.Render11.LightingStage.EnvironmentProbe.MyEnvironmentProbe", throwOnError: true);
                harmony.Patch(AccessTools.Method(probe, "UpdateCullQuery") ?? throw new MissingMethodException("MyEnvironmentProbe", "UpdateCullQuery"),
                    prefix: new HarmonyMethod(typeof(SceneOnce), nameof(ProbeStep)));
                harmony.Patch(AccessTools.Method(probe, "FinalizeEnvProbes") ?? throw new MissingMethodException("MyEnvironmentProbe", "FinalizeEnvProbes"),
                    prefix: new HarmonyMethod(typeof(SceneOnce), nameof(ProbeStep)));
                Log.Info("Environment probe: hooked; it steps in every pass unless RenderDebug ProbeOnce=1");
            }
            catch (Exception e)
            {
                Log.Error(e, "Environment probe: could not hook MyEnvironmentProbe; it steps in every pass");
            }
        }

        /// <summary>Render thread, before the first eye's pass.</summary>
        public static void BeginFrame() => spotsPrepared = false;

        /// <summary>Render thread, in MyShadows.PrepareQueries: false skips the spot light shadow maps for this pass.</summary>
        private static bool BeforeSpotlights()
        {
            int pass = ScenePass.Current;
            if (pass == ScenePass.FirstEye || pass == ScenePass.None)
            {
                spotsPrepared = pass == ScenePass.FirstEye;
                return true;
            }
            if (pass != ScenePass.SecondEye || SpotShadowsEachEye || !spotsPrepared)
                return true;
            if (!loggedSpots)
            {
                loggedSpots = true;
                Log.Info("Spot light shadows: the second eye took the first eye's maps (first time)");
            }
            return false;
        }

        /// <summary>
        /// MyEnvironmentProbe.UpdateCullQuery (render thread) and FinalizeEnvProbes (a worker job of the same pass): false
        /// skips the probe's step in the second eye's pass and the external view's.
        /// </summary>
        private static bool ProbeStep()
        {
            int pass = ScenePass.Current;
            if (ProbeEachPass || (pass != ScenePass.SecondEye && pass != ScenePass.Chase))
                return true;
            if (!loggedProbe)
            {
                loggedProbe = true;
                Log.Info("Environment probe: skipped its step outside the first eye's pass (first time)");
            }
            return false;
        }
    }
}
