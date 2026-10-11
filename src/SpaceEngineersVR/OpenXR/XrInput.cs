using System;
using System.Collections.Generic;
using SpaceEngineersVR.Input;
using SpaceEngineersVR.Rendering;
using SpaceEngineersVR.Tracking;
using VRageMath;

namespace SpaceEngineersVR.OpenXR
{
    /// <summary>
    /// The headset's controllers as <see cref="VRInput"/> hands: one action set, bound for the common controllers,
    /// synced and located once per headset frame on the render thread, in the space the head is in. The game thread
    /// reads the latest snapshot, whose poses are predicted for when the frame the game draws from them is shown
    /// (see Prediction below); the render thread's own placements read them at this frame's display time (<see cref="ReadNow"/>).
    /// Also takes the runtime's recentre, which belongs to the head but arrives with the same events.
    /// </summary>
    /// <remarks>
    /// Controls (action: where it goes in <see cref="HandState"/>):
    /// grip_pose, aim_pose and palm_pose per hand: Grip, Aim, Palm (palm_pose only with XR_EXT_palm_pose). trigger and
    /// squeeze per hand (float): Trigger, Squeeze, and the Trigger and Grip button bits. thumbstick (vector2f) and
    /// thumbstick_click per hand: Stick, StickClick. button_a, button_b (right hand) and button_x, button_y (left hand):
    /// A, B, X, Y. menu per hand: Menu. haptic output per hand. The Index controller has A/B on both hands, so its left
    /// A/B are X/Y; the Steam Frame's left controller has a D-pad and View, so its D-pad down and up are X and Y and View
    /// is Menu. Touch sensors, on the controllers that have them (the Quest family, PICO, Steam Frame): trigger_touch,
    /// thumbstick_touch, thumbrest_touch per hand and touch_a/b/x/y: Touch, and TouchSensors for the ones bound.
    /// </remarks>
    internal sealed unsafe class XrInput : IVRInputSource
    {
        // The analog bits press and release at different values, so a trigger resting at the edge does not flicker.
        private const float PressAt = 0.6f, ReleaseAt = 0.4f;
        private const float MaxHapticSeconds = 10f;

        // ---- Prediction ----
        // The game reads the hands published in headset frame M, builds its frame from them, and the renderer shows that
        // frame in a later headset frame N, which the eyes are located for (with the late head correction, CameraHeadLink).
        // The hands in it are right when they are located for N's display time, not M's. N - M is not fixed (the game
        // runs at its own rate, and a headset frame with no new game frame shows the old one again), so it is measured:
        // each frame, this frame's display time less the frame time of the hands in the camera it draws (CameraHeadLink.HandsFrameTime),
        // averaged. Until a sample arrives the lead is one display period.

        /// <summary>Weight of each new sample in the averaged lead (about a second to settle at 72-90 Hz).</summary>
        private const double LeadSmoothing = 0.02;

        /// <summary>Samples further behind than this many display periods are not a drawn game frame (a pause, a load) and are skipped.</summary>
        private const int MaxLeadPeriods = 4;

        /// <summary>The measured lead is logged once, after this many samples.</summary>
        private const int LeadSamplesToLog = 300;

        /// <summary>RenderDebug HandIKLead=0: the game's hands at the display time of the frame they are read in, as before, to compare.</summary>
        public static bool Lead = true;

        private static double leadNs;
        private static long leadSampleMin = long.MaxValue, leadSampleMax;
        private static int leadSamples;
        private static bool leadStartLogged;

        private static readonly string[] HandNames = { "left", "right" };

        private static XrInput current;

        // Recentre. Static because it does not depend on the controllers being set up.
        private static bool recentrePending;
        private static long recentreTime;

        private readonly ulong session;
        private readonly ulong baseSpace;
        private readonly ulong[] handPaths = new ulong[2];
        private readonly ulong[] gripSpaces = new ulong[2];
        private readonly ulong[] aimSpaces = new ulong[2];
        private readonly ulong[] palmSpaces = new ulong[2];
        private ulong actionSet;
        private ulong gripAction, aimAction, palmAction, triggerAction, squeezeAction, stickAction, stickClickAction;
        private ulong aAction, bAction, xAction, yAction, menuAction, hapticAction;
        private ulong triggerTouchAction, stickTouchAction, thumbrestTouchAction, aTouchAction, bTouchAction, xTouchAction, yTouchAction;

