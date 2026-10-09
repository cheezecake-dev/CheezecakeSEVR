using System;
using HarmonyLib;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using SpaceEngineersVR.Gui;
using SpaceEngineersVR.Input;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Where the GUI panel (<see cref="HudPanel"/>) sits while a menu is open. In plain gameplay only the HUD shows,
    /// head-locked like a visor. A menu is a screen that takes the focus and blocks gameplay input (the screen with
    /// focus, MyScreenManager.GetScreenWithFocus, is something other than the gameplay screen): then the panel is
    /// left where it was when the menu opened - <see cref="VRSettings.MenuDistance"/> ahead of the head, upright,
    /// facing it - and stays there in the tracking space until no menu is open, so the player can look around it.
    /// It is the same panel, so the HUD stays with the menu meanwhile.
    /// </summary>
    /// <remarks>
    /// The one menu that is not left in the world is the quick-actions screen (<see cref="QuickActionsScreen"/>, opened from
    /// <see cref="WristButton"/>): while it has the focus the panel is on the left wrist instead, <see cref="WristWidth"/> wide,
    /// following the hand every frame (<see cref="WristPose"/>), so the other hand can point at it (<see cref="Laser"/>
    /// reads the panel's placement, wherever it is). If the hand cannot be tracked the panel falls back to the world
    /// placement, so the buttons stay reachable.
    /// </remarks>
    internal static class WorldMenu
    {
        /// <summary>Width, metres, of the panel when it is on the wrist.</summary>
        public const float WristWidth = 0.30f;

        /// <summary>From the middle of the palm (where the grip pose is) along the forearm to the wrist, metres.</summary>
        public const float WristFromPalm = 0.08f;

        /// <summary>From the wrist toward the eyes, metres: the panel's middle hovers this far above the back of the wrist.</summary>
        public const float WristLift = 0.12f;

        /// <summary>The panel is never brought closer to the eyes than this (metres) by the lift, however near the hand is held.</summary>
        public const float WristNearest = 0.25f;

        /// <summary>Debug: menus are head-locked like the HUD (<see cref="RenderDebug"/> WorldMenu=0).</summary>
        public static bool Off { get; set; }

        /// <summary>A menu has the focus. Written on the game thread as the screens are drawn, read on the render thread.</summary>
        private static volatile bool open;

        /// <summary>The menu that has the focus is the quick-actions screen, which belongs on the wrist.</summary>
        private static volatile bool wrist;

        /// <summary>The menu that has the focus is the actions wheel (<see cref="ActionsWheel"/>), which stands where the hand opened it.</summary>
        private static volatile bool wheel;

        private static bool placed, onWrist, onWheel;

        /// <summary>
        /// Where the head was and which way it faced (along the horizon) when the menu opened. The panel stands
        /// <see cref="VRSettings.MenuDistance"/> from the first along the second, worked out each frame, so dragging
        /// the distance slider in the options screen moves an open panel; its width follows the setting in
        /// <see cref="HudPanel.Show"/>.
        /// </summary>
        private static Vector3 anchor, ahead;

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyScreenManager), nameof(MyScreenManager.Draw)),
                postfix: new HarmonyMethod(typeof(WorldMenu), nameof(Track)));
        }

        // Every frame, on the thread that owns the screens, so the list is not changing underneath.
        private static void Track()
        {
            try
            {
                MyGuiScreenBase focus = MyScreenManager.GetScreenWithFocus();
                open = focus != null && !(focus is MyGuiScreenGamePlay);
                wrist = open && focus is QuickActionsScreen;
                wheel = open && focus is ActionsWheelScreen;
            }
            catch (Exception e)
            {
                open = wrist = wheel = false;
                Log.Error(e, "Could not tell whether a menu is open");
            }
        }

        /// <summary>
        /// Render thread, each frame the panel is shown: whether the panel is world-locked, and its pose in the tracking
        /// space if so. The pose is taken from the head the first frame a menu is open and kept until none is.
        /// </summary>
        /// <param name="head">The head in the tracking space.</param>
        public static bool Locked(in Matrix head, out Matrix panel) => Locked(head, out panel, out _);

        /// <summary>
        /// As <see cref="Locked(in Matrix, out Matrix)"/>, and <paramref name="width"/> is the panel's width in metres when it
        /// has a width of its own (the wrist panel), and 0 when it is sized by an angle at a distance, like the others.
        /// </summary>
        public static bool Locked(in Matrix head, out Matrix panel, out float width)
        {
            width = 0f;
            if (Off || !open)
            {
                if (placed || onWrist || onWheel)
                    Log.Info("Menu closed; the panel is a visor again");
                placed = onWrist = onWheel = false;
                panel = default;
                return false;
            }

            // The actions wheel: where the hand was when it opened, facing the head as it was then, fixed until it closes.
            if (wheel && ActionsWheel.TryGetPanel(out panel))
            {
                if (!onWheel)
                {
                    Vector3 at = panel.Translation;
                    Log.Info($"Menu opened as the actions wheel; panel {ActionsWheel.PanelWidth} m wide, fixed at the hand ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) in the tracking space");
                }
                onWheel = true;
                placed = onWrist = false;
                width = ActionsWheel.PanelWidth;
                return true;
            }
            onWheel = false;

            if (wrist && WristButton.Enabled && WristButton.TryGetWristGrip(out Matrix grip) && WristPose(grip, head, out panel))
            {
                if (!onWrist)
                    Log.Info($"Menu opened on the wrist; panel {WristWidth} m wide follows the hand");
                onWrist = true;
                placed = false;
                width = WristWidth;
                return true;
            }

            onWrist = false;
            if (!placed)
            {
                Place(head);
                placed = true;
                Vector3 at = anchor + ahead * VRSettings.MenuDistance;
                Log.Info($"Menu opened; panel placed {VRSettings.MenuDistance} m ahead at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) in the tracking space");
            }
            panel = Matrix.CreateWorld(anchor + ahead * VRSettings.MenuDistance, ahead, Vector3.Up);
            return true;
        }

        /// <summary>
        /// The panel on the wrist, from the hand's grip pose and the head, both in the tracking space: the wrist is
        /// <see cref="WristFromPalm"/> back along the forearm from the grip (the grip's own Z axis points back along the
        /// forearm), and the panel's middle is <see cref="WristLift"/> from the wrist toward the eyes, turned to face them
        /// with its top toward the top of the head. Only the grip's position and that one axis are used, so the panel does
        /// not depend on how the controller is rolled round the arm. False when the hand is too near the eyes to have a
        /// direction to them (or has no pose).
        /// </summary>
        internal static bool WristPose(in Matrix grip, in Matrix head, out Matrix panel)
        {
            panel = default;
            Vector3 along = grip.Backward;
            float armLength = along.Length();
            Vector3 wristAt = grip.Translation;
            if (armLength > 0.5f)
                wristAt += along / armLength * WristFromPalm;

            Vector3 toHead = head.Translation - wristAt;
            float distance = toHead.Length();
            if (!(distance > 0.05f) || float.IsInfinity(distance))
                return false;
            toHead /= distance;

            // Up the way the head's top points, unless the head looks along it (a panel seen edge-on has no sensible top).
            Vector3 up = head.Up;
            if (Math.Abs(Vector3.Dot(up, toHead)) > 0.98f)
                up = head.Forward;

            float lift = Math.Min(WristLift, Math.Max(0f, distance - WristNearest));
            // CreateWorld faces the panel's +Z opposite to 'forward': the panel looks back at the eyes.
            panel = Matrix.CreateWorld(wristAt + toHead * lift, -toHead, up);
            return true;
        }

        /// <summary>
        /// Straight ahead of the head along the horizon, at the head's height, standing upright and facing the head.
        /// Looking up or down when the menu opens does not tilt it; looking straight up or down uses the way the top of
        /// the head points instead.
        /// </summary>
        private static void Place(in Matrix head)
        {
            Vector3 forward = head.Forward;
            Vector3 along = new Vector3(forward.X, 0f, forward.Z);
            if (along.LengthSquared() < 0.04f)
            {
                Vector3 up = head.Up;
                along = forward.Y < 0f ? new Vector3(up.X, 0f, up.Z) : new Vector3(-up.X, 0f, -up.Z);
            }
            if (along.LengthSquared() < 1e-6f)
                along = Vector3.Forward;
            along.Normalize();
            anchor = head.Translation;
            ahead = along;
        }
    }
}
