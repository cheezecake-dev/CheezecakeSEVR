using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The game's 3D debug shapes (MyRenderProxy.DebugDrawLine3D, DebugDrawSphere, DebugDrawTriangle and the other boxes,
    /// cones and points) in each eye. Building shows two of its aids this way rather than as scene geometry: the symmetry
    /// planes (MyCubeBuilder.DrawSymmetryPlane, two triangles each) and the planet preview of the spawn menu's paste
    /// (MyVoxelClipboard.UpdateVoxelMapTransformations, a sphere). The renderer draws the shapes once, after the scene
    /// (MyRender11.DrawDebugScene), which <see cref="EyeRenderer"/> runs on the head camera into the window, so no eye had them.
    /// </summary>
    /// <remarks>
    /// Each eye, straight after its scene, hands the frame's shapes to the renderer's own conversion
    /// (MyRender11.ProcessDebugMessages) while the eye's camera is set, and draws them as DrawDebugScene does
    /// (MyPrimitivesRenderer.Draw against the scene's depth, then MyLinesRenderer.Draw), under the HUD panel. The window's
    /// pass then leaves the shapes out, since the eyes it shows already have them; it still draws the debug text and 2D
    /// lines, and still keeps, moves and frees every message as before: the eyes only read the lists.
    /// Text placed in the world (DebugDrawText3D, such as the mount point labels) is a screen sprite
    /// and stays in the window.
    /// </remarks>
    internal static class DebugShapes
    {
        private static readonly Assembly Render11 = typeof(MyDX11Render).Assembly;
        private static readonly Type Render = Render11.GetType("VRageRender.MyRender11", throwOnError: true);

        private static MethodInfo process, drawTriangles, drawLines;
        private static List<MyRenderMessageBase> frameMessages, persistentMessages;
        private static readonly List<MyRenderMessageBase> shapes = new List<MyRenderMessageBase>();
        private static readonly List<MyRenderMessageBase> notShapes = new List<MyRenderMessageBase>();
        private static volatile bool hooked, failed;
        private static bool windowLeavesShapes, logged;

        /// <summary>Render thread: how many shapes the last eye drew (0 when there were none, or the eyes do not draw them).</summary>
        public static int Drawn { get; private set; }

        /// <summary>The eyes draw the shapes: hooked, and not stopped by a failure.</summary>
        public static bool Active => hooked && !failed;

        public static void Patch(Harmony harmony)
        {
            process = AccessTools.Method(Render, "ProcessDebugMessages", new[] { typeof(List<MyRenderMessageBase>) })
                ?? throw new MissingMethodException(Render.Name, "ProcessDebugMessages");
            frameMessages = (List<MyRenderMessageBase>)(AccessTools.Field(Render, "m_debugDrawMessages")
                ?? throw new MissingFieldException(Render.Name, "m_debugDrawMessages")).GetValue(null);
            persistentMessages = (List<MyRenderMessageBase>)(AccessTools.Field(Render, "m_persistentDebugMessages")
                ?? throw new MissingFieldException(Render.Name, "m_persistentDebugMessages")).GetValue(null);
            Type primitives = Render11.GetType("VRageRender.MyPrimitivesRenderer", throwOnError: true);
            drawTriangles = AccessTools.Method(primitives, "Draw") ?? throw new MissingMethodException(primitives.Name, "Draw");
            Type lines = Render11.GetType("VRageRender.MyLinesRenderer", throwOnError: true);
            drawLines = AccessTools.Method(lines, "Draw") ?? throw new MissingMethodException(lines.Name, "Draw");
            harmony.Patch(process, prefix: new HarmonyMethod(typeof(DebugShapes), nameof(WindowList)));
            hooked = true;
            Log.Info("Debug shapes: hooked; the game's 3D debug drawing (symmetry planes, planet preview) is drawn in each eye");
        }

        /// <summary>
        /// Render thread, in an eye's pass after its scene, with the eye's camera set: draws the frame's shapes into
        /// <paramref name="target"/> through <paramref name="viewProjection"/> (the eye's ViewProjectionAt0).
        /// </summary>
        public static void DrawEye(object target, Matrix viewProjection)
        {
            Drawn = 0;
            if (!Active)
                return;
            try
            {
                shapes.Clear();
                Collect(frameMessages, shapes, wantShapes: true);
                Collect(persistentMessages, shapes, wantShapes: true);
                if (shapes.Count == 0)
                    return;
                process.Invoke(null, new object[] { shapes });
                drawTriangles.Invoke(null, new[] { target, viewProjection, (object)true });
                drawLines.Invoke(null, new[] { target, (object)false });
                Drawn = shapes.Count;
                if (!logged)
                {
                    logged = true;
                    Log.Info($"Debug shapes: {shapes.Count} drawn in the eyes (first time)");
                }
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "Debug shapes could not be drawn in the eyes; for the rest of the run they show in the window only");
            }
            finally
            {
                shapes.Clear();
            }
        }

        /// <summary>
        /// Render thread, round the window's debug pass (MyRender11.DrawDebugScene): <paramref name="eyesHaveThem"/> when both
        /// eyes were drawn this frame, so the pass leaves the shapes out.
        /// </summary>
        public static void BeginWindow(bool eyesHaveThem) => windowLeavesShapes = eyesHaveThem && Active;

        public static void EndWindow() => windowLeavesShapes = false;

        /// <summary>Prefix of MyRender11.ProcessDebugMessages(List): in the window's pass after the eyes, the list less its shapes.</summary>
        private static void WindowList(ref List<MyRenderMessageBase> messageList)
        {
            if (!windowLeavesShapes || messageList.Count == 0)
                return;
            notShapes.Clear();
            Collect(messageList, notShapes, wantShapes: false);
            messageList = notShapes;
        }

        private static void Collect(List<MyRenderMessageBase> from, List<MyRenderMessageBase> into, bool wantShapes)
        {
            for (int i = 0; i < from.Count; i++)
                if (IsShape(from[i].MessageType) == wantShapes)
                    into.Add(from[i]);
        }

        /// <summary>
        /// The messages ProcessDebugMessages turns into world-space triangles and lines, and that do nothing else. Text, 2D
        /// lines, debug meshes (added to a persistent set), and the clear and present messages stay with the window's pass.
        /// </summary>
        private static bool IsShape(MyRenderMessageEnum type)
        {
            switch (type)
            {
                case MyRenderMessageEnum.DebugDrawLine3D:
                case MyRenderMessageEnum.DebugDrawPoint:
                case MyRenderMessageEnum.DebugDrawSphere:
                case MyRenderMessageEnum.DebugDrawAABB:
                case MyRenderMessageEnum.DebugDraw6FaceConvex:
                case MyRenderMessageEnum.DebugDrawCone:
                case MyRenderMessageEnum.DebugDrawAxis:
                case MyRenderMessageEnum.DebugDrawOBB:
                case MyRenderMessageEnum.DebugDrawFrustrum:
                case MyRenderMessageEnum.DebugDrawCylinder:
                case MyRenderMessageEnum.DebugDrawTriangle:
                case MyRenderMessageEnum.DebugDrawTriangles:
                case MyRenderMessageEnum.DebugDrawCapsule:
                    return true;
                default:
                    return false;
            }
        }
    }
}
