using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SharpDX.Mathematics.Interop;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.OpenXR;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The main menu (and every screen shown while no world is loaded) in VR. With no world there is no scene: the
    /// game never sends the draw-scene message (MyGuiScreenGamePlay.Draw does, once the world is loaded), so the
    /// renderer skips <c>MyRender11.DrawScene</c>, which <see cref="EyeRenderer"/> turns into the two eye views, and the
    /// GUI panel fell back to being stretched over the whole window. Here the eyes are drawn without a scene instead:
    /// a black backdrop in each (the menu's own background is a video, a 2D sprite that goes into the panel with the
    /// buttons, so there is no 3D scene to draw), and the panel placed exactly as in game (<see cref="WorldMenu"/>,
    /// <see cref="HudPanel"/>), in each eye of the window and as the quad layer of the headset frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The laser pointer needs nothing from a scene: <see cref="Laser"/> casts the hand's aim, a pose in the tracking space,
    /// at the panel's placement, also in the tracking space, on the game thread (MyVRageInput.Update runs every frame,
    /// whatever screens are up). What it lacked was its beam: that is a world billboard from MySession.DrawSync, which
    /// the gameplay screen calls and the main menu never does, and HandWorld has no camera to put it in (MySector.MainCamera
    /// exists only while a world is loaded). The beam is drawn here instead, from the same two tracking-space points
    /// (<see cref="Laser.TryGetLine"/>), straight into each eye image, and, without a headset, again over the panel in the window.
    /// </para>
    /// <para>
    /// Loading screens are left as they were (the panel over the window, a quad in the headset): this applies only when
    /// there is no world and no loading screen. <see cref="Wanted"/> is decided on the game thread; the eyes are drawn
    /// from <see cref="EyeRenderer"/>'s ShowPanel, on the render thread, once the GUI has been drawn into the panel.
    /// </para>
    /// </remarks>
    internal static class MainMenu
    {
        /// <summary>Debug: the main menu is the flat panel over the window, as before (<see cref="RenderDebug"/> MainMenuVR=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Closer to an eye than this (metres, along its view) the beam is cut off.</summary>
        internal const float Near = 0.05f;

        /// <summary>At most this many squares make up a beam in one view.</summary>
        internal const int MaxDots = 120;

        /// <summary>No world is loaded and nothing is loading. Written on the game thread as the screens are drawn, read on the render thread.</summary>
        private static volatile bool noWorld;

        /// <summary>There is no world, no loading screen: the frame has no scene and the main menu shows.</summary>
        public static bool Wanted => noWorld;

        private static int frame, failures, trackErrors, beamErrors;
        private static bool failed, drew, drewToHeadset, announced;

        private static MethodInfo clearRtv;

        private static readonly List<Dot> dots = new List<Dot>(MaxDots + 1);

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyScreenManager), nameof(MyScreenManager.Draw)),
                    postfix: new HarmonyMethod(typeof(MainMenu), nameof(Track)));
            }
            catch (Exception e)
            {
                Log.Error(e, "The main menu in VR could not be hooked; it stays a flat panel over the window");
            }
        }

        // Every frame, on the thread that owns the screens.
        private static void Track()
        {
            try
            {
                noWorld = MySession.Static == null && !MyScreenManager.ExistsScreenOfType(typeof(MyGuiScreenLoading));
            }
            catch (Exception e)
            {
                noWorld = false;
                if (trackErrors++ < 3)
                    Log.Error(e, "Could not tell whether the main menu is up");
            }
        }

        // ---- The frame: render thread ----

        /// <summary>
        /// Draws this frame's two eyes for a menu with no scene, and sets <see cref="EyeRenderer.Eyes"/> so that
        /// <see cref="HudPanel.Show"/> puts the panel in each. Call after the headset frame was begun and the GUI drawn
        /// into the panel; follow with <see cref="HudPanel.Show"/> and <see cref="ShowBeam"/>.
        /// </summary>
        /// <param name="headset">The eye images were filled for the headset, so its frame goes with the eyes.</param>
        /// <returns>The eyes are drawn. False leaves the frame as a loading screen's: the panel over the window.</returns>
        public static bool DrawEyes(out bool headset)
        {
            headset = false;
            drew = false;
            try
            {
                // The files that steer the fake head and the debug switches are read in the scene's draw; here there is none.
                frame++;
                FakeHead.Update(frame);
                RenderDebug.Update(frame);
                if (Off || failed)
                    return false;

                bool drawn = Draw(out headset);
                failures = 0;
                drew = drawn;
                drewToHeadset = headset;
                if (drawn && !announced)
                {
                    announced = true;
                    Log.Info($"Main menu in VR: no scene, so both eyes are black with the panel placed in each ({(headset ? "headset" : "window")}); MainMenuVR=0 turns it off");
                }
                return drawn;
            }
            catch (Exception e)
            {
                headset = false;
                drew = false;
                if (++failures >= 3)
                {
                    failed = true;
                    Log.Error(e, "Drawing the main menu's eyes failed three times; it stays a flat panel over the window");
                }
                else
                {
                    Log.Error(e, "Drawing the main menu's eyes failed");
                }
                return false;
            }
        }

        private static bool Draw(out bool headset)
        {
            headset = false;
            Vector2I window = EyeRenderer.WindowSize;
            if (window.X <= 0 || window.Y <= 0)
                return false;
            Vector2I eye = EyeRenderer.EyeResolution;
            bool toHeadset = XrSession.InFrame && XrSession.ShouldRender;
            Matrix head = EyeRenderer.Head;

            MyViewport[] windows = Windows(toHeadset, window, eye);
            Matrix inverseHead = toHeadset ? Matrix.Invert(EyeRenderer.ToMatrix(XrSession.Head)) : Matrix.Identity;
            float halfIpd = VRSettings.FallbackIpd * 0.5f;
            for (int i = 0; i < 2; i++)
            {
                Matrix inHead;
                EnvMatrices.FovTangents fov;
                if (toHeadset)
                {
                    XrView view = XrSession.View(i);
                    inHead = EyeRenderer.ToMatrix(view.Pose) * inverseHead;
                    fov = EyeRenderer.ToTangents(view.Fov);
                }
                else
                {
                    inHead = Matrix.CreateTranslation(i == 0 ? -halfIpd : halfIpd, 0f, 0f);
                    EnvMatrices.FovTangents centred = DesktopFov(GameFieldOfView(), (float)window.X / window.Y, 0f);
                    fov = FakeHead.Fov(Perspective(centred)) ?? centred;
                }
                EyeRenderer.Eyes[i].InHead = inHead;
                EyeRenderer.Eyes[i].Projection = Perspective(fov);
                EyeRenderer.Eyes[i].Window = windows[i];
            }

            Prepare();
            object windowTarget = EyeRenderer.Window;
            object target = EyeRenderer.Borrow("VR menu eye", eye);
            try
            {
                var whole = new MyViewport(eye.X, eye.Y);
                for (int i = 0; i < 2; i++)
                {
                    Clear(target, 0f, 0f, 0f, 1f);
                    DrawBeam(target, i, head, whole, whole);

                    if (toHeadset)
                    {
                        SharpDX.Direct3D11.Texture2D image = XrSession.AcquireImage(i);
                        try
                        {
                            EyeRenderer.CopyToImage(target, image);
                        }
                        finally
                        {
                            XrSession.ReleaseImage(i);
                        }
                    }
                    if (EyeRenderer.Eyes[i].Window.Width > 0f)
                        EyeRenderer.CopyOpaque(windowTarget, target, EyeRenderer.Eyes[i].Window);
                }
            }
            finally
            {
                EyeRenderer.Release(target);
            }
            headset = toHeadset;
            return true;
        }

        /// <summary>
        /// After the panel was blended into the window: the beam again, over it, in each eye of the window, so that the
        /// desktop view shows where the ray goes across the panel too. In the eye images (as in game, where the beam is
        /// a billboard in the scene) the panel hides the part of the beam that passes behind it. With a headset the
        /// window is only a mirror of one eye and this is skipped, to keep the frame light.
        /// </summary>
        public static void ShowBeam()
        {
            bool shown = drew;
            drew = false;
            if (!shown || drewToHeadset)
                return;
            try
            {
                Vector2I size = EyeRenderer.WindowSize;
                var whole = new MyViewport(size.X, size.Y);
                Matrix head = EyeRenderer.Head;
                object window = EyeRenderer.Window;
                for (int i = 0; i < 2; i++)
                {
                    MyViewport eye = EyeRenderer.Eyes[i].Window;
                    if (eye.Width > 0f)
                        DrawBeam(window, i, head, eye, Intersect(eye, whole));
                }
            }
            catch (Exception e)
            {
                if (beamErrors++ < 3)
                    Log.Error(e, "Drawing the laser beam over the main menu failed");
            }
        }

        /// <summary>The laser's beam in one eye: squares along its projection, into <paramref name="viewport"/> of the target, kept within <paramref name="area"/>.</summary>
        private static void DrawBeam(object target, int eye, in Matrix head, MyViewport viewport, MyViewport area)
        {
            if (!Laser.TryGetLine(out Vector3 from, out Vector3 to))
                return;
            Matrix eyeFromTracking = Matrix.Invert(EyeRenderer.Eyes[eye].InHead * head);
            BeamDots(from, to, eyeFromTracking, EyeRenderer.Eyes[eye].Projection, new Vector2(viewport.Width, viewport.Height),
                Laser.LineThickness, dots);
            if (dots.Count == 0)
                return;

            Vector4 color = Laser.LineColor;
            object square = EyeRenderer.Borrow("VR menu beam", new Vector2I(4, 4));
            try
            {
                Clear(square, color.X, color.Y, color.Z, color.W);
                foreach (Dot dot in dots)
                {
                    // The square is one colour, so cutting it to the area it may paint in is exact.
                    MyViewport rect = Intersect(new MyViewport(viewport.OffsetX + dot.X - dot.Radius, viewport.OffsetY + dot.Y - dot.Radius,
                        dot.Radius * 2f, dot.Radius * 2f), area);
                    if (rect.Width > 0f)
                        EyeRenderer.Blend(target, square, rect);
                }
            }
            finally
            {
                EyeRenderer.Release(square);
            }
        }

        private static void Prepare()
        {
            if (clearRtv != null)
                return;
            Assembly render11 = typeof(MyDX11Render).Assembly;
            Type context = render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);
            Type rtv = render11.GetType("VRage.Render11.Resources.IRtvBindable", throwOnError: true);
            clearRtv = AccessTools.Method(context, "ClearRtv", new[] { rtv, typeof(RawColor4) })
                       ?? throw new MissingMethodException("MyRenderContext.ClearRtv(IRtvBindable, RawColor4)");
        }

        private static void Clear(object target, float r, float g, float b, float a) =>
            clearRtv.Invoke(EyeRenderer.Context, new[] { target, new RawColor4(r, g, b, a) });

        // ---- Geometry: no game state, so it can be checked on its own ----

        /// <summary>The game camera's vertical field of view, radians: the player's setting, as MySector builds its camera from.</summary>
        private static float GameFieldOfView()
        {
            float fov = 0f;
            try
            {
                fov = MySandboxGame.Config?.FieldOfView ?? 0f;
            }
            catch (Exception)
            {
                // The config is not up yet: the default below.
            }
            return fov > 0.3f && fov < 2.6f ? fov : MathHelper.ToRadians(70f);
        }

        /// <summary>
        /// The window's eyes without a headset, as the game camera sees them in game (a vertical field of view over the
        /// window's aspect): the tangents of the half-angles, the whole view shifted sideways by <paramref name="shift"/>.
        /// </summary>
        internal static EnvMatrices.FovTangents DesktopFov(float verticalFov, float aspect, float shift)
        {
            float v = (float)Math.Tan(verticalFov * 0.5f), h = v * aspect;
            return new EnvMatrices.FovTangents { Left = -h + shift, Right = h + shift, Up = v, Down = -v };
        }

        /// <summary>
        /// A perspective projection for an eye of the given field of view, in the form the renderer's own have (row
        /// vectors, w = -z, the field of view in M11/M22 and its off-centre shift in M31/M32): all that
        /// <see cref="HudPanel"/> and the beam need of it is where a point lands in the eye. Depth is not used.
        /// </summary>
        internal static Matrix Perspective(in EnvMatrices.FovTangents fov)
        {
            var projection = new Matrix { M33 = -1f, M34 = -1f, M43 = -Near };
            return EnvMatrices.WithFov(projection, fov);
        }

        /// <summary>
        /// Where each eye shows in the window, as <see cref="EyeRenderer"/> draws them: with a headset the left eye fills
        /// the width (the eye is taller than the window, so its middle shows) and the right shows nowhere; without one the
        /// eyes sit side by side, left on the left. A viewport without width is not shown.
        /// </summary>
        internal static MyViewport[] Windows(bool headset, Vector2I window, Vector2I eye)
        {
            if (headset)
            {
                float height = window.X * (float)eye.Y / eye.X;
                return new[] { new MyViewport(0f, (window.Y - height) / 2f, window.X, height), default };
            }
            int half = window.X / 2;
            return new[] { new MyViewport(0f, 0f, half, window.Y), new MyViewport(half, 0f, window.X - half, window.Y) };
        }

        internal static MyViewport Intersect(MyViewport a, MyViewport b)
        {
            float left = Math.Max(a.OffsetX, b.OffsetX), top = Math.Max(a.OffsetY, b.OffsetY);
            float right = Math.Min(a.OffsetX + a.Width, b.OffsetX + b.Width), bottom = Math.Min(a.OffsetY + a.Height, b.OffsetY + b.Height);
            return right > left && bottom > top ? new MyViewport(left, top, right - left, bottom - top) : default;
        }

        /// <summary>One square of the beam, in pixels of the view it is drawn into.</summary>
        internal struct Dot
        {
            public float X, Y, Radius;
        }

        /// <summary>
        /// The beam from <paramref name="from"/> to <paramref name="to"/> (both in the tracking space) as squares along its
        /// projection into an eye, <paramref name="size"/> pixels across. The part nearer than <see cref="Near"/> is cut off
        /// and the rest clipped to the view; the squares are as wide as the beam is at their depth
        /// (<paramref name="thickness"/>, metres) but never thinner than a few pixels, and overlap so that they join.
        /// </summary>
        /// <param name="eyeFromTracking">The inverse of the eye's pose in the tracking space.</param>
        /// <returns>The number of squares; <paramref name="result"/> holds them.</returns>
        internal static int BeamDots(Vector3 from, Vector3 to, in Matrix eyeFromTracking, in Matrix projection, Vector2 size,
            float thickness, List<Dot> result)
        {
            result.Clear();
            Vector3 a = Vector3.Transform(from, eyeFromTracking), b = Vector3.Transform(to, eyeFromTracking);
            float depthA = -a.Z, depthB = -b.Z;
            if (!IsFinite(a.X + a.Y + a.Z + b.X + b.Y + b.Z) || !(size.X > 0f) || !(size.Y > 0f)
                || (!(depthA > Near) && !(depthB > Near)))
                return 0;

            // The part in front of the near plane, as a share (s0 to s1) of the segment from a to b.
            float s0 = 0f, s1 = 1f;
            if (depthA <= Near)
                s0 = (Near - depthA) / (depthB - depthA);
            else if (depthB <= Near)
                s1 = (depthA - Near) / (depthA - depthB);
            Vector3 start = Vector3.Lerp(a, b, s0), end = Vector3.Lerp(a, b, s1);

            Vector2 pa = ToPixels(start, projection, size), pb = ToPixels(end, projection, size);
            if (!IsFinite(pa.X + pa.Y + pb.X + pb.Y))
                return 0;
            float invA = 1f / -start.Z, invB = 1f / -end.Z;

            // The part inside the view, as a share (t0 to t1) of what is left. Liang-Barsky against the view's rectangle.
            const float margin = 2f;
            float t0 = 0f, t1 = 1f;
            if (!Clip(pa.X + margin, pb.X - pa.X, ref t0, ref t1) || !Clip(size.X + margin - pa.X, pa.X - pb.X, ref t0, ref t1)
                || !Clip(pa.Y + margin, pb.Y - pa.Y, ref t0, ref t1) || !Clip(size.Y + margin - pa.Y, pa.Y - pb.Y, ref t0, ref t1))
                return 0;

            float length = Vector2.Distance(pa, pb) * (t1 - t0);
            float minRadius = MathHelper.Clamp(size.Y / 800f, 1.2f, 4f);
            int count = MathHelper.Clamp((int)Math.Ceiling(length / (minRadius * 1.5f)), 1, MaxDots);
            float spacing = length / count;
            for (int i = 0; i <= count; i++)
            {
                float t = t0 + (t1 - t0) * i / count;
                // Depth is not linear on the screen, its inverse is.
                float depth = 1f / (invA + (invB - invA) * t);
                float radius = Math.Max(Math.Max(minRadius, thickness * 0.5f * projection.M22 * size.Y * 0.5f / depth), spacing * 0.6f);
                result.Add(new Dot { X = pa.X + (pb.X - pa.X) * t, Y = pa.Y + (pb.Y - pa.Y) * t, Radius = radius });
            }
            return result.Count;
        }

        /// <summary>A point in the eye's space (x right, y up, -z forward) to pixels, 0,0 the top left of the view.</summary>
        internal static Vector2 ToPixels(Vector3 inEye, in Matrix projection, Vector2 size)
        {
            Vector4 clip = Vector4.Transform(new Vector4(inEye, 1f), projection);
            return new Vector2((clip.X / clip.W + 1f) * 0.5f * size.X, (1f - clip.Y / clip.W) * 0.5f * size.Y);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>One edge of Liang-Barsky: keeps what satisfies <c>distance + t * step >= 0</c>. False when none of the segment does.</summary>
        private static bool Clip(float distance, float step, ref float t0, ref float t1)
        {
            if (step == 0f)
                return distance >= 0f;
            float t = -distance / step;
            if (step > 0f)
            {
                if (t > t1)
                    return false;
                t0 = Math.Max(t0, t);
            }
            else
            {
                if (t < t0)
                    return false;
                t1 = Math.Min(t1, t);
            }
            return true;
        }
    }
}
