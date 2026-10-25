// ToolAim: where the tool or gun in the hand points, as its own sensors and shot are given it, and what that line meets.
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
using Sandbox.Game.Weapons.Guns;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace SpaceEngineersVR.Hands
{
    /// <summary>
    /// The held item's aim as <see cref="HeldItem"/> last gave it to the game: the ray its sensors read (the weapon position
    /// component's logical position and orientation) and the shot is sent along. Read by the crosshair
    /// (<see cref="Rendering.HandCrosshair"/>) and the tool's laser (<see cref="HandRay"/>), so both show the line the game
    /// itself works with, never a line of their own. Game thread.
    /// </summary>
    internal static class ToolAim
    {
        /// <summary>An aim older than this (seconds) is not used: the item has not been placed in the hand since.</summary>
        private const double Fresh = 0.25;

        private static object placedFor;
        private static Vector3D placedOrigin, placedForward;
        private static long placedAt;

        private static readonly List<MyPhysics.HitInfo> hits = new List<MyPhysics.HitInfo>();

        private static readonly AccessTools.FieldRef<MyHandDrill, MyDrillBase> drillBase =
            AccessTools.FieldRefAccess<MyHandDrill, MyDrillBase>("m_drillBase");

        /// <summary>Game thread, from <see cref="HeldItem"/>: the item in hand was placed on the hand with this aim.</summary>
        internal static void Placed(object weapon, Vector3D origin, Vector3D forward)
        {
            placedFor = weapon;
            placedOrigin = origin;
            placedForward = forward;
            placedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>The world is unloading: the last item placed is let go of, so its closed entity is not kept.</summary>
        internal static void Forget()
        {
            placedFor = null;
            placedAt = 0;
        }

        /// <summary>
        /// The local character on foot holds <paramref name="weapon"/> in the tracked hand, placed there this frame or the last:
        /// the ray the game's sensors and shot are given (<paramref name="origin"/>, <paramref name="forward"/>, unit length).
        /// False when the game holds it its own way (no hand, hand tools off, a gun whose line checks gave up).
        /// </summary>
        internal static bool TryGet(out MyCharacter character, out IMyEntity weapon, out Vector3D origin, out Vector3D forward)
        {
            character = MySession.Static?.LocalCharacter;
            weapon = character?.CurrentWeapon as IMyEntity;
            origin = placedOrigin;
            forward = placedForward;
            return weapon != null && HandTools.IsLocalOnFoot(character) && ReferenceEquals(weapon, placedFor)
                   && Stopwatch.GetTimestamp() - placedAt < (long)(Fresh * Stopwatch.Frequency);
        }

        /// <summary>
        /// Where a hand tool's own sensor ray runs (a welder's or grinder's caster, a drill's sensor): from
        /// <paramref name="from"/> to <paramref name="to"/>, the reach it works over. False for anything that is not one.
        /// </summary>
        internal static bool TrySensor(IMyEntity weapon, out Vector3D from, out Vector3D to)
        {
            from = to = Vector3D.Zero;
            MyDrillSensorBase sensor = null;
            if (weapon is MyEngineerToolBase tool)
                sensor = tool.Components.Get<MyCasterComponent>()?.Caster;
            else if (weapon is MyHandDrill drill)
                sensor = drillBase(drill)?.Sensor;
            if (sensor == null)
                return false;
            from = sensor.Center;
            to = sensor.FrontPoint;
            return Vector3D.DistanceSquared(from, to) > 1e-6;
        }

        /// <summary>
        /// How far along the line from <paramref name="from"/> to <paramref name="to"/> the first thing is that is not the
        /// character or what it holds, in the collision layer given (the game's own: its sensors cast in
        /// <see cref="MyPhysics.CollisionLayers.ObjectDetectionCollisionLayer"/>, its crosshair target in the default layer).
        /// </summary>
        internal static bool FirstHit(Vector3D from, Vector3D to, MyCharacter character, IMyEntity weapon, int layer, out double along)
        {
            along = double.MaxValue;
            Vector3D direction = to - from;
            double length = direction.Length();
            if (!(length > 1e-6))
                return false;
            direction /= length;
            hits.Clear();
            MyPhysics.CastRay(from, to, hits, layer);
            foreach (MyPhysics.HitInfo hit in hits)
            {
                IMyEntity entity = hit.HkHitInfo.GetHitEntity();
                if (entity == null)
                    continue;
                IMyEntity top = entity.GetTopMostParent();
                if (top == character || entity == weapon || top == weapon)
                    continue;
                double d = Vector3D.Dot(hit.Position - from, direction);
                if (d >= 0d && d < along)
                    along = d;
            }
            hits.Clear();
            return along <= length;
        }
    }
}
