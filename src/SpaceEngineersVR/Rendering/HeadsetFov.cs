// HeadsetFov: how much the headset shows, for what is fitted to the view.
using System;
using SpaceEngineersVR.OpenXR;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>The headset's field of view this frame, as both eyes see it together, for what is fitted to it (<see cref="HelmetInterior"/>, <see cref="Vignette.DarknessAt"/>).</summary>
    internal static class HeadsetFov
    {
        /// <summary>Taken while the runtime has not given the eyes' views yet, radians each way.</summary>
        private static readonly float AssumedHalfWide = MathHelper.ToRadians(55f), AssumedHalfHigh = MathHelper.ToRadians(50f);

        /// <summary>Half the field of view across and up or down (the larger side of each), radians: the runtime's views this frame, or an assumed headset's before it gives them.</summary>
        public static void Half(out float halfWide, out float halfHigh)
        {
            halfWide = halfHigh = 0f;
            for (int eye = 0; eye < 2; eye++)
            {
                XrFovf fov = XrSession.View(eye).Fov;
                halfWide = Math.Max(halfWide, Math.Max(-fov.AngleLeft, fov.AngleRight));
                halfHigh = Math.Max(halfHigh, Math.Max(fov.AngleUp, -fov.AngleDown));
            }
            if (halfWide <= 0f)
                halfWide = AssumedHalfWide;
            if (halfHigh <= 0f)
                halfHigh = AssumedHalfHigh;
        }
    }
}