        // Render thread only: the hands as last read, which keeps the last good poses while a controller is lost.
        // working: for the game (predicted); workingNow: at this frame's display time.
        private readonly HandState[] working = new HandState[2];
        private readonly HandState[] workingNow = new HandState[2];
        private bool failed, hapticsFailed, loggedProfiles, sourcesPending = true;

        // The interaction profile last logged per hand; MaxValue until the first is read, so that one is logged too.
        private readonly ulong[] loggedProfile = { ulong.MaxValue, ulong.MaxValue };

        // Shared with the game thread.
        private readonly object gate = new object();
        private readonly HandState[] published = new HandState[2];
        private readonly HandState[] publishedNow = new HandState[2];
        private readonly float[] hapticAmplitude = new float[2];
        private readonly float[] hapticSeconds = new float[2];
        private volatile bool active;

        private XrInput(ulong session, ulong baseSpace)
        {
            this.session = session;
            this.baseSpace = baseSpace;
        }

        /// <summary>The session is running and focused: the hands below are being read every frame.</summary>
        public bool Active => active;

        public void Read(out HandState left, out HandState right)
        {
            lock (gate)
            {
                left = published[0];
                right = published[1];
            }
        }

        public void ReadNow(out HandState left, out HandState right)
        {
            lock (gate)
            {
                left = publishedNow[0];
                right = publishedNow[1];
            }
        }

        /// <summary>
        /// Queued for the render thread, which owns the session. The strongest request of a frame wins; the runtime
        /// replaces a vibration in progress with the next one anyway.
        /// </summary>
        public void Haptic(Hand hand, float amplitude, float seconds)
        {
            if (!active || !(amplitude > 0f))
                return;
            int i = (int)hand;
            amplitude = Math.Min(amplitude, 1f);
            lock (gate)
            {
                if (amplitude <= hapticAmplitude[i])
                    return;
                hapticAmplitude[i] = amplitude;
                hapticSeconds[i] = seconds;
            }
        }

        // ---- Setup (render thread, right after the session is created) ----

        /// <summary>Makes the action set, suggests its bindings, attaches it and puts the source on <see cref="VRInput.Headset"/>. Never throws: without controllers the display carries on.</summary>
        public static void Create(ulong session, ulong baseSpace)
        {
            var input = new XrInput(session, baseSpace);
            try
            {
                input.Setup();
            }
            catch (Exception e)
            {
                Log.Error(e, "OpenXR controller input could not be set up; continuing without controllers");
                input.Release();
                return;
            }
            current = input;
            VRInput.Headset = input;
        }

        /// <summary>
        /// The session is stopping (the headset is off or asleep): no frame reads the controllers until it runs again, so
        /// what they held is let go, and the fake hands can take over (<see cref="VRInput"/> takes the first active source).
        /// </summary>
        public static void SessionStopping() => current?.Deactivate();

        /// <summary>Before the session is destroyed.</summary>
        public static void Destroy()
        {
            XrInput input = current;
            current = null;
            recentrePending = false;
            if (input == null)
                return;
            if (ReferenceEquals(VRInput.Headset, input))
                VRInput.Headset = null;
            input.Release();
        }

