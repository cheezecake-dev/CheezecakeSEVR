using System;
using VRageMath;

namespace SpaceEngineersVR.Input
{
    internal enum Hand
    {
        Left,
        Right,
    }

    /// <summary>Digital controls of one hand. A/B are on the right controller, X/Y and Menu on the left.</summary>
    [Flags]
    internal enum VRButtons
    {
        None = 0,
        Trigger = 1 << 0,
        Grip = 1 << 1,
        StickClick = 1 << 2,
        A = 1 << 3,
        B = 1 << 4,
        X = 1 << 5,
        Y = 1 << 6,
        Menu = 1 << 7,
    }

    /// <summary>The controller's touch sensors: a finger resting on a control without pressing it.</summary>
    [Flags]
    internal enum VRTouch
    {
        None = 0,
        Trigger = 1 << 0,
        Stick = 1 << 1,
        Thumbrest = 1 << 2,
        A = 1 << 3,
        B = 1 << 4,
        X = 1 << 5,
        Y = 1 << 6,

        /// <summary>Where the thumb rests: the stick, the thumbrest and the face buttons.</summary>
        Thumb = Stick | Thumbrest | A | B | X | Y,
    }

    /// <summary>One hand as a source reports it.</summary>
    internal struct HandState
    {
        /// <summary>The poses below are current; false when the controller is not tracked (or there is none).</summary>
        public bool Tracked;

        /// <summary>Where the hand holds things, in the tracking space the head is in (HeadTracker), metres.</summary>
        public Matrix Grip;

        /// <summary>Where the hand points (forward = -Z), same space.</summary>
        public Matrix Aim;

        /// <summary>
        /// <see cref="Palm"/> is the runtime's and current. False when the source has no palm pose (no XR_EXT_palm_pose,
        /// the profile binds none, the desktop hands) or lost it this frame: then the palm is placed from <see cref="Grip"/>.
        /// </summary>
        public bool PalmTracked;

        /// <summary>
        /// The runtime's palm pose (XR_EXT_palm_pose, OpenXR 1.1 grip_surface), same space: the palm's centroid on its
        /// surface; -Z along the index finger straightened; +X the palm's normal, into the palm on the right hand and out
        /// of it on the left; +Y = Z x X, toward the thumb.
        /// </summary>
        public Matrix Palm;

        /// <summary>Analog trigger and grip, 0..1.</summary>
        public float Trigger, Squeeze;

        /// <summary>Thumbstick, -1..1 each; +Y is forward.</summary>
        public Vector2 Stick;

        public VRButtons Buttons;

        /// <summary>
        /// The controller's own controls that are down, as they are on the controller, before any layout puts them in the Quest's
        /// (a Vive wand's trackpad is its trackpad here, not a stick): bit <c>1 &lt;&lt; (int)ControllerControl</c>. What the
        /// bindings read (<see cref="Bindings.BindingEngine"/>).
        /// </summary>
        public uint ControlsDown;

        /// <summary>The controller's own controls a finger or thumb is on (or that are down), as <see cref="ControlsDown"/>.</summary>
        public uint ControlsTouched;

        /// <summary>The touch sensors a finger is on.</summary>
        public VRTouch Touch;

        /// <summary>The touch sensors the controller has (bound and reporting); a sensor not here never shows in <see cref="Touch"/>.</summary>
        public VRTouch TouchSensors;

        /// <summary>
        /// The OpenXR display time (ns) of the headset frame these were read in; the poses are located for this time plus
        /// the pipeline lead (XrInput). 0 from a source without one.
        /// </summary>
        public long FrameTime;

        /// <summary>
        /// Debug (<see cref="Hands.HeldTrace"/>): the runtime's location flags for <see cref="Grip"/>, <see cref="Aim"/> and
        /// <see cref="Palm"/> as last located (OpenXR's valid and tracked bits, 0x1 to 0x8); 0 from a source without them.
        /// </summary>
        public byte GripFlags, AimFlags, PalmFlags;

        /// <summary>
        /// The grip's position is actively tracked (OpenXR's position-tracked bit), not just valid. A controller out of the
        /// headset's cameras' view stays valid but loses it, and its position then drifts or freezes while its turn, from
        /// the inertial sensors, stays good. True from a source without location flags (the desktop hands) while
        /// <see cref="Tracked"/>.
        /// </summary>
        public bool PositionTracked => Tracked && (GripFlags == 0 || (GripFlags & (byte)OpenXR.XrConst.SpaceLocationPositionTracked) != 0);
    }

    /// <summary>
    /// Where hand input comes from: OpenXR with a headset, or the desktop emulator without one. Read on the game
    /// thread; a source that is fed on the render thread publishes under a lock, as HeadTracker does.
    /// </summary>
    internal interface IVRInputSource
    {
        /// <summary>The source has hands to report now.</summary>
        bool Active { get; }

