using System;
using System.Collections.Generic;
using HarmonyLib;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Light flares (MyFlareRenderer: the sun's and every lamp's, thruster's and beacon's glare) are placed from one view
    /// midway between the eyes, so both eyes draw the same ghosts, streaks and brightness.
    /// The renderer puts a flare's ghosts on the line from the light's spot on screen through the screen's centre
    /// (SubGlare ScreenCenterDistance, in the view-projection's clip space), and scales the brightness and the streak's
    /// width by how far from the centre the light sits. Each eye's view is off-centre the other way, so each eye
    /// reflected the ghosts through a different point: with the sun ahead, the left eye's ghost sat left of centre and the
    /// right eye's right of it, a doubled ghost at no depth the eyes can fuse.
    /// </summary>
    /// <remarks>
    /// The middle view's orientation is halfway between the eyes', its position is halfway between them, and its field of
    /// view is symmetric and covers both eyes'. Only the flare placement reads it (ViewProjectionAt0, InvViewProjectionAt0
    /// and InvViewAt0, swapped round MyFlareRenderer.Draw); each eye still draws the billboards from its own camera.
    /// RenderDebug FlaresPerEye=1 places them per eye again, as the renderer does.
    /// </remarks>
    internal static class CentredFlares
    {
        /// <summary>Each eye places its own flares, as the renderer does (RenderDebug FlaresPerEye=1).</summary>
        public static volatile bool PerEye;

        private static Matrix viewProjection, invViewProjection, invView;
        private static bool ready, swapped, logged;
        private static Matrix savedViewProjection, savedInvViewProjection, savedInvView;
        private static object environmentMatrices;

        public static void Patch(Harmony harmony)
        {
            Type light = typeof(MyDX11Render).Assembly.GetType("VRage.Render11.Scene.Components.MyLightComponent", throwOnError: true);
            Type flares = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyFlareRenderer", throwOnError: true);
            var draw = AccessTools.Method(flares, "Draw", new[] { typeof(IEnumerable<>).MakeGenericType(light), typeof(float) })
                ?? throw new MissingMethodException(flares.Name, "Draw");
            harmony.Patch(draw, prefix: new HarmonyMethod(typeof(CentredFlares), nameof(BeforeDraw)),
                finalizer: new HarmonyMethod(typeof(CentredFlares), nameof(AfterDraw)));
            Log.Info("Flares: hooked; placed from the middle of the eyes unless RenderDebug FlaresPerEye=1");
        }

        /// <summary>
        /// Render thread, once the frame's eye placements are known: works out the middle view from the head camera and
        /// both eyes (<see cref="EnvMatrices.MakeEye"/>'s arguments).
        /// </summary>
        public static void BeginFrame(object env, in EnvMatrices.Snapshot head, Matrix[] eyeFromHead, EnvMatrices.FovTangents?[] eyeFov)
        {
            environmentMatrices = env;
            Quaternion q0 = Quaternion.CreateFromRotationMatrix(eyeFromHead[0]), q1 = Quaternion.CreateFromRotationMatrix(eyeFromHead[1]);
            Matrix middle = Matrix.CreateFromQuaternion(Quaternion.Slerp(q0, q1, 0.5f));
            middle.Translation = (eyeFromHead[0].Translation + eyeFromHead[1].Translation) * 0.5f;

            Matrix projection = head.Projection;
            if (eyeFov[0].HasValue && eyeFov[1].HasValue)
            {
                EnvMatrices.FovTangents a = eyeFov[0].Value, b = eyeFov[1].Value;
                float across = Math.Max(Math.Max(-a.Left, -b.Left), Math.Max(a.Right, b.Right));
                float upDown = Math.Max(Math.Max(a.Up, b.Up), Math.Max(-a.Down, -b.Down));
                projection = EnvMatrices.WithFov(projection, new EnvMatrices.FovTangents { Left = -across, Right = across, Up = upDown, Down = -upDown });
            }

            Matrix viewAt0 = head.ViewAt0 * Matrix.Invert(middle);
            invView = Matrix.Invert(viewAt0);
            viewProjection = viewAt0 * projection;
            invViewProjection = Matrix.Invert(viewProjection);
            ready = true;
        }

        public static void EndFrame() => ready = false;

        private static void BeforeDraw()
        {
            int pass = ScenePass.Current;
            if (PerEye || !ready || (pass != ScenePass.FirstEye && pass != ScenePass.SecondEye))
                return;
            object env = environmentMatrices;
            savedViewProjection = EnvMatrices.ViewProjectionAt0(env);
            savedInvViewProjection = EnvMatrices.InvViewProjectionAt0(env);
            savedInvView = EnvMatrices.InvViewAt0(env);
            EnvMatrices.ViewProjectionAt0(env) = viewProjection;
            EnvMatrices.InvViewProjectionAt0(env) = invViewProjection;
            EnvMatrices.InvViewAt0(env) = invView;
            swapped = true;
            if (!logged)
            {
                logged = true;
                Log.Info("Flares: placed from the middle of the eyes (first time)");
            }
        }

        private static Exception AfterDraw(Exception __exception)
        {
            if (swapped)
            {
                object env = environmentMatrices;
                EnvMatrices.ViewProjectionAt0(env) = savedViewProjection;
                EnvMatrices.InvViewProjectionAt0(env) = savedInvViewProjection;
                EnvMatrices.InvViewAt0(env) = savedInvView;
                swapped = false;
            }
            return __exception;
        }
    }
}
