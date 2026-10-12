// ExternalView, game thread: keeps the chase camera out of walls, the way the game's third-person camera does.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Debris;
using Sandbox.Game.Weapons;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Rendering
{
    internal static partial class ExternalView
    {
        /// <summary>How fast the camera goes back out once nothing holds it in, as a share of its full distance per second.</summary>
        private const double EaseOutPerSecond = 0.5;

        private static readonly List<MyPhysics.HitInfo> chaseHits = new List<MyPhysics.HitInfo>();
        private static HkShape chaseBall;
        private static float chaseBallRadius;
        private static int chaseErrors;
        private static bool chaseUnsweptNoted;

        /// <summary>How far out the camera is, as a share of the way from what it follows to where it would stand.</summary>
        private static double chaseOut = 1.0;
        private static long chaseOutAt;

        /// <summary>
        /// Moves <paramref name="camera"/> in along its line from <paramref name="center"/> to stand in front of the first
        /// thing in the way, as MyThirdPersonSpectator.RaycastOccludingObjects does: a ball swept from what is followed to the
        /// camera with <c>MyPhysics.CastShapeReturnContactBodyDatas</c> and the game's filter (layer 0 in the controlled
        /// entity's collision system), passing back faces and what the game's camera passes (<see cref="PassesThrough"/>).
        /// The ball is the game's: 0.125 m for a character, 0.5 m for a seat, the grid size for a grid; for a ship the sweep
        /// starts one ball out of the centre. It goes in at once and back out smoothly. False when the sweep cannot run or
        /// fails: what is in the way is not known, so there is no picture this frame.
        /// </summary>
        private static bool KeepClear(MyEntity controlled, Vector3D center, ref MatrixD camera)
        {
            Vector3D eye = camera.Translation;
            Vector3D way = eye - center;
            double length = way.Length();
            if (length < 1e-3)
                return true;
            way /= length;

            float radius = controlled is MyCharacter ? 0.125f : controlled is MyCubeGrid grid ? grid.GridSize : 0.5f;
            Vector3D start = controlled is MyCharacter ? center : center + way * radius;
            double reach = (eye - start).Dot(way);
            double clear = 1.0;
            if (reach > radius && !TryClear(controlled, start, eye, way, radius, out clear))
                return false;

            long now = Stopwatch.GetTimestamp();
            double seconds = chaseOutAt == 0 ? 0.0 : Math.Min(Seconds(now - chaseOutAt), 0.25);
            chaseOutAt = now;
            chaseOut = clear < chaseOut ? clear : Math.Min(clear, chaseOut + seconds * EaseOutPerSecond);
            if (chaseOut < 1.0)
                camera.Translation = Vector3D.Lerp(start, eye, Math.Max(chaseOut, 0.0));
            return true;
        }

        /// <summary>
        /// The share of the way from <paramref name="start"/> to <paramref name="eye"/> the ball travels before it touches
        /// anything, 1 when nothing is in the way. False when physics cannot be asked now, or the sweep fails.
        /// </summary>
        private static bool TryClear(MyEntity controlled, Vector3D start, Vector3D eye, Vector3D way, float radius, out double clear)
        {
            clear = 0.0;
            if (chaseErrors >= 3)
                return false;
            if (!MyPhysics.CheckThread() || MyPhysics.InsideSimulation)
            {
                if (!chaseUnsweptNoted)
                    Log.Info("External view: the chase camera could not be swept for walls in a frame (physics busy); there is no picture in such a frame");
                chaseUnsweptNoted = true;
                return false;
            }
            try
            {
                if (chaseBallRadius != radius)
                {
                    if (chaseBallRadius > 0f)
                        chaseBall.RemoveReference();
                    chaseBallRadius = 0f;
                    chaseBall = new HkSphereShape(radius).Base;
                    chaseBallRadius = radius;
                }
                MyEntity top = controlled.GetTopMostParent() ?? controlled;
                uint filter = top.Physics is MyPhysicsBody body ? HkGroupFilter.CalcFilterInfo(0, body.HavokCollisionSystemID, 1, 1) : 0u;
                MatrixD at = MatrixD.CreateTranslation(start);
                chaseHits.Clear();
                MyPhysics.CastShapeReturnContactBodyDatas(eye, chaseBall, ref at, filter, 0f, chaseHits);
                double first = 1.0;
                foreach (MyPhysics.HitInfo hit in chaseHits)
                {
                    HkHitInfo info = hit.HkHitInfo;
                    if (Vector3D.Dot(info.Normal, way) > 0.0)
                        continue;
                    if (PassesThrough(info, controlled))
                        continue;
                    first = Math.Min(first, Math.Max(info.HitFraction, 1e-4f));
                }
                chaseHits.Clear();
                clear = first;
                return true;
            }
            catch (Exception e)
            {
                chaseHits.Clear();
                Log.Error(e, ++chaseErrors < 3 ? "External view: the chase camera's sweep failed; no picture this frame" : "External view: the chase camera's sweep failed again; there is no chase picture until the game restarts");
                return false;
            }
        }

        /// <summary>
        /// What the game's third-person camera does not stop at (MyThirdPersonSpectator.IsEntityFiltered): phantoms, the
        /// followed entity and anything of its grid or its mechanical group, characters, hand tools, floating objects,
        /// debris and safe zones.
        /// </summary>
        private static bool PassesThrough(HkHitInfo info, MyEntity controlled)
        {
            HkRigidBody body = info.Body;
            if (body == null || !(body.UserObject is MyPhysicsBody physics) || physics.IsPhantom)
                return true;
            var hit = info.GetHitEntity() as MyEntity;
            if (hit == null || hit == controlled)
                return true;
            if (hit is MyCharacter || hit is MyFloatingObject || hit is MyDebrisBase || hit is MySafeZone || hit is IMyHandheldGunObject<MyDeviceBase>)
                return true;
            MyEntity hitTop = hit.GetTopMostParent() ?? hit;
            MyEntity ownTop = controlled.GetTopMostParent() ?? controlled;
            if (hitTop == ownTop)
                return true;
            return ownTop is MyCubeGrid own && hitTop is MyCubeGrid other && MyCubeGridGroups.Static.Mechanical.HasSameGroup(own, other);
        }
    }
}
