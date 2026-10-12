using System;
using System.Collections.Generic;
using System.IO;
#if NETCOREAPP
using System.Reflection;
#endif
using System.Runtime.InteropServices;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The OpenXR instance and headset system. Created before the game starts so the render device can be put on
    /// the GPU the headset runtime requires; the session itself needs that device and is created later.
    /// </summary>
    internal static unsafe class XrRuntime
    {
        // Conditional compilation here uses two different symbols. NETCOREAPP is the runtime (CoreCLR, where the loader is
        // bound through a DllImport resolver); LINUX is the operating system (library names and runtime discovery). Linux
        // builds always run on CoreCLR, so LINUX implies NETCOREAPP; Windows builds may be either.

        public static ulong Instance { get; private set; }
        public static ulong SystemId { get; private set; }
        public static string SystemName { get; private set; }

        /// <summary>LUID of the GPU the runtime requires, or 0 when no headset system was found.</summary>
        public static long AdapterLuid { get; private set; }

        public static int MinFeatureLevel { get; private set; }
        public static uint EyeWidth { get; private set; }
        public static uint EyeHeight { get; private set; }
        public static bool HasVisibilityMask { get; private set; }

        /// <summary>
        /// XR_EXT_palm_pose is enabled: the controllers have a palm pose (/input/palm_ext/pose). The instance is OpenXR 1.0,
        /// so the extension is the way to it; 1.1's /input/grip_surface/pose is the same pose under another name.
        /// </summary>
        public static bool HasPalmPose { get; private set; }

        public static bool HasSystem => SystemId != 0;

        /// <summary>XR_KHR_composition_layer_cylinder is enabled: layers can be curved round the head (<see cref="Rendering.HudVisor"/>).</summary>
        public static bool HasCylinder { get; private set; }

        /// <summary>The most composition layers the headset takes in one frame (the spec's minimum is 16); 0 before <see cref="TryGetSystem"/>.</summary>
        public static int MaxLayerCount { get; private set; }

        /// <summary>The largest swapchain image the headset takes, pixels.</summary>
        public static int MaxImageWidth { get; private set; }
        public static int MaxImageHeight { get; private set; }

        /// <summary>XR_OCULUS_audio_device_guid is enabled: the runtime names the headset's audio output (<see cref="AudioOutputDeviceId"/>).</summary>
        public static bool HasAudioDeviceGuid { get; private set; }

        /// <summary>XR_KHR_D3D11_enable is enabled: a session on the game's own D3D11 device, the way without DXVK.</summary>
        public static bool HasD3D11 { get; private set; }

        /// <summary>
        /// The game's DXGI is DXVK (<see cref="Rendering.DxvkInterop.DxgiIsDxvk"/>), found before the instance was created. Only
        /// then is a Vulkan extension enabled.
        /// </summary>
        public static bool DxgiIsDxvk { get; private set; }

        /// <summary>
        /// The Vulkan extension enabled for a session on DXVK's Vulkan device (<see cref="XrConst.VulkanExtension"/>, else
        /// <see cref="XrConst.Vulkan2Extension"/>); null without DXVK or when the runtime offers neither.
        /// </summary>
        public static string VulkanExtension { get; private set; }

        public static bool HasVulkan => VulkanExtension != null;

        /// <summary>The Vulkan API versions the runtime takes (XrVersion: major &lt;&lt; 48 | minor &lt;&lt; 32 | patch); 0 before <see cref="TryGetSystem"/> or without Vulkan.</summary>
        public static ulong VulkanMinApiVersion { get; private set; }
        public static ulong VulkanMaxApiVersion { get; private set; }

        internal static Xr.GetD3D11GraphicsRequirementsKHR GetD3D11Requirements;
        private static Xr.GetVulkanGraphicsRequirementsKHR getVulkanRequirements;
        private static Xr.GetVulkanGraphicsDeviceKHR getVulkanDevice;
        private static Xr.GetVulkanGraphicsDevice2KHR getVulkanDevice2;
        private static Xr.GetVulkanExtensionsKHR getVulkanInstanceExtensions, getVulkanDeviceExtensions;
        internal static Xr.GetVisibilityMaskKHR GetVisibilityMask;
        private static Xr.GetAudioOutputDeviceGuidOculus getAudioOutputDeviceGuid;

#if NETCOREAPP
        /// <summary>The loader's handle, from <see cref="LoadLoader"/>; <see cref="ResolveImport"/> gives it to the DllImports in <see cref="Xr"/>.</summary>
        private static IntPtr loaderHandle;

        private static bool resolverSet;
#else
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);
#endif

        /// <summary>Full path of the loader that was loaded (the library name when the system's was), or null before <see cref="CreateInstance"/> loads one.</summary>
        public static string LoaderPath { get; private set; }

