using VRageMath;

namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// Where a hand's aim ray meets the GUI panel, as a pixel of the GUI. Only geometry, no game state: the panel and
    /// the hand are given in the same space (the tracking space), so it can be checked on its own.
    /// </summary>
    internal static class LaserRay
    {
        internal struct Hit
        {
            /// <summary>The ray reaches the panel's plane from its front. Nothing else in here means anything without it.</summary>
            public bool Valid;

            /// <summary>The ray is within the panel's edges. When it is not, <see cref="Pixel"/> and <see cref="Point"/> are the nearest edge.</summary>
            public bool Inside;

            /// <summary>GUI pixels, x right and y down from the top left, the panel's edges at 0 and size - 1 (as <see cref="Rendering.PanelCursor"/> maps the mouse).</summary>
            public Vector2 Pixel;

            /// <summary>The point on the panel, in the tracking space, metres.</summary>
            public Vector3 Point;

            /// <summary>From the hand to <see cref="Point"/>, metres.</summary>
            public float Distance;
        }

        /// <param name="aim">The hand's aim pose in the tracking space; it points along -Z.</param>
        /// <param name="panel">The panel's pose in the same space: x right, y up, facing +z, its middle at the origin.</param>
        /// <param name="halfWidth">Half the panel's width, metres.</param>
        /// <param name="halfHeight">Half the panel's height, metres.</param>
        /// <param name="guiSize">The GUI's size in pixels.</param>
        public static Hit Cast(in Matrix aim, in Matrix panel, float halfWidth, float halfHeight, Vector2 guiSize)
        {
            var hit = new Hit();
            if (!(halfWidth > 0f) || !(halfHeight > 0f))
                return hit;

            // The ray in the panel's own space, where the panel is the plane z = 0 and the viewer is at +z.
            Matrix toPanel = Matrix.Invert(panel);
            Vector3 origin = Vector3.Transform(aim.Translation, toPanel);
            Vector3 direction = Vector3.TransformNormal(aim.Forward, toPanel);
            float length = direction.Length();
            if (!(length > 1e-6f))
                return hit;
            direction /= length;

            // From the front only, and towards it.
            if (!(origin.Z > 0f) || !(direction.Z < -1e-4f))
                return hit;
            float t = -origin.Z / direction.Z;
            Vector3 at = origin + direction * t;

            float u = (at.X + halfWidth) / (2f * halfWidth);
            float v = (halfHeight - at.Y) / (2f * halfHeight); // v runs down from the panel's top edge, as the GUI's y does
            hit.Valid = true;
            hit.Inside = u >= 0f && u <= 1f && v >= 0f && v <= 1f;
            hit.Pixel = new Vector2(MathHelper.Clamp(u, 0f, 1f) * (guiSize.X - 1f), MathHelper.Clamp(v, 0f, 1f) * (guiSize.Y - 1f));
            hit.Point = Vector3.Transform(
                new Vector3(MathHelper.Clamp(at.X, -halfWidth, halfWidth), MathHelper.Clamp(at.Y, -halfHeight, halfHeight), 0f), panel);
            hit.Distance = Vector3.Distance(aim.Translation, hit.Point);
            return hit;
        }
    }
}
