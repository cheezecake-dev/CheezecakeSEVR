using System;
using System.Diagnostics;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using SpaceEngineersVR.Hands;
using SpaceEngineersVR.Tracking;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// The view goes black as the eye reaches a wall, so no eye sees through one. The tracked head moves the eye away from
    /// the character's head bone (on foot as far as the head lock lets it, or any distance with it off; in a seat up to the
    /// seat's reach), and nothing in the world stops it there. The eye is not moved back, which would pull the view against
    /// the player's head; the view fades instead.
    /// </summary>
    /// <remarks>
    /// Each game frame, as the camera goes to the renderer (MyRenderProxy.SetCameraViewMatrix), a ball as wide as the
    /// camera's near plane is swept from the head bone to the eye and on <see cref="Lead"/> past it (<see cref="Reach.Sweep(Vector3D, Vector3D, float, float, MySlimBlock, MySlimBlock, System.Text.StringBuilder, Reach.Failures, out float, out object)"/>:
    /// solid is a grid that is not a projection, or voxels; what the ball starts in at the head bone is passed; in a closed
    /// cockpit leaving its cells counts as a wall, so its canopy does however coarse its collision). Where the ball meets
    /// something, the view darkens over the last <see cref="Lead"/> metres before the eye gets there and is black when the
    /// ball at the eye touches it, so nothing nearer the eye than the near plane is drawn. Seated, the cockpit's own inside
    /// counts too: lean into the dashboard or the canopy and the view darkens the same way. A seat holds the head close to
    /// its own shapes (in the small sloped cockpit the head sits about 6 cm from the headrest's collision), so seated the
    /// fade starts no farther out than the ball's clearance with the eye at rest, along the same line: the view is never
    /// dim at rest, and still black at the glass. The local player's own first-person view is measured (on foot, or in the
    /// seat it sits in, also while a remote control is what the player steers). Third person, and a vehicle's camera that
    /// the game does not build from the head, are measured the same way from the camera: the eyes put the tracked head's
    /// move round that camera (<see cref="EyeRenderer"/>), so the ball is swept from the camera to where the move puts the
    /// eye. The darkness is blended over both eyes after the scene and the vignette, and the HUD panel, a separate layer,
    /// stays.
    /// <para>
    /// It fails closed. A frame whose line could not be measured (physics cannot be asked from here, the sweep failed, an
    /// error) is black, never clear and never the last frame's level. The fade keeps its own count of failures
    /// (<see cref="Reach.Failures"/>), apart from the hands' reach. At the third it stops until the next world loads, and
    /// instead the eye is held at the head bone (<see cref="EyeHeld"/>), as Lock head to body holds it, with no move of
    /// the head to reach through a wall: the view is clear and cannot see through one. In third person that leaves the
    /// camera and the head's turn only.
    /// </para>
    /// <para>
    /// The level is from the game frame, and the eyes follow the head every headset frame. While the game thread stalls
    /// (a game frame older than <see cref="StallSeconds"/>) the head's move since that frame is held to the room the
    /// measure found, no more than <see cref="Lead"/> (<see cref="StallCap"/>), so the head cannot lean through a wall
    /// the stale level knows nothing of.
    /// </para>
    /// </remarks>
    internal static class WallFade
    {
        /// <summary>The view starts to darken this far, metres, before the ball round the eye would touch something.</summary>
        public const double Lead = 0.06;

        private const int TextureSize = 4;

        private static volatile float level;

        /// <summary>Any thread: how dark the view is, 0 (clear) to 1 (black).</summary>
        public static float Level => level;

        /// <summary>
        /// The last measure (game thread): metres from the eye to where the ball stops (past the eye, negative), or
        /// <see cref="double.PositiveInfinity"/> when nothing is within <see cref="Lead"/>; seated, the same with the eye at
        /// rest on the head bone (on foot, infinity); what stopped it; the eye's distance from the head bone (in third
        /// person, from the camera); and the simulation frame.
        /// </summary>
        internal static double Clearance = double.PositiveInfinity, RestClearance = double.PositiveInfinity, EyeFromHead;
        internal static object Blocker;
        internal static ulong MeasuredFrame;

        /// <summary>
        /// Game thread: what the last measure was of ("first person", "third person"; null for a view that is not measured),
        /// and why the view was black with no measure when it was (null when it was measured).
        /// </summary>
        internal static string Seen, Unmeasured;

        private static ulong noteFrame;
        private static bool noted, blackNoted;

        /// <summary>The fade's own count of failed measures, apart from the hands' (<see cref="Reach.Sweep(Vector3D, Vector3D, float, float, MySlimBlock, MySlimBlock, System.Text.StringBuilder, Reach.Failures, out float, out object)"/>).</summary>
        private static readonly Reach.Failures failures = new Reach.Failures(
            "Wall fade: the eye's line could not be measured; the view goes black for that frame",
            "Wall fade: the eye's line failed again; the fade is off until the next world loads, and the eye is held at the head bone until then, as Lock head to body holds it (in third person, the head's turn only, not its move)");

        /// <summary>
        /// Any thread: the fade failed three times and is off until the next world loads (<see cref="Reach.Failures.Reset"/>),
        /// so the eye is held at the head bone with
        /// no move of the head (as Lock head to body holds it; <see cref="CharacterHead"/>, <see cref="CockpitHead"/>), and
        /// round a camera that is not built from the head (third person) the head only turns the view
        /// (<see cref="EyeRenderer"/>). Nothing can then carry the eye through a wall, so the view is left clear.
        /// </summary>
        public static bool EyeHeld => failures.GivenUp;

        public static void Patch(Harmony harmony)
        {
            // The camera's way to the renderer, once a frame on the game thread, outside the simulation: physics can be asked.
            harmony.Patch(AccessTools.Method(typeof(MyRenderProxy), nameof(MyRenderProxy.SetCameraViewMatrix)),
                postfix: new HarmonyMethod(typeof(WallFade), nameof(Measure)));
            // The fade's failure count starts again with each world, so a few stray errors in one do not hold the eye in the next.
            PatchRegistry.Hook(harmony, typeof(WallFade), nameof(WallFade), "the fade's failure count carries over from one world to the next",
                "world load", Reach.WorldLoad(), prefix: nameof(NewWorld));
            Log.Info($"Wall fade hooked: the view darkens over {Lead * 100:0} cm before the eye's near plane reaches a wall");
        }

        /// <summary>A game frame older than this is a stall of the game thread, seconds.</summary>
        private const double StallSeconds = 0.1;

        private static bool stallNoted;

        /// <summary>
        /// Render thread, as the eyes are placed: the shift to put into their placement (pose * this * from-camera, in the tracking
        /// space) that holds back the head's move since the game frame the level was measured for. The eyes follow the head every
        /// headset frame, the level is from the game frame; while that frame is fresh the difference is a centimetre or two, but
        /// when the game thread stalls the head goes on and could lean through a wall the stale level knows nothing of. Once the
        /// game frame is older than <see cref="StallSeconds"/> the move since is held to the room that frame measured in front of
        /// the eye (<see cref="Clearance"/>, no more than <see cref="Lead"/>; none when it measured none), and the turn is not.
        /// Identity while the frame is fresh, or when there is no measure to hold to (the head was not tracked, the camera was not
        /// found). <paramref name="head"/> is the tracked head now.
        /// </summary>
        internal static Matrix StallCap(in Matrix head)
        {
            Vector3? measured = CameraHeadLink.MeasuredHead;
            long sent = CameraHeadLink.SentTime;
            if (!measured.HasValue || sent == 0 || Stopwatch.GetTimestamp() - sent <= StallSeconds * Stopwatch.Frequency)
            {
                stallNoted = false;
                return Matrix.Identity;
            }
            Vector3 late = head.Translation - measured.Value;
            double length = late.Length();
            double room = Clearance > 0.0 ? Math.Min(Lead, Clearance) : 0.0;
            if (length <= room)
                return Matrix.Identity;
            if (!stallNoted)
            {
                stallNoted = true;
                Log.Info($"Wall fade: the game frame is {(Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency:0} ms old; the head's move since is held to {room * 100:0} cm of the {length * 100:0} cm it made");
            }
            return Matrix.CreateTranslation(-late * (float)(1.0 - room / length));
        }

        private static void NewWorld()
        {
            if (failures.Reset())
                Log.Info("Wall fade: a world is loading; the eye's line is measured again");
        }

        private static void Measure(float nearPlane, Vector3D cameraPosition, MatrixD viewMatrix)
        {
            // A camera with no near plane is the session's last, empty one as it closes.
            if (!(nearPlane > 0f))
            {
                level = 0f;
                return;
            }
            // Given up: the eye is held at the head bone (EyeHeld), so there is nothing to measure.
            if (failures.GivenUp)
            {
                Seen = null;
                Unmeasured = "the fade gave up after three failures; the eye is held at the head bone";
                level = 0f;
                return;
            }
            try
            {
                level = (float)Darkness(nearPlane, cameraPosition, viewMatrix);
            }
            catch (Exception e)
            {
                // Fail closed: a view that could not be measured is black.
                level = 1f;
                Clearance = RestClearance = double.PositiveInfinity;
                Blocker = null;
                Unmeasured = "an error measuring it";
                failures.Add(e);
            }
        }

        /// <summary>
        /// How dark the view is, from the camera at <paramref name="camera"/> (the eye, in first person) and the view matrix the
        /// game sent with it: 0 when the eyes are not the player's own, or not the game's own camera put round by the head.
        /// </summary>
        private static double Darkness(float radius, Vector3D camera, in MatrixD view)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || character.IsDead)
                return Clear();
            if (OwnView(character))
            {
                Seen = "first person";
                if (!Reach.TryHead(character, out Vector3D head))
                    return Black("the character has no head bone to measure from");
                return Fade(head, camera, radius, Reach.ClosedSeat(character), character.Parent is MyCockpit);
            }
            if (HeadMovesCamera(character))
            {
                // The eyes are the tracked head's pose round this camera, its move included (EyeRenderer): in the camera's axes.
                Seen = "third person";
                Vector3D eye = camera + Vector3D.TransformNormal((Vector3D)GameHead.TrackingPose.Translation, MatrixD.Invert(view));
                return Fade(camera, eye, radius, null, false);
            }
            return Clear();
        }

        /// <summary>
        /// How dark the view is with the eye at <paramref name="eye"/>: a ball <paramref name="radius"/> wide is swept from
        /// <paramref name="from"/> (the head bone, or in third person the camera) to the eye and <see cref="Lead"/> past it.
        /// <paramref name="seated"/>: the eye is the seat's, so the fade starts no farther out than the eye at rest has room.
        /// Black when the sweep cannot be run.
        /// </summary>
        private static double Fade(Vector3D from, Vector3D eye, float radius, MySlimBlock closedSeat, bool seated)
        {
            Vector3D way = eye - from;
            double away = way.Length();
            if (double.IsNaN(away) || double.IsInfinity(away))
                return Black("the eye's position is not a number");
            if (!(away > 1e-3))
                return Clear();
            way /= away;
            double length = away + Lead;
            if (!Reach.Sweep(from, from + way * length, radius, radius, null, closedSeat, null, failures, out float first, out object blocker))
                return Black("physics could not be asked from here, or the sweep failed");
            MeasuredFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            Unmeasured = null;
            blackNoted = false;
            EyeFromHead = away;
            Blocker = blocker;
            double toStop = blocker != null ? first * length : double.PositiveInfinity;
            Clearance = toStop - away;
            RestClearance = seated ? toStop : double.PositiveInfinity;
            double dark = Shade(Clearance, Math.Min(Lead, RestClearance));
            Note(dark);
            return dark;
        }

        /// <summary>
        /// Full black for a view whose line could not be measured: nothing says the way is clear, so it is not shown. Logged as
        /// it starts, not every frame it lasts.
        /// </summary>
        private static double Black(string why)
        {
            MeasuredFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            Clearance = RestClearance = double.PositiveInfinity;
            Blocker = null;
            Unmeasured = why;
            if (!blackNoted)
            {
                blackNoted = true;
                Log.Info($"Wall fade: view black, the eye's line was not measured: {why}");
            }
            return 1.0;
        }

        /// <summary>0 at <paramref name="band"/> or more, 1 at 0 or less, smoothly between.</summary>
        internal static double Shade(double clearance, double band)
        {
            if (!(band > 0.0))
                return clearance > 0.0 ? 0.0 : 1.0;
            double t = 1.0 - clearance / band;
            t = t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t;
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Clear()
        {
            Clearance = RestClearance = double.PositiveInfinity;
            Blocker = null;
            Seen = Unmeasured = null;
            blackNoted = false;
            return 0.0;
        }

        /// <summary>
        /// The camera is the local character's own eyes, built from the tracked head: on foot in first person, or in first
        /// person from the seat it sits in. Also while a remote control is what the player steers: the controlled entity is
        /// the remote control then, but the camera is still the character's or its seat's.
        /// </summary>
        private static bool OwnView(MyCharacter character)
        {
            switch (MySession.Static.CameraController)
            {
                case MyCharacter body when ReferenceEquals(body, character):
                    return CharacterHead.IsLocalFirstPerson(character);
                case MyCockpit seat when seat.Pilot == character:
                    return seat.IsInFirstPersonView || seat.ForceFirstPersonCamera;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The camera is the local player's own body or vehicle but not built from the head (third person on foot or from a
        /// seat, a vehicle that is the camera itself), so the eyes put the tracked head's move round it. Not a camera
        /// block's, a turret's or a searchlight's (the head's turn and move are taken out of those, <see cref="ForcedView"/>),
        /// nor the spectator's or a cutscene's, which have no walls.
        /// </summary>
        private static bool HeadMovesCamera(MyCharacter character)
        {
            var camera = MySession.Static.CameraController;
            if (ForcedView.FixedCamera(camera) != null || !GameHead.Take())
                return false;
            return camera is MyCharacter body ? ReferenceEquals(body, character) : camera is MyShipController;
        }

        /// <summary>A log line as the view first goes half dark, and again at most every 2 seconds of simulation while it does.</summary>
        private static void Note(double dark)
        {
            bool half = dark >= 0.5;
            if (half && (!noted || MeasuredFrame - noteFrame > 120))
            {
                noteFrame = MeasuredFrame;
                Log.Info($"Wall fade: view {dark * 100:0}% dark, the eye {EyeFromHead * 100:0} cm from the {(Seen == "first person" ? "head bone" : "camera")} and {Clearance * 100:0} cm from {SurfaceStop.Describe(Blocker)}");
            }
            noted = half;
        }

        // ---- The renderer's side ----

        private static readonly GeneratedTexture[] shades = new GeneratedTexture[CameraCut.Levels];
        private static bool texturesFailed;

        /// <summary>
        /// Render thread, once per eye, after the scene, the vignette and the cut fade: darkens <paramref name="eyeTarget"/>
        /// by <see cref="Level"/>, in the cut fade's steps of uniform black (<see cref="CameraCut.Uniform"/>). Full black is
        /// a clear, so a view that should be black is black even where the textures could not be made; then a view half dark
        /// or more is cleared to black.
        /// </summary>
        public static void Draw(object eyeTarget, Vector2I eye)
        {
            float dark = level;
            int step = CameraCut.Level(dark);
            if (step == 0)
                return;
            if (step == CameraCut.Levels || (texturesFailed && dark >= 0.5f))
            {
                EyeRenderer.Clear(eyeTarget);
                return;
            }
            if (texturesFailed)
                return;
            try
            {
                GeneratedTexture shade = shades[step - 1];
                if (shade == null)
                {
                    float alpha = step / (float)CameraCut.Levels;
                    shade = shades[step - 1] = new GeneratedTexture($"VR wall fade {step}", TextureSize, size => CameraCut.Uniform(size, alpha));
                }
                object texture = shade.Get();
                if (texture != null)
                    EyeRenderer.Blend(eyeTarget, texture, new MyViewport(eye.X, eye.Y));
            }
            catch (Exception e)
            {
                texturesFailed = true;
                Log.Error(e, "Wall fade: the darkening could not be drawn; from now on the view goes black, without a fade, once half dark");
            }
        }
    }
}
