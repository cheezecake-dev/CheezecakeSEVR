// XrInput, interaction-profile bindings: which controller profiles are offered to the runtime and which paths each binds.

using System;
using System.Collections.Generic;

namespace SpaceEngineersVR.OpenXR
{
    internal sealed unsafe partial class XrInput
    {
        // Controllers whose profile comes with an extension. The instance is OpenXR 1.0, where these profiles are not core:
        // each is there only on an instance with its extension. XrRuntime enables one only when the runtime lists it, and its
        // profile is suggested only when it is enabled, so a runtime without it never sees the profile, and no other profile
        // depends on it.

        private const string PicoExtension = "XR_BD_controller_interaction";
        private const string PicoUltraExtension = "XR_BD_ultra_controller_interaction";
        private const string ReverbExtension = "XR_EXT_hp_mixed_reality_controller";
        private const string SteamFrameExtension = "XR_VALVE_frame_controller_interaction";

        private static readonly string[] ProfileExtensions =
            { PicoExtension, PicoUltraExtension, ReverbExtension, SteamFrameExtension };

        /// <summary>Those of <see cref="ProfileExtensions"/> enabled on the instance.</summary>
        private static readonly HashSet<string> enabledProfileExtensions = new HashSet<string>();

        /// <summary>
        /// While the instance is made (<see cref="XrRuntime.CreateInstance"/>): adds to <paramref name="extensions"/> each
        /// controller extension the runtime lists in <paramref name="available"/>, and remembers them for the bindings.
        /// </summary>
        public static void AddProfileExtensions(HashSet<string> available, List<string> extensions)
        {
            enabledProfileExtensions.Clear();
            foreach (string extension in ProfileExtensions)
            {
                if (!available.Contains(extension))
                    continue;
                enabledProfileExtensions.Add(extension);
                extensions.Add(extension);
            }
        }

