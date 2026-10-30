// ActionNames: what the player presses for an action, named from the bindings in use, for every label and prompt.
using System.Collections.Generic;
using SpaceEngineersVR.Gui;

namespace SpaceEngineersVR.Input.Bindings
{
    /// <summary>
    /// The one source of control names for the labels beside the hands (<see cref="Rendering.HandHints"/>), the wrist panel's
    /// Controls page, the hints (<see cref="ControlHints"/>) and the game's prompts (<see cref="VRPrompts"/>): the bindings in
    /// use (<see cref="ControllerBindings.InUse"/>) named as the controller in hand calls its controls (<see cref="BindingNames"/>).
    /// Game thread.
    /// </summary>
    internal static class ActionNames
    {
        // With no controller seen yet the bindings in use are the Quest's (DefaultBindings.For), so they are named as a Quest names them.
        // Made again only when the bindings in use or the controller change, not per call.
        private static BindingNames Now
        {
            get
            {
                string profile = ControllerBindings.ActiveProfile ?? DefaultBindings.QuestProfile;
                IReadOnlyList<Binding> list = ControllerBindings.InUse;
                if (!ReferenceEquals(list, namedList) || profile != namedProfile)
                {
                    bool[] has = OnHand(profile);
                    named = new BindingNames(list, profile, (hand, control) => has[new ControlRef(hand, control).Index], ControllerBindings.InUseMirrored);
                    namedList = list;
                    namedProfile = profile;
                    version++;
                }
                return named;
            }
        }

        private static BindingNames named;
        private static int version;

        /// <summary>Changes whenever the bindings in use or the controller do, so what was named from them is named again.</summary>
        public static int Version
        {
            get
            {
                _ = Now;
                return version;
            }
        }
        private static IReadOnlyList<Binding> namedList;
        private static string namedProfile;

        // Per control (ControlRef.Index): the controller's drawing lists it on that hand; one with no drawing has them all.
        private static bool[] OnHand(string profile)
        {
            var has = new bool[2 * ControlRef.ControlCount];
            for (int h = 0; h < 2; h++)
            {
                bool drawn = ControllerDiagrams.TryGet(profile, (Hand)h, out ControllerDiagram d);
                for (int c = 0; c < ControlRef.ControlCount; c++)
                {
                    var control = new ControlRef((Hand)h, (ControllerControl)c);
                    if (!drawn)
                        has[control.Index] = true;
                    else
                        foreach (var anchor in d.Anchors)
                            if (anchor.Control == control.Control)
                                has[control.Index] = true;
                }
            }
            return has;
        }

        /// <summary>The bindings in use, named for the controller in hand: what <see cref="Find"/> and the rest read.</summary>
        public static BindingNames InUse => Now;

        /// <summary>The controller's name, as the bindings page titles it ("HP Reverb G2"); "Meta Quest Touch" with none.</summary>
        public static string Controller => ControllerDiagrams.Title(ControllerBindings.ActiveProfile) ?? "Meta Quest Touch";

        /// <inheritdoc cref="BindingNames.Find"/>
        public static Binding Find(VRAction action, bool mirrored = false, Hand? hand = null) => Now.Find(action, mirrored, hand);

        /// <inheritdoc cref="BindingNames.Also"/>
        public static string Also(Binding main, VRAction other, string what, bool mirrored = false) => Now.Also(main, other, what, mirrored);

        /// <inheritdoc cref="BindingNames.Control"/>
        public static string Control(Hand hand, ControllerControl control) => Now.Control(hand, control);

        /// <inheritdoc cref="BindingNames.Qualified"/>
        public static string Qualified(Hand hand, ControllerControl control) => Now.Qualified(hand, control);

        /// <inheritdoc cref="BindingNames.Name"/>
        public static string Name(VRAction action, bool mirrored = false) => Now.Name(action, mirrored);

        /// <inheritdoc cref="BindingNames.Describe"/>
        public static string Describe(VRAction action, bool mirrored = false) => Now.Describe(action, mirrored);

        /// <summary>
        /// For a log line: what is pressed for the action now (<see cref="Name"/>), or the action said to be unbound, so a log
        /// names the control the player's bindings have rather than the default's.
        /// </summary>
        public static string ForLog(VRAction action) => Name(action) ?? action + " (not bound)";
    }
}