        private void Setup()
        {
            for (int h = 0; h < 2; h++)
                handPaths[h] = StringToPath("/user/hand/" + HandNames[h]);

            var setInfo = new XrActionSetCreateInfo { Type = XrStructureType.ActionSetCreateInfo };
            Xr.WriteString(setInfo.ActionSetName, 64, "gameplay");
            Xr.WriteString(setInfo.LocalizedActionSetName, 128, "Gameplay");
            Xr.Check(Xr.xrCreateActionSet(XrRuntime.Instance, ref setInfo, out actionSet), "xrCreateActionSet", XrRuntime.Instance);

            gripAction = CreateAction("grip_pose", "Grip pose", XrActionType.PoseInput, true);
            aimAction = CreateAction("aim_pose", "Aim pose", XrActionType.PoseInput, true);
            triggerAction = CreateAction("trigger", "Trigger", XrActionType.FloatInput, true);
            squeezeAction = CreateAction("squeeze", "Squeeze", XrActionType.FloatInput, true);
            stickAction = CreateAction("thumbstick", "Thumbstick", XrActionType.Vector2fInput, true);
            stickClickAction = CreateAction("thumbstick_click", "Thumbstick click", XrActionType.BooleanInput, true);
            aAction = CreateAction("button_a", "A button", XrActionType.BooleanInput, false);
            bAction = CreateAction("button_b", "B button", XrActionType.BooleanInput, false);
            xAction = CreateAction("button_x", "X button", XrActionType.BooleanInput, false);
            yAction = CreateAction("button_y", "Y button", XrActionType.BooleanInput, false);
            menuAction = CreateAction("menu", "Menu", XrActionType.BooleanInput, true);
            hapticAction = CreateAction("haptic", "Vibration", XrActionType.VibrationOutput, true);
            if (XrRuntime.HasPalmPose)
                palmAction = CreateAction("palm_pose", "Palm pose", XrActionType.PoseInput, true);
            triggerTouchAction = CreateAction("trigger_touch", "Finger on the trigger", XrActionType.BooleanInput, true);
            stickTouchAction = CreateAction("thumbstick_touch", "Thumb on the thumbstick", XrActionType.BooleanInput, true);
            thumbrestTouchAction = CreateAction("thumbrest_touch", "Thumb on the thumbrest", XrActionType.BooleanInput, true);
            aTouchAction = CreateAction("touch_a", "Thumb on A", XrActionType.BooleanInput, false);
            bTouchAction = CreateAction("touch_b", "Thumb on B", XrActionType.BooleanInput, false);
            xTouchAction = CreateAction("touch_x", "Thumb on X", XrActionType.BooleanInput, false);
            yTouchAction = CreateAction("touch_y", "Thumb on Y", XrActionType.BooleanInput, false);

            // Bindings have to be suggested before the set is attached, and cannot change after.
            List<string> taken = SuggestBindings();
            if (taken.Count == 0)
                throw new InvalidOperationException("the runtime accepted no interaction profile");

            ulong set = actionSet;
            var attach = new XrSessionActionSetsAttachInfo
            {
                Type = XrStructureType.SessionActionSetsAttachInfo,
                CountActionSets = 1,
                ActionSets = (IntPtr)(&set),
            };
            Xr.Check(Xr.xrAttachSessionActionSets(session, ref attach), "xrAttachSessionActionSets", XrRuntime.Instance);

            for (int h = 0; h < 2; h++)
            {
                gripSpaces[h] = CreateActionSpace(gripAction, handPaths[h]);
                aimSpaces[h] = CreateActionSpace(aimAction, handPaths[h]);
                if (palmAction != 0)
                    palmSpaces[h] = CreateActionSpace(palmAction, handPaths[h]);
            }
            Log.Info($"OpenXR input ready: action set 'gameplay' attached, bindings suggested for {taken.Count} interaction profile(s) " +
                     $"({string.Join(", ", taken)}), palm pose {(palmAction != 0 ? "asked for (" + XrConst.PalmPoseExtension + ")" : "not offered")}");
        }

        // ---- Controllers whose profile comes with an extension ----
        // The instance is OpenXR 1.0, where these profiles are not core: each is there only on an instance with its
        // extension. XrRuntime enables one only when the runtime lists it, and its profile is suggested only when it is
        // enabled, so a runtime without it never sees the profile, and no other profile depends on it.

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

        private ulong CreateAction(string name, string localizedName, XrActionType type, bool perHand)
        {
            ulong* subactionPaths = stackalloc ulong[2];
            subactionPaths[0] = handPaths[0];
            subactionPaths[1] = handPaths[1];

            var info = new XrActionCreateInfo { Type = XrStructureType.ActionCreateInfo, ActionType = type };
            Xr.WriteString(info.ActionName, 64, name);
            Xr.WriteString(info.LocalizedActionName, 128, localizedName);
            if (perHand)
            {
                info.CountSubactionPaths = 2;
                info.SubactionPaths = (IntPtr)subactionPaths;
            }
            Xr.Check(Xr.xrCreateAction(actionSet, ref info, out ulong action), "xrCreateAction " + name, XrRuntime.Instance);
            return action;
        }

