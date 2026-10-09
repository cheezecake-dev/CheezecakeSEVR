using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The headset session on the game's render device: one swapchain per eye and the frame loop. Everything here
    /// runs on the render thread, which owns the device's immediate context the runtime also uses.
    /// </summary>
    internal static unsafe class XrSession
    {
        private static ulong session;
        private static ulong space;
        private static ulong viewSpace;
        private static ulong stageSpace;
        private static long floorSeenAt;
        private static bool floorLogged;
        private static readonly ulong[] swapchains = new ulong[2];
        private static readonly Texture2D[][] images = new Texture2D[2][];
        private static Format swapchainFormat;
        private static ulong panelSwapchain;
        private static Texture2D[] panelImages;
        private static int panelWidth, panelHeight;
        private static ulong screenSwapchain;
        private static Texture2D[] screenImages;
        private static int screenWidth, screenHeight;
        private static XrSessionState state;
        private static XrFrameState frame;
        private static readonly XrView[] views = new XrView[2];

        /// <summary>True between xrBeginSession and xrEndSession: frames have to be submitted.</summary>
        public static bool Running { get; private set; }

        /// <summary>True between xrBeginFrame and xrEndFrame.</summary>
        public static bool InFrame { get; private set; }

        /// <summary>The current frame is shown: its layers have to be drawn.</summary>
        public static bool ShouldRender { get; private set; }

        /// <summary>A flat panel (the GUI and HUD), from the panel swapchain. Metres.</summary>
        public struct Panel
        {
            /// <summary>Head-locked: this far ahead of the head. Unused when <see cref="WorldLocked"/>.</summary>
            public float Distance, Width, Height;

            /// <summary>The panel stays at <see cref="Pose"/> in the tracking space (LOCAL) instead of following the head.</summary>
            public bool WorldLocked;

            public XrPosef Pose;
        }

        /// <summary>
        /// A picture on a flat screen standing in the tracking space (LOCAL): the external view
        /// (<see cref="Rendering.ExternalView"/>). Metres. Opaque, no alpha.
        /// </summary>
        public struct Screen
        {
            public float Width, Height;

            /// <summary>Where the middle of the screen is, in the tracking space, facing +z.</summary>
            public XrPosef Pose;
        }

        /// <summary>The session is running and has the input focus (not behind the runtime's dashboard).</summary>
        public static bool Focused => Running && state == XrSessionState.Focused;

        public static bool Exists => session != 0;
        public static int EyeWidth { get; private set; }
        public static int EyeHeight { get; private set; }

        /// <summary>The head (the runtime's VIEW space) in the LOCAL space, at the current frame's display time.</summary>
        public static XrPosef Head { get; private set; } = XrPosef.Identity;

        /// <summary>
        /// How high the LOCAL space's origin is above the floor (the STAGE space's origin) at the current frame's display
        /// time, metres; null when the runtime has no STAGE space or has not located it. It changes when the user recentres
        /// (LOCAL's origin follows the head), which is why it is measured every frame. A frame or two without a location
        /// keep the last height, so the view does not jump; after a second the floor is unknown.
        /// </summary>
        public static float? OriginAboveFloor { get; private set; }

        /// <summary>The eye poses (in the LOCAL space: origin at the head when tracking started) and FOVs of the current frame.</summary>
        public static XrView View(int eye) => views[eye];

        /// <param name="device">The game's D3D11 device; it has to be on the GPU the runtime asked for.</param>
        /// <param name="eyeFormat">Format the eyes are drawn in. The swapchain is the same bits in its sRGB flavour.</param>
        public static void Create(Device device, Format eyeFormat)
        {
            var binding = new XrGraphicsBindingD3D11KHR { Type = XrStructureType.GraphicsBindingD3D11KHR, Device = device.NativePointer };
            var info = new XrSessionCreateInfo { Type = XrStructureType.SessionCreateInfo, Next = (IntPtr)(&binding), SystemId = XrRuntime.SystemId };
            Xr.Check(Xr.xrCreateSession(XrRuntime.Instance, ref info, out session), "xrCreateSession", XrRuntime.Instance);

            // Seated: LOCAL keeps the origin where the head was at start (or at the last recentre).
            var spaceInfo = new XrReferenceSpaceCreateInfo
            {
                Type = XrStructureType.ReferenceSpaceCreateInfo,
                ReferenceSpaceType = XrReferenceSpaceType.Local,
                PoseInReferenceSpace = XrPosef.Identity,
            };
            Xr.Check(Xr.xrCreateReferenceSpace(session, ref spaceInfo, out space), "xrCreateReferenceSpace", XrRuntime.Instance);
            spaceInfo.ReferenceSpaceType = XrReferenceSpaceType.View;
            Xr.Check(Xr.xrCreateReferenceSpace(session, ref spaceInfo, out viewSpace), "xrCreateReferenceSpace", XrRuntime.Instance);
            CreateStageSpace();

            EyeWidth = (int)XrRuntime.EyeWidth;
            EyeHeight = (int)XrRuntime.EyeHeight;
            Format format = swapchainFormat = ChooseFormat(eyeFormat);
            for (int eye = 0; eye < 2; eye++)
                swapchains[eye] = CreateSwapchain(EyeWidth, EyeHeight, out images[eye]);

            for (int eye = 0; eye < 2; eye++)
                views[eye] = new XrView { Type = XrStructureType.View, Pose = XrPosef.Identity };

            XrInput.Create(session, space); // never throws: without controllers the display still works
            Log.Info($"OpenXR session created: eyes {EyeWidth}x{EyeHeight} {format}");
        }

        /// <summary>
        /// The STAGE space, whose origin is on the floor, when the runtime offers one: the floor for height calibration.
        /// Never throws; without it the floor is simply not known.
        /// </summary>
        private static void CreateStageSpace()
        {
            stageSpace = 0;
            try
            {
                Xr.Check(Xr.xrEnumerateReferenceSpaces(session, 0, out uint count, null), "xrEnumerateReferenceSpaces", XrRuntime.Instance);
                int[] types = new int[count];
                if (count > 0)
                    fixed (int* ptr = types)
                        Xr.Check(Xr.xrEnumerateReferenceSpaces(session, count, out count, ptr), "xrEnumerateReferenceSpaces", XrRuntime.Instance);
                if (Array.IndexOf(types, (int)XrReferenceSpaceType.Stage) < 0)
                {
                    Log.Info("OpenXR STAGE space: not offered by the runtime; the floor is not known, so height cannot be calibrated " +
                             $"(it offers {string.Join(", ", types)})");
                    return;
                }
                var info = new XrReferenceSpaceCreateInfo
                {
                    Type = XrStructureType.ReferenceSpaceCreateInfo,
                    ReferenceSpaceType = XrReferenceSpaceType.Stage,
                    PoseInReferenceSpace = XrPosef.Identity,
                };
                Xr.Check(Xr.xrCreateReferenceSpace(session, ref info, out stageSpace), "xrCreateReferenceSpace (STAGE)", XrRuntime.Instance);
                Log.Info("OpenXR STAGE space: available; its origin is the floor for height calibration");
            }
            catch (Exception e)
            {
                stageSpace = 0;
                Log.Error(e, "OpenXR STAGE space could not be set up; the floor is not known");
            }
        }

        /// <summary>
        /// Where the LOCAL space's origin is above the floor, at the display time the eyes were located for. A failure to
        /// locate it makes the floor unknown, never the headset fail.
        /// </summary>
        private static void LocateFloor()
        {
            if (stageSpace == 0)
                return;
            var location = new XrSpaceLocation { Type = XrStructureType.SpaceLocation };
            int result = Xr.xrLocateSpace(space, stageSpace, frame.PredictedDisplayTime, ref location);
            long now = Stopwatch.GetTimestamp();
            if (result >= 0 && (location.LocationFlags & XrConst.SpaceLocationPositionValid) != 0)
            {
                // LOCAL's position in STAGE: STAGE's up is the vertical and its origin is on the floor, so Y is the height.
                OriginAboveFloor = location.Pose.Position.Y;
                floorSeenAt = now;
                if (!floorLogged)
                {
                    floorLogged = true;
                    Log.Info($"OpenXR floor located: the LOCAL origin is {location.Pose.Position.Y:0.00} m above it");
                }
            }
            else if (OriginAboveFloor.HasValue && now - floorSeenAt > Stopwatch.Frequency)
            {
                OriginAboveFloor = null;
                floorLogged = false;
                Log.Warn($"OpenXR floor lost (xrLocateSpace {result}, flags {location.LocationFlags:X}); height cannot be corrected until it is back");
            }
        }

        /// <summary>
        /// The game writes display-ready (gamma-encoded) values. Copied bit for bit into an sRGB swapchain they are
        /// read the way they were meant; in a UNORM swapchain the compositor would encode them a second time.
        /// </summary>
        private static Format ChooseFormat(Format eyeFormat)
        {
            Xr.Check(Xr.xrEnumerateSwapchainFormats(session, 0, out uint count, null), "xrEnumerateSwapchainFormats", XrRuntime.Instance);
            long[] formats = new long[count];
            fixed (long* ptr = formats)
                Xr.Check(Xr.xrEnumerateSwapchainFormats(session, count, out count, ptr), "xrEnumerateSwapchainFormats", XrRuntime.Instance);

            Format srgb = eyeFormat == Format.R8G8B8A8_UNorm ? Format.R8G8B8A8_UNorm_SRgb
                : eyeFormat == Format.B8G8R8A8_UNorm ? Format.B8G8R8A8_UNorm_SRgb
                : eyeFormat;
            if (Array.IndexOf(formats, (long)srgb) >= 0)
                return srgb;
            if (Array.IndexOf(formats, (long)eyeFormat) >= 0)
            {
                Log.Warn($"The runtime has no {srgb} swapchain; using {eyeFormat}, the headset image will look washed out");
                return eyeFormat;
            }
            throw new InvalidOperationException($"The runtime offers no swapchain format the eyes ({eyeFormat}) can be copied into: " +
                                                string.Join(", ", formats));
        }

        private static ulong CreateSwapchain(int width, int height, out Texture2D[] textures)
        {
            var info = new XrSwapchainCreateInfo
            {
                Type = XrStructureType.SwapchainCreateInfo,
                UsageFlags = XrConst.SwapchainUsageColorAttachment | XrConst.SwapchainUsageTransferDst,
                Format = (long)swapchainFormat,
                SampleCount = 1,
                Width = (uint)width,
                Height = (uint)height,
                FaceCount = 1,
                ArraySize = 1,
                MipCount = 1,
            };
            Xr.Check(Xr.xrCreateSwapchain(session, ref info, out ulong swapchain), "xrCreateSwapchain", XrRuntime.Instance);

            Xr.Check(Xr.xrEnumerateSwapchainImages(swapchain, 0, out uint count, null), "xrEnumerateSwapchainImages", XrRuntime.Instance);
            var native = new XrSwapchainImageD3D11KHR[count];
            for (int i = 0; i < native.Length; i++)
                native[i].Type = XrStructureType.SwapchainImageD3D11KHR;
            fixed (XrSwapchainImageD3D11KHR* ptr = native)
                Xr.Check(Xr.xrEnumerateSwapchainImages(swapchain, count, out count, ptr), "xrEnumerateSwapchainImages", XrRuntime.Instance);

            // The runtime owns the textures; each wrapper holds a reference of its own so disposing it is balanced.
            textures = new Texture2D[count];
            for (int i = 0; i < count; i++)
            {
                Marshal.AddRef(native[i].Texture);
                textures[i] = new Texture2D(native[i].Texture);
            }
            return swapchain;
        }

        private static void DestroySwapchain(ref ulong swapchain, ref Texture2D[] textures)
        {
            if (textures != null)
                foreach (Texture2D texture in textures)
                    texture.Dispose();
            textures = null;
            if (swapchain != 0)
                Xr.xrDestroySwapchain(swapchain);
            swapchain = 0;
        }

        /// <summary>Handles the runtime's events: starts and stops the session as the headset comes and goes.</summary>
        public static void PollEvents()
        {
            var buffer = new XrEventDataBuffer();
            while (true)
            {
                buffer.Type = XrStructureType.EventDataBuffer;
                buffer.Next = IntPtr.Zero;
                int result = Xr.Check(Xr.xrPollEvent(XrRuntime.Instance, ref buffer), "xrPollEvent", XrRuntime.Instance);
                if (result == XrResult.EventUnavailable)
                    return;

                if (buffer.Type == XrStructureType.EventDataSessionStateChanged)
                {
                    XrEventDataSessionStateChanged changed = *(XrEventDataSessionStateChanged*)&buffer;
                    OnStateChanged(changed.State);
                }
                else if (buffer.Type == XrStructureType.EventDataInstanceLossPending)
                {
                    Log.Warn("OpenXR instance is being lost (runtime shutting down); VR stops");
                    Destroy();
                    return;
                }
                else
                {
                    XrInput.OnEvent(&buffer); // recentre, controllers changing
                }
            }
        }

        private static void OnStateChanged(XrSessionState newState)
        {
            Log.Info($"OpenXR session {state} -> {newState}");
            state = newState;
            switch (newState)
            {
                case XrSessionState.Ready:
                    var begin = new XrSessionBeginInfo { Type = XrStructureType.SessionBeginInfo, PrimaryViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo };
                    Xr.Check(Xr.xrBeginSession(session, ref begin), "xrBeginSession", XrRuntime.Instance);
                    Running = true;
                    break;
                case XrSessionState.Stopping:
                    Running = false;
                    Xr.Check(Xr.xrEndSession(session), "xrEndSession", XrRuntime.Instance);
                    break;
                case XrSessionState.Exiting:
                case XrSessionState.LossPending:
                    Destroy();
                    break;
            }
        }

        /// <summary>
        /// Waits for the headset's next frame and locates the eyes for its display time. True when the eyes should be
        /// drawn for the headset; whenever a frame was begun (<see cref="InFrame"/>), <see cref="EndFrame"/> must follow.
        /// </summary>
        public static bool BeginFrame()
        {
            if (!Running)
                return false;

            frame = new XrFrameState { Type = XrStructureType.FrameState };
            var waitInfo = new XrBaseInfo(XrStructureType.FrameWaitInfo);
            Xr.Check(Xr.xrWaitFrame(session, ref waitInfo, ref frame), "xrWaitFrame", XrRuntime.Instance);
            var beginInfo = new XrBaseInfo(XrStructureType.FrameBeginInfo);
            Xr.Check(Xr.xrBeginFrame(session, ref beginInfo), "xrBeginFrame", XrRuntime.Instance);
            InFrame = true;
            ShouldRender = frame.ShouldRender != 0;
            // The controllers at the time the eyes are located for (ReadNow), and for the game at the time the frame it draws
            // from them will be shown (Read): the camera this frame draws was taken on before this, which measures that lead.
            XrInput.Update(frame.PredictedDisplayTime, frame.PredictedDisplayPeriod, Focused);
            if (!ShouldRender)
                return false;

            var locateInfo = new XrViewLocateInfo
            {
                Type = XrStructureType.ViewLocateInfo,
                ViewConfigurationType = XrConst.ViewConfigurationPrimaryStereo,
                DisplayTime = frame.PredictedDisplayTime,
                Space = space,
            };
            var viewState = new XrViewState { Type = XrStructureType.ViewState };
            XrView* located = stackalloc XrView[2];
            located[0] = new XrView { Type = XrStructureType.View };
            located[1] = new XrView { Type = XrStructureType.View };
            Xr.Check(Xr.xrLocateViews(session, ref locateInfo, ref viewState, 2, out uint _, located), "xrLocateViews", XrRuntime.Instance);

            // Tracking lost: keep the last good poses rather than snapping the view to the origin.
            const ulong valid = XrConst.ViewStateOrientationValid | XrConst.ViewStatePositionValid;
            if ((viewState.ViewStateFlags & valid) == valid)
            {
                views[0] = located[0];
                views[1] = located[1];
            }

            var head = new XrSpaceLocation { Type = XrStructureType.SpaceLocation };
            Xr.Check(Xr.xrLocateSpace(viewSpace, space, frame.PredictedDisplayTime, ref head), "xrLocateSpace", XrRuntime.Instance);
            const ulong headValid = XrConst.SpaceLocationOrientationValid | XrConst.SpaceLocationPositionValid;
            if ((head.LocationFlags & headValid) == headValid)
                Head = head.Pose;
            LocateFloor();
            return true;
        }

        /// <summary>The swapchain image to copy this eye into. Pair with <see cref="ReleaseImage"/>.</summary>
        public static Texture2D AcquireImage(int eye) => images[eye][Acquire(swapchains[eye])];

        public static void ReleaseImage(int eye) => Release(swapchains[eye]);

        /// <summary>
        /// The panel swapchain image to copy the GUI into, at the GUI's size (the swapchain is made again when it
        /// changes). Pair with <see cref="ReleasePanelImage"/>.
        /// </summary>
        public static Texture2D AcquirePanelImage(int width, int height)
        {
            if (width != panelWidth || height != panelHeight)
            {
                DestroySwapchain(ref panelSwapchain, ref panelImages);
                panelSwapchain = CreateSwapchain(width, height, out panelImages);
                panelWidth = width;
                panelHeight = height;
                Log.Info($"OpenXR panel swapchain {width}x{height}");
            }
            return panelImages[Acquire(panelSwapchain)];
        }

        public static void ReleasePanelImage() => Release(panelSwapchain);

        /// <summary>The external view's swapchain image to copy the picture into (made again when the size changes). Pair with <see cref="ReleaseScreenImage"/>.</summary>
        public static Texture2D AcquireScreenImage(int width, int height)
        {
            if (width != screenWidth || height != screenHeight)
            {
                DestroySwapchain(ref screenSwapchain, ref screenImages);
                screenSwapchain = CreateSwapchain(width, height, out screenImages);
                screenWidth = width;
                screenHeight = height;
                Log.Info($"OpenXR external view swapchain {width}x{height}");
            }
            return screenImages[Acquire(screenSwapchain)];
        }

        public static void ReleaseScreenImage() => Release(screenSwapchain);

        private static uint Acquire(ulong swapchain)
        {
            var acquire = new XrBaseInfo(XrStructureType.SwapchainImageAcquireInfo);
            Xr.Check(Xr.xrAcquireSwapchainImage(swapchain, ref acquire, out uint index), "xrAcquireSwapchainImage", XrRuntime.Instance);
            var wait = new XrSwapchainImageWaitInfo { Type = XrStructureType.SwapchainImageWaitInfo, Timeout = XrConst.InfiniteDuration };
            Xr.Check(Xr.xrWaitSwapchainImage(swapchain, ref wait), "xrWaitSwapchainImage", XrRuntime.Instance);
            return index;
        }

        private static void Release(ulong swapchain)
        {
            var release = new XrBaseInfo(XrStructureType.SwapchainImageReleaseInfo);
            Xr.Check(Xr.xrReleaseSwapchainImage(swapchain, ref release), "xrReleaseSwapchainImage", XrRuntime.Instance);
        }

        /// <param name="eyesDrawn">Both eye images were filled this frame.</param>
        /// <param name="panel">The panel image was filled this frame; it is shown ahead of the eyes, or where a menu put it.</param>
        /// <param name="screen">The external view's image was filled this frame; it is shown between the eyes and the panel.</param>
        public static void EndFrame(bool eyesDrawn, Panel? panel, Screen? screen = null)
        {
            if (!InFrame)
                return;
            InFrame = false;
            uint layerCount = 0;
            IntPtr* layers = stackalloc IntPtr[3];

            XrCompositionLayerProjectionView* projectionViews = stackalloc XrCompositionLayerProjectionView[2];
            for (int eye = 0; eye < 2; eye++)
            {
                projectionViews[eye] = new XrCompositionLayerProjectionView
                {
                    Type = XrStructureType.CompositionLayerProjectionView,
                    Pose = views[eye].Pose,
                    Fov = views[eye].Fov,
                    SubImage = new XrSwapchainSubImage
                    {
                        Swapchain = swapchains[eye],
                        ImageRect = new XrRect2Di { Width = EyeWidth, Height = EyeHeight },
                    },
                };
            }
            var layer = new XrCompositionLayerProjection
            {
                Type = XrStructureType.CompositionLayerProjection,
                Space = space,
                ViewCount = 2,
                Views = (IntPtr)projectionViews,
            };
            if (eyesDrawn)
                layers[layerCount++] = (IntPtr)(&layer);

            // The external view: an opaque screen in the tracking space, over the scene and under the panel.
            Screen standing = screen ?? default;
            var screenQuad = new XrCompositionLayerQuad
            {
                Type = XrStructureType.CompositionLayerQuad,
                LayerFlags = 0,
                Space = space,
                EyeVisibility = XrEyeVisibility.Both,
                SubImage = new XrSwapchainSubImage
                {
                    Swapchain = screenSwapchain,
                    ImageRect = new XrRect2Di { Width = screenWidth, Height = screenHeight },
                },
                Pose = standing.Pose,
                Size = new XrExtent2Df { Width = standing.Width, Height = standing.Height },
            };
            if (eyesDrawn && screen.HasValue && screenSwapchain != 0)
                layers[layerCount++] = (IntPtr)(&screenQuad);

            // Premultiplied alpha, as the game's sprites are drawn. In the VIEW space it stays ahead of the head; a menu's
            // panel is in the LOCAL space, where it was put.
            Panel shown = panel ?? default;
            var quad = new XrCompositionLayerQuad
            {
                Type = XrStructureType.CompositionLayerQuad,
                LayerFlags = XrConst.LayerBlendTextureSourceAlpha,
                Space = shown.WorldLocked ? space : viewSpace,
                EyeVisibility = XrEyeVisibility.Both,
                SubImage = new XrSwapchainSubImage
                {
                    Swapchain = panelSwapchain,
                    ImageRect = new XrRect2Di { Width = panelWidth, Height = panelHeight },
                },
                Pose = shown.WorldLocked ? shown.Pose : new XrPosef
                {
                    Orientation = new XrQuaternionf { W = 1f },
                    Position = new XrVector3f { Z = -shown.Distance },
                },
                Size = new XrExtent2Df { Width = shown.Width, Height = shown.Height },
            };
            if (panel.HasValue && panelSwapchain != 0)
                layers[layerCount++] = (IntPtr)(&quad);

            var endInfo = new XrFrameEndInfo
            {
                Type = XrStructureType.FrameEndInfo,
                DisplayTime = frame.PredictedDisplayTime,
                EnvironmentBlendMode = XrConst.EnvironmentBlendOpaque,
                LayerCount = layerCount,
                Layers = (IntPtr)layers,
            };
            Xr.Check(Xr.xrEndFrame(session, ref endInfo), "xrEndFrame", XrRuntime.Instance);
        }

        public static void Destroy()
        {
            if (session == 0)
                return;
            Running = false;
            InFrame = false;
            for (int eye = 0; eye < 2; eye++)
                DestroySwapchain(ref swapchains[eye], ref images[eye]);
            DestroySwapchain(ref panelSwapchain, ref panelImages);
            panelWidth = panelHeight = 0;
            DestroySwapchain(ref screenSwapchain, ref screenImages);
            screenWidth = screenHeight = 0;
            XrInput.Destroy();
            if (stageSpace != 0)
                Xr.xrDestroySpace(stageSpace);
            stageSpace = 0;
            OriginAboveFloor = null;
            floorLogged = false;
            if (viewSpace != 0)
                Xr.xrDestroySpace(viewSpace);
            viewSpace = 0;
            if (space != 0)
                Xr.xrDestroySpace(space);
            space = 0;
            Xr.xrDestroySession(session);
            session = 0;
            Log.Info("OpenXR session destroyed");
        }
    }
}
