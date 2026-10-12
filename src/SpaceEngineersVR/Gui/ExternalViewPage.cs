using System;
using SpaceEngineersVR.Rendering;
using VRageMath;

namespace SpaceEngineersVR.Gui
{
    /// <summary>
    /// The wrist panel's External view page (<see cref="QuickActionsScreen"/>): the external view screen (<see cref="ExternalView"/>)
    /// on and off, its third-person camera taken round what it follows on a sphere - left and right round it, over and under
    /// it, nearer and farther (the sphere's radius) - always looking at it, the screen's size, and Reset. Each press is one
    /// step, seen on the screen at once, and kept in the settings file. The screen itself is moved by gripping it
    /// (<see cref="Hands.ViewScreenGrab"/>); Reset also puts it back on the dash.
    /// </summary>
    /// <remarks>
    /// The page is a cross of orbit buttons with what the screen shows in the middle, Back and Reset above it beside Orbit up,
    /// Nearer and Farther beside Orbit down, and the screen's size and its switch along the foot. Orbit and distance move the
    /// third-person camera only; while the screen shows one of the ship's camera blocks they are greyed.
    /// </remarks>
    internal static class ExternalViewPage
    {
        private const string Hud = GuiTextures.Hud;

        /// <summary>The game's white chevron (it points right), turned for each way the camera goes.</summary>
        private const string Chevron = "Textures\\GUI\\Icons\\buttons\\ArrowSingle.dds";

        internal const string PageIcon = Hud + "CameraSpectator.png";

        /// <summary>Degrees round per press: a few presses are a clear new angle, twelve go all the way round.</summary>
        public const float OrbitStep = 15f;

        /// <summary>Degrees over or under per press.</summary>
        public const float ElevationStep = 10f;

        /// <summary>Metres of screen width per press.</summary>
        public const float SizeStep = 0.05f;

        /// <summary>
        /// Distance steps: three to a doubling (each about a quarter more), from 0.4 of the distance that suits what is
        /// followed out to 16 times it. That distance grows with a grid's size (and stops at 400 m for the biggest), so the
        /// far end clears any grid: 6.6 km from the biggest station, 58 m from a character.
        /// </summary>
        private const int StepsPerDoubling = 3, NearestStep = -4, FarthestStep = 12;

        /// <summary>The page in reading order, three across; null is the middle of the orbit cross, which says what the screen shows.</summary>
        internal static QuickActionsScreen.QuickAction[] Build(Action back)
        {
            Func<bool> chase = () => ExternalView.Usable && !VRSettings.ExternalViewIsCamera;
            return new[]
            {
                new QuickActionsScreen.QuickAction("Back", "Back to the quick actions.", Hud + "RotateCounterClockWise.png", back),
                new QuickActionsScreen.QuickAction("Orbit up", "Takes the camera up over what it follows.", Chevron,
                    () => Elevate(+1), Lift, canUse: () => chase() && CanElevate(+1), whiteIcon: true, iconRotation: -MathHelper.PiOver2),
                new QuickActionsScreen.QuickAction("Reset", "Puts the camera back behind and above at its first distance, and the screen back on the dash at its first size.",
                    Hud + "ReloadGame.png", Reset, canUse: () => ExternalView.Usable),

                new QuickActionsScreen.QuickAction("Orbit left", "Takes the camera round to the left of what it follows.", Chevron,
                    () => Orbit(+1), Round, canUse: chase, whiteIcon: true, iconRotation: MathHelper.Pi),
                null,
                new QuickActionsScreen.QuickAction("Orbit right", "Takes the camera round to the right of what it follows.", Chevron,
                    () => Orbit(-1), Round, canUse: chase, whiteIcon: true),

                new QuickActionsScreen.QuickAction("Nearer", "Brings the camera nearer to what it follows.", Hud + "MoveCloser.png",
                    () => Distance(-1), Far, canUse: () => chase() && CanDistance(-1)),
                new QuickActionsScreen.QuickAction("Orbit down", "Takes the camera down under what it follows.", Chevron,
                    () => Elevate(-1), Lift, canUse: () => chase() && CanElevate(-1), whiteIcon: true, iconRotation: MathHelper.PiOver2),
                new QuickActionsScreen.QuickAction("Farther", "Takes the camera farther from what it follows.", Hud + "MoveFurther.png",
                    () => Distance(+1), Far, canUse: () => chase() && CanDistance(+1)),

                new QuickActionsScreen.QuickAction("Smaller", "Makes the screen smaller.", Hud + "ScaleDown.png",
                    () => Size(-1), Width, canUse: () => CanSize(-1)),
                new QuickActionsScreen.QuickAction("External view", "Turns the external view screen on or off.", PageIcon,
                    Switch, state: () => ExternalView.On, canUse: () => ExternalView.Usable),
                new QuickActionsScreen.QuickAction("Larger", "Makes the screen larger.", Hud + "ScaleUp.png",
                    () => Size(+1), Width, canUse: () => CanSize(+1)),
            };
        }

