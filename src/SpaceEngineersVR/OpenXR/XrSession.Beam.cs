// XrSession, laser beam: the quad layer that draws the beam over the GUI panel.

using System;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace SpaceEngineersVR.OpenXR
{
    internal static unsafe partial class XrSession
    {
        private const int BeamTextureWidth = 8, BeamTextureHeight = 64;
        private static ulong beamSwapchain;
        private static Texture2D[] beamImages;
        private static bool beamFailed;

        /// <summary>
        /// Fills <paramref name="quad"/> with a layer for the laser's beam (a thin quad along it, turned about its axis to face the
        /// head), when the laser is on the panel. The tracking space, like the beam's ends. False when there is no beam to show.
        /// </summary>
        private static bool TryBeamLayer(ref XrCompositionLayerQuad quad)
        {
            if (beamFailed || !Input.Laser.TryGetLine(out VRageMath.Vector3 from, out VRageMath.Vector3 to))
                return false;
            try
            {
                if (beamSwapchain == 0 && !MakeBeamTexture())
                    return false;
            }
            catch (Exception e)
            {
                beamFailed = true;
                Log.Error(e, "The beam layer could not be made; the beam stays behind the panel");
                return false;
            }

            if (!BeamPose(new XrVector3f { X = from.X, Y = from.Y, Z = from.Z }, new XrVector3f { X = to.X, Y = to.Y, Z = to.Z },
                    Head.Position, Input.Laser.LineThickness * 2f, out XrPosef pose, out XrExtent2Df size))
                return false;
            quad = new XrCompositionLayerQuad
            {
                Type = XrStructureType.CompositionLayerQuad,
                LayerFlags = XrConst.LayerBlendTextureSourceAlpha,
                Space = space,
                EyeVisibility = XrEyeVisibility.Both,
                SubImage = new XrSwapchainSubImage
                {
                    Swapchain = beamSwapchain,
                    ImageRect = new XrRect2Di { Width = BeamTextureWidth, Height = BeamTextureHeight },
                },
                Pose = pose,
                Size = size,
            };
            return true;
        }

        /// <summary>
        /// A quad along the beam: its middle at the beam's, its Y along the beam and as high as the beam is long, its normal turned
        /// about the beam to face <paramref name="head"/>. The width is twice the beam's thickness: the texture falls off to nothing
        /// at its edges, so half of it is what shows. False for a beam with no length or no finite ends.
        /// </summary>
        internal static bool BeamPose(XrVector3f from, XrVector3f to, XrVector3f head, float width, out XrPosef pose, out XrExtent2Df size)
        {
            pose = XrPosef.Identity;
            size = default;
            double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
            double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (double.IsNaN(length) || double.IsInfinity(length) || length < 1e-3)
                return false;
            double yx = dx / length, yy = dy / length, yz = dz / length;

            double mx = (from.X + to.X) * 0.5, my = (from.Y + to.Y) * 0.5, mz = (from.Z + to.Z) * 0.5;
            // The direction to the head, without its part along the beam.
            double hx = head.X - mx, hy = head.Y - my, hz = head.Z - mz;
            double along = hx * yx + hy * yy + hz * yz;
            hx -= along * yx; hy -= along * yy; hz -= along * yz;
            double hl = Math.Sqrt(hx * hx + hy * hy + hz * hz);
            if (!(hl > 1e-4))
            {
                // The head is on the beam's axis (or not known): any direction across the beam will do.
                if (Math.Abs(yy) < 0.9) { hx = 0; hy = 1; hz = 0; } else { hx = 1; hy = 0; hz = 0; }
                along = hx * yx + hy * yy + hz * yz;
                hx -= along * yx; hy -= along * yy; hz -= along * yz;
                hl = Math.Sqrt(hx * hx + hy * hy + hz * hz);
            }
            double zx = hx / hl, zy = hy / hl, zz = hz / hl;
            // X = Y x Z
            double xx = yy * zz - yz * zy, xy = yz * zx - yx * zz, xz = yx * zy - yy * zx;

            // Rotation matrix with columns X, Y, Z to a quaternion.
            double m00 = xx, m10 = xy, m20 = xz, m01 = yx, m11 = yy, m21 = yz, m02 = zx, m12 = zy, m22 = zz;
            double trace = m00 + m11 + m22, qx, qy, qz, qw;
            if (trace > 0)
            {
                double t = Math.Sqrt(trace + 1) * 2;
                qw = t / 4; qx = (m21 - m12) / t; qy = (m02 - m20) / t; qz = (m10 - m01) / t;
            }
            else if (m00 > m11 && m00 > m22)
            {
                double t = Math.Sqrt(1 + m00 - m11 - m22) * 2;
                qw = (m21 - m12) / t; qx = t / 4; qy = (m01 + m10) / t; qz = (m02 + m20) / t;
            }
            else if (m11 > m22)
            {
                double t = Math.Sqrt(1 + m11 - m00 - m22) * 2;
                qw = (m02 - m20) / t; qx = (m01 + m10) / t; qy = t / 4; qz = (m12 + m21) / t;
            }
            else
            {
                double t = Math.Sqrt(1 + m22 - m00 - m11) * 2;
                qw = (m10 - m01) / t; qx = (m02 + m20) / t; qy = (m12 + m21) / t; qz = t / 4;
            }
            double ql = Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            pose = new XrPosef
            {
                Orientation = new XrQuaternionf { X = (float)(qx / ql), Y = (float)(qy / ql), Z = (float)(qz / ql), W = (float)(qw / ql) },
                Position = new XrVector3f { X = (float)mx, Y = (float)my, Z = (float)mz },
            };
            size = new XrExtent2Df { Width = width, Height = (float)length };
            return true;
        }

        /// <summary>The beam's swapchain, every image filled once with the beam's colour, fading to nothing across the width.</summary>
        private static bool MakeBeamTexture()
        {
            if (session == 0)
                return false;
            ulong swapchain = CreateSwapchain(BeamTextureWidth, BeamTextureHeight, out Texture2D[] textures);
            bool bgra = swapchainFormat == Format.B8G8R8A8_UNorm || swapchainFormat == Format.B8G8R8A8_UNorm_SRgb;
            byte[] pixels = new byte[BeamTextureWidth * BeamTextureHeight * 4];
            VRageMath.Vector4 color = Input.Laser.LineColor;
            for (int x = 0; x < BeamTextureWidth; x++)
            {
                // Across the width: 1 in the middle, 0 at the edges. Premultiplied, as the layer's blend takes it.
                float u = Math.Abs((x + 0.5f) / BeamTextureWidth * 2f - 1f);
                float a = Math.Max(0f, 1f - u) * color.W;
                byte r = (byte)Math.Round(color.X * a * 255f), g = (byte)Math.Round(color.Y * a * 255f), b = (byte)Math.Round(color.Z * a * 255f);
                for (int y = 0; y < BeamTextureHeight; y++)
                {
                    int i = (y * BeamTextureWidth + x) * 4;
                    pixels[i] = bgra ? b : r;
                    pixels[i + 1] = g;
                    pixels[i + 2] = bgra ? r : b;
                    pixels[i + 3] = (byte)Math.Round(a * 255f);
                }
            }

            beamSwapchain = swapchain;
            beamImages = textures;
            // Every image of the chain, so whichever the runtime shows has the beam in it.
            for (int i = 0; i < textures.Length; i++)
            {
                Texture2D image = textures[Acquire(swapchain)];
                try
                {
                    fixed (byte* data = pixels)
                        image.Device.ImmediateContext.UpdateSubresource(new SharpDX.DataBox((IntPtr)data, BeamTextureWidth * 4, 0), image, 0);
                }
                finally
                {
                    Release(swapchain);
                }
            }
            Log.Info($"OpenXR beam swapchain {BeamTextureWidth}x{BeamTextureHeight}, {textures.Length} images");
            return true;
        }
    }
}
