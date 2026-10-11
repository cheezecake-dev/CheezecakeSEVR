using System;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// A camera block's, a turret's or a searchlight's zoom in the headset: the game's own zoomed view on a small screen
    /// standing where the player was looking when the zoom began, magnified exactly as much as the game magnifies it, with
    /// the world round it at its normal scale. Zooming back out to 1x takes it down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game zooms by narrowing the camera's field of view: CAMERA_ZOOM_IN and OUT held (the hands: the right stick up
    /// and down through a camera block, A and B in a turret, <see cref="Input.VRControls"/>) or the mouse wheel step the
    /// block's target field of view between its definition's MaxFov and MinFov, and every update the block eases its own
    /// field of view half way there and writes it into MySector.MainCamera.FieldOfView (MyCameraBlock.UpdateAfterSimulation
    /// :391 and SetFov :345; MyLargeTurretBase :1761 and :2729; MySearchlight likewise). The eyes are drawn with the
    /// headset's own field of view and never take the game's, so in the headset the zoom changed nothing; narrowing the
    /// eyes instead would scale the whole world about the head, which is the kind of motion the player did not make.
    /// </para>
    /// <para>
    /// The magnification is the game's: the block's widest field of view (its definition's MaxFov, 1x, where the eyes show
    /// the world at its own size) over the one it has now, as tangents of the half-angles. The picture is the external
    /// view's pass (<see cref="ExternalView"/>, <see cref="ExternalView.Kind.Zoom"/>), drawn from the head along the inset's
    /// axis with a field of view <c>zoom</c> times narrower than the inset covers of the world behind it, into its
    /// 640x360 picture; the inset is that screen's quad layer, so it takes the dash screen's place while it is up. It is
    /// placed once per zoom, in the tracking space, which the block's camera carries: turning a turret turns the inset
    /// with the view, as a scope on it would. Only with a headset: without one the eyes are drawn with the game camera's
    /// field of view, so they zoom as a monitor does.
    /// </para>
    /// </remarks>
    internal static class ZoomInset
    {
        /// <summary>How far ahead of the head the inset stands, metres.</summary>
        public const float Distance = 1.2f;

        /// <summary>How wide the inset is, metres: about 33 degrees at <see cref="Distance"/>; its height is the picture's shape.</summary>
        public const float Width = 0.70f;

        /// <summary>The inset's height, metres.</summary>
        public const float Height = Width * ExternalView.TextureHeight / ExternalView.TextureWidth;

        /// <summary>The zoom the inset comes up at, and the one it goes down below: a little apart, so a zoom easing back to 1x does not flicker it.</summary>
        private const float OnAbove = 1.05f, OffBelow = 1.02f;

        /// <summary>The most the inset magnifies, whatever a block's definition allows (the pass's field of view stays above a tenth of a degree or so).</summary>
        private const float MaxZoom = 200f;

        /// <summary>Updates the zoom has to hold still before its level is logged.</summary>
        private const int SteadyUpdates = 10;

        /// <summary>Debug: no inset; the eyes ignore the zoom, as before (<see cref="RenderDebug"/> ZoomInset=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Debug: the inset's picture is drawn every other frame (<see cref="RenderDebug"/> ZoomInsetHalfRate=1).</summary>
        public static bool HalfRate { get; set; }

        // ---- game thread ----
        private static bool on;
        private static float lastZoom, loggedZoom;
        private static int steady, errors;

        // ---- render thread ----
        private static Matrix pose = Matrix.Identity;

        /// <summary>
        /// Game thread, as the camera goes to the renderer (<see cref="ExternalView"/>'s Capture): whether this frame's view
        /// is zoomed, and by how much. On, off and the level the zoom settles at are logged.
        /// </summary>
        internal static bool Wanted(out float zoom)
        {
            zoom = 1f;
            if (Off || errors >= 3)
                return Stop("turned off");
            try
            {
                if (!Widest(out float widest, out string block))
                    return Stop("the view is not a camera block, a turret or a searchlight any more");
                float field = MySector.MainCamera.FieldOfView;
                zoom = Magnification(widest, field);
                bool wanted = on ? zoom > OffBelow : zoom > OnAbove;
                if (!wanted)
                {
                    zoom = 1f;
                    return Stop("back to 1x");
                }
                if (!on)
                {
                    on = true;
                    steady = 0;
                    loggedZoom = zoom;
                    Log.Info($"Zoom inset: on, the {block} zoomed {zoom:0.0}x (field of view {MathHelper.ToDegrees(field):0.0} of {MathHelper.ToDegrees(widest):0.0} deg); the eyes stay at 1x");
                }
                else if (Math.Abs(zoom - lastZoom) > 1e-4f * zoom)
                    steady = 0;
                else if (++steady == SteadyUpdates && Math.Abs(zoom - loggedZoom) > 0.02f * loggedZoom)
                {
                    loggedZoom = zoom;
                    Log.Info($"Zoom inset: {zoom:0.0}x (field of view {MathHelper.ToDegrees(field):0.00} deg)");
                }
                lastZoom = zoom;
                return true;
            }
            catch (Exception e)
            {
                if (++errors >= 3)
                    Log.Error(e, "Zoom inset: reading the zoom failed three times; no inset for the rest of the run");
                else
                    Log.Error(e, "Zoom inset: reading the zoom failed");
                zoom = 1f;
                return Stop("reading the zoom failed");
            }
        }

        private static bool Stop(string why)
        {
            if (on)
                Log.Info($"Zoom inset: off, {why}");
            on = false;
            return false;
        }

        /// <summary>
        /// The block the player looks through, if it zooms: a camera block that is the view (on foot, from a seat, or as a
        /// turret control block's camera), a turret or a searchlight that is the view, and its widest field of view (1x), radians.
        /// </summary>
        private static bool Widest(out float widest, out string block)
        {
            widest = 0f;
            block = null;
            if (MySector.MainCamera == null)
                return false;
            switch (MySession.Static?.CameraController)
            {
                case MyCameraBlock camera when camera.IsActive:
                    widest = camera.BlockDefinition.MaxFov;
                    block = "camera block";
                    break;
                case MyLargeTurretBase turret:
                    widest = turret.BlockDefinition?.MaxFov ?? 0f;
                    block = "turret";
                    break;
                case MySearchlight searchlight:
                    widest = searchlight.BlockDefinition?.MaxFov ?? 0f;
                    block = "searchlight";
                    break;
                default:
                    return false;
            }
            return widest > 0f;
        }

        /// <summary>How many times a view of <paramref name="field"/> radians magnifies one of <paramref name="widest"/> (both vertical), 1 at the widest.</summary>
        internal static float Magnification(float widest, float field)
        {
            if (!(field > 0f) || !(widest > 0f))
                return 1f;
            double zoom = Math.Tan(widest / 2.0) / Math.Tan(Math.Min(field, 3.1f) / 2.0);
            return (float)Math.Min(Math.Max(zoom, 1.0), MaxZoom);
        }

        /// <summary>The inset in the tracking space, facing +z (towards where the head was). Render thread.</summary>
        internal static Matrix Pose => pose;

        /// <summary>
        /// Render thread, the first frame of a zoom: the inset stands <see cref="Distance"/> ahead along where the head looks,
        /// upright (its top towards the tracking space's up, unless the head looks straight up or down), facing the head.
        /// </summary>
        internal static void Place(in Matrix head)
        {
            Vector3 forward = head.Forward;
            if (forward.LengthSquared() < 1e-6f)
                forward = Vector3.Forward;
            forward.Normalize();
            Vector3 up = Vector3.Up - forward * forward.Y;
            if (up.LengthSquared() < 0.01f)
                up = head.Up - forward * Vector3.Dot(head.Up, forward);
            if (up.LengthSquared() < 1e-6f)
                up = Vector3.Backward;
            up.Normalize();
            pose = Matrix.CreateWorld(head.Translation + forward * Distance, forward, up);
            Vector3 at = pose.Translation;
            Log.Info($"Zoom inset: placed {Distance:0.00} m ahead along the gaze, {Width:0.00} m wide ({MathHelper.ToDegrees((float)(2.0 * Math.Atan(Width / 2f / Distance))):0} deg), at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}) in the tracking space");
        }

        /// <summary>
        /// Render thread: the camera the zoomed picture is drawn from, in the tracking space - at the head now, looking along
        /// the inset's axis, upright with it - and its half-angle tangents: what the inset covers of the world behind it
        /// from where it was placed, narrowed <paramref name="zoom"/> times.
        /// </summary>
        internal static Matrix Eye(in Matrix head, float zoom, out EnvMatrices.FovTangents fov)
        {
            float z = MathHelper.Clamp(zoom, 1f, MaxZoom);
            float h = Width / 2f / Distance / z, v = Height / 2f / Distance / z;
            fov = new EnvMatrices.FovTangents { Left = -h, Right = h, Up = v, Down = -v };
            return Matrix.CreateWorld(head.Translation, pose.Forward, pose.Up);
        }
    }
}
