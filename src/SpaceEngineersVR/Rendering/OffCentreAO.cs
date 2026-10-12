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
    /// The ambient occlusion (MyHBAO) rebuilds each pixel's view-space position for the eye's own, off-centre field of view.
    /// It turns a pixel's place on screen into a view direction as uv * UVToViewA + UVToViewB, and MyHBAO.InitConstantBuffer
    /// works both out from the projection's scale alone (M11, M22), as if the view were centred: B is minus and plus the
    /// half-width and half-height tangents. A headset's eye looks off-centre (more to the outside than the inside, and on
    /// canted displays turned outwards as well), and the projection keeps that in M31 and M32 (<see cref="EnvMatrices.WithFov"/>),
    /// so every rebuilt position was sheared sideways by the eye's offset, the other way in each eye. The occlusion, worked
    /// out from the angles between those positions, differed between the eyes in corners, seams and under overhangs.
    /// B is now the field of view's left and up tangents, which is what it was for a centred view.
    /// </summary>
    /// <remarks>RenderDebug AOCentred=1 leaves the renderer's centred values.</remarks>
    internal static class OffCentreAO
    {
        /// <summary>The occlusion takes the view as centred, as the renderer does (RenderDebug AOCentred=1).</summary>
        public static volatile bool Centred;

        private static object environmentMatrices;
        private static FieldInfo uvToViewB;
        private static bool patched;

        public static void Patch(Harmony harmony)
        {
            Type render = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyRender11", throwOnError: true);
            Type hbao = typeof(MyDX11Render).Assembly.GetType("VRageRender.MyHBAO", throwOnError: true);
            MethodInfo init = AccessTools.Method(hbao, "InitConstantBuffer") ?? throw new MissingMethodException(hbao.Name, "InitConstantBuffer");
            uvToViewB = AccessTools.Field(init.ReturnType, "UVToViewB") ?? throw new MissingFieldException(init.ReturnType.Name, "UVToViewB");
            object environment = AccessTools.Field(render, "Environment").GetValue(null);
            environmentMatrices = AccessTools.Field(environment.GetType(), "Matrices").GetValue(environment);
            harmony.Patch(init, transpiler: new HarmonyMethod(typeof(OffCentreAO), nameof(OffCentre)));
            if (!patched)
                throw new MissingMethodException("Ambient occlusion: the view reconstruction has changed shape; it stays centred");
            Log.Info("Ambient occlusion: hooked; it rebuilds positions for off-centre eyes unless RenderDebug AOCentred=1");
        }

        /// <summary>
        /// The two stores into UVToViewB (its X, then its Y) go through <see cref="ShiftX"/> and <see cref="ShiftY"/> first.
        /// </summary>
        private static IEnumerable<CodeInstruction> OffCentre(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo x = AccessTools.Field(typeof(Vector2), nameof(Vector2.X)), y = AccessTools.Field(typeof(Vector2), nameof(Vector2.Y));
            MethodInfo shiftX = AccessTools.Method(typeof(OffCentreAO), nameof(ShiftX)), shiftY = AccessTools.Method(typeof(OffCentreAO), nameof(ShiftY));
            bool inB = false;
            int foundX = 0, foundY = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.LoadsField(uvToViewB, byAddress: true))
                {
                    inB = true;
                }
                else if (inB && instruction.StoresField(x))
                {
                    yield return new CodeInstruction(OpCodes.Call, shiftX);
                    foundX++;
                    inB = false;
                }
                else if (inB && instruction.StoresField(y))
                {
                    yield return new CodeInstruction(OpCodes.Call, shiftY);
                    foundY++;
                    inB = false;
                }
                yield return instruction;
            }
            patched = foundX == 1 && foundY == 1;
        }

        /// <summary>UVToViewB.X: from minus the half-width tangent to the left tangent, (M31 - 1) / M11.</summary>
        private static float ShiftX(float centred)
        {
            if (Centred || !StereoPatches.VRPicture)
                return centred;
            Matrix projection = EnvMatrices.Projection(environmentMatrices);
            return centred + projection.M31 / Math.Abs(projection.M11);
        }

        /// <summary>UVToViewB.Y: from the half-height tangent to the up tangent, (M32 + 1) / M22.</summary>
        private static float ShiftY(float centred)
        {
            if (Centred || !StereoPatches.VRPicture)
                return centred;
            Matrix projection = EnvMatrices.Projection(environmentMatrices);
            return centred + projection.M32 / Math.Abs(projection.M22);
        }
    }
}
