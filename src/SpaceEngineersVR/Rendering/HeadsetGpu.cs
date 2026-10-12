using System;
using HarmonyLib;
using SharpDX.DXGI;
using SpaceEngineersVR.OpenXR;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The runtime names the GPU the headset session has to be created on. The game picks its adapter from the
    /// video settings (an ordinal into its adapter list, whose entries hold the DXGI Factory1 index); on a PC with more
    /// than one GPU that has to be overridden to the headset's, or the session cannot be created.
    /// </summary>
    internal static class HeadsetGpu
    {
        private static readonly Type PlatformRender = AccessTools.TypeByName("VRage.Platform.Windows.Render.MyPlatformRender");
        private static bool logged;

        /// <summary>On DXVK with the runtime's Vulkan extension the GPU is found from the runtime's Vulkan physical device, not the LUID.</summary>
        private static bool Vulkan => XrRuntime.HasVulkan && XrRuntime.HasSystem;

        public static void Patch(Harmony harmony)
        {
            if (XrRuntime.AdapterLuid == 0 && !Vulkan)
                return;
            harmony.Patch(AccessTools.Method(PlatformRender, "GetAdapter"),
                prefix: new HarmonyMethod(typeof(HeadsetGpu), nameof(UseHeadsetAdapter)));
        }

        private static void UseHeadsetAdapter(ref int adapterOrdinal)
        {
            if (Vulkan)
            {
                UseVulkanAdapter(ref adapterOrdinal);
                return;
            }
            int factoryIndex = FactoryIndex(XrRuntime.AdapterLuid);
            var adapters = (MyAdapterInfo[])AccessTools.Method(PlatformRender, "GetAdaptersList").Invoke(null, null);
            int ordinal = Array.FindIndex(adapters, a => a.AdapterDeviceId == factoryIndex);
            if (!logged)
            {
                logged = true;
                if (ordinal < 0)
                    Log.Error($"The headset's GPU (LUID {XrRuntime.AdapterLuid:X}, DXGI index {factoryIndex}) is not in the game's adapter list");
                else
                    Log.Info($"Drawing on the headset's GPU: {adapters[ordinal].Name} (adapter {ordinal}, video settings say {adapterOrdinal})");
            }
            if (ordinal >= 0)
                adapterOrdinal = ordinal;
        }

        /// <summary>
        /// DXVK: the adapter whose Vulkan physical device is the one the runtime names for DXVK's instance
        /// (xrGetVulkanGraphicsDeviceKHR). A failure keeps the game's own choice.
        /// </summary>
        private static void UseVulkanAdapter(ref int adapterOrdinal)
        {
            int factoryIndex = -1;
            IntPtr physicalDevice = IntPtr.Zero;
            try
            {
                using (var factory = new Factory1())
                {
                    IntPtr instance = DxvkInterop.FactoryVulkanInstance(factory.NativePointer);
                    string problem = XrRuntime.VulkanInstanceProblem(instance);
                    if (problem != null)
                    {
                        if (!logged)
                            Log.Error(problem);
                        logged = true;
                        return;
                    }
                    if (instance != IntPtr.Zero)
                    {
                        physicalDevice = XrRuntime.VulkanGraphicsDevice(instance);
                        for (int i = 0; i < factory.GetAdapterCount1() && factoryIndex < 0; i++)
                            using (Adapter1 adapter = factory.GetAdapter1(i))
                                if (DxvkInterop.AdapterPhysicalDevice(adapter.NativePointer) == physicalDevice)
                                    factoryIndex = i;
                    }
                }
            }
            catch (Exception e)
            {
                if (!logged)
                    Log.Error(e, "Could not find the headset's GPU from the runtime's Vulkan physical device; the game picks its own");
                logged = true;
                return;
            }

            var adapters = (MyAdapterInfo[])AccessTools.Method(PlatformRender, "GetAdaptersList").Invoke(null, null);
            int ordinal = factoryIndex < 0 ? -1 : Array.FindIndex(adapters, a => a.AdapterDeviceId == factoryIndex);
            if (!logged)
            {
                logged = true;
                if (ordinal < 0)
                    Log.Error($"The headset's GPU (Vulkan physical device {physicalDevice.ToInt64():X}, DXGI index {factoryIndex}) is not in the game's adapter list");
                else
                    Log.Info($"Drawing on the headset's GPU: {adapters[ordinal].Name} (adapter {ordinal}, video settings say {adapterOrdinal}), " +
                             $"from the runtime's Vulkan physical device {physicalDevice.ToInt64():X}");
            }
            if (ordinal >= 0)
                adapterOrdinal = ordinal;
        }

        private static int FactoryIndex(long luid)
        {
            using (var factory = new Factory1())
            {
                for (int i = 0; i < factory.GetAdapterCount1(); i++)
                    using (Adapter1 adapter = factory.GetAdapter1(i))
                        if (adapter.Description1.Luid == luid)
                            return i;
            }
            return -1;
        }
    }
}
