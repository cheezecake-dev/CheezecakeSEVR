// XrSession, Vulkan path: the session on DXVK's Vulkan device, format mapping, and Meta runtime error logging.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SharpDX.DXGI;

namespace SpaceEngineersVR.OpenXR
{
    internal static unsafe partial class XrSession
    {
        /// <summary>
        /// Meta's runtime keeps why a call failed in its own last error (ovr_GetLastErrorInfo in LibOVRRTImpl64_1.dll, which its
        /// OpenXR runtime loads into the game); the OpenXR result alone does not say which of its checks failed. Same thread. Windows only: kernel32 loads the library, which exists only with Meta's
        /// runtime, and the call site is not built on Linux.
        /// </summary>
        private static void LogMetaError()
        {
            IntPtr module = GetModuleHandle("LibOVRRTImpl64_1.dll");
            IntPtr call = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "ovr_GetLastErrorInfo");
            if (call == IntPtr.Zero)
            {
                Log.Info("Meta's runtime: no ovr_GetLastErrorInfo in the process (not Meta's runtime?)");
                return;
            }
            var error = new OvrErrorInfo();
            Marshal.GetDelegateForFunctionPointer<GetLastErrorInfo>(call)(&error);
            Log.Info($"Meta's runtime: last error {error.Result}: {Marshal.PtrToStringAnsi((IntPtr)error.ErrorString)}");
        }

        private struct OvrErrorInfo
        {
            public int Result;
            public fixed byte ErrorString[512];
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GetLastErrorInfo(OvrErrorInfo* error);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>
        /// The session on DXVK's Vulkan device: its instance, device and graphics queue, on the physical device the runtime
        /// names for that instance (HeadsetGpu put the game there). Throws when DXVK is on another one.
        /// </summary>
        private static void CreateVulkanSession()
        {
            dxvk.GetVulkanHandles(out IntPtr vkInstance, out IntPtr vkPhysicalDevice, out IntPtr vkDevice);
            dxvk.GetSubmissionQueue(out IntPtr _, out uint queueIndex, out uint queueFamily);
            LogVulkanExtensions(); // first: a runtime that fails on DXVK's instance usually fails for one of these
            IntPtr wanted = XrRuntime.VulkanGraphicsDevice(vkInstance);
            if (wanted != vkPhysicalDevice)
                throw new InvalidOperationException($"The headset needs Vulkan physical device {wanted.ToInt64():X} but DXVK draws on {vkPhysicalDevice.ToInt64():X}");

            var binding = new XrGraphicsBindingVulkanKHR
            {
                Type = XrStructureType.GraphicsBindingVulkanKHR,
                Instance = vkInstance,
                PhysicalDevice = vkPhysicalDevice,
                Device = vkDevice,
                QueueFamilyIndex = queueFamily,
                QueueIndex = queueIndex,
            };
            var info = new XrSessionCreateInfo { Type = XrStructureType.SessionCreateInfo, Next = (IntPtr)(&binding), SystemId = XrRuntime.SystemId };
            int created = Xr.xrCreateSession(XrRuntime.Instance, ref info, out session);
#if !LINUX
            if (created < 0)
                LogMetaError(); // Windows only: Meta's runtime
#endif
            Xr.Check(created, "xrCreateSession (Vulkan)", XrRuntime.Instance);
            Log.Info($"OpenXR session on DXVK's Vulkan device: physical device {vkPhysicalDevice.ToInt64():X} (the runtime's), " +
                     $"queue family {queueFamily} index {queueIndex}");
        }

