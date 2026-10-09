using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// One set of sun shadow cascades for both eyes. The renderer fits its cascades to the camera's frustum in every
    /// DrawGameScene (MyShadowCascades.PrepareQueries), so each eye got cascades of its own, fitted to its own frustum, and
    /// drew them again. The shadow texels land on the same world grid either way (the fit snaps them to the world origin,
    /// and a cascade's radius depends only on the field of view), but a pixel uses the cascade one finer than its distance
    /// calls for when that cascade's box holds it (Csm.hlsli, CalculateCascadeIndexFromSplit), and the eyes' boxes had their
    /// edges an eye separation apart: along those edges one eye took the sharper cascade and the other the softer one.
    /// The light direction of each cascade also steps on a count of fits, so the eyes could take a new sun direction a frame
    /// apart. Here the first eye's pass fits the cascades to one frustum that holds both eyes, and the second eye's pass keeps
    /// them (no refit, no redraw): both eyes sample the same maps through the same matrices.
    /// </summary>
    /// <remarks>
    /// The shared frustum looks the eyes' way with the widest of their half-angles on each axis, from an apex pulled back
    /// behind the eyes just far enough that both eyes are inside it. A cascade is fitted to the slice between two split
    /// depths measured from that apex, so near each slice's far end an eye sees up to the pull-back further than the slice
    /// reaches; each cascade's sphere is widened by that much along the frustum's edge, so it holds everything both eyes see.
    /// Eyes that are not parallel (canted displays) keep a fit each, as before.
    /// </remarks>
    internal static class SharedShadows
    {
        /// <summary>Each eye fits and draws its own cascades, as the renderer does. On until the shared fit is checked in game; <see cref="RenderDebug"/> SharedShadows=1 shares.</summary>
        public static bool Off { get; set; } = true;

        private static object env;
        private static bool patched, fitValid, prepared, swapped, loggedFit, loggedCanted;
        private static int pass = -1;
        private static Matrix fitInvViewAt0, fitProjection, savedInvViewAt0, savedProjection;
        private static double margin;

        public static void Patch(Harmony harmony)
        {
            Type cascades = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyShadowCascades", throwOnError: true);
            harmony.Patch(AccessTools.Method(cascades, "PrepareQueries"),
                prefix: new HarmonyMethod(typeof(SharedShadows), nameof(BeforePrepare)),
                transpiler: new HarmonyMethod(typeof(SharedShadows), nameof(WidenSpheres)),
                finalizer: new HarmonyMethod(typeof(SharedShadows), nameof(AfterPrepare)));
            if (!patched)
                Log.Warn("Shadow cascades: the cascade fit has changed shape; each eye keeps its own cascades");
        }

        /// <summary>
        /// Before the eyes are drawn: the frustum the cascades are fitted to, from both eyes' placements.
        /// </summary>
        /// <param name="environment">The renderer's MyEnvironmentMatrices, holding the head camera.</param>
        /// <param name="head">The head camera, as <see cref="EnvMatrices.MakeEye"/> starts from it.</param>
        /// <param name="eyeFromHead">Each eye's pose in the head camera's view space, as MakeEye takes it.</param>
        /// <param name="fov">Each eye's field of view, or null for the head camera's.</param>
        public static void Fit(object environment, in EnvMatrices.Snapshot head, Matrix[] eyeFromHead, EnvMatrices.FovTangents?[] fov)
        {
            env = environment;
            prepared = false;
            fitValid = false;

            Matrix eye0 = eyeFromHead[0], eye1 = eyeFromHead[1];
            const float parallel = 0.99996f; // cos 0.5 degrees
            if (Vector3.Dot(Vector3.Normalize(eye0.Forward), Vector3.Normalize(eye1.Forward)) < parallel ||
                Vector3.Dot(Vector3.Normalize(eye0.Up), Vector3.Normalize(eye1.Up)) < parallel)
            {
                if (!loggedCanted)
                    Log.Info("Shadow cascades: the eyes are not parallel; each eye keeps its own cascades");
                loggedCanted = true;
                return;
            }

            // The eyes' own axes, centred between them.
            Matrix fitFromHead = eye0;
            fitFromHead.Translation = (eye0.Translation + eye1.Translation) * 0.5f;
            Matrix headFromFit = Matrix.Invert(fitFromHead);

            float tanX = 0f, tanY = 0f;
            for (int i = 0; i < 2; i++)
            {
                EnvMatrices.FovTangents t = fov[i] ?? EnvMatrices.Tangents(head.Projection);
                tanX = Math.Max(tanX, Math.Max(Math.Abs(t.Left), Math.Abs(t.Right)));
                tanY = Math.Max(tanY, Math.Max(Math.Abs(t.Up), Math.Abs(t.Down)));
            }
            if (!(tanX > 0f && tanY > 0f))
                return;

            // Pull the apex back (+z, behind) until both eyes are inside the frustum.
            float back = 0f, depthSpread = 0f;
            for (int i = 0; i < 2; i++)
            {
                Vector3 p = (eyeFromHead[i] * headFromFit).Translation;
                back = Math.Max(back, Math.Max(Math.Abs(p.X) / tanX, Math.Abs(p.Y) / tanY) + p.Z);
                depthSpread = Math.Max(depthSpread, Math.Abs(p.Z));
            }

            fitInvViewAt0 = Matrix.CreateTranslation(0f, 0f, back) * fitFromHead * head.InvViewAt0;
            fitProjection = EnvMatrices.WithFov(head.Projection, new EnvMatrices.FovTangents { Left = -tanX, Right = tanX, Up = tanY, Down = -tanY });
            // An eye sees at most (back + its depth offset) past a slice's far end, along the frustum's edge.
            margin = (back + depthSpread) * Math.Sqrt(1.0 + tanX * tanX + tanY * tanY);
            fitValid = true;

            if (!loggedFit)
                Log.Info($"Shadow cascades: one fit for both eyes, field of view tangents {tanX:F3} x {tanY:F3}, apex {back * 100f:F1} cm " +
                         $"behind the eyes, spheres widened {margin * 100.0:F1} cm");
            loggedFit = true;
        }

        /// <summary>The eye about to be drawn: 0 for the first this frame, 1 for the second.</summary>
        public static void BeginEye(int n) => pass = n;

        /// <summary>After an eye, and for every other DrawGameScene (the chase camera's), the renderer's own fit.</summary>
        public static void EndEye() => pass = -1;

        private static bool BeforePrepare()
        {
            swapped = false;
            if (pass < 0 || Off || !fitValid || !patched)
                return true;
            if (pass > 0)
                return !prepared; // the first eye's cascades serve this eye too

            savedInvViewAt0 = EnvMatrices.InvViewAt0(env);
            savedProjection = EnvMatrices.Projection(env);
            EnvMatrices.InvViewAt0(env) = fitInvViewAt0;
            EnvMatrices.Projection(env) = fitProjection;
            swapped = true;
            return true;
        }

        private static void AfterPrepare()
        {
            if (!swapped)
                return;
            EnvMatrices.InvViewAt0(env) = savedInvViewAt0;
            EnvMatrices.Projection(env) = savedProjection;
            swapped = false;
            prepared = true;
        }

        /// <summary>PrepareQueries' BoundingSphereD.CreateFromPoints, the sphere each cascade is fitted round, goes through <see cref="CascadeSphere"/>.</summary>
        private static IEnumerable<CodeInstruction> WidenSpheres(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo fromPoints = AccessTools.Method(typeof(BoundingSphereD), nameof(BoundingSphereD.CreateFromPoints), new[] { typeof(Vector3D[]) });
            MethodInfo widened = AccessTools.Method(typeof(SharedShadows), nameof(CascadeSphere));
            var code = new List<CodeInstruction>(instructions);
            int found = 0;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.Calls(fromPoints))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = widened;
                    found++;
                }
            }
            patched = found == 1;
            return code;
        }

        private static BoundingSphereD CascadeSphere(Vector3D[] points)
        {
            BoundingSphereD sphere = BoundingSphereD.CreateFromPoints(points);
            if (swapped)
                sphere.Radius += margin;
            return sphere;
        }
    }
}
