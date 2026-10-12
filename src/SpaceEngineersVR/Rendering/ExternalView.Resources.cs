// ExternalView, render thread: the picture texture and the pass's own screen-sized renderer resources.

using System;

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