        /// <summary>The hands for the game's frame: what it draws reaches the headset a frame or two later, and the poses are for then.</summary>
        void Read(out HandState left, out HandState right);

        /// <summary>
        /// Render thread: the hands at the display time of the headset frame being composed now, for what is placed in
        /// that frame directly rather than through the game (the wrist panel, the residual measure). A source that does
        /// not predict returns what <see cref="Read"/> does.
        /// </summary>
        void ReadNow(out HandState left, out HandState right);

        /// <summary>A vibration on one hand; a source without haptics ignores it.</summary>
        void Haptic(Hand hand, float amplitude, float seconds);
    }

    /// <summary>
    /// The hands, one immutable snapshot per game frame: what is held, what was pressed or released since the last
    /// frame. Everything game-side (input injection, laser pointer, hands, vehicles) reads this, never a source.
    /// </summary>
    internal static class VRInput
    {
        private static readonly IVRInputSource[] sources = new IVRInputSource[2];
        private static HandState left, right;
        private static VRButtons leftBefore, rightBefore;

        /// <summary>The headset's controllers; used whenever it is active, unless the desktop emulator is (see <see cref="Desktop"/>).</summary>
        public static IVRInputSource Headset
        {
            get => sources[0];
            set => sources[0] = value;
        }

        /// <summary>
        /// The desktop emulator; used when the headset source is not active, and also over an active headset while it has hands
        /// to report: it is only active while its control file has content, which is a test asking for those hands.
        /// </summary>
        public static IVRInputSource Desktop
        {
            get => sources[1];
            set => sources[1] = value;
        }

        /// <summary>A source was active for this frame's snapshot.</summary>
        public static bool Active { get; private set; }

        /// <summary>
        /// The screen is laid out for the hands this frame: the desktop emulator is active, or the headset's controllers are
        /// set up and the picture is VR's. Unlike <see cref="Active"/> it holds while the session is only Visible or
        /// Synchronized (the runtime's own menu, the state changes after the headset wakes), when OpenXR reports no input
        /// but the picture stays VR's; it follows the picture switch, which waits for the headset to be away for 2 s. What
        /// the HUD and the prompts show reads this, so they do not flicker with the session state.
        /// </summary>
        public static bool Layout { get; private set; }

        /// <summary>Counts <see cref="Update"/>s: which snapshot a reader got, for the trace (<see cref="Hands.HeldTrace"/>).</summary>
        public static int Sequence { get; private set; }

        /// <summary>Game thread, once per simulation frame, before anything reads the snapshot.</summary>
        public static void Update()
        {
            Sequence++;
            leftBefore = left.Buttons;
            rightBefore = right.Buttons;
            IVRInputSource source = InUse();
            Active = source != null;
            Layout = Active || sources[0] != null && Rendering.StereoPatches.VRPicture;
            if (Active)
                source.Read(out left, out right);
            else
                left = right = default;
        }

        /// <summary>
        /// The source that gives the hands now: the desktop emulator while it is active, else the headset while the picture is
        /// VR's (<see cref="Rendering.StereoPatches.VRPicture"/>), else none. Everything that stands down without VR reads
        /// <see cref="Active"/>, so it follows the picture to the game's while the headset is away and back with it.
        /// </summary>
        private static IVRInputSource InUse() =>
            sources[1]?.Active == true ? sources[1] : sources[0]?.Active == true && Rendering.StereoPatches.VRPicture ? sources[0] : null;

        public static HandState Get(Hand hand) => hand == Hand.Left ? left : right;

        /// <summary>
        /// The hands from the source in use now, not the frame's snapshot, for code that can run on any thread
        /// (<see cref="Tracking.HeightCalibration"/>). False when no source is active.
        /// </summary>
        public static bool TryReadLive(out HandState leftHand, out HandState rightHand)
        {
            IVRInputSource source = InUse();
            if (source == null)
            {
                leftHand = rightHand = default;
                return false;
            }
            source.Read(out leftHand, out rightHand);
            return true;
        }

        public static bool IsPressed(Hand hand, VRButtons button) => (Get(hand).Buttons & button) != 0;

        public static bool IsNewPressed(Hand hand, VRButtons button) => IsPressed(hand, button) && (Before(hand) & button) == 0;

        public static bool IsReleased(Hand hand, VRButtons button) => !IsPressed(hand, button) && (Before(hand) & button) != 0;

        public static void Haptic(Hand hand, float amplitude, float seconds)
        {
            IVRInputSource source = InUse() ?? sources[1];
            source?.Haptic(hand, amplitude, seconds);
        }

        private static VRButtons Before(Hand hand) => hand == Hand.Left ? leftBefore : rightBefore;
    }
}
