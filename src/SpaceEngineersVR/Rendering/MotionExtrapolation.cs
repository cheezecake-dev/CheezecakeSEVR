using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Platform;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Library.Utils;
using VRage.Render.Scene;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    /// <summary>
    /// Moves the view, and what carries it, by how far the carrier goes between the game frame and the headset frame
    /// that shows it, so the world goes by at the headset's rate rather than in the game's 60 Hz steps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game updates 60 times a second and the headset shows 72 frames. The renderer draws the last game frame it
    /// has (MyUpdateData.GetRenderFrame keeps the final frame in the queue "so we can interpolate and draw frame again"),
    /// and nothing in MyRender11 interpolates: the camera takes the message's view matrix as it is
    /// (SetupCameraMatricesInternal), entities take the matrices sent (MyPostponedUpdateManager.UpdateActor ->
    /// MyActor.SetTransforms), and LastMomentUpdateIndex is not read. So one headset frame in six draws a game frame
    /// again, and between those the frame shown is 0 to 14 ms old in a repeating sawtooth: moving at v, the world is
    /// up to v x 7 ms either side of where it should be, 12 times a second (at 20 m/s, 14 cm).
    /// </para>
    /// <para>
    /// The camera alone cannot be moved: the vehicle or body carrying it is drawn from the same 60 Hz game frame, so
    /// moving only the camera would move that judder onto the cockpit, the body and the hands. So the camera and the
    /// carrier's render roots (every grid mechanically joined to a controlled seat's grid, by their cells' cull objects;
    /// the pilot or the character on foot by its cull object; the held tool) move together by the carrier's velocity at
    /// the eye times how much older this frame's game frame is than the running mean (the mean is left out, so on
    /// average everything is where the simulation put it). The roots are put back after the eyes are drawn.
    /// </para>
    /// <para>
    /// Not moved: billboards (the lasers, hand rays and wrist screen this plugin draws, thruster and jetpack flames),
    /// lights that are not parented to a grid cell, and loose objects riding the vehicle: they keep the game frame's place
    /// and so show the v x 7 ms judder against the carrier instead of against the world. Rotation is not extrapolated: a
    /// turning vehicle still turns in 60 Hz steps. RenderDebug MotionExtrapolation=0 turns it off.
    /// </para>
    /// </remarks>
    internal static class MotionExtrapolation
    {
        /// <summary>RenderDebug MotionExtrapolation=0 turns it off.</summary>
        public static bool On = true;

        /// <summary>No shift beyond one game step either way, ms.</summary>
        private const double MaxShiftMs = 1000.0 / 60.0;

        /// <summary>A game frame older than this is not a moving game (a pause, a load): no shift, ms.</summary>
        private const double MaxAgeMs = 100.0;

        /// <summary>Weight of each frame in the running mean of the game frame's age (about 0.7 s at 72 Hz; the sawtooth repeats every 6 frames).</summary>
        private const double MeanWeight = 0.02;

        /// <summary>Faster than this is not motion to smooth (a teleport, a respawn), m/s.</summary>
        private const double MaxSpeed = 1000.0;

        private const double StatsSeconds = 10.0;

        /// <summary>What the game thread knew when it sent a camera: when its update began, the carrier's velocity at the eye, and the carrier's render roots.</summary>
        internal sealed class Sample
        {
            public bool Valid;
            public long StateTicks;
            public Vector3D Velocity;
            public uint[] Roots = new uint[16];
            public int Count;

            public void Add(uint id)
            {
                if (id == uint.MaxValue)
                    return;
                if (Count == Roots.Length)
                    Array.Resize(ref Roots, Count * 2);
                Roots[Count++] = id;
            }

            public void CopyFrom(Sample s)
            {
                Valid = s.Valid;
                StateTicks = s.StateTicks;
                Velocity = s.Velocity;
                if (Roots.Length < s.Count)
                    Roots = new uint[s.Roots.Length];
                Array.Copy(s.Roots, Roots, s.Count);
                Count = s.Count;
            }
        }

        // ---- game thread ----

        private static readonly AccessTools.FieldRef<Game, MyGameTimer> gameTimer = AccessTools.FieldRefAccess<Game, MyGameTimer>("m_gameTimer");
        private static readonly AccessTools.FieldRef<MyRenderComponentCharacter, uint> cullRenderId =
            AccessTools.FieldRefAccess<MyRenderComponentCharacter, uint>("m_cullRenderId");
        private static readonly List<MyCubeGrid> grids = new List<MyCubeGrid>();
        private static bool captureFailed;

        /// <summary>
        /// Game thread, as the camera goes to the renderer (<see cref="Tracking.CameraHeadLink"/>): fills
        /// <paramref name="s"/> for the camera <paramref name="viewMatrix"/>. Invalid when the camera is not the
        /// controlled seat's or character's own (spectator, a camera block, a turret).
        /// </summary>
        public static void Capture(Sample s, in MatrixD viewMatrix)
        {
            s.Valid = false;
            s.Count = 0;
            if (!On || captureFailed)
                return;
            try
            {
                MySession session = MySession.Static;
                var controlled = session?.ControlledEntity;
                if (controlled == null || !ReferenceEquals(session.CameraController, controlled))
                    return;
                Vector3D eye = MatrixD.Invert(viewMatrix).Translation;
                switch (controlled.Entity)
                {
                    case MyShipController seat:
                        MyCubeGrid grid = seat.CubeGrid;
                        if (grid?.Physics == null)
                            return;
                        s.Velocity = grid.Physics.GetVelocityAtPoint(eye);
                        grids.Clear();
                        MyCubeGridGroups.Static?.Mechanical.GetGroupNodes(grid, grids);
                        if (grids.Count == 0)
                            grids.Add(grid);
                        foreach (MyCubeGrid joined in grids)
                        {
                            uint[] ids = joined.Render?.RenderObjectIDs;
                            if (ids != null)
                                foreach (uint id in ids)
                                    s.Add(id);
                        }
                        grids.Clear();
                        if (seat.Pilot != null)
                            AddCharacter(s, seat.Pilot);
                        break;
                    case MyCharacter character:
                        if (character.Physics == null)
                            return;
                        s.Velocity = character.Physics.LinearVelocity;
                        AddCharacter(s, character);
                        break;
                    default:
                        return;
                }
                if (s.Velocity.LengthSquared() > MaxSpeed * MaxSpeed)
                {
                    s.Count = 0;
                    return;
                }

                // The update's start in Stopwatch ticks: MyGameTimer counts Stopwatch ticks, and TotalTime is its reading as the update began.
                Game game = MySandboxGame.Static;
                MyGameTimer timer = game != null ? gameTimer(game) : null;
                if (timer == null)
                {
                    s.Count = 0;
                    return;
                }
                long now = Stopwatch.GetTimestamp();
                s.StateTicks = now - (timer.ElapsedTicks - game.TotalTime.Ticks);
                s.Valid = true;
            }
            catch (Exception e)
            {
                captureFailed = true;
                s.Valid = false;
                s.Count = 0;
                Log.Error(e, "Motion extrapolation: reading the carrier failed; the view stays on the game's frames for the rest of the run");
            }
        }

        private static void AddCharacter(Sample s, MyCharacter character)
        {
            if (((MyEntity)character).Render is MyRenderComponentCharacter render)
                s.Add(cullRenderId(render));
            if (character.CurrentWeapon is MyEntity tool && tool.Render?.RenderObjectIDs is uint[] ids)
                foreach (uint id in ids)
                    s.Add(id);
        }

        // ---- render thread ----

        private static readonly Sample current = new Sample();

        /// <summary>Render thread, as the renderer takes a camera on: the sample sent with it (null when none was found).</summary>
        public static void Take(Sample s)
        {
            if (s == null)
                current.Valid = false;
            else
                current.CopyFrom(s);
        }

        private struct Moved
        {
            public MyActor Actor;
            public MatrixD From, To;
        }

        private static readonly List<Moved> moved = new List<Moved>();
        private static double meanAge;
        private static bool meanSet;
        private static long lastState;

        // For the log line: this window's frames.
        private static long windowStart;
        private static int frames, carried, repeats, skips, shifted, roots;
        private static double shiftSum, shiftMax, ageMin = double.MaxValue, ageMax, speedSum;

        /// <summary>
        /// Render thread, after the headset frame began (so now is a fixed time before it is shown) and before the eyes
        /// are placed: moves the carrier's roots and returns the world shift the eyes take too.
        /// </summary>
        public static Vector3D Begin()
        {
            long now = Stopwatch.GetTimestamp();
            Stats(now);
            frames++;
            Sample s = current;
            if (!On || !s.Valid)
            {
                meanSet = false;
                lastState = 0;
                return Vector3D.Zero;
            }
            carried++;
            speedSum += s.Velocity.Length();
            double step = Stopwatch.Frequency / 60.0;
            if (lastState != 0)
            {
                if (s.StateTicks == lastState)
                    repeats++;
                else if (s.StateTicks - lastState > 1.5 * step)
                    skips++;
            }
            lastState = s.StateTicks;

            double age = (now - s.StateTicks) * 1000.0 / Stopwatch.Frequency;
            if (age < 0 || age > MaxAgeMs)
            {
                meanSet = false;
                return Vector3D.Zero;
            }
            ageMin = Math.Min(ageMin, age);
            ageMax = Math.Max(ageMax, age);
            if (!meanSet)
            {
                meanAge = age;
                meanSet = true;
            }
            else
            {
                meanAge += (age - meanAge) * MeanWeight;
            }
            double dt = Math.Max(-MaxShiftMs, Math.Min(MaxShiftMs, age - meanAge)) / 1000.0;
            Vector3D shift = s.Velocity * dt;
            double length = shift.Length();
            if (length < 1e-4)
                return Vector3D.Zero;

            moved.Clear();
            for (int i = 0; i < s.Count; i++)
            {
                MyActor actor = MyIDTracker<MyActor>.FindByID(s.Roots[i]);
                if (actor == null || actor.Parent != null)
                    continue;
                MatrixD from = actor.WorldMatrix;
                MatrixD to = from;
                to.Translation += shift;
                actor.SetMatrix(ref to);
                moved.Add(new Moved { Actor = actor, From = from, To = to });
            }
            shifted++;
            shiftSum += length;
            shiftMax = Math.Max(shiftMax, length);
            roots = Math.Max(roots, moved.Count);
            return shift;
        }

        /// <summary>Render thread, after the eyes: the carrier's roots go back where the game put them (unless something else moved one since).</summary>
        public static void End()
        {
            for (int i = 0; i < moved.Count; i++)
            {
                Moved m = moved[i];
                if (m.Actor.WorldMatrix.Equals(m.To))
                    m.Actor.SetMatrix(ref m.From);
            }
            moved.Clear();
        }

        /// <summary>
        /// Once every 10 s while a carrier was known: how many headset frames drew a game frame again (repeats; about
        /// 12 a second at 72 Hz over 60) or skipped one, the game frame's age when shown, and the shift.
        /// </summary>
        private static void Stats(long now)
        {
            if (windowStart == 0)
            {
                windowStart = now;
                return;
            }
            double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
            if (seconds < StatsSeconds)
                return;
            if (carried > 0)
                Log.Info(string.Format(CultureInfo.InvariantCulture,
                    "Motion {0:0.0} s: {1} frames, {2} with a carrier, {3} drew a game frame again ({4:0.0}/s), {5} skipped one; game frame {6:0.0}-{7:0.0} ms old when drawn, mean {8:0.0}; " +
                    "speed {9:0.0} m/s; {10}",
                    seconds, frames, carried, repeats, repeats / seconds, skips, ageMin == double.MaxValue ? 0 : ageMin, ageMax, meanAge, speedSum / carried,
                    !On ? "extrapolation off" : shifted == 0 ? "no shift"
                        : string.Format(CultureInfo.InvariantCulture, "shifted {0} frames by {1:0.0} cm mean, {2:0.0} max, up to {3} roots", shifted, shiftSum / shifted * 100.0, shiftMax * 100.0, roots)));
            windowStart = now;
            frames = carried = repeats = skips = shifted = roots = 0;
            shiftSum = shiftMax = speedSum = ageMax = 0;
            ageMin = double.MaxValue;
        }
    }
}
