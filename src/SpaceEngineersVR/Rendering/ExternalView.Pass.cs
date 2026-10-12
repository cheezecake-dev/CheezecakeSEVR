// ExternalView, render thread: finds the frame's camera and renders the extra pass with it, plus the pass timing log.

using System;
using System.Collections;
using System.Diagnostics;
using VRageMath;
using VRageRender;
using VRageRender.Messages;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class ExternalView
    {
        /// <summary>
        /// Render thread, before the eyes are drawn: finds the camera the renderer holds among those the game sent, and with
        /// it what the picture is for this frame (<see cref="Kind"/>), for <see cref="Render"/> and the screen. Never throws.
        /// </summary>
        /// <param name="env">The renderer's environment matrices, as the head camera.</param>
        public static Kind Find(object env)
        {
            frameKind = Kind.None;
            if (!available)
                return Kind.None;
            try
            {
                if (!TryGetPose(env, out Matrix pose, out float field, out Kind kind))
                    return Kind.None;
                framePose = pose;
                frameField = field;
                frameKind = kind;
            }
            catch (Exception e)
            {
                available = false;
                haveImage = false;
                Log.Error(e, "External view: finding this frame's camera failed; the screen and the forced view are off for the rest of the run");
            }
            return frameKind;
        }

        /// <summary>
        /// Draws this frame's picture (<see cref="Find"/> says which) after the eyes, and puts the head camera back: the dash
        /// screen's view (every other frame with <see cref="VRSettings.ExternalViewHalfRate"/>) or the forced view's. Never throws;
        /// on a failure the screen turns itself off.
        /// </summary>
        /// <param name="env">The renderer's environment matrices, as the head camera.</param>
        /// <param name="head">The head camera, to put back.</param>
        /// <param name="trackingToCamera">From the tracking space into the game camera's, as the eyes are placed (the head
        /// the camera already holds taken out), without the eyes' extrapolation shift, which is undone before this pass.</param>
        public static void Render(object env, in EnvMatrices.Snapshot head, int frame, in Matrix trackingToCamera)
        {
            try
            {
                RenderPass(env, head, frame, trackingToCamera);
            }
            catch (Exception e)
            {
                available = false;
                haveImage = false;
                Log.Error(e, "External view: the chase pass could not start; the screen is off for the rest of the run");
            }
        }

        private static void RenderPass(object env, in EnvMatrices.Snapshot head, int frame, in Matrix trackingToCamera)
        {
            Kind kind = available ? frameKind : Kind.None;
            bool changed = kind != lastKind;
            if (changed)
                Switched(lastKind, kind);
            lastKind = kind;
            if (kind == Kind.None)
            {
                // Nothing to draw this frame. With the screen on, it keeps its place and its last picture for a moment.
                if (!Wanted)
                {
                    haveImage = false;
                    placed = false;
                    passTimed = false;
                }
                return;
            }
            bool halfRate = kind == Kind.Screen && VRSettings.ExternalViewHalfRate;
            if (haveImage && !changed && halfRate && (frame & 1) != 0)
                return;

            Matrix eyeFromHead;
            EnvMatrices.FovTangents fov;
            Matrix tracking = EyeRenderer.Head;
            switch (kind)
            {
                case Kind.Forced:
                    // The game's own camera, without the head it may hold: where the death camera or the respawn screen put it.
                    ForcedView.Place(tracking);
                    eyeFromHead = trackingToCamera;
                    fov = Tangents(frameField);
                    break;
                default:
                    eyeFromHead = framePose;
                    fov = Tangents(frameField);
                    break;
            }
            long started = Stopwatch.GetTimestamp();
            bool first = !passTimed;

            MyRenderDebugOverrides overrides = (MyRenderDebugOverrides)debugOverrides.GetValue(null);
            bool shadows = overrides.Shadows, spotLights = overrides.SpotLights, flares = overrides.Flares,
                foliage = overrides.Foliage, particles = overrides.GPUParticles, ssao = overrides.SSAO;
            IList shadowLights = (IList)tempShadowLights.GetValue(shadowsManager.GetValue(null));
            var savedShadowLights = new ArrayList(shadowLights);
            Vector2I bigResolution = (Vector2I)resolution.GetValue(null);
            Vector2I bigViewport = (Vector2I)viewportResolution.GetValue(null);
            object bigFullRes = fullResViewport.GetValue(null);
            object bigGBuffer = gbufferMain.GetValue(null);
            object[] bigLights = new object[lightFields.Length];
            for (int i = 0; i < bigLights.Length; i++)
                bigLights[i] = lightFields[i].GetValue(null);
            object bigExposure = autoExposure.GetValue(null);

            try
            {
                FitSize(kind);
                EnsureTexture();
                EnsureScreenSet();

                resolution.SetValue(null, new Vector2I(TextureWidth, TextureHeight));
                viewportResolution.SetValue(null, new Vector2I(TextureWidth, TextureHeight));
                fullResViewport.SetValue(null, new MyViewport(TextureWidth, TextureHeight));
                gbufferMain.SetValue(null, smallGBuffer);
                for (int i = 0; i < smallLights.Length; i++)
                    lightFields[i].SetValue(null, smallLights[i]);
                autoExposure.SetValue(null, smallExposure);

                overrides.Shadows = overrides.SpotLights = overrides.Flares = overrides.Foliage = overrides.GPUParticles = overrides.SSAO = false;
                drawing = true;

                EnvMatrices.MakeEye(env, eyeFromHead, fov);
                ScenePass.Current = ScenePass.Chase;
                try
                {
                    EyeRenderer.DrawSceneTo(texture);
                }
                finally
                {
                    ScenePass.Current = ScenePass.None;
                }

                drawnAt = Stopwatch.GetTimestamp();
                haveImage = true;
                drawnKind = kind;
                TimePass(first, drawnAt - started);
            }
            catch (Exception e)
            {
                available = false;
                haveImage = false;
                Log.Error(e, "External view: the chase pass failed; the screen is off for the rest of the run");
            }
            finally
            {
                drawing = false;
                overrides.Shadows = shadows;
                overrides.SpotLights = spotLights;
                overrides.Flares = flares;
                overrides.Foliage = foliage;
                overrides.GPUParticles = particles;
                overrides.SSAO = ssao;
                shadowLights.Clear();
                foreach (object light in savedShadowLights)
                    shadowLights.Add(light);

                autoExposure.SetValue(null, bigExposure);
                for (int i = 0; i < bigLights.Length; i++)
                    lightFields[i].SetValue(null, bigLights[i]);
                gbufferMain.SetValue(null, bigGBuffer);
                fullResViewport.SetValue(null, bigFullRes);
                viewportResolution.SetValue(null, bigViewport);
                resolution.SetValue(null, bigResolution);
                EnvMatrices.Restore(env, head);
            }
        }

        // The cost of the pass on the render thread (the time to record it, not the GPU's), for the log: what turning the
        // screen on costs the frame it first draws, and what it costs once running.
        private const int TimedPasses = 120;
        private static bool passTimed;
        private static int passCount;
        private static double passMillis, firstPassMillis;
        private static string timingWhy = "it was turned on";

        /// <summary>The next pass is timed for the log as a first one: the source was switched (<paramref name="why"/>).</summary>
        public static void Retime(string why)
        {
            timingWhy = why;
            passTimed = false;
        }

        /// <summary>Logs the first pass after the screen was turned on, and then the average of the next <see cref="TimedPasses"/>.</summary>
        private static void TimePass(bool first, long ticks)
        {
            double ms = Seconds(ticks) * 1000.0;
            if (first)
            {
                passTimed = true;
                passCount = 0;
                passMillis = 0.0;
                firstPassMillis = ms;
                Log.Info($"External view: the first pass after {timingWhy} took {ms:0.0} ms on the render thread" + (screenSetJustBuilt ? " (the pass's own screen resources were made then)" : " (its resources were already made)"));
                screenSetJustBuilt = false;
                timingWhy = "it was turned on";
                return;
            }
            if (passCount >= TimedPasses)
                return;
            passMillis += ms;
            if (++passCount == TimedPasses)
                Log.Info($"External view: the next {TimedPasses} passes took {passMillis / TimedPasses:0.0} ms each on the render thread (the first took {firstPassMillis:0.0} ms)");
        }

        /// <summary>A picture's half-angle tangents for a vertical field of view of <paramref name="field"/> radians, the picture's shape across.</summary>
        private static EnvMatrices.FovTangents Tangents(float field)
        {
            float v = (float)Math.Tan(field / 2.0);
            float h = v * TextureWidth / TextureHeight;
            return new EnvMatrices.FovTangents { Left = -h, Right = h, Up = v, Down = -v };
        }

        /// <summary>
        /// The picture changes what it is for: the forced view's screen is placed afresh, and the pass is timed again as a
        /// first one for the log.
        /// </summary>
        private static void Switched(Kind from, Kind to)
        {
            if (from == Kind.Forced)
                ForcedView.Ended();
            switch (to)
            {
                case Kind.Forced:
                    Retime("the forced view came on");
                    break;
                case Kind.Screen:
                    if (from != Kind.None)
                        Retime("the screen came back");
                    break;
            }
        }

        /// <summary>
        /// The camera sent with the game camera this pass is relative to, as the eye-from-head matrix <see cref="EnvMatrices.MakeEye"/>
        /// takes, its vertical field of view in radians, and what the picture was for in that frame.
        /// </summary>
        private static bool TryGetPose(object env, out Matrix eyeFromHead, out float field, out Kind kind)
        {
            eyeFromHead = Matrix.Identity;
            field = MathHelper.ToRadians(FieldOfViewDegrees);
            kind = Kind.None;
            MatrixD view = EnvMatrices.ViewD(env);
            long now = Stopwatch.GetTimestamp();
            lock (gate)
            {
                int newest = -1;
                for (int i = 1; i <= Capacity; i++)
                {
                    int index = (next - i + Capacity) % Capacity;
                    if (stamps[index] == 0 || Seconds(now - stamps[index]) > SnapshotSeconds)
                        continue;
                    if (newest < 0)
                        newest = index;
                    if (views[index] == view)
                    {
                        eyeFromHead = (Matrix)poses[index];
                        field = fields[index];
                        kind = kinds[index];
                        return true;
                    }
                }
                if (newest < 0)
                    return false;
                // The camera the renderer holds is not among the recent ones: the newest chase camera is a frame off at worst.
                eyeFromHead = (Matrix)poses[newest];
                field = fields[newest];
                kind = kinds[newest];
            }
            // Every frame is kept now, wanted or not: a miss is worth a line only when there is a picture to draw.
            if (kind != Kind.None && (++misses & (misses - 1)) == 0)
                Log.Warn($"External view: camera not found among the recent ones (miss {misses}); using the newest");
            return true;
        }
    }
}
