using System;
using HarmonyLib;
using Sandbox;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;
using Sandbox.Game.World;
using VRageMath;

namespace SpaceEngineersVR.Tracking
{
    /// <summary>
    /// The third-person camera in a ship, made for a head that is not on a mouse. The game's chase camera takes the
    /// seat's own orientation and looks at the pilot, so every roll and pitch of the ship turns the whole world around
    /// the player. Here the camera sits where the game puts it (behind and above the ship, at the zoom the player
    /// chose, with the game's collision handling) but in a level frame - the "up" of gravity, or of the ship as it was
    /// when third person began when there is no gravity, and only the heading follows the ship - and looks along that
    /// frame's horizon: no pitch, no roll. The tracked head is added on top by the eye rendering, as for any camera
    /// that was not built from the head (<see cref="CameraHeadLink.HeadInCamera"/> is null then, and each eye is the
    /// full head pose around the camera), so a head turn leaves the horizon level; looking down at the ship is the
    /// head's.
    /// </summary>
    /// <remarks>
    /// MyThirdPersonSpectator.Update reads the seat's head matrix, lerps its own m_targetOrientation towards that
    /// orientation, and builds the camera position (m_desiredPosition, then the collision step into m_positionSafe)
    /// from it. So the frame is replaced where the game reads it: a postfix on MyCockpit.GetHeadMatrix, active only
    /// inside that one Update call, returns the level frame as the head matrix's orientation (the translation, the
    /// pilot's head, is the game's) and puts it into m_targetOrientation as well, so the game's lerp has nothing to
    /// do and the smoothing is only ours. Nothing else that asks the seat for its head matrix (aiming, the use ray)
    /// sees the level frame. The view matrix (MyThirdPersonSpectator.GetViewMatrix: CreateLookAt from m_positionSafe
    /// at the pilot) then has its orientation replaced by the level frame's, from the same position.
    /// Only a seat the local player sits in, in third person, while the setting and the debug switch are on.
    /// First person, a character in third person, the spectator camera and remote/camera blocks are the game's.
    /// </remarks>
    internal static class ChaseCamera
    {
        /// <summary>Debug: the game's own camera (<see cref="Rendering.RenderDebug"/> ChaseCamera=0).</summary>
        public static bool Off { get; set; }

        /// <summary>Gravity weaker than this (m/s^2) is not a field to be level with: the frame holds the last up instead.</summary>
        private const float MinGravity = 0.1f;

        /// <summary>The game updates the camera every simulation frame in third person; a longer gap is a new entry into third person.</summary>
        private const int NewEntryAfterFrames = 120;

        private const double SimulationRate = 60.0;

        private static AccessTools.FieldRef<MyCockpit, MatrixD> cameraDummy;
        private static AccessTools.FieldRef<MyThirdPersonSpectator, MatrixD> targetOrientation;

        private static readonly LevelFrame frame = new LevelFrame();
        private static MyCockpit owner;
        private static ulong lastFrame;
        private static MatrixD level = MatrixD.Identity;
        private static bool failed;

        // The camera update runs on the game thread, and so do the head matrix calls inside it.
        [ThreadStatic] private static bool inUpdate;
        [ThreadStatic] private static MyCockpit updateSeat;

        public static void Patch(Harmony harmony)
        {
            try
            {
                cameraDummy = AccessTools.FieldRefAccess<MyCockpit, MatrixD>("m_cameraDummy");
                targetOrientation = AccessTools.FieldRefAccess<MyThirdPersonSpectator, MatrixD>("m_targetOrientation");
                harmony.Patch(AccessTools.DeclaredMethod(typeof(MyThirdPersonSpectator), nameof(MyThirdPersonSpectator.Update), Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(ChaseCamera), nameof(BeginUpdate)),
                    finalizer: new HarmonyMethod(typeof(ChaseCamera), nameof(EndUpdate)));
                harmony.Patch(AccessTools.Method(typeof(MyCockpit), nameof(MyCockpit.GetHeadMatrix)),
                    postfix: new HarmonyMethod(typeof(ChaseCamera), nameof(Level)));
                harmony.Patch(AccessTools.Method(typeof(MyThirdPersonSpectator), nameof(MyThirdPersonSpectator.GetViewMatrix)),
                    postfix: new HarmonyMethod(typeof(ChaseCamera), nameof(View)));
            }
            catch (Exception e)
            {
                // The game's own chase camera stays; nothing else depends on this.
                Log.Error(e, "Chase camera could not be hooked; the game's camera stays");
            }
        }

        /// <summary>The seat the local player is in, in third person, as MyThirdPersonSpectator.Update picks its subject; null otherwise.</summary>
        private static MyCockpit ThirdPersonSeat()
        {
            MySession session = MySession.Static;
            if (session == null)
                return null;
            IMyControllableEntity controlled = session.ControlledEntity;
            if (controlled == null || controlled.Entity == null)
                controlled = session.CameraController as IMyControllableEntity;
            if (!(controlled is MyCockpit seat) || seat.PositionComp == null || seat.IsInFirstPersonView)
                return null;
            return seat;
        }

