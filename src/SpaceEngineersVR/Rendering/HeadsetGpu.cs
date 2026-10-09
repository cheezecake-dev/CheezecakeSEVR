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

        public static void Patch(Harmony harmony)
        {
            if (XrRuntime.AdapterLuid == 0)
                return;
            harmony.Patch(AccessTools.Method(PlatformRender, "GetAdapter"),
                prefix: new HarmonyMethod(typeof(HeadsetGpu), nameof(UseHeadsetAdapter)));
        }

        private static void UseHeadsetAdapter(ref int adapterOrdinal)
        {
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
