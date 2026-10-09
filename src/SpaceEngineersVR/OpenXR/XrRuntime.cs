using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The OpenXR instance and headset system. Created before the game starts so the render device can be put on
    /// the GPU the headset runtime requires; the session itself needs that device and is created later.
    /// </summary>
    internal static unsafe class XrRuntime
    {
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

        internal static Xr.GetD3D11GraphicsRequirementsKHR GetD3D11Requirements;
        internal static Xr.GetVisibilityMaskKHR GetVisibilityMask;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string path);

        /// <summary>Full path of the openxr_loader.dll that was loaded, or null before <see cref="CreateInstance"/> loads one.</summary>
        public static string LoaderPath { get; private set; }

        /// <summary>
        /// Where openxr_loader.dll is looked for under the plugin folder, in this order: beside the plugin (the launcher's
        /// install, a Pulsar development folder, a loader copied from the repository), then x64\bin\, where the Khronos
        /// release archive has it when Pulsar downloads the archive and extracts it into the plugin's Bin folder (an asset
        /// with a Url and Extract="true", as in release/PluginHub/CheezecakeSEVR.xml).
        /// </summary>
        internal static readonly string[] LoaderPaths = { "openxr_loader.dll", @"x64\bin\openxr_loader.dll" };

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

        /// <summary>Loads the Khronos loader from the plugin folder (<see cref="FindLoader"/>) and creates the instance. False when OpenXR is unusable.</summary>
        public static bool CreateInstance(string pluginDir)
        {
            string loaderPath = FindLoader(pluginDir);
            if (LoadLibrary(loaderPath) == IntPtr.Zero)
            {
                Log.Error($"Could not load {loaderPath} (Win32 error {Marshal.GetLastWin32Error()})");
                return false;
            }
            LoaderPath = loaderPath;
            Log.Info("OpenXR loader: " + loaderPath);

            HashSet<string> available = EnumerateExtensions();
            if (!available.Contains(XrConst.D3D11Extension))
            {
                Log.Error("The active OpenXR runtime does not support Direct3D 11 (XR_KHR_D3D11_enable)");
                return false;
            }

            var extensions = new List<string> { XrConst.D3D11Extension };
            HasVisibilityMask = available.Contains(XrConst.VisibilityMaskExtension);
            if (HasVisibilityMask)
                extensions.Add(XrConst.VisibilityMaskExtension);
            HasPalmPose = available.Contains(XrConst.PalmPoseExtension);
            if (HasPalmPose)
                extensions.Add(XrConst.PalmPoseExtension);
            else
                Log.Info($"The OpenXR runtime does not offer {XrConst.PalmPoseExtension}; the palm is placed from the grip pose");

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

            GetD3D11Requirements = Xr.GetFunction<Xr.GetD3D11GraphicsRequirementsKHR>(Instance, "xrGetD3D11GraphicsRequirementsKHR");
            if (HasVisibilityMask)
                GetVisibilityMask = Xr.GetFunction<Xr.GetVisibilityMaskKHR>(Instance, "xrGetVisibilityMaskKHR");

            Log.Info($"OpenXR instance created (extensions: {string.Join(", ", extensions)})");
            return true;
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

            // The runtime requires this call before a D3D11 session can be created.
            var requirements = new XrGraphicsRequirementsD3D11KHR { Type = XrStructureType.GraphicsRequirementsD3D11KHR };
            Xr.Check(GetD3D11Requirements(Instance, systemId, ref requirements), "xrGetD3D11GraphicsRequirementsKHR", Instance);
            AdapterLuid = requirements.AdapterLuid;
            MinFeatureLevel = requirements.MinFeatureLevel;

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
                     $"tracking orientation={properties.OrientationTracking != 0} position={properties.PositionTracking != 0}");
            return true;
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
