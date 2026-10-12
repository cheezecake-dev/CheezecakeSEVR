using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// DXVK's Vulkan interop (its dxgi_interfaces.h): when the game's Direct3D 11 is DXVK (Linux, or DXVK's d3d11.dll and
    /// dxgi.dll beside the game), the headset session runs on DXVK's own Vulkan instance, device and queue, and the runtime's
    /// Vulkan swapchain images are made into D3D11 textures the eyes are copied into, the way Proton's wineopenxr does it.
    /// COM calls by vtable slot. DXVK submits to the queue from its own thread: anyone else submitting to it (the runtime, in
    /// xrBeginFrame, xrEndFrame, xrAcquireSwapchainImage and xrReleaseSwapchainImage) holds <see cref="LockQueue"/>, and
    /// D3D11 must not be called from the locking thread while it is held.
    /// </summary>
    internal sealed unsafe class DxvkInterop : IDisposable
    {
        // IDXGIVkInteropDevice1 : IDXGIVkInteropDevice : IUnknown. Slots: QueryInterface, AddRef, Release, GetVulkanHandles,
        // GetSubmissionQueue, TransitionSurfaceLayout, FlushRenderingCommands, LockSubmissionQueue, ReleaseSubmissionQueue,
        // GetSubmissionQueue1, CreateTexture2DFromVkImage.
        private const string InteropDevice1 = "e2ef5fa5-dc21-4af7-90c4-f67ef6a09324";
        private const string InteropFactory = "4c5e1b0d-b0c8-4131-bfd8-9b2476f7f408";
        private const string InteropAdapter = "3a6d8f2c-b0e8-4ab4-b4dc-4fd24891bfa5";
        private const string DxgiFactory1 = "770aae78-f26f-4dba-a829-253c83d1b387";

        private const int SlotGetVulkanHandles = 3;
        private const int SlotFlushRenderingCommands = 6;
        private const int SlotLockSubmissionQueue = 7;
        private const int SlotReleaseSubmissionQueue = 8;
        private const int SlotGetSubmissionQueue1 = 9;
        private const int SlotCreateTexture2DFromVkImage = 10;

        /// <summary>D3D11_BIND_RENDER_TARGET: the image is a colour attachment, which DXVK keeps in COLOR_ATTACHMENT_OPTIMAL between uses, the layout the runtime hands it over and takes it back in.</summary>
        private const uint BindRenderTarget = 0x20;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetVulkanHandlesFn(IntPtr self, out IntPtr instance, out IntPtr physicalDevice, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetSubmissionQueue1Fn(IntPtr self, out IntPtr queue, out uint queueIndex, out uint queueFamilyIndex);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CallFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture2DFromVkImageFn(IntPtr self, ref Texture2DDesc1 desc, ulong vkImage, out IntPtr texture);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetAdapterVulkanHandlesFn(IntPtr self, out IntPtr instance, out IntPtr physicalDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetVulkanInstanceFn(IntPtr self, out IntPtr instance, out IntPtr getInstanceProcAddr);

        /// <summary>D3D11_TEXTURE2D_DESC1.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Texture2DDesc1
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CpuAccessFlags, MiscFlags;
            public int TextureLayout;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

#if LINUX
        private const string VulkanLibrary = "libvulkan.so.1";
#else
        private const string VulkanLibrary = "vulkan-1";
#endif

        [DllImport(VulkanLibrary)]
        private static extern int vkQueueWaitIdle(IntPtr queue);

        private IntPtr pointer;
        private readonly GetVulkanHandlesFn getVulkanHandles;
        private readonly GetSubmissionQueue1Fn getSubmissionQueue1;
        private readonly CallFn flushRenderingCommands, lockSubmissionQueue, releaseSubmissionQueue;
        private readonly CreateTexture2DFromVkImageFn createTexture2DFromVkImage;
        private bool waitIdleMissing;

        private DxvkInterop(IntPtr pointer)
        {
            this.pointer = pointer;
            getVulkanHandles = Method<GetVulkanHandlesFn>(pointer, SlotGetVulkanHandles);
            flushRenderingCommands = Method<CallFn>(pointer, SlotFlushRenderingCommands);
            lockSubmissionQueue = Method<CallFn>(pointer, SlotLockSubmissionQueue);
            releaseSubmissionQueue = Method<CallFn>(pointer, SlotReleaseSubmissionQueue);
            getSubmissionQueue1 = Method<GetSubmissionQueue1Fn>(pointer, SlotGetSubmissionQueue1);
            createTexture2DFromVkImage = Method<CreateTexture2DFromVkImageFn>(pointer, SlotCreateTexture2DFromVkImage);
        }

        private static T Method<T>(IntPtr com, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>((*(IntPtr**)com)[slot]);

        /// <summary>The interop of the game's D3D11 device when it is DXVK's (it answers IDXGIVkInteropDevice1); null otherwise.</summary>
        public static DxvkInterop FromDevice(IntPtr d3d11Device)
        {
            var iid = new Guid(InteropDevice1);
            if (d3d11Device == IntPtr.Zero || Marshal.QueryInterface(d3d11Device, ref iid, out IntPtr interop) < 0 || interop == IntPtr.Zero)
                return null;
            return new DxvkInterop(interop);
        }

        /// <summary>
        /// Whether the process's DXGI is DXVK: a factory from the dxgi.dll the game will load answers IDXGIVkInteropFactory.
        /// Asked before the game makes its device, to choose the OpenXR graphics extension. <paramref name="module"/> is the
        /// loaded dxgi.dll's path, for the log. False, never a throw, when DXGI cannot be loaded at all.
        /// </summary>
        public static bool DxgiIsDxvk(out string module)
        {
#if LINUX
            // On Linux DXGI is always DXVK Native (linux-compat's libdxvk_dxgi.so). No factory is made to ask: one made
            // this early loads DXVK before linux-compat has loaded its SDL3, DXVK's window system then binds another
            // SDL3 (the distro's), and the game's window is "Invalid window" to it, so the swap chain fails.
            module = "libdxvk_dxgi.so";
            return true;
#else
            module = null;
            try
            {
                var iid = new Guid(DxgiFactory1);
                if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0 || factory == IntPtr.Zero)
                    return false;
                module = ModulePath("dxgi.dll");
                try
                {
                    return FactoryVulkanInstance(factory) != IntPtr.Zero;
                }
                finally
                {
                    Marshal.Release(factory);
                }
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                module = e.GetType().Name;
                return false;
            }
#endif
        }

        /// <summary>DXVK's VkInstance behind a DXGI factory (IDXGIVkInteropFactory); zero when the factory is not DXVK's.</summary>
        public static IntPtr FactoryVulkanInstance(IntPtr dxgiFactory)
        {
            var iid = new Guid(InteropFactory);
            if (Marshal.QueryInterface(dxgiFactory, ref iid, out IntPtr interop) < 0 || interop == IntPtr.Zero)
                return IntPtr.Zero;
            try
            {
                Method<GetVulkanInstanceFn>(interop, 3)(interop, out IntPtr instance, out IntPtr _);
                return instance;
            }
            finally
            {
                Marshal.Release(interop);
            }
        }

        /// <summary>The VkPhysicalDevice behind a DXGI adapter of DXVK's (IDXGIVkInteropAdapter); zero when it is not DXVK's.</summary>
        public static IntPtr AdapterPhysicalDevice(IntPtr dxgiAdapter)
        {
            var iid = new Guid(InteropAdapter);
            if (Marshal.QueryInterface(dxgiAdapter, ref iid, out IntPtr interop) < 0 || interop == IntPtr.Zero)
                return IntPtr.Zero;
            try
            {
                Method<GetAdapterVulkanHandlesFn>(interop, 3)(interop, out IntPtr _, out IntPtr physicalDevice);
                return physicalDevice;
            }
            finally
            {
                Marshal.Release(interop);
            }
        }

        public void GetVulkanHandles(out IntPtr instance, out IntPtr physicalDevice, out IntPtr device) =>
            getVulkanHandles(pointer, out instance, out physicalDevice, out device);

        /// <summary>DXVK's graphics queue, the one it submits the game's frames to, and its index in its family.</summary>
        public void GetSubmissionQueue(out IntPtr queue, out uint queueIndex, out uint queueFamilyIndex) =>
            getSubmissionQueue1(pointer, out queue, out queueIndex, out queueFamilyIndex);

        /// <summary>
        /// Submits everything the immediate context has recorded and waits until DXVK's command thread has passed it on: a
        /// copy into a swapchain image is in the queue before the runtime takes the image. A D3D11 call: before <see cref="LockQueue"/>.
        /// </summary>
        public void FlushRenderingCommands() => flushRenderingCommands(pointer);

        /// <summary>Waits for DXVK's submissions so far, then keeps it off the queue until <see cref="ReleaseQueue"/>.</summary>
        public void LockQueue() => lockSubmissionQueue(pointer);

        public void ReleaseQueue() => releaseSubmissionQueue(pointer);

        /// <summary>
        /// A D3D11 texture over one of the runtime's VkImages: <paramref name="format"/> is the DXGI format whose Vulkan format
        /// the image has. A render target and nothing else, so DXVK returns it to COLOR_ATTACHMENT_OPTIMAL after every use. The
        /// texture holds one reference, which its wrapper's Dispose releases; the VkImage stays the runtime's.
        /// </summary>
        public IntPtr CreateTexture(int width, int height, int format, ulong vkImage)
        {
            var desc = new Texture2DDesc1
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleCount = 1,
                BindFlags = BindRenderTarget,
            };
            int result = createTexture2DFromVkImage(pointer, ref desc, vkImage, out IntPtr texture);
            if (result < 0 || texture == IntPtr.Zero)
                throw new InvalidOperationException($"DXVK could not make a texture of VkImage {vkImage:X} ({width}x{height}, DXGI format {format}): HRESULT {result:X8}");
            return texture;
        }

        /// <summary>
        /// Waits until the GPU has finished everything submitted to DXVK's queue, before the runtime destroys VkImages that DXVK's
        /// copies may still be writing: its textures over them do not own them. A stall, for swapchain changes only.
        /// </summary>
        public void WaitIdle()
        {
            if (waitIdleMissing)
                return;
            FlushRenderingCommands();
            GetSubmissionQueue(out IntPtr queue, out uint _, out uint _);
            LockQueue();
            try
            {
                vkQueueWaitIdle(queue);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                waitIdleMissing = true;
                Log.Warn($"vkQueueWaitIdle is not available ({e.GetType().Name}); swapchains are destroyed without waiting for the GPU");
            }
            finally
            {
                ReleaseQueue();
            }
        }

        /// <summary>
        /// The Vulkan extensions DXVK enabled on its instance and its device, from its own log ("Enabled instance extensions:",
        /// "Enabled extensions:"), which it writes as <c>exe_dxgi.log</c> and <c>exe_d3d11.log</c> in DXVK_LOG_PATH or the working
        /// folder. The last list of each kind counts. False, with the reason in <paramref name="source"/>, when there is no log.
        /// </summary>
        public static bool EnabledExtensions(out HashSet<string> instance, out HashSet<string> device, out string source)
        {
            instance = new HashSet<string>();
            device = new HashSet<string>();
            string folder = Environment.GetEnvironmentVariable("DXVK_LOG_PATH");
            if (folder == "none")
            {
                source = "DXVK_LOG_PATH=none, DXVK writes no log";
                return false;
            }
            if (string.IsNullOrEmpty(folder))
                folder = Environment.CurrentDirectory;

            Process process = Process.GetCurrentProcess();
            string exe = Path.GetFileNameWithoutExtension(process.MainModule.FileName);
            var read = new List<string>();
            foreach (string name in new[] { "_dxgi.log", "_d3d11.log" })
            {
                // A log older than this process is a previous run's: DXVK only rewrites it when it has something to say.
                string path = Path.Combine(folder, exe + name);
                if (!File.Exists(path) || File.GetLastWriteTime(path) < process.StartTime)
                    continue;
                HashSet<string> list = null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // Every line is "info:  " (or another level) and the message.
                        string text = Regex.Replace(line, @"^[a-z]+:\s+", "").Trim();
                        if (text == "Enabled instance extensions:")
                        {
                            list = instance;
                            list.Clear();
                        }
                        else if (text == "Enabled extensions:")
                        {
                            list = device;
                            list.Clear();
                        }
                        else if (list != null && Regex.IsMatch(text, @"^VK_\w+$"))
                        {
                            list.Add(text);
                        }
                        else
                        {
                            list = null;
                        }
                    }
                }
                read.Add(path);
            }
            source = read.Count > 0 ? string.Join(", ", read) : $"no {exe}_dxgi.log or {exe}_d3d11.log in {folder}";
            return instance.Count > 0 || device.Count > 0;
        }

        /// <summary>
        /// Extensions DXVK does not list by name but has anyway: promoted into core Vulkan up to 1.3, the version DXVK creates its
        /// instance and device with. A runtime asking for one gets the core functionality (not the KHR-named entry points).
        /// </summary>
        public static readonly HashSet<string> CoreUpTo13 = new HashSet<string>
        {
            // 1.1
            "VK_KHR_16bit_storage", "VK_KHR_bind_memory2", "VK_KHR_dedicated_allocation", "VK_KHR_descriptor_update_template",
            "VK_KHR_device_group", "VK_KHR_device_group_creation", "VK_KHR_external_fence", "VK_KHR_external_fence_capabilities",
            "VK_KHR_external_memory", "VK_KHR_external_memory_capabilities", "VK_KHR_external_semaphore",
            "VK_KHR_external_semaphore_capabilities", "VK_KHR_get_memory_requirements2", "VK_KHR_get_physical_device_properties2",
            "VK_KHR_maintenance1", "VK_KHR_maintenance2", "VK_KHR_maintenance3", "VK_KHR_multiview", "VK_KHR_relaxed_block_layout",
            "VK_KHR_sampler_ycbcr_conversion", "VK_KHR_shader_draw_parameters", "VK_KHR_storage_buffer_storage_class",
            "VK_KHR_variable_pointers",
            // 1.2
            "VK_KHR_8bit_storage", "VK_KHR_buffer_device_address", "VK_KHR_create_renderpass2", "VK_KHR_depth_stencil_resolve",
            "VK_KHR_draw_indirect_count", "VK_KHR_driver_properties", "VK_KHR_image_format_list", "VK_KHR_imageless_framebuffer",
            "VK_KHR_sampler_mirror_clamp_to_edge", "VK_KHR_separate_depth_stencil_layouts", "VK_KHR_shader_atomic_int64",
            "VK_KHR_shader_float16_int8", "VK_KHR_shader_float_controls", "VK_KHR_shader_subgroup_extended_types",
            "VK_KHR_spirv_1_4", "VK_KHR_timeline_semaphore", "VK_KHR_uniform_buffer_standard_layout", "VK_KHR_vulkan_memory_model",
            "VK_EXT_descriptor_indexing", "VK_EXT_host_query_reset", "VK_EXT_sampler_filter_minmax", "VK_EXT_scalar_block_layout",
            "VK_EXT_separate_stencil_usage", "VK_EXT_shader_viewport_index_layer",
            // 1.3
            "VK_KHR_copy_commands2", "VK_KHR_dynamic_rendering", "VK_KHR_format_feature_flags2", "VK_KHR_maintenance4",
            "VK_KHR_shader_integer_dot_product", "VK_KHR_shader_non_semantic_info", "VK_KHR_shader_terminate_invocation",
            "VK_KHR_synchronization2", "VK_KHR_zero_initialize_workgroup_memory", "VK_EXT_4444_formats",
            "VK_EXT_extended_dynamic_state", "VK_EXT_extended_dynamic_state2", "VK_EXT_image_robustness",
            "VK_EXT_inline_uniform_block", "VK_EXT_pipeline_creation_cache_control", "VK_EXT_pipeline_creation_feedback",
            "VK_EXT_private_data", "VK_EXT_shader_demote_to_helper_invocation", "VK_EXT_subgroup_size_control",
            "VK_EXT_texel_buffer_alignment", "VK_EXT_texture_compression_astc_hdr", "VK_EXT_tooling_info",
            "VK_EXT_ycbcr_2plane_444_formats",
        };

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileName(IntPtr module, char* buffer, int size);

        private static string ModulePath(string name)
        {
            IntPtr module = GetModuleHandle(name);
            if (module == IntPtr.Zero)
                return name;
            char* buffer = stackalloc char[1024];
            int length = GetModuleFileName(module, buffer, 1024);
            return length > 0 ? new string(buffer, 0, length) : name;
        }

        public void Dispose()
        {
            if (pointer != IntPtr.Zero)
                Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }
}
