// ApiState: what SevrApi's state members answer, from what the plugin reads (no game types; tests/ApiTests runs it).
using System;

namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The answers of SevrApi's state members that are more than a value read: each gives its "no VR" value while VR is not
    /// active (docs/MOD_API.md, "State, poses and haptics"), as the plugin's other queries do.
    /// </summary>
    internal static class ApiState
    {
        /// <summary>How OpenXR paths an interaction profile; the binding system keeps profiles without it (ControllerBindings.Key).</summary>
        public const string ProfilePrefix = "/interaction_profiles/";

        /// <summary>
        /// "ControllerProfile": the interaction profile of the controller in a hand as OpenXR paths it
        /// ("/interaction_profiles/oculus/touch_controller"), from the binding system's name for it, <paramref name="key"/>,
        /// with or without the prefix. Empty while VR is not active (<paramref name="active"/>), or with no controller.
        /// </summary>
        public static string ControllerProfile(bool active, string key)
        {
            if (!active || string.IsNullOrEmpty(key))
                return string.Empty;
            return key.StartsWith("/", StringComparison.Ordinal) ? key : ProfilePrefix + key;
        }
    }
}