        private ulong CreateActionSpace(ulong action, ulong handPath)
        {
            var info = new XrActionSpaceCreateInfo
            {
                Type = XrStructureType.ActionSpaceCreateInfo,
                Action = action,
                SubactionPath = handPath,
                PoseInActionSpace = XrPosef.Identity,
            };
            Xr.Check(Xr.xrCreateActionSpace(session, ref info, out ulong space), "xrCreateActionSpace", XrRuntime.Instance);
            return space;
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

            // Vive wands: the trackpad stands in for the thumbstick, the grip buttons are clicks.
            var vive = new List<XrActionSuggestedBinding>();
            var viveExtra = new List<XrActionSuggestedBinding>();
            BindHands(vive, viveExtra, "trigger/value", "squeeze/click", "trackpad", "trackpad/click");
            BindMenuBothHands(vive);
            Offer(taken, unoffered, "/interaction_profiles/htc/vive_controller", null, vive, viveExtra);

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

        private void Release()
        {
            active = false;
            lock (gate)
            {
                published[0] = published[1] = default;
                publishedNow[0] = publishedNow[1] = default;
                hapticAmplitude[0] = hapticAmplitude[1] = 0f;
            }
            for (int h = 0; h < 2; h++)
            {
                if (gripSpaces[h] != 0)
                    Xr.xrDestroySpace(gripSpaces[h]);
                if (aimSpaces[h] != 0)
                    Xr.xrDestroySpace(aimSpaces[h]);
                if (palmSpaces[h] != 0)
                    Xr.xrDestroySpace(palmSpaces[h]);
                gripSpaces[h] = aimSpaces[h] = palmSpaces[h] = 0;
            }
            if (actionSet != 0)
                Xr.xrDestroyActionSet(actionSet); // takes its actions with it
            actionSet = 0;
        }

        // ---- Every frame (render thread) ----

        /// <summary>
        /// Inside the headset frame, after xrBeginFrame: the controllers for the game at the time the frame it draws from
        /// them will be shown, and at this frame's display time for <see cref="ReadNow"/>. Never throws.
        /// </summary>
        /// <param name="displayTime">The frame's predicted display time (xrWaitFrame), which the eyes are located for.</param>
        /// <param name="displayPeriod">The frame's predicted display period (xrWaitFrame).</param>
        public static void Update(long displayTime, long displayPeriod, bool focused)
        {
            ApplyRecentre(displayTime);
            long poseTime = PoseTime(displayTime, displayPeriod);
            current?.Frame(displayTime, poseTime, focused);
        }

        /// <summary>
        /// The time the game's hands are located for: this frame's display time plus the measured lead (see Prediction
        /// at the top). Render thread, once per headset frame, after the camera this frame draws was taken on.
        /// </summary>
        private static long PoseTime(long displayTime, long period)
        {
            if (period <= 0)
                return displayTime;
            if (leadNs <= 0)
                leadNs = period;
            if (!leadStartLogged)
            {
                leadStartLogged = true;
                Log.Info($"OpenXR hands for the game: located {period / 1e6:F1} ms (one display period) after the frame's display time " +
                         "until the time to the frame drawn from them is measured" + (Lead ? string.Empty : "; HandIKLead=0, so at the display time"));
            }

            long drawnFrom = CameraHeadLink.HandsFrameTime;
            if (drawnFrom > 0)
            {
                long sample = displayTime - drawnFrom;
                if (sample >= 0 && sample <= MaxLeadPeriods * period)
                {
                    leadNs += (sample - leadNs) * LeadSmoothing;
                    leadSampleMin = Math.Min(leadSampleMin, sample);
                    leadSampleMax = Math.Max(leadSampleMax, sample);
                    if (++leadSamples == LeadSamplesToLog)
                    {
                        Log.Info($"OpenXR hands for the game: located {leadNs / 1e6:F1} ms ({leadNs / period:F2} display periods of {period / 1e6:F1} ms) " +
                                 $"after the display time of the frame they are read in: the measured average until the frame drawn from them " +
                                 $"is shown, over {leadSamples} frames (from {leadSampleMin / (double)period:F1} to {leadSampleMax / (double)period:F1} periods)" +
                                 (Lead ? string.Empty : "; HandIKLead=0, so not applied"));
                    }
                }
            }
            return Lead ? displayTime + (long)leadNs : displayTime;
        }

        private void Frame(long displayTime, long poseTime, bool focused)
        {
            if (failed)
                return;
            try
            {
                // Unfocused (the dashboard is up, or the session is winding down): the runtime gives no input, so none is reported.
                if (!focused || Xr.Check(SyncActions(), "xrSyncActions", XrRuntime.Instance) == XrResult.SessionNotFocused)
                {
                    Deactivate();
                    return;
                }

                for (int h = 0; h < 2; h++)
                    ReadHand(h, displayTime, poseTime);
                lock (gate)
                {
                    published[0] = working[0];
                    published[1] = working[1];
                    publishedNow[0] = workingNow[0];
                    publishedNow[1] = workingNow[1];
                }
                if (!active)
                {
                    active = true;
                    Log.Info("OpenXR controllers active");
                }
                if (!loggedProfiles)
                {
                    loggedProfiles = true;
                    LogProfiles();
                }
                if (sourcesPending)
                {
                    sourcesPending = false;
                    LogSources();
                }
                ApplyHaptics();
            }
            catch (Exception e)
            {
                Log.Error(e, "OpenXR controller input failed; controllers off for the rest of the run");
                failed = true;
                Deactivate();
            }
        }

        private int SyncActions()
        {
            var activeSet = new XrActiveActionSet { ActionSet = actionSet };
            var info = new XrActionsSyncInfo
            {
                Type = XrStructureType.ActionsSyncInfo,
                CountActiveActionSets = 1,
                ActiveActionSets = (IntPtr)(&activeSet),
            };
            return Xr.xrSyncActions(session, ref info);
        }

        private void Deactivate()
        {
            if (active)
                Log.Info("OpenXR controllers inactive");
            active = false;
            working[0] = working[1] = default;
            workingNow[0] = workingNow[1] = default;
            lock (gate)
            {
                published[0] = published[1] = default;
                publishedNow[0] = publishedNow[1] = default;
                hapticAmplitude[0] = hapticAmplitude[1] = 0f;
            }
        }

        /// <summary>Into <see cref="working"/> (poses at <paramref name="poseTime"/>) and <see cref="workingNow"/> (at <paramref name="displayTime"/>).</summary>
        private void ReadHand(int h, long displayTime, long poseTime)
        {
            ulong hand = handPaths[h];
            HandState s = working[h];
            bool wasTracked = s.Tracked;

            LocatePoses(h, poseTime, ref s);
            s.FrameTime = displayTime;
            if (s.Tracked != wasTracked)
                Log.Info($"OpenXR {HandNames[h]} controller {(s.Tracked ? "tracked" : "not tracked")}");

            s.Trigger = GetFloat(triggerAction, hand);
            s.Squeeze = GetFloat(squeezeAction, hand);
            XrVector2f stick = GetVector2(stickAction, hand);
            s.Stick = new Vector2(stick.X, stick.Y);

            VRButtons buttons = VRButtons.None;
            if (Down((s.Buttons & VRButtons.Trigger) != 0, s.Trigger))
                buttons |= VRButtons.Trigger;
            if (Down((s.Buttons & VRButtons.Grip) != 0, s.Squeeze))
                buttons |= VRButtons.Grip;
            if (GetBoolean(stickClickAction, hand))
                buttons |= VRButtons.StickClick;
            if (GetBoolean(menuAction, hand))
                buttons |= VRButtons.Menu;
            if (h == (int)Hand.Right)
            {
                if (GetBoolean(aAction, 0))
                    buttons |= VRButtons.A;
                if (GetBoolean(bAction, 0))
                    buttons |= VRButtons.B;
            }
            else
            {
                if (GetBoolean(xAction, 0))
                    buttons |= VRButtons.X;
                if (GetBoolean(yAction, 0))
                    buttons |= VRButtons.Y;
            }
            s.Buttons = buttons;

            VRTouch sensors = VRTouch.None, touched = VRTouch.None;
            ReadTouch(triggerTouchAction, hand, VRTouch.Trigger, ref sensors, ref touched);
            ReadTouch(stickTouchAction, hand, VRTouch.Stick, ref sensors, ref touched);
            ReadTouch(thumbrestTouchAction, hand, VRTouch.Thumbrest, ref sensors, ref touched);
            if (h == (int)Hand.Right)
            {
                ReadTouch(aTouchAction, 0, VRTouch.A, ref sensors, ref touched);
                ReadTouch(bTouchAction, 0, VRTouch.B, ref sensors, ref touched);
            }
            else
            {
                ReadTouch(xTouchAction, 0, VRTouch.X, ref sensors, ref touched);
                ReadTouch(yTouchAction, 0, VRTouch.Y, ref sensors, ref touched);
            }
            s.TouchSensors = sensors;
            s.Touch = touched;
            working[h] = s;

            // The same hand at this frame's display time: the controls as read, the poses located again.
            HandState now = s;
            if (poseTime != displayTime)
            {
                HandState before = workingNow[h];
                now.Grip = before.Grip;
                now.Aim = before.Aim;
                now.Palm = before.Palm;
                LocatePoses(h, displayTime, ref now);
            }
            workingNow[h] = now;
        }

        /// <summary>Grip, aim and palm at <paramref name="time"/>; a pose that is not valid keeps what it was.</summary>
        private void LocatePoses(int h, long time, ref HandState s)
        {
            bool gripped = Locate(gripSpaces[h], time, ref s.Grip, out s.GripFlags);
            bool aimed = Locate(aimSpaces[h], time, ref s.Aim, out s.AimFlags);
            s.Tracked = gripped && aimed;
            // Without the palm action bound the space is unlocatable, which reads as not valid.
            s.PalmFlags = 0;
            s.PalmTracked = palmSpaces[h] != 0 && Locate(palmSpaces[h], time, ref s.Palm, out s.PalmFlags, optional: true);
        }

        /// <summary>A touch sensor; one the runtime fails to read counts as absent, never as a reason to take the controllers down.</summary>
        private void ReadTouch(ulong action, ulong subactionPath, VRTouch flag, ref VRTouch sensors, ref VRTouch touched)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateBoolean { Type = XrStructureType.ActionStateBoolean };
            if (Xr.xrGetActionStateBoolean(session, ref info, ref state) < 0 || state.IsActive == 0)
                return;
            sensors |= flag;
            if (state.CurrentState != 0)
                touched |= flag;
        }

