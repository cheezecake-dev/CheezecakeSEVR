// ExternalView, render thread: the picture texture and the pass's own screen-sized renderer resources.

using System;
using SpaceEngineersVR.OpenXR;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class ExternalView
    {
        /// <summary>The picture: made the first time, and again after the render size changes nothing about it (it has a size of its own).</summary>
        private static void EnsureTexture()
        {
            if (texture != null)
                return;
            texture = createRtv.Invoke(rwTextures.GetValue(null), new object[]
            {
                "VR external view", TextureWidth, TextureHeight, EyeRenderer.WindowFormat, 1, 0,
                SharpDX.Direct3D11.ResourceOptionFlags.None, SharpDX.Direct3D11.ResourceUsage.Default, 1, SharpDX.Direct3D11.CpuAccessFlags.None,
            });
            EyeRenderer.Clear(texture);
        }

        /// <summary>
        /// The picture's size for this pass. A block's camera on the forced view's screen is the player's whole view, so it is
        /// drawn at about the headset's own pixels across the angle the screen covers (<see cref="ForcedView"/> puts it
        /// <see cref="VRSettings.MenuWidthDegrees"/> wide), at the density the eye images have at their middle, up to
        /// <see cref="LargestWidth"/>; anything else is <see cref="SmallWidth"/> wide. A new size remakes the picture and the
        /// pass's screen set.
        /// </summary>
        private static void FitSize(Kind kind)
        {
            int width = kind == Kind.Forced && ForcedView.ForBlock ? BlockWidth() : SmallWidth;
            if (width == TextureWidth)
                return;
            if (texture != null)
            {
                object[] args = { texture };
                disposeRtv.Invoke(rwTextures.GetValue(null), args);
                texture = null;
            }
            ScreenResourcesChanged();
            haveImage = false;
            Log.Info($"External view: picture {TextureWidth}x{TextureHeight} -> {width}x{width * SmallHeight / SmallWidth}" +
                     (width > SmallWidth ? $" for a block's camera ({VRSettings.MenuWidthDegrees:0} deg wide on the headset)" : ""));
            TextureWidth = width;
            TextureHeight = width * SmallHeight / SmallWidth;
        }

        /// <summary>A block camera's picture width (see <see cref="FitSize"/>), a multiple of 32 so its height is whole; the current width outside a headset frame.</summary>
        private static int BlockWidth()
        {
            if (!XrSession.InFrame)
                return XrSession.Exists ? TextureWidth : SmallWidth;
            XrFovf fov = XrSession.View(0).Fov;
            double across = Math.Tan(fov.AngleRight) - Math.Tan(fov.AngleLeft);
            if (!(across > 0.1))
                return TextureWidth;
            double half = Math.Tan(MathHelper.ToRadians(VRSettings.MenuWidthDegrees) / 2.0);
            double wanted = EyeRenderer.EyeResolution.X / across * 2.0 * half;
            int width = (int)Math.Round(wanted / 32.0) * 32;
            return MathHelper.Clamp(width, SmallWidth, LargestWidth);
        }

        /// <summary>
        /// A GBuffer, the light tiles and the auto-exposure pair at the picture's size, made with the renderer's own
        /// functions (MyGBuffer.Resize, MyLightsRendering.Resize) while the big ones are kept out of their way.
        /// The exposure starts from where the eyes' is, so the picture is not dark for the first second.
        /// </summary>
        private static void EnsureScreenSet()
        {
            if (screenSetBuilt)
                return;

            object big = gbufferMain.GetValue(null);
            object hdr = gbufferHdr.GetValue(null);
            object samples = gbufferSamples.GetValue(big);
            object quality = gbufferQuality.GetValue(big);
            object gbuffer = Activator.CreateInstance(gbufferType, nonPublic: true);
            gbufferResize.Invoke(gbuffer, new[] { TextureWidth, TextureHeight, samples, quality, hdr });

            object[] bigLights = new object[lightFields.Length];
            for (int i = 0; i < bigLights.Length; i++)
                bigLights[i] = lightFields[i].GetValue(null);
            object[] lights = new object[lightFields.Length];
            try
            {
                // Resize disposes what is there first: with nothing there, it builds the small set and leaves the big one alone.
                lightFields[0].SetValue(null, null);
                lightsResize.Invoke(null, new object[] { TextureWidth, TextureHeight });
                for (int i = 0; i < lights.Length; i++)
                    lights[i] = lightFields[i].GetValue(null);
            }
            finally
            {
                for (int i = 0; i < bigLights.Length; i++)
                    lightFields[i].SetValue(null, bigLights[i]);
            }

            Type rtvTexture = autoExposure.FieldType.GetElementType();
            Array exposure = Array.CreateInstance(rtvTexture, 2);
            object bigExposure = ((Array)autoExposure.GetValue(null)).GetValue(0);
            object rc = EyeRenderer.Context;
            for (int i = 0; i < 2; i++)
            {
                object created = createRtv.Invoke(rwTextures.GetValue(null), new object[]
                {
                    "AutoExposureExternal" + i, 1, 1, SharpDX.DXGI.Format.R32G32_Float, 1, 0,
                    SharpDX.Direct3D11.ResourceOptionFlags.None, SharpDX.Direct3D11.ResourceUsage.Default, 1, SharpDX.Direct3D11.CpuAccessFlags.None,
                });
                exposure.SetValue(created, i);
                copyResourceToResource.Invoke(rc, new[] { bigExposure, created });
            }

            smallGBuffer = gbuffer;
            smallLights = lights;
            smallExposure = exposure;
            screenSetBuilt = true;
            screenSetJustBuilt = true;
            Log.Info($"External view: screen resources made at {TextureWidth}x{TextureHeight}");
        }

        /// <summary>The renderer made its screen resources again (a new size, a new anti-aliasing mode): the small set is made again too, next time.</summary>
        private static void ScreenResourcesChanged()
        {
            if (!screenSetBuilt)
                return;
            screenSetBuilt = false;
            try
            {
                gbufferRelease.Invoke(smallGBuffer, null);

                object[] bigLights = new object[lightFields.Length];
                for (int i = 0; i < bigLights.Length; i++)
                    bigLights[i] = lightFields[i].GetValue(null);
                try
                {
                    for (int i = 0; i < smallLights.Length; i++)
                        lightFields[i].SetValue(null, smallLights[i]);
                    lightsDispose.Invoke(null, null);
                }
                finally
                {
                    for (int i = 0; i < bigLights.Length; i++)
                        lightFields[i].SetValue(null, bigLights[i]);
                }

                Array exposure = (Array)smallExposure;
                for (int i = 0; i < exposure.Length; i++)
                {
                    object[] args = { exposure.GetValue(i) };
                    disposeRtv.Invoke(rwTextures.GetValue(null), args);
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "External view: freeing the screen resources failed");
            }
            smallGBuffer = null;
            smallLights = null;
            smallExposure = null;
        }
    }
}
