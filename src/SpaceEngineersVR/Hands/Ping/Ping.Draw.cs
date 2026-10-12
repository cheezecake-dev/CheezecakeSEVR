// Ping: the laser and the dot while aiming, and the flash where a ping or mark lands.
using System;
using System.Diagnostics;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    internal static partial class Ping
    {
        /// <summary>The physics is cast every frame while aiming; the voxel geometry every this many.</summary>
        private const int VoxelCastEveryFrames = 10;

        private const float LineThickness = 0.003f;

        /// <summary>How far the laser goes when the ray meets nothing, metres: far enough to read as "out there".</summary>
        private const float MissLength = 40f;

        /// <summary>The dot's size: this share of its distance (so it looks the same size near and far), and never smaller than the minimum.</summary>
        private const double DotShare = 0.006, DotMinimum = 0.015;

        /// <summary>The flash: how long it lasts, and how far it grows from the dot's size.</summary>
        private const double FlashSeconds = 1.2, FlashGrowth = 4.0;

        // Ignoring depth: the dot sits on the surface it marks, and the flash may be far off behind a hill's edge.
        private static readonly MyStringId LineMaterial = MyStringId.GetOrCompute("SquareIgnoreDepth");
        private static readonly MyStringId DotMaterial = MyStringId.GetOrCompute("RedDotIgnoreDepth");

        /// <summary>What the laser is on while aiming (the last cast).</summary>
        private static Target preview;
        private static int castFrame;

        private struct Flash
        {
            public Vector3D Point;
            public Color Color;
            public double Distance, Started;
        }

        private static Flash flash = new Flash { Started = double.NegativeInfinity };
        private static int drawErrors;

        private static double FlashClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>Postfix of MySession.DrawSync, as the hand ray's: billboards added here are drawn in both eyes this frame.</summary>
        private static void Draw()
        {
            if (drawErrors >= 3 || !MyTransparentGeometry.HasCamera || !VRInput.Active)
                return;
            try
            {
                if (aim != Aim.None && preview.Name != null && TryRay(aimContext, out Vector3D origin, out Vector3D direction, out bool fromHand))
                {
                    Vector4 color = preview.Color.ToVector4();
                    double along = preview.Hit ? Math.Max(Vector3D.Dot(preview.Point - origin, direction), 0.05) : MissLength;
                    // The line rides with the hand (MotionExtrapolation.AddLine); from the head it would be a line out of the eye.
                    if (fromHand)
                        MotionExtrapolation.AddLine(LineMaterial, new Vector4(color.X, color.Y, color.Z, 0.6f), origin, (Vector3)direction, (float)along, LineThickness);
                    if (preview.Hit)
                        MyTransparentGeometry.AddPointBillboard(DotMaterial, color, origin + direction * along, (float)Math.Max(along * DotShare, DotMinimum), 0f);
                }

                double age = FlashClock() - flash.Started;
                if (age >= 0d && age < FlashSeconds)
                {
                    double t = age / FlashSeconds;
                    double size = Math.Max(flash.Distance * DotShare, DotMinimum);
                    Vector4 c = flash.Color.ToVector4();
                    float fade = (float)(1d - t);
                    // A ring of light that grows and fades, over a steady dot.
                    MyTransparentGeometry.AddPointBillboard(DotMaterial, new Vector4(c.X, c.Y, c.Z, 1f) * fade, flash.Point, (float)(size * (1d + FlashGrowth * Common.Ease.Smooth01(t))), 0f);
                    MyTransparentGeometry.AddPointBillboard(DotMaterial, c, flash.Point, (float)size, 0f);
                }
            }
            catch (Exception e)
            {
                if (drawErrors++ < 3)
                    Log.Error(e, "Ping: the laser or the flash could not be drawn");
            }
        }
    }
}