        private static bool Down(bool wasDown, float value) => wasDown ? value > ReleaseAt : value >= PressAt;

        /// <summary>
        /// The pose in the same space the head is in (LOCAL), through the same conversion. Left as it was when not valid.
        /// An <paramref name="optional"/> pose (the palm) that fails to locate is not valid rather than an error.
        /// <paramref name="flags"/> is the location's valid and tracked bits, for the trace (<see cref="Hands.HeldTrace"/>).
        /// </summary>
        private bool Locate(ulong space, long displayTime, ref Matrix pose, out byte flags, bool optional = false)
        {
            flags = 0;
            var location = new XrSpaceLocation { Type = XrStructureType.SpaceLocation };
            int result = Xr.xrLocateSpace(space, baseSpace, displayTime, ref location);
            if (optional && result < 0)
                return false;
            Xr.Check(result, "xrLocateSpace", XrRuntime.Instance);
            flags = (byte)(location.LocationFlags & 0xF);
            const ulong valid = XrConst.SpaceLocationOrientationValid | XrConst.SpaceLocationPositionValid;
            if ((location.LocationFlags & valid) != valid)
                return false;
            pose = EyeRenderer.ToMatrix(location.Pose);
            return true;
        }

        private float GetFloat(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateFloat { Type = XrStructureType.ActionStateFloat };
            Xr.Check(Xr.xrGetActionStateFloat(session, ref info, ref state), "xrGetActionStateFloat", XrRuntime.Instance);
            return state.IsActive != 0 ? state.CurrentState : 0f;
        }

