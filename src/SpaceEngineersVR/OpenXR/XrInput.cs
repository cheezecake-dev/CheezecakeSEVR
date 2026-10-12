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
    /// wand's trackpad touch is the thumbstick_touch (the thumb on the pad, for the hand's pose). A hand whose current
    /// profile is the Vive wand's has its trackpad and Menu button put in the Quest's layout before they are reported
    /// (<see cref="WandLayout"/>), so Stick, StickClick, B, A, Y and Menu mean what they do on a Quest.
    /// </remarks>
    internal sealed unsafe partial class XrInput : IVRInputSource
    {
        // The analog bits press and release at different values, so a trigger resting at the edge does not flicker.
        private const float PressAt = 0.6f, ReleaseAt = 0.4f;
        private const float MaxHapticSeconds = 10f;

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

        // Render thread only: the hand's current profile is the Vive wand's (set where the profiles are read, LogProfiles), so its
        // controls are put in the Quest's layout (WandLayout); and the right wand's Menu button timing.
        private ulong viveProfile;
        private readonly bool[] wand = new bool[2];
        private WandLayout.MenuState wandMenu;

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
            // A Menu button down when the input stopped must not read as a tap or a hold when it is let go after.
            wandMenu = default;
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
            bool wandMenuPulse = false;
            if (wand[h])
            {
                // A Vive wand reported the trackpad as the stick and its click, and Menu: they are put in the Quest's layout. Whether a
                // seat is being driven is the game thread's word, a volatile flag it sets each frame (VRControls.SeatDriven).
                bool padClick = (buttons & VRButtons.StickClick) != 0, menu = (buttons & VRButtons.Menu) != 0;
                WandLayout.Output layout = h == (int)Hand.Right
                    ? WandLayout.Right(ref wandMenu, s.Stick, padClick, menu, VRControls.SeatDriven, displayTime / 1e9)
                    : WandLayout.Left(s.Stick, padClick, menu);
                buttons = (buttons & (VRButtons.Trigger | VRButtons.Grip)) | layout.Buttons;
                s.Stick = layout.Stick;
                wandMenuPulse = layout.GameMenu;
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

            // The right wand's held Menu is the game menu, which the game reads from the left hand's Menu; the left hand was read first.
            if (wandMenuPulse)
            {
                working[(int)Hand.Left].Buttons |= VRButtons.Menu;
                workingNow[(int)Hand.Left].Buttons |= VRButtons.Menu;
            }
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

        /// <summary>The hand is on a Vive wand now, or is not; logged once each time it switches, so a log shows the layout engaged.</summary>
        private void SetWand(int h, bool onWand)
        {
            if (wand[h] == onWand)
                return;
            wand[h] = onWand;
            if (h == (int)Hand.Right)
                wandMenu = default;
            if (!onWand)
                return;
            Log.Info(h == (int)Hand.Right
                ? "OpenXR right hand is a Vive wand: Quest layout from the wand's controls (pad clicked: left or right turns, up or down jump and crouch " +
                  "or, in a seat, A and B; centre click is the stick click; Menu tapped is B, held half a second is the game menu)"
                : "OpenXR left hand is a Vive wand: Quest layout from the wand's controls (pad is the stick and its click the stick click; Menu is Y)");
        }
    }
}
