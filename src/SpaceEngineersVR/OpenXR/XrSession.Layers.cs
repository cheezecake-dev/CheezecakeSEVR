// XrSession, frame submission: builds the composition layer list and calls xrEndFrame.

using System;

namespace SpaceEngineersVR.OpenXR
{
    internal static unsafe partial class XrSession
    {
        /// <param name="eyesDrawn">Both eye images were filled this frame.</param>
        /// <param name="panel">The panel image was filled this frame; it is shown ahead of the eyes, or where a menu put it.</param>
        /// <param name="screen">The external view's image was filled this frame; it is shown between the eyes and the panel.</param>
        public static void EndFrame(bool eyesDrawn, Panel? panel, Screen? screen = null)
        {
            if (!InFrame)
                return;
            InFrame = false;
            uint layerCount = 0;
            IntPtr* layers = stackalloc IntPtr[MostLayers];

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
                        ImageRect = new XrRect2Di
                        {
                            Width = eyeRectWidth > 0 ? Math.Min(eyeRectWidth, EyeWidth) : EyeWidth,
                            Height = eyeRectHeight > 0 ? Math.Min(eyeRectHeight, EyeHeight) : EyeHeight,
                        },
                    },
                };
            }
            eyeRectWidth = eyeRectHeight = 0;
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

            // The laser's beam over the panel: the eyes' beam is hidden behind the panel quad, so the beam is a quad layer of its own
            // after it. Its slot comes out of the visor pieces' share, not on top of the runtime's limit.
            int limit = Math.Min(XrRuntime.MaxLayerCount > 0 ? XrRuntime.MaxLayerCount : 16, MostLayers);
            XrCompositionLayerQuad beamQuad = default;
            if (panel.HasValue && panelSwapchain != 0 && (int)layerCount < limit && TryBeamLayer(ref beamQuad))
                layers[layerCount++] = (IntPtr)(&beamQuad);

            // The HUD's pieces on the visor, over the panel: head-locked in the VIEW space like it, premultiplied alpha, each a
            // part of the one visor image. No more than the runtime takes (its maxLayerCount) with the layers above.
            int pieces = panel.HasValue && !shown.WorldLocked && shown.Visor != null && visorSwapchain != 0
                ? Math.Max(0, Math.Min(Math.Min(shown.VisorCount, shown.Visor.Length), limit - (int)layerCount))
                : 0;
            XrCompositionLayerCylinderKHR* cylinders = stackalloc XrCompositionLayerCylinderKHR[Math.Max(pieces, 1)];
            XrCompositionLayerQuad* flats = stackalloc XrCompositionLayerQuad[Math.Max(pieces, 1)];
            for (int i = 0; i < pieces; i++)
            {
                VisorPiece piece = shown.Visor[i];
                var subImage = new XrSwapchainSubImage { Swapchain = visorSwapchain, ImageRect = piece.Rect };
                if (shown.VisorCylinder)
                {
                    // The cylinder's middle is the head; the piece is the part of it straight ahead of Orientation.
                    cylinders[i] = new XrCompositionLayerCylinderKHR
                    {
                        Type = XrStructureType.CompositionLayerCylinderKHR,
                        LayerFlags = XrConst.LayerBlendTextureSourceAlpha,
                        Space = viewSpace,
                        EyeVisibility = XrEyeVisibility.Both,
                        SubImage = subImage,
                        Pose = new XrPosef { Orientation = piece.Orientation },
                        Radius = piece.Radius,
                        CentralAngle = piece.Width / piece.Radius,
                        AspectRatio = piece.Width / piece.Height,
                    };
                    layers[layerCount++] = (IntPtr)(&cylinders[i]);
                }
                else
                {
                    flats[i] = new XrCompositionLayerQuad
                    {
                        Type = XrStructureType.CompositionLayerQuad,
                        LayerFlags = XrConst.LayerBlendTextureSourceAlpha,
                        Space = viewSpace,
                        EyeVisibility = XrEyeVisibility.Both,
                        SubImage = subImage,
                        Pose = new XrPosef { Orientation = piece.Orientation, Position = piece.Middle },
                        Size = new XrExtent2Df { Width = piece.Width, Height = piece.Height },
                    };
                    layers[layerCount++] = (IntPtr)(&flats[i]);
                }
            }

            var endInfo = new XrFrameEndInfo
            {
                Type = XrStructureType.FrameEndInfo,
                DisplayTime = frame.PredictedDisplayTime,
                EnvironmentBlendMode = XrConst.EnvironmentBlendOpaque,
                LayerCount = layerCount,
                Layers = (IntPtr)layers,
            };
            int result;
            dxvk?.LockQueue();
            try
            {
                result = Xr.xrEndFrame(session, ref endInfo);
                if (result < 0 && pieces > 0)
                {
                    // Refused with the visor's pieces in it (they are the last layers): sent again without them, so the headset
                    // keeps its picture; taken without them, the HUD goes back on the panel from the next frame (HudVisor).
                    int refused = result;
                    endInfo.LayerCount = layerCount - (uint)pieces;
                    result = Xr.xrEndFrame(session, ref endInfo);
                    if (result >= 0)
                        VisorRefused = Xr.ResultName(XrRuntime.Instance, refused);
                }
            }
            finally
            {
                dxvk?.ReleaseQueue();
            }
            Xr.Check(result, "xrEndFrame", XrRuntime.Instance);
            Rendering.PerfMeter.Submitted();
        }
    }
}