        private bool GetBoolean(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateBoolean { Type = XrStructureType.ActionStateBoolean };
            Xr.Check(Xr.xrGetActionStateBoolean(session, ref info, ref state), "xrGetActionStateBoolean", XrRuntime.Instance);
            return state.IsActive != 0 && state.CurrentState != 0;
        }

        private XrVector2f GetVector2(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateVector2f { Type = XrStructureType.ActionStateVector2f };
            Xr.Check(Xr.xrGetActionStateVector2f(session, ref info, ref state), "xrGetActionStateVector2f", XrRuntime.Instance);
            return state.IsActive != 0 ? state.CurrentState : default;
        }

        /// <summary>Plays what the game thread queued since the last frame.</summary>
        private void ApplyHaptics()
        {
            for (int h = 0; h < 2; h++)
            {
                float amplitude, seconds;
                lock (gate)
                {
                    amplitude = hapticAmplitude[h];
                    seconds = hapticSeconds[h];
                    hapticAmplitude[h] = 0f;
                }
                if (amplitude <= 0f || hapticsFailed)
                    continue;

                try
                {
                    var info = new XrHapticActionInfo { Type = XrStructureType.HapticActionInfo, Action = hapticAction, SubactionPath = handPaths[h] };
                    var vibration = new XrHapticVibration
                    {
                        Type = XrStructureType.HapticVibration,
                        // No length asked for: the shortest pulse the controller can do.
                        Duration = seconds > 0f ? (long)(Math.Min(seconds, MaxHapticSeconds) * 1e9) : XrConst.MinHapticDuration,
                        Frequency = XrConst.FrequencyUnspecified,
                        Amplitude = amplitude,
                    };
                    Xr.Check(Xr.xrApplyHapticFeedback(session, ref info, ref vibration), "xrApplyHapticFeedback", XrRuntime.Instance);
                }
                catch (XrException e)
                {
                    // Not worth taking the controllers down for.
                    Log.Warn($"Haptics are off for the rest of the run: {e.Message}");
                    hapticsFailed = true;
                }
            }
        }

