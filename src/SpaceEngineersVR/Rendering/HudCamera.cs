using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Game.GUI.HudViewers;
using Sandbox.Graphics;
using SpaceEngineersVR.Tracking;
using VRage.Game.Utils;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The HUD places what marks a place in the world (GPS, ore, antennas, target locks, the selection box) by
    /// projecting it with the main camera: the camera's whole field of view across the window. Shown on the panel
    /// (<see cref="HudPanel"/>), the window spans only <see cref="VRSettings.HudWidthDegrees"/>, seen from the head.
    /// So in the HUD's own code, reads of the camera's view and projection are turned into the panel's: from the
    /// head, through the panel's edges. The camera itself is left alone - the HUD draws on a task of its own
    /// (MyGuiScreenHudSpace.DrawAsync) while the game goes on using the camera.
    /// </summary>
    internal static class HudCamera
    {
        /// <summary>Debug: the HUD projects with the game camera, as before (<see cref="RenderDebug"/> HudCamera=0).</summary>
        public static bool Off { get; set; }

        /// <summary>The HUD's classes; every method in them (and in the classes nested in them) that reads the camera.</summary>
        private static readonly Type[] HudTypes = { typeof(MyHudMarkerRender), typeof(MyHudCrosshair), typeof(MyGuiScreenHudBase), typeof(MyGuiScreenHudSpace) };

        private static readonly Dictionary<FieldInfo, MethodInfo> FieldReads = new Dictionary<FieldInfo, MethodInfo>
        {
            [AccessTools.Field(typeof(MyCamera), nameof(MyCamera.ViewMatrix))] = AccessTools.Method(typeof(HudCamera), nameof(View)),
            [AccessTools.Field(typeof(MyCamera), nameof(MyCamera.WorldMatrix))] = AccessTools.Method(typeof(HudCamera), nameof(World)),
            [AccessTools.Field(typeof(MyCamera), nameof(MyCamera.ProjectionMatrix))] = AccessTools.Method(typeof(HudCamera), nameof(Projection)),
            [AccessTools.Field(typeof(MyCamera), nameof(MyCamera.ViewProjectionMatrix))] = AccessTools.Method(typeof(HudCamera), nameof(ViewProjection)),
        };

        private static readonly Dictionary<MethodInfo, MethodInfo> Calls = new Dictionary<MethodInfo, MethodInfo>
        {
            [AccessTools.Method(typeof(MyCamera), nameof(MyCamera.WorldToScreen))] = AccessTools.Method(typeof(HudCamera), nameof(WorldToScreen)),
            [AccessTools.PropertyGetter(typeof(MyCamera), nameof(MyCamera.ForwardVector))] = AccessTools.Method(typeof(HudCamera), nameof(Forward)),
        };

        public static void Patch(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(HudCamera), nameof(Transpile));
            var patched = new List<string>();
            foreach (MethodBase method in HudTypes.SelectMany(WithNested).SelectMany(MethodsOf).Where(ReadsCamera))
            {
                harmony.Patch(method, transpiler: transpiler);
                patched.Add($"{method.DeclaringType.Name}.{method.Name}");
            }
            Log.Info($"HUD camera: {patched.Count} methods ({string.Join(", ", patched)})");
        }

        public static MatrixD View(MyCamera camera) =>
            Off ? camera.ViewMatrix : camera.ViewMatrix * CameraHeadLink.HeadFromSentCamera;

        public static MatrixD World(MyCamera camera) =>
            Off ? camera.WorldMatrix : MatrixD.Invert(View(camera));

        public static MatrixD Projection(MyCamera camera)
        {
            MatrixD projection = camera.ProjectionMatrix;
            Rectangle screen = MyGuiManager.GetFullscreenRectangle();
            if (Off || screen.Width <= 0 || screen.Height <= 0)
                return projection;
            // Through the panel's edges; depth as it was.
            double tanHalfWidth = Math.Tan(MathHelper.ToRadians(VRSettings.HudWidthDegrees) / 2.0);
            projection.M11 = 1.0 / tanHalfWidth;
            projection.M22 = (double)screen.Width / screen.Height / tanHalfWidth;
            projection.M31 = projection.M32 = 0.0;
            return projection;
        }

        public static MatrixD ViewProjection(MyCamera camera) =>
            Off ? camera.ViewProjectionMatrix : View(camera) * Projection(camera);

        public static Vector3D WorldToScreen(MyCamera camera, ref Vector3D worldPos) =>
            Vector3D.Transform(worldPos, ViewProjection(camera));

        public static Vector3 Forward(MyCamera camera) =>
            Off ? camera.ForwardVector : (Vector3)World(camera).Forward;

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Ldfld || instruction.opcode == OpCodes.Ldflda)
                    && instruction.operand is FieldInfo field && FieldReads.TryGetValue(field, out MethodInfo read))
                {
                    yield return new CodeInstruction(OpCodes.Call, read).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    if (instruction.opcode == OpCodes.Ldflda)
                    {
                        // Read through a reference: the panel's matrix from a local of its own.
                        LocalBuilder local = generator.DeclareLocal(typeof(MatrixD));
                        yield return new CodeInstruction(OpCodes.Stloc, local);
                        yield return new CodeInstruction(OpCodes.Ldloca, local);
                    }
                    continue;
                }
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo called && Calls.TryGetValue(called, out MethodInfo replacement))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }

        private static bool ReadsCamera(MethodBase method)
        {
            try
            {
                return PatchProcessor.ReadMethodBody(method).Any(op =>
                    op.Value is FieldInfo field && FieldReads.ContainsKey(field) || op.Value is MethodInfo called && Calls.ContainsKey(called));
            }
            catch (Exception e)
            {
                Log.Warn($"HUD camera: could not read {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                return false;
            }
        }

        private static IEnumerable<Type> WithNested(Type type) =>
            new[] { type }.Concat(type.GetNestedTypes(AccessTools.all).SelectMany(WithNested));

        private static IEnumerable<MethodBase> MethodsOf(Type type) =>
            type.GetMethods(AccessTools.allDeclared).Cast<MethodBase>()
                .Concat(type.GetConstructors(AccessTools.allDeclared))
                .Where(method => method.GetMethodBody() != null && !method.ContainsGenericParameters);
    }
}