        /// <summary>
        /// Each profile binds only what its controller has: a controller without a thumbstick gets no stick, without
        /// A/B/X/Y no buttons. A boolean control (a click) bound to a float action reads 0 or 1. Returns the profiles
        /// the runtime took; one it does not know, or whose extension it does not offer, is skipped, not fatal.
        /// </summary>
        /// <remarks>
        /// Every profile is suggested on its own, and within one a binding the runtime refuses costs only that control
        /// (<see cref="Suggest"/>), so the palm pose and the touch sensors, the extras, can never cost the controllers.
        /// Every controller takes the Quest's layout as far as it has the controls: the same hand and the same role.
        /// </remarks>
        private List<string> SuggestBindings()
        {
            var taken = new List<string>();
            var unoffered = new List<string>();

            // Quest over Link. X, Y and Menu are on the left controller, A and B on the right (the spec's oculus/touch_controller
            // has menu/click on the left only, and a system button on the right that apps cannot take). Every path here is in
            // that profile's list, and the runtime validates them in xrSuggestInteractionProfileBindings.
            var touch = new List<XrActionSuggestedBinding>();
            var touchExtra = new List<XrActionSuggestedBinding>();
            BindTouchLayout(touch, touchExtra, menuBothHands: false, touchSensors: true, thumbrest: true);
            Offer(taken, unoffered, "/interaction_profiles/oculus/touch_controller", null, touch, touchExtra);

            // Quest 3 and 3S (Touch Plus) and Quest Pro (Touch Pro) controllers are left on oculus/touch_controller: a runtime
            // given no binding for meta/touch_controller_plus or facebook/touch_controller_pro uses this one for them, which is
            // the path the Quest 3 was tested on. Their own profiles would bring the same buttons and nothing more, only the
            // chance of a pose or sensor the runtime takes differently.

            // PICO 4 and PICO Neo3 (one extension) and PICO 4 Ultra: the Quest's controls, no thumbrest sensor. The Neo3 has a
            // Menu button on each controller, the others on the left only.
            var pico4 = new List<XrActionSuggestedBinding>();
            var pico4Extra = new List<XrActionSuggestedBinding>();
            BindTouchLayout(pico4, pico4Extra, menuBothHands: false, touchSensors: true, thumbrest: false);
            Offer(taken, unoffered, "/interaction_profiles/bytedance/pico4_controller", PicoExtension, pico4, pico4Extra);

            var picoNeo3 = new List<XrActionSuggestedBinding>();
            var picoNeo3Extra = new List<XrActionSuggestedBinding>();
            BindTouchLayout(picoNeo3, picoNeo3Extra, menuBothHands: true, touchSensors: true, thumbrest: false);
            Offer(taken, unoffered, "/interaction_profiles/bytedance/pico_neo3_controller", PicoExtension, picoNeo3, picoNeo3Extra);

            var picoUltra = new List<XrActionSuggestedBinding>();
            var picoUltraExtra = new List<XrActionSuggestedBinding>();
            BindTouchLayout(picoUltra, picoUltraExtra, menuBothHands: false, touchSensors: true, thumbrest: false);
            Offer(taken, unoffered, "/interaction_profiles/bytedance/pico_ultra_controller_bd", PicoUltraExtension, picoUltra, picoUltraExtra);

            // HP Reverb G2: the Quest's controls with a Menu button on each controller, and no touch sensors.
            var reverb = new List<XrActionSuggestedBinding>();
            var reverbExtra = new List<XrActionSuggestedBinding>();
            BindTouchLayout(reverb, reverbExtra, menuBothHands: true, touchSensors: false, thumbrest: false);
            Offer(taken, unoffered, "/interaction_profiles/hp/mixed_reality_controller", ReverbExtension, reverb, reverbExtra);

            // Index has A and B on both hands (the left ones act as X and Y) and no menu button apps can use.
            var index = new List<XrActionSuggestedBinding>();
            var indexExtra = new List<XrActionSuggestedBinding>();
            BindHands(index, indexExtra, "trigger/value", "squeeze/value", "thumbstick", "thumbstick/click");
            Bind(index, aAction, "/user/hand/right/input/a/click");
            Bind(index, bAction, "/user/hand/right/input/b/click");
            Bind(index, xAction, "/user/hand/left/input/a/click");
            Bind(index, yAction, "/user/hand/left/input/b/click");
            Offer(taken, unoffered, "/interaction_profiles/valve/index_controller", null, index, indexExtra);

            // Steam Frame: a gamepad split in two. Where the Quest's left controller has X, Y and Menu, the Frame's has a D-pad
            // and View: View is Menu (as SteamVR maps the Quest's Menu when a game has no Frame bindings), and the D-pad's
            // down and up are X and Y, as X sits below Y on the Quest. The right controller's A and B are A and B. Its X and Y,
            // its Menu, both bumpers and the D-pad's left and right are left free: they have no control of their own here,
            // and X and Y on the right hand would be the left hand's buttons. The paths are Valve's (Steamworks, Steam Frame
            // Input); the profile is not in the Khronos registry.
            var frame = new List<XrActionSuggestedBinding>();
            var frameExtra = new List<XrActionSuggestedBinding>();
            BindHands(frame, frameExtra, "trigger/value", "squeeze/value", "thumbstick", "thumbstick/click");
            Bind(frame, xAction, "/user/hand/left/input/dpad_down/click");
            Bind(frame, yAction, "/user/hand/left/input/dpad_up/click");
            Bind(frame, menuAction, "/user/hand/left/input/view/click");
            Bind(frame, aAction, "/user/hand/right/input/a/click");
            Bind(frame, bAction, "/user/hand/right/input/b/click");
            // The thumb's sensors: on any button of the left's D-pad or the right's four, the thumb is on the controller's face.
            for (int h = 0; h < 2; h++)
            {
                string hand = "/user/hand/" + HandNames[h];
                Bind(frameExtra, triggerTouchAction, hand + "/input/trigger/touch");
                Bind(frameExtra, stickTouchAction, hand + "/input/thumbstick/touch");
            }
            Bind(frameExtra, xTouchAction, "/user/hand/left/input/dpad_down/touch");
            Bind(frameExtra, xTouchAction, "/user/hand/left/input/dpad_left/touch");
            Bind(frameExtra, yTouchAction, "/user/hand/left/input/dpad_up/touch");
            Bind(frameExtra, yTouchAction, "/user/hand/left/input/dpad_right/touch");
            Bind(frameExtra, aTouchAction, "/user/hand/right/input/a/touch");
            Bind(frameExtra, aTouchAction, "/user/hand/right/input/x/touch");
            Bind(frameExtra, bTouchAction, "/user/hand/right/input/b/touch");
            Bind(frameExtra, bTouchAction, "/user/hand/right/input/y/touch");
            Offer(taken, unoffered, "/interaction_profiles/valve/frame_controller_valve", SteamFrameExtension, frame, frameExtra);

            // Vive wands: the trackpad and its click stand in for the thumbstick and its click, the grip buttons are clicks. The wand
            // has no A, B, X or Y, so those come from the trackpad and the Menu buttons in ReadHand (WandLayout). The trackpad's touch
            // sensor is the thumb on the controller, for the hand's pose.
            var vive = new List<XrActionSuggestedBinding>();
            var viveExtra = new List<XrActionSuggestedBinding>();
            BindHands(vive, viveExtra, "trigger/value", "squeeze/click", "trackpad", "trackpad/click");
            BindMenuBothHands(vive);
            for (int h = 0; h < 2; h++)
                Bind(viveExtra, stickTouchAction, "/user/hand/" + HandNames[h] + "/input/trackpad/touch");
            Offer(taken, unoffered, WandLayout.Profile, null, vive, viveExtra);

            var windowsMixedReality = new List<XrActionSuggestedBinding>();
            var windowsMixedRealityExtra = new List<XrActionSuggestedBinding>();
            BindHands(windowsMixedReality, windowsMixedRealityExtra, "trigger/value", "squeeze/click", "thumbstick", "thumbstick/click");
            BindMenuBothHands(windowsMixedReality);
            Offer(taken, unoffered, "/interaction_profiles/microsoft/motion_controller", null, windowsMixedReality, windowsMixedRealityExtra);

            // The fallback every runtime has: one select button (as the trigger), a menu button, the poses.
            var simple = new List<XrActionSuggestedBinding>();
            var simpleExtra = new List<XrActionSuggestedBinding>();
            BindHands(simple, simpleExtra, "select/click", null, null, null);
            BindMenuBothHands(simple);
            Offer(taken, unoffered, "/interaction_profiles/khr/simple_controller", null, simple, simpleExtra);

            if (unoffered.Count > 0)
                Log.Info("OpenXR controllers this runtime does not offer, so not bound: " + string.Join(", ", unoffered));
            return taken;
        }