        /// <summary>
        /// The Vulkan extensions the runtime needs on the instance and device, against those DXVK enabled (from its log). Missing
        /// ones are a warning: the runtime may fail on them, and on Windows nothing can make DXVK add them (its OpenXR provider
        /// only asks Proton's wineopenxr). Never throws.
        /// </summary>
        private static void LogVulkanExtensions()
        {
            try
            {
                string[] instanceNeeded = XrRuntime.VulkanRequiredExtensions(false);
                string[] deviceNeeded = XrRuntime.VulkanRequiredExtensions(true);
                if (instanceNeeded == null || deviceNeeded == null)
                {
                    Log.Info($"Vulkan extensions the runtime needs: not known ({XrRuntime.VulkanExtension} has no query for them)");
                    return;
                }
                string needed = $"Vulkan extensions the runtime needs: instance [{string.Join(" ", instanceNeeded)}], device [{string.Join(" ", deviceNeeded)}]";
                if (!Rendering.DxvkInterop.EnabledExtensions(out HashSet<string> instanceHas, out HashSet<string> deviceHas, out string source))
                {
#if LINUX
                    // DXVK Native writes its log to stderr unless DXVK_LOG_PATH is set, so this is the usual case there;
                    // XrRuntime.VulkanInstanceProblem has already refused a DXVK that lacks what the runtime needs.
                    Log.Info($"{needed}; which DXVK enabled is not known ({source})");
#else
                    Log.Warn($"{needed}; which DXVK enabled is not known ({source})");
#endif
                    return;
                }

                string[] lacking = instanceNeeded.Where(e => !instanceHas.Contains(e)).Concat(deviceNeeded.Where(e => !deviceHas.Contains(e))).ToArray();
                string[] missing = lacking.Where(e => !Rendering.DxvkInterop.CoreUpTo13.Contains(e)).ToArray();
                string[] core = lacking.Where(e => Rendering.DxvkInterop.CoreUpTo13.Contains(e)).ToArray();
                string text = $"{needed}; DXVK enabled {instanceHas.Count} instance and {deviceHas.Count} device extensions ({source})" +
                              (core.Length > 0 ? $"; in core Vulkan 1.3 rather than by name: {string.Join(" ", core)}" : "");
                if (missing.Length == 0)
                    Log.Info(text + "; none missing");
                else
                    Log.Warn(text + $"; MISSING: {string.Join(" ", missing)} - the runtime may fail without them");
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not compare the runtime's Vulkan extensions with DXVK's");
            }
        }

        /// <summary>The DXGI formats the eyes can be copied into and their Vulkan twins (VkFormat), for a session on DXVK.</summary>
        private static readonly KeyValuePair<Format, long>[] VulkanFormats =
        {
            new KeyValuePair<Format, long>(Format.R8G8B8A8_UNorm, 37), // VK_FORMAT_R8G8B8A8_UNORM
            new KeyValuePair<Format, long>(Format.R8G8B8A8_UNorm_SRgb, 43), // VK_FORMAT_R8G8B8A8_SRGB
            new KeyValuePair<Format, long>(Format.B8G8R8A8_UNorm, 44), // VK_FORMAT_B8G8R8A8_UNORM
            new KeyValuePair<Format, long>(Format.B8G8R8A8_UNorm_SRgb, 50), // VK_FORMAT_B8G8R8A8_SRGB
            new KeyValuePair<Format, long>(Format.R10G10B10A2_UNorm, 64), // VK_FORMAT_A2B10G10R10_UNORM_PACK32
            new KeyValuePair<Format, long>(Format.R16G16B16A16_Float, 97), // VK_FORMAT_R16G16B16A16_SFLOAT
            new KeyValuePair<Format, long>(Format.R11G11B10_Float, 122), // VK_FORMAT_B10G11R11_UFLOAT_PACK32
        };

        private static long VulkanFormat(Format format)
        {
            foreach (KeyValuePair<Format, long> pair in VulkanFormats)
                if (pair.Key == format)
                    return pair.Value;
            throw new InvalidOperationException($"No Vulkan format for {format}");
        }

        /// <summary>The DXGI format of a VkFormat the runtime offers; Unknown for one the eyes cannot be copied into.</summary>
        private static Format DxgiFormat(long vkFormat)
        {
            foreach (KeyValuePair<Format, long> pair in VulkanFormats)
                if (pair.Value == vkFormat)
                    return pair.Key;
            return Format.Unknown;
        }
    }
}
