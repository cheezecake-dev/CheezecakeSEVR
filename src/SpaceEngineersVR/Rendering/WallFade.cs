using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
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
    /// camera's near plane is swept from the head bone to the eye and on <see cref="Lead"/> past it (<see cref="Reach.Sweep"/>:
    /// solid is a grid that is not a projection, or voxels; what the ball starts in at the head bone is passed; in a closed
    /// cockpit leaving its cells counts as a wall, so its canopy does however coarse its collision). Where the ball meets
    /// something, the view darkens over the last <see cref="Lead"/> metres before the eye gets there and is black when the
    /// ball at the eye touches it, so nothing nearer the eye than the near plane is drawn. Seated, the cockpit's own inside
    /// counts too: lean into the dashboard or the canopy and the view darkens the same way. A seat holds the head close to
    /// its own shapes (in the small sloped cockpit the head sits about 6 cm from the headrest's collision), so seated the
    /// fade starts no farther out than the ball's clearance with the eye at rest, along the same line: the view is never
    /// dim at rest, and still black at the glass. Only the local player's own
    /// first-person view is measured (on foot, or in the seat it sits in); the darkness is blended over both eyes after the
    /// scene and the vignette, and the HUD panel, a separate layer, stays.
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
        /// rest on the head bone (on foot, infinity); what stopped it; the eye's distance from the head bone; and the
        /// simulation frame.
        /// </summary>
        internal static double Clearance = double.PositiveInfinity, RestClearance = double.PositiveInfinity, EyeFromHead;
        internal static object Blocker;
        internal static ulong MeasuredFrame;

        private static int errors;
        private static ulong noteFrame;
        private static bool noted;

        public static void Patch(Harmony harmony)
        {
            // The camera's way to the renderer, once a frame on the game thread, outside the simulation: physics can be asked.
            harmony.Patch(AccessTools.Method(typeof(MyRenderProxy), nameof(MyRenderProxy.SetCameraViewMatrix)),
                postfix: new HarmonyMethod(typeof(WallFade), nameof(Measure)));
            Log.Info($"Wall fade hooked: the view darkens over {Lead * 100:0} cm before the eye's near plane reaches a wall");
        }

        private static void Measure(float nearPlane, Vector3D cameraPosition)
        {
            // A camera with no near plane is the session's last, empty one as it closes.
            if (!(nearPlane > 0f))
            {
                level = 0f;
                return;
            }
            try
            {
                level = (float)Darkness(nearPlane, cameraPosition);
            }
            catch (Exception e)
            {
                level = 0f;
                if (errors++ < 3)
                    Log.Error(e, "Wall fade: the eye's line could not be measured");
            }
        }

        /// <summary>How dark the view is with the eye at <paramref name="eye"/>: 0 when this is not the player's own first-person view.</summary>
        private static double Darkness(float radius, Vector3D eye)
        {
            MyCharacter character = MySession.Static?.LocalCharacter;
            if (character == null || character.IsDead || !OwnView(character) || !Reach.TryHead(character, out Vector3D head))
                return Clear();
            Vector3D way = eye - head;
            double fromHead = way.Length();
            if (!(fromHead > 1e-3))
                return Clear();
            way /= fromHead;
            double length = fromHead + Lead;
            if (!Reach.Sweep(head, head + way * length, radius, radius, null, Reach.ClosedSeat(character), null, out float first, out object blocker))
                return level; // physics could not be asked: the last frame's stands
            MeasuredFrame = MySandboxGame.Static?.SimulationFrameCounter ?? 0;
            EyeFromHead = fromHead;
            Blocker = blocker;
            double toStop = blocker != null ? first * length : double.PositiveInfinity;
            Clearance = toStop - fromHead;
            RestClearance = character.Parent is MyCockpit ? toStop : double.PositiveInfinity;
            double dark = Shade(Clearance, Math.Min(Lead, RestClearance));
            Note(dark);
            return dark;
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
            return 0.0;
        }

        /// <summary>The camera is the local character's own eyes: on foot in first person, or in first person from the seat it sits in.</summary>
        private static bool OwnView(MyCharacter character)
        {
            object controlled = MySession.Static.ControlledEntity;
            if (!ReferenceEquals(MySession.Static.CameraController, controlled))
                return false;
            if (ReferenceEquals(controlled, character))
                return CharacterHead.IsLocalFirstPerson(character);
            return controlled is MyCockpit seat && seat.Pilot == character && (seat.IsInFirstPersonView || seat.ForceFirstPersonCamera);
        }

        /// <summary>A log line as the view first goes half dark, and again at most every 2 seconds of simulation while it does.</summary>
        private static void Note(double dark)
        {
            bool half = dark >= 0.5;
            if (half && (!noted || MeasuredFrame - noteFrame > 120))
            {
                noteFrame = MeasuredFrame;
                Log.Info($"Wall fade: view {dark * 100:0}% dark, the eye {EyeFromHead * 100:0} cm from the head bone and {Clearance * 100:0} cm from {SurfaceStop.Describe(Blocker)}");
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