#if LINUX
        /// <summary>
        /// The Khronos loader's file names under Linux: looked for in the system's library paths first (the distribution's libopenxr-loader1 package), then the plugin folder (a
        /// Platforms="Linux" asset of the plugin data file, none today: the loader needs libjsoncpp, so it is the distribution's to match).
        /// </summary>
        internal static readonly string[] LoaderPaths = { "libopenxr_loader.so.1", "libopenxr_loader.so" };
#else
        /// <summary>
        /// Where openxr_loader.dll is looked for under the plugin folder, in this order: beside the plugin (the launcher's
        /// install, a Pulsar development folder, a loader copied from the repository), then x64\bin\, where the Khronos
        /// release archive has it when Pulsar downloads the archive and extracts it into the plugin's Bin folder (an asset
        /// with a Url and Extract="true", as in release/PluginHub/CheezecakeSEVR.xml).
        /// </summary>
        internal static readonly string[] LoaderPaths = { "openxr_loader.dll", @"x64\bin\openxr_loader.dll" };
#endif

        /// <summary>The first of <see cref="LoaderPaths"/> under <paramref name="pluginDir"/> that exists; the first one when none does, for the error.</summary>
        internal static string FindLoader(string pluginDir)
        {
            foreach (string relative in LoaderPaths)
            {
                string path = Path.Combine(pluginDir, relative);
                if (File.Exists(path))
                    return path;
            }
            return Path.Combine(pluginDir, LoaderPaths[0]);
        }

