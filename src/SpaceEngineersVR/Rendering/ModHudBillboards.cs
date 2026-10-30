using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Sandbox.Game.World;
using VRage.Game.Utils;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// A compatibility shim, by name: mods' HUDs drawn as billboards just ahead of the camera, moved onto the VR HUD for the
    /// eyes, without touching the mods. The way into VR for a mod's HUD is <see cref="Api.SevrApi"/>'s HudToWorld (with
    /// HudVisible, HudDepth and HudTangents, docs/MOD_API.md "HUD"); this shim is for the HUD frameworks that do not call it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What it is for, and only this:
    /// <list type="bullet">
    /// <item>Text HUD API (Steam Workshop 758597413), and the mods that draw through it.</item>
    /// <item>Rich HUD Framework (Steam Workshop 1965654081), and the mods that draw through it.</item>
    /// </list>
    /// A framework or mod on that list that calls HudToWorld is left alone by it (a HUD placed by HudToWorld is at least
    /// 0.5 m out, beyond <see cref="Within"/>), so the list shrinks as they do. Nothing is added to it without a framework
    /// that cannot call the API; a mod or plugin author is pointed at HudToWorld (docs/MOD_API.md, "Mods that do not use the
    /// API").
    /// </para>
    /// <para>
    /// Those frameworks place their HUD on the game thread, from MyAPIGateway.Session.Camera.WorldMatrix (the game frame's
    /// camera): Text HUD API 0.1 m ahead of it (ModHUDMessage.WriteLetter), Rich HUD Framework at the camera's near plane
    /// (HudMain.UpdateCache), both laid out to the camera's field of view, and both drawn after the game's processing
    /// (MyBillboard.BlendTypeEnum.PostPP, their default). The eyes draw them as they are: a quad a few centimetres from a
    /// camera that is a game frame old, too near to fuse, behind the head's turn since, shaken by the eyes' move
    /// (<see cref="MotionExtrapolation"/>), and partly behind the near plane.
    /// </para>
    /// <para>
    /// So in each eye's pass, the game frame's billboards (MyRenderProxy.BillboardsRead) that are drawn after processing, given
    /// in the world and quads or points, and wholly within <see cref="Within"/> of the camera and ahead of it, are taken as a
    /// mod's HUD: each corner goes from the screen the mods lay out on to its place in the band the game's HUD has on the
    /// visor (<see cref="ModScreenToHud"/>: <see cref="VRSettings.HudWidthDegrees"/> wide, 16:9), at
    /// <see cref="VRSettings.HudDistance"/> ahead of the head the eyes are drawn from, so it turns and moves with the late head
    /// and the eyes' shift. That quad is then drawn <see cref="SeenFrom"/> from the eye along the eye's rays to it: the eye
    /// sees the same picture, seen at the HUD's distance as the game's HUD is, but nearer than the scene, so walls and
    /// consoles nearer than the HUD do not hide it (the pass reads the scene's depth). Put back after the eyes; hidden in the
    /// external view's pass; hidden while the visor's HUD is (<see cref="HudShown.OnVisor"/>: the helmet open, the suit's battery out).
    /// </para>
    /// <para>
    /// Left as they are: billboards anchored in the world (WeaponCore's target boxes), the game's and this plugin's own
    /// (none in the game frame's list is drawn after processing but the disconnected player's icon, and this plugin's after-
    /// processing quads are added for one eye's pass, <see cref="OnceBillboards"/>), lines, billboards given in an object's
    /// space or with their own projection, and a mod's HUD that uses <see cref="Api.SevrApi"/>'s HudToWorld (at
    /// <see cref="VRSettings.HudDistance"/>, at least 0.5 m out). <see cref="Off"/> leaves them all where the mod put them.
    /// </para>
    /// </remarks>
    internal static class ModHudBillboards
    {
        /// <summary>
        /// Diagnostic off switch (RenderDebug ModHud=0): the shim takes no billboards, so the eyes draw each mod's HUD where the
        /// mod put it, to tell what a mod draws from what the shim does to it. No other path is kept behind it.
        /// </summary>
        public static bool Off { get; set; }

        /// <summary>A mod's HUD lies wholly within this of the camera, m. Below the HUD's least distance (0.5 m), so a HUD placed by HudToWorld is never taken.</summary>
        private const double Within = 0.45;

        /// <summary>Each eye's quad is drawn this far along its rays, m: nearer than the scene, beyond the near plane.</summary>
        private const double SeenFrom = 0.1;

        private const double StatsSeconds = 10.0;

        private sealed class Taken
        {
            public MyBillboard Billboard;
            public bool Point, Triangle;
            public Vector3D From0, From1, From2, From3;
            public Vector4 Colour;
            public Vector3D Hud0, Hud1, Hud2, Hud3;
            public double HudRadius;
            public Vector3D Written;
        }

        private static readonly FeatureGuard guard = new FeatureGuard(nameof(ModHudBillboards));
        private static readonly List<Taken> taken = new List<Taken>();
        private static readonly List<Taken> spare = new List<Taken>();
        private static MatrixD eyesHead;
        private static bool hidden;

        // For the log line: this window's frames.
        private static long windowStart;
        private static int frames, framesWith, most, left, hiddenFrames;
        private static long total;
        private static bool firstLogged;

        /// <summary>
        /// Render thread, before the eyes: finds this game frame's mod HUD billboards and where each goes on the HUD.
        /// <paramref name="head"/> is the game frame's camera, <paramref name="headDrawn"/> the head the eyes are drawn from.
        /// </summary>
        public static void Prepare(in EnvMatrices.Snapshot head, in MatrixD headDrawn)
        {
            Release();
            Stats();
            frames++;
            if (Off || !StereoPatches.VRPicture || !guard.On)
                return;
            try
            {
                List<MyBillboard> billboards = MyRenderProxy.BillboardsRead;
                if (billboards == null || billboards.Count == 0)
                    return;
                MatrixD view = head.ViewD;
                Vector3D camera = head.InvViewD.Translation;
                if (!ModScreenToHud(out double scale))
                    return;
                double distance = VRSettings.HudDistance;
                double within2 = Within * Within;
                eyesHead = headDrawn;

                for (int i = 0; i < billboards.Count; i++)
                {
                    MyBillboard b = billboards[i];
                    // Given in an object's space or with a projection of its own: not placed from the camera.
                    if (b == null || b.ParentID != uint.MaxValue || b.CustomViewProjection != -1)
                        continue;
                    bool point = b.LocalType == MyBillboard.LocalTypeEnum.Point;
                    bool triangle = b is MyTriangleBillboard;
                    if (!point && b.LocalType != MyBillboard.LocalTypeEnum.Custom)
                        continue;
                    // Wholly near the camera: the corners, or a point's middle and its radius.
                    double radius = point ? Math.Abs(b.Position2.X) : 0.0;
                    if (point ? (Vector3D.DistanceSquared(b.Position0, camera) > (Within - radius) * (Within - radius) || radius >= Within)
                        : Vector3D.DistanceSquared(b.Position0, camera) > within2 || Vector3D.DistanceSquared(b.Position1, camera) > within2
                          || Vector3D.DistanceSquared(b.Position2, camera) > within2 || !triangle && Vector3D.DistanceSquared(b.Position3, camera) > within2)
                        continue;
                    if (b.BlendType != MyBillboard.BlendTypeEnum.PostPP)
                    {
                        left++;
                        continue;
                    }
                    Taken t = spare.Count > 0 ? spare[spare.Count - 1] : new Taken();
                    if (spare.Count > 0)
                        spare.RemoveAt(spare.Count - 1);
                    if (!ToHud(b.Position0, ref view, scale, distance, out t.Hud0, out double depth)
                        || !point && (!ToHud(b.Position1, ref view, scale, distance, out t.Hud1, out _)
                            || !ToHud(b.Position2, ref view, scale, distance, out t.Hud2, out _)
                            || !triangle && !ToHud(b.Position3, ref view, scale, distance, out t.Hud3, out _)))
                    {
                        spare.Add(t);
                        continue;
                    }
                    t.Billboard = b;
                    t.Point = point;
                    t.Triangle = triangle;
                    t.From0 = b.Position0;
                    t.From1 = b.Position1;
                    t.From2 = b.Position2;
                    t.From3 = b.Position3;
                    t.Colour = b.Color;
                    t.HudRadius = point ? radius * scale * distance / depth : 0.0;
                    taken.Add(t);
                }
                hidden = !HudShown.OnVisor;
                if (taken.Count > 0)
                {
                    framesWith++;
                    total += taken.Count;
                    most = Math.Max(most, taken.Count);
                    if (hidden)
                        hiddenFrames++;
                    if (!firstLogged)
                    {
                        firstLogged = true;
                        Log.Info(string.Format(CultureInfo.InvariantCulture,
                            "Mod HUD: {0} billboard(s) a mod drew just ahead of the camera are drawn on the VR HUD, the mods' screen ({1:0.000} x {2:0.000} " +
                            "tangents, the camera's field of view and viewport) fitted to the game HUD's 16:9 ({3:0.000} x {4:0.000}, {5:0} deg wide, seen {6:0.00} m out; " +
                            "the eye's projection is {7:0.000} x {8:0.000}); RenderDebug ModHud=0 leaves them where the mod put them",
                            taken.Count, modTangents.X, modTangents.Y, hudTangents.X, hudTangents.Y, VRSettings.HudWidthDegrees, VRSettings.HudDistance,
                            1.0 / head.Projection.M11, 1.0 / head.Projection.M22));
                    }
                }
            }
            catch (Exception e)
            {
                Release();
                guard.Failed(e, "Mod HUD: the mods' HUD billboards could not be moved onto the VR HUD; they are drawn where the mod put them");
            }
        }

        /// <summary>
        /// How much a tangent from the camera on the mods' screen grows or shrinks on the HUD. The mods lay their HUD out on the
        /// screen they read from the camera (MyAPIGateway.Session.Camera): FovWithZoom high, ViewportSize's shape wide (Text HUD
        /// API's ModHUDMessage, Rich HUD Framework's HudMain.UpdateCache), whatever the eye's own projection is. That screen is
        /// fitted, its shape kept, into the band the game's HUD has on the visor (<see cref="HudCamera"/>:
        /// <see cref="VRSettings.HudWidthDegrees"/> wide, the GUI's 16:9 high), so a mod's HUD made for a 16:9 screen lies where
        /// the game's HUD does. False when there is no camera.
        /// </summary>
        private static bool ModScreenToHud(out double scale)
        {
            scale = 0.0;
            MyCamera camera = MySector.MainCamera;
            if (camera == null || camera.Viewport.Width <= 0f || camera.Viewport.Height <= 0f)
                return false;
            double high = Math.Tan(camera.FovWithZoom / 2.0);
            if (!(high > 0.0) || double.IsInfinity(high))
                return false;
            modTangents = new Vector2D(high * camera.Viewport.Width / camera.Viewport.Height, high);
            double wide = Math.Tan(MathHelper.ToRadians(VRSettings.HudWidthDegrees) / 2.0);
            hudTangents = new Vector2D(wide, wide * GuiLayout.Size.Y / GuiLayout.Size.X);
            scale = Math.Min(hudTangents.X / modTangents.X, hudTangents.Y / modTangents.Y);
            return true;
        }

        /// <summary>For the log: the mods' screen and the HUD's band, half of each as tangents from the camera.</summary>
        private static Vector2D modTangents, hudTangents;

        /// <summary>
        /// A point given in the camera's frustum to its place on the HUD in the head's space: the same tangents times
        /// <paramref name="scale"/>, <paramref name="distance"/> ahead. False behind the camera. <paramref name="depth"/>: how far ahead of the camera it was.
        /// </summary>
        private static bool ToHud(in Vector3D world, ref MatrixD view, double scale, double distance, out Vector3D hud, out double depth)
        {
            Vector3D local = Vector3D.Transform(world, view);
            depth = -local.Z;
            if (!(depth > 1e-4))
            {
                hud = Vector3D.Zero;
                return false;
            }
            double along = scale * distance / depth;
            hud = new Vector3D(local.X * along, local.Y * along, -distance);
            return true;
        }

        /// <summary>
        /// Render thread, in an eye's pass before its scene is drawn: each taken billboard on the HUD as this eye sees it.
        /// <paramref name="eyeFromHead"/> places the eye in the game frame's camera <paramref name="head"/>, as the eye is drawn.
        /// </summary>
        public static void ToEye(in Matrix eyeFromHead, in EnvMatrices.Snapshot head)
        {
            if (taken.Count == 0)
                return;
            try
            {
                Vector3D eye = Vector3D.Transform((Vector3D)eyeFromHead.Translation, head.InvViewD);
                double near = SeenFrom / VRSettings.HudDistance;
                for (int i = 0; i < taken.Count; i++)
                {
                    Taken t = taken[i];
                    MyBillboard b = t.Billboard;
                    // Along the eye's rays to the HUD's corners: the same picture to this eye, nearer.
                    b.Position0 = Toward(eye, t.Hud0, near);
                    if (t.Point)
                    {
                        b.Position2 = new Vector3D(t.HudRadius * near, t.From2.Y, t.From2.Z);
                    }
                    else
                    {
                        b.Position1 = Toward(eye, t.Hud1, near);
                        b.Position2 = Toward(eye, t.Hud2, near);
                        if (!t.Triangle)
                            b.Position3 = Toward(eye, t.Hud3, near);
                    }
                    b.Color = hidden ? Vector4.Zero : t.Colour;
                    t.Written = b.Position0;
                }
            }
            catch (Exception e)
            {
                End();
                guard.Failed(e, "Mod HUD: the mods' HUD billboards could not be placed for an eye; they are drawn where the mod put them");
            }
        }

        private static Vector3D Toward(in Vector3D eye, in Vector3D hudInHead, double near)
        {
            Vector3D hud = Vector3D.Transform(hudInHead, eyesHead);
            return eye + (hud - eye) * near;
        }

        /// <summary>Render thread, after the eyes: the taken billboards are not drawn in the passes that follow (the external view's).</summary>
        public static void Hide()
        {
            for (int i = 0; i < taken.Count; i++)
            {
                Taken t = taken[i];
                if (t.Billboard.Position0.Equals(t.Written))
                    t.Billboard.Color = Vector4.Zero;
            }
        }

        /// <summary>
        /// Render thread, after the frame's passes (and on a failure): the billboards go back where the mod put them, unless the
        /// mod has written one since (the game thread reuses its billboards), so the same game frame drawn again starts from the mod's.
        /// </summary>
        public static void End()
        {
            for (int i = 0; i < taken.Count; i++)
            {
                Taken t = taken[i];
                MyBillboard b = t.Billboard;
                if (b.Position0.Equals(t.Written))
                {
                    b.Position0 = t.From0;
                    b.Position1 = t.From1;
                    b.Position2 = t.From2;
                    b.Position3 = t.From3;
                    b.Color = t.Colour;
                }
            }
            Release();
        }

        private static void Release()
        {
            for (int i = 0; i < taken.Count; i++)
            {
                taken[i].Billboard = null;
                spare.Add(taken[i]);
            }
            taken.Clear();
        }

        /// <summary>Once every 10 s while mods drew a HUD: how many frames had one, how many billboards were moved, and how many near ones were left.</summary>
        private static void Stats()
        {
            long now = Stopwatch.GetTimestamp();
            if (windowStart == 0)
            {
                windowStart = now;
                return;
            }
            double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
            if (seconds < StatsSeconds)
                return;
            if (framesWith > 0 || left > 0)
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "Mod HUD {0:0.0} s: {1} of {2} frames had mods' HUD billboards, {3} moved onto the VR HUD ({4:0} a frame, up to {5}){6}; " +
                    "{7} other billboard(s) within {8:0} cm of the camera left where they were (not drawn after processing)",
                    seconds, framesWith, frames, total, framesWith > 0 ? (double)total / framesWith : 0.0, most,
                    hiddenFrames > 0 ? $", hidden with the visor's HUD in {hiddenFrames}" : "", left, Within * 100.0));
            windowStart = now;
            frames = framesWith = most = left = hiddenFrames = 0;
            total = 0;
        }
    }
}