        /// <summary>
        /// The Quest's layout, which the Touch Plus, Touch Pro, PICO and HP Reverb G2 controllers share: X and Y on the
        /// left, A and B on the right, Menu on the left (on both with <paramref name="menuBothHands"/>). The touch sensors go
        /// in <paramref name="extra"/>, all but the thumbrest's without <paramref name="thumbrest"/>.
        /// </summary>
        private void BindTouchLayout(List<XrActionSuggestedBinding> bindings, List<XrActionSuggestedBinding> extra, bool menuBothHands, bool touchSensors, bool thumbrest)
        {
            BindHands(bindings, extra, "trigger/value", "squeeze/value", "thumbstick", "thumbstick/click");
            Bind(bindings, xAction, "/user/hand/left/input/x/click");
            Bind(bindings, yAction, "/user/hand/left/input/y/click");
            if (menuBothHands)
                BindMenuBothHands(bindings);
            else
                Bind(bindings, menuAction, "/user/hand/left/input/menu/click");
            Bind(bindings, aAction, "/user/hand/right/input/a/click");
            Bind(bindings, bAction, "/user/hand/right/input/b/click");
            if (!touchSensors)
                return;
            // The capacitive sensors: a finger resting on a control.
            for (int h = 0; h < 2; h++)
            {
                string hand = "/user/hand/" + HandNames[h];
                Bind(extra, triggerTouchAction, hand + "/input/trigger/touch");
                Bind(extra, stickTouchAction, hand + "/input/thumbstick/touch");
                if (thumbrest)
                    Bind(extra, thumbrestTouchAction, hand + "/input/thumbrest/touch");
            }
            Bind(extra, xTouchAction, "/user/hand/left/input/x/touch");
            Bind(extra, yTouchAction, "/user/hand/left/input/y/touch");
            Bind(extra, aTouchAction, "/user/hand/right/input/a/touch");
            Bind(extra, bTouchAction, "/user/hand/right/input/b/touch");
        }

        /// <summary>
        /// Poses, haptics and the analog controls every hand has; a null path leaves that control unbound. The palm pose,
        /// which XR_EXT_palm_pose adds to every profile with hands, goes in <paramref name="extra"/>.
        /// </summary>
        private void BindHands(List<XrActionSuggestedBinding> bindings, List<XrActionSuggestedBinding> extra, string trigger, string squeeze, string stick, string stickClick)
        {
            for (int h = 0; h < 2; h++)
            {
                string hand = "/user/hand/" + HandNames[h];
                Bind(bindings, gripAction, hand + "/input/grip/pose");
                Bind(bindings, aimAction, hand + "/input/aim/pose");
                if (palmAction != 0)
                    Bind(extra, palmAction, hand + "/input/palm_ext/pose");
                Bind(bindings, hapticAction, hand + "/output/haptic");
                Bind(bindings, triggerAction, hand + "/input/" + trigger);
                if (squeeze != null)
                    Bind(bindings, squeezeAction, hand + "/input/" + squeeze);
                if (stick != null)
                    Bind(bindings, stickAction, hand + "/input/" + stick);
                if (stickClick != null)
                    Bind(bindings, stickClickAction, hand + "/input/" + stickClick);
            }
        }

        private void BindMenuBothHands(List<XrActionSuggestedBinding> bindings)
        {
            for (int h = 0; h < 2; h++)
                Bind(bindings, menuAction, "/user/hand/" + HandNames[h] + "/input/menu/click");
        }

