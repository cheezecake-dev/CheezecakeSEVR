using System;
using System.Diagnostics;
using SpaceEngineersVR.Input;
using VRageMath;
using VRageRender;

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
    /// The same vignette is the grey-out under G in a seat (<see cref="Common.SeatG.GreyOut"/>, its own setting): the darker of
    /// the two is drawn.
    /// Debug (<see cref="RenderDebug"/>): Vignette=0 turns it off, VignetteTest=1 draws it at full strength without motion.
    /// </remarks>
    internal static class Vignette
    {
        /// <summary>Debug: no vignette (<see cref="RenderDebug"/> Vignette=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: strength 1 whatever the motion and the setting (<see cref="RenderDebug"/> VignetteTest=1).</summary>
        public static bool Test { get; set; }

        /// <summary>The strength the eyes were drawn with this frame, 0 to 1: motion, smoothed, times the setting, or the grey-out under G if darker.</summary>
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
                    data[(y * size + x) * 4 + 3] = (byte)(AlphaAt(u, v) * 255f + 0.5f);
                }
            }
            return data;
        }

        /// <summary>The texture's darkness at (<paramref name="u"/>, <paramref name="v"/>) from its middle, as shares of its size.</summary>
        private static float AlphaAt(float u, float v) => Fade(((float)Math.Sqrt(u * u + v * v) - InnerUv) / (OuterUv - InnerUv));

        /// <summary>
        /// How dark the vignette is now (0 clear to 1 black) in the direction <paramref name="inHead"/>, in the head's own space
        /// (-z ahead), for what is shown over the eyes as a layer of its own and should be dimmed with them (the external view's
        /// screen, <see cref="VisorGlass.CopyThrough"/>): the texture as <see cref="Layout"/> lays it at the current strength, the
        /// direction taken onto the eye through the headset's field of view, centred (the eye's off-centre shift left out).
        /// Behind the head: as dark as the vignette's edge.
        /// </summary>
        public static float DarknessAt(Vector3 inHead)
        {
            float strength = Strength;
            if (strength < Visible || Off)
                return 0f;
            HeadsetFov.Half(out float halfWide, out float halfHigh);
            if (inHead.Z >= -1e-3f)
                return AlphaAt(OuterUv, 0f);
            float x = inHead.X / -inHead.Z / (float)Math.Tan(halfWide), y = inHead.Y / -inHead.Z / (float)Math.Tan(halfHigh);
            float scale = Scale(strength);
            return AlphaAt(x / (2f * scale), y / (2f * scale));
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
        private static readonly GeneratedTexture texture = new GeneratedTexture("VR comfort vignette", TextureSize, Pixels);

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
                    // The comfort vignette, or the grey-out under G in a seat (Common.SeatG), whichever is darker.
                    Strength = Off ? 0f : Test ? 1f : Math.Max(level * VRSettings.ComfortVignette, Common.SeatG.GreyOut);
                }
                if (Strength <= Visible)
                    return;
                object pixels = texture.Get();
                if (pixels == null)
                    return;
                var (x, y, width, height) = Layout(Strength, eye.X, eye.Y, projection.M31, projection.M32);
                EyeRenderer.Blend(eyeTarget, pixels, new MyViewport(x, y, width, height));
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "Comfort vignette failed; it stays off for the rest of the run");
            }
        }
    }
}