        // ---- Events (render thread, from XrSession.PollEvents) ----

        /// <summary>The input-related events; any other type is ignored.</summary>
        public static void OnEvent(XrEventDataBuffer* buffer)
        {
            if (buffer->Type == XrStructureType.EventDataReferenceSpaceChangePending)
            {
                XrEventDataReferenceSpaceChangePending* change = (XrEventDataReferenceSpaceChangePending*)buffer;
                // Only the space the head and hands are located in; the poses from the change time on are in the new origin.
                if (change->ReferenceSpaceType != XrReferenceSpaceType.Local)
                    return;
                recentreTime = change->ChangeTime;
                recentrePending = true;
                Log.Info($"OpenXR LOCAL space is being recentred (change time {change->ChangeTime}, pose valid {change->PoseValid != 0})");
            }
            else if (buffer->Type == XrStructureType.EventDataInteractionProfileChanged)
            {
                // The runtime has picked (or changed) the controller; the sources it binds show on the next sync.
                if (current != null)
                {
                    current.LogProfiles();
                    current.sourcesPending = true;
                }
            }
        }

        /// <summary>
        /// The runtime moved the origin of the tracking space to where the user is and faces. From the first frame in
        /// the new space the game's head is recentred on all axes from the head (<see cref="GameHead.Recentre"/>), so the
        /// eye is at the character's eye and forward is where the user now faces; sooner would take the head of a frame
        /// still in the old space.
        /// </summary>
        private static void ApplyRecentre(long displayTime)
        {
            if (!recentrePending || displayTime < recentreTime)
                return;
            recentrePending = false;
            GameHead.Recentre("the headset's recentre");
        }

        /// <summary>The profile the runtime picked for each hand: the first read, then only when it changes.</summary>
        private void LogProfiles()
        {
            try
            {
                for (int h = 0; h < 2; h++)
                {
                    var state = new XrInteractionProfileState { Type = XrStructureType.InteractionProfileState };
                    Xr.Check(Xr.xrGetCurrentInteractionProfile(session, handPaths[h], ref state), "xrGetCurrentInteractionProfile", XrRuntime.Instance);
                    if (state.InteractionProfile == loggedProfile[h])
                        continue;
                    loggedProfile[h] = state.InteractionProfile;
                    Log.Info($"OpenXR {HandNames[h]} hand interaction profile: {PathName(state.InteractionProfile)}");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"Could not read the interaction profiles: {e.Message}");
            }
        }

