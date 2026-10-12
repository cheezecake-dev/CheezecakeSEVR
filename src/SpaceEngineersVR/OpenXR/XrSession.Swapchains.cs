// XrSession, swapchains: format choice, creation (D3D11 and Vulkan), destruction and image acquire/release.

using System;
using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace SpaceEngineersVR.OpenXR
{
    internal static unsafe partial class XrSession
    {
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
            string offered = string.Join(", ", formats);
            if (dxvk != null)
            {
                // A Vulkan session names VkFormats: compared as their DXGI twins, the formats DXVK's textures over them have.
                offered = "VkFormat " + offered;
                formats = Array.ConvertAll(formats, f => (long)DxgiFormat(f));
            }

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
            throw new InvalidOperationException($"The runtime offers no swapchain format the eyes ({eyeFormat}) can be copied into: " + offered);
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
            if (dxvk != null)
                return CreateVulkanSwapchain(info, out textures);
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

        /// <summary>
        /// A swapchain of a Vulkan session: VkImages, each made into a D3D11 texture by DXVK (a render target in the swapchain's
        /// DXGI format), so the eyes are copied in as on Direct3D 11. Mutable format is asked for, as wineopenxr does: DXVK takes
        /// an sRGB colour image to be viewable as its UNORM twin. The runtime hands the images over and takes them back in
        /// COLOR_ATTACHMENT_OPTIMAL, which is where DXVK returns a render target after each use; no transitions of our own.
        /// </summary>
        private static ulong CreateVulkanSwapchain(XrSwapchainCreateInfo info, out Texture2D[] textures)
        {
            info.Format = runtimeFormat;
            ulong swapchain = 0;
            int result = -1;
            if (!mutableRefused)
            {
                info.UsageFlags |= XrConst.SwapchainUsageMutableFormat;
                result = Xr.xrCreateSwapchain(session, ref info, out swapchain);
                if (result < 0)
                {
                    mutableRefused = true;
                    info.UsageFlags &= ~XrConst.SwapchainUsageMutableFormat;
                    Log.Warn($"The runtime refused a mutable-format swapchain ({Xr.ResultName(XrRuntime.Instance, result)}); asking without it, " +
                             "which is fine for the copies made into it but not for DXVK views in another format");
                }
            }
            if (result < 0)
                Xr.Check(Xr.xrCreateSwapchain(session, ref info, out swapchain), "xrCreateSwapchain", XrRuntime.Instance);

            Xr.Check(Xr.xrEnumerateSwapchainImagesVulkan(swapchain, 0, out uint count, null), "xrEnumerateSwapchainImages", XrRuntime.Instance);
            var native = new XrSwapchainImageVulkanKHR[count];
            for (int i = 0; i < native.Length; i++)
                native[i].Type = XrStructureType.SwapchainImageVulkanKHR;
            fixed (XrSwapchainImageVulkanKHR* ptr = native)
                Xr.Check(Xr.xrEnumerateSwapchainImagesVulkan(swapchain, count, out count, ptr), "xrEnumerateSwapchainImages", XrRuntime.Instance);

            textures = new Texture2D[count];
            try
            {
                for (int i = 0; i < count; i++)
                    textures[i] = new Texture2D(dxvk.CreateTexture((int)info.Width, (int)info.Height, (int)swapchainFormat, native[i].Image));
            }
            catch
            {
                foreach (Texture2D texture in textures)
                    texture?.Dispose();
                textures = null;
                Xr.xrDestroySwapchain(swapchain);
                throw;
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
            {
                // DXVK's textures do not own the runtime's VkImages: its copies into them have to be finished before they go.
                dxvk?.WaitIdle();
                Xr.xrDestroySwapchain(swapchain);
            }
            swapchain = 0;
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

        /// <summary>The visor swapchain image to copy the HUD's pieces into (made again when the size changes). Pair with <see cref="ReleaseVisorImage"/>.</summary>
        public static Texture2D AcquireVisorImage(int width, int height)
        {
            if (width != visorWidth || height != visorHeight)
            {
                DestroySwapchain(ref visorSwapchain, ref visorImages);
                visorSwapchain = CreateSwapchain(width, height, out visorImages);
                visorWidth = width;
                visorHeight = height;
                Log.Info($"OpenXR visor swapchain {width}x{height}");
            }
            return visorImages[Acquire(visorSwapchain)];
        }

        public static void ReleaseVisorImage() => Release(visorSwapchain);

        /// <summary>
        /// On DXVK the runtime may submit to DXVK's queue in xrAcquireSwapchainImage, xrReleaseSwapchainImage, xrBeginFrame and
        /// xrEndFrame (the only calls the spec lets it), so DXVK is kept off the queue around them.
        /// </summary>
        private static uint Acquire(ulong swapchain)
        {
            var acquire = new XrBaseInfo(XrStructureType.SwapchainImageAcquireInfo);
            uint index;
            dxvk?.LockQueue();
            try
            {
                Xr.Check(Xr.xrAcquireSwapchainImage(swapchain, ref acquire, out index), "xrAcquireSwapchainImage", XrRuntime.Instance);
            }
            finally
            {
                dxvk?.ReleaseQueue();
            }
            var wait = new XrSwapchainImageWaitInfo { Type = XrStructureType.SwapchainImageWaitInfo, Timeout = XrConst.InfiniteDuration };
            Xr.Check(Xr.xrWaitSwapchainImage(swapchain, ref wait), "xrWaitSwapchainImage", XrRuntime.Instance);
            return index;
        }

        private static void Release(ulong swapchain)
        {
            var release = new XrBaseInfo(XrStructureType.SwapchainImageReleaseInfo);
            if (dxvk != null)
            {
                // The copy into the image is recorded but not submitted: it goes into the queue first, the runtime's work after it.
                dxvk.FlushRenderingCommands();
                dxvk.LockQueue();
            }
            try
            {
                Xr.Check(Xr.xrReleaseSwapchainImage(swapchain, ref release), "xrReleaseSwapchainImage", XrRuntime.Instance);
            }
            finally
            {
                dxvk?.ReleaseQueue();
            }
        }
    }
}
