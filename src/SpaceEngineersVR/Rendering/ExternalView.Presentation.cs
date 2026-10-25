// ExternalView, render thread: shows the finished picture in the desktop window or as a screen layer in the headset.

using System;
using SpaceEngineersVR.OpenXR;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class ExternalView
    {
        /// <summary>
        /// The desktop without a headset: the picture over the lower left of each eye's part of the window (a quarter of its width at the
        /// screen's first size, in proportion to <see cref="VRSettings.ExternalViewSize"/>), after the eyes were copied into it. With a
        /// headset the window shows the screen where it stands, as the headset does (<see cref="MirrorLayers"/>).
        /// </summary>
        public static void ShowInWindow(object window, Vector2I windowSize)
        {
            if (frameKind == Kind.Forced)
            {
                if (haveImage && drawnKind == frameKind)
                    ShowStandingInWindow(window, windowSize, ForcedView.Pose, ForcedView.Width);
                return;
            }
            // In a headset the dash screen is in the eyes' own picture (PrepareScreen), which the window shows.
            if (!Showing || worldPrepared)
                return;
            for (int i = 0; i < 2; i++)
            {
                MyViewport area = EyeRenderer.Eyes[i].Window;
                if (area.Width <= 0f)
                    continue;
                float left = Math.Max(area.OffsetX, 0f), right = Math.Min(area.OffsetX + area.Width, windowSize.X);
                float top = Math.Max(area.OffsetY, 0f), bottom = Math.Min(area.OffsetY + area.Height, windowSize.Y);
                if (right - left < 16f || bottom - top < 16f)
                    continue;
                // A quarter of the eye's width at the screen's first size, and larger or smaller with it (the wrist panel's View size).
                float share = MathHelper.Clamp(0.25f * VRSettings.ExternalViewSize / VRSettings.ExternalViewSizeDefault, 0.1f, 0.5f);
                float width = (right - left) * share, height = width * TextureHeight / TextureWidth;
                float margin = (right - left) * 0.02f;
                EyeRenderer.CopyOpaque(window, texture, new MyViewport(left + margin, bottom - margin - height, width, height));
            }
        }

        /// <summary>
        /// The desktop: a screen standing in the tracking space (the forced view's) over each eye's part of the
        /// window where it stands in that eye, as the rectangle round its corners (<see cref="HudPanel.InWindow"/>: the window
        /// is a mirror, not drawn in perspective). One that would reach over into the other eye's half is left out of that eye.
        /// </summary>
        private static void ShowStandingInWindow(object window, Vector2I windowSize, in Matrix pose, float width)
        {
            Matrix inHead = pose * Matrix.Invert(EyeRenderer.Head);
            float halfWidth = width / 2f, halfHeight = halfWidth * TextureHeight / TextureWidth;
            for (int i = 0; i < 2; i++)
            {
                MyViewport area = EyeRenderer.Eyes[i].Window;
                if (area.Width <= 0f)
                    continue;
                MyViewport rect = HudPanel.InWindow(EyeRenderer.Eyes[i], inHead, halfWidth, halfHeight);
                if (rect.Width < 4f || rect.Height < 4f)
                    continue;
                float left = Math.Max(area.OffsetX, 0f), right = Math.Min(area.OffsetX + area.Width, windowSize.X);
                bool spills = left > 0.5f && rect.OffsetX < left - 0.5f
                              || right < windowSize.X - 0.5f && rect.OffsetX + rect.Width > right + 0.5f;
                if (!spills)
                    EyeRenderer.CopyOpaque(window, texture, rect);
            }
        }

        /// <summary>
        /// The headset, after the pass: the forced view's picture into the screen layer's swapchain, to be submitted with this
        /// frame (<see cref="TakeLayer"/>); the dash screen's into its texture in the world (<see cref="PrepareScreen"/>).
        /// </summary>
        /// <param name="head">The head in the tracking space.</param>
        public static void ShowInHeadset(in Matrix head)
        {
            pending = null;
            if (frameKind == Kind.Forced)
            {
                // The forced view stands in the screen's place; the dash screen keeps its own for after.
                // Neither can be taken hold of (Hands.ViewScreenGrab lets go of a screen that is not shown).
                lock (screenGate)
                    shown = false;
                if (!haveImage || drawnKind != frameKind || !XrSession.InFrame || !XrSession.ShouldRender)
                    return;
                try
                {
                    Submit(ForcedView.Pose, ForcedView.Width);
                }
                catch (Exception e)
                {
                    available = false;
                    Log.Error(e, "External view: showing the forced view in the headset failed; the screen is off for the rest of the run");
                }
                return;
            }
            // The dash screen stands in the world (PrepareScreen, AddToEye): its new picture goes into its texture.
            if (!Showing)
                return;
            try
            {
                CopyWorldPicture();
            }
            catch (Exception e)
            {
                available = false;
                Log.Error(e, "External view: showing it in the headset failed; the screen is off for the rest of the run");
            }
        }

        /// <summary>Copies the picture into the screen's swapchain, to be submitted with this frame standing at <paramref name="pose"/>, <paramref name="width"/> metres wide.</summary>
        private static void Submit(in Matrix pose, float width)
        {
            SharpDX.Direct3D11.Texture2D image = XrSession.AcquireScreenImage(TextureWidth, TextureHeight);
            try
            {
                // Outside the helmet: seen through the visor. A window on the world, so it goes dark with the world in a camera cut
                // and as the comfort vignette closes over where it stands (menus and the HUD are not, so they stay readable).
                Vector3 inHead = Vector3.Transform(pose.Translation, Matrix.Invert(EyeRenderer.Head));
                float light = (1f - CameraCut.Blackness) * (1f - Vignette.DarknessAt(inHead));
                VisorGlass.CopyThrough(texture, new Vector2I(TextureWidth, TextureHeight), image, light);
                MirrorLayers.Screen(texture, new Vector2I(TextureWidth, TextureHeight), pose, width, width * TextureHeight / TextureWidth,
                    VisorGlass.Behind * MathHelper.Clamp(light, 0f, 1f));
            }
            finally
            {
                XrSession.ReleaseScreenImage();
            }
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(pose);
            Vector3 position = pose.Translation;
            pending = new XrSession.Screen
            {
                Width = width,
                Height = width * TextureHeight / TextureWidth,
                Pose = new XrPosef
                {
                    Orientation = new XrQuaternionf { X = rotation.X, Y = rotation.Y, Z = rotation.Z, W = rotation.W },
                    Position = new XrVector3f { X = position.X, Y = position.Y, Z = position.Z },
                },
            };
        }

        /// <summary>The screen to submit with this headset frame, if there is one.</summary>
        public static XrSession.Screen? TakeLayer()
        {
            XrSession.Screen? layer = pending;
            pending = null;
            return layer;
        }

        /// <summary>
        /// The dash: <see cref="DashOffset"/> from the head along the horizon (the way it faces now), tilted to face
        /// the head. Panels in the tracking space face +z; <see cref="Matrix.CreateWorld"/> points -z along its forward.
        /// </summary>
        private static Matrix Place(in Matrix head)
        {
            Vector3 centre = Vector3.Transform(DashOffset, Heading(head));
            return Matrix.CreateWorld(centre, Vector3.Normalize(centre - head.Translation), Vector3.Up);
        }

        /// <summary>The head's place and its heading along the horizon (the way it faces now), level: what the screen's place is kept from.</summary>
        private static Matrix Heading(in Matrix head)
        {
            Vector3 forward = head.Forward;
            Vector3 along = new Vector3(forward.X, 0f, forward.Z);
            if (along.LengthSquared() < 0.04f)
            {
                Vector3 up = head.Up;
                along = forward.Y < 0f ? new Vector3(up.X, 0f, up.Z) : new Vector3(-up.X, 0f, -up.Z);
            }
            if (along.LengthSquared() < 1e-6f)
                along = Vector3.Forward;
            along.Normalize();
            return Matrix.CreateWorld(head.Translation, along, Vector3.Up);
        }
    }
}
