// InteractionProfile: an OpenXR interaction profile's path and the plugin's key for it (no game types; the tests use it).
using System;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// OpenXR paths an interaction profile <c>/interaction_profiles/oculus/touch_controller</c>; the plugin keys what it keeps
    /// per controller (bindings, drawings, grip fits) by the path without that prefix, <c>oculus/touch_controller</c>. The one
    /// place either is turned into the other.
    /// </summary>
    internal static class InteractionProfile
    {
        /// <summary>What every interaction profile's path starts with.</summary>
        public const string Prefix = "/interaction_profiles/";

        /// <summary>The key for a profile, from its path or a key already; null for null or empty.</summary>
        public static string Key(string profile)
        {
            if (string.IsNullOrEmpty(profile))
                return null;
            string key = profile.StartsWith(Prefix, StringComparison.Ordinal) ? profile.Substring(Prefix.Length) : profile;
            return key.Length == 0 ? null : key;
        }

        /// <summary>The path for a profile, from its key or a path already (anything starting "/"); null for null or empty.</summary>
        public static string Path(string profile)
        {
            if (string.IsNullOrEmpty(profile))
                return null;
            return profile.StartsWith("/", StringComparison.Ordinal) ? profile : Prefix + profile;
        }
    }
}
