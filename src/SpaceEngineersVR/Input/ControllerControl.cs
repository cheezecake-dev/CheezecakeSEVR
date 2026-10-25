namespace SpaceEngineersVR.Input
{
    /// <summary>
    /// A control a VR controller can have, named by what it is rather than by what a particular maker calls it, so one
    /// diagram or binding list can speak of every controller. A controller has only some of these.
    /// </summary>
    internal enum ControllerControl
    {
        /// <summary>The index-finger trigger (also a simple controller's select button).</summary>
        Trigger,
        /// <summary>The grip, squeezed by the middle fingers (a squeeze on Quest, Index and PICO, a button on a Vive wand).</summary>
        Grip,
        /// <summary>The thumbstick, pushed in any direction.</summary>
        Thumbstick,
        /// <summary>The thumbstick pressed straight down.</summary>
        ThumbstickClick,
        /// <summary>The lower face button: A on the right controller, X on the left of a Quest controller.</summary>
        ButtonLower,
        /// <summary>The upper face button: B on the right controller, Y on the left of a Quest controller.</summary>
        ButtonUpper,
        /// <summary>The Menu button.</summary>
        Menu,
        /// <summary>The trackpad as a whole: touched, or its position read.</summary>
        Trackpad,
        /// <summary>The trackpad pressed at its top edge.</summary>
        TrackpadUp,
        /// <summary>The trackpad pressed at its bottom edge.</summary>
        TrackpadDown,
        /// <summary>The trackpad pressed at its left edge.</summary>
        TrackpadLeft,
        /// <summary>The trackpad pressed at its right edge.</summary>
        TrackpadRight,
        /// <summary>The trackpad pressed in the middle.</summary>
        TrackpadCentre,
        /// <summary>The bumper (shoulder button) above the trigger.</summary>
        Bumper,
        /// <summary>The thumbrest beside the thumbstick, which senses a resting thumb.</summary>
        Thumbrest,
        /// <summary>The D-pad pressed up.</summary>
        DpadUp,
        /// <summary>The D-pad pressed down.</summary>
        DpadDown,
        /// <summary>The D-pad pressed left.</summary>
        DpadLeft,
        /// <summary>The D-pad pressed right.</summary>
        DpadRight,
        /// <summary>The View button (the small button beside a D-pad that shows the overview).</summary>
        View,
        /// <summary>The system button (Oculus, Steam, Vive or PICO button), which the runtime usually keeps for itself.</summary>
        System,
        /// <summary>The trackpad squeezed firmly (a Valve Index controller's, whose force the runtime turns into a press).</summary>
        TrackpadForce,
    }
}
