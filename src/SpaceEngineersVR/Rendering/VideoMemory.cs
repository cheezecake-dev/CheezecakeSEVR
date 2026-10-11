using System;
using System.Globalization;
using SharpDX.DXGI;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The video memory the game's GPU is using against what the operating system lets this process use (IDXGIAdapter3::
    /// QueryVideoMemoryInfo): "local" is the card's own memory, "non-local" the system memory the GPU reaches over the bus.
    /// A process over its local budget has its allocations moved to non-local memory by the video memory manager, which is
    /// slow, so usage near or past the budget explains a frame that takes seconds where the GPU timestamps cannot (those
    /// include any time the GPU sat idle). Read on the render thread for the Perf lines and on the stall watchdog's thread
    /// during a stall; the call takes no lock of ours and is safe from any thread.
    /// </summary>
    internal static class VideoMemory
    {
        private const double Gb = 1024.0 * 1024.0 * 1024.0;

        private static Adapter3 adapter;
        private static bool tried;
        private static string note;

        /// <summary>The adapter the game's D3D11 device runs on. Once, from the render thread.</summary>
        public static void Init(SharpDX.Direct3D11.Device device)
        {
            if (tried)
                return;
            tried = true;
            try
            {
                using (var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>())
                using (Adapter dxgiAdapter = dxgiDevice.Adapter)
                {
                    AdapterDescription description = dxgiAdapter.Description;
                    adapter = dxgiAdapter.QueryInterface<Adapter3>();
                    Log.Info($"Video memory: {description.Description.Trim()}, {description.DedicatedVideoMemory / Gb:0.00} GB dedicated; " + Describe());
                }
            }
            catch (Exception e)
            {
                adapter = null;
                note = e.GetType().Name + ": " + e.Message;
                Log.Warn("Video memory: could not query the adapter's video memory budget (" + note + "); the Perf lines go without it");
            }
        }

        /// <summary>"VRAM local 5.12 of 7.84 GB, non-local 0.20 of 15.90 GB" (in use of the budget). Never throws.</summary>
        public static string Describe()
        {
            Adapter3 a = adapter;
            if (a == null)
                return "VRAM n/a" + (note == null ? string.Empty : " (" + note + ")");
            try
            {
                QueryVideoMemoryInformation local = a.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                QueryVideoMemoryInformation nonLocal = a.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal);
                return string.Format(CultureInfo.InvariantCulture, "VRAM local {0:0.00} of {1:0.00} GB, non-local {2:0.00} of {3:0.00} GB",
                    local.CurrentUsage / Gb, local.Budget / Gb, nonLocal.CurrentUsage / Gb, nonLocal.Budget / Gb);
            }
            catch (Exception e)
            {
                adapter = null;
                note = e.GetType().Name + ": " + e.Message;
                return "VRAM n/a (" + note + ")";
            }
        }
    }
}
