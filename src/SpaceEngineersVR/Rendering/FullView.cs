using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SharpDX.DXGI;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// 2D that belongs over the whole view rather than on the panel (<see cref="HudPanel"/>): the red flash and
    /// low-health tint when the character is hurt (MyGuiControlBloodOverlay, Textures\Gui\Blood.dds stretched over
    /// the window). On the panel it covered only the panel. Its sprite goes to a sprite target of its own instead of
    /// the GUI's; that is drawn at the GUI's size before the eyes and laid over the whole of each eye.
    /// </summary>
    internal static class FullView
    {
        private const string Target = "VR full view";

        private static readonly Assembly Render11 = typeof(MyDX11Render).Assembly;
        private static readonly Type Render = Render11.GetType("VRageRender.MyRender11", throwOnError: true);
        private static readonly Type Managers = Render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);

        private static FieldInfo spritesManager;
        private static MethodInfo acquire, dispose, drawOffscreen;

        /// <summary>Debug: the tint stays on the panel, as before (<see cref="RenderDebug"/> FullView=0).</summary>
        public static bool Off { get; set; }

        public static void Patch(Harmony harmony)
        {
            spritesManager = AccessTools.Field(Managers, "SpritesManager");
            acquire = AccessTools.Method(spritesManager.FieldType, "AcquireDrawMessages");
            dispose = AccessTools.Method(spritesManager.FieldType, "DisposeDrawMessages");
            drawOffscreen = AccessTools.GetDeclaredMethods(Render).Single(method => method.Name == "DrawSpritesOffscreen"
                && method.GetParameters()[0].ParameterType == acquire.ReturnType && method.GetParameters()[1].ParameterType == typeof(string));

            Type overlay = AccessTools.TypeByName("Sandbox.Game.Screens.Helpers.MyGuiControlBloodOverlay");
            harmony.Patch(AccessTools.Method(overlay, "Draw", new[] { typeof(float), typeof(float) }),
                transpiler: new HarmonyMethod(typeof(FullView), nameof(Transpile)));
        }

        /// <summary>
        /// Render thread, before the eyes: this frame's full-view sprites drawn at the GUI's size, premultiplied, or
        /// null when there are none. Release with <see cref="EyeRenderer.Release"/>.
        /// </summary>
        internal static object Draw(Vector2I guiSize)
        {
            object manager = spritesManager.GetValue(null);
            object messages = acquire.Invoke(manager, new object[] { Target });
            if (messages == null)
                return null;
            try
            {
                return drawOffscreen.Invoke(null, new object[] { messages, Target, guiSize.X, guiSize.Y, Format.B8G8R8A8_UNorm, null, null });
            }
            finally
            {
                dispose.Invoke(manager, new[] { messages });
            }
        }

        // MyRenderProxy.DrawSprite, to the full-view target.
        private static void DrawSprite(string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color, float rotation,
            bool ignoreBounds, bool waitTillLoaded, string targetTexture, string maskTexture, float rotSpeed) =>
            MyRenderProxy.DrawSprite(texture, ref destination, sourceRectangle, color, rotation, ignoreBounds, waitTillLoaded,
                Off || targetTexture != null ? targetTexture : Target, maskTexture, rotSpeed);

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo drawSprite = AccessTools.Method(typeof(MyRenderProxy), nameof(MyRenderProxy.DrawSprite),
                new[] { typeof(string), typeof(RectangleF).MakeByRefType(), typeof(Rectangle?), typeof(Color), typeof(float), typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(float) });
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(drawSprite))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(FullView), nameof(DrawSprite));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1)
                Log.Warn($"Full view: expected one blood overlay sprite, found {replaced}");
        }
    }
}
