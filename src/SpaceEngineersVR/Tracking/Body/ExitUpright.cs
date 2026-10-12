using System;
using System.Collections.Generic;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using SpaceEngineersVR.OpenXR;
using VRage.ModAPI;
using VRageMath;
using VRageRender;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// Getting up from a seat in gravity (a planet's or a gravity generator's) comes out upright, in the same call that takes
    /// the character out of the seat, so the view is not turned upright over the frames after it.
    /// </summary>
    /// <remarks>
    /// What the game does (decompiled): <c>MyCockpit.RemovePilot</c> (every seat, cryo chambers too) places the character
    /// where it stood when it sat down, kept relative to the seat (<c>m_pilotRelativeWorld</c>, so it turns with the vehicle),
    /// or, when that spot is taken, beside the seat (<c>FindFreeNeighbourPosition</c>) with the seat's own forward and up:
    /// either way tilted as the vehicle is. Then, with the jetpack off, <c>MyCharacter.UpdatePhysicalMovement</c> turns the
    /// body toward gravity's up every frame (<c>UpdateStandup</c>: at most 0.04 rad, 2.3 deg, a frame, about the body's middle,
    /// through the physics proxy's <c>SetForwardAndUp</c>), and the first-person view is built on the body
    /// (<see cref="CharacterHead"/>), so a 30 deg tilt rolls and pitches the view for about 13 frames. With the jetpack on the
    /// body is not turned and stays tilted. The player's astronaut aims at gravity alone (RotationToSupport None).
    ///
    /// Here, after the game has placed the character: up is against gravity there (natural, artificial and the physics
    /// world's, as the game sums them; below the game's own 0.1 the character is left as placed), the body is turned about its
    /// middle, which is where the game's own turn leaves it, and it faces where the head looked from the seat, flattened onto
    /// the ground, with the head's yaw from the body taken up by the body (<see cref="GameHead.BodyTurned"/>), so the view
    /// keeps its heading and only its tilt changes, on the frame the exit already moves it. Without a tracked head in a
    /// first-person seat, or looking nearly straight up or down, the body keeps the heading the game's own turn would give it.
    /// When the upright body would be in something (the hull, the ground) and the game's tilted one is not, it is raised by
    /// up to 50 cm, then turned about its feet instead; when nothing fits the game's placement is kept.
    /// RenderDebug ExitUpright=0 leaves it to the game; ExitUprightTrace=1 logs every frame of the 3 s after an exit.
    /// </remarks>
    internal static class ExitUpright
    {
        /// <summary>RenderDebug ExitUpright=0: the game places and turns the character.</summary>
        public static bool Off { get; set; }

        /// <summary>RenderDebug ExitUprightTrace=1: a log line for every camera of the 3 s after getting up.</summary>
        public static bool Trace { get; set; }

        private const float MinGravitySquared = 0.1f; // the game's own: below it the body is not turned toward gravity
        private const double MinTilt = 0.5; // degrees: less is upright already
        private const double FlatLook = 0.25; // the look's share along the ground below which it gives no heading (about 75 deg up or down)
        private static readonly double[] Lifts = { 0.05, 0.15, 0.3, 0.5 };
        private static readonly List<HkBodyCollision> hits = new List<HkBodyCollision>();

        public struct Leaving
        {
            public MyCharacter Pilot;
            public Vector3D? Look;
            public string Seat;
        }

        public static void Patch(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MyCockpit), nameof(MyCockpit.RemovePilot)),
                    prefix: new HarmonyMethod(typeof(ExitUpright), nameof(Before)),
                    postfix: new HarmonyMethod(typeof(ExitUpright), nameof(After)));
                harmony.Patch(AccessTools.Method(typeof(MyRenderProxy), nameof(MyRenderProxy.SetCameraViewMatrix)),
                    postfix: new HarmonyMethod(typeof(ExitUpright), nameof(Watch)));
            }
            catch (Exception e)
            {
                PatchRegistry.Lost(e, "Exit upright: not hooked; getting up from a seat in gravity is left to the game");
                throw;
            }
        }

        /// <summary>The seat still has its pilot: who gets up, and where they look from the seat.</summary>
        private static void Before(MyCockpit __instance, out Leaving __state)
        {
            __state = default;
            try
            {
                MyCharacter pilot = __instance.Pilot;
                // Without the headset in use the game gets the pilot up as usual.
                if (pilot == null || pilot != MySession.Static?.LocalCharacter || !XrSession.Focused)
                    return;
                __state.Pilot = pilot;
                __state.Look = CockpitHead.Look(__instance);
                __state.Seat = __instance.BlockDefinition?.Id.SubtypeName ?? __instance.GetType().Name;
            }
            catch (Exception e)
            {
                __state = default;
                Log.Error(e, "Exit upright: before getting up");
            }
        }

        /// <summary>The game has placed the character out of the seat: turned upright here, before any frame is drawn.</summary>
        private static void After(bool __result, Leaving __state)
        {
            MyCharacter body = __state.Pilot;
            if (!__result || body == null || body.IsDead || body.Closed || body.Parent != null || MyEntities.CloseAllowed)
                return;
            MyPhysicsBody physics = body.Physics;
            if (physics == null || !physics.Enabled)
                return;
            try
            {
                Straighten(body, physics, __state);
            }
            catch (Exception e)
            {
                Log.Error(e, "Exit upright: the character is left as the game placed it");
            }
        }

        private static void Straighten(MyCharacter body, MyPhysicsBody physics, Leaving leaving)
        {
            MatrixD was = body.WorldMatrix;
            Vector3D middle = was.Translation + Vector3D.TransformNormal((Vector3D)physics.Center, was);
            Vector3 natural = MyGravityProviderSystem.CalculateNaturalGravityInPoint(middle);
            Vector3 total = MyGravityProviderSystem.CalculateTotalGravityInPoint(middle);
            Vector3 artificial = total - natural;
            if (physics.HavokWorld != null)
                total += physics.HavokWorld.Gravity;
            if (total.LengthSquared() <= MinGravitySquared)
            {
                Log.Info($"Got up from {leaving.Seat} in no gravity: left as the game places it");
                return;
            }
            string source = Source(natural, artificial);
            Vector3D up = -Vector3D.Normalize((Vector3D)total);
            double tilt = Degrees(was.Up, up);
            string jetpack = body.JetpackRunning ? "jetpack on" : "jetpack off";
            StartWatch(body, tilt, Off);
            if (Off)
            {
                Log.Info($"Got up from {leaving.Seat} in {source} gravity ({total.Length():F1} m/s2, {jetpack}): placed {tilt:F1} deg off upright, " +
                         $"left to the game to turn (ExitUpright=0)");
                return;
            }
            if (tilt < MinTilt)
            {
                Log.Info($"Got up from {leaving.Seat} in {source} gravity ({total.Length():F1} m/s2, {jetpack}): placed {tilt:F1} deg off upright, left as it is");
                return;
            }

            // The shortest turn that brings the body's up to gravity's: where the game's own turn would end. Upside down, over
            // the body's forward.
            Vector3D axis = Vector3D.Cross(was.Up, up);
            if (axis.LengthSquared() < 1e-12)
                axis = was.Forward;
            MatrixD turn = MatrixD.CreateFromAxisAngle(Vector3D.Normalize(axis), MathHelper.ToRadians(tilt));
            Vector3D gameForward = Vector3D.Normalize(Flat(Vector3D.TransformNormal(was.Forward, turn), up));
            Vector3D forward = gameForward;
            bool faced = false;
            if (leaving.Look.HasValue)
            {
                Vector3D look = Flat(leaving.Look.Value, up);
                if (look.Length() > FlatLook)
                {
                    forward = Vector3D.Normalize(look);
                    faced = true;
                }
            }
            double turned = Turn(gameForward, forward, up);

            MatrixD upright = MatrixD.CreateWorld(Vector3D.Zero, forward, up);
            Vector3D aboutMiddle = middle - Vector3D.TransformNormal((Vector3D)physics.Center, upright);
            bool gameFree = Free(body, was, out string gameBlocked);
            string placed = null;
            string blocked = null;
            if (Try(body, ref upright, aboutMiddle, ref blocked))
                placed = "about the body's middle";
            else
            {
                foreach (double lift in Lifts)
                    if (Try(body, ref upright, aboutMiddle + up * lift, ref blocked))
                    {
                        placed = $"about the body's middle and {lift * 100:F0} cm higher";
                        break;
                    }
                if (placed == null && Try(body, ref upright, was.Translation, ref blocked))
                    placed = "about the feet";
                if (placed == null && !gameFree)
                {
                    upright.Translation = aboutMiddle;
                    placed = $"about the body's middle, in {blocked} as the game's own spot is (in {gameBlocked})";
                }
            }
            if (placed == null)
            {
                Log.Info($"Got up from {leaving.Seat} in {source} gravity ({total.Length():F1} m/s2, {jetpack}): placed {tilt:F1} deg off upright; " +
                         $"no room upright (in {blocked}), so left as the game placed it");
                watchUpright = false;
                return;
            }

            body.PositionComp.SetWorldMatrix(ref upright); // the physics follows (MyPhysicsBody.OnWorldPositionChanged: the proxy's transform)
            physics.CharacterProxy?.SetForwardAndUp((Vector3)forward, (Vector3)up); // as the game's own turn sets it
            float headYaw = 0f;
            if (faced && GameHead.Take())
            {
                headYaw = GameHead.Yaw;
                GameHead.BodyTurned(headYaw);
            }
            Log.Info($"Got up upright from {leaving.Seat} in {source} gravity ({total.Length():F1} m/s2, {jetpack}): removed {tilt:F1} deg of tilt, turned {placed}, " +
                     (faced
                         ? $"facing where the head looked ({turned:F0} deg from the game's heading; the head's {MathHelper.ToDegrees(headYaw):F0} deg from the body taken up by the body)"
                         : "keeping the heading the game's own turn gives") +
                     $"; the game's spot was {(gameFree ? "clear" : "in " + gameBlocked)}");
        }

        private static bool Try(MyCharacter body, ref MatrixD upright, Vector3D at, ref string blocked)
        {
            upright.Translation = at;
            if (Free(body, upright, out string by))
                return true;
            blocked = blocked ?? by;
            return false;
        }

        /// <summary>
        /// The character's own shape at this placement is in nothing solid (its own body and phantoms aside), as the game's
        /// <c>MyCharacter.CanPlaceCharacter</c> tests it, without that one's 75 cm sphere round the feet, which any ground
        /// under the feet is in.
        /// </summary>
        private static bool Free(MyCharacter body, MatrixD placement, out string by)
        {
            by = null;
            MyCharacterProxy proxy = body.Physics?.CharacterProxy;
            if (proxy == null)
                return true;
            Vector3D at = placement.Translation + Vector3D.TransformNormal((Vector3D)body.Physics.Center, placement);
            Quaternion rotation = Quaternion.CreateFromRotationMatrix(in placement);
            hits.Clear();
            MyPhysics.GetPenetrationsShape(proxy.GetCollisionShape(), ref at, ref rotation, hits, MyPhysics.CollisionLayers.CharacterCollisionLayer);
            foreach (HkBodyCollision hit in hits)
            {
                IMyEntity entity = hit.GetCollisionEntity();
                if (entity == null || entity == body || entity.Physics == null || entity.Physics.IsPhantom)
                    continue;
                by = entity.DisplayName ?? entity.GetType().Name;
                hits.Clear();
                return false;
            }
            hits.Clear();
            return true;
        }

        private static string Source(Vector3 natural, Vector3 artificial)
        {
            bool planet = natural.LengthSquared() > MinGravitySquared, generator = artificial.LengthSquared() > MinGravitySquared;
            return planet && generator ? "planet and generator" : generator ? "generator" : "planet";
        }

        private static Vector3D Flat(Vector3D v, Vector3D up) => v - Vector3D.Dot(v, up) * up;

        private static double Degrees(Vector3D a, Vector3D b) =>
            MathHelper.ToDegrees(Math.Acos(MathHelper.Clamp(Vector3D.Dot(Vector3D.Normalize(a), Vector3D.Normalize(b)), -1.0, 1.0)));

        // The 3 s after getting up, measured on the cameras sent to the renderer (game thread): how far the body and the view
        // still turn after the exit's own frame. Head motion is in the view's numbers; with a still head it is the body's alone.
        private const int WatchFrames = 180;
        private static MyCharacter watched;
        private static bool watchUpright, watchOff;
        private static double watchTilt;
        private static int watchFrame;
        private static bool haveCamera;
        private static MatrixD lastCamera;
        private static double exitPitch, exitRoll, pitchSum, rollSum, yawSum, fastest, lastTilt, bodySum, firstTilt;

        private static void StartWatch(MyCharacter body, double tilt, bool off)
        {
            watched = body;
            watchTilt = tilt;
            watchOff = off;
            watchUpright = !off;
            watchFrame = 0;
            exitPitch = exitRoll = pitchSum = rollSum = yawSum = fastest = bodySum = 0.0;
            lastTilt = firstTilt = double.NaN;
        }

        private static void Watch(MatrixD viewMatrix)
        {
            MatrixD camera;
            try
            {
                camera = MatrixD.Invert(viewMatrix);
            }
            catch
            {
                // A view matrix that cannot be inverted gives no camera to measure this frame.
                return;
            }
            try
            {
                if (watched != null)
                    Sample(camera);
            }
            catch (Exception e)
            {
                watched = null;
                Log.Error(e, "Exit upright: the watch after getting up");
            }
            lastCamera = camera;
            haveCamera = true;
        }

        private static void Sample(MatrixD camera)
        {
            MyCharacter body = watched;
            if (body.Closed || body.IsDead || MySession.Static?.LocalCharacter != body || !haveCamera)
            {
                watched = null;
                return;
            }
            watchFrame++;
            MatrixD world = body.WorldMatrix;
            Vector3D middle = world.Translation + Vector3D.TransformNormal((Vector3D)(body.Physics?.Center ?? Vector3.Zero), world);
            Vector3 gravity = MyGravityProviderSystem.CalculateTotalGravityInPoint(middle);
            Vector3D up = gravity.LengthSquared() > 1e-6f ? -Vector3D.Normalize((Vector3D)gravity) : world.Up;
            double tilt = Degrees(world.Up, up);

            // This camera's turn from the last one, about its own right (pitch), forward (roll) and up (yaw), degrees.
            double pitch = Turn(lastCamera.Forward, camera.Forward, camera.Right);
            double roll = Turn(lastCamera.Right, camera.Right, camera.Forward);
            double yaw = Turn(lastCamera.Forward, camera.Forward, camera.Up);
            double horizon = MathHelper.ToDegrees(Math.Asin(MathHelper.Clamp(Vector3D.Dot(camera.Right, up), -1.0, 1.0)));
            double level = MathHelper.ToDegrees(Math.Asin(MathHelper.Clamp(Vector3D.Dot(camera.Forward, up), -1.0, 1.0)));
            if (watchFrame == 1)
            {
                exitPitch = pitch;
                exitRoll = roll;
                firstTilt = tilt;
            }
            else
            {
                pitchSum += Math.Abs(pitch);
                rollSum += Math.Abs(roll);
                yawSum += Math.Abs(yaw);
                fastest = Math.Max(fastest, Math.Max(Math.Abs(pitch), Math.Abs(roll)));
                bodySum += Math.Abs(tilt - lastTilt);
            }
            lastTilt = tilt;
            if (Trace)
                Log.Info($"Exit watch {watchFrame}: body {tilt:F2} deg off upright; view turned pitch {pitch:+0.00;-0.00} roll {roll:+0.00;-0.00} yaw {yaw:+0.00;-0.00} deg; " +
                         $"view horizon roll {horizon:F2}, pitch {level:F2} deg");
            if (watchFrame < WatchFrames)
                return;
            Log.Info($"Got up {(watchOff ? "(ExitUpright=0)" : watchUpright ? "upright" : "(left tilted)")}, {WatchFrames} frames on: placed {watchTilt:F1} deg off upright; " +
                     $"the exit's frame turned the view pitch {exitPitch:+0.0;-0.0} roll {exitRoll:+0.0;-0.0} deg, the body {firstTilt:F1} deg off upright after it; " +
                     $"after the exit's frame the view pitched {pitchSum:F1} and rolled {rollSum:F1} deg in all (fastest {fastest:F2} deg a frame; yaw {yawSum:F1}), " +
                     $"the body's tilt changed {bodySum:F1} deg, now {tilt:F1} deg off upright; the view's horizon is {horizon:F1} deg off level, its pitch {level:F1} deg " +
                     "(head motion included)");
            watched = null;
        }

        /// <summary>How far, degrees, a vector turned about an axis from one camera to the next.</summary>
        private static double Turn(Vector3D before, Vector3D after, Vector3D about)
        {
            return MathHelper.ToDegrees(Math.Atan2(Vector3D.Dot(Vector3D.Cross(before, after), about), Vector3D.Dot(before, after)));
        }
    }
}
