using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using SpaceEngineersVR.Input;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The comfort vignette: the edges of each eye darken while the sticks move or turn the player, which takes the
    /// edge of the view's flow away and eases motion sickness. The strength comes from artificial motion only
    /// (<see cref="VRControls.Motion"/>: the left stick walking or flying, a seat's turn, a snap turn), never from the
    /// head, and is smoothed over about 0.15 s. It is one generated texture, a clear circle fading to black, blended
    /// over each eye after the scene (<see cref="EyeRenderer.Blend"/>); the strength moves the viewport it is
    /// stretched over: the smaller the viewport, the smaller the clear middle.
    /// </summary>
    /// <remarks>
    /// The texture is centred on the eye's optical axis (the projection's off-centre shift), so a headset whose field
    /// of view is not symmetric still gets a vignette that closes in on the middle of the lens. The panel (HUD and
    /// menus) is a separate layer over the eyes and is not darkened. Setting: ComfortVignette (0 = off).
    /// Debug (<see cref="RenderDebug"/>): Vignette=0 turns it off, VignetteTest=1 draws it at full strength without motion.
    /// </remarks>
    internal static class Vignette
    {
        /// <summary>Debug: no vignette (<see cref="RenderDebug"/> Vignette=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: strength 1 whatever the motion and the setting (<see cref="RenderDebug"/> VignetteTest=1).</summary>
        public static bool Test { get; set; }

        /// <summary>The strength the eyes were drawn with this frame, 0 to 1: motion, smoothed, times the setting.</summary>
        public static float Strength { get; private set; }

        // ---- Tuning ----

        /// <summary>A change in strength, rise or fall, covers 90% of its way in this long, seconds.</summary>
        public const float Seconds = 0.15f;

        /// <summary>A snap turn asks for this much for <see cref="SnapHold"/> seconds, then lets go: a short dark blink.</summary>
        public const float SnapLevel = 0.8f, SnapHold = 0.15f;

        /// <summary>Below this the vignette is not drawn at all.</summary>
        public const float Visible = 0.01f;

        /// <summary>
        /// The size of the viewport the texture is stretched over, as a multiple of the eye, at full strength and at none.
        /// Full: the clear middle is 0.45 of the eye's half width and the edge is black from 0.95. None: the clear
        /// middle reaches past the farthest corner, 1.6 half widths.
        /// </summary>
        public const float ScaleFull = 1.25f, ScaleOff = 4.5f;

        /// <summary>The texture, as a share of its width from its middle: clear inside the first, black from the second.</summary>
        public const float InnerUv = 0.18f, OuterUv = 0.38f;

        public const int TextureSize = 256;

        /// <summary>Direct3D 11 viewports are at most 16384 wide.</summary>
        private const float MaxViewport = 16000f;

        // ---- What the strength does: pure, so it can be checked without a game. ----

        /// <summary>Where the smoothing has got to, and what it needs to notice a snap turn.</summary>
        internal struct State
        {
            public float Level;
            public int Snaps;
            public double PulseEnd, Time;
            public bool Started;
        }

        /// <summary>What the motion asks for, 0 to 1: the strongest of the move, the turn and a snap turn's blink.</summary>
        public static float Target(float move, float turn, bool snapping) =>
            Clamp01(Math.Max(Math.Max(move, turn), snapping ? SnapLevel : 0f));

        /// <summary>Moves <paramref name="level"/> toward <paramref name="target"/>, covering 90% of the way in <see cref="Seconds"/>.</summary>
        public static float Smooth(float level, float target, float seconds) =>
            level + (target - level) * (1f - (float)Math.Exp(-Math.Max(seconds, 0f) * Math.Log(10d) / Seconds));

        /// <summary>
        /// One step of the smoothing: the motion as <see cref="VRControls.Motion"/> reports it, at <paramref name="now"/>
        /// seconds. A new snap turn starts a blink of <see cref="SnapHold"/> seconds. Returns the level, 0 to 1.
        /// </summary>
        public static float Advance(ref State state, double now, float move, float turn, int snaps)
        {
            if (!state.Started)
            {
                state.Started = true;
                state.Snaps = snaps; // turns made before the first frame are history, not a blink
                state.Time = now;
            }
            float seconds = (float)Math.Max(0d, Math.Min(now - state.Time, 0.1d)); // a stall must not jump the level
            state.Time = now;
            if (snaps != state.Snaps)
            {
                state.Snaps = snaps;
                state.PulseEnd = now + SnapHold;
            }
            state.Level = Smooth(state.Level, Target(move, turn, now < state.PulseEnd), seconds);
            return state.Level;
        }

        /// <summary>
        /// The size of the viewport, as a multiple of the eye, that gives a strength. A strength of 0.5 is already a
        /// clear vignette, so the strength rises quickly at first and eases toward full.
        /// </summary>
        public static float Scale(float strength)
        {
            float t = Clamp01(strength);
            float eased = 1f - (1f - t) * (1f - t);
            return ScaleOff + (ScaleFull - ScaleOff) * eased;
        }

        /// <summary>
        /// Where the texture goes in the eye image: x, y, width, height of the viewport it is stretched over, in pixels,
        /// centred on the optical axis. <paramref name="m31"/> and <paramref name="m32"/> are the projection's off-centre
        /// shift (<see cref="EnvMatrices.WithFov"/>), which put the axis at (1 - m31) / 2 and (1 + m32) / 2 of the
        /// image. The centre is kept within 0.4 to 0.6 so that the viewport always covers the eye.
        /// </summary>
        public static (float X, float Y, float Width, float Height) Layout(float strength, int eyeWidth, int eyeHeight, float m31, float m32)
        {
            float scale = Math.Min(Scale(strength), MaxViewport / Math.Max(Math.Max(eyeWidth, eyeHeight), 1));
            float width = scale * eyeWidth, height = scale * eyeHeight;
            float u = Clamp(0.5f * (1f - m31), 0.4f, 0.6f), v = Clamp(0.5f * (1f + m32), 0.4f, 0.6f);
            return (u * eyeWidth - width * 0.5f, v * eyeHeight - height * 0.5f, width, height);
        }

        /// <summary>The texture: premultiplied black, so only the alpha varies, clear inside <see cref="InnerUv"/> of the middle and opaque from <see cref="OuterUv"/>.</summary>
        public static byte[] Pixels(int size)
        {
            var data = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size - 0.5f, v = (y + 0.5f) / size - 0.5f;
                    float radius = (float)Math.Sqrt(u * u + v * v);
                    data[(y * size + x) * 4 + 3] = (byte)(Fade((radius - InnerUv) / (OuterUv - InnerUv)) * 255f + 0.5f);
                }
            }
            return data;
        }

        /// <summary>Smootherstep, so the edge of the clear middle and the start of the black have no visible line.</summary>
        private static float Fade(float t)
        {
            t = Clamp01(t);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        private static float Clamp01(float value) => Clamp(value, 0f, 1f);

        private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

        // ---- The renderer's side ----

        private static State state;
        private static bool failed;
        private static object texture, manager;
        private static FieldInfo managerField;
        private static MethodInfo create, discard;
        private static PropertyInfo srv;

        /// <summary>
        /// Render thread, once per eye, after the scene and the full-view overlay: darkens the edges of
        /// <paramref name="eyeTarget"/>. The first eye of a frame (<paramref name="pass"/> 0) also takes the frame's
        /// motion. <paramref name="projection"/> is the eye's, for its optical axis. Never throws: a vignette that cannot
        /// be drawn is logged once and left off.
        /// </summary>
        public static void Draw(object eyeTarget, int pass, Vector2I eye, in Matrix projection)
        {
            if (failed)
                return;
            try
            {
                if (pass == 0)
                {
                    var motion = VRControls.Motion;
                    float level = Advance(ref state, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, motion.Move, motion.Turn, motion.Snaps);
                    Strength = Off ? 0f : Test ? 1f : level * VRSettings.ComfortVignette;
                }
                if (Strength <= Visible || !Ready())
                    return;
                var (x, y, width, height) = Layout(Strength, eye.X, eye.Y, projection.M31, projection.M32);
                EyeRenderer.Blend(eyeTarget, texture, new MyViewport(x, y, width, height));
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "Comfort vignette failed; it stays off for the rest of the run");
            }
        }

        /// <summary>
        /// The texture exists and is alive. It is made the first time it is needed, by the game's own generated texture
        /// manager (the one behind MyRenderProxy.CreateGeneratedTexture), and made again if a device reset disposed it.
        /// </summary>
        private static bool Ready()
        {
            if (managerField == null)
            {
                Type managers = typeof(MyDX11Render).Assembly.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
                managerField = AccessTools.Field(managers, "GeneratedTextures");
            }
            object current = managerField.GetValue(null);
            if (current == null)
                return false; // the render managers are not up yet
            if (!ReferenceEquals(current, manager))
            {
                manager = current;
                texture = null;
                create = AccessTools.Method(manager.GetType(), "NewUserTexture",
                    new[] { typeof(string), typeof(int), typeof(int), typeof(MyGeneratedTextureType), typeof(bool), typeof(bool), typeof(byte[]) });
                discard = AccessTools.Method(manager.GetType(), "DisposeTex");
            }
            if (texture != null && srv.GetValue(texture) == null)
            {
                discard.Invoke(manager, new[] { texture });
                texture = null;
            }
            if (texture == null)
            {
                texture = create.Invoke(manager, new object[] { "VR comfort vignette", TextureSize, TextureSize, MyGeneratedTextureType.RGBA_Linear, false, true, Pixels(TextureSize) });
                srv = AccessTools.Property(texture.GetType(), "Srv");
                Log.Info($"Comfort vignette texture made ({TextureSize}x{TextureSize})");
            }
            return true;
        }
    }
}
