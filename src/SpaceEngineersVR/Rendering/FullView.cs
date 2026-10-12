using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SharpDX.DXGI;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// 2D that belongs over the whole view rather than on the panel (<see cref="HudPanel"/>): the red flash and
    /// low-health tint when the character is hurt (MyGuiControlBloodOverlay, Textures\Gui\Blood.dds stretched over
    /// the window). On the panel it covered only the panel. Its sprite goes to a sprite target of its own instead of
    /// the GUI's; that is drawn at the GUI's size before the eyes and laid over the whole of each eye. How strong it is
    /// over time, a slow pulse in place of the game's flashes, is <see cref="BloodPulse"/>.
    /// </summary>
    /// <remarks>
    /// A sprite with a target texture is a StateChangeOnce message (MySpriteDrawRenderMessage.MessageClass): the renderer
    /// takes it once per game frame, when that frame is first processed (MyRender11.ProcessRenderFrame skips it on a frame
    /// already processed). The render thread draws a game frame again whenever no new one has come (90 Hz eyes, 60 Hz
    /// game; and every time the game thread stalls), and on those frames there was no sprite: the tint was there or not
    /// from one eye frame to the next. So the last sprite is kept and drawn again until a new game frame comes without one.
    /// <para>
    /// Blood.dds is faint (0.48 alpha at its very edge) and its fall to clear is spread over the whole view, so while
    /// <see cref="BloodPulse"/> has given the alpha the sprite is drawn as <see cref="BloodPulse.Layers"/> copies, each as
    /// eight cells round a clear middle (<see cref="DrawCells"/>) that show its whole fall from edge to middle over the
    /// outer <see cref="BloodPulse.Reach"/> of the view.
    /// </para>
    /// </remarks>
    internal static partial class FullView
    {
        private const string Target = "VR full view";

        private static readonly Assembly Render11 = typeof(MyDX11Render).Assembly;
        private static readonly Type Render = Render11.GetType("VRageRender.MyRender11", throwOnError: true);
        private static readonly Type Managers = Render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);

        private static FieldInfo spritesManager, drawQueue, messageList, frameStatus;
        private static MethodInfo acquire, dispose, drawOffscreen;

        /// <summary>Debug: the tint stays on the panel (<see cref="RenderDebug"/> FullView=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: once a second, what the game sent and what the eyes got (<see cref="RenderDebug"/> BloodTrace=1).</summary>
        public static bool Trace { get; set; }

        private enum Kind { None, New, Again }

        // Render thread: the sprites drawn last (MySpriteMessageData), kept for the frames that bring none.
        private static object held;
        private static float heldAlpha, heldReach;
        private static string heldWhat;

        // Render thread: this frame, for the test screenshot note.
        private static Kind frameKind;
        private static int frameEyes;
        private static long frameStamp;

        // Game thread to render thread, for the trace.
        private static readonly object sentGate = new object();
        private static int sent;
        private static float sentAlpha;
        private static string sentWhat;

        // Render thread: the trace's second.
        private static long traceStart;
        private static int traceNew, traceAgain, traceNone, traceEyes, traceSentBefore;
        private static string traceMissing;

        // Blood.dds's alpha, 0-1, at every 1/32 of the way from its edge to its middle (the file's rows 496-528, the four edges
        // averaged: the fall is the same distance from any edge, to within 0.05). The last is the middle's.
        private static readonly float[] EdgeAlpha =
            { 0.481f, 0.464f, 0.447f, 0.429f, 0.410f, 0.388f, 0.362f, 0.329f, 0.294f, 0.257f, 0.219f, 0.177f, 0.137f, 0.097f, 0.060f, 0.028f, 0.020f };

        // Blood.dds is this many texels across and down (GUI textures are loaded whole; the renderer takes a source rectangle in texels).
        private const int BloodTexels = 1024;

        public static void Patch(Harmony harmony)
        {
            rcProperty = AccessTools.Property(Render, "RC");
            deviceContext = rcProperty == null ? null : AccessTools.Property(rcProperty.PropertyType, "DeviceContext");
            resource = Render11.GetType("VRage.Render11.Resources.IResource")?.GetProperty("Resource");
            spritesManager = AccessTools.Field(Managers, "SpritesManager");
            acquire = AccessTools.Method(spritesManager.FieldType, "AcquireDrawMessages");
            dispose = AccessTools.Method(spritesManager.FieldType, "DisposeDrawMessages");
            drawQueue = AccessTools.Field(spritesManager.FieldType, "m_drawQueue");
            messageList = AccessTools.Field(acquire.ReturnType, "Messages");
            frameStatus = AccessTools.Field(Render, "FrameProcessStatus");
            drawOffscreen = AccessTools.GetDeclaredMethods(Render).Single(method => method.Name == "DrawSpritesOffscreen"
                && method.GetParameters()[0].ParameterType == acquire.ReturnType && method.GetParameters()[1].ParameterType == typeof(string));

            PatchRegistry.Apply("blood overlay", () =>
            {
                Type overlay = AccessTools.TypeByName("Sandbox.Game.Screens.Helpers.MyGuiControlBloodOverlay");
                harmony.Patch(AccessTools.Method(overlay, "Draw", new[] { typeof(float), typeof(float) }),
                    transpiler: new HarmonyMethod(typeof(FullView), nameof(Transpile)));
            }, "Full view: the blood overlay could not be hooked; it draws as the game draws it");
        }

        /// <summary>
        /// Render thread, before the eyes: the full-view sprites drawn at the GUI's size, premultiplied, or null when there
        /// are none. This frame's from the game, else the last ones again while no new game frame has come. Release with
        /// <see cref="EyeRenderer.Release"/>; call <see cref="Laid"/> for each eye it is laid over.
        /// </summary>
        internal static object Draw(Vector2I guiSize)
        {
            if (probePending)
                ProbeRead();
            probing = false;
            object manager = spritesManager.GetValue(null);
            object messages = acquire.Invoke(manager, new object[] { Target });
            Kind kind;
            if (messages != null)
            {
                Drop(manager);
                held = messages;
                Describe(messages);
                kind = Kind.New;
            }
            else if (held != null && !NewGameFrame())
                kind = Kind.Again;
            else
            {
                Drop(manager);
                kind = Kind.None;
            }
            frameKind = kind;
            frameEyes = 0;
            frameStamp = Stopwatch.GetTimestamp();
            if (Trace)
                Count(manager, kind, guiSize);
            else
                traceStart = 0;

            if (held == null)
                return null;
            object texture = drawOffscreen.Invoke(null, new object[] { held, Target, guiSize.X, guiSize.Y, Format.B8G8R8A8_UNorm, null, null });
            if (Trace && texture != null && frameStamp - probeAt >= Stopwatch.Frequency)
            {
                probeAt = frameStamp;
                probeAlpha = heldAlpha;
                probeReach = heldReach;
                probeKind = kind;
                Array.Clear(probeWhere, 0, ProbeSlots);
                Array.Clear(probeCopied, 0, ProbeSlots);
                probing = probePending = true;
                ProbeCopy(0, texture);
            }
            return texture;
        }

        /// <summary>Render thread: the texture from <see cref="Draw"/> is about to be laid over eye <paramref name="eye"/> (0 left).</summary>
        internal static void BeforeBlend(object eyeTarget, int eye)
        {
            if (probing)
                ProbeCopy(eye == 0 ? 1 : 3, eyeTarget);
        }

        /// <summary>Render thread: the texture from <see cref="Draw"/> has been laid over eye <paramref name="eye"/> (0 left).</summary>
        internal static void Laid(object eyeTarget, int eye)
        {
            frameEyes++;
            traceEyes++;
            if (probing)
                ProbeCopy(eye == 0 ? 2 : 4, eyeTarget);
        }

        /// <summary>
        /// What the full-view texture should hold (its alpha: how much of the eye is covered) <paramref name="depth"/> of the view's
        /// width or height in from an edge, for a sprite of <paramref name="alpha"/> drawn with <paramref name="reach"/> (0: whole).
        /// At the cells' own edge (a depth of the reach) it is 0 or the middle's alpha, by the pixel.
        /// </summary>
        internal static float Coverage(float depth, float alpha, float reach)
        {
            bool cells = reach > 0f;
            if (cells && depth > reach)
                return 0f;
            float across = Math.Min(0.5f, cells ? depth * 0.5f / reach : depth) * 32f;
            int step = Math.Min(15, (int)across);
            float layer = (EdgeAlpha[step] + (EdgeAlpha[step + 1] - EdgeAlpha[step]) * (across - step)) * alpha;
            return 1f - (float)Math.Pow(1f - layer, cells ? BloodPulse.Layers : 1);
        }

        private static void Drop(object manager)
        {
            if (held == null)
                return;
            object old = held;
            held = null;
            dispose.Invoke(manager, new[] { old });
        }

        // MyRender11.ProcessMessageQueue this frame took a game frame not taken before (the queue's last, or ones before it).
        private static bool NewGameFrame()
        {
            var status = (FrameProcessStatusEnum)frameStatus.GetValue(null);
            return status == FrameProcessStatusEnum.Success || status == FrameProcessStatusEnum.Skipped;
        }

        private static void Describe(object messages)
        {
            heldAlpha = heldReach = 0f;
            heldWhat = "no sprite";
            if (messageList.GetValue(messages) is IList list && list.Count > 0)
            {
                if (list[0] is MyRenderMessageDrawSprite sprite)
                {
                    heldAlpha = sprite.Color.A / 255f;
                    heldWhat = $"{sprite.Texture} {Rect(sprite.DestinationRectangle)}";
                }
                else
                    heldWhat = list[0]?.GetType().Name ?? "null";
                if (list.Count > 1)
                {
                    heldWhat += $" and {list.Count - 1} more";
                    // The cells' first is the top left corner: its width is the reach, of the whole they make.
                    float left = float.MaxValue, right = float.MinValue;
                    foreach (object item in list)
                    {
                        if (item is MyRenderMessageDrawSprite cell)
                        {
                            left = Math.Min(left, cell.DestinationRectangle.X);
                            right = Math.Max(right, cell.DestinationRectangle.Right);
                        }
                    }
                    if (list[0] is MyRenderMessageDrawSprite corner && right > left)
                        heldReach = corner.DestinationRectangle.Width / (right - left);
                }
            }
        }

        private static string Rect(RectangleF r) => $"{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}";

        private static void Count(object manager, Kind kind, Vector2I size)
        {
            long now = Stopwatch.GetTimestamp();
            if (traceStart == 0)
            {
                traceStart = now;
                traceNew = traceAgain = traceNone = traceEyes = 0;
                traceMissing = null;
                lock (sentGate)
                    traceSentBefore = sent;
            }
            if (kind == Kind.New)
                traceNew++;
            else if (kind == Kind.Again)
                traceAgain++;
            else
            {
                traceNone++;
                if (traceMissing == null)
                {
                    int sentNow;
                    lock (sentGate)
                        sentNow = sent;
                    if (sentNow != traceSentBefore)
                    {
                        var queue = drawQueue.GetValue(manager) as IDictionary;
                        string keys = queue == null ? "?" : string.Join(", ", queue.Keys.Cast<object>().Select(key => $"'{key}'"));
                        traceMissing = $"; a frame with none while the game was sending: game frame {frameStatus.GetValue(null)}, sprite targets waiting [{keys}]";
                    }
                }
            }

            double seconds = (now - traceStart) / (double)Stopwatch.Frequency;
            if (seconds < 1.0)
                return;
            int count;
            float alpha;
            string what;
            lock (sentGate)
            {
                count = sent - traceSentBefore;
                alpha = sentAlpha;
                what = sentWhat;
            }
            int frames = traceNew + traceAgain + traceNone;
            string game = count == 0 ? "the game sent no blood sprite" : $"the game sent {count} blood sprite{(count == 1 ? "" : "s")} (last alpha {alpha:0.00}, {what})";
            string drawn = held != null ? $"; drawn at alpha {heldAlpha:0.00}, {heldWhat}, into {size.X}x{size.Y}" : "";
            string readback = probeResult != null ? $"; readback {(now - probeAt) / (double)Stopwatch.Frequency:0.0} s ago: {probeResult}" : "";
            Log.Info($"Blood trace {seconds:0.0} s: {game}; {frames} eye frame{(frames == 1 ? "" : "s")}: {traceNew} with the game's new sprite, " +
                $"{traceAgain} with the last again (no new game frame), {traceNone} with none; laid over {traceEyes} eye image{(traceEyes == 1 ? "" : "s")}{drawn}{readback}{traceMissing}");
            probeResult = null;
            traceStart = 0;
        }

        // MyRenderProxy.DrawSprite, to the full-view target.
        private static void DrawSprite(string texture, ref RectangleF destination, Rectangle? sourceRectangle, Color color, float rotation,
            bool ignoreBounds, bool waitTillLoaded, string targetTexture, string maskTexture, float rotSpeed)
        {
            string target = Off || targetTexture != null ? targetTexture : Target;
            if (Trace)
            {
                lock (sentGate)
                {
                    sent++;
                    sentAlpha = color.A / 255f;
                    sentWhat = $"{Rect(destination)} to {(target == null ? "the panel" : $"'{target}'")}";
                }
            }
            float reach = target == Target && rotation == 0f && sourceRectangle == null ? BloodPulse.Reach : 0f;
            if (reach > 0f && destination.Width >= 64f && destination.Height >= 64f)
            {
                DrawCells(texture, destination, color, Math.Min(reach, 0.45f), ignoreBounds, waitTillLoaded, target, maskTexture, rotSpeed);
                return;
            }
            MyRenderProxy.DrawSprite(texture, ref destination, sourceRectangle, color, rotation, ignoreBounds, waitTillLoaded,
                target, maskTexture, rotSpeed);
        }

        // Blood.dds is dark at its edges and fades to clear at its middle, over the whole view. Drawn as eight cells round a clear
        // middle: each corner cell shows a quarter of the texture and each side cell a thin band across its middle, so the whole
        // fall from edge to middle takes the first <reach> of the view and the rest is left clear. BloodPulse.Layers of them, one
        // over another. The renderer keeps a sprite's place and size as half floats, so the cells' edges are on multiples of 4
        // pixels, where neither leaves a gap or a double line between cells.
        private static void DrawCells(string texture, RectangleF whole, Color color, float reach, bool ignoreBounds, bool waitTillLoaded,
            string target, string maskTexture, float rotSpeed)
        {
            float[] xs = { whole.X, Snap(whole.X + reach * whole.Width), Snap(whole.Right - reach * whole.Width), whole.Right };
            float[] ys = { whole.Y, Snap(whole.Y + reach * whole.Height), Snap(whole.Bottom - reach * whole.Height), whole.Bottom };
            int half = BloodTexels / 2;
            int[] from = { 0, half - 1, half }, size = { half, 2, half };
            for (int layer = 0; layer < BloodPulse.Layers; layer++)
            {
                for (int row = 0; row < 3; row++)
                {
                    for (int column = 0; column < 3; column++)
                    {
                        if (row == 1 && column == 1)
                            continue;
                        var cell = new RectangleF(xs[column], ys[row], xs[column + 1] - xs[column], ys[row + 1] - ys[row]);
                        MyRenderProxy.DrawSprite(texture, ref cell, new Rectangle(from[column], from[row], size[column], size[row]), color, 0f,
                            ignoreBounds, waitTillLoaded, target, maskTexture, rotSpeed);
                    }
                }
            }
        }

        private static float Snap(float pixels) => (float)Math.Round(pixels / 4f) * 4f;

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
            if (replaced == 0)
                throw new MissingMethodException("Full view: the blood overlay draws no sprite any more");
            if (replaced != 1)
                Log.Warn($"Full view: expected one blood overlay sprite, found {replaced}");
        }
    }
}
