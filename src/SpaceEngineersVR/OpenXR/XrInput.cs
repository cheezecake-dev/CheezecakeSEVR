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
    /// thumbstick_touch, thumbrest_touch per hand and touch_a/b/x/y: Touch, and TouchSensors for the ones bound. The Vive
    /// wand's trackpad touch is the thumbstick_touch (the thumb on the pad, for the hand's pose). The buttons are reported as
    /// the controller has them; what each does is the binding table's (ControlsDown, <see cref="Input.Bindings.ControllerBindings"/>).
    /// A Vive wand's trackpads are made its sticks (<see cref="WandLayout"/>).
    /// </remarks>
    internal sealed unsafe partial class XrInput : IVRInputSource
    {
        // The analog bits press and release at different values, so a trigger resting at the edge does not flicker.
        private const float PressAt = 0.6f, ReleaseAt = 0.4f;
        internal const float MaxHapticSeconds = 10f;

        // ---- Prediction ----
        // The game reads the hands published in headset frame M, builds its frame from them, and the renderer shows that
        // frame in a later headset frame N, which the eyes are located for (with the late head correction, CameraHeadLink).
        // The hands in it are right when they are located for N's display time, not M's. N - M is not fixed (the game
        // runs at its own rate, and a headset frame with no new game frame shows the old one again), so it is sampled:
        // each frame, this frame's display time less the frame time of the hands in the camera it draws (CameraHeadLink.HandsFrameTime),
        // averaged. Until a sample arrives the lead is one display period.

        /// <summary>Weight of each new sample in the averaged lead (about a second to settle at 72-90 Hz).</summary>
        private const double LeadSmoothing = 0.02;

        /// <summary>Samples further behind than this many display periods are not a drawn game frame (a pause, a load) and are skipped.</summary>
        private const int MaxLeadPeriods = 4;

        /// <summary>The averaged lead is logged once, after this many samples.</summary>
        private const int LeadSamplesToLog = 300;

        /// <summary>RenderDebug HandIKLead=0: the game's hands at the display time of the frame they are read in, to compare.</summary>
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

        // The head, to refuse a hand placed inside the headset (InsideHeadset); 0 if the runtime would not make it.
        private ulong viewSpace;

        // Render thread only: per hand, whether its last poses were refused as inside the headset (logged when it changes).
        private readonly bool[] insideHeadset = new bool[2];

        // Render thread only: per hand, the time (XrTime) it was first seen beyond HeadsetRelease while inside; 0 when not.
        private readonly long[] outsideSince = new long[2];

        private ulong actionSet;
        private ulong gripAction, aimAction, palmAction, triggerAction, squeezeAction, stickAction, stickClickAction;
        private ulong aAction, bAction, xAction, yAction, menuAction, hapticAction;
        // The Windows Mixed Reality controller's trackpad, bound in its profile only: clicked, its parts are controls for the bindings (TrackpadPart).
        private ulong padAction, padClickAction;
        private ulong triggerTouchAction, stickTouchAction, thumbrestTouchAction, aTouchAction, bTouchAction, xTouchAction, yTouchAction;
        // Controls only the bindings read (ControllerControls): a bumper, a thumb on a trackpad, fingers on a grip that clicks.
        private ulong bumperAction, padTouchAction, squeezeTouchAction;

        // Render thread only: the hands as last read, which keeps the last good poses while a controller is lost.
        // working: for the game (predicted); workingNow: at this frame's display time.
        private readonly HandState[] working = new HandState[2];
        private readonly HandState[] workingNow = new HandState[2];
        private bool hapticsFailed, loggedProfiles, sourcesPending = true;

        // Render thread: input that failed is tried again after a wait that doubles to RetryMaxSeconds, never given up on.
        private int failures;
        private long retryAt;
        private string lastFailure;
        private const double RetryMaxSeconds = 30d;

        // Render thread: why the controllers are inactive, as last logged (null while active).
        private string inactiveWhy;

        // Render thread: per hand, whether its tracked state has been logged yet (it is logged on the first read and each change).
        private readonly bool[] trackingLogged = new bool[2];

        // Render thread: the controls only the bindings read that the runtime failed to read, each warned of once.
        private readonly HashSet<string> extraWarned = new HashSet<string>(StringComparer.Ordinal);

        // The interaction profile last logged per hand; MaxValue until the first is read, so that one is logged too.
        private readonly ulong[] loggedProfile = { ulong.MaxValue, ulong.MaxValue };

        // Render thread only: the hand's current profile is the Vive wand's (set where the profiles are read, LogProfiles), so its
        // trackpads are made its sticks (WandLayout).
        private ulong viveProfile;
        private readonly bool[] wand = new bool[2];

        // Render thread only: the hand's current profile as the bindings name it (ControllerBindings.Key), or null.
        private readonly string[] profileKey = new string[2];


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
        public static void SessionStopping() => current?.Deactivate("the session is stopping");

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
            padAction = CreateAction("trackpad", "Trackpad", XrActionType.Vector2fInput, true);
            padClickAction = CreateAction("trackpad_click", "Trackpad click", XrActionType.BooleanInput, true);
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
            bumperAction = CreateAction("bumper", "Bumper", XrActionType.BooleanInput, true);
            padTouchAction = CreateAction("trackpad_touch", "Thumb on the trackpad", XrActionType.BooleanInput, true);
            squeezeTouchAction = CreateAction("squeeze_touch", "Fingers on the grip", XrActionType.BooleanInput, true);

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
            var view = new XrReferenceSpaceCreateInfo
            {
                Type = XrStructureType.ReferenceSpaceCreateInfo,
                ReferenceSpaceType = XrReferenceSpaceType.View,
                PoseInReferenceSpace = XrPosef.Identity,
            };
            if (Xr.xrCreateReferenceSpace(session, ref view, out viewSpace) < 0)
                viewSpace = 0;
            Log.Info($"OpenXR input ready: action set 'gameplay' attached, bindings suggested for {taken.Count} interaction profile(s) " +
                     $"({string.Join(", ", taken)}), palm pose {(palmAction != 0 ? "asked for (" + XrConst.PalmPoseExtension + ")" : "not offered")}");
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
            if (viewSpace != 0)
                Xr.xrDestroySpace(viewSpace);
            viewSpace = 0;
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
            if (failures > 0 && System.Diagnostics.Stopwatch.GetTimestamp() < retryAt)
                return;
            try
            {
                // Unfocused (the dashboard is up, or the session is winding down): the runtime gives no input, so none is reported.
                if (!focused)
                {
                    Deactivate($"the session is not focused ({XrSession.State}): the runtime gives input only to the focused app");
                    return;
                }
                if (Xr.Check(SyncActions(), "xrSyncActions", XrRuntime.Instance) == XrResult.SessionNotFocused)
                {
                    Deactivate("xrSyncActions says the session is not focused (another app or the runtime's overlay has the input)");
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
                if (failures > 0)
                {
                    Log.Info($"OpenXR controller input working again after {failures} failed {(failures == 1 ? "try" : "tries")}");
                    failures = 0;
                    lastFailure = null;
                }
                if (!active)
                {
                    active = true;
                    inactiveWhy = null;
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
                failures++;
                double wait = Math.Min(RetryMaxSeconds, Math.Pow(2d, Math.Min(failures - 1, 10)));
                retryAt = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(wait * System.Diagnostics.Stopwatch.Frequency);
                if (e.Message != lastFailure)
                    Log.Error(e, $"OpenXR controller input failed ({failures} in a row); controllers off, trying again in {wait:0} s");
                else
                    Log.Warn($"OpenXR controller input failed again ({failures} in a row): {e.Message}; trying again in {wait:0} s");
                lastFailure = e.Message;
                Deactivate("controller input failed");
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

        /// <summary>No controller input: logged with <paramref name="why"/> whenever it starts or its reason changes.</summary>
        private void Deactivate(string why)
        {
            if (active || why != inactiveWhy)
                Log.Info("OpenXR controllers inactive: " + why);
            inactiveWhy = why;
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
            if (s.Tracked != wasTracked || !trackingLogged[h])
            {
                trackingLogged[h] = true;
                Log.Info($"OpenXR {HandNames[h]} controller {(s.Tracked ? "tracked" : "not tracked")} (grip pose flags 0x{s.GripFlags:X}, aim 0x{s.AimFlags:X}: " +
                         "1 orientation valid, 2 position valid, 4 and 8 tracked)");
            }

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
            bool trackpadClick = ReadExtra(padClickAction, hand, "trackpad click");
            Vector2 padAt = Vector2.Zero;
            if (trackpadClick)
            {
                XrVector2f pad = ReadExtraVector2(padAction, hand, "trackpad position");
                padAt = new Vector2(pad.X, pad.Y);
            }
            // The buttons stay as the controller has them; what each does is the bindings' (ControllerControls).
            Vector2 rawStick = s.Stick;
            if (wand[h])
            {
                // A Vive wand reports its trackpad as the stick and its click as the stick click: the pad is made the stick the hands
                // turn and walk with. Whether a seat is being driven is the game thread's word, a volatile flag (VRControls.SeatDriven).
                bool padClick = (buttons & VRButtons.StickClick) != 0;
                s.Stick = h == (int)Hand.Right ? WandLayout.Right(s.Stick, padClick, VRControls.SeatDriven) : WandLayout.Left(s.Stick);
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
            bool bumper = ReadExtra(bumperAction, hand, "bumper"), padTouch = ReadExtra(padTouchAction, hand, "trackpad touch"),
                gripTouch = ReadExtra(squeezeTouchAction, hand, "grip touch");
            ControllerControls(h, buttons, rawStick, trackpadClick, padAt, touched, bumper, padTouch, gripTouch, out s.ControlsDown, out s.ControlsTouched);
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

        /// <summary>
        /// Grip, aim and palm at <paramref name="time"/>; a pose that is not valid keeps what it was, and so do all three
        /// while the hand is inside the headset (<see cref="InsideHeadset"/>).
        /// </summary>
        private void LocatePoses(int h, long time, ref HandState s)
        {
            Matrix grip = s.Grip, aim = s.Aim;
            bool gripped = Locate(gripSpaces[h], time, ref grip, out s.GripFlags);
            bool aimed = Locate(aimSpaces[h], time, ref aim, out s.AimFlags);
            bool near = gripped && aimed && InsideHeadset(time, grip.Translation, aim.Translation, insideHeadset[h] ? HeadsetRelease : HeadsetShell);
            bool inside = insideHeadset[h];
            if (near)
            {
                inside = true;
                outsideSince[h] = 0;
            }
            else if (inside)
            {
                if (outsideSince[h] == 0)
                    outsideSince[h] = time;
                if (time - outsideSince[h] >= HeadsetReleaseHold)
                    inside = false;
            }
            if (inside != insideHeadset[h])
            {
                insideHeadset[h] = inside;
                Log.Info(inside
                    ? $"OpenXR {HandNames[h]} hand reported inside the headset (hand tracking, the hand out of view): not tracked until it leaves"
                    : $"OpenXR {HandNames[h]} hand no longer reported inside the headset");
            }
            if (inside)
            {
                s.Tracked = s.PalmTracked = false;
                return;
            }
            if (gripped)
                s.Grip = grip;
            if (aimed)
                s.Aim = aim;
            s.Tracked = gripped && aimed;
            // Without the palm action bound the space is unlocatable, which reads as not valid.
            s.PalmFlags = 0;
            s.PalmTracked = palmSpaces[h] != 0 && Locate(palmSpaces[h], time, ref s.Palm, out s.PalmFlags, optional: true);
        }

        /// <summary>A grip or aim pose this close to the eyes (metres) is inside the headset's shell: no hand or controller is there.</summary>
        private const float HeadsetShell = 0.10f;

        /// <summary>
        /// A hand already refused leaves only once its poses have stayed beyond this (metres) for <see cref="HeadsetReleaseHold"/>:
        /// the stand-in pose hovers around the shell edge, and entering and leaving at one radius made it flap, logging each
        /// change and jumping the arm between pose and animation.
        /// </summary>
        private const float HeadsetRelease = 0.15f;

        /// <summary>How long (XrTime, nanoseconds) the poses must stay beyond <see cref="HeadsetRelease"/> to leave.</summary>
        private const long HeadsetReleaseHold = 300_000_000;

        /// <summary>
        /// The hand's grip or aim is inside the headset. A Quest tracking hands (the controllers put down) reports a hand it
        /// cannot see at a fixed place a few centimetres in front of the eyes, both aims on one point, flagged valid and
        /// tracked; an arm drawn there is cut by the near plane into shards across the view.
        /// </summary>
        private bool InsideHeadset(long time, Vector3 grip, Vector3 aim, float radius)
        {
            if (viewSpace == 0)
                return false;
            var head = new XrSpaceLocation { Type = XrStructureType.SpaceLocation };
            if (Xr.xrLocateSpace(viewSpace, baseSpace, time, ref head) < 0 || (head.LocationFlags & XrConst.SpaceLocationPositionValid) == 0)
                return false;
            var eyes = new Vector3(head.Pose.Position.X, head.Pose.Position.Y, head.Pose.Position.Z);
            float shell = radius * radius;
            return Vector3.DistanceSquared(grip, eyes) < shell || Vector3.DistanceSquared(aim, eyes) < shell;
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

        /// <summary>
        /// A control only the bindings read (a bumper, a trackpad, a finger on a grip); one the runtime fails to read counts as
        /// not there, never as a reason to take the controllers down. Each is warned of once.
        /// </summary>
        private bool ReadExtra(ulong action, ulong subactionPath, string name)
        {
            if (action == 0)
                return false;
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateBoolean { Type = XrStructureType.ActionStateBoolean };
            int result = Xr.xrGetActionStateBoolean(session, ref info, ref state);
            if (result < 0)
            {
                WarnExtra(name, result);
                return false;
            }
            return state.IsActive != 0 && state.CurrentState != 0;
        }

        /// <summary>As <see cref="ReadExtra"/>, a position: zero when it cannot be read.</summary>
        private XrVector2f ReadExtraVector2(ulong action, ulong subactionPath, string name)
        {
            if (action == 0)
                return default;
            var info = new XrActionStateGetInfo { Type = XrStructureType.ActionStateGetInfo, Action = action, SubactionPath = subactionPath };
            var state = new XrActionStateVector2f { Type = XrStructureType.ActionStateVector2f };
            int result = Xr.xrGetActionStateVector2f(session, ref info, ref state);
            if (result < 0)
            {
                WarnExtra(name, result);
                return default;
            }
            return state.IsActive != 0 ? state.CurrentState : default;
        }

        private void WarnExtra(string name, int result)
        {
            if (extraWarned.Add(name))
                Log.Warn($"OpenXR: the runtime could not read the {name} ({Xr.ResultName(XrRuntime.Instance, result)}); it counts as not there");
        }

        private static bool Down(bool wasDown, float value) => wasDown ? value > ReleaseAt : value >= PressAt;

        /// <summary>
        /// The pose in the same space the head is in (LOCAL), through the same conversion. Left as it was when not valid, or when
        /// the runtime calls it valid and it is not plausible (<see cref="Plausible"/>).
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
            Matrix located = EyeRenderer.ToMatrix(location.Pose);
            if (!Plausible(located))
            {
                implausiblePoses++;
                if (implausiblePoses == 1 || implausiblePoses % 1000 == 0)
                    Log.Warn($"OpenXR: a controller pose flagged valid is not usable (translation {located.Translation}, forward length squared " +
                             $"{located.Forward.LengthSquared():0.###}, up {located.Up.LengthSquared():0.###}); the last good pose is kept ({implausiblePoses} so far)");
                return false;
            }
            pose = located;
            return true;
        }

        /// <summary>How many poses flagged valid were refused by <see cref="Plausible"/>; for the log.</summary>
        private int implausiblePoses;

        /// <summary>
        /// A dropped or waking controller can report a valid flag with an unusable pose: a matrix that is finite, with a translation
        /// within a kilometre and a forward and up of near unit length (length squared within 0.1 of 1) is taken as one.
        /// </summary>
        // Adapted from ColbyJack1134's SpaceEngineersVR (Player/TrackedDevice.cs), with his permission.
        internal static bool Plausible(in Matrix pose) => pose.IsValid() && pose.Translation.LengthSquared() < 1e6f &&
            Math.Abs(pose.Forward.LengthSquared() - 1f) < 0.1f && Math.Abs(pose.Up.LengthSquared() - 1f) < 0.1f;

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
                    Log.Warn($"Haptics are off for this headset session: {e.Message}");
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

        /// <summary>
        /// The hand's controls as they are on the controller (<see cref="HandState.ControlsDown"/>): the buttons as read, before
        /// a layout, by the names of the controls they are bound to (a Steam Frame's left D-pad and View, an Index's left
        /// trackpad pressed firmly), and a clicked trackpad (a Vive wand's, read as its stick, as the part of it clicked; a
        /// Windows Mixed Reality controller's by its upper and lower half, as its face buttons have always been read). The
        /// bumper, a thumb on a trackpad and fingers on a grip come from controls only the bindings read.
        /// </summary>
        private void ControllerControls(int h, VRButtons raw, Vector2 rawStick, bool padClick, Vector2 padAt, VRTouch touched,
                                        bool bumper, bool padTouch, bool gripTouch, out uint down, out uint touch)
        {
            uint d = 0u, t = 0u;
            void Put(ControllerControl control, bool isDown, bool isTouched)
            {
                uint bit = 1u << (int)control;
                if (isDown)
                    d |= bit;
                if (isDown || isTouched)
                    t |= bit;
            }
            bool left = h == (int)Hand.Left;
            // The left hand's Menu, X and Y are bound to other controls on these (XrInput.Bindings.cs).
            bool steamFrame = left && profileKey[h] == Input.Bindings.DefaultBindings.SteamFrameProfile;
            bool index = left && profileKey[h] == Input.Bindings.DefaultBindings.IndexProfile;
            Put(ControllerControl.Trigger, (raw & VRButtons.Trigger) != 0, (touched & VRTouch.Trigger) != 0);
            Put(ControllerControl.Grip, (raw & VRButtons.Grip) != 0, gripTouch);
            Put(steamFrame ? ControllerControl.View : index ? ControllerControl.TrackpadForce : ControllerControl.Menu, (raw & VRButtons.Menu) != 0, false);
            Put(ControllerControl.Bumper, bumper, false);
            if (padTouch)
                Put(ControllerControl.Trackpad, false, true);
            Put(steamFrame ? ControllerControl.DpadDown : ControllerControl.ButtonLower, (raw & (left ? VRButtons.X : VRButtons.A)) != 0, (touched & (left ? VRTouch.X : VRTouch.A)) != 0);
            Put(steamFrame ? ControllerControl.DpadUp : ControllerControl.ButtonUpper, (raw & (left ? VRButtons.Y : VRButtons.B)) != 0, (touched & (left ? VRTouch.Y : VRTouch.B)) != 0);
            Put(ControllerControl.Thumbrest, false, (touched & VRTouch.Thumbrest) != 0);
            bool stickClick = (raw & VRButtons.StickClick) != 0, stickTouch = (touched & VRTouch.Stick) != 0;
            if (wand[h])
            {
                // A wand's trackpad is read as the stick (BindHands): it is its trackpad here.
                Put(ControllerControl.Trackpad, stickClick, stickTouch);
                if (stickClick)
                    Put(TrackpadPart(rawStick), true, true);
            }
            else
            {
                Put(ControllerControl.ThumbstickClick, stickClick, stickTouch);
                Put(ControllerControl.Thumbstick, false, stickTouch);
            }
            if (padClick)
            {
                Put(ControllerControl.Trackpad, true, true);
                Put(padAt.Y >= 0f ? ControllerControl.TrackpadUp : ControllerControl.TrackpadDown, true, true);
            }
            down = d;
            touch = t;
        }

        /// <summary>The part of a trackpad clicked at <paramref name="at"/> (+Y up): the middle within <see cref="WandLayout.CentreRadius"/>, else the side it is nearest.</summary>
        internal static ControllerControl TrackpadPart(Vector2 at)
        {
            if (at.LengthSquared() <= WandLayout.CentreRadius * WandLayout.CentreRadius)
                return ControllerControl.TrackpadCentre;
            if (Math.Abs(at.Y) >= Math.Abs(at.X))
                return at.Y > 0f ? ControllerControl.TrackpadUp : ControllerControl.TrackpadDown;
            return at.X > 0f ? ControllerControl.TrackpadRight : ControllerControl.TrackpadLeft;
        }

        /// <summary>The hand is on a Vive wand now, or is not; logged once each time it switches, so a log shows the layout engaged.</summary>
        private void SetWand(int h, bool onWand)
        {
            if (wand[h] == onWand)
                return;
            wand[h] = onWand;

            if (!onWand)
                return;
            Log.Info(h == (int)Hand.Right
                ? "OpenXR right hand is a Vive wand: its trackpad clicked left or right turns, up or down jumps and crouches (not in a seat); its buttons are the bindings'"
                : "OpenXR left hand is a Vive wand: its trackpad is the stick; its buttons are the bindings'");
        }
    }
}
