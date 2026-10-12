// ExternalView, game thread: records the camera each frame and works out the chase camera behind the character.

using System;
using System.Diagnostics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class ExternalView
    {
        /// <summary>
        /// Game thread, as the camera goes to the renderer: what the picture is for this frame (<see cref="Kind"/>) and, for
        /// the dash screen, the chase camera, in the game camera's own space; for the zoom inset and the death view, the
        /// game camera's field of view (and the zoom). Keyed by the view matrix so that the renderer, which may be a message
        /// behind, pairs it with the ship as that frame drew it (the same way <see cref="Tracking.CameraHeadLink"/> pairs
        /// the head). A frame that wants no picture is kept too, so the renderer never takes an older frame's for it.
        /// </summary>
        private static void Capture(MatrixD viewMatrix)
        {
            if (!available)
                return;
            try
            {
                Kind kind = Kind.None;
                MatrixD pose = MatrixD.Identity;
                float field = 0f, zoom = 1f;
                if (DeathView.Wanted())
                {
                    kind = Kind.Death;
                    field = GameField();
                }
                else if (ZoomInset.Wanted(out zoom))
                {
                    kind = Kind.Zoom;
                    field = GameField();
                }
                else if (On && TryViewpoint(out MatrixD camera, out field))
                {
                    kind = Kind.Screen;
                    pose = camera * viewMatrix;
                }
                lock (gate)
                {
                    views[next] = viewMatrix;
                    poses[next] = pose;
                    fields[next] = field;
                    zooms[next] = zoom;
                    kinds[next] = kind;
                    stamps[next] = Stopwatch.GetTimestamp();
                    next = (next + 1) % Capacity;
                }
            }
            catch (Exception e)
            {
                if (!captureFailureLogged)
                {
                    captureFailureLogged = true;
                    Log.Error(e, "External view: could not place the chase camera");
                }
            }
        }

        /// <summary>The game camera's vertical field of view, radians, within what the pass draws with.</summary>
        private static float GameField() =>
            MathHelper.Clamp(MySector.MainCamera?.FieldOfView ?? MathHelper.ToRadians(FieldOfViewDegrees), 1e-4f, MaxFieldOfView);

        /// <summary>
        /// Where the picture is taken from this frame: the chosen camera block's view (its world matrix and field of view,
        /// read only: the player's own view is never moved to it, and the block is never made the active camera), or the
        /// chase camera when that is the source, or when the chosen camera cannot be used now (<see cref="ExternalViewSource"/>
        /// says so once).
        /// </summary>
        private static bool TryViewpoint(out MatrixD camera, out float field)
        {
            field = MathHelper.ToRadians(FieldOfViewDegrees);
            if (ExternalViewSource.TryCameraView(out camera, out float cameraField))
            {
                field = MathHelper.Clamp(cameraField, MinFieldOfView, MaxFieldOfView);
                return true;
            }
            return TryChaseCamera(out camera);
        }

        /// <summary>
        /// The chase camera's world matrix: behind and above what the player controls, looking at it, horizon level
        /// (up is against natural gravity; in space, up is the ship's own).
        /// </summary>
        private static bool TryChaseCamera(out MatrixD camera)
        {
            camera = MatrixD.Identity;
            MySession session = MySession.Static;
            var controlled = session?.ControlledEntity;
            if (controlled == null || session.IsCameraUserControlledSpectator())
                return false;

            MatrixD frame;
            Vector3D center;
            double distance, height;
            MyEntity entity = controlled.Entity;
            bool onFoot = entity is MyCharacter;
            if (entity is MyCharacter character)
            {
                frame = character.PositionComp.WorldMatrixRef;
                center = frame.Translation + frame.Up * 1.0;
                distance = 3.5;
                height = 1.0;
            }
            else
            {
                MyCubeGrid grid = (entity as MyCubeBlock)?.CubeGrid ?? entity?.GetTopMostParent() as MyCubeGrid;
                if (grid == null)
                    return false;
                frame = grid.PositionComp.WorldMatrixRef;
                BoundingSphereD volume = grid.PositionComp.WorldVolume;
                center = volume.Center;
                distance = MathHelper.Clamp(volume.Radius * 2.4 + 3.0, 6.0, 400.0);
                height = distance * 0.28;
            }

            // The wrist panel's External view page: nearer or farther (a multiple, so it suits a character and the biggest
            // station alike), and round and over or under what is followed, on the sphere through where the camera starts.
            double scale = VRSettings.ExternalViewDistance;
            distance *= scale;
            height *= scale;
            ChaseRadius = Math.Sqrt(distance * distance + height * height);
            ChasingCharacter = onFoot;

            Vector3 gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(center);
            return Compose(frame, center, gravity, distance, height,
                MathHelper.ToRadians(VRSettings.ExternalViewOrbit), MathHelper.ToRadians(VRSettings.ExternalViewElevation), out camera);
        }

        /// <summary>The third-person camera's distance from what it follows, metres, the last time it was placed (for the wrist panel's words). Zero before then.</summary>
        public static double ChaseRadius { get; private set; }

        /// <summary>The third-person camera followed a character (on foot) the last time it was placed, not a grid.</summary>
        public static bool ChasingCharacter { get; private set; }

        /// <summary>Where the third-person camera starts, degrees over what it follows: its height over its distance, as <see cref="TryChaseCamera"/> sets them (the same for a character and a grid, near enough).</summary>
        public const float StartLiftDegrees = 15.6f;

        /// <summary>The camera's height over its horizon, degrees, for an elevation (<see cref="VRSettings.ExternalViewElevation"/>), clamped as <see cref="Compose"/> clamps it.</summary>
        public static float LiftDegrees(float elevation) =>
            MathHelper.Clamp(StartLiftDegrees + elevation, -MaxLiftDegrees, MaxLiftDegrees);

        /// <summary>
        /// The chase camera for something at <paramref name="center"/> facing along <paramref name="frame"/>'s forward:
        /// <paramref name="distance"/> behind it along the horizon and <paramref name="height"/> above, looking at it,
        /// with the horizon level. Up is against <paramref name="gravity"/> where there is any, else the frame's own up.
        /// Then taken round it on the sphere through that point: <paramref name="orbit"/> radians round the up axis (positive
        /// to its left) and <paramref name="elevation"/> radians over it (negative under), never past <see cref="MaxLift"/>;
        /// it always looks at the centre.
        /// </summary>
        internal static bool Compose(in MatrixD frame, Vector3D center, Vector3 gravity, double distance, double height,
                                     double orbit, double elevation, out MatrixD camera)
        {
            camera = MatrixD.Identity;
            Vector3D up = frame.Up;
            if (gravity.LengthSquared() > 1e-4f)
                up = -Vector3D.Normalize((Vector3D)gravity);

            // Which way the ship faces along the horizon. Pointing nearly straight up or down, its forward says nothing
            // about that: use the way its top points then, as the world menu does for a head looking up or down.
            Vector3D forward = frame.Forward;
            Vector3D along = forward - up * forward.Dot(up);
            if (along.LengthSquared() < 0.04)
            {
                Vector3D top = frame.Up;
                along = top - up * top.Dot(up);
                if (forward.Dot(up) > 0.0)
                    along = -along;
            }
            if (along.LengthSquared() < 1e-6)
                along = Vector3D.Cross(up, frame.Right);
            if (along.LengthSquared() < 1e-6)
                return false;
            along.Normalize();

            double radius = Math.Sqrt(distance * distance + height * height);
            double lift = MathHelper.Clamp(Math.Atan2(height, distance) + elevation, -MaxLift, MaxLift);
            // Round the up axis: a positive turn takes "behind" toward the right, so the left is the negative one.
            Vector3D behind = Vector3D.Transform(-along, MatrixD.CreateFromAxisAngle(up, -orbit));
            Vector3D eye = center + (behind * Math.Cos(lift) + up * Math.Sin(lift)) * radius;
            camera = MatrixD.CreateWorld(eye, Vector3D.Normalize(center - eye), up);
            return true;
        }
    }
}