        private static void BeginUpdate()
        {
            inUpdate = false;
            updateSeat = null;
            if (failed)
                return;
            MyCockpit seat = ThirdPersonSeat();
            if (seat == null || Off || !VRSettings.LevelChaseCamera)
            {
                Leave(seat);
                return;
            }
            inUpdate = true;
            updateSeat = seat;
        }

        private static void EndUpdate()
        {
            inUpdate = false;
            updateSeat = null;
        }

        /// <summary>Not levelling this update. <paramref name="seat"/>: the seat in third person, if the camera is still on one.</summary>
        private static void Leave(MyCockpit seat)
        {
            if (!frame.Started)
                return;
            frame.Reset();
            MyThirdPersonSpectator spectator = MyThirdPersonSpectator.Static;
            if (seat != null && seat == owner && spectator != null)
            {
                // Switched off while in the seat (the menu, the debug switch): the game's frame at once. Its own lerp from
                // the level frame could pass through a matrix with no up in it, with the ship upside down to the level one.
                try
                {
                    targetOrientation(spectator) = seat.GetHeadMatrix(true).GetOrientation();
                }
                catch (Exception e)
                {
                    Log.Error(e, "Chase camera: could not hand the camera back");
                }
            }
            owner = null;
        }

        /// <summary>Inside the camera update: the seat's head matrix with the level frame for its orientation.</summary>
        private static void Level(MyCockpit __instance, ref MatrixD __result)
        {
            if (!inUpdate || __instance != updateSeat)
                return;
            try
            {
                Vector3D head = __result.Translation;
                MatrixD orientation = Frame(__instance, head);
                MyThirdPersonSpectator spectator = MyThirdPersonSpectator.Static;
                if (spectator != null)
                    targetOrientation(spectator) = orientation;
                orientation.Translation = head;
                __result = orientation;
            }
            catch (Exception e)
            {
                // Once: the game's camera from here on, rather than an error every frame.
                failed = true;
                Log.Error(e, "Chase camera failed; the game's camera from here on");
            }
        }

        /// <summary>
        /// The view matrix of a seat's third person: from where the game put the camera, looking along the level
        /// frame instead of at the pilot. A character's third person, and every other caller, is left alone.
        /// </summary>
        private static void View(ref MatrixD __result)
        {
            if (failed || Off || !frame.Started || !VRSettings.LevelChaseCamera || MySession.Static?.CameraController == null)
                return;
            MyCockpit seat = ThirdPersonSeat();
            if (seat == null || seat != owner)
                return;
            try
            {
                __result = LevelFrame.View(__result, level);
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error(e, "Chase camera view failed; the game's camera from here on");
            }
        }

        /// <summary>The level frame for this simulation frame, stepped once however often the camera update is called within it.</summary>
        private static MatrixD Frame(MyCockpit seat, Vector3D head)
        {
            ulong now = MySandboxGame.Static.SimulationFrameCounter;
            if (frame.Started && seat == owner && now == lastFrame)
                return level;

            bool entered = !frame.Started || seat != owner || now < lastFrame || now - lastFrame > NewEntryAfterFrames;
            if (entered)
                frame.Reset();
            double dt = entered ? 0.0 : (now - lastFrame) / SimulationRate;

            MatrixD pose = cameraDummy(seat) * seat.PositionComp.WorldMatrixRef;
            Vector3D forward = Vector3D.Normalize(pose.Forward);
            Vector3D shipUp = Vector3D.Normalize(pose.Up);
            Vector3 gravity = MyGravityProviderSystem.CalculateNaturalGravityInPoint(head);
            float strength = gravity.Length();
            Vector3D? gravityUp = strength >= MinGravity ? -(Vector3D)gravity / strength : (Vector3D?)null;

            level = frame.Step(forward, shipUp, gravityUp, dt);
            if (entered)
                Log.Info($"Chase camera: level, up from {(gravityUp.HasValue ? "gravity" : "the ship as it is now")}");
            owner = seat;
            lastFrame = now;
            return level;
        }
    }

    /// <summary>
    /// A level frame that follows a ship: its up is the reference "up" (gravity's, or the one held), its forward the
    /// ship's heading about that up. Both are smoothed, and the heading holds when the ship points along the up (the
    /// heading is not defined there). No game types, so it can be checked on its own.
    /// </summary>
    internal sealed class LevelFrame
    {
        /// <summary>Time constant of the follow, seconds: the frame closes 63% of a turn in this long.</summary>
        public const double Smoothing = 0.5;

        /// <summary>
        /// How horizontal the ship's forward is (1 level, 0 straight up or down) for the heading to follow it:
        /// not at all below the first, fully above the second, easing between.
        /// </summary>
        public const double HoldBelow = 0.15, FollowAbove = 0.35;

        private Vector3D referenceUp, up, heading;

        public bool Started { get; private set; }