        /// <summary>A path the runtime cannot make (xrStringToPath fails) is left out with a warning, never thrown: it costs only that control.</summary>
        private static void Bind(List<XrActionSuggestedBinding> bindings, ulong action, string path)
        {
            int result = Xr.xrStringToPath(XrRuntime.Instance, path, out ulong binding);
            if (result < 0)
            {
                Log.Warn($"OpenXR binding {path} left out: xrStringToPath failed: {Xr.ResultName(XrRuntime.Instance, result)} ({result})");
                return;
            }
            bindings.Add(new XrActionSuggestedBinding { Action = action, Binding = binding });
        }

        /// <summary>
        /// Suggests the profile when it is core (<paramref name="extension"/> null) or its extension is enabled, and adds
        /// it to <paramref name="taken"/> when the runtime takes it, or to <paramref name="unoffered"/> when the extension
        /// is not there. Never throws: one profile can never cost another.
        /// </summary>
        private static void Offer(List<string> taken, List<string> unoffered, string profile, string extension,
                                  List<XrActionSuggestedBinding> bindings, List<XrActionSuggestedBinding> extra)
        {
            string name = profile.Replace("/interaction_profiles/", string.Empty);
            if (extension != null && !enabledProfileExtensions.Contains(extension))
            {
                unoffered.Add($"{name} ({extension})");
                return;
            }
            try
            {
                if (Suggest(profile, bindings, extra))
                    taken.Add(name);
            }
            catch (Exception e)
            {
                Log.Warn($"The bindings for {profile} could not be suggested: {e.Message}");
            }
        }

        /// <summary>
        /// The profile's bindings with <paramref name="extra"/>. If the runtime refuses them it is offered them again one
        /// at a time, keeping each it takes, so a path it does not know costs only that control; a later suggestion for
        /// the same profile replaces the earlier one. Refusing the first on its own (the left grip pose, which every
        /// controller has) is refusing the profile. Logs one line when anything was refused.
        /// </summary>
        /// <returns>True when the runtime took bindings for the profile.</returns>
        private static bool Suggest(string profile, List<XrActionSuggestedBinding> bindings, List<XrActionSuggestedBinding> extra)
        {
            var all = new List<XrActionSuggestedBinding>(bindings);
            all.AddRange(extra);
            if (all.Count == 0)
                return false;
            if (TrySuggest(profile, all, out string refused))
                return true;

            var kept = new List<XrActionSuggestedBinding>(all.Count);
            var without = new List<string>();
            bool lastTaken = false;
            foreach (XrActionSuggestedBinding binding in all)
            {
                kept.Add(binding);
                lastTaken = TrySuggest(profile, kept, out string error);
                if (lastTaken)
                    continue;
                kept.RemoveAt(kept.Count - 1);
                if (kept.Count == 0)
                {
                    Log.Warn($"The runtime did not take the bindings for {profile}: {refused}");
                    return false;
                }
                without.Add($"{PathName(binding.Binding)} ({error})");
            }
            // The refused last one may have replaced the set the runtime had taken: leave it the bindings it took.
            if (!lastTaken && !TrySuggest(profile, kept, out string again))
            {
                Log.Warn($"The runtime did not take the bindings for {profile}: {refused}; offered again without the ones it refused, {again}");
                return false;
            }
            Log.Warn($"The runtime did not take all the bindings for {profile} ({refused}); it took {kept.Count} of {all.Count}, " +
                     $"without {(without.Count > 0 ? string.Join(", ", without) : "none, one at a time")}");
            return true;
        }

        private static bool TrySuggest(string profile, List<XrActionSuggestedBinding> bindings, out string error)
        {
            XrActionSuggestedBinding[] array = bindings.ToArray();
            error = null;
            try
            {
                fixed (XrActionSuggestedBinding* ptr = array)
                {
                    var info = new XrInteractionProfileSuggestedBinding
                    {
                        Type = XrStructureType.InteractionProfileSuggestedBinding,
                        InteractionProfile = StringToPath(profile),
                        CountSuggestedBindings = (uint)array.Length,
                        SuggestedBindings = (IntPtr)ptr,
                    };
                    Xr.Check(Xr.xrSuggestInteractionProfileBindings(XrRuntime.Instance, ref info), "xrSuggestInteractionProfileBindings", XrRuntime.Instance);
                }
                return true;
            }
            catch (XrException e)
            {
                error = e.Message;
                return false;
            }
        }

        private static ulong StringToPath(string path)
        {
            Xr.Check(Xr.xrStringToPath(XrRuntime.Instance, path, out ulong value), "xrStringToPath " + path, XrRuntime.Instance);
            return value;
        }
    }
}
