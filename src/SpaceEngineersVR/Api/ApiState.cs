// ApiState: what SevrApi's state members answer, from what the plugin reads (no game types; tests/ApiTests runs it).
namespace SpaceEngineersVR.Api
{
    /// <summary>
    /// The answers of SevrApi's state members that are more than a value read: each gives its "no VR" value while VR is not
    /// active (docs/MOD_API.md, "State, poses and haptics"), as the plugin's other queries do.
    /// </summary>
    internal static class ApiState
    {
        /// <summary>
        /// "ControllerProfile": the interaction profile of the controller in a hand as OpenXR paths it
        /// ("/interaction_profiles/oculus/touch_controller"), from the binding system's name for it, <paramref name="key"/>,
        /// with or without the prefix. Empty while VR is not active (<paramref name="active"/>), or with no controller.
        /// </summary>
        public static string ControllerProfile(bool active, string key) =>
            active ? OpenXR.InteractionProfile.Path(key) ?? string.Empty : string.Empty;
    }
}
