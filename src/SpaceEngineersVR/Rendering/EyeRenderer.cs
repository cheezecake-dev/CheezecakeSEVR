using System;
using System.Reflection;
using HarmonyLib;
using SharpDX.DXGI;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Tracking;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Draws the 3D scene once per eye. Keen's old side-by-side stereo mode only reaches the older passes (voxels,
    /// lights, billboards, particles); the GeometryStage2 meshes (grids, characters), foliage and post-processing
    /// are mono, so with it on the eyes disagree. Instead each eye gets a complete, ordinary frame drawn from its own
    /// camera - the engine already draws the scene a second time within one frame for custom-size screenshots
    /// (MyRender11.TakeCustomSizedScreenshot), and this follows that path.
    /// </summary>
    /// <remarks>
    /// The game's render resolution (m_resolution, which sizes the GBuffer and every screen resource) is the eye
    /// resolution; the swapchain stays the size of the desktop mirror window. The GUI has a size of its own
    /// (<see cref="GuiLayout"/>) and is drawn into a panel texture at that size.
    /// With a headset, each eye is drawn from the runtime's eye pose and field of view and copied into the eye's OpenXR
    /// swapchain image; the window shows the left eye. Without one, the eyes sit side by side in the window.
    /// </remarks>
    internal static class EyeRenderer
    {
        private static readonly Assembly Render11 = typeof(MyDX11Render).Assembly;
        private static readonly Type Render = Render11.GetType("VRageRender.MyRender11", throwOnError: true);
        private static readonly Type RtvBindable = Render11.GetType("VRage.Render11.Resources.IRtvBindable", throwOnError: true);
        private static readonly Type SrvBindable = Render11.GetType("VRage.Render11.Resources.ISrvBindable", throwOnError: true);
        private static readonly Type BorrowedTexture = Render11.GetType("VRage.Render11.Resources.IBorrowedSrvTexture", throwOnError: true);
        private static readonly Type Managers = Render11.GetType("VRage.Render11.Common.MyManagers", throwOnError: true);
        private static readonly Type CopyToRT = Render11.GetType("VRageRender.MyCopyToRT", throwOnError: true);
        private static readonly Type Resource = Render11.GetType("VRage.Render11.Resources.IResource", throwOnError: true);
        private static readonly Type RenderContext = Render11.GetType("VRage.Render11.RenderContext.MyRenderContext", throwOnError: true);

        private static MethodInfo drawGameScene;
        private static MethodInfo drawDebugScene;
        private static MethodInfo renderMainSprites;
        private static MethodInfo copy;
        private static MethodInfo borrowRtv;
        private static MethodInfo release;
        private static MethodInfo copyResource;
        private static MethodInfo clearRtv;
        private static PropertyInfo device;
        private static PropertyInfo renderContext;
        private static PropertyInfo backbuffer;
        private static FieldInfo resolution;
        private static FieldInfo swapchain;
        private static FieldInfo mainViewportScale;
        private static FieldInfo texturesPool;
        private static object environmentMatrices;

        /// <summary>Size of the swapchain (the desktop mirror window). m_resolution no longer says, it is the eye size.</summary>
        private static Vector2I windowSize;

        internal static Vector2I WindowSize => windowSize;

        /// <summary>Swapchain format; eye images use it too, so the final copy in DrawGameScene behaves as for the window.</summary>
        private static Format windowFormat;

        private static readonly object[] eyeTargets = new object[2];

        /// <summary>This frame's eyes as drawn, for <see cref="HudPanel"/>.</summary>
        internal struct EyeView
        {
            /// <summary>The eye relative to the head.</summary>
            public Matrix InHead;

            public Matrix Projection;

            /// <summary>Where the eye shows in the window; zero size when it does not.</summary>
            public MyViewport Window;
        }

        internal static readonly EyeView[] Eyes = new EyeView[2];

        /// <summary>Both eyes were drawn this frame (there was a scene to draw), for the headset when <see cref="headsetEyes"/>.</summary>
        private static bool eyesDrawn, headsetEyes;

        public static void Patch(Harmony harmony)
        {
            drawGameScene = AccessTools.Method(Render, "DrawGameScene");
            drawDebugScene = AccessTools.Method(Render, "DrawDebugScene");
            renderMainSprites = AccessTools.Method(Render, "RenderMainSprites",
                new[] { RtvBindable, typeof(MyViewport), typeof(MyViewport), typeof(Vector2), typeof(MyViewport?) });
            copy = AccessTools.Method(CopyToRT, "Run");
            texturesPool = AccessTools.Field(Managers, "RwTexturesPool");
            borrowRtv = AccessTools.Method(texturesPool.FieldType, "BorrowRtv",
                new[] { typeof(string), typeof(int), typeof(int), typeof(Format), typeof(int), typeof(int) });
            release = AccessTools.Method(BorrowedTexture, "Release");
            copyResource = AccessTools.Method(RenderContext, "CopyResource", new[] { Resource, typeof(SharpDX.Direct3D11.Resource) });
            clearRtv = AccessTools.Method(RenderContext, "ClearRtv", new[] { RtvBindable, typeof(SharpDX.Mathematics.Interop.RawColor4) });
            device = AccessTools.Property(Render, "DeviceInstance");
            renderContext = AccessTools.Property(Render, "RC");
            backbuffer = AccessTools.Property(Render, "Backbuffer");
            resolution = AccessTools.Field(Render, "m_resolution");
            swapchain = AccessTools.Field(Render, "m_swapchain");
            mainViewportScale = AccessTools.Field(Render, "m_mainViewportScaleFactor");
            object environment = AccessTools.Field(Render, "Environment").GetValue(null);
            environmentMatrices = AccessTools.Field(environment.GetType(), "Matrices").GetValue(environment);

            StackWatchdog.StartFromEnvironment();
            harmony.Patch(AccessTools.Method(Render, "ResizeSwapchain"),
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(RecordWindowSize)));
            harmony.Patch(AccessTools.Method(Render, "CreateScreenResources"),
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(UseEyeResolution)));
            harmony.Patch(AccessTools.Method(Render, "DrawScene"),
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(DrawEyes)));
            harmony.Patch(AccessTools.Method(Render, "RenderMainSprites", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(EyeRenderer), nameof(RenderSpritesToPanel)));
            harmony.Patch(AccessTools.Method(Render, "ConsumeMainSprites"),
                postfix: new HarmonyMethod(typeof(EyeRenderer), nameof(ShowPanel)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MyDX11Render), nameof(MyDX11Render.BackBufferResolution)),
                postfix: new HarmonyMethod(typeof(EyeRenderer), nameof(ReportWindowResolution)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(MyDX11Render), nameof(MyDX11Render.MainViewport)),
                postfix: new HarmonyMethod(typeof(EyeRenderer), nameof(ReportWindowViewport)));
            Log.Info("Per-eye renderer patched");
        }

        /// <summary>Eye image size: the runtime's recommendation, or without a headset half the mirror window each.</summary>
        internal static Vector2I EyeResolution => XrSession.Exists
            ? new Vector2I(XrSession.EyeWidth, XrSession.EyeHeight)
            : new Vector2I(Math.Max(windowSize.X / 2, 1), Math.Max(windowSize.Y, 1));

        private static void RecordWindowSize(int width, int height)
        {
            Trace(ref resizeCalls, $"ResizeSwapchain {width}x{height}");
            windowSize = new Vector2I(width, height);
        }

        private static int resizeCalls, screenResourceCalls, drawCalls, spriteCalls;

        /// <summary>Logs the 1st, 2nd, 4th, 8th... call of a hook, so a hook stuck in a loop shows up without flooding the log.</summary>
        private static void Trace(ref int counter, string what)
        {
            int n = ++counter;
            if ((n & (n - 1)) == 0)
                Log.Info($"{what} (call {n})");
        }

        /// <summary>Every screen resource (GBuffer, lighting, HBAO) is sized from m_resolution here.</summary>
        private static void UseEyeResolution()
        {
            Trace(ref screenResourceCalls, "CreateScreenResources");
            StackWatchdog.Watch(System.Threading.Thread.CurrentThread);
            if (windowSize.X == 0)
                windowSize = (Vector2I)resolution.GetValue(null);
            if (swapchain.GetValue(null) is SwapChain chain)
                windowFormat = chain.Description.ModeDescription.Format;
            if (!headsetTried)
            {
                headsetTried = true;
                StartHeadset();
            }

            Vector2I eye = EyeResolution;
            if ((Vector2I)resolution.GetValue(null) != eye)
            {
                resolution.SetValue(null, eye);
                Log.Info($"Render resolution set to the eye size {eye.X}x{eye.Y} (window {windowSize.X}x{windowSize.Y})");
            }
        }

        private static bool headsetTried;

        // Each eye's placement this frame, worked out before either is drawn.
        private static readonly Matrix[] eyeFromHead = new Matrix[2], eyeInHead = new Matrix[2];
        private static readonly EnvMatrices.FovTangents?[] eyeFov = new EnvMatrices.FovTangents?[2];

        /// <summary>Opens the headset session on the game's device, the first time the device exists.</summary>
        private static void StartHeadset()
        {
            if (!XrRuntime.HasSystem)
                return;
            try
            {
                var dx = (SharpDX.Direct3D11.Device)device.GetValue(null);
                using (var dxgiDevice = dx.QueryInterface<SharpDX.DXGI.Device>())
                using (Adapter adapter = dxgiDevice.Adapter)
                {
                    if (adapter.Description.Luid != XrRuntime.AdapterLuid)
                    {
                        Log.Error($"The game is drawing on {adapter.Description.Description.Trim()} but the headset needs the GPU with LUID " +
                                  $"{XrRuntime.AdapterLuid:X}; showing both eyes in the window instead");
                        return;
                    }
                }
                XrSession.Create(dx, windowFormat);
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not start the headset session; showing both eyes in the window instead");
                XrSession.Destroy();
            }
        }

        /// <summary>Replaces MyRender11.DrawScene: both eyes, then the mirror, then the game's debug drawing.</summary>
        private static bool DrawEyes()
        {
            Trace(ref drawCalls, "DrawScene");
            if (XrSession.InFrame)
                EndHeadsetFrame(false, null); // a frame the GUI step never finished
            bool headset = BeginHeadsetFrame();
            object window = backbuffer.GetValue(null);
            Vector2I eye = EyeResolution;
            object pool = texturesPool.GetValue(null);
            EnvMatrices.Snapshot head = EnvMatrices.Save(environmentMatrices);
            object debugAmbientOcclusion = null;
            int drawn = 0;

            // The head now goes to the game thread; the camera it sent already includes the head as it was then, so
            // the eyes are placed relative to that (eye pose now * inverse(head in camera)) - head motion since the
            // game's update is still applied, every frame.
            FakeHead.Update(drawCalls);
            RenderDebug.Update(drawCalls);
            Matrix desktopHead = FakeHead.Active ? FakeHead.Head : Matrix.Identity;
            if (headset)
                HeadTracker.Set(ToMatrix(XrSession.Head), XrSession.OriginAboveFloor);
            else if (FakeHead.Active)
                HeadTracker.Set(desktopHead, FakeHead.OriginAboveFloor);
            else
                HeadTracker.Clear();
            Matrix? headInCamera = CameraHeadLink.HeadInCamera;
            Matrix fromCamera = headInCamera.HasValue ? Matrix.Invert(headInCamera.Value) : Matrix.Identity;
            HandResidual.Measure(fromCamera, head.InvViewD);
            object fullView = null;

            try
            {
                fullView = FullView.Draw(GuiLayout.Size);
                int billboards = OnceBillboards.Mark();

                // Both eyes' placements first: the sun shadow cascades are fitted once, round both eyes.
                for (int i = 0; i < 2; i++)
                {
                    if (headset)
                    {
                        XrView view = XrSession.View(i);
                        Matrix pose = ToMatrix(view.Pose);
                        eyeFromHead[i] = pose * fromCamera;
                        eyeFov[i] = ToTangents(view.Fov);
                        eyeInHead[i] = pose * Matrix.Invert(ToMatrix(XrSession.Head));
                    }
                    else
                    {
                        float halfIpd = (RenderDebug.DesktopIpd ?? VRSettings.FallbackIpd) * 0.5f;
                        Matrix inHead = Matrix.CreateTranslation(i == 0 ? -halfIpd : halfIpd, 0f, 0f);
                        eyeFromHead[i] = inHead * desktopHead * fromCamera;
                        eyeFov[i] = FakeHead.Fov(head.Projection);
                        eyeInHead[i] = inHead;
                    }
                }
                SharedShadows.Fit(environmentMatrices, head, eyeFromHead, eyeFov);

                for (int n = 0; n < 2; n++)
                {
                    if (n > 0)
                        OnceBillboards.Rewind(billboards);
                    int i = RenderDebug.RightFirst ? 1 - n : n;
                    bool left = i == 0;
                    eyeTargets[i] = borrowRtv.Invoke(pool, new object[] { left ? "VR left eye" : "VR right eye", eye.X, eye.Y, windowFormat, 1, 0 });

                    EnvMatrices.MakeEye(environmentMatrices, eyeFromHead[i], eyeFov[i]);
                    Eyes[i].InHead = eyeInHead[i];
                    Eyes[i].Projection = EnvMatrices.Projection(environmentMatrices);

                    object[] args = { eyeTargets[i], null };
                    SharedShadows.BeginEye(n);
                    drawGameScene.Invoke(null, args);
                    SharedShadows.EndEye();
                    if (debugAmbientOcclusion != null)
                        release.Invoke(debugAmbientOcclusion, null);
                    debugAmbientOcclusion = args[1];
                    if (fullView != null)
                        Blend(eyeTargets[i], fullView, new MyViewport(eye.X, eye.Y));
                    Vignette.Draw(eyeTargets[i], n, eye, Eyes[i].Projection);

                    if (headset)
                    {
                        SharpDX.Direct3D11.Texture2D image = XrSession.AcquireImage(i);
                        try
                        {
                            copyResource.Invoke(renderContext.GetValue(null), new[] { eyeTargets[i], image });
                        }
                        finally
                        {
                            XrSession.ReleaseImage(i);
                        }
                    }
                    drawn++;

                    EnvMatrices.Restore(environmentMatrices, head);
                }

                // The chase camera's picture, after both eyes (it puts the head camera back itself).
                if (drawn == 2)
                {
                    OnceBillboards.Rewind(billboards);
                    ExternalView.Render(environmentMatrices, head, drawCalls);
                }

                if (headset)
                {
                    // Left eye, filling the window's width; the eye is taller than the window, so its middle shows.
                    float height = windowSize.X * (float)eye.Y / eye.X;
                    Eyes[0].Window = new MyViewport(0, (windowSize.Y - height) / 2f, windowSize.X, height);
                    Eyes[1].Window = default;
                }
                else
                {
                    // Desktop: left eye on the left, right eye on the right.
                    int half = windowSize.X / 2;
                    Eyes[0].Window = new MyViewport(0, 0, half, windowSize.Y);
                    Eyes[1].Window = new MyViewport(half, 0, windowSize.X - half, windowSize.Y);
                }
                for (int i = 0; i < 2; i++)
                    if (Eyes[i].Window.Width > 0f)
                        copy.Invoke(null, new object[] { window, eyeTargets[i], false, Eyes[i].Window, true });
                if (drawn == 2)
                {
                    ExternalView.ShowInWindow(window, windowSize);
                    if (headset)
                        ExternalView.ShowInHeadset(ToMatrix(XrSession.Head));
                }
                eyesDrawn = drawn == 2;
                headsetEyes = headset && eyesDrawn;
            }
            finally
            {
                SharedShadows.EndEye();
                EnvMatrices.Restore(environmentMatrices, head);
                if (fullView != null)
                    release.Invoke(fullView, null);
                for (int i = 0; i < eyeTargets.Length; i++)
                {
                    if (eyeTargets[i] != null)
                        release.Invoke(eyeTargets[i], null);
                    eyeTargets[i] = null;
                }
                // The headset frame is ended after the GUI, which goes into it as a panel (ShowPanel).
            }

            // Debug drawing (and the per-frame debug message queue) stays on the head camera, in the window.
            drawDebugScene.Invoke(null, new[] { debugAmbientOcclusion });
            return false;
        }

        /// <summary>True when this frame goes to the headset. Any OpenXR failure turns the headset off for the rest of the run.</summary>
        private static bool BeginHeadsetFrame()
        {
            if (!XrSession.Exists)
                return false;
            try
            {
                XrSession.PollEvents();
                return XrSession.Exists && XrSession.BeginFrame();
            }
            catch (Exception e)
            {
                Log.Error(e, "Headset frame failed; VR display stops");
                XrSession.Destroy();
                return false;
            }
        }

        private static void EndHeadsetFrame(bool eyes, XrSession.Panel? panel)
        {
            if (!XrSession.InFrame)
                return;
            try
            {
                XrSession.EndFrame(eyes, panel, eyes ? ExternalView.TakeLayer() : null);
            }
            catch (Exception e)
            {
                Log.Error(e, "Headset frame failed; VR display stops");
                XrSession.Destroy();
            }
        }

        /// <summary>OpenXR and the game's view space agree: right-handed, x right, y up, -z forward. Row vectors, as VRageMath.</summary>
        internal static Matrix ToMatrix(in XrPosef pose)
        {
            Matrix m = Matrix.CreateFromQuaternion(new Quaternion(pose.Orientation.X, pose.Orientation.Y, pose.Orientation.Z, pose.Orientation.W));
            m.Translation = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
            return m;
        }

        internal static EnvMatrices.FovTangents ToTangents(in XrFovf fov) => new EnvMatrices.FovTangents
        {
            Left = (float)Math.Tan(fov.AngleLeft),
            Right = (float)Math.Tan(fov.AngleRight),
            Up = (float)Math.Tan(fov.AngleUp),
            Down = (float)Math.Tan(fov.AngleDown),
        };

        /// <summary>
        /// The game's screen size (game camera aspect, and the GUI layout until <see cref="GuiLayout"/> replaces its size)
        /// comes from these two, via MyRenderProxy. The renderer reports m_resolution, now the eye size; the game should
        /// keep the camera's aspect to the window.
        /// </summary>
        private static void ReportWindowResolution(ref Vector2I __result)
        {
            if (windowSize.X != 0)
                __result = windowSize;
        }

        private static void ReportWindowViewport(ref MyViewport __result)
        {
            if (windowSize.X != 0)
                __result = new MyViewport(windowSize.X, windowSize.Y);
        }

        /// <summary>
        /// The GUI and HUD are laid out for the GUI's size (<see cref="GuiLayout"/>), not the eye or the window: draw
        /// them at that size, into the panel (<see cref="HudPanel"/>), which is shown after the eyes.
        /// </summary>
        private static bool RenderSpritesToPanel()
        {
            Trace(ref spriteCalls, "RenderMainSprites");
            if (windowSize.X == 0)
                return true;

            Vector2I gui = GuiLayout.Size;
            float scale = (float)mainViewportScale.GetValue(null);
            var full = new MyViewport(gui.X, gui.Y);
            var bound = new MyViewport((gui.X - gui.X * scale) / 2f, (gui.Y - gui.Y * scale) / 2f, gui.X * scale, gui.Y * scale);
            try
            {
                object target = HudPanel.Begin(gui);
                renderMainSprites.Invoke(null, new object[] { target, bound, full, new Vector2(gui.X, gui.Y), null });
            }
            catch (Exception e)
            {
                Log.Error(e, "Drawing the GUI failed");
                throw;
            }
            return false;
        }

        /// <summary>
        /// After the GUI has been drawn (MyRender11.ConsumeMainSprites): shows the panel over the eyes, and ends the
        /// headset frame with it - beginning one first when there was no scene to draw (menus, loading).
        /// </summary>
        private static void ShowPanel()
        {
            bool eyes = eyesDrawn, headset = headsetEyes;
            eyesDrawn = headsetEyes = false;
            if (!XrSession.InFrame && XrSession.Exists && HudPanel.Pending)
                BeginHeadsetFrame();
            // No scene (main menu): both eyes without one, so the panel is placed in each as in game (MainMenu).
            bool menu = false;
            if (!eyes && HudPanel.Pending && MainMenu.Wanted)
                menu = eyes = MainMenu.DrawEyes(out headset);
            XrSession.Panel? panel = null;
            try
            {
                panel = HudPanel.Show(eyes);
                if (menu)
                    MainMenu.ShowBeam();
            }
            finally
            {
                EndHeadsetFrame(headset, panel);
            }
        }

        /// <summary>
        /// The head in the tracking space this frame, as the eyes were placed from it: the headset's, else the fake head's
        /// (<see cref="FakeHead"/>), else the origin.
        /// </summary>
        internal static Matrix Head => XrSession.InFrame && XrSession.ShouldRender ? ToMatrix(XrSession.Head)
            : FakeHead.Active ? FakeHead.Head : Matrix.Identity;

        // For HudPanel: the renderer's own resources and copies.
        internal static object Window => backbuffer.GetValue(null);

        internal static object Borrow(string name, Vector2I size) =>
            borrowRtv.Invoke(texturesPool.GetValue(null), new object[] { name, size.X, size.Y, windowFormat, 1, 0 });

        internal static void Release(object texture) => release.Invoke(texture, null);

        internal static void Clear(object target) =>
            clearRtv.Invoke(renderContext.GetValue(null), new[] { target, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f) });

        /// <summary>Draws <paramref name="source"/> stretched over <paramref name="viewport"/> of <paramref name="target"/>, premultiplied alpha over what is there.</summary>
        internal static void Blend(object target, object source, MyViewport viewport) =>
            copy.Invoke(null, new object[] { target, source, true, viewport, true });

        // For ExternalView: a pass of its own through the same scene drawing the eyes use.
        internal static Format WindowFormat => windowFormat;

        /// <summary>The renderer's immediate render context (MyRender11.RC).</summary>
        internal static object Context => renderContext.GetValue(null);

        /// <summary>Draws the game scene from the camera in the environment matrices into <paramref name="target"/>, as an eye is drawn.</summary>
        internal static void DrawSceneTo(object target)
        {
            object[] args = { target, null };
            drawGameScene.Invoke(null, args);
            if (args[1] != null)
                release.Invoke(args[1], null);
        }

        /// <summary>Draws <paramref name="source"/> stretched over <paramref name="viewport"/> of <paramref name="target"/>, replacing what is there.</summary>
        internal static void CopyOpaque(object target, object source, MyViewport viewport) =>
            copy.Invoke(null, new object[] { target, source, false, viewport, true });

        /// <summary>Copies a texture bit for bit into a swapchain image of the same size.</summary>
        internal static void CopyToImage(object texture, SharpDX.Direct3D11.Texture2D image) =>
            copyResource.Invoke(renderContext.GetValue(null), new[] { texture, image });
    }
}
