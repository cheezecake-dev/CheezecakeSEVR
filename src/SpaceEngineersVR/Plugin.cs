using System;
using System.Collections.Generic;
using System.IO;
using SpaceEngineersVR.OpenXR;
using SpaceEngineersVR.Rendering;
using VRage.Plugins;

#if NETCOREAPP && !LINUX
// Pulsar defines LINUX on every platform but Windows, so a CoreCLR build without it is a Windows build.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif

namespace SpaceEngineersVR
{
    /// <summary>
    /// Game plugin entry point. Normally we are bootstrapped already, before the game started: by SpaceEngineersVR.Launcher,
    /// or under Pulsar by <see cref="global::Preloader.Finish"/>. If neither ran, set up now (too late for the render
    /// device, which <see cref="Bootstrap"/> logs).
    /// </summary>
    public class Plugin : IPlugin
    {
        /// <summary>The name of the openxr_loader.dll asset in SpaceEngineersVR.xml.</summary>
        private const string LoaderAsset = "openxr_loader";

        private readonly FeatureGuard updateGuard = new FeatureGuard("Patches");

        public Plugin()
        {
            Bootstrap.Initialize(Environment.GetCommandLineArgs(), beforeGame: false, "a plugin loader");
        }

        /// <summary>
        /// Pulsar calls this after the constructor with each asset's name and the full path it put the asset at
        /// (Pulsar Legacy/Loader/PluginInstance.cs LoadAssets). The loader was already loaded before the game started;
        /// this records whether it was the file Pulsar delivered.
        /// </summary>
        public void LoadAssets(IReadOnlyDictionary<string, string> assets)
        {
            if (!assets.TryGetValue(LoaderAsset, out string assetPath))
            {
                Log.Warn($"Pulsar delivered no '{LoaderAsset}' asset");
                return;
            }

            // A file asset is the loader itself. An extracted archive (Url with Extract="true") is given as the folder it
            // was extracted into, the plugin's Bin folder, with the loader at x64\bin\ in it.
            string delivered = Directory.Exists(assetPath) ? XrRuntime.FindLoader(assetPath) : assetPath;
            Log.Info($"Pulsar asset {LoaderAsset}: {assetPath}" + (delivered != assetPath ? $" (the loader in it: {delivered})" : ""));
            string loaded = XrRuntime.LoaderPath;
            if (loaded != null && !string.Equals(Path.GetFullPath(loaded), Path.GetFullPath(delivered), StringComparison.OrdinalIgnoreCase))
                Log.Warn($"The OpenXR loader in use ({loaded}) is not the one Pulsar delivered; check Placement=\"Bin\" in the plugin data file");
        }

        public void Init(object gameInstance)
        {
            Log.Info("Plugin.Init");
        }

        /// <summary>
        /// Called by the game once a frame, on the game thread: patches what changes play once the headset is first found,
        /// turns the picture to the game's while the headset is away and back when it returns (off for now:
        /// <c>StereoPatches.FlatWhenAway</c>), and shows the notice about patches that failed. Before that, and whatever becomes of
        /// it, the eyes come back after a failure when the world allows (<see cref="MenuFallback"/>). Offers the mod API while a
        /// world runs (<see cref="Api.SevrApi"/>).
        /// </summary>
        public void Update()
        {
            MenuFallback.Update();
            if (!updateGuard.On)
                return;
            try
            {
                StereoPatches.PatchForHeadset();
                StereoPatches.FollowHeadset();
                PatchRegistry.Update();
                Api.SevrApi.Update();
            }
            catch (Exception e)
            {
                updateGuard.Failed(e, "Patching for the headset, following it, or the patch notice failed");
            }
        }

        public void Dispose()
        {
            Log.Info("Plugin.Dispose");
            // The game is closing: have the last lines on disk before anything else goes.
            Log.Flush();
        }
    }
}
