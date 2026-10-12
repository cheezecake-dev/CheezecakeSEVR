using System.Collections.Generic;
using SpaceEngineersVR.Tracking;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The grip fit (<see cref="GripFit"/>) as sliders in Options > VR: the turn and shift of the astronaut's palm on the
    /// controllers in use. They are not in the settings file as settings of their own: the fit is stored per controller
    /// profile in <c>gripFit</c> (<see cref="VRSettings.GripFit"/>), and each slider reads and writes that profile's number.
    /// Moving one applies at once and has the settings file written, as the RenderDebug keys do.
    /// </summary>
    internal static class GripFitSettings
    {
        public const string GroupName = "Hand fit";

        private const string FileSection = "Hands";

        private static readonly Slider[] sliders =
        {
            new Slider("gripFitPitch", "Turn pitch", GripFit.Axis.Pitch, -90f, 90f, 1f, "deg", 1f,
                "Tips the hand about the controller's side-to-side axis, fingers up or down. Fits the hand to the controllers in use."),
            new Slider("gripFitRoll", "Turn roll", GripFit.Axis.Roll, -90f, 90f, 1f, "deg", 1f,
                "Rolls the hand about the controller's forward axis, as turning a key. Fits the hand to the controllers in use."),
            new Slider("gripFitYaw", "Turn yaw", GripFit.Axis.Yaw, -90f, 90f, 1f, "deg", 1f,
                "Swings the hand about the controller's up axis, fingers left or right. Fits the hand to the controllers in use."),
            new Slider("gripFitX", "Shift side", GripFit.Axis.X, -50f, 50f, 1f, "cm", 0.1f,
                "Moves the hand sideways along the controller. The left hand mirrors it, so one number fits both. Fits the hand to the controllers in use."),
            new Slider("gripFitY", "Shift up", GripFit.Axis.Y, -50f, 50f, 1f, "cm", 0.1f,
                "Moves the hand up along the controller. Fits the hand to the controllers in use."),
            new Slider("gripFitZ", "Shift back", GripFit.Axis.Z, -50f, 50f, 1f, "cm", 0.1f,
                "Moves the hand back along the controller, toward you. Fits the hand to the controllers in use."),
        };

        /// <summary>The sliders, in screen order. Not in <see cref="VRSettings.All"/>.</summary>
        public static IReadOnlyList<Setting> All { get; } = sliders;

        /// <summary>The group's caption: what it fits and the controller profile it fits.</summary>
        public static string Caption => "Hand fit for " + GripFit.ProfilesInUse();

        public static VROptionsTabs.GroupButton ResetButton { get; } = new VROptionsTabs.GroupButton("Reset hand fit",
            "Forgets the fit for the controllers in use, so the hand sits the way the plugin places it. Defaults does the same, and the other controllers' fits stay.", Reset);

        /// <summary>Puts the fit stored for the controllers in use in the sliders, without storing it again.</summary>
        public static void Load()
        {
            GripFit.Fit fit = GripFit.InUseFit();
            foreach (Slider slider in sliders)
                slider.Force(slider.Of(fit));
        }

        private static void Reset()
        {
            GripFit.Reset();
            Load();
        }

        /// <summary>A slider for one number of the fit; it keeps the fit of the controllers in use in step with itself.</summary>
        private sealed class Slider : NumberSetting
        {
            private readonly GripFit.Axis axis;

            public Slider(string key, string label, GripFit.Axis axis, float min, float max, float step, string unit, float displayScale, string tooltip)
                : base(key, label, FileSection, 0f, min, max, step, unit, decimals: displayScale == 1f ? 0 : 1, displayScale: displayScale, tooltip: tooltip)
            {
                this.axis = axis;
            }

            public float Of(GripFit.Fit fit)
            {
                switch (axis)
                {
                    case GripFit.Axis.Pitch: return fit.Turn.X;
                    case GripFit.Axis.Roll: return fit.Turn.Y;
                    case GripFit.Axis.Yaw: return fit.Turn.Z;
                    case GripFit.Axis.X: return fit.Shift.X;
                    case GripFit.Axis.Y: return fit.Shift.Y;
                    default: return fit.Shift.Z;
                }
            }

            public override void Set(float? value)
            {
                base.Set(value);
                if (Pending.HasValue)
                    GripFit.Set(axis, Pending.Value, quiet: true);
            }
        }
    }
}