        // ---- What each control says under its name. ----

        private static string Round()
        {
            float orbit = VRSettings.ExternalViewOrbit;
            if (Math.Abs(orbit) < 0.5f)
                return "Behind";
            if (Math.Abs(orbit) > 179.5f)
                return "In front";
            return orbit > 0f ? $"{orbit:0}\u00B0 left" : $"{-orbit:0}\u00B0 right";
        }

        private static string Lift()
        {
            float lift = ExternalView.LiftDegrees(VRSettings.ExternalViewElevation);
            if (Math.Abs(lift) < 0.5f)
                return "Level";
            return lift > 0f ? $"{lift:0}\u00B0 above" : $"{-lift:0}\u00B0 below";
        }

        private static string Far()
        {
            double radius = ExternalView.ChaseRadius;
            if (radius <= 0.0)
                return $"x{VRSettings.ExternalViewDistance:0.##}";
            if (radius < 10.0)
                return $"{radius:0.0} m";
            return radius < 1000.0 ? $"{radius:0} m" : $"{radius / 1000.0:0.0} km";
        }

        private static string Width() => $"{VRSettings.ExternalViewSize * 100f:0} cm";

        /// <summary>What the middle of the cross says: what the screen shows now.</summary>
        internal static string Showing() => ExternalViewSource.Describe();

        /// <summary>The second line in the middle of the cross.</summary>
        internal static string Hint()
        {
            if (!ExternalView.Usable)
                return "Not available";
            if (VRSettings.ExternalViewIsCamera)
                return "Orbit moves third person";
            return VRSettings.MoveViewByHand && !Hands.ViewScreenGrab.Off ? "Grip the screen to move it" : "Orbit the camera";
        }

        // ---- The steps. ----

        private static void Orbit(int way)
        {
            float orbit = (float)Math.Round(VRSettings.ExternalViewOrbit / OrbitStep) * OrbitStep + way * OrbitStep;
            if (orbit > 180f)
                orbit -= 360f;
            else if (orbit <= -180f)
                orbit += 360f;
            VRSettings.ExternalViewOrbit = orbit;
            Changed("orbit " + Round());
        }

        private static bool CanElevate(int way)
        {
            float lift = ExternalView.StartLiftDegrees + Snap(VRSettings.ExternalViewElevation, ElevationStep) + way * ElevationStep;
            return Math.Abs(lift) <= ExternalView.MaxLiftDegrees;
        }

        private static void Elevate(int way)
        {
            if (!CanElevate(way))
                return;
            VRSettings.ExternalViewElevation = Snap(VRSettings.ExternalViewElevation, ElevationStep) + way * ElevationStep;
            Changed("camera " + Lift());
        }

        private static int DistanceStep => (int)Math.Round(StepsPerDoubling * Math.Log(Math.Max(VRSettings.ExternalViewDistance, 0.01f), 2.0));

        private static bool CanDistance(int way)
        {
            int next = DistanceStep + way;
            return next >= NearestStep && next <= FarthestStep;
        }

        private static void Distance(int way)
        {
            if (!CanDistance(way))
                return;
            VRSettings.ExternalViewDistance = (float)Math.Pow(2.0, (DistanceStep + way) / (double)StepsPerDoubling);
            // The words follow once the camera has been placed again; the multiple is what was chosen.
            Changed($"distance x{VRSettings.ExternalViewDistance:0.00}");
        }

        /// <summary>
        /// The next mark of the size steps that way: a size between marks (from the settings file) goes to the nearer mark
        /// that way (23 cm: 25 larger, 20 smaller), one on a mark to the next.
        /// </summary>
        private static float NextSize(int way)
        {
            double marks = VRSettings.ExternalViewSize / SizeStep;
            double next = way > 0 ? Math.Floor(marks + 0.01) + 1.0 : Math.Ceiling(marks - 0.01) - 1.0;
            return (float)(next * SizeStep);
        }

        private static bool CanSize(int way)
        {
            float next = NextSize(way);
            return next >= VRSettings.ExternalViewSizeMin - 0.001f && next <= VRSettings.ExternalViewSizeMax + 0.001f;
        }

        private static void Size(int way)
        {
            if (!CanSize(way))
                return;
            VRSettings.ExternalViewSize = NextSize(way);
            Changed("screen " + Width());
        }

        private static void Switch() => ExternalViewSource.Toggle();

        private static void Reset()
        {
            VRSettings.ExternalViewOrbit = 0f;
            VRSettings.ExternalViewElevation = 0f;
            VRSettings.ExternalViewDistance = 1f;
            VRSettings.ExternalViewSize = VRSettings.ExternalViewSizeDefault;
            ExternalView.PutScreenBack();
            Changed("reset: behind and above, first distance, screen on the dash at " + Width());
        }

        private static float Snap(float value, float step) => (float)Math.Round(value / step) * step;

        private static void Changed(string what)
        {
            Log.Info("Wrist panel: External view: " + what);
            ExternalViewSource.QueueSave();
        }
    }
}
