using System.Collections.Generic;
using VRage.Plugins;

namespace SevrSamplePlugin
{
    /// <summary>
    /// The plugin's entry point. Pulsar makes one and calls Update once a frame on the game thread, at the main menu too.
    /// The VR plugin is looked for from Init, once every plugin has been made, never from the constructor.
    /// </summary>
    public class Plugin : IPlugin
    {
        /// <summary>
        /// Pulsar calls this once, right after making the plugin, with each Asset of SevrSamplePlugin.xml: its Name, and the
        /// full path Pulsar put the file at. Pulsar finds it by its name and parameter, on this class itself (not a base class).
        /// This is how a plugin finds a file it ships, such as an icon.
        /// </summary>
        public void LoadAssets(IReadOnlyDictionary<string, string> assets)
        {
            string icon;
            if (assets != null && assets.TryGetValue("icon", out icon))
                SevrSample.IconPath = icon;
        }

        /// <summary>The game calls Init once every plugin is made, on the game thread: the VR plugin can be found from here.</summary>
        public void Init(object gameInstance)
        {
            SevrSample.Connect();
        }

        public void Update()
        {
            SevrSample.Update();
        }

        public void Dispose()
        {
            SevrSample.Disconnect();
        }
    }
}