        public Vector3D Up => up;

        public Vector3D Heading => heading;

        public void Reset() => Started = false;

        /// <param name="forward">The ship's forward, unit.</param>
        /// <param name="shipUp">The ship's up, unit.</param>
        /// <param name="gravityUp">Away from gravity, unit; null when there is none. Without it the last reference up is kept.</param>
        /// <param name="dt">Seconds since the last step.</param>
        /// <returns>A rotation: forward is the heading, up is the up. Pitch and roll are none.</returns>
        public MatrixD Step(Vector3D forward, Vector3D shipUp, Vector3D? gravityUp, double dt)
        {
            if (!IsDirection(forward) || !IsDirection(shipUp))
                return Started ? MatrixD.CreateWorld(Vector3D.Zero, heading, up) : MatrixD.Identity;

            if (gravityUp.HasValue && IsDirection(gravityUp.Value))
                referenceUp = Vector3D.Normalize(gravityUp.Value);
            if (!Started)
            {
                if (!(gravityUp.HasValue && IsDirection(gravityUp.Value)))
                    referenceUp = shipUp;
                up = referenceUp;
                heading = FirstHeading(forward, shipUp, up);
                Started = true;
            }
            else
            {
                double k = 1.0 - Math.Exp(-Math.Max(dt, 0.0) / Smoothing);
                up = Toward(up, referenceUp, k);
                heading = Flatten(heading, up);

                Vector3D flat = Reject(forward, up);
                double length = flat.Length();
                if (length > HoldBelow)
                {
                    double follow = Math.Min((length - HoldBelow) / (FollowAbove - HoldBelow), 1.0);
                    heading = Turn(heading, up, SignedAngle(heading, flat / length, up) * k * follow);
                    heading = Flatten(heading, up);
                }
            }
            return MatrixD.CreateWorld(Vector3D.Zero, heading, up);
        }

        /// <summary>
        /// The game's view matrix (a look at the pilot) with its orientation replaced by the level frame's: the eye is
        /// where the game put it, and looks along the heading with the up level.
        /// </summary>
        public static MatrixD View(MatrixD gameView, MatrixD level)
        {
            MatrixD eye = level;
            eye.Translation = MatrixD.Invert(gameView).Translation;
            return MatrixD.Invert(eye);
        }

        /// <summary>The heading to start from: the ship's forward flattened, or, pointing along the up, where its top says.</summary>
        private static Vector3D FirstHeading(Vector3D forward, Vector3D shipUp, Vector3D up)
        {
            Vector3D flat = Reject(forward, up);
            double length = flat.Length();
            if (length > 1e-3)
                return flat / length;
            // Nose up, the ship's top points backwards; nose down, forwards.
            Vector3D top = Reject(shipUp, up);
            double topLength = top.Length();
            if (topLength > 1e-3)
                return (Vector3D.Dot(forward, up) > 0.0 ? -top : top) / topLength;
            return Perpendicular(up);
        }

        /// <summary>The heading kept to the horizontal plane of the up, unit.</summary>
        private static Vector3D Flatten(Vector3D heading, Vector3D up)
        {
            Vector3D flat = Reject(heading, up);
            double length = flat.Length();
            return length > 1e-6 ? flat / length : Perpendicular(up);
        }

        private static bool IsDirection(Vector3D v)
        {
            double length = v.Length();
            return !double.IsNaN(length) && !double.IsInfinity(length) && length > 1e-6;
        }

        private static Vector3D Reject(Vector3D v, Vector3D unitAxis) => v - unitAxis * Vector3D.Dot(v, unitAxis);

        private static double SignedAngle(Vector3D from, Vector3D to, Vector3D unitAxis) =>
            Math.Atan2(Vector3D.Dot(Vector3D.Cross(from, to), unitAxis), Vector3D.Dot(from, to));

        /// <summary>Some unit vector at right angles to the unit vector given.</summary>
        private static Vector3D Perpendicular(Vector3D unit)
        {
            Vector3D axis = Math.Abs(unit.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitY;
            return Vector3D.Normalize(Reject(axis, unit));
        }

        /// <summary>Rodrigues: the vector turned by the angle about the unit axis (right-handed).</summary>
        private static Vector3D Turn(Vector3D v, Vector3D unitAxis, double angle)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            return v * cos + Vector3D.Cross(unitAxis, v) * sin + unitAxis * (Vector3D.Dot(unitAxis, v) * (1.0 - cos));
        }

        /// <summary>The unit vector turned the given share of the way to the target along the shortest arc, unit.</summary>
        private static Vector3D Toward(Vector3D v, Vector3D target, double share)
        {
            double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3D.Dot(v, target))));
            if (angle < 1e-9)
                return target;
            Vector3D axis = Vector3D.Cross(v, target);
            double length = axis.Length();
            axis = length > 1e-9 ? axis / length : Perpendicular(v); // straight back: any way round
            return Vector3D.Normalize(Turn(v, axis, angle * share));
        }
    }
}