#if NETCOREAPP
        /// <summary>
        /// Loads the loader by path (on Linux, from the system's library paths when the plugin folder has none), and has every
        /// DllImport of <see cref="Xr"/> use that library. <paramref name="path"/> becomes what was loaded.
        /// </summary>
        private static bool LoadLoader(ref string path, out string error)
        {
            error = null;
            try
            {
                if (!resolverSet)
                {
                    // The DllImports name the library "openxr_loader", which a path-loaded library does not answer to on Linux.
                    NativeLibrary.SetDllImportResolver(typeof(Xr).Assembly, ResolveImport);
                    resolverSet = true;
                }
#if LINUX
                // The distribution's loader first: it reads the same runtime manifests as every other OpenXR program on the
                // machine. The plugin folder's copy (a Platforms="Linux" asset, if the data file ships one) is the fallback.
                foreach (string name in LoaderPaths)
                {
                    if (NativeLibrary.TryLoad(name, out loaderHandle))
                    {
                        path = name;
                        return true;
                    }
                }
                if (!File.Exists(path))
                {
                    error = $"the system has no {LoaderPaths[0]} and the plugin folder has none. Install the OpenXR loader: "
                        + "libopenxr-loader1 (Debian, Ubuntu), openxr (Arch), openxr-libs (Fedora)";
                    return false;
                }
#endif
                loaderHandle = NativeLibrary.Load(path);
                return true;
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        private static IntPtr ResolveImport(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
            name == Xr.Loader ? loaderHandle : IntPtr.Zero;
#else
        private static bool LoadLoader(ref string path, out string error)
        {
            if (LoadLibrary(path) != IntPtr.Zero)
            {
                error = null;
                return true;
            }
            error = "Win32 error " + Marshal.GetLastWin32Error();
            return false;
        }
#endif

        /// <summary>Loads the Khronos loader from the plugin folder (<see cref="FindLoader"/>) and creates the instance. False when OpenXR is unusable.</summary>
        public static bool CreateInstance(string pluginDir)
        {
#if LINUX
            XrRuntimeDiscovery.Apply(); // before the loader reads XR_RUNTIME_JSON
#endif
            string loaderPath = FindLoader(pluginDir);
            if (!LoadLoader(ref loaderPath, out string loadError))
            {
                Log.Error($"Could not load {loaderPath} ({loadError})");
                return false;
            }
            LoaderPath = loaderPath;
            Log.Info("OpenXR loader: " + loaderPath);

            HashSet<string> available = EnumerateExtensions();
            HasD3D11 = available.Contains(XrConst.D3D11Extension);
            ChooseVulkan(available);
            if (!HasD3D11 && !HasVulkan)
            {
                Log.Error("The active OpenXR runtime does not support Direct3D 11 (XR_KHR_D3D11_enable)");
                return false;
            }

            var extensions = new List<string>();
            if (HasD3D11)
                extensions.Add(XrConst.D3D11Extension);
            if (HasVulkan)
                extensions.Add(VulkanExtension);
            XrInput.AddProfileExtensions(available, extensions); // the controllers with their own profile, those the runtime lists
            HasVisibilityMask = available.Contains(XrConst.VisibilityMaskExtension);
            if (HasVisibilityMask)
                extensions.Add(XrConst.VisibilityMaskExtension);
            HasPalmPose = available.Contains(XrConst.PalmPoseExtension);
            if (HasPalmPose)
                extensions.Add(XrConst.PalmPoseExtension);
            else
                Log.Info($"The OpenXR runtime does not offer {XrConst.PalmPoseExtension}; the palm is placed from the grip pose");
            HasAudioDeviceGuid = available.Contains(XrConst.AudioDeviceGuidExtension);
            if (HasAudioDeviceGuid)
                extensions.Add(XrConst.AudioDeviceGuidExtension);
            HasCylinder = available.Contains(XrConst.CylinderExtension);
            if (HasCylinder)
                extensions.Add(XrConst.CylinderExtension);
            else
                Log.Info($"The OpenXR runtime does not offer {XrConst.CylinderExtension}; the HUD on the visor is drawn on flat pieces turned to face the eyes");

            IntPtr[] names = new IntPtr[extensions.Count];
            try
            {
                for (int i = 0; i < names.Length; i++)
                    names[i] = Marshal.StringToHGlobalAnsi(extensions[i]);

                fixed (IntPtr* namesPtr = names)
                {
                    var info = new XrInstanceCreateInfo
                    {
                        Type = XrStructureType.InstanceCreateInfo,
                        EnabledExtensionCount = (uint)names.Length,
                        EnabledExtensionNames = (IntPtr)namesPtr,
                    };
                    Xr.WriteString(info.ApplicationInfo.ApplicationName, 128, "Space Engineers VR");
                    info.ApplicationInfo.ApplicationVersion = 1;
                    Xr.WriteString(info.ApplicationInfo.EngineName, 128, "VRage");
                    info.ApplicationInfo.EngineVersion = 1;
                    info.ApplicationInfo.ApiVersion = XrConst.ApiVersion_1_0;

                    Xr.Check(Xr.xrCreateInstance(ref info, out ulong instance), "xrCreateInstance");
                    Instance = instance;
                }
            }
            finally
            {
                foreach (IntPtr name in names)
                    if (name != IntPtr.Zero)
                        Marshal.FreeHGlobal(name);
            }

            if (HasD3D11)
                GetD3D11Requirements = Xr.GetFunction<Xr.GetD3D11GraphicsRequirementsKHR>(Instance, "xrGetD3D11GraphicsRequirementsKHR");
            if (VulkanExtension == XrConst.VulkanExtension)
            {
                getVulkanRequirements = Xr.GetFunction<Xr.GetVulkanGraphicsRequirementsKHR>(Instance, "xrGetVulkanGraphicsRequirementsKHR");
                getVulkanDevice = Xr.GetFunction<Xr.GetVulkanGraphicsDeviceKHR>(Instance, "xrGetVulkanGraphicsDeviceKHR");
                getVulkanInstanceExtensions = Xr.GetFunction<Xr.GetVulkanExtensionsKHR>(Instance, "xrGetVulkanInstanceExtensionsKHR");
                getVulkanDeviceExtensions = Xr.GetFunction<Xr.GetVulkanExtensionsKHR>(Instance, "xrGetVulkanDeviceExtensionsKHR");
            }
            else if (VulkanExtension == XrConst.Vulkan2Extension)
            {
                getVulkanRequirements = Xr.GetFunction<Xr.GetVulkanGraphicsRequirementsKHR>(Instance, "xrGetVulkanGraphicsRequirements2KHR");
                getVulkanDevice2 = Xr.GetFunction<Xr.GetVulkanGraphicsDevice2KHR>(Instance, "xrGetVulkanGraphicsDevice2KHR");
            }
            if (HasVisibilityMask)
                GetVisibilityMask = Xr.GetFunction<Xr.GetVisibilityMaskKHR>(Instance, "xrGetVisibilityMaskKHR");
            if (HasAudioDeviceGuid)
                getAudioOutputDeviceGuid = Xr.GetFunction<Xr.GetAudioOutputDeviceGuidOculus>(Instance, "xrGetAudioOutputDeviceGuidOculus");

            Log.Info($"OpenXR instance created (extensions: {string.Join(", ", extensions)})");
            return true;
        }

        /// <summary>
        /// The Windows audio endpoint the runtime plays VR sound through (Meta's XR_OCULUS_audio_device_guid), in the form
        /// IMMDevice::GetId gives, e.g. "{0.0.0.00000000}.{guid}". Null, with the reason, when the runtime names none.
        /// </summary>
        public static string AudioOutputDeviceId(out string reason)
        {
            if (Instance == 0)
            {
                reason = "no OpenXR instance";
                return null;
            }
            if (!HasAudioDeviceGuid || getAudioOutputDeviceGuid == null)
            {
                reason = $"the OpenXR runtime does not offer {XrConst.AudioDeviceGuidExtension}";
                return null;
            }

            char* buffer = stackalloc char[XrConst.MaxAudioDeviceStrSizeOculus];
            buffer[0] = '\0';
            int result = getAudioOutputDeviceGuid(Instance, (IntPtr)buffer);
            if (result < 0)
            {
                reason = "xrGetAudioOutputDeviceGuidOculus failed: " + Xr.ResultName(Instance, result);
                return null;
            }
            int length = 0;
            while (length < XrConst.MaxAudioDeviceStrSizeOculus && buffer[length] != '\0')
                length++;
            if (length == 0)
            {
                reason = "the OpenXR runtime named no audio output device";
                return null;
            }
            reason = null;
            return new string(buffer, 0, length);
        }

        /// <summary>Finds the headset. False when none is connected yet (the runtime reports the form factor unavailable).</summary>
        public static bool TryGetSystem()
        {
            if (HasSystem)
                return true;

            var getInfo = new XrSystemGetInfo { Type = XrStructureType.SystemGetInfo, FormFactor = XrConst.FormFactorHeadMountedDisplay };
            int result = Xr.xrGetSystem(Instance, ref getInfo, out ulong systemId);
            if (result == XrResult.ErrorFormFactorUnavailable)
                return false;
            Xr.Check(result, "xrGetSystem", Instance);

            var properties = new XrSystemProperties { Type = XrStructureType.SystemProperties };
            Xr.Check(Xr.xrGetSystemProperties(Instance, systemId, ref properties), "xrGetSystemProperties", Instance);
            SystemName = Xr.ReadString(properties.SystemName, 256);
            MaxLayerCount = (int)Math.Min(properties.MaxLayerCount, 64u);
            MaxImageWidth = (int)Math.Min(properties.MaxSwapchainImageWidth, 16384u);
            MaxImageHeight = (int)Math.Min(properties.MaxSwapchainImageHeight, 16384u);

            // The runtime requires this call before a D3D11 session can be created.
            if (HasD3D11)
            {
                var requirements = new XrGraphicsRequirementsD3D11KHR { Type = XrStructureType.GraphicsRequirementsD3D11KHR };
                Xr.Check(GetD3D11Requirements(Instance, systemId, ref requirements), "xrGetD3D11GraphicsRequirementsKHR", Instance);
                AdapterLuid = requirements.AdapterLuid;
                MinFeatureLevel = requirements.MinFeatureLevel;
            }
            // ... and this one before a Vulkan session.
            if (HasVulkan)
            {
                var vulkan = new XrGraphicsRequirementsVulkanKHR { Type = XrStructureType.GraphicsRequirementsVulkanKHR };
                Xr.Check(getVulkanRequirements(Instance, systemId, ref vulkan),
                    getVulkanDevice != null ? "xrGetVulkanGraphicsRequirementsKHR" : "xrGetVulkanGraphicsRequirements2KHR", Instance);
                VulkanMinApiVersion = vulkan.MinApiVersionSupported;
                VulkanMaxApiVersion = vulkan.MaxApiVersionSupported;
            }

            Xr.Check(Xr.xrEnumerateViewConfigurationViews(Instance, systemId, XrConst.ViewConfigurationPrimaryStereo, 0, out uint viewCount, null),
                "xrEnumerateViewConfigurationViews", Instance);
            var views = new XrViewConfigurationView[viewCount];
            for (int i = 0; i < views.Length; i++)
                views[i].Type = XrStructureType.ViewConfigurationView;
            fixed (XrViewConfigurationView* viewsPtr = views)
                Xr.Check(Xr.xrEnumerateViewConfigurationViews(Instance, systemId, XrConst.ViewConfigurationPrimaryStereo, viewCount, out viewCount, viewsPtr),
                    "xrEnumerateViewConfigurationViews", Instance);
            EyeWidth = views[0].RecommendedImageRectWidth;
            EyeHeight = views[0].RecommendedImageRectHeight;

            SystemId = systemId;
            Log.Info($"Headset: {SystemName}, eye {EyeWidth}x{EyeHeight}, GPU LUID {AdapterLuid:X}, " +
                     $"tracking orientation={properties.OrientationTracking != 0} position={properties.PositionTracking != 0}, " +
                     $"up to {properties.MaxLayerCount} layers a frame, images up to {properties.MaxSwapchainImageWidth}x{properties.MaxSwapchainImageHeight}, " +
                     $"cylinder layers {(HasCylinder ? "offered" : "not offered")}" +
                     (HasVulkan ? $", Vulkan API {VersionText(VulkanMinApiVersion)} to {VersionText(VulkanMaxApiVersion)}" : ""));
            return true;
        }

        /// <summary>An XrVersion as major.minor.patch.</summary>
        public static string VersionText(ulong version) => $"{version >> 48}.{(version >> 32) & 0xffff}.{version & 0xffffffff}";

        /// <summary>
        /// With DXVK as the game's DXGI, the session has to be a Vulkan one on DXVK's device (a runtime cannot take DXVK's D3D11
        /// device for a Direct3D 11 one), so the runtime's Vulkan extension is enabled. Without DXVK nothing changes.
        /// </summary>
        private static void ChooseVulkan(HashSet<string> available)
        {
            VulkanExtension = null;
            string module;
            try
            {
                DxgiIsDxvk = Rendering.DxvkInterop.DxgiIsDxvk(out module);
            }
            catch (Exception e)
            {
                DxgiIsDxvk = false;
                Log.Error(e, "Could not tell whether the game's DXGI is DXVK; going on as without it");
                return;
            }
            if (!DxgiIsDxvk)
                return;

            if (available.Contains(XrConst.VulkanExtension))
                VulkanExtension = XrConst.VulkanExtension;
            else if (available.Contains(XrConst.Vulkan2Extension))
                VulkanExtension = XrConst.Vulkan2Extension;
            if (VulkanExtension == XrConst.VulkanExtension)
                Log.Info($"The game's DXGI is DXVK ({module}): the headset session will run on DXVK's Vulkan device through {VulkanExtension}");
            else if (HasVulkan)
                Log.Warn($"The game's DXGI is DXVK ({module}): the headset session will run on DXVK's Vulkan device through {VulkanExtension}, " +
                         $"as the runtime has no {XrConst.VulkanExtension}; enable2 expects an instance and device the runtime made, which DXVK's are not");
            else
                Log.Warn($"The game's DXGI is DXVK ({module}) but the OpenXR runtime offers neither {XrConst.VulkanExtension} nor {XrConst.Vulkan2Extension}; " +
                         "only Direct3D 11 is left, which a runtime cannot normally use on DXVK");
        }

#if LINUX
        [DllImport("libvulkan.so.1")]
        private static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, [MarshalAs(UnmanagedType.LPStr)] string name);
#endif

        /// <summary>
        /// Why a runtime cannot use DXVK's Vulkan instance, or null when it can (or cannot be told). Linux only: a runtime that
        /// calls vkGetPhysicalDeviceProperties2KHR on an instance without VK_KHR_get_physical_device_properties2 (stock DXVK
        /// enables none of what OpenXR needs) crashes the game inside xrGetVulkanGraphicsDeviceKHR, so it must not be asked.
        /// </summary>
        public static string VulkanInstanceProblem(IntPtr vkInstance)
        {
#if LINUX
            // The loader only hands out an instance extension's entry points when the instance enabled that extension.
            if (vkInstance != IntPtr.Zero && vkGetInstanceProcAddr(vkInstance, "vkGetPhysicalDeviceProperties2KHR") == IntPtr.Zero)
                return "DXVK lacks the Vulkan extensions OpenXR needs (stock linux-compat DXVK); run tools/linux/setup.sh to install the patched one";
#endif
            return null;
        }

        /// <summary>The VkPhysicalDevice of <paramref name="vkInstance"/> the headset has to be drawn on (xrGetVulkanGraphicsDevice(2)KHR).</summary>
        public static IntPtr VulkanGraphicsDevice(IntPtr vkInstance)
        {
            string problem = VulkanInstanceProblem(vkInstance);
            if (problem != null)
                throw new InvalidOperationException(problem);
            IntPtr physicalDevice;
            if (getVulkanDevice != null)
            {
                Xr.Check(getVulkanDevice(Instance, SystemId, vkInstance, out physicalDevice), "xrGetVulkanGraphicsDeviceKHR", Instance);
            }
            else
            {
                var info = new XrVulkanGraphicsDeviceGetInfoKHR { Type = XrStructureType.VulkanGraphicsDeviceGetInfoKHR, SystemId = SystemId, VulkanInstance = vkInstance };
                Xr.Check(getVulkanDevice2(Instance, ref info, out physicalDevice), "xrGetVulkanGraphicsDevice2KHR", Instance);
            }
            return physicalDevice;
        }

        /// <summary>
        /// The Vulkan instance (or device) extensions the runtime needs enabled on the application's instance (device), from
        /// xrGetVulkanInstanceExtensionsKHR (xrGetVulkanDeviceExtensionsKHR). Null with XR_KHR_vulkan_enable2, which has no such
        /// query: its runtime adds them itself when it makes the instance and device.
        /// </summary>
        public static string[] VulkanRequiredExtensions(bool device)
        {
            Xr.GetVulkanExtensionsKHR get = device ? getVulkanDeviceExtensions : getVulkanInstanceExtensions;
            if (get == null)
                return null;
            string call = device ? "xrGetVulkanDeviceExtensionsKHR" : "xrGetVulkanInstanceExtensionsKHR";
            Xr.Check(get(Instance, SystemId, 0, out uint count, null), call, Instance);
            byte[] buffer = new byte[Math.Max(count, 1u)];
            fixed (byte* ptr = buffer)
            {
                Xr.Check(get(Instance, SystemId, (uint)buffer.Length, out count, ptr), call, Instance);
                return Xr.ReadString(ptr, buffer.Length).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static HashSet<string> EnumerateExtensions()
        {
            Xr.Check(Xr.xrEnumerateInstanceExtensionProperties(IntPtr.Zero, 0, out uint count, null), "xrEnumerateInstanceExtensionProperties");
            var properties = new XrExtensionProperties[count];
            for (int i = 0; i < properties.Length; i++)
                properties[i].Type = XrStructureType.ExtensionProperties;

            var names = new HashSet<string>();
            fixed (XrExtensionProperties* ptr = properties)
            {
                Xr.Check(Xr.xrEnumerateInstanceExtensionProperties(IntPtr.Zero, count, out count, ptr), "xrEnumerateInstanceExtensionProperties");
                for (int i = 0; i < count; i++)
                    names.Add(Xr.ReadString(ptr[i].ExtensionName, 128));
            }
            return names;
        }
    }
}
