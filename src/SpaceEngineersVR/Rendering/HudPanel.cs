using System;
using System.Diagnostics;
using SpaceEngineersVR.OpenXR;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The GUI and HUD as a flat panel in front of the eyes. The game draws all of its 2D - menus, HUD, cursor - in one
    /// sprite pass laid out for one flat screen (<see cref="GuiLayout"/>); drawn straight over the window it spans both
    /// eyes. Instead it goes into a texture of its own, shown as a panel: with a headset as an OpenXR quad layer (the
    /// runtime composites it, so text stays sharp), and in the window drawn into each eye where that eye sees it.
    /// In plain gameplay it is a head-locked visor, <see cref="VRSettings.HudDistance"/> ahead and
    /// <see cref="VRSettings.HudWidthDegrees"/> wide; while a menu is open it stays where it was put when the menu
    /// opened (<see cref="WorldMenu"/>, <see cref="VRSettings.MenuDistance"/>, <see cref="VRSettings.MenuWidthDegrees"/>).
    /// </summary>
    internal static class HudPanel
    {
        /// <summary>Closer to an eye than this (metres, along its view) the panel is cut off when it is drawn in the window.</summary>
        private const float EyeNear = 0.1f;

        private static object texture;
        private static Vector2I size;

        /// <summary>Where the panel can show in the window (an eye's part of it), and where it landed there.</summary>
        private static readonly MyViewport[] areas = new MyViewport[2], inAreas = new MyViewport[2];

        private static readonly Vector3[] corners = new Vector3[4], cut = new Vector3[8];

        /// <summary>A panel shown longer ago than this is no longer there (the GUI stopped drawing, or the headset frame is paused).</summary>
        private const double PlacementFreshSeconds = 0.5;

        private static readonly object placementGate = new object();
        private static Matrix placedPose = Matrix.Identity;
        private static float placedHalfWidth, placedHalfHeight;
        private static long placedAt;

        /// <summary>A panel was drawn this frame and has not been shown yet.</summary>
        public static bool Pending => texture != null;

        /// <summary>
        /// Where the panel was last shown, for pointing at it (<see cref="Input.Laser"/>): its pose in the tracking space
        /// (x right, y up, facing +z, the middle of the panel at the origin) and half its size in metres - the very
        /// numbers the eyes and the headset layer are given. Any thread. False when no panel was shown lately.
        /// </summary>
        public static bool TryGetPlacement(out Matrix pose, out float halfWidth, out float halfHeight)
        {
            lock (placementGate)
            {
                pose = placedPose;
                halfWidth = placedHalfWidth;
                halfHeight = placedHalfHeight;
                return placedAt != 0 && (Stopwatch.GetTimestamp() - placedAt) < PlacementFreshSeconds * Stopwatch.Frequency;
            }
        }

        private static void Place(in Matrix pose, float halfWidth, float halfHeight)
        {
            lock (placementGate)
            {
                placedPose = pose;
                placedHalfWidth = halfWidth;
                placedHalfHeight = halfHeight;
                placedAt = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>The panel to draw this frame's GUI into, cleared to transparent.</summary>
        public static object Begin(Vector2I guiSize)
        {
            if (texture != null)
                EyeRenderer.Release(texture);
            size = guiSize;
            texture = EyeRenderer.Borrow("VR GUI panel", size);
            EyeRenderer.Clear(texture);
            return texture;
        }

        /// <summary>
        /// Shows this frame's panel: over each eye in the window when the eyes were drawn, over the whole window
        /// otherwise (menus, loading: nothing else to show there), and in the headset frame if one is open.
        /// </summary>
        /// <returns>The panel layer for the headset frame, or null when it has none.</returns>
        public static XrSession.Panel? Show(bool eyesDrawn)
        {
            object shown = texture;
            texture = null;
            if (shown == null)
                return null;
            try
            {
                Matrix head = EyeRenderer.Head;
                bool locked = WorldMenu.Locked(head, out Matrix pose, out float fixedWidth);
                float distance = locked ? VRSettings.MenuDistance : VRSettings.HudDistance;
                float widthDegrees = !locked ? VRSettings.HudWidthDegrees : MainMenu.Wanted ? VRSettings.MainMenuWidthDegrees : VRSettings.MenuWidthDegrees;
                // The wrist panel has a size of its own, in metres (WorldMenu gives 0 for the others, which are an angle at a distance).
                float halfWidth = fixedWidth > 0f ? fixedWidth / 2f : distance * (float)Math.Tan(MathHelper.ToRadians(widthDegrees) / 2f);
                float halfHeight = halfWidth * size.Y / size.X;

                // The panel's own space (x right, y up, facing +z) in the head's: ahead of it, or where it was left.
                Matrix inHead = locked ? pose * Matrix.Invert(head) : Matrix.CreateTranslation(0f, 0f, -distance);
                Place(locked ? pose : inHead * head, halfWidth, halfHeight);

                object window = EyeRenderer.Window;
                Vector2I windowSize = EyeRenderer.WindowSize;
                var whole = new MyViewport(windowSize.X, windowSize.Y);
                if (eyesDrawn)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        areas[i] = EyeRenderer.Eyes[i].Window;
                        inAreas[i] = areas[i].Width > 0f ? InWindow(EyeRenderer.Eyes[i], inHead, halfWidth, halfHeight) : default;
                        if (inAreas[i].Width > 0f)
                            BlendWithin(window, shown, inAreas[i], areas[i], whole);
                    }
                }
                else
                {
                    areas[0] = whole;
                    inAreas[0] = Fit(whole);
                    areas[1] = inAreas[1] = default;
                    EyeRenderer.Blend(window, shown, inAreas[0]);
                }
                PanelCursor.Shown(areas, inAreas);

                if (!XrSession.InFrame || !XrSession.ShouldRender)
                    return null;
                SharpDX.Direct3D11.Texture2D image = XrSession.AcquirePanelImage(size.X, size.Y);
                try
                {
                    EyeRenderer.CopyToImage(shown, image);
                }
                finally
                {
                    XrSession.ReleasePanelImage();
                }
                return new XrSession.Panel
                {
                    Distance = distance,
                    Width = 2f * halfWidth,
                    Height = 2f * halfHeight,
                    WorldLocked = locked,
                    Pose = locked ? ToPose(pose) : default,
                };
            }
            finally
            {
                EyeRenderer.Release(shown);
            }
        }

        /// <summary>The largest part of the window with the GUI's shape, in the middle (all of it for a 16:9 window).</summary>
        private static MyViewport Fit(MyViewport window)
        {
            float width = Math.Min(window.Width, window.Height * size.X / size.Y);
            float height = width * size.Y / size.X;
            return new MyViewport((window.Width - width) / 2f, (window.Height - height) / 2f, width, height);
        }

        private static XrPosef ToPose(in Matrix pose)
        {
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(pose);
            Vector3 at = pose.Translation;
            return new XrPosef
            {
                Orientation = new XrQuaternionf { X = rotation.X, Y = rotation.Y, Z = rotation.Z, W = rotation.W },
                Position = new XrVector3f { X = at.X, Y = at.Y, Z = at.Z },
            };
        }

        /// <summary>
        /// Where the panel lands in an eye's part of the window: its corners, from the panel into the eye and through
        /// the eye's projection, as the rectangle around them (the panel is a flat image, not drawn in perspective).
        /// The part of the panel closer than <see cref="EyeNear"/> is cut off first; none of it in front of the eye
        /// gives a rectangle without size. The rectangle may reach past the eye's part of the window.
        /// </summary>
        private static MyViewport InWindow(in EyeRenderer.EyeView eye, in Matrix panelInHead, float halfWidth, float halfHeight)
        {
            Matrix toEye = panelInHead * Matrix.Invert(eye.InHead);
            for (int corner = 0; corner < 4; corner++)
            {
                // In order round the panel, so that consecutive corners are the ends of an edge.
                bool onRight = corner == 1 || corner == 2, onTop = corner >= 2;
                corners[corner] = Vector3.Transform(new Vector3(onRight ? halfWidth : -halfWidth, onTop ? halfHeight : -halfHeight, 0f), toEye);
            }

            int count = 0;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 from = corners[i], to = corners[(i + 1) % corners.Length];
                bool fromIn = from.Z <= -EyeNear, toIn = to.Z <= -EyeNear;
                if (fromIn)
                    cut[count++] = from;
                if (fromIn != toIn)
                    cut[count++] = from + (to - from) * ((-EyeNear - from.Z) / (to.Z - from.Z));
            }

            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                Vector4 clip = Vector4.Transform(new Vector4(cut[i], 1f), eye.Projection);
                if (clip.W <= 1e-4f)
                    return default;
                float x = eye.Window.OffsetX + (clip.X / clip.W + 1f) * 0.5f * eye.Window.Width;
                float y = eye.Window.OffsetY + (1f - clip.Y / clip.W) * 0.5f * eye.Window.Height;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
            // Nothing in front of the eye, or so close that the rectangle is beyond what a viewport can be.
            if (count == 0 || !(right - left < 16000f) || !(bottom - top < 16000f))
                return default;
            return new MyViewport(left, top, right - left, bottom - top);
        }

        /// <summary>
        /// Draws the panel at <paramref name="rect"/> in the window, but not past <paramref name="area"/>, the eye's
        /// part of it. A viewport draws wherever it is, so a panel reaching across the border to the other eye would be
        /// drawn over that eye too: then it goes into a layer the size of the eye first, and only that is laid on the
        /// window. Past the edge of the window itself nothing is drawn anyway.
        /// </summary>
        private static void BlendWithin(object window, object panel, MyViewport rect, MyViewport area, MyViewport whole)
        {
            float left = Math.Max(area.OffsetX, 0f), top = Math.Max(area.OffsetY, 0f);
            float right = Math.Min(area.OffsetX + area.Width, whole.Width), bottom = Math.Min(area.OffsetY + area.Height, whole.Height);
            if (right <= left || bottom <= top)
                return;

            const float slack = 0.5f;
            bool spills = left > slack && rect.OffsetX < left - slack
                          || right < whole.Width - slack && rect.OffsetX + rect.Width > right + slack
                          || top > slack && rect.OffsetY < top - slack
                          || bottom < whole.Height - slack && rect.OffsetY + rect.Height > bottom + slack;
            if (!spills)
            {
                EyeRenderer.Blend(window, panel, rect);
                return;
            }

            if (rect.OffsetX >= right || rect.OffsetX + rect.Width <= left || rect.OffsetY >= bottom || rect.OffsetY + rect.Height <= top)
                return;
            var layerSize = new Vector2I((int)Math.Ceiling(right - left), (int)Math.Ceiling(bottom - top));
            object layer = EyeRenderer.Borrow("VR GUI clip", layerSize);
            try
            {
                EyeRenderer.Clear(layer);
                EyeRenderer.Blend(layer, panel, new MyViewport(rect.OffsetX - left, rect.OffsetY - top, rect.Width, rect.Height));
                EyeRenderer.Blend(window, layer, new MyViewport(left, top, layerSize.X, layerSize.Y));
            }
            finally
            {
                EyeRenderer.Release(layer);
            }
        }
    }
}
