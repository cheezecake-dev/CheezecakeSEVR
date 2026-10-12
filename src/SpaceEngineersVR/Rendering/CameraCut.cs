using System;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// A cut is the game putting the view somewhere else at once, with nothing the head did: the eyes go black on that
    /// frame and the world fades back in over a moment, so the player is never snapped to a new place or a new heading
    /// with the world in view. Setting: ComfortFade (on by default).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the game does (decompiled): every change of what the view is taken from goes through
    /// MySession.SetCameraController - getting into or out of a seat or cryo chamber (MyCockpit.cs:2270, :2573,
    /// MyShipController.cs:2218, MyCryoChamber.cs:518), a camera block's view and its next or previous camera
    /// (MyCameraBlock.cs:335, MyGridCameraSystem.cs:203-210), a turret's or searchlight's control (MyLargeTurretBase.cs:2523,
    /// :2669, MySearchlight.cs:1360, :2840), a remote control, a spawn or respawn (MyPlayerCollection.cs:1861, :2633), a
    /// cutscene's start and end (MySessionComponentCutscenes.cs:140, :521) and the spectator keys. Each of those changes
    /// MySession.CameraController, so a cut is seen as the controller changing between two cameras sent to the renderer.
    /// The rest move the view's own controller far in one frame: a jump drive's jump (MyGridJumpDriveSystem: Grid.Teleport),
    /// an admin or script teleport. Those are seen as the camera landing more than <see cref="JumpMetres"/> from where its
    /// carrier's velocity takes it in a game step. The spectator (no entity, so no velocity to tell flying from a jump) is
    /// only cut by a change of controller. Others ask for one themselves (<see cref="Mark"/>: <see cref="Tracking.StandUpright"/>).
    /// </para>
    /// <para>
    /// A cut is only known once it has happened, so there is no fade out before it: the eyes are black from the frame it is
    /// seen (one frame early at most, as the renderer may be a game frame behind), held for <see cref="HoldSeconds"/>, then
    /// fade in over <see cref="FadeSeconds"/>, eased. The time is the render thread's frames', each at most 0.1 s, so a
    /// stall while the new place loads does not use the fade up unseen. The black is one of <see cref="Levels"/> small
    /// generated textures of uniform alpha, blended over each eye after the vignette; the panel (HUD and menus) is a layer of
    /// its own and stays. RenderDebug CameraCut=0 turns it off.
    /// </para>
    /// </remarks>
    internal static partial class CameraCut
    {
        /// <summary>Debug: no fade (<see cref="RenderDebug"/> CameraCut=0).</summary>
        public static bool Off { get; set; }

        // ---- Tuning ----

        /// <summary>The eyes stay fully black this long after a cut, seconds, while the new place settles.</summary>
        public const float HoldSeconds = 0.08f;

        /// <summary>The world then fades back in over this long, seconds.</summary>
        public const float FadeSeconds = 0.35f;

        /// <summary>A camera this far from where its carrier's velocity takes it in one game step has jumped, metres (beyond the step's own travel).</summary>
        public const double JumpMetres = 25.0;

        /// <summary>The steps of black the fade is drawn in.</summary>
        public const int Levels = 32;

        private const int TextureSize = 4;
        private const double Step = MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;

        // ---- What the fade does: pure, so it can be checked without a game. ----

        /// <summary>How black the eyes are, 0 to 1, <paramref name="since"/> seconds after a cut.</summary>
        public static float Black(double since)
        {
            if (since < HoldSeconds)
                return 1f;
            double t = (since - HoldSeconds) / FadeSeconds;
            if (t >= 1.0)
                return 0f;
            float clear = (float)(t * t * (3.0 - 2.0 * t));
            return 1f - clear;
        }

        /// <summary>The texture level, 1 to <see cref="Levels"/>, nearest a blackness; 0 for none.</summary>
        public static int Level(float black) => Math.Max(0, Math.Min(Levels, (int)Math.Round(black * Levels)));

        /// <summary>A texture of premultiplied black at <paramref name="alpha"/> throughout.</summary>
        public static byte[] Uniform(int size, float alpha)
        {
            var data = new byte[size * size * 4];
            byte a = (byte)(Math.Max(0f, Math.Min(1f, alpha)) * 255f + 0.5f);
            for (int i = 3; i < data.Length; i += 4)
                data[i] = a;
            return data;
        }

        // ---- Game thread: telling a cut ----

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(CameraCut));
        private static readonly Common.LineThrottle lines = new Common.LineThrottle(4);
        private static object lastController;
        private static Vector3D lastEye;
        private static int cuts;

        /// <summary>Cuts seen or asked for this run.</summary>
        public static int Count => Volatile.Read(ref cuts);

        /// <summary>Why the last cut was, for the drive's report.</summary>
        public static string LastReason { get; private set; } = "none yet";

        public static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MyRenderProxy), nameof(MyRenderProxy.SetCameraViewMatrix)),
                postfix: new HarmonyMethod(typeof(CameraCut), nameof(Watch)));
        }

        /// <summary>Game thread: the view has just been put somewhere else at once; the eyes go black and fade back in.</summary>
        public static void Mark(string reason)
        {
            LastReason = reason;
            int n = Interlocked.Increment(ref cuts);
            lines.Info($"Camera cut {n}: {reason}{(Off || !VRSettings.ComfortFade ? " (no fade: it is off)" : "")}");
        }

        /// <summary>Game thread, as the camera goes to the renderer: a change of controller, or a jump, is a cut.</summary>
        private static void Watch(MatrixD viewMatrix)
        {
            if (!guard.On)
                return;
            try
            {
                var controller = MySession.Static?.CameraController;
                if (controller == null)
                {
                    lastController = null;
                    return;
                }
                Vector3D eye = MatrixD.Invert(viewMatrix).Translation;
                if (lastController != null && !ReferenceEquals(controller, lastController))
                {
                    Mark($"the view went from {Name(lastController)} to {Name(controller)}");
                }
                else if (lastController != null && controller is MyEntity entity)
                {
                    Vector3D velocity = entity.GetTopMostParent()?.Physics?.GetVelocityAtPoint(eye) ?? Vector3D.Zero;
                    double off = (eye - lastEye - velocity * Step).Length();
                    if (off > JumpMetres + velocity.Length() * Step)
                        Mark($"the view jumped {off:0} m ({Name(controller)})");
                }
                lastController = controller;
                lastEye = eye;
            }
            catch (Exception e)
            {
                lastController = null;
                guard.Failed(e, "Camera cut: telling whether the view was cut failed");
            }
        }

        private static string Name(object controller) =>
            controller is MyEntity entity && !string.IsNullOrEmpty(entity.DisplayName) ? $"{controller.GetType().Name} \"{entity.DisplayName}\"" : controller.GetType().Name;

        // ---- Render thread: the fade ----

        private static readonly GeneratedTexture[] textures = new GeneratedTexture[Levels];
        private static int seen;
        private static double since = double.MaxValue;
        private static long lastTicks;
        private static bool failed;

        /// <summary>How black the eyes were drawn this frame, 0 to 1.</summary>
        public static float Blackness { get; private set; }

        /// <summary>
        /// Render thread, once per eye, after the vignette: blackens <paramref name="eyeTarget"/> by how long ago the last cut
        /// was. The first eye of a frame (<paramref name="pass"/> 0) moves the fade on. Never throws: a fade that cannot be
        /// drawn is logged once and left off.
        /// </summary>
        public static void Draw(object eyeTarget, int pass, Vector2I eye)
        {
            if (failed)
                return;
            try
            {
                if (pass == 0)
                    Advance();
                int level = Level(Blackness);
                if (level == 0)
                    return;
                GeneratedTexture black = textures[level - 1];
                if (black == null)
                {
                    float alpha = level / (float)Levels;
                    black = textures[level - 1] = new GeneratedTexture($"VR cut fade {level}", TextureSize, size => Uniform(size, alpha));
                }
                object texture = black.Get();
                if (texture != null)
                    EyeRenderer.Blend(eyeTarget, texture, new MyViewport(eye.X, eye.Y));
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "Camera cut fade failed; cuts are not faded for the rest of the run");
            }
        }

        private static void Advance()
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = lastTicks == 0 ? 0.0 : Math.Max(0.0, Math.Min((now - lastTicks) / (double)Stopwatch.Frequency, 0.1));
            lastTicks = now;
            int count = Count;
            if (count != seen)
            {
                seen = count;
                since = 0.0;
            }
            else if (since < double.MaxValue)
            {
                since += seconds;
            }
            Blackness = Off || !VRSettings.ComfortFade ? 0f : Black(since);
        }
    }
}