        /// <summary>
        /// Which controls the runtime bound to a real source, as of the last sync: an action that is not active has nothing
        /// behind it (the profile the runtime chose has no such control, or took none of the bindings). This is what says,
        /// on a headset session, that Menu, X, Y, A and B are really there, without anyone pressing them.
        /// </summary>
        private void LogSources()
        {
            try
            {
                var names = new List<string>();
                for (int h = 0; h < 2; h++)
                {
                    ulong hand = handPaths[h];
                    var list = new List<string>();
                    if (BooleanActive(menuAction, hand))
                        list.Add("menu");
                    if (BooleanActive(stickClickAction, hand))
                        list.Add("stick click");
                    if (FloatActive(triggerAction, hand))
                        list.Add("trigger");
                    if (FloatActive(squeezeAction, hand))
                        list.Add("squeeze");
                    if (Vector2Active(stickAction, hand))
                        list.Add("stick");
                    if (palmAction != 0 && PoseActive(palmAction, hand))
                        list.Add("palm pose");
                    if (BooleanActive(triggerTouchAction, hand))
                        list.Add("trigger touch");
                    if (BooleanActive(stickTouchAction, hand))
                        list.Add("stick touch");
                    if (BooleanActive(thumbrestTouchAction, hand))
                        list.Add("thumbrest touch");
                    names.Add($"{HandNames[h]}: {(list.Count > 0 ? string.Join(", ", list) : "nothing")}");
                }
                var buttons = new List<string>();
                if (BooleanActive(aAction, 0))
                    buttons.Add("A");
                if (BooleanActive(bAction, 0))
                    buttons.Add("B");
                if (BooleanActive(xAction, 0))
                    buttons.Add("X");
                if (BooleanActive(yAction, 0))
                    buttons.Add("Y");
                names.Add("face buttons: " + (buttons.Count > 0 ? string.Join(" ", buttons) : "none"));
                var touches = new List<string>();
                if (BooleanActive(aTouchAction, 0))
                    touches.Add("A");
                if (BooleanActive(bTouchAction, 0))
                    touches.Add("B");
                if (BooleanActive(xTouchAction, 0))
                    touches.Add("X");
                if (BooleanActive(yTouchAction, 0))
                    touches.Add("Y");
                names.Add("face touch: " + (touches.Count > 0 ? string.Join(" ", touches) : "none"));
                Log.Info("OpenXR bound sources (active after a sync): " + string.Join("; ", names));
            }
            catch (Exception e)
            {
                Log.Warn($"Could not read which controls are bound: {e.Message}");
            }
        }

        private bool BooleanActive(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateBoolean { Type = XrStructureType.ActionStateBoolean };
            Xr.Check(Xr.xrGetActionStateBoolean(session, ref info, ref state), "xrGetActionStateBoolean", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private bool PoseActive(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStatePose { Type = XrStructureType.ActionStatePose };
            Xr.Check(Xr.xrGetActionStatePose(session, ref info, ref state), "xrGetActionStatePose", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private bool FloatActive(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateFloat { Type = XrStructureType.ActionStateFloat };
            Xr.Check(Xr.xrGetActionStateFloat(session, ref info, ref state), "xrGetActionStateFloat", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private bool Vector2Active(ulong action, ulong subactionPath)
        {
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateVector2f { Type = XrStructureType.ActionStateVector2f };
            Xr.Check(Xr.xrGetActionStateVector2f(session, ref info, ref state), "xrGetActionStateVector2f", XrRuntime.Instance);
            return state.IsActive != 0;
        }

        private static string PathName(ulong path)
        {
            if (path == 0)
                return "(none yet)";
            byte* buffer = stackalloc byte[256];
            return Xr.xrPathToString(XrRuntime.Instance, path, 256, out uint _, buffer) >= 0 ? Xr.ReadString(buffer, 256) : path.ToString();
        }
    }
}
