using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SpaceEngineersVR.OpenXR
{
    // Bindings for the parts of OpenXR 1.1 this plugin uses, transcribed from the Khronos headers (openxr.h,
    // openxr_platform.h 1.1.63). Handles are 64-bit on x64, so every handle, XrPath and XrSystemId is a ulong.
    //
    // The structs below are mirror types of the specification's C structs, marshalled by layout: field order, field types
    // and sequential packing must match the header exactly, and a field that nothing reads is still there to keep the
    // offsets of those after it.

    internal static class XrResult
    {
        public const int Success = 0;
        public const int ErrorRuntimeFailure = -2;
        public const int TimeoutExpired = 1;
        public const int SessionLossPending = 3;
        public const int EventUnavailable = 4;
        public const int SessionNotFocused = 8;
        public const int FrameDiscarded = 9;
        public const int ErrorInstanceLost = -13;
        public const int ErrorSessionNotRunning = -16;
        public const int ErrorSessionLost = -17;
        public const int ErrorSessionNotReady = -28;
        public const int ErrorCallOrderInvalid = -37;
        public const int ErrorFormFactorUnavailable = -35;
        public const int ErrorRuntimeUnavailable = -51;
    }

    internal enum XrStructureType
    {
        ExtensionProperties = 2,
        InstanceCreateInfo = 3,
        SystemGetInfo = 4,
        SystemProperties = 5,
        ViewLocateInfo = 6,
        View = 7,
        SessionCreateInfo = 8,
        SwapchainCreateInfo = 9,
        SessionBeginInfo = 10,
        ViewState = 11,
        FrameEndInfo = 12,
        HapticVibration = 13,
        EventDataBuffer = 16,
        EventDataInstanceLossPending = 17,
        EventDataSessionStateChanged = 18,
        ActionStateBoolean = 23,
        ActionStateFloat = 24,
        ActionStateVector2f = 25,
        ActionStatePose = 27,
        ActionSetCreateInfo = 28,
        ActionCreateInfo = 29,
        FrameWaitInfo = 33,
        CompositionLayerProjection = 35,
        CompositionLayerQuad = 36,
        ReferenceSpaceCreateInfo = 37,
        ActionSpaceCreateInfo = 38,
        EventDataReferenceSpaceChangePending = 40,
        ViewConfigurationView = 41,
        SpaceLocation = 42,
        FrameState = 44,
        FrameBeginInfo = 46,
        CompositionLayerProjectionView = 48,
        InteractionProfileSuggestedBinding = 51,
        EventDataInteractionProfileChanged = 52,
        InteractionProfileState = 53,
        SwapchainImageAcquireInfo = 55,
        SwapchainImageWaitInfo = 56,
        SwapchainImageReleaseInfo = 57,
        ActionStateGetInfo = 58,
        HapticActionInfo = 59,
        SessionActionSetsAttachInfo = 60,
        ActionsSyncInfo = 61,
        CompositionLayerCylinderKHR = 1000017000,
        GraphicsBindingVulkanKHR = 1000025000,
        SwapchainImageVulkanKHR = 1000025001,
        GraphicsRequirementsVulkanKHR = 1000025002,
        GraphicsBindingD3D11KHR = 1000027000,
        SwapchainImageD3D11KHR = 1000027001,
        GraphicsRequirementsD3D11KHR = 1000027002,
        VisibilityMaskKHR = 1000031000,
        EventDataVisibilityMaskChangedKHR = 1000031001,
        EventDataDisplayRefreshRateChangedFB = 1000101000,
        VulkanGraphicsDeviceGetInfoKHR = 1000090003,
    }

    internal enum XrSessionState
    {
        Unknown = 0, Idle = 1, Ready = 2, Synchronized = 3, Visible = 4, Focused = 5, Stopping = 6, LossPending = 7, Exiting = 8,
    }

    internal enum XrReferenceSpaceType { View = 1, Local = 2, Stage = 3 }

    internal enum XrActionType { BooleanInput = 1, FloatInput = 2, Vector2fInput = 3, PoseInput = 4, VibrationOutput = 100 }

    internal enum XrEyeVisibility { Both = 0, Left = 1, Right = 2 }

    internal enum XrVisibilityMaskType { HiddenTriangleMesh = 1, VisibleTriangleMesh = 2, LineLoop = 3 }

    internal static class XrConst
    {
        public const ulong ApiVersion_1_0 = 1UL << 48;
        public const int FormFactorHeadMountedDisplay = 1;
        public const int ViewConfigurationPrimaryStereo = 2;
        public const int EnvironmentBlendOpaque = 1;
        public const ulong SwapchainUsageColorAttachment = 0x1;
        public const ulong SwapchainUsageTransferDst = 0x10;
        public const ulong SwapchainUsageSampled = 0x20;

        /// <summary>The images can be viewed in other formats of their size: DXVK takes an sRGB image to be mutable (its UNORM twin).</summary>
        public const ulong SwapchainUsageMutableFormat = 0x40;
        public const ulong LayerCorrectChromaticAberration = 0x1;
        public const ulong LayerBlendTextureSourceAlpha = 0x2;
        public const ulong SpaceLocationOrientationValid = 0x1;
        public const ulong SpaceLocationPositionValid = 0x2;
        public const ulong SpaceLocationOrientationTracked = 0x4;
        public const ulong SpaceLocationPositionTracked = 0x8;
        public const ulong ViewStateOrientationValid = 0x1;
        public const ulong ViewStatePositionValid = 0x2;
        public const long InfiniteDuration = 0x7fffffffffffffffL;
        public const long MinHapticDuration = -1;
        public const float FrequencyUnspecified = 0f;
        public const string D3D11Extension = "XR_KHR_D3D11_enable";

        /// <summary>
        /// Vulkan with the application's own instance and device: the one DXVK made (<see cref="Rendering.DxvkInterop"/>). Preferred
        /// over <see cref="Vulkan2Extension"/>, whose instance and device have to come from the runtime's own create calls.
        /// </summary>
        public const string VulkanExtension = "XR_KHR_vulkan_enable";
        public const string Vulkan2Extension = "XR_KHR_vulkan_enable2";
        public const string VisibilityMaskExtension = "XR_KHR_visibility_mask";

        /// <summary>Curved layers: the HUD's pieces on the visor (<see cref="Rendering.HudVisor"/>) are drawn on cylinders round the head.</summary>
        public const string CylinderExtension = "XR_KHR_composition_layer_cylinder";

        /// <summary>Adds /input/palm_ext/pose to every hand's profiles (OpenXR 1.1 calls it grip_surface; the definitions are identical).</summary>
        public const string PalmPoseExtension = "XR_EXT_palm_pose";

        /// <summary>Meta's runtime names the Windows audio endpoints of the headset (xrGetAudioOutputDeviceGuidOculus).</summary>
        public const string AudioDeviceGuidExtension = "XR_OCULUS_audio_device_guid";

        /// <summary>The headset's display refresh rate can be listed and requested (xrRequestDisplayRefreshRateFB).</summary>
        public const string DisplayRefreshRateExtension = "XR_FB_display_refresh_rate";

        /// <summary>XR_MAX_AUDIO_DEVICE_STR_SIZE_OCULUS: the wchar_t buffer xrGetAudioOutputDeviceGuidOculus fills.</summary>
        public const int MaxAudioDeviceStrSizeOculus = 128;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrVector2f { public float X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrVector3f { public float X, Y, Z; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrQuaternionf { public float X, Y, Z, W; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrPosef
    {
        public XrQuaternionf Orientation;
        public XrVector3f Position;

        public static XrPosef Identity => new XrPosef { Orientation = new XrQuaternionf { W = 1f } };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrFovf { public float AngleLeft, AngleRight, AngleUp, AngleDown; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrExtent2Df { public float Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrRect2Di { public int X, Y, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XrApplicationInfo
    {
        public fixed byte ApplicationName[128];
        public uint ApplicationVersion;
        public fixed byte EngineName[128];
        public uint EngineVersion;
        public ulong ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XrExtensionProperties
    {
        public XrStructureType Type;
        public IntPtr Next;
        public fixed byte ExtensionName[128];
        public uint ExtensionVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrInstanceCreateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong CreateFlags;
        public XrApplicationInfo ApplicationInfo;
        public uint EnabledApiLayerCount;
        public IntPtr EnabledApiLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSystemGetInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public int FormFactor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XrSystemProperties
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong SystemId;
        public uint VendorId;
        public fixed byte SystemName[256];
        public uint MaxSwapchainImageHeight;
        public uint MaxSwapchainImageWidth;
        public uint MaxLayerCount;
        public uint OrientationTracking;
        public uint PositionTracking;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrGraphicsRequirementsD3D11KHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public int MinFeatureLevel;

        public long AdapterLuid => ((long)AdapterLuidHigh << 32) | AdapterLuidLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrGraphicsBindingD3D11KHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public IntPtr Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrGraphicsRequirementsVulkanKHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong MinApiVersionSupported;
        public ulong MaxApiVersionSupported;
    }

    /// <summary>The Vulkan session's graphics: DXVK's own handles. Also XrGraphicsBindingVulkan2KHR (same type and layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrGraphicsBindingVulkanKHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public IntPtr Instance;
        public IntPtr PhysicalDevice;
        public IntPtr Device;
        public uint QueueFamilyIndex;
        public uint QueueIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrVulkanGraphicsDeviceGetInfoKHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong SystemId;
        public IntPtr VulkanInstance;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSessionCreateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong CreateFlags;
        public ulong SystemId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSessionBeginInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public int PrimaryViewConfigurationType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrReferenceSpaceCreateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public XrReferenceSpaceType ReferenceSpaceType;
        public XrPosef PoseInReferenceSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrViewConfigurationView
    {
        public XrStructureType Type;
        public IntPtr Next;
        public uint RecommendedImageRectWidth;
        public uint MaxImageRectWidth;
        public uint RecommendedImageRectHeight;
        public uint MaxImageRectHeight;
        public uint RecommendedSwapchainSampleCount;
        public uint MaxSwapchainSampleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSwapchainCreateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong CreateFlags;
        public ulong UsageFlags;
        public long Format;
        public uint SampleCount;
        public uint Width;
        public uint Height;
        public uint FaceCount;
        public uint ArraySize;
        public uint MipCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSwapchainImageD3D11KHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public IntPtr Texture;
    }

    /// <summary>A swapchain image of a Vulkan session: the VkImage (a 64-bit handle).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSwapchainImageVulkanKHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Image;
    }

    /// <summary>Shared shape of the info structs that carry nothing but type and next (acquire, release, frame wait/begin).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrBaseInfo
    {
        public XrStructureType Type;
        public IntPtr Next;

        public XrBaseInfo(XrStructureType type) { Type = type; Next = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSwapchainImageWaitInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public long Timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrFrameState
    {
        public XrStructureType Type;
        public IntPtr Next;
        public long PredictedDisplayTime;
        public long PredictedDisplayPeriod;
        public uint ShouldRender;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrFrameEndInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public long DisplayTime;
        public int EnvironmentBlendMode;
        public uint LayerCount;
        public IntPtr Layers;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrViewLocateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public int ViewConfigurationType;
        public long DisplayTime;
        public ulong Space;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrViewState
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong ViewStateFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrView
    {
        public XrStructureType Type;
        public IntPtr Next;
        public XrPosef Pose;
        public XrFovf Fov;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSwapchainSubImage
    {
        public ulong Swapchain;
        public XrRect2Di ImageRect;
        public uint ImageArrayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrCompositionLayerProjectionView
    {
        public XrStructureType Type;
        public IntPtr Next;
        public XrPosef Pose;
        public XrFovf Fov;
        public XrSwapchainSubImage SubImage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrCompositionLayerProjection
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong LayerFlags;
        public ulong Space;
        public uint ViewCount;
        public IntPtr Views;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrCompositionLayerQuad
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong LayerFlags;
        public ulong Space;
        public XrEyeVisibility EyeVisibility;
        public XrSwapchainSubImage SubImage;
        public XrPosef Pose;
        public XrExtent2Df Size;
    }

    /// <summary>
    /// XR_KHR_composition_layer_cylinder: an image on the inside of a cylinder round <see cref="Pose"/>, its axis along the
    /// pose's y, the middle of the image where the pose looks (-z), <see cref="CentralAngle"/> radians across, and
    /// <see cref="Radius"/> * <see cref="CentralAngle"/> / <see cref="AspectRatio"/> metres high.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrCompositionLayerCylinderKHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong LayerFlags;
        public ulong Space;
        public XrEyeVisibility EyeVisibility;
        public XrSwapchainSubImage SubImage;
        public XrPosef Pose;
        public float Radius;
        public float CentralAngle;
        public float AspectRatio;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XrEventDataBuffer
    {
        public XrStructureType Type;
        public IntPtr Next;
        public fixed byte Varying[4000];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrEventDataSessionStateChanged
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Session;
        public XrSessionState State;
        public long Time;
    }

    /// <summary>The runtime is about to move a reference space's origin (the user recentred). Poses at or after ChangeTime are in the new space.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrEventDataReferenceSpaceChangePending
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Session;
        public XrReferenceSpaceType ReferenceSpaceType;
        public long ChangeTime;
        public uint PoseValid;
        public XrPosef PoseInPreviousSpace;
    }

    /// <summary>The controllers' interaction profile changed (controllers on, off or swapped).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrEventDataInteractionProfileChanged
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Session;
    }

    /// <summary>The headset's display refresh rate changed (XR_FB_display_refresh_rate), Hz.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrEventDataDisplayRefreshRateChangedFB
    {
        public XrStructureType Type;
        public IntPtr Next;
        public float FromDisplayRefreshRate;
        public float ToDisplayRefreshRate;
    }

    /// <summary>A view's visibility mask changed (XR_KHR_visibility_mask): it has to be read again.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XrEventDataVisibilityMaskChangedKHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Session;
        public int ViewConfigurationType;
        public uint ViewIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrInteractionProfileState
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong InteractionProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSpaceLocation
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong LocationFlags;
        public XrPosef Pose;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XrActionSetCreateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public fixed byte ActionSetName[64];
        public fixed byte LocalizedActionSetName[128];
        public uint Priority;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct XrActionCreateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public fixed byte ActionName[64];
        public XrActionType ActionType;
        public uint CountSubactionPaths;
        public IntPtr SubactionPaths;
        public fixed byte LocalizedActionName[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionSuggestedBinding
    {
        public ulong Action;
        public ulong Binding;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrInteractionProfileSuggestedBinding
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong InteractionProfile;
        public uint CountSuggestedBindings;
        public IntPtr SuggestedBindings;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionSpaceCreateInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Action;
        public ulong SubactionPath;
        public XrPosef PoseInActionSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrSessionActionSetsAttachInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public uint CountActionSets;
        public IntPtr ActionSets;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActiveActionSet
    {
        public ulong ActionSet;
        public ulong SubactionPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionsSyncInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public uint CountActiveActionSets;
        public IntPtr ActiveActionSets;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionStateGetInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Action;
        public ulong SubactionPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionStateBoolean
    {
        public XrStructureType Type;
        public IntPtr Next;
        public uint CurrentState;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionStateFloat
    {
        public XrStructureType Type;
        public IntPtr Next;
        public float CurrentState;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionStateVector2f
    {
        public XrStructureType Type;
        public IntPtr Next;
        public XrVector2f CurrentState;
        public uint ChangedSinceLastSync;
        public long LastChangeTime;
        public uint IsActive;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrActionStatePose
    {
        public XrStructureType Type;
        public IntPtr Next;
        public uint IsActive;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrHapticActionInfo
    {
        public XrStructureType Type;
        public IntPtr Next;
        public ulong Action;
        public ulong SubactionPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrHapticVibration
    {
        public XrStructureType Type;
        public IntPtr Next;
        public long Duration;
        public float Frequency;
        public float Amplitude;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XrVisibilityMaskKHR
    {
        public XrStructureType Type;
        public IntPtr Next;
        public uint VertexCapacityInput;
        public uint VertexCountOutput;
        public IntPtr Vertices;
        public uint IndexCapacityInput;
        public uint IndexCountOutput;
        public IntPtr Indices;
    }

    /// <summary>Core entry points, exported by the Khronos loader (openxr_loader.dll).</summary>
    internal static unsafe class Xr
    {
        internal const string Loader = "openxr_loader";

        [DllImport(Loader)] public static extern int xrEnumerateInstanceExtensionProperties(IntPtr layerName, uint capacity, out uint count, XrExtensionProperties* properties);
        [DllImport(Loader)] public static extern int xrCreateInstance(ref XrInstanceCreateInfo createInfo, out ulong instance);
        [DllImport(Loader)] public static extern int xrDestroyInstance(ulong instance);
        [DllImport(Loader)] public static extern int xrResultToString(ulong instance, int value, byte* buffer);
        [DllImport(Loader)] public static extern int xrPollEvent(ulong instance, ref XrEventDataBuffer eventData);
        [DllImport(Loader)] public static extern int xrGetInstanceProcAddr(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);
        [DllImport(Loader)] public static extern int xrStringToPath(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string pathString, out ulong path);

        [DllImport(Loader)] public static extern int xrGetSystem(ulong instance, ref XrSystemGetInfo getInfo, out ulong systemId);
        [DllImport(Loader)] public static extern int xrGetSystemProperties(ulong instance, ulong systemId, ref XrSystemProperties properties);
        [DllImport(Loader)] public static extern int xrEnumerateViewConfigurationViews(ulong instance, ulong systemId, int viewConfigurationType, uint capacity, out uint count, XrViewConfigurationView* views);

        [DllImport(Loader)] public static extern int xrCreateSession(ulong instance, ref XrSessionCreateInfo createInfo, out ulong session);
        [DllImport(Loader)] public static extern int xrDestroySession(ulong session);
        [DllImport(Loader)] public static extern int xrBeginSession(ulong session, ref XrSessionBeginInfo beginInfo);
        [DllImport(Loader)] public static extern int xrEndSession(ulong session);
        [DllImport(Loader)] public static extern int xrRequestExitSession(ulong session);

        [DllImport(Loader)] public static extern int xrEnumerateReferenceSpaces(ulong session, uint capacity, out uint count, int* spaces);
        [DllImport(Loader)] public static extern int xrCreateReferenceSpace(ulong session, ref XrReferenceSpaceCreateInfo createInfo, out ulong space);
        [DllImport(Loader)] public static extern int xrDestroySpace(ulong space);
        [DllImport(Loader)] public static extern int xrLocateSpace(ulong space, ulong baseSpace, long time, ref XrSpaceLocation location);

        [DllImport(Loader)] public static extern int xrEnumerateSwapchainFormats(ulong session, uint capacity, out uint count, long* formats);
        [DllImport(Loader)] public static extern int xrCreateSwapchain(ulong session, ref XrSwapchainCreateInfo createInfo, out ulong swapchain);
        [DllImport(Loader)] public static extern int xrDestroySwapchain(ulong swapchain);
        [DllImport(Loader)] public static extern int xrEnumerateSwapchainImages(ulong swapchain, uint capacity, out uint count, XrSwapchainImageD3D11KHR* images);
        [DllImport(Loader, EntryPoint = "xrEnumerateSwapchainImages")] public static extern int xrEnumerateSwapchainImagesVulkan(ulong swapchain, uint capacity, out uint count, XrSwapchainImageVulkanKHR* images);
        [DllImport(Loader)] public static extern int xrAcquireSwapchainImage(ulong swapchain, ref XrBaseInfo acquireInfo, out uint index);
        [DllImport(Loader)] public static extern int xrWaitSwapchainImage(ulong swapchain, ref XrSwapchainImageWaitInfo waitInfo);
        [DllImport(Loader)] public static extern int xrReleaseSwapchainImage(ulong swapchain, ref XrBaseInfo releaseInfo);

        [DllImport(Loader)] public static extern int xrWaitFrame(ulong session, ref XrBaseInfo waitInfo, ref XrFrameState frameState);
        [DllImport(Loader)] public static extern int xrBeginFrame(ulong session, ref XrBaseInfo beginInfo);
        [DllImport(Loader)] public static extern int xrEndFrame(ulong session, ref XrFrameEndInfo endInfo);
        [DllImport(Loader)] public static extern int xrLocateViews(ulong session, ref XrViewLocateInfo locateInfo, ref XrViewState viewState, uint capacity, out uint count, XrView* views);

        [DllImport(Loader)] public static extern int xrPathToString(ulong instance, ulong path, uint capacity, out uint count, byte* buffer);
        [DllImport(Loader)] public static extern int xrDestroyActionSet(ulong actionSet);
        [DllImport(Loader)] public static extern int xrCreateActionSet(ulong instance, ref XrActionSetCreateInfo createInfo, out ulong actionSet);
        [DllImport(Loader)] public static extern int xrCreateAction(ulong actionSet, ref XrActionCreateInfo createInfo, out ulong action);
        [DllImport(Loader)] public static extern int xrSuggestInteractionProfileBindings(ulong instance, ref XrInteractionProfileSuggestedBinding suggestedBindings);
        [DllImport(Loader)] public static extern int xrAttachSessionActionSets(ulong session, ref XrSessionActionSetsAttachInfo attachInfo);
        [DllImport(Loader)] public static extern int xrCreateActionSpace(ulong session, ref XrActionSpaceCreateInfo createInfo, out ulong space);
        [DllImport(Loader)] public static extern int xrSyncActions(ulong session, ref XrActionsSyncInfo syncInfo);
        [DllImport(Loader)] public static extern int xrGetActionStateBoolean(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStateBoolean state);
        [DllImport(Loader)] public static extern int xrGetActionStateFloat(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStateFloat state);
        [DllImport(Loader)] public static extern int xrGetActionStateVector2f(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStateVector2f state);
        [DllImport(Loader)] public static extern int xrGetActionStatePose(ulong session, ref XrActionStateGetInfo getInfo, ref XrActionStatePose state);
        [DllImport(Loader)] public static extern int xrGetCurrentInteractionProfile(ulong session, ulong topLevelUserPath, ref XrInteractionProfileState interactionProfile);
        [DllImport(Loader)] public static extern int xrApplyHapticFeedback(ulong session, ref XrHapticActionInfo hapticActionInfo, ref XrHapticVibration hapticFeedback);

        // Extension entry points are not exported; they come from xrGetInstanceProcAddr.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetD3D11GraphicsRequirementsKHR(ulong instance, ulong systemId, ref XrGraphicsRequirementsD3D11KHR requirements);

        /// <summary>xrGetVulkanGraphicsRequirementsKHR, and xrGetVulkanGraphicsRequirements2KHR (same signature).</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetVulkanGraphicsRequirementsKHR(ulong instance, ulong systemId, ref XrGraphicsRequirementsVulkanKHR requirements);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetVulkanGraphicsDeviceKHR(ulong instance, ulong systemId, IntPtr vkInstance, out IntPtr vkPhysicalDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetVulkanGraphicsDevice2KHR(ulong instance, ref XrVulkanGraphicsDeviceGetInfoKHR getInfo, out IntPtr vkPhysicalDevice);

        /// <summary>xrGetVulkanInstanceExtensionsKHR and xrGetVulkanDeviceExtensionsKHR: the names, space separated.</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetVulkanExtensionsKHR(ulong instance, ulong systemId, uint capacity, out uint count, byte* buffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetVisibilityMaskKHR(ulong session, int viewConfigurationType, uint viewIndex, XrVisibilityMaskType type, ref XrVisibilityMaskKHR mask);

        /// <summary>xrGetAudioOutputDeviceGuidOculus(XrInstance, wchar_t buffer[XR_MAX_AUDIO_DEVICE_STR_SIZE_OCULUS]).</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetAudioOutputDeviceGuidOculus(ulong instance, IntPtr buffer);

        /// <summary>xrEnumerateDisplayRefreshRatesFB(session, capacity, out count, float* rates).</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EnumerateDisplayRefreshRatesFB(ulong session, uint capacity, out uint count, float* rates);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetDisplayRefreshRateFB(ulong session, out float rate);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int RequestDisplayRefreshRateFB(ulong session, float rate);

        public static T GetFunction<T>(ulong instance, string name) where T : Delegate
        {
            Check(xrGetInstanceProcAddr(instance, name, out IntPtr function), name, instance);
            return Marshal.GetDelegateForFunctionPointer<T>(function);
        }

        /// <summary>Throws on any failure code; success codes (including qualified ones such as FrameDiscarded) are returned.</summary>
        public static int Check(int result, string call, ulong instance = 0)
        {
            if (result < 0)
                throw new XrException(result, call, ResultName(instance, result));
            return result;
        }

        public static string ResultName(ulong instance, int result)
        {
            if (instance == 0)
                return result.ToString();
            byte* buffer = stackalloc byte[64];
            return xrResultToString(instance, result, buffer) >= 0 ? ReadString(buffer, 64) : result.ToString();
        }

        public static void WriteString(byte* destination, int capacity, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            int length = Math.Min(bytes.Length, capacity - 1);
            for (int i = 0; i < length; i++)
                destination[i] = bytes[i];
            destination[length] = 0;
        }

        public static string ReadString(byte* source, int capacity)
        {
            int length = 0;
            while (length < capacity && source[length] != 0)
                length++;
            return Encoding.UTF8.GetString(source, length);
        }
    }

    internal sealed class XrException : Exception
    {
        public int Result { get; }

        public XrException(int result, string call, string name)
            : base($"{call} failed: {name} ({result})")
        {
            Result = result;
        }
    }
}
