// FocusReturn on Linux: not available, so Start only says so when the variable is set.
#if LINUX
using System;

namespace SpaceEngineersVR.Input
{
    internal static partial class FocusReturn
    {
        /// <summary>Does nothing on Linux: handing the foreground window back goes through user32, which Linux does not have.</summary>
        public static void Start()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
                Log.Info($"Focus return is not available on Linux ({Variable} ignored)");
        }
    }
}
#endif
